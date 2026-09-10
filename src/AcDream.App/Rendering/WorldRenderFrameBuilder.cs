using System.Numerics;
using AcDream.App.Audio;
using AcDream.App.Input;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Settings;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Audio;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal readonly record struct WorldCameraFrame(
    ICamera Camera,
    Matrix4x4 Projection,
    Matrix4x4 ViewProjection,
    FrustumPlanes Frustum,
    Matrix4x4 InverseView,
    Vector3 Position)
{
    public bool IsOverheadView { get; init; }
}

internal static class WorldCameraViewPolicy
{
    internal static bool IsOverheadView(ICamera activeCamera) =>
        activeCamera is RetailChaseCamera { IsMapMode: true }
            or ChaseCamera { IsMapMode: true };

    internal static AtmosphereSnapshot Apply(
        in AtmosphereSnapshot atmosphere,
        bool distanceFogDisabled) =>
        distanceFogDisabled
            ? atmosphere with { FogMode = FogMode.Off }
            : atmosphere;
}

internal readonly record struct WorldRootFrame(
    LoadedCell? PlayerRoot,
    bool PlayerSeenOutside,
    uint ViewerCellId,
    Vector3 ViewerEyePosition,
    Vector3 PlayerViewPosition,
    LoadedCell? ViewerRoot,
    bool CameraInsideCell,
    bool RootSeenOutside,
    bool PlayerInsideCell,
    uint? PlayerLandblockId,
    int RenderCenterLandblockX,
    int RenderCenterLandblockY,
    uint PlayerCellId,
    bool PlayerIndoorGate)
{
    public bool RenderSky => ViewerRoot is null || RootSeenOutside;

    public bool SkyEffectsActive => RenderSky && !PlayerInsideCell;

    public bool CameraInsideEnclosedCell => CameraInsideCell && !RootSeenOutside;

    /// <summary>Environment gate shared by directional shadows and other
    /// outdoor-only render-pack effects.</summary>
    public bool PlayerOrCameraInsideEnclosedCell =>
        PlayerInsideCell || CameraInsideEnclosedCell;

    public bool IsAtmosphericallyOutdoor =>
        RenderSky && !CameraInsideEnclosedCell;
}

/// <summary>Borrowed building scratch, valid only until the next build.</summary>
internal readonly record struct WorldBuildingFrame(
    LoadedCell? OutdoorNode,
    IReadOnlyList<LoadedCell> NearbyBuildingCells);

internal readonly record struct WorldRenderFrame(
    WorldCameraFrame Camera,
    WorldRootFrame Roots,
    WorldBuildingFrame Buildings,
    HashSet<uint> AnimatedEntityIds)
{
    public LoadedCell? ClipRoot => Roots.ViewerRoot ?? Buildings.OutdoorNode;

    public ResidentStreamingWindowFact ResidentStreamingWindow { get; init; }

    public AuthoredCelestialShadowSource CelestialShadowSource { get; init; }

    public RetailLandscapeVisibilityFrame PriorLandscapeVisibility { get; init; }

    public IDirectionalShadowCellMembership? DirectionalShadowCellMembership
        { get; init; }
}

internal interface IDirectionalShadowCellMembership
{
    bool TryGetRetailCellArray(uint entityId, out IReadOnlyList<uint> cells);
}

internal sealed class EmptyDirectionalShadowCellMembership
    : IDirectionalShadowCellMembership
{
    internal static EmptyDirectionalShadowCellMembership Instance { get; } = new();

    public bool TryGetRetailCellArray(
        uint entityId,
        out IReadOnlyList<uint> cells)
    {
        _ = entityId;
        cells = Array.Empty<uint>();
        return false;
    }
}

internal sealed class RuntimeDirectionalShadowCellMembership(
    ShadowObjectRegistry source) : IDirectionalShadowCellMembership
{
    private readonly ShadowObjectRegistry _source = source
        ?? throw new ArgumentNullException(nameof(source));

    public bool TryGetRetailCellArray(
        uint entityId,
        out IReadOnlyList<uint> cells) =>
        _source.TryGetRetailCellArray(entityId, out cells);
}

internal interface IWorldRenderFrameBuilder
{
    WorldRenderFrame Build(
        in RenderFrameFoundation foundation,
        bool waitingForLogin,
        DayGroupData? activeDayGroup);

}

internal interface IWorldFrameCameraSource
{
    WorldCameraFrame Resolve();
}

internal interface IWorldFrameRootSource
{
    WorldRootFrame Resolve(in WorldCameraFrame camera);
}

