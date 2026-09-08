using System.Collections.Immutable;
using System.Numerics;
using DatReaderWriter.Enums;

namespace AcDream.Core.Physics;

public readonly record struct FlatIndexRange(int Start, int Count)
{
    public int EndExclusive => checked(Start + Count);

    internal void Validate(int length, string name)
    {
        if (Start < 0 || Count < 0 || Start > length - Count)
        {
            throw new InvalidDataException(
                $"{name} range [{Start}, {Start}+{Count}) exceeds array length {length}.");
        }
    }
}

public readonly record struct FlatCollisionSphere(Vector3 Origin, float Radius);

public readonly record struct FlatCollisionCylinder(
    Vector3 Origin,
    float Radius,
    float Height);

public readonly record struct FlatCollisionPolygon(
    ushort Id,
    Plane Plane,
    CullMode SidesType,
    int NumPoints,
    FlatIndexRange VertexRange);

public sealed class FlatPolygonTable
{
    public static FlatPolygonTable Empty { get; } = new(
        ImmutableArray<FlatCollisionPolygon>.Empty,
        ImmutableArray<Vector3>.Empty);

    public FlatPolygonTable(
        ImmutableArray<FlatCollisionPolygon> polygons,
        ImmutableArray<Vector3> vertices)
    {
        if (polygons.IsDefault)
            throw new ArgumentException("Polygon rows must not be default.", nameof(polygons));
        if (vertices.IsDefault)
            throw new ArgumentException("Vertex rows must not be default.", nameof(vertices));

        ushort previousId = 0;
        for (int i = 0; i < polygons.Length; i++)
        {
            FlatCollisionPolygon polygon = polygons[i];
            polygon.VertexRange.Validate(vertices.Length, $"polygon[{i}].vertices");

            if (polygon.NumPoints != polygon.VertexRange.Count)
            {
                throw new InvalidDataException(
                    $"polygon[{i}] NumPoints {polygon.NumPoints} does not match " +
                    $"its vertex count {polygon.VertexRange.Count}.");
            }

            if (!Enum.IsDefined(polygon.SidesType))
            {
                throw new InvalidDataException(
                    $"polygon[{i}] has unknown cull mode {(int)polygon.SidesType}.");
            }

            if (i != 0 && polygon.Id <= previousId)
            {
                throw new InvalidDataException(
                    "Flat polygon rows must be strictly ordered by unique ID.");
            }

            previousId = polygon.Id;
        }

        Polygons = polygons;
        Vertices = vertices;
    }

    public ImmutableArray<FlatCollisionPolygon> Polygons { get; }

    public ImmutableArray<Vector3> Vertices { get; }

    public bool TryFindPolygonIndex(ushort id, out int index)
    {
        int low = 0;
        int high = Polygons.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            ushort candidate = Polygons[middle].Id;
            if (candidate == id)
            {
                index = middle;
                return true;
            }

            if (candidate < id)
                low = middle + 1;
            else
                high = middle - 1;
        }

        index = -1;
        return false;
    }
}

public readonly record struct FlatPhysicsBspNode(
    BSPNodeType Type,
    Plane SplittingPlane,
    int PositiveChildIndex,
    int NegativeChildIndex,
    int LeafIndex,
    int Solid,
    FlatCollisionSphere BoundingSphere,
    FlatIndexRange PolygonIndexRange);

public sealed class FlatPhysicsBsp
{
    public FlatPhysicsBsp(
        int rootIndex,
        ImmutableArray<FlatPhysicsBspNode> nodes,
        ImmutableArray<int> polygonIndexStream,
        FlatPolygonTable polygonTable)
    {
        if (nodes.IsDefault)
            throw new ArgumentException("Node rows must not be default.", nameof(nodes));
        if (polygonIndexStream.IsDefault)
        {
            throw new ArgumentException(
                "Polygon-index rows must not be default.",
                nameof(polygonIndexStream));
        }

        ArgumentNullException.ThrowIfNull(polygonTable);
        ValidateTree(rootIndex, nodes, polygonIndexStream, polygonTable.Polygons.Length);

        RootIndex = rootIndex;
        Nodes = nodes;
        PolygonIndexStream = polygonIndexStream;
        PolygonTable = polygonTable;
    }

    public int RootIndex { get; }

    public ImmutableArray<FlatPhysicsBspNode> Nodes { get; }

    public ImmutableArray<int> PolygonIndexStream { get; }

    public FlatPolygonTable PolygonTable { get; }

