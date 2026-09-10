using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.Rendering;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using AcDream.App.Rendering.Selection;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

public sealed partial class WbDrawDispatcher : IDisposable
{
    public enum EntitySet
    {
        /// <summary>Every entity walked, gated only by the existing
        /// <c>ParentCellId ∈ visibleCellIds</c> filter.</summary>
        All,
    }

    private readonly TextureCache _textures;
    private readonly WbMeshAdapter _meshAdapter;
    private readonly EntitySpawnAdapter _entitySpawnAdapter;
    private readonly IRetailSelectionRenderSink? _selectionSink;
    private readonly IRetailSelectionLightingSource? _selectionLighting;
    private readonly RetailAlphaQueue? _alphaQueue;
    private readonly Func<uint, float>? _hierarchicalTranslucency;
    private readonly AlphaDrawSource _alphaSource;
    private readonly RetainedScratchCapacityPolicy _alphaScratchPolicy;
    private int _scratchPeakUnits;

    private ICurrentRenderDispatcherObserver? _currentRenderSceneObserver;

    public readonly record struct DrawStats(
        EntitySet Set,
        int EntitiesWalked,
        int MeshRefs,
        int Instances,
        int Draws,
        int CullRuns,
        int OpaqueDraws,
        int TransparentDraws,
        long Triangles);

    public DrawStats LastDrawStats { get; private set; }
    public bool CompositeTexturesReady { get; private set; } = true;
    internal int LastCompositeWarmupPendingCount { get; private set; }
    internal const int MaximumCompositeWarmupScanEntitiesPerFrame = 4096;
    internal const int MaximumCompositeWarmupPrepareEntitiesPerFrame = 128;
    private readonly Queue<WorldEntity> _compositeWarmupQueue = new();
    private readonly HashSet<WorldEntity> _compositeWarmupTracked = [];
    private IReadOnlyList<WorldEntity>? _compositeWarmupSource;
    private ulong _compositeWarmupSourceGeneration;
    private uint _compositeWarmupDestinationCell;
    private int _compositeWarmupRadius;
    private int _compositeWarmupScanIndex;
    private bool _compositeWarmupScanComplete = true;

    private enum CompositeWarmupResult : byte
    {
        Complete,
        Pending,
        UploadBudgetBlocked,
    }

    public void InvalidateCompositeWarmupReadiness()
    {
        CompositeTexturesReady = false;
        LastCompositeWarmupPendingCount = 1;
        _compositeWarmupQueue.Clear();
        _compositeWarmupTracked.Clear();
        _compositeWarmupSource = null;
        _compositeWarmupSourceGeneration = 0;
        _compositeWarmupDestinationCell = 0;
        _compositeWarmupRadius = 0;
        _compositeWarmupScanIndex = 0;
        _compositeWarmupScanComplete = true;
    }

    public void PrepareCompositeTextures(
        IReadOnlyList<WorldEntity> entities,
        ulong entityGeneration,
        uint destinationCell,
        int radius)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        if (RequiresCompositeWarmupRebuild(
                _compositeWarmupSource,
                _compositeWarmupDestinationCell,
                _compositeWarmupRadius,
                entities,
                destinationCell,
                radius))
        {
            RebuildCompositeWarmupQueue(
                entities,
                entityGeneration,
                destinationCell,
                radius);
        }
        else if (ShouldBeginCompositeWarmupRescan(
                     _compositeWarmupScanComplete,
                     _compositeWarmupSourceGeneration,
                     entityGeneration))
        {
            BeginCompositeWarmupRescan(entities.Count, entityGeneration);
        }
        if (CompositeTexturesReady)
            return;

        _compositeWarmupScanIndex =
            Math.Min(_compositeWarmupScanIndex, entities.Count);
        int scanEnd = CompositeWarmupScanEnd(
            _compositeWarmupScanIndex,
            entities.Count);
        for (; _compositeWarmupScanIndex < scanEnd; _compositeWarmupScanIndex++)
        {
            WorldEntity entity = entities[_compositeWarmupScanIndex];
            if (IsCompositeWarmupCandidate(entity, destinationCell, radius)
                && _compositeWarmupTracked.Add(entity))
            {
                _compositeWarmupQueue.Enqueue(entity);
            }
        }
        _compositeWarmupScanComplete = _compositeWarmupScanIndex == entities.Count;
        if (ShouldBeginCompositeWarmupRescan(
                _compositeWarmupScanComplete,
                _compositeWarmupSourceGeneration,
                entityGeneration))
        {
            BeginCompositeWarmupRescan(entities.Count, entityGeneration);
        }

        int candidatesThisPass = Math.Min(
            _compositeWarmupQueue.Count,
            MaximumCompositeWarmupPrepareEntitiesPerFrame);
        for (int i = 0; i < candidatesThisPass; i++)
        {
            WorldEntity entity = _compositeWarmupQueue.Dequeue();
            CompositeWarmupResult result = PrepareCompositeEntity(entity);
            if (result != CompositeWarmupResult.Complete)
                _compositeWarmupQueue.Enqueue(entity);
            if (result == CompositeWarmupResult.UploadBudgetBlocked)
                break;
        }

