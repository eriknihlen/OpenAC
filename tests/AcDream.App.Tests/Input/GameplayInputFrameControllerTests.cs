using AcDream.App.Input;
using AcDream.App.Update;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class GameplayInputFrameControllerTests
{
    [Fact]
    public void TickPreservesDispatcherMouseLookCombatOrder()
    {
        var calls = new List<string>();
        var keyboard = new HeldKeyboard(Key.W);
        var mouseSource = new NullMouse();
        var bindings = new KeyBindings();
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementForward,
            ActivationType.Hold));
        var dispatcher = InputDispatcher.CreateDetached(keyboard, mouseSource, bindings);
        dispatcher.Attach();
        keyboard.Press(Key.W);
        dispatcher.Fired += (_, activation) =>
        {
            if (activation == ActivationType.Hold)
                calls.Add("dispatcher");
        };
        var mouseLook = new FakeMouseLook(calls);
        var combat = new FakeCombat(calls);
        var controller = new GameplayInputFrameController(
            dispatcher,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook,
            combat);

        controller.Tick(new UpdateFrameTiming(1d / 60d, 1f / 60f, 1d / 60d));

        Assert.Equal(["dispatcher", "mouse-look", "combat"], calls);
    }

    [Fact]
    public void MissingInputDevicesStillTicksCombat()
    {
        var calls = new List<string>();
        var controller = new GameplayInputFrameController(
            dispatcher: null,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook: null,
            new FakeCombat(calls));

        controller.Tick(new UpdateFrameTiming(0d, 0f, 0d));

        Assert.Equal(["combat"], calls);
    }

    [Fact]
    public void CombatActionFirstAbortsAutomaticAttackThenRoutesAction()
    {
        var calls = new List<string>();
        var controller = new GameplayInputFrameController(
            dispatcher: null,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook: null,
            new FakeCombat(calls, consumes: true));

        bool consumed = controller.HandleCombatAction(
            InputAction.MovementForward,
            ActivationType.Press);

        Assert.True(consumed);
        Assert.Equal(["combat-movement", "combat-action"], calls);
    }

    [Fact]
    public void ResetSessionReleasesMouseLookAndAutorun()
    {
        var calls = new List<string>();
        var movement = new DispatcherMovementInputSource(
            new RuntimeLocalPlayerMovementState());
        movement.HandlePressedAction(InputAction.MovementRunLock);
        var mouseLook = new FakeMouseLook(calls);
        var controller = new GameplayInputFrameController(
            dispatcher: null,
            movement,
            mouseLook,
            new FakeCombat(calls));

        controller.ResetSession();

        Assert.False(movement.AutoRunActive);
        Assert.Equal(["mouse-reset"], calls);
    }

    [Fact]
    public void PointerAndRawDeltaRemainOwnedByMouseLookController()
    {
        var calls = new List<string>();
        var mouseLook = new FakeMouseLook(calls, active: true, consumes: true);
        var controller = new GameplayInputFrameController(
            dispatcher: null,
            new DispatcherMovementInputSource(new RuntimeLocalPlayerMovementState()),
            mouseLook,
            new FakeCombat(calls));

        Assert.True(controller.MouseLookActive);
        Assert.True(controller.HandlePointerAction(
            InputAction.CameraInstantMouseLook,
            ActivationType.Press));
        controller.QueueRawMouseDelta(3f, -2f);
        controller.EndMouseLook();

        Assert.Equal(["mouse-action", "mouse-delta", "mouse-lifecycle-end"], calls);
    }

    private sealed class FakeCombat : ICombatInputFrameController
    {
        private readonly List<string> _calls;
        private readonly bool _consumes;

        public FakeCombat(List<string> calls, bool consumes = false)
        {
            _calls = calls;
            _consumes = consumes;
        }

        public void Tick() => _calls.Add("combat");
        public void HandleMovementInput(InputAction action, ActivationType activation) =>
            _calls.Add("combat-movement");
        public void AbortAutomaticAttack() => _calls.Add("combat-abort");
        public bool HandleInputAction(InputAction action, ActivationType activation)
        {
            _calls.Add("combat-action");
            return _consumes;
        }
    }

    private sealed class FakeMouseLook : IMouseLookInputFrameController
    {
        private readonly List<string> _calls;
        private readonly bool _consumes;

        public FakeMouseLook(
            List<string> calls,
            bool active = false,
            bool consumes = false)
        {
            _calls = calls;
            _consumes = consumes;
            Active = active;
        }

        public bool Active { get; }
        public void Tick() => _calls.Add("mouse-look");
        public bool HandlePointerAction(InputAction action, ActivationType activation)
        {
            _calls.Add("mouse-action");
            return _consumes;
        }
        public void QueueRawDelta(float dx, float dy) => _calls.Add("mouse-delta");
        public void EndAndRestoreCursor() => _calls.Add("mouse-end");
        public void EndForLifecycle() => _calls.Add("mouse-lifecycle-end");
        public void ResetSession() => _calls.Add("mouse-reset");
    }

    private sealed class HeldKeyboard : IKeyboardSource
    {
        private readonly HashSet<Key> _held = [];

        public HeldKeyboard(params Key[] held)
        {
            foreach (Key key in held)
                _held.Add(key);
        }

        public event Action<Key, ModifierMask>? KeyDown;
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => _held.Contains(key);
        public ModifierMask CurrentModifiers => ModifierMask.None;
        public void Press(Key key) => KeyDown?.Invoke(key, ModifierMask.None);
    }

    private sealed class NullMouse : IMouseSource
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
}
