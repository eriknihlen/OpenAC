using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

internal enum RuntimeLocalPlayerPhysicsPublicationStatus
{
    Prepared,
    Committed,
    RejectedAuthority,
    RejectedToken,
    Discarded,
}

internal enum RuntimeLocalPlayerPhysicsActivationStatus
{
    Evaluated,
    DeferredCell,
    RejectedPlacement,
    RejectedAuthority,
    RejectedToken,
}

internal enum RuntimeLocalPlayerShadowDisposition : byte
{
    RegisteredAuthoredPayload,
    ProvenShapeless,
}

internal readonly record struct RuntimeLocalPlayerPhysicsActivationPreparation(
    float Radius,
    float Height,
    RuntimeLocalPlayerShadowDisposition ShadowDisposition)
{
    internal bool IsValid => float.IsFinite(Radius)
        && Radius >= 0f
        && float.IsFinite(Height)
        && Height >= 0f
        && ShadowDisposition is RuntimeLocalPlayerShadowDisposition
            .RegisteredAuthoredPayload
            or RuntimeLocalPlayerShadowDisposition.ProvenShapeless;
}

internal readonly record struct RuntimeLocalPlayerPhysicsPublicationToken(
    RuntimeEntityKey Entity,
    RuntimeEntityPlacementToken Placement,
    ulong PublicationId,
    uint LocalPlayerServerGuid,
    long LocalPlayerIdentityRevision,
    ulong PhysicsOwnershipEpoch,
    ulong ObjectClockEpoch,
    ulong ControllerOwnershipEpoch,
    ulong SessionGenerationAuthority)
{
    internal bool IsValid => PublicationId != 0UL
        && LocalPlayerServerGuid != 0u
        && Placement.IsValid
        && Entity == Placement.Entity;
}

internal readonly record struct RuntimeLocalPlayerPhysicsActivationToken(
    RuntimeEntityKey Entity,
    RuntimeEntityPlacementToken Placement,
    ulong ActivationId,
    uint LocalPlayerServerGuid,
    long LocalPlayerIdentityRevision,
    ulong PhysicsOwnershipEpoch,
    ulong ObjectClockEpoch,
    ulong ControllerOwnershipEpoch,
    ulong SessionGenerationAuthority)
{
    internal bool IsValid => ActivationId != 0UL
        && LocalPlayerServerGuid != 0u
        && Placement.IsValid
        && Entity == Placement.Entity;
}

internal readonly record struct RuntimeLocalPlayerPhysicsActivationReceipt(
    RuntimeLocalPlayerPhysicsActivationToken Token,
    ulong EvaluationId,
    RuntimeDormantSetPositionEvaluation Placement)
{
    internal bool IsValid => Token.IsValid
        && EvaluationId != 0UL
        && Placement.Placement == Token.Placement;
}

internal readonly record struct RuntimeLocalPlayerPhysicsCandidateSnapshot(
    Vector3 Position,
    Quaternion Orientation,
    uint CellId,
    Vector3 CellLocalPosition,
    PhysicsStateFlags State,
    TransientStateFlags TransientState,
    bool InWorld);

public readonly record struct
    RuntimeLocalPlayerPhysicsPublicationOwnershipSnapshot(
        bool IsBound,
        bool IsDisposed,
        int CandidateCount,
        int PendingActivationCount,
        ulong LastPublicationId)
{
    internal bool IsConverged => !IsBound
        || (IsDisposed
            && CandidateCount == 0
            && PendingActivationCount == 0);
}

internal sealed class RuntimeLocalPlayerPhysicsPublicationState : IDisposable
{
    private sealed class Candidate
    {
        internal required RuntimeLocalPlayerPhysicsPublicationToken Token
            { get; init; }
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeSetPositionCommand PlacementCommand
            { get; init; }
        internal required PlayerMovementController Controller { get; init; }
        internal required PhysicsBody Body { get; init; }
        internal required EntityPhysicsHost PhysicsHost { get; init; }
        internal required MovementManager Movement { get; init; }
        internal required MotionInterpreter Motion { get; init; }
        internal required RuntimeLocalPlayerPhysicsActivationPreparation
            ActivationPreparation { get; init; }
        internal required Activation PreparedActivation { get; init; }
    }

