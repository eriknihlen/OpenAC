using AcDream.Core.World;
using AcDream.App.Rendering.Packs;

namespace AcDream.App.Rendering;

internal readonly record struct RenderFrameDiagnosticsSnapshot(
    double Fps,
    double FrameMilliseconds,
    int VisibleLandblocks,
    int TotalLandblocks,
    int EntityCount,
    int AnimatedEntityCount)
{
    public static RenderFrameDiagnosticsSnapshot Initial => new(
        Fps: 60.0,
        FrameMilliseconds: 16.7,
        VisibleLandblocks: 0,
        TotalLandblocks: 0,
        EntityCount: 0,
        AnimatedEntityCount: 0);
}

internal readonly record struct RenderFrameTitleFacts(
    int EntityCount,
    int AnimatedEntityCount,
    DerethDateTime.Calendar Calendar,
    double DayFraction);

internal interface IRenderFrameTitleFactsSource
{
    RenderFrameTitleFacts Capture();
}

internal interface IRenderFrameTitleSink
{
    void SetTitle(string title);
}

internal interface IRenderFrameDiagnosticLog
{
    void WriteLine(string message);
}

internal sealed class ConsoleRenderFrameDiagnosticLog : IRenderFrameDiagnosticLog
{
    public void WriteLine(string message) => Console.WriteLine(message);
}

internal interface IRenderFrameResourceDiagnosticsSource
{
    RenderFrameResourceDiagnosticsSnapshot Capture();
}

internal readonly record struct VfxStreamResourceDiagnostics(
    int ActiveEmitters,
    int ActiveParticles,
    int ActiveBindings);

internal readonly record struct DynamicBufferResourceDiagnostics(
    int WorldBufferSets,
    int EnvCellBufferSets,
    int ParticleBufferSets,
    long ParticleBufferBytes,
    long UiTextBufferBytes,
    long PortalDepthMaskBufferBytes,
    int ClipBufferSets,
    int TerrainIndirectBuffers,
    int SceneLightingBuffers);

internal readonly record struct MeshStreamResourceDiagnostics(
    int RenderData,
    int AtlasArrays,
    int UnusedLru,
    long EstimatedBytes,
    long GlobalUploadCount,
    long GlobalUploadedBytes,
    int FrameUploadCount,
    long FrameUploadBytes,
    long FrameArrayAllocationBytes,
    long FramePlannedMipmapBytes,
    int FrameNewArrayCount,
    long FrameBufferUploadBytes,
    long FrameBufferAllocationBytes,
    long FrameBufferCopyBytes,
    int FrameNewBufferCount,
    int StagedUploadBacklog,
    long StagedUploadBytes,
    bool StagingAtHighWater,
    int FrameStaleDiscardCount,
    int CpuMeshCacheCount,
    long CpuMeshCacheBytes,
    int FrameMipmapArrayCount,
    long FrameMipmapBytes,
    long GlobalCapacityBytes,
    long GlobalPhysicalCapacityBytes,
    bool GlobalMigrationInProgress,
    long PreparedProbes,
    long PreparedReads,
    long PreparedLoaded,
    long PreparedMissing,
    long PreparedCorrupt);

internal readonly record struct TextureStreamResourceDiagnostics(
    int OwnedBindlessTextures,
    int TextureOwners,
    int ActiveParticleTextures,
    int ParticleTextureOwners,
    int CachedParticleTextures,
    int CachedUnownedParticleTextures,
    long CachedUnownedParticleTextureBytes,
    int CachedCompositeTextures,
    int CachedUnownedComposites,
    long CachedUnownedCompositeBytes,
    int CompositeAtlases,
    long CompositeAtlasBytes,
    int FrameCompositeUploadCount,
    long FrameCompositeUploadBytes,
    int CompositeWarmupPending);

internal readonly record struct ProcessResourceDiagnostics(
    long ManagedBytes,
    long ManagedCommittedBytes,
    long TrackedGpuBytes,
    int TrackedGpuBuffers,
    int TrackedGpuTextures);

