using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Input;

internal sealed class PlayerModeController :
    ILocalPlayerTeleportModeOperations,
    IDevToolsPlayerModeTarget
{
    private readonly LocalPlayerModeState _mode;
    private readonly RuntimeLocalPlayerMovementState _controllerSlot;
    private readonly LocalPlayerPhysicsHostSlot _hostSlot;
    private readonly ChaseCameraInputState _chase;
    private readonly CameraController _camera;
    private readonly PhysicsEngine _physics;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly LiveWorldOriginState _origin;
    private readonly ILiveEntityMotionRuntimeBindings _motionBindings;
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly LiveCollisionAssetPublisher _collisionAssets;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animations;
    private readonly LocalPlayerAnimationController _animation;
    private readonly LocalPlayerShadowSynchronizer _shadow;
    private readonly IPlayerApproachCompletionLifetimeOwner _approachCompletions;
    private readonly ILocalPlayerTeleportInputLifetime _input;
    private readonly ILiveInWorldSource _session;
    private readonly MovementTruthDiagnosticController _movementDiagnostics;
    private readonly RuntimeMovementSkillState _skills;
    private readonly IViewportAspectSource _viewport;
    private PlayerModeAutoEntry? _autoEntry;
    private IPlayerApproachCompletionSink? _approachLifetime;

    public PlayerModeController(
        LocalPlayerModeState mode,
        RuntimeLocalPlayerMovementState controllerSlot,
        LocalPlayerPhysicsHostSlot hostSlot,
        ChaseCameraInputState chase,
        CameraController camera,
        PhysicsEngine physics,
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        LiveWorldOriginState origin,
        ILiveEntityMotionRuntimeBindings motionBindings,
        IDatReaderWriter dats,
        object datLock,
        LiveCollisionAssetPublisher collisionAssets,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        LocalPlayerAnimationController animation,
        LocalPlayerShadowSynchronizer shadow,
        IPlayerApproachCompletionLifetimeOwner approachCompletions,
        ILocalPlayerTeleportInputLifetime input,
        ILiveInWorldSource session,
        MovementTruthDiagnosticController movementDiagnostics,
        RuntimeMovementSkillState skills,
        IViewportAspectSource viewport)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _controllerSlot = controllerSlot ?? throw new ArgumentNullException(nameof(controllerSlot));
        _hostSlot = hostSlot ?? throw new ArgumentNullException(nameof(hostSlot));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _motionBindings = motionBindings ?? throw new ArgumentNullException(nameof(motionBindings));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        _collisionAssets = collisionAssets ??
            throw new ArgumentNullException(nameof(collisionAssets));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _animation = animation ?? throw new ArgumentNullException(nameof(animation));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _approachCompletions = approachCompletions
            ?? throw new ArgumentNullException(nameof(approachCompletions));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _movementDiagnostics = movementDiagnostics
            ?? throw new ArgumentNullException(nameof(movementDiagnostics));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
    }

    public PlayerMovementController? Controller => _controllerSlot.Controller;
    public Matrix4x4 Projection => _camera.Active.Projection;

    public void BindAutoEntry(PlayerModeAutoEntry autoEntry)
    {
        ArgumentNullException.ThrowIfNull(autoEntry);
        if (_autoEntry is not null)
            throw new InvalidOperationException("Player-mode auto-entry is already bound.");
        _autoEntry = autoEntry;
    }

    public void Toggle()
    {
        if (!_session.IsInWorld)
            return;

        _autoEntry?.Cancel();
        if (_mode.IsPlayerMode)
            Exit();
        else
            _ = TryEnter("Tab");
    }

    public void EnterFromAutoEntry()
    {
        if (TryEnter("auto-entry"))
        {
            Console.WriteLine(
                $"live: auto-entered player mode for 0x{_identity.ServerGuid:X8}");
        }
    }

    public bool TryEnterPortalSpace()
    {
        if (Controller is null && !TryEnter("teleport"))
            return false;

        if (Controller is not { CanExecuteLiveMovement: true } controller)
            return false;

        controller.State = PlayerState.PortalSpace;
        _autoEntry?.Cancel();
        return true;
    }

    public bool TryEnterPortalSpaceForLogin()
    {
        if (!_mode.IsPlayerMode && !TryEnter("login"))
            return false;

        return TryEnterPortalSpace();
    }

    public void EnterWorld()
    {
        if (Controller is { } controller)
            controller.State = PlayerState.InWorld;
    }

    public void Exit()
    {
        var failures = new List<Exception>();
        try { _input.EndMouseLook(); }
        catch (Exception error) { failures.Add(error); }
        try { _camera.ExitChaseMode(); }
        catch (Exception error) { failures.Add(error); }
        try { RetireApproachLifetime(); }
        catch (Exception error) { failures.Add(error); }
        _mode.IsPlayerMode = false;
        _hostSlot.Host = null;
        _chase.Legacy = null;
        _chase.Retail = null;

        if (failures.Count != 0)
            throw new AggregateException("Player-mode exit was incomplete.", failures);
    }

    public void ToggleFlyOrChase()
    {
        _autoEntry?.Cancel();
        if (_camera.IsFlyMode
            && _mode.IsPlayerMode
            && _chase.Legacy is { } legacy)
        {
            _chase.Retail ??= new RetailChaseCamera
            {
                Aspect = legacy.Aspect,
                CollisionProbe = new PhysicsCameraCollisionProbe(_physics),
            };
            _camera.EnterChaseMode(legacy, _chase.Retail);
            return;
        }

        _camera.ToggleFly();
    }

    public void ResetSession()
    {
        _autoEntry?.Cancel();
        var failures = new List<Exception>();
        try { _camera.ExitChaseMode(); }
        catch (Exception error) { failures.Add(error); }
        try { RetireApproachLifetime(); }
        catch (Exception error) { failures.Add(error); }
        _mode.ResetSession();
        _hostSlot.Host = null;
        _chase.Legacy = null;
        _chase.Retail = null;
        try { _movementDiagnostics.ResetSession(); }
        catch (Exception error) { failures.Add(error); }
        try { _shadow.ResetSession(); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count != 0)
            throw new AggregateException("Player-mode session reset was incomplete.", failures);
    }

    private bool TryEnter(string loggingTag)
    {
        uint playerGuid = _identity.ServerGuid;
        if (!_liveEntities.TryGetWorldEntity(
                playerGuid,
                out WorldEntity? playerEntity))
        {
            Console.WriteLine(
                $"live: {loggingTag} — player entity 0x{playerGuid:X8} not found yet");
            return false;
        }

        if (!_liveEntities.TryGetRecord(playerGuid, out LiveEntityRecord playerRecord))
        {
            Console.WriteLine(
                $"live: {loggingTag} — player record 0x{playerGuid:X8} not found yet");
            return false;
        }

        if (_controllerSlot.Controller is not { } publishedController
            || !publishedController.IsRuntimePublished
            || playerRecord.PhysicsHost is not EntityPhysicsHost)
        {
            Console.WriteLine(
                $"live: {loggingTag} — Runtime first-entry controller for "
                + $"0x{playerGuid:X8} not committed yet");
            return false;
        }

        BuildControllerAndCamera(
            loggingTag,
            playerGuid,
            playerEntity,
            playerRecord);
        return true;
    }

    private void BuildControllerAndCamera(
        string loggingTag,
        uint playerGuid,
        WorldEntity playerEntity,
        LiveEntityRecord playerRecord)
    {
        if (_controllerSlot.Controller is not { } controller
            || !controller.IsRuntimePublished)
        {
            throw new InvalidOperationException(
                $"Player mode ({loggingTag}) requires the Runtime-published "
                + "local movement controller; the first-entry conductor has "
                + "not committed it yet.");
        }
        if (playerRecord.PhysicsHost is not EntityPhysicsHost playerHost)
        {
            throw new InvalidOperationException(
                $"Player mode ({loggingTag}) requires the Runtime-committed "
                + "local physics host.");
        }

        IPlayerApproachCompletionSink approachLifetime =
            _approachCompletions.BeginControllerLifetime();
        bool lifetimeCommitted = false;
        bool cameraAttempted = false;
        bool shadowAttempted = false;
        CameraController.CameraState priorCamera = _camera.CaptureState();
        LocalPlayerShadowState.Snapshot? priorShadow = _shadow.Capture();
        try
        {
            if (controller.MoveTo is { } moveTo)
            {
                moveTo.MoveToComplete = error =>
                {
                    if (error == WeenieError.None)
                        approachLifetime.PublishNaturalCompletion();
                    else
                        approachLifetime.PublishCancellation(error);
                };
                moveTo.MoveToCancelled = error =>
                    approachLifetime.PublishCancellation(error);
            }

            if (_animations.TryGetValue(playerEntity.Id, out LiveEntityAnimationState? animation)
                && animation.Sequencer is { } sequencer)
            {
                controller.AttachCycleVelocityAccessor(() => sequencer.CurrentVelocity);
                controller.ObjectScale = animation.Scale;
                controller.AttachAnimationRootMotionSource(
                    _animation.AdvanceRoot,
                    _animation.CaptureHooks);
                controller.Motion.RemoveLinkAnimations =
                    sequencer.Manager.HandleEnterWorld;
                controller.Motion.InitializeMotionTables =
                    sequencer.Manager.InitializeState;
                controller.Motion.CheckForCompletedMotions =
                    sequencer.Manager.CheckForCompletedMotions;
                controller.Motion.DefaultSink =
                    new MotionTableDispatchSink(sequencer);
                sequencer.Manager.HandleEnterWorld();
                controller.Motion.HandleExitWorld();
            }

            var legacyCamera = new ChaseCamera { Aspect = _viewport.Aspect };
            var retailCamera = new RetailChaseCamera
            {
                Aspect = _viewport.Aspect,
                CollisionProbe = new PhysicsCameraCollisionProbe(_physics),
            };
            cameraAttempted = true;
            _camera.EnterChaseMode(legacyCamera, retailCamera);

            shadowAttempted = true;
            _shadow.SyncPose(
                playerEntity,
                controller.Position,
                playerEntity.Rotation,
                controller.CellId,
                force: true);

            _hostSlot.Host = playerHost;
            _chase.Legacy = legacyCamera;
            _chase.Retail = retailCamera;
            _mode.IsPlayerMode = true;
            _mode.ChaseModeEverEntered = true;
            _approachLifetime = approachLifetime;
            lifetimeCommitted = true;
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (shadowAttempted)
            {
                try { _shadow.Restore(playerEntity, priorShadow); }
                catch (Exception cleanupError) { failures.Add(cleanupError); }
            }
            if (cameraAttempted)
            {
                try { _camera.RestoreState(priorCamera); }
                catch (Exception cleanupError) { failures.Add(cleanupError); }
            }

            _mode.IsPlayerMode = false;
            _hostSlot.Host = null;
            _chase.Legacy = null;
            _chase.Retail = null;

            if (failures.Count != 1)
                throw new AggregateException(
                    "Player-mode entry failed and rollback was incomplete.",
                    failures);
            throw;
        }
        finally
        {
            if (!lifetimeCommitted)
                _approachCompletions.RetireControllerLifetime(approachLifetime);
        }
    }

    private void RetireApproachLifetime()
    {
        if (_approachLifetime is not { } lifetime)
            return;
        _approachLifetime = null;
        _approachCompletions.RetireControllerLifetime(lifetime);
    }

}
