using AcDream.App.Input;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal readonly record struct LiveEntityLivenessSample(
    RuntimeEntityKey Key,
    uint ServerGuid,
    bool IsConservativelyVisible,
    bool HasNonWorldRetention);

internal readonly record struct LiveEntityPruneCandidate(
    RuntimeEntityKey Key,
    uint ServerGuid,
    ushort Generation)
{
    public LiveEntityPruneCandidate(RuntimeEntityKey key, uint serverGuid)
        : this(key, serverGuid, key.Incarnation)
    {
    }
}

internal sealed class LiveEntityLivenessTracker
{
    internal const double DestructionTimeoutSeconds = 25.0;

    private readonly Dictionary<RuntimeEntityKey, double> _deadlines = [];
    private readonly HashSet<RuntimeEntityKey> _present = [];
    private readonly List<RuntimeEntityKey> _stale = [];
    private readonly List<LiveEntityPruneCandidate> _due = [];

    internal int DeadlineCount => _deadlines.Count;

    internal IReadOnlyList<LiveEntityPruneCandidate> Tick(
        double now,
        IReadOnlyList<LiveEntityLivenessSample> samples)
    {
        _present.Clear();
        _due.Clear();
        for (int i = 0; i < samples.Count; i++)
        {
            LiveEntityLivenessSample sample = samples[i];
            _present.Add(sample.Key);
            if (sample.IsConservativelyVisible || sample.HasNonWorldRetention)
            {
                _deadlines.Remove(sample.Key);
                continue;
            }

            if (!_deadlines.TryGetValue(sample.Key, out double expiresAt))
            {
                _deadlines[sample.Key] = now + DestructionTimeoutSeconds;
                continue;
            }

            if (expiresAt > now)
                continue;

            _due.Add(new LiveEntityPruneCandidate(sample.Key, sample.ServerGuid));
            _deadlines.Remove(sample.Key);
        }

        _stale.Clear();
        foreach (RuntimeEntityKey key in _deadlines.Keys)
        {
            if (!_present.Contains(key))
                _stale.Add(key);
        }
        for (int i = 0; i < _stale.Count; i++)
            _deadlines.Remove(_stale[i]);

        return _due;
    }

    internal void Clear() => _deadlines.Clear();
}

/// <summary>
/// Owns the 25-second destruction deadline for world objects that left
/// visibility. Visibility is the player's landblock and its eight
/// neighbours (a dungeon is its own landblock): the original client only
/// ever holds those cells, releases everything outside them, and the server
/// mirrors the same 3x3 as the player's known-object set, forgetting an
/// object 25 s after it leaves and re-sending it on return. Objects held by
/// a container, wielder, or parent, and attached projections, never expire.
/// </summary>
internal sealed class LiveEntityLivenessController
{
    /// <summary>Landblock Chebyshev radius of the visible neighbourhood.</summary>
    internal const int VisibleLandblockRadius = 1;
    private const double MaintenanceIntervalSeconds = 1.0;

    private readonly LiveEntityRuntime _runtime;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILiveEntityPruneSink _prune;
    private readonly LiveEntityLivenessTracker _tracker = new();
    private readonly List<LiveEntityLivenessSample> _samples = new();
    private double _nextMaintenanceAt;

    public LiveEntityLivenessController(
        LiveEntityRuntime runtime,
        ILocalPlayerIdentitySource identity,
        ILiveEntityPruneSink prune)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _prune = prune ?? throw new ArgumentNullException(nameof(prune));
    }

    public void Tick(double now)
    {
        if (now < _nextMaintenanceAt)
            return;
        _nextMaintenanceAt = now + MaintenanceIntervalSeconds;

        uint playerGuid = _identity.ServerGuid;
        if (playerGuid == 0
            || !_runtime.TryGetRecord(playerGuid, out LiveEntityRecord player))
        {
            return;
        }
        uint playerCell = CellOf(player);
        if (playerCell == 0u)
            return;

        _samples.Clear();
        foreach (LiveEntityRecord record in _runtime.Records)
        {
            if (record.ServerGuid == playerGuid)
                continue;
            uint cell = CellOf(record);
            if (cell == 0u)
                continue;

            bool retained = record.ProjectionKind is LiveEntityProjectionKind.Attached
                || NonZero(record.Snapshot.ContainerId)
                || NonZero(record.Snapshot.WielderId)
                || NonZero(record.Snapshot.ParentGuid);
            _samples.Add(new LiveEntityLivenessSample(
                record.ProjectionKey
                    ?? throw new InvalidOperationException(
                        $"Materialized liveness owner 0x{record.ServerGuid:X8}/" +
                        $"{record.Generation} has no exact projection key."),
                record.ServerGuid,
                IsWithinVisibleLandblocks(playerCell, cell),
                retained));
        }

        IReadOnlyList<LiveEntityPruneCandidate> due = _tracker.Tick(now, _samples);
        for (int i = 0; i < due.Count; i++)
        {
            LiveEntityPruneCandidate candidate = due[i];
            if (_runtime.TryGetRecord(candidate.Key, out LiveEntityRecord current)
                && current.ServerGuid == candidate.ServerGuid)
            {
                _prune.Prune(candidate);
            }
        }
    }

    public void Clear()
    {
        _tracker.Clear();
        _samples.Clear();
        _nextMaintenanceAt = 0;
    }

    /// <summary>
    /// True when <paramref name="entityCell"/>'s landblock is within one
    /// landblock (Chebyshev) of <paramref name="playerCell"/>'s. Both are
    /// cell ids; only the landblock halves matter.
    /// </summary>
    internal static bool IsWithinVisibleLandblocks(uint playerCell, uint entityCell)
    {
        int dx = Math.Abs((int)((playerCell >> 24) & 0xFFu) - (int)((entityCell >> 24) & 0xFFu));
        int dy = Math.Abs((int)((playerCell >> 16) & 0xFFu) - (int)((entityCell >> 16) & 0xFFu));
        return Math.Max(dx, dy) <= VisibleLandblockRadius;
    }

    /// <summary>The record's committed cell, else its accepted spawn cell, else 0.</summary>
    private static uint CellOf(LiveEntityRecord record)
    {
        if (record.FullCellId != 0u)
            return record.FullCellId;
        return record.Snapshot.Position?.LandblockId ?? 0u;
    }

    private static bool NonZero(uint? value) => value.GetValueOrDefault() != 0u;
}
