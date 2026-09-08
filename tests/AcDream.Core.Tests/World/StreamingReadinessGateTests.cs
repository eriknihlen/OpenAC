using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public class StreamingReadinessGateTests
{
    [Fact]
    public void ShouldStream_OfflineMode_AlwaysTrue()
    {
        // Not live mode at all — no race is possible, no real position to wait for.
        Assert.True(StreamingReadinessGate.ShouldStream(
            liveModeEnabled: false, chaseModeEverEntered: false, liveInWorld: false, liveCenterKnown: false));
    }

    [Fact]
    public void ShouldStream_ChaseModeEverEntered_AlwaysTrueRegardlessOfCenterKnown()
    {
        Assert.True(StreamingReadinessGate.ShouldStream(
            liveModeEnabled: true, chaseModeEverEntered: true, liveInWorld: true, liveCenterKnown: false));
    }

    [Fact]
    public void ShouldStream_LiveModeNotYetInWorld_False()
    {
        Assert.False(StreamingReadinessGate.ShouldStream(
            liveModeEnabled: true, chaseModeEverEntered: false, liveInWorld: false, liveCenterKnown: false));
    }

    [Fact]
    public void ShouldStream_LiveModeInWorldButCenterUnknown_False()
    {
        Assert.False(StreamingReadinessGate.ShouldStream(
            liveModeEnabled: true, chaseModeEverEntered: false, liveInWorld: true, liveCenterKnown: false));
    }

    [Fact]
    public void ShouldStream_LiveModeInWorldAndCenterKnown_True()
    {
        Assert.True(StreamingReadinessGate.ShouldStream(
            liveModeEnabled: true, chaseModeEverEntered: false, liveInWorld: true, liveCenterKnown: true));
    }
}
