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
    private RuntimeApproachCompletionState? _approachCompletions;
    private IRuntimeApproachCompletionSink? _approachLifetime;

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
            // The body walking is changing, so whatever was armed for the
            // walk it was on can no longer be about this one.
            RetireApproachLifetime();
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
                // The stop key puts the forward axis back to ready, as the
                // Ready posture does. Held keys stay held: a forward key
                // still down moves again only once pressed again, and a
                // jump being charged keeps charging.
                CancelAutoRun();
                ClearCommandInput();
                _ = _controller?.RequestPosture(MotionCommand.Ready);
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

    /// <summary>
    /// Whether the character's body is in position for <paramref name="mode"/>,
    /// by the original client's rule: a stance still being taken up is not
    /// ready for a mode change, and a melee or missile stance not yet held is
    /// not ready for an attack. False with no body to ask.
    /// </summary>
    public bool IsInReadyPosition(CombatMode mode, bool lenient, bool hasCombatTable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controller is not { } controller)
            return false;
        var motion = controller.Motion.InterpretedState;
        return CombatInputPlanner.PlayerInReadyPosition(
            mode,
            motion.CurrentStyle,
            motion.ForwardCommand,
            hasCombatTable,
            controller.Motion.MotionsPending(),
            lenient);
    }

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
        RetireApproachLifetime();
        _approachCompletions?.Clear();
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
        RetireApproachLifetime();
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
        BeginApproachLifetime();
        ControllerOwnershipEpoch++;
        Interlocked.Increment(ref _revision);
    }

    /// <summary>
    /// Names where walks sent to reach something report that they arrived or
    /// were called off. Bound once, by the runtime that owns both.
    /// </summary>
    internal void AttachApproachCompletions(
        RuntimeApproachCompletionState completions)
    {
        ArgumentNullException.ThrowIfNull(completions);
        if (_approachCompletions is not null)
        {
            throw new InvalidOperationException(
                "The approach-completion owner is already bound.");
        }
        _approachCompletions = completions;
    }

    /// <summary>
    /// Starts the run of walks belonging to a body the character has just
    /// taken, and points that body's walk at it. Every client does this here,
    /// so a walk-to-then-act works the same with or without a window.
    /// </summary>
    private void BeginApproachLifetime()
    {
        RetireApproachLifetime();
        if (_approachCompletions is not { } completions)
            return;
        _approachLifetime = completions.BeginControllerLifetime();
    }

    /// <summary>
    /// Points a body's walks at the character's current run of walks. Called
    /// while the body is still being built, because a committed one can no
    /// longer be rewired; the answers themselves are matched to whichever run
    /// is current when the walk ends, so a body built and then discarded
    /// reports nothing.
    /// </summary>
    internal void WireApproachCompletions(
        AcDream.Core.Physics.Motion.MoveToManager? moveTo)
    {
        if (moveTo is null)
            return;
        moveTo.MoveToComplete = error =>
        {
            if (error == WeenieError.None)
                _approachLifetime?.PublishNaturalCompletion();
            else
                _approachLifetime?.PublishCancellation(error);
        };
        moveTo.MoveToCancelled = error =>
            _approachLifetime?.PublishCancellation(error);
    }

    /// <summary>Ends that run when the body goes.</summary>
    private void RetireApproachLifetime()
    {
        if (_approachLifetime is not { } lifetime)
            return;
        _approachLifetime = null;
        _approachCompletions?.RetireControllerLifetime(lifetime);
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
