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
                    _commandInput)
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
                    _commandInput);
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
        Interlocked.Increment(ref _revision);
    }

    public void ResetInputIntent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_autoRunActive && !_hasCommandInput && !_commandInterpreterDisabled)
            return;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInterpreterDisabled = false;
        _commandInput = default;
        Interlocked.Increment(ref _revision);
    }

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _physicsPublication?.ResetSession();
        bool changed =
            _autoRunActive
            || _hasCommandInput
            || _commandInterpreterDisabled
            || _controller is not null
            || _preparingMotionOwner is not null;
        _autoRunActive = false;
        _hasCommandInput = false;
        _commandInterpreterDisabled = false;
        _commandInput = default;
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
