using AcDream.App.Streaming;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingRegionTests
{
    [Fact]
    public void Constructor_Radius2_Produces25Landblocks()
    {
        var region = new StreamingRegion(cx: 50, cy: 50, radius: 2);

        Assert.Equal(25, region.Visible.Count);
    }

    [Fact]
    public void Constructor_NearOrigin_ClampsToWorldEdge()
    {
        var region = new StreamingRegion(cx: 0, cy: 0, radius: 2);

        Assert.Equal(9, region.Visible.Count);
    }

    [Fact]
    public void Constructor_NearFarEdge_ClampsToWorldEdge()
    {
        var region = new StreamingRegion(cx: 0xFF, cy: 0xFF, radius: 2);

        Assert.Equal(9, region.Visible.Count);
    }

    [Fact]
    public void RecenterTo_SamePosition_EmptyDiff()
    {
        var region = new StreamingRegion(cx: 50, cy: 50, radius: 2);

        var diff = region.RecenterToSingleTier(50, 50);

        Assert.Empty(diff.ToLoad);
        Assert.Empty(diff.ToUnload);
    }

    [Fact]
    public void RecenterTo_SingleStepEast_LoadsColumn_NoUnloadsDueToHysteresis()
    {
        var region = new StreamingRegion(cx: 50, cy: 50, radius: 2);

        var diff = region.RecenterToSingleTier(51, 50);

        Assert.Equal(5, diff.ToLoad.Count);
        Assert.Empty(diff.ToUnload);
        // Visible is strictly the 5×5 window around (51, 50).
        Assert.Equal(25, region.Visible.Count);
        Assert.Equal(30, region.Resident.Count);
    }

    [Fact]
    public void RecenterTo_ThreeStepEast_LoadsAndUnloadsColumns()
    {
        var region = new StreamingRegion(cx: 50, cy: 50, radius: 2);

        var diff = region.RecenterToSingleTier(53, 50);

        Assert.Equal(15, diff.ToLoad.Count);
        Assert.Equal(5, diff.ToUnload.Count);
    }

    [Fact]
    public void RecenterTo_LongTeleport_UnloadsEverythingLoadsEverything()
    {
        var region = new StreamingRegion(cx: 50, cy: 50, radius: 2);

        var diff = region.RecenterToSingleTier(200, 200);

        Assert.Equal(25, diff.ToLoad.Count);
        Assert.Equal(25, diff.ToUnload.Count);
    }

    [Fact]
    public void Constructor_NearXEdgeOnly_ClampsOnlyXAxis()
    {
        var region = new StreamingRegion(cx: 0, cy: 50, radius: 2);

        Assert.Equal(15, region.Visible.Count);
    }

    [Fact]
    public void Constructor_SmallRadius_IDsMatchEncodingRule()
    {
        var region = new StreamingRegion(cx: 0x12, cy: 0x34, radius: 0);

        Assert.Single(region.Visible);
        Assert.Contains(0x1234FFFFu, region.Visible);
    }
}
