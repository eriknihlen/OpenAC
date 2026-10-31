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

    [Fact]
    public void ModernRmb_StartsOnlyOverWorldAndSurvivesDraggingAcrossUi()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        harness.Mouse.WantCaptureMouse = true;

        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        Assert.False(harness.Chase.RmbOrbitHeld);

        harness.Mouse.WantCaptureMouse = false;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        Assert.True(harness.Chase.RmbOrbitHeld);
        Assert.True(harness.Cursor.HasSavedMode);

        harness.Mouse.WantCaptureMouse = true;
        harness.Chase.ModernViewYaw = harness.Player.Yaw + 0.5f;
        harness.Owner.Tick();
        MovementResult turning = harness.Player.Update(0.01f, new MovementInput(Run: true));
        Assert.NotNull(turning.TurnCommand);
        Assert.True(harness.Chase.RmbOrbitHeld);

        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Release);
        Assert.False(harness.Chase.RmbOrbitHeld);
        Assert.False(harness.Cursor.HasSavedMode);
        Assert.Equal(1, harness.Cursor.RestoreCount);
    }

    [Fact]
    public void ModernRmb_MouseOnlyConvergesCharacterOnViewHeading()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        float target = harness.Player.Yaw + 0.6f;
        harness.Chase.ModernViewYaw = target;

        for (int i = 0; i < 180; i++)
        {
            harness.Owner.Tick();
            harness.Player.Update(1f / 60f, new MovementInput(Run: true));
        }

        float error = MathF.Abs(MathF.IEEERemainder(
            harness.Chase.ModernViewYaw - harness.Player.Yaw,
            2f * MathF.PI));
        Assert.True(error < 0.08f, $"character/view yaw error: {error}");
    }

    // The character's turn moves its heading in whole animation keyframes
    // (the human turn cycle turns 0.05 rad, about 2.9 degrees, per keyframe,
    // 30 keyframes a second at turn speed 1), applied on 30 Hz physics steps.
    // A view that comes to rest between two keyframe headings must not make
    // the character swing back and forth around it, and the turn must not be
    // stopped and restarted at a new speed every frame.
    [Fact]
    public void ModernRmb_ViewRestingBetweenTurnSteps_CharacterSettlesWithoutHunting()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        AttachKeyframedTurn(harness.Player);
        var input = new MovementInput(Run: true);
        for (int i = 0; i < 10; i++)
            harness.Player.Update(1f / 60f, input);

        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        Assert.True(harness.Chase.RmbOrbitHeld);

        const float FrameSeconds = 1f / 144f;
        float start = harness.Chase.ModernViewYaw;
        int reversals = 0;
        int turnEdges = 0;
        uint? lastTurn = null;
        uint? turnAtRest = null;
        for (int frame = 0; frame < 144 * 3; frame++)
        {
            // Drag left at 2.05 rad/s (about 117 degrees a second) for half a
            // second, then hold the mouse still: the view rests at 1.025 rad,
            // between two of the character's keyframe headings.
            if (frame < 72)
                harness.Chase.ModernViewYaw = start + 2.05f * FrameSeconds * (frame + 1);
            harness.Owner.Tick();
            MovementResult result = harness.Player.Update(FrameSeconds, input);
            if (result.TurnCommand != lastTurn)
            {
                turnEdges++;
                if (lastTurn.HasValue && result.TurnCommand.HasValue)
                    reversals++;
            }
            lastTurn = result.TurnCommand;
            if (frame == 144 * 3 - 1)
                turnAtRest = result.TurnCommand;
        }

        float error = MathF.IEEERemainder(
            harness.Chase.ModernViewYaw - harness.Player.Yaw, 2f * MathF.PI);
        Assert.Equal(0, reversals);
        Assert.Null(turnAtRest);
        Assert.True(MathF.Abs(error) < 0.001f, $"character/view yaw error: {error}");
        // One left turn: it starts once and stops once.
        Assert.Equal(2, turnEdges);
    }

    [Fact]
    public void ModernRmb_ReleaseLetsTheCharacterFinishTurningToTheView()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        AttachKeyframedTurn(harness.Player);
        var input = new MovementInput(Run: true);
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        float view = harness.Chase.ModernViewYaw + 2f;
        harness.Chase.ModernViewYaw = view;
        harness.Owner.Tick();
        harness.Player.Update(1f / 30f, input);

        // Released long before a 115 degree turn can finish: the view stays
        // the camera's, and the character carries on to it.
        harness.Mouse.RightHeld = false;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Release);
        Assert.False(harness.Chase.RmbOrbitHeld);
        Assert.True(harness.Chase.ModernViewActive);
        Assert.Equal(1, harness.Cursor.RestoreCount);

        for (int frame = 0; frame < 144 * 2; frame++)
        {
            harness.Owner.Tick();
            harness.Player.Update(1f / 144f, input);
        }

        Assert.False(harness.Chase.ModernViewActive);
        Assert.False(harness.Player.EndMouseLook(input));
        Assert.True(MathF.Abs(MathF.IEEERemainder(view - harness.Player.Yaw, 2f * MathF.PI)) < 0.001f);
        Assert.Equal(1, harness.Cursor.RestoreCount);
    }

    [Fact]
    public void ModernRmb_TurnKeyAfterReleaseHandsTheTurnBackAtOnce()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);
        harness.Chase.ModernViewYaw += 2f;
        harness.Owner.Tick();
        harness.Player.Update(1f / 30f, new MovementInput(Run: true));
        harness.Mouse.RightHeld = false;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Release);
        Assert.True(harness.Chase.ModernViewActive);

        harness.Movement.Input = new MovementInput(Run: true, TurnRight: true);
        harness.Owner.Tick();

        Assert.False(harness.Chase.ModernViewActive);
        MovementResult keys = harness.Player.Update(1f / 60f, harness.Movement.Input);
        Assert.Equal(MotionCommand.TurnRight, keys.TurnCommand);
        Assert.False(keys.TurnUsesRunHold);
    }

    // Root motion for a turn the way the human turn cycle delivers it: whole
    // keyframes of 0.05 rad, 30 a second at speed 1.
    private static void AttachKeyframedTurn(PlayerMovementController player)
    {
        float keyframes = 0f;
        AcDream.Core.Physics.MotionInterpreter motion = player.Motion;
        player.AttachAnimationRootMotionSource((dt, delta) =>
        {
            var state = motion.InterpretedState;
            if (state.TurnCommand != MotionCommand.TurnRight
                || state.TurnSpeed == 0f)
            {
                keyframes = 0f;
                return;
            }

            keyframes += dt * 30f * MathF.Abs(state.TurnSpeed);
            int whole = (int)keyframes;
            keyframes -= whole;
            if (whole > 0)
                delta.Orientation = Quaternion.CreateFromAxisAngle(
                    Vector3.UnitZ,
                    -MathF.Sign(state.TurnSpeed) * 0.05f * whole);
        });
    }

    [Fact]
    public void ModernRmb_LifecycleExitRestoresCursorOnce()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Mouse.RightHeld = true;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);

        harness.Owner.EndForLifecycle();
        harness.Owner.EndForLifecycle();

        Assert.False(harness.Chase.RmbOrbitHeld);
        Assert.Equal(1, harness.Cursor.RestoreCount);
        Assert.False(harness.Player.EndMouseLook(new MovementInput()));
    }

    [Fact]
    public void BothButtonForward_RequiresWorldPressesAndStopsOnEitherRelease()
    {
        Harness harness = CreateHarness();
        harness.Mode.IsPlayerMode = true;
        harness.EnterChase();
        harness.Chase.ModernMouseTurning = true;
        harness.Chase.BothMouseButtonsRunForward = true;
        harness.Mouse.RightHeld = true;
        harness.Mouse.LeftHeld = true;
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Press);

        harness.Owner.Tick();
        Assert.False(harness.Chase.ModernMouseForward); // LMB began on UI.

        harness.Mouse.LeftStartedInWorld = true;
        harness.Owner.Tick();
        Assert.True(harness.Chase.ModernMouseForward);

        harness.Mouse.WantCaptureMouse = true; // Drag crosses UI.
        harness.Owner.Tick();
        Assert.True(harness.Chase.ModernMouseForward);

        harness.Mouse.LeftHeld = false;
        harness.Owner.Tick();
        Assert.False(harness.Chase.ModernMouseForward);

        harness.Mouse.LeftHeld = true;
        harness.Owner.Tick();
        Assert.True(harness.Chase.ModernMouseForward);
        harness.Owner.HandlePointerAction(
            InputAction.AcdreamRmbOrbitHold, ActivationType.Release);
        Assert.False(harness.Chase.ModernMouseForward);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvertMouseLookYAxisFlipsPitchForMouseLook(bool retailCamera)
    {
        bool previousRetailCamera = CameraDiagnostics.UseRetailChaseCamera;
        CameraDiagnostics.UseRetailChaseCamera = retailCamera;
        try
        {
            float normal = PitchChangeAfterMouseLook(retailCamera, invert: false, dy: 10f);
            float inverted = PitchChangeAfterMouseLook(retailCamera, invert: true, dy: 10f);

            Assert.True(normal > 0f);
            Assert.Equal(-normal, inverted, 5);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = previousRetailCamera;
        }
    }

    [Fact]
    public void InvertMouseLookYAxisFlipsYawTurnDirection()
    {
        uint? normal = TurnCommandAfterMouseLook(invert: false, dx: 2f);
        uint? inverted = TurnCommandAfterMouseLook(invert: true, dx: 2f);

        Assert.NotNull(normal);
        Assert.NotNull(inverted);
        Assert.NotEqual(normal, inverted);
    }

    private static float PitchChangeAfterMouseLook(bool retailCamera, bool invert, float dy)
    {
        Harness harness = CreateActiveHarness();
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        harness.Chase.Legacy = legacy;
        harness.Chase.Retail = retailCamera ? retail : null;
        harness.Chase.InvertMouseLookYAxis = invert;
        float before = retailCamera ? retail.Pitch : legacy.Pitch;

        harness.Clock.NowSeconds += 0.01f;
        harness.Owner.QueueRawDelta(0f, dy);
        harness.Owner.Tick();

        return (retailCamera ? retail.Pitch : legacy.Pitch) - before;
    }

    private static uint? TurnCommandAfterMouseLook(bool invert, float dx)
    {
        Harness harness = CreateActiveHarness();
        harness.Chase.InvertMouseLookYAxis = invert;

        for (int i = 0; i < 6; i++)
        {
            harness.Clock.NowSeconds += 0.01f;
            harness.Owner.QueueRawDelta(dx, 0f);
            harness.Owner.Tick();
        }

        return harness.Player.Update(
            PhysicsBody.MinQuantum + 0.001f,
            new MovementInput()).TurnCommand;
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
        return new Harness(owner, mode, player, camera, chase, mouse, cursor, clock, session, movement);
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
        NullSessionSource Session,
        FixedMovementInputSource Movement)
    {
        public void EnterChase() =>
            Camera.EnterChaseMode(new ChaseCamera(), new RetailChaseCamera());
    }

    private sealed class FixedMovementInputSource : IMovementInputSource
    {
        public MovementInput Input { get; set; } = new(Run: true);
        public MovementInput Capture() => Input;
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
        public bool RightHeld { get; set; }
        public bool LeftHeld { get; set; }
        public bool LeftStartedInWorld { get; set; }
        public bool RightStartedInWorld { get; set; } = true;
        public bool IsHeld(MouseButton button) => button switch
        {
            MouseButton.Right => RightHeld,
            MouseButton.Left => LeftHeld,
            _ => false,
        };
        public bool WasPressedOverWorld(MouseButton button) => button switch
        {
            MouseButton.Right => RightStartedInWorld,
            MouseButton.Left => LeftStartedInWorld,
            _ => false,
        };
    }
}
