using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

internal readonly record struct RuntimeInitialCreateResidenceToken(
    RuntimeEntityKey Entity,
    ulong LeaseId,
    ulong SessionLifetimeVersion,
    ulong PositionAuthorityVersion,
    ulong CreateIntegrationVersion,
    ulong SourcePlacementCommitVersion)
{
    internal bool IsValid => Entity.LocalEntityId != 0u
        && LeaseId != 0UL
        && PositionAuthorityVersion != 0UL
        && CreateIntegrationVersion != 0UL;
}

internal readonly record struct RuntimeInitialCreateResidenceLease(
    RuntimeInitialCreateResidenceToken Token,
    RuntimeAuthoritativePositionRoute Route,
    RuntimeEntityPlacementToken Placement,
    WorldSession.EntitySpawn InitialCreate,
    ImmutableArray<RuntimeInitialCreateResidenceContinuation> Continuations)
{
    internal bool IsValid => Token.IsValid
        && Route.Accepted
        && Route.Authority.Entity == Token.Entity
        && InitialCreate.Guid != 0u
        && InitialCreate.InstanceSequence == Token.Entity.Incarnation
        && !Continuations.IsDefault
        && HasValidContinuationChain()
        && (!Route.PerformsSetPosition
            || Placement.IsValid
                && Placement.Entity == Token.Entity);

    private bool HasValidContinuationChain()
    {
        uint ownerGuid = InitialCreate.Guid;
        for (int index = 0; index < Continuations.Length; index++)
        {
            RuntimeInitialCreateResidenceContinuation continuation =
                Continuations[index];
            if (!continuation.IsValid
                || continuation.Sequence != (ulong)index + 1UL
                || continuation.InstanceSequence != Token.Entity.Incarnation
                || continuation.Actions.Any(
                    action => action.OwnerGuid != ownerGuid))
            {
                return false;
            }
        }
        return true;
    }
}

internal enum RuntimeInitialCreateContinuationKind : byte
{
    SameIncarnationCreate,
    ObjDesc,
    Parent,
    Pickup,
    Position,
    Movement,
    State,
    Vector,
}

internal enum RuntimeInitialCreateTailActionKind : byte
{
    PreTailDescriptionAdaptation,
    ObjDesc,
    CreateParent,
    Parent,
    Pickup,
    Position,
    Movement,
    State,
    Vector,
    WeenieDescription,
    ResidentCellCleanup,
}

