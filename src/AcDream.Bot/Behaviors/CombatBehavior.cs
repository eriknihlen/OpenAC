using AcDream.Bot.Combat;
using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Behaviors;

/// <summary>
/// Fights whatever <see cref="TargetSelector"/> picks. Physical styles press
/// an attack, let the power bar build to the configured level, release, and
/// wait for the server to report the swing; the magic style casts the best
/// known war spell. Either way one swing or cast is one step, so the arbiter
/// can put vitals or buffs in between.
/// </summary>
public sealed class CombatBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<CombatSettings> settings) : IBehavior
{
    private const double SwingTimeoutSeconds = 10d;
    private const double ModeChangeTimeoutSeconds = 4d;

    private Phase _phase;
    private uint _targetId;
    private long _completionRevisionAtSwing;
    private double _phaseStartedAt;
    private bool _leftCombat = true;

    private enum Phase
    {
        Idle,
        ChangingMode,
        Building,
        AwaitingSwing,
        Casting,
    }

    public string Name => "combat";

    public BehaviorPriority Priority => BehaviorPriority.Combat;

    public uint CurrentTargetId => _targetId;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        CombatSettings combat = settings();
        if (!combat.Enabled)
            return false;
        if (TargetSelector.TrySelect(board.Hostiles, combat, _targetId, out PluginCombatTarget target))
        {
            reason = $"{target.Name} at {target.Distance:0.0}m";
            return true;
        }
        if (_phase != Phase.Idle)
        {
            reason = "finishing swing";
            return true;
        }
        if (combat.LeaveCombatWhenIdle && !_leftCombat && board.Combat.Mode != PluginCombatMode.Peace)
        {
            reason = "leaving combat";
            return true;
        }
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        CombatSettings combat = settings();
        ICombatAutomation host = context.Surface.Combat;

        if (_phase != Phase.Idle && board.Now - _phaseStartedAt > SwingTimeoutSeconds)
        {
            host.AbortPhysicalAttack();
            casts.Clear();
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Fail("swing timed out");
        }

        switch (_phase)
        {
            case Phase.ChangingMode:
                if (board.Combat.Mode == DesiredMode(combat.Style))
                {
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Continue;
                }
                if (board.Now - _phaseStartedAt > ModeChangeTimeoutSeconds)
                {
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Fail("combat mode change was not confirmed");
                }
                return BehaviorStep.Continue;

            case Phase.Building:
                if (!board.Combat.RequestInProgress)
                {
                    EnterPhase(Phase.AwaitingSwing, board.Now);
                    return BehaviorStep.Continue;
                }
                if (!board.Combat.BuildInProgress
                    || board.Combat.PowerBarLevel >= board.Combat.DesiredPower)
                {
                    host.ReleasePhysicalAttack();
                    EnterPhase(Phase.AwaitingSwing, board.Now);
                }
                return BehaviorStep.Continue;

            case Phase.AwaitingSwing:
                if (board.Combat.CompletionRevision != _completionRevisionAtSwing
                    || (!board.Combat.RequestInProgress && !board.Combat.ServerResponsePending))
                {
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Done;
                }
                return BehaviorStep.Continue;

            case Phase.Casting:
                CastOutcome? outcome = casts.Poll();
                if (casts.HasPendingRequest)
                    return BehaviorStep.Continue;
                EnterPhase(Phase.Idle, board.Now);
                return outcome is CastOutcome.Succeeded or null
                    ? BehaviorStep.Done
                    : BehaviorStep.Fail($"cast {outcome}");
        }

        // Idle: pick a target or stand down.
        if (!TargetSelector.TrySelect(board.Hostiles, combat, _targetId, out PluginCombatTarget target))
        {
            _targetId = 0u;
            if (combat.LeaveCombatWhenIdle && board.Combat.Mode != PluginCombatMode.Peace)
            {
                PluginCombatCommandResult peace = host.EnterMode(PluginCombatMode.Peace);
                _leftCombat = true;
                return peace.Accepted
                    ? BehaviorStep.Done
                    : BehaviorStep.Fail($"could not leave combat: {peace.Status}");
            }
            _leftCombat = true;
            return BehaviorStep.Done;
        }
        _targetId = target.ObjectId;
        _leftCombat = false;

        if (board.IsActionPending)
            return BehaviorStep.Continue;

        PluginCombatMode desired = DesiredMode(combat.Style);
        if (board.Combat.Mode != desired)
        {
            PluginCombatCommandResult mode = host.EnterMode(desired);
            if (!mode.Accepted)
                return BehaviorStep.Fail($"cannot enter {desired}: {mode.Status} {mode.Notice}");
            EnterPhase(Phase.ChangingMode, board.Now);
            return BehaviorStep.Continue;
        }

        if (combat.Style == CombatStyle.Magic)
            return BeginCast(context, combat, target);

        _completionRevisionAtSwing = board.Combat.CompletionRevision;
        PluginCombatCommandResult begin = host.BeginPhysicalAttack(
            target.ObjectId,
            (PluginAttackHeight)combat.Height,
            Math.Clamp(combat.Power, 0f, 1f));
        if (begin.Status == PluginCombatCommandStatus.Busy)
            return BehaviorStep.Continue;
        if (!begin.Accepted)
            return BehaviorStep.Fail($"attack refused: {begin.Status} {begin.Notice}");
        EnterPhase(Phase.Building, board.Now);
        return BehaviorStep.Continue;
    }

    public void Interrupt(BehaviorContext context)
    {
        if (_phase is Phase.Building or Phase.AwaitingSwing)
            context.Surface.Combat.AbortPhysicalAttack();
        casts.Clear();
        _phase = Phase.Idle;
    }

    private BehaviorStep BeginCast(
        BehaviorContext context,
        CombatSettings combat,
        in PluginCombatTarget target)
    {
        if (!spells.TryBestAttack(combat.ElementKeyword, out PluginSpellInfo spell))
            return BehaviorStep.Fail("no castable attack spell");
        if (casts.IsOnCooldown(spell.SpellId))
            return BehaviorStep.Continue;
        PluginCastRequestResult result = casts.Request(spell.SpellId, target.ObjectId);
        if (result != PluginCastRequestResult.Sent)
            return BehaviorStep.Fail($"{spell.Name}: {result}");
        EnterPhase(Phase.Casting, context.Board.Now);
        return BehaviorStep.Continue;
    }

    private void EnterPhase(Phase phase, double now)
    {
        _phase = phase;
        _phaseStartedAt = now;
    }

    private static PluginCombatMode DesiredMode(CombatStyle style) => style switch
    {
        CombatStyle.Missile => PluginCombatMode.Missile,
        CombatStyle.Magic => PluginCombatMode.Magic,
        _ => PluginCombatMode.Melee,
    };
}
