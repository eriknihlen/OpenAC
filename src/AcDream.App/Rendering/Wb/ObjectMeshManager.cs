using Chorizite.Core.Lib;
using Chorizite.Core.Render;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using CullMode = DatReaderWriter.Enums.CullMode;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AcDream.Content;
using AcDream.App.Rendering.Residency;
using AcDream.Core.Rendering.Wb;
using BoundingBox = Chorizite.Core.Lib.BoundingBox;

namespace AcDream.App.Rendering.Wb
{
    public class ObjectRenderData
    {
        public uint VAO { get; set; }
        public uint VBO { get; set; }
        public int VertexCount { get; set; }
        public List<ObjectRenderBatch> Batches { get; set; } = new();

        public bool HasCutoutSubset { get; set; }

        internal GlobalMeshAllocation? GlobalAllocation { get; set; }
        public bool IsSetup { get; set; }
        public List<(ulong GfxObjId, Matrix4x4 Transform)> SetupParts { get; set; } = new();

        /// <summary>Particle emitters from physics scripts.</summary>
        public List<StagedEmitter> ParticleEmitters { get; set; } = new();

        /// <summary>CPU-side vertex positions for raycasting.</summary>
        public Vector3[] CPUPositions { get; set; } = Array.Empty<Vector3>();

        /// <summary>CPU-side indices for raycasting.</summary>
        public ushort[] CPUIndices { get; set; } = Array.Empty<ushort>();

        /// <summary>CPU-side edge line vertices for Environment wireframe rendering.</summary>
        public Vector3[] CPUEdgeLines { get; set; } = Array.Empty<Vector3>();

        /// <summary>Local bounding box.</summary>
        public BoundingBox BoundingBox { get; set; }

        public Vector3 SortCenter { get; set; }

        /// <summary>DataID of a simpler GfxObj to use at long distance / low quality, or GfxObjDegradeInfo.</summary>
        public uint DIDDegrade { get; set; }

        /// <summary>Sphere used for mouse selection.</summary>
        public Sphere? SelectionSphere { get; set; }

        /// <summary>Estimated GPU memory usage in bytes.</summary>
        public long MemorySize { get; set; }

        internal long NonArenaGpuBytes { get; set; }
    }

    /// <summary>
    /// A single GPU draw batch: IBO + texture array layer.
    /// </summary>
    public class ObjectRenderBatch
    {
        public uint IBO { get; set; }
        public int IndexCount { get; set; }
        public TextureAtlasManager Atlas { get; set; } = null!;
        public int TextureIndex { get; set; }
        public (int Width, int Height) TextureSize { get; set; }
        public TextureFormat TextureFormat { get; set; }
        public uint SurfaceId { get; set; }
        public byte RetailSurfaceMask { get; set; }
        public TextureKey Key { get; set; }
        public DatReaderWriter.Enums.CullMode CullMode { get; set; }
        public AcDream.Core.Meshing.TranslucencyKind Translucency { get; set; }
        public AcDream.Core.Meshing.RetailSetSurfaceMaterialState MaterialState { get; set; }
            = AcDream.Core.Meshing.RetailSetSurfaceMaterialState.Opaque;
        public bool IsTransparent { get; set; }
        public bool IsAdditive { get; set; }
        public bool HasWrappingUVs { get; set; }
        /// <summary>Authored material alpha; base texture alpha is separate.</summary>
        public float SurfaceOpacity { get; set; } = 1f;

        // Modern rendering path fields
        public uint FirstIndex { get; set; }
        public uint BaseVertex { get; set; }

        internal AcDream.App.Rendering.Gpu.GpuTextureSlot TextureSlot { get; set; }
            = AcDream.App.Rendering.Gpu.GpuTextureSlot.Unassigned;
    }

    public class ObjectMeshManager : IDisposable
    {
        private readonly IMeshPipelineDevice _graphicsDevice;

        private readonly IPreparedAssetSource _preparedAssets;
        private readonly ILogger _logger;

        private readonly AcDream.App.Rendering.Gpu.IGpuDevice _gpuDevice;

        private readonly IWorldTextureArrayFactory _atlasArrays;


        public bool IsDisposed { get; private set; }
        private readonly object _disposeGate = new();
        private bool _disposeCompleted;
        private bool _disposeRunning;
        private bool _workersQuiesced;
        private bool _workSignalDisposed;
        private readonly ConcurrentDictionary<ulong, ObjectRenderData> _renderData = new();
        private long _renderDataAvailabilityVersion;
        private readonly Dictionary<ulong, ObjectReleaseTicket> _objectReleases = new();
        private readonly Queue<ulong> _objectReleaseQueue = new();
        private readonly Dictionary<ulong, RetryableResourceReleaseLedger> _uploadRollbacks = new();
        private readonly Queue<ulong> _uploadRollbackQueue = new();
        private readonly MeshOwnershipCounter _ownership = new();
        private readonly ConcurrentDictionary<ulong, Task<ObjectMeshData?>> _preparationTasks = new();

        private readonly LinkedList<ulong> _lruList = new();
        private readonly long _maxGpuMemory;
        private readonly int _maxCachedObjects;
        private long _currentNonArenaGpuMemory;

        // Shared atlases grouped by (Width, Height, Format)
        private readonly Dictionary<(int Width, int Height, TextureFormat Format), List<TextureAtlasManager>> _globalAtlases = new();
        private readonly HashSet<TextureAtlasManager> _dirtyAtlases = new();
        private long _atlasUseSequence;
        private const long RetainedEmptyAtlasBudgetBytes = 64L * 1024 * 1024;
        private const int RetainedEmptyAtlasCountLimit = 32;
        private readonly AcDream.App.Rendering.BoundedUnownedResourceCache<TextureAtlasManager>
            _safeEmptyAtlases = new(RetainedEmptyAtlasBudgetBytes, RetainedEmptyAtlasCountLimit);
        private readonly List<TextureAtlasManager> _retiringAtlases = [];


        private readonly CpuMeshUploadCache _cpuMeshCache;

        private readonly MeshUploadStagingQueue _stagedMeshData;
        private volatile bool _arenaBackpressured;

        public const int MaxUploadRetries = 3;

        internal bool UploadOrRequeue(MeshUploadQueueItem item)
        {
            ObjectMeshData meshData = item.Data;
            if (!_ownership.IsOwned(meshData.ObjectId))
            {
                _stagedMeshData.CompleteOrRestageIfOwned(item, _ownership);
                return false;
            }
            if (UploadMeshData(meshData) is not null)
            {
                _stagedMeshData.Complete(item);
                return false;                       // success (incl. legitimate 0-vertex → empty render data)
            }
            if (HasRenderData(meshData.ObjectId))
            {
                _stagedMeshData.Complete(item);
                return false;                       // raced to present by another path
            }
            if (_objectReleases.ContainsKey(meshData.ObjectId)
                || _uploadRollbacks.ContainsKey(meshData.ObjectId))
            {
                return true;
            }
            if (meshData.UploadAttempts < MaxUploadRetries)
                return true;                        // re-stage for next frame
            _stagedMeshData.Complete(item);
            Console.WriteLine($"[up-retry] 0x{meshData.ObjectId:X10} upload failed {meshData.UploadAttempts}x — giving up (was the #125 silent sticky drop; a GL error is being surfaced, not hidden)");
            return false;
        }

        internal bool TryDequeueStagedMeshData(out MeshUploadQueueItem item) =>
            _stagedMeshData.TryDequeue(out item);

        internal bool TryPeekStagedMeshData(out MeshUploadQueueItem item) =>
            _stagedMeshData.TryPeek(out item);

        internal int StagedMeshCount => _stagedMeshData.ClaimCount;
        internal long StagedMeshBytes => _stagedMeshData.ClaimedBytes;
        internal bool StagingAtHighWater => _stagedMeshData.IsAtHighWater;

        internal int DiscardUnownedStagedPrefix(int maximum) =>
            _stagedMeshData.DiscardUnownedPrefix(_ownership, maximum);

        internal bool IsOwned(ulong id) => _ownership.IsOwned(id);

        internal void RequeueStagedMeshData(MeshUploadQueueItem item) =>
            _stagedMeshData.Requeue(item);

        internal void RejectUnsupportedStagedUpload(
            MeshUploadQueueItem item,
            NotSupportedException error)
        {
            ArgumentNullException.ThrowIfNull(error);
            _stagedMeshData.Complete(item);
            lock (_pendingRequests)
                _terminalPreparationFailures.Add(item.Data.ObjectId);
            _logger.LogError(
                error,
                "Mesh 0x{Id:X10} generation {Generation} exceeds an explicit GPU upload limit",
                item.Data.ObjectId,
                item.Generation);
        }

        internal void SetArenaBackpressure(bool enabled)
        {
            _arenaBackpressured = enabled;
            if (!enabled)
                ResumePreparationWorkers();
        }

        public GlobalMeshBuffer? GlobalBuffer { get; }
        internal (int RenderData, int AtlasArrays, int UnusedLru, long EstimatedBytes) Diagnostics
        {
            get
            {
                int atlasArrays = 0;
                foreach (List<TextureAtlasManager> atlases in _globalAtlases.Values)
                    atlasArrays += atlases.Count;
                long atlasBytes = CalculateAtlasBytes(_globalAtlases.Values);
                long retiringAtlasBytes = CalculateAtlasBytes(_retiringAtlases);
                long physicalBytes = CalculateTrackedGpuBytes(
                    checked(
                        _currentNonArenaGpuMemory
                        + atlasBytes
                        + retiringAtlasBytes),
                    GlobalBuffer?.PhysicalCapacityBytes ?? 0);
                return (_renderData.Count, atlasArrays, _lruList.Count, physicalBytes);
            }
        }
        internal (int Count, long Bytes) CpuCacheDiagnostics =>
            (_cpuMeshCache.Count, _cpuMeshCache.ResidentBytes);

        internal CacheStats CpuMeshCacheStats => _cpuMeshCache.Stats;

        internal CacheStats DecodedTextureCacheStats =>
            _preparedAssets.DecodedTextureCacheStats;

