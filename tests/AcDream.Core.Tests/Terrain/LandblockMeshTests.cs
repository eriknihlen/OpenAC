using System.Numerics;
using AcDream.Core.Terrain;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Terrain;

public class LandblockMeshTests
{
    private static readonly float[] IdentityHeightTable =
        Enumerable.Range(0, 256).Select(i => i * 2f).ToArray();

    private static TerrainBlendingContext MakeContext() => new(
        TerrainTypeToLayer: new Dictionary<uint, byte> { [0u] = 0 },
        RoadLayer: SurfaceInfo.None,
        CornerAlphaLayers: Array.Empty<byte>(),
        SideAlphaLayers:   Array.Empty<byte>(),
        RoadAlphaLayers:   Array.Empty<byte>(),
        CornerAlphaTCodes: Array.Empty<uint>(),
        SideAlphaTCodes:   Array.Empty<uint>(),
        RoadAlphaRCodes:   Array.Empty<uint>());

    private static LandBlock BuildFlatLandBlock(byte heightIndex = 0)
    {
        var block = new LandBlock
        {
            HasObjects = false,
            Terrain = new TerrainInfo[81],
            Height = new byte[81],
        };
        for (int i = 0; i < 81; i++)
        {
            block.Terrain[i] = (ushort)0;
            block.Height[i] = heightIndex;
        }
        return block;
    }

    [Fact]
    public void Build_FlatBlock_Produces384VerticesAnd128Triangles()
    {
        var block = BuildFlatLandBlock();
        var cache = new Dictionary<uint, SurfaceInfo>();

        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        Assert.Equal(384, mesh.Vertices.Length);
        Assert.Equal(128 * 3, mesh.Indices.Length);
    }

    [Fact]
    public void Build_Vertices_CoverExactly192x192WorldUnits()
    {
        var block = BuildFlatLandBlock();
        var cache = new Dictionary<uint, SurfaceInfo>();

        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        var minX = mesh.Vertices.Min(v => v.Position.X);
        var maxX = mesh.Vertices.Max(v => v.Position.X);
        var minY = mesh.Vertices.Min(v => v.Position.Y);
        var maxY = mesh.Vertices.Max(v => v.Position.Y);
        Assert.Equal(0.0f, minX);
        Assert.Equal(192.0f, maxX);
        Assert.Equal(0.0f, minY);
        Assert.Equal(192.0f, maxY);
    }

    [Fact]
    public void Build_FlatBlock_AllVerticesSameZ()
    {
        var block = BuildFlatLandBlock(heightIndex: 10);
        var cache = new Dictionary<uint, SurfaceInfo>();

        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        var zs = mesh.Vertices.Select(v => v.Position.Z).Distinct().ToArray();
        Assert.Single(zs);
        Assert.Equal(20.0f, zs[0]);  // heightIndex 10 × IdentityHeightTable[10] = 20
    }

    [Fact]
    public void Build_FlatBlock_NormalsPointStraightUp()
    {
        var block = BuildFlatLandBlock();
        var cache = new Dictionary<uint, SurfaceInfo>();

        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        foreach (var v in mesh.Vertices)
        {
            Assert.Equal(new Vector3(0, 0, 1), v.Normal);
        }
    }

    [Fact]
    public void Build_AllVerticesOfACellShareIdenticalData()
    {
        var block = BuildFlatLandBlock();
        var cache = new Dictionary<uint, SurfaceInfo>();

        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        for (int cellIdx = 0; cellIdx < 64; cellIdx++)
        {
            int baseIdx = cellIdx * 6;
            var d0 = mesh.Vertices[baseIdx].Data0;
            var d1 = mesh.Vertices[baseIdx].Data1;
            var d2 = mesh.Vertices[baseIdx].Data2;
            var d3 = mesh.Vertices[baseIdx].Data3;
            for (int i = 1; i < 6; i++)
            {
                Assert.Equal(d0, mesh.Vertices[baseIdx + i].Data0);
                Assert.Equal(d1, mesh.Vertices[baseIdx + i].Data1);
                Assert.Equal(d2, mesh.Vertices[baseIdx + i].Data2);
                Assert.Equal(d3, mesh.Vertices[baseIdx + i].Data3);
            }
        }
    }

    [Fact]
    public void Build_SurfaceCacheIsReusedAcrossIdenticalCells()
    {
        var block = BuildFlatLandBlock();
        var cache = new Dictionary<uint, SurfaceInfo>();

        LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        Assert.Single(cache);
    }

