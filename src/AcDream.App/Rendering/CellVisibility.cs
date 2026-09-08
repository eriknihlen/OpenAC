
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Rendering;

// ---------------------------------------------------------------------------
// Data structures
// ---------------------------------------------------------------------------

public sealed class LoadedCell
{
    public uint CellId;

    public Vector3 WorldPosition;

    public Matrix4x4 WorldTransform;

    public Matrix4x4 InverseWorldTransform;

    public Vector3 LocalBoundsMin;

    public Vector3 LocalBoundsMax;

    public List<CellPortalInfo> Portals = new();

    public List<PortalClipPlane> ClipPlanes = new();

    public List<Vector3[]> PortalPolygons = new();

    public uint? BuildingId { get; internal set; }

    public IReadOnlyList<uint> VisibleCells = System.Array.Empty<uint>();

    public bool SeenOutside;

    public bool IsOutdoorNode;

    public WalkCell? Walk { get; internal set; }
}

public readonly record struct CellPortalInfo(
    ushort OtherCellId, ushort PolygonId, ushort Flags, ushort OtherPortalId);

public struct PortalClipPlane
{
    public Vector3 Normal;

    /// <summary>Plane offset so that Dot(Normal, point) + D = 0 on the plane.</summary>
    public float D;

    public int InsideSide;
}

public enum CameraCellResolution
{
    None,
    Cache,
    Neighbour,
    BruteForce,
    Grace,
}

public sealed class CellVisibility
{

    private const float PointInCellEpsilon = 0.01f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private readonly Dictionary<uint, List<LoadedCell>> _cellsByLandblock = new();

    /// <summary>Full-ID lookup used by the production frame walk.</summary>
    private readonly Dictionary<uint, LoadedCell> _cellLookup = new();

    public CameraCellResolution LastCameraCellResolution { get; private set; } = CameraCellResolution.None;

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    public void AddCell(LoadedCell cell)
    {
        uint lbId = cell.CellId >> 16;

        if (!_cellsByLandblock.TryGetValue(lbId, out var list))
        {
            list = new List<LoadedCell>();
            _cellsByLandblock[lbId] = list;
        }

        list.Add(cell);
        _cellLookup[cell.CellId] = cell;
    }

    public void CommitLandblock(uint landblockId, IReadOnlyList<LoadedCell> cells)
    {
        uint prefix = landblockId >> 16;
        if (cells.Any(cell => (cell.CellId >> 16) != prefix))
            throw new ArgumentException(
                "A visibility cell belongs to a different landblock.",
                nameof(cells));

        if (_cellsByLandblock.TryGetValue(prefix, out var previous))
        {
            foreach (var cell in previous)
                _cellLookup.Remove(cell.CellId);
        }

        var committed = new List<LoadedCell>(cells);
        _cellsByLandblock[prefix] = committed;
        foreach (var cell in committed)
            _cellLookup[cell.CellId] = cell;
    }

    public IReadOnlyList<LoadedCell> GetCellsForLandblock(uint lbId)
    {
        return _cellsByLandblock.TryGetValue(lbId, out var list)
            ? list
            : System.Array.Empty<LoadedCell>();
    }

    public bool TryGetCell(uint cellId, out LoadedCell? cell)
        => _cellLookup.TryGetValue(cellId, out cell);

    public void RemoveLandblock(uint lbId)
    {
        if (!_cellsByLandblock.TryGetValue(lbId, out var list))
            return;

        foreach (var cell in list)
        {
            _cellLookup.Remove(cell.CellId);
        }

        _cellsByLandblock.Remove(lbId);
    }

    // ------------------------------------------------------------------
    // PointInCell
    // ------------------------------------------------------------------

    public static bool PointInCell(Vector3 worldPoint, LoadedCell cell)
    {
        if (cell.LocalBoundsMin.X >= cell.LocalBoundsMax.X)
            return false;

        var local = Vector3.Transform(worldPoint, cell.InverseWorldTransform);

        return local.X >= cell.LocalBoundsMin.X - PointInCellEpsilon &&
               local.X <= cell.LocalBoundsMax.X + PointInCellEpsilon &&
               local.Y >= cell.LocalBoundsMin.Y - PointInCellEpsilon &&
               local.Y <= cell.LocalBoundsMax.Y + PointInCellEpsilon &&
               local.Z >= cell.LocalBoundsMin.Z - PointInCellEpsilon &&
               local.Z <= cell.LocalBoundsMax.Z + PointInCellEpsilon;
    }

    public bool IsInsideAnyCell(Vector3 worldPoint)
    {
        foreach (var cell in _cellLookup.Values)
            if (PointInCell(worldPoint, cell)) return true;
        return false;
    }

}