        LastCompositeWarmupPendingCount = _compositeWarmupQueue.Count
            + (_compositeWarmupScanComplete ? 0 : entities.Count - _compositeWarmupScanIndex);
        CompositeTexturesReady = _compositeWarmupScanComplete
            && _compositeWarmupQueue.Count == 0;
        if (!CompositeTexturesReady
            && AcDream.Core.Net.NetDiagnostics.ProbeReveal)
        {
            ProbeRevealWarmupStall();
        }
    }

    private long _probeWarmupLastEmitTs;

    private void ProbeRevealWarmupStall()
    {
        long now = Stopwatch.GetTimestamp();
        if (_probeWarmupLastEmitTs != 0
            && now - _probeWarmupLastEmitTs < Stopwatch.Frequency)
        {
            return;
        }
        _probeWarmupLastEmitTs = now;

        var pendingIds = new System.Text.StringBuilder();
        int listed = 0;
        foreach (WorldEntity entity in _compositeWarmupQueue)
        {
            if (listed >= 4)
                break;
            ulong gfxObjId = entity.MeshRefs.Count > 0
                ? entity.MeshRefs[0].GfxObjId
                : 0;
            if (listed > 0)
                pendingIds.Append(',');
            pendingIds.Append($"0x{gfxObjId:X8}");
            listed++;
        }
        Console.WriteLine(
            $"[composite-warmup] STALL pending={LastCompositeWarmupPendingCount}"
            + $" queue={_compositeWarmupQueue.Count}"
            + $" scanComplete={_compositeWarmupScanComplete}"
            + $" uploadOpen={_textures.CanStartCompositeUpload}"
            + $" firstPending=[{pendingIds}]");
    }

    internal static bool RequiresCompositeWarmupRebuild(
        IReadOnlyList<WorldEntity>? currentSource,
        uint currentDestinationCell,
        int currentRadius,
        IReadOnlyList<WorldEntity> nextSource,
        uint nextDestinationCell,
        int nextRadius) =>
        !ReferenceEquals(currentSource, nextSource)
        || currentDestinationCell != nextDestinationCell
        || currentRadius != nextRadius;

    internal static bool ShouldBeginCompositeWarmupRescan(
        bool scanComplete,
        ulong scanGeneration,
        ulong entityGeneration) =>
        scanComplete && scanGeneration != entityGeneration;

    internal static int CompositeWarmupScanEnd(
        int scanIndex,
        int entityCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scanIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(entityCount);
        return Math.Min(
            entityCount,
            scanIndex + MaximumCompositeWarmupScanEntitiesPerFrame);
    }

    private void RebuildCompositeWarmupQueue(
        IReadOnlyList<WorldEntity> entities,
        ulong entityGeneration,
        uint destinationCell,
        int radius)
    {
        _compositeWarmupQueue.Clear();
        _compositeWarmupTracked.Clear();
        _compositeWarmupSource = entities;
        _compositeWarmupSourceGeneration = entityGeneration;
        _compositeWarmupDestinationCell = destinationCell;
        _compositeWarmupRadius = radius;
        _compositeWarmupScanIndex = 0;
        _compositeWarmupScanComplete = entities.Count == 0;
        LastCompositeWarmupPendingCount = entities.Count;
        CompositeTexturesReady = _compositeWarmupScanComplete;
    }

    private void BeginCompositeWarmupRescan(
        int entityCount,
        ulong entityGeneration)
    {
        _compositeWarmupSourceGeneration = entityGeneration;
        _compositeWarmupScanIndex = 0;
        _compositeWarmupScanComplete = entityCount == 0;
        CompositeTexturesReady =
            _compositeWarmupScanComplete && _compositeWarmupQueue.Count == 0;
    }

    internal static bool IsCompositeWarmupCandidate(
        WorldEntity entity,
        uint destinationCell,
        int radius)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentOutOfRangeException.ThrowIfNegative(radius);

        if (destinationCell != 0)
        {
            if (!TryGetEntityCell(entity, out uint entityCell)
                || !IsWithinLandblockRadius(entityCell, destinationCell, radius))
            {
                return false;
            }
        }

        if (entity.PaletteOverride is not null)
            return true;

        for (int meshIndex = 0; meshIndex < entity.MeshRefs.Count; meshIndex++)
        {
            if (entity.MeshRefs[meshIndex].SurfaceOverrides is { Count: > 0 })
                return true;
        }

        return false;
    }

    private CompositeWarmupResult PrepareCompositeEntity(WorldEntity entity)
    {
        bool pending = false;
        PaletteCompositeIdentity paletteIdentity = entity.PaletteOverride is not null
            ? TextureCache.GetPaletteIdentity(entity.PaletteOverride)
            : default;
        for (int meshIndex = 0; meshIndex < entity.MeshRefs.Count; meshIndex++)
        {
            MeshRef meshRef = entity.MeshRefs[meshIndex];
            ObjectRenderData? renderData = _meshAdapter.TryGetRenderData(meshRef.GfxObjId);
            if (renderData is null)
            {
                _meshAdapter.EnsureLoaded(meshRef.GfxObjId);
                pending = true;
                continue;
            }

            if (renderData.IsSetup && renderData.SetupParts.Count > 0)
            {
                for (int partIndex = 0; partIndex < renderData.SetupParts.Count; partIndex++)
                {
                    ulong partId = renderData.SetupParts[partIndex].GfxObjId;
                    ObjectRenderData? partData = _meshAdapter.TryGetRenderData(partId);
                    if (partData is null)
                    {
                        _meshAdapter.EnsureLoaded(partId);
                        pending = true;
                        continue;
                    }
                    if (!PrepareCompositeBatches(entity, meshRef, partData, paletteIdentity))
                        pending = true;
                    if (!_textures.CanStartCompositeUpload && pending)
                        return CompositeWarmupResult.UploadBudgetBlocked;
                }
            }
            else
            {
                if (!PrepareCompositeBatches(entity, meshRef, renderData, paletteIdentity))
                    pending = true;
                if (!_textures.CanStartCompositeUpload && pending)
                    return CompositeWarmupResult.UploadBudgetBlocked;
            }
        }
        return pending ? CompositeWarmupResult.Pending : CompositeWarmupResult.Complete;
    }

    private bool PrepareCompositeBatches(
        WorldEntity entity,
        MeshRef meshRef,
        ObjectRenderData renderData,
        PaletteCompositeIdentity paletteIdentity)
    {
        bool complete = true;
        for (int batchIndex = 0; batchIndex < renderData.Batches.Count; batchIndex++)
        {
            _ = ResolveTexture(
                entity,
                meshRef,
                renderData.Batches[batchIndex],
                paletteIdentity,
                out bool compositePending);
            if (compositePending)
                complete = false;
            if (compositePending && !_textures.CanStartCompositeUpload)
                break;
        }
        return complete;
    }

    private static bool TryGetEntityCell(WorldEntity entity, out uint cell)
    {
        if (entity.VisibilityCellId is uint resolved)
        {
            cell = resolved;
            return true;
        }
        cell = 0;
        return false;
    }

    private static bool IsWithinLandblockRadius(uint cell, uint center, int radius)
    {
        int x = (int)(cell >> 24);
        int y = (int)((cell >> 16) & 0xFFu);
        int centerX = (int)(center >> 24);
        int centerY = (int)((center >> 16) & 0xFFu);
        return Math.Abs(x - centerX) <= radius && Math.Abs(y - centerY) <= radius;
    }

    private readonly EntityClassificationCache _cache;

    private readonly AcDream.Core.Rendering.TranslucencyFadeManager _translucencyFades;

    private readonly bool _tier1CacheDisabled =
        string.Equals(Environment.GetEnvironmentVariable("ACDREAM_DISABLE_TIER1_CACHE"), "1", StringComparison.Ordinal);

    public bool AlphaToCoverage { get; set; } = true;

    public IReadOnlySet<uint> FoliageWindExclusions { get; set; } =
        System.Collections.Frozen.FrozenSet<uint>.Empty;

    private uint[] _clipSlotData = new uint[256];

    private int[] _lightSetData = new int[256 * LightManager.MaxLightsPerObject];
    private float[] _globalLightData = new float[GlobalLightPacker.FloatsPerLight * 16];   // 16 floats (4 vec4) per GlobalLight

    private uint[] _indoorData = new uint[256];

    private uint[] _detailCategoryData = new uint[256];

    private float[] _alphaData = new float[256];

    private bool _dynamicFrameStarted;

    internal int DynamicBufferSetCount => 0;

    private Vector2[] _selectionLightingData = new Vector2[256];
    // This frame's point-light snapshot, handed in by GameWindow before Draw via
    // SetSceneLights. Null/empty ⇒ only ambient + sun render (all instance sets -1).
    private IReadOnlyList<LightSource>? _pointSnapshot;
    private readonly int[] _currentEntityLightSetScratch = new int[LightManager.MaxLightsPerObject];
    private InstanceLightSet _currentEntityLightSet = InstanceLightSet.Disabled;

    private bool _currentEntityIndoor;
    private bool _currentEntityBuildingDetail;
    private Vector2 _currentEntitySelectionLighting = new(0f, 1f);

    private uint _sharedClipRegionSsbo;


    private uint _currentEntitySlot;

    private bool _currentEntityCulled;

    // Per-frame scratch arrays — Tasks 9-10 fully wire these.
    private float[] _instanceData = new float[256 * 16];      // mat4 floats per instance
    private BatchData[] _batchData = new BatchData[256];
    private DrawElementsIndirectCommand[] _indirectCommands = new DrawElementsIndirectCommand[256];
    private CullMode[] _drawCullModes = new CullMode[256];

    private CullMode[] _orderedDrawCullModes = new CullMode[256];
    private BatchDataPublic[] _batchPublicScratch = new BatchDataPublic[256];
    private readonly List<IndirectGroupInput> _groupInputScratch = new(256);
    private readonly List<GroupKey> _retiredGroupKeys = new();
    private long _nextGroupRegistration = 1;
    private long _groupFrame;

    private int _opaqueDrawCount;
    private int _transparentDrawCount;
    private int _transparentByteOffset;

    // std430 layout: uint TextureIndex at offset 0, float SurfaceOpacity at
    // offset 4, uint TextureLayer at offset 8,
    // uint Flags at offset 12. Total 16 bytes — unchanged from before V2, so
    // every existing CPU writer's offsets are unchanged (see
    // GpuBindingModel.GpuBatchDataStrideBytes). TextureIndex used to be a
    // 64-bit ulong TextureHandle (an ARB_bindless_texture handle, uvec2 in
    // GLSL); it is now a slot into the device texture table
    // (mesh_modern.vert's BatchData.textureIndex / ACDREAM_TEXTURE_HANDLE),
    // which is why the struct only needs 4-byte (not 8-byte) packing now.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BatchData
    {
        public uint TextureIndex;   // slot into the device texture table
        public float SurfaceOpacity;
        public uint TextureLayer;
        public uint Flags;
    }

    private readonly record struct DeferredAlphaInstance(
        GroupKey Key,
        Matrix4x4 Model,
        uint ClipSlot,
        InstanceLightSet Lights,
        uint Indoor,
        uint DetailCategory,
        float Opacity,
        Vector2 SelectionLighting);

    internal readonly record struct InstanceLightSet(
        int L0, int L1, int L2, int L3,
        int L4, int L5, int L6, int L7)
    {
        public static InstanceLightSet Disabled { get; } = new(
            -1, -1, -1, -1, -1, -1, -1, -1);

        public static InstanceLightSet From(ReadOnlySpan<int> source)
        {
            if (source.Length < LightManager.MaxLightsPerObject)
                throw new ArgumentException("A retail object-light set requires eight entries.", nameof(source));

            return new InstanceLightSet(
                source[0], source[1], source[2], source[3],
                source[4], source[5], source[6], source[7]);
        }

        public void CopyTo(int[] destination, int offset)
        {
            destination[offset + 0] = L0;
            destination[offset + 1] = L1;
            destination[offset + 2] = L2;
            destination[offset + 3] = L3;
            destination[offset + 4] = L4;
            destination[offset + 5] = L5;
            destination[offset + 6] = L6;
            destination[offset + 7] = L7;
        }

        public int this[int index] => index switch
        {
            0 => L0, 1 => L1, 2 => L2, 3 => L3,
            4 => L4, 5 => L5, 6 => L6, 7 => L7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    private sealed class AlphaDrawSource(WbDrawDispatcher owner) : IRetailAlphaDrawSource
    {
        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
            => owner.PrepareDeferredAlphaDraws(tokens);

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
            => owner.DrawPreparedAlphaBatch(firstPreparedDraw, drawCount);

        public void ResetAlphaSubmissions()
            => owner.ResetDeferredAlphaSubmissions();
    }

    // Per-frame scratch — reused across frames to avoid per-frame allocation.
    private readonly Dictionary<GroupKey, InstanceGroup> _groups = new();
    private readonly List<InstanceGroup> _opaqueDraws = new();
    private readonly List<InstanceGroup> _translucentDraws = new();
    private readonly List<AlphaFingerprint> _alphaFingerprintScratch = [];
    private readonly List<DeferredAlphaInstance> _deferredAlpha = new(128);
    private TranslucencyKind[] _deferredAlphaKinds = new TranslucencyKind[128];
    private Matrix4x4 _deferredAlphaViewProjection;
    private int _nextInstanceSubmissionOrder;

    internal long AlphaScratchBudgetBytes => _alphaScratchPolicy.BudgetBytes;
    internal long RetainedAlphaScratchBytes => checked(
        (long)_instanceData.Length * sizeof(float)
        + (long)_clipSlotData.Length * sizeof(uint)
        + (long)_lightSetData.Length * sizeof(int)
        + (long)_indoorData.Length * sizeof(uint)
        + (long)_detailCategoryData.Length * sizeof(uint)
        + (long)_alphaData.Length * sizeof(float)
        + (long)_selectionLightingData.Length * Unsafe.SizeOf<Vector2>()
        + (long)_batchData.Length * Unsafe.SizeOf<BatchData>()
        + (long)_indirectCommands.Length
            * Unsafe.SizeOf<DrawElementsIndirectCommand>()
        + (long)_drawCullModes.Length * Unsafe.SizeOf<CullMode>()
        + (long)_batchPublicScratch.Length
            * Unsafe.SizeOf<BatchDataPublic>()
        + (long)_deferredAlphaKinds.Length
            * Unsafe.SizeOf<TranslucencyKind>()
        + (long)_deferredAlpha.Capacity
            * Unsafe.SizeOf<DeferredAlphaInstance>());
    // A.5 T26 follow-up (Bug B): WalkEntities populates this scratch list
    // instead of allocating a fresh List<(WorldEntity, int)> per frame. At
    // ~10K entities × ~3 mesh refs = ~30K tuples × 16 bytes = ~480 KB / frame
    // of GC pressure on the render thread under the original T17 shape.
    private readonly List<(WorldEntity Entity, int MeshRefIndex, uint LandblockId)> _walkScratch = new();
    private readonly List<RenderInstanceTuple> _candidateTupleScratch = new();

    private readonly List<CachedBatch> _populateScratch = new();
    private readonly List<CachedSelectionPart> _populateSelectionScratch = new();

    private const float PerEntityCullRadius = 5.0f;

    private RetryableResourceReleaseLedger? _disposeResources;
    private bool _disposing;
    private bool _disposed;

    private int _entitiesSeen;
    private int _entitiesDrawn;
    private int _meshesMissing;
    private int _drawsIssued;
    private int _instancesIssued;
    private long _lastLogTick;

    private readonly HashSet<ulong> _missRequested = new();
    private readonly HashSet<ulong> _missLogged = new();

    // CPU + GPU timing for [WB-DIAG] under ACDREAM_WB_DIAG=1. The GPU samples
    // are written by the RHI arm's SampleRhiTimers (WbDrawDispatcher.Rhi.cs)
    // from the device's own timer pool; the raw-GL query-object ring that used
    // to feed them is gone with the raw-GL draw path.
    private readonly System.Diagnostics.Stopwatch _cpuStopwatch = new();
    private readonly long[] _cpuSamples = new long[256];   // microseconds
    private int _cpuSampleCursor;
    private readonly long[] _gpuSamples = new long[256];   // microseconds
    private int _gpuSampleCursor;

    public void BeginFrame(int frameSlot)
    {
        _ = frameSlot;
        ResetWorldTransformFrame();
        if (_groupFrame == long.MaxValue)
            throw new InvalidOperationException("Instance-group frame identity was exhausted.");

        ApplyScratchRetention(_scratchPeakUnits);
        _scratchPeakUnits = 0;
        _groupFrame++;
        PruneInstanceGroupsUnusedBeforeFrame(
            _groups,
            _retiredGroupKeys,
            _groupFrame - 1);
        _dynamicFrameStarted = true;
        _currentRenderSceneObserver?.BeginDispatcherFrame();
    }

    internal void SetCurrentRenderSceneObserver(
        ICurrentRenderDispatcherObserver? observer) =>
        _currentRenderSceneObserver = observer;

    internal void AbortCurrentRenderSceneObserverFrame() =>
        _currentRenderSceneObserver?.AbortDispatcherFrame();

    public void SetSceneLights(IReadOnlyList<LightSource>? pointSnapshot)
        => _pointSnapshot = pointSnapshot;

    public void SetClipRegionSsbo(uint sharedClipRegionSsbo)
        => _sharedClipRegionSsbo = sharedClipRegionSsbo;

    internal static (uint Slot, bool Culled) ResolveSlotForFrame() => (0u, false);

    public static Matrix4x4 ComposePartWorldMatrix(
        Matrix4x4 entityWorld,
        Matrix4x4 animOverride,
        Matrix4x4 restPose)
        => restPose * animOverride * entityWorld;

    public readonly record struct LandblockEntry(
        uint LandblockId,
        Vector3 AabbMin,
        Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById);

    public struct WalkResult
    {
        public int EntitiesWalked;
        public int BuildingShellAnchorPass;
        public int BuildingShellAnchorReject;
        public List<(WorldEntity Entity, int MeshRefIndex, uint LandblockId)> ToDraw;
    }

    internal static WalkResult WalkEntities(
        IEnumerable<LandblockEntry> landblockEntries,
        FrustumPlanes? frustum,
        uint? neverCullLandblockId,
        HashSet<uint>? visibleCellIds,
        HashSet<uint>? animatedEntityIds)
    {
        var scratch = new List<(WorldEntity Entity, int MeshRefIndex, uint LandblockId)>();
        var result = new WalkResult { ToDraw = scratch };
        WalkEntitiesInto(
            landblockEntries, frustum, neverCullLandblockId,
            visibleCellIds, animatedEntityIds, scratch, ref result);
        return result;
    }

    internal static void WalkEntitiesInto(
        IEnumerable<LandblockEntry> landblockEntries,
        FrustumPlanes? frustum,
        uint? neverCullLandblockId,
        HashSet<uint>? visibleCellIds,
        HashSet<uint>? animatedEntityIds,
        List<(WorldEntity Entity, int MeshRefIndex, uint LandblockId)> scratch,
        ref WalkResult result,
        EntitySet set = EntitySet.All)
    {
        scratch.Clear();
        result.EntitiesWalked = 0;
        result.ToDraw = scratch;

        foreach (var entry in landblockEntries)
        {
            bool landblockVisible = frustum is null
                || entry.LandblockId == neverCullLandblockId
                || FrustumCuller.IsAabbVisible(frustum.Value, entry.AabbMin, entry.AabbMax);

            if (!landblockVisible)
            {
                if (animatedEntityIds is null || animatedEntityIds.Count == 0) continue;
                if (entry.AnimatedById is null) continue;
                foreach (var animatedId in animatedEntityIds)
                {
                    if (!entry.AnimatedById.TryGetValue(animatedId, out var entity)) continue;
                    if (!entity.IsDrawVisible || !entity.IsAncestorDrawVisible) continue;
                    if (!EntityMatchesSet(entity, set)) continue;
                    if (entity.MeshRefs.Count == 0) continue;
                    bool shellScoped = IsShellScopedSet(set)
                        && entity.IsBuildingShell
                        && visibleCellIds is not null;
                    if (!EntityPassesVisibleCellGate(entity, visibleCellIds, set))
                    {
                        if (shellScoped) result.BuildingShellAnchorReject++;
                        continue;
                    }
                    if (shellScoped) result.BuildingShellAnchorPass++;
                    result.EntitiesWalked++;
                    for (int i = 0; i < entity.MeshRefs.Count; i++)
                        scratch.Add((entity, i, entry.LandblockId));
                }
                continue;
            }

            foreach (var entity in entry.Entities)
            {
                if (!entity.IsDrawVisible || !entity.IsAncestorDrawVisible) continue;
                if (!EntityMatchesSet(entity, set)) continue;
                if (entity.MeshRefs.Count == 0) continue;

                bool shellScoped = IsShellScopedSet(set)
                    && entity.IsBuildingShell
                    && visibleCellIds is not null;
                bool cellInVis = EntityPassesVisibleCellGate(entity, visibleCellIds, set);
                if (!cellInVis)
                {
                    if (shellScoped) result.BuildingShellAnchorReject++;
                    continue;
                }
                if (shellScoped) result.BuildingShellAnchorPass++;

                bool isAnimated = animatedEntityIds?.Contains(entity.Id) == true;
                bool aabbVisible = true;
                if (frustum is not null && !isAnimated && entry.LandblockId != neverCullLandblockId)
                {
                    if (entity.AabbDirty) entity.RefreshAabb();
                    aabbVisible = FrustumCuller.IsAabbVisible(frustum.Value, entity.AabbMin, entity.AabbMax);
                }

                if (!aabbVisible)
                {
                    continue;
                }

                result.EntitiesWalked++;
                for (int i = 0; i < entity.MeshRefs.Count; i++)
                    scratch.Add((entity, i, entry.LandblockId));
            }
        }
    }

    internal static uint ResolveCacheLandblockHint(WorldEntity entity, uint tupleLandblockId)
        => entity.ParentCellId is uint pc ? ((pc & 0xFFFF0000u) | 0xFFFFu) : tupleLandblockId;

    internal static uint ResolveCacheLandblockHint(
        in RenderInstanceCandidate entity) =>
        entity.CacheLandblockId;

    private static void BuildCurrentCandidateTuples(
        List<(WorldEntity Entity, int MeshRefIndex, uint LandblockId)> source,
        HashSet<uint>? animatedEntityIds,
        List<RenderInstanceTuple> destination)
    {
        destination.Clear();
        if (destination.Capacity < source.Count)
            destination.Capacity = source.Count;

        int index = 0;
        while (index < source.Count)
        {
            (WorldEntity entity, _, uint tupleLandblockId) = source[index];
            int end = index + 1;
            while (end < source.Count
                   && ReferenceEquals(source[end].Entity, entity)
                   && source[end].LandblockId == tupleLandblockId)
            {
                end++;
            }

            int meshPartCount = end - index;
            RenderInstanceCandidate candidate =
                RenderInstanceCandidate.FromWorldEntity(
                    entity,
                    animatedEntityIds?.Contains(entity.Id) == true,
                    meshPartCount,
                    tupleLandblockId);
            for (; index < end; index++)
            {
                int meshRefIndex = source[index].MeshRefIndex;
                destination.Add(new RenderInstanceTuple(
                    candidate,
                    meshRefIndex,
                    entity.MeshRefs[meshRefIndex]));
            }
        }
    }

    public void Draw(
        ICamera camera,
        IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                     IReadOnlyList<WorldEntity> Entities,
                     IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        FrustumPlanes? frustum = null,
        uint? neverCullLandblockId = null,
        HashSet<uint>? visibleCellIds = null,
        HashSet<uint>? animatedEntityIds = null,
        EntitySet set = EntitySet.All)
    {
        bool diag = BeginEntityDispatch(
            camera,
            out Matrix4x4 vp,
            out Vector3 camPos);

        _nextInstanceSubmissionOrder = 0;
        foreach (InstanceGroup group in _groups.Values)
            group.ClearPerInstanceData();

        uint anyVao = 0;

        // Project the 5-tuple enumerable into LandblockEntry records for WalkEntities.
        static IEnumerable<LandblockEntry> ToEntries(
            IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                         IReadOnlyList<WorldEntity> Entities,
                         IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> src)
        {
            foreach (var e in src)
                yield return new LandblockEntry(e.LandblockId, e.AabbMin, e.AabbMax, e.Entities, e.AnimatedById);
        }

        // A.5 T26 follow-up (Bug B): use the no-alloc WalkEntitiesInto overload
        // that populates _walkScratch (a per-dispatcher field reused across frames)
        // instead of allocating a fresh List<(WorldEntity, int)> per frame.
        //
        var walkResult = default(WalkResult);
        WalkEntitiesInto(
            ToEntries(landblockEntries),
            frustum,
            neverCullLandblockId,
            visibleCellIds,
            animatedEntityIds,
            _walkScratch,
            ref walkResult,
            set);
        _currentRenderSceneObserver?.ObserveDispatcherDraw(
            set,
            walkResult.EntitiesWalked,
            _walkScratch);
        BuildCurrentCandidateTuples(
            _walkScratch,
            animatedEntityIds,
            _candidateTupleScratch);

        uint? populateEntityId = null;
        uint populateLandblockId = 0;

        uint? lastHitEntityId = null;

        bool currentEntityIncomplete = false;

        uint? prevTupleEntityId = null;

        foreach (RenderInstanceTuple tuple in _candidateTupleScratch)
        {
            RenderInstanceCandidate entity = tuple.Candidate;
            int partIdx = tuple.MeshRefIndex;
            uint landblockId = entity.TupleLandblockId;
            if (diag) _entitiesSeen++;

            if (lastHitEntityId == entity.Id)
            {
                if (diag) _entitiesDrawn++;
                continue;
            }

            if (lastHitEntityId.HasValue && lastHitEntityId.Value != entity.Id)
            {
                lastHitEntityId = null;
            }

            uint cacheLb = ResolveCacheLandblockHint(in entity);

            bool isNewEntity = !prevTupleEntityId.HasValue || prevTupleEntityId.Value != entity.Id;
            if (isNewEntity)
            {
                if (populateEntityId.HasValue && currentEntityIncomplete)
                {
                    _populateScratch.Clear();
                    _populateSelectionScratch.Clear();
                    populateEntityId = null;
                }
                currentEntityIncomplete = false;

                (_currentEntitySlot, _currentEntityCulled) = ResolveSlotForFrame();

                ComputeEntityLightSet(entity);
                _currentEntityBuildingDetail = entity.IsBuildingShell;
                _currentEntitySelectionLighting =
                    _selectionLighting?.TryGetLighting(
                        entity.ServerGuid,
                        entity.Id,
                        out var lighting) == true
                        ? new Vector2(lighting.Luminosity, lighting.Diffuse)
                        : new Vector2(0f, 1f);

            }
            prevTupleEntityId = entity.Id;

            (populateEntityId, populateLandblockId) = MaybeFlushOnEntityChange(
                populateEntityId, populateLandblockId, entity.Id, _cache,
                _populateScratch, _populateSelectionScratch);

            if (_currentEntityCulled)
                continue;

            Matrix4x4 entityWorld = entity.RootWorld;

            bool isAnimated = entity.Animated;

            if (!isAnimated && !_tier1CacheDisabled && _cache.TryGet(entity.Id, cacheLb, out var cachedEntry))
            {
                ApplyCacheHitDirect(cachedEntry!, entityWorld);

                if (_selectionSink is not null)
                    PublishCachedSelectionParts(cachedEntry!, entity, entityWorld);

                if (anyVao == 0)
                {
                    MeshRef firstMeshRef = tuple.MeshRef;
                    var firstRenderData = _meshAdapter.TryGetRenderData(firstMeshRef.GfxObjId);
                    if (firstRenderData is not null) anyVao = firstRenderData.VAO;
                }

                if (diag) _entitiesDrawn++;
                lastHitEntityId = entity.Id;

#if DEBUG
                // Cross-check guard: assert the membership predicate held at hit time.
                // The full re-classification cross-check (spec section 6.5) is a stretch
                // goal; this simpler assert catches the prior Tier 1 bug class — a
                // static entity that turns out to actually be animated would fire here.
                //
                // Structurally redundant with the `if (!isAnimated && ...)` branch
                // condition, but serves as a TRIPWIRE: a future refactor that
                // incorrectly relaxes the branch condition (e.g., removes
                // `!isAnimated` from the guard) would silently allow animated
                // entities into the fast path; the assert catches that immediately.
                System.Diagnostics.Debug.Assert(
                    !isAnimated,
                    $"EntityClassificationCache hit on animated entity {entity.Id} — invariant violated");
#endif

                continue;
            }

            PaletteCompositeIdentity paletteIdentity = default;
            if (entity.PaletteOverride is not null)
                paletteIdentity = TextureCache.GetPaletteIdentity(entity.PaletteOverride);

            MeshRef meshRef = tuple.MeshRef;
            ulong gfxObjId = meshRef.GfxObjId;

            var renderData = _meshAdapter.TryGetRenderData(gfxObjId);

            if (renderData is null)
            {
                currentEntityIncomplete = true;
                if (diag) _meshesMissing++;
                if (_missRequested.Add(gfxObjId))
                {
                    _meshAdapter.EnsureLoaded(gfxObjId);
                    if (diag && _missLogged.Add(gfxObjId))
                        Console.WriteLine($"[mesh-miss] 0x{gfxObjId:X10} re-requested at point of use");
                }
                continue;
            }
            if (anyVao == 0) anyVao = renderData.VAO;

            var collector = isAnimated ? null : _populateScratch;
            var selectionCollector = isAnimated ? null : _populateSelectionScratch;

            bool drewAny = false;
            if (renderData.IsSetup && renderData.SetupParts.Count > 0)
            {
                bool entityHasCutoutSubset = FoliageWindClassification.ComputeEntityHasCutoutSubset(
                    renderData.SetupParts,
                    _meshAdapter,
                    static (adapter, part) => adapter.TryGetRenderData(part.GfxObjId) is { HasCutoutSubset: true });

                for (int setupPartIndex = 0; setupPartIndex < renderData.SetupParts.Count; setupPartIndex++)
                {
                    var (partGfxObjId, partTransform) = renderData.SetupParts[setupPartIndex];
                    var partData = _meshAdapter.TryGetRenderData(partGfxObjId);
                    if (partData is null)
                    {
                        currentEntityIncomplete = true;
                        if (diag) _meshesMissing++;
                        if (_missRequested.Add(partGfxObjId))
                        {
                            _meshAdapter.EnsureLoaded(partGfxObjId);
                            if (diag && _missLogged.Add(partGfxObjId))
                                Console.WriteLine($"[mesh-miss] 0x{partGfxObjId:X10} (setup part) re-requested at point of use");
                        }
                        continue;
                    }

                    var model = ComposePartWorldMatrix(
                        entityWorld, meshRef.PartTransform, partTransform);

                    var restPose = partTransform * meshRef.PartTransform;

                    float opacityMultiplier = EntityOpacity(entity.ServerGuid);
                    if (opacityMultiplier <= 0f) continue;
                    if (_translucencyFades.TryGetCurrentValue(entity.Id, (uint)setupPartIndex, out float translucencyValue))
                    {
                        if (translucencyValue >= 1.0f) continue; // skip this part's draw entirely
                        opacityMultiplier *= 1f - translucencyValue;
                    }

                    if (!ClassifyBatches(partData, model, entity, meshRef, paletteIdentity, restPose, opacityMultiplier, collector, entityHasCutoutSubset))
                        currentEntityIncomplete = true;
                    _selectionSink?.AddVisiblePart(
                        entity.ServerGuid,
                        entity.LocalEntityId,
                        unchecked((partIdx << 16) | (setupPartIndex & 0xFFFF)),
                        (uint)partGfxObjId,
                        model);
                    selectionCollector?.Add(new CachedSelectionPart(
                        unchecked((partIdx << 16) | (setupPartIndex & 0xFFFF)),
                        (uint)partGfxObjId,
                        restPose));
                    drewAny = true;
                }
            }
            else
            {
                float opacityMultiplier = EntityOpacity(entity.ServerGuid);
                bool fullyInvisible = false;
                if (opacityMultiplier <= 0f)
                    fullyInvisible = true;
                if (_translucencyFades.TryGetCurrentValue(entity.Id, (uint)partIdx, out float translucencyValue))
                {
                    if (translucencyValue >= 1.0f) fullyInvisible = true;
                    else opacityMultiplier *= 1f - translucencyValue;
                }

                if (!fullyInvisible)
                {
                    var model = meshRef.PartTransform * entityWorld;
                    if (!ClassifyBatches(renderData, model, entity, meshRef, paletteIdentity, restPose: meshRef.PartTransform, opacityMultiplier: opacityMultiplier, collector: collector))
                        currentEntityIncomplete = true;
                    _selectionSink?.AddVisiblePart(
                        entity.ServerGuid,
                        entity.LocalEntityId,
                        partIdx,
                        (uint)gfxObjId,
                        model);
                    selectionCollector?.Add(new CachedSelectionPart(
                        partIdx,
                        (uint)gfxObjId,
                        meshRef.PartTransform));
                    drewAny = true;
                }
            }

            if (collector is not null)
            {
                populateEntityId = entity.Id;
                populateLandblockId = cacheLb;
            }

            if (diag && drewAny) _entitiesDrawn++;
        }

        if (currentEntityIncomplete)
        {
            _populateScratch.Clear();
            _populateSelectionScratch.Clear();
            populateEntityId = null;
        }

        FinalFlushPopulate(
            populateEntityId, populateLandblockId, _cache,
            _populateScratch, _populateSelectionScratch);

        ExecuteClassifiedGroups(
            vp,
            camPos,
            anyVao,
            _groups.Values,
            set,
            walkResult.EntitiesWalked,
            _walkScratch.Count,
            diag,
            observeCurrentPath: true);
    }

    internal bool PreparePrivateEntityResources(
        IReadOnlyList<WorldEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        bool complete = true;
        for (int i = 0; i < entities.Count; i++)
        {
            if (PrepareCompositeEntity(entities[i]) != CompositeWarmupResult.Complete)
                complete = false;
        }
        return complete;
    }

    private float EntityOpacity(uint serverGuid)
    {
        float translucency = _hierarchicalTranslucency?.Invoke(serverGuid) ?? 0f;
        return 1f - Math.Clamp(translucency, 0f, 1f);
    }

    /// <summary>
    /// Whether there is a mesh source to draw from. The encoder arm has no
    /// vertex array of its own — the pipeline owns one shaped by
    /// <c>GpuVertexLayout.WorldMesh</c> — so this asks the shared mesh arena
    /// directly, the backend-neutral question V6i-3 published as
    /// <c>HasStores</c>.
    /// </summary>
    private bool MeshSourceReady() =>
        _meshAdapter.MeshManager?.GlobalBuffer is { HasStores: true };

    private bool BeginEntityDispatch(
        ICamera camera,
        out Matrix4x4 viewProjection,
        out Vector3 cameraWorldPosition)
    {
        _selectionLighting?.TickLighting();
        viewProjection = camera.View * camera.Projection;
        _missRequested.Clear();

        bool diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("ACDREAM_WB_DIAG"),
            "1",
            StringComparison.Ordinal);

        _cpuStopwatch.Restart();
        cameraWorldPosition = Vector3.Zero;
        if (Matrix4x4.Invert(camera.View, out Matrix4x4 inverseView))
            cameraWorldPosition = inverseView.Translation;
        return diagnosticsEnabled;
    }

    private void ExecuteClassifiedGroups(
        Matrix4x4 vp,
        Vector3 camPos,
        uint anyVao,
        IEnumerable<InstanceGroup> groups,
        EntitySet set,
        int entitiesWalked,
        int tupleCount,
        bool diag,
        bool observeCurrentPath)
    {
        // Nothing visible — skip the pass entirely.
        if (!MeshSourceReady())
        {
            LastDrawStats = new DrawStats(set, entitiesWalked, tupleCount, 0, 0, 0, 0, 0, 0);
            ObserveClassifiedDispatcherSubmission(observeCurrentPath,
                visibleInstanceCount: 0,
                immediateInstanceCount: 0,
                deferTransparent: false);
            _cpuStopwatch.Stop();
            if (diag) MaybeFlushDiag();
            return;
        }

        bool deferTransparent = _alphaQueue?.IsCollecting == true;
        var instanceCounts = PartitionInstanceGroups(
            groups,
            deferTransparent,
            camPos,
            _opaqueDraws,
            _translucentDraws);
        int totalInstances = instanceCounts.VisibleInstances;
        int immediateInstances = instanceCounts.ImmediateInstances;
        if (totalInstances == 0)
        {
            LastDrawStats = new DrawStats(set, entitiesWalked, tupleCount, 0, 0, 0, 0, 0, 0);
            ObserveClassifiedDispatcherSubmission(observeCurrentPath,
                visibleInstanceCount: 0,
                immediateInstanceCount: 0,
                deferTransparent);
            _cpuStopwatch.Stop();
            if (diag) MaybeFlushDiag();
            return;
        }

        _opaqueDraws.Sort(CompareOpaqueSubmissionOrder);
        if (deferTransparent)
            DeferTransparentGroups(vp);
        else
            _translucentDraws.Sort(CompareTransparentSubmissionOrder);

        int needed = immediateInstances * 16;
        if (_instanceData.Length < needed)
            _instanceData = new float[needed + 256 * 16];

        if (_clipSlotData.Length < immediateInstances)
            _clipSlotData = new uint[immediateInstances + 256];

        if (_lightSetData.Length < immediateInstances * LightManager.MaxLightsPerObject)
            _lightSetData = new int[(immediateInstances + 256) * LightManager.MaxLightsPerObject];

        if (_indoorData.Length < immediateInstances)
            _indoorData = new uint[immediateInstances + 256];

        if (_detailCategoryData.Length < immediateInstances)
            _detailCategoryData = new uint[immediateInstances + 256];

        if (_alphaData.Length < immediateInstances)
            _alphaData = new float[immediateInstances + 256];

        if (_selectionLightingData.Length < immediateInstances)
            _selectionLightingData = new Vector2[immediateInstances + 256];

        int cursor = 0;
        foreach (InstanceGroup grp in _opaqueDraws)
            StageImmediateGroup(grp, ref cursor);
        if (!deferTransparent)
        {
            foreach (InstanceGroup grp in _translucentDraws)
                StageImmediateGroup(grp, ref cursor);
        }
        System.Diagnostics.Debug.Assert(cursor == immediateInstances);

        int immediateTransparentCount = deferTransparent ? 0 : _translucentDraws.Count;
        int totalDraws = _opaqueDraws.Count + immediateTransparentCount;
        TrackScratchDemand(Math.Max(totalInstances, totalDraws));
        if (_batchData.Length < totalDraws)
            _batchData = new BatchData[totalDraws + 64];
        if (_indirectCommands.Length < totalDraws)
            _indirectCommands = new DrawElementsIndirectCommand[totalDraws + 64];
        if (_drawCullModes.Length < totalDraws)
            _drawCullModes = new CullMode[totalDraws + 64];
        if (_batchPublicScratch.Length < totalDraws)
            _batchPublicScratch = new BatchDataPublic[totalDraws + 64];

        _groupInputScratch.Clear();
        foreach (var g in _opaqueDraws) _groupInputScratch.Add(ToInput(g));
        if (!deferTransparent)
            foreach (var g in _translucentDraws) _groupInputScratch.Add(ToInput(g));

        var layout = BuildIndirectArrays(
            _groupInputScratch,
            _indirectCommands,
            _batchPublicScratch,
            _drawCullModes);
        long totalTriangles = 0;
        foreach (var input in _groupInputScratch)
            totalTriangles += (long)(input.IndexCount / 3) * input.InstanceCount;
        int cullRuns =
            CountCullRuns(_drawCullModes, 0, layout.OpaqueCount) +
            CountCullRuns(_drawCullModes, layout.OpaqueCount, layout.TransparentCount);

        for (int i = 0; i < totalDraws; i++)
        {
            _batchData[i] = new BatchData
            {
                TextureIndex = _batchPublicScratch[i].TextureIndex,
                SurfaceOpacity = _batchPublicScratch[i].SurfaceOpacity,
                TextureLayer = _batchPublicScratch[i].TextureLayer,
                Flags        = _batchPublicScratch[i].Flags,
            };
        }
        _opaqueDrawCount       = layout.OpaqueCount;
        _transparentDrawCount  = layout.TransparentCount;
        _transparentByteOffset = layout.TransparentByteOffset;
        LastDrawStats = new DrawStats(
            set,
            entitiesWalked,
            tupleCount,
            totalInstances,
            totalDraws,
            cullRuns,
            _opaqueDrawCount,
            _transparentDrawCount,
            totalTriangles);
        ObserveClassifiedDispatcherSubmission(observeCurrentPath,
            totalInstances,
            immediateInstances,
            deferTransparent);

        SubmitRhi(vp, immediateInstances, totalDraws, diag);
        _cpuStopwatch.Stop();
        if (diag)
        {
            long cpuUs = _cpuStopwatch.ElapsedTicks * 1_000_000L
                / System.Diagnostics.Stopwatch.Frequency;
            _cpuSamples[_cpuSampleCursor] = cpuUs;
            _cpuSampleCursor = (_cpuSampleCursor + 1) % _cpuSamples.Length;
            _drawsIssued += _opaqueDrawCount + _transparentDrawCount;
            _instancesIssued += totalInstances;
            MaybeFlushDiag();
        }
    }


    public void Draw(
        ICamera camera,
        IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                     IReadOnlyList<WorldEntity> Entities,
                     IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        IReadOnlyCollection<uint> cellIds,
        FrustumPlanes? frustum = null,
        uint? neverCullLandblockId = null,
        HashSet<uint>? animatedEntityIds = null,
        EntitySet set = EntitySet.All)
    {
        HashSet<uint> cellIdSet = cellIds is HashSet<uint> hs ? hs : new HashSet<uint>(cellIds);
        Draw(camera, landblockEntries,
             frustum: frustum,
             neverCullLandblockId: neverCullLandblockId,
             visibleCellIds: cellIdSet,
             animatedEntityIds: animatedEntityIds,
             set: set);
    }

    private void PublishCachedSelectionParts(
        EntityCacheEntry cachedEntry,
        in RenderInstanceCandidate entity,
        Matrix4x4 entityWorld)
    {
        foreach (CachedSelectionPart part in cachedEntry.SelectionParts)
        {
            _selectionSink!.AddVisiblePart(
                entity.ServerGuid,
                entity.LocalEntityId,
                part.PartIndex,
                part.GfxObjId,
                part.RestPose * entityWorld);
        }
    }

    private static IndirectGroupInput ToInput(InstanceGroup g) => new(
        IndexCount:    g.IndexCount,
        FirstIndex:    g.FirstIndex,
        BaseVertex:    g.BaseVertex,
        InstanceCount: g.InstanceCount,
        FirstInstance: g.FirstInstance,
        TextureIndex:  g.TextureSlot.Index,
        TextureLayer:  g.TextureLayer,
        Translucency:  g.Translucency,
        MaterialState: g.MaterialState,
        SurfaceOpacity: g.SurfaceOpacity,
        CullMode:      g.CullMode,
        FoliageFlags:  g.FoliageFlags);

    internal readonly record struct InstanceLayoutCounts(
        int VisibleInstances,
        int ImmediateInstances);

    internal static InstanceLayoutCounts PartitionInstanceGroups(
        IEnumerable<InstanceGroup> groups,
        bool deferTransparent,
        Vector3 cameraWorldPosition,
        List<InstanceGroup> opaque,
        List<InstanceGroup> transparent)
    {
        opaque.Clear();
        transparent.Clear();

        int visibleInstances = 0;
        int immediateInstances = 0;
        foreach (InstanceGroup group in groups)
        {
            int count = group.Matrices.Count;
            if (count == 0)
                continue;

            group.InstanceCount = count;
            Matrix4x4 first = group.Matrices[0];
            var groupPosition = new Vector3(first.M41, first.M42, first.M43);
            group.SortDistance = Vector3.DistanceSquared(cameraWorldPosition, groupPosition);
            visibleInstances += count;

            if (IsOpaque(group.Translucency))
            {
                opaque.Add(group);
                immediateInstances += count;
            }
            else
            {
                transparent.Add(group);
                if (!deferTransparent)
                    immediateInstances += count;
            }
        }

        return new InstanceLayoutCounts(visibleInstances, immediateInstances);
    }

    private void StageImmediateGroup(InstanceGroup group, ref int cursor)
    {
        group.FirstInstance = cursor;
        for (int i = 0; i < group.Matrices.Count; i++)
        {
            WriteMatrix(_instanceData, cursor * 16, group.Matrices[i]);
            _clipSlotData[cursor] = group.Slots[i];
            group.LightSets[i].CopyTo(
                _lightSetData,
                cursor * LightManager.MaxLightsPerObject);
            _indoorData[cursor] = group.IndoorFlags[i];
            _detailCategoryData[cursor] = group.DetailCategories[i];
            _alphaData[cursor] = group.Opacities[i];
            _selectionLightingData[cursor] = group.SelectionLighting[i];
            cursor++;
        }
    }

    private static GroupKey ToKey(InstanceGroup g) => new(
        g.FirstIndex,
        g.BaseVertex,
        g.IndexCount,
        g.TextureSlot,
        g.TextureLayer,
        g.Translucency,
        g.MaterialState,
        g.FoliageFlags,
        g.SurfaceOpacity,
        g.CullMode);

    internal static InstanceGroup CreateGroupFromKey(
        GroupKey key,
        long registration,
        long frame) => new()
    {
        FirstIndex = key.FirstIndex,
        BaseVertex = key.BaseVertex,
        IndexCount = key.IndexCount,
        TextureSlot = key.TextureSlot,
        TextureLayer = key.TextureLayer,
        Translucency = key.Translucency,
        MaterialState = key.MaterialState,
        CullMode = key.CullMode,
        FoliageFlags = key.FoliageFlags,
        SurfaceOpacity = key.SurfaceOpacity,
        Registration = registration,
        LastUsedFrame = frame,
    };

    private void ObserveCurrentDispatcherSubmission(
        int visibleInstanceCount,
        int immediateInstanceCount,
        bool deferTransparent)
    {
        ICurrentRenderDispatcherObserver? observer =
            _currentRenderSceneObserver;
        if (observer is null)
            return;

        CurrentRenderDispatcherSubmission submission =
            CreateDispatcherSubmission(
                visibleInstanceCount,
                immediateInstanceCount,
                deferTransparent,
                _opaqueDraws,
                _translucentDraws,
                _alphaFingerprintScratch);
        observer.ObserveDispatcherSubmission(in submission);
    }

    private void ObserveClassifiedDispatcherSubmission(
        bool observeCurrentPath,
        int visibleInstanceCount,
        int immediateInstanceCount,
        bool deferTransparent)
    {
        if (observeCurrentPath)
        {
            ObserveCurrentDispatcherSubmission(
                visibleInstanceCount,
                immediateInstanceCount,
                deferTransparent);
        }
    }

    internal static CurrentRenderDispatcherSubmission
        CreateDispatcherSubmission(
            int visibleInstanceCount,
            int immediateInstanceCount,
            bool deferTransparent,
            IReadOnlyList<InstanceGroup> opaque,
            IReadOnlyList<InstanceGroup> transparent,
            List<AlphaFingerprint> alphaScratch)
    {
        IReadOnlyList<InstanceGroup> acceptedOpaque =
            visibleInstanceCount == 0
                ? Array.Empty<InstanceGroup>()
                : opaque;
        IReadOnlyList<InstanceGroup> acceptedTransparent =
            visibleInstanceCount == 0
                ? Array.Empty<InstanceGroup>()
                : transparent;
        int opaqueGroupCount = acceptedOpaque.Count;
        int transparentGroupCount = acceptedTransparent.Count;
        StableRenderHash128 hash = StableRenderHash128.Create();
        hash.Add(visibleInstanceCount);
        hash.Add(immediateInstanceCount);
        hash.Add(opaqueGroupCount);
        hash.Add(transparentGroupCount);
        hash.Add(deferTransparent);
        RenderSceneHash128 opaqueDigest =
            BuildOpaqueSubmissionDigest(acceptedOpaque);
        RenderSceneHash128 transparentDigest =
            BuildTransparentSubmissionDigest(
                acceptedTransparent,
                alphaScratch);
        RenderSceneHash128 transparentSetDigest =
            BuildOpaqueSubmissionDigest(acceptedTransparent);
        hash.Add(opaqueDigest.Low);
        hash.Add(opaqueDigest.High);
        hash.Add(transparentDigest.Low);
        hash.Add(transparentDigest.High);
        hash.Add(transparentSetDigest.Low);
        hash.Add(transparentSetDigest.High);

        return new CurrentRenderDispatcherSubmission(
            VisibleInstanceCount: visibleInstanceCount,
            ImmediateInstanceCount: immediateInstanceCount,
            OpaqueGroupCount: opaqueGroupCount,
            TransparentGroupCount: transparentGroupCount,
            TransparentDeferred: deferTransparent,
            OpaqueDigest: opaqueDigest,
            TransparentDigest: transparentDigest,
            TransparentSetDigest: transparentSetDigest,
            Digest: hash.Finish());
    }

    private static RenderSceneHash128 BuildOpaqueSubmissionDigest(
        IReadOnlyList<InstanceGroup> groups)
    {
        ulong xorLow = 0;
        ulong xorHigh = 0;
        ulong sumLow = 0;
        ulong sumHigh = 0;
        for (int index = 0; index < groups.Count; index++)
        {
            StableRenderHash128 groupHash = StableRenderHash128.Create();
            AddOpaqueSubmissionGroup(
                ref groupHash,
                groups[index]);
            RenderSceneHash128 digest = groupHash.Finish();
            xorLow ^= digest.Low;
            xorHigh ^= digest.High;
            sumLow = unchecked(sumLow + digest.Low);
            sumHigh = unchecked(sumHigh + digest.High);
        }

        StableRenderHash128 hash = StableRenderHash128.Create();
        hash.Add(groups.Count);
        hash.Add(xorLow);
        hash.Add(xorHigh);
        hash.Add(sumLow);
        hash.Add(sumHigh);
        return hash.Finish();
    }

    private static RenderSceneHash128 BuildTransparentSubmissionDigest(
        IReadOnlyList<InstanceGroup> groups,
        List<AlphaFingerprint> scratch)
    {
        scratch.Clear();
        for (int groupIndex = 0;
             groupIndex < groups.Count;
             groupIndex++)
        {
            InstanceGroup group = groups[groupIndex];
            for (int instanceIndex = 0;
                 instanceIndex < group.Matrices.Count;
                 instanceIndex++)
            {
                scratch.Add(new AlphaFingerprint(
                    group,
                    instanceIndex,
                    group.SubmissionOrders[instanceIndex]));
            }
        }
        scratch.Sort(AlphaSubmissionOrderComparer.Instance);

        StableRenderHash128 hash = StableRenderHash128.Create();
        hash.Add(scratch.Count);
        for (int index = 0; index < scratch.Count; index++)
        {
            AlphaFingerprint entry = scratch[index];
            GroupKey key = ToKey(entry.Group);
            hash.Add(key.FirstIndex);
            hash.Add(key.BaseVertex);
            hash.Add(key.IndexCount);
            hash.Add(key.TextureSlot.Index);
            hash.Add(key.TextureLayer);
            hash.Add((int)key.Translucency);
            hash.Add((int)key.CullMode);
            hash.Add(key.FoliageFlags);
            AddSubmissionInstance(
                ref hash,
                entry.Group,
                entry.InstanceIndex);
        }
        return hash.Finish();
    }

    internal readonly record struct AlphaFingerprint(
        InstanceGroup Group,
        int InstanceIndex,
        int SubmissionOrder);

    private static void AddOpaqueSubmissionGroup(
        ref StableRenderHash128 hash,
        InstanceGroup group)
    {
        GroupKey key = ToKey(group);
        hash.Add(key.FirstIndex);
        hash.Add(key.BaseVertex);
        hash.Add(key.IndexCount);
        hash.Add(key.TextureSlot.Index);
        hash.Add(key.TextureLayer);
        hash.Add((int)key.Translucency);
        hash.Add((int)key.CullMode);
        hash.Add(key.FoliageFlags);
        hash.Add(group.Matrices.Count);

        ulong xorLow = 0;
        ulong xorHigh = 0;
        ulong sumLow = 0;
        ulong sumHigh = 0;
        for (int index = 0;
             index < group.Matrices.Count;
             index++)
        {
            StableRenderHash128 instanceHash =
                StableRenderHash128.Create();
            AddSubmissionInstance(
                ref instanceHash,
                group,
                index);
            RenderSceneHash128 digest = instanceHash.Finish();
            xorLow ^= digest.Low;
            xorHigh ^= digest.High;
            sumLow = unchecked(sumLow + digest.Low);
            sumHigh = unchecked(sumHigh + digest.High);
        }

        hash.Add(xorLow);
        hash.Add(xorHigh);
        hash.Add(sumLow);
        hash.Add(sumHigh);
    }

    private static void AddSubmissionInstance(
        ref StableRenderHash128 hash,
        InstanceGroup group,
        int index)
    {
        hash.Add(group.Matrices[index]);
        hash.Add(group.Slots[index]);
        InstanceLightSet lights = group.LightSets[index];
        for (int lightIndex = 0;
             lightIndex < LightManager.MaxLightsPerObject;
             lightIndex++)
        {
            hash.Add(lights[lightIndex]);
        }
        hash.Add(group.IndoorFlags[index]);
        hash.Add(group.DetailCategories[index]);
        hash.Add(group.Opacities[index]);
        hash.Add(group.SelectionLighting[index]);
    }

    private void DeferTransparentGroups(Matrix4x4 viewProjection)
    {
        RetailAlphaQueue queue = _alphaQueue!;
        if (_deferredAlpha.Count == 0)
            _deferredAlphaViewProjection = viewProjection;
        else if (_deferredAlphaViewProjection != viewProjection)
            throw new InvalidOperationException(
                "One retail alpha scope cannot combine different view-projection matrices.");

        _alphaFingerprintScratch.Clear();
        foreach (InstanceGroup group in _translucentDraws)
        {
            for (int i = 0; i < group.Matrices.Count; i++)
            {
                _alphaFingerprintScratch.Add(new AlphaFingerprint(
                    group,
                    i,
                    group.SubmissionOrders[i]));
            }
        }
        _alphaFingerprintScratch.Sort(
            AlphaSubmissionOrderComparer.Instance);

        foreach (AlphaFingerprint entry in _alphaFingerprintScratch)
        {
            InstanceGroup group = entry.Group;
            int i = entry.InstanceIndex;
            var candidate = new DeferredAlphaInstance(
                ToKey(group),
                group.Matrices[i],
                group.Slots[i],
                group.LightSets[i],
                group.IndoorFlags[i],
                group.DetailCategories[i],
                group.Opacities[i],
                group.SelectionLighting[i]);
            SubmitToAlphaQueue(
                queue, group.Translucency, in candidate, group.DetailCategories[i] == 1u, viewProjection);
        }
    }

    private void SubmitToAlphaQueue(
        RetailAlphaQueue queue,
        TranslucencyKind kind,
        in DeferredAlphaInstance candidate,
        bool isBuildingShell,
        Matrix4x4 viewProjection)
    {
        byte mask = RetailAlphaMeshRouter.MaskFromTranslucencyKind(kind);
        bool detailSurfaceActive = isBuildingShell
            && RetailDetailTextureContract.ShouldRender(_buildingDetailEnabled(), _buildingDetail)
            && _buildingDetail.Tiling != 0f;
        RetailAlphaMeshDecision decision = RetailAlphaMeshRouter.Route(
            currentlyDrawingSky: false,
            delayMask: RetailAlphaMeshRouter.DefaultDelayMask,
            detailSurfaceActive: detailSurfaceActive,
            multiPassAlpha: false,
            subsetMask: mask,
            materialHasAlpha: false);

        if (decision.Action == RetailAlphaMeshAction.Immediate)
        {
            DrawImmediateAlphaInstance(in candidate, viewProjection);
            return;
        }

        int token = _deferredAlpha.Count;
        _deferredAlpha.Add(candidate);
        queue.TryAppend(decision.List, _alphaSource, token, decision.OverrideClipmap);

        if (decision.Action == RetailAlphaMeshAction.AppendClipAndImmediate)
        {
            DrawImmediateAlphaInstance(in candidate, viewProjection);
        }

    }

    private sealed class AlphaSubmissionOrderComparer :
        IComparer<AlphaFingerprint>
    {
        public static AlphaSubmissionOrderComparer Instance { get; } =
            new();

        private AlphaSubmissionOrderComparer()
        {
        }

        public int Compare(
            AlphaFingerprint left,
            AlphaFingerprint right) =>
            left.SubmissionOrder.CompareTo(right.SubmissionOrder);
    }

    private void WriteDeferredAlphaEntrySlot(int slot, in DeferredAlphaInstance entry)
    {
        WriteMatrix(_instanceData, slot * 16, entry.Model);
        _clipSlotData[slot] = entry.ClipSlot;
        _indoorData[slot] = entry.Indoor;
        _detailCategoryData[slot] = entry.DetailCategory;
        _alphaData[slot] = entry.Opacity;
        _selectionLightingData[slot] = entry.SelectionLighting;
        int lightOffset = slot * LightManager.MaxLightsPerObject;
        entry.Lights.CopyTo(_lightSetData, lightOffset);

        GroupKey key = entry.Key;
        _batchData[slot] = new BatchData
        {
            TextureIndex = key.TextureSlot.Index,
            SurfaceOpacity = key.SurfaceOpacity,
            TextureLayer = key.TextureLayer,
            Flags = 1u | key.FoliageFlags,
        };
        _indirectCommands[slot] = new DrawElementsIndirectCommand
        {
            Count = (uint)key.IndexCount,
            InstanceCount = 1,
            FirstIndex = key.FirstIndex,
            BaseVertex = key.BaseVertex,
            BaseInstance = (uint)slot,
        };
        _drawCullModes[slot] = key.CullMode;
        _deferredAlphaKinds[slot] = key.Translucency;
    }

    private void PrepareDeferredAlphaDraws(ReadOnlySpan<int> tokens)
    {
        if (tokens.Length == 0)
            return;

        GlobalMeshBuffer? global = _meshAdapter.MeshManager?.GlobalBuffer;
        if (global is null || !MeshSourceReady())
            return;

        int count = tokens.Length;
        EnsureDeferredAlphaCapacity(count);
        for (int i = 0; i < count; i++)
            WriteDeferredAlphaEntrySlot(i, _deferredAlpha[tokens[i]]);

        PrepareRhiAlphaSections(count);
    }

    private void DrawImmediateAlphaInstance(in DeferredAlphaInstance entry, Matrix4x4 viewProjection)
    {
        GlobalMeshBuffer? global = _meshAdapter.MeshManager?.GlobalBuffer;
        if (global is null || !MeshSourceReady())
            return;

        EnsureDeferredAlphaCapacity(1);
        WriteDeferredAlphaEntrySlot(0, in entry);
        PrepareRhiAlphaSections(1);
        DrawImmediateAlphaInstanceRhi(
            global,
            entry.Key.Translucency,
            entry.Key.MaterialState,
            viewProjection);
    }

    private void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
    {
        if (drawCount <= 0)
            return;
        if (firstPreparedDraw < 0
            || firstPreparedDraw > _deferredAlpha.Count - drawCount)
            throw new ArgumentOutOfRangeException(nameof(firstPreparedDraw));

        GlobalMeshBuffer? global = _meshAdapter.MeshManager?.GlobalBuffer;
        if (global is null || !MeshSourceReady())
            return;

        DrawPreparedAlphaBatchRhi(global, firstPreparedDraw, drawCount);
    }

    private void EnsureDeferredAlphaCapacity(int count)
    {
        TrackScratchDemand(count);
        int neededMatrixFloats = count * 16;
        if (_instanceData.Length < neededMatrixFloats)
            _instanceData = new float[neededMatrixFloats + 256 * 16];
        if (_clipSlotData.Length < count)
            _clipSlotData = new uint[count + 256];
        if (_indoorData.Length < count)
            _indoorData = new uint[count + 256];
        if (_detailCategoryData.Length < count)
            _detailCategoryData = new uint[count + 256];
        if (_alphaData.Length < count)
            _alphaData = new float[count + 256];
        if (_selectionLightingData.Length < count)
            _selectionLightingData = new Vector2[count + 256];
        if (_lightSetData.Length < count * LightManager.MaxLightsPerObject)
            _lightSetData = new int[(count + 256) * LightManager.MaxLightsPerObject];
        if (_batchData.Length < count)
            _batchData = new BatchData[count + 64];
        if (_indirectCommands.Length < count)
            _indirectCommands = new DrawElementsIndirectCommand[count + 64];
        if (_drawCullModes.Length < count)
            _drawCullModes = new CullMode[count + 64];
        if (_deferredAlphaKinds.Length < count)
            _deferredAlphaKinds = new TranslucencyKind[count + 64];
    }

    private void ResetDeferredAlphaSubmissions()
    {
        _deferredAlpha.Clear();
    }

    private void TrackScratchDemand(int units)
    {
        if (units > _scratchPeakUnits)
            _scratchPeakUnits = units;
    }

    private void ApplyScratchRetention(int observedUnits)
    {
        int currentCapacity = Math.Max(
            _instanceData.Length / 16,
            Math.Max(
                _lightSetData.Length / LightManager.MaxLightsPerObject,
                Math.Max(
                    _deferredAlpha.Capacity,
                    Math.Max(_batchData.Length, _indirectCommands.Length))));
        int bytesPerUnit = checked(
            16 * sizeof(float)
            + sizeof(uint)
            + sizeof(uint)
            + LightManager.MaxLightsPerObject * sizeof(int)
            + sizeof(uint)
            + sizeof(float)
            + Unsafe.SizeOf<Vector2>()
            + Unsafe.SizeOf<BatchData>()
            + Unsafe.SizeOf<DrawElementsIndirectCommand>()
            + Unsafe.SizeOf<CullMode>()
            + Unsafe.SizeOf<BatchDataPublic>()
            + Unsafe.SizeOf<TranslucencyKind>()
            + Unsafe.SizeOf<DeferredAlphaInstance>());
        int targetCapacity = _alphaScratchPolicy.ObserveAndSelectCapacity(
            currentCapacity,
            observedUnits,
            bytesPerUnit,
            minimumCapacity: 256,
            growthQuantum: 256);
        if (targetCapacity >= currentCapacity)
            return;

        _instanceData = new float[checked(targetCapacity * 16)];
        _clipSlotData = new uint[targetCapacity];
        _lightSetData = new int[
            checked(targetCapacity * LightManager.MaxLightsPerObject)];
        _indoorData = new uint[targetCapacity];
        _detailCategoryData = new uint[targetCapacity];
        _alphaData = new float[targetCapacity];
        _selectionLightingData = new Vector2[targetCapacity];
        _batchData = new BatchData[targetCapacity];
        _indirectCommands = new DrawElementsIndirectCommand[targetCapacity];
        _drawCullModes = new CullMode[targetCapacity];
        _batchPublicScratch = new BatchDataPublic[targetCapacity];
        _deferredAlphaKinds = new TranslucencyKind[targetCapacity];
        _deferredAlpha.Capacity = targetCapacity;
    }

    private static int CompareOpaqueSubmissionOrder(InstanceGroup a, InstanceGroup b)
    {
        int cull = ((int)a.CullMode).CompareTo((int)b.CullMode);
        return cull != 0 ? cull : a.SortDistance.CompareTo(b.SortDistance);
    }

    private static int CompareTransparentSubmissionOrder(InstanceGroup a, InstanceGroup b)
    {
        int cull = ((int)a.CullMode).CompareTo((int)b.CullMode);
        return cull != 0 ? cull : b.SortDistance.CompareTo(a.SortDistance);
    }

    private static int CountCullRuns(CullMode[] modes, int startCommand, int commandCount)
    {
        if (commandCount <= 0) return 0;

        int end = startCommand + commandCount;
        int runs = 1;
        var previous = modes[startCommand];
        for (int i = startCommand + 1; i < end; i++)
        {
            var current = modes[i];
            if (current == previous) continue;
            runs++;
            previous = current;
        }
        return runs;
    }

    private void MaybeFlushDiag()
    {
        long now = Environment.TickCount64;
        if (now - _lastLogTick > 5000)
        {
            long cpuMed = MedianMicros(_cpuSamples);
            long cpuP95 = Percentile95Micros(_cpuSamples);
            long gpuMed = MedianMicros(_gpuSamples);
            long gpuP95 = Percentile95Micros(_gpuSamples);
            const long BudgetUs = 2000;
            string budgetFlag = cpuMed > BudgetUs ? " BUDGET_OVER" : "";
            Console.WriteLine(
                $"[WB-DIAG]{budgetFlag} entSeen={_entitiesSeen} entDrawn={_entitiesDrawn} meshMissing={_meshesMissing} drawsIssued={_drawsIssued} instances={_instancesIssued} groups={_groups.Count} " +
                $"cpu_us={cpuMed}m/{cpuP95}p95 gpu_us={gpuMed}m/{gpuP95}p95");
            _entitiesSeen = _entitiesDrawn = _meshesMissing = _drawsIssued = _instancesIssued = 0;
            _lastLogTick = now;
        }
    }

    private static long MedianMicros(long[] samples)
    {
        var copy = (long[])samples.Clone();
        Array.Sort(copy);
        int nz = 0;
        foreach (var v in copy) if (v > 0) nz++;
        if (nz == 0) return 0;
        return copy[copy.Length - 1 - (nz - 1) / 2];
    }

    private static long Percentile95Micros(long[] samples)
    {
        var copy = (long[])samples.Clone();
        Array.Sort(copy);
        int nz = 0;
        foreach (var v in copy) if (v > 0) nz++;
        if (nz == 0) return 0;
        int idx = copy.Length - 1 - (int)(nz * 0.05);
        return copy[idx];
    }


    internal static void ApplyCacheHit(
        EntityCacheEntry entry,
        Matrix4x4 entityWorld,
        Action<GroupKey, Matrix4x4> appendInstance)
    {
        foreach (var cached in entry.Batches)
        {
            appendInstance(
                cached.Key,
                cached.RestPose * entityWorld);
        }
    }

    internal static bool TryResolveCachedGroup(
        CachedBatch cached,
        out InstanceGroup? group)
    {
        group = cached.Group;
        return group is not null
            && cached.GroupRegistration != 0
            && cached.GroupRegistration == group.Registration;
    }

    private void ApplyCacheHitDirect(EntityCacheEntry entry, Matrix4x4 entityWorld)
    {
        for (int i = 0; i < entry.Batches.Length; i++)
        {
            CachedBatch cached = entry.Batches[i];
            Matrix4x4 model = cached.RestPose * entityWorld;
            if (!TryResolveCachedGroup(cached, out InstanceGroup? group))
            {
                group = GetOrCreateInstanceGroup(cached.Key);
                entry.Batches[i] = cached with
                {
                    Group = group,
                    GroupRegistration = group.Registration,
                };
            }
            AppendInstanceToGroup(group!, model);
        }
    }

    internal static int PruneInstanceGroupsUnusedBeforeFrame(
        Dictionary<GroupKey, InstanceGroup> groups,
        List<GroupKey> retiredKeys,
        long oldestLiveFrame)
    {
        retiredKeys.Clear();
        foreach ((GroupKey key, InstanceGroup group) in groups)
        {
            if (group.LastUsedFrame < oldestLiveFrame)
            {
                group.Registration = 0;
                group.ReleasePerInstanceStorage();
                retiredKeys.Add(key);
            }
        }

        foreach (GroupKey key in retiredKeys)
            groups.Remove(key);

        int retiredCount = retiredKeys.Count;
        retiredKeys.Clear();
        return retiredCount;
    }

    internal static (uint? PopulateEntityId, uint PopulateLandblockId)
        MaybeFlushOnEntityChange(
            uint? populateEntityId,
            uint populateLandblockId,
            uint currentEntityId,
            EntityClassificationCache cache,
            List<CachedBatch> populateScratch,
            List<CachedSelectionPart>? selectionScratch = null)
    {
        if (populateEntityId.HasValue && populateEntityId.Value != currentEntityId)
        {
            if (populateScratch.Count > 0)
            {
                cache.Populate(
                    populateEntityId.Value,
                    populateLandblockId,
                    populateScratch.ToArray(),
                    selectionScratch?.ToArray());
            }
            populateScratch.Clear();
            selectionScratch?.Clear();
            return (null, 0u);
        }
        return (populateEntityId, populateLandblockId);
    }

    internal static void FinalFlushPopulate(
        uint? populateEntityId,
        uint populateLandblockId,
        EntityClassificationCache cache,
        List<CachedBatch> populateScratch,
        List<CachedSelectionPart>? selectionScratch = null)
    {
        if (populateEntityId.HasValue && populateScratch.Count > 0)
        {
            cache.Populate(
                populateEntityId.Value,
                populateLandblockId,
                populateScratch.ToArray(),
                selectionScratch?.ToArray());
            populateScratch.Clear();
        }
        selectionScratch?.Clear();
    }

    private void AppendInstanceToGroup(
        GroupKey key,
        Matrix4x4 model)
    {
        InstanceGroup grp = GetOrCreateInstanceGroup(key);
        AppendInstanceToGroup(grp, model);
    }

    private InstanceGroup GetOrCreateInstanceGroup(GroupKey key)
    {
        if (_groups.TryGetValue(key, out InstanceGroup? group))
        {
            group.LastUsedFrame = _groupFrame;
            return group;
        }

        if (_nextGroupRegistration == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Instance-group registration space was exhausted before a safe identity could be assigned.");
        }

        group = CreateGroupFromKey(
            key,
            registration: _nextGroupRegistration++,
            frame: _groupFrame);
        _groups.Add(key, group);
        return group;
    }

    private void AppendInstanceToGroup(
        InstanceGroup grp,
        Matrix4x4 model)
    {
        grp.LastUsedFrame = _groupFrame;
        grp.Matrices.Add(model);
        grp.SubmissionOrders.Add(_nextInstanceSubmissionOrder++);
        grp.Slots.Add(_currentEntitySlot);
        AppendCurrentLightSet(grp);        // Fix B — 8 ints per instance, parallel to Matrices
        grp.Opacities.Add(1.0f);
        grp.SelectionLighting.Add(_currentEntitySelectionLighting);
    }

    private void ComputeEntityLightSet(
        in RenderInstanceCandidate entity)
    {
        _currentEntityIndoor =
            IndoorObjectReceivesTorches(entity.ParentCell);

        _currentEntityLightSet = InstanceLightSet.Disabled;
        var snap = _pointSnapshot;
        if (snap is null || snap.Count == 0) return;

        if (!_currentEntityIndoor) return;

        Vector3 center =
            (entity.Bounds.Minimum + entity.Bounds.Maximum) * 0.5f;
        float radius =
            (entity.Bounds.Maximum - entity.Bounds.Minimum).Length() * 0.5f;
        Array.Fill(_currentEntityLightSetScratch, -1);
        LightManager.SelectForObject(snap, center, radius, _currentEntityLightSetScratch);
        _currentEntityLightSet = InstanceLightSet.From(_currentEntityLightSetScratch);
    }

    internal static bool IndoorObjectReceivesTorches(uint? parentCellId)
        => parentCellId.HasValue
           && (parentCellId.Value & 0xFFFFu) >= 0x0100u
           && (parentCellId.Value & 0xFFFFu) != 0xFFFFu;   // 0xFFFF = landblock marker, not an EnvCell → outdoor

    private void AppendCurrentLightSet(InstanceGroup grp)
    {
        grp.LightSets.Add(_currentEntityLightSet);
        grp.IndoorFlags.Add(_currentEntityIndoor ? 1u : 0u);
        grp.DetailCategories.Add(_currentEntityBuildingDetail ? 1u : 0u);
    }

    private bool ClassifyBatches(
        ObjectRenderData renderData,
        Matrix4x4 model,
        in RenderInstanceCandidate entity,
        MeshRef meshRef,
        PaletteCompositeIdentity paletteIdentity,
        Matrix4x4 restPose,
        float opacityMultiplier = 1.0f,
        List<CachedBatch>? collector = null,
        bool? entityHasCutoutSubsetOverride = null)
    {
        if (_meshAdapter.IsRuntimeHiddenMarker(meshRef.GfxObjId))
            return true;

        bool entityHasCutoutSubset = entityHasCutoutSubsetOverride ?? renderData.HasCutoutSubset;
        bool allTexturesReady = true;
        for (int batchIdx = 0; batchIdx < renderData.Batches.Count; batchIdx++)
        {
            bool survives = TryClassifyBatch(
                renderData, batchIdx, in entity, meshRef, paletteIdentity,
                opacityMultiplier, entityHasCutoutSubset,
                out GroupKey key, out bool compositePending);
            if (compositePending)
                allTexturesReady = false;
            if (!survives)
                continue;
            GpuTextureSlot texSlot = key.TextureSlot;

            InstanceGroup grp = GetOrCreateInstanceGroup(key);
            grp.Matrices.Add(model);
            grp.SubmissionOrders.Add(_nextInstanceSubmissionOrder++);
            grp.Slots.Add(_currentEntitySlot);
            AppendCurrentLightSet(grp);        // Fix B — 8 ints per instance, parallel to Matrices
            grp.Opacities.Add(opacityMultiplier);
            grp.SelectionLighting.Add(_currentEntitySelectionLighting);
            collector?.Add(new CachedBatch(
                key,
                texSlot,
                restPose,
                grp,
                grp.Registration));
        }
        return allTexturesReady;
    }

    private readonly record struct ResolvedTexture(GpuTextureSlot Slot, uint Layer);

    private ResolvedTexture ResolveTexture(
        in RenderInstanceCandidate entity,
        MeshRef meshRef,
        ObjectRenderBatch batch,
        PaletteCompositeIdentity paletteIdentity,
        out bool compositePending) =>
        ResolveTexture(
            entity.LocalEntityId,
            entity.PaletteOverride,
            meshRef,
            batch,
            paletteIdentity,
            out compositePending);

    private ResolvedTexture ResolveTexture(
        WorldEntity entity,
        MeshRef meshRef,
        ObjectRenderBatch batch,
        PaletteCompositeIdentity paletteIdentity,
        out bool compositePending) =>
        ResolveTexture(
            entity.Id,
            entity.PaletteOverride,
            meshRef,
            batch,
            paletteIdentity,
            out compositePending);

    private ResolvedTexture ResolveTexture(
        uint localEntityId,
        PaletteOverride? paletteOverride,
        MeshRef meshRef,
        ObjectRenderBatch batch,
        PaletteCompositeIdentity paletteIdentity,
        out bool compositePending)
    {
        compositePending = false;
        uint surfaceId = batch.Key.SurfaceId;
        if (surfaceId == 0 || surfaceId == 0xFFFFFFFF)
            return default;

        uint overrideOrigTex = 0;
        bool hasOrigTexOverride = meshRef.SurfaceOverrides is not null
            && meshRef.SurfaceOverrides.TryGetValue(surfaceId, out overrideOrigTex)
            && overrideOrigTex != 0;
        uint? origTexOverride = hasOrigTexOverride ? overrideOrigTex : (uint?)null;

        bool sourceIsPaletteIndexed = paletteOverride is not null
            && _textures.IsPaletteIndexed(surfaceId, origTexOverride);
        WbTextureResolutionKind resolution = WbTextureResolutionPolicy.Select(
            hasOrigTexOverride,
            paletteOverride is not null,
            sourceIsPaletteIndexed);

        switch (resolution)
        {
            case WbTextureResolutionKind.PaletteComposite:
            {
                BindlessTextureLocation texture =
                    _textures.GetOrUploadWithPaletteOverrideBindless(
                        localEntityId,
                        surfaceId,
                        origTexOverride,
                        paletteOverride!,
                        paletteIdentity);
                compositePending = !texture.IsResolved;
                return new ResolvedTexture(texture.ResolveSlot(batch.HasWrappingUVs), texture.Layer);
            }

            case WbTextureResolutionKind.OriginalTextureOverride:
            {
                BindlessTextureLocation texture =
                    _textures.GetOrUploadWithOrigTextureOverrideBindless(
                        localEntityId,
                        surfaceId,
                        overrideOrigTex);
                compositePending = !texture.IsResolved;
                return new ResolvedTexture(texture.ResolveSlot(batch.HasWrappingUVs), texture.Layer);
            }

            case WbTextureResolutionKind.SharedAtlas:
                return new ResolvedTexture(
                    batch.TextureSlot,
                    checked((uint)batch.TextureIndex));

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution));
        }
    }

    private static void WriteMatrix(float[] buf, int offset, in Matrix4x4 m)
    {
        buf[offset + 0]  = m.M11; buf[offset + 1]  = m.M12; buf[offset + 2]  = m.M13; buf[offset + 3]  = m.M14;
        buf[offset + 4]  = m.M21; buf[offset + 5]  = m.M22; buf[offset + 6]  = m.M23; buf[offset + 7]  = m.M24;
        buf[offset + 8]  = m.M31; buf[offset + 9]  = m.M32; buf[offset + 10] = m.M33; buf[offset + 11] = m.M34;
        buf[offset + 12] = m.M41; buf[offset + 13] = m.M42; buf[offset + 14] = m.M43; buf[offset + 15] = m.M44;
    }

    private static bool EntityMatchesSet(WorldEntity entity, EntitySet set) => true;

    internal static bool EntityPassesVisibleCellGate(
        WorldEntity entity,
        HashSet<uint>? visibleCellIds,
        EntitySet set)
    {
        if (visibleCellIds is null)
            return true;

        if (entity.ParentCellId.HasValue)
            return visibleCellIds.Contains(entity.ParentCellId.Value);

        return false;
    }

    private static bool IsShellScopedSet(EntitySet set) => false;

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        try
        {
            if (_disposeResources is null)
            {
                var releases = new List<(string Name, Action Release)>();
                DisposeRhiResources();
                _disposeResources = new RetryableResourceReleaseLedger(releases);
            }

            ResourceReleaseAttempt attempt = _disposeResources.Advance();
            if (!_disposeResources.IsComplete)
            {
                throw attempt.ToException(
                    "One or more entity renderer resources could not be released.");
            }

            CompleteDispose();
            _disposeResources = null;
            _disposed = true;

            if (attempt.HasFailures)
            {
                throw attempt.ToException(
                    "Entity renderer resources released with exceptional committed outcomes.");
            }
        }
        finally
        {
            _disposing = false;
        }
    }

    private void CompleteDispose()
    {
        _dynamicFrameStarted = false;
    }


    public const int DrawCommandStride = 20; // sizeof(DrawElementsIndirectCommand): 5 × uint

    public readonly record struct IndirectGroupInput(
        int IndexCount,
        uint FirstIndex,
        int BaseVertex,
        int InstanceCount,
        int FirstInstance,
        uint TextureIndex,
        uint TextureLayer,
        TranslucencyKind Translucency,
        RetailSetSurfaceMaterialState MaterialState,
        float SurfaceOpacity = 1f,
        CullMode CullMode = CullMode.CounterClockwise,
        uint FoliageFlags = 0u);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BatchDataPublic
    {
        public uint TextureIndex;
        public float SurfaceOpacity;
        public uint TextureLayer;
        public uint Flags;
    }

    public readonly record struct IndirectLayoutResult(
        int OpaqueCount,
        int TransparentCount,
        int TransparentByteOffset);

    public static IndirectLayoutResult BuildIndirectArrays(
        IReadOnlyList<IndirectGroupInput> groups,
        DrawElementsIndirectCommand[] indirectScratch,
        BatchDataPublic[] batchScratch,
        CullMode[]? cullScratch = null)
    {
        int opaqueCount = 0;
        int transparentCount = 0;

        foreach (var g in groups)
        {
            if (IsOpaque(g.Translucency)) opaqueCount++;
            else transparentCount++;
        }

        int oi = 0;
        int ti = opaqueCount;

        foreach (var g in groups)
        {
            var dec = new DrawElementsIndirectCommand
            {
                Count         = (uint)g.IndexCount,
                InstanceCount = (uint)g.InstanceCount,
                FirstIndex    = g.FirstIndex,
                BaseVertex    = g.BaseVertex,
                BaseInstance  = (uint)g.FirstInstance,
            };
            var bd = new BatchDataPublic
            {
                TextureIndex = g.TextureIndex,
                SurfaceOpacity = g.SurfaceOpacity,
                TextureLayer = g.TextureLayer,
                Flags        = 1u | g.FoliageFlags,
            };

            if (IsOpaque(g.Translucency))
            {
                indirectScratch[oi] = dec;
                batchScratch[oi]    = bd;
                if (cullScratch is not null) cullScratch[oi] = g.CullMode;
                oi++;
            }
            else
            {
                indirectScratch[ti] = dec;
                batchScratch[ti]    = bd;
                if (cullScratch is not null) cullScratch[ti] = g.CullMode;
                ti++;
            }
        }

        return new IndirectLayoutResult(opaqueCount, transparentCount, opaqueCount * DrawCommandStride);
    }

    public static bool IsOpaquePublic(TranslucencyKind t) => IsOpaque(t);

    internal readonly record struct DetailCommandRun(
        int FirstCommand,
        int CommandCount);

    internal static bool TryGetNextDetailCommandRun(
        ReadOnlySpan<DrawElementsIndirectCommand> commands,
        ReadOnlySpan<uint> detailCategories,
        int searchStart,
        int exclusiveEnd,
        out DetailCommandRun run)
    {
        if (searchStart < 0
            || exclusiveEnd < searchStart
            || exclusiveEnd > commands.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchStart),
                "The requested detail-command search range is invalid.");
        }

        int first = searchStart;
        while (first < exclusiveEnd
            && !CommandContainsDetailCategory(
                commands[first],
                detailCategories))
        {
            first++;
        }
        if (first == exclusiveEnd)
        {
            run = default;
            return false;
        }

        int end = first + 1;
        while (end < exclusiveEnd
            && CommandContainsDetailCategory(
                commands[end],
                detailCategories))
        {
            end++;
        }
        run = new DetailCommandRun(first, end - first);
        return true;
    }

    internal static bool CommandContainsDetailCategory(
        DrawElementsIndirectCommand command,
        ReadOnlySpan<uint> detailCategories)
    {
        ulong first = command.BaseInstance;
        ulong end = first + command.InstanceCount;
        if (end > (ulong)detailCategories.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "The indirect instance range exceeds the detail-category buffer.");
        }

        for (ulong index = first; index < end; index++)
        {
            if (detailCategories[(int)index] != 0u)
                return true;
        }
        return false;
    }

    private static bool IsOpaque(TranslucencyKind t)
        => t == TranslucencyKind.Opaque || t == TranslucencyKind.ClipMap;

    // ────────────────────────────────────────────────────────────────────────

    internal sealed class InstanceGroup
    {
        public long Registration;
        public long LastUsedFrame;
        public uint FirstIndex;
        public int BaseVertex;
        public int IndexCount;
        public AcDream.App.Rendering.Gpu.GpuTextureSlot TextureSlot =
            AcDream.App.Rendering.Gpu.GpuTextureSlot.Unassigned;
        public uint TextureLayer;
        public TranslucencyKind Translucency;
        public RetailSetSurfaceMaterialState MaterialState =
            RetailSetSurfaceMaterialState.Opaque;
        public float SurfaceOpacity = 1f;
        public CullMode CullMode;
        public int FirstInstance;   // offset into the shared instance VBO (in instances, not bytes)
        public int InstanceCount;

        public uint FoliageFlags;

        public float SortDistance;
        public readonly List<Matrix4x4> Matrices = new();

        public readonly List<int> SubmissionOrders = new();

        public readonly List<uint> Slots = new();

        public readonly List<InstanceLightSet> LightSets = new();

        public readonly List<uint> IndoorFlags = new();

        public readonly List<uint> DetailCategories = new();

        public readonly List<float> Opacities = new();

        public readonly List<Vector2> SelectionLighting = new();

        public void ClearPerInstanceData()
        {
            Matrices.Clear();
            SubmissionOrders.Clear();
            Slots.Clear();
            LightSets.Clear();
            IndoorFlags.Clear();
            DetailCategories.Clear();
            Opacities.Clear();
            SelectionLighting.Clear();
        }

        public void ReleasePerInstanceStorage()
        {
            ClearPerInstanceData();
            Matrices.TrimExcess();
            SubmissionOrders.TrimExcess();
            Slots.TrimExcess();
            LightSets.TrimExcess();
            IndoorFlags.TrimExcess();
            DetailCategories.TrimExcess();
            Opacities.TrimExcess();
            SelectionLighting.TrimExcess();
        }
    }
}
