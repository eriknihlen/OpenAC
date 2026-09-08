using System.Collections;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class FlatBspQueryDifferentialTests
{
    [Fact]
    public void CellContainment_NullOnPlaneRadiusEqualityAndDeepChain_MatchGraph()
    {
        var leaf = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 77,
        };
        CellBSPNode graph = leaf;
        const int Depth = 256;
        for (int i = 0; i < Depth; i++)
        {
            graph = new CellBSPNode
            {
                Type = BSPNodeType.BPIn,
                SplittingPlane = new Plane(
                    Vector3.UnitX,
                    i == 0 ? 0f : 1_000f),
                PosNode = graph,
            };
        }

        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(graph);
        Vector3[] points =
        [
            Vector3.Zero,
            new Vector3(0.5f, 0f, 0f),
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(
                BitConverter.Int32BitsToSingle(1),
                0f,
                0f),
        ];

        foreach (Vector3 point in points)
        {
            Assert.Equal(
                BSPQuery.PointInsideCellBsp(graph, point),
                FlatBspQuery.PointInsideCellBsp(flat, point));
        }

        foreach ((Vector3 center, float radius) in new[]
        {
            (Vector3.Zero, 0f),
            (new Vector3(-0.51f, 0f, 0f), 0.5f),
            (new Vector3(-0.51f, 0f, 0f), 0.49999997f),
            (new Vector3(-0.51f, 0f, 0f), 0.50000006f),
        })
        {
            Assert.Equal(
                BSPQuery.SphereIntersectsCellBsp(graph, center, radius),
                FlatBspQuery.SphereIntersectsCellBsp(flat, center, radius));
        }

        var empty = FlatCollisionAssetBuilder.FlattenCellContainmentBsp(null);
        Assert.True(FlatBspQuery.PointInsideCellBsp(empty, Vector3.Zero));
        Assert.True(FlatBspQuery.SphereIntersectsCellBsp(
            empty,
            Vector3.Zero,
            0.5f));
    }

    [Fact]
    public void StaticAndSweptOverlap_MultiPolygonLeafOrderAndBoundaryValues_MatchGraphBits()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildTwoWallLeaf();
        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);

        var random = new Random(0x4B53_5034);
        for (int i = 0; i < 10_000; i++)
        {
            Vector3 center = new(
                NextFloat(random, -3f, 3f),
                NextFloat(random, -1f, 1f),
                NextFloat(random, -3f, 3f));
            float radius = i % 7 switch
            {
                0 => 0f,
                1 => PhysicsGlobals.EPSILON,
                2 => 0.5f,
                _ => NextFloat(random, 0.01f, 1.5f),
            };
            Vector3 movement = new(
                NextFloat(random, -1f, 1f),
                NextFloat(random, -1f, 1f),
                NextFloat(random, -1f, 1f));

            bool graphStatic = BSPQuery.SphereIntersectsPoly(
                root,
                resolved,
                center,
                radius,
                out ushort graphStaticId,
                out Vector3 graphStaticNormal);
            bool flatStatic = FlatBspQuery.SphereIntersectsPoly(
                flat,
                center,
                radius,
                out ushort flatStaticId,
                out Vector3 flatStaticNormal);

            Assert.Equal(graphStatic, flatStatic);
            Assert.Equal(graphStaticId, flatStaticId);
            AssertVectorBits(graphStaticNormal, flatStaticNormal);

            bool graphSwept = BSPQuery.SphereIntersectsPolyWithTime(
                root,
                resolved,
                center,
                radius,
                movement,
                out ushort graphSweptId,
                out Vector3 graphSweptNormal,
                out float graphTime);
            bool flatSwept = FlatBspQuery.SphereIntersectsPolyWithTime(
                flat,
                center,
                radius,
                movement,
                out ushort flatSweptId,
                out Vector3 flatSweptNormal,
                out float flatTime);

            Assert.Equal(graphSwept, flatSwept);
            Assert.Equal(graphSweptId, flatSweptId);
            AssertVectorBits(graphSweptNormal, flatSweptNormal);
            AssertFloatBits(graphTime, flatTime);
        }
    }

    [Fact]
    public void FindWalkable_RandomizedAndEqualCandidateDistances_MatchesGraphBits()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildTwoFloorLeaf();
        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);
        var random = new Random(0x5741_4C4B);

        for (int i = 0; i < 5_000; i++)
        {
            Vector3 center = new(
                NextFloat(random, -2.5f, 2.5f),
                NextFloat(random, -2.5f, 2.5f),
                NextFloat(random, -0.25f, 1.25f));
            float radius = i % 5 == 0
                ? 0.48f
                : NextFloat(random, 0.05f, 0.8f);
            float probe = i % 7 == 0
                ? 0.5f
                : NextFloat(random, 0.01f, 1.5f);

            Transition graphTransition = NewTransition();
            Transition flatTransition = NewTransition();
            bool graphFound = BSPQuery.FindWalkableSphere(
                root,
                resolved,
                graphTransition,
                center,
                radius,
                probe,
                Vector3.UnitZ,
                out ResolvedPolygon? graphPolygon,
                out ushort graphId,
                out Vector3 graphCenter);
            bool flatFound = FlatBspQuery.FindWalkableSphere(
                flat,
                flatTransition,
                center,
                radius,
                probe,
                Vector3.UnitZ,
                out int flatPolygonIndex,
                out ushort flatId,
                out Vector3 flatCenter);

            Assert.Equal(graphFound, flatFound);
            Assert.Equal(graphId, flatId);
            Assert.Equal(graphPolygon is null, flatPolygonIndex < 0);
            AssertVectorBits(graphCenter, flatCenter);
            AssertTransitionEquivalent(graphTransition, flatTransition);
        }
    }

    [Fact]
    public void PhysicsTraversal_NullChildRadiusEqualityEqualCandidatesAndDeepTree_Match()
    {
        ResolvedPolygon first = Polygon(
            9,
            new Plane(Vector3.UnitZ, 0f),
            new Vector3(-2f, -2f, 0f),
            new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f),
            new Vector3(-2f, 2f, 0f));
        ResolvedPolygon second = Polygon(
            4,
            first.Plane,
            first.Vertices.ToArray());
        PhysicsBSPNode graph = Leaf();
        graph.Polygons.Add(first.Id);
        graph.Polygons.Add(second.Id);
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [second.Id] = second,
            [first.Id] = first,
        };

        const int Depth = 128;
        for (int i = 0; i < Depth; i++)
        {
            graph = new PhysicsBSPNode
            {
                Type = BSPNodeType.BPIn,
                SplittingPlane = new Plane(Vector3.UnitX, 1_000f),
                PosNode = graph,
                BoundingSphere = new Sphere
                {
                    Origin = Vector3.Zero,
                    Radius = 10_000f,
                },
            };
        }

        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(graph, resolved);
        Vector3 equalityCenter = new(0f, 0f, 0.48f);
        bool graphStatic = BSPQuery.SphereIntersectsPoly(
            graph,
            resolved,
            equalityCenter,
            0.48f,
            out ushort graphStaticId,
            out Vector3 graphStaticNormal);
        bool flatStatic = FlatBspQuery.SphereIntersectsPoly(
            flat,
            equalityCenter,
            0.48f,
            out ushort flatStaticId,
            out Vector3 flatStaticNormal);
        Assert.Equal(graphStatic, flatStatic);
        Assert.Equal(graphStaticId, flatStaticId);
        AssertVectorBits(graphStaticNormal, flatStaticNormal);

        Transition graphTransition = NewTransition();
        Transition flatTransition = NewTransition();
        bool graphFound = BSPQuery.FindWalkableSphere(
            graph,
            resolved,
            graphTransition,
            new Vector3(0f, 0f, 0.4f),
            0.48f,
            0.5f,
            Vector3.UnitZ,
            out ResolvedPolygon? graphPolygon,
            out ushort graphPolygonId,
            out Vector3 graphAdjustedCenter);
        bool flatFound = FlatBspQuery.FindWalkableSphere(
            flat,
            flatTransition,
            new Vector3(0f, 0f, 0.4f),
            0.48f,
            0.5f,
            Vector3.UnitZ,
            out int flatPolygonIndex,
            out ushort flatPolygonId,
            out Vector3 flatAdjustedCenter);
        Assert.Equal(graphFound, flatFound);
        Assert.Equal(graphPolygonId, flatPolygonId);
        Assert.Equal(graphPolygon is null, flatPolygonIndex < 0);
        AssertVectorBits(graphAdjustedCenter, flatAdjustedCenter);
        AssertTransitionEquivalent(graphTransition, flatTransition);
    }

    [Fact]
    public void FindCollisions_Path1Placement_MatchesGraphStateAndMutations()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildFloorLeaf();
        Sphere sphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        AssertFindCollisionsEquivalent(
            root,
            resolved,
            sphere,
            null,
            sphere.Origin,
            transition => transition.SpherePath.InsertType = InsertType.Placement);
    }

    [Fact]
    public void FindCollisions_Path2CheckWalkable_MatchesGraphStateAndMutations()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildFloorLeaf();
        Sphere sphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        AssertFindCollisionsEquivalent(
            root,
            resolved,
            sphere,
            null,
            sphere.Origin,
            transition => transition.SpherePath.CheckWalkable = true);
    }

    [Fact]
    public void FindCollisions_Path3StepDown_MatchesEveryTransitionOutputBit()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildFloorLeaf();
        Sphere sphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        AssertFindCollisionsEquivalent(
            root,
            resolved,
            sphere,
            null,
            sphere.Origin,
            transition =>
            {
                transition.SpherePath.StepDown = true;
                transition.SpherePath.StepDownAmt = 0.5f;
            },
            worldOrigin: new Vector3(17f, -23f, 94f));
    }

    [Fact]
    public void FindCollisions_Path4Landing_MatchesEveryTransitionOutputBit()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildFloorLeaf();
        Sphere sphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        AssertFindCollisionsEquivalent(
            root,
            resolved,
            sphere,
            null,
            sphere.Origin + new Vector3(0f, 0f, 0.05f),
            transition => transition.SpherePath.Collide = true,
            worldOrigin: new Vector3(17f, -23f, 94f));
    }

    [Fact]
    public void FindCollisions_Path5FullAndNearMiss_MatchEveryTransitionOutputBit()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildTwoWallLeaf();
        Sphere foot = SphereAt(new Vector3(0f, 0.3f, 0f));
        Sphere head = SphereAt(new Vector3(0f, 0.3f, 1f));

        AssertFindCollisionsEquivalent(
            root,
            resolved,
            foot,
            head,
            foot.Origin - new Vector3(0.05f, 0f, 0f),
            transition =>
                transition.ObjectInfo.State = ObjectInfoState.Contact);

        AssertFindCollisionsEquivalent(
            root,
            resolved,
            foot,
            null,
            foot.Origin - new Vector3(0f, -0.05f, 0f),
            transition =>
                transition.ObjectInfo.State = ObjectInfoState.Contact);
    }

    [Fact]
    public void FindCollisions_Path6LandingSteepAndPerfectClip_MatchEveryOutputBit()
    {
        (PhysicsBSPNode floorRoot, Dictionary<ushort, ResolvedPolygon> floorResolved) =
            BuildFloorLeaf();
        Sphere floorSphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        AssertFindCollisionsEquivalent(
            floorRoot,
            floorResolved,
            floorSphere,
            null,
            floorSphere.Origin + new Vector3(0f, 0f, 0.05f),
            configure: null);

        (PhysicsBSPNode wallRoot, Dictionary<ushort, ResolvedPolygon> wallResolved) =
            BuildTwoWallLeaf();
        Sphere wallSphere = SphereAt(new Vector3(0f, 0.3f, 0f));
        AssertFindCollisionsEquivalent(
            wallRoot,
            wallResolved,
            wallSphere,
            null,
            wallSphere.Origin - new Vector3(0f, -0.05f, 0f),
            transition =>
            {
                transition.ObjectInfo.State =
                    ObjectInfoState.PathClipped | ObjectInfoState.PerfectClip;
            });
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_LargeRandomizedSweep_HasZeroBitMismatch()
    {
        string? datDirectory = ConformanceDats.ResolveDatDir();
        if (datDirectory is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        var random = new Random(0x4934_4253);
        foreach (uint cellId in new[]
        {
            0x8A02_016Eu,
            0x8A02_017Au,
            0xA9B4_013Fu,
            0xA9B4_0150u,
            0xA9B4_0159u,
            0xA9B4_015Au,
            0xA9B4_0161u,
            0xA9B4_0162u,
            0xA9B4_0164u,
            0xA9B4_0166u,
        })
        {
            var cache = new PhysicsDataCache();
            ConformanceDats.LoadEnvCell(dats, cache, cellId);
            CellPhysics source = Assert.IsType<CellPhysics>(
                cache.GetCellStruct(cellId));
            FlatPhysicsBsp flat =
                FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                    source.BSP?.Root,
                    source.Resolved);
            FlatCellContainmentBsp flatContainment =
                FlatCollisionAssetBuilder.FlattenCellContainmentBsp(
                    source.CellBSP?.Root);
            if (flat.PolygonTable.Polygons.Length == 0)
                continue;

            for (int iteration = 0; iteration < 2_500; iteration++)
            {
                int polygonIndex =
                    random.Next(flat.PolygonTable.Polygons.Length);
                FlatCollisionPolygon polygon =
                    flat.PolygonTable.Polygons[polygonIndex];
                Vector3 anchor = flat.PolygonTable.Vertices[
                    polygon.VertexRange.Start +
                    random.Next(polygon.VertexRange.Count)];
                float radius = (iteration % 11) switch
                {
                    0 => PhysicsGlobals.EPSILON,
                    1 => 0.01f,
                    2 => 0.48f,
                    3 => 1f,
                    _ => NextFloat(random, 0.05f, 1.25f),
                };
                float signedOffset = (iteration % 13) switch
                {
                    0 => radius,
                    1 => radius - PhysicsGlobals.EPSILON,
                    2 => radius + PhysicsGlobals.EPSILON,
                    3 => 0f,
                    _ => NextFloat(random, -radius * 1.5f, radius * 1.5f),
                };
                Vector3 center =
                    anchor + polygon.Plane.Normal * signedOffset;
                Vector3 movement = (iteration % 7) switch
                {
                    0 => Vector3.Zero,
                    1 => -polygon.Plane.Normal * 0.05f,
                    2 => polygon.Plane.Normal * 0.05f,
                    _ => new Vector3(
                        NextFloat(random, -0.5f, 0.5f),
                        NextFloat(random, -0.5f, 0.5f),
                        NextFloat(random, -0.5f, 0.5f)),
                };

                AssertStaticAndSweptEquivalent(
                    source.BSP?.Root,
                    source.Resolved,
                    flat,
                    center,
                    radius,
                    movement,
                    cellId,
                    iteration);

                bool graphInside = BSPQuery.PointInsideCellBsp(
                    source.CellBSP?.Root,
                    center);
                bool flatInside =
                    FlatBspQuery.PointInsideCellBsp(flatContainment, center);
                Assert.True(
                    graphInside == flatInside,
                    $"cell 0x{cellId:X8}, iteration {iteration}: point containment.");

                bool graphSphereInside = BSPQuery.SphereIntersectsCellBsp(
                    source.CellBSP?.Root,
                    center,
                    radius);
                bool flatSphereInside =
                    FlatBspQuery.SphereIntersectsCellBsp(
                        flatContainment,
                        center,
                        radius);
                Assert.True(
                    graphSphereInside == flatSphereInside,
                    $"cell 0x{cellId:X8}, iteration {iteration}: sphere containment.");

                Transition graphWalkable = NewTransition();
                Transition flatWalkable = NewTransition();
                float probeDistance = iteration % 5 == 0
                    ? 0.5f
                    : NextFloat(random, 0.01f, 1.5f);
                bool graphFound = BSPQuery.FindWalkableSphere(
                    source.BSP?.Root,
                    source.Resolved,
                    graphWalkable,
                    center,
                    radius,
                    probeDistance,
                    Vector3.UnitZ,
                    out ResolvedPolygon? graphPolygon,
                    out ushort graphPolygonId,
                    out Vector3 graphAdjustedCenter);
                bool flatFound = FlatBspQuery.FindWalkableSphere(
                    flat,
                    flatWalkable,
                    center,
                    radius,
                    probeDistance,
                    Vector3.UnitZ,
                    out int flatPolygonIndex,
                    out ushort flatPolygonId,
                    out Vector3 flatAdjustedCenter);
                Assert.True(
                    graphFound == flatFound,
                    $"cell 0x{cellId:X8}, iteration {iteration}: walkable result.");
                Assert.Equal(graphPolygonId, flatPolygonId);
                Assert.Equal(graphPolygon is null, flatPolygonIndex < 0);
                AssertVectorBits(graphAdjustedCenter, flatAdjustedCenter);
                AssertTransitionEquivalent(graphWalkable, flatWalkable);

                Sphere sphere0 = new()
                {
                    Origin = center,
                    Radius = radius,
                };
                Sphere? sphere1 = iteration % 2 == 0
                    ? new Sphere
                    {
                        Origin = center + Vector3.UnitZ * (radius * 1.75f),
                        Radius = radius,
                    }
                    : null;
                Vector3 currentCenter = center - movement;
                Transition graphTransition =
                    SeedTransition(sphere0, sphere1, currentCenter);
                Transition flatTransition =
                    SeedTransition(sphere0, sphere1, currentCenter);
                ConfigureRandomPath(graphTransition, iteration);
                ConfigureRandomPath(flatTransition, iteration);
                float angle =
                    NextFloat(random, -MathF.PI, MathF.PI);
                Quaternion localToWorld =
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
                Vector3 localSpaceZ = Vector3.Transform(
                    Vector3.UnitZ,
                    Quaternion.Conjugate(localToWorld));
                float scale = (iteration % 3) switch
                {
                    0 => 0.5f,
                    1 => 1f,
                    _ => 2f,
                };
                Vector3 worldOrigin = new(
                    NextFloat(random, -200f, 200f),
                    NextFloat(random, -200f, 200f),
                    NextFloat(random, -20f, 200f));

                TransitionState graphState = BSPQuery.FindCollisions(
                    source.BSP?.Root,
                    source.Resolved,
                    graphTransition,
                    sphere0,
                    sphere1,
                    currentCenter,
                    localSpaceZ,
                    scale,
                    localToWorld,
                    engine: null,
                    worldOrigin);
                TransitionState flatState = FlatBspQuery.FindCollisions(
                    flat,
                    flatTransition,
                    sphere0,
                    sphere1,
                    currentCenter,
                    localSpaceZ,
                    scale,
                    localToWorld,
                    engine: null,
                    worldOrigin);
                Assert.True(
                    graphState == flatState,
                    $"cell 0x{cellId:X8}, iteration {iteration}: " +
                    $"collision state graph={graphState}, flat={flatState}.");
                AssertTransitionEquivalent(graphTransition, flatTransition);
            }
        }
    }

    [Fact]
    public void CompleteResolver_FinalResultBodyAndCellMembership_MatchGraphBits()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildTwoWallLeaf();
        var containmentRoot = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 3,
        };
        FlatPhysicsBsp flatPhysics =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);
        FlatCellContainmentBsp flatContainment =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(
                containmentRoot);
        const uint CellId = 0xA9B4_0157u;

        CellPhysics CreateCell() => new()
        {
            BSP = new PhysicsBSPTree { Root = root },
            Resolved = resolved,
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            CellBSP = new CellBSPTree { Root = containmentRoot },
            FlatPhysicsBsp = flatPhysics,
            FlatContainmentBsp = flatContainment,
        };

        PhysicsEngine graphEngine = CreateEngine(
            CellId,
            CreateCell(),
            CollisionTraversalMode.Graph);
        PhysicsEngine flatEngine = CreateEngine(
            CellId,
            CreateCell(),
            CollisionTraversalMode.Flat);
        var random = new Random(0x5245_534F);

        for (int sequence = 0; sequence < 100; sequence++)
        {
            PhysicsBody graphBody = CreateGroundedBody(CellId);
            PhysicsBody flatBody = CreateGroundedBody(CellId);
            Vector3 position = new(
                NextFloat(random, -1.5f, 1.5f),
                NextFloat(random, 0.15f, 0.8f),
                0f);
            graphBody.SnapToCell(CellId, position, position);
            flatBody.SnapToCell(CellId, position, position);

            for (int frame = 0; frame < 12; frame++)
            {
                Vector3 movement = (frame % 4) switch
                {
                    0 => new Vector3(0.08f, -0.12f, 0f),
                    1 => new Vector3(-0.05f, -0.08f, 0f),
                    2 => new Vector3(0.03f, 0.1f, 0f),
                    _ => new Vector3(
                        NextFloat(random, -0.1f, 0.1f),
                        NextFloat(random, -0.15f, 0.15f),
                        0f),
                };
                Vector3 target = position + movement;

                ResolveResult graphResult = graphEngine.ResolveWithTransition(
                    position,
                    target,
                    CellId,
                    sphereRadius: 0.48f,
                    sphereHeight: 1.835f,
                    stepUpHeight: 0.1f,
                    stepDownHeight: 0.04f,
                    isOnGround: true,
                    graphBody,
                    moverFlags: ObjectInfoState.IsPlayer);
                ResolveResult flatResult = flatEngine.ResolveWithTransition(
                    position,
                    target,
                    CellId,
                    sphereRadius: 0.48f,
                    sphereHeight: 1.835f,
                    stepUpHeight: 0.1f,
                    stepDownHeight: 0.04f,
                    isOnGround: true,
                    flatBody,
                    moverFlags: ObjectInfoState.IsPlayer);

                AssertValueBits(
                    $"sequence[{sequence}].frame[{frame}].result",
                    graphResult,
                    flatResult);
                AssertValueBits(
                    $"sequence[{sequence}].frame[{frame}].body",
                    graphBody,
                    flatBody);
                Assert.Equal(graphResult.CellId, flatResult.CellId);
                Assert.Equal(CellId, flatResult.CellId);
                position = graphResult.Position;
            }
        }
    }

    [Fact]
    public void WarmedFlatTraversal_AllocatesZeroBytes()
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildFloorLeaf();
        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);
        Sphere sphere = SphereAt(new Vector3(0f, 0f, 0.4f));
        var transition = new Transition();

        void Execute()
        {
            transition.ResetForReuse();
            transition.SpherePath.WalkableAllowance = PhysicsGlobals.FloorZ;
            transition.SpherePath.WalkInterp = 1f;
            transition.SpherePath.StepDown = true;
            transition.SpherePath.StepDownAmt = 0.5f;
            transition.SpherePath.CheckCellId = 0xA9B4_0157u;
            _ = FlatBspQuery.FindCollisions(
                flat,
                transition,
                sphere,
                null,
                sphere.Origin,
                Vector3.UnitZ,
                1f,
                Quaternion.Identity,
                engine: null,
                worldOrigin: Vector3.Zero);
            _ = FlatBspQuery.SphereIntersectsPoly(
                flat,
                sphere.Origin,
                sphere.Radius,
                out _,
                out _);
            _ = FlatBspQuery.SphereIntersectsPolyWithTime(
                flat,
                sphere.Origin,
                sphere.Radius,
                -Vector3.UnitZ * 0.1f,
                out _,
                out _,
                out _);
        }

        for (int i = 0; i < 100; i++)
            Execute();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            Execute();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static PhysicsEngine CreateEngine(
        uint cellId,
        CellPhysics cell,
        CollisionTraversalMode mode)
    {
        var cache = new PhysicsDataCache
        {
            CollisionTraversalMode = mode,
        };
        cache.RegisterCellStructForTest(cellId, cell);
        var engine = new PhysicsEngine
        {
            DataCache = cache,
        };
        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i;
        engine.AddLandblock(
            0xA9B4_FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static PhysicsBody CreateGroundedBody(uint cellId)
    {
        var body = new PhysicsBody
        {
            ContactPlaneValid = true,
            ContactPlane = new Plane(Vector3.UnitZ, 0f),
            ContactPlaneCellId = cellId,
            TransientState =
                TransientStateFlags.Contact |
                TransientStateFlags.OnWalkable,
            State = PhysicsStateFlags.Gravity,
        };
        return body;
    }

    private static void AssertStaticAndSweptEquivalent(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        FlatPhysicsBsp flat,
        Vector3 center,
        float radius,
        Vector3 movement,
        uint cellId,
        int iteration)
    {
        bool graphStatic = BSPQuery.SphereIntersectsPoly(
            root,
            resolved,
            center,
            radius,
            out ushort graphStaticId,
            out Vector3 graphStaticNormal);
        bool flatStatic = FlatBspQuery.SphereIntersectsPoly(
            flat,
            center,
            radius,
            out ushort flatStaticId,
            out Vector3 flatStaticNormal);
        Assert.True(
            graphStatic == flatStatic,
            $"cell 0x{cellId:X8}, iteration {iteration}: static result.");
        Assert.Equal(graphStaticId, flatStaticId);
        AssertVectorBits(graphStaticNormal, flatStaticNormal);

        bool graphSwept = BSPQuery.SphereIntersectsPolyWithTime(
            root,
            resolved,
            center,
            radius,
            movement,
            out ushort graphSweptId,
            out Vector3 graphSweptNormal,
            out float graphTime);
        bool flatSwept = FlatBspQuery.SphereIntersectsPolyWithTime(
            flat,
            center,
            radius,
            movement,
            out ushort flatSweptId,
            out Vector3 flatSweptNormal,
            out float flatTime);
        Assert.True(
            graphSwept == flatSwept,
            $"cell 0x{cellId:X8}, iteration {iteration}: swept result.");
        Assert.Equal(graphSweptId, flatSweptId);
        AssertVectorBits(graphSweptNormal, flatSweptNormal);
        AssertFloatBits(graphTime, flatTime);
    }

    private static void ConfigureRandomPath(
        Transition transition,
        int iteration)
    {
        switch (iteration % 6)
        {
            case 0:
                transition.SpherePath.InsertType = InsertType.Placement;
                break;
            case 1:
                transition.SpherePath.CheckWalkable = true;
                break;
            case 2:
                transition.SpherePath.StepDown = true;
                transition.SpherePath.StepDownAmt = 0.5f;
                break;
            case 3:
                transition.SpherePath.Collide = true;
                break;
            case 4:
                transition.ObjectInfo.State = ObjectInfoState.Contact;
                break;
            default:
                if ((iteration & 12) == 12)
                {
                    transition.ObjectInfo.State =
                        ObjectInfoState.PathClipped |
                        ObjectInfoState.PerfectClip;
                }
                break;
        }
    }

    private static void AssertFindCollisionsEquivalent(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Sphere sphere0,
        Sphere? sphere1,
        Vector3 localCurrentCenter,
        Action<Transition>? configure,
        Vector3 worldOrigin = default)
    {
        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);
        Transition graphTransition =
            SeedTransition(sphere0, sphere1, localCurrentCenter);
        Transition flatTransition =
            SeedTransition(sphere0, sphere1, localCurrentCenter);
        configure?.Invoke(graphTransition);
        configure?.Invoke(flatTransition);

        TransitionState graphState = BSPQuery.FindCollisions(
            root,
            resolved,
            graphTransition,
            sphere0,
            sphere1,
            localCurrentCenter,
            Vector3.UnitZ,
            1f,
            Quaternion.Identity,
            engine: null,
            worldOrigin);
        TransitionState flatState = FlatBspQuery.FindCollisions(
            flat,
            flatTransition,
            sphere0,
            sphere1,
            localCurrentCenter,
            Vector3.UnitZ,
            1f,
            Quaternion.Identity,
            engine: null,
            worldOrigin);

        Assert.Equal(graphState, flatState);
        AssertTransitionEquivalent(graphTransition, flatTransition);
    }

    private static Transition SeedTransition(
        Sphere sphere0,
        Sphere? sphere1,
        Vector3 localCurrentCenter)
    {
        Transition transition = NewTransition();
        SpherePath path = transition.SpherePath;
        path.NumSphere = sphere1 is null ? 1 : 2;
        path.LocalSphere[0].Origin = sphere0.Origin;
        path.LocalSphere[0].Radius = sphere0.Radius;
        path.GlobalSphere[0].Origin = sphere0.Origin;
        path.GlobalSphere[0].Radius = sphere0.Radius;
        path.GlobalCurrCenter[0].Origin = localCurrentCenter;
        path.GlobalCurrCenter[0].Radius = sphere0.Radius;
        if (sphere1 is not null)
        {
            path.LocalSphere[1].Origin = sphere1.Origin;
            path.LocalSphere[1].Radius = sphere1.Radius;
            path.GlobalSphere[1].Origin = sphere1.Origin;
            path.GlobalSphere[1].Radius = sphere1.Radius;
            path.GlobalCurrCenter[1].Origin =
                sphere1.Origin - (sphere0.Origin - localCurrentCenter);
            path.GlobalCurrCenter[1].Radius = sphere1.Radius;
        }

        path.CheckPos = sphere0.Origin;
        path.CheckCellId = 0xA9B4_013Fu;
        path.BackupCheckPos = new Vector3(3f, 5f, 7f);
        path.BackupCheckCellId = 0x8A02_016Eu;
        return transition;
    }

    private static Transition NewTransition()
    {
        var transition = new Transition();
        transition.SpherePath.WalkableAllowance = PhysicsGlobals.FloorZ;
        transition.SpherePath.WalkInterp = 1f;
        return transition;
    }

    private static (PhysicsBSPNode, Dictionary<ushort, ResolvedPolygon>)
        BuildFloorLeaf()
    {
        ResolvedPolygon floor = Polygon(
            7,
            new Plane(Vector3.UnitZ, 0f),
            new Vector3(-2f, -2f, 0f),
            new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f),
            new Vector3(-2f, 2f, 0f));
        PhysicsBSPNode root = Leaf();
        root.Polygons.Add(floor.Id);
        return (root, new Dictionary<ushort, ResolvedPolygon>
        {
            [floor.Id] = floor,
        });
    }

    private static (PhysicsBSPNode, Dictionary<ushort, ResolvedPolygon>)
        BuildTwoFloorLeaf()
    {
        ResolvedPolygon lower = Polygon(
            9,
            new Plane(Vector3.UnitZ, 0f),
            new Vector3(-2f, -2f, 0f),
            new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f),
            new Vector3(-2f, 2f, 0f));
        ResolvedPolygon upper = Polygon(
            4,
            new Plane(Vector3.UnitZ, -0.75f),
            new Vector3(-2f, -2f, 0.75f),
            new Vector3(2f, -2f, 0.75f),
            new Vector3(2f, 2f, 0.75f),
            new Vector3(-2f, 2f, 0.75f));
        PhysicsBSPNode root = Leaf();
        root.Polygons.Add(lower.Id);
        root.Polygons.Add(upper.Id);
        return (root, new Dictionary<ushort, ResolvedPolygon>
        {
            [upper.Id] = upper,
            [lower.Id] = lower,
        });
    }

    private static (PhysicsBSPNode, Dictionary<ushort, ResolvedPolygon>)
        BuildTwoWallLeaf()
    {
        ResolvedPolygon first = Polygon(
            9,
            new Plane(Vector3.UnitY, 0f),
            new Vector3(-2f, 0f, -2f),
            new Vector3(-2f, 0f, 2f),
            new Vector3(2f, 0f, 2f),
            new Vector3(2f, 0f, -2f));
        ResolvedPolygon second = Polygon(
            4,
            new Plane(Vector3.UnitY, -0.1f),
            new Vector3(-2f, 0.1f, -2f),
            new Vector3(-2f, 0.1f, 2f),
            new Vector3(2f, 0.1f, 2f),
            new Vector3(2f, 0.1f, -2f));
        PhysicsBSPNode root = Leaf();
        root.Polygons.Add(first.Id);
        root.Polygons.Add(second.Id);
        return (root, new Dictionary<ushort, ResolvedPolygon>
        {
            [second.Id] = second,
            [first.Id] = first,
        });
    }

    private static PhysicsBSPNode Leaf() => new()
    {
        Type = BSPNodeType.Leaf,
        BoundingSphere = new Sphere
        {
            Origin = Vector3.Zero,
            Radius = 10_000f,
        },
    };

    private static ResolvedPolygon Polygon(
        ushort id,
        Plane plane,
        params Vector3[] vertices) => new()
        {
            Id = id,
            Plane = plane,
            SidesType = CullMode.None,
            NumPoints = vertices.Length,
            Vertices = vertices,
        };

    private static Sphere SphereAt(Vector3 center) => new()
    {
        Origin = center,
        Radius = 0.48f,
    };

    private static float NextFloat(Random random, float minimum, float maximum)
        => minimum + (float)random.NextDouble() * (maximum - minimum);

    private static void AssertTransitionEquivalent(
        Transition expected,
        Transition actual)
    {
        AssertValueBits("ObjectInfo", expected.ObjectInfo, actual.ObjectInfo);
        AssertValueBits("SpherePath", expected.SpherePath, actual.SpherePath);
        AssertValueBits("CollisionInfo", expected.CollisionInfo, actual.CollisionInfo);
        Assert.Equal(
            expected.CollisionInfo.ContactPlaneWriteCount,
            actual.CollisionInfo.ContactPlaneWriteCount);
    }

    private static void AssertValueBits(
        string path,
        object? expected,
        object? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.True(
                expected is null && actual is null,
                $"{path}: one value is null.");
            return;
        }

        Type expectedType = expected.GetType();
        Assert.Equal(expectedType, actual.GetType());

        if (expected is float expectedFloat &&
            actual is float actualFloat)
        {
            AssertFloatBits(expectedFloat, actualFloat, path);
            return;
        }

        if (expected is Vector3 expectedVector &&
            actual is Vector3 actualVector)
        {
            AssertVectorBits(expectedVector, actualVector, path);
            return;
        }

        if (expected is Quaternion expectedQuaternion &&
            actual is Quaternion actualQuaternion)
        {
            AssertFloatBits(
                expectedQuaternion.X,
                actualQuaternion.X,
                $"{path}.X");
            AssertFloatBits(
                expectedQuaternion.Y,
                actualQuaternion.Y,
                $"{path}.Y");
            AssertFloatBits(
                expectedQuaternion.Z,
                actualQuaternion.Z,
                $"{path}.Z");
            AssertFloatBits(
                expectedQuaternion.W,
                actualQuaternion.W,
                $"{path}.W");
            return;
        }

        if (expected is Plane expectedPlane &&
            actual is Plane actualPlane)
        {
            AssertVectorBits(
                expectedPlane.Normal,
                actualPlane.Normal,
                $"{path}.Normal");
            AssertFloatBits(expectedPlane.D, actualPlane.D, $"{path}.D");
            return;
        }

        if (expectedType.IsEnum ||
            expectedType.IsPrimitive ||
            expected is string ||
            expected is decimal)
        {
            Assert.True(
                expected.Equals(actual),
                $"{path}: expected {expected}, actual {actual}.");
            return;
        }

        if (expected is IEnumerable expectedEnumerable &&
            actual is IEnumerable actualEnumerable)
        {
            object?[] expectedItems = expectedEnumerable.Cast<object?>().ToArray();
            object?[] actualItems = actualEnumerable.Cast<object?>().ToArray();
            Assert.Equal(expectedItems.Length, actualItems.Length);
            for (int i = 0; i < expectedItems.Length; i++)
            {
                AssertValueBits(
                    $"{path}[{i}]",
                    expectedItems[i],
                    actualItems[i]);
            }

            return;
        }

        FieldInfo[] fields = expectedType
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        PropertyInfo[] properties = expectedType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.CanRead &&
                property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            fields.Length != 0 || properties.Length != 0,
            $"{path}: unsupported comparison type {expectedType.FullName}.");

        foreach (FieldInfo field in fields)
        {
            AssertValueBits(
                $"{path}.{field.Name}",
                field.GetValue(expected),
                field.GetValue(actual));
        }

        foreach (PropertyInfo property in properties)
        {
            AssertValueBits(
                $"{path}.{property.Name}",
                property.GetValue(expected),
                property.GetValue(actual));
        }
    }

    private static void AssertVectorBits(
        Vector3 expected,
        Vector3 actual,
        string path = "vector")
    {
        AssertFloatBits(expected.X, actual.X, $"{path}.X");
        AssertFloatBits(expected.Y, actual.Y, $"{path}.Y");
        AssertFloatBits(expected.Z, actual.Z, $"{path}.Z");
    }

    private static void AssertFloatBits(
        float expected,
        float actual,
        string path = "float")
    {
        Assert.True(
            BitConverter.SingleToInt32Bits(expected) ==
            BitConverter.SingleToInt32Bits(actual),
            $"{path}: expected 0x{BitConverter.SingleToInt32Bits(expected):X8}, " +
            $"actual 0x{BitConverter.SingleToInt32Bits(actual):X8}.");
    }
}
