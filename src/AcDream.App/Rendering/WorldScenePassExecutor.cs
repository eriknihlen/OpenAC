using AcDream.App.Rendering.Sky;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Rendering;
using AcDream.Core.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IWorldScenePassExecutor
{
    void BeginFrame();

    void PrepareFlatWorldClip();

    void DrawFlatSky(
        in WorldCameraFrame camera,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup,
        float dayFraction);

    void DrawFlatTerrain(in WorldCameraFrame camera, uint? playerLandblockId);

    void DrawFlatEntities(
        in WorldCameraFrame camera,
        IEnumerable<(uint LandblockId, System.Numerics.Vector3 AabbMin,
            System.Numerics.Vector3 AabbMax,
            IReadOnlyList<WorldEntity> Entities,
            IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> entries,
        uint? playerLandblockId,
        HashSet<uint> animatedEntityIds);

    void DrawPostWorldParticles(
        LoadedCell? clipRoot,
        ClipFrameAssembly? clipAssembly,
        in WorldCameraFrame camera);

    void DrawFlatWeather(
        in WorldCameraFrame camera,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup,
        float dayFraction);

    void DisableClipDistances();

    void AbortFrame();
}

internal sealed class WorldScenePassExecutor : IWorldScenePassExecutor
{
    private readonly IWorldPassSurface _surface;
    private readonly IRenderFrameGlState _frameGlState;
    private readonly ClipFrame _clipFrame;
    private readonly WbDrawDispatcher _entities;
    private readonly EnvCellRenderer _environmentCells;
    private readonly TerrainModernRenderer? _terrain;
    private readonly TerrainDrawDiagnosticsController _terrainDiagnostics;
    private readonly SkyRenderer? _sky;
    private readonly ParticleSystem? _particles;
    private readonly ParticleRenderer? _particleRenderer;
    private readonly HashSet<uint> _visibleParticleOwners = [];
    private readonly HashSet<uint> _noExcludedParticleOwners = [];

    public WorldScenePassExecutor(
        IWorldPassSurface surface,
        IRenderFrameGlState frameGlState,
        ClipFrame clipFrame,
        WbDrawDispatcher entities,
        EnvCellRenderer environmentCells,
        TerrainModernRenderer? terrain,
        TerrainDrawDiagnosticsController terrainDiagnostics,
        SkyRenderer? sky,
        ParticleSystem? particles,
        ParticleRenderer? particleRenderer)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _frameGlState = frameGlState
            ?? throw new ArgumentNullException(nameof(frameGlState));
        _clipFrame = clipFrame ?? throw new ArgumentNullException(nameof(clipFrame));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _environmentCells = environmentCells
            ?? throw new ArgumentNullException(nameof(environmentCells));
        _terrain = terrain;
        _terrainDiagnostics = terrainDiagnostics
            ?? throw new ArgumentNullException(nameof(terrainDiagnostics));
        _sky = sky;
        _particles = particles;
        _particleRenderer = particleRenderer;
    }

    public void BeginFrame()
    {
        _visibleParticleOwners.Clear();
        _clipFrame.Reset();
    }

    public void PrepareFlatWorldClip() => _surface.PrepareClipFrame();

    public void DrawFlatSky(
        in WorldCameraFrame camera,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup,
        float dayFraction)
    {
        _surface.EnableClipDistances();
        Exception? drawFailure = null;
        try
        {
            _sky?.RenderSky(
                camera.Camera,
                camera.Position,
                dayFraction,
                activeDayGroup,
                foundation.Sky,
                foundation.EnvironOverrideActive);
        }
        catch (Exception error)
        {
            drawFailure = error;
            throw;
        }
        finally
        {
            try
            {
                DisableClipDistances();
            }
            catch (Exception closeFailure) when (drawFailure is not null)
            {
                throw new AggregateException(
                    "Sky drawing failed and its clip-distance bracket could not be closed.",
                    drawFailure,
                    closeFailure);
            }
        }

        if (_particles is not null && _particleRenderer is not null)
        {
            _particleRenderer.Draw(
                camera.Camera,
                camera.Position,
                ParticleRenderPass.SkyPreScene);
        }
    }

    public void DrawFlatTerrain(
        in WorldCameraFrame camera,
        uint? playerLandblockId)
    {
        _surface.EnableClipDistances();
        _terrainDiagnostics.Begin();
        _terrain?.Draw(
            camera.Camera,
            camera.Frustum,
            neverCullLandblockId: playerLandblockId);
        _terrainDiagnostics.Complete();
    }

    public void DrawFlatEntities(
        in WorldCameraFrame camera,
        IEnumerable<(uint LandblockId, System.Numerics.Vector3 AabbMin,
            System.Numerics.Vector3 AabbMax,
            IReadOnlyList<WorldEntity> Entities,
            IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> entries,
        uint? playerLandblockId,
        HashSet<uint> animatedEntityIds) =>
        _entities.Draw(
            camera.Camera,
            entries,
            camera.Frustum,
            neverCullLandblockId: playerLandblockId,
            visibleCellIds: null,
            animatedEntityIds: animatedEntityIds);

    public void DrawPostWorldParticles(
        LoadedCell? clipRoot,
        ClipFrameAssembly? clipAssembly,
        in WorldCameraFrame camera)
    {
        if (_particles is null || _particleRenderer is null)
            return;

        if (clipRoot is null)
        {
            if (clipAssembly is not null)
            {
                _particleRenderer.DrawForOwners(
                    camera.Camera,
                    camera.Position,
                    ParticleRenderPass.Scene,
                    _visibleParticleOwners,
                    includeUnattached: true,
                    excludedAttachedOwnerIds: _noExcludedParticleOwners);
                return;
            }

            _particleRenderer.Draw(
                camera.Camera,
                camera.Position,
                ParticleRenderPass.Scene);
            return;
        }

        return;
    }

    public void DrawFlatWeather(
        in WorldCameraFrame camera,
        in RenderFrameFoundation foundation,
        DayGroupData? activeDayGroup,
        float dayFraction)
    {
        _surface.EnableClipDistances();
        Exception? drawFailure = null;
        try
        {
            _sky?.RenderWeather(
                camera.Camera,
                camera.Position,
                dayFraction,
                activeDayGroup,
                foundation.Sky,
                foundation.EnvironOverrideActive);
        }
        catch (Exception error)
        {
            drawFailure = error;
            throw;
        }
        finally
        {
            try
            {
                DisableClipDistances();
            }
            catch (Exception closeFailure) when (drawFailure is not null)
            {
                throw new AggregateException(
                    "Weather drawing failed and its clip-distance bracket could not be closed.",
                    drawFailure,
                    closeFailure);
            }
        }

        if (_particles is not null && _particleRenderer is not null)
        {
            _particleRenderer.Draw(
                camera.Camera,
                camera.Position,
                ParticleRenderPass.SkyPostScene);
        }
    }

    public void DisableClipDistances() => _surface.DisableClipDistances();

    public void AbortFrame()
    {
        List<Exception>? failures = null;
        TryAbort(_frameGlState.RestoreFrameDefaults);
        TryAbort(_clipFrame.Reset);
        TryAbort(_entities.AbortCurrentRenderSceneObserverFrame);
        _visibleParticleOwners.Clear();
        if (failures is { Count: > 0 })
            throw new AggregateException("World scene pass abort failed.", failures);

        void TryAbort(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
    }

}
