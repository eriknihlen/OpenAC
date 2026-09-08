using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class CylSphereFamilyTests
{
    private readonly ITestOutputHelper _out;
    public CylSphereFamilyTests(ITestOutputHelper output) => _out = output;

    private const uint TestLandblockId = 0xA9B40000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;   // landcell (0,0)

    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.20f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    // The live platform's registered shape ([cyl-test] launch-137-repro.log).
    private const float PlatformRadius = 2.597f;
    private const float PlatformHeight = 0.256f;

    [Fact]
    public void Grounded_WalkIntoWideLowCylinder_StepsUpOntoTop()
    {
        var engine = BuildEngine(out _);
        RegisterCylinder(engine, entityId: 0xCAFEu,
            worldPos: new Vector3(12f, 14f, 0f),
            radius: PlatformRadius, height: PlatformHeight);

        var body = MakeGroundedBody(new Vector3(12f, 10.4f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.10f, 0f);

        for (int tick = 0; tick < 40; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) grounded={grounded}");

        Assert.True(pos.Y > 11.5f,
            $"Player must advance past the rim contact (pre-port it pinned at Y≈10.9); got Y={pos.Y:F3}");
        Assert.True(MathF.Abs(pos.Z - PlatformHeight) < 0.05f,
            $"Player must stand ON the platform top (Z≈{PlatformHeight:F3}); got Z={pos.Z:F3}");
        Assert.True(grounded, "Player must remain grounded after stepping onto the platform");
    }

    [Fact]
    public void Grounded_WalkIntoTallCylinder_BlocksBeforeAxis()
    {
        var engine = BuildEngine(out _);
        RegisterCylinder(engine, entityId: 0xF00Du,
            worldPos: new Vector3(12f, 14f, 0f),
            radius: 0.2f, height: 2.2f);

        var body = MakeGroundedBody(new Vector3(12f, 12.6f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.10f, 0f);

        for (int tick = 0; tick < 30; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) grounded={grounded}");

        Assert.True(pos.Y < 13.4f,
            $"Tall cylinder must block the dead-center approach; got Y={pos.Y:F3}");
        Assert.True(pos.Z < 0.5f,
            $"Player must NOT end up on top of a 2.2 m cylinder; got Z={pos.Z:F3}");
    }

    [Fact]
    public void Airborne_FallOntoWideCylinder_LandsOnTop()
    {
        var engine = BuildEngine(out _);
        RegisterCylinder(engine, entityId: 0xCAFEu,
            worldPos: new Vector3(12f, 14f, 0f),
            radius: PlatformRadius, height: PlatformHeight);

        Vector3 pos = new(12f, 14f, 1.0f);
        uint cellId = TestCellId;
        bool grounded = false;
        var perTick = new Vector3(0f, 0f, -0.25f);

        int landedTick = -1;
        for (int tick = 0; tick < 20; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded,
                body:           null,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;

            if (grounded) { landedTick = tick; break; }
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) grounded={grounded} landedTick={landedTick}");

        Assert.True(grounded, "Falling sphere must land (ground) on the platform top");
        Assert.True(MathF.Abs(pos.Z - PlatformHeight) < 0.05f,
            $"Landing must rest on the top disc (Z≈{PlatformHeight:F3}), not the terrain " +
            $"(Z=0) inside the footprint; got Z={pos.Z:F3}");
    }

    [Fact]
    public void Grounded_EtherealCylinder_IsFullyPassable()
    {
        var engine = BuildEngine(out _);
        RegisterCylinder(engine, entityId: 0xE7E7u,
            worldPos: new Vector3(12f, 14f, 0f),
            radius: 0.2f, height: 2.2f,
            state: 0x4u);   // ETHEREAL_PS, non-static

        var body = MakeGroundedBody(new Vector3(12f, 12.6f, 0f));
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.10f, 0f);

        for (int tick = 0; tick < 30; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
                grounded,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            pos      = result.Position;
            cellId   = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})");

        Assert.True(pos.Y > 14.5f,
            $"Ethereal cylinder must not block (walked from 12.6 to past the axis); got Y={pos.Y:F3}");
    }

    // ───────────────────────────────────────────────────────────────
    // Harness
    // ───────────────────────────────────────────────────────────────

    private static PhysicsEngine BuildEngine(out PhysicsDataCache cache)
    {
        cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        // Flat terrain at Z=0 across the whole landblock.
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

    private static void RegisterCylinder(PhysicsEngine engine, uint entityId,
        Vector3 worldPos, float radius, float height, uint state = 0u)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            worldPos, Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: height,
            state: state);
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
