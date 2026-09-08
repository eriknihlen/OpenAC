using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class TerrainSurfaceTests
{
    // A height table where index N maps to N * 1.0f (linear).
    // Makes test assertions predictable: height byte 10 → Z = 10.0.
    private static float[] LinearHeightTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = i * 1.0f;
        return table;
    }

    // A flat heightmap where every vertex is height byte 50.
    private static byte[] FlatHeightmap(byte value = 50)
    {
        var heights = new byte[81];
        Array.Fill(heights, value);
        return heights;
    }

    [Fact]
    public void SampleZ_FlatTerrain_ReturnsSameValueEverywhere()
    {
        var surface = new TerrainSurface(FlatHeightmap(50), LinearHeightTable());

        Assert.Equal(50f, surface.SampleZ(0f, 0f));
        Assert.Equal(50f, surface.SampleZ(96f, 96f));
        Assert.Equal(50f, surface.SampleZ(191f, 191f));
    }

    [Fact]
    public void SampleZ_SlopeAlongX_InterpolatesLinearly()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)(10 + x * 10);

        var surface = new TerrainSurface(heights, LinearHeightTable());

        // At x=0 (vertex 0): Z = 10
        Assert.Equal(10f, surface.SampleZ(0f, 96f), precision: 1);
        // At x=96 (midpoint, vertex 4): Z = 50
        Assert.Equal(50f, surface.SampleZ(96f, 96f), precision: 1);
        // At x=192 (vertex 8): Z = 90
        Assert.Equal(90f, surface.SampleZ(192f, 96f), precision: 1);
        // At x=48 (between vertex 2 and 3): Z = 30 + 0.5 * 10 = 35
        // vertex 2 = byte 30, vertex 3 = byte 40, midpoint = 35
        Assert.Equal(35f, surface.SampleZ(60f, 96f), precision: 1);
    }

    [Fact]
    public void SampleZ_ClampsOutOfBounds()
    {
        var surface = new TerrainSurface(FlatHeightmap(42), LinearHeightTable());

        Assert.Equal(42f, surface.SampleZ(-10f, -10f));
        Assert.Equal(42f, surface.SampleZ(300f, 300f));
    }

    [Fact]
    public void SampleZFromHeightmap_AgreesWithInstance_AcrossWholeLandblock()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)((x * 17 + y * 13) % 256);

        var hTable = LinearHeightTable();

        const uint lbX = 0xA9, lbY = 0xB3;
        var instance = new TerrainSurface(heights, hTable, lbX, lbY);

        for (float lx = 0.5f; lx < 192f; lx += 5f)
            for (float ly = 0.5f; ly < 192f; ly += 5f)
            {
                float instanceZ = instance.SampleZ(lx, ly);
                float staticZ = TerrainSurface.SampleZFromHeightmap(
                    heights, hTable, lbX, lbY, lx, ly);
                Assert.True(
                    Math.Abs(instanceZ - staticZ) < 0.0001f,
                    $"Z mismatch at ({lx:F1},{ly:F1}) lb=(0x{lbX:X},0x{lbY:X}): instance={instanceZ:F4} static={staticZ:F4}");
            }
    }

    [Fact]
    public void SampleZFromHeightmap_RejectsBadInputs()
    {
        var goodHeights = new byte[81];
        var goodTable = LinearHeightTable();

        Assert.Throws<ArgumentNullException>(() =>
            TerrainSurface.SampleZFromHeightmap(null!, goodTable, 0, 0, 0f, 0f));
        Assert.Throws<ArgumentNullException>(() =>
            TerrainSurface.SampleZFromHeightmap(goodHeights, null!, 0, 0, 0f, 0f));
        Assert.Throws<ArgumentException>(() =>
            TerrainSurface.SampleZFromHeightmap(new byte[80], goodTable, 0, 0, 0f, 0f));
        Assert.Throws<ArgumentException>(() =>
            TerrainSurface.SampleZFromHeightmap(goodHeights, new float[255], 0, 0, 0f, 0f));
    }

    [Fact]
    public void SampleSurfacePolygon_ReturnsContainingTriangleVertices()
    {
        var heights = FlatHeightmap(50);
        var surface = new TerrainSurface(heights, LinearHeightTable(), landblockX: 0, landblockY: 0);

        var sample = surface.SampleSurfacePolygon(2f, 2f);

        Assert.Equal(3, sample.Vertices.Length);
        for (int i = 0; i < sample.Vertices.Length; i++)
            Assert.Equal(50f, sample.Vertices[i].Z);
        Assert.Equal(1f, sample.Normal.Z, precision: 3);
        Assert.True(
            sample.Vertices.V0.X == 0f && sample.Vertices.V0.Y == 0f
            || sample.Vertices.V1.X == 0f && sample.Vertices.V1.Y == 0f
            || sample.Vertices.V2.X == 0f && sample.Vertices.V2.Y == 0f);
    }

    [Fact]
    public void SampleNormalZFromHeightmap_FlatTerrain_ReturnsOne()
    {
        var heights = FlatHeightmap(50);
        var hTable = LinearHeightTable();
        float nz = TerrainSurface.SampleNormalZFromHeightmap(heights, hTable, 0, 0, 96f, 96f);
        Assert.Equal(1f, nz, precision: 5);
    }

    [Fact]
    public void SampleNormalZFromHeightmap_SlopedTerrain_ReturnsLessThanOne()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)(x * 20);
        var hTable = LinearHeightTable();
        float nz = TerrainSurface.SampleNormalZFromHeightmap(heights, hTable, 0, 0, 12f, 12f);
        Assert.True(nz < 1f, $"Sloped terrain should have nz < 1.0, got {nz}");
        Assert.True(nz > 0f, $"nz should be positive, got {nz}");
    }

    [Fact]
    public void SampleNormalZFromHeightmap_AgreesWithSampleSurface()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)((x * 17 + y * 13) % 256);

        var hTable = LinearHeightTable();
        const uint lbX = 0xA9, lbY = 0xB3;
        var instance = new TerrainSurface(heights, hTable, lbX, lbY);

        for (float lx = 0.5f; lx < 192f; lx += 8f)
            for (float ly = 0.5f; ly < 192f; ly += 8f)
            {
                var (_, normal) = instance.SampleSurface(lx, ly);
                float staticNz = TerrainSurface.SampleNormalZFromHeightmap(
                    heights, hTable, lbX, lbY, lx, ly);
                Assert.True(
                    Math.Abs(normal.Z - staticNz) < 0.0001f,
                    $"NormalZ mismatch at ({lx:F1},{ly:F1}): instance={normal.Z:F4} static={staticNz:F4}");
            }
    }

    [Fact]
    public void ComputeOutdoorCellId_Origin_ReturnsFirst()
    {
        var surface = new TerrainSurface(FlatHeightmap(), LinearHeightTable());

        Assert.Equal(0x0001u, surface.ComputeOutdoorCellId(0f, 0f));
    }

    [Fact]
    public void ComputeOutdoorCellId_SecondColumn_ReturnsCorrect()
    {
        var surface = new TerrainSurface(FlatHeightmap(), LinearHeightTable());

        Assert.Equal(0x0009u, surface.ComputeOutdoorCellId(24f, 0f));
    }

    [Fact]
    public void ComputeOutdoorCellId_LastCell_Returns0x0040()
    {
        var surface = new TerrainSurface(FlatHeightmap(), LinearHeightTable());

        Assert.Equal(0x0040u, surface.ComputeOutdoorCellId(191f, 191f));
    }
}
