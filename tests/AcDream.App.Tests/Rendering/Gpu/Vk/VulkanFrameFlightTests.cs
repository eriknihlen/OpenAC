using System;
using System.Collections.Generic;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanFrameFlightTests
{
    private sealed class FakeTimeline : IVulkanTimelineApi
    {
        public ulong Value { get; set; }

        public List<ulong> Waits { get; } = [];

        public ulong CurrentValue => Value;

        public void Wait(ulong value)
        {
            Waits.Add(value);
            // A real wait blocks until the GPU gets there; the fake models a
            // GPU that is always just far enough ahead.
            Value = Math.Max(Value, value);
        }
    }


    [Fact]
    public void Flights_FirstFramesDoNotWaitBecauseNoSlotIsOccupiedYet()
    {
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);

        Assert.Equal(1, flights.BeginFrame());
        flights.EndFrame();
        Assert.Equal(2, flights.BeginFrame());
        flights.EndFrame();

        Assert.Empty(timeline.Waits);
    }

    [Fact]
    public void Flights_ThirdFrameWaitsForTheFirstToComplete()
    {
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);
        flights.BeginFrame();
        flights.EndFrame();
        flights.BeginFrame();
        flights.EndFrame();

        Assert.Equal(3, flights.BeginFrame());

        Assert.Equal([1ul], timeline.Waits);
    }

    [Fact]
    public void Flights_SerialsMapOntoSlotsRoundRobin()
    {
        var flights = new VulkanFrameFlightController(new FakeTimeline(), framesInFlight: 2);

        Assert.Equal(0, flights.SlotIndexOf(1));
        Assert.Equal(1, flights.SlotIndexOf(2));
        Assert.Equal(0, flights.SlotIndexOf(3));
        Assert.Equal(1, flights.SlotIndexOf(4));
    }

    [Fact]
    public void Flights_RetirementIsKeyedToTheOpenFrameNotTheCompletedOne()
    {
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);
        var released = new List<string>();

        flights.BeginFrame();          // serial 1 open
        flights.Retire(() => released.Add("during-1"));
        flights.EndFrame();

        timeline.Value = 0;
        flights.BeginFrame();          // serial 2 open; retirements run here
        Assert.Empty(released);

        flights.EndFrame();
        timeline.Value = 1;
        flights.RunRetirements();

        Assert.Equal(["during-1"], released);
    }

    [Fact]
    public void Flights_ResourcesReleasedBetweenFramesBelongToTheNextFrame()
    {
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);
        var released = new List<string>();

        flights.BeginFrame();
        flights.EndFrame();
        flights.Retire(() => released.Add("between"));

        // Filed against serial 2, which has not even opened.
        timeline.Value = 1;
        flights.RunRetirements();
        Assert.Empty(released);

        timeline.Value = 2;
        flights.RunRetirements();
        Assert.Equal(["between"], released);
    }

    [Fact]
    public void Flights_WaitForSubmittedWorkDrainsEverythingPending()
    {
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);
        var released = new List<string>();

        flights.BeginFrame();
        flights.Retire(() => released.Add("a"));
        flights.EndFrame();
        flights.Retire(() => released.Add("b"));

        flights.WaitForSubmittedWork();

        Assert.Equal(["a", "b"], released);
        Assert.Equal(0, flights.PendingRetirementCount);
    }

    [Fact]
    public void Flights_OpeningTwoFramesAtOnceIsRejected()
    {
        var flights = new VulkanFrameFlightController(new FakeTimeline(), framesInFlight: 2);
        flights.BeginFrame();

        Assert.Throws<InvalidOperationException>(() => flights.BeginFrame());
    }

    [Fact]
    public void Flights_DisposeRunsRemainingReleasesSoNothingLeaks()
    {
        var flights = new VulkanFrameFlightController(new FakeTimeline(), framesInFlight: 2);
        var released = new List<string>();
        flights.BeginFrame();
        flights.Retire(() => released.Add("pending"));

        flights.Dispose();

        Assert.Equal(["pending"], released);
    }

    // ── per-frame ring ───────────────────────────────────────────────────────

    [Fact]
    public void Ring_AllocatesForwardWithAlignmentAndTracksThePeak()
    {
        var ring = new VulkanRingBufferState(1024);

        Assert.Equal(0ul, ring.Allocate(10, 1));
        Assert.Equal(256ul, ring.Allocate(16, 256));
        Assert.Equal(272ul, ring.AllocatedBytes);
        Assert.Equal(272ul, ring.PeakAllocatedBytes);

        ring.Reset();

        Assert.Equal(0ul, ring.AllocatedBytes);
        Assert.Equal(272ul, ring.PeakAllocatedBytes);
    }

    [Fact]
    public void Ring_OverCapacityThrowsRatherThanTruncating()
    {
        var ring = new VulkanRingBufferState(64);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ring.Allocate(65, 1));

        Assert.Contains("64 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ring_ZeroSizedAllocationIsLegalAndMovesNothing()
    {
        var ring = new VulkanRingBufferState(64);
        Assert.Equal(0ul, ring.Allocate(0, 16));
        Assert.Equal(0ul, ring.AllocatedBytes);
    }

    // ── staging ring ─────────────────────────────────────────────────────────

    [Fact]
    public void Staging_HandsOutBytesUntilTheRingIsFullOfUnretiredFrames()
    {
        var staging = new VulkanStagingRingState(1024);

        Assert.True(staging.TryAllocate(512, 4, serial: 1, out ulong first));
        Assert.True(staging.TryAllocate(512, 4, serial: 1, out ulong second));
        Assert.Equal(0ul, first);
        Assert.Equal(512ul, second);

        // Nothing has retired, so there is genuinely nowhere to put this.
        Assert.False(staging.TryAllocate(1, 4, serial: 1, out _));
    }

    [Fact]
    public void Staging_ReclaimsSpaceWhenTheFrameThatTookItCompletes()
    {
        var staging = new VulkanStagingRingState(1024);
        Assert.True(staging.TryAllocate(1024, 4, serial: 1, out _));
        Assert.False(staging.TryAllocate(16, 4, serial: 2, out _));

        staging.Release(completedSerial: 1);

        Assert.Equal(0ul, staging.LiveBytes);
        Assert.True(staging.TryAllocate(16, 4, serial: 2, out _));
    }

    [Fact]
    public void Staging_ReleasesOnlyFramesTheGpuHasActuallyFinished()
    {
        var staging = new VulkanStagingRingState(1024);
        Assert.True(staging.TryAllocate(256, 4, serial: 1, out _));
        Assert.True(staging.TryAllocate(256, 4, serial: 2, out _));

        staging.Release(completedSerial: 1);

        Assert.Equal(256ul, staging.LiveBytes);
        Assert.Equal(1, staging.PendingSegmentCount);
    }

    [Fact]
    public void Staging_WrapsRatherThanRefusingWhenTheTailIsFree()
    {
        var staging = new VulkanStagingRingState(1024);
        Assert.True(staging.TryAllocate(768, 4, serial: 1, out _));
        staging.Release(completedSerial: 1);

        Assert.True(staging.TryAllocate(512, 4, serial: 2, out ulong wrapped));

        Assert.Equal(0ul, wrapped);
        Assert.Equal(768ul, staging.LiveBytes);
    }

    [Fact]
    public void Staging_RefusesAPayloadLargerThanTheWholeRing()
    {
        var staging = new VulkanStagingRingState(1024);

        Assert.False(staging.TryAllocate(2048, 4, serial: 1, out _));
    }

    [Fact]
    public void Staging_MergesConsecutiveRequestsFromTheSameFrame()
    {
        var staging = new VulkanStagingRingState(1024);
        Assert.True(staging.TryAllocate(16, 4, serial: 1, out _));
        Assert.True(staging.TryAllocate(16, 4, serial: 1, out _));
        Assert.True(staging.TryAllocate(16, 4, serial: 2, out _));

        Assert.Equal(2, staging.PendingSegmentCount);
    }
}