internal readonly record struct RuntimeInitialCreateTailAction(
    RuntimeInitialCreateTailActionKind Kind,
    uint OwnerGuid,
    PhysicsSpawnData? Description = null,
    ObjDescEvent.Parsed? ObjDesc = null,
    CreateParentUpdate? CreateParent = null,
    ParentEvent.Parsed? Parent = null,
    PickupEvent.Parsed? Pickup = null,
    WorldSession.EntityPositionUpdate? Position = null,
    WorldSession.EntityMotionUpdate? Movement = null,
    SetState.Parsed? State = null,
    VectorUpdate.Parsed? Vector = null,
    WorldSession.EntitySpawn? WeenieDescription = null,
    RuntimeAcceptedPositionSource PositionSource =
        RuntimeAcceptedPositionSource.Unknown,
    PositionTimestampDisposition PositionDisposition =
        PositionTimestampDisposition.Rejected,
    ushort PreviousTeleportSequence = 0,
    AcceptedPhysicsTimestamps AcceptedTimestamps = default,
    bool AppliesMovementPayload = false,
    bool RetainMovementPayload = true,
    bool HasTimestampMutation = false)
{
    internal bool IsStructurallyValid => HasExclusivePayload()
        && Kind switch
    {
        RuntimeInitialCreateTailActionKind.PreTailDescriptionAdaptation =>
            Description is not null,
        RuntimeInitialCreateTailActionKind.ObjDesc => ObjDesc is { } objDesc
            && objDesc.Guid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.CreateParent =>
            CreateParent is { } createParent
            && createParent.ChildGuid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.Parent => Parent is { } parent
            && parent.ChildGuid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.Pickup => Pickup is { } pickup
            && pickup.Guid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.Position => Position is { } position
            && position.Guid == OwnerGuid
            && PositionSource is RuntimeAcceptedPositionSource.PositionEvent
                or RuntimeAcceptedPositionSource.SameIncarnationCreate
            && (PositionDisposition is PositionTimestampDisposition.Apply
                    or PositionTimestampDisposition.ForcePosition
                || PositionDisposition is PositionTimestampDisposition.Rejected
                    && HasTimestampMutation),
        RuntimeInitialCreateTailActionKind.Movement => Movement is { } movement
            && movement.Guid == OwnerGuid
            && (AppliesMovementPayload || HasTimestampMutation),
        RuntimeInitialCreateTailActionKind.State => State is { } state
            && state.Guid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.Vector => Vector is { } vector
            && vector.Guid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.WeenieDescription =>
            WeenieDescription is { } weenie
            && weenie.Guid == OwnerGuid,
        RuntimeInitialCreateTailActionKind.ResidentCellCleanup => true,
        _ => false,
    };

    internal bool MatchesInstance(ushort instanceSequence) => Kind switch
    {
        RuntimeInitialCreateTailActionKind.PreTailDescriptionAdaptation =>
            Description is { } description
            && description.Timestamps.Instance == instanceSequence,
        RuntimeInitialCreateTailActionKind.ObjDesc =>
            ObjDesc is { } objDesc
            && objDesc.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.CreateParent =>
            CreateParent is { } createParent
            && createParent.ChildInstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.Parent =>
            AcceptedTimestamps.Instance == instanceSequence,
        RuntimeInitialCreateTailActionKind.Pickup =>
            Pickup is { } pickup
            && pickup.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.Position =>
            Position is { } position
            && position.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.Movement =>
            Movement is { } movement
            && movement.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.State =>
            State is { } state
            && state.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.Vector =>
            Vector is { } vector
            && vector.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.WeenieDescription =>
            WeenieDescription is { } weenie
            && weenie.InstanceSequence == instanceSequence,
        RuntimeInitialCreateTailActionKind.ResidentCellCleanup => true,
        _ => false,
    };

    private bool HasExclusivePayload()
    {
        int payloadCount = (Description is null ? 0 : 1)
            + (ObjDesc is null ? 0 : 1)
            + (CreateParent is null ? 0 : 1)
            + (Parent is null ? 0 : 1)
            + (Pickup is null ? 0 : 1)
            + (Position is null ? 0 : 1)
            + (Movement is null ? 0 : 1)
            + (State is null ? 0 : 1)
            + (Vector is null ? 0 : 1)
            + (WeenieDescription is null ? 0 : 1);
        return Kind is RuntimeInitialCreateTailActionKind.ResidentCellCleanup
            ? payloadCount == 0
            : payloadCount == 1;
    }
}

