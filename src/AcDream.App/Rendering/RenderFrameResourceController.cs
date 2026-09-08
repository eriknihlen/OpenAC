using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal readonly record struct RenderFrameFoundation(
    bool PortalViewportVisible,
    SkyKeyframe Sky,
    AtmosphereSnapshot Atmosphere)
{
    public bool EnvironOverrideActive =>
        Atmosphere.Override != EnvironOverride.None;
}

internal interface IRenderFrameBeginResources
{
    void Begin(int gpuSlot);
}

internal interface IRenderFrameSlotSource
{
    int CurrentSlot { get; }
}

internal interface IRenderFrameClearPhase
{
    RenderFrameFoundation Clear();
}

internal interface IRenderFrameFoundationSource
{
    RenderFrameFoundation Foundation { get; }
}

internal interface IRenderFrameLivePreparation
{
    void Prepare(int gpuSlot);
}

internal sealed class RenderFrameResourceController :
    IRenderFrameResourcePhase,
    IRenderFrameFoundationSource
{
    private readonly IRenderFrameSlotSource _frameSlots;
    private readonly IRenderFrameBeginResources _resources;
    private readonly IRenderFrameClearPhase _clear;
    private readonly IRenderFrameLivePreparation _live;

    public RenderFrameResourceController(
        IRenderFrameSlotSource frameSlots,
        IRenderFrameBeginResources resources,
        IRenderFrameClearPhase clear,
        IRenderFrameLivePreparation live)
    {
        _frameSlots = frameSlots
            ?? throw new ArgumentNullException(nameof(frameSlots));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        _live = live ?? throw new ArgumentNullException(nameof(live));
    }

    public RenderFrameFoundation Foundation { get; private set; }

    public void Prepare(RenderFrameInput input)
    {
        int gpuSlot = _frameSlots.CurrentSlot;
        _resources.Begin(gpuSlot);
        Foundation = _clear.Clear();
        _live.Prepare(gpuSlot);
    }
}

internal sealed class RuntimeRenderFrameBeginResources : IRenderFrameBeginResources
{
    private readonly TextureCache? _textures;
    private readonly WbDrawDispatcher? _dispatcher;
    private readonly EnvCellRenderer? _environmentCells;
    private readonly PortalDepthMaskRenderer? _portalDepth;
    private readonly ClipFrame? _clip;
    private readonly TerrainModernRenderer? _terrain;
    private readonly SceneLightingUboBinding? _lighting;
    public RuntimeRenderFrameBeginResources(
        TextureCache? textures,
        WbDrawDispatcher? dispatcher,
        EnvCellRenderer? environmentCells,
        PortalDepthMaskRenderer? portalDepth,
        ClipFrame? clip,
        TerrainModernRenderer? terrain,
        SceneLightingUboBinding? lighting)
    {
        _textures = textures;
        _dispatcher = dispatcher;
        _environmentCells = environmentCells;
        _portalDepth = portalDepth;
        _clip = clip;
        _terrain = terrain;
        _lighting = lighting;
    }

    public void Begin(int gpuSlot)
    {
        _textures?.BeginCompositeTextureFrame();
        _textures?.TickCompositeTextureCache();
        _dispatcher?.BeginFrame(gpuSlot);
        _environmentCells?.BeginFrame(gpuSlot);
        _portalDepth?.BeginFrame(gpuSlot);
        _clip?.BeginFrame(gpuSlot);
        _terrain?.BeginFrame(gpuSlot);
        _lighting?.BeginFrame(gpuSlot);
    }
}

internal interface IRenderFramePortalStateSource
{
    bool IsPortalViewportVisible { get; }

    uint ActiveDestinationCell { get; }
}

internal sealed class LocalPlayerTeleportRenderStateSource
    : IRenderFramePortalStateSource
{
    private readonly LocalPlayerTeleportController _teleport;
    private readonly IRenderLoginStateSource _login;

    public LocalPlayerTeleportRenderStateSource(
        LocalPlayerTeleportController teleport,
        IRenderLoginStateSource login)
    {
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _login = login ?? throw new ArgumentNullException(nameof(login));
    }

    public bool IsPortalViewportVisible =>
        _teleport.IsPortalViewportVisible || _login.IsWaitingForLogin;

    public uint ActiveDestinationCell => _teleport.ActiveDestinationCell;
}


internal interface IRenderLoginStateSource
{
    bool IsWaitingForLogin { get; }
}

