using System;
using System.Numerics;
using AcDream.Core.Chat;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Input state for a single frame of player movement.
/// </summary>
public readonly record struct MovementInput(
    bool Forward = false,
    bool Backward = false,
    bool StrafeLeft = false,
    bool StrafeRight = false,
    bool TurnLeft = false,
    bool TurnRight = false,
    bool Run = false,
    float MouseDeltaX = 0f,
    bool Jump = false,
    bool IsPersistentCommand = false);

public readonly record struct PlayerMovementConstructionOptions(
    int RunSkill,
    int JumpSkill)
{
    public const int FallbackRunSkill = 200;
    public const int FallbackJumpSkill = 300;

    public static PlayerMovementConstructionOptions Fallback =>
        new(FallbackRunSkill, FallbackJumpSkill);

    public static PlayerMovementConstructionOptions From(
        RuntimeMovementSkillSnapshot skills) =>
        new(
            skills.RunSkill >= 0 ? skills.RunSkill : FallbackRunSkill,
            skills.JumpSkill >= 0 ? skills.JumpSkill : FallbackJumpSkill);
}

public readonly record struct JumpChargeSnapshot(bool IsCharging, float Power);

public readonly record struct MovementResult(
    Vector3 Position,
    Vector3 RenderPosition,
    uint CellId,
    bool IsOnGround,
    bool MotionStateChanged,
    uint? ForwardCommand,
    uint? SidestepCommand,
    uint? TurnCommand,
    float? ForwardSpeed,
    float? SidestepSpeed,
    float? TurnSpeed,
    bool IsRunning = false,
    bool JustLanded = false,              // true on the single frame we transitioned airborne → grounded
    float? JumpExtent = null,       // non-null when a jump was triggered this frame
    Vector3? JumpVelocity = null,
    bool ShouldSendMovementEvent = false,
    bool TurnUsesRunHold = false,
    bool SidestepUsesRunHold = false,
    bool IsMouseLookMovementEvent = false,
    uint CurrentStyle = 0x8000003Du,
    RawMotionState? RawMotionStateOverride = null);

public enum PlayerState { InWorld, PortalSpace }

internal enum PlayerMovementControllerPublicationLifecycle
{
    StandalonePublished,
    CandidatePreparing,
    CandidateSealed,
    RuntimeOwnedDormant,
    RuntimePublished,
    RuntimeRetired,
    Discarded,
}

public sealed class PlayerMovementController
{
    private readonly PhysicsEngine _physics;
    private readonly PhysicsBody _body;
    private readonly MotionInterpreter _motion;
    private readonly PlayerWeenie _weenie;
    private readonly AcDream.Core.Physics.Motion.MovementManager
        _movementManager;
    private float _stepUpHeight = 0.4f;
    private float _stepDownHeight = 0.4f;
    private ObjectInfoState _ownPvpFlags = ObjectInfoState.None;
    private System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>
        _sphereList;
    private float _objectScale = 1f;
    private PlayerState _state = PlayerState.InWorld;
    private uint _localEntityId;
    private AcDream.Core.Physics.Motion.PositionManager? _positionManager;

    public Action<string, RetailLogTextType>? OnInterfaceText { get; set; }

    public float StepUpHeight
    {
        get => _stepUpHeight;
        set
        {
            EnsureConfigurationMutable();
            _stepUpHeight = value;
        }
    }

    public float StepDownHeight
    {
        get => _stepDownHeight;
        set
        {
            EnsureConfigurationMutable();
            _stepDownHeight = value;
        }
    }

    public ObjectInfoState OwnPvpFlags
    {
        get => _ownPvpFlags;
        set
        {
            EnsureConfigurationMutable();
            _ownPvpFlags = value;
        }
    }

    public System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>
        SphereList
    {
        get => _sphereList;
        set
        {
            EnsureConfigurationMutable();
            _sphereList = value;
        }
    }

    public float ObjectScale
    {
        get => _objectScale;
        set
        {
            EnsureConfigurationMutable();
            _objectScale = value;
        }
    }

    public PlayerState State
    {
        get => _state;
        set
        {
            EnsurePublishedForRuntimeOperation();
            _state = value;
        }
    }

    public float Yaw
    {
        get => AcDream.Core.Physics.Motion.MoveToMath.YawFromHeading(
            AcDream.Core.Physics.Motion.MoveToMath.GetHeading(_body.Orientation));
        set
        {
            EnsurePublishedForRuntimeOperation();
            float wrapped = value;
            while (wrapped > MathF.PI) wrapped -= 2f * MathF.PI;
            while (wrapped < -MathF.PI) wrapped += 2f * MathF.PI;
            _body.Orientation = AcDream.Core.Physics.Motion.MoveToMath.SetHeading(
                _body.Orientation,
                AcDream.Core.Physics.Motion.MoveToMath.HeadingFromYaw(wrapped));
        }
    }
    public Vector3 Position => _body.Position;
    public Vector3 RenderPosition => ComputeRenderPosition();
    public uint CellId { get; private set; }
    public AcDream.Core.Physics.Position CellPosition => _body.CellPosition;

    internal AcDream.Core.Physics.Position CurrentCellPosition
    {
        get
        {
            AcDream.Core.Physics.Position carried = _body.CellPosition;
            return new AcDream.Core.Physics.Position(
                carried.ObjCellId,
                carried.Frame.Origin,
                _body.Orientation);
        }
    }

    internal bool AdvancedObjectQuantumLastTick { get; private set; }

    public float PresentedDeltaSeconds { get; private set; }

    private float _lastQuantumSeconds = PhysicsBody.MinQuantum;

    private float ComputePresentedDelta(
        float wallDt,
        double pendingBeforeSeconds,
        in RetailObjectQuantumBatch batch)
    {
        if (batch.Discarded)
        {
            _lastQuantumSeconds = PhysicsBody.MinQuantum;
            return wallDt;
        }
        float previousInterval = _lastQuantumSeconds;
        float simulated = 0f;
        if (batch.Count > 0)
        {
            simulated = batch.FullSteps * PhysicsBody.MaxQuantum + batch.Remainder;
            _lastQuantumSeconds = batch.GetQuantum(batch.Count - 1);
        }
        float interval = _lastQuantumSeconds;
        float before = Math.Min((float)pendingBeforeSeconds, previousInterval);
        float after = Math.Min((float)_objectClock.PendingSeconds, interval);
        float delta = simulated + (after - interval) - (before - previousInterval);
        return Math.Max(delta, 0f);
    }

    internal bool TryGetOutboundPosition(
        out AcDream.Core.Physics.Position outboundPosition)
    {
        EnsurePublishedForRuntimeOperation();
        outboundPosition = CurrentCellPosition;
        return PositionFrameValidation.IsValid(
            outboundPosition.ObjCellId,
            outboundPosition.Frame.Origin,
            outboundPosition.Frame.Orientation);
    }

    public uint LocalEntityId
    {
        get => _localEntityId;
        set
        {
            if (value == _localEntityId)
                return;
            EnsureConfigurationMutable();
            _localEntityId = value;
        }
    }

    public void ApplyPhysicsState(PhysicsStateFlags state)
    {
        EnsureConfigurationMutable();
        _body.State = state;
        _body.calc_acceleration();
    }

    internal RuntimeServerPhysicsStateApplication ApplyServerPhysicsState(
        PhysicsStateFlags state)
    {
        switch (_publicationLifecycle)
        {
            case PlayerMovementControllerPublicationLifecycle.StandalonePublished:
            case PlayerMovementControllerPublicationLifecycle.CandidatePreparing:
            case PlayerMovementControllerPublicationLifecycle.RuntimePublished:
                _body.State = state;
                _body.calc_acceleration();
                return RuntimeServerPhysicsStateApplication.AppliedLive;
            case PlayerMovementControllerPublicationLifecycle.RuntimeOwnedDormant:
                return RuntimeServerPhysicsStateApplication
                    .DroppedDormantActivationOwned;
            default:
                return RuntimeServerPhysicsStateApplication
                    .DroppedDisplacedController;
        }
    }

    public bool IsAirborne => !_body.OnWalkable;

    public float VerticalVelocity => _body.Velocity.Z;

    /// <summary>Full 3D world-space velocity of the physics body. Exposed for diagnostic logging.</summary>
    public Vector3 BodyVelocity => _body.Velocity;

    public System.Numerics.Plane ContactPlane => _body.ContactPlane;

    private bool _jumpCharging;
    private float _jumpExtent;

    private bool _prevJumpHeld;

    public JumpChargeSnapshot JumpCharge
        => new(_jumpCharging, _jumpCharging ? _jumpExtent : 0f);

    internal bool IsStandingStill => _motion.IsStandingStill();

