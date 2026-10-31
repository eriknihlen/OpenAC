using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Update;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Rendering;

namespace AcDream.App.Rendering;

internal sealed class CameraFrameController : ICameraFramePhase
{
    private readonly CameraController _camera;
    private readonly IInputCaptureSource _capture;
    private readonly ICameraFrameInputSource _input;
    private readonly ILocalPlayerPresentationRuntime _player;
    private readonly IChaseCameraSource _chase;
    private readonly RetailLocalPlayerFrameController _localFrame;
    private readonly ILiveSpatialReconcilePhase _spatialReconciler;
    private readonly ICombatCameraTargetSource _combatTarget;

    public CameraFrameController(
        CameraController camera,
        IInputCaptureSource capture,
        ICameraFrameInputSource input,
        ILocalPlayerPresentationRuntime player,
        IChaseCameraSource chase,
        RetailLocalPlayerFrameController localFrame,
        ILiveSpatialReconcilePhase spatialReconciler,
        ICombatCameraTargetSource combatTarget)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _localFrame = localFrame ?? throw new ArgumentNullException(nameof(localFrame));
        _spatialReconciler = spatialReconciler
            ?? throw new ArgumentNullException(nameof(spatialReconciler));
        _combatTarget = combatTarget ?? throw new ArgumentNullException(nameof(combatTarget));
    }

    public void Tick(UpdateFrameTiming timing)
    {
        // An overlay window with the keyboard takes the camera keys, not the
        // camera: the chase still follows the character (a bot walking it
        // while its panel has focus) and only the adjustments go quiet.
        bool keysAvailable = !_capture.DevToolsWantCaptureKeyboard && _input.IsAvailable;

        if (_camera.IsFlyMode)
        {
            if (!keysAvailable)
                return;
            FlyCameraInput fly = _input.CaptureFly();
            _camera.Fly.Update(
                timing.SimulationDeltaSeconds,
                fly.Forward,
                fly.Left,
                fly.Backward,
                fly.Right,
                fly.Up,
                fly.Down,
                fly.Boost);
            return;
        }

        PlayerMovementController? controller = _player.Controller;
        ChaseCamera? legacy = _chase.Legacy;
        RetailChaseCamera? retail = _chase.Retail;
        if (!_player.CanPresentPlayer || controller is null || legacy is null)
            return;

        ChaseCameraAdjustmentInput input = keysAvailable
            ? _input.CaptureChaseAdjustment()
            : default;
        // While a modern mouse drag owns the view (held, or finishing its turn)
        // the mouse is the only thing that rotates it.
        bool modernView = _chase.ModernMouseTurning && _chase.ModernViewActive;
        if (modernView)
            input = input with { RotateLeft = false, RotateRight = false };
        if (CameraDiagnostics.UseRetailChaseCamera && retail is not null)
        {
            float adjustment = CameraDiagnostics.CameraAdjustmentSpeed
                * timing.SimulationDeltaSecondsSingle;
            if (input.ZoomIn)
                retail.AdjustDistance(-adjustment);
            if (input.ZoomOut)
                retail.AdjustDistance(+adjustment);
            if (input.Raise)
                retail.AdjustPitch(+adjustment);
            if (input.Lower)
                retail.AdjustPitch(-adjustment);
            // Keypad 4 orbits the camera to the player's left, which is a negative
            // yaw offset in this frame - the signs were the other way round and the
            // keys rotated backwards. Same below for the legacy camera.
            if (input.RotateLeft)
                retail.AdjustYaw(-adjustment);
            if (input.RotateRight)
                retail.AdjustYaw(+adjustment);
        }
        else
        {
            float adjustment = CameraDiagnostics.CameraAdjustmentSpeed
                * timing.SimulationDeltaSecondsSingle;
            if (input.ZoomIn)
                legacy.AdjustDistance(-adjustment);
            if (input.ZoomOut)
                legacy.AdjustDistance(+adjustment);
            if (input.Raise)
                legacy.AdjustPitch(+adjustment * 0.02f);
            if (input.Lower)
                legacy.AdjustPitch(-adjustment * 0.02f);
            if (input.RotateLeft)
                legacy.YawOffset -= adjustment * 0.02f;
            if (input.RotateRight)
                legacy.YawOffset += adjustment * 0.02f;
        }

        // In the head view a rotate (keys or mouse orbit) turns the character.
        if (retail is not null && retail.TryTakeFirstPersonTurn(out float headingStep))
            TurnForFirstPersonRotate(controller, headingStep);

        if (!_localFrame.TryGetPresentationAfterNetwork(out var playerFrame))
            return;

        if (!playerFrame.AdvancedBeforeNetwork)
            _spatialReconciler.Reconcile();

        MovementResult result = playerFrame.Movement;
        float cameraDt = controller.PresentedDeltaSeconds;
        if (modernView)
        {
            // The orbit is the view yaw measured from the character's facing,
            // so the eye follows the mouse alone and never the facing's steps.
            // When the drag ends the last offset stays, so the ordinary
            // camera takes over from exactly where the view was.
            float yawOffset = MathF.IEEERemainder(
                _chase.ModernViewYaw - controller.Yaw, 2f * MathF.PI);
            legacy.YawOffset = yawOffset;
            if (retail is not null)
                retail.YawOffset = yawOffset;
        }
        legacy.Update(
            result.RenderPosition,
            controller.Yaw,
            isOnGround: result.IsOnGround,
            dt: cameraDt,
            directOrbit: modernView);

        retail?.Update(
            result.RenderPosition,
            controller.Yaw,
            playerVelocity: controller.CachedVelocity,
            inContact: controller.BodyInContact,
            contactPlaneNormal: controller.ContactPlane.Normal,
            dt: cameraDt,
            cellId: controller.CellId,
            selfEntityId: controller.LocalEntityId,
            trackedTargetPoint: modernView
                ? null : _combatTarget.GetTrackedTargetPoint(),
            // The head view looks out of the character's eyes, which turn at
            // the character's rate; it keeps its ordinary smoothing.
            directOrbit: modernView && !retail.IsInHead);
    }

    // One step re-targets the turn from the current heading, so a held rotate
    // turns the character continuously and finishes its last step on release.
    // The turn sends no movement event of its own; the heading reaches the
    // server with the position heartbeat, so a held key is not a packet per frame.
    private static void TurnForFirstPersonRotate(
        PlayerMovementController controller,
        float headingStepDegrees)
    {
        if (!controller.CanExecuteLiveMovement)
            return;
        float heading = MoveToMath.HeadingFromYaw(controller.Yaw);
        controller.RequestTurnToHeading(
            RetailChaseCamera.FirstPersonTurnTargetHeading(heading, headingStepDegrees),
            applyRunHoldKey: false,
            requestMovementEvent: false);
    }
}
