using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class TransitFailProbeTests
{
    private const uint CellId = 0xA9B40001u;
    private const uint GfxObjId = 0x0100F100u;

    [Fact]
    public void Probe_FiresOnSyntheticStuckTick_WallAbsorbsWholeRequest()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var engine = BuildEngine(root, resolved);
        var body = new PhysicsBody();
        ResetBody(body);

        PhysicsDiagnostics.DumpTransitFailEnabled = true;
        var saved = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        ResolveResult result;
        try
        {
            result = engine.ResolveWithTransition(
                currentPos: new Vector3(0.30f, 0f, 0.20f),
                targetPos: new Vector3(0.60f, 0f, 0.20f),
                cellId: CellId,
                sphereRadius: BSPStepUpFixtures.SphereRadius,
                sphereHeight: 1.20f,
                stepUpHeight: 0.60f,
                stepDownHeight: 1.50f,
                isOnGround: true,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x5000000Au);
        }
        finally
        {
            Console.SetOut(saved);
            PhysicsDiagnostics.DumpTransitFailEnabled = false;
        }

        string log = sw.ToString();

        // The stuck-tick predicate must have fired: requested ~0.30 m of
        // XY, delivered essentially none.
        Assert.Contains("[transit-fail]", log);
        Assert.Contains("STUCK-TICK", log);
        Assert.Contains("mover=0x5000000A", log);

        Assert.Contains("[transit-fail-insert]", log);

        float actualDx = result.Position.X - 0.30f;
        float actualDy = result.Position.Y - 0f;
        float actualXYLen = MathF.Sqrt(actualDx * actualDx + actualDy * actualDy);
        Assert.True(
            actualXYLen < 0.01f,
            $"expected the wall to absorb ~all requested XY movement, " +
            $"actual XY delta length={actualXYLen:F5} (position=" +
            $"{result.Position.X:F4},{result.Position.Y:F4},{result.Position.Z:F4})");
    }

    [Fact]
    public void Probe_StaysSilentOnOrdinaryMovingTick()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var engine = BuildEngine(root, resolved);
        var body = new PhysicsBody();
        ResetBody(body);

        PhysicsDiagnostics.DumpTransitFailEnabled = true;
        var saved = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        ResolveResult result;
        try
        {
            // Same floor, same player profile, but walking parallel to the
            // wall (along -Y) far from x=0.5 — nothing should block this
            // move at all.
            result = engine.ResolveWithTransition(
                currentPos: new Vector3(-1.50f, 0.00f, 0.20f),
                targetPos: new Vector3(-1.50f, -0.30f, 0.20f),
                cellId: CellId,
                sphereRadius: BSPStepUpFixtures.SphereRadius,
                sphereHeight: 1.20f,
                stepUpHeight: 0.60f,
                stepDownHeight: 1.50f,
                isOnGround: true,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x5000000Au);
        }
        finally
        {
            Console.SetOut(saved);
            PhysicsDiagnostics.DumpTransitFailEnabled = false;
        }

        string log = sw.ToString();

        Assert.DoesNotContain("[transit-fail", log);

        float actualDy = result.Position.Y - 0.00f;
        Assert.True(
            MathF.Abs(actualDy) > 0.20f,
            $"expected the open-floor move to actually advance in Y, " +
            $"actual Y={result.Position.Y:F4}");
    }

    private static void ResetBody(PhysicsBody body)
    {
        body.State = PhysicsStateFlags.Gravity;
        body.TransientState = TransientStateFlags.Active;
        body.ContactPlaneValid = false;
        body.WalkablePolygonValid = false;
        body.WalkableVertices = null;
        body.SlidingNormal = Vector3.Zero;
        body.FramesStationaryFall = 0;
    }

    private static PhysicsEngine BuildEngine(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved)
    {
        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, -50f);

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);

        var cache = new PhysicsDataCache();
        cache.RegisterGfxObjForTest(GfxObjId, new GfxObjPhysics
        {
            BSP = new PhysicsBSPTree { Root = root },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices = new VertexArray(),
            Resolved = resolved,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0f, 0f, 2.5f),
                Radius = 10f,
            },
        });
        engine.DataCache = cache;
        engine.ShadowObjects.Register(
            entityId: GfxObjId,
            gfxObjId: GfxObjId,
            worldPos: Vector3.Zero,
            rotation: Quaternion.Identity,
            radius: 10f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            collisionType: ShadowCollisionType.BSP,
            scale: 1f);

        return engine;
    }
}
