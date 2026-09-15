using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Fights whatever <see cref="TargetSelector"/> picks. Physical styles press
/// an attack, let the power bar build to the configured level, release, and
/// wait for the server to report the swing; the magic style casts the best
/// known war spell. Ranged styles ask <see cref="LineOfSightService"/> before
/// every shot and fall through to the next hostile when the path is blocked;
/// when no hostile can be reached, or a melee target is out of reach, an
/// approach step walks toward it and re-checks. The walk uses the same
/// service as an obstacle sense: a target no heading can walk to is passed
/// over, and while walking the direct heading is probed every tick and a
/// steering fan tried when it is blocked, with the navigation recoveries as
/// a last resort. Either way one swing, cast or walk is one step, so the
/// arbiter can put vitals or buffs in between.
/// </summary>
public sealed class CombatBehavior(
    IAutomationSurface surface,
    SpellSelector spells,
    CastTracker casts,
    LineOfSightService lineOfSight,
    Func<CombatSettings> settings) : IBehavior
{
    private const double SwingTimeoutSeconds = 10d;

    /// <summary>
    /// How far the server swings from without moving the character. A
    /// swing asked for from farther out is the server's to walk in for,
    /// and when the character is hemmed in - a swarm, a doorway - that
    /// walk never comes and neither does the swing. So a swing that has
    /// not completed after <see cref="CloseInAfterSeconds"/> with the
    /// target still beyond this is abandoned and the bot walks in itself,
    /// to this distance, before swinging again.
    /// </summary>
    private const float ServerReachMeters = 2f;
    private const double CloseInAfterSeconds = 3d;
    /// <summary>The target the bot is walking in on after a swing the server did not take; zero when none.</summary>
    private uint _closeInOn;
    private const double ModeChangeTimeoutSeconds = 4d;
    private readonly Walker _walker = new();
    private readonly WeaponReadiness _weapons = new();
    private readonly AmmoCrafter _fletcher = new();

    private Phase _phase;
    private uint _targetId;
    private long _completionRevisionAtSwing;
    private double _phaseStartedAt;
    private double _lastApproachTraceAt = double.NegativeInfinity;
    private double _lastSightCheckAt = double.NegativeInfinity;
    /// <summary>How often the approach looks again for the target it is walking at.</summary>
    private const double SightRecheckSeconds = 0.5d;
    private (double At, Engagement Engagement)? _chosen;
    private bool _modeMismatchTold;
    /// <summary>The log of the last Execute, for the selection that runs inside WantsControl.</summary>
    private IPluginLogger? _log;
    private bool _leftCombat = true;
    private int _recoveryCount;
    private uint _refusedTargetId;
    private int _refusals;
    private double _holdUntil = double.NegativeInfinity;

    /// <summary>A refused attack is tried again on the same target after this long, this many times, before the target is struck.</summary>
    public const double RefusalHoldSeconds = 0.3d;
    public const int RefusalsBeforeStrike = 3;

    private enum Phase
    {
        Idle,
        ChangingMode,
        Building,
        AwaitingSwing,
        Casting,
        Approaching,
        BackingOff,
    }

    /// <summary>What the idle step decided to do with the ranked hostiles.</summary>
    private readonly record struct Engagement(
        PluginCombatTarget Target,
        PluginAttackHeight Height,
        bool Approach);

    public string Name => "combat";

    public BehaviorPriority Priority => BehaviorPriority.Combat;

    public uint CurrentTargetId => _targetId;

    /// <summary>Hostiles this session's fights have put down.</summary>
    public int Kills { get; private set; }

    /// <summary>The name and health of the target the last swing went at, to tell a kill from a walk-away.</summary>
    private string _swungAtName = string.Empty;
    private bool _swungAt;
    private readonly HashSet<uint> _seenCorpses = [];

    public bool IsApproaching => _phase == Phase.Approaching;

    public bool IsBackingOff => _phase == Phase.BackingOff;

    /// <summary>Mid-fight: a target chosen and an attack or cast under way, so a priority boost does not pull the bot off it.</summary>
    public bool IsEngaged => _targetId != 0u && _phase is not Phase.Idle;

    /// <summary>The heading the approach step is currently walking, or NaN.</summary>
    public float ApproachHeadingDegrees { get; private set; } = float.NaN;

    public LineOfSightService LineOfSight => lineOfSight;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        CombatSettings combat = settings();
        if (!combat.Enabled)
            return false;
        // Only a hostile that can actually be fought - shot from here, or
        // walked to - is a reason to take over. Claiming control for one
        // that is blocked or too far to walk to, then finding nothing to do,
        // would pre-empt navigation every tick and the walk would never
        // resume. The choice is kept for Execute on the same tick.
        if (_phase == Phase.Idle)
        {
            if (TryChooseEngagement(board, combat, out Engagement engagement, out int blocked))
            {
                _chosen = (board.Now, engagement);
                reason = $"{engagement.Target.Name} at {engagement.Target.Distance:0.0}m";
                return true;
            }
            TraceNoTarget(board, combat);
            // Hostiles about that cannot be fought from here keep the
            // stance, and are nothing to take over for: claiming control
            // to leave combat, then keeping the stance, was a takeover
            // every tick that walked nowhere.
            if (blocked != 0)
                return false;
        }
        else if (TargetSelector.TrySelect(board.Hostiles, combat, _targetId, out PluginCombatTarget target, lineOfSight.IsBlacklisted))
        {
            reason = $"{target.Name} at {target.Distance:0.0}m";
            return true;
        }
        if (_phase != Phase.Idle)
        {
            reason = _phase switch
            {
                Phase.Approaching => "finishing approach",
                Phase.BackingOff => "backing off",
                _ => "finishing swing",
            };
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
        _log = context.Log;

        if (_phase == Phase.Approaching)
            return StepApproach(context, combat);
        if (_phase == Phase.BackingOff)
            return StepBackOff(context, combat);

        if (_phase is Phase.Building or Phase.AwaitingSwing
            && combat.Style == CombatStyle.Melee
            && _closeInOn != _targetId
            && board.Now - _phaseStartedAt > CloseInAfterSeconds
            && TryFindHostile(board, _targetId, out PluginCombatTarget farTarget)
            && farTarget.Distance > ServerReachMeters
            && board.Navigation.IsAvailable)
        {
            host.AbortPhysicalAttack();
            _closeInOn = _targetId;
            context.Log.Info($"combat: the server has not swung at {farTarget.Name} {farTarget.Distance:0.0}m away; closing in to {ServerReachMeters:0.0}m");
            EnterPhase(Phase.Approaching, board.Now);
            return StepApproach(context, combat);
        }
        if (_phase != Phase.Idle && board.Now - _phaseStartedAt > SwingTimeoutSeconds)
        {
            host.AbortPhysicalAttack();
            casts.Clear();
            EnterPhase(Phase.Idle, board.Now);
            // A swing the server never finished - the target flitted out
            // of reach, or is not one it lets the character hit - counts
            // like a walk that never arrived: enough of them and the
            // target is left alone for a while.
            bool blacklisted = lineOfSight.ReportUnreachable(_targetId);
            return BehaviorStep.Fail($"swing timed out{(blacklisted ? "; leaving the target alone" : string.Empty)}");
        }

        switch (_phase)
        {
            case Phase.ChangingMode:
            {
                PluginCombatMode wanted = DesiredMode(combat.Style);
                if (board.Combat.Mode == wanted)
                {
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Continue;
                }
                // The server answers a physical mode with the one the weapon
                // in hand allows: melee asked for with a bow wielded comes
                // back missile. That is still a fight - both swing through
                // the same request - so it is taken, with a word about it.
                if (IsPhysical(wanted) && IsPhysical(board.Combat.Mode))
                {
                    if (!_modeMismatchTold)
                    {
                        _modeMismatchTold = true;
                        context.Log.Warn($"combat: asked for {wanted} but the weapon in hand puts the character in {board.Combat.Mode}; fighting in {board.Combat.Mode} (set the style's weapon, or the style, to match)");
                    }
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Continue;
                }
                if (board.Now - _phaseStartedAt > ModeChangeTimeoutSeconds)
                {
                    EnterPhase(Phase.Idle, board.Now);
                    return BehaviorStep.Fail($"combat mode change to {wanted} was not confirmed; the client is in {board.Combat.Mode} (is the right weapon wielded?)");
                }
                return BehaviorStep.Continue;
            }

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
                    _closeInOn = 0u;
                    _swungAt = true;
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

        NoteKill(board, context.Log);

        // Idle: pick a target or stand down.
        Engagement engagement;
        int blocked = 0;
        bool chosen;
        if (_chosen is { } kept && kept.At == board.Now)
        {
            engagement = kept.Engagement;
            chosen = true;
        }
        else
        {
            chosen = TryChooseEngagement(board, combat, out engagement, out blocked);
        }
        _chosen = null;
        if (!chosen)
        {
            _targetId = 0u;
            // Hostiles are there but none can be shot from here and none
            // is worth walking to; the strikes just taken will blacklist
            // them shortly. The stance is kept for when one comes into
            // view. Not a failure worth a log line every tick.
            if (blocked != 0)
                return BehaviorStep.Done;
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
        PluginCombatTarget target = engagement.Target;
        if (target.ObjectId != _targetId)
            context.Log.Info($"combat: target {target.Name} 0x{target.ObjectId:X8} at {target.Distance:0.0}m dz {target.HeightDifferenceMeters:+0.0;-0.0} ({(engagement.Approach ? "approach" : "in reach")}, {(target.IsHealthKnown ? $"{target.HealthFraction:P0}" : "hp ?")}) of {board.Hostiles.Count} hostile(s)");
        if (target.ObjectId != _targetId)
        {
            _closeInOn = 0u;
            _swungAt = false;
        }
        _targetId = target.ObjectId;
        _swungAtName = target.Name;
        _leftCombat = false;

        if (board.IsActionPending || board.Now < _holdUntil)
            return BehaviorStep.Continue;

        // A ranged style with something on top of it steps back before the next shot.
        if (combat.Style != CombatStyle.Melee
            && combat.BackOffWhenWithinMeters > 0f
            && board.Navigation.IsAvailable
            && TryNearestHostile(board, out PluginCombatTarget close)
            && close.Distance <= combat.BackOffWhenWithinMeters)
        {
            context.Log.Info($"backing off from {close.Name} at {close.Distance:0.0}m");
            EnterPhase(Phase.BackingOff, board.Now);
            return StepBackOff(context, combat);
        }

        if (engagement.Approach)
        {
            if (!board.Navigation.IsAvailable)
                return BehaviorStep.Fail($"{target.Name} is out of reach and navigation is unavailable");
            context.Log.Info($"approaching {target.Name} at {target.Distance:0.0}m");
            EnterPhase(Phase.Approaching, board.Now);
            return StepApproach(context, combat);
        }

        // The right weapon first: a swap in flight means peace mode and a
        // wait, and a missing weapon or quiver is not worth swinging without.
        MonsterRule? rule = combat.Monsters.Count > 0 ? MonsterRules.For(combat.Monsters, target.Name) : null;
        switch (_weapons.Ensure(context.Surface.Equipment, combat, rule, board.Now, out string weaponDetail))
        {
            case WeaponReadiness.Verdict.Swapping:
                if (board.Combat.Mode != PluginCombatMode.Peace)
                    host.EnterMode(PluginCombatMode.Peace);
                return BehaviorStep.Continue;
            case WeaponReadiness.Verdict.Missing:
                return BehaviorStep.Fail(weaponDetail);
            case WeaponReadiness.Verdict.NoAmmunition:
                return Fletch(context, weaponDetail);
        }

        PluginCombatMode desired = DesiredMode(combat.Style);
        if (board.Combat.Mode != desired && !(IsPhysical(desired) && IsPhysical(board.Combat.Mode)))
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
            engagement.Height,
            Math.Clamp(combat.Power, 0f, 1f));
        if (begin.Status == PluginCombatCommandStatus.Busy)
            return BehaviorStep.Continue;
        if (!begin.Accepted)
        {
            // The host would not start the swing (the target not attackable
            // just now, the character mid-something the flags do not show).
            // Failing here dropped the target and picked the next, which was
            // refused the same way: fifteen targets in a third of a second.
            // Hold the target a moment and try again; a target refused three
            // times over is blacklisted and passed over.
            if (_refusedTargetId != target.ObjectId)
            {
                _refusedTargetId = target.ObjectId;
                _refusals = 0;
            }
            if (++_refusals < RefusalsBeforeStrike)
            {
                _holdUntil = board.Now + RefusalHoldSeconds;
                context.Log.Debug($"combat: attack on {target.Name} refused ({begin.Status} {begin.Notice}); trying again in {RefusalHoldSeconds:0.0}s");
                return BehaviorStep.Continue;
            }
            _refusals = 0;
            _targetId = 0u;
            lineOfSight.Blacklist(target.ObjectId);
            return BehaviorStep.Fail($"attack refused {RefusalsBeforeStrike} times: {begin.Status} {begin.Notice}; leaving {target.Name} alone for {combat.LineOfSight.BlacklistSeconds:0}s");
        }
        _refusals = 0;
        context.Log.Debug($"combat: swing at {target.Name} ({engagement.Height}, power {combat.Power:P0}, {target.Distance:0.0}m)");
        EnterPhase(Phase.Building, board.Now);
        return BehaviorStep.Continue;
    }

    /// <summary>
    /// Whether the body is up against the target: a walk toward it stops
    /// on the target itself before anything else. A walk that goes the
    /// whole way unhindered means the character can get closer, which is
    /// what the approach is for - it is not touching.
    /// </summary>
    private bool TouchesTarget(Blackboard board, in PluginCombatTarget target)
    {
        // Asked for every near hostile on every tick while idle - a swarm
        // of twenty at four metres was a thousand collision walks a second
        // and the frame rate went with them - so the answer is kept for a
        // moment. Bodies do not move far in a quarter of a second.
        if (_touches.TryGetValue(target.ObjectId, out (double At, bool Touches) kept) && board.Now - kept.At <= TouchCacheSeconds)
            return kept.Touches;
        bool touches = ProbeTouch(board, target);
        if (_touches.Count > 64)
            _touches.Clear();
        _touches[target.ObjectId] = (board.Now, touches);
        return touches;
    }

    private bool ProbeTouch(Blackboard board, in PluginCombatTarget target)
    {
        IMovementProbeAutomation probe = surface.MovementProbe;
        if (!probe.IsAvailable || !surface.Navigation.TryGetObject(target.ObjectId, out PluginNavigationObject body))
            return false;
        float heading = RouteFollower.HeadingTo(board.Navigation.Position, body.Position);
        PluginWalkProbeResult result = probe.ProbeWalk(new PluginWalkProbeRequest(heading, target.Distance + 1f)
        {
            StepDistance = 0.5f,
            MaximumCollisionChecks = 24,
            TargetObjectId = target.ObjectId,
        });
        return result.Status == PluginWalkProbeStatus.Blocked
            && result.BlockingObjectId == target.ObjectId
            && result.ClearDistanceMeters <= combatReachSlack;
    }

    /// <summary>How far a walk may go before bumping the target and still count as touching it.</summary>
    private const float combatReachSlack = 1.5f;
    /// <summary>How long a body-to-body answer is kept before the walk is probed again.</summary>
    private const double TouchCacheSeconds = 0.25d;
    private readonly Dictionary<uint, (double At, bool Touches)> _touches = [];

    /// <summary>
    /// Makes ammunition from bundles in the pack when the quiver is empty;
    /// in peace mode, one combine at a time, then the swap gate wields it.
    /// </summary>
    private BehaviorStep Fletch(BehaviorContext context, string weaponDetail)
    {
        Blackboard board = context.Board;
        if (board.Combat.Mode != PluginCombatMode.Peace)
        {
            context.Surface.Combat.EnterMode(PluginCombatMode.Peace);
            return BehaviorStep.Continue;
        }
        AmmoCrafter.WeaponCategory category = AmmoCrafter.CategoryOf(_weapons.MissileWeaponName);
        switch (_fletcher.Tick(context.Surface, category, board.Now, out string detail))
        {
            case AmmoCrafter.Verdict.Crafting:
                return BehaviorStep.Continue;
            case AmmoCrafter.Verdict.Crafted:
                context.Log.Info(detail);
                return BehaviorStep.Continue;
            case AmmoCrafter.Verdict.Failed:
                return BehaviorStep.Fail(detail);
            default:
                return BehaviorStep.Fail(weaponDetail);
        }
    }

    public void Interrupt(BehaviorContext context)
    {
        _fletcher.Reset();
        if (_phase is Phase.Building or Phase.AwaitingSwing)
            context.Surface.Combat.AbortPhysicalAttack();
        _walker.Reset(context.Surface.Navigation);
        casts.Clear();
        _phase = Phase.Idle;
        ApproachHeadingDegrees = float.NaN;
    }

    /// <summary>
    /// Walks the ranked hostiles: the first one that can be attacked from
    /// here wins. A melee target out of reach but inside the approach range
    /// is remembered as something to walk toward if nothing better turns
    /// up - provided the body can actually walk that way; one it cannot
    /// earns a strike and is passed over, and one beyond the approach range
    /// is simply left alone. A ranged target with no line of sight is
    /// never walked to: it earns a strike and is passed over.
    /// </summary>
    private bool TryChooseEngagement(
        Blackboard board,
        CombatSettings combat,
        out Engagement engagement,
        out int blocked)
    {
        engagement = default;
        blocked = 0;
        IReadOnlyList<PluginCombatTarget> ranked = TargetSelector.Rank(
            board.Hostiles, combat, _targetId, lineOfSight.IsBlacklisted);
        if (ranked.Count == 0)
            return false;

        var preferred = (PluginAttackHeight)combat.Height;
        bool outdoors = board.Navigation.Position.IsOutdoor;
        bool checkPath = LineOfSightService.AppliesTo(combat.Style) && lineOfSight.IsEnabled;
        Engagement? approach = null;
        foreach (PluginCombatTarget candidate in ranked)
        {
            // Out of sight is out of mind: a hostile the world hides - the
            // floor above, the far side of a wall - is not a candidate at
            // all, for any style, and needs no strike to be passed over.
            // For a walk, one another creature stands in front of is
            // passed over too: the creature in front is the one in sight,
            // and it ranks on its own; a shot may still arc over it, so
            // the ranged styles let the path sweeps decide. One in reach
            // is fought regardless: nothing hides a monster two metres
            // away that the body is touching.
            if (candidate.Distance > combat.MeleeRangeMeters)
            {
                Sight sight = lineOfSight.See(candidate.ObjectId);
                if (sight == Sight.Hidden || (sight == Sight.Obscured && combat.Style == CombatStyle.Melee))
                {
                    blocked++;
                    continue;
                }
            }
            if (combat.Style == CombatStyle.Melee)
            {
                bool closingIn = candidate.ObjectId == _closeInOn && candidate.Distance > ServerReachMeters;
                if (!closingIn
                    && (candidate.Distance <= combat.MeleeRangeMeters
                        || (candidate.Distance <= combat.MeleeRangeMeters * 3f && TouchesTarget(board, candidate))))
                {
                    engagement = new Engagement(candidate, preferred, Approach: false);
                    return true;
                }
                if (candidate.Distance > combat.ApproachRangeMeters)
                    continue;
                if (approach is null && !CanWalkToward(board, candidate))
                {
                    blocked++;
                    if (lineOfSight.ReportBlocked(candidate.ObjectId))
                        _log?.Info($"combat: {candidate.Name} 0x{candidate.ObjectId:X8} at {candidate.Distance:0.0}m (dz {candidate.HeightDifferenceMeters:+0.0;-0.0}) walled off; blacklisted for {combat.LineOfSight.BlacklistSeconds:0}s");
                    continue;
                }
                approach ??= new Engagement(candidate, preferred, Approach: true);
                continue;
            }
            if (!checkPath)
            {
                engagement = new Engagement(candidate, preferred, Approach: false);
                return true;
            }
            LineOfSightVerdict verdict = lineOfSight.EvaluateAnyHeight(
                candidate.ObjectId, combat.Style, preferred, outdoors);
            if (verdict.IsUsable)
            {
                engagement = new Engagement(candidate, verdict.Height, Approach: false);
                return true;
            }
            blocked++;
            lineOfSight.ReportBlocked(candidate.ObjectId);
        }
        if (approach is { } walk)
        {
            engagement = walk;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the body can start toward a hostile. The direct walk must
    /// be open, or blocked only by the hostile itself or by something
    /// standing in the way (another creature, a door) that the steering fan
    /// can go round. A direct walk the world itself blocks - a wall, the
    /// floor of the room above, a door frame - is not a hostile to walk
    /// at: it earns a strike and is passed over, and the fan is not
    /// consulted, because something is always open sideways and that is
    /// what made the character run back and forth under a monster on the
    /// floor overhead. True when walking is not checked or the hostile's
    /// position is unknown (the walk itself will tell).
    /// </summary>
    private bool CanWalkToward(Blackboard board, in PluginCombatTarget candidate)
    {
        if (!lineOfSight.ChecksWalking || !board.Navigation.IsAvailable)
            return true;
        if (!surface.Navigation.TryGetObject(candidate.ObjectId, out PluginNavigationObject where))
            return true;
        float heading = RouteFollower.HeadingTo(board.Navigation.Position, where.Position);
        float distance = WalkDistance(candidate.Distance);
        WalkVerdict direct = lineOfSight.EvaluateWalk(candidate.ObjectId, heading, distance);
        if (direct.IsUsable || direct.BlockingObjectId == candidate.ObjectId)
        {
            if (direct.IsClear)
                lineOfSight.ReportWalkClear(candidate.ObjectId);
            return true;
        }
        if (direct.State == LineOfSightState.Blacklisted)
            return false;
        if (direct.ByEnvironment)
        {
            lineOfSight.ReportWalkWalledOff(candidate.ObjectId, direct);
            return false;
        }
        return lineOfSight.TryFindWalkHeading(candidate.ObjectId, heading, distance, out _);
    }

    /// <summary>How far to probe toward a hostile: up to its body, not through it.</summary>
    private static float WalkDistance(float targetDistance) => MathF.Max(0.5f, targetDistance - 1f);

    /// <summary>
    /// One tick of walking toward the target: turn in place until facing
    /// it, then hold a run forward. Ends when the target is in reach (melee)
    /// or the path clears or the approach range is met (ranged), when the
    /// target is gone, or when the walk times out.
    /// </summary>
    private BehaviorStep StepApproach(BehaviorContext context, CombatSettings combat)
    {
        Blackboard board = context.Board;
        INavigationAutomation nav = context.Surface.Navigation;
        if (!TryFindHostile(board, _targetId, out PluginCombatTarget target))
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Done;
        }
        if (!board.Navigation.IsAvailable || board.Navigation.IsPortalSpace)
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Fail("navigation is unavailable");
        }

        bool arrived;
        if (combat.Style == CombatStyle.Melee && target.ObjectId == _closeInOn)
        {
            // Walking in after a swing the server did not take: only the
            // server's own reach will do, whatever the reach setting says.
            arrived = target.Distance <= ServerReachMeters;
        }
        else if (combat.Style == CombatStyle.Melee)
        {
            arrived = target.Distance <= combat.MeleeRangeMeters;
            // A big monster keeps the character at its own radius, beyond
            // the reach setting, while already touching it: when the walk
            // probe says the body bumps the target before anything else,
            // that is close enough to swing.
            if (!arrived && target.Distance <= combat.MeleeRangeMeters * 3f && TouchesTarget(board, target))
            {
                context.Log.Debug($"combat: {target.Name} at {target.Distance:0.0}m is body to body; swinging from here");
                arrived = true;
            }
        }
        else
        {
            arrived = target.Distance <= combat.ApproachRangeMeters;
            if (LineOfSightService.AppliesTo(combat.Style) && lineOfSight.IsEnabled)
            {
                LineOfSightVerdict verdict = lineOfSight.EvaluateAnyHeight(
                    target.ObjectId, combat.Style, (PluginAttackHeight)combat.Height,
                    board.Navigation.Position.IsOutdoor);
                arrived |= verdict.IsUsable;
            }
        }
        if (arrived)
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Done;
        }
        // A target that walks out of sight on the way - round a corner,
        // through a door - is let go there and then, not chased round the
        // corner by the steering fan until the walk times out with the
        // character wedged in a doorway. It is looked at again when it
        // shows itself.
        if (board.Now - _lastSightCheckAt >= SightRecheckSeconds)
        {
            _lastSightCheckAt = board.Now;
            if (lineOfSight.See(target.ObjectId) == Sight.Hidden)
            {
                StopMoving(nav);
                EnterPhase(Phase.Idle, board.Now);
                return BehaviorStep.Fail($"lost sight of {target.Name} at {target.Distance:0.0}m; leaving it");
            }
        }
        if (board.Now - _phaseStartedAt > combat.ApproachTimeoutSeconds)
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            bool blacklisted = lineOfSight.ReportUnreachable(target.ObjectId);
            if (blacklisted)
                context.Log.Info($"combat: {target.Name} 0x{target.ObjectId:X8} blacklisted for {combat.LineOfSight.BlacklistSeconds:0}s after {combat.LineOfSight.BlacklistStrikes} failed approaches");
            return BehaviorStep.Fail($"could not reach {target.Name} at {target.Distance:0.0}m{(blacklisted ? "; leaving it alone" : string.Empty)}");
        }
        if (!nav.TryGetObject(target.ObjectId, out PluginNavigationObject where))
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Fail($"{target.Name} has no position");
        }

        // A recovery move runs its course before the walk is reconsidered.
        if (_walker.ContinueRecovery(nav, board.Now))
            return BehaviorStep.Continue;

        PluginNavigationPosition position = board.Navigation.Position;
        float heading = RouteFollower.HeadingTo(position, where.Position);
        if (lineOfSight.ChecksWalking)
        {
            if (lineOfSight.TryFindWalkHeading(
                    target.ObjectId, heading, WalkDistance(target.Distance), out WalkVerdict walk))
            {
                heading = walk.HeadingDegrees;
            }
            else
            {
                // Nothing in the fan is open: try the same moves a stuck
                // walker tries, one at a time, and look again afterwards.
                context.Log.Info($"no open heading toward {target.Name}; trying a recovery move");
                return BeginRecovery(nav, board.Now, NextRecovery());
            }
        }
        ApproachHeadingDegrees = heading;

        // The probes see modelled geometry; the stuck detector catches the rest.
        StuckRecovery? stuck = _walker.Toward(nav, position, heading, board.Now);
        if (board.Now - _lastApproachTraceAt >= 0.5d && context.Log.Debugs())
        {
            _lastApproachTraceAt = board.Now;
            float error = RouteFollower.HeadingDelta(position.HeadingDegrees, heading);
            context.Log.Debug($"combat: approach {target.Name} {target.Distance:0.0}m heading {heading:0} (error {error:+0;-0}) walker {_walker.State}"
                + $" at {BotEngine.Describe(position)} target at {BotEngine.Describe(where.Position)}{(board.Navigation.IsMoving ? string.Empty : " host:not-moving")}");
        }
        if (stuck is { } recovery)
        {
            context.Log.Info($"approach stuck near {target.Name}; trying {recovery}");
            return BeginRecovery(nav, board.Now, recovery);
        }
        return BehaviorStep.Continue;
    }

    /// <summary>
    /// Walks directly away from the nearest hostile until it is at the
    /// back-off distance, the time runs out, or nothing is near any more.
    /// Uses the same walker as the approach, so a wall behind is handled by
    /// the stuck recoveries and the timeout.
    /// </summary>
    private BehaviorStep StepBackOff(BehaviorContext context, CombatSettings combat)
    {
        Blackboard board = context.Board;
        INavigationAutomation nav = context.Surface.Navigation;
        if (!board.Navigation.IsAvailable
            || !TryNearestHostile(board, out PluginCombatTarget close)
            || close.Distance >= combat.BackOffToMeters
            || board.Now - _phaseStartedAt > combat.BackOffTimeoutSeconds)
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Done;
        }
        if (_walker.ContinueRecovery(nav, board.Now))
            return BehaviorStep.Continue;
        if (!nav.TryGetObject(close.ObjectId, out PluginNavigationObject where))
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            return BehaviorStep.Done;
        }
        PluginNavigationPosition position = board.Navigation.Position;
        float away = (RouteFollower.HeadingTo(position, where.Position) + 180f) % 360f;
        if (lineOfSight.ChecksWalking
            && lineOfSight.TryFindWalkHeading(close.ObjectId, away, combat.BackOffToMeters, out WalkVerdict walk))
        {
            away = walk.HeadingDegrees;
        }
        StuckRecovery? stuck = _walker.Toward(nav, position, away, board.Now);
        if (stuck is { } recovery)
            _walker.BeginRecovery(nav, recovery, board.Now);
        return BehaviorStep.Continue;
    }

    private static bool TryNearestHostile(Blackboard board, out PluginCombatTarget nearest)
    {
        nearest = default;
        float best = float.PositiveInfinity;
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (hostile.IsHealthKnown && hostile.HealthFraction <= 0f)
                continue;
            if (hostile.Distance < best)
            {
                best = hostile.Distance;
                nearest = hostile;
            }
        }
        return !float.IsPositiveInfinity(best);
    }

    private BehaviorStep BeginRecovery(INavigationAutomation nav, double now, StuckRecovery recovery)
    {
        _walker.BeginRecovery(nav, recovery, now);
        return BehaviorStep.Continue;
    }

    private StuckRecovery NextRecovery() => (_recoveryCount++ % 3) switch
    {
        0 => StuckRecovery.BackUp,
        1 => StuckRecovery.StrafeLeft,
        _ => StuckRecovery.StrafeRight,
    };

    private void StopMoving(INavigationAutomation nav) => _walker.Reset(nav);

    private double _lastNoTargetTraceAt = double.NegativeInfinity;

    /// <summary>
    /// With hostiles about and none chosen, say every few seconds why the
    /// nearest few were passed over: the character standing in a pack in
    /// peace mode is otherwise a silence in the log.
    /// </summary>
    private void TraceNoTarget(Blackboard board, CombatSettings combat)
    {
        if (board.Hostiles.Count == 0 || _log is not { } log || !log.Debugs() || board.Now - _lastNoTargetTraceAt < 5d)
            return;
        _lastNoTargetTraceAt = board.Now;
        var nearest = new List<PluginCombatTarget>(board.Hostiles);
        nearest.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        var line = new System.Text.StringBuilder($"combat: none of {board.Hostiles.Count} hostile(s) taken:");
        for (int index = 0; index < nearest.Count && index < 4; index++)
        {
            PluginCombatTarget hostile = nearest[index];
            string why;
            if (hostile.Distance > combat.EngageDistance)
                why = "beyond monster range";
            else if (combat.MaxHeightDifferenceMeters > 0f && Math.Abs(hostile.HeightDifferenceMeters) > combat.MaxHeightDifferenceMeters)
                why = "another floor";
            else if (combat.Monsters.Count > 0 && MonsterRules.For(combat.Monsters, hostile.Name) is { Priority: <= 0 })
                why = "priority zero on the monster list";
            else if (TargetSelector.IsIgnored(hostile.Name, combat.IgnoreNames))
                why = "ignored by name";
            else if (lineOfSight.IsBlacklisted(hostile.ObjectId))
                why = "blacklisted";
            else if (hostile.IsHealthKnown && hostile.HealthFraction <= 0f)
                why = "dead";
            else if (hostile.Distance > combat.MeleeRangeMeters && lineOfSight.See(hostile.ObjectId) is Sight sight and not Sight.Seen)
                why = sight.ToString().ToLowerInvariant();
            else if (combat.Style == CombatStyle.Melee && hostile.Distance > combat.ApproachRangeMeters)
                why = "beyond walk-up range";
            else
                why = "no way to it";
            line.Append($" {hostile.Name} {hostile.Distance:0.0}m dz{hostile.HeightDifferenceMeters:+0.0;-0.0} ({why});");
        }
        log.Debug(line.ToString());
    }

    /// <summary>
    /// A target the character has swung at and that is now dead - health
    /// gone, or gone from the hostiles with its corpse where it stood -
    /// is a kill. One that simply walked out of range is not.
    /// </summary>
    private void NoteKill(Blackboard board, IPluginLogger log)
    {
        if (_targetId == 0u || !_swungAt)
            return;
        bool dead = false;
        bool present = false;
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (hostile.ObjectId != _targetId)
                continue;
            present = true;
            dead = hostile.IsHealthKnown && hostile.HealthFraction <= 0f;
            break;
        }
        if (!present)
        {
            // Gone from the hostiles: a kill when a corpse of its name has
            // just appeared close by - one not seen on an earlier look, so
            // an old corpse of a swarm-mate does not count for a target
            // that merely walked off.
            foreach (PluginLootContainer corpse in board.Corpses)
            {
                if (_seenCorpses.Contains(corpse.ObjectId))
                    continue;
                if (corpse.ObjectId == _targetId
                    || (corpse.Distance < 6f && string.Equals(corpse.Name, $"Corpse of {_swungAtName}", StringComparison.Ordinal)))
                {
                    dead = true;
                    _seenCorpses.Add(corpse.ObjectId);
                    break;
                }
            }
        }
        if (_seenCorpses.Count > 256)
            _seenCorpses.Clear();
        foreach (PluginLootContainer corpse in board.Corpses)
            _seenCorpses.Add(corpse.ObjectId);
        if (!dead)
            return;
        Kills++;
        log.Info($"combat: {_swungAtName} down; {Kills} kill(s) this session");
        _targetId = 0u;
        _swungAt = false;
    }

    private static bool TryFindHostile(Blackboard board, uint targetId, out PluginCombatTarget target)
    {
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (hostile.ObjectId == targetId
                && !(hostile.IsHealthKnown && hostile.HealthFraction <= 0f))
            {
                target = hostile;
                return true;
            }
        }
        target = default;
        return false;
    }

    private BehaviorStep BeginCast(
        BehaviorContext context,
        CombatSettings combat,
        in PluginCombatTarget target)
    {
        if (!ChooseSpell(context, combat, target, out PluginSpellInfo spell))
            return BehaviorStep.Fail("no castable attack spell");
        if (casts.IsOnCooldown(spell.SpellId))
            return BehaviorStep.Continue;
        PluginCastRequestResult result = casts.Request(spell.SpellId, target.ObjectId);
        if (result != PluginCastRequestResult.Sent)
            return BehaviorStep.Fail($"{spell.Name}: {result}");
        context.Log.Debug($"combat: cast {spell.Name} ({spell.SpellId}) at {target.Name} {target.Distance:0.0}m");
        EnterPhase(Phase.Casting, context.Board.Now);
        return BehaviorStep.Continue;
    }

    /// <summary>
    /// The next spell for a target under the monster list: a debuff the
    /// rule wants that the target does not carry yet (the client tracks
    /// what the character landed), else the rule's war spell - a ring when
    /// enough hostiles crowd within ring range. Without a rule the
    /// profile's element keyword picks the best direct attack as before.
    /// </summary>
    private bool ChooseSpell(
        BehaviorContext context,
        CombatSettings combat,
        in PluginCombatTarget target,
        out PluginSpellInfo spell)
    {
        MonsterRule? rule = combat.Monsters.Count > 0 ? MonsterRules.For(combat.Monsters, target.Name) : null;
        if (rule is null)
            return spells.TryBestAttack(combat.ElementKeyword, out spell);

        string element = rule.Element.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? (combat.ElementKeyword.Length > 0 ? combat.ElementKeyword : "Fire")
            : rule.Element;

        IReadOnlyList<PluginTrackedEnchantment> landed = context.Surface.Enchantments.Capture(target.ObjectId);
        foreach ((DebuffKind kind, string debuffElement) in MonsterRules.Debuffs(rule, element))
        {
            if (!spells.TryBestDebuff(kind, debuffElement, out PluginSpellInfo debuff))
                continue;
            bool present = false;
            foreach (PluginTrackedEnchantment enchantment in landed)
            {
                if (enchantment.Family == debuff.Family && enchantment.SecondsRemaining > 5d)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                spell = debuff;
                return true;
            }
        }

        bool ring = false;
        if (rule.UseRing && combat.RingRangeMeters > 0f)
        {
            int crowd = 0;
            foreach (PluginCombatTarget hostile in context.Board.Hostiles)
            {
                if (!(hostile.IsHealthKnown && hostile.HealthFraction <= 0f) && hostile.Distance <= combat.RingRangeMeters)
                    crowd++;
            }
            ring = crowd >= Math.Max(1, combat.MinRingTargets);
        }
        if (spells.TryBestOffensive(element, rule.Shape, ring, out spell))
            return true;
        if (ring && spells.TryBestOffensive(element, rule.Shape, ring: false, out spell))
            return true;
        return spells.TryBestAttack(combat.ElementKeyword, out spell);
    }

    private void EnterPhase(Phase phase, double now)
    {
        _phase = phase;
        _phaseStartedAt = now;
        if (phase == Phase.Approaching)
            _recoveryCount = 0;
        else
        {
            ApproachHeadingDegrees = float.NaN;
        }
    }

    private static bool IsPhysical(PluginCombatMode mode) => mode is PluginCombatMode.Melee or PluginCombatMode.Missile;

    private static PluginCombatMode DesiredMode(CombatStyle style) => style switch
    {
        CombatStyle.Missile => PluginCombatMode.Missile,
        CombatStyle.Magic => PluginCombatMode.Magic,
        _ => PluginCombatMode.Melee,
    };
}
