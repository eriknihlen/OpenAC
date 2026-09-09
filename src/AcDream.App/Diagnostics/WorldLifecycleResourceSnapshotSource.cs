using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Residency;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using DatReaderWriter.Lib.IO;

namespace AcDream.App.Diagnostics;

internal sealed class WorldLifecycleResourceSnapshotSource
{
    // Generation index 3 is the large object heap; see GCMemoryInfo.GenerationInfo
    // (verified against the installed net10.0 runtime — Gen0/1/2, LOH, POH).
    private const int LohGenerationIndex = 3;

    private readonly GpuWorldState _world;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState>
        _animations;
    private readonly RenderFrameDiagnosticsController _renderDiagnostics;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly StreamingController _streaming;
    private readonly ParticleSystem _particles;
    private readonly ParticleHookSink _particleSink;
    private readonly EntityEffectController _effects;
    private readonly LiveEntityLightController _lights;
    private readonly PhysicsScriptRunner _scripts;
    private readonly WbMeshAdapter? _meshes;
    private readonly TextureCache _textures;
    private readonly WbDrawDispatcher? _dispatcher;
    private readonly FrameProfiler _frameProfiler;
    private readonly IDatReaderWriter _dats;
    private readonly ResidencyManager _residency;
    private readonly PhysicsDataCache _physics;
    private readonly ICurrentRenderSceneOracleSnapshotSource?
        _renderSceneOracle;
    private readonly IRenderSceneShadowSnapshotSource?
        _renderSceneShadow;

    public WorldLifecycleResourceSnapshotSource(
        GpuWorldState world,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        RenderFrameDiagnosticsController renderDiagnostics,
        LiveEntityRuntime liveEntities,
        StreamingController streaming,
        ParticleSystem particles,
        ParticleHookSink particleSink,
        EntityEffectController effects,
        LiveEntityLightController lights,
        PhysicsScriptRunner scripts,
        WbMeshAdapter? meshes,
        TextureCache textures,
        WbDrawDispatcher? dispatcher,
        FrameProfiler frameProfiler,
        IDatReaderWriter dats,
        ResidencyManager residency,
        PhysicsDataCache physics,
        ICurrentRenderSceneOracleSnapshotSource? renderSceneOracle = null,
        IRenderSceneShadowSnapshotSource? renderSceneShadow = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _animations = animations
            ?? throw new ArgumentNullException(nameof(animations));
        _renderDiagnostics = renderDiagnostics
            ?? throw new ArgumentNullException(nameof(renderDiagnostics));
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
        _streaming = streaming
            ?? throw new ArgumentNullException(nameof(streaming));
        _particles = particles
            ?? throw new ArgumentNullException(nameof(particles));
        _particleSink = particleSink
            ?? throw new ArgumentNullException(nameof(particleSink));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        _meshes = meshes;
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _dispatcher = dispatcher;
        _frameProfiler = frameProfiler
            ?? throw new ArgumentNullException(nameof(frameProfiler));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _residency = residency
            ?? throw new ArgumentNullException(nameof(residency));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _renderSceneOracle = renderSceneOracle;
        _renderSceneShadow = renderSceneShadow;
    }

