using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.Core.Rendering;

namespace AcDream.App.Rendering;

internal interface IWorldScenePViewRenderer
{

    RetailPViewFrameResult DrawInside(RetailPViewFrameInput input);

    void AbortFrame();
}

internal readonly record struct PreparedWorldSceneFrame(
    bool ShouldRender,
    RenderFrameFoundation Foundation,
    WorldRenderFrame World,
    int ActiveDayGroup);

internal interface IPreparedWorldSceneFramePhase : IWorldSceneFramePhase
{
    PreparedWorldSceneFrame PrepareEnhanced(RenderFrameInput input);

    WorldRenderFrameOutcome RenderPreparedEnhanced(
        RenderFrameInput input,
        in PreparedWorldSceneFrame prepared);

    void CancelPreparedEnhanced(in PreparedWorldSceneFrame prepared);
}

internal sealed class WorldScenePViewRenderer : IWorldScenePViewRenderer
{
    private readonly RetailPViewRenderer _renderer;
    private readonly RetailPViewPassExecutor _passes;

    public WorldScenePViewRenderer(
        RetailPViewRenderer renderer,
        RetailPViewPassExecutor passes)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
    }


    public RetailPViewFrameResult DrawInside(RetailPViewFrameInput input) =>
        _renderer.DrawInside(input, _passes);

    public void AbortFrame() => _passes.AbortFrame();
}

internal sealed class WorldSceneRenderer : IPreparedWorldSceneFramePhase
{
    private readonly IRenderFrameFoundationSource _foundation;
    private readonly IRenderLoginStateSource _login;
    private readonly IWorldSceneSkyStateSource _sky;
    private readonly IWorldRenderFrameBuilder _frames;
    private readonly IWorldSceneEntitySource _entities;
    private readonly IWorldSceneSelectionFrame? _selection;
    private readonly IWorldSceneAlphaFrame _alpha;
    private readonly IWorldSceneParticleVisibility _particleVisibility;
    private readonly IWorldScenePViewRenderer _pview;
    private readonly IRetailPViewCellSource _pviewCells;
    private readonly IWorldScenePassExecutor _passes;
    private readonly IWorldRenderRangeSource _renderRange;
    private readonly IWorldSceneDiagnostics _diagnostics;
    private readonly IWorldGenerationAvailability _availability;
    private readonly IAtmosphericWorldFrameSink? _atmosphere;
    private readonly RetailPViewFrameInput _pviewFrameInput = new();
    private WorldRenderFrame _preparedEnhancedWorld;
    private bool _hasPreparedEnhancedWorld;

    public WorldSceneRenderer(
        IRenderFrameFoundationSource foundation,
        IRenderLoginStateSource login,
        IWorldSceneSkyStateSource sky,
        IWorldRenderFrameBuilder frames,
        IWorldSceneEntitySource entities,
        IWorldSceneSelectionFrame? selection,
        IWorldSceneAlphaFrame alpha,
        IWorldSceneParticleVisibility particleVisibility,
        IWorldScenePViewRenderer pview,
        IRetailPViewCellSource pviewCells,
        IWorldScenePassExecutor passes,
        IWorldRenderRangeSource renderRange,
        IWorldSceneDiagnostics diagnostics,
        IWorldGenerationAvailability? availability = null,
        IAtmosphericWorldFrameSink? atmosphere = null)
    {
        _foundation = foundation ?? throw new ArgumentNullException(nameof(foundation));
        _login = login ?? throw new ArgumentNullException(nameof(login));
        _sky = sky ?? throw new ArgumentNullException(nameof(sky));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _selection = selection;
        _alpha = alpha ?? throw new ArgumentNullException(nameof(alpha));
        _particleVisibility = particleVisibility
            ?? throw new ArgumentNullException(nameof(particleVisibility));
        _pview = pview ?? throw new ArgumentNullException(nameof(pview));
        _pviewCells = pviewCells ?? throw new ArgumentNullException(nameof(pviewCells));
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _renderRange = renderRange ?? throw new ArgumentNullException(nameof(renderRange));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _availability = availability ?? AlwaysAvailableWorldGeneration.Instance;
        _atmosphere = atmosphere;
    }