        private sealed class PreparationRequest(
            PreparedAssetRequest asset,
            ObjectMeshData? cachedData,
            TaskCompletionSource<ObjectMeshData?> completion,
            CancellationTokenSource cancellation)
        {
            private readonly object _cancellationGate = new();
            private bool _cancelStarted;
            private bool _cancelInProgress;
            private bool _disposeRequested;
            private bool _cancellationDisposed;

            public PreparedAssetRequest Asset { get; } = asset;
            public ulong Id => Asset.RuntimeObjectId;
            public ObjectMeshData? CachedData { get; } = cachedData;
            public TaskCompletionSource<ObjectMeshData?> Completion { get; } = completion;
            public CancellationTokenSource Cancellation { get; } = cancellation;

            public void Cancel()
            {
                lock (_cancellationGate)
                {
                    if (_cancellationDisposed || _cancelStarted)
                        return;
                    _cancelStarted = true;
                    _cancelInProgress = true;
                }

                try
                {
                    Cancellation.Cancel();
                }
                finally
                {
                    bool dispose;
                    lock (_cancellationGate)
                    {
                        _cancelInProgress = false;
                        dispose = _disposeRequested && !_cancellationDisposed;
                        if (dispose)
                            _cancellationDisposed = true;
                    }
                    if (dispose)
                        Cancellation.Dispose();
                }
            }

            public void DisposeCancellation()
            {
                lock (_cancellationGate)
                {
                    if (_cancellationDisposed)
                        return;
                    if (_cancelInProgress)
                    {
                        _disposeRequested = true;
                        return;
                    }
                    _cancellationDisposed = true;
                }
                Cancellation.Dispose();
            }
        }

        private readonly LinkedList<PreparationRequest> _pendingRequests = new();
        private readonly Dictionary<ulong, LinkedListNode<PreparationRequest>> _pendingRequestById = new();
        private readonly Dictionary<ulong, PreparationRequest> _activePreparationById = new();
        private readonly Dictionary<ulong, EnvCellGeomRequest> _envCellDescriptors = new();
        private readonly HashSet<ulong> _terminalPreparationFailures = new();
        private readonly HashSet<Task> _workerTasks = new();
        private readonly ManualResetEventSlim _preparationWorkAvailable = new(false);
        private const int MaxParallelLoads = 4;

        internal enum PreparationWorkerWakeAction
        {
            Process,
            ResetAndWait,
            Exit,
        }

        internal static PreparationWorkerWakeAction DecidePreparationWorkerWake(
            bool isDisposed,
            bool hasPendingRequests,
            bool stagingAtHighWater,
            bool arenaBackpressured)
        {
            // Shutdown has priority over every ordinary idle/backpressure state.
            // Dispose sets one shared manual-reset signal for all persistent
            // workers; no worker may reset that signal before its peers wake.
            if (isDisposed)
                return PreparationWorkerWakeAction.Exit;
            if (!hasPendingRequests || stagingAtHighWater || arenaBackpressured)
                return PreparationWorkerWakeAction.ResetAndWait;
            return PreparationWorkerWakeAction.Process;
        }

        private sealed class ObjectReleaseTicket(
            ulong id,
            ObjectRenderData data,
            long reclaimableBytes,
            RetryableResourceReleaseLedger resources)
        {
            public ulong Id { get; } = id;
            public ObjectRenderData Data { get; } = data;
            public long ReclaimableBytes { get; } = reclaimableBytes;
            public RetryableResourceReleaseLedger Resources { get; } = resources;
            public bool IsQueued { get; set; }
        }

        internal ObjectMeshManager(
            IMeshPipelineDevice graphicsDevice,
            AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
            IPreparedAssetSource preparedAssets,
            ILogger<ObjectMeshManager> logger,
            ResidencyBudgetOptions? budgets = null)
        {
            budgets ??= ResidencyBudgetOptions.Default;
            _graphicsDevice = graphicsDevice
                ?? throw new ArgumentNullException(nameof(graphicsDevice));
            ArgumentNullException.ThrowIfNull(gpuDevice);
            _gpuDevice = gpuDevice;
            _atlasArrays = IWorldTextureArrayFactory.For(
                graphicsDevice,
                gpuDevice,
                logger ?? throw new ArgumentNullException(nameof(logger)));
            _preparedAssets = preparedAssets
                ?? throw new ArgumentNullException(nameof(preparedAssets));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
            _maxGpuMemory = budgets.ObjectMeshGpuBytes;
            _maxCachedObjects = budgets.ObjectMeshUnownedEntries;
            _cpuMeshCache = new CpuMeshUploadCache(
                budgets.PreparedMeshCpuEntries,
                budgets.PreparedMeshCpuBytes);
            _stagedMeshData = new MeshUploadStagingQueue(
                budgets.MeshStagingEntries,
                budgets.MeshStagingBytes);
            if (_graphicsDevice.HasOpenGL43 && _graphicsDevice.HasBindless)
            {
                GlobalBuffer = new GlobalMeshBuffer(
                    gpuDevice,
                    _graphicsDevice.ResourceRetirement);
            }
        }

        public ObjectRenderData? GetRenderData(ulong id)
        {
            if (!_objectReleases.ContainsKey(id)
                && _renderData.TryGetValue(id, out var data))
            {
                IncrementRefCount(id);

                return data;
            }
            return null;
        }

        public bool HasRenderData(ulong id) =>
            !_objectReleases.ContainsKey(id)
            && _renderData.ContainsKey(id);

        public ObjectRenderData? TryGetRenderData(ulong id)
        {
            return !_objectReleases.ContainsKey(id)
                && _renderData.TryGetValue(id, out var data)
                    ? data
                    : null;
        }

        public long RenderDataAvailabilityVersion => Volatile.Read(ref _renderDataAvailabilityVersion);

        private void MarkRenderDataAvailabilityChanged() =>
            Interlocked.Increment(ref _renderDataAvailabilityVersion);

        public void IncrementRefCount(ulong id)
        {
            lock (_pendingRequests)
            {
                _ownership.Acquire(id);
                lock (_lruList)
                {
                    _lruList.Remove(id);
                }
            }
        }

        public (int Arrays, long Bytes) GenerateMipmaps()
        {
            int generatedArrays = 0;
            long generatedBytes = 0;
            foreach (TextureAtlasManager atlas in _dirtyAtlases)
            {
                long bytes = atlas.TextureArray.ProcessDirtyUpdates();
                if (bytes > 0)
                {
                    generatedArrays++;
                    generatedBytes = checked(generatedBytes + bytes);
                }
            }
            _dirtyAtlases.Clear();
            return (generatedArrays, generatedBytes);
        }

        internal bool EvictOneEmptyAtlas()
        {
            RetryRetiringAtlasDisposals();
            if (!_safeEmptyAtlases.TryTakeOldestOverBudget(out TextureAtlasManager victim))
                return false;

            var key = (victim.Width, victim.Height, victim.Format);
            if (_globalAtlases.TryGetValue(key, out List<TextureAtlasManager>? list))
            {
                list.Remove(victim);
                if (list.Count == 0)
                    _globalAtlases.Remove(key);
            }
            _dirtyAtlases.Remove(victim);
            _retiringAtlases.Add(victim);
            victim.Dispose();
            RemoveCompletedAtlasRetirements();
            return true;
        }

        private void RetryRetiringAtlasDisposals()
        {
            for (int i = 0; i < _retiringAtlases.Count; i++)
                _retiringAtlases[i].Dispose();
            RemoveCompletedAtlasRetirements();
        }

        private void RemoveCompletedAtlasRetirements()
        {
            for (int i = _retiringAtlases.Count - 1; i >= 0; i--)
            {
                if (!_retiringAtlases[i].IsPhysicalRetirementComplete)
                    continue;
                _retiringAtlases[i].TextureArray.ReleaseTextureSlots();
                _retiringAtlases.RemoveAt(i);
            }
        }

        private void OnAtlasGpuSafeEmpty(TextureAtlasManager atlas)
        {
            if (IsDisposed || !atlas.IsGpuSafeEmpty || _safeEmptyAtlases.Contains(atlas))
                return;
            _safeEmptyAtlases.MarkUnowned(atlas, atlas.AllocatedBytes);
        }

        private void MarkAtlasActive(TextureAtlasManager atlas) => _safeEmptyAtlases.MarkOwned(atlas);

        public (int PendingUpdates, int ArraysWithPending, int TotalArrays) GetPendingTextureUpdateStats()
        {
            int pending = 0, arraysWith = 0, total = 0;
            foreach (var atlasList in _globalAtlases.Values)
            {
                foreach (var atlas in atlasList)
                {
                    total++;
                    int p = atlas.TextureArray.PendingUpdateCount;
                    if (p > 0) { arraysWith++; pending += p; }
                }
            }
            return (pending, arraysWith, total);
        }

        public void DecrementRefCount(ulong id)
        {
            (PreparationRequest? Pending, PreparationRequest? Active) canceled = default;
            bool finalOwner;
            lock (_pendingRequests)
            {
                finalOwner = _ownership.Count(id) <= 1;
                if (finalOwner)
                    canceled = DetachPendingPreparationLocked(id);
            }

            if (finalOwner)
                CancelDetachedPreparation(canceled);

            lock (_pendingRequests)
            {
                int newCount = _ownership.Release(id);
                if (newCount > 0)
                    return;

                _envCellDescriptors.Remove(id);
                _terminalPreparationFailures.Remove(id);
                if (_renderData.ContainsKey(id))
                {
                    // Instead of unloading, move resident data to LRU.
                    lock (_lruList)
                    {
                        _lruList.Remove(id);
                        _lruList.AddLast(id);
                    }
                }
                else
                {
                    _ownership.Remove(id);
                }
            }
        }

        public void ReleaseRenderData(ulong id)
        {
            (PreparationRequest? Pending, PreparationRequest? Active) canceled = default;
            lock (_pendingRequests)
            {
                if (_ownership.IsOwned(id))
                {
                    var newCount = _ownership.Release(id);
                    if (newCount <= 0)
                    {
                        _envCellDescriptors.Remove(id);
                        _terminalPreparationFailures.Remove(id);
                        canceled = DetachPendingPreparationLocked(id);
                        if (_renderData.ContainsKey(id))
                        {
                            lock (_lruList)
                            {
                                _lruList.Remove(id);
                                _lruList.AddLast(id);
                            }
                        }
                        else
                        {
                            _ownership.Remove(id);
                        }
                    }
                }
            }
            CancelDetachedPreparation(canceled);
        }

