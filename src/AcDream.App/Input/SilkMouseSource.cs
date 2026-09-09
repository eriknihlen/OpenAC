using System.Numerics;
using AcDream.App.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Input;

internal interface IMouseEventSurface
{
    void AddMouseDown(Action<MouseButton> callback);
    void RemoveMouseDown(Action<MouseButton> callback);
    void AddMouseUp(Action<MouseButton> callback);
    void RemoveMouseUp(Action<MouseButton> callback);
    void AddMouseMove(Action<Vector2> callback);
    void RemoveMouseMove(Action<Vector2> callback);
    void AddScroll(Action<float> callback);
    void RemoveScroll(Action<float> callback);
    bool IsButtonPressed(MouseButton button);
}

internal sealed class SilkMouseEventSurface : IMouseEventSurface
{
    private readonly IMouse _mouse;
    private Action<MouseButton>? _downCallback;
    private Action<MouseButton>? _upCallback;
    private Action<Vector2>? _moveCallback;
    private Action<float>? _scrollCallback;
    private readonly Action<IMouse, MouseButton> _down;
    private readonly Action<IMouse, MouseButton> _up;
    private readonly Action<IMouse, Vector2> _move;
    private readonly Action<IMouse, ScrollWheel> _scroll;

    public SilkMouseEventSurface(IMouse mouse)
    {
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _down = OnDown;
        _up = OnUp;
        _move = OnMove;
        _scroll = OnScroll;
    }

    public void AddMouseDown(Action<MouseButton> callback)
    {
        _downCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseDown += _down;
    }

    public void RemoveMouseDown(Action<MouseButton> callback)
    {
        if (!ReferenceEquals(_downCallback, callback)) return;
        _mouse.MouseDown -= _down;
        _downCallback = null;
    }

    public void AddMouseUp(Action<MouseButton> callback)
    {
        _upCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseUp += _up;
    }

    public void RemoveMouseUp(Action<MouseButton> callback)
    {
        if (!ReferenceEquals(_upCallback, callback)) return;
        _mouse.MouseUp -= _up;
        _upCallback = null;
    }

    public void AddMouseMove(Action<Vector2> callback)
    {
        _moveCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseMove += _move;
    }

    public void RemoveMouseMove(Action<Vector2> callback)
    {
        if (!ReferenceEquals(_moveCallback, callback)) return;
        _mouse.MouseMove -= _move;
        _moveCallback = null;
    }

    public void AddScroll(Action<float> callback)
    {
        _scrollCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.Scroll += _scroll;
    }

    public void RemoveScroll(Action<float> callback)
    {
        if (!ReferenceEquals(_scrollCallback, callback)) return;
        _mouse.Scroll -= _scroll;
        _scrollCallback = null;
    }

    public bool IsButtonPressed(MouseButton button) => _mouse.IsButtonPressed(button);

    private void OnDown(IMouse _, MouseButton button) => _downCallback?.Invoke(button);
    private void OnUp(IMouse _, MouseButton button) => _upCallback?.Invoke(button);
    private void OnMove(IMouse _, Vector2 position) => _moveCallback?.Invoke(position);
    private void OnScroll(IMouse _, ScrollWheel scroll) => _scrollCallback?.Invoke(scroll.Y);
}

public sealed class SilkMouseSource : IMouseSource, IDisposable
{
    private readonly IMouseEventSurface _surface;
    private readonly IInputCaptureSource _capture;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<MouseButton> _mouseDown;
    private readonly Action<MouseButton> _mouseUp;
    private readonly Action<Vector2> _mouseMove;
    private readonly Action<float> _scroll;
    private readonly bool[] _attached = new bool[4];
    private ResourceShutdownTransaction? _detach;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;
    private float _lastX;
    private float _lastY;
    private bool _haveLastPosition;

    public event Action<MouseButton, ModifierMask>? MouseDown;
    public event Action<MouseButton, ModifierMask>? MouseUp;
    public event Action<float, float>? MouseMove;
    public event Action<float>? Scroll;

    public IKeyboardSource? ModifierSource { get; set; }

    private SilkMouseSource(
        IMouseEventSurface surface,
        IInputCaptureSource capture,
        IKeyboardSource? modifierSource,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        ModifierSource = modifierSource;
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _mouseDown = OnMouseDown;
        _mouseUp = OnMouseUp;
        _mouseMove = OnMouseMove;
        _scroll = OnScroll;
    }

    internal static SilkMouseSource CreateDetached(
        IMouse mouse,
        IInputCaptureSource capture,
        IKeyboardSource? modifierSource,
        HostQuiescenceGate quiescence) =>
        new(
            new SilkMouseEventSurface(mouse),
            capture,
            modifierSource,
            quiescence);

    internal static SilkMouseSource CreateDetached(
        IMouseEventSurface surface,
        IInputCaptureSource capture,
        IKeyboardSource? modifierSource,
        HostQuiescenceGate quiescence) =>
        new(surface, capture, modifierSource, quiescence);

    public bool IsDisposalComplete => _attached.All(static value => !value);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException("Mouse source attachment has already started.");
        _attachStarted = true;
        try
        {
            _attached[0] = true;
            _surface.AddMouseDown(_mouseDown);
            _attached[1] = true;
            _surface.AddMouseUp(_mouseUp);
            _attached[2] = true;
            _surface.AddMouseMove(_mouseMove);
            _attached[3] = true;
            _surface.AddScroll(_scroll);
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            RollBackOrThrow(attachError);
        }
    }

    public bool IsHeld(MouseButton button) => _surface.IsButtonPressed(button);
    public bool WantCaptureMouse => _capture.WantCaptureMouse;
    public bool WantCaptureKeyboard => _capture.WantCaptureKeyboard;

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private ModifierMask ReadModifiers() =>
        ModifierSource?.CurrentModifiers ?? ModifierMask.None;

    private void OnMouseDown(MouseButton button) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                MouseDown?.Invoke(button, ReadModifiers());
        });

    private void OnMouseUp(MouseButton button) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                MouseUp?.Invoke(button, ReadModifiers());
        });

    private void OnMouseMove(Vector2 position) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            float dx = 0f;
            float dy = 0f;
            if (_haveLastPosition)
            {
                dx = position.X - _lastX;
                dy = position.Y - _lastY;
            }
            else
            {
                _haveLastPosition = true;
            }

            _lastX = position.X;
            _lastY = position.Y;
            MouseMove?.Invoke(dx, dy);
        });

    private void OnScroll(float delta) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                Scroll?.Invoke(delta);
        });

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("mouse source callbacks",
            [
                new("scroll", () => Remove(3)),
                new("move", () => Remove(2)),
                new("up", () => Remove(1)),
                new("down", () => Remove(0)),
            ]));

    private void Remove(int index)
    {
        if (!_attached[index])
            return;
        switch (index)
        {
            case 0: _surface.RemoveMouseDown(_mouseDown); break;
            case 1: _surface.RemoveMouseUp(_mouseUp); break;
            case 2: _surface.RemoveMouseMove(_mouseMove); break;
            case 3: _surface.RemoveScroll(_scroll); break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
        _attached[index] = false;
    }

    private void RollBackOrThrow(Exception attachError)
    {
        try
        {
            EnsureDetachTransaction().CompleteOrThrow();
        }
        catch (Exception rollbackError)
        {
            throw new AggregateException(
                "Mouse source registration and rollback both failed.",
                new InvalidOperationException(
                    "Mouse source registration failed.", attachError),
                rollbackError);
        }

        throw new InvalidOperationException(
            "Mouse source registration failed and was rolled back.",
            attachError);
    }
}
