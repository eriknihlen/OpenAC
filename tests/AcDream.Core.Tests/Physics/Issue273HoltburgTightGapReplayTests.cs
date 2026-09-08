using System;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public sealed class Issue273HoltburgTightGapReplayTests
{
    private readonly ITestOutputHelper _output;

    public Issue273HoltburgTightGapReplayTests(ITestOutputHelper output)
        => _output = output;

    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = 0xA9B40032u;
    private const uint ShellGfxObj = 0x01000F69u;
    private const uint PlayerEntity = 0x000F4243u;

    private static readonly Vector3 BuildingOrigin = new(158.178f, 37.7055f, 94f);
    private static readonly Quaternion BuildingRotation =
        Quaternion.Normalize(new Quaternion(0f, 0f, -0.343045f, 0.939319f));
    private static readonly Matrix4x4 BuildingTransform =
        Matrix4x4.CreateFromQuaternion(BuildingRotation)
        * Matrix4x4.CreateTranslation(BuildingOrigin);

    private static readonly ImmutableArray<FlatCollisionSphere> PlayerSpheres =
    [
        new FlatCollisionSphere(new Vector3(0f, 0f, 0.475f), 0.480f),
        new FlatCollisionSphere(new Vector3(0f, 0f, 1.350f), 0.480f),
    ];

    private static PhysicsEngine BuildEngine(
        bool preparedFlat = false,
        bool includeShell = true,
        bool includeBlockingPost = true)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        string dumpPath = Path.Combine(
            SolutionRoot(),
            "tests",
            "AcDream.Core.Tests",
            "Fixtures",
            "issue273",
            "0x01000F69.gfxobj.json");
        Assert.True(File.Exists(dumpPath), $"Missing issue #273 fixture: {dumpPath}");
        GfxObjPhysics physics = GfxObjDumpSerializer.Hydrate(
            GfxObjDumpSerializer.Read(dumpPath));
        if (preparedFlat)
        {
            cache.CollisionTraversalMode = CollisionTraversalMode.Flat;
            cache.CacheGfxObj(
                ShellGfxObj,
                FlatCollisionAssetBuilder.FlattenGfxObj(physics));
        }
        else
        {
            cache.RegisterGfxObjForTest(ShellGfxObj, physics);
        }

        if (includeShell)
        {
            cache.CacheBuilding(
                Cell,
                Array.Empty<BldPortalInfo>(),
                BuildingTransform,
                ShellGfxObj);
        }

        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, includeShell ? -1000f : 96f);
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);

        if (includeBlockingPost)
        {
            RegisterPost(
                engine,
                0xCA9B4027u,
                new Vector3(160.173f, 34.487f, 95.975f),
                radius: 0.282f);
        }
        RegisterPost(
            engine,
            0xCA9B402Eu,
            new Vector3(158.282f, 34.610f, 95.975f),
            radius: 0.282f);
        RegisterPost(
            engine,
            0xCA9B402Fu,
            new Vector3(157.952f, 32.239f, 96f),
            radius: 0.600f);

        return engine;
    }

    private static void RegisterPost(
        PhysicsEngine engine,
        uint entityId,
        Vector3 basePosition,
        float radius)
    {
        engine.ShadowObjects.Register(
            entityId,
            gfxObjId: 0u,
            worldPos: basePosition,
            rotation: Quaternion.Identity,
            radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 5.564f,
            state: 0u,
            seedCellId: Cell,
            isStatic: true);
    }

    private static PhysicsBody GroundedBody(Vector3 position)
    {
        Vector3[] localWalkable =
        [
            new(4f, 7.25f, 2f),
            new(3.3f, 6.5003f, 2f),
            new(3.3f, -2.0157f, 2f),
            new(4f, -4.7f, 2f),
        ];
        var worldWalkable = new Vector3[localWalkable.Length];
        for (int i = 0; i < localWalkable.Length; i++)
            worldWalkable[i] = Vector3.Transform(localWalkable[i], BuildingTransform);

        var floor = new Plane(Vector3.UnitZ, -96f);
        return new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            ContactPlaneValid = true,
            ContactPlane = floor,
            ContactPlaneCellId = Cell,
            WalkablePolygonValid = true,
            WalkablePlane = floor,
            WalkableUp = Vector3.UnitZ,
            WalkableVertices = worldWalkable,
            TransientState =
                TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }

    [Theory]
    [InlineData(1.239f, true)]
    [InlineData(1.241f, false)]
    public void RetailHalfRadiusSupport_RejectsCenterBeyondQuarterMeterEdge(
        float centerX,
        bool expected)
    {
        Vector3[] floor =
        [
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(1f, 1f, 0f),
            new(0f, 1f, 0f),
        ];

        bool supported = BSPQuery.CheckWalkableSupport(
            new Plane(Vector3.UnitZ, 0f),
            floor,
            new Vector3(centerX, 0.5f, 0.48f),
            supportRadius: 0.24f,
            Vector3.UnitZ);

        Assert.Equal(expected, supported);
    }

    [Fact]
    public void CapturedRun_DoesNotSqueezeBetweenPostAndBuilding()
    {
        ReplayFrame[] trace = RunCapturedReplay(BuildEngine());
        Vector3 position = trace[^1].Result.Position;

        Assert.True(
            position.Y < 34.487f,
            $"Player squeezed through the retail-blocked gap: "
            + $"final=({position.X:F3},{position.Y:F3},{position.Z:F3}).");
    }

    [Fact]
    public void CapturedRun_PreparedFlatMatchesGraphForEveryFrame()
    {
        ReplayFrame[] graph = RunCapturedReplay(BuildEngine());
        ReplayFrame[] preparedFlat = RunCapturedReplay(
            BuildEngine(preparedFlat: true));

        Assert.Equal(graph.Length, preparedFlat.Length);
        for (int frame = 0; frame < graph.Length; frame++)
            AssertFrameBitwise(graph[frame], preparedFlat[frame], frame);
    }

    [Theory]
    [InlineData(false, true, "shell")]
    [InlineData(true, false, "blocking post")]
    public void CapturedRun_RequiresBothShellAndBlockingPost(
        bool includeShell,
        bool includeBlockingPost,
        string omittedGeometry)
    {
        PhysicsEngine engine = BuildEngine(
            includeShell: includeShell,
            includeBlockingPost: includeBlockingPost);
        PhysicsBody body = GroundedBody(new Vector3(160.016f, 33.562f, 96.005f));
        if (!includeShell)
        {
            body.WalkablePolygonValid = false;
            body.WalkableVertices = null;
        }

        ReplayFrame[] trace = RunCapturedReplay(engine, body);
        Vector3 final = trace[^1].Result.Position;

        Assert.True(
            final.Y > 34.487f,
            $"Omitting the {omittedGeometry} still reproduced the complete "
            + $"fixture's block: final=({final.X:F3},{final.Y:F3},{final.Z:F3}).");
    }

    private ReplayFrame[] RunCapturedReplay(
        PhysicsEngine engine,
        PhysicsBody? body = null)
    {
        Vector3 position = new(160.016f, 33.562f, 96.005f);
        body ??= GroundedBody(position);
        uint cell = Cell;

        Vector3[] offsets =
        [
            new(1.396f, 1.123f, 0f),
            new(0.727f, 0.584f, 0f),
            new(0.624f, 0.501f, 0f),
            new(0.727f, 0.585f, 0f),
            new(0.728f, 0.585f, 0f),
            new(0.727f, 0.585f, 0f),
            new(0.727f, 0.585f, 0f),
            new(0.728f, 0.585f, 0f),
        ];
        var trace = new ReplayFrame[offsets.Length];

        for (int frame = 0; frame < offsets.Length; frame++)
        {
            ResolveResult result = engine.ResolveWithTransition(
                currentPos: position,
                targetPos: position + offsets[frame],
                cellId: cell,
                sphereRadius: 0.48f,
                sphereHeight: 1.835f,
                stepUpHeight: 0.6f,
                stepDownHeight: 1.5f,
                isOnGround: true,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: PlayerEntity,
                sphereList: PlayerSpheres,
                sphereScale: 1f);

            _output.WriteLine(
                $"f{frame}: in=({position.X:F3},{position.Y:F3},{position.Z:F3}) "
                + $"out=({result.Position.X:F3},{result.Position.Y:F3},{result.Position.Z:F3}) "
                + $"hit={result.CollisionNormalValid} "
                + $"normal=({result.CollisionNormal.X:F3},"
                + $"{result.CollisionNormal.Y:F3},{result.CollisionNormal.Z:F3})");

            position = result.Position;
            cell = result.CellId;
            body.Position = position;
            trace[frame] = new ReplayFrame(
                result,
                body.TransientState,
                body.ContactPlaneValid,
                body.ContactPlane,
                body.ContactPlaneCellId,
                body.ContactPlaneIsWater,
                body.WalkablePolygonValid,
                body.WalkablePlane,
                body.WalkableUp,
                body.SlidingNormal);
        }

        return trace;
    }

    private static void AssertFrameBitwise(
        ReplayFrame expected,
        ReplayFrame actual,
        int frame)
    {
        string context = $"frame {frame}";
        AssertVectorBitwise(expected.Result.Position, actual.Result.Position, context);
        Assert.Equal(expected.Result.CellId, actual.Result.CellId);
        Assert.Equal(expected.Result.IsOnGround, actual.Result.IsOnGround);
        Assert.Equal(
            expected.Result.CollisionNormalValid,
            actual.Result.CollisionNormalValid);
        AssertVectorBitwise(
            expected.Result.CollisionNormal,
            actual.Result.CollisionNormal,
            context);
        Assert.Equal(expected.Result.Ok, actual.Result.Ok);
        AssertQuaternionBitwise(
            expected.Result.Orientation,
            actual.Result.Orientation,
            context);
        Assert.Equal(expected.Result.InContact, actual.Result.InContact);
        Assert.Equal(expected.Result.OnWalkable, actual.Result.OnWalkable);
        AssertPlaneBitwise(
            expected.Result.ContactPlane,
            actual.Result.ContactPlane,
            context);
        Assert.Equal(
            expected.Result.ContactPlaneCellId,
            actual.Result.ContactPlaneCellId);
        Assert.Equal(
            expected.Result.ContactPlaneIsWater,
            actual.Result.ContactPlaneIsWater);
        Assert.Equal(expected.TransientState, actual.TransientState);
        Assert.Equal(expected.ContactPlaneValid, actual.ContactPlaneValid);
        AssertPlaneBitwise(expected.ContactPlane, actual.ContactPlane, context);
        Assert.Equal(expected.ContactPlaneCellId, actual.ContactPlaneCellId);
        Assert.Equal(expected.ContactPlaneIsWater, actual.ContactPlaneIsWater);
        Assert.Equal(expected.WalkablePolygonValid, actual.WalkablePolygonValid);
        AssertPlaneBitwise(expected.WalkablePlane, actual.WalkablePlane, context);
        AssertVectorBitwise(expected.WalkableUp, actual.WalkableUp, context);
        AssertVectorBitwise(expected.SlidingNormal, actual.SlidingNormal, context);
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

    private readonly record struct ReplayFrame(
        ResolveResult Result,
        TransientStateFlags TransientState,
        bool ContactPlaneValid,
        Plane ContactPlane,
        uint ContactPlaneCellId,
        bool ContactPlaneIsWater,
        bool WalkablePolygonValid,
        Plane WalkablePlane,
        Vector3 WalkableUp,
        Vector3 SlidingNormal);

    private static string SolutionRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "AcDream.slnx")))
                return directory;
            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException(
            $"Could not locate AcDream.slnx from {AppContext.BaseDirectory}.");
    }
}
