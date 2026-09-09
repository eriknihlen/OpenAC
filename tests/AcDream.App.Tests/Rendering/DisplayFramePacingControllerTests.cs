using AcDream.App.Diagnostics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class DisplayFramePacingControllerTests
{
    [Fact]
    public void Binding_surface_preserves_startup_fallback_until_monitor_refresh()
    {
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface
        {
            VSync = true,
            ActiveMonitorRefreshHz = 144,
        };

        FramePacingPolicy startup = controller.InitializeStartup(
            requestedVSync: false);
        controller.BindSurface(surface);

        Assert.Equal(FramePacingPolicy.FallbackRefreshHz, startup.SoftwareLimitHz);
        Assert.Equal(startup, controller.Policy);

        controller.RefreshActiveMonitor();

        Assert.Equal(new FramePacingPolicy(false, 144d), controller.Policy);
        Assert.False(surface.VSync);
    }

    [Fact]
    public void Preference_and_monitor_changes_resolve_one_owned_policy()
    {
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface
        {
            VSync = false,
            ActiveMonitorRefreshHz = 120,
        };
        controller.InitializeStartup(requestedVSync: false);
        controller.BindSurface(surface);
        controller.RefreshActiveMonitor();

        controller.ApplyPreference(requestedVSync: true);
        Assert.Equal(new FramePacingPolicy(true, null), controller.Policy);
        Assert.True(surface.VSync);

        surface.ActiveMonitorRefreshHz = 165;
        controller.ApplyPreference(requestedVSync: false);
        Assert.Equal(new FramePacingPolicy(false, 120d), controller.Policy);
        Assert.False(surface.VSync);

        controller.OnWindowStateChanged(default);
        Assert.Equal(new FramePacingPolicy(false, 165d), controller.Policy);

        surface.ActiveMonitorRefreshHz = 75;
        controller.OnWindowMoved(default);
        Assert.Equal(new FramePacingPolicy(false, 75d), controller.Policy);
    }

    [Fact]
    public void Repeated_preference_preview_uses_the_event_refreshed_monitor_cache()
    {
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface
        {
            VSync = false,
            ActiveMonitorRefreshHz = 144,
        };
        controller.InitializeStartup(requestedVSync: false);
        controller.BindSurface(surface);
        controller.RefreshActiveMonitor();
        Assert.Equal(1, surface.RefreshReadCount);

        controller.ApplyPreference(requestedVSync: false);
        controller.ApplyPreference(requestedVSync: false);
        controller.ApplyPreference(requestedVSync: false);

        Assert.Equal(1, surface.RefreshReadCount);
        Assert.Equal(new FramePacingPolicy(false, 144d), controller.Policy);
    }

    [Fact]
    public void Unavailable_monitor_during_display_handoff_uses_fallback_policy()
    {
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface
        {
            VSync = false,
            ActiveMonitorRefreshHz = 144,
        };
        controller.InitializeStartup(requestedVSync: false);
        controller.BindSurface(surface);
        controller.RefreshActiveMonitor();
        Assert.Equal(new FramePacingPolicy(false, 144d), controller.Policy);

        surface.MonitorAvailable = false;
        controller.OnWindowMoved(default);

        Assert.Equal(
            new FramePacingPolicy(
                false,
                FramePacingPolicy.FallbackRefreshHz),
            controller.Policy);
        Assert.False(surface.VSync);
    }

    [Fact]
    public void Explicit_uncapped_mode_disables_both_pacing_mechanisms()
    {
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: true,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface
        {
            VSync = true,
            ActiveMonitorRefreshHz = 240,
        };
        controller.InitializeStartup(requestedVSync: true);
        controller.BindSurface(surface);

        controller.RefreshActiveMonitor();

        Assert.Equal(new FramePacingPolicy(false, null), controller.Policy);
        Assert.False(surface.VSync);
    }

    [Fact]
    public void Render_completion_delegates_to_the_owned_software_pacer()
    {
        var clock = new FakeClock(1_000);
        var waiter = new RecordingWaiter(clock);
        using var profiler = new FrameProfiler();
        using var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(clock, waiter));
        controller.InitializeStartup(requestedVSync: false);

        clock.Timestamp = 10;
        controller.OnFrameRendered(0d);

        Assert.Equal([7L], waiter.Durations);
    }

    [Fact]
    public void Dispose_releases_borrowed_surface_and_profiler_without_disposing_surface()
    {
        using var profiler = new FrameProfiler();
        var controller = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(
                new FakeClock(1_000),
                new RecordingWaiter()));
        var surface = new FakeSurface();
        controller.BindSurface(surface);
        Assert.True(controller.IsSurfaceBound);

        controller.Dispose();
        controller.Dispose();

        Assert.False(controller.IsSurfaceBound);
        Assert.Throws<ObjectDisposedException>(() => controller.OnFrameRendered(0d));
        Assert.False(surface.Disposed);
    }

    private sealed class FakeSurface : IDisplayFramePacingSurface, IDisposable
    {
        public bool Disposed { get; private set; }

        public bool VSync { get; set; }

        private int? _activeMonitorRefreshHz;

        public int RefreshReadCount { get; private set; }

        public bool MonitorAvailable { get; set; } = true;

        public int? ActiveMonitorRefreshHz
        {
            get => _activeMonitorRefreshHz;
            set => _activeMonitorRefreshHz = value;
        }

        public bool TryGetActiveMonitorRefreshHz(out int refreshHz)
        {
            RefreshReadCount++;
            refreshHz = _activeMonitorRefreshHz.GetValueOrDefault();
            return MonitorAvailable &&
                _activeMonitorRefreshHz is > 0;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeClock(long frequency) : IFramePacingClock
    {
        public long Frequency { get; } = frequency;

        public long Timestamp { get; set; }

        public long GetTimestamp() => Timestamp;
    }

    private sealed class RecordingWaiter(FakeClock? clock = null) : IFramePacingWaiter
    {
        public List<long> Durations { get; } = [];

        public void Wait(long durationTicks, long clockFrequency)
        {
            Durations.Add(durationTicks);
            if (clock is not null)
                clock.Timestamp += durationTicks;
        }
    }
}
