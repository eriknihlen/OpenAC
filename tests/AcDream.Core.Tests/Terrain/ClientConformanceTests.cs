using AcDream.Core.Terrain;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Terrain;

public class ClientConformanceTests
{
    // ── Split Direction ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(127, 127)]
    [InlineData(1016, 1016)]
    [InlineData(2039, 2039)]
    [InlineData(500, 1200)]
    [InlineData(1999, 3)]
    public void SplitDirection_MatchesClient(int globalX, int globalY)
    {
        bool clientResult = ClientReference.IsSWtoNECut(globalX, globalY);

        uint lbX = (uint)(globalX / 8);
        uint cx = (uint)(globalX % 8);
        uint lbY = (uint)(globalY / 8);
        uint cy = (uint)(globalY % 8);
        bool acdreamResult = TerrainBlending.CalculateSplitDirection(lbX, cx, lbY, cy)
            == CellSplitDirection.SWtoNE;

        Assert.Equal(clientResult, acdreamResult);
    }

    [Fact]
    public void SplitDirection_MatchesClient_FullSweep()
    {
        int mismatches = 0;
        int tested = 0;

        for (uint lbX = 0; lbX < 255; lbX += 8)
        {
            for (uint lbY = 0; lbY < 255; lbY += 8)
            {
                for (uint cx = 0; cx < 8; cx++)
                {
                    for (uint cy = 0; cy < 8; cy++)
                    {
                        int gx = (int)(lbX * 8 + cx);
                        int gy = (int)(lbY * 8 + cy);

                        bool client = ClientReference.IsSWtoNECut(gx, gy);
                        bool acdream = TerrainBlending.CalculateSplitDirection(lbX, cx, lbY, cy)
                            == CellSplitDirection.SWtoNE;

                        if (client != acdream) mismatches++;
                        tested++;
                    }
                }
            }
        }

        Assert.True(mismatches == 0,
            $"Split direction mismatch: {mismatches} of {tested} cells differ");
    }

    // Also verify TerrainSurface's private IsSplitSWtoNE matches.
    // We test it indirectly via SampleZ on a known asymmetric heightmap.
    [Fact]
    public void SplitDirection_TerrainSurface_AgreesWith_TerrainBlending()
    {
        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;

        heights[0 * 9 + 0] = 0;   // BL
        heights[1 * 9 + 0] = 100; // BR
        heights[0 * 9 + 1] = 0;   // TL
        heights[1 * 9 + 1] = 0;   // TR


        bool clientSplit = ClientReference.IsSWtoNECut(0, 0);
        var surface = new TerrainSurface(heights, heightTable, 0, 0);
        float z = surface.SampleZ(20f, 2f);

        if (clientSplit)
        {
            // SWtoNE → {BL,BR,TR} triangle at (0.833, 0.083) → Z ≈ 75.0
            Assert.InRange(z, 74f, 77f);
        }
        else
        {
            // SEtoNW → {BL,BR,TL} triangle at (0.833, 0.083) → Z ≈ 83.3
            Assert.InRange(z, 82f, 85f);
        }
    }

    // ── PalCode ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, 0, 1, 1, 1, 1)]
    [InlineData(1, 0, 0, 0, 5, 10, 15, 20)]
    [InlineData(3, 3, 3, 3, 31, 31, 31, 31)]
    [InlineData(0, 1, 2, 3, 0, 5, 10, 15)]
    [InlineData(2, 0, 1, 3, 8, 4, 12, 16)]
    public void PalCode_MatchesClient(int r0, int r1, int r2, int r3, int t0, int t1, int t2, int t3)
    {
        uint clientResult = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);
        uint acdreamResult = TerrainBlending.GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);

        Assert.Equal(clientResult, acdreamResult);
    }

    [Fact]
    public void PalCode_MatchesClient_ExhaustiveRoads()
    {
        int mismatches = 0;
        int t0 = 5, t1 = 10, t2 = 15, t3 = 20;

        for (int r0 = 0; r0 <= 3; r0++)
        for (int r1 = 0; r1 <= 3; r1++)
        for (int r2 = 0; r2 <= 3; r2++)
        for (int r3 = 0; r3 <= 3; r3++)
        {
            uint client = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);
            uint acdream = TerrainBlending.GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);
            if (client != acdream) mismatches++;
        }

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void PalCode_MatchesClient_ExhaustiveTypes()
    {
        int mismatches = 0;
        int r0 = 1, r1 = 0, r2 = 2, r3 = 3;

        for (int t0 = 0; t0 < 32; t0++)
        for (int t1 = 0; t1 < 32; t1++)
        for (int t2 = 0; t2 < 32; t2++)
        for (int t3 = 0; t3 < 32; t3++)
        {
            uint client = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);
            uint acdream = TerrainBlending.GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);
            if (client != acdream) mismatches++;
        }

        Assert.Equal(0, mismatches);
    }

    // ── Height Sampling ──────────────────────────────────────────────────

    [Fact]
    public void HeightSampling_FlatTerrain_MatchesClient()
    {
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 2f;

        byte heightByte = 50;
        float clientHeight = ClientReference.GetVertexHeight(heightTable, heightByte);

        var heights = new byte[81];
        Array.Fill(heights, heightByte);
        var surface = new TerrainSurface(heights, heightTable);
        float acdreamHeight = surface.SampleZ(96f, 96f);

        Assert.Equal(clientHeight, acdreamHeight, precision: 3);
    }

    [Fact]
    public void HeightSampling_VertexCorners_MatchClient()
    {
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 2f;

        // Sloped heightmap: height = x*10 + y*5
        var heights = new byte[81];
        for (int x = 0; x <= 8; x++)
        for (int y = 0; y <= 8; y++)
            heights[x * 9 + y] = (byte)(x * 10 + y * 5);

        var surface = new TerrainSurface(heights, heightTable);

        for (int vx = 0; vx <= 8; vx++)
        for (int vy = 0; vy <= 8; vy++)
        {
            byte hByte = heights[vx * 9 + vy];
            float clientH = ClientReference.GetVertexHeight(heightTable, hByte);
            float acdreamH = surface.SampleZ(vx * 24f, vy * 24f);

            Assert.Equal(clientH, acdreamH, precision: 1);
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(12f, 12f)]
    [InlineData(23.9f, 23.9f)]
    [InlineData(48f, 72f)]
    [InlineData(96f, 96f)]
    [InlineData(180f, 180f)]
    public void HeightSampling_InterpolatedPoints_InRange(float localX, float localY)
    {
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 2f;

        var heights = new byte[81];
        for (int x = 0; x <= 8; x++)
        for (int y = 0; y <= 8; y++)
            heights[x * 9 + y] = (byte)(x * 10 + y * 5);

        var surface = new TerrainSurface(heights, heightTable);
        float z = surface.SampleZ(localX, localY);

        float minH = heightTable[0];
        float maxH = heightTable[heights.Max()];

        Assert.InRange(z, minH, maxH);
    }


    [Fact]
    public void Constants_MatchClient()
    {
        // TerrainSurface uses these internally; verify they match.
        Assert.Equal(ClientReference.CellSize, 24f);
        Assert.Equal(ClientReference.CellsPerBlock, 8);
        Assert.Equal(ClientReference.BlockLength, 192f);
    }
}
