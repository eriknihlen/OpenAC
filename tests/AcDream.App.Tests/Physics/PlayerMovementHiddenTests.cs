using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Tests.Physics;

public sealed class PlayerMovementHiddenTests
{
    [Fact]
    public void TickHidden_PositionManagerMovesRootAndEffectPoseWithoutPhysicsInput()
    {
        const uint playerGuid = 0x50000001u;
        const uint targetGuid = 0x50000002u;
        const uint cellId = 0x01010001u;

        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(Vector3.Zero, cellId, Vector3.Zero);

        var hosts = new Dictionary<uint, IPhysicsObjHost>();
        EntityPhysicsHost? playerHost = null;
        playerHost = new EntityPhysicsHost(
            playerGuid,
            () => controller.CellPosition,
            () => controller.BodyVelocity,
            () => 0.48f,
            () => controller.BodyInContact,
            () => 3f,
            () => 0.1,
            () => 0.1,
            id => hosts.GetValueOrDefault(id),
            info => playerHost!.PositionManager.HandleUpdateTarget(info),
            () => { });
        var targetPosition = new Position(
            cellId,
            new Vector3(5f, 0f, 0f),
            Quaternion.Identity);
        var targetHost = new EntityPhysicsHost(
            targetGuid,
            () => targetPosition,
            () => Vector3.Zero,
            () => 0.48f,
            () => true,
            () => null,
            () => 0.1,
            () => 0.1,
            id => hosts.GetValueOrDefault(id),
            _ => { },
            () => { });
        hosts.Add(playerGuid, playerHost);
        hosts.Add(targetGuid, targetHost);
        controller.PositionManager = playerHost.PositionManager;
        playerHost.PositionManager.StickTo(targetGuid, radius: 0.48f, height: 1.835f);

        var entity = new WorldEntity
        {
            Id = 7u,
            ServerGuid = playerGuid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            ParentCellId = cellId,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(entity, Array.Empty<Matrix4x4>());

        MovementResult result = controller.TickHidden(0.1f);
        entity.SetPosition(result.Position);
        entity.ParentCellId = result.CellId;
        Assert.True(poses.UpdateRoot(entity));

        Assert.InRange(result.Position.X, 0.01f, 1.5f);
        Assert.Equal(0f, result.Position.Z);
        Assert.Equal(Vector3.Zero, controller.BodyVelocity);
        Assert.True(poses.TryGetRootPose(entity.Id, out Matrix4x4 root));
        Assert.Equal(result.Position, root.Translation);
    }
}
