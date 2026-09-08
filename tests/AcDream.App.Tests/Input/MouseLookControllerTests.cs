using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

[Collection(AcDream.App.Tests.Rendering.CameraDiagnosticsCollection.Name)]
public sealed class MouseLookControllerTests
{
    [Fact]
    public void InstantMouseLookRequiresPlayerChaseAndInWorldState()
    {
        Harness harness = CreateHarness();

        Assert.True(harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Press));
        Assert.False(harness.Owner.Active);

        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Player.State = PlayerState.PortalSpace;
        harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Press);
        Assert.False(harness.Owner.Active);

        harness.Player.State = PlayerState.InWorld;
        harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Press);

        Assert.True(harness.Owner.Active);
        Assert.Equal(1, harness.Cursor.HideCount);
    }

    [Fact]
    public void ReleaseEndsMouseLookAndRestoresCursorExactlyOnce()
    {
        Harness harness = CreateActiveHarness();

        harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Release);
        harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Release);

        Assert.False(harness.Owner.Active);
        Assert.Equal(1, harness.Cursor.RestoreCount);
        Assert.False(harness.Player.EndMouseLook(new MovementInput()));
    }

    [Fact]
    public void UiCaptureTransitionEndsActiveMouseLook()
    {
        Harness harness = CreateActiveHarness();

        harness.Mouse.WantCaptureMouse = true;
        harness.Owner.Tick();

        Assert.False(harness.Owner.Active);
        Assert.Equal(1, harness.Cursor.RestoreCount);
        Assert.False(harness.Player.EndMouseLook(new MovementInput()));
    }

    [Fact]
    public void SessionResetRestoresCaptureAndClearsRmbOrbitWithoutWireSend()
    {
        Harness harness = CreateActiveHarness();
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Press);
        Assert.True(harness.Chase.RmbOrbitHeld);

        harness.Owner.ResetSession();

        Assert.False(harness.Owner.Active);
        Assert.False(harness.Chase.RmbOrbitHeld);
        Assert.Equal(1, harness.Cursor.RestoreCount);
        Assert.Null(harness.Session.CurrentSession);
    }

    [Fact]
    public void SixRawSamplesCrossRetailExtentGateAndSubmitTurnMotion()
    {
        Harness harness = CreateActiveHarness();

        for (int i = 0; i < 6; i++)
        {
            harness.Clock.NowSeconds += 0.01f;
            harness.Owner.QueueRawDelta(2f, 0f);
            harness.Owner.Tick();
        }

        MovementResult result = harness.Player.Update(
            PhysicsBody.MinQuantum + 0.001f,
            new MovementInput());
        Assert.NotNull(result.TurnCommand);
    }

    [Fact]
    public void StrictIdleBoundaryStopsMouseDriftOnlyAfterPointTwoSeconds()
    {
        bool previousRetailCamera = CameraDiagnostics.UseRetailChaseCamera;
        CameraDiagnostics.UseRetailChaseCamera = false;
        try
        {
            Harness harness = CreateActiveHarness();
            for (int i = 0; i < 6; i++)
            {
                harness.Clock.NowSeconds += 0.01f;
                harness.Owner.QueueRawDelta(2f, 0f);
                harness.Owner.Tick();
            }

            MovementResult turning = harness.Player.Update(
                PhysicsBody.MinQuantum + 0.001f,
                new MovementInput(Run: true));
            Assert.NotNull(turning.TurnCommand);

            float boundary = harness.Clock.NowSeconds
                + MouseLookState.IdleZeroDelaySeconds;
            harness.Clock.NowSeconds = boundary;
            harness.Owner.Tick();
            MovementResult held = harness.Player.Update(
                PhysicsBody.MinQuantum + 0.001f,
                new MovementInput(Run: true));
            Assert.NotNull(held.TurnCommand);

            harness.Clock.NowSeconds = boundary + 0.0001f;
            harness.Owner.Tick();
            MovementResult stopped = harness.Player.Update(
                PhysicsBody.MinQuantum + 0.001f,
                new MovementInput(Run: true));
            Assert.Null(stopped.TurnCommand);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = previousRetailCamera;
        }
    }

    [Fact]
    public void LifecycleExitAlsoClearsRmbOrbitLatch()
    {
        Harness harness = CreateActiveHarness();
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Press);
        Assert.True(harness.Chase.RmbOrbitHeld);

        harness.Owner.EndForLifecycle();

        Assert.False(harness.Owner.Active);
        Assert.False(harness.Chase.RmbOrbitHeld);
        Assert.False(harness.Player.EndMouseLook(new MovementInput()));
    }

    [Fact]
    public void RmbOrbitLatchOnlyEngagesInPlayerChaseMode()
    {
        Harness harness = CreateHarness();

        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Press);
        Assert.False(harness.Chase.RmbOrbitHeld);

        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Press);
        Assert.True(harness.Chase.RmbOrbitHeld);

        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Release);
        Assert.False(harness.Chase.RmbOrbitHeld);
    }

    private static Harness CreateActiveHarness()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Owner.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Press);
        Assert.True(harness.Owner.Active);
        return harness;
    }

    private static Harness CreateHarness()
    {
        PlayerMovementController player = CreatePlayer();
        var mode = new LocalPlayerModeState();
        var slot = new RuntimeLocalPlayerMovementState { Controller = player };
        var camera = new CameraController(new OrbitCamera(), new FlyCamera());
        var chase = new ChaseCameraInputState();
        var mouse = new FakeMouse();
        var pointer = new PointerPositionState { X = 320f, Y = 240f };
        var movement = new FixedMovementInputSource();
        var cursor = new FakeCursor();
        var clock = new FakeClock();
        var session = new NullSessionSource();
        var outbound = new LocalPlayerOutboundController(
            static (_, _, _, _, _, _) => { });
        var owner = new MouseLookController(
            mouse,
            pointer,
            mode,
            slot,
            camera,
            chase,
            movement,
            outbound,
            session,
            cursor,
            clock);
        return new Harness(owner, mode, player, camera, chase, mouse, cursor, clock, session);
    }

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
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001u, new Vector3(96f, 96f, 50f));
        return controller;
    }

    private sealed record Harness(
        MouseLookController Owner,
        LocalPlayerModeState Mode,
        PlayerMovementController Player,
        CameraController Camera,
        ChaseCameraInputState Chase,
        FakeMouse Mouse,
        FakeCursor Cursor,
        FakeClock Clock,
        NullSessionSource Session)
    {
        public void EnterChase() =>
            Camera.EnterChaseMode(new ChaseCamera(), new RetailChaseCamera());
    }

    private sealed class FixedMovementInputSource : IMovementInputSource
    {
        public MovementInput Capture() => new(Run: true);
    }

    private sealed class FakeClock : IInputMonotonicClock
    {
        public float NowSeconds { get; set; }
    }

    private sealed class FakeCursor : IMouseLookCursor
    {
        public int HideCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool HasSavedMode { get; private set; }
        public void Hide()
        {
            HideCount++;
            HasSavedMode = true;
        }
        public void Restore()
        {
            RestoreCount++;
            HasSavedMode = false;
        }
    }

    private sealed class NullSessionSource : ILiveWorldSessionSource
    {
        public WorldSession? CurrentSession => null;
    }

    private sealed class FakeMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureMouse { get; set; }
        public bool WantCaptureKeyboard { get; set; }
        public bool IsHeld(MouseButton button) => false;
    }
}