    internal void FinishJump()
    {
        _jumpCharging = false;
        _jumpExtent = 0f;
        _motion.StandingLongJump = false;
    }
    private const float JumpChargeRate =
        1f / (float)AcDream.Core.Combat.CombatInputPlanner.AttackPowerUpSeconds;
    private const float DualWieldJumpChargeRate =
        1f / (float)AcDream.Core.Combat.CombatInputPlanner.DualWieldPowerUpSeconds;

    private bool _wasAirborneLastFrame;

    private uint? _prevForwardCmd;
    private uint? _prevSidestepCmd;
    private uint? _prevTurnCmd;
    private float? _prevForwardSpeed;
    private bool _prevRunHold;

    private bool _prevForwardHeld;
    private bool _prevBackwardHeld;
    private bool _prevStrafeLeftHeld;
    private bool _prevStrafeRightHeld;
    private bool _prevTurnLeftHeld;
    private bool _prevTurnRightHeld;
    private bool _prevRunHeld;
    private bool _hasInputSnapshot;

    private bool _mouseLookActive;
    private bool _mouseTurnSamplePending;
    private float _mouseTurnAdjustment;
    private uint? _activeInputTurnCommand;
    private float _activeInputTurnSpeed;
    private bool _activeInputTurnFromMouse;
    private uint? _activeInputSidestepCommand;
    private bool _activeInputSidestepUsesRunHold;
    private bool _mouseMovementEventCandidate;
    private bool _mouseMovementEventPending;
    private float _lastMouseMovementEventTime;
    private bool _controlledByServer = true;

    public const float MouseMovementEventInterval = 0.5f;

    private const float MouseTurnDeadZone = 0.02f;
    private const float MouseTurnSpeedScale = 2.0f;
    private const float MouseTurnMaximumSpeed = 1.5f;

    public const float HeartbeatInterval = 1.0f;

    private AcDream.Core.Physics.Position _lastSentPosition;
    private System.Numerics.Plane _lastSentContactPlane;
    private float _lastSentTime;
    private bool _lastSentInitialized;
    private float _simTimeSeconds;

    /// <summary>Sim-time accumulator (advanced by dt at the top of Update).
    /// Exposed for the network outbound layer to stamp NotePositionSent.</summary>
    public float SimTimeSeconds => _simTimeSeconds;

    private RetailObjectQuantumClock _objectClock;
    private PlayerMovementControllerPublicationLifecycle _publicationLifecycle;
    private Vector3 _prevPhysicsPos;
    private Vector3 _currPhysicsPos;
    private Action<float, AcDream.Core.Physics.Motion.MotionDeltaFrame>?
        _advanceAnimationRootMotion;
    private Action? _processAnimationHooks;
    private readonly AcDream.Core.Physics.Motion.MotionDeltaFrame
        _animationRootMotionScratch = new();
    private readonly AcDream.Core.Physics.Motion.MotionDeltaFrame
        _positionManagerDeltaScratch = new();
    private bool _externalMovementEventPending;
    private RawMotionState? _externalRawMotionStatePending;
    private uint _localActionStamp;


    public AcDream.Core.Physics.Motion.MovementManager Movement
    {
        get
        {
            EnsureConfigurationMutable();
            return _movementManager;
        }
    }

    public AcDream.Core.Physics.Motion.MoveToManager? MoveTo
    {
        get => Movement.MoveTo;
        set
        {
            EnsureConfigurationMutable();
            var mtm = value ?? throw new ArgumentNullException(nameof(value));
            Movement.MoveToFactory = () => mtm;
            Movement.MakeMoveToManager();
        }
    }

    public AcDream.Core.Physics.Motion.PositionManager? PositionManager
    {
        get
        {
            EnsureConfigurationMutable();
            return _positionManager;
        }
        set
        {
            EnsureConfigurationMutable();
            _positionManager = value;
        }
    }

    public PlayerMovementController(
        PhysicsEngine physics,
        RetailObjectQuantumClock? objectClock = null)
        : this(
            physics,
            objectClock,
            PlayerMovementConstructionOptions.Fallback,
            PlayerMovementControllerPublicationLifecycle.StandalonePublished)
    {
    }

    public PlayerMovementController(
        PhysicsEngine physics,
        RetailObjectQuantumClock? objectClock,
        PlayerMovementConstructionOptions options)
        : this(
            physics,
            objectClock,
            options,
            PlayerMovementControllerPublicationLifecycle.StandalonePublished)
    {
    }

    private PlayerMovementController(
        PhysicsEngine physics,
        RetailObjectQuantumClock? objectClock,
        PlayerMovementConstructionOptions options,
        PlayerMovementControllerPublicationLifecycle publicationLifecycle)
    {
        _physics = physics;
        _objectClock = objectClock ?? new RetailObjectQuantumClock();
        _publicationLifecycle = publicationLifecycle;

        _body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        };