internal readonly record struct RuntimeInitialCreateResidenceContinuation(
    ulong Sequence,
    RuntimeInitialCreateContinuationKind Kind,
    ushort InstanceSequence,
    RuntimeAcceptedPositionSource PositionSource,
    ImmutableArray<RuntimeInitialCreateTailAction> Actions)
{
    internal bool IsValid => Sequence != 0UL
        && !Actions.IsDefaultOrEmpty
        && Actions.All(static action => action.IsStructurallyValid)
        && HasMatchingInstances()
        && HasValidShape();

    private bool HasMatchingInstances()
    {
        for (int index = 0; index < Actions.Length; index++)
        {
            if (!Actions[index].MatchesInstance(InstanceSequence))
                return false;
        }
        return true;
    }

    private bool HasValidShape()
    {
        if (Kind is not RuntimeInitialCreateContinuationKind.SameIncarnationCreate)
        {
            if (Actions.Length != 1)
                return false;
            RuntimeInitialCreateTailAction action = Actions[0];
            RuntimeInitialCreateTailActionKind expected = Kind switch
            {
                RuntimeInitialCreateContinuationKind.ObjDesc =>
                    RuntimeInitialCreateTailActionKind.ObjDesc,
                RuntimeInitialCreateContinuationKind.Parent =>
                    RuntimeInitialCreateTailActionKind.Parent,
                RuntimeInitialCreateContinuationKind.Pickup =>
                    RuntimeInitialCreateTailActionKind.Pickup,
                RuntimeInitialCreateContinuationKind.Position =>
                    RuntimeInitialCreateTailActionKind.Position,
                RuntimeInitialCreateContinuationKind.Movement =>
                    RuntimeInitialCreateTailActionKind.Movement,
                RuntimeInitialCreateContinuationKind.State =>
                    RuntimeInitialCreateTailActionKind.State,
                RuntimeInitialCreateContinuationKind.Vector =>
                    RuntimeInitialCreateTailActionKind.Vector,
                _ => throw new InvalidOperationException(
                    $"Unsupported initial-Create continuation kind {Kind}."),
            };
            return action.Kind == expected
                && (Kind is RuntimeInitialCreateContinuationKind.Position
                    ? PositionSource is RuntimeAcceptedPositionSource.PositionEvent
                        && action.PositionSource == PositionSource
                    : PositionSource is RuntimeAcceptedPositionSource.Unknown);
        }

        if (Actions.Length < 2
            || Actions[^2].Kind
                is not RuntimeInitialCreateTailActionKind.WeenieDescription
            || Actions[^1].Kind
                is not RuntimeInitialCreateTailActionKind.ResidentCellCleanup)
        {
            return false;
        }

        bool hasPosition = Actions.Any(
            static action => action.Kind
                is RuntimeInitialCreateTailActionKind.Position);
        if (hasPosition
                ? PositionSource
                    is not RuntimeAcceptedPositionSource.SameIncarnationCreate
                : PositionSource is not RuntimeAcceptedPositionSource.Unknown)
        {
            return false;
        }

        int previousStage = -1;
        int positionBranchCount = 0;
        for (int index = 0; index < Actions.Length; index++)
        {
            RuntimeInitialCreateTailAction action = Actions[index];
            int stage = SameCreateStage(action.Kind);
            if (stage <= previousStage)
                return false;
            if (action.Kind is RuntimeInitialCreateTailActionKind.Position
                && action.PositionSource != PositionSource)
            {
                return false;
            }
            if (action.Kind is RuntimeInitialCreateTailActionKind.CreateParent
                or RuntimeInitialCreateTailActionKind.Pickup
                or RuntimeInitialCreateTailActionKind.Position)
            {
                positionBranchCount++;
            }
            previousStage = stage;
        }
        bool hasPhysicsDescription = Actions.Any(
            static action => action.Kind
                is RuntimeInitialCreateTailActionKind
                    .PreTailDescriptionAdaptation);
        return hasPhysicsDescription
            ? positionBranchCount <= 1
            : positionBranchCount == 0;
    }

    private static int SameCreateStage(RuntimeInitialCreateTailActionKind kind) =>
        kind switch
        {
            RuntimeInitialCreateTailActionKind.PreTailDescriptionAdaptation => 0,
            RuntimeInitialCreateTailActionKind.ObjDesc => 1,
            RuntimeInitialCreateTailActionKind.CreateParent
                or RuntimeInitialCreateTailActionKind.Pickup
                or RuntimeInitialCreateTailActionKind.Position => 2,
            RuntimeInitialCreateTailActionKind.Movement => 3,
            RuntimeInitialCreateTailActionKind.State => 4,
            RuntimeInitialCreateTailActionKind.Vector => 5,
            RuntimeInitialCreateTailActionKind.WeenieDescription => 6,
            RuntimeInitialCreateTailActionKind.ResidentCellCleanup => 7,
            _ => int.MinValue,
        };
}

internal readonly record struct RuntimeInitialCreateResidenceAdoptionToken(
    RuntimeEntityKey Entity,
    ulong LeaseId,
    ulong AdoptionId,
    ulong SessionLifetimeVersion,
    ulong Revision)
{
    internal bool IsValid => Entity.LocalEntityId != 0u
        && LeaseId != 0UL
        && AdoptionId != 0UL
        && Revision != 0UL;
}

internal enum RuntimeInitialCreateResidenceCompletionStatus : byte
{
    Completed,
    PendingPlacement,
    RejectedToken,
    RejectedAuthority,
}

internal enum RuntimeInitialCreateResidenceExecutorReleaseStatus : byte
{
    Released,
    Revised,
    RejectedToken,
    RejectedAuthority,
}

internal readonly record struct RuntimeInitialCreateResidenceReceipt(
    RuntimeInitialCreateResidenceToken Token,
    RuntimeTeleportHookPhase TeleportHookPhase,
    RuntimePlacementProjectionToken Projection,
    uint FullCellId,
    ulong PlacementCommitVersion,
    RuntimeInitialCreateResidenceAdoptionToken Adoption,
    ImmutableArray<RuntimeInitialCreateResidenceContinuation> Continuations);

internal readonly record struct RuntimeInitialCreateResidenceOwnershipSnapshot(
    int ActiveLeaseCount,
    int PendingAdoptionCount,
    ulong LastLeaseId)
{
    internal bool IsConverged => ActiveLeaseCount == 0
        && PendingAdoptionCount == 0;
}

[Flags]
internal enum RuntimeExecutorBaselineFields : byte
{
    None = 0,
    PositionAuthorityVersion = 1 << 0,
    CreateIntegrationVersion = 1 << 1,
    FullCellId = 1 << 2,
    PlacementCommitVersion = 1 << 3,
}

internal sealed class RuntimeInitialCreateResidenceState
{
    private sealed class Entry
    {
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeInitialCreateResidenceLease Lease { get; set; }
    }

    private sealed class CompletedEntry
    {
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeInitialCreateResidenceLease Lease { get; set; }
        internal required RuntimeInitialCreateResidenceReceipt Receipt { get; set; }