    private sealed class Activation
    {
        internal required RuntimeLocalPlayerPhysicsActivationToken Token
            { get; init; }
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeSetPositionCommand PlacementCommand
            { get; init; }
        internal required PlayerMovementController Controller { get; init; }
        internal required PhysicsBody Body { get; init; }
        internal required EntityPhysicsHost PhysicsHost { get; init; }
        internal required MovementManager Movement { get; init; }
        internal required MotionInterpreter Motion { get; init; }
        internal required RuntimeLocalPlayerPhysicsActivationPreparation
            ActivationPreparation { get; init; }
        internal RuntimeLocalPlayerPhysicsActivationReceipt Receipt
            { get; set; }
        internal RuntimeDormantSetPositionCommitReceipt PendingFinalCommit
            { get; set; }
    }

    private readonly RuntimeEntityDirectory _entities;
    private readonly RuntimePhysicsState _physics;
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly RuntimeLocalPlayerIdentityState _identity;
    private Candidate? _candidate;
    private Activation? _activation;
    private ulong _nextPublicationId;
    private ulong _nextActivationId;
    private ulong _nextEvaluationId;
    private long _activationDispatchFailureCount;
    private bool _disposed;

    internal RuntimeLocalPlayerPhysicsPublicationState(
        RuntimeEntityDirectory entities,
        RuntimePhysicsState physics,
        RuntimeLocalPlayerMovementState movement,
        RuntimeLocalPlayerIdentityState identity)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    internal RuntimeLocalPlayerPhysicsPublicationStatus Prepare(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken placement,
        in RuntimeSetPositionCommand command,
        PlayerMovementConstructionOptions options,
        in RuntimeLocalPlayerPhysicsActivationPreparation activationPreparation,
        out RuntimeLocalPlayerPhysicsPublicationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        token = default;
        if (!activationPreparation.IsValid
            || !CanPrepare(record, placement, command))
            return RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority;

        ulong publicationId = checked(_nextPublicationId + 1UL);
        ulong activationId = checked(_nextActivationId + 1UL);

        RuntimeLocalPlayerPhysicsActivationPreparation preparedActivation =
            activationPreparation;

        var controller = PlayerMovementController.CreatePublicationCandidate(
            _physics.Engine,
            options);
        controller.LocalEntityId = record.Key!.Value.LocalEntityId;
        controller.StepUpHeight = command.Physics.StepUpHeight;
        controller.StepDownHeight = command.Physics.StepDownHeight;

        controller.SphereList = command.Physics.Spheres;
        controller.ObjectScale = command.Physics.Scale;
        controller.PreparePositionForCommit(
            command.Physics.Position,
            command.Physics.CellId,
            command.Physics.CellLocalPosition);
        controller.SetBodyOrientation(command.Physics.Orientation);
        controller.ApplyPhysicsState(record.FinalPhysicsState);
        PhysicsBody body = controller.PhysicsBody;
        var physics = record.Snapshot.Physics;
        body.Friction = NormalizeFriction(
            physics?.Friction ?? record.Snapshot.Friction);
        body.Elasticity = NormalizeElasticity(
            physics?.Elasticity
                ?? record.Snapshot.Elasticity
                ?? body.Elasticity);
        if (physics?.Velocity is { } initialVelocity)
            body.set_velocity(initialVelocity);
        if (physics?.AngularVelocity is { } initialOmega)
            body.Omega = initialOmega;
        MovementManager movement = controller.Movement;
        MotionInterpreter motion = controller.Motion;
        EntityPhysicsHost physicsHost = null!;
        movement.MoveToFactory = () =>
        {
            var moveTo = new MoveToManager(
                motion,
                stopCompletely: () =>
                    _ = controller.StopCompletelyAtPhysicsObjectBoundary(),
                getPosition: () => new Position(
                    body.CellPosition.ObjCellId,
                    body.Position,
                    body.Orientation),
                getHeading: () => MoveToMath.GetHeading(body.Orientation),
                setHeading: (heading, _) => body.Orientation =
                    MoveToMath.SetHeading(body.Orientation, heading),
                getOwnRadius: () => preparedActivation.Radius,
                getOwnHeight: () => preparedActivation.Height,
                contact: () => body.InContact,
                isInterpolating: static () => false,
                getVelocity: () => body.Velocity,
                getSelfId: () => record.ServerGuid,
                setTarget: (context, target, radius, quantum) =>
                    physicsHost.SetTarget(context, target, radius, quantum),
                clearTarget: () => physicsHost.ClearTarget(),
                getTargetQuantum: () =>
                    physicsHost.TargetManager.GetTargetQuantum(),
                setTargetQuantum: quantum =>
                    physicsHost.TargetManager.SetTargetQuantum(quantum),
                curTime: () => controller.SimTimeSeconds);
            moveTo.StickTo = (target, radius, height) =>
                physicsHost.PositionManager.StickTo(target, radius, height);
            moveTo.Unstick = physicsHost.PositionManager.UnStick;
            return moveTo;
        };
        physicsHost = new EntityPhysicsHost(
            record.ServerGuid,
            getPosition: () => new Position(
                body.CellPosition.ObjCellId,
                body.Position,
                body.Orientation),
            getVelocity: () => body.Velocity,
            getRadius: () => preparedActivation.Radius,
            inContact: () => body.InContact,
            minterpMaxSpeed: () => motion.GetAdjustedMaxSpeed(),
            curTime: () => controller.SimTimeSeconds,
            physicsTimerTime: () => controller.SimTimeSeconds,
            getObjectA: _physics.ResolveObjectTableHost,
            handleUpdateTarget: info =>
            {
                movement.HandleUpdateTarget(info);
            },
            interruptCurrentMovement: () =>
            {
                movement.CancelMoveTo(WeenieError.ActionCancelled);
            });
        movement.MakeMoveToManager();
        motion.UnstickFromObject = physicsHost.PositionManager.UnStick;
        motion.InterruptCurrentMovement = () =>
        {
            movement.CancelMoveTo(WeenieError.ActionCancelled);
        };
        controller.PositionManager = physicsHost.PositionManager;
        body.InWorld = false;
        body.TransientState &= ~TransientStateFlags.Active;
        controller.SealPublicationCandidate();

        if (!CanPrepare(record, placement, command))
        {
            controller.DiscardRuntimeCandidate();
            return RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority;
        }

        DiscardCurrent();
        _nextPublicationId = publicationId;
        _nextActivationId = activationId;
        token = new RuntimeLocalPlayerPhysicsPublicationToken(
            record.Key.Value,
            placement,
            publicationId,
            _identity.ServerGuid,
            _identity.Revision,
            record.PhysicsOwnershipEpoch,
            record.ObjectClockEpoch,
            _movement.ControllerOwnershipEpoch,
            _entities.SessionLifetimeVersion);
        ulong expectedControllerEpoch = checked(
            _movement.ControllerOwnershipEpoch + 1UL);
        var activationEnvelope = new Activation
        {
            Token = new RuntimeLocalPlayerPhysicsActivationToken(
                token.Entity,
                token.Placement,
                activationId,
                token.LocalPlayerServerGuid,
                token.LocalPlayerIdentityRevision,
                checked(token.PhysicsOwnershipEpoch + 1UL),
                token.ObjectClockEpoch,
                expectedControllerEpoch,
                token.SessionGenerationAuthority),
            Record = record,
            PlacementCommand = command,
            Controller = controller,
            Body = body,
            PhysicsHost = physicsHost,
            Movement = movement,
            Motion = motion,
            ActivationPreparation = preparedActivation,
        };
        _candidate = new Candidate
        {
            Token = token,
            Record = record,
            PlacementCommand = command,
            Controller = controller,
            Body = body,
            PhysicsHost = physicsHost,
            Movement = movement,
            Motion = motion,
            ActivationPreparation = preparedActivation,
            PreparedActivation = activationEnvelope,
        };
        return RuntimeLocalPlayerPhysicsPublicationStatus.Prepared;
    }

