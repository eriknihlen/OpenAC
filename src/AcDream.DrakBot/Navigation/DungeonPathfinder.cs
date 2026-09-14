using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Plans through a dungeon on its cell graph, the way RynthAi's dungeon
/// pathfinder does. Adjacency comes from the cells' doorways (the client's
/// portal records), never from what a cell can see, so no edge cuts through
/// a wall; an edge that climbs or falls steeper than <see cref="DropAngleDegrees"/>
/// is a drop the character cannot walk and is never taken. A path is walked
/// through doorway midpoints - a point 30% of the way to the next cell to
/// square up on the doorway, then the midpoint itself - and finished at the
/// exact destination. A patrol is a closed walk over the dungeon's main
/// route: the cells that sit on a cycle or between junctions, with dead-end
/// spurs stripped, covering every corridor once and taking loop-closing
/// edges so a loop is walked round rather than in and out.
/// </summary>
public static class DungeonPathfinder
{
    /// <summary>An edge steeper than this, centre to centre, is a drop.</summary>
    public const double DropAngleDegrees = 45d;

    /// <summary>Cells within this many physics units of the character's height count as the same floor.</summary>
    private const double SameFloorBand = 8d;

    private const int PatrolMinKeepNodes = 8;
    private const double PatrolMinKeepFraction = 0.2;

    public static Dictionary<uint, PluginDungeonCell> Graph(IReadOnlyList<PluginDungeonCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var graph = new Dictionary<uint, PluginDungeonCell>(cells.Count);
        foreach (PluginDungeonCell cell in cells)
            graph[cell.CellId] = cell;
        return graph;
    }

    public static bool IsDropEdge(in PluginDungeonCell from, in PluginDungeonCell to)
    {
        double rise = Math.Abs(to.Elevation - from.Elevation) * 240d;
        if (rise < 0.5d)
            return false;
        double run = from.Position.HorizontalDistanceMeters(to.Position);
        if (run < 1d)
            return true;
        return Math.Atan2(rise, run) * (180d / Math.PI) > DropAngleDegrees;
    }

    /// <summary>
    /// The cell whose centre is nearest a position, preferring cells on the
    /// same floor (within <see cref="SameFloorBand"/> of the given elevation)
    /// and falling back to the nearest anywhere. Zero for an empty graph.
    /// </summary>
    public static uint NearestCell(Dictionary<uint, PluginDungeonCell> graph, in PluginNavigationPosition position, bool useElevation = true)
    {
        uint best = 0u, bestOnFloor = 0u;
        double bestDistance = double.PositiveInfinity, bestOnFloorDistance = double.PositiveInfinity;
        foreach (PluginDungeonCell cell in graph.Values)
        {
            double distance = cell.Position.HorizontalDistanceMeters(position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cell.CellId;
            }
            if (useElevation && Math.Abs(cell.Elevation - position.Elevation) * 240d <= SameFloorBand && distance < bestOnFloorDistance)
            {
                bestOnFloorDistance = distance;
                bestOnFloor = cell.CellId;
            }
        }
        return bestOnFloor != 0u ? bestOnFloor : best;
    }

    /// <summary>
    /// A cell to leave a hazardous one for: a doorway neighbour that is safe
    /// and walkable, else the nearest safe cell anywhere. Zero when none.
    /// </summary>
    public static uint NearestSafeCell(Dictionary<uint, PluginDungeonCell> graph, uint fromCell, IReadOnlySet<uint> hazards)
    {
        if (!graph.TryGetValue(fromCell, out PluginDungeonCell from))
            return 0u;
        uint bestNeighbor = 0u;
        double bestNeighborDistance = double.PositiveInfinity;
        foreach (uint id in from.Neighbors)
        {
            if (hazards.Contains(id) || !graph.TryGetValue(id, out PluginDungeonCell neighbor) || IsDropEdge(from, neighbor))
                continue;
            double distance = neighbor.Position.HorizontalDistanceMeters(from.Position);
            if (distance < bestNeighborDistance)
            {
                bestNeighborDistance = distance;
                bestNeighbor = id;
            }
        }
        if (bestNeighbor != 0u)
            return bestNeighbor;
        uint best = 0u;
        double bestDistance = double.PositiveInfinity;
        foreach (PluginDungeonCell cell in graph.Values)
        {
            if (cell.CellId == fromCell || hazards.Contains(cell.CellId))
                continue;
            double distance = cell.Position.HorizontalDistanceMeters(from.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cell.CellId;
            }
        }
        return best;
    }