        internal bool PlacementAdopted { get; set; }

        internal ulong ExpectedPositionAuthorityVersion { get; set; }
        internal ulong ExpectedCreateIntegrationVersion { get; set; }
        internal uint ExpectedFullCellId { get; set; }
        internal ulong ExpectedPlacementCommitVersion { get; set; }
    }

    private readonly RuntimeEntityDirectory _entities;
    private readonly RuntimeSetPositionState _setPosition;
    private readonly Dictionary<RuntimeEntityKey, Entry> _entries = [];
    private readonly Dictionary<RuntimeEntityKey, CompletedEntry> _completed = [];
    private Func<RuntimeGenerationToken>? _generation;
    private readonly List<Action<RuntimeEntityKey>> _retirementNotifications = [];
    private ulong _nextLeaseId;

    internal RuntimeInitialCreateResidenceState(
        RuntimeEntityDirectory entities,
        RuntimeSetPositionState setPosition)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _setPosition = setPosition
            ?? throw new ArgumentNullException(nameof(setPosition));
    }

    internal void BindGeneration(Func<RuntimeGenerationToken> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (_generation is not null)
        {
            throw new InvalidOperationException(
                "The initial Create residence generation source is already bound.");
        }
        _generation = generation;
    }

    internal void BindRetirementNotification(Action<RuntimeEntityKey> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        _retirementNotifications.Add(notify);
    }

    private void NotifyRetirement(RuntimeEntityKey key)
    {
        foreach (Action<RuntimeEntityKey> notify in _retirementNotifications.ToArray())
            notify(key);
    }

    internal bool CanAcceptCreate(WorldSession.EntitySpawn incoming)
    {
        bool parented = (incoming.ParentGuid
                ?? incoming.Physics?.Parent?.Guid)
            is not null and not 0u;
        bool topLevel = !parented
            && incoming.Position is { LandblockId: not 0u };
        return CurrentGeneration().Value != 0UL
            && _nextLeaseId != ulong.MaxValue
            && (!topLevel
                || _setPosition.CanBeginAuthoredPlacementSequence
                    && RuntimeAuthoritativePositionRouteClassifier
                        .IsValidCreateWirePosition(
                            incoming.Position!.Value));
    }

    internal RuntimeInitialCreateResidenceLease Begin(
        RuntimeEntityRecord record,
        in InboundCreateResult accepted,
        bool isLocalPlayer)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_entities.IsCurrent(record)
            || record.Key is not { } key
            || record.FullCellId != 0u
            || accepted.Snapshot.Guid != record.ServerGuid
            || accepted.Snapshot.InstanceSequence != record.Incarnation)
        {
            return default;
        }

        RuntimeGenerationToken generation = CurrentGeneration();
        RuntimePositionEntityKind entityKind = isLocalPlayer
            ? RuntimePositionEntityKind.LocalPlayer
            : (record.FinalPhysicsState & PhysicsStateFlags.Missile) != 0
                ? RuntimePositionEntityKind.Projectile
                : RuntimePositionEntityKind.Remote;
        WorldSession.EntitySpawn snapshot = accepted.Snapshot;
        RuntimeCreateResidenceKind residence =
            (snapshot.ParentGuid ?? snapshot.Physics?.Parent?.Guid)
                is not null and not 0u
                ? RuntimeCreateResidenceKind.Parented
                : snapshot.Position is { LandblockId: not 0u }
                    ? RuntimeCreateResidenceKind.TopLevel
                    : RuntimeCreateResidenceKind.PickedUp;
        var authority = new RuntimeAuthoritativePositionAuthority(
            generation,
            key,
            record.PositionAuthorityVersion,
            snapshot.PositionSequence,
            accepted.Timestamps.Teleport,
            accepted.Timestamps.Teleport,
            PositionTimestampDisposition.Apply);
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    authority,
                    entityKind,
                    residence,
                    snapshot.Position,
                    new RuntimePositionPlacementFacts(
                        record.FinalPhysicsState,
                        HasAuthoredMoverShape: snapshot.SetupTableId is not null)));
        return Own(record, route);
    }

    internal RuntimeInitialCreateResidenceLease EnqueueAccepted(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceLease prior,
        RuntimeInitialCreateContinuationKind kind,
        RuntimeAcceptedPositionSource positionSource,
        ImmutableArray<RuntimeInitialCreateTailAction> actions)
    {
        var owned = ImmutableArray.CreateBuilder<
            RuntimeInitialCreateTailAction>(actions.Length);
        foreach (RuntimeInitialCreateTailAction action in actions)
        {
            owned.Add(RuntimeInitialCreateAdmissionFreezer.Freeze(action));
        }
        return Enqueue(
            record,
            prior,
            new RuntimeInitialCreateResidenceContinuation(
                NextSequence(prior),
                kind,
                record.Incarnation,
                positionSource,
                owned.MoveToImmutable()));
    }

    internal bool CanEnqueue(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceLease prior)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return false;
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            return ReferenceEquals(entry.Record, record)
                && entry.Lease.Token == prior.Token
                && IsCurrent(entry)
                && entry.Lease.Continuations.Length < int.MaxValue;
        }
        return _completed.TryGetValue(key, out CompletedEntry? completed)
            && ReferenceEquals(completed.Record, record)
            && completed.Lease.Token == prior.Token
            && IsCompletedCurrent(completed)
            && completed.Lease.Continuations.Length < int.MaxValue
            && completed.Receipt.Adoption.Revision < ulong.MaxValue;
    }

    private RuntimeInitialCreateResidenceLease Enqueue(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceLease prior,
        in RuntimeInitialCreateResidenceContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!continuation.IsValid
            || continuation.InstanceSequence != record.Incarnation
            || !CanEnqueue(record, prior))
        {
            return default;
        }

        RuntimeEntityKey key = record.Key!.Value;
        Entry? active = null;
        CompletedEntry? completed = null;
        RuntimeInitialCreateResidenceLease current;
        if (_entries.TryGetValue(key, out active))
            current = active.Lease;
        else if (_completed.TryGetValue(key, out completed))
            current = completed.Lease;
        else
            return default;

        if (continuation.Sequence != NextSequence(current))
            return default;
        RuntimeInitialCreateResidenceLease revised = current with
        {
            Continuations = current.Continuations.Add(continuation),
        };
        if (active is not null)
        {
            active.Lease = revised;
        }
        else
        {
            completed!.Lease = revised;
            RuntimeInitialCreateResidenceAdoptionToken adoption =
                completed.Receipt.Adoption with
                {
                    Revision = completed.Receipt.Adoption.Revision + 1UL,
                };
            completed.Receipt = completed.Receipt with
            {
                Adoption = adoption,
                Continuations = revised.Continuations,
            };
        }
        return revised;
    }

    private static ulong NextSequence(
        in RuntimeInitialCreateResidenceLease lease) =>
        (ulong)lease.Continuations.Length + 1UL;

    internal bool TryGetTransaction(
        RuntimeEntityRecord record,
        out RuntimeInitialCreateResidenceLease lease)
    {
        if (TryGetCurrent(record, out lease))
            return true;
        if (record.Key is { } key
            && _completed.TryGetValue(key, out CompletedEntry? completed)
            && ReferenceEquals(completed.Record, record))
        {
            if (IsCompletedCurrent(completed))
            {
                lease = completed.Lease;
                return true;
            }
            Retire(completed);
        }
        lease = default;
        return false;
    }

    private RuntimeInitialCreateResidenceLease Own(
        RuntimeEntityRecord record,
        in RuntimeAuthoritativePositionRoute route)
    {
        RuntimeEntityKey key = record.Key!.Value;
        if (_entries.ContainsKey(key)
            || _nextLeaseId == ulong.MaxValue)
        {
            return default;
        }
        ulong leaseId = _nextLeaseId + 1UL;

        RuntimeEntityPlacementToken placement = default;
        if (route.PerformsSetPosition)
        {
            placement = _setPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                route.OperationKind);
            if (!placement.IsValid)
                return default;
            if (!_setPosition.WatchPlacementCompletion(placement))
            {
                _ = _setPosition.ForgetExactPlacement(placement);
                return default;
            }
        }
        else if (!route.Accepted)
        {
            return default;
        }

        var token = new RuntimeInitialCreateResidenceToken(
            key,
            leaseId,
            _entities.SessionLifetimeVersion,
            record.PositionAuthorityVersion,
            record.CreateIntegrationVersion,
            record.PlacementCommitVersion);
        var lease = new RuntimeInitialCreateResidenceLease(
            token,
            route,
            placement,
            RuntimeInitialCreateAdmissionFreezer.Freeze(record.Snapshot),
            ImmutableArray<RuntimeInitialCreateResidenceContinuation>.Empty);
        _entries.Add(key, new Entry
        {
            Record = record,
            Lease = lease,
        });
        _nextLeaseId = leaseId;
        return lease;
    }

    internal bool TryGetCurrent(
        RuntimeEntityRecord record,
        out RuntimeInitialCreateResidenceLease lease)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is { } key
            && _entries.TryGetValue(key, out Entry? entry)
            && ReferenceEquals(entry.Record, record))
        {
            if (IsCurrent(entry))
            {
                lease = entry.Lease;
                return true;
            }
            Retire(entry);
        }
        lease = default;
        return false;
    }

    internal RuntimeInitialCreateResidenceCompletionStatus Complete(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken token,
        out RuntimeInitialCreateResidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(record);
        receipt = default;
        if (token.IsValid
            && _completed.TryGetValue(token.Entity, out CompletedEntry? completed)
            && completed.Receipt.Token == token
            && ReferenceEquals(completed.Record, record))
        {
            if (IsCompletedCurrent(completed))
            {
                receipt = completed.Receipt;
                return RuntimeInitialCreateResidenceCompletionStatus.Completed;
            }
            Retire(completed);
            return RuntimeInitialCreateResidenceCompletionStatus
                .RejectedAuthority;
        }
        if (!token.IsValid
            || !_entries.TryGetValue(token.Entity, out Entry? entry)
            || entry.Lease.Token != token
            || !ReferenceEquals(entry.Record, record))
        {
            return RuntimeInitialCreateResidenceCompletionStatus.RejectedToken;
        }
        if (!IsCurrent(entry))
        {
            Retire(entry);
            return RuntimeInitialCreateResidenceCompletionStatus
                .RejectedAuthority;
        }

        RuntimeInitialCreateResidenceLease lease = entry.Lease;
        RuntimePlacementProjectionToken projection = default;
        if (lease.Route.PerformsSetPosition)
        {
            if (_setPosition.IsPlacementCurrent(lease.Placement))
            {
                return RuntimeInitialCreateResidenceCompletionStatus
                    .PendingPlacement;
            }
            if (!_setPosition.TryPeekAcknowledgedPlacement(
                    lease.Placement,
                    out projection)
                || projection.Entity != token.Entity
                || projection.PositionAuthorityVersion
                    != token.PositionAuthorityVersion
                || projection.SessionLifetimeVersion
                    != token.SessionLifetimeVersion
                || projection.ExactCellId == 0u
                || projection.ExactCellId != record.FullCellId
                || projection.PlacementCommitVersion
                    <= token.SourcePlacementCommitVersion
                || projection.PlacementCommitVersion
                    != record.PlacementCommitVersion)
            {
                Retire(entry);
                return RuntimeInitialCreateResidenceCompletionStatus
                    .RejectedAuthority;
            }
        }
        else if (record.FullCellId != 0u)
        {
            Retire(entry);
            return RuntimeInitialCreateResidenceCompletionStatus
                .RejectedAuthority;
        }

        var adoption = new RuntimeInitialCreateResidenceAdoptionToken(
            token.Entity,
            token.LeaseId,
            token.LeaseId,
            token.SessionLifetimeVersion,
            Revision: 1UL);
        receipt = new RuntimeInitialCreateResidenceReceipt(
            token,
            lease.Route.TeleportHookPhase,
            projection,
            record.FullCellId,
            record.PlacementCommitVersion,
            adoption,
            lease.Continuations);
        _entries.Remove(token.Entity);
        _completed.Add(token.Entity, new CompletedEntry
        {
            Record = record,
            Lease = lease,
            Receipt = receipt,
            ExpectedPositionAuthorityVersion = token.PositionAuthorityVersion,
            ExpectedCreateIntegrationVersion = token.CreateIntegrationVersion,
            ExpectedFullCellId = receipt.FullCellId,
            ExpectedPlacementCommitVersion = receipt.PlacementCommitVersion,
        });
        return RuntimeInitialCreateResidenceCompletionStatus.Completed;
    }

    internal bool AdvanceExecutorBaseline(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeExecutorBaselineFields fields)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!token.IsValid
            || !_completed.TryGetValue(token.Entity, out CompletedEntry? entry)
            || !ReferenceEquals(entry.Record, record)
            || entry.Receipt.Token != token)
        {
            return false;
        }
        if ((fields & RuntimeExecutorBaselineFields.PositionAuthorityVersion) != 0)
            entry.ExpectedPositionAuthorityVersion = record.PositionAuthorityVersion;
        if ((fields & RuntimeExecutorBaselineFields.CreateIntegrationVersion) != 0)
            entry.ExpectedCreateIntegrationVersion = record.CreateIntegrationVersion;
        if ((fields & RuntimeExecutorBaselineFields.FullCellId) != 0)
            entry.ExpectedFullCellId = record.FullCellId;
        if ((fields & RuntimeExecutorBaselineFields.PlacementCommitVersion) != 0)
            entry.ExpectedPlacementCommitVersion = record.PlacementCommitVersion;
        return true;
    }

    internal bool AcknowledgeAdoption(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceAdoptionToken token)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!token.IsValid)
            return false;
        if (!_completed.TryGetValue(token.Entity, out CompletedEntry? current)
            || !ReferenceEquals(current.Record, record)
            || current.Receipt.Adoption != token)
        {
            return false;
        }
        if (!IsCompletedCurrent(current))
        {
            Retire(current);
            return false;
        }
        if (!current.Lease.Continuations.IsEmpty)
            return false;
        if (current.Lease.Route.PerformsSetPosition
            && !current.PlacementAdopted
            && !_setPosition.ConsumeAcknowledgedPlacement(
                current.Lease.Placement,
                current.Receipt.Projection))
        {
            return false;
        }
        return _completed.Remove(token.Entity);
    }

    internal bool TryConvertToCellessRoute(RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key
            || !_entries.TryGetValue(key, out Entry? entry)
            || !ReferenceEquals(entry.Record, record))
        {
            return false;
        }
        if (!IsCurrent(entry))
        {
            Retire(entry);
            return false;
        }
        RuntimeInitialCreateResidenceLease lease = entry.Lease;
        if (!lease.Route.PerformsSetPosition)
            return true;
        if (record.FullCellId != 0u)
            return false;
        RuntimePlacementCancellationReceipt cancellation =
            _setPosition.ForgetExactPlacement(lease.Placement);
        entry.Lease = lease with
        {
            Route = RuntimeAuthoritativePositionRouteClassifier
                .ToCellessCreateRoute(lease.Route),
            Placement = default,
        };
        _setPosition.PublishCancellation(cancellation);
        NotifyRetirement(key);
        return true;
    }

    internal bool Forget(
        RuntimeEntityRecord record,
        out RuntimeInitialCreateResidenceLease lease,
        out RuntimePlacementCancellationReceipt cancellation)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellation = default;
        if (record.Key is { } key
            && _entries.TryGetValue(key, out Entry? entry)
            && ReferenceEquals(entry.Record, record)
            && _entries.Remove(key))
        {
            lease = entry.Lease;
            cancellation = _setPosition.ForgetExactPlacement(
                lease.Placement);
            NotifyRetirement(key);
            return true;
        }
        if (record.Key is { } completedKey
            && _completed.TryGetValue(
                completedKey,
                out CompletedEntry? completed)
            && ReferenceEquals(completed.Record, record)
            && _completed.Remove(completedKey))
        {
            lease = completed.Lease;
            cancellation = _setPosition.ForgetExactPlacement(
                lease.Placement);
            NotifyRetirement(completedKey);
            return true;
        }
        lease = default;
        return false;
    }

    internal void Clear()
    {
        Entry[] active = _entries.Values.ToArray();
        CompletedEntry[] completed = _completed.Values.ToArray();
        var cancellations = new RuntimePlacementCancellationReceipt[
            active.Length + completed.Length];
        _entries.Clear();
        _completed.Clear();
        int cancellationCount = 0;
        foreach (Entry entry in active)
        {
            RuntimePlacementCancellationReceipt cancellation =
                _setPosition.ForgetExactPlacement(
                    entry.Lease.Placement);
            if (cancellation.IsValid)
                cancellations[cancellationCount++] = cancellation;
        }
        foreach (CompletedEntry entry in completed)
        {
            RuntimePlacementCancellationReceipt cancellation =
                _setPosition.ForgetExactPlacement(
                    entry.Lease.Placement);
            if (cancellation.IsValid)
                cancellations[cancellationCount++] = cancellation;
        }
        for (int index = 0; index < cancellationCount; index++)
        {
            _setPosition.PublishCancellation(cancellations[index]);
        }
        foreach (Entry entry in active)
            NotifyRetirement(entry.Lease.Token.Entity);
        foreach (CompletedEntry entry in completed)
            NotifyRetirement(entry.Receipt.Token.Entity);
    }

    internal RuntimeInitialCreateResidenceOwnershipSnapshot CaptureOwnership() =>
        new(_entries.Count, _completed.Count, _nextLeaseId);

    private bool IsCurrent(Entry entry)
    {
        RuntimeInitialCreateResidenceToken token = entry.Lease.Token;
        bool placementCurrent = !entry.Lease.Route.PerformsSetPosition
            || _setPosition.IsPlacementCompletionTracked(
                entry.Lease.Placement);
        return placementCurrent
            && _entities.IsCurrent(entry.Record)
            && entry.Record.Key == token.Entity
            && _entities.SessionLifetimeVersion
                == token.SessionLifetimeVersion
            && entry.Record.PositionAuthorityVersion
                == token.PositionAuthorityVersion
            && entry.Record.CreateIntegrationVersion
                == token.CreateIntegrationVersion
            && entry.Lease.Route.Authority.Generation
                == CurrentGeneration();
    }

    private RuntimeGenerationToken CurrentGeneration()
    {
        return _generation?.Invoke() ?? default;
    }

    private bool IsCompletedCurrent(CompletedEntry entry)
    {
        RuntimeInitialCreateResidenceReceipt receipt = entry.Receipt;
        return _entities.IsCurrent(entry.Record)
            && entry.Record.Key == receipt.Token.Entity
            && _entities.SessionLifetimeVersion
                == receipt.Token.SessionLifetimeVersion
            && entry.Record.PositionAuthorityVersion
                == entry.ExpectedPositionAuthorityVersion
            && entry.Record.CreateIntegrationVersion
                == entry.ExpectedCreateIntegrationVersion
            && entry.Record.FullCellId == entry.ExpectedFullCellId
            && entry.Record.PlacementCommitVersion
                == entry.ExpectedPlacementCommitVersion
            && entry.Lease.Route.Authority.Generation
                == CurrentGeneration()
            && receipt.Token.SessionLifetimeVersion
                == receipt.Adoption.SessionLifetimeVersion
            && receipt.Token.LeaseId == receipt.Adoption.LeaseId
            && (!entry.Lease.Route.PerformsSetPosition
                || entry.PlacementAdopted
                || _setPosition.IsPlacementCompletionTracked(
                    entry.Lease.Placement));
    }

    internal bool AdoptCompletedPlacement(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken token)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!token.IsValid
            || !_completed.TryGetValue(token.Entity, out CompletedEntry? entry)
            || !ReferenceEquals(entry.Record, record)
            || entry.Receipt.Token != token)
        {
            return false;
        }
        if (entry.PlacementAdopted)
            return IsCompletedCurrent(entry);
        if (!IsCompletedCurrent(entry))
        {
            Retire(entry);
            return false;
        }
        if (!entry.Lease.Route.PerformsSetPosition)
        {
            entry.PlacementAdopted = true;
            return true;
        }
        if (!_setPosition.ConsumeAcknowledgedPlacement(
                entry.Lease.Placement,
                entry.Receipt.Projection))
        {
            return false;
        }
        entry.PlacementAdopted = true;
        return true;
    }

    internal RuntimeInitialCreateResidenceExecutorReleaseStatus ConsumeExecuted(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceAdoptionToken token,
        ulong executedThroughSequence)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!token.IsValid
            || !_completed.TryGetValue(token.Entity, out CompletedEntry? entry)
            || !ReferenceEquals(entry.Record, record)
            || entry.Receipt.Adoption.Entity != token.Entity
            || entry.Receipt.Adoption.LeaseId != token.LeaseId)
        {
            return RuntimeInitialCreateResidenceExecutorReleaseStatus
                .RejectedToken;
        }
        if (!IsCompletedCurrent(entry))
        {
            Retire(entry);
            return RuntimeInitialCreateResidenceExecutorReleaseStatus
                .RejectedAuthority;
        }
        if (entry.Receipt.Adoption.Revision != token.Revision)
        {
            return RuntimeInitialCreateResidenceExecutorReleaseStatus.Revised;
        }
        if (!entry.PlacementAdopted && entry.Lease.Route.PerformsSetPosition)
        {
            throw new InvalidOperationException(
                "Executor release requires the initial placement to have been adopted first.");
        }
        if ((ulong)entry.Lease.Continuations.Length != executedThroughSequence)
        {
            return RuntimeInitialCreateResidenceExecutorReleaseStatus
                .RejectedAuthority;
        }
        return _completed.Remove(token.Entity)
            ? RuntimeInitialCreateResidenceExecutorReleaseStatus.Released
            : RuntimeInitialCreateResidenceExecutorReleaseStatus
                .RejectedAuthority;
    }

    private void Retire(Entry entry)
    {
        RuntimeEntityKey key = entry.Lease.Token.Entity;
        _entries.Remove(key);
        RuntimePlacementCancellationReceipt cancellation =
            _setPosition.ForgetExactPlacement(entry.Lease.Placement);
        _setPosition.PublishCancellation(cancellation);
        NotifyRetirement(key);
    }

    private void Retire(CompletedEntry entry)
    {
        RuntimeEntityKey key = entry.Receipt.Token.Entity;
        _completed.Remove(key);
        RuntimePlacementCancellationReceipt cancellation =
            _setPosition.ForgetExactPlacement(entry.Lease.Placement);
        _setPosition.PublishCancellation(cancellation);
        NotifyRetirement(key);
    }
}
