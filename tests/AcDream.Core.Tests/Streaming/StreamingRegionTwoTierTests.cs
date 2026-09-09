using AcDream.App.Streaming;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingRegionTwoTierTests
{
    [Fact]
    public void Constructor_TwoRadii_ExposesNearAndFarRadii()
    {
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 4, farRadius: 12);

        Assert.Equal(4, region.NearRadius);
        Assert.Equal(12, region.FarRadius);
        Assert.Equal(100, region.CenterX);
        Assert.Equal(100, region.CenterY);
        Assert.Equal(region.FarRadius, region.Radius);
    }

    [Fact]
    public void ComputeFirstTickDiff_FirstTick_SplitsLoadIntoNearAndFar()
    {
        // near=1, far=3 → near window is 3×3=9, far window is 7×7-3×3=40 LBs.
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 1, farRadius: 3);
        var diff = region.ComputeFirstTickDiff();

        Assert.Equal(9,  diff.ToLoadNear.Count);
        Assert.Equal(40, diff.ToLoadFar.Count);
        Assert.Empty(diff.ToPromote);
        Assert.Empty(diff.ToDemote);
        Assert.Empty(diff.ToUnload);
    }

    [Fact]
    public void RecenterTo_PlayerWalks_NullToFar_AppearsInToLoadFar()
    {
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 1, farRadius: 3);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        var diff = region.RecenterTo(newCx: 101, newCy: 100);

        foreach (var y in new[] { 97, 98, 99, 100, 101, 102, 103 })
        {
            var id = StreamingRegion.EncodeLandblockIdForTest(104, y);
            Assert.Contains(id, diff.ToLoadFar);
        }
        Assert.Empty(diff.ToLoadNear);
        Assert.Equal(3, diff.ToPromote.Count);
        Assert.Empty(diff.ToUnload);
    }

    [Fact]
    public void RecenterTo_PlayerWalks_FarToNear_AppearsInToPromote()
    {
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 1, farRadius: 3);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        var diff = region.RecenterTo(newCx: 102, newCy: 100);

        var promotedId = StreamingRegion.EncodeLandblockIdForTest(102, 100);
        Assert.Contains(promotedId, diff.ToPromote);
        Assert.DoesNotContain(promotedId, diff.ToLoadNear);
        Assert.DoesNotContain(promotedId, diff.ToLoadFar);
    }

    [Fact]
    public void RecenterTo_PlayerTeleports_NullToNear_AppearsInToLoadNear()
    {
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 1, farRadius: 3);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        // Teleport to (200, 200) — entirely new region.
        var diff = region.RecenterTo(newCx: 200, newCy: 200);

        Assert.Equal(9,  diff.ToLoadNear.Count);
        Assert.Equal(40, diff.ToLoadFar.Count);
        Assert.Empty(diff.ToPromote);
    }

    [Fact]
    public void RecenterTo_NearToFar_DemoteOnlyAfterHysteresis()
    {
        // near=2, far=4 → near hysteresis threshold = 4.
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 2, farRadius: 4);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        // LB (100,100) was Near. Walk 3 east → distance 3 > NearRadius=2 but ≤ 4. No demote yet.
        var diff1 = region.RecenterTo(newCx: 103, newCy: 100);
        var lb100 = StreamingRegion.EncodeLandblockIdForTest(100, 100);
        Assert.DoesNotContain(lb100, diff1.ToDemote);

        // Walk 2 more east → distance 5 > 4. Demote.
        var diff2 = region.RecenterTo(newCx: 105, newCy: 100);
        Assert.Contains(lb100, diff2.ToDemote);
    }

    [Fact]
    public void RecenterTo_FarToNull_UnloadOnlyAfterHysteresis()
    {
        var region = new StreamingRegion(centerX: 100, centerY: 100, nearRadius: 1, farRadius: 3);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        // LB (97, 100) was at distance 3 (Far). Walk 1 east → distance 4. ≤ FarRadius+2=5.
        var diff1 = region.RecenterTo(newCx: 101, newCy: 100);
        var lb97 = StreamingRegion.EncodeLandblockIdForTest(97, 100);
        Assert.DoesNotContain(lb97, diff1.ToUnload);

        // Walk 2 more east → distance 6 > 5. Unload.
        var diff2 = region.RecenterTo(newCx: 103, newCy: 100);
        Assert.Contains(lb97, diff2.ToUnload);
    }

    [Fact]
    public void RecenterTo_PlayerOscillates_NoThrashWithinHysteresis()
    {
        var region = new StreamingRegion(centerX: 103, centerY: 100, nearRadius: 2, farRadius: 4);
        _ = region.ComputeFirstTickDiff();
        region.MarkResidentFromBootstrap();

        // Bounce between (103,100) and (102,100). All resident LBs stay
        // within the hysteresis window — no demotes expected.
        int totalDemotes  = 0;
        int totalPromotes = 0;
        for (int i = 0; i < 5; i++)
        {
            var d1 = region.RecenterTo(102, 100);
            totalDemotes  += d1.ToDemote.Count;
            totalPromotes += d1.ToPromote.Count;
            var d2 = region.RecenterTo(103, 100);
            totalDemotes  += d2.ToDemote.Count;
            totalPromotes += d2.ToPromote.Count;
        }

        Assert.Equal(0, totalDemotes);
        Assert.True(totalPromotes <= 5,
            $"Expected ≤5 promotes across 5 oscillations; got {totalPromotes}");
    }
}
