using System.Numerics;
using AcDream.App.Rendering;
using Silk.NET.Input;

namespace AcDream.App.UI;

internal interface IRetainedUiInputBinding : IDisposable
{
    bool IsDisposalComplete { get; }
    void Attach();
    void Deactivate();
}

internal interface IRetainedMouseSurface
{
    void AddMouseDown(Action<MouseButton, int, int> callback);
    void RemoveMouseDown(Action<MouseButton, int, int> callback);
    void AddMouseUp(Action<MouseButton, int, int> callback);
    void RemoveMouseUp(Action<MouseButton, int, int> callback);
    void AddMouseMove(Action<int, int> callback);
    void RemoveMouseMove(Action<int, int> callback);
    void AddScroll(Action<int> callback);
    void RemoveScroll(Action<int> callback);
}

internal sealed class SilkRetainedMouseSurface : IRetainedMouseSurface
{
    private readonly IMouse _mouse;
    private Action<MouseButton, int, int>? _downCallback;
    private Action<MouseButton, int, int>? _upCallback;
    private Action<int, int>? _moveCallback;
    private Action<int>? _scrollCallback;
    private readonly Action<IMouse, MouseButton> _down;
    private readonly Action<IMouse, MouseButton> _up;
    private readonly Action<IMouse, Vector2> _move;
    private readonly Action<IMouse, ScrollWheel> _scroll;

    public SilkRetainedMouseSurface(IMouse mouse)
    {
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _down = OnDown;
        _up = OnUp;
        _move = OnMove;
        _scroll = OnScroll;
    }

    public void AddMouseDown(Action<MouseButton, int, int> callback)
    {
        _downCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseDown += _down;
    }

    public void RemoveMouseDown(Action<MouseButton, int, int> callback)
    {
        if (!ReferenceEquals(_downCallback, callback)) return;
        _mouse.MouseDown -= _down;
        _downCallback = null;
    }

    public void AddMouseUp(Action<MouseButton, int, int> callback)
    {
        _upCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseUp += _up;
    }

    public void RemoveMouseUp(Action<MouseButton, int, int> callback)
    {
        if (!ReferenceEquals(_upCallback, callback)) return;
        _mouse.MouseUp -= _up;
        _upCallback = null;
    }

    public void AddMouseMove(Action<int, int> callback)
    {
        _moveCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.MouseMove += _move;
    }

    public void RemoveMouseMove(Action<int, int> callback)
    {
        if (!ReferenceEquals(_moveCallback, callback)) return;
        _mouse.MouseMove -= _move;
        _moveCallback = null;
    }

    public void AddScroll(Action<int> callback)
    {
        _scrollCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _mouse.Scroll += _scroll;
    }

    public void RemoveScroll(Action<int> callback)
    {
        if (!ReferenceEquals(_scrollCallback, callback)) return;
        _mouse.Scroll -= _scroll;
        _scrollCallback = null;
    }

    private void OnDown(IMouse sender, MouseButton button) =>
        _downCallback?.Invoke(button, (int)sender.Position.X, (int)sender.Position.Y);
    private void OnUp(IMouse sender, MouseButton button) =>
        _upCallback?.Invoke(button, (int)sender.Position.X, (int)sender.Position.Y);
    private void OnMove(IMouse _, Vector2 position) =>
        _moveCallback?.Invoke((int)position.X, (int)position.Y);
    private void OnScroll(IMouse _, ScrollWheel scroll) =>
        _scrollCallback?.Invoke((int)scroll.Y);
}

internal sealed class RetainedMouseInputBinding : IRetainedUiInputBinding
{
    private readonly IRetainedMouseSurface _surface;
    private readonly UiRoot _root;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<MouseButton, int, int> _down;
    private readonly Action<MouseButton, int, int> _up;
    private readonly Action<int, int> _move;
    private readonly Action<int> _scroll;
    private readonly bool[] _attached = new bool[4];
    private ResourceShutdownTransaction? _detach;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    public RetainedMouseInputBinding(
        IRetainedMouseSurface surface,
        UiRoot root,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _down = OnDown;
        _up = OnUp;
        _move = OnMove;
        _scroll = OnScroll;
    }

    public bool IsDisposalComplete => _attached.All(static value => !value);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException("Retained mouse attachment has already started.");
        _attachStarted = true;
        try
        {
            _attached[0] = true;
            _surface.AddMouseDown(_down);
            _attached[1] = true;
            _surface.AddMouseUp(_up);
            _attached[2] = true;
            _surface.AddMouseMove(_move);
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

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void OnDown(MouseButton button, int x, int y) =>
        Invoke(() => _root.OnMouseDown(MapButton(button), x, y));
    private void OnUp(MouseButton button, int x, int y) =>
        Invoke(() => _root.OnMouseUp(MapButton(button), x, y));
    private void OnMove(int x, int y) => Invoke(() => _root.OnMouseMove(x, y));
    private void OnScroll(int amount) => Invoke(() => _root.OnScroll(amount));

    private void Invoke(Action callback) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                callback();
        });

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("retained mouse callbacks",
            [
                new("scroll", () => Remove(3)),
                new("move", () => Remove(2)),
                new("up", () => Remove(1)),
                new("down", () => Remove(0)),
            ]));

    private void Remove(int index)
    {
        if (!_attached[index]) return;
        switch (index)
        {
            case 0: _surface.RemoveMouseDown(_down); break;
            case 1: _surface.RemoveMouseUp(_up); break;
            case 2: _surface.RemoveMouseMove(_move); break;
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
                "Retained mouse registration and rollback both failed.",
                new InvalidOperationException("Retained mouse registration failed.", attachError),
                rollbackError);
        }
        throw new InvalidOperationException(
            "Retained mouse registration failed and was rolled back.", attachError);
    }

    private static UiMouseButton MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => UiMouseButton.Left,
        MouseButton.Right => UiMouseButton.Right,
        MouseButton.Middle => UiMouseButton.Middle,
        _ => UiMouseButton.Left,
    };
}

