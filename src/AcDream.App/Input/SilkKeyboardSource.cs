using AcDream.App.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Input;

internal interface IKeyboardEventSurface
{
    void AddKeyDown(Action<Key> callback);
    void RemoveKeyDown(Action<Key> callback);
    void AddKeyUp(Action<Key> callback);
    void RemoveKeyUp(Action<Key> callback);
    bool IsKeyPressed(Key key);
}

internal sealed class SilkKeyboardEventSurface : IKeyboardEventSurface
{
    private readonly IKeyboard _keyboard;
    private Action<Key>? _keyDownCallback;
    private Action<Key>? _keyUpCallback;
    private readonly Action<IKeyboard, Key, int> _keyDown;
    private readonly Action<IKeyboard, Key, int> _keyUp;

    public SilkKeyboardEventSurface(IKeyboard keyboard)
    {
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _keyDown = OnKeyDown;
        _keyUp = OnKeyUp;
    }

    public void AddKeyDown(Action<Key> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _keyDownCallback = callback;
        _keyboard.KeyDown += _keyDown;
    }

    public void RemoveKeyDown(Action<Key> callback)
    {
        if (!ReferenceEquals(_keyDownCallback, callback))
            return;
        _keyboard.KeyDown -= _keyDown;
        _keyDownCallback = null;
    }

    public void AddKeyUp(Action<Key> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _keyUpCallback = callback;
        _keyboard.KeyUp += _keyUp;
    }

    public void RemoveKeyUp(Action<Key> callback)
    {
        if (!ReferenceEquals(_keyUpCallback, callback))
            return;
        _keyboard.KeyUp -= _keyUp;
        _keyUpCallback = null;
    }

    public bool IsKeyPressed(Key key) => _keyboard.IsKeyPressed(key);

    private void OnKeyDown(IKeyboard _, Key key, int __) =>
        _keyDownCallback?.Invoke(key);

    private void OnKeyUp(IKeyboard _, Key key, int __) =>
        _keyUpCallback?.Invoke(key);
}

public sealed class SilkKeyboardSource : IKeyboardSource, IDisposable
{
    private readonly IKeyboardEventSurface _surface;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<Key> _keyDown;
    private readonly Action<Key> _keyUp;
    private readonly bool[] _attached = new bool[2];
    private ResourceShutdownTransaction? _detach;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    public event Action<Key, ModifierMask>? KeyDown;
    public event Action<Key, ModifierMask>? KeyUp;

    private SilkKeyboardSource(
        IKeyboardEventSurface surface,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _keyDown = OnKeyDown;
        _keyUp = OnKeyUp;
    }

    internal static SilkKeyboardSource CreateDetached(
        IKeyboard keyboard,
        HostQuiescenceGate quiescence) =>
        new(
            new SilkKeyboardEventSurface(keyboard),
            quiescence);

    internal static SilkKeyboardSource CreateDetached(
        IKeyboardEventSurface surface,
        HostQuiescenceGate quiescence) =>
        new(surface, quiescence);

    public bool IsDisposalComplete => _attached.All(static value => !value);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException("Keyboard source attachment has already started.");
        _attachStarted = true;

        try
        {
            _attached[0] = true;
            _surface.AddKeyDown(_keyDown);
            _attached[1] = true;
            _surface.AddKeyUp(_keyUp);
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            RollBackOrThrow(attachError);
        }
    }

    public bool IsHeld(Key key) => _surface.IsKeyPressed(key);

    public ModifierMask CurrentModifiers => ReadModifiers();

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void OnKeyDown(Key key) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                KeyDown?.Invoke(key, ReadModifiers());
        });

    private void OnKeyUp(Key key) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                KeyUp?.Invoke(key, ReadModifiers());
        });

    private ModifierMask ReadModifiers()
    {
        ModifierMask modifiers = ModifierMask.None;
        if (_surface.IsKeyPressed(Key.ShiftLeft) || _surface.IsKeyPressed(Key.ShiftRight))
            modifiers |= ModifierMask.Shift;
        if (_surface.IsKeyPressed(Key.ControlLeft) || _surface.IsKeyPressed(Key.ControlRight))
            modifiers |= ModifierMask.Ctrl;
        if (_surface.IsKeyPressed(Key.AltLeft) || _surface.IsKeyPressed(Key.AltRight))
            modifiers |= ModifierMask.Alt;
        if (_surface.IsKeyPressed(Key.SuperLeft) || _surface.IsKeyPressed(Key.SuperRight))
            modifiers |= ModifierMask.Win;
        return modifiers;
    }

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("keyboard source callbacks",
            [
                new("key up", () => Remove(1)),
                new("key down", () => Remove(0)),
            ]));

    private void Remove(int index)
    {
        if (!_attached[index])
            return;
        if (index == 1)
            _surface.RemoveKeyUp(_keyUp);
        else
            _surface.RemoveKeyDown(_keyDown);
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
                "Keyboard source registration and rollback both failed.",
                new InvalidOperationException(
                    "Keyboard source registration failed.", attachError),
                rollbackError);
        }

        throw new InvalidOperationException(
            "Keyboard source registration failed and was rolled back.",
            attachError);
    }
}
