using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class BSPStepUpTests
{
    // =========================================================================
    // Group A — Baselines (pass before AND after the implementation)
    // =========================================================================

    [Fact]
    public void A1_NullRoot_ReturnsOK()
    {
        var from = new Vector3(0f, 0f, BSPStepUpFixtures.SphereRadius);
        var to   = new Vector3(0.1f, 0f, BSPStepUpFixtures.SphereRadius);
        var t    = BSPStepUpFixtures.MakeGroundedTransition(from, to);

        var localSphere = new DatReaderWriter.Types.Sphere
        {
            Origin = to,
            Radius = BSPStepUpFixtures.SphereRadius,
        };

        var result = BSPQuery.FindCollisions(
            null,
            new Dictionary<ushort, ResolvedPolygon>(),
            t, localSphere, null,
            from, Vector3.UnitZ, 1.0f);

        Assert.Equal(TransitionState.OK, result);
    }

    [Fact]
    public void A2_GroundedMover_NoWallNear_ReturnsOK()
    {
        var (root, resolved) = BSPStepUpFixtures.LowStep();

        // Moving in -X, away from the wall at x=0.5.
        var from = new Vector3(-1f, 0f, BSPStepUpFixtures.SphereRadius);
        var to   = new Vector3(-1.5f, 0f, BSPStepUpFixtures.SphereRadius);
        var t    = BSPStepUpFixtures.MakeGroundedTransition(from, to);

        var localSphere = new DatReaderWriter.Types.Sphere { Origin = to, Radius = BSPStepUpFixtures.SphereRadius };

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            from, Vector3.UnitZ, 1.0f);

        Assert.Equal(TransitionState.OK, result);
    }

    [Fact]
    public void A3_AirborneMover_AboveRoof_ReturnsOK()
    {
        var (root, resolved) = BSPStepUpFixtures.FlatRoof();

        // Mover at z=6 (well above the roof at z=3) with tiny downward step.
        float highZ = 6f;
        var from = new Vector3(0f, 0f, highZ + BSPStepUpFixtures.SphereRadius);
        var to   = new Vector3(0f, 0f, highZ + BSPStepUpFixtures.SphereRadius - 0.01f);
        var t    = BSPStepUpFixtures.MakeAirborneTransition(from, to);

        var localSphere = new DatReaderWriter.Types.Sphere { Origin = to, Radius = BSPStepUpFixtures.SphereRadius };

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            from, Vector3.UnitZ, 1.0f);

        Assert.Equal(TransitionState.OK, result);
    }

    [Fact]
    public void A4_SlopedFixture_NormalBelowFloorZ()
    {
        var (_, resolved) = BSPStepUpFixtures.SlopedUnwalkable();
        var slope = resolved[BSPStepUpFixtures.SlopedUnwalkable_SlopeId];

        Assert.True(slope.Plane.Normal.Z < PhysicsGlobals.FloorZ,
            $"Slope normal.Z ({slope.Plane.Normal.Z:F4}) must be < FloorZ ({PhysicsGlobals.FloorZ:F4})");
        Assert.True(slope.Plane.Normal.Z > 0f,
            $"Slope normal.Z ({slope.Plane.Normal.Z:F4}) must be > 0 (upward-facing)");
    }

    /// <summary>
    /// Low-step upper-floor polygon has normal.Z >= FloorZ (it IS walkable).
    /// </summary>
    [Fact]
    public void A5_LowStepUpperFloor_NormalAboveFloorZ()
    {
        var (_, resolved) = BSPStepUpFixtures.LowStep();
        var upper = resolved[BSPStepUpFixtures.LowStep_UpperFloorId];

        Assert.True(upper.Plane.Normal.Z >= PhysicsGlobals.FloorZ,
            $"Upper floor normal.Z ({upper.Plane.Normal.Z:F4}) must be >= FloorZ ({PhysicsGlobals.FloorZ:F4})");
    }

    /// <summary>
    /// Roof polygon has normal.Z >= LandingZ (it can be landed on).
    /// </summary>
    [Fact]
    public void A6_FlatRoofPolygon_NormalAboveLandingZ()
    {
        var (_, resolved) = BSPStepUpFixtures.FlatRoof();
        var roof = resolved[BSPStepUpFixtures.FlatRoof_RoofId];

        Assert.True(roof.Plane.Normal.Z >= PhysicsGlobals.LandingZ,
            $"Roof normal.Z ({roof.Plane.Normal.Z:F4}) must be >= LandingZ ({PhysicsGlobals.LandingZ:F4})");
    }


    [Fact]
    public void B1_GroundedMover_LowStep_StepsUp()
    {
        var (root, resolved) = BSPStepUpFixtures.LowStep();
        const float stepUpHeight = 0.30f;  // larger than step (0.25), so step-up succeeds

        var from = new Vector3(0.1f, 0f, 0f);
        var to   = new Vector3(0.6f, 0f, 0f);

        var t      = BSPStepUpFixtures.MakeGroundedTransition(from, to, stepUpHeight);
        var engine = MakeTestEngine(root, resolved, terrainZ: 0f);

        bool ok = t.FindTransitionalPosition(engine);

        float expectedMinZ = 0.25f - PhysicsGlobals.EPSILON * 10f;
        Assert.True(t.SpherePath.CurPos.Z >= expectedMinZ,
            $"Expected Z >= {expectedMinZ:F4} (stepped up to upper floor at z=0.25), " +
            $"got CurPos.Z = {t.SpherePath.CurPos.Z:F4}. " +
            "Path 5 must call StepUp (L.2.1) instead of wall-sliding.");
    }

    [Fact]
    public void B2_GroundedMover_TallWall_BlockedOrSlides()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        const float stepUpHeight = 0.04f;

        // Foot at z=0 (on terrain). Same reasoning as B1.
        var from = new Vector3(0.1f, 0f, 0f);
        var to   = new Vector3(0.6f, 0f, 0f);

        var t      = BSPStepUpFixtures.MakeGroundedTransition(from, to, stepUpHeight);
        // terrainZ=0f: keep grounded between steps (same as B1).
        var engine = MakeTestEngine(root, resolved, terrainZ: 0f);

        t.FindTransitionalPosition(engine);

        float wallFace = 0.5f - BSPStepUpFixtures.SphereRadius;
        Assert.True(t.SpherePath.CurPos.X <= wallFace + PhysicsGlobals.EPSILON * 20f,
            $"Expected mover blocked before wall (x <= {wallFace:F3}), " +
            $"got CurPos.X = {t.SpherePath.CurPos.X:F4}");
    }

    [Fact]
    public void B3_Path5_DirectCall_ContactHitsLowWall_NotSlid()
    {
        var (root, resolved) = BSPStepUpFixtures.LowStep();

        float r   = BSPStepUpFixtures.SphereRadius;
        var checkPos = new Vector3(0.5f - r * 0.5f, 0f, r);
        var currPos  = new Vector3(0.1f, 0f, r);

        var t = new Transition();
        t.SpherePath.InitPath(currPos, checkPos, 0xA9B40001u, r);
        t.SpherePath.SetCheckPos(checkPos, 0xA9B40001u);
        t.ObjectInfo.State          = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
        t.ObjectInfo.StepUpHeight   = 0.30f;
        t.ObjectInfo.StepDownHeight = 0.04f;
        t.CollisionInfo.LastKnownContactPlane      = new Plane(Vector3.UnitZ, 0f);
        t.CollisionInfo.LastKnownContactPlaneValid = true;

        var localSphere = new DatReaderWriter.Types.Sphere { Origin = checkPos, Radius = r };

        var engine = MakeTestEngine(root, resolved);

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            currPos, Vector3.UnitZ, 1.0f, Quaternion.Identity, engine);

        // After L.2.1 this assertion flips from failing (Slid) to passing.
        Assert.NotEqual(TransitionState.Slid, result);
    }


    [Fact]
    public void C1_Path6_AirborneMoverHitsRoof_SetsCollideFlagAndAdjusted()
    {
        var (root, resolved) = BSPStepUpFixtures.FlatRoof();

        float r   = BSPStepUpFixtures.SphereRadius;
        var checkPos = new Vector3(0f, 0f, 3f + r * 0.5f);   // half-radius above roof
        var currPos  = new Vector3(0f, 0f, 3f + r + 0.1f);

        var t = new Transition();
        t.SpherePath.InitPath(currPos, checkPos, 0xA9B40001u, r);
        t.SpherePath.SetCheckPos(checkPos, 0xA9B40001u);
        t.ObjectInfo.State = ObjectInfoState.None;

        var localSphere = new DatReaderWriter.Types.Sphere { Origin = checkPos, Radius = r };

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            currPos, Vector3.UnitZ, 1.0f);

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.True(t.SpherePath.Collide,
            "Expected SpherePath.Collide = true after Path 6 hit (L.2.2)");
        Assert.Equal(PhysicsGlobals.LandingZ, t.SpherePath.WalkableAllowance,
            precision: 5);
    }

    [Fact]
    public void C2_AirborneMover_LandsOnFlatRoof_ContactPlaneSet()
    {
        var (root, resolved) = BSPStepUpFixtures.FlatRoof();

        float roofZ = 3f;
        float r     = BSPStepUpFixtures.SphereRadius;
        var from = new Vector3(0f, 0f, roofZ - r + 0.3f);
        var to   = new Vector3(0f, 0f, roofZ - r - 0.05f);   // sphere bottom at z ≈ 2.95 (into roof)

        var t      = BSPStepUpFixtures.MakeAirborneTransition(from, to);
        // terrainZ=-50f: airborne mover — terrain must not interfere with roof landing.
        var engine = MakeTestEngine(root, resolved, terrainZ: -50f);

        t.FindTransitionalPosition(engine);

        bool planeSet = t.CollisionInfo.ContactPlaneValid
                     || t.CollisionInfo.LastKnownContactPlaneValid;

        Assert.True(planeSet,
            "Expected a contact plane after landing on roof (L.2.2). " +
            "Currently Path 6 wall-slides and never sets ContactPlane.");

        if (planeSet)
        {
            var plane = t.CollisionInfo.ContactPlaneValid
                ? t.CollisionInfo.ContactPlane
                : t.CollisionInfo.LastKnownContactPlane;

            Assert.True(plane.Normal.Z >= PhysicsGlobals.LandingZ,
                $"Contact plane normal.Z ({plane.Normal.Z:F4}) must be >= LandingZ ({PhysicsGlobals.LandingZ:F4})");
        }
    }

    [Fact]
    public void C3_Path6_AirborneMoverHitsSteepSlope_DefersThroughSetCollide()
    {
        var (root, resolved) = BSPStepUpFixtures.SlopedUnwalkable();

        float r   = BSPStepUpFixtures.SphereRadius;
        // Approach the slope mid-face from above.
        var checkPos = new Vector3(0.5f, 0f, 1.0f + r * 0.5f);
        var currPos  = new Vector3(0.5f, 0f, 1.0f + r + 0.1f);

        var t = new Transition();
        t.SpherePath.InitPath(currPos, checkPos, 0xA9B40001u, r);
        t.SpherePath.SetCheckPos(checkPos, 0xA9B40001u);
        t.ObjectInfo.State = ObjectInfoState.None;  // airborne

        var localSphere = new DatReaderWriter.Types.Sphere { Origin = checkPos, Radius = r };

        var result = BSPQuery.FindCollisions(
            root, resolved, t, localSphere, null,
            currPos, Vector3.UnitZ, 1.0f);

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.True(t.SpherePath.Collide);
        Assert.Equal(PhysicsGlobals.LandingZ, t.SpherePath.WalkableAllowance);
        Assert.False(t.CollisionInfo.CollisionNormalValid);
        Assert.False(t.CollisionInfo.SlidingNormalValid);
    }


    [Fact]
    public void D1_GroundedMover_TooTallWall_PreservesContactPlane()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();

        // Foot at z=0, walking into the wall.
        var from = new Vector3(0.1f, 0f, 0f);
        var to   = new Vector3(0.6f, 0f, 0f);

        var t      = BSPStepUpFixtures.MakeGroundedTransition(from, to, stepUpHeight: 0.04f);
        var engine = MakeTestEngine(root, resolved, terrainZ: 0f);

        t.FindTransitionalPosition(engine);

        bool stillGrounded = t.CollisionInfo.ContactPlaneValid
                          || t.CollisionInfo.LastKnownContactPlaneValid
                          || t.ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable);
        Assert.True(stillGrounded,
            "Expected mover to still be grounded after walking into a too-tall " +
            "wall (failed step-up should preserve LastKnownContactPlane).");
    }

    [Fact]
    public void D1b_LastKnownPlaneCollisionRecovery_KillsVelocity()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var transition = BSPStepUpFixtures.MakeGroundedTransition(
            from: new Vector3(0.1f, 0f, 0f),
            to: new Vector3(0.6f, 0f, 0f),
            stepUpHeight: 0.04f);
        var engine = MakeTestEngine(root, resolved, terrainZ: 0f);

        transition.FindTransitionalPosition(engine);

        Assert.True(
            transition.ObjectInfo.VelocityKilled,
            "Retail OBJECTINFO::kill_velocity must run before restoring the " +
            "remembered floor after a collision/slide retry.");
    }

    [Fact]
    public void D1c_LastKnownPlaneCleanAdvance_DoesNotKillOrReground()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var transition = BSPStepUpFixtures.MakeAirborneTransition(
            from: new Vector3(-2f, 0f, 2f),
            to: new Vector3(-2f, 0f, 2.25f));
        transition.CollisionInfo.LastKnownContactPlane = new Plane(Vector3.UnitZ, -1.52f);
        transition.CollisionInfo.LastKnownContactPlaneValid = true;
        var engine = MakeTestEngine(root, resolved, terrainZ: -50f);

        transition.FindTransitionalPosition(engine);

        Assert.False(transition.ObjectInfo.VelocityKilled);
        Assert.False(transition.CollisionInfo.ContactPlaneValid);
        Assert.False(transition.CollisionInfo.LastKnownContactPlaneValid);
        Assert.False(transition.ObjectInfo.OnWalkable);
    }

    [Fact]
    public void D2_GroundedMover_TallWall_DoesNotRecurseInfinitely()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();

        var from = new Vector3(0.1f, 0f, 0f);
        var to   = new Vector3(0.6f, 0f, 0f);

        var t      = BSPStepUpFixtures.MakeGroundedTransition(from, to, stepUpHeight: 0.04f);
        var engine = MakeTestEngine(root, resolved, terrainZ: 0f);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.FindTransitionalPosition(engine);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Step-up against tall wall took {sw.ElapsedMilliseconds}ms — " +
            "indicates Path 5 recursing through DoStepUp without guard.");
    }

    [Fact]
    public void D3_AirborneMover_TallWall_PreservesVerticalMotion()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();

        var from = new Vector3(0.1f, 0f, 2.0f);
        var to   = new Vector3(0.6f, 0f, 1.5f);

        var t      = BSPStepUpFixtures.MakeAirborneTransition(from, to);
        var engine = MakeTestEngine(root, resolved, terrainZ: -50f);

        t.FindTransitionalPosition(engine);

        Assert.True(t.SpherePath.CurPos.Z < from.Z - 0.1f,
            $"Expected airborne wall-slide to preserve downward motion; " +
            $"from.Z={from.Z:F3}, CurPos.Z={t.SpherePath.CurPos.Z:F3}");
        Assert.True(t.SpherePath.CurPos.X <= 0.5f - BSPStepUpFixtures.SphereRadius + PhysicsGlobals.EPSILON * 20f,
            $"Expected wall to block X penetration; got CurPos.X={t.SpherePath.CurPos.X:F3}");
    }

    [Fact]
    public void D4_AirborneMover_TallWall_PersistsSlidingNormalAcrossFrames()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var engine = MakeTestEngine(root, resolved, terrainZ: -50f);
        var body = new PhysicsBody
        {
            Position = new Vector3(0.25f, 0f, 2.0f),
            TransientState = TransientStateFlags.Active,
        };

        var frame1 = engine.ResolveWithTransition(
            currentPos: body.Position,
            targetPos:  new Vector3(0.36f, 0f, 1.92f),
            cellId: 0xA9B40001u,
            sphereRadius: BSPStepUpFixtures.SphereRadius,
            sphereHeight: 0f,
            stepUpHeight: 0.04f,
            stepDownHeight: 0.04f,
            isOnGround: false,
            body: body);

        body.Position = frame1.Position;

        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "First airborne wall hit should cache SlidingNormal for the next frame.");
        // Path 6's primary-sphere SetCollide hard-stops this first frame; the
        // persisted normal then permits the downward tangent on frame two.
        Assert.Equal(2.0f, frame1.Position.Z, precision: 3);

        var frame2 = engine.ResolveWithTransition(
            currentPos: body.Position,
            targetPos:  body.Position + new Vector3(0.11f, 0f, -0.08f),
            cellId: 0xA9B40001u,
            sphereRadius: BSPStepUpFixtures.SphereRadius,
            sphereHeight: 0f,
            stepUpHeight: 0.04f,
            stepDownHeight: 0.04f,
            isOnGround: false,
            body: body);

        Assert.True(frame2.Position.Z < frame1.Position.Z - 0.05f,
            $"Expected cached wall-slide normal to allow falling on frame 2; " +
            $"frame1.Z={frame1.Position.Z:F3}, frame2.Z={frame2.Position.Z:F3}");
        Assert.InRange(frame2.Position.X, 0.24f, 0.31f);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static PhysicsEngine MakeTestEngine(
        PhysicsBSPNode                       root,
        Dictionary<ushort, ResolvedPolygon>  resolved,
        Vector3?                             objectPosition = null,
        float                                terrainZ       = 0f)
    {
        const uint LandblockId    = 0xA9B4FFFFu;
        const uint SyntheticGfxId = 0xDEADBEEFu;

        var heights   = new byte[81];  // all zero → uses index 0 from heightTable
        var heightTab = new float[256];
        for (int i = 0; i < 256; i++) heightTab[i] = terrainZ;

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            LandblockId,
            new TerrainSurface(heights, heightTab),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        var cache  = new PhysicsDataCache();
        var bspTree = new DatReaderWriter.Types.PhysicsBSPTree { Root = root };
        var physics = new GfxObjPhysics
        {
            BSP             = bspTree,
            PhysicsPolygons = new Dictionary<ushort, DatReaderWriter.Types.Polygon>(),
            Vertices        = new DatReaderWriter.Types.VertexArray(),
            Resolved        = resolved,
            BoundingSphere  = new DatReaderWriter.Types.Sphere { Origin = Vector3.Zero, Radius = 15f },
        };
        cache.RegisterGfxObjForTest(SyntheticGfxId, physics);
        engine.DataCache = cache;

        // Register the object in the shadow registry so FindObjCollisions picks it up.
        Vector3 pos = objectPosition ?? Vector3.Zero;
        engine.ShadowObjects.Register(
            entityId:      SyntheticGfxId,
            gfxObjId:      SyntheticGfxId,
            worldPos:      pos,
            rotation:      Quaternion.Identity,
            radius:        15f,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   LandblockId,
            collisionType: ShadowCollisionType.BSP,
            scale:         1.0f);

        return engine;
    }
}
