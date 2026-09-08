using System;
using System.Collections.Generic;

namespace AcDream.App.Audio;

internal sealed class AlBufferBudgetTracker
{
    private sealed class Entry
    {
        public required uint WaveId { get; init; }
        public required uint BufferId { get; init; }
        public required long Bytes { get; init; }
        public long LastUseTick { get; set; }
    }

    private readonly Dictionary<uint, Entry> _byWaveId = new();
    private long _tick;

    public AlBufferBudgetTracker(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public long ResidentBytes { get; private set; }

    public int Count => _byWaveId.Count;

    public bool TryGetBufferId(uint waveId, out uint bufferId)
    {
        if (_byWaveId.TryGetValue(waveId, out var entry))
        {
            bufferId = entry.BufferId;
            return true;
        }
        bufferId = 0;
        return false;
    }

    /// <summary>
    /// Record a freshly-allocated, freshly-uploaded buffer as resident and
    /// most-recently-used.
    /// </summary>
    public void RecordCreated(uint waveId, uint bufferId, long bytes)
    {
        long charged = Math.Max(1L, bytes);
        _byWaveId[waveId] = new Entry
        {
            WaveId = waveId,
            BufferId = bufferId,
            Bytes = charged,
            LastUseTick = ++_tick,
        };
        ResidentBytes += charged;
    }

    public void Touch(uint waveId)
    {
        if (_byWaveId.TryGetValue(waveId, out var entry))
            entry.LastUseTick = ++_tick;
    }

    public bool TryEvictOldestUnprotected(
        Func<uint, bool> isProtected,
        out uint evictedWaveId,
        out uint evictedBufferId)
    {
        ArgumentNullException.ThrowIfNull(isProtected);

        Entry? victim = null;
        foreach (Entry candidate in _byWaveId.Values)
        {
            if (isProtected(candidate.BufferId))
                continue;
            if (victim is null || candidate.LastUseTick < victim.LastUseTick)
                victim = candidate;
        }

        if (victim is null)
        {
            evictedWaveId = 0;
            evictedBufferId = 0;
            return false;
        }

        _byWaveId.Remove(victim.WaveId);
        ResidentBytes -= victim.Bytes;
        evictedWaveId = victim.WaveId;
        evictedBufferId = victim.BufferId;
        return true;
    }
}