    /// <summary>
    /// A*, doorway to doorway, skipping drops and hazard cells (the goal is
    /// allowed to be one: the caller asked to go there). Empty when unreachable.
    /// </summary>
    public static List<uint> FindPath(Dictionary<uint, PluginDungeonCell> graph, uint start, uint goal, IReadOnlySet<uint>? hazards = null)
    {
        if (!graph.TryGetValue(start, out PluginDungeonCell startCell) || !graph.TryGetValue(goal, out PluginDungeonCell goalCell))
            return new List<uint>();
        if (start == goal)
            return new List<uint> { start };

        var cost = new Dictionary<uint, double> { [start] = 0d };
        var cameFrom = new Dictionary<uint, uint>();
        var open = new PriorityQueue<uint, double>();
        var closed = new HashSet<uint>();
        open.Enqueue(start, Heuristic(startCell, goalCell));
        while (open.Count > 0)
        {
            uint current = open.Dequeue();
            if (!closed.Add(current))
                continue;
            if (current == goal)
                return Reconstruct(cameFrom, current);
            PluginDungeonCell cell = graph[current];
            double soFar = cost[current];
            foreach (uint next in cell.Neighbors)
            {
                if (closed.Contains(next) || !graph.TryGetValue(next, out PluginDungeonCell nextCell) || IsDropEdge(cell, nextCell))
                    continue;
                if (next != goal && hazards is not null && hazards.Contains(next))
                    continue;
                double tentative = soFar + cell.Position.HorizontalDistanceMeters(nextCell.Position);
                if (tentative < cost.GetValueOrDefault(next, double.PositiveInfinity))
                {
                    cameFrom[next] = current;
                    cost[next] = tentative;
                    open.Enqueue(next, tentative + Heuristic(nextCell, goalCell));
                }
            }
        }
        return new List<uint>();
    }

    /// <summary>A once route along a cell path, through doorway points, ending at the exact destination.</summary>
    public static Route BuildRoute(Dictionary<uint, PluginDungeonCell> graph, IReadOnlyList<uint> path, in PluginNavigationPosition destination, string name = "path")
    {
        var waypoints = new List<Waypoint>();
        for (int index = 0; index + 1 < path.Count; index++)
        {
            if (graph.TryGetValue(path[index], out PluginDungeonCell from) && graph.TryGetValue(path[index + 1], out PluginDungeonCell to))
                AddDoorwayPoints(waypoints, from, to);
        }
        waypoints.Add(new Waypoint(WaypointKind.Point, destination.EastWest, destination.NorthSouth) { Elevation = destination.Elevation });
        Simplify(waypoints);
        return new Route { Name = name, Mode = RouteMode.Once, Waypoints = waypoints };
    }

