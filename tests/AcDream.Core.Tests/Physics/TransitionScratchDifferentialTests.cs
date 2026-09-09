using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class TransitionScratchDifferentialTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;
    private const uint GfxObjId = 0x0100F100u;

    [Fact]
    public void ReusedScratch_MatchesFreshAcrossTransitionFamilies()
    {
        RunSequence(
            reuse => BuildBspEngine(reuse, BSPStepUpFixtures.TallWall, 0f),
            new ResolveSpec(
                "grounded two-sphere wall",
                new Vector3(0.10f, 0f, 0f),
                new Vector3(0.60f, 0f, 0f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.04f,
                0.40f,
                true,
                GroundedBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000001u,
                Coverage: CoverageKind.WallBlock),
            new ResolveSpec(
                "airborne wall collision",
                new Vector3(0.10f, 0f, 2.00f),
                new Vector3(0.60f, 0f, 1.50f),
                BSPStepUpFixtures.SphereRadius,
                0f,
                0.04f,
                0.04f,
                false,
                AirborneBody,
                ObjectInfoState.EdgeSlide,
                0x80000001u,
                Coverage: CoverageKind.AirborneDescent),
            new ResolveSpec(
                "carried sliding normal",
                new Vector3(0.10f, 0f, 2.00f),
                new Vector3(0.60f, 0.12f, 1.50f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.04f,
                0.40f,
                false,
                SlidingBody,
                ObjectInfoState.EdgeSlide,
                0x80000002u,
                Coverage: CoverageKind.AirborneDescent),
            new ResolveSpec(
                "projectile single sphere",
                new Vector3(0.10f, 0f, 2.00f),
                new Vector3(0.60f, 0f, 2.00f),
                0.05f,
                0f,
                0f,
                0f,
                false,
                ProjectileBody,
                ObjectInfoState.None,
                0x80000003u,
                BeginOrientation: Quaternion.Identity,
                EndOrientation: Quaternion.CreateFromAxisAngle(
                    Vector3.UnitZ,
                    0.25f),
                DesignatedTargetId: 0x50000099u,
                Coverage: CoverageKind.Success),
            new ResolveSpec(
                "viewer camera",
                new Vector3(0.10f, 0f, 2.00f),
                new Vector3(0.60f, 0f, 2.00f),
                0.30f,
                0f,
                0f,
                0f,
                false,
                static () => null,
                ObjectInfoState.IsViewer
                | ObjectInfoState.PathClipped
                | ObjectInfoState.FreeRotate
                | ObjectInfoState.PerfectClip,
                0u,
                Coverage: CoverageKind.Success));

        RunSequence(
            reuse => BuildBspEngine(reuse, BSPStepUpFixtures.LowStep, 0f),
            new ResolveSpec(
                "step up",
                new Vector3(0.10f, 0f, 0f),
                new Vector3(0.60f, 0f, 0f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.35f,
                0.60f,
                true,
                GroundedBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000011u,
                Coverage: CoverageKind.StepUp),
            new ResolveSpec(
                "step down",
                new Vector3(0.80f, 0f, 0.25f),
                new Vector3(0.10f, 0f, 0.25f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.35f,
                0.60f,
                true,
                UpperGroundedBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000012u,
                Coverage: CoverageKind.Success));

        RunSequence(
            reuse => BuildBspEngine(reuse, BSPStepUpFixtures.FlatRoof, -50f),
            new ResolveSpec(
                "airborne roof landing",
                new Vector3(0f, 0f, 3.10f),
                new Vector3(0f, 0f, 2.75f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.04f,
                0.40f,
                false,
                AirborneBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000021u,
                Coverage: CoverageKind.RoofLanding));

        RunSequence(
            reuse => BuildOpenEngine(reuse, includeLandblock: true),
            new ResolveSpec(
                "open outdoor success",
                new Vector3(8f, 8f, 0f),
                new Vector3(8.25f, 8f, 0f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.40f,
                1.50f,
                true,
                GroundedBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000031u,
                Coverage: CoverageKind.Success));

        RunSequence(
            reuse => BuildOpenEngine(reuse, includeLandblock: false),
            new ResolveSpec(
                "missing-cell failure",
                new Vector3(8f, 8f, 2f),
                new Vector3(8.25f, 8f, 2f),
                BSPStepUpFixtures.SphereRadius,
                1.20f,
                0.40f,
                1.50f,
                false,
                AirborneBody,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000032u,
                CellId: 0u,
                Coverage: CoverageKind.Failure));
    }

    [Fact]
    public void ReusedScratch_MatchesFreshPlacementSearch()
    {
        PhysicsEngine fresh = BuildPlacementEngine(reuse: false);
        PhysicsEngine reused = BuildPlacementEngine(reuse: true);

        PhysicsSetPositionResult expected = fresh.SetPosition(
            PlacementRequest(
                new Vector3(10f, 10f, 0f),
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000101u));
        PhysicsSetPositionResult actual = reused.SetPosition(
            PlacementRequest(
                new Vector3(10f, 10f, 0f),
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                0x50000101u));

        AssertSetPositionBitwise(expected, actual, "placement");
        Assert.True(expected.IsCommitted, "fresh engine did not commit the placement");
        Assert.True(actual.IsCommitted, "reused engine did not commit the placement");

        expected = fresh.SetPosition(
            PlacementRequest(
                new Vector3(11f, 10f, 0f),
                ObjectInfoState.EdgeSlide,
                0x80000102u));
        actual = reused.SetPosition(
            PlacementRequest(
                new Vector3(11f, 10f, 0f),
                ObjectInfoState.EdgeSlide,
                0x80000102u));

        AssertSetPositionBitwise(expected, actual, "placement after hostile identity");
        Assert.True(
            expected.IsCommitted,
            "fresh engine did not commit the second placement");
        Assert.True(
            actual.IsCommitted,
            "reused engine did not commit the second placement");
    }

    private static PhysicsSetPositionRequest PlacementRequest(
        Vector3 position,
        ObjectInfoState moverFlags,
        uint movingEntityId) => new(
            Position: position,
            Orientation: Quaternion.Identity,
            CellId: Cell,
            CellLocalPosition: position,
            Spheres: ImmutableArray.Create(
                new FlatCollisionSphere(new Vector3(0f, 0f, 0.48f), 0.48f),
                new FlatCollisionSphere(new Vector3(0f, 0f, 1.835f - 0.48f), 0.48f)),
            Scale: 1f,
            StepUpHeight: 0.40f,
            StepDownHeight: 0.40f,
            MoverFlags: moverFlags,
            MovingEntityId: movingEntityId,
            Flags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);

    private static void RunSequence(
        Func<bool, PhysicsEngine> buildEngine,
        params ResolveSpec[] specs)
    {
        PhysicsEngine fresh = buildEngine(false);
        PhysicsEngine reused = buildEngine(true);

        // Repeat in alternating directions. This leaves every retained field
        // exposed to a materially different next mover and branch family.
        for (int pass = 0; pass < 4; pass++)
        {
            for (int index = 0; index < specs.Length; index++)
            {
                int specIndex = (pass & 1) == 0
                    ? index
                    : specs.Length - 1 - index;
                ResolveSpec spec = specs[specIndex];
                PhysicsBody? expectedBody = spec.BodyFactory();
                PhysicsBody? actualBody = spec.BodyFactory();

                ResolveResult expected = spec.Resolve(fresh, expectedBody);
                ResolveResult actual = spec.Resolve(reused, actualBody);

                AssertCoverage(spec, expected);
                AssertResolveBitwise(expected, actual, spec.Name);
                AssertBodyBitwise(expectedBody, actualBody, spec.Name);
            }
        }
    }

    private static PhysicsEngine BuildBspEngine(
        bool reuse,
        Func<(PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved)> fixture,
        float terrainZ)
    {
        var (root, resolved) = fixture();
        PhysicsEngine engine = BuildOpenEngine(reuse, includeLandblock: true, terrainZ);
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
                Radius = 15f,
            },
        });
        engine.DataCache = cache;
        engine.ShadowObjects.Register(
            entityId: GfxObjId,
            gfxObjId: GfxObjId,
            worldPos: Vector3.Zero,
            rotation: Quaternion.Identity,
            radius: 15f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.BSP,
            scale: 1f);
        return engine;
    }

    private static PhysicsEngine BuildOpenEngine(
        bool reuse,
        bool includeLandblock,
        float terrainZ = 0f)
    {
        var engine = new PhysicsEngine(reuse);
        if (!includeLandblock)
            return engine;

        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, terrainZ);
        engine.AddLandblock(
            Landblock | 0xFFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static PhysicsEngine BuildPlacementEngine(bool reuse)
    {
        PhysicsEngine engine = BuildOpenEngine(reuse, includeLandblock: true);
        engine.DataCache = new PhysicsDataCache();
        RegisterSphere(
            engine,
            0x50000101u,
            new Vector3(10f, 10f, 0.48f),
            EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsCreature);
        RegisterSphere(
            engine,
            0x50000102u,
            new Vector3(10.10f, 10f, 0.48f),
            EntityCollisionFlags.IsCreature);
        return engine;
    }

    private static void RegisterSphere(
        PhysicsEngine engine,
        uint entityId,
        Vector3 center,
        EntityCollisionFlags flags)
    {
        engine.ShadowObjects.Register(
            entityId,
            gfxObjId: 0u,
            worldPos: center,
            rotation: Quaternion.Identity,
            radius: 0.48f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f,
            scale: 1f,
            state: 0u,
            flags: flags,
            seedCellId: Cell,
            isStatic: false);
    }

    private static PhysicsBody GroundedBody()
        => new()
        {
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active
                             | TransientStateFlags.Contact
                             | TransientStateFlags.OnWalkable,
            ContactPlaneValid = true,
            ContactPlane = new Plane(Vector3.UnitZ, 0f),
            ContactPlaneCellId = Cell,
            WalkablePolygonValid = true,
            WalkablePlane = new Plane(Vector3.UnitZ, 0f),
            WalkableVertices =
            [
                new(-2f, -2f, 0f),
                new(2f, -2f, 0f),
                new(2f, 2f, 0f),
                new(-2f, 2f, 0f),
            ],
            WalkableUp = Vector3.UnitZ,
            Velocity = new Vector3(0.25f, 0.125f, 0f),
        };

    private static PhysicsBody UpperGroundedBody()
    {
        PhysicsBody body = GroundedBody();
        body.ContactPlane = new Plane(Vector3.UnitZ, -0.25f);
        body.WalkablePlane = new Plane(Vector3.UnitZ, -0.25f);
        body.WalkableVertices =
        [
            new(0.2f, -2f, 0.25f),
            new(2f, -2f, 0.25f),
            new(2f, 2f, 0.25f),
            new(0.2f, 2f, 0.25f),
        ];
        return body;
    }

    private static PhysicsBody AirborneBody()
        => new()
        {
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
            Velocity = new Vector3(0.25f, 0f, -0.08f),
        };

    private static PhysicsBody SlidingBody()
        => new()
        {
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active
                             | TransientStateFlags.Sliding
                             | TransientStateFlags.StationaryFall,
            SlidingNormal = Vector3.UnitY,
            Velocity = new Vector3(0.25f, 0.125f, -0.08f),
        };

    private static PhysicsBody ProjectileBody()
        => new()
        {
            State = PhysicsStateFlags.Missile
                    | PhysicsStateFlags.PathClipped
                    | PhysicsStateFlags.Inelastic,
            TransientState = TransientStateFlags.Active,
            Velocity = new Vector3(20f, 0f, 0f),
        };

    private static void AssertResolveBitwise(
        ResolveResult expected,
        ResolveResult actual,
        string context)
    {
        AssertVectorBitwise(expected.Position, actual.Position, context);
        Assert.Equal(expected.CellId, actual.CellId);
        Assert.Equal(expected.IsOnGround, actual.IsOnGround);
        Assert.Equal(expected.CollisionNormalValid, actual.CollisionNormalValid);
        AssertVectorBitwise(expected.CollisionNormal, actual.CollisionNormal, context);
        Assert.Equal(expected.Ok, actual.Ok);
        AssertQuaternionBitwise(expected.Orientation, actual.Orientation, context);
        Assert.Equal(expected.InContact, actual.InContact);
        Assert.Equal(expected.OnWalkable, actual.OnWalkable);
        AssertPlaneBitwise(expected.ContactPlane, actual.ContactPlane, context);
        Assert.Equal(expected.ContactPlaneCellId, actual.ContactPlaneCellId);
        Assert.Equal(expected.ContactPlaneIsWater, actual.ContactPlaneIsWater);
    }

    private static void AssertSetPositionBitwise(
        PhysicsSetPositionResult expected,
        PhysicsSetPositionResult actual,
        string context)
    {
        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.Residence, actual.Residence);
        AssertVectorBitwise(expected.Position, actual.Position, context);
        AssertQuaternionBitwise(expected.Orientation, actual.Orientation, context);
        Assert.Equal(expected.CellId, actual.CellId);
        AssertVectorBitwise(
            expected.CellLocalPosition,
            actual.CellLocalPosition,
            $"{context}.CellLocalPosition");
        Assert.Equal(expected.InContact, actual.InContact);
        Assert.Equal(expected.OnWalkable, actual.OnWalkable);
        AssertPlaneBitwise(expected.ContactPlane, actual.ContactPlane, context);
        Assert.Equal(expected.ContactPlaneCellId, actual.ContactPlaneCellId);
        Assert.Equal(expected.ContactPlaneIsWater, actual.ContactPlaneIsWater);
        Assert.Equal(expected.SlidingNormalValid, actual.SlidingNormalValid);
        AssertVectorBitwise(
            expected.SlidingNormal,
            actual.SlidingNormal,
            $"{context}.SlidingNormal");
        Assert.Equal(expected.CollisionNormalValid, actual.CollisionNormalValid);
        AssertVectorBitwise(
            expected.CollisionNormal,
            actual.CollisionNormal,
            $"{context}.CollisionNormal");
        Assert.Equal(expected.FramesStationaryFall, actual.FramesStationaryFall);
        Assert.Equal(expected.CollidedWithEnvironment, actual.CollidedWithEnvironment);
        Assert.Equal(expected.CollisionHandlerResult, actual.CollisionHandlerResult);
        Assert.Equal(expected.CellChanged, actual.CellChanged);
        Assert.Equal(expected.ShadowAction, actual.ShadowAction);
        Assert.Equal(ToArrayOrEmpty(expected.CrossCellIds), ToArrayOrEmpty(actual.CrossCellIds));
        Assert.Equal(
            ToArrayOrEmpty(expected.CollidedObjectIds),
            ToArrayOrEmpty(actual.CollidedObjectIds));
        Assert.Equal(
            ToArrayOrEmpty(expected.QueriedCellIds),
            ToArrayOrEmpty(actual.QueriedCellIds));
    }

    private static uint[] ToArrayOrEmpty(ImmutableArray<uint> array) =>
        array.IsDefault ? Array.Empty<uint>() : array.ToArray();

    private static void AssertCoverage(ResolveSpec spec, ResolveResult result)
    {
        switch (spec.Coverage)
        {
            case CoverageKind.None:
                return;
            case CoverageKind.Success:
                Assert.True(result.Ok, $"{spec.Name} did not reach a successful transition.");
                return;
            case CoverageKind.Failure:
                Assert.False(result.Ok, $"{spec.Name} did not reach the failure branch.");
                return;
            case CoverageKind.WallBlock:
                Assert.True(
                    result.CollisionNormalValid
                    || result.Position.X < spec.TargetPos.X,
                    $"{spec.Name} did not exercise wall collision/blocking.");
                return;
            case CoverageKind.AirborneDescent:
                Assert.True(
                    result.Position.Z < spec.CurrentPos.Z,
                    $"{spec.Name} did not preserve airborne descent.");
                return;
            case CoverageKind.StepUp:
                Assert.True(
                    result.Position.Z >= 0.25f - PhysicsGlobals.EPSILON * 10f,
                    $"{spec.Name} did not reach the upper floor.");
                return;
            case CoverageKind.RoofLanding:
                Assert.True(
                    result.InContact || result.IsOnGround,
                    $"{spec.Name} did not reach the roof contact branch.");
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void AssertBodyBitwise(
        PhysicsBody? expected,
        PhysicsBody? actual,
        string context)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected is null, actual is null);
            return;
        }

        foreach (PropertyInfo property in typeof(PhysicsBody).GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? expectedValue = property.GetValue(expected);
            object? actualValue = property.GetValue(actual);
            string memberContext = $"{context}: PhysicsBody.{property.Name}";

            switch (expectedValue)
            {
                case float expectedFloat:
                    AssertFloatBitwise(
                        expectedFloat,
                        Assert.IsType<float>(actualValue),
                        memberContext);
                    break;
                case double expectedDouble:
                    Assert.Equal(
                        BitConverter.DoubleToInt64Bits(expectedDouble),
                        BitConverter.DoubleToInt64Bits(
                            Assert.IsType<double>(actualValue)));
                    break;
                case Vector3 expectedVector:
                    AssertVectorBitwise(
                        expectedVector,
                        Assert.IsType<Vector3>(actualValue),
                        memberContext);
                    break;
                case Quaternion expectedRotation:
                    AssertQuaternionBitwise(
                        expectedRotation,
                        Assert.IsType<Quaternion>(actualValue),
                        memberContext);
                    break;
                case Plane expectedPlane:
                    AssertPlaneBitwise(
                        expectedPlane,
                        Assert.IsType<Plane>(actualValue),
                        memberContext);
                    break;
                case Vector3[] expectedVertices:
                    {
                        Vector3[] actualVertices = Assert.IsType<Vector3[]>(actualValue);
                        Assert.Equal(expectedVertices.Length, actualVertices.Length);
                        for (int i = 0; i < expectedVertices.Length; i++)
                        {
                            AssertVectorBitwise(
                                expectedVertices[i],
                                actualVertices[i],
                                $"{memberContext}[{i}]");
                        }
                        break;
                    }
                case null:
                    Assert.Null(actualValue);
                    break;
                default:
                    Assert.Equal(expectedValue, actualValue);
                    break;
            }
        }
    }

    private static void AssertVectorBitwise(
        Vector3 expected,
        Vector3 actual,
        string context)
    {
        AssertFloatBitwise(expected.X, actual.X, $"{context}.X");
        AssertFloatBitwise(expected.Y, actual.Y, $"{context}.Y");
        AssertFloatBitwise(expected.Z, actual.Z, $"{context}.Z");
    }

    private static void AssertQuaternionBitwise(
        Quaternion expected,
        Quaternion actual,
        string context)
    {
        AssertFloatBitwise(expected.X, actual.X, $"{context}.X");
        AssertFloatBitwise(expected.Y, actual.Y, $"{context}.Y");
        AssertFloatBitwise(expected.Z, actual.Z, $"{context}.Z");
        AssertFloatBitwise(expected.W, actual.W, $"{context}.W");
    }

    private static void AssertPlaneBitwise(
        Plane expected,
        Plane actual,
        string context)
    {
        AssertVectorBitwise(expected.Normal, actual.Normal, $"{context}.Normal");
        AssertFloatBitwise(expected.D, actual.D, $"{context}.D");
    }

    private static void AssertFloatBitwise(
        float expected,
        float actual,
        string context)
        => Assert.True(
            BitConverter.SingleToInt32Bits(expected)
            == BitConverter.SingleToInt32Bits(actual),
            $"{context}: expected 0x{BitConverter.SingleToInt32Bits(expected):X8}, "
            + $"actual 0x{BitConverter.SingleToInt32Bits(actual):X8}");

    private sealed record ResolveSpec(
        string Name,
        Vector3 CurrentPos,
        Vector3 TargetPos,
        float SphereRadius,
        float SphereHeight,
        float StepUpHeight,
        float StepDownHeight,
        bool IsOnGround,
        Func<PhysicsBody?> BodyFactory,
        ObjectInfoState MoverFlags,
        uint MovingEntityId,
        Vector3? LocalSphereOrigin = null,
        Quaternion? BeginOrientation = null,
        Quaternion? EndOrientation = null,
        uint DesignatedTargetId = 0,
        uint CellId = TransitionScratchDifferentialTests.Cell,
        CoverageKind Coverage = CoverageKind.None)
    {
        internal ResolveResult Resolve(PhysicsEngine engine, PhysicsBody? body)
            => engine.ResolveWithTransition(
                CurrentPos,
                TargetPos,
                CellId,
                SphereRadius,
                SphereHeight,
                StepUpHeight,
                StepDownHeight,
                IsOnGround,
                body,
                MoverFlags,
                MovingEntityId,
                LocalSphereOrigin,
                BeginOrientation,
                EndOrientation,
                DesignatedTargetId);
    }

    private enum CoverageKind
    {
        None,
        Success,
        Failure,
        WallBlock,
        AirborneDescent,
        StepUp,
        RoofLanding,
    }
}
