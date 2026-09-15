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
            surface,
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
    public void TargetSelectorLeavesTheFloorOverheadAlone()
    {
        var settings = new CombatSettings { MaxHeightDifferenceMeters = 3.5f };
        PluginCombatTarget[] hostiles =
        [
            Hostile(1, "Upstairs", 4f) with { HeightDifferenceMeters = 6f },
            Hostile(2, "Downstairs", 5f) with { HeightDifferenceMeters = -5f },
            Hostile(3, "Up the ramp", 6f) with { HeightDifferenceMeters = 2f },
        ];

        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out PluginCombatTarget target));
        Assert.Equal(3u, target.ObjectId);

        // Zero fights at any height.
        settings = settings with { MaxHeightDifferenceMeters = 0f };
        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out target));
        Assert.Equal(1u, target.ObjectId);
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
    public void ATargetThatDiesAfterASwingIsAKillAndOneThatWalksOffIsNot()
    {
        static void Swing(CombatBehavior behavior, FakeAutomationSurface surface, TickClock clock)
        {
            for (int step = 0; step < 8 && (surface.Commands.Count == 0 || surface.Commands[^1] != "release"); step++)
            {
                Step(behavior, surface, clock);
                surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee, PowerBarLevel = 1f };
            }
            Assert.Equal("release", surface.Commands[^1]);
            surface.CompleteSwing();
        }
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, LineOfSight = new LineOfSightSettings { Enabled = false } });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));
        surface.Corpses.Add(new PluginLootContainer(900u, 0u, "Corpse of Drudge", 3f, true, false, false)); // an old one lying about

        Swing(behavior, surface, clock);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(0, behavior.Kills);

        // Health gone: a kill.
        surface.Hostiles[0] = new PluginCombatTarget(7u, "Drudge", 0u, 2f, 0f, true, 0f);
        Step(behavior, surface, clock);
        Assert.Equal(1, behavior.Kills);

        // A second one, swung at, then gone from the list with only the old corpse nearby: not a kill.
        surface.Hostiles.Clear();
        surface.Hostiles.Add(Hostile(8, "Drudge", 2f));
        Swing(behavior, surface, clock);
        Step(behavior, surface, clock);
        surface.Hostiles.Clear();
        Step(behavior, surface, clock);
        Assert.Equal(1, behavior.Kills);

        // A third, gone with a fresh corpse of its name beside the character: a kill.
        surface.Hostiles.Add(Hostile(9, "Drudge", 2f));
        Swing(behavior, surface, clock);
        Step(behavior, surface, clock);
        surface.Hostiles.Clear();
        surface.Corpses.Add(new PluginLootContainer(901u, 0u, "Corpse of Drudge", 2f, false, false, false));
        Step(behavior, surface, clock);
        Assert.Equal(2, behavior.Kills);

        // A fourth goes down while the next target has already been taken up: still its kill.
        surface.Hostiles.Add(Hostile(10, "Drudge", 2f));
        Swing(behavior, surface, clock);
        surface.Hostiles.Clear();
        surface.Hostiles.Add(Hostile(11, "Drudge", 2.2f));
        Step(behavior, surface, clock); // 10 is gone but its corpse is not there yet; 11 is taken up
        Step(behavior, surface, clock);
        Assert.Equal(2, behavior.Kills);
        surface.Corpses.Add(new PluginLootContainer(902u, 0u, "Corpse of Drudge", 2f, false, false, false));
        Step(behavior, surface, clock);
        Assert.Equal(3, behavior.Kills);
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
        Assert.Equal(["9:Missile:Medium", "9:Missile:High", "9:Missile:Low"], surface.PathQueries.Where(query => query.Contains(":Missile:", StringComparison.Ordinal)));
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
    public void ABlockedRangedTargetIsStruckAndNeverWalkedToWhateverItsDistance()
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

        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.False(behavior.IsApproaching);
        Assert.Empty(surface.Commands);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(9u));
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));

        // The path opens: shoot from here.
        surface.ClearPath(9u);
        clock.Advance(0.6d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Tusker at 15.0m", reason);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("cast:300@9", surface.Commands[^1]);
    }

    [Fact]
    public void ASwingTheServerDoesNotTakeBecomesAWalkInToItsOwnReach()
    {
        // Reach set generously: the swing goes out at three metres. The
        // server would walk the character in for it, but hemmed in it never
        // does, so after a moment the bot walks in itself and swings again.
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 3.5f, LineOfSight = new LineOfSightSettings { Enabled = false } });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Noble", 3f));
        Place(surface, 9u, north: 3d, east: 0d);

        Step(behavior, surface, clock);
        Assert.Contains(surface.Commands, c => c.StartsWith("attack:9", StringComparison.Ordinal));
        Step(behavior, surface, clock); // released; the server is now to answer
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 1d).Result);

        // Nothing back after three seconds and still three metres off: closing in.
        Step(behavior, surface, clock, dt: 2.5d);
        Assert.True(behavior.IsApproaching);
        Assert.Contains("abort", surface.Commands);
        Assert.Contains(surface.Commands, c => c.StartsWith("move:", StringComparison.Ordinal));

        // Within the server's reach: the walk ends and the next step swings again.
        surface.Hostiles[0] = Hostile(9, "Noble", 1.8f);
        Place(surface, 9u, north: 1.8d, east: 0d);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.False(behavior.IsApproaching);
        int swings = surface.Commands.Count(c => c.StartsWith("attack:9", StringComparison.Ordinal));
        Step(behavior, surface, clock);
        Assert.Equal(swings + 1, surface.Commands.Count(c => c.StartsWith("attack:9", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnApproachThatGoesNowhereTimesOutWithAStrike()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, ApproachRangeMeters = 20f, ApproachTimeoutSeconds = 3d });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Tusker", 15f));
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
    public void AnOpenWalkToAMonsterSevenMetresOffIsAnApproachNotASwing()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 2.5f, ApproachRangeMeters = 10f });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Soldier", 7f));
        Place(surface, 9u, north: 7d, east: 0d);
        // Nothing between: the body could walk the whole way, so it is not touching anything.

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.True(behavior.IsApproaching);
        Assert.DoesNotContain(surface.Commands, command => command.StartsWith("attack:", StringComparison.Ordinal));
    }

    [Fact]
    public void MeleeWalksUpToAFarTargetThenSwings()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 2.5f, ApproachRangeMeters = 10f });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 8f));
        Place(surface, 7u, north: 8d, east: 0d);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["move:forward"], surface.Commands);
        // A melee walk sweeps no shot; only the sight check looks along a straight line.
        Assert.All(surface.PathQueries, query => Assert.Contains(":Straight:", query));

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
            Build(new CombatSettings { Style = CombatStyle.Melee, ApproachRangeMeters = 10f });
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
            Build(new CombatSettings { Style = CombatStyle.Melee, ApproachRangeMeters = 10f });
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
    public void AHostileTheWorldHidesIsNotATargetAtAllUntilItComesIntoView()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            MeleeRangeMeters = 1.5f,
            ApproachRangeMeters = 10f,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Upstairs", 5f));
        Place(surface, 9u, north: 5d, east: 0d);
        surface.PathBlockedByEnvironment = true;
        surface.BlockPath(9u);

        // Hidden: nothing to fight, no walk, and no strike to wait out.
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.DoesNotContain(surface.Commands, command => command.StartsWith("move:", StringComparison.Ordinal) || command.StartsWith("attack:", StringComparison.Ordinal));
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(9u));
        Assert.False(behavior.LineOfSight.IsBlacklisted(9u));

        // It comes round the corner: the next look sees it and the walk begins.
        surface.ClearPath(9u);
        clock.Advance(0.6d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Upstairs at 5.0m", reason);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.True(behavior.IsApproaching);

        // A creature steps in the way mid-walk: not a wall, the walk goes on.
        surface.PathBlockedByEnvironment = false;
        surface.BlockPath(9u);
        clock.Advance(0.6d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.True(behavior.IsApproaching);
    }

    [Fact]
    public void AMeleeHostileCoveredByAnotherCreatureIsPassedOverForTheOneInFront()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            MeleeRangeMeters = 1.5f,
            ApproachRangeMeters = 10f,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        // The straggler ranks first (nearer), but every line to it stops at the swarm-mate in front.
        surface.Hostiles.Add(Hostile(9, "Straggler", 4f));
        surface.Hostiles.Add(Hostile(8, "Swarm-mate", 6f));
        Place(surface, 9u, north: 4d, east: 0d);
        Place(surface, 8u, north: 6d, east: 0d);
        surface.BlockingObjectId = 8u;
        surface.BlockPath(9u);

        // The one in front is what the character walks at; the covered one is
        // not a wall, so it is not hidden for good, just not the target now.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Swarm-mate at 6.0m", reason);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(8u, behavior.CurrentTargetId);
        Assert.False(behavior.LineOfSight.IsBlacklisted(9u));
    }

    [Fact]
    public void AnApproachLetsGoOfATargetThatWalksOutOfSight()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            MeleeRangeMeters = 1.5f,
            ApproachRangeMeters = 20f,
            ApproachTimeoutSeconds = 30d,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(9, "Runner", 12f));
        Place(surface, 9u, north: 12d, east: 0d);

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.True(behavior.IsApproaching);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.3).Result);

        // Round the corner: the world now hides it. The walk stops there, well
        // before its timeout, rather than steering round the corner after it.
        surface.PathBlockedByEnvironment = true;
        surface.BlockPath(9u);
        BehaviorStep step = Step(behavior, surface, clock, dt: 0.6);
        Assert.Equal(StepResult.Failed, step.Result);
        Assert.StartsWith("lost sight of Runner", step.Reason);
        Assert.False(behavior.IsApproaching);
        Assert.Equal("move:clear", surface.Commands[^1]);
        // Not a hostile now, and nothing held against it: it is fought when it shows itself again.
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.False(behavior.LineOfSight.IsBlacklisted(9u));
    }

    [Fact]
    public void ARefusedAttackIsTriedAgainOnTheSameTargetBeforeItIsStruck()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 2f));
        surface.Hostiles.Add(Hostile(8, "Other drudge", 2.2f));
        surface.RefusedAttackTargets.Add(7u);

        // Refused: the target is held, not dropped for the next one.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(7u, behavior.CurrentTargetId);
        Assert.Equal(1, surface.Commands.Count(command => command.StartsWith("attack:", StringComparison.Ordinal)));
        // Inside the hold nothing is tried.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(1, surface.Commands.Count(command => command.StartsWith("attack:", StringComparison.Ordinal)));
        // After it, again; the third refusal is a strike and the target is let go.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.4).Result);
        BehaviorStep step = Step(behavior, surface, clock, dt: 0.4);
        Assert.Equal(StepResult.Failed, step.Result);
        Assert.StartsWith("attack refused 3 times", step.Reason);
        Assert.Equal(3, surface.Commands.Count(command => command.StartsWith("attack:7:", StringComparison.Ordinal)));
        Assert.True(behavior.LineOfSight.IsBlacklisted(7u));

        // The other one swings fine.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(8u, behavior.CurrentTargetId);
        Assert.Contains(surface.Commands, command => command.StartsWith("attack:8:", StringComparison.Ordinal));
    }

    [Fact]
    public void AMeleeTargetBehindAWallIsNeverWalkedAtAndIsBlacklistedBySweeps()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            LineOfSight = new LineOfSightSettings { CacheSeconds = 0.5d, BlacklistStrikes = 3, BlacklistSeconds = 30d },
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        // The monster on the floor above: five metres away on the map, the
        // direct walk stopped by the world, and every fan heading open.
        surface.Hostiles.Add(Hostile(9, "Upstairs", 5f));
        Place(surface, 9u, north: 5d, east: 0d);
        surface.BlockedWalkHeadings.Add(0);
        surface.WalkBlockedByEnvironment = true;

        // Not a target: no walk, one strike per fresh sweep.
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.DoesNotContain(surface.Commands, command => command.StartsWith("move:", StringComparison.Ordinal));
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(9u));
        Step(behavior, surface, clock, dt: 0.6);
        Assert.Equal(2, behavior.LineOfSight.StrikesFor(9u));
        Step(behavior, surface, clock, dt: 0.6);
        Assert.True(behavior.LineOfSight.IsBlacklisted(9u));

        // Something standing in the way (a creature) is different: the fan goes round it.
        surface.WalkBlockedByEnvironment = false;
        surface.Hostiles.Add(Hostile(10, "Behind a drudge", 6f));
        Place(surface, 10u, north: 6d, east: 0d);
        clock.Advance(0.6d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Behind a drudge at 6.0m", reason);
    }

    [Fact]
    public void AMeleeTargetNoHeadingReachesIsStruckAndAReachableOneWalkedTo()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, ApproachRangeMeters = 10f });
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
        Assert.Equal(["move:turnright"], surface.Commands);
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(1u));
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(2u));
    }

    [Fact]
    public void ApproachSteersAroundABlockedHeadingAndComesBackWhenItClears()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            ApproachRangeMeters = 12f,
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
        Assert.Equal(["move:turnright"], surface.Commands);
        Assert.Equal(30f, behavior.ApproachHeadingDegrees);
        // Turned: the next tick runs.
        surface.Position = surface.Position with { HeadingDegrees = 30f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);

        // The obstacle is passed: back onto the direct heading, a turn in place again.
        surface.BlockedWalkHeadings.Clear();
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock, dt: 0.6).Result);
        Assert.Equal(["move:turnright", "move:clear", "move:forward", "move:turnleft"], surface.Commands);
        Assert.Equal(0f, behavior.ApproachHeadingDegrees);
        surface.Position = surface.Position with { HeadingDegrees = 0f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("move:forward", surface.Commands[^1]);
    }

    [Fact]
    public void ApproachWithNoOpenHeadingTriesRecoveryMovesOneAtATime()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Melee,
            ApproachRangeMeters = 12f,
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
    public void AMeleeTargetBeyondTheWalkRangeIsLeftAloneWithoutAStrike()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, MeleeRangeMeters = 1.5f, ApproachRangeMeters = 6f, LeaveCombatWhenIdle = false });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 9f));
        surface.Hostiles.Add(Hostile(8, "Nearer drudge", 5f));
        Place(surface, 7u, north: 9d, east: 0d);
        Place(surface, 8u, north: -5d, east: 0d);

        // The nearer one, inside the walk range, is walked to; the far one is not a target at all.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Nearer drudge at 5.0m", reason);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(8u, behavior.CurrentTargetId);
        Assert.True(behavior.IsApproaching);
        Assert.Equal(0, behavior.LineOfSight.StrikesFor(7u));

        // With only the far one left, combat has nothing to do until it comes closer.
        surface.Hostiles.RemoveAt(1);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        MoveHostile(surface, 7u, 5.5f);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
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
        // One strike for the shot; the walk is never tried.
        Assert.Equal(1, behavior.LineOfSight.StrikesFor(9u));
    }
}
