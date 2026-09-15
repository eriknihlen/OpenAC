using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Tests.UI.Layout;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;
using DatReaderWriter;
using DatReaderWriter.Options;
using Silk.NET.Input;

namespace AcDream.App.Tests.Physics;

public sealed partial class PlayerRunSidestepInstalledDatTests
{
    [InstalledDatFact]
    public void AutorunLeftStrafeKeepsRunningThroughReleaseUntilStop() =>
        AssertAutorunSteering(Key.Z, 1f);

    [InstalledDatFact]
    public void AutorunRightStrafeKeepsRunningThroughReleaseUntilStop() =>
        AssertAutorunSteering(Key.C, -1f);

    private static void AssertAutorunSteering(Key strafeKey, float lateralSign)
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var (controller, sequencer) = CreateWiredController(dats);
        controller.Yaw = 0f;
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
            RunAsDefaultMovementSource = () => false,
        };
        var input = new DispatcherMovementInputSource(movement);
        var keyboard = new SteeringKeyboard();
        using var dispatcher = InputDispatcher.CreateDetached(
            keyboard, new SteeringMouse(), KeyBindings.RetailDefaults());
        dispatcher.Fired += (action, activation) =>
        {
            if (activation != ActivationType.Press)
                return;
            if (RuntimeGameplayInputPriorityTargets.ResolvePressedMovementCommand(action) is { } command)
                movement.Execute(command);
            else
                input.HandlePressedAction(action);
        };
        dispatcher.Attach();
        input.Bind(dispatcher);

        // Tap Run Lock. Forward is never held: all forward input must come
        // from the latch, including while a strafe key is down.
        keyboard.Press(Key.Q);
        keyboard.Release(Key.Q);
        Assert.True(input.AutoRunActive);
        StepCaptured();
        Vector3 straight = StepCaptured();
        Assert.True(straight.X > 1.5f, $"autorun moved {straight}");
        Assert.Equal(0f, straight.Y, 3);
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));

        keyboard.Press(strafeKey);
        Vector3 diagonal = StepCaptured();
        Assert.True(diagonal.X > 1.5f,
            $"strafe interrupted forward running: displacement={diagonal}, autorun={input.AutoRunActive}");
        Assert.True(diagonal.Y * lateralSign > 1f,
            $"run did not steer to the requested side: {diagonal}");
        Assert.True(input.AutoRunActive);
        Assert.True(input.Capture().Forward);
        Assert.True(input.Capture().Run);
        Assert.Equal(MotionCommand.RunForward, sequencer.Manager.State.Substate);
        Assert.Equal(MotionCommand.SideStepRight,
            Assert.Single(sequencer.Manager.State.Modifiers).Motion);

        keyboard.Release(strafeKey);
        Vector3 released = StepCaptured();
        Assert.True(input.AutoRunActive);
        Assert.True(released.X > 1.5f, $"autorun did not resume straight: {released}");
        Assert.Equal(0f, released.Y, 3);
        Assert.Empty(sequencer.Manager.State.Modifiers);

        keyboard.Press(Key.S);
        keyboard.Release(Key.S);
        Assert.False(input.AutoRunActive);
        Assert.False(input.Capture().Forward);
        Assert.False(input.Capture().Run);

        Vector3 StepCaptured()
        {
            Vector3 before = controller.Position;
            for (int i = 0; i < FramesPerStep; i++)
            {
                dispatcher.Tick();
                controller.Update(FrameSeconds, input.Capture());
            }
            return controller.Position - before;
        }
    }

    private sealed class SteeringKeyboard : IKeyboardSource
    {
        private readonly HashSet<Key> _held = new();
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
        public ModifierMask CurrentModifiers => ModifierMask.None;
        public bool IsHeld(Key key) => _held.Contains(key);
        public void Press(Key key)
        {
            _held.Add(key);
            KeyDown?.Invoke(key, ModifierMask.None);
        }
        public void Release(Key key)
        {
            _held.Remove(key);
            KeyUp?.Invoke(key, ModifierMask.None);
        }
    }

    private sealed class SteeringMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureKeyboard => false;
        public bool WantCaptureMouse => false;
        public bool IsHeld(MouseButton button) => false;
    }
}
