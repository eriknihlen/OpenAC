using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A wall seen from above: one straight line in landblock-local metres. See
/// <see cref="PluginDungeonFloorplan"/> for the frame.
/// </summary>
/// <param name="Start">One end of the wall.</param>
/// <param name="End">The other end.</param>
public readonly record struct PluginDungeonWall(Vector2 Start, Vector2 End);

/// <summary>
/// One cell of a floorplan: where it is and which layer it was drawn in.
/// </summary>
/// <param name="CellId">The full cell id, landblock in the high half.</param>
/// <param name="Center">The middle of the cell's geometry, in landblock-local metres.</param>
/// <param name="LayerZ">The <see cref="PluginDungeonLayer.Z"/> of the layer the cell is drawn in.</param>
public readonly record struct PluginDungeonCell(uint CellId, Vector3 Center, float LayerZ);

/// <summary>
/// One indoor cell exactly as the game data authors it: which environment
/// piece it is built from and how that piece is placed in the landblock. It
/// is the raw placement, not a drawing: a plugin that draws dungeons from
/// its own tiles, one per environment piece, places each tile with this.
/// </summary>
/// <param name="CellId">The full cell id, landblock in the high half.</param>
/// <param name="EnvironmentId">
/// The environment piece the cell is built from, as the cell stores it: the
/// low sixteen bits of the environment file's id. Zero for a cell that names
/// none.
/// </param>
/// <param name="CellStructure">
/// Which of that environment piece's structures the cell uses.
/// </param>
/// <param name="Origin">
/// Where the piece's own origin sits, in landblock-local metres (x east and
/// y north from the landblock's south-west corner, z up), exactly as stored.
/// </param>
/// <param name="Orientation">
/// How the piece is turned, exactly as stored. The data stores the four
/// components in the order W, X, Y, Z; a piece turned only about the
/// vertical axis by an angle a has W = cos(a/2) and Z = sin(a/2), with a
/// positive a turning east towards north. Identity for a cell that stores no
/// placement.
/// </param>
/// <param name="SeesOutside">
/// True for a cell that sees the landscape, as a building's rooms do; false
/// for a sealed dungeon's cell, the same answer
/// <see cref="IDungeonMapAutomation.IsSealedDungeon"/> gives in reverse.
/// </param>
public readonly record struct PluginIndoorCell(
    uint CellId,
    uint EnvironmentId,
    uint CellStructure,
    Vector3 Origin,
    Quaternion Orientation,
    bool SeesOutside)
{
    /// <summary>
    /// The turn about the vertical axis <see cref="Orientation"/> describes,
    /// in degrees from 0 up to 360, counted from east towards north. For the
    /// quarter turns dungeon pieces are laid at this is 0, 90, 180 or 270 to
    /// within rounding; a piece also tilted about another axis reads only
    /// its turn about the vertical.
    /// </summary>
    public float YawDegrees
    {
        get
        {
            Quaternion q = Orientation;
            double yaw = Math.Atan2(
                2d * ((q.W * q.Z) + (q.X * q.Y)),
                1d - (2d * ((q.Y * q.Y) + (q.Z * q.Z))));
            double degrees = yaw * (180d / Math.PI);
            if (degrees < 0d)
                degrees += 360d;
            return degrees >= 360d ? 0f : (float)degrees;
        }
    }
}

/// <summary>
/// One storey of a floorplan: the cells whose floor lies in one six-metre
/// band of height, flattened together. A cell with no floor of its own, such
/// as the upper cell of a tall room, is drawn in the band of the cell it
/// opens onto below, however many floorless cells sit between; a cell with
/// no floor and no way down is drawn in the band its origin is in.
/// </summary>
/// <param name="Z">
/// The lowest height the band covers, in metres; bands are six metres tall
/// and shifted down three, so a floor at height z is in the band
/// floor((z + 3) / 6) * 6.
/// </param>
/// <param name="Floors">
/// The floor area as closed polygons, each vertex in landblock-local metres,
/// in the order the data gives them. The polygons overlap wherever two cells
/// meet; fill each one and the room comes out whole.
/// </param>
/// <param name="Walls">The walls as lines, collinear runs already joined.</param>
public sealed record PluginDungeonLayer(
    float Z,
    IReadOnlyList<IReadOnlyList<Vector2>> Floors,
    IReadOnlyList<PluginDungeonWall> Walls);

