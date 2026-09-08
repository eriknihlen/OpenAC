using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class FlatCollisionAssetBuilderTests
{
    [Fact]
    public void PhysicsBsp_FlattensPositiveBeforeNegative_AndPreservesBitsAndLeafOrder()
    {
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x8000_0000));
        float payload = BitConverter.Int32BitsToSingle(unchecked((int)0x3F12_3456));
        var polygon4 = Polygon(
            4,
            new Plane(negativeZero, payload, 1f, -2f),
            CullMode.Clockwise,
            new Vector3(negativeZero, payload, 3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f));
        var polygon9 = Polygon(
            9,
            new Plane(-1f, 0f, 0f, 4f),
            CullMode.CounterClockwise,
            new Vector3(9f, 8f, 7f),
            new Vector3(6f, 5f, 4f),
            new Vector3(3f, 2f, 1f));
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [9] = polygon9,
            [4] = polygon4,
        };

        var positive = Node(BSPNodeType.Leaf, leafIndex: 17);
        positive.Polygons.Add(9);
        positive.Polygons.Add(4);
        var negative = Node(BSPNodeType.Leaf, leafIndex: 23);
        negative.Polygons.Add(4);
        negative.Polygons.Add(9);
        var root = Node(BSPNodeType.BPIN);
        root.SplittingPlane = new Plane(payload, negativeZero, 0.25f, -12f);
        root.PosNode = positive;
        root.NegNode = negative;

        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);

        Assert.Equal(0, flat.RootIndex);
        Assert.Equal(3, flat.Nodes.Length);
        Assert.Equal(1, flat.Nodes[0].PositiveChildIndex);
        Assert.Equal(2, flat.Nodes[0].NegativeChildIndex);
        Assert.Equal(17, flat.Nodes[1].LeafIndex);
        Assert.Equal(23, flat.Nodes[2].LeafIndex);
        Assert.Equal(new ushort[] { 4, 9 }, flat.PolygonTable.Polygons.Select(p => p.Id));
        Assert.Equal(new[] { 1, 0, 0, 1 }, flat.PolygonIndexStream);
        AssertPlaneBits(root.SplittingPlane, flat.Nodes[0].SplittingPlane);
        AssertPlaneBits(polygon4.Plane, flat.PolygonTable.Polygons[0].Plane);
        AssertVectorBits(polygon4.Vertices[0], flat.PolygonTable.Vertices[0]);

        polygon4.Vertices[0] = new Vector3(100f, 200f, 300f);
        positive.Polygons.Clear();
        root.SplittingPlane = default;
        AssertVectorBits(
            new Vector3(negativeZero, payload, 3f),
            flat.PolygonTable.Vertices[0]);
        Assert.Equal(new[] { 1, 0, 0, 1 }, flat.PolygonIndexStream);
        AssertPlaneBits(
            new Plane(payload, negativeZero, 0.25f, -12f),
            flat.Nodes[0].SplittingPlane);
    }

    [Fact]
    public void PhysicsBsp_IsDeterministicAcrossPolygonDictionaryInsertionOrder()
    {
        ResolvedPolygon polygon2 = Polygon(
            2,
            new Plane(Vector3.UnitX, 2f),
            CullMode.None,
            Vector3.Zero,
            Vector3.UnitY,
            Vector3.UnitZ);
        ResolvedPolygon polygon7 = Polygon(
            7,
            new Plane(Vector3.UnitY, 7f),
            CullMode.Clockwise,
            Vector3.One,
            Vector3.UnitX,
            Vector3.UnitZ);
        var first = new Dictionary<ushort, ResolvedPolygon>
        {
            [2] = polygon2,
            [7] = polygon7,
        };
        var second = new Dictionary<ushort, ResolvedPolygon>
        {
            [7] = polygon7,
            [2] = polygon2,
        };
        PhysicsBSPNode root = Node(BSPNodeType.Leaf);
        root.Polygons.Add(7);
        root.Polygons.Add(2);

        FlatPhysicsBsp left =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, first);
        FlatPhysicsBsp right =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, second);

        AssertFlatPhysicsEqual(left, right);
    }

    [Fact]
    public void PhysicsBsp_MissingLeafPolygon_IsCorrupt()
    {
        PhysicsBSPNode root = Node(BSPNodeType.Leaf);
        root.Polygons.Add(55);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                root,
                new Dictionary<ushort, ResolvedPolygon>()));

        Assert.Contains("0x0037", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicsBsp_CycleAndSharedChild_AreRejected()
    {
        PhysicsBSPNode cycle = Node(BSPNodeType.BPIn);
        cycle.PosNode = cycle;
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                cycle,
                new Dictionary<ushort, ResolvedPolygon>()));

        PhysicsBSPNode child = Node(BSPNodeType.Leaf);
        PhysicsBSPNode shared = Node(BSPNodeType.BPIN);
        shared.PosNode = child;
        shared.NegNode = child;
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                shared,
                new Dictionary<ushort, ResolvedPolygon>()));
    }

    [Fact]
    public void FlatPhysicsSchema_RejectsInvalidRangesChildrenAndShape()
    {
        var table = new FlatPolygonTable(
            ImmutableArray.Create(new FlatCollisionPolygon(
                3,
                new Plane(Vector3.UnitZ, 0f),
                CullMode.None,
                3,
                new FlatIndexRange(0, 3))),
            ImmutableArray.Create(Vector3.Zero, Vector3.UnitX, Vector3.UnitY));

        Assert.Throws<InvalidDataException>(() => new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(new FlatPhysicsBspNode(
                BSPNodeType.BPIn,
                default,
                4,
                -1,
                0,
                0,
                default,
                default)),
            ImmutableArray<int>.Empty,
            table));

        Assert.Throws<InvalidDataException>(() => new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(new FlatPhysicsBspNode(
                BSPNodeType.Leaf,
                default,
                -1,
                -1,
                0,
                0,
                default,
                new FlatIndexRange(0, 1))),
            ImmutableArray.Create(7),
            table));

        Assert.Throws<InvalidDataException>(() => new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(new FlatPhysicsBspNode(
                BSPNodeType.Leaf,
                default,
                0,
                -1,
                0,
                0,
                default,
                default)),
            ImmutableArray<int>.Empty,
            table));

        var root = new FlatPhysicsBspNode(
            BSPNodeType.BPIN,
            default,
            2,
            1,
            0,
            0,
            default,
            default);
        var leaf = new FlatPhysicsBspNode(
            BSPNodeType.Leaf,
            default,
            -1,
            -1,
            0,
            0,
            default,
            default);
        Assert.Throws<InvalidDataException>(() => new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(root, leaf, leaf),
            ImmutableArray<int>.Empty,
            table));
    }

    [Fact]
    public void CellContainment_FlattensAsymmetricTreeIteratively()
    {
        var positiveLeaf = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 11,
        };
        var negativeLeaf = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 22,
        };
        var negativeBranch = new CellBSPNode
        {
            Type = BSPNodeType.BpnN,
            SplittingPlane = new Plane(Vector3.UnitY, -5f),
            NegNode = negativeLeaf,
        };
        var root = new CellBSPNode
        {
            Type = BSPNodeType.BPIN,
            SplittingPlane = new Plane(Vector3.UnitX, -3f),
            PosNode = positiveLeaf,
            NegNode = negativeBranch,
        };

        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root);

        Assert.Equal(4, flat.Nodes.Length);
        Assert.Equal((1, 2), (
            flat.Nodes[0].PositiveChildIndex,
            flat.Nodes[0].NegativeChildIndex));
        Assert.Equal(-1, flat.Nodes[2].PositiveChildIndex);
        Assert.Equal(3, flat.Nodes[2].NegativeChildIndex);
        Assert.Equal(11, flat.Nodes[1].LeafIndex);
        Assert.Equal(22, flat.Nodes[3].LeafIndex);
        AssertPlaneBits(root.SplittingPlane, flat.Nodes[0].SplittingPlane);
        AssertPlaneBits(
            negativeBranch.SplittingPlane,
            flat.Nodes[2].SplittingPlane);
    }

    [Fact]
    public void CellContainment_CycleSharedChildAndInvalidFlatChild_AreRejected()
    {
        var cycle = new CellBSPNode { Type = BSPNodeType.BPIn };
        cycle.PosNode = cycle;
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenCellContainmentBsp(cycle));

        var child = new CellBSPNode { Type = BSPNodeType.Leaf };
        var shared = new CellBSPNode
        {
            Type = BSPNodeType.BPIN,
            PosNode = child,
            NegNode = child,
        };
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenCellContainmentBsp(shared));

        Assert.Throws<InvalidDataException>(() => new FlatCellContainmentBsp(
            0,
            ImmutableArray.Create(new FlatCellBspNode(
                BSPNodeType.BPIn,
                default,
                9,
                -1,
                0))));
    }

    [Fact]
    public void CellContainment_DeepTree_DoesNotUseCallStack()
    {
        const int depth = 20_000;
        var root = new CellBSPNode { Type = BSPNodeType.Leaf };
        for (int i = 1; i < depth; i++)
        {
            root = new CellBSPNode
            {
                Type = BSPNodeType.BPIn,
                LeafIndex = i,
                SplittingPlane = new Plane(Vector3.UnitX, i),
                PosNode = root,
            };
        }

        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root);

        Assert.Equal(depth, flat.Nodes.Length);
        Assert.Equal(-1, flat.Nodes[^1].PositiveChildIndex);
    }

    [Fact]
    public void Setup_FlattensOrderedShapesAndExactFloatBits()
    {
        float payloadA = BitConverter.Int32BitsToSingle(unchecked((int)0x3F45_6789));
        float payloadB = BitConverter.Int32BitsToSingle(unchecked((int)0xBF12_3456));
        var source = new SetupPhysics
        {
            CylSpheres =
            [
                new CylSphere
                {
                    Origin = new Vector3(payloadA, 2f, 3f),
                    Radius = payloadB,
                    Height = 5f,
                },
                new CylSphere
                {
                    Origin = new Vector3(6f, 7f, 8f),
                    Radius = 9f,
                    Height = 10f,
                },
            ],
            Spheres =
            [
                new Sphere
                {
                    Origin = new Vector3(11f, payloadB, 13f),
                    Radius = payloadA,
                },
            ],
            Height = payloadA,
            Radius = payloadB,
            StepUpHeight = -0f,
            StepDownHeight = 0.75f,
        };

        FlatSetupCollision flat = FlatCollisionAssetBuilder.FlattenSetup(source);

        Assert.Equal(2, flat.Cylinders.Length);
        Assert.Single(flat.Spheres);
        AssertVectorBits(source.CylSpheres[0].Origin, flat.Cylinders[0].Origin);
        AssertFloatBits(source.CylSpheres[0].Radius, flat.Cylinders[0].Radius);
        AssertFloatBits(source.CylSpheres[0].Height, flat.Cylinders[0].Height);
        AssertVectorBits(source.Spheres[0].Origin, flat.Spheres[0].Origin);
        AssertFloatBits(source.Spheres[0].Radius, flat.Spheres[0].Radius);
        AssertFloatBits(source.Height, flat.Height);
        AssertFloatBits(source.Radius, flat.Radius);
        AssertFloatBits(source.StepUpHeight, flat.StepUpHeight);
        AssertFloatBits(source.StepDownHeight, flat.StepDownHeight);
    }

    [Fact]
    public void CellAsset_ResolvesPortalIdsAndSortsVisibleCells()
    {
        var physicsRoot = Node(BSPNodeType.Leaf);
        ResolvedPolygon physicsPolygon = Polygon(
            1,
            new Plane(Vector3.UnitZ, 0f),
            CullMode.None,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY);
        physicsRoot.Polygons.Add(1);
        ResolvedPolygon portal8 = Polygon(
            8,
            new Plane(Vector3.UnitX, -1f),
            CullMode.CounterClockwise,
            Vector3.Zero,
            Vector3.UnitY,
            Vector3.UnitZ);
        ResolvedPolygon portal3 = Polygon(
            3,
            new Plane(Vector3.UnitY, -1f),
            CullMode.Clockwise,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitZ);
        var source = new CellPhysics
        {
            BSP = new PhysicsBSPTree { Root = physicsRoot },
            Resolved = new Dictionary<ushort, ResolvedPolygon>
            {
                [1] = physicsPolygon,
            },
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            },
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon>
            {
                [8] = portal8,
                [3] = portal3,
            },
            Portals =
            [
                new PortalInfo(0x0102, 8, 4),
                new PortalInfo(0x0103, 3, 2),
            ],
            VisibleCellIds = new HashSet<uint>
            {
                0xA9B4_0103,
                0xA9B4_0101,
                0xA9B4_0102,
            },
            SeenOutside = true,
        };

        FlatCellCollisionAsset flat =
            FlatCollisionAssetBuilder.FlattenCell(source);

        Assert.Equal(new ushort[] { 3, 8 },
            flat.Structure.PortalPolygons.Polygons.Select(p => p.Id));
        Assert.Equal(1, flat.Topology.Portals[0].PolygonIndex);
        Assert.Equal(0, flat.Topology.Portals[1].PolygonIndex);
        Assert.Equal(
            new uint[] { 0xA9B4_0101, 0xA9B4_0102, 0xA9B4_0103 },
            flat.Topology.VisibleCellIds);
        Assert.True(flat.Topology.SeenOutside);
    }

    [Fact]
    public void PolygonTable_KeyIdentityAndPointCountAreValidated()
    {
        ResolvedPolygon mismatchedId = Polygon(
            4,
            default,
            CullMode.None,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY);
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenPolygonTable(
                new Dictionary<ushort, ResolvedPolygon> { [5] = mismatchedId }));

        var mismatchedCount = new ResolvedPolygon
        {
            Id = 6,
            Plane = default,
            SidesType = CullMode.None,
            NumPoints = 99,
            Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        };
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetBuilder.FlattenPolygonTable(
                new Dictionary<ushort, ResolvedPolygon> { [6] = mismatchedCount }));
    }

    private static PhysicsBSPNode Node(
        BSPNodeType type,
        int leafIndex = 0) => new()
        {
            Type = type,
            LeafIndex = leafIndex,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(1f, 2f, 3f),
                Radius = 4f,
            },
        };

    private static ResolvedPolygon Polygon(
        ushort id,
        Plane plane,
        CullMode sides,
        params Vector3[] vertices) => new()
        {
            Id = id,
            Plane = plane,
            SidesType = sides,
            NumPoints = vertices.Length,
            Vertices = vertices,
        };

    internal static void AssertFlatPhysicsEqual(
        FlatPhysicsBsp expected,
        FlatPhysicsBsp actual)
    {
        Assert.Equal(expected.RootIndex, actual.RootIndex);
        Assert.Equal(expected.Nodes.Length, actual.Nodes.Length);
        Assert.Equal(
            expected.PolygonIndexStream.AsEnumerable(),
            actual.PolygonIndexStream.AsEnumerable());
        Assert.Equal(
            expected.PolygonTable.Polygons.Length,
            actual.PolygonTable.Polygons.Length);
        Assert.Equal(
            expected.PolygonTable.Vertices.Length,
            actual.PolygonTable.Vertices.Length);

        for (int i = 0; i < expected.Nodes.Length; i++)
        {
            FlatPhysicsBspNode left = expected.Nodes[i];
            FlatPhysicsBspNode right = actual.Nodes[i];
            Assert.Equal(left.Type, right.Type);
            AssertPlaneBits(left.SplittingPlane, right.SplittingPlane);
            Assert.Equal(left.PositiveChildIndex, right.PositiveChildIndex);
            Assert.Equal(left.NegativeChildIndex, right.NegativeChildIndex);
            Assert.Equal(left.LeafIndex, right.LeafIndex);
            Assert.Equal(left.Solid, right.Solid);
            AssertVectorBits(left.BoundingSphere.Origin, right.BoundingSphere.Origin);
            AssertFloatBits(left.BoundingSphere.Radius, right.BoundingSphere.Radius);
            Assert.Equal(left.PolygonIndexRange, right.PolygonIndexRange);
        }

        for (int i = 0; i < expected.PolygonTable.Polygons.Length; i++)
        {
            FlatCollisionPolygon left = expected.PolygonTable.Polygons[i];
            FlatCollisionPolygon right = actual.PolygonTable.Polygons[i];
            Assert.Equal(left.Id, right.Id);
            AssertPlaneBits(left.Plane, right.Plane);
            Assert.Equal(left.SidesType, right.SidesType);
            Assert.Equal(left.NumPoints, right.NumPoints);
            Assert.Equal(left.VertexRange, right.VertexRange);
        }

        for (int i = 0; i < expected.PolygonTable.Vertices.Length; i++)
        {
            AssertVectorBits(
                expected.PolygonTable.Vertices[i],
                actual.PolygonTable.Vertices[i]);
        }
    }

    internal static void AssertPlaneBits(Plane expected, Plane actual)
    {
        AssertVectorBits(expected.Normal, actual.Normal);
        AssertFloatBits(expected.D, actual.D);
    }

    internal static void AssertVectorBits(Vector3 expected, Vector3 actual)
    {
        AssertFloatBits(expected.X, actual.X);
        AssertFloatBits(expected.Y, actual.Y);
        AssertFloatBits(expected.Z, actual.Z);
    }

    internal static void AssertFloatBits(float expected, float actual) =>
        Assert.Equal(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(actual));
}
