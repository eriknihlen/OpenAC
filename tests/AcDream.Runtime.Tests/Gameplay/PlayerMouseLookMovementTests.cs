using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class PlayerMouseLookMovementTests
{
    [Fact]
    public void KeyboardMotionEdges_StillRequestImmediateMovementEvents()
    {
        var controller = CreateController();

        MovementResult pressed = controller.Update(
            1f / 60f,
            new MovementInput(Forward: true));
        MovementResult held = controller.Update(
            1f / 60f,
            new MovementInput(Forward: true));
        MovementResult released = controller.Update(
            1f / 60f,
            new MovementInput());

        Assert.True(pressed.ShouldSendMovementEvent);
        Assert.False(held.ShouldSendMovementEvent);
        Assert.True(released.ShouldSendMovementEvent);
    }

    [Fact]
    public void MouseAdjustment_UsesTurnMotionInsteadOfDirectYawMutation()
    {
        var controller = CreateController();
        Assert.True(controller.BeginMouseLook(new MovementInput()));
        controller.NoteMovementSent(controller.SimTimeSeconds, mouseLookEvent: false);

        float yawBefore = controller.Yaw;
        controller.SubmitMouseTurnAdjustment(-0.4f, new MovementInput());
        MovementResult turning = controller.Update(
            PhysicsBody.MinQuantum + 0.001f,
            new MovementInput());

        Assert.Equal(MotionCommand.TurnRight, turning.TurnCommand);
        Assert.Equal(0.8f, turning.TurnSpeed);
        Assert.True(controller.Yaw < yawBefore);
        Assert.False(turning.ShouldSendMovementEvent);
        Assert.True(turning.TurnUsesRunHold);

        MovementResult held = controller.Update(1f / 60f, new MovementInput());
        Assert.Equal(MotionCommand.TurnRight, held.TurnCommand);

        controller.StopMouseDrift(new MovementInput());
        MovementResult stopped = controller.Update(1f / 60f, new MovementInput());
        Assert.Null(stopped.TurnCommand);
        Assert.False(stopped.ShouldSendMovementEvent);
    }

    [Fact]
    public void MouseLook_ReportsStartCadenceAndStopWithoutPerSampleFlooding()
    {
        var controller = CreateController();
        Assert.True(controller.BeginMouseLook(new MovementInput()));
        MovementResult begin = controller.CaptureMovementResult(mouseLookEvent: false);
        Assert.True(begin.ShouldSendMovementEvent);
        Assert.False(begin.IsMouseLookMovementEvent);
        controller.NoteMovementSent(controller.SimTimeSeconds, mouseLookEvent: false);

        controller.SubmitMouseTurnAdjustment(0.5f, new MovementInput());
        MovementResult sample = controller.Update(0.01f, new MovementInput());
        Assert.Equal(MotionCommand.TurnLeft, sample.TurnCommand);
        Assert.False(sample.ShouldSendMovementEvent);

        controller.Update(
            PlayerMovementController.MouseMovementEventInterval,
            new MovementInput());
        controller.SubmitMouseTurnAdjustment(0.5f, new MovementInput());
        MovementResult cadence = controller.Update(
            0.001f,
            new MovementInput());
        Assert.True(cadence.ShouldSendMovementEvent);
        Assert.True(cadence.IsMouseLookMovementEvent);
        controller.NoteMovementSent(
            controller.SimTimeSeconds,
            mouseLookEvent: true);

        Assert.True(controller.EndMouseLook(new MovementInput()));
        MovementResult ended = controller.CaptureMovementResult(mouseLookEvent: false);
        Assert.True(ended.ShouldSendMovementEvent);
        Assert.False(ended.IsMouseLookMovementEvent);
        Assert.Null(ended.TurnCommand);
    }

    [Fact]
    public void MouseMovementEvent_IsRetriedUntilSuccessfulSendIsAcknowledged()
    {
        var controller = CreateController();
        controller.BeginMouseLook(new MovementInput());
        controller.Update(
            PlayerMovementController.MouseMovementEventInterval + 0.001f,
            new MovementInput());
        controller.SubmitMouseTurnAdjustment(0.4f, new MovementInput());

        MovementResult first = controller.Update(0.01f, new MovementInput(Run: true));
        MovementResult retry = controller.Update(0.01f, new MovementInput(Run: true));

        Assert.True(first.ShouldSendMovementEvent);
        Assert.True(first.IsMouseLookMovementEvent);
        Assert.True(retry.ShouldSendMovementEvent);

        controller.NoteMovementSent(
            controller.SimTimeSeconds,
            mouseLookEvent: true);
        MovementResult acknowledged = controller.Update(
            0.01f,
            new MovementInput(Run: true));
        Assert.False(acknowledged.ShouldSendMovementEvent);
    }

    [Fact]
    public void MouseLook_RapidPressReleaseCanBeCapturedSynchronously()
    {
        var controller = CreateController();
        var input = new MovementInput(Run: true);

        Assert.True(controller.BeginMouseLook(input));
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);
        Assert.True(controller.EndMouseLook(input));
        MovementResult release = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.True(press.ShouldSendMovementEvent);
        Assert.False(press.IsMouseLookMovementEvent);
        Assert.True(release.ShouldSendMovementEvent);
        Assert.False(release.IsMouseLookMovementEvent);
    }

    [Fact]
    public void MouseLook_CadenceUsesStrictHalfSecondBoundary()
    {
        var controller = CreateController();
        controller.BeginMouseLook(new MovementInput());
        controller.NoteMovementSent(nowSeconds: 0f, mouseLookEvent: true);

        controller.SubmitMouseTurnAdjustment(0.4f, new MovementInput());
        MovementResult boundary = controller.Update(
            PlayerMovementController.MouseMovementEventInterval,
            new MovementInput());
        Assert.False(boundary.ShouldSendMovementEvent);

        controller.SubmitMouseTurnAdjustment(0.4f, new MovementInput());
        MovementResult justOver = controller.Update(
            0.0001f,
            new MovementInput());
        Assert.True(justOver.ShouldSendMovementEvent);
        Assert.True(justOver.IsMouseLookMovementEvent);
    }

    [Fact]
    public void MouseTurn_UsesPerAxisRunHoldEvenInWalkMode()
    {
        var controller = CreateController();
        controller.BeginMouseLook(new MovementInput());
        controller.NoteMovementSent(nowSeconds: 0f, mouseLookEvent: true);
        controller.SubmitMouseTurnAdjustment(-0.4f, new MovementInput());

        MovementResult turning = controller.Update(
            0.01f,
            new MovementInput(Run: false));

        Assert.Equal(MotionCommand.TurnRight, turning.TurnCommand);
        Assert.False(turning.IsRunning);
        Assert.True(turning.TurnUsesRunHold);
        Assert.Equal(HoldKey.Run, controller.Motion.RawState.TurnHoldKey);
    }

    [Fact]
    public void MouseLookRelease_SynchronouslyRestoresHeldKeyboardTurn()
    {
        var controller = CreateController();
        controller.Update(
            0.01f,
            new MovementInput(TurnLeft: true, Run: true));
        var held = new MovementInput(TurnLeft: true, Run: true);
        controller.BeginMouseLook(held);
        controller.NoteMovementSent(controller.SimTimeSeconds, mouseLookEvent: false);
        controller.SubmitMouseTurnAdjustment(-0.4f, held);
        controller.Update(0.01f, held);

        controller.EndMouseLook(held);
        MovementResult release = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Equal(MotionCommand.TurnLeft, release.TurnCommand);
        Assert.Equal(1f, release.TurnSpeed);
        Assert.False(release.TurnUsesRunHold);
    }

    [Fact]
    public void MouseLook_RemapMovesHeldKeyboardTurnToSidestepAndBack()
    {
        var controller = CreateController();
        var held = new MovementInput(TurnLeft: true, Run: true);
        controller.Update(0.01f, held);

        Assert.True(controller.BeginMouseLook(held));
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Null(press.TurnCommand);
        Assert.Equal(MotionCommand.SideStepLeft, press.SidestepCommand);
        Assert.Equal(HoldKey.Run, controller.Motion.RawState.SidestepHoldKey);

        Assert.True(controller.EndMouseLook(held));
        MovementResult release = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Null(release.SidestepCommand);
        Assert.Equal(MotionCommand.TurnLeft, release.TurnCommand);
        Assert.False(release.TurnUsesRunHold);
    }

    [Fact]
    public void MouseLookEntry_PreservesHeldForwardAndRunAfterTakingControl()
    {
        var controller = CreateController();
        var held = new MovementInput(Forward: true, Run: true);

        Assert.True(controller.BeginMouseLook(held));
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Equal(MotionCommand.WalkForward, press.ForwardCommand);
        Assert.True(press.IsRunning);
        Assert.Equal(HoldKey.Run, controller.Motion.RawState.CurrentHoldKey);
    }

    [Fact]
    public void MouseLookEntry_AlreadyAutonomousSameFrameForwardEdgeIsNotLost()
    {
        var controller = CreateController();
        controller.Update(0.01f, new MovementInput(Forward: true));
        controller.Update(0.01f, new MovementInput());
        var justPressed = new MovementInput(Forward: true, Run: true);

        controller.BeginMouseLook(justPressed);
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Equal(MotionCommand.WalkForward, press.ForwardCommand);
        Assert.True(press.IsRunning);
        Assert.Equal(HoldKey.Run, controller.Motion.RawState.CurrentHoldKey);
    }

    [Fact]
    public void MouseLookEntry_AlreadyAutonomousSameFrameRunChangeIsNotLost()
    {
        var controller = CreateController();
        controller.Update(0.01f, new MovementInput(Forward: true));
        controller.Update(0.01f, new MovementInput());

        controller.BeginMouseLook(new MovementInput(Run: true));
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);
        RawMotionState wire = LocalPlayerOutboundController.BuildRawMotionState(press);

        Assert.Equal(HoldKey.Run, controller.Motion.RawState.CurrentHoldKey);
        Assert.True(press.IsRunning);
        Assert.Equal(HoldKey.Run, wire.CurrentHoldKey);
        Assert.Equal(HoldKey.Invalid, wire.ForwardHoldKey);
        Assert.Equal(HoldKey.Invalid, wire.SidestepHoldKey);
        Assert.Equal(HoldKey.Invalid, wire.TurnHoldKey);
    }

    [Fact]
    public void MouseLookEntry_ServerControlledHeldTurnNeverQueuesWrongAxis()
    {
        var controller = CreateController();

        controller.BeginMouseLook(new MovementInput(TurnLeft: true));

        Assert.DoesNotContain(
            controller.Motion.PendingMotions,
            node => node.Motion == MotionCommand.TurnLeft);
        Assert.Equal(MotionCommand.SideStepLeft,
            controller.Motion.RawState.SidestepCommand);
    }

    [Fact]
    public void MouseLookExit_ServerControlledHeldTurnNeverQueuesRemappedAxis()
    {
        var controller = CreateController();
        var held = new MovementInput(TurnLeft: true);
        controller.BeginMouseLook(held);
        DrainPendingMotions(controller);
        controller.SetLastMoveWasAutonomous(false);

        controller.EndMouseLook(held);

        Assert.DoesNotContain(
            controller.Motion.PendingMotions,
            node => node.Motion == MotionCommand.SideStepLeft);
        Assert.Equal(MotionCommand.TurnLeft, controller.Motion.RawState.TurnCommand);
    }

    [Fact]
    public void MouseLook_RemapPressedAfterEntryAppearsInOutboundResult()
    {
        var controller = CreateController();
        controller.BeginMouseLook(new MovementInput());

        MovementResult result = controller.Update(
            0.01f,
            new MovementInput(TurnLeft: true));

        Assert.Equal(MotionCommand.SideStepLeft, result.SidestepCommand);
        Assert.Null(result.TurnCommand);
        Assert.True(result.SidestepUsesRunHold);
        Assert.False(result.IsRunning);
        Assert.True(result.ShouldSendMovementEvent);
    }

    [Fact]
    public void MouseLook_WalkModeRemapCarriesPerAxisRunHold()
    {
        var controller = CreateController();
        var held = new MovementInput(TurnRight: true, Run: false);

        controller.BeginMouseLook(held);
        MovementResult press = controller.CaptureMovementResult(mouseLookEvent: false);
        RawMotionState wire = LocalPlayerOutboundController.BuildRawMotionState(press);

        Assert.Equal(MotionCommand.SideStepRight, press.SidestepCommand);
        Assert.True(press.SidestepUsesRunHold);
        Assert.False(press.IsRunning);
        Assert.Equal(HoldKey.Run, controller.Motion.RawState.SidestepHoldKey);
        Assert.Equal(HoldKey.None, wire.CurrentHoldKey);
        Assert.Equal(HoldKey.Run, wire.SidestepHoldKey);
    }

    [Fact]
    public void MouseLook_MouseHandlerRetakesServerControlWithoutDroppingHeldInput()
    {
        var controller = CreateController();
        var held = new MovementInput(Forward: true, TurnLeft: true, Run: true);
        controller.BeginMouseLook(held);
        controller.SetLastMoveWasAutonomous(false);

        controller.SubmitMouseTurnAdjustment(-0.4f, held);
        MovementResult result = controller.Update(0.01f, held);

        Assert.Equal(MotionCommand.WalkForward, result.ForwardCommand);
        Assert.Equal(MotionCommand.SideStepLeft, result.SidestepCommand);
        Assert.Equal(MotionCommand.TurnRight, result.TurnCommand);
        Assert.True(result.IsRunning);
        Assert.True(result.SidestepUsesRunHold);
        Assert.True(result.TurnUsesRunHold);
    }

    [Fact]
    public void MouseLookRelease_UsesCurrentInputSnapshotInsteadOfPreviousFrame()
    {
        var controller = CreateController();
        var held = new MovementInput(Forward: true, TurnLeft: true, Run: true);
        controller.Update(0.01f, held);
        controller.BeginMouseLook(held);

        Assert.True(controller.EndMouseLook(new MovementInput()));
        MovementResult release = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Null(release.ForwardCommand);
        Assert.Null(release.SidestepCommand);
        Assert.Null(release.TurnCommand);
        Assert.False(release.IsRunning);
    }

    [Fact]
    public void MouseLook_CannotBeginInPortalSpace()
    {
        var controller = CreateController();
        controller.State = PlayerState.PortalSpace;

        Assert.False(controller.BeginMouseLook(new MovementInput(Forward: true)));

        MovementResult result = controller.CaptureMovementResult(mouseLookEvent: false);
        Assert.Null(result.ForwardCommand);
        Assert.Null(result.SidestepCommand);
        Assert.Null(result.TurnCommand);
    }

    [Fact]
    public void AttackPreparation_StopsMouseTurnAndPublishesFinalHeading()
    {
        var controller = CreateController();
        controller.BeginMouseLook(new MovementInput());
        controller.Update(0.01f, new MovementInput());
        controller.SubmitMouseTurnAdjustment(-0.5f, new MovementInput());
        MovementResult turning = controller.Update(1f / 60f, new MovementInput());
        Assert.Equal(MotionCommand.TurnRight, turning.TurnCommand);

        Assert.True(controller.PrepareForAttackRequest());
        float finalYaw = controller.Yaw;
        MovementResult stopped = controller.CaptureMovementResult(mouseLookEvent: false);

        Assert.Equal(finalYaw, controller.Yaw);
        Assert.Null(stopped.TurnCommand);
        Assert.True(stopped.ShouldSendMovementEvent);
    }

    [Fact]
    public void AttackPreparation_DoesNotInterruptServerControlledMovement()
    {
        var controller = CreateController();
        controller.SetLastMoveWasAutonomous(false);

        Assert.False(controller.PrepareForAttackRequest());
        MovementResult result = controller.Update(
            0.01f,
            new MovementInput(Run: true));

        Assert.False(result.ShouldSendMovementEvent);
    }

    [Fact]
    public void AttackPreparation_DefaultServerControlledStateIsNoOp()
    {
        var controller = CreateController();

        Assert.False(controller.PrepareForAttackRequest());
    }

    [Fact]
    public void PersistentCommandRetakesTurnAfterServerPostureAcknowledgement()
    {
        var controller = CreateController();
        var command = new MovementInput(
            TurnRight: true,
            IsPersistentCommand: true);

        MovementResult started = controller.Update(1f / 60f, command);
        Assert.Equal(MotionCommand.TurnRight, started.TurnCommand);

        controller.SetLastMoveWasAutonomous(false);
        controller.Motion.MoveToInterpretedState(new InboundInterpretedState
        {
            CurrentStyle = 0x80000049u,
            ForwardCommand = MotionCommand.Ready,
            ForwardSpeed = 1f,
            TurnCommand = 0u,
            TurnSpeed = 1f,
        });
        Assert.Equal(0u, controller.Motion.InterpretedState.TurnCommand);

        MovementResult resumed = controller.Update(1f / 60f, command);

        Assert.True(resumed.ShouldSendMovementEvent);
        Assert.Equal(
            MotionCommand.TurnRight,
            controller.Motion.InterpretedState.TurnCommand);
        Assert.Equal(MotionCommand.TurnRight, resumed.TurnCommand);
    }

    [Fact]
    public void PhysicalHeldKeyKeepsRetailEdgeBehaviorAfterServerPosture()
    {
        var controller = CreateController();
        var physical = new MovementInput(TurnRight: true);
        _ = controller.Update(1f / 60f, physical);

        controller.SetLastMoveWasAutonomous(false);
        controller.Motion.MoveToInterpretedState(new InboundInterpretedState
        {
            CurrentStyle = 0x80000049u,
            ForwardCommand = MotionCommand.Ready,
            ForwardSpeed = 1f,
            TurnCommand = 0u,
            TurnSpeed = 1f,
        });

        MovementResult held = controller.Update(1f / 60f, physical);

        Assert.False(held.ShouldSendMovementEvent);
        Assert.Equal(0u, controller.Motion.InterpretedState.TurnCommand);
    }

    private static PlayerMovementController CreateController()
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

    private static void DrainPendingMotions(PlayerMovementController controller)
    {
        int remaining = controller.Motion.PendingMotions.Count();
        for (int i = 0; i < remaining; i++)
            controller.Motion.MotionDone(0u, success: true);
    }
}
