using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal sealed class RetailPViewRenderer
{
    private readonly RenderSceneShadowRuntime _renderSceneShadow;
    private readonly ClipFrameAssembly _clipAssemblyScratch = new();
    private readonly RetailPViewFrameResult _frameResultScratch = new();

    private readonly HashSet<uint> _drawableCellsScratch = new();
    private readonly HashSet<uint> _visibleCellsScratch = new();
    private readonly HashSet<uint> _visibleLandscapeCellsScratch = new();

    private Action? _walkPreClearDynamics;

    private Walk.WalkFrameDriver? _walkFrameDriverScratch;
    private Walk.WalkProductionFrameContext? _walkFrameContextScratch;
    private WalkProductionLeafRenderer? _walkLeafRendererScratch;
    private readonly Action _walkFlushLandscapeAction;
    private readonly Action _walkClearInteriorDepthAction;
    private readonly Func<int> _walkDrawExitSealsFunc;
    private RetailPViewPassExecutor? _activeWalkPasses;
    private RetailPViewFrameInput? _activeWalkFrame;
    private ClipFrameAssembly? _activeWalkClipAssembly;

    private readonly Walk.WalkBuildingRegistry _walkBuildings;
    private readonly Walk.WalkLandscapeAssembler _walkLandscape;
    private readonly CellVisibility _walkCellRegistry;

    private readonly Walk.WalkProductionWorldData _walkWorldData;

    internal RetailPViewRenderer(
        RenderSceneShadowRuntime renderSceneShadow,
        Walk.WalkBuildingRegistry walkBuildings,
        Walk.WalkLandscapeAssembler walkLandscape,
        CellVisibility walkCellRegistry,
        ShadowObjectRegistry shadows,
        BuildingDegradeController? buildingDegrades = null)
    {
        _renderSceneShadow = renderSceneShadow
            ?? throw new ArgumentNullException(nameof(renderSceneShadow));
        _walkBuildings = walkBuildings
            ?? throw new ArgumentNullException(nameof(walkBuildings));
        _walkLandscape = walkLandscape
            ?? throw new ArgumentNullException(nameof(walkLandscape));
        _walkCellRegistry = walkCellRegistry
            ?? throw new ArgumentNullException(nameof(walkCellRegistry));
        _walkWorldData = new Walk.WalkProductionWorldData(
            _walkBuildings,
            shadows ?? throw new ArgumentNullException(nameof(shadows)));
        _frameWalk = buildingDegrades is null
            ? new Walk.RetailFrameWalk()
            : new Walk.RetailFrameWalk(buildingDegrades);
        _walkFlushLandscapeAction = FlushWalkLandscape;
        _walkClearInteriorDepthAction = ClearWalkInteriorDepth;
        _walkDrawExitSealsFunc = DrawWalkExitSeals;
    }

    private const float OutdoorBuildingSeedDistance = float.PositiveInfinity;

    internal RetailPViewFrameResult DrawInside(
        RetailPViewFrameInput ctx,
        RetailPViewPassExecutor passes)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(passes);
        passes.BeginFrame();
        RetailPViewPassExecutor walkExecutor = passes as RetailPViewPassExecutor
            ?? throw new InvalidOperationException(
                "The retail frame walk requires RetailPViewPassExecutor.");
        ClipFrameAssembly clipAssembly = passes.BeginWalkClipFrame(
            ctx.RootCell.IsOutdoorNode,
            _clipAssemblyScratch);

        _drawableCellsScratch.Clear();
        HashSet<uint> drawableCells = _drawableCellsScratch;

        Walk.WalkFrameDriver walkDriver;
        {
            Matrix4x4 view = ctx.CameraView;
            var forward = Vector3.Normalize(new Vector3(-view.M13, -view.M23, -view.M33));
            (int Width, int Height)? attachment = walkExecutor!.WalkAttachmentExtent;
            float viewportWidth = attachment?.Width ?? 1024f;
            float viewportHeight = attachment?.Height ?? 720f;
            bool weatherGateOpen = RetailPViewPassExecutor.ShouldDrawWeatherOnce(
                ctx.RenderSky, ctx.RenderWeather, ctx.PlayerCellId);
            if (_walkFrameContextScratch is null)
            {
                _walkFrameContextScratch = new Walk.WalkProductionFrameContext(
                    _walkCellRegistry,
                    _walkBuildings,
                    ctx.ViewerEyePos,
                    forward,
                    ctx.ViewProjection,
                    viewportWidth,
                    viewportHeight,
                    ctx.ViewerCellId,
                    weatherGateOpen);
            }
            else
            {
                _walkFrameContextScratch.Reset(
                    ctx.ViewerEyePos,
                    forward,
                    ctx.ViewProjection,
                    viewportWidth,
                    viewportHeight,
                    ctx.ViewerCellId,
                    weatherGateOpen);
            }
            Walk.WalkProductionFrameContext walkContext = _walkFrameContextScratch;
            _walkLandscape!.SetViewer(ctx.ViewerCellId, ctx.ViewerEyePos);
            Walk.WalkLandscape walkLandscape = _walkLandscape.Landscape;

            Walk.WalkCell? walkCameraCell = null;
            if ((ctx.ViewerCellId & 0xFFFFu) >= 0x100)
            {
                walkCameraCell = _walkCellRegistry!.TryGetCell(ctx.ViewerCellId, out LoadedCell? loaded)
                    ? loaded?.Walk
                    : null;
                if (walkCameraCell is null)
                {
                    throw new InvalidOperationException(
                        $"walk root=0x{ctx.ViewerCellId:X8}: the interior camera cell has "
                        + "no committed walk data — rendering "
                        + "requires the walk registry to already hold the viewer's own cell "
                        + "(fail-loud rule; a silently skipped root would leave the frame "
                        + "with no static draws at all).");
                }
            }

            if (ctx.RootCell.IsOutdoorNode && clipAssembly.OutsideViewSlices.Length != 1)
            {
                throw new InvalidOperationException(
                    "walk static cutover: an outdoor root's clip assembly produced "
                    + $"{clipAssembly.OutsideViewSlices.Length} outside-view slices, not the "
                    + "expected 1 — the outdoor root draws through the full-screen default "
                    + "view (retail set_default_view; the walk fans exactly its own 1), and "
                    + "the outdoor slice data comes from the assembler. Refuse an invalid count.");
            }

            _walkWorldData!.BeginFrame(
                _renderSceneShadow!.Query,
                ctx.PlayerLandblockId ?? 0u,
                ctx.RenderCenterLbX,
                ctx.RenderCenterLbY);

            _activeWalkPasses = walkExecutor;
            _activeWalkFrame = ctx;
            _activeWalkClipAssembly = clipAssembly;
            if (_walkLeafRendererScratch is null)
            {
                _walkLeafRendererScratch = new WalkProductionLeafRenderer(
                    walkExecutor,
                    ctx,
                    clipAssembly,
                    _walkFlushLandscapeAction,
                    _walkClearInteriorDepthAction,
                    _walkDrawExitSealsFunc);
            }
            else
            {
                _walkLeafRendererScratch.Reset(
                    walkExecutor,
                    ctx,
                    clipAssembly,
                    _walkFlushLandscapeAction,
                    _walkClearInteriorDepthAction,
                    _walkDrawExitSealsFunc);
            }

            if (_walkFrameDriverScratch is null)
            {
                _walkFrameDriverScratch = new Walk.WalkFrameDriver(
                    walkExecutor.Dispatcher,
                    _walkLeafRendererScratch,
                    _walkWorldData,
                    clipFrame: clipAssembly.Frame);
            }
            else
            {
                _walkFrameDriverScratch.RebindFrame(
                    _walkLeafRendererScratch,
                    clipAssembly.Frame);
            }
            walkDriver = _walkFrameDriverScratch;

            _frameWalk.ObjectRingLimit = ctx.RenderRadius;

            try
            {
                walkDriver.Collect(
                    _frameWalk, ctx.ViewerCellId, walkCameraCell, walkLandscape, walkContext,
                    ctx.ViewProjection, ctx.CameraWorldPosition);
            }
            catch
            {
                walkDriver.AbortFrame();
                ClearWalkFrameBindings();
                throw;
            }
            if (walkCameraCell is not null)
            {
                ClipFrameAssembler.ReassembleOutsideViewFromWalk(
                    clipAssembly,
                    _frameWalk.InteriorOutsideView,
                    viewportWidth,
                    viewportHeight);
            }

            _drawableCellsScratch.Clear();
            _drawableCellsScratch.UnionWith(walkDriver.VisitedCells);
            walkDriver.CopyVisibleCellsTo(_visibleCellsScratch);
            _visibleLandscapeCellsScratch.Clear();
            _visibleLandscapeCellsScratch.UnionWith(walkDriver.VisitedLandscapeCellIds);

        }

        passes.PrepareClipFrame();

        HashSet<uint> prepareCells = drawableCells;


        passes.PrepareCellBatches(ctx, prepareCells);

        {
            RenderProjectionCounts retainedCounts = _renderSceneShadow.Counts;
            RenderFrameDiagnosticCounts counts = WalkDiagnosticCounts(retainedCounts);
            RenderProjectionCounts sourceCounts = retainedCounts;
            RetailPViewFrameResult result = _frameResultScratch.Reset(
                clipAssembly,
                drawableCells,
                _visibleCellsScratch,
                _visibleLandscapeCellsScratch,
                counts,
                sourceCounts,
                diagnosticPartition: null);
            _walkPreClearDynamics = () =>
            {
                DrawLandscapeDynamicsPhase(
                    ctx,
                    passes,
                    clipAssembly,
                    walkDriver);
            };
            try
            {
                DrawWalkDrivenStatics(ctx, walkExecutor, walkDriver!);
                if (ctx.RootCell.IsOutdoorNode)
                {
                    DrawLandscapeDynamicsPhase(
                        ctx,
                        passes,
                        clipAssembly,
                        walkDriver);
                }
            }
            finally
            {
                _walkPreClearDynamics = null;
                ClearWalkFrameBindings();
            }


            passes.DrawUnattachedSceneParticles(ctx, outdoorCells: false);

            return result;
        }
    }

    private readonly Walk.RetailFrameWalk _frameWalk;

    private void FlushWalkLandscape()
    {
        RetailPViewPassExecutor passes = _activeWalkPasses
            ?? throw new InvalidOperationException(
                "The retained walk leaf has no active pass binding.");

        _walkPreClearDynamics?.Invoke();
        passes.FlushLandscapeAlpha();
    }

    private void ClearWalkInteriorDepth()
    {
        RetailPViewPassExecutor passes = _activeWalkPasses
            ?? throw new InvalidOperationException(
                "The retained walk leaf has no active pass binding.");

        passes.ClearInteriorDepth();
    }

    private int DrawWalkExitSeals()
    {
        RetailPViewFrameInput frame = _activeWalkFrame
            ?? throw new InvalidOperationException(
                "The retained walk leaf has no active frame binding.");
        RetailPViewPassExecutor passes = _activeWalkPasses
            ?? throw new InvalidOperationException(
                "The retained walk leaf has no active pass binding.");
        Walk.WalkFrameDriver driver = _walkFrameDriverScratch
            ?? throw new InvalidOperationException(
                "The retained walk leaf has no active driver binding.");

        return DrawWalkExitPortalMasks(frame, passes, driver);
    }

    private void ClearWalkFrameBindings()
    {
        _walkFrameDriverScratch?.AbortFrame(clearRetained: false);
        _activeWalkPasses = null;
        _activeWalkFrame = null;
        _activeWalkClipAssembly = null;
    }

    private void DrawWalkDrivenStatics(
        RetailPViewFrameInput ctx,
        RetailPViewPassExecutor passes,
        Walk.WalkFrameDriver driver)
    {
        var (frame, encoder) = passes.RequireWalkSubmission();
        try
        {
            driver.Replay(frame, encoder);
        }
        finally
        {
            passes.CompleteWalkTerrainFrame();
        }


    }

    private void DrawLandscapeDynamicsPhase(
        RetailPViewFrameInput ctx,
        RetailPViewPassExecutor passes,
        ClipFrameAssembly clipAssembly,
        Walk.WalkFrameDriver walkDriver)
    {
        if (clipAssembly.OutsideViewSlices.Length != 0)
        {
            passes.DrawUnattachedSceneParticles(ctx, outdoorCells: true);
        }

        if (walkDriver.WeatherTurnFired)
            passes.DrawWeatherOnce(ctx);
    }

    private int DrawWalkExitPortalMasks(
        RetailPViewFrameInput ctx,
        RetailPViewPassExecutor passes,
        Walk.WalkFrameDriver driver)
    {
        int submitted = 0;
        List<uint> floodCells = driver.InteriorFloodCells;
        for (int i = floodCells.Count - 1; i >= 0; i--)
        {
            uint cellId = floodCells[i];
            int sliceCount = driver.InteriorFloodViewSliceCountAt(i);
            for (int sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
            {
                submitted += passes.DrawExitPortalMask(
                    ctx,
                    cellId,
                    driver.InteriorFloodViewClipPlanesAt(i, sliceIndex));
            }
        }
        return submitted;
    }

    private static RenderFrameDiagnosticCounts WalkDiagnosticCounts(
        RenderProjectionCounts source)
    {
        int dynamics = checked(
            source.LiveDynamicRoot
            + source.ActiveAnimatedStatic
            + source.EquippedChild);
        return new RenderFrameDiagnosticCounts(
            source.OutdoorStatic,
            source.IndoorCellStatic,
            dynamics,
            TransformCount: source.Total,
            OpaqueClassificationCount: 0,
            AlphaClassificationCount: 0,
            LightSetCount: 0,
            SelectionPartCount: 0,
            RouteCandidateCount: source.Total,
            EntityCandidateCount: source.Total,
            MeshPartCount: 0);
    }

}