        internal (int Count, long Bytes) ReclaimUnusedResources(
            int maximumCount,
            long maximumBytes,
            bool forceArenaReclamation = false)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            RetryRetiringAtlasDisposals();
            RetryPendingAtlasRetirements();
            // Rollbacks own unpublished resources and therefore have no LRU
            // node to wake them. Advance a bounded snapshot every render tick
            // even if the requesting owner disappeared after the upload failed.
            AdvancePendingUploadRollbacks(maximumCount);

            (int reclaimedCount, long reclaimedBytes) =
                AdvancePendingObjectReleases(maximumCount, maximumBytes);
            int candidateBudget;
            lock (_lruList)
                candidateBudget = _lruList.Count;
            int attemptedCandidates = 0;
            long trackedAtlasBytes = checked(
                CalculateAtlasBytes(_globalAtlases.Values)
                + CalculateAtlasBytes(_retiringAtlases));

            while (reclaimedCount < maximumCount
                && attemptedCandidates < candidateBudget)
            {
                ulong idToEvict;
                lock (_lruList)
                {
                    long physicalArenaBytes = GlobalBuffer?.PhysicalCapacityBytes ?? 0;
                    long nonArenaAndAtlasBytes = checked(
                        _currentNonArenaGpuMemory
                        + trackedAtlasBytes);
                    if (!forceArenaReclamation
                        && IsWithinGpuCacheBudget(
                            nonArenaAndAtlasBytes,
                            physicalArenaBytes,
                            _maxGpuMemory)
                        && _lruList.Count <= _maxCachedObjects)
                    {
                        break;
                    }

                    if (_lruList.Count == 0)
                        break;
                    idToEvict = _lruList.First!.Value;
                    _lruList.RemoveFirst();
                }
                attemptedCandidates++;

                lock (_pendingRequests)
                {
                    if (!_ownership.IsOwned(idToEvict))
                    {
                        long bytes = GetObjectReclaimableBytes(idToEvict);
                        if (!FitsReclamationBudget(bytes, reclaimedBytes, maximumBytes))
                        {
                            lock (_lruList)
                                _lruList.AddLast(idToEvict);
                            continue;
                        }

                        if (TryAdvanceObjectRelease(idToEvict, out long completedBytes))
                        {
                            reclaimedBytes = checked(reclaimedBytes + completedBytes);
                            reclaimedCount++;
                        }
                        // An unfinished release moves from the ordinary LRU to
                        // _objectReleases. That dedicated queue advances once
                        // at the start of a later frame, including when a new
                        // logical owner acquired the id in the meantime.
                    }
                }
            }

            return (reclaimedCount, reclaimedBytes);
        }

