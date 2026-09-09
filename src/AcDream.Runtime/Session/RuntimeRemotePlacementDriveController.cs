using System.Numerics;
using AcDream.Content;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Session;

public interface IRuntimeRemotePlacementServiceWindow
{
    bool IsWithinServiceWindow(uint landblockId);
}

internal enum RuntimeRemotePlacementExecutionStatus : byte
{
    NotApplicable,

    Refused,

    Contention,

    RejectedPreparation,

    Committed,

    Deferred,

    RejectedByPlacement,
}

internal static class RuntimeRemotePlacementExecutionStatusExtensions
{
    internal static bool StoresAcceptedDestination(
        this RuntimeRemotePlacementExecutionStatus status) =>
        status switch
        {
            RuntimeRemotePlacementExecutionStatus.NotApplicable => true,
            RuntimeRemotePlacementExecutionStatus.Refused => true,
            RuntimeRemotePlacementExecutionStatus.Contention => true,
            RuntimeRemotePlacementExecutionStatus.RejectedPreparation => true,
            RuntimeRemotePlacementExecutionStatus.Committed => false,
            RuntimeRemotePlacementExecutionStatus.Deferred => false,
            RuntimeRemotePlacementExecutionStatus.RejectedByPlacement => false,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
}

internal sealed class RuntimeRemotePlacementDriveController
{
    private sealed class Pending
    {
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeEntityPlacementToken Token { get; init; }
        internal required RuntimeAuthoritativePositionRoute Route { get; init; }
    }

    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly IGameRuntimeClock _clock;
    private readonly IPreparedCollisionSource _collisionSource;
    private readonly IRuntimeRemotePlacementServiceWindow _serviceWindow;

    private readonly Dictionary<RuntimeEntityKey, Pending> _pending = [];
    private readonly Dictionary<RuntimeEntityKey, RuntimeEntityPlacementToken>
        _awaitingAcknowledgement = [];
    private readonly List<RuntimeEntityKey> _driveScratch = [];
    private readonly List<RuntimeEntityKey> _awaitingAcknowledgementScratch = [];
    private readonly List<RuntimeEntityKey> _pendingScratch = [];
    private bool _driving;
    private object? _routeOwner;

    internal RuntimeRemotePlacementDriveController(
        RuntimeEntityObjectLifetime entityObjects,
        IGameRuntimeClock clock,
        IPreparedCollisionSource collisionSource,
        IRuntimeRemotePlacementServiceWindow serviceWindow)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _collisionSource = collisionSource
            ?? throw new ArgumentNullException(nameof(collisionSource));
        _serviceWindow = serviceWindow
            ?? throw new ArgumentNullException(nameof(serviceWindow));
        _entityObjects.RegisterRemotePlacementDriveOwnership(
            CountLivePending);
        _entityObjects.RegisterRemotePlacementDriveOwnership(
            CountLiveAwaitingAcknowledgement);
    }

    internal int PendingCount => _pending.Count;

    internal static bool OwnsPlacement(RuntimeAuthoritativePositionRoute route) =>
        route.OperationKind is RuntimeSetPositionOperationKind.RemoteAuthoritative
            or RuntimeSetPositionOperationKind.ProjectileAuthoritative
        && route.Disposition is RuntimeAuthoritativePositionDisposition.SetPosition
            or RuntimeAuthoritativePositionDisposition.SetPositionSimple
        && (route.SetPositionFlags & PhysicsSetPositionFlags.Teleport) != 0;

    internal void AttachRoute(object route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (_routeOwner is not null && !ReferenceEquals(_routeOwner, route))
        {
            throw new InvalidOperationException(
                "A remote placement drive controller serves one session "
                + "route at a time; the prior route must be disposed "
                + "(session reset precedes a new route) before a "
                + "replacement attaches.");
        }
        _routeOwner = route;
    }

