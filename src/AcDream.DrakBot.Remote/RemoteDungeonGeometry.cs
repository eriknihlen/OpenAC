using System.Numerics;

namespace AcDream.DrakBot.Remote;

/// <summary>What a dungeon polygon is to the floor plan.</summary>
public enum RemoteDungeonSurface
{
    /// <summary>Level ground: painted light.</summary>
    Floor,
    /// <summary>A slope you walk: painted a shade darker than a floor.</summary>
    Ramp,
    /// <summary>Stands up: drawn as its outline, dark.</summary>
    Wall,
}

/// <summary>
/// One polygon of a dungeon in the map frame: metres east and north, the
/// frame the status document's wx and wy are in (map units times 240),
/// and the metres up its vertices span.
/// </summary>
public sealed record RemoteDungeonPolygon(
    RemoteDungeonSurface Kind,
    IReadOnlyList<Vector2> Points,
    float ZMin,
    float ZMax);

/// <summary>
/// A dungeon's floors and walls as the host reads them from its data
/// files, in the map frame. The remote rasterizes it into floor plans;
/// what a floor plan cannot say (the bot's patrol, its hazards, where it
/// stands still) the dungeon document carries on top.
/// </summary>
public sealed record RemoteDungeonGeometry(uint LandblockId, IReadOnlyList<RemoteDungeonPolygon> Polygons);
