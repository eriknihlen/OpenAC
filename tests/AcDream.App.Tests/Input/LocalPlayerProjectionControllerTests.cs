using System.Numerics;
using AcDream.App.Input;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Tests.Input;

public sealed class LocalPlayerProjectionControllerTests
{
    [Fact]
    public void Project_PreservesCompleteAuthoritativeBodyQuaternion()
    {
        const uint cellId = 0x01010001u;
        Quaternion complete = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.4f));
        var movement = new PlayerMovementController(new PhysicsEngine());
        movement.SeedPlacementForTest(Vector3.Zero, cellId, Vector3.Zero);
        movement.SetBodyOrientation(complete);
        var entity = new WorldEntity
        {
            Id = 7u,
            ServerGuid = 0x50000001u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var projection = new LocalPlayerProjectionController(new TestProjectionRuntime(
            () => entity,
            () => 1,
            () => 1,
            (_, _) => { },
            (_, _) => { },
            _ => true,
            _ => { }));

        projection.Project(
            movement,
            movement.CapturePresentationResult(),
            hidden: false);

        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                complete,
                Quaternion.Normalize(entity.Rotation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void Project_AlreadyPendingChangedPose_DoesNotResurrectShadow()
    {
        const uint cellId = 0x02020001u;
        var movement = new PlayerMovementController(new PhysicsEngine());
        movement.SeedPlacementForTest(new Vector3(12f, 8f, 3f), cellId, Vector3.Zero);
        var entity = new WorldEntity
        {
            Id = 8u,
            ServerGuid = 0x50000001u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var order = new List<string>();
        var projection = new LocalPlayerProjectionController(new TestProjectionRuntime(
            () => entity,
            () => 2,
            () => 2,
            (_, _) => order.Add("shadow"),
            (_, _) => order.Add("rebucket"),
            _ => false,
            _ => order.Add("suspend")));

        projection.Project(
            movement,
            movement.CapturePresentationResult(),
            hidden: false);

        Assert.Equal(["rebucket", "suspend"], order);
        Assert.Equal(movement.Position, entity.Position);
        Assert.Equal(cellId, entity.ParentCellId);
    }

    [Fact]
    public void Project_PendingToLoaded_RebucketsBeforeStationaryShadowRestore()
    {
        const uint cellId = 0x02020001u;
        var movement = new PlayerMovementController(new PhysicsEngine());
        movement.SeedPlacementForTest(new Vector3(12f, 8f, 3f), cellId, Vector3.Zero);
        var entity = new WorldEntity
        {
            Id = 9u,
            ServerGuid = 0x50000001u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = movement.Position,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        bool visible = false;
        var order = new List<string>();
        var projection = new LocalPlayerProjectionController(new TestProjectionRuntime(
            () => entity,
            () => 2,
            () => 2,
            (_, _) => order.Add("shadow"),
            (_, _) =>
            {
                order.Add("rebucket");
                visible = true;
            },
            _ => visible,
            _ => order.Add("suspend")));

        projection.Project(
            movement,
            movement.CapturePresentationResult(),
            hidden: false);

        Assert.Equal(["rebucket", "shadow"], order);
    }

    private sealed class TestProjectionRuntime(
        Func<WorldEntity?> resolveEntity,
        Func<int> liveCenterX,
        Func<int> liveCenterY,
        Action<WorldEntity, uint> syncShadow,
        Action<uint, uint> rebucket,
        Func<WorldEntity, bool> isCurrentVisibleProjection,
        Action<WorldEntity> suspendShadow) : ILocalPlayerProjectionRuntime
    {
        public WorldEntity? ResolveEntity() => resolveEntity();
        public int LiveCenterX => liveCenterX();
        public int LiveCenterY => liveCenterY();
        public void SyncShadow(WorldEntity entity, uint cellId) =>
            syncShadow(entity, cellId);
        public void Rebucket(uint serverGuid, uint landblockId) =>
            rebucket(serverGuid, landblockId);
        public bool IsCurrentVisibleProjection(WorldEntity entity) =>
            isCurrentVisibleProjection(entity);
        public void SuspendShadow(WorldEntity entity) => suspendShadow(entity);
    }
}
