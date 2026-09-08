using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class Issue165RemoteWallPenetrationDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public Issue165RemoteWallPenetrationDiagnosticTests(ITestOutputHelper output) => _out = output;

    private const uint TestLandblockId = 0xA9D60000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;

    private const float SphereRadius = 0.48f;
    private const float SphereHeight = 1.835f;
    private const float StepUpHeight = 0.4f;
    private const float StepDownHeight = 0.4f;

    [Fact]
    public void SingleLargeTickJumpThroughObstacle_IsStillBlockedAtSurface()
    {
        var engine = BuildEngine();
        RegisterCreatureSphere(engine, 0xC0F0u, 12f, 11.5f); // due north, same as the proven 30-tick test

        var body = MakeGroundedBody(new Vector3(12f, 10f, 0f));
        Vector3 farTarget = new(12f, 12.4f, 0f);

        var result = engine.ResolveWithTransition(
            body.Position, farTarget, TestCellId,
            SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
            isOnGround: true, body: body,
            moverFlags: ObjectInfoState.EdgeSlide,
            movingEntityId: 0);

        _out.WriteLine(
            $"single-jump result pos=({result.Position.X:F3},{result.Position.Y:F3},{result.Position.Z:F3}) "
            + $"ok={result.Ok} collisionNormalValid={result.CollisionNormalValid}");

        Assert.True(
            result.Position.Y < 10.7f,
            "A single large-tick jump through solid geometry must be blocked "
            + $"at the surface, matching the small-step case; got Y={result.Position.Y:F3}. "
            + "If this fails, candidate (a) (the sweep does not catch large "
            + "single-tick deltas) is CONFIRMED as a contributing #165 mechanism.");
    }

    [Fact]
    public void SingleLargeTickJumpWithNoObstacle_ReachesFarTarget()
    {
        var engine = BuildEngine();

        var body = MakeGroundedBody(new Vector3(12f, 10f, 0f));
        Vector3 farTarget = new(12f, 12.4f, 0f);

        var result = engine.ResolveWithTransition(
            body.Position, farTarget, TestCellId,
            SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
            isOnGround: true, body: body,
            moverFlags: ObjectInfoState.EdgeSlide,
            movingEntityId: 0);

        _out.WriteLine($"unobstructed pos=({result.Position.X:F3},{result.Position.Y:F3})");

        Assert.True(
            result.Position.Y > 12.2f,
            $"An unobstructed large single-tick jump must actually complete; got Y={result.Position.Y:F3}");
    }

    [Fact]
    public void SingleLargeTickJumpStartingInsideObstacleOverlap_DoesNotAcceptTunneledCandidate()
    {
        var engine = BuildEngine();
        RegisterCreatureSphere(engine, 0xC0F1u, 12f, 11.5f);

        var body = MakeGroundedBody(new Vector3(12f, 10.84f, 0f));
        Vector3 farTarget = new(12f, 13f, 0f);

        var result = engine.ResolveWithTransition(
            body.Position, farTarget, TestCellId,
            SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
            isOnGround: true, body: body,
            moverFlags: ObjectInfoState.EdgeSlide,
            movingEntityId: 0);

        _out.WriteLine(
            $"overlap-start result pos=({result.Position.X:F3},{result.Position.Y:F3}) "
            + $"input Y=10.84 (already inside the {0.48f + 0.48f:F2} m combined-radius overlap)");

        Assert.True(
            result.Position.Y < 11.4f,
            "A candidate that starts inside a solid obstacle's overlap zone must not "
            + $"sail through to the far target; got Y={result.Position.Y:F3}");
    }

    private static PhysicsEngine BuildEngine()
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        var heights = new byte[81];
        var heightTable = new float[256]; // all zero → terrain Z = 0
        engine.AddLandblock(
            landblockId: TestLandblockId,
            terrain: new TerrainSurface(heights, heightTable),
            cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static void RegisterCreatureSphere(
        PhysicsEngine engine, uint entityId, float x, float y)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            new Vector3(x, y, SphereRadius), Quaternion.Identity, SphereRadius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f, scale: 1f,
            state: 0u,
            flags: EntityCollisionFlags.IsCreature,
            isStatic: false);
    }

    private static PhysicsBody MakeGroundedBody(Vector3 position)
    {
        var floorPlane = new Plane(Vector3.UnitZ, 0f);
        var floorVerts = new[]
        {
            new Vector3(-100f, -100f, 0f),
            new Vector3(100f, -100f, 0f),
            new Vector3(100f, 100f, 0f),
            new Vector3(-100f, 100f, 0f),
        };

        return new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            ContactPlaneValid = true,
            ContactPlane = floorPlane,
            ContactPlaneCellId = TestCellId,
            WalkablePolygonValid = true,
            WalkablePlane = floorPlane,
            WalkableVertices = floorVerts,
            WalkableUp = Vector3.UnitZ,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }
}
