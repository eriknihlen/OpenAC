using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public static class FlatCollisionAssetBuilder
{
    private static FlatPhysicsBsp EmptyPhysicsBsp() =>
        new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);

    public static FlatPhysicsBsp FlattenPhysicsBsp(
        PhysicsBSPNode? root,
        IReadOnlyDictionary<ushort, ResolvedPolygon> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        FlatPolygonTable polygonTable = FlattenPolygonTable(resolved);
        if (root is null)
        {
            return new FlatPhysicsBsp(
                -1,
                ImmutableArray<FlatPhysicsBspNode>.Empty,
                ImmutableArray<int>.Empty,
                polygonTable);
        }

        var nodes = new List<MutablePhysicsNode>();
        var polygonIndices = new List<int>();
        var visited = new Dictionary<PhysicsBSPNode, int>(
            ReferenceEqualityComparer<PhysicsBSPNode>.Instance);
        var stack = new Stack<PhysicsNodeFrame>();
        stack.Push(new PhysicsNodeFrame(root, -1, ChildEdge.Root));

        while (stack.Count != 0)
        {
            PhysicsNodeFrame frame = stack.Pop();
            FlatBspNodeContract.Validate(
                frame.Node.Type,
                frame.Node.PosNode is not null,
                frame.Node.NegNode is not null,
                "physics BSP source node");
            if (visited.ContainsKey(frame.Node))
            {
                throw new InvalidDataException(
                    "Physics BSP source contains a cycle or shared child.");
            }

            int nodeIndex = nodes.Count;
            visited.Add(frame.Node, nodeIndex);
            AttachChild(nodes, frame.ParentIndex, frame.Edge, nodeIndex);

            int polygonStart = polygonIndices.Count;
            if (frame.Node.Polygons is null)
            {
                throw new InvalidDataException(
                    $"Physics BSP node {nodeIndex} has no polygon-reference list.");
            }
            foreach (ushort polygonId in frame.Node.Polygons)
            {
                if (!polygonTable.TryFindPolygonIndex(polygonId, out int polygonIndex))
                {
                    throw new InvalidDataException(
                        $"Physics BSP leaf references missing polygon 0x{polygonId:X4}.");
                }
                polygonIndices.Add(polygonIndex);
            }

            Sphere boundingSphere = frame.Node.BoundingSphere
                ?? throw new InvalidDataException(
                    $"Physics BSP node {nodeIndex} has no bounding sphere.");
            nodes.Add(new MutablePhysicsNode(
                frame.Node.Type,
                frame.Node.SplittingPlane,
                frame.Node.LeafIndex,
                frame.Node.Solid,
                new FlatCollisionSphere(
                    boundingSphere.Origin,
                    boundingSphere.Radius),
                new FlatIndexRange(
                    polygonStart,
                    polygonIndices.Count - polygonStart)));

            // Stack order is reversed so positive is emitted before negative.
            if (frame.Node.NegNode is not null)
            {
                stack.Push(new PhysicsNodeFrame(
                    frame.Node.NegNode,
                    nodeIndex,
                    ChildEdge.Negative));
            }
            if (frame.Node.PosNode is not null)
            {
                stack.Push(new PhysicsNodeFrame(
                    frame.Node.PosNode,
                    nodeIndex,
                    ChildEdge.Positive));
            }
        }

        var immutableNodes = ImmutableArray.CreateBuilder<FlatPhysicsBspNode>(nodes.Count);
        foreach (MutablePhysicsNode node in nodes)
            immutableNodes.Add(node.Freeze());

        return new FlatPhysicsBsp(
            0,
            immutableNodes.MoveToImmutable(),
            ImmutableArray.CreateRange(polygonIndices),
            polygonTable);
    }

    public static FlatCellContainmentBsp FlattenCellContainmentBsp(
        CellBSPNode? root)
    {
        if (root is null)
        {
            return new FlatCellContainmentBsp(
                -1,
                ImmutableArray<FlatCellBspNode>.Empty);
        }

        var nodes = new List<MutableCellNode>();
        var visited = new Dictionary<CellBSPNode, int>(
            ReferenceEqualityComparer<CellBSPNode>.Instance);
        var stack = new Stack<CellNodeFrame>();
        stack.Push(new CellNodeFrame(root, -1, ChildEdge.Root));

        while (stack.Count != 0)
        {
            CellNodeFrame frame = stack.Pop();
            FlatBspNodeContract.Validate(
                frame.Node.Type,
                frame.Node.PosNode is not null,
                frame.Node.NegNode is not null,
                "cell-containment BSP source node");
            if (visited.ContainsKey(frame.Node))
            {
                throw new InvalidDataException(
                    "Cell-containment BSP source contains a cycle or shared child.");
            }

            int nodeIndex = nodes.Count;
            visited.Add(frame.Node, nodeIndex);
            AttachChild(nodes, frame.ParentIndex, frame.Edge, nodeIndex);
            nodes.Add(new MutableCellNode(
                frame.Node.Type,
                frame.Node.SplittingPlane,
                frame.Node.LeafIndex));

            if (frame.Node.NegNode is not null)
            {
                stack.Push(new CellNodeFrame(
                    frame.Node.NegNode,
                    nodeIndex,
                    ChildEdge.Negative));
            }
            if (frame.Node.PosNode is not null)
            {
                stack.Push(new CellNodeFrame(
                    frame.Node.PosNode,
                    nodeIndex,
                    ChildEdge.Positive));
            }
        }

        var immutableNodes = ImmutableArray.CreateBuilder<FlatCellBspNode>(nodes.Count);
        foreach (MutableCellNode node in nodes)
            immutableNodes.Add(node.Freeze());

        return new FlatCellContainmentBsp(0, immutableNodes.MoveToImmutable());
    }

    public static FlatPolygonTable FlattenPolygonTable(
        IReadOnlyDictionary<ushort, ResolvedPolygon> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var ordered = new List<KeyValuePair<ushort, ResolvedPolygon>>(resolved);
        ordered.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        int vertexCount = 0;
        foreach ((ushort id, ResolvedPolygon source) in ordered)
        {
            ValidateSourcePolygon(id, source);
            vertexCount = checked(vertexCount + source.Vertices.Length);
        }

        var polygons = ImmutableArray.CreateBuilder<FlatCollisionPolygon>(ordered.Count);
        var vertices = ImmutableArray.CreateBuilder<Vector3>(vertexCount);
        foreach ((ushort id, ResolvedPolygon source) in ordered)
        {
            int vertexStart = vertices.Count;
            foreach (Vector3 vertex in source.Vertices)
                vertices.Add(vertex);

            polygons.Add(new FlatCollisionPolygon(
                id,
                source.Plane,
                source.SidesType,
                source.NumPoints,
                new FlatIndexRange(
                    vertexStart,
                    vertices.Count - vertexStart)));
        }

        return new FlatPolygonTable(
            polygons.MoveToImmutable(),
            vertices.MoveToImmutable());
    }

    private static void ValidateSourcePolygon(
        ushort id,
        ResolvedPolygon source)
    {
        if (source is null)
            throw new InvalidDataException($"Polygon 0x{id:X4} is null.");
        if (source.Id != id)
        {
            throw new InvalidDataException(
                $"Polygon dictionary key 0x{id:X4} does not match row ID " +
                $"0x{source.Id:X4}.");
        }
        if (source.Vertices is null)
            throw new InvalidDataException($"Polygon 0x{id:X4} has no vertices.");
        if (source.NumPoints != source.Vertices.Length)
        {
            throw new InvalidDataException(
                $"Polygon 0x{id:X4} NumPoints {source.NumPoints} does not " +
                $"match vertex count {source.Vertices.Length}.");
        }
    }

    public static FlatSetupCollision FlattenSetup(SetupPhysics source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var cylinders =
            ImmutableArray.CreateBuilder<FlatCollisionCylinder>(source.CylSpheres.Count);
        foreach (CylSphere cylinder in source.CylSpheres)
        {
            if (cylinder is null)
                throw new InvalidDataException("Setup cylinder row is null.");
            cylinders.Add(new FlatCollisionCylinder(
                cylinder.Origin,
                cylinder.Radius,
                cylinder.Height));
        }

        var spheres =
            ImmutableArray.CreateBuilder<FlatCollisionSphere>(source.Spheres.Count);
        foreach (Sphere sphere in source.Spheres)
        {
            if (sphere is null)
                throw new InvalidDataException("Setup sphere row is null.");
            spheres.Add(new FlatCollisionSphere(sphere.Origin, sphere.Radius));
        }

        return new FlatSetupCollision(
            cylinders.MoveToImmutable(),
            spheres.MoveToImmutable(),
            source.Height,
            source.Radius,
            source.StepUpHeight,
            source.StepDownHeight);
    }

    public static FlatSetupCollision FlattenSetup(Setup source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var cylinders = ImmutableArray.CreateBuilder<FlatCollisionCylinder>(
            source.CylSpheres?.Count ?? 0);
        if (source.CylSpheres is not null)
        {
            foreach (CylSphere cylinder in source.CylSpheres)
            {
                if (cylinder is null)
                    throw new InvalidDataException("Setup cylinder row is null.");
                cylinders.Add(new FlatCollisionCylinder(
                    cylinder.Origin,
                    cylinder.Radius,
                    cylinder.Height));
            }
        }

        var spheres = ImmutableArray.CreateBuilder<FlatCollisionSphere>(
            source.Spheres?.Count ?? 0);
        if (source.Spheres is not null)
        {
            foreach (Sphere sphere in source.Spheres)
            {
                if (sphere is null)
                    throw new InvalidDataException("Setup sphere row is null.");
                spheres.Add(new FlatCollisionSphere(
                    sphere.Origin,
                    sphere.Radius));
            }
        }

        return new FlatSetupCollision(
            cylinders.MoveToImmutable(),
            spheres.MoveToImmutable(),
            source.Height,
            source.Radius,
            source.StepUpHeight,
            source.StepDownHeight);
    }

    public static FlatGfxObjCollisionAsset FlattenGfxObj(
        GfxObjPhysics source,
        GfxObjVisualBounds? visualBounds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.BSP);

        FlatCollisionSphere? boundingSphere = source.BoundingSphere is null
            ? null
            : new FlatCollisionSphere(
                source.BoundingSphere.Origin,
                source.BoundingSphere.Radius);
        FlatGfxObjVisualBounds? flatVisualBounds = visualBounds is null
            ? null
            : new FlatGfxObjVisualBounds(
                visualBounds.Min,
                visualBounds.Max,
                visualBounds.Center,
                visualBounds.Radius,
                visualBounds.HalfExtents);

        return new FlatGfxObjCollisionAsset(
            FlattenPhysicsBsp(source.BSP.Root, source.Resolved),
            boundingSphere,
            flatVisualBounds);
    }

    public static FlatGfxObjCollisionAsset FlattenGfxObj(GfxObj source)
    {
        ArgumentNullException.ThrowIfNull(source);

        GfxObjVisualBounds? visualBounds = source.VertexArray is null
            ? null
            : PhysicsDataCache.ComputeVisualBounds(source.VertexArray);
        FlatGfxObjVisualBounds? flatVisualBounds = visualBounds is null
            ? null
            : new FlatGfxObjVisualBounds(
                visualBounds.Min,
                visualBounds.Max,
                visualBounds.Center,
                visualBounds.Radius,
                visualBounds.HalfExtents);

        bool hasPhysics =
            source.Flags.HasFlag(GfxObjFlags.HasPhysics)
            && source.PhysicsBSP?.Root is not null
            && source.VertexArray is not null;
        if (!hasPhysics)
        {
            return new FlatGfxObjCollisionAsset(
                EmptyPhysicsBsp(),
                null,
                flatVisualBounds);
        }

        Dictionary<ushort, ResolvedPolygon> resolved =
            PhysicsDataCache.ResolvePolygons(
                source.PhysicsPolygons,
                source.VertexArray!);
        Sphere boundingSphere = source.PhysicsBSP!.Root!.BoundingSphere
            ?? throw new InvalidDataException(
                "GfxObj physics BSP root has no bounding sphere.");

        return new FlatGfxObjCollisionAsset(
            FlattenPhysicsBsp(source.PhysicsBSP.Root, resolved),
            new FlatCollisionSphere(
                boundingSphere.Origin,
                boundingSphere.Radius),
            flatVisualBounds);
    }

    public static FlatCellStructureCollisionAsset FlattenCellStructure(
        CellStruct source)
    {
        ArgumentNullException.ThrowIfNull(source);

        FlatPhysicsBsp physicsBsp;
        if (source.PhysicsBSP?.Root is null)
        {
            physicsBsp = EmptyPhysicsBsp();
        }
        else
        {
            Dictionary<ushort, ResolvedPolygon> resolved =
                PhysicsDataCache.ResolvePolygons(
                    source.PhysicsPolygons,
                    source.VertexArray);
            physicsBsp = FlattenPhysicsBsp(
                source.PhysicsBSP.Root,
                resolved);
        }

        Dictionary<ushort, ResolvedPolygon> portalResolved =
            PhysicsDataCache.ResolvePolygons(
                source.Polygons,
                source.VertexArray);
        return new FlatCellStructureCollisionAsset(
            physicsBsp,
            FlattenCellContainmentBsp(source.CellBSP?.Root),
            FlattenPolygonTable(portalResolved));
    }

    public static FlatEnvCellTopology FlattenEnvCellTopology(
        uint envCellId,
        EnvCell source,
        FlatPolygonTable portalPolygons)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(portalPolygons);

        var portals = ImmutableArray.CreateBuilder<FlatEnvCellPortal>(
            source.CellPortals.Count);
        foreach (CellPortal portal in source.CellPortals)
        {
            ushort polygonId = portal.PolygonId;
            if (!portalPolygons.TryFindPolygonIndex(
                    polygonId,
                    out int polygonIndex))
            {
                throw new InvalidDataException(
                    $"EnvCell 0x{envCellId:X8} portal references missing " +
                    $"polygon 0x{polygonId:X4}.");
            }

            portals.Add(new FlatEnvCellPortal(
                portal.OtherCellId,
                polygonId,
                (ushort)portal.Flags,
                polygonIndex));
        }

        uint prefix = envCellId & 0xFFFF_0000u;
        uint[] visibleIds = source.VisibleCells is null
            ? []
            : source.VisibleCells
                .Select(id => prefix | id)
                .Distinct()
                .Order()
                .ToArray();

        return new FlatEnvCellTopology(
            portals.MoveToImmutable(),
            ImmutableArray.Create(visibleIds),
            source.Flags.HasFlag(EnvCellFlags.SeenOutside));
    }

    public static FlatCellCollisionAsset FlattenCell(CellPhysics source)
    {
        ArgumentNullException.ThrowIfNull(source);

        FlatPhysicsBsp physicsBsp = FlattenPhysicsBsp(
            source.BSP?.Root,
            source.Resolved);
        FlatCellContainmentBsp containmentBsp =
            FlattenCellContainmentBsp(source.CellBSP?.Root);
        FlatPolygonTable portalPolygons = FlattenPolygonTable(
            source.PortalPolygons
            ?? new Dictionary<ushort, ResolvedPolygon>());

        FlatEnvCellTopology topology = FlattenEnvCellTopology(
            source.Portals,
            source.VisibleCellIds,
            source.SeenOutside,
            portalPolygons);
        var structure = new FlatCellStructureCollisionAsset(
            physicsBsp,
            containmentBsp,
            portalPolygons);
        return new FlatCellCollisionAsset(structure, topology);
    }

    public static FlatEnvCellTopology FlattenEnvCellTopology(
        IReadOnlyList<PortalInfo> portals,
        IEnumerable<uint> visibleCellIds,
        bool seenOutside,
        FlatPolygonTable portalPolygons)
    {
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(visibleCellIds);
        ArgumentNullException.ThrowIfNull(portalPolygons);

        var flatPortals = ImmutableArray.CreateBuilder<FlatEnvCellPortal>(
            portals.Count);
        foreach (PortalInfo portal in portals)
        {
            if (!portalPolygons.TryFindPolygonIndex(
                    portal.PolygonId,
                    out int polygonIndex))
            {
                throw new InvalidDataException(
                    $"EnvCell portal references missing polygon " +
                    $"0x{portal.PolygonId:X4}.");
            }

            flatPortals.Add(new FlatEnvCellPortal(
                portal.OtherCellId,
                portal.PolygonId,
                portal.Flags,
                polygonIndex));
        }

        uint[] visibleIds = visibleCellIds
            .Distinct()
            .Order()
            .ToArray();
        return new FlatEnvCellTopology(
            flatPortals.MoveToImmutable(),
            ImmutableArray.Create(visibleIds),
            seenOutside);
    }

    private static void AttachChild(
        List<MutablePhysicsNode> nodes,
        int parentIndex,
        ChildEdge edge,
        int childIndex)
    {
        if (edge == ChildEdge.Root)
            return;
        if ((uint)parentIndex >= (uint)nodes.Count)
            throw new InvalidDataException("Physics BSP child has no valid parent.");

        MutablePhysicsNode parent = nodes[parentIndex];
        if (edge == ChildEdge.Positive)
            parent.PositiveChildIndex = childIndex;
        else
            parent.NegativeChildIndex = childIndex;
    }

    private static void AttachChild(
        List<MutableCellNode> nodes,
        int parentIndex,
        ChildEdge edge,
        int childIndex)
    {
        if (edge == ChildEdge.Root)
            return;
        if ((uint)parentIndex >= (uint)nodes.Count)
            throw new InvalidDataException("Cell BSP child has no valid parent.");

        MutableCellNode parent = nodes[parentIndex];
        if (edge == ChildEdge.Positive)
            parent.PositiveChildIndex = childIndex;
        else
            parent.NegativeChildIndex = childIndex;
    }

    private enum ChildEdge : byte
    {
        Root,
        Positive,
        Negative,
    }

    private readonly record struct PhysicsNodeFrame(
        PhysicsBSPNode Node,
        int ParentIndex,
        ChildEdge Edge);

    private readonly record struct CellNodeFrame(
        CellBSPNode Node,
        int ParentIndex,
        ChildEdge Edge);

    private sealed class MutablePhysicsNode
    {
        public MutablePhysicsNode(
            DatReaderWriter.Enums.BSPNodeType type,
            Plane splittingPlane,
            int leafIndex,
            int solid,
            FlatCollisionSphere boundingSphere,
            FlatIndexRange polygonIndexRange)
        {
            Type = type;
            SplittingPlane = splittingPlane;
            LeafIndex = leafIndex;
            Solid = solid;
            BoundingSphere = boundingSphere;
            PolygonIndexRange = polygonIndexRange;
        }

        public DatReaderWriter.Enums.BSPNodeType Type { get; }

        public Plane SplittingPlane { get; }

        public int PositiveChildIndex { get; set; } = -1;

        public int NegativeChildIndex { get; set; } = -1;

        public int LeafIndex { get; }

        public int Solid { get; }

        public FlatCollisionSphere BoundingSphere { get; }

        public FlatIndexRange PolygonIndexRange { get; }

        public FlatPhysicsBspNode Freeze() => new(
            Type,
            SplittingPlane,
            PositiveChildIndex,
            NegativeChildIndex,
            LeafIndex,
            Solid,
            BoundingSphere,
            PolygonIndexRange);
    }

    private sealed class MutableCellNode
    {
        public MutableCellNode(
            DatReaderWriter.Enums.BSPNodeType type,
            Plane splittingPlane,
            int leafIndex)
        {
            Type = type;
            SplittingPlane = splittingPlane;
            LeafIndex = leafIndex;
        }

        public DatReaderWriter.Enums.BSPNodeType Type { get; }

        public Plane SplittingPlane { get; }

        public int PositiveChildIndex { get; set; } = -1;

        public int NegativeChildIndex { get; set; } = -1;

        public int LeafIndex { get; }

        public FlatCellBspNode Freeze() => new(
            Type,
            SplittingPlane,
            PositiveChildIndex,
            NegativeChildIndex,
            LeafIndex);
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceEqualityComparer<T> Instance { get; } = new();

        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
