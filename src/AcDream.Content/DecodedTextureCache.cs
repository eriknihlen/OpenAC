using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

namespace AcDream.Content;

internal sealed class DecodedTextureCache {
    private sealed record Entry(DecodedTextureKey Key, byte[] Pixels);

    private readonly object _gate = new();
    private readonly Dictionary<DecodedTextureKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly ConcurrentDictionary<DecodedTextureKey, Lazy<byte[]>> _inflight = new();
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private long _residentBytes;

    private long _hits;
    private long _misses;
    private long _evictions;

    public CacheStats Stats => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _evictions));

    public DecodedTextureCache(long maximumBytes, int maximumEntries) {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);
        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
    }

    public int Count {
        get {
            lock (_gate) {
                return _entries.Count;
            }
        }
    }

    public long ResidentBytes {
        get {
            lock (_gate) {
                return _residentBytes;
            }
        }
    }

    public bool TryGet(DecodedTextureKey key, out byte[] pixels) {
        lock (_gate) {
            if (!_entries.TryGetValue(key, out var node)) {
                Interlocked.Increment(ref _misses);
                pixels = null!;
                return false;
            }

            Interlocked.Increment(ref _hits);
            Touch(node);
            pixels = node.Value.Pixels;
            return true;
        }
    }

    public byte[] RetainOrUse(DecodedTextureKey key, byte[] pixels, out bool isCached) {
        ArgumentNullException.ThrowIfNull(pixels);

        lock (_gate) {
            if (_entries.TryGetValue(key, out var existing)) {
                Touch(existing);
                isCached = true;
                return existing.Value.Pixels;
            }

            if (_maximumEntries == 0 || pixels.LongLength > _maximumBytes) {
                isCached = false;
                return pixels;
            }

            while (_lru.First is { } oldest
                   && (_entries.Count >= _maximumEntries
                       || _residentBytes + pixels.LongLength > _maximumBytes)) {
                Remove(oldest);
            }

            var node = _lru.AddLast(new Entry(key, pixels));
            _entries.Add(key, node);
            _residentBytes += pixels.LongLength;
            isCached = true;
            return pixels;
        }
    }

    public byte[] GetOrCreate(
        DecodedTextureKey key,
        Func<byte[]> factory,
        out bool isCached) {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet(key, out byte[] cached)) {
            isCached = true;
            return cached;
        }

        var candidate = new Lazy<byte[]>(
            factory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<byte[]> shared = _inflight.GetOrAdd(key, candidate);
        try {
            byte[] decoded = shared.Value;
            return RetainOrUse(key, decoded, out isCached);
        }
        finally {
            _inflight.TryRemove(
                new KeyValuePair<DecodedTextureKey, Lazy<byte[]>>(key, shared));
        }
    }

    private void Touch(LinkedListNode<Entry> node) {
        if (node != _lru.Last) {
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }

    private void Remove(LinkedListNode<Entry> node) {
        _lru.Remove(node);
        _entries.Remove(node.Value.Key);
        _residentBytes -= node.Value.Pixels.LongLength;
        Interlocked.Increment(ref _evictions);
    }
}

internal readonly record struct DecodedTextureKey(
    uint RenderSurfaceId,
    bool IsClipMap,
    bool IsAdditive);
