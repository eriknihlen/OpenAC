using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public sealed class TransitionAllocationBaselineTests
{
    private const int WarmupIterations = 256;
    private const int MeasuredIterations = 4_096;
    private const uint CellId = 0xA9B40001u;

    private readonly ITestOutputHelper _output;

    public TransitionAllocationBaselineTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void ReusedScratch_AllocatesZeroBytesPerResolveForEveryMoverFamily()
    {
        var (root, resolved) = BSPStepUpFixtures.TallWall();
        var engine = BuildEngine(root, resolved);
        var groundEngine = BuildGroundEngine();

        long player = Measure(() => ResolvePlayer(engine));
        long groundedPublication = Measure(
            () => ResolveGroundedPublication(groundEngine));
        long remote = Measure(() => ResolveRemote(engine));
        long projectile = Measure(() => ResolveProjectile(engine));
        long camera = Measure(() => ResolveCamera(engine));

        _output.WriteLine(
            $"Slice I1 steady scratch gate ({MeasuredIterations:N0} resolves/profile):");
        _output.WriteLine($"  player:     {player,8:N0} B/resolve");
        _output.WriteLine(
            $"  grounded:   {groundedPublication,8:N0} B/resolve");
        _output.WriteLine($"  remote:     {remote,8:N0} B/resolve");
        _output.WriteLine($"  projectile: {projectile,8:N0} B/resolve");
        _output.WriteLine($"  camera:     {camera,8:N0} B/resolve");

        Assert.Equal(0, player);
        Assert.Equal(0, groundedPublication);
        Assert.Equal(0, remote);
        Assert.Equal(0, projectile);
        Assert.Equal(0, camera);
    }

    private static long Measure(Func<ResolveResult> resolve)
    {
        ResolveResult sink = default;
        for (int i = 0; i < WarmupIterations; i++)
            sink = resolve();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++)
            sink = resolve();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(sink);
        return allocated / MeasuredIterations;
    }

    private static ResolveResult ResolvePlayer(PhysicsEngine engine)
    {
        var body = Bodies.Player;
        ResetBody(body, PhysicsStateFlags.Gravity);
        return engine.ResolveWithTransition(
            currentPos: new Vector3(0.10f, 0f, 0.20f),
            targetPos: new Vector3(0.36f, 0f, 0.20f),
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

    private static ResolveResult ResolveRemote(PhysicsEngine engine)
    {
        var body = Bodies.Remote;
        ResetBody(body, PhysicsStateFlags.Gravity);
        return engine.ResolveWithTransition(
            currentPos: new Vector3(0.10f, 0f, 0.20f),
            targetPos: new Vector3(0.36f, 0f, 0.20f),
            cellId: CellId,
            sphereRadius: BSPStepUpFixtures.SphereRadius,
            sphereHeight: 1.20f,
            stepUpHeight: 0.60f,
            stepDownHeight: 1.50f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.EdgeSlide,
            movingEntityId: 0x80000001u);
    }

    private static ResolveResult ResolveGroundedPublication(PhysicsEngine engine)
    {
        var body = Bodies.Grounded;
        ResetBody(body, PhysicsStateFlags.Gravity);
        return engine.ResolveWithTransition(
            currentPos: new Vector3(8f, 8f, 0f),
            targetPos: new Vector3(8.12f, 8f, 0f),
            cellId: CellId,
            sphereRadius: BSPStepUpFixtures.SphereRadius,
            sphereHeight: 1.20f,
            stepUpHeight: 0.40f,
            stepDownHeight: 1.50f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x5000000Bu);
    }

    private static ResolveResult ResolveProjectile(PhysicsEngine engine)
    {
        var body = Bodies.Projectile;
        ResetBody(body, PhysicsStateFlags.Missile);
        return engine.ResolveWithTransition(
            currentPos: new Vector3(0.10f, 0f, 2.00f),
            targetPos: new Vector3(0.36f, 0f, 2.00f),
            cellId: CellId,
            sphereRadius: 0.05f,
            sphereHeight: 0f,
            stepUpHeight: 0f,
            stepDownHeight: 0f,
            isOnGround: false,
            body: body,
            movingEntityId: 0x80000002u);
    }

    private static ResolveResult ResolveCamera(PhysicsEngine engine)
        => engine.ResolveWithTransition(
            currentPos: new Vector3(0.10f, 0f, 2.00f),
            targetPos: new Vector3(0.36f, 0f, 2.00f),
            cellId: CellId,
            sphereRadius: 0.30f,
            sphereHeight: 0f,
            stepUpHeight: 0f,
            stepDownHeight: 0f,
            isOnGround: false,
            body: null,
            moverFlags: ObjectInfoState.IsViewer
                      | ObjectInfoState.PathClipped
                      | ObjectInfoState.FreeRotate
                      | ObjectInfoState.PerfectClip);

    private static void ResetBody(PhysicsBody body, PhysicsStateFlags state)
    {
        body.State = state;
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

        const uint gfxObjId = 0x0100F100u;
        var cache = new PhysicsDataCache();
        cache.RegisterGfxObjForTest(gfxObjId, new GfxObjPhysics
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
            entityId: gfxObjId,
            gfxObjId: gfxObjId,
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

    private static PhysicsEngine BuildGroundEngine()
    {
        var heights = new byte[81];
        var heightTable = new float[256];
        var engine = new PhysicsEngine();
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static class Bodies
    {
        internal static readonly PhysicsBody Player = new();
        internal static readonly PhysicsBody Grounded = new();
        internal static readonly PhysicsBody Remote = new();
        internal static readonly PhysicsBody Projectile = new();
    }
}