        internal static bool FitsReclamationBudget(
            long candidateBytes,
            long alreadyReclaimedBytes,
            long maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(candidateBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(alreadyReclaimedBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            return candidateBytes <= maximumBytes - Math.Min(alreadyReclaimedBytes, maximumBytes);
        }

        private void RetryPendingAtlasRetirements()
        {
            List<Exception>? failures = null;
            foreach (List<TextureAtlasManager> atlases in _globalAtlases.Values)
            {
                for (int i = 0; i < atlases.Count; i++)
                {
                    try { atlases[i].RetryPendingRetirements(); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
            }
            if (failures is not null)
            {
                throw new AggregateException(
                    "One or more texture-atlas layer retirements could not be published.",
                    failures);
            }
        }

        internal bool EvictOneOldResource() =>
            ReclaimUnusedResources(1, long.MaxValue).Count != 0;

        private long GetReclaimableBytes(ObjectRenderData data)
        {
            if (data.GlobalAllocation is { } allocation)
            {
                return checked(
                    (long)allocation.Vertices.Length * VertexPositionNormalTexture.Size
                    + (long)allocation.Indices.Length * sizeof(ushort));
            }
            return Math.Max(0, data.NonArenaGpuBytes);
        }

        public void EvictAllUnused()
        {
            AdvancePendingUploadRollbacks(Math.Max(1, _uploadRollbackQueue.Count));
            AdvancePendingObjectReleases(
                Math.Max(1, _objectReleaseQueue.Count),
                long.MaxValue);

            int candidateBudget;
            lock (_lruList)
                candidateBudget = _lruList.Count;

            for (int attempted = 0; attempted < candidateBudget; attempted++)
            {
                ulong idToEvict;
                lock (_lruList)
                {
                    if (_lruList.Count == 0)
                        break;
                    idToEvict = _lruList.First!.Value;
                    _lruList.RemoveFirst();
                }

                lock (_pendingRequests)
                {
                    if (!_ownership.IsOwned(idToEvict))
                    {
                        TryAdvanceObjectRelease(idToEvict, out _);
                    }
                }
            }

            _cpuMeshCache.Clear();
        }

        public struct EnvCellGeomRequest
        {
            public uint SourceCellId;
            public uint EnvironmentId;
            public ushort CellStructure;
            public List<ushort> Surfaces;
        }

        public Task<ObjectMeshData?> PrepareEnvCellGeomMeshDataAsync(
            ulong geomId,
            uint sourceCellId,
            uint environmentId,
            ushort cellStructure,
            List<ushort> surfaces,
            CancellationToken ct = default)
        {
            if (IsDisposed || HasRenderData(geomId)) return Task.FromResult<ObjectMeshData?>(null);

            var envCell = new EnvCellGeomRequest
            {
                SourceCellId = sourceCellId,
                EnvironmentId = environmentId,
                CellStructure = cellStructure,
                Surfaces = surfaces
            };
            lock (_pendingRequests)
            {
                _envCellDescriptors[geomId] = envCell;
                // An explicit schema-bearing schedule is a new opportunity to
                // prepare this immutable DAT object (for example, a later
                // landblock sharing geometry after an earlier failure).
                _terminalPreparationFailures.Remove(geomId);
            }

            ObjectMeshData? deferredCachedData = null;
            if (_cpuMeshCache.TryGetAndStageOwned(
                    geomId,
                    _stagedMeshData,
                    _ownership,
                    out ObjectMeshData? cachedData,
                    out MeshStageResult cacheStage))
            {
                if (cacheStage != MeshStageResult.HighWater)
                    return Task.FromResult(cachedData);
                deferredCachedData = cachedData;
            }

            lock (_pendingRequests)
            {
                if (_preparationTasks.TryGetValue(geomId, out Task<ObjectMeshData?>? existing)
                    && !existing.IsFaulted
                    && !existing.IsCanceled)
                {
                    bool canceledActiveGeneration =
                        _activePreparationById.TryGetValue(geomId, out PreparationRequest? active)
                        && active.Cancellation.IsCancellationRequested
                        && ReferenceEquals(existing, active.Completion.Task);
                    if (!canceledActiveGeneration)
                        return existing;
                }
                _preparationTasks.TryRemove(geomId, out _);

                var tcs = new TaskCompletionSource<ObjectMeshData?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task<ObjectMeshData?> task = tcs.Task;
                if (IsDisposed)
                {
                    tcs.TrySetCanceled();
                    return task;
                }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _preparationTasks[geomId] = task;
                var request = new PreparationRequest(
                    PreparedAssetRequest.EnvCellGeometry(
                        sourceCellId,
                        geomId,
                        environmentId,
                        cellStructure,
                        surfaces),
                    deferredCachedData,
                    tcs,
                    cancellation);
                _pendingRequestById.Add(geomId, _pendingRequests.AddLast(request));
                StartPreparationWorkersLocked();
                return task;
            }
        }

        public Task<ObjectMeshData?> PrepareMeshDataAsync(ulong id, bool isSetup, CancellationToken ct = default)
        {
            if (IsDisposed || HasRenderData(id)) return Task.FromResult<ObjectMeshData?>(null);

            lock (_pendingRequests)
                _terminalPreparationFailures.Remove(id);

            ObjectMeshData? deferredCachedData = null;
            if (_cpuMeshCache.TryGetAndStageOwned(
                    id,
                    _stagedMeshData,
                    _ownership,
                    out ObjectMeshData? cachedData,
                    out MeshStageResult cacheStage))
            {
                if (cacheStage != MeshStageResult.HighWater)
                    return Task.FromResult(cachedData);
                deferredCachedData = cachedData;
            }

            lock (_pendingRequests)
            {
                if (_preparationTasks.TryGetValue(id, out Task<ObjectMeshData?>? existing)
                    && !existing.IsFaulted
                    && !existing.IsCanceled)
                {
                    bool canceledActiveGeneration =
                        _activePreparationById.TryGetValue(id, out PreparationRequest? active)
                        && active.Cancellation.IsCancellationRequested
                        && ReferenceEquals(existing, active.Completion.Task);
                    if (!canceledActiveGeneration)
                        return existing;
                }
                _preparationTasks.TryRemove(id, out _);

                var tcs = new TaskCompletionSource<ObjectMeshData?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task<ObjectMeshData?> task = tcs.Task;
                if (IsDisposed)
                {
                    tcs.TrySetCanceled();
                    return task;
                }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _preparationTasks[id] = task;
                EnvCellGeomRequest? envCell = _envCellDescriptors.TryGetValue(id, out EnvCellGeomRequest descriptor)
                    ? descriptor
                    : null;
                if (envCell is null && id > uint.MaxValue)
                {
                    var confused = new InvalidOperationException(
                        $"Packed EnvCell geometry id 0x{id:X16} reached the "
                        + "Setup/GfxObj preparation arm without a registered "
                        + "descriptor (#339). Callers must route packed ids "
                        + "through PrepareEnvCellGeomMeshDataAsync or treat "
                        + "them as not-ready until the scheduler registers "
                        + "them.");
                    tcs.TrySetException(confused);
                    _terminalPreparationFailures.Add(id);
                    return task;
                }
                PreparedAssetRequest asset = envCell is { } env
                    ? PreparedAssetRequest.EnvCellGeometry(
                        env.SourceCellId,
                        id,
                        env.EnvironmentId,
                        env.CellStructure,
                        env.Surfaces)
                    : isSetup
                        ? PreparedAssetRequest.Setup(checked((uint)id))
                        : PreparedAssetRequest.GfxObj(checked((uint)id));
                var request = new PreparationRequest(
                    asset,
                    deferredCachedData,
                    tcs,
                    cancellation);
                _pendingRequestById.Add(id, _pendingRequests.AddLast(request));
                StartPreparationWorkersLocked();
                return task;
            }
        }

        private void ProcessQueue()
        {
            while (true)
            {
                _preparationWorkAvailable.Wait();
                PreparationRequest request;

                lock (_pendingRequests)
                {
                    PreparationWorkerWakeAction wakeAction = DecidePreparationWorkerWake(
                        IsDisposed,
                        _pendingRequests.Count != 0,
                        _stagedMeshData.IsAtHighWater,
                        _arenaBackpressured);
                    if (wakeAction == PreparationWorkerWakeAction.Exit)
                        return;
                    if (wakeAction == PreparationWorkerWakeAction.ResetAndWait)
                    {
                        _preparationWorkAvailable.Reset();
                        continue;
                    }

                    // LIFO: pick the most recently requested destination
                    // mesh without scanning/reordering the whole queue.
                    LinkedListNode<PreparationRequest>? node = _pendingRequests.Last;
                    while (node is not null && _activePreparationById.ContainsKey(node.Value.Id))
                        node = node.Previous;
                    if (node is null)
                    {
                        _preparationWorkAvailable.Reset();
                        continue;
                    }
                    request = node.Value;
                    _pendingRequests.Remove(node);
                    _pendingRequestById.Remove(request.Id);
                    _activePreparationById.Add(request.Id, request);
                }

                ulong id = request.Id;
                TaskCompletionSource<ObjectMeshData?> tcs = request.Completion;
                CancellationToken ct = request.Cancellation.Token;

                ObjectMeshData? completionResult = null;
                Exception? completionError = null;
                bool completionCanceled = false;
                CancellationToken completionCancellation = ct;

                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        completionCanceled = true;
                    }
                    else
                    {

                        ObjectMeshData? data = request.CachedData;
                        if (data is null)
                        {
                            PreparedAssetReadResult read =
                                _preparedAssets.Read(request.Asset, ct);
                            data = read.Data;
                            if (read.Status != PreparedAssetReadStatus.Loaded)
                            {
                                _logger.LogError(
                                    "Prepared mesh {Status} for {Type} source " +
                                    "0x{SourceId:X8}, runtime 0x{RuntimeId:X10}",
                                    read.Status,
                                    request.Asset.Type,
                                    request.Asset.SourceFileId,
                                    request.Asset.RuntimeObjectId);
                            }
                        }
                        if (ct.IsCancellationRequested)
                        {
                            completionCanceled = true;
                        }
                        else if (data != null)
                        {
                            _cpuMeshCache.Store(data);
                        }
                        if (!completionCanceled && data != null && _ownership.IsOwned(id))
                        {
                            _stagedMeshData.TryStage(data);
                        }
                        if (!completionCanceled)
                            completionResult = data;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    completionCanceled = true;
                    completionCancellation = ex.CancellationToken;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error preparing mesh data for 0x{Id:X8}", id);
                    completionError = ex;
                }
                finally
                {
                    lock (_pendingRequests)
                    {
                        if (_activePreparationById.TryGetValue(id, out PreparationRequest? active)
                            && ReferenceEquals(active, request))
                        {
                            _activePreparationById.Remove(id);
                        }
                        // Publish only after removing this generation from
                        // the active map. A replacement can never race into
                        // an id still occupied by the terminal generation.
                        if (completionError is not null)
                            tcs.TrySetException(completionError);
                        else if (completionCanceled)
                            tcs.TrySetCanceled(completionCancellation.IsCancellationRequested
                                ? completionCancellation
                                : default);
                        else
                            tcs.TrySetResult(completionResult);

                        if (_preparationTasks.TryGetValue(id, out Task<ObjectMeshData?>? current)
                            && ReferenceEquals(current, tcs.Task))
                        {
                            _preparationTasks.TryRemove(id, out _);
                        }
                        if (!completionCanceled && (completionError is not null || completionResult is null))
                            _terminalPreparationFailures.Add(id);
                        else if (completionResult is not null)
                            _terminalPreparationFailures.Remove(id);
                        if (!IsDisposed
                            && _pendingRequests.Count != 0
                            && !_stagedMeshData.IsAtHighWater
                            && !_arenaBackpressured)
                        {
                            _preparationWorkAvailable.Set();
                        }
                    }
                    request.DisposeCancellation();
                }
            }
        }

        private void StartPreparationWorkersLocked()
        {
            if (IsDisposed)
                return;

            while (_workerTasks.Count < MaxParallelLoads)
            {
                Task worker = Task.Run(ProcessQueue);
                _workerTasks.Add(worker);
                _ = worker.ContinueWith(
                    OnPreparationWorkerCompleted,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            if (_pendingRequests.Count != 0
                && !_stagedMeshData.IsAtHighWater
                && !_arenaBackpressured)
                _preparationWorkAvailable.Set();
        }

        private void OnPreparationWorkerCompleted(Task worker)
        {
            if (worker.IsFaulted)
                _logger.LogError(worker.Exception, "Mesh preparation worker terminated unexpectedly.");

            lock (_pendingRequests)
            {
                _workerTasks.Remove(worker);
                StartPreparationWorkersLocked();
            }
        }

        internal void ResumePreparationWorkers()
        {
            lock (_pendingRequests)
                StartPreparationWorkersLocked();
        }

        internal bool EnsureRenderDataReady(ulong id)
        {
            if (HasRenderData(id))
                return true;

            EnvCellGeomRequest? envCell;
            lock (_pendingRequests)
            {
                if (IsDisposed
                    || !_ownership.IsOwned(id)
                    || _terminalPreparationFailures.Contains(id))
                {
                    return false;
                }
                envCell = _envCellDescriptors.TryGetValue(id, out EnvCellGeomRequest descriptor)
                    ? descriptor
                    : null;
            }

            if (envCell is { } req)
            {
                _ = PrepareEnvCellGeomMeshDataAsync(
                    id,
                    req.SourceCellId,
                    req.EnvironmentId,
                    req.CellStructure,
                    req.Surfaces);
            }
            else if (id > uint.MaxValue)
            {
                return false;
            }
            else
            {
                _ = PrepareMeshDataAsync(id, isSetup: false);
            }
            return false;
        }

        public ObjectMeshData? PrepareMeshData(ulong id, bool isSetup, CancellationToken ct = default)
        {
            PreparedAssetRequest request = isSetup
                ? PreparedAssetRequest.Setup(checked((uint)id))
                : PreparedAssetRequest.GfxObj(checked((uint)id));
            return _preparedAssets.Read(request, ct).Data;
        }

        internal ResidencyDomainSnapshot CaptureObjectMeshResidency()
        {
            GlobalMeshBuffer? arena = GlobalBuffer;
            long nonArenaRetiring = 0;
            foreach (ObjectReleaseTicket ticket in _objectReleases.Values)
            {
                nonArenaRetiring = checked(
                    nonArenaRetiring
                    + Math.Max(0, ticket.Data.NonArenaGpuBytes));
            }
            nonArenaRetiring = Math.Min(
                nonArenaRetiring,
                _currentNonArenaGpuMemory);

            long arenaResident = arena?.ResidentCapacityBytes ?? 0;
            long arenaRequested = arena?.RequestedMigrationBytes ?? 0;
            long arenaRetiring = checked(
                (arena?.PendingRangeRetirementBytes ?? 0)
                + (arena?.RetiredBackingBytes ?? 0));
            long atlasResident = CalculateAtlasBytes(_globalAtlases.Values);
            long atlasRetiring = CalculateAtlasBytes(_retiringAtlases);
            return new ResidencyDomainSnapshot(
                ResidencyDomain.ObjectMeshes,
                EntryCount: checked(
                    _renderData.Count
                    + _globalAtlases.Values.Sum(static atlases => atlases.Count)
                    + _retiringAtlases.Count),
                OwnerCount: _ownership.TotalReferenceCount,
                Charges: new ResidencyCharges(
                    GpuRequestedBytes: arenaRequested,
                    GpuResidentBytes: checked(
                        _currentNonArenaGpuMemory
                        - nonArenaRetiring
                        + arenaResident
                        + atlasResident),
                    RetiringBytes: checked(
                        nonArenaRetiring
                        + arenaRetiring
                        + atlasRetiring)),
                BudgetBytes: _maxGpuMemory);
        }

        internal static long CalculateAtlasBytes(
            IEnumerable<IReadOnlyCollection<TextureAtlasManager>> atlasFamilies)
        {
            ArgumentNullException.ThrowIfNull(atlasFamilies);
            long bytes = 0;
            foreach (IReadOnlyCollection<TextureAtlasManager> family in atlasFamilies)
            {
                foreach (TextureAtlasManager atlas in family)
                    bytes = checked(bytes + atlas.AllocatedBytes);
            }
            return bytes;
        }

        internal static long CalculateAtlasBytes(
            IEnumerable<TextureAtlasManager> atlases)
        {
            ArgumentNullException.ThrowIfNull(atlases);
            long bytes = 0;
            foreach (TextureAtlasManager atlas in atlases)
                bytes = checked(bytes + atlas.AllocatedBytes);
            return bytes;
        }

        internal ResidencyDomainSnapshot CapturePreparedMeshResidency()
        {
            CacheStats stats = _cpuMeshCache.Stats;
            return new ResidencyDomainSnapshot(
                ResidencyDomain.PreparedMeshCpu,
                EntryCount: _cpuMeshCache.Count,
                OwnerCount: 0,
                Charges: new ResidencyCharges(
                    CpuPreparedBytes: _cpuMeshCache.ResidentBytes),
                BudgetBytes: _cpuMeshCache.ByteCapacity,
                Hits: stats.Hits,
                Misses: stats.Misses,
                Evictions: stats.Evictions);
        }

        internal ResidencyDomainSnapshot CaptureStagingResidency() =>
            new(
                ResidencyDomain.MeshStaging,
                EntryCount: _stagedMeshData.ClaimCount,
                OwnerCount: 0,
                Charges: new ResidencyCharges(
                    StagingBytes: _stagedMeshData.ClaimedBytes),
                BudgetBytes: _stagedMeshData.MaximumBytes);

        internal ResidencyDomainSnapshot CaptureGlobalArenaResidency()
        {
            GlobalMeshBuffer? arena = GlobalBuffer;
            return new ResidencyDomainSnapshot(
                ResidencyDomain.GlobalMeshArena,
                EntryCount: arena is null ? 0 : 2,
                OwnerCount: 0,
                Charges: ResidencyCharges.Zero,
                BudgetBytes: GlobalMeshBuffer.MaximumPhysicalArenaBytes,
                CapacityBytes: arena?.CapacityBytes ?? 0,
                UsedBytes: arena?.UsedBytes ?? 0,
                LargestFreeBytes: arena?.LargestFreeBytes ?? 0);
        }

        public void CancelStagedUploads(IEnumerable<ulong> ids)
        {
            foreach (ulong id in ids)
                CancelPendingPreparation(id);
        }

        private void CancelPendingPreparation(ulong id)
        {
            (PreparationRequest? Pending, PreparationRequest? Active) canceled;
            lock (_pendingRequests)
                canceled = DetachPendingPreparationLocked(id);
            CancelDetachedPreparation(canceled);
        }

        private (PreparationRequest? Pending, PreparationRequest? Active) DetachPendingPreparationLocked(ulong id)
        {
            PreparationRequest? pending = null;
            if (_pendingRequestById.Remove(id, out LinkedListNode<PreparationRequest>? node))
            {
                _pendingRequests.Remove(node);
                pending = node.Value;
                if (_preparationTasks.TryGetValue(id, out Task<ObjectMeshData?>? current)
                    && ReferenceEquals(current, pending.Completion.Task))
                {
                    _preparationTasks.TryRemove(id, out _);
                }
            }
            _activePreparationById.TryGetValue(id, out PreparationRequest? active);
            return (pending, active);
        }

        private static void CancelDetachedPreparation(
            (PreparationRequest? Pending, PreparationRequest? Active) canceled)
        {
            List<Exception>? failures = null;
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
            if (canceled.Pending is { } pending)
            {
                Attempt(pending.Cancel);
                pending.Completion.TrySetCanceled(pending.Cancellation.Token);
                Attempt(pending.DisposeCancellation);
            }
            if (canceled.Active is { } active)
                Attempt(active.Cancel);
            if (failures is not null)
                throw new AggregateException("Mesh preparation cancellation failed.", failures);
        }

        public ObjectRenderData? UploadMeshData(ObjectMeshData meshData)
        {
            bool uploadAttempted = false;
            try
            {
                if (_objectReleases.ContainsKey(meshData.ObjectId)
                    && !TryAdvanceObjectRelease(meshData.ObjectId, out _))
                {
                    return null;
                }
                if (!TryAdvanceUploadRollback(meshData.ObjectId))
                    return null;

                if (_renderData.TryGetValue(meshData.ObjectId, out var existing))
                {
                    UpdateLruAfterUpload(meshData.ObjectId);
                    return existing;
                }

                uploadAttempted = true;
                if (meshData.IsSetup)
                {
                    // Upload EnvCell geometry if present to ensure it's in _renderData
                    if (meshData.EnvCellGeometry != null)
                    {
                        if (UploadMeshData(meshData.EnvCellGeometry) is null)
                        {
                            throw new InvalidOperationException(
                                $"Nested EnvCell geometry 0x{meshData.EnvCellGeometry.ObjectId:X10} "
                                + $"failed while uploading setup 0x{meshData.ObjectId:X10}.");
                        }
                    }

                    // Setup objects are multi-part - each part needs its own render data
                    var data = new ObjectRenderData
                    {
                        IsSetup = true,
                        SetupParts = meshData.SetupParts,
                        ParticleEmitters = meshData.ParticleEmitters,
                        Batches = new List<ObjectRenderBatch>(),
                        BoundingBox = meshData.BoundingBox,
                        SortCenter = meshData.SortCenter,
                        DIDDegrade = meshData.DIDDegrade,
                        SelectionSphere = meshData.SelectionSphere,
                        MemorySize = 1024 // Small overhead for the setup itself
                    };
                    var acquiredParts = new List<ulong>(meshData.SetupParts.Count);
                    try
                    {
                        foreach (var (partId, _) in meshData.SetupParts)
                        {
                            IncrementRefCount(partId);
                            acquiredParts.Add(partId);
                            _ = PrepareMeshDataAsync(partId, isSetup: false);
                        }

                        if (!_renderData.TryAdd(meshData.ObjectId, data))
                            throw new InvalidOperationException(
                                $"Setup 0x{meshData.ObjectId:X10} was published concurrently.");
                        MarkRenderDataAvailabilityChanged();
                        _currentNonArenaGpuMemory = checked(
                            _currentNonArenaGpuMemory + data.NonArenaGpuBytes);
                    }
                    catch (Exception setupFailure)
                    {
                        RetryableResourceReleaseLedger rollback =
                            CreateSetupPartRollback(acquiredParts, DecrementRefCount);
                        ResourceReleaseAttempt attempt = rollback.Advance();
                        if (!rollback.IsComplete)
                        {
                            _uploadRollbacks[meshData.ObjectId] = rollback;
                            _uploadRollbackQueue.Enqueue(meshData.ObjectId);
                        }
                        if (!attempt.HasFailures)
                            throw;
                        throw new AggregateException(
                            $"Setup 0x{meshData.ObjectId:X10} upload and dependency rollback failed.",
                            setupFailure,
                            attempt.ToException(
                                $"Setup 0x{meshData.ObjectId:X10} retained unfinished part references."));
                    }

                    UpdateLruAfterUpload(meshData.ObjectId);

                    return data;
                }

                var renderData = UploadGfxObjMeshData(meshData);
                if (renderData == null)
                {
                    Console.WriteLine($"[up-null] 0x{meshData.ObjectId:X10} produced a 0-vertex mesh — caching empty render data (legitimate only for degenerate/no-valid-surface models post-#426; dat-verify via Issue119UpNullGfxObjDumpTests)");
                    renderData = new ObjectRenderData();
                }

                renderData.BoundingBox = meshData.BoundingBox;
                renderData.SortCenter = meshData.SortCenter;
                renderData.DIDDegrade = meshData.DIDDegrade;
                renderData.SelectionSphere = meshData.SelectionSphere;
                _renderData.TryAdd(meshData.ObjectId, renderData);
                MarkRenderDataAvailabilityChanged();
                _currentNonArenaGpuMemory = checked(
                    _currentNonArenaGpuMemory + renderData.NonArenaGpuBytes);
                UpdateLruAfterUpload(meshData.ObjectId);

                return renderData;
            }
            catch (Exception ex)
            {
                if (uploadAttempted)
                    meshData.UploadAttempts++;
                _logger.LogError(ex, "Error uploading mesh data for 0x{Id:X8}", meshData.ObjectId);
                return null;
            }
        }

        internal static long EstimateUploadBytes(ObjectMeshData meshData)
        {
            ArgumentNullException.ThrowIfNull(meshData);
            return meshData.GetEstimatedUploadBytes();
        }

        internal static long CalculateNonArenaGeometryBytes(
            bool usesGlobalArena,
            long geometryBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(geometryBytes);
            return usesGlobalArena ? 0 : geometryBytes;
        }

        internal static long CalculateTrackedGpuBytes(
            long nonArenaBytes,
            long physicalArenaBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nonArenaBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(physicalArenaBytes);
            return checked(nonArenaBytes + physicalArenaBytes);
        }

        internal static bool IsWithinGpuCacheBudget(
            long nonArenaBytes,
            long physicalArenaBytes,
            long maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nonArenaBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(physicalArenaBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            return nonArenaBytes <= maximumBytes - Math.Min(maximumBytes, physicalArenaBytes);
        }

        private sealed class UploadAtlasPlan
        {
            public TextureAtlasManager? Existing { get; init; }
            public required int Capacity { get; init; }
            public required long TotalArrayBytes { get; init; }
            public int AvailableSlots { get; set; }
            public bool Touched { get; set; }
            public HashSet<TextureKey> PlannedKeys { get; } = new();

            public bool HasTexture(TextureKey key) =>
                PlannedKeys.Contains(key) || Existing?.HasTexture(key) == true;
        }

        internal MeshUploadCost PlanUploadCost(
            ObjectMeshData meshData,
            IReadOnlySet<TextureAtlasManager> mipmapsAlreadyBudgeted,
            ulong queueGeneration)
        {
            ArgumentNullException.ThrowIfNull(meshData);
            ArgumentNullException.ThrowIfNull(mipmapsAlreadyBudgeted);
            if (HasRenderData(meshData.ObjectId))
                return default;

            var plans = new Dictionary<
                (int Width, int Height, TextureFormat Format),
                List<UploadAtlasPlan>>();
            long arrayAllocationBytes = 0;
            long mipmapBytes = 0;
            int newArrayCount = 0;

            PlanTextureWork(
                meshData,
                plans,
                mipmapsAlreadyBudgeted,
                ref arrayAllocationBytes,
                ref mipmapBytes,
                ref newArrayCount);

            GlobalMeshUploadPlan bufferPlan = PlanGlobalBufferWork(meshData);

            return new MeshUploadCost(
                EstimateUploadBytes(meshData),
                arrayAllocationBytes,
                mipmapBytes,
                newArrayCount,
                bufferPlan.UploadBytes,
                bufferPlan.AllocationBytes,
                bufferPlan.CopyBytes,
                bufferPlan.NewBufferCount,
                queueGeneration);
        }

        private GlobalMeshUploadPlan PlanGlobalBufferWork(ObjectMeshData meshData)
        {
            (int vertexCount, int indexCount) = GetGlobalMeshElementCounts(meshData);
            if (vertexCount == 0 || indexCount == 0 || GlobalBuffer is null)
                return default;
            return GlobalBuffer.PlanUpload(vertexCount, indexCount);
        }

        internal GlobalMeshCapacityResult EnsureGlobalBufferCapacity(
            MeshUploadQueueItem item,
            out GlobalMeshMaintenanceStep step)
        {
            if (GlobalBuffer is null)
            {
                step = default;
                return GlobalMeshCapacityResult.Ready;
            }
            (int vertexCount, int indexCount) = GetGlobalMeshElementCounts(item.Data);
            return GlobalBuffer.EnsureUploadCapacity(vertexCount, indexCount, out step);
        }

        private (int Vertices, int Indices) GetGlobalMeshElementCounts(ObjectMeshData meshData)
        {
            if (meshData.IsSetup)
            {
                return meshData.EnvCellGeometry is { } nested
                    && !HasRenderData(nested.ObjectId)
                    ? GetGlobalMeshElementCounts(nested)
                    : default;
            }
            if (meshData.Vertices.Length == 0)
                return default;

            int indexCount = 0;
            foreach (List<TextureBatchData> batches in meshData.TextureBatches.Values)
            {
                foreach (TextureBatchData batch in batches)
                    indexCount = checked(indexCount + batch.Indices.Count);
            }
            return (meshData.Vertices.Length, indexCount);
        }

        private void PlanTextureWork(
            ObjectMeshData meshData,
            Dictionary<(int Width, int Height, TextureFormat Format), List<UploadAtlasPlan>> plans,
            IReadOnlySet<TextureAtlasManager> mipmapsAlreadyBudgeted,
            ref long arrayAllocationBytes,
            ref long mipmapBytes,
            ref int newArrayCount)
        {
            if (meshData.IsSetup)
            {
                if (meshData.EnvCellGeometry is { } nested
                    && !HasRenderData(nested.ObjectId))
                {
                    PlanTextureWork(
                        nested,
                        plans,
                        mipmapsAlreadyBudgeted,
                        ref arrayAllocationBytes,
                        ref mipmapBytes,
                        ref newArrayCount);
                }
                return;
            }
            if (meshData.Vertices.Length == 0)
                return;

            foreach (var (format, batches) in meshData.TextureBatches)
            {
                if (!plans.TryGetValue(format, out List<UploadAtlasPlan>? atlasPlans))
                {
                    atlasPlans = new List<UploadAtlasPlan>();
                    if (_globalAtlases.TryGetValue(format, out List<TextureAtlasManager>? existingAtlases))
                    {
                        foreach (TextureAtlasManager existing in existingAtlases)
                        {
                            atlasPlans.Add(new UploadAtlasPlan
                            {
                                Existing = existing,
                                Capacity = existing.TotalSlots,
                                AvailableSlots = existing.AvailableSlots,
                                TotalArrayBytes = existing.TextureArray.TotalSizeInBytes,
                            });
                        }
                    }
                    plans.Add(format, atlasPlans);
                }

                foreach (TextureBatchData batch in batches)
                {
                    if (batch.Indices.Count == 0)
                        continue;

                    UploadAtlasPlan? selected = null;
                    for (int i = 0; i < atlasPlans.Count; i++)
                    {
                        UploadAtlasPlan candidate = atlasPlans[i];
                        if (candidate.HasTexture(batch.Key))
                        {
                            selected = candidate;
                            break;
                        }
                    }
                    if (selected is null)
                    {
                        for (int i = 0; i < atlasPlans.Count; i++)
                        {
                            UploadAtlasPlan candidate = atlasPlans[i];
                            if (candidate.AvailableSlots > 0)
                            {
                                selected = candidate;
                                break;
                            }
                        }
                    }

                    if (selected is null)
                    {
                        int capacity = TextureAtlasManager.CalculateInitialCapacity(
                            format.Width,
                            format.Height,
                            format.Format);
                        long totalArrayBytes = TextureAtlasManager.CalculateArrayBytes(
                            format.Width,
                            format.Height,
                            format.Format);
                        selected = new UploadAtlasPlan
                        {
                            Capacity = capacity,
                            AvailableSlots = capacity,
                            TotalArrayBytes = totalArrayBytes,
                        };
                        atlasPlans.Add(selected);
                        arrayAllocationBytes = checked(arrayAllocationBytes + totalArrayBytes);
                        newArrayCount++;
                    }

                    if (selected.HasTexture(batch.Key))
                        continue;

                    selected.PlannedKeys.Add(batch.Key);
                    selected.AvailableSlots--;
                    if (!selected.Touched)
                    {
                        bool mipAlreadyBudgeted = selected.Existing is not null
                            && mipmapsAlreadyBudgeted.Contains(selected.Existing);
                        if (!mipAlreadyBudgeted)
                            mipmapBytes = checked(mipmapBytes + selected.TotalArrayBytes);
                        selected.Touched = true;
                    }
                }
            }
        }

        internal static MeshUploadCost CalculateNewAtlasFirstUploadCost(
            int width,
            int height,
            TextureFormat format,
            int uploadBytes,
            long sourceBytes = 0) =>
            CalculateNewAtlasUploadCost(width, height, format, [uploadBytes], sourceBytes);

        internal static MeshUploadCost CalculateNewAtlasUploadCost(
            int width,
            int height,
            TextureFormat format,
            IReadOnlyList<int> uploadBytes,
            long sourceBytes = 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
            ArgumentNullException.ThrowIfNull(uploadBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
            long arrayBytes = TextureAtlasManager.CalculateArrayBytes(width, height, format);
            for (int i = 0; i < uploadBytes.Count; i++)
                ArgumentOutOfRangeException.ThrowIfNegative(uploadBytes[i]);
            return new MeshUploadCost(sourceBytes, arrayBytes, arrayBytes, 1);
        }

        internal void AddDirtyAtlasesTo(ISet<TextureAtlasManager> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            destination.UnionWith(_dirtyAtlases);
        }

        private void UpdateLruAfterUpload(ulong id)
        {
            lock (_lruList)
            {
                _lruList.Remove(id);
                if (!_ownership.MarkUploadComplete(id))
                    _lruList.AddLast(id);
            }
        }

        #region Private: Background Preparation

        internal static HashSet<ushort>? CollectDrawingBspPolygonIds(GfxObj gfxObj)
        {
            if (gfxObj.DrawingBSP?.Root is null) return null;
            var ids = new HashSet<ushort>();
            CollectDrawingBspPolygonIds(gfxObj.DrawingBSP.Root, ids);
            return ids;
        }

        private static void CollectDrawingBspPolygonIds(DatReaderWriter.Types.DrawingBSPNode node, HashSet<ushort> ids)
        {
            if (node.Polygons is not null)
                foreach (var pid in node.Polygons)
                    ids.Add((ushort)pid);
            if (node.PosNode is not null) CollectDrawingBspPolygonIds(node.PosNode, ids);
            if (node.NegNode is not null) CollectDrawingBspPolygonIds(node.NegNode, ids);
        }

        #endregion

        #region Private: GPU Upload

        internal static List<((int Width, int Height, TextureFormat Format) Format, TextureBatchData Batch)> OrderedUploadBatches(ObjectMeshData meshData)
        {
            var pairs = new List<((int Width, int Height, TextureFormat Format) Format, TextureBatchData Batch)>();
            bool cellShell = false;
            foreach (var (format, batches) in meshData.TextureBatches)
            {
                foreach (var batch in batches)
                {
                    pairs.Add((format, batch));
                    cellShell |= batch.IsCellShell;
                }
            }
            if (cellShell)
                return pairs.OrderBy(p => p.Batch.SourceSurfaceIndex).ToList(); // stable: OrderBy preserves storage order on ties
            return pairs;
        }

        private ObjectRenderData? UploadGfxObjMeshData(ObjectMeshData meshData)
        {
            if (meshData.Vertices.Length == 0) return null;

            List<((int Width, int Height, TextureFormat Format) Format, TextureBatchData Batch)> uploadOrder =
                OrderedUploadBatches(meshData);

            int totalIndexCount = 0;
            int nonEmptyBatchCount = 0;
            foreach (var (_, formatBatch) in uploadOrder)
            {
                if (formatBatch.Indices.Count == 0) continue;
                totalIndexCount = checked(totalIndexCount + formatBatch.Indices.Count);
                nonEmptyBatchCount++;
            }

            var cpuIndices = new ushort[totalIndexCount];
            var indexSegments = new (int Offset, int Count)[nonEmptyBatchCount];
            {
                int fillOffset = 0;
                int segmentIndex = 0;
                foreach (var (_, formatBatch) in uploadOrder)
                {
                    int count = formatBatch.Indices.Count;
                    if (count == 0) continue;
                    CollectionsMarshal.AsSpan(formatBatch.Indices)
                        .CopyTo(cpuIndices.AsSpan(fillOffset, count));
                    indexSegments[segmentIndex++] = (fillOffset, count);
                    fillOffset = checked(fillOffset + count);
                }
            }

            GlobalMeshAllocation? globalAllocation = null;
            var renderBatches = new List<ObjectRenderBatch>(nonEmptyBatchCount);
            var acquiredTextures = new List<(TextureAtlasManager Atlas, TextureKey Key)>();

            try
            {
                if (GlobalBuffer is not null && nonEmptyBatchCount != 0)
                {
                    globalAllocation = GlobalBuffer.UploadMesh(
                        meshData.Vertices,
                        cpuIndices,
                        indexSegments);
                }

                foreach (var (format, batch) in uploadOrder)
                {
                    {
                        if (batch.Indices.Count == 0) continue;

                        TextureAtlasManager? atlasManager = null;
                        int textureIndex = 0;
                        uint firstIndex = 0;
                        int batchBaseVertex = 0;

                        if (!_globalAtlases.TryGetValue(format, out var atlasList))
                        {
                            atlasList = new List<TextureAtlasManager>();
                            _globalAtlases[format] = atlasList;
                        }

                        for (int i = 0; i < atlasList.Count; i++)
                        {
                            if (atlasList[i].HasTexture(batch.Key))
                            {
                                atlasManager = atlasList[i];
                                break;
                            }
                        }
                        if (atlasManager is null)
                        {
                            for (int i = 0; i < atlasList.Count; i++)
                            {
                                if (atlasList[i].AvailableSlots > 0)
                                {
                                    atlasManager = atlasList[i];
                                    break;
                                }
                            }
                        }
                        if (atlasManager == null)
                        {
                            atlasManager = new TextureAtlasManager(
                                _atlasArrays,
                                format.Width,
                                format.Height,
                                format.Format,
                                OnAtlasGpuSafeEmpty);
                            atlasList.Add(atlasManager);
                        }
                        atlasManager.LastUseSequence = ++_atlasUseSequence;

                        bool uploadsNewLayer = !atlasManager.HasTexture(batch.Key);
                        try
                        {
                            textureIndex = atlasManager.AddTexture(batch.Key, batch.TextureData,
                                batch.UploadPixelFormat, batch.UploadPixelType);
                        }
                        catch
                        {
                            if (atlasManager.IsGpuSafeEmpty)
                                OnAtlasGpuSafeEmpty(atlasManager);
                            throw;
                        }
                        acquiredTextures.Add((atlasManager, batch.Key));
                        MarkAtlasActive(atlasManager);
                        if (uploadsNewLayer)
                            _dirtyAtlases.Add(atlasManager);

                        AcDream.App.Rendering.Gpu.GpuTextureSlot textureSlot =
                            atlasManager.TextureArray.ResolveSlot(batch.HasWrappingUVs);

                        renderBatches.Add(new ObjectRenderBatch
                        {
                            IndexCount = batch.Indices.Count,
                            Atlas = atlasManager!,
                            TextureIndex = textureIndex,
                            TextureSize = (format.Width, format.Height),
                            TextureFormat = format.Format,
                            RetailSurfaceMask = batch.RetailSurfaceMask,
                            Translucency = batch.Translucency,
                            MaterialState = batch.MaterialState,
                            IsTransparent = batch.IsTransparent,
                            IsAdditive = batch.IsAdditive,
                            HasWrappingUVs = batch.HasWrappingUVs,
                            SurfaceOpacity = batch.SurfaceOpacity,
                            Key = batch.Key,
                            CullMode = batch.CullMode,
                            FirstIndex = firstIndex,
                            BaseVertex = (uint)batchBaseVertex,
                            TextureSlot = textureSlot,
                        });
                    }
                }

                if (globalAllocation is not null)
                {
                    if (renderBatches.Count != globalAllocation.BatchFirstIndices.Count)
                    {
                        throw new InvalidOperationException("Global mesh batch allocation count mismatch.");
                    }

                    for (int i = 0; i < renderBatches.Count; i++)
                    {
                        renderBatches[i].BaseVertex = (uint)globalAllocation.Vertices.Offset;
                        renderBatches[i].FirstIndex = (uint)globalAllocation.BatchFirstIndices[i];
                    }
                }

                long geometryBytes = checked(
                    (long)meshData.Vertices.Length * VertexPositionNormalTexture.Size
                    + (long)totalIndexCount * sizeof(ushort));
                bool hasCutoutSubset = false;
                for (int i = 0; i < renderBatches.Count; i++)
                {
                    if (renderBatches[i].Translucency
                        == AcDream.Core.Meshing.TranslucencyKind.ClipMap)
                    {
                        hasCutoutSubset = true;
                        break;
                    }
                }
                var cpuPositions = new Vector3[meshData.Vertices.Length];
                for (int i = 0; i < cpuPositions.Length; i++)
                    cpuPositions[i] = meshData.Vertices[i].Position;
                var renderData = new ObjectRenderData
                {
                    VertexCount = meshData.Vertices.Length,
                    Batches = renderBatches,
                    HasCutoutSubset = hasCutoutSubset,
                    GlobalAllocation = globalAllocation,
                    ParticleEmitters = meshData.ParticleEmitters,
                    DIDDegrade = meshData.DIDDegrade,
                    CPUPositions = cpuPositions,
                    CPUIndices = cpuIndices,
                    CPUEdgeLines = meshData.EdgeLines,
                    MemorySize = geometryBytes,
                    NonArenaGpuBytes = CalculateNonArenaGeometryBytes(
                        GlobalBuffer is not null,
                        geometryBytes),
                };

                return renderData;
            }
            catch (Exception uploadFailure)
            {
                RetryableResourceReleaseLedger rollback = CreateUploadRollback(
                    globalAllocation,
                    acquiredTextures);
                ResourceReleaseAttempt attempt = rollback.Advance();
                if (!rollback.IsComplete)
                {
                    _uploadRollbacks[meshData.ObjectId] = rollback;
                    _uploadRollbackQueue.Enqueue(meshData.ObjectId);
                }

                if (!attempt.HasFailures)
                    throw;

                throw new AggregateException(
                    $"Mesh 0x{meshData.ObjectId:X10} upload and rollback failed.",
                    uploadFailure,
                    attempt.ToException(
                        $"Mesh 0x{meshData.ObjectId:X10} upload rollback had unfinished resources."));
            }
        }

        private RetryableResourceReleaseLedger CreateUploadRollback(
            GlobalMeshAllocation? globalAllocation,
            IReadOnlyList<(TextureAtlasManager Atlas, TextureKey Key)> acquiredTextures)
        {
            var releases = new List<(string Name, Action Release)>();

            if (globalAllocation is not null)
            {
                releases.Add((
                    "global-index-range",
                    () => GlobalBuffer!.AbortIndexRange(globalAllocation)));
                releases.Add((
                    "global-vertex-range",
                    () => GlobalBuffer!.AbortVertexRange(globalAllocation)));
            }

            for (int i = acquiredTextures.Count - 1; i >= 0; i--)
            {
                int releaseIndex = i;
                releases.Add((
                    $"atlas-texture-{releaseIndex}",
                    () => ReleaseAtlasTexture(
                        acquiredTextures[releaseIndex].Atlas,
                        acquiredTextures[releaseIndex].Key)));
            }

            return new RetryableResourceReleaseLedger(releases);
        }

        internal static RetryableResourceReleaseLedger CreateSetupPartRollback(
            IReadOnlyList<ulong> acquiredParts,
            Action<ulong> releasePart)
        {
            ArgumentNullException.ThrowIfNull(acquiredParts);
            ArgumentNullException.ThrowIfNull(releasePart);
            var releases = new List<(string Name, Action Release)>(acquiredParts.Count);
            for (int i = acquiredParts.Count - 1; i >= 0; i--)
            {
                int releaseIndex = i;
                releases.Add((
                    $"setup-part-{releaseIndex}",
                    () => releasePart(acquiredParts[releaseIndex])));
            }
            return new RetryableResourceReleaseLedger(releases);
        }

        #endregion

        #region Private: Utilities

        #region Raycasting

        public bool IntersectMesh(ObjectRenderData renderData, Matrix4x4 transform, Vector3 rayOrigin, Vector3 rayDirection, out float distance, out Vector3 normal)
        {
            return IntersectMeshInternal(renderData, transform, rayOrigin, rayDirection, 0, out distance, out normal);
        }

        private bool IntersectMeshInternal(ObjectRenderData renderData, Matrix4x4 transform, Vector3 rayOrigin, Vector3 rayDirection, int depth, out float distance, out Vector3 normal)
        {
            distance = float.MaxValue;
            normal = Vector3.UnitZ;
            bool hit = false;

            if (depth > 32) return false;

            if (renderData.IsSetup)
            {
                foreach (var part in renderData.SetupParts)
                {
                    var partData = TryGetRenderData(part.GfxObjId);
                    if (partData != null)
                    {
                        if (IntersectMeshInternal(partData, part.Transform * transform, rayOrigin, rayDirection, depth + 1, out float d, out Vector3 n))
                        {
                            if (d < distance)
                            {
                                distance = d;
                                normal = n;
                                hit = true;
                            }
                        }
                    }
                }
                return hit;
            }

            if (renderData.CPUPositions.Length == 0 || renderData.CPUIndices.Length == 0)
            {
                // Fallback to sphere if no CPU mesh data
                if (renderData.SelectionSphere != null && renderData.SelectionSphere.Radius > 0.001f)
                {
                    var worldOrigin = Vector3.Transform(renderData.SelectionSphere.Origin, transform);
                    float radius = renderData.SelectionSphere.Radius * transform.Translation.Length(); // Rough scale
                    if (GeometryUtils.RayIntersectsSphere(rayOrigin, rayDirection, worldOrigin, radius, out distance))
                    {
                        normal = Vector3.Normalize(rayOrigin + rayDirection * distance - worldOrigin);
                        return true;
                    }
                }
                return false;
            }

            // Transform ray to local space
            if (!Matrix4x4.Invert(transform, out var invTransform)) return false;
            Vector3 localOrigin = Vector3.Transform(rayOrigin, invTransform);
            Vector3 localDirection = Vector3.Normalize(Vector3.TransformNormal(rayDirection, invTransform));

            // Iterate through triangles
            for (int i = 0; i < renderData.CPUIndices.Length; i += 3)
            {
                Vector3 v0 = renderData.CPUPositions[renderData.CPUIndices[i]];
                Vector3 v1 = renderData.CPUPositions[renderData.CPUIndices[i + 1]];
                Vector3 v2 = renderData.CPUPositions[renderData.CPUIndices[i + 2]];

                if (GeometryUtils.RayIntersectsTriangle(localOrigin, localDirection, v0, v1, v2, out float t))
                {
                    Vector3 hitPointLocal = localOrigin + localDirection * t;
                    Vector3 hitPointWorld = Vector3.Transform(hitPointLocal, transform);
                    float worldDist = Vector3.Distance(rayOrigin, hitPointWorld);

                    if (worldDist < distance)
                    {
                        distance = worldDist;

                        Vector3 localNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
                        normal = Vector3.Normalize(Vector3.TransformNormal(localNormal, transform));

                        // Ensure normal faces the ray
                        if (Vector3.Dot(normal, rayDirection) > 0)
                        {
                            normal = -normal;
                        }

                        hit = true;
                    }
                }
            }

            return hit;
        }

        #endregion

        private long GetObjectReclaimableBytes(ulong key)
        {
            if (_objectReleases.TryGetValue(key, out ObjectReleaseTicket? release))
                return release.ReclaimableBytes;
            return _renderData.TryGetValue(key, out ObjectRenderData? data)
                ? GetReclaimableBytes(data)
                : 0;
        }

        private ObjectReleaseTicket? GetOrCreateObjectRelease(ulong key)
        {
            if (_objectReleases.TryGetValue(key, out ObjectReleaseTicket? existing))
                return existing;
            if (!_renderData.TryGetValue(key, out ObjectRenderData? data))
                return null;

            var releases = new List<(string Name, Action Release)>();
            if (data.GlobalAllocation is { } allocation)
            {
                releases.Add((
                    "global-index-range",
                    () => GlobalBuffer!.ReleaseIndexRange(allocation)));
                releases.Add((
                    "global-vertex-range",
                    () => GlobalBuffer!.ReleaseVertexRange(allocation)));
            }

            for (int i = 0; i < data.Batches.Count; i++)
            {
                int batchIndex = i;
                ObjectRenderBatch batch = data.Batches[batchIndex];
                if (batch.Atlas is null)
                    continue;
                releases.Add((
                    $"atlas-texture-{batchIndex}",
                    () => ReleaseAtlasTexture(
                        data.Batches[batchIndex].Atlas,
                        data.Batches[batchIndex].Key)));
            }

            if (data.IsSetup)
            {
                for (int i = 0; i < data.SetupParts.Count; i++)
                {
                    int partIndex = i;
                    releases.Add((
                        $"setup-part-{partIndex}",
                        () => DecrementRefCount(data.SetupParts[partIndex].GfxObjId)));
                }
            }

            releases.Add((
                "non-arena-memory-accounting",
                () => _currentNonArenaGpuMemory = checked(
                    _currentNonArenaGpuMemory - data.NonArenaGpuBytes)));

            var ticket = new ObjectReleaseTicket(
                key,
                data,
                GetReclaimableBytes(data),
                new RetryableResourceReleaseLedger(releases));
            _objectReleases.Add(key, ticket);
            MarkRenderDataAvailabilityChanged();
            return ticket;
        }

        private bool TryAdvanceObjectRelease(ulong key, out long reclaimedBytes)
        {
            reclaimedBytes = 0;
            ObjectReleaseTicket? ticket = GetOrCreateObjectRelease(key);
            if (ticket is null)
            {
                if (!_ownership.IsOwned(key))
                    _ownership.Remove(key);
                return false;
            }

            ResourceReleaseAttempt attempt = ticket.Resources.Advance();
            if (!ticket.Resources.IsComplete)
            {
                if (!ticket.IsQueued)
                {
                    ticket.IsQueued = true;
                    _objectReleaseQueue.Enqueue(key);
                }
                LogReleaseFailure(
                    key,
                    "resource release",
                    ticket.Resources.RemainingCount,
                    attempt);
                return false;
            }

            if (_renderData.TryGetValue(key, out ObjectRenderData? current)
                && ReferenceEquals(current, ticket.Data))
            {
                _renderData.TryRemove(key, out _);
            }
            _objectReleases.Remove(key);
            MarkRenderDataAvailabilityChanged();
            if (!_ownership.IsOwned(key))
                _ownership.Remove(key);
            lock (_lruList)
                _lruList.Remove(key);
            reclaimedBytes = ticket.ReclaimableBytes;
            LogReleaseFailure(key, "resource release", 0, attempt);
            return true;
        }

        private static void ReleaseAtlasTexture(
            TextureAtlasManager atlas,
            TextureKey key)
        {
            try
            {
                atlas.ReleaseTexture(key);
            }
            catch (Exception error) when (!atlas.HasTexture(key))
            {
                throw new MeshReferenceMutationException(
                    $"Texture {key} was released from atlas slot {atlas.Slot}, but its retirement callback failed.",
                    mutationCommitted: true,
                    error);
            }
        }

        private bool TryAdvanceUploadRollback(ulong key)
        {
            if (!_uploadRollbacks.TryGetValue(key, out RetryableResourceReleaseLedger? rollback))
                return true;

            ResourceReleaseAttempt attempt = rollback.Advance();
            if (!rollback.IsComplete)
            {
                LogReleaseFailure(
                    key,
                    "upload rollback",
                    rollback.RemainingCount,
                    attempt);
                return false;
            }

            _uploadRollbacks.Remove(key);
            LogReleaseFailure(key, "upload rollback", 0, attempt);
            return true;
        }

        private void LogReleaseFailure(
            ulong key,
            string operation,
            int remaining,
            ResourceReleaseAttempt attempt)
        {
            if (!attempt.HasFailures)
                return;
            _logger.LogError(
                attempt.ToException(
                    $"Mesh 0x{key:X10} {operation} reported exceptional resource outcomes."),
                "Mesh 0x{Id:X10} {Operation} completed {Completed} of {Attempted} attempted stages; {Remaining} remain",
                key,
                operation,
                attempt.CompletedCount,
                attempt.AttemptedCount,
                remaining);
        }

        private (int Count, long Bytes) AdvancePendingObjectReleases(
            int maximumCount,
            long maximumBytes)
        {
            int count = 0;
            long bytes = 0;
            int attempts = Math.Min(maximumCount, _objectReleaseQueue.Count);
            for (int i = 0; i < attempts; i++)
            {
                ulong key = _objectReleaseQueue.Dequeue();
                if (!_objectReleases.TryGetValue(key, out ObjectReleaseTicket? ticket))
                    continue;
                ticket.IsQueued = false;
                if (!FitsReclamationBudget(
                    ticket.ReclaimableBytes,
                    bytes,
                    maximumBytes))
                {
                    ticket.IsQueued = true;
                    _objectReleaseQueue.Enqueue(key);
                    continue;
                }
                if (!TryAdvanceObjectRelease(key, out long completedBytes))
                    continue;

                bytes = checked(bytes + completedBytes);
                count++;
            }
            return (count, bytes);
        }

        private void AdvancePendingUploadRollbacks(int maximumCount)
        {
            int attempts = Math.Min(maximumCount, _uploadRollbackQueue.Count);
            for (int i = 0; i < attempts; i++)
            {
                ulong key = _uploadRollbackQueue.Dequeue();
                if (!_uploadRollbacks.ContainsKey(key))
                    continue;
                try
                {
                    TryAdvanceUploadRollback(key);
                }
                finally
                {
                    if (_uploadRollbacks.ContainsKey(key))
                        _uploadRollbackQueue.Enqueue(key);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            lock (_disposeGate)
            {
                if (_disposeCompleted)
                    return;
                if (_disposeRunning)
                    return;
                _disposeRunning = true;
                try
                {
                    DisposeCore();
                    _disposeCompleted = true;
                }
                finally
                {
                    _disposeRunning = false;
                }
            }
        }

        private void DisposeCore()
        {
            List<Exception>? failures = null;
            static void Capture(ref List<Exception>? failures, Action action)
            {
                try { action(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
            if (!_workersQuiesced)
            {
                PreparationRequest[] pendingToCancel;
                PreparationRequest[] activeToCancel;
                lock (_pendingRequests)
                {
                    IsDisposed = true;
                    pendingToCancel = _pendingRequests.ToArray();
                    activeToCancel = _activePreparationById.Values.ToArray();
                    foreach (PreparationRequest request in pendingToCancel)
                        _preparationTasks.TryRemove(request.Id, out _);
                    _pendingRequests.Clear();
                    _pendingRequestById.Clear();
                    _envCellDescriptors.Clear();
                    _terminalPreparationFailures.Clear();
                }
                foreach (PreparationRequest request in pendingToCancel)
                {
                    Capture(ref failures, request.Cancel);
                    request.Completion.TrySetCanceled(request.Cancellation.Token);
                    Capture(ref failures, request.DisposeCancellation);
                }
                foreach (PreparationRequest request in activeToCancel)
                    Capture(ref failures, request.Cancel);

                // Release every persistent worker from its zero-CPU wait so it can
                // observe IsDisposed and terminate before DAT mappings are freed.
                Capture(ref failures, _preparationWorkAvailable.Set);
                Task[] workers;
                lock (_pendingRequests)
                    workers = _workerTasks.ToArray();
                Capture(ref failures, () => Task.WhenAll(workers).GetAwaiter().GetResult());
                lock (_pendingRequests)
                {
                    _workerTasks.RemoveWhere(worker => worker.IsCompleted);
                    if (_workerTasks.Count != 0)
                    {
                        (failures ??= []).Add(
                            new InvalidOperationException("Mesh workers remained live after their join completed."));
                    }
                    else
                    {
                        _workersQuiesced = true;
                    }
                }
            }

            ulong[] objectIds = _renderData.Keys
                .Concat(_objectReleases.Keys)
                .Distinct()
                .ToArray();
            for (int i = 0; i < objectIds.Length; i++)
            {
                ulong id = objectIds[i];
                try
                {
                    if (!TryAdvanceObjectRelease(id, out _))
                        (failures ??= []).Add(new InvalidOperationException(
                            $"Mesh 0x{id:X10} still has unfinished resource-release stages."));
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            ulong[] rollbackIds = _uploadRollbacks.Keys.ToArray();
            for (int i = 0; i < rollbackIds.Length; i++)
            {
                ulong id = rollbackIds[i];
                try
                {
                    if (!TryAdvanceUploadRollback(id))
                        (failures ??= []).Add(new InvalidOperationException(
                            $"Mesh 0x{id:X10} still has unfinished upload-rollback stages."));
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (_objectReleases.Count != 0 || _uploadRollbacks.Count != 0)
            {
                throw new AggregateException(
                    "One or more mesh resource transactions remain unfinished.",
                    failures ?? [new InvalidOperationException("Mesh resource teardown did not converge.")]);
            }

            Capture(ref failures, RetryPendingAtlasRetirements);
            Capture(ref failures, RetryRetiringAtlasDisposals);

            bool atlasFailure = false;
            foreach (List<TextureAtlasManager> atlasList in _globalAtlases.Values)
            {
                foreach (TextureAtlasManager atlas in atlasList)
                {
                    try { atlas.Dispose(); }
                    catch (Exception error)
                    {
                        atlasFailure = true;
                        (failures ??= []).Add(error);
                    }
                }
            }
            if (atlasFailure)
                throw new AggregateException(
                    "One or more texture atlases could not be disposed.",
                    failures!);

            if (GlobalBuffer is not null)
                Capture(ref failures, GlobalBuffer.Dispose);

            if (failures is not null)
                throw new AggregateException("One or more mesh-manager teardown operations failed.", failures);

            _renderData.Clear();
            _objectReleases.Clear();
            MarkRenderDataAvailabilityChanged();
            _objectReleaseQueue.Clear();
            _uploadRollbacks.Clear();
            _uploadRollbackQueue.Clear();
            _globalAtlases.Clear();
            _retiringAtlases.Clear();
            _dirtyAtlases.Clear();
            _safeEmptyAtlases.Clear();
            _currentNonArenaGpuMemory = 0;
            _cpuMeshCache.Clear();
            lock (_lruList)
                _lruList.Clear();

            if (!_workSignalDisposed)
            {
                _preparationWorkAvailable.Dispose();
                _workSignalDisposed = true;
            }

        }
    }
}
