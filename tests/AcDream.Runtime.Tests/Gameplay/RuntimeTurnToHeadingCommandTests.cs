using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeTurnToHeadingCommandTests
{
    private sealed class Rig
    {
        public required PlayerMovementController Controller { get; init; }
        public required MoveToManager MoveTo { get; init; }
        public int CompletionCount;
        public WeenieError LastCompletionError = WeenieError.None;
        public int CancellationCount;
        public WeenieError LastCancellationError = WeenieError.None;
    }

    private static Rig MakeRig(float startHeadingDegrees = 0f)
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(
            new Vector3(96f, 96f, 50f),
            0x0001u,
            new Vector3(96f, 96f, 50f));
        controller.Yaw = MoveToMath.YawFromHeading(startHeadingDegrees);

        const uint selfGuid = 0x5000000Au;
        EntityPhysicsHost host = null!;
        var moveTo = new MoveToManager(
            controller.Motion,
            stopCompletely: () =>
                controller.StopCompletelyAtPhysicsObjectBoundary(),
            getPosition: () => new Position(
                controller.CellId,
                controller.Position,
                controller.BodyOrientation),
            getHeading: () => MoveToMath.HeadingFromYaw(controller.Yaw),
            setHeading: (h, _) => controller.Yaw =
                MoveToMath.YawFromHeading(h),
            getOwnRadius: () => 0.5f,
            getOwnHeight: () => 1f,
            contact: () => controller.BodyInContact,
            isInterpolating: static () => false,
            getVelocity: () => controller.BodyVelocity,
            getSelfId: () => selfGuid,
            setTarget: (ctx, id, radius, quantum) =>
                host.SetTarget(ctx, id, radius, quantum),
            clearTarget: () => host.ClearTarget(),
            getTargetQuantum: () => host.TargetManager.GetTargetQuantum(),
            setTargetQuantum: quantum =>
                host.TargetManager.SetTargetQuantum(quantum),
            curTime: () => controller.SimTimeSeconds);
        host = new EntityPhysicsHost(
            selfGuid,
            getPosition: () => new Position(
                controller.CellId,
                controller.Position,
                controller.BodyOrientation),
            getVelocity: () => controller.BodyVelocity,
            getRadius: () => 0.5f,
            inContact: () => controller.BodyInContact,
            minterpMaxSpeed: () => controller.Motion.GetMaxSpeed(),
            curTime: () => controller.SimTimeSeconds,
            physicsTimerTime: () => controller.SimTimeSeconds,
            getObjectA: static _ => null,
            handleUpdateTarget: info => moveTo.HandleUpdateTarget(info),
            interruptCurrentMovement: () =>
                moveTo.CancelMoveTo(WeenieError.ActionCancelled));
        controller.MoveTo = moveTo;
        controller.Motion.InterruptCurrentMovement =
            () => moveTo.CancelMoveTo(WeenieError.ActionCancelled);

        var rig = new Rig { Controller = controller, MoveTo = moveTo };
        moveTo.MoveToComplete = err =>
        {
            rig.CompletionCount++;
            rig.LastCompletionError = err;
        };
        moveTo.MoveToCancelled = err =>
        {
            rig.CancellationCount++;
            rig.LastCancellationError = err;
        };
        return rig;
    }

    private static void Step(Rig rig, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            while (rig.Controller.Motion.MotionsPending())
                rig.Controller.Motion.MotionDone(0, true);
            rig.Controller.Update(1f / 30f, new MovementInput());
        }
    }

    private static float Heading(Rig rig) =>
        MoveToMath.HeadingFromYaw(rig.Controller.Yaw);


    [Fact]
    public void CommandInstallsRetailTurnToHeadingNodeWithoutStoppingFirst()
    {
        Rig rig = MakeRig(startHeadingDegrees: 0f);
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(90f));

        Assert.Equal(MovementType.TurnToHeading, rig.MoveTo.MovementTypeState);
        Assert.False(rig.MoveTo.Params.StopCompletelyFlag);
        Assert.False(rig.MoveTo.Params.Sticky);
        // @006b45a3 speed = 1.0; @006b45af not taken → hold key untouched.
        Assert.Equal(1f, rig.MoveTo.Params.Speed);
        Assert.Equal(HoldKey.Invalid, rig.MoveTo.Params.HoldKeyToApply);
        Assert.True(rig.MoveTo.Params.CancelMoveTo);
        Assert.Equal(90f, rig.MoveTo.Params.DesiredHeading, 3);
    }

    [Fact]
    public void RunHoldKeyArgumentAppliesRetailHoldKeyRun()
    {
        Rig rig = MakeRig();
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(45f, applyRunHoldKey: true));

        Assert.Equal(HoldKey.Run, rig.MoveTo.Params.HoldKeyToApply);
    }

    [Theory]
    [InlineData(0f, 90f)]
    [InlineData(0f, 270f)]
    [InlineData(350f, 10f)]
    [InlineData(10f, 350f)]
    [InlineData(180f, 179f)]
    public void HeadingIsReachedWithinRetailArrivalTolerance(
        float start,
        float target)
    {
        Rig rig = MakeRig(start);
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(target));
        Step(rig, 600);

        float reached = Heading(rig);
        float error = MathF.Abs(
            MoveToMath.HeadingDiff(reached, target, MotionCommand.TurnRight));
        if (error > 180f)
            error = 360f - error;
        Assert.True(
            error <= MoveToMath.Epsilon,
            $"heading {reached} off target {target} by {error}°");
        Assert.False(rig.MoveTo.IsMovingTo());
        Assert.Equal(1, rig.CompletionCount);
        Assert.Equal(WeenieError.None, rig.LastCompletionError);
    }

    [Fact]
    public void HeadingAlreadyInsideToleranceCompletesWithoutTurning()
    {
        Rig rig = MakeRig(startHeadingDegrees: 33f);
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(33f));
        Step(rig, 4);

        Assert.Equal(33f, Heading(rig), 3);
        Assert.False(rig.MoveTo.IsMovingTo());
    }


    [Fact]
    public void KeyIntentCancelsAnInFlightTurnToHeading()
    {
        Rig rig = MakeRig(startHeadingDegrees: 0f);
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(180f));
        Step(rig, 2);
        Assert.True(rig.MoveTo.IsMovingTo());
        float partway = Heading(rig);

        rig.Controller.Update(
            1f / 30f,
            new MovementInput(Forward: true, Run: true));

        Assert.False(rig.MoveTo.IsMovingTo());
        Assert.Equal(1, rig.CancellationCount);
        Assert.Equal(WeenieError.ActionCancelled, rig.LastCancellationError);
        Assert.Equal(0, rig.CompletionCount);

        Step(rig, 60);
        Assert.True(
            MathF.Abs(Heading(rig) - partway) < 45f,
            $"heading kept turning after cancel: {partway} → {Heading(rig)}");
    }


    [Fact]
    public void OwnerRefusesWhenNoControllerIsPublished()
    {
        using var movement = new RuntimeLocalPlayerMovementState();

        Assert.False(movement.TurnToHeading(90f));
    }

    [Fact]
    public void OwnerRefusesWhenNoMoveToManagerIsBound()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(
            new Vector3(96f, 96f, 50f),
            0x0001u,
            new Vector3(96f, 96f, 50f));
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };

        Assert.False(movement.TurnToHeading(90f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void OwnerRefusesNonFiniteHeadings(float heading)
    {
        Rig rig = MakeRig();
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.False(movement.TurnToHeading(heading));
        Assert.False(rig.MoveTo.IsMovingTo());
    }

    [Theory]
    [InlineData(450f, 90f)]
    [InlineData(-90f, 270f)]
    [InlineData(-450f, 270f)]
    public void OwnerNormalizesHeadingIntoZeroToThreeSixty(
        float requested,
        float expected)
    {
        Rig rig = MakeRig();
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = rig.Controller,
        };

        Assert.True(movement.TurnToHeading(requested));

        Assert.Equal(expected, rig.MoveTo.Params.DesiredHeading, 3);
    }

}
