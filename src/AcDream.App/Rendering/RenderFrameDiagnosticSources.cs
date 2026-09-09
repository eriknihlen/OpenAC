using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

internal sealed class RuntimeRenderFrameTitleFactsSource : IRenderFrameTitleFactsSource
{
    private readonly GpuWorldState _world;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animations;
    private readonly WorldTimeService _time;

    public RuntimeRenderFrameTitleFactsSource(
        GpuWorldState world,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        WorldTimeService time)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public RenderFrameTitleFacts Capture() => new(
        _world.Entities.Count,
        _animations.Count,
        _time.CurrentCalendar,
        _time.DayFraction);
}

internal sealed class SilkRenderFrameTitleSink : IRenderFrameTitleSink
{
    private readonly IWindow _window;

    public SilkRenderFrameTitleSink(IWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    public void SetTitle(string title) => _window.Title = title;
}

internal sealed class RuntimeRenderFrameResourceDiagnosticsSource :
    IRenderFrameResourceDiagnosticsSource
{
    private readonly ParticleSystem? _particles;
    private readonly ParticleHookSink? _particleBindings;
    private readonly WbDrawDispatcher? _worldDispatcher;
    private readonly Wb.EnvCellRenderer? _environmentCells;
    private readonly ParticleRenderer? _particleRenderer;
    private readonly TextRenderer? _uiTextRenderer;
    private readonly PortalDepthMaskRenderer? _portalDepthMask;
    private readonly ClipFrame? _clipFrame;
    private readonly TerrainModernRenderer? _terrain;
    private readonly SceneLightingUboBinding? _lighting;
    private readonly WbMeshAdapter? _meshes;
    private readonly TextureCache? _textures;
    private readonly IPreparedAssetSource? _preparedAssets;

    public RuntimeRenderFrameResourceDiagnosticsSource(
        ParticleSystem? particles,
        ParticleHookSink? particleBindings,
        WbDrawDispatcher? worldDispatcher,
        Wb.EnvCellRenderer? environmentCells,
        ParticleRenderer? particleRenderer,
        TextRenderer? uiTextRenderer,
        PortalDepthMaskRenderer? portalDepthMask,
        ClipFrame? clipFrame,
        TerrainModernRenderer? terrain,
        SceneLightingUboBinding? lighting,
        WbMeshAdapter? meshes,
        TextureCache? textures,
        IPreparedAssetSource? preparedAssets)
    {
        _particles = particles;
        _particleBindings = particleBindings;
        _worldDispatcher = worldDispatcher;
        _environmentCells = environmentCells;
        _particleRenderer = particleRenderer;
        _uiTextRenderer = uiTextRenderer;
        _portalDepthMask = portalDepthMask;
        _clipFrame = clipFrame;
        _terrain = terrain;
        _lighting = lighting;
        _meshes = meshes;
        _textures = textures;
        _preparedAssets = preparedAssets;
    }

    public RenderFrameResourceDiagnosticsSnapshot Capture()
    {
        (int particleSets, long particleBytes) =
            _particleRenderer?.DynamicBufferDiagnostics ?? default;
        var meshManager = _meshes?.MeshManager;
        var mesh = meshManager?.Diagnostics ?? default;
        var globalMesh = meshManager?.GlobalBuffer;
        var cpuMesh = _meshes?.CpuMeshCacheDiagnostics ?? default;
        PreparedAssetSourceStats prepared =
            _preparedAssets?.Stats ?? default;
        GCMemoryInfo gc = GC.GetGCMemoryInfo();

        return new RenderFrameResourceDiagnosticsSnapshot(
            new VfxStreamResourceDiagnostics(
                _particles?.ActiveEmitterCount ?? 0,
                _particles?.ActiveParticleCount ?? 0,
                _particleBindings?.ActiveBindingCount ?? 0),
            new DynamicBufferResourceDiagnostics(
                _worldDispatcher?.DynamicBufferSetCount ?? 0,
                _environmentCells?.DynamicBufferSetCount ?? 0,
                particleSets,
                particleBytes,
                _uiTextRenderer?.DynamicBufferCapacityBytes ?? 0,
                _portalDepthMask?.DynamicBufferCapacityBytes ?? 0,
                _clipFrame?.DynamicBufferSetCount ?? 0,
                _terrain?.DynamicIndirectBufferCount ?? 0,
                _lighting?.DynamicBufferCount ?? 0),
            new MeshStreamResourceDiagnostics(
                mesh.RenderData,
                mesh.AtlasArrays,
                mesh.UnusedLru,
                mesh.EstimatedBytes,
                globalMesh?.UploadCount ?? 0,
                globalMesh?.UploadedBytes ?? 0,
                _meshes?.LastUploadCount ?? 0,
                _meshes?.LastUploadBytes ?? 0,
                _meshes?.LastArrayAllocationBytes ?? 0,
                _meshes?.LastPlannedMipmapBytes ?? 0,
                _meshes?.LastNewArrayCount ?? 0,
                _meshes?.LastBufferUploadBytes ?? 0,
                _meshes?.LastBufferAllocationBytes ?? 0,
                _meshes?.LastBufferCopyBytes ?? 0,
                _meshes?.LastNewBufferCount ?? 0,
                _meshes?.StagedUploadBacklog ?? 0,
                _meshes?.StagedUploadBytes ?? 0,
                _meshes?.StagingAtHighWater ?? false,
                _meshes?.LastStaleDiscardCount ?? 0,
                cpuMesh.Count,
                cpuMesh.Bytes,
                _meshes?.LastMipmapArrayCount ?? 0,
                _meshes?.LastMipmapBytes ?? 0,
                globalMesh?.CapacityBytes ?? 0,
                globalMesh?.PhysicalCapacityBytes ?? 0,
                globalMesh?.IsMigrationInProgress ?? false,
                prepared.Probes,
                prepared.Reads,
                prepared.Loaded,
                prepared.Missing,
                prepared.Corrupt),
            new TextureStreamResourceDiagnostics(
                _textures?.OwnedBindlessTextureCount ?? 0,
                _textures?.TextureOwnerCount ?? 0,
                _textures?.ActiveParticleTextureCount ?? 0,
                _textures?.ParticleTextureOwnerCount ?? 0,
                _textures?.CachedParticleTextureCount ?? 0,
                _textures?.CachedUnownedParticleTextureCount ?? 0,
                _textures?.CachedUnownedParticleTextureBytes ?? 0,
                _textures?.CachedCompositeTextureCount ?? 0,
                _textures?.CachedUnownedCompositeCount ?? 0,
                _textures?.CachedUnownedCompositeBytes ?? 0,
                _textures?.CompositeAtlasCount ?? 0,
                _textures?.CompositeAtlasBytes ?? 0,
                _textures?.CompositeFrameUploadCount ?? 0,
                _textures?.CompositeFrameUploadBytes ?? 0,
                _worldDispatcher?.LastCompositeWarmupPendingCount ?? 0),
            new ProcessResourceDiagnostics(
                GC.GetTotalMemory(false),
                gc.TotalCommittedBytes,
                GpuMemoryTracker.AllocatedBytes,
                GpuMemoryTracker.BufferCount,
                GpuMemoryTracker.TextureCount));
    }
}
