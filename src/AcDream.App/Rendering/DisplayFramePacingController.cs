using AcDream.App.Diagnostics;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

internal interface IDisplayFramePacingSurface
{
    bool VSync { get; set; }

    bool TryGetActiveMonitorRefreshHz(out int refreshHz);
}

/// <summary>Narrow Silk window adapter for display pacing.</summary>
internal sealed class SilkDisplayFramePacingSurface : IDisplayFramePacingSurface
{
    private readonly IWindow _window;

    public SilkDisplayFramePacingSurface(IWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public bool VSync
    {
        get => _window.VSync;
        set => _window.VSync = value;
    }

    public bool TryGetActiveMonitorRefreshHz(out int refreshHz)
    {
        refreshHz = 0;
        try
        {
            int? activeRefreshHz = _window.Monitor?.VideoMode.RefreshRate;
            if (activeRefreshHz is not > 0)
                return false;

            refreshHz = activeRefreshHz.Value;
            return true;
        }
        catch (Silk.NET.GLFW.GlfwException)
        {
            return false;
        }
    }
}

internal sealed class DisplayFramePacingController : IDisposable
{
    private readonly bool _uncappedRendering;
    private FrameProfiler? _profiler;
    private readonly FramePacingController _pacing;
    private IDisplayFramePacingSurface? _surface;
    private int? _activeMonitorRefreshHz;
    private bool _requestedVSync =
        AcDream.UI.Abstractions.Panels.Settings.DisplaySettings.Default.VSync;

    public DisplayFramePacingController(
        bool uncappedRendering,
        FrameProfiler profiler)
        : this(uncappedRendering, profiler, new FramePacingController())
    {
    }

    internal DisplayFramePacingController(
        bool uncappedRendering,
        FrameProfiler profiler,
        IFramePacingWaiterFactory waiterFactory)
        : this(
            uncappedRendering,
            profiler,
            new FramePacingController(waiterFactory))
    {
    }

    internal DisplayFramePacingController(
        bool uncappedRendering,
        FrameProfiler profiler,
        FramePacingController pacing)
    {
        _uncappedRendering = uncappedRendering;
        _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
        _pacing = pacing ?? throw new ArgumentNullException(nameof(pacing));
    }

    internal bool RequestedVSync => _requestedVSync;

    internal FramePacingPolicy Policy => _pacing.Policy;

    internal bool IsSurfaceBound => _surface is not null;

    public FramePacingPolicy InitializeStartup(bool requestedVSync)
    {
        _requestedVSync = requestedVSync;
        FramePacingPolicy policy = Resolve(monitorRefreshHz: null);
        _pacing.Apply(policy);
        return policy;
    }

    public void BindSurface(IDisplayFramePacingSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public void ApplyPreference(bool requestedVSync)
    {
        _requestedVSync = requestedVSync;
        ApplyResolvedPolicy();
    }

    public void RefreshActiveMonitor()
    {
        _activeMonitorRefreshHz =
            _surface?.TryGetActiveMonitorRefreshHz(out int refreshHz) == true
                ? refreshHz
                : null;
        ApplyResolvedPolicy();
    }

    public void OnWindowMoved(Vector2D<int> _) => RefreshActiveMonitor();

    public void OnWindowStateChanged(WindowState _) => RefreshActiveMonitor();

    public void OnFrameRendered(double _)
    {
        FrameProfiler profiler = _profiler
            ?? throw new ObjectDisposedException(nameof(DisplayFramePacingController));
        using var pacingStage = profiler.BeginStage(FrameStage.Pacing);
        _pacing.CompleteFrame();
    }

    private FramePacingPolicy Resolve(int? monitorRefreshHz) =>
        FramePacingPolicy.Resolve(
            _requestedVSync,
            _uncappedRendering,
            monitorRefreshHz);

    private void ApplyResolvedPolicy()
    {
        FramePacingPolicy policy = Resolve(_activeMonitorRefreshHz);
        _pacing.Apply(policy);
        if (_surface is not null && _surface.VSync != policy.UseVSync)
            _surface.VSync = policy.UseVSync;
    }

    public void Dispose()
    {
        try
        {
            _pacing.Dispose();
        }
        finally
        {
            _surface = null;
            _profiler = null;
        }
    }
}
