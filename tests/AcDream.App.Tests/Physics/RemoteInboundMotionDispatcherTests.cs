using AcDream.App.Physics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Tests.Physics;

public sealed class RemoteInboundMotionDispatcherTests
{
    [Fact]
    public void AnimationlessTypeZero_UsesCompleteRetailPacketFunnel()
    {
        var body = new PhysicsBody
        {
            TransientState = TransientStateFlags.Contact
                | TransientStateFlags.OnWalkable
                | TransientStateFlags.Active,
        };
        var motion = new MotionInterpreter(body);
        var movement = new MovementManager(motion);
        var calls = new List<string>();
        motion.InterruptCurrentMovement = () => calls.Add("interrupt");
        motion.UnstickFromObject = () => calls.Add("unstick");
        var dispatcher = new RemoteInboundMotionDispatcher(
            (owner, cellId, update) =>
            {
                Assert.Same(movement, owner);
                Assert.Equal(0x01010001u, cellId);
                calls.Add("route");
                return false;
            },
            (host, stickyGuid) =>
            {
                Assert.Null(host);
                Assert.Equal(0x70000099u, stickyGuid);
                calls.Add("stick");
            });
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x3F,
            ForwardCommand: 0x0007,
            ForwardSpeed: 2.5f,
            SideStepCommand: 0x000F,
            SideStepSpeed: 0.75f,
            TurnCommand: 0x000D,
            TurnSpeed: 1.25f,
            MovementType: 0,
            StickyObjectGuid: 0x70000099u,
            StandingLongJump: true);
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x70000001u,
            MotionState: wire,
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        RemoteInboundMotionDispatchResult result = dispatcher.Apply(
            update,
            movement,
            animationSink: null,
            host: null,
            cellId: 0x01010001u,
            fallbackForwardClass: 0x41000000u);

        Assert.False(result.RoutedMoveTo);
        Assert.True(result.AppliedInterpretedState);
        Assert.True(result.ForwardCommandChanged);
        Assert.Equal(0x8000003Fu, motion.InterpretedState.CurrentStyle);
        Assert.Equal(0x44000007u, motion.InterpretedState.ForwardCommand);
        Assert.Equal(2.5f, motion.InterpretedState.ForwardSpeed);
        Assert.Equal(0x6500000Fu, motion.InterpretedState.SideStepCommand);
        Assert.Equal(0.75f, motion.InterpretedState.SideStepSpeed);
        Assert.Equal(0x6500000Du, motion.InterpretedState.TurnCommand);
        Assert.Equal(1.25f, motion.InterpretedState.TurnSpeed);
        Assert.True(motion.StandingLongJump);
        Assert.Equal("interrupt", calls[0]);
        Assert.Equal("unstick", calls[1]);
        Assert.Contains("route", calls);
        Assert.Equal("stick", calls[^1]);
    }

    [Fact]
    public void UnknownMovementType_RunsHeadButDoesNotEnterTypeZeroFunnel()
    {
        var body = new PhysicsBody();
        var motion = new MotionInterpreter(body);
        var movement = new MovementManager(motion);
        int interrupts = 0;
        int unsticks = 0;
        motion.InterruptCurrentMovement = () => interrupts++;
        motion.UnstickFromObject = () => unsticks++;
        var dispatcher = new RemoteInboundMotionDispatcher(
            (_, _, _) => false,
            (_, _) => throw new InvalidOperationException(
                "Unknown movement type reached the type-zero tail."));
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x70000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: 0x3E,
                ForwardCommand: 0x0007,
                MovementType: 5),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        RemoteInboundMotionDispatchResult result = dispatcher.Apply(
            update,
            movement,
            animationSink: null,
            host: null,
            cellId: 0x01010001u,
            fallbackForwardClass: 0x41000000u);

        Assert.False(result.RoutedMoveTo);
        Assert.False(result.AppliedInterpretedState);
        Assert.True(interrupts >= 1);
        Assert.Equal(1, unsticks);
        Assert.Equal(0x41000003u, motion.InterpretedState.ForwardCommand);
    }

    [Fact]
    public void SupersededDuringInterrupt_StopsBeforeEveryLaterPacketSideEffect()
    {
        var body = new PhysicsBody();
        var motion = new MotionInterpreter(body);
        var movement = new MovementManager(motion);
        bool current = true;
        int unsticks = 0;
        int routes = 0;
        int sticks = 0;
        motion.InterruptCurrentMovement = () => current = false;
        motion.UnstickFromObject = () => unsticks++;
        var dispatcher = new RemoteInboundMotionDispatcher(
            (_, _, _) =>
            {
                routes++;
                return false;
            },
            (_, _) => sticks++);
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x70000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: 0x3F,
                ForwardCommand: 0x0007,
                MovementType: 0,
                StickyObjectGuid: 0x70000099u,
                StandingLongJump: true),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        RemoteInboundMotionDispatchResult result = dispatcher.Apply(
            update,
            movement,
            animationSink: null,
            host: null,
            cellId: 0x01010001u,
            fallbackForwardClass: 0x41000000u,
            isCurrent: () => current);

        Assert.True(result.Superseded);
        Assert.False(result.AppliedInterpretedState);
        Assert.Equal(0, unsticks);
        Assert.Equal(0, routes);
        Assert.Equal(0, sticks);
        Assert.False(motion.StandingLongJump);
    }
}
