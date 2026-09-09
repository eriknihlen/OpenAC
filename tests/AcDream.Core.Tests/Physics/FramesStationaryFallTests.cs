using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class FramesStationaryFallTests
{
    private readonly ITestOutputHelper _out;
    public FramesStationaryFallTests(ITestOutputHelper output) => _out = output;

    private const uint Lb = 0xA9B40000u;
    private const uint Cell = Lb | 0x0001u;
    private const float R = 0.48f, H = 1.835f, StepUp = 0.60f, StepDown = 0.04f;

    private static PhysicsEngine BuildEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            landblockId: Lb,
            terrain: new TerrainSurface(new byte[81], new float[256]),   // flat terrain at Z=0
            cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    private static void RegisterCreatureAt(PhysicsEngine e, uint id, Vector3 center, float radius = R)
    {
        e.ShadowObjects.Register(
            id, gfxObjId: 0u, center, Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: Lb,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f, scale: 1f, state: 0u,
            flags: EntityCollisionFlags.IsCreature, isStatic: false);
    }

    private static PhysicsBody AirborneBody(Vector3 pos) => new PhysicsBody
    {
        Position = pos,
        Orientation = Quaternion.Identity,
        State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.None,
    };

    private ResolveResult Push(PhysicsEngine engine, PhysicsBody body, Vector3 delta, uint cell)
        => engine.ResolveWithTransition(
            body.Position, body.Position + delta, cell,
            R, H, StepUp, StepDown, isOnGround: false, body: body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

    [Fact]
    public void AirborneJumpBlockedOverhead_FsfClimbsTo3_ThenResetsWhenAdvancing()
    {
        var engine = BuildEngine();
        RegisterCreatureAt(engine, 0xC0B0u, new Vector3(12f, 10f, 4.5f));   // directly overhead

        var body = AirborneBody(new Vector3(12f, 10f, 1f));
        uint cell = Cell;
        var up = new Vector3(0f, 0f, 0.6f);   // persistent jump intent

        int firstFrameFsf = -1, maxFsf = 0;
        for (int i = 0; i < 14; i++)
        {
            var r = Push(engine, body, up, cell);
            body.Position = r.Position; cell = r.CellId;
            if (i == 0) firstFrameFsf = body.FramesStationaryFall;
            maxFsf = Math.Max(maxFsf, body.FramesStationaryFall);
            _out.WriteLine($"frame{i,2}: z={body.Position.Z:F3} fsf={body.FramesStationaryFall} " +
                $"ts=0x{(uint)body.TransientState:X} onGround={r.IsOnGround}");
        }

        Assert.Equal(0, firstFrameFsf);
        Assert.True(maxFsf == 3, $"fsf must escalate to 3 while the jump is blocked overhead; got {maxFsf}");
        Assert.True(body.ContactPlaneValid, "fsf≥3 should manufacture a contact plane");
        Assert.True(body.ContactPlane.Normal.Z > 0.99f, "manufactured contact plane points up");
    }

    [Fact]
    public void GroundedWallSlide_DoesNotAccumulateFsf()
    {
        var engine = BuildEngine();
        RegisterCreatureAt(engine, 0xC0C0u, new Vector3(12f, 11.5f, R));

        var floor = new Plane(Vector3.UnitZ, 0f);
        var verts = new[]
        {
            new Vector3(-100f, -100f, 0f), new Vector3(100f, -100f, 0f),
            new Vector3(100f, 100f, 0f), new Vector3(-100f, 100f, 0f),
        };
        var body = new PhysicsBody
        {
            Position = new Vector3(12f, 10f, 0f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            ContactPlaneValid = true, ContactPlane = floor, ContactPlaneCellId = Cell,
            WalkablePolygonValid = true, WalkablePlane = floor, WalkableVertices = verts,
            WalkableUp = Vector3.UnitZ,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
        uint cell = Cell;

        int maxFsf = 0;
        for (int i = 0; i < 40; i++)
        {
            var r = engine.ResolveWithTransition(
                body.Position, body.Position + new Vector3(0f, 0.08f, 0f), cell,
                R, H, StepUp, StepDown, isOnGround: true, body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);
            body.Position = r.Position; cell = r.CellId;
            maxFsf = Math.Max(maxFsf, body.FramesStationaryFall);
        }
        _out.WriteLine($"grounded push maxFsf={maxFsf}");
        Assert.Equal(0, maxFsf);   // a grounded mover never accumulates a stuck-fall
    }
}
