using AcDream.Core.World;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.World;

public class SceneryGeneratorTests
{
    // Terrain word layout (ushort):
    //   bits 0-1  = Road  (non-zero → on a road)
    //   bits 2-6  = TerrainType
    //   bits 11-15 = SceneType

    [Theory]
    [InlineData(0x0000, false)]  // no road bits
    [InlineData(0x0001, true)]   // road bit 0 set
    [InlineData(0x0002, true)]   // road bit 1 set
    [InlineData(0x0003, true)]   // both road bits set
    [InlineData(0x007C, false)]  // terrain type bits only, no road
    [InlineData(0xF800, false)]  // scenery bits only, no road
    [InlineData(0xF803, true)]   // road + scenery bits
    public void IsRoadVertex_CorrectlyIdentifiesRoadBits(ushort raw, bool expectedIsRoad)
    {
        Assert.Equal(expectedIsRoad, SceneryGenerator.IsRoadVertex(raw));
    }

    [Fact]
    public void IsRoadVertex_ZeroTerrain_IsNotRoad()
    {
        Assert.False(SceneryGenerator.IsRoadVertex(0));
    }

    [Fact]
    public void IsRoadVertex_MatchesTerrainInfoRoadProperty()
    {
        for (ushort raw = 0; raw < 4; raw++)
        {
            TerrainInfo ti = raw;
            bool expectedFromStruct = ti.Road != 0;
            bool actual = SceneryGenerator.IsRoadVertex(raw);
            Assert.True(actual == expectedFromStruct,
                $"raw=0x{raw:X4}: IsRoadVertex={actual} but TerrainInfo.Road={ti.Road}");
        }
    }
}