internal interface IWorldFrameVisibilityPreparation
{
    RetailLandscapeVisibilityFrame CaptureCompletedLandscapeVisibility();

    void Begin(in WorldCameraFrame camera, bool waitingForLogin);

    void PublishViewProjection(in WorldCameraFrame camera);
}

internal interface IWorldFrameSettingsPreview
{
    void Apply(in WorldCameraFrame camera);
}

internal interface IWorldFrameEnvironmentPreparation
{
    void Prepare(
        in WorldCameraFrame camera,
        in WorldRootFrame roots,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup);

}

internal interface ISkyPesActivationGate
{
    void Tick();
}

internal sealed class RuntimeSkyPesActivationGate
    : ISkyPesActivationGate
{
    private readonly SkyPesFrameController _skyPes;
    private readonly IWorldFrameCameraSource _camera;
    private readonly IWorldFrameRootSource _roots;

    public RuntimeSkyPesActivationGate(
        SkyPesFrameController skyPes,
        IWorldFrameCameraSource camera,
        IWorldFrameRootSource roots)
    {
        _skyPes = skyPes ?? throw new ArgumentNullException(nameof(skyPes));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    public void Tick()
    {
        WorldCameraFrame camera = _camera.Resolve();
        WorldRootFrame roots = _roots.Resolve(in camera);
        _skyPes.SetActive(roots.SkyEffectsActive);
    }
}

internal interface IWorldFrameAnimatedEntitySource
{
    HashSet<uint> Capture();
}

internal interface IWorldFrameBuildingSource
{
    WorldBuildingFrame Gather(
        LoadedCell? viewerRoot,
        uint viewerCellId,
        in FrustumPlanes frustum);
}

internal sealed class WorldRenderFrameBuilder : IWorldRenderFrameBuilder
{
    private readonly IWorldFrameCameraSource _camera;
    private readonly IWorldFrameVisibilityPreparation _visibility;
    private readonly IWorldFrameSettingsPreview _settings;
    private readonly IWorldFrameRootSource _roots;
    private readonly IWorldFrameEnvironmentPreparation _environment;
    private readonly IWorldFrameAnimatedEntitySource _animated;
    private readonly IWorldFrameBuildingSource _buildings;
    private readonly IDirectionalShadowCellMembership _directionalShadowCells;

    public WorldRenderFrameBuilder(
        IWorldFrameCameraSource camera,
        IWorldFrameVisibilityPreparation visibility,
        IWorldFrameSettingsPreview settings,
        IWorldFrameRootSource roots,
        IWorldFrameEnvironmentPreparation environment,
        IWorldFrameAnimatedEntitySource animated,
        IWorldFrameBuildingSource buildings,
        IDirectionalShadowCellMembership directionalShadowCells)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _animated = animated ?? throw new ArgumentNullException(nameof(animated));
        _buildings = buildings ?? throw new ArgumentNullException(nameof(buildings));
        _directionalShadowCells = directionalShadowCells
            ?? throw new ArgumentNullException(nameof(directionalShadowCells));
    }

    public WorldRenderFrame Build(
        in RenderFrameFoundation foundation,
        bool waitingForLogin,
        DayGroupData? activeDayGroup)
    {
        RetailLandscapeVisibilityFrame priorLandscapeVisibility =
            _visibility.CaptureCompletedLandscapeVisibility();
        if (waitingForLogin)
            priorLandscapeVisibility = RetailLandscapeVisibilityFrame.None;
        WorldCameraFrame camera = _camera.Resolve();
        _visibility.Begin(in camera, waitingForLogin);
        _settings.Apply(in camera);
        WorldRootFrame roots = _roots.Resolve(in camera);
        _environment.Prepare(in camera, in roots, in foundation, activeDayGroup);
        _visibility.PublishViewProjection(in camera);
        HashSet<uint> animated = _animated.Capture();
        FrustumPlanes frustum = camera.Frustum;
        WorldBuildingFrame buildings = _buildings.Gather(
            roots.ViewerRoot,
            roots.ViewerCellId,
            in frustum);
        return new WorldRenderFrame(camera, roots, buildings, animated)
        {
            PriorLandscapeVisibility = priorLandscapeVisibility,
            DirectionalShadowCellMembership = _directionalShadowCells,
        };
    }

}

internal sealed class RuntimeWorldFrameCameraSource : IWorldFrameCameraSource
{
    private readonly CameraController _cameras;
    private readonly Func<ICamera, ICamera> _applyViewPlane;

