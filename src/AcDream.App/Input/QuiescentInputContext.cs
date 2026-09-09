using System.Numerics;
using AcDream.App.Rendering;
using Silk.NET.Input;

namespace AcDream.App.Input;

internal interface IDevToolsInputContext : IInputContext, IDisposable
{
    bool IsDisposalComplete { get; }
    void Activate();
    void Deactivate();
}

internal sealed class QuiescentInputContext : IDevToolsInputContext
{
    private readonly IInputContext _inner;
    private readonly HostQuiescenceGate _quiescence;
    private readonly QuiescentKeyboard[] _keyboards;
    private readonly QuiescentMouse[] _mice;
    private readonly Action<IInputDevice, bool> _connectionChanged;
    private Action<IInputDevice, bool>? _connectionSubscribers;
    private ResourceShutdownTransaction? _detach;
    private bool _connectionAttached;
    private int _active;
    private bool _activationStarted;
    private int _disposeRequested;

    public QuiescentInputContext(
        IInputContext inner,
        HostQuiescenceGate quiescence)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _keyboards = inner.Keyboards
            .Select(keyboard => new QuiescentKeyboard(keyboard, quiescence))
            .ToArray();
        _mice = inner.Mice
            .Select(mouse => new QuiescentMouse(mouse, quiescence))
            .ToArray();
        _connectionChanged = OnConnectionChanged;
    }

    public nint Handle => _inner.Handle;
    public IReadOnlyList<IGamepad> Gamepads => _inner.Gamepads;
    public IReadOnlyList<IJoystick> Joysticks => _inner.Joysticks;
    public IReadOnlyList<IKeyboard> Keyboards => _keyboards;
    public IReadOnlyList<IMouse> Mice => _mice;
    public IReadOnlyList<IInputDevice> OtherDevices => _inner.OtherDevices;

    public event Action<IInputDevice, bool>? ConnectionChanged
    {
        add
        {
            if (value is null) return;
            ThrowIfDisposed();
            _connectionSubscribers += value;
            if (_connectionAttached) return;
            _connectionAttached = true;
            _inner.ConnectionChanged += _connectionChanged;
        }
        remove
        {
            if (value is null) return;
            _connectionSubscribers -= value;
            if (_connectionSubscribers is not null || !_connectionAttached) return;
            _inner.ConnectionChanged -= _connectionChanged;
            _connectionAttached = false;
        }
    }

    public bool IsDisposalComplete =>
        !_connectionAttached
        && _keyboards.All(static keyboard => keyboard.IsDisposalComplete)
        && _mice.All(static mouse => mouse.IsDisposalComplete);

    public void Activate()
    {
        ThrowIfDisposed();
        if (_activationStarted)
            throw new InvalidOperationException(
                "Frontend input activation has already been attempted.");
        _activationStarted = true;
        foreach (QuiescentKeyboard keyboard in _keyboards)
            keyboard.Activate();
        foreach (QuiescentMouse mouse in _mice)
            mouse.Activate();
        Volatile.Write(ref _active, 1);
    }

    public void Deactivate()
    {
        Interlocked.Exchange(ref _active, 0);
        foreach (QuiescentKeyboard keyboard in _keyboards)
            keyboard.Deactivate();
        foreach (QuiescentMouse mouse in _mice)
            mouse.Deactivate();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        foreach (QuiescentKeyboard keyboard in _keyboards)
            keyboard.RequestDisposal();
        foreach (QuiescentMouse mouse in _mice)
            mouse.RequestDisposal();
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);

    private void OnConnectionChanged(IInputDevice device, bool connected) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                _connectionSubscribers?.Invoke(device, connected);
        });

    private ResourceShutdownTransaction EnsureDetachTransaction()
    {
        if (_detach is not null)
            return _detach;

        var operations = new List<ResourceShutdownOperation>();
        for (int i = _mice.Length - 1; i >= 0; i--)
        {
            int index = i;
            operations.Add(new ResourceShutdownOperation(
                $"frontend mouse {index}",
                _mice[index].Dispose));
        }
        for (int i = _keyboards.Length - 1; i >= 0; i--)
        {
            int index = i;
            operations.Add(new ResourceShutdownOperation(
                $"frontend keyboard {index}",
                _keyboards[index].Dispose));
        }
        operations.Add(new ResourceShutdownOperation(
            "frontend connection event",
            RemoveConnectionChanged));
        _detach = new ResourceShutdownTransaction(
            new ResourceShutdownStage("frontend input callbacks", operations.ToArray()));
        return _detach;
    }

    private void RemoveConnectionChanged()
    {
        if (!_connectionAttached) return;
        _inner.ConnectionChanged -= _connectionChanged;
        _connectionAttached = false;
        _connectionSubscribers = null;
    }

    private sealed class QuiescentKeyboard : IKeyboard, IDisposable
    {
        private readonly IKeyboard _inner;
        private readonly HostQuiescenceGate _quiescence;
        private readonly Action<IKeyboard, Key, int> _downRelay;
        private readonly Action<IKeyboard, Key, int> _upRelay;
        private readonly Action<IKeyboard, char> _charRelay;
        private Action<IKeyboard, Key, int>? _downSubscribers;
        private Action<IKeyboard, Key, int>? _upSubscribers;
        private Action<IKeyboard, char>? _charSubscribers;
        private readonly bool[] _attached = new bool[3];
        private ResourceShutdownTransaction? _detach;
        private int _disposeRequested;
        private int _active;

        public QuiescentKeyboard(IKeyboard inner, HostQuiescenceGate quiescence)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _quiescence = quiescence;
            _downRelay = OnDown;
            _upRelay = OnUp;
            _charRelay = OnChar;
        }

        public string Name => _inner.Name;
        public int Index => _inner.Index;
        public bool IsConnected => _inner.IsConnected;
        public IReadOnlyList<Key> SupportedKeys => _inner.SupportedKeys;
        public string ClipboardText
        {
            get => _inner.ClipboardText;
            set => _inner.ClipboardText = value;
        }

        public event Action<IKeyboard, Key, int>? KeyDown
        {
            add
            {
                if (value is null) return;
                ThrowIfDisposed();
                _downSubscribers += value;
                if (_attached[0]) return;
                _attached[0] = true;
                _inner.KeyDown += _downRelay;
            }
            remove
            {
                if (value is null) return;
                _downSubscribers -= value;
                if (_downSubscribers is not null || !_attached[0]) return;
                _inner.KeyDown -= _downRelay;
                _attached[0] = false;
            }
        }

        public event Action<IKeyboard, Key, int>? KeyUp
        {
            add
            {
                if (value is null) return;
                ThrowIfDisposed();
                _upSubscribers += value;
                if (_attached[1]) return;
                _attached[1] = true;
                _inner.KeyUp += _upRelay;
            }
            remove
            {
                if (value is null) return;
                _upSubscribers -= value;
                if (_upSubscribers is not null || !_attached[1]) return;
                _inner.KeyUp -= _upRelay;
                _attached[1] = false;
            }
        }

        public event Action<IKeyboard, char>? KeyChar
        {
            add
            {
                if (value is null) return;
                ThrowIfDisposed();
                _charSubscribers += value;
                if (_attached[2]) return;
                _attached[2] = true;
                _inner.KeyChar += _charRelay;
            }
            remove
            {
                if (value is null) return;
                _charSubscribers -= value;
                if (_charSubscribers is not null || !_attached[2]) return;
                _inner.KeyChar -= _charRelay;
                _attached[2] = false;
            }
        }

        public bool IsDisposalComplete => _attached.All(static value => !value);
        public bool IsKeyPressed(Key key) => _inner.IsKeyPressed(key);
        public bool IsScancodePressed(int scancode) => _inner.IsScancodePressed(scancode);
        public void BeginInput() => _inner.BeginInput();
        public void EndInput() => _inner.EndInput();
        public void Activate()
        {
            ThrowIfDisposed();
            Volatile.Write(ref _active, 1);
        }
        public void Deactivate() => Interlocked.Exchange(ref _active, 0);

        public void RequestDisposal() =>
            Interlocked.Exchange(ref _disposeRequested, 1);

        public void Dispose()
        {
            RequestDisposal();
            Deactivate();
            _detach ??= new ResourceShutdownTransaction(
                new ResourceShutdownStage("frontend keyboard callbacks",
                [
                    new("character", () => Remove(2)),
                    new("up", () => Remove(1)),
                    new("down", () => Remove(0)),
                ]));
            _detach.CompleteOrThrow();
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeRequested) != 0,
                this);

        private void OnDown(IKeyboard _, Key key, int scanCode) =>
            Invoke(() => _downSubscribers?.Invoke(this, key, scanCode));
        private void OnUp(IKeyboard _, Key key, int scanCode) =>
            Invoke(() => _upSubscribers?.Invoke(this, key, scanCode));
        private void OnChar(IKeyboard _, char value) =>
            Invoke(() => _charSubscribers?.Invoke(this, value));
        private void Invoke(Action callback) =>
            _quiescence.Invoke(() =>
            {
                if (Volatile.Read(ref _active) != 0)
                    callback();
            });

        private void Remove(int index)
        {
            if (!_attached[index]) return;
            switch (index)
            {
                case 0: _inner.KeyDown -= _downRelay; _downSubscribers = null; break;
                case 1: _inner.KeyUp -= _upRelay; _upSubscribers = null; break;
                case 2: _inner.KeyChar -= _charRelay; _charSubscribers = null; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
            _attached[index] = false;
        }
    }

    private sealed class QuiescentMouse : IMouse, IDisposable
    {
        private readonly IMouse _inner;
        private readonly HostQuiescenceGate _quiescence;
        private readonly Action<IMouse, MouseButton> _downRelay;
        private readonly Action<IMouse, MouseButton> _upRelay;
        private readonly Action<IMouse, MouseButton, Vector2> _clickRelay;
        private readonly Action<IMouse, MouseButton, Vector2> _doubleClickRelay;
        private readonly Action<IMouse, Vector2> _moveRelay;
        private readonly Action<IMouse, ScrollWheel> _scrollRelay;
        private Action<IMouse, MouseButton>? _downSubscribers;
        private Action<IMouse, MouseButton>? _upSubscribers;
        private Action<IMouse, MouseButton, Vector2>? _clickSubscribers;
        private Action<IMouse, MouseButton, Vector2>? _doubleClickSubscribers;
        private Action<IMouse, Vector2>? _moveSubscribers;
        private Action<IMouse, ScrollWheel>? _scrollSubscribers;
        private readonly bool[] _attached = new bool[6];
        private ResourceShutdownTransaction? _detach;
        private int _disposeRequested;
        private int _active;

        public QuiescentMouse(IMouse inner, HostQuiescenceGate quiescence)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _quiescence = quiescence;
            _downRelay = OnDown;
            _upRelay = OnUp;
            _clickRelay = OnClick;
            _doubleClickRelay = OnDoubleClick;
            _moveRelay = OnMove;
            _scrollRelay = OnScroll;
        }

        public string Name => _inner.Name;
        public int Index => _inner.Index;
        public bool IsConnected => _inner.IsConnected;
        public IReadOnlyList<MouseButton> SupportedButtons => _inner.SupportedButtons;
        public IReadOnlyList<ScrollWheel> ScrollWheels => _inner.ScrollWheels;
        public Vector2 Position { get => _inner.Position; set => _inner.Position = value; }
        public ICursor Cursor => _inner.Cursor;
        public int DoubleClickTime { get => _inner.DoubleClickTime; set => _inner.DoubleClickTime = value; }
        public int DoubleClickRange { get => _inner.DoubleClickRange; set => _inner.DoubleClickRange = value; }

        public event Action<IMouse, MouseButton> MouseDown
        {
            add => Add(ref _downSubscribers, value, 0, () => _inner.MouseDown += _downRelay);
            remove => RemoveSubscriber(ref _downSubscribers, value, 0, () => _inner.MouseDown -= _downRelay);
        }
        public event Action<IMouse, MouseButton> MouseUp
        {
            add => Add(ref _upSubscribers, value, 1, () => _inner.MouseUp += _upRelay);
            remove => RemoveSubscriber(ref _upSubscribers, value, 1, () => _inner.MouseUp -= _upRelay);
        }
        public event Action<IMouse, MouseButton, Vector2> Click
        {
            add => Add(ref _clickSubscribers, value, 2, () => _inner.Click += _clickRelay);
            remove => RemoveSubscriber(ref _clickSubscribers, value, 2, () => _inner.Click -= _clickRelay);
        }
        public event Action<IMouse, MouseButton, Vector2> DoubleClick
        {
            add => Add(ref _doubleClickSubscribers, value, 3, () => _inner.DoubleClick += _doubleClickRelay);
            remove => RemoveSubscriber(ref _doubleClickSubscribers, value, 3, () => _inner.DoubleClick -= _doubleClickRelay);
        }
        public event Action<IMouse, Vector2> MouseMove
        {
            add => Add(ref _moveSubscribers, value, 4, () => _inner.MouseMove += _moveRelay);
            remove => RemoveSubscriber(ref _moveSubscribers, value, 4, () => _inner.MouseMove -= _moveRelay);
        }
        public event Action<IMouse, ScrollWheel> Scroll
        {
            add => Add(ref _scrollSubscribers, value, 5, () => _inner.Scroll += _scrollRelay);
            remove => RemoveSubscriber(ref _scrollSubscribers, value, 5, () => _inner.Scroll -= _scrollRelay);
        }

        public bool IsDisposalComplete => _attached.All(static value => !value);
        public bool IsButtonPressed(MouseButton button) => _inner.IsButtonPressed(button);
        public void Activate()
        {
            ThrowIfDisposed();
            Volatile.Write(ref _active, 1);
        }
        public void Deactivate() => Interlocked.Exchange(ref _active, 0);

        public void RequestDisposal() =>
            Interlocked.Exchange(ref _disposeRequested, 1);

        public void Dispose()
        {
            RequestDisposal();
            Deactivate();
            _detach ??= new ResourceShutdownTransaction(
                new ResourceShutdownStage("frontend mouse callbacks",
                [
                    new("scroll", () => RemovePhysical(5)),
                    new("move", () => RemovePhysical(4)),
                    new("double click", () => RemovePhysical(3)),
                    new("click", () => RemovePhysical(2)),
                    new("up", () => RemovePhysical(1)),
                    new("down", () => RemovePhysical(0)),
                ]));
            _detach.CompleteOrThrow();
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeRequested) != 0,
                this);

        private void OnDown(IMouse _, MouseButton button) =>
            Invoke(() => _downSubscribers?.Invoke(this, button));
        private void OnUp(IMouse _, MouseButton button) =>
            Invoke(() => _upSubscribers?.Invoke(this, button));
        private void OnClick(IMouse _, MouseButton button, Vector2 position) =>
            Invoke(() => _clickSubscribers?.Invoke(this, button, position));
        private void OnDoubleClick(IMouse _, MouseButton button, Vector2 position) =>
            Invoke(() => _doubleClickSubscribers?.Invoke(this, button, position));
        private void OnMove(IMouse _, Vector2 position) =>
            Invoke(() => _moveSubscribers?.Invoke(this, position));
        private void OnScroll(IMouse _, ScrollWheel scroll) =>
            Invoke(() => _scrollSubscribers?.Invoke(this, scroll));
        private void Invoke(Action callback) =>
            _quiescence.Invoke(() =>
            {
                if (Volatile.Read(ref _active) != 0)
                    callback();
            });

        private void Add<T>(ref T? subscribers, T value, int index, Action attach)
            where T : Delegate
        {
            ThrowIfDisposed();
            subscribers = (T?)Delegate.Combine(subscribers, value);
            if (_attached[index]) return;
            _attached[index] = true;
            attach();
        }

        private void RemoveSubscriber<T>(
            ref T? subscribers,
            T value,
            int index,
            Action detach)
            where T : Delegate
        {
            subscribers = (T?)Delegate.Remove(subscribers, value);
            if (subscribers is not null || !_attached[index]) return;
            detach();
            _attached[index] = false;
        }

        private void RemovePhysical(int index)
        {
            if (!_attached[index]) return;
            switch (index)
            {
                case 0: _inner.MouseDown -= _downRelay; _downSubscribers = null; break;
                case 1: _inner.MouseUp -= _upRelay; _upSubscribers = null; break;
                case 2: _inner.Click -= _clickRelay; _clickSubscribers = null; break;
                case 3: _inner.DoubleClick -= _doubleClickRelay; _doubleClickSubscribers = null; break;
                case 4: _inner.MouseMove -= _moveRelay; _moveSubscribers = null; break;
                case 5: _inner.Scroll -= _scrollRelay; _scrollSubscribers = null; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
            _attached[index] = false;
        }
    }
}
