using System.Runtime.InteropServices;
using AcDream.Content;
using Chorizite.Core.Render.Enums;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering.Wb;

internal sealed record GlobalMeshAllocation(
    MeshBufferRange Vertices,
    MeshBufferRange Indices,
    IReadOnlyList<int> BatchFirstIndices);

internal readonly record struct GlobalMeshUploadPlan(
    long UploadBytes,
    long AllocationBytes,
    long CopyBytes,
    int NewBufferCount);

internal readonly record struct GlobalMeshMaintenanceStep(
    long AllocationBytes,
    long CopyBytes,
    int NewBufferCount,
    bool Completed);

internal sealed class GlobalMeshMigrationAbortTicket
{
    private readonly RetryableGpuResourceRelease _release;

    public GlobalMeshMigrationAbortTicket(
        IGpuBuffer buffer,
        long capacityBytes,
        RetryableGpuResourceRelease release)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        Buffer = buffer;
        CapacityBytes = capacityBytes;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public IGpuBuffer Buffer { get; }
    public long CapacityBytes { get; }
    public bool IsComplete => _release.IsComplete;

    public void Advance() => _release.Run();
}

internal enum GlobalMeshCapacityResult
{
    Ready,
    MigrationStarted,
    MigrationInProgress,
    NeedsReclamation,
}

public sealed class GlobalMeshBuffer : IDisposable
{
    internal const int InitialVertexCapacity = 1024 * 1024;
    internal const int InitialIndexCapacity = 3 * 1024 * 1024;
    internal const int VertexGrowthQuantum = 256 * 1024;
    internal const int IndexGrowthQuantum = 1024 * 1024;
    internal const long MaximumVertexBufferBytes = 384L * 1024 * 1024;
    internal const long MaximumIndexBufferBytes = 128L * 1024 * 1024;
    internal const long MaximumPhysicalArenaBytes = 896L * 1024 * 1024;
    internal static readonly int MaximumVertexCapacity = checked(
        (int)(MaximumVertexBufferBytes / VertexPositionNormalTexture.Size));
    internal const int MaximumIndexCapacity =
        (int)(MaximumIndexBufferBytes / sizeof(ushort));

    private readonly IGpuDevice _device;
    private readonly GpuRetirementLedger _retirementLedger;
    private readonly GpuRetiredRangeAllocator _vertices;
    private readonly GpuRetiredRangeAllocator _indices;
    private IGpuBuffer? _vertexBuffer;
    private IGpuBuffer? _indexBuffer;
    private BufferMigration? _migration;
    private GlobalMeshMigrationAbortTicket? _migrationAbort;
    private long _retiredCapacityBytes;
    private int _storeGeneration;
    private bool _disposed;
    private RetryableResourceReleaseLedger? _disposeResources;

    private static IGpuBuffer RequireStore(IGpuBuffer? store) =>
        store ?? throw new InvalidOperationException(
            "The global mesh arena has no live backing store.");

    private enum BufferKind
    {
        Vertices,
        Indices,
    }

    private sealed record BufferMigration(
        BufferKind Kind,
        IGpuBuffer OldBuffer,
        IGpuBuffer NewBuffer,
        int OldCapacity,
        int NewCapacity,
        long OldCapacityBytes,
        long NewCapacityBytes,
        long CopyBytes)
    {
        public long CopiedBytes { get; set; }
    }

    internal IGpuBuffer? VertexStore => _vertexBuffer;

    internal IGpuBuffer? IndexStore => _indexBuffer;

