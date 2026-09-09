using AcDream.App.Input;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal interface ILiveEntityPruneSink
{
    bool Prune(LiveEntityPruneCandidate candidate);
}

/// <summary>
/// Owns authoritative and expiry-driven live-object deletion through one
/// generation-safe runtime transaction.
/// </summary>
internal sealed class LiveEntityDeletionController : ILiveEntityPruneSink
{
    private readonly LiveEntityRuntime _runtime;
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly ILiveEntityTeardownCoordinator _teardown;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly DormantLiveEntityStore _dormant;

    public LiveEntityDeletionController(
        LiveEntityRuntime runtime,
        RuntimeEntityObjectLifetime entityObjects,
        ILiveEntityTeardownCoordinator teardown,
        ILocalPlayerIdentitySource identity,
        DormantLiveEntityStore? dormant = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _teardown = teardown ?? throw new ArgumentNullException(nameof(teardown));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _dormant = dormant ?? new DormantLiveEntityStore();
    }

    public bool Delete(DeleteObject.Parsed delete)
    {
        if (delete.Guid == _identity.ServerGuid)
            return false;

        bool hasActiveRecord = _runtime.TryGetRecord(delete.Guid, out _);
        if (!hasActiveRecord)
            _teardown.ForgetUnknownOwner(delete.Guid);

        bool removed = _runtime.UnregisterLiveEntity(
            delete,
            isLocalPlayer: false,
            removeRetainedObject: true);
        bool removedDormant = _dormant.RemoveExact(delete);
        if (!removed && removedDormant)
            _entityObjects.ApplyAcceptedDormantDelete(delete);
        if (removed)
        {
            _dormant.RemoveExact(delete);
        }
        return removed || removedDormant;
    }

    public bool DeleteClientGhost(uint serverGuid)
    {
        if (serverGuid == 0u
            || serverGuid == _identity.ServerGuid
            || !_runtime.TryGetRecord(serverGuid, out LiveEntityRecord record))
        {
            return false;
        }
        return Delete(new DeleteObject.Parsed(serverGuid, record.Generation));
    }

    public bool Prune(LiveEntityPruneCandidate candidate)
    {
        if (!_runtime.TryGetRecord(
                candidate.Key,
                out LiveEntityRecord record)
            || record.ServerGuid != candidate.ServerGuid)
        {
            return false;
        }

        WorldSession.EntitySpawn snapshot = record.Snapshot;
        bool removed = _runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(
                candidate.ServerGuid,
                candidate.Generation),
            isLocalPlayer: false);
        if (!removed)
            return false;

        _dormant.Retain(snapshot);
        return true;
    }
}
