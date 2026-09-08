using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal interface IMovementInputSource
    : IRuntimeMovementInputSource
{
}

internal sealed class DispatcherMovementInputSource : IMovementInputSource
{
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly IInputCaptureSource? _capture;
    private InputDispatcher? _dispatcher;

    public DispatcherMovementInputSource(
        RuntimeLocalPlayerMovementState movement,
        IInputCaptureSource? capture = null)
    {
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _capture = capture;
    }

    public bool AutoRunActive => _movement.AutoRunActive;
    public bool IsAvailable => _dispatcher is not null;

    public void Bind(InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, dispatcher))
            throw new InvalidOperationException(
                "The movement input source is already bound to another dispatcher.");
        _dispatcher = dispatcher;
    }

    public void Unbind(InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (ReferenceEquals(_dispatcher, dispatcher))
            _dispatcher = null;
    }

    public MovementInput Capture()
    {
        if (_movement.CommandInterpreterDisabled)
            return default;

        if (_capture?.DevToolsWantCaptureKeyboard == true)
            return default;

        if (_movement.HasCommandInput)
            return _movement.CommandInput with { IsPersistentCommand = true };

        if (_dispatcher is not { } dispatcher)
            return default;

        bool walking = dispatcher.IsActionHeld(InputAction.MovementWalkMode);
        bool forward = dispatcher.IsActionHeld(InputAction.MovementForward);
        return new MovementInput(
            Forward: forward || AutoRunActive,
            Backward: dispatcher.IsActionHeld(InputAction.MovementBackup),
            StrafeLeft: dispatcher.IsActionHeld(InputAction.MovementStrafeLeft),
            StrafeRight: dispatcher.IsActionHeld(InputAction.MovementStrafeRight),
            TurnLeft: dispatcher.IsActionHeld(InputAction.MovementTurnLeft),
            TurnRight: dispatcher.IsActionHeld(InputAction.MovementTurnRight),
            Run: (_movement.RunAsDefaultMovement != walking) || AutoRunActive,
            Jump: dispatcher.IsActionHeld(InputAction.MovementJump));
    }

    public bool HandlePressedAction(InputAction action)
    {
        if (action == InputAction.MovementRunLock)
            return _movement.Execute(
                AcDream.Runtime.RuntimeMovementCommand.ToggleRunLock);

        if (AutoRunActive && action is (
            InputAction.MovementForward
            or InputAction.MovementBackup
            or InputAction.MovementStop
            or InputAction.MovementStrafeLeft
            or InputAction.MovementStrafeRight))
        {
            _movement.CancelAutoRun();
        }

        return false;
    }

    public void ResetSession() => _movement.ResetInputIntent();
}
