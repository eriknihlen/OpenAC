using AcDream.App.Plugins;
using AcDream.Plugin.Abstractions;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Plugins;

public sealed class WindowPluginHostWindowTests
{
    [Fact]
    public void MinimizeReportsDoneOnlyWhenTheStateActuallyChanges()
    {
        var target = new FakeHostWindowTarget();
        var queue = new MainThreadDispatchQueue();
        bool minimized = false;
        var window = new WindowPluginHostWindow(
            () => target,
            () => minimized,
            () => queue);

        HostWindowResult result = window.Minimize();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(WindowState.Minimized, target.WindowState);
    }

    [Fact]
    public void MinimizeIsUnavailableWhenTheWriteDoesNotLand()
    {
        var target = new FakeHostWindowTarget
        {
            OnSetWindowState = _ => false,
        };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Minimize();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
        Assert.Equal(WindowState.Normal, target.WindowState);
    }

    [Fact]
    public void RestoreReportsDoneOnlyWhenTheStateActuallyChanges()
    {
        var target = new FakeHostWindowTarget { WindowState = WindowState.Minimized };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Restore();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(WindowState.Normal, target.WindowState);
    }

    [Fact]
    public void MinimizeAndRestoreAreUnavailableWithNoWindow()
    {
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => null,
            () => false,
            () => queue);

        Assert.Equal(HostWindowStatus.Unavailable, window.Minimize().Status);
        Assert.Equal(HostWindowStatus.Unavailable, window.Restore().Status);
    }

    [Fact]
    public void IsMinimizedReadsTheSuppliedCachedFlagNotTheLiveWindowState()
    {
        var target = new FakeHostWindowTarget { WindowState = WindowState.Minimized };
        var queue = new MainThreadDispatchQueue();
        bool cached = false;
        var window = new WindowPluginHostWindow(
            () => target,
            () => cached,
            () => queue);

        // The live window disagrees with the cached flag on purpose: this
        // proves IsMinimized answers from the supplied cache, never by
        // reading WindowState directly (which would not be safe off the
        // window's own thread).
        Assert.False(window.IsMinimized);
        cached = true;
        Assert.True(window.IsMinimized);
    }

    [Fact]
    public void RequestCloseReachesTheCloseRouteExactlyOnceAndNeverExitsTheProcessDirectly()
    {
        var target = new FakeHostWindowTarget();
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.RequestClose();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(1, target.CloseCount);
        // No assertion needed for "does not call Environment.Exit" --
        // there is no such call in WindowPluginHostWindow.RequestClose to
        // begin with; Close() is the only route it takes, and the fake
        // proves it reached exactly once.
    }

    [Fact]
    public void RequestCloseIsUnavailableWithNoWindow()
    {
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => null,
            () => false,
            () => queue);

        Assert.Equal(HostWindowStatus.Unavailable, window.RequestClose().Status);
    }

    [Fact]
    public void MinimizeMarshalsTheWriteOntoTheQueuesOwnerThread()
    {
        // Mirrors WindowPluginClipboardTests' marshalling proof: a call
        // from a different thread must not touch the window until the
        // owner thread drains the queue.
        var target = new FakeHostWindowTarget();
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        using var callerReady = new ManualResetEventSlim(false);
        using var writeObserved = new ManualResetEventSlim(false);
        HostWindowResult result = default;

        var worker = new Thread(() =>
        {
            callerReady.Set();
            result = window.Minimize();
            writeObserved.Set();
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(callerReady.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Equal(WindowState.Normal, target.WindowState);

        queue.Drain();

        Assert.True(writeObserved.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(WindowState.Minimized, target.WindowState);
    }

    [Fact]
    public void RestoreSucceedsWithoutWritingWhenTheWindowIsAlreadyNotMinimized()
    {
        // Restore must never force WindowState to Normal on a window
        // that is already Maximized/Fullscreen/Normal -- doing so would
        // also desynchronize the display mode switcher's own tracking of
        // fullscreen/maximized state. A Restore call here is a no-op
        // success, not a write.
        var target = new FakeHostWindowTarget { WindowState = WindowState.Maximized };
        int baselineSetCount = target.SetWindowStateCount;
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Restore();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(WindowState.Maximized, target.WindowState);
        Assert.Equal(baselineSetCount, target.SetWindowStateCount);
    }

    [Fact]
    public void RestoringAWindowThatWasFullscreenBeforeItWasMinimizedSucceedsEvenThoughTheReadbackIsNotNormal()
    {
        // On Silk.NET's GLFW backend, un-minimizing a
        // window that was fullscreen before it was minimized reads back
        // as Fullscreen again, never as Normal. A success check of
        // "WindowState == Normal" would report Unavailable here even
        // though the window did un-minimize -- a retrying plugin would
        // spin forever. Restore's real success predicate is
        // "WindowState != Minimized".
        var target = new FakeHostWindowTarget
        {
            WindowState = WindowState.Minimized,
            OverrideResultingState = desired =>
                desired == WindowState.Normal ? WindowState.Fullscreen : desired,
        };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Restore();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(WindowState.Fullscreen, target.WindowState);
    }

    [Fact]
    public void RestoreIsUnavailableWhenTheWindowIsStillMinimizedAfterTheWrite()
    {
        var target = new FakeHostWindowTarget
        {
            WindowState = WindowState.Minimized,
            OnSetWindowState = _ => false,
        };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Restore();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
        Assert.Equal(WindowState.Minimized, target.WindowState);
    }

    [Fact]
    public void MinimizeSucceedsWithoutWritingWhenAlreadyMinimized()
    {
        var target = new FakeHostWindowTarget { WindowState = WindowState.Minimized };
        int baselineSetCount = target.SetWindowStateCount;
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Minimize();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(baselineSetCount, target.SetWindowStateCount);
    }

    [Fact]
    public void MinimizeReportsUnavailableWhenTheOnThreadWriteThrows()
    {
        // A throw from the on-thread write must not escape into the
        // calling plugin -- this runs on the calling thread's own fast
        // path (already the owner thread), the case most likely to let a
        // throw propagate straight out of InvokeAndWait.
        var target = new FakeHostWindowTarget { ThrowOnSetWindowState = true };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Minimize();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
    }

    [Fact]
    public void RestoreReportsUnavailableWhenTheOnThreadWriteThrows()
    {
        var target = new FakeHostWindowTarget
        {
            WindowState = WindowState.Minimized,
            ThrowOnSetWindowState = true,
        };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.Restore();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
    }

    [Fact]
    public void RequestCloseReportsUnavailableWhenTheOnThreadCloseThrows()
    {
        var target = new FakeHostWindowTarget { ThrowOnClose = true };
        var queue = new MainThreadDispatchQueue();
        var window = new WindowPluginHostWindow(
            () => target,
            () => false,
            () => queue);

        HostWindowResult result = window.RequestClose();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
    }
}
