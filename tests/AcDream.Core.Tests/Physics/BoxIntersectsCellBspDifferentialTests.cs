using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class BoxIntersectsCellBspDifferentialTests
{
    [Fact]
    public void NullRoot_LeafRoot_ReturnTrue_BothRepresentations()
    {
        FlatCellContainmentBsp emptyFlat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(null);
        Assert.True(BSPQuery.BoxIntersectsCellBsp(
            null, new Vector3(-1f), new Vector3(1f)));
        Assert.True(FlatBspQuery.BoxIntersectsCellBsp(
            emptyFlat, new Vector3(-1f), new Vector3(1f)));

        var leaf = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 5,
        };
        FlatCellContainmentBsp leafFlat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(leaf);
        Assert.True(BSPQuery.BoxIntersectsCellBsp(
            leaf, new Vector3(-1f), new Vector3(1f)));
        Assert.True(FlatBspQuery.BoxIntersectsCellBsp(
            leafFlat, new Vector3(-1f), new Vector3(1f)));
    }

    [Fact]
    public void SingleSplittingPlane_UniformPositiveNegativeAndStraddle_MatchGraphAndRetailSemantics()
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf, LeafIndex = 1 };
        var root = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitX, 0f),
            PosNode = leaf,
        };
        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root);

        // Box entirely positive (min.x > 0): admitted (descends to leaf -> true).
        AssertBoxEqual(root, flat, new Vector3(1f, -1f, -1f), new Vector3(2f, 1f, 1f), expectTrue: true);

        AssertBoxEqual(root, flat, new Vector3(-2f, -1f, -1f), new Vector3(-1f, 1f, 1f), expectTrue: false);

        // Box straddling x=0: admitted (true) — a straddling box is never
        // "entirely negative."
        AssertBoxEqual(root, flat, new Vector3(-0.5f, -1f, -1f), new Vector3(0.5f, 1f, 1f), expectTrue: true);

        // Exact epsilon boundary: max.x just inside -eps (entirely negative
        // by the tiniest margin) vs just outside (straddling by the tiniest
        // margin). F_EPSILON = 0.000199999995f (BSPQuery.BoxPlaneEpsilon).
        const float eps = 0.000199999995f;
        AssertBoxEqual(
            root, flat,
            new Vector3(-1f, -1f, -1f), new Vector3(-eps - 0.0001f, 1f, 1f),
            expectTrue: false);
        AssertBoxEqual(
            root, flat,
            new Vector3(-1f, -1f, -1f), new Vector3(-eps + 0.0001f, 1f, 1f),
            expectTrue: true);
    }

    [Fact]
    public void DeepChain_MultipleNodeTypesAndChildNullTermination_MatchGraphBits()
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf, LeafIndex = 42 };
        CellBSPNode graph = leaf;
        Vector3[] normals =
        [
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            Vector3.Normalize(new Vector3(1f, 1f, 1f)),
        ];
        const int Depth = 200;
        for (int i = 0; i < Depth; i++)
        {
            graph = new CellBSPNode
            {
                Type = BSPNodeType.BPIn,
                SplittingPlane = new Plane(normals[i % normals.Length], 1_000f),
                PosNode = graph,
            };
        }

        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(graph);

        AssertBoxEqual(graph, flat, new Vector3(-1f), new Vector3(1f), expectTrue: true);

        AssertBoxEqual(
            graph, flat,
            new Vector3(-2000f, -2000f, -2000f), new Vector3(-1900f, -1900f, -1900f),
            expectTrue: false);
    }

    [Fact]
    public void RandomizedSyntheticSweep_ArbitraryBoxesAgainstBranchingTree_MatchGraphBits()
    {
        var leafA = new CellBSPNode { Type = BSPNodeType.Leaf, LeafIndex = 1 };
        var leafB = new CellBSPNode { Type = BSPNodeType.Leaf, LeafIndex = 2 };
        var midY = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitY, -3f),
            PosNode = leafB,
        };
        var midX = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitX, -3f),
            PosNode = midY,
        };
        var root = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitZ, -3f),
            PosNode = midX,
        };
        _ = leafA; // referenced only to document the tree shape; unreachable via PosNode-only walk

        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root);

        var random = new Random(0x4150_3135);
        for (int i = 0; i < 20_000; i++)
        {
            Vector3 a = new(
                NextFloat(random, -6f, 6f),
                NextFloat(random, -6f, 6f),
                NextFloat(random, -6f, 6f));
            Vector3 extent = new(
                NextFloat(random, 0f, 4f),
                NextFloat(random, 0f, 4f),
                NextFloat(random, 0f, 4f));
            Vector3 min = a;
            Vector3 max = a + extent;

            bool graphResult = BSPQuery.BoxIntersectsCellBsp(root, min, max);
            bool flatResult = FlatBspQuery.BoxIntersectsCellBsp(flat, min, max);
            Assert.True(
                graphResult == flatResult,
                $"iteration {i}: min={min}, max={max}, graph={graphResult}, flat={flatResult}.");
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_RandomizedBoxSweepOverEnvCellContainmentBsps_HasZeroMismatch()
    {
        string? datDirectory = ConformanceDats.ResolveDatDir();
        if (datDirectory is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        var random = new Random(0x4230_5820);
        int cellsSwept = 0;
        int comparisons = 0;

        foreach (uint cellId in new[]
        {
            0x8A02_016Eu,
            0x8A02_017Au,
            0xA9B4_013Fu,
            0xA9B4_0150u,
            0xA9B4_0159u,
            0xA9B4_015Au,
            0xA9B4_0161u,
            0xA9B4_0162u,
            0xA9B4_0164u,
            0xA9B4_0166u,
        })
        {
            var cache = new PhysicsDataCache();
            ConformanceDats.LoadEnvCell(dats, cache, cellId);
            CellPhysics source = Assert.IsType<CellPhysics>(
                cache.GetCellStruct(cellId));
            FlatCellContainmentBsp flatContainment =
                FlatCollisionAssetBuilder.FlattenCellContainmentBsp(
                    source.CellBSP?.Root);

            if (source.CellBSP?.Root is null)
                continue;
            cellsSwept++;

            Vector3[] anchors = source.Resolved.Count > 0
                ? source.Resolved.Values
                    .SelectMany(p => p.Vertices.ToArray())
                    .ToArray()
                : [Vector3.Zero];

            for (int iteration = 0; iteration < 2_000; iteration++)
            {
                Vector3 anchor = anchors[random.Next(anchors.Length)];
                float halfExtent = (iteration % 9) switch
                {
                    0 => BSPQuery.BoxPlaneEpsilon,
                    1 => 0.01f,
                    2 => 0.5f,
                    _ => NextFloat(random, 0.05f, 2.5f),
                };
                Vector3 jitter = new(
                    NextFloat(random, -1.5f, 1.5f),
                    NextFloat(random, -1.5f, 1.5f),
                    NextFloat(random, -1.5f, 1.5f));
                Vector3 center = anchor + jitter;
                Vector3 min = center - new Vector3(halfExtent);
                Vector3 max = center + new Vector3(halfExtent);

                bool graphResult = BSPQuery.BoxIntersectsCellBsp(
                    source.CellBSP?.Root, min, max);
                bool flatResult = FlatBspQuery.BoxIntersectsCellBsp(
                    flatContainment, min, max);
                comparisons++;
                Assert.True(
                    graphResult == flatResult,
                    $"cell 0x{cellId:X8}, iteration {iteration}: " +
                    $"min={min}, max={max}, graph={graphResult}, flat={flatResult}.");
            }
        }

        Console.WriteLine(
            $"box-differential installed sweep: cellsSwept={cellsSwept} comparisons={comparisons}");
        Assert.Equal(10, cellsSwept);
        Assert.Equal(20_000, comparisons);
    }

    private static void AssertBoxEqual(
        CellBSPNode? graph,
        FlatCellContainmentBsp flat,
        Vector3 min,
        Vector3 max,
        bool expectTrue)
    {
        bool graphResult = BSPQuery.BoxIntersectsCellBsp(graph, min, max);
        bool flatResult = FlatBspQuery.BoxIntersectsCellBsp(flat, min, max);
        Assert.Equal(expectTrue, graphResult);
        Assert.Equal(expectTrue, flatResult);
        Assert.Equal(graphResult, flatResult);
    }

    private static float NextFloat(Random random, float minimum, float maximum)
        => minimum + (float)random.NextDouble() * (maximum - minimum);
}
