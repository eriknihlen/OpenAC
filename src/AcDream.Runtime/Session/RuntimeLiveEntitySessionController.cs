using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Session;

public interface IRuntimeDirectWorldProjection
{
    void ProjectSpawn(
        RuntimeEntityRecord record,
        bool isLocalPlayer);

    void ProjectPosition(
        RuntimeEntityRecord record,
        bool isLocalPlayer,
        PositionTimestampDisposition disposition);

    void CenterOnAcceptedForcePosition(RuntimeEntityRecord record);

    void BeginTeleport();

    RuntimeDestinationReadiness PrepareDestination(
        long revealGeneration,
        RuntimeTeleportDestination destination,
        RuntimeWorldHostProjectionToken portal);
}

public sealed class RuntimeLiveEntitySessionController
{
    private readonly GameRuntime _runtime;
    private readonly WorldSession _session;
    private readonly Action<string> _log;
    private readonly IRuntimeDirectWorldProjection? _worldProjection;
    private readonly RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private readonly Action? _onLoginCompleteSent;
    private bool _initialLoginCompleteSent;
    private readonly Func<uint, bool> _isChildGuidKnown;
    private readonly Func<uint, ushort?> _resolveParentInstance;
    private readonly Func<ParentEvent.Parsed, bool> _acceptParentEvent;
    private RuntimeRemoteArming? _remoteArming;

    public RuntimeLiveEntitySessionController(
        GameRuntime runtime,
        WorldSession session,
        Action<string>? log = null,
        IRuntimeDirectWorldProjection? worldProjection = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null,
        Action? onLoginCompleteSent = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _log = log ?? (_ => { });
        _worldProjection = worldProjection;
        _acceptedPositionDrive = acceptedPositionDrive;
        _onLoginCompleteSent = onLoginCompleteSent;
        _isChildGuidKnown = guid => Entities.Entities.TryGetSnapshot(guid, out _);
        _resolveParentInstance = guid =>
            Entities.Entities.TryGetSnapshot(guid, out WorldSession.EntitySpawn spawn)
                ? spawn.InstanceSequence
                : null;
        _acceptParentEvent = candidate => Entities.TryApplyParent(
            candidate,
            acknowledgeProjection: null,
            out _);
    }

    /// <summary>
    /// Hands this session the one owner that arms another creature's body
    /// from the server's word about it. Without one, an accepted movement or
    /// position for another creature still updates what this client knows and
    /// still moves the body between cells; it just arms nothing.
    /// </summary>
    internal void BindRemoteArming(RuntimeRemoteArming arming)
    {
        ArgumentNullException.ThrowIfNull(arming);
        if (_remoteArming is not null)
        {
            throw new InvalidOperationException(
                "This session already has a remote arming bound.");
        }
        _remoteArming = arming;
    }

    public LiveEntitySessionSink CreateSink() => new(
        OnSpawned,
        OnDeleted,
        OnPickedUp,
        OnMotionUpdated,
        OnPositionUpdated,
        OnVectorUpdated,
        OnStateUpdated,
        OnParentUpdated,
        OnTeleportStarted,
        OnAppearanceUpdated,
        // Effect and sound playback are presentation: the no-window host parses
        // these packets and discards them, exactly as it does F754/F755.
        _ => { },
        _ => { },
        _ => { });

    private RuntimeEntityObjectLifetime Entities =>
        _runtime.EntityObjects;

