using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Tests.Interaction;

public sealed class PlayerInteractionMovementSinkTests
{
    private const uint Player = 0x5000_0001u;
    private const uint Target = 0x7000_0001u;
    private const uint Cell = 0x0101_0001u;

    [Fact]
    public void MissingPlayerDoesNotArmTheIntent()
    {
        var completions = new PlayerApproachCompletionState();
        var sink = new PlayerInteractionMovementSink(() => null, completions);
        bool armed = false;

        Assert.False(sink.BeginApproach(Approach(closeRange: true), _ => armed = true));
        Assert.False(armed);
    }

    [Theory]
    [InlineData(true, MovementType.TurnToObject)]
    [InlineData(false, MovementType.MoveToObject)]
    public void ProductionSinkCancelsThenArmsAndInstallsExactMovement(
        bool closeRange,
        MovementType expectedType)
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(Vector3.Zero, Cell, Vector3.Zero);
        bool nonAutonomousAtTargetInstall = false;
        int cancellations = 0;
        double targetQuantum = 0d;
        var moveTo = new MoveToManager(
            controller.Motion,
            stopCompletely: () => { },
            getPosition: () => controller.CellPosition,
            getHeading: () => MoveToMath.HeadingFromYaw(controller.Yaw),
            setHeading: (heading, _) => controller.Yaw = MoveToMath.YawFromHeading(heading),
            getOwnRadius: () => 0.4f,
            getOwnHeight: () => 1.8f,
            contact: () => true,
            isInterpolating: () => false,
            getVelocity: () => Vector3.Zero,
            getSelfId: () => Player,
            setTarget: (_, topLevelId, radius, quantum) =>
            {
                Assert.Equal(Target, topLevelId);
                Assert.Equal(0.5f, radius);
                targetQuantum = quantum;
                nonAutonomousAtTargetInstall =
                    controller.Motion.PhysicsObj?.LastMoveWasAutonomous == false;
            },
            clearTarget: () => { },
            getTargetQuantum: () => targetQuantum,
            setTargetQuantum: quantum => targetQuantum = quantum);
        moveTo.MoveToCancelled = _ => cancellations++;
        controller.MoveTo = moveTo;

        moveTo.MoveToPosition(
            new Position(Cell, new Vector3(2f, 0f, 0f), Quaternion.Identity),
            new MovementParameters { UseSpheres = false });
        bool armedAfterCancellation = false;
        var completions = new PlayerApproachCompletionState();
        _ = completions.BeginControllerLifetime();
        var sink = new PlayerInteractionMovementSink(
            () => controller,
            completions);

        Assert.True(sink.BeginApproach(
            Approach(closeRange),
            _ => armedAfterCancellation = cancellations == 1 && !moveTo.IsMovingTo()));

        Assert.True(armedAfterCancellation);
        Assert.True(nonAutonomousAtTargetInstall);
        Assert.Equal(expectedType, moveTo.MovementTypeState);
        Assert.Equal(Target, moveTo.SoughtObjectId);
        Assert.Equal(Target, moveTo.TopLevelObjectId);
        Assert.Equal(closeRange ? 0f : 0.75f, moveTo.SoughtObjectRadius);
        Assert.Equal(closeRange ? 0f : 2.25f, moveTo.SoughtObjectHeight);
        Assert.Equal(0.6f, moveTo.Params.DistanceToObject);
        Assert.Equal(!closeRange, moveTo.Params.CanCharge);
    }

    private static InteractionApproach Approach(bool closeRange)
    {
        var entity = new WorldEntity
        {
            Id = 101u,
            ServerGuid = Target,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = new Vector3(5f, 0f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = [],
        };
        return new InteractionApproach(
            new WorldInteractionTarget(Target, entity.Id, entity),
            new PlayerInteractionPose(Cell, Vector3.Zero),
            UseRadius: 0.6f,
            IsCloseRange: closeRange,
            CanCharge: !closeRange,
            TargetRadius: 0.75f,
            TargetHeight: 2.25f);
    }
}
