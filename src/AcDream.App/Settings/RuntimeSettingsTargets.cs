using System.Globalization;
using System.Linq;
using AcDream.App.Audio;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.UI.Abstractions.Settings;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Settings;

internal interface IRuntimeDisplayWindowTarget
{
    RuntimeDisplayApplyResult Apply(DisplaySettings display);
}

internal readonly record struct RuntimeDisplayApplyResult(bool Fullscreen);

internal interface IRuntimeQualityApplicationTarget
{
    void SetAlphaToCoverage(bool enabled);

    void SetAnisotropic(int level);

    void PublishRenderRange(int nearRadius, int farRadius);

    void ReconfigureStreamingRadii(int nearRadius, int farRadius);

    void SetCompletionBudget(int maxCompletionsPerFrame);
}

internal interface IRuntimeUiLockTarget
{
    void Apply(bool locked);
}

internal interface IRuntimeChatOpacityTarget
{
    void Apply(float defaultOpacity, float activeOpacity);
}

internal interface IWindowedSizeSurface
{
    Vector2D<int> Size { get; set; }

    bool IsMaximized { get; }

    void Restore();
}

internal sealed class SilkWindowSizeSurface(IWindow window) : IWindowedSizeSurface
{
    private readonly IWindow _window = window
        ?? throw new ArgumentNullException(nameof(window));

    public Vector2D<int> Size
    {
        get => _window.Size;
        set => _window.Size = value;
    }

    public bool IsMaximized => _window.WindowState == WindowState.Maximized;

    public void Restore() => _window.WindowState = WindowState.Normal;
}

internal sealed class SilkRuntimeDisplayWindowTarget : IRuntimeDisplayWindowTarget
{
    private readonly IWindowedSizeSurface _window;
    private readonly IDisplayModeSwitcher _modeSwitcher;
    private readonly Func<string, bool> _isOfferedMode;

    public SilkRuntimeDisplayWindowTarget(IWindow window)
        : this(
            new SilkWindowSizeSurface(window),
            new GlfwDisplayModeSwitcher(window),
            spec => (Rendering.DisplayModeCatalog.Resolutions
                     ?? DisplaySettings.AvailableResolutions).Contains(spec))
    {
    }

    internal SilkRuntimeDisplayWindowTarget(
        IWindowedSizeSurface window,
        IDisplayModeSwitcher modeSwitcher,
        Func<string, bool> isOfferedMode)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _modeSwitcher = modeSwitcher
            ?? throw new ArgumentNullException(nameof(modeSwitcher));
        _isOfferedMode = isOfferedMode
            ?? throw new ArgumentNullException(nameof(isOfferedMode));
    }

    public RuntimeDisplayApplyResult Apply(DisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);
        bool haveResolution =
            TryParseResolution(display.Resolution, out int width, out int height);

        if (display.Fullscreen)
        {
            if (!haveResolution)
            {
                Console.WriteLine(
                    $"display: fullscreen refused — unparseable resolution '{display.Resolution}'");
                return CurrentResult();
            }
            if (_modeSwitcher.CurrentFullscreenMode is (int curW, int curH)
                && curW == width && curH == height)
                return CurrentResult();
            if (!_isOfferedMode.Invoke($"{width}x{height}"))
            {
                Console.WriteLine(
                    $"display: fullscreen {width}x{height} refused — not an offered mode");
                return CurrentResult();
            }
            if (!_modeSwitcher.TryEnterFullscreen(width, height, out string? error))
                Console.WriteLine(
                    $"display: fullscreen {width}x{height} failed ({error}) — window state unchanged");
            return CurrentResult();
        }

        if (_modeSwitcher.IsFullscreen)
        {
            if (!haveResolution)
            {
                width = _window.Size.X;
                height = _window.Size.Y;
            }
            if (!_modeSwitcher.TryLeaveFullscreen(width, height, out string? error))
                Console.WriteLine(
                    $"display: leaving fullscreen failed ({error})");
            return CurrentResult();
        }

        if (haveResolution && (_window.Size.X != width || _window.Size.Y != height))
        {
            if (_window.IsMaximized)
                _window.Restore();
            Console.WriteLine(
                $"display: resolution pick {width}x{height} " +
                $"(window was {_window.Size.X}x{_window.Size.Y})");
            _window.Size = new Vector2D<int>(width, height);
        }
        return CurrentResult();
    }

    private RuntimeDisplayApplyResult CurrentResult() =>
        new(_modeSwitcher.IsFullscreen);

    internal static bool TryParseResolution(
        string spec,
        out int width,
        out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(spec))
            return false;
        string[] parts = spec.Split('x', 2);
        return parts.Length == 2
            && int.TryParse(
                parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(
                parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            && width > 0
            && height > 0;
    }
}