    private static float NormalizeFriction(float? value) =>
        value is >= 0f and <= 1f && float.IsFinite(value.Value)
            ? value.Value
            : PhysicsBody.DefaultFriction;

    private static float NormalizeElasticity(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
            return 0f;
        return MathF.Min(value, 0.1f);
    }

    internal RuntimeLocalPlayerPhysicsPublicationStatus Commit(
        in RuntimeLocalPlayerPhysicsPublicationToken token) =>
        Commit(token, out _);

    internal RuntimeLocalPlayerPhysicsPublicationStatus Commit(
        in RuntimeLocalPlayerPhysicsPublicationToken token,
        out RuntimeLocalPlayerPhysicsActivationToken activationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        activationToken = default;
        if (!token.IsValid
            || _candidate is not { } candidate
            || candidate.Token != token)
        {
            return RuntimeLocalPlayerPhysicsPublicationStatus.RejectedToken;
        }
        if (!IsCurrent(candidate))
        {
            DiscardCurrent();
            return RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority;
        }

        _physics.SetPosition.PrepareDormantLocalActivationOwnership(
            candidate.Record,
            candidate.Body,
            candidate.PreparedActivation.Token.Placement);

        candidate.Controller.CommitRuntimeOwnership(
            candidate.Record.ObjectClock);
        candidate.Record.SetPhysicsBody(candidate.Body);
        _movement.CommitRuntimeOwnedController(candidate.Controller);
        activationToken = candidate.PreparedActivation.Token;
        _activation = candidate.PreparedActivation;
        _candidate = null;
        return RuntimeLocalPlayerPhysicsPublicationStatus.Committed;
    }

