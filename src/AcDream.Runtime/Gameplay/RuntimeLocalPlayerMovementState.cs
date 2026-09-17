using System.Diagnostics;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

public interface IRuntimeLocalPlayerControllerSource
{
    PlayerMovementController? Controller { get; }
}

public interface IRuntimeLocalPlayerMotionSource
{
    MotionInterpreter? Motion { get; }
}

public enum RuntimeMovementStatsApplication
{
    AppliedLive,

    AppliedDormant,

    DroppedNoController,

    /// <summary>The skill snapshot has no authoritative run/jump values yet
    /// (PlayerDescription not processed) — same silent skip as the pre-F1
    /// path.</summary>
    DroppedIncompleteSnapshot,

    DroppedDisplacedController,
}

public enum RuntimeServerPhysicsStateApplication
{
    AppliedLive,

    DroppedDormantActivationOwned,

    DroppedDisplacedController,
}

public sealed class RuntimeLocalPlayerMovementState
    : IRuntimeLocalPlayerControllerSource,
      IRuntimeLocalPlayerMotionSource,
      IRuntimeMovementView,
      IDisposable
{
    private PlayerMovementController? _controller;
    private PlayerMovementController? _preparingMotionOwner;
    private RuntimeLocalPlayerPhysicsPublicationState? _physicsPublication;
    private bool _autoRunActive;
    private bool _hasCommandInput;
    private bool _commandInterpreterDisabled;
    private MovementInput _commandInput;
    private readonly RuntimeScriptedMovement _scripted = new();
    private long _playerMovementInputFrames;
    private Position _scriptStart;
    private bool _scriptStartKnown;
    private bool _disposed;
    private long _revision;
    private Action<string, RetailLogTextType>? _onInterfaceText;

    public Action<string, RetailLogTextType>? OnInterfaceText
    {
        get => _onInterfaceText;
        set
        {
            _onInterfaceText = value;
            if (_controller is not null)
                _controller.OnInterfaceText = value;
        }
    }

    public PlayerMovementController? Controller
    {
        get => _controller;
        internal set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_controller, value))
                return;
            _controller?.RetireRuntimePublication();
            _scripted.Lose();
            _controller = value;
            if (_controller is not null)
                _controller.OnInterfaceText = _onInterfaceText;
            ControllerOwnershipEpoch++;
            Interlocked.Increment(ref _revision);
        }
    }

    public bool AutoRunActive => _autoRunActive;

    public Func<bool>? RunAsDefaultMovementSource { get; set; }

    public bool RunAsDefaultMovement => RunAsDefaultMovementSource?.Invoke() ?? true;

    public bool HasCommandInput => _hasCommandInput;
    public MovementInput CommandInput => _commandInput;
    public long Revision => Interlocked.Read(ref _revision);
    public ulong ControllerOwnershipEpoch { get; private set; }
    public IRuntimeMovementView View => this;
    public bool IsStandingStill => _controller?.IsStandingStill ?? true;
    public JumpChargeSnapshot JumpCharge => _controller?.JumpCharge ?? default;

    internal RuntimeLocalPlayerPhysicsPublicationState PhysicsPublication =>
        _physicsPublication ?? throw new InvalidOperationException(
            "The Runtime local-player physics publication owner is not bound.");

    MotionInterpreter? IRuntimeLocalPlayerMotionSource.Motion =>
        _preparingMotionOwner?.Motion ?? _controller?.Motion;

    public RuntimeMovementSnapshot Snapshot
    {
        get
        {
            PlayerMovementController? controller = _controller;
            return controller is null
                ? new RuntimeMovementSnapshot(
                    false,
                    0u,
                    default,
                    default,
                    false,
                    0d,
                    Revision,
                    _autoRunActive,
                    _hasCommandInput,
                    _commandInput,
                    _scripted.Snapshot)
                : new RuntimeMovementSnapshot(
                    true,
                    controller.LocalEntityId,
                    controller.CurrentCellPosition,
                    controller.BodyVelocity,
                    controller.IsAirborne,
                    controller.SimTimeSeconds,
                    Revision,
                    _autoRunActive,
                    _hasCommandInput,
                    _commandInput,
                    _scripted.Snapshot);
        }
    }

    public IDisposable BeginMotionPreparation(
        PlayerMovementController controller,
        Action? drainPriorAnimationQueue = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(controller);
        if (_preparingMotionOwner is not null)
        {
            throw new InvalidOperationException(
                "A local player motion owner is already being prepared.");
        }

        drainPriorAnimationQueue?.Invoke();
        _preparingMotionOwner = controller;
        return new MotionPreparation(this, controller);
    }

    public bool Execute(RuntimeMovementCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (command)
        {
            case RuntimeMovementCommand.ToggleRunLock:
                _autoRunActive = !_autoRunActive;
                Interlocked.Increment(ref _revision);
                return true;
            case RuntimeMovementCommand.Stop:
                CancelAutoRun();
                ClearCommandInput();
                return true;
            case RuntimeMovementCommand.StopCompletely:
                CancelAutoRun();
                ClearCommandInput();
                _ = _controller?.StopCompletelyAtPhysicsObjectBoundary();
                Interlocked.Increment(ref _revision);
                return true;
            case RuntimeMovementCommand.FinishJump:
                _controller?.FinishJump();
                Interlocked.Increment(ref _revision);
                return true;
            case RuntimeMovementCommand.Ready:
            case RuntimeMovementCommand.Sit:
            case RuntimeMovementCommand.Crouch:
            case RuntimeMovementCommand.Sleep:
                CancelAutoRun();
                ClearCommandInput();
                uint motion = command switch
                {
                    RuntimeMovementCommand.Ready =>
                        MotionCommand.Ready,
                    RuntimeMovementCommand.Sit =>
                        MotionCommand.Sitting,
                    RuntimeMovementCommand.Crouch =>
                        MotionCommand.Crouch,
                    RuntimeMovementCommand.Sleep =>
                        MotionCommand.Sleeping,
                    _ => throw new UnreachableException(),
                };
                return _controller?.RequestPosture(motion) == true;
            default:
                return false;
        }
    }

    public bool ExecuteMotion(uint motionCommand)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _controller?.RequestCommandMotion(motionCommand) == true;
    }

    public bool TurnToHeading(
        float headingDegrees,
        bool applyRunHoldKey = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(headingDegrees))
            return false;
        float normalized = headingDegrees % 360f;
        if (normalized < 0f)
            normalized += 360f;
        return _controller?.RequestTurnToHeading(normalized, applyRunHoldKey)
            == true;
    }

    /// <summary>The most recent scripted move on each channel, and the most recent jump.</summary>
    public RuntimeScriptedMoveSnapshot ScriptedMove => _scripted.Snapshot;

    /// <summary>
    /// How many frames the player's own movement input has asked the character to move,
    /// whether or not a scripted move was running: a walk that sees it grow knows the player
    /// took the character. A plugin's held intent does not count.
    /// </summary>
    public long PlayerMovementInputFrames => Interlocked.Read(ref _playerMovementInputFrames);

    /// <summary>
    /// Begins a scripted move on its channel, replacing only a move already on
    /// that channel. The player's own movement ends every scripted move.
    /// </summary>
    public bool BeginMove(in RuntimeMoveRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controller is null || _commandInterpreterDisabled || !_scripted.Begin(request))
            return false;
        CancelAutoRun();
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>Ends every scripted move in progress.</summary>
    public bool StopMove()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_scripted.Stop())
            return false;
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>Ends the scripted move on one channel, if there is one.</summary>
    public bool StopMove(RuntimeMoveChannel channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_scripted.Stop(channel))
            return false;
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>
    /// Charges a jump for part of a full charge, from 0 to 1, then releases it; with a pace to
    /// leave at, the body charges standing and presses forward at that pace as the jump releases.
    /// </summary>
    public bool BeginJump(float power, RuntimeMovePace? leaveAt = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controller is null || _commandInterpreterDisabled || !_scripted.BeginJump(power, leaveAt))
            return false;
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>Whether a frame's input is the player's own asking the character to move or turn, rather than a plugin's held intent.</summary>
    private static bool IsPlayerMovement(in MovementInput frameInput) =>
        !frameInput.IsPersistentCommand
        && (frameInput.Forward
            || frameInput.Backward
            || frameInput.StrafeLeft
            || frameInput.StrafeRight
            || frameInput.TurnLeft
            || frameInput.TurnRight);

    /// <summary>
    /// Advances any scripted moves or jump by one frame and returns the input
    /// the body should act on instead of <paramref name="frameInput"/>, or
    /// <see langword="null"/> when nothing scripted is in progress. A turn that
    /// has just passed its angle sets the body's heading to land on it. The
    /// player's own movement input is counted either way.
    /// </summary>
    internal MovementInput? CaptureScriptedInput(in MovementInput frameInput)
    {
        if (!_disposed && IsPlayerMovement(frameInput))
            Interlocked.Increment(ref _playerMovementInputFrames);
        if (_disposed || !_scripted.IsActive)
            return null;
        RuntimeScriptedMoveSnapshot before = _scripted.Snapshot;
        MovementInput? input;
        if (_controller is not { } controller)
        {
            _scripted.Lose();
            input = null;
        }
        else
        {
            Position now = controller.CurrentCellPosition;
            if (!_scriptStartKnown)
            {
                _scriptStart = now;
                _scriptStartKnown = true;
            }
            System.Numerics.Vector3 offset =
                LandDefs.GetBlockOffset(_scriptStart.ObjCellId, now.ObjCellId)
                + now.Frame.Origin
                - _scriptStart.Frame.Origin;
            bool manual = IsPlayerMovement(frameInput);
            input = _scripted.Advance(
                new RuntimeScriptedMoveSample(
                    offset,
                    AcDream.Core.Physics.Motion.MoveToMath.HeadingFromYaw(controller.Yaw),
                    controller.SimTimeSeconds,
                    controller.State == PlayerState.PortalSpace,
                    manual),
                out float? heading);
            if (heading is { } landed && controller.CanExecuteLiveMovement)
                controller.Yaw = AcDream.Core.Physics.Motion.MoveToMath.YawFromHeading(landed);
        }
        if (!_scripted.IsActive)
            _scriptStartKnown = false;
        if (Changed(before, _scripted.Snapshot))
            Interlocked.Increment(ref _revision);
        return input;
    }

    private static bool Changed(in RuntimeScriptedMoveSnapshot before, in RuntimeScriptedMoveSnapshot after) =>
        Changed(before.Travel, after.Travel)
        || Changed(before.Strafe, after.Strafe)
        || Changed(before.Turn, after.Turn)
        || before.JumpCharging != after.JumpCharging;

    private static bool Changed(in RuntimeMoveChannelSnapshot before, in RuntimeMoveChannelSnapshot after) =>
        before.State != after.State || before.Sequence != after.Sequence;

    public bool CancelAutoRun()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_autoRunActive)
            return false;
        _autoRunActive = false;
        Interlocked.Increment(ref _revision);
        return true;
    }

    public void SetCommandInput(in MovementInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasCommandInput && _commandInput == input)
            return;
        _commandInput = input;
        _hasCommandInput = true;
        Interlocked.Increment(ref _revision);
    }

    public bool ClearCommandInput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasCommandInput)
            return false;
        _commandInput = default;
        _hasCommandInput = false;
        Interlocked.Increment(ref _revision);
        return true;
    }

    public RuntimeMovementStatsApplication ApplyCharacterMovementStats(
        RuntimeMovementSkillState skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (_controller is not { } controller)
            return RuntimeMovementStatsApplication.DroppedNoController;
        RuntimeMovementSkillSnapshot snapshot = skills.Snapshot;
        if (!snapshot.IsComplete)
            return RuntimeMovementStatsApplication.DroppedIncompleteSnapshot;
        return controller.ApplyCharacterMovementStats(snapshot);
    }

    public bool ReportExhaustion() =>
        _controller?.ReportExhaustionAtMovementBoundary() == true;

    public bool IsReadyForAttack(CombatMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controller is not { } controller)
            return false;
        var motion = controller.Motion.InterpretedState;
        return CombatInputPlanner.PlayerInReadyPositionForAttack(
            mode,
            motion.CurrentStyle,
            motion.ForwardCommand);
    }

    public bool IsDualWield
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _controller?.Motion.InterpretedState.CurrentStyle
                == CombatInputPlanner.DualWieldCombatStyle;
        }
    }

    public bool PrepareForAttackRequest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelAutoRun();
        return _controller?.PrepareForAttackRequest() == true;
    }

    public bool CommandInterpreterDisabled => _commandInterpreterDisabled;

    public void DisableCommandInterpreter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_commandInterpreterDisabled)
            return;
        _commandInterpreterDisabled = true;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInput = default;
        _scripted.Lose();
        Interlocked.Increment(ref _revision);
    }

    public void ResetInputIntent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_autoRunActive && !_hasCommandInput && !_commandInterpreterDisabled && !_scripted.IsActive)
            return;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInterpreterDisabled = false;
        _commandInput = default;
        _scripted.Lose();
        Interlocked.Increment(ref _revision);
    }

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _physicsPublication?.ResetSession();
        bool changed =
            _scripted.IsActive
            || _autoRunActive
            || _hasCommandInput
            || _commandInterpreterDisabled
            || _controller is not null
            || _preparingMotionOwner is not null;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInterpreterDisabled = false;
        _commandInput = default;
        _scripted.Lose();
        if (_controller is not null)
        {
            _controller.RetireRuntimePublication();
            _controller = null;
            ControllerOwnershipEpoch++;
        }
        _preparingMotionOwner = null;
        if (changed)
            Interlocked.Increment(ref _revision);
    }

    public RuntimeLocalMovementOwnershipSnapshot CaptureOwnership() =>
        new(
            _disposed,
            _controller is not null,
            _preparingMotionOwner is not null,
            _autoRunActive,
            _hasCommandInput,
            Revision,
            ControllerOwnershipEpoch,
            _physicsPublication?.CaptureOwnership() ?? default);

    public void Dispose()
    {
        if (_disposed)
            return;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInput = default;
        _scripted.Lose();
        _physicsPublication?.Dispose();
        if (_controller is not null)
        {
            _controller.RetireRuntimePublication();
            _controller = null;
            ControllerOwnershipEpoch++;
        }
        _preparingMotionOwner = null;
        Interlocked.Increment(ref _revision);
        _disposed = true;
    }

    internal void AttachPhysicsPublication(
        RuntimeLocalPlayerPhysicsPublicationState publication)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publication);
        if (_physicsPublication is not null)
        {
            throw new InvalidOperationException(
                "The Runtime local-player physics publication owner is already bound.");
        }
        _physicsPublication = publication;
    }

    internal bool CanCommitRuntimeOwnedController(
        ulong expectedEpoch,
        PlayerMovementController? expectedController) =>
        !_disposed
        && ControllerOwnershipEpoch == expectedEpoch
        && ReferenceEquals(_controller, expectedController);

    internal void CommitRuntimeOwnedController(PlayerMovementController controller)
    {
        _controller?.RetireRuntimePublication();
        _scripted.Lose();
        _controller = controller;
        controller.OnInterfaceText = _onInterfaceText;
        ControllerOwnershipEpoch++;
        Interlocked.Increment(ref _revision);
    }

    private void EndMotionPreparation(PlayerMovementController controller)
    {
        if (_disposed && _preparingMotionOwner is null)
            return;

        if (!ReferenceEquals(_preparingMotionOwner, controller))
        {
            throw new InvalidOperationException(
                "The local player motion preparation owner changed unexpectedly.");
        }
        _preparingMotionOwner = null;
    }

    private sealed class MotionPreparation(
        RuntimeLocalPlayerMovementState owner,
        PlayerMovementController controller) : IDisposable
    {
        private RuntimeLocalPlayerMovementState? _owner = owner;

        public void Dispose()
        {
            RuntimeLocalPlayerMovementState? current =
                Interlocked.Exchange(ref _owner, null);
            current?.EndMotionPreparation(controller);
        }
    }
}

public readonly record struct RuntimeLocalMovementOwnershipSnapshot(
    bool IsDisposed,
    bool HasController,
    bool HasPreparingMotionOwner,
    bool AutoRunActive,
    bool HasCommandInput,
    long Revision,
    ulong ControllerOwnershipEpoch,
    RuntimeLocalPlayerPhysicsPublicationOwnershipSnapshot PhysicsPublication)
{
    public bool IsConverged =>
        IsDisposed
        && !HasController
        && !HasPreparingMotionOwner
        && !AutoRunActive
        && !HasCommandInput
        && PhysicsPublication.IsConverged;
}
