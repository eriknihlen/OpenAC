using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;
using DatReaderWriter;
using DatReaderWriter.Options;
using Silk.NET.Input;

namespace AcDream.App.Tests.Physics;

public sealed partial class PlayerRunSidestepInstalledDatTests
{
    /// <summary>
    /// The stop key puts the forward axis back to ready. A forward key
    /// still held does not move the character again until it is pressed
    /// again.
    /// </summary>
    [InstalledDatFact]
    public void TheStopKeyStopsARunWhileTheForwardKeyIsStillHeld()
    {
        using var rig = new StopRig();
        rig.Keyboard.Press(Key.W);
        rig.Step();
        Vector3 running = rig.Step();
        Assert.True(running.Length() > 1.5f, $"the run moved {running}");

        rig.Keyboard.Press(Key.S);
        rig.Keyboard.Release(Key.S);
        rig.Step();
        Vector3 stopped = rig.Step();

        Assert.True(rig.Dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.True(stopped.Length() < 0.05f, $"still moving after the stop key: {stopped}");
        Assert.Equal(MotionCommand.Ready, rig.Sequencer.Manager.State.Substate);

        // Released and pressed again, the forward key runs again.
        rig.Keyboard.Release(Key.W);
        rig.Step();
        rig.Keyboard.Press(Key.W);
        rig.Step();
        Vector3 again = rig.Step();
        Assert.True(again.Length() > 1.5f, $"the second press moved {again}");
    }

    /// <summary>The stop key does not end a jump being charged.</summary>
    [InstalledDatFact]
    public void TheStopKeyLeavesAChargingJumpCharging()
    {
        using var rig = new StopRig();
        rig.Keyboard.Press(Key.Space);
        rig.Step();
        Assert.True(rig.Controller.JumpCharge.IsCharging);

        rig.Keyboard.Press(Key.S);
        rig.Keyboard.Release(Key.S);
        rig.Step();

        Assert.True(rig.Controller.JumpCharge.IsCharging);
        Assert.False(rig.Controller.IsAirborne);
    }

    private sealed class StopRig : IDisposable
    {
        private readonly DatCollection _dats;
        private readonly RuntimeLocalPlayerMovementState _movement;
        private readonly DispatcherMovementInputSource _input;

        public StopRig()
        {
            _dats = new DatCollection(DatDirectory, DatAccessType.Read);
            (Controller, Sequencer) = CreateWiredController(_dats);
            Controller.Yaw = 0f;
            _movement = new RuntimeLocalPlayerMovementState
            {
                Controller = Controller,
                RunAsDefaultMovementSource = () => true,
            };
            _input = new DispatcherMovementInputSource(_movement);
            Keyboard = new SteeringKeyboard();
            Dispatcher = InputDispatcher.CreateDetached(
                Keyboard, new SteeringMouse(), KeyBindings.RetailDefaults());
            Dispatcher.Fired += (action, activation) =>
            {
                if (activation != ActivationType.Press)
                    return;
                if (RuntimeGameplayInputPriorityTargets.ResolvePressedMovementCommand(action) is { } command)
                    _movement.Execute(command);
                else
                    _input.HandlePressedAction(action);
            };
            Dispatcher.Attach();
            _input.Bind(Dispatcher);
        }

        public PlayerMovementController Controller { get; }
        public AnimationSequencer Sequencer { get; }
        public SteeringKeyboard Keyboard { get; }
        public InputDispatcher Dispatcher { get; }

        public Vector3 Step()
        {
            Vector3 before = Controller.Position;
            for (int i = 0; i < FramesPerStep; i++)
            {
                Dispatcher.Tick();
                Controller.Update(FrameSeconds, _input.Capture());
            }
            return Controller.Position - before;
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
            _movement.Dispose();
            _dats.Dispose();
        }
    }
}
