using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

/// <summary>
/// The single update-thread authority for live server GUIDs, incarnations,
/// accepted wire snapshots, teardown tombstones, and runtime-local identity.
/// It owns no graphical state and can run without loading App or a backend.
/// </summary>
public sealed class RuntimeEntityDirectory
{
    public const uint FirstLocalEntityId = 1_000_000u;
    public const uint LastLocalEntityId = 0x3FFF_FFFFu;

    private readonly InboundPhysicsStateController _inbound = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<uint, RuntimeEntityRecord> _activeByGuid = new();
    private readonly Dictionary<(uint Guid, ushort Incarnation), RuntimeEntityRecord>
        _teardownByIncarnation = new();
    private readonly Dictionary<uint, RuntimeEntityRecord> _byLocalId = new();
    private readonly Dictionary<uint, ulong> _lifetimeMutationByGuid = new();
    private uint _nextLocalEntityId;

    public RuntimeEntityDirectory(uint firstLocalEntityId = FirstLocalEntityId)
    {
        if (firstLocalEntityId is < FirstLocalEntityId or > LastLocalEntityId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstLocalEntityId),
                $"Live entity ids must stay in 0x{FirstLocalEntityId:X8}..0x{LastLocalEntityId:X8}.");
        }

        _nextLocalEntityId = firstLocalEntityId;
    }

    public int Count
    {
        get
        {
            lock (_activeGate)
                return _activeByGuid.Count;
        }
    }
    public int PendingTeardownCount => _teardownByIncarnation.Count;
    public int ClaimedLocalIdCount
    {
        get
        {
            lock (_activeGate)
                return _byLocalId.Count;
        }
    }
    public ulong SessionLifetimeVersion { get; private set; }
    public IReadOnlyCollection<RuntimeEntityRecord> ActiveRecords => _activeByGuid.Values;
    public IReadOnlyCollection<RuntimeEntityRecord> TeardownRecords =>
        _teardownByIncarnation.Values;
    public IReadOnlyDictionary<uint, WorldSession.EntitySpawn> Snapshots => _inbound.Snapshots;
    public ParentAttachmentState ParentAttachments { get; } = new();

    public InboundCreateResult AcceptCreate(WorldSession.EntitySpawn incoming) =>
        _inbound.AcceptCreate(incoming);

    internal InboundCreateResult AcceptCreateDeferredSameGeneration(
        WorldSession.EntitySpawn incoming) =>
        _inbound.AcceptCreateDeferredSameGeneration(incoming);

    public CreateObjectTimestampDisposition PreviewCreateDisposition(
        WorldSession.EntitySpawn incoming) =>
        _inbound.PreviewCreateDisposition(incoming);

    public bool TryDelete(
        AcDream.Core.Net.Messages.DeleteObject.Parsed delete,
        bool isLocalPlayer) =>
        _inbound.TryDelete(delete, isLocalPlayer);

    public bool TryGetSnapshot(uint guid, out WorldSession.EntitySpawn spawn) =>
        _inbound.TryGetSnapshot(guid, out spawn);

    internal bool TryGetAcceptedTimestamps(
        uint guid,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryGetAcceptedTimestamps(guid, out timestamps);

    public bool TryGetActive(uint guid, out RuntimeEntityRecord record) =>
        _activeByGuid.TryGetValue(guid, out record!);

    public bool IsCurrent(RuntimeEntityRecord record) =>
        _activeByGuid.TryGetValue(record.ServerGuid, out RuntimeEntityRecord? current)
        && ReferenceEquals(current, record);

    public bool TryGetByLocalId(uint localEntityId, out RuntimeEntityRecord record) =>
        _byLocalId.TryGetValue(localEntityId, out record!);

    public bool TryGetTeardown(
        uint guid,
        ushort incarnation,
        out RuntimeEntityRecord record) =>
        _teardownByIncarnation.TryGetValue((guid, incarnation), out record!);

    public RuntimeEntityRecord AddActive(WorldSession.EntitySpawn snapshot)
    {
        lock (_activeGate)
        {
            if (_activeByGuid.ContainsKey(snapshot.Guid))
            {
                throw new InvalidOperationException(
                    $"Live entity 0x{snapshot.Guid:X8} already has an active incarnation.");
            }

            var record = new RuntimeEntityRecord(snapshot);
            _activeByGuid.Add(snapshot.Guid, record);
            try
            {
                ClaimLocalId(record);
                return record;
            }
            catch
            {
                _activeByGuid.Remove(snapshot.Guid);
                throw;
            }
        }
    }

    public bool RemoveActive(uint guid, out RuntimeEntityRecord? record)
    {
        lock (_activeGate)
            return _activeByGuid.Remove(guid, out record);
    }

    public bool RemoveActive(RuntimeEntityRecord expected)
    {
        lock (_activeGate)
        {
            if (!_activeByGuid.TryGetValue(
                    expected.ServerGuid,
                    out RuntimeEntityRecord? current)
                || !ReferenceEquals(current, expected))
            {
                return false;
            }
            return _activeByGuid.Remove(expected.ServerGuid);
        }
    }

    public void RetainTeardown(RuntimeEntityRecord record)
    {
        var key = (record.ServerGuid, record.Incarnation);
        if (_teardownByIncarnation.TryGetValue(
                key,
                out RuntimeEntityRecord? retained)
            && !ReferenceEquals(retained, record))
        {
            throw new InvalidOperationException(
                $"Live entity teardown tombstone collision for 0x{record.ServerGuid:X8} generation {record.Incarnation}.");
        }

        _teardownByIncarnation[key] = record;
    }

    public void ReleaseTeardown(RuntimeEntityRecord record)
    {
        var key = (record.ServerGuid, record.Incarnation);
        if (_teardownByIncarnation.TryGetValue(
                key,
                out RuntimeEntityRecord? retained)
            && ReferenceEquals(retained, record))
        {
            _teardownByIncarnation.Remove(key);
        }
    }

    public bool HasPendingTeardown(uint serverGuid)
    {
        foreach ((uint Guid, ushort Incarnation) key in _teardownByIncarnation.Keys)
        {
            if (key.Guid == serverGuid)
                return true;
        }

        return false;
    }

    public uint ClaimLocalId(RuntimeEntityRecord record)
    {
        lock (_activeGate)
        {
            if (!IsKnown(record))
            {
                throw new InvalidOperationException(
                    "A local id can only be claimed for an active or retained incarnation.");
            }

            if (record.LocalEntityId is { } existing)
                return existing;

            uint start = _nextLocalEntityId;
            do
            {
                uint candidate = _nextLocalEntityId;
                _nextLocalEntityId = candidate == LastLocalEntityId
                    ? FirstLocalEntityId
                    : candidate + 1u;
                if (_byLocalId.ContainsKey(candidate))
                    continue;

                _byLocalId.Add(candidate, record);
                record.LocalEntityId = candidate;
                return candidate;
            }
            while (_nextLocalEntityId != start);

            throw new InvalidOperationException("The live entity id namespace is exhausted.");
        }
    }

    public bool ReleaseLocalId(RuntimeEntityRecord record)
    {
        lock (_activeGate)
        {
            if (record.LocalEntityId is not { } localId)
                return false;
            if (_byLocalId.TryGetValue(localId, out RuntimeEntityRecord? retained)
                && ReferenceEquals(retained, record))
            {
                _byLocalId.Remove(localId);
            }

            record.LocalEntityId = null;
            return true;
        }
    }

    public ulong AdvanceLifetimeMutation(uint serverGuid)
    {
        ulong next = _lifetimeMutationByGuid.GetValueOrDefault(serverGuid) + 1UL;
        _lifetimeMutationByGuid[serverGuid] = next;
        return next;
    }

    public ulong CurrentLifetimeMutation(uint serverGuid) =>
        _lifetimeMutationByGuid.GetValueOrDefault(serverGuid);

    public void BeginSessionClear()
    {
        SessionLifetimeVersion++;
        _lifetimeMutationByGuid.Clear();
        ParentAttachments.Clear();
        _inbound.Clear();
    }

    public bool CompleteSessionClearIfConverged()
    {
        lock (_activeGate)
        {
            if (_activeByGuid.Count != 0 || _teardownByIncarnation.Count != 0)
                return false;

            _byLocalId.Clear();
        }
        ParentAttachments.Clear();
        _inbound.Clear();
        return true;
    }

    internal ActiveReadLease AcquireActiveRead() => new(_activeGate);

    internal readonly struct ActiveReadLease : IDisposable
    {
        private readonly object _gate;

        internal ActiveReadLease(object gate)
        {
            _gate = gate;
            Monitor.Enter(gate);
        }

        public void Dispose() => Monitor.Exit(_gate);
    }

    public void RefreshSnapshot(
        RuntimeEntityRecord record,
        WorldSession.EntitySpawn accepted,
        bool refreshPosition = false)
    {
        EnsureKnown(record);
        uint previousCell = record.FullCellId;
        record.Snapshot = accepted;
        record.RefreshDerivedState(refreshPosition);
        if (record.FullCellId != previousCell)
        {
            PropagateFullCellToChildren(
                record,
                record.FullCellId,
                record.CanonicalLandblockId);
        }
    }

    public void AdvanceCreateAuthority(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceCreateAuthority();
    }

    public void AdvancePositionAuthority(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvancePositionAuthority();
    }

    public void AdvanceVectorAuthority(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceVectorAuthority();
    }

    public void AdvanceMovementAuthority(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceMovementAuthority();
    }

    public void AdvanceMovementCommit(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceMovementCommit();
    }

    public void AdvancePlacementCommit(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvancePlacementCommit();
    }

    public void AdvanceParentCommit(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceParentCommit();
    }

    public void AdvanceObjDescAuthority(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.AdvanceObjDescAuthority();
    }

    public RetailPhysicsStateTransition ApplyRawPhysicsState(
        RuntimeEntityRecord record,
        uint rawState)
    {
        EnsureKnown(record);
        return record.ApplyRawPhysicsState(rawState);
    }

    public bool TryDequeueStateTransition(
        RuntimeEntityRecord record,
        out RetailPhysicsStateTransition transition)
    {
        EnsureKnown(record);
        return record.TryDequeueStateTransition(out transition);
    }

    public void SetChildNoDraw(RuntimeEntityRecord record, bool noDraw)
    {
        EnsureKnown(record);
        record.SetChildNoDraw(noDraw);
    }

    public void SuspendObjectClock(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.SuspendObjectClock();
    }

    public void ResumeObjectClock(RuntimeEntityRecord record)
    {
        EnsureKnown(record);
        record.ResumeObjectClock();
    }

    public void ResetObjectClockForEnterWorld(RuntimeEntityRecord record, bool isStatic)
    {
        EnsureKnown(record);
        record.ResetObjectClockForEnterWorld(isStatic);
    }

    public void SetFullCell(
        RuntimeEntityRecord record,
        uint fullCellId,
        uint canonicalLandblockId)
    {
        EnsureKnown(record);
        record.SetFullCell(fullCellId, canonicalLandblockId);
        PropagateFullCellToChildren(record, fullCellId, canonicalLandblockId);
    }

    private readonly Stack<RuntimeEntityRecord> _propagationWorklist = new();

    private void PropagateFullCellToChildren(
        RuntimeEntityRecord root,
        uint fullCellId,
        uint canonicalLandblockId)
    {
        _propagationWorklist.Clear();
        _propagationWorklist.Push(root);
        while (_propagationWorklist.Count > 0)
        {
            RuntimeEntityRecord current = _propagationWorklist.Pop();
            IReadOnlyList<uint> children = ParentAttachments.ChildrenAttachedToParent(
                current.ServerGuid,
                current.Incarnation);
            for (int i = 0; i < children.Count; i++)
            {
                if (!TryGetActive(children[i], out RuntimeEntityRecord child)
                    || (child.FullCellId == fullCellId
                        && child.CanonicalLandblockId == canonicalLandblockId))
                {
                    continue;
                }
                if (PhysicsDiagnostics.ProbeChildCellEnabled)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"[child-cell] parent=0x{current.ServerGuid:X8} child=0x{child.ServerGuid:X8} old=0x{child.FullCellId:X8} new=0x{fullCellId:X8} cause={(fullCellId == 0u ? "withdraw" : "propagate")}"));
                }
                child.SetFullCell(fullCellId, canonicalLandblockId);
                _propagationWorklist.Push(child);
            }
        }
    }

    public void SetFinalPhysicsState(
        RuntimeEntityRecord record,
        PhysicsStateFlags state)
    {
        EnsureKnown(record);
        record.SetFinalPhysicsState(state);
    }

    internal bool StopMissileAfterCollision(
        RuntimeEntityRecord record,
        bool requireCurrentMissile)
    {
        EnsureKnown(record);
        return record.StopMissileAfterCollision(requireCurrentMissile);
    }

    public void SetHasPartArray(RuntimeEntityRecord record, bool value)
    {
        EnsureKnown(record);
        record.HasPartArray = value;
    }

    public void SetPhysicsBody(
        RuntimeEntityRecord record,
        AcDream.Core.Physics.PhysicsBody? body)
    {
        EnsureKnown(record);
        record.SetPhysicsBody(body);
    }

    public void SetPhysicsBodyAcquisitionInProgress(
        RuntimeEntityRecord record,
        bool value)
    {
        EnsureKnown(record);
        record.PhysicsBodyAcquisitionInProgress = value;
    }

    public void SetRemoteMotion(
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion? remote)
    {
        EnsureKnown(record);
        record.RemoteMotion = remote;
    }

    public void SetRemoteMotionBindingInProgress(
        RuntimeEntityRecord record,
        bool value)
    {
        EnsureKnown(record);
        record.RemoteMotionBindingInProgress = value;
    }

    public void SetProjectile(
        RuntimeEntityRecord record,
        IRuntimeProjectile? projectile)
    {
        EnsureKnown(record);
        record.Projectile = projectile;
    }

    public void SetProjectileBindingInProgress(
        RuntimeEntityRecord record,
        bool value)
    {
        EnsureKnown(record);
        record.ProjectileBindingInProgress = value;
    }

    public void SetRequiresRemotePlacementRuntime(
        RuntimeEntityRecord record,
        bool value)
    {
        EnsureKnown(record);
        record.RequiresRemotePlacementRuntime = value;
    }

    public void SetPhysicsHost(
        RuntimeEntityRecord record,
        AcDream.Core.Physics.Motion.IPhysicsObjHost? host)
    {
        EnsureKnown(record);
        record.PhysicsHost = host;
    }

    public void SetDeleteAcceptedForTeardown(
        RuntimeEntityRecord record,
        bool value)
    {
        EnsureKnown(record);
        record.DeleteAcceptedForTeardown = value;
    }

    public bool TryApplyObjDesc(
        AcDream.Core.Net.Messages.ObjDescEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyObjDesc(update, out accepted);

    public bool TryApplyPickup(
        AcDream.Core.Net.Messages.PickupEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyPickup(update, out accepted);

    public bool TryApplyCreateParent(
        CreateParentUpdate update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyCreateParent(update, out accepted);

    public bool TryApplyParent(
        AcDream.Core.Net.Messages.ParentEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyParent(update, out accepted);

    public bool TryCommitParent(
        uint childGuid,
        uint parentGuid,
        uint parentLocation,
        uint placementId,
        ushort positionSequence,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryCommitParent(
            childGuid,
            parentGuid,
            parentLocation,
            placementId,
            positionSequence,
            out accepted);

    public bool TryApplyMotion(
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryApplyMotion(update, retainPayload, out accepted, out timestamps);

    public bool TryApplyVector(
        AcDream.Core.Net.Messages.VectorUpdate.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyVector(update, out accepted);

    public bool TryApplyState(
        AcDream.Core.Net.Messages.SetState.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.TryApplyState(update, out accepted);

    public bool TryApplyPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        out PositionTimestampDisposition disposition,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryApplyPosition(
            update,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            out disposition,
            out accepted,
            out timestamps);

    internal bool TryAcceptDeferredPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        out PositionTimestampDisposition disposition,
        out AcceptedPhysicsTimestamps timestamps,
        out bool hasTimestampMutation) =>
        _inbound.TryAcceptDeferredPosition(
            update,
            isLocalPlayer,
            out disposition,
            out timestamps,
            out hasTimestampMutation);

    internal bool TryAcceptDeferredObjDesc(
        ObjDescEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredObjDesc(update, out timestamps);

    internal bool TryAcceptDeferredPickup(
        PickupEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredPickup(update, out timestamps);

    internal bool TryAcceptDeferredCreateParent(
        CreateParentUpdate update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredCreateParent(update, out timestamps);

    internal bool TryAcceptDeferredParent(
        ParentEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredParent(update, out timestamps);

    internal bool TryAcceptDeferredMotion(
        WorldSession.EntityMotionUpdate update,
        out AcceptedPhysicsTimestamps timestamps,
        out bool hasTimestampMutation) =>
        _inbound.TryAcceptDeferredMotion(
            update,
            out timestamps,
            out hasTimestampMutation);

    internal bool TryAcceptDeferredState(
        SetState.Parsed update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredState(update, out timestamps);

    internal bool TryAcceptDeferredVector(
        VectorUpdate.Parsed update,
        out AcceptedPhysicsTimestamps timestamps) =>
        _inbound.TryAcceptDeferredVector(update, out timestamps);

    public bool IsFreshTeleportStart(uint guid, ushort teleportSequence) =>
        _inbound.IsFreshTeleportStart(guid, teleportSequence);

    internal bool ApplyAcceptedObjDescSnapshot(
        uint guid,
        ObjDescEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedObjDescSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedPickupSnapshot(
        uint guid,
        PickupEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedPickupSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedCreateParentSnapshot(
        uint guid,
        CreateParentUpdate update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedCreateParentSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedParentSnapshot(
        uint guid,
        ParentEvent.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedParentSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedMotionSnapshot(
        uint guid,
        ushort movementSequence,
        ushort acceptedServerControlledMove,
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedMotionSnapshot(
            guid,
            movementSequence,
            acceptedServerControlledMove,
            update,
            retainPayload,
            out accepted);

    internal bool ApplyAcceptedStateSnapshot(
        uint guid,
        SetState.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedStateSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedVectorSnapshot(
        uint guid,
        VectorUpdate.Parsed update,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedVectorSnapshot(guid, update, out accepted);

    internal bool ApplyAcceptedPositionSnapshot(
        uint guid,
        WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        AcceptedPhysicsTimestamps timestamps,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        bool installPlacementFrame,
        bool clearParent,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedPositionSnapshot(
            guid,
            update,
            disposition,
            timestamps,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            installPlacementFrame,
            clearParent,
            out accepted);

    internal bool ApplyAcceptedPositionExecutionRejectedSnapshot(
        uint guid,
        ushort acceptedPositionSequence,
        AcceptedPhysicsTimestamps timestamps,
        out WorldSession.EntitySpawn accepted) =>
        _inbound.ApplyAcceptedPositionExecutionRejectedSnapshot(
            guid,
            acceptedPositionSequence,
            timestamps,
            out accepted);

    internal bool ApplyAcceptedWeenieDescriptionSnapshot(
        uint guid,
        WorldSession.EntitySpawn incoming,
        out WorldSession.EntitySpawn merged) =>
        _inbound.ApplyAcceptedWeenieDescriptionSnapshot(guid, incoming, out merged);

    internal bool TryRefreshObjectDescriptionFlags(
        uint guid,
        uint bitfield,
        out WorldSession.EntitySpawn merged) =>
        _inbound.TryRefreshObjectDescriptionFlags(guid, bitfield, out merged);

    private bool IsKnown(RuntimeEntityRecord record)
    {
        if (IsCurrent(record))
            return true;
        return _teardownByIncarnation.TryGetValue(
                (record.ServerGuid, record.Incarnation),
                out RuntimeEntityRecord? retained)
            && ReferenceEquals(retained, record);
    }

    private void EnsureKnown(RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsKnown(record))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} is no longer owned by the directory.");
        }
    }
}