    [Fact]
    public void Build_CellsWithDistinctTerrainTypes_ProducesDistinctPaletteCodes()
    {
        var block = BuildFlatLandBlock();
        // Type is at bits 2-6, so type=4 → ushort = (4 << 2) = 0x10.
        block.Terrain[4 * 9 + 4] = (ushort)(4 << 2);

        var ctx = new TerrainBlendingContext(
            TerrainTypeToLayer: new Dictionary<uint, byte> { [0u] = 0, [4u] = 1 },
            RoadLayer: SurfaceInfo.None,
            CornerAlphaLayers: new byte[] { 0, 1, 2, 3 },
            SideAlphaLayers:   Array.Empty<byte>(),
            RoadAlphaLayers:   Array.Empty<byte>(),
            CornerAlphaTCodes: new uint[] { 1, 2, 4, 8 },
            SideAlphaTCodes:   Array.Empty<uint>(),
            RoadAlphaRCodes:   Array.Empty<uint>());
        var cache = new Dictionary<uint, SurfaceInfo>();

        LandblockMesh.Build(block, 0, 0, IdentityHeightTable, ctx, cache);

        Assert.True(cache.Count >= 2, $"Expected mix of palette codes, got {cache.Count}");
    }

    [Fact]
    public void Build_AllTriangles_WindCounterClockwiseInWorldXY()
    {
        var block = BuildFlatLandBlock();
        for (int i = 0; i < 81; i++)
            block.Height[i] = (byte)((i * 37) % 64);   // varied, deterministic slopes

        foreach (var (lbx, lby) in new[] { (0u, 0u), (0xA9u, 0xB4u), (3u, 7u) })
        {
            var cache = new Dictionary<uint, SurfaceInfo>();
            var mesh = LandblockMesh.Build(block, lbx, lby, IdentityHeightTable, MakeContext(), cache);

            for (int t = 0; t < mesh.Indices.Length; t += 3)
            {
                var p0 = mesh.Vertices[mesh.Indices[t + 0]].Position;
                var p1 = mesh.Vertices[mesh.Indices[t + 1]].Position;
                var p2 = mesh.Vertices[mesh.Indices[t + 2]].Position;
                float crossZ = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
                Assert.True(crossZ > 0f,
                    $"lb=({lbx},{lby}) triangle {t / 3} winds CW in world XY (crossZ={crossZ}) — " +
                    "backface culling in TerrainModernRenderer would cull its TOP side");
            }
        }
    }

    [Fact]
    public void Build_HeightmapPackedAsXMajor_NotYMajor()
    {
        var block = BuildFlatLandBlock();
        block.Height[2 * 9 + 0] = 5;  // x=2, y=0 → world (48, 0), Z should be 10

        var cache = new Dictionary<uint, SurfaceInfo>();
        var mesh = LandblockMesh.Build(block, 0, 0, IdentityHeightTable, MakeContext(), cache);

        // Search the vertex buffer for a vertex at world position (48, 0).
        var atX48Y0 = mesh.Vertices.FirstOrDefault(v =>
            Math.Abs(v.Position.X - 48f) < 0.01f && Math.Abs(v.Position.Y) < 0.01f);
        var atX0Y48 = mesh.Vertices.FirstOrDefault(v =>
            Math.Abs(v.Position.X) < 0.01f && Math.Abs(v.Position.Y - 48f) < 0.01f);

        Assert.Equal(10.0f, atX48Y0.Position.Z);
        Assert.Equal(0.0f, atX0Y48.Position.Z);
    }

