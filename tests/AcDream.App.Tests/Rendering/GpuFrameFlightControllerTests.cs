using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class GpuFrameFlightControllerTests
{
    [Fact]
    public void FourthFrameRetiresOldestFenceBeforeReusingItsSlot()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 3);

        SubmitFrame(controller);
        SubmitFrame(controller);
        SubmitFrame(controller);

        Assert.Empty(api.Waited);

        controller.BeginFrame();

        Assert.Equal([1], api.Waited);
        Assert.Equal([1], api.Deleted);

        controller.EndFrame();
        Assert.Equal([1, 2, 3, 4], api.Inserted);
    }

    [Fact]
    public void TimeoutIsRetriedAndOnlyFirstWaitFlushesCommands()
    {
        var api = new FakeFenceApi();
        api.Results.Enqueue(GpuFenceWaitResult.Timeout);
        api.Results.Enqueue(GpuFenceWaitResult.Signaled);
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 1);

        SubmitFrame(controller);
        controller.BeginFrame();

        Assert.Equal([true, false], api.FlushRequests);
        Assert.Equal([1, 1], api.Waited);
        Assert.Equal([1], api.Deleted);
    }

    [Fact]
    public void WaitFailureDoesNotDeleteOrReuseUnretiredFence()
    {
        var api = new FakeFenceApi();
        api.Results.Enqueue(GpuFenceWaitResult.Failed);
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 1);

        SubmitFrame(controller);

        Assert.Throws<InvalidOperationException>(() => controller.BeginFrame());
        Assert.Empty(api.Deleted);
        Assert.Throws<InvalidOperationException>(() => controller.EndFrame());
    }

    [Fact]
    public void DisposeDeletesEveryOutstandingFenceExactlyOnce()
    {
        var api = new FakeFenceApi();
        var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 3);
        SubmitFrame(controller);
        SubmitFrame(controller);
        SubmitFrame(controller);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal([1, 2, 3], api.Deleted);
    }

    [Fact]
    public void RetirementAfterSubmittedFrameWaitsForItsFence()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 2);
        int releases = 0;

        SubmitFrame(controller);
        controller.Retire(() => releases++);

        Assert.Equal(0, releases);
        SubmitFrame(controller);
        Assert.Equal(0, releases);

        controller.BeginFrame();
        Assert.Equal(1, releases);
        controller.EndFrame();
    }

    [Fact]
    public void RetirementDuringFrameIncludesCurrentFrameFence()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 1);
        int releases = 0;

        controller.BeginFrame();
        controller.Retire(() => releases++);
        Assert.Equal(0, releases);
        controller.EndFrame();

        controller.BeginFrame();
        Assert.Equal(1, releases);
        controller.EndFrame();
    }

    [Fact]
    public void RetirementBeforeFirstFrameRunsImmediately()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api);
        int releases = 0;

        controller.Retire(() => releases++);

        Assert.Equal(1, releases);
        Assert.Equal(0, controller.PendingRetirementCount);
    }

    [Fact]
    public void ThrowingRetirementDoesNotDropLaterCallbacksInCompletedBucket()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 1);
        int releases = 0;
        int attempts = 0;

        SubmitFrame(controller);
        controller.Retire(() =>
        {
            if (++attempts == 1)
                throw new InvalidOperationException("first");
        });
        controller.Retire(() => releases++);

        AggregateException error = Assert.Throws<AggregateException>(() => controller.BeginFrame());

        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, releases);
        Assert.Equal(1, controller.PendingRetirementCount);

        controller.WaitForSubmittedWork();
        Assert.Equal(2, attempts);
        Assert.Equal(0, controller.PendingRetirementCount);
    }

    [Fact]
    public void WaitForSubmittedWorkDrainsLaterFencesAfterEarlierRetirementThrows()
    {
        var api = new FakeFenceApi();
        using var controller = new GpuFrameFlightController(api, maximumFramesInFlight: 2);
        int laterReleases = 0;
        bool failOnce = true;

        SubmitFrame(controller);
        controller.Retire(() =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new InvalidOperationException("first frame");
            }
        });
        SubmitFrame(controller);
        controller.Retire(() => laterReleases++);

        AggregateException error = Assert.Throws<AggregateException>(controller.WaitForSubmittedWork);

        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, laterReleases);
        Assert.Equal([1, 2], api.Deleted);
        Assert.Equal(0, controller.PendingRetirementCount);
    }

    private static void SubmitFrame(GpuFrameFlightController controller)
    {
        controller.BeginFrame();
        controller.EndFrame();
    }

    private sealed class FakeFenceApi : IGpuFenceApi
    {
        private nint _nextFence = 1;

        public List<nint> Inserted { get; } = [];
        public List<nint> Waited { get; } = [];
        public List<bool> FlushRequests { get; } = [];
        public List<nint> Deleted { get; } = [];
        public Queue<GpuFenceWaitResult> Results { get; } = new();

        public nint Insert()
        {
            nint fence = _nextFence++;
            Inserted.Add(fence);
            return fence;
        }

        public GpuFenceWaitResult Wait(nint fence, bool flushCommands, ulong timeoutNanoseconds)
        {
            Assert.Equal(1_000_000ul, timeoutNanoseconds);
            Waited.Add(fence);
            FlushRequests.Add(flushCommands);
            return Results.TryDequeue(out GpuFenceWaitResult result)
                ? result
                : GpuFenceWaitResult.Signaled;
        }

        public void Delete(nint fence) => Deleted.Add(fence);
    }
}
