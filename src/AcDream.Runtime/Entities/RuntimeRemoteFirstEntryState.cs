using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

internal enum RuntimeRemoteFirstEntryStatus : byte
{
    Completed,

    AwaitingCollisionSource,

    AwaitingPlacement,

    AwaitingReceiptAcknowledgement,

    AwaitingContinuationPlacement,

    Contention,

    RejectedToken,

    RejectedAuthority,
}

internal readonly record struct RuntimeRemoteFirstEntryOwnershipSnapshot(
    int ActiveCount)
{
    internal bool IsConverged => ActiveCount == 0;
}

internal sealed class RuntimeRemoteFirstEntryState
{
    private enum Stage : byte
    {
        /// <summary>No progress yet, or the mover has not been prepared.</summary>
        AwaitingMoverPreparation,

        MoverPrepared,

        BodyConstructed,

        PlacementSubmitted,

        /// <summary>
        /// The Place projection token is known but not yet acknowledged.
        /// </summary>
        PlacementCommitted,

        Acknowledged,
    }

    private sealed class Progress
    {
        internal required ulong LeaseId { get; init; }
        internal Stage Stage { get; set; } = Stage.AwaitingMoverPreparation;
        internal RuntimeSetPositionCommand PreparedCommand { get; set; }
        internal PhysicsBody? ConstructedBody { get; set; }
        internal RuntimeRemoteBodyConstructionReceipt Construction { get; set; }
        internal RuntimePlacementProjectionToken Projection { get; set; }
    }

    private readonly RuntimeInitialCreateResidenceState _residences;
    private readonly RuntimeInitialCreateContinuationExecutor _executor;
    private readonly RuntimePhysicsState _physics;
    private readonly Dictionary<RuntimeEntityKey, Progress> _progress = [];
    private readonly HashSet<RuntimeEntityKey> _executing = [];

    internal RuntimeRemoteFirstEntryState(
        RuntimeInitialCreateResidenceState residences,
        RuntimeInitialCreateContinuationExecutor executor,
        RuntimePhysicsState physics)
    {
        _residences = residences
            ?? throw new ArgumentNullException(nameof(residences));
        _executor = executor
            ?? throw new ArgumentNullException(nameof(executor));
        _physics = physics
            ?? throw new ArgumentNullException(nameof(physics));
    }

    internal bool TryGetConstruction(
        RuntimeEntityKey key,
        out RuntimeRemoteBodyConstructionReceipt construction)
    {
        if (_progress.TryGetValue(key, out Progress? progress)
            && progress.ConstructedBody is not null)
        {
            construction = progress.Construction;
            return true;
        }
        construction = default;
        return false;
    }

    internal RuntimeRemoteFirstEntryStatus Advance(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        in RuntimeInitialCreateExecutionInputs inputs,
        out RuntimeInitialCreateExecutionReceipt receipt,
        out RuntimeRemoteBodyConstructionReceipt construction)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(collisionSource);
        receipt = default;
        construction = default;
        if (!residenceToken.IsValid || record.Key is not { } key)
            return RuntimeRemoteFirstEntryStatus.RejectedToken;

