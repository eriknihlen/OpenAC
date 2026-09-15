using System.Numerics;
using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
public sealed class CameraFrameControllerTests
{
    [Fact]
    public void FlyMode_UsesOneSemanticHeldInputSnapshot()
    {
        CameraController camera = CreateCamera();
        camera.ToggleFly();
        Vector3 start = camera.Fly.Position;
        var input = new InputSource
        {
            Fly = new FlyCameraInput(
                Forward: true,
                Left: false,
                Backward: false,
                Right: false,
                Up: true,
                Down: false,
                Boost: false),
        };
        CameraFrameController frame = CreateFrame(camera, input: input);

        frame.Tick(new UpdateFrameTiming(0.5, 0.5f, 0.5));

        Assert.Equal(1, input.FlyCaptures);
        Assert.InRange(camera.Fly.Position.Y - start.Y, 5.999f, 6.001f);
        Assert.InRange(camera.Fly.Position.Z - start.Z, 5.999f, 6.001f);
    }

    [Fact]
    public void DevToolsKeyboardCapture_PausesFlyCamera()
    {
        CameraController camera = CreateCamera();
        camera.ToggleFly();
        Vector3 start = camera.Fly.Position;
        var input = new InputSource
        {
            Fly = new FlyCameraInput(true, false, false, false, false, false, false),
        };
        CameraFrameController frame = CreateFrame(
            camera,
            capture: new CaptureSource { DevToolsKeyboard = true },
            input: input);

        frame.Tick(new UpdateFrameTiming(1.0, 1f, 1.0));

        Assert.Equal(start, camera.Fly.Position);
        Assert.Equal(0, input.FlyCaptures);
    }

    [Fact]
    public void DevToolsKeyboardCapture_KeepsTheChaseCameraFollowing_AndOnlyMutesTheAdjustmentKeys()
    {
        PlayerMovementController controller = CreatePlayer();
        var calls = new List<string>();
        var runtime = new PlayerRuntime(controller, calls);
        var localFrame = new RetailLocalPlayerFrameController(
            runtime,
            new StillMovementInput());
        CameraController camera = CreateCamera();
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        camera.EnterChaseMode(legacy, retail);
        float distanceBefore = legacy.Distance;
        var frame = new CameraFrameController(
            camera,
            new CaptureSource { DevToolsKeyboard = true },
            new InputSource { Chase = new ChaseCameraAdjustmentInput(ZoomIn: true, false, false, false, false, false) },
            runtime,
            new ChaseSource(legacy, retail),
            localFrame,
            new Reconciler(calls),
            new CombatTargetSource());

        frame.Tick(new UpdateFrameTiming(1.0 / 60.0, 1f / 60f, 1.0));

        // The camera still went to the character; the held zoom key did nothing.
        Assert.NotEqual(Vector3.Zero, legacy.Position);
        Assert.Equal(distanceBefore, legacy.Distance);
    }

    [Fact]
    public void InboundCreatedPlayer_ProjectsThenReconcilesBeforeCameraPublication()
    {
        PlayerMovementController controller = CreatePlayer();
        var calls = new List<string>();
        var runtime = new PlayerRuntime(controller, calls);
        var localFrame = new RetailLocalPlayerFrameController(
            runtime,
            new StillMovementInput());
        CameraController camera = CreateCamera();
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        camera.EnterChaseMode(legacy, retail);
        var chase = new ChaseSource(legacy, retail);
        var reconciler = new Reconciler(calls);
        var frame = new CameraFrameController(
            camera,
            new CaptureSource(),
            new InputSource(),
            runtime,
            chase,
            localFrame,
            reconciler,
            new CombatTargetSource());

        frame.Tick(new UpdateFrameTiming(1.0 / 60.0, 1f / 60f, 1.0));

        Assert.Equal(["project", "reconcile"], calls);
        Assert.NotEqual(Vector3.Zero, legacy.Position);
        Assert.NotEqual(Vector3.Zero, retail.Position);
    }

    [Fact]
    public void KeypadRotate_SwingsTheRetailCameraLeftForANegativeYaw_AndRightForAPositiveOne()
    {
        // The camera left key rotates the viewer offset by the negative angle and
        // the right key by the positive one; a positive yaw offset moves the eye
        // to the player's right (x = h * sin(yaw)).
        PlayerMovementController controller = CreatePlayer();
        var runtime = new PlayerRuntime(controller, []);
        var localFrame = new RetailLocalPlayerFrameController(runtime, new StillMovementInput());
        CameraController camera = CreateCamera();
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        camera.EnterChaseMode(legacy, retail);
        var input = new InputSource { Chase = default(ChaseCameraAdjustmentInput) with { RotateLeft = true } };
        var frame = new CameraFrameController(
            camera, new CaptureSource(), input, runtime,
            new ChaseSource(legacy, retail), localFrame, new Reconciler([]), new CombatTargetSource());
        var timing = new UpdateFrameTiming(1.0 / 60.0, 1f / 60f, 1.0);

        frame.Tick(timing);
        Assert.True(retail.YawOffset < 0f, $"left gave {retail.YawOffset}");
        float afterLeft = retail.YawOffset;

        input.Chase = default(ChaseCameraAdjustmentInput) with { RotateRight = true };
        frame.Tick(timing);
        Assert.True(retail.YawOffset > afterLeft, $"right gave {retail.YawOffset}");
    }

