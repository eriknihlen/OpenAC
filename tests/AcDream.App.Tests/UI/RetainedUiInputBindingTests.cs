using AcDream.App.Rendering;
using AcDream.App.UI;
using Silk.NET.Input;

namespace AcDream.App.Tests.UI;

public sealed class RetainedUiInputBindingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MouseAttachFailureRollsBackEveryPossiblyAcquiredPrefix(int failAdd)
    {
        var surface = new MouseSurface { FailAdd = failAdd };
        var binding = new RetainedMouseInputBinding(
            surface,
            new UiRoot(),
            new HostQuiescenceGate());

        Assert.ThrowsAny<Exception>(binding.Attach);

        Assert.True(binding.IsDisposalComplete);
        Assert.Equal(failAdd, surface.AddCalls);
        Assert.Equal(failAdd, surface.RemoveCalls);
        Assert.Equal(0, surface.LiveEdges);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void KeyboardAttachFailureRollsBackEveryPossiblyAcquiredPrefix(int failAdd)
    {
        var surface = new KeyboardSurface { FailAdd = failAdd };
        var binding = new RetainedKeyboardInputBinding(
            surface,
            new UiRoot(),
            new HostQuiescenceGate());

        Assert.ThrowsAny<Exception>(binding.Attach);

        Assert.True(binding.IsDisposalComplete);
        Assert.Equal(failAdd, surface.AddCalls);
        Assert.Equal(failAdd, surface.RemoveCalls);
        Assert.Equal(0, surface.LiveEdges);
    }

    [Fact]
    public void MouseDeactivateSilencesCopiedDelegateBeforePhysicalDetach()
    {
        var surface = new MouseSurface();
        var root = new UiRoot();
        int worldClicks = 0;
        root.WorldMouseFallThrough += (_, _, _, _) => worldClicks++;
        var binding = new RetainedMouseInputBinding(
            surface,
            root,
            new HostQuiescenceGate());
        binding.Attach();
        Action<MouseButton, int, int> copied = surface.Down!;

        copied(MouseButton.Left, 10, 20);
        binding.Deactivate();
        copied(MouseButton.Left, 10, 20);
        binding.Dispose();

        Assert.Equal(1, worldClicks);
    }

    [Fact]
    public void MouseEventRaisedReentrantlyDuringAttachCannotEnterPartialUiBinding()
    {
        var surface = new MouseSurface { RaiseDuringAdd = true };
        var root = new UiRoot();
        int worldClicks = 0;
        root.WorldMouseFallThrough += (_, _, _, _) => worldClicks++;
        var binding = new RetainedMouseInputBinding(
            surface,
            root,
            new HostQuiescenceGate());

        binding.Attach();
        Assert.Equal(0, worldClicks);
        surface.Down!(MouseButton.Left, 1, 2);

        Assert.Equal(1, worldClicks);
    }

    [Fact]
    public void KeyboardDeactivateSilencesCopiedDelegateBeforePhysicalDetach()
    {
        var surface = new KeyboardSurface();
        var root = new UiRoot();
        int worldKeys = 0;
        root.WorldKeyFallThrough += (_, _) => worldKeys++;
        var binding = new RetainedKeyboardInputBinding(
            surface,
            root,
            new HostQuiescenceGate());
        binding.Attach();
        Action<Key> copied = surface.Down!;

        copied(Key.A);
        binding.Deactivate();
        copied(Key.B);
        binding.Dispose();

        Assert.Equal(1, worldKeys);
    }

    [Fact]
    public void RetainedBindingsAreTerminalAfterDisposeBeforeAttach()
    {
        var mouseSurface = new MouseSurface();
        var mouse = new RetainedMouseInputBinding(
            mouseSurface,
            new UiRoot(),
            new HostQuiescenceGate());
        var keyboardSurface = new KeyboardSurface();
        var keyboard = new RetainedKeyboardInputBinding(
            keyboardSurface,
            new UiRoot(),
            new HostQuiescenceGate());

        mouse.Dispose();
        keyboard.Dispose();

        Assert.Throws<ObjectDisposedException>(mouse.Attach);
        Assert.Throws<ObjectDisposedException>(keyboard.Attach);
        mouse.Dispose();
        keyboard.Dispose();
        Assert.Equal(0, mouseSurface.LiveEdges);
        Assert.Equal(0, keyboardSurface.LiveEdges);
    }

    [Fact]
    public void RetainedBindingsRetryOnlyFailedPhysicalEdges()
    {
        var mouseSurface = new MouseSurface();
        var mouse = new RetainedMouseInputBinding(
            mouseSurface,
            new UiRoot(),
            new HostQuiescenceGate());
        var keyboardSurface = new KeyboardSurface();
        var keyboard = new RetainedKeyboardInputBinding(
            keyboardSurface,
            new UiRoot(),
            new HostQuiescenceGate());
        mouse.Attach();
        keyboard.Attach();
        mouseSurface.FailRemoveMoveOnce = true;
        keyboardSurface.FailRemoveUpOnce = true;

        mouse.Dispose();
        keyboard.Dispose();
        int mouseRemoves = mouseSurface.RemoveCalls;
        int keyboardRemoves = keyboardSurface.RemoveCalls;
        mouse.Dispose();
        keyboard.Dispose();

        Assert.Equal(mouseRemoves, mouseSurface.RemoveCalls);
        Assert.Equal(keyboardRemoves, keyboardSurface.RemoveCalls);
        Assert.Equal(2, mouseSurface.MoveRemoveCalls);
        Assert.Equal(2, keyboardSurface.UpRemoveCalls);
        Assert.True(mouse.IsDisposalComplete);
        Assert.True(keyboard.IsDisposalComplete);
    }

    private sealed class MouseSurface : IRetainedMouseSurface
    {
        public int FailAdd { get; init; }
        public bool RaiseDuringAdd { get; init; }
        public bool FailRemoveMoveOnce { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int MoveRemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        public Action<MouseButton, int, int>? Down { get; private set; }
        private Action<MouseButton, int, int>? _up;
        private Action<int, int>? _move;
        private Action<int>? _scroll;

        public void AddMouseDown(Action<MouseButton, int, int> callback) => Add(() => Down = callback);
        public void AddMouseUp(Action<MouseButton, int, int> callback) => Add(() => _up = callback);
        public void AddMouseMove(Action<int, int> callback) => Add(() => _move = callback);
        public void AddScroll(Action<int> callback) => Add(() => _scroll = callback);
        public void RemoveMouseDown(Action<MouseButton, int, int> callback) => Remove(() => Down = null);
        public void RemoveMouseUp(Action<MouseButton, int, int> callback) => Remove(() => _up = null);
        public void RemoveMouseMove(Action<int, int> callback)
        {
            MoveRemoveCalls++;
            if (FailRemoveMoveOnce)
            {
                FailRemoveMoveOnce = false;
                throw new InvalidOperationException("remove move");
            }
            Remove(() => _move = null);
        }
        public void RemoveScroll(Action<int> callback) => Remove(() => _scroll = null);

        private void Add(Action publish)
        {
            AddCalls++;
            publish();
            LiveEdges++;
            if (RaiseDuringAdd)
                Down?.Invoke(MouseButton.Left, 1, 2);
            if (AddCalls == FailAdd) throw new InvalidOperationException("add");
        }
        private void Remove(Action clear)
        {
            RemoveCalls++;
            clear();
            LiveEdges--;
        }
    }

    private sealed class KeyboardSurface : IRetainedKeyboardSurface
    {
        public int FailAdd { get; init; }
        public bool FailRemoveUpOnce { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int UpRemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        public Action<Key>? Down { get; private set; }
        private Action<Key>? _up;
        private Action<char>? _char;

        public void AddKeyDown(Action<Key> callback) => Add(() => Down = callback);
        public void AddKeyUp(Action<Key> callback) => Add(() => _up = callback);
        public void AddKeyChar(Action<char> callback) => Add(() => _char = callback);
        public void RemoveKeyDown(Action<Key> callback) => Remove(() => Down = null);
        public void RemoveKeyUp(Action<Key> callback)
        {
            UpRemoveCalls++;
            if (FailRemoveUpOnce)
            {
                FailRemoveUpOnce = false;
                throw new InvalidOperationException("remove up");
            }
            Remove(() => _up = null);
        }
        public void RemoveKeyChar(Action<char> callback) => Remove(() => _char = null);

        private void Add(Action publish)
        {
            AddCalls++;
            publish();
            LiveEdges++;
            if (AddCalls == FailAdd) throw new InvalidOperationException("add");
        }
        private void Remove(Action clear)
        {
            RemoveCalls++;
            clear();
            LiveEdges--;
        }
    }
}
