using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Navigation;

/// <summary>
/// A region's geometry takes the collision of the objects the server placed in it that
/// a body meets, so their tops are floor a walk stands on and a jump lands on, and it
/// remembers what those objects came to, so a grid is known to be out of date once one
/// arrives, leaves or moves.
/// </summary>
public class NavGeometryObjectCaptureTests
{
    private const uint LandblockId = 0xA9B4FFFFu;
    private const uint Rock = 0x8000_2AAEu;
    private const uint Post = 0xCA9B_4061u;
    private const uint Bolt = 0x8000_2AAFu;
    private static readonly NavBody Body = NavBody.Player(0.6f, 1.5f);

    [Fact]
    public void AnObjectTheServerPlacedBecomesFloorOnTopAndWallBelow()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterRock(engine, new Vector3(30f, 30f, 0f), radius: 1.2f, height: 1.5f);

        NavGeometry geometry = NavGeometry.Capture(engine, 0f, 0f, 64f, standsOn: Standable)!;
        NavGrid grid = NavGrid.Build(geometry, Body);

        Assert.Contains(Rock, geometry.ObjectIds);
        Assert.NotEqual(0uL, geometry.ObjectFingerprint);
        int top = grid.FindNode(new Vector3(30f, 30f, 51.5f), 0.5f, 0.3f);
        Assert.True(top >= 0, "no node stands on the object the server placed");
        Assert.InRange(grid.Position(top).Z, 51.4f, 51.6f);
    }

    [Fact]
    public void AnObjectIsLeftOutWhereNothingSaysABodyMeetsIt()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterRock(engine, new Vector3(30f, 30f, 0f), radius: 1.2f, height: 1.5f);

        NavGeometry geometry = NavGeometry.Capture(engine, 0f, 0f, 64f)!;
        NavGrid grid = NavGrid.Build(geometry, Body);

        Assert.Empty(geometry.ObjectIds);
        Assert.Equal(0uL, geometry.ObjectFingerprint);
        Assert.True(
            grid.FindNode(new Vector3(30f, 30f, 51.5f), 0.5f, 0.3f) < 0,
            "a node stands on an object the grid was not given");
    }

    [Fact]
    public void WhatTheObjectsCameToChangesAsOneArrivesAndMoves()
    {
        PhysicsEngine engine = FlatWorld();
        ulong empty = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Standable);

        RegisterRock(engine, new Vector3(30f, 30f, 0f), radius: 1.2f, height: 1.5f);
        ulong arrived = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Standable);

        RegisterRock(engine, new Vector3(34f, 30f, 0f), radius: 1.2f, height: 1.5f);
        ulong moved = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Standable);

        Assert.Equal(0uL, empty);
        Assert.NotEqual(empty, arrived);
        Assert.NotEqual(arrived, moved);
        Assert.Equal(0uL, NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, standsOn: null));
    }

    /// <summary>
    /// A grid must not be out of date the moment it is built, or every plan rebuilds it
    /// and gives up. What the capture recorded and what the world holds now have to
    /// weigh the same objects, landblock-owned ones included.
    /// </summary>
    [Fact]
    public void AFreshCaptureIsNotAlreadyOutOfDate()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterRock(engine, new Vector3(30f, 30f, 0f), radius: 1.2f, height: 1.5f);
        engine.ShadowObjects.Register(
            Post,
            gfxObjId: 0u,
            new Vector3(26f, 30f, 50f),
            Quaternion.Identity,
            0.2f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 2.2f,
            seedCellId: (LandblockId & 0xFFFF0000u) | 0x0021u,
            isStatic: true);

        NavGeometry geometry = NavGeometry.Capture(engine, 0f, 0f, 64f, standsOn: Anything)!;

        Assert.Equal(
            geometry.ObjectFingerprint,
            NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Anything));
        Assert.NotEqual(0uL, geometry.ObjectFingerprint);
    }

    /// <summary>
    /// A spell's bolt, an arrow or a thrown blade crosses a dungeon every frame it is
    /// in flight. None of it is floor or wall, and none of it may count towards what
    /// the objects here came to: a grid that counted one would be out of date the
    /// moment it was built, and the client would do nothing but build grids while
    /// anything was being cast.
    /// </summary>
    [Fact]
    public void AMissileInFlightIsNeitherFloorNorAReasonToBuildAgain()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterRock(engine, new Vector3(30f, 30f, 0f), radius: 1.2f, height: 1.5f);
        ulong still = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Anything);

        RegisterBolt(engine, new Vector3(20f, 30f, 51f));
        NavGeometry geometry = NavGeometry.Capture(engine, 0f, 0f, 64f, standsOn: Anything)!;
        ulong cast = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Anything);

        RegisterBolt(engine, new Vector3(24f, 30f, 51f));
        ulong flying = NavGeometry.FingerprintObjects(engine, 0f, 0f, 64f, Anything);

        Assert.Equal(still, cast);
        Assert.Equal(still, flying);
        Assert.Equal(geometry.ObjectFingerprint, flying);
        Assert.DoesNotContain(Bolt, geometry.ObjectIds);
        NavGrid grid = NavGrid.Build(geometry, Body);
        Assert.True(
            grid.FindNode(new Vector3(20f, 30f, 51.5f), 0.5f, 0.3f) < 0,
            "a node stands on a missile in flight");
    }

    private static bool Anything(uint entityId) => true;

    private static bool Standable(uint entityId) => entityId == Rock;

    private static void RegisterBolt(PhysicsEngine engine, Vector3 at) =>
        engine.ShadowObjects.Register(
            Bolt,
            gfxObjId: 0u,
            at,
            Quaternion.Identity,
            0.1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 0.2f,
            state: (uint)(PhysicsStateFlags.Missile | PhysicsStateFlags.ReportCollisions),
            isStatic: false);

    private static void RegisterRock(
        PhysicsEngine engine,
        Vector3 at,
        float radius,
        float height) =>
        engine.ShadowObjects.Register(
            Rock,
            gfxObjId: 0u,
            at with { Z = at.Z + 50f },
            Quaternion.Identity,
            radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: height,
            isStatic: false);

    private static PhysicsEngine FlatWorld()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index * 1f;
        var terrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(
            LandblockId,
            terrain,
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }
}
