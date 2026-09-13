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

    private static (BotController Controller, FakeAutomationSurface Surface, TickClock Clock) Build()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        // A square loop of four rooms and a spur off room 2.
        var dungeon = new FakeDungeon();
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
}
