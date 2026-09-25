using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// The room check a summoner makes before calling a pet: can a body be set
/// down three metres ahead, on flat ground, against a wall, beside a
/// creature, on a step, and off the edge of what is loaded.
/// </summary>
public sealed class PlacementRoomProbeTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;
    private const uint Self = 0x50000001u;
    private const uint Wall = 0x80000010u;
    private const uint Monster = 0x80000011u;
    private const float Radius = 0.48f;
    private const float Height = 1.835f;

    private static readonly PlacementRoomBody Body = new(
        ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, Radius), Radius),
            new FlatCollisionSphere(new Vector3(0f, 0f, Height - Radius), Radius)),
        Scale: 1f,
        StepUpHeight: 0.4f,
        StepDownHeight: 0.4f,
        SelfEntityId: Self);

    /// <summary>Facing north, the heading a fresh orientation has.</summary>
    private static readonly Quaternion North = Quaternion.Identity;

    /// <summary>Facing east: a quarter turn clockwise seen from above.</summary>
    private static readonly Quaternion East =
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f);

    private static readonly Vector3 Feet = new(96f, 96f, 0f);

    [Fact]
    public void OpenGroundAheadIsClearAndTheBodyStandsThreeMetresOut()
    {
        PhysicsEngine engine = FlatWorld();

        PlacementRoomResult result = Check(engine, North, 3f);

        Assert.Equal(PlacementRoom.Clear, result.Room);
        Assert.Equal(Landblock, result.CellId & 0xFFFF0000u);
        Assert.Equal(96f, result.Position.X, 2);
        Assert.Equal(99f, result.Position.Y, 2);
        // Set down a hand's width up and settled onto the ground, not left
        // hanging or sunk into it.
        Assert.InRange(result.Position.Z, 0f, 0.11f);
    }

    [Fact]
    public void AWallWhereTheBodyWouldStandBlocksIt()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterCylinder(engine, Wall, new Vector3(96f, 99f, 0f), 1.5f, 4f, EntityCollisionFlags.None);

        Assert.Equal(PlacementRoom.Blocked, Check(engine, North, 3f).Room);
    }

    [Fact]
    public void TheCheckLooksWhereTheBodyFacesNotElsewhere()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterCylinder(engine, Wall, new Vector3(96f, 99f, 0f), 1.5f, 4f, EntityCollisionFlags.None);

        PlacementRoomResult result = Check(engine, East, 3f);

        Assert.Equal(PlacementRoom.Clear, result.Room);
        Assert.Equal(99f, result.Position.X, 2);
        Assert.Equal(96f, result.Position.Y, 2);
    }

    [Fact]
    public void ACreatureStandingThereDoesNotCount()
    {
        PhysicsEngine engine = FlatWorld();
        RegisterCylinder(engine, Monster, new Vector3(96f, 99f, 0f), 0.6f, 1.8f, EntityCollisionFlags.IsCreature);

        Assert.Equal(PlacementRoom.Clear, Check(engine, North, 3f).Room);
    }

    [Fact]
    public void GroundThatRisesAheadIsClimbedOnto()
    {
        PhysicsEngine engine = FlatWorld();
        // A broad step half a metre high: the spot a hand's width above the
        // feet is inside it, so the check climbs until the body fits on top.
        RegisterCylinder(engine, Wall, new Vector3(96f, 99f, 0f), 2f, 0.5f, EntityCollisionFlags.None);

        PlacementRoomResult result = Check(engine, North, 3f);

        Assert.Equal(PlacementRoom.Clear, result.Room);
        Assert.InRange(result.Position.Z, 0.49f, 0.61f);
    }

    [Fact]
    public void GroundThatFallsAwayUnderALowRoofIsSteppedDownTo()
    {
        PhysicsEngine engine = FlatWorld();
        // The character stands half a metre up; ahead the ground is lower and
        // a roof hangs 2.2 m over it, so the body only fits set down lower.
        var onALedge = Feet with { Z = 0.5f };
        RegisterCylinder(engine, Wall, new Vector3(96f, 99f, 2.2f), 2f, 3f, EntityCollisionFlags.None);

        PlacementRoomResult result = PlacementRoomProbe.Check(
            engine, Body, onALedge, North, Cell, onALedge, 3f);

        Assert.Equal(PlacementRoom.Clear, result.Room);
        Assert.InRange(result.Position.Z, 0f, 0.3f);
    }

    [Fact]
    public void ASpotBeyondWhatIsLoadedIsUnknown()
    {
        PhysicsEngine engine = FlatWorld();
        var nearTheEdge = new Vector3(191f, 96f, 0f);

        PlacementRoomResult result = PlacementRoomProbe.Check(
            engine, Body, nearTheEdge, East, Cell, nearTheEdge, 3f);

        Assert.Equal(PlacementRoom.Unknown, result.Room);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(10.5f)]
    [InlineData(float.NaN)]
    public void ADistanceOutsideTheRangeIsUnknown(float distance)
    {
        PhysicsEngine engine = FlatWorld();

        Assert.Equal(PlacementRoom.Unknown, Check(engine, North, distance).Room);
    }

    [Fact]
    public void ABodyWithNoShapeIsUnknown()
    {
        PhysicsEngine engine = FlatWorld();

        PlacementRoomResult result = PlacementRoomProbe.Check(
            engine,
            Body with { Spheres = ImmutableArray<FlatCollisionSphere>.Empty },
            Feet,
            North,
            Cell,
            Feet,
            3f);

        Assert.Equal(PlacementRoom.Unknown, result.Room);
    }

    private static PlacementRoomResult Check(PhysicsEngine engine, Quaternion facing, float distance) =>
        PlacementRoomProbe.Check(engine, Body, Feet, facing, Cell, Feet, distance);

    private static PhysicsEngine FlatWorld()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock | 0xFFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static void RegisterCylinder(
        PhysicsEngine engine,
        uint id,
        Vector3 at,
        float radius,
        float height,
        EntityCollisionFlags flags) =>
        engine.ShadowObjects.Register(
            id,
            gfxObjId: 0u,
            worldPos: at,
            rotation: Quaternion.Identity,
            radius: radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock | 0xFFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: height,
            flags: flags,
            seedCellId: Cell,
            isStatic: false);
}
