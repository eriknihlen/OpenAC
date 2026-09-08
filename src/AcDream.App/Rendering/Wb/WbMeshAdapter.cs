using System;
using System.Collections.Generic;
using AcDream.Content;
using AcDream.App.Rendering.Residency;
using AcDream.Core.Rendering;
using DatReaderWriter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Rendering.Wb;

public sealed class WbMeshAdapter
    : IDisposable,
      IWbMeshAdapter
{
    internal const int MaximumUploadsPerFrame = 8;
    internal const int DestinationRevealMaximumUploadsPerFrame = 64;
    internal const long MaximumUploadBytesPerFrame = 8L * 1024 * 1024;
    internal const long MaximumArrayAllocationBytesPerFrame = 8L * 1024 * 1024;
    internal const long MaximumMipmapBytesPerFrame = 8L * 1024 * 1024;
    internal const int MaximumNewArraysPerFrame = 1;
    internal const long MaximumBufferUploadBytesPerFrame = 8L * 1024 * 1024;
    internal const long MaximumBufferAllocationBytesPerFrame =
        GlobalMeshBuffer.MaximumVertexBufferBytes;
    internal const long MaximumBufferCopyBytesPerFrame = 32L * 1024 * 1024;
    internal const int MaximumNewBuffersPerFrame = 1;
    internal const long MaximumSingleUploadBytes = 128L * 1024 * 1024;
    internal const long MaximumSingleArrayAllocationBytes = 128L * 1024 * 1024;
    internal const long MaximumSingleMipmapBytes = 128L * 1024 * 1024;
    internal const int MaximumSingleNewArrays = 16;
    internal const long MaximumSingleBufferUploadBytes = 32L * 1024 * 1024;
    internal const int MaximumReclaimedMeshesPerFrame = MaximumUploadsPerFrame;
    internal const long MaximumReclaimedMeshBytesPerFrame = 64L * 1024 * 1024;
    internal const int MaximumStaleDiscardsPerFrame = 64;
    private readonly IMeshPipelineDevice? _graphicsDevice;
    private readonly ObjectMeshManager? _meshManager;
    private readonly AcDream.App.Rendering.IGpuResourceRetirementQueue? _resourceRetirement;
    private readonly Func<uint, bool> _runtimeHiddenMarker;
    private readonly IPreparedAssetSource? _ownedPreparedAssets;
    private readonly MeshUploadFrameBudget _ordinaryUploadBudget =
        CreateUploadBudget(MaximumUploadsPerFrame);
    private readonly MeshUploadFrameBudget _destinationRevealUploadBudget =
        CreateUploadBudget(DestinationRevealMaximumUploadsPerFrame);
    private readonly HashSet<TextureAtlasManager> _mipmapsBudgeted = new();
    private bool _destinationRevealUploadPriority;

    private readonly bool _isUninitialized;

    private bool _disposed;
    private AcDream.App.Rendering.OrderedResourceTeardown? _teardown;
    internal int LastUploadCount { get; private set; }
    internal long LastUploadBytes { get; private set; }
    internal int LastStaleDiscardCount { get; private set; }
    internal long LastArrayAllocationBytes { get; private set; }
    internal long LastPlannedMipmapBytes { get; private set; }
    internal int LastNewArrayCount { get; private set; }
    internal long LastBufferUploadBytes { get; private set; }
    internal long LastBufferAllocationBytes { get; private set; }
    internal long LastBufferCopyBytes { get; private set; }
    internal int LastNewBufferCount { get; private set; }
    internal int LastMipmapArrayCount { get; private set; }
    internal long LastMipmapBytes { get; private set; }
    internal int StagedUploadBacklog => _meshManager?.StagedMeshCount ?? 0;
    internal long StagedUploadBytes => _meshManager?.StagedMeshBytes ?? 0;
    internal bool StagingAtHighWater => _meshManager?.StagingAtHighWater ?? false;
    internal (int Count, long Bytes) CpuMeshCacheDiagnostics =>
        _meshManager?.CpuCacheDiagnostics ?? default;

    internal bool IsRuntimeHiddenMarker(uint gfxObjId) =>
        _runtimeHiddenMarker(gfxObjId);

    internal void SetDestinationRevealUploadPriority(bool enabled) =>
        _destinationRevealUploadPriority = enabled;

    private static MeshUploadFrameBudget CreateUploadBudget(
        int maximumObjects) =>
        new(new MeshUploadBudgetLimits(
            maximumObjects,
            MaximumUploadBytesPerFrame,
            MaximumArrayAllocationBytesPerFrame,
            MaximumMipmapBytesPerFrame,
            MaximumNewArraysPerFrame,
            MaximumBufferUploadBytesPerFrame,
            MaximumBufferAllocationBytesPerFrame,
            MaximumBufferCopyBytesPerFrame,
            MaximumNewBuffersPerFrame,
            MaximumSingleUploadBytes,
            MaximumSingleArrayAllocationBytes,
            MaximumSingleMipmapBytes,
            MaximumSingleNewArrays,
            MaximumSingleBufferUploadBytes));

    internal WbMeshAdapter(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        IDatReaderWriter dats,
        ILogger<WbMeshAdapter> logger)
        : this(
            gpuDevice,
            dats,
            preparedAssets: null,
            logger,
            AcDream.App.Rendering.ImmediateGpuResourceRetirementQueue.Instance,
            ownsPreparedAssets: true,
            ResidencyBudgetOptions.Default)
    {
    }

    internal static WbMeshAdapter CreateWithLiveDatPreparedAssets(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        IDatReaderWriter dats,
        ILogger<WbMeshAdapter> logger,
        AcDream.App.Rendering.IGpuResourceRetirementQueue resourceRetirement) =>
        new(
            gpuDevice,
            dats,
            preparedAssets: null,
            logger,
            resourceRetirement,
            ownsPreparedAssets: true,
            ResidencyBudgetOptions.Default);

    internal WbMeshAdapter(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        IDatReaderWriter dats,
        IPreparedAssetSource preparedAssets,
        ILogger<WbMeshAdapter> logger,
        AcDream.App.Rendering.IGpuResourceRetirementQueue resourceRetirement,
        ResidencyBudgetOptions? budgets = null)
        : this(
            gpuDevice,
            dats,
            preparedAssets,
            logger,
            resourceRetirement,
            ownsPreparedAssets: false,
            budgets ?? ResidencyBudgetOptions.Default)
    {
    }

    private WbMeshAdapter(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        IDatReaderWriter dats,
        IPreparedAssetSource? preparedAssets,
        ILogger<WbMeshAdapter> logger,
        AcDream.App.Rendering.IGpuResourceRetirementQueue resourceRetirement,
        bool ownsPreparedAssets,
        ResidencyBudgetOptions budgets)
    {
        ArgumentNullException.ThrowIfNull(gpuDevice);
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(budgets);

        _resourceRetirement = resourceRetirement;
        var hiddenMarkerMemo =
            new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();
        _runtimeHiddenMarker = gfxObjId =>
            hiddenMarkerMemo.GetOrAdd(
                gfxObjId,
                static (id, source) =>
                    AcDream.Core.Meshing.GfxObjDegradeResolver
                        .IsRuntimeHiddenMarker(source, id),
                dats);
        var resources = new AcDream.App.Rendering.ResourceCleanupGroup();
        IMeshPipelineDevice? graphicsDevice = null;
        IPreparedAssetSource? resolvedPreparedAssets = preparedAssets;
        ObjectMeshManager? meshManager = null;
        try
        {
            var rhiDevice = new AcDream.App.Rendering.Gpu.Vk.VulkanMeshPipelineDevice(
                resourceRetirement);
            graphicsDevice = rhiDevice;
            resources.Add("WB graphics device", rhiDevice.Dispose);
            if (resolvedPreparedAssets is null)
            {
                resolvedPreparedAssets = new DatPreparedAssetSource(
                    dats,
                    new ConsoleErrorLogger<ObjectMeshManager>());
                resources.Add(
                    "WB tooling prepared asset source",
                    resolvedPreparedAssets.Dispose);
            }
            meshManager = new ObjectMeshManager(
                graphicsDevice,
                gpuDevice,
                resolvedPreparedAssets,
                new ConsoleErrorLogger<ObjectMeshManager>(),
                budgets);
            resources.Add("WB object mesh manager", meshManager.Dispose);
            resources.TransferAll();
        }
        catch (Exception constructionFailure)
        {
            resources.RollbackConstructionAndThrow(
                "WbMeshAdapter construction failed and its WB/GL prefix did not cleanly roll back.",
                constructionFailure);
        }

        _graphicsDevice = graphicsDevice;
        _meshManager = meshManager;
        _ownedPreparedAssets = ownsPreparedAssets
            ? resolvedPreparedAssets
            : null;
    }

    internal void RegisterResidencySources(ResidencyManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ObjectMeshManager meshManager = _meshManager
            ?? throw new InvalidOperationException(
                "An initialized mesh adapter is required for residency diagnostics.");
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.ObjectMeshes,
            meshManager.CaptureObjectMeshResidency));
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.PreparedMeshCpu,
            meshManager.CapturePreparedMeshResidency));
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.MeshStaging,
            meshManager.CaptureStagingResidency));
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.GlobalMeshArena,
            meshManager.CaptureGlobalArenaResidency));
    }

    private sealed class ConsoleErrorLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            Console.WriteLine($"[wb-error] {message}");
            if (exception is not null)
            {
                Console.WriteLine($"[wb-error]   {exception.GetType().Name}: {exception.Message}");
                var stack = (exception.StackTrace ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Take(5);
                foreach (var s in stack) Console.WriteLine($"[wb-error]   {s.Trim()}");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private WbMeshAdapter()
    {
        _isUninitialized = true;
        _runtimeHiddenMarker = static _ => false;
    }

    /// <summary>Test/init helper — produces a Dispose-safe instance with no
    /// underlying mesh manager. Public methods are all no-ops.</summary>
    public static WbMeshAdapter CreateUninitialized() => new();

    public ObjectMeshManager? MeshManager => _meshManager;

    public ObjectRenderData? GetRenderData(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return null;
        return _meshManager.GetRenderData(id);
    }

    public ObjectRenderData? TryGetRenderData(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return null;
        return _meshManager.TryGetRenderData(id);
    }

    /// <inheritdoc/>
    public bool IsRenderDataReady(ulong id)
    {
        return _isUninitialized || (_meshManager?.EnsureRenderDataReady(id) ?? false);
    }

    /// <inheritdoc/>
    public void IncrementRefCount(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return;
        _meshManager.IncrementRefCount(id);

        try
        {
            if (_meshManager.TryGetRenderData(id) is null)
                _meshManager.PrepareMeshDataAsync(id, isSetup: false);
        }
        catch (Exception acquireFailure)
        {
            try
            {
                _meshManager.DecrementRefCount(id);
            }
            catch (Exception rollbackFailure)
            {
                throw new MeshReferenceMutationException(
                    $"Mesh 0x{id:X10} reference acquisition failed and its committed increment could not be rolled back.",
                    mutationCommitted: true,
                    new AggregateException(acquireFailure, rollbackFailure));
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void DecrementRefCount(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return;
        _meshManager.DecrementRefCount(id);
    }

    /// <inheritdoc/>
    public void PinPreparedRenderData(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return;
        // EnvCell geometry has its own schema-aware preparation request.
        // Only establish lifecycle ownership here; IncrementRefCount's normal
        // generic GfxObj preparation would decode the synthetic id incorrectly.
        _meshManager.IncrementRefCount(id);
    }

    public void EnsureLoaded(ulong id)
    {
        if (_isUninitialized || _meshManager is null) return;
        _meshManager.PrepareMeshDataAsync(id, isSetup: false);
    }

    public void Tick()
    {
        if (_isUninitialized) return;
        if (_disposed) return;

        ObjectMeshManager meshManager = _meshManager!;
        _graphicsDevice!.ProcessQueue();
        List<MeshUploadQueueItem>? requeue = null;
        MeshUploadFrameBudget uploadBudget =
            _destinationRevealUploadPriority
                ? _destinationRevealUploadBudget
                : _ordinaryUploadBudget;
        int maximumUploads =
            _destinationRevealUploadPriority
                ? DestinationRevealMaximumUploadsPerFrame
                : MaximumUploadsPerFrame;
        uploadBudget.Reset();
        _mipmapsBudgeted.Clear();
        int staleDiscardCount = 0;
        bool arenaBackpressured = false;
        GlobalMeshBuffer? globalBuffer = meshManager.GlobalBuffer;

        if (globalBuffer?.IsMigrationInProgress == true)
        {
            GlobalMeshMaintenanceStep maintenance =
                globalBuffer.AdvanceMigration(MaximumBufferCopyBytesPerFrame);
            uploadBudget.RecordBufferMaintenance(
                maintenance.AllocationBytes,
                maintenance.CopyBytes,
                maintenance.NewBufferCount);
            arenaBackpressured = !maintenance.Completed;
        }

        while (globalBuffer?.IsMigrationInProgress != true
            && uploadBudget.BufferCopyBytes == 0
            && uploadBudget.ObjectCount < maximumUploads)
        {
            staleDiscardCount += meshManager.DiscardUnownedStagedPrefix(
                MaximumStaleDiscardsPerFrame - staleDiscardCount);
            if (staleDiscardCount >= MaximumStaleDiscardsPerFrame)
                break;
            if (!meshManager.TryPeekStagedMeshData(out MeshUploadQueueItem next))
                break;

            GlobalMeshCapacityResult capacity;
            GlobalMeshMaintenanceStep maintenance;
            try
            {
                capacity = meshManager.EnsureGlobalBufferCapacity(next, out maintenance);
            }
            catch (NotSupportedException error)
            {
                RejectUnsupportedHead(meshManager, next, error);
                throw;
            }
            if (capacity == GlobalMeshCapacityResult.MigrationStarted)
            {
                uploadBudget.RecordBufferMaintenance(
                    maintenance.AllocationBytes,
                    maintenance.CopyBytes,
                    maintenance.NewBufferCount);
                arenaBackpressured = true;
                break;
            }
            if (capacity == GlobalMeshCapacityResult.MigrationInProgress)
            {
                arenaBackpressured = true;
                break;
            }
            if (capacity == GlobalMeshCapacityResult.NeedsReclamation)
            {
                // Do not evict another batch while the frame-fence queue is
                // already returning ranges or retiring an old backing store.
                // Waiting for real allocator state prevents three frames of
                // duplicate eviction before the first release becomes reusable.
                if (globalBuffer?.HasPendingReclamation != true)
                {
                    var reclaimed = meshManager.ReclaimUnusedResources(
                        MaximumReclaimedMeshesPerFrame,
                        MaximumReclaimedMeshBytesPerFrame,
                        forceArenaReclamation: true);
                    if (reclaimed.Count == 0)
                    {
                        throw new NotSupportedException(
                            $"The live global-mesh working set cannot fit the supported "
                            + $"{GlobalMeshBuffer.MaximumVertexBufferBytes + GlobalMeshBuffer.MaximumIndexBufferBytes:N0}-byte arena "
                            + $"(blocked staging generation {next.Generation}, object 0x{next.Data.ObjectId:X10}).");
                    }
                }
                arenaBackpressured = true;
                break;
            }

            MeshUploadCost cost;
            try
            {
                cost = meshManager.PlanUploadCost(
                    next.Data,
                    _mipmapsBudgeted,
                    next.Generation);
                if (!uploadBudget.TryAdmit(cost))
                    break;
            }
            catch (NotSupportedException error)
            {
                RejectUnsupportedHead(meshManager, next, error);
                throw;
            }

            if (!meshManager.TryDequeueStagedMeshData(out MeshUploadQueueItem meshData))
                break;
            if (meshData.Generation != next.Generation)
                throw new InvalidOperationException("The staged mesh FIFO head changed during render-thread admission.");
            if (meshManager.UploadOrRequeue(meshData))
                (requeue ??= new()).Add(meshData);
            meshManager.AddDirtyAtlasesTo(_mipmapsBudgeted);
        }
        if (requeue is not null)
            foreach (var m in requeue)
                meshManager.RequeueStagedMeshData(m);
        meshManager.SetArenaBackpressure(arenaBackpressured);

        (LastMipmapArrayCount, LastMipmapBytes) = meshManager.GenerateMipmaps();
        if (globalBuffer?.HasPendingReclamation != true)
        {
            meshManager.ReclaimUnusedResources(
                MaximumReclaimedMeshesPerFrame,
                MaximumReclaimedMeshBytesPerFrame);
        }
        meshManager.EvictOneEmptyAtlas();

        if (uploadBudget.ObjectCount == 0
            && LastMipmapArrayCount == 0
            && meshManager.StagedMeshCount == 0
            && globalBuffer?.IsMigrationInProgress == false
            && globalBuffer.TryTrimUnusedTail(out GlobalMeshMaintenanceStep trim))
        {
            uploadBudget.RecordBufferMaintenance(
                trim.AllocationBytes,
                trim.CopyBytes,
                trim.NewBufferCount);
            meshManager.SetArenaBackpressure(true);
        }

        LastUploadCount = uploadBudget.ObjectCount;
        LastUploadBytes = uploadBudget.SourceBytes;
        LastStaleDiscardCount = staleDiscardCount;
        LastArrayAllocationBytes = uploadBudget.ArrayAllocationBytes;
        LastPlannedMipmapBytes = uploadBudget.MipmapBytes;
        LastNewArrayCount = uploadBudget.NewArrayCount;
        LastBufferUploadBytes = uploadBudget.BufferUploadBytes;
        LastBufferAllocationBytes = uploadBudget.BufferAllocationBytes;
        LastBufferCopyBytes = uploadBudget.BufferCopyBytes;
        LastNewBufferCount = uploadBudget.NewBufferCount;
    }

    private static void RejectUnsupportedHead(
        ObjectMeshManager meshManager,
        MeshUploadQueueItem expected,
        NotSupportedException error)
    {
        if (!meshManager.TryDequeueStagedMeshData(out MeshUploadQueueItem rejected)
            || rejected.Generation != expected.Generation)
        {
            throw new InvalidOperationException(
                "The staged mesh FIFO head changed while rejecting an unsupported generation.",
                error);
        }
        meshManager.RejectUnsupportedStagedUpload(rejected, error);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _teardown ??= new AcDream.App.Rendering.OrderedResourceTeardown(
            () =>
            {
                if (_resourceRetirement is AcDream.App.Rendering.Gpu.Vk.VulkanFrameFlightController frameFlights)
                    frameFlights.WaitForSubmittedWork();
            },
            () => _meshManager?.Dispose(),
            () => _ownedPreparedAssets?.Dispose(),
            () => DrainGraphicsQueue("publishing mesh resource retirements"),
            () =>
            {
                if (_resourceRetirement is AcDream.App.Rendering.Gpu.Vk.VulkanFrameFlightController frameFlights)
                    frameFlights.WaitForSubmittedWork();
            },
            () => DrainGraphicsQueue("releasing retired mesh resources"),
            () => _graphicsDevice?.Dispose(),
            () => DrainGraphicsQueue("deleting mesh graphics-device resources"));

        _teardown.Advance();
        _disposed = _teardown.IsComplete;
    }

    private void DrainGraphicsQueue(string operation)
    {
        if (_graphicsDevice is null)
            return;

        _graphicsDevice.ProcessQueue();
        if (_graphicsDevice.HasPendingWork)
        {
            throw new InvalidOperationException(
                $"OpenGL work remains pending after {operation}; retry adapter disposal to continue the exact stage.");
        }
    }
}
