using AcDream.Runtime.Entities;

namespace AcDream.App.World;

/// <summary>
/// App-only projection storage. RuntimeEntityDirectory remains the sole
/// server-GUID/incarnation/local-ID authority; every App sidecar exists only
/// after Runtime has issued its exact local-ID plus INSTANCE_TS key.
/// </summary>
internal sealed class LiveEntityProjectionStore
{
    private readonly RuntimeEntityDirectory _directory;
    private readonly Dictionary<RuntimeEntityKey, LiveEntityRecord> _materialized = new();
    private readonly Dictionary<RuntimeEntityKey, LiveEntityRecord> _visible = new();
    private readonly Dictionary<RuntimeEntityKey, LiveEntityRecord> _teardown = new();
    private readonly Dictionary<RuntimeEntityRecord, LiveEntityRecord>
        _unmaterializedTeardown = new(ReferenceEqualityComparer.Instance);
    private readonly List<LiveEntityRecord> _materializedInRegistrationOrder = [];
    private readonly TeardownRecordCollection _teardownRecords;

    public LiveEntityProjectionStore(RuntimeEntityDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _teardownRecords = new TeardownRecordCollection(
            _teardown,
            _unmaterializedTeardown);
    }

    public int ActiveCount => _materializedInRegistrationOrder.Count;
    public int MaterializedCount => _materialized.Count + _teardown.Count;
    public int TeardownCount => _teardown.Count + _unmaterializedTeardown.Count;
    public IReadOnlyList<LiveEntityRecord> ActiveRecords =>
        _materializedInRegistrationOrder;
    public IReadOnlyList<LiveEntityRecord> Values =>
        _materializedInRegistrationOrder;
    public IReadOnlyCollection<LiveEntityRecord> MaterializedRecords =>
        _materialized.Values;
    public IReadOnlyCollection<LiveEntityRecord> VisibleRecords => _visible.Values;
    public IReadOnlyCollection<LiveEntityRecord> TeardownRecords =>
        _teardownRecords;

    public LiveEntityRecord AddMaterializing(RuntimeEntityRecord canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (!_directory.IsCurrent(canonical))
        {
            throw new InvalidOperationException(
                "An App projection sidecar can only be added for the current Runtime incarnation.");
        }
        RuntimeEntityKey key = canonical.Key
            ?? throw new InvalidOperationException(
                "Runtime must claim the local projection ID before App creates a sidecar.");
        if (_materialized.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{canonical.ServerGuid:X8}/{canonical.Incarnation} already has an App sidecar.");
        }

        var record = new LiveEntityRecord(_directory, canonical)
        {
            ProjectionKey = key,
        };
        _materialized.Add(key, record);
        _materializedInRegistrationOrder.Add(record);
        return record;
    }

    public bool TryGetCurrent(uint serverGuid, out LiveEntityRecord record)
    {
        if (_directory.TryGetActive(serverGuid, out RuntimeEntityRecord canonical))
            return TryGet(canonical, out record);

        record = null!;
        return false;
    }

    public bool ContainsCurrent(uint serverGuid) =>
        TryGetCurrent(serverGuid, out _);

    public LiveEntityRecord? GetCurrentOrDefault(uint serverGuid) =>
        TryGetCurrent(serverGuid, out LiveEntityRecord record) ? record : null;

    public bool TryGet(RuntimeEntityRecord canonical, out LiveEntityRecord record)
    {
        if (canonical.Key is { } key
            && _materialized.TryGetValue(key, out LiveEntityRecord? candidate)
            && ReferenceEquals(candidate.Canonical, canonical))
        {
            record = candidate;
            return true;
        }

        record = null!;
        return false;
    }

    public bool TryGet(RuntimeEntityKey key, out LiveEntityRecord record) =>
        _materialized.TryGetValue(key, out record!);

    public bool TryGetByLocalId(uint localEntityId, out LiveEntityRecord record)
    {
        if (_directory.TryGetByLocalId(
                localEntityId,
                out RuntimeEntityRecord canonical))
        {
            return TryGet(canonical, out record);
        }

        record = null!;
        return false;
    }

    public bool IsCurrent(LiveEntityRecord record) =>
        _directory.IsCurrent(record.Canonical)
        && TryGet(record.Canonical, out LiveEntityRecord current)
        && ReferenceEquals(current, record);

    public void SetVisible(LiveEntityRecord record, bool visible)
    {
        RuntimeEntityKey key = RequireProjectionKey(record);
        if (!_materialized.TryGetValue(key, out LiveEntityRecord? materialized)
            || !ReferenceEquals(materialized, record))
        {
            throw new InvalidOperationException(
                "Only a current exact App sidecar can change visibility.");
        }

        if (visible)
            _visible[key] = record;
        else
            _visible.Remove(key);
    }