    private void OnSpawned(WorldSession.EntitySpawn spawn)
    {
        RuntimeEntityRegistrationResult registration = _worldProjection is null
            ? Entities.RegisterEntity(spawn)
            : Entities.RegisterEntityWithInitialResidence(
                spawn,
                isLocalPlayer: spawn.Guid
                    == _runtime.PlayerIdentity.ServerGuid);
        if (registration.Canonical is not { } canonical)
            return;

        ulong integrationVersion = canonical.CreateIntegrationVersion;
        bool applied = Entities.ApplyAcceptedSpawn(
            canonical,
            integrationVersion,
            canonical.Snapshot,
            replaceGeneration:
                registration.Inbound.Disposition
                    is CreateObjectTimestampDisposition.NewGeneration);
        if (applied)
        {
            _worldProjection?.ProjectSpawn(
                canonical,
                canonical.ServerGuid
                    == _runtime.PlayerIdentity.ServerGuid);
            RetryChildrenWaitingForParent(canonical.ServerGuid);
            if (_worldProjection is null
                && canonical.ServerGuid
                    == _runtime.PlayerIdentity.ServerGuid
                && !_initialLoginCompleteSent)
            {
                _initialLoginCompleteSent = true;
                _session.SendLoginComplete();
                _session.SendHouseQuery();
                _onLoginCompleteSent?.Invoke();
            }
        }
    }

    private void OnDeleted(DeleteObject.Parsed delete)
    {
        if (delete.Guid == _runtime.PlayerIdentity.ServerGuid)
            return;

        // The same path an expired out-of-visibility object takes on a
        // host without presentation.
        RuntimeCanonicalEntityExpirySink.DeleteCanonicalOnly(Entities, delete);
    }

    private void OnPickedUp(PickupEvent.Parsed pickup) =>
        _ = Entities.TryApplyPickup(
            pickup,
            acknowledgeProjection: null,
            out _);

    private void OnMotionUpdated(
        WorldSession.EntityMotionUpdate update)
    {
        bool isLocal =
            update.Guid == _runtime.PlayerIdentity.ServerGuid;
        bool known = Entities.TryApplyMotion(
            update,
            retainPayload: !isLocal || !update.IsAutonomous,
            acknowledgeProjection: null,
            out _,
            out _);
        if (isLocal
            && update.MotionState.MovementType == 0
            && update.PackedMotionFlags == 0u)
        {
            _runtime.ActionOwner.CombatMode.RecordQualifiedSelfMotion(
                _runtime.Clock.SimulationTimeSeconds);
        }
        if (known && isLocal)
            ApplyServerMotionToLocalBody(update);
        else if (known)
            ArmRemoteInboundMotion(update);
    }

    /// <summary>
    /// A movement the server sends about this character's own body. The
    /// original unpacks it into the body unless it is the echo of a movement
    /// this character made itself, and then hands the body to the server. A
    /// walk or turn the server orders is one such movement, and it is the
    /// only answer a use out of arm's reach ever gets. So is the stance the
    /// server puts the body in after a combat-mode change: the body takes
    /// that stance up over the length of its animation, and until it has, it
    /// is not in position for the next mode change. A host that skipped this
    /// asked for the next mode while the server still had the last one
    /// playing, and the cast that followed arrived in the wrong mode.
    /// A host with a window has always done both.
    /// </summary>
    private void ApplyServerMotionToLocalBody(
        in WorldSession.EntityMotionUpdate update)
    {
        if (RuntimeServerControlledLocalMovement.TryApply(_runtime, update))
            return;
        if (update.IsAutonomous
            || update.MotionState.MovementType != 0
            || _remoteArming is not { } arming
            || _runtime.MovementOwner.Controller is not { } controller)
        {
            return;
        }

        controller.SetLastMoveWasAutonomous(update.IsAutonomous);
        uint commandClass =
            controller.Motion.InterpretedState.ForwardCommand & 0xFF000000u;
        if (commandClass == 0u)
            commandClass = 0x41000000u;
        _ = arming.InboundMotion.Apply(
            update,
            controller.Movement,
            controller.Motion.DefaultSink,
            _runtime.EntityObjects.Physics.ResolveObjectTableHost(update.Guid),
            controller.CellId,
            commandClass);
    }

