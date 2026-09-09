using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace AcDream.Content;

internal sealed class BoundedDatObjectCache
{
    internal const int DefaultEntryLimit = 256;
    internal const long DefaultEstimatedByteLimit = 64L * 1024L * 1024L;
    private const long UnknownObjectEstimate = 128L * 1024L;

    private readonly record struct CacheKey(Type ObjectType, uint FileId);

    private sealed record CacheEntry(
        CacheKey Key,
        IDBObj Value,
        long EstimatedBytes);

    private readonly int _entryLimit;
    private readonly long _estimatedByteLimit;
    private readonly Func<IDBObj, long> _estimateRetainedBytes;
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _byKey = new();
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = new();
    private readonly object _gate = new();
    private long _estimatedBytes;

    private long _hits;
    private long _misses;
    private long _evictions;

    internal CacheStats Stats => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _evictions));

    internal BoundedDatObjectCache(
        int entryLimit = DefaultEntryLimit,
        long estimatedByteLimit = DefaultEstimatedByteLimit,
        Func<IDBObj, long>? estimateRetainedBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entryLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedByteLimit, 1);

        _entryLimit = entryLimit;
        _estimatedByteLimit = estimatedByteLimit;
        _estimateRetainedBytes = estimateRetainedBytes ?? EstimateRetainedBytes;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _byKey.Count;
        }
    }

    internal long EstimatedBytes
    {
        get
        {
            lock (_gate)
                return _estimatedBytes;
        }
    }

    internal bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
        where T : IDBObj
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(new CacheKey(typeof(T), fileId), out var node))
            {
                Interlocked.Increment(ref _misses);
                value = default;
                return false;
            }

            Interlocked.Increment(ref _hits);
            MarkMostRecentlyUsed(node);
            value = (T)node.Value.Value;
            return true;
        }
    }

    internal T GetOrAdd<T>(uint fileId, T candidate)
        where T : IDBObj
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var key = new CacheKey(typeof(T), fileId);
        long estimatedBytes = Math.Max(1L, _estimateRetainedBytes(candidate));

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                MarkMostRecentlyUsed(existing);
                return (T)existing.Value.Value;
            }

            if (estimatedBytes > _estimatedByteLimit)
                return candidate;

            while (_byKey.Count >= _entryLimit
                || _estimatedBytes > _estimatedByteLimit - estimatedBytes)
            {
                EvictLeastRecentlyUsed();
            }

            var entry = new CacheEntry(key, candidate, estimatedBytes);
            var node = _leastRecentlyUsed.AddLast(entry);
            _byKey.Add(key, node);
            _estimatedBytes += estimatedBytes;
            return candidate;
        }
    }

    private void MarkMostRecentlyUsed(LinkedListNode<CacheEntry> node)
    {
        if (!ReferenceEquals(node, _leastRecentlyUsed.Last))
        {
            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddLast(node);
        }
    }

    private void EvictLeastRecentlyUsed()
    {
        LinkedListNode<CacheEntry>? node = _leastRecentlyUsed.First;
        if (node is null)
            throw new InvalidOperationException("DAT object cache accounting is inconsistent.");

        _leastRecentlyUsed.RemoveFirst();
        _byKey.Remove(node.Value.Key);
        _estimatedBytes -= node.Value.EstimatedBytes;
        Interlocked.Increment(ref _evictions);
    }

    private static long EstimateRetainedBytes(IDBObj value)
    {
        if (value is RenderSurface renderSurface)
            return Math.Max(UnknownObjectEstimate, renderSurface.SourceData.LongLength + 256L);

        return UnknownObjectEstimate;
    }
}
