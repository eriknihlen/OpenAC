using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class InitialPlacementOverlapTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;
    private const uint PlayerId = 0x50000001u;
    private const uint MonsterId = 0x50000002u;
    private const float Radius = 0.48f;
    private const float SphereHeight = 1.835f;

    [Fact]
    public void PlayerReloggingInsideMonster_SearchesOutToNearestClearRing()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);

        var savedFeet = new Vector3(10f, 10f, 0f);
        var playerCenter = savedFeet + new Vector3(0f, 0f, Radius);
        var monsterCenter = playerCenter + new Vector3(0.10f, 0f, 0f);

        RegisterSphere(engine, PlayerId, playerCenter,
            EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsCreature);
        RegisterSphere(engine, MonsterId, monsterCenter,
            EntityCollisionFlags.IsCreature);

        ImmutableArray<FlatCollisionSphere> spheres = ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, Radius), Radius),
            new FlatCollisionSphere(new Vector3(0f, 0f, SphereHeight - Radius), Radius));

        PhysicsSetPositionResult result = engine.SetPosition(
            new PhysicsSetPositionRequest(
                Position: savedFeet,
                Orientation: Quaternion.Identity,
                CellId: Cell,
                CellLocalPosition: savedFeet,
                Spheres: spheres,
                Scale: 1f,
                StepUpHeight: 0.4f,
                StepDownHeight: 0.4f,
                MoverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                MovingEntityId: PlayerId,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide));

        Assert.True(result.IsCommitted);
        Assert.True(result.InContact);
        Assert.True(result.OnWalkable);
        Assert.Equal(Cell, result.ContactPlaneCellId);
        Assert.NotEqual(savedFeet, result.Position);
        float centerDistance = Vector3.Distance(
            result.Position + new Vector3(0f, 0f, Radius),
            monsterCenter);
        Assert.True(centerDistance >= Radius * 2f - PhysicsGlobals.EPSILON,
            $"placement must clear the monster; centers remain {centerDistance:F3} m apart");
        Assert.True(Vector3.Distance(savedFeet, result.Position) <= 4f,
            "retail placement search is bounded to four metres");
    }

    private static void RegisterSphere(
        PhysicsEngine engine,
        uint id,
        Vector3 center,
        EntityCollisionFlags flags)
    {
        engine.ShadowObjects.Register(
            id,
            gfxObjId: 0u,
            worldPos: center,
            rotation: Quaternion.Identity,
            radius: Radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f,
            scale: 1f,
            state: 0u,
            flags: flags,
            seedCellId: Cell,
            isStatic: false);
    }
}
