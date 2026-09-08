using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using Silk.NET.Input;

namespace AcDream.App.UI;

public sealed class UiHost : System.IDisposable
{
    public UiRoot Root { get; } = new();
    public RetailWindowManager WindowManager => Root.WindowManager;
    public TextRenderer TextRenderer { get; }
    public BitmapFont? DefaultFont { get; set; }

    public IKeyboard? Keyboard { get; private set; }

    private long _startTicks = System.Environment.TickCount64;
    private readonly HostQuiescenceGate _quiescence;
    private readonly List<IRetainedUiInputBinding> _inputBindings = new();
    private ResourceShutdownTransaction? _inputShutdown;
    private ResourceShutdownTransaction? _shutdown;
    private bool _disposeRequested;
    private bool _disposed;

    internal bool IsDisposalComplete => _disposed;

    internal UiHost(
        IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shaderDir,
        BitmapFont? defaultFont = null)
        : this(device, frameSource, shaderDir, defaultFont, new HostQuiescenceGate())
    {
    }

    internal UiHost(
        IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shaderDir,
        BitmapFont? defaultFont,
        HostQuiescenceGate quiescence)
    {
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        TextRenderer = new TextRenderer(device, frameSource, shaderDir);
        DefaultFont = defaultFont;
    }

    // ── Per-frame ──────────────────────────────────────────────────────

    public void Tick(double deltaSeconds)
    {
        long now = System.Environment.TickCount64 - _startTicks;
        Root.Tick(deltaSeconds, now);
    }

    public void Draw(Vector2 screenSize)
    {
        // Set UiRoot bounds to full screen so HitTestTopDown works.
        Root.Width = screenSize.X;
        Root.Height = screenSize.Y;
        var ctx = new UiRenderContext(TextRenderer, screenSize, DefaultFont);
        TextRenderer.Begin(screenSize);
        Root.Draw(ctx);
        TextRenderer.Flush(DefaultFont);
    }

    // ── Input wiring helpers ───────────────────────────────────────────

    public void WireMouse(IMouse mouse)
    {
        System.ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        System.ArgumentNullException.ThrowIfNull(mouse);

        var binding = new RetainedMouseInputBinding(
            new SilkRetainedMouseSurface(mouse),
            Root,
            _quiescence);
        _inputBindings.Add(binding);
        try
        {
            binding.Attach();
        }
        catch
        {
            if (binding.IsDisposalComplete)
                _inputBindings.Remove(binding);
            throw;
        }
    }

    public void WireKeyboard(IKeyboard kb)
    {
        System.ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        System.ArgumentNullException.ThrowIfNull(kb);
        Keyboard = kb;   // last wired keyboard wins (one-keyboard desktop)
        var binding = new RetainedKeyboardInputBinding(
            new SilkRetainedKeyboardSurface(kb),
            Root,
            _quiescence);
        _inputBindings.Add(binding);
        try
        {
            binding.Attach();
        }
        catch
        {
            if (binding.IsDisposalComplete)
                _inputBindings.Remove(binding);
            throw;
        }
    }

    public void QuiesceInput()
    {
        _disposeRequested = true;
        foreach (IRetainedUiInputBinding binding in _inputBindings)
            binding.Deactivate();
        Keyboard = null;
    }

    /// <summary>Physically removes every retained input edge after quiescence.</summary>
    public void DeactivateInput() => CompleteInputShutdown(reportFailures: true);

    private void CompleteInputShutdown(bool reportFailures)
    {
        QuiesceInput();

        _inputShutdown ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage(
                "retained UI input subscriptions",
                _inputBindings
                    .AsEnumerable()
                    .Reverse()
                    .Select((binding, index) => new ResourceShutdownOperation(
                        $"input binding {index}",
                        binding.Dispose,
                        ResourceShutdownOperationPolicy.ReportAndContinue))
                    .ToArray()));
        _inputShutdown.CompleteOrThrow();
        if (_inputShutdown.IsComplete && _inputShutdown.CleanupFailures.Count == 0)
            _inputBindings.Clear();
        if (reportFailures && _inputShutdown.CleanupFailures.Count != 0)
        {
            throw new AggregateException(
                "Retained UI input callback cleanup completed with failures.",
                _inputShutdown.CleanupFailures.Select(static failure =>
                    new InvalidOperationException(
                        $"Retained UI operation '{failure.Operation}' failed in stage '{failure.Stage}'.",
                        failure.Error)));
        }
    }

    // ── Window manager forwarders (delegate to UiRoot) ─────────────────

    public RetailWindowHandle RegisterWindow(
        string name,
        UiElement window,
        UiElement? contentRoot = null,
        IRetainedPanelController? controller = null,
        IRetainedWindowStateController? stateController = null,
        int authoredGeometryRevision = 0)
        => Root.RegisterWindow(
            name,
            window,
            contentRoot,
            controller,
            stateController,
            authoredGeometryRevision);

    public bool UnregisterWindow(string name) => Root.UnregisterWindow(name);

    /// <summary>Show a registered window; returns false if the name is unknown.</summary>
    public bool ShowWindow(string name) => Root.ShowWindow(name);

    /// <summary>Hide a registered window; returns false if the name is unknown.</summary>
    public bool HideWindow(string name) => Root.HideWindow(name);

    public bool CloseWindow(string name) => Root.CloseWindow(name);

    public bool IsWindowVisible(string name) => Root.IsWindowVisible(name);

    /// <summary>Toggle a registered window's visibility; returns the new IsVisible.</summary>
    public bool ToggleWindow(string name) => Root.ToggleWindow(name);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposeRequested = true;
        _shutdown ??= CreateShutdownTransaction(
            [() => CompleteInputShutdown(reportFailures: false)],
            () => { },
            WindowManager.Dispose,
            TextRenderer.Dispose);
        _shutdown.CompleteOrThrow();
        _disposed = _shutdown.IsComplete;
    }

    internal static ResourceShutdownTransaction CreateShutdownTransaction(
        IReadOnlyList<Action> inputUnsubscribers,
        Action releaseInputState,
        Action disposeWindowManager,
        Action disposeTextRenderer)
    {
        ArgumentNullException.ThrowIfNull(inputUnsubscribers);
        ArgumentNullException.ThrowIfNull(releaseInputState);
        ArgumentNullException.ThrowIfNull(disposeWindowManager);
        ArgumentNullException.ThrowIfNull(disposeTextRenderer);

        return new ResourceShutdownTransaction(
            new ResourceShutdownStage(
                "retained UI input subscriptions",
                inputUnsubscribers
                    .Select((unsubscribe, index) => new ResourceShutdownOperation(
                        $"input subscription {index}",
                        unsubscribe ?? throw new ArgumentException(
                            "Input unsubscriber entries cannot be null.",
                            nameof(inputUnsubscribers))))
                    .ToArray()),
            new ResourceShutdownStage("retained UI input state",
            [
                new("input state", releaseInputState),
            ]),
            new ResourceShutdownStage("retained UI windows",
            [
                new("window manager", disposeWindowManager),
            ]),
            new ResourceShutdownStage("retained UI renderer",
            [
                new("text renderer", disposeTextRenderer),
            ]));
    }
}
