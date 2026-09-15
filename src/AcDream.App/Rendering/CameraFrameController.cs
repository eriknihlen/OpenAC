using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Update;
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

        if (!_localFrame.TryGetPresentationAfterNetwork(out var playerFrame))
            return;

        if (!playerFrame.AdvancedBeforeNetwork)
            _spatialReconciler.Reconcile();

        MovementResult result = playerFrame.Movement;
        float cameraDt = controller.PresentedDeltaSeconds;
        legacy.Update(
            result.RenderPosition,
            controller.Yaw,
            isOnGround: result.IsOnGround,
            dt: cameraDt);

        retail?.Update(
            result.RenderPosition,
            controller.Yaw,
            playerVelocity: controller.CachedVelocity,
            inContact: controller.BodyInContact,
            contactPlaneNormal: controller.ContactPlane.Normal,
            dt: cameraDt,
            cellId: controller.CellId,
            selfEntityId: controller.LocalEntityId,
            trackedTargetPoint: _combatTarget.GetTrackedTargetPoint());
    }
}