    [Fact]
    public void KeypadRotate_SwingsTheLegacyCameraTheSameWay()
    {
        PlayerMovementController controller = CreatePlayer();
        var runtime = new PlayerRuntime(controller, []);
        var localFrame = new RetailLocalPlayerFrameController(runtime, new StillMovementInput());
        CameraController camera = CreateCamera();
        var legacy = new ChaseCamera();
        camera.EnterChaseMode(legacy, new RetailChaseCamera());
        var input = new InputSource { Chase = default(ChaseCameraAdjustmentInput) with { RotateLeft = true } };
        var frame = new CameraFrameController(
            camera, new CaptureSource(), input, runtime,
            new ChaseSource(legacy, null), localFrame, new Reconciler([]), new CombatTargetSource());
        var timing = new UpdateFrameTiming(1.0 / 60.0, 1f / 60f, 1.0);

        frame.Tick(timing);
        Assert.True(legacy.YawOffset < 0f, $"left gave {legacy.YawOffset}");
        float afterLeft = legacy.YawOffset;

        input.Chase = default(ChaseCameraAdjustmentInput) with { RotateRight = true };
        frame.Tick(timing);
        Assert.True(legacy.YawOffset > afterLeft, $"right gave {legacy.YawOffset}");
    }

    [Fact]
    public void PreNetworkAdvancedPlayer_DoesNotRunTheInboundCreationReconcile()
    {
        PlayerMovementController controller = CreatePlayer();
        var calls = new List<string>();
        var runtime = new PlayerRuntime(controller, calls);
        var localFrame = new RetailLocalPlayerFrameController(
            runtime,
            new StillMovementInput());
        localFrame.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);
        calls.Clear();
        CameraController camera = CreateCamera();
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        camera.EnterChaseMode(legacy, retail);
        var frame = new CameraFrameController(
            camera,
            new CaptureSource(),
            new InputSource(),
            runtime,
            new ChaseSource(legacy, retail),
            localFrame,
            new Reconciler(calls),
            new CombatTargetSource());

        frame.Tick(new UpdateFrameTiming(1.0 / 60.0, 1f / 60f, 1.0));

