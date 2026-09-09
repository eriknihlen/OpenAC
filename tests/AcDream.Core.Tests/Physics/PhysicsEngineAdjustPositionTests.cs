using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsEngineAdjustPositionTests
{
    private const uint FeetCellId = 0xA9B40174u;
    private const uint RoomCellId = 0xA9B40171u;
    private const uint LandblockId = 0xA9B40000u;

    [Fact]
    public void Indoor_PivotInStabListSibling_ResolvesSiblingAndFound()
    {
        var engine = BuildIndoorEngine();

        var (cellId, found) = engine.AdjustPosition(FeetCellId, new Vector3(0f, 8f, 0f));

        Assert.True(found);
        Assert.Equal(RoomCellId, cellId);
    }

    [Fact]
    public void Indoor_PointInNoCell_NotFound()
    {
        var engine = BuildIndoorEngine();

        var (_, found) = engine.AdjustPosition(FeetCellId, new Vector3(0f, 5f, 0f));

        Assert.False(found);
    }

    [Fact]
    public void Outdoor_PointInLandblock_SnapsToLandcell()
    {
        var engine = BuildIndoorEngine();   // also registers the landblock

        var (cellId, found) = engine.AdjustPosition(0xA9B40001u, new Vector3(12f, 12f, 50f));

        Assert.True(found);
        Assert.Equal(LandblockId, cellId & 0xFFFF0000u);
        Assert.True((cellId & 0xFFFFu) < 0x0100u);          // an outdoor landcell low byte
    }

    // ── fixture ────────────────────────────────────────────────────────────

    private static PhysicsEngine BuildIndoorEngine()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        cache.RegisterCellStructForTest(FeetCellId, MakeCell(InteriorYAtMost(3f), new uint[] { RoomCellId }));
        cache.RegisterCellStructForTest(RoomCellId, MakeCell(InteriorYAtLeast(7f), Array.Empty<uint>()));

        // A flat stub landblock so the outdoor branch has a grid to snap to.
        var heights     = new byte[81];
        var heightTable = new float[256];
        engine.AddLandblock(
            landblockId:  LandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static CellBSPNode InteriorYAtMost(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, -1f, 0f), boundary),
        PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
    };

    private static CellBSPNode InteriorYAtLeast(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, 1f, 0f), -boundary),
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
