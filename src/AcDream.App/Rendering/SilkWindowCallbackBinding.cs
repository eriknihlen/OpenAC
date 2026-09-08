using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

internal sealed class NativeWindowCallbackCleanupDeferredException()
    : InvalidOperationException(
        "Native window callback cleanup was requested reentrantly and remains pending.");

internal sealed class WindowCallbackTargets
{
    public WindowCallbackTargets(
        Action load,
        Action<double> update,
        Action<double> render,
        Action closing,
        Action<bool> focusChanged,
        Action<Vector2D<int>> framebufferResize)
    {
        Load = load ?? throw new ArgumentNullException(nameof(load));
        Update = update ?? throw new ArgumentNullException(nameof(update));
        Render = render ?? throw new ArgumentNullException(nameof(render));
        Closing = closing ?? throw new ArgumentNullException(nameof(closing));
        FocusChanged = focusChanged ?? throw new ArgumentNullException(nameof(focusChanged));
        FramebufferResize = framebufferResize
            ?? throw new ArgumentNullException(nameof(framebufferResize));
    }

    public Action Load { get; }
    public Action<double> Update { get; }
    public Action<double> Render { get; }
    public Action Closing { get; }
    public Action<bool> FocusChanged { get; }
    public Action<Vector2D<int>> FramebufferResize { get; }
}

internal interface IWindowCallbackSurface
{
    void AddLoad(Action callback);
    void RemoveLoad(Action callback);
    void AddUpdate(Action<double> callback);
    void RemoveUpdate(Action<double> callback);
    void AddRender(Action<double> callback);
    void RemoveRender(Action<double> callback);
    void AddClosing(Action callback);
    void RemoveClosing(Action callback);
    void AddFocusChanged(Action<bool> callback);
    void RemoveFocusChanged(Action<bool> callback);
    void AddMove(Action<Vector2D<int>> callback);
    void RemoveMove(Action<Vector2D<int>> callback);
    void AddStateChanged(Action<WindowState> callback);
    void RemoveStateChanged(Action<WindowState> callback);
    void AddFramebufferResize(Action<Vector2D<int>> callback);
    void RemoveFramebufferResize(Action<Vector2D<int>> callback);
}

/// <summary>Production adapter over Silk's native event surface.</summary>
internal sealed class SilkWindowCallbackSurface(IWindow window) : IWindowCallbackSurface
{
    private readonly IWindow _window = window ?? throw new ArgumentNullException(nameof(window));

    public void AddLoad(Action callback) => _window.Load += callback;
    public void RemoveLoad(Action callback) => _window.Load -= callback;
    public void AddUpdate(Action<double> callback) => _window.Update += callback;
    public void RemoveUpdate(Action<double> callback) => _window.Update -= callback;
    public void AddRender(Action<double> callback) => _window.Render += callback;
    public void RemoveRender(Action<double> callback) => _window.Render -= callback;
    public void AddClosing(Action callback) => _window.Closing += callback;
    public void RemoveClosing(Action callback) => _window.Closing -= callback;
    public void AddFocusChanged(Action<bool> callback) => _window.FocusChanged += callback;
    public void RemoveFocusChanged(Action<bool> callback) => _window.FocusChanged -= callback;
    public void AddMove(Action<Vector2D<int>> callback) => _window.Move += callback;
    public void RemoveMove(Action<Vector2D<int>> callback) => _window.Move -= callback;
    public void AddStateChanged(Action<WindowState> callback) => _window.StateChanged += callback;
    public void RemoveStateChanged(Action<WindowState> callback) => _window.StateChanged -= callback;
    public void AddFramebufferResize(Action<Vector2D<int>> callback) =>
        _window.FramebufferResize += callback;
    public void RemoveFramebufferResize(Action<Vector2D<int>> callback) =>
        _window.FramebufferResize -= callback;
}

internal sealed class SilkWindowCallbackBinding : IDisposable
{
    private enum LifecycleState
    {
        Created,
        Attaching,
        Attached,
        AttachFailed,
        Detaching,
        DetachPending,
        Detached,
    }

    private const int LoadIndex = 0;
    private const int UpdateIndex = 1;
    private const int MainRenderIndex = 2;
    private const int PacingRenderIndex = 3;
    private const int ClosingIndex = 4;
    private const int FocusChangedIndex = 5;
    private const int MoveIndex = 6;
    private const int StateChangedIndex = 7;
    private const int FramebufferResizeIndex = 8;
    private const int AttachmentCount = 9;

