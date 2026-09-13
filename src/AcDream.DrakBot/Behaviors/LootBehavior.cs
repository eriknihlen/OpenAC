using AcDream.DrakBot.Loot;
using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Works through nearby corpses one at a time: open, look at each item,
/// appraise the ones a rule cannot judge by name and class alone, pick up
/// what a rule keeps. A corpse is remembered as finished whether or not it
/// yielded anything, so the bot never loops back to an empty one. The
/// rules are the profile's own, or a VTank <c>.utl</c> profile when the
/// profile names one.
/// </summary>
public sealed class LootBehavior(
    Func<LootSettings> settings,
    Func<string, VTankLootProfile?>? utlLoader = null,
    Func<ManaStoneSettings>? manaStones = null,
    Action<uint>? salvage = null) : IBehavior
{
    private readonly HashSet<uint> _finishedCorpses = [];
    private readonly HashSet<uint> _handledItems = [];
    private string _utlName = string.Empty;
    private VTankLootProfile? _utl;
    private Phase _phase;
    private uint _corpseId;
    private uint _itemId;
    private bool _salvageOnPickup;
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
                    else if (_salvageOnPickup)
                        salvage?.Invoke(_itemId);
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

            LootDecision decision = Decide(context, loot, item, IsAppraised(context, item.ObjectId));
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

            if (decision.Action == LootAction.Ignore)
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
            _salvageOnPickup = decision.Action == LootAction.Salvage && salvage is not null;
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

    /// <summary>
    /// The unworn pack items the rules mark for sale, for a vendor step:
    /// a <c>.utl</c> Sell rule or a profile rule with the Sell action;
    /// never a container, and never a full salvage bag unless a rule says.
    /// </summary>
    public IReadOnlyList<uint> ItemsToSell(IAutomationSurface surface, LootSettings loot)
    {
        var ids = new List<uint>();
        VTankLootProfile? utl = UtlProfile(loot);
        UtlLootContext? utlContext = utl is null ? null : new UtlLootContext(surface);
        foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
        {
            if (item.IsEquipped || item.ObjectClass is PluginObjectClass.Container or PluginObjectClass.Foci)
                continue;
            if (utl is not null)
            {
                foreach (VTankLootRule rule in utl.Rules)
                {
                    if (!rule.Enabled || !UtlLootEvaluator.Match(rule, item, utlContext!))
                        continue;
                    if (rule.Action == VTankLootAction.Sell)
                        ids.Add(item.ObjectId);
                    break;
                }
            }
            else if (loot.Rules.Decide(item, isAppraised: true).Action == LootAction.Sell)
            {
                ids.Add(item.ObjectId);
            }
        }
        return ids;
    }

    /// <summary>The loaded <c>.utl</c> when the profile names one, reloaded when the name changes.</summary>
    public VTankLootProfile? UtlProfile(LootSettings loot)
    {
        if (utlLoader is null || loot.UtlProfile.Length == 0)
        {
            _utl = null;
            _utlName = string.Empty;
            return null;
        }
        if (!_utlName.Equals(loot.UtlProfile, StringComparison.OrdinalIgnoreCase))
        {
            _utlName = loot.UtlProfile;
            _utl = utlLoader(loot.UtlProfile);
        }
        return _utl;
    }

    private LootDecision Decide(BehaviorContext context, LootSettings loot, in PluginInventoryItem item, bool isAppraised)
    {
        // Mana stones are stocked up to the keep count when the stone
        // behavior is on, whatever the rules say about them.
        if (item.ObjectClass == PluginObjectClass.ManaStone && manaStones?.Invoke() is { Enabled: true } stones)
        {
            int have = 0;
            foreach (PluginInventoryItem owned in context.Surface.Items.CaptureOwnedItems())
            {
                if (owned.ObjectClass == PluginObjectClass.ManaStone)
                    have += Math.Max(1, owned.StackSize);
            }
            return new LootDecision(have < stones.KeepCount ? LootAction.Keep : LootAction.Ignore, $"mana stones {have}/{stones.KeepCount}");
        }
        VTankLootProfile? utl = UtlProfile(loot);
        if (utl is null)
            return loot.Rules.Decide(item, isAppraised);

        // Rules that judge by name and class alone decide unappraised; the
        // rest wait for an appraisal, which is asked for only when some rule
        // could use it.
        var utlContext = new UtlLootContext(context.Surface);
        bool wantsAppraisal = false;
        foreach (VTankLootRule rule in utl.Rules)
        {
            if (!rule.Enabled)
                continue;
            bool needs = UtlLootEvaluator.NeedsAppraisal(rule);
            if (needs && !isAppraised)
            {
                if (UtlLootEvaluator.CouldMatchAfterAppraisal(rule, item, utlContext))
                    wantsAppraisal = true;
                continue;
            }
            if (UtlLootEvaluator.Match(rule, item, utlContext))
                return Translate(rule, item, context);
        }
        return wantsAppraisal && !isAppraised ? LootDecision.Appraise : LootDecision.Ignore;
    }

    private static LootDecision Translate(VTankLootRule rule, in PluginInventoryItem item, BehaviorContext context)
    {
        switch (rule.Action)
        {
            case VTankLootAction.Keep:
                return new LootDecision(LootAction.Keep, rule.Name);
            case VTankLootAction.Sell:
                return new LootDecision(LootAction.Sell, rule.Name);
            case VTankLootAction.Salvage:
                return new LootDecision(LootAction.Salvage, rule.Name);
            case VTankLootAction.KeepUpTo:
            {
                int have = 0;
                foreach (PluginInventoryItem owned in context.Surface.Items.CaptureOwnedItems())
                {
                    if (owned.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                        have += Math.Max(1, owned.StackSize);
                }
                return have < (rule.KeepCount ?? 0)
                    ? new LootDecision(LootAction.Keep, rule.Name)
                    : new LootDecision(LootAction.Ignore, rule.Name);
            }
            default:
                return new LootDecision(LootAction.Ignore, rule.Name);
        }
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