    public WorldRenderFrameOutcome Render(RenderFrameInput input)
    {
        _ = input;
        bool selectionFrameStarted = _selection is not null;
        bool worldFrameStarted = false;
        bool pviewFrameStarted = false;
        try
        {
            FrustumPlanes? preparedSelectionFrustum = _hasPreparedEnhancedWorld
                ? _preparedEnhancedWorld.Camera.Frustum
                : null;
            _selection?.BeginFrame(preparedSelectionFrustum);
            if (!_availability.IsWorldAvailable)
            {
                _selection?.CompleteFrame();
                selectionFrameStarted = false;
                return default;
            }

            RenderFrameFoundation foundation = _foundation.Foundation;
            if (foundation.PortalViewportVisible)
            {
                _selection?.CompleteFrame();
                selectionFrameStarted = false;
                return default;
            }

            // Set before BeginFrame so an already-poisoned queue is recovered by
            // the same abort path instead of making every later frame fail.
            worldFrameStarted = true;
            _alpha.BeginFrame();
            WorldRenderFrame world;
            if (_hasPreparedEnhancedWorld)
            {
                world = _preparedEnhancedWorld;
                _hasPreparedEnhancedWorld = false;
            }
            else
            {
                world = _frames.Build(
                    in foundation,
                    _login.IsWaitingForLogin,
                    _sky.ActiveDayGroup);
            }
            _atmosphere?.Publish(
                in foundation,
                in world,
                _sky.ActiveDayGroupIndex);
            _passes.BeginFrame();

            WorldCameraFrame camera = world.Camera;
            WorldRootFrame roots = world.Roots;
            LoadedCell? clipRoot = world.ClipRoot;
            bool renderSky = roots.RenderSky;
            bool drawSkyThisFrame = false;
            RetailPViewFrameResult? pviewResult = null;

            if (clipRoot is null)
            {
                _passes.PrepareFlatWorldClip();
                drawSkyThisFrame = renderSky;
                if (drawSkyThisFrame)
                {
                    _passes.DrawFlatSky(
                        in camera,
                        in foundation,
                        _sky.ActiveDayGroup,
                        _sky.DayFraction);
                }

                _passes.DrawFlatTerrain(in camera, roots.PlayerLandblockId);
            }

            if (clipRoot is not null)
            {
                pviewFrameStarted = true;
                pviewResult = _pview.DrawInside(
                    _pviewFrameInput.Reset(
                        clipRoot,
                        world.Buildings.NearbyBuildingCells,
                        roots.ViewerEyePosition,
                        camera.ViewProjection,
                        _pviewCells,
                        camera.Camera,
                        camera.Position,
                        camera.Frustum,
                        roots.PlayerLandblockId,
                        world.AnimatedEntityIds,
                        roots.RenderCenterLandblockX,
                        roots.RenderCenterLandblockY,
                        _renderRange.NearRadius,
                        _entities.LandblockEntries,
                        renderSky,
                        roots.PlayerSeenOutside,
                        _sky.DayFraction,
                        _sky.ActiveDayGroup,
                        foundation.Sky,
                        foundation.EnvironOverrideActive,
                        roots.ViewerCellId,
                        roots.PlayerCellId,
                        roots.PlayerViewPosition,
                        camera.Camera.View,
                        _diagnostics.CameraCellResolution));

                _particleVisibility.MarkVisibleLandscapeCells(
                    pviewResult.VisibleLandscapeCells);
            }
            else
            {
                _passes.DrawFlatEntities(
                    in camera,
                    _entities.LandblockEntries,
                    roots.PlayerLandblockId,
                    world.AnimatedEntityIds);
            }

            _passes.DisableClipDistances();
            _passes.DrawPostWorldParticles(
                clipRoot,
                pviewResult?.ClipAssembly,
                in camera);

            if (clipRoot is null && drawSkyThisFrame)
            {
                _passes.DrawFlatWeather(
                    in camera,
                    in foundation,
                    _sky.ActiveDayGroup,
                    _sky.DayFraction);
            }


            WorldSceneDiagnosticOutcome diagnostic = _diagnostics.DrawAndPublish(
                in camera,
                _entities.LandblockBounds);
            CompleteWorldFrame();
            worldFrameStarted = false;
            pviewFrameStarted = false;
            _selection?.CompleteFrame();
            selectionFrameStarted = false;
            return new WorldRenderFrameOutcome(
                diagnostic.VisibleLandblocks,
                diagnostic.TotalLandblocks,
                NormalWorldDrawn: true);
        }
        catch (Exception renderFailure)
        {
            List<Exception>? abortFailures = AbortFrame(
                worldFrameStarted,
                pviewFrameStarted,
                selectionFrameStarted);
            if (abortFailures is { Count: > 0 })
            {
                abortFailures.Insert(0, renderFailure);
                throw new AggregateException(
                    "World rendering failed and the incomplete frame could not be fully aborted.",
                    abortFailures);
            }
            throw;
        }
    }

