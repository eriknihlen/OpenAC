using System.Collections.Concurrent;
using AcDream.Content;

namespace AcDream.App.Rendering.Wb;

internal enum MeshStageResult
{
    Staged,
    AlreadyStaged,
    HighWater,
    NotOwned,
}

internal readonly record struct MeshUploadQueueItem(
    ObjectMeshData Data,
    long SourceBytes,
    ulong Generation);

/// <summary>
/// Deduplicated CPU-to-GPU staging queue. One object id may be queued or
/// in-flight at a time; a retry retains ownership until upload succeeds or
/// exhausts its retry budget.
/// </summary>
internal sealed class MeshUploadStagingQueue
{
    internal const int DefaultMaximumCount = 256;
    internal const long DefaultMaximumBytes = 128L * 1024 * 1024;
    internal const long DefaultMaximumSingleEntryBytes = 128L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Queue<Entry> _queue = new();
    private readonly Dictionary<ulong, ulong> _generationById = new();
    private readonly int _maximumCount;
    private readonly long _maximumBytes;
    private readonly long _maximumSingleEntryBytes;
    private long _queuedBytes;
    private long _claimedBytes;
    private ulong _nextGeneration;

    private readonly record struct Entry(ObjectMeshData Data, long Bytes, ulong Generation)
    {
        public MeshUploadQueueItem Item => new(Data, Bytes, Generation);
    }

    public MeshUploadStagingQueue(
        int maximumCount = DefaultMaximumCount,
        long maximumBytes = DefaultMaximumBytes,
        long maximumSingleEntryBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSingleEntryBytes);
        if (maximumSingleEntryBytes == 0)
            maximumSingleEntryBytes = Math.Max(maximumBytes, DefaultMaximumSingleEntryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSingleEntryBytes, maximumBytes);
        _maximumCount = maximumCount;
        _maximumBytes = maximumBytes;
        _maximumSingleEntryBytes = maximumSingleEntryBytes;
    }

    public bool Stage(ObjectMeshData data)
        => TryStage(data) == MeshStageResult.Staged;

    public MeshStageResult TryStage(ObjectMeshData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        long bytes = ObjectMeshManager.EstimateUploadBytes(data);
        lock (_gate)
        {
            if (_generationById.ContainsKey(data.ObjectId))
                return MeshStageResult.AlreadyStaged;

            if (bytes > _maximumSingleEntryBytes)
                throw new NotSupportedException(
                    $"Mesh 0x{data.ObjectId:X10} requires {bytes:N0} staged bytes; "
                    + $"the supported per-object maximum is {_maximumSingleEntryBytes:N0} bytes.");

            if (_generationById.Count != 0
                && (_generationById.Count >= _maximumCount
                    || bytes > _maximumBytes - Math.Min(_claimedBytes, _maximumBytes)))
            {
                return MeshStageResult.HighWater;
            }

            ulong generation = checked(++_nextGeneration);
            _generationById.Add(data.ObjectId, generation);
            data.UploadAttempts = 0;
            _queue.Enqueue(new Entry(data, bytes, generation));
            _queuedBytes = checked(_queuedBytes + bytes);
            _claimedBytes = checked(_claimedBytes + bytes);
            return MeshStageResult.Staged;
        }
    }

    public bool TryDequeue(out MeshUploadQueueItem item)
    {
        lock (_gate)
        {
            if (!_queue.TryDequeue(out Entry entry))
            {
                item = default;
                return false;
            }
            _queuedBytes -= entry.Bytes;
            item = entry.Item;
            return true;
        }
    }

    public bool TryPeek(out MeshUploadQueueItem item)
    {
        lock (_gate)
        {
            if (!_queue.TryPeek(out Entry entry))
            {
                item = default;
                return false;
            }
            item = entry.Item;
            return true;
        }
    }

    public int Count { get { lock (_gate) return _queue.Count; } }
    public int ClaimCount { get { lock (_gate) return _generationById.Count; } }
    public long QueuedBytes { get { lock (_gate) return _queuedBytes; } }
    public long ClaimedBytes { get { lock (_gate) return _claimedBytes; } }
    public int MaximumCount => _maximumCount;
    public long MaximumBytes => _maximumBytes;
    public bool IsAtHighWater
    {
        get
        {
            lock (_gate)
                return _generationById.Count >= _maximumCount || _claimedBytes >= _maximumBytes;
        }
    }

    public void Requeue(MeshUploadQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item.Data);
        lock (_gate)
        {
            ValidateCurrentGeneration(item);
            if (item.SourceBytes > _maximumSingleEntryBytes)
                throw new InvalidOperationException("Cannot retry mesh data after staging ownership completed.");
            _queue.Enqueue(new Entry(item.Data, item.SourceBytes, item.Generation));
            _queuedBytes = checked(_queuedBytes + item.SourceBytes);
        }
    }

    public void Complete(MeshUploadQueueItem item)
    {
        lock (_gate)
        {
            ValidateCurrentGeneration(item);
            _generationById.Remove(item.Data.ObjectId);
            _claimedBytes = checked(_claimedBytes - item.SourceBytes);
        }
    }

    public bool CompleteOrRestageIfOwned(
        MeshUploadQueueItem item,
        MeshOwnershipCounter ownership)
    {
        ArgumentNullException.ThrowIfNull(item.Data);
        ArgumentNullException.ThrowIfNull(ownership);
        lock (_gate)
        {
            ValidateCurrentGeneration(item);
            _generationById.Remove(item.Data.ObjectId);
            _claimedBytes = checked(_claimedBytes - item.SourceBytes);
            if (!ownership.IsOwned(item.Data.ObjectId))
                return false;

            ulong generation = checked(++_nextGeneration);
            _generationById.Add(item.Data.ObjectId, generation);
            item.Data.UploadAttempts = 0;
            _queue.Enqueue(new Entry(item.Data, item.SourceBytes, generation));
            _queuedBytes = checked(_queuedBytes + item.SourceBytes);
            _claimedBytes = checked(_claimedBytes + item.SourceBytes);
            return true;
        }
    }

    public int DiscardUnownedPrefix(MeshOwnershipCounter ownership, int maximum)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        int discarded = 0;
        lock (_gate)
        {
            while (discarded < maximum
                && _queue.TryPeek(out Entry entry)
                && !ownership.IsOwned(entry.Data.ObjectId))
            {
                _queue.Dequeue();
                _queuedBytes -= entry.Bytes;
                if (_generationById.TryGetValue(entry.Data.ObjectId, out ulong generation)
                    && generation == entry.Generation)
                {
                    _generationById.Remove(entry.Data.ObjectId);
                    _claimedBytes = checked(_claimedBytes - entry.Bytes);
                }
                discarded++;
            }
        }
        return discarded;
    }

    private void ValidateCurrentGeneration(MeshUploadQueueItem item)
    {
        if (!_generationById.TryGetValue(item.Data.ObjectId, out ulong current)
            || current != item.Generation)
        {
            throw new InvalidOperationException(
                $"Stale mesh-upload generation {item.Generation} for 0x{item.Data.ObjectId:X10}; current={current}.");
        }
    }
}

