using AcDream.App.Physics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

public sealed class LiveEntityPresentationController : IDisposable
{
    public const uint UnHideScriptType = 0x75u;
    public const uint HiddenScriptType = 0x76u;

    private readonly LiveEntityRuntime _liveEntities;
    private readonly ShadowObjectRegistry _shadows;
    private readonly Func<uint, uint, float, bool> _playTyped;
    private readonly Action<uint, bool> _setDirectChildrenNoDraw;
    private readonly Action<uint> _clearInvalidTarget;
    private readonly Func<(int X, int Y)> _liveCenter;
    private readonly Action<uint>? _onShadowRestored;
    private readonly LiveEntityPartArrayEnterWorldPort _partArrayEnterWorld;
    private readonly HashSet<RuntimeEntityKey> _readyOwners = [];
    private readonly HashSet<RuntimeEntityKey> _suspendedShadowOwners = [];
    private readonly HashSet<LiveEntityRecord> _drainingRecords = new();
    private bool _disposed;

    public LiveEntityPresentationController(
        LiveEntityRuntime liveEntities,
        ShadowObjectRegistry shadows,
        Func<uint, uint, float, bool> playTyped,
        LiveEntityPartArrayEnterWorldPort partArrayEnterWorld,
        Action<uint, bool>? setDirectChildrenNoDraw = null,
        Action<uint>? clearInvalidTarget = null,
        Func<(int X, int Y)>? liveCenter = null,
        Action<uint>? onShadowRestored = null)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _playTyped = playTyped ?? throw new ArgumentNullException(nameof(playTyped));
        _partArrayEnterWorld = partArrayEnterWorld
            ?? throw new ArgumentNullException(nameof(partArrayEnterWorld));
        _setDirectChildrenNoDraw = setDirectChildrenNoDraw ?? ((_, _) => { });
        _clearInvalidTarget = clearInvalidTarget ?? (_ => { });
        _liveCenter = liveCenter ?? (() => (0, 0));
        _onShadowRestored = onShadowRestored;
        _liveEntities.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
    }

    public bool OnLiveEntityReady(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is null
            || !record.ResourcesRegistered)
        {
            return false;
        }

        RuntimeEntityKey key = RequireProjectionKey(record);
        _readyOwners.Add(key);
        if (!ApplyPendingTransitions(record)
            || record.WorldEntity is not { } entity
            || !IsCurrent(record, entity))
        {
            return false;
        }

        SuspendOrdinaryShadowOutsideProjection(record, entity);
        return IsCurrent(record, entity);
    }

    /// <summary>Drains a newly accepted SetState when this incarnation is ready.</summary>
    public bool OnStateAccepted(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || !TryGetProjectionKey(record, out RuntimeEntityKey key)
            || !_readyOwners.Contains(key))
        {
            return false;
        }

        return ApplyPendingTransitions(record);
    }

    internal bool HasDeferredShadowRestore(uint serverGuid) =>
        TryGetCurrentProjectionKey(serverGuid, out RuntimeEntityKey key)
        && _suspendedShadowOwners.Contains(key);

    internal int ReadyOwnerCount => _readyOwners.Count;
    internal int DeferredShadowRestoreCount => _suspendedShadowOwners.Count;

    public void Forget(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (TryGetProjectionKey(record, out RuntimeEntityKey key))
        {
            _readyOwners.Remove(key);
            _suspendedShadowOwners.Remove(key);
        }
        _drainingRecords.Remove(record);
    }

    public void Clear()
    {
        _readyOwners.Clear();
        _suspendedShadowOwners.Clear();
        _drainingRecords.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _liveEntities.ProjectionVisibilityChanged -= OnProjectionVisibilityChanged;
        Clear();
    }

    private bool ApplyPendingTransitions(LiveEntityRecord record)
    {
        if (!_drainingRecords.Add(record))
            return IsCurrent(record, record.WorldEntity);

        try
        {
            while (record.TryDequeueStateTransition(out RetailPhysicsStateTransition transition))
            {
                if (record.WorldEntity is not { } entity
                    || !IsCurrent(record, entity))
                {
                    return false;
                }

                _shadows.UpdatePhysicsState(entity.Id, (uint)transition.FinalState);

                switch (transition.HiddenTransition)
                {
                    case RetailHiddenTransition.BecameHidden:
                        _playTyped(entity.Id, HiddenScriptType, 1f);
                        if (!IsCurrent(record, entity))
                            return false;
                        _setDirectChildrenNoDraw(record.ServerGuid, true);
                        if (!IsCurrent(record, entity))
                            return false;
                        _shadows.Suspend(entity.Id);
                        _suspendedShadowOwners.Add(RequireProjectionKey(record));
                        _partArrayEnterWorld.HandleEnterWorld(entity.Id);
                        if (!IsCurrent(record, entity))
                            return false;
                        _clearInvalidTarget(record.ServerGuid);
                        if (!IsCurrent(record, entity))
                            return false;
                        break;

                    case RetailHiddenTransition.BecameVisible:
                        _playTyped(entity.Id, UnHideScriptType, 1f);
                        if (!IsCurrent(record, entity))
                            return false;
                        _setDirectChildrenNoDraw(record.ServerGuid, false);
                        if (!IsCurrent(record, entity))
                            return false;
                        _partArrayEnterWorld.HandleEnterWorld(entity.Id);
                        if (!IsCurrent(record, entity))
                            return false;
                        bool restored = RestoreShadow(record, entity);
                        if (!IsCurrent(record, entity))
                            return false;
                        if (restored)
                            _suspendedShadowOwners.Remove(RequireProjectionKey(record));
                        break;
                }
            }

            return IsCurrent(record, record.WorldEntity);
        }
        finally
        {
            _drainingRecords.Remove(record);
        }
    }

    private bool RestoreShadow(LiveEntityRecord record, AcDream.Core.World.WorldEntity entity)
    {
        if (!record.IsSpatiallyProjected
            || !record.IsSpatiallyVisible
            || record.FullCellId == 0
            || _liveEntities.ParentAttachments.HasCommittedParent(record.ServerGuid))
        {
            return false;
        }

        (int centerX, int centerY) = _liveCenter();
        ShadowPositionSynchronizer.Sync(
            _shadows,
            entity.Id,
            entity.Position,
            entity.Rotation,
            record.FullCellId,
            centerX,
            centerY);
        _onShadowRestored?.Invoke(record.ServerGuid);
        return true;
    }

    private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
    {
        if (record.WorldEntity is not { } entity
            || !IsCurrent(record, entity))
        {
            return;
        }

        if (!visible)
        {
            SuspendOrdinaryShadowOutsideProjection(record, entity);
            return;
        }

        if ((record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0
            || !TryGetProjectionKey(record, out RuntimeEntityKey key)
            || !_readyOwners.Contains(key)
            || !_suspendedShadowOwners.Contains(key))
        {
            return;
        }

        bool restored = RestoreShadow(record, entity);
        if (!IsCurrent(record, entity))
            return;
        if (restored)
            _suspendedShadowOwners.Remove(key);
    }

    private void SuspendOrdinaryShadowOutsideProjection(
        LiveEntityRecord record,
        AcDream.Core.World.WorldEntity entity)
    {
        if (record.IsSpatiallyVisible
            || record.ProjectileRuntime is not null
            || !TryGetProjectionKey(record, out RuntimeEntityKey key)
            || !_readyOwners.Contains(key)
            || !_shadows.Suspend(entity.Id))
        {
            return;
        }

        _suspendedShadowOwners.Add(key);
    }

    private bool IsCurrent(
        LiveEntityRecord record,
        AcDream.Core.World.WorldEntity? entity) =>
        entity is not null
        && _liveEntities.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
        && ReferenceEquals(current, record)
        && ReferenceEquals(current.WorldEntity, entity);

    private bool TryGetCurrentProjectionKey(
        uint serverGuid,
        out RuntimeEntityKey key)
    {
        if (_liveEntities.TryGetRecord(
                serverGuid,
                out LiveEntityRecord record))
        {
            return TryGetProjectionKey(record, out key);
        }

        key = default;
        return false;
    }

    private static bool TryGetProjectionKey(
        LiveEntityRecord record,
        out RuntimeEntityKey key)
    {
        if (record.ProjectionKey is { } projectionKey)
        {
            key = projectionKey;
            return true;
        }

        key = default;
        return false;
    }

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} " +
            "has no exact projection key.");
}
