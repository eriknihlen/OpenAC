using AcDream.App.World;

namespace AcDream.App.Tests.World;

public sealed class RetailInboundEventDispatcherTests
{
    private sealed class Counter
    {
        public int Value;
    }

    [Fact]
    public void NestedEvents_DrainAfterCompleteOuterTail_InArrivalOrder()
    {
        var dispatcher = new RetailInboundEventDispatcher();
        var calls = new List<string>();

        dispatcher.Run(() =>
        {
            calls.Add("position-head");
            dispatcher.Run(() => calls.Add("state"));
            dispatcher.Run(() => calls.Add("vector"));
            calls.Add("position-tail");
        });

        Assert.Equal(
            ["position-head", "position-tail", "state", "vector"],
            calls);
        Assert.False(dispatcher.IsDraining);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void FrameOperation_DefersInboundMutationUntilQuantumTail()
    {
        var dispatcher = new RetailInboundEventDispatcher();
        var calls = new List<string>();

        dispatcher.Run(() =>
        {
            calls.Add("physics");
            dispatcher.Run(() => calls.Add("position"));
            calls.Add("shadow-and-hooks");
        });

        Assert.Equal(["physics", "shadow-and-hooks", "position"], calls);
    }

    [Fact]
    public void NestedStateOperations_DrainAfterOuterTail_InArrivalOrder()
    {
        var dispatcher = new RetailInboundEventDispatcher();
        var calls = new List<string>();

        dispatcher.Run(
            dispatcher,
            calls,
            static (active, output) =>
            {
                output.Add("position-head");
                active.Run(
                    output,
                    "state",
                    static (target, value) => target.Add(value));
                output.Add("position-tail");
            });

        Assert.Equal(["position-head", "position-tail", "state"], calls);
        Assert.False(dispatcher.IsDraining);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void Failure_DiscardsQueuedTailAndLeavesDispatcherReusable()
    {
        var dispatcher = new RetailInboundEventDispatcher();
        bool queuedRan = false;

        Assert.Throws<InvalidOperationException>(() => dispatcher.Run(() =>
        {
            dispatcher.Run(() => queuedRan = true);
            throw new InvalidOperationException("packet failed");
        }));

        Assert.False(queuedRan);
        Assert.False(dispatcher.IsDraining);
        Assert.Equal(0, dispatcher.PendingCount);

        dispatcher.Run(() => queuedRan = true);
        Assert.True(queuedRan);
    }

    [Fact]
    public void StateFastPath_DoesNotAllocateAfterWarmup()
    {
        var dispatcher = new RetailInboundEventDispatcher();
        var counter = new Counter();
        Action<Counter, int> increment =
            static (target, amount) => target.Value += amount;
        dispatcher.Run(counter, 1, increment);

        int dispatches = 0;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "RetailInboundEventDispatcher.Run state fast path",
            () =>
            {
                dispatches++;
                dispatcher.Run(counter, 1, increment);
            });

        Assert.True(dispatches > 0);
        Assert.Equal(dispatches + 1, counter.Value);
    }
}
