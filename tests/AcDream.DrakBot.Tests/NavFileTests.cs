using AcDream.DrakBot.Navigation;

namespace AcDream.DrakBot.Tests;

public sealed class NavFileTests
{
    private static string[] Lines(params string[] lines) => lines;

    [Fact]
    public void ReadsEveryPointKindAndTheRouteMode()
    {
        string[] lines = Lines(
            "uTank2 NAV 1.2",
            "1", // circular
            "6",
            "0", "0.1", "0.2", "5", "0",                       // point
            "3", "0", "0", "0", "0", "2500",                   // pause 2.5 s
            "4", "0", "0", "0", "0", "/say hi",                // chat
            "2", "0", "0", "0", "0", "48",                     // recall spell 48
            "6", "0.1", "0.2", "5", "0", "Portal to Town Network", "14", "False", "0.11", "0.21", "5.5", // portal
            "7", "0.3", "0.4", "0", "0", "Ulgrim", "37", "True", "0.31", "0.41", "0"); // npc

        Route route = NavFile.Parse("holtburg", lines, out string? warning);

        Assert.Null(warning);
        Assert.Equal("holtburg", route.Name);
        Assert.Equal(RouteMode.Loop, route.Mode);
        Assert.Equal(6, route.Waypoints.Count);
        Assert.Equal(new Waypoint(WaypointKind.Point, 0.1d, 0.2d) { Elevation = 5d }, route.Waypoints[0]);
        Assert.Equal(2.5d, route.Waypoints[1].Seconds);
        Assert.Equal("/say hi", route.Waypoints[2].Text);
        Assert.Equal(48u, route.Waypoints[3].SpellId);
        Waypoint portal = route.Waypoints[4];
        Assert.Equal(WaypointKind.Portal, portal.Kind);
        Assert.Equal("Portal to Town Network", portal.TargetName);
        Assert.Equal(0.11d, portal.TargetEastWest);
        Assert.Equal(0.21d, portal.TargetNorthSouth);
        Assert.True(portal.HasTargetPosition);
        Assert.Equal(WaypointKind.Npc, route.Waypoints[5].Kind);
        Assert.Equal("Ulgrim", route.Waypoints[5].TargetName);
    }

    [Fact]
    public void RoundTripsThroughWrite()
    {
        var route = new Route
        {
            Name = "loop",
            Mode = RouteMode.PingPong,
            Waypoints =
            [
                new Waypoint(WaypointKind.Point, -0.125d, 33.5d) { Elevation = 0.25d },
                new Waypoint(WaypointKind.Pause, 0d, 0d) { Seconds = 1.5d },
                new Waypoint(WaypointKind.Chat, 0d, 0d) { Text = "/vt jump" },
                new Waypoint(WaypointKind.Recall, 0d, 0d) { SpellId = 2645u },
                new Waypoint(WaypointKind.Portal, 1d, 2d) { TargetName = "Portal", TargetEastWest = 1.5d, TargetNorthSouth = 2.5d, TargetElevation = 3d },
                new Waypoint(WaypointKind.Vendor, 1d, 2d) { TargetName = "Shopkeep", VendorId = 0x8000_0001u },
            ],
        };

        string text = NavFile.Write(route);
        Route back = NavFile.Parse("loop", text.Split('\n'), out string? warning);

        Assert.Null(warning);
        Assert.Equal(route.Mode, back.Mode);
        Assert.Equal(route.Waypoints, back.Waypoints);
        Assert.StartsWith("uTank2 NAV 1.2\n2\n6\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKindStopsTheRouteWithWhatWasReadAndAWarning()
    {
        string[] lines = Lines(
            "uTank2 NAV 1.2",
            "4",
            "3",
            "0", "0.1", "0.2", "0", "0",
            "9", "0", "0", "0", "0",
            "0", "0.3", "0.4", "0", "0");

        Route route = NavFile.Parse("odd", lines, out string? warning);

        Assert.Equal(RouteMode.Once, route.Mode);
        Assert.Single(route.Waypoints);
        Assert.NotNull(warning);
        Assert.Contains("unknown waypoint type 9", warning);
    }

    [Fact]
    public void ANonNavFileIsAnEmptyRouteWithAWarning()
    {
        Route route = NavFile.Parse("x", Lines("{ \"name\": \"json\" }"), out string? warning);
        Assert.True(route.IsEmpty);
        Assert.Equal("not a uTank2 NAV 1.2 file", warning);
    }

    [Fact]
    public void RouteJsonCarriesTheNewFieldsAndMode()
    {
        var route = new Route
        {
            Name = "r",
            Mode = RouteMode.Once,
            Waypoints = [new Waypoint(WaypointKind.Portal, 1d, 2d) { TargetName = "P", TargetEastWest = 1.1d }],
        };
        Route back = Route.FromJson(route.ToJson());
        Assert.Equal(route.Mode, back.Mode);
        Assert.Equal(route.Waypoints, back.Waypoints);
    }
}
