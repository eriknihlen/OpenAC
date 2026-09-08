using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class IndoorContactPlaneRetentionTests
{
    private const uint IndoorCellId = 0xA9B40166u;

    private const float SphereRadius = 0.48f;

    // ── Maximum allowed additional CP writes in 60 frames (post-fix budget) ──
    private const int MaxAdditionalCpWrites = 5;

    // ── Number of simulated frames ────────────────────────────────────────────
    private const int SimulatedFrames = 60;

    // =========================================================================
    // Helpers
    // =========================================================================

    private static PhysicsBSPTree BuildLeafBsp(
        IEnumerable<ushort> polyIds,
        Vector3             bspCenter,
        float               bspRadius = 10f)
    {
        var node = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = bspCenter, Radius = bspRadius },
        };
        foreach (var id in polyIds)
            node.Polygons.Add(id);
        return new PhysicsBSPTree { Root = node };
    }

    private static CellPhysics BuildCellWithFloor(float floorZ, float bspCenterZ)
    {
        // 20×20 upward-facing floor at local Z = floorZ.
        var verts = new[]
        {
            new Vector3(-10f, -10f, floorZ),
            new Vector3( 10f, -10f, floorZ),
            new Vector3( 10f,  10f, floorZ),
            new Vector3(-10f,  10f, floorZ),
        };
        var normal = Vector3.UnitZ;         // straight up
        float d    = -floorZ;              // N·p + D = 0  →  D = -floorZ

        var floorPoly = new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = new Plane(normal, d),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var bspCenter = new Vector3(0f, 0f, bspCenterZ);

        return new CellPhysics
        {
            BSP                   = BuildLeafBsp(new ushort[] { 0 }, bspCenter, bspRadius: 10f),
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon> { [0] = floorPoly },
            CellBSP = null,
        };
    }

    private static PhysicsEngine BuildEngine(uint indoorCellId, CellPhysics cell)
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        // Flat terrain height table: all zeroes.
        var heights = new byte[81];
        Array.Fill(heights, (byte)0);
        var ht = new float[256];
        for (int i = 0; i < 256; i++) ht[i] = (float)i;

        uint landblockKey = (indoorCellId & 0xFFFF0000u) | 0xFFFFu;
        engine.AddLandblock(landblockKey,
            new TerrainSurface(heights, ht),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        engine.DataCache.RegisterCellStructForTest(indoorCellId, cell);
        return engine;
    }

    private static Transition BuildGroundedTransition(
        Vector3 worldPos,
        Vector3 worldTarget,
        uint    cellId,
        Plane   floorPlane)
    {
        var t = new Transition();
        t.SpherePath.InitPath(worldPos, worldTarget, cellId, SphereRadius);

        // Grounded state — mirrors BSPStepUpFixtures.MakeGroundedTransition.
        t.ObjectInfo.State          = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
        t.ObjectInfo.StepUpHeight   = 0.04f;
        t.ObjectInfo.StepDownHeight = 0.04f;
        t.ObjectInfo.StepDown       = true;

        // Seed LastKnownContactPlane — the resolver's "we were on this floor last frame" memory.
        t.CollisionInfo.LastKnownContactPlane      = floorPlane;
        t.CollisionInfo.LastKnownContactPlaneValid = true;

        t.CollisionInfo.SetContactPlane(floorPlane, cellId, isWater: false);

        return t;
    }

    // =========================================================================
    // Tests
    // =========================================================================

    [Fact]
    public void IndoorFlatFloorWalking_60Frames_ProducesAtMost5ExtraCpWrites()
    {
        const float floorZ        = 0f;
        const float worldPosZ     = floorZ - 0.05f;          // 5 cm below floor
        const float sphereCenterZ = worldPosZ + SphereRadius; // = 0.43

        var floorPlane = new Plane(Vector3.UnitZ, -floorZ);   // N·p + D = 0 → D = 0
        var worldPos   = new Vector3(0f, 0f, worldPosZ);

        var cell   = BuildCellWithFloor(floorZ, bspCenterZ: sphereCenterZ);
        var engine = BuildEngine(IndoorCellId, cell);
        var t      = BuildGroundedTransition(
            worldPos,
            worldPos + new Vector3(0.001f, 0f, 0f),  // tiny horizontal target
            IndoorCellId,
            floorPlane);

        int seededWrites = t.CollisionInfo.ContactPlaneWriteCount;
        Assert.Equal(1, seededWrites);

        for (int frame = 0; frame < SimulatedFrames; frame++)
        {
            var newPos = new Vector3(frame * 0.001f, 0f, worldPosZ);
            t.SpherePath.SetCheckPos(newPos, IndoorCellId);

            t.FindEnvCollisions(engine, IndoorCellId);
        }

        // ── Assert ────────────────────────────────────────────────────────────
        int totalWrites      = t.CollisionInfo.ContactPlaneWriteCount;
        int additionalWrites = totalWrites - seededWrites;

        Assert.True(
            additionalWrites <= MaxAdditionalCpWrites,
            $"Expected ≤{MaxAdditionalCpWrites} additional CP writes across " +
            $"{SimulatedFrames} flat-floor frames, got {additionalWrites}. " +
            "Finding 2 fix not complete.");
    }
}
