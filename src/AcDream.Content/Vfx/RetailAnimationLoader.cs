using System.Collections.Concurrent;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;

namespace AcDream.Content.Vfx;

public sealed class RetailAnimationLoader : IAnimationLoader
{
    public const long DefaultMaximumEstimatedBytes = 64L * 1024L * 1024L;
    public const int DefaultMaximumEntries = 512;
    private const long NegativeEntryEstimate = 128;

    private readonly record struct LoadResult(
        Animation? Animation,
        long EstimatedBytes);

    private sealed record CacheEntry(
        uint Id,
        Animation? Animation,
        long EstimatedBytes);

    private readonly Func<uint, byte[]?> _readRaw;
    private readonly DatDatabase? _database;
    private readonly ConcurrentDictionary<uint, Lazy<LoadResult>> _inflight = new();
    private readonly object _gate = new();
    private readonly Dictionary<uint, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = [];
    private readonly long _maximumEstimatedBytes;
    private readonly int _maximumEntries;
    private long _estimatedBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    public RetailAnimationLoader(IDatReaderWriter dats)
        : this(
            dats,
            DefaultMaximumEstimatedBytes,
            DefaultMaximumEntries)
    {
    }

    public RetailAnimationLoader(
        IDatReaderWriter dats,
        long maximumEstimatedBytes,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ValidateBudgets(maximumEstimatedBytes, maximumEntries);
        _database = dats.Portal.Db;
        _readRaw = id => dats.Portal.TryGetFileBytes(id, out byte[]? bytes)
            ? bytes
            : null;
        _maximumEstimatedBytes = maximumEstimatedBytes;
        _maximumEntries = maximumEntries;
    }

    public RetailAnimationLoader(IDatDatabase portal)
        : this(
            portal,
            DefaultMaximumEstimatedBytes,
            DefaultMaximumEntries)
    {
    }

    public RetailAnimationLoader(
        IDatDatabase portal,
        long maximumEstimatedBytes,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ValidateBudgets(maximumEstimatedBytes, maximumEntries);
        _database = portal.Db;
        _readRaw = id => portal.TryGetFileBytes(id, out byte[]? bytes)
            ? bytes
            : null;
        _maximumEstimatedBytes = maximumEstimatedBytes;
        _maximumEntries = maximumEntries;
    }

