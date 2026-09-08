using AcDream.Core.Net.Messages;
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

internal sealed class LiveEntityLivenessController
{
    internal const float ConservativeVisibilityDistance = 384f;
    private const double MaintenanceIntervalSeconds = 1.0;

    private readonly LiveEntityRuntime _runtime;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILiveEntityPruneSink _prune;
    private readonly DormantLiveEntityStore? _dormant;
    private readonly LiveEntityLivenessTracker _tracker = new();
    private readonly List<LiveEntityLivenessSample> _samples = new();
    private double _nextMaintenanceAt;

    public LiveEntityLivenessController(
        LiveEntityRuntime runtime,
        ILocalPlayerIdentitySource identity,
        ILiveEntityPruneSink prune,
        DormantLiveEntityStore? dormant = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _prune = prune ?? throw new ArgumentNullException(nameof(prune));
        _dormant = dormant;
    }

    public void Tick(double now)
    {
        if (now < _nextMaintenanceAt)
            return;
        _nextMaintenanceAt = now + MaintenanceIntervalSeconds;

        uint playerGuid = _identity.ServerGuid;
        if (playerGuid == 0
            || !_runtime.TryGetRecord(playerGuid, out LiveEntityRecord player)
            || player.Snapshot.Position is not { } playerPosition)
        {
            return;
        }

        _samples.Clear();
        foreach (LiveEntityRecord record in _runtime.Records)
        {
            if (record.ServerGuid == playerGuid
                || record.Snapshot.Position is not { } position)
            {
                continue;
            }

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
                record.IsSpatiallyVisible
                    || IsWithinConservativeVisibility(playerPosition, position),
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
        _dormant?.Clear();
        _samples.Clear();
        _nextMaintenanceAt = 0;
    }

    internal static bool IsWithinConservativeVisibility(
        CreateObject.ServerPosition player,
        CreateObject.ServerPosition entity)
    {
        (double playerX, double playerY) = GlobalXY(player);
        (double entityX, double entityY) = GlobalXY(entity);
        double dx = entityX - playerX;
        double dy = entityY - playerY;
        double dz = entity.PositionZ - player.PositionZ;
        double maximum = ConservativeVisibilityDistance;
        return dx * dx + dy * dy + dz * dz <= maximum * maximum;
    }

    private static (double X, double Y) GlobalXY(CreateObject.ServerPosition position)
    {
        uint landblockX = (position.LandblockId >> 24) & 0xFFu;
        uint landblockY = (position.LandblockId >> 16) & 0xFFu;
        return (
            landblockX * 192.0 + position.PositionX,
            landblockY * 192.0 + position.PositionY);
    }

    private static bool NonZero(uint? value) => value.GetValueOrDefault() != 0u;
}
