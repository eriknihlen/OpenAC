using AcDream.Content;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

internal enum RuntimeLocalPlayerFirstEntryStatus : byte
{
    Completed,

    AwaitingCollisionSource,

    AwaitingActivation,

    AwaitingReceiptAcknowledgement,

    AwaitingContinuationPlacement,

    Contention,

    /// <summary>The residence/placement token no longer matches anything tracked.</summary>
    RejectedToken,

    RejectedAuthority,
}

internal readonly record struct RuntimeLocalPlayerFirstEntryOwnershipSnapshot(
    int ActiveCount)
{
    internal bool IsConverged => ActiveCount == 0;
}

internal sealed class RuntimeLocalPlayerFirstEntryState
{
    private enum Stage : byte
    {
        /// <summary>No progress yet, or the mover has not been prepared.</summary>
        AwaitingMoverPreparation,

        MoverPrepared,

        PublicationCommitted,

        ActivationCommitted,

        Acknowledged,
    }

    private sealed class Progress
    {
        internal required ulong LeaseId { get; init; }
        internal Stage Stage { get; set; } = Stage.AwaitingMoverPreparation;
        internal RuntimeSetPositionCommand PreparedCommand { get; set; }
        internal RuntimeLocalPlayerPhysicsPublicationToken PublicationToken
            { get; set; }
        internal RuntimeLocalPlayerPhysicsActivationToken ActivationToken
            { get; set; }
        internal RuntimePlacementProjectionToken Projection { get; set; }
    }

    private readonly RuntimeInitialCreateResidenceState _residences;
    private readonly RuntimeInitialCreateContinuationExecutor _executor;
    private readonly RuntimePhysicsState _physics;
    private RuntimeLocalPlayerPhysicsPublicationState? _publication;
    private readonly Dictionary<RuntimeEntityKey, Progress> _progress = [];
    private readonly HashSet<RuntimeEntityKey> _executing = [];

    internal RuntimeLocalPlayerFirstEntryState(
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

    internal void BindPublication(
        RuntimeLocalPlayerPhysicsPublicationState publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (_publication is not null)
        {
            throw new InvalidOperationException(
                "The local-player first-entry conductor's publication owner is already bound.");
        }
        _publication = publication;
    }

    private RuntimeLocalPlayerPhysicsPublicationState Publication =>
        _publication ?? throw new InvalidOperationException(
            "The local-player first-entry conductor's publication owner is not yet bound.");

    internal RuntimeLocalPlayerFirstEntryStatus Advance(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        PlayerMovementConstructionOptions options,
        in RuntimeLocalPlayerPhysicsActivationPreparation activationPreparation,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        in RuntimeInitialCreateExecutionInputs inputs,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(collisionSource);
        receipt = default;
        if (!residenceToken.IsValid || record.Key is not { } key)
            return RuntimeLocalPlayerFirstEntryStatus.RejectedToken;

        if (!_executing.Add(key))
            return RuntimeLocalPlayerFirstEntryStatus.Contention;
        try
        {
            return AdvanceCore(
                record,
                residenceToken,
                options,
                activationPreparation,
                collisionSource,
                gameTime,
                inputs,
                key,
                out receipt);
        }
        finally
        {
            _executing.Remove(key);
        }
    }

    private RuntimeLocalPlayerFirstEntryStatus AdvanceCore(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        PlayerMovementConstructionOptions options,
        in RuntimeLocalPlayerPhysicsActivationPreparation activationPreparation,
        IPreparedCollisionSource collisionSource,
        double gameTime,
        in RuntimeInitialCreateExecutionInputs inputs,
        RuntimeEntityKey key,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        receipt = default;

        _ = Publication;

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
                    return RuntimeLocalPlayerFirstEntryStatus.RejectedToken;
                Discard(key);
                return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }

            if (!lease.Route.PerformsSetPosition)
            {
                progress ??= new Progress { LeaseId = residenceToken.LeaseId };
                progress.Stage = Stage.Acknowledged;
                _progress[key] = progress;
                return RunExecute(record, residenceToken, inputs, key, out receipt);
            }

            RuntimeSetPositionMoverPreparationStatus moverStatus = _physics
                .SetPosition.TryPrepareAuthoredMover(
                    record,
                    lease.Placement,
                    lease.Route.OperationKind,
                    lease.Route.SetPositionFlags,
                    collisionSource,
                    gameTime,
                    out RuntimeSetPositionCommand command);
            if (moverStatus.IsRetryable())
            {
                return RuntimeLocalPlayerFirstEntryStatus
                    .AwaitingCollisionSource;
            }
            if (moverStatus != RuntimeSetPositionMoverPreparationStatus.Prepared)
            {
                if (progress is not null)
                    Discard(key);
                return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
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
                return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }

            RuntimeLocalPlayerPhysicsPublicationStatus prepareStatus =
                Publication.Prepare(
                    record,
                    lease.Placement,
                    progress.PreparedCommand,
                    options,
                    activationPreparation,
                    out RuntimeLocalPlayerPhysicsPublicationToken pubToken);
            if (prepareStatus
                != RuntimeLocalPlayerPhysicsPublicationStatus.Prepared)
            {
                Discard(key);
                return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }

            progress.PublicationToken = pubToken;

            RuntimeLocalPlayerPhysicsPublicationStatus commitStatus =
                Publication.Commit(
                    pubToken,
                    out RuntimeLocalPlayerPhysicsActivationToken activationToken);
            if (commitStatus != RuntimeLocalPlayerPhysicsPublicationStatus.Committed)
            {
                Discard(key);
                return commitStatus
                        is RuntimeLocalPlayerPhysicsPublicationStatus.RejectedToken
                    ? RuntimeLocalPlayerFirstEntryStatus.RejectedToken
                    : RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }

            progress.ActivationToken = activationToken;
            progress.Stage = Stage.PublicationCommitted;
        }

