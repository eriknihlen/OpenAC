using System.Numerics;
using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.Core.Physics;

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
