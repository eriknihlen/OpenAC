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
    void CaptureRaw() => Hide();
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

    public void CaptureRaw()
    {
        _savedMode = _mouse.Cursor.CursorMode;
        _mouse.Cursor.CursorMode = CursorMode.Raw;
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
    bool ModernMouseTurning => false;
    bool RmbOrbitHeld => false;
    float ModernViewYaw => 0f;
}

internal sealed class ChaseCameraInputState : IChaseCameraSource
{
    public ChaseCamera? Legacy { get; set; }
    public RetailChaseCamera? Retail { get; set; }
    public float Sensitivity { get; set; } = 0.15f;
    public bool InvertMouseLookYAxis { get; set; }
    public bool RmbOrbitHeld { get; set; }
    public bool ModernMouseTurning { get; set; }
    public bool BothMouseButtonsRunForward { get; set; }
    public bool ModernMouseForward { get; set; }
    public float ModernViewYaw { get; set; }
    public bool IgnoreNextMouseMove { get; set; }
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
    private bool _modernRmbCaptured;
    private bool _modernTurnMoving;

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
            if (_modernRmbCaptured && activation == ActivationType.Release)
                EndModernRmb();
            else if (_chase.ModernMouseTurning)
            {
                if (activation == ActivationType.Press)
                    BeginModernRmb();
                else if (activation == ActivationType.Release)
                    EndModernRmb();
            }
            else if (activation == ActivationType.Press)
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
        _chase.ModernMouseForward = _chase.ModernMouseTurning
            && _chase.BothMouseButtonsRunForward
            && _modernRmbCaptured
            && _mouseSource.IsHeld(MouseButton.Right)
            && _mouseSource.IsHeld(MouseButton.Left)
            && _mouseSource.WasPressedOverWorld(MouseButton.Left);
        if (_chase.ModernMouseTurning && _chase.RmbOrbitHeld)
            TickModernRmb();
        else if (_modernRmbCaptured)
            EndModernRmb();

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
        float invertSign = _chase.InvertMouseLookYAxis ? -1f : 1f;
        _state.ApplyDelta(filteredX * invertSign, _chase.Sensitivity);
        float pitchDelta = filteredY * invertSign;
        if (_chase.Retail is { } retailCamera)
            retailCamera.AdjustPitch(pitchDelta * 0.0666666701f * _chase.Sensitivity);
        else
            _chase.Legacy?.AdjustPitch(pitchDelta * 0.003f * _chase.Sensitivity);
    }

    public void EndAndRestoreCursor()
    {
        if (_modernRmbCaptured)
            return;

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
        EndModernRmb();
        _chase.RmbOrbitHeld = false;
        _chase.ModernMouseForward = false;
    }

    public void ResetSession()
    {
        bool stateWasActive = _state.Active;
        _state.Release();
        EndModernRmb(sendMovement: false);
        _chase.RmbOrbitHeld = false;
        _chase.ModernMouseForward = false;
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

    private void BeginModernRmb()
    {
        PlayerMovementController? controller = _playerController.Controller;
        // The dispatcher only fires a world press, but check again because a
        // UI capture may have been established by another event handler.
        if (_mouseSource.WantCaptureMouse
            || !_mouseSource.WasPressedOverWorld(MouseButton.Right)
            || !_playerMode.IsPlayerMode
            || !_camera.IsChaseMode
            || controller is not { State: PlayerState.InWorld }
            || _chase.RmbOrbitHeld)
            return;

        MovementInput input = _movementInput.Capture();
        if (!controller.BeginMouseLook(input, modern: true))
            return;

        _chase.ModernViewYaw = MathF.IEEERemainder(
            controller.Yaw + (CameraDiagnostics.UseRetailChaseCamera
                ? _chase.Retail?.YawOffset ?? 0f
                : _chase.Legacy?.YawOffset ?? 0f),
            2f * MathF.PI);
        _chase.RmbOrbitHeld = true;
        _chase.IgnoreNextMouseMove = true;
        _modernRmbCaptured = true;
        _outbound.TrySendMovement(
            _session.CurrentSession, controller,
            controller.CaptureMovementResult(mouseLookEvent: false));
        _cursor.CaptureRaw();
    }

    private void TickModernRmb()
    {
        PlayerMovementController? controller = _playerController.Controller;
        if (!_playerMode.IsPlayerMode || !_camera.IsChaseMode
            || controller is not { State: PlayerState.InWorld }
            || !_mouseSource.IsHeld(MouseButton.Right))
        {
            EndModernRmb();
            return;
        }

        MovementInput input = _movementInput.Capture();
        if (input.TurnLeft != input.TurnRight)
            return;

        float difference = MathF.IEEERemainder(
            _chase.ModernViewYaw - controller.Yaw, 2f * MathF.PI);
        if (MathF.Abs(difference) < 0.015f)
        {
            if (_modernTurnMoving)
                controller.StopMouseDrift(input);
            _modernTurnMoving = false;
        }
        else
        {
            controller.SubmitMouseTurnAdjustment(
                Math.Clamp(difference * 3f, -0.75f, 0.75f), input);
            _modernTurnMoving = true;
        }
    }

    private void EndModernRmb(bool sendMovement = true)
    {
        if (!_modernRmbCaptured)
            return;

        _modernRmbCaptured = false;
        _modernTurnMoving = false;
        _chase.RmbOrbitHeld = false;
        _chase.ModernMouseForward = false;
        _chase.IgnoreNextMouseMove = false;
        PlayerMovementController? controller = _playerController.Controller;
        if (controller is { CanExecuteLiveMovement: true }
            && controller.EndMouseLook(_movementInput.Capture())
            && sendMovement)
            _outbound.TrySendMovement(
                _session.CurrentSession, controller,
                controller.CaptureMovementResult(mouseLookEvent: false));
        _cursor.Restore();
    }
}
