using AcDream.App.Net;

namespace AcDream.App.Tests.Net;

public sealed class StaminaExhaustionEdgeTrackerTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public void FirstSample_EstablishesBaselineWithoutNotification(int stamina)
    {
        var tracker = new StaminaExhaustionEdgeTracker();

        Assert.False(tracker.Observe(stamina));
    }

    [Fact]
    public void RepeatedStatsTicks_DoNotRedispatchMovement()
    {
        var tracker = new StaminaExhaustionEdgeTracker();

        Assert.False(tracker.Observe(100));
        Assert.False(tracker.Observe(99));
        Assert.False(tracker.Observe(50));
        Assert.False(tracker.Observe(1));
    }

    [Fact]
    public void ExhaustedStateTransitions_ReportExactlyOncePerEdge()
    {
        var tracker = new StaminaExhaustionEdgeTracker();

        Assert.False(tracker.Observe(50));
        Assert.True(tracker.Observe(0));
        Assert.False(tracker.Observe(0));
        Assert.True(tracker.Observe(1));
        Assert.False(tracker.Observe(80));
    }

    [Fact]
    public void Reset_PreventsCrossGenerationSyntheticEdge()
    {
        var tracker = new StaminaExhaustionEdgeTracker();
        Assert.False(tracker.Observe(50));
        Assert.True(tracker.Observe(0));

        tracker.Reset();

        Assert.False(tracker.Observe(75));
        Assert.True(tracker.Observe(0));
    }
}