    public PreparedWorldSceneFrame PrepareEnhanced(RenderFrameInput input)
    {
        _ = input;
        if (_hasPreparedEnhancedWorld)
        {
            throw new InvalidOperationException(
                "The prior enhanced world preparation was not consumed.");
        }

        RenderFrameFoundation foundation = _foundation.Foundation;
        if (!_availability.IsWorldAvailable || foundation.PortalViewportVisible)
            return new PreparedWorldSceneFrame(false, foundation, default, -1);

        WorldRenderFrame world = _frames.Build(
            in foundation,
            _login.IsWaitingForLogin,
            _sky.ActiveDayGroup);
        WorldRootFrame roots = world.Roots;
        world = world with
        {
            ResidentStreamingWindow =
                _entities.CaptureResidentStreamingWindow(
                    roots.RenderCenterLandblockX,
                    roots.RenderCenterLandblockY),
            CelestialShadowSource =
                AuthoredCelestialShadowSourceResolver.Resolve(
                    _sky.ActiveDayGroup,
                    _sky.DayFraction,
                    foundation.Sky),
        };
        _preparedEnhancedWorld = world;
        _hasPreparedEnhancedWorld = true;
        return new PreparedWorldSceneFrame(
            true,
            foundation,
            world,
            _sky.ActiveDayGroupIndex);
    }

    public WorldRenderFrameOutcome RenderPreparedEnhanced(
        RenderFrameInput input,
        in PreparedWorldSceneFrame prepared)
    {
        if (prepared.ShouldRender != _hasPreparedEnhancedWorld)
        {
            throw new InvalidOperationException(
                "The prepared enhanced-world token does not match the pending frame.");
        }

        try
        {
            return Render(input);
        }
        finally
        {
            _hasPreparedEnhancedWorld = false;
            _preparedEnhancedWorld = default;
        }
    }

    public void CancelPreparedEnhanced(in PreparedWorldSceneFrame prepared)
    {
        if (!prepared.ShouldRender)
            return;
        if (!_hasPreparedEnhancedWorld)
            return;
        _hasPreparedEnhancedWorld = false;
        _preparedEnhancedWorld = default;
    }

    private void CompleteWorldFrame()
    {
        _alpha.EndFrame();
        _particleVisibility.CompleteFrame();
    }

    private List<Exception>? AbortFrame(
        bool worldFrameStarted,
        bool pviewFrameStarted,
        bool selectionFrameStarted)
    {
        List<Exception>? failures = null;
        if (worldFrameStarted)
        {
            if (pviewFrameStarted)
                TryAbort(_pview.AbortFrame);
            TryAbort(_passes.AbortFrame);
            TryAbort(_particleVisibility.AbortFrame);
            TryAbort(_alpha.AbortFrame);
        }
        if (selectionFrameStarted && _selection is not null)
            TryAbort(_selection.AbortFrame);
        return failures;

        void TryAbort(Action abort)
        {
            try
            {
                abort();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
    }
}
