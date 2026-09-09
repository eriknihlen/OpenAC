using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class CameraCollisionUpdateViewerTests
{
    private const uint FeetCellId = 0xA9B40174u;
    private const uint RoomCellId = 0xA9B40171u;
    private const uint LandblockId = 0xA9B40000u;

    [Fact]
    public void SweepEye_IndoorPivotInCellAboveFeet_SeatsStartAtPivotCell()
    {
        var engine = BuildTwoCellEngine();
        var probe  = new PhysicsCameraCollisionProbe(engine);

        var feet  = new Vector3(0f, 0f, 93f);
        var pivot = new Vector3(0f, 0f, 94.5f);
        var eye   = new Vector3(0f, 3f, 95.5f);   // behind + up, still in the room region, no wall

        var result = probe.SweepEye(pivot, eye, cellId: FeetCellId, selfEntityId: 0u, playerPos: feet);

        Assert.Equal(RoomCellId, result.ViewerCellId);
    }

    [Fact]
    public void SweepEye_NoStartCell_SnapsToPlayer()
    {
        var probe = new PhysicsCameraCollisionProbe(new PhysicsEngine());

        var player = new Vector3(7f, 8f, 9f);
        var result = probe.SweepEye(
            pivot: new Vector3(7f, 8f, 10.5f), desiredEye: new Vector3(7f, 13f, 11f),
            cellId: 0u, selfEntityId: 0u, playerPos: player);

        Assert.Equal(player, result.Eye);
        Assert.Equal(0u, result.ViewerCellId);
    }

    // ── fixture ────────────────────────────────────────────────────────────

    private static PhysicsEngine BuildTwoCellEngine()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        cache.RegisterCellStructForTest(FeetCellId, MakeCell(InteriorZAtMost(94f), new uint[] { RoomCellId }));
        cache.RegisterCellStructForTest(RoomCellId, MakeCell(InteriorZAtLeast(94f), Array.Empty<uint>()));

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  LandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static CellBSPNode InteriorZAtMost(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, 0f, -1f), boundary),   // dist = boundary − Z ≥ 0 ⇔ Z ≤ boundary
        PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
    };

    private static CellBSPNode InteriorZAtLeast(float boundary) => new()
    {
        SplittingPlane = new Plane(new Vector3(0f, 0f, 1f), -boundary),   // dist = Z − boundary ≥ 0 ⇔ Z ≥ boundary
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
