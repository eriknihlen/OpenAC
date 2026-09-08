using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class CameraCollisionIndoorTests
{

    private const uint OutdoorCellId = 0xA9B40001u;  // landcell (0,0)
    private const uint LandblockId   = 0xA9B40000u;

    private static readonly Vector3 PivotWorld = new(0f, 1f, 95.5f);

    // Desired eye: backward past the building wall at Y=4.0.
    private static readonly Vector3 DesiredEye = new(0f, 5f, 96.25f);

    private const float BuildingWallY = 4.0f;

    private const float MinExpectedPullIn = 0.5f;

    [Fact]
    public void SweepEye_OutdoorCell_StoppedByBuildingChannelShell()
    {
        var (engine, _) = BuildEngineWithBuilding();
        var probe = new PhysicsCameraCollisionProbe(engine);

        var stoppedEye = probe.SweepEye(
            pivot:          PivotWorld,
            desiredEye:     DesiredEye,
            cellId:         OutdoorCellId,
            selfEntityId:   0u,
            playerPos:      PivotWorld - new Vector3(0f, 0f, 1.5f)).Eye;

        float pulledIn = MathF.Abs(DesiredEye.Y - stoppedEye.Y);

        Assert.True(
            pulledIn >= MinExpectedPullIn,
            $"Camera sweep should be stopped by the building-channel shell BSP at " +
            $"Y={BuildingWallY:F1} (cache.GetBuilding({OutdoorCellId:X8}) with ModelId set). " +
            $"Actual pulled-in: {pulledIn:F4} m (stopped eye Y={stoppedEye.Y:F4}). " +
            $"REGRESSION: Transition.FindBuildingCollisions " +
            $"(CBuildingObj::find_building_collisions) is not engaging " +
            $"for the viewer's outdoor primary cell.");
    }

    [Fact]
    public void SweepEye_BuildingWithoutModelId_ChannelInert()
    {
        var (engine, cache) = BuildEngineWithBuilding();

        // Replace the building entry with a model-less one.
        cache.RegisterBuildingForTest(OutdoorCellId, new BuildingPhysics
        {
            WorldTransform        = Matrix4x4.CreateTranslation(0f, BuildingWallY, 96f),
            InverseWorldTransform = InvertOrIdentity(Matrix4x4.CreateTranslation(0f, BuildingWallY, 96f)),
            Portals               = Array.Empty<BldPortalInfo>(),
            ModelId               = 0u,
        });

        var probe = new PhysicsCameraCollisionProbe(engine);
        var stoppedEye = probe.SweepEye(
            pivot:          PivotWorld,
            desiredEye:     DesiredEye,
            cellId:         OutdoorCellId,
            selfEntityId:   0u,
            playerPos:      PivotWorld - new Vector3(0f, 0f, 1.5f)).Eye;

        Assert.True(MathF.Abs(DesiredEye.Y - stoppedEye.Y) < 0.05f,
            $"Model-less building entries must keep the channel inert; eye stopped at " +
            $"Y={stoppedEye.Y:F4} instead of reaching {DesiredEye.Y:F4}.");
    }

    // ── Engine + fixture builder ──────────────────────────────────────────

    private static Matrix4x4 InvertOrIdentity(Matrix4x4 m)
        => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    private static (PhysicsEngine engine, PhysicsDataCache cache)
        BuildEngineWithBuilding()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        // ── 1. Shell GfxObj: one two-sided wall polygon at local Y=0 ──────
        const uint ShellGfxId = 0x01AABB01u;
        const ushort WallPolyId = 1;

        var wallPoly = new ResolvedPolygon
        {
            Vertices  = new[]
            {
                new Vector3(-3f, 0f, -3f),
                new Vector3( 3f, 0f, -3f),
                new Vector3( 3f, 0f,  3f),
                new Vector3(-3f, 0f,  3f),
            },
            Plane     = new System.Numerics.Plane(new Vector3(0f, -1f, 0f), 0f),
            NumPoints = 4,
            SidesType = CullMode.None,   // two-sided: stops from both directions
        };

        var gfxLeaf = new PhysicsBSPNode
        {
            Type           = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 10f },
        };
        gfxLeaf.Polygons.Add(WallPolyId);

        var gfxPhysics = new GfxObjPhysics
        {
            BSP             = new PhysicsBSPTree { Root = gfxLeaf },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = new Dictionary<ushort, ResolvedPolygon> { [WallPolyId] = wallPoly },
            BoundingSphere  = new Sphere { Origin = Vector3.Zero, Radius = 10f },
        };
        cache.RegisterGfxObjForTest(ShellGfxId, gfxPhysics);

        // ── 2. The per-LandCell building reference ─────────────────────────
        var bldTransform = Matrix4x4.CreateTranslation(0f, BuildingWallY, 96f);
        cache.RegisterBuildingForTest(OutdoorCellId, new BuildingPhysics
        {
            WorldTransform        = bldTransform,
            InverseWorldTransform = InvertOrIdentity(bldTransform),
            Portals               = Array.Empty<BldPortalInfo>(),
            ModelId               = ShellGfxId,
        });

        // ── 3. Stub landblock: terrain far below ───────────────────────────
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

        return (engine, cache);
    }
}