internal sealed class RuntimeSettingsStartupTargets : IRuntimeSettingsStartupTarget
{
    private readonly IRuntimeDisplayWindowTarget _displayWindow;
    private readonly DisplayFramePacingController _pacing;
    private readonly CameraController _cameras;
    private readonly OpenAlAudioEngine? _audio;

    public RuntimeSettingsStartupTargets(
        IRuntimeDisplayWindowTarget displayWindow,
        DisplayFramePacingController pacing,
        CameraController cameras,
        OpenAlAudioEngine? audio)
    {
        _displayWindow = displayWindow
            ?? throw new ArgumentNullException(nameof(displayWindow));
        _pacing = pacing ?? throw new ArgumentNullException(nameof(pacing));
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _audio = audio;
    }

    public RuntimeDisplayApplyResult ApplyDisplay(DisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);
        _pacing.RefreshActiveMonitor();
        _pacing.ApplyPreference(display.VSync);
        RuntimeDisplayApplyResult result = _displayWindow.Apply(display);
        ApplyFieldOfView(_cameras, display.FieldOfView);
        return result;
    }

    public void ApplyAudio(AudioSettings audio) => ApplyAudio(_audio, audio);

    internal static void ApplyFieldOfView(
        CameraController cameras,
        float degrees)
    {
        cameras.SetGameFov(degrees * (MathF.PI / 180f));
    }

    internal static void ApplyAudio(
        OpenAlAudioEngine? engine,
        AudioSettings audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (engine is not { IsAvailable: true })
            return;
        engine.MasterVolume = audio.Master;
        (float sfx, float ambient) = ComputeEffectiveCategoryVolumes(audio);
        engine.SfxVolume = sfx;
        engine.AmbientVolume = ambient;
    }

    internal static (float Sfx, float Ambient) ComputeEffectiveCategoryVolumes(AudioSettings audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        float sfx = audio.SfxEnabled ? audio.Sfx : 0f;
        float ambient = audio.AmbientEnabled ? audio.Ambient : 0f;
        return (sfx, ambient);
    }
}

internal sealed class RuntimeQualityApplicationTarget
    : IRuntimeQualityApplicationTarget
{
    private readonly WbDrawDispatcher? _dispatcher;
    private readonly TerrainAtlas? _terrainAtlas;
    private readonly StreamingController _streaming;
    private readonly WorldRenderRangeState _renderRange;

    public RuntimeQualityApplicationTarget(
        WbDrawDispatcher? dispatcher,
        TerrainAtlas? terrainAtlas,
        StreamingController streaming,
        WorldRenderRangeState renderRange)
    {
        _dispatcher = dispatcher;
        _terrainAtlas = terrainAtlas;
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _renderRange = renderRange ?? throw new ArgumentNullException(nameof(renderRange));
    }

    public void SetAlphaToCoverage(bool enabled)
    {
        if (_dispatcher is not null)
            _dispatcher.AlphaToCoverage = enabled;
    }

    public void SetAnisotropic(int level) => _terrainAtlas?.SetAnisotropic(level);

    public void PublishRenderRange(int nearRadius, int farRadius)
    {
        _renderRange.NearRadius = nearRadius;
        _renderRange.FarRadius = farRadius;
    }

    public void ReconfigureStreamingRadii(int nearRadius, int farRadius) =>
        _streaming.ReconfigureRadii(nearRadius, farRadius);

    public void SetCompletionBudget(int maxCompletionsPerFrame) =>
        _streaming.MaxCompletionsPerFrame = maxCompletionsPerFrame;
}

internal sealed class RuntimeUiLockTarget(UiRoot root) : IRuntimeUiLockTarget
{
    private readonly UiRoot _root = root ?? throw new ArgumentNullException(nameof(root));

    public void Apply(bool locked) => _root.UiLocked = locked;
}

internal sealed class NullRuntimeUiLockTarget : IRuntimeUiLockTarget
{
    public static NullRuntimeUiLockTarget Instance { get; } = new();

    private NullRuntimeUiLockTarget()
    {
    }

    public void Apply(bool locked)
    {
    }
}

internal sealed class RuntimeChatOpacityTarget(RetailWindowOpacityController controller)
    : IRuntimeChatOpacityTarget
{
    private readonly RetailWindowOpacityController _controller =
        controller ?? throw new ArgumentNullException(nameof(controller));

    public void Apply(float defaultOpacity, float activeOpacity) =>
        _controller.SetOpacity(defaultOpacity, activeOpacity);
}