    public WorldLifecycleResourceSnapshot Capture(RenderFrameOutcome outcome)
    {
        ObjectMeshManager? meshManager = _meshes is null
            ? null
            : _meshes.MeshManager
                ?? throw new InvalidOperationException(
                    "Lifecycle snapshots require the composed modern mesh manager.");
        var mesh = meshManager?.Diagnostics ?? default;
        RenderFrameDiagnosticsSnapshot render = _renderDiagnostics.Snapshot;
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> generations = memory.GenerationInfo;
        long lohSizeBytes = generations.Length > LohGenerationIndex
            ? generations[LohGenerationIndex].SizeAfterBytes
            : 0L;
        long lohFragmentationBytes = generations.Length > LohGenerationIndex
            ? generations[LohGenerationIndex].FragmentationAfterBytes
            : 0L;

        CacheStats datObjectCacheStats = _dats is RuntimeDatCollection runtimeDats
            ? runtimeDats.ObjectCacheStats
            : default;
        CacheStats cpuMeshCacheStats = meshManager?.CpuMeshCacheStats ?? default;
        CacheStats decodedTextureCacheStats =
            meshManager?.DecodedTextureCacheStats ?? default;
        ResidencySnapshot residency = _residency.CaptureSnapshot();
        long trackedGpuBytes = GpuMemoryTracker.AllocatedBytes;
        long residencyGpuBytes = residency.TotalCharges.PhysicalGpuBytes;

        return new WorldLifecycleResourceSnapshot(
            LoadedLandblocks: _world.LoadedLandblockIds.Count,
            WorldEntities: _world.Entities.Count,
            AnimatedEntities: _animations.Count,
            VisibleLandblocks: outcome.World.VisibleLandblocks,
            TotalLandblocks: outcome.World.TotalLandblocks,
            LiveEntities: _liveEntities.Count,
            MaterializedLiveEntities: _liveEntities.MaterializedCount,
            RenderSceneOracle: _renderSceneOracle?.Snapshot
                ?? CurrentRenderSceneOracleSnapshot.Disabled,
            RenderSceneShadow:
                _renderSceneShadow?.CaptureCheckpointSnapshot()
                ?? RenderSceneShadowComparisonSnapshot.Disabled,
            RenderFrameProduct: RenderFrameProductComparisonSnapshot.Disabled,
            PendingLiveTeardowns: _liveEntities.PendingTeardownCount,
            PendingLandblockRetirements: _streaming.PendingRetirementCount,
            ParticleEmitters: _particles.ActiveEmitterCount,
            Particles: _particles.ActiveParticleCount,
            ParticleBindings: _particleSink.ActiveBindingCount,
            ParticleOwners: _particleSink.TrackedOwnerCount,
            EffectOwners: _effects.ReadyOwnerCount,
            LightOwners: _lights.TrackedOwnerCount,
            ScriptOwners: _scripts.ActiveOwnerCount,
            ActiveScripts: _scripts.ActiveScriptCount,
            MeshRenderData: mesh.RenderData,
            MeshAtlasArrays: mesh.AtlasArrays,
            MeshEstimatedBytes: mesh.EstimatedBytes,
            StagedMeshUploads: _meshes?.StagedUploadBacklog ?? 0,
            StagedMeshBytes: _meshes?.StagedUploadBytes ?? 0,
            TrackedGpuBytes: trackedGpuBytes,
            ResidencyGpuBytes: residencyGpuBytes,
            GpuTrackerMinusResidencyBytes: checked(
                trackedGpuBytes - residencyGpuBytes),
            TrackedGpuBuffers: GpuMemoryTracker.BufferCount,
            TrackedGpuTextures: GpuMemoryTracker.TextureCount,
            OwnedCompositeTextures: _textures.OwnedBindlessTextureCount,
            CompositeTextureOwners: _textures.TextureOwnerCount,
            ActiveParticleTextures: _textures.ActiveParticleTextureCount,
            ParticleTextureOwners: _textures.ParticleTextureOwnerCount,
            CompositeWarmupPending:
                _dispatcher?.LastCompositeWarmupPendingCount ?? 0,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false),
            ManagedCommittedBytes: memory.TotalCommittedBytes,
            LohSizeBytes: lohSizeBytes,
            LohFragmentationBytes: lohFragmentationBytes,
            ProcessTotalAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            CpuMeshCacheHits: cpuMeshCacheStats.Hits,
            CpuMeshCacheMisses: cpuMeshCacheStats.Misses,
            CpuMeshCacheEvictions: cpuMeshCacheStats.Evictions,
            DecodedTextureCacheHits: decodedTextureCacheStats.Hits,
            DecodedTextureCacheMisses: decodedTextureCacheStats.Misses,
            DecodedTextureCacheEvictions: decodedTextureCacheStats.Evictions,
            DatObjectCacheHits: datObjectCacheStats.Hits,
            DatObjectCacheMisses: datObjectCacheStats.Misses,
            DatObjectCacheEvictions: datObjectCacheStats.Evictions,
            PhysicsGraphGfxObjs: _physics.GraphGfxObjCount,
            PhysicsGraphSetups: _physics.GraphSetupCount,
            PhysicsGraphCells: _physics.GraphCellStructCount,
            PhysicsFlatGfxObjs: _physics.FlatGfxObjCount,
            PhysicsFlatSetups: _physics.FlatSetupCount,
            PhysicsFlatCells: _physics.FlatCellStructCount,
            PhysicsFlatEnvCells: _physics.FlatEnvCellCount,
            CollisionShadow: _physics.CollisionShadowStats,
            StreamingWork: _streaming.WorkDiagnostics,
            Residency: residency,
            Fps: render.Fps,
            FrameMilliseconds: render.FrameMilliseconds,
            LastFrameProfile: _frameProfiler.LastReport);
    }

}