/// <summary>
/// One landblock's indoor cells seen from above, worked out from the cell
/// geometry the game data carries: level faces are floor, standing faces
/// are walls, and the doorways between cells are left open. Everything is
/// in the landblock's own frame and in metres: x grows east and y grows
/// north from the landblock's south-west corner, the frame the game's own
/// cell positions use. The plan is built once and never changed, so it is
/// safe to keep and to read from any thread.
/// </summary>
/// <param name="LandblockId">The landblock the plan is of, with a zero low half.</param>
/// <param name="Layers">The storeys, lowest first.</param>
/// <param name="Cells">Every cell with geometry, in cell id order.</param>
/// <param name="BoundsMin">The lowest corner of the box around every cell's geometry, in landblock-local metres.</param>
/// <param name="BoundsMax">The highest corner of that box.</param>
public sealed record PluginDungeonFloorplan(
    uint LandblockId,
    IReadOnlyList<PluginDungeonLayer> Layers,
    IReadOnlyList<PluginDungeonCell> Cells,
    Vector3 BoundsMin,
    Vector3 BoundsMax)
{
    /// <summary>
    /// The plan of nothing: what comes back for a landblock the host does
    /// not know, a landblock with no indoor cells, or a host that cannot
    /// read the game data.
    /// </summary>
    public static PluginDungeonFloorplan Empty { get; } =
        new(0u, Array.Empty<PluginDungeonLayer>(), Array.Empty<PluginDungeonCell>(), Vector3.Zero, Vector3.Zero);

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Layers.Count == 0;

    /// <summary>
    /// A position in the plan's frame: metres east and north of the
    /// south-west corner of the position's own landblock, and metres up.
    /// Compare its landblock (<see cref="PluginNavigationPosition.CellId"/>
    /// with a zero low half) with <see cref="LandblockId"/> before drawing
    /// it on this plan.
    /// </summary>
    /// <param name="position">A position as the navigation surface reports it.</param>
    public static Vector3 ToLandblockLocal(in PluginNavigationPosition position)
    {
        uint blockX = (position.CellId >> 24) & 0xFFu;
        uint blockY = (position.CellId >> 16) & 0xFFu;
        return new Vector3(
            (float)((position.EastWest * 240d) + 84d - (((double)blockX - 127d) * 192d)),
            (float)((position.NorthSouth * 240d) + 84d - (((double)blockY - 127d) * 192d)),
            (float)(position.Elevation * 240d));
    }
}

/// <summary>
/// The shape of the dungeon the character is in, for a plugin drawing a map
/// of it. The plan is data, not a picture: the plugin draws it however it
/// likes, on whatever it has to draw with.
/// </summary>
public interface IDungeonMapAutomation
{
    /// <summary>
    /// Whether <paramref name="cellId"/> is an indoor cell that sees nothing
    /// outside, as a dungeon's cells are; false for outdoor cells, for the
    /// interiors of buildings that open onto the landscape, for cells the
    /// data lacks, and on a host that cannot read the game data, which is
    /// what the default implementation always reports.
    /// </summary>
    /// <param name="cellId">The full cell id, landblock in the high half.</param>
    bool IsSealedDungeon(uint cellId) => false;

    /// <summary>
    /// The landblock the character stands in, with a zero low half, or zero
    /// when the character has no position yet, which is also what the
    /// default implementation always reports.
    /// </summary>
    uint CurrentLandblockId => 0u;

    /// <summary>
    /// The floorplan of a landblock's indoor cells, built from the game data
    /// the first time it is asked for and the same plan every time after.
    /// Any cell id in the landblock will do. Reading the data can take a few
    /// milliseconds on a large dungeon the first time; the plan is worth
    /// keeping. <see cref="PluginDungeonFloorplan.Empty"/> comes back for a
    /// landblock the data does not have, for one with no indoor cells, and
    /// on a host that cannot read the game data, which is what the default
    /// implementation always returns.
    /// </summary>
    /// <param name="landblockId">The landblock, or any cell in it.</param>
    PluginDungeonFloorplan CaptureFloorplan(uint landblockId) => PluginDungeonFloorplan.Empty;

    /// <summary>
    /// Every indoor cell of a landblock as the game data authors it -- the
    /// environment piece each is built from and how it is placed -- in cell
    /// id order, read from the game data the first time it is asked for and
    /// the same list every time after. Unlike the floorplan this is the raw
    /// placement of every cell the landblock lists, geometry or not. Empty
    /// for a landblock the data does not have, one with no indoor cells, and
    /// on a host that cannot read the game data, which is what the default
    /// implementation always returns.
    /// </summary>
    /// <param name="landblockId">The landblock, or any cell in it.</param>
    IReadOnlyList<PluginIndoorCell> CaptureIndoorCells(uint landblockId) =>
        Array.Empty<PluginIndoorCell>();
}
