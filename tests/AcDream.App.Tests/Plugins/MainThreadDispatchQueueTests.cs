using AcDream.App.Plugins;

namespace AcDream.App.Tests.Plugins;

public sealed class MainThreadDispatchQueueTests
{
    [Fact]
    public void InvokeAndWaitRunsInlineOnTheOwnerThread()
    {
        var queue = new MainThreadDispatchQueue();
        int calls = 0;

        bool completed = queue.InvokeAndWait(
            () => calls++, TimeSpan.FromSeconds(1));

        Assert.True(completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvokeAndWaitFromAnotherThreadBlocksUntilDrained()
    {
        var queue = new MainThreadDispatchQueue();
        int calls = 0;
        using var workerReady = new ManualResetEventSlim(false);
        bool? completed = null;

        var worker = new Thread(() =>
        {
            workerReady.Set();
            completed = queue.InvokeAndWait(
                () => Interlocked.Increment(ref calls),
                TimeSpan.FromSeconds(5));
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(workerReady.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Equal(0, calls);

        queue.Drain();
        worker.Join(TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvokeAndWaitTimesOutIfNeverDrained()
    {
        var queue = new MainThreadDispatchQueue();
        using var workerReady = new ManualResetEventSlim(false);
        bool? completed = null;

        var worker = new Thread(() =>
        {
            workerReady.Set();
            completed = queue.InvokeAndWait(
                () => { }, TimeSpan.FromMilliseconds(50));
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(workerReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.False(completed);
    }

    [Fact]
    public void InvokeAndWaitDoesNotRunTheActionAfterATimeout()
    {
        var queue = new MainThreadDispatchQueue();
        int calls = 0;
        using var workerReady = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            workerReady.Set();
            queue.InvokeAndWait(
                () => Interlocked.Increment(ref calls),
                TimeSpan.FromMilliseconds(50));
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(workerReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);

        // The waiter already abandoned this item by the time Drain runs --
        // the action must not run at all, not merely run late.
        queue.Drain();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void DrainIsolatesAThrowingActionAndKeepsDrainingSubsequentOnes()
    {
        var queue = new MainThreadDispatchQueue();
        bool ranAfter = false;
        using var throwerReady = new ManualResetEventSlim(false);
        using var secondReady = new ManualResetEventSlim(false);
        bool? throwerCompleted = null;
        bool? secondCompleted = null;

        var thrower = new Thread(() =>
        {
            throwerReady.Set();
            throwerCompleted = queue.InvokeAndWait(
                () => throw new InvalidOperationException("boom"),
                TimeSpan.FromSeconds(5));
        });
        thrower.IsBackground = true;
        thrower.Start();
        Assert.True(throwerReady.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);

        var second = new Thread(() =>
        {
            secondReady.Set();
            secondCompleted = queue.InvokeAndWait(
                () => ranAfter = true, TimeSpan.FromSeconds(5));
        });
        second.IsBackground = true;
        second.Start();
        Assert.True(secondReady.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);

        var stderr = new StringWriter();
        TextWriter original = Console.Error;
        Console.SetError(stderr);
        try
        {
            queue.Drain();
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.True(thrower.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));

        // The waiter is released despite the throw -- Run()'s finally
        // block sets the event before the exception reaches Drain's catch.
        Assert.True(throwerCompleted);
        Assert.True(secondCompleted);
        // Draining continued past the throwing item instead of stopping.
        Assert.True(ranAfter);
        Assert.Contains("boom", stderr.ToString());
    }

    [Fact]
    public void DrainFromANonOwnerThreadThrows()
    {
        var queue = new MainThreadDispatchQueue();
        Exception? caught = null;

        var worker = new Thread(() =>
        {
            try
            {
                queue.Drain();
            }
            catch (Exception error)
            {
                caught = error;
            }
        });
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(caught);
    }
}
