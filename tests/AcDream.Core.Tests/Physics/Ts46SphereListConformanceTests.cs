using System;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class Ts46SphereListConformanceTests
{
    private readonly ITestOutputHelper _out;
    public Ts46SphereListConformanceTests(ITestOutputHelper output) => _out = output;

    private const uint TestLandblockId = 0xA9C50000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;

    private static readonly ImmutableArray<FlatCollisionSphere> HumanSetupSpheres =
        ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, 0.475f), 0.48f),
            new FlatCollisionSphere(new Vector3(0f, 0f, 1.350f), 0.48f));

    [Fact]
    public void SphereListInitPath_MatchesDatSpheresExactly_NotTheReconstruction()
    {
        var sp = new SpherePath();

        sp.InitPath(
            begin: Vector3.Zero,
            end: new Vector3(1f, 0f, 0f),
            cellId: TestCellId,
            spheres: HumanSetupSpheres,
            scale: 1f);

        Assert.Equal(2, sp.NumSphere);

        Assert.Equal(new Vector3(0f, 0f, 0.475f), sp.LocalSphere[0].Origin);
        Assert.Equal(0.48f, sp.LocalSphere[0].Radius);

        Assert.Equal(new Vector3(0f, 0f, 1.350f), sp.LocalSphere[1].Origin);
        Assert.Equal(0.48f, sp.LocalSphere[1].Radius);
        Assert.NotEqual(1.355f, sp.LocalSphere[1].Origin.Z);
    }

    [Fact]
    public void SphereListInitPath_AppliesScaleToOriginAndRadius()
    {
        var sp = new SpherePath();
        sp.InitPath(
            begin: Vector3.Zero,
            end: Vector3.Zero,
            cellId: TestCellId,
            spheres: HumanSetupSpheres,
            scale: 2f);

        Assert.Equal(new Vector3(0f, 0f, 0.95f), sp.LocalSphere[0].Origin);
        Assert.Equal(0.96f, sp.LocalSphere[0].Radius);
        Assert.Equal(new Vector3(0f, 0f, 2.70f), sp.LocalSphere[1].Origin);
        Assert.Equal(0.96f, sp.LocalSphere[1].Radius);
    }

    [Fact]
    public void SphereListInitPath_CapsAtTwoSpheres_MatchingRetailHardCap()
    {
        var threeSpheres = ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, 0.475f), 0.48f),
            new FlatCollisionSphere(new Vector3(0f, 0f, 1.350f), 0.48f),
            new FlatCollisionSphere(new Vector3(0f, 0f, 2.0f), 0.20f));

        var sp = new SpherePath();
        sp.InitPath(Vector3.Zero, Vector3.Zero, TestCellId, threeSpheres, scale: 1f);

        Assert.Equal(2, sp.NumSphere);
        Assert.Equal(1.350f, sp.LocalSphere[1].Origin.Z);
    }

    [Fact]
    public void SphereListInitPath_EmptyList_FallsBackToDummySphere()
    {
        var sp = new SpherePath();
        sp.InitPath(Vector3.Zero, Vector3.Zero, TestCellId, ImmutableArray<FlatCollisionSphere>.Empty);

        Assert.Equal(1, sp.NumSphere);
        Assert.Equal(PhysicsGlobals.DummySphereRadius, sp.LocalSphere[0].Radius);
    }

    [Fact]
    public void ScalarInitPath_IsByteForByteUnchanged_PreservingCapturedFixtureReplays()
    {
        var sp = new SpherePath();
        sp.InitPath(
            begin: Vector3.Zero,
            end: Vector3.Zero,
            cellId: TestCellId,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f);

        Assert.Equal(2, sp.NumSphere);
        Assert.Equal(new Vector3(0f, 0f, 0.48f), sp.LocalSphere[0].Origin);
        Assert.Equal(0.48f, sp.LocalSphere[0].Radius);
        // The reconstruction's own arithmetic: height − radius = 1.835 − 0.48 = 1.355.
        Assert.Equal(new Vector3(0f, 0f, 1.355f), sp.LocalSphere[1].Origin);
        Assert.Equal(0.48f, sp.LocalSphere[1].Radius);

        Assert.NotEqual(1.350f, sp.LocalSphere[1].Origin.Z);
    }

    [Fact]
    public void ResolveWithTransition_HonorsSphereListOverScalarDecoyWhenBothSupplied()
    {
        var engine = BuildEngine();
        const float HeadZ = 1.350f;
        RegisterObstacleSphere(engine, 0xD0D0u, x: 12f, y: 11.0f, z: HeadZ, radius: 0.30f);

        Vector3 pos = new(12f, 10f, 0f);
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.08f, 0f);

        for (int tick = 0; tick < 30; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                sphereRadius: 0.05f,
                sphereHeight: 0.10f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f,
                isOnGround: grounded,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                sphereList: HumanSetupSpheres,
                sphereScale: 1f);

            pos = result.Position;
            cellId = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})");

        Assert.True(pos.Y < 10.6f,
            "Supplying the real human sphere list must block the mover's head "
            + $"sphere at the head-height obstacle, proving sphereList (not the "
            + $"decoy scalar) drove the sweep; got Y={pos.Y:F3}");
        Assert.True(pos.Y > 9.9f,
            $"The mover must actually reach the obstacle, not stop early; got Y={pos.Y:F3}");
    }

    [Fact]
    public void ResolveWithTransition_EmptySphereList_FallsBackToScalarReconstruction()
    {
        var engine = BuildEngine();
        const float HeadZ = 1.350f;
        RegisterObstacleSphere(engine, 0xD0D1u, x: 12f, y: 11.0f, z: HeadZ, radius: 0.30f);

        Vector3 pos = new(12f, 10f, 0f);
        uint cellId = TestCellId;
        bool grounded = true;
        var perTick = new Vector3(0f, 0.08f, 0f);

        for (int tick = 0; tick < 30; tick++)
        {
            var result = engine.ResolveWithTransition(
                pos, pos + perTick, cellId,
                sphereRadius: 0.05f,
                sphereHeight: 0.10f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f,
                isOnGround: grounded,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);
                // sphereList omitted — default empty, legacy scalar reconstruction.

            pos = result.Position;
            cellId = result.CellId;
            grounded = result.IsOnGround;
        }

        _out.WriteLine($"final pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})");

        Assert.True(pos.Y > 11.9f,
            "With no sphereList supplied, the decoy capsule (too short to reach "
            + $"the head-height obstacle) must sail through untouched; got Y={pos.Y:F3}");
    }

    private static PhysicsEngine BuildEngine()
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        var heights = new byte[81];
        var heightTable = new float[256];   // all zero → terrain Z = 0
        engine.AddLandblock(
            landblockId: TestLandblockId,
            terrain: new TerrainSurface(heights, heightTable),
            cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static void RegisterObstacleSphere(
        PhysicsEngine engine, uint entityId, float x, float y, float z, float radius)
    {
        engine.ShadowObjects.Register(
            entityId, gfxObjId: 0u,
            new Vector3(x, y, z), Quaternion.Identity, radius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: TestLandblockId,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f, scale: 1f,
            state: 0u,
            flags: EntityCollisionFlags.IsCreature,
            isStatic: false);
    }
}
