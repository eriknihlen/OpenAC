using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Input;

internal interface IInputMonotonicClock
{
    float NowSeconds { get; }
}

internal sealed class EnvironmentInputMonotonicClock : IInputMonotonicClock
{
    public float NowSeconds => (float)(Environment.TickCount64 / 1000.0);
}

internal interface IPointerPositionSource
{
    float X { get; }
    float Y { get; }
}

internal sealed class PointerPositionState : IPointerPositionSource
{
    public float X { get; set; }
    public float Y { get; set; }
}

internal interface IMouseLookCursor
{
    bool HasSavedMode { get; }
    void Hide();
    void Restore();
}

internal interface IMouseLookInputFrameController
{
    bool Active { get; }
    bool HandlePointerAction(InputAction action, ActivationType activation);
    void QueueRawDelta(float dx, float dy);
    void Tick();
    void EndAndRestoreCursor();
    void EndForLifecycle();
    void ResetSession();
}

internal sealed class SilkMouseLookCursor : IMouseLookCursor
{
    private readonly IMouse _mouse;
    private CursorMode? _savedMode;

    public SilkMouseLookCursor(IMouse mouse) =>
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));

    public bool HasSavedMode => _savedMode.HasValue;

    public void Hide()
    {
        _savedMode = _mouse.Cursor.CursorMode;
        _mouse.Cursor.CursorMode = CursorMode.Hidden;
    }

    public void Restore()
    {
        _mouse.Cursor.CursorMode = _savedMode ?? CursorMode.Normal;
        _savedMode = null;
    }
}

internal interface IChaseCameraSource
{
    ChaseCamera? Legacy { get; }
    RetailChaseCamera? Retail { get; }
}

internal sealed class ChaseCameraInputState : IChaseCameraSource
{
    public ChaseCamera? Legacy { get; set; }
    public RetailChaseCamera? Retail { get; set; }
    public float Sensitivity { get; set; } = 0.15f;
    public bool RmbOrbitHeld { get; set; }
}

internal sealed class MouseLookController : IMouseLookInputFrameController
{
    private readonly IMouseSource _mouseSource;
    private readonly IPointerPositionSource _pointer;
    private readonly ILocalPlayerModeSource _playerMode;
    private readonly IRuntimeLocalPlayerControllerSource _playerController;
    private readonly CameraController _camera;
    private readonly ChaseCameraInputState _chase;
    private readonly IMovementInputSource _movementInput;
    private readonly LocalPlayerOutboundController _outbound;
    private readonly ILiveWorldSessionSource _session;
    private readonly IMouseLookCursor _cursor;
    private readonly IInputMonotonicClock _clock;
    private readonly MouseLookState _state;
    private bool _lastWantCaptureMouse;

    public MouseLookController(
        IMouseSource mouseSource,
        IPointerPositionSource pointer,
        ILocalPlayerModeSource playerMode,
        IRuntimeLocalPlayerControllerSource playerController,
        CameraController camera,
        ChaseCameraInputState chase,
        IMovementInputSource movementInput,
        LocalPlayerOutboundController outbound,
        ILiveWorldSessionSource session,
        IMouseLookCursor cursor,
        IInputMonotonicClock clock)
    {
        _mouseSource = mouseSource ?? throw new ArgumentNullException(nameof(mouseSource));
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _playerMode = playerMode ?? throw new ArgumentNullException(nameof(playerMode));
        _playerController = playerController
            ?? throw new ArgumentNullException(nameof(playerController));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _movementInput = movementInput
            ?? throw new ArgumentNullException(nameof(movementInput));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _state = new MouseLookState(ApplyHorizontalAdjustment);
    }

    public bool Active => _state.Active;