    private static void ValidateTree(
        int rootIndex,
        ImmutableArray<FlatPhysicsBspNode> nodes,
        ImmutableArray<int> polygonIndexStream,
        int polygonCount)
    {
        if (nodes.Length == 0)
        {
            if (rootIndex != -1)
                throw new InvalidDataException("An empty physics BSP must use root index -1.");
            if (polygonIndexStream.Length != 0)
            {
                throw new InvalidDataException(
                    "An empty physics BSP cannot contain leaf polygon indices.");
            }
            return;
        }

        if (rootIndex != 0)
        {
            throw new InvalidDataException(
                $"A deterministic pre-order physics BSP must start at index 0, not {rootIndex}.");
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            FlatPhysicsBspNode node = nodes[i];
            FlatBspNodeContract.Validate(
                node.Type,
                node.PositiveChildIndex >= 0,
                node.NegativeChildIndex >= 0,
                $"physics node[{i}]");
            ValidateChild(node.PositiveChildIndex, nodes.Length, i, "positive");
            ValidateChild(node.NegativeChildIndex, nodes.Length, i, "negative");
            node.PolygonIndexRange.Validate(
                polygonIndexStream.Length,
                $"physics node[{i}].polygonIndices");
        }

        for (int i = 0; i < polygonIndexStream.Length; i++)
        {
            int polygonIndex = polygonIndexStream[i];
            if ((uint)polygonIndex >= (uint)polygonCount)
            {
                throw new InvalidDataException(
                    $"leaf polygon stream[{i}] index {polygonIndex} exceeds " +
                    $"polygon count {polygonCount}.");
            }
        }

        ValidatePreOrderShape(
            rootIndex,
            nodes.Length,
            static (rows, index) => rows[index].PositiveChildIndex,
            static (rows, index) => rows[index].NegativeChildIndex,
            nodes,
            "physics BSP");
    }

    private static void ValidateChild(
        int childIndex,
        int nodeCount,
        int ownerIndex,
        string edge)
    {
        if (childIndex < -1 || childIndex >= nodeCount)
        {
            throw new InvalidDataException(
                $"physics node[{ownerIndex}] {edge} child {childIndex} is out of range.");
        }
    }

    internal static void ValidatePreOrderShape<TNode>(
        int rootIndex,
        int nodeCount,
        Func<ImmutableArray<TNode>, int, int> getPositive,
        Func<ImmutableArray<TNode>, int, int> getNegative,
        ImmutableArray<TNode> nodes,
        string name)
    {
        var visited = new bool[nodeCount];
        var stack = new Stack<int>();
        stack.Push(rootIndex);
        int expectedIndex = 0;

        while (stack.Count != 0)
        {
            int index = stack.Pop();
            if (visited[index])
                throw new InvalidDataException($"{name} contains a cycle or shared child.");
            if (index != expectedIndex)
            {
                throw new InvalidDataException(
                    $"{name} is not in deterministic positive-before-negative pre-order: " +
                    $"expected node {expectedIndex}, encountered {index}.");
            }

            visited[index] = true;
            expectedIndex++;

            int negative = getNegative(nodes, index);
            int positive = getPositive(nodes, index);
            if (negative >= 0)
                stack.Push(negative);
            if (positive >= 0)
                stack.Push(positive);
        }

        if (expectedIndex != nodeCount)
        {
            throw new InvalidDataException(
                $"{name} contains {nodeCount - expectedIndex} unreachable node(s).");
        }
    }
}

public readonly record struct FlatCellBspNode(
    BSPNodeType Type,
    Plane SplittingPlane,
    int PositiveChildIndex,
    int NegativeChildIndex,
    int LeafIndex);

public sealed class FlatCellContainmentBsp
{
    public FlatCellContainmentBsp(
        int rootIndex,
        ImmutableArray<FlatCellBspNode> nodes)
    {
        if (nodes.IsDefault)
            throw new ArgumentException("Node rows must not be default.", nameof(nodes));

        if (nodes.Length == 0)
        {
            if (rootIndex != -1)
                throw new InvalidDataException("An empty cell BSP must use root index -1.");
        }
        else
        {
            if (rootIndex != 0)
            {
                throw new InvalidDataException(
                    $"A deterministic pre-order cell BSP must start at index 0, not {rootIndex}.");
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                FlatCellBspNode node = nodes[i];
                FlatBspNodeContract.Validate(
                    node.Type,
                    node.PositiveChildIndex >= 0,
                    node.NegativeChildIndex >= 0,
                    $"cell node[{i}]");
                if (node.PositiveChildIndex < -1 || node.PositiveChildIndex >= nodes.Length)
                {
                    throw new InvalidDataException(
                        $"cell node[{i}] positive child {node.PositiveChildIndex} is out of range.");
                }
                if (node.NegativeChildIndex < -1 || node.NegativeChildIndex >= nodes.Length)
                {
                    throw new InvalidDataException(
                        $"cell node[{i}] negative child {node.NegativeChildIndex} is out of range.");
                }
            }

            FlatPhysicsBsp.ValidatePreOrderShape(
                rootIndex,
                nodes.Length,
                static (rows, index) => rows[index].PositiveChildIndex,
                static (rows, index) => rows[index].NegativeChildIndex,
                nodes,
                "cell-containment BSP");
        }

        RootIndex = rootIndex;
        Nodes = nodes;
    }

    public int RootIndex { get; }

    public ImmutableArray<FlatCellBspNode> Nodes { get; }
}

