using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;

namespace AcDream.Core.Tests.Physics;

public sealed class PhysicsDataCacheProductionTests
{
    [Fact]
    public void CreateProduction_SelectsFlatTraversal()
    {
        PhysicsDataCache cache = PhysicsDataCache.CreateProduction();

        Assert.Equal(
            CollisionTraversalMode.Flat,
            cache.CollisionTraversalMode);
    }

    [Fact]
    public void ProductionSetupPublication_RejectsMissingPreparedAsset()
    {
        PhysicsDataCache cache = PhysicsDataCache.CreateProduction();

        InvalidOperationException fault = Assert.Throws<InvalidOperationException>(
            () => cache.CacheSetup(0x0200_0001u, new Setup()));

        Assert.Contains(
            "no prepared collision asset",
            fault.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GraphOracleFixture_StillAcceptsSourceSetup()
    {
        var cache = new PhysicsDataCache();

        cache.CacheSetup(0x0200_0001u, new Setup());

        Assert.NotNull(cache.GetSetup(0x0200_0001u));
    }

    [Fact]
    public void ProductionSetupPublication_AllowsIdempotentCachedFlatAsset()
    {
        PhysicsDataCache cache = PhysicsDataCache.CreateProduction();
        var setup = new Setup();
        var prepared = new FlatSetupCollision(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            ImmutableArray<FlatCollisionSphere>.Empty,
            0f,
            0f,
            0f,
            0f);

        cache.CacheSetup(0x0200_0001u, setup, prepared);
        cache.CacheSetup(0x0200_0001u, setup);

        Assert.Same(prepared, cache.GetFlatSetup(0x0200_0001u));
    }

    [Fact]
    public void ProductionPreparedRecords_RetainNoParsedCollisionGraph()
    {
        const uint gfxObjId = 0x0100_0001u;
        const uint setupId = 0x0200_0001u;
        const uint cellId = 0xA9B4_0100u;
        PhysicsDataCache cache = PhysicsDataCache.CreateProduction();
        Assert.Null(cache.CollisionShadow);

        var node = new FlatPhysicsBspNode(
            BSPNodeType.Leaf,
            default,
            -1,
            -1,
            0,
            0,
            new FlatCollisionSphere(Vector3.Zero, 3f),
            new FlatIndexRange(0, 0));
        var physicsBsp = new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(node),
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);
        var gfx = new FlatGfxObjCollisionAsset(
            physicsBsp,
            new FlatCollisionSphere(Vector3.Zero, 3f),
            new FlatGfxObjVisualBounds(
                -Vector3.One,
                Vector3.One,
                Vector3.Zero,
                1.7320508f,
                Vector3.One));
        var setup = new FlatSetupCollision(
            ImmutableArray.Create(
                new FlatCollisionCylinder(
                    Vector3.UnitZ,
                    0.4f,
                    1.2f)),
            ImmutableArray<FlatCollisionSphere>.Empty,
            1.2f,
            0.4f,
            0.5f,
            0.5f);
        var structure = new FlatCellStructureCollisionAsset(
            physicsBsp,
            new FlatCellContainmentBsp(
                0,
                ImmutableArray.Create(new FlatCellBspNode(
                    BSPNodeType.Leaf,
                    default,
                    -1,
                    -1,
                    0))),
            FlatPolygonTable.Empty);
        var topology = new FlatEnvCellTopology(
            ImmutableArray<FlatEnvCellPortal>.Empty,
            ImmutableArray<uint>.Empty,
            seenOutside: false);

        cache.CacheGfxObj(gfxObjId, gfx);
        cache.CacheSetup(setupId, setup);
        cache.CacheCellStruct(
            cellId,
            new EnvCell(),
            Matrix4x4.Identity,
            structure,
            topology);

        GfxObjPhysics retainedGfx = Assert.IsType<GfxObjPhysics>(
            cache.GetGfxObj(gfxObjId));
        Assert.Null(retainedGfx.BSP);
        Assert.Null(retainedGfx.PhysicsPolygons);
        Assert.Null(retainedGfx.Vertices);
        Assert.Empty(retainedGfx.Resolved);

        SetupPhysics retainedSetup = Assert.IsType<SetupPhysics>(
            cache.GetSetup(setupId));
        Assert.Empty(retainedSetup.CylSpheres);
        Assert.Empty(retainedSetup.Spheres);

        CellPhysics retainedCell = Assert.IsType<CellPhysics>(
            cache.GetCellStruct(cellId));
        Assert.Null(retainedCell.BSP);
        Assert.Null(retainedCell.CellBSP);
        Assert.Null(retainedCell.PhysicsPolygons);
        Assert.Null(retainedCell.Vertices);
        Assert.Null(retainedCell.PortalPolygons);
        Assert.Empty(retainedCell.Resolved);

        Assert.Equal(0, cache.GraphGfxObjCount);
        Assert.Equal(0, cache.GraphSetupCount);
        Assert.Equal(0, cache.GraphCellStructCount);
        Assert.Equal(1, cache.FlatGfxObjCount);
        Assert.Equal(1, cache.FlatSetupCount);
        Assert.Equal(1, cache.FlatCellStructCount);
        Assert.Equal(1, cache.FlatEnvCellCount);
        var runtimeCell =
            Assert.IsType<AcDream.Core.World.Cells.EnvCell>(
                cache.CellGraph.GetVisible(cellId));
        Assert.Null(runtimeCell.ContainmentBsp);
        Assert.Same(
            structure.ContainmentBsp,
            runtimeCell.FlatContainmentBsp);
    }

    [Fact]
    public void ProductionCellPublication_RejectsRootlessContainment_ThenAllowsValidRetry()
    {
        const uint cellId = 0xA9B4_0174u;
        PhysicsDataCache cache = PhysicsDataCache.CreateProduction();
        var emptyPhysics = new FlatPhysicsBsp(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);
        var emptyContainment = new FlatCellContainmentBsp(
            -1,
            ImmutableArray<FlatCellBspNode>.Empty);
        var structure = new FlatCellStructureCollisionAsset(
            emptyPhysics,
            emptyContainment,
            FlatPolygonTable.Empty);
        var topology = new FlatEnvCellTopology(
            ImmutableArray<FlatEnvCellPortal>.Empty,
            ImmutableArray<uint>.Empty,
            seenOutside: false);

        cache.CacheCellStruct(
            cellId,
            new EnvCell(),
            Matrix4x4.Identity,
            structure,
            topology);

        Assert.Null(cache.GetCellStruct(cellId));
        Assert.Null(cache.CellGraph.GetVisible(cellId));
        Assert.Equal(0, cache.FlatCellStructCount);
        Assert.Equal(0, cache.FlatEnvCellCount);

        var validContainment = new FlatCellContainmentBsp(
            0,
            ImmutableArray.Create(new FlatCellBspNode(
                BSPNodeType.Leaf,
                default,
                -1,
                -1,
                0)));
        cache.CacheCellStruct(
            cellId,
            new EnvCell(),
            Matrix4x4.Identity,
            new FlatCellStructureCollisionAsset(
                emptyPhysics,
                validContainment,
                FlatPolygonTable.Empty),
            topology);

        CellPhysics loaded = Assert.IsType<CellPhysics>(cache.GetCellStruct(cellId));
        Assert.False(CollisionTraversal.HasPhysics(cache, loaded));
        Assert.True(CollisionTraversal.HasCellContainment(cache, loaded));
        Assert.NotNull(cache.CellGraph.GetVisible(cellId));
        Assert.Equal(1, cache.CellStructCount);
        Assert.Equal(1, cache.FlatCellStructCount);
        Assert.Equal(0, cache.GraphCellStructCount);
    }
}