internal interface IRetainedKeyboardSurface
{
    void AddKeyDown(Action<Key> callback);
    void RemoveKeyDown(Action<Key> callback);
    void AddKeyUp(Action<Key> callback);
    void RemoveKeyUp(Action<Key> callback);
    void AddKeyChar(Action<char> callback);
    void RemoveKeyChar(Action<char> callback);
}

internal sealed class SilkRetainedKeyboardSurface : IRetainedKeyboardSurface
{
    private readonly IKeyboard _keyboard;
    private Action<Key>? _downCallback;
    private Action<Key>? _upCallback;
    private Action<char>? _charCallback;
    private readonly Action<IKeyboard, Key, int> _down;
    private readonly Action<IKeyboard, Key, int> _up;
    private readonly Action<IKeyboard, char> _char;

    public SilkRetainedKeyboardSurface(IKeyboard keyboard)
    {
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _down = OnDown;
        _up = OnUp;
        _char = OnChar;
    }

    public void AddKeyDown(Action<Key> callback)
    {
        _downCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _keyboard.KeyDown += _down;
    }

    public void RemoveKeyDown(Action<Key> callback)
    {
        if (!ReferenceEquals(_downCallback, callback)) return;
        _keyboard.KeyDown -= _down;
        _downCallback = null;
    }

    public void AddKeyUp(Action<Key> callback)
    {
        _upCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _keyboard.KeyUp += _up;
    }

    public void RemoveKeyUp(Action<Key> callback)
    {
        if (!ReferenceEquals(_upCallback, callback)) return;
        _keyboard.KeyUp -= _up;
        _upCallback = null;
    }

    public void AddKeyChar(Action<char> callback)
    {
        _charCallback = callback ?? throw new ArgumentNullException(nameof(callback));
        _keyboard.KeyChar += _char;
    }

    public void RemoveKeyChar(Action<char> callback)
    {
        if (!ReferenceEquals(_charCallback, callback)) return;
        _keyboard.KeyChar -= _char;
        _charCallback = null;
    }

    private void OnDown(IKeyboard _, Key key, int __) => _downCallback?.Invoke(key);
    private void OnUp(IKeyboard _, Key key, int __) => _upCallback?.Invoke(key);
    private void OnChar(IKeyboard _, char value) => _charCallback?.Invoke(value);
}

internal sealed class RetainedKeyboardInputBinding : IRetainedUiInputBinding
{
    private readonly IRetainedKeyboardSurface _surface;
    private readonly UiRoot _root;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<Key> _down;
    private readonly Action<Key> _up;
    private readonly Action<char> _char;
    private readonly bool[] _attached = new bool[3];
    private ResourceShutdownTransaction? _detach;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    public RetainedKeyboardInputBinding(
        IRetainedKeyboardSurface surface,
        UiRoot root,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _down = key => Invoke(() => _root.OnKeyDown((int)key));
        _up = key => Invoke(() => _root.OnKeyUp((int)key));
        _char = value => Invoke(() => _root.OnChar(value));
    }

    public bool IsDisposalComplete => _attached.All(static value => !value);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException("Retained keyboard attachment has already started.");
        _attachStarted = true;
        try
        {
            _attached[0] = true;
            _surface.AddKeyDown(_down);
            _attached[1] = true;
            _surface.AddKeyUp(_up);
            _attached[2] = true;
            _surface.AddKeyChar(_char);
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            RollBackOrThrow(attachError);
        }
    }

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void Invoke(Action callback) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                callback();
        });

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("retained keyboard callbacks",
            [
                new("character", () => Remove(2)),
                new("up", () => Remove(1)),
                new("down", () => Remove(0)),
            ]));

    private void Remove(int index)
    {
        if (!_attached[index]) return;
        switch (index)
        {
            case 0: _surface.RemoveKeyDown(_down); break;
            case 1: _surface.RemoveKeyUp(_up); break;
            case 2: _surface.RemoveKeyChar(_char); break;
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
                "Retained keyboard registration and rollback both failed.",
                new InvalidOperationException("Retained keyboard registration failed.", attachError),
                rollbackError);
        }
        throw new InvalidOperationException(
            "Retained keyboard registration failed and was rolled back.", attachError);
    }
}