    internal RuntimeLocalPlayerPhysicsActivationStatus EvaluateActivation(
        in RuntimeLocalPlayerPhysicsActivationToken token,
        out RuntimeLocalPlayerPhysicsActivationReceipt receipt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        receipt = default;
        if (!token.IsValid
            || _activation is not { } activation
            || activation.Token != token)
        {
            return RuntimeLocalPlayerPhysicsActivationStatus.RejectedToken;
        }
        if (!IsActivationCurrent(activation))
        {
            DiscardActivation();
            return RuntimeLocalPlayerPhysicsActivationStatus.RejectedAuthority;
        }
        if (!_physics.SetPosition.TryEvaluateDormantLocalActivation(
                activation.Record,
                activation.Body,
                token.Placement,
                activation.PlacementCommand,
                out RuntimeDormantSetPositionEvaluation placement))
        {
            if (ReferenceEquals(_activation, activation)
                && IsActivationCurrent(activation)
                && _physics.SetPosition.IsDormantLocalActivationAwaitingCell(
                    activation.Record,
                    activation.Body,
                    token.Placement,
                    activation.PlacementCommand))
            {
                return RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell;
            }
            if (ReferenceEquals(_activation, activation)
                && IsActivationCurrent(activation)
                && _physics.SetPosition.IsDormantLocalActivationLeaseCurrent(
                    activation.Record,
                    activation.Body,
                    token.Placement,
                    activation.PlacementCommand))
            {
                return RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell;
            }
            if (ReferenceEquals(_activation, activation)
                && !IsActivationCurrent(activation))
            {
                DiscardActivation();
            }
            return RuntimeLocalPlayerPhysicsActivationStatus.RejectedAuthority;
        }

        receipt = new RuntimeLocalPlayerPhysicsActivationReceipt(
            token,
            checked(++_nextEvaluationId),
            placement);
        activation.Receipt = receipt;
        if (placement.Result.IsDeferred)
            return RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell;
        return placement.Result.IsCommitted
            ? RuntimeLocalPlayerPhysicsActivationStatus.Evaluated
            : RuntimeLocalPlayerPhysicsActivationStatus.RejectedPlacement;
    }

