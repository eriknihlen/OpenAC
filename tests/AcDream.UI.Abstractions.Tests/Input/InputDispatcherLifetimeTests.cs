using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public sealed class InputDispatcherLifetimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AttachFailureRollsBackEveryPossiblyAcquiredPrefix(int failAdd)
    {
        var source = new ThrowingSources { FailAdd = failAdd };
        var dispatcher = InputDispatcher.CreateDetached(
            source,
            source,
            new KeyBindings());

        Assert.ThrowsAny<Exception>(dispatcher.Attach);

        Assert.True(dispatcher.IsDisposalComplete);
        Assert.Equal(failAdd, source.AddCalls);
        Assert.Equal(failAdd, source.RemoveCalls);
        Assert.Equal(0, source.LiveEdges);
    }

    [Fact]
    public void DeactivateSilencesCopiedSourceDelegateBeforePhysicalDetach()
    {
        var source = new ThrowingSources();
        var bindings = new KeyBindings();
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementForward));
        var dispatcher = InputDispatcher.CreateDetached(source, source, bindings);
        int fired = 0;
        dispatcher.Fired += (_, _) => fired++;
        dispatcher.Attach();
        Action<Key, ModifierMask> copied = source.KeyDownDelegate!;

        copied(Key.W, ModifierMask.None);
        dispatcher.Deactivate();
        copied(Key.W, ModifierMask.None);
        dispatcher.Dispose();

        Assert.Equal(1, fired);
        Assert.True(dispatcher.IsDisposalComplete);
    }

    [Fact]
    public void SourceEventRaisedReentrantlyDuringAttachCannotEnterPartialDispatcher()
    {
        var source = new ThrowingSources { RaiseDuringKeyDownAdd = true };
        var bindings = new KeyBindings();
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementForward));
        var dispatcher = InputDispatcher.CreateDetached(source, source, bindings);
        int fired = 0;
        dispatcher.Fired += (_, _) => fired++;

        dispatcher.Attach();
        Assert.Equal(0, fired);
        source.KeyDownDelegate!(Key.W, ModifierMask.None);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void FailedDetachRetainsOnlyPendingSourceForRetry()
    {
        var source = new ThrowingSources();
        var dispatcher = InputDispatcher.CreateDetached(
            source,
            source,
            new KeyBindings());
        dispatcher.Attach();
        source.FailRemoveKeyDown = true;

        Assert.Throws<AggregateException>(dispatcher.Dispose);
        int completedRemoves = source.RemoveCalls - source.KeyDownRemoveCalls;
        source.FailRemoveKeyDown = false;
        dispatcher.Dispose();

        Assert.Equal(completedRemoves, source.RemoveCalls - source.KeyDownRemoveCalls);
        Assert.True(dispatcher.IsDisposalComplete);
    }

    [Fact]
    public void DisposeBeforeAttachMakesDispatcherTerminal()
    {
        var source = new ThrowingSources();
        var dispatcher = InputDispatcher.CreateDetached(
            source,
            source,
            new KeyBindings());

        dispatcher.Dispose();

        Assert.Throws<ObjectDisposedException>(dispatcher.Attach);
        dispatcher.Dispose();
        Assert.Equal(0, source.AddCalls);
        Assert.Equal(0, source.LiveEdges);
    }

    private sealed class ThrowingSources : IKeyboardSource, IMouseSource
    {
        private Action<Key, ModifierMask>? _keyDown;
        private Action<Key, ModifierMask>? _keyUp;
        private Action<MouseButton, ModifierMask>? _mouseDown;
        private Action<MouseButton, ModifierMask>? _mouseUp;
        private Action<float, float>? _mouseMove;
        private Action<float>? _scroll;

        public int FailAdd { get; init; }
        public bool FailRemoveKeyDown { get; set; }
        public bool RaiseDuringKeyDownAdd { get; init; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int KeyDownRemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        public Action<Key, ModifierMask>? KeyDownDelegate => _keyDown;

        public event Action<Key, ModifierMask>? KeyDown
        {
            add
            {
                Add(ref _keyDown, value!);
                if (RaiseDuringKeyDownAdd)
                    value?.Invoke(Key.W, ModifierMask.None);
            }
            remove
            {
                KeyDownRemoveCalls++;
                if (FailRemoveKeyDown) throw new InvalidOperationException("remove key down");
                Remove(ref _keyDown, value!);
            }
        }
        public event Action<Key, ModifierMask>? KeyUp
        {
            add => Add(ref _keyUp, value!);
            remove => Remove(ref _keyUp, value!);
        }
        public event Action<MouseButton, ModifierMask>? MouseDown
        {
            add => Add(ref _mouseDown, value!);
            remove => Remove(ref _mouseDown, value!);
        }
        public event Action<MouseButton, ModifierMask>? MouseUp
        {
            add => Add(ref _mouseUp, value!);
            remove => Remove(ref _mouseUp, value!);
        }
        public event Action<float, float>? MouseMove
        {
            add => Add(ref _mouseMove, value!);
            remove => Remove(ref _mouseMove, value!);
        }
        public event Action<float>? Scroll
        {
            add => Add(ref _scroll, value!);
            remove => Remove(ref _scroll, value!);
        }

        public ModifierMask CurrentModifiers => ModifierMask.None;
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => false;
        public bool IsHeld(Key key) => false;
        public bool IsHeld(MouseButton button) => false;

        private void Add<T>(ref T? slot, T value) where T : Delegate
        {
            AddCalls++;
            slot = (T?)Delegate.Combine(slot, value);
            LiveEdges++;
            if (AddCalls == FailAdd) throw new InvalidOperationException("add");
        }

        private void Remove<T>(ref T? slot, T value) where T : Delegate
        {
            RemoveCalls++;
            if (slot is null) return;
            slot = (T?)Delegate.Remove(slot, value);
            LiveEdges--;
        }
    }
}
