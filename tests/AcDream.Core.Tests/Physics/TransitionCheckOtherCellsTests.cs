using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class TransitionCheckOtherCellsTests
{
    private static Transition MakeTransition(bool contactFlag = false)
    {
        var t = new Transition();
        t.SpherePath.InitPath(Vector3.Zero, Vector3.Zero, cellId: 0xA9B40100u, sphereRadius: 0.48f);
        t.ObjectInfo.State = contactFlag ? ObjectInfoState.Contact : ObjectInfoState.None;
        t.CollisionInfo.ContactPlaneValid   = true;
        t.CollisionInfo.ContactPlaneIsWater = true;
        return t;
    }

    [Fact]
    public void OK_ContinuesIteration_DoesNotMutate()
    {
        var t = MakeTransition();

        bool halt = t.ApplyOtherCellResult(TransitionState.OK, out var finalState);

        Assert.False(halt);
        Assert.Equal(TransitionState.OK, finalState);
        Assert.True(t.CollisionInfo.ContactPlaneValid);
        Assert.True(t.CollisionInfo.ContactPlaneIsWater);
        Assert.False(t.CollisionInfo.CollidedWithEnvironment);
    }

    [Fact]
    public void Collided_HaltsAndSetsCollidedWithEnvironment_WhenNotInContact()
    {
        var t = MakeTransition(contactFlag: false);

        bool halt = t.ApplyOtherCellResult(TransitionState.Collided, out var finalState);

        Assert.True(halt);
        Assert.Equal(TransitionState.Collided, finalState);
        Assert.True(t.CollisionInfo.CollidedWithEnvironment);
    }

    [Fact]
    public void Collided_DoesNotSetCollidedWithEnvironment_WhenInContact()
    {
        var t = MakeTransition(contactFlag: true);

        bool halt = t.ApplyOtherCellResult(TransitionState.Collided, out var finalState);

        Assert.True(halt);
        Assert.Equal(TransitionState.Collided, finalState);
        Assert.False(t.CollisionInfo.CollidedWithEnvironment);
    }

    [Fact]
    public void Adjusted_HaltsAndSetsCollidedWithEnvironment_WhenNotInContact()
    {
        var t = MakeTransition(contactFlag: false);

        bool halt = t.ApplyOtherCellResult(TransitionState.Adjusted, out var finalState);

        Assert.True(halt);
        Assert.Equal(TransitionState.Adjusted, finalState);
        Assert.True(t.CollisionInfo.CollidedWithEnvironment);
    }

    [Fact]
    public void Slid_HaltsAndClearsContactPlaneFields()
    {
        var t = MakeTransition();
        Assert.True(t.CollisionInfo.ContactPlaneValid);
        Assert.True(t.CollisionInfo.ContactPlaneIsWater);

        bool halt = t.ApplyOtherCellResult(TransitionState.Slid, out var finalState);

        Assert.True(halt);
        Assert.Equal(TransitionState.Slid, finalState);
        Assert.False(t.CollisionInfo.ContactPlaneValid);
        Assert.False(t.CollisionInfo.ContactPlaneIsWater);
    }

    [Fact]
    public void CheckOtherCells_CellWithNullBspRoot_IsSkippedNoCrash()
    {
        var cell = new CellPhysics
        {
            BSP                   = null,  // <-- the guard target
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
        };

        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();   // PhysicsEngine has nullable DataCache
        var heights = new byte[81];
        Array.Fill(heights, (byte)0);
        var ht = new float[256];
        for (int i = 0; i < 256; i++) ht[i] = i * 1.0f;
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(heights, ht),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        engine.DataCache.RegisterCellStructForTest(0xA9B40157u, cell);

        var t = MakeTransition();
        var cellSet = new HashSet<uint> { 0xA9B40157u };

        var result = t.CheckOtherCells(engine, Vector3.Zero, 0.48f, cellSet);

        Assert.Equal(TransitionState.OK, result);
    }

    [Fact]
    public void CheckOtherCells_OutdoorLandcellCandidate_UsesTerrainWalkable()
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        var heights = new byte[81];
        Array.Fill(heights, (byte)0);
        var ht = new float[256];
        for (int i = 0; i < 256; i++) ht[i] = i * 1.0f;
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(heights, ht),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        var t = MakeTransition(contactFlag: true);
        t.SpherePath.InitPath(new Vector3(10f, 10f, -0.28f), new Vector3(10f, 10f, -0.28f),
            cellId: 0xA9B40147u, sphereRadius: 0.48f);

        var footCenter = new Vector3(10f, 10f, 0.20f);
        var cellSet = new HashSet<uint> { 0xA9B40001u };

        var result = t.CheckOtherCells(engine, footCenter, 0.48f, cellSet);

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.True(t.CollisionInfo.ContactPlaneValid);
        Assert.Equal(0xA9B40001u, t.CollisionInfo.ContactPlaneCellId);
        Assert.True(t.SpherePath.CheckPos.Z > -0.28f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutdoorTerrainMiss_RetainsPreparedBuildingAndObjectCollision(bool objectChannel)
    {
        PhysicsEngine engine = NeighborWall(objectChannel);
        Transition transition = NeighborTransition();
        Assert.NotNull(engine.DataCache!.CellGraph.GetVisible(NeighborCell));
        Assert.Null(engine.SampleTerrainWalkableInCell(NeighborCell, 23.8f, 12f));

        TransitionState result = transition.CheckOtherCells(engine,
            transition.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell });

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.True(transition.SpherePath.Collide);
        Assert.Equal(-Vector3.UnitX, transition.SpherePath.StepUpNormal);
        if (objectChannel)
            Assert.Equal(NeighborObject, transition.CollisionInfo.LastCollidedObjectGuid);
        else
            Assert.True(transition.CollisionInfo.CollidedWithEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutdoorTerrainMiss_NonintersectingPreparedNeighborRemainsClear(bool objectChannel)
    {
        PhysicsEngine engine = NeighborWall(objectChannel, wallOriginX: 30f);
        Transition transition = NeighborTransition();
        Vector3 target = transition.SpherePath.CheckPos;

        TransitionState result = transition.CheckOtherCells(engine,
            transition.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell });

        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(target, transition.SpherePath.CheckPos);
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
        Assert.Null(transition.CollisionInfo.LastCollidedObjectGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOutdoorCell_DoesNotDispatchRetainedBuildingOrObjects(bool objectChannel)
    {
        PhysicsEngine engine = NeighborWall(objectChannel, wallOriginX: 24f);
        Transition available = NeighborTransition(targetX: 24.3f);
        Assert.Equal(NeighborCell, engine.SampleTerrainWalkable(24.3f, 12f)!.Value.CellId);
        Assert.Equal(TransitionState.Adjusted, available.CheckOtherCells(engine,
            available.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell }));
        engine.DataCache!.CellGraph.RemoveLandblock(NeighborCell);
        Assert.Null(engine.DataCache.CellGraph.GetVisible(NeighborCell));
        Assert.NotNull(engine.DataCache.GetGfxObj(NeighborGfx));
        if (objectChannel)
            Assert.NotEmpty(engine.ShadowObjects.GetObjectsInCell(NeighborCell));
        else
            Assert.NotNull(engine.DataCache.GetBuilding(NeighborCell));
        Transition transition = NeighborTransition(targetX: 24.3f);
        Vector3 target = transition.SpherePath.CheckPos;

        TransitionState result = transition.CheckOtherCells(engine,
            transition.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell });

        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(target, transition.SpherePath.CheckPos);
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
        Assert.Null(transition.CollisionInfo.LastCollidedObjectGuid);
    }

    [Fact]
    public void OutdoorPrimaryCell_IsNotDispatchedAgain()
    {
        PhysicsEngine engine = NeighborWall(objectChannel: false);
        Transition transition = NeighborTransition();
        transition.SpherePath.SetCheckPos(transition.SpherePath.CheckPos, NeighborCell);

        Assert.Equal(TransitionState.OK, transition.CheckOtherCells(engine,
            transition.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell }));
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
    }

    [Fact]
    public void NeighborBuildingNonOk_StopsBeforeNeighborObjects()
    {
        PhysicsEngine engine = NeighborWall(objectChannel: false);
        engine.ShadowObjects.Register(NeighborObject, NeighborGfx, new Vector3(23.5f, 12f, 0f),
            Quaternion.Identity, 10f, 0f, 0f, 0xA9B40000u,
            collisionType: ShadowCollisionType.BSP, state: 0x00010000u,
            seedCellId: NeighborCell, isStatic: true);
        Assert.NotEmpty(engine.ShadowObjects.GetObjectsInCell(NeighborCell));
        Transition transition = NeighborTransition();

        Assert.Equal(TransitionState.Adjusted, transition.CheckOtherCells(engine,
            transition.SpherePath.GlobalSphere[0].Origin, 0.48f, new[] { NeighborCell }));
        Assert.True(transition.CollisionInfo.CollidedWithEnvironment);
        Assert.Null(transition.CollisionInfo.LastCollidedObjectGuid);
    }

    private const uint NeighborCell = 0xA9B40009u;
    private const uint NeighborGfx = 0x0100F482u;
    private const uint NeighborObject = 0xCA9B4482u;

    private static Transition NeighborTransition(float targetX = 23.8f)
    {
        Vector3 target = new(targetX, 12f, 1f);
        var transition = new Transition();
        transition.SpherePath.InitPath(new Vector3(targetX - 0.4f, 12f, 1f), target,
            0xA9B40001u, 0.48f);
        transition.SpherePath.SetCheckPos(target, 0xA9B40001u);
        return transition;
    }

    private static PhysicsEngine NeighborWall(bool objectChannel, float wallOriginX = 23.5f)
    {
        var (root, polygons) = BSPStepUpFixtures.TallWall();
        var normalized = new Dictionary<ushort, ResolvedPolygon>();
        foreach ((ushort id, ResolvedPolygon polygon) in polygons)
            normalized.Add(id, new ResolvedPolygon
            {
                Id = id, Vertices = polygon.Vertices, Plane = polygon.Plane,
                NumPoints = polygon.NumPoints, SidesType = polygon.SidesType,
            });
        var graph = new GfxObjPhysics
        {
            SourceId = NeighborGfx,
            BSP = new PhysicsBSPTree { Root = root },
            Resolved = normalized,
            BoundingSphere = root.BoundingSphere,
        };
        var cache = PhysicsDataCache.CreateProduction();
        cache.CacheGfxObj(NeighborGfx, FlatCollisionAssetBuilder.FlattenGfxObj(graph));
        Assert.Equal(CollisionTraversalMode.Flat, cache.CollisionTraversalMode);
        Assert.Null(cache.GetGfxObj(NeighborGfx)!.BSP);
        Assert.NotNull(cache.GetGfxObj(NeighborGfx)!.FlatPhysicsBsp);
        var engine = new PhysicsEngine { DataCache = cache };
        var heightTable = new float[256];
        Array.Fill(heightTable, -1000f);
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(new byte[81], heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        Vector3 origin = new(wallOriginX, 12f, 0f);
        if (objectChannel)
        {
            engine.ShadowObjects.Register(NeighborObject, NeighborGfx, origin,
                Quaternion.Identity, 10f, 0f, 0f, 0xA9B40000u,
                collisionType: ShadowCollisionType.BSP, state: 0x00010000u,
                seedCellId: NeighborCell, isStatic: true);
            Assert.Contains(engine.ShadowObjects.GetObjectsInCell(NeighborCell),
                entry => entry.EntityId == NeighborObject);
        }
        else
        {
            cache.CacheBuilding(NeighborCell, Array.Empty<BldPortalInfo>(),
                Matrix4x4.CreateTranslation(origin), NeighborGfx);
        }
        return engine;
    }
}
