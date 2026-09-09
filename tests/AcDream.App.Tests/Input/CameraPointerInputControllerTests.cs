using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class CameraPointerInputControllerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AttachFailureRollsBackRawPrefixAndLeavesCameraUnsubscribed(int failureIndex)
    {
        var surfaces = new[] { new RawSurface(), new RawSurface(), new RawSurface() };
        surfaces[failureIndex].FailAdd = true;
        CameraPointerInputController owner = Create(surfaces).Owner;

        Assert.ThrowsAny<Exception>(owner.AttachRaw);

        Assert.True(owner.IsDisposalComplete);
        Assert.All(surfaces, surface => Assert.Equal(0, surface.LiveEdges));
        Assert.Equal(failureIndex + 1, surfaces.Sum(surface => surface.AddCalls));
        Assert.Equal(failureIndex + 1, surfaces.Sum(surface => surface.RemoveCalls));
    }

    [Fact]
    public void FlyPointerUsesAbsoluteDeltaAndCaptureAdvancesPointerWithoutLooking()
    {
        var surface = new RawSurface();
        var fixture = Create([surface]);
        fixture.Camera.ToggleFly();
        fixture.Owner.AttachRaw();
        float originalYaw = fixture.Camera.Fly.Yaw;
        float originalPitch = fixture.Camera.Fly.Pitch;

        surface.Raise(new Vector2(10f, 5f));
        Assert.Equal(originalYaw - 10f * 0.003f, fixture.Camera.Fly.Yaw, 5);
        Assert.Equal(originalPitch - 5f * 0.003f, fixture.Camera.Fly.Pitch, 5);

        fixture.Capture.Mouse = true;
        surface.Raise(new Vector2(100f, 100f));
        float capturedYaw = fixture.Camera.Fly.Yaw;
        float capturedPitch = fixture.Camera.Fly.Pitch;
        fixture.Capture.Mouse = false;
        surface.Raise(new Vector2(102f, 97f));

        Assert.Equal(capturedYaw - 2f * 0.003f, fixture.Camera.Fly.Yaw, 5);
        Assert.Equal(capturedPitch + 3f * 0.003f, fixture.Camera.Fly.Pitch, 5);
    }

    [Fact]
    public void RawMoveRaisedReentrantlyDuringAttachCannotEnterPartialOwner()
    {
        var surface = new RawSurface { RaiseDuringAdd = true };
        var fixture = Create([surface]);
        fixture.Camera.ToggleFly();
        float yaw = fixture.Camera.Fly.Yaw;

        fixture.Owner.AttachRaw();
        Assert.Equal(yaw, fixture.Camera.Fly.Yaw);
        surface.Raise(new Vector2(3, 0));

        Assert.NotEqual(yaw, fixture.Camera.Fly.Yaw);
    }

    [Fact]
    public void LateGameplayBindDoesNotResubscribeAndFocusLossEndsMouseLook()
    {
        var surface = new RawSurface();
        var fixture = Create([surface]);
        fixture.Owner.AttachRaw();
        int adds = surface.AddCalls;
        var mouseLook = new MouseLook(active: true);
        var frame = new GameplayInputFrameController(
            dispatcher: null,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook,
            new Combat());

        fixture.Owner.BindGameplayFrame(frame);
        fixture.Owner.HandleFocusChanged(focused: false);

        Assert.Equal(adds, surface.AddCalls);
        Assert.Equal(1, mouseLook.LifecycleEndCalls);
    }

    [Fact]
    public void DeactivateSilencesCopiedRawAndCameraDelegatesBeforeDetach()
    {
        var surface = new RawSurface();
        var fixture = Create([surface]);
        fixture.Camera.ToggleFly();
        fixture.Owner.AttachRaw();
        Action<Vector2> copied = surface.Callback!;
        float yaw = fixture.Camera.Fly.Yaw;

        fixture.Owner.Deactivate();
        copied(new Vector2(100, 0));
        fixture.Camera.ToggleFly();
        fixture.Owner.Dispose();

        Assert.Equal(yaw, fixture.Camera.Fly.Yaw);
        Assert.True(fixture.Owner.IsDisposalComplete);
    }

    [Fact]
    public void ProcessCloseCutoffDoesNotEndMouseLookUntilSessionRetirement()
    {
        var fixture = Create([new RawSurface()]);
        var mouseLook = new MouseLook(active: true);
        var frame = new GameplayInputFrameController(
            dispatcher: null,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook,
            new Combat());
        fixture.Owner.BindGameplayFrame(frame);
        fixture.Owner.AttachRaw();

        fixture.Owner.Deactivate();
        fixture.Owner.Dispose();
        Assert.Equal(0, mouseLook.LifecycleEndCalls);

        fixture.Owner.ReleaseMouseLookAfterSessionRetirement();
        Assert.Equal(1, mouseLook.LifecycleEndCalls);
    }

    [Fact]
    public void FailedRawDetachRetriesOnlyPendingEdge()
    {
        var surface = new RawSurface();
        var fixture = Create([surface]);
        fixture.Owner.AttachRaw();
        surface.FailRemoveOnce = true;

        fixture.Owner.Dispose();
        int removes = surface.RemoveCalls;
        fixture.Owner.Dispose();

        Assert.Equal(2, surface.RemoveCalls);
        Assert.Equal(removes, surface.RemoveCalls);
        Assert.True(fixture.Owner.IsDisposalComplete);
    }

    [Fact]
    public void DisposeBeforeAttachMakesPointerOwnerTerminal()
    {
        var surface = new RawSurface();
        var fixture = Create([surface]);

        fixture.Owner.Dispose();

        Assert.Throws<ObjectDisposedException>(fixture.Owner.AttachRaw);
        fixture.Owner.Dispose();
        Assert.Equal(0, surface.LiveEdges);
    }

    [Fact]
    public void ScrollAndSensitivityPreserveModeSpecificPolicy()
    {
        var fixture = Create([new RawSurface()]);
        float orbitDistance = fixture.Camera.Orbit.Distance;

        fixture.Owner.HandleScroll(InputAction.ScrollUp);
        string orbit = fixture.Owner.AdjustSensitivity(1.2f);
        fixture.Camera.ToggleFly();
        float flyBefore = fixture.Owner.ActiveSensitivity;
        string fly = fixture.Owner.AdjustSensitivity(1.2f);

        Assert.Equal(Math.Clamp(orbitDistance - 20f, 50f, 2000f), fixture.Camera.Orbit.Distance);
        Assert.Equal("Orbit sens 1.200x", orbit);
        Assert.Equal("Fly sens 1.200x", fly);
        Assert.Equal(flyBefore * 1.2f, fixture.Owner.ActiveSensitivity, 5);
    }

    [Fact]
    public void ChaseMouseWheel_UsesRetailMultiplicativeZoomAndComponentLimit()
    {
        var fixture = Create([new RawSurface()]);
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        fixture.Mode.IsPlayerMode = true;
        fixture.Chase.Legacy = legacy;
        fixture.Chase.Retail = retail;
        fixture.Camera.EnterChaseMode(legacy, retail);

        bool savedRetail = CameraDiagnostics.UseRetailChaseCamera;
        try
        {
            CameraDiagnostics.UseRetailChaseCamera = true;
            float before = retail.Distance;
            fixture.Owner.HandleScroll(InputAction.ScrollDown);
            float afterOne = retail.Distance;
            for (int i = 0; i < 100; i++)
                fixture.Owner.HandleScroll(InputAction.ScrollDown);

            Assert.Equal(before * (1f + 0.8f * RetailChaseCamera.OffsetScalePerAdjustment), afterOne, 5);
            Assert.True(retail.Distance < 10.5f);
            Assert.True(retail.Distance > 8f);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = savedRetail;
        }
    }

    private static Fixture Create(IReadOnlyList<RawSurface> surfaces)
    {
        var camera = new CameraController(new OrbitCamera(), new FlyCamera());
        var capture = new Capture();
        var mouse = new Mouse();
        var cursor = new Cursor();
        var mode = new LocalPlayerModeState();
        var chase = new ChaseCameraInputState();
        var owner = new CameraPointerInputController(
            surfaces,
            cursor,
            new HostQuiescenceGate(),
            capture,
            mode,
            camera,
            chase,
            mouse,
            new PointerPositionState(),
            new Clock());
        return new Fixture(owner, camera, capture, cursor, mode, chase);
    }

    private sealed record Fixture(
        CameraPointerInputController Owner,
        CameraController Camera,
        Capture Capture,
        Cursor Cursor,
        LocalPlayerModeState Mode,
        ChaseCameraInputState Chase);

    private sealed class RawSurface : IRawPointerSurface
    {
        public bool FailAdd { get; set; }
        public bool FailRemoveOnce { get; set; }
        public bool RaiseDuringAdd { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        public Action<Vector2>? Callback { get; private set; }
        public void AddMouseMove(Action<Vector2> callback)
        {
            AddCalls++;
            Callback = callback;
            LiveEdges++;
            if (RaiseDuringAdd) callback(new Vector2(50, 25));
            if (FailAdd) throw new InvalidOperationException("add");
        }
        public void RemoveMouseMove(Action<Vector2> callback)
        {
            RemoveCalls++;
            if (FailRemoveOnce)
            {
                FailRemoveOnce = false;
                throw new InvalidOperationException("remove");
            }
            if (ReferenceEquals(Callback, callback))
            {
                Callback = null;
                LiveEdges--;
            }
        }
        public void Raise(Vector2 position) => Callback?.Invoke(position);
    }

    private sealed class Cursor : IPointerCursorModeTarget
    {
        public CursorMode CursorMode { get; set; }
    }

    private sealed class Capture : IInputCaptureSource
    {
        public bool Mouse { get; set; }
        public bool WantCaptureMouse => Mouse;
        public bool WantCaptureKeyboard => false;
        public bool DevToolsWantCaptureKeyboard => false;
    }

    private sealed class Mouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool IsHeld(MouseButton button) => false;
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => false;
    }

    private sealed class Clock : IInputMonotonicClock
    {
        public float NowSeconds => 1f;
    }

    private sealed class MouseLook(bool active) : IMouseLookInputFrameController
    {
        public bool Active { get; } = active;
        public int LifecycleEndCalls { get; private set; }
        public bool HandlePointerAction(InputAction action, ActivationType activation) => false;
        public void QueueRawDelta(float dx, float dy) { }
        public void Tick() { }
        public void EndAndRestoreCursor() { }
        public void EndForLifecycle() => LifecycleEndCalls++;
        public void ResetSession() { }
    }

    private sealed class Combat : ICombatInputFrameController
    {
        public void Tick() { }
        public void HandleMovementInput(InputAction action, ActivationType activation) { }
        public void AbortAutomaticAttack() { }
        public bool HandleInputAction(InputAction action, ActivationType activation) => false;
    }
}