internal sealed class NullRuntimeChatOpacityTarget : IRuntimeChatOpacityTarget
{
    public static NullRuntimeChatOpacityTarget Instance { get; } = new();

    private NullRuntimeChatOpacityTarget()
    {
    }

    public void Apply(float defaultOpacity, float activeOpacity)
    {
    }
}

internal sealed class RuntimeSettingsTargets : IRuntimeSettingsTargets
{
    private readonly IRuntimeDisplayWindowTarget _displayWindow;
    private readonly IRuntimeQualityApplicationTarget _quality;
    private readonly IRuntimeUiLockTarget _uiLock;
    private readonly IRuntimeChatOpacityTarget _chatOpacity;
    private readonly ICommandBus _commands;
    private readonly Action<string> _log;
    private readonly OpenAlAudioEngine? _audio;
    private readonly CameraController? _cameras;

    public RuntimeSettingsTargets(
        IRuntimeDisplayWindowTarget displayWindow,
        WbDrawDispatcher? dispatcher,
        TerrainAtlas? terrainAtlas,
        StreamingController streaming,
        WorldRenderRangeState renderRange,
        UiRoot? uiRoot,
        ICommandBus commands,
        RetailWindowOpacityController? chatOpacity = null,
        Action<string>? log = null,
        OpenAlAudioEngine? audio = null,
        CameraController? cameras = null)
        : this(
            displayWindow,
            new RuntimeQualityApplicationTarget(
                dispatcher,
                terrainAtlas,
                streaming,
                renderRange),
            uiRoot is null
                ? NullRuntimeUiLockTarget.Instance
                : new RuntimeUiLockTarget(uiRoot),
            commands,
            log,
            chatOpacity is null
                ? NullRuntimeChatOpacityTarget.Instance
                : new RuntimeChatOpacityTarget(chatOpacity),
            audio,
            cameras)
    {
    }

    internal RuntimeSettingsTargets(
        IRuntimeDisplayWindowTarget displayWindow,
        IRuntimeQualityApplicationTarget quality,
        IRuntimeUiLockTarget uiLock,
        ICommandBus commands,
        Action<string>? log = null,
        IRuntimeChatOpacityTarget? chatOpacity = null,
        OpenAlAudioEngine? audio = null,
        CameraController? cameras = null)
    {
        _displayWindow = displayWindow
            ?? throw new ArgumentNullException(nameof(displayWindow));
        _quality = quality ?? throw new ArgumentNullException(nameof(quality));
        _uiLock = uiLock ?? throw new ArgumentNullException(nameof(uiLock));
        _chatOpacity = chatOpacity ?? NullRuntimeChatOpacityTarget.Instance;
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _log = log ?? Console.WriteLine;
        _audio = audio;
        _cameras = cameras;
    }

    public RuntimeDisplayApplyResult ApplyDisplayWindowState(DisplaySettings display)
    {
        RuntimeDisplayApplyResult result = _displayWindow.Apply(display);
        if (_cameras is not null)
            RuntimeSettingsStartupTargets.ApplyFieldOfView(_cameras, display.FieldOfView);
        return result;
    }

    public void ApplyAudio(AudioSettings audio) =>
        RuntimeSettingsStartupTargets.ApplyAudio(_audio, audio);

    public void ApplyQuality(QualitySettings quality)
    {
        _quality.SetAlphaToCoverage(quality.AlphaToCoverage);
        _quality.SetAnisotropic(quality.AnisotropicLevel);
        _quality.PublishRenderRange(quality.NearRadius, quality.FarRadius);
        _quality.ReconfigureStreamingRadii(quality.NearRadius, quality.FarRadius);
        _quality.SetCompletionBudget(quality.MaxCompletionsPerFrame);
        _log(
            $"[QUALITY] Streaming reconciled: nearRadius={quality.NearRadius}, " +
            $"farRadius={quality.FarRadius}, " +
            $"maxCompletions={quality.MaxCompletionsPerFrame}");
    }

    public void ApplyUiLock(bool locked) => _uiLock.Apply(locked);

    public void SetSingleCharacterOption(uint optionId, bool value) =>
        _commands.Publish(new SetSingleCharacterOptionRuntimeCmd(optionId, value));

    public void SetChatOpacity(float defaultOpacity, float activeOpacity) =>
        _chatOpacity.Apply(defaultOpacity, activeOpacity);
}
