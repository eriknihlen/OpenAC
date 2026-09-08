using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityLivenessControllerTests
{
    [Fact]
    public void OutOfRangeWorldEntityExpiresAfterTwentyFiveSeconds()
    {
        var tracker = new LiveEntityLivenessTracker();
        var samples = new[] { Sample(0x7000_0001u, generation: 4, visible: false) };

        Assert.Empty(tracker.Tick(10.0, samples));
        Assert.Empty(tracker.Tick(34.999, samples));
        Assert.Equal(
            new LiveEntityPruneCandidate(
                new RuntimeEntityKey(0x7000_0001u, 4),
                0x7000_0001u),
            Assert.Single(tracker.Tick(35.0, samples)));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReturningToVisibilityCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(20.0, [Sample(1, 1, visible: true)]));
        Assert.Empty(tracker.Tick(40.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(64.9, [Sample(1, 1, visible: false)]));
        Assert.Single(tracker.Tick(65.0, [Sample(1, 1, visible: false)]));
    }

    [Fact]
    public void NonWorldRetentionCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(30.0, [Sample(1, 1, visible: false, retained: true)]));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReusedGuidGetsANewGenerationDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(24.0, [Sample(1, 2, visible: false)]));
        Assert.Empty(tracker.Tick(25.0, [Sample(1, 2, visible: false)]));
        Assert.Equal(
            new LiveEntityPruneCandidate(new RuntimeEntityKey(1, 2), 1),
            Assert.Single(tracker.Tick(49.0, [Sample(1, 2, visible: false)])));
    }

    [Fact]
    public void RemovedRecordDropsItsDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));

        Assert.Empty(tracker.Tick(30.0, []));

        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ConservativeVisibilityUsesGlobalLandblockCoordinates()
    {
        CreateObject.ServerPosition player = Position(0x3032_0001u, 190f, 20f, 5f);
        CreateObject.ServerPosition adjacent = Position(0x3132_0001u, 1f, 20f, 5f);
        CreateObject.ServerPosition exactlyTwoBlocks = Position(0x3232_0001u, 190f, 20f, 5f);
        CreateObject.ServerPosition beyond = Position(0x3332_0001u, 1f, 20f, 5f);

        Assert.True(LiveEntityLivenessController.IsWithinConservativeVisibility(player, adjacent));
        Assert.True(LiveEntityLivenessController.IsWithinConservativeVisibility(player, exactlyTwoBlocks));
        Assert.False(LiveEntityLivenessController.IsWithinConservativeVisibility(player, beyond));
    }

    private static LiveEntityLivenessSample Sample(
        uint guid,
        ushort generation,
        bool visible,
        bool retained = false) =>
        new(
            new RuntimeEntityKey(guid, generation),
            guid,
            visible,
            retained);

    private static CreateObject.ServerPosition Position(
        uint cell,
        float x,
        float y,
        float z) =>
        new(cell, x, y, z, 1f, 0f, 0f, 0f);
}