public interface IRetailPViewCellSource
{
    LoadedCell? Find(uint cellId);
}

public sealed class RetailPViewFrameInput
{
    public LoadedCell RootCell { get; private set; } = null!;

    public IReadOnlyList<LoadedCell>? NearbyBuildingCells { get; private set; }

    public Vector3 ViewerEyePos { get; private set; }
    public Matrix4x4 ViewProjection { get; private set; }
    public IRetailPViewCellSource Cells { get; private set; } = null!;
    public ICamera Camera { get; private set; } = null!;
    public Vector3 CameraWorldPosition { get; private set; }
    public FrustumPlanes? Frustum { get; private set; }
    public uint? PlayerLandblockId { get; private set; }
    public HashSet<uint>? AnimatedEntityIds { get; private set; }
    public int RenderCenterLbX { get; private set; }
    public int RenderCenterLbY { get; private set; }
    public int RenderRadius { get; private set; }
    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntries
    { get; private set; } = Array.Empty<(uint, Vector3, Vector3,
        IReadOnlyList<WorldEntity>,
        IReadOnlyDictionary<uint, WorldEntity>?)>();

    public bool RenderSky { get; private set; }
    public bool RenderWeather { get; private set; }
    public float DayFraction { get; private set; }
    public DayGroupData? ActiveDayGroup { get; private set; }
    public SkyKeyframe SkyKeyframe { get; private set; }
    public bool EnvironOverrideActive { get; private set; }
    public uint ViewerCellId { get; private set; }
    public uint PlayerCellId { get; private set; }
    public Vector3 PlayerViewPosition { get; private set; }
    public Matrix4x4 CameraView { get; private set; }
    public CameraCellResolution CameraCellResolution { get; private set; }

