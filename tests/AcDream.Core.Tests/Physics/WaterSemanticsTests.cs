using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class WaterSemanticsTests
{
    // ── §1: TerrainSurface.SampleWaterDepth golden values ──────────────────

    /// <summary>terrainTypes byte whose (byte&gt;&gt;2)&amp;0x1F == 0x10 (WaterRunning, the lowest water type).</summary>
    private const byte WaterTerrainByte = 0x10 << 2; // 0x40
    private const byte DryTerrainByte = 0x00;

    private static byte[] AllVertices(byte value)
    {
        var arr = new byte[81];
        Array.Fill(arr, value);
        return arr;
    }

    [Fact]
    public void SampleWaterDepth_NotWaterCell_ReturnsZero()
    {
        var surface = new TerrainSurface(
            new byte[81], new float[256],
            terrainTypes: AllVertices(DryTerrainByte));

        Assert.Equal(0f, surface.SampleWaterDepth(12f, 12f));
    }

    [Fact]
    public void SampleWaterDepth_EntirelyWaterCell_ReturnsPoint9()
    {
        var surface = new TerrainSurface(
            new byte[81], new float[256],
            terrainTypes: AllVertices(WaterTerrainByte));

        Assert.Equal(0.9f, surface.SampleWaterDepth(12f, 12f));
    }

    [Fact]
    public void SampleWaterDepth_PartiallyWaterCell_WaterCorner_ReturnsPoint45()
    {
        var types = new byte[81];
        types[1 * 9 + 1] = WaterTerrainByte; // vertex (x=1, y=1)

        var surface = new TerrainSurface(new byte[81], new float[256], terrainTypes: types);

        Assert.Equal(0.45f, surface.SampleWaterDepth(18f, 18f));
    }

    [Fact]
    public void SampleWaterDepth_PartiallyWaterCell_DryCorner_ReturnsPoint1_RestoredRetailConstant()
    {
        var types = new byte[81];
        types[1 * 9 + 1] = WaterTerrainByte;

        var surface = new TerrainSurface(new byte[81], new float[256], terrainTypes: types);

        Assert.Equal(0.1f, surface.SampleWaterDepth(4f, 4f));
    }

    [Fact]
    public void SampleWaterDepth_DryCorner_StaysBelowTheIsWaterClassificationThreshold()
    {
        var types = new byte[81];
        types[1 * 9 + 1] = WaterTerrainByte;
        var surface = new TerrainSurface(new byte[81], new float[256], terrainTypes: types);

        float dryDepth = surface.SampleWaterDepth(4f, 4f);
        Assert.True(dryDepth < 0.45f, $"Dry-corner depth {dryDepth} must stay below the isWater threshold");
    }

    // ── §2: WATER_CONTACT_TS mirroring in PhysicsObjUpdate ─────────────────

    private static PhysicsBody MakeBody(bool contactPlaneIsWater) => new()
    {
        TransientState = TransientStateFlags.None,
        ContactPlaneIsWater = contactPlaneIsWater,
    };

    [Fact]
    public void ApplySetPositionContact_WaterContactPlane_SetsWaterContactBit()
    {
        var body = MakeBody(contactPlaneIsWater: true);

        PhysicsObjUpdate.ApplySetPositionContact(body, inContact: true, onWalkable: true);

        Assert.True(body.IsWaterContact);
    }

    [Fact]
    public void ApplySetPositionContact_DryContactPlane_ClearsWaterContactBit()
    {
        var body = MakeBody(contactPlaneIsWater: false);
        body.TransientState |= TransientStateFlags.WaterContact; // pre-seed stale bit

        PhysicsObjUpdate.ApplySetPositionContact(body, inContact: true, onWalkable: true);

        Assert.False(body.IsWaterContact);
    }

    [Fact]
    public void ApplySetPositionContact_WaterContactMirrorsIndependentlyOfContactBit()
    {
        var body = MakeBody(contactPlaneIsWater: true);

        PhysicsObjUpdate.ApplySetPositionContact(body, inContact: false, onWalkable: false);

        Assert.False(body.InContact);
        Assert.True(body.IsWaterContact);
    }

    [Fact]
    public void CommitSetPositionTransition_WaterContactPlane_SetsWaterContactBit()
    {
        var body = MakeBody(contactPlaneIsWater: true);

        PhysicsObjUpdate.CommitSetPositionTransition(
            body,
            inContact: true,
            onWalkable: true,
            collisionNormalValid: false,
            collisionNormal: Vector3.Zero,
            previousContact: false,
            previousOnWalkable: false);

        Assert.True(body.IsWaterContact);
    }

    [Fact]
    public void CommitSetPositionTransition_DryContactPlane_ClearsWaterContactBit()
    {
        var body = MakeBody(contactPlaneIsWater: false);
        body.TransientState |= TransientStateFlags.WaterContact; // pre-seed stale bit

        PhysicsObjUpdate.CommitSetPositionTransition(
            body,
            inContact: true,
            onWalkable: true,
            collisionNormalValid: false,
            collisionNormal: Vector3.Zero,
            previousContact: true,
            previousOnWalkable: true);

        Assert.False(body.IsWaterContact);
    }


    private const uint TestLandblockId = 0xA9B40000u;
    private const uint TestCellId = TestLandblockId | 0x0001u;
    private const float SphereRadius = 0.4f;
    private const float SphereHeight = 1.2f;

    private static PhysicsEngine BuildEngineWithFlatWaterTerrain(bool water)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        var heights = new byte[81];   // all zero -> terrain Z = 0 everywhere
        var heightTable = new float[256];
        var types = water ? AllVertices(WaterTerrainByte) : AllVertices(DryTerrainByte);

        engine.AddLandblock(
            landblockId: TestLandblockId,
            terrain: new TerrainSurface(heights, heightTable, terrainTypes: types),
            cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static PhysicsBody MakeGroundedBody(Vector3 position)
    {
        var floorPlane = new Plane(Vector3.UnitZ, 0f);
        var floorVerts = new[]
        {
            new Vector3(-100f, -100f, 0f),
            new Vector3(100f, -100f, 0f),
            new Vector3(100f, 100f, 0f),
            new Vector3(-100f, 100f, 0f),
        };

        return new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            ContactPlaneValid = true,
            ContactPlane = floorPlane,
            ContactPlaneCellId = TestCellId,
            WalkablePolygonValid = true,
            WalkablePlane = floorPlane,
            WalkableVertices = floorVerts,
            WalkableUp = Vector3.UnitZ,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }

    private static void SettleOntoTerrain(PhysicsEngine engine, PhysicsBody body, int ticks = 60)
    {
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool grounded = true;
        for (int tick = 0; tick < ticks; tick++)
        {
            var target = pos + new Vector3(0f, 0.001f, -0.05f);
            var result = engine.ResolveWithTransition(
                pos, target, cellId,
                SphereRadius, SphereHeight,
                stepUpHeight: 0.04f, stepDownHeight: 0.04f,
                isOnGround: grounded,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer,
                movingEntityId: 0);
            body.Position = result.Position;
            pos = result.Position;
            cellId = result.CellId;
            grounded = result.IsOnGround;
        }
    }

    [Fact]
    public void EndToEnd_SettleOntoEntirelyWaterTerrain_SetsBodyWaterContact()
    {
        var engine = BuildEngineWithFlatWaterTerrain(water: true);
        var body = MakeGroundedBody(new Vector3(12f, 12f, 1.0f));

        SettleOntoTerrain(engine, body);

        Assert.True(MathF.Abs(body.Position.Z - (-0.9f)) < 0.05f,
            $"Body should settle 0.9m below the nominal terrain plane in an EntirelyWater cell; got Z={body.Position.Z:F3}");
        Assert.True(body.ContactPlaneIsWater, "Body must record the water contact plane");
        Assert.True(body.IsWaterContact, "WATER_CONTACT_TS must mirror ContactPlaneIsWater");
    }

    [Fact]
    public void EndToEnd_SettleOntoDryTerrain_LeavesBodyWaterContactClear()
    {
        var engine = BuildEngineWithFlatWaterTerrain(water: false);
        var body = MakeGroundedBody(new Vector3(12f, 12f, 1.0f));
        body.TransientState |= TransientStateFlags.WaterContact; // pre-seed stale bit

        SettleOntoTerrain(engine, body);

        Assert.True(MathF.Abs(body.Position.Z) < 0.05f,
            $"Body should settle exactly on the dry Z=0 terrain plane (no sink-in); got Z={body.Position.Z:F3}");
        Assert.False(body.ContactPlaneIsWater);
        Assert.False(body.IsWaterContact,
            "Dry-land resolves must clear any stale WaterContact bit, not just leave it unset");
    }
}
