using System.Collections.Immutable;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal enum RuntimeCollisionReportKind
{
    ObjectCollision,
    ObjectCollisionEnd,
    EnvironmentCollision,
}

internal enum SetPositionCollisionBatchDispatchStatus : byte
{
    RejectedReceipt,
    Displaced,
    Completed,
}

internal readonly record struct SetPositionCollisionBatchDispatchResult(
    SetPositionCollisionBatchDispatchStatus Status,
    bool Reported);

internal readonly record struct RuntimeCollisionReport(
    ulong Sequence,
    RuntimeCollisionReportKind Kind,
    RuntimeEntityKey Recipient,
    uint RecipientServerGuid,
    RuntimeEntityKey? Other,
    uint? OtherServerGuid,
    bool RecipientWasInContact,
    bool OtherWasInContact);

internal interface IRuntimeCollisionReportObserver
{
    void OnCollisionReport(in RuntimeCollisionReport report);
}

internal readonly record struct RuntimeCollisionReportingOwnershipSnapshot(
    int OwnerCount,
    int TrackedObjectCount,
    int ReversePeerCount,
    int ObserverCount,
    int PendingReportCount,
    int LeavingOwnerCount,
    int AdmissionBlockedOwnerCount,
    int PendingSetPositionDispatchCount,
    bool IsDispatching,
    long DispatchFailureCount,
    bool IsDisposed)
{
    internal bool IsConverged =>
        IsDisposed
        && OwnerCount == 0
        && TrackedObjectCount == 0
        && ReversePeerCount == 0
        && ObserverCount == 0
        && PendingReportCount == 0
        && LeavingOwnerCount == 0
        && AdmissionBlockedOwnerCount == 0
        && PendingSetPositionDispatchCount == 0
        && !IsDispatching;
}

internal sealed class RuntimeCollisionReportingState : IDisposable
{
    private readonly RuntimeEntityDirectory _entities;
    private readonly ShadowObjectRegistry _shadows;
    private readonly Dictionary<RuntimeEntityKey, OwnerState> _owners = new();
    private readonly Dictionary<RuntimeEntityKey, List<RuntimeEntityKey>>
        _ownersByPeer = new();
    private readonly Queue<PendingReport> _pendingReports = new();
    private readonly HashSet<RuntimeEntityKey> _leaving = [];
    private readonly HashSet<RuntimeEntityKey> _admissionBlocked = [];
    private IRuntimeCollisionReportObserver[] _observers = [];
    private ulong _nextSequence;
    private ulong _dispatchEpoch = 1UL;
    private long _dispatchFailureCount;
    private ulong _mutationRevision;
    private ulong _nextPreparedBatchId;
    private ulong _lastInstalledBatchId;
    private readonly HashSet<ulong> _pendingSetPositionDispatches = [];
    private bool _dispatching;
    private bool _disposed;