    public RuntimeWorldFrameCameraSource(
        CameraController cameras,
        Func<ICamera, ICamera> applyViewPlane)
    {
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _applyViewPlane = applyViewPlane
            ?? throw new ArgumentNullException(nameof(applyViewPlane));
    }

    public WorldCameraFrame Resolve()
    {
        ICamera activeCamera = _cameras.Active;
        bool overheadView = WorldCameraViewPolicy.IsOverheadView(activeCamera);
        ICamera camera = _applyViewPlane(activeCamera);
        Matrix4x4 projection = camera.Projection;
        Matrix4x4 viewProjection = camera.View * projection;
        FrustumPlanes frustum = FrustumPlanes.FromViewProjection(viewProjection);
        Matrix4x4.Invert(camera.View, out Matrix4x4 inverseView);
        var position = new Vector3(inverseView.M41, inverseView.M42, inverseView.M43);
        return new WorldCameraFrame(
            camera,
            projection,
            viewProjection,
            frustum,
            inverseView,
            position)
        {
            IsOverheadView = overheadView,
        };
    }
}

internal sealed class RuntimeWorldFrameRootSource : IWorldFrameRootSource
{
    private const float LandblockSize = 192f;

    private readonly PhysicsEngine _physics;
    private readonly CellVisibility _cells;
    private readonly ILocalPlayerModeSource _mode;
    private readonly IChaseCameraSource _chase;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly LiveWorldOriginState _origin;

    public RuntimeWorldFrameRootSource(
        PhysicsEngine physics,
        CellVisibility cells,
        ILocalPlayerModeSource mode,
        IChaseCameraSource chase,
        IRuntimeLocalPlayerControllerSource player,
        LiveWorldOriginState origin)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
    }

    public WorldRootFrame Resolve(in WorldCameraFrame camera)
    {
        LoadedCell? playerRoot = null;
        if (_physics.DataCache?.CellGraph.CurrCell is AcDream.Core.World.Cells.EnvCell playerCell
            && _cells.TryGetCell(playerCell.Id, out LoadedCell? registeredPlayer))
        {
            playerRoot = registeredPlayer;
        }

        bool playerSeenOutside = playerRoot?.SeenOutside ?? true;
        uint viewerCellId = _mode.IsPlayerMode
            && _chase.Retail is { } retailChase
            && CameraDiagnostics.UseRetailChaseCamera
                ? retailChase.ViewerCellId
                : playerRoot?.CellId ?? 0u;
        LoadedCell? viewerRoot = null;
        if (viewerCellId != 0u
            && _cells.TryGetCell(viewerCellId, out LoadedCell? registeredViewer))
        {
            viewerRoot = registeredViewer;
        }

        var player = _player.Controller;
        Vector3 playerViewPosition = player?.RenderPosition
            ?? player?.Position
            ?? camera.Position;
        bool cameraInsideCell = viewerRoot is not null;
        bool rootSeenOutside = viewerRoot?.SeenOutside ?? true;
        bool playerInsideCell = playerRoot is not null && !playerSeenOutside;

        uint? playerLandblockId = null;
        if (_mode.IsPlayerMode && player is not null)
        {
            int playerX = _origin.CenterX + (int)Math.Floor(player.Position.X / LandblockSize);
            int playerY = _origin.CenterY + (int)Math.Floor(player.Position.Y / LandblockSize);
            playerLandblockId = (uint)((playerX << 24) | (playerY << 16) | 0xFFFF);
        }

        int renderCenterX = _origin.CenterX
            + (int)Math.Floor(camera.Position.X / LandblockSize);
        int renderCenterY = _origin.CenterY
            + (int)Math.Floor(camera.Position.Y / LandblockSize);
        uint playerCellId = _physics.DataCache?.CellGraph.CurrCell?.Id ?? 0u;
        bool playerIndoorGate = RenderingDiagnostics.ShouldRenderIndoor(
            playerCellId,
            playerRoot is not null);

        return new WorldRootFrame(
            playerRoot,
            playerSeenOutside,
            viewerCellId,
            camera.Position,
            playerViewPosition,
            viewerRoot,
            cameraInsideCell,
            rootSeenOutside,
            playerInsideCell,
            playerLandblockId,
            renderCenterX,
            renderCenterY,
            playerCellId,
            playerIndoorGate);
    }
}

