using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Rendering.Wb;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering.Wb;

public sealed record EnvCellShellPlacement(
    uint CellId,
    ulong GeometryId,
    uint EnvironmentId,
    ushort CellStructure,
    ImmutableArray<ushort> Surfaces,
    Vector3 WorldPosition,
    Quaternion Rotation,
    Matrix4x4 Transform,
    WbBoundingBox LocalBounds,
    WbBoundingBox WorldBounds);

public sealed class EnvCellLandblockBuild
{
    public EnvCellLandblockBuild(
        uint landblockId,
        IEnumerable<LoadedCell> visibilityCells,
        IEnumerable<EnvCellShellPlacement> shells,
        IEnumerable<WalkBuildingFactory.Entry>? walkBuildings = null,
        float walkMaxZ = 0f,
        float walkMinZ = 0f)
    {
        LandblockId = landblockId;
        VisibilityCells = visibilityCells.ToImmutableArray();
        Shells = shells.ToImmutableArray();
        WalkBuildings = (walkBuildings ?? Enumerable.Empty<WalkBuildingFactory.Entry>()).ToImmutableArray();
        WalkBuildingMeshDependencies = CollectWalkBuildingMeshDependencies(WalkBuildings);
        WalkMaxZ = walkMaxZ;
        WalkMinZ = walkMinZ;

        uint expectedPrefix = landblockId & 0xFFFF0000u;
        if (VisibilityCells.Any(cell => (cell.CellId & 0xFFFF0000u) != expectedPrefix))
            throw new ArgumentException("A visibility cell belongs to a different landblock.", nameof(visibilityCells));
        if (Shells.Any(shell => (shell.CellId & 0xFFFF0000u) != expectedPrefix))
            throw new ArgumentException("A render shell belongs to a different landblock.", nameof(shells));
        if (WalkBuildings.Any(entry => (entry.Building.PositionCellId & 0xFFFF0000u) != expectedPrefix))
            throw new ArgumentException("A walk building belongs to a different landblock.", nameof(walkBuildings));
    }

    public uint LandblockId { get; }
    public ImmutableArray<LoadedCell> VisibilityCells { get; }
    public ImmutableArray<EnvCellShellPlacement> Shells { get; }

    public ImmutableArray<WalkBuildingFactory.Entry> WalkBuildings { get; }

    public ImmutableArray<ulong> WalkBuildingMeshDependencies { get; }

    public float WalkMaxZ { get; }

    public float WalkMinZ { get; }

    private static ImmutableArray<ulong> CollectWalkBuildingMeshDependencies(
        ImmutableArray<WalkBuildingFactory.Entry> buildings)
    {
        var seen = new HashSet<uint>();
        var dependencies = ImmutableArray.CreateBuilder<ulong>();
        for (int buildingIndex = 0; buildingIndex < buildings.Length; buildingIndex++)
        {
            WalkBuildingDegradeLevel[] levels = buildings[buildingIndex].Building.DegradeLevels;
            for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
            {
                uint id = levels[levelIndex].GfxObjId;
                if (id != 0 && seen.Add(id))
                    dependencies.Add(id);
            }
        }
        return dependencies.ToImmutable();
    }
}

public sealed class EnvCellLandblockBuildBuilder
{
    private readonly uint _landblockId;
    private readonly List<LoadedCell> _visibilityCells = new();
    private readonly List<EnvCellShellPlacement> _shells = new();
    private readonly List<WalkBuildingFactory.Entry> _walkBuildings = new();
    private float _walkMaxZ;
    private float _walkMinZ;
    private bool _built;

    public EnvCellLandblockBuildBuilder(uint landblockId)
    {
        _landblockId = landblockId;
    }

    public void AddWalkBuildings(IEnumerable<WalkBuildingFactory.Entry> entries)
    {
        if (_built)
            throw new InvalidOperationException("This landblock cell build is already complete.");
        _walkBuildings.AddRange(entries);
    }

    public void SetWalkZSlab(float maxZ, float minZ)
    {
        if (_built)
            throw new InvalidOperationException("This landblock cell build is already complete.");
        _walkMaxZ = maxZ;
        _walkMinZ = minZ;
    }

    public void AddCell(
        uint envCellId,
        EnvCell envCell,
        CellStruct cellStruct,
        Vector3 physicsCellOrigin,
        Matrix4x4 physicsCellTransform,
        Vector3 shellWorldPosition,
        Matrix4x4 shellTransform,
        bool hasDrawableGeometry)
    {
        if (_built)
            throw new InvalidOperationException("This landblock cell build is already complete.");

        var localBounds = ComputeLocalBounds(cellStruct);
        _visibilityCells.Add(BuildVisibilityCell(
            envCellId,
            envCell,
            cellStruct,
            physicsCellOrigin,
            physicsCellTransform,
            localBounds));

        if (!hasDrawableGeometry)
            return;

        ulong geometryId = ComputeGeometryId(
            envCell.EnvironmentId,
            envCell.CellStructure,
            envCell.Surfaces);
        _shells.Add(new EnvCellShellPlacement(
            envCellId,
            geometryId,
            envCell.EnvironmentId,
            envCell.CellStructure,
            envCell.Surfaces.ToImmutableArray(),
            shellWorldPosition,
            envCell.Position.Orientation,
            shellTransform,
            localBounds,
            TransformBoundingBox(localBounds, shellTransform)));
    }

    public EnvCellLandblockBuild Build()
    {
        if (_built)
            throw new InvalidOperationException("This landblock cell build is already complete.");
        _built = true;
        return new EnvCellLandblockBuild(
            _landblockId, _visibilityCells, _shells, _walkBuildings, _walkMaxZ, _walkMinZ);
    }