        if (progress.Stage is Stage.PublicationCommitted)
        {
            RuntimeLocalPlayerPhysicsActivationStatus evalStatus =
                Publication.EvaluateActivation(
                    progress.ActivationToken,
                    out RuntimeLocalPlayerPhysicsActivationReceipt evalReceipt);
            if (evalStatus is
                RuntimeLocalPlayerPhysicsActivationStatus.RejectedToken
                or RuntimeLocalPlayerPhysicsActivationStatus.RejectedAuthority)
            {
                Discard(key);
                return evalStatus
                        is RuntimeLocalPlayerPhysicsActivationStatus.RejectedToken
                    ? RuntimeLocalPlayerFirstEntryStatus.RejectedToken
                    : RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }
            if (!evalReceipt.IsValid)
            {
                return RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation;
            }

            RuntimeDormantSetPositionCommitStatus commitActivationStatus =
                Publication.CommitActivation(
                    evalReceipt,
                    out RuntimePlacementProjectionToken projection);
            switch (commitActivationStatus)
            {
                case RuntimeDormantSetPositionCommitStatus.Committed:
                    progress.Projection = projection;
                    progress.Stage = Stage.ActivationCommitted;
                    break;
                case RuntimeDormantSetPositionCommitStatus.DeferredCell:
                case RuntimeDormantSetPositionCommitStatus.RejectedPlacement:
                    return RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation;
                default:
                    Discard(key);
                    return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
            }
        }

        if (progress.Stage is Stage.ActivationCommitted)
        {
            if (!_physics.SetPosition.AcknowledgeProjection(
                    progress.Projection))
            {
                if (!IsAcknowledgementStillPending(
                        record, residenceToken, progress.Projection))
                {
                    Discard(key);
                    return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
                }
                return RuntimeLocalPlayerFirstEntryStatus
                    .AwaitingReceiptAcknowledgement;
            }
            progress.Stage = Stage.Acknowledged;
        }

        return RunExecute(record, residenceToken, inputs, key, out receipt);
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

    private RuntimeLocalPlayerFirstEntryStatus RunExecute(
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        in RuntimeInitialCreateExecutionInputs inputs,
        RuntimeEntityKey key,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        RuntimeInitialCreateExecutionStatus executeStatus = _executor.Execute(
            record, residenceToken, inputs, out receipt);
        switch (executeStatus)
        {
            case RuntimeInitialCreateExecutionStatus.Completed:
                _progress.Remove(key);
                return RuntimeLocalPlayerFirstEntryStatus.Completed;
            case RuntimeInitialCreateExecutionStatus.PendingPlacement:
                return RuntimeLocalPlayerFirstEntryStatus
                    .AwaitingReceiptAcknowledgement;
            case RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement:
                return RuntimeLocalPlayerFirstEntryStatus
                    .AwaitingContinuationPlacement;
            case RuntimeInitialCreateExecutionStatus.RejectedToken:
                _progress.Remove(key);
                return RuntimeLocalPlayerFirstEntryStatus.RejectedToken;
            default:
                _progress.Remove(key);
                return RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
        }
    }

    private void Discard(RuntimeEntityKey key)
    {
        if (!_progress.TryGetValue(key, out Progress? progress))
            return;
        _progress.Remove(key);
        if (_publication is null)
            return;
        Publication.Discard(progress.PublicationToken);
        Publication.DiscardActivation(progress.ActivationToken);
    }

    internal void Forget(RuntimeEntityKey key) => Discard(key);

    internal void DiscardAll()
    {
        if (_publication is not null)
        {
            foreach (Progress progress in _progress.Values)
            {
                Publication.Discard(progress.PublicationToken);
                Publication.DiscardActivation(progress.ActivationToken);
            }
        }
        _progress.Clear();
    }

    internal RuntimeLocalPlayerFirstEntryOwnershipSnapshot CaptureOwnership() =>
        new(_progress.Count);
}
