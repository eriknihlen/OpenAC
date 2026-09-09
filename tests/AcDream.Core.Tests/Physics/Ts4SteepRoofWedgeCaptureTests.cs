using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class Ts4SteepRoofWedgeCaptureTests
{
    private readonly ITestOutputHelper _out;
    public Ts4SteepRoofWedgeCaptureTests(ITestOutputHelper output) => _out = output;

    private const uint CellId = 0xA9B40001u;
    private const int TicksPerSecond = 30;
    private const int MaxTicks = 3 * TicksPerSecond;
    private const int WedgeTickThreshold = 15;  // 0.5 s of zero motion == wedged
    private const float WedgeEpsilon = 0.001f;  // 1 mm

    private static PhysicsEngine MakeSlopeEngine()
    {
        var (root, resolved) = BSPStepUpFixtures.SlopedUnwalkable();

        const uint LandblockId = 0xA9B4FFFFu;
        const uint SyntheticGfxId = 0xDEADBEEFu;

        var heights = new byte[81];
        var heightTab = new float[256];
        for (int i = 0; i < 256; i++) heightTab[i] = -1000f;   // terrain never interferes

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            LandblockId,
            new TerrainSurface(heights, heightTab),
            System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        var cache = new PhysicsDataCache();
        var bspTree = new DatReaderWriter.Types.PhysicsBSPTree { Root = root };
        var physics = new GfxObjPhysics
        {
            BSP = bspTree,
            PhysicsPolygons = new System.Collections.Generic.Dictionary<ushort, DatReaderWriter.Types.Polygon>(),
            Vertices = new DatReaderWriter.Types.VertexArray(),
            Resolved = resolved,
            BoundingSphere = new DatReaderWriter.Types.Sphere { Origin = Vector3.Zero, Radius = 15f },
        };
        cache.RegisterGfxObjForTest(SyntheticGfxId, physics);
        engine.DataCache = cache;

        engine.ShadowObjects.Register(
            entityId: SyntheticGfxId,
            gfxObjId: SyntheticGfxId,
            worldPos: Vector3.Zero,
            rotation: Quaternion.Identity,
            radius: 15f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId,
            collisionType: ShadowCollisionType.BSP,
            scale: 1.0f);

        return engine;
    }

    [Fact]
    public void FallOntoSteepSlope_PureVertical_NeverWedgesWithinThreeSeconds()
    {
        var engine = MakeSlopeEngine();
        float r = BSPStepUpFixtures.SphereRadius;
        const float dt = 1f / TicksPerSecond;
        const float gravity = -9.8f;

        var body = new PhysicsBody
        {
            TransientState = TransientStateFlags.Active,
        };

        // Start well above the slope's mid-face (slope spans x in [0,1], z in
        // [0,2] at that x-range), falling straight down.
        Vector3 pos = new(0.5f, 0f, 3.0f);
        float fallVelocityZ = 0f;
        uint cell = CellId;

        Vector3 start = pos;
        int frozenStreak = 0;

        for (int tick = 0; tick < MaxTicks; tick++)
        {
            fallVelocityZ += gravity * dt;
            Vector3 target = pos + new Vector3(0f, 0f, fallVelocityZ * dt);

            var result = engine.ResolveWithTransition(
                currentPos: pos,
                targetPos: target,
                cellId: cell,
                sphereRadius: r,
                sphereHeight: r * 2f,
                stepUpHeight: 0.30f,
                stepDownHeight: 0.04f,
                isOnGround: body.OnWalkable,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u);

            var newPos = result.Position;
            float moved = Vector3.Distance(newPos, pos);

            if (moved < WedgeEpsilon)
                frozenStreak++;
            else
                frozenStreak = 0;

            _out.WriteLine(
                $"t{tick,3}: pos=({newPos.X:F3},{newPos.Y:F3},{newPos.Z:F3}) " +
                $"moved={moved:F4} onGround={result.IsOnGround} onWalkable={result.OnWalkable} " +
                $"contact={result.InContact} vz={fallVelocityZ:F2} frozen={frozenStreak}");

            pos = newPos;
            cell = result.CellId;
            body.Position = pos;

            if (result.IsOnGround)
                fallVelocityZ = 0f;

            body.TransientState &=
                ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
            if (result.InContact)
                body.TransientState |= TransientStateFlags.Contact;
            if (result.OnWalkable)
                body.TransientState |= TransientStateFlags.OnWalkable;

            Assert.True(frozenStreak <= WedgeTickThreshold,
                $"Body froze for {frozenStreak} ticks at {pos}.");

        }

        Assert.True(pos.X < start.X - 0.10f,
            $"The resolver-only trace made no downhill progress within " +
            $"{MaxTicks} ticks; start={start}, final={pos}.");
    }
}
