using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class RouteActionTests
{
    private static PluginNavigationPosition At(double eastWest, double northSouth, uint cell = 0x00010100u) =>
        new(cell, eastWest, northSouth, 0d, 0f, true);

    private static PluginNavigationSnapshot Snapshot(FakeAutomationSurface surface) =>
        ((INavigationAutomation)surface).Snapshot;

    private static PluginWorldObject Landscape(uint id, string name, double eastWest, double northSouth) =>
        new(id, 0u, name, PluginObjectClass.Portal, 0u, 0u, 0u)
        {
            IsLandscape = true,
            HasPosition = true,
            Position = At(eastWest, northSouth),
        };

    [Fact]
    public void ChatStepSubmitsTheLineOnceAfterSettling()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        runner.Begin(new Waypoint(WaypointKind.Chat, 0d, 0d) { Text = "/say hello" }, Snapshot(surface), now: 0d);

        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 0.2d, 4d, log, out _));
        Assert.Empty(surface.Commands);
        Assert.Equal(RouteActionStatus.Done, runner.Tick(surface, Snapshot(surface), 0.7d, 4d, log, out _));
        Assert.Equal(["chat:/say hello"], surface.Commands);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public void RecallCastsAgainUntilTheTeleportShowsThenSettles()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        runner.Begin(new Waypoint(WaypointKind.Recall, 0d, 0d) { SpellId = 48 }, Snapshot(surface), now: 0d);

        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 0.7d, 4d, log, out _));
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 0.8d, 4d, log, out _));
        Assert.Equal(["cast:48"], surface.Commands);

        // A fizzle: nothing happened for the retry period, so cast again.
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 5d, 4d, log, out _));
        Assert.Equal(["cast:48", "cast:48"], surface.Commands);

        // Through portal space and out the other side: teleported.
        surface.IsPortalSpace = true;
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 6d, 4d, log, out _));
        surface.IsPortalSpace = false;
        surface.Position = At(0.5d, 0.5d, cell: 0x00A9_0100u);
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 7d, 4d, log, out _));
        Assert.Equal(["cast:48", "cast:48"], surface.Commands); // no cast once teleported

        // The post-portal settle runs its course.
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 10d, 4d, log, out _));
        Assert.Equal(RouteActionStatus.Done, runner.Tick(surface, Snapshot(surface), 11.1d, 4d, log, out _));
    }

    [Fact]
    public void ALandblockChangeCountsAsATeleportEvenForAShortHop()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        runner.Begin(new Waypoint(WaypointKind.Recall, 0d, 0d) { SpellId = 48 }, Snapshot(surface), now: 0d);
        runner.Tick(surface, Snapshot(surface), 0.7d, 0d, log, out _);
        runner.Tick(surface, Snapshot(surface), 0.8d, 0d, log, out _);

        surface.Position = At(0.01d, 0d, cell: 0x00A9_0100u); // 2.4 m away, other landblock
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 1d, 0d, log, out _));
        Assert.Equal(RouteActionStatus.Done, runner.Tick(surface, Snapshot(surface), 1.1d, 0d, log, out _));
    }

    [Fact]
    public void PortalLooksAgainUntilTheObjectAppearsThenUsesItOnce()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        runner.Begin(
            new Waypoint(WaypointKind.Portal, 0d, 0d) { TargetName = "Portal to Town Network", TargetEastWest = 0.01d, TargetNorthSouth = 0d },
            Snapshot(surface),
            now: 0d);

        runner.Tick(surface, Snapshot(surface), 0.7d, 0d, log, out _);
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 0.8d, 0d, log, out _));
        Assert.Empty(surface.Commands);
        Assert.Contains("looking for", runner.Status);

        // Two same-named portals: the one at the recorded spot wins.
        surface.WorldObjects.Add(Landscape(0x7000_0001u, "Portal to Town Network", 0.5d, 0.5d));
        surface.WorldObjects.Add(Landscape(0x7000_0002u, "Portal to Town Network", 0.011d, 0.001d));
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 2.5d, 0d, log, out _));
        Assert.Equal(["useobject:1879048194"], surface.Commands);
        Assert.Equal(0x7000_0002u, runner.TargetObjectId);

        // Not used again while the walk to it is in progress.
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 5d, 0d, log, out _));
        Assert.Single(surface.Commands);

        surface.Position = At(1d, 1d);
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 6d, 0d, log, out _));
        Assert.Equal(RouteActionStatus.Done, runner.Tick(surface, Snapshot(surface), 6.1d, 0d, log, out _));
    }

    [Fact]
    public void ARefusedUseIsRetriedAndTheStepGivesUpAfterTheTimeout()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        surface.WorldObjects.Add(Landscape(0x7000_0001u, "Portal", 0d, 0d));
        surface.NextUseStatus = PluginItemCommandStatus.Busy;
        runner.Begin(new Waypoint(WaypointKind.Portal, 0d, 0d) { TargetName = "Portal" }, Snapshot(surface), now: 0d);

        runner.Tick(surface, Snapshot(surface), 0.7d, 0d, log, out _);
        runner.Tick(surface, Snapshot(surface), 0.8d, 0d, log, out _);
        runner.Tick(surface, Snapshot(surface), 2.5d, 0d, log, out _);
        Assert.Equal(2, surface.Commands.Count(c => c.StartsWith("useobject:", StringComparison.Ordinal)));

        Assert.Equal(RouteActionStatus.Failed, runner.Tick(surface, Snapshot(surface), 61d, 0d, log, out string failure));
        Assert.Contains("gave up", failure);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public void NpcStepUsesTheNpcAndMovesOnAfterAMoment()
    {
        var surface = new FakeAutomationSurface();
        var runner = new RouteActionRunner();
        var log = new FakeLogger();
        surface.WorldObjects.Add(Landscape(0x7000_0009u, "Ulgrim the Unpleasant", 0d, 0d));
        runner.Begin(new Waypoint(WaypointKind.Npc, 0d, 0d) { TargetName = "Ulgrim" }, Snapshot(surface), now: 0d);

        runner.Tick(surface, Snapshot(surface), 0.7d, 0d, log, out _);
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 0.8d, 0d, log, out _));
        Assert.Equal(["useobject:1879048201"], surface.Commands);
        Assert.Equal(RouteActionStatus.Running, runner.Tick(surface, Snapshot(surface), 1.5d, 0d, log, out _));
        Assert.Equal(RouteActionStatus.Done, runner.Tick(surface, Snapshot(surface), 2.5d, 0d, log, out _));
    }

    [Fact]
    public void NavigationBehaviorWalksToThePortalThenUsesItAndCarriesOn()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new NavigationSettings { PostPortalDelaySeconds = 1d };
        var behavior = new NavigationBehavior(() => settings);
        var route = new Route
        {
            Waypoints =
            [
                new Waypoint(WaypointKind.Point, 0d, 0.02d),
                new Waypoint(WaypointKind.Portal, 0d, 0.02d) { TargetName = "Portal", TargetEastWest = 0d, TargetNorthSouth = 0.021d },
                new Waypoint(WaypointKind.Point, 0.5d, 0.52d),
            ],
        };
        behavior.SetRoute(route);
        surface.WorldObjects.Add(Landscape(0x7000_0001u, "Portal", 0d, 0.021d));

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        // Walk north to the first point.
        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]);

        // Arrive: the point is done, the portal step begins and the keys are released.
        surface.Position = At(0d, 0.02d);
        clock.Advance(1d);
        behavior.Execute(Context());
        behavior.Execute(Context());
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.Equal(1, behavior.WaypointIndex);

        clock.Advance(0.7d);
        behavior.Execute(Context()); // settled: on to firing
        behavior.Execute(Context());
        Assert.Equal("useobject:1879048193", surface.Commands[^1]);
        Assert.True(behavior.WantsControl(Context().Board, out string reason));
        Assert.Contains("portal", reason);

        // Through the portal: the route waits in portal space and settles after.
        surface.IsPortalSpace = true;
        clock.Advance(1d);
        behavior.Execute(Context());
        surface.IsPortalSpace = false;
        surface.Position = At(0.5d, 0.5d, cell: 0x00A9_0100u);
        clock.Advance(0.5d);
        behavior.Execute(Context());
        Assert.Equal(1, behavior.WaypointIndex);
        clock.Advance(1.1d);
        behavior.Execute(Context());
        Assert.Equal(2, behavior.WaypointIndex);

        // And walks on toward the last point without reading the arrival as a second teleport.
        clock.Advance(0.1d);
        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]);
    }

    [Fact]
    public void ATeleportTheRouteDidNotFireStopsTheWalkAndSettles()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new NavigationSettings { PostPortalDelaySeconds = 2d };
        var behavior = new NavigationBehavior(() => settings);
        behavior.SetRoute(new Route { Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0.1d)] });

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]);

        // Someone recalled by hand: 100 m away between two ticks.
        surface.Position = At(0.4d, 0.1d);
        clock.Advance(0.1d);
        behavior.Execute(Context());
        Assert.Equal("move:clear", surface.Commands[^1]);
        int commands = surface.Commands.Count;

        clock.Advance(1d);
        behavior.Execute(Context());
        Assert.Equal(commands, surface.Commands.Count); // still settling

        clock.Advance(1.5d);
        behavior.Execute(Context());
        Assert.NotEqual(commands, surface.Commands.Count); // walking again
    }
}
