using System.Diagnostics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

internal readonly record struct FramePipelineDiagnosticFacts(
    LandblockPresentationDiagnostics Presentation,
    RollingTimingPercentiles UploadTiming,
    int EntitiesWalked,
    int DeferredApplyBacklog,
    int FullWindowRetirementCount,
    int LastFullWindowRetirementLandblockCount,
    int MaterializedEntityCount,
    int SpawnSnapshotCount,
    int ResidentEntityCount);

internal interface IFramePipelineDiagnosticFactsSource
{
    TerrainRenderDiagnosticFacts CaptureTerrain();

    FramePipelineDiagnosticFacts CaptureFrame();
}

internal sealed class RuntimeFramePipelineDiagnosticFactsSource :
    IFramePipelineDiagnosticFactsSource
{
    private readonly TerrainModernRenderer? _terrain;
    private readonly LandblockPresentationPipeline? _presentation;
    private readonly RuntimeRenderFrameLivePreparation? _livePreparation;
    private readonly WbDrawDispatcher? _dispatcher;
    private readonly StreamingController? _streaming;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly GpuWorldState _world;

    public RuntimeFramePipelineDiagnosticFactsSource(
        TerrainModernRenderer? terrain,
        LandblockPresentationPipeline? presentation,
        RuntimeRenderFrameLivePreparation? livePreparation,
        WbDrawDispatcher? dispatcher,
        StreamingController? streaming,
        LiveEntityRuntime liveEntities,
        GpuWorldState world)
    {
        _terrain = terrain;
        _presentation = presentation;
        _livePreparation = livePreparation;
        _dispatcher = dispatcher;
        _streaming = streaming;
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public TerrainRenderDiagnosticFacts CaptureTerrain()
    {
        int walkDraws = _terrain?.WalkDrawCount ?? 0;
        if (walkDraws > 0)
            return new(_terrain!.WalkVisibleSlotCount, walkDraws, _terrain.LoadedSlots, _terrain.CapacitySlots);

        int visibleSlots = _terrain?.VisibleSlots ?? 0;
        return new(visibleSlots, visibleSlots, _terrain?.LoadedSlots ?? 0, _terrain?.CapacitySlots ?? 0);
    }

    public FramePipelineDiagnosticFacts CaptureFrame() => new(
        _presentation?.Diagnostics ?? default,
        _livePreparation?.UploadTiming ?? default,
        _dispatcher?.LastDrawStats.EntitiesWalked ?? 0,
        _streaming?.DeferredApplyBacklog ?? 0,
        _streaming?.FullWindowRetirementCount ?? 0,
        _streaming?.LastFullWindowRetirementLandblockCount ?? 0,
        _liveEntities.MaterializedCount,
        _liveEntities.Snapshots.Count,
        _world.Entities.Count);
}

internal sealed class TerrainDrawDiagnosticsController
{
    internal const long PublicationIntervalMilliseconds = 5_000;

    private readonly bool _enabled;
    private readonly WorldRenderDiagnostics _world;
    private readonly IFramePipelineDiagnosticFactsSource _facts;
    private readonly IRenderFrameDiagnosticLog _log;
    private long _lastPublicationMilliseconds;

    private long _walkAccumulatedTicks;

    public TerrainDrawDiagnosticsController(
        bool enabled,
        WorldRenderDiagnostics world,
        IFramePipelineDiagnosticFactsSource facts,
        IRenderFrameDiagnosticLog log)
    {
        _enabled = enabled;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Begin() => _world.BeginTerrainDraw();

    public void Complete() => Complete(_enabled ? Environment.TickCount64 : 0L);

    internal void Complete(long nowMilliseconds)
    {
        _world.EndTerrainDraw();
        PublishIfDue(nowMilliseconds);
    }

    public void AccumulateWalkBatch(long elapsedTicks) => _walkAccumulatedTicks += elapsedTicks;

    public void CompleteWalkFrame() => CompleteWalkFrame(_enabled ? Environment.TickCount64 : 0L);

    internal void CompleteWalkFrame(long nowMilliseconds)
    {
        double ticksToHundredthsMicroseconds = 1_000_000.0 * 100.0 / Stopwatch.Frequency;
        _world.PushTerrainSample(
            (long)(_walkAccumulatedTicks * ticksToHundredthsMicroseconds));
        _walkAccumulatedTicks = 0;
        PublishIfDue(nowMilliseconds);
    }

    private void PublishIfDue(long nowMilliseconds)
    {
        if (!_enabled
            || nowMilliseconds - _lastPublicationMilliseconds <= PublicationIntervalMilliseconds)
        {
            return;
        }

        _world.PublishTerrainDiagnostics(_facts.CaptureTerrain());
        _log.WriteLine(FormatFrame(_facts.CaptureFrame()));
        _lastPublicationMilliseconds = nowMilliseconds;
    }

    internal static string FormatFrame(FramePipelineDiagnosticFacts facts)
    {
        LandblockRenderPublisherDiagnostics render = facts.Presentation.Render;
        LandblockPhysicsPublisherDiagnostics physics = facts.Presentation.Physics;
        LandblockStaticPresentationDiagnostics statics = facts.Presentation.Statics;
        double ticksToMicros = 1_000_000.0 / Stopwatch.Frequency;
        double uploadMedian = facts.UploadTiming.MedianHundredthsMicroseconds / 100.0;
        double uploadP95 = facts.UploadTiming.Percentile95HundredthsMicroseconds / 100.0;
        return
            $"[FRAME-DIAG] publish={render.BeginCount}/{render.CompleteCount} "
            + $"render_total_us=[terrain={render.TerrainPublishTicks * ticksToMicros:F1} "
            + $"begin={render.BeginPublishTicks * ticksToMicros:F1} "
            + $"complete={render.CompletePublishTicks * ticksToMicros:F1}] "
            + $"physics_total_us=[base={physics.BasePublishTicks * ticksToMicros:F1} "
            + $"gfx={physics.GfxCacheTicks * ticksToMicros:F1} "
            + $"complete={physics.CompletePublishTicks * ticksToMicros:F1}] "
            + $"static={statics.BeginCount}/{statics.CompleteCount}"
            + $"(active={statics.ActiveEntityCount})  "
            + $"entUpl_us={uploadMedian:F1}m/{uploadP95:F1}p95  "
            + $"deferred={facts.DeferredApplyBacklog}  "
            + $"fullRetire={facts.FullWindowRetirementCount}"
            + $"(landblocks={facts.LastFullWindowRetirementLandblockCount})  "
            + $"esg={facts.MaterializedEntityCount} spawn={facts.SpawnSnapshotCount} "
            + $"resident={facts.ResidentEntityCount} walked={facts.EntitiesWalked}";
    }
}
