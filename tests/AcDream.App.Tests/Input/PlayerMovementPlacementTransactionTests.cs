using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.Types;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;

namespace AcDream.App.Tests.Input;

public sealed class PlayerMovementPlacementTransactionTests
{
    private const uint PlayerGuid = 0x5000_0001u;
    private const uint TargetGuid = 0x7000_0001u;
    private const uint PriorCell = 0x0101_0101u;
    private const uint DestinationCell = 0x0101_0102u;

    [Fact]
    public void PreparedPosition_DefersRenderRootPublish_CommitArmsLeashOnly()
    {
        PhysicsEngine physics = PhysicsWithCells(PriorCell, DestinationCell);
        physics.UpdatePlayerCurrCell(PriorCell);
        var controller = new PlayerMovementController(physics);
        var hosts = new Dictionary<uint, IPhysicsObjHost>();
        IPhysicsObjHost? Resolve(uint id) => hosts.GetValueOrDefault(id);
        EntityPhysicsHost player = Host(PlayerGuid, Resolve);
        EntityPhysicsHost target = Host(TargetGuid, Resolve);
        hosts.Add(PlayerGuid, player);
        hosts.Add(TargetGuid, target);
        controller.PositionManager = player.PositionManager;
        player.PositionManager.StickTo(TargetGuid, 0.5f, 1.8f);

        controller.PreparePositionForCommit(
            new Vector3(2f, 3f, 4f),
            DestinationCell,
            new Vector3(2f, 3f, 4f));

        Assert.Equal(DestinationCell, controller.CellId);
        Assert.Equal(PriorCell, physics.DataCache!.CellGraph.CurrCell!.Id);
        Assert.Equal(TargetGuid, player.PositionManager.GetStickyObjectId());
        Assert.False(player.PositionManager.IsFullyConstrained());
        Assert.Null(player.PositionManager.Constraint);

        controller.ArmConstraintLeashAtCommittedPlacement();

        // The leash is armed...
        Assert.NotNull(player.PositionManager.Constraint);
        Assert.True(player.PositionManager.Constraint!.IsConstrained);
        Assert.Equal(PriorCell, physics.DataCache.CellGraph.CurrCell!.Id);
        Assert.Equal(TargetGuid, player.PositionManager.GetStickyObjectId());
    }

    private static PhysicsEngine PhysicsWithCells(params uint[] cellIds)
    {
        var physics = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        foreach (uint cellId in cellIds)
        {
            var cellStruct = new CellStruct
            {
                VertexArray = new VertexArray
                {
                    Vertices = new Dictionary<ushort, SWVertex>(),
                },
                Polygons = new Dictionary<ushort, Polygon>(),
                CellBSP = new CellBSPTree
                {
                    Root = new CellBSPNode
                    {
                        Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                    },
                },
            };
            var envCell = new DatEnvCell
            {
                CellPortals = [],
                VisibleCells = [],
            };
            physics.DataCache.CacheCellStruct(
                cellId,
                envCell,
                cellStruct,
                Matrix4x4.Identity);
        }
        return physics;
    }

    private static EntityPhysicsHost Host(
        uint guid,
        Func<uint, IPhysicsObjHost?> resolve) =>
        new(
            guid,
            getPosition: () => new AcDream.Core.Physics.Position(
                PriorCell,
                Vector3.Zero,
                Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 0d,
            physicsTimerTime: () => 0d,
            getObjectA: resolve,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
}
