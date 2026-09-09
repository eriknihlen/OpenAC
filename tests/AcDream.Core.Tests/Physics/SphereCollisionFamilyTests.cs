using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class SphereCollisionFamilyTests
{
    private readonly ITestOutputHelper _out;
    public SphereCollisionFamilyTests(ITestOutputHelper output) => _out = output;

    private const uint TestLandblockId = 0xA9B40000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;   // landcell (0,0)

    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.20f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    private const float CreatureRadius = 0.48f;   // a humanoid body sphere

    [Fact]
    public void GroundedDiagonalApproach_SlidesPastOffsetCreature()
    {
        var engine = BuildEngine(out _);
        RegisterCreatureSphere(engine, 0xC0C0u, 10.35f, 12f);   // slightly EAST of the N path

        var body = MakeGroundedBody(new Vector3(10f, 10f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.08f, 0f);   // push NORTH

        for (int tick = 0; tick < 60; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded, body: body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
            _out.WriteLine($"tick {tick,2}: ({pos.X:F3},{pos.Y:F3})");
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) grounded={grounded}");

        Assert.True(pos.Y > 12.5f,
            $"Player must slide around the offset creature and continue north, not stick at " +
            $"its surface; got Y={pos.Y:F3}");
    }

    [Fact]
    public void GroundedSingleCreature_HeadOnPush_BlocksWithoutPenetration()
    {
        var engine = BuildEngine(out _);
        RegisterCreatureSphere(engine, 0xC0B0u, 12f, 11.5f);   // due north

        var body = MakeGroundedBody(new Vector3(12f, 10f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.08f, 0f);   // straight in

        for (int tick = 0; tick < 30; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded, body: body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})");

        Assert.True(pos.Y < 10.7f,
            $"Player must be blocked at the sphere surface (Y≈10.54), not penetrate; got Y={pos.Y:F3}");
        Assert.True(pos.Y > 10.3f,
            $"Player must actually reach the creature (not stop early); got Y={pos.Y:F3}");
    }

    [Fact]
    public void GroundedEtherealSphere_IsFullyPassable()
    {
        var engine = BuildEngine(out _);
        RegisterCreatureSphere(engine, 0xC0E0u, 12f, 11.5f,
            state: 0x4u, isCreatureFlag: false);   // ETHEREAL_PS, non-static

        var body = MakeGroundedBody(new Vector3(12f, 10f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.08f, 0f);

        for (int tick = 0; tick < 45; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded, body: body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})");

        Assert.True(pos.Y > 12.5f,
            $"Ethereal sphere must not block (walked from 10 past the axis); got Y={pos.Y:F3}");
    }


    private static PhysicsEngine BuildEngine(out PhysicsDataCache cache)
    {
        cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        var heights = new byte[81];
        var heightTable = new float[256];   // all zero → terrain Z = 0
        engine.AddLandblock(
            landblockId:  TestLandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static void RegisterCreatureSphere(PhysicsEngine engine, uint entityId,
        float x, float y, float radius = CreatureRadius, uint state = 0u,
        bool isCreatureFlag = true)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            new Vector3(x, y, SphereRadius), Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f, scale: 1f,
            state: state,
            flags: isCreatureFlag ? EntityCollisionFlags.IsCreature : EntityCollisionFlags.None,
            isStatic: false);
    }

    private static PhysicsBody MakeGroundedBody(Vector3 position)
    {
        var floorPlane = new Plane(Vector3.UnitZ, 0f);
        var floorVerts = new[]
        {
            new Vector3(-100f, -100f, 0f),
            new Vector3( 100f, -100f, 0f),
            new Vector3( 100f,  100f, 0f),
            new Vector3(-100f,  100f, 0f),
        };

        return new PhysicsBody
        {
            Position             = position,
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = floorPlane,
            ContactPlaneCellId   = TestCellId,
            WalkablePolygonValid = true,
            WalkablePlane        = floorPlane,
            WalkableVertices     = floorVerts,
            WalkableUp           = Vector3.UnitZ,
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }
}
