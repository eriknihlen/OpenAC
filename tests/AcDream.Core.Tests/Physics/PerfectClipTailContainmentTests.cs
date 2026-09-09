using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class PerfectClipTailContainmentTests
{
    private readonly ITestOutputHelper _out;
    public PerfectClipTailContainmentTests(ITestOutputHelper output) => _out = output;

    private const uint TestLandblockId = 0xA9B40000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;

    private const float ViewerSphereRadius = 0.3f;

    private static readonly ObjectInfoState CameraMoverFlags =
        ObjectInfoState.IsViewer | ObjectInfoState.PathClipped
        | ObjectInfoState.FreeRotate | ObjectInfoState.PerfectClip;


    [Fact]
    public void CameraSweep_HitsNonCreatureSphereProp_ReachesTail_RecordsCameraLive()
    {
        PhysicsDiagnostics.ResetPerfectClipTailGuardForTest();
        var engine = BuildEngine();
        RegisterSphere(engine, 0x00005001u, new Vector3(12f, 14f, 1.0f), radius: 1.0f,
            flags: EntityCollisionFlags.None);

        Vector3 pivot = new(12f, 10f, 1.0f);
        Vector3 eye   = new(12f, 20f, 1.0f);

        var r = SweepViewer(engine, pivot, eye, TestCellId);

        _out.WriteLine(FormattableString.Invariant($"ok={r.Ok} pos=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) collNorm={r.CollisionNormalValid} normal=({r.CollisionNormal.X:F3},{r.CollisionNormal.Y:F3},{r.CollisionNormal.Z:F3}) cameraLive={PhysicsDiagnostics.SphereToiCameraLiveCount} unverified={PhysicsDiagnostics.SphereToiUnverifiedCount}"));

        Assert.True(PhysicsDiagnostics.SphereToiCameraLiveCount > 0,
            "The camera's PathClipped, never-grounded sweep must reach the Sphere PerfectClip TOI tail for a non-creature target.");
        Assert.Equal(0, PhysicsDiagnostics.SphereToiUnverifiedCount);

        Assert.True(r.Position.Y < 14f,
            $"camera must be stopped by the prop, not pass through it; got Y={r.Position.Y:F3}");
        Assert.True(r.Position.Y > 11f,
            $"camera must actually reach the prop's surface, not stop early; got Y={r.Position.Y:F3}");
    }

    [Fact]
    public void CameraSweep_HitsCreatureFlaggedSphere_ExemptionCutsChain_GuardStaysSilent()
    {
        PhysicsDiagnostics.ResetPerfectClipTailGuardForTest();
        var engine = BuildEngine();
        RegisterSphere(engine, 0x00005002u, new Vector3(12f, 14f, 1.0f), radius: 1.0f,
            flags: EntityCollisionFlags.IsCreature);

        Vector3 pivot = new(12f, 10f, 1.0f);
        Vector3 eye   = new(12f, 20f, 1.0f);

        var r = SweepViewer(engine, pivot, eye, TestCellId);

        _out.WriteLine(FormattableString.Invariant($"ok={r.Ok} pos=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) cameraLive={PhysicsDiagnostics.SphereToiCameraLiveCount}"));

        Assert.Equal(0, PhysicsDiagnostics.SphereToiCameraLiveCount);
        Assert.Equal(0, PhysicsDiagnostics.SphereToiUnverifiedCount);
        Assert.True(r.Position.Y > 19f,
            $"a creature-flagged prop must be fully exempt for a viewer mover; got Y={r.Position.Y:F3}");
    }


    [Fact]
    public void CameraSweep_HitsNonCreatureCylinderProp_ReachesTail_RecordsCameraLive()
    {
        PhysicsDiagnostics.ResetPerfectClipTailGuardForTest();
        var engine = BuildEngine();
        RegisterCylinder(engine, 0x00006001u, new Vector3(12f, 14f, 0f), radius: 1.0f, height: 2.0f,
            flags: EntityCollisionFlags.None);

        Vector3 pivot = new(12f, 10f, 1.0f);
        Vector3 eye   = new(12f, 20f, 1.0f);

        var r = SweepViewer(engine, pivot, eye, TestCellId);

        _out.WriteLine(FormattableString.Invariant($"ok={r.Ok} pos=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) collNorm={r.CollisionNormalValid} normal=({r.CollisionNormal.X:F3},{r.CollisionNormal.Y:F3},{r.CollisionNormal.Z:F3}) cameraLive={PhysicsDiagnostics.CylToiCameraLiveCount} unverified={PhysicsDiagnostics.CylToiUnverifiedCount}"));

        Assert.True(PhysicsDiagnostics.CylToiCameraLiveCount > 0,
            "The camera's PathClipped, never-grounded sweep must reach the Cyl PerfectClip TOI tail for a non-creature target.");
        Assert.Equal(0, PhysicsDiagnostics.CylToiUnverifiedCount);

        Assert.True(r.Position.Y < 14f,
            $"camera must be stopped by the prop, not pass through it; got Y={r.Position.Y:F3}");
        Assert.True(r.Position.Y > 11f,
            $"camera must actually reach the prop's surface, not stop early; got Y={r.Position.Y:F3}");
    }

    [Fact]
    public void CameraSweep_HitsCreatureFlaggedCylinder_ExemptionCutsChain_GuardStaysSilent()
    {
        PhysicsDiagnostics.ResetPerfectClipTailGuardForTest();
        var engine = BuildEngine();
        RegisterCylinder(engine, 0x00006002u, new Vector3(12f, 14f, 0f), radius: 1.0f, height: 2.0f,
            flags: EntityCollisionFlags.IsCreature);

        Vector3 pivot = new(12f, 10f, 1.0f);
        Vector3 eye   = new(12f, 20f, 1.0f);

        var r = SweepViewer(engine, pivot, eye, TestCellId);

        _out.WriteLine(FormattableString.Invariant($"ok={r.Ok} pos=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) cameraLive={PhysicsDiagnostics.CylToiCameraLiveCount}"));

        Assert.Equal(0, PhysicsDiagnostics.CylToiCameraLiveCount);
        Assert.Equal(0, PhysicsDiagnostics.CylToiUnverifiedCount);
        Assert.True(r.Position.Y > 19f,
            $"a creature-flagged prop must be fully exempt for a viewer mover; got Y={r.Position.Y:F3}");
    }


    private static PhysicsEngine BuildEngine()
    {
        var cache = new PhysicsDataCache();
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

    private static ResolveResult SweepViewer(PhysicsEngine engine, Vector3 pivot, Vector3 desiredEye, uint cellId)
    {
        Vector3 begin = pivot      - new Vector3(0f, 0f, ViewerSphereRadius);
        Vector3 end   = desiredEye - new Vector3(0f, 0f, ViewerSphereRadius);

        return engine.ResolveWithTransition(
            currentPos:     begin,
            targetPos:      end,
            cellId:         cellId,
            sphereRadius:   ViewerSphereRadius,
            sphereHeight:   0f,
            stepUpHeight:   0f,
            stepDownHeight: 0f,
            isOnGround:     false,
            body:           null,
            moverFlags:     CameraMoverFlags,
            movingEntityId: 0);
    }

    private static void RegisterSphere(PhysicsEngine engine, uint entityId, Vector3 worldPos,
        float radius, EntityCollisionFlags flags)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            worldPos, Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f, scale: 1f,
            state: 0u,
            flags: flags,
            isStatic: true);
    }

    private static void RegisterCylinder(PhysicsEngine engine, uint entityId, Vector3 worldPos,
        float radius, float height, EntityCollisionFlags flags)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            worldPos, Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: height, scale: 1f,
            state: 0u,
            flags: flags,
            isStatic: true);
    }
}
