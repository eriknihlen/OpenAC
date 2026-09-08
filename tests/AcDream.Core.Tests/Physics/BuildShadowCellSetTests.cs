using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class BuildShadowCellSetTests
{
    private const uint IndoorSeed   = 0xA9B40100u;
    private const uint NeighborCell = 0xA9B40101u;

    private static Sphere[] One(Vector3 center, float radius) =>
        new[] { new Sphere { Origin = center, Radius = radius } };

    private static CellPhysics MakeCellWithPortalAtRightWall(
        Matrix4x4 worldTransform, ushort otherCellId, IReadOnlySet<uint>? visible = null)
    {
        var portalPoly = new ResolvedPolygon
        {
            Vertices  = new[]
            {
                new Vector3(2.5f, -2.5f,  0f),
                new Vector3(2.5f,  2.5f,  0f),
                new Vector3(2.5f,  2.5f,  5f),
                new Vector3(2.5f, -2.5f,  5f),
            },
            Plane     = new Plane(new Vector3(1, 0, 0), -2.5f),  // x = 2.5
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform        = worldTransform,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            PortalPolygons        = new Dictionary<ushort, ResolvedPolygon> { [10] = portalPoly },
            Portals               = new[]
            {
                new PortalInfo(otherCellId: otherCellId, polygonId: 10, flags: 0),
            },
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            },
            VisibleCellIds = visible ?? new HashSet<uint>(),
        };
    }

    private static CellPhysics MakeLeafCell(Matrix4x4 worldTransform)
    {
        Matrix4x4.Invert(worldTransform, out var inv);
        var root = new CellBSPNode { Type = BSPNodeType.Leaf };
        return new CellPhysics
        {
            WorldTransform        = worldTransform,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = root,
            },
            FlatContainmentBsp = FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root),
        };
    }

    private static CellPhysics MakeValidCellWithExteriorPortal(
        Matrix4x4 worldTransform)
    {
        Matrix4x4.Invert(worldTransform, out var inv);
        var portalPlane = new Plane(new Vector3(1f, 0f, 0f), -2.5f);
        var root = new CellBSPNode { Type = BSPNodeType.Leaf };
        return new CellPhysics
        {
            WorldTransform = worldTransform,
            InverseWorldTransform = inv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree { Root = root },
            FlatContainmentBsp = FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root),
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon>
            {
                [10] = new ResolvedPolygon
                {
                    Vertices =
                    [
                        new Vector3(2.5f, -2.5f, 0f),
                        new Vector3(2.5f, 2.5f, 0f),
                        new Vector3(2.5f, 2.5f, 5f),
                        new Vector3(2.5f, -2.5f, 5f),
                    ],
                    Plane = portalPlane,
                    NumPoints = 4,
                    SidesType = CullMode.None,
                },
            },
            Portals =
            [
                new PortalInfo(otherCellId: 0xFFFF, polygonId: 10, flags: 0),
            ],
        };
    }

    private static void RegisterFlatTerrain(PhysicsDataCache cache)
        => cache.CellGraph.RegisterTerrain(
            0xA9B40000u,
            new TerrainSurface(new byte[81], new float[256]),
            Vector3.Zero);

    // ── Seeds ──────────────────────────────────────────────────────────

    [Fact]
    public void IndoorSeed_SphereAwayFromPortals_FloodsSeedOnly()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0x0101));

        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(new Vector3(-1f, 0f, 2.5f), 0.5f), 1,
            isStatic: false);

        Assert.Equal(new[] { IndoorSeed }, set);
    }

    [Fact]
    public void IndoorSeed_SphereOverlapsNeighborBsp_FloodsNeighbor()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0x0101));
        cache.RegisterCellStructForTest(NeighborCell, MakeLeafCell(
            Matrix4x4.CreateTranslation(5f, 0f, 0f)));

        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(new Vector3(2.0f, 0f, 2.5f), 0.5f), 1,
            isStatic: false);

        Assert.Contains(IndoorSeed, set);
        Assert.Contains(NeighborCell, set);
        Assert.Equal(IndoorSeed, set[0]);
    }

    [Fact]
    public void UnloadedIndoorSeed_ReturnsSeedByIdOnly_NoWalk()
    {
        var cache = new PhysicsDataCache();

        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(Vector3.Zero, 0.5f), 1, isStatic: false);

        Assert.Equal(new[] { IndoorSeed }, set);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndoorSeed_RefloodsAfterValidPayloadHydrates(
        bool useFlat)
    {
        var cache = new PhysicsDataCache
        {
            CollisionTraversalMode = useFlat
                ? CollisionTraversalMode.Flat
                : CollisionTraversalMode.Graph,
        };
        Sphere[] sphere = One(new Vector3(2.4f, 0f, 2.5f), 0.5f);

        IReadOnlyList<uint> unavailable = CellTransit.BuildShadowCellSet(
            cache,
            IndoorSeed,
            sphere,
            1,
            isStatic: false);
        Assert.Equal(new[] { IndoorSeed }, unavailable);

        cache.RegisterCellStructForTest(
            IndoorSeed,
            MakeValidCellWithExteriorPortal(Matrix4x4.Identity));
        IReadOnlyList<uint> hydrated = CellTransit.BuildShadowCellSet(
            cache,
            IndoorSeed,
            sphere,
            1,
            isStatic: false);

        Assert.Contains(IndoorSeed, hydrated);
        Assert.Contains(hydrated, id => (id & 0xFFFFu) < 0x0100u);
    }

    [Fact]
    public void OutdoorSeed_FloodsOverlappedLandcells_BlockCrossingMath()
    {
        var cache = new PhysicsDataCache();
        // Near the east boundary of landcell grid(0,0): AddAllOutsideCells
        // adds the east neighbor grid(1,0) (block-local frame, anchor block).
        var set = CellTransit.BuildShadowCellSet(
            cache, 0xA9B40001u, One(new Vector3(23.8f, 12f, 0f), 0.5f), 1,
            isStatic: false);

        Assert.Contains(0xA9B40001u, set);
        Assert.True(set.Count >= 2,
            $"Expected >= 2 outdoor cells (seed + east neighbor), got {set.Count}");
        Assert.All(set, id => Assert.True((id & 0xFFFFu) < 0x0100u));
    }

    // ── The building bridge (outdoor → indoor at registration) ────────

    [Fact]
    public void OutdoorSeed_BuildingOnLandcell_SphereInsideInterior_AddsInteriorCell()
    {
        var cache = new PhysicsDataCache();
        RegisterFlatTerrain(cache);
        cache.RegisterCellStructForTest(NeighborCell, MakeLeafCell(Matrix4x4.Identity));

        var sphere = One(new Vector3(12f, 12f, 0f), 0.5f);
        var probe = CellTransit.BuildShadowCellSet(
            cache, 0xA9B40001u, sphere, 1, isStatic: false);
        uint floodedLandcell = probe[0];

        cache.RegisterBuildingForTest(floodedLandcell, new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals = new[]
            {
                new BldPortalInfo(NeighborCell, otherPortalId: 0, flags: 0),
            },
        });

        var set = CellTransit.BuildShadowCellSet(
            cache, 0xA9B40001u, sphere, 1, isStatic: false);

        Assert.Contains(floodedLandcell, set);
        Assert.Contains(NeighborCell, set);
    }

    [Fact]
    public void OutdoorSeed_BuildingPortalNegativeId_InteriorNotAdded()
    {
        var cache = new PhysicsDataCache();
        RegisterFlatTerrain(cache);
        cache.RegisterCellStructForTest(NeighborCell, MakeLeafCell(Matrix4x4.Identity));

        var sphere = One(new Vector3(12f, 12f, 0f), 0.5f);
        var probe = CellTransit.BuildShadowCellSet(
            cache, 0xA9B40001u, sphere, 1, isStatic: false);
        uint floodedLandcell = probe[0];

        cache.RegisterBuildingForTest(floodedLandcell, new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals = new[]
            {
                new BldPortalInfo(NeighborCell, otherPortalId: unchecked((short)0xFFFF), flags: 0),
            },
        });

        var set = CellTransit.BuildShadowCellSet(
            cache, 0xA9B40001u, sphere, 1, isStatic: false);

        Assert.DoesNotContain(NeighborCell, set);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOutdoorSeed_AddsOutsideButSkipsTransit_UntilTerrainHydrates(
        bool useFlat)
    {
        var cache = new PhysicsDataCache
        {
            CollisionTraversalMode = useFlat
                ? CollisionTraversalMode.Flat
                : CollisionTraversalMode.Graph,
        };
        cache.RegisterCellStructForTest(
            NeighborCell,
            MakeLeafCell(Matrix4x4.Identity));

        var sphere = One(new Vector3(12f, 12f, 0f), 0.5f);
        IReadOnlyList<uint> seeded = CellTransit.BuildShadowCellSet(
            cache,
            0xA9B40001u,
            sphere,
            1,
            isStatic: false);
        uint landcell = seeded[0];
        cache.RegisterBuildingForTest(landcell, new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals =
            [
                new BldPortalInfo(NeighborCell, otherPortalId: 0, flags: 0),
            ],
        });

        IReadOnlyList<uint> unavailable = CellTransit.BuildShadowCellSet(
            cache,
            0xA9B40001u,
            sphere,
            1,
            isStatic: false);

        Assert.NotEmpty(unavailable);
        Assert.All(unavailable, id => Assert.True((id & 0xFFFFu) < 0x0100u));
        Assert.DoesNotContain(NeighborCell, unavailable);

        RegisterFlatTerrain(cache);
        IReadOnlyList<uint> hydrated = CellTransit.BuildShadowCellSet(
            cache,
            0xA9B40001u,
            sphere,
            1,
            isStatic: false);

        Assert.Contains(landcell, hydrated);
        Assert.Contains(NeighborCell, hydrated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedSeed_AbsentAdjacentLandcell_SkipsStaleBuildingUntilAdjacentHydrates(
        bool useFlat)
    {
        const uint seedCell = 0xA9B4_0031u;
        const uint adjacentCell = 0xA9B3_0038u;
        const uint interiorCell = 0xA9B3_0100u;
        var cache = new PhysicsDataCache
        {
            CollisionTraversalMode = useFlat
                ? CollisionTraversalMode.Flat
                : CollisionTraversalMode.Graph,
        };
        cache.CellGraph.RegisterTerrain(
            0xA9B4_0000u,
            new TerrainSurface(new byte[81], new float[256]),
            Vector3.Zero);
        cache.RegisterCellStructForTest(
            interiorCell,
            MakeLeafCell(Matrix4x4.Identity));
        cache.RegisterBuildingForTest(adjacentCell, new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals =
            [
                new BldPortalInfo(interiorCell, otherPortalId: 0, flags: 0),
            ],
        });
        Sphere[] sphere = One(new Vector3(150f, 0.2f, 0f), 0.5f);

        IReadOnlyList<uint> unavailable = CellTransit.BuildShadowCellSet(
            cache,
            seedCell,
            sphere,
            1,
            isStatic: false);

        Assert.Contains(seedCell, unavailable);
        Assert.Contains(adjacentCell, unavailable);
        Assert.DoesNotContain(interiorCell, unavailable);

        cache.CellGraph.RegisterTerrain(
            0xA9B3_0000u,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(0f, -192f, 0f));
        IReadOnlyList<uint> hydrated = CellTransit.BuildShadowCellSet(
            cache,
            seedCell,
            sphere,
            1,
            isStatic: false);

        Assert.Contains(adjacentCell, hydrated);
        Assert.Contains(interiorCell, hydrated);
    }

    // ── Exterior straddle from an indoor seed ──────────────────────────

    [Fact]
    public void IndoorSeed_ExteriorPortalStraddle_AddsOutsideCells()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0xFFFF));

        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(new Vector3(2.4f, 0f, 2.5f), 0.5f), 1,
            isStatic: false);

        Assert.Contains(IndoorSeed, set);
        Assert.Contains(set, id => (id & 0xFFFFu) < 0x0100u);
    }

    [Fact]
    public void IndoorSeed_SphereAwayFromExteriorPortal_NoOutsideCells()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0xFFFF));

        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(new Vector3(-1f, 0f, 2.5f), 0.5f), 1,
            isStatic: false);

        Assert.Equal(new[] { IndoorSeed }, set);
    }

    // ── Static prune (do_not_load_cells) ───────────────────────────────

    [Fact]
    public void StaticIndoorSeed_PrunesCellsOutsideStabList()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0x0101, visible: new HashSet<uint>()));
        cache.RegisterCellStructForTest(NeighborCell, MakeLeafCell(
            Matrix4x4.CreateTranslation(5f, 0f, 0f)));

        var sphere = One(new Vector3(2.0f, 0f, 2.5f), 0.5f);

        var dynamicSet = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, sphere, 1, isStatic: false);
        Assert.Contains(NeighborCell, dynamicSet);

        var staticSet = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, sphere, 1, isStatic: true);
        Assert.Equal(new[] { IndoorSeed }, staticSet);
    }

    [Fact]
    public void StaticIndoorSeed_KeepsCellsInStabList()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(IndoorSeed, MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity, otherCellId: 0x0101,
            visible: new HashSet<uint> { NeighborCell }));
        cache.RegisterCellStructForTest(NeighborCell, MakeLeafCell(
            Matrix4x4.CreateTranslation(5f, 0f, 0f)));

        var staticSet = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, One(new Vector3(2.0f, 0f, 2.5f), 0.5f), 1,
            isStatic: true);

        Assert.Contains(IndoorSeed, staticSet);
        Assert.Contains(NeighborCell, staticSet);
    }

    [Fact]
    public void ZeroSpheres_ReturnsEmpty()
    {
        var cache = new PhysicsDataCache();
        var set = CellTransit.BuildShadowCellSet(
            cache, IndoorSeed, System.Array.Empty<Sphere>(), 0, isStatic: false);
        Assert.Empty(set);
    }
}
