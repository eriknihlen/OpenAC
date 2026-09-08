using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using AcDream.Core.Content;

namespace AcDream.Core.Audio;

public readonly record struct DatSoundCacheDiagnostics(
    int CachedWaveCount,
    long ResidentWaveBytes,
    long BudgetBytes,
    long Hits,
    long Misses,
    long Evictions);

public sealed class DatSoundCache
{
    internal const long DefaultMaxWaveBytes = 32L * 1024 * 1024; // 32 MiB

    private readonly IDatObjectSource _dats;
    private readonly ConcurrentDictionary<uint, SoundTable?> _tables = new();

    private readonly ConcurrentDictionary<uint, byte> _negativeWaveIds = new();

    private readonly ConcurrentDictionary<uint, Lazy<WaveData?>> _inflight = new();

    // Payload LRU: resident decoded waves, bounded by _maxWaveBytes.
    private readonly object _gate = new();
    private readonly Dictionary<uint, LinkedListNode<WaveEntry>> _waveEntries = new();
    private readonly LinkedList<WaveEntry> _waveLru = new();
    private readonly long _maxWaveBytes;
    private readonly Action<uint>? _afterInitialWaveMiss;
    private long _residentWaveBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    private sealed record WaveEntry(uint WaveId, WaveData Wave, long Bytes);

    public DatSoundCache(IDatObjectSource dats)
        : this(dats, DefaultMaxWaveBytes)
    {
    }

    /// <summary>
    /// Test seam for pinning a smaller byte budget so eviction can be
    /// exercised deterministically without allocating tens of megabytes.
    /// </summary>
    public DatSoundCache(IDatObjectSource dats, long maxWaveBytes)
        : this(dats, maxWaveBytes, afterInitialWaveMiss: null)
    {
    }

    internal DatSoundCache(
        IDatObjectSource dats,
        long maxWaveBytes,
        Action<uint>? afterInitialWaveMiss)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWaveBytes, 1);
        _dats = dats;
        _maxWaveBytes = maxWaveBytes;
        _afterInitialWaveMiss = afterInitialWaveMiss;
    }

    public WaveData? GetWave(uint waveId)
    {
        lock (_gate)
        {
            if (_waveEntries.TryGetValue(waveId, out var node))
            {
                Interlocked.Increment(ref _hits);
                Touch(node);
                return node.Value.Wave;
            }
        }

        if (_negativeWaveIds.ContainsKey(waveId))
        {
            Interlocked.Increment(ref _hits);
            return null;
        }

        _afterInitialWaveMiss?.Invoke(waveId);

        Lazy<WaveData?> lazy;
        lock (_gate)
        {
            if (_waveEntries.TryGetValue(waveId, out var node))
            {
                Interlocked.Increment(ref _hits);
                Touch(node);
                return node.Value.Wave;
            }

            if (_negativeWaveIds.ContainsKey(waveId))
            {
                Interlocked.Increment(ref _hits);
                return null;
            }

            Interlocked.Increment(ref _misses);
            lazy = _inflight.GetOrAdd(
                waveId,
                static (id, self) => new Lazy<WaveData?>(
                    () => self.DecodeUncached(id),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                this);
        }
        try
        {
            WaveData? decoded = lazy.Value;
            if (decoded is null)
            {
                _negativeWaveIds.TryAdd(waveId, 0);
                return null;
            }
            return Admit(waveId, decoded);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<uint, Lazy<WaveData?>>(waveId, lazy));
        }
    }

    /// <summary>
    /// Retrieve a SoundTable by dat id. Returns null if the table is
    /// missing from the dats.
    /// </summary>
    public SoundTable? GetSoundTable(uint soundTableId)
    {
        if (_tables.TryGetValue(soundTableId, out var cached)) return cached;

        var table = _dats.Get<SoundTable>(soundTableId);
        _tables[soundTableId] = table;
        return table;
    }

    public int CachedWaveCount
    {
        get { lock (_gate) return _waveEntries.Count; }
    }

    public long ResidentWaveBytes
    {
        get { lock (_gate) return _residentWaveBytes; }
    }

    /// <summary>
    /// Total number of SoundTables that have been accessed.
    /// </summary>
    public int CachedSoundTableCount => _tables.Count;

    public DatSoundCacheDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new DatSoundCacheDiagnostics(
                    _waveEntries.Count,
                    _residentWaveBytes,
                    _maxWaveBytes,
                    Interlocked.Read(ref _hits),
                    Interlocked.Read(ref _misses),
                    Interlocked.Read(ref _evictions));
            }
        }
    }

    private WaveData? DecodeUncached(uint waveId)
    {
        var wave = _dats.Get<Wave>(waveId);
        if (wave is null) return null;
        return WaveDecoder.Decode(wave.Header, wave.Data);
    }

    private WaveData Admit(uint waveId, WaveData wave)
    {
        long bytes = Math.Max(1L, (long)wave.PcmBytes.Length);
        lock (_gate)
        {
            if (_waveEntries.TryGetValue(waveId, out var existing))
            {
                Touch(existing);
                return existing.Value.Wave;
            }

            if (bytes > _maxWaveBytes)
            {
                // Larger than the entire budget: serve it but don't retain
                // it — matches BoundedDatObjectCache's oversize handling.
                return wave;
            }

            while (_waveLru.First is { } oldest
                   && _residentWaveBytes + bytes > _maxWaveBytes)
            {
                Evict(oldest);
            }

            var node = _waveLru.AddLast(new WaveEntry(waveId, wave, bytes));
            _waveEntries[waveId] = node;
            _residentWaveBytes += bytes;
            return wave;
        }
    }

    private void Touch(LinkedListNode<WaveEntry> node)
    {
        if (!ReferenceEquals(node, _waveLru.Last))
        {
            _waveLru.Remove(node);
            _waveLru.AddLast(node);
        }
    }

    private void Evict(LinkedListNode<WaveEntry> node)
    {
        _waveLru.Remove(node);
        _waveEntries.Remove(node.Value.WaveId);
        _residentWaveBytes -= node.Value.Bytes;
        Interlocked.Increment(ref _evictions);
    }
}
