using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class OutdoorStairsSeamReplayTests
{
    private readonly ITestOutputHelper _out;
    public OutdoorStairsSeamReplayTests(ITestOutputHelper output) => _out = output;

    private const uint StairCellId    = 0xF682002Cu;   // outdoor landcell (low16 = 0x2C < 0x100)
    private const uint StairLandblock = 0xF6820000u;
    private const uint StepGfxObjId   = 0x01000AC5u;
    private const int  StepCount      = 8;

    private static readonly Quaternion StepRot =
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

    private static readonly Vector3 Step0Origin = new(132.0f, 75.245f, 58.815f);

    private static PhysicsEngine BuildStairEngine()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        var dumpPath = Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests",
            "Fixtures", "outdoor-stairs-seam", "0x01000AC5.gfxobj.json");
        Assert.True(File.Exists(dumpPath), $"Missing fixture: {dumpPath}");
        var physics = GfxObjDumpSerializer.Hydrate(GfxObjDumpSerializer.Read(dumpPath));
        cache.RegisterGfxObjForTest(StepGfxObjId, physics);
        float bspR = physics.BoundingSphere?.Radius ?? 1.06f;

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  StairLandblock,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        for (int k = 0; k < StepCount; k++)
        {
            var origin = Step0Origin + new Vector3(0f, 0.5f * k, 0.4f * k);
            engine.ShadowObjects.Register(
                entityId:      0xF6820100u + (uint)k,
                gfxObjId:      StepGfxObjId,
                worldPos:      origin,
                rotation:      StepRot,
                radius:        bspR,
                worldOffsetX:  0f,
                worldOffsetY:  0f,
                landblockId:   StairLandblock,
                collisionType: ShadowCollisionType.BSP,
                scale:         1.0f,
                seedCellId:    StairCellId);
        }

        return engine;
    }

    private static PhysicsBody GroundedOnTread()
    {
        var tread = new Plane(new Vector3(0f, -0.62469506f, 0.78086877f), 0.765995f);
        return new PhysicsBody
        {
            Position             = new Vector3(131.72375f, 77.49132f, 61.146755f),
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = tread,
            ContactPlaneCellId   = StairCellId,
            WalkablePolygonValid = true,
            WalkablePlane        = tread,
            WalkableUp           = Vector3.UnitZ,
            WalkableVertices     = new[]
            {
                new Vector3(132.75f, 77.495f, 61.015f),
                new Vector3(131.25f, 77.495f, 61.015f),
                new Vector3(131.25f, 76.995f, 60.615f),
                new Vector3(132.75f, 76.995f, 60.615f),
            },
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }

    [Fact]
    public void OutdoorStairs_ForwardRun_ClimbsPastSeam_NoWedge()
    {
        var engine = BuildStairEngine();
        var body   = GroundedOnTread();

        var pos  = body.Position;
        uint cell = StairCellId;
        ResolveResult r = default;

        for (int i = 0; i < 12; i++)
        {
            var target = new Vector3(pos.X, pos.Y + 0.2f, pos.Z);
            r = engine.ResolveWithTransition(
                currentPos:     pos,
                targetPos:      target,
                cellId:         cell,
                sphereRadius:   0.48f,
                sphereHeight:   1.835f,
                stepUpHeight:   0.6f,
                stepDownHeight: 1.5f,
                isOnGround:     true,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u);

            _out.WriteLine(
                $"f{i}: out=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) cell=0x{r.CellId:X8} " +
                $"cnV={r.CollisionNormalValid} cn=({r.CollisionNormal.X:F2},{r.CollisionNormal.Y:F2},{r.CollisionNormal.Z:F2}) " +
                $"sliding={body.TransientState.HasFlag(TransientStateFlags.Sliding)} " +
                $"sN=({body.SlidingNormal.X:F2},{body.SlidingNormal.Y:F2},{body.SlidingNormal.Z:F2})");

            pos  = r.Position;
            cell = r.CellId;
            body.Position = pos;
        }

        Assert.True(pos.Y > 78.10f,
            $"Player must climb past the tread seam (reached Y={pos.Y:F3}); pinned at ~77.9 = the " +
            $"Fabricated-precipice wedge (PrecipiceSlide horizontal sliding normal absorbs +Y).");
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "A continuous walkable ramp seam must not persist a horizontal sliding normal.");
    }

    [Fact]
    public void OutdoorStairs_SideWallContact_DoesNotReverseDownhill()
    {
        var engine = BuildStairEngine();
        var body = CapturedSideWallBody(x: 133.03775f);

        ResolveResult result = engine.ResolveWithTransition(
            currentPos: body.Position,
            targetPos: body.Position + new Vector3(0.30007935f, 0.8837738f, 0f),
            cellId: StairCellId,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            stepUpHeight: 0.6f,
            stepDownHeight: 1.5f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x01000000u);

        _out.WriteLine(
            $"out=({result.Position.X:F6},{result.Position.Y:F6},{result.Position.Z:F6}) " +
            $"collision={result.CollisionNormalValid} " +
            $"normal=({result.CollisionNormal.X:F3},{result.CollisionNormal.Y:F3},{result.CollisionNormal.Z:F3})");

        Assert.True(result.Position.Y >= body.Position.Y - 0.001f,
            $"Side-wall response reversed the intended uphill motion: " +
            $"{body.Position.Y:F6} -> {result.Position.Y:F6}.");
        Assert.True(result.Position.Z >= body.Position.Z - 0.001f,
            $"Side-wall response dropped the grounded player downhill: " +
            $"{body.Position.Z:F6} -> {result.Position.Z:F6}.");
    }

    [Fact]
    public void OutdoorStairs_SideWallContact_InsideHalfRadius_AdvancesUphill()
    {
        var engine = BuildStairEngine();
        var body = CapturedSideWallBody(x: 132.95f);

        ResolveResult result = engine.ResolveWithTransition(
            currentPos: body.Position,
            targetPos: body.Position + new Vector3(0.30007935f, 0.8837738f, 0f),
            cellId: StairCellId,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            stepUpHeight: 0.6f,
            stepDownHeight: 1.5f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x01000000u);

        _out.WriteLine(
            $"inside-support out=({result.Position.X:F6},{result.Position.Y:F6},"
            + $"{result.Position.Z:F6}) collision={result.CollisionNormalValid} "
            + $"normal=({result.CollisionNormal.X:F3},{result.CollisionNormal.Y:F3},"
            + $"{result.CollisionNormal.Z:F3})");

        Assert.True(result.Position.Y > body.Position.Y + 0.25f,
            $"Inside-half-radius support failed to preserve meaningful uphill motion: "
            + $"{body.Position.Y:F6} -> {result.Position.Y:F6}.");
        Assert.True(result.Position.Z > body.Position.Z + 0.10f,
            $"Inside-half-radius support failed to climb the tread: "
            + $"{body.Position.Z:F6} -> {result.Position.Z:F6}.");
    }

    private static PhysicsBody CapturedSideWallBody(float x)
    {
        PhysicsBody body = GroundedOnTread();
        body.Position = new Vector3(x, 75.53931f, 59.608147f);
        var tread = new Plane(
            new Vector3(3.2782555e-07f, -0.62469506f, 0.78086877f),
            0.75193405f);
        body.ContactPlane = tread;
        body.WalkablePlane = tread;

        Vector3 delta = new(0f, 1.5f, 1.2f);
        Vector3[] vertices = Assert.IsType<Vector3[]>(body.WalkableVertices);
        body.WalkableVertices = Array.ConvertAll(vertices, vertex => vertex - delta);
        return body;
    }

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate AcDream.slnx from " + AppContext.BaseDirectory);
    }
}
