using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitFindVisibleChildCellTests
{
    private const uint StartCellId   = 0xA9B40174u;  // low 0x0174 ≥ 0x0100 → indoor
    private const uint SiblingCellId = 0xA9B40171u;  // the "room above" in StartCell's stab list

    [Fact]
    public void PointInsideStartCell_ReturnsStartCell()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(StartCellId,  MakeCell(InteriorYAtMost(3f), new uint[] { SiblingCellId }));
        cache.RegisterCellStructForTest(SiblingCellId, MakeCell(InteriorYAtLeast(7f), Array.Empty<uint>()));

        uint result = CellTransit.FindVisibleChildCell(cache, StartCellId, new Vector3(0f, 1f, 0f), useStabList: true);

        Assert.Equal(StartCellId, result);
    }

    [Fact]
    public void PointInStabListSibling_ReturnsSibling()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(StartCellId,  MakeCell(InteriorYAtMost(3f), new uint[] { SiblingCellId }));
        cache.RegisterCellStructForTest(SiblingCellId, MakeCell(InteriorYAtLeast(7f), Array.Empty<uint>()));

        // P at Y=8 is outside A (Y≤3) but inside B (Y≥7), and B is in A's stab list.
        uint result = CellTransit.FindVisibleChildCell(cache, StartCellId, new Vector3(0f, 8f, 0f), useStabList: true);

        Assert.Equal(SiblingCellId, result);
    }

    [Fact]
    public void PointInNoCell_ReturnsZero()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(StartCellId,  MakeCell(InteriorYAtMost(3f), new uint[] { SiblingCellId }));
        cache.RegisterCellStructForTest(SiblingCellId, MakeCell(InteriorYAtLeast(7f), Array.Empty<uint>()));

        // P at Y=5 is in the gap: outside A (Y≤3) and outside B (Y≥7).
        uint result = CellTransit.FindVisibleChildCell(cache, StartCellId, new Vector3(0f, 5f, 0f), useStabList: true);

        Assert.Equal(0u, result);
    }

    [Fact]
    public void UnknownStartCell_ReturnsZero()
    {
        var cache = new PhysicsDataCache();
        uint result = CellTransit.FindVisibleChildCell(cache, 0xDEADBEEFu, new Vector3(0f, 1f, 0f), useStabList: true);
        Assert.Equal(0u, result);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static CellBSPNode InteriorYAtMost(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, -1f, 0f), boundary),  // dist = boundary − Y ≥ 0 ⇔ Y ≤ boundary
        PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
    };

    private static CellBSPNode InteriorYAtLeast(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, 1f, 0f), -boundary),  // dist = Y − boundary ≥ 0 ⇔ Y ≥ boundary
        PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
    };

    private static CellPhysics MakeCell(CellBSPNode cellBspRoot, uint[] visibleCellIds) => new()
    {
        BSP                   = new PhysicsBSPTree { Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf } },
        WorldTransform        = Matrix4x4.Identity,
        InverseWorldTransform = Matrix4x4.Identity,
        Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
        CellBSP               = new CellBSPTree { Root = cellBspRoot },
        Portals               = [new PortalInfo(0xFFFF, 0, 0)],
        PortalPolygons        = new Dictionary<ushort, ResolvedPolygon>(),
        VisibleCellIds        = new HashSet<uint>(visibleCellIds),
    };
}