    internal bool IsEvaluationCurrent(
        in RuntimeLocalPlayerPhysicsActivationReceipt receipt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!receipt.IsValid
            || _activation is not { } activation
            || activation.Token != receipt.Token
            || activation.Receipt != receipt)
        {
            return false;
        }
        return IsActivationCurrent(activation)
            && _physics.SetPosition.IsDormantLocalEvaluationCurrent(
                activation.Record,
                activation.Body,
                receipt.Placement);
    }

    internal RuntimeDormantSetPositionCommitStatus CommitActivation(
        in RuntimeLocalPlayerPhysicsActivationReceipt receipt,
        out RuntimePlacementProjectionToken projection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        projection = default;
        if (!receipt.IsValid
            || _activation is not { } activation
            || activation.Token != receipt.Token
            || activation.Receipt != receipt)
        {
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (activation.PendingFinalCommit.Status
            is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation)
        {
            return FinalizeActivation(
                activation,
                activation.PendingFinalCommit,
                out projection);
        }
        if (!IsActivationCurrent(activation)
            || !_physics.SetPosition.IsDormantLocalEvaluationCurrent(
                activation.Record,
                activation.Body,
                receipt.Placement))
        {
            activation.Receipt = default;
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }

        bool provenShapeless = activation.ActivationPreparation
            .ShadowDisposition
            is RuntimeLocalPlayerShadowDisposition.ProvenShapeless;
        if (!_physics.SetPosition.TryPrepareDormantLocalActivationCommit(
                activation.Record,
                activation.Body,
                receipt.Placement,
                provenShapeless,
                out PreparedDormantSetPositionCommit? prepared)
            || prepared is null
            || !IsActivationCurrent(activation))
        {
            activation.Receipt = default;
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }

        if (!_physics.SetPosition.TryApplyDormantLocalActivationCommit(
                activation.Record,
                activation.Body,
                activation.Controller,
                activation.PhysicsHost,
                prepared,
                out RuntimeDormantSetPositionCommitReceipt committed))
        {
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (committed.Status is RuntimeDormantSetPositionCommitStatus.DeferredCell)
        {
            activation.Receipt = default;
            _physics.SetPosition.DispatchDormantLocalActivationShadow(committed);
            return committed.Status;
        }

        try
        {
            activation.Controller.BeginDormantSetPositionGroundPhase();
            if (committed.HitGround)
                activation.Movement.HitGround();
            else if (committed.LeaveGround)
                activation.Motion.LeaveGround();
        }
        catch
        {
            _activationDispatchFailureCount++;
        }
        finally
        {
            activation.Controller.EndDormantSetPositionGroundPhase();
        }
        if (committed.Status is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation
            && (!IsActivationPrephaseEnvelopeCurrent(activation, committed)
                || !RefreshDormantVector(activation, committed)
                || !RefreshDormantState(activation)
                || !_physics.SetPosition.CommitDormantLocalActivationPostGround(
                    activation.Record,
                    activation.Body,
                    committed)))
        {
            AbortActivation(
                activation, committed, collisionAlreadyDispatched: false);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        try
        {
            SetPositionCollisionBatchDispatchResult collisionDispatch =
                _physics.SetPosition.DispatchDormantLocalActivationCollision(
                    committed);
            if (collisionDispatch.Status
                is not SetPositionCollisionBatchDispatchStatus.Completed)
            {
                AbortActivation(
                    activation, committed, collisionAlreadyDispatched: true);
                return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
            }
        }
        catch
        {
            _activationDispatchFailureCount++;
            AbortActivation(
                activation, committed, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (!IsActivationResponseEnvelopeCurrent(activation, committed))
        {
            AbortActivation(
                activation, committed, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        activation.Controller.RefreshDormantRuntimePhysicsState(
            activation.Record.FinalPhysicsState,
            recalculateAcceleration: false);
        _ = RefreshDormantVector(activation, committed);
        if (committed.Status is RuntimeDormantSetPositionCommitStatus
                .AwaitingFinalShadowPreparation
                && !IsActivationPrephaseEnvelopeCurrent(activation, committed)
            || !_physics.SetPosition.CommitDormantLocalActivationPostCollision(
                activation.Record,
                activation.Body,
                committed))
        {
            AbortActivation(
                activation, committed, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (committed.Status is RuntimeDormantSetPositionCommitStatus
                .RejectedPlacement)
        {
            activation.Receipt = default;
            activation.PendingFinalCommit = committed;
            return committed.Status;
        }
        activation.PendingFinalCommit = committed;
        return FinalizeActivation(activation, committed, out projection);
    }

    private RuntimeDormantSetPositionCommitStatus FinalizeActivation(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt prephase,
        out RuntimePlacementProjectionToken projection)
    {
        projection = default;
        if (!IsActivationPrephaseEnvelopeCurrent(activation, prephase))
        {
            AbortActivation(
                activation, prephase, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        activation.Controller.RefreshDormantRuntimePhysicsState(
            activation.Record.FinalPhysicsState,
            recalculateAcceleration: false);
        bool provenShapeless = activation.ActivationPreparation
            .ShadowDisposition is RuntimeLocalPlayerShadowDisposition.ProvenShapeless;
        if (!_physics.SetPosition.TryPrepareDormantLocalActivationFinalCommit(
                activation.Record,
                activation.Body,
                prephase,
                provenShapeless,
                out PreparedDormantActivationFinalCommit? prepared)
            || prepared is null)
        {
            if (IsActivationPrephaseEnvelopeCurrent(activation, prephase))
                return prephase.Status;
            AbortActivation(
                activation, prephase, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (!IsActivationPrephaseEnvelopeCurrent(activation, prephase))
        {
            AbortActivation(
                activation, prephase, collisionAlreadyDispatched: true);
            return RuntimeDormantSetPositionCommitStatus.RejectedAuthority;
        }
        if (!_physics.SetPosition.TryApplyDormantLocalActivationFinalCommit(
                activation.Record,
                activation.Body,
                activation.Controller,
                activation.PhysicsHost,
                prephase,
                prepared,
                out RuntimeDormantSetPositionCommitReceipt committed))
        {
            return prephase.Status;
        }
        activation.Receipt = default;
        activation.PendingFinalCommit = default;
        _activation = null;
        _physics.SetPosition.DispatchDormantLocalActivationShadow(committed);
        if (!IsCommittedActivationSuffixCurrent(activation, committed))
            return committed.Status;
        ArmFirstEntryConstraintLeash(activation);
        SettleFirstEntryGroundContact(activation);
        _physics.SetPosition.DispatchDormantLocalActivationPlacement(committed);
        projection = committed.Projection.Token;
        return committed.Status;
    }

    private void ArmFirstEntryConstraintLeash(Activation activation)
    {
        try
        {
            activation.Controller.ArmConstraintLeashAtCommittedPlacement();
        }
        catch
        {
            _activationDispatchFailureCount++;
        }
    }

    private void SettleFirstEntryGroundContact(Activation activation)
    {
        try
        {
            _ = SpawnPlacementSettler.TrySettle(
                _physics.Engine,
                activation.Body,
                activation.Body.Position,
                activation.Body.CellPosition.ObjCellId,
                activation.ActivationPreparation.Radius,
                activation.ActivationPreparation.Height,
                ObjectInfoState.IsPlayer
                    | ObjectInfoState.EdgeSlide
                    | activation.Controller.OwnPvpFlags,
                activation.Controller.LocalEntityId,
                activation.Movement.HitGround,
                activation.Motion.LeaveGround);
        }
        catch
        {
            _activationDispatchFailureCount++;
        }
    }

    private bool IsActivationPrephaseEnvelopeCurrent(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt receipt) =>
        IsActivationOwnershipEnvelopeCurrent(activation)
        && _physics.IsCollisionEvaluationFatalAuthorityCurrent(
            receipt.CollisionAuthority)
        && _physics.SetPosition.IsDormantLocalActivationPrephaseCurrent(
            activation.Record,
            activation.Body,
            receipt);

    private bool IsActivationResponseEnvelopeCurrent(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt receipt) =>
        IsActivationOwnershipEnvelopeCurrent(activation)
        && _physics.IsCollisionEvaluationFatalAuthorityCurrent(
            receipt.CollisionAuthority)
        && _physics.SetPosition.IsDormantLocalActivationResponseCurrent(
            activation.Record,
            activation.Body,
            receipt);

    private bool IsActivationOwnershipEnvelopeCurrent(
        Activation activation) =>
        activation.Controller.IsRuntimeOwnedDormant
        && !activation.Controller.IsDormantSetPositionGroundPhaseActive
        && activation.Controller.OwnsPhysicsBody(activation.Body)
        && _entities.SessionLifetimeVersion
            == activation.Token.SessionGenerationAuthority
        && _entities.IsCurrent(activation.Record)
        && activation.Record.Key == activation.Token.Entity
        && !_identity.IsDisposed
        && _identity.ServerGuid == activation.Token.LocalPlayerServerGuid
        && _identity.ServerGuid == activation.Record.ServerGuid
        && _identity.Revision == activation.Token.LocalPlayerIdentityRevision
        && activation.Record.PhysicsOwnershipEpoch
            == activation.Token.PhysicsOwnershipEpoch
        && activation.Record.ObjectClockEpoch
            == activation.Token.ObjectClockEpoch
        && _movement.CanCommitRuntimeOwnedController(
            activation.Token.ControllerOwnershipEpoch,
            activation.Controller)
        && ReferenceEquals(activation.Record.PhysicsBody, activation.Body)
        && activation.Record.PhysicsHost is null
        && activation.Record.RemoteMotion is null
        && activation.Record.Projectile is null
        && !activation.Record.PhysicsBodyAcquisitionInProgress
        && !activation.Record.RemoteMotionBindingInProgress
        && !activation.Record.ProjectileBindingInProgress
        && !activation.Record.RequiresRemotePlacementRuntime
        && !activation.Record.DeleteAcceptedForTeardown;

    private static bool RefreshDormantState(Activation activation)
    {
        activation.Controller.RefreshDormantRuntimePhysicsState(
            activation.Record.FinalPhysicsState,
            recalculateAcceleration: false);
        return true;
    }

    private static bool RefreshDormantVector(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        if (activation.Record.VectorAuthorityVersion
            == receipt.SourceVectorAuthorityVersion)
            return true;
        var physics = activation.Record.Snapshot.Physics;
        activation.Controller.RefreshDormantRuntimeVector(
            physics?.Velocity,
            physics?.AngularVelocity);
        return true;
    }

    private void AbortActivation(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt receipt,
        bool collisionAlreadyDispatched)
    {
        _physics.SetPosition.RetireDormantLocalActivation(
            receipt,
            collisionAlreadyDispatched);
        activation.Receipt = default;
        activation.PendingFinalCommit = default;
        if (ReferenceEquals(_activation, activation))
            DiscardActivation();
    }

    internal bool DiscardActivation(
        in RuntimeLocalPlayerPhysicsActivationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!token.IsValid
            || _activation is not { } activation
            || activation.Token != token)
        {
            return false;
        }
        DiscardActivation();
        return true;
    }

    internal RuntimeLocalPlayerPhysicsPublicationStatus Discard(
        in RuntimeLocalPlayerPhysicsPublicationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!token.IsValid
            || _candidate is not { } candidate
            || candidate.Token != token)
        {
            return RuntimeLocalPlayerPhysicsPublicationStatus.RejectedToken;
        }
        DiscardCurrent();
        return RuntimeLocalPlayerPhysicsPublicationStatus.Discarded;
    }

    internal bool TryCaptureCandidateSnapshot(
        in RuntimeLocalPlayerPhysicsPublicationToken token,
        out RuntimeLocalPlayerPhysicsCandidateSnapshot snapshot)
    {
        if (!_disposed
            && token.IsValid
            && _candidate is { } candidate
            && candidate.Token == token)
        {
            PhysicsBody body = candidate.Body;
            snapshot = new RuntimeLocalPlayerPhysicsCandidateSnapshot(
                body.Position,
                body.Orientation,
                body.CellPosition.ObjCellId,
                body.CellPosition.Frame.Origin,
                body.State,
                body.TransientState,
                body.InWorld);
            return true;
        }
        snapshot = default;
        return false;
    }

    internal RuntimeLocalPlayerPhysicsPublicationOwnershipSnapshot
        CaptureOwnership() => new(
            IsBound: true,
            _disposed,
            _candidate is null ? 0 : 1,
            _activation is null ? 0 : 1,
            _nextPublicationId);

    internal long ActivationDispatchFailureCount =>
        _activationDispatchFailureCount;

    internal void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DiscardCurrent();
        DiscardActivation();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DiscardCurrent();
        DiscardActivation();
        _disposed = true;
    }

    private bool CanPrepare(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken placement,
        in RuntimeSetPositionCommand command) =>
        _activation is null
        && record.Key is { } key
        && key == placement.Entity
        && _entities.IsCurrent(record)
        && !record.DeleteAcceptedForTeardown
        && command.Kind is RuntimeSetPositionOperationKind.InitialLogin
            or RuntimeSetPositionOperationKind.LocalAuthoritative
        && command.Physics.MovingEntityId == key.LocalEntityId
        && !_identity.IsDisposed
        && _identity.ServerGuid != 0u
        && _identity.ServerGuid == record.ServerGuid
        && record.PhysicsBody is null
        && _movement.Controller is null
        && record.PhysicsHost is null
        && record.RemoteMotion is null
        && record.Projectile is null
        && !record.PhysicsBodyAcquisitionInProgress
        && !record.RemoteMotionBindingInProgress
        && !record.ProjectileBindingInProgress
        && !record.RequiresRemotePlacementRuntime
        && _physics.SetPosition.IsExactPreparedPlacementCurrent(
            record,
            placement,
            command);

    private bool IsCurrent(Candidate candidate) =>
        _activation is null
        && candidate.Controller.IsSealedPublicationCandidate
        && candidate.Controller.OwnsPhysicsBody(candidate.Body)
        && _entities.SessionLifetimeVersion
            == candidate.Token.SessionGenerationAuthority
        && _entities.IsCurrent(candidate.Record)
        && candidate.Record.Key == candidate.Token.Entity
        && !_identity.IsDisposed
        && _identity.ServerGuid == candidate.Token.LocalPlayerServerGuid
        && _identity.ServerGuid == candidate.Record.ServerGuid
        && _identity.Revision == candidate.Token.LocalPlayerIdentityRevision
        && candidate.Record.PhysicsOwnershipEpoch
            == candidate.Token.PhysicsOwnershipEpoch
        && candidate.Record.ObjectClockEpoch
            == candidate.Token.ObjectClockEpoch
        && _movement.CanCommitRuntimeOwnedController(
            candidate.Token.ControllerOwnershipEpoch,
            expectedController: null)
        && candidate.Record.PhysicsBody is null
        && candidate.Record.PhysicsHost is null
        && candidate.Record.RemoteMotion is null
        && candidate.Record.Projectile is null
        && !candidate.Record.PhysicsBodyAcquisitionInProgress
        && !candidate.Record.RemoteMotionBindingInProgress
        && !candidate.Record.ProjectileBindingInProgress
        && !candidate.Record.RequiresRemotePlacementRuntime
        && !candidate.Record.DeleteAcceptedForTeardown
        && _physics.SetPosition.IsExactPreparedPlacementCurrent(
            candidate.Record,
            candidate.Token.Placement,
            candidate.PlacementCommand);

    private void DiscardCurrent()
    {
        Candidate? candidate = _candidate;
        _candidate = null;
        candidate?.Controller.DiscardRuntimeCandidate();
    }

    private bool IsActivationCurrent(Activation activation) =>
        activation.Controller.IsRuntimeOwnedDormant
        && activation.Controller.OwnsPhysicsBody(activation.Body)
        && _entities.SessionLifetimeVersion
            == activation.Token.SessionGenerationAuthority
        && _entities.IsCurrent(activation.Record)
        && activation.Record.Key == activation.Token.Entity
        && !_identity.IsDisposed
        && _identity.ServerGuid == activation.Token.LocalPlayerServerGuid
        && _identity.ServerGuid == activation.Record.ServerGuid
        && _identity.Revision == activation.Token.LocalPlayerIdentityRevision
        && activation.Record.PhysicsOwnershipEpoch
            == activation.Token.PhysicsOwnershipEpoch
        && activation.Record.ObjectClockEpoch
            == activation.Token.ObjectClockEpoch
        && _movement.CanCommitRuntimeOwnedController(
            activation.Token.ControllerOwnershipEpoch,
            activation.Controller)
        && ReferenceEquals(activation.Record.PhysicsBody, activation.Body)
        && activation.Record.PhysicsHost is null
        && activation.Record.RemoteMotion is null
        && activation.Record.Projectile is null
        && !activation.Record.PhysicsBodyAcquisitionInProgress
        && !activation.Record.RemoteMotionBindingInProgress
        && !activation.Record.ProjectileBindingInProgress
        && !activation.Record.RequiresRemotePlacementRuntime
        && !activation.Record.DeleteAcceptedForTeardown
        && _physics.SetPosition.IsDormantLocalActivationLeaseCurrent(
            activation.Record,
            activation.Body,
            activation.Token.Placement,
            activation.PlacementCommand);

    private bool IsCommittedActivationSuffixCurrent(
        Activation activation,
        in RuntimeDormantSetPositionCommitReceipt receipt)
    {
        return activation.Token.ObjectClockEpoch != ulong.MaxValue
            && _entities.SessionLifetimeVersion
                == activation.Token.SessionGenerationAuthority
            && _entities.IsCurrent(activation.Record)
            && activation.Record.Key == activation.Token.Entity
            && !_identity.IsDisposed
            && _identity.ServerGuid == activation.Token.LocalPlayerServerGuid
            && _identity.ServerGuid == activation.Record.ServerGuid
            && _identity.Revision
                == activation.Token.LocalPlayerIdentityRevision
            && activation.Record.PhysicsOwnershipEpoch
                == activation.Token.PhysicsOwnershipEpoch
            && activation.Record.ObjectClockEpoch
                == activation.Token.ObjectClockEpoch + 1UL
            && _movement.ControllerOwnershipEpoch
                == activation.Token.ControllerOwnershipEpoch
            && ReferenceEquals(_movement.Controller, activation.Controller)
            && activation.Controller.IsRuntimePublished
            && activation.Controller.OwnsPhysicsBody(activation.Body)
            && ReferenceEquals(
                activation.Record.PhysicsBody,
                activation.Body)
            && ReferenceEquals(
                activation.Record.PhysicsHost,
                activation.PhysicsHost)
            && activation.Record.RemoteMotion is null
            && activation.Record.Projectile is null
            && !activation.Record.DeleteAcceptedForTeardown
            && _physics.SetPosition.IsDormantLocalActivationCommitCurrent(
                activation.Record,
                activation.Body,
                receipt);
    }

    private void DiscardActivation()
    {
        Activation? activation = _activation;
        _activation = null;
        if (activation is not null)
        {
            if (activation.PendingFinalCommit.Status
                is not RuntimeDormantSetPositionCommitStatus.None)
            {
                _physics.SetPosition.RetireDormantLocalActivation(
                    activation.PendingFinalCommit,
                    collisionAlreadyDispatched: true);
            }
            _physics.SetPosition.RetireDormantLocalActivationToken(
                activation.Record,
                activation.Token.Placement);
        }
        if (activation is not null
            && _entities.IsCurrent(activation.Record)
            && ReferenceEquals(
                activation.Record.PhysicsBody,
                activation.Body))
        {
            _entities.SetPhysicsBody(activation.Record, null);
        }
        if (activation is not null
            && ReferenceEquals(_movement.Controller, activation.Controller))
        {
            _movement.Controller = null;
        }
    }
}