    /// <summary>
    /// A looping patrol over the dungeon's main route from the start cell:
    /// every corridor once, loops walked round, dead ends and bridges
    /// re-trodden only by the shortest way back, closed back to the start.
    /// Empty when the dungeon has nothing to walk.
    /// </summary>
    public static Route BuildPatrolRoute(Dictionary<uint, PluginDungeonCell> graph, uint start, IReadOnlySet<uint>? hazards = null, string name = "patrol")
    {
        HashSet<uint> main = MainRouteCells(graph, start, hazards);
        var adjacency = new Dictionary<uint, List<uint>>();
        var edges = new HashSet<ulong>();
        foreach (uint id in main)
        {
            if (!graph.TryGetValue(id, out PluginDungeonCell cell))
                continue;
            foreach (uint next in cell.Neighbors)
            {
                if (!main.Contains(next) || !graph.TryGetValue(next, out PluginDungeonCell nextCell) || IsDropEdge(cell, nextCell))
                    continue;
                if (!adjacency.TryGetValue(id, out List<uint>? list))
                    adjacency[id] = list = new List<uint>();
                list.Add(next);
                edges.Add(EdgeKey(id, next));
            }
        }

        var waypoints = new List<Waypoint>();
        uint walkStart = adjacency.TryGetValue(start, out List<uint>? startList) && startList.Count > 0
            ? start
            : NearestCellWithEdges(graph, adjacency, start);
        if (walkStart == 0u || edges.Count == 0)
            return new Route { Name = name, Mode = RouteMode.Loop, Waypoints = waypoints };

        var walk = new List<uint> { walkStart };
        var remaining = new HashSet<ulong>(edges);
        uint current = walkStart;
        int guard = edges.Count * 4 + main.Count + 16;
        while (remaining.Count > 0 && guard-- > 0)
        {
            uint next = 0u;
            bool found = false;
            if (adjacency.TryGetValue(current, out List<uint>? neighbors))
            {
                foreach (uint candidate in neighbors)
                {
                    if (remaining.Contains(EdgeKey(current, candidate)))
                    {
                        next = candidate;
                        found = true;
                        break;
                    }
                }
            }
            if (found)
            {
                remaining.Remove(EdgeKey(current, next));
                walk.Add(next);
                current = next;
                continue;
            }
            // Out of fresh corridors here: the shortest hop to a cell that still has one.
            List<uint>? hop = PathToUnusedEdge(adjacency, current, remaining);
            if (hop is null)
                break;
            for (int index = 1; index < hop.Count; index++)
                walk.Add(hop[index]);
            current = hop[^1];
        }
        if (current != walkStart)
        {
            List<uint>? back = ShortestPath(adjacency, current, walkStart);
            if (back is not null)
            {
                for (int index = 1; index < back.Count; index++)
                    walk.Add(back[index]);
            }
        }

        // Standing off the main route (a dead-end spur, the entrance corridor):
        // a one-time lead-in along the doorways to where the loop begins, so
        // the first step is never across a wall.
        int loopStart = 0;
        if (walkStart != start && graph.ContainsKey(start))
        {
            List<uint> leadIn = FindPath(graph, start, walkStart, hazards);
            for (int index = 0; index + 1 < leadIn.Count; index++)
            {
                if (graph.TryGetValue(leadIn[index], out PluginDungeonCell from) && graph.TryGetValue(leadIn[index + 1], out PluginDungeonCell to))
                    AddDoorwayPoints(waypoints, from, to);
            }
            Simplify(waypoints);
            loopStart = waypoints.Count;
        }

        var loop = new List<Waypoint>();
        for (int index = 0; index + 1 < walk.Count; index++)
        {
            if (graph.TryGetValue(walk[index], out PluginDungeonCell from) && graph.TryGetValue(walk[index + 1], out PluginDungeonCell to))
                AddDoorwayPoints(loop, from, to);
        }
        Simplify(loop);
        waypoints.AddRange(loop);
        return new Route { Name = name, Mode = RouteMode.Loop, Waypoints = waypoints, LoopStart = loopStart };
    }

