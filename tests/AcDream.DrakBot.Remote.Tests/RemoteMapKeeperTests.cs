using System.Numerics;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteMapKeeperTests
{
    private static RemoteDungeonGeometry Geometry(uint landblock) => new(landblock,
    [
        new RemoteDungeonPolygon(RemoteDungeonSurface.Floor, [new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(10f, 10f), new Vector2(0f, 10f)], 0f, 0f),
    ]);

    private static PluginNavigationSnapshot Indoors(uint landblock) =>
        new(true, false, 0x50000001u, new PluginNavigationPosition(landblock | 0x0103u, -23.8d, -47.4d, 0d, 0f, IsOutdoor: false), false, false);

    private static readonly PluginNavigationSnapshot Outdoors =
        new(true, false, 0x50000001u, new PluginNavigationPosition(0xA9B40023u, 0d, 0d, 0d, 0f, IsOutdoor: true), false, false);

    /// <summary>Ticks until the drawing started on the pool has been taken, or gives up.</summary>
    private static void TickUntilDrawn(RemoteMapKeeper keeper, ref double now, in PluginNavigationSnapshot where, int expected)
    {
        Assert.True(keeper.IsDrawing);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (keeper.IsDrawing && DateTime.UtcNow < deadline)
        {
            now += RemoteMapKeeper.PollSeconds;
            keeper.Tick(now, where);
            Thread.Sleep(5);
        }
        Assert.False(keeper.IsDrawing);
        Assert.Equal(expected, keeper.Count);
    }

    [Fact]
    public void ADungeonEnteredIsReadOnTheTickDrawnOffItAndKeptOnceDone()
    {
        var asked = new List<uint>();
        int drawnOn = -1;
        var keeper = new RemoteMapKeeper(
            landblock => { asked.Add(landblock); return Geometry(landblock); },
            geometry => { drawnOn = Environment.CurrentManagedThreadId; return RemoteDungeonMapRasterizer.Render(geometry, DateTime.MinValue); },
            () => new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));
        double now = 0d;

        keeper.Tick(now, Outdoors);
        Assert.Empty(asked);
        Assert.False(keeper.Changed);

        keeper.Tick(now, Indoors(0x61450000u));
        Assert.Empty(asked);   // the landblock is looked at once a second, not on every tick
        now += RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Indoors(0x61450000u));
        Assert.Equal([0x61450000u], asked);

        TickUntilDrawn(keeper, ref now, Indoors(0x61450000u), 1);
        Assert.NotEqual(Environment.CurrentManagedThreadId, drawnOn);
        Assert.True(keeper.Changed);
        IReadOnlyList<RemoteDungeonMaps> maps = keeper.Snapshot();
        Assert.False(keeper.Changed);
        RemoteDungeonMaps set = Assert.Single(maps);
        Assert.Equal(0x61450000u, set.LandblockId);
        Assert.Equal(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc), set.BuiltUtc);
        Assert.Single(set.Layers);

        // Staying put asks nothing more; leaving and coming back neither.
        for (int tick = 0; tick < 5; tick++)
        {
            now += RemoteMapKeeper.PollSeconds;
            keeper.Tick(now, Indoors(0x61450000u));
        }
        now += RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Outdoors);
        now += RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Indoors(0x61450000u));
        Assert.Equal([0x61450000u], asked);
        Assert.False(keeper.Changed);
    }

    [Fact]
    public void ALandblockWithNothingToDrawIsNotAskedAboutAgainForAWhile_AndAFailedReadIsLogged()
    {
        var asked = new List<uint>();
        var log = new FakeLogger();
        var keeper = new RemoteMapKeeper(
            landblock => { asked.Add(landblock); return landblock == 0x01230000u ? throw new InvalidOperationException("no such file") : null; },
            geometry => RemoteDungeonMapRasterizer.Render(geometry, DateTime.MinValue),
            () => DateTime.UtcNow,
            log);
        double now = RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Indoors(0xA9B40000u));
        Assert.Equal([0xA9B40000u], asked);
        for (int second = 1; second < RemoteMapKeeper.RetrySeconds; second++)
        {
            now += RemoteMapKeeper.PollSeconds;
            keeper.Tick(now, Indoors(0xA9B40000u));
        }
        Assert.Equal([0xA9B40000u], asked);
        now += RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Indoors(0xA9B40000u));
        Assert.Equal([0xA9B40000u, 0xA9B40000u], asked);

        now += RemoteMapKeeper.PollSeconds;
        keeper.Tick(now, Indoors(0x01230000u));
        Assert.Contains(log.Lines, line => line.Contains("0123", StringComparison.Ordinal) && line.Contains("no such file", StringComparison.Ordinal));
        Assert.Equal(0, keeper.Count);
        Assert.False(keeper.Changed);
    }

    [Fact]
    public void OnlySoManyDungeonsAreKept_TheLeastRecentlyVisitedGoesFirst()
    {
        var keeper = new RemoteMapKeeper(
            Geometry,
            geometry => RemoteDungeonMapRasterizer.Render(geometry, DateTime.MinValue),
            () => DateTime.UtcNow);
        double now = 0d;
        for (uint index = 1; index <= RemoteMapKeeper.MaxDungeons + 1; index++)
        {
            uint landblock = index << 16;
            now += RemoteMapKeeper.PollSeconds;
            keeper.Tick(now, Indoors(landblock));
            TickUntilDrawn(keeper, ref now, Indoors(landblock), (int)Math.Min(index, RemoteMapKeeper.MaxDungeons));
        }
        IReadOnlyList<RemoteDungeonMaps> kept = keeper.Snapshot();
        Assert.Equal(RemoteMapKeeper.MaxDungeons, kept.Count);
        Assert.DoesNotContain(kept, set => set.LandblockId == 1u << 16);
        // The most recently visited comes first.
        Assert.Equal((uint)(RemoteMapKeeper.MaxDungeons + 1) << 16, kept[0].LandblockId);
    }
}