        _weenie = new PlayerWeenie(
            runSkill: options.RunSkill,
            jumpSkill: options.JumpSkill);
        _motion = new MotionInterpreter(_body, _weenie);
        _movementManager = new AcDream.Core.Physics.Motion.MovementManager(_motion);
        _movementManager.ActivatePhysicsObject = ActivateFromMovement;
        _body.LastMoveWasAutonomous = true;
    }

    internal static PlayerMovementController CreatePublicationCandidate(
        PhysicsEngine physics,
        PlayerMovementConstructionOptions options) => new(
            physics,
            new RetailObjectQuantumClock(),
            options,
            PlayerMovementControllerPublicationLifecycle.CandidatePreparing);

    internal PhysicsBody PhysicsBody
    {
        get
        {
            EnsureConfigurationMutable();
            return _body;
        }
    }

    internal bool OwnsPhysicsBody(PhysicsBody body) =>
        ReferenceEquals(_body, body);

    internal bool IsSealedPublicationCandidate => _publicationLifecycle
        is PlayerMovementControllerPublicationLifecycle.CandidateSealed;

    internal bool IsRuntimeOwnedDormant => _publicationLifecycle
        is PlayerMovementControllerPublicationLifecycle.RuntimeOwnedDormant;

    internal bool IsRuntimePublished => _publicationLifecycle
        is PlayerMovementControllerPublicationLifecycle.RuntimePublished;

    public bool CanExecuteLiveMovement => _publicationLifecycle
        is PlayerMovementControllerPublicationLifecycle.StandalonePublished
            or PlayerMovementControllerPublicationLifecycle.RuntimePublished;

    private bool _dormantSetPositionGroundPhase;

    internal void BeginDormantSetPositionGroundPhase()
    {
        if (!IsRuntimeOwnedDormant || _dormantSetPositionGroundPhase)
            throw new InvalidOperationException(
                "Dormant SetPosition ground phase requires one dormant Runtime owner.");
        _dormantSetPositionGroundPhase = true;
    }

    internal void EndDormantSetPositionGroundPhase()
    {
        if (!_dormantSetPositionGroundPhase)
            throw new InvalidOperationException(
                "Dormant SetPosition ground phase is not active.");
        _body.TransientState &= ~TransientStateFlags.Active;
        _dormantSetPositionGroundPhase = false;
    }

    internal bool IsDormantSetPositionGroundPhaseActive =>
        _dormantSetPositionGroundPhase;

    internal void RefreshDormantRuntimePhysicsState(
        PhysicsStateFlags state,
        bool recalculateAcceleration)
    {
        if (!IsRuntimeOwnedDormant || _dormantSetPositionGroundPhase)
            throw new InvalidOperationException(
                "Only an idle dormant Runtime owner can refresh physics state.");
        _body.State = state;
        if (recalculateAcceleration)
            _body.calc_acceleration();
    }

    internal void RefreshDormantRuntimeVector(
        Vector3? velocity,
        Vector3? omega)
    {
        if (!IsRuntimeOwnedDormant || _dormantSetPositionGroundPhase)
            throw new InvalidOperationException(
                "Only an idle dormant Runtime owner can refresh vector state.");
        if (velocity is { } liveVelocity)
            _body.set_velocity(liveVelocity);
        if (omega is { } liveOmega)
            _body.Omega = liveOmega;
        _body.TransientState &= ~TransientStateFlags.Active;
    }

    internal void SealPublicationCandidate()
    {
        if (_publicationLifecycle
            is not PlayerMovementControllerPublicationLifecycle
                .CandidatePreparing)
        {
            throw new InvalidOperationException(
                "Only a preparing Runtime movement candidate can be sealed.");
        }
        _publicationLifecycle = PlayerMovementControllerPublicationLifecycle
            .CandidateSealed;
    }

    internal void CommitRuntimeOwnership(RetailObjectQuantumClock objectClock)
    {
        ArgumentNullException.ThrowIfNull(objectClock);
        if (_publicationLifecycle
            is not PlayerMovementControllerPublicationLifecycle.CandidateSealed)
        {
            throw new InvalidOperationException(
                "Only a sealed Runtime movement candidate can be published.");
        }
        _objectClock = objectClock;
        _publicationLifecycle = PlayerMovementControllerPublicationLifecycle
            .RuntimeOwnedDormant;
    }

    internal void ActivateRuntimePublication()
    {
        if (_publicationLifecycle
            is not PlayerMovementControllerPublicationLifecycle
                .RuntimeOwnedDormant
            || _dormantSetPositionGroundPhase)
        {
            throw new InvalidOperationException(
                "Only a dormant Runtime-owned movement controller can be activated.");
        }
        _publicationLifecycle = PlayerMovementControllerPublicationLifecycle
            .RuntimePublished;
    }

    internal void CommitRuntimeActivationFrame()
    {
        if (_publicationLifecycle
            is not PlayerMovementControllerPublicationLifecycle
                .RuntimeOwnedDormant
            || _dormantSetPositionGroundPhase)
        {
            throw new InvalidOperationException(
                "Only a dormant Runtime-owned movement controller can accept its activation frame.");
        }

        _prevPhysicsPos = _body.Position;
        _currPhysicsPos = _body.Position;
        CellId = _body.CellPosition.ObjCellId;
    }

    internal void DiscardRuntimeCandidate()
    {
        if (_publicationLifecycle
            is PlayerMovementControllerPublicationLifecycle.CandidatePreparing
                or PlayerMovementControllerPublicationLifecycle.CandidateSealed)
        {
            _publicationLifecycle = PlayerMovementControllerPublicationLifecycle
                .Discarded;
        }
    }

    internal void RetireRuntimePublication()
    {
        if (_publicationLifecycle
            is PlayerMovementControllerPublicationLifecycle.RuntimeOwnedDormant
                or PlayerMovementControllerPublicationLifecycle.RuntimePublished)
        {
            _publicationLifecycle = PlayerMovementControllerPublicationLifecycle
                .RuntimeRetired;
        }
    }

    private void EnsureConfigurationMutable()
    {
        if (_publicationLifecycle
            is PlayerMovementControllerPublicationLifecycle.StandalonePublished
                or PlayerMovementControllerPublicationLifecycle
                    .CandidatePreparing
                or PlayerMovementControllerPublicationLifecycle.RuntimePublished
            || IsRuntimeOwnedDormant && _dormantSetPositionGroundPhase)
        {
            return;
        }
        throw new InvalidOperationException(
            "A sealed, retired, or discarded Runtime movement controller cannot be mutated.");
    }

    private void ReportJumpRefusal(WeenieError result)
    {
        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeJumpEnabled)
        {
            Console.WriteLine(
                $"[jump] ReportJumpRefusal result={result} "
                + $"hasCallback={OnInterfaceText is not null} "
                + $"onWalkable={_body.OnWalkable} "
                + $"prevJumpHeld={_prevJumpHeld}");
        }

        if (OnInterfaceText is null)
            return;

        string? text = result switch
        {
            WeenieError.NotGrounded => ClientTextRefusals.CantJumpInAir,                // 0x24
            WeenieError.YouCantJumpFromThisPosition => ClientTextRefusals.CantJumpPosition, // 0x48
            WeenieError.CantJumpLoadedDown => ClientTextRefusals.CantJumpLoad,           // 0x49
            _ => null,
        };
        if (text is not null)
            OnInterfaceText(text, RetailLogTextType.ClientLocal);
    }

    private void EnsurePublishedForRuntimeOperation()
    {
        if (_publicationLifecycle
            is PlayerMovementControllerPublicationLifecycle.StandalonePublished
                or PlayerMovementControllerPublicationLifecycle.RuntimePublished)
        {
            return;
        }
        throw new InvalidOperationException(
            "An unpublished or retired Runtime movement controller cannot execute live movement operations.");
    }

    private void ActivateFromMovement()
    {
        EnsureConfigurationMutable();
        if ((_body.State & PhysicsStateFlags.Static) != 0)
            return;

        if (IsRuntimeOwnedDormant && _dormantSetPositionGroundPhase)
            return;

        _objectClock.Activate();
        _body.TransientState |= TransientStateFlags.Active;
    }

    internal void SuspendObjectUpdate(float elapsedSeconds)
    {
        EnsurePublishedForRuntimeOperation();
        AdvancedObjectQuantumLastTick = false;
        PresentedDeltaSeconds = 0f;
        if (float.IsFinite(elapsedSeconds) && elapsedSeconds > 0f)
            _simTimeSeconds += elapsedSeconds;
        _objectClock.Deactivate();
        _body.TransientState &= ~TransientStateFlags.Active;
    }

    public bool BeginMouseLook(MovementInput input)
    {
        EnsurePublishedForRuntimeOperation();
        if (_mouseLookActive || State != PlayerState.InWorld)
            return false;

        _mouseLookActive = true;
        TakeControlFromServer(input);
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;
        ApplyMouseLookToggleMovement(input, entering: true);
        return true;
    }

    public void SubmitMouseTurnAdjustment(
        float signedAdjustment,
        MovementInput input)
    {
        EnsurePublishedForRuntimeOperation();
        if (!_mouseLookActive || !float.IsFinite(signedAdjustment))
            return;

        TakeControlFromServer(input);
        _mouseTurnAdjustment = signedAdjustment;
        _mouseTurnSamplePending = true;
        _mouseMovementEventCandidate = true;
    }

    public void StopMouseDrift(MovementInput input)
    {
        EnsurePublishedForRuntimeOperation();
        if (!_mouseLookActive)
            return;

        TakeControlFromServer(input);

        var p = MouseAxisParameters(_activeInputTurnSpeed == 0f
            ? 1f
            : _activeInputTurnSpeed);
        StopMotionAtPhysicsObjectBoundary(MotionCommand.TurnRight, p);
        StopMotionAtPhysicsObjectBoundary(MotionCommand.TurnLeft, p);
        _activeInputTurnCommand = null;
        _activeInputTurnSpeed = 0f;
        _activeInputTurnFromMouse = false;
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;
        _mouseMovementEventCandidate = true;
    }

    public bool EndMouseLook(MovementInput input)
    {
        EnsurePublishedForRuntimeOperation();
        if (!_mouseLookActive && !_activeInputTurnFromMouse)
            return false;

        _mouseLookActive = false;
        TakeControlFromServer(input);
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;

        ApplyMouseLookToggleMovement(input, entering: false);
        return true;
    }

    private void ApplyMouseLookToggleMovement(MovementInput input, bool entering)
    {
        (uint? sidestep, bool useRunHold) = DesiredInputSidestep(input, entering);
        ApplyInputSidestep(sidestep, useRunHold, reapply: !entering);

        uint? turn = entering
            ? null
            : input.TurnRight
                ? MotionCommand.TurnRight
                : input.TurnLeft
                    ? MotionCommand.TurnLeft
                    : null;
        ApplyInputTurn(turn, speed: 1f, fromMouse: false, reapply: !entering);

        bool runHeld = _motion.RawState.CurrentHoldKey == HoldKey.Run;
        if (runHeld != input.Run)
            _motion.set_hold_run(input.Run, interrupt: true);

        uint? desiredForward = input.Forward
            ? MotionCommand.WalkForward
            : input.Backward
                ? MotionCommand.WalkBackward
                : null;

        uint rawForward = _motion.RawState.ForwardCommand;
        uint? activeForward = rawForward == RawMotionState.Default.ForwardCommand
            ? null
            : rawForward;
        if (activeForward is { } active && active != desiredForward)
        {
            StopMotionAtPhysicsObjectBoundary(
                active,
                new AcDream.Core.Physics.Motion.MovementParameters());
        }

        if (desiredForward is { } command
            && (activeForward != desiredForward || !entering))
            DoMotionAtPhysicsObjectBoundary(
                command,
                new AcDream.Core.Physics.Motion.MovementParameters());

        _hasInputSnapshot = true;
        _prevForwardHeld = input.Forward;
        _prevBackwardHeld = input.Backward;
        _prevStrafeLeftHeld = input.StrafeLeft;
        _prevStrafeRightHeld = input.StrafeRight;
        _prevTurnLeftHeld = input.TurnLeft;
        _prevTurnRightHeld = input.TurnRight;
        _prevRunHeld = input.Run;

        _prevRunHold = input.Run;
        _body.LastMoveWasAutonomous = true;
    }

    private static (uint? Command, bool UseRunHold) DesiredInputSidestep(
        MovementInput input,
        bool mouseLookActive)
    {
        if (input.StrafeRight)
            return (MotionCommand.SideStepRight, false);
        if (input.StrafeLeft)
            return (MotionCommand.SideStepLeft, false);
        if (mouseLookActive && input.TurnRight)
            return (MotionCommand.SideStepRight, true);
        if (mouseLookActive && input.TurnLeft)
            return (MotionCommand.SideStepLeft, true);
        return (null, false);
    }

    private bool ApplyInputSidestep(
        uint? desired,
        bool useRunHold,
        bool reapply = false)
    {
        bool changed = desired != _activeInputSidestepCommand;
        if (_activeInputSidestepCommand is { } active
            && (changed || reapply))
        {
            StopMotionAtPhysicsObjectBoundary(
                active,
                _activeInputSidestepUsesRunHold
                    ? MouseAxisParameters(1f)
                    : new());
        }

        if (desired is { } command && (changed || reapply))
        {
            DoMotionAtPhysicsObjectBoundary(
                command,
                useRunHold ? MouseAxisParameters(1f) : new());
        }

        _activeInputSidestepCommand = desired;
        _activeInputSidestepUsesRunHold = desired.HasValue && useRunHold;
        return changed || (reapply && desired.HasValue);
    }

    private bool ApplyInputTurn(
        uint? desired,
        float speed,
        bool fromMouse,
        bool reapply = false)
    {
        bool commandChanged = desired != _activeInputTurnCommand;
        bool bothPresent = desired.HasValue && _activeInputTurnCommand.HasValue;
        bool speedChanged = bothPresent
            && MathF.Abs(speed - _activeInputTurnSpeed) >= 0.0001f;
        bool ownerChanged = bothPresent && fromMouse != _activeInputTurnFromMouse;
        bool apply = commandChanged || speedChanged || ownerChanged
            || (reapply && desired.HasValue);

        if (_activeInputTurnCommand is { } active && apply)
        {
            StopMotionAtPhysicsObjectBoundary(
                active,
                _activeInputTurnFromMouse
                    ? MouseAxisParameters(_activeInputTurnSpeed)
                    : new());
        }

        if (desired is { } command && apply)
        {
            DoMotionAtPhysicsObjectBoundary(
                command,
                fromMouse ? MouseAxisParameters(speed) : new());
        }

        _activeInputTurnCommand = desired;
        _activeInputTurnSpeed = desired.HasValue ? speed : 0f;
        _activeInputTurnFromMouse = desired.HasValue && fromMouse;
        return apply;
    }

    private static AcDream.Core.Physics.Motion.MovementParameters MouseAxisParameters(
        float speed) => new()
        {
            Speed = speed,
            SetHoldKey = false,
            HoldKeyToApply = HoldKey.Run,
        };

    private void TakeControlFromServer(MovementInput? currentInput = null)
    {
        if (!_controlledByServer)
            return;

        _controlledByServer = false;
        _body.LastMoveWasAutonomous = true;
        StopCompletelyAtPhysicsObjectBoundary();
        _activeInputTurnCommand = null;
        _activeInputTurnSpeed = 0f;
        _activeInputTurnFromMouse = false;
        _activeInputSidestepCommand = null;
        _activeInputSidestepUsesRunHold = false;

        _prevForwardHeld = false;
        _prevBackwardHeld = false;
        _prevStrafeLeftHeld = false;
        _prevStrafeRightHeld = false;
        _prevTurnLeftHeld = false;
        _prevTurnRightHeld = false;
        _prevRunHeld = _motion.RawState.CurrentHoldKey == HoldKey.Run;

        if (currentInput is { } input)
            ApplyMouseLookToggleMovement(input, entering: _mouseLookActive);
    }

    public bool PrepareForAttackRequest()
    {
        EnsurePublishedForRuntimeOperation();
        if (_controlledByServer)
            return false;

        StopCompletelyAtPhysicsObjectBoundary();
        _activeInputTurnCommand = null;
        _activeInputTurnSpeed = 0f;
        _activeInputTurnFromMouse = false;
        _activeInputSidestepCommand = null;
        _activeInputSidestepUsesRunHold = false;
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;
        _body.LastMoveWasAutonomous = true;
        return true;
    }

    public bool RequestPosture(uint motion)
    {
        EnsurePublishedForRuntimeOperation();
        if (motion is not (
            MotionCommand.Ready
            or MotionCommand.Crouch
            or MotionCommand.Sitting
            or MotionCommand.Sleeping))
        {
            return false;
        }

        TakeControlFromServer();
        var parameters =
            new AcDream.Core.Physics.Motion.MovementParameters();
        if (DoMotionAtPhysicsObjectBoundary(motion, parameters)
            != WeenieError.None)
        {
            return false;
        }

        _externalMovementEventPending = true;
        return true;
    }

    internal bool RequestTurnToHeading(
        float headingDegrees,
        bool applyRunHoldKey = false)
    {
        EnsurePublishedForRuntimeOperation();
        // @006b4580: `if (this->vtable->IsActive() == 0) return 0`. The local
        // analogue is "no MoveToManager bound yet" — the publication that
        // binds MoveToFactory is what makes this interpreter live.
        if (!float.IsFinite(headingDegrees) || Movement.MoveTo is null)
            return false;

        TakeControlFromServer();

        var parameters =
            new AcDream.Core.Physics.Motion.MovementParameters
            {
                // @006b4593  var_1c = arg2
                DesiredHeading = headingDegrees,
                Speed = 1f,
                StopCompletelyFlag = false,
            };
        // @006b45af / @006b45b1  var_8_1 = 2
        if (applyRunHoldKey)
            parameters.HoldKeyToApply = HoldKey.Run;

        _body.LastMoveWasAutonomous = true;
        if (Movement.PerformMovement(new MovementStruct
        {
            Type = MovementType.TurnToHeading,
            Params = parameters,
        }) != WeenieError.None)
        {
            return false;
        }

        _externalMovementEventPending = true;
        return true;
    }

    internal bool RequestCommandMotion(uint motion)
    {
        EnsurePublishedForRuntimeOperation();
        TakeControlFromServer();
        var parameters =
            new AcDream.Core.Physics.Motion.MovementParameters
            {
                Autonomous = true,
                ActionStamp = _localActionStamp,
            };
        if (DoMotionAtPhysicsObjectBoundary(motion, parameters)
            != WeenieError.None)
        {
            return false;
        }

        if ((motion & 0x10000000u) != 0u)
            _localActionStamp++;
        _externalRawMotionStatePending = new RawMotionState(_motion.RawState);
        _externalMovementEventPending = true;
        return true;
    }

    public void SetCharacterSkills(int runSkill, int jumpSkill)
    {
        EnsureConfigurationMutable();
        _weenie.SetSkills(runSkill, jumpSkill);
    }

    public void SetCharacterBurden(float burden)
    {
        EnsureConfigurationMutable();
        _weenie.SetBurden(burden);
    }

    public void SetCharacterStamina(int currentStamina)
    {
        EnsureConfigurationMutable();
        _weenie.SetStamina(currentStamina < 0 ? null : (uint)currentStamina);
    }

    public void SetCharacterPkStatus(int playerKillerStatus, float? lastPkAttackTimestamp)
    {
        EnsureConfigurationMutable();
        _weenie.SetPlayerKillerStatus(
            playerKillerStatus < 0 ? null : playerKillerStatus,
            lastPkAttackTimestamp);
    }

    internal RuntimeMovementStatsApplication ApplyCharacterMovementStats(
        in RuntimeMovementSkillSnapshot snapshot)
    {
        switch (_publicationLifecycle)
        {
            case PlayerMovementControllerPublicationLifecycle.StandalonePublished:
            case PlayerMovementControllerPublicationLifecycle.CandidatePreparing:
            case PlayerMovementControllerPublicationLifecycle.RuntimePublished:
                ApplyCharacterMovementStatsCore(snapshot);
                return RuntimeMovementStatsApplication.AppliedLive;
            case PlayerMovementControllerPublicationLifecycle.RuntimeOwnedDormant:
                ApplyCharacterMovementStatsCore(snapshot);
                return RuntimeMovementStatsApplication.AppliedDormant;
            default:
                return RuntimeMovementStatsApplication.DroppedDisplacedController;
        }
    }

    private void ApplyCharacterMovementStatsCore(
        in RuntimeMovementSkillSnapshot snapshot)
    {
        _weenie.SetSkills(snapshot.RunSkill, snapshot.JumpSkill);
        _weenie.SetBurden(snapshot.Burden);
        _weenie.SetStamina(
            snapshot.CurrentStamina < 0 ? null : (uint)snapshot.CurrentStamina);
        _ownPvpFlags = EntityCollisionFlagsExt
            .FromPwdBitfield(snapshot.OwnPwdBitfield)
            .ToMoverState();
        _weenie.SetPlayerKillerStatus(
            snapshot.PlayerKillerStatus < 0 ? null : snapshot.PlayerKillerStatus,
            snapshot.LastPkAttackTimestamp);
    }

    internal bool ReportExhaustionAtMovementBoundary()
    {
        if (_publicationLifecycle
            is PlayerMovementControllerPublicationLifecycle.StandalonePublished
                or PlayerMovementControllerPublicationLifecycle.CandidatePreparing
                or PlayerMovementControllerPublicationLifecycle.RuntimePublished)
        {
            _motion.ReportExhaustion();
            return true;
        }
        return false;
    }

    internal MotionInterpreter Motion
    {
        get
        {
            EnsureConfigurationMutable();
            return _motion;
        }
    }

    internal WeenieError StopCompletelyAtPhysicsObjectBoundary()
    {
        EnsureConfigurationMutable();
        return _movementManager.PerformMovement(new MovementStruct
        {
            Type = MovementType.StopCompletely,
        });
    }

    private WeenieError DoMotionAtPhysicsObjectBoundary(
        uint motion,
        AcDream.Core.Physics.Motion.MovementParameters parameters)
    {
        EnsureConfigurationMutable();
        _body.LastMoveWasAutonomous = true;
        return Movement.PerformMovement(new MovementStruct
        {
            Type = MovementType.RawCommand,
            Motion = motion,
            Params = parameters,
        });
    }

    private WeenieError StopMotionAtPhysicsObjectBoundary(
        uint motion,
        AcDream.Core.Physics.Motion.MovementParameters parameters)
    {
        EnsureConfigurationMutable();
        _body.LastMoveWasAutonomous = true;
        return Movement.PerformMovement(new MovementStruct
        {
            Type = MovementType.StopRawCommand,
            Motion = motion,
            Params = parameters,
        });
    }

    internal bool BodyInContact => _body.InContact;

    internal bool CanSendPositionEvent => _body.InContact && _body.OnWalkable;

    internal Quaternion BodyOrientation => _body.Orientation;

    public void SetBodyOrientation(Quaternion orientation)
    {
        EnsureConfigurationMutable();
        _body.Orientation = AcDream.Core.Physics.Motion.FrameOps.SetRotate(
            _body.Position,
            _body.Orientation,
            orientation);
    }

    internal void SetLastMoveWasAutonomous(bool autonomous)
    {
        EnsureConfigurationMutable();
        _body.LastMoveWasAutonomous = autonomous;
        _controlledByServer = !autonomous;
    }

    public void AttachCycleVelocityAccessor(Func<Vector3> accessor)
    {
        EnsureConfigurationMutable();
        if (accessor is null) throw new ArgumentNullException(nameof(accessor));
        _motion.GetCycleVelocity = accessor;
    }

    public void AttachAnimationRootMotionSource(
        Action<float, AcDream.Core.Physics.Motion.MotionDeltaFrame> advance,
        Action? processHooks = null)
    {
        EnsureConfigurationMutable();
        _advanceAnimationRootMotion = advance
            ?? throw new ArgumentNullException(nameof(advance));
        _processAnimationHooks = processHooks;
        _animationRootMotionScratch.Reset();
    }

    public void NoteMovementSent(float nowSeconds, bool mouseLookEvent = false)
    {
        EnsurePublishedForRuntimeOperation();
        _lastSentTime = nowSeconds;
        if (mouseLookEvent)
        {
            _mouseMovementEventPending = false;
            _lastMouseMovementEventTime = nowSeconds;
        }
    }

    public MovementResult CaptureMovementResult(bool mouseLookEvent)
    {
        EnsurePublishedForRuntimeOperation();
        var raw = _motion.RawState;
        uint? forward = raw.ForwardCommand == RawMotionState.Default.ForwardCommand
            ? null : raw.ForwardCommand;
        uint? sidestep = raw.SidestepCommand == RawMotionState.Default.SidestepCommand
            ? null : raw.SidestepCommand;
        uint? turn = raw.TurnCommand == RawMotionState.Default.TurnCommand
            ? null : raw.TurnCommand;
        return new MovementResult(
            Position: Position,
            RenderPosition: RenderPosition,
            CellId: CellId,
            IsOnGround: CanSendPositionEvent,
            MotionStateChanged: true,
            ForwardCommand: forward,
            SidestepCommand: sidestep,
            TurnCommand: turn,
            ForwardSpeed: forward.HasValue ? raw.ForwardSpeed : null,
            SidestepSpeed: sidestep.HasValue ? raw.SidestepSpeed : null,
            TurnSpeed: turn.HasValue ? raw.TurnSpeed : null,
            IsRunning: raw.CurrentHoldKey == HoldKey.Run,
            ShouldSendMovementEvent: true,
            TurnUsesRunHold: turn.HasValue && raw.TurnHoldKey == HoldKey.Run,
            SidestepUsesRunHold: sidestep.HasValue
                && raw.SidestepHoldKey == HoldKey.Run,
            IsMouseLookMovementEvent: mouseLookEvent,
            CurrentStyle: raw.CurrentStyle);
    }

    public MovementResult CapturePresentationResult()
    {
        MovementResult current = CaptureMovementResult(mouseLookEvent: false);
        return current with
        {
            MotionStateChanged = false,
            ShouldSendMovementEvent = false,
            IsMouseLookMovementEvent = false,
        };
    }

    public void NotePositionSent(AcDream.Core.Physics.Position position,
                                 System.Numerics.Plane contactPlane,
                                 float nowSeconds)
    {
        EnsurePublishedForRuntimeOperation();
        _lastSentPosition = position;
        _lastSentContactPlane = contactPlane;
        _lastSentTime = nowSeconds;
        _lastSentInitialized = true;
    }

    internal bool ShouldSendPositionEvent(
        AcDream.Core.Physics.Position currentPosition,
        System.Numerics.Plane currentContactPlane,
        float nowSeconds)
    {
        EnsurePublishedForRuntimeOperation();
        if (!_lastSentInitialized)
            return true;

        bool cellChanged = _lastSentPosition.ObjCellId != currentPosition.ObjCellId;
        if ((_lastSentTime + HeartbeatInterval) >= nowSeconds)
        {
            return cellChanged
                || !ApproxPlaneEqual(_lastSentContactPlane, currentContactPlane);
        }

        return cellChanged
            || !ApproxFrameEqual(_lastSentPosition.Frame, currentPosition.Frame);
    }

    private void UpdateCellId(
        uint newCellId,
        string reason,
        bool publishRenderRoot = true)
    {
        if (newCellId != CellId && PhysicsDiagnostics.ProbeCellEnabled)
        {
            var pos = _body.Position;
            Console.WriteLine(System.FormattableString.Invariant(
                $"[cell-transit] 0x{CellId:X8} -> 0x{newCellId:X8} pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) reason={reason}"));
        }
        CellId = newCellId;

        if (publishRenderRoot)
            _physics.UpdatePlayerCurrCell(newCellId);
    }

    internal void SeedPlacementForTest(Vector3 pos, uint cellId, Vector3 cellLocal)
    {
        EnsurePublishedForRuntimeOperation();
        SetPositionCore(
            pos,
            cellId,
            cellLocal,
            publishSharedState: true);
    }

    internal void PreparePositionForCommit(
        Vector3 pos,
        uint cellId,
        Vector3 cellLocal)
    {
        EnsureConfigurationMutable();
        SetPositionCore(
            pos,
            cellId,
            cellLocal,
            publishSharedState: false);
    }

    internal void ArmConstraintLeashAtCommittedPlacement()
    {
        EnsurePublishedForRuntimeOperation();
        RearmConstraintLeashAtCurrentPosition();
    }

    private void RearmConstraintLeashAtCurrentPosition()
    {
        if (PositionManager is not { } positionManager)
            return;
        AcDream.Core.Physics.Position anchor = _body.CellPosition;
        positionManager.ConstrainTo(
            anchor,
            AcDream.Core.Physics.Motion.ConstraintDistance.GetStartConstraintDistance(anchor.ObjCellId),
            AcDream.Core.Physics.Motion.ConstraintDistance.GetMaxConstraintDistance(anchor.ObjCellId));
    }

    private void SetPositionCore(
        Vector3 pos,
        uint cellId,
        Vector3 cellLocal,
        bool publishSharedState)
    {
        _body.SnapToCell(cellId, pos, cellLocal);
        _prevPhysicsPos = pos;
        _currPhysicsPos = pos;
        UpdateCellId(
            _body.CellPosition.ObjCellId,
            "teleport",
            publishSharedState);

        // Treat as grounded after a server-side position snap.
        _body.TransientState = TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable
            | TransientStateFlags.Active;
        _body.Velocity = Vector3.Zero;

        StopCompletelyAtPhysicsObjectBoundary();
        _activeInputTurnCommand = null;
        _activeInputTurnSpeed = 0f;
        _activeInputTurnFromMouse = false;
        _activeInputSidestepCommand = null;
        _activeInputSidestepUsesRunHold = false;
        _mouseLookActive = false;
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;
        _mouseMovementEventCandidate = false;
        _mouseMovementEventPending = false;
        if (publishSharedState)
        {
            PositionManager?.UnStick();
            PositionManager?.UnConstrain();
            RearmConstraintLeashAtCurrentPosition();
        }
        // Reset the edge tracker: the stop wiped the motion state, so keys
        // still physically held must re-fire as press edges on the next
        // Update (matches the pre-W6 level-triggered behavior of walking
        // straight out of a teleport while W stays held).
        _prevForwardHeld = false;
        _prevBackwardHeld = false;
        _prevStrafeLeftHeld = false;
        _prevStrafeRightHeld = false;
        _prevTurnLeftHeld = false;
        _prevTurnRightHeld = false;
        _prevRunHeld = false;
        _hasInputSnapshot = false;

        _body.LastUpdateTime = 0.0;
        _objectClock.ResetForEnterWorld();
    }

    internal void CommitCanonicalForcePositionFrame()
    {
        EnsurePublishedForRuntimeOperation();
        _prevPhysicsPos = _body.Position;
        _currPhysicsPos = _body.Position;
        UpdateCellId(_body.CellPosition.ObjCellId, "force-position");
    }

    internal void CommitCanonicalTeleportFrame(
        bool zeroVelocity,
        bool rearmConstraintLeash,
        bool runTeleportHookTail = true)
    {
        EnsurePublishedForRuntimeOperation();
        _prevPhysicsPos = _body.Position;
        _currPhysicsPos = _body.Position;
        UpdateCellId(_body.CellPosition.ObjCellId, "teleport");

        if (zeroVelocity)
            _body.Velocity = Vector3.Zero;
        StopCompletelyAtPhysicsObjectBoundary();
        _activeInputTurnCommand = null;
        _activeInputTurnSpeed = 0f;
        _activeInputTurnFromMouse = false;
        _activeInputSidestepCommand = null;
        _activeInputSidestepUsesRunHold = false;
        _mouseLookActive = false;
        _mouseTurnSamplePending = false;
        _mouseTurnAdjustment = 0f;
        _mouseMovementEventCandidate = false;
        _mouseMovementEventPending = false;

        if (runTeleportHookTail)
        {
            PositionManager?.UnStick();
            PositionManager?.UnConstrain();
            if (rearmConstraintLeash)
                RearmConstraintLeashAtCurrentPosition();
        }

        // Reset the edge tracker: the stop wiped the motion state, so keys
        // still physically held must re-fire as press edges on the next
        // Update (matches SetPositionCore's walking-straight-out-of-a-
        // teleport behavior while W stays held).
        _prevForwardHeld = false;
        _prevBackwardHeld = false;
        _prevStrafeLeftHeld = false;
        _prevStrafeRightHeld = false;
        _prevTurnLeftHeld = false;
        _prevTurnRightHeld = false;
        _prevRunHeld = false;
        _hasInputSnapshot = false;

        _body.LastUpdateTime = 0.0;
        _objectClock.ResetForEnterWorld();
    }

    private Vector3 ComputeRenderPosition()
    {
        float alpha = Math.Clamp(
            (float)(_objectClock.PendingSeconds / _lastQuantumSeconds),
            0f,
            1f);
        return Vector3.Lerp(_prevPhysicsPos, _currPhysicsPos, alpha);
    }

    public MovementResult TickHidden(float dt, Action? handleTargeting = null)
    {
        EnsurePublishedForRuntimeOperation();
        AdvancedObjectQuantumLastTick = false;
        PresentedDeltaSeconds = 0f;
        if (!float.IsFinite(dt) || dt <= 0f)
        {
            return CapturePresentationResult() with
            {
                RenderPosition = _body.Position,
                IsOnGround = _body.OnWalkable,
            };
        }

        _simTimeSeconds += dt;
        double pendingBeforeSeconds = _objectClock.PendingSeconds;
        bool reactivated = _objectClock.Activate();
        _body.TransientState |= TransientStateFlags.Active;
        RetailObjectQuantumBatch batch = reactivated
            ? default
            : _objectClock.Advance(dt);
        AdvancedObjectQuantumLastTick = batch.Count > 0;
        PresentedDeltaSeconds = ComputePresentedDelta(dt, pendingBeforeSeconds, in batch);
        if (batch.Discarded)
        {
            _prevPhysicsPos = _body.Position;
            _currPhysicsPos = _body.Position;
        }

        for (int qi = 0; qi < batch.Count; qi++)
        {
            float quantum = batch.GetQuantum(qi);
            Vector3 previousPosition = _body.Position;
            bool previousContact = _body.InContact;
            bool previousOnWalkable = _body.OnWalkable;

            if (PositionManager is { } manager)
            {
                var delta = _positionManagerDeltaScratch;
                delta.Reset();
                manager.AdjustOffset(delta, quantum);
                if (delta.Origin != Vector3.Zero)
                    _body.Position += Vector3.Transform(delta.Origin, _body.Orientation);
                if (!delta.Orientation.IsIdentity)
                {
                    _body.Orientation = AcDream.Core.Physics.Motion.FrameOps.SetRotate(
                        _body.Position,
                        _body.Orientation,
                        _body.Orientation * delta.Orientation);
                }
            }

            _processAnimationHooks?.Invoke();

            if (_body.Position != previousPosition && CellId != 0 && _physics.LandblockCount > 0)
            {
                ResolveResult resolved = _physics.ResolveWithTransition(
                    previousPosition,
                    _body.Position,
                    CellId,
                    sphereRadius: 0.48f,
                    sphereHeight: 1.835f,
                    stepUpHeight: StepUpHeight,
                    stepDownHeight: StepDownHeight,
                    isOnGround: previousOnWalkable,
                    body: _body,
                    moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide | OwnPvpFlags,
                    movingEntityId: LocalEntityId,
                    sphereList: SphereList,
                    sphereScale: ObjectScale);
                _body.CommitTransitionPosition(resolved.CellId, resolved.Position);
                PhysicsObjUpdate.CommitSetPositionTransition(
                    _body,
                    resolved.InContact,
                    resolved.OnWalkable,
                    resolved.CollisionNormalValid,
                    resolved.CollisionNormal,
                    previousContact,
                    previousOnWalkable,
                    Movement.HitGround,
                    _motion.LeaveGround);
                UpdateCellId(resolved.CellId, "hidden-position-manager");
            }

            RetailObjectManagerTail.Run(
                handleTargeting,
                Movement,
                _motion.CheckForCompletedMotions,
                PositionManager);
            _prevPhysicsPos = _body.Position;
            _currPhysicsPos = _body.Position;
        }

        _prevPhysicsPos = _body.Position;
        _currPhysicsPos = _body.Position;
        _wasAirborneLastFrame = !_body.OnWalkable;

        return new MovementResult(
            Position: _body.Position,
            RenderPosition: _body.Position,
            CellId: CellId,
            IsOnGround: _body.OnWalkable,
            MotionStateChanged: false,
            ForwardCommand: null,
            SidestepCommand: null,
            TurnCommand: null,
            ForwardSpeed: null,
            SidestepSpeed: null,
            TurnSpeed: null,
            CurrentStyle: _motion.RawState.CurrentStyle);
    }

    public MovementResult Update(
        float dt,
        MovementInput input,
        Action? handleTargeting = null)
    {
        EnsurePublishedForRuntimeOperation();
        AdvancedObjectQuantumLastTick = false;
        PresentedDeltaSeconds = 0f;
        if (!float.IsFinite(dt) || dt <= 0f)
            return CapturePresentationResult();

        _simTimeSeconds += dt;

        if (State == PlayerState.PortalSpace)
        {
            return new MovementResult(
                Position: Position,
                RenderPosition: RenderPosition,
                CellId: CellId,
                IsOnGround: _body.OnWalkable,
                MotionStateChanged: false,
                ForwardCommand: null,
                SidestepCommand: null,
                TurnCommand: null,
                ForwardSpeed: null,
                SidestepSpeed: null,
                TurnSpeed: null,
                CurrentStyle: _motion.RawState.CurrentStyle);
        }

        if (!_hasInputSnapshot)
        {
            _hasInputSnapshot = true;
            _prevRunHeld = input.Run;
            _prevRunHold = input.Run;
            _motion.set_hold_run(input.Run, interrupt: false);
        }

        bool externallyRequestedMovementEvent =
            _externalMovementEventPending;
        _externalMovementEventPending = false;
        RawMotionState? externalRawMotionState =
            _externalRawMotionStatePending;
        _externalRawMotionStatePending = null;
        bool motionEdgeFired = false;
        bool movementEventRequested =
            externallyRequestedMovementEvent;
        {
            bool persistentMovementHeld = input.IsPersistentCommand
                && (input.Forward
                    || input.Backward
                    || input.StrafeLeft
                    || input.StrafeRight
                    || input.TurnLeft
                    || input.TurnRight);
            if (_controlledByServer && persistentMovementHeld)
                TakeControlFromServer();

            bool userInputEdge = input.Run != _prevRunHeld
                || input.Forward != _prevForwardHeld
                || input.Backward != _prevBackwardHeld
                || input.StrafeLeft != _prevStrafeLeftHeld
                || input.StrafeRight != _prevStrafeRightHeld
                || input.TurnLeft != _prevTurnLeftHeld
                || input.TurnRight != _prevTurnRightHeld;
            if (userInputEdge)
                TakeControlFromServer();

            var p = new AcDream.Core.Physics.Motion.MovementParameters();

            if (input.Run != _prevRunHeld)
            {
                _motion.set_hold_run(input.Run, interrupt: true);
                motionEdgeFired = true;
            }

            if (input.Forward && !_prevForwardHeld)
            { DoMotionAtPhysicsObjectBoundary(MotionCommand.WalkForward, p); motionEdgeFired = true; }
            else if (input.Backward && !_prevBackwardHeld && !input.Forward)
            { DoMotionAtPhysicsObjectBoundary(MotionCommand.WalkBackward, p); motionEdgeFired = true; }
            if (!input.Forward && _prevForwardHeld)
            {
                if (input.Backward)
                    DoMotionAtPhysicsObjectBoundary(MotionCommand.WalkBackward, p);
                else
                    StopMotionAtPhysicsObjectBoundary(MotionCommand.WalkForward, p);
                motionEdgeFired = true;
            }
            else if (!input.Backward && _prevBackwardHeld && !input.Forward)
            { StopMotionAtPhysicsObjectBoundary(MotionCommand.WalkBackward, p); motionEdgeFired = true; }

            (uint? desiredSidestep, bool sidestepUsesRunHold) =
                DesiredInputSidestep(input, _mouseLookActive);
            if (ApplyInputSidestep(desiredSidestep, sidestepUsesRunHold))
                motionEdgeFired = true;

            movementEventRequested |= motionEdgeFired;

            bool keyboardTurnEdge = input.TurnRight != _prevTurnRightHeld
                                 || input.TurnLeft != _prevTurnLeftHeld;
            if (keyboardTurnEdge)
                movementEventRequested = true;

            uint? desiredTurnCommand = _mouseLookActive
                ? null
                : input.TurnRight
                    ? MotionCommand.TurnRight
                    : input.TurnLeft
                        ? MotionCommand.TurnLeft
                        : null;
            float desiredTurnSpeed = 1f;
            bool desiredTurnFromMouse = false;

            if (_mouseLookActive && _mouseTurnSamplePending)
            {
                float adjustment = _mouseTurnAdjustment;
                _mouseTurnSamplePending = false;
                _mouseTurnAdjustment = 0f;

                if (MathF.Abs(adjustment) >= MouseTurnDeadZone)
                {
                    desiredTurnCommand = adjustment < 0f
                        ? MotionCommand.TurnRight
                        : MotionCommand.TurnLeft;
                    desiredTurnSpeed = MathF.Min(
                        MathF.Abs(adjustment) * MouseTurnSpeedScale,
                        MouseTurnMaximumSpeed);
                    desiredTurnFromMouse = true;
                }
            }
            else if (_mouseLookActive && _activeInputTurnFromMouse)
            {
                desiredTurnCommand = _activeInputTurnCommand;
                desiredTurnSpeed = _activeInputTurnSpeed;
                desiredTurnFromMouse = true;
            }

            if (ApplyInputTurn(
                    desiredTurnCommand,
                    desiredTurnSpeed,
                    desiredTurnFromMouse))
                motionEdgeFired = true;

            if (motionEdgeFired)
            {
                _body.LastMoveWasAutonomous = true;
                _controlledByServer = false;
            }
        }

        _prevForwardHeld = input.Forward;
        _prevBackwardHeld = input.Backward;
        _prevStrafeLeftHeld = input.StrafeLeft;
        _prevStrafeRightHeld = input.StrafeRight;
        _prevTurnLeftHeld = input.TurnLeft;
        _prevTurnRightHeld = input.TurnRight;
        _prevRunHeld = input.Run;

        bool hasAnimationRootMotion = _advanceAnimationRootMotion is not null;

        float? outJumpExtent = null;
        Vector3? outJumpVelocity = null;

        if (input.Jump && !_jumpCharging && !_prevJumpHeld)
        {
            WeenieError chargeResult = _motion.ChargeJump();
            if (chargeResult == WeenieError.None)
            {
                _jumpCharging = true;
                _jumpExtent = 0f;
            }
            else
            {
                ReportJumpRefusal(chargeResult);
            }
        }

        if (input.Jump && _jumpCharging)
        {
            float chargeRate = _motion.InterpretedState.CurrentStyle
                == AcDream.Core.Combat.CombatInputPlanner.DualWieldCombatStyle
                    ? DualWieldJumpChargeRate
                    : JumpChargeRate;
            _jumpExtent = MathF.Min(_jumpExtent + dt * chargeRate, 1.0f);
        }
        else if (_jumpCharging)
        {
            var jumpResult = _motion.jump(_jumpExtent);
            if (jumpResult == WeenieError.None)
            {
                float jumpVz = _motion.GetJumpVZ();
                outJumpExtent = _jumpExtent;
                var jumpVel = _motion.get_state_velocity();
                outJumpVelocity = new Vector3(jumpVel.X, jumpVel.Y, jumpVz);

                _body.set_local_velocity(outJumpVelocity.Value, autonomous: true);
            }
            else
            {
                ReportJumpRefusal(jumpResult);
            }
            _jumpCharging = false;
            _jumpExtent = 0f;
        }

        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeJumpEnabled
            && (input.Jump || _prevJumpHeld))
        {
            Console.WriteLine(
                $"[jump-tick] input.Jump={input.Jump} prevJumpHeld={_prevJumpHeld} "
                + $"onWalkable={_body.OnWalkable} jumpCharging={_jumpCharging} "
                + $"jumpExtent={_jumpExtent:F2}");
        }
        _prevJumpHeld = input.Jump;

        double pendingBeforeSeconds = _objectClock.PendingSeconds;
        bool reactivated = _objectClock.Activate();
        _body.TransientState |= TransientStateFlags.Active;
        RetailObjectQuantumBatch quantumBatch = reactivated
            ? default
            : _objectClock.Advance(dt);
        AdvancedObjectQuantumLastTick = quantumBatch.Count > 0;
        PresentedDeltaSeconds =
            ComputePresentedDelta(dt, pendingBeforeSeconds, in quantumBatch);
        bool justLanded = false;
        if (quantumBatch.Discarded)
        {
            _prevPhysicsPos = _body.Position;
            _currPhysicsPos = _body.Position;
        }

        for (int qi = 0; qi < quantumBatch.Count; qi++)
        {
            float tickDt = quantumBatch.GetQuantum(qi);
            bool captureQuantum = PlayerPhysicsQuantumCapture.IsEnabled;
            uint captureCellBefore = CellId;
            PlayerPhysicsBodyTraceSnapshot captureQuantumStart = captureQuantum
                ? PlayerPhysicsQuantumCapture.Snapshot(_body)
                : default;

            var pmDelta = _positionManagerDeltaScratch;
            pmDelta.Reset();
            if (_advanceAnimationRootMotion is { } advanceRootMotion)
            {
                _animationRootMotionScratch.Reset();
                advanceRootMotion(tickDt, _animationRootMotionScratch);
                pmDelta.Origin = _body.OnWalkable
                    ? _animationRootMotionScratch.Origin * ObjectScale
                    : Vector3.Zero;
                pmDelta.Orientation = _animationRootMotionScratch.Orientation;
            }
            else if (_motion.InterpretedState.TurnCommand == MotionCommand.TurnRight)
            {
                Yaw -= 1.5f
                     * _motion.InterpretedState.TurnSpeed
                     * tickDt;
            }

            if (_body.OnWalkable && !hasAnimationRootMotion)
            {
                float savedWorldVz = _body.Velocity.Z;
                Vector3 stateVelocity = _motion.get_state_velocity();
                _body.set_local_velocity(
                    new Vector3(stateVelocity.X, stateVelocity.Y, savedWorldVz),
                    autonomous: _body.LastMoveWasAutonomous);
            }

            var preIntegratePos = _body.Position;
            Vector3 oldTickEndPos = _currPhysicsPos;

            PositionManager?.AdjustOffset(pmDelta, tickDt);
            _body.IsFullyConstrained = PositionManager?.IsFullyConstrained() ?? false;
            if (pmDelta.Origin != Vector3.Zero)
                _body.Position += Vector3.Transform(pmDelta.Origin, _body.Orientation);
            if (!pmDelta.Orientation.IsIdentity)
            {
                _body.Orientation = AcDream.Core.Physics.Motion.FrameOps.SetRotate(
                    _body.Position,
                    _body.Orientation,
                    _body.Orientation * pmDelta.Orientation);
            }

            _body.calc_acceleration();
            PlayerPhysicsBodyTraceSnapshot capturePreIntegration = captureQuantum
                ? PlayerPhysicsQuantumCapture.Snapshot(_body)
                : default;
            _body.UpdatePhysicsInternal(tickDt);
            PlayerPhysicsBodyTraceSnapshot capturePostIntegration = captureQuantum
                ? PlayerPhysicsQuantumCapture.Snapshot(_body)
                : default;

            _processAnimationHooks?.Invoke();
            var postIntegratePos = _body.Position;

            bool candidateMoved = postIntegratePos != preIntegratePos;

            var resolveResult = _physics.ResolveWithTransition(
                preIntegratePos, postIntegratePos, CellId,
                sphereRadius: 0.48f,
                sphereHeight: 1.835f,
                stepUpHeight: StepUpHeight,
                stepDownHeight: StepDownHeight,  // L.2.3a: from Setup.StepDownHeight
                isOnGround: _body.OnWalkable,
                body: _body,
                moverFlags: AcDream.Core.Physics.ObjectInfoState.IsPlayer
                          | AcDream.Core.Physics.ObjectInfoState.EdgeSlide
                          | OwnPvpFlags,
                movingEntityId: LocalEntityId,
                sphereList: SphereList,
                sphereScale: ObjectScale);

            if (AcDream.Core.Physics.PhysicsDiagnostics.DumpSteepRoofEnabled
                && resolveResult.CollisionNormalValid)
            {
                Console.WriteLine(
                    $"[steep-roof] FRAME pre=({preIntegratePos.X:F2},{preIntegratePos.Y:F2},{preIntegratePos.Z:F2}) " +
                    $"post=({postIntegratePos.X:F2},{postIntegratePos.Y:F2},{postIntegratePos.Z:F2}) " +
                    $"resolved=({resolveResult.Position.X:F2},{resolveResult.Position.Y:F2},{resolveResult.Position.Z:F2}) " +
                    $"isOnGround={resolveResult.IsOnGround}");
            }


            _body.CachedVelocity = candidateMoved
                ? (resolveResult.Position - preIntegratePos) / tickDt
                : Vector3.Zero;

            bool prevContact = _body.InContact;
            bool prevOnWalkable = _body.OnWalkable;

            _body.CommitTransitionPosition(resolveResult.CellId, resolveResult.Position);
            _prevPhysicsPos = oldTickEndPos;
            _currPhysicsPos = _body.Position;

            bool landedThisQuantum = false;
            if (resolveResult.Ok && candidateMoved)
            {
                if (resolveResult.InContact)
                    _body.TransientState |= TransientStateFlags.Contact;
                else
                    _body.TransientState &= ~TransientStateFlags.Contact;
                _body.calc_acceleration();

                if (resolveResult.InContact && resolveResult.OnWalkable)
                {
                    bool wasAirborne = !_body.OnWalkable;
                    _body.TransientState |= TransientStateFlags.OnWalkable;
                    if (wasAirborne)
                    {
                        Movement.HitGround();
                        landedThisQuantum = true;
                    }
                }
                else
                {
                    _body.TransientState &= ~TransientStateFlags.OnWalkable;
                }
                _body.calc_acceleration();          // pc:283475/283490 (set_on_walkable tail)

                PhysicsObjUpdate.HandleAllCollisions(
                    _body,
                    resolveResult.CollisionNormalValid, resolveResult.CollisionNormal,
                    prevContact, prevOnWalkable, nowOnWalkable: _body.OnWalkable);
            }

            if (!_body.OnWalkable && !_wasAirborneLastFrame)
                _motion.LeaveGround();

            _wasAirborneLastFrame = !_body.OnWalkable;
            justLanded |= landedThisQuantum;
            UpdateCellId(resolveResult.CellId, "resolver");

            RetailObjectManagerTail.Run(
                handleTargeting,
                Movement,
                _motion.CheckForCompletedMotions,
                PositionManager);

            if (captureQuantum)
            {
                PlayerPhysicsQuantumCapture.Log(
                    tickDt,
                    captureCellBefore,
                    CellId,
                    input,
                    capturePreIntegration.Position - captureQuantumStart.Position,
                    candidateMoved,
                    captureQuantumStart,
                    capturePreIntegration,
                    capturePostIntegration,
                    new PlayerPhysicsResolveTraceSnapshot(
                        resolveResult.Position,
                        resolveResult.CellId,
                        resolveResult.Ok,
                        resolveResult.IsOnGround,
                        resolveResult.InContact,
                        resolveResult.OnWalkable,
                        resolveResult.CollisionNormalValid,
                        resolveResult.CollisionNormal),
                    PlayerPhysicsQuantumCapture.Snapshot(_body));
            }

        }

        uint? outForwardCmd = null;
        float? outForwardSpeed = null;
        uint? outSidestepCmd = null;
        float? outSidestepSpeed = null;
        uint? outTurnCmd = null;
        float? outTurnSpeed = null;

        if (input.Forward)
        {
            outForwardCmd = MotionCommand.WalkForward;
            outForwardSpeed = 1.0f;
        }
        else if (input.Backward)
        {
            outForwardCmd = MotionCommand.WalkBackward;
            outForwardSpeed = 1.0f;
        }
        else if (_motion.RawState.ForwardCommand is (
            MotionCommand.Crouch
            or MotionCommand.Sitting
            or MotionCommand.Sleeping))
        {
            outForwardCmd = _motion.RawState.ForwardCommand;
            outForwardSpeed = _motion.RawState.ForwardSpeed;
        }

        if (_activeInputSidestepCommand is { } activeInputSidestep)
        {
            outSidestepCmd = activeInputSidestep;
            outSidestepSpeed = _motion.RawState.SidestepSpeed;
        }

        if (_activeInputTurnCommand is { } activeInputTurn)
        {
            outTurnCmd = activeInputTurn;
            outTurnSpeed = _activeInputTurnSpeed;
        }

        bool runHold = input.Run;
        bool changed = outForwardCmd != _prevForwardCmd
                    || outSidestepCmd != _prevSidestepCmd
                    || outTurnCmd != _prevTurnCmd
                    || !FloatsEqual(outForwardSpeed, _prevForwardSpeed)
                    || runHold != _prevRunHold
                    || motionEdgeFired
                    || externallyRequestedMovementEvent;

        bool mouseMovementEventDue = _mouseMovementEventPending
            || (_mouseMovementEventCandidate
                && _simTimeSeconds > _lastMouseMovementEventTime + MouseMovementEventInterval);
        _mouseMovementEventCandidate = false;
        if (mouseMovementEventDue)
            _mouseMovementEventPending = true;
        bool shouldSendMovementEvent = movementEventRequested || mouseMovementEventDue;

        _prevForwardCmd = outForwardCmd;
        _prevSidestepCmd = outSidestepCmd;
        _prevTurnCmd = outTurnCmd;
        _prevForwardSpeed = outForwardSpeed;
        _prevRunHold = runHold;

        static bool FloatsEqual(float? a, float? b)
        {
            if (a.HasValue != b.HasValue) return false;
            if (!a.HasValue || !b.HasValue) return true;
            return System.Math.Abs(a.Value - b.Value) < 1e-4f;
        }


        return new MovementResult(
            Position: Position,
            RenderPosition: RenderPosition,
            CellId: CellId,
            IsOnGround: _body.OnWalkable,
            MotionStateChanged: changed,
            ForwardCommand: outForwardCmd,
            SidestepCommand: outSidestepCmd,
            TurnCommand: outTurnCmd,
            ForwardSpeed: outForwardSpeed,
            SidestepSpeed: outSidestepSpeed,
            TurnSpeed: outTurnSpeed,
            IsRunning: input.Run,
            JustLanded: justLanded,
            JumpExtent: outJumpExtent,
            JumpVelocity: outJumpVelocity,
            ShouldSendMovementEvent: shouldSendMovementEvent,
            TurnUsesRunHold: _activeInputTurnFromMouse && outTurnCmd.HasValue,
            SidestepUsesRunHold: _activeInputSidestepUsesRunHold
                && outSidestepCmd.HasValue,
            IsMouseLookMovementEvent: mouseMovementEventDue,
            CurrentStyle: _motion.RawState.CurrentStyle,
            RawMotionStateOverride: externalRawMotionState);
    }

    private static bool ApproxFrameEqual(CellFrame a, CellFrame b)
    {
        const float Epsilon = 0.000199999995f;
        return MathF.Abs(a.Origin.X - b.Origin.X) <= Epsilon
            && MathF.Abs(a.Origin.Y - b.Origin.Y) <= Epsilon
            && MathF.Abs(a.Origin.Z - b.Origin.Z) <= Epsilon
            && MathF.Abs(a.Orientation.W - b.Orientation.W) < Epsilon
            && MathF.Abs(a.Orientation.X - b.Orientation.X) < Epsilon
            && MathF.Abs(a.Orientation.Y - b.Orientation.Y) < Epsilon
            && MathF.Abs(a.Orientation.Z - b.Orientation.Z) < Epsilon;
    }

    private static bool ApproxPlaneEqual(
        System.Numerics.Plane a, System.Numerics.Plane b)
    {
        const float Epsilon = 0.000199999995f;
        return MathF.Abs(a.Normal.X - b.Normal.X) <= Epsilon
            && MathF.Abs(a.Normal.Y - b.Normal.Y) <= Epsilon
            && MathF.Abs(a.Normal.Z - b.Normal.Z) <= Epsilon
            && MathF.Abs(a.D - b.D) < Epsilon;
    }
}
