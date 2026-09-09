using System.Numerics;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Session;

internal enum RuntimeAcceptedPositionExecutionStatus : byte
{
    NotApplicable,

    Rejected,

    Contention,

    DeferredCell,

    Committed,
}

public sealed class RuntimeAcceptedPositionDriveController
{
    private sealed class Pending
    {
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeEntityPlacementToken Token { get; init; }
        internal required RuntimeAuthoritativePositionRoute Route { get; init; }
        internal required bool AwaitingCommitWake { get; init; }

        internal required bool PositionEventOwed { get; init; }

        internal RuntimePortalPlacementAuthority Portal { get; init; }
    }

    private readonly record struct AcceptedForceObservation(
        RuntimeEntityKey Entity,
        ulong PositionAuthorityVersion,
        RuntimeAuthoritativePositionRoute Route);

    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly IGameRuntimeClock _clock;
    private readonly IPreparedCollisionSource _collisionSource;
    private readonly LocalPlayerOutboundController _localPlayerOutbound;
    private readonly Func<RuntimeGenerationToken> _generation;
    private readonly Func<uint> _localPlayerServerGuid;
    private readonly Func<PlayerMovementController?> _localController;
    private readonly Func<bool> _usePositionFromServer;
    private readonly Func<WorldSession?> _session;

    private readonly Func<RuntimeLocalPlayerMovementState?> _localMovementState;

    private readonly Func<RuntimePortalPlacementAuthority, bool>?
        _isPortalAuthorityCurrent;

    private Pending? _pending;
    private AcceptedForceObservation? _newestForce;
    private object? _routeOwner;

