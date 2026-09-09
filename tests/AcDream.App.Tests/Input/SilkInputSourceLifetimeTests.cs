using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class SilkInputSourceLifetimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KeyboardAttachFailureRollsBackEveryPossiblyAcquiredPrefix(int failAdd)
    {
        var surface = new KeyboardSurface { FailAdd = failAdd };
        var source = SilkKeyboardSource.CreateDetached(surface, new HostQuiescenceGate());

        Assert.ThrowsAny<Exception>(source.Attach);

        Assert.True(source.IsDisposalComplete);
        Assert.Equal(failAdd, surface.AddCalls);
        Assert.Equal(failAdd, surface.RemoveCalls);
        Assert.Equal(0, surface.LiveEdges);
    }

    [Fact]
    public void KeyboardDeactivateSilencesCopiedDelegateBeforePhysicalDetach()
    {
        var surface = new KeyboardSurface();
        var source = SilkKeyboardSource.CreateDetached(surface, new HostQuiescenceGate());
        int calls = 0;
        source.KeyDown += (_, _) => calls++;
        source.Attach();
        Action<Key> copied = surface.Down!;

        surface.RaiseDown(Key.A);
        source.Deactivate();
        copied(Key.B);
        source.Dispose();

        Assert.Equal(1, calls);
        Assert.True(source.IsDisposalComplete);
    }

    [Fact]
    public void KeyboardEventRaisedReentrantlyDuringAttachCannotEnterPartialSource()
    {
        var surface = new KeyboardSurface { RaiseDuringAdd = true };
        var source = SilkKeyboardSource.CreateDetached(surface, new HostQuiescenceGate());
        int calls = 0;
        source.KeyDown += (_, _) => calls++;

        source.Attach();
        Assert.Equal(0, calls);
        surface.RaiseDown(Key.A);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void KeyboardDetachRetriesOnlyPendingEdge()
    {
        var surface = new KeyboardSurface();
        var source = SilkKeyboardSource.CreateDetached(surface, new HostQuiescenceGate());
        source.Attach();
        surface.FailRemoveDown = true;

        Assert.Throws<AggregateException>(source.Dispose);
        int upRemoves = surface.UpRemoveCalls;
        surface.FailRemoveDown = false;
        source.Dispose();

        Assert.Equal(upRemoves, surface.UpRemoveCalls);
        Assert.True(source.IsDisposalComplete);
    }

    [Fact]
    public void KeyboardDisposeBeforeAttachMakesSourceTerminal()
    {
        var surface = new KeyboardSurface();
        var source = SilkKeyboardSource.CreateDetached(surface, new HostQuiescenceGate());

        source.Dispose();

        Assert.Throws<ObjectDisposedException>(source.Attach);
        source.Dispose();
        Assert.Equal(0, surface.LiveEdges);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MouseAttachFailureRollsBackEveryPossiblyAcquiredPrefix(int failAdd)
    {
        var surface = new MouseSurface { FailAdd = failAdd };
        var source = SilkMouseSource.CreateDetached(
            surface,
            new Capture(),
            modifierSource: null,
            new HostQuiescenceGate());

        Assert.ThrowsAny<Exception>(source.Attach);

        Assert.True(source.IsDisposalComplete);
        Assert.Equal(failAdd, surface.AddCalls);
        Assert.Equal(failAdd, surface.RemoveCalls);
        Assert.Equal(0, surface.LiveEdges);
    }

    [Fact]
    public void MouseDeactivateSilencesAllCopiedDelegatesBeforePhysicalDetach()
    {
        var surface = new MouseSurface();
        var source = SilkMouseSource.CreateDetached(
            surface,
            new Capture(),
            modifierSource: null,
            new HostQuiescenceGate());
        int calls = 0;
        source.MouseDown += (_, _) => calls++;
        source.MouseUp += (_, _) => calls++;
        source.MouseMove += (_, _) => calls++;
        source.Scroll += _ => calls++;
        source.Attach();
        var copied = surface.CopyCallbacks();

        surface.RaiseAll();
        source.Deactivate();
        copied.Down(MouseButton.Left);
        copied.Up(MouseButton.Left);
        copied.Move(new Vector2(5, 6));
        copied.Scroll(1);
        source.Dispose();

        Assert.Equal(4, calls);
        Assert.True(source.IsDisposalComplete);
    }

    [Fact]
    public void MouseFailedDetachRetriesOnlyPendingEdge()
    {
        var surface = new MouseSurface();
        var source = SilkMouseSource.CreateDetached(
            surface,
            new Capture(),
            modifierSource: null,
            new HostQuiescenceGate());
        source.Attach();
        surface.FailRemoveMoveOnce = true;

        source.Dispose();
        int removes = surface.RemoveCalls;
        source.Dispose();

        Assert.Equal(removes, surface.RemoveCalls);
        Assert.Equal(2, surface.MoveRemoveCalls);
        Assert.True(source.IsDisposalComplete);
    }

    [Fact]
    public void MouseDisposeBeforeAttachMakesSourceTerminal()
    {
        var surface = new MouseSurface();
        var source = SilkMouseSource.CreateDetached(
            surface,
            new Capture(),
            modifierSource: null,
            new HostQuiescenceGate());

        source.Dispose();

        Assert.Throws<ObjectDisposedException>(source.Attach);
        source.Dispose();
        Assert.Equal(0, surface.LiveEdges);
    }

    private sealed class Capture : IInputCaptureSource
    {
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => false;
        public bool DevToolsWantCaptureKeyboard => false;
    }

    private sealed class KeyboardSurface : IKeyboardEventSurface
    {
        public int FailAdd { get; init; }
        public bool FailRemoveDown { get; set; }
        public bool RaiseDuringAdd { get; init; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int UpRemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        private Action<Key>? _down;
        private Action<Key>? _up;
        public Action<Key>? Down => _down;

        public void AddKeyDown(Action<Key> callback) => Add(ref _down, callback);
        public void AddKeyUp(Action<Key> callback) => Add(ref _up, callback);
        public void RemoveKeyDown(Action<Key> callback)
        {
            RemoveCalls++;
            if (FailRemoveDown) throw new InvalidOperationException("remove down");
            if (ReferenceEquals(_down, callback)) { _down = null; LiveEdges--; }
        }
        public void RemoveKeyUp(Action<Key> callback)
        {
            RemoveCalls++;
            UpRemoveCalls++;
            if (ReferenceEquals(_up, callback)) { _up = null; LiveEdges--; }
        }
        public bool IsKeyPressed(Key key) => false;
        public void RaiseDown(Key key) => _down?.Invoke(key);

        private void Add(ref Action<Key>? slot, Action<Key> callback)
        {
            AddCalls++;
            slot = callback;
            LiveEdges++;
            if (RaiseDuringAdd) callback(Key.A);
            if (AddCalls == FailAdd) throw new InvalidOperationException("add");
        }
    }

    private sealed class MouseSurface : IMouseEventSurface
    {
        public int FailAdd { get; init; }
        public bool FailRemoveMoveOnce { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int MoveRemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        private Action<MouseButton>? _down;
        private Action<MouseButton>? _up;
        private Action<Vector2>? _move;
        private Action<float>? _scroll;

        public void AddMouseDown(Action<MouseButton> callback) => Add(ref _down, callback);
        public void AddMouseUp(Action<MouseButton> callback) => Add(ref _up, callback);
        public void AddMouseMove(Action<Vector2> callback) => Add(ref _move, callback);
        public void AddScroll(Action<float> callback) => Add(ref _scroll, callback);
        public void RemoveMouseDown(Action<MouseButton> callback) => Remove(ref _down, callback);
        public void RemoveMouseUp(Action<MouseButton> callback) => Remove(ref _up, callback);
        public void RemoveMouseMove(Action<Vector2> callback)
        {
            MoveRemoveCalls++;
            if (FailRemoveMoveOnce)
            {
                FailRemoveMoveOnce = false;
                throw new InvalidOperationException("remove move");
            }
            Remove(ref _move, callback);
        }
        public void RemoveScroll(Action<float> callback) => Remove(ref _scroll, callback);
        public bool IsButtonPressed(MouseButton button) => false;

        public (Action<MouseButton> Down, Action<MouseButton> Up, Action<Vector2> Move, Action<float> Scroll)
            CopyCallbacks() => (_down!, _up!, _move!, _scroll!);

        public void RaiseAll()
        {
            _down?.Invoke(MouseButton.Left);
            _up?.Invoke(MouseButton.Left);
            _move?.Invoke(Vector2.One);
            _scroll?.Invoke(1);
        }

        private void Add<T>(ref Action<T>? slot, Action<T> callback)
        {
            AddCalls++;
            slot = callback;
            LiveEdges++;
            if (AddCalls == FailAdd) throw new InvalidOperationException("add");
        }

        private void Remove<T>(ref Action<T>? slot, Action<T> callback)
        {
            RemoveCalls++;
            if (ReferenceEquals(slot, callback)) { slot = null; LiveEdges--; }
        }
    }
}