    private readonly IWindowCallbackSurface _surface;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action _load;
    private readonly Action<double> _update;
    private readonly Action<double> _mainRender;
    private readonly Action<double> _pacingRender;
    private readonly Action _closing;
    private readonly Action<bool> _focusChanged;
    private readonly Action<Vector2D<int>> _moved;
    private readonly Action<WindowState> _stateChanged;
    private readonly Action<Vector2D<int>> _framebufferResize;
    private readonly ResourceShutdownTransaction _detach;
    private readonly bool[] _attached = new bool[AttachmentCount];
    private readonly object _lifecycleSync = new();
    private int _attachedCount;
    private int _detachRequested;
    private int _lifecycleOwnerThreadId;
    private LifecycleState _lifecycleState = LifecycleState.Created;

    private SilkWindowCallbackBinding(
        IWindowCallbackSurface surface,
        WindowCallbackTargets targets,
        DisplayFramePacingController pacing,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(pacing);
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));

        _load = () => _quiescence.Invoke(targets.Load);
        _update = value => _quiescence.Invoke(targets.Update, value);
        _mainRender = value => _quiescence.Invoke(targets.Render, value);
        _pacingRender = value => _quiescence.Invoke(pacing.OnFrameRendered, value);
        _closing = () => _quiescence.Invoke(targets.Closing);
        _focusChanged = value => _quiescence.Invoke(targets.FocusChanged, value);
        _moved = value => _quiescence.Invoke(pacing.OnWindowMoved, value);
        _stateChanged = value => _quiescence.Invoke(pacing.OnWindowStateChanged, value);
        _framebufferResize = value => _quiescence.Invoke(targets.FramebufferResize, value);

        _detach = new ResourceShutdownTransaction(
            new ResourceShutdownStage("native window callbacks",
            [
                new("framebuffer resize", () => Remove(FramebufferResizeIndex)),
                new("window state", () => Remove(StateChangedIndex)),
                new("window move", () => Remove(MoveIndex)),
                new("focus changed", () => Remove(FocusChangedIndex)),
                new("closing", () => Remove(ClosingIndex)),
                new("pacing render", () => Remove(PacingRenderIndex)),
                new("main render", () => Remove(MainRenderIndex)),
                new("update", () => Remove(UpdateIndex)),
                new("load", () => Remove(LoadIndex)),
            ]));
    }

    public bool IsDetached
    {
        get
        {
            lock (_lifecycleSync)
                return _attached.All(static attached => !attached);
        }
    }

    public bool IsDisposalComplete
    {
        get
        {
            lock (_lifecycleSync)
                return _lifecycleState == LifecycleState.Detached;
        }
    }

    public static SilkWindowCallbackBinding Create(
        IWindow window,
        WindowCallbackTargets targets,
        DisplayFramePacingController pacing,
        HostQuiescenceGate quiescence) =>
        Create(new SilkWindowCallbackSurface(window), targets, pacing, quiescence);

    internal static SilkWindowCallbackBinding Create(
        IWindowCallbackSurface surface,
        WindowCallbackTargets targets,
        DisplayFramePacingController pacing,
        HostQuiescenceGate quiescence) =>
        new(surface, targets, pacing, quiescence);

    public void Attach()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifecycleSync)
        {
            if (_lifecycleState != LifecycleState.Created)
            {
                throw _lifecycleState == LifecycleState.Detached
                    ? new ObjectDisposedException(nameof(SilkWindowCallbackBinding))
                    : new InvalidOperationException(
                        "Native window callback attachment has already started.");
            }
            if (Volatile.Read(ref _detachRequested) != 0 || _detach.IsComplete)
                throw new ObjectDisposedException(nameof(SilkWindowCallbackBinding));

            _lifecycleState = LifecycleState.Attaching;
            _lifecycleOwnerThreadId = threadId;
        }

        try
        {
            Attach(LoadIndex);
            Attach(UpdateIndex);
            Attach(MainRenderIndex);
            Attach(PacingRenderIndex);
            Attach(ClosingIndex);
            Attach(FocusChangedIndex);
            Attach(MoveIndex);
            Attach(StateChangedIndex);
            Attach(FramebufferResizeIndex);

            lock (_lifecycleSync)
            {
                if (Volatile.Read(ref _detachRequested) != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(SilkWindowCallbackBinding),
                        "Shutdown was requested while native callbacks were attaching.");
                }

                _lifecycleState = LifecycleState.Attached;
                _lifecycleOwnerThreadId = 0;
                System.Threading.Monitor.PulseAll(_lifecycleSync);
            }
        }
        catch (Exception attachError)
        {
            Interlocked.Exchange(ref _detachRequested, 1);
            _quiescence.StopAccepting();
            List<Exception>? rollbackErrors = null;
            for (int index = _attachedCount - 1; index >= 0; index--)
            {
                try
                {
                    Remove(index);
                }
                catch (Exception rollbackError)
                {
                    (rollbackErrors ??= []).Add(new InvalidOperationException(
                        $"Failed to roll back native window callback index {index}.",
                        rollbackError));
                }
            }

            lock (_lifecycleSync)
            {
                _lifecycleState = LifecycleState.AttachFailed;
                _lifecycleOwnerThreadId = 0;
                System.Threading.Monitor.PulseAll(_lifecycleSync);
            }

            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, new InvalidOperationException(
                    "Native window callback registration failed.", attachError));
                throw new AggregateException(
                    "Native window callback registration and rollback both failed.",
                    rollbackErrors);
            }

            throw new InvalidOperationException(
                "Native window callback registration failed and was rolled back.",
                attachError);
        }
    }

    private void Attach(int index)
    {
        lock (_lifecycleSync)
        {
            _attached[index] = true;
            _attachedCount = Math.Max(_attachedCount, index + 1);
        }
        switch (index)
        {
            case LoadIndex: _surface.AddLoad(_load); break;
            case UpdateIndex: _surface.AddUpdate(_update); break;
            case MainRenderIndex: _surface.AddRender(_mainRender); break;
            case PacingRenderIndex: _surface.AddRender(_pacingRender); break;
            case ClosingIndex: _surface.AddClosing(_closing); break;
            case FocusChangedIndex: _surface.AddFocusChanged(_focusChanged); break;
            case MoveIndex: _surface.AddMove(_moved); break;
            case StateChangedIndex: _surface.AddStateChanged(_stateChanged); break;
            case FramebufferResizeIndex:
                _surface.AddFramebufferResize(_framebufferResize);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (Volatile.Read(ref _detachRequested) != 0)
        {
            throw new ObjectDisposedException(
                nameof(SilkWindowCallbackBinding),
                "Shutdown was requested while native callbacks were attaching.");
        }
    }

    private void Remove(int index)
    {
        lock (_lifecycleSync)
        {
            if (index >= _attachedCount || !_attached[index])
                return;
        }

        switch (index)
        {
            case LoadIndex: _surface.RemoveLoad(_load); break;
            case UpdateIndex: _surface.RemoveUpdate(_update); break;
            case MainRenderIndex: _surface.RemoveRender(_mainRender); break;
            case PacingRenderIndex: _surface.RemoveRender(_pacingRender); break;
            case ClosingIndex: _surface.RemoveClosing(_closing); break;
            case FocusChangedIndex: _surface.RemoveFocusChanged(_focusChanged); break;
            case MoveIndex: _surface.RemoveMove(_moved); break;
            case StateChangedIndex: _surface.RemoveStateChanged(_stateChanged); break;
            case FramebufferResizeIndex:
                _surface.RemoveFramebufferResize(_framebufferResize);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }

        lock (_lifecycleSync)
            _attached[index] = false;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _detachRequested, 1);
        _quiescence.StopAccepting();
        int threadId = Environment.CurrentManagedThreadId;
        bool ownsGate = _quiescence.IsEnteredByCurrentThread;
        while (true)
        {
            lock (_lifecycleSync)
            {
                if (_lifecycleState == LifecycleState.Detached)
                    return;
                if (_lifecycleState == LifecycleState.Attaching)
                {
                    if (_lifecycleOwnerThreadId == threadId || ownsGate)
                        throw new NativeWindowCallbackCleanupDeferredException();

                    System.Threading.Monitor.Wait(_lifecycleSync);
                    continue;
                }
                if (_lifecycleState == LifecycleState.Detaching)
                {
                    if (_lifecycleOwnerThreadId == threadId)
                        return;
                    if (ownsGate)
                        throw new NativeWindowCallbackCleanupDeferredException();

                    System.Threading.Monitor.Wait(_lifecycleSync);
                    continue;
                }

                _lifecycleState = LifecycleState.Detaching;
                _lifecycleOwnerThreadId = threadId;
                break;
            }
        }

        try
        {
            _detach.CompleteOrThrow();
            lock (_lifecycleSync)
            {
                _lifecycleState = LifecycleState.Detached;
                _lifecycleOwnerThreadId = 0;
                System.Threading.Monitor.PulseAll(_lifecycleSync);
            }
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _lifecycleState = LifecycleState.DetachPending;
                _lifecycleOwnerThreadId = 0;
                System.Threading.Monitor.PulseAll(_lifecycleSync);
            }

            throw;
        }
    }
}
