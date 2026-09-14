namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One room or corridor cell of a dungeon: where its centre is, in the map
/// coordinates the rest of the navigation contract uses, and which cells it
/// shares a doorway with. Adjacency is by doorway, not by line of sight, so
/// walking cell to cell along it never cuts through a wall.
/// </summary>
public readonly record struct PluginDungeonCell(
    uint CellId,
    double EastWest,
    double NorthSouth,
    double Elevation,
    IReadOnlyList<uint> Neighbors)
{
    public PluginNavigationPosition Position =>
        new(CellId, EastWest, NorthSouth, Elevation, 0f, IsOutdoor: false);

    /// <summary>
    /// The doorways out of this cell, where each opening actually is (its
    /// polygon's centre at floor level). Empty when the host knows only
    /// the adjacency, in which case a walk has to aim between cell origins.
    /// </summary>
    public IReadOnlyList<PluginDungeonDoorway> Doorways { get; init; } = Array.Empty<PluginDungeonDoorway>();
}

/// <summary>One opening between two cells, in map coordinates at floor level.</summary>
public readonly record struct PluginDungeonDoorway(
    uint OtherCellId,
    double EastWest,
    double NorthSouth,
    double Elevation)
{
    public PluginNavigationPosition Position =>
        new(0u, EastWest, NorthSouth, Elevation, 0f, IsOutdoor: false);
}

/// <summary>The loaded dungeon's cell graph, for a plugin that plans its own way through it.</summary>
public interface IDungeonAutomation
{
    bool IsAvailable => false;

    /// <summary>
    /// Every environment cell of a landblock the client has loaded (the id
    /// is the landblock's upper 16 bits, or a full cell id). Empty for an
    /// outdoor landblock or one not loaded.
    /// </summary>
    IReadOnlyList<PluginDungeonCell> CaptureCells(uint landblockId) =>
        Array.Empty<PluginDungeonCell>();
}