    /// <summary>
    /// The 2-core of the walkable cells reachable from the start: leaves are
    /// peeled until every cell keeps two walkable neighbours, which strips
    /// dead-end spurs of any depth. Falls back to everything reachable when
    /// the pruning collapses a small or purely linear dungeon.
    /// </summary>
    public static HashSet<uint> MainRouteCells(Dictionary<uint, PluginDungeonCell> graph, uint start, IReadOnlySet<uint>? hazards = null)
    {
        var reachable = new HashSet<uint>();
        if (graph.ContainsKey(start))
        {
            var queue = new Queue<uint>();
            queue.Enqueue(start);
            reachable.Add(start);
            while (queue.Count > 0)
            {
                uint current = queue.Dequeue();
                PluginDungeonCell cell = graph[current];
                foreach (uint next in cell.Neighbors)
                {
                    if (reachable.Contains(next) || !graph.TryGetValue(next, out PluginDungeonCell nextCell))
                        continue;
                    if (IsDropEdge(cell, nextCell) || (hazards is not null && hazards.Contains(next)))
                        continue;
                    reachable.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        var pruned = new HashSet<uint>(reachable);
        while (true)
        {
            var leaves = new List<uint>();
            foreach (uint id in pruned)
            {
                PluginDungeonCell cell = graph[id];
                int degree = 0;
                foreach (uint next in cell.Neighbors)
                {
                    if (pruned.Contains(next) && graph.TryGetValue(next, out PluginDungeonCell nextCell) && !IsDropEdge(cell, nextCell) && ++degree > 1)
                        break;
                }
                if (degree <= 1)
                    leaves.Add(id);
            }
            if (leaves.Count == 0)
                break;
            foreach (uint id in leaves)
                pruned.Remove(id);
        }
        return pruned.Count < PatrolMinKeepNodes || pruned.Count < reachable.Count * PatrolMinKeepFraction
            ? reachable
            : pruned;
    }

    /// <summary>
    /// The way from one cell into the next: through the opening itself when
    /// the host says where it is - a cell's origin is its model anchor, not
    /// a point on the floor between its doors - else 30% and 50% of the way
    /// between the two origins.
    /// </summary>
    private static void AddDoorwayPoints(List<Waypoint> waypoints, in PluginDungeonCell from, in PluginDungeonCell to)
    {
        if (TryDoorway(from, to, out PluginDungeonDoorway doorway) || TryDoorway(to, from, out doorway))
        {
            waypoints.Add(new Waypoint(WaypointKind.Point, doorway.EastWest, doorway.NorthSouth) { Elevation = doorway.Elevation, Precise = true });
            return;
        }
        double east = to.EastWest - from.EastWest;
        double north = to.NorthSouth - from.NorthSouth;
        double up = to.Elevation - from.Elevation;
        waypoints.Add(new Waypoint(WaypointKind.Point, from.EastWest + east * 0.3d, from.NorthSouth + north * 0.3d) { Elevation = from.Elevation + up * 0.3d });
        waypoints.Add(new Waypoint(WaypointKind.Point, from.EastWest + east * 0.5d, from.NorthSouth + north * 0.5d) { Elevation = from.Elevation + up * 0.5d });
    }

    private static bool TryDoorway(in PluginDungeonCell cell, in PluginDungeonCell other, out PluginDungeonDoorway doorway)
    {
        foreach (PluginDungeonDoorway candidate in cell.Doorways)
        {
            if (candidate.OtherCellId == other.CellId)
            {
                doorway = candidate;
                return true;
            }
        }
        doorway = default;
        return false;
    }

    /// <summary>
    /// Drops points that lie within 1.5 m of the segment between their
    /// neighbours - between them, not merely on the line through them, so
    /// the far end of an out-and-back spur survives - until none are left.
    /// </summary>
    private static void Simplify(List<Waypoint> waypoints, double thresholdMeters = 1.5d)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int index = 1; index + 1 < waypoints.Count; index++)
            {
                if (waypoints[index].Kind != WaypointKind.Point)
                    continue;
                if (LiesBetween(waypoints[index - 1], waypoints[index + 1], waypoints[index], thresholdMeters))
                {
                    waypoints.RemoveAt(index--);
                    changed = true;
                }
            }
        }
    }

