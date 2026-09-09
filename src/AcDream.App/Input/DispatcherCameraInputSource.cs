using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal readonly record struct FlyCameraInput(
    bool Forward,
    bool Left,
    bool Backward,
    bool Right,
    bool Up,
    bool Down,
    bool Boost);

internal readonly record struct ChaseCameraAdjustmentInput(
    bool ZoomIn,
    bool ZoomOut,
    bool Raise,
    bool Lower,
    bool RotateLeft,
    bool RotateRight);

internal interface ICameraFrameInputSource
{
    bool IsAvailable { get; }
    FlyCameraInput CaptureFly();
    ChaseCameraAdjustmentInput CaptureChaseAdjustment();
}

internal sealed class DispatcherCameraInputSource : ICameraFrameInputSource
{
    private InputDispatcher? _dispatcher;

    public bool IsAvailable => _dispatcher is not null;

    public void Bind(InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, dispatcher))
            throw new InvalidOperationException(
                "The camera input source is already bound to another dispatcher.");
        _dispatcher = dispatcher;
    }

    public void Unbind(InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (ReferenceEquals(_dispatcher, dispatcher))
            _dispatcher = null;
    }

    public FlyCameraInput CaptureFly()
    {
        if (_dispatcher is not { } dispatcher)
            return default;

        return new FlyCameraInput(
            dispatcher.IsActionHeld(InputAction.MovementForward),
            dispatcher.IsActionHeld(InputAction.MovementTurnLeft),
            dispatcher.IsActionHeld(InputAction.MovementBackup),
            dispatcher.IsActionHeld(InputAction.MovementTurnRight),
            dispatcher.IsActionHeld(InputAction.MovementJump),
            dispatcher.IsActionHeld(InputAction.AcdreamFlyDown),
            dispatcher.IsActionHeld(InputAction.MovementRunLock));
    }

    public ChaseCameraAdjustmentInput CaptureChaseAdjustment()
    {
        if (_dispatcher is not { } dispatcher)
            return default;

        return new ChaseCameraAdjustmentInput(
            dispatcher.IsActionHeld(InputAction.CameraZoomIn)
                || dispatcher.IsActionHeld(InputAction.CameraMoveToward)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateMoveToward),
            dispatcher.IsActionHeld(InputAction.CameraZoomOut)
                || dispatcher.IsActionHeld(InputAction.CameraMoveAway)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateMoveAway),
            dispatcher.IsActionHeld(InputAction.CameraRaise)
                || dispatcher.IsActionHeld(InputAction.CameraRotateUp)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateRotateUp),
            dispatcher.IsActionHeld(InputAction.CameraLower)
                || dispatcher.IsActionHeld(InputAction.CameraRotateDown)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateRotateDown),
            dispatcher.IsActionHeld(InputAction.CameraRotateLeft)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateRotateLeft),
            dispatcher.IsActionHeld(InputAction.CameraRotateRight)
                || dispatcher.IsActionHeld(InputAction.CameraAlternateRotateRight));
    }
}