internal sealed class RuntimeWorldFrameVisibilityPreparation
    : IWorldFrameVisibilityPreparation
{
    private readonly RetailSelectionScene? _selection;
    private readonly ParticleVisibilityController _particles;
    private readonly WorldRevealCoordinator? _reveal;
    private readonly WbFrustum? _environmentFrustum;

    public RuntimeWorldFrameVisibilityPreparation(
        RetailSelectionScene? selection,
        ParticleVisibilityController particles,
        TerrainModernRenderer? terrain,
        WorldRevealCoordinator? reveal,
        WbFrustum? environmentFrustum)
    {
        _selection = selection;
        _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        _ = terrain;
        _reveal = reveal;
        _environmentFrustum = environmentFrustum;
    }

    public RetailLandscapeVisibilityFrame CaptureCompletedLandscapeVisibility() =>
        _particles.CaptureCompletedLandscapeVisibility();

    public void Begin(in WorldCameraFrame camera, bool waitingForLogin)
    {
        _selection?.SetViewFrustum(camera.Frustum);
        _particles.BeginFrame(camera.Position);
        if (waitingForLogin)
            return;

        _particles.UseWorldView();
        _reveal?.ObserveWorldViewportVisible();
    }

    public void PublishViewProjection(in WorldCameraFrame camera) =>
        _environmentFrustum?.Update(camera.ViewProjection);
}

internal sealed class RuntimeWorldFrameSettingsPreview : IWorldFrameSettingsPreview
{
    private readonly IRuntimeSettingsPreviewSource _settings;
    private readonly OpenAlAudioEngine? _audio;
    private readonly CameraController _cameras;
    private readonly DisplayFramePacingController _pacing;

    public RuntimeWorldFrameSettingsPreview(
        IRuntimeSettingsPreviewSource settings,
        OpenAlAudioEngine? audio,
        CameraController cameras,
        DisplayFramePacingController pacing)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audio = audio;
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _pacing = pacing ?? throw new ArgumentNullException(nameof(pacing));
    }

    public void Apply(in WorldCameraFrame camera)
    {
        if (_settings.HasDraftPreview)
        {
            RuntimeSettingsStartupTargets.ApplyAudio(
                _audio,
                _settings.AudioPreview);
            var display = _settings.DisplayPreview;
            RuntimeSettingsStartupTargets.ApplyFieldOfView(
                _cameras,
                display.FieldOfView);
            _pacing.ApplyPreference(display.VSync);
        }

        if (_audio is not { IsAvailable: true })
            return;

        Matrix4x4 inverse = camera.InverseView;
        var forward = new Vector3(-inverse.M31, -inverse.M32, -inverse.M33);
        Vector3 position = camera.Position;
        _audio.SetListener(
            position.X, position.Y, position.Z,
            RetailSoundMixer.CompassHeadingDegrees(Vector3.Zero, forward));
    }
}

internal interface IWorldRenderRangeSource
{
    int NearRadius { get; }

    int FarRadius { get; }
}

internal sealed class WorldRenderRangeState : IWorldRenderRangeSource
{
    public WorldRenderRangeState(int nearRadius, int farRadius)
    {
        NearRadius = nearRadius;
        FarRadius = farRadius;
    }

    public int NearRadius { get; set; }

    public int FarRadius { get; set; }
}