internal readonly record struct RenderFrameResourceDiagnosticsSnapshot(
    VfxStreamResourceDiagnostics Vfx,
    DynamicBufferResourceDiagnostics DynamicBuffers,
    MeshStreamResourceDiagnostics Mesh,
    TextureStreamResourceDiagnostics Textures,
    ProcessResourceDiagnostics Process);

internal sealed class RenderFrameDiagnosticsController :
    IRenderFrameDiagnosticsPhase,
    IRenderFrameDiagnosticsSnapshotSource
{
    internal const double PublicationIntervalSeconds = 0.5;

    private readonly IRenderFrameTitleFactsSource _titleFacts;
    private readonly IRenderFrameTitleSink _titleSink;
    private readonly IRenderFrameResourceDiagnosticsSource? _resources;
    private readonly IRenderFrameDiagnosticLog _log;
    private readonly bool _publishResourceDiagnostics;
    private readonly IRenderPackDiagnosticsSnapshotSource? _renderPack;

    private double _elapsedSeconds;
    private int _frameCount;

    public RenderFrameDiagnosticsSnapshot Snapshot { get; private set; } =
        RenderFrameDiagnosticsSnapshot.Initial;

    public RenderFrameDiagnosticsController(
        IRenderFrameTitleFactsSource titleFacts,
        IRenderFrameTitleSink titleSink,
        IRenderFrameDiagnosticLog log,
        bool publishResourceDiagnostics,
        IRenderFrameResourceDiagnosticsSource? resources = null,
        IRenderPackDiagnosticsSnapshotSource? renderPack = null)
    {
        _titleFacts = titleFacts ?? throw new ArgumentNullException(nameof(titleFacts));
        _titleSink = titleSink ?? throw new ArgumentNullException(nameof(titleSink));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _publishResourceDiagnostics = publishResourceDiagnostics;
        _resources = publishResourceDiagnostics
            ? resources ?? throw new ArgumentNullException(nameof(resources))
            : resources;
        _renderPack = renderPack;
    }

    public void Publish(RenderFrameInput input, RenderFrameOutcome outcome)
    {
        _elapsedSeconds += input.DeltaSeconds;
        _frameCount++;
        if (_elapsedSeconds < PublicationIntervalSeconds)
            return;

        double averageFrameMilliseconds = _elapsedSeconds / _frameCount * 1000.0;
        double fps = _frameCount / _elapsedSeconds;
        RenderFrameTitleFacts facts = _titleFacts.Capture();

        _titleSink.SetTitle(FormatTitle(
            fps,
            averageFrameMilliseconds,
            outcome.World.VisibleLandblocks,
            outcome.World.TotalLandblocks,
            facts));

        if (_publishResourceDiagnostics)
        {
            RenderFrameResourceDiagnosticsSnapshot resources = _resources!.Capture();
            _log.WriteLine(FormatGpuStream(resources));
            if (_renderPack is not null)
            {
                _log.WriteLine(RenderPackDiagnosticsFormatter.Format(
                    _renderPack.CaptureDiagnostics()));
            }
        }

        Snapshot = new RenderFrameDiagnosticsSnapshot(
            fps,
            averageFrameMilliseconds,
            outcome.World.VisibleLandblocks,
            outcome.World.TotalLandblocks,
            facts.EntityCount,
            facts.AnimatedEntityCount);
        _elapsedSeconds = 0;
        _frameCount = 0;
    }

    internal static string FormatTitle(
        double fps,
        double averageFrameMilliseconds,
        int visibleLandblocks,
        int totalLandblocks,
        RenderFrameTitleFacts facts) =>
        $"acdream | {fps:F0} fps | {averageFrameMilliseconds:F1} ms | "
        + $"lb {visibleLandblocks}/{totalLandblocks} | "
        + $"ent {facts.EntityCount}/anim {facts.AnimatedEntityCount} | "
        + $"PY{facts.Calendar.Year} {facts.Calendar.Month} {facts.Calendar.Day} "
        + $"{facts.Calendar.Hour} (df={facts.DayFraction:F4})";

    internal static string FormatGpuStream(RenderFrameResourceDiagnosticsSnapshot snapshot)
    {
        VfxStreamResourceDiagnostics vfx = snapshot.Vfx;
        DynamicBufferResourceDiagnostics buffers = snapshot.DynamicBuffers;
        MeshStreamResourceDiagnostics mesh = snapshot.Mesh;
        TextureStreamResourceDiagnostics textures = snapshot.Textures;
        ProcessResourceDiagnostics process = snapshot.Process;
        return
            $"[gpu-stream] emit={vfx.ActiveEmitters} "
            + $"particles={vfx.ActiveParticles} "
            + $"bindings={vfx.ActiveBindings} "
            + $"wbSets={buffers.WorldBufferSets} "
            + $"cellSets={buffers.EnvCellBufferSets} "
            + $"particleSets={buffers.ParticleBufferSets}/{buffers.ParticleBufferBytes}B "
            + $"uiBytes={buffers.UiTextBufferBytes} "
            + $"portalBytes={buffers.PortalDepthMaskBufferBytes} "
            + $"clipSets={buffers.ClipBufferSets} "
            + $"terrainBuffers={buffers.TerrainIndirectBuffers} "
            + $"lightBuffers={buffers.SceneLightingBuffers} "
            + $"mesh={mesh.RenderData}/{mesh.AtlasArrays}/{mesh.UnusedLru} "
            + $"meshEst={mesh.EstimatedBytes} "
            + $"meshUpload={mesh.GlobalUploadCount}/{mesh.GlobalUploadedBytes} "
            + $"uploadFrame={mesh.FrameUploadCount}/{mesh.FrameUploadBytes}/"
            + $"{mesh.FrameArrayAllocationBytes}/{mesh.FramePlannedMipmapBytes}/"
            + $"{mesh.FrameNewArrayCount} "
            + $"bufferFrame={mesh.FrameBufferUploadBytes}/{mesh.FrameBufferAllocationBytes}/"
            + $"{mesh.FrameBufferCopyBytes}/{mesh.FrameNewBufferCount} "
            + $"staging={mesh.StagedUploadBacklog}/{mesh.StagedUploadBytes}/"
            + $"{mesh.StagingAtHighWater}/{mesh.FrameStaleDiscardCount} "
            + $"cpuMesh={mesh.CpuMeshCacheCount}/{mesh.CpuMeshCacheBytes} "
            + $"mipmapFrame={mesh.FrameMipmapArrayCount}/{mesh.FrameMipmapBytes} "
            + $"meshCap={mesh.GlobalCapacityBytes} "
            + $"meshPhys={mesh.GlobalPhysicalCapacityBytes}/{mesh.GlobalMigrationInProgress} "
            + $"prepared={mesh.PreparedProbes}/{mesh.PreparedReads}/"
            + $"{mesh.PreparedLoaded}/{mesh.PreparedMissing}/{mesh.PreparedCorrupt} "
            + $"gpuTrack={process.TrackedGpuBytes}/{process.TrackedGpuBuffers}/"
            + $"{process.TrackedGpuTextures} "
            + $"managed={process.ManagedBytes}/{process.ManagedCommittedBytes} "
            + $"ownedTextures={textures.OwnedBindlessTextures}/{textures.TextureOwners} "
            + $"particleTextures={textures.ActiveParticleTextures}/"
            + $"{textures.ParticleTextureOwners}/{textures.CachedParticleTextures}/"
            + $"{textures.CachedUnownedParticleTextures}/"
            + $"{textures.CachedUnownedParticleTextureBytes} "
            + $"compositeCache={textures.CachedCompositeTextures}/"
            + $"{textures.CachedUnownedComposites}/{textures.CachedUnownedCompositeBytes} "
            + $"compositeAtlas={textures.CompositeAtlases}/{textures.CompositeAtlasBytes} "
            + $"compositeUpload={textures.FrameCompositeUploadCount}/"
            + $"{textures.FrameCompositeUploadBytes}/{textures.CompositeWarmupPending}";
    }
}
