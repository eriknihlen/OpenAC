using System.Numerics;
using AcDream.Runtime.Navigation;
using AcDream.Core.Items;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Navigation;

public sealed class RuntimeNavigationGoalSourceTests
{
    /// <summary>A place given no height stands on the terrain under it, and at the fallback height where none is loaded.</summary>
    [Fact]
    public void APlaceWithNoHeightStandsOnTheTerrainUnderIt()
    {
        var heights = new float[256];
        heights[0] = 5f;
        var physics = new AcDream.Core.Physics.PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new AcDream.Core.Physics.TerrainSurface(new byte[81], heights),
            [],
            [],
            0f,
            0f);

        Vector3 loaded = RuntimeNavigationGoalSource.OnTheGround(physics, new Vector3(60f, 70f, float.NaN), fallbackZ: 90f);
        Vector3 unloaded = RuntimeNavigationGoalSource.OnTheGround(physics, new Vector3(400f, 70f, float.NaN), fallbackZ: 90f);
        Vector3 given = RuntimeNavigationGoalSource.OnTheGround(physics, new Vector3(60f, 70f, 12f), fallbackZ: 90f);

        Assert.Equal(5f, loaded.Z, 3);
        Assert.Equal(90f, unloaded.Z);
        Assert.Equal(12f, given.Z);
    }

    [Fact]
    public void ARouteKeepsOutOfAnObjectThatStandsStill() =>
        Assert.True(RuntimeNavigationGoalSource.StandsInTheWay(Item((ItemType)0, (PublicWeenieFlags)0)));

    [Theory]
    [InlineData((PublicWeenieFlags)0)]
    [InlineData(PublicWeenieFlags.Vendor)]
    public void ARouteKeepsOutOfACreatureThatCannotBeAttackedAndFollowsNoOneSuchAsAVendor(PublicWeenieFlags flags)
    {
        ClientObject vendor = Item(ItemType.Creature, flags);

        Assert.False(RuntimeNavigationGoalSource.Moves(vendor));
        Assert.True(RuntimeNavigationGoalSource.StandsInTheWay(vendor));
    }

    [Theory]
    [InlineData(PublicWeenieFlags.Attackable, 0u)]
    [InlineData((PublicWeenieFlags)0, 0x5000_0001u)]
    [InlineData(PublicWeenieFlags.Player, 0u)]
    public void PlayersAndCreaturesThatCanBeAttackedOrFollowAnOwnerMove(PublicWeenieFlags flags, uint petOwner)
    {
        ClientObject mover = Item(ItemType.Creature, flags, petOwner);

        Assert.True(RuntimeNavigationGoalSource.Moves(mover));
        Assert.False(RuntimeNavigationGoalSource.StandsInTheWay(mover));
    }

    /// <summary>
    /// A grid takes the collision of anything that stands where it is and can be met
    /// from any side, so a route can walk around it and a jump can land on top of it.
    /// </summary>
    [Fact]
    public void AGridTakesTheCollisionOfAnObjectThatStandsStill() =>
        Assert.True(RuntimeNavigationGoalSource.CanBeStoodOn(Item((ItemType)0, (PublicWeenieFlags)0)));

    /// <summary>A creature holds its ground, but no body stands on one.</summary>
    [Theory]
    [InlineData((PublicWeenieFlags)0)]
    [InlineData(PublicWeenieFlags.Vendor)]
    public void AGridLeavesCreaturesToBeWalkedAround(PublicWeenieFlags flags)
    {
        ClientObject vendor = Item(ItemType.Creature, flags);

        Assert.True(RuntimeNavigationGoalSource.StandsInTheWay(vendor));
        Assert.False(RuntimeNavigationGoalSource.CanBeStoodOn(vendor));
    }

    [Theory]
    [InlineData(PublicWeenieFlags.Door)]
    [InlineData(PublicWeenieFlags.Corpse)]
    [InlineData(PublicWeenieFlags.Player)]
    public void AGridLeavesOutWhatComesAndGoesOrMoves(PublicWeenieFlags flags) =>
        Assert.False(RuntimeNavigationGoalSource.CanBeStoodOn(Item((ItemType)0, flags)));

    [Theory]
    [InlineData(PublicWeenieFlags.Door)]
    [InlineData(PublicWeenieFlags.Corpse)]
    public void ARouteDoesNotKeepOutOfDoorsOrCorpses(PublicWeenieFlags flags) =>
        Assert.False(RuntimeNavigationGoalSource.StandsInTheWay(Item((ItemType)0, flags)));

    [Theory]
    [InlineData(ItemType.Portal, (PublicWeenieFlags)0)]
    [InlineData((ItemType)0, PublicWeenieFlags.Portal)]
    public void APortalIsKnownByItsTypeOrItsFlag(ItemType type, PublicWeenieFlags flags) =>
        Assert.True(RuntimeNavigationGoalSource.IsPortal(Item(type, flags)));

    [Fact]
    public void AnOrdinaryObjectIsNoPortal() =>
        Assert.False(RuntimeNavigationGoalSource.IsPortal(Item((ItemType)0, PublicWeenieFlags.Door)));

    /// <summary>A portal whose physics states no shape is still kept out of by a body's width and more.</summary>
    [Fact]
    public void APortalWithNoShapeIsKeptOutOfAllTheSame() =>
        Assert.Equal(
            RuntimeNavigationGoalSource.PortalLeastRadius,
            RuntimeNavigationGoalSource.PortalFootprint(new NavAvoidance(System.Numerics.Vector3.Zero, 0f)).Radius);

    /// <summary>
    /// Travel walks to a spot a couple of metres from its portal, so a portal that near
    /// the goal is the one being gone to, and one across the square is not.
    /// </summary>
    [Theory]
    [InlineData(2f, true)]
    [InlineData(4f, true)]
    [InlineData(8f, false)]
    public void APortalAtTheWalksEndIsTheOneItGoesTo(float away, bool goesTo) =>
        Assert.Equal(
            goesTo,
            RuntimeNavigationGoalSource.IsWhereTheWalkEnds(
                new NavAvoidance(new System.Numerics.Vector3(away, 0f, 0f), 1.5f),
                System.Numerics.Vector3.Zero));

    /// <summary>
    /// Two portals near the walk's end, as in the Town Network: only the nearer is the
    /// one being gone to, and the other is still kept out of.
    /// </summary>
    [Fact]
    public void OnlyThePortalNearestTheWalksEndIsLeftOut()
    {
        var going = new NavAvoidance(new System.Numerics.Vector3(1f, 0f, 0f), 1.5f);
        var neighbour = new NavAvoidance(new System.Numerics.Vector3(3.5f, 0f, 0f), 1.5f);

        IReadOnlyList<NavAvoidance> kept = RuntimeNavigationGoalSource.ExceptTheOneTheWalkEnds(
            [neighbour, going],
            System.Numerics.Vector3.Zero);

        Assert.Equal([neighbour], kept);
    }

    /// <summary>
    /// A missile in flight stands nowhere, so it is none of the three things a walk asks about
    /// objects: not a wall the grid holds, not something a route keeps out of, and not what
    /// stopped a walk that stalled. Each used to ask in its own words, and the one that names a
    /// stalled walk's blocker never asked at all, so an arrow passing a stuck body was named as
    /// the thing in its way and kept out of for the rest of the walk.
    /// </summary>
    [Fact]
    public void AMissileInFlightStandsNowhere()
    {
        Assert.False(RuntimeNavigationGoalSource.StandsAnywhere(PhysicsStateFlags.Missile));
        Assert.False(RuntimeNavigationGoalSource.StandsAnywhere(PhysicsStateFlags.Missile | PhysicsStateFlags.Ethereal));
        Assert.True(RuntimeNavigationGoalSource.StandsAnywhere((PhysicsStateFlags)0));
        Assert.True(RuntimeNavigationGoalSource.StandsAnywhere(PhysicsStateFlags.Ethereal));
    }

    private static ClientObject Item(ItemType type, PublicWeenieFlags flags, uint petOwner = 0u) => new()
    {
        ObjectId = 0x7000_0001u,
        Name = "Object",
        Type = type,
        PublicWeenieBitfield = (uint)flags,
        PetOwnerId = petOwner,
    };
}
