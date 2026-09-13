using System.Text.Json;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

public enum RouteMode
{
    /// <summary>After the last waypoint, continue from the first.</summary>
    Loop,
    /// <summary>After the last waypoint, walk the route backwards.</summary>
    PingPong,
    /// <summary>Stop at the last waypoint.</summary>
    Once,
}

/// <summary>
/// The step kinds a VTank-style route carries. The numbering is the one the
/// <c>.nav</c> file uses, so <see cref="NavFile"/> and JSON agree on it.
/// </summary>
public enum WaypointKind
{
    /// <summary>Walk to the coordinate.</summary>
    Point = 0,
    /// <summary>Cast <see cref="Waypoint.SpellId"/> on yourself and wait for the teleport.</summary>
    Recall = 2,
    /// <summary>Stand still for <see cref="Waypoint.Seconds"/>.</summary>
    Pause = 3,
    /// <summary>Send <see cref="Waypoint.Text"/> to chat (a slash command or a line to say).</summary>
    Chat = 4,
    /// <summary>Walk to the coordinate and use the vendor named <see cref="Waypoint.TargetName"/>.</summary>
    Vendor = 5,
    /// <summary>Use the portal named <see cref="Waypoint.TargetName"/> and wait for the teleport.</summary>
    Portal = 6,
    /// <summary>Walk to the coordinate and use the NPC named <see cref="Waypoint.TargetName"/>.</summary>
    Npc = 7,
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

    /// <summary><see cref="WaypointKind.Pause"/>: how long to stand.</summary>
    public double Seconds { get; init; }

    /// <summary><see cref="WaypointKind.Recall"/>: the spell to self-cast.</summary>
    public uint SpellId { get; init; }

    /// <summary><see cref="WaypointKind.Chat"/>: the line to submit.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Portal, NPC and vendor steps: the object's name as the game shows it.</summary>
    public string TargetName { get; init; } = string.Empty;

    /// <summary>
    /// Portal, NPC and vendor steps: where the object stood when the route
    /// was recorded, used to pick between same-named objects. Zero when unknown.
    /// </summary>
    public double TargetEastWest { get; init; }

    public double TargetNorthSouth { get; init; }

    public double TargetElevation { get; init; }

    /// <summary>Vendor steps: the vendor's object id when it was recorded; zero when unknown.</summary>
    public uint VendorId { get; init; }

    /// <summary>True for the step kinds the walker travels to; false for the ones that act in place.</summary>
    public bool IsTravel => Kind is WaypointKind.Point or WaypointKind.Vendor or WaypointKind.Npc;

    public bool HasTargetPosition => TargetEastWest != 0d || TargetNorthSouth != 0d;

    public static Waypoint At(in PluginNavigationPosition position) =>
        new(WaypointKind.Point, position.EastWest, position.NorthSouth)
        {
            Elevation = position.Elevation,
        };

    public PluginNavigationPosition ToPosition() =>
        new(0u, EastWest, NorthSouth, Elevation, 0f, true);

    public PluginNavigationPosition TargetPosition() =>
        new(0u, TargetEastWest, TargetNorthSouth, TargetElevation, 0f, true);

    public override string ToString() => Kind switch
    {
        WaypointKind.Point => $"point {NorthSouth:0.000}, {EastWest:0.000}",
        WaypointKind.Recall => $"recall spell {SpellId}",
        WaypointKind.Pause => $"pause {Seconds:0.#}s",
        WaypointKind.Chat => $"chat {Text}",
        WaypointKind.Vendor => $"vendor {TargetName}",
        WaypointKind.Portal => $"portal {TargetName}",
        WaypointKind.Npc => $"npc {TargetName}",
        _ => Kind.ToString(),
    };
}

public sealed record Route
{
    public string Name { get; init; } = "unnamed";

    /// <summary>The route's own mode; null follows the profile's navigation mode.</summary>
    public RouteMode? Mode { get; init; }

    public IReadOnlyList<Waypoint> Waypoints { get; init; } = [];

    public bool IsEmpty => Waypoints.Count == 0;

    public Route Append(Waypoint waypoint) =>
        this with { Waypoints = [.. Waypoints, waypoint] };

    public string ToJson() => JsonSerializer.Serialize(this, BotProfile.JsonOptions);

    public static Route FromJson(string json) =>
        JsonSerializer.Deserialize<Route>(json, BotProfile.JsonOptions)
            ?? throw new JsonException("route is empty");
}
