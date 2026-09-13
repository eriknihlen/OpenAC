using AcDream.DrakBot.Loot;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Works through nearby corpses one at a time: open, look at each item,
/// appraise the ones a rule cannot judge by name and class alone, pick up
/// what a rule keeps. A corpse is remembered as finished whether or not it
/// yielded anything, so the bot never loops back to an empty one.
/// </summary>
public sealed class LootBehavior(Func<LootSettings> settings) : IBehavior
{
    private readonly HashSet<uint> _finishedCorpses = [];
    private readonly HashSet<uint> _handledItems = [];
    private Phase _phase;
    private uint _corpseId;
    private uint _itemId;
    private long _inventoryRevisionAtPickup;
    private double _phaseStartedAt;

    private enum Phase
    {
        Idle,
        Opening,
        Evaluating,
        Appraising,
        PickingUp,
    }

    public string Name => "loot";

    public BehaviorPriority Priority => BehaviorPriority.Looting;

    public int FinishedCorpseCount => _finishedCorpses.Count;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        LootSettings loot = settings();
        if (!loot.Enabled)
            return false;
        if (_phase != Phase.Idle)
        {
            reason = "looting";
            return true;
        }
        if (TryPickCorpse(board.Corpses, out PluginLootContainer corpse))
        {
            reason = $"{corpse.Name} at {corpse.Distance:0.0}m";
            return true;
        }
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        LootSettings loot = settings();
        ILootAutomation host = context.Surface.Loot;

        if (_phase != Phase.Idle && board.Now - _phaseStartedAt > loot.StepTimeoutSeconds)
        {
            string stalled = _phase.ToString();
            FinishCorpse();
            return BehaviorStep.Fail($"{stalled} timed out");
        }

        switch (_phase)
        {
            case Phase.Opening:
                if (board.OpenContainerId == _corpseId)
                    EnterPhase(Phase.Evaluating, board.Now);
                return BehaviorStep.Continue;

            case Phase.Appraising:
                if (IsAppraised(context, _itemId))
                    EnterPhase(Phase.Evaluating, board.Now);
                return BehaviorStep.Continue;

            case Phase.PickingUp:
                PluginInventoryCompletion completion = host.LastInventoryCompletion;
                bool gone = !host.CaptureCurrentContents().Any(item => item.ObjectId == _itemId);
                if (completion.Revision != _inventoryRevisionAtPickup || gone)
                {
                    if (completion.Revision != _inventoryRevisionAtPickup && !completion.IsSuccess)
                        context.Log.Warn($"pickup of {_itemId:X8} failed with {completion.WeenieError}");
                    _handledItems.Add(_itemId);
                    EnterPhase(Phase.Evaluating, board.Now);
                }
                return BehaviorStep.Continue;

            case Phase.Evaluating:
                return Evaluate(context, loot);
        }

        if (board.LootBusy || board.IsActionPending)
            return BehaviorStep.Continue;
        if (!TryPickCorpse(board.Corpses, out PluginLootContainer corpse))
            return BehaviorStep.Done;

        _corpseId = corpse.ObjectId;
        _handledItems.Clear();
        if (board.OpenContainerId == _corpseId)
        {
            EnterPhase(Phase.Evaluating, board.Now);
            return BehaviorStep.Continue;
        }
        PluginItemCommandResult open = host.Open(_corpseId);
        if (open.Status == PluginItemCommandStatus.Busy)
            return BehaviorStep.Continue;
        if (!open.Accepted)
        {
            FinishCorpse();
            return BehaviorStep.Fail($"could not open {corpse.Name}: {open.Status} {open.Notice}");
        }
        EnterPhase(Phase.Opening, board.Now);
        return BehaviorStep.Continue;
    }

    public void Interrupt(BehaviorContext context)
    {
        // Leave the corpse for later rather than marking it finished: a fight
        // interrupted us, not the corpse.
        _phase = Phase.Idle;
    }

    private BehaviorStep Evaluate(BehaviorContext context, LootSettings loot)
    {
        ILootAutomation host = context.Surface.Loot;
        if (host.IsBusy)
            return BehaviorStep.Continue;

        foreach (PluginInventoryItem item in host.CaptureCurrentContents())
        {
            if (_handledItems.Contains(item.ObjectId))
                continue;

            LootDecision decision = loot.Rules.Decide(item, IsAppraised(context, item.ObjectId));
            if (decision.RequiresAppraisal)
            {
                PluginItemCommandResult identify = host.Identify(item.ObjectId);
                if (identify.Status == PluginItemCommandStatus.Busy)
                    return BehaviorStep.Continue;
                if (!identify.Accepted)
                {
                    _handledItems.Add(item.ObjectId);
                    continue;
                }
                _itemId = item.ObjectId;
                EnterPhase(Phase.Appraising, context.Board.Now);
                return BehaviorStep.Continue;
            }

            if (decision.Action != LootAction.Keep)
            {
                _handledItems.Add(item.ObjectId);
                continue;
            }

            _inventoryRevisionAtPickup = host.LastInventoryCompletion.Revision;
            PluginItemCommandResult pickup = host.Pickup(item.ObjectId);
            if (pickup.Status == PluginItemCommandStatus.Busy)
                return BehaviorStep.Continue;
            if (!pickup.Accepted)
            {
                context.Log.Warn($"pickup of {item.Name} refused: {pickup.Status} {pickup.Notice}");
                _handledItems.Add(item.ObjectId);
                continue;
            }
            context.Log.Info($"looting {item.Name} ({decision.RuleName})");
            _itemId = item.ObjectId;
            EnterPhase(Phase.PickingUp, context.Board.Now);
            return BehaviorStep.Continue;
        }

        FinishCorpse();
        return BehaviorStep.Done;
    }

    private bool TryPickCorpse(IReadOnlyList<PluginLootContainer> corpses, out PluginLootContainer corpse)
    {
        corpse = default;
        float best = float.PositiveInfinity;
        foreach (PluginLootContainer candidate in corpses)
        {
            if (_finishedCorpses.Contains(candidate.ObjectId))
                continue;
            if (candidate.Distance < best)
            {
                best = candidate.Distance;
                corpse = candidate;
            }
        }
        return !float.IsPositiveInfinity(best);
    }

    private static bool IsAppraised(BehaviorContext context, uint objectId)
    {
        if (context.Surface.Objects.TryGet(objectId, out PluginWorldObject worldObject))
            return worldObject.HasAppraisalData;
        return context.Surface.Loot.Appraisal.CurrentObjectId == objectId;
    }

    private void FinishCorpse()
    {
        if (_corpseId != 0u)
            _finishedCorpses.Add(_corpseId);
        _corpseId = 0u;
        _itemId = 0u;
        _handledItems.Clear();
        _phase = Phase.Idle;
    }

    private void EnterPhase(Phase phase, double now)
    {
        _phase = phase;
        _phaseStartedAt = now;
    }
}
