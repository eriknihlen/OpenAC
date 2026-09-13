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
    SpellSelector spells,
    CastTracker casts,
    LineOfSightService lineOfSight,
    Func<CombatSettings> settings) : IBehavior
{
    private const double SwingTimeoutSeconds = 10d;
    private const double ModeChangeTimeoutSeconds = 4d;
    private readonly Walker _walker = new();
    private readonly WeaponReadiness _weapons = new();

    private Phase _phase;
    private uint _targetId;
    private long _completionRevisionAtSwing;
    private double _phaseStartedAt;
    private bool _leftCombat = true;
    private int _recoveryCount;

    private enum Phase
    {
        Idle,
        ChangingMode,
        Building,
        AwaitingSwing,
        Casting,
        Approaching,
    }

    /// <summary>What the idle step decided to do with the ranked hostiles.</summary>
    private readonly record struct Engagement(
        PluginCombatTarget Target,
        PluginAttackHeight Height,
        bool Approach);

    public string Name => "combat";

    public BehaviorPriority Priority => BehaviorPriority.Combat;

    public uint CurrentTargetId => _targetId;

    public bool IsApproaching => _phase == Phase.Approaching;

    /// <summary>The heading the approach step is currently walking, or NaN.</summary>
    public float ApproachHeadingDegrees { get; private set; } = float.NaN;

    public LineOfSightService LineOfSight => lineOfSight;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        CombatSettings combat = settings();
        if (!combat.Enabled)
            return false;
        if (TargetSelector.TrySelect(board.Hostiles, combat, _targetId, out PluginCombatTarget target, lineOfSight.IsBlacklisted))
        {
            reason = $"{target.Name} at {target.Distance:0.0}m";
            return true;
        }
        if (_phase != Phase.Idle)
        {
            reason = _phase == Phase.Approaching ? "finishing approach" : "finishing swing";
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

        if (_phase == Phase.Approaching)
            return StepApproach(context, combat);

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
        if (!TryChooseEngagement(context, combat, out Engagement engagement, out int blocked))
        {
            _targetId = 0u;
            // Hostiles are there but none can be shot from here and none
            // is worth walking to; the strikes just taken will blacklist
            // them shortly. Not a failure worth a log line every tick.
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
        _targetId = target.ObjectId;
        _leftCombat = false;

        if (board.IsActionPending)
            return BehaviorStep.Continue;

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
        }

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
            engagement.Height,
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
        _walker.Reset(context.Surface.Navigation);
        casts.Clear();
        _phase = Phase.Idle;
        ApproachHeadingDegrees = float.NaN;
    }

    /// <summary>
    /// Walks the ranked hostiles: the first one that can be attacked from
    /// here wins. A melee target out of reach, or a ranged target blocked
    /// beyond the approach range, is remembered as something to walk toward
    /// if nothing better turns up - provided the body can actually walk
    /// that way; one it cannot earns a strike, as does a ranged target
    /// blocked inside the approach range, and is passed over.
    /// </summary>
    private bool TryChooseEngagement(
        BehaviorContext context,
        CombatSettings combat,
        out Engagement engagement,
        out int blocked)
    {
        Blackboard board = context.Board;
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
            if (combat.Style == CombatStyle.Melee)
            {
                if (candidate.Distance <= combat.MeleeRangeMeters)
                {
                    engagement = new Engagement(candidate, preferred, Approach: false);
                    return true;
                }
                if (approach is null && !CanWalkToward(context, candidate))
                {
                    blocked++;
                    lineOfSight.ReportBlocked(candidate.ObjectId);
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
            if (candidate.Distance > combat.ApproachRangeMeters
                && (approach is not null || CanWalkToward(context, candidate)))
            {
                approach ??= new Engagement(candidate, preferred, Approach: true);
                continue;
            }
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
    /// Whether the body can start toward a hostile: the direct heading, or
    /// one of the steering fan, is open. True when walking is not checked
    /// or the hostile's position is unknown (the walk itself will tell).
    /// </summary>
    private bool CanWalkToward(BehaviorContext context, in PluginCombatTarget candidate)
    {
        if (!lineOfSight.ChecksWalking || !context.Board.Navigation.IsAvailable)
            return true;
        if (!context.Surface.Navigation.TryGetObject(candidate.ObjectId, out PluginNavigationObject where))
            return true;
        float heading = RouteFollower.HeadingTo(context.Board.Navigation.Position, where.Position);
        return lineOfSight.TryFindWalkHeading(
            candidate.ObjectId, heading, WalkDistance(candidate.Distance), out _);
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
        if (combat.Style == CombatStyle.Melee)
        {
            arrived = target.Distance <= combat.MeleeRangeMeters;
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
        if (board.Now - _phaseStartedAt > combat.ApproachTimeoutSeconds)
        {
            StopMoving(nav);
            EnterPhase(Phase.Idle, board.Now);
            lineOfSight.ReportBlocked(target.ObjectId);
            return BehaviorStep.Fail($"could not reach {target.Name}");
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
        if (stuck is { } recovery)
        {
            context.Log.Info($"approach stuck near {target.Name}; trying {recovery}");
            return BeginRecovery(nav, board.Now, recovery);
        }
        return BehaviorStep.Continue;
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

    private static PluginCombatMode DesiredMode(CombatStyle style) => style switch
    {
        CombatStyle.Missile => PluginCombatMode.Missile,
        CombatStyle.Magic => PluginCombatMode.Magic,
        _ => PluginCombatMode.Melee,
    };
}