        if (!_executing.Add(key))
            return RuntimeRemoteFirstEntryStatus.Contention;
        try
        {
            return AdvanceCore(
                record,
                residenceToken,
                collisionSource,
                gameTime,
                inputs,
                key,
                out receipt,
                out construction);
        }
        finally
        {
            _executing.Remove(key);
        }
    }

    private RuntimeRemoteFirstEntryStatus AdvanceCore(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        in RuntimeInitialCreateExecutionInputs inputs,
        RuntimeEntityKey key,
        out RuntimeInitialCreateExecutionReceipt receipt,
        out RuntimeRemoteBodyConstructionReceipt construction)
    {
        receipt = default;
        construction = default;

        _progress.TryGetValue(key, out Progress? progress);
        if (progress is not null && progress.LeaseId != residenceToken.LeaseId)
        {
            Discard(key);
            progress = null;
        }

        if (progress is null || progress.Stage is Stage.AwaitingMoverPreparation)
        {
            if (!_residences.TryGetCurrent(
                    record,
                    out RuntimeInitialCreateResidenceLease lease)
                || lease.Token != residenceToken)
            {
                if (progress is null)
                    return RuntimeRemoteFirstEntryStatus.RejectedToken;
                Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }

            if (lease.Route.OperationKind
                is not (RuntimeSetPositionOperationKind.RemoteAuthoritative
                    or RuntimeSetPositionOperationKind.ProjectileAuthoritative))
            {
                return RuntimeRemoteFirstEntryStatus.RejectedToken;
            }

            if (!lease.Route.PerformsSetPosition)
            {
                progress ??= new Progress { LeaseId = residenceToken.LeaseId };
                progress.Stage = Stage.Acknowledged;
                _progress[key] = progress;
                return RunExecute(
                    record,
                    residenceToken,
                    inputs,
                    key,
                    progress,
                    out receipt,
                    out construction);
            }

            RuntimeSetPositionMoverPreparationStatus moverStatus = _physics
                .SetPosition.TryPrepareAuthoredMover(
                    record,
                    lease.Placement,
                    lease.Route.OperationKind,
                    lease.Route.SetPositionFlags,
                    collisionSource,
                    gameTime,
                    out RuntimeSetPositionCommand command,
                    resolveWorldOffsetFromRuntimeFrame: true);
            if (moverStatus.IsRetryable())
            {
                return RuntimeRemoteFirstEntryStatus.AwaitingCollisionSource;
            }
            if (moverStatus != RuntimeSetPositionMoverPreparationStatus.Prepared)
            {
                if (progress is not null)
                    Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }

            progress ??= new Progress { LeaseId = residenceToken.LeaseId };
            progress.PreparedCommand = command;
            progress.Stage = Stage.MoverPrepared;
            _progress[key] = progress;
        }

        if (progress.Stage is Stage.MoverPrepared)
        {
            if (!_residences.TryGetCurrent(
                    record,
                    out RuntimeInitialCreateResidenceLease lease)
                || lease.Token != residenceToken)
            {
                Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }

            if (record.PhysicsBody is { } existing)
            {
                if (ReferenceEquals(progress.ConstructedBody, existing))
                {
                    progress.Stage = Stage.BodyConstructed;
                }
                else
                {
                    Discard(key);
                    return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
                }
            }
            else if (record.PhysicsBodyAcquisitionInProgress
                || record.RemoteMotionBindingInProgress)
            {
                return RuntimeRemoteFirstEntryStatus.Contention;
            }
            else
            {
                RuntimeRemoteBodyConstructionReceipt built = default;
                PhysicsBody constructed = _physics.GetOrCreatePhysicsBody(
                    record,
                    r => RuntimeRemoteBodyDescription.Construct(
                        r,
                        lease.InitialCreate.Physics,
                        progress.PreparedCommand,
                        out built));
                progress.ConstructedBody = constructed;
                progress.Construction = built;
                progress.Stage = Stage.BodyConstructed;
            }
        }

        if (progress.Stage is Stage.BodyConstructed)
        {
            if (!_residences.TryGetCurrent(
                    record,
                    out RuntimeInitialCreateResidenceLease lease)
                || lease.Token != residenceToken)
            {
                Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }

            RuntimeSetPositionOutcome outcome = _physics.SetPosition
                .SubmitPreparedPlacement(lease.Placement, progress.PreparedCommand);
            switch (outcome.Status)
            {
                case RuntimeSetPositionStatus.CommittedHostAcknowledgementPending:
                    progress.Projection = outcome.Projection;
                    progress.Stage = Stage.PlacementCommitted;
                    break;
                case RuntimeSetPositionStatus.DeferredCell:
                    progress.Stage = Stage.PlacementSubmitted;
                    break;
                default:
                    Discard(key);
                    return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }
        }

        if (progress.Stage is Stage.PlacementSubmitted)
        {
            if (!_residences.TryGetCurrent(
                    record,
                    out RuntimeInitialCreateResidenceLease lease)
                || lease.Token != residenceToken)
            {
                Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }

            while (true)
            {
                if (!_physics.SetPosition.TryPeekProjection(
                        out RuntimePlacementProjectionSnapshot head))
                {
                    return RuntimeRemoteFirstEntryStatus.AwaitingPlacement;
                }
                if (head.Token.Entity != key)
                {
                    // Another entity's receipt sits ahead of ours in the one
                    // ordered FIFO.
                    return RuntimeRemoteFirstEntryStatus
                        .AwaitingReceiptAcknowledgement;
                }
                if (head.Kind is RuntimePlacementProjectionKind.Withdraw)
                {
                    if (!_physics.SetPosition.AcknowledgeProjection(head.Token))
                    {
                        Discard(key);
                        return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
                    }
                    // The withdrawal acknowledgement may have re-armed (or —
                    // when the generation was already ready — synchronously
                    // resubmitted) the parked operation; peek again.
                    continue;
                }
                if (head.Kind is RuntimePlacementProjectionKind.Place)
                {
                    progress.Projection = head.Token;
                    progress.Stage = Stage.PlacementCommitted;
                    break;
                }
                Discard(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
            }
        }

        if (progress.Stage is Stage.PlacementCommitted)
        {
            if (!_physics.SetPosition.AcknowledgeProjection(progress.Projection))
            {
                if (!IsAcknowledgementStillPending(
                        record, residenceToken, progress.Projection))
                {
                    Discard(key);
                    return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
                }
                return RuntimeRemoteFirstEntryStatus
                    .AwaitingReceiptAcknowledgement;
            }
            progress.Stage = Stage.Acknowledged;
        }

        return RunExecute(
            record,
            residenceToken,
            inputs,
            key,
            progress,
            out receipt,
            out construction);
    }

    private bool IsAcknowledgementStillPending(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        in RuntimePlacementProjectionToken expected) =>
        RuntimeFirstEntryAcknowledgement.IsStillPending(
            _residences,
            _physics.SetPosition,
            record,
            residenceToken,
            expected);

    private RuntimeRemoteFirstEntryStatus RunExecute(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        in RuntimeInitialCreateExecutionInputs inputs,
        RuntimeEntityKey key,
        Progress progress,
        out RuntimeInitialCreateExecutionReceipt receipt,
        out RuntimeRemoteBodyConstructionReceipt construction)
    {
        construction = default;
        RuntimeInitialCreateExecutionStatus executeStatus = _executor.Execute(
            record, residenceToken, inputs, out receipt);
        switch (executeStatus)
        {
            case RuntimeInitialCreateExecutionStatus.Completed:
                construction = progress.Construction;
                _progress.Remove(key);
                return RuntimeRemoteFirstEntryStatus.Completed;
            case RuntimeInitialCreateExecutionStatus.PendingPlacement:
                return RuntimeRemoteFirstEntryStatus
                    .AwaitingReceiptAcknowledgement;
            case RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement:
                return RuntimeRemoteFirstEntryStatus
                    .AwaitingContinuationPlacement;
            case RuntimeInitialCreateExecutionStatus.RejectedToken:
                _progress.Remove(key);
                return RuntimeRemoteFirstEntryStatus.RejectedToken;
            default:
                _progress.Remove(key);
                return RuntimeRemoteFirstEntryStatus.RejectedAuthority;
        }
    }

    private void Discard(RuntimeEntityKey key) => _progress.Remove(key);

    internal void Forget(RuntimeEntityKey key) => Discard(key);

    internal void DiscardAll() => _progress.Clear();

    internal RuntimeRemoteFirstEntryOwnershipSnapshot CaptureOwnership() =>
        new(_progress.Count);
}