    [Fact]
    public void Build_NormalsMatchRetailIncidentFaceAverages_NotCentralDifferences()
    {
        var block = BuildFlatLandBlock();
        for (int x = 0; x < LandblockMesh.HeightmapSide; x++)
        for (int y = 0; y < LandblockMesh.HeightmapSide; y++)
            block.Height[x * LandblockMesh.HeightmapSide + y] =
                (byte)((x * x * 3 + y * y * 5 + x * y * 11 + x * 7 + y * 13) % 96);

        const uint landblockX = 0xA9;
        const uint landblockY = 0xB4;
        var mesh = LandblockMesh.Build(
            block,
            landblockX,
            landblockY,
            IdentityHeightTable,
            MakeContext(),
            new Dictionary<uint, SurfaceInfo>());

        var incidentNormalSums = new Dictionary<Vector3, Vector3>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vector3 p0 = mesh.Vertices[mesh.Indices[i]].Position;
            Vector3 p1 = mesh.Vertices[mesh.Indices[i + 1]].Position;
            Vector3 p2 = mesh.Vertices[mesh.Indices[i + 2]].Position;
            Vector3 planeNormal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));

            AddNormal(incidentNormalSums, p0, planeNormal);
            AddNormal(incidentNormalSums, p1, planeNormal);
            AddNormal(incidentNormalSums, p2, planeNormal);
        }

        foreach (TerrainVertex vertex in mesh.Vertices)
        {
            Vector3 expected = Vector3.Normalize(incidentNormalSums[vertex.Position]);
            AssertVectorNear(expected, vertex.Normal, 1e-6f);
            Assert.InRange(vertex.Normal.Length(), 1f - 1e-6f, 1f + 1e-6f);
        }

        bool differsFromCentralDifferences = false;
        for (int x = 0; x < LandblockMesh.HeightmapSide; x++)
        {
            for (int y = 0; y < LandblockMesh.HeightmapSide; y++)
            {
                int xL = Math.Max(x - 1, 0);
                int xR = Math.Min(x + 1, LandblockMesh.HeightmapSide - 1);
                int yD = Math.Max(y - 1, 0);
                int yU = Math.Min(y + 1, LandblockMesh.HeightmapSide - 1);
                float dx = (HeightAt(block, xR, y) - HeightAt(block, xL, y)) /
                           ((xR - xL) * LandblockMesh.CellSize);
                float dy = (HeightAt(block, x, yU) - HeightAt(block, x, yD)) /
                           ((yU - yD) * LandblockMesh.CellSize);
                Vector3 oldApproximation = Vector3.Normalize(new Vector3(-dx, -dy, 1f));
                Vector3 position = new(
                    x * LandblockMesh.CellSize,
                    y * LandblockMesh.CellSize,
                    HeightAt(block, x, y));
                Vector3 actual = mesh.Vertices.First(vertex => vertex.Position == position).Normal;
                differsFromCentralDifferences |= Vector3.Distance(oldApproximation, actual) > 1e-4f;
            }
        }

        Assert.True(
            differsFromCentralDifferences,
            "Synthetic terrain failed to distinguish retail incident-face averaging from central differences.");
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0xA9u, 0xB4u)]
    public void Build_RetailNormalChange_PreservesExactSplitAwarePositionsAndIndices(
        uint landblockX,
        uint landblockY)
    {
        var block = BuildFlatLandBlock();
        for (int x = 0; x < LandblockMesh.HeightmapSide; x++)
        for (int y = 0; y < LandblockMesh.HeightmapSide; y++)
            block.Height[x * LandblockMesh.HeightmapSide + y] =
                (byte)((x * 17 + y * 29 + x * y * 3) % 80);

        var mesh = LandblockMesh.Build(
            block,
            landblockX,
            landblockY,
            IdentityHeightTable,
            MakeContext(),
            new Dictionary<uint, SurfaceInfo>());

        Assert.Equal(
            Enumerable.Range(0, LandblockMesh.VerticesPerLandblock).Select(i => (uint)i),
            mesh.Indices);

        int vertexIndex = 0;
        for (int cy = 0; cy < LandblockMesh.CellsPerSide; cy++)
        {
            for (int cx = 0; cx < LandblockMesh.CellsPerSide; cx++)
            {
                Vector3 bl = PositionAt(block, cx,     cy);
                Vector3 br = PositionAt(block, cx + 1, cy);
                Vector3 tr = PositionAt(block, cx + 1, cy + 1);
                Vector3 tl = PositionAt(block, cx,     cy + 1);
                Vector3[] expected = TerrainBlending.CalculateSplitDirection(
                    landblockX, (uint)cx, landblockY, (uint)cy) == CellSplitDirection.SWtoNE
                    ? [bl, br, tr, bl, tr, tl]
                    : [bl, br, tl, br, tr, tl];

                foreach (Vector3 position in expected)
                    Assert.Equal(position, mesh.Vertices[vertexIndex++].Position);
            }
        }

        Assert.Equal(LandblockMesh.VerticesPerLandblock, vertexIndex);
    }

    private static float HeightAt(LandBlock block, int x, int y) =>
        IdentityHeightTable[block.Height[x * LandblockMesh.HeightmapSide + y]];

    private static Vector3 PositionAt(LandBlock block, int x, int y) => new(
        x * LandblockMesh.CellSize,
        y * LandblockMesh.CellSize,
        HeightAt(block, x, y));

    private static void AddNormal(
        IDictionary<Vector3, Vector3> sums,
        Vector3 position,
        Vector3 normal)
    {
        sums.TryGetValue(position, out Vector3 sum);
        sums[position] = sum + normal;
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }
}
