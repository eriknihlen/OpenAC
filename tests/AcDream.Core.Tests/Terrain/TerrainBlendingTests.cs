using AcDream.Core.Terrain;
using Xunit;

namespace AcDream.Core.Tests.Terrain;

public class TerrainBlendingTests
{
    private const int Grass = 1;
    private const int Dirt = 4;

    [Fact]
    public void GetPalCode_AllGrassNoRoads_ProducesHandComputedValue()
    {
        uint actual = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Grass, Grass);
        Assert.Equal(0x10008421u, actual);
    }

    [Fact]
    public void GetPalCode_AllZeros_ProducesOnlySizeBit()
    {
        uint actual = TerrainBlending.GetPalCode(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0x10000000u, actual);
    }

    [Fact]
    public void GetPalCode_IsDeterministic()
    {
        uint a = TerrainBlending.GetPalCode(1, 0, 0, 1, Grass, Dirt, Grass, Dirt);
        uint b = TerrainBlending.GetPalCode(1, 0, 0, 1, Grass, Dirt, Grass, Dirt);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetPalCode_DifferentTerrainCornersProduceDifferentCodes()
    {
        uint allGrass = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Grass, Grass);
        uint grassDirtMix = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Dirt, Grass, Dirt);
        Assert.NotEqual(allGrass, grassDirtMix);
    }

    [Fact]
    public void GetPalCode_RoadFlagAffectsOutput()
    {
        uint noRoad = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Grass, Grass);
        uint withRoad = TerrainBlending.GetPalCode(1, 0, 0, 0, Grass, Grass, Grass, Grass);
        Assert.NotEqual(noRoad, withRoad);
        Assert.Equal(1u << 26, noRoad ^ withRoad);
    }

    [Fact]
    public void GetPalCode_SizeBitAlwaysSet()
    {
        uint code = TerrainBlending.GetPalCode(3, 3, 3, 3, Dirt, Dirt, Dirt, Dirt);
        Assert.Equal(1u << 28, code & (1u << 28));
    }

    [Fact]
    public void GetPalCode_TerrainBitsOccupyLowFifteen()
    {
        uint code = TerrainBlending.GetPalCode(0, 0, 0, 0, 31, 31, 31, 31);
        // Nothing between bits 20-27 should be set.
        uint forbiddenRegion = code & 0x0FF00000u;
        Assert.Equal(0u, forbiddenRegion);
    }

    [Fact]
    public void CalculateSplitDirection_OriginCell_IsSWtoNE()
    {
        var actual = TerrainBlending.CalculateSplitDirection(0, 0, 0, 0);
        Assert.Equal(CellSplitDirection.SWtoNE, actual);
    }

    [Fact]
    public void CalculateSplitDirection_IsDeterministic()
    {
        var a = TerrainBlending.CalculateSplitDirection(0xA9, 3, 0xB4, 5);
        var b = TerrainBlending.CalculateSplitDirection(0xA9, 3, 0xB4, 5);
        Assert.Equal(a, b);
    }

    [Fact]
    public void CalculateSplitDirection_DifferentCellsCanDiffer()
    {
        int swCount = 0, seCount = 0;
        for (uint cx = 0; cx < 8; cx++)
        for (uint cy = 0; cy < 8; cy++)
        {
            var dir = TerrainBlending.CalculateSplitDirection(0xA9, cx, 0xB4, cy);
            if (dir == CellSplitDirection.SWtoNE) swCount++; else seCount++;
        }
        Assert.True(swCount > 0, "Expected at least one SWtoNE cell in a landblock");
        Assert.True(seCount > 0, "Expected at least one SEtoNW cell in a landblock");
    }


    [Fact]
    public void ExtractTerrainCodes_InverseOfGetPalCode()
    {
        uint palCode = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Dirt, Grass, Dirt);
        uint[] extracted = TerrainBlending.ExtractTerrainCodes(palCode);
        Assert.Equal(new uint[] { Grass, Dirt, Grass, Dirt }, extracted);
    }

    [Fact]
    public void RotateTerrainCode_CyclesThroughSingleCornerPatterns()
    {
        Assert.Equal(2u, TerrainBlending.RotateTerrainCode(1));
        Assert.Equal(4u, TerrainBlending.RotateTerrainCode(2));
        Assert.Equal(8u, TerrainBlending.RotateTerrainCode(4));
        Assert.Equal(1u, TerrainBlending.RotateTerrainCode(8));  // 16 → -15 = 1
    }

    [Fact]
    public void RotateTerrainCode_HandlesMultiCornerPatterns()
    {
        Assert.Equal(6u, TerrainBlending.RotateTerrainCode(3));
        Assert.Equal(12u, TerrainBlending.RotateTerrainCode(6));
        Assert.Equal(9u, TerrainBlending.RotateTerrainCode(12));
    }

    [Fact]
    public void GetRoadCodes_NoRoad_IsAllZero()
    {
        uint pal = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Grass, Grass);
        var (r0, r1, allRoad) = TerrainBlending.GetRoadCodes(pal);
        Assert.Equal(0u, r0);
        Assert.Equal(0u, r1);
        Assert.False(allRoad);
    }

    [Fact]
    public void GetRoadCodes_AllFourRoads_IsAllRoad()
    {
        uint pal = TerrainBlending.GetPalCode(1, 1, 1, 1, Grass, Grass, Grass, Grass);
        var (_, _, allRoad) = TerrainBlending.GetRoadCodes(pal);
        Assert.True(allRoad);
    }

    [Fact]
    public void GetRoadCodes_SingleCornerRoad_MapsToMaskBit()
    {
        uint pal = TerrainBlending.GetPalCode(1, 0, 0, 0, Grass, Grass, Grass, Grass);
        var (r0, r1, allRoad) = TerrainBlending.GetRoadCodes(pal);
        Assert.Equal(1u, r0);
        Assert.Equal(0u, r1);
        Assert.False(allRoad);
    }

    [Fact]
    public void PseudoRandomIndex_ReturnsValueInRange()
    {
        for (int seed = 1; seed < 20; seed++)
        {
            int v = TerrainBlending.PseudoRandomIndex((uint)seed * 1000u, 5);
            Assert.InRange(v, 0, 4);
        }
    }

    [Fact]
    public void PseudoRandomIndex_CountZero_ReturnsZero()
    {
        Assert.Equal(0, TerrainBlending.PseudoRandomIndex(0x12345678u, 0));
    }

    [Fact]
    public void PseudoRandomIndex_IsDeterministic()
    {
        uint pal = 0xDEADBEEFu;
        int a = TerrainBlending.PseudoRandomIndex(pal, 4);
        int b = TerrainBlending.PseudoRandomIndex(pal, 4);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildSurface_AllGrassNoRoads_ReturnsBaseOnly()
    {
        var ctx = MakeContext(terrainLayers: new Dictionary<uint, byte>
        {
            [Grass] = 7,
            [Dirt] = 11,
        });
        uint pal = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Grass, Grass);

        var surf = TerrainBlending.BuildSurface(pal, ctx);

        Assert.Equal(7, surf.BaseLayer);
        Assert.Equal(SurfaceInfo.None, surf.Ovl0Layer);
        Assert.Equal(SurfaceInfo.None, surf.Ovl1Layer);
        Assert.Equal(SurfaceInfo.None, surf.Ovl2Layer);
        Assert.Equal(SurfaceInfo.None, surf.RoadLayer);
    }

    [Fact]
    public void BuildSurface_AllRoadsCell_UsesRoadTextureAsBase()
    {
        var ctx = MakeContext(
            terrainLayers: new Dictionary<uint, byte> { [Grass] = 7 },
            roadLayer: 24);
        uint pal = TerrainBlending.GetPalCode(1, 1, 1, 1, Grass, Grass, Grass, Grass);

        var surf = TerrainBlending.BuildSurface(pal, ctx);

        Assert.Equal(24, surf.BaseLayer);
        Assert.Equal(SurfaceInfo.None, surf.Ovl0Layer);
    }

    [Fact]
    public void BuildSurface_TwoGrassTwoDirt_EmitsBaseAndAtLeastOneOverlay()
    {
        var ctx = MakeContext(terrainLayers: new Dictionary<uint, byte>
        {
            [Grass] = 7,
            [Dirt] = 11,
        });
        uint pal = TerrainBlending.GetPalCode(0, 0, 0, 0, Grass, Grass, Dirt, Dirt);

        var surf = TerrainBlending.BuildSurface(pal, ctx);

        // Base must be the duplicate (grass, layer 7).
        Assert.Equal(7, surf.BaseLayer);
        // There must be at least one overlay with the other terrain (dirt, layer 11).
        Assert.Equal(11, surf.Ovl0Layer);
    }

    [Fact]
    public void FillCellData_RoundTripBitLayout()
    {
        var s = new SurfaceInfo(
            BaseLayer: 0x01,
            Ovl0Layer: 0x02, Ovl0AlphaLayer: 0x03, Ovl0Rotation: 1,
            Ovl1Layer: 0x04, Ovl1AlphaLayer: 0x05, Ovl1Rotation: 2,
            Ovl2Layer: 0x06, Ovl2AlphaLayer: 0x07, Ovl2Rotation: 3,
            RoadLayer: 0x08, Road0AlphaLayer: 0x09, Road0Rotation: 1,
            Road1AlphaLayer: 0x0A, Road1Rotation: 2);

        var (d0, d1, d2, d3) = TerrainBlending.FillCellData(s, CellSplitDirection.SEtoNW);

        // data0 low byte = BaseLayer (0x01)
        Assert.Equal(0x01u, d0 & 0xFFu);
        // data0 byte 1 = baseAlpha = 255 (unused slot)
        Assert.Equal(0xFFu, (d0 >> 8) & 0xFFu);
        // data0 byte 2 = Ovl0Layer (0x02)
        Assert.Equal(0x02u, (d0 >> 16) & 0xFFu);
        // data0 byte 3 = Ovl0AlphaLayer (0x03)
        Assert.Equal(0x03u, (d0 >> 24) & 0xFFu);
        // data1: Ovl1Layer, Ovl1AlphaLayer, Ovl2Layer, Ovl2AlphaLayer
        Assert.Equal(0x04u, d1 & 0xFFu);
        Assert.Equal(0x05u, (d1 >> 8) & 0xFFu);
        Assert.Equal(0x06u, (d1 >> 16) & 0xFFu);
        Assert.Equal(0x07u, (d1 >> 24) & 0xFFu);
        // data2: Road0Layer, Road0AlphaLayer, Road1Layer (= RoadLayer because Road1AlphaLayer != None), Road1AlphaLayer
        Assert.Equal(0x08u, d2 & 0xFFu);
        Assert.Equal(0x09u, (d2 >> 8) & 0xFFu);
        Assert.Equal(0x08u, (d2 >> 16) & 0xFFu);  // texRd1 = same road layer
        Assert.Equal(0x0Au, (d2 >> 24) & 0xFFu);
        // data3: rotBase=0 bits 0-1, rotOvl0=1 bits 2-3, rotOvl1=2 bits 4-5,
        // rotOvl2=3 bits 6-7, rotRd0=1 bits 8-9, rotRd1=2 bits 10-11, split=1 bit 12
        uint expectedD3 = (1u << 2) | (2u << 4) | (3u << 6) | (1u << 8) | (2u << 10) | (1u << 12);
        Assert.Equal(expectedD3, d3);
    }

    [Fact]
    public void FillCellData_NoRoad1_LeavesRoad1SlotAs255()
    {
        var s = new SurfaceInfo(
            BaseLayer: 0x01,
            Ovl0Layer: SurfaceInfo.None, Ovl0AlphaLayer: SurfaceInfo.None, Ovl0Rotation: 0,
            Ovl1Layer: SurfaceInfo.None, Ovl1AlphaLayer: SurfaceInfo.None, Ovl1Rotation: 0,
            Ovl2Layer: SurfaceInfo.None, Ovl2AlphaLayer: SurfaceInfo.None, Ovl2Rotation: 0,
            RoadLayer: 0x08, Road0AlphaLayer: 0x09, Road0Rotation: 0,
            Road1AlphaLayer: SurfaceInfo.None, Road1Rotation: 0);

        var (_, _, d2, _) = TerrainBlending.FillCellData(s, CellSplitDirection.SWtoNE);

        // Road0 present, Road1 absent → texRd1 and alphaRd1 both 255.
        Assert.Equal(0x08u, d2 & 0xFFu);
        Assert.Equal(0x09u, (d2 >> 8) & 0xFFu);
        Assert.Equal(0xFFu, (d2 >> 16) & 0xFFu);
        Assert.Equal(0xFFu, (d2 >> 24) & 0xFFu);
    }

    // ---- helpers ----

    private static TerrainBlendingContext MakeContext(
        IReadOnlyDictionary<uint, byte>? terrainLayers = null,
        byte roadLayer = SurfaceInfo.None,
        IReadOnlyList<byte>? cornerAlphaLayers = null,
        IReadOnlyList<byte>? sideAlphaLayers = null,
        IReadOnlyList<byte>? roadAlphaLayers = null,
        IReadOnlyList<uint>? cornerTCodes = null,
        IReadOnlyList<uint>? sideTCodes = null,
        IReadOnlyList<uint>? roadRCodes = null)
    {
        return new TerrainBlendingContext(
            TerrainTypeToLayer: terrainLayers ?? new Dictionary<uint, byte>(),
            RoadLayer: roadLayer,
            CornerAlphaLayers: cornerAlphaLayers ?? new byte[] { 0, 1, 2, 3 },
            SideAlphaLayers:   sideAlphaLayers   ?? new byte[] { 4 },
            RoadAlphaLayers:   roadAlphaLayers   ?? new byte[] { 5, 6, 7 },
            CornerAlphaTCodes: cornerTCodes      ?? new uint[] { 1, 2, 4, 8 },
            SideAlphaTCodes:   sideTCodes        ?? new uint[] { 3 },
            RoadAlphaRCodes:   roadRCodes        ?? new uint[] { 1, 2, 4 });
    }
}
