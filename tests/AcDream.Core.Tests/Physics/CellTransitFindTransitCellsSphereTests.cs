using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitFindTransitCellsSphereTests
{
    private static CellBSPTree SinglePlaneCellBsp()
    {
        var leaf = new CellBSPNode { Type = DatReaderWriter.Enums.BSPNodeType.Leaf };
        return new CellBSPTree
        {
            Root = new CellBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.BPIn,
                SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),
                PosNode = leaf,
            }
        };
    }

    private static CellPhysics MakeCellWithPortalAtRightWall(
        Matrix4x4 worldTransform, uint otherCellId, ushort flags)
    {
        // Portal poly at local x=2.5 (right wall), normal +X.
        var portalPolyA = new ResolvedPolygon
        {
            Id        = 10,
            Vertices  = new[]
            {
                new Vector3(2.5f, -2.5f,  0f),
                new Vector3(2.5f,  2.5f,  0f),
                new Vector3(2.5f,  2.5f,  5f),
                new Vector3(2.5f, -2.5f,  5f),
            },
            Plane     = new Plane(new Vector3(1, 0, 0), -2.5f),  // x = 2.5
            NumPoints = 4,
            SidesType = DatReaderWriter.Enums.CullMode.None,
        };

        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform = worldTransform,
            InverseWorldTransform = inv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon> { [10] = portalPolyA },
            Portals = new[]
            {
                new PortalInfo(otherCellId: (ushort)otherCellId, polygonId: 10, flags: flags),
            },
        };
    }

    private static CellPhysics PrepareCell(CellPhysics graphCell, uint sourceId)
    {
        FlatCellCollisionAsset prepared =
            FlatCollisionAssetBuilder.FlattenCell(graphCell);
        return new CellPhysics
        {
            SourceId = sourceId,
            WorldTransform = graphCell.WorldTransform,
            InverseWorldTransform = graphCell.InverseWorldTransform,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            FlatPhysicsBsp = prepared.Structure.PhysicsBsp,
            FlatContainmentBsp = prepared.Structure.ContainmentBsp,
            FlatPortalPolygons = prepared.Structure.PortalPolygons,
            FlatTopology = prepared.Topology,
            Portals = graphCell.Portals,
        };
    }

    [Fact]
    public void SphereInsideCellA_NearPortal_AddsCellB()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);

        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var worldSphereCenter = new Vector3(2.0f, 0f, 2.5f);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            worldSphereCenter, sphereRadius: 0.5f, candidates, out bool exitOutside);

        Assert.Contains(0xA9B40101u, candidates);
        Assert.False(exitOutside);
    }

    [Fact]
    public void SphereInsideCellA_FarFromPortal_DoesNotAddCellB()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);

        // Sphere far from portal (local x=-1.0, reach to x=-0.5 — nowhere near portal at x=2.5).
        var worldSphereCenter = new Vector3(-1.0f, 0f, 2.5f);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            worldSphereCenter, sphereRadius: 0.5f, candidates, out bool exitOutside);

        Assert.DoesNotContain(0xA9B40101u, candidates);
    }

    [Fact]
    public void LoadedNeighbor_SphereIntersectsNeighborCellBsp_AddsEvenWhenPortalHintWouldReject()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 2);

        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform        = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP               = SinglePlaneCellBsp(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var worldSphereCenter = new Vector3(4.75f, 0f, 2.5f);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            worldSphereCenter, sphereRadius: 0.5f, candidates, out bool exitOutside);

        Assert.Contains(0xA9B40101u, candidates);
        Assert.False(exitOutside);
    }

    [Fact]
    public void LoadedNeighbor_SphereOutsideNeighborCellBsp_DoesNotUsePortalHintFallback()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);

        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform        = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP               = SinglePlaneCellBsp(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var worldSphereCenter = new Vector3(3.05f, 0f, 2.5f);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            worldSphereCenter, sphereRadius: 0.5f, candidates, out bool exitOutside);

        Assert.DoesNotContain(0xA9B40101u, candidates);
        Assert.False(exitOutside);
    }

    [Fact]
    public void ExitPortal_SphereStraddlesPortalPlane_FlagsCheckOutside()
    {
        var exitCell = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0xFFFF, flags: 0);

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, exitCell);

        var worldSphereCenter = new Vector3(2.0f, 0f, 2.5f);
        var candidates = new HashSet<uint>();

        CellTransit.FindTransitCellsSphere(
            cache, exitCell, currentCellId: 0xA9B40100u,
            worldSphereCenter, sphereRadius: 0.5f, candidates, out bool exitOutside);

        Assert.True(exitOutside);
    }

    [Fact]
    public void ExitPortal_SecondSphereStraddlesPortalPlane_FlagsCheckOutside()
    {
        var exitCell = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0xFFFF, flags: 0);

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, exitCell);

        var spheres = new[]
        {
            new Sphere { Origin = new Vector3(0.0f, 0f, 2.5f), Radius = 0.5f },
            new Sphere { Origin = new Vector3(2.0f, 0f, 3.2f), Radius = 0.5f },
        };
        var candidates = new HashSet<uint>();

        CellTransit.FindTransitCellsSphere(
            cache, exitCell, currentCellId: 0xA9B40100u,
            spheres, spheres.Length, candidates, out bool exitOutside);

        Assert.True(exitOutside);
    }

    [Fact]
    public void PreparedExitPortal_UsesIndexedPlaneWithoutGraphDictionary()
    {
        const uint cellId = 0xA9B40100u;
        CellPhysics graphCell = MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity,
            otherCellId: 0xFFFF,
            flags: 0);
        CellPhysics preparedCell = PrepareCell(graphCell, cellId);
        var cache = PhysicsDataCache.CreateProduction();
        cache.RegisterCellStructForTest(cellId, preparedCell);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache,
            preparedCell,
            cellId,
            new Vector3(2.0f, 0f, 2.5f),
            sphereRadius: 0.5f,
            candidates,
            out bool exitOutside);

        Assert.Null(preparedCell.PortalPolygons);
        Assert.True(exitOutside);
    }

    [Fact]
    public void PreparedLoadedNeighbor_UsesFlatContainmentWithoutGraphObjects()
    {
        const uint cellAId = 0xA9B40100u;
        const uint cellBId = 0xA9B40101u;
        CellPhysics graphCellA = MakeCellWithPortalAtRightWall(
            Matrix4x4.Identity,
            otherCellId: 0x0101,
            flags: 2);

        var cellBTransform = Matrix4x4.CreateTranslation(
            new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBTransform, out Matrix4x4 cellBInverse);
        var graphCellB = new CellPhysics
        {
            WorldTransform = cellBTransform,
            InverseWorldTransform = cellBInverse,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = SinglePlaneCellBsp(),
        };

        CellPhysics preparedCellA = PrepareCell(graphCellA, cellAId);
        CellPhysics preparedCellB = PrepareCell(graphCellB, cellBId);
        var cache = PhysicsDataCache.CreateProduction();
        cache.RegisterCellStructForTest(cellAId, preparedCellA);
        cache.RegisterCellStructForTest(cellBId, preparedCellB);

        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache,
            preparedCellA,
            cellAId,
            new Vector3(4.75f, 0f, 2.5f),
            sphereRadius: 0.5f,
            candidates,
            out bool exitOutside);

        Assert.Null(preparedCellA.PortalPolygons);
        Assert.Null(preparedCellB.CellBSP);
        Assert.Contains(cellBId, candidates);
        Assert.False(exitOutside);
    }
}
