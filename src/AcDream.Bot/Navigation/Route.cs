using System.Text.Json;
using AcDream.Bot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Navigation;

public enum RouteMode
{
    /// <summary>After the last waypoint, continue from the first.</summary>
    Loop,
    /// <summary>After the last waypoint, walk the route backwards.</summary>
    PingPong,
    /// <summary>Stop at the last waypoint.</summary>
    Once,
}

public enum WaypointKind
{
    /// <summary>Walk to the coordinate.</summary>
    Point = 0,
    /// <summary>Stand still for <see cref="Waypoint.Seconds"/>.</summary>
    Pause,
}

/// <summary>
/// One route step in map coordinates (the same east-west / north-south the
/// game prints for <c>/loc</c>, so a waypoint can be typed by hand).
/// </summary>
public sealed record Waypoint(
    WaypointKind Kind,
    double EastWest,
    double NorthSouth)
{
    public double Elevation { get; init; }

    public double Seconds { get; init; }

    public static Waypoint At(in PluginNavigationPosition position) =>
        new(WaypointKind.Point, position.EastWest, position.NorthSouth)
        {
            Elevation = position.Elevation,
        };

    public PluginNavigationPosition ToPosition() =>
        new(0u, EastWest, NorthSouth, Elevation, 0f, true);
}

public sealed record Route
{
    public string Name { get; init; } = "unnamed";

    public IReadOnlyList<Waypoint> Waypoints { get; init; } = [];

    public bool IsEmpty => Waypoints.Count == 0;

    public Route Append(Waypoint waypoint) =>
        this with { Waypoints = [.. Waypoints, waypoint] };

    public string ToJson() => JsonSerializer.Serialize(this, BotProfile.JsonOptions);

    public static Route FromJson(string json) =>
        JsonSerializer.Deserialize<Route>(json, BotProfile.JsonOptions)
            ?? throw new JsonException("route is empty");
}