    private void OnPositionUpdated(
        WorldSession.EntityPositionUpdate update)
    {
        if (!RuntimeAuthoritativePositionRouteClassifier
                .IsValidCreateWirePosition(update.Position)
            || update.Velocity is { } wireVelocity
                && !(float.IsFinite(wireVelocity.X)
                    && float.IsFinite(wireVelocity.Y)
                    && float.IsFinite(wireVelocity.Z)))
        {
            return;
        }

        bool isLocal =
            update.Guid == _runtime.PlayerIdentity.ServerGuid;
        PlayerMovementController? localController =
            isLocal ? _runtime.MovementOwner.Controller : null;
        bool known = Entities.TryApplyPosition(
            update,
            isLocal,
            forcePositionRotation: localController?.BodyOrientation,
            currentLocalVelocity: localController?.BodyVelocity,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps);
        if (!known
            || disposition is PositionTimestampDisposition.Rejected)
        {
            return;
        }

        if (!isLocal)
        {
            ArmRemoteAcceptedPosition(update, disposition, timestamps);
            TryCommitAcceptedWireCell(update);
            return;
        }

        if (disposition is PositionTimestampDisposition.Apply)
        {
            var position = update.Position;
            var destination = new RuntimeTeleportDestination(
                update.Guid,
                update.InstanceSequence,
                update.PositionSequence,
                update.TeleportSequence,
                update.ForcePositionSequence,
                new Position(
                    position.LandblockId,
                    new Vector3(
                        position.PositionX,
                        position.PositionY,
                        position.PositionZ),
                    new Quaternion(
                        position.RotationX,
                        position.RotationY,
                        position.RotationZ,
                        position.RotationW)));
            _runtime.TransitOwner.OfferTeleportDestination(
                destination,
                timestamps.TeleportAdvanced);
        }
        if (Entities.Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord record))
        {
            if (disposition is PositionTimestampDisposition.ForcePosition)
            {
                _worldProjection?.CenterOnAcceptedForcePosition(record);

                RuntimeAcceptedPositionExecutionStatus forceStatus =
                    _acceptedPositionDrive?.TryExecuteAcceptedLocalPosition(
                        record,
                        update,
                        disposition,
                        timestamps,
                        timestamps.PreviousTeleport)
                    ?? RuntimeAcceptedPositionExecutionStatus.NotApplicable;
                if (forceStatus is RuntimeAcceptedPositionExecutionStatus
                    .NotApplicable)
                {
                    TryCommitAcceptedWireCell(update);
                    _worldProjection?.ProjectPosition(
                        record,
                        isLocalPlayer: true,
                        disposition);
                }
            }
            else
            {
                TryCommitAcceptedWireCell(update);
                _worldProjection?.ProjectPosition(
                    record,
                    isLocalPlayer: true,
                    disposition);
            }
        }
        TryCompletePortal();
    }

    private void TryCommitAcceptedWireCell(
        WorldSession.EntityPositionUpdate update)
    {
        if (!Entities.Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical)
            || Entities.TryGetInitialCreateResidence(canonical, out _)
            || IsMissilePacket(canonical, update.Guid))
        {
            return;
        }

        _ = Entities.CommitWireCellRebucket(
            canonical,
            update.Position.LandblockId);
    }

    private bool IsMissilePacket(
        RuntimeEntityRecord canonical,
        uint guid) =>
        guid != _runtime.PlayerIdentity.ServerGuid
        && (canonical.FinalPhysicsState & PhysicsStateFlags.Missile) != 0
        && canonical.Projectile is { } projectile
        && ReferenceEquals(canonical.PhysicsBody, projectile.Body);

    private void OnVectorUpdated(VectorUpdate.Parsed update) =>
        _ = Entities.TryApplyVector(
            update,
            acknowledgeProjection: null,
            out _);

    private void OnStateUpdated(SetState.Parsed update) =>
        _ = Entities.TryApplyState(
            update,
            acknowledgeProjection: null,
            out _,
            out _);

    private void OnParentUpdated(ParentEvent.Parsed update)
    {
        Entities.Entities.ParentAttachments.Enqueue(update);
        ResolveAndCommitChildAttachment(update.ChildGuid);
    }

    private bool ResolveAndCommitChildAttachment(uint childGuid)
    {
        ParentAttachmentState relations = Entities.Entities.ParentAttachments;
        relations.Resolve(
            childGuid,
            _isChildGuidKnown,
            _resolveParentInstance,
            _acceptParentEvent);
        if (!relations.TryGetStagedProjection(
                childGuid,
                out ParentAttachmentRelation staged))
        {
            return false;
        }
        if (!Entities.Entities.TryGetActive(
                childGuid,
                out RuntimeEntityRecord canonical))
        {
            return false;
        }

        if (!relations.CanCommitIncarnation(staged, _resolveParentInstance))
        {
            relations.RejectProjection(staged);
            return false;
        }

        ulong positionAuthorityVersion = canonical.PositionAuthorityVersion;
        if (!Entities.TryCommitParent(staged, acknowledgeProjection: null, out _)
            || !relations.CommitProjection(staged, _resolveParentInstance))
        {
            return false;
        }
        bool committed = Entities.CommitAcceptedParentCellless(
            canonical,
            positionAuthorityVersion,
            acknowledgeProjection: null);
        if (committed && PhysicsDiagnostics.ProbeChildCellEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[child-cell] parent=0x{staged.ParentGuid:X8} child=0x{canonical.ServerGuid:X8} new=0x{canonical.FullCellId:X8} cause=headless-attach"));
        }
        return committed;
    }

    private void RetryChildrenWaitingForParent(uint parentGuid)
    {
        IReadOnlyList<uint> waiting = Entities.Entities.ParentAttachments
            .ChildrenUnresolvedForParent(parentGuid);
        for (int i = 0; i < waiting.Count; i++)
            ResolveAndCommitChildAttachment(waiting[i]);
    }

    private void OnTeleportStarted(uint rawSequence)
    {
        ushort sequence = unchecked((ushort)rawSequence);
        RuntimeWorldTransitState transit = _runtime.TransitOwner;
        if (!transit.TryQueueTeleportStart(sequence))
            return;
        _worldProjection?.BeginTeleport();
        if (!transit.ActivateQueuedTeleport())
        {
            throw new InvalidOperationException(
                "Runtime rejected its queued headless teleport activation.");
        }
        TryCompletePortal();
    }

    private void OnAppearanceUpdated(ObjDescEvent.Parsed update) =>
        _ = Entities.TryApplyObjDesc(
            update,
            acknowledgeProjection: null,
            out _);

    private (long Generation,
        RuntimeTeleportDestination Destination,
        RuntimeWorldHostProjectionToken Projection)? _pendingPortalCompletion;

    private int _pendingPortalCompletionRetryCount;

    private const int PendingPortalCompletionLogInterval = 100;

    private void TryCompletePortal()
    {
        RuntimeWorldTransitState transit = _runtime.TransitOwner;
        if (!transit.TryGetAcceptedTeleportDestination(
                out RuntimeTeleportDestination destination)
            || !transit.CanBeginPortalReveal(
                destination.TeleportSequence,
                destination.CellId))
        {
            return;
        }

        // A newer teleport replaces a reveal that is still waiting for its
        // destination. This client holds nothing for that reveal beyond its
        // acknowledgements, so it retires it completely before the new one
        // begins, as the windowed client withdraws its reveal host.
        if (_pendingPortalCompletion is { } replaced)
        {
            ClearPendingPortalCompletion();
            if (!transit.BeginHostProjectionSupersession(replaced.Projection))
            {
                throw new InvalidOperationException(
                    "Runtime rejected the headless portal supersession.");
            }
            AcknowledgeRetirement(transit, replaced.Projection);
        }

        if (!transit.TryBeginPortalReveal(
                destination.TeleportSequence,
                destination.CellId,
                out long generation))
        {
            throw new InvalidOperationException(
                "Runtime refused a headless portal reveal it had just allowed.");
        }

        if (!transit.TryRegisterHostProjection(
                generation,
                destination.CellId,
                out RuntimeWorldHostProjectionToken projection))
        {
            throw new InvalidOperationException(
                "Runtime rejected the headless portal projection.");
        }

        Acknowledge(
            transit,
            projection,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered);

        _pendingPortalCompletion = (generation, destination, projection);
        _pendingPortalCompletionRetryCount = 0;
        TryAdvancePortalCompletion();
    }

    private void TryAdvancePortalCompletion()
    {
        if (_pendingPortalCompletion is not { } pending)
            return;
        (long generation, RuntimeTeleportDestination destination,
                RuntimeWorldHostProjectionToken projection) = pending;

        RuntimeWorldTransitState transit = _runtime.TransitOwner;
        bool indoor = (destination.CellId & 0xFFFFu) >= 0x0100u;
        RuntimeDestinationReadiness readiness =
            _worldProjection?.PrepareDestination(
                generation,
                destination,
                projection)
            ?? new RuntimeDestinationReadiness(
                generation,
                destination.CellId,
                indoor,
                IsUnhydratable: false,
                RequiredRenderRadius: indoor ? 0 : 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true);
        if (!readiness.IsCollisionReady)
        {
            _pendingPortalCompletionRetryCount++;
            if (_pendingPortalCompletionRetryCount % PendingPortalCompletionLogInterval == 0)
            {
                _log(
                    $"headless: portal completion still parked after "
                    + $"{_pendingPortalCompletionRetryCount} retries "
                    + $"generation={generation} cell=0x{destination.CellId:X8}");
            }
            return;
        }

        ClearPendingPortalCompletion();

        if (!transit.AcknowledgeDestinationReadiness(
                readiness))
        {
            throw new InvalidOperationException(
                "Runtime rejected headless destination readiness.");
        }

        if (!transit.AcknowledgePortalMaterialized(
                generation,
                destination.TeleportSequence,
                destination.CellId))
        {
            throw new InvalidOperationException(
                "Runtime rejected headless portal materialization.");
        }
        Acknowledge(
            transit,
            projection,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected);

        if (!transit.RequireDestinationReservationRelease(projection))
        {
            throw new InvalidOperationException(
                "Runtime rejected headless destination release.");
        }
        Acknowledge(
            transit,
            projection,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased);

        if (!transit.AcknowledgeWorldViewportVisible(generation)
            || !transit.Complete(generation))
        {
            throw new InvalidOperationException(
                "Runtime rejected headless portal completion.");
        }
        Acknowledge(
            transit,
            projection,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected);

        _session.SendLoginComplete();
        transit.EndTeleport();
        _log(
            $"headless: portal complete generation={generation} "
            + $"cell=0x{destination.CellId:X8}");
        _onLoginCompleteSent?.Invoke();
    }

    public void PumpPortalCompletion() => TryAdvancePortalCompletion();

    /// <summary>
    /// Ends a portal reveal that is still waiting for its destination when
    /// the session ends, before the world transit resets. The windowed
    /// client cancels its reveal and releases its host at the same edge.
    /// </summary>
    public void EndPortalRevealForSessionReset()
    {
        if (_pendingPortalCompletion is not { } pending)
            return;

        ClearPendingPortalCompletion();
        RuntimeWorldTransitState transit = _runtime.TransitOwner;
        if (!transit.Cancel(pending.Generation))
        {
            throw new InvalidOperationException(
                "Runtime refused to cancel the headless portal reveal.");
        }
        AcknowledgeRetirement(transit, pending.Projection);
    }

    private void ClearPendingPortalCompletion()
    {
        _pendingPortalCompletion = null;
        _pendingPortalCompletionRetryCount = 0;
    }

    /// <summary>
    /// Gives a retiring reveal every acknowledgement the transit still waits
    /// for, in the order the windowed client drains its reveal host. This
    /// client holds no simulation pause or destination reservation of its
    /// own, so each release is already true.
    /// </summary>
    private static void AcknowledgeRetirement(
        RuntimeWorldTransitState transit,
        RuntimeWorldHostProjectionToken projection)
    {
        ReadOnlySpan<RuntimeWorldHostAcknowledgementStage> order =
        [
            RuntimeWorldHostAcknowledgementStage.SimulationReleaseProjected,
            RuntimeWorldHostAcknowledgementStage.DestinationReservationReleased,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected,
        ];
        foreach (RuntimeWorldHostAcknowledgementStage stage in order)
        {
            if (!transit.TryGetHostProjection(
                    projection,
                    out RuntimeWorldHostProjectionSnapshot host))
            {
                throw new InvalidOperationException(
                    "The retiring headless portal projection is gone before "
                    + "its terminal acknowledgement.");
            }
            if ((host.PendingAcknowledgements & stage) != 0)
                Acknowledge(transit, projection, stage);
        }
        if (transit.TryGetHostProjection(projection, out _))
        {
            throw new InvalidOperationException(
                "The retiring headless portal projection is still owned "
                + "after its terminal acknowledgement.");
        }
    }

    private static void Acknowledge(
        RuntimeWorldTransitState transit,
        RuntimeWorldHostProjectionToken projection,
        RuntimeWorldHostAcknowledgementStage stage)
    {
        if (!transit.AcknowledgeHostProjection(
                new RuntimeWorldHostAcknowledgement(
                    projection,
                    stage)))
        {
            throw new InvalidOperationException(
                $"Runtime rejected headless host acknowledgement {stage}.");
        }
    }

    /// <summary>
    /// Arms another creature's body from an accepted movement: gives the body
    /// what it needs to move itself, then hands the movement to its
    /// interpreter. This is the same arming a client with a window does, from
    /// the same owner; what differs is only that nothing here is drawing the
    /// body.
    /// </summary>
    private void ArmRemoteInboundMotion(
        WorldSession.EntityMotionUpdate update)
    {
        if (_remoteArming is not { } arming
            || !Entities.Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord record)
            || (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0
            || record.PhysicsBody is null)
        {
            return;
        }

        ulong movementAuthority = record.MovementAuthorityVersion;
        ulong velocityAuthority = record.VelocityAuthorityVersion;
        bool IsCurrent() =>
            Entities.Entities.IsCurrent(record)
            && record.MovementAuthorityVersion == movementAuthority
            && record.VelocityAuthorityVersion == velocityAuthority;
        if (!IsCurrent())
            return;

        RemoteMotion remote = Entities.Physics.GetOrCreateRemoteMotion(record);
        if (!IsCurrent())
            return;

        AnimationSequencer? sequencer =
            Entities.Physics.EntityRemoteAnimation(update.Guid)?.Sequencer;
        IInterpretedMotionSink? sink = arming.EnsureRemoteMotionBindings(
            remote,
            sequencer,
            update.Guid);
        if (!IsCurrent())
            return;

        uint commandClass = sequencer?.CurrentMotion & 0xFF000000u
            ?? remote.Motion.InterpretedState.ForwardCommand & 0xFF000000u;
        if (commandClass == 0u)
            commandClass = 0x41000000u;

        _ = arming.InboundMotion.Apply(
            update,
            remote.Movement,
            sink,
            remote.Host,
            remote.CellId,
            commandClass,
            IsCurrent);
    }

    /// <summary>
    /// Does to another creature's body what an accepted position says to do:
    /// the same one of four things the client with a window does, decided by
    /// the same owner. A body with nothing armed under it yet is left to the
    /// cell bookkeeping alone.
    /// </summary>
    private void ArmRemoteAcceptedPosition(
        WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        in AcceptedPhysicsTimestamps timestamps)
    {
        if (_remoteArming is not { } arming
            || !Entities.Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord record)
            || (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0
            || record.PhysicsBody is null
            || IsMissilePacket(record, update.Guid))
        {
            return;
        }

        Vector3 worldPosition = Entities.Physics.WireOriginToWorldFrame(
            update.Position.LandblockId,
            update.Position.PositionX,
            update.Position.PositionY,
            update.Position.PositionZ);
        var wireRotation = new Quaternion(
            update.Position.RotationX,
            update.Position.RotationY,
            update.Position.RotationZ,
            update.Position.RotationW);

        // The server's first word about where another creature is is also
        // when that creature gets a body to move: a creature that arrives
        // after this client entered the world is never armed by anything
        // else, and one with no body cannot catch up to anything.
        RemoteMotion remote = Entities.Physics.GetOrCreateRemoteMotion(record);
        if (!Entities.Entities.IsCurrent(record)
            || !ReferenceEquals(record.RemoteMotion, remote))
        {
            return;
        }
        if (!remote.Body.InContact)
        {
            remote.Body.Position = worldPosition;
            remote.Body.Orientation = wireRotation;
            arming.SeatBodyIfUnseated(
                record,
                remote,
                worldPosition,
                update.Position.LandblockId);
        }

        ulong positionAuthority = record.PositionAuthorityVersion;
        bool IsCurrent() =>
            Entities.Entities.IsCurrent(record)
            && record.PositionAuthorityVersion == positionAuthority
            && ReferenceEquals(record.RemoteMotion, remote);
        if (!IsCurrent())
            return;

        RuntimeAuthoritativePositionRoute? route =
            Entities.ClassifyRemoteAcceptedPosition(
                record,
                update,
                disposition,
                timestamps,
                PlayerDistanceTo(update.Position));
        if (!IsCurrent())
            return;

        double nowSeconds = Entities.Physics.UtcNowSeconds;
        if (RuntimeRemoteSteadyStatePosition.IsAirborneNoOperation(route))
        {
            RuntimeRemoteServerPosition.StampAirborneLeftover(
                remote, update.Position.LandblockId, worldPosition, nowSeconds);
            return;
        }

        bool isTeleport =
            RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route);
        if (!update.IsGrounded && !isTeleport)
        {
            RuntimeRemoteServerPosition.StampAirborneLeftover(
                remote, update.Position.LandblockId, worldPosition, nowSeconds);
            return;
        }

        RuntimeRemoteServerPosition.DeriveVelocity(
            remote,
            update.Velocity,
            worldPosition,
            nowSeconds,
            isTeleport);

        RuntimeRemoteContactRouting routing =
            arming.ApplyRemoteContactRouting(
                record,
                remote,
                route,
                worldPosition,
                wireRotation,
                // The same shared test the client with a window makes: a body
                // that is going to be carried forward catches up to the
                // server's word on its own, and one that is not takes it
                // outright.
                willBeAdvanced: RuntimeRemoteBodyDisposition.WillAdvance(
                    Entities.Physics,
                    record,
                    remote),
                runTeleportHook: IsCurrent);
        if (!IsCurrent())
            return;

        RuntimeRemoteSteadyStatePosition.TryArmConstraintAfterOperation(
            ToConstraintArm(routing.Arm),
            remote);
        RuntimeRemoteArming.TryAdoptWireCellAfterRouting(
            remote,
            routing,
            update.Position.LandblockId);

        RuntimeRemoteServerPosition.Stamp(remote, worldPosition, nowSeconds);
    }

    /// <summary>
    /// How far the accepted position is from this character, which is one of
    /// the things that decides whether a body is re-placed or asked to catch
    /// up. Null when this character has no body of its own yet.
    /// </summary>
    private float? PlayerDistanceTo(CreateObject.ServerPosition position)
    {
        if (_runtime.MovementOwner.Controller is not { } controller)
            return null;
        return Vector3.Distance(
            Entities.Physics.WireOriginToWorldFrame(
                position.LandblockId,
                position.PositionX,
                position.PositionY,
                position.PositionZ),
            controller.Position);
    }

    private static RuntimeRemoteAcceptedPositionArm ToConstraintArm(
        RuntimeRemoteContactArm arm) => arm switch
    {
        RuntimeRemoteContactArm.TeleportPlacement =>
            RuntimeRemoteAcceptedPositionArm.TeleportPlacement,
        RuntimeRemoteContactArm.FarSnapPlacement =>
            RuntimeRemoteAcceptedPositionArm.FarSnapPlacement,
        RuntimeRemoteContactArm.SteadyStateInterpolate =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        RuntimeRemoteContactArm.AirborneSnap =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        RuntimeRemoteContactArm.UnroutedCatchUp =>
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
        _ => throw new ArgumentOutOfRangeException(
            nameof(arm), arm, "Unhandled remote contact arm."),
    };
}
