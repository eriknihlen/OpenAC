using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class PatrolControllerTests
{
    private const uint Block = 0x01A9_0000u;

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) => _text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => _text.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        public void WriteText(string key, string content) => _text[key] = content;
        public bool Delete(string key) => _text.Remove(key);
    }

    private sealed class FakeDungeon : IDungeonAutomation
    {
        public List<PluginDungeonCell> Cells { get; } = [];
        public bool IsAvailable => true;
        public IReadOnlyList<PluginDungeonCell> CaptureCells(uint landblockId) => Cells;
    }

    private static PluginDungeonCell Cell(uint low, double eastMeters, double northMeters, params uint[] neighbors) =>
        new(Block | low, eastMeters / 240d, northMeters / 240d, 0d, neighbors.Select(n => Block | n).ToArray());

    private static (BotController Controller, FakeAutomationSurface Surface, TickClock Clock) Build() =>
        Build(out _);

    private static (BotController Controller, FakeAutomationSurface Surface, TickClock Clock) Build(out FakeDungeon dungeon)
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        // A square loop of four rooms and a spur off room 2.
        dungeon = new FakeDungeon();
        dungeon.Cells.AddRange(
        [
            Cell(0x100, 0d, 0d, 0x101, 0x103),
            Cell(0x101, 20d, 0d, 0x100, 0x102, 0x104),
            Cell(0x102, 20d, 20d, 0x101, 0x103),
            Cell(0x103, 0d, 20d, 0x102, 0x100),
            Cell(0x104, 40d, 0d, 0x101),
        ]);
        surface.Position = new PluginNavigationPosition(Block | 0x100, 0d, 0d, 0d, 0f, false);
        var settings = new NavigationSettings();
        var navigation = new NavigationBehavior(() => settings);
        var engine = new BotEngine(surface, new FakeLogger(), [navigation], clock);
        var controller = new BotController(
            engine,
            new BotStore(new MemoryStorage()),
            navigation,
            new SelfBuffBehavior(new SpellSelector(surface, surface), new CastTracker(surface, clock), () => engine.Profile.Buffs, surface),
            () => surface.Navigation.Snapshot,
            dungeon: dungeon,
            hazards: new DungeonHazards(new MemoryStorage()));
        controller.ObjectScan = surface.CaptureObjects;
        return (controller, surface, clock);
    }

    [Fact]
    public void ALavaPoolInViewMarksItsCellAndReroutesThePatrolAroundIt()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build();
        Assert.True(controller.TryStartPatrol(out string message));
        Assert.Contains("patrolling", message);
        Assert.True(controller.IsPatrolling);
        Route before = controller.Navigation.Route!;
        // The doorway into room 3 from room 2 sits at (20 E, 10 N); the walk passes it.
        Assert.Contains(before.Waypoints, w => w.EastWest * 240d > 15d && w.NorthSouth * 240d > 5d);

        // A lava pool sits in room 3 (0x102).
        surface.WorldObjects.Add(new PluginWorldObject(0x7000_0001u, 0u, "Pool of Lava", PluginObjectClass.Misc, 0u, 0u, 0u)
        {
            HasPosition = true,
            Position = new PluginNavigationPosition(Block | 0x102, 20d / 240d, 20d / 240d, 0d, 0f, false),
        });
        clock.Advance(1.5d);
        controller.Tick(clock.Now);

        Assert.Equal(1, controller.HazardCount);
        Route after = controller.Navigation.Route!;
        Assert.NotSame(before, after);
        Assert.DoesNotContain(after.Waypoints, w => w.EastWest * 240d > 15d && w.NorthSouth * 240d > 5d);
        Assert.True(controller.IsPatrolling);
    }

    [Fact]
    public void AcidDamageTwiceInACellMarksItAHazardAndReroutes()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build();
        controller.ChatAfter = surface.CaptureMessages;
        Assert.True(controller.TryStartPatrol(out _));
        Route before = controller.Navigation.Route!;
        // Standing in room 3 with acid underfoot: the first tick is not yet a hazard, the second is.
        surface.Position = new PluginNavigationPosition(Block | 0x102, 20d / 240d, 20d / 240d, 0d, 0f, false);
        surface.ChatMessages.Add(new PluginChatMessage(1UL, 0u, 4, string.Empty, "You suffer 47 damage from acid!", string.Empty));
        clock.Advance(1.5d);
        controller.Tick(clock.Now);
        Assert.Equal(0, controller.HazardCount);

        surface.ChatMessages.Add(new PluginChatMessage(2UL, 0u, 4, string.Empty, "You suffer 44 damage from acid!", string.Empty));
        clock.Advance(3d);
        controller.Tick(clock.Now);
        Assert.Equal(1, controller.HazardCount);
        Assert.NotSame(before, controller.Navigation.Route);
        Assert.Contains(controller.Log.Snapshot(), line => line.Text.Contains("taking acid damage", StringComparison.Ordinal));
    }

    [Fact]
    public void ATeleportOutOfTheDungeonPutsThePatrolDownAndLoginPatrolRearms()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build();
        controller.Update(p => p with { Navigation = p.Navigation with { PatrolOnLogin = true } });
        Assert.True(controller.TryStartPatrol(out _));
        Assert.True(controller.IsPatrolling);

        // Whisked outdoors, twenty kilometres from the route.
        surface.Position = new PluginNavigationPosition(0x016C0013u, 0.5d, -0.5d, 0d, 0f, true);
        clock.Advance(1.5d);
        controller.Tick(clock.Now);
        Assert.False(controller.IsPatrolling);
        Assert.Null(controller.Navigation.Route);
        Assert.Contains(controller.Log.Snapshot(), line => line.Text.Contains("patrol stopped", StringComparison.Ordinal));

        // Back inside: the login patrol starts over.
        surface.Position = new PluginNavigationPosition(Block | 0x100, 0d, 0d, 0d, 0f, false);
        clock.Advance(1.5d);
        controller.Tick(clock.Now);
        Assert.True(controller.IsPatrolling);
    }

    [Fact]
    public void APointGivenUpOnIsRememberedForTheNextPatrolOfTheDungeon()
    {
        var storage = new MemoryStorage();
        var hazards = new DungeonHazards(storage);
        string key = NavigationBehavior.GivenUpKey(Block | 0x100, new Waypoint(WaypointKind.Point, 0d, 20d / 240d));
        Assert.True(hazards.AddGivenUp(Block | 0x100, key));
        Assert.False(hazards.AddGivenUp(Block | 0x100, key));
        // Another instance over the same storage - the next session - reads it back.
        Assert.Contains(key, new DungeonHazards(storage).GivenUpFor(Block | 0x100));
    }

    [Fact]
    public void TheMarketplaceIsNotADungeonToPatrol()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build(out FakeDungeon dungeon);
        // The fixture's cells, but in the Marketplace's landblock.
        var cells = dungeon.Cells.Select(c => c with { CellId = 0x016C0000u | (c.CellId & 0xFFFFu), Neighbors = c.Neighbors.Select(n => 0x016C0000u | (n & 0xFFFFu)).ToArray() }).ToArray();
        dungeon.Cells.Clear();
        dungeon.Cells.AddRange(cells);
        surface.Position = new PluginNavigationPosition(0x016C0100u, 0d, 0d, 0d, 0f, false);

        Assert.False(controller.TryStartPatrol(out string message));
        Assert.Contains("no-patrol list", message);
        Assert.False(controller.IsPatrolling);
    }

    [Fact]
    public void PatrolOnLoginStartsTheBotOnceInsideADungeon()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build();
        controller.Update(p => p with { Navigation = p.Navigation with { PatrolOnLogin = true } });
        surface.Position = new PluginNavigationPosition(0x00010005u, 0d, 0d, 0d, 0f, true); // outdoors: nothing yet
        clock.Advance(1.5d);
        controller.Tick(clock.Now);
        Assert.False(controller.Engine.IsRunning);

        surface.Position = new PluginNavigationPosition(Block | 0x101, 20d / 240d, 0d, 0d, 0f, false);
        clock.Advance(1.5d);
        controller.Tick(clock.Now);
        Assert.True(controller.Engine.IsRunning);
        Assert.True(controller.IsPatrolling);
    }

    [Fact]
    public void PatrolOnLoginKeepsAskingUntilTheDungeonHasStreamedIn()
    {
        (BotController controller, FakeAutomationSurface surface, TickClock clock) = Build(out FakeDungeon dungeon);
        controller.Update(p => p with { Navigation = p.Navigation with { PatrolOnLogin = true } });
        // In the dungeon, but its cells have not arrived yet: nothing to build from.
        var cells = dungeon.Cells.ToArray();
        dungeon.Cells.Clear();
        for (int second = 0; second < 5; second++)
        {
            clock.Advance(1.1d);
            controller.Tick(clock.Now);
        }
        Assert.False(controller.Engine.IsRunning);
        Assert.Contains(controller.Log.Snapshot(), line => line.Text.Contains("waiting to start on login", StringComparison.Ordinal));
        Assert.DoesNotContain(controller.Log.Snapshot(), line => line.Level == BotLogLevel.Quiet && line.Text.Contains("no cells", StringComparison.Ordinal));

        // The cells stream in: the next ask builds the patrol and starts the bot.
        dungeon.Cells.AddRange(cells);
        clock.Advance(1.1d);
        controller.Tick(clock.Now);
        Assert.True(controller.Engine.IsRunning);
        Assert.True(controller.IsPatrolling);
        Assert.Contains(controller.Log.Snapshot(), line => line.Text.Contains("started on login after", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedSettingIsSavedAMomentLaterUnderTheProfileInUse()
    {
        var storage = new MemoryStorage();
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var store = new BotStore(storage);
        var navigation = new NavigationBehavior(() => new NavigationSettings());
        var engine = new BotEngine(surface, new FakeLogger(), [navigation], clock);
        var controller = new BotController(
            engine, store, navigation,
            new SelfBuffBehavior(new SpellSelector(surface, surface), new CastTracker(surface, clock), () => engine.Profile.Buffs, surface),
            () => surface.Navigation.Snapshot);
        controller.SaveProfile("mine");
        Assert.Equal("mine", store.LastProfileName);

        clock.Advance(1d);
        controller.Update(p => p with { Combat = p.Combat with { EngageDistance = 9f } });
        controller.Tick(clock.Now);
        // Not yet: a dragged slider is one write, not a hundred.
        Assert.NotEqual(9f, store.LoadProfile("mine")!.Combat.EngageDistance);
        clock.Advance(BotController.AutoSaveDelaySeconds + 0.1d);
        controller.Tick(clock.Now);
        Assert.Equal(9f, store.LoadProfile("mine")!.Combat.EngageDistance);

        // A change that changes nothing writes nothing; shutdown writes what is pending.
        controller.Update(p => p);
        controller.Update(p => p with { Combat = p.Combat with { EngageDistance = 11f } });
        controller.FlushProfile();
        Assert.Equal(11f, store.LoadProfile("mine")!.Combat.EngageDistance);

        // A route loaded by name is the profile's route, and comes back with it.
        store.SaveRoute(new Route { Name = "hall", Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0.1d)] });
        Assert.True(controller.LoadRouteByName("hall"));
        controller.FlushProfile();
        Assert.Equal("hall", store.LoadProfile("mine")!.Navigation.RouteName);
    }
}