    internal RetailPViewFrameInput Reset(
        LoadedCell rootCell,
        IReadOnlyList<LoadedCell>? nearbyBuildingCells,
        Vector3 viewerEyePos,
        Matrix4x4 viewProjection,
        IRetailPViewCellSource cells,
        ICamera camera,
        Vector3 cameraWorldPosition,
        FrustumPlanes? frustum,
        uint? playerLandblockId,
        HashSet<uint>? animatedEntityIds,
        int renderCenterLbX,
        int renderCenterLbY,
        int renderRadius,
        IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
            IReadOnlyList<WorldEntity> Entities,
            IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        bool renderSky,
        bool renderWeather,
        float dayFraction,
        DayGroupData? activeDayGroup,
        SkyKeyframe skyKeyframe,
        bool environOverrideActive,
        uint viewerCellId,
        uint playerCellId,
        Vector3 playerViewPosition,
        Matrix4x4 cameraView,
        CameraCellResolution cameraCellResolution)
    {
        RootCell = rootCell;
        NearbyBuildingCells = nearbyBuildingCells;
        ViewerEyePos = viewerEyePos;
        ViewProjection = viewProjection;
        Cells = cells;
        Camera = camera;
        CameraWorldPosition = cameraWorldPosition;
        Frustum = frustum;
        PlayerLandblockId = playerLandblockId;
        AnimatedEntityIds = animatedEntityIds;
        RenderCenterLbX = renderCenterLbX;
        RenderCenterLbY = renderCenterLbY;
        RenderRadius = renderRadius;
        LandblockEntries = landblockEntries;
        RenderSky = renderSky;
        RenderWeather = renderWeather;
        DayFraction = dayFraction;
        ActiveDayGroup = activeDayGroup;
        SkyKeyframe = skyKeyframe;
        EnvironOverrideActive = environOverrideActive;
        ViewerCellId = viewerCellId;
        PlayerCellId = playerCellId;
        PlayerViewPosition = playerViewPosition;
        CameraView = cameraView;
        CameraCellResolution = cameraCellResolution;
        return this;
    }
}

