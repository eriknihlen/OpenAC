using AcDream.App.Input;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Rendering.Scene;

internal interface ILiveRenderProjectionSink : IRenderProjectionSyncPhase
{
    bool OnEntityReady(LiveEntityReadyCandidate candidate);
    void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible);
    void OnProjectionPoseReady(uint serverGuid);
    void OnProjectionRemoved(LiveEntityRecord record);
    void OnResourceUnregister(WorldEntity entity);
}

internal sealed class LiveRenderProjectionJournal : ILiveRenderProjectionSink
{
    private const byte LiveProjectionDomain = 3;

    private readonly LiveEntityRuntime _runtime;
    private readonly RenderProjectionJournal _journal;
    private readonly IRenderTraversalOrderSource _traversalOrder;
    private readonly ILocalPlayerIdentitySource? _localPlayer;
    private readonly Dictionary<RuntimeEntityKey, TrackedProjection> _byKey = [];
    private readonly List<LiveEntityRecord> _activeRootScratch = [];
    private readonly List<TrackedProjection> _activeScratch = [];

    public LiveRenderProjectionJournal(
        LiveEntityRuntime runtime,
        RenderProjectionJournal journal,
        IRenderTraversalOrderSource traversalOrder,
        ILocalPlayerIdentitySource? localPlayer = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _traversalOrder = traversalOrder
            ?? throw new ArgumentNullException(nameof(traversalOrder));
        _localPlayer = localPlayer;
    }

    public int ProjectionCount => _byKey.Count;

    public bool OnEntityReady(LiveEntityReadyCandidate candidate)
    {
        if (!candidate.IsCurrent(_runtime)
            || candidate.WorldEntity is not { } entity
            || !candidate.Record.ResourcesRegistered)
        {
            return false;
        }

        Upsert(candidate.Record, entity, candidate.Record.IsSpatiallyVisible);
        return candidate.IsCurrent(_runtime);
    }