internal sealed class RenderLoginStateSource : IRenderLoginStateSource
{
    private readonly bool _liveMode;
    private readonly ILocalPlayerModeSource _mode;

    public RenderLoginStateSource(bool liveMode, ILocalPlayerModeSource mode)
    {
        _liveMode = liveMode;
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
    }

    public bool IsWaitingForLogin => _liveMode && !_mode.ChaseModeEverEntered;
}

internal interface ILoginRevealCellSource
{
    bool TryGet(out uint cellId);
}

internal sealed class LiveLoginRevealCellSource : ILoginRevealCellSource
{
    private readonly LiveEntityRuntime _entities;
    private readonly ILocalPlayerIdentitySource _identity;

    public LiveLoginRevealCellSource(
        LiveEntityRuntime entities,
        ILocalPlayerIdentitySource identity)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public bool TryGet(out uint cellId)
    {
        cellId = 0;
        if (!_entities.TryGetSnapshot(_identity.ServerGuid, out var playerSpawn)
            || playerSpawn.Position is not { } position)
        {
            return false;
        }

        cellId = position.LandblockId;
        return cellId != 0;
    }
}

/// <summary>Render-thread mesh publication, reveal evaluation, and VFX begin.</summary>
internal sealed class RuntimeRenderFrameLivePreparation
    : IRenderFrameLivePreparation
{
    private readonly TextureCache? _textures;
    private readonly WbMeshAdapter? _meshes;
    private readonly WorldRevealCoordinator? _worldReveal;
    private readonly IRenderFramePortalStateSource _portal;
    private readonly IRenderLoginStateSource _login;
    private readonly ILoginRevealCellSource _loginCell;
    private readonly ParticleRenderer? _particles;
    private readonly FrameProfiler _profiler;
    private readonly bool _diagnosticsEnabled;
    private readonly RollingTimingSampleWindow _uploadTiming = new(256);

    public RuntimeRenderFrameLivePreparation(
        TextureCache? textures,
        WbMeshAdapter? meshes,
        WorldRevealCoordinator? worldReveal,
        IRenderFramePortalStateSource portal,
        IRenderLoginStateSource login,
        ILoginRevealCellSource loginCell,
        ParticleRenderer? particles,
        FrameProfiler profiler,
        bool diagnosticsEnabled)
    {
        _textures = textures;
        _meshes = meshes;
        _worldReveal = worldReveal;
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _login = login ?? throw new ArgumentNullException(nameof(login));
        _loginCell = loginCell ?? throw new ArgumentNullException(nameof(loginCell));
        _particles = particles;
        _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
        _diagnosticsEnabled = diagnosticsEnabled;
    }

    public RollingTimingPercentiles UploadTiming => _uploadTiming.Snapshot();

    public void Prepare(int gpuSlot)
    {
        _textures?.TickSurfaceHistogramDumpIfEnabled();
        long start = _diagnosticsEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        using (var uploadStage = _profiler.BeginStage(FrameStage.Upload))
        {
            _meshes?.Tick();
            uint revealCell = _portal.ActiveDestinationCell;
            if (revealCell == 0
                && _login.IsWaitingForLogin
                && _loginCell.TryGet(out uint loginCell))
            {
                revealCell = loginCell;
            }

            if (revealCell != 0)
                _worldReveal?.PrepareAndEvaluate(revealCell);
            _particles?.BeginFrame(gpuSlot);
        }

        if (_diagnosticsEnabled)
        {
            _uploadTiming.PushStopwatchTicks(
                System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }
}

internal sealed class RenderWeatherFrameController : IRenderWeatherFramePhase
{
    private readonly WorldTimeService _worldTime;
    private readonly WeatherSystem _weather;
    private double _elapsedSeconds;

    public RenderWeatherFrameController(
        WorldTimeService worldTime,
        WeatherSystem weather)
    {
        _worldTime = worldTime ?? throw new ArgumentNullException(nameof(worldTime));
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));
    }

    internal double ElapsedSeconds => _elapsedSeconds;

    public void Tick(double deltaSeconds)
    {
        DerethDateTime.Calendar calendar = _worldTime.CurrentCalendar;
        int dayIndex = calendar.Year
            * (DerethDateTime.DaysInAMonth * DerethDateTime.MonthsInAYear)
            + (int)calendar.Month * DerethDateTime.DaysInAMonth
            + (calendar.Day - 1);
        _weather.Tick(
            nowSeconds: _elapsedSeconds,
            dayIndex,
            dtSeconds: (float)deltaSeconds);
        _elapsedSeconds += deltaSeconds;
    }
}
