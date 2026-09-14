using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class CombatBehaviorTests
{
    private static PluginCombatTarget Hostile(uint id, string name, float distance) =>
        new(id, name, 0u, distance, 0f, true, 1f);

    private static (FakeAutomationSurface Surface, CombatBehavior Behavior, TickClock Clock) Build(CombatSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.AttackSpells.Add(Spell.Attack(300, "Flame Bolt VI", 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new CombatBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            new LineOfSightService(surface, surface, clock, () => settings.LineOfSight),
            () => settings);
        return (surface, behavior, clock);
    }

    /// <summary>Places a hostile due <paramref name="north"/>/<paramref name="east"/> of the character, in meters.</summary>
    private static void Place(FakeAutomationSurface surface, uint id, double north, double east) =>
        surface.ObjectPositions[id] = surface.Position with
        {
            NorthSouth = surface.Position.NorthSouth + north / 240d,
            EastWest = surface.Position.EastWest + east / 240d,
        };

    private static void MoveHostile(FakeAutomationSurface surface, uint id, float distance)
    {
        int index = surface.Hostiles.FindIndex(h => h.ObjectId == id);
        surface.Hostiles[index] = surface.Hostiles[index] with { Distance = distance };
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    private static BehaviorStep Step(CombatBehavior behavior, FakeAutomationSurface surface, TickClock clock, double dt = 0.1)
    {
        clock.Advance(dt);
        return behavior.Execute(Context(surface, clock));
    }

    [Fact]
    public void TargetSelectorPrefersPriorityNamesThenNearest()
    {
        var settings = new CombatSettings { PriorityNames = ["Olthoi"], IgnoreNames = ["Rabbit"] };
        PluginCombatTarget[] hostiles =
        [
            Hostile(1, "Rabbit", 1f),
            Hostile(2, "Drudge Slinker", 4f),
            Hostile(3, "Olthoi Worker", 12f),
            Hostile(4, "Drudge Prowler", 3f),
        ];

        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out PluginCombatTarget target));
        Assert.Equal(3u, target.ObjectId);

        settings = settings with { PriorityNames = [] };
        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out target));
        Assert.Equal(4u, target.ObjectId);
    }

    [Fact]
    public void TargetSelectorSticksWithTheCurrentTargetWhenRangesAreClose()
    {
        var settings = new CombatSettings();
        PluginCombatTarget[] hostiles = [Hostile(1, "A", 5f), Hostile(2, "B", 4f)];

        Assert.True(TargetSelector.TrySelect(hostiles, settings, 1u, out PluginCombatTarget target));
        Assert.Equal(1u, target.ObjectId);
    }

    [Fact]
    public void MeleeSwingEntersModeBuildsPowerReleasesAndWaitsForTheServer()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, Power = 0.8f });
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));

        // Peace -> melee.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["mode:Melee"], surface.Commands);
        // Mode confirmed on the next snapshot; press the attack.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("attack:7:Medium:0.8", surface.Commands[^1]);

        // Power bar still building: hold.
        surface.CombatSnapshot = surface.CombatSnapshot with { PowerBarLevel = 0.3f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.DoesNotContain("release", surface.Commands);

        surface.CombatSnapshot = surface.CombatSnapshot with { PowerBarLevel = 0.85f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("release", surface.Commands[^1]);

        // Waiting on the server.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        surface.CompleteSwing();
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
    }

    [Fact]
    public void ReturnsToPeaceWhenNothingIsLeft()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));
        Step(behavior, surface, clock);
        Assert.Equal(PluginCombatMode.Melee, surface.CombatSnapshot.Mode);
        surface.Hostiles.Clear();
        // The mode change still has to be confirmed before standing down.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("leaving combat", reason);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void MagicStyleCastsTheBestAttackAtTheTarget()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Magic });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 10f));

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["cast:300@9"], surface.Commands);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        surface.CompleteCast(300, target: 9);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
    }

    [Fact]
    public void AStalledSwingIsAbortedAndReported()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));
        Step(behavior, surface, clock);
        Assert.StartsWith("attack:", surface.Commands[^1], StringComparison.Ordinal);

        BehaviorStep step = Step(behavior, surface, clock, dt: 11d);

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Equal("abort", surface.Commands[^1]);
    }

    [Fact]
    public void InterruptMidSwingAborts()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));
        Step(behavior, surface, clock);

        behavior.Interrupt(Context(surface, clock));

        Assert.Equal("abort", surface.Commands[^1]);
    }

    [Fact]
    public void MissileShotUsesTheFirstClearAimHeight()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Missile });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Missile };
        surface.Hostiles.Add(Hostile(9, "Tusker", 12f));
        surface.PathStatuses[(9u, PluginAttackHeight.Medium)] = PluginProjectilePathStatus.Blocked;
        surface.PathStatuses[(9u, PluginAttackHeight.High)] = PluginProjectilePathStatus.Blocked;

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.Equal("attack:9:Low:1", surface.Commands[^1]);
        Assert.Equal(["9:Missile:Medium", "9:Missile:High", "9:Missile:Low"], surface.PathQueries);
        Assert.True(behavior.LineOfSight.TryGetLast(9u, out LineOfSightVerdict verdict));
        Assert.True(verdict.IsClear);
        Assert.Equal(PluginAttackHeight.Low, verdict.Height);
    }

    [Fact]
    public void ABlockedTargetInsideApproachRangeIsStruckAndTheNextClearOneIsCast()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Magic });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(1, "Near behind a pillar", 4f));
        surface.Hostiles.Add(Hostile(2, "Farther in the open", 9f));
        surface.BlockPath(1u);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.Equal(["cast:300@2"], surface.Commands);
        Assert.Equal(2u, behavior.CurrentTargetId);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(1u));
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(2u));
    }

    [Fact]
    public void ATargetBlockedInsideApproachRangeIsBlacklistedAfterItsStrikes()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Magic,
            LineOfSight = new LineOfSightSettings { BlacklistStrikes = 2, CacheSeconds = 0.5d, BlacklistSeconds = 5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(1, "Drudge", 4f));
        surface.BlockPath(1u);

        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(1u));
        // The cached verdict is not a second strike.
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(1u));
        Assert.Empty(surface.Commands);

        Assert.Equal(StepResult.Done, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.True(behavior.LineOfSight.IsBlacklisted(1u));
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));

        // Once the blacklist lapses and the path is open the fight resumes.
        surface.ClearPath(1u);
        clock.Advance(6d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["cast:300@1"], surface.Commands);
    }

    [Fact]
    public void EveryTargetBlockedBeyondApproachRangeWalksTowardTheBestUntilThePathClears()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Magic,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 15f));
        surface.BlockPath(9u);
        Place(surface, 9u, north: 0d, east: 15d);

        // Turn first: the target is due east and the character faces north.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.True(behavior.IsApproaching);
        Assert.Equal(["face:90"], surface.Commands);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Tusker at 15.0m", reason);

        // Facing it now: walk.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(2, surface.Commands.Count);
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(9u));

        // The path opens while still 12 m out: stop and shoot.
        surface.ClearPath(9u);
        MoveHostile(surface, 9u, 12f);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.False(behavior.IsApproaching);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("cast:300@9", surface.Commands[^1]);
    }

    [Fact]
    public void ApproachStopsAtTheApproachRangeAndTheTargetIsThenStruck()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Missile, ApproachRangeMeters = 5f });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Missile };
        surface.Hostiles.Add(Hostile(9, "Tusker", 15f));
        surface.BlockPath(9u);
        Place(surface, 9u, north: 15d, east: 0d);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["move:forward"], surface.Commands);

        MoveHostile(surface, 9u, 4.5f);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal("move:clear", surface.Commands[^1]);
        // Now inside the range: no walk, one strike, nothing to shoot.
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(9u));
        Assert.DoesNotContain("attack:9:Medium:1", surface.Commands);
    }

    [Fact]
    public void AnApproachThatGoesNowhereTimesOutWithAStrike()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Magic, ApproachTimeoutSeconds = 3d });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 15f));
        surface.BlockPath(9u);
        Place(surface, 9u, north: 15d, east: 0d);

        Step(behavior, surface, clock);
        BehaviorStep step = Step(behavior, surface, clock, dt: 3.5);

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.StartsWith("could not reach Tusker", step.Reason);
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(9u));
    }

    [Fact]
    public void AMeleeTargetThatCannotBeReachedIsBlacklistedAfterThreeApproaches()
    {
        // A melee walk makes no line-of-sight sweeps, so the timeout itself has to count.
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 2.5f, ApproachTimeoutSeconds = 3d });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Noble", 5f));
        Place(surface, 9u, north: 5d, east: 0d);
        surface.BlockedWalkHeadings.Add(0); // a wall due north, between the character and the Noble

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
            BehaviorStep step = Step(behavior, surface, clock, dt: 3.5);
            Assert.Equal(StepResult.Failed, step.Result);
            Assert.Equal(attempt + 1, behavior.LineOfSight.StrikesFor(9u));
        }
        Assert.True(behavior.LineOfSight.IsBlacklisted(9u));
        // Left alone: the only thing combat still wants is to drop out of melee mode.
        Assert.True(behavior.WantsControl(Blackboard.Capture(surface, clock, 25f, 15f), out string reason));
        Assert.Equal("leaving combat", reason);
    }

    [Fact]
    public void ABigMonsterTheBodyBumpsIntoIsSwungAtBeyondTheReachSetting()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 2.5f });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Noble", 4f));
        Place(surface, 9u, north: 4d, east: 0d);
        // The walk north runs straight into the Noble itself.
        surface.BlockedWalkHeadings.Add(0);
        surface.WalkBlockingObjectId = 9u;

        // Body to body: no walk, straight to the swing.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("attack:9:Medium:1", surface.Commands[^1]);
        Assert.DoesNotContain(surface.Commands, command => command.StartsWith("move:", StringComparison.Ordinal));
    }

    [Fact]
    public void MeleeWalksUpToAFarTargetThenSwings()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 2.5f });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 8f));
        Place(surface, 7u, north: 8d, east: 0d);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["move:forward"], surface.Commands);
        Assert.Empty(surface.PathQueries);

        MoveHostile(surface, 7u, 2f);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("attack:7:Medium:1", surface.Commands[^1]);
    }

    [Fact]
    public void InterruptWhileApproachingStopsWalking()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 8f));
        Place(surface, 7u, north: 8d, east: 0d);
        Step(behavior, surface, clock);
        Assert.Equal("move:forward", surface.Commands[^1]);

        behavior.Interrupt(Context(surface, clock));

        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.False(behavior.IsApproaching);
        Assert.Null(surface.Intent);
    }

    [Fact]
    public void ATargetThatVanishesMidApproachEndsTheWalk()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 8f));
        Place(surface, 7u, north: 8d, east: 0d);
        Step(behavior, surface, clock);

        surface.Hostiles.Clear();
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal("move:clear", surface.Commands[^1]);
    }

    [Fact]
    public void LineOfSightOffShootsWithoutAsking()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Magic,
            LineOfSight = new LineOfSightSettings { Enabled = false },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 12f));
        surface.BlockPath(9u);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.Equal(["cast:300@9"], surface.Commands);
        Assert.Empty(surface.PathQueries);
    }

    // ── walking as an obstacle sense ─────────────────────────────────────

    [Fact]
    public void AMeleeTargetNoHeadingReachesIsStruckAndAReachableOneWalkedTo()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(1, "Behind the fence", 6f));
        surface.Hostiles.Add(Hostile(2, "In the open", 8f));
        Place(surface, 1u, north: 6d, east: 0d);
        Place(surface, 2u, north: -8d, east: 0d);
        // Everything toward the fence, and its whole steering fan, is walled off.
        surface.BlockedWalkHeadings.UnionWith([0, 30, 330, 60, 300, 90, 270]);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.Equal(2u, behavior.CurrentTargetId);
        Assert.True(behavior.IsApproaching);
        Assert.Equal(["face:180"], surface.Commands);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(1u));
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(2u));
    }

    [Fact]
    public void ApproachSteersAroundABlockedHeadingAndComesBackWhenItClears()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 10f));
        Place(surface, 7u, north: 10d, east: 0d);
        surface.BlockedWalkHeadings.Add(0);

        // Straight north is blocked; the first fan heading is 30 degrees
        // right, past the turn-in-place angle, so the walker faces it first.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["face:30"], surface.Commands);
        Assert.Equal(30f, behavior.ApproachHeadingDegrees);
        // The fake turns instantly, so the next tick runs.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);

        // The obstacle is passed: back onto the direct heading.
        surface.BlockedWalkHeadings.Clear();
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.Equal(["face:30", "move:forward", "move:clear", "face:0"], surface.Commands);
        Assert.Equal(0f, behavior.ApproachHeadingDegrees);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);
    }

    [Fact]
    public void ApproachWithNoOpenHeadingTriesRecoveryMovesOneAtATime()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d, BlacklistStrikes = 10 },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 10f));
        Place(surface, 7u, north: 10d, east: 0d);

        // Reachable at selection time, walled in once the walk begins.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["move:forward"], surface.Commands);
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.Equal("move:back", surface.Commands[^1]);
        // The recovery runs its course before the walk is reconsidered.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.1).Result);
        Assert.Equal("move:back", surface.Commands[^1]);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.Equal(["move:forward", "move:back", "move:clear", "move:left"], surface.Commands);

        // The way opens again: back to walking.
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Clear;
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.7).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);
    }

    [Fact]
    public void ARangedTargetBlockedBeyondRangeIsNotWalkedToWhenNoHeadingIsOpen()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Magic });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 15f));
        surface.BlockPath(9u);
        Place(surface, 9u, north: 15d, east: 0d);
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;

        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);

        Assert.Empty(surface.Commands);
        Assert.False(behavior.IsApproaching);
        // One strike for the shot and one for the walk.
        Assert.Equal(2, behavior.LineOfSight.StrikesFor(9u));
    }
}
