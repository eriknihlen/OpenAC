using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Navigation;

public sealed class NavGridTests
{
    private static readonly NavBody Body = new(0.48f, 1.835f, 0.4f, 0.4f);

    [Fact]
    public void AFlatFloorIsStoodOnAndItsEdgeIsTooTightToWalk()
    {
        NavGrid grid = Build(Floor(0f, 0f, 10f, 10f, 0f));

        int middle = grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f);

        Assert.True(middle >= 0);
        Assert.Equal(0f, grid.Position(middle).Z, 3);
        Assert.Equal(-1, grid.FindNode(new Vector3(0.125f, 5.125f, 0f), 0.1f, 0.5f));
    }

    [Fact]
    public void TheOpenSeaIsNoFloorAndTheShoreBesideItIsTight()
    {
        byte[] waterEverywhere = new byte[81];
        Array.Fill(waterEverywhere, (byte)(0x10 << 2));
        var sea = new TerrainSurface(new byte[81], new float[256], terrainTypes: waterEverywhere);
        var land = new TerrainSurface(new byte[81], new float[256]);

        NavGrid grid = NavGrid.Build(
            new NavGeometry(176f, 0f, 32f, [new NavTerrain(land, 0f, 0f), new NavTerrain(sea, 192f, 0f)], [], [], []),
            Body);

        Assert.True(sea.IsEntirelyWater);
        (int inland, int inlandNodes) = grid.NodesInColumn(32, 64);
        (int shore, int shoreNodes) = grid.NodesInColumn(63, 64);
        (_, int seaNodes) = grid.NodesInColumn(64, 64);
        Assert.Equal(1, inlandNodes);
        Assert.True(grid.IsClear(inland));
        Assert.Equal(1, shoreNodes);
        Assert.False(grid.IsClear(shore));
        Assert.Equal(0, seaNodes);
    }

    [Fact]
    public void GroundOverACellarIsStillFloor()
    {
        float[] heightTable = new float[256];
        Array.Fill(heightTable, 10f);
        var ground = new TerrainSurface(new byte[81], heightTable);

        NavGrid grid = NavGrid.Build(
            new NavGeometry(0f, 0f, 32f, [new NavTerrain(ground, 0f, 0f)], Floor(8f, 8f, 24f, 24f, 5f), [], []),
            Body);

        Assert.True(grid.FindNode(new Vector3(16f, 16f, 10f), 0.2f, 0.5f) >= 0);
        Assert.True(grid.FindNode(new Vector3(16f, 16f, 5f), 0.2f, 0.5f) >= 0);
    }

    [Fact]
    public void ARouteAcrossOpenFloorIsOneStraightLeg()
    {
        NavGrid grid = Build(Floor(0f, 0f, 20f, 20f, 0f));

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(15f, 12f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2, route.Legs.Count);
    }

    [Fact]
    public void AWallIsPassedThroughItsDoorway()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9f, 10f, 0f, 3f),
            .. Wall(11f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Contains(route.Path, point => point.Y > 9.9f && point.Y < 10.4f && point.X > 9f && point.X < 11f);
    }

    [Fact]
    public void ADoorwayJustWiderThanTheBodyIsPassed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.4f, 10f, 0f, 3f),
            .. Wall(10.6f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
    }

    [Fact]
    public void ADoorwayNarrowerThanTheBodyIsNotPassedAndTheRouteEndsWhereTheGoalCanBeSeenThroughIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.75f, 10f, 0f, 3f),
            .. Wall(10.25f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.All(route.Path, point => Assert.True(point.Y < 10f));
        Assert.Contains("nearest spot that can", route.Reason);
        Assert.True(route.EndsInSight);
    }

    [Fact]
    public void ARampClimbsFromTheGroundOntoADeck()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Ramp(6f, 10f, 10f, 16f, 0f, 2.5f),
            .. Floor(0f, 16f, 20f, 20f, 2.5f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 18f, 2.5f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2.5f, route.Path[^1].Z, 1);
        Assert.Contains(route.Path, point => point.Y > 11f && point.Y < 15f && point.Z > 0.3f && point.Z < 2.2f);
    }

    /// <summary>
    /// A place on top of a raised block is arrived at on the block. Without leaps nothing walks
    /// up onto it, so the route says it does not reach the goal's floor rather than ending on
    /// the ground beside it; with leaps the route jumps up and ends on top.
    /// </summary>
    [Fact]
    public void APlaceOnARaisedTopIsArrivedAtOnTheTopAndNeverOnTheGroundBesideIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Floor(9f, 9f, 13f, 13f, 1f),
            .. Wall(9f, 9f, 13f, 9f, 0f, 1f),
            .. Wall(13f, 9f, 13f, 13f, 0f, 1f),
            .. Wall(13f, 13f, 9f, 13f, 0f, 1f),
            .. Wall(9f, 13f, 9f, 9f, 0f, 1f)]);
        var top = new Vector3(11f, 11f, 1f);

        NavRoute anyFloor = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), top, arrivalRadius: 2.5f);
        NavRoute walked = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), top, arrivalRadius: 2.5f, arriveOnGoalFloor: true);
        NavRoute leapt = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), top, arrivalRadius: 2.5f, leaps: Leaper, arriveOnGoalFloor: true);

        Assert.True(anyFloor.Outcome == NavRouteOutcome.Routed && anyFloor.Path[^1].Z < 0.5f, Described(anyFloor));
        Assert.True(walked.Outcome != NavRouteOutcome.Routed, Described(walked));
        Assert.Contains("floor", walked.Reason);
        Assert.True(leapt.Outcome == NavRouteOutcome.Routed, Described(leapt));
        Assert.InRange(leapt.Path[^1].Z, 0.9f, 1.1f);
    }

    /// <summary>
    /// An object whose own collision is in the grid, such as a life stone, stands with its
    /// middle inside itself, so no spot sees that point through its walls. Told how wide the
    /// object is, the route sees its side instead and arrives beside it in sight, even with
    /// spots to keep out of, such as a portal nearby.
    /// </summary>
    [Fact]
    public void AnObjectWhoseCollisionIsInTheGridIsArrivedAtBesideItInSight()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 24f, 24f, 0f),
            .. Floor(10f, 10f, 13f, 13f, 3f),
            .. Wall(10f, 10f, 13f, 10f, 0f, 3f),
            .. Wall(13f, 10f, 13f, 13f, 0f, 3f),
            .. Wall(13f, 13f, 10f, 13f, 0f, 3f),
            .. Wall(10f, 13f, 10f, 10f, 0f, 3f)]);
        var stone = new Vector3(11.5f, 11.5f, 0f);
        NavAvoidance[] portal = [new NavAvoidance(new Vector3(3f, 20f, 0f), 1.5f)];

        NavRoute pointOnly = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), stone, arrivalRadius: 2.5f, avoid: portal);
        NavRoute beside = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), stone, arrivalRadius: 2.5f, avoid: portal, goalRadius: 2.2f);

        Assert.True(pointOnly.Outcome != NavRouteOutcome.Routed, Described(pointOnly));
        Assert.True(beside.Outcome == NavRouteOutcome.Routed && beside.Reason == "routed", Described(beside));
        Assert.True(beside.EndsInSight);
        Assert.InRange(Vector2.Distance(new Vector2(beside.Path[^1].X, beside.Path[^1].Y), new Vector2(stone.X, stone.Y)), 1.5f, 4.7f);
    }

    /// <summary>
    /// An object whose collision is one tall cylinder, such as a town sign, is seen at its side:
    /// a sight line to the very edge of its collision would graze it, so the side is taken a
    /// little way out from it.
    /// </summary>
    [Fact]
    public void ATallCylinderObjectIsArrivedAtBesideItInSight()
    {
        var sign = new NavCylinder(new Vector3(12f, 12f, 0f), 0.9f, 6.7f);
        NavGrid grid = Build([.. Floor(0f, 0f, 24f, 24f, 0f)], cylinders: [sign]);
        NavAvoidance[] portal = [new NavAvoidance(new Vector3(3f, 20f, 0f), 1.5f)];

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), sign.Base, arrivalRadius: 2f, avoid: portal, goalRadius: sign.Radius);

        Assert.True(route.Outcome == NavRouteOutcome.Routed && route.Reason == "routed", Described(route));
        Assert.True(route.EndsInSight);
    }

    /// <summary>The goal's floor is all the floor a body walks across from it, so a place on a deck is reached up a ramp.</summary>
    [Fact]
    public void APlaceOnADeckIsStillReachedUpTheRampThatJoinsIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Ramp(6f, 10f, 10f, 16f, 0f, 2.5f),
            .. Floor(0f, 16f, 20f, 20f, 2.5f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 18f, 2.5f), arrivalRadius: 1f, arriveOnGoalFloor: true);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2.5f, route.Path[^1].Z, 1);
    }

    /// <summary>
    /// A route onto an object ends on the highest floor standing on the object's own surfaces,
    /// never on the ground around it. Without leaps a block too high to step onto is not
    /// arrived at; with leaps the route jumps up and ends on its top.
    /// </summary>
    [Fact]
    public void ARouteOntoAnObjectEndsOnItsTopAndNeverOnTheGroundBesideIt()
    {
        NavTriangle[] block = [
            .. Floor(9f, 9f, 13f, 13f, 1f),
            .. Wall(9f, 9f, 13f, 9f, 0f, 1f),
            .. Wall(13f, 9f, 13f, 13f, 0f, 1f),
            .. Wall(13f, 13f, 9f, 13f, 0f, 1f),
            .. Wall(9f, 13f, 9f, 9f, 0f, 1f)];
        NavGrid grid = Build([.. Floor(0f, 0f, 20f, 20f, 0f), .. block]);
        var surfaces = new NavSurfaces(block, []);

        NavRoute walked = NavRouter.FindOnto(grid, new Vector3(3f, 3f, 0f), surfaces, arrivalRadius: 2.5f);
        NavRoute leapt = NavRouter.FindOnto(grid, new Vector3(3f, 3f, 0f), surfaces, arrivalRadius: 2.5f, leaps: Leaper);

        Assert.True(walked.Outcome != NavRouteOutcome.Routed, Described(walked));
        Assert.True(leapt.Outcome == NavRouteOutcome.Routed, Described(leapt));
        Assert.InRange(leapt.Path[^1].Z, 0.9f, 1.1f);
        Assert.InRange(leapt.Path[^1].X, 9f, 13f);
        Assert.InRange(leapt.Path[^1].Y, 9f, 13f);
    }

    /// <summary>An object with a ramp up to a deck is stood on at the deck, its highest floor, reached up the ramp.</summary>
    [Fact]
    public void AnObjectWithARampIsStoodOnAtItsDeck()
    {
        NavTriangle[] stage = [
            .. Ramp(6f, 10f, 10f, 16f, 0f, 2.5f),
            .. Floor(4f, 16f, 12f, 20f, 2.5f)];
        NavGrid grid = Build([.. Floor(0f, 0f, 20f, 10f, 0f), .. stage]);

        NavRoute route = NavRouter.FindOnto(grid, new Vector3(3f, 3f, 0f), new NavSurfaces(stage, []), arrivalRadius: 1f);

        Assert.True(route.Outcome == NavRouteOutcome.Routed, Described(route));
        Assert.Equal(2.5f, route.Path[^1].Z, 1);
    }

    /// <summary>The top of a post is a perch a body stands on, so a route onto the post ends on it.</summary>
    [Fact]
    public void ARouteOntoAPostEndsOnItsTop()
    {
        var post = new NavCylinder(new Vector3(10f, 10f, 0f), 0.9f, 1.2f);
        NavGrid grid = Build([.. Floor(2f, 2f, 20f, 20f, 0f)], cylinders: [post]);

        NavRoute route = NavRouter.FindOnto(grid, new Vector3(4f, 4f, 0f), new NavSurfaces([], [post]), arrivalRadius: 2.5f, leaps: Leaper);

        Assert.True(route.Outcome == NavRouteOutcome.Routed, Described(route));
        Assert.InRange(route.Path[^1].Z, 1.15f, 1.25f);
    }

    /// <summary>An object with nothing a body stands on, such as a thin wall, has no top to route onto.</summary>
    [Fact]
    public void AnObjectWithNothingToStandOnHasNoTop()
    {
        NavTriangle[] wall = Wall(8f, 12f, 16f, 12f, 0f, 3f);
        NavGrid grid = Build([.. Floor(0f, 0f, 20f, 20f, 0f), .. wall]);

        NavRoute route = NavRouter.FindOnto(grid, new Vector3(3f, 3f, 0f), new NavSurfaces(wall, []), arrivalRadius: 2.5f, leaps: Leaper);

        Assert.Equal(NavRouteOutcome.NoGoal, route.Outcome);
        Assert.Contains("nothing on top", route.Reason);
    }

    [Fact]
    public void ARampTooSteepToStandOnIsNotClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Ramp(6f, 10f, 10f, 12f, 0f, 4f),
            .. Floor(0f, 12f, 20f, 20f, 4f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 16f, 4f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.NoPath, route.Outcome);
    }

    [Fact]
    public void StairsWithLowRisersAreClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Stairs(6f, 10f, yStart: 10f, depth: 0.5f, rise: 0.2f, steps: 10),
            .. Floor(0f, 15f, 20f, 20f, 2f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 18f, 2f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2f, route.Path[^1].Z, 1);
    }

    [Fact]
    public void StairsWithRisersTallerThanAStepAreNotClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Stairs(6f, 10f, yStart: 10f, depth: 0.5f, rise: 0.8f, steps: 3),
            .. Floor(0f, 11.5f, 20f, 20f, 2.4f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 16f, 2.4f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.NoPath, route.Outcome);
    }

    [Fact]
    public void GroundUnderADeckAndTheDeckAreSeparateStandingPoints()
    {
        NavGrid grid = Build([.. Floor(0f, 0f, 10f, 10f, 0f), .. Floor(3f, 3f, 7f, 7f, 3f)]);

        int ground = grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f);
        int deck = grid.FindNode(new Vector3(5.125f, 5.125f, 3f), 0.1f, 0.5f);

        Assert.True(ground >= 0 && deck >= 0);
        Assert.Equal(0f, grid.Position(ground).Z, 3);
        Assert.Equal(3f, grid.Position(deck).Z, 3);
        Assert.Equal(grid.ColumnOf(ground), grid.ColumnOf(deck));
    }

    [Fact]
    public void ALowCeilingLeavesNoRoomToStandUnderIt()
    {
        NavGrid grid = Build([.. Floor(0f, 0f, 10f, 10f, 0f), .. Floor(2f, 2f, 8f, 8f, 1.2f)]);

        Assert.Equal(-1, grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f));
        Assert.True(grid.FindNode(new Vector3(5.125f, 5.125f, 1.2f), 0.1f, 0.5f) >= 0);
    }

    [Fact]
    public void ACylinderIsWalkedAround()
    {
        NavGrid grid = Build(
            Floor(0f, 0f, 20f, 8f, 0f),
            cylinders: [new NavCylinder(new Vector3(10f, 4f, 0f), 1f, 2f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(2f, 4f, 0f), new Vector3(18f, 4f, 0f), arrivalRadius: 0.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Legs.Count > 2);
        Assert.All(route.Path, point =>
            Assert.True(Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(10f, 4f)) >= 1f));
    }

    [Fact]
    public void TerrainIsStoodOnExceptWhereACellCoversIt()
    {
        var terrain = new TerrainSurface(new byte[81], new float[256]);
        NavGrid grid = NavGrid.Build(
            new NavGeometry(
                0f,
                0f,
                NavGeometry.LandblockSize,
                [new NavTerrain(terrain, 0f, 0f)],
                Floor(100f, 100f, 110f, 110f, 1f),
                [],
                []),
            Body);

        Assert.True(grid.FindNode(new Vector3(50.125f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        Assert.Equal(-1, grid.FindNode(new Vector3(105.125f, 105.125f, 0f), 0.1f, 0.5f));
        Assert.True(grid.FindNode(new Vector3(105.125f, 105.125f, 1f), 0.1f, 0.5f) >= 0);
        Assert.Equal(
            NavRouteOutcome.Routed,
            NavRouter.Find(grid, new Vector3(40f, 40f, 0f), new Vector3(60f, 70f, 0f), 1f).Outcome);
    }

    [Fact]
    public void ARegionSpanningTwoLandblocksIsWalkedAcrossTheirSeam()
    {
        NavGrid grid = NavGrid.Build(
            new NavGeometry(
                96f,
                0f,
                NavGeometry.LandblockSize,
                [
                    new NavTerrain(new TerrainSurface(new byte[81], new float[256]), 0f, 0f),
                    new NavTerrain(new TerrainSurface(new byte[81], new float[256]), 192f, 0f),
                ],
                [],
                [],
                []),
            Body);

        Assert.True(grid.FindNode(new Vector3(191.875f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        Assert.True(grid.FindNode(new Vector3(192.125f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        NavRoute route = NavRouter.Find(grid, new Vector3(150f, 50f, 0f), new Vector3(250f, 60f, 0f), 1f);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2, route.Legs.Count);
    }

    [Fact]
    public void ARegionIsCapturedFromEveryResidentLandblockItOverlaps()
    {
        var engine = new PhysicsEngine();
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 0f, 0f);
        engine.AddLandblock(0xAAB4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 192f, 0f);

        NavGeometry? both = NavGeometry.Capture(engine, 150f, 10f, 96f);
        NavGeometry? west = NavGeometry.Capture(engine, 10f, 10f, 96f);
        NavGeometry? east = NavGeometry.CaptureLandblock(engine, 0xAAB4FFFFu);

        Assert.Equal([0xA9B4FFFFu, 0xAAB4FFFFu], both!.LandblockIds.Order());
        Assert.Equal([0xA9B4FFFFu], west!.LandblockIds);
        Assert.Equal(192f, east!.OriginX);
        Assert.Equal([0xAAB4FFFFu], east.LandblockIds);
        Assert.Null(NavGeometry.Capture(engine, 1000f, 1000f, 96f));
    }

    [Fact]
    public void AGoalBehindAThinWallIsReachedFromTheSideThatCanSeeIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(10f, 0f, 10f, 16f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(6f, 4f, 0f), new Vector3(11.5f, 4f, 0f), arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Path[^1].X > 10f);
        Assert.Contains(route.Path, point => point.Y > 16f);
    }

    [Fact]
    public void AGoalBehindACounterWindowIsReachedAtTheNearestSpotThatCanSeeIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(12f, 8f, 16f, 8f, 0f, 3f),
            .. Wall(16f, 8f, 16f, 12f, 0f, 3f),
            .. Wall(16f, 12f, 12f, 12f, 0f, 3f),
            .. Wall(12f, 12f, 12f, 8f, 0f, 1f),
            .. Wall(12f, 12f, 12f, 8f, 2f, 3f)]);
        var goal = new Vector3(14.5f, 10f, 0f);

        NavRoute route = NavRouter.Find(grid, new Vector3(4f, 10f, 0f), goal, arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Path[^1].X < 12f);
        Assert.InRange(Vector2.Distance(new Vector2(route.Path[^1].X, route.Path[^1].Y), new Vector2(goal.X, goal.Y)), 2.5f, 4f);
        Assert.Contains("nearest spot that can", route.Reason);
    }

    [Fact]
    public void AGoalShutInACupboardIsReachedAtTheNearestSpotWithoutALineOfSight()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(9.5f, 9.5f, 10.5f, 9.5f, 0f, 3f),
            .. Wall(10.5f, 9.5f, 10.5f, 10.5f, 0f, 3f),
            .. Wall(10.5f, 10.5f, 9.5f, 10.5f, 0f, 3f),
            .. Wall(9.5f, 10.5f, 9.5f, 9.5f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(10f, 10f, 0f), arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Contains("without a line of sight", route.Reason);
        Assert.False(route.EndsInSight);
        Vector3 end = route.Path[^1];
        Assert.False(end.X > 9.5f && end.X < 10.5f && end.Y > 9.5f && end.Y < 10.5f);
        Assert.InRange(Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(10f, 10f)), 0.5f, 2.5f);
    }

    [Fact]
    public void AStartInsideAPassageTooNarrowToStandInIsNotTakenFromBeyondItsWalls()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 12f, 10f, 0f, 3f),
            .. Wall(0f, 10.7f, 12f, 10.7f, 0f, 3f)]);
        var start = new Vector3(6f, 10.35f, 0f);

        Assert.True(grid.FindNode(start, NavRouter.StartRadius, NavRouter.StartHeightTolerance) >= 0);
        Assert.Equal(-1, grid.FindWalkableNode(start, NavRouter.StartRadius, NavRouter.StartHeightTolerance));
        Assert.Equal(NavRouteOutcome.NoStart, NavRouter.Find(grid, start, new Vector3(16f, 16f, 0f), 1f).Outcome);
    }

    [Fact]
    public void LegsThroughADoorwayKeepTheBodyClearOfItsFrame()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.4f, 10f, 0f, 3f),
            .. Wall(10.6f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(2f, 4f, 0f), new Vector3(15f, 17f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            foreach (Vector2 frame in new[] { new Vector2(9.4f, 10f), new Vector2(10.6f, 10f) })
            {
                Assert.True(
                    DistanceToSegment(frame, route.Legs[leg - 1], route.Legs[leg]) >= grid.NearestWall - 0.05f,
                    $"leg {leg} passes within {DistanceToSegment(frame, route.Legs[leg - 1], route.Legs[leg]):0.00} m of a door frame");
            }
        }
    }

    [Fact]
    public void LegsAroundACornerKeepTheRoomTheRouteKept()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(10f, 0f, 10f, 10f, 0f, 3f),
            .. Wall(10f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(16f, 15f, 0f), new Vector3(4f, 4f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Legs.Count <= 3, $"the route turns the corner in {route.Legs.Count - 1} legs");
        var corner = new Vector2(10f, 10f);
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            float distance = DistanceToSegment(corner, route.Legs[leg - 1], route.Legs[leg]);
            Assert.True(distance >= 0.85f, $"leg {leg} passes within {distance:0.00} m of the corner");
        }
    }

    [Fact]
    public void ABodyMayBrushAWallAlongAPathButNotCrossOneOrLeaveItsFloor()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 10f, 10f, 0f),
            .. Wall(5f, 0f, 5f, 4f, 0f, 3f)]);
        int west = grid.FindNode(new Vector3(2.125f, 4.375f, 0f), 0.1f, 0.5f);
        int east = grid.FindNode(new Vector3(8.125f, 4.375f, 0f), 0.1f, 0.5f);

        Assert.False(grid.CanWalkStraight(west, east), "a walked leg keeps farther from the wall's end");
        Assert.True(grid.CanBrushAlong([grid.Position(west), new Vector3(5f, 4.375f, 0f), grid.Position(east)]));
        Assert.False(grid.CanBrushAlong([new Vector3(2.125f, 2.125f, 0f), new Vector3(8.125f, 2.125f, 0f)]), "through the wall");
        Assert.False(grid.CanBrushAlong([new Vector3(5.125f, 1f, 0f), new Vector3(5.125f, 3f, 0f)]), "pressed against the wall");
        Assert.False(grid.CanBrushAlong([new Vector3(8.125f, 8.125f, 0f), new Vector3(11f, 8.125f, 0f)]), "off the floor");
        Assert.False(grid.CanBrushAlong([new Vector3(2.125f, 8.125f, 5f), new Vector3(4.125f, 8.125f, 5f)]), "far above the floor");
    }

    [Fact]
    public void ARouteKeepsOutOfAvoidedSpots()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 4f, 10f, 0f, 3f),
            .. Wall(6f, 10f, 14f, 10f, 0f, 3f),
            .. Wall(16f, 10f, 20f, 10f, 0f, 3f)]);
        var from = new Vector3(5f, 5f, 0f);
        var to = new Vector3(5f, 15f, 0f);
        var leftDoor = new NavAvoidance(new Vector3(5f, 10f, 0f), 1.2f);
        var rightDoor = new NavAvoidance(new Vector3(15f, 10f, 0f), 1.2f);

        NavRoute direct = NavRouter.Find(grid, from, to, 1f);
        NavRoute around = NavRouter.Find(grid, from, to, 1f, [leftDoor]);
        NavRoute nowhere = NavRouter.Find(grid, from, to, 1f, [leftDoor, rightDoor]);

        Assert.Equal(NavRouteOutcome.Routed, direct.Outcome);
        Assert.DoesNotContain(direct.Path, point => point.X > 13f);
        Assert.Equal(NavRouteOutcome.Routed, around.Outcome);
        Assert.Contains(around.Path, point => point.X > 13f && MathF.Abs(point.Y - 10f) < 0.5f);
        Assert.All(around.Path, point =>
            Assert.True(Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(5f, 10f)) > 1.2f));
        Assert.Equal(NavRouteOutcome.NoPath, nowhere.Outcome);
    }

    [Fact]
    public void ARouteGoesAroundACreatureWhereThereIsRoomAndKeepsAsClearOfWalls()
    {
        NavGrid grid = Build(Floor(0f, 0f, 20f, 20f, 0f));
        var from = new Vector3(10f, 2f, 0f);
        var to = new Vector3(10f, 18f, 0f);
        var creature = new NavAvoidance(new Vector3(10f, 10f, 0f), 1.6f);

        NavRoute plain = NavRouter.Find(grid, from, to, 1f);
        NavRoute around = NavRouter.Find(grid, from, to, 1f, crowd: [creature]);

        Assert.Equal("routed", around.Reason);
        Assert.True(plain.Crowding == 0f, "a route not asked about the creature measures no crowding");
        for (int leg = 1; leg < around.Legs.Count; leg++)
        {
            float distance = DistanceToSegment(new Vector2(10f, 10f), around.Legs[leg - 1], around.Legs[leg]);
            Assert.True(distance >= creature.Radius - 0.05f, $"leg {leg} passes {distance:0.00} m from the creature");
        }
        Assert.True(around.Crowding < 0.05f, $"the route grazes the creature by {around.Crowding:0.000} m");
        Assert.True(
            around.Scrape <= plain.Scrape + 0.25f,
            $"around the creature scrapes {around.Scrape:0.00} m², straight on {plain.Scrape:0.00} m²");
    }

    [Fact]
    public void ARouteGoesThroughACreatureFillingACorridorRatherThanScrapingAlongItsWalls()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(8.5f, 0f, 8.5f, 20f, 0f, 3f),
            .. Wall(11.5f, 0f, 11.5f, 20f, 0f, 3f)]);
        var from = new Vector3(10f, 2f, 0f);
        var to = new Vector3(10f, 18f, 0f);
        var creature = new NavAvoidance(new Vector3(10f, 10f, 0f), 1.4f);

        NavRoute plain = NavRouter.Find(grid, from, to, 1f);
        NavRoute route = NavRouter.Find(grid, from, to, 1f, crowd: [creature]);

        Assert.Equal("routed", route.Reason);
        Assert.True(route.Crowding > 0f, "the route goes through the creature");
        Assert.True(
            route.Scrape <= plain.Scrape + 0.25f,
            $"the route scrapes {route.Scrape:0.00} m², straight on {plain.Scrape:0.00} m²");
        Assert.All(route.Legs, point => Assert.InRange(point.X, 9.5f, 10.5f));
    }

    [Fact]
    public void ARouteSidestepsACreatureOnTheSideWithRoomAndKeepsClearOfTheWallThere()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(6f, 0f, 6f, 20f, 0f, 3f),
            .. Wall(14f, 0f, 14f, 20f, 0f, 3f)]);
        var from = new Vector3(9f, 2f, 0f);
        var to = new Vector3(9f, 18f, 0f);
        var creature = new NavAvoidance(new Vector3(8f, 10f, 0f), 1.4f);

        NavRoute plain = NavRouter.Find(grid, from, to, 1f);
        NavRoute route = NavRouter.Find(grid, from, to, 1f, crowd: [creature]);

        Assert.Equal("routed", route.Reason);
        Assert.True(route.Crowding < 0.05f, $"the route grazes the creature by {route.Crowding:0.000} m");
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            float distance = DistanceToSegment(new Vector2(8f, 10f), route.Legs[leg - 1], route.Legs[leg]);
            Assert.True(distance >= creature.Radius - 0.05f, $"leg {leg} passes {distance:0.00} m from the creature");
        }
        Assert.All(route.Path, point => Assert.True(point.X <= 13f, $"the route comes within {14f - point.X:0.00} m of the far wall"));
        Assert.True(
            route.Scrape <= plain.Scrape + 0.25f,
            $"the sidestep scrapes {route.Scrape:0.00} m², straight on {plain.Scrape:0.00} m²");
    }

    [Fact]
    public void ACreatureWhereARouteRoundsACornerDoesNotPullTheRouteIntoTheCorner()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(10f, 0f, 10f, 10f, 0f, 3f),
            .. Wall(10f, 10f, 20f, 10f, 0f, 3f)]);
        var from = new Vector3(16f, 15f, 0f);
        var to = new Vector3(4f, 4f, 0f);
        NavRoute plain = NavRouter.Find(grid, from, to, 1f);
        Assert.True(plain.Legs.Count > 2);
        var creature = new NavAvoidance(plain.Legs[1], 1.2f);

        NavRoute route = NavRouter.Find(grid, from, to, 1f, crowd: [creature]);

        Assert.Equal("routed", route.Reason);
        var corner = new Vector2(10f, 10f);
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            float distance = DistanceToSegment(corner, route.Legs[leg - 1], route.Legs[leg]);
            Assert.True(distance >= 0.85f, $"leg {leg} passes within {distance:0.00} m of the corner");
        }
        Assert.True(
            route.Scrape <= plain.Scrape + 0.25f,
            $"the route scrapes {route.Scrape:0.00} m², without the creature {plain.Scrape:0.00} m²");
    }

    [Fact]
    public void ARouteStartingInsideAnAvoidedSpotLeavesItWithoutGoingDeeper()
    {
        NavGrid grid = Build(Floor(0f, 0f, 20f, 20f, 0f));
        var spot = new NavAvoidance(new Vector3(10f, 10f, 0f), 2f);

        NavRoute away = NavRouter.Find(grid, new Vector3(10f, 11f, 0f), new Vector3(10f, 18f, 0f), 1f, [spot]);
        NavRoute past = NavRouter.Find(grid, new Vector3(10f, 11f, 0f), new Vector3(10f, 2f, 0f), 1f, [spot]);

        Assert.Equal("routed", away.Reason);
        Assert.Equal("routed", past.Reason);
        Assert.All(past.Path, point =>
            Assert.True(Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(10f, 10f)) >= 0.85f));
    }

    [Fact]
    public void AGoalBeyondTheGridIsRoutedToTheGridsEdgeTowardIt()
    {
        NavGrid grid = Build(Floor(0f, 0f, 32f, 32f, 0f));

        NavRoute route = NavRouter.FindToward(grid, new Vector3(4f, 16f, 0f), new Vector3(200f, 16f, 0f), band: 4f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.InRange(route.Legs[^1].X, 27.5f, 32f);
        Assert.Contains("its edge toward the goal", route.Reason);
    }

    [Fact]
    public void AGoalBeyondAWallAcrossTheGridIsRoutedToTheReachableSpotNearestIt()
    {
        NavGrid grid = Build([.. Floor(0f, 0f, 32f, 32f, 0f), .. Wall(20f, 0f, 20f, 32f, 0f, 3f)]);

        NavRoute route = NavRouter.FindToward(grid, new Vector3(4f, 16f, 0f), new Vector3(200f, 16f, 0f), band: 4f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.InRange(route.Legs[^1].X, 17f, 20f);
        Assert.Contains("the reachable spot nearest it", route.Reason);
    }

    [Fact]
    public void ALandblocksCellsAreMeasuredAcrossEveryPolygon()
    {
        var engine = new PhysicsEngine();
        var room = new CellSurface(
            0xA9B40100u,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(10f, 20f, -30f),
                [1] = new(40f, 20f, -30f),
                [2] = new(40f, 50f, -30f),
                [3] = new(10f, 50f, -30f),
            },
            [[0, 1, 2, 3]]);
        var corridor = new CellSurface(
            0xA9B40101u,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(40f, 30f, -30f),
                [1] = new(250f, 30f, -30f),
                [2] = new(250f, 34f, -30f),
                [3] = new(40f, 34f, -30f),
            },
            [[0, 1, 2, 3]]);
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(new byte[81], new float[256]), [room, corridor], [], 0f, 0f);
        engine.AddLandblock(0xAAB4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 192f, 0f);

        Assert.True(NavGeometry.TryMeasureCells(engine, 0xA9B4FFFFu, out Vector2 minimum, out Vector2 maximum));
        Assert.Equal(new Vector2(10f, 20f), minimum);
        Assert.Equal(new Vector2(250f, 50f), maximum);
        Assert.False(NavGeometry.TryMeasureCells(engine, 0xAAB4FFFFu, out _, out _));
        Assert.False(NavGeometry.TryMeasureCells(engine, 0x1234FFFFu, out _, out _));
    }

    [Fact]
    public void ARegionOverDungeonCellsOutsideTheirLandblocksSquareIsCaptured()
    {
        var engine = new PhysicsEngine();
        var hall = new CellSurface(
            0x00190100u,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(70f, -330f, 6f),
                [1] = new(110f, -330f, 6f),
                [2] = new(110f, -290f, 6f),
                [3] = new(70f, -290f, 6f),
            },
            [[0, 1, 2, 3]]);
        engine.AddLandblock(0x0019FFFFu, new TerrainSurface(new byte[81], new float[256]), [hall], [], 0f, 0f);

        NavGeometry? geometry = NavGeometry.Capture(engine, 60f, -340f, 64f);

        Assert.NotNull(geometry);
        Assert.Equal([0x0019FFFFu], geometry.LandblockIds);
        Assert.Equal(2, geometry.CellTriangles.Count);
        Assert.Null(NavGeometry.Capture(engine, 400f, -340f, 64f));
    }

    [Fact]
    public void ADungeonIsCapturedFromItsOwnLandblockWithoutTerrain()
    {
        var engine = new PhysicsEngine();
        var hall = new CellSurface(
            0x00190100u,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(70f, -330f, 6f),
                [1] = new(110f, -330f, 6f),
                [2] = new(110f, -290f, 6f),
                [3] = new(70f, -290f, 6f),
            },
            [[0, 1, 2, 3]]);
        engine.AddLandblock(0x0019FFFFu, new TerrainSurface(new byte[81], new float[256]), [hall], [], 0f, 0f);
        engine.AddLandblock(0x0018FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 0f, -384f);

        NavGeometry? around = NavGeometry.Capture(engine, 60f, -340f, 64f);
        NavGeometry? dungeon = NavGeometry.CaptureDungeon(engine, 0x00190100u, 60f, -340f, 64f);

        Assert.Equal([0x0018FFFFu, 0x0019FFFFu], around!.LandblockIds.Order());
        Assert.NotEmpty(around.Terrains);
        Assert.Equal([0x0019FFFFu], dungeon!.LandblockIds);
        Assert.Empty(dungeon.Terrains);
        Assert.Equal(2, dungeon.CellTriangles.Count);
        Assert.Null(NavGeometry.CaptureDungeon(engine, 0x12340100u, 60f, -340f, 64f));
    }

    [Fact]
    public void AnObjectsFootprintIsItsCylinderOrSphereOrElseWhereItWasRegistered()
    {
        var cylinder = new ShadowEntry(1u, 0u, new Vector3(10f, 20f, 1f), Quaternion.Identity, 0.6f, ShadowCollisionType.Cylinder, 2f);
        var sphere = new ShadowEntry(2u, 0u, new Vector3(5f, 6f, 7f), Quaternion.Identity, 0.4f, ShadowCollisionType.Sphere);
        var unloadedModel = new ShadowEntry(3u, 0x01001234u, new Vector3(1f, 2f, 3f), Quaternion.Identity, 2.5f);

        Assert.Equal(new NavAvoidance(new Vector3(10f, 20f, 1f), 0.6f), NavGeometry.FootprintOf(cylinder, null));
        Assert.Equal(new NavAvoidance(new Vector3(5f, 6f, 7f), 0.4f), NavGeometry.FootprintOf(sphere, null));
        Assert.Equal(new NavAvoidance(new Vector3(1f, 2f, 3f), 2.5f), NavGeometry.FootprintOf(unloadedModel, null));
    }

    [Fact]
    public void AHopTakesARouteOffADeckDownToTheGroundBelow()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 12f, 12f, 3f),
            .. Wall(12f, 2f, 12f, 12f, 0f, 3f),
            .. Floor(12f, 2f, 24f, 12f, 0f)]);
        var from = new Vector3(6f, 7f, 3f);
        var to = new Vector3(18f, 7f, 0f);

        NavRoute walked = NavRouter.Find(grid, from, to, 1f);
        NavRoute leapt = NavRouter.Find(grid, from, to, 1f, leaps: Leaper);

        Assert.NotEqual("routed", walked.Reason);
        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.False(leap.Run);
        Assert.True(leap.Power <= 0.3f, $"the hop took {leap.Power:0.0} of full power");
        Assert.Equal(3f, leapt.Legs[leap.LegIndex - 1].Z, 1);
        Assert.Equal(0f, leapt.Legs[leap.LegIndex].Z, 1);
    }

    [Fact]
    public void AJumpTakesARouteAcrossAGapNoWalkCrosses()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 2f, 24f, 12f, 0f)]);
        var from = new Vector3(6f, 7f, 0f);
        var to = new Vector3(18f, 7f, 0f);

        NavRoute walked = NavRouter.Find(grid, from, to, 1f);
        NavRoute leapt = NavRouter.Find(grid, from, to, 1f, leaps: Leaper);

        Assert.NotEqual("routed", walked.Reason);
        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.True(leapt.Legs[leap.LegIndex - 1].X < 10f && leapt.Legs[leap.LegIndex].X > 13f);
    }

    [Fact]
    public void AJumpAtARunTakesARouteAcrossAGapNoWalkJumpClears()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(17f, 2f, 28f, 12f, 0f)]);

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(22f, 7f, 0f), 1f, leaps: Leaper);

        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.True(leap.Run);
        Assert.True(leapt.Legs[leap.LegIndex - 1].X < 10f && leapt.Legs[leap.LegIndex].X > 17f);
    }

    [Fact]
    public void AStandingJumpTakesARouteUpOntoALedge()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 12f, 12f, 0f),
            .. Wall(12f, 2f, 12f, 12f, 0f, 1.5f),
            .. Floor(12f, 2f, 24f, 12f, 1.5f)]);
        var from = new Vector3(6f, 7f, 0f);
        var to = new Vector3(18f, 7f, 1.5f);

        NavRoute walked = NavRouter.Find(grid, from, to, 1f);
        NavRoute leapt = NavRouter.Find(grid, from, to, 1f, leaps: Leaper);

        Assert.NotEqual("routed", walked.Reason);
        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.Equal(0f, leapt.Legs[leap.LegIndex - 1].Z, 1);
        Assert.Equal(1.5f, leapt.Legs[leap.LegIndex].Z, 1);
    }

    /// <summary>
    /// A leap aimed again from its own takeoff is the leap the route planned: the same pace, and
    /// about the same power.
    /// </summary>
    [Fact]
    public void ALeapAimedAgainFromItsTakeoffIsTheLeapTheRoutePlanned()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 2f, 28f, 12f, 0f)]);
        NavLeapAbility sliding = Leaper with { Physics = OrdinaryGround };
        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(22f, 7f, 0f), 1f, leaps: sliding);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);

        NavLeapAim? aim = new NavLeapFinder(grid, sliding).AimFrom(leapt.Legs[leap.LegIndex - 1], leapt.Legs[leap.LegIndex], leap.Run);

        Assert.NotNull(aim);
        Assert.Equal(leap.Run, aim.Value.Run);
        Assert.InRange(aim.Value.Power, leap.Power - 0.1f, leap.Power + 0.1f);
    }

    /// <summary>
    /// A body that stopped a step short of its takeoff, as one a walk let go of early does, is aimed
    /// from where it stands: charged harder, and kept by the same checks the route's own leaps pass.
    /// </summary>
    [Fact]
    public void ALeapAimedFromAStepShortOfItsTakeoffIsChargedHarder()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 2f, 28f, 12f, 0f)]);
        NavLeapAbility sliding = Leaper with { Physics = OrdinaryGround };
        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(22f, 7f, 0f), 1f, leaps: sliding);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 takeoff = leapt.Legs[leap.LegIndex - 1];
        Vector3 landing = leapt.Legs[leap.LegIndex];
        Vector3 away = Vector3.Normalize(new Vector3(takeoff.X - landing.X, takeoff.Y - landing.Y, 0f));

        NavLeapAim? aim = new NavLeapFinder(grid, sliding).AimFrom(takeoff + (away * 0.8f), landing, leap.Run);

        Assert.NotNull(aim);
        Assert.True(aim.Value.Power > leap.Power || aim.Value.Run != leap.Run, $"aimed at {aim.Value.Power:0.00} power against the planned {leap.Power:0.00}");
    }

    /// <summary>
    /// Where no leap at the landing a route planned is kept from the spot a body came to rest on,
    /// a leap onto another spot of that same floor is taken from where it stands, so the body
    /// jumps on rather than walk back to the takeoff the route picked.
    /// </summary>
    [Fact]
    public void ALeapOntoAnotherSpotOfTheLandingsFloorIsAimedFromWhereTheBodyStands()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 2f, 28f, 12f, 0f)]);
        NavLeapAbility sliding = Leaper with { Physics = OrdinaryGround };
        var finder = new NavLeapFinder(grid, sliding);
        var standing = new Vector3(6f, 7f, 0f);
        // Farther across the far floor than a leap from here reaches.
        var planned = new Vector3(27f, 7f, 0f);

        Assert.Null(finder.AimFrom(standing, planned, preferRun: true));
        NavLeapAim? onward = finder.AimOnward(standing, planned, preferRun: true, out Vector3 spot);

        Assert.NotNull(onward);
        Assert.True(spot.X < planned.X, $"the leap aimed onward at ({spot.X:0.0}, {spot.Y:0.0}), no nearer than the landing it replaced");
        Assert.NotNull(finder.AimFrom(standing, spot, preferRun: true));
    }

    /// <summary>
    /// A landing smaller than the body keeps no room to spare: the route's own leap onto one is
    /// kept without the check that it still lands when flown a little off, so a leap aimed again
    /// from the takeoff it was planned from is kept too, and one aimed from farther off is not.
    /// </summary>
    [Fact]
    public void ALeapOntoALandingSmallerThanTheBodyIsKeptOnlyFromItsOwnTakeoff()
    {
        NavGrid grid = Build([
            .. Floor(2f, 12f, 8f, 18f, 0f),
            .. Floor(17f, 14.75f, 25f, 15.25f, 0f)]);
        var finder = new NavLeapFinder(grid, Leaper with { Physics = OrdinaryGround });
        var takeoff = new Vector3(7f, 15f, 0f);
        var landing = new Vector3(21f, 15f, 0f);

        Assert.NotNull(finder.AimFrom(takeoff, landing, preferRun: true, atItsTakeoff: true, out _));
        Assert.Null(finder.AimFrom(takeoff, landing, preferRun: true, atItsTakeoff: false, out _));
    }

    /// <summary>
    /// A leap aimed again is kept only as the route's own leaps are: one that comes down on a strip
    /// only when flown exactly, and off it when turned a little or charged a frame more or less, is
    /// not aimed from where the body stands, while the same leap onto a wider strip is.
    /// </summary>
    [Fact]
    public void ALeapAimedAgainMustStillLandWhenFlownALittleOff()
    {
        NavLeapAim? AimOnto(float width)
        {
            NavGrid grid = Build([
                .. Floor(2f, 12f, 8f, 18f, 0f),
                .. Floor(17f, 15f - (width / 2f), 25f, 15f + (width / 2f), 0f)]);
            return new NavLeapFinder(grid, Leaper with { Physics = OrdinaryGround })
                .AimFrom(new Vector3(7f, 15f, 0f), new Vector3(21f, 15f, 0f), preferRun: true);
        }

        Assert.Null(AimOnto(2f));
        Assert.NotNull(AimOnto(3f));
    }

    /// <summary>
    /// A body sliding into a ledge at a slant is pushed along the edge and slides on, as the
    /// client's precipice slide carries it, with the share of its slide that lies along the edge;
    /// one sliding into it head on stops there. Live, long running leaps on a jump puzzle's rocks
    /// slid on 1.3 m to 1.9 m at 20 to 50 degrees off their line.
    /// </summary>
    [Fact]
    public void ASlideIntoALedgeAtASlantGoesOnAlongItsEdge()
    {
        NavGrid grid = Build([.. Floor(2f, 2f, 24f, 6f, 0f)]);
        var finder = new NavLeapFinder(grid, Leaper with { Physics = OrdinaryGround });
        int from = grid.FindNode(new Vector3(8f, 4.5f, 0f), 0.5f, 0.5f);

        Vector3 slanted = grid.Position(finder.SlidesTo(from, new Vector2(1f, 1f), 4f));
        Vector3 headOn = grid.Position(finder.SlidesTo(from, new Vector2(0f, 1f), 4f));

        Assert.True(slanted.X > 9.5f, $"the slanted slide came to rest at {slanted.X:0.00}, {slanted.Y:0.00}");
        Assert.True(slanted.Y < 6f);
        Assert.InRange(headOn.X, 7.5f, 8.5f);
        Assert.True(headOn.Y < 6f);
    }

    /// <summary>
    /// A landing slide spends its speed climbing: at a run of 7.3 m/s it rises no more than its speed
    /// squared over twice gravity, 2.7 m, however far it would slide on the flat. Live, the planner
    /// slid a leap 3.4 m up the side of a rock onto its top, and the body climbed 1.5 m of it.
    /// </summary>
    [Fact]
    public void ALandingSlideClimbsNoHigherThanItsSpeedCarriesIt()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 20f, 6f, 0f),
            .. Ramp(2f, 6f, 20f, 12f, 0f, 5.04f)]);
        var finder = new NavLeapFinder(grid, Leaper with { Physics = OrdinaryGround });
        int belowRise = grid.FindNode(new Vector3(10f, 5.5f, 0f), 0.5f, 0.5f);
        int onFlat = grid.FindNode(new Vector3(10f, 3f, 0f), 0.5f, 0.5f);

        Vector3 flat = grid.Position(finder.SlidesTo(onFlat, new Vector2(1f, 0f), 6f));
        Vector3 climbed = grid.Position(finder.SlidesTo(belowRise, new Vector2(0f, 1f), 6f));

        Assert.True(flat.X - grid.Position(onFlat).X > 5f);
        float most = 7.3f * 7.3f / (2f * 9.8f);
        Assert.True(climbed.Z > 1f && climbed.Z <= most + 0.1f, $"the slide climbed to {climbed.Z:0.00} m");
    }

    /// <summary>
    /// A leap coming down on terrain is thrown back up a little and skims on through the hop; one
    /// coming down on an object's floor slides on with no hop, as over a hundred landings on
    /// Doriathazaar's rocks did.
    /// </summary>
    [Fact]
    public void ALeapHopsOnTerrainButNotOnAnObjectsFloor()
    {
        var sliding = Leaper with { Physics = OrdinaryGround };
        var onTerrain = NavGrid.Build(
            new NavGeometry(0f, 0f, 32f, [new NavTerrain(new TerrainSurface(new byte[81], new float[256]), 0f, 0f)], [], [], []),
            Body);
        NavGrid onObject = Build([.. Floor(0f, 0f, 32f, 32f, 0f)]);
        int terrainNode = onTerrain.FindNode(new Vector3(10f, 16f, 0f), 0.5f, 0.5f);
        int objectNode = onObject.FindNode(new Vector3(10f, 16f, 0f), 0.5f, 0.5f);

        Assert.True(onTerrain.IsTerrain(terrainNode));
        Assert.False(onObject.IsTerrain(objectNode));
        Assert.True(
            new NavLeapFinder(onTerrain, sliding).Follow(new Vector3(4f, 16f, 0f), new Vector3(20f, 16f, 0f), 0.9f, true, out Vector3 terrainDown, out Vector3 terrainRest));
        Assert.True(
            new NavLeapFinder(onObject, sliding).Follow(new Vector3(4f, 16f, 0f), new Vector3(20f, 16f, 0f), 0.9f, true, out Vector3 objectDown, out Vector3 objectRest));
        float terrainSlide = terrainRest.X - terrainDown.X;
        float objectSlide = objectRest.X - objectDown.X;
        Assert.True(terrainSlide > objectSlide + 0.5f, $"slid {terrainSlide:0.00} m on terrain and {objectSlide:0.00} m on the object");
        Assert.InRange(objectSlide, sliding.Physics.GroundSlide(sliding.RunSpeed) - 0.3f, sliding.Physics.GroundSlide(sliding.RunSpeed) + 0.4f);
    }

    /// <summary>A body standing against the wall of a ledge no jump from there clears is not aimed from there.</summary>
    [Fact]
    public void NoLeapIsAimedFromWhereItCannotClearTheLedgeItJumpsUpOnto()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 12f, 12f, 0f),
            .. Wall(12f, 2f, 12f, 12f, 0f, 1.5f),
            .. Floor(12f, 2f, 24f, 12f, 1.5f)]);
        var finder = new NavLeapFinder(grid, Leaper);

        Assert.Null(finder.AimFrom(new Vector3(11.4f, 7f, 0f), new Vector3(18f, 7f, 1.5f), preferRun: true));
        Assert.NotNull(finder.AimFrom(new Vector3(6f, 7f, 0f), new Vector3(15f, 7f, 1.5f), preferRun: true));
    }

    /// <summary>
    /// How far a leap may carry comes from the body itself, how fast it runs and how long its
    /// jump keeps it in the air, not from a fixed distance: a body that runs 10.6 m/s and jumps
    /// 5.7 m high clears a gap of 18 m onto floor at the same height, and one that jumps 1.5 m does not.
    /// </summary>
    [Fact]
    public void ALeapCarriesAsFarAsTheBodysOwnRunAndJumpTakeIt()
    {
        NavGrid grid = Build(
            [
                .. Floor(2f, 10f, 8f, 16f, 5f),
                .. Floor(26f, 6f, 38f, 20f, 5f),
            ],
            size: 40f);
        var from = new Vector3(4f, 13f, 5f);
        var to = new Vector3(32f, 13f, 5f);
        var far = new NavLeapAbility(WalkSpeed: 3.12f, RunSpeed: 10.6f, FullJumpHeight: 5.69f, MaximumDrop: 12f, Physics: OrdinaryGround);
        var low = far with { FullJumpHeight = 1.5f };

        NavRoute longJump = NavRouter.Find(grid, from, to, 1.5f, leaps: far, arriveOnGoalFloor: true);
        NavRoute lowJump = NavRouter.Find(grid, from, to, 1.5f, leaps: low, arriveOnGoalFloor: true);

        Assert.True(longJump.Reason == "routed" && longJump.Leaps.Count == 1, Described(longJump));
        Assert.NotEqual("routed", lowJump.Reason);
    }

    /// <summary>
    /// A room under a roof too steep to stand on has that roof as its ceiling. A body flying high
    /// over the roof is above that ceiling, not under it, so it is in open air; a body in the room
    /// whose head reaches the roof is not.
    /// </summary>
    [Fact]
    public void ABodyAboveTheCeilingOfARoomIsInOpenAirButOneWhoseHeadMeetsItIsNot()
    {
        NavGrid grid = Build(
            [
                .. Floor(0f, 0f, 24f, 24f, 0f),
                .. Ramp(8f, 4f, 12f, 20f, 4f, 4f),
                .. Quad(new Vector3(8f, 4f, 4f), new Vector3(8f, 20f, 4f), new Vector3(10f, 20f, 9f), new Vector3(10f, 4f, 9f)),
            ],
            size: 24f);

        Assert.True(grid.IsOpenAir(new Vector3(9f, 12f, 10f)), "a body flying over the steep roof is in open air");
        Assert.False(grid.IsOpenAir(new Vector3(9f, 12f, 5f)), "a body whose head meets the roof over the room is not");
    }

    [Fact]
    public void ADropDeeperThanTheBodyMayFallIsNotTaken()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 12f, 12f, 8f),
            .. Wall(12f, 2f, 12f, 12f, 0f, 8f),
            .. Floor(12f, 2f, 24f, 12f, 0f)]);

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 8f), new Vector3(18f, 7f, 0f), 1f, leaps: Leaper);

        Assert.NotEqual("routed", leapt.Reason);
        Assert.Empty(leapt.Leaps);
    }

    [Fact]
    public void ALeapIsAimedOntoASmallBlockThatNoSetPowerComesDownOn()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13.25f, 6.5f, 14.5f, 7.75f, 1.5f),
            .. Wall(13.25f, 6.5f, 14.5f, 6.5f, -5f, 1.5f),
            .. Wall(14.5f, 6.5f, 14.5f, 7.75f, -5f, 1.5f),
            .. Wall(14.5f, 7.75f, 13.25f, 7.75f, -5f, 1.5f),
            .. Wall(13.25f, 7.75f, 13.25f, 6.5f, -5f, 1.5f)]);

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7.125f, 0f), new Vector3(13.875f, 7.125f, 1.5f), 0.5f, leaps: Leaper);

        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 landing = leapt.Legs[leap.LegIndex];
        Assert.Equal(1.5f, landing.Z, 1);
        Assert.InRange(landing.X, 13.5f, 14.25f);
        Assert.InRange(landing.Y, 6.75f, 7.5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJumpUpNearlyAsHighAsTheBodyCanJumpLeavesFromFarEnoughBackToClearTheEdge(bool slides)
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 14f, 12f, 0f),
            .. Wall(14f, 2f, 14f, 12f, 0f, 3.8f),
            .. Floor(14f, 2f, 26f, 12f, 3.8f)]);

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(20f, 7f, 3.8f), 1f, leaps: slides ? Leaper with { Physics = OrdinaryGround } : Leaper);

        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 takeoff = leapt.Legs[leap.LegIndex - 1];
        Assert.Equal(0f, takeoff.Z, 1);
        Assert.Equal(3.8f, leapt.Legs[leap.LegIndex].Z, 1);
        Assert.True(takeoff.X < 12f, $"the leap left from {14f - takeoff.X:0.00} m before the edge");
    }

    [Fact]
    public void ALeapComesDownInTheMiddleOfASmallPlatformRatherThanNearItsEdge()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 5f, 17f, 9f, 1f),
            .. Wall(13f, 5f, 17f, 5f, -5f, 1f),
            .. Wall(17f, 5f, 17f, 9f, -5f, 1f),
            .. Wall(17f, 9f, 13f, 9f, -5f, 1f),
            .. Wall(13f, 9f, 13f, 5f, -5f, 1f)]);

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(15f, 7f, 1f), 1f, leaps: Leaper);

        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 landing = leapt.Legs[leap.LegIndex];
        Assert.Equal(1f, landing.Z, 1);
        float fromCentre = Vector2.Distance(new Vector2(landing.X, landing.Y), new Vector2(15f, 7f));
        Assert.True(fromCentre <= 0.75f, $"the leap came down {fromCentre:0.00} m from the platform's centre");
    }

    [Fact]
    public void ALeapEndsWhereTheBodyComesToRestAfterItsLandingHopAndSlide()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 10f, 12f, 0f),
            .. Floor(13f, 2f, 28f, 12f, 0f)]);
        NavLeapAbility sliding = Leaper with { Physics = OrdinaryGround };

        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 7f, 0f), new Vector3(22f, 7f, 0f), 1f, leaps: sliding);

        Assert.Equal("routed", leapt.Reason);
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 takeoff = leapt.Legs[leap.LegIndex - 1];
        Vector3 rest = leapt.Legs[leap.LegIndex];
        float speed = leap.Run ? sliding.RunSpeed : sliding.WalkSpeed;
        float launch = MathF.Sqrt(2f * 9.8f * sliding.JumpHeight(leap.Power));
        float aloft = SecondsAloft(OrdinaryGround, launch);
        float comesToRest = (speed * aloft) + OrdinaryGround.Slide(speed, OrdinaryGround.FallSpeed(launch, aloft));
        float travelled = Vector2.Distance(new Vector2(takeoff.X, takeoff.Y), new Vector2(rest.X, rest.Y));
        Assert.True(
            MathF.Abs(travelled - comesToRest) <= 0.55f,
            $"the leap came to rest {travelled:0.00} m on, where its physics puts it {comesToRest:0.00} m on; {Described(leapt)}");
    }

    [Fact]
    public void ALeapOntoAPlatformTooShortToComeToRestInItsMiddleComesDownThereAndSlidesOn()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 8f, 12f, 0f),
            .. Floor(11f, 6f, 13.5f, 8f, 0f),
            .. Wall(11f, 6f, 13.5f, 6f, -5f, 0f),
            .. Wall(13.5f, 6f, 13.5f, 8f, -5f, 0f),
            .. Wall(13.5f, 8f, 11f, 8f, -5f, 0f),
            .. Wall(11f, 8f, 11f, 6f, -5f, 0f)]);
        NavLeapAbility sliding = Leaper with { Physics = OrdinaryGround };

        NavRoute leapt = NavRouter.Find(grid, new Vector3(5f, 7f, 0f), new Vector3(12.25f, 7f, 0f), 1f, leaps: sliding);

        Assert.True(leapt.Reason == "routed", Described(leapt));
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 rest = leapt.Legs[leap.LegIndex];
        // Past the platform's middle at 12.25: coming down on a platform rather than terrain, the
        // body slides on without a landing hop.
        Assert.InRange(rest.X, 12.3f, 13.5f);
    }

    [Fact]
    public void ALeapOntoAPlatformItWouldSlideAcrossComesDownInItsMiddleAndSlidesToItsFarEdge()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 8f, 12f, 0f),
            .. Floor(13f, 6f, 16f, 8f, 1f),
            .. Wall(13f, 6f, 16f, 6f, -5f, 1f),
            .. Wall(16f, 6f, 16f, 8f, -5f, 1f),
            .. Wall(16f, 8f, 13f, 8f, -5f, 1f),
            .. Wall(13f, 8f, 13f, 6f, -5f, 1f)]);
        NavLeapAbility onIce = Leaper with { FullJumpHeight = 8f, Physics = OrdinaryGround with { Friction = 0.5f } };

        NavRoute leapt = NavRouter.Find(grid, new Vector3(5f, 7f, 0f), new Vector3(14.5f, 7f, 1f), 1f, leaps: onIce);

        Assert.True(leapt.Reason == "routed", Described(leapt));
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Vector3 rest = leapt.Legs[leap.LegIndex];
        Assert.Equal(1f, rest.Z, 1);
        Assert.True(rest.X > 15.25f, $"the leap came to rest {rest.X - 14.5f:0.00} m past the platform's middle");
    }

    [Fact]
    public void ALeapComesDownOnASteepRoofItsSideMeetsBeforeItsFeet()
    {
        NavGrid grid = Build([
            .. Floor(2f, 2f, 20f, 10f, 0f),
            .. Wall(2f, 10f, 20f, 10f, 0f, 4.94f),
            .. Ramp(2f, 10f, 20f, 12.5f, 4.94f, 7.4f),
            .. Floor(2f, 12.5f, 20f, 14.5f, 7.4f),
            .. Ramp(2f, 14.5f, 20f, 17f, 7.4f, 4.94f),
            .. Wall(2f, 17f, 20f, 17f, 0f, 4.94f)]);
        NavLeapAbility climber = Leaper with { FullJumpHeight = 6.42f, Physics = OrdinaryGround with { StepSeconds = 0.035f } };

        NavRoute leapt = NavRouter.Find(grid, new Vector3(11f, 5f, 0f), new Vector3(11f, 11f, 5.92f), 1f, leaps: climber);

        Assert.True(leapt.Reason == "routed", Described(leapt));
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.InRange(leapt.Legs[leap.LegIndex].Z, 4.9f, 7.5f);
    }

    [Theory]
    [InlineData(3.12f, 2.5f, 0.05f)]
    [InlineData(3.12f, 4f, 0.035f)]
    [InlineData(9.5f, 3.8f, 0.0667f)]
    public void AJumpComesToRestWhereThePhysicsBodyItIsPlannedForComesToRest(float speed, float height, float step)
    {
        NavLeapPhysics physics = OrdinaryGround with { StepSeconds = step };
        float launch = MathF.Sqrt(2f * 9.8f * height);
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            Elasticity = physics.Elasticity,
            Friction = physics.Friction,
            Velocity = new Vector3(speed, 0f, launch),
        };

        for (int steps = 0; steps < 2000 && body.Velocity != Vector3.Zero; steps++)
        {
            bool wasInContact = body.InContact;
            bool wasOnWalkable = body.OnWalkable;
            body.calc_acceleration();
            body.UpdatePhysicsInternal(step);
            bool down = body.Position.Z <= 0f;
            if (down)
            {
                body.Position = body.Position with { Z = 0f };
                body.TransientState |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
            }
            else
            {
                body.TransientState &= ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
            }
            body.calc_acceleration();
            PhysicsObjUpdate.HandleAllCollisions(body, down, Vector3.UnitZ, wasInContact, wasOnWalkable, down);
        }

        float aloft = SecondsAloft(physics, launch);
        float comesToRest = (speed * aloft) + physics.Slide(speed, physics.FallSpeed(launch, aloft));
        Assert.Equal(Vector3.Zero, body.Velocity);
        Assert.InRange(body.Position.X, comesToRest - (speed * step), comesToRest + (speed * step));
    }

    /// <summary>A route's reason and leaps, for a failed assertion to show.</summary>
    private static string Described(NavRoute route) =>
        $"{route.Reason}; leaps: "
        + string.Join(", ", route.Leaps.Select(leap =>
            $"{route.Legs[leap.LegIndex - 1]} to {route.Legs[leap.LegIndex]} at power {leap.Power:0.00} {(leap.Run ? "run" : "walk")}"));

    /// <summary>How long a jump launched upward at <paramref name="launch"/> stays in the air before it comes back down to its takeoff's height.</summary>
    private static float SecondsAloft(NavLeapPhysics physics, float launch) =>
        physics.StepSeconds + ((launch + MathF.Sqrt((launch * launch) + (2f * 9.8f * launch * physics.StepSeconds))) / 9.8f);

    /// <summary>Physics that moves a body in steps of 1/20 s, throws it back up at 5% of the speed it lands at, and takes 95% of its speed along the ground away each second.</summary>
    private static readonly NavLeapPhysics OrdinaryGround = new(StepSeconds: 0.05f, Elasticity: 0.05f, Friction: 0.95f);

    /// <summary>A body that walks at 3.12 m/s, runs at 7.3 m/s, jumps 4.2 m high at full power and may drop 5 m.</summary>
    private static readonly NavLeapAbility Leaper = new(WalkSpeed: 3.12f, RunSpeed: 7.3f, FullJumpHeight: 4.2f, MaximumDrop: 5f);

    private static float DistanceToSegment(Vector2 point, Vector3 from, Vector3 to)
    {
        var start = new Vector2(from.X, from.Y);
        Vector2 along = new Vector2(to.X, to.Y) - start;
        float lengthSquared = along.LengthSquared();
        float t = lengthSquared > 0f ? Math.Clamp(Vector2.Dot(point - start, along) / lengthSquared, 0f, 1f) : 0f;
        return Vector2.Distance(point, start + (along * t));
    }

    private static NavGrid Build(
        NavTriangle[] triangles,
        NavCylinder[]? cylinders = null,
        float size = 32f,
        NavTriangle[]? shells = null) =>
        NavGrid.Build(
            new NavGeometry(0f, 0f, size, [], triangles, [], cylinders ?? [], shells),
            Body);

    [Fact]
    public void ABodyStandsOnTopOfAPost()
    {
        NavGrid grid = Build(
            [.. Floor(2f, 2f, 20f, 20f, 0f)],
            cylinders: [new NavCylinder(new Vector3(10f, 10f, 0f), 0.9f, 1.2f)]);

        int top = grid.FindNode(new Vector3(10f, 10f, 1.2f), 0.5f, 0.2f);

        Assert.True(top >= 0, "no node stands on the post");
        Assert.InRange(grid.Position(top).Z, 1.15f, 1.25f);
    }

    /// <summary>
    /// Small platforms with gaps between them are climbed by jumping from one to the
    /// next, the way a jump puzzle is built. A body has room to stand on each, so every
    /// jump is one it can fly a little off and still land.
    /// </summary>
    [Fact]
    public void AStackOfSmallPlatformsIsClimbedByJumping()
    {
        NavGrid grid = Build(
            [
                .. Floor(2f, 2f, 8f, 20f, 0f),
                .. Floor(10f, 8f, 12.5f, 10.5f, 1.4f),
                .. Floor(14f, 8f, 16.5f, 10.5f, 2.8f),
                .. Floor(18f, 8f, 20.5f, 10.5f, 4.2f),
            ],
            size: 32f);
        NavLeapAbility climber = Leaper with
        {
            FullJumpHeight = 6.42f,
            Physics = OrdinaryGround with { StepSeconds = 0.035f },
        };

        NavRoute leapt = NavRouter.Find(
            grid,
            new Vector3(5f, 9f, 0f),
            new Vector3(19.25f, 9.25f, 4.2f),
            1f,
            leaps: climber);

        Assert.True(leapt.Reason == "routed", Described(leapt));
        // A running leap onto a platform, rather than terrain, comes down with no hop to carry it
        // off again, so it may take the top in one leap rather than hopping up the stack.
        Assert.NotEmpty(leapt.Leaps);
        Assert.InRange(leapt.Legs[^1].Z, 4.1f, 4.3f);
    }

    /// <summary>
    /// A candelabra is a hand's width across and taller than a body. Its cap is floor a
    /// body perches on, and a jump aimed at it lands there, since a body that comes down
    /// on a cap has nowhere to slide.
    /// </summary>
    [Fact]
    public void ALeapReachesACapNoWiderThanAHand()
    {
        NavGrid grid = Build(
            [.. Floor(2f, 2f, 20f, 20f, 0f)],
            cylinders: [new NavCylinder(new Vector3(14f, 10f, 0f), 0.2f, 2.2f)]);
        var cap = new Vector3(14f, 10f, 2.2f);

        NavLeapAbility climber = Leaper with
        {
            FullJumpHeight = 6.42f,
            Physics = OrdinaryGround with { StepSeconds = 0.035f },
        };

        NavRoute walked = NavRouter.Find(grid, new Vector3(6f, 10f, 0f), cap, 0.5f);
        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 10f, 0f), cap, 0.5f, leaps: climber);

        Assert.True(walked.Outcome != NavRouteOutcome.Routed, Described(walked));
        Assert.True(leapt.Reason == "routed", Described(leapt));
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.InRange(leapt.Legs[leap.LegIndex].Z, 2.1f, 2.3f);
    }

    /// <summary>
    /// A dome's top curves away, so a body stands over its middle and not out on its
    /// flank, where the surface is steeper than a floor.
    /// </summary>
    /// <summary>
    /// The top of a post is floor a body perches on, too small to walk clear of. A jump
    /// reaches it and a route may end there; a walk never crosses it.
    /// </summary>
    [Fact]
    public void ALeapReachesAPostTopThatAWalkCannot()
    {
        NavGrid grid = Build(
            [.. Floor(2f, 2f, 20f, 20f, 0f)],
            cylinders: [new NavCylinder(new Vector3(14f, 10f, 0f), 0.35f, 1.4f)]);
        var post = new Vector3(14f, 10f, 1.4f);

        NavRoute walked = NavRouter.Find(grid, new Vector3(6f, 10f, 0f), post, 0.5f);
        NavRoute leapt = NavRouter.Find(grid, new Vector3(6f, 10f, 0f), post, 0.5f, leaps: Leaper);

        Assert.True(walked.Outcome != NavRouteOutcome.Routed, Described(walked));
        Assert.True(leapt.Reason == "routed", Described(leapt));
        NavRouteLeap leap = Assert.Single(leapt.Leaps);
        Assert.InRange(leapt.Legs[leap.LegIndex].Z, 1.3f, 1.5f);
    }

    [Fact]
    public void ABodyStandsOnADomesMiddleAndNotItsFlank()
    {
        NavGrid grid = Build(
            [.. Floor(2f, 2f, 20f, 20f, 0f)],
            cylinders: [new NavCylinder(new Vector3(10f, 10f, 0f), 1.6f, 3.2f, Dome: true)]);

        int middle = grid.FindNode(new Vector3(10f, 10f, 3.2f), 0.3f, 0.2f);
        int flank = grid.FindNode(new Vector3(11.5f, 10f, 2.2f), 0.2f, 0.4f);

        Assert.True(middle >= 0, "no node stands on the dome's middle");
        Assert.InRange(grid.Position(middle).Z, 3.0f, 3.25f);
        Assert.True(flank < 0, "a node stands out on the dome's flank");
    }

    /// <summary>
    /// A building's shell carries a plate where its ground floor is, and that plate
    /// runs on over the stairwell down to its cellar, where the cells leave a hole.
    /// Inside the building the shell is no floor and no ceiling: the cells' own floors,
    /// stairs and ceilings are what a body meets.
    /// </summary>
    [Fact]
    public void ABuildingsShellNeitherFloorsNorRoofsItsOwnRooms()
    {
        NavTriangle[] cells =
        [
            .. Floor(2f, 2f, 20f, 20f, 0f),
            .. Floor(2f, 2f, 20f, 20f, 8f),
            .. Ramp(6f, 6f, 14f, 12f, 0f, 2.6f),
        ];
        NavGrid grid = Build(
            cells,
            size: 32f,
            shells: [.. Floor(2f, 2f, 20f, 20f, 3f), .. Floor(2f, 2f, 20f, 20f, 12f)]);

        int onTheStairs = grid.FindNode(new Vector3(10f, 9f, 1.3f), 0.5f, 0.5f);
        int onThePlate = grid.FindNode(new Vector3(10f, 9f, 3f), 0.5f, 0.2f);
        int onTheRoof = grid.FindNode(new Vector3(10f, 9f, 12f), 0.5f, 0.2f);

        Assert.True(onTheStairs >= 0, "no node stands on the stairs under the shell's plate");
        Assert.True(onThePlate < 0, "a node stands on the shell's plate inside the building");
        Assert.True(onTheRoof >= 0, "no node stands on the shell above the building's rooms");
    }

    private static NavTriangle[] Floor(float x0, float y0, float x1, float y1, float z) =>
        Quad(new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z));

    private static NavTriangle[] Ramp(float x0, float y0, float x1, float y1, float zAtY0, float zAtY1) =>
        Quad(new Vector3(x0, y0, zAtY0), new Vector3(x1, y0, zAtY0), new Vector3(x1, y1, zAtY1), new Vector3(x0, y1, zAtY1));

    private static NavTriangle[] Wall(float x0, float y0, float x1, float y1, float bottom, float top) =>
        Quad(new Vector3(x0, y0, bottom), new Vector3(x1, y1, bottom), new Vector3(x1, y1, top), new Vector3(x0, y0, top));

    private static NavTriangle[] Stairs(float x0, float x1, float yStart, float depth, float rise, int steps)
    {
        var triangles = new List<NavTriangle>();
        for (int step = 0; step < steps; step++)
        {
            float y = yStart + (step * depth);
            triangles.AddRange(Wall(x0, y, x1, y, step * rise, (step + 1) * rise));
            triangles.AddRange(Floor(x0, y, x1, y + depth, (step + 1) * rise));
        }
        return [.. triangles];
    }

    private static NavTriangle[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        [new NavTriangle(a, b, c), new NavTriangle(a, c, d)];
}
