using AcDream.App.Diagnostics;
using AcDream.App.Platform;
using AcDream.App.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanBringUpHost : IDisposable
{
    internal const int FlightCount = 2;

    internal static readonly float[] ClearColor = [0.043f, 0.075f, 0.153f, 1f];

    private const string ScreenshotName = "vulkan-bringup";

    private readonly RuntimeOptions _options;
    private readonly GraphicalHostPlatformServices _platform;
    private readonly FramePacingPolicy _pacing;
    private readonly Action<string> _log;

    private IWindow? _window;
    private VulkanGraphicsContext? _graphics;
    private VulkanRhiScene? _scene;
    private VulkanRetainedUiScene? _ui;
    private ulong _frameSerial;
    private bool _disposed;

    internal VulkanBringUpHost(
        RuntimeOptions options,
        GraphicalHostPlatformServices platform,
        bool requestedVSync,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _log = log ?? Console.WriteLine;
        _pacing = FramePacingPolicy.Resolve(
            requestedVSync,
            _options.UncappedRendering,
            monitorRefreshHz: null);
    }

    internal VulkanCapabilityRecord? Capabilities => _graphics?.Capabilities;

    internal void Run()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CreateWindow();
        _graphics = VulkanGraphicsContext.Acquire(
            _window!,
            _options,
            _platform,
            _pacing,
            // Four samples where the device allows it, so the backbuffer pass
            // really resolves rather than rendering straight into the swapchain
            // image. Plan §4.10 records that the V7 differential must force MSAA
            // off; this is not that gate, and a resolve path that is never
            // exercised is a resolve path that does not work.
            requestedSampleCount: 4,
            _log);
        CreateScenes();
        Present();
    }

    private void CreateWindow()
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "acdream — Vulkan capability probe",
            VSync = _pacing.UseVSync,
        };
        _window = Window.Create(options);
        _window.Initialize();
    }

    private void CreateScenes()
    {
        VulkanGraphicsContext graphics = _graphics!;
        _scene = new VulkanRhiScene(graphics.Device, graphics.SampleCount);
        _ui = new VulkanRetainedUiScene(
            graphics.Device,
            VulkanGraphicsContext.ShaderSpirvDirectory());
        _log(
            "vulkan: retained UI up — TextRenderer and DebugLineRenderer on the Vulkan device" +
            (_ui.HasFont ? string.Empty : " (no system font found; glyph draws are skipped)"));
    }

    private void Present()
    {
        IWindow window = _window!;
        VulkanGraphicsContext graphics = _graphics!;
        VulkanGpuDevice device = graphics.Device;
        VulkanRhiScene scene = _scene!;
        FrameScreenshotController? screenshots = CreateScreenshotController();
        bool screenshotRequested = false;
        DateTimeOffset started = DateTimeOffset.UtcNow;

        while (!window.IsClosing)
        {
            window.DoEvents();
            if (window.IsClosing)
                break;

            if (!graphics.PrepareFrame())
            {
                Thread.Sleep(16);
                continue;
            }

            if (!device.TryBeginFrame(out IGpuFrame? frame) || frame is null)
            {
                graphics.RequestRecreate();
                continue;
            }

            double elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
            using (frame)
            {
                scene.Render(frame, graphics.Width, graphics.Height, elapsed);
                _ui?.Render(frame, graphics.Width, graphics.Height, elapsed);
            }

            _frameSerial = (ulong)frame.Serial;
            graphics.NoteFrameClosed();

            if (screenshots is not null && !screenshotRequested && _frameSerial >= 4)
            {
                screenshotRequested = true;
                if (screenshots.TryRequest(ScreenshotName, out string error))
                {
                    screenshots.CapturePending((int)graphics.Width, (int)graphics.Height);
                    ReportTimings(device);
                }
                else
                {
                    _log($"vulkan: screenshot request rejected: {error}");
                }
            }

            if (ShouldRetire(screenshots, screenshotRequested))
            {
                _log(
                    $"vulkan: frame budget of {_options.VulkanCapabilityProbeFrames} " +
                    "reached; closing the probe.");
                break;
            }
        }

        device.WaitIdle();
        _log($"vulkan: presented {_frameSerial} RHI frame(s); shutting down.");
    }

    private bool ShouldRetire(
        FrameScreenshotController? screenshots,
        bool screenshotRequested) =>
        ShouldRetire(
            _options.VulkanCapabilityProbeFrames,
            _frameSerial,
            screenshots is not null,
            screenshotRequested);

    internal static bool ShouldRetire(
        int frameBudget,
        ulong presentedFrames,
        bool capturesScreenshot,
        bool screenshotRequested)
    {
        if (frameBudget <= 0)
            return false;
        if (presentedFrames < (ulong)frameBudget)
            return false;
        return !capturesScreenshot || screenshotRequested;
    }

    private void ReportTimings(VulkanGpuDevice device)
    {
        if (!device.Timers.IsSupported)
        {
            _log("vulkan: GPU timestamps are unsupported on this device");
            return;
        }

        string offscreen = device.Timers.TryResolve("offscreen", out double offscreenMs)
            ? $"{offscreenMs:F3} ms"
            : "pending";
        string main = device.Timers.TryResolve("main", out double mainMs)
            ? $"{mainMs:F3} ms"
            : "pending";
        _log($"vulkan: GPU timer scopes — offscreen {offscreen}, main {main}");
    }

    private FrameScreenshotController? CreateScreenshotController()
    {
        if (string.IsNullOrWhiteSpace(_options.AutomationArtifactDirectory))
            return null;

        return new FrameScreenshotController(
            (width, height) => FrameScreenshotController.FlipRows(
                _graphics!.Device.CaptureBackbuffer(width, height),
                width,
                height),
            _options.AutomationArtifactDirectory,
            _log);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _ui?.Dispose();
        _ui = null;
        _scene?.Dispose();
        _scene = null;
        _graphics?.Dispose();
        _graphics = null;
        _window?.Dispose();
        _window = null;
    }
}
