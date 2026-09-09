using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class CathedralExteriorWallCollisionInstalledDatTests
    : IClassFixture<CathedralExteriorWallCollisionInstalledDatTests.Scene>
{
    private const uint PrimaryCell = 0xF4180012u;
    private const uint BuildingCell = 0xF418000Au;
    private const uint ShellGfx = 0x01001FB3u;
    private const uint HumanSetup = 0x02000001u;
    private const float MoverScale = 1f;
    private static readonly Vector3 Reported = new(48.002960f, 39.257545f, 160.004990f);
    private ImmutableArray<FlatCollisionSphere> HumanSpheres => _scene.Mover.Spheres;
    private float Radius => HumanSpheres[0].Radius * MoverScale;
    private float Height => _scene.Mover.Height * MoverScale;
    private float StepUp => _scene.Mover.StepUpHeight * MoverScale;
    private float StepDown => _scene.Mover.StepDownHeight * MoverScale;
    private readonly Scene _scene;
    private readonly ITestOutputHelper _output;

    public CathedralExteriorWallCollisionInstalledDatTests(Scene scene, ITestOutputHelper output)
        => (_scene, _output) = (scene, output);

    [Fact]
    public void OutsideApproach_IsBlockedByNeighboringPreparedShell()
    {
        AssertSceneAndCandidate();
        Vector3 from = Reported with { X = 48.6f };
        ResolveResult result = _scene.Engine.ResolveWithTransition(
            from, Reported, PrimaryCell, Radius, Height, StepUp, StepDown,
            isOnGround: true, body: GroundedBody(from),
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x000F4243u, sphereList: HumanSpheres);
        _output.WriteLine($"approach from={from} requested={Reported} actual={result.Position} ok={result.Ok}");
        Assert.True(result.Ok);
        Assert.InRange(result.Position.X, 48.479f, 48.601f);
        Assert.InRange(MathF.Abs(result.Position.Y - Reported.Y), 0f, 0.001f);
        Assert.InRange(MathF.Abs(result.Position.Z - Reported.Z), 0f, 0.01f);
    }

    [Fact]
    public void ReportedInitialOverlap_PlacementFindsClearPositionOutsideWall()
    {
        AssertSceneAndCandidate();
        PhysicsSetPositionResult result = _scene.Engine.SetPosition(new PhysicsSetPositionRequest(
            Reported, Quaternion.Identity, PrimaryCell, Reported, HumanSpheres,
            Scale: MoverScale, StepUpHeight: StepUp, StepDownHeight: StepDown,
            MoverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            MovingEntityId: 0x000F4243u,
            Flags: PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide
                | PhysicsSetPositionFlags.DoNotCreateCells));
        _output.WriteLine($"overlap requested={Reported} actual={result.Position} error={result.Error} residence={result.Residence}");
        Assert.True(result.IsCommitted);
        Assert.True(result.Position.X >= 48f + Radius - 0.001f);
        Assert.Equal(TransitionState.OK, QueryShellPlacement(result.Position));
    }

    [Fact]
    public void ClearExteriorPosition_PreservesFreeMovement()
    {
        AssertSceneAndCandidate();
        Vector3 from = Reported with { X = 49f };
        Vector3 target = from + new Vector3(0f, 0.1f, 0f);
        ResolveResult result = _scene.Engine.ResolveWithTransition(
            from, target, PrimaryCell, Radius, Height, StepUp, StepDown,
            isOnGround: true, body: GroundedBody(from),
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x000F4243u, sphereList: HumanSpheres);
        _output.WriteLine($"clear requested={target} actual={result.Position} ok={result.Ok}");
        Assert.True(result.Ok);
        Assert.InRange(Vector3.Distance(target, result.Position), 0f, 0.001f);
    }

    [Fact]
    public void MovementAwayFromExteriorWall_IsUnobstructed()
    {
        AssertSceneAndCandidate();
        Vector3 from = Reported with { X = 48.6f };
        Vector3 target = from + new Vector3(0.2f, 0f, 0f);
        ResolveResult result = _scene.Engine.ResolveWithTransition(
            from, target, PrimaryCell, Radius, Height, StepUp, StepDown,
            isOnGround: true, body: GroundedBody(from),
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x000F4243u, sphereList: HumanSpheres);
        _output.WriteLine($"away requested={target} actual={result.Position} ok={result.Ok}");
        Assert.True(result.Ok);
        Assert.InRange(Vector3.Distance(target, result.Position), 0f, 0.001f);
        Assert.Equal(TransitionState.OK, QueryShellPlacement(result.Position));
    }

    private void AssertSceneAndCandidate()
    {
        PhysicsDataCache cache = _scene.Cache;
        Assert.Equal(CollisionTraversalMode.Flat, cache.CollisionTraversalMode);
        BuildingPhysics building = Assert.IsType<BuildingPhysics>(cache.GetBuilding(BuildingCell));
        Assert.Equal(ShellGfx, building.ModelId);
        Assert.Equal(new Vector3(36f, 36f, 160f), building.WorldTransform.Translation);
        Assert.InRange(Vector3.Distance(-Vector3.UnitX,
            Vector3.TransformNormal(Vector3.UnitX, building.WorldTransform)), 0f, 0.000001f);
        Assert.InRange(Vector3.Distance(-Vector3.UnitY,
            Vector3.TransformNormal(Vector3.UnitY, building.WorldTransform)), 0f, 0.000001f);
        Assert.Null(cache.GetBuilding(PrimaryCell));
        GfxObjPhysics physics = Assert.IsType<GfxObjPhysics>(cache.GetGfxObj(ShellGfx));
        Assert.Null(physics.BSP);
        Assert.NotNull(physics.FlatPhysicsBsp);
        Assert.True(physics.FlatPhysicsBsp.RootIndex >= 0);
        Assert.Equal(449, physics.FlatPhysicsBsp.PolygonTable.Polygons.Length);
        Assert.NotNull(cache.CellGraph.GetVisible(BuildingCell));
        _output.WriteLine($"mover standard-human={HumanSetup:X8} scale={MoverScale} height={Height} steps={StepUp}/{StepDown} spheres={string.Join(';', HumanSpheres)}");
        Vector3 center = Reported + HumanSpheres[0].Origin * MoverScale;
        uint containing = CellTransit.FindCellSet(cache, center, Radius, PrimaryCell, out var cells);
        _output.WriteLine($"candidate containing={containing:X8} cells={string.Join(',', cells.Select(id => id.ToString("X8")))} shell={building.ModelId:X8} anchor={building.WorldTransform.Translation}");
        Assert.Equal(PrimaryCell, containing);
        Assert.Contains(BuildingCell, cells);
        Assert.Equal(PrimaryCell, _scene.Engine.SampleTerrainWalkable(center.X, center.Y)!.Value.CellId);
        Assert.Null(_scene.Engine.SampleTerrainWalkableInCell(BuildingCell, center.X, center.Y));
        Assert.Equal(TransitionState.Collided, QueryShellPlacement(Reported));
    }

    private TransitionState QueryShellPlacement(Vector3 position)
    {
        var transition = new Transition();
        transition.SpherePath.InitPath(position, position, PrimaryCell, HumanSpheres, MoverScale);
        transition.SpherePath.InsertType = InsertType.Placement;
        transition.SpherePath.BldgCheck = true;
        BuildingPhysics building = _scene.Cache.GetBuilding(BuildingCell)!;
        Assert.True(Matrix4x4.Decompose(building.WorldTransform, out _, out var rotation, out var origin));
        var inverse = Quaternion.Inverse(rotation);
        Vector3 local0 = Vector3.Transform(transition.SpherePath.GlobalSphere[0].Origin - origin, inverse);
        Vector3 local1 = Vector3.Transform(transition.SpherePath.GlobalSphere[1].Origin - origin, inverse);
        return CollisionTraversal.FindCollisions(_scene.Cache, _scene.Cache.GetGfxObj(ShellGfx)!,
            transition, local0, Radius, true, local1, HumanSpheres[1].Radius * MoverScale,
            local0, Vector3.Transform(Vector3.UnitZ, inverse), 1f, rotation, _scene.Engine, origin);
    }

    private static PhysicsBody GroundedBody(Vector3 position) => new()
    {
        Position = position,
        Orientation = Quaternion.Identity,
        ContactPlaneValid = true,
        ContactPlane = new Plane(Vector3.UnitZ, -160f),
        ContactPlaneCellId = PrimaryCell,
        TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
    };

    public sealed class Scene : IDisposable
    {
        private readonly BoundedTestDatCollection _dat;
        private readonly PakPreparedAssetSource _prepared;
        public PhysicsDataCache Cache { get; }
        public PhysicsEngine Engine { get; }
        public FlatSetupCollision Mover { get; }

        public Scene()
        {
            Assert.Equal("1", System.Environment.GetEnvironmentVariable("ACDREAM_RUN_INSTALLED_DAT_TESTS"));
            string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
            string? package = System.Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH");
            Assert.True(Directory.Exists(directory), "An explicit installed ACDREAM_DAT_DIR is required.");
            Assert.True(File.Exists(package), "An explicit validated ACDREAM_PAK_PATH is required.");
            _dat = new BoundedTestDatCollection(directory!);
            var bounded = (IDatReaderWriter)_dat;
            _prepared = new PakPreparedAssetSource(package!, bounded);
            PreparedCollisionReadResult<FlatSetupCollision> mover = _prepared.ReadSetupCollision(HumanSetup);
            Assert.Equal(PreparedAssetReadStatus.Loaded, mover.Status);
            Mover = Assert.IsType<FlatSetupCollision>(mover.Data);
            Assert.Equal(2, Mover.Spheres.Length);
            float[] heights = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u)).LandDefs.LandHeightTable;
            var factory = new LandblockBuildFactory(bounded, _prepared, new object(), heights);
            LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(new LandblockBuildRequest(
                0xF418FFFFu, LandblockStreamJobKind.LoadNear, Generation: 1,
                new LandblockBuildOrigin(0xF4, 0x18))));
            Assert.NotNull(build.EnvCells);
            LandblockCollisionBuild collisions = Assert.IsType<LandblockCollisionBuild>(build.Collisions);
            Assert.NotEmpty(collisions.CellStructures);
            Cache = PhysicsDataCache.CreateProduction();
            Engine = new PhysicsEngine { DataCache = Cache };
            TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(build.Landblock, heights);
            var surfaces = new List<CellSurface>();
            var portals = new List<PortalPlane>();
            LandblockPhysicsContentBuilder.PublishPreparedCells(Cache, build.Landblock, collisions,
                Vector3.Zero, surfaces, portals);
            LandblockPhysicsContentBuilder.CacheBuildings(Cache, build.Landblock, terrain, Vector3.Zero);
            LandblockPhysicsContentBuilder.CachePreparedObjects(Cache, collisions);
            Engine.AddLandblock(build.LandblockId, terrain, surfaces, portals, 0f, 0f);
            _ = LandblockPhysicsContentBuilder.PublishStaticCollision(Engine, Cache,
                build.Landblock, collisions, Vector3.Zero);
        }

        public void Dispose()
        {
            _prepared.Dispose();
            _dat.Dispose();
        }
    }
}