internal sealed class RuntimeWorldFrameEnvironmentPreparation
    : IWorldFrameEnvironmentPreparation
{
    private const float LandblockSize = 192f;

    private readonly RuntimeOptions _options;
    private readonly WorldTimeService _worldTime;
    private readonly LightManager _lighting;
    private readonly WbDrawDispatcher? _dispatcher;
    private readonly EnvCellRenderer? _environmentCells;
    private readonly SceneLightingUboBinding? _lightingUbo;
    private readonly IWorldRenderRangeSource _ranges;
    private readonly SkyPesFrameController? _skyPes;
    private readonly Func<bool> _persistentDaylight;

    public RuntimeWorldFrameEnvironmentPreparation(
        RuntimeOptions options,
        WorldTimeService worldTime,
        LightManager lighting,
        WbDrawDispatcher? dispatcher,
        EnvCellRenderer? environmentCells,
        SceneLightingUboBinding? lightingUbo,
        IWorldRenderRangeSource ranges,
        SkyPesFrameController? skyPes,
        Func<bool>? persistentDaylight = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _worldTime = worldTime ?? throw new ArgumentNullException(nameof(worldTime));
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        _dispatcher = dispatcher;
        _environmentCells = environmentCells;
        _lightingUbo = lightingUbo;
        _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        _skyPes = skyPes;
        _persistentDaylight = persistentDaylight ?? (static () => false);
    }

    public void Prepare(
        in WorldCameraFrame camera,
        in WorldRootFrame roots,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup)
    {
        _skyPes?.Update(
            (float)_worldTime.DayFraction,
            activeDayGroup,
            camera.Position,
            roots.SkyEffectsActive);

        SkyKeyframe landscapeLighting = _persistentDaylight()
            ? _worldTime.SkyAtDayFraction(0.5f)
            : foundation.Sky;
        UpdateSunFromSky(landscapeLighting, roots.PlayerInsideCell);
        _lighting.UpdateViewerLight(roots.PlayerViewPosition);
        _lighting.Tick(camera.Position);
        _lighting.BuildPointLightSnapshot(roots.PlayerViewPosition);
        _dispatcher?.SetSceneLights(_lighting.PointSnapshot);
        _environmentCells?.SetPointSnapshot(_lighting.PointSnapshot);

        AtmosphereSnapshot atmosphere = WorldCameraViewPolicy.Apply(
            foundation.Atmosphere,
            camera.IsOverheadView);
        SceneLightingUbo ubo = SceneLightingUbo.Build(
            _lighting,
            in atmosphere,
            camera.Position,
            (float)_worldTime.DayFraction);
        _lightingUbo?.Upload(ubo);
    }

    private void UpdateSunFromSky(SkyKeyframe keyframe, bool playerInsideCell)
    {
        Vector3 sunToWorld = -SkyStateProvider.SunDirectionFromKeyframe(keyframe);
        if (playerInsideCell)
        {
            _lighting.Sun = new LightSource
            {
                Kind = LightKind.Directional,
                WorldForward = sunToWorld,
                ColorLinear = Vector3.Zero,
                Intensity = 0f,
                Range = 1f,
            };
            _lighting.CurrentAmbient = new CellAmbientState(
                new Vector3(0.20f, 0.20f, 0.20f),
                Vector3.Zero,
                sunToWorld);
            return;
        }

        _lighting.Sun = new LightSource
        {
            Kind = LightKind.Directional,
            WorldForward = sunToWorld,
            ColorLinear = keyframe.SunColor,
            Intensity = 1f,
            Range = 1f,
        };
        _lighting.CurrentAmbient = new CellAmbientState(
            keyframe.AmbientColor,
            keyframe.SunColor,
            sunToWorld);
    }
}

internal sealed class RuntimeWorldFrameAnimatedEntitySource
    : IWorldFrameAnimatedEntitySource
{
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _live;
    private readonly RetailStaticAnimatingObjectScheduler? _statics;
    private readonly EquippedChildRenderController? _equipped;
    private readonly HashSet<uint> _scratch = [];

    public RuntimeWorldFrameAnimatedEntitySource(
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> live,
        RetailStaticAnimatingObjectScheduler? statics,
        EquippedChildRenderController? equipped)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _statics = statics;
        _equipped = equipped;
    }

    public HashSet<uint> Capture()
    {
        _live.CopySpatialIdsTo(_scratch);
        _statics?.CopyAnimatedEntityIdsTo(_scratch);
        if (_equipped is not null)
        {
            foreach (uint entityId in _equipped.AttachedEntityIds)
                _scratch.Add(entityId);
        }

        return _scratch;
    }
}

internal sealed class RuntimeWorldFrameBuildingSource : IWorldFrameBuildingSource
{
    private readonly LandblockPresentationPipeline _presentation;
    private readonly CellVisibility _cells;
    private readonly List<LoadedCell> _scratch = [];

    public RuntimeWorldFrameBuildingSource(
        LandblockPresentationPipeline presentation,
        CellVisibility cells)
    {
        _presentation = presentation
            ?? throw new ArgumentNullException(nameof(presentation));
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    public WorldBuildingFrame Gather(
        LoadedCell? viewerRoot,
        uint viewerCellId,
        in FrustumPlanes frustum)
    {
        _scratch.Clear();
        LoadedCell? outdoorNode = null;
        if (viewerRoot is null && viewerCellId == 0u)
            return new WorldBuildingFrame(null, _scratch);

        foreach (BuildingRegistry registry in _presentation.BuildingRegistries)
        {
            foreach (Building building in registry.All())
            {
                if (building.HasPortalBounds
                    && !FrustumCuller.IsAabbVisible(
                        frustum,
                        building.PortalBounds.Min,
                        building.PortalBounds.Max))
                {
                    continue;
                }

                foreach (uint cellId in building.EnvCellIds)
                {
                    if (_cells.TryGetCell(cellId, out LoadedCell? cell) && cell is not null)
                        _scratch.Add(cell);
                }
            }
        }

        if (viewerRoot is null)
            outdoorNode = OutdoorCellNode.Build(viewerCellId);

        return new WorldBuildingFrame(outdoorNode, _scratch);
    }
}