        Assert.Empty(calls);
    }

    [Fact]
    public void RetailChaseCamera_OnASlopeWithRealizedVelocity_TiltsToFacing()
    {
        float upDelta = RunSlopeArmDelta(facingYaw: 0f);
        float downDelta = RunSlopeArmDelta(facingYaw: MathF.PI);

        Assert.True(upDelta > 0.05f, $"uphill on-off forward.Z delta was {upDelta}");
        Assert.True(downDelta < -0.05f, $"downhill on-off forward.Z delta was {downDelta}");
    }

    private static float RunSlopeArmDelta(float facingYaw) =>
        RunSlopeArm(facingYaw, alignToSlope: true) - RunSlopeArm(facingYaw, alignToSlope: false);

    // Every arm backpedals through the same quanta, then faces the direction under test.
    private static float RunSlopeArm(float facingYaw, bool alignToSlope)
    {
        bool savedAlign = CameraDiagnostics.AlignToSlope;
        try
        {
            CameraDiagnostics.AlignToSlope = alignToSlope;

            PlayerMovementController controller = CreateSlopedPlayer();
            controller.AttachAnimationRootMotionSource(
                (_, frame) => frame.Origin = new Vector3(0f, -0.1f, 0f));
            var runtime = new PlayerRuntime(controller, []);
            var localFrame = new RetailLocalPlayerFrameController(
                runtime, new BackwardMovementInput());
            CameraController camera = CreateCamera();
            var legacy = new ChaseCamera();
            var retail = new RetailChaseCamera();
            camera.EnterChaseMode(legacy, retail);
            var frame = new CameraFrameController(
                camera, new CaptureSource(), new InputSource(), runtime,
                new ChaseSource(legacy, retail), localFrame, new Reconciler([]),
                new CombatTargetSource());

            float dt = PhysicsBody.MinQuantum + 0.001f;
            var timing = new UpdateFrameTiming(dt, dt, 1.0);

            float startX = controller.Position.X;
            for (int i = 0; i < 20; i++)
                localFrame.AdvanceBeforeNetwork(dt);

            // Reseeding in place zeroes the velocity calc_friction projected onto the slope; the realized velocity stays.
            controller.SeedPlacementForTest(controller.Position, 0x0001u, controller.Position);

            Assert.True(controller.BodyVelocity.X * controller.BodyVelocity.X
                + controller.BodyVelocity.Y * controller.BodyVelocity.Y < 1e-4f);
            Assert.True(MathF.Abs(controller.CachedVelocity.X) > 1f);
            Assert.True(MathF.Abs(controller.CachedVelocity.Y) > 1f);
            Assert.False(controller.IsAirborne);
            Assert.True(controller.ContactPlane.Normal.Z < MathF.Cos(10f * MathF.PI / 180f));
            Assert.True(MathF.Abs(controller.Position.X - startX) > 0.5f);

            controller.Yaw = facingYaw;
            for (int i = 0; i < 200; i++)
                frame.Tick(timing);

            Vector3 pivot = controller.Position + new Vector3(0f, 0f, retail.PivotHeight);
            return Vector3.Normalize(pivot - retail.Position).Z;
        }
        finally
        {
            CameraDiagnostics.AlignToSlope = savedAlign;
        }
    }

    private static PlayerMovementController CreateSlopedPlayer()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)(50 + x * 6 + y * 6);
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i;

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);

        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 74f), 0x0001u, new Vector3(96f, 96f, 74f));
        // Backpedaling at 45 deg realizes a diagonal world velocity from the one local root-motion axis.
        controller.Yaw = MathF.PI / 4f;
        return controller;
    }

    private static CameraFrameController CreateFrame(
        CameraController camera,
        CaptureSource? capture = null,
        InputSource? input = null)
    {
        var runtime = new PlayerRuntime(null, []);
        var localFrame = new RetailLocalPlayerFrameController(
            runtime,
            new StillMovementInput());
        return new CameraFrameController(
            camera,
            capture ?? new CaptureSource(),
            input ?? new InputSource(),
            runtime,
            new ChaseSource(null, null),
            localFrame,
            new Reconciler([]),
            new CombatTargetSource());
    }

    private static CameraController CreateCamera() =>
        new(new OrbitCamera(), new FlyCamera());

    private static PlayerMovementController CreatePlayer()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i;
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001u, new Vector3(96f, 96f, 50f));
        return controller;
    }

    private sealed class CaptureSource : IInputCaptureSource
    {
        public bool DevToolsKeyboard { get; set; }
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => DevToolsKeyboard;
        public bool DevToolsWantCaptureKeyboard => DevToolsKeyboard;
    }

    private sealed class InputSource : ICameraFrameInputSource
    {
        public bool IsAvailable { get; set; } = true;
        public FlyCameraInput Fly { get; set; }
        public ChaseCameraAdjustmentInput Chase { get; set; }
        public int FlyCaptures { get; private set; }
        public FlyCameraInput CaptureFly()
        {
            FlyCaptures++;
            return Fly;
        }
        public ChaseCameraAdjustmentInput CaptureChaseAdjustment() => Chase;
    }

    private sealed class ChaseSource(
        ChaseCamera? legacy,
        RetailChaseCamera? retail) : IChaseCameraSource
    {
        public ChaseCamera? Legacy => legacy;
        public RetailChaseCamera? Retail => retail;
    }

    private sealed class StillMovementInput : IMovementInputSource
    {
        public MovementInput Capture() => default;
    }

    private sealed class BackwardMovementInput : IMovementInputSource
    {
        public MovementInput Capture() => new(Backward: true);
    }

    private sealed class PlayerRuntime(
        PlayerMovementController? controller,
        List<string> calls) : ILocalPlayerFrameRuntime
    {
        public bool CanPresentPlayer { get; set; } = controller is not null;
        public PlayerMovementController? Controller => controller;
        public uint ResolveLocalEntityId() => 7u;
        public void HandleTargeting() { }
        public bool IsHidden => false;
        public RetailObjectClockDisposition ObjectClockDisposition =>
            RetailObjectClockDisposition.Advance;
        public void Project(
            PlayerMovementController owner,
            MovementResult movement,
            bool hidden) => calls.Add("project");
        public void SendPreNetwork(
            PlayerMovementController owner,
            MovementResult movement,
            bool hidden) { }
        public void SendPostNetwork(PlayerMovementController owner, bool hidden) { }
    }

    private sealed class Reconciler(List<string> calls)
        : ILiveSpatialReconcilePhase
    {
        public void Reconcile() => calls.Add("reconcile");
    }

    private sealed class CombatTargetSource : ICombatCameraTargetSource
    {
        public Vector3? GetTrackedTargetPoint() => null;
    }
}
