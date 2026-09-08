using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class Issue137SlidingNormalLifecycleTests
{

    [Fact]
    public void ContactFootFullHit_StepUpUnavailable_RealSlide_NoSlidingNormalWrite()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();

        var from = new Vector3(0.10f, 0f, BSPStepUpFixtures.SphereRadius);
        var to   = new Vector3(0.35f, 0f, BSPStepUpFixtures.SphereRadius);
        var t    = BSPStepUpFixtures.MakeGroundedTransition(from, to);

        var localSphere = new DatReaderWriter.Types.Sphere
        {
            Origin = to,
            Radius = BSPStepUpFixtures.SphereRadius,
        };

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            from, Vector3.UnitZ, 1.0f);

        Assert.False(t.CollisionInfo.SlidingNormalValid,
            "find_collisions must not write collision_info.sliding_normal — " +
            "the only in-transition writer is validate_transition. A sliding " +
            "normal leaked here survives to the body " +
            "writeback and absorbs the next frame's forward offset (#137).");
        Assert.True(t.CollisionInfo.CollisionNormalValid,
            "The real slide records the collision normal (CSphere::slide_sphere " +
            "→ set_collision_normal).");
        Assert.Equal(TransitionState.Collided, result);
    }

    [Fact]
    public void ContactHeadFullHit_RealSlide_NoSlidingNormalWrite()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>();

        var floorVerts = new[]
        {
            new Vector3(-2f, -1f, 0f), new Vector3(2f, -1f, 0f),
            new Vector3(2f,  1f, 0f), new Vector3(-2f, 1f, 0f),
        };
        resolved[1] = new ResolvedPolygon
        {
            Vertices  = floorVerts,
            Plane     = new Plane(Vector3.UnitZ, 0f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var wallNormal = new Vector3(-1f, 0f, 0f);
        var wallVerts = new[]
        {
            new Vector3(0.5f, -1f, 0.6f),
            new Vector3(0.5f, -1f, 5f),
            new Vector3(0.5f,  1f, 5f),
            new Vector3(0.5f,  1f, 0.6f),
        };
        resolved[2] = new ResolvedPolygon
        {
            Vertices  = wallVerts,
            Plane     = new Plane(wallNormal, 0.5f),   // n·p + d = 0 at x=0.5
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var leaf = new PhysicsBSPNode
        {
            Type           = BSPNodeType.Leaf,
            BoundingSphere = new DatReaderWriter.Types.Sphere { Origin = new Vector3(0f, 0f, 2.5f), Radius = 10f },
        };
        leaf.Polygons.Add(1);
        leaf.Polygons.Add(2);

        var from = new Vector3(0.10f, 0f, BSPStepUpFixtures.SphereRadius);
        var to   = new Vector3(0.35f, 0f, BSPStepUpFixtures.SphereRadius);
        var t    = BSPStepUpFixtures.MakeGroundedTransition(from, to);

        var footSphere = new DatReaderWriter.Types.Sphere
        {
            Origin = to,
            Radius = BSPStepUpFixtures.SphereRadius,
        };
        var headSphere = new DatReaderWriter.Types.Sphere
        {
            Origin = new Vector3(to.X, to.Y, 0.8f),
            Radius = BSPStepUpFixtures.SphereRadius,
        };

        var result = BSPQuery.FindCollisions(
            leaf, resolved, t, footSphere, headSphere,
            from, Vector3.UnitZ, 1.0f);

        Assert.False(t.CollisionInfo.SlidingNormalValid,
            "Head full hit must go through the real BSPTREE::slide_sphere — " +
            "no sliding_normal write at the BSP layer.");
        Assert.True(t.CollisionInfo.CollisionNormalValid);
        Assert.Equal(TransitionState.Collided, result);
    }

    [Fact]
    public void SlideSphere_OpposingNormals_ReturnsCollided_WithReversedDisplacementNormal()
    {
        var t = new Transition();
        t.SpherePath.InitPath(
            new Vector3(0f, 0f, 0.2f), new Vector3(0.3f, 0f, 0.2f),
            0xA9B40001u, BSPStepUpFixtures.SphereRadius);
        t.CollisionInfo.SetContactPlane(new Plane(Vector3.UnitZ, 0f), 0xA9B40001u, false);

        var currPos = t.SpherePath.GlobalSphere[0].Origin - new Vector3(0.4f, 0f, 0f);

        var result = t.SlideSphereInternal(new Vector3(0f, 0f, -1f), currPos);

        Assert.Equal(TransitionState.Collided, result);
        Assert.True(t.CollisionInfo.CollisionNormalValid);
        Assert.True(t.CollisionInfo.CollisionNormal.X < -0.99f,
            $"Collision normal must be the normalized reversed displacement " +
            $"(−1,0,0); got ({t.CollisionInfo.CollisionNormal.X:F3}," +
            $"{t.CollisionInfo.CollisionNormal.Y:F3},{t.CollisionInfo.CollisionNormal.Z:F3}).");
    }


    private const uint CellId = 0xA9B40157u;

    private static PhysicsEngine BuildWallEngine()
    {
        var (wallRoot, wallResolved) = BSPStepUpFixtures.TallWall();

        var cell = new CellPhysics
        {
            BSP                   = new PhysicsBSPTree { Root = wallRoot },
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = wallResolved,
            CellBSP               = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            },
        };

        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        // Flat terrain strip so the outdoor fall-through has something to
        // sample if it ever fires (same shape as FindEnvCollisionsMultiCellTests).
        var heights = new byte[81];
        Array.Fill(heights, (byte)0);
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(heights, BuildHeightTable()),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        engine.DataCache.RegisterCellStructForTest(CellId, cell);
        return engine;
    }

    private static float[] BuildHeightTable()
    {
        var ht = new float[256];
        for (int i = 0; i < 256; i++) ht[i] = i * 1.0f;
        return ht;
    }

    private static PhysicsBody GroundedBody()
    {
        var body = new PhysicsBody();
        body.ContactPlaneValid = true;
        body.ContactPlane      = new Plane(Vector3.UnitZ, 0f);
        body.TransientState   |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
        return body;
    }

    private ResolveResult ResolveForward(PhysicsEngine engine, PhysicsBody body,
        Vector3 from, Vector3 to)
        => engine.ResolveWithTransition(
            currentPos:     from,
            targetPos:      to,
            cellId:         CellId,
            sphereRadius:   BSPStepUpFixtures.SphereRadius,
            sphereHeight:   0f,     // single sphere — keeps the scenario deterministic
            stepUpHeight:   0.04f,
            stepDownHeight: 0.04f,
            isOnGround:     true,
            body:           body);

    [Fact]
    public void WallLifecycle_PersistOnBlock_AbsorbExactAntiParallel_ClearOnEscape()
    {
        var engine = BuildWallEngine();
        var body   = GroundedBody();

        // ── 1. Face-on +X into the wall at x=0.5 (normal −X) ─────────────
        var r1 = ResolveForward(engine, body,
            from: new Vector3(0.10f, 0f, 0f),
            to:   new Vector3(0.35f, 0f, 0f));

        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "A blocked push must persist the validate-recorded sliding normal " +
            "(SetPositionInternal, on transition success).");
        Assert.True(body.SlidingNormal.X < -0.9f,
            $"Persisted normal should face the mover (−X); got {body.SlidingNormal}.");
        Assert.True(r1.Position.X + BSPStepUpFixtures.SphereRadius
                    <= 0.5f + PhysicsGlobals.EPSILON * 20f,
            $"The 5 m wall must block the sphere; reach={r1.Position.X + BSPStepUpFixtures.SphereRadius:F4}.");

        // ── 2. Exactly-anti-parallel push again: absorbed frame ──────────
        var r2 = ResolveForward(engine, body,
            from: r1.Position,
            to:   r1.Position + new Vector3(0.15f, 0f, 0f));

        Assert.False(r2.Ok,
            "The seeded sliding normal projects the exactly-anti-parallel " +
            "offset to zero → step-0 abort (retail find_transitional_position " +
            "0050bfb7/0050c0ef). Faithful absorbed frame at a REAL wall.");
        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "A failed transition leaves the body's sliding state untouched " +
            "(retail: SetPositionInternal never runs on failure).");

        var r3 = ResolveForward(engine, body,
            from: r2.Position,
            to:   r2.Position + new Vector3(0.10f, 0.15f, 0f));

        Assert.True(r3.Ok, "Oblique push must escape along the wall tangent.");
        Assert.True(r3.Position.Y > r2.Position.Y + 0.05f,
            $"Expected tangential advance along +Y; got Y={r3.Position.Y:F4} " +
            $"(from {r2.Position.Y:F4}).");
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "A successful transition whose steps re-record no collision must " +
            "CLEAR the body's sliding state (SetPositionInternal syncs " +
            "bit 4 from the transition's final " +
            "sliding_normal_valid, which each step clears before its insert).");
        Assert.Equal(Vector3.Zero, body.SlidingNormal);
    }
}
