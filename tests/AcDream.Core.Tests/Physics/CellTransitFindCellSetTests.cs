using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitFindCellSetTests
{

    private static CellPhysics MakeCellWithPortalAtRightWall(
        Matrix4x4 worldTransform, uint otherCellId, ushort flags)
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
                new PortalInfo(otherCellId: (ushort)otherCellId, polygonId: 10, flags: flags),
            },
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            }
        };
    }

    // ──────────────────────────────────────────────────────────────────
    // Tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Sphere_FullyInsidePrimaryCell_ReturnsOnlyPrimary()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);

        // Sphere far from any portal — local x=-1, reach to x=-0.5; portal at x=2.5.
        var sphereCenter = new Vector3(-1.0f, 0f, 2.5f);

        uint containing = CellTransit.FindCellSet(
            cache, sphereCenter, sphereRadius: 0.5f,
            currentCellId: 0xA9B40100u,
            out var cellSet);

        Assert.Equal(0xA9B40100u, containing);
        Assert.Single(cellSet);
        Assert.Contains(0xA9B40100u, cellSet);
    }

    [Fact]
    public void Sphere_StraddlingPortal_ReturnsBothCells()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform        = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            }
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var sphereCenter = new Vector3(2.0f, 0f, 2.5f);

        uint containing = CellTransit.FindCellSet(
            cache, sphereCenter, sphereRadius: 0.5f,
            currentCellId: 0xA9B40100u,
            out var cellSet);

        Assert.Contains(0xA9B40100u, cellSet);
        Assert.Contains(0xA9B40101u, cellSet);
    }

    [Fact]
    public void FindCellSet_OutdoorSeed_IncludesNeighbourLandcells()
    {
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(0xA9B40000u, new TerrainSurface(new byte[81], new float[256]), Vector3.Zero);
        var sphereCenter = new Vector3(23.8f, 12f, 0f);

        uint containing = CellTransit.FindCellSet(
            cache, sphereCenter, sphereRadius: 0.5f,
            currentCellId: 0xA9B40001u,
            out var cellSet);

        Assert.Equal(0xA9B40001u, containing);
        Assert.True(cellSet.Count >= 2, $"Expected ≥2 cells in set (primary + east neighbour), got {cellSet.Count}");
    }

    [Fact]
    public void IndoorSeed_ExitPortalTouchedOnlyBySecondSphere_AddsOutdoorLandcell()
    {
        var exitCell = MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity,
            otherCellId: 0xFFFF,
            flags: 0);

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, exitCell);

        var spheres = new[]
        {
            // Foot sphere is not near the exit portal plane at local x=2.5.
            new Sphere { Origin = new Vector3(0f, 12f, 2.5f), Radius = 0.5f },
            // Head sphere reaches the exit portal plane and should trigger
            // outdoor landcell expansion.
            new Sphere { Origin = new Vector3(2f, 12f, 3.2f), Radius = 0.5f },
        };

        uint containing = CellTransit.FindCellSet(
            cache, spheres, spheres.Length,
            currentCellId: 0xA9B40100u,
            out var cellSet);

        Assert.Equal(0xA9B40100u, containing);
        Assert.Contains(0xA9B40100u, cellSet);
        Assert.Contains(0xA9B40001u, cellSet);
    }


    [Fact]
    public void OutdoorSeed_CrossesLandblockBoundary_South_AfterDestinationHydrates()
    {
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(0xA9B40000u, new TerrainSurface(new byte[81], new float[256]), Vector3.Zero);

        uint unavailable = CellTransit.FindCellSet(
            cache, new Vector3(150f, -0.2f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B40031u,
            out var cellSet);

        Assert.Equal(0xA9B40031u, unavailable);
        Assert.Contains(0xA9B30038u, cellSet);
        Assert.Contains(0xA9B40031u, cellSet);   // +Y neighbour still in the set

        cache.CellGraph.RegisterTerrain(
            0xA9B30000u,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(0f, -192f, 0f));
        uint containing = CellTransit.FindCellSet(
            cache, new Vector3(150f, -0.2f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B40031u,
            out _);

        Assert.Equal(0xA9B30038u, containing);
    }

    [Fact]
    public void OutdoorSeed_NearBoundaryButInside_StaysCurrent()
    {
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(0xA9B40000u, new TerrainSurface(new byte[81], new float[256]), Vector3.Zero);

        uint containing = CellTransit.FindCellSet(
            cache, new Vector3(150f, 0.2f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B40031u,
            out var cellSet);

        Assert.Equal(0xA9B40031u, containing);
        Assert.Contains(0xA9B30038u, cellSet);
    }

    [Fact]
    public void OutdoorSeed_NonAnchorBlock_UsesRegisteredTerrainOrigin()
    {
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(
            0xA9B30000u,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(0f, -192f, 0f));
        cache.CellGraph.RegisterTerrain(
            0xA9B40000u,
            new TerrainSurface(new byte[81], new float[256]),
            Vector3.Zero);

        uint containing = CellTransit.FindCellSet(
            cache, new Vector3(150f, 1f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B30038u,
            out _);

        Assert.Equal(0xA9B40031u, containing);
    }


    private static CellPhysics MakeCellWithBoundedBsp(Matrix4x4 worldTransform)
    {
        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform        = worldTransform,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode
                {
                    SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),
                    PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
                },
            }
        };
    }

    [Fact]
    public void IndoorSeed_SphereFullyOutsideHydratedCell_KeepsCurrent_RetailNullResult()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40150u, MakeCellWithBoundedBsp(Matrix4x4.Identity));

        uint containing = CellTransit.FindCellSet(
            cache, new Vector3(-10f, 12f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B40150u,
            out _);

        Assert.Equal(0xA9B40150u, containing);
    }

    [Fact]
    public void IndoorSeed_SphereStraddlesCellBoundary_StaysCurrent()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40150u, MakeCellWithBoundedBsp(Matrix4x4.Identity));

        uint containing = CellTransit.FindCellSet(
            cache, new Vector3(-0.3f, 12f, 0f), sphereRadius: 0.5f,
            currentCellId: 0xA9B40150u,
            out _);

        Assert.Equal(0xA9B40150u, containing);
    }

    [Fact]
    public void FindVisibleChildCell_RootlessContainment_IsUnavailable()
    {
        Matrix4x4.Invert(Matrix4x4.Identity, out var inv);
        var cellNoBsp = new CellPhysics
        {
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            Portals = [new PortalInfo(0x0101, 0, 0)],
        };
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40150u, cellNoBsp);

        uint containing = CellTransit.FindVisibleChildCell(
            cache,
            0xA9B40150u,
            new Vector3(-10f, 12f, 0f),
            useStabList: true);

        Assert.Equal(0u, containing);
    }


    [Theory]
    [InlineData(0xA9B40100u, 0xA9B40101u)]
    [InlineData(0xA9B40101u, 0xA9B40100u)]
    public void TwoOverlappingCells_CurrentCellWinsTheStraddle(uint currentCellId, uint otherCellId)
    {
        ushort currentLow = (ushort)(currentCellId & 0xFFFF);
        ushort otherLow   = (ushort)(otherCellId & 0xFFFF);

        var current = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherLow,   flags: 0);
        var other   = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, currentLow, flags: 0);

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(currentCellId, current);
        cache.RegisterCellStructForTest(otherCellId, other);

        var sphereCenter = new Vector3(2.0f, 0f, 2.5f);

        uint containing = CellTransit.FindCellSet(
            cache, sphereCenter, sphereRadius: 0.5f,
            currentCellId: currentCellId,
            out var cellSet);

        Assert.Contains(currentCellId, cellSet);
        Assert.Contains(otherCellId, cellSet);
        Assert.Equal(currentCellId, containing);
    }

    [Fact]
    public void FindCellSet_CurrentCellIsFirstInTheSet()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree { Root = new CellBSPNode { Type = BSPNodeType.Leaf } },
        };
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var sphereCenter = new Vector3(2.0f, 0f, 2.5f);

        CellTransit.FindCellSet(cache, sphereCenter, 0.5f, 0xA9B40100u, out var cellSet);
        Assert.Equal(0xA9B40100u, cellSet.First());
    }

    [Fact]
    public void IndoorWithExitPortal_InteriorWinsWhileItContainsCentre()
    {
        var exitCell = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0xFFFF, flags: 0);
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, exitCell);

        var sphereCenter = new Vector3(0f, 12f, 2.5f);
        uint containing = CellTransit.FindCellSet(cache, sphereCenter, 0.5f, 0xA9B40100u, out _);
        Assert.Equal(0xA9B40100u, containing);   // interior-wins, not the outdoor landcell
    }
}