    internal void DetachRoute(object route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!ReferenceEquals(_routeOwner, route))
            return;
        _routeOwner = null;

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        if (_pending.Count != 0)
        {
            Pending[] abandoned = [.. _pending.Values];
            _pending.Clear();
            foreach (Pending entry in abandoned)
            {
                setPosition.ForgetPlacementCompletion(entry.Token);
                CancelToken(setPosition, entry.Token);
            }
        }
        if (_awaitingAcknowledgement.Count != 0)
        {
            RuntimeEntityPlacementToken[] abandoned =
                [.. _awaitingAcknowledgement.Values];
            _awaitingAcknowledgement.Clear();
            foreach (RuntimeEntityPlacementToken token in abandoned)
            {
                setPosition.ForgetPlacementCompletion(token);
                CancelToken(setPosition, token);
            }
        }
    }

    internal RuntimeRemotePlacementExecutionStatus TryExecuteAcceptedRemotePosition(
        RuntimeEntityRecord record,
        in RuntimeAuthoritativePositionRoute route)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!OwnsPlacement(route)
            || record.PhysicsBody is null
            || record.Key is not { } key)
        {
            return RuntimeRemotePlacementExecutionStatus.NotApplicable;
        }

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;

        if (_pending.TryGetValue(key, out Pending? stale)
            && !setPosition.IsPlacementCurrent(stale.Token))
        {
            _pending.Remove(key);
        }

        CreateObject.ServerPosition? destination =
            record.Snapshot.Physics?.Position ?? record.Snapshot.Position;
        if (destination is not { } accepted
            || !CanAttemptDestination(setPosition, accepted.LandblockId))
        {
            return RuntimeRemotePlacementExecutionStatus.Refused;
        }

        RuntimeEntityPlacementToken token =
            setPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                route.OperationKind);
        if (!token.IsValid)
            return RuntimeRemotePlacementExecutionStatus.Contention;

        return SubmitAndResolve(record, token, route);
    }

    internal RuntimeRemotePlacementExecutionStatus ApplyAcceptedRemoteFarSnap(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        in RuntimeAuthoritativePositionRoute route)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        if (!RuntimeRemoteFarSnapPosition.OwnsFarSnap(route))
        {
            throw new ArgumentException(
                "Only a remote far-snap classification (SetPositionSimple, "
                + "RemoteAuthoritative, Teleport-flagged) may be applied "
                + "through the far-snap arm; the caller must select the arm "
                + "with RuntimeRemoteFarSnapPosition.ResolveArm.",
                nameof(route));
        }

        if (route.StopInterpolating)
            remote.Interp.Clear();
        RuntimeRemotePlacementExecutionStatus status =
            TryExecuteAcceptedRemotePosition(record, route);
        if (status.StoresAcceptedDestination())
            StoreAcceptedDestinationPose(record);
        return status;
    }

    internal RuntimeRemotePlacementExecutionStatus ApplyAcceptedRemoteTeleport(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        in RuntimeAuthoritativePositionRoute route)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        if (!RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route))
        {
            throw new ArgumentException(
                "Only a remote teleport/cell-less classification (SetPosition, "
                + "RemoteAuthoritative, Teleport-flagged) may be applied "
                + "through the teleport arm; the caller must select the arm "
                + "with RuntimeRemoteTeleportPosition.OwnsTeleportPlacement.",
                nameof(route));
        }

        RuntimeRemotePlacementExecutionStatus status =
            TryExecuteAcceptedRemotePosition(record, route);
        if (status.StoresAcceptedDestination())
            StoreAcceptedDestinationPose(record);
        return status;
    }

    internal RuntimeRemotePlacementExecutionStatus? ApplyAcceptedProjectilePosition(
        RuntimeEntityRecord record,
        in RuntimeAuthoritativePositionRoute route)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (route.OperationKind
                is not RuntimeSetPositionOperationKind.ProjectileAuthoritative
            || record.Projectile is not RuntimeProjectile projectile
            || record.PhysicsBody is not { } body
            || !ReferenceEquals(body, projectile.Body))
        {
            return null;
        }

        bool wasInWorld = body.InWorld;

        RuntimeRemotePlacementExecutionStatus status;
        switch (route.Disposition)
        {
            case RuntimeAuthoritativePositionDisposition.SetPosition:
                _entityObjects.Physics.CollisionReports.LeaveWorld(record);
                projectile.InvalidatePrediction();
                status = TryExecuteAcceptedRemotePosition(record, route);
                break;

            case RuntimeAuthoritativePositionDisposition.SetPositionSimple:
                if (route.StopInterpolating
                    && record.RemoteMotion is RemoteMotion adoptedFar)
                {
                    adoptedFar.Interp.Clear();
                }
                projectile.InvalidatePrediction();
                status = TryExecuteAcceptedRemotePosition(record, route);
                break;

            default:
                // Interpolate / NoPositionOperation: pinned no-op (D-P4).
                // RejectedAuthority / RejectedData: swallow (T5) — the
                // shared authority gate already rejected an invalid payload
                // upstream; there is nothing left to route.
                return null;
        }

        if (status.StoresAcceptedDestination())
            StoreAcceptedDestinationPose(record);
        if (status is not RuntimeRemotePlacementExecutionStatus.Deferred
            and not RuntimeRemotePlacementExecutionStatus.RejectedByPlacement)
        {
            SyncProjectilePresentation(record, projectile, body, wasInWorld);
        }
        return status;
    }

    private void SyncProjectilePresentation(
        RuntimeEntityRecord record,
        RuntimeProjectile projectile,
        PhysicsBody body,
        bool wasInWorld)
    {
        if (!_entityObjects.Entities.IsCurrent(record)
            || !ReferenceEquals(record.Projectile, projectile)
            || !ReferenceEquals(record.PhysicsBody, body))
        {
            return;
        }

        RuntimePhysicsState physics = _entityObjects.Physics;
        bool spatial = physics.IsSpatialProjectile(record, projectile);
        bool hidden =
            (record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0;
        uint localId = record.LocalEntityId ?? 0u;
        if (spatial && !hidden)
        {
            if (!wasInWorld)
            {
                body.LastUpdateTime = _clock.SimulationTimeSeconds;
                if ((body.State & PhysicsStateFlags.Static) == 0)
                    body.TransientState |= TransientStateFlags.Active;
            }
            body.InWorld = true;
            if (physics.TryGetWorldFrameOffset(
                    record.FullCellId,
                    out float offsetX,
                    out float offsetY))
            {
                physics.Engine.ShadowObjects.UpdatePosition(
                    localId,
                    body.Position,
                    body.Orientation,
                    offsetX,
                    offsetY,
                    record.FullCellId,
                    seedCellId: record.FullCellId);
            }
            else
            {
                physics.ThrowIfWorldFrameUnreachable(record.FullCellId);
            }
        }
        else if (spatial)
        {
            body.InWorld = true;
            body.LastUpdateTime = _clock.SimulationTimeSeconds;
            physics.Engine.ShadowObjects.Suspend(localId);
        }
        else
        {
            body.InWorld = false;
            body.TransientState &= ~TransientStateFlags.Active;
            physics.Engine.ShadowObjects.Suspend(localId);
        }
    }

    private bool StoreAcceptedDestinationPose(RuntimeEntityRecord record)
    {
        if (!_entityObjects.Entities.IsCurrent(record)
            || record.PhysicsBody is not { } body)
        {
            return false;
        }

        CreateObject.ServerPosition? destination =
            record.Snapshot.Physics?.Position ?? record.Snapshot.Position;
        if (destination is not { } accepted)
            return false;

        if (!_entityObjects.Physics.TryGetWorldFrameOffset(
                accepted.LandblockId,
                out float worldOffsetX,
                out float worldOffsetY))
        {
            _entityObjects.Physics.ThrowIfWorldFrameUnreachable(
                accepted.LandblockId);
            return false;
        }

        body.Position = new Vector3(
            accepted.PositionX + worldOffsetX,
            accepted.PositionY + worldOffsetY,
            accepted.PositionZ);
        body.Orientation = new Quaternion(
            accepted.RotationX,
            accepted.RotationY,
            accepted.RotationZ,
            accepted.RotationW);
        return true;
    }

    internal void Advance()
    {
        if (_driving || _pending.Count == 0)
            return;
        _driving = true;
        try
        {
            _driveScratch.Clear();
            foreach (RuntimeEntityKey key in _pending.Keys)
                _driveScratch.Add(key);

            RuntimeSetPositionState setPosition =
                _entityObjects.Physics.SetPosition;
            foreach (RuntimeEntityKey key in _driveScratch)
            {
                if (!_pending.TryGetValue(key, out Pending? pending))
                    continue;
                if (!setPosition.IsPlacementCurrent(pending.Token))
                {
                    _pending.Remove(key);
                    continue;
                }
                _pending.Remove(key);

                RuntimeProjectile? pendingProjectile = null;
                PhysicsBody? pendingBody = null;
                if (pending.Route.OperationKind
                        is RuntimeSetPositionOperationKind.ProjectileAuthoritative
                    && pending.Record.Projectile is RuntimeProjectile candidateProjectile
                    && pending.Record.PhysicsBody is { } candidateBody
                    && ReferenceEquals(candidateBody, candidateProjectile.Body))
                {
                    pendingProjectile = candidateProjectile;
                    pendingBody = candidateBody;
                }
                bool pendingWasInWorld = pendingBody?.InWorld ?? false;

                CreateObject.ServerPosition? destination =
                    pending.Record.Snapshot.Physics?.Position
                        ?? pending.Record.Snapshot.Position;
                RuntimeRemotePlacementExecutionStatus retryStatus;
                if (destination is not { } accepted
                    || !CanAttemptDestination(
                        setPosition,
                        accepted.LandblockId))
                {
                    CancelToken(setPosition, pending.Token);
                    pendingProjectile?.InvalidatePrediction();
                    StoreAcceptedDestinationPose(pending.Record);
                    retryStatus = RuntimeRemotePlacementExecutionStatus.Refused;
                }
                else
                {
                    retryStatus = SubmitAndResolve(
                        pending.Record, pending.Token, pending.Route);
                    if (retryStatus
                        is not RuntimeRemotePlacementExecutionStatus.Contention)
                    {
                        pendingProjectile?.InvalidatePrediction();
                    }
                }

                if (pendingProjectile is { } confirmedProjectile
                    && pendingBody is { } confirmedBody
                    && retryStatus is not RuntimeRemotePlacementExecutionStatus.Deferred
                        and not RuntimeRemotePlacementExecutionStatus.RejectedByPlacement)
                {
                    SyncProjectilePresentation(
                        pending.Record,
                        confirmedProjectile,
                        confirmedBody,
                        pendingWasInWorld);
                }
            }
        }
        finally
        {
            _driving = false;
        }
    }

    private RuntimeRemotePlacementExecutionStatus SubmitAndResolve(
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
                _pending[token.Entity] = new Pending
                {
                    Record = record,
                    Token = token,
                    Route = route,
                };
                return RuntimeRemotePlacementExecutionStatus.Contention;
            }

            CancelToken(setPosition, token);
            return RuntimeRemotePlacementExecutionStatus.RejectedPreparation;
        }

        switch (outcome.Status)
        {
            case RuntimeSetPositionStatus.CommittedHostAcknowledgementPending:
                if (setPosition.IsPlacementCurrent(token))
                    _awaitingAcknowledgement[token.Entity] = token;
                return RuntimeRemotePlacementExecutionStatus.Committed;

            case RuntimeSetPositionStatus.DeferredCell:
                CancelToken(setPosition, token);
                return RuntimeRemotePlacementExecutionStatus.Deferred;

            default:
                CancelToken(setPosition, token);
                return RuntimeRemotePlacementExecutionStatus.RejectedByPlacement;
        }
    }

    private int CountLivePending()
    {
        if (_pending.Count == 0)
            return 0;
        if (_entityObjects.Physics.IsDisposed)
            return _pending.Count;

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        _pendingScratch.Clear();
        foreach ((RuntimeEntityKey key, Pending entry) in _pending)
        {
            if (!setPosition.IsPlacementCurrent(entry.Token))
                _pendingScratch.Add(key);
        }
        foreach (RuntimeEntityKey key in _pendingScratch)
            _pending.Remove(key);
        return _pending.Count;
    }

    private int CountLiveAwaitingAcknowledgement()
    {
        if (_awaitingAcknowledgement.Count == 0)
            return 0;
        if (_entityObjects.Physics.IsDisposed)
            return _awaitingAcknowledgement.Count;

        RuntimeSetPositionState setPosition = _entityObjects.Physics.SetPosition;
        _awaitingAcknowledgementScratch.Clear();
        foreach ((RuntimeEntityKey key, RuntimeEntityPlacementToken token)
                 in _awaitingAcknowledgement)
        {
            if (!setPosition.IsPlacementCurrent(token))
                _awaitingAcknowledgementScratch.Add(key);
        }
        foreach (RuntimeEntityKey key in _awaitingAcknowledgementScratch)
            _awaitingAcknowledgement.Remove(key);
        return _awaitingAcknowledgement.Count;
    }

    private bool CanAttemptDestination(
        RuntimeSetPositionState setPosition,
        uint landblockId) =>
        _serviceWindow.IsWithinServiceWindow(landblockId)
        && !setPosition.IsCollisionPrefixQuiescing(landblockId);

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
}