    /// <summary>True once both backing stores exist.</summary>
    internal bool HasStores => _vertexBuffer is not null && _indexBuffer is not null;
    internal long UploadCount { get; private set; }
    internal long UploadedBytes { get; private set; }
    internal long CapacityBytes =>
        (long)_vertices.Capacity * VertexPositionNormalTexture.Size
        + (long)_indices.Capacity * sizeof(ushort);
    internal long PhysicalCapacityBytes => checked(
        CapacityBytes + (_migration?.NewCapacityBytes ?? 0) + _retiredCapacityBytes);
    internal bool IsMigrationInProgress => _migration is not null;
    internal bool HasPendingReclamation =>
        _migration is not null
        || _vertices.PendingReleaseCount != 0
        || _indices.PendingReleaseCount != 0
        || _retiredCapacityBytes != 0;
    internal int VertexHighWaterMark => _vertices.HighWaterMark;
    internal int IndexHighWaterMark => _indices.HighWaterMark;
    internal long UsedBytes => checked(
        (long)_vertices.Used * VertexPositionNormalTexture.Size
        + (long)_indices.Used * sizeof(ushort));
    internal long LargestFreeBytes => checked(
        (long)_vertices.LargestFreeRange * VertexPositionNormalTexture.Size
        + (long)_indices.LargestFreeRange * sizeof(ushort));
    internal long PendingRangeRetirementBytes => checked(
        (long)_vertices.PendingReleaseLength * VertexPositionNormalTexture.Size
        + (long)_indices.PendingReleaseLength * sizeof(ushort));
    internal long RequestedMigrationBytes => _migration?.NewCapacityBytes ?? 0;
    internal long RetiredBackingBytes => _retiredCapacityBytes;
    internal long ResidentCapacityBytes => checked(
        CapacityBytes - PendingRangeRetirementBytes);

    internal GlobalMeshUploadPlan PlanUpload(int vertexCount, int indexCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();
        if (_migration is not null)
            throw new InvalidOperationException("Upload planning is unavailable while a backing-buffer migration is in progress.");
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(indexCount);
        long allocationBytes = 0;
        long copyBytes = 0;
        int newBuffers = 0;

        if (vertexCount > _vertices.LargestFreeRange)
        {
            int newCapacity = CalculateGrowthCapacity(
                _vertices.Capacity, _vertices.TrailingFreeLength,
                vertexCount, VertexGrowthQuantum, MaximumVertexCapacity);
            allocationBytes = checked(allocationBytes
                + (long)newCapacity * VertexPositionNormalTexture.Size);
            copyBytes = checked(copyBytes
                + (long)_vertices.HighWaterMark * VertexPositionNormalTexture.Size);
            newBuffers++;
        }
        if (indexCount > _indices.LargestFreeRange)
        {
            int newCapacity = CalculateGrowthCapacity(
                _indices.Capacity, _indices.TrailingFreeLength,
                indexCount, IndexGrowthQuantum, MaximumIndexCapacity);
            allocationBytes = checked(allocationBytes + (long)newCapacity * sizeof(ushort));
            copyBytes = checked(copyBytes + (long)_indices.HighWaterMark * sizeof(ushort));
            newBuffers++;
        }

        return new GlobalMeshUploadPlan(
            checked((long)vertexCount * VertexPositionNormalTexture.Size
                + (long)indexCount * sizeof(ushort)),
            allocationBytes,
            copyBytes,
            newBuffers);
    }