    internal RuntimeCollisionReportingState(
        RuntimeEntityDirectory entities,
        ShadowObjectRegistry shadows)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
    }

    internal RuntimeCollisionReportingOwnershipSnapshot CaptureOwnership()
    {
        int tracked = 0;
        foreach ((_, OwnerState owner) in _owners)
            tracked += owner.Records.Count;
        return new RuntimeCollisionReportingOwnershipSnapshot(
            _owners.Count,
            tracked,
            _ownersByPeer.Count,
            _observers.Length,
            _pendingReports.Count,
            _leaving.Count,
            _admissionBlocked.Count,
            _pendingSetPositionDispatches.Count,
            _dispatching,
            _dispatchFailureCount,
            _disposed);
    }

    internal IDisposable Subscribe(IRuntimeCollisionReportObserver observer)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(observer);
        if (Array.IndexOf(_observers, observer) >= 0)
        {
            throw new InvalidOperationException(
                "A collision-report observer cannot be subscribed twice.");
        }

        var replacement = new IRuntimeCollisionReportObserver[
            _observers.Length + 1];
        Array.Copy(_observers, replacement, _observers.Length);
        replacement[^1] = observer;
        _observers = replacement;
        return new Subscription(this, observer);
    }

    internal enum StagedReportEligibility : byte
    {
        Environment,
        Object,
    }

    private sealed record FrozenCollisionSubject(
        uint LocalEntityId,
        bool IsStatic,
        RuntimeEntityRecord? Record,
        PhysicsBody? Body,
        RuntimeEntityKey Key);

    internal sealed record StagedReportAction(
        StagedReportEligibility Eligibility,
        RuntimeEntityRecord Recipient,
        PhysicsBody RecipientBody,
        RuntimeEntityKey RecipientKey,
        RuntimeEntityRecord? Other,
        PhysicsBody? OtherBody,
        RuntimeEntityKey? OtherKey,
        bool RecipientContact,
        bool ExactDormantRecipient,
        ulong RecipientPositionAuthorityVersion);

    internal sealed class PreparedSetPositionCollisionBatch
    {
        internal required ulong BatchId { get; init; }
        internal required ulong ExpectedMutationRevision { get; init; }
        internal required ulong InstalledMutationRevision { get; init; }
        internal required ulong SessionLifetimeVersion { get; init; }
        internal required RuntimeEntityRecord Owner { get; init; }
        internal required PhysicsBody OwnerBody { get; init; }
        internal required RuntimeEntityKey OwnerKey { get; init; }
        internal required ulong OwnerPositionAuthorityVersion { get; init; }
        internal required OwnerState? OwnerState { get; init; }
        internal required bool PreviousContact { get; init; }
        internal required bool FinalCollidedWithEnvironment { get; init; }
        internal required bool FinalGroundEdge { get; init; }
        internal required double PhysicsTime { get; init; }
        internal required StagedReportAction[] Actions { get; init; }
    }

    internal readonly record struct SetPositionCollisionBatchReceipt(
        ulong BatchId,
        RuntimeEntityRecord Owner,
        PhysicsBody OwnerBody,
        RuntimeEntityKey OwnerKey,
        ulong OwnerPositionAuthorityVersion,
        bool PreviousContact,
        bool FinalCollidedWithEnvironment,
        bool FinalGroundEdge,
        double PhysicsTime,
        StagedReportAction[] Actions)
    {
        internal bool IsValid => BatchId != 0UL;
    }

    internal bool TryPrepareSetPositionBatch(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        double physicsTime,
        bool previousContact,
        bool previousOnWalkable,
        bool finalOnWalkable,
        bool collidedWithEnvironment,
        ImmutableArray<uint> collidedObjectIds,
        out PreparedSetPositionCollisionBatch? prepared)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ownerBody);
        prepared = null;
        RuntimeEntityKey ownerKey = owner.Key ?? default;
        if (!double.IsFinite(physicsTime)
            || ownerKey == default
            || !IsKnownParticipant(owner, ownerBody, ownerKey)
            || _leaving.Contains(ownerKey)
            || _admissionBlocked.Contains(ownerKey))
        {
            return false;
        }

        ulong expectedMutation = _mutationRevision;
        ulong installedMutation = checked(expectedMutation + 1UL);
        ulong sessionLifetime = _entities.SessionLifetimeVersion;
        if (collidedObjectIds.IsDefault)
            collidedObjectIds = ImmutableArray<uint>.Empty;
        var subjects = new List<FrozenCollisionSubject>(
            collidedObjectIds.Length);
        for (int index = 0; index < collidedObjectIds.Length; index++)
        {
            uint localId = collidedObjectIds[index];
            if (localId == 0u || localId == ownerKey.LocalEntityId)
                continue;
            if (!_shadows.TryGetCollisionOwner(
                    localId,
                    out uint shadowState,
                    out bool isStatic))
            {
                continue;
            }
            if (isStatic)
            {
                subjects.Add(new FrozenCollisionSubject(
                    localId,
                    IsStatic: true,
                    Record: null,
                    Body: null,
                    Key: default));
                continue;
            }
            if (!_entities.TryGetByLocalId(localId, out RuntimeEntityRecord target)
                || target.PhysicsBody is not { } targetBody
                || !_entities.IsCurrent(target)
                || target.Key is not { } targetKey)
            {
                continue;
            }
            subjects.Add(new FrozenCollisionSubject(
                localId,
                IsStatic: false,
                target,
                targetBody,
                targetKey));
            _ = shadowState;
        }

        OwnerState staged = CloneOwnerState(TryGetOwner(ownerKey));
        var actions = new List<StagedReportAction>(subjects.Count);
        void StageEnvironment()
        {
            actions.Add(new StagedReportAction(
                StagedReportEligibility.Environment,
                owner,
                ownerBody,
                ownerKey,
                Other: null,
                OtherBody: null,
                OtherKey: null,
                RecipientContact: previousContact,
                ExactDormantRecipient: !ownerBody.InWorld,
                RecipientPositionAuthorityVersion:
                    owner.PositionAuthorityVersion));
        }
        for (int index = 0; index < subjects.Count; index++)
        {
            FrozenCollisionSubject subject = subjects[index];
            if (subject.IsStatic)
            {
                StageEnvironment();
                continue;
            }
            RuntimeEntityRecord target = subject.Record!;
            PhysicsBody targetBody = subject.Body!;
            RuntimeEntityKey targetKey = subject.Key;
            actions.Add(new StagedReportAction(
                StagedReportEligibility.Object,
                owner,
                ownerBody,
                ownerKey,
                target,
                targetBody,
                targetKey,
                RecipientContact: previousContact,
                ExactDormantRecipient: !ownerBody.InWorld,
                RecipientPositionAuthorityVersion:
                    owner.PositionAuthorityVersion));
        }

        _owners.EnsureCapacity(_owners.Count + 1);
        _pendingSetPositionDispatches.EnsureCapacity(
            _pendingSetPositionDispatches.Count + 1);
        prepared = new PreparedSetPositionCollisionBatch
        {
            BatchId = checked(++_nextPreparedBatchId),
            ExpectedMutationRevision = expectedMutation,
            InstalledMutationRevision = installedMutation,
            SessionLifetimeVersion = sessionLifetime,
            Owner = owner,
            OwnerBody = ownerBody,
            OwnerKey = ownerKey,
            OwnerPositionAuthorityVersion = owner.PositionAuthorityVersion,
            OwnerState = staged.Records.Count == 0
                && !staged.CollidingWithEnvironment
                    ? null
                    : staged,
            PreviousContact = previousContact,
            FinalCollidedWithEnvironment = collidedWithEnvironment,
            FinalGroundEdge = !previousOnWalkable && finalOnWalkable,
            PhysicsTime = physicsTime,
            Actions = actions.ToArray(),
        };
        _ = previousContact;
        return _mutationRevision == expectedMutation
            && _entities.SessionLifetimeVersion == sessionLifetime
            && IsKnownParticipant(owner, ownerBody, ownerKey);
    }

    internal bool TryInstallSetPositionBatch(
        PreparedSetPositionCollisionBatch prepared,
        out SetPositionCollisionBatchReceipt receipt)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(prepared);
        receipt = default;
        if (prepared.BatchId <= _lastInstalledBatchId
            || _mutationRevision != prepared.ExpectedMutationRevision
            || _entities.SessionLifetimeVersion
                != prepared.SessionLifetimeVersion
            || !IsKnownParticipant(
                prepared.Owner,
                prepared.OwnerBody,
                prepared.OwnerKey))
        {
            return false;
        }
        if (!IsKnownParticipant(
                prepared.Owner,
                prepared.OwnerBody,
                prepared.OwnerKey))
            return false;

        if (prepared.OwnerState is null)
            _owners.Remove(prepared.OwnerKey);
        else
        {
            prepared.OwnerState.SetPositionBatchId = prepared.BatchId;
            _owners[prepared.OwnerKey] = prepared.OwnerState;
        }
        _mutationRevision = prepared.InstalledMutationRevision;
        _lastInstalledBatchId = prepared.BatchId;
        _pendingSetPositionDispatches.Add(prepared.BatchId);
        receipt = new SetPositionCollisionBatchReceipt(
            prepared.BatchId,
            prepared.Owner,
            prepared.OwnerBody,
            prepared.OwnerKey,
            prepared.OwnerPositionAuthorityVersion,
            prepared.PreviousContact,
            prepared.FinalCollidedWithEnvironment,
            prepared.FinalGroundEdge,
            prepared.PhysicsTime,
            prepared.Actions);
        return true;
    }

    internal bool IsPreparedSetPositionBatchCurrent(
        PreparedSetPositionCollisionBatch prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return !_disposed
            && prepared.BatchId > _lastInstalledBatchId
            && _mutationRevision == prepared.ExpectedMutationRevision
            && _entities.SessionLifetimeVersion
                == prepared.SessionLifetimeVersion
            && IsKnownParticipant(
                prepared.Owner,
                prepared.OwnerBody,
                prepared.OwnerKey);
    }

    internal bool DispatchSetPositionBatch(
        in SetPositionCollisionBatchReceipt receipt) =>
        DispatchSetPositionBatchResult(receipt).Reported;

    internal SetPositionCollisionBatchDispatchResult
        DispatchSetPositionBatchResult(
        in SetPositionCollisionBatchReceipt receipt)
    {
        if (!receipt.IsValid
            || _disposed
            || receipt.BatchId > _lastInstalledBatchId
            || !_pendingSetPositionDispatches.Remove(receipt.BatchId))
        {
            return new(
                SetPositionCollisionBatchDispatchStatus.RejectedReceipt,
                Reported: false);
        }
        bool reported = false;
        for (int index = 0; index < receipt.Actions.Length; index++)
        {
            if (IsExactSetPositionBatchOwnerHidden(receipt))
            {
                return new(
                    SetPositionCollisionBatchDispatchStatus.Completed,
                    reported);
            }
            if (receipt.Owner.PositionAuthorityVersion
                    != receipt.OwnerPositionAuthorityVersion
                || _owners.TryGetValue(
                    receipt.OwnerKey, out OwnerState? currentOwner)
                    && currentOwner.SetPositionBatchId != receipt.BatchId)
            {
                return new(
                    SetPositionCollisionBatchDispatchStatus.Displaced,
                    reported);
            }
            StagedReportAction action = receipt.Actions[index];
            if (action.Eligibility is StagedReportEligibility.Environment)
            {
                reported |= DispatchEnvironmentAction(
                    action.Recipient,
                    action.RecipientBody,
                    action.RecipientKey,
                    action.RecipientContact,
                    receipt.BatchId);
                continue;
            }
            reported |= DispatchTrackingAction(
                action, receipt.PhysicsTime, receipt.BatchId);
        }

        if (IsExactSetPositionBatchOwnerHidden(receipt))
        {
            return new(
                SetPositionCollisionBatchDispatchStatus.Completed,
                reported);
        }
        if (receipt.Owner.PositionAuthorityVersion
                != receipt.OwnerPositionAuthorityVersion
            || _owners.TryGetValue(
                receipt.OwnerKey, out OwnerState? suffixOwner)
                && suffixOwner.SetPositionBatchId != receipt.BatchId)
        {
            return new(
                SetPositionCollisionBatchDispatchStatus.Displaced,
                reported);
        }

        EndExpiredObjectCollisions(
            receipt.Owner,
            receipt.OwnerBody,
            receipt.OwnerKey,
            receipt.PhysicsTime,
            force: false,
            receipt.BatchId,
            receipt.OwnerPositionAuthorityVersion);
        if (!IsSetPositionBatchOwnerCurrent(receipt))
        {
            return new(
                SetPositionCollisionBatchDispatchStatus.Displaced,
                reported);
        }
        reported |= DispatchEnvironmentSuffix(receipt);
        return new(
            SetPositionCollisionBatchDispatchStatus.Completed,
            reported);
    }

    private bool IsExactSetPositionBatchOwnerHidden(
        in SetPositionCollisionBatchReceipt receipt) =>
        _entities.IsCurrent(receipt.Owner)
        && receipt.Owner.PositionAuthorityVersion
            == receipt.OwnerPositionAuthorityVersion
        && ReferenceEquals(receipt.Owner.PhysicsBody, receipt.OwnerBody)
        && (receipt.OwnerBody.State & PhysicsStateFlags.Hidden) != 0;

    internal bool DiscardSetPositionBatch(
        in SetPositionCollisionBatchReceipt receipt) =>
        receipt.IsValid
        && _pendingSetPositionDispatches.Remove(receipt.BatchId);

    internal void RetireSetPositionBatchOwner(
        in SetPositionCollisionBatchReceipt receipt)
    {
        if (!receipt.IsValid || _disposed)
            return;
        _pendingSetPositionDispatches.Remove(receipt.BatchId);
        if (!IsKnownParticipant(
                receipt.Owner,
                receipt.OwnerBody,
                receipt.OwnerKey)
            || !_owners.TryGetValue(
                receipt.OwnerKey, out OwnerState? owner)
            || owner.SetPositionBatchId != receipt.BatchId)
        {
            return;
        }
        _mutationRevision = checked(_mutationRevision + 1UL);
        if (!_admissionBlocked.Add(receipt.OwnerKey))
            return;
        try
        {
            ForceEnd(receipt.Owner, receipt.OwnerKey);
            if (_owners.TryGetValue(receipt.OwnerKey, out OwnerState? current)
                && ReferenceEquals(current, owner)
                && current.SetPositionBatchId == receipt.BatchId)
            {
                current.CollidingWithEnvironment = false;
                _owners.Remove(receipt.OwnerKey);
            }
        }
        finally
        {
            _admissionBlocked.Remove(receipt.OwnerKey);
        }
    }

    private bool DispatchTrackingAction(
        StagedReportAction action,
        double physicsTime,
        ulong batchId)
    {
        if (action.Other is null
            || action.OtherBody is null
            || action.OtherKey is not { } targetKey
            || !IsKnownParticipant(
                action.Recipient,
                action.RecipientBody,
                action.RecipientKey)
            || !IsKnownParticipant(
                action.Other,
                action.OtherBody,
                targetKey))
        {
            return false;
        }

        PhysicsStateFlags targetState = action.OtherBody.State;
        if ((targetState & PhysicsStateFlags.Static) != 0)
        {
            // track_object_collision is skipped entirely on this path. Any
            // older record therefore retains its old timestamp and remains
            // eligible for the immediately-following expiry pass.
            return DispatchEnvironmentAction(
                action.Recipient,
                action.RecipientBody,
                action.RecipientKey,
                action.RecipientContact,
                batchId);
        }

        OwnerState state = GetOrCreateOwner(action.RecipientKey, batchId);
        bool isNew = !state.Records.ContainsKey(targetKey);
        state.Records[targetKey] = new CollisionRecord(
            physicsTime,
            (targetState & PhysicsStateFlags.Ethereal) != 0,
            action.Other.ServerGuid);
        if (!isNew)
        {
            _mutationRevision = checked(_mutationRevision + 1UL);
            return false;
        }

        state.Order.Add(targetKey);
        AddReverseOwner(targetKey, action.RecipientKey);
        _mutationRevision = checked(_mutationRevision + 1UL);
        return ReportObject(
            action.Recipient,
            action.RecipientBody,
            action.RecipientKey,
            action.Other,
            action.OtherBody,
            targetKey,
            targetState,
            action.RecipientContact,
            action.ExactDormantRecipient,
            action.RecipientPositionAuthorityVersion,
            batchId);
    }

    private bool DispatchEnvironmentAction(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        RuntimeEntityKey ownerKey,
        bool previousContact,
        ulong batchId)
    {
        if (!IsKnownParticipant(owner, ownerBody, ownerKey))
            return false;
        OwnerState state = GetOrCreateOwner(ownerKey, batchId);
        if (state.CollidingWithEnvironment)
            return false;

        state.CollidingWithEnvironment = true;
        _mutationRevision = checked(_mutationRevision + 1UL);
        bool reported = (ownerBody.State
            & PhysicsStateFlags.ReportCollisions) != 0;
        if (reported)
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.EnvironmentCollision,
                ownerKey,
                owner.ServerGuid,
                Other: null,
                OtherServerGuid: null,
                previousContact,
                OtherWasInContact: false));
        }

        StopMissileForStagedOwner(
            owner,
            ownerBody,
            ownerKey,
            requireCurrentMissile: true);
        return reported;
    }

    private bool DispatchEnvironmentSuffix(
        in SetPositionCollisionBatchReceipt receipt)
    {
        if (!IsSetPositionBatchOwnerCurrent(receipt)
            || !IsKnownParticipant(
                receipt.Owner,
                receipt.OwnerBody,
                receipt.OwnerKey))
        {
            return false;
        }

        OwnerState? state = TryGetOwner(receipt.OwnerKey);
        if (state?.CollidingWithEnvironment == true)
        {
            if (state.CollidingWithEnvironment
                != receipt.FinalCollidedWithEnvironment)
            {
                state.CollidingWithEnvironment =
                    receipt.FinalCollidedWithEnvironment;
                _mutationRevision = checked(_mutationRevision + 1UL);
            }
        }
        else if (receipt.FinalCollidedWithEnvironment
                 || receipt.FinalGroundEdge)
        {
            bool reported = DispatchEnvironmentAction(
                receipt.Owner,
                receipt.OwnerBody,
                receipt.OwnerKey,
                receipt.PreviousContact,
                receipt.BatchId);
            TrimEmptyOwner(receipt.OwnerKey);
            return reported;
        }
        TrimEmptyOwner(receipt.OwnerKey);
        return false;
    }

    private void StopMissileForStagedOwner(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        RuntimeEntityKey ownerKey,
        bool requireCurrentMissile)
    {
        if (!IsKnownParticipant(owner, ownerBody, ownerKey)
            || !_entities.StopMissileAfterCollision(
                owner,
                requireCurrentMissile))
        {
            return;
        }
        _shadows.UpdatePhysicsState(
            ownerKey.LocalEntityId,
            (uint)owner.FinalPhysicsState);
    }

    private static OwnerState CloneOwnerState(OwnerState? source)
    {
        var clone = new OwnerState();
        if (source is null)
            return clone;
        foreach ((RuntimeEntityKey key, CollisionRecord record)
                 in source.Records)
            clone.Records.Add(key, record);
        clone.Order.AddRange(source.Order);
        clone.CollidingWithEnvironment = source.CollidingWithEnvironment;
        return clone;
    }

    internal bool HandleReports(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        double physicsTime,
        bool previousContact,
        bool previousOnWalkable,
        bool collidedWithEnvironment,
        ImmutableArray<uint> collidedObjectIds)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ownerBody);
        if (!double.IsFinite(physicsTime)
            || !TryGetCurrentParticipant(owner, ownerBody, out RuntimeEntityKey key))
        {
            return false;
        }
        _mutationRevision = checked(_mutationRevision + 1UL);

        bool reported = false;
        if (collidedObjectIds.IsDefault)
            collidedObjectIds = ImmutableArray<uint>.Empty;
        for (int index = 0; index < collidedObjectIds.Length; index++)
        {
            if (!IsCurrentParticipant(owner, ownerBody, key))
                return reported;

            uint collidedId = collidedObjectIds[index];
            if (collidedId == 0u || collidedId == key.LocalEntityId)
                continue;
            if (!_shadows.TryGetCollisionOwner(
                    collidedId,
                    out _,
                    out bool registeredStatic))
            {
                continue;
            }

            if (registeredStatic)
            {
                reported |= ReportEnvironment(
                    owner,
                    ownerBody,
                    key,
                    previousContact);
                continue;
            }

            if (!TryGetCurrentParticipant(
                    collidedId,
                    out RuntimeEntityRecord target,
                    out PhysicsBody targetBody,
                    out RuntimeEntityKey targetKey))
            {
                continue;
            }

            PhysicsStateFlags targetState = targetBody.State;
            if ((targetState & PhysicsStateFlags.Static) != 0)
            {
                reported |= ReportEnvironment(
                    owner,
                    ownerBody,
                    key,
                    previousContact);
                continue;
            }

            OwnerState ownerState = GetOrCreateOwner(key);
            bool isNew = !ownerState.Records.ContainsKey(targetKey);
            ownerState.Records[targetKey] = new CollisionRecord(
                physicsTime,
                (targetState & PhysicsStateFlags.Ethereal) != 0,
                target.ServerGuid);
            if (!isNew)
                continue;

            ownerState.Order.Add(targetKey);
            AddReverseOwner(targetKey, key);
            reported |= ReportObject(
                owner,
                ownerBody,
                key,
                target,
                targetBody,
                targetKey,
                targetState,
                previousContact);
        }

        if (!IsCurrentParticipant(owner, ownerBody, key))
            return reported;

        EndExpiredObjectCollisions(
            owner,
            ownerBody,
            key,
            physicsTime,
            force: false);
        if (!IsCurrentParticipant(owner, ownerBody, key))
            return reported;

        OwnerState? retained = TryGetOwner(key);
        if (retained?.CollidingWithEnvironment == true)
        {
            retained.CollidingWithEnvironment = collidedWithEnvironment;
        }
        else if (collidedWithEnvironment
                 || (!previousOnWalkable && ownerBody.OnWalkable))
        {
            reported |= ReportEnvironment(
                owner,
                ownerBody,
                key,
                previousContact);
        }

        TrimEmptyOwner(key);
        return reported;
    }

    internal void LeaveWorld(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return;
        _mutationRevision = checked(_mutationRevision + 1UL);
        if (!_admissionBlocked.Add(key))
            return;
        try
        {
            ForceEnd(record, key);
        }
        finally
        {
            _admissionBlocked.Remove(key);
        }
    }

    internal void LeaveWorldBatch(
        IReadOnlyList<RuntimeEntityRecord> records)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(records);
        _mutationRevision = checked(_mutationRevision + 1UL);
        var blocked = new List<(RuntimeEntityRecord Record, RuntimeEntityKey Key)>(
            records.Count);
        for (int index = 0; index < records.Count; index++)
        {
            if (records[index].Key is { } key
                && _admissionBlocked.Add(key))
            {
                blocked.Add((records[index], key));
            }
        }
        try
        {
            for (int index = 0; index < blocked.Count; index++)
                ForceEnd(blocked[index].Record, blocked[index].Key);
        }
        finally
        {
            for (int index = 0; index < blocked.Count; index++)
                _admissionBlocked.Remove(blocked[index].Key);
        }
    }

    internal void Forget(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return;
        _mutationRevision = checked(_mutationRevision + 1UL);
        if (_admissionBlocked.Contains(key))
        {
            if (!_leaving.Contains(key))
                ForceEnd(record, key);
        }
        else
        {
            LeaveWorld(record);
        }
        _owners.Remove(key);
    }

    internal void ResetSession()
    {
        EnsureNotDisposed();
        _mutationRevision = checked(_mutationRevision + 1UL);
        _owners.Clear();
        _ownersByPeer.Clear();
        _pendingReports.Clear();
        _leaving.Clear();
        _admissionBlocked.Clear();
        _pendingSetPositionDispatches.Clear();
        _dispatchEpoch = checked(_dispatchEpoch + 1UL);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _owners.Clear();
        _ownersByPeer.Clear();
        _pendingReports.Clear();
        _leaving.Clear();
        _admissionBlocked.Clear();
        _pendingSetPositionDispatches.Clear();
        _observers = [];
        _dispatchEpoch = checked(_dispatchEpoch + 1UL);
        _disposed = true;
    }

    private bool ReportObject(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        RuntimeEntityKey ownerKey,
        RuntimeEntityRecord target,
        PhysicsBody targetBody,
        RuntimeEntityKey targetKey,
        PhysicsStateFlags targetState,
        bool previousContact,
        bool exactDormantOwner = false,
        ulong expectedOwnerPositionAuthorityVersion = 0UL,
        ulong setPositionBatchId = 0UL)
    {
        if ((targetState & PhysicsStateFlags.ReportAsEnvironment) != 0)
        {
            return ReportEnvironment(
                owner,
                ownerBody,
                ownerKey,
                previousContact,
                setPositionBatchId);
        }

        PhysicsStateFlags ownerState = ownerBody.State;
        bool ownerWasMissile =
            (ownerState & PhysicsStateFlags.Missile) != 0;
        bool ownerReported = (targetState
                & PhysicsStateFlags.IgnoreCollisions) == 0
            && (ownerState & PhysicsStateFlags.ReportCollisions) != 0;
        if (ownerReported)
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.ObjectCollision,
                ownerKey,
                owner.ServerGuid,
                targetKey,
                target.ServerGuid,
                previousContact,
                targetBody.InContact));
        }

        if (ownerWasMissile
            && (targetState & PhysicsStateFlags.IgnoreCollisions) == 0)
        {
            StopMissile(
                owner,
                ownerBody,
                ownerKey,
                requireCurrentMissile: false);
        }

        bool targetReported = IsCurrentParticipant(target, targetBody, targetKey)
            && (targetBody.State & PhysicsStateFlags.ReportCollisions) != 0
            && (IsCurrentParticipant(owner, ownerBody, ownerKey)
                || exactDormantOwner
                    && IsExactDormantParticipant(
                        owner,
                        ownerBody,
                        ownerKey,
                        expectedOwnerPositionAuthorityVersion))
            && (ownerBody.State & PhysicsStateFlags.IgnoreCollisions) == 0;
        if (targetReported)
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.ObjectCollision,
                targetKey,
                target.ServerGuid,
                ownerKey,
                owner.ServerGuid,
                targetBody.InContact,
                previousContact));
        }
        return ownerReported || targetReported;
    }

    private bool IsExactDormantParticipant(
        RuntimeEntityRecord record,
        PhysicsBody body,
        RuntimeEntityKey key,
        ulong expectedPositionAuthorityVersion) =>
        !body.InWorld
        && (body.TransientState & TransientStateFlags.Active) == 0
        && (body.State & PhysicsStateFlags.Hidden) == 0
        && _entities.IsCurrent(record)
        && !_leaving.Contains(key)
        && !_admissionBlocked.Contains(key)
        && expectedPositionAuthorityVersion != 0UL
        && record.PositionAuthorityVersion
            == expectedPositionAuthorityVersion
        && IsKnownParticipant(record, body, key);

    private bool ReportEnvironment(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        RuntimeEntityKey ownerKey,
        bool previousContact,
        ulong setPositionBatchId = 0UL)
    {
        OwnerState state = GetOrCreateOwner(
            ownerKey, setPositionBatchId);
        if (state.CollidingWithEnvironment)
            return false;

        bool reported = (ownerBody.State
            & PhysicsStateFlags.ReportCollisions) != 0;
        state.CollidingWithEnvironment = true;
        if (reported)
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.EnvironmentCollision,
                ownerKey,
                owner.ServerGuid,
                Other: null,
                OtherServerGuid: null,
                previousContact,
                OtherWasInContact: false));
        }
        StopMissile(owner, ownerBody, ownerKey);
        return reported;
    }

    private void EndExpiredObjectCollisions(
        RuntimeEntityRecord owner,
        PhysicsBody? ownerBody,
        RuntimeEntityKey ownerKey,
        double physicsTime,
        bool force,
        ulong setPositionBatchId = 0UL,
        ulong expectedPositionAuthorityVersion = 0UL)
    {
        if (!_owners.TryGetValue(ownerKey, out OwnerState? state)
            || state.Records.Count == 0)
        {
            return;
        }

        List<EndedCollision>? ended = null;
        for (int index = 0; index < state.Order.Count; index++)
        {
            RuntimeEntityKey targetKey = state.Order[index];
            if (!state.Records.TryGetValue(
                    targetKey,
                    out CollisionRecord collision))
            {
                continue;
            }
            double age = physicsTime - collision.TouchedTime;
            if (!force
                && !(age > 1d)
                && !(collision.Ethereal && age > 0d))
            {
                continue;
            }
            (ended ??= []).Add(new EndedCollision(
                targetKey,
                collision.ServerGuid));
        }

        if (ended is null)
            return;

        ulong reportEpoch = _dispatchEpoch;
        for (int index = 0; index < ended.Count; index++)
        {
            EndedCollision collision = ended[index];
            state.Records.Remove(collision.Key);
            state.Order.Remove(collision.Key);
            RemoveReverseOwner(collision.Key, ownerKey);
        }

        ulong sourceSessionVersion = _entities.SessionLifetimeVersion;
        ulong sourceLifetimeMutation =
            _entities.CurrentLifetimeMutation(owner.ServerGuid);
        for (int index = 0; index < ended.Count; index++)
        {
            if (_disposed
                || reportEpoch != _dispatchEpoch
                || _entities.SessionLifetimeVersion != sourceSessionVersion
                || _entities.CurrentLifetimeMutation(owner.ServerGuid)
                    != sourceLifetimeMutation
                || setPositionBatchId != 0UL
                    && (owner.PositionAuthorityVersion
                            != expectedPositionAuthorityVersion
                        || !_owners.TryGetValue(
                            ownerKey, out OwnerState? currentOwner)
                        || !ReferenceEquals(currentOwner, state)
                        || currentOwner.SetPositionBatchId
                            != setPositionBatchId))
            {
                break;
            }
            EndedCollision collision = ended[index];
            if (TryGetParticipant(
                    collision.Key,
                    out RuntimeEntityRecord target,
                    out PhysicsBody targetBody))
            {
                if ((targetBody.State
                        & PhysicsStateFlags.ReportAsEnvironment) != 0)
                {
                    continue;
                }
                PublishResolvedObjectEnd(
                    owner,
                    ownerBody,
                    ownerKey,
                    target,
                    targetBody,
                    collision.Key);
            }
            else
            {
                PublishMissingObjectEnd(
                    owner,
                    ownerBody,
                    ownerKey,
                    collision.Key,
                    collision.ServerGuid);
            }
        }
        TrimEmptyOwner(ownerKey);
    }

    private bool IsSetPositionBatchOwnerCurrent(
        in SetPositionCollisionBatchReceipt receipt) =>
        receipt.Owner.PositionAuthorityVersion
            == receipt.OwnerPositionAuthorityVersion
        && (!_owners.TryGetValue(receipt.OwnerKey, out OwnerState? owner)
            || owner.SetPositionBatchId == receipt.BatchId);

    private void PublishResolvedObjectEnd(
        RuntimeEntityRecord owner,
        PhysicsBody? ownerBody,
        RuntimeEntityKey ownerKey,
        RuntimeEntityRecord target,
        PhysicsBody targetBody,
        RuntimeEntityKey targetKey)
    {
        ulong sourceSessionVersion = _entities.SessionLifetimeVersion;
        ulong sourceLifetimeMutation =
            _entities.CurrentLifetimeMutation(owner.ServerGuid);
        ulong reportEpoch = _dispatchEpoch;
        if (ownerBody is not null
            && (ownerBody.State & PhysicsStateFlags.ReportCollisions) != 0
            && IsKnownParticipant(owner, ownerBody, ownerKey))
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                ownerKey,
                owner.ServerGuid,
                targetKey,
                target.ServerGuid,
                ownerBody.InContact,
                targetBody.InContact));
        }

        if ((targetBody.State & PhysicsStateFlags.ReportCollisions) != 0
            && reportEpoch == _dispatchEpoch
            && !_disposed
            && _entities.SessionLifetimeVersion == sourceSessionVersion
            && _entities.CurrentLifetimeMutation(owner.ServerGuid)
                == sourceLifetimeMutation
            && ownerBody is not null
            && IsKnownParticipant(owner, ownerBody, ownerKey)
            && IsKnownParticipant(target, targetBody, targetKey))
        {
            Publish(new RuntimeCollisionReport(
                NextSequence(),
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                targetKey,
                target.ServerGuid,
                ownerKey,
                owner.ServerGuid,
                targetBody.InContact,
                ownerBody?.InContact ?? false));
        }
    }

    private void PublishMissingObjectEnd(
        RuntimeEntityRecord owner,
        PhysicsBody? ownerBody,
        RuntimeEntityKey ownerKey,
        RuntimeEntityKey targetKey,
        uint targetServerGuid)
    {
        if (ownerBody is null
            || (ownerBody.State & PhysicsStateFlags.ReportCollisions) == 0
            || !IsKnownParticipant(owner, ownerBody, ownerKey))
        {
            return;
        }
        Publish(new RuntimeCollisionReport(
            NextSequence(),
            RuntimeCollisionReportKind.ObjectCollisionEnd,
            ownerKey,
            owner.ServerGuid,
            targetKey,
            targetServerGuid,
            ownerBody.InContact,
            OtherWasInContact: false));
    }

    private void StopMissile(
        RuntimeEntityRecord owner,
        PhysicsBody ownerBody,
        RuntimeEntityKey ownerKey,
        bool requireCurrentMissile = true)
    {
        if (!IsCurrentParticipant(owner, ownerBody, ownerKey)
            || !_entities.StopMissileAfterCollision(
                owner,
                requireCurrentMissile))
        {
            return;
        }
        _shadows.UpdatePhysicsState(
            ownerKey.LocalEntityId,
            (uint)owner.FinalPhysicsState);
    }

    private OwnerState GetOrCreateOwner(
        RuntimeEntityKey key,
        ulong setPositionBatchId = 0UL)
    {
        if (!_owners.TryGetValue(key, out OwnerState? owner))
        {
            owner = new OwnerState();
            _owners.Add(key, owner);
        }
        owner.SetPositionBatchId = setPositionBatchId;
        return owner;
    }

    private OwnerState? TryGetOwner(RuntimeEntityKey key) =>
        _owners.TryGetValue(key, out OwnerState? owner) ? owner : null;

    private void TrimEmptyOwner(RuntimeEntityKey key)
    {
        if (_owners.TryGetValue(key, out OwnerState? owner)
            && owner.Records.Count == 0
            && !owner.CollidingWithEnvironment)
        {
            _owners.Remove(key);
        }
    }

    private void AddReverseOwner(
        RuntimeEntityKey peer,
        RuntimeEntityKey owner)
    {
        if (!_ownersByPeer.TryGetValue(
                peer,
                out List<RuntimeEntityKey>? owners))
        {
            owners = [];
            _ownersByPeer.Add(peer, owners);
        }
        if (!owners.Contains(owner))
            owners.Add(owner);
    }

    private void RemoveReverseOwner(
        RuntimeEntityKey peer,
        RuntimeEntityKey owner)
    {
        if (!_ownersByPeer.TryGetValue(
                peer,
                out List<RuntimeEntityKey>? owners))
        {
            return;
        }
        owners.Remove(owner);
        if (owners.Count == 0)
            _ownersByPeer.Remove(peer);
    }

    private bool TryGetCurrentParticipant(
        RuntimeEntityRecord record,
        PhysicsBody body,
        out RuntimeEntityKey key)
    {
        key = record.Key ?? default;
        return key != default
            && IsCurrentParticipant(record, body, key);
    }

    private bool TryGetCurrentParticipant(
        uint localEntityId,
        out RuntimeEntityRecord record,
        out PhysicsBody body,
        out RuntimeEntityKey key)
    {
        if (_entities.TryGetByLocalId(localEntityId, out record!)
            && record.PhysicsBody is { } retained
            && record.Key is { } retainedKey
            && retainedKey.LocalEntityId == localEntityId
            && !_leaving.Contains(retainedKey)
            && !_admissionBlocked.Contains(retainedKey)
            && retained.InWorld
            && (retained.State & PhysicsStateFlags.Hidden) == 0
            && _entities.IsCurrent(record))
        {
            body = retained;
            key = retainedKey;
            return true;
        }
        body = null!;
        key = default;
        return false;
    }

    private bool TryGetParticipant(
        RuntimeEntityKey key,
        out RuntimeEntityRecord record,
        out PhysicsBody body)
    {
        if (_entities.TryGetByLocalId(key.LocalEntityId, out record!)
            && record.Key == key
            && !_leaving.Contains(key)
            && _entities.IsCurrent(record)
            && record.PhysicsBody is { } retained)
        {
            body = retained;
            return true;
        }
        body = null!;
        return false;
    }

    private bool IsCurrentParticipant(
        RuntimeEntityRecord record,
        PhysicsBody body,
        RuntimeEntityKey key) =>
        _entities.IsCurrent(record)
        && !_leaving.Contains(key)
        && !_admissionBlocked.Contains(key)
        && body.InWorld
        && (body.State & PhysicsStateFlags.Hidden) == 0
        && IsKnownParticipant(record, body, key);

    private void ForceEnd(
        RuntimeEntityRecord record,
        RuntimeEntityKey key)
    {
        if (!_leaving.Add(key))
            return;
        try
        {
            EndExpiredObjectCollisions(
                record,
                record.PhysicsBody,
                key,
                physicsTime: 0d,
                force: true);
        }
        finally
        {
            _leaving.Remove(key);
        }
    }

    private bool IsKnownParticipant(
        RuntimeEntityRecord record,
        PhysicsBody body,
        RuntimeEntityKey key) =>
        record.Key == key
        && ReferenceEquals(record.PhysicsBody, body)
        && _entities.TryGetByLocalId(
            key.LocalEntityId,
            out RuntimeEntityRecord retained)
        && ReferenceEquals(retained, record);

    private ulong NextSequence() => checked(++_nextSequence);

    private void Publish(in RuntimeCollisionReport report)
    {
        _pendingReports.Enqueue(new PendingReport(_dispatchEpoch, report));
        if (_dispatching)
            return;

        _dispatching = true;
        try
        {
            while (!_disposed && _pendingReports.TryDequeue(out PendingReport pending))
            {
                if (pending.Epoch != _dispatchEpoch)
                    continue;
                IRuntimeCollisionReportObserver[] observers = _observers;
                for (int index = 0; index < observers.Length; index++)
                {
                    try
                    {
                        observers[index].OnCollisionReport(pending.Report);
                    }
                    catch (Exception error)
                    {
                        _dispatchFailureCount++;
                        System.Diagnostics.Trace.TraceError(
                            "Runtime collision-report observer failed: {0}",
                            error);
                    }
                    if (_disposed || pending.Epoch != _dispatchEpoch)
                        break;
                }
            }
        }
        finally
        {
            _dispatching = false;
            if (_disposed)
                _pendingReports.Clear();
        }
    }

    private void Unsubscribe(IRuntimeCollisionReportObserver observer)
    {
        int index = Array.IndexOf(_observers, observer);
        if (index < 0)
            return;
        if (_observers.Length == 1)
        {
            _observers = [];
            return;
        }
        var replacement = new IRuntimeCollisionReportObserver[
            _observers.Length - 1];
        if (index > 0)
            Array.Copy(_observers, 0, replacement, 0, index);
        if (index < _observers.Length - 1)
        {
            Array.Copy(
                _observers,
                index + 1,
                replacement,
                index,
                _observers.Length - index - 1);
        }
        _observers = replacement;
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    internal sealed class OwnerState
    {
        internal Dictionary<RuntimeEntityKey, CollisionRecord> Records { get; }
            = new();
        internal List<RuntimeEntityKey> Order { get; } = [];
        internal bool CollidingWithEnvironment { get; set; }
        internal ulong SetPositionBatchId { get; set; }
    }

    internal readonly record struct CollisionRecord(
        double TouchedTime,
        bool Ethereal,
        uint ServerGuid);

    private readonly record struct EndedCollision(
        RuntimeEntityKey Key,
        uint ServerGuid);

    private readonly record struct PendingReport(
        ulong Epoch,
        RuntimeCollisionReport Report);

    private sealed class Subscription : IDisposable
    {
        private RuntimeCollisionReportingState? _owner;
        private readonly IRuntimeCollisionReportObserver _observer;

        internal Subscription(
            RuntimeCollisionReportingState owner,
            IRuntimeCollisionReportObserver observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            RuntimeCollisionReportingState? owner =
                Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(_observer);
        }
    }
}