    public bool HandlePointerAction(InputAction action, ActivationType activation)
    {
        if (action == InputAction.AcdreamRmbOrbitHold)
        {
            if (activation == ActivationType.Press)
                _chase.RmbOrbitHeld = _playerMode.IsPlayerMode && _camera.IsChaseMode;
            else if (activation == ActivationType.Release)
                _chase.RmbOrbitHeld = false;
            return true;
        }

        if (action is not (
            InputAction.CameraInstantMouseLook
            or InputAction.CameraActivateAlternateMode))
            return false;

        if (activation == ActivationType.Press)
            Begin();
        else if (activation == ActivationType.Release)
            EndAndRestoreCursor();
        return true;
    }

    public void QueueRawDelta(float dx, float dy) => _state.QueueDelta(dx, dy);

    public void Tick()
    {
        bool wantCaptureMouse = _mouseSource.WantCaptureMouse;
        if (wantCaptureMouse != _lastWantCaptureMouse)
        {
            if (wantCaptureMouse && _state.Active)
                EndAndRestoreCursor();
            _state.OnWantCaptureMouseChanged(wantCaptureMouse);
            _lastWantCaptureMouse = wantCaptureMouse;
        }

        float nowSeconds = _clock.NowSeconds;
        if (!_state.TryTakeRawSample(nowSeconds, out float rawX, out float rawY))
            return;

        PlayerMovementController? controller = _playerController.Controller;
        if (rawX == 0f && rawY == 0f)
            controller?.StopMouseDrift(_movementInput.Capture());

        (float filteredX, float filteredY) =
            CameraDiagnostics.UseRetailChaseCamera && _chase.Retail is { } retail
                ? retail.FilterMouseDelta(rawX, rawY, weight: 0.5f, nowSec: nowSeconds)
                : (rawX, rawY);
        _state.ApplyDelta(filteredX, _chase.Sensitivity);
        if (_chase.Retail is { } retailCamera)
            retailCamera.AdjustPitch(filteredY * 0.0666666701f * _chase.Sensitivity);
        else
            _chase.Legacy?.AdjustPitch(filteredY * 0.003f * _chase.Sensitivity);
    }

    public void EndAndRestoreCursor()
    {
        bool stateWasActive = _state.Active;
        _state.Release();

        PlayerMovementController? controller = _playerController.Controller;
        if (controller is { CanExecuteLiveMovement: true }
            && controller.EndMouseLook(_movementInput.Capture()))
        {
            _outbound.TrySendMovement(
                _session.CurrentSession,
                controller,
                controller.CaptureMovementResult(mouseLookEvent: false));
        }

        if (stateWasActive || _cursor.HasSavedMode)
            _cursor.Restore();
    }

    public void EndForLifecycle()
    {
        EndAndRestoreCursor();
        _chase.RmbOrbitHeld = false;
    }

    public void ResetSession()
    {
        bool stateWasActive = _state.Active;
        _state.Release();
        _chase.RmbOrbitHeld = false;
        _lastWantCaptureMouse = false;
        if (stateWasActive || _cursor.HasSavedMode)
            _cursor.Restore();
    }

    private void Begin()
    {
        PlayerMovementController? controller = _playerController.Controller;
        if (!_playerMode.IsPlayerMode
            || !_camera.IsChaseMode
            || controller is not { State: PlayerState.InWorld })
        {
            return;
        }

        float nowSeconds = _clock.NowSeconds;
        _state.Press(
            _pointer.X,
            _pointer.Y,
            _mouseSource.WantCaptureMouse,
            nowSeconds);
        if (!_state.Active)
            return;

        if (!controller.BeginMouseLook(_movementInput.Capture()))
        {
            _state.Release();
            return;
        }

        _outbound.TrySendMovement(
            _session.CurrentSession,
            controller,
            controller.CaptureMovementResult(mouseLookEvent: false));
        _cursor.Hide();
    }

    private void ApplyHorizontalAdjustment(float adjustment) =>
        _playerController.Controller?.SubmitMouseTurnAdjustment(
            adjustment,
            _movementInput.Capture());
}
