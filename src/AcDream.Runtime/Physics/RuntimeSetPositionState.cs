using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Physics;

internal enum RuntimeSetPositionOperationKind
{
    InitialLogin,
    LocalAuthoritative,
    RemoteAuthoritative,
    ProjectileAuthoritative,
}

internal enum RuntimeSetPositionStatus
{
    Rejected,
    DeferredCell,
    CommittedHostAcknowledgementPending,
    Committed,
    Cancelled,
}

internal enum RuntimeEntityPlacementStage
{
    AwaitingPreparation,
    AwaitingWithdrawalAcknowledgement,
    AwaitingCell,
    QuiescenceHeld,
    AwaitingFinalShadowPreparation,
    AwaitingCommitAcknowledgement,
    CancelledAwaitingAcknowledgement,
}

internal enum RuntimeEntityPlacementPreparationKind : byte
{
    LegacyDirect,
    AuthoredMover,
}

internal readonly record struct RuntimeEntityPlacementToken(
    ulong SessionLifetimeVersion,
    RuntimeEntityKey Entity,
    ulong PositionAuthorityVersion,
    ulong OperationId,
    RuntimeEntityPlacementPreparationKind PreparationKind)
{
    internal bool IsValid => OperationId != 0UL
        && Entity.LocalEntityId != 0u
        && PositionAuthorityVersion != 0UL;
}

public enum RuntimePlacementProjectionKind
{
    Withdraw,
    Place,
    Discard,
    ExecutorCompleted,
    WithdrawalRestored,
}

public readonly record struct RuntimePortalPlacementAuthority(
    bool Present,
    long RevealGeneration,
    ushort TeleportSequence,
    RuntimeWorldHostProjectionToken Projection)
{
    internal bool IsEmpty => !Present
        && RevealGeneration == 0
        && TeleportSequence == 0
        && Projection == default;
    internal bool IsValid => Present
        && RevealGeneration != 0
        && Projection.IsValid
        && Projection.Generation == RevealGeneration;
}

internal readonly record struct RuntimeSetPositionCommand(
    PhysicsSetPositionRequest Physics,
    RuntimeSetPositionOperationKind Kind,
    double GameTime,
    ulong ExpectedVelocityAuthorityVersion,
    float ShadowWorldOffsetX = 0f,
    float ShadowWorldOffsetY = 0f,
    RuntimePortalPlacementAuthority Portal = default);

internal readonly record struct RuntimeCollisionGenerationAuthority(
    uint LandblockId,
    ulong Generation);

internal readonly record struct RuntimeCollisionPrefixQuiescenceToken(
    ulong SessionLifetimeVersion,
    uint LandblockPrefix,
    ulong CollisionGeneration,
    ulong OperationId)
{
    internal bool IsValid => (LandblockPrefix & 0xFFFFu) == 0u
        && CollisionGeneration != 0UL
        && OperationId != 0UL;
}

internal readonly record struct RuntimeCollisionPrefixMutationPermission(
    RuntimeCollisionPrefixQuiescenceToken Quiescence,
    ImmutableArray<RuntimePlacementProjectionToken> Withdrawals)
{
    internal bool IsValid => Quiescence.IsValid;
}

internal readonly record struct RuntimeCollisionEvaluationAuthority(
    ulong CollisionWorldAuthority,
    ulong ShadowWorldAuthority,
    ClientObjectTable? ObjectTable,
    ulong ObjectTableBindingAuthority,
    ulong ObjectTableAuthority,
    ImmutableArray<RuntimeCollisionGenerationAuthority> Generations)
{
    internal bool IsValid => CollisionWorldAuthority != 0UL
        && !Generations.IsDefault;
}

internal readonly record struct RuntimeDormantSetPositionEvaluation(
    RuntimeEntityPlacementToken Placement,
    RuntimeSetPositionCommand Command,
    PhysicsSetPositionResult Result,
    RuntimeCollisionEvaluationAuthority CollisionAuthority)
{
    internal bool IsValid => Placement.IsValid
        && CollisionAuthority.IsValid;
}

internal enum RuntimeDormantSetPositionCommitStatus : byte
{
    None,
    AwaitingFinalShadowPreparation,
    Committed,
    DeferredCell,
    RejectedPlacement,
    RejectedAuthority,
}

internal sealed class PreparedDormantSetPositionCommit
{
    internal required RuntimeDormantSetPositionEvaluation Evaluation
        { get; init; }
    internal required RuntimeEntityKey Entity { get; init; }
    internal required ulong OperationId { get; init; }
    internal required ulong ExpectedProjectionSequence { get; init; }
    internal required ShadowObjectRegistry.PreparedSetPositionShadowCommit?
        Shadow { get; init; }
    internal required RuntimeCollisionReportingState
        .PreparedSetPositionCollisionBatch? Collision { get; init; }
    internal required RuntimePlacementProjectionSnapshot Projection
        { get; init; }
    internal required SortedDictionary<ulong, RuntimePlacementProjectionSnapshot>?
        PendingProjection { get; init; }
    internal required ulong DeferredCollisionGeneration { get; init; }
    internal required bool DeferredCollisionGenerationReady { get; init; }
    internal required List<RuntimeEntityKey>? DeferredBucket { get; init; }
    internal required bool DeferredBucketIsNew { get; init; }
}

internal sealed class PreparedDormantActivationFinalCommit
{
    internal required RuntimeEntityKey Entity { get; init; }
    internal required ulong OperationId { get; init; }
    internal required ulong ExpectedProjectionSequence { get; init; }
    internal required ShadowObjectRegistry.PreparedSetPositionShadowCommit
        Shadow { get; init; }
    internal required RuntimePlacementProjectionSnapshot Projection
        { get; init; }
    internal required SortedDictionary<ulong, RuntimePlacementProjectionSnapshot>
        PendingProjection { get; init; }
}

internal readonly record struct RuntimeDormantSetPositionCommitReceipt(
    RuntimeDormantSetPositionCommitStatus Status,
    RuntimeEntityKey Entity,
    ulong OperationId,
    RuntimePlacementProjectionSnapshot Projection,
    RuntimeCollisionReportingState.SetPositionCollisionBatchReceipt Collision,
    ShadowObjectRegistry.SetPositionShadowCommitReceipt Shadow,
    RuntimeCollisionEvaluationAuthority CollisionAuthority,
    ulong SourceVectorAuthorityVersion,
    bool HitGround,
    bool LeaveGround)
{
    internal bool IsCommitted => Status
        is RuntimeDormantSetPositionCommitStatus.Committed;
}

public readonly record struct RuntimePlacementProjectionToken(
    ulong Sequence,
    ulong Revision,
    RuntimeEntityKey Entity,
    ulong PositionAuthorityVersion,
    ulong SpatialAuthorityVersion,
    ulong PlacementCommitVersion,
    ulong SessionLifetimeVersion,
    uint ExactCellId,
    ulong CollisionGeneration,
    RuntimePortalPlacementAuthority Portal)
{
    internal bool IsValid => Sequence != 0
        && Entity.LocalEntityId != 0u
        && PositionAuthorityVersion != 0UL;
}

public readonly record struct RuntimePlacementProjectionSnapshot(
    RuntimePlacementProjectionToken Token,
    RuntimePlacementProjectionKind Kind,
    Vector3 WorldPosition,
    Quaternion Orientation,
    Vector3 CellLocalPosition,
    bool InContact,
    bool OnWalkable);

internal readonly record struct RuntimeSetPositionOutcome(
    RuntimeSetPositionStatus Status,
    PhysicsSetPositionError Error,
    PhysicsResidenceDisposition Residence,
    uint ExactCellId,
    RuntimePlacementProjectionToken Projection)
{
    internal bool Accepted => Error == PhysicsSetPositionError.Ok;
}

internal readonly record struct RuntimePlacementCancellationReceipt(
    RuntimePlacementProjectionSnapshot Projection)
{
    internal bool IsValid => Projection.Kind
            is RuntimePlacementProjectionKind.Discard
        && Projection.Token.IsValid;
}

internal readonly record struct RuntimeSetPositionOwnershipSnapshot(
    int ActiveOperationCount,
    int AwaitingPreparationCount,
    int DeferredCellCount,
    int PendingProjectionAcknowledgementCount,
    int LostDeadlineCount,
    int LostDeadlineNodeCount,
    int LostDeadlineIndexCount,
    int ExpiredLostCellCount,
    int ExpiredLostCellIndexCount,
    int DeferredBucketCount,
    int DeferredBucketOrderCount,
    int UnboundDeferredCellCount,
    int UnboundDeferredCellOrderCount,
    int PreparedMoverCount,
    int MoverPreparationAuthorityCount,
    int PlacementCompletionWatchCount,
    int AcknowledgedPlacementCompletionCount,
    int CollisionPrefixQuiescenceCount,
    int PendingQuiescenceProjectionCount,
    int PooledOperationCount,
    int ParkedAwaitingSetupCollisionCount = 0,
    int ParkedAwaitingWorldFrameCount = 0)
{
    internal int ParkedPlacementCount =>
        ParkedAwaitingSetupCollisionCount + ParkedAwaitingWorldFrameCount;

    internal bool IndexesConsistent =>
        LostDeadlineCount == LostDeadlineNodeCount
        && LostDeadlineCount == LostDeadlineIndexCount
        && ExpiredLostCellCount == ExpiredLostCellIndexCount
        && DeferredBucketCount == DeferredBucketOrderCount
        && UnboundDeferredCellCount == UnboundDeferredCellOrderCount
        && MoverPreparationAuthorityCount <= ActiveOperationCount;

    internal bool IsConverged => ActiveOperationCount == 0
        && AwaitingPreparationCount == 0
        && DeferredCellCount == 0
        && PendingProjectionAcknowledgementCount == 0
        && LostDeadlineCount == 0
        && LostDeadlineNodeCount == 0
        && LostDeadlineIndexCount == 0
        && ExpiredLostCellCount == 0
        && ExpiredLostCellIndexCount == 0
        && DeferredBucketCount == 0
        && DeferredBucketOrderCount == 0
        && UnboundDeferredCellCount == 0
        && UnboundDeferredCellOrderCount == 0
        && PreparedMoverCount == 0
        && MoverPreparationAuthorityCount == 0
        && PlacementCompletionWatchCount == 0
        && AcknowledgedPlacementCompletionCount == 0
        && CollisionPrefixQuiescenceCount == 0
        && PendingQuiescenceProjectionCount == 0;
}

internal sealed class RuntimeSetPositionState : IDisposable
{
    private const uint MaxSynchronousScatterAttempts = 64u;

    private readonly record struct CellGenerationKey(
        uint CellId,
        uint CollisionPrefix,
        ulong CollisionGeneration);

    private readonly record struct UnboundCellKey(
        uint CellId,
        uint CollisionPrefix);

    private readonly record struct MoverPreparationAuthority(
        ulong OperationId,
        CreateObject.ServerPosition AcceptedPosition,
        uint SetupTableId,
        ulong PositionAuthorityVersion,
        ulong VelocityAuthorityVersion,
        ulong StateAuthorityVersion,
        ulong VectorAuthorityVersion,
        ulong ObjDescAuthorityVersion,
        ulong CreateIntegrationVersion,
        ulong PhysicsStateMutationVersion,
        bool Prepared,
        RuntimeSetPositionCommand PreparedCommand);

    private sealed class Operation
    {
        internal RuntimeEntityRecord Record { get; set; } = null!;
        internal PhysicsBody? Body { get; set; }
        internal RuntimeEntityPlacementToken Token { get; set; }
        internal RuntimeEntityKey Key { get; set; }
        internal ulong PositionAuthorityVersion { get; set; }
        internal ulong SessionLifetimeVersion { get; set; }
        internal ulong SourceSpatialAuthorityVersion { get; set; }
        internal ulong SourceVelocityAuthorityVersion { get; set; }
        internal bool PreviousContact { get; set; }
        internal bool PreviousOnWalkable { get; set; }
        internal RuntimeSetPositionCommand Command { get; set; }
        internal PhysicsSetPositionResult Result { get; set; }
        internal ulong SpatialAuthorityVersion { get; set; }
        internal ulong PlacementCommitVersion { get; set; }
        internal uint ExactCellId { get; set; }
        internal ulong CollisionGeneration { get; set; }
        internal uint CollisionPrefix { get; set; }
        internal bool WithdrawalAcknowledged { get; set; }
        internal bool CollisionGenerationReady { get; set; }
        internal bool CollisionQuiescenceHeld { get; set; }
        internal ulong ProjectionSequence { get; set; }
        internal bool WakeableLostCell { get; set; }
        internal RuntimeEntityPlacementStage Stage { get; set; }
        internal RuntimeSetPositionOperationKind Kind { get; set; }
        internal RuntimePortalPlacementAuthority Portal { get; set; }
        internal bool RequiresPreparation { get; set; }
        internal bool Expired { get; set; }
        internal List<RuntimeEntityKey>? LostFamilyKeys { get; set; }
        internal bool InheritedLostDeadline { get; set; }
        internal bool EnteringWorldFromCelllessResidence { get; set; }
        internal bool DormantLocalActivation { get; set; }

        internal ParkWithdrawal ParkWithdrawal { get; set; }

        internal RuntimeSetPositionParkReason ParkReason { get; set; }
        internal RuntimeSetPositionCommand? PreparedCommandAwaitingWithdrawalAck
        {
            get;
            set;
        }

        internal bool InPool { get; set; }

        internal void ResetAllFieldsToDefault()
        {
            Record = null!;
            Body = null;
            Token = default;
            Key = default;
            PositionAuthorityVersion = 0UL;
            SessionLifetimeVersion = 0UL;
            SourceSpatialAuthorityVersion = 0UL;
            SourceVelocityAuthorityVersion = 0UL;
            PreviousContact = false;
            PreviousOnWalkable = false;
            Command = default;
            Result = default;
            SpatialAuthorityVersion = 0UL;
            PlacementCommitVersion = 0UL;
            ExactCellId = 0u;
            CollisionGeneration = 0UL;
            CollisionPrefix = 0u;
            WithdrawalAcknowledged = false;
            CollisionGenerationReady = false;
            CollisionQuiescenceHeld = false;
            ProjectionSequence = 0UL;
            WakeableLostCell = false;
            Stage = default;
            Kind = default;
            Portal = default;
            RequiresPreparation = false;
            Expired = false;
            LostFamilyKeys = null;
            InheritedLostDeadline = false;
            EnteringWorldFromCelllessResidence = false;
            DormantLocalActivation = false;
            ParkReason = RuntimeSetPositionParkReason.None;
            PreparedCommandAwaitingWithdrawalAck = null;
            ParkWithdrawal = default;
            InPool = false;
        }
    }

    private readonly record struct ParkWithdrawal(
        bool Captured,
        bool InWorld,
        TransientStateFlags TransientState,
        bool ClockActive);

    private sealed class CollisionPrefixQuiescence
    {
        internal required RuntimeCollisionPrefixQuiescenceToken Token
            { get; init; }
        internal required bool IncludeOutdoorCells { get; init; }
        internal required ulong ProjectionBarrierSequence { get; init; }
        internal SortedDictionary<ulong, RuntimePlacementProjectionToken>
            PendingWithdrawals { get; } = [];
        internal SortedDictionary<ulong, RuntimePlacementProjectionToken>
            PendingRestorePlacements { get; } = [];
        internal List<RuntimePlacementProjectionToken> RetainedWithdrawals
            { get; } = [];
        internal bool ResidentsParked { get; set; }
        internal bool PermissionIssued { get; set; }
        internal bool ReleaseInProgress { get; set; }
        internal ulong ReleaseGeneration { get; set; }
        internal bool ReleaseGenerationReady { get; set; }
    }

    private sealed class ContactCommitGuard(
        RuntimeSetPositionState owner,
        ulong positionAuthorityVersion,
        ulong spatialAuthorityVersion,
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong placementCommitVersion,
        uint fullCellId)
    {
        internal bool IsCurrent() =>
            owner.IsCanonicalPlacementCommitCurrent(
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                placementCommitVersion,
                fullCellId,
                requireSpatialRoot: false);
    }

    private readonly RuntimePhysicsState _physics;
    private readonly RuntimeEntityDirectory _entities;
    private readonly Dictionary<RuntimeEntityKey, Operation> _operations = [];
    private readonly Dictionary<CellGenerationKey, List<RuntimeEntityKey>>
        _deferredByCellGeneration = [];
    private SortedDictionary<ulong, RuntimePlacementProjectionSnapshot>
        _pendingProjection = [];
    private readonly List<List<RuntimePlacementProjectionSnapshot>>
        _pendingProjectionRetryScratchByDepth = [new()];
    private int _pendingProjectionRetryDepth;
    private readonly List<CellGenerationKey> _deferredBucketOrder = [];
    private readonly Dictionary<UnboundCellKey, List<RuntimeEntityKey>>
        _unboundDeferredByCell = [];
    private readonly List<UnboundCellKey> _unboundDeferredCellOrder = [];
    private readonly Dictionary<RuntimeEntityKey, double> _lostDeadlines = [];
    private readonly List<LostDeadlineEntry> _lostDeadlineNodes = [];
    private readonly Dictionary<RuntimeEntityKey, int>
        _lostDeadlineNodeIndex = [];
    private readonly Dictionary<RuntimeEntityKey, PhysicsSetPositionRequest>
        _preparedMovers = [];
    private readonly Dictionary<RuntimeEntityKey, MoverPreparationAuthority>
        _moverPreparationAuthorities = [];
    private readonly HashSet<RuntimeEntityPlacementToken>
        _placementCompletionWatches = [];
    private readonly Dictionary<RuntimeEntityPlacementToken,
        RuntimePlacementProjectionToken> _acknowledgedPlacementCompletions = [];
    private readonly Dictionary<uint, CollisionPrefixQuiescence>
        _collisionPrefixQuiescence = [];
    private readonly LinkedList<RuntimeEntityKey> _expiredLostCells = [];
    private readonly Dictionary<RuntimeEntityKey, LinkedListNode<RuntimeEntityKey>>
        _expiredLostCellNodes = [];
    private ulong _nextProjectionSequence;
    private ulong _nextOperationId;
    private ulong _nextCollisionPrefixQuiescenceOperationId;
    private ulong _nextLostDeadlineSequence;
    private RuntimeEntityObjectEventStream? _events;
    private Action<RuntimeEntityKey, ulong>? _executorCompletionAcknowledged;
    private bool _disposed;

    private readonly record struct LostDeadlineEntry(
        RuntimeEntityKey Key,
        double Deadline,
        ulong Sequence);

    private readonly record struct CollisionCallbackContext(
        RuntimeEntityRecord Record,
        ulong PositionAuthorityVersion,
        ulong SpatialAuthorityVersion,
        ulong VelocityAuthorityVersion,
        double GameTime,
        bool PreviousContact,
        bool PreviousOnWalkable);

    private readonly Stack<CollisionCallbackContext> _collisionCallbackContexts
        = new(4);
    private readonly Func<PhysicsSetPositionCollisionReport, bool>
        _handleSetPositionCollisionsCallback;

    private const int MaxPooledOperations = 64;
    private readonly Stack<Operation> _operationPool = new();

