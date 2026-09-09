using AcDream.Content.Pak;
using AcDream.Core.Physics;

namespace AcDream.Content;

public readonly record struct SharedPreparedCollisionCacheSnapshot(
    int Count,
    int Capacity,
    long Hits,
    long Misses,
    bool IsDisposed)
{
    public bool IsBounded => Count >= 0 && Count <= Capacity;
}

public sealed class SharedPreparedCollisionCache
    : IPreparedCollisionSource
{
    public const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly IPreparedCollisionSource _source;
    private readonly int _capacity;
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>>
        _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _probes;
    private long _reads;
    private long _loaded;
    private long _missing;
    private long _corrupt;
    private long _hits;
    private long _misses;
    private bool _disposed;

    public SharedPreparedCollisionCache(
        IPreparedCollisionSource source,
        int capacity = DefaultCapacity)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public PreparedCollisionSourceStats CollisionStats => new(
        Interlocked.Read(ref _probes),
        Interlocked.Read(ref _reads),
        Interlocked.Read(ref _loaded),
        Interlocked.Read(ref _missing),
        Interlocked.Read(ref _corrupt));

    public SharedPreparedCollisionCacheSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new SharedPreparedCollisionCacheSnapshot(
                _entries.Count,
                _capacity,
                Interlocked.Read(ref _hits),
                Interlocked.Read(ref _misses),
                _disposed);
        }
    }

    public PreparedAssetPresence ProbeCollision(
        PakAssetType type,
        uint sourceFileId)
    {
        PreparedCollisionTypeContract.Validate(type);
        Interlocked.Increment(ref _probes);
        lock (_gate)
        {
            EnsureNotDisposed();
            if (_entries.TryGetValue(
                    new CacheKey(type, sourceFileId),
                    out LinkedListNode<CacheEntry>? node))
            {
                Touch(node);
                return node.Value.Status switch
                {
                    PreparedAssetReadStatus.Loaded =>
                        PreparedAssetPresence.Available,
                    PreparedAssetReadStatus.Corrupt =>
                        PreparedAssetPresence.Corrupt,
                    _ => PreparedAssetPresence.Missing,
                };
            }
            return _source.ProbeCollision(type, sourceFileId);
        }
    }

    public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
        ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        Read(
            PakAssetType.GfxObjCollision,
            sourceFileId,
            token => _source.ReadGfxObjCollision(sourceFileId, token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatSetupCollision>
        ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        Read(
            PakAssetType.SetupCollision,
            sourceFileId,
            token => _source.ReadSetupCollision(sourceFileId, token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
        ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        Read(
            PakAssetType.CellStructureCollision,
            sourceFileId,
            token => _source.ReadCellStructureCollision(
                sourceFileId,
                token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatEnvCellTopology>
        ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        Read(
            PakAssetType.EnvCellTopology,
            sourceFileId,
            token => _source.ReadEnvCellTopology(sourceFileId, token),
            cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _entries.Clear();
            _lru.Clear();
            _disposed = true;
        }
    }

    private PreparedCollisionReadResult<T> Read<T>(
        PakAssetType type,
        uint sourceFileId,
        Func<CancellationToken, PreparedCollisionReadResult<T>> loader,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);
        lock (_gate)
        {
            EnsureNotDisposed();
            var key = new CacheKey(type, sourceFileId);
            if (_entries.TryGetValue(
                    key,
                    out LinkedListNode<CacheEntry>? node))
            {
                Touch(node);
                Interlocked.Increment(ref _hits);
                return CountAndConvert<T>(node.Value);
            }

            Interlocked.Increment(ref _misses);
            PreparedCollisionReadResult<T> loaded =
                loader(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new CacheEntry(
                key,
                loaded.Status,
                loaded.Data);
            LinkedListNode<CacheEntry> added = _lru.AddFirst(entry);
            _entries.Add(key, added);
            Trim();
            return CountAndConvert<T>(entry);
        }
    }

    private PreparedCollisionReadResult<T> CountAndConvert<T>(
        CacheEntry entry)
        where T : class
    {
        switch (entry.Status)
        {
            case PreparedAssetReadStatus.Loaded
                when entry.Data is T data:
                Interlocked.Increment(ref _loaded);
                return PreparedCollisionReadResult<T>.Loaded(data);
            case PreparedAssetReadStatus.Missing:
                Interlocked.Increment(ref _missing);
                return PreparedCollisionReadResult<T>.Missing;
            case PreparedAssetReadStatus.Corrupt:
                Interlocked.Increment(ref _corrupt);
                return PreparedCollisionReadResult<T>.Corrupt;
            default:
                throw new InvalidDataException(
                    $"Prepared collision cache entry {entry.Key} has an invalid payload.");
        }
    }

    private void Touch(LinkedListNode<CacheEntry> node)
    {
        if (ReferenceEquals(_lru.First, node))
            return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            LinkedListNode<CacheEntry> tail =
                _lru.Last
                ?? throw new InvalidOperationException(
                    "Prepared collision LRU lost its terminal entry.");
            _entries.Remove(tail.Value.Key);
            _lru.RemoveLast();
        }
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct CacheKey(
        PakAssetType Type,
        uint SourceFileId);

    private sealed record CacheEntry(
        CacheKey Key,
        PreparedAssetReadStatus Status,
        object? Data);
}
