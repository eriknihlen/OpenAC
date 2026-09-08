using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class TransitionTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static float[] LinearHeightTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = i * 1.0f;
        return t;
    }

    private static TerrainSurface FlatTerrain(float terrainZ)
    {
        int idx     = Math.Clamp((int)terrainZ, 0, 255);
        var heights = new byte[81];
        Array.Fill(heights, (byte)idx);
        return new TerrainSurface(heights, LinearHeightTable());
    }

    private static TerrainSurface SlopedTerrain(float baseZ, float risePerCell)
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
        for (int y = 0; y < 9; y++)
        {
            float z   = baseZ + x * risePerCell;
            int   idx = Math.Clamp((int)z, 0, 255);
            heights[x * 9 + y] = (byte)idx;
        }
        return new TerrainSurface(heights, LinearHeightTable());
    }

    private static PhysicsEngine MakeEngine(TerrainSurface terrain)
    {
        var engine = new PhysicsEngine();
        engine.AddLandblock(0xA9B4FFFFu, terrain,
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    private static Transition MakeTransition(
        Vector3 from, Vector3 to,
        float sphereRadius = 0.5f,
        uint cellId        = 0x0001)
    {
        var t  = new Transition();
        t.SpherePath.InitPath(from, to, cellId, sphereRadius);
        t.ObjectInfo.State = ObjectInfoState.None;
        return t;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FindTransitionalPosition_FlatTerrain_MovesFullDistance()
    {
        // Arrange: flat terrain at Z=10, sphere starts at Z=10 (sitting on ground).
        const float groundZ = 10f;
        var terrain = FlatTerrain(groundZ);
        var engine  = MakeEngine(terrain);

        Vector3 from = new(50f, 50f, groundZ);
        Vector3 to   = new(55f, 50f, groundZ);  // 5 units forward

        var transition = MakeTransition(from, to);

        // Act
        bool ok = transition.FindTransitionalPosition(engine);

        // Assert: transition succeeded and position advanced toward the target.
        Assert.True(ok);
        Assert.True(transition.SpherePath.CurPos.X > from.X,
            "Sphere should have moved in +X");
        Assert.InRange(transition.SpherePath.CurPos.X, from.X + 1f, to.X + 0.1f);
        Assert.InRange(transition.SpherePath.CurPos.Z, groundZ - 0.1f, groundZ + 0.1f);
    }

    [Fact]
    public void FindTransitionalPosition_NullBeginCell_ReturnsFalse()
    {
        var terrain = FlatTerrain(0f);
        var engine  = MakeEngine(terrain);

        Vector3 from = new(50f, 50f, 0f);
        Vector3 to   = new(55f, 50f, 0f);

        var transition = MakeTransition(from, to, cellId: 0);

        // Act
        bool ok = transition.FindTransitionalPosition(engine);

        // Assert
        Assert.False(ok, "No beginning cell should abort immediately");
    }

    [Fact]
    public void FindTransitionalPosition_NoTerrain_AllowsPassThrough()
    {
        // Arrange: engine has no landblocks → SampleTerrainZ returns null.
        var engine     = new PhysicsEngine();
        var transition = MakeTransition(new(50f, 50f, 0f), new(55f, 50f, 0f));

        // Act — should not throw; terrain Z is unknown so movement is accepted.
        bool ok = transition.FindTransitionalPosition(engine);

        Assert.True(ok);
    }

    [Fact]
    public void FindTransitionalPosition_ZeroMovement_ReturnsTrueWithUnchangedPosition()
    {
        var terrain    = FlatTerrain(5f);
        var engine     = MakeEngine(terrain);
        var start      = new Vector3(96f, 96f, 5f);
        var transition = MakeTransition(start, start);

        // Act
        bool ok = transition.FindTransitionalPosition(engine);

        // Assert
        Assert.True(ok);
        Assert.Equal(start, transition.SpherePath.CurPos);
    }

    [Fact]
    public void FindTransitionalPosition_SphereAboveTerrain_SnapsTerrain()
    {
        var terrain    = FlatTerrain(0f);
        var engine     = MakeEngine(terrain);
        var from       = new Vector3(50f, 50f, 3f);    // floating above terrain
        var to         = new Vector3(51f, 50f, 3f);
        var transition = MakeTransition(from, to);

        transition.ObjectInfo.State = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;

        // Act
        bool ok = transition.FindTransitionalPosition(engine);

        // Assert: transition returned; sphere should be at or near terrain Z.
        Assert.True(ok);
        Assert.True(transition.SpherePath.CurPos.Z <= from.Z + 0.1f,
            $"Expected Z <= {from.Z + 0.1f}, got {transition.SpherePath.CurPos.Z}");
    }

    [Fact]
    public void FindTransitionalPosition_IntoHill_AdjustsOrStops()
    {
        var terrain = SlopedTerrain(baseZ: 0f, risePerCell: 5f);
        var engine  = MakeEngine(terrain);

        float radius = 0.5f;
        var   from   = new Vector3(12f, 96f, 0f + radius);  // foot on terrain
        var   to     = new Vector3(30f, 96f, 0f + radius);  // moving up the slope

        var transition = MakeTransition(from, to, sphereRadius: radius);

        // Act — must not throw.
        bool ok = transition.FindTransitionalPosition(engine);

        if (ok)
        {
            float terrainAtFinal = terrain.SampleZ(
                transition.SpherePath.CurPos.X, transition.SpherePath.CurPos.Y);
            Assert.True(
                transition.SpherePath.CurPos.Z >= terrainAtFinal - 0.1f,
                $"Sphere went below terrain: posZ={transition.SpherePath.CurPos.Z}, terrainZ={terrainAtFinal}");
        }
        // ok == false is also acceptable (movement was too steep and blocked).
    }

    [Fact]
    public void StepDown_MaintainsGroundContact()
    {
        const float groundZ = 10f;
        var terrain = FlatTerrain(groundZ);
        var engine  = MakeEngine(terrain);

        float radius = 1.0f;  // larger radius → fewer steps needed for same distance
        var   from   = new Vector3(50f, 96f, groundZ + radius);  // foot on terrain
        var   to     = new Vector3(65f, 96f, groundZ + radius);  // 15 units → 15 steps

        var transition = MakeTransition(from, to, sphereRadius: radius);
        transition.ObjectInfo.State = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;

        // Act
        bool ok = transition.FindTransitionalPosition(engine);

        // Assert: movement accepted and sphere stayed on the surface.
        Assert.True(ok);
        float finalBottom = transition.SpherePath.CurPos.Z - radius;
        Assert.True(
            finalBottom >= groundZ - PhysicsGlobals.EPSILON,
            $"Sphere fell below terrain: bottom={finalBottom:F4}, terrainZ={groundZ}");
        Assert.True(
            transition.SpherePath.CurPos.X > from.X,
            "Sphere should have advanced in +X");
    }

    [Fact]
    public void AdjustOffset_ContactPlanePresent_RemovesIntoPlaneComponent()
    {
        var terrain  = FlatTerrain(10f);
        var engine   = MakeEngine(terrain);

        const float groundZ = 10f;
        const float radius  = 0.5f;

        // First transition: move from above onto terrain (sphere sits on ground).
        var from1 = new Vector3(50f, 50f, groundZ + radius);
        var to1   = new Vector3(51f, 50f, groundZ + radius);
        var t1    = MakeTransition(from1, to1, radius);
        bool ok1  = t1.FindTransitionalPosition(engine);

        Assert.True(ok1);

        var from2 = t1.SpherePath.CurPos;
        var to2   = from2 + new Vector3(2f, 0f, 0f);
        var t2    = MakeTransition(from2, to2, radius);
        // Seed as on-walkable (as if we just landed).
        t2.ObjectInfo.State = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;

        bool ok2 = t2.FindTransitionalPosition(engine);

        Assert.True(ok2);
        float bottom = t2.SpherePath.CurPos.Z - radius;
        Assert.True(bottom >= groundZ - PhysicsGlobals.EPSILON,
            $"Sphere bottom {bottom:F4} should be >= terrain {groundZ}");
    }

    [Fact]
    public void FindTransitionalPosition_LongSweep_HasNoInventedStepCap()
    {
        const float groundZ = 10f;
        var engine = MakeEngine(FlatTerrain(groundZ));

        Vector3 from = new(50f, 50f, groundZ);
        Vector3 to   = new(62f, 50f, groundZ);  // 12 units → 40 steps at r=0.3 (>30 cap)

        var normal = MakeTransition(from, to, sphereRadius: 0.3f);
        bool normalOk = normal.FindTransitionalPosition(engine);
        Assert.True(normalOk);
        Assert.True(normal.SpherePath.CurPos.X > from.X);

        var viewer = MakeTransition(from, to, sphereRadius: 0.3f);
        viewer.ObjectInfo.State |= ObjectInfoState.IsViewer;
        bool viewerOk = viewer.FindTransitionalPosition(engine);
        Assert.True(viewerOk);
        Assert.True(viewer.SpherePath.CurPos.X > from.X,
            "Viewer sphere should have advanced in +X through the full sweep");
    }

    [Fact]
    public void SampleTerrainZ_FindsCorrectLandblock()
    {
        // Ensure SampleTerrainZ dispatches to the right landblock.
        var engine = new PhysicsEngine();

        var terrain1 = FlatTerrain(10f);
        var terrain2 = FlatTerrain(20f);

        engine.AddLandblock(0xAAAA0000u, terrain1,
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        engine.AddLandblock(0xAAAB0000u, terrain2,
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 192f, worldOffsetY: 0f);

        float? z1 = engine.SampleTerrainZ(96f,  96f);   // inside lb1
        float? z2 = engine.SampleTerrainZ(288f, 96f);   // inside lb2

        Assert.NotNull(z1);
        Assert.NotNull(z2);
        Assert.Equal(10f, z1!.Value, precision: 0);
        Assert.Equal(20f, z2!.Value, precision: 0);
    }
}
