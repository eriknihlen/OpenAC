using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsEngineTests
{
    private static float[] LinearHeightTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = i * 1.0f;
        return table;
    }

    private static byte[] FlatHeightmap(byte value = 50)
    {
        var heights = new byte[81];
        Array.Fill(heights, value);
        return heights;
    }

    private PhysicsEngine MakeFlatEngine(float terrainZ = 50f)
    {
        var engine = new PhysicsEngine();
        var terrain = new TerrainSurface(FlatHeightmap((byte)terrainZ), LinearHeightTable());
        engine.AddLandblock(0xA9B4FFFFu, terrain, Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    [Fact]
    public void ResolveWithTransition_OutdoorCellBoundary_UpdatesLowCellId()
    {
        var engine = MakeFlatEngine(terrainZ: 50f);

        var result = engine.ResolveWithTransition(
            currentPos: new Vector3(23f, 10f, 50f),
            targetPos:  new Vector3(25f, 10f, 50f),
            cellId: 0x0001u,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true);

        Assert.True(result.IsOnGround);
        Assert.InRange(result.Position.X, 24.9f, 25.1f);
        Assert.Equal(0xA9B40009u, result.CellId);
    }

    [Fact]
    public void ResolveWithTransition_EdgeSlideFlag_AllowsNormalFlatMovement()
    {
        var engine = MakeFlatEngine(terrainZ: 50f);

        var result = engine.ResolveWithTransition(
            currentPos: new Vector3(96f, 96f, 50f),
            targetPos:  new Vector3(98f, 96f, 50f),
            cellId: 0x0025u,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true,
            moverFlags: ObjectInfoState.EdgeSlide);

        Assert.True(result.IsOnGround);
        Assert.InRange(result.Position.X, 97.9f, 98.1f);
        Assert.Equal(0xA9B40025u, result.CellId);
    }

    [Fact]
    public void ResolveWithTransition_EdgeSlideStopsAtLoadedTerrainBoundary()
    {
        var engine = MakeFlatEngine(terrainZ: 50f);
        var body = new PhysicsBody
        {
            Position = new Vector3(191.25f, 96f, 50f),
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            ContactPlaneValid = true,
            ContactPlane = new Plane(Vector3.UnitZ, -50f),
            ContactPlaneCellId = 0x003Du,
        };

        var result = engine.ResolveWithTransition(
            currentPos: new Vector3(191.25f, 96f, 50f),
            targetPos:  new Vector3(193f, 96f, 50f),
            cellId: 0x003Du,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.EdgeSlide);

        Assert.True(result.IsOnGround);
        Assert.InRange(result.Position.X, 190.75f, 192.0001f);
        Assert.Equal(50f, result.Position.Z, precision: 2);
    }

    [Fact]
    public void ResolveWithTransition_EdgeSlideAtLoadedTerrainBoundary_PreservesTangentMotion()
    {
        var engine = MakeFlatEngine(terrainZ: 50f);
        var body = new PhysicsBody
        {
            Position = new Vector3(191f, 96f, 50f),
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            ContactPlaneValid = true,
            ContactPlane = new Plane(Vector3.UnitZ, -50f),
            ContactPlaneCellId = 0x003Du,
        };

        var settled = engine.ResolveWithTransition(
            currentPos: new Vector3(191f, 96f, 50f),
            targetPos:  new Vector3(191.25f, 96f, 50f),
            cellId: 0x003Du,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.EdgeSlide);

        Assert.True(body.WalkablePolygonValid);
        Assert.NotNull(body.WalkableVertices);

        var result = engine.ResolveWithTransition(
            currentPos: settled.Position,
            targetPos:  new Vector3(193f, 98f, 50f),
            cellId: 0x003Du,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true,
            body: body,
            moverFlags: ObjectInfoState.EdgeSlide);

        Assert.True(result.IsOnGround);
        Assert.InRange(result.Position.X, 190.75f, 192.0001f);
        Assert.True(result.Position.Y > 96.2f);
        Assert.Equal(50f, result.Position.Z, precision: 2);
    }

    [Fact]
    public void ResolveWithTransition_LandblockBoundary_UpdatesFullOutdoorCellId()
    {
        var engine = new PhysicsEngine();

        var terrainA = new TerrainSurface(FlatHeightmap(50), LinearHeightTable());
        engine.AddLandblock(0xA9B4FFFFu, terrainA, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);

        var terrainB = new TerrainSurface(FlatHeightmap(50), LinearHeightTable());
        engine.AddLandblock(0xAAB4FFFFu, terrainB, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 192f, worldOffsetY: 0f);

        var result = engine.ResolveWithTransition(
            currentPos: new Vector3(191f, 10f, 50f),
            targetPos:  new Vector3(193f, 10f, 50f),
            cellId: 0xA9B40039u,
            sphereRadius: 0.5f,
            sphereHeight: 1.2f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true);

        Assert.True(result.IsOnGround);
        Assert.InRange(result.Position.X, 192.9f, 193.1f);
        Assert.Equal(0xAAB40001u, result.CellId);
    }

    [Fact]
    public void ResolveWithTransition_SelfShadowEntry_NotPushedWhenIdMatches()
    {
        var freshCache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = freshCache };
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(FlatHeightmap(50), LinearHeightTable()),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        freshCache.CellGraph.RegisterTerrain(0xA9B4FFFFu, new TerrainSurface(FlatHeightmap(50), LinearHeightTable()), Vector3.Zero);

        const uint movingEntityId = 0xDEADBEEFu;
        var bodyPos   = new Vector3(96f, 96f, 50f);
        var targetPos = bodyPos + new Vector3(0f, 0f, 0.022f);   // stationary +Z

        engine.ShadowObjects.Register(
            entityId:      movingEntityId,
            gfxObjId:      0x02000001u,
            worldPos:      bodyPos,
            rotation:      Quaternion.Identity,
            radius:        0.679f,
            worldOffsetX:  0f, worldOffsetY: 0f,
            landblockId:   0xA9B4FFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight:     1.835f);

        var unfiltered = engine.ResolveWithTransition(
            currentPos: bodyPos, targetPos: targetPos,
            cellId: 0xA9B40039u,
            sphereRadius: 0.48f, sphereHeight: 1.2f,
            stepUpHeight: 0.4f, stepDownHeight: 0.4f,
            isOnGround: false,
            movingEntityId: 0u);

        Assert.True(unfiltered.Position.Z < targetPos.Z - 0.01f,
            $"Without movingEntityId, the sweep must collide with the mover's own " +
            $"ShadowEntry and deny the +Z movement (retail: land_on_cylinder → " +
            $"Collide re-test → COLLIDED → stay-put). Got Z={unfiltered.Position.Z:F4}, " +
            $"target Z={targetPos.Z:F4}");

        // With the gate: the sweep must leave XY unchanged.
        var filtered = engine.ResolveWithTransition(
            currentPos: bodyPos, targetPos: targetPos,
            cellId: 0xA9B40039u,
            sphereRadius: 0.48f, sphereHeight: 1.2f,
            stepUpHeight: 0.4f, stepDownHeight: 0.4f,
            isOnGround: false,
            movingEntityId: movingEntityId);

        float filteredXY = MathF.Sqrt(
            (filtered.Position.X - targetPos.X) * (filtered.Position.X - targetPos.X) +
            (filtered.Position.Y - targetPos.Y) * (filtered.Position.Y - targetPos.Y));
        Assert.InRange(filteredXY, 0f, 0.001f);
    }
}
