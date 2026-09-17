using AcDream.App.Plugins;

namespace AcDream.App.Tests.Plugins;

public sealed class WindowPluginClipboardTests
{
    [Fact]
    public void TrySetTextReturnsTrueWhenTheWriteActuallyLands()
    {
        var keyboard = new FakeKeyboard();
        var queue = new MainThreadDispatchQueue();
        var clipboard = new WindowPluginClipboard(() => keyboard, () => queue);

        bool accepted = clipboard.TrySetText("hello");

        Assert.True(accepted);
        // StoredText is read directly, not through the interface's
        // ClipboardText accessor WindowPluginClipboard itself calls -- an
        // independent check that the write actually reached the fake's
        // backing store, not just that the same field it wrote read back
        // as itself. GetCount pins that the read-back verification really
        // ran (at least once for the write, once for the confirming read)
        // rather than TrySetText returning true some other way.
        Assert.Equal("hello", keyboard.StoredText);
        Assert.Equal(1, keyboard.SetCount);
        Assert.True(keyboard.GetCount >= 1);
    }

    [Fact]
    public void TrySetTextIsFalseWhenTheUnderlyingWriteSilentlyDoesNothing()
    {
        // Models the exact failure this fixes: the platform "accepts" the
        // clipboard write (no exception) but the OS clipboard never
        // actually changes -- what an off-main-thread GLFW call does.
        var keyboard = new FakeKeyboard { OnSetClipboardText = _ => false };
        var queue = new MainThreadDispatchQueue();
        var clipboard = new WindowPluginClipboard(() => keyboard, () => queue);

        bool accepted = clipboard.TrySetText("hello");

        Assert.False(accepted);
        Assert.Equal(string.Empty, keyboard.StoredText);
    }

    [Fact]
    public void TrySetTextReturnsFalseForANullKeyboard()
    {
        var queue = new MainThreadDispatchQueue();
        var clipboard = new WindowPluginClipboard(() => null, () => queue);

        Assert.False(clipboard.TrySetText("hello"));
    }

    [Fact]
    public void TrySetTextReturnsFalseWhenTheKeyboardResolverThrows()
    {
        var queue = new MainThreadDispatchQueue();
        var clipboard = new WindowPluginClipboard(
            () => throw new InvalidOperationException("no window yet"),
            () => queue);

        Assert.False(clipboard.TrySetText("hello"));
    }

    [Fact]
    public void TrySetTextMarshalsTheWriteOntoTheQueuesOwnerThread()
    {
        // The queue is constructed on this (the test/"owner") thread. A
        // call from a different thread must not touch the keyboard until
        // that owner thread drains the queue -- proving the write is
        // marshaled rather than executed wherever the caller happens to be.
        var keyboard = new FakeKeyboard();
        var queue = new MainThreadDispatchQueue();
        var clipboard = new WindowPluginClipboard(() => keyboard, () => queue);

        using var callerReady = new ManualResetEventSlim(false);
        using var writeObserved = new ManualResetEventSlim(false);
        bool? result = null;

        var worker = new Thread(() =>
        {
            callerReady.Set();
            result = clipboard.TrySetText("from-worker");
            writeObserved.Set();
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(callerReady.Wait(TimeSpan.FromSeconds(5)));
        // Give the worker a moment to actually reach the blocking call;
        // the queue should still be empty of completed work.
        Thread.Sleep(50);
        Assert.Equal(string.Empty, keyboard.StoredText);

        queue.Drain();

        Assert.True(writeObserved.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(result);
        Assert.Equal("from-worker", keyboard.StoredText);
    }
}
