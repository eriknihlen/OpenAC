using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitCheckBuildingTransitTests
{
    [Fact]
    public void BuildingPortalWithRootlessContainment_CellIsRejected()
    {

        var building = new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals = new[]
            {
                new BldPortalInfo(
                    otherCellId: 0xA9B40100u,
                    otherPortalId: 0,
                    flags: 0),
            },
        };

        // Rootless fixture bypasses the production quarantine to verify that
        // the traversal boundary still rejects it safely.
        var interiorCell = new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new DatReaderWriter.Types.CellBSPTree { Root = null },
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, interiorCell);

        var candidates = new HashSet<uint>();
        CellTransit.CheckBuildingTransit(
            cache, building,
            worldSphereCenter: new Vector3(0, 0, 0),
            sphereRadius: 0.5f,
            candidates);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildingPortalWithUnavailableCell_NoCandidateAdded()
    {
        var building = new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals =
            [
                new BldPortalInfo(
                    otherCellId: 0xA9B40100u,
                    otherPortalId: 0,
                    flags: 0),
            ],
        };
        var candidates = new HashSet<uint>();

        CellTransit.CheckBuildingTransit(
            new PhysicsDataCache(),
            building,
            worldSphereCenter: Vector3.Zero,
            sphereRadius: 0.5f,
            candidates);

        Assert.Empty(candidates);
    }

    [Fact]
    public void NegativeOtherPortalId_RejectsTransit_PositiveAdmits()
    {
        var leafBsp = new DatReaderWriter.Types.CellBSPTree
        {
            Root = new DatReaderWriter.Types.CellBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
            },
        };

        CellPhysics MakeLeafCell() => new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = leafBsp,
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40101u, MakeLeafCell());
        cache.RegisterCellStructForTest(0xA9B40102u, MakeLeafCell());

        var building = new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals = new[]
            {
                // Wire 0xFFFF reinterpreted signed = -1 → must be skipped.
                new BldPortalInfo(
                    otherCellId: 0xA9B40101u,
                    otherPortalId: unchecked((short)0xFFFF),
                    flags: 0),
                // Non-negative id → admitted (leaf BSP ⇒ sphere inside).
                new BldPortalInfo(
                    otherCellId: 0xA9B40102u,
                    otherPortalId: 0,
                    flags: 0),
            },
        };

        var candidates = new HashSet<uint>();
        CellTransit.CheckBuildingTransit(
            cache, building,
            worldSphereCenter: new Vector3(0, 0, 0),
            sphereRadius: 0.5f,
            candidates);

        Assert.DoesNotContain(0xA9B40101u, candidates);
        Assert.Contains(0xA9B40102u, candidates);
    }

    [Fact]
    public void MultiSphere_AnySphereAdmits_SetsHitsInteriorCell()
    {
        var leafBsp = new DatReaderWriter.Types.CellBSPTree
        {
            Root = new DatReaderWriter.Types.CellBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
            },
        };
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40103u, new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = leafBsp,
        });

        var building = new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals = new[]
            {
                new BldPortalInfo(0xA9B40103u, otherPortalId: 1, flags: 0),
            },
        };

        var spheres = new[]
        {
            new DatReaderWriter.Types.Sphere { Origin = new Vector3(500f, 0f, 0f), Radius = 0.5f },
            new DatReaderWriter.Types.Sphere { Origin = Vector3.Zero,              Radius = 0.5f },
        };

        var candidates = new HashSet<uint>();
        CellTransit.CheckBuildingTransit(
            cache, building, spheres, spheres.Length, candidates, out bool hits);

        Assert.Contains(0xA9B40103u, candidates);
        Assert.True(hits);

        // Zero spheres → nothing admitted, flag stays false.
        var empty = new HashSet<uint>();
        CellTransit.CheckBuildingTransit(
            cache, building, spheres, 0, empty, out bool hitsNone);
        Assert.Empty(empty);
        Assert.False(hitsNone);
    }
}