    internal RuntimeSetPositionState(
        RuntimePhysicsState physics,
        RuntimeEntityDirectory entities)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _handleSetPositionCollisionsCallback =
            HandleSetPositionCollisionsCallback;
    }

    private bool HandleSetPositionCollisionsCallback(
        PhysicsSetPositionCollisionReport report)
    {
        CollisionCallbackContext context = _collisionCallbackContexts.Peek();
        return _physics.HandleSetPositionCollisions(
            context.Record,
            context.PositionAuthorityVersion,
            context.SpatialAuthorityVersion,
            context.VelocityAuthorityVersion,
            context.GameTime,
            context.PreviousContact,
            context.PreviousOnWalkable,
            report);
    }

    private Operation RentOperation()
    {
        if (_operationPool.Count == 0)
            return new Operation();
        Operation pooled = _operationPool.Pop();
        pooled.ResetAllFieldsToDefault();
        return pooled;
    }

    private void RetireOperationToPool(Operation operation)
    {
        if (operation.InPool)
        {
            throw new InvalidOperationException(
                "Operation was already retired to the pool - a double-retire " +
                "without an intervening rent would duplicate it in the pool " +
                "stack.");
        }
        if (_operationPool.Count >= MaxPooledOperations)
            return;
        operation.InPool = true;
        _operationPool.Push(operation);
    }

    internal RuntimeSetPositionOwnershipSnapshot CaptureOwnership()
    {
        int deferred = 0;
        int awaitingPreparation = 0;
        int parkedAwaitingSetup = 0;
        int parkedAwaitingWorldFrame = 0;
        foreach (Operation operation in _operations.Values)
        {
            if (operation.Stage
                is RuntimeEntityPlacementStage.AwaitingPreparation)
            {
                awaitingPreparation++;
            }
            if (operation.WakeableLostCell)
                deferred++;
            switch (operation.ParkReason)
            {
                case RuntimeSetPositionParkReason.AwaitingSetupCollision:
                    parkedAwaitingSetup++;
                    break;
                case RuntimeSetPositionParkReason.AwaitingWorldFrame:
                    parkedAwaitingWorldFrame++;
                    break;
            }
        }
        int pendingQuiescenceProjections = 0;
        foreach (CollisionPrefixQuiescence quiescence
                 in _collisionPrefixQuiescence.Values)
        {
            pendingQuiescenceProjections +=
                quiescence.PendingWithdrawals.Count
                + quiescence.PendingRestorePlacements.Count;
        }
        return new RuntimeSetPositionOwnershipSnapshot(
            _operations.Count,
            awaitingPreparation,
            deferred,
            _pendingProjection.Count,
            _lostDeadlines.Count,
            _lostDeadlineNodes.Count,
            _lostDeadlineNodeIndex.Count,
            _expiredLostCells.Count,
            _expiredLostCellNodes.Count,
            _deferredByCellGeneration.Count,
            _deferredBucketOrder.Count,
            _unboundDeferredByCell.Count,
            _unboundDeferredCellOrder.Count,
            _preparedMovers.Count,
            _moverPreparationAuthorities.Count,
            _placementCompletionWatches.Count,
            _acknowledgedPlacementCompletions.Count,
            _collisionPrefixQuiescence.Count,
            pendingQuiescenceProjections,
            _operationPool.Count,
            parkedAwaitingSetup,
            parkedAwaitingWorldFrame);
    }

    internal int PendingProjectionCount => _pendingProjection.Count;
    internal bool CanBeginAuthoredPlacementSequence =>
        _nextOperationId != ulong.MaxValue;

    internal bool IsCollisionPrefixQuiescing(uint landblockId) =>
        _collisionPrefixQuiescence.ContainsKey(
            landblockId & 0xFFFF0000u);

    internal RuntimeCollisionPrefixQuiescenceToken
        BeginCollisionPrefixQuiescence(
            uint landblockId,
            ulong collisionGeneration,
            bool includeOutdoorCells)
    {
        EnsureNotDisposed();
        if (collisionGeneration == 0UL)
            throw new ArgumentOutOfRangeException(nameof(collisionGeneration));
        if (landblockId == 0u)
            throw new ArgumentOutOfRangeException(nameof(landblockId));
        uint prefix = landblockId & 0xFFFF0000u;

        if (_collisionPrefixQuiescence.TryGetValue(
                prefix,
                out CollisionPrefixQuiescence? active)
            && active.ReleaseInProgress)
        {
            throw new InvalidOperationException(
                $"Collision quiescence 0x{prefix:X8}/{active.Token.OperationId} is releasing its retained residents.");
        }
        _collisionPrefixQuiescence.Remove(
            prefix,
            out CollisionPrefixQuiescence? superseded);

        var token = new RuntimeCollisionPrefixQuiescenceToken(
            _entities.SessionLifetimeVersion,
            prefix,
            collisionGeneration,
            checked(++_nextCollisionPrefixQuiescenceOperationId));
        var replacement = new CollisionPrefixQuiescence
        {
            Token = token,
            IncludeOutdoorCells = includeOutdoorCells,
            ProjectionBarrierSequence = superseded is null
                ? _nextProjectionSequence
                : Math.Max(
                    _nextProjectionSequence,
                    superseded.ProjectionBarrierSequence),
            ResidentsParked = superseded?.ResidentsParked ?? false,
        };
        if (superseded is not null)
        {
            foreach ((ulong sequence, RuntimePlacementProjectionToken pending)
                     in superseded.PendingWithdrawals)
                replacement.PendingWithdrawals.Add(sequence, pending);
            replacement.RetainedWithdrawals.AddRange(
                superseded.RetainedWithdrawals);
        }
        _collisionPrefixQuiescence.Add(prefix, replacement);
        if (superseded is not null)
        {
            RebindQuiescedDeferredOperations(
                superseded.Token,
                collisionGeneration,
                ready: false);
        }
        _physics.AdvanceCollisionQuiescenceAuthority();
        return token;
    }

    internal bool TryAcquireCollisionPrefixMutationPermission(
        in RuntimeCollisionPrefixQuiescenceToken token,
        out RuntimeCollisionPrefixMutationPermission permission)
    {
        EnsureNotDisposed();
        permission = default;
        if (!TryGetCurrentQuiescence(token, out CollisionPrefixQuiescence? state))
            return false;
        CollisionPrefixQuiescence current = state!;

        if (HasPendingProjectionThrough(current.ProjectionBarrierSequence))
            return false;
        CancelUnpreparedPrefixPlacementDebt(current);
        if (!TryGetCurrentQuiescence(
                token,
                out CollisionPrefixQuiescence? afterCancellation)
            || !ReferenceEquals(afterCancellation, current))
        {
            return false;
        }
        if (HasOldPrefixPlacementDebt(current))
            return false;

        if (!current.ResidentsParked)
        {
            ParkCollisionResidentsForQuiescence(current);
            if (!TryGetCurrentQuiescence(
                    token,
                    out CollisionPrefixQuiescence? afterParking)
                || !ReferenceEquals(afterParking, current))
            {
                return false;
            }
            current.ResidentsParked = true;
        }
        else if (HasAffectedCollisionResident(
                     token.LandblockPrefix,
                     current.IncludeOutdoorCells))
        {
            ParkCollisionResidentsForQuiescence(current);
            if (!TryGetCurrentQuiescence(
                    token,
                    out CollisionPrefixQuiescence? afterAdditionalParking)
                || !ReferenceEquals(afterAdditionalParking, current))
            {
                return false;
            }
        }

        RemoveRetiredQuiescenceWithdrawals(current);
        if (current.PendingWithdrawals.Count != 0
            || HasAffectedCollisionResident(
                token.LandblockPrefix,
                current.IncludeOutdoorCells)
            || HasOldPrefixPlacementDebt(current)
            || HasCollisionDispatchDebt())
        {
            return false;
        }

        current.PermissionIssued = true;
        permission = new RuntimeCollisionPrefixMutationPermission(
            current.Token,
            current.RetainedWithdrawals.Count == 0
                ? default
                : current.RetainedWithdrawals.ToImmutableArray());
        return true;
    }

    internal bool IsCollisionPrefixMutationPermissionCurrent(
        in RuntimeCollisionPrefixMutationPermission permission)
    {
        EnsureNotDisposed();
        return permission.IsValid
            && TryGetCurrentQuiescence(
                permission.Quiescence,
                out CollisionPrefixQuiescence? state)
            && state!.PermissionIssued
            && state.PendingWithdrawals.Count == 0
            && !HasAffectedCollisionResident(
                state.Token.LandblockPrefix,
                state.IncludeOutdoorCells)
            && !HasOldPrefixPlacementDebt(state)
            && !HasCollisionDispatchDebt();
    }

    internal bool CancelCollisionPrefixQuiescence(
        in RuntimeCollisionPrefixQuiescenceToken token,
        ulong successorGeneration = 0UL,
        bool successorReady = false)
    {
        EnsureNotDisposed();
        if (!TryGetCurrentQuiescence(token, out CollisionPrefixQuiescence? state))
            return false;

        CollisionPrefixQuiescence current = state!;
        if (!current.ResidentsParked
            && current.PendingWithdrawals.Count == 0
            && current.PendingRestorePlacements.Count == 0
            && !HasQuiescedDeferredOperations(token.LandblockPrefix))
        {
            bool removedBeforePark = _collisionPrefixQuiescence.Remove(
                token.LandblockPrefix);
            if (removedBeforePark)
                _physics.AdvanceCollisionQuiescenceAuthority();
            return removedBeforePark;
        }

        if (successorGeneration == 0UL)
            return false;

        return AdvanceCollisionPrefixRelease(
            token,
            successorGeneration,
            successorReady,
            requireMutationPermission: false);
    }

    internal bool CancelCollisionPrefixQuiescenceToUnavailable(
        in RuntimeCollisionPrefixQuiescenceToken token)
    {
        EnsureNotDisposed();
        return AdvanceCollisionPrefixRelease(
            token,
            generation: 0UL,
            ready: false,
            requireMutationPermission: false);
    }

    internal bool ReleaseCollisionPrefixAfterMutation(
        in RuntimeCollisionPrefixQuiescenceToken token,
        ulong activeGeneration,
        bool ready)
    {
        EnsureNotDisposed();
        return AdvanceCollisionPrefixRelease(
            token,
            activeGeneration,
            ready,
            requireMutationPermission: true);
    }

    private bool AdvanceCollisionPrefixRelease(
        in RuntimeCollisionPrefixQuiescenceToken token,
        ulong generation,
        bool ready,
        bool requireMutationPermission)
    {
        if ((generation == 0UL && ready)
            || !TryGetCurrentQuiescence(
                token,
                out CollisionPrefixQuiescence? state))
        {
            return false;
        }

        CollisionPrefixQuiescence current = state!;
        if (current.PendingWithdrawals.Count != 0)
            return false;
        if (requireMutationPermission
            && !current.PermissionIssued
            && !current.ReleaseInProgress)
        {
            return false;
        }
        if (!current.ReleaseInProgress)
        {
            current.ReleaseInProgress = true;
            current.ReleaseGeneration = generation;
            current.ReleaseGenerationReady = ready;
            current.PermissionIssued = false;
        }
        else if (current.ReleaseGeneration != generation
            || current.ReleaseGenerationReady != ready)
        {
            return false;
        }

        if (_operations.Count != 0)
        {
            RebindQuiescedDeferredOperations(
                token,
                ready ? generation : 0UL,
                ready,
                releaseUnavailable: !ready);
        }

        if (current.PendingRestorePlacements.Count != 0
            || HasQuiescedDeferredOperations(token.LandblockPrefix))
        {
            return false;
        }
        bool removed = _collisionPrefixQuiescence.Remove(
            token.LandblockPrefix);
        if (removed)
            _physics.AdvanceCollisionQuiescenceAuthority();
        return removed;
    }

    internal void BindEventStream(RuntimeEntityObjectEventStream events)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(events);
        if (_events is not null)
            throw new InvalidOperationException(
                "The Runtime placement event stream is already bound.");
        _events = events;
    }

    internal void RetryPendingProjections()
    {
        EnsureNotDisposed();
        int depth = _pendingProjectionRetryDepth;
        if (depth == _pendingProjectionRetryScratchByDepth.Count)
        {
            _pendingProjectionRetryScratchByDepth.Add([]);
        }
        List<RuntimePlacementProjectionSnapshot> snapshot =
            _pendingProjectionRetryScratchByDepth[depth];
        _pendingProjectionRetryDepth = depth + 1;
        try
        {
            snapshot.Clear();
            foreach (KeyValuePair<ulong, RuntimePlacementProjectionSnapshot>
                     entry in _pendingProjection)
            {
                snapshot.Add(entry.Value);
            }
            for (int index = 0; index < snapshot.Count; index++)
            {
                RuntimePlacementProjectionSnapshot projection = snapshot[index];
                if (_pendingProjection.TryGetValue(
                        projection.Token.Sequence,
                        out RuntimePlacementProjectionSnapshot current)
                    && current == projection)
                {
                    PublishPlacement(projection);
                }
            }
        }
        finally
        {
            snapshot.Clear();
            _pendingProjectionRetryDepth = depth;
        }
    }

    internal RuntimePlacementProjectionToken PublishExecutorCompletion(
        RuntimeEntityRecord record,
        Action<RuntimePlacementProjectionToken>? beforePublish = null,
        RuntimePortalPlacementAuthority portal = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return default;

        PhysicsBody? body = record.PhysicsBody;
        ulong sequence = checked(++_nextProjectionSequence);
        var token = new RuntimePlacementProjectionToken(
            sequence,
            Revision: 1UL,
            key,
            record.PositionAuthorityVersion,
            record.SpatialAuthorityVersion,
            record.PlacementCommitVersion,
            _entities.SessionLifetimeVersion,
            record.FullCellId,
            _physics.ExpectedCollisionGeneration(record.FullCellId),
            portal);
        var snapshot = new RuntimePlacementProjectionSnapshot(
            token,
            RuntimePlacementProjectionKind.ExecutorCompleted,
            body?.Position ?? Vector3.Zero,
            body?.Orientation ?? Quaternion.Identity,
            body?.CellPosition.Frame.Origin ?? Vector3.Zero,
            body?.InContact ?? false,
            body?.OnWalkable ?? false);
        _pendingProjection.Add(sequence, snapshot);
        beforePublish?.Invoke(token);
        PublishPlacement(snapshot);
        return token;
    }

    private void PublishWithdrawalRestoration(RuntimeEntityRecord record)
    {
        if (record.Key is not { } key)
            return;
        PhysicsBody? body = record.PhysicsBody;
        ulong sequence = checked(++_nextProjectionSequence);
        var token = new RuntimePlacementProjectionToken(
            sequence,
            Revision: 1UL,
            key,
            record.PositionAuthorityVersion,
            record.SpatialAuthorityVersion,
            record.PlacementCommitVersion,
            _entities.SessionLifetimeVersion,
            record.FullCellId,
            _physics.ExpectedCollisionGeneration(record.FullCellId),
            Portal: default);
        var snapshot = new RuntimePlacementProjectionSnapshot(
            token,
            RuntimePlacementProjectionKind.WithdrawalRestored,
            body?.Position ?? Vector3.Zero,
            body?.Orientation ?? Quaternion.Identity,
            body?.CellPosition.Frame.Origin ?? Vector3.Zero,
            body?.InContact ?? false,
            body?.OnWalkable ?? false);
        _pendingProjection.Add(sequence, snapshot);
        PublishPlacement(snapshot);
    }

    internal void BindExecutorCompletionAcknowledgement(
        Action<RuntimeEntityKey, ulong> acknowledged)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);
        if (_executorCompletionAcknowledged is not null)
        {
            throw new InvalidOperationException(
                "The executor-completion acknowledgement notification is already bound.");
        }
        _executorCompletionAcknowledged = acknowledged;
    }

    internal void ResetSession()
    {
        EnsureNotDisposed();
        ClearOwnedState();
    }

    internal RuntimeSetPositionOutcome Apply(
        RuntimeEntityRecord record,
        ulong expectedPositionAuthorityVersion,
        in RuntimeSetPositionCommand command)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        RuntimeEntityPlacementToken token = BeginAcceptedPlacementCore(
            record,
            expectedPositionAuthorityVersion,
            command.Kind,
            command.Portal,
            captureMoverPreparationAuthority: false);
        if (!token.IsValid)
        {
            return Rejected(command.Physics);
        }

        return SubmitPreparedPlacementCore(
            token,
            command,
            allowDirectUnsealed: true);
    }

    internal RuntimeEntityPlacementToken BeginAcceptedPlacement(
        RuntimeEntityRecord record,
        ulong expectedPositionAuthorityVersion,
        RuntimeSetPositionOperationKind kind,
        RuntimePortalPlacementAuthority portal = default) =>
        BeginAcceptedPlacementCore(
            record,
            expectedPositionAuthorityVersion,
            kind,
            portal,
            captureMoverPreparationAuthority: false);

    internal RuntimeEntityPlacementToken BeginAuthoredPlacement(
        RuntimeEntityRecord record,
        ulong expectedPositionAuthorityVersion,
        RuntimeSetPositionOperationKind kind,
        RuntimePortalPlacementAuthority portal = default) =>
        BeginAcceptedPlacementCore(
            record,
            expectedPositionAuthorityVersion,
            kind,
            portal,
            captureMoverPreparationAuthority: true);

    internal bool IsPlacementCurrent(
        in RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        return token.IsValid
            && _operations.TryGetValue(token.Entity, out Operation? operation)
            && operation.Token == token
            && IsCurrent(operation);
    }

    internal bool WatchPlacementCompletion(
        in RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        return IsPlacementCurrent(token)
            && _placementCompletionWatches.Add(token);
    }

    internal bool IsPlacementCompletionTracked(
        in RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        return token.IsValid
            && (IsPlacementCurrent(token)
                && _placementCompletionWatches.Contains(token)
                || _acknowledgedPlacementCompletions.ContainsKey(token));
    }

    internal bool TryPeekAcknowledgedPlacement(
        in RuntimeEntityPlacementToken token,
        out RuntimePlacementProjectionToken projection)
    {
        EnsureNotDisposed();
        if (token.IsValid
            && _acknowledgedPlacementCompletions.TryGetValue(
                token,
                out projection))
        {
            return true;
        }
        projection = default;
        return false;
    }

    internal bool ConsumeAcknowledgedPlacement(
        in RuntimeEntityPlacementToken token,
        in RuntimePlacementProjectionToken expected)
    {
        EnsureNotDisposed();
        return token.IsValid
            && _acknowledgedPlacementCompletions.TryGetValue(
                token,
                out RuntimePlacementProjectionToken current)
            && current == expected
            && _acknowledgedPlacementCompletions.Remove(token);
    }

    internal void ForgetPlacementCompletion(
        in RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        ForgetPlacementCompletionCore(token);
    }

    internal RuntimePlacementCancellationReceipt ForgetExactPlacement(
        in RuntimeEntityPlacementToken token,
        bool restoreCancelledPark = false)
    {
        EnsureNotDisposed();
        ForgetPlacementCompletionCore(token);
        if (!token.IsValid
            || !_operations.TryGetValue(token.Entity, out Operation? operation)
            || operation.Token != token)
        {
            return default;
        }
        return CancelCore(
            token.Entity,
            token,
            restoreCancelledPark: restoreCancelledPark);
    }

    internal RuntimeEntityPlacementToken TryBeginExclusiveAuthoredPlacement(
        RuntimeEntityRecord record,
        ulong expectedPositionAuthorityVersion,
        RuntimeSetPositionOperationKind kind,
        RuntimePortalPlacementAuthority portal = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key
            || _operations.ContainsKey(key)
            || HasRetainedCompletion(key))
        {
            return default;
        }
        return BeginAcceptedPlacementCore(
            record,
            expectedPositionAuthorityVersion,
            kind,
            portal,
            captureMoverPreparationAuthority: true);
    }

    internal void PrepareDormantLocalActivationOwnership(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        if (!token.IsValid
            || record.Key != token.Entity
            || !_operations.TryGetValue(token.Entity, out Operation? operation)
            || operation.Token != token
            || operation.Stage is not RuntimeEntityPlacementStage
                .AwaitingPreparation
            || !ReferenceEquals(operation.Record, record)
            // B2 dependency (C4 route 4b-2): this `is not null` is what makes
            // DormantLocalActivation and "the record has a body" mutually
            // exclusive, which is the step
            // RuntimeRemotePlacementDriveController's pre-engine-Rejected
            // argument uses to dismiss the AwaitingCell stage divergence
            // between PrepareMover and SubmitPreparedPlacementCore.
            || record.PhysicsBody is not null
            || !IsCurrent(operation)
            || body.InWorld
            || (body.TransientState & TransientStateFlags.Active) != 0)
        {
            throw new InvalidOperationException(
                "Dormant local activation must bind to the exact current placement owner.");
        }

        operation.Body = body;
        operation.DormantLocalActivation = true;
    }

    private RuntimeEntityPlacementToken BeginAcceptedPlacementCore(
        RuntimeEntityRecord record,
        ulong expectedPositionAuthorityVersion,
        RuntimeSetPositionOperationKind kind,
        RuntimePortalPlacementAuthority portal,
        bool captureMoverPreparationAuthority)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        CreateObject.ServerPosition? acceptedPosition =
            record.Snapshot.Physics?.Position ?? record.Snapshot.Position;
        if (record.Key is not { } key
            || !_entities.IsCurrent(record)
            || HasRetainedCompletion(key)
            || record.PositionAuthorityVersion
                != expectedPositionAuthorityVersion
            || (captureMoverPreparationAuthority
                && acceptedPosition is null)
            || !(portal.IsEmpty
                || (portal.IsValid
                    && kind is RuntimeSetPositionOperationKind
                        .LocalAuthoritative
                    && acceptedPosition is { } portalPosition
                    && portal.Projection.DestinationCell
                        == portalPosition.LandblockId)))
        {
            return default;
        }

        var token = new RuntimeEntityPlacementToken(
            _entities.SessionLifetimeVersion,
            key,
            expectedPositionAuthorityVersion,
            checked(++_nextOperationId),
            captureMoverPreparationAuthority
                ? RuntimeEntityPlacementPreparationKind.AuthoredMover
                : RuntimeEntityPlacementPreparationKind.LegacyDirect);
        List<RuntimeEntityKey>? inheritedLostFamily = null;
        RuntimePlacementProjectionSnapshot? inheritedWithdrawal = null;
        bool inheritedWithdrawalAcknowledged = false;
        PhysicsSetPositionResult inheritedResult = default;
        uint inheritedExactCellId = 0u;
        ulong inheritedCollisionGeneration = 0UL;
        if (_operations.TryGetValue(key, out Operation? displaced)
            && (displaced.WakeableLostCell
                || displaced.InheritedLostDeadline))
        {
            inheritedLostFamily = displaced.LostFamilyKeys;
            displaced.LostFamilyKeys = null;
            inheritedWithdrawalAcknowledged =
                displaced.WithdrawalAcknowledged;
            inheritedResult = displaced.Result;
            inheritedExactCellId = displaced.ExactCellId;
            inheritedCollisionGeneration = displaced.CollisionGeneration;
            if (displaced.ProjectionSequence != 0UL
                && _pendingProjection.TryGetValue(
                    displaced.ProjectionSequence,
                    out RuntimePlacementProjectionSnapshot pendingWithdrawal)
                && pendingWithdrawal.Kind
                    is RuntimePlacementProjectionKind.Withdraw)
            {
                inheritedWithdrawal = pendingWithdrawal;
                displaced.ProjectionSequence = 0UL;
            }
        }
        _ = CancelCoreDeferred(
            key,
            cancelLostFamily: false,
            preserveLostFamily: inheritedLostFamily is not null,
            out RuntimePlacementProjectionSnapshot? discard);

        Operation replacement = RentOperation();
        replacement.Record = record;
        replacement.Token = token;
        replacement.Key = key;
        replacement.PositionAuthorityVersion = expectedPositionAuthorityVersion;
        replacement.SessionLifetimeVersion = _entities.SessionLifetimeVersion;
        replacement.SourceSpatialAuthorityVersion =
            record.SpatialAuthorityVersion;
        replacement.SourceVelocityAuthorityVersion =
            record.VelocityAuthorityVersion;
        replacement.PreviousContact = record.PhysicsBody?.InContact ?? false;
        replacement.PreviousOnWalkable =
            record.PhysicsBody?.OnWalkable ?? false;
        replacement.Command = default;
        replacement.Result = default;
        replacement.SpatialAuthorityVersion = record.SpatialAuthorityVersion;
        replacement.PlacementCommitVersion = record.PlacementCommitVersion;
        replacement.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
        replacement.Kind = kind;
        replacement.Portal = portal;
        replacement.LostFamilyKeys = inheritedLostFamily;
        replacement.InheritedLostDeadline = inheritedLostFamily is not null;
        replacement.WithdrawalAcknowledged = inheritedWithdrawalAcknowledged;
        if (inheritedWithdrawal is { } retainedWithdrawal)
        {
            replacement.ProjectionSequence =
                retainedWithdrawal.Token.Sequence;
            replacement.Result = inheritedResult;
            replacement.ExactCellId = inheritedExactCellId;
            replacement.CollisionGeneration = inheritedCollisionGeneration;
        }
        _operations[key] = replacement;
        if (captureMoverPreparationAuthority)
        {
            _moverPreparationAuthorities[key] = CapturePreparationAuthority(
                replacement,
                acceptedPosition!.Value,
                prepared: false);
        }
        if (discard is { } cancelled)
            PublishPlacement(cancelled);
        return _operations.TryGetValue(key, out Operation? currentOperation)
            && currentOperation.Token == token
            ? token
            : default;
    }

    private bool HasRetainedCompletion(RuntimeEntityKey key)
    {
        foreach (RuntimeEntityPlacementToken token
            in _acknowledgedPlacementCompletions.Keys)
        {
            if (token.Entity == key)
                return true;
        }
        return false;
    }

    internal RuntimeSetPositionMoverPreparationStatus PrepareMover(
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionMoverPreparation preparation,
        out RuntimeSetPositionCommand command)
    {
        EnsureNotDisposed();
        command = default;
        if (!token.IsValid
            || !_operations.TryGetValue(token.Entity, out Operation? operation)
            || operation.Token != token
            || operation.Stage is not (
                RuntimeEntityPlacementStage.AwaitingPreparation
                or RuntimeEntityPlacementStage.AwaitingCell)
            || operation.Stage is RuntimeEntityPlacementStage.AwaitingCell
                && (!operation.DormantLocalActivation
                    || !operation.WakeableLostCell)
            || !IsCurrent(operation)
            || !_moverPreparationAuthorities.TryGetValue(
                token.Entity,
                out MoverPreparationAuthority authority)
            || authority.OperationId != token.OperationId
            || !IsPreparationAuthorityCurrent(operation, authority))
        {
            return RuntimeSetPositionMoverPreparationStatus.RejectedAuthority;
        }

        if (!preparation.Setup.IsResolved)
        {
            return Park(
                operation,
                RuntimeSetPositionMoverPreparationStatus
                    .RetrySetupUnavailable);
        }

        RuntimeSetPositionMoverPreparation effectivePreparation = preparation;
        if (preparation.ResolveWorldOffsetFromRuntimeFrame)
        {
            if (!_physics.TryGetWorldFrameOffset(
                    authority.AcceptedPosition.LandblockId,
                    out float worldOffsetX,
                    out float worldOffsetY))
            {
                _physics.ThrowIfWorldFrameUnreachable(
                    authority.AcceptedPosition.LandblockId);
                return Park(
                    operation,
                    RuntimeSetPositionMoverPreparationStatus
                        .RetryWorldFrameUnavailable);
            }

            effectivePreparation = preparation with
            {
                ShadowWorldOffsetX = worldOffsetX,
                ShadowWorldOffsetY = worldOffsetY,
            };
        }

        if (!RuntimeSetPositionMoverPreparer.TryBuild(
                operation.Record,
                authority.AcceptedPosition,
                authority.SetupTableId,
                operation.Kind,
                operation.Portal,
                authority.VelocityAuthorityVersion,
                effectivePreparation,
                out command)
            || !IsStructurallyValid(command.Physics))
        {
            command = default;
            return RuntimeSetPositionMoverPreparationStatus.InvalidData;
        }

        _moverPreparationAuthorities[token.Entity] = authority with
        {
            Prepared = true,
            PreparedCommand = command,
        };

        operation.ParkReason = RuntimeSetPositionParkReason.None;
        return RuntimeSetPositionMoverPreparationStatus.Prepared;
    }

    private static RuntimeSetPositionMoverPreparationStatus Park(
        Operation operation,
        RuntimeSetPositionMoverPreparationStatus status)
    {
        operation.ParkReason = status.ParkReason();
        return status;
    }

    internal RuntimeSetPositionMoverPreparationStatus
        TryPrepareAndSubmitAuthoredPlacement(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token,
        RuntimeSetPositionOperationKind operationKind,
        PhysicsSetPositionFlags flags,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        out RuntimeSetPositionOutcome outcome,
        PhysicsPlacementClass placementClass = PhysicsPlacementClass.Ordinary,
        RuntimePortalPlacementAuthority portal = default,
        Vector3 line = default,
        float scatterRadiusX = 0f,
        float scatterRadiusY = 0f,
        uint scatterAttempts = 0u,
        float shadowWorldOffsetX = 0f,
        float shadowWorldOffsetY = 0f,
        bool resolveWorldOffsetFromRuntimeFrame = false)
    {
        outcome = default;

        RuntimeSetPositionMoverPreparationStatus status =
            TryPrepareAuthoredMover(
                record,
                token,
                operationKind,
                flags,
                collisionSource,
                gameTime,
                out RuntimeSetPositionCommand command,
                placementClass,
                portal,
                line,
                scatterRadiusX,
                scatterRadiusY,
                scatterAttempts,
                shadowWorldOffsetX,
                shadowWorldOffsetY,
                resolveWorldOffsetFromRuntimeFrame);
        if (status != RuntimeSetPositionMoverPreparationStatus.Prepared)
            return status;

        outcome = SubmitPreparedPlacement(token, command);
        return RuntimeSetPositionMoverPreparationStatus.Prepared;
    }

    internal RuntimeSetPositionMoverPreparationStatus TryPrepareAuthoredMover(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token,
        RuntimeSetPositionOperationKind operationKind,
        PhysicsSetPositionFlags flags,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        out RuntimeSetPositionCommand command,
        PhysicsPlacementClass placementClass = PhysicsPlacementClass.Ordinary,
        RuntimePortalPlacementAuthority portal = default,
        Vector3 line = default,
        float scatterRadiusX = 0f,
        float scatterRadiusY = 0f,
        uint scatterAttempts = 0u,
        float shadowWorldOffsetX = 0f,
        float shadowWorldOffsetY = 0f,
        bool resolveWorldOffsetFromRuntimeFrame = false)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(collisionSource);
        command = default;

        uint setupTableId = CanonicalSetupTableId(record);
        RuntimeSetPositionMoverSetup setup;
        if (setupTableId == 0u)
        {
            setup = RuntimeSetPositionMoverSetup.ResolvedAbsent;
        }
        else
        {
            PreparedCollisionReadResult<FlatSetupCollision> read =
                collisionSource.ReadSetupCollision(setupTableId);
            if (read.Status != PreparedAssetReadStatus.Loaded
                || read.Data is null)
            {
                return RuntimeSetPositionMoverPreparationStatus
                    .RetrySetupUnavailable;
            }
            setup = RuntimeSetPositionMoverSetup.Resolved(
                setupTableId, read.Data);
        }

        var preparation = new RuntimeSetPositionMoverPreparation(
            setup,
            operationKind,
            gameTime,
            placementClass,
            flags,
            line,
            scatterRadiusX,
            scatterRadiusY,
            scatterAttempts,
            shadowWorldOffsetX,
            shadowWorldOffsetY,
            portal,
            resolveWorldOffsetFromRuntimeFrame);
        return PrepareMover(token, preparation, out command);
    }

    internal bool IsExactPreparedPlacementCurrent(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        return token.IsValid
            && token.Entity == record.Key
            && _operations.TryGetValue(token.Entity, out Operation? operation)
            && ReferenceEquals(operation.Record, record)
            && operation.Token == token
            && operation.Stage
                is RuntimeEntityPlacementStage.AwaitingPreparation
            && IsCurrent(operation)
            && _moverPreparationAuthorities.TryGetValue(
                token.Entity,
                out MoverPreparationAuthority authority)
            && authority.OperationId == token.OperationId
            && authority.Prepared
            && authority.PreparedCommand == command
            && IsPreparationAuthorityCurrent(operation, authority);
    }

    internal bool TryEvaluateDormantLocalActivation(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command,
        out RuntimeDormantSetPositionEvaluation evaluation)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        evaluation = default;
        // Deferred dormant leases stay "current" while unbound, so TryRearm is
        // skipped by the short-circuit below. Recover spawn-ready indoor cells
        // first so rearm can publish first-entry after a stranded commit.
        if (_operations.TryGetValue(token.Entity, out Operation? pending)
            && pending.WakeableLostCell
            && pending.ExactCellId != 0u)
        {
            TryRecoverUnboundDeferredWhenSpawnReady(pending.ExactCellId);
        }

        if (!IsExactDormantLocalActivationCurrent(
                record,
                body,
                token,
                command,
                out _)
            && !TryRearmDeferredDormantLocalActivation(
                record,
                body,
                token,
                command))
        {
            return false;
        }
        if (!IsExactDormantLocalActivationCurrent(
                record,
                body,
                token,
                command,
                out Operation? operation))
        {
            return false;
        }

        PhysicsSetPositionRequest canonicalRequest = command.Physics with
        {
            MoverPhysicsState = record.FinalPhysicsState,
            MovingEntityId = token.Entity.LocalEntityId,
            CurrentCellId = null,
        };
        if (!IsStructurallyValid(canonicalRequest))
            return false;

        var canonicalCommand = command with { Physics = canonicalRequest };
        ulong collisionWorldAuthority = _physics.CollisionWorldAuthority;
        ulong shadowWorldAuthority = _physics.ShadowWorldAuthority;
        ClientObjectTable? objectTable = _physics.ObjectTable;
        ulong objectTableBindingAuthority =
            _physics.ObjectTableBindingAuthority;
        ulong objectTableAuthority = objectTable?.MutationRevision ?? 0UL;
        PhysicsSetPositionResult result = _physics.Engine.SetPosition(
            canonicalRequest,
            handleCollisions: null);
        if (!IsExactDormantLocalActivationCurrent(
                record,
                body,
                token,
                command,
                out Operation? current)
            || !ReferenceEquals(current, operation))
        {
            return false;
        }

        if (!_physics.TrySealCollisionEvaluationAuthority(
                result,
                collisionWorldAuthority,
                shadowWorldAuthority,
                objectTable,
                objectTableBindingAuthority,
                objectTableAuthority,
                out RuntimeCollisionEvaluationAuthority collisionAuthority))
        {
            if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"[rearm] guid=0x{record.ServerGuid:X8} seal-refused (transient; lease retained)"));
            }
            return false;
        }

        evaluation = new RuntimeDormantSetPositionEvaluation(
            token,
            canonicalCommand,
            result,
            collisionAuthority);
        return true;
    }

    internal bool IsDormantLocalEvaluationCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionEvaluation evaluation) =>
        evaluation.IsValid
        && IsExactDormantLocalActivationCurrent(
            record,
            body,
            evaluation.Placement,
            evaluation.Command,
            out _,
            allowCanonicalCommand: true)
        && _physics.IsCollisionEvaluationAuthorityCurrent(
            evaluation.CollisionAuthority);

    internal bool IsDormantLocalActivationLeaseCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command) =>
        IsExactDormantLocalActivationCurrent(
            record,
            body,
            token,
            command,
            out _,
            allowDeferredLease: true);

    internal bool IsDormantLocalActivationAwaitingCell(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command)
    {
        return IsExactDormantLocalActivationCurrent(
                record,
                body,
                token,
                command,
                out Operation? operation,
                allowDeferredLease: true)
            && operation is not null
            && operation.Stage is RuntimeEntityPlacementStage.AwaitingCell
            && operation.DormantLocalActivation
            && operation.WakeableLostCell;
    }

    private bool TryRearmDeferredDormantLocalActivation(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command)
    {
        bool current = IsExactDormantLocalActivationCurrent(
            record,
            body,
            token,
            command,
            out Operation? operation,
            allowDeferredLease: true);
        if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
        {
            string why = !current || operation is null
                ? "not-current"
                : operation.Stage is not RuntimeEntityPlacementStage.AwaitingCell
                    ? $"stage={operation.Stage}"
                : !operation.DormantLocalActivation ? "not-dormant"
                : !operation.WakeableLostCell ? "not-wakeable"
                : !operation.CollisionGenerationReady ? "gen-not-ready"
                : operation.ProjectionSequence != 0UL ? "proj-seq"
                : operation.CollisionGeneration != _physics
                    .CollisionGenerationAuthority(operation.ExactCellId)
                    ? $"gen-mismatch({operation.CollisionGeneration}!={_physics.CollisionGenerationAuthority(operation.ExactCellId)})"
                : !_physics.Engine.IsSpawnCellReady(operation.ExactCellId)
                    ? "spawn-not-ready"
                : !_physics.IsCollisionEvaluationPrefixAdmissible(
                    operation.ExactCellId)
                    ? "prefix-inadmissible"
                : "OK";
            Console.WriteLine(FormattableString.Invariant(
                $"[rearm] guid=0x{record.ServerGuid:X8} verdict={why}"));
        }
        if (!current
            || operation is null
            || operation.Stage is not RuntimeEntityPlacementStage.AwaitingCell
            || !operation.DormantLocalActivation
            || !operation.WakeableLostCell
            || !operation.CollisionGenerationReady
            || operation.ProjectionSequence != 0UL
            || operation.CollisionGeneration != _physics
                .CollisionGenerationAuthority(operation.ExactCellId)
            || !_physics.Engine.IsSpawnCellReady(operation.ExactCellId)
            || !_physics.IsCollisionEvaluationPrefixAdmissible(
                operation.ExactCellId))
        {
            return false;
        }

        UnindexDeferred(operation);
        operation.WakeableLostCell = false;
        operation.CollisionGenerationReady = false;
        operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
        return true;
    }

    /// <summary>
    /// When an indoor spawn EnvCell becomes ready after its landblock collision
    /// generation already committed, rebind stranded deferred ops onto that
    /// authority and wake them. Covers both:
    /// - unbound (gen==0) ops left by CommitCollisionGeneration when spawn was
    ///   not ready at commit time, and
    /// - ops parked on Expected (authority+1) because first-entry activated
    ///   after authority already existed — no later admission ever commits that
    ///   future generation, so dormant local first-entry stays unpublished.
    /// </summary>
    internal int TryRecoverUnboundDeferredWhenSpawnReady(uint cellId)
    {
        EnsureNotDisposed();
        if (cellId == 0u
            || !_physics.Engine.IsSpawnCellReady(cellId))
        {
            return 0;
        }

        uint prefix = cellId & 0xFFFF0000u;
        ulong authority = _physics.CollisionGenerationAuthority(cellId);
        if (authority == 0UL
            || !_physics.IsCollisionEvaluationPrefixAdmissible(cellId))
        {
            return 0;
        }

        int recovered = RecoverUnboundDeferredOntoAuthority(
            cellId,
            prefix,
            authority);
        recovered += RecoverStaleExpectedDeferredOntoAuthority(
            cellId,
            prefix,
            authority);
        return recovered;
    }

    /// <summary>
    /// After a collision admission fully closes, scan deferred ops under the
    /// landblock whose spawn cells are already ready and recover them now that
    /// the prefix is admissible.
    /// </summary>
    internal int TryRecoverDeferredForLandblock(uint landblockId)
    {
        EnsureNotDisposed();
        uint prefix = landblockId & 0xFFFF0000u;
        if (prefix == 0u)
            return 0;

        var cells = new HashSet<uint>();
        for (int index = 0; index < _unboundDeferredCellOrder.Count; index++)
        {
            UnboundCellKey key = _unboundDeferredCellOrder[index];
            if (key.CollisionPrefix == prefix)
                cells.Add(key.CellId);
        }
        for (int index = 0; index < _deferredBucketOrder.Count; index++)
        {
            CellGenerationKey key = _deferredBucketOrder[index];
            if (key.CollisionPrefix == prefix)
                cells.Add(key.CellId);
        }

        int recovered = 0;
        foreach (uint cellId in cells)
            recovered += TryRecoverUnboundDeferredWhenSpawnReady(cellId);
        return recovered;
    }

    private int RecoverUnboundDeferredOntoAuthority(
        uint cellId,
        uint prefix,
        ulong authority)
    {
        var unboundKey = new UnboundCellKey(cellId, prefix);
        if (!_unboundDeferredByCell.Remove(
                unboundKey,
                out List<RuntimeEntityKey>? retained))
        {
            return 0;
        }

        _unboundDeferredCellOrder.Remove(unboundKey);
        int recovered = 0;
        List<RuntimeEntityKey>? leftover = null;
        for (int index = 0; index < retained.Count; index++)
        {
            RuntimeEntityKey entity = retained[index];
            if (!_operations.TryGetValue(entity, out Operation? operation)
                || !operation.WakeableLostCell
                || operation.ExactCellId != cellId
                || operation.CollisionPrefix != prefix
                || operation.CollisionGeneration != 0UL)
            {
                leftover ??= [];
                leftover.Add(entity);
                continue;
            }

            MarkDeferredRecoveredOntoAuthority(operation, authority);
            recovered++;
        }

        if (leftover is { Count: > 0 })
        {
            _unboundDeferredByCell.Add(unboundKey, leftover);
            _unboundDeferredCellOrder.Add(unboundKey);
        }

        return recovered;
    }

    private int RecoverStaleExpectedDeferredOntoAuthority(
        uint cellId,
        uint prefix,
        ulong authority)
    {
        // Ops parked on Expected (typically authority+1) after the landblock
        // already committed never receive a matching CommitCollisionGeneration.
        CellGenerationKey[] stale = _deferredBucketOrder
            .Where(key => key.CellId == cellId
                && key.CollisionPrefix == prefix
                && key.CollisionGeneration != authority)
            .ToArray();
        if (stale.Length == 0)
            return 0;

        int recovered = 0;
        for (int bucketIndex = 0; bucketIndex < stale.Length; bucketIndex++)
        {
            CellGenerationKey bucket = stale[bucketIndex];
            if (!_deferredByCellGeneration.TryGetValue(
                    bucket,
                    out List<RuntimeEntityKey>? indexed))
            {
                continue;
            }

            RuntimeEntityKey[] entities = indexed.ToArray();
            RemoveDeferredBucket(bucket);
            for (int index = 0; index < entities.Length; index++)
            {
                RuntimeEntityKey entity = entities[index];
                if (!_operations.TryGetValue(entity, out Operation? operation)
                    || !operation.WakeableLostCell
                    || operation.ExactCellId != cellId
                    || operation.CollisionPrefix != prefix
                    || operation.CollisionGeneration != bucket.CollisionGeneration)
                {
                    continue;
                }

                MarkDeferredRecoveredOntoAuthority(operation, authority);
                recovered++;
            }
        }

        return recovered;
    }

    private void MarkDeferredRecoveredOntoAuthority(
        Operation operation,
        ulong authority)
    {
        ulong prior = operation.CollisionGeneration;
        operation.CollisionGeneration = authority;
        operation.CollisionGenerationReady = true;
        if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[wake] recover-spawn cell=0x{operation.ExactCellId:X8} gen={authority} (was {prior}) guid=0x{operation.Record.ServerGuid:X8} dormant={operation.DormantLocalActivation}"));
        }

        if (operation.WithdrawalAcknowledged)
            RetryDeferred(operation);
    }

    internal bool TryPrepareDormantLocalActivationCommit(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionEvaluation evaluation,
        bool provenShapeless,
        out PreparedDormantSetPositionCommit? prepared)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        prepared = null;
        if (!IsDormantLocalEvaluationCurrent(record, body, evaluation)
            || !IsExactDormantLocalActivationCurrent(
                record,
                body,
                evaluation.Placement,
                evaluation.Command,
                out Operation? operation,
                allowCanonicalCommand: true)
            || operation is null)
        {
            return false;
        }

        PhysicsSetPositionResult result = evaluation.Result;
        ShadowObjectRegistry.PreparedSetPositionShadowCommit? shadow = null;
        RuntimeCollisionReportingState.PreparedSetPositionCollisionBatch?
            collision = null;
        RuntimePlacementProjectionSnapshot projection = default;
        SortedDictionary<ulong, RuntimePlacementProjectionSnapshot>?
            pendingProjection = null;
        ulong deferredCollisionGeneration = 0UL;
        bool deferredCollisionGenerationReady = false;
        List<RuntimeEntityKey>? deferredBucket = null;
        bool deferredBucketIsNew = false;

        if (!result.IsDeferred)
        {
            if (!_physics.CollisionReports.TryPrepareSetPositionBatch(
                    record,
                    body,
                    evaluation.Command.GameTime,
                    result.IsCommitted && operation.PreviousContact,
                    result.IsCommitted && operation.PreviousOnWalkable,
                    result.IsCommitted && result.OnWalkable,
                    result.CollidedWithEnvironment,
                    result.CollidedObjectIds,
                    out collision)
                || collision is null)
            {
                return false;
            }
        }

        if (result.IsDeferred)
        {
            // When the spawn EnvCell is already ready under a committed,
            // admissible landblock authority, park on that authority with
            // ready=true so TryRearm can publish first-entry immediately.
            // Parking on Expected (authority+1) waits forever if no later
            // admission runs — the indoor login strand after world-reveal
            // already reports ready.
            ulong authority = _physics.CollisionGenerationAuthority(result.CellId);
            bool spawnReady = result.CellId != 0u
                && _physics.Engine.IsSpawnCellReady(result.CellId);
            bool admissible = result.CellId != 0u
                && _physics.IsCollisionEvaluationPrefixAdmissible(result.CellId);
            bool wakeImmediately = authority != 0UL && spawnReady && admissible;
            deferredCollisionGeneration = wakeImmediately
                ? authority
                : _physics.ExpectedCollisionGeneration(result.CellId);
            deferredCollisionGenerationReady = wakeImmediately;
            _preparedMovers.EnsureCapacity(_preparedMovers.Count + 1);
            if (result.CellId != 0u && deferredCollisionGeneration != 0UL)
            {
                var bucketKey = new CellGenerationKey(
                    result.CellId,
                    result.CellId & 0xFFFF0000u,
                    deferredCollisionGeneration);
                if (_deferredByCellGeneration.TryGetValue(
                        bucketKey,
                        out deferredBucket))
                {
                    deferredBucket.EnsureCapacity(deferredBucket.Count + 1);
                }
                else
                {
                    deferredBucket = [operation.Key];
                    deferredBucketIsNew = true;
                    _deferredByCellGeneration.EnsureCapacity(
                        _deferredByCellGeneration.Count + 1);
                    _deferredBucketOrder.EnsureCapacity(
                        _deferredBucketOrder.Count + 1);
                }
            }
            if (!_physics.Engine.ShadowObjects.TryPrepareSetPosition(
                    operation.Key.LocalEntityId,
                    result.Position,
                    result.Orientation,
                    result.CellId,
                    evaluation.Command.ShadowWorldOffsetX,
                    evaluation.Command.ShadowWorldOffsetY,
                    PhysicsShadowCommitAction.Preserve,
                    ImmutableArray<uint>.Empty,
                    provenShapeless,
                    suspendOwner: true,
                    out shadow)
                || shadow is null)
            {
                return false;
            }
        }

        ulong expectedProjectionSequence = _nextProjectionSequence;
        if (result.IsCommitted)
        {
            _ = checked(record.ObjectClockEpoch + 1UL);
            _ = checked(record.PlacementCommitVersion + 1UL);
            if (record.FullCellId != result.CellId)
                _ = checked(record.SpatialAuthorityVersion + 1UL);
            if (!_physics.TryPrepareSpatialRootAdmission(record))
                return false;

            ulong sequence = checked(expectedProjectionSequence + 1UL);
            ulong spatial = record.SpatialAuthorityVersion
                + (record.FullCellId == result.CellId ? 0UL : 1UL);
            ulong placement = checked(record.PlacementCommitVersion + 1UL);
            var token = new RuntimePlacementProjectionToken(
                sequence,
                Revision: 1UL,
                operation.Key,
                operation.PositionAuthorityVersion,
                spatial,
                placement,
                operation.SessionLifetimeVersion,
                result.CellId,
                operation.CollisionGeneration,
                evaluation.Command.Portal);
            projection = new RuntimePlacementProjectionSnapshot(
                token,
                RuntimePlacementProjectionKind.Place,
                result.Position,
                result.Orientation,
                result.CellLocalPosition,
                result.InContact,
                result.OnWalkable);
            pendingProjection = new SortedDictionary<
                ulong,
                RuntimePlacementProjectionSnapshot>(_pendingProjection)
            {
                [sequence] = projection,
            };
        }

        prepared = new PreparedDormantSetPositionCommit
        {
            Evaluation = evaluation,
            Entity = operation.Key,
            OperationId = operation.Token.OperationId,
            ExpectedProjectionSequence = expectedProjectionSequence,
            Shadow = shadow,
            Collision = collision,
            Projection = projection,
            PendingProjection = pendingProjection,
            DeferredCollisionGeneration = deferredCollisionGeneration,
            DeferredCollisionGenerationReady = deferredCollisionGenerationReady,
            DeferredBucket = deferredBucket,
            DeferredBucketIsNew = deferredBucketIsNew,
        };
        return IsPreparedDormantCommitCurrent(record, body, prepared);
    }

    internal bool TryApplyDormantLocalActivationCommit(
        RuntimeEntityRecord record,
        PhysicsBody body,
        PlayerMovementController controller,
        EntityPhysicsHost physicsHost,
        PreparedDormantSetPositionCommit prepared,
        out RuntimeDormantSetPositionCommitReceipt receipt)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(physicsHost);
        ArgumentNullException.ThrowIfNull(prepared);
        receipt = default;
        if (!IsPreparedDormantCommitCurrent(record, body, prepared)
            || !_operations.TryGetValue(
                prepared.Entity,
                out Operation? operation))
        {
            return false;
        }

        PhysicsSetPositionResult result = prepared.Evaluation.Result;
        operation.Body = body;
        operation.DormantLocalActivation = true;
        if (result.IsDeferred)
        {
            if (prepared.Shadow is null
                || !_physics.Engine.ShadowObjects.TryApplySetPosition(
                    prepared.Shadow,
                    out ShadowObjectRegistry.SetPositionShadowCommitReceipt
                        deferredShadowReceipt))
            {
                return false;
            }
            body.Orientation = result.Orientation;
            body.StageDormantCellFrame(
                result.CellId,
                result.Position,
                result.CellLocalPosition);
            body.InWorld = false;
            body.TransientState &= ~TransientStateFlags.Active;
            operation.Result = result;
            operation.ExactCellId = result.CellId;
            operation.WakeableLostCell = true;
            operation.CollisionGeneration = prepared
                .DeferredCollisionGeneration;
            operation.CollisionPrefix = result.CellId & 0xFFFF0000u;
            operation.CollisionGenerationReady = prepared
                .DeferredCollisionGenerationReady;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingCell;
            _preparedMovers[operation.Key] = prepared.Evaluation.Command.Physics;
            if (prepared.DeferredBucket is { } deferredBucket)
            {
                var bucketKey = new CellGenerationKey(
                    result.CellId,
                    operation.CollisionPrefix,
                    prepared.DeferredCollisionGeneration);
                if (prepared.DeferredBucketIsNew)
                {
                    _deferredByCellGeneration.Add(bucketKey, deferredBucket);
                    _deferredBucketOrder.Add(bucketKey);
                }
                else if (!deferredBucket.Contains(operation.Key))
                {
                    deferredBucket.Add(operation.Key);
                }
            }
            receipt = new RuntimeDormantSetPositionCommitReceipt(
                RuntimeDormantSetPositionCommitStatus.DeferredCell,
                operation.Key,
                operation.Token.OperationId,
                default,
                default,
                deferredShadowReceipt,
                prepared.Evaluation.CollisionAuthority,
                operation.Record.VectorAuthorityVersion,
                HitGround: false,
                LeaveGround: false);
            return true;
        }

        if (prepared.Collision is null
            || !_physics.CollisionReports.TryInstallSetPositionBatch(
                prepared.Collision,
                out RuntimeCollisionReportingState
                    .SetPositionCollisionBatchReceipt collisionReceipt))
        {
            return false;
        }

        if (!result.IsCommitted)
        {
            operation.Result = result;
            receipt = new RuntimeDormantSetPositionCommitReceipt(
                RuntimeDormantSetPositionCommitStatus.RejectedPlacement,
                operation.Key,
                operation.Token.OperationId,
                default,
                collisionReceipt,
                default,
                prepared.Evaluation.CollisionAuthority,
                operation.Record.VectorAuthorityVersion,
                HitGround: false,
                LeaveGround: false);
            return true;
        }

        bool previousOnWalkable = operation.PreviousOnWalkable;
        bool hitGround = !previousOnWalkable
            && result.InContact
            && result.OnWalkable;
        bool leaveGround = previousOnWalkable
            && !(result.InContact && result.OnWalkable);

        UnindexDeferred(operation);
        body.Orientation = result.Orientation;
        body.StageDormantCellFrame(
            result.CellId,
            result.Position,
            result.CellLocalPosition);
        body.LastUpdateTime = prepared.Evaluation.Command.GameTime;
        body.ContactPlaneValid = result.InContact;
        body.ContactPlane = result.ContactPlane;
        body.ContactPlaneCellId = result.ContactPlaneCellId;
        body.ContactPlaneIsWater = result.ContactPlaneIsWater;
        if (result.InContact)
            body.GroundNormal = result.ContactPlane.Normal;
        _ = PhysicsObjUpdate.CommitSetPositionContactPrefix(
            body,
            result.InContact,
            result.OnWalkable,
            previousOnWalkable);
        operation.Result = result;
        operation.ExactCellId = result.CellId;
        operation.WakeableLostCell = false;
        operation.CollisionGenerationReady = false;
        operation.EnteringWorldFromCelllessResidence = false;
        operation.Stage = RuntimeEntityPlacementStage
            .AwaitingFinalShadowPreparation;
        receipt = new RuntimeDormantSetPositionCommitReceipt(
            RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation,
            operation.Key,
            operation.Token.OperationId,
            prepared.Projection,
            collisionReceipt,
            default,
            prepared.Evaluation.CollisionAuthority,
            operation.Record.VectorAuthorityVersion,
            hitGround,
            leaveGround);
        return true;
    }

    internal SetPositionCollisionBatchDispatchResult
        DispatchDormantLocalActivationCollision(
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (receipt.Status
            is RuntimeDormantSetPositionCommitStatus.DeferredCell
                or RuntimeDormantSetPositionCommitStatus.RejectedAuthority)
        {
            return new(
                SetPositionCollisionBatchDispatchStatus.RejectedReceipt,
                Reported: false);
        }
        SetPositionCollisionBatchDispatchResult dispatch = _physics
            .CollisionReports.DispatchSetPositionBatchResult(receipt.Collision);
        bool reported = dispatch.Reported;
        if (receipt.Status
                is RuntimeDormantSetPositionCommitStatus.RejectedPlacement
            && _operations.TryGetValue(receipt.Entity, out Operation? operation)
            && operation.Token.OperationId == receipt.OperationId
            && IsCurrent(operation)
            && !operation.Result.IsCommitted
            && !operation.Result.IsDeferred)
        {
            operation.Result = operation.Result with
            {
                Error = reported
                    ? PhysicsSetPositionError.Collided
                    : PhysicsSetPositionError.NoValidPosition,
                CollisionHandlerResult = reported,
            };
        }
        return dispatch;
    }

    internal bool IsDormantLocalActivationPrephaseCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        return receipt.Status is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation
            && _operations.TryGetValue(receipt.Entity, out Operation? operation)
            && operation.Token.OperationId == receipt.OperationId
            && operation.Stage is RuntimeEntityPlacementStage
                .AwaitingFinalShadowPreparation
            && operation.DormantLocalActivation
            && IsCurrent(operation)
            && ReferenceEquals(operation.Record, record)
            && ReferenceEquals(record.PhysicsBody, body)
            && !body.InWorld
            && (body.TransientState & TransientStateFlags.Active) == 0
            && record.PhysicsHost is null
            && record.RemoteMotion is null
            && record.Projectile is null
            && !_physics.IsSpatialRoot(record)
            && _moverPreparationAuthorities.TryGetValue(
                receipt.Entity,
                out MoverPreparationAuthority authority)
            && authority.OperationId == receipt.OperationId
            && authority.Prepared
            && record.PositionAuthorityVersion
                == authority.PositionAuthorityVersion
            && record.ObjDescAuthorityVersion
                == authority.ObjDescAuthorityVersion
            && record.CreateIntegrationVersion
                == authority.CreateIntegrationVersion
            && CanonicalSetupTableId(record) == authority.SetupTableId;
    }

    internal bool IsDormantLocalActivationResponseCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (receipt.Status is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation)
            return IsDormantLocalActivationPrephaseCurrent(record, body, receipt);
        return receipt.Status is RuntimeDormantSetPositionCommitStatus
                .RejectedPlacement
            && _operations.TryGetValue(receipt.Entity, out Operation? operation)
            && operation.Token.OperationId == receipt.OperationId
            && operation.DormantLocalActivation
            && IsCurrent(operation)
            && ReferenceEquals(operation.Record, record)
            && ReferenceEquals(record.PhysicsBody, body)
            && !operation.Result.IsCommitted
            && !operation.Result.IsDeferred
            && !body.InWorld
            && (body.TransientState & TransientStateFlags.Active) == 0
            && record.PhysicsHost is null
            && record.RemoteMotion is null
            && record.Projectile is null
            && !_physics.IsSpatialRoot(record);
    }

    internal bool CommitDormantLocalActivationPostGround(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (!IsDormantLocalActivationPrephaseCurrent(record, body, receipt))
            return false;
        PhysicsObjUpdate.CommitSetPositionPostGround(body);
        PhysicsSetPositionResult result = _operations[receipt.Entity].Result;
        body.SlidingNormal = result.SlidingNormal;
        if (result.SlidingNormalValid)
            body.TransientState |= TransientStateFlags.Sliding;
        else
            body.TransientState &= ~TransientStateFlags.Sliding;
        return IsDormantLocalActivationPrephaseCurrent(record, body, receipt);
    }

    internal bool CommitDormantLocalActivationPostCollision(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (!_operations.TryGetValue(receipt.Entity, out Operation? operation)
            || operation.Token.OperationId != receipt.OperationId
            || !IsCurrent(operation)
            || !ReferenceEquals(operation.Record, record)
            || !ReferenceEquals(record.PhysicsBody, body))
        {
            return false;
        }
        if (receipt.Status is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation
            && !IsDormantLocalActivationPrephaseCurrent(record, body, receipt))
        {
            return false;
        }
        PhysicsSetPositionResult result = operation.Result;
        body.FramesStationaryFall = result.FramesStationaryFall;
        if (IsVelocityCurrent(operation))
        {
            PhysicsObjUpdate.HandleAllCollisions(
                body,
                result.CollisionNormalValid,
                result.CollisionNormal,
                operation.PreviousContact,
                operation.PreviousOnWalkable,
                body.OnWalkable);
        }
        CommitStationaryBits(body, result.FramesStationaryFall);
        return IsCurrent(operation);
    }

    internal bool TryPrepareDormantLocalActivationFinalCommit(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt,
        bool provenShapeless,
        out PreparedDormantActivationFinalCommit? prepared)
    {
        prepared = null;
        if (!IsDormantLocalActivationPrephaseCurrent(record, body, receipt)
            || !_operations.TryGetValue(receipt.Entity, out Operation? operation))
        {
            return false;
        }
        PhysicsSetPositionResult result = operation.Result;
        if (!_physics.Engine.ShadowObjects.TryPrepareSetPosition(
                operation.Key.LocalEntityId,
                result.Position,
                result.Orientation,
                result.CellId,
                operation.Command.ShadowWorldOffsetX,
                operation.Command.ShadowWorldOffsetY,
                result.ShadowAction,
                result.CrossCellIds,
                provenShapeless,
                suspendOwner: false,
                out ShadowObjectRegistry.PreparedSetPositionShadowCommit? shadow)
            || shadow is null
            || _nextProjectionSequence + 1UL
                != receipt.Projection.Token.Sequence)
        {
            return false;
        }
        var pending = new SortedDictionary<
            ulong,
            RuntimePlacementProjectionSnapshot>(_pendingProjection)
        {
            [receipt.Projection.Token.Sequence] = receipt.Projection,
        };
        prepared = new PreparedDormantActivationFinalCommit
        {
            Entity = receipt.Entity,
            OperationId = receipt.OperationId,
            ExpectedProjectionSequence = _nextProjectionSequence,
            Shadow = shadow,
            Projection = receipt.Projection,
            PendingProjection = pending,
        };
        bool current = IsDormantLocalActivationPrephaseCurrent(
            record, body, receipt);
        bool shadowCurrent = _physics.Engine.ShadowObjects
            .IsPreparedSetPositionCurrent(shadow);
        return current && shadowCurrent;
    }

    internal bool TryApplyDormantLocalActivationFinalCommit(
        RuntimeEntityRecord record,
        PhysicsBody body,
        PlayerMovementController controller,
        EntityPhysicsHost physicsHost,
        in RuntimeDormantSetPositionCommitReceipt prephase,
        PreparedDormantActivationFinalCommit prepared,
        out RuntimeDormantSetPositionCommitReceipt committed)
    {
        committed = default;
        if (prepared.Entity != prephase.Entity
            || prepared.OperationId != prephase.OperationId
            || prepared.ExpectedProjectionSequence != _nextProjectionSequence
            || prepared.Projection != prephase.Projection
            || !IsDormantLocalActivationPrephaseCurrent(record, body, prephase)
            || !_physics.Engine.ShadowObjects.TryApplySetPosition(
                prepared.Shadow,
                out ShadowObjectRegistry.SetPositionShadowCommitReceipt shadow))
        {
            return false;
        }
        Operation operation = _operations[prephase.Entity];
        PhysicsSetPositionResult result = operation.Result;
        if (record.FullCellId != result.CellId)
        {
            _entities.SetFullCell(record, result.CellId,
                (result.CellId & 0xFFFF0000u) | 0xFFFFu);
        }
        operation.SpatialAuthorityVersion = record.SpatialAuthorityVersion;
        _entities.AdvancePlacementCommit(record);
        operation.PlacementCommitVersion = record.PlacementCommitVersion;
        body.InWorld = true;
        bool isStatic = (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0;
        if (!isStatic)
            body.TransientState |= TransientStateFlags.Active;
        _entities.SetPhysicsHost(record, physicsHost);
        controller.CommitRuntimeActivationFrame();
        _physics.Engine.UpdatePlayerCurrCell(result.CellId);
        _physics.AcknowledgeSpatialProjection(record, spatial: true);
        _entities.ResetObjectClockForEnterWorld(record, isStatic);
        operation.Stage = RuntimeEntityPlacementStage.AwaitingCommitAcknowledgement;
        operation.ProjectionSequence = prepared.Projection.Token.Sequence;
        _pendingProjection = prepared.PendingProjection;
        _nextProjectionSequence = prepared.Projection.Token.Sequence;
        CancelLostFamilyDeadlines(operation);
        controller.ActivateRuntimePublication();
        committed = prephase with
        {
            Status = RuntimeDormantSetPositionCommitStatus.Committed,
            Shadow = shadow,
        };
        return true;
    }

    internal void DispatchDormantLocalActivationShadow(
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (receipt.Status is not (
                RuntimeDormantSetPositionCommitStatus.Committed
                or RuntimeDormantSetPositionCommitStatus.DeferredCell))
            return;
        _physics.Engine.ShadowObjects.DispatchSetPositionCommit(receipt.Shadow);
    }

    internal void DispatchDormantLocalActivationPlacement(
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (!receipt.IsCommitted)
            return;
        PublishPlacement(receipt.Projection);
    }

    internal void DiscardDormantLocalActivationDispatches(
        in RuntimeDormantSetPositionCommitReceipt receipt,
        bool collisionAlreadyDispatched)
    {
        if (!collisionAlreadyDispatched)
            _physics.CollisionReports.DiscardSetPositionBatch(receipt.Collision);
        _physics.Engine.ShadowObjects.DiscardSetPositionCommit(receipt.Shadow);
    }

    internal void RetireDormantLocalActivation(
        in RuntimeDormantSetPositionCommitReceipt receipt,
        bool collisionAlreadyDispatched)
    {
        DiscardDormantLocalActivationDispatches(
            receipt,
            collisionAlreadyDispatched);
        _physics.CollisionReports.RetireSetPositionBatchOwner(
            receipt.Collision);
        if (_operations.TryGetValue(receipt.Entity, out Operation? operation)
            && operation.Token.OperationId == receipt.OperationId)
        {
            _ = CancelCore(receipt.Entity, operation.Token);
        }
    }

    internal void RetireDormantLocalActivationToken(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token)
    {
        if (!token.IsValid
            || record.Key != token.Entity
            || !_operations.TryGetValue(token.Entity, out Operation? operation)
            || operation.Token != token
            || !ReferenceEquals(operation.Record, record))
        {
            return;
        }
        _ = CancelCore(token.Entity, token);
    }

    internal bool IsDormantLocalActivationCommitCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (!receipt.IsCommitted
            || !receipt.Projection.Token.IsValid
            || record.Key != receipt.Projection.Token.Entity
            || !ReferenceEquals(record.PhysicsBody, body)
            || !_pendingProjection.TryGetValue(
                receipt.Projection.Token.Sequence,
                out RuntimePlacementProjectionSnapshot pending)
            || pending != receipt.Projection
            || !_operations.TryGetValue(
                receipt.Projection.Token.Entity,
                out Operation? operation)
            || operation.Stage is not RuntimeEntityPlacementStage
                .AwaitingCommitAcknowledgement
            || operation.ProjectionSequence
                != receipt.Projection.Token.Sequence
            || !IsCurrent(operation)
            || !body.InWorld
            || !_physics.IsSpatialRoot(record))
        {
            return false;
        }
        return true;
    }

    internal bool TryCaptureDormantLocalActivationResult(
        in RuntimeEntityPlacementToken token,
        out PhysicsSetPositionResult result)
    {
        if (!_disposed
            && token.IsValid
            && _operations.TryGetValue(token.Entity, out Operation? operation)
            && operation.Token == token)
        {
            result = operation.Result;
            return true;
        }
        result = default;
        return false;
    }

    private bool IsPreparedDormantCommitCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        PreparedDormantSetPositionCommit prepared)
    {
        if (_nextProjectionSequence != prepared.ExpectedProjectionSequence
            || !IsDormantLocalEvaluationCurrent(
                record,
                body,
                prepared.Evaluation)
            || !_operations.TryGetValue(
                prepared.Entity,
                out Operation? operation)
            || operation.Token.OperationId != prepared.OperationId)
        {
            return false;
        }
        if (prepared.Collision is not null
            && !_physics.CollisionReports.IsPreparedSetPositionBatchCurrent(
                prepared.Collision))
            return false;
        return prepared.Shadow is null
            || _physics.Engine.ShadowObjects.IsPreparedSetPositionCurrent(
                prepared.Shadow);
    }

    private static void CommitStationaryBits(
        PhysicsBody body,
        int framesStationaryFall)
    {
        body.TransientState &= ~(TransientStateFlags.StationaryFall
            | TransientStateFlags.StationaryStop
            | TransientStateFlags.StationaryStuck);
        body.TransientState |= framesStationaryFall switch
        {
            1 => TransientStateFlags.StationaryFall,
            2 => TransientStateFlags.StationaryStop,
            3 => TransientStateFlags.StationaryStuck,
            _ => TransientStateFlags.None,
        };
    }

    internal RuntimeSetPositionOutcome SubmitPreparedPlacement(
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command) =>
        SubmitPreparedPlacementCore(
            token,
            command,
            allowDirectUnsealed: token.PreparationKind
                is RuntimeEntityPlacementPreparationKind.LegacyDirect);

    private RuntimeSetPositionOutcome SubmitPreparedPlacementCore(
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command,
        bool allowDirectUnsealed)
    {
        EnsureNotDisposed();
        Operation? operation = null;
        bool ownsToken = token.IsValid
            && _operations.TryGetValue(token.Entity, out operation)
            && operation.Token == token;
        MoverPreparationAuthority exactAuthority = default;
        bool hasPreparationAuthority = token.IsValid
            && _moverPreparationAuthorities.TryGetValue(
                token.Entity,
                out exactAuthority)
            && exactAuthority.OperationId == token.OperationId;
        if (!ownsToken
            || operation is null
            || operation.Stage
                is not RuntimeEntityPlacementStage.AwaitingPreparation
            || !IsCurrent(operation)
            || operation.Record.PhysicsBody is not { } body
            || !double.IsFinite(command.GameTime)
            || command.Kind != operation.Kind
            || command.Portal != operation.Portal
            || (hasPreparationAuthority
                ? !exactAuthority.Prepared
                    || exactAuthority.PreparedCommand != command
                    || !IsPreparationAuthorityCurrent(
                        operation,
                        exactAuthority)
                : !allowDirectUnsealed)
            || (command.ExpectedVelocityAuthorityVersion != 0UL
                && operation.Record.VelocityAuthorityVersion
                    != command.ExpectedVelocityAuthorityVersion))
        {
            return Rejected(command.Physics);
        }

        operation.Body = body;
        operation.EnteringWorldFromCelllessResidence |=
            !body.InWorld || operation.Record.FullCellId == 0u;
        if (command.ExpectedVelocityAuthorityVersion != 0UL)
        {
            operation.SourceVelocityAuthorityVersion =
                command.ExpectedVelocityAuthorityVersion;
        }
        operation.PreviousContact = body.InContact;
        operation.PreviousOnWalkable = body.OnWalkable;
        var canonicalRequest = command.Physics with
        {
            Position = operation.WakeableLostCell
                ? operation.Result.Position
                : command.Physics.Position,
            Orientation = operation.WakeableLostCell
                ? operation.Result.Orientation
                : command.Physics.Orientation,
            CellId = operation.WakeableLostCell
                ? operation.ExactCellId
                : command.Physics.CellId,
            CellLocalPosition = operation.WakeableLostCell
                ? operation.Result.CellLocalPosition
                : command.Physics.CellLocalPosition,
            MoverPhysicsState = operation.Record.FinalPhysicsState,
            MovingEntityId = operation.Key.LocalEntityId,
            CurrentCellId = operation.WakeableLostCell
                ? null
                : body.InWorld
                    && operation.Record.FullCellId != 0u
                ? operation.Record.FullCellId
                : null,
        };
        var canonicalCommand = command with { Physics = canonicalRequest };
        operation.Command = canonicalCommand;
        if (hasPreparationAuthority)
        {
            _moverPreparationAuthorities[token.Entity] = exactAuthority with
            {
                PreparedCommand = canonicalCommand,
            };
        }

        if (operation.InheritedLostDeadline
            && !operation.WithdrawalAcknowledged
            && operation.ProjectionSequence != 0UL)
        {
            operation.PreparedCommandAwaitingWithdrawalAck =
                canonicalCommand;
            operation.Stage = RuntimeEntityPlacementStage
                .AwaitingWithdrawalAcknowledgement;
            return Outcome(
                RuntimeSetPositionStatus.DeferredCell,
                operation.Result,
                _pendingProjection.TryGetValue(
                    operation.ProjectionSequence,
                    out RuntimePlacementProjectionSnapshot pending)
                    ? pending.Token
                    : default);
        }

        if (operation.WakeableLostCell)
        {
            operation.RequiresPreparation = false;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingCell;
            if (operation.WithdrawalAcknowledged
                && operation.CollisionGenerationReady
                && operation.ProjectionSequence == 0UL)
            {
                RetryDeferred(operation);
            }
            return Outcome(
                RuntimeSetPositionStatus.DeferredCell,
                operation.Result,
                operation.ProjectionSequence != 0UL
                    && _pendingProjection.TryGetValue(
                        operation.ProjectionSequence,
                        out RuntimePlacementProjectionSnapshot pending)
                        ? pending.Token
                        : default);
        }

        if (!IsStructurallyValid(canonicalRequest))
        {
            PhysicsSetPositionResult invalid = InvalidResult(canonicalRequest);
            operation.Result = invalid;
            operation.RequiresPreparation = true;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
            return Outcome(RuntimeSetPositionStatus.Rejected, invalid, default);
        }

        if (TryGetBlockingQuiescence(
                canonicalRequest,
                out CollisionPrefixQuiescence? quiescence))
        {
            var deferred = new PhysicsSetPositionResult(
                PhysicsSetPositionError.Ok,
                PhysicsResidenceDisposition.DeferredCell,
                canonicalRequest.Position,
                canonicalRequest.Orientation,
                canonicalRequest.CellId,
                canonicalRequest.CellLocalPosition,
                InContact: operation.PreviousContact,
                OnWalkable: operation.PreviousOnWalkable,
                ContactPlane: body.ContactPlane,
                ContactPlaneCellId: body.ContactPlaneCellId,
                ContactPlaneIsWater: body.ContactPlaneIsWater,
                SlidingNormalValid: body.SlidingNormal != Vector3.Zero,
                SlidingNormal: body.SlidingNormal,
                FramesStationaryFall: body.FramesStationaryFall,
                CrossCellIds: ImmutableArray<uint>.Empty,
                CollidedObjectIds: ImmutableArray<uint>.Empty,
                QueriedCellIds: ImmutableArray<uint>.Empty);
            operation.Result = deferred;
            operation.RequiresPreparation = false;
            operation.ExactCellId = deferred.CellId;
            _preparedMovers[operation.Key] = canonicalRequest;
            return ParkDeferred(
                operation,
                deferred,
                collisionGenerationOverride:
                    quiescence!.Token.CollisionGeneration,
                collisionPrefixOverride:
                    quiescence.Token.LandblockPrefix,
                restorableOnCancel: true);
        }

        PhysicsSetPositionResult result;
        _collisionCallbackContexts.Push(new CollisionCallbackContext(
            operation.Record,
            operation.PositionAuthorityVersion,
            operation.SourceSpatialAuthorityVersion,
            operation.SourceVelocityAuthorityVersion,
            canonicalCommand.GameTime,
            operation.PreviousContact,
            operation.PreviousOnWalkable));
        try
        {
            result = _physics.Engine.SetPosition(
                canonicalRequest,
                _handleSetPositionCollisionsCallback);
        }
        finally
        {
            _collisionCallbackContexts.Pop();
        }
        if (!IsCurrentByToken(token.Entity, token, out operation))
            return Outcome(RuntimeSetPositionStatus.Cancelled, result, default);
        if (result.IsSuccessful
            && TryGetBlockingQuiescence(
                result,
                out CollisionPrefixQuiescence? queriedQuiescence))
        {
            PhysicsSetPositionResult held = result with
            {
                Residence = PhysicsResidenceDisposition.DeferredCell,
            };
            operation.Result = held;
            operation.RequiresPreparation = false;
            operation.ExactCellId = held.CellId;
            _preparedMovers[operation.Key] = canonicalRequest;
            return ParkDeferred(
                operation,
                held,
                collisionGenerationOverride:
                    queriedQuiescence!.Token.CollisionGeneration,
                collisionPrefixOverride:
                    queriedQuiescence.Token.LandblockPrefix,
                // Same reason as the pre-sweep park above, plus the swept
                // footprint: ResultTouchesPrefix scans every QueriedCellIds
                // entry, and that set spans NEIGHBOUR landblocks.
                restorableOnCancel: true);
        }
        operation.Result = result;
        if (!result.IsSuccessful)
        {
            operation.RequiresPreparation = true;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
            return Outcome(RuntimeSetPositionStatus.Rejected, result, default);
        }

        if (!IsCurrentByToken(token.Entity, token, out operation))
            return Outcome(RuntimeSetPositionStatus.Cancelled, result, default);
        operation.RequiresPreparation = false;
        operation.ExactCellId = result.CellId;
        _preparedMovers[operation.Key] = canonicalRequest;

        if (result.IsDeferred)
            return ParkDeferred(operation, result, restorableOnCancel: true);

        if (!CommitCanonical(operation, result))
        {
            PublishCancellation(CancelCore(token.Entity, token));
            return Outcome(RuntimeSetPositionStatus.Cancelled, result, default);
        }

        if (!_operations.TryGetValue(token.Entity, out Operation? stillOwns)
            || stillOwns.Token != token)
        {
            return Outcome(RuntimeSetPositionStatus.Cancelled, result, default);
        }

        operation.Stage = RuntimeEntityPlacementStage
            .AwaitingCommitAcknowledgement;
        RuntimePlacementProjectionToken projection = PublishProjection(
            operation,
            RuntimePlacementProjectionKind.Place,
            result);
        return Outcome(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            result,
            projection);
    }

    private KeyValuePair<ulong, RuntimePlacementProjectionSnapshot>
        FirstPendingProjection()
    {
        foreach (KeyValuePair<ulong, RuntimePlacementProjectionSnapshot> entry
                 in _pendingProjection)
        {
            return entry;
        }
        throw new InvalidOperationException(
            "FirstPendingProjection requires at least one pending entry.");
    }

    internal bool TryPeekProjection(
        out RuntimePlacementProjectionSnapshot projection)
    {
        EnsureNotDisposed();
        if (_pendingProjection.Count == 0)
        {
            projection = default;
            return false;
        }
        projection = FirstPendingProjection().Value;
        return true;
    }

    internal bool AcknowledgeProjection(
        in RuntimePlacementProjectionToken token)
    {
        EnsureNotDisposed();
        if (!token.IsValid
            || _pendingProjection.Count == 0
            || FirstPendingProjection().Key != token.Sequence
            || !_pendingProjection.TryGetValue(
                token.Sequence,
                out RuntimePlacementProjectionSnapshot pending)
            || pending.Token != token)
        {
            return false;
        }
        if (pending.Kind is RuntimePlacementProjectionKind.Discard
            or RuntimePlacementProjectionKind.ExecutorCompleted
            or RuntimePlacementProjectionKind.WithdrawalRestored)
        {
            _pendingProjection.Remove(token.Sequence);
            RetireQuiescenceProjectionSequence(token.Sequence);
            if (pending.Kind is RuntimePlacementProjectionKind.ExecutorCompleted)
            {
                _executorCompletionAcknowledged?.Invoke(
                    token.Entity,
                    token.Sequence);
            }
            return true;
        }
        if (!_operations.TryGetValue(
                token.Entity,
                out Operation? operation)
            || operation.ProjectionSequence != token.Sequence
            || !IsCurrent(operation)
            || operation.SpatialAuthorityVersion
                != token.SpatialAuthorityVersion)
        {
            return false;
        }

        _pendingProjection.Remove(token.Sequence);
        RetireQuiescenceProjectionSequence(token.Sequence);
        operation.ProjectionSequence = 0UL;
        if (pending.Kind is RuntimePlacementProjectionKind.Place)
        {
            if (_placementCompletionWatches.Remove(operation.Token))
            {
                _acknowledgedPlacementCompletions.Add(
                    operation.Token,
                    pending.Token);
            }
            operation.Stage = RuntimeEntityPlacementStage.AwaitingCommitAcknowledgement;
            _moverPreparationAuthorities.Remove(operation.Key);
            bool removed = _operations.Remove(operation.Key);
            if (removed)
                RetireOperationToPool(operation);
            return removed;
        }
        if (!operation.WakeableLostCell
            && !operation.InheritedLostDeadline)
        {
            _moverPreparationAuthorities.Remove(operation.Key);
            bool removed = _operations.Remove(operation.Key);
            if (removed)
                RetireOperationToPool(operation);
            return removed;
        }

        operation.WithdrawalAcknowledged = true;
        operation.Stage = operation.RequiresPreparation
                || operation.InheritedLostDeadline
            ? RuntimeEntityPlacementStage.AwaitingPreparation
            : operation.CollisionQuiescenceHeld
                ? RuntimeEntityPlacementStage.QuiescenceHeld
                : RuntimeEntityPlacementStage.AwaitingCell;
        if (operation.PreparedCommandAwaitingWithdrawalAck is { } prepared)
        {
            operation.PreparedCommandAwaitingWithdrawalAck = null;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
            _ = SubmitPreparedPlacement(operation.Token, prepared);
            return true;
        }
        if (operation.CollisionGenerationReady)
            RetryDeferred(operation);
        return true;
    }

    internal void TickLostCellDeadlines()
    {
        EnsureNotDisposed();
        if (_lostDeadlineNodes.Count == 0)
            return;
        double now = _physics.MonotonicNowSeconds;
        while (_lostDeadlineNodes.Count != 0)
        {
            LostDeadlineEntry entry = _lostDeadlineNodes[0];
            if (entry.Deadline > now)
                break;
            RemoveLostDeadlineNodeAt(0);
            if (!_lostDeadlines.Remove(entry.Key))
                throw new InvalidOperationException(
                    "The lost-cell deadline index diverged from its exact key owner.");
            if (_operations.TryGetValue(
                    entry.Key,
                    out Operation? operation))
                operation.Expired = true;
            if (_entities.TryGetByLocalId(
                    entry.Key.LocalEntityId,
                    out RuntimeEntityRecord record)
                && record.Key == entry.Key)
            {
                if (!_expiredLostCellNodes.ContainsKey(entry.Key))
                {
                    LinkedListNode<RuntimeEntityKey> node =
                        _expiredLostCells.AddLast(entry.Key);
                    _expiredLostCellNodes.Add(entry.Key, node);
                }
            }
        }
    }

    internal bool TryDequeueExpiredLostCell(out RuntimeEntityKey key)
    {
        EnsureNotDisposed();
        if (_expiredLostCells.First is not { } first)
        {
            key = default;
            return false;
        }
        key = first.Value;
        _expiredLostCells.RemoveFirst();
        _expiredLostCellNodes.Remove(key);
        return true;
    }

    internal bool Cancel(RuntimeEntityRecord record, bool publishWithdrawal)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return false;

        bool removed = CancelCoreDeferred(
            key,
            cancelLostFamily: false,
            preserveLostFamily: false,
            out RuntimePlacementProjectionSnapshot? discard);
        if (!publishWithdrawal || !_entities.IsCurrent(record))
        {
            if (discard is { } cancelled)
                PublishPlacement(cancelled);
            return removed;
        }

        LeaveWorldCanonical(record);
        if (record.PhysicsBody is null)
        {
            if (discard is { } cancelledBodyless)
                PublishPlacement(cancelledBodyless);
            return removed;
        }
        var operation = CreateWithdrawalOperation(record, key);
        _operations[key] = operation;
        RuntimeEntityPlacementToken capturedToken = operation.Token;
        if (discard is { } cancelledOld)
            PublishPlacement(cancelledOld);
        if (!IsCurrentByToken(key, capturedToken, out operation))
            return true;
        _ = PublishProjection(
            operation,
            RuntimePlacementProjectionKind.Withdraw,
            operation.Result);
        return true;
    }

    internal RuntimePlacementCancellationReceipt Forget(
        RuntimeEntityRecord record,
        bool releasePreparedMover = false,
        bool restoreCancelledPark = false)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        RuntimePlacementCancellationReceipt receipt = default;
        if (record.Key is { } key)
        {
            ParkWithdrawal withdrawal =
                restoreCancelledPark
                && _operations.TryGetValue(key, out Operation? parked)
                && parked.WakeableLostCell
                && ReferenceEquals(parked.Record, record)
                    ? parked.ParkWithdrawal
                    : default;
            receipt = CancelCore(key);
            if (releasePreparedMover)
                _preparedMovers.Remove(key);
            if (withdrawal.Captured)
                RestoreParkWithdrawal(record, withdrawal);
        }
        return receipt;
    }

    private void RestoreParkWithdrawal(
        RuntimeEntityRecord record,
        in ParkWithdrawal withdrawal)
    {
        if (!_entities.IsCurrent(record))
            return;
        uint residentCellId = 0u;
        if (record.PhysicsBody is { } body)
        {
            body.InWorld = withdrawal.InWorld;
            body.TransientState = withdrawal.TransientState;
            residentCellId = body.CellPosition.ObjCellId;
        }
        if (withdrawal.ClockActive)
            _entities.ResumeObjectClock(record);
        bool residencyRestored = false;
        if (residentCellId != 0u
            && !IsCollisionPrefixQuiescing(residentCellId)
            && record.FullCellId == 0u)
        {
            _entities.SetFullCell(
                record,
                residentCellId,
                (residentCellId & 0xFFFF0000u) | 0xFFFFu);
            _physics.AcknowledgeSpatialProjection(record, spatial: true);
            residencyRestored = true;
        }
        bool canonicallyWhole = record.FullCellId != 0u
            && record.PhysicsBody is { InWorld: true };
        if (canonicallyWhole)
            PublishWithdrawalRestoration(record);
        if (PhysicsDiagnostics.ProbeParkEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[park-restore] guid=0x{record.ServerGuid:X8} restoreCell=0x{residentCellId:X8} inWorld={withdrawal.InWorld} residency={residencyRestored} presentation={canonicallyWhole}"));
        }
    }

    internal void LeaveWorld(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        RuntimePlacementCancellationReceipt receipt = record.Key is { } key
            ? CancelCore(key)
            : default;
        if (_entities.IsCurrent(record))
            LeaveWorldCanonical(record);
        PublishCancellation(receipt);
    }

    internal bool IsDeferred(RuntimeEntityRecord record) =>
        record.Key is { } key
        && _operations.TryGetValue(key, out Operation? operation)
        && ReferenceEquals(operation.Record, record)
        && operation.WakeableLostCell;

    internal bool TryGetPreparedMoverSphereCount(
        RuntimeEntityRecord record,
        out int sphereCount)
    {
        EnsureNotDisposed();
        if (record.Key is { } key
            && _preparedMovers.TryGetValue(
                key,
                out PhysicsSetPositionRequest request))
        {
            sphereCount = request.Spheres.Length;
            return true;
        }
        sphereCount = 0;
        return false;
    }

    internal bool TryGetAwaitingPreparationToken(
        RuntimeEntityRecord record,
        out RuntimeEntityPlacementToken token)
    {
        EnsureNotDisposed();
        if (record.Key is { } key
            && _operations.TryGetValue(key, out Operation? operation)
            && ReferenceEquals(operation.Record, record)
            && operation.RequiresPreparation)
        {
            token = operation.Token;
            return true;
        }
        token = default;
        return false;
    }

    internal void ParkCollisionResidents(
        uint landblockId,
        bool includeOutdoorCells)
    {
        EnsureNotDisposed();
        uint prefix = landblockId & 0xFFFF0000u;
        var roots = new List<RuntimeEntityRecord>();
        _physics.CopySpatialRootsTo(roots);
        RuntimeEntityRecord[] affected = roots
            .Where(record => IsAffectedCollisionResident(
                record,
                prefix,
                includeOutdoorCells))
            .ToArray();
        for (int index = 0; index < affected.Length; index++)
        {
            if (affected[index].Key is { } key
                && _operations.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"Collision retirement for 0x{prefix:X8} cannot overlap active placement for 0x{affected[index].ServerGuid:X8}/{affected[index].Incarnation}.");
            }
        }
        _physics.CollisionReports.LeaveWorldBatch(affected);
        var stagedWithdrawals = new List<RuntimePlacementProjectionSnapshot>(
            roots.Count);
        for (int index = 0; index < roots.Count; index++)
        {
            RuntimeEntityRecord record = roots[index];
            uint cellId = record.FullCellId;
            if (!IsAffectedCollisionResident(
                    record,
                    prefix,
                    includeOutdoorCells)
                || record.Key is not { } key
                || record.PhysicsBody is not { } body
                || _operations.ContainsKey(key)
                || !affected.Any(candidate =>
                    ReferenceEquals(candidate, record)
                    && candidate.Key == key))
            {
                continue;
            }

            bool hasPrepared = _preparedMovers.TryGetValue(
                key,
                out PhysicsSetPositionRequest prepared);
            var request = (hasPrepared
                ? prepared
                : new PhysicsSetPositionRequest(
                    body.Position,
                    body.Orientation,
                    cellId,
                    body.CellPosition.Frame.Origin,
                    ImmutableArray<FlatCollisionSphere>.Empty,
                    1f,
                    0f,
                    0f)) with
            {
                Position = body.Position,
                Orientation = body.Orientation,
                CellId = cellId,
                CellLocalPosition = body.CellPosition.Frame.Origin,
                MoverPhysicsState = record.FinalPhysicsState,
                MovingEntityId = key.LocalEntityId,
                CurrentCellId = null,
            };
            var result = new PhysicsSetPositionResult(
                PhysicsSetPositionError.Ok,
                PhysicsResidenceDisposition.DeferredCell,
                body.Position,
                body.Orientation,
                cellId,
                body.CellPosition.Frame.Origin,
                InContact: body.InContact,
                OnWalkable: body.OnWalkable,
                ContactPlane: body.ContactPlane,
                ContactPlaneCellId: body.ContactPlaneCellId,
                ContactPlaneIsWater: body.ContactPlaneIsWater,
                SlidingNormalValid: body.SlidingNormal != Vector3.Zero,
                SlidingNormal: body.SlidingNormal,
                FramesStationaryFall: body.FramesStationaryFall,
                CrossCellIds: ImmutableArray<uint>.Empty,
                CollidedObjectIds: ImmutableArray<uint>.Empty);
            var command = new RuntimeSetPositionCommand(
                request,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                body.LastUpdateTime,
                record.VelocityAuthorityVersion);
            Operation operation = RentOperation();
            operation.Record = record;
            operation.Body = body;
            operation.Token = new RuntimeEntityPlacementToken(
                _entities.SessionLifetimeVersion,
                key,
                record.PositionAuthorityVersion,
                checked(++_nextOperationId),
                RuntimeEntityPlacementPreparationKind.AuthoredMover);
            operation.Key = key;
            operation.PositionAuthorityVersion = record.PositionAuthorityVersion;
            operation.SessionLifetimeVersion = _entities.SessionLifetimeVersion;
            operation.SourceSpatialAuthorityVersion =
                record.SpatialAuthorityVersion;
            operation.SourceVelocityAuthorityVersion =
                record.VelocityAuthorityVersion;
            operation.PreviousContact = body.InContact;
            operation.PreviousOnWalkable = body.OnWalkable;
            operation.Command = command;
            operation.Result = result;
            operation.SpatialAuthorityVersion = record.SpatialAuthorityVersion;
            operation.PlacementCommitVersion = record.PlacementCommitVersion;
            operation.ExactCellId = cellId;
            operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
            operation.Kind = RuntimeSetPositionOperationKind.RemoteAuthoritative;
            operation.Portal = default;
            operation.RequiresPreparation = !hasPrepared;
            _operations.Add(key, operation);
            _moverPreparationAuthorities[key] = CapturePreparationAuthority(
                operation,
                ServerPositionFrom(
                    cellId,
                    body.CellPosition.Frame.Origin,
                    body.Orientation),
                prepared: hasPrepared,
                command);
            CollisionPrefixQuiescence? quiescence =
                _collisionPrefixQuiescence.GetValueOrDefault(prefix);
            RuntimeSetPositionOutcome parked = ParkDeferred(
                operation,
                result,
                publishImmediately: false,
                collisionGenerationOverride:
                    quiescence?.Token.CollisionGeneration,
                collisionPrefixOverride:
                    quiescence?.Token.LandblockPrefix,
                restorableOnCancel: false);
            if (_pendingProjection.TryGetValue(
                    parked.Projection.Sequence,
                    out RuntimePlacementProjectionSnapshot staged))
            {
                stagedWithdrawals.Add(staged);
            }
            if (operation.RequiresPreparation)
            {
                operation.Stage = RuntimeEntityPlacementStage
                    .AwaitingPreparation;
            }
        }

        for (int index = 0; index < stagedWithdrawals.Count; index++)
        {
            RuntimePlacementProjectionSnapshot staged =
                stagedWithdrawals[index];
            if (_pendingProjection.TryGetValue(
                    staged.Token.Sequence,
                    out RuntimePlacementProjectionSnapshot current)
                && current == staged)
            {
                PublishPlacement(staged);
            }
        }
    }

    private bool IsAffectedCollisionResident(
        RuntimeEntityRecord record,
        uint prefix,
        bool includeOutdoorCells)
    {
        uint cellId = record.FullCellId;
        bool exactResidence = (cellId & 0xFFFF0000u) == prefix
            && (includeOutdoorCells || (cellId & 0xFFFFu) >= 0x0100u);
        return exactResidence
            && record.Key is not null
            && _entities.IsCurrent(record)
            && record.PhysicsBody is not null
            && _physics.IsSpatialRoot(record)
            && (record.FinalPhysicsState & PhysicsStateFlags.Static) == 0
            && !_entities.ParentAttachments.HasCommittedParent(
                record.ServerGuid);
    }

    private void ParkCollisionResidentsForQuiescence(
        CollisionPrefixQuiescence state)
    {
        if (!TryGetCurrentQuiescence(state.Token, out CollisionPrefixQuiescence? current)
            || !ReferenceEquals(current, state))
        {
            return;
        }
        ParkCollisionResidents(
            state.Token.LandblockPrefix,
            state.IncludeOutdoorCells);
    }

    private bool HasAffectedCollisionResident(
        uint prefix,
        bool includeOutdoorCells)
    {
        if (_physics.SpatialRootCount == 0)
            return false;
        var roots = new List<RuntimeEntityRecord>();
        _physics.CopySpatialRootsTo(roots);
        for (int index = 0; index < roots.Count; index++)
        {
            if (IsAffectedCollisionResident(
                    roots[index],
                    prefix,
                    includeOutdoorCells))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetCurrentQuiescence(
        in RuntimeCollisionPrefixQuiescenceToken token,
        out CollisionPrefixQuiescence? state)
    {
        if (token.IsValid
            && token.SessionLifetimeVersion == _entities.SessionLifetimeVersion
            && _collisionPrefixQuiescence.TryGetValue(
                token.LandblockPrefix,
                out state)
            && state.Token == token)
        {
            return true;
        }
        state = null;
        return false;
    }

    private bool HasPendingProjectionThrough(ulong barrierSequence) =>
        barrierSequence != 0UL
        && _pendingProjection.Count != 0
        && FirstPendingProjection().Key <= barrierSequence;

    private bool HasCollisionDispatchDebt()
    {
        RuntimeCollisionReportingOwnershipSnapshot reports =
            _physics.CollisionReports.CaptureOwnership();
        return reports.PendingReportCount != 0
            || reports.LeavingOwnerCount != 0
            || reports.AdmissionBlockedOwnerCount != 0
            || reports.PendingSetPositionDispatchCount != 0
            || reports.IsDispatching
            || _physics.Engine.ShadowObjects
                .PendingSetPositionDispatchCount != 0;
    }

    private bool HasOldPrefixPlacementDebt(CollisionPrefixQuiescence state)
    {
        uint prefix = state.Token.LandblockPrefix;
        foreach (Operation operation in _operations.Values)
        {
            if (operation.WakeableLostCell || operation.DormantLocalActivation)
                continue;
            if (PlacementTouchesPrefix(operation.Command.Physics, prefix)
                || ResultTouchesPrefix(operation.Result, prefix)
                || _moverPreparationAuthorities.TryGetValue(
                    operation.Key,
                    out MoverPreparationAuthority preparation)
                    && preparation.OperationId
                        == operation.Token.OperationId
                    && (preparation.AcceptedPosition.LandblockId
                        & 0xFFFF0000u) == prefix
                || IsAffectedCollisionResident(
                    operation.Record,
                    prefix,
                    state.IncludeOutdoorCells))
            {
                return true;
            }
        }
        return false;
    }

    private void CancelUnpreparedPrefixPlacementDebt(
        CollisionPrefixQuiescence state)
    {
        uint prefix = state.Token.LandblockPrefix;
        List<RuntimeEntityKey>? cancelled = null;
        foreach (Operation operation in _operations.Values)
        {
            if (operation.WakeableLostCell
                || operation.DormantLocalActivation
                || operation.Stage is not RuntimeEntityPlacementStage
                    .AwaitingPreparation
                || !_moverPreparationAuthorities.TryGetValue(
                    operation.Key,
                    out MoverPreparationAuthority preparation)
                || preparation.OperationId != operation.Token.OperationId
                || preparation.Prepared
                || !((preparation.AcceptedPosition.LandblockId
                        & 0xFFFF0000u) == prefix
                    || IsAffectedCollisionResident(
                        operation.Record,
                        prefix,
                        state.IncludeOutdoorCells)))
            {
                continue;
            }

            (cancelled ??= []).Add(operation.Key);
        }

        if (cancelled is null)
            return;

        for (int index = 0; index < cancelled.Count; index++)
        {
            _ = CancelCoreDeferred(
                cancelled[index],
                cancelLostFamily: false,
                preserveLostFamily: false,
                out RuntimePlacementProjectionSnapshot? discard);
            if (discard is { } projection)
                PublishPlacement(projection);
        }
    }

    private static bool PlacementTouchesPrefix(
        in PhysicsSetPositionRequest request,
        uint prefix) =>
        (request.CellId & 0xFFFF0000u) == prefix
        || request.CurrentCellId is uint current
            && (current & 0xFFFF0000u) == prefix;

    private static bool ResultTouchesPrefix(
        in PhysicsSetPositionResult result,
        uint prefix)
    {
        if ((result.CellId & 0xFFFF0000u) == prefix)
            return true;
        if (!result.QueriedCellIds.IsDefaultOrEmpty)
        {
            foreach (uint cellId in result.QueriedCellIds)
            {
                if ((cellId & 0xFFFF0000u) == prefix)
                    return true;
            }
        }
        return false;
    }

    private bool TryGetBlockingQuiescence(
        in PhysicsSetPositionRequest request,
        out CollisionPrefixQuiescence? state)
    {
        state = null;
        foreach (CollisionPrefixQuiescence candidate
                 in _collisionPrefixQuiescence.Values)
        {
            if (PlacementTouchesPrefix(
                    request,
                    candidate.Token.LandblockPrefix)
                && (state is null
                    || candidate.Token.OperationId
                        < state.Token.OperationId))
            {
                state = candidate;
            }
        }
        return state is not null;
    }

    private bool TryGetBlockingQuiescence(
        in PhysicsSetPositionResult result,
        out CollisionPrefixQuiescence? state,
        in RuntimeCollisionPrefixQuiescenceToken excluded = default)
    {
        state = null;
        foreach (CollisionPrefixQuiescence candidate
                 in _collisionPrefixQuiescence.Values)
        {
            if (excluded.IsValid && candidate.Token == excluded)
                continue;
            if (ResultTouchesPrefix(
                    result,
                    candidate.Token.LandblockPrefix)
                && (state is null
                    || candidate.Token.OperationId
                        < state.Token.OperationId))
            {
                state = candidate;
            }
        }
        return state is not null;
    }

    private void TrackQuiescenceWithdrawal(
        Operation operation,
        in RuntimePlacementProjectionSnapshot snapshot)
    {
        if (snapshot.Kind is not RuntimePlacementProjectionKind.Withdraw
            || !_collisionPrefixQuiescence.TryGetValue(
                operation.CollisionPrefix,
                out CollisionPrefixQuiescence? state)
            || snapshot.Token.CollisionGeneration
                != state.Token.CollisionGeneration)
        {
            return;
        }
        state.PendingWithdrawals[snapshot.Token.Sequence] = snapshot.Token;
        state.RetainedWithdrawals.Add(snapshot.Token);
        state.PermissionIssued = false;
    }

    private void TrackQuiescenceRestorePlacement(
        Operation operation,
        in RuntimePlacementProjectionSnapshot snapshot)
    {
        if (snapshot.Kind is not RuntimePlacementProjectionKind.Place
            || !_collisionPrefixQuiescence.TryGetValue(
                operation.CollisionPrefix,
                out CollisionPrefixQuiescence? state)
            || !state.ReleaseInProgress
            || !state.ReleaseGenerationReady)
        {
            return;
        }
        state.PendingRestorePlacements[snapshot.Token.Sequence] =
            snapshot.Token;
    }

    private void RetireQuiescenceProjectionSequence(ulong sequence)
    {
        foreach (CollisionPrefixQuiescence state
                 in _collisionPrefixQuiescence.Values)
        {
            if (state.PendingWithdrawals.TryGetValue(
                    sequence,
                    out _))
            {
                state.PendingWithdrawals.Remove(sequence);
                state.PermissionIssued = false;
                return;
            }
            if (state.PendingRestorePlacements.Remove(sequence))
                return;
        }
    }

    private void RemoveRetiredQuiescenceWithdrawals(
        CollisionPrefixQuiescence state)
    {
        if (state.PendingWithdrawals.Count == 0)
            return;
        ulong[] stale = state.PendingWithdrawals
            .Where(pair => !_pendingProjection.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();
        for (int index = 0; index < stale.Length; index++)
            state.PendingWithdrawals.Remove(stale[index]);
    }

    private void RebindQuiescedDeferredOperations(
        in RuntimeCollisionPrefixQuiescenceToken token,
        ulong successorGeneration,
        bool ready,
        bool releaseUnavailable = false)
    {
        var snapshot =
            new List<(RuntimeEntityKey Key, RuntimeEntityPlacementToken Token)>(
                _operations.Count);
        foreach (Operation existing in _operations.Values)
            snapshot.Add((existing.Key, existing.Token));
        foreach ((RuntimeEntityKey key, RuntimeEntityPlacementToken capturedToken)
                 in snapshot)
        {
            if (!IsCurrentByToken(key, capturedToken, out Operation? operation))
                continue;
            bool unavailableAfterReadyCommit = ready
                && operation.CollisionQuiescenceHeld
                && operation.CollisionGeneration == 0UL
                && operation.CollisionPrefix == token.LandblockPrefix;
            if (!operation.WakeableLostCell
                || !operation.CollisionQuiescenceHeld
                || operation.CollisionPrefix != token.LandblockPrefix
                || (operation.CollisionGeneration
                        != token.CollisionGeneration
                    && !unavailableAfterReadyCommit))
            {
                continue;
            }
            UnindexDeferred(operation);
            ulong reboundGeneration = releaseUnavailable
                || unavailableAfterReadyCommit
                    ? 0UL
                    : successorGeneration;
            operation.CollisionGeneration = reboundGeneration;
            operation.CollisionGenerationReady = ready
                && reboundGeneration != 0UL;
            if (releaseUnavailable || unavailableAfterReadyCommit)
            {
                operation.CollisionQuiescenceHeld = false;
                operation.Stage = operation.RequiresPreparation
                    ? RuntimeEntityPlacementStage.AwaitingPreparation
                    : RuntimeEntityPlacementStage.AwaitingCell;
            }
            if (reboundGeneration != 0UL)
                IndexDeferred(operation);
            else
                IndexUnboundDeferred(operation);
            if (operation.CollisionGenerationReady
                && operation.WithdrawalAcknowledged)
            {
                RetryDeferred(operation);
            }
        }
    }

    private bool HasQuiescedDeferredOperations(uint prefix)
    {
        foreach (Operation operation in _operations.Values)
        {
            if (operation.WakeableLostCell
                && operation.CollisionQuiescenceHeld
                && operation.CollisionPrefix == prefix)
            {
                return true;
            }
        }
        return false;
    }

    internal void BeginCollisionGeneration(uint landblockId, ulong generation)
    {
        EnsureNotDisposed();
        if (generation == 0UL)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[wake] begin lb=0x{landblockId:X8} gen={generation} unboundCells={_unboundDeferredCellOrder.Count} buckets={_deferredBucketOrder.Count}"));
        }
        uint prefix = landblockId & 0xFFFF0000u;
        for (int index = 0; index < _unboundDeferredCellOrder.Count;)
        {
            UnboundCellKey unboundKey = _unboundDeferredCellOrder[index];
            if (unboundKey.CollisionPrefix != prefix
                || !_unboundDeferredByCell.Remove(
                    unboundKey,
                    out List<RuntimeEntityKey>? retained))
            {
                index++;
                continue;
            }
            _unboundDeferredCellOrder.RemoveAt(index);
            var bucket = new CellGenerationKey(
                unboundKey.CellId,
                unboundKey.CollisionPrefix,
                generation);
            var rebound = new List<RuntimeEntityKey>(retained.Count);
            for (int entityIndex = 0;
                 entityIndex < retained.Count;
                 entityIndex++)
            {
                RuntimeEntityKey entity = retained[entityIndex];
                if (_operations.TryGetValue(entity, out Operation? operation)
                    && operation.WakeableLostCell
                    && operation.ExactCellId == unboundKey.CellId
                    && operation.CollisionPrefix == prefix
                    && operation.CollisionGeneration == 0UL)
                {
                    operation.CollisionGeneration = generation;
                    rebound.Add(entity);
                }
            }
            if (rebound.Count != 0)
            {
                if (_deferredByCellGeneration.TryGetValue(
                        bucket,
                        out List<RuntimeEntityKey>? futureBound))
                {
                    // The unbound survivors entered lost residence before
                    // entities indexed directly into this future generation.
                    // Preserve both append orders while restoring that age.
                    var merged = new List<RuntimeEntityKey>(
                        rebound.Count + futureBound.Count);
                    merged.AddRange(rebound);
                    merged.AddRange(futureBound);
                    _deferredByCellGeneration[bucket] = merged;
                }
                else
                {
                    _deferredByCellGeneration.Add(bucket, rebound);
                    _deferredBucketOrder.Add(bucket);
                }
            }
        }
        foreach (Operation operation in _operations.Values)
        {
            if (!operation.WakeableLostCell
                || operation.CollisionGeneration != 0UL
                || operation.CollisionPrefix != prefix)
            {
                continue;
            }
            operation.CollisionGeneration = generation;
            IndexDeferred(operation);
        }
    }

    internal void CancelCollisionGeneration(uint landblockId, ulong generation)
    {
        EnsureNotDisposed();
        if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[wake] cancel lb=0x{landblockId:X8} gen={generation} buckets={_deferredBucketOrder.Count}"));
        }
        if (_deferredBucketOrder.Count == 0)
            return;
        uint prefix = landblockId & 0xFFFF0000u;
        CellGenerationKey[] matching = _deferredBucketOrder
            .Where(key => key.CollisionGeneration == generation
                && key.CollisionPrefix == prefix)
            .ToArray();
        for (int index = 0; index < matching.Length; index++)
        {
            UnbindDeferredBucket(matching[index]);
        }
    }

    internal void CommitCollisionGeneration(
        uint landblockId,
        ulong generation,
        bool ready)
    {
        EnsureNotDisposed();
        if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[wake] commit lb=0x{landblockId:X8} gen={generation} ready={ready} buckets={_deferredBucketOrder.Count}"));
        }
        if (!ready || _deferredBucketOrder.Count == 0)
            return;

        uint prefix = landblockId & 0xFFFF0000u;
        var cells = new List<CellGenerationKey>();
        for (int index = 0; index < _deferredBucketOrder.Count; index++)
        {
            CellGenerationKey key = _deferredBucketOrder[index];
            if (key.CollisionGeneration == generation
                && key.CollisionPrefix == prefix)
            {
                cells.Add(key);
            }
        }
        foreach (CellGenerationKey cell in cells)
        {
            if (!_deferredByCellGeneration.TryGetValue(
                    cell,
                    out List<RuntimeEntityKey>? indexed))
            {
                continue;
            }
            RuntimeEntityKey[] exact = indexed.ToArray();
            if (!_physics.Engine.IsSpawnCellReady(cell.CellId))
            {
                if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"[wake] STRAND cell=0x{cell.CellId:X8} gen={cell.CollisionGeneration} spawnReady=false ops={exact.Length} -> unbound"));
                }
                UnbindDeferredBucket(cell);
                continue;
            }
            if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"[wake] wake cell=0x{cell.CellId:X8} gen={cell.CollisionGeneration} ops={exact.Length}"));
            }
            RemoveDeferredBucket(cell);
            foreach (RuntimeEntityKey entity in exact)
            {
                if (!_operations.TryGetValue(
                        entity,
                        out Operation? operation)
                    || !operation.WakeableLostCell
                    || operation.ExactCellId != cell.CellId
                    || operation.CollisionPrefix != prefix
                    || operation.CollisionGeneration != generation)
                {
                    continue;
                }
                operation.CollisionGenerationReady = true;
                if (Core.Physics.PhysicsDiagnostics.ProbeParkEnabled)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"[wake] op guid=0x{operation.Record.ServerGuid:X8} dormant={operation.DormantLocalActivation} ack={operation.WithdrawalAcknowledged} stage={operation.Stage} reqPrep={operation.RequiresPreparation} projSeq={operation.ProjectionSequence}"));
                }
                if (operation.WithdrawalAcknowledged)
                    RetryDeferred(operation);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        ClearOwnedState();
        _events = null;
        _disposed = true;
    }

    private void ClearOwnedState()
    {
        _operations.Clear();
        _deferredByCellGeneration.Clear();
        _deferredBucketOrder.Clear();
        _unboundDeferredByCell.Clear();
        _unboundDeferredCellOrder.Clear();
        _lostDeadlines.Clear();
        _lostDeadlineNodes.Clear();
        _lostDeadlineNodeIndex.Clear();
        _preparedMovers.Clear();
        _moverPreparationAuthorities.Clear();
        _placementCompletionWatches.Clear();
        _acknowledgedPlacementCompletions.Clear();
        _collisionPrefixQuiescence.Clear();
        _pendingProjection.Clear();
        _expiredLostCells.Clear();
        _expiredLostCellNodes.Clear();
        _operationPool.Clear();
    }

    private RuntimeSetPositionOutcome ParkDeferred(
        Operation operation,
        in PhysicsSetPositionResult result,
        bool publishImmediately = true,
        ulong? collisionGenerationOverride = null,
        uint? collisionPrefixOverride = null,
        bool restorableOnCancel = false)
    {
        PhysicsBody body = operation.Body!;
        bool priorInWorld = body.InWorld;
        TransientStateFlags priorTransientState = body.TransientState;
        bool priorClockActive = operation.Record.ObjectClock.IsActive;
        body.Orientation = result.Orientation;
        body.SnapToCell(
            result.CellId,
            result.Position,
            result.CellLocalPosition);
        uint parkedCellId = body.CellPosition.ObjCellId;
        operation.ParkWithdrawal = new ParkWithdrawal(
            Captured: restorableOnCancel
                && (parkedCellId == 0u
                    || !IsCollisionPrefixQuiescing(parkedCellId)),
            InWorld: priorInWorld,
            TransientState: priorTransientState,
            ClockActive: priorClockActive);
        if (PhysicsDiagnostics.ProbeParkEnabled)
        {
            string parkCause = collisionPrefixOverride is uint blockedPrefix
                ? FormattableString.Invariant($"quiescence:0x{blockedPrefix:X8}")
                : "unplaceable";
            Console.WriteLine(FormattableString.Invariant(
                $"[park] guid=0x{operation.Record.ServerGuid:X8} cause={parkCause} resultCell=0x{result.CellId:X8} restoreCell=0x{parkedCellId:X8} eligible={restorableOnCancel} captured={operation.ParkWithdrawal.Captured}"));
        }
        body.InWorld = false;
        body.TransientState &= ~TransientStateFlags.Active;
        if (operation.Record.RemoteMotion is IRuntimeRemotePlacement remote)
        {
            remote.LastServerPosition = result.Position;
            remote.LastServerPositionTime = _physics.UtcNowSeconds;
            remote.LastShadowSyncPosition = Vector3.Zero;
            remote.LastShadowSyncOrientation = Quaternion.Zero;
        }

        WithdrawCanonical(operation.Record);
        _entities.SuspendObjectClock(operation.Record);
        operation.SpatialAuthorityVersion =
            operation.Record.SpatialAuthorityVersion;
        _entities.AdvancePlacementCommit(operation.Record);
        operation.PlacementCommitVersion =
            operation.Record.PlacementCommitVersion;
        operation.ExactCellId = result.CellId;
        operation.WakeableLostCell = true;
        operation.EnteringWorldFromCelllessResidence = true;
        ArmLostFamilyDeadlines(operation);
        operation.CollisionGeneration = collisionGenerationOverride
            ?? _physics.ExpectedCollisionGeneration(result.CellId);
        operation.CollisionPrefix = collisionPrefixOverride
            ?? result.CellId & 0xFFFF0000u;
        operation.CollisionQuiescenceHeld = collisionPrefixOverride.HasValue;
        operation.Command = operation.Command with
        {
            Physics = operation.Command.Physics with
            {
                Position = result.Position,
                Orientation = result.Orientation,
                CellId = result.CellId,
                CellLocalPosition = result.CellLocalPosition,
                CurrentCellId = null,
            },
        };
        if (_moverPreparationAuthorities.ContainsKey(operation.Key))
        {
            RebindPreparedCommand(operation);
        }
        else
        {
            _moverPreparationAuthorities[operation.Key] =
                CapturePreparationAuthority(
                    operation,
                    ServerPositionFrom(
                        result.CellId,
                        result.CellLocalPosition,
                        result.Orientation),
                    prepared: true,
                    operation.Command);
        }
        IndexDeferred(operation);
        operation.Stage = operation.RequiresPreparation
            ? RuntimeEntityPlacementStage.AwaitingPreparation
            : operation.CollisionQuiescenceHeld
                ? RuntimeEntityPlacementStage.QuiescenceHeld
                : RuntimeEntityPlacementStage.AwaitingWithdrawalAcknowledgement;
        RuntimePlacementProjectionToken projection = PublishProjection(
            operation,
            RuntimePlacementProjectionKind.Withdraw,
            result,
            publishImmediately);
        return Outcome(
            RuntimeSetPositionStatus.DeferredCell,
            result,
            projection);
    }

    private void RetryDeferred(Operation operation)
    {
        if (operation.DormantLocalActivation)
            return;
        if (!IsCurrent(operation)
            || !operation.WakeableLostCell
            || operation.RequiresPreparation
            || operation.Expired
            || !operation.WithdrawalAcknowledged
            || !operation.CollisionGenerationReady
            || operation.ProjectionSequence != 0UL)
        {
            return;
        }

        if (!IsDeferredWakePreparationCurrent(operation))
            return;

        RuntimeEntityPlacementToken operationToken = operation.Token;
        RuntimeCollisionPrefixQuiescenceToken restoringQuiescence = default;
        if (TryGetBlockingQuiescence(
                operation.Command.Physics,
                out CollisionPrefixQuiescence? blocking))
        {
            if (blocking!.ReleaseInProgress
                && blocking.ReleaseGenerationReady
                && operation.CollisionQuiescenceHeld
                && operation.CollisionPrefix
                    == blocking.Token.LandblockPrefix)
            {
                restoringQuiescence = blocking.Token;
            }
            else
            {
                UnindexDeferred(operation);
                operation.CollisionPrefix = blocking.Token.LandblockPrefix;
                operation.CollisionGeneration = blocking.Token.CollisionGeneration;
                operation.CollisionGenerationReady = false;
                operation.CollisionQuiescenceHeld = true;
                operation.Stage = RuntimeEntityPlacementStage.QuiescenceHeld;
                IndexDeferred(operation);
                return;
            }
        }

        UnindexDeferred(operation);
        operation.CollisionQuiescenceHeld = false;
        operation.Command = operation.Command with
        {
            GameTime = _physics.PlacementSimulationTime(
                operation.Command.GameTime),
        };
        RebindPreparedCommand(operation);
        PhysicsSetPositionResult result;
        if (IsStructurallyValid(operation.Command.Physics))
        {
            _collisionCallbackContexts.Push(new CollisionCallbackContext(
                operation.Record,
                operation.PositionAuthorityVersion,
                operation.SpatialAuthorityVersion,
                operation.SourceVelocityAuthorityVersion,
                operation.Command.GameTime,
                operation.PreviousContact,
                operation.PreviousOnWalkable));
            try
            {
                result = _physics.Engine.SetPosition(
                    operation.Command.Physics,
                    _handleSetPositionCollisionsCallback);
            }
            finally
            {
                _collisionCallbackContexts.Pop();
            }
        }
        else
        {
            result = InvalidResult(operation.Command.Physics);
        }
        operation.CollisionGenerationReady = false;
        if (!IsCurrentByToken(
                operationToken.Entity,
                operationToken,
                out Operation? refreshed))
        {
            return;
        }
        operation = refreshed;
        if (result.IsSuccessful
            && TryGetBlockingQuiescence(
                result,
                out CollisionPrefixQuiescence? queriedQuiescence,
                restoringQuiescence))
        {
            PhysicsSetPositionResult held = result with
            {
                Residence = PhysicsResidenceDisposition.DeferredCell,
            };
            operation.Result = held;
            operation.ExactCellId = held.CellId;
            operation.CollisionPrefix =
                queriedQuiescence!.Token.LandblockPrefix;
            operation.CollisionGeneration =
                queriedQuiescence.Token.CollisionGeneration;
            operation.CollisionQuiescenceHeld = true;
            operation.Stage = RuntimeEntityPlacementStage.QuiescenceHeld;
            _preparedMovers[operation.Key] = operation.Command.Physics;
            IndexDeferred(operation);
            return;
        }
        if (result.IsDeferred)
        {
            operation.Result = result;
            operation.ExactCellId = result.CellId;
            _preparedMovers[operation.Key] = operation.Command.Physics;
            operation.CollisionPrefix = result.CellId & 0xFFFF0000u;
            operation.CollisionGeneration = _physics
                .ExpectedCollisionGeneration(result.CellId);
            IndexDeferred(operation);
            return;
        }
        if (!result.IsSuccessful)
        {
            if (result.Error is PhysicsSetPositionError.InvalidArguments)
            {
                operation.RequiresPreparation = true;
                operation.Stage = RuntimeEntityPlacementStage
                    .AwaitingPreparation;
                operation.CollisionPrefix = operation.ExactCellId
                    & 0xFFFF0000u;
                operation.CollisionGeneration = _physics
                    .ExpectedCollisionGeneration(operation.ExactCellId);
                IndexDeferred(operation);
            }
            else
            {
                operation.Stage = RuntimeEntityPlacementStage.AwaitingCell;
                operation.CollisionPrefix = operation.ExactCellId
                    & 0xFFFF0000u;
                operation.CollisionGeneration = _physics
                    .ExpectedCollisionGeneration(operation.ExactCellId);
                IndexDeferred(operation);
            }
            return;
        }
        if (!IsCurrentByToken(
                operationToken.Entity,
                operationToken,
                out Operation? stillCurrent))
        {
            return;
        }
        operation = stillCurrent;
        _preparedMovers[operation.Key] = operation.Command.Physics;
        if (!CommitCanonical(operation, result))
        {
            PublishCancellation(CancelCore(operationToken.Entity, operationToken));
            return;
        }

        if (!_operations.TryGetValue(
                operationToken.Entity,
                out Operation? stillOwns)
            || stillOwns.Token != operationToken)
        {
            return;
        }

        operation.Stage = RuntimeEntityPlacementStage
            .AwaitingCommitAcknowledgement;
        _ = PublishProjection(
            operation,
            RuntimePlacementProjectionKind.Place,
            result);
    }

    private bool CommitCanonical(
        Operation operation,
        in PhysicsSetPositionResult result)
    {
        if (!result.IsCommitted || !IsCurrent(operation))
            return false;
        RuntimeEntityRecord record = operation.Record;
        PhysicsBody body = operation.Body!;
        RuntimeEntityPlacementToken operationToken = operation.Token;
        RuntimeEntityKey operationKey = operation.Key;
        ulong positionAuthorityVersion = operation.PositionAuthorityVersion;
        ulong sourceVelocityAuthorityVersion =
            operation.SourceVelocityAuthorityVersion;
        double commandGameTime = operation.Command.GameTime;
        bool previousContact = operation.PreviousContact;
        bool previousOnWalkable = operation.PreviousOnWalkable;
        float shadowWorldOffsetX = operation.Command.ShadowWorldOffsetX;
        float shadowWorldOffsetY = operation.Command.ShadowWorldOffsetY;
        body.Orientation = result.Orientation;
        body.SnapToCell(
            result.CellId,
            result.Position,
            result.CellLocalPosition);
        bool isStatic = (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0;
        if (operation.EnteringWorldFromCelllessResidence)
        {
            body.LastUpdateTime = commandGameTime;
            _entities.ResetObjectClockForEnterWorld(record, isStatic);
        }
        if (operation.EnteringWorldFromCelllessResidence && !isStatic)
            body.TransientState |= TransientStateFlags.Active;
        body.ContactPlaneValid = result.InContact;
        body.ContactPlane = result.ContactPlane;
        body.ContactPlaneCellId = result.ContactPlaneCellId;
        body.ContactPlaneIsWater = result.ContactPlaneIsWater;
        if (result.InContact)
            body.GroundNormal = result.ContactPlane.Normal;
        body.SlidingNormal = result.SlidingNormal;
        if (result.SlidingNormalValid)
            body.TransientState |= TransientStateFlags.Sliding;
        else
            body.TransientState &= ~TransientStateFlags.Sliding;
        IRuntimeRemotePlacement? remote =
            record.RemoteMotion as IRuntimeRemotePlacement;
        if (record.FullCellId != result.CellId)
        {
            _entities.SetFullCell(
                record,
                result.CellId,
                (result.CellId & 0xFFFF0000u) | 0xFFFFu);
        }
        operation.SpatialAuthorityVersion = record.SpatialAuthorityVersion;
        ulong spatialAuthorityVersion = record.SpatialAuthorityVersion;
        _entities.AdvancePlacementCommit(record);
        operation.PlacementCommitVersion = record.PlacementCommitVersion;
        ulong canonicalCommitVersion = record.PlacementCommitVersion;
        if (remote is not null)
        {
            remote.CellId = result.CellId;
            remote.LastServerPosition = result.Position;
            remote.LastServerPositionTime = _physics.UtcNowSeconds;
            remote.LastShadowSyncPosition = result.Position;
            remote.LastShadowSyncOrientation = result.Orientation;
        }

        uint committedCellId = result.CellId;
        bool collidedWithEnvironment = result.CollidedWithEnvironment;
        System.Collections.Immutable.ImmutableArray<uint> collidedObjectIds =
            result.CollidedObjectIds;
        if (!IsCanonicalPlacementCommitCurrent(
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                canonicalCommitVersion,
                committedCellId,
                requireSpatialRoot: false))
            return false;
        bool contactCommitted;
        if (remote is null)
        {
            contactCommitted = PhysicsObjUpdate.CommitSetPositionContactTransition(
                body,
                result.InContact,
                result.OnWalkable,
                previousOnWalkable);
        }
        else
        {
            var guard = new ContactCommitGuard(
                this,
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                canonicalCommitVersion,
                committedCellId);
            contactCommitted = PhysicsObjUpdate.CommitSetPositionContactTransition(
                body,
                result.InContact,
                result.OnWalkable,
                previousOnWalkable,
                remote.HitGround,
                remote.LeaveGround,
                guard.IsCurrent);
        }
        if (!contactCommitted
            || !IsCanonicalPlacementCommitCurrent(
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                canonicalCommitVersion,
                committedCellId,
                requireSpatialRoot: false))
        {
            return false;
        }
        bool reportingCurrent = !IsCollisionReportingEligible(record, body)
            || _physics.HandleSetPositionCollisionReports(
                record,
                positionAuthorityVersion,
                spatialAuthorityVersion,
                commandGameTime,
                previousContact,
                previousOnWalkable,
                collidedWithEnvironment,
                collidedObjectIds,
                out _);
        if (!reportingCurrent
            || !IsCanonicalPlacementCommitCurrent(
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                canonicalCommitVersion,
                committedCellId,
                requireSpatialRoot: false))
            return false;
        body.FramesStationaryFall = result.FramesStationaryFall;
        if (IsVelocityCurrent(sourceVelocityAuthorityVersion, record))
        {
            PhysicsObjUpdate.HandleAllCollisions(
                body,
                result.CollisionNormalValid,
                result.CollisionNormal,
                previousContact,
                previousOnWalkable,
                body.OnWalkable);
        }
        body.TransientState &= ~(TransientStateFlags.StationaryFall
            | TransientStateFlags.StationaryStop
            | TransientStateFlags.StationaryStuck);
        body.TransientState |= result.FramesStationaryFall switch
        {
            1 => TransientStateFlags.StationaryFall,
            2 => TransientStateFlags.StationaryStop,
            3 => TransientStateFlags.StationaryStuck,
            _ => TransientStateFlags.None,
        };
        if (remote is not null)
            remote.Airborne = !body.OnWalkable;
        if (!IsCanonicalPlacementCommitCurrent(
                positionAuthorityVersion,
                spatialAuthorityVersion,
                record,
                body,
                canonicalCommitVersion,
                committedCellId,
                requireSpatialRoot: false))
            return false;

        _physics.Engine.ShadowObjects.CommitSetPosition(
            operationKey.LocalEntityId,
            result.Position,
            result.Orientation,
            result.CellId,
            shadowWorldOffsetX,
            shadowWorldOffsetY,
            result.ShadowAction,
            result.CrossCellIds);
        _physics.AcknowledgeSpatialProjection(record, spatial: true);

        if (_operations.TryGetValue(operationKey, out Operation? currentOperation)
            && currentOperation.Token == operationToken)
        {
            currentOperation.ExactCellId = result.CellId;
            currentOperation.Result = result;
            currentOperation.WakeableLostCell = false;
            currentOperation.EnteringWorldFromCelllessResidence = false;
            CancelLostFamilyDeadlines(currentOperation);
        }

        return IsCanonicalPlacementCommitCurrent(
            positionAuthorityVersion,
            spatialAuthorityVersion,
            record,
            body,
            canonicalCommitVersion,
            committedCellId,
            requireSpatialRoot: true);
    }

    private bool IsCanonicalPlacementCommitCurrent(
        ulong positionAuthorityVersion,
        ulong spatialAuthorityVersion,
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong placementCommitVersion,
        uint fullCellId,
        bool requireSpatialRoot) =>
        _entities.IsCurrent(record)
        && ReferenceEquals(record.PhysicsBody, body)
        && record.PositionAuthorityVersion == positionAuthorityVersion
        && record.SpatialAuthorityVersion == spatialAuthorityVersion
        && record.PlacementCommitVersion == placementCommitVersion
        && record.FullCellId == fullCellId
        && (!requireSpatialRoot || _physics.IsSpatialRoot(record));

    private bool IsCollisionReportingEligible(
        RuntimeEntityRecord record,
        PhysicsBody body) =>
        _entities.IsCurrent(record)
        && ReferenceEquals(record.PhysicsBody, body)
        && body.InWorld
        && (body.State & PhysicsStateFlags.Hidden) == 0;

    private void WithdrawCanonical(RuntimeEntityRecord record)
    {
        _physics.CollisionReports.LeaveWorld(record);
        _physics.RemoveSpatialProjection(record);
        if (record.Key is { } key)
            _physics.Engine.ShadowObjects.Suspend(key.LocalEntityId);
        if (record.FullCellId != 0u)
            _entities.SetFullCell(record, 0u, 0u);
    }

    private void LeaveWorldCanonical(RuntimeEntityRecord record)
    {
        WithdrawCanonical(record);
        if (record.PhysicsBody is not { } body)
        {
            _entities.SuspendObjectClock(record);
            _entities.AdvancePlacementCommit(record);
            return;
        }
        body.SnapToCell(
            0u,
            body.Position,
            body.CellPosition.Frame.Origin);
        body.InWorld = false;
        body.TransientState = TransientStateFlags.None;
        body.ContactPlaneValid = false;
        body.ContactPlaneCellId = 0u;
        body.ContactPlaneIsWater = false;
        body.SlidingNormal = Vector3.Zero;
        body.FramesStationaryFall = 0;
        body.calc_acceleration();
        if (record.RemoteMotion is IRuntimeRemotePlacement remote)
            remote.CellId = 0u;
        _entities.SuspendObjectClock(record);
        _entities.AdvancePlacementCommit(record);
    }

    private RuntimePlacementProjectionToken PublishProjection(
        Operation operation,
        RuntimePlacementProjectionKind kind,
        in PhysicsSetPositionResult result,
        bool publishImmediately = true)
    {
        if (operation.ProjectionSequence != 0UL)
            _pendingProjection.Remove(operation.ProjectionSequence);
        ulong sequence = checked(++_nextProjectionSequence);
        var token = new RuntimePlacementProjectionToken(
            sequence,
            Revision: 1UL,
            operation.Key,
            operation.PositionAuthorityVersion,
            operation.SpatialAuthorityVersion,
            operation.PlacementCommitVersion,
            operation.SessionLifetimeVersion,
            operation.ExactCellId,
            operation.CollisionGeneration,
            operation.Command.Portal);
        var snapshot = new RuntimePlacementProjectionSnapshot(
            token,
            kind,
            result.Position,
            result.Orientation,
            result.CellLocalPosition,
            result.InContact,
            result.OnWalkable);
        operation.ProjectionSequence = sequence;
        _pendingProjection.Add(sequence, snapshot);
        TrackQuiescenceWithdrawal(operation, snapshot);
        TrackQuiescenceRestorePlacement(operation, snapshot);
        if (publishImmediately)
            PublishPlacement(snapshot);
        return token;
    }

    private Operation CreateWithdrawalOperation(
        RuntimeEntityRecord record,
        RuntimeEntityKey key)
    {
        PhysicsBody body = record.PhysicsBody
            ?? throw new InvalidOperationException(
                "A withdrawal projection requires the canonical PhysicsBody.");
        var physics = new PhysicsSetPositionRequest(
            body.Position,
            body.Orientation,
            body.CellPosition.ObjCellId,
            body.CellPosition.Frame.Origin,
            ImmutableArray<FlatCollisionSphere>.Empty,
            1f,
            0f,
            0f);
        var result = new PhysicsSetPositionResult(
            PhysicsSetPositionError.Ok,
            PhysicsResidenceDisposition.DeferredCell,
            body.Position,
            body.Orientation,
            body.CellPosition.ObjCellId,
            body.CellPosition.Frame.Origin,
            CrossCellIds: ImmutableArray<uint>.Empty,
            CollidedObjectIds: ImmutableArray<uint>.Empty);
        Operation operation = RentOperation();
        operation.Record = record;
        operation.Body = body;
        operation.Token = new RuntimeEntityPlacementToken(
            _entities.SessionLifetimeVersion,
            key,
            record.PositionAuthorityVersion,
            checked(++_nextOperationId),
            RuntimeEntityPlacementPreparationKind.LegacyDirect);
        operation.Key = key;
        operation.PositionAuthorityVersion = record.PositionAuthorityVersion;
        operation.SessionLifetimeVersion = _entities.SessionLifetimeVersion;
        operation.SourceSpatialAuthorityVersion = record.SpatialAuthorityVersion;
        operation.SourceVelocityAuthorityVersion = record.VelocityAuthorityVersion;
        operation.PreviousContact = body.InContact;
        operation.PreviousOnWalkable = body.OnWalkable;
        operation.Command = new RuntimeSetPositionCommand(
            physics,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            body.LastUpdateTime,
            record.VelocityAuthorityVersion,
            ShadowWorldOffsetX: 0f,
            ShadowWorldOffsetY: 0f);
        operation.Result = result;
        operation.SpatialAuthorityVersion = record.SpatialAuthorityVersion;
        operation.PlacementCommitVersion = record.PlacementCommitVersion;
        operation.ExactCellId = result.CellId;
        operation.WakeableLostCell = false;
        operation.Stage = RuntimeEntityPlacementStage
            .AwaitingWithdrawalAcknowledgement;
        operation.Kind = RuntimeSetPositionOperationKind.RemoteAuthoritative;
        operation.Portal = default;
        return operation;
    }

    private bool IsCurrent(Operation operation) =>
        _operations.TryGetValue(operation.Key, out Operation? current)
        && ReferenceEquals(current, operation)
        && IsOperationStateConsistent(operation);

    private bool IsOperationStateConsistent(Operation operation) =>
        _entities.SessionLifetimeVersion == operation.SessionLifetimeVersion
        && _entities.IsCurrent(operation.Record)
        && operation.Record.Key == operation.Key
        && (operation.Body is null
            || ReferenceEquals(operation.Record.PhysicsBody, operation.Body))
        && operation.Record.PositionAuthorityVersion
            == operation.PositionAuthorityVersion
        && operation.Record.SpatialAuthorityVersion
            == operation.SpatialAuthorityVersion
        && operation.Record.PlacementCommitVersion
            == operation.PlacementCommitVersion;

    private bool IsCurrentByToken(
        RuntimeEntityKey key,
        in RuntimeEntityPlacementToken capturedToken,
        [NotNullWhen(true)] out Operation? operation)
    {
        if (_operations.TryGetValue(key, out Operation? current)
            && current.Token == capturedToken
            && IsOperationStateConsistent(current))
        {
            operation = current;
            return true;
        }
        operation = null;
        return false;
    }

    private bool IsExactDormantLocalActivationCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        in RuntimeEntityPlacementToken token,
        in RuntimeSetPositionCommand command,
        out Operation? operation,
        bool allowCanonicalCommand = false,
        bool allowDeferredLease = false)
    {
        operation = null;
        if (!token.IsValid
            || token.Entity != record.Key
            || token.PreparationKind
                is not RuntimeEntityPlacementPreparationKind.AuthoredMover
            || command.Kind is not (RuntimeSetPositionOperationKind.InitialLogin
                or RuntimeSetPositionOperationKind.LocalAuthoritative)
            || command.Portal != default
                && !command.Portal.IsValid
            || !_operations.TryGetValue(token.Entity, out operation)
            || operation.Token != token
            || operation.Stage
                is not RuntimeEntityPlacementStage.AwaitingPreparation
                && !(allowDeferredLease
                    && operation.DormantLocalActivation
                    && (operation.Stage
                            is RuntimeEntityPlacementStage.AwaitingCell
                        && operation.WakeableLostCell
                        || operation.Stage is RuntimeEntityPlacementStage
                            .AwaitingFinalShadowPreparation)
                    && operation.ProjectionSequence == 0UL)
            || !ReferenceEquals(operation.Record, record)
            || !IsCurrent(operation)
            || !ReferenceEquals(record.PhysicsBody, body)
            || body.InWorld
            || (body.TransientState & TransientStateFlags.Active) != 0
            || record.PhysicsHost is not null
            || record.RemoteMotion is not null
            || record.Projectile is not null
            || record.PhysicsBodyAcquisitionInProgress
            || record.RemoteMotionBindingInProgress
            || record.ProjectileBindingInProgress
            || record.RequiresRemotePlacementRuntime
            || record.DeleteAcceptedForTeardown
            || _physics.IsSpatialRoot(record)
            || !_moverPreparationAuthorities.TryGetValue(
                token.Entity,
                out MoverPreparationAuthority authority)
            || authority.OperationId != token.OperationId
            || !authority.Prepared
            || !IsPreparationAuthorityCurrent(operation, authority))
        {
            operation = null;
            return false;
        }

        if (authority.PreparedCommand == command)
            return true;
        if (!allowCanonicalCommand)
            return false;

        RuntimeSetPositionCommand authored = authority.PreparedCommand;
        return authored with
        {
            Physics = authored.Physics with
            {
                MoverPhysicsState = record.FinalPhysicsState,
                MovingEntityId = token.Entity.LocalEntityId,
                CurrentCellId = null,
            },
        } == command;
    }

    private bool IsVelocityCurrent(Operation operation) =>
        IsVelocityCurrent(
            operation.SourceVelocityAuthorityVersion,
            operation.Record);

    private static bool IsVelocityCurrent(
        ulong sourceVelocityAuthorityVersion,
        RuntimeEntityRecord record) =>
        sourceVelocityAuthorityVersion == 0UL
        || record.VelocityAuthorityVersion == sourceVelocityAuthorityVersion;

    private static MoverPreparationAuthority CapturePreparationAuthority(
        Operation operation,
        in CreateObject.ServerPosition acceptedPosition,
        bool prepared,
        in RuntimeSetPositionCommand preparedCommand = default) => new(
            operation.Token.OperationId,
            acceptedPosition,
            CanonicalSetupTableId(operation.Record),
            operation.Record.PositionAuthorityVersion,
            operation.Record.VelocityAuthorityVersion,
            operation.Record.StateAuthorityVersion,
            operation.Record.VectorAuthorityVersion,
            operation.Record.ObjDescAuthorityVersion,
            operation.Record.CreateIntegrationVersion,
            operation.Record.PhysicsStateMutationVersion,
            prepared,
            preparedCommand);

    private static uint CanonicalSetupTableId(RuntimeEntityRecord record) =>
        record.Snapshot.Physics?.SetupTableId
            ?? record.Snapshot.SetupTableId
            ?? 0u;

    private static CreateObject.ServerPosition ServerPositionFrom(
        uint cellId,
        Vector3 cellLocal,
        Quaternion orientation) => new(
            cellId,
            cellLocal.X,
            cellLocal.Y,
            cellLocal.Z,
            orientation.W,
            orientation.X,
            orientation.Y,
            orientation.Z);

    private static bool IsPreparationAuthorityCurrent(
        Operation operation,
        in MoverPreparationAuthority authority) =>
        operation.Record.PositionAuthorityVersion
            == authority.PositionAuthorityVersion
        && operation.Record.VelocityAuthorityVersion
            == authority.VelocityAuthorityVersion
        && operation.Record.StateAuthorityVersion
            == authority.StateAuthorityVersion
        && operation.Record.VectorAuthorityVersion
            == authority.VectorAuthorityVersion
        && operation.Record.ObjDescAuthorityVersion
            == authority.ObjDescAuthorityVersion
        && operation.Record.CreateIntegrationVersion
            == authority.CreateIntegrationVersion
        && operation.Record.PhysicsStateMutationVersion
            == authority.PhysicsStateMutationVersion;

    private void RebindPreparedCommand(Operation operation)
    {
        if (_moverPreparationAuthorities.TryGetValue(
                operation.Key,
                out MoverPreparationAuthority authority)
            && authority.OperationId == operation.Token.OperationId
            && authority.Prepared)
        {
            _moverPreparationAuthorities[operation.Key] = authority with
            {
                PreparedCommand = operation.Command,
            };
        }
    }

    private bool IsDeferredWakePreparationCurrent(Operation operation)
    {
        if (_moverPreparationAuthorities.TryGetValue(
                operation.Key,
                out MoverPreparationAuthority authority)
            && authority.OperationId == operation.Token.OperationId
            && authority.Prepared
            && authority.PreparedCommand == operation.Command
            && IsPreparationAuthorityCurrent(operation, authority))
        {
            return true;
        }

        operation.SourceVelocityAuthorityVersion =
            operation.Record.VelocityAuthorityVersion;
        operation.RequiresPreparation = true;
        operation.Stage = RuntimeEntityPlacementStage.AwaitingPreparation;
        operation.PreparedCommandAwaitingWithdrawalAck = null;
        _moverPreparationAuthorities[operation.Key] =
            CapturePreparationAuthority(
                operation,
                ServerPositionFrom(
                    operation.ExactCellId,
                    operation.Result.CellLocalPosition,
                    operation.Result.Orientation),
                prepared: false);
        return false;
    }

    internal void PublishCancellation(
        in RuntimePlacementCancellationReceipt receipt)
    {
        if (!receipt.IsValid)
            return;
        RuntimePlacementProjectionSnapshot projection = receipt.Projection;
        if (_pendingProjection.TryGetValue(
                projection.Token.Sequence,
                out RuntimePlacementProjectionSnapshot current)
            && current == projection)
        {
            PublishPlacement(projection);
        }
    }

    private RuntimePlacementCancellationReceipt CancelCore(
        RuntimeEntityKey key)
    {
        _ = CancelCoreDeferred(
            key,
            cancelLostFamily: false,
            preserveLostFamily: false,
            out RuntimePlacementProjectionSnapshot? discard);
        return discard is { } projection
            ? new RuntimePlacementCancellationReceipt(projection)
            : default;
    }

    private bool CancelCoreDeferred(
        RuntimeEntityKey key,
        bool cancelLostFamily,
        bool preserveLostFamily,
        out RuntimePlacementProjectionSnapshot? discard)
    {
        discard = null;
        if (!preserveLostFamily)
            CancelExactLostKey(key);
        if (!_operations.Remove(key, out Operation? operation))
            return false;
        ForgetPlacementCompletionCore(operation.Token);
        _moverPreparationAuthorities.Remove(key);
        UnindexDeferred(operation);
        if (!preserveLostFamily && cancelLostFamily)
            CancelLostFamilyDeadlines(operation);
        if (operation.ProjectionSequence != 0UL
            && _pendingProjection.TryGetValue(
                operation.ProjectionSequence,
                out RuntimePlacementProjectionSnapshot pending))
        {
            RuntimePlacementProjectionSnapshot cancelled = pending with
            {
                Token = pending.Token with
                {
                    Revision = checked(pending.Token.Revision + 1UL),
                },
                Kind = RuntimePlacementProjectionKind.Discard,
            };
            _pendingProjection[operation.ProjectionSequence] = cancelled;
            if (pending.Kind is RuntimePlacementProjectionKind.Withdraw)
            {
                foreach (CollisionPrefixQuiescence state
                         in _collisionPrefixQuiescence.Values)
                {
                    if (state.PendingWithdrawals.ContainsKey(
                            operation.ProjectionSequence))
                    {
                        state.PendingWithdrawals[
                            operation.ProjectionSequence] = cancelled.Token;
                        for (int retainedIndex = 0;
                             retainedIndex < state.RetainedWithdrawals.Count;
                             retainedIndex++)
                        {
                            if (state.RetainedWithdrawals[retainedIndex].Sequence
                                == operation.ProjectionSequence)
                            {
                                state.RetainedWithdrawals[retainedIndex] =
                                    cancelled.Token;
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            operation.Stage = RuntimeEntityPlacementStage
                .CancelledAwaitingAcknowledgement;
            discard = cancelled;
        }
        RetireOperationToPool(operation);
        return true;
    }

    private RuntimePlacementCancellationReceipt CancelCore(
        RuntimeEntityKey key,
        in RuntimeEntityPlacementToken expectedToken,
        bool preserveLostFamily = false,
        bool restoreCancelledPark = false)
    {
        if (!_operations.TryGetValue(key, out Operation? current)
            || current.Token != expectedToken)
        {
            return default;
        }
        RuntimeEntityRecord parkedRecord = current.Record;
        ParkWithdrawal withdrawal =
            restoreCancelledPark && current.WakeableLostCell
                ? current.ParkWithdrawal
                : default;
        _ = CancelCoreDeferred(
            key,
            cancelLostFamily: false,
            preserveLostFamily,
            out RuntimePlacementProjectionSnapshot? discard);
        if (withdrawal.Captured)
            RestoreParkWithdrawal(parkedRecord, withdrawal);
        return discard is { } projection
            ? new RuntimePlacementCancellationReceipt(projection)
            : default;
    }

    private void ForgetPlacementCompletionCore(
        in RuntimeEntityPlacementToken token)
    {
        if (!token.IsValid)
            return;
        _placementCompletionWatches.Remove(token);
        _acknowledgedPlacementCompletions.Remove(token);
    }

    private void IndexDeferred(Operation operation)
    {
        if (operation.ExactCellId == 0u
            || operation.CollisionGeneration == 0UL)
        {
            return;
        }
        var bucket = new CellGenerationKey(
            operation.ExactCellId,
            operation.CollisionPrefix,
            operation.CollisionGeneration);
        if (!_deferredByCellGeneration.TryGetValue(
                bucket,
                out List<RuntimeEntityKey>? entities))
        {
            entities = [];
            _deferredByCellGeneration.Add(bucket, entities);
            _deferredBucketOrder.Add(bucket);
        }
        if (!entities.Contains(operation.Key))
            entities.Add(operation.Key);
    }

    private void IndexUnboundDeferred(Operation operation)
    {
        if (operation.ExactCellId == 0u)
            return;
        var key = new UnboundCellKey(
            operation.ExactCellId,
            operation.CollisionPrefix);
        if (!_unboundDeferredByCell.TryGetValue(
                key,
                out List<RuntimeEntityKey>? entities))
        {
            entities = [];
            _unboundDeferredByCell.Add(key, entities);
            _unboundDeferredCellOrder.Add(key);
        }
        if (!entities.Contains(operation.Key))
            entities.Add(operation.Key);
    }

    private void UnindexDeferred(Operation operation)
    {
        if (operation.ExactCellId == 0u)
        {
            return;
        }
        if (operation.CollisionGeneration == 0UL)
        {
            var key = new UnboundCellKey(
                operation.ExactCellId,
                operation.CollisionPrefix);
            if (_unboundDeferredByCell.TryGetValue(
                    key,
                    out List<RuntimeEntityKey>? unbound))
            {
                int unboundIndex = unbound.IndexOf(operation.Key);
                if (unboundIndex >= 0)
                {
                    int last = unbound.Count - 1;
                    unbound[unboundIndex] = unbound[last];
                    unbound.RemoveAt(last);
                }
                if (unbound.Count == 0)
                {
                    _unboundDeferredByCell.Remove(key);
                    _unboundDeferredCellOrder.Remove(key);
                }
            }
            return;
        }
        var bucket = new CellGenerationKey(
            operation.ExactCellId,
            operation.CollisionPrefix,
            operation.CollisionGeneration);
        if (_deferredByCellGeneration.TryGetValue(
                bucket,
                out List<RuntimeEntityKey>? entities))
        {
            int index = entities.IndexOf(operation.Key);
            if (index >= 0)
            {
                int last = entities.Count - 1;
                entities[index] = entities[last];
                entities.RemoveAt(last);
            }
            if (entities.Count == 0)
                RemoveDeferredBucket(bucket);
        }
    }

    private void UnbindDeferredBucket(CellGenerationKey bucket)
    {
        if (!_deferredByCellGeneration.TryGetValue(
                bucket,
                out List<RuntimeEntityKey>? entities))
        {
            return;
        }
        RemoveDeferredBucket(bucket);
        var unboundKey = new UnboundCellKey(
            bucket.CellId,
            bucket.CollisionPrefix);
        if (!_unboundDeferredByCell.TryGetValue(
                unboundKey,
                out List<RuntimeEntityKey>? unbound))
        {
            unbound = [];
            _unboundDeferredByCell.Add(unboundKey, unbound);
            _unboundDeferredCellOrder.Add(unboundKey);
        }
        for (int index = 0; index < entities.Count; index++)
        {
            RuntimeEntityKey key = entities[index];
            if (!_operations.TryGetValue(key, out Operation? operation)
                || !operation.WakeableLostCell
                || operation.ExactCellId != bucket.CellId
                || operation.CollisionPrefix != bucket.CollisionPrefix
                || operation.CollisionGeneration != bucket.CollisionGeneration)
            {
                continue;
            }
            operation.CollisionGeneration = 0UL;
            operation.CollisionGenerationReady = false;
            if (!unbound.Contains(key))
                unbound.Add(key);
        }
        if (unbound.Count == 0)
        {
            _unboundDeferredByCell.Remove(unboundKey);
            _unboundDeferredCellOrder.Remove(unboundKey);
        }
    }

    private void RemoveDeferredBucket(CellGenerationKey bucket)
    {
        _deferredByCellGeneration.Remove(bucket);
        int index = _deferredBucketOrder.IndexOf(bucket);
        if (index < 0)
            return;
        int last = _deferredBucketOrder.Count - 1;
        _deferredBucketOrder[index] = _deferredBucketOrder[last];
        _deferredBucketOrder.RemoveAt(last);
    }

    private static RuntimeSetPositionOutcome Outcome(
        RuntimeSetPositionStatus status,
        in PhysicsSetPositionResult result,
        in RuntimePlacementProjectionToken projection) => new(
            status,
            result.Error,
            result.Residence,
            result.CellId,
            projection);

    private static RuntimeSetPositionOutcome Rejected(
        in PhysicsSetPositionRequest request) => new(
            RuntimeSetPositionStatus.Rejected,
            PhysicsSetPositionError.InvalidArguments,
            PhysicsResidenceDisposition.Unchanged,
            request.CellId,
            default);

    private static PhysicsSetPositionResult InvalidResult(
        in PhysicsSetPositionRequest request) => new(
            PhysicsSetPositionError.InvalidArguments,
            PhysicsResidenceDisposition.Unchanged,
            request.Position,
            request.Orientation,
            request.CellId,
            request.CellLocalPosition,
            CrossCellIds: ImmutableArray<uint>.Empty,
            CollidedObjectIds: ImmutableArray<uint>.Empty);

    private static bool IsStructurallyValid(
        in PhysicsSetPositionRequest request)
    {
        static bool Finite(Vector3 value) => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z);

        if (!PositionFrameValidation.IsValid(
                request.CellId,
                request.CellLocalPosition,
                request.Orientation)
            || !Finite(request.Position)
            || !Finite(request.CellLocalPosition)
            || !float.IsFinite(request.StepUpHeight)
            || !float.IsFinite(request.StepDownHeight))
        {
            return false;
        }
        if (!request.Spheres.IsDefaultOrEmpty)
        {
            if (!float.IsFinite(request.Scale))
                return false;
            int count = Math.Min(request.Spheres.Length, 2);
            for (int index = 0; index < count; index++)
            {
                FlatCollisionSphere sphere = request.Spheres[index];
                if (!Finite(sphere.Origin) || !float.IsFinite(sphere.Radius))
                    return false;
            }
        }
        if (request.Flags.HasFlag(PhysicsSetPositionFlags.Line)
            && !Finite(request.Line))
        {
            return false;
        }
        bool scatter = request.Flags.HasFlag(PhysicsSetPositionFlags.Scatter)
            || request.Flags.HasFlag(PhysicsSetPositionFlags.RandomScatter);
        if (scatter
            && (!float.IsFinite(request.ScatterRadiusX)
                || !float.IsFinite(request.ScatterRadiusY)
                || request.ScatterAttempts > MaxSynchronousScatterAttempts))
        {
            return false;
        }
        return true;
    }

    private void PublishPlacement(
        in RuntimePlacementProjectionSnapshot projection) =>
        _events?.PublishPlacement(projection);

    private void ArmLostFamilyDeadlines(Operation operation)
    {
        RuntimeEntityRecord root = operation.Record;
        CancelLostFamilyDeadlines(operation);
        double deadline = _physics.MonotonicNowSeconds + 25d;
        operation.LostFamilyKeys ??= [];
        if (root.Key is { } rootKey)
        {
            operation.LostFamilyKeys.Add(rootKey);
            ArmLostDeadline(rootKey, deadline);
        }
        IReadOnlyList<uint> children = _entities.ParentAttachments
            .ChildrenAttachedToParent(root.ServerGuid, root.Incarnation);
        for (int index = 0; index < children.Count; index++)
        {
            if (_entities.TryGetActive(
                    children[index],
                    out RuntimeEntityRecord child)
                && child.Key is { } childKey)
            {
                operation.LostFamilyKeys.Add(childKey);
                ArmLostDeadline(childKey, deadline);
            }
        }
    }

    private void CancelLostFamilyDeadlines(Operation operation)
    {
        if (operation.Record.Key is { } rootKey)
        {
            RemoveLostDeadline(rootKey);
            RemoveExpiredLostCell(rootKey);
        }
        IReadOnlyList<uint> currentChildren = _entities.ParentAttachments
            .ChildrenAttachedToParent(
                operation.Record.ServerGuid,
                operation.Record.Incarnation);
        for (int index = 0; index < currentChildren.Count; index++)
        {
            if (_entities.TryGetActive(
                    currentChildren[index],
                    out RuntimeEntityRecord child)
                && child.Key is { } key)
            {
                RemoveLostDeadline(key);
                RemoveExpiredLostCell(key);
            }
        }
        operation.LostFamilyKeys?.Clear();
    }

    private void CancelExactLostKey(RuntimeEntityKey key)
    {
        RemoveLostDeadline(key);
        RemoveExpiredLostCell(key);
        foreach (Operation operation in _operations.Values)
            operation.LostFamilyKeys?.Remove(key);
    }

    private void RemoveExpiredLostCell(RuntimeEntityKey key)
    {
        if (!_expiredLostCellNodes.Remove(
                key,
                out LinkedListNode<RuntimeEntityKey>? node))
        {
            return;
        }
        _expiredLostCells.Remove(node);
    }

    private void ArmLostDeadline(RuntimeEntityKey key, double deadline)
    {
        RemoveLostDeadlineNode(key);
        _lostDeadlines[key] = deadline;
        int index = _lostDeadlineNodes.Count;
        _lostDeadlineNodes.Add(new LostDeadlineEntry(
            key,
            deadline,
            checked(++_nextLostDeadlineSequence)));
        _lostDeadlineNodeIndex.Add(key, index);
        BubbleLostDeadlineUp(index);
    }

    private void RemoveLostDeadline(RuntimeEntityKey key)
    {
        _lostDeadlines.Remove(key);
        RemoveLostDeadlineNode(key);
    }

    private static bool IsEarlier(
        in LostDeadlineEntry candidate,
        in LostDeadlineEntry current) =>
        candidate.Deadline < current.Deadline
        || (candidate.Deadline == current.Deadline
            && candidate.Sequence < current.Sequence);

    private void BubbleLostDeadlineUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!IsEarlier(
                    _lostDeadlineNodes[index],
                    _lostDeadlineNodes[parent]))
                break;
            SwapLostDeadlineNodes(index, parent);
            index = parent;
        }
    }

    private void BubbleLostDeadlineDown(int index)
    {
        while (true)
        {
            int left = checked(index * 2 + 1);
            if (left >= _lostDeadlineNodes.Count)
                return;
            int right = left + 1;
            int earlier = right < _lostDeadlineNodes.Count
                && IsEarlier(
                    _lostDeadlineNodes[right],
                    _lostDeadlineNodes[left])
                    ? right
                    : left;
            if (!IsEarlier(
                    _lostDeadlineNodes[earlier],
                    _lostDeadlineNodes[index]))
                return;
            SwapLostDeadlineNodes(index, earlier);
            index = earlier;
        }
    }

    private void SwapLostDeadlineNodes(int first, int second)
    {
        LostDeadlineEntry temporary = _lostDeadlineNodes[first];
        _lostDeadlineNodes[first] = _lostDeadlineNodes[second];
        _lostDeadlineNodes[second] = temporary;
        _lostDeadlineNodeIndex[_lostDeadlineNodes[first].Key] = first;
        _lostDeadlineNodeIndex[_lostDeadlineNodes[second].Key] = second;
    }

    private void RemoveLostDeadlineNode(RuntimeEntityKey key)
    {
        if (_lostDeadlineNodeIndex.TryGetValue(key, out int index))
            RemoveLostDeadlineNodeAt(index);
    }

    private void RemoveLostDeadlineNodeAt(int index)
    {
        LostDeadlineEntry removed = _lostDeadlineNodes[index];
        _lostDeadlineNodeIndex.Remove(removed.Key);
        int last = _lostDeadlineNodes.Count - 1;
        if (index != last)
        {
            LostDeadlineEntry moved = _lostDeadlineNodes[last];
            _lostDeadlineNodes[index] = moved;
            _lostDeadlineNodeIndex[moved.Key] = index;
        }
        _lostDeadlineNodes.RemoveAt(last);
        if (index >= _lostDeadlineNodes.Count)
            return;
        int parent = index == 0 ? -1 : (index - 1) / 2;
        if (parent >= 0
            && IsEarlier(
                _lostDeadlineNodes[index],
                _lostDeadlineNodes[parent]))
        {
            BubbleLostDeadlineUp(index);
        }
        else
        {
            BubbleLostDeadlineDown(index);
        }
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