public sealed class FlatSetupCollision
{
    public FlatSetupCollision(
        ImmutableArray<FlatCollisionCylinder> cylinders,
        ImmutableArray<FlatCollisionSphere> spheres,
        float height,
        float radius,
        float stepUpHeight,
        float stepDownHeight)
    {
        if (cylinders.IsDefault)
            throw new ArgumentException("Cylinder rows must not be default.", nameof(cylinders));
        if (spheres.IsDefault)
            throw new ArgumentException("Sphere rows must not be default.", nameof(spheres));

        Cylinders = cylinders;
        Spheres = spheres;
        Height = height;
        Radius = radius;
        StepUpHeight = stepUpHeight;
        StepDownHeight = stepDownHeight;
    }

    public ImmutableArray<FlatCollisionCylinder> Cylinders { get; }

    public ImmutableArray<FlatCollisionSphere> Spheres { get; }

    public float Height { get; }

    public float Radius { get; }

    public float StepUpHeight { get; }

    public float StepDownHeight { get; }
}

public readonly record struct FlatGfxObjVisualBounds(
    Vector3 Min,
    Vector3 Max,
    Vector3 Center,
    float Radius,
    Vector3 HalfExtents);

public sealed record FlatGfxObjCollisionAsset(
    FlatPhysicsBsp PhysicsBsp,
    FlatCollisionSphere? BoundingSphere,
    FlatGfxObjVisualBounds? VisualBounds);

public sealed record FlatCellStructureCollisionAsset(
    FlatPhysicsBsp PhysicsBsp,
    FlatCellContainmentBsp ContainmentBsp,
    FlatPolygonTable PortalPolygons);

public readonly record struct FlatEnvCellPortal(
    ushort OtherCellId,
    ushort PolygonId,
    ushort Flags,
    int PolygonIndex);

public sealed class FlatEnvCellTopology
{
    public FlatEnvCellTopology(
        ImmutableArray<FlatEnvCellPortal> portals,
        ImmutableArray<uint> visibleCellIds,
        bool seenOutside)
    {
        if (portals.IsDefault)
            throw new ArgumentException("Portal rows must not be default.", nameof(portals));
        if (visibleCellIds.IsDefault)
        {
            throw new ArgumentException(
                "Visible-cell rows must not be default.",
                nameof(visibleCellIds));
        }

        uint previous = 0;
        for (int i = 0; i < visibleCellIds.Length; i++)
        {
            uint current = visibleCellIds[i];
            if (i != 0 && current <= previous)
            {
                throw new InvalidDataException(
                    "Visible cell IDs must be strictly ordered and unique.");
            }
            previous = current;
        }

        for (int i = 0; i < portals.Length; i++)
        {
            if (portals[i].PolygonIndex < 0)
            {
                throw new InvalidDataException(
                    $"portal[{i}] has invalid polygon index {portals[i].PolygonIndex}.");
            }
        }

        Portals = portals;
        VisibleCellIds = visibleCellIds;
        SeenOutside = seenOutside;
    }

    public ImmutableArray<FlatEnvCellPortal> Portals { get; }

    public ImmutableArray<uint> VisibleCellIds { get; }

    public bool SeenOutside { get; }
}

public sealed class FlatCellCollisionAsset
{
    public FlatCellCollisionAsset(
        FlatCellStructureCollisionAsset structure,
        FlatEnvCellTopology topology)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(topology);

        int polygonCount = structure.PortalPolygons.Polygons.Length;
        for (int i = 0; i < topology.Portals.Length; i++)
        {
            int polygonIndex = topology.Portals[i].PolygonIndex;
            if ((uint)polygonIndex >= (uint)polygonCount)
            {
                throw new InvalidDataException(
                    $"portal[{i}] polygon index {polygonIndex} exceeds " +
                    $"portal polygon count {polygonCount}.");
            }
        }

        Structure = structure;
        Topology = topology;
    }

    public FlatCellStructureCollisionAsset Structure { get; }

    public FlatEnvCellTopology Topology { get; }
}

internal static class FlatBspNodeContract
{
    public static void Validate(
        BSPNodeType type,
        bool hasPositive,
        bool hasNegative,
        string name)
    {
        if (!Enum.IsDefined(type) || type == BSPNodeType.Portal)
            throw new InvalidDataException($"{name} has unsupported type 0x{(uint)type:X8}.");

        (bool expectedPositive, bool expectedNegative) = type switch
        {
            BSPNodeType.Leaf => (false, false),
            BSPNodeType.BPIn or BSPNodeType.BPnn => (true, false),
            BSPNodeType.BpIN or BSPNodeType.BpnN => (false, true),
            BSPNodeType.BPIN or BSPNodeType.BPnN => (true, true),
            BSPNodeType.BPOL or BSPNodeType.BPFL => (false, false),
            _ => throw new InvalidDataException(
                $"{name} has unsupported type 0x{(uint)type:X8}."),
        };

        if (hasPositive != expectedPositive || hasNegative != expectedNegative)
        {
            throw new InvalidDataException(
                $"{name} type {type} requires children " +
                $"(+={expectedPositive}, -={expectedNegative}) but contains " +
                $"(+={hasPositive}, -={hasNegative}).");
        }
    }
}
