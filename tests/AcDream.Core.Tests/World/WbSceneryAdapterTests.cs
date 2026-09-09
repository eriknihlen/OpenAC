using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.World;

public class WbSceneryAdapterTests
{
    [Fact]
    public void BuildTerrainEntries_PreservesRoadTextureSceneryHeight()
    {
        var block = new LandBlock();

        // Vertex 0: road=0x3, type=0x00, scenery=0x1F, height=42
        // raw layout: bits 0-1=11, bits 2-6=00000, bits 7-10=0000, bits 11-15=11111
        // = 0xF803
        block.Terrain[0] = (TerrainInfo)0xF803;
        block.Height[0]  = 42;

        // Vertex 80: road=0x0, type=0x1F, scenery=0x00, height=200
        // raw layout: bits 0-1=00, bits 2-6=11111, bits 11-15=00000
        // = 0x007C
        block.Terrain[80] = (TerrainInfo)0x007C;
        block.Height[80]  = 200;

        var entries = WbSceneryAdapter.BuildTerrainEntries(block);

        Assert.Equal(81, entries.Length);

        Assert.Equal((byte)42,   entries[0].Height);
        Assert.Equal((byte)0x3,  entries[0].Road);
        Assert.Equal((byte)0x00, entries[0].Type);
        Assert.Equal((byte)0x1F, entries[0].Scenery);

        Assert.Equal((byte)200,  entries[80].Height);
        Assert.Equal((byte)0x0,  entries[80].Road);
        Assert.Equal((byte)0x1F, entries[80].Type);
        Assert.Equal((byte)0x00, entries[80].Scenery);
    }

    [Fact]
    public void BuildTerrainEntries_AllZeros_ProducesEmptyEntries()
    {
        var block = new LandBlock();
        var entries = WbSceneryAdapter.BuildTerrainEntries(block);
        Assert.All(entries, e =>
        {
            Assert.Equal((byte)0, e.Height);
            Assert.Equal((byte)0, e.Road);
            Assert.Equal((byte)0, e.Type);
            Assert.Equal((byte)0, e.Scenery);
        });
    }

    [Fact]
    public void BuildTerrainEntries_NullBlock_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WbSceneryAdapter.BuildTerrainEntries(null!));
    }
}
