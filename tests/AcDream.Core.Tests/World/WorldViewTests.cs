using AcDream.Core.World;

namespace AcDream.Core.Tests.World;

public class WorldViewTests
{
    [Fact]
    public void NeighborIds_Center_Returns9Ids()
    {
        var ids = WorldView.NeighborLandblockIds(0xA9B4FFFFu).ToList();

        Assert.Equal(9, ids.Count);
        Assert.Contains(0xA9B4FFFFu, ids);
        Assert.Contains(0xA8B3FFFFu, ids);  // NW
        Assert.Contains(0xAAB5FFFFu, ids);  // SE
    }

    [Fact]
    public void NeighborIds_LowerEdge_ClampsUnderflow()
    {
        var ids = WorldView.NeighborLandblockIds(0x0000FFFFu).ToList();

        Assert.Equal(4, ids.Count);
        Assert.Contains(0x0000FFFFu, ids);
        Assert.Contains(0x0100FFFFu, ids);
        Assert.Contains(0x0001FFFFu, ids);
        Assert.Contains(0x0101FFFFu, ids);
    }

    [Fact]
    public void NeighborIds_UpperEdge_ClampsOverflow()
    {
        var ids = WorldView.NeighborLandblockIds(0xFFFFFFFFu).ToList();

        Assert.Equal(4, ids.Count);
        Assert.Contains(0xFFFFFFFFu, ids);
        Assert.Contains(0xFEFFFFFFu, ids);
        Assert.Contains(0xFFFEFFFFu, ids);
        Assert.Contains(0xFEFEFFFFu, ids);
    }
}