    public bool TryGetVisibleCurrent(uint serverGuid, out LiveEntityRecord record)
    {
        if (_directory.TryGetActive(serverGuid, out RuntimeEntityRecord canonical)
            && canonical.Key is { } key
            && _visible.TryGetValue(key, out LiveEntityRecord? visible)
            && ReferenceEquals(visible.Canonical, canonical))
        {
            record = visible;
            return true;
        }

        record = null!;
        return false;
    }

    public bool RemoveCurrent(
        uint serverGuid,
        out LiveEntityRecord? record)
    {
        if (!_directory.TryGetActive(
                serverGuid,
                out RuntimeEntityRecord canonical)
            || !TryGet(canonical, out LiveEntityRecord sidecar)
            || !RemoveActive(sidecar))
        {
            record = null;
            return false;
        }

        record = sidecar;
        return true;
    }

    public bool RemoveCurrent(uint serverGuid) =>
        RemoveCurrent(serverGuid, out _);

    public bool RemoveActive(LiveEntityRecord record)
    {
        if (record.ProjectionKey is not { } key)
            return false;

        _visible.Remove(key);
        bool removed = _materialized.Remove(
                key,
                out LiveEntityRecord? materialized)
            && ReferenceEquals(materialized, record);
        if (!removed)
            return false;
        if (!_materializedInRegistrationOrder.Remove(record))
        {
            throw new InvalidOperationException(
                "The exact App sidecar was absent from materialization order.");
        }
        return true;
    }

    public void RetainTeardown(LiveEntityRecord record)
    {
        if (record.ProjectionKey is { } key)
            AddExact(_teardown, key, record);
        else
            AddExact(_unmaterializedTeardown, record.Canonical, record);
    }

    public bool TryGetTeardown(
        RuntimeEntityRecord canonical,
        out LiveEntityRecord record)
    {
        if (canonical.Key is { } key
            && _teardown.TryGetValue(key, out record!))
        {
            return true;
        }
        foreach (LiveEntityRecord retained in _teardown.Values)
        {
            if (ReferenceEquals(retained.Canonical, canonical))
            {
                record = retained;
                return true;
            }
        }
        return _unmaterializedTeardown.TryGetValue(canonical, out record!);
    }

    public void ReleaseTeardown(LiveEntityRecord record)
    {
        if (record.ProjectionKey is { } key)
        {
            RemoveExact(_teardown, key, record);
            record.ProjectionKey = null;
            return;
        }

        RemoveExact(_unmaterializedTeardown, record.Canonical, record);
    }

    public bool IsRetainedTeardown(LiveEntityRecord record) =>
        TryGetTeardown(record.Canonical, out LiveEntityRecord retained)
        && ReferenceEquals(retained, record);

    public void ClearConverged()
    {
        if (_materializedInRegistrationOrder.Count != 0 || TeardownCount != 0)
        {
            throw new InvalidOperationException(
                "Projection storage cannot clear before active and teardown ownership converges.");
        }

        _materialized.Clear();
        _visible.Clear();
        _teardown.Clear();
        _unmaterializedTeardown.Clear();
    }

    private static RuntimeEntityKey RequireProjectionKey(LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Runtime entity 0x{record.ServerGuid:X8}/{record.Generation} has no App projection key.");

    private static void AddExact<TKey>(
        Dictionary<TKey, LiveEntityRecord> destination,
        TKey key,
        LiveEntityRecord record)
        where TKey : notnull
    {
        if (destination.TryGetValue(key, out LiveEntityRecord? retained)
            && !ReferenceEquals(retained, record))
        {
            throw new InvalidOperationException(
                $"App teardown sidecar collision for 0x{record.ServerGuid:X8}/{record.Generation}.");
        }
        destination[key] = record;
    }

    private static void RemoveExact<TKey>(
        Dictionary<TKey, LiveEntityRecord> source,
        TKey key,
        LiveEntityRecord record)
        where TKey : notnull
    {
        if (source.TryGetValue(key, out LiveEntityRecord? retained)
            && ReferenceEquals(retained, record))
        {
            source.Remove(key);
        }
    }

    private sealed class TeardownRecordCollection :
        IReadOnlyCollection<LiveEntityRecord>
    {
        private readonly Dictionary<RuntimeEntityKey, LiveEntityRecord> _keyed;
        private readonly Dictionary<RuntimeEntityRecord, LiveEntityRecord> _unmaterialized;

        public TeardownRecordCollection(
            Dictionary<RuntimeEntityKey, LiveEntityRecord> keyed,
            Dictionary<RuntimeEntityRecord, LiveEntityRecord> unmaterialized)
        {
            _keyed = keyed;
            _unmaterialized = unmaterialized;
        }

        public int Count => _keyed.Count + _unmaterialized.Count;

        public IEnumerator<LiveEntityRecord> GetEnumerator()
        {
            foreach (LiveEntityRecord record in _keyed.Values)
                yield return record;
            foreach (LiveEntityRecord record in _unmaterialized.Values)
                yield return record;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
