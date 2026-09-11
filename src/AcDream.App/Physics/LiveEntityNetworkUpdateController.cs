using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Core.Selection;
using AcDream.Core.World;
using DatReaderWriter;

namespace AcDream.App.Physics;

internal sealed class LiveEntityNetworkUpdateController
    : ILiveEntityNetworkUpdateSink,
      ILiveEntitySameGenerationUpdateSink,
      ILocalPlayerLandblockSource
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ClientObjectTable _objects;
    private readonly LiveEntityHydrationController _liveEntityHydration;
    private readonly EntityEffectController _entityEffects;
    private readonly LiveEntityPresentationController _liveEntityPresentation;
    private readonly LiveEntityLightController _liveEntityLights;
    private readonly EquippedChildRenderController _equippedChildRenderer;
    private readonly ProjectileController _projectileController;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animatedEntities;
    private readonly RemoteMovementObservationTracker _remoteMovementObservations;
    private readonly RemotePhysicsUpdater _remotePhysicsUpdater;
    private readonly RemoteInboundMotionDispatcher _remoteInboundMotion;
    private readonly LiveEntityMotionRuntimeController _motionRuntime;
    private readonly PhysicsEngine _physicsEngine;
    private readonly IDatReaderWriter _dats;
    private readonly IAnimationLoader _animLoader;
    private readonly RuntimeCombatTargetState? _combatTargetController;
    private readonly LiveWorldOriginState _origin;
    private readonly AcDream.App.Streaming.ILocalPlayerTeleportNetworkSink
        _localPlayerTeleport;
    private readonly IRuntimeLocalPlayerControllerSource _playerControllerSource;
    private readonly LocalPlayerOutboundController _localPlayerOutbound;
    private readonly ILocalPlayerPhysicsHostSource _playerHostSource;
    private readonly ILocalPlayerIdentitySource _playerIdentity;
    private readonly IPhysicsScriptTimeSource _gameTime;
    private readonly ILiveWorldSessionSource _session;
    private readonly LiveEntityInboundAuthorityGate _authorityGate;
    private readonly IMovementTruthDiagnosticSink _movementTruthDiagnostics;
    private readonly InventoryWorldDropProjectionController?
        _worldDropProjection;
    private readonly RuntimeAcceptedPositionDriveController _acceptedPositionDrive;
    private readonly RuntimeRemotePlacementDriveController _remotePlacementDrive;

    private RuntimeEntityRecord? _remoteArmCanonical;
    private RemoteMotion? _remoteArmMotion;
    private LiveEntityRecord? _remoteArmPositionRecord;
    private ulong _remoteArmPositionAuthorityVersion;
    private AcDream.Core.World.WorldEntity? _remoteArmExpectedEntity;

    private LiveEntityRecord? _projectileArmPositionRecord;
    private ulong _projectileArmPositionAuthorityVersion;

    private sealed class RemoteArmCallbacks
    {
        internal readonly Func<bool> IsCurrentPositionOwner;
        internal readonly Func<bool> RunTeleportHook;

        internal readonly Func<bool> IsCurrentProjectilePositionOwner;

        internal RemoteArmCallbacks(LiveEntityNetworkUpdateController owner)
        {
            IsCurrentPositionOwner = owner.IsCurrentRemoteArmPositionOwner;
            RunTeleportHook = owner.RunCachedRemoteTeleportHook;
            IsCurrentProjectilePositionOwner =
                owner.IsCurrentProjectileArmPositionOwner;
        }
    }

    private readonly RemoteArmCallbacks _remoteArmCallbacks;

    private PlayerMovementController? _playerController => _playerControllerSource.Controller;
    private EntityPhysicsHost? _playerHost => _playerHostSource.Host;
    private uint _playerServerGuid => _playerIdentity.ServerGuid;
    private double _physicsScriptGameTime => _gameTime.CurrentScriptTime;
    internal uint? LastLivePlayerLandblockId =>
        _authorityGate.LastLivePlayerLandblockId;

    uint? ILocalPlayerLandblockSource.LastKnownLandblockId =>
        LastLivePlayerLandblockId;

    public LiveEntityNetworkUpdateController(
        LiveEntityRuntime liveEntities,
        ClientObjectTable objects,
        LiveEntityHydrationController liveEntityHydration,
        EntityEffectController entityEffects,
        LiveEntityPresentationController liveEntityPresentation,
        LiveEntityLightController liveEntityLights,
        EquippedChildRenderController equippedChildRenderer,
        ProjectileController projectileController,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animatedEntities,
        RemoteMovementObservationTracker remoteMovementObservations,
        RemotePhysicsUpdater remotePhysicsUpdater,
        RemoteInboundMotionDispatcher remoteInboundMotion,
        LiveEntityMotionRuntimeController motionRuntime,
        PhysicsEngine physicsEngine,
        IDatReaderWriter dats,
        IAnimationLoader animLoader,
        RuntimeCombatTargetState? combatTargetController,
        LiveWorldOriginState origin,
        AcDream.App.Streaming.ILocalPlayerTeleportNetworkSink localPlayerTeleport,
        IRuntimeLocalPlayerControllerSource playerControllerSource,
        LocalPlayerOutboundController localPlayerOutbound,
        ILocalPlayerPhysicsHostSource playerHostSource,
        ILocalPlayerIdentitySource playerIdentity,
        IPhysicsScriptTimeSource gameTime,
        ILiveWorldSessionSource session,
        Action<uint, AcceptedPhysicsTimestamps> publishTimestamps,
        IMovementTruthDiagnosticSink movementTruthDiagnostics,
        RuntimeAcceptedPositionDriveController acceptedPositionDrive,
        RuntimeRemotePlacementDriveController remotePlacementDrive,
        InventoryWorldDropProjectionController? worldDropProjection = null)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _liveEntityHydration = liveEntityHydration ?? throw new ArgumentNullException(nameof(liveEntityHydration));
        _entityEffects = entityEffects ?? throw new ArgumentNullException(nameof(entityEffects));
        _liveEntityPresentation = liveEntityPresentation ?? throw new ArgumentNullException(nameof(liveEntityPresentation));
        _liveEntityLights = liveEntityLights ?? throw new ArgumentNullException(nameof(liveEntityLights));
        _equippedChildRenderer = equippedChildRenderer ?? throw new ArgumentNullException(nameof(equippedChildRenderer));
        _projectileController = projectileController ?? throw new ArgumentNullException(nameof(projectileController));
        _animatedEntities = animatedEntities ?? throw new ArgumentNullException(nameof(animatedEntities));
        _remoteMovementObservations = remoteMovementObservations ?? throw new ArgumentNullException(nameof(remoteMovementObservations));
        _remotePhysicsUpdater = remotePhysicsUpdater ?? throw new ArgumentNullException(nameof(remotePhysicsUpdater));
        _remoteInboundMotion = remoteInboundMotion ?? throw new ArgumentNullException(nameof(remoteInboundMotion));
        _motionRuntime = motionRuntime ?? throw new ArgumentNullException(nameof(motionRuntime));
        _physicsEngine = physicsEngine ?? throw new ArgumentNullException(nameof(physicsEngine));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _animLoader = animLoader ?? throw new ArgumentNullException(nameof(animLoader));
        _combatTargetController = combatTargetController;
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _localPlayerTeleport = localPlayerTeleport
            ?? throw new ArgumentNullException(nameof(localPlayerTeleport));
        _playerControllerSource = playerControllerSource ?? throw new ArgumentNullException(nameof(playerControllerSource));
        _localPlayerOutbound = localPlayerOutbound
            ?? throw new ArgumentNullException(nameof(localPlayerOutbound));
        _playerHostSource = playerHostSource ?? throw new ArgumentNullException(nameof(playerHostSource));
        _playerIdentity = playerIdentity ?? throw new ArgumentNullException(nameof(playerIdentity));
        _gameTime = gameTime ?? throw new ArgumentNullException(nameof(gameTime));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _authorityGate = new LiveEntityInboundAuthorityGate(
            liveEntities,
            publishTimestamps);
        _movementTruthDiagnostics = movementTruthDiagnostics
            ?? throw new ArgumentNullException(nameof(movementTruthDiagnostics));
        _acceptedPositionDrive = acceptedPositionDrive
            ?? throw new ArgumentNullException(nameof(acceptedPositionDrive));
        _remotePlacementDrive = remotePlacementDrive
            ?? throw new ArgumentNullException(nameof(remotePlacementDrive));
        _worldDropProjection = worldDropProjection;
        _remoteArmCallbacks = new RemoteArmCallbacks(this);
    }

    internal void ResetSessionState() => _authorityGate.ResetSessionState();

    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;

    private static bool IsDoorName(string? name) => name == "Door";

    private void SeedRemoteSpawnPlacement(
        RemoteMotion remote,
        uint serverGuid,
        AcDream.Core.World.WorldEntity entity,
        System.Numerics.Vector3 worldPos,
        uint cellId)
    {
        var (radius, height) = _motionRuntime.GetSetupCylinder(serverGuid, entity);
        if (radius < 0.05f)
        {
            radius = 0.48f;
            height = 1.835f;
        }

        var moverFlags = IsPlayerGuid(serverGuid)
            ? AcDream.Core.Physics.ObjectInfoState.IsPlayer
              | AcDream.Core.Physics.ObjectInfoState.EdgeSlide
            : AcDream.Core.Physics.ObjectInfoState.EdgeSlide;

        if (!AcDream.Core.Physics.SpawnPlacementSettler.TrySettle(
            _physicsEngine,
            remote.Body,
            worldPos,
            cellId,
            radius,
            height,
            moverFlags,
            entity.Id,
            remote.Movement.HitGround,
            remote.Motion.LeaveGround))
        {
            return;
        }
        remote.Airborne = !remote.Body.OnWalkable;
    }

    private bool RunRemoteTeleportHook(
        RuntimeEntityRecord canonical,
        RemoteMotion remote,
        Func<bool> isCurrent)
    {
        EntityPhysicsHost? host = remote.Host;
        return RemoteTeleportHook.Execute(
            new RemoteTeleportHookActions(
                CancelMoveTo: error => remote.Movement.CancelMoveTo(error),
                UnStick: () => host?.PositionManager.UnStick(),
                StopInterpolating: () => remote.Interp.Clear(),
                UnConstrain: () => host?.PositionManager.UnConstrain(),
                NotifyTeleported: () => host?.NotifyTeleported(),
                ReportCollisionEnd: () =>
                    _liveEntities.ForceEndCollisionReporting(canonical)),
            isCurrent);
    }
    public void ApplySameGeneration(
        SameGenerationCreateObjectEvents refresh) =>
        LiveEntitySameGenerationUpdateRouter.Apply(refresh, this);

    void ILiveEntitySameGenerationUpdateSink.OnDescription(
        uint ownerGuid,
        PhysicsSpawnData description)
    {
        if (_liveEntities.TryGetEffectProfile(
                ownerGuid,
                out var effectProfile)
            && effectProfile is EntityEffectProfile liveProfile)
        {
            liveProfile.ApplyNetworkDescription(description);
            _entityEffects.OnLiveEntityDescriptionChanged(ownerGuid);
        }
    }

    void ILiveEntitySameGenerationUpdateSink.OnAppearance(
        AcDream.Core.Net.Messages.ObjDescEvent.Parsed appearance) =>
        _liveEntityHydration.OnAppearance(appearance);

    void ILiveEntitySameGenerationUpdateSink.OnParent(CreateParentUpdate parent) =>
        _liveEntityHydration.OnCreateParentAccepted(parent);

    void ILiveEntitySameGenerationUpdateSink.OnPosition(
        WorldSession.EntityPositionUpdate position) => OnPosition(position);

    void ILiveEntitySameGenerationUpdateSink.OnPickup(
        AcDream.Core.Net.Messages.PickupEvent.Parsed pickup) =>
        _liveEntityHydration.OnPickup(pickup);

    void ILiveEntitySameGenerationUpdateSink.OnMovement(
        WorldSession.EntityMotionUpdate movement) => OnMotion(movement);

    void ILiveEntitySameGenerationUpdateSink.OnState(
        AcDream.Core.Net.Messages.SetState.Parsed state) => OnState(state);

    void ILiveEntitySameGenerationUpdateSink.OnVector(
        AcDream.Core.Net.Messages.VectorUpdate.Parsed vector) => OnVector(vector);


    public void OnMotion(AcDream.Core.Net.WorldSession.EntityMotionUpdate update)
    {
        bool retainPayload = update.Guid != _playerServerGuid || !update.IsAutonomous;
        if (!_authorityGate.TryAcceptMotion(
                update,
                retainPayload,
                out AcceptedMotionNetworkUpdate accepted,
                out bool timestampAccepted))
        {
            if (!timestampAccepted
                && (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1"
                || Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1")
                )
            {
                Console.WriteLine(
                    $"[UM_STALE] guid={update.Guid:X8} inst={update.InstanceSequence} "
                    + $"mov={update.MovementSequence} sc={update.ServerControlSequence} dropped");
            }
            return;
        }

        LiveEntityRecord acceptedMotionRecord = accepted.Record;
        ulong acceptedMovementAuthorityVersion =
            accepted.MovementAuthorityVersion;
        ulong acceptedMovementVelocityAuthorityVersion =
            accepted.VelocityAuthorityVersion;

        if (!_liveEntities.TryGetWorldEntity(update.Guid, out var entity)) return;
        if (!_animatedEntities.TryGetValue(entity.Id, out var ae))
        {
            DispatchRemoteInboundMotion(
                update,
                entity,
                ae: null,
                acceptedMotionRecord,
                acceptedMovementAuthorityVersion,
                acceptedMovementVelocityAuthorityVersion);
            return;
        }
        if (_dats is null) return;

        ushort stance = update.MotionState.Stance;
        ushort? command = update.MotionState.ForwardCommand;

        if (System.Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1"
            && update.Guid != _playerServerGuid)
        {
            string cmdStrRaw = command.HasValue ? $"0x{command.Value:X4}" : "null";
            string sideStr = update.MotionState.SideStepCommand is { } s ? $"0x{s:X4}" : "null";
            string turnStr = update.MotionState.TurnCommand is { } t ? $"0x{t:X4}" : "null";
            string fwdSpdStr = update.MotionState.ForwardSpeed is { } fs ? $"{fs:F2}" : "null";
            uint seqMot = ae.Sequencer?.CurrentMotion ?? 0;
            System.Console.WriteLine(
                $"[UM_RAW] guid={update.Guid:X8} stance=0x{stance:X4} fwd={cmdStrRaw} fwdSpd={fwdSpdStr} "
                + $"side={sideStr} turn={turnStr} mt=0x{update.MotionState.MovementType:X2} "
                + $"isMoveTo={update.MotionState.IsServerControlledMoveTo} "
                + $"seq.CurrentMotion=0x{seqMot:X8}");
        }

        if (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1"
            && update.Guid != _playerServerGuid)
        {
            string cmdStr = command.HasValue ? $"0x{command.Value:X4}" : "null";
            float spd = update.MotionState.ForwardSpeed
                ?? ((update.MotionState.MoveToSpeed ?? 0f)
                    * (update.MotionState.MoveToRunRate ?? 0f));
            uint seqStyle = ae.Sequencer?.CurrentStyle ?? 0;
            uint seqMotion = ae.Sequencer?.CurrentMotion ?? 0;
            Console.WriteLine(
                $"UM guid=0x{update.Guid:X8} mt=0x{update.MotionState.MovementType:X2} stance=0x{stance:X4} cmd={cmdStr} spd={spd:F2} " +
                $"| seq now style=0x{seqStyle:X8} motion=0x{seqMotion:X8}");
        }

        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeBuildingEnabled
            && IsDoorName(_objects.Get(update.Guid)?.Name))
        {
            Console.WriteLine(System.FormattableString.Invariant(
                $"[door-cycle] guid=0x{update.Guid:X8} stance=0x{stance:X4} cmd=0x{(command ?? 0u):X4}"));
        }

        if (ae.Sequencer is not null)
        {
            uint fullStyle = stance != 0
                ? (0x80000000u | (uint)stance)
                : ae.Sequencer.CurrentStyle;

            float speedMod = update.MotionState.ForwardSpeed ?? 1f;
            uint fullMotion;
            if (!command.HasValue || command.Value == 0)
            {
                fullMotion = 0x41000003u;
            }
            else
            {
                uint resolved = AcDream.Core.Physics.MotionCommandResolver
                    .ReconstructFullCommand(command.Value);
                fullMotion = resolved != 0
                    ? resolved
                    : (ae.Sequencer.CurrentMotion & 0xFF000000u) | (uint)command.Value;
                if (fullMotion == (uint)command.Value)
                    fullMotion = 0x40000000u | (uint)command.Value;
            }

            if (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1"
                && update.Guid != _playerServerGuid)
                Console.WriteLine(
                    $"UM    ↳ SetCycle(style=0x{fullStyle:X8}, motion=0x{fullMotion:X8}, speed={speedMod:F2})");

            // No-op if same; the sequencer's fast path guards against that.
            uint priorMotion = ae.Sequencer.CurrentMotion;

            if (update.Guid == _playerServerGuid)
            {
                if (_playerController is not null)
                {
                    _playerController.SetLastMoveWasAutonomous(update.IsAutonomous);
                    bool IsCurrentLocalMotion() =>
                        _liveEntities.IsCurrentMovementAuthority(
                            acceptedMotionRecord,
                            acceptedMovementAuthorityVersion)
                        && _liveEntities.IsCurrentVelocityAuthority(
                            acceptedMotionRecord,
                            acceptedMovementVelocityAuthorityVersion)
                        && ReferenceEquals(
                            acceptedMotionRecord.WorldEntity,
                            entity);
                    if (!IsCurrentLocalMotion())
                        return;

                    AcDream.App.Physics.RemoteInboundMotionDispatchResult localDispatch =
                        _remoteInboundMotion.Apply(
                            update,
                            _playerController.Movement,
                            _playerController.Motion.DefaultSink,
                            _playerHost,
                            _playerController.CellId,
                            ae.Sequencer.CurrentMotion & 0xFF000000u,
                            IsCurrentLocalMotion);
                    if (localDispatch.Superseded
                        || !IsCurrentLocalMotion())
                    {
                        return;
                    }
                    if (localDispatch.RoutedMoveTo)
                    {
                        return;
                    }
                    if (!localDispatch.AppliedInterpretedState)
                        return;
                    fullMotion = localDispatch.CurrentForwardCommand;
                }
            }
            else
            {
                AcDream.App.Physics.RemoteInboundMotionDispatchResult dispatch =
                    DispatchRemoteInboundMotion(
                        update,
                        entity,
                        ae,
                        acceptedMotionRecord,
                        acceptedMovementAuthorityVersion,
                        acceptedMovementVelocityAuthorityVersion);
                if (dispatch.Superseded
                    || dispatch.RoutedMoveTo
                    || !dispatch.AppliedInterpretedState)
                    return;
                fullMotion = dispatch.CurrentForwardCommand;
            }

            _combatTargetController?.OnMotionApplied(
                update.Guid, ae.Sequencer.CurrentMotion);
            if (!_liveEntities.IsCurrentMovementAuthority(
                    acceptedMotionRecord,
                    acceptedMovementAuthorityVersion)
                || !_liveEntities.IsCurrentVelocityAuthority(
                    acceptedMotionRecord,
                    acceptedMovementVelocityAuthorityVersion))
            {
                return;
            }

            uint newLo = fullMotion & 0xFFu;
            bool enteringLocomotion = newLo == 0x05 || newLo == 0x06
                                   || newLo == 0x07
                                   || newLo == 0x0F || newLo == 0x10;
            uint oldLo = priorMotion & 0xFFu;
            bool wasLocomotion = oldLo == 0x05 || oldLo == 0x06
                              || oldLo == 0x07
                              || oldLo == 0x0F || oldLo == 0x10;
            if (enteringLocomotion && !wasLocomotion && update.Guid != _playerServerGuid)
            {
                // Reset both stop signals so stop-detection starts a fresh
                // window from this transition. Without this, the entity
                // starts its run animation and is instantly interrupted.
                var refreshedTime = System.DateTime.UtcNow;
                if (acceptedMotionRecord.ProjectionKey is { } motionKey
                    && _remoteMovementObservations.TryGetValue(
                        motionKey,
                        out var prev))
                {
                    _remoteMovementObservations[motionKey] =
                        (prev.Pos, refreshedTime);
                }
                if (_liveEntities.TryGetRemoteMotionRuntime(
                        update.Guid,
                        out IRuntimeRemoteMotion? remoteRuntime)
                    && remoteRuntime is RemoteMotion dr)
                    dr.LastServerPosTime = (refreshedTime - System.DateTime.UnixEpoch).TotalSeconds;
            }

            return;
        }

        var newCycle = AcDream.Core.Meshing.MotionResolver.GetIdleCycle(
            ae.Setup, _dats, _animLoader!,
            motionTableIdOverride: null,
            stanceOverride: stance,
            commandOverride: command);
        bool newCycleIsGood = newCycle is not null
            && newCycle.Framerate != 0f
            && newCycle.HighFrame >= newCycle.LowFrame
            && newCycle.Animation.PartFrames.Count >= 1;
        if (!newCycleIsGood) return;

        ae.Animation = newCycle!.Animation;
        ae.LowFrame = Math.Max(0, newCycle.LowFrame);
        ae.HighFrame = Math.Min(newCycle.HighFrame, newCycle.Animation.PartFrames.Count - 1);
        ae.Framerate = newCycle.Framerate;
        ae.CurrFrame = ae.LowFrame;
    }

    private AcDream.App.Physics.RemoteInboundMotionDispatchResult
        DispatchRemoteInboundMotion(
        AcDream.Core.Net.WorldSession.EntityMotionUpdate update,
        AcDream.Core.World.WorldEntity entity,
        LiveEntityAnimationState? ae,
        LiveEntityRecord acceptedRecord,
        ulong acceptedMovementAuthorityVersion,
        ulong acceptedVelocityAuthorityVersion)
    {
        if (update.Guid == _playerServerGuid)
            return default;

        bool IsCurrentOwner(RemoteMotion? expectedRemote = null) =>
            _liveEntities is { } live
            && live.IsCurrentMovementAuthority(
                acceptedRecord,
                acceptedMovementAuthorityVersion)
            && live.IsCurrentVelocityAuthority(
                acceptedRecord,
                acceptedVelocityAuthorityVersion)
            && ReferenceEquals(acceptedRecord.WorldEntity, entity)
            && (ae is null
                ? acceptedRecord.AnimationRuntime is null
                : ReferenceEquals(acceptedRecord.AnimationRuntime, ae))
            && (expectedRemote is null
                || ReferenceEquals(
                    acceptedRecord.RemoteMotionRuntime,
                    expectedRemote));
        if (!IsCurrentOwner())
            return default;

        if (!_liveEntities.TryGetRemoteMotionRuntime(
                update.Guid,
                out IRuntimeRemoteMotion? remoteRuntime)
            || remoteRuntime is not RemoteMotion remote)
        {
            remote = _liveEntities.GetOrCreateRemoteMotionRuntime(
                update.Guid);
            remote.Body.Orientation = entity.Rotation;
            remote.Body.Position = entity.Position;
        }
        if (!remote.Body.InContact)
        {
            SeedRemoteSpawnPlacement(
                remote,
                update.Guid,
                entity,
                remote.Body.Position,
                entity.VisibilityCellId ?? 0u);
        }
        if (!IsCurrentOwner(remote))
            return default;

        var sink = _motionRuntime.EnsureRemoteMotionBindings(remote, ae, update.Guid);
        uint commandClass = ae?.Sequencer?.CurrentMotion & 0xFF000000u
            ?? remote.Motion.InterpretedState.ForwardCommand & 0xFF000000u;
        if (commandClass == 0u)
            commandClass = 0x41000000u;

        AcDream.App.Physics.RemoteInboundMotionDispatchResult result =
            _remoteInboundMotion.Apply(
                update,
                remote.Movement,
                sink,
                remote.Host,
                remote.CellId,
                commandClass,
                () => IsCurrentOwner(remote));

        if (result.Superseded || !IsCurrentOwner(remote))
            return result with { Superseded = true };

        if (result.ForwardCommandChanged)
        {
            if (System.Environment.GetEnvironmentVariable(
                    "ACDREAM_REMOTE_VEL_DIAG") == "1")
            {
                System.Console.WriteLine(
                    $"[FWD_WIRE] guid={update.Guid:X8} "
                    + $"oldCmd=0x{result.PreviousForwardCommand:X8} "
                    + $"newCmd=0x{result.CurrentForwardCommand:X8} "
                    + $"newLow=0x{result.CurrentForwardCommand & 0xFFu:X2} "
                    + $"speed={update.MotionState.ForwardSpeed ?? 1f:F3}");
            }
            remote.PrevServerPosTime = 0.0;
        }

        if (result.AppliedInterpretedState && ae is null)
        {
            _combatTargetController?.OnMotionApplied(
                update.Guid,
                result.CurrentForwardCommand);
            if (!IsCurrentOwner(remote))
                return result with { Superseded = true };
        }
        return result;
    }

    private bool WillAdvanceRemoteMotion(uint serverGuid, RemoteMotion remote)
    {
        return _liveEntities is { } runtime
            && runtime.TryGetRecord(serverGuid, out LiveEntityRecord record)
            && ReferenceEquals(record.RemoteMotionRuntime, remote)
            && (record.FinalPhysicsState
                & AcDream.Core.Physics.PhysicsStateFlags.Static) == 0
            && runtime.GetRootObjectClockDisposition(serverGuid)
                is AcDream.Core.Physics.RetailObjectClockDisposition.Advance
            && runtime.IsCurrentSpatialRemoteMotion(record, remote);
    }

    private RuntimeAuthoritativePositionRoute? ClassifyRemoteAcceptedPosition(
        AcDream.Core.Net.WorldSession.EntityPositionUpdate update,
        RuntimeEntityRecord canonical,
        AcDream.Core.Physics.PositionTimestampDisposition timestampDisposition,
        AcceptedPhysicsTimestamps timestamps,
        System.Numerics.Vector3 worldPos) =>
        _liveEntities.ClassifyRemoteAcceptedPosition(
            canonical,
            update,
            timestampDisposition,
            timestamps,
            _playerController is { } controller
                ? System.Numerics.Vector3.Distance(worldPos, controller.Position)
                : null);

    internal static bool TryApplyGenericRemoteRenderPose(
        AcDream.Core.World.WorldEntity entity,
        RuntimeAuthoritativePositionRoute? route,
        System.Numerics.Vector3 worldPos,
        uint landblockId,
        System.Numerics.Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (RuntimeRemoteSteadyStatePosition.OwnsSteadyState(route))
            return false;

        entity.SetPosition(worldPos);
        entity.ParentCellId = landblockId;
        entity.Rotation = rotation;
        return true;
    }

    internal enum RemoteContactArm : byte
    {
        AirborneSnap,

        /// <summary>Route 4a's near InterpolateTo branch.</summary>
        SteadyStateInterpolate,

        FarSnapPlacement,

        TeleportPlacement,

        UnroutedCatchUp,
    }

    internal readonly record struct RemoteContactRouting(
        RemoteContactArm Arm,
        RuntimeRemotePlacementExecutionStatus? Placement);

    internal static RemoteContactRouting ApplyRemoteContactRouting(
        RuntimeRemotePlacementDriveController placementDrive,
        RuntimeEntityRecord canonical,
        RemoteMotion remote,
        RuntimeAuthoritativePositionRoute? route,
        System.Numerics.Vector3 worldPos,
        System.Numerics.Quaternion rotation,
        bool willBeDrTicked,
        Func<bool> runTeleportHook)
    {
        ArgumentNullException.ThrowIfNull(placementDrive);
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(runTeleportHook);

        if (RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route))
        {
            bool hookRan = runTeleportHook();
            RuntimeRemotePlacementExecutionStatus teleportStatus =
                placementDrive.ApplyAcceptedRemoteTeleport(
                    canonical,
                    remote,
                    route!.Value);
            if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeRemoteTeleportEnabled)
            {
                AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteTeleport(
                    canonical.ServerGuid,
                    cause: route.Value.Authority.TeleportAdvanced
                        ? "teleport-ts"
                        : "cellless",
                    hookRan,
                    teleportStatus.ToString());
            }
            return new RemoteContactRouting(
                RemoteContactArm.TeleportPlacement,
                teleportStatus);
        }

        AcDream.Core.Physics.PhysicsDiagnostics.BeginRemoteSlideAttribution(
            canonical.ServerGuid);
        if (!remote.Body.InContact)
        {
            remote.Body.Position = worldPos;
            remote.Body.Orientation = rotation;
            return new RemoteContactRouting(
                RemoteContactArm.AirborneSnap, Placement: null);
        }

        switch (RuntimeRemoteFarSnapPosition.ResolveArm(route))
        {
            case RuntimeRemoteAcceptedPositionArm.FarSnapPlacement:
                return new RemoteContactRouting(
                    RemoteContactArm.FarSnapPlacement,
                    placementDrive.ApplyAcceptedRemoteFarSnap(
                        canonical,
                        remote,
                        route!.Value));

            case RuntimeRemoteAcceptedPositionArm.NearInterpolate:
                RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                    remote,
                    worldPos,
                    rotation,
                    isMovingTo: remote.Movement.IsMovingTo(),
                    willBeDrTicked);
                return new RemoteContactRouting(
                    RemoteContactArm.SteadyStateInterpolate, Placement: null);

            case RuntimeRemoteAcceptedPositionArm.AirborneNoOperation:
                throw new InvalidOperationException(
                    "A NoPositionOperation (airborne no-op) classification "
                    + "must be handled by the caller's own early return "
                    + "before routing; MoveOrTeleport writes nothing "
                    + "at all on that branch.");

            default:
                RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                    remote,
                    worldPos,
                    rotation,
                    isMovingTo: remote.Movement.IsMovingTo(),
                    willBeDrTicked);
                return new RemoteContactRouting(
                    RemoteContactArm.UnroutedCatchUp, Placement: null);
        }
    }

    private static void ApplyWireAirborneLeftoverBookkeeping(
        RemoteMotion remote,
        uint wireCellId,
        System.Numerics.Vector3 worldPos,
        double nowSec)
    {
        ArgumentNullException.ThrowIfNull(remote);
        remote.CellId = wireCellId;
        remote.LastServerPos = worldPos;
        remote.LastServerPosTime = nowSec;
    }

    private RemoteContactRouting? RunRemoteArmTail(
        RuntimeEntityRecord canonical,
        LiveEntityRecord positionRecord,
        RemoteMotion remote,
        RuntimeAuthoritativePositionRoute? route,
        uint guid,
        System.Numerics.Vector3 worldPos,
        System.Numerics.Quaternion rotation,
        ulong positionAuthorityVersion,
        AcDream.Core.World.WorldEntity? expectedEntity)
    {
        _remoteArmCanonical = canonical;
        _remoteArmMotion = remote;
        _remoteArmPositionRecord = positionRecord;
        _remoteArmPositionAuthorityVersion = positionAuthorityVersion;
        _remoteArmExpectedEntity = expectedEntity;

        RemoteContactRouting routing = ApplyRemoteContactRouting(
            _remotePlacementDrive,
            canonical,
            remote,
            route,
            worldPos,
            rotation,
            willBeDrTicked: WillAdvanceRemoteMotion(guid, remote),
            runTeleportHook: _remoteArmCallbacks.RunTeleportHook);

        if ((routing.Arm is RemoteContactArm.FarSnapPlacement
                or RemoteContactArm.TeleportPlacement)
            && (!_remoteArmCallbacks.IsCurrentPositionOwner()
                || !ReferenceEquals(positionRecord.RemoteMotionRuntime, remote)))
        {
            return null;
        }

        return routing;
    }

    private bool IsCurrentRemoteArmPositionOwner() =>
        _remoteArmPositionRecord is { } record
        && _liveEntities.IsCurrentPositionAuthority(
            record, _remoteArmPositionAuthorityVersion)
        && (_remoteArmExpectedEntity is null
            || ReferenceEquals(record.WorldEntity, _remoteArmExpectedEntity));

    private bool IsCurrentProjectileArmPositionOwner() =>
        _projectileArmPositionRecord is { } record
        && _liveEntities.IsCurrentPositionAuthority(
            record, _projectileArmPositionAuthorityVersion);

    private bool RunCachedRemoteTeleportHook() =>
        _remoteArmCanonical is { } canonical
        && _remoteArmMotion is { } motion
        && RunRemoteTeleportHook(
            canonical, motion, _remoteArmCallbacks.IsCurrentPositionOwner);

    internal static bool TryAdoptWireCellAfterRouting(
        RemoteMotion remote,
        RemoteContactArm arm,
        uint wireCellId)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (arm is RemoteContactArm.FarSnapPlacement
            or RemoteContactArm.TeleportPlacement)
            return false;
        remote.CellId = wireCellId;
        return true;
    }

    private static RuntimeRemoteAcceptedPositionArm ToConstraintArm(
        RemoteContactArm arm) => arm switch
    {
        RemoteContactArm.TeleportPlacement =>
            RuntimeRemoteAcceptedPositionArm.TeleportPlacement,
        RemoteContactArm.FarSnapPlacement =>
            RuntimeRemoteAcceptedPositionArm.FarSnapPlacement,
        RemoteContactArm.SteadyStateInterpolate =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        RemoteContactArm.AirborneSnap =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        RemoteContactArm.UnroutedCatchUp =>
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
        _ => throw new ArgumentOutOfRangeException(
            nameof(arm), arm, "Unhandled RemoteContactArm in ToConstraintArm."),
    };

    public void OnVector(AcDream.Core.Net.Messages.VectorUpdate.Parsed update)
    {
        bool payloadIsValid = _projectileController?.CanAcceptVectorPayload(
                update.Guid,
                update.Velocity,
                update.Omega) != false;
        if (!_authorityGate.TryAcceptVector(
                update,
                payloadIsValid,
                out AcceptedVectorNetworkUpdate accepted))
        {
            return;
        }
        LiveEntityRecord acceptedVectorRecord = accepted.Record;
        ulong acceptedVectorAuthorityVersion =
            accepted.VectorAuthorityVersion;
        ulong acceptedVectorVelocityAuthorityVersion =
            accepted.VelocityAuthorityVersion;

        LiveEntityVectorRouter.Route(
            () => _projectileController?.ApplyAuthoritativeVector(
                    acceptedVectorRecord,
                    acceptedVectorAuthorityVersion,
                    acceptedVectorVelocityAuthorityVersion,
                    update.Velocity,
                    update.Omega,
                    _physicsScriptGameTime) == true,
            () =>
            {
                if (update.Guid == _playerServerGuid
                    || acceptedVectorRecord.RemoteMotionRuntime is not null
                    || acceptedVectorRecord.PhysicsBody is not { } canonicalBody)
                {
                    return false;
                }
                _liveEntities.TryCommitAuthoritativeVector(
                    acceptedVectorRecord,
                    canonicalBody,
                    update.Velocity,
                    update.Omega,
                    _physicsScriptGameTime);
                return true;
            },
            () => ApplyOrdinaryVector(
                update,
                acceptedVectorRecord,
                acceptedVectorAuthorityVersion,
                acceptedVectorVelocityAuthorityVersion));
    }

    private void ApplyOrdinaryVector(
        AcDream.Core.Net.Messages.VectorUpdate.Parsed update,
        LiveEntityRecord acceptedVectorRecord,
        ulong acceptedVectorAuthorityVersion,
        ulong acceptedVectorVelocityAuthorityVersion)
    {
        if (!_liveEntities.ContainsWorldEntity(update.Guid)) return;

        if (update.Guid == _playerServerGuid) return;          // local jump uses our own physics
        if (!_liveEntities.TryGetRemoteMotionRuntime(
                update.Guid,
                out IRuntimeRemoteMotion? remoteRuntime)
            || remoteRuntime is not RemoteMotion rm)
        {
            return;
        }
        LiveEntityRecord remoteRecord = acceptedVectorRecord;

        if (!_liveEntities.TryCommitAuthoritativeVector(
                remoteRecord,
                rm.Body,
                update.Velocity,
                update.Omega,
                _physicsScriptGameTime))
        {
            return;
        }

        if (AcDream.Core.Physics.PhysicsDiagnostics.ShouldLogRemoteSlide(
                update.Guid))
        {
            AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideVector(
                guid: update.Guid,
                wireVelocity: update.Velocity,
                wireOmega: update.Omega,
                willMarkAirborne: update.Velocity.Z > 0.5f,
                airborneBefore: rm.Airborne,
                contact: rm.Body.InContact,
                onWalkable: rm.Body.OnWalkable,
                gravity: rm.Body.HasGravity,
                bodyVelocity: rm.Body.Velocity,
                contactPlaneValid: rm.Body.ContactPlaneValid,
                contactPlaneNormalZ: rm.Body.ContactPlane.Normal.Z);
        }

        if (update.Velocity.Z > 0.5f)
        {
            rm.Airborne = true;
            rm.Body.TransientState &= ~(AcDream.Core.Physics.TransientStateFlags.Contact
                                      | AcDream.Core.Physics.TransientStateFlags.OnWalkable);

            if (_liveEntities.TryGetWorldEntity(update.Guid, out var ent)
                && _animatedEntities.TryGetValue(ent.Id, out var ae)
                && ae.Sequencer is not null)
            {
                _motionRuntime.EnsureRemoteMotionBindings(rm, ae, update.Guid);
                rm.Motion.LeaveGround();
                if (!_liveEntities.IsCurrentVectorAuthority(
                        remoteRecord,
                        acceptedVectorAuthorityVersion)
                    || !_liveEntities.IsCurrentVelocityAuthority(
                        remoteRecord,
                        acceptedVectorVelocityAuthorityVersion)
                    || !_liveEntities.TryCommitAuthoritativeVector(
                        remoteRecord,
                        rm.Body,
                        update.Velocity,
                        update.Omega,
                        _physicsScriptGameTime))
                {
                    return;
                }
            }
        }

        if (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1")
        {
            Console.WriteLine(
                $"VU    guid=0x{update.Guid:X8} vel=({update.Velocity.X:F2},{update.Velocity.Y:F2},{update.Velocity.Z:F2}) airborne={rm.Airborne}");
        }
    }

    public void OnState(AcDream.Core.Net.Messages.SetState.Parsed parsed)
    {
        if (!_authorityGate.TryAcceptState(
                parsed,
                out AcceptedStateNetworkUpdate accepted))
            return;
        LiveEntityRecord record = accepted.Record;
        ulong acceptedStateAuthorityVersion = accepted.StateAuthorityVersion;

        _liveEntityLights?.OnStateChanged(parsed.Guid);
        _liveEntityPresentation?.OnStateAccepted(parsed.Guid);

        if (!_liveEntities.IsCurrentStateAuthority(
                record,
                acceptedStateAuthorityVersion))
        {
            return;
        }

        _projectileController?.ApplyAuthoritativeState(
            record,
            acceptedStateAuthorityVersion,
            record.FinalPhysicsState,
            _physicsScriptGameTime,
            _origin.CenterX,
            _origin.CenterY);
        if (!_liveEntities.IsCurrentStateAuthority(
                record,
                acceptedStateAuthorityVersion))
        {
            return;
        }
        if (parsed.Guid == _playerServerGuid)
        {
            _ = _playerController?.ApplyServerPhysicsState(
                record.FinalPhysicsState);
        }

        if (!_liveEntities.TryGetWorldEntity(parsed.Guid, out var entity)) return;

        uint registryKey = entity.Id;

        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeBuildingEnabled)
            Console.WriteLine(System.FormattableString.Invariant(
                $"[setstate] guid=0x{parsed.Guid:X8} entityId=0x{registryKey:X8} raw=0x{parsed.PhysicsState:X8} final=0x{(uint)record.FinalPhysicsState:X8} instSeq={parsed.InstanceSequence} stateSeq={parsed.StateSequence}"));
    }

    public void OnPosition(AcDream.Core.Net.WorldSession.EntityPositionUpdate update)
    {
        if (_worldDropProjection?.TryRecoverUnknownPosition(update) == true)
        {
            return;
        }

        bool payloadIsValid = _projectileController?.CanAcceptPositionPayload(
                update.Guid,
                update.Position,
                update.Velocity) != false;
        if (!_authorityGate.TryAcceptPosition(
            update,
            _playerServerGuid,
            update.Guid == _playerServerGuid && _playerController is not null
                ? _playerController.BodyOrientation
                : null,
            update.Guid == _playerServerGuid && _playerController is not null
                ? _playerController.BodyVelocity
                : null,
            payloadIsValid,
            out AcceptedPositionNetworkUpdate accepted))
        {
            return;
        }
        var timestampDisposition = accepted.TimestampDisposition;
        var acceptedSpawn = accepted.Spawn;
        var timestamps = accepted.Timestamps;
        RuntimeEntityRecord acceptedPositionCanonical = accepted.Canonical;
        ulong acceptedPositionAuthorityVersion =
            accepted.PositionAuthorityVersion;
        if (!_liveEntities.TryGetProjection(
                acceptedPositionCanonical,
                out LiveEntityRecord acceptedPositionRecord)
            && !_liveEntityHydration.RecoverCanonicalProjection(
                acceptedPositionCanonical,
                acceptedPositionAuthorityVersion,
                out acceptedPositionRecord))
        {
            return;
        }

        bool IsCurrentPositionOwner(
            AcDream.Core.World.WorldEntity? expectedEntity = null) =>
            _liveEntities.IsCurrentPositionAuthority(
                acceptedPositionRecord,
                acceptedPositionAuthorityVersion)
            && (expectedEntity is null
                || ReferenceEquals(
                    acceptedPositionRecord.WorldEntity,
                    expectedEntity));
        if (!IsCurrentPositionOwner())
            return;

        if (_liveEntityHydration?.EnsureWorldOrigin(
                acceptedPositionRecord,
                acceptedPositionAuthorityVersion,
                acceptedSpawn) != true
            || !IsCurrentPositionOwner())
            return;

        var p = update.Position;
        int lbX = (int)((p.LandblockId >> 24) & 0xFFu);
        int lbY = (int)((p.LandblockId >> 16) & 0xFFu);
        var origin = new System.Numerics.Vector3(
            (lbX - _origin.CenterX) * 192f,
            (lbY - _origin.CenterY) * 192f,
            0f);
        var worldPos = new System.Numerics.Vector3(p.PositionX, p.PositionY, p.PositionZ) + origin;

        bool forceLocal = timestampDisposition is AcDream.Core.Physics.PositionTimestampDisposition.ForcePosition
            && update.Guid == _playerServerGuid
            && _playerController is not null;
        if (forceLocal)
        {
            if (!IsCurrentPositionOwner())
                return;

            RuntimeAcceptedPositionExecutionStatus forceStatus =
                _acceptedPositionDrive.TryExecuteAcceptedLocalPosition(
                    acceptedPositionCanonical,
                    update,
                    timestampDisposition,
                    timestamps,
                    timestamps.PreviousTeleport);
            if (forceStatus is RuntimeAcceptedPositionExecutionStatus.Committed
                or RuntimeAcceptedPositionExecutionStatus.DeferredCell)
            {
                _entityEffects?.MarkLiveOwnerPoseDirty(update.Guid);
                _authorityGate.ObserveAcceptedLocalPosition(
                    update.Position.LandblockId);
                return;
            }
            if (forceStatus is not RuntimeAcceptedPositionExecutionStatus.NotApplicable)
            {
                return;
            }
        }

        if (RequiresSpatialProjectionRecovery(acceptedPositionRecord))
        {
            if (!IsCurrentPositionOwner())
                return;
            AcDream.App.Rendering.ChildUnparentDisposition unparented =
                _equippedChildRenderer?.OnChildBecameUnparented(
                    update.Guid,
                    () =>
                    {
                        if (!IsCurrentPositionOwner())
                            return;
                        _liveEntityHydration!.RecoverProjection(
                            acceptedPositionRecord,
                            acceptedPositionAuthorityVersion,
                            acceptedSpawn);
                    })
                ?? AcDream.App.Rendering.ChildUnparentDisposition.NotAttached;
            if (unparented is AcDream.App.Rendering.ChildUnparentDisposition.Superseded
                or AcDream.App.Rendering.ChildUnparentDisposition.Pending)
                return;
            if (!IsCurrentPositionOwner())
                return;
            if (unparented is AcDream.App.Rendering.ChildUnparentDisposition.NotAttached)
            {
                _liveEntityHydration!.RecoverProjection(
                    acceptedPositionRecord,
                    acceptedPositionAuthorityVersion,
                    acceptedSpawn);
                if (!IsCurrentPositionOwner())
                    return;
            }
        }

        if (!_liveEntities.TryGetWorldEntity(update.Guid, out var entity)) return;
        if (!IsCurrentPositionOwner(entity))
            return;
        _entityEffects?.MarkLiveOwnerPoseDirty(update.Guid);
        if (!IsCurrentPositionOwner(entity))
            return;

        if (update.Guid == _playerServerGuid)
            _authorityGate.ObserveAcceptedLocalPosition(update.Position.LandblockId);

        var rot = timestampDisposition is AcDream.Core.Physics.PositionTimestampDisposition.ForcePosition
            ? entity.Rotation
            : new System.Numerics.Quaternion(p.RotationX, p.RotationY, p.RotationZ, p.RotationW);
        _movementTruthDiagnostics.OnServerEcho(update, worldPos);

        RuntimeAuthoritativePositionRoute? earlyRemoteRoute =
            update.Guid != _playerServerGuid
                ? ClassifyRemoteAcceptedPosition(
                    update,
                    acceptedPositionCanonical,
                    timestampDisposition,
                    timestamps,
                    worldPos)
                : null;
        bool isMissilePacket = earlyRemoteRoute is { } classifiedRoute
            ? classifiedRoute.OperationKind
                is RuntimeSetPositionOperationKind.ProjectileAuthoritative
            : update.Guid != _playerServerGuid
                && (acceptedPositionCanonical.FinalPhysicsState
                    & AcDream.Core.Physics.PhysicsStateFlags.Missile) != 0
                && acceptedPositionCanonical.Projectile is { } boundProjectile
                && ReferenceEquals(
                    acceptedPositionCanonical.PhysicsBody,
                    boundProjectile.Body);
        if (isMissilePacket)
        {
            if (earlyRemoteRoute is { } route)
            {
                if (route.Disposition
                        is RuntimeAuthoritativePositionDisposition.SetPosition
                    && acceptedPositionCanonical.RemoteMotion is RemoteMotion adoptedRemote)
                {
                    _projectileArmPositionRecord = acceptedPositionRecord;
                    _projectileArmPositionAuthorityVersion =
                        acceptedPositionAuthorityVersion;
                    RunRemoteTeleportHook(
                        acceptedPositionCanonical,
                        adoptedRemote,
                        _remoteArmCallbacks.IsCurrentProjectilePositionOwner);
                }

                RuntimeRemotePlacementExecutionStatus? placementStatus =
                    _remotePlacementDrive.ApplyAcceptedProjectilePosition(
                        acceptedPositionCanonical,
                        route);
                if (placementStatus is not null
                    and not RuntimeRemotePlacementExecutionStatus.Deferred
                    and not RuntimeRemotePlacementExecutionStatus.RejectedByPlacement)
                {
                    _projectileController?.SyncPresentationFromResolvedBody(
                        acceptedPositionRecord,
                        _physicsScriptGameTime);
                }
            }
            return;
        }

        if (!_liveEntities.TryGetRecord(
                update.Guid,
                out LiveEntityRecord positionRecord)
            || !ReferenceEquals(positionRecord, acceptedPositionRecord)
            || !ReferenceEquals(positionRecord.WorldEntity, entity)
            || !_liveEntities.IsCurrentPositionAuthority(
                positionRecord,
                acceptedPositionAuthorityVersion))
        {
            return;
        }


        TryApplyGenericRemoteRenderPose(
            entity,
            earlyRemoteRoute,
            worldPos,
            p.LandblockId,
            rot);
        if (!_liveEntities!.RebucketLiveEntity(update.Guid, p.LandblockId)
            || !_liveEntities.TryGetRecord(
                update.Guid,
                out LiveEntityRecord afterRebucket)
            || !ReferenceEquals(afterRebucket, positionRecord)
            || !ReferenceEquals(afterRebucket.WorldEntity, entity)
            || !_liveEntities.IsCurrentPositionAuthority(
                afterRebucket,
                acceptedPositionAuthorityVersion))
        {
            return;
        }


        if (update.Guid != _playerServerGuid)
        {
            var now = System.DateTime.UtcNow;
            RuntimeEntityKey positionKey = positionRecord.ProjectionKey
                ?? throw new InvalidOperationException(
                    $"Position owner 0x{update.Guid:X8}/" +
                    $"{positionRecord.Generation} has no exact projection key.");
            if (_remoteMovementObservations.TryGetValue(positionKey, out var prev))
            {
                float moveDist = System.Numerics.Vector3.Distance(prev.Pos, worldPos);
                if (moveDist > 0.05f)
                    _remoteMovementObservations[positionKey] = (worldPos, now);
                // else: leave old entry so "Time" = last real movement time
            }
            else
            {
                _remoteMovementObservations[positionKey] = (worldPos, now);
            }

            if (!_liveEntities.TryGetRemoteMotionRuntime(
                    update.Guid,
                    out IRuntimeRemoteMotion? remoteRuntime)
                || remoteRuntime is not RemoteMotion rmState)
            {
                rmState =
                    _liveEntities.GetOrCreateRemoteMotionRuntime(
                        update.Guid);
                // Hard-snap orientation on first spawn so the per-tick
                // slerp doesn't visibly rotate from Identity to truth.
                rmState.Body.Orientation = rot;
                rmState.Body.Position = worldPos;
                SeedRemoteSpawnPlacement(
                    rmState,
                    update.Guid,
                    entity,
                    worldPos,
                    update.Position.LandblockId);
            }

            if (!_liveEntities.IsCurrentPositionAuthority(
                    positionRecord,
                    acceptedPositionAuthorityVersion))
            {
                return;
            }


            if (AcDream.Core.Physics.PhysicsDiagnostics.ShouldLogRemoteSlide(
                    update.Guid))
            {
                (int slideQueueDepth, int slideFailCount) =
                    rmState.Interp.DiagnosticInterpolationState;
                AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideUp(
                    guid: update.Guid,
                    wireGrounded: update.IsGrounded,
                    wireVelocity: update.Velocity,
                    disposition: earlyRemoteRoute is { } slideRoute
                        ? slideRoute.Disposition.ToString()
                        : "unclassified",
                    playerDistance: _playerController is { } slideController
                        ? System.Numerics.Vector3.Distance(
                            worldPos,
                            slideController.Position)
                        : null,
                    bodyToTarget: System.Numerics.Vector3.Distance(
                        rmState.Body.Position,
                        worldPos),
                    bodySnapThreshold:
                        RuntimeRemoteSteadyStatePosition.DiagnosticBodySnapThreshold,
                    willBeDrTicked: WillAdvanceRemoteMotion(update.Guid, rmState),
                    firstUp: rmState.LastServerPosTime <= 0.0,
                    airborne: rmState.Airborne,
                    contact: rmState.Body.InContact,
                    onWalkable: rmState.Body.OnWalkable,
                    gravity: rmState.Body.HasGravity,
                    bodyVelocity: rmState.Body.Velocity,
                    contactPlaneValid: rmState.Body.ContactPlaneValid,
                    contactPlaneNormalZ: rmState.Body.ContactPlane.Normal.Z,
                    wirePosition: worldPos,
                    bodyPosition: rmState.Body.Position,
                    interpQueueDepth: slideQueueDepth,
                    interpFailCount: slideFailCount);
            }

            double nowSec = (now - System.DateTime.UnixEpoch).TotalSeconds;

            if (IsPlayerGuid(update.Guid))
            {
                if (System.Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1"
                    && rmState.LastServerPosTime > 0.0)
                {
                    double dtServer = nowSec - rmState.LastServerPosTime;
                    if (dtServer > 0.001)
                    {
                        var serverDelta = worldPos - rmState.LastServerPos;
                        float serverSpeed = (float)(serverDelta.Length() / dtServer);
                        float rootMotionSpeed = rmState.MaxRootMotionSpeedSinceLastUP;
                        if (serverSpeed > 0.1f || rootMotionSpeed > 0.1f)
                        {
                            System.Console.WriteLine(
                                $"[VEL_DIAG] guid={update.Guid:X8} maxRootMotionSpeed={rootMotionSpeed:F3} m/s "
                                + $"serverSpeed={serverSpeed:F3} m/s dtServer={dtServer:F3}s "
                                + $"ratio={(serverSpeed > 1e-3f ? rootMotionSpeed / serverSpeed : 0f):F3}");
                        }
                    }
                }
                rmState.MaxRootMotionSpeedSinceLastUP = 0f;
                rmState.PrevServerPos = rmState.LastServerPos;
                rmState.PrevServerPosTime = rmState.LastServerPosTime;
            }

            if (RuntimeRemoteSteadyStatePosition.IsAirborneNoOperation(
                    earlyRemoteRoute))
            {
                ApplyWireAirborneLeftoverBookkeeping(
                    rmState, p.LandblockId, worldPos, nowSec);
                return;
            }

            bool isTeleportRoute = RuntimeRemoteTeleportPosition
                .OwnsTeleportPlacement(earlyRemoteRoute);

            if (!update.IsGrounded && !isTeleportRoute)
            {
                ApplyWireAirborneLeftoverBookkeeping(
                    rmState, p.LandblockId, worldPos, nowSec);
                return;
            }

            if (!isTeleportRoute)
            {
                System.Numerics.Vector3? serverVelocity = update.Velocity;
                if (serverVelocity is null && rmState.LastServerPosTime > 0.0)
                {
                    double elapsed = nowSec - rmState.LastServerPosTime;
                    if (elapsed > 0.001)
                        serverVelocity = (worldPos - rmState.LastServerPos) / (float)elapsed;
                }
                if (serverVelocity is { } authoritativeVelocity)
                {
                    rmState.ServerVelocity = authoritativeVelocity;
                    rmState.HasServerVelocity = true;
                }
                else
                {
                    rmState.ServerVelocity = System.Numerics.Vector3.Zero;
                    rmState.HasServerVelocity = false;
                }
            }

            // A sticky lease (a creature closing on its melee target) does not
            // gate the position arm: the server correction routes like any
            // other, and the per-tick stick adjustment then overwrites the frame.
            RemoteContactArm arm = RemoteContactArm.UnroutedCatchUp;
            RemoteContactRouting? routing = RunRemoteArmTail(
                acceptedPositionCanonical,
                positionRecord,
                rmState,
                earlyRemoteRoute,
                update.Guid,
                worldPos,
                rot,
                acceptedPositionAuthorityVersion,
                entity);
            if (routing is null)
                return;
            arm = routing.Value.Arm;

            if (arm is RemoteContactArm.AirborneSnap)
            {
                if (IsPlayerGuid(update.Guid))
                {
                    rmState.Interp.Clear();
                }

                if (_animatedEntities.TryGetValue(entity.Id, out var aeForLand)
                    && aeForLand.Sequencer is not null)
                {
                    _motionRuntime.EnsureRemoteMotionBindings(
                        rmState, aeForLand, update.Guid);
                }
            }

            RuntimeRemoteSteadyStatePosition.TryArmConstraintAfterOperation(
                ToConstraintArm(arm), rmState);

            TryAdoptWireCellAfterRouting(rmState, arm, p.LandblockId);

            rmState.LastServerPos = worldPos;
            rmState.LastServerPosTime = nowSec;

            if (!isTeleportRoute
                && rmState.HasServerVelocity
                && _animatedEntities.TryGetValue(entity.Id, out var aeForVelocity))
            {
                if (System.Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1")
                {
                    string velSrc = update.Velocity is null ? "synth" : "wire";
                    System.Console.WriteLine(
                        $"[UPCYCLE_SRC] guid={update.Guid:X8} src={velSrc}");
                }
                RemoteServerControlledVelocityCycle.Apply(
                    update.Guid,
                    aeForVelocity,
                    rmState,
                    rmState.ServerVelocity);
            }

            entity.SetPosition(rmState.Body.Position);
            entity.ParentCellId = rmState.CellId;
            entity.Rotation = rmState.Body.Orientation;
            AcDream.App.Physics.LiveEntityShadowPublisher.TryPublishRemote(
                _liveEntities,
                positionRecord,
                entity,
                rmState,
                acceptedPositionAuthorityVersion,
                () => _remotePhysicsUpdater.SyncRemoteShadowToBody(
                    entity.Id,
                    rmState,
                    _origin.CenterX,
                    _origin.CenterY));
        }

        if (timestampDisposition is AcDream.Core.Physics.PositionTimestampDisposition.Apply
            && update.Guid == _playerServerGuid)
        {
            _localPlayerTeleport.OfferDestination(
                RuntimeTeleportDestinationAdapter.FromAcceptedPosition(
                    update),
                timestamps.TeleportAdvanced);
        }
    }

    internal static bool RequiresSpatialProjectionRecovery(
        LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.WorldEntity is null || !record.IsSpatiallyProjected;
    }

}