    private static bool LiesBetween(Waypoint from, Waypoint to, Waypoint point, double thresholdMeters)
    {
        double lineEast = to.EastWest - from.EastWest;
        double lineNorth = to.NorthSouth - from.NorthSouth;
        double lengthSquared = lineEast * lineEast + lineNorth * lineNorth;
        double pointEast = point.EastWest - from.EastWest;
        double pointNorth = point.NorthSouth - from.NorthSouth;
        if (lengthSquared < 1e-18)
            return Math.Sqrt(pointEast * pointEast + pointNorth * pointNorth) * 240d < thresholdMeters;
        double t = (pointEast * lineEast + pointNorth * lineNorth) / lengthSquared;
        if (t <= 0d || t >= 1d)
            return false;
        double crossTrack = Math.Abs(pointEast * lineNorth - pointNorth * lineEast) / Math.Sqrt(lengthSquared) * 240d;
        return crossTrack < thresholdMeters;
    }

    private static double Heuristic(in PluginDungeonCell from, in PluginDungeonCell to) =>
        from.Position.HorizontalDistanceMeters(to.Position);

    private static List<uint> Reconstruct(Dictionary<uint, uint> cameFrom, uint goal)
    {
        var path = new List<uint> { goal };
        uint current = goal;
        while (cameFrom.TryGetValue(current, out uint previous))
        {
            path.Add(previous);
            current = previous;
        }
        path.Reverse();
        return path;
    }

    private static ulong EdgeKey(uint a, uint b) =>
        a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    private static uint NearestCellWithEdges(Dictionary<uint, PluginDungeonCell> graph, Dictionary<uint, List<uint>> adjacency, uint fromCell)
    {
        if (!graph.TryGetValue(fromCell, out PluginDungeonCell from))
        {
            foreach (KeyValuePair<uint, List<uint>> pair in adjacency)
                if (pair.Value.Count > 0)
                    return pair.Key;
            return 0u;
        }
        uint best = 0u;
        double bestDistance = double.PositiveInfinity;
        foreach (KeyValuePair<uint, List<uint>> pair in adjacency)
        {
            if (pair.Value.Count == 0 || !graph.TryGetValue(pair.Key, out PluginDungeonCell cell))
                continue;
            double distance = cell.Position.HorizontalDistanceMeters(from.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pair.Key;
            }
        }
        return best;
    }

    private static List<uint>? ShortestPath(Dictionary<uint, List<uint>> adjacency, uint source, uint destination)
    {
        if (source == destination)
            return new List<uint> { source };
        var previous = new Dictionary<uint, uint>();
        var seen = new HashSet<uint> { source };
        var queue = new Queue<uint>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            uint current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out List<uint>? neighbors))
                continue;
            foreach (uint next in neighbors)
            {
                if (!seen.Add(next))
                    continue;
                previous[next] = current;
                if (next == destination)
                    return Rebuild(previous, source, destination);
                queue.Enqueue(next);
            }
        }
        return null;
    }

    private static List<uint>? PathToUnusedEdge(Dictionary<uint, List<uint>> adjacency, uint source, HashSet<ulong> remaining)
    {
        var previous = new Dictionary<uint, uint>();
        var seen = new HashSet<uint> { source };
        var queue = new Queue<uint>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            uint current = queue.Dequeue();
            if (current != source && HasUnusedEdge(adjacency, current, remaining))
                return Rebuild(previous, source, current);
            if (!adjacency.TryGetValue(current, out List<uint>? neighbors))
                continue;
            foreach (uint next in neighbors)
            {
                if (!seen.Add(next))
                    continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    private static bool HasUnusedEdge(Dictionary<uint, List<uint>> adjacency, uint cell, HashSet<ulong> remaining)
    {
        if (!adjacency.TryGetValue(cell, out List<uint>? neighbors))
            return false;
        foreach (uint next in neighbors)
            if (remaining.Contains(EdgeKey(cell, next)))
                return true;
        return false;
    }

    private static List<uint> Rebuild(Dictionary<uint, uint> previous, uint source, uint destination)
    {
        var path = new List<uint> { destination };
        uint current = destination;
        while (current != source && previous.TryGetValue(current, out uint step))
        {
            path.Add(step);
            current = step;
        }
        path.Reverse();
        return path;
    }
}