    public void OnProjectionVisibilityChanged(
        LiveEntityRecord record,
        bool visible)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_runtime.IsCurrentRecord(record)
            || record.WorldEntity is not { } entity
            || record.ProjectionKey is not { } key
            || !_byKey.ContainsKey(key))
        {
            return;
        }

        Upsert(record, entity, visible);
    }

    public void OnProjectionPoseReady(uint serverGuid)
    {
        if (!_runtime.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity
            || record.ProjectionKind is not LiveEntityProjectionKind.Attached)
        {
            return;
        }

        Upsert(record, entity, record.IsSpatiallyVisible);
    }

    public void OnProjectionRemoved(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProjectionKey is { } key
            && _byKey.TryGetValue(
                key,
                out TrackedProjection? tracked)
            && tracked.Projection.ProjectionClass is
                RenderProjectionClass.EquippedChild)
        {
            if (_runtime.IsCurrentRecord(tracked.Record)
                && ReferenceEquals(
                    tracked.Record.WorldEntity,
                    tracked.Entity))
            {
                Upsert(tracked.Record, tracked.Entity, spatiallyVisible: false);
            }
            else
            {
                Remove(tracked);
            }
        }
    }

    public void OnResourceUnregister(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        TrackedProjection? tracked = null;
        foreach (TrackedProjection candidate in _byKey.Values)
        {
            if (ReferenceEquals(candidate.Entity, entity))
            {
                tracked = candidate;
                break;
            }
        }
        if (tracked is not null)
            Remove(tracked);
    }

    public void SynchronizeActiveSources()
    {
        _runtime.CopySpatialRootObjectRecordsTo(_activeRootScratch);
        for (int i = 0; i < _activeRootScratch.Count; i++)
        {
            LiveEntityRecord record = _activeRootScratch[i];
            if (!record.ResourcesRegistered
                || record.WorldEntity is not { } entity
                || !_runtime.IsCurrentSpatialRootObject(record))
            {
                continue;
            }

            Upsert(record, entity, record.IsSpatiallyVisible);
        }

        _activeScratch.Clear();
        foreach (TrackedProjection tracked in _byKey.Values)
        {
            if (tracked.Record.IsSpatiallyProjected
                && tracked.Record.ProjectionKind is
                    LiveEntityProjectionKind.Attached)
                _activeScratch.Add(tracked);
        }

        for (int i = 0; i < _activeScratch.Count; i++)
        {
            TrackedProjection tracked = _activeScratch[i];
            if (!_runtime.IsCurrentRecord(tracked.Record)
                || !ReferenceEquals(tracked.Record.WorldEntity, tracked.Entity)
                || tracked.Record.ProjectionKey is not { } key
                || !_byKey.TryGetValue(
                    key,
                    out TrackedProjection? current)
                || !ReferenceEquals(current, tracked))
            {
                continue;
            }

            Upsert(
                tracked.Record,
                tracked.Entity,
                tracked.Record.IsSpatiallyVisible);
        }
    }

    public void ResetTracking()
    {
        _byKey.Clear();
        _activeRootScratch.Clear();
        _activeScratch.Clear();
    }

    internal bool TryGet(
        uint localEntityId,
        out RenderProjectionRecord record)
    {
        if (_runtime.TryGetRecordByLocalEntityId(
                localEntityId,
                out LiveEntityRecord owner)
            && owner.ProjectionKey is { } key
            && _byKey.TryGetValue(key, out TrackedProjection? tracked))
        {
            record = tracked.Projection;
            return true;
        }

        record = default;
        return false;
    }

    internal static RenderProjectionId ProjectionId(uint localEntityId) =>
        RenderProjectionId.FromRaw(
            ((ulong)LiveProjectionDomain << 56) | localEntityId);

    private void Upsert(
        LiveEntityRecord record,
        WorldEntity entity,
        bool spatiallyVisible)
    {
        RenderProjectionRecord projected = Project(
            record,
            entity,
            spatiallyVisible);
        RuntimeEntityKey key = RequireProjectionKey(record);
        if (!_byKey.TryGetValue(key, out TrackedProjection? tracked))
        {
            _journal.Register(in projected);
            _byKey.Add(
                key,
                new TrackedProjection(record, entity, projected));
            return;
        }

        if (!ReferenceEquals(tracked.Record, record)
            || !ReferenceEquals(tracked.Entity, entity)
            || tracked.Projection.OwnerIncarnation
                != projected.OwnerIncarnation
            || tracked.Projection.ProjectionClass
                != projected.ProjectionClass)
        {
            _journal.Register(in projected);
            _byKey[key] =
                new TrackedProjection(record, entity, projected);
            return;
        }

        projected = projected with
        {
            PreviousTransform = new PreviousRenderTransform(
                tracked.Projection.Transform.LocalToWorld),
        };
        _journal.AppendDifference(tracked.Projection, projected);
        tracked.Projection = projected;
    }

    private RenderProjectionRecord Project(
        LiveEntityRecord record,
        WorldEntity entity,
        bool spatiallyVisible)
    {
        uint fullCellId = record.FullCellId != 0
            ? record.FullCellId
            : entity.ParentCellId ?? 0;
        uint ownerLandblockId = record.CanonicalLandblockId != 0
            ? record.CanonicalLandblockId
            : fullCellId == 0
                ? 0
                : Canonicalize(fullCellId);
        RenderProjectionRecord projected =
            RenderProjectionRecordFactory.ProjectEntity(
            ProjectionId(entity.Id),
            record.ProjectionKind is LiveEntityProjectionKind.Attached
                ? RenderProjectionClass.EquippedChild
                : RenderProjectionClass.LiveDynamicRoot,
            RenderOwnerIncarnation.FromRaw(
                ((ulong)record.Generation << 32) | entity.Id),
            ownerLandblockId,
            fullCellId,
            entity,
            spatiallyVisible,
            record.ProjectionKind is LiveEntityProjectionKind.Attached
                ? RenderCasterIdentityKind.EquippedChild
                : RenderCasterIdentityClassifier.Classify(
                    record.Snapshot.Guid,
                    record.Snapshot.ItemType,
                    record.Snapshot.ObjectDescriptionFlags,
                    _localPlayer?.ServerGuid ?? 0u));
        if (_traversalOrder.TryGetTraversalSortKey(
                entity,
                out RenderSortKey sortKey))
        {
            return projected with { SortKey = sortKey };
        }

        // Spatial withdrawal is not logical destruction. Retain the last
        // resident order while the projection is inactive so a portal/rebucket
        // edge does not manufacture an unrelated ordering mutation.
        RuntimeEntityKey key = RequireProjectionKey(record);
        return _byKey.TryGetValue(
            key,
            out TrackedProjection? retained)
            ? projected with { SortKey = retained.Projection.SortKey }
            : projected;
    }

    private void Remove(TrackedProjection tracked)
    {
        RuntimeEntityKey key = RequireProjectionKey(tracked.Record);
        if (!_byKey.Remove(key))
            return;
        _journal.Unregister(
            tracked.Projection.Id,
            tracked.Projection.OwnerIncarnation);
    }

    private static uint Canonicalize(uint cellOrLandblockId) =>
        (cellOrLandblockId & 0xFFFF0000u) | 0xFFFFu;

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} " +
            "has no exact projection key.");

    private sealed class TrackedProjection(
        LiveEntityRecord record,
        WorldEntity entity,
        RenderProjectionRecord projection)
    {
        public LiveEntityRecord Record { get; } = record;
        public WorldEntity Entity { get; } = entity;
        public RenderProjectionRecord Projection { get; set; } = projection;
    }
}

internal static class RenderCasterIdentityClassifier
{
    private const uint PlayerDescriptionFlag = 0x8u;
    private const uint PlayerGuidPrefix = 0x50000000u;

    internal static RenderCasterIdentityKind Classify(
        uint serverGuid,
        uint? itemType,
        uint? objectDescriptionFlags,
        uint localPlayerGuid)
    {
        if (localPlayerGuid != 0 && serverGuid == localPlayerGuid)
            return RenderCasterIdentityKind.LocalPlayer;
        if ((objectDescriptionFlags.GetValueOrDefault()
                & PlayerDescriptionFlag) != 0
            || (serverGuid & 0xFF000000u) == PlayerGuidPrefix)
        {
            return RenderCasterIdentityKind.RemotePlayer;
        }
        if ((itemType.GetValueOrDefault() & (uint)ItemType.Creature) != 0)
            return RenderCasterIdentityKind.NonPlayerCreature;
        return RenderCasterIdentityKind.OtherLiveDynamic;
    }
}

internal sealed class LiveRenderProjectionResourceLifecycle(
    ILiveRenderProjectionSink sink) : ILiveEntityResourceLifecycle
{
    private readonly ILiveRenderProjectionSink _sink =
        sink ?? throw new ArgumentNullException(nameof(sink));

    public void Register(WorldEntity entity)
    {
        // The exact EntityReady edge follows all resource registration and is
        // the first point at which the projection may enter the journal.
    }

    public void Unregister(WorldEntity entity) =>
        _sink.OnResourceUnregister(entity);
}