public sealed class RetailPViewFrameResult
{
    public ClipFrameAssembly ClipAssembly { get; private set; } = null!;
    public HashSet<uint> DrawableCells { get; private set; } = null!;

    public HashSet<uint> VisibleLandscapeCells { get; private set; } = null!;

    public HashSet<uint> VisibleCells { get; private set; } = null!;

    internal RenderFrameDiagnosticCounts DiagnosticCounts { get; private set; }
    internal RenderProjectionCounts SourceCounts { get; private set; }
    internal InteriorEntityPartition.Result? DiagnosticPartition
    { get; private set; }

    internal RetailPViewFrameResult Reset(
        ClipFrameAssembly clipAssembly,
        HashSet<uint> drawableCells,
        HashSet<uint> visibleCells,
        HashSet<uint> visibleLandscapeCells,
        RenderFrameDiagnosticCounts diagnosticCounts,
        RenderProjectionCounts sourceCounts,
        InteriorEntityPartition.Result? diagnosticPartition)
    {
        ClipAssembly = clipAssembly;
        DrawableCells = drawableCells;
        VisibleCells = visibleCells;
        VisibleLandscapeCells = visibleLandscapeCells;
        DiagnosticCounts = diagnosticCounts;
        SourceCounts = sourceCounts;
        DiagnosticPartition = diagnosticPartition;
        return this;
    }

}