    internal RuntimeAcceptedPositionDriveController(
        RuntimeEntityObjectLifetime entityObjects,
        IGameRuntimeClock clock,
        IPreparedCollisionSource collisionSource,
        LocalPlayerOutboundController localPlayerOutbound,
        Func<RuntimeGenerationToken> generation,
        Func<uint> localPlayerServerGuid,
        Func<PlayerMovementController?> localController,
        Func<bool> usePositionFromServer,
        Func<WorldSession?> session,
        Func<RuntimeLocalPlayerMovementState?>? localMovementState = null,
        Func<RuntimePortalPlacementAuthority, bool>? isPortalAuthorityCurrent = null)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _collisionSource = collisionSource
            ?? throw new ArgumentNullException(nameof(collisionSource));
        _localPlayerOutbound = localPlayerOutbound
            ?? throw new ArgumentNullException(nameof(localPlayerOutbound));
        _generation = generation
            ?? throw new ArgumentNullException(nameof(generation));
        _localPlayerServerGuid = localPlayerServerGuid
            ?? throw new ArgumentNullException(nameof(localPlayerServerGuid));
        _localController = localController
            ?? throw new ArgumentNullException(nameof(localController));
        _usePositionFromServer = usePositionFromServer
            ?? throw new ArgumentNullException(nameof(usePositionFromServer));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _localMovementState = localMovementState ?? (static () => null);
        _isPortalAuthorityCurrent = isPortalAuthorityCurrent;
        _entityObjects.RegisterAcceptedPositionDriveOwnership(
            () => _pending is null ? 0 : 1);
    }

    internal int PendingCount => _pending is null ? 0 : 1;

    private (long RevealGeneration, ushort TeleportSequence)? _lastCommittedPortal;

    internal bool TryConsumePortalCommit(
        long revealGeneration,
        ushort teleportSequence)
    {
        if (_lastCommittedPortal is not { } committed
            || committed.RevealGeneration != revealGeneration
            || committed.TeleportSequence != teleportSequence)
        {
            return false;
        }
        _lastCommittedPortal = null;
        return true;
    }

    internal void AttachRoute(object route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (_routeOwner is not null && !ReferenceEquals(_routeOwner, route))
        {
            throw new InvalidOperationException(
                "An accepted-position drive controller serves one session "
                + "route at a time; the prior route must be disposed "
                + "(session reset precedes a new route) before a "
                + "replacement attaches.");
        }
        _routeOwner = route;
    }

    /// <summary>
    /// Route-scoped teardown: abandons any pending operation, but ONLY when
    /// <paramref name="route"/> is the attached owner.
    /// </summary>
    internal void DetachRoute(object route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!ReferenceEquals(_routeOwner, route))
            return;
        _routeOwner = null;
        AbandonPending();
    }

    private void AbandonPending()
    {
        _newestForce = null;
        _lastCommittedPortal = null;
        if (_pending is not { } pending)
            return;
        _pending = null;
        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        setPosition.ForgetPlacementCompletion(pending.Token);
        RuntimePlacementCancellationReceipt cancellation =
            setPosition.ForgetExactPlacement(
                pending.Token,
                restoreCancelledPark: true);
        if (cancellation.IsValid)
            setPosition.PublishCancellation(cancellation);
    }

    internal RuntimeAcceptedPositionExecutionStatus TryExecuteAcceptedLocalPosition(
        RuntimeEntityRecord record,
        in WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        in AcceptedPhysicsTimestamps timestamps,
        ushort previousTeleportSequence)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (disposition is not PositionTimestampDisposition.ForcePosition
            || record.ServerGuid != _localPlayerServerGuid()
            || record.PhysicsBody is null
            || record.Key is not { } key
            || _entityObjects.TryGetInitialCreateResidence(record, out _))
        {
            return RuntimeAcceptedPositionExecutionStatus.NotApplicable;
        }

        RuntimeAuthoritativePositionRoute route = ClassifyForcePosition(
            record, key, update, disposition, timestamps, previousTeleportSequence);
        if (!route.Accepted)
        {
            return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }

        ulong acceptedVersion = record.PositionAuthorityVersion;
        _newestForce = new AcceptedForceObservation(key, acceptedVersion, route);

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        RuntimeEntityPlacementToken token =
            setPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                acceptedVersion,
                route.OperationKind);
        if (!token.IsValid)
        {
            return RuntimeAcceptedPositionExecutionStatus.Contention;
        }

        return SubmitAndResolve(record, token, route);
    }

    internal RuntimeAcceptedPositionExecutionStatus TryExecuteAcceptedPortalArrival(
        in RuntimeTeleportDestination destination,
        in RuntimePortalPlacementAuthority portal)
    {
        if (!portal.IsValid
            || !_entityObjects.Entities.TryGetActive(
                _localPlayerServerGuid(), out RuntimeEntityRecord record)
            || record.PhysicsBody is null
            || record.Key is not { } key
            || _entityObjects.TryGetInitialCreateResidence(record, out _))
        {
            LogPortalArrivalAttempt(
                RuntimeAcceptedPositionExecutionStatus.NotApplicable,
                portal,
                resolvedCell: 0u);
            return RuntimeAcceptedPositionExecutionStatus.NotApplicable;
        }

        RuntimeAuthoritativePositionRoute route = ClassifyPortalArrival(
            record, key, destination, _generation());
        if (!route.Accepted)
        {
            LogPortalArrivalAttempt(
                RuntimeAcceptedPositionExecutionStatus.Rejected,
                portal,
                record.FullCellId);
            return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        ulong acceptedVersion = record.PositionAuthorityVersion;
        RuntimeEntityPlacementToken token =
            setPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                acceptedVersion,
                route.OperationKind,
                portal);
        if (!token.IsValid)
        {
            LogPortalArrivalAttempt(
                RuntimeAcceptedPositionExecutionStatus.Contention,
                portal,
                record.FullCellId);
            return RuntimeAcceptedPositionExecutionStatus.Contention;
        }

        return SubmitAndResolvePortal(record, token, route, portal);
    }

    private static RuntimeAuthoritativePositionRoute ClassifyPortalArrival(
        RuntimeEntityRecord record,
        RuntimeEntityKey key,
        in RuntimeTeleportDestination destination,
        RuntimeGenerationToken generation)
    {
        ushort acceptedTeleport = destination.TeleportSequence;
        ushort priorTeleport = unchecked((ushort)(acceptedTeleport - 1));
        var authority = new RuntimeAuthoritativePositionAuthority(
            generation,
            key,
            record.PositionAuthorityVersion,
            destination.PositionSequence,
            priorTeleport,
            acceptedTeleport,
            PositionTimestampDisposition.Apply);

        bool hasAnimations = (record.Snapshot.MotionTableId
                ?? record.Snapshot.Physics?.MotionTableId) is { } motionTableId
            && motionTableId != 0u;

        var wirePosition = new CreateObject.ServerPosition(
            destination.CellId,
            destination.Position.Frame.Origin.X,
            destination.Position.Frame.Origin.Y,
            destination.Position.Frame.Origin.Z,
            destination.Position.Frame.Orientation.W,
            destination.Position.Frame.Orientation.X,
            destination.Position.Frame.Orientation.Y,
            destination.Position.Frame.Orientation.Z);

        var request = new RuntimeAcceptedPositionRouteRequest(
            authority,
            RuntimePositionEntityKind.LocalPlayer,
            RuntimeAcceptedPositionSource.PositionEvent,
            wirePosition,
            PlacementFrame: null,
            PositionPackVelocity: null,
            CommittedCellId: record.FullCellId,
            HasContact: false,
            PlayerDistance: 0f,
            UsePositionFromServer: false,
            hasAnimations,
            new RuntimePositionPlacementFacts(
                record.FinalPhysicsState,
                record.Snapshot.SetupTableId is not null));

        return RuntimeAuthoritativePositionRouteClassifier
            .ClassifyAcceptedPosition(request);
    }

    private RuntimeAcceptedPositionExecutionStatus SubmitAndResolvePortal(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token,
        in RuntimeAuthoritativePositionRoute route,
        in RuntimePortalPlacementAuthority portal)
    {
        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        RuntimeSetPositionMoverPreparationStatus status =
            setPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                route.OperationKind,
                route.SetPositionFlags,
                _collisionSource,
                _clock.SimulationTimeSeconds,
                out RuntimeSetPositionOutcome outcome,
                portal: portal,
                resolveWorldOffsetFromRuntimeFrame: true);

        if (status != RuntimeSetPositionMoverPreparationStatus.Prepared)
        {
            if (status.IsRetryable())
            {
                RetainPending(setPosition, new Pending
                {
                    Record = record,
                    Token = token,
                    Route = route,
                    AwaitingCommitWake = false,
                    PositionEventOwed = false,
                    Portal = portal,
                });
                LogPortalArrivalAttempt(
                    RuntimeAcceptedPositionExecutionStatus.Contention,
                    portal,
                    record.FullCellId);
                return RuntimeAcceptedPositionExecutionStatus.Contention;
            }

            CancelToken(setPosition, token);
            LogPortalArrivalAttempt(
                RuntimeAcceptedPositionExecutionStatus.Rejected,
                portal,
                record.FullCellId);
            return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }

        switch (outcome.Status)
        {
            case RuntimeSetPositionStatus.CommittedHostAcknowledgementPending:
                ReconcileAndAcknowledgePortal(record, route, portal);
                return RuntimeAcceptedPositionExecutionStatus.Committed;

            case RuntimeSetPositionStatus.DeferredCell:
                while (setPosition.TryPeekProjection(
                        out RuntimePlacementProjectionSnapshot parked)
                    && parked.Token.Entity == token.Entity
                    && parked.Kind is RuntimePlacementProjectionKind.Withdraw)
                {
                    if (!setPosition.AcknowledgeProjection(parked.Token))
                        break;
                }
                if (!setPosition.WatchPlacementCompletion(token))
                {
                    CancelToken(setPosition, token);
                    LogPortalArrivalAttempt(
                        RuntimeAcceptedPositionExecutionStatus.Rejected,
                        portal,
                        record.FullCellId);
                    return RuntimeAcceptedPositionExecutionStatus.Rejected;
                }
                RetainPending(setPosition, new Pending
                {
                    Record = record,
                    Token = token,
                    Route = route,
                    AwaitingCommitWake = true,
                    PositionEventOwed = false,
                    Portal = portal,
                });
                LogPortalArrivalAttempt(
                    RuntimeAcceptedPositionExecutionStatus.DeferredCell,
                    portal,
                    record.FullCellId);
                return RuntimeAcceptedPositionExecutionStatus.DeferredCell;

            default:
                CancelToken(setPosition, token);
                LogPortalArrivalAttempt(
                    RuntimeAcceptedPositionExecutionStatus.Rejected,
                    portal,
                    record.FullCellId);
                return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }
    }

    private static void LogPortalArrivalAttempt(
        RuntimeAcceptedPositionExecutionStatus status,
        in RuntimePortalPlacementAuthority portal,
        uint resolvedCell)
    {
        PhysicsDiagnostics.LogLocalTeleportArrival(
            cause: "portal",
            placementStatus: status.ToString(),
            portalGeneration: portal.RevealGeneration,
            teleportSequence: portal.TeleportSequence,
            destinationCell: portal.Projection.DestinationCell,
            resolvedCell: resolvedCell,
            hookTailRan: false,
            leashArmed: false,
            autorunCancelled: false);
    }

    private bool IsPortalAuthorityCurrent(
        in RuntimePortalPlacementAuthority portal) =>
        _isPortalAuthorityCurrent is null || _isPortalAuthorityCurrent(portal);

    private void ReconcileAndAcknowledgePortal(
        RuntimeEntityRecord record,
        in RuntimeAuthoritativePositionRoute route,
        in RuntimePortalPlacementAuthority portal)
    {
        _lastCommittedPortal = (portal.RevealGeneration, portal.TeleportSequence);
        if (record.ServerGuid != _localPlayerServerGuid())
            return;
        if (_localController() is not { } controller)
            return;
        bool hookTailRan = route.RunsTeleportHook;
        controller.CommitCanonicalTeleportFrame(
            zeroVelocity: route.ZeroVelocity,
            rearmConstraintLeash: route.ConstrainAfterRouting,
            runTeleportHookTail: hookTailRan);
        bool autorunCancelled = _localMovementState()?.CancelAutoRun() ?? false;
        if (!_usePositionFromServer())
        {
            _localPlayerOutbound.TrySendMovement(
                _session(),
                controller,
                controller.CapturePresentationResult());
        }

        PhysicsDiagnostics.LogLocalTeleportArrival(
            cause: "portal",
            placementStatus: "Committed",
            portalGeneration: portal.RevealGeneration,
            teleportSequence: portal.TeleportSequence,
            destinationCell: portal.Projection.DestinationCell,
            resolvedCell: record.FullCellId,
            hookTailRan: hookTailRan,
            leashArmed: controller.PositionManager?.Constraint?.IsConstrained
                ?? false,
            autorunCancelled: autorunCancelled);
    }

    internal void Advance()
    {
        if (_pending is not { } pending)
            return;

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        if (pending.AwaitingCommitWake)
        {
            if (setPosition.TryPeekAcknowledgedPlacement(
                    pending.Token,
                    out RuntimePlacementProjectionToken projection))
            {
                if (!setPosition.ConsumeAcknowledgedPlacement(
                        pending.Token, projection))
                {
                    return;
                }
                if (pending.Portal.Present)
                {
                    _pending = null;
                    if (!IsPortalAuthorityCurrent(pending.Portal))
                    {
                        PhysicsDiagnostics.LogLocalTeleportArrival(
                            cause: "portal",
                            placementStatus: "AbandonedAtWake",
                            portalGeneration: pending.Portal.RevealGeneration,
                            teleportSequence: pending.Portal.TeleportSequence,
                            destinationCell:
                                pending.Portal.Projection.DestinationCell,
                            resolvedCell: pending.Record.FullCellId,
                            hookTailRan: false,
                            leashArmed: false,
                            autorunCancelled: false);
                        return;
                    }
                    ReconcileAndAcknowledgePortal(
                        pending.Record, pending.Route, pending.Portal);
                    return;
                }
                ReconcileAndAcknowledge(pending.Record, pending.Route);
                SettlePending(
                    pending.Record,
                    pending.Token,
                    pending.Route,
                    positionEventOwed: false);
                return;
            }

            if (setPosition.IsPlacementCompletionTracked(pending.Token))
            {
                // Still parked, watch alive. Nothing more this pump can do.
                return;
            }

            if (pending.Portal.Present)
            {
                _pending = null;
                PhysicsDiagnostics.LogLocalTeleportArrival(
                    cause: "portal",
                    placementStatus: "WatchDied",
                    portalGeneration: pending.Portal.RevealGeneration,
                    teleportSequence: pending.Portal.TeleportSequence,
                    destinationCell: pending.Portal.Projection.DestinationCell,
                    resolvedCell: pending.Record.FullCellId,
                    hookTailRan: false,
                    leashArmed: false,
                    autorunCancelled: false);
                return;
            }
            SettlePending(
                pending.Record,
                pending.Token,
                pending.Route,
                pending.PositionEventOwed);
            return;
        }

        if (setPosition.IsPlacementCurrent(pending.Token))
        {
            if (pending.Portal.Present
                && !IsPortalAuthorityCurrent(pending.Portal))
            {
                _pending = null;
                CancelToken(setPosition, pending.Token);
                PhysicsDiagnostics.LogLocalTeleportArrival(
                    cause: "portal",
                    placementStatus: "AbandonedAtWake",
                    portalGeneration: pending.Portal.RevealGeneration,
                    teleportSequence: pending.Portal.TeleportSequence,
                    destinationCell: pending.Portal.Projection.DestinationCell,
                    resolvedCell: pending.Record.FullCellId,
                    hookTailRan: false,
                    leashArmed: false,
                    autorunCancelled: false);
                return;
            }
            _ = pending.Portal.Present
                ? SubmitAndResolvePortal(
                    pending.Record, pending.Token, pending.Route, pending.Portal)
                : SubmitAndResolve(pending.Record, pending.Token, pending.Route);
            return;
        }

        if (pending.Portal.Present)
        {
            _pending = null;
            PhysicsDiagnostics.LogLocalTeleportArrival(
                cause: "portal",
                placementStatus: "PrepareRetryLost",
                portalGeneration: pending.Portal.RevealGeneration,
                teleportSequence: pending.Portal.TeleportSequence,
                destinationCell: pending.Portal.Projection.DestinationCell,
                resolvedCell: pending.Record.FullCellId,
                hookTailRan: false,
                leashArmed: false,
                autorunCancelled: false);
            return;
        }
        SettlePending(
            pending.Record,
            pending.Token,
            pending.Route,
            pending.PositionEventOwed);
    }

    private void SettlePending(
        RuntimeEntityRecord terminalRecord,
        in RuntimeEntityPlacementToken terminalToken,
        in RuntimeAuthoritativePositionRoute terminalRoute,
        bool positionEventOwed)
    {
        _pending = null;

        if (!_entityObjects.Entities.TryGetActive(
                terminalRecord.ServerGuid, out RuntimeEntityRecord record)
            || record.ServerGuid != _localPlayerServerGuid()
            || record.PhysicsBody is null
            || record.Key is not { } key
            || key != terminalToken.Entity)
        {
            return;
        }

        if (positionEventOwed && _localController() is { } terminalController)
            SendPositionEvent(terminalController, terminalRoute);

        ulong current = record.PositionAuthorityVersion;
        if (terminalToken.PositionAuthorityVersion == current)
            return;

        if (_newestForce is not { } newest
            || newest.Entity != key
            || newest.PositionAuthorityVersion != current)
        {
            return;
        }

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        RuntimeEntityPlacementToken token =
            setPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                current,
                newest.Route.OperationKind);
        if (!token.IsValid)
        {
            _pending = new Pending
            {
                Record = terminalRecord,
                Token = terminalToken,
                Route = terminalRoute,
                AwaitingCommitWake = false,
                PositionEventOwed = false,
            };
            return;
        }

        _ = SubmitAndResolve(record, token, newest.Route);
    }

    private RuntimeAcceptedPositionExecutionStatus SubmitAndResolve(
        RuntimeEntityRecord record,
        in RuntimeEntityPlacementToken token,
        in RuntimeAuthoritativePositionRoute route)
    {
        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        RuntimeSetPositionMoverPreparationStatus status =
            setPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                route.OperationKind,
                route.SetPositionFlags,
                _collisionSource,
                _clock.SimulationTimeSeconds,
                out RuntimeSetPositionOutcome outcome,
                resolveWorldOffsetFromRuntimeFrame: true);

        if (status != RuntimeSetPositionMoverPreparationStatus.Prepared)
        {
            if (status.IsRetryable())
            {
                RetainPending(setPosition, new Pending
                {
                    Record = record,
                    Token = token,
                    Route = route,
                    AwaitingCommitWake = false,
                    PositionEventOwed = true,
                });
                return RuntimeAcceptedPositionExecutionStatus.Contention;
            }

            CancelToken(setPosition, token);
            SettlePending(record, token, route, positionEventOwed: true);
            return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }

        switch (outcome.Status)
        {
            case RuntimeSetPositionStatus.CommittedHostAcknowledgementPending:
                ReconcileAndAcknowledge(record, route);
                SettlePending(record, token, route, positionEventOwed: false);
                return RuntimeAcceptedPositionExecutionStatus.Committed;

            case RuntimeSetPositionStatus.DeferredCell:
                while (setPosition.TryPeekProjection(
                        out RuntimePlacementProjectionSnapshot parked)
                    && parked.Token.Entity == token.Entity
                    && parked.Kind is RuntimePlacementProjectionKind.Withdraw)
                {
                    if (!setPosition.AcknowledgeProjection(parked.Token))
                        break;
                }
                if (!setPosition.WatchPlacementCompletion(token))
                {
                    CancelToken(setPosition, token);
                    SettlePending(record, token, route, positionEventOwed: true);
                    return RuntimeAcceptedPositionExecutionStatus.Rejected;
                }
                RetainPending(setPosition, new Pending
                {
                    Record = record,
                    Token = token,
                    Route = route,
                    AwaitingCommitWake = true,
                    PositionEventOwed = true,
                });
                return RuntimeAcceptedPositionExecutionStatus.DeferredCell;

            default:
                CancelToken(setPosition, token);
                SettlePending(record, token, route, positionEventOwed: true);
                return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }
    }

    private void RetainPending(
        RuntimeSetPositionState setPosition,
        Pending next)
    {
        if (_pending is { } existing
            && existing.Token != next.Token
            && setPosition.IsPlacementCurrent(existing.Token))
        {
            throw new InvalidOperationException(
                "RuntimeAcceptedPositionDriveController tracks at most one "
                + "pending accepted-position operation (the local player); "
                + "a still-live pending operation must never be silently "
                + "overwritten by a new one.");
        }
        _pending = next;
    }

    private static void CancelToken(
        RuntimeSetPositionState setPosition,
        in RuntimeEntityPlacementToken token)
    {
        RuntimePlacementCancellationReceipt cancellation =
            setPosition.ForgetExactPlacement(
                token,
                restoreCancelledPark: true);
        if (cancellation.IsValid)
            setPosition.PublishCancellation(cancellation);
    }

    private void ReconcileAndAcknowledge(
        RuntimeEntityRecord record,
        in RuntimeAuthoritativePositionRoute route)
    {
        if (record.ServerGuid != _localPlayerServerGuid())
            return;
        if (_localController() is not { } controller)
            return;
        controller.CommitCanonicalForcePositionFrame();
        SendPositionEvent(controller, route);
    }

    private void SendPositionEvent(
        PlayerMovementController controller,
        in RuntimeAuthoritativePositionRoute route)
    {
        if (!route.SendPositionImmediately)
            return;
        _localPlayerOutbound.SendImmediatePosition(_session(), controller);
    }

    private RuntimeAuthoritativePositionRoute ClassifyForcePosition(
        RuntimeEntityRecord record,
        RuntimeEntityKey key,
        in WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        in AcceptedPhysicsTimestamps timestamps,
        ushort previousTeleportSequence)
    {
        var authority = new RuntimeAuthoritativePositionAuthority(
            _generation(),
            key,
            record.PositionAuthorityVersion,
            update.PositionSequence,
            previousTeleportSequence,
            timestamps.Teleport,
            disposition);

        bool hasContact = update.IsGrounded;
        bool hasAnimations = (record.Snapshot.MotionTableId
                ?? record.Snapshot.Physics?.MotionTableId) is { } motionTableId
            && motionTableId != 0u;

        bool usePositionFromServer = _usePositionFromServer();
        float playerDistance = 0f;
        if (_localController() is { } controllerForDistance
            && (record.Snapshot.Physics?.Position
                ?? record.Snapshot.Position) is { } acceptedForDistance)
        {
            var target = new Vector3(
                acceptedForDistance.PositionX,
                acceptedForDistance.PositionY,
                acceptedForDistance.PositionZ);
            playerDistance = Vector3.Distance(
                target, controllerForDistance.Position);
        }

        var request = new RuntimeAcceptedPositionRouteRequest(
            authority,
            RuntimePositionEntityKind.LocalPlayer,
            RuntimeAcceptedPositionSource.PositionEvent,
            update.Position,
            update.PlacementId,
            update.Velocity,
            record.FullCellId,
            hasContact,
            playerDistance,
            usePositionFromServer,
            hasAnimations,
            new RuntimePositionPlacementFacts(
                record.FinalPhysicsState,
                record.Snapshot.SetupTableId is not null));

        return RuntimeAuthoritativePositionRouteClassifier
            .ClassifyAcceptedPosition(request);
    }
}
