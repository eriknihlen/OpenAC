using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Numerics;
using AcDream.Runtime.Navigation;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Navigation;

public sealed class NavigationWalkControllerTests
{
    private const float Frame = 1f / 30f;
    private const uint Target = 0x50000001u;
    private const uint Other = 0x50000002u;
    private const uint Door = 0x7A000001u;

    /// <summary>How near a creature's middle the simulated body's middle can come, for a creature 0.8 m across its footprint's radius.</summary>
    private static readonly float Reach = 0.8f + NavBody.Player(0.6f, 1.5f).Radius;

    [Fact]
    public void AWalkIsPlannedWalkedToItsGoalAndEndsFacingIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        long sequence = walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);
        body.Integrate(3f);

        Assert.Equal(sequence, report.Sequence);
        Assert.Equal(NavigationWalkState.Arrived, report.State);
        float left = Vector2.Distance(body.Flat, new Vector2(60f, 75f));
        Assert.InRange(left, 0f, NavigationWalkController.DefaultArrivalMeters + 0.05f);
        Assert.Equal(left, report.RemainingMeters, 1);
        Assert.False(body.Travelling);
        Assert.InRange(MathF.Abs(body.HeadingErrorTo(new Vector2(60f, 75f))), 0f, 10.5f);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    public void AWalkEndsWithinTheDistanceItWasAskedToArriveWithin(float arrivalMeters)
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        walk.WalkTo(Target, arrivalMeters);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(report.RemainingMeters, 0f, arrivalMeters + 0.05f);
    }

    [Fact]
    public void AWalkAskedForInTheAirWaitsHoweverLongTheCharacterIsAloftAndPlansFromWhereItLands()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 3f)) { Airborne = true };
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        walk.WalkTo(Target);
        TickFor(walk, 12f);

        Assert.Equal(NavigationWalkState.Planning, walk.Report.State);
        Assert.Equal("waiting for the character to land", walk.Report.Reason);
        Assert.Null(walk.Route);

        body.Place(new Vector3(40f, 40f, 0f));
        body.Airborne = false;
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
    }

    [Fact]
    public void AWalkWaitsWhileSomethingElseNeedsTheCharacterAndGoesOnFromWhereItWasLeft()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        string? need = null;
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            PausedBy = () => need,
        };

        walk.WalkTo(Target);
        RunUntil(walk, body, _ => body.Position.Y > 60f);
        need = "MossTank is running Attack";
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal("waiting: MossTank is running Attack", walk.Report.Reason);
        Assert.False(body.Travelling);
        body.Place(new Vector3(55f, 70f, 0f));
        Run(walk, body, 10f);
        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal(new Vector2(55f, 70f), body.Flat);

        need = null;
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.InRange(
            Vector2.Distance(body.Flat, new Vector2(40f, 150f)),
            0f,
            NavigationWalkController.DefaultArrivalMeters + 0.05f);
    }

    /// <summary>
    /// Once every landblock a walk's grid was built over has unloaded, as when the character
    /// portals out of a dungeon, the grid is let go rather than kept until the next walk.
    /// </summary>
    [Fact]
    public void AGridOverLandblocksThatHaveAllUnloadedIsLetGo()
    {
        PhysicsEngine physics = FlatWorld(landblocksNorth: 2);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(physics, body, new Goals { [Target] = new Vector3(40f, 180f, 0f) });
        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        NavGrid grid = Assert.IsType<NavGrid>(walk.Grid);
        Assert.Equal(2, grid.LandblockIds.Count);

        physics.RemoveLandblock(0xA9B5FFFFu);
        walk.Tick(Frame);
        Assert.Same(grid, walk.Grid);

        physics.RemoveLandblock(0xA9B4FFFFu);
        walk.Tick(Frame);
        Assert.Null(walk.Grid);
    }

    /// <summary>A sealed dungeon's grid is let go once the dungeon's landblock has unloaded, as when the character portals out.</summary>
    [Fact]
    public void ADungeonsGridIsLetGoOnceTheDungeonHasUnloaded()
    {
        const uint room = 0xA9B40100u;
        PhysicsEngine physics = DungeonWorld(room);
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var walk = new NavigationWalkController(
            physics,
            body,
            new Goals { [Target] = new Vector3(240f, 32f, -30f) },
            isSealedDungeon: cell => cell == room);
        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        Assert.NotNull(walk.Grid);

        physics.RemoveLandblock(0xA9B4FFFFu);
        walk.Tick(Frame);

        Assert.Null(walk.Grid);
    }

    /// <summary>
    /// Whoever listens to a walk's narration hears every line the walk reports, and on top of
    /// them where it started and headed and the points of the route it found.
    /// </summary>
    [Fact]
    public void AWalksNarrationCarriesItsReportsAndWhereItStartsHeadsAndGoes()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { CellId = 0xA9B40021u };
        var said = new List<string>();
        var narrated = new List<string>();
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) }, said.Add)
        {
            Narration = narrated.Add,
        };

        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);

        Assert.NotEmpty(said);
        Assert.Equal(said, narrated.Where(line => said.Contains(line)));
        Assert.Contains(narrated, line => line.Contains("from (40.0, 40.0, 0.0) in cell 0xA9B40021 toward (40.0, 150.0, 0.0), within 2.5 m"));
        Assert.Contains(narrated, line => line.StartsWith("Route points: (", StringComparison.Ordinal) && line.Contains("ends in sight of the goal"));
        Assert.DoesNotContain(said, line => line.StartsWith("Route points:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Narration reads the same on a machine whose culture writes decimals with a comma: the
    /// numbers it carries are for the log and the tests that read it, not for the locale.
    /// </summary>
    [Fact]
    public void NarrationWritesItsNumbersTheSameUnderACommaDecimalCulture()
    {
        using var _ = new CultureScope("sv-SE");
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { CellId = 0xA9B40021u };
        var narrated = new List<string>();
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            Narration = narrated.Add,
        };

        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);

        Assert.Contains(narrated, line => line.Contains("toward (40.0, 150.0, 0.0), within 2.5 m", StringComparison.Ordinal));
        Assert.Contains(narrated, line => line.StartsWith("Route: ", StringComparison.Ordinal) && Regex.IsMatch(line, @" legs, \d+\.\d m, "));
        Assert.DoesNotContain(narrated, line => Regex.IsMatch(line, @"\d,\d"));
    }

    /// <summary>
    /// A goal walled off from everywhere the character can reach is walked to its nearest spot,
    /// and the walk says it ended without a line of sight rather than that it arrived.
    /// </summary>
    [Fact]
    public void AWalkThatCanOnlyEndOutOfSightOfItsGoalDoesNotSayItArrived()
    {
        var body = new SimulatedBody(new Vector3(10f, 4f, -30f));
        var walk = new NavigationWalkController(RoomWithDoorways(), body, new Goals { [Target] = new Vector3(10f, 16f, -30f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.ArrivedWithoutSight, report.State);
        Assert.Contains("without a line of sight", report.Reason);
        Assert.True(body.Position.Y < 10f);
    }

    /// <summary>
    /// The player's own movement ends a walk whatever it is doing, not only while it drives
    /// the character: here while it still plans, before any move has begun.
    /// </summary>
    [Fact]
    public void ThePlayerMovingEndsAWalkThatIsStillPlanning()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });

        walk.WalkTo(Target);
        walk.Tick(Frame);
        Assert.Equal(NavigationWalkState.Planning, walk.Report.State);
        body.PlayerMovementInputFrames++;
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.False(walk.IsBusy);
        Run(walk, body, 5f);
        Assert.Equal(0, body.MovesBegun);
    }

    /// <summary>While a walk waits for something else that needs the character, the player moving still ends it.</summary>
    [Fact]
    public void ThePlayerMovingEndsAWalkThatIsWaiting()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        string? need = null;
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            PausedBy = () => need,
        };
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        need = "MossTank is running Attack";
        Run(walk, body, 1f);
        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        int moves = body.MovesBegun;

        body.PlayerMovementInputFrames++;
        walk.Tick(Frame);
        need = null;
        Run(walk, body, 5f);

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.Equal(moves, body.MovesBegun);
    }

    /// <summary>Input the player gave before a walk was asked for does not end it.</summary>
    [Fact]
    public void ThePlayerHavingMovedBeforeAWalkBeganDoesNotEndIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { PlayerMovementInputFrames = 40 };
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });

        walk.WalkTo(Target);

        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
    }

    [Fact]
    public void AWalkSetsOffAgainOnlyOnceNothingHasNeededTheCharacterForAMoment()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        string? need = null;
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            PausedBy = () => need,
        };
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        need = "MossTank is running Attack";
        Run(walk, body, 1f);
        int moves = body.MovesBegun;

        for (int gap = 0; gap < 5; gap++)
        {
            need = null;
            Run(walk, body, (float)NavigationWalkController.PauseSettleSeconds * 0.5f);
            need = "MossTank is running LootCorpseIdle";
            Run(walk, body, 0.2f);
        }

        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal("waiting: MossTank is running LootCorpseIdle", walk.Report.Reason);
        Assert.Equal(moves, body.MovesBegun);
        need = null;
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
    }

    [Fact]
    public void ARouteAskedForAloneIsFoundWhileSomethingElseNeedsTheCharacter()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) })
        {
            PausedBy = () => "MossTank is running Attack",
        };

        walk.RouteTo(Target);

        Assert.Equal(NavigationWalkState.Planned, RunUntilSettled(walk, body).State);
    }

    [Fact]
    public void APlaceKnownOnlyByItsMapCoordinatesLiesWhereItsCellPlacesIt()
    {
        var here = new Vector3(15.259f, -39.938f, 0.005f);
        Vector3 byCell = RuntimeNavigationGoalSource.PlaceOffset(0x019E0114u, new Vector3(10f, -40f, 0.005f), 0x019E0123u, here);
        Vector3 byMap = RuntimeNavigationGoalSource.PlaceOffset(0u, new Vector3(202f, 30296f, 0.005f), 0x019E0123u, here);

        Assert.Equal(-5.259d, byCell.X, 3);
        Assert.Equal(-0.062d, byCell.Y, 3);
        Assert.Equal(byCell.X, byMap.X, 2);
        Assert.Equal(byCell.Y, byMap.Y, 2);
        Assert.Equal(byCell.Z, byMap.Z, 3);
    }

    /// <summary>
    /// A grid is built before the server has placed everything around the character, as a
    /// dungeon's is on arrival. An object placed after it is floor the next walk stands on.
    /// </summary>
    [Fact]
    public void AnObjectPlacedAfterTheGridWasBuiltIsFloorTheNextWalkStandsOn()
    {
        const uint crate = 0x80002AB0u;
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        PhysicsEngine world = FlatWorld();
        var goals = new Goals();
        var walk = new NavigationWalkController(world, body, goals);
        walk.WalkToPlace(0xA9B40001u, new Vector3(45f, 40f, 0f), 1f);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);

        world.ShadowObjects.Register(
            crate,
            gfxObjId: 0u,
            new Vector3(60f, 40f, 0f),
            Quaternion.Identity,
            2f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 0.5f,
            seedCellId: 0xA9B40001u,
            isStatic: false);
        goals.Standing.Add(crate);
        walk.WalkToPlace(0xA9B40001u, new Vector3(60f, 40f, 0.5f), 1f);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0.5f, walk.Route!.Legs[^1].Z, 1);
    }

    [Fact]
    public void AWalkToAPlaceArrivesThereWithoutTurningToFaceIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WalkToPlace(0xA9B40001u, new Vector3(60f, 75f, 0f), 2f);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0u, report.ObjectId);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(60f, 75f)), 0f, 2.05f);
        Assert.False(body.Travelling);
    }

    /// <summary>
    /// A walk to a place ends on the floor that place stands on. A slab too high to step onto,
    /// with no leap to reach it, is not arrived at from the ground beside it; a walk to an
    /// object standing on the same slab still ends beside it, as walks to objects do.
    /// </summary>
    [Fact]
    public void AWalkToAPlaceOnATopItCannotGetOntoFindsNoRouteWhereAWalkToAnObjectThereArrivesBeside()
    {
        var goal = new Vector3(52f, 52f, 1f);
        var placeBody = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var placeWalk = new NavigationWalkController(RaisedSlabWorld(), placeBody, new Goals());
        placeWalk.WalkToPlace(0xA9B40001u, goal, 2.5f);
        NavigationWalkReport place = RunUntilSettled(placeWalk, placeBody);

        var objectBody = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var objectWalk = new NavigationWalkController(RaisedSlabWorld(), objectBody, new Goals { [Target] = goal });
        objectWalk.WalkTo(Target);
        NavigationWalkReport onObject = RunUntilSettled(objectWalk, objectBody);

        Assert.Equal(NavigationWalkState.NoRoute, place.State);
        Assert.Contains("floor", place.Reason);
        Assert.Equal(NavigationWalkState.Arrived, onObject.State);
    }

    /// <summary>
    /// Standing on an object ends on its top. A slab too high to step onto, with no leap to
    /// reach it, is not stood on from the ground beside it, where a walk to it arrives.
    /// </summary>
    [Fact]
    public void StandingOnATopItCannotGetOntoFindsNoRouteWhereAWalkToItArrivesBeside()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(52f, 52f, 0f) };
        goals.Surfaces[Target] = new NavSurfaces(SlabTop, []);
        var walk = new NavigationWalkController(RaisedSlabWorld(), body, goals);

        walk.StandOn(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("top of the object", report.Reason);
        Assert.Equal(0, body.MovesBegun);
    }

    /// <summary>An object with a ramp up to its deck is stood on at the deck, not partway up the ramp.</summary>
    [Fact]
    public void StandingOnAnObjectWithARampWalksUpItAndEndsOnTheDeck()
    {
        var body = new SimulatedBody(new Vector3(52f, 36f, 0f));
        var goals = new Goals { [Target] = new Vector3(52f, 53f, 0f) };
        goals.Surfaces[Target] = new NavSurfaces([.. RampDeck.SelectMany(Triangles)], []);
        var walk = new NavigationWalkController(RampDeckWorld(), body, goals);

        walk.StandOn(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(2f, walk.Route!.Legs[^1].Z, 1);
        Assert.InRange(body.Position.Y, 51f, 56f);
        Assert.False(body.Travelling);
    }

    /// <summary>
    /// An object whose collision is in the grid, such as a life stone, hides its own middle, and
    /// a portal nearby leaves no fallback to end out of sight. The walk still arrives beside it.
    /// </summary>
    [Fact]
    public void AWalkToAnObjectWhoseCollisionHidesItsMiddleArrivesBesideIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(52f, 52f, 0f) };
        goals.Surfaces[Target] = new NavSurfaces([.. StoneBlock.SelectMany(Triangles)], []);
        goals.Portals.Add(new NavAvoidance(new Vector3(40f, 60f, 0f), 1f));
        var walk = new NavigationWalkController(StoneWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(52f, 52f)), 1.5f, 5f);
    }

    /// <summary>A stone 2 m across and 3 m tall standing on the ground, as the polygons of one cell.</summary>
    private static readonly Vector3[][] StoneBlock =
    [
        [new(51f, 51f, 3f), new(53f, 51f, 3f), new(53f, 53f, 3f), new(51f, 53f, 3f)],
        [new(51f, 51f, 0f), new(53f, 51f, 0f), new(53f, 51f, 3f), new(51f, 51f, 3f)],
        [new(53f, 51f, 0f), new(53f, 53f, 0f), new(53f, 53f, 3f), new(53f, 51f, 3f)],
        [new(53f, 53f, 0f), new(51f, 53f, 0f), new(51f, 53f, 3f), new(53f, 53f, 3f)],
        [new(51f, 53f, 0f), new(51f, 51f, 0f), new(51f, 51f, 3f), new(51f, 53f, 3f)],
    ];

    private static PhysicsEngine StoneWorld() => CellWorld(StoneBlock);

    /// <summary>
    /// A debugging listener hears what a walk found of its object's collision, what it searched
    /// for, and for a walk that found no route, what stood around the goal.
    /// </summary>
    [Fact]
    public void ANarratedWalkThatFindsNoRouteSaysWhatItLookedAt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(52f, 52f, 0f) };
        goals.Surfaces[Target] = new NavSurfaces(SlabTop, []);
        var walk = new NavigationWalkController(RaisedSlabWorld(), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.StandOn(Target);
        RunUntilSettled(walk, body);

        Assert.Contains(heard, line => line.Contains("collision of 2 triangles and 0 cylinders", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.Contains(": searching for ", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.StartsWith("No route to 0x", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.StartsWith("Around the goal at ", StringComparison.Ordinal));
    }

    [Fact]
    public void FollowingSomethingThatIsNotAPlayerEndsAtOnce()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 70f, 0f) });

        walk.Follow(Target, 3f);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("only players", report.Reason);
        Assert.Equal(0, body.MovesBegun);
    }

    /// <summary>
    /// A follow walks up behind the player, holds there without ending, and walks on each
    /// time the player moves away.
    /// </summary>
    [Fact]
    public void FollowingAPlayerKeepsBehindThemAndGoesOnWhenTheyMove()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Run(walk, body, seconds: 2f);

        Assert.True(walk.IsBusy);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 70f)), 0f, 4.5f);

        goals[Target] = new Vector3(40f, 130f, 0f);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking && report.Reason == "walking");
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));

        Assert.True(walk.IsBusy);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 130f)), 0f, 4.5f);
        Assert.True(body.Flat.Y < 130f, "the follower stays on the side it came from, behind the player");
    }

    /// <summary>A player walking on while the follower is on its way is chased without stopping to plan again.</summary>
    [Fact]
    public void FollowingAPlayerWhoKeepsMovingReplansOnTheWay()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 90f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking && report.Reason == "walking");
        Run(walk, body, seconds: 1f);
        goals[Target] = new Vector3(70f, 100f, 0f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));

        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(70f, 100f)), 0f, 4.5f);
        Assert.Contains(heard, line => line.Contains("the player moved", StringComparison.Ordinal));
    }

    [Fact]
    public void FollowingAPlayerOutOfSightWaitsAndGoesOnOnceTheyAreSeen()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals();
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.Follow(Target, 3f);
        Run(walk, body, seconds: 3f);

        Assert.True(walk.IsBusy);
        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Contains("waiting to see", walk.Report.Reason);

        goals[Target] = new Vector3(40f, 70f, 0f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 70f)), 0f, 4.5f);
    }

    /// <summary>
    /// A player who vanishes beside a portal went through it: the follower walks into that
    /// portal, uses it, waits out portal space, and goes on following once it sees the player.
    /// </summary>
    [Fact]
    public void FollowingAPlayerGoesThroughThePortalTheyLeftBy()
    {
        const uint Portal = 0x7A000077u;
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f), [Portal] = new Vector3(40f, 74f, 0f) };
        goals.Players.Add(Target);
        goals.PortalIds[Portal] = new Vector3(40f, 74f, 0f);
        var doors = new UseRecorder();
        var walk = new NavigationWalkController(FlatWorld(), body, goals, doors: doors);

        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        goals.Remove(Target);
        RunUntil(walk, body, _ => doors.Used.Contains(Portal), seconds: 60f);

        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 74f)), 0f, 1.5f);

        body.InPortalSpace = true;
        Run(walk, body, seconds: 1f);
        Assert.True(walk.IsBusy);
        Assert.Contains("portal space", walk.Report.Reason);

        body.InPortalSpace = false;
        body.Place(new Vector3(100f, 100f, 0f));
        goals[Target] = new Vector3(100f, 130f, 0f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(100f, 130f)), 0f, 4.5f);
    }

    /// <summary>
    /// The client keeps a player who enters a portal and moves them to where it sent them, far
    /// away at once. The follower goes through the portal they entered rather than turning
    /// toward where they landed.
    /// </summary>
    [Fact]
    public void FollowingAPlayerWhoJumpsFarAtOnceGoesThroughThePortalTheyEntered()
    {
        const uint Portal = 0x7A000077u;
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f), [Portal] = new Vector3(40f, 74f, 0f) };
        goals.Players.Add(Target);
        goals.PortalIds[Portal] = new Vector3(40f, 74f, 0f);
        var doors = new UseRecorder();
        var walk = new NavigationWalkController(FlatWorld(landblocksNorth: 2), body, goals, doors: doors);

        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        goals[Target] = new Vector3(40f, 73.5f, 0f);
        Run(walk, body, seconds: 0.2f);
        goals[Target] = new Vector3(150f, 300f, 0f);
        RunUntil(walk, body, _ => doors.Used.Contains(Portal), seconds: 60f);

        Assert.Contains(Portal, doors.Used);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 74f)), 0f, 1.5f);
    }

    /// <summary>
    /// A player moved far away at once with no portal beside them, as by a recall or a teleport,
    /// is out of reach: the follow sleeps, walking nowhere, until they are within reach again.
    /// A short move is followed.
    /// </summary>
    [Fact]
    public void FollowingSleepsWhileThePlayerIsMovedFarAwayWithNoPortal()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));

        goals[Target] = new Vector3(40f, 120f, 0f);
        Run(walk, body, seconds: 1f);
        Assert.True(walk.IsBusy, "a player moved a short way at once is still followed");
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Vector2 before = body.Flat;

        goals[Target] = new Vector3(-600f, 4800f, 0f);
        Run(walk, body, seconds: 3f);

        Assert.True(walk.IsBusy);
        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Contains("out of reach", walk.Report.Reason);
        Assert.False(body.Travelling);
        Assert.InRange(Vector2.Distance(body.Flat, before), 0f, 1f);

        goals[Target] = new Vector3(60f, 140f, 0f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(60f, 140f)), 0f, 4.5f);
    }

    [Fact]
    public void FollowingAPlayerKeepsTryingWhenTheWayIsBlocked()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Stuck = true };
        var goals = new Goals { [Target] = new Vector3(40f, 150f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.Follow(Target, 3f);
        NavigationWalkReport report = RunUntil(walk, body, r => r.Reason.Contains("trying again", StringComparison.Ordinal), seconds: 200f);

        Assert.Contains("trying again", report.Reason);
        Run(walk, body, seconds: 3f);
        Assert.True(walk.IsBusy);
    }

    /// <summary>A follower holding behind the player goes on when the player climbs or drops, as onto a platform, even without moving across.</summary>
    [Fact]
    public void FollowingAPlayerGoesOnWhenTheyClimbWithoutMovingAcross()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;
        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Run(walk, body, seconds: 1f);
        heard.Clear();

        goals[Target] = new Vector3(40f, 70f, 2f);
        Run(walk, body, seconds: 3f);

        Assert.Contains(heard, line => line.Contains("the player moved to", StringComparison.Ordinal));
        Assert.True(walk.IsBusy);
    }

    /// <summary>
    /// A player with no floor under them is in the air, as mid-jump, so the follow waits for
    /// them to land before planning, rather than aiming at whatever top stands near the arc.
    /// </summary>
    [Fact]
    public void FollowingAPlayerInTheAirWaitsForThemToLand()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;
        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Run(walk, body, seconds: 1f);

        goals[Target] = new Vector3(40f, 90f, 1.5f);
        Run(walk, body, seconds: 0.5f);
        Assert.Contains("waiting for the player to land", walk.Report.Reason);
        Assert.DoesNotContain(heard, line => line.Contains("searching for (40.0, 90.0, 1.5)", StringComparison.Ordinal));

        goals[Target] = new Vector3(40f, 90f, 0f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 90f)), 0f, 4.5f);
    }

    /// <summary>
    /// A follower left standing on a top too small to walk on, with no route off it, steps off
    /// toward the player and plans again from where it lands.
    /// </summary>
    [Fact]
    public void FollowingFromATopWithNoRouteOffStepsOffTowardThePlayer()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 2f));
        var goals = new Goals { [Target] = new Vector3(40f, 55f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(CellWorld(PostTop), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.Follow(Target, 3f);
        RunUntil(walk, body, _ => heard.Any(line => line.Contains("stepping off", StringComparison.Ordinal)), seconds: 10f);

        Assert.Contains(heard, line => line.Contains("stepping off", StringComparison.Ordinal));
        Assert.True(body.Travelling || body.Flat.Y > 40.5f, "the follower stepped off the top");
    }

    /// <summary>A post 0.8 m across and 2 m tall, its top too small to walk on.</summary>
    private static readonly Vector3[][] PostTop =
    [
        [new(39.6f, 39.6f, 2f), new(40.4f, 39.6f, 2f), new(40.4f, 40.4f, 2f), new(39.6f, 40.4f, 2f)],
        [new(39.6f, 39.6f, 0f), new(40.4f, 39.6f, 0f), new(40.4f, 39.6f, 2f), new(39.6f, 39.6f, 2f)],
        [new(40.4f, 39.6f, 0f), new(40.4f, 40.4f, 0f), new(40.4f, 40.4f, 2f), new(40.4f, 39.6f, 2f)],
        [new(40.4f, 40.4f, 0f), new(39.6f, 40.4f, 0f), new(39.6f, 40.4f, 2f), new(40.4f, 40.4f, 2f)],
        [new(39.6f, 40.4f, 0f), new(39.6f, 39.6f, 0f), new(39.6f, 39.6f, 2f), new(39.6f, 40.4f, 2f)],
    ];

    /// <summary>
    /// A player on a block the follower can neither walk nor jump onto is not followed to some
    /// other spot near them: the follower holds where it stands, says why, and keeps trying.
    /// </summary>
    [Fact]
    public void FollowingAPlayerOnATopItCannotReachHoldsWhereItStands()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(52f, 52f, 2.5f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(CellWorld(Furnace), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.Follow(Target, 3f);
        RunUntil(walk, body, _ => heard.Count(line => line.Contains("holding where the character stands", StringComparison.Ordinal)) >= 2, seconds: 30f);

        Assert.True(walk.IsBusy);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 40f)), 0f, 0.5f);
        Assert.Contains(heard, line => line.Contains("no route reaches the player's floor", StringComparison.Ordinal)
            && line.Contains("holding where the character stands", StringComparison.Ordinal));
    }

    /// <summary>A block 4 m across and 2.5 m tall, higher than a body steps or reaches without leaping.</summary>
    private static readonly Vector3[][] Furnace =
    [
        [new(50f, 50f, 2.5f), new(54f, 50f, 2.5f), new(54f, 54f, 2.5f), new(50f, 54f, 2.5f)],
        [new(50f, 50f, 0f), new(54f, 50f, 0f), new(54f, 50f, 2.5f), new(50f, 50f, 2.5f)],
        [new(54f, 50f, 0f), new(54f, 54f, 0f), new(54f, 54f, 2.5f), new(54f, 50f, 2.5f)],
        [new(54f, 54f, 0f), new(50f, 54f, 0f), new(50f, 54f, 2.5f), new(54f, 54f, 2.5f)],
        [new(50f, 54f, 0f), new(50f, 50f, 0f), new(50f, 50f, 2.5f), new(50f, 54f, 2.5f)],
    ];

    [Fact]
    public void ThePlayerMovingTheCharacterEndsAFollow()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 70f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.Follow(Target, 3f);
        RunUntil(walk, body, report => report.Reason.StartsWith("keeping behind", StringComparison.Ordinal));

        body.PlayerMovementInputFrames++;
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.False(walk.IsBusy);
    }

    /// <summary>Records every object a walk asks the client to use.</summary>
    private sealed class UseRecorder : INavigationDoors
    {
        public List<uint> Used { get; } = [];

        public bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door)
        {
            door = default;
            return false;
        }

        public bool IsOpen(uint doorId) => true;

        public void Use(uint doorId) => Used.Add(doorId);
    }

    [Fact]
    public void StandingOnAnObjectWithNoCollisionFindsNoRoute()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(52f, 52f, 0f) });

        walk.StandOn(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("no collision", report.Reason);
    }

    private static readonly NavTriangle[] SlabTop =
    [
        new(new Vector3(50f, 50f, 1f), new Vector3(54f, 50f, 1f), new Vector3(54f, 54f, 1f)),
        new(new Vector3(50f, 50f, 1f), new Vector3(54f, 54f, 1f), new Vector3(50f, 54f, 1f)),
    ];

    /// <summary>A ramp 6 m long rising 2 m to a deck 4 m across, as the polygons of one cell.</summary>
    private static readonly Vector3[][] RampDeck =
    [
        [new(50f, 46f, 0f), new(54f, 46f, 0f), new(54f, 52f, 2f), new(50f, 52f, 2f)],
        [new(50f, 52f, 2f), new(54f, 52f, 2f), new(54f, 56f, 2f), new(50f, 56f, 2f)],
    ];

    private static IEnumerable<NavTriangle> Triangles(Vector3[] polygon)
    {
        for (int index = 2; index < polygon.Length; index++)
            yield return new NavTriangle(polygon[0], polygon[index - 1], polygon[index]);
    }

    private static PhysicsEngine RampDeckWorld() => CellWorld(RampDeck);

    /// <summary>Flat ground with the polygons of one cell standing on it.</summary>
    private static PhysicsEngine CellWorld(Vector3[][] cell)
    {
        var vertices = new Dictionary<ushort, Vector3>();
        var polygons = new List<List<short>>();
        foreach (Vector3[] polygon in cell)
        {
            var indices = new List<short>();
            foreach (Vector3 corner in polygon)
            {
                indices.Add((short)vertices.Count);
                vertices[(ushort)vertices.Count] = corner;
            }
            polygons.Add(indices);
        }
        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [new CellSurface(0xA9B40100u, vertices, polygons)],
            [],
            0f,
            0f);
        return physics;
    }

    /// <summary>Flat ground with a slab 4 m across and 1 m up, higher than a body steps.</summary>
    private static PhysicsEngine RaisedSlabWorld()
    {
        var slab = new Dictionary<ushort, Vector3>
        {
            [0] = new(50f, 50f, 1f),
            [1] = new(54f, 50f, 1f),
            [2] = new(54f, 54f, 1f),
            [3] = new(50f, 54f, 1f),
        };
        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [new CellSurface(0xA9B40100u, slab, [[0, 1, 2, 3]])],
            [],
            0f,
            0f);
        return physics;
    }

    [Fact]
    public void ARouteAskedForAloneIsFoundWithoutMovingTheCharacter()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Equal(NavRouteOutcome.Routed, walk.Route!.Outcome);
        Assert.Equal(0, body.MovesBegun);
    }

    [Fact]
    public void StoppingAWalkEndsItAndStopsItsMoves()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        Run(walk, body, seconds: 1f);

        walk.Stop();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Stopped, walk.Report.State);
        Assert.Equal(150f - body.Position.Y, walk.Report.RemainingMeters, 1);
        Assert.False(body.Travelling);
        Assert.False(walk.IsBusy);
    }

    [Fact]
    public void WhatIsLeftIsMeasuredToWhereTheObjectStandsWhenTheWalkEnds()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 150f, 0f) };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);

        goals[Target] = new Vector3(40f, 120f, 0f);
        walk.Stop();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Stopped, walk.Report.State);
        Assert.Equal(120f - body.Position.Y, walk.Report.RemainingMeters, 1);
    }

    [Fact]
    public void ThePlayerMovingTheCharacterEndsTheWalk()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        Run(walk, body, seconds: 1f);

        body.Interrupt();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.False(walk.IsBusy);
    }

    [Fact]
    public void ACharacterThatCannotMovePlansAgainThenGivesUpNamingWhatBlockedIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 150f, 0f),
            Blocker = new NavigationBlocker(Door, "Door", IsClosedDoor: true),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(NavigationWalkController.MaximumReplans, report.Replans);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("a closed door, Door (0x7A000001)", report.Reason);
        Assert.Equal(110f, report.RemainingMeters, 1);
        Assert.False(body.Travelling);
    }

    /// <summary>
    /// A walk that keeps stopping plans again from where it stuck the first time, and after that
    /// tries a different way off what it met at each stop before planning again: stepping back,
    /// the roomiest spot nearby, a sidestep toward the more open side, one the other way, and a
    /// hop forward. Narration says where it stuck, facing which way, on which leg, and how near
    /// the walls were.
    /// </summary>
    [Fact]
    public void AWalkThatKeepsStoppingTriesADifferentWayOffEachTimeAndSaysWhereItStuck()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Stuck = true };
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Contains(heard, line => line.Contains("stopped making progress at (40.0, 40.0, 0.0)", StringComparison.Ordinal)
            && line.Contains("heading", StringComparison.Ordinal)
            && line.Contains("leg 1 of", StringComparison.Ordinal)
            && line.Contains("nearest wall", StringComparison.Ordinal));
        string[] tried = [.. heard.Where(line => line.Contains("planning again (", StringComparison.Ordinal))];
        Assert.Equal(NavigationWalkController.MaximumReplans, tried.Length);
        Assert.DoesNotContain(" first", tried[0]);
        Assert.Contains("stepping back 1 m first", tried[1]);
        Assert.Contains("finding no roomier spot nearby first", tried[2]);
        Assert.Contains("sidestepping 1 m to the", tried[3]);
        Assert.Contains("the more open side first", tried[3]);
        Assert.Contains("sidestepping 1 m to the", tried[4]);
        Assert.Contains("hopping forward first", tried[5]);
        Assert.Contains(RuntimeMoveDirection.Backward, body.TravelDirections);
        Assert.Contains(RuntimeMoveDirection.StrafeLeft, body.TravelDirections);
        Assert.Contains(RuntimeMoveDirection.StrafeRight, body.TravelDirections);
        Assert.Equal(1, body.Jumps);
    }

    /// <summary>A walk stuck against the wall of a narrow corridor moves to the middle of it.</summary>
    [Fact]
    public void AWalkStuckAgainstACorridorWallMovesToTheMiddleOfIt()
    {
        var body = new SimulatedBody(new Vector3(38.7f, 40f, 0f)) { Stuck = true };
        var walk = new NavigationWalkController(CellWorld(Corridor), body, new Goals { [Target] = new Vector3(40f, 56f, 0f) });
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.WalkTo(Target);
        RunUntil(walk, body, _ => heard.Any(line => line.Contains("roomiest spot nearby at", StringComparison.Ordinal)), seconds: 60f);

        string moving = Assert.Single(heard, line => line.Contains("roomiest spot nearby at", StringComparison.Ordinal));
        float x = float.Parse(moving[(moving.IndexOf("roomiest spot nearby at (", StringComparison.Ordinal) + 25)..].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(x, 39f, 41f);
    }

    /// <summary>
    /// A walk stuck in a passage so narrow that keeping out of where it stuck leaves no route
    /// plans through that spot again and goes on down its ladder of ways off, rather than
    /// ending at the first stop.
    /// </summary>
    [Fact]
    public void AWalkStuckInAPassageWithNoOtherWayKeepsTryingDifferentWaysOff()
    {
        var body = new SimulatedBody(new Vector3(40f, 34f, 0f)) { Stuck = true };
        var walk = new NavigationWalkController(CellWorld(Passage), body, new Goals { [Target] = new Vector3(40f, 56f, 0f) });
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body, seconds: 200f);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Contains(heard, line => line.Contains("no route keeps out of where the character stuck", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.Contains("stepping back 1 m first", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.Contains("hopping forward first", StringComparison.Ordinal));
        Assert.Equal(NavigationWalkController.MaximumReplans, heard.Count(line => line.Contains("planning again (", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A follow stuck in such a passage keeps going round the ways off for as long as it stays
    /// stuck, starting over without starting the ways off over: only its very first stop tries
    /// nothing.
    /// </summary>
    [Fact]
    public void AFollowStuckInAPassageWithNoOtherWayKeepsTryingDifferentWaysOff()
    {
        var body = new SimulatedBody(new Vector3(40f, 34f, 0f)) { Stuck = true };
        var goals = new Goals { [Target] = new Vector3(40f, 56f, 0f) };
        goals.Players.Add(Target);
        var walk = new NavigationWalkController(CellWorld(Passage), body, goals);
        var heard = new List<string>();
        walk.Narration = heard.Add;

        walk.Follow(Target, 3f);
        RunUntil(walk, body, _ => heard.Count(line => line.Contains("hopping forward first", StringComparison.Ordinal)) >= 3, seconds: 600f);

        Assert.True(walk.IsBusy);
        string[] stops = [.. heard.Where(line => line.Contains("planning again (", StringComparison.Ordinal))];
        Assert.True(stops.Length > 2 * NavigationWalkController.MaximumReplans, $"stopped {stops.Length} times");
        Assert.Single(stops, line => !line.Contains(" first", StringComparison.Ordinal));
        Assert.Contains(stops, line => line.Contains("stepping back 1 m first", StringComparison.Ordinal));
        Assert.Contains(stops, line => line.Contains("sidestepping 1 m to the", StringComparison.Ordinal));
        Assert.True(body.Jumps >= 3);
    }

    /// <summary>A dead-end passage 1.6 m wide and 3 m tall: walls at x = 39.2 and x = 40.8 from y = 30 to y = 60, closed at y = 30.</summary>
    private static readonly Vector3[][] Passage =
    [
        [new(39.2f, 30f, 0f), new(39.2f, 60f, 0f), new(39.2f, 60f, 3f), new(39.2f, 30f, 3f)],
        [new(40.8f, 30f, 0f), new(40.8f, 60f, 0f), new(40.8f, 60f, 3f), new(40.8f, 30f, 3f)],
        [new(39.2f, 30f, 0f), new(40.8f, 30f, 0f), new(40.8f, 30f, 3f), new(39.2f, 30f, 3f)],
    ];

    /// <summary>A corridor 4 m wide and 3 m tall, walls at x = 38 and x = 42 from y = 30 to y = 60.</summary>
    private static readonly Vector3[][] Corridor =
    [
        [new(38f, 30f, 0f), new(38f, 60f, 0f), new(38f, 60f, 3f), new(38f, 30f, 3f)],
        [new(42f, 30f, 0f), new(42f, 60f, 0f), new(42f, 60f, 3f), new(42f, 30f, 3f)],
    ];

    [Fact]
    public void AWalkRunsThroughADoorwayWithoutStoppingWhereItsArcsKeepToTheFloor()
    {
        var turning = new RuntimeRouteTurning(RunSpeed: 4f, RunTurnDegreesPerSecond: 180f);
        var standing = new SimulatedBody(new Vector3(4f, 4f, -30f));
        var cutting = new SimulatedBody(new Vector3(4f, 4f, -30f)) { Turning = turning };
        var said = new List<string>();

        foreach (SimulatedBody body in new[] { standing, cutting })
        {
            said.Clear();
            var walk = new NavigationWalkController(RoomWithDoorways(10f), body, new Goals { [Target] = new Vector3(4f, 16f, -30f) }, said.Add);
            walk.WalkTo(Target);
            Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        }

        Assert.True(standing.TravelStops > 1, $"turning in place, the walk stopped {standing.TravelStops} times");
        Assert.Equal(1, cutting.TravelStops);
        Assert.Contains(said, line => line.Contains("turned in place at 0", StringComparison.Ordinal) && !line.Contains("ran around 0 corners", StringComparison.Ordinal));
        for (int step = 1; step < cutting.Path.Count; step++)
        {
            Vector2 from = cutting.Path[step - 1];
            Vector2 to = cutting.Path[step];
            if ((from.Y < 10f) == (to.Y < 10f))
                continue;
            float crossing = from.X + ((to.X - from.X) * ((10f - from.Y) / (to.Y - from.Y)));
            Assert.InRange(crossing, 9f + NavGrid.BrushMargin, 11f - NavGrid.BrushMargin);
        }
    }

    [Fact]
    public void AWalkPlansAroundTheSpotWhereItWasBlocked()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 0.3f) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Other, "Barrel", IsClosedDoor: false),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(report.Replans, 1, NavigationWalkController.MaximumReplans);
        Assert.Equal(0u, report.BlockedByObjectId);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 80f)), 0f, NavigationWalkController.DefaultArrivalMeters + 0.75f);
    }

    [Fact]
    public void AWalkBlockedBesideAnObjectPlansAroundAllOfItAfterward()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 1.5f) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Other, "Ore Deposit", IsClosedDoor: false, new Vector3(40f, 60f, 0f), 1.5f),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, report.Replans);
        NavRoute route = walk.Route!;
        Assert.All(route.Legs.Skip(1), end =>
            Assert.True(Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(40f, 60f)) >= 1.9f, "a leg ends inside the deposit"));
        for (int leg = 2; leg < route.Legs.Count; leg++)
        {
            var start = new Vector2(route.Legs[leg - 1].X, route.Legs[leg - 1].Y);
            Vector2 along = new Vector2(route.Legs[leg].X, route.Legs[leg].Y) - start;
            float t = along.LengthSquared() > 0f
                ? Math.Clamp(Vector2.Dot(new Vector2(40f, 60f) - start, along) / along.LengthSquared(), 0f, 1f)
                : 0f;
            Assert.True(Vector2.Distance(new Vector2(40f, 60f), start + (along * t)) >= 1.5f, $"leg {leg} passes through the deposit");
        }
    }

    [Fact]
    public void AWalkBlockedBesideAnObjectWithNoOtherWayEndsBlockedNamingItAtOnce()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.8f) };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Ore Deposit", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f),
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(1, report.Replans);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("Ore Deposit", report.Reason);
        Assert.Contains("stands in the way, and no other way around it was found", report.Reason);
    }

    [Fact]
    public void AWalkPlansAroundACreatureInItsWayAndKeepsClearOfIt()
    {
        var creature = new Vector2(40f, 70f);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (creature, Reach) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 100f, 0f),
            CrowdNow = () => [new NavAvoidance(new Vector3(creature, 0f), 0.8f)],
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        float nearest = float.PositiveInfinity;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntil(walk, body, report =>
        {
            nearest = MathF.Min(nearest, Vector2.Distance(body.Flat, creature));
            return report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting);
        });

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.True(nearest >= Reach - 0.05f, $"the walk came within {nearest:0.00} m of the creature");
    }

    [Fact]
    public void AWalkStepsAroundACreatureThatWalksOntoItsRouteWithoutStopping()
    {
        var creature = new Vector2(40f, 80f);
        bool there = false;
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (creature, Reach), ObstacleActive = () => there };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 120f, 0f),
            CrowdNow = () => there ? [new NavAvoidance(new Vector3(creature, 0f), 0.8f)] : [],
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        float nearest = float.PositiveInfinity;
        int longestStill = 0;
        bool planning = false;

        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking && body.Position.Y > 60f);
        there = true;
        NavigationWalkReport report = RunUntil(walk, body, report =>
        {
            nearest = MathF.Min(nearest, Vector2.Distance(body.Flat, creature));
            longestStill = Math.Max(longestStill, body.StillFrames);
            planning |= report.State == NavigationWalkState.Planning;
            return report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting);
        });

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.False(planning, "the walk stopped to plan again");
        Assert.True(nearest >= Reach - 0.05f, $"the walk came within {nearest:0.00} m of the creature");
        Assert.True(longestStill <= 10, $"the walk stood still for {longestStill} frames");
    }

    [Fact]
    public void AWalkStoppedByACreatureWaitsForItToMoveAsideAndGoesOn()
    {
        bool movedAside = false;
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f))
        {
            Obstacle = (new Vector2(5f, 10f), Reach),
            ObstacleActive = () => !movedAside,
        };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true),
            CrowdNow = () => movedAside ? [] : [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport waiting = RunUntil(walk, body, report => report.Reason.Contains("waiting for it to move"));
        movedAside = true;
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Contains("Drudge Skulker", waiting.Reason);
        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
    }

    [Fact]
    public void AWalkThatACreatureNeverLetsByEndsBlockedNamingIt()
    {
        var body = new SimulatedBody(new Vector3(5f, 8f, -30f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true),
            CrowdNow = () => [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("Drudge Skulker", report.Reason);
    }

    [Fact]
    public void AWalkStoppedByAHostileMonsterPlansAroundItAtOnceInsteadOfWaitingForItToMove()
    {
        var body = new SimulatedBody(new Vector3(5f, 8f, -30f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true, Hostile: true),
            CrowdNow = () => [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport first = RunUntil(
            walk,
            body,
            report => report.Reason.Contains("waiting for it to move") || report.Reason.Contains("planning again"));
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Contains("planning again", first.Reason);
        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("a hostile monster, Drudge Skulker", report.Reason);
    }

    [Fact]
    public void ANewerRequestReplacesTheWalkUnderWay()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 150f, 0f), [Other] = new Vector3(70f, 40f, 0f) };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);

        long second = walk.WalkTo(Other);
        NavigationWalkReport settled = RunUntilSettled(walk, body);

        Assert.Equal(second, settled.Sequence);
        Assert.Equal(NavigationWalkState.Arrived, settled.State);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(70f, 40f)), 0f, NavigationWalkController.DefaultArrivalMeters + 0.75f);
    }

    [Fact]
    public void AnObjectTheClientCannotPlaceHasNoRouteAndNoDistance()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WalkTo(Other);
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.NoRoute, walk.Report.State);
        Assert.Contains("0x50000002", walk.Report.Reason);
        Assert.True(float.IsNaN(walk.Report.RemainingMeters));
    }

    [Fact]
    public void AGoalTooFarAwayForOneRegionIsWalkedToInStages()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(
            FlatWorld(landblocksNorth: 3),
            body,
            new Goals { [Target] = new Vector3(60f, 520f, 0f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body, seconds: 300f);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(
            Vector2.Distance(body.Flat, new Vector2(60f, 520f)),
            0f,
            NavigationWalkController.DefaultArrivalMeters + 0.05f);
        Assert.Equal(0, report.Replans);
    }

    [Fact]
    public void AWalkTowardAGoalBeyondTheLoadedWorldEndsWhereItCanGetNoNearer()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 900f, 0f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body, seconds: 200f);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("no way on toward the goal", report.Reason);
        Assert.True(body.Position.Y > 150f);
    }

    [Fact]
    public void AStageRegionHoldsTheCharacterAndReachesTowardTheGoal()
    {
        var from = new Vector3(500f, 500f, 0f);
        float margin = NavigationWalkController.RegionMargin - NavGrid.DefaultCellSize;
        foreach (Vector3 goal in new[] { new Vector3(1500f, 500f, 0f), new Vector3(-400f, -600f, 0f), new Vector3(700f, 1400f, 0f) })
        {
            NavigationWalkController.ChooseStageRegion(from, goal, out float originX, out float originY, out float size);

            Assert.Equal(NavigationWalkController.MaximumRegion, size);
            Assert.InRange(from.X - originX, margin, size - margin);
            Assert.InRange(from.Y - originY, margin, size - margin);
            var centre = new Vector2(originX + (size * 0.5f), originY + (size * 0.5f));
            var flatGoal = new Vector2(goal.X, goal.Y);
            Assert.True(Vector2.Distance(centre, flatGoal) < Vector2.Distance(new Vector2(from.X, from.Y), flatGoal) - 100f);
        }
    }

    [Fact]
    public void InsideASealedDungeonOneGridCoversEveryCellAndServesEveryWalkThere()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals
        {
            [Target] = new Vector3(60f, 32f, -30f),
            [Other] = new Vector3(245f, 32f, -30f),
        };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room);

        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        NavGrid? first = walk.Grid;
        walk.WalkTo(Other);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Same(first, walk.Grid);
        Assert.True(first!.Contains(new Vector3(10f, 10f, -30f)) && first.Contains(new Vector3(250f, 50f, -30f)));
    }

    [Fact]
    public void ADungeonWiderThanAnyOutdoorRegionGetsOneGridWithoutTerrain()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(645f, 32f, -30f) };
        var walk = new NavigationWalkController(
            DungeonWorld(room, corridorEnd: 630f),
            body,
            goals,
            isSealedDungeon: cell => cell == room);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Equal("a route was found", report.Reason);
        NavGrid grid = walk.Grid!;
        Assert.True(grid.Size > 2f * NavigationWalkController.MaximumRegion);
        Assert.Equal([0xA9B4FFFFu], grid.LandblockIds);
        Assert.True(grid.FindNode(new Vector3(100f, 150f, 0f), 2f, 2f) < 0);
    }

    [Fact]
    public void AGridShownInsideASealedDungeonCoversTheWholeDungeonAndServesItsWalks()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(245f, 32f, -30f) };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room)
        {
            ShowGrid = true,
        };

        var wall = Stopwatch.StartNew();
        while (walk.Grid is null && wall.Elapsed < TimeSpan.FromSeconds(30))
        {
            walk.Tick(Frame);
            Thread.Sleep(1);
        }
        NavGrid? shown = walk.Grid;
        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.NotNull(shown);
        Assert.True(shown.Contains(new Vector3(10f, 10f, -30f)) && shown.Contains(new Vector3(250f, 50f, -30f)));
        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Same(shown, walk.Grid);
    }

    [Fact]
    public void AGoalFarOutsideTheSealedDungeonIsReportedWithoutBuildingAGrid()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(5000f, 32f, -30f) };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("outside this dungeon", report.Reason);
        Assert.Null(walk.Grid);
    }

    [Fact]
    public void AWalkPlansAroundAnObjectTheServerPlacedWhenAnotherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        goals.Obstacles.Add(new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f));
        var walk = new NavigationWalkController(RoomWithDoorways(5f, 15f), body, goals);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Contains(walk.Route!.Path, point => point.X > 13f && MathF.Abs(point.Y - 10f) < 0.5f);
    }

    [Fact]
    public void AWalkPlansThroughAnObjectTheServerPlacedWhenNoOtherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        goals.Obstacles.Add(new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f));
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Contains(walk.Route!.Path, point => MathF.Abs(point.X - 5f) < 1f && MathF.Abs(point.Y - 10f) < 0.5f);
    }

    /// <summary>
    /// A portal collides with nothing, and a walk that brushed one was sent somewhere
    /// else: Holtburg into the Dark Cavern, the Town Network into 0x016C. A route goes
    /// around a portal where another way arrives.
    /// </summary>
    [Fact]
    public void AWalkPlansAroundAPortalWhenAnotherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        var portal = new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f);
        goals.Portals.Add(portal);
        var walk = new NavigationWalkController(RoomWithDoorways(5f, 15f), body, goals);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Contains(walk.Route!.Path, point => point.X > 13f && MathF.Abs(point.Y - 10f) < 0.5f);
        AssertKeepsClearOf(walk.Route!, portal);
    }

    /// <summary>
    /// Unlike an object the server placed, a portal is never walked through because no
    /// other way arrives: going through it ends the walk somewhere else entirely.
    /// </summary>
    [Fact]
    public void AWalkNeverPlansThroughAPortalEvenWhenNoOtherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        var portal = new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f);
        goals.Portals.Add(portal);
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.RouteTo(Target);
        RunUntilSettled(walk, body);

        if (walk.Route is { } route)
            AssertKeepsClearOf(route, portal);
    }

    /// <summary>
    /// A walk to the portal it is going to use still reaches it: it ends where the
    /// same walk ends with no portal there at all.
    /// </summary>
    [Fact]
    public void AWalkToAPortalStillReachesIt()
    {
        Vector3 withPortal = EndOfWalk(new NavAvoidance(new Vector3(5.5f, 16f, -30f), 0.8f));
        Vector3 without = EndOfWalk(portal: null);

        Assert.True(Vector3.Distance(withPortal, without) < 0.5f, $"with the portal the walk ends at {withPortal}, without it at {without}");

        static Vector3 EndOfWalk(NavAvoidance? portal)
        {
            var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
            var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
            if (portal is { } standing)
                goals.Portals.Add(standing);
            var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

            walk.RouteTo(Target);
            Assert.Equal(NavigationWalkState.Planned, RunUntilSettled(walk, body).State);
            return walk.Route!.Path[^1];
        }
    }

    private static void AssertKeepsClearOf(NavRoute route, NavAvoidance portal)
    {
        var centre = new Vector2(portal.Centre.X, portal.Centre.Y);
        for (int index = 1; index < route.Path.Count; index++)
        {
            var from = new Vector2(route.Path[index - 1].X, route.Path[index - 1].Y);
            var to = new Vector2(route.Path[index].X, route.Path[index].Y);
            Vector2 along = to - from;
            float t = along.LengthSquared() > 1e-6f
                ? Math.Clamp(Vector2.Dot(centre - from, along) / along.LengthSquared(), 0f, 1f)
                : 0f;
            Assert.True(
                Vector2.Distance(centre, from + (along * t)) > RuntimeNavigationGoalSource.PortalLeastRadius,
                $"the route passes within {RuntimeNavigationGoalSource.PortalLeastRadius} m of the portal between {from} and {to}");
        }
    }

    [Fact]
    public void APlanningRegionHoldsBothEndsWithRoomAroundThem()
    {
        var from = new Vector3(10f, 10f, 0f);
        var to = new Vector3(130f, 40f, 0f);

        Assert.True(NavigationWalkController.TryChooseRegion(from, to, out float originX, out float originY, out float size));

        float margin = NavigationWalkController.RegionMargin - NavGrid.DefaultCellSize;
        foreach (Vector3 end in new[] { from, to })
        {
            Assert.InRange(end.X - originX, margin, size - margin);
            Assert.InRange(end.Y - originY, margin, size - margin);
        }
        Assert.False(NavigationWalkController.TryChooseRegion(from, new Vector3(400f, 10f, 0f), out _, out _, out _));
    }

    [Fact]
    public void AClosedDoorOnTheRouteIsOpenedAndWalkedThrough()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: true);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f))
        {
            Obstacle = (new Vector2(40f, 60f), 0.3f),
            ObstacleActive = () => !doors.Open,
        };
        var walk = new NavigationWalkController(
            FlatWorld(),
            body,
            new Goals { [Target] = new Vector3(40f, 80f, 0f) },
            doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, doors.DroppedUses);
        Assert.Equal(0, report.Replans);
        Assert.True(body.Position.Y > 60f);
    }

    [Fact]
    public void ADoorIsWalkedUpToBeforeItIsUsedSoTheClientNeedNotWalkIn()
    {
        var doors = new FakeDoors(
            new NavigationDoor(Door, "Door", new Vector3(40f, 60f, 0f), 0.3f),
            new Vector2(40f, 60f),
            opens: true)
        {
            Reach = 1.2f,
        };
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f))
        {
            Obstacle = (new Vector2(40f, 60f), 0.3f),
            ObstacleActive = () => !doors.Open,
        };
        var walk = new NavigationWalkController(
            FlatWorld(),
            body,
            new Goals { [Target] = new Vector3(40f, 80f, 0f) },
            doors: doors);
        doors.Where = () => new Vector2(body.Position.X, body.Position.Y);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, doors.DroppedUses);
        float usedFrom = Assert.Single(doors.UsedFrom);
        Assert.True(
            usedFrom <= 1.2f,
            $"the door was used from {usedFrom:0.00} m away, outside the client's {1.2f:0.00} m use range");
    }

    [Fact]
    public void ADoorThatWillNotOpenEndsTheWalkBlockedNamingIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: false);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 0.3f) };
        var walk = new NavigationWalkController(
            FlatWorld(),
            body,
            new Goals { [Target] = new Vector3(40f, 80f, 0f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("would not open: Door (0x7A000001)", report.Reason);
        Assert.Equal(1, doors.Uses);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void ALockedDoorIsWalkedAroundWithoutBeingTriedWhenAnotherWayArrives()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false)
        {
            LockedWhenAppraised = true,
        };
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f, 15f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Appraisals);
        Assert.Equal(0, doors.Uses);
        Assert.True(body.Position.Y > 12f);
    }

    [Fact]
    public void ALockedDoorOnTheOnlyWayEndsTheWalkBlockedNamingItWithoutTryingIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false)
        {
            LockedWhenAppraised = true,
        };
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("a locked door: Door (0x7A000001), and no other way around it was found", report.Reason);
        Assert.Equal(0, doors.Uses);
    }

    [Fact]
    public void ADoorThatWillNotOpenIsWalkedAroundWhenAnotherWayArrives()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false);
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f, 15f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.True(body.Position.Y > 12f);
    }

    [Fact]
    public void AWalkThatStopsBesideAClosedDoorItDidNotSeeAheadOpensIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: true) { SeenAhead = false };
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f))
        {
            Obstacle = (new Vector2(40f, 60f), 0.3f),
            ObstacleActive = () => !doors.Open,
        };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Door, "Door", IsClosedDoor: true),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals, doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, report.Replans);
        Assert.True(body.Position.Y > 60f);
    }

    /// <summary>
    /// A walk builds its grid again over the objects standing here where it needs them and
    /// the grid has none of them, and it does that once for each stretch it walks. Where
    /// the goal is one nothing reaches and the objects here keep changing, the answer must
    /// still be that nothing reaches it: build, search, build, search is a walk that never
    /// answers and a client that does nothing else.
    /// </summary>
    [Fact]
    public void AWalkThatCannotArriveStopsBuildingGridsThoughObjectsKeepChanging()
    {
        const uint drifter = 0x80002AB2u;
        var said = new List<string>();
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        PhysicsEngine world = FlatWorld();
        var goals = new Goals();
        var walk = new NavigationWalkController(world, body, goals, say: said.Add);
        walk.WalkToPlace(0xA9B40001u, new Vector3(45f, 40f, 0f), 1f);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);

        world.ShadowObjects.Register(
            drifter,
            gfxObjId: 0u,
            new Vector3(50f, 40f, 0f),
            Quaternion.Identity,
            1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 1f,
            seedCellId: 0xA9B40001u,
            isStatic: false);
        goals.Standing.Add(drifter);

        int built = said.Count(line => line.StartsWith("Navmesh:", StringComparison.Ordinal));
        walk.WalkToPlace(0xA9B40001u, new Vector3(60f, 40f, 4f), 1f);

        var wall = Stopwatch.StartNew();
        NavigationWalkReport report = walk.Report;
        for (float along = 0f; wall.Elapsed < TimeSpan.FromSeconds(60); along += 0.5f)
        {
            world.ShadowObjects.UpdatePosition(
                drifter,
                new Vector3(50f + (along % 8f), 40f, 0f),
                Quaternion.Identity,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0xA9B4FFFFu,
                seedCellId: 0xA9B40001u);
            walk.Tick(Frame);
            report = walk.Report;
            if (report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting))
                break;
            Thread.Sleep(1);
        }

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.InRange(said.Count(line => line.StartsWith("Navmesh:", StringComparison.Ordinal)) - built, 0, 2);
    }

    /// <summary>
    /// The ground hardly ever changes: what changes it is a thing the server places and
    /// leaves, as the rocks of a jump puzzle are placed as the character comes near. A
    /// thing crossing the region is not that, and a grid must not be built for it: a
    /// dungeon's grid takes far longer to build than the moment the thing is there, so
    /// one built for each look would be built again and again and never be of use.
    /// </summary>
    [Fact]
    public void AThingCrossingTheRegionBuildsNoGridAndOneThatStaysBuildsOne()
    {
        const uint drifter = 0x80002AB1u;
        var said = new List<string>();
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        PhysicsEngine world = FlatWorld();
        var goals = new Goals();
        var walk = new NavigationWalkController(world, body, goals, say: said.Add) { ShowGrid = true };
        Assert.True(TickUntilSaid(walk, said, 1), "no grid was built to show around the character");

        world.ShadowObjects.Register(
            drifter,
            gfxObjId: 0u,
            new Vector3(50f, 40f, 0f),
            Quaternion.Identity,
            1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 1f,
            seedCellId: 0xA9B40001u,
            isStatic: false);
        goals.Standing.Add(drifter);

        var wall = Stopwatch.StartNew();
        for (float along = 0f; wall.Elapsed < TimeSpan.FromSeconds(2); along += 0.5f)
        {
            world.ShadowObjects.UpdatePosition(
                drifter,
                new Vector3(50f + (along % 10f), 40f, 0f),
                Quaternion.Identity,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0xA9B4FFFFu,
                seedCellId: 0xA9B40001u);
            walk.Tick(Frame);
            Thread.Sleep(1);
        }
        Assert.Single(said);

        Assert.True(TickUntilSaid(walk, said, 2), "no grid was built once the thing stayed where it was");
        Assert.Contains("built because the objects in it changed", said[1]);
    }

    /// <summary>Ticks a walk until it has said this many lines, or gives up.</summary>
    private static bool TickUntilSaid(NavigationWalkController walk, List<string> said, int lines)
    {
        var wall = Stopwatch.StartNew();
        while (wall.Elapsed < TimeSpan.FromSeconds(20))
        {
            walk.Tick(Frame);
            if (said.Count >= lines)
                return said.Count == lines;
            Thread.Sleep(1);
        }
        return false;
    }

    private static PhysicsEngine FlatWorld(int landblocksNorth = 1)
    {
        var physics = new PhysicsEngine();
        for (int index = 0; index < landblocksNorth; index++)
        {
            physics.AddLandblock(
                0xA9B4FFFFu + ((uint)index << 16),
                new TerrainSurface(new byte[81], new float[256]),
                [],
                [],
                0f,
                index * 192f);
        }
        return physics;
    }

    /// <summary>
    /// A room 20 m square, 30 m below its landblock's terrain, split along y = 10 by
    /// a wall 3 m high with a doorway 2 m wide centred at each x in <paramref name="doorways"/>.
    /// </summary>
    private static PhysicsEngine RoomWithDoorways(params float[] doorways)
    {
        const float floor = -30f;
        const float top = -27f;
        var vertices = new Dictionary<ushort, Vector3>
        {
            [0] = new(0f, 0f, floor),
            [1] = new(20f, 0f, floor),
            [2] = new(20f, 20f, floor),
            [3] = new(0f, 20f, floor),
        };
        var polygons = new List<List<short>> { new() { 0, 1, 2, 3 } };
        float start = 0f;
        foreach (float end in doorways.Order().Select(centre => centre - 1f).Append(20f))
        {
            if (end > start)
            {
                short first = (short)vertices.Count;
                vertices[(ushort)first] = new Vector3(start, 10f, floor);
                vertices[(ushort)(first + 1)] = new Vector3(end, 10f, floor);
                vertices[(ushort)(first + 2)] = new Vector3(end, 10f, top);
                vertices[(ushort)(first + 3)] = new Vector3(start, 10f, top);
                polygons.Add([first, (short)(first + 1), (short)(first + 2), (short)(first + 3)]);
            }
            start = end + 2f;
        }

        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [new CellSurface(0xA9B40100u, vertices, polygons)],
            [],
            0f,
            0f);
        return physics;
    }

    /// <summary>
    /// A landblock holding a dungeon 30 m below its terrain: two rooms joined by a
    /// corridor, 190 m long unless <paramref name="corridorEnd"/> moves the far room.
    /// </summary>
    private static PhysicsEngine DungeonWorld(uint firstCell, float corridorEnd = 230f)
    {
        static CellSurface Floor(uint cellId, float x0, float y0, float x1, float y1) => new(
            cellId,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(x0, y0, -30f),
                [1] = new(x1, y0, -30f),
                [2] = new(x1, y1, -30f),
                [3] = new(x0, y1, -30f),
            },
            [[0, 1, 2, 3]]);

        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [
                Floor(firstCell, 10f, 10f, 40f, 50f),
                Floor(firstCell + 1u, 40f, 28f, corridorEnd, 36f),
                Floor(firstCell + 2u, corridorEnd, 20f, corridorEnd + 20f, 50f),
            ],
            [],
            0f,
            0f);
        return physics;
    }

    private static NavigationWalkReport RunUntilSettled(NavigationWalkController walk, SimulatedBody body, float seconds = 120f) =>
        RunUntil(
            walk,
            body,
            report => report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting),
            seconds);

    /// <summary>Ticks a walk through a stretch of time without moving the body, each tick once any search under way has finished.</summary>
    private static void TickFor(NavigationWalkController walk, float seconds)
    {
        var wall = Stopwatch.StartNew();
        for (float simulated = 0f; simulated < seconds && wall.Elapsed < TimeSpan.FromSeconds(60); simulated += Frame)
        {
            walk.Tick(Frame);
            while (walk.IsSearching && !walk.SearchFinished && wall.Elapsed < TimeSpan.FromSeconds(60))
                Thread.Sleep(1);
        }
    }

    private static NavigationWalkReport RunUntil(
        NavigationWalkController walk,
        SimulatedBody body,
        Func<NavigationWalkReport, bool> done,
        float seconds = 120f)
    {
        var wall = Stopwatch.StartNew();
        float simulated = 0f;
        while (simulated < seconds && wall.Elapsed < TimeSpan.FromSeconds(120))
        {
            walk.Tick(Frame);
            NavigationWalkReport report = walk.Report;
            if (done(report))
                return report;
            if (report.State == NavigationWalkState.Planning || walk.IsSearching)
            {
                Thread.Sleep(1);
                continue;
            }
            body.Integrate(Frame);
            simulated += Frame;
        }
        return walk.Report;
    }

    private static void Run(NavigationWalkController walk, SimulatedBody body, float seconds)
    {
        for (float simulated = 0f; simulated < seconds; simulated += Frame)
        {
            walk.Tick(Frame);
            body.Integrate(Frame);
        }
    }

    internal sealed class Goals : Dictionary<uint, Vector3>, INavigationGoalSource
    {
        public bool TryGlobalOf(Vector3 world, out Vector3 global)
        {
            global = world;
            return true;
        }

        public NavigationBlocker? Blocker { get; init; }

        /// <summary>The objects that are players.</summary>
        public HashSet<uint> Players { get; } = [];

        public bool IsPlayer(uint objectId) => Players.Contains(objectId);

        /// <summary>The portals by id and where they stand.</summary>
        public Dictionary<uint, Vector3> PortalIds { get; } = [];

        public bool TryFindPortal(Vector3 near, float radius, out uint portalId, out Vector3 position)
        {
            foreach ((uint id, Vector3 at) in PortalIds)
            {
                if (Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(near.X, near.Y)) <= radius)
                {
                    portalId = id;
                    position = at;
                    return true;
                }
            }
            portalId = 0u;
            position = default;
            return false;
        }

        /// <summary>The collision of the objects a walk may stand on.</summary>
        public Dictionary<uint, NavSurfaces> Surfaces { get; } = [];

        /// <summary>The objects a grid stands on.</summary>
        public HashSet<uint> Standing { get; } = [];

        public bool StandsStill(uint entityLocalId) => Standing.Contains(entityLocalId);

        public bool TryGetSurfaces(uint objectId, out NavSurfaces surfaces) => Surfaces.TryGetValue(objectId, out surfaces!);

        /// <summary>The objects the server placed, as a walk asks for them.</summary>
        public List<NavAvoidance> Obstacles { get; } = [];

        public bool TryLocate(uint objectId, out Vector3 position) => TryGetValue(objectId, out position);

        public IReadOnlyList<NavAvoidance> FindObstacles(
            Vector3 around,
            float radius,
            uint goalObjectId,
            IReadOnlySet<uint>? inGrid = null) => Obstacles;

        /// <summary>The creatures and players standing around, as a walk asks for them now.</summary>
        public Func<IReadOnlyList<NavAvoidance>>? CrowdNow { get; init; }

        public IReadOnlyList<NavAvoidance> FindCrowd(Vector3 around, float radius, uint goalObjectId) => CrowdNow?.Invoke() ?? [];

        /// <summary>The portals standing about, which a walk keeps out of unless it is going to one.</summary>
        public List<NavAvoidance> Portals { get; } = [];

        public IReadOnlyList<NavAvoidance> FindPortals(Vector3 around, float radius, uint goalObjectId, Vector3 goal) =>
            RuntimeNavigationGoalSource.ExceptTheOneTheWalkEnds(
                [.. Portals.Select(RuntimeNavigationGoalSource.PortalFootprint)],
                goal);

        /// <summary>Places stand where their landblock-local point says, the test world having one landblock at its origin.</summary>
        public bool TryLocatePlace(uint cellId, Vector3 local, out Vector3 position)
        {
            position = local;
            return true;
        }

        public bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
        {
            blocker = Blocker ?? default;
            return Blocker is not null;
        }
    }

    /// <summary>A single door that opens a few frames after it is used, or never.</summary>
    internal sealed class FakeDoors : INavigationDoors
    {
        private int _pollsUntilOpen = -1;

        public FakeDoors(NavigationDoor door, Vector2 at, bool opens)
        {
            Door = door;
            At = at;
            Opens = opens;
        }

        public NavigationDoor Door { get; }

        public Vector2 At { get; }

        public bool Opens { get; }

        /// <summary>Whether a walk looking ahead along its leg finds the door.</summary>
        public bool SeenAhead { get; init; } = true;

        public bool Open { get; private set; }

        public int Uses { get; private set; }

        public bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door)
        {
            door = Door;
            if (Open || !SeenAhead)
                return false;
            var start = new Vector2(from.X, from.Y);
            Vector2 along = new Vector2(to.X, to.Y) - start;
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-6f ? Vector2.Dot(At - start, along) / lengthSquared : 0f;
            return t >= 0f && Vector2.Distance(At, start + (along * MathF.Min(t, 1f))) <= corridor;
        }

        public bool IsOpen(uint doorId)
        {
            if (_pollsUntilOpen > 0 && --_pollsUntilOpen == 0)
                Open = true;
            return Open;
        }

        /// <summary>
        /// Whether a use reaches the door. The client drops a use when a stop that
        /// lands after it cancels the walk into the door's use range.
        /// </summary>
        public Func<bool>? AcceptsUse { get; set; }

        public int DroppedUses { get; private set; }

        /// <summary>How near the door's middle the client would use it where the character stands.</summary>
        public float Reach { get; init; }

        /// <summary>Where the character stands, when the test says.</summary>
        public Func<Vector2>? Where { get; set; }

        /// <summary>How far the character stood from the door as each use went out.</summary>
        public List<float> UsedFrom { get; } = new();

        public float UseReach(uint doorId) => Reach;

        /// <summary>Whether an appraisal finds the door locked; null for a door the client cannot appraise.</summary>
        public bool? LockedWhenAppraised { get; init; }

        public int Appraisals { get; private set; }

        private bool? _locked;

        public bool? IsLocked(uint doorId) => _locked;

        public bool Appraise(uint doorId)
        {
            Appraisals++;
            _locked = LockedWhenAppraised;
            return LockedWhenAppraised is not null;
        }

        public void Use(uint doorId)
        {
            Uses++;
            if (Where is { } where)
                UsedFrom.Add(Vector2.Distance(where(), At));
            if (AcceptsUse?.Invoke() == false)
            {
                DroppedUses++;
                return;
            }
            if (Opens)
                _pollsUntilOpen = 10;
        }
    }

    /// <summary>
    /// A body that carries out scripted moves the way the client does, at fixed
    /// speeds, sliding off a round obstacle and reporting a move blocked once it
    /// stops making progress.
    /// </summary>
    internal sealed class SimulatedBody : INavigationWalkBody
    {
        private const float StallSeconds = 1.5f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;
        private float _stalledSeconds;

        public SimulatedBody(Vector3 position) => Position = position;

        public float RunSpeed { get; init; } = 4f;

        public float WalkSpeed { get; init; } = 1.5f;

        /// <summary>Degrees a second the body turns standing or running.</summary>
        public float TurnSpeed { get; init; } = 180f;

        /// <summary>Degrees a second the body turns while it walks.</summary>
        public float WalkTurnSpeed { get; init; } = 180f;

        /// <summary>The frame time integrated, and how much of it the body spent walking.</summary>
        public double Seconds { get; private set; }

        public double WalkingSeconds { get; private set; }

        public Vector3 Position { get; private set; }

        public float Heading { get; private set; }

        public bool Stuck { get; init; }

        /// <summary>Whether the body is in portal space.</summary>
        public bool InPortalSpace { get; set; }

        /// <summary>How many frames the player's own keys have asked the body to move.</summary>
        public long PlayerMovementInputFrames { get; set; }

        public uint CellId { get; init; }

        public (Vector2 Centre, float Radius)? Obstacle { get; init; }

        public RuntimeRouteTurning? Turning { get; init; }

        /// <summary>Whether the body is in the air, jumping or falling.</summary>
        public bool Airborne { get; set; }

        /// <summary>How many times a travel under way was stopped.</summary>
        public int TravelStops { get; private set; }

        /// <summary>Where the body stood after each frame it travelled.</summary>
        public List<Vector2> Path { get; } = [];

        /// <summary>Whether the obstacle is there, for one that can go away, such as a door that opens.</summary>
        public Func<bool>? ObstacleActive { get; init; }

        public int MovesBegun { get; private set; }

        /// <summary>How many jumps the body was asked to charge.</summary>
        public int Jumps { get; private set; }

        public bool BeginJump(float power, RuntimeMovePace? leaveAt)
        {
            Jumps++;
            return true;
        }

        /// <summary>The direction of every travel move begun, in order.</summary>
        public List<RuntimeMoveDirection> TravelDirections { get; } = [];

        /// <summary>Frames integrated since the body last travelled.</summary>
        public int StillFrames { get; private set; }

        public Vector2 Flat => new(Position.X, Position.Y);

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = new NavigationWalkBodySample(
                Position,
                Heading,
                NavBody.Player(0.6f, 1.5f),
                new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, false),
                InPortalSpace: InPortalSpace,
                CellId: CellId,
                Airborne: Airborne,
                Turning: Turning,
                PlayerMovementInputFrames: PlayerMovementInputFrames);
            return true;
        }

        public bool BeginMove(in RuntimeMoveRequest request)
        {
            MovesBegun++;
            var begun = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, request, 0f, 0f);
            if (request.Direction is RuntimeMoveDirection.TurnLeft or RuntimeMoveDirection.TurnRight)
            {
                _turn = begun;
                _turnRemaining = request.Amount;
            }
            else
            {
                _travel = begun;
                _stalledSeconds = 0f;
                TravelDirections.Add(request.Direction);
            }
            return true;
        }

        public bool StopMove(RuntimeMoveChannel channel)
        {
            if (channel == RuntimeMoveChannel.Turn && _turn.State == RuntimeScriptedMoveState.Moving)
            {
                _turn = _turn with { State = RuntimeScriptedMoveState.Stopped };
                return true;
            }
            if (channel == RuntimeMoveChannel.Travel && _travel.State == RuntimeScriptedMoveState.Moving)
            {
                _travel = _travel with { State = RuntimeScriptedMoveState.Stopped };
                TravelStops++;
                return true;
            }
            return false;
        }

        public void Interrupt()
        {
            if (_travel.State == RuntimeScriptedMoveState.Moving)
                _travel = _travel with { State = RuntimeScriptedMoveState.Interrupted };
            if (_turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Interrupted };
        }

        /// <summary>Puts the body somewhere else, as something other than the walk moving it would.</summary>
        public void Place(Vector3 position) => Position = position;

        public float HeadingErrorTo(Vector2 target)
        {
            Vector2 toward = target - Flat;
            float heading = MathF.Atan2(toward.X, toward.Y) * (180f / MathF.PI);
            return ((((heading - Heading) % 360f) + 540f) % 360f) - 180f;
        }

        public void Integrate(float seconds)
        {
            Seconds += seconds;
            bool walking = _travel.State == RuntimeScriptedMoveState.Moving && _travel.Request.Pace == RuntimeMovePace.Walk;
            if (_turn.State == RuntimeScriptedMoveState.Moving)
            {
                float step = MathF.Min(_turnRemaining, (walking ? WalkTurnSpeed : TurnSpeed) * seconds);
                _turnRemaining -= step;
                float signed = _turn.Request.Direction == RuntimeMoveDirection.TurnRight ? step : -step;
                Heading = (((Heading + signed) % 360f) + 360f) % 360f;
                if (_turnRemaining <= 0f)
                    _turn = _turn with { State = RuntimeScriptedMoveState.Completed, Covered = _turn.Request.Amount };
            }
            if (_travel.State != RuntimeScriptedMoveState.Moving)
            {
                StillFrames++;
                return;
            }
            StillFrames = 0;
            if (walking)
                WalkingSeconds += seconds;

            float speed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
            float radians = Heading * MathF.PI / 180f;
            Vector3 intended = Position + (new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * speed * seconds);
            Vector3 next = Stuck ? Position : SlideOffObstacle(intended);
            float moved = Vector2.Distance(new Vector2(next.X, next.Y), Flat);
            Position = next;
            Path.Add(Flat);
            _stalledSeconds = moved < speed * seconds * 0.25f ? _stalledSeconds + seconds : 0f;
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            if (_stalledSeconds > StallSeconds)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }

        private Vector3 SlideOffObstacle(Vector3 intended)
        {
            if (Obstacle is not { } obstacle || ObstacleActive?.Invoke() == false)
                return intended;
            Vector2 offset = new Vector2(intended.X, intended.Y) - obstacle.Centre;
            float distance = offset.Length();
            if (distance >= obstacle.Radius)
                return intended;
            Vector2 pushed = distance > 1e-4f
                ? obstacle.Centre + (offset / distance * obstacle.Radius)
                : Flat;
            return new Vector3(pushed, intended.Z);
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
