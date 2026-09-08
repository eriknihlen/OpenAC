using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class SpawnPlacementSettlerTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;
    private const float Radius = 0.48f;
    private const float Height = 1.835f;

    [Fact]
    public void FloorWithinFirstGravityFrame_CommitsGroundContactOnce()
    {
        PhysicsEngine engine = BuildFlatEngine();
        PhysicsBody body = AirborneBody(new Vector3(12f, 12f, 0.25f));
        int hitGround = 0;
        int leaveGround = 0;

        bool settled = SpawnPlacementSettler.TrySettle(
            engine,
            body,
            body.Position,
            Cell,
            Radius,
            Height,
            ObjectInfoState.EdgeSlide,
            movingEntityId: 0x70000001u,
            () => hitGround++,
            () => leaveGround++);

        Assert.True(settled);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.Equal(1, hitGround);
        Assert.Equal(0, leaveGround);
    }

    [Fact]
    public void MissingCell_CanRetryAfterHydrationWithoutReconstructingBody()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        PhysicsBody body = AirborneBody(new Vector3(12f, 12f, 0.25f));
        Vector3 originalPosition = body.Position;

        Assert.False(TrySettle(engine, body));
        Assert.False(body.InContact);
        Assert.Equal(originalPosition, body.Position);

        AddFlatLandblock(engine);

        Assert.True(TrySettle(engine, body));
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
    }

    [Fact]
    public void FloorOutsideSettleDistance_LeavesGenuinelyAirborneBodyUnchanged()
    {
        PhysicsEngine engine = BuildFlatEngine();
        PhysicsBody body = AirborneBody(new Vector3(12f, 12f, 1.25f));
        Vector3 originalPosition = body.Position;

        Assert.False(TrySettle(engine, body));

        Assert.False(body.InContact);
        Assert.False(body.OnWalkable);
        Assert.Equal(originalPosition, body.Position);
    }

    [Fact]
    public void SettleLeavingAnEnvCell_AdoptsTheTransitionsResolvedOutdoorCell()
    {
        const uint StaleEnvCell = Landblock | 0x0100u;
        PhysicsEngine engine = BuildFlatEngine();
        PhysicsBody body = AirborneBody(new Vector3(12f, 12f, 0.25f));
        body.StageDormantCellFrame(
            StaleEnvCell,
            body.Position,
            body.Position);
        Assert.Equal(StaleEnvCell, body.CellPosition.ObjCellId);

        bool settled = SpawnPlacementSettler.TrySettle(
            engine,
            body,
            body.Position,
            Cell,
            Radius,
            Height,
            ObjectInfoState.EdgeSlide,
            movingEntityId: 0x70000001u,
            static () => { },
            static () => { });

        Assert.True(settled);
        Assert.True(body.InContact);

        Assert.NotEqual(StaleEnvCell, body.CellPosition.ObjCellId);
        Assert.Equal(Landblock, body.CellPosition.ObjCellId & 0xFFFF0000u);
        Assert.InRange(body.CellPosition.ObjCellId & 0xFFFFu, 1u, 0x40u);
    }

    private static bool TrySettle(PhysicsEngine engine, PhysicsBody body) =>
        SpawnPlacementSettler.TrySettle(
            engine,
            body,
            body.Position,
            Cell,
            Radius,
            Height,
            ObjectInfoState.EdgeSlide,
            movingEntityId: 0x70000001u,
            static () => { },
            static () => { });

    private static PhysicsBody AirborneBody(Vector3 position) => new()
    {
        Position = position,
        Orientation = Quaternion.Identity,
        State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.None,
    };

    private static PhysicsEngine BuildFlatEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        AddFlatLandblock(engine);
        return engine;
    }

    private static void AddFlatLandblock(PhysicsEngine engine) =>
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
}