    public AnimationCacheDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new AnimationCacheDiagnostics(
                    _entries.Count,
                    _estimatedBytes,
                    _maximumEstimatedBytes,
                    _maximumEntries,
                    new CacheStats(
                        Interlocked.Read(ref _hits),
                        Interlocked.Read(ref _misses),
                        Interlocked.Read(ref _evictions)));
            }
        }
    }

    public Animation? LoadAnimation(uint id)
    {
        if (!IsAnimationDid(id))
            return null;

        if (TryGet(id, out Animation? cached))
            return cached;

        Lazy<LoadResult> candidate = new(
            () => LoadUncached(id),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<LoadResult> shared = _inflight.GetOrAdd(
            id,
            candidate);
        try
        {
            LoadResult loaded = shared.Value;
            return Admit(id, loaded);
        }
        finally
        {
            _inflight.TryRemove(
                new KeyValuePair<uint, Lazy<LoadResult>>(id, shared));
        }
    }

    private bool TryGet(uint id, out Animation? animation)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(
                    id,
                    out LinkedListNode<CacheEntry>? node))
            {
                Interlocked.Increment(ref _misses);
                animation = null;
                return false;
            }

            Interlocked.Increment(ref _hits);
            Touch(node);
            animation = node.Value.Animation;
            return true;
        }
    }

    private LoadResult LoadUncached(uint id)
    {
        byte[]? bytes = _readRaw(id);
        if (bytes is null)
            return new LoadResult(null, NegativeEntryEstimate);

        Animation animation = Parse(bytes, _database);
        if (animation.Id != id)
            throw new InvalidDataException(
                $"Animation entry 0x{id:X8} contained id 0x{animation.Id:X8}.");
        return new LoadResult(
            animation,
            EstimateRetainedBytes(animation, bytes.LongLength));
    }

    private Animation? Admit(uint id, LoadResult loaded)
    {
        long estimatedBytes = Math.Max(1, loaded.EstimatedBytes);
        lock (_gate)
        {
            if (_entries.TryGetValue(
                    id,
                    out LinkedListNode<CacheEntry>? existing))
            {
                Touch(existing);
                return existing.Value.Animation;
            }

            if (estimatedBytes > _maximumEstimatedBytes)
                return loaded.Animation;

            while (_entries.Count >= _maximumEntries
                || _estimatedBytes
                    > _maximumEstimatedBytes - estimatedBytes)
            {
                EvictLeastRecentlyUsed();
            }

            var entry = new CacheEntry(
                id,
                loaded.Animation,
                estimatedBytes);
            LinkedListNode<CacheEntry> node =
                _leastRecentlyUsed.AddLast(entry);
            _entries.Add(id, node);
            _estimatedBytes = checked(
                _estimatedBytes + estimatedBytes);
            return loaded.Animation;
        }
    }

    private void Touch(LinkedListNode<CacheEntry> node)
    {
        if (ReferenceEquals(node, _leastRecentlyUsed.Last))
            return;
        _leastRecentlyUsed.Remove(node);
        _leastRecentlyUsed.AddLast(node);
    }

    private void EvictLeastRecentlyUsed()
    {
        LinkedListNode<CacheEntry>? node = _leastRecentlyUsed.First;
        if (node is null)
        {
            throw new InvalidOperationException(
                "Animation cache accounting is inconsistent.");
        }

        _leastRecentlyUsed.RemoveFirst();
        _entries.Remove(node.Value.Id);
        _estimatedBytes = checked(
            _estimatedBytes - node.Value.EstimatedBytes);
        Interlocked.Increment(ref _evictions);
    }

    internal static long EstimateRetainedBytes(
        Animation animation,
        long rawBytes)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentOutOfRangeException.ThrowIfNegative(rawBytes);

        long estimate = 512;
        estimate = checked(
            estimate + animation.PosFrames.Count * 64L);
        for (int i = 0; i < animation.PartFrames.Count; i++)
        {
            AnimationFrame frame = animation.PartFrames[i];
            estimate = checked(
                estimate
                + 96L
                + frame.Frames.Count * 64L
                + frame.Hooks.Count * 192L);
        }
        return Math.Max(rawBytes, estimate);
    }

    private static void ValidateBudgets(
        long maximumEstimatedBytes,
        int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumEstimatedBytes,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
    }

    public static Animation Parse(
        ReadOnlyMemory<byte> bytes,
        DatDatabase? database = null)
    {
        var reader = new DatBinReader(bytes, database);
        var animation = new Animation
        {
            Id = reader.ReadUInt32(),
            Flags = (AnimationFlags)reader.ReadUInt32(),
            NumParts = reader.ReadUInt32(),
        };
        uint frameCount = reader.ReadUInt32();

        if ((animation.Flags & AnimationFlags.PosFrames) != 0)
        {
            for (uint i = 0; i < frameCount; i++)
                animation.PosFrames.Add(reader.ReadItem<Frame>());
        }

        for (uint frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frame = new AnimationFrame(animation.NumParts);
            for (uint partIndex = 0; partIndex < animation.NumParts; partIndex++)
                frame.Frames.Add(reader.ReadItem<Frame>());

            uint hookCount = reader.ReadUInt32();
            for (uint hookIndex = 0; hookIndex < hookCount; hookIndex++)
                frame.Hooks.Add(RetailAnimationHookReader.Read(reader));
            animation.PartFrames.Add(frame);
        }

        if (reader.Offset != reader.Length)
            throw new InvalidDataException(
                $"Animation 0x{animation.Id:X8} consumed {reader.Offset} of {reader.Length} bytes.");
        return animation;
    }

    public static bool IsAnimationDid(uint did) =>
        // AC data IDs reserve only the high byte for DBObj type.
        (did & 0xFF000000u) == 0x03000000u;
}

public readonly record struct AnimationCacheDiagnostics(
    int Count,
    long EstimatedBytes,
    long BudgetBytes,
    int EntryLimit,
    CacheStats Stats);
