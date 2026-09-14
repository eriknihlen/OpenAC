using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class NavigationTests
{
    private static PluginNavigationPosition At(double eastWest, double northSouth, float heading = 0f) =>
        new(0u, eastWest, northSouth, 0d, heading, true);

    [Theory]
    [InlineData(0d, 1d, 0f)]     // due north
    [InlineData(1d, 0d, 90f)]    // due east
    [InlineData(0d, -1d, 180f)]  // due south
    [InlineData(-1d, 0d, 270f)]  // due west
    [InlineData(1d, 1d, 45f)]
    public void HeadingFollowsTheCompass(double east, double north, float expected)
    {
        float heading = RouteFollower.HeadingTo(At(0d, 0d), At(east, north));
        Assert.Equal(expected, heading, 0.01f);
    }

    [Theory]
    [InlineData(350f, 10f, 20f)]
    [InlineData(10f, 350f, -20f)]
    [InlineData(90f, 270f, 180f)]
    public void HeadingDeltaIsTheShortestTurn(float current, float target, float expected) =>
        Assert.Equal(expected, RouteFollower.HeadingDelta(current, target), 0.01f);

    [Fact]
    public void FollowerTurnsThenWalksThenAdvances()
    {
        var route = new Route
        {
            Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0.05d), new Waypoint(WaypointKind.Point, 0.05d, 0.05d)],
        };
        var follower = new RouteFollower(route, RouteMode.Once);
        follower.Reset();

        NavigationStep step = follower.Advance(At(0d, 0d, heading: 90f), 0d, 1.5d, 12f);
        Assert.Equal(NavigationAction.Turn, step.Action);
        Assert.Equal(0f, step.HeadingDegrees, 0.01f);

        step = follower.Advance(At(0d, 0d, heading: 2f), 0d, 1.5d, 12f);
        Assert.Equal(NavigationAction.Walk, step.Action);
        Assert.Equal(12d, step.DistanceMeters, 0.01d);

        step = follower.Advance(At(0d, 0.05d, heading: 0f), 0d, 1.5d, 12f);
        Assert.Equal(NavigationAction.Hold, step.Action);
        Assert.Equal(1, follower.CurrentIndex);

        step = follower.Advance(At(0.05d, 0.05d, heading: 90f), 0d, 1.5d, 12f);
        Assert.Equal(NavigationAction.Finished, step.Action);
        Assert.True(follower.IsFinished);
    }

    [Fact]
    public void LoopAndPingPongWrapDifferently()
    {
        var route = new Route
        {
            Waypoints =
            [
                // An L, so no point lies on the line to the one after it.
                new Waypoint(WaypointKind.Point, 0d, 0d),
                new Waypoint(WaypointKind.Point, 0.01d, 0d),
                new Waypoint(WaypointKind.Point, 0.01d, 0.01d),
            ],
        };

        var loop = new RouteFollower(route, RouteMode.Loop);
        loop.Reset();
        for (int index = 0; index < 3; index++)
            loop.Advance(route.Waypoints[index].ToPosition(), 0d, 1d, 12f);
        Assert.Equal(0, loop.CurrentIndex);

        var pingPong = new RouteFollower(route, RouteMode.PingPong);
        pingPong.Reset();
        for (int index = 0; index < 3; index++)
            pingPong.Advance(route.Waypoints[index].ToPosition(), 0d, 1d, 12f);
        Assert.Equal(1, pingPong.CurrentIndex);
    }

    [Fact]
    public void PauseWaypointsHoldForTheirDuration()
    {
        var route = new Route
        {
            Waypoints = [new Waypoint(WaypointKind.Pause, 0d, 0d) { Seconds = 5d }, new Waypoint(WaypointKind.Point, 0d, 0.1d)],
        };
        var follower = new RouteFollower(route, RouteMode.Once);
        follower.Reset();

        Assert.Equal(NavigationAction.Hold, follower.Advance(At(0d, 0d), 0d, 1d, 12f).Action);
        Assert.Equal(NavigationAction.Hold, follower.Advance(At(0d, 0d), 4d, 1d, 12f).Action);
        Assert.Equal(0, follower.CurrentIndex);
        Assert.Equal(NavigationAction.Hold, follower.Advance(At(0d, 0d), 5.1d, 1d, 12f).Action);
        Assert.Equal(1, follower.CurrentIndex);
    }

    [Fact]
    public void PassingCloseByAWaypointCountsAsReachingIt()
    {
        var follower = new RouteFollower(
            new Route { Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0d), new Waypoint(WaypointKind.Point, 0d, 0.1d)] },
            RouteMode.Once);
        follower.Reset();

        // Sweep past the first point 2 m to its east (arrival is 1 m): the
        // closest approach is inside 2.5 arrivals and the distance then grows.
        Assert.NotEqual(NavigationAction.Hold, follower.Advance(At(0.01d, -0.02d, heading: 0f), 0d, 1d, 45f).Action);
        Assert.NotEqual(NavigationAction.Hold, follower.Advance(At(0.01d, 0d, heading: 0f), 0.5d, 1d, 45f).Action);
        Assert.Equal(0, follower.CurrentIndex);
        Assert.Equal(NavigationAction.Hold, follower.Advance(At(0.01d, 0.01d, heading: 0f), 1d, 1d, 45f).Action);
        Assert.Equal(1, follower.CurrentIndex);
    }

    [Fact]
    public void TheAimPointBlendsTowardTheNextWaypointInsideTheLookahead()
    {
        var follower = new RouteFollower(
            new Route { Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0d), new Waypoint(WaypointKind.Point, 0.1d, 0d)] },
            RouteMode.Once);
        follower.Reset();

        // 2 m south of the first point with a 4 m lookahead: half way blended
        // toward the second point, which lies east, so the heading swings right of north.
        NavigationStep step = follower.Advance(At(0d, -2d / 240d, heading: 0f), 0d, 1d, 45f, lookaheadMeters: 4d);
        Assert.InRange(step.HeadingDegrees, 60f, 90f);

        // Without lookahead the aim is straight north.
        Assert.Equal(0f, follower.Advance(At(0d, -2d / 240d, heading: 0f), 0d, 1d, 45f).HeadingDegrees, 0.01f);
    }

    [Fact]
    public void PointsAlreadyReachedOrOnTheLineToTheNextAreSkippedAfterAnAdvance()
    {
        var route = new Route
        {
            Waypoints =
            [
                new Waypoint(WaypointKind.Point, 0d, 0d),
                new Waypoint(WaypointKind.Point, 0.001d, 0d),   // 0.24 m on: inside arrival
                new Waypoint(WaypointKind.Point, 0.02d, 0.001d), // nearly on the line to the next
                new Waypoint(WaypointKind.Point, 0.1d, 0d),
                new Waypoint(WaypointKind.Point, 0.1d, 0.1d),  // a corner: kept
            ],
        };
        var follower = new RouteFollower(route, RouteMode.Once);
        follower.Reset();

        // Reaching the first point advances, and the skip runs on the next look.
        Assert.Equal(NavigationAction.Hold, follower.Advance(At(0d, 0d), 0d, 1d, 45f).Action);
        Assert.Equal(1, follower.CurrentIndex);
        NavigationStep step = follower.Advance(At(0d, 0d), 0.1d, 1d, 45f);
        Assert.Equal(3, follower.CurrentIndex);
        Assert.NotEqual(NavigationAction.Hold, step.Action);
    }

    [Fact]
    public void StuckDetectorEscalatesOnlyWithoutProgress()
    {
        var detector = new StuckDetector();

        Assert.Null(detector.Observe(At(0d, 0d), 0d));
        Assert.Null(detector.Observe(At(0d, 0d), 1d));
        Assert.Equal(StuckRecovery.BackUp, detector.Observe(At(0d, 0d), 3.5d));
        Assert.Equal(StuckRecovery.StrafeLeft, detector.Observe(At(0d, 0d), 7d));

        // Real movement resets the escalation.
        Assert.Null(detector.Observe(At(0.01d, 0d), 8d));
        Assert.Equal(0, detector.Escalation);
    }

    [Fact]
    public void NavigationBehaviorWalksAndReleasesMovementWhenInterrupted()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new NavigationSettings();
        var behavior = new NavigationBehavior(() => settings);
        behavior.SetRoute(new Route { Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0.1d)] });
        surface.Position = At(0d, 0d, heading: 180f);

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        Assert.True(behavior.WantsControl(Context().Board, out string reason));
        Assert.Equal("waypoint 1/1", reason);

        behavior.Execute(Context());
        Assert.Equal(["move:turnright"], surface.Commands);
        surface.Position = surface.Position with { HeadingDegrees = 0f };

        // The fake turns instantly, so the next step walks.
        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]);

        behavior.Interrupt(Context());
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.Null(surface.Intent);
    }

    [Fact]
    public void NavigationBehaviorIsQuietWithoutARouteOrInPortalSpace()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var behavior = new NavigationBehavior(() => new NavigationSettings());

        Assert.False(behavior.WantsControl(Blackboard.Capture(surface, clock, 25f, 15f), out _));

        behavior.SetRoute(new Route { Waypoints = [new Waypoint(WaypointKind.Point, 0d, 0.1d)] });
        surface.NavigationAvailable = false;
        Assert.False(behavior.WantsControl(Blackboard.Capture(surface, clock, 25f, 15f), out _));
    }

    [Fact]
    public void RouteRoundTripsThroughJson()
    {
        var route = new Route
        {
            Name = "loop",
            Waypoints = [new Waypoint(WaypointKind.Point, 12.5d, -3.25d) { Elevation = 0.1d }, new Waypoint(WaypointKind.Pause, 0d, 0d) { Seconds = 2d }],
        };

        Route restored = Route.FromJson(route.ToJson());

        Assert.Equal(route, restored with { Waypoints = route.Waypoints });
        Assert.Equal(2, restored.Waypoints.Count);
        Assert.Equal(route.Waypoints[0], restored.Waypoints[0]);
        Assert.Equal(route.Waypoints[1], restored.Waypoints[1]);
    }
}