    internal GlobalMeshBuffer(IGpuDevice device, IGpuResourceRetirementQueue retirement)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(retirement);
        _retirementLedger = new GpuRetirementLedger(retirement);
        _vertices = new GpuRetiredRangeAllocator(InitialVertexCapacity, retirement); // ~32 MB
        _indices = new GpuRetiredRangeAllocator(InitialIndexCapacity, retirement);   // ~6 MB
        InitBuffers();
    }

    private static GpuBufferDescription DescribeStore(BufferKind kind, long sizeBytes, int generation) =>
        new(
            kind == BufferKind.Vertices
                ? $"mesh-arena-vertex-{generation}"
                : $"mesh-arena-index-{generation}",
            sizeBytes,
            (kind == BufferKind.Vertices ? GpuBufferUsage.Vertex : GpuBufferUsage.Index)
                | GpuBufferUsage.TransferSource
                | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal);

    private void InitBuffers()
    {
        IGpuBuffer? vbo = null;
        IGpuBuffer? ibo = null;
        long vertexBytes = (long)_vertices.Capacity * VertexPositionNormalTexture.Size;
        long indexBytes = (long)_indices.Capacity * sizeof(ushort);
        bool vertexTracked = false;
        bool indexTracked = false;

        try
        {
            vbo = _device.CreateBuffer(DescribeStore(BufferKind.Vertices, vertexBytes, _storeGeneration));
            ibo = _device.CreateBuffer(DescribeStore(BufferKind.Indices, indexBytes, _storeGeneration));

            GpuMemoryTracker.TrackResourceAllocation(GpuResourceType.Buffer);
            GpuMemoryTracker.TrackAllocation(vertexBytes, GpuResourceType.Buffer);
            vertexTracked = true;
            GpuMemoryTracker.TrackResourceAllocation(GpuResourceType.Buffer);
            GpuMemoryTracker.TrackAllocation(indexBytes, GpuResourceType.Buffer);
            indexTracked = true;

            _vertexBuffer = vbo;
            _indexBuffer = ibo;
        }
        catch
        {
            ibo?.Dispose();
            vbo?.Dispose();
            if (indexTracked)
            {
                GpuMemoryTracker.TrackDeallocation(indexBytes, GpuResourceType.Buffer);
                GpuMemoryTracker.TrackResourceDeallocation(GpuResourceType.Buffer);
            }
            if (vertexTracked)
            {
                GpuMemoryTracker.TrackDeallocation(vertexBytes, GpuResourceType.Buffer);
                GpuMemoryTracker.TrackResourceDeallocation(GpuResourceType.Buffer);
            }
            throw;
        }
    }

    internal GlobalMeshAllocation UploadMesh(
        VertexPositionNormalTexture[] vertices,
        ushort[] indices,
        ReadOnlySpan<(int Offset, int Count)> indexBatches)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();
        if (_migration is not null)
            throw new InvalidOperationException("A mesh upload cannot mutate the arena while a backing-buffer migration is in progress.");
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length == 0)
            throw new ArgumentException("A global mesh allocation requires vertices.", nameof(vertices));

        int totalIndices = 0;
        for (int i = 0; i < indexBatches.Length; i++)
        {
            (int offset, int count) = indexBatches[i];
            if (offset < 0 || count <= 0 || (long)offset + count > indices.Length)
            {
                throw new ArgumentException(
                    $"Index batch {i} ({offset}, {count}) is outside the shared index array ({indices.Length}).",
                    nameof(indexBatches));
            }
            totalIndices = checked(totalIndices + count);
        }
        if (totalIndices == 0)
            throw new ArgumentException("A global mesh allocation requires indices.", nameof(indexBatches));

        MeshBufferRange vertexRange = AllocateVertices(vertices.Length);
        MeshBufferRange indexRange;
        try
        {
            indexRange = AllocateIndices(totalIndices);
        }
        catch
        {
            _vertices.ReleaseUnsubmitted(vertexRange);
            throw;
        }

        var firstIndices = new int[indexBatches.Length];
        try
        {
            long vertexOffsetBytes = checked((long)vertexRange.Offset * VertexPositionNormalTexture.Size);
            RequireStore(_vertexBuffer).Upload(
                vertexOffsetBytes,
                MemoryMarshal.AsBytes(new ReadOnlySpan<VertexPositionNormalTexture>(vertices)));

            IGpuBuffer indexStore = RequireStore(_indexBuffer);
            int indexOffset = indexRange.Offset;
            for (int i = 0; i < indexBatches.Length; i++)
            {
                (int offset, int count) = indexBatches[i];
                firstIndices[i] = indexOffset;
                long indexOffsetBytes = checked((long)indexOffset * sizeof(ushort));
                indexStore.Upload(
                    indexOffsetBytes,
                    MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>(indices, offset, count)));
                indexOffset = checked(indexOffset + count);
            }
        }
        catch
        {
            _indices.ReleaseUnsubmitted(indexRange);
            _vertices.ReleaseUnsubmitted(vertexRange);
            throw;
        }

        UploadCount++;
        UploadedBytes = checked(UploadedBytes
            + checked((long)vertices.Length * VertexPositionNormalTexture.Size)
            + checked((long)totalIndices * sizeof(ushort)));
        return new GlobalMeshAllocation(vertexRange, indexRange, firstIndices);
    }

    internal void Release(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _indices.ReleaseAfterGpuUse(allocation.Indices);
        _vertices.ReleaseAfterGpuUse(allocation.Vertices);
    }

    internal void ReleaseIndexRange(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _indices.ReleaseAfterGpuUse(allocation.Indices);
    }

    internal void ReleaseVertexRange(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _vertices.ReleaseAfterGpuUse(allocation.Vertices);
    }

    /// <summary>
    /// Rolls back a mesh transaction which never published render data and
    /// therefore can never have been referenced by a submitted draw.
    /// </summary>
    internal void Abort(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _indices.ReleaseUnsubmitted(allocation.Indices);
        _vertices.ReleaseUnsubmitted(allocation.Vertices);
    }

    // Failed uploads were never submitted, so these matching seams return
    // each range immediately while preserving independent rollback progress.
    internal void AbortIndexRange(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _indices.ReleaseUnsubmitted(allocation.Indices);
    }

    internal void AbortVertexRange(GlobalMeshAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _vertices.ReleaseUnsubmitted(allocation.Vertices);
    }

    private MeshBufferRange AllocateVertices(int count)
    {
        if (_vertices.TryAllocate(count, out MeshBufferRange allocation))
            return allocation;
        throw new InvalidOperationException(
            "Vertex capacity was not migrated before the staged mesh upload was admitted.");
    }

    private MeshBufferRange AllocateIndices(int count)
    {
        if (_indices.TryAllocate(count, out MeshBufferRange allocation))
            return allocation;

        throw new InvalidOperationException(
            "Index capacity was not migrated before the staged mesh upload was admitted.");
    }

    internal static int CalculateGrowthCapacity(
        int capacity,
        int trailingFreeLength,
        int requiredContiguousLength,
        int growthQuantum,
        int maximumCapacity = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(trailingFreeLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trailingFreeLength, capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredContiguousLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(growthQuantum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCapacity, capacity);

        long missing = Math.Max(0L, (long)requiredContiguousLength - trailingFreeLength);
        if (missing == 0)
            return capacity;

        long minimum = checked((long)capacity + missing);
        if (minimum > maximumCapacity)
            throw new NotSupportedException(
                $"A contiguous range of {requiredContiguousLength:N0} elements exceeds the supported arena capacity {maximumCapacity:N0}.");

        long geometric = checked((long)capacity + Math.Max((long)growthQuantum, capacity / 2L));
        long target = Math.Min(maximumCapacity, Math.Max(minimum, geometric));
        return RoundUpToLimit(target, growthQuantum, maximumCapacity);
    }

    internal static bool TryCalculateTrimCapacity(
        int capacity,
        int highWaterMark,
        int initialCapacity,
        int growthQuantum,
        out int trimmedCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(highWaterMark);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(highWaterMark, capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(growthQuantum);

        trimmedCapacity = capacity;
        if (capacity <= initialCapacity)
            return false;

        long withHeadroom = checked(
            highWaterMark + Math.Max((long)growthQuantum, highWaterMark));
        int target = Math.Max(
            initialCapacity,
            RoundUpToLimit(withHeadroom, growthQuantum, int.MaxValue));
        if (target > capacity / 3)
            return false;

        trimmedCapacity = target;
        return true;
    }

    internal GlobalMeshCapacityResult EnsureUploadCapacity(
        int vertexCount,
        int indexCount,
        out GlobalMeshMaintenanceStep step)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(indexCount);
        if (vertexCount > MaximumVertexCapacity)
            throw new NotSupportedException(
                $"Mesh requires {vertexCount:N0} vertices; the supported per-arena maximum is {MaximumVertexCapacity:N0}.");
        if (indexCount > MaximumIndexCapacity)
            throw new NotSupportedException(
                $"Mesh requires {indexCount:N0} indices; the supported per-arena maximum is {MaximumIndexCapacity:N0}.");

        step = default;
        if (_migration is not null)
            return GlobalMeshCapacityResult.MigrationInProgress;
        if (vertexCount <= _vertices.LargestFreeRange
            && indexCount <= _indices.LargestFreeRange)
        {
            return GlobalMeshCapacityResult.Ready;
        }

        BufferKind kind;
        int targetCapacity;
        long copyBytes;
        if (vertexCount > _vertices.LargestFreeRange)
        {
            long minimum = checked(
                (long)_vertices.Capacity
                + Math.Max(0L, (long)vertexCount - _vertices.TrailingFreeLength));
            if (minimum > MaximumVertexCapacity)
                return GlobalMeshCapacityResult.NeedsReclamation;
            kind = BufferKind.Vertices;
            targetCapacity = CalculateGrowthCapacity(
                _vertices.Capacity,
                _vertices.TrailingFreeLength,
                vertexCount,
                VertexGrowthQuantum,
                MaximumVertexCapacity);
            copyBytes = checked((long)_vertices.HighWaterMark * VertexPositionNormalTexture.Size);
        }
        else
        {
            long minimum = checked(
                (long)_indices.Capacity
                + Math.Max(0L, (long)indexCount - _indices.TrailingFreeLength));
            if (minimum > MaximumIndexCapacity)
                return GlobalMeshCapacityResult.NeedsReclamation;
            kind = BufferKind.Indices;
            targetCapacity = CalculateGrowthCapacity(
                _indices.Capacity,
                _indices.TrailingFreeLength,
                indexCount,
                IndexGrowthQuantum,
                MaximumIndexCapacity);
            copyBytes = checked((long)_indices.HighWaterMark * sizeof(ushort));
        }

        long newBytes = CapacityBytesFor(kind, targetCapacity);
        if (newBytes > MaximumPhysicalArenaBytes - PhysicalCapacityBytes)
            return GlobalMeshCapacityResult.NeedsReclamation;

        BeginMigration(kind, targetCapacity, copyBytes);
        step = new GlobalMeshMaintenanceStep(newBytes, 0, 1, false);
        return GlobalMeshCapacityResult.MigrationStarted;
    }

    internal GlobalMeshMaintenanceStep AdvanceMigration(long maximumCopyBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCopyBytes);
        BufferMigration? migration = _migration;
        if (migration is null)
            return default;

        long chunk = CalculateCopyChunk(
            migration.CopyBytes,
            migration.CopiedBytes,
            maximumCopyBytes);
        try
        {
            if (chunk != 0)
            {
                migration.OldBuffer.CopyTo(
                    migration.NewBuffer,
                    migration.CopiedBytes,
                    migration.CopiedBytes,
                    chunk);
                migration.CopiedBytes = checked(migration.CopiedBytes + chunk);
            }

            bool complete = migration.CopiedBytes == migration.CopyBytes;
            if (complete)
                CommitMigration(migration);
            return new GlobalMeshMaintenanceStep(0, chunk, 0, complete);
        }
        catch (Exception migrationError)
        {
            try
            {
                AbortMigration(migration);
            }
            catch (Exception abortError)
            {
                throw new AggregateException(
                    "Global mesh migration failed and its staged buffer could not yet be released.",
                    migrationError,
                    abortError);
            }
            throw;
        }
    }

    internal bool TryTrimUnusedTail(out GlobalMeshMaintenanceStep step)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();
        step = default;
        if (_migration is not null)
            return false;
        bool trimVertices = TryCalculateTrimCapacity(
            _vertices.Capacity, _vertices.HighWaterMark,
            InitialVertexCapacity, VertexGrowthQuantum,
            out int vertexCapacity);
        bool trimIndices = TryCalculateTrimCapacity(
            _indices.Capacity, _indices.HighWaterMark,
            InitialIndexCapacity, IndexGrowthQuantum,
            out int indexCapacity);

        long vertexSaving = trimVertices
            ? (long)(_vertices.Capacity - vertexCapacity) * VertexPositionNormalTexture.Size
            : 0;
        long indexSaving = trimIndices
            ? (long)(_indices.Capacity - indexCapacity) * sizeof(ushort)
            : 0;
        if (vertexSaving == 0 && indexSaving == 0)
            return false;

        BufferKind kind;
        int capacity;
        long copyBytes;
        if (vertexSaving >= indexSaving)
        {
            kind = BufferKind.Vertices;
            capacity = vertexCapacity;
            copyBytes = checked((long)_vertices.HighWaterMark * VertexPositionNormalTexture.Size);
        }
        else
        {
            kind = BufferKind.Indices;
            capacity = indexCapacity;
            copyBytes = checked((long)_indices.HighWaterMark * sizeof(ushort));
        }

        long newBytes = CapacityBytesFor(kind, capacity);
        if (newBytes > MaximumPhysicalArenaBytes - PhysicalCapacityBytes)
            return false;
        BeginMigration(kind, capacity, copyBytes);
        step = new GlobalMeshMaintenanceStep(newBytes, 0, 1, false);
        return true;
    }

    internal static long CalculateCopyChunk(long totalBytes, long copiedBytes, long maximumCopyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(copiedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(copiedBytes, totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCopyBytes);
        return Math.Min(totalBytes - copiedBytes, maximumCopyBytes);
    }

    private void BeginMigration(BufferKind kind, int newCapacity, long copyBytes)
    {
        if (_migration is not null || _migrationAbort is not null)
            throw new InvalidOperationException("Only one global mesh backing buffer may migrate at a time.");
        int oldCapacity = kind == BufferKind.Vertices ? _vertices.Capacity : _indices.Capacity;
        IGpuBuffer oldBuffer = RequireStore(
            kind == BufferKind.Vertices ? _vertexBuffer : _indexBuffer);
        long oldBytes = CapacityBytesFor(kind, oldCapacity);
        long newBytes = CapacityBytesFor(kind, newCapacity);

        IGpuBuffer newBuffer = _device.CreateBuffer(
            DescribeStore(kind, newBytes, checked(++_storeGeneration)));

        GpuMemoryTracker.TrackResourceAllocation(GpuResourceType.Buffer);
        GpuMemoryTracker.TrackAllocation(newBytes, GpuResourceType.Buffer);
        _migration = new BufferMigration(
            kind,
            oldBuffer,
            newBuffer,
            oldCapacity,
            newCapacity,
            oldBytes,
            newBytes,
            copyBytes);
    }

    private void CommitMigration(BufferMigration migration)
    {
        // The atomic publication step is nothing at all here: the vertex source
        // is a per-draw pass-encoder bind, so the field swap below IS the
        // publication, and the next pass reads the new store.
        if (migration.Kind == BufferKind.Vertices)
        {
            _vertexBuffer = migration.NewBuffer;
            if (migration.NewCapacity > migration.OldCapacity)
                _vertices.Grow(migration.NewCapacity);
            else
                _vertices.Shrink(migration.NewCapacity);
        }
        else
        {
            _indexBuffer = migration.NewBuffer;
            if (migration.NewCapacity > migration.OldCapacity)
                _indices.Grow(migration.NewCapacity);
            else
                _indices.Shrink(migration.NewCapacity);
        }

        _migration = null;
        _retiredCapacityBytes = checked(_retiredCapacityBytes + migration.OldCapacityBytes);
        RetryableGpuResourceRelease oldBufferRelease =
            CreateRetryableStoreDeletion(
                migration.OldBuffer,
                migration.OldCapacityBytes,
                $"retiring replaced global {migration.Kind} arena buffer '{migration.OldBuffer.Name}'");
        _retirementLedger.Retire(new RetryableGpuResourceRelease(
            oldBufferRelease.Run,
            () => _retiredCapacityBytes = checked(
                _retiredCapacityBytes - migration.OldCapacityBytes)));
    }

    private void AbortMigration(BufferMigration migration)
    {
        if (!ReferenceEquals(_migration, migration))
            return;
        _migrationAbort ??= new GlobalMeshMigrationAbortTicket(
            migration.NewBuffer,
            migration.NewCapacityBytes,
            CreateRetryableStoreDeletion(
                migration.NewBuffer,
                migration.NewCapacityBytes,
                $"aborting staged global {migration.Kind} arena buffer '{migration.NewBuffer.Name}'"));
        RetryPendingMigrationAbort();
    }

    private RetryableGpuResourceRelease CreateRetryableStoreDeletion(
        IGpuBuffer buffer,
        long capacityBytes,
        string context)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        return new RetryableGpuResourceRelease(
            () => { },
            () => buffer.Dispose(),
            () =>
            {
                if (capacityBytes != 0)
                    GpuMemoryTracker.TrackDeallocation(capacityBytes, GpuResourceType.Buffer);
            },
            () => GpuMemoryTracker.TrackResourceDeallocation(GpuResourceType.Buffer));
    }

    private void RetryPendingMigrationAbort()
    {
        GlobalMeshMigrationAbortTicket? ticket = _migrationAbort;
        if (ticket is null)
            return;

        try
        {
            ticket.Advance();
        }
        finally
        {
            if (ticket.IsComplete)
            {
                BufferMigration migration = _migration
                    ?? throw new InvalidOperationException(
                        "A staged-buffer abort ticket outlived its migration record.");
                if (!ReferenceEquals(migration.NewBuffer, ticket.Buffer)
                    || migration.NewCapacityBytes != ticket.CapacityBytes)
                {
                    throw new InvalidOperationException(
                        "A staged-buffer abort ticket no longer matches its migration record.");
                }

                _migrationAbort = null;
                _migration = null;
            }
        }
    }

    private static long CapacityBytesFor(BufferKind kind, int capacity) => kind switch
    {
        BufferKind.Vertices => checked((long)capacity * VertexPositionNormalTexture.Size),
        BufferKind.Indices => checked((long)capacity * sizeof(ushort)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int RoundUpToLimit(long value, int quantum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        long remainder = value % quantum;
        long rounded = remainder == 0 ? value : checked(value + quantum - remainder);
        if (rounded > maximum)
            rounded = maximum;
        return checked((int)rounded);
    }


    public void Dispose()
    {
        if (_disposed)
            return;
        _retirementLedger.RetryPendingPublications();
        RetryPendingMigrationAbort();

        if (_disposeResources is null)
        {
            var releases = new List<(string Name, Action Release)>();
            if (_migration is { } migration)
            {
                RetryableGpuResourceRelease release =
                    CreateRetryableStoreDeletion(
                        migration.NewBuffer,
                        migration.NewCapacityBytes,
                        $"deleting staged global {migration.Kind} arena buffer '{migration.NewBuffer.Name}'");
                releases.Add(("staged-migration-buffer", release.Run));
            }

            if (_vertexBuffer is { } vertexStore)
            {
                RetryableGpuResourceRelease release =
                    CreateRetryableStoreDeletion(
                        vertexStore,
                        (long)_vertices.Capacity * VertexPositionNormalTexture.Size,
                        $"deleting global mesh vertex buffer '{vertexStore.Name}'");
                releases.Add(("global-vbo", release.Run));
            }
            if (_indexBuffer is { } indexStore)
            {
                RetryableGpuResourceRelease release =
                    CreateRetryableStoreDeletion(
                        indexStore,
                        (long)_indices.Capacity * sizeof(ushort),
                        $"deleting global mesh index buffer '{indexStore.Name}'");
                releases.Add(("global-ibo", release.Run));
            }
            _disposeResources = new RetryableResourceReleaseLedger(releases);
        }

        ResourceReleaseAttempt attempt = _disposeResources.Advance();
        if (!_disposeResources.IsComplete)
            throw attempt.ToException(
                "One or more global mesh-buffer resources could not be released.");

        _migration = null;
        _migrationAbort = null;
        _vertexBuffer = null;
        _indexBuffer = null;
        _disposeResources = null;
        _disposed = true;

        if (attempt.HasFailures)
            throw attempt.ToException(
                "Global mesh-buffer resources released with exceptional committed outcomes.");
    }
}