internal sealed class MeshOwnershipCounter
{
    private readonly ConcurrentDictionary<ulong, int> _counts = new();

    public int Acquire(ulong id) => _counts.AddOrUpdate(id, 1, (_, count) => count + 1);

    public int Release(ulong id) => _counts.AddOrUpdate(id, 0, (_, count) => Math.Max(0, count - 1));

    public int Count(ulong id) => _counts.TryGetValue(id, out int count) ? count : 0;

    public bool IsOwned(ulong id) => Count(id) > 0;

    public int OwnedIdCount => _counts.Count(pair => pair.Value > 0);

    public int TotalReferenceCount => _counts.Sum(
        pair => Math.Max(0, pair.Value));

    /// <summary>
    /// Reports whether freshly uploaded data has a live owner. This is a query,
    /// not an acquire: upload existence and logical ownership are independent.
    /// </summary>
    public bool MarkUploadComplete(ulong id) => IsOwned(id);

    public bool Remove(ulong id) => _counts.TryRemove(id, out _);
}

internal sealed class CpuMeshUploadCache
{
    private readonly Dictionary<ulong, ObjectMeshData> _data = new();
    private readonly Dictionary<ulong, long> _bytesById = new();
    private readonly LinkedList<ulong> _lru = new();
    private readonly int _capacity;
    private readonly long _byteCapacity;
    private long _residentBytes;

    private long _hits;
    private long _misses;
    private long _evictions;

    internal CacheStats Stats => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _evictions));

    public CpuMeshUploadCache(int capacity, long byteCapacity = 128L * 1024 * 1024)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCapacity, 1);
        _capacity = capacity;
        _byteCapacity = byteCapacity;
    }

    internal int Count { get { lock (_data) return _data.Count; } }
    internal long ResidentBytes { get { lock (_data) return _residentBytes; } }
    internal int Capacity => _capacity;
    internal long ByteCapacity => _byteCapacity;

    public bool TryGetAndStageOwned(
        ulong id,
        MeshUploadStagingQueue staging,
        MeshOwnershipCounter ownership,
        out ObjectMeshData? data,
        out MeshStageResult stageResult)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(ownership);
        lock (_data)
        {
            if (!_data.TryGetValue(id, out data)) {
                Interlocked.Increment(ref _misses);
                stageResult = default;
                return false;
            }
            Interlocked.Increment(ref _hits);
            _lru.Remove(id);
            _lru.AddLast(id);
            // Prepared payload residency is deliberately independent from
            // live render ownership. A point-of-use lookup from a retiring
            // world generation must never recreate GPU staging work after
            // that generation released its final mesh reference.
            stageResult = ownership.IsOwned(id)
                ? staging.TryStage(data)
                : MeshStageResult.NotOwned;
            return true;
        }
    }

    public bool Store(ObjectMeshData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        long bytes = ObjectMeshManager.EstimateUploadBytes(data);
        if (bytes > _byteCapacity)
            return false;
        lock (_data)
        {
            if (_bytesById.Remove(data.ObjectId, out long replacedBytes))
                _residentBytes -= replacedBytes;

            while (_lru.Count != 0
                && (!_data.ContainsKey(data.ObjectId) && _data.Count >= _capacity
                    || (_data.Count != 0 && bytes > _byteCapacity - _residentBytes)))
            {
                ulong oldest = _lru.First!.Value;
                _lru.RemoveFirst();
                _data.Remove(oldest);
                if (_bytesById.Remove(oldest, out long oldestBytes))
                    _residentBytes -= oldestBytes;
                Interlocked.Increment(ref _evictions);
            }

            _data[data.ObjectId] = data;
            _bytesById[data.ObjectId] = bytes;
            _residentBytes = checked(_residentBytes + bytes);
            _lru.Remove(data.ObjectId);
            _lru.AddLast(data.ObjectId);
            return true;
        }
    }

    public void Clear()
    {
        lock (_data)
        {
            _data.Clear();
            _bytesById.Clear();
            _lru.Clear();
            _residentBytes = 0;
        }
    }
}
