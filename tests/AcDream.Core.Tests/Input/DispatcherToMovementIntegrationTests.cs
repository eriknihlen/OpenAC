using System;
using System.Numerics;
using AcDream.App.Input;
using AcDream.Core.Physics;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;
using Xunit;

namespace AcDream.Core.Tests.Input;

public class DispatcherToMovementIntegrationTests
{
    /// <summary>Test double — the same fake the UI.Abstractions tests use,
    /// duplicated here because it's <c>internal</c> in that assembly.</summary>
    private sealed class FakeKb : IKeyboardSource
    {
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
        private readonly System.Collections.Generic.HashSet<Key> _held = new();
        public ModifierMask CurrentModifiers { get; set; } = ModifierMask.None;
        public bool IsHeld(Key k) => _held.Contains(k);
        public void Press(Key k, ModifierMask mods = ModifierMask.None)
        {
            CurrentModifiers = mods;
            _held.Add(k);
            KeyDown?.Invoke(k, mods);
        }
        public void Release(Key k, ModifierMask mods = ModifierMask.None)
        {
            CurrentModifiers = mods;
            _held.Remove(k);
            KeyUp?.Invoke(k, mods);
        }
    }

#pragma warning disable CS0067 // events declared on the interface but unused in this fake
    private sealed class FakeMouse : IMouseSource
    {
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
        public bool IsHeld(MouseButton b) => false;
        public bool WantCaptureMouse { get; set; }
        public bool WantCaptureKeyboard { get; set; }
    }
#pragma warning restore CS0067

    private static PhysicsEngine MakeFlatEngine()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;
        var terrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(0xA9B4FFFFu, terrain, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    private static MovementInput BuildInputFromDispatcher(InputDispatcher d) =>
        new MovementInput(
            Forward:     d.IsActionHeld(InputAction.MovementForward),
            Backward:    d.IsActionHeld(InputAction.MovementBackup),
            StrafeLeft:  d.IsActionHeld(InputAction.MovementStrafeLeft),
            StrafeRight: d.IsActionHeld(InputAction.MovementStrafeRight),
            TurnLeft:    d.IsActionHeld(InputAction.MovementTurnLeft),
            TurnRight:   d.IsActionHeld(InputAction.MovementTurnRight),
            Run:         d.IsActionHeld(InputAction.MovementRunLock),
            MouseDeltaX: 0f,
            Jump:        d.IsActionHeld(InputAction.MovementJump));

    [Fact]
    public void Dispatcher_W_held_produces_forward_motion()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;  // facing +X

        var kb = new FakeKb();
        var mouse = new FakeMouse();
        var bindings = KeyBindings.AcdreamCurrentDefaults();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();

        kb.Press(Key.W);

        var input = BuildInputFromDispatcher(dispatcher);
        Assert.True(input.Forward);
        Assert.False(input.Run);

        MovementResult result = default;
        int ticks = (int)MathF.Ceiling(1.0f / PhysicsBody.MaxQuantum) + 1;  // ~11 ticks
        for (int i = 0; i < ticks; i++)
            result = controller.Update(PhysicsBody.MaxQuantum, input);
        Assert.True(result.Position.X > 96f + 2f, $"X={result.Position.X} should have moved forward");
    }

    [Fact]
    public void Dispatcher_W_then_Shift_gives_running_motion()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;

        var kb = new FakeKb();
        var mouse = new FakeMouse();
        var bindings = KeyBindings.AcdreamCurrentDefaults();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();

        kb.Press(Key.W);
        kb.Press(Key.ShiftLeft, ModifierMask.Shift);

        var input = BuildInputFromDispatcher(dispatcher);
        Assert.True(input.Forward);
        Assert.True(input.Run);      // (ShiftLeft, Shift) binding
    }

    [Fact]
    public void Dispatcher_W_release_clears_forward()
    {
        var kb = new FakeKb();
        var mouse = new FakeMouse();
        var bindings = KeyBindings.AcdreamCurrentDefaults();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();

        kb.Press(Key.W);
        Assert.True(BuildInputFromDispatcher(dispatcher).Forward);

        kb.Release(Key.W);
        Assert.False(BuildInputFromDispatcher(dispatcher).Forward);
    }

    [Fact]
    public void MovementInput_with_mouse_delta_zero_matches_input_with_mouse_delta_nonzero()
    {
        var engineA = MakeFlatEngine();
        var ctrlA = new PlayerMovementController(engineA);
        ctrlA.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        ctrlA.Yaw = 0f;

        var engineB = MakeFlatEngine();
        var ctrlB = new PlayerMovementController(engineB);
        ctrlB.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        ctrlB.Yaw = 0f;

        var inputZero = new MovementInput(Forward: true, MouseDeltaX: 0f);
        var inputJittered = new MovementInput(Forward: true, MouseDeltaX: 47.3f);

        var rA = ctrlA.Update(0.05f, inputZero);
        var rB = ctrlB.Update(0.05f, inputJittered);

        Assert.Equal(rA.ForwardCommand,   rB.ForwardCommand);
        Assert.Equal(rA.SidestepCommand,  rB.SidestepCommand);
        Assert.Equal(rA.TurnCommand,      rB.TurnCommand);
        Assert.Equal(rA.ForwardSpeed,     rB.ForwardSpeed);
    }

    [Fact]
    public void Dispatcher_no_keys_held_produces_idle_input()
    {
        var kb = new FakeKb();
        var mouse = new FakeMouse();
        var bindings = KeyBindings.AcdreamCurrentDefaults();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();

        var input = BuildInputFromDispatcher(dispatcher);
        Assert.False(input.Forward);
        Assert.False(input.Backward);
        Assert.False(input.StrafeLeft);
        Assert.False(input.StrafeRight);
        Assert.False(input.TurnLeft);
        Assert.False(input.TurnRight);
        Assert.False(input.Run);
        Assert.False(input.Jump);
        Assert.Equal(0f, input.MouseDeltaX);
    }
}