    public static ulong ComputeGeometryId(
        uint environmentId,
        ushort cellStructure,
        IReadOnlyList<ushort> surfaces)
        => EnvCellGeometryIdentity.Compute(environmentId, cellStructure, surfaces);

    private static WbBoundingBox ComputeLocalBounds(CellStruct cellStruct)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in cellStruct.VertexArray.Vertices.Values)
        {
            var position = new Vector3(vertex.Origin.X, vertex.Origin.Y, vertex.Origin.Z);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return min.X == float.MaxValue
            ? new WbBoundingBox(Vector3.Zero, Vector3.Zero)
            : new WbBoundingBox(min, max);
    }

    private static WbBoundingBox TransformBoundingBox(WbBoundingBox box, Matrix4x4 transform)
    {
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new(box.Min.X, box.Min.Y, box.Min.Z),
            new(box.Max.X, box.Min.Y, box.Min.Z),
            new(box.Min.X, box.Max.Y, box.Min.Z),
            new(box.Max.X, box.Max.Y, box.Min.Z),
            new(box.Min.X, box.Min.Y, box.Max.Z),
            new(box.Max.X, box.Min.Y, box.Max.Z),
            new(box.Min.X, box.Max.Y, box.Max.Z),
            new(box.Max.X, box.Max.Y, box.Max.Z),
        };

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var corner in corners)
        {
            var transformed = Vector3.Transform(corner, transform);
            min = Vector3.Min(min, transformed);
            max = Vector3.Max(max, transformed);
        }

        return new WbBoundingBox(min, max);
    }

    private static LoadedCell BuildVisibilityCell(
        uint envCellId,
        EnvCell envCell,
        CellStruct cellStruct,
        Vector3 cellOrigin,
        Matrix4x4 cellTransform,
        WbBoundingBox localBounds)
    {
        Matrix4x4.Invert(cellTransform, out var inverse);

        var portals = new List<CellPortalInfo>();
        var clipPlanes = new List<PortalClipPlane>();
        var portalPolygons = new List<Vector3[]>();

        foreach (var portal in envCell.CellPortals)
        {
            portals.Add(new CellPortalInfo(
                portal.OtherCellId,
                portal.PolygonId,
                (ushort)portal.Flags,
                portal.OtherPortalId));

            if (cellStruct.Polygons.TryGetValue(portal.PolygonId, out var poly)
                && poly.VertexIds.Count >= 3)
            {
                Vector3 p0 = Vector3.Zero, p1 = Vector3.Zero, p2 = Vector3.Zero;
                bool found = true;
                if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[0], out var v0))
                    p0 = new Vector3(v0.Origin.X, v0.Origin.Y, v0.Origin.Z);
                else
                    found = false;
                if (found && cellStruct.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[1], out var v1))
                    p1 = new Vector3(v1.Origin.X, v1.Origin.Y, v1.Origin.Z);
                else
                    found = false;
                if (found && cellStruct.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[2], out var v2))
                    p2 = new Vector3(v2.Origin.X, v2.Origin.Y, v2.Origin.Z);
                else
                    found = false;

                if (found)
                {
                    var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                    float d = -Vector3.Dot(normal, p0);
                    int insideSide = ((ushort)portal.Flags & 0x2) == 0 ? 1 : 0;
                    clipPlanes.Add(new PortalClipPlane
                    {
                        Normal = normal,
                        D = d,
                        InsideSide = insideSide,
                    });
                }
                else
                {
                    clipPlanes.Add(default);
                }
            }
            else
            {
                clipPlanes.Add(default);
            }

            Vector3[] polygonVertices = Array.Empty<Vector3>();
            if (cellStruct.Polygons.TryGetValue(portal.PolygonId, out var portalPolygon)
                && portalPolygon.VertexIds.Count >= 3)
            {
                polygonVertices = new Vector3[portalPolygon.VertexIds.Count];
                bool allResolved = true;
                for (int vertexIndex = 0; vertexIndex < portalPolygon.VertexIds.Count; vertexIndex++)
                {
                    if (cellStruct.VertexArray.Vertices.TryGetValue(
                        (ushort)portalPolygon.VertexIds[vertexIndex], out var vertex))
                    {
                        polygonVertices[vertexIndex] = new Vector3(
                            vertex.Origin.X,
                            vertex.Origin.Y,
                            vertex.Origin.Z);
                    }
                    else
                    {
                        allResolved = false;
                        break;
                    }
                }

                if (!allResolved)
                    polygonVertices = Array.Empty<Vector3>();
            }
            portalPolygons.Add(polygonVertices);
        }

        uint landblockPrefix = envCellId & 0xFFFF0000u;
        var visibleCells = new List<uint>();
        if (envCell.VisibleCells is not null)
        {
            foreach (var lowId in envCell.VisibleCells)
                visibleCells.Add(landblockPrefix | lowId);
        }

        return new LoadedCell
        {
            CellId = envCellId,
            WorldPosition = cellOrigin,
            WorldTransform = cellTransform,
            InverseWorldTransform = inverse,
            LocalBoundsMin = localBounds.Min,
            LocalBoundsMax = localBounds.Max,
            Portals = portals,
            ClipPlanes = clipPlanes,
            PortalPolygons = portalPolygons,
            VisibleCells = visibleCells,
            SeenOutside = envCell.Flags.HasFlag(EnvCellFlags.SeenOutside),
            Walk = WalkCellFactory.FromParsed(envCellId, envCell, cellStruct, cellTransform, inverse),
        };
    }
}
