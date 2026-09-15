using AcDream.DrakBot.Navigation;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class DungeonPathfinderTests
{
    private const uint Block = 0x01A9_0000u;

    /// <summary>A cell at (east, north) in meters, with doorway neighbours by low cell number.</summary>
    private static PluginDungeonCell Cell(uint low, double eastMeters, double northMeters, double zUnits = 0d, params uint[] neighbors) =>
        new(Block | low, eastMeters / 240d, northMeters / 240d, zUnits / 240d, neighbors.Select(n => Block | n).ToArray());

    /// <summary>
    /// A square loop of four rooms (1-2-3-4-1) with a dead-end corridor off
    /// room 2 (5, then 6) and a pit off room 3 reachable only by a drop (7).
    /// </summary>
    private static Dictionary<uint, PluginDungeonCell> Loop() => DungeonPathfinder.Graph(
    [
        Cell(0x100, 0d, 0d, 0d, 0x101, 0x103),
        Cell(0x101, 20d, 0d, 0d, 0x100, 0x102, 0x104),
        Cell(0x102, 20d, 20d, 0d, 0x101, 0x103, 0x106),
        Cell(0x103, 0d, 20d, 0d, 0x102, 0x100),
        Cell(0x104, 40d, 0d, 0d, 0x101, 0x105),
        Cell(0x105, 60d, 0d, 0d, 0x104),
        Cell(0x106, 20d, 21d, -30d, 0x102),
    ]);

    [Fact]
    public void FindsTheShortestWayThroughDoorwaysAndNeverThroughADrop()
    {
        Dictionary<uint, PluginDungeonCell> graph = Loop();

        List<uint> path = DungeonPathfinder.FindPath(graph, Block | 0x100, Block | 0x102);
        Assert.Equal(3, path.Count);
        Assert.Equal(Block | 0x100, path[0]);
        Assert.Equal(Block | 0x102, path[2]);

        Assert.Empty(DungeonPathfinder.FindPath(graph, Block | 0x100, Block | 0x106));
        Assert.True(DungeonPathfinder.IsDropEdge(graph[Block | 0x102], graph[Block | 0x106]));
        Assert.False(DungeonPathfinder.IsDropEdge(graph[Block | 0x100], graph[Block | 0x101]));
    }

    [Fact]
    public void ADoorwaySixMetresAboveTheFloorIsAHoleNotAWayUp()
    {
        // Two cells whose centres are far enough apart on the map for a
        // gentle slope, but whose shared doorway sits straight above the
        // lower cell's floor: a hole in the ceiling to the room above.
        PluginDungeonCell corridor = Cell(0x200, 0d, 0d, -12d, 0x201) with
        {
            Doorways = [new PluginDungeonDoorway(Block | 0x201, 3d / 240d, 0d, -6d / 240d)],
        };
        PluginDungeonCell room = Cell(0x201, 12d, 0d, -6d, 0x200);
        Assert.True(DungeonPathfinder.IsDropEdge(corridor, room));
        Assert.True(DungeonPathfinder.IsDropEdge(room, corridor));

        // The same rise over a ramp's length, doorway at the ramp's foot: walkable.
        PluginDungeonCell foot = Cell(0x202, 0d, 0d, -12d, 0x203) with
        {
            Doorways = [new PluginDungeonDoorway(Block | 0x203, 2d / 240d, 0d, -12d / 240d)],
        };
        PluginDungeonCell ramp = Cell(0x203, 10d, 0d, -9d, 0x202);
        Assert.False(DungeonPathfinder.IsDropEdge(foot, ramp));
    }

    [Fact]
    public void HazardsAreRoutedAroundUnlessTheyAreTheGoal()
    {
        Dictionary<uint, PluginDungeonCell> graph = Loop();
        var hazards = new HashSet<uint> { Block | 0x101 };

        List<uint> around = DungeonPathfinder.FindPath(graph, Block | 0x100, Block | 0x102, hazards);
        Assert.Equal([Block | 0x100, Block | 0x103, Block | 0x102], around);

        List<uint> into = DungeonPathfinder.FindPath(graph, Block | 0x100, Block | 0x101, hazards);
        Assert.Equal([Block | 0x100, Block | 0x101], into);

        Assert.Equal(Block | 0x100, DungeonPathfinder.NearestSafeCell(graph, Block | 0x101, hazards));
    }

    [Fact]
    public void NearestCellPrefersTheSameFloor()
    {
        Dictionary<uint, PluginDungeonCell> graph = Loop();
        // Standing near room 3 but 30 units down: the pit under it is the same floor.
        var low = new PluginNavigationPosition(0u, 21d / 240d, 20d / 240d, -30d / 240d, 0f, false);
        Assert.Equal(Block | 0x106, DungeonPathfinder.NearestCell(graph, low));
        Assert.Equal(Block | 0x102, DungeonPathfinder.NearestCell(graph, low, useElevation: false));
    }

    [Fact]
    public void ARampsMiddleDoorwayIsKeptEvenThoughItLiesOnTheLineOnTheMap()
    {
        // Three cells due north of one another: the floor, a landing only 1 m up, then the room 6 m up
        // (the climb is all in the second half). On the map every doorway between them is on one straight line.
        Dictionary<uint, PluginDungeonCell> ramp = DungeonPathfinder.Graph(
        [
            Cell(0x100, 0d, 0d, 0d, 0x101),
            Cell(0x101, 0d, 20d, 1d, 0x100, 0x102),
            Cell(0x102, 0d, 40d, 6d, 0x101),
        ]);
        var top = new PluginNavigationPosition(0u, 0d, 40d / 240d, 6d / 240d, 0f, false);

        Route route = DungeonPathfinder.BuildRoute(ramp, [Block | 0x100, Block | 0x101, Block | 0x102], top);

        // The flat version of the same walk collapses to the destination alone;
        // the ramp keeps its doorways, because a walk from 0 m straight to 6 m
        // goes through the floor.
        Assert.True(route.Waypoints.Count >= 2, $"{route.Waypoints.Count} steps");
        Assert.Contains(route.Waypoints, w => w.Elevation * 240d > 1.5d && w.Elevation * 240d < 5d);
        Dictionary<uint, PluginDungeonCell> flat = DungeonPathfinder.Graph(
        [
            Cell(0x100, 0d, 0d, 0d, 0x101),
            Cell(0x101, 0d, 20d, 0d, 0x100, 0x102),
            Cell(0x102, 0d, 40d, 0d, 0x101),
        ]);
        Route straight = DungeonPathfinder.BuildRoute(flat, [Block | 0x100, Block | 0x101, Block | 0x102], top with { Elevation = 0d });
        Assert.True(straight.Waypoints.Count < route.Waypoints.Count);
    }

    [Fact]
    public void ARouteGoesThroughDoorwayPointsAndEndsAtTheDestination()
    {
        Dictionary<uint, PluginDungeonCell> graph = Loop();
        var destination = new PluginNavigationPosition(0u, 21d / 240d, 19d / 240d, 0d, 0f, false);
        List<uint> path = DungeonPathfinder.FindPath(graph, Block | 0x100, Block | 0x102);

        Route route = DungeonPathfinder.BuildRoute(graph, path, destination);

        Assert.Equal(RouteMode.Once, route.Mode);
        Assert.All(route.Waypoints, w => Assert.Equal(WaypointKind.Point, w.Kind));
        Waypoint last = route.Waypoints[^1];
        Assert.Equal(destination.EastWest, last.EastWest);
        Assert.Equal(destination.NorthSouth, last.NorthSouth);
        // The corner at room 2 is kept: the doorway midpoint between rooms 1 and 2 lies well off the straight line.
        Assert.Contains(route.Waypoints, w => Math.Abs(w.EastWest * 240d - 10d) < 0.01d && Math.Abs(w.NorthSouth) < 0.01d);
    }

    [Fact]
    public void APatrolStripsTheDeadEndAndWalksTheLoopRound()
    {
        Dictionary<uint, PluginDungeonCell> graph = Loop();

        HashSet<uint> main = DungeonPathfinder.MainRouteCells(graph, Block | 0x100);
        // The loop is under the fallback size, so the reachable set stands; the pit is behind a drop.
        Assert.DoesNotContain(Block | 0x106, main);
        Assert.Contains(Block | 0x105, main);

        Route patrol = DungeonPathfinder.BuildPatrolRoute(graph, Block | 0x100);
        Assert.Equal(RouteMode.Loop, patrol.Mode);
        Assert.NotEmpty(patrol.Waypoints);
        // Every corridor is walked: the far end of the spur is reached.
        Assert.Contains(patrol.Waypoints, w => w.EastWest * 240d > 49d);
    }

    /// <summary>A ring of twelve rooms with a two-room spur off each of three of them.</summary>
    private static Dictionary<uint, PluginDungeonCell> RingWithSpurs()
    {
        var cells = new List<PluginDungeonCell>();
        for (uint index = 0; index < 12; index++)
        {
            double angle = index * Math.PI * 2d / 12d;
            uint previous = (index + 11) % 12;
            uint next = (index + 1) % 12;
            var neighbors = new List<uint> { 0x100 + previous, 0x100 + next };
            if (index % 4 == 0)
                neighbors.Add(0x200 + index);
            cells.Add(Cell(0x100 + index, Math.Cos(angle) * 60d, Math.Sin(angle) * 60d, 0d, neighbors.ToArray()));
            if (index % 4 == 0)
            {
                cells.Add(Cell(0x200 + index, Math.Cos(angle) * 80d, Math.Sin(angle) * 80d, 0d, 0x100 + index, 0x300 + index));
                cells.Add(Cell(0x300 + index, Math.Cos(angle) * 100d, Math.Sin(angle) * 100d, 0d, 0x200 + index));
            }
        }
        return DungeonPathfinder.Graph(cells);
    }

    [Fact]
    public void APatrolStartedInASpurLeadsInAlongTheDoorwaysAndLoopsWithoutThem()
    {
        // Standing at the far end of the spur off room 0 (at 100 m east): the way in runs 0x300 -> 0x200 -> 0x100, then the ring.
        Route patrol = DungeonPathfinder.BuildPatrolRoute(RingWithSpurs(), Block | 0x300);
        Assert.True(patrol.LoopStart > 0);
        Assert.True(patrol.LoopStart < patrol.Waypoints.Count);
        // The lead-in comes in along the spur: every lead-in step lies east of the ring.
        for (int index = 0; index < patrol.LoopStart; index++)
            Assert.InRange(patrol.Waypoints[index].EastWest * 240d, 60d, 100d);
        // The ring proper never goes back out a spur.
        for (int index = patrol.LoopStart; index < patrol.Waypoints.Count; index++)
            Assert.InRange(Math.Sqrt(Math.Pow(patrol.Waypoints[index].EastWest * 240d, 2) + Math.Pow(patrol.Waypoints[index].NorthSouth * 240d, 2)), 0d, 61d);

        // Following it: after the last step the cursor returns to the loop start, not the lead-in.
        var follower = new RouteFollower(patrol, RouteMode.Loop);
        follower.Reset();
        for (int step = 0; step < patrol.Waypoints.Count - 1; step++)
            follower.Complete();
        Assert.Equal(patrol.Waypoints.Count - 1, follower.CurrentIndex);
        follower.Complete();
        Assert.Equal(patrol.LoopStart, follower.CurrentIndex);
    }

    [Fact]
    public void RejoiningAPatrolFromAnotherRoomLeadsInByTheDoorwaysAndRotatesTheLoop()
    {
        Dictionary<uint, PluginDungeonCell> graph = RingWithSpurs();
        Route patrol = DungeonPathfinder.BuildPatrolRoute(graph, Block | 0x100);
        Assert.Equal(0, patrol.LoopStart);
        // Walking to step 4 of the ring, a fight dragged the character out to the end of the spur off ring cell 4.
        int index = 3;
        PluginDungeonCell spurEnd = graph[Block | 0x304];
        var position = new PluginNavigationPosition(spurEnd.CellId, spurEnd.EastWest, spurEnd.NorthSouth, spurEnd.Elevation, 0f, IsOutdoor: false);

        Route? rejoined = DungeonPathfinder.Rejoin(graph, patrol, index, position);

        Assert.NotNull(rejoined);
        // The lead-in comes back in along the spur: its first doorway is out beyond the ring...
        Assert.True(rejoined.LoopStart >= 2);
        Assert.True(Math.Sqrt(Math.Pow(rejoined.Waypoints[0].EastWest * 240d, 2) + Math.Pow(rejoined.Waypoints[0].NorthSouth * 240d, 2)) > 61d);
        // ...then the loop continues from the step that was being walked to, and wraps through the ones before it.
        Assert.Equal(patrol.Waypoints.Count, rejoined.Waypoints.Count - rejoined.LoopStart);
        Assert.Equal(patrol.Waypoints[index], rejoined.Waypoints[rejoined.LoopStart]);
        Assert.Equal(patrol.Waypoints[index - 1], rejoined.Waypoints[^1]);
        Assert.Equal(RouteMode.Loop, rejoined.Mode);

        // Already in the step's cell: nothing to route around.
        PluginDungeonCell stepCell = graph[DungeonPathfinder.NearestCell(graph, patrol.Waypoints[index].ToPosition())];
        var inRoom = new PluginNavigationPosition(stepCell.CellId, stepCell.EastWest, stepCell.NorthSouth, 0d, 0f, IsOutdoor: false);
        Assert.Null(DungeonPathfinder.Rejoin(graph, patrol, index, inRoom));
    }

    [Fact]
    public void ARouteGoesThroughTheDoorwaysThemselvesWhenTheHostKnowsThem()
    {
        // Two rooms whose model anchors sit off in a corner; the doorway between them is at (10 E, 8 N).
        PluginDungeonCell a = Cell(0x100, 0d, 0d, 0d, 0x101) with
        {
            Doorways = [new PluginDungeonDoorway(Block | 0x101, 10d / 240d, 8d / 240d, 0d)],
        };
        PluginDungeonCell b = Cell(0x101, 20d, 20d, 0d, 0x100);
        Dictionary<uint, PluginDungeonCell> graph = DungeonPathfinder.Graph([a, b]);
        var destination = new PluginNavigationPosition(0u, 20d / 240d, 20d / 240d, 0d, 0f, false);

        Route route = DungeonPathfinder.BuildRoute(graph, [Block | 0x100, Block | 0x101], destination);
        Assert.Equal(2, route.Waypoints.Count);
        Assert.Equal(10d, route.Waypoints[0].EastWest * 240d, 3);
        Assert.Equal(8d, route.Waypoints[0].NorthSouth * 240d, 3);
    }

    [Fact]
    public void ALargeLoopWithSpursPatrolsOnlyTheLoop()
    {
        // A ring of twelve rooms with a two-room spur off each of three of them.
        var cells = new List<PluginDungeonCell>();
        for (uint index = 0; index < 12; index++)
        {
            double angle = index * Math.PI * 2d / 12d;
            uint previous = (index + 11) % 12;
            uint next = (index + 1) % 12;
            var neighbors = new List<uint> { 0x100 + previous, 0x100 + next };
            if (index % 4 == 0)
                neighbors.Add(0x200 + index);
            cells.Add(Cell(0x100 + index, Math.Cos(angle) * 60d, Math.Sin(angle) * 60d, 0d, neighbors.ToArray()));
            if (index % 4 == 0)
            {
                cells.Add(Cell(0x200 + index, Math.Cos(angle) * 80d, Math.Sin(angle) * 80d, 0d, 0x100 + index, 0x300 + index));
                cells.Add(Cell(0x300 + index, Math.Cos(angle) * 100d, Math.Sin(angle) * 100d, 0d, 0x200 + index));
            }
        }
        Dictionary<uint, PluginDungeonCell> graph = DungeonPathfinder.Graph(cells);

        HashSet<uint> main = DungeonPathfinder.MainRouteCells(graph, Block | 0x100);
        Assert.Equal(12, main.Count);
        Route patrol = DungeonPathfinder.BuildPatrolRoute(graph, Block | 0x100);
        Assert.All(patrol.Waypoints, w => Assert.InRange(Math.Sqrt(w.EastWest * w.EastWest + w.NorthSouth * w.NorthSouth) * 240d, 0d, 61d));
    }
}
