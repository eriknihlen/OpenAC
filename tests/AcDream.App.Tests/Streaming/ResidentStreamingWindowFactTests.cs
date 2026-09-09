using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class ResidentStreamingWindowFactTests
{
    [Fact]
    public void CaptureUsesOnlyLargestCompleteActuallyPublishedWindow()
    {
        var state = new GpuWorldState();
        AddSquare(state, centerX: 100, centerY: 101, radius: 1);
        ResidentStreamingWindowFact first =
            state.CaptureResidentStreamingWindow(100, 101);
        Assert.True(first.HasPublishedCenter);
        Assert.Equal(1, first.CompleteRadiusLandblocks);
        Assert.Equal(192f, first.MaximumReachMeters);

        // A retained outer ring with one unpublished gap is not a safe shadow
        // reach even though most of its landblocks are live.
        AddRing(state, 100, 101, radius: 2, skipX: 102, skipY: 101);
        ResidentStreamingWindowFact incomplete =
            state.CaptureResidentStreamingWindow(100, 101);
        Assert.Equal(1, incomplete.CompleteRadiusLandblocks);
        Assert.Equal(192f, incomplete.MaximumReachMeters);

        Add(state, 102, 101);
        ResidentStreamingWindowFact complete =
            state.CaptureResidentStreamingWindow(100, 101);
        Assert.Equal(2, complete.CompleteRadiusLandblocks);
        Assert.Equal(384f, complete.MaximumReachMeters);
    }

    [Fact]
    public void NearToFarDemotionRetainsTerrainReachAndDoesNotRepublishTheWindow()
    {
        var state = new GpuWorldState();
        AddSquare(state, centerX: 40, centerY: 50, radius: 1);
        ResidentStreamingWindowFact before =
            state.CaptureResidentStreamingWindow(40, 50);

        GpuLandblockRetirement? retirement = state.DetachNearLayer(
            StreamingRegion.EncodeLandblockId(41, 50));
        ResidentStreamingWindowFact after =
            state.CaptureResidentStreamingWindow(40, 50);

        Assert.NotNull(retirement);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.MaximumReachMeters, after.MaximumReachMeters);
        Assert.Equal(before.PublishedLandblockCount, after.PublishedLandblockCount);
    }

    [Fact]
    public void RecenterAndPortalGenerationTurnoverCannotReusePriorReach()
    {
        var state = new GpuWorldState();
        AddSquare(state, centerX: 12, centerY: 20, radius: 2);
        ResidentStreamingWindowFact oldGeneration =
            state.CaptureResidentStreamingWindow(12, 20);
        Assert.Equal(384f, oldGeneration.MaximumReachMeters);

        _ = state.DetachAllForOriginRecenter();
        ResidentStreamingWindowFact betweenGenerations =
            state.CaptureResidentStreamingWindow(12, 20);
        Assert.False(betweenGenerations.HasPublishedCenter);
        Assert.Equal(0f, betweenGenerations.MaximumReachMeters);
        Assert.True(betweenGenerations.Revision > oldGeneration.Revision);

        Add(state, 220, 221);
        ResidentStreamingWindowFact newGeneration =
            state.CaptureResidentStreamingWindow(220, 221);
        Assert.True(newGeneration.HasPublishedCenter);
        Assert.Equal(0, newGeneration.CompleteRadiusLandblocks);
        Assert.Equal(0f, newGeneration.MaximumReachMeters);
        Assert.True(newGeneration.Revision > betweenGenerations.Revision);
        Assert.False(state.CaptureResidentStreamingWindow(12, 20).HasPublishedCenter);
    }

    [Fact]
    public void RemovingOneResidentEdgeImmediatelyShrinksTheReadOnlyReach()
    {
        var state = new GpuWorldState();
        AddSquare(state, centerX: 80, centerY: 90, radius: 2);
        ResidentStreamingWindowFact before =
            state.CaptureResidentStreamingWindow(80, 90);

        state.RemoveLandblock(StreamingRegion.EncodeLandblockId(82, 90));
        ResidentStreamingWindowFact after =
            state.CaptureResidentStreamingWindow(80, 90);

        Assert.True(after.Revision > before.Revision);
        Assert.Equal(1, after.CompleteRadiusLandblocks);
        Assert.Equal(192f, after.MaximumReachMeters);
    }

    private static void AddSquare(
        GpuWorldState state,
        int centerX,
        int centerY,
        int radius)
    {
        for (int x = centerX - radius; x <= centerX + radius; x++)
        {
            for (int y = centerY - radius; y <= centerY + radius; y++)
                Add(state, x, y);
        }
    }

    private static void AddRing(
        GpuWorldState state,
        int centerX,
        int centerY,
        int radius,
        int skipX,
        int skipY)
    {
        for (int x = centerX - radius; x <= centerX + radius; x++)
        {
            for (int y = centerY - radius; y <= centerY + radius; y++)
            {
                if (Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)) != radius
                    || (x == skipX && y == skipY))
                {
                    continue;
                }
                Add(state, x, y);
            }
        }
    }

    private static void Add(GpuWorldState state, int x, int y)
    {
        uint landblockId = StreamingRegion.EncodeLandblockId(x, y);
        if (state.TryGetLandblock(landblockId, out _))
            return;
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
    }
}
