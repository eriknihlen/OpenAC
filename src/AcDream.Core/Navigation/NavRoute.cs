using System.Diagnostics;
using System.Numerics;

namespace AcDream.Core.Navigation;

public enum NavRouteOutcome
{
    Routed,

    /// <summary>No clear node near the start can be walked to without passing a wall.</summary>
    NoStart,

    /// <summary>No clear node near enough to the goal can see it.</summary>
    NoGoal,

    /// <summary>No clear path joins the start to a node that can see the goal.</summary>
    NoPath,
}

/// <summary>A spot a route keeps out of, such as where a walk last stopped making progress.</summary>
public readonly record struct NavAvoidance(Vector3 Centre, float Radius);

/// <summary>
/// A route over a <see cref="NavGrid"/>: the nodes it steps through, and the
/// straight legs between them that a body can walk.
/// </summary>
public sealed record NavRoute(
    NavRouteOutcome Outcome,
    string Reason,
    IReadOnlyList<Vector3> Path,
    IReadOnlyList<Vector3> Legs,
    float Length,
    int Expansions,
    double Milliseconds)
{
    /// <summary>The legs flown as standing long jumps rather than walked, in order.</summary>
    public IReadOnlyList<NavRouteLeap> Leaps { get; init; } = [];

    /// <summary>
    /// Whether the route ends where the body can see its goal. False only for a route that
    /// ends at the nearest spot it reaches because no spot it reaches can see the goal.
    /// </summary>
    public bool EndsInSight { get; init; } = true;

    /// <summary>
    /// How much of the route's legs is walked nearer walls and ledges than is
    /// comfortable, in square meters: the shortfall at each step, times the step.
    /// </summary>
    public float Scrape { get; init; }

    /// <summary>
    /// How much of the route's legs passes through the spots it was asked to pass
    /// creatures and players in, in meters, each step counted by how deep in it is.
    /// </summary>
    public float Crowding { get; init; }
}

/// <summary>
/// Finds routes over a <see cref="NavGrid"/> with A*. A route starts from a
/// clear node the start can walk to without passing through a wall, and ends at
/// a clear node near the goal from which a body can see the goal, so a wall
/// between the two never counts as arriving.
/// </summary>
public static class NavRouter
{
    public const float StartRadius = 1.5f;
    public const float StartHeightTolerance = 1f;

    /// <summary>How far above or below the goal an arrival node may stand.</summary>
    public const float GoalHeightTolerance = 1.2f;

    /// <summary>
    /// When no node within the arrival radius can be both reached and see the
    /// goal, as for a vendor behind a counter, the route ends at the nearest
    /// reachable node within this far that can see it. When none can see it, as
    /// through a window whose collision fills the opening, and the route is not
    /// keeping out of a spot where a walk was blocked, it ends at the nearest
    /// reachable node within this far, without a line of sight.
    /// </summary>
    public const float FallbackReach = 10f;

    public const int MaximumExpansions = 4_000_000;

    /// <summary>
    /// How a search weighs walking near walls and ledges: a node nearer one than
    /// <paramref name="Clearance"/> costs <paramref name="Weight"/> more to step onto
    /// for each meter it falls short, so routes keep to the middle of doorways.
    /// </summary>
    private readonly record struct SearchProfile(float Clearance, float Weight);

    /// <summary>
    /// A route is planned in each of these ways at once, from one keeping well clear
    /// of walls to the shortest, and the tidiest of the routes found is kept. A
    /// route toward a goal beyond the grid is planned the first way only.
    /// </summary>
    private static readonly SearchProfile[] Profiles = [new(1.25f, 0.5f), new(2f, 1f), new(0f, 0f)];

    /// <summary>
    /// What makes a route untidy to walk, in meters added to its length: each leg,
    /// each turn sharper than <see cref="SharpTurnDegrees"/>, and each square meter
    /// of shortfall from <see cref="ComfortableLegClearance"/> along its legs.
    /// </summary>
    private const float LegUntidiness = 0.5f;
    private const float TurnUntidiness = 2f;
    private const float ScrapeUntidiness = 2f;
    private const float SharpTurnDegrees = 30f;

    /// <summary>What a leap costs a route beyond the distance it covers, in meters, and more for each share of full power.</summary>
    private const float LeapCost = 3f;
    private const float LeapPowerCost = 2f;

    /// <summary>What each unit of a leap's risk costs a route, in meters, so a route takes an easy leap over a risky one wherever it can.</summary>
    private const float LeapRiskCost = 6f;

    /// <summary>A route toward a goal beyond the grid found with leaps is taken only when it ends this much nearer the goal.</summary>
    private const float LeapWorthMeters = 1f;

    /// <summary>
    /// A route may pass through the spots creatures and players stand in, since they
    /// move aside when bumped, but it pays to. Stepping onto a node inside one costs up
    /// to <see cref="CrowdCost"/> more: <see cref="CrowdEdgeShare"/> of it just inside the
    /// edge, so grazing a creature costs as surely as walking into it, and the rest by how
    /// deep in. Each meter through one makes a route <see cref="CrowdUntidiness"/> meters
    /// untidier. Legs shaped from a searched path go no deeper into a spot than the path
    /// did, less <see cref="CrowdShapeSlack"/>. A route around them is
    /// taken only when it walks no nearer walls and ledges than the route through them,
    /// give or take <see cref="CrowdScrapeAllowance"/> square meters, so keeping out of a
    /// crowd never trades it for scraping along a wall.
    /// </summary>
    private const float CrowdCost = 2f;
    private const float CrowdEdgeShare = 0.5f;
    private const float CrowdShapeSlack = 0.05f;
    private const float CrowdUntidiness = 8f;
    private const float CrowdScrapeAllowance = 0.25f;

    private const string ReachedEdgeReason = "the goal lies beyond this grid, so the route ends at its edge toward the goal";

    /// <summary>
    /// A corner a route turns at is moved, when it helps and there is room, so that
    /// both legs through it keep up to this far from walls and ledges, the way a
    /// walker takes a corner wide instead of brushing it. A corner moves at most
    /// <see cref="CornerShift"/>, and only for a gain of at least <see cref="CornerGain"/>.
    /// </summary>
    private const float ComfortableLegClearance = 1f;
    private const float CornerShift = 1.5f;
    private const float CornerGain = 0.1f;

    /// <summary>How far above or below an avoided spot a node is still kept out of it.</summary>
    private const float AvoidanceHeight = 2f;

    /// <summary>A start this far from its node walks to the node first when it cannot walk the first leg straight.</summary>
    private const float StartOffNode = 0.3f;

    private const float DiagonalStep = 1.41421356f;

    /// <summary>
    /// Plans a route in each of the <see cref="Profiles"/> at once and returns the
    /// tidiest: a route that arrives before one that ends short of the goal, and of
    /// those alike, the one least untidy to walk. When no walked route arrives and
    /// <paramref name="leaps"/> says what the body can leap, the route is planned
    /// again the first way with leaps, and the better of the two is returned. A route
    /// passes the spots in <paramref name="crowd"/>, where creatures and players stand,
    /// around them when that keeps as clear of walls as going through them, and through
    /// them otherwise.
    /// </summary>
    public static NavRoute Find(
        NavGrid grid,
        Vector3 from,
        Vector3 to,
        float arrivalRadius,
        IReadOnlyList<NavAvoidance>? avoid = null,
        NavLeapAbility? leaps = null,
        IReadOnlyList<NavAvoidance>? crowd = null,
        bool arriveOnGoalFloor = false,
        float goalRadius = 0f)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return Find(grid, from, to, arrivalRadius, avoid, leaps, crowd, arriveOnGoalFloor, onto: null, goalRadius);
    }

    /// <summary>
    /// Finds a route onto an object whose collision is <paramref name="surfaces"/>, ending on
    /// its top: the highest floor standing on the object's own surfaces that a body stands on,
    /// within <paramref name="arrivalRadius"/> of that floor's middle. A route never ends on the
    /// ground beside the object, and fails when nothing on top of it can be stood on or no
    /// route, leaping where <paramref name="leaps"/> allows, gets up there.
    /// </summary>
    public static NavRoute FindOnto(
        NavGrid grid,
        Vector3 from,
        NavSurfaces surfaces,
        float arrivalRadius,
        IReadOnlyList<NavAvoidance>? avoid = null,
        NavLeapAbility? leaps = null,
        IReadOnlyList<NavAvoidance>? crowd = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(surfaces);
        if (!TryFindTop(grid, surfaces, out Vector3 goal, out HashSet<int> top))
            return Failed(NavRouteOutcome.NoGoal, "nothing on top of the object can be stood on", 0, Stopwatch.StartNew());
        return Find(grid, from, goal, arrivalRadius, avoid, leaps, crowd, arriveOnGoalFloor: false, top);
    }

    private static NavRoute Find(
        NavGrid grid,
        Vector3 from,
        Vector3 to,
        float arrivalRadius,
        IReadOnlyList<NavAvoidance>? avoid,
        NavLeapAbility? leaps,
        IReadOnlyList<NavAvoidance>? crowd,
        bool arriveOnGoalFloor,
        IReadOnlySet<int>? onto,
        float goalRadius = 0f)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NavAvoidance> crowded = crowd ?? [];
        bool priced = crowded.Count > 0;
        var plans = new (NavRoute Route, float Untidiness)[priced ? Profiles.Length * 2 : Profiles.Length];
        Parallel.For(0, plans.Length, index => plans[index] = Find(
            grid,
            from,
            to,
            arrivalRadius,
            avoid,
            crowded,
            priceCrowd: priced && index < Profiles.Length,
            Profiles[index % Profiles.Length],
            leaps: null,
            arriveOnGoalFloor,
            onto,
            goalRadius));
        (NavRoute Route, float Untidiness) tidiest = Tidiest(plans.AsSpan(0, Profiles.Length));
        if (priced)
        {
            (NavRoute Route, float Untidiness) through = Tidiest(plans.AsSpan(Profiles.Length));
            if (Rank(through.Route) < Rank(tidiest.Route)
                || (Rank(through.Route) == Rank(tidiest.Route)
                    && tidiest.Route.Scrape > through.Route.Scrape + CrowdScrapeAllowance))
            {
                tidiest = through;
            }
        }
        if (Rank(tidiest.Route) > 0 && leaps is { } ability)
        {
            (NavRoute Route, float Untidiness) leapt =
                Find(grid, from, to, arrivalRadius, avoid, crowded, priced, Profiles[0], new NavLeapFinder(grid, ability), arriveOnGoalFloor, onto, goalRadius);
            if (Rank(leapt.Route) < Rank(tidiest.Route))
                tidiest = leapt;
        }
        return tidiest.Route with { Milliseconds = clock.Elapsed.TotalMilliseconds };
    }

    /// <summary>The plan that ranks first, and of plans alike, the least untidy.</summary>
    private static (NavRoute Route, float Untidiness) Tidiest(ReadOnlySpan<(NavRoute Route, float Untidiness)> plans)
    {
        (NavRoute Route, float Untidiness) tidiest = plans[0];
        foreach ((NavRoute Route, float Untidiness) plan in plans)
        {
            int rank = Rank(plan.Route);
            if (rank < Rank(tidiest.Route) || (rank == Rank(tidiest.Route) && plan.Untidiness < tidiest.Untidiness))
                tidiest = plan;
        }
        return tidiest;
    }

    /// <summary>A route that arrives ranks first, then one that ends short of the goal, then one that failed.</summary>
    private static int Rank(NavRoute route) =>
        route.Outcome != NavRouteOutcome.Routed ? 2 : route.Reason == "routed" ? 0 : 1;

    private static (NavRoute Route, float Untidiness) Find(
        NavGrid grid,
        Vector3 from,
        Vector3 to,
        float arrivalRadius,
        IReadOnlyList<NavAvoidance>? avoid,
        IReadOnlyList<NavAvoidance> crowd,
        bool priceCrowd,
        SearchProfile profile,
        NavLeapFinder? leaps,
        bool arriveOnGoalFloor = false,
        IReadOnlySet<int>? onto = null,
        float goalRadius = 0f)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NavAvoidance> avoided = avoid ?? [];
        float objectRadius = float.IsFinite(goalRadius) ? MathF.Max(goalRadius, 0f) : 0f;
        float reach = MathF.Max(arrivalRadius, grid.CellSize) + objectRadius;
        int start = grid.FindWalkableNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            start = grid.FindStandingNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            return (NoStart(clock), float.PositiveInfinity);
        (int[]? pieces, int goalPiece) = arriveOnGoalFloor && onto is null ? GoalFloor(grid, to) : (null, -1);
        Func<int, bool>? accepts = onto is not null
            ? onto.Contains
            : goalPiece >= 0
                ? node => pieces![node] == goalPiece
                : null;
        var goal = new GoalTest(grid, to, reach, avoided, accepts, objectRadius);
        bool nearGoalSeen = goal.MaySucceed();

        var search = new Search(grid, start, to, reach, avoided, priceCrowd ? crowd : [], profile, leaps);
        int end = search.Run(node => nearGoalSeen && goal.IsReachedAt(node));
        if (end >= 0)
            return Routed(grid, from, start, end, search, avoided, crowd, priceCrowd, clock, "routed");
        if (end == Search.GaveUp)
            return (SearchGaveUp(search, clock), float.PositiveInfinity);
        if (accepts is not null)
        {
            // Ending anywhere but the floor the goal stands on would be arriving beside it,
            // such as on the ground below a rock top, so no nearer spot will do.
            NavRoute offFloor = Failed(
                NavRouteOutcome.NoPath,
                onto is not null ? "no route reaches the top of the object" : "no route reaches the floor the goal stands on",
                search.Expansions,
                clock);
            return (offFloor, float.PositiveInfinity);
        }
        int nearest = goal.NearestSeeingAmong(search.Closed);
        if (nearest >= 0)
        {
            Vector3 at = grid.Position(nearest);
            float away = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(to.X, to.Y));
            return Routed(
                grid,
                from,
                start,
                nearest,
                search,
                avoided,
                crowd,
                priceCrowd,
                clock,
                $"nothing within {reach:0.#} m of the goal can be reached and see it, so the route ends at "
                + $"the nearest spot that can, {away:0.0} m from it");
        }
        int nearestUnseeing = avoided.Count == 0 ? goal.NearestAmong(search.Closed) : -1;
        if (nearestUnseeing >= 0)
        {
            Vector3 at = grid.Position(nearestUnseeing);
            float away = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(to.X, to.Y));
            (NavRoute unseeing, float untidiness) = Routed(
                grid,
                from,
                start,
                nearestUnseeing,
                search,
                avoided,
                crowd,
                priceCrowd,
                clock,
                $"no reachable spot can see the goal, so the route ends at the nearest spot, {away:0.0} m from it, "
                + "without a line of sight");
            return (unseeing with { EndsInSight = false }, untidiness);
        }
        NavRoute failed = nearGoalSeen
            ? Failed(
                NavRouteOutcome.NoPath,
                $"no clear path leads from the start to a spot within {FallbackReach:0} m that can see the goal",
                search.Expansions,
                clock)
            : Failed(
                NavRouteOutcome.NoGoal,
                $"no spot within {FallbackReach:0} m of the goal that the start can reach can see it",
                search.Expansions,
                clock);
        return (failed, float.PositiveInfinity);
    }

    /// <summary>
    /// Finds a route toward a goal that lies beyond the grid, for a walk too long
    /// for one grid to hold: to the first reachable node within
    /// <paramref name="band"/> of the nearest the grid comes to the goal, or, when
    /// none is reachable, to the reachable node nearest the goal. When no walked
    /// route reaches the grid's edge toward the goal and <paramref name="leaps"/> says
    /// what the body can leap, it is found again with leaps, and taken when it ends
    /// nearer the goal.
    /// </summary>
    public static NavRoute FindToward(
        NavGrid grid,
        Vector3 from,
        Vector3 goal,
        float band,
        IReadOnlyList<NavAvoidance>? avoid = null,
        NavLeapAbility? leaps = null,
        IReadOnlyList<NavAvoidance>? crowd = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        IReadOnlyList<NavAvoidance> crowded = crowd ?? [];
        bool priced = crowded.Count > 0;
        NavRoute walked = FindTowardWith(grid, from, goal, band, avoid, crowded, priced, null);
        if (priced)
        {
            NavRoute through = FindTowardWith(grid, from, goal, band, avoid, crowded, false, null);
            if (through.Outcome == NavRouteOutcome.Routed
                && (walked.Outcome != NavRouteOutcome.Routed
                    || Away(through.Legs[^1], goal) + LeapWorthMeters < Away(walked.Legs[^1], goal)
                    || walked.Scrape > through.Scrape + CrowdScrapeAllowance))
            {
                walked = through;
            }
        }
        if (leaps is not { } ability || walked.Reason.StartsWith(ReachedEdgeReason, StringComparison.Ordinal))
            return walked;
        NavRoute leapt = FindTowardWith(grid, from, goal, band, avoid, crowded, priced, new NavLeapFinder(grid, ability));
        if (leapt.Outcome != NavRouteOutcome.Routed)
            return walked;
        return walked.Outcome != NavRouteOutcome.Routed
            || Away(leapt.Legs[^1], goal) + LeapWorthMeters < Away(walked.Legs[^1], goal)
                ? leapt
                : walked;
    }

    private static NavRoute FindTowardWith(
        NavGrid grid,
        Vector3 from,
        Vector3 goal,
        float band,
        IReadOnlyList<NavAvoidance>? avoid,
        IReadOnlyList<NavAvoidance> crowd,
        bool priceCrowd,
        NavLeapFinder? leaps)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NavAvoidance> avoided = avoid ?? [];
        int start = grid.FindWalkableNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            start = grid.FindStandingNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            return NoStart(clock);

        float enough = DistanceBeyond(grid, goal) + MathF.Max(band, grid.CellSize);
        int nearest = start;
        float nearestAway = Away(grid.Position(start), goal);
        var search = new Search(grid, start, goal, 0f, avoided, priceCrowd ? crowd : [], Profiles[0], leaps);
        int end = search.Run(node =>
        {
            float away = Away(grid.Position(node), goal);
            if (away < nearestAway)
            {
                nearest = node;
                nearestAway = away;
            }
            return away <= enough;
        });
        if (end >= 0)
        {
            return Routed(
                grid,
                from,
                start,
                end,
                search,
                avoided,
                crowd,
                priceCrowd,
                clock,
                $"{ReachedEdgeReason}, {nearestAway:0} m from it").Route;
        }
        if (nearest != start && Away(from, goal) - nearestAway >= grid.CellSize)
        {
            return Routed(
                grid,
                from,
                start,
                nearest,
                search,
                avoided,
                crowd,
                priceCrowd,
                clock,
                $"the goal lies beyond this grid, and the route ends at the reachable spot nearest it, {nearestAway:0} m from it").Route;
        }
        return end == Search.GaveUp
            ? SearchGaveUp(search, clock)
            : Failed(NavRouteOutcome.NoPath, "no clear path leads any nearer the goal", search.Expansions, clock);
    }

    private static (NavRoute Route, float Untidiness) Routed(
        NavGrid grid,
        Vector3 from,
        int start,
        int end,
        Search search,
        IReadOnlyList<NavAvoidance> avoided,
        IReadOnlyList<NavAvoidance> crowd,
        bool priceCrowd,
        Stopwatch clock,
        string reason)
    {
        var nodes = new List<int>();
        for (int node = end; ; node = search.Parent[node])
        {
            nodes.Add(node);
            if (node == start)
                break;
        }
        nodes.Reverse();

        var path = new List<Vector3>(nodes.Count);
        foreach (int node in nodes)
            path.Add(grid.Position(node));

        // Legs are shaped no deeper into crowd spots than the search went, so a
        // straighter leg never cuts back into a creature the route kept clear of.
        IReadOnlyList<NavAvoidance> shaping = priceCrowd ? [.. avoided, .. KeptOut(crowd, path)] : avoided;

        // The walked stretches between leaps are straightened on their own, so a
        // leap's takeoff and landing stay where the search found them.
        var corners = new List<int>();
        var leaps = new List<(int Corner, NavLeap Leap)>();
        int stretchStart = 0;
        for (int index = 1; index <= nodes.Count; index++)
        {
            NavLeap leap = default;
            bool leapsHere = index < nodes.Count
                && search.LeapInto.TryGetValue(nodes[index], out leap)
                && leap.From == nodes[index - 1];
            if (index < nodes.Count && !leapsHere)
                continue;
            List<int> stretch = nodes.GetRange(stretchStart, index - stretchStart);
            List<int> stretchCorners = stretch.Count == 1
                ? [stretch[0]]
                : Straighten(grid, stretch, path.GetRange(stretchStart, index - stretchStart), shaping);
            DropNeedlessCorners(grid, stretchCorners, shaping);
            WidenCorners(grid, stretchCorners, shaping);
            DropNeedlessCorners(grid, stretchCorners, shaping);
            corners.AddRange(stretchCorners);
            if (leapsHere)
                leaps.Add((corners.Count, leap));
            stretchStart = index;
        }
        var legs = new List<Vector3>(corners.Count);
        foreach (int corner in corners)
            legs.Add(grid.Position(corner));

        float offNode = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(path[0].X, path[0].Y));
        int inserted = 0;
        if (legs.Count > 1 && offNode > StartOffNode && !grid.CanSweep(from, legs[1]))
        {
            legs.Insert(0, from);
            inserted = 1;
        }

        float length = 0f;
        for (int index = 1; index < legs.Count; index++)
            length += Vector3.Distance(legs[index - 1], legs[index]);
        float scrape = 0f;
        for (int index = 1; index < corners.Count; index++)
            scrape += Scrape(grid, corners[index - 1], corners[index]);
        float crowding = Crowding(grid, corners, crowd);
        var route = new NavRoute(
            NavRouteOutcome.Routed,
            reason,
            path,
            legs,
            length,
            search.Expansions,
            clock.Elapsed.TotalMilliseconds)
        {
            Leaps = [.. leaps.Select(pair => new NavRouteLeap(pair.Corner + inserted, pair.Leap.Power, pair.Leap.Run))],
            Scrape = scrape,
            Crowding = crowding,
        };
        float untidiness = Untidiness(grid, corners) + (priceCrowd ? CrowdUntidiness * crowding : 0f);
        foreach ((_, NavLeap leap) in leaps)
            untidiness += LeapCost + (leap.Power * LeapPowerCost) + (leap.Risk * LeapRiskCost);
        return (route, untidiness);
    }

    /// <summary>
    /// The nodes a route's straight legs join: from the start, each leg runs to the
    /// farthest node along the path a body can walk to in a straight line.
    /// </summary>
    private static List<int> Straighten(NavGrid grid, List<int> nodes, List<Vector3> path, IReadOnlyList<NavAvoidance> avoided)
    {
        var corners = new List<int> { nodes[0] };
        int anchor = 0;
        while (anchor < nodes.Count - 1)
        {
            int furthest = anchor + 1;
            while (furthest + 1 < nodes.Count
                && grid.CanWalkStraight(nodes[anchor], nodes[furthest + 1])
                && !PassesAvoided(path[anchor], path[furthest + 1], avoided))
            {
                furthest++;
            }
            corners.Add(nodes[furthest]);
            anchor = furthest;
        }
        return corners;
    }

    /// <summary>
    /// Removes each corner whose neighbours a body can walk between in a straight
    /// line keeping as far from walls and ledges as the two legs through it did.
    /// </summary>
    private static void DropNeedlessCorners(NavGrid grid, List<int> corners, IReadOnlyList<NavAvoidance> avoided)
    {
        int index = 1;
        while (index < corners.Count - 1)
        {
            int before = corners[index - 1];
            int after = corners[index + 1];
            float kept = MathF.Min(LegClearance(grid, before, corners[index]), LegClearance(grid, corners[index], after));
            if (grid.CanWalkStraight(before, after, kept, BorderStepsFor(grid, kept))
                && !PassesAvoided(grid.Position(before), grid.Position(after), avoided))
            {
                corners.RemoveAt(index);
                index = Math.Max(1, index - 1);
            }
            else
            {
                index++;
            }
        }
    }

    /// <summary>
    /// Moves each corner to a nearby node on the same floor from which both of its
    /// legs keep farther from walls and ledges, up to
    /// <see cref="ComfortableLegClearance"/>, preferring the shortest such turn.
    /// </summary>
    private static void WidenCorners(NavGrid grid, List<int> corners, IReadOnlyList<NavAvoidance> avoided)
    {
        for (int index = 1; index < corners.Count - 1; index++)
        {
            int before = corners[index - 1];
            int corner = corners[index];
            int after = corners[index + 1];
            float kept = MathF.Min(LegClearance(grid, before, corner), LegClearance(grid, corner, after));
            if (kept + CornerGain > ComfortableLegClearance)
                continue;

            Vector3 at = grid.Position(corner);
            Vector3 from = grid.Position(before);
            Vector3 to = grid.Position(after);
            (int centreX, int centreY) = grid.ColumnOf(corner);
            int reach = (int)MathF.Ceiling(CornerShift / grid.CellSize);
            var candidates = new List<(int Node, float Length)>();
            for (int y = centreY - reach; y <= centreY + reach; y++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    (int first, int count) = grid.NodesInColumn(x, y);
                    for (int node = first; node < first + count; node++)
                    {
                        Vector3 there = grid.Position(node);
                        if (node == corner
                            || !grid.IsClear(node)
                            || grid.WallDistance(node) < kept + CornerGain
                            || MathF.Abs(there.Z - at.Z) > grid.Body.StepUpHeight
                            || Vector2.Distance(new Vector2(there.X, there.Y), new Vector2(at.X, at.Y)) > CornerShift
                            || IsAvoided(there, avoided, Nowhere))
                        {
                            continue;
                        }
                        candidates.Add((node, Vector3.Distance(from, there) + Vector3.Distance(there, to)));
                    }
                }
            }
            candidates.Sort(static (left, right) => left.Length.CompareTo(right.Length));

            bool moved = false;
            for (float wanted = ComfortableLegClearance; !moved && wanted >= kept + CornerGain; wanted -= CornerGain)
            {
                int border = BorderStepsFor(grid, wanted);
                foreach ((int node, _) in candidates)
                {
                    if (grid.WallDistance(node) < wanted || grid.BorderDistance(node) < border)
                        continue;
                    Vector3 there = grid.Position(node);
                    if (grid.CanWalkStraight(before, node, wanted, border)
                        && grid.CanWalkStraight(node, after, wanted, border)
                        && !PassesAvoided(from, there, avoided)
                        && !PassesAvoided(there, to, avoided))
                    {
                        corners[index] = node;
                        moved = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The farthest from walls and ledges, up to <see cref="ComfortableLegClearance"/>,
    /// that a body walking straight between two nodes keeps.
    /// </summary>
    private static float LegClearance(NavGrid grid, int from, int to)
    {
        float low = grid.NearestWall;
        float high = ComfortableLegClearance;
        if (grid.CanWalkStraight(from, to, high, BorderStepsFor(grid, high)))
            return high;
        for (int halving = 0; halving < 5; halving++)
        {
            float middle = (low + high) * 0.5f;
            if (grid.CanWalkStraight(from, to, middle, BorderStepsFor(grid, middle)))
                low = middle;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>
    /// How untidy a route's legs are to walk, in meters: their length, with
    /// <see cref="LegUntidiness"/> for each leg, <see cref="TurnUntidiness"/> for each
    /// sharp turn, and <see cref="ScrapeUntidiness"/> for each square meter of
    /// shortfall from <see cref="ComfortableLegClearance"/> along them.
    /// </summary>
    private static float Untidiness(NavGrid grid, List<int> corners)
    {
        float untidiness = LegUntidiness * (corners.Count - 1);
        float sharp = MathF.Cos(SharpTurnDegrees * (MathF.PI / 180f));
        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 a = grid.Position(corners[index - 1]);
            Vector3 b = grid.Position(corners[index]);
            untidiness += Vector3.Distance(a, b) + (ScrapeUntidiness * Scrape(grid, corners[index - 1], corners[index]));
            if (index + 1 >= corners.Count)
                continue;
            Vector3 c = grid.Position(corners[index + 1]);
            var inbound = new Vector2(b.X - a.X, b.Y - a.Y);
            var outbound = new Vector2(c.X - b.X, c.Y - b.Y);
            if (inbound.LengthSquared() > 1e-6f
                && outbound.LengthSquared() > 1e-6f
                && Vector2.Dot(Vector2.Normalize(inbound), Vector2.Normalize(outbound)) < sharp)
            {
                untidiness += TurnUntidiness;
            }
        }
        return untidiness;
    }

    /// <summary>
    /// How much of a leg is walked nearer walls or ledges than
    /// <see cref="ComfortableLegClearance"/>, in square meters: the shortfall at each
    /// step along it, times the step.
    /// </summary>
    private static float Scrape(NavGrid grid, int from, int to)
    {
        float scrape = 0f;
        foreach (int node in grid.NodesAlong(from, to))
        {
            float clearance = MathF.Min(grid.WallDistance(node), grid.BorderDistance(node) * grid.CellSize);
            scrape += MathF.Max(0f, ComfortableLegClearance - clearance) * grid.CellSize;
        }
        return scrape;
    }

    /// <summary>How much of a route's legs passes through crowd spots, in meters, each step counted by how deep in it is.</summary>
    private static float Crowding(NavGrid grid, List<int> corners, IReadOnlyList<NavAvoidance> crowd)
    {
        if (crowd.Count == 0)
            return 0f;
        float crowding = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            foreach (int node in grid.NodesAlong(corners[index - 1], corners[index]))
                crowding += CrowdDepth(grid.Position(node), crowd) * grid.CellSize;
        }
        return crowding;
    }

    /// <summary>How deep a point stands in crowd spots, summed over them: one at a spot's middle, falling to nothing at its edge.</summary>
    private static float CrowdDepth(Vector3 point, IReadOnlyList<NavAvoidance> crowd)
    {
        float depth = 0f;
        foreach (NavAvoidance spot in crowd)
        {
            if (spot.Radius <= 0f || MathF.Abs(point.Z - spot.Centre.Z) > AvoidanceHeight)
                continue;
            float distance = MathF.Sqrt(FlatDistanceSquared(point, spot.Centre));
            if (distance < spot.Radius)
                depth += 1f - (distance / spot.Radius);
        }
        return depth;
    }

    /// <summary>What stepping onto a point costs in crowd spots, summed over the spots it is inside.</summary>
    private static float CrowdPrice(Vector3 point, IReadOnlyList<NavAvoidance> crowd)
    {
        float price = 0f;
        foreach (NavAvoidance spot in crowd)
        {
            if (spot.Radius <= 0f || MathF.Abs(point.Z - spot.Centre.Z) > AvoidanceHeight)
                continue;
            float distance = MathF.Sqrt(FlatDistanceSquared(point, spot.Centre));
            if (distance < spot.Radius)
                price += CrowdEdgeShare + ((1f - CrowdEdgeShare) * (1f - (distance / spot.Radius)));
        }
        return price;
    }

    /// <summary>
    /// The part of each crowd spot that legs shaped from a searched path keep out of:
    /// all of a spot the path kept out of, and of one it went into, what lies deeper
    /// than the path went.
    /// </summary>
    private static IEnumerable<NavAvoidance> KeptOut(IReadOnlyList<NavAvoidance> crowd, List<Vector3> path)
    {
        foreach (NavAvoidance spot in crowd)
        {
            float nearest = spot.Radius;
            foreach (Vector3 point in path)
            {
                if (MathF.Abs(point.Z - spot.Centre.Z) <= AvoidanceHeight)
                    nearest = MathF.Min(nearest, MathF.Sqrt(FlatDistanceSquared(point, spot.Centre)));
            }
            float kept = nearest - CrowdShapeSlack;
            if (kept > 0f)
                yield return spot with { Radius = kept };
        }
    }

    /// <summary>How many steps from a ledge a body keeps when it keeps <paramref name="clearance"/> from it.</summary>
    private static int BorderStepsFor(NavGrid grid, float clearance) =>
        Math.Max(1, (int)MathF.Floor(clearance / grid.CellSize));

    private static float WallCost(NavGrid grid, int node, SearchProfile profile)
    {
        if (profile.Weight <= 0f)
            return 0f;
        float clearance = MathF.Min(grid.WallDistance(node), grid.BorderDistance(node) * grid.CellSize);
        return clearance >= profile.Clearance ? 0f : (profile.Clearance - clearance) * profile.Weight;
    }

    /// <summary>A point no step is taken from, for tests of avoided spots that allow no way out of them.</summary>
    private static readonly Vector3 Nowhere = new(float.PositiveInfinity);

    /// <summary>
    /// Whether a step onto a point enters a spot a route keeps out of. A route that
    /// starts in such a spot, as a walk that stopped against an object does, may
    /// leave it: a step inside the spot counts only when it comes no farther from
    /// the spot's middle than the point it was taken from.
    /// </summary>
    private static bool IsAvoided(Vector3 point, IReadOnlyList<NavAvoidance> avoided, Vector3 from)
    {
        foreach (NavAvoidance avoidance in avoided)
        {
            float here = FlatDistanceSquared(point, avoidance.Centre);
            if (here > avoidance.Radius * avoidance.Radius || MathF.Abs(point.Z - avoidance.Centre.Z) > AvoidanceHeight)
                continue;
            if (here > FlatDistanceSquared(from, avoidance.Centre))
                continue;
            return true;
        }
        return false;
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static bool PassesAvoided(Vector3 from, Vector3 to, IReadOnlyList<NavAvoidance> avoided)
    {
        foreach (NavAvoidance avoidance in avoided)
        {
            var start = new Vector2(from.X, from.Y);
            Vector2 along = new Vector2(to.X, to.Y) - start;
            var centre = new Vector2(avoidance.Centre.X, avoidance.Centre.Y);
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-12f
                ? Math.Clamp(Vector2.Dot(centre - start, along) / lengthSquared, 0f, 1f)
                : 0f;
            float height = from.Z + ((to.Z - from.Z) * t);
            float closest = Vector2.Distance(centre, start + (along * t));
            if (closest > avoidance.Radius || MathF.Abs(height - avoidance.Centre.Z) > AvoidanceHeight)
                continue;
            float away = Vector2.Distance(centre, start);
            if (away <= avoidance.Radius && closest >= away - 1e-3f)
                continue;
            return true;
        }
        return false;
    }

    private static float Remaining(Vector3 position, Vector3 goal, float reach)
    {
        float dx = position.X - goal.X;
        float dy = position.Y - goal.Y;
        return MathF.Max(0f, MathF.Sqrt((dx * dx) + (dy * dy)) - reach);
    }

    private static NavRoute Failed(NavRouteOutcome outcome, string reason, int expansions, Stopwatch clock) =>
        new(outcome, reason, [], [], 0f, expansions, clock.Elapsed.TotalMilliseconds);

    private static NavRoute NoStart(Stopwatch clock) =>
        Failed(NavRouteOutcome.NoStart, "no clear spot near the start can be walked to without passing a wall", 0, clock);

    private static NavRoute SearchGaveUp(Search search, Stopwatch clock) =>
        Failed(
            NavRouteOutcome.NoPath,
            $"the search gave up after {search.Expansions} expansions",
            search.Expansions,
            clock);

    private static float Away(Vector3 point, Vector3 goal) =>
        Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(goal.X, goal.Y));

    /// <summary>How far a point lies outside a grid's square, measured flat; zero inside it.</summary>
    private static float DistanceBeyond(NavGrid grid, Vector3 point)
    {
        float dx = MathF.Max(0f, MathF.Max(grid.OriginX - point.X, point.X - (grid.OriginX + grid.Size)));
        float dy = MathF.Max(0f, MathF.Max(grid.OriginY - point.Y, point.Y - (grid.OriginY + grid.Size)));
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// A best-first expansion of the clear nodes reachable from a start, ordered
    /// by the cost so far plus the distance left to a point.
    /// </summary>
    private sealed class Search
    {
        /// <summary>What <see cref="Run"/> returns once every reachable node has been expanded.</summary>
        public const int Exhausted = -1;

        /// <summary>What <see cref="Run"/> returns when it stops at <see cref="MaximumExpansions"/>.</summary>
        public const int GaveUp = -2;

        private readonly NavGrid _grid;
        private readonly Vector3 _toward;
        private readonly float _reach;
        private readonly IReadOnlyList<NavAvoidance> _avoided;
        private readonly IReadOnlyList<NavAvoidance> _crowd;
        private readonly SearchProfile _profile;
        private readonly NavLeapFinder? _leaps;
        private readonly float[] _cost;
        private readonly PriorityQueue<int, float> _open = new();

        public Search(
            NavGrid grid,
            int start,
            Vector3 toward,
            float reach,
            IReadOnlyList<NavAvoidance> avoided,
            IReadOnlyList<NavAvoidance> crowd,
            SearchProfile profile,
            NavLeapFinder? leaps)
        {
            _grid = grid;
            _toward = toward;
            _reach = reach;
            _avoided = avoided;
            _crowd = crowd;
            _profile = profile;
            _leaps = leaps;
            _cost = new float[grid.NodeCount];
            Array.Fill(_cost, float.PositiveInfinity);
            Parent = new int[grid.NodeCount];
            Closed = new bool[grid.NodeCount];
            _cost[start] = 0f;
            Parent[start] = start;
            _open.Enqueue(start, Remaining(grid.Position(start), toward, reach));
        }

        public int[] Parent { get; }

        /// <summary>The leap each node reached by one was last reached with.</summary>
        public Dictionary<int, NavLeap> LeapInto { get; } = [];

        /// <summary>The nodes expanded so far.</summary>
        public bool[] Closed { get; }

        public int Expansions { get; private set; }

        /// <summary>Expands nodes until one is an end and returns it, or returns <see cref="Exhausted"/> or <see cref="GaveUp"/>.</summary>
        public int Run(Func<int, bool> isEnd)
        {
            while (_open.TryDequeue(out int node, out _))
            {
                if (Closed[node])
                    continue;
                Closed[node] = true;
                Expansions++;
                if (isEnd(node))
                    return node;
                if (Expansions >= MaximumExpansions)
                    return GaveUp;

                Vector3 here = _grid.Position(node);
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = _grid.Link(node, direction);
                    if (next < 0 || Closed[next] || !_grid.IsClear(next))
                        continue;
                    Vector3 there = _grid.Position(next);
                    if (IsAvoided(there, _avoided, here))
                        continue;
                    float step = (_grid.CellSize * (direction < 4 ? 1f : DiagonalStep))
                        + MathF.Abs(there.Z - here.Z)
                        + WallCost(_grid, next, _profile)
                        + CrowdCostAt(there);
                    float total = _cost[node] + step;
                    if (total >= _cost[next])
                        continue;
                    _cost[next] = total;
                    Parent[next] = node;
                    LeapInto.Remove(next);
                    _open.Enqueue(next, total + Remaining(there, _toward, _reach));
                }
                if (_leaps is not null)
                    RelaxLeaps(node, here);
            }
            return Exhausted;
        }

        private float CrowdCostAt(Vector3 point) =>
            _crowd.Count == 0 ? 0f : CrowdCost * CrowdPrice(point, _crowd);

        private void RelaxLeaps(int node, Vector3 here)
        {
            foreach (NavLeap leap in _leaps!.From(node))
            {
                int next = leap.To;
                if (Closed[next] || !_grid.IsStandable(next))
                    continue;
                Vector3 there = _grid.Position(next);
                if (IsAvoided(there, _avoided, here))
                    continue;
                float total = _cost[node]
                    + Vector2.Distance(new Vector2(here.X, here.Y), new Vector2(there.X, there.Y))
                    + MathF.Abs(there.Z - here.Z)
                    + LeapCost
                    + (leap.Power * LeapPowerCost)
                    + (leap.Risk * LeapRiskCost)
                    + CrowdCostAt(there);
                if (total >= _cost[next])
                    continue;
                _cost[next] = total;
                Parent[next] = node;
                LeapInto[next] = leap;
                _open.Enqueue(next, total + Remaining(there, _toward, _reach));
            }
        }
    }

    /// <summary>
    /// Which nodes count as arriving at a goal: near enough to it, and able to
    /// see it along a line that crosses no wall and no avoided spot.
    /// </summary>
    /// <summary>How far across and up or down from a place the node it stands on may lie.</summary>
    private const float GoalFloorReach = 0.75f;
    private const float GoalFloorHeight = 0.5f;

    /// <summary>
    /// The floor a place stands on: the piece of floor of the nearest node that stands under
    /// the point, or -1 with no such node, when any floor within reach of the goal will do.
    /// </summary>
    private static (int[] Pieces, int Piece) GoalFloor(NavGrid grid, Vector3 goal)
    {
        int[] pieces = NavLeapFinder.FloorPieces(grid);
        int centreX = (int)MathF.Floor((goal.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((goal.Y - grid.OriginY) / grid.CellSize);
        int reach = (int)MathF.Ceiling(GoalFloorReach / grid.CellSize);
        int piece = -1;
        float nearest = float.PositiveInfinity;
        for (int y = centreY - reach; y <= centreY + reach; y++)
        {
            for (int x = centreX - reach; x <= centreX + reach; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    if (pieces[node] < 0)
                        continue;
                    Vector3 at = grid.Position(node);
                    if (MathF.Abs(at.Z - goal.Z) > GoalFloorHeight)
                        continue;
                    float away = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(goal.X, goal.Y));
                    if (away > GoalFloorReach || away >= nearest)
                        continue;
                    nearest = away;
                    piece = pieces[node];
                }
            }
        }
        return (pieces, piece);
    }

    /// <summary>How far above or below an object's surface a node still stands on it.</summary>
    private const float TopStandTolerance = 0.2f;

    /// <summary>
    /// How far below the highest spot on an object its top still reaches: less than a step.
    /// </summary>
    private const float TopBand = 0.25f;

    /// <summary>The steepest surface of an object counted as something to stand on, as the cosine of its slope.</summary>
    private const float TopSlopeCosine = 0.5f;

    /// <summary>
    /// The top of an object, and the spot on it nearest its middle. Of the nodes a body stands
    /// on over the object's own upward-facing surfaces, the top is those on the piece of floor
    /// of the highest, over the faces that lie wholly within <see cref="TopBand"/> of it, so a
    /// ramp or stair up to a deck is not taken for the deck. Where no face does, as on one
    /// sloping face, it is the nodes within that band. False when no node stands on the object.
    /// </summary>
    internal static bool TryFindTop(NavGrid grid, NavSurfaces surfaces, out Vector3 goal, out HashSet<int> top)
    {
        int[] pieces = NavLeapFinder.FloorPieces(grid);
        var faces = new List<List<int>>();
        foreach (NavTriangle triangle in surfaces.Triangles)
            AddStandingOn(grid, pieces, triangle, faces);
        foreach (NavCylinder cylinder in surfaces.Cylinders)
            AddStandingOn(grid, pieces, cylinder, faces);

        top = [];
        goal = default;
        int highest = -1;
        foreach (List<int> face in faces)
        {
            foreach (int node in face)
            {
                if (highest < 0 || grid.Position(node).Z > grid.Position(highest).Z)
                    highest = node;
            }
        }
        if (highest < 0)
            return false;

        float floor = grid.Position(highest).Z - TopBand;
        foreach (List<int> face in faces)
        {
            if (face.All(node => pieces[node] != pieces[highest] || grid.Position(node).Z >= floor))
                top.UnionWith(face.Where(node => pieces[node] == pieces[highest]));
        }
        if (top.Count == 0)
        {
            foreach (List<int> face in faces)
                top.UnionWith(face.Where(node => pieces[node] == pieces[highest] && grid.Position(node).Z >= floor));
        }

        Vector2 middle = Vector2.Zero;
        foreach (int node in top)
            middle += new Vector2(grid.Position(node).X, grid.Position(node).Y);
        middle /= top.Count;
        float nearest = float.PositiveInfinity;
        foreach (int node in top)
        {
            Vector3 at = grid.Position(node);
            float away = Vector2.Distance(new Vector2(at.X, at.Y), middle);
            if (away < nearest)
            {
                nearest = away;
                goal = at;
            }
        }
        return true;
    }

    private static void AddStandingOn(NavGrid grid, int[] pieces, NavTriangle triangle, List<List<int>> faces)
    {
        Vector3 normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
        float length = normal.Length();
        if (length < 1e-6f || MathF.Abs(normal.Z) < TopSlopeCosine * length)
            return;
        float minimumX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        float maximumX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        float minimumY = MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y));
        float maximumY = MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y));
        var face = new List<int>();
        ForColumns(grid, minimumX, minimumY, maximumX, maximumY, (x, y, centre) =>
        {
            if (TryHeightOver(triangle, centre, out float height))
                AddNodesAt(grid, pieces, x, y, height, face);
        });
        if (face.Count > 0)
            faces.Add(face);
    }

    /// <summary>A cylinder's flat top is one face; a dome curves away, so each spot on it is a face of its own.</summary>
    private static void AddStandingOn(NavGrid grid, int[] pieces, NavCylinder cylinder, List<List<int>> faces)
    {
        var axis = new Vector2(cylinder.Base.X, cylinder.Base.Y);
        var flat = new List<int>();
        ForColumns(
            grid,
            axis.X - cylinder.Radius,
            axis.Y - cylinder.Radius,
            axis.X + cylinder.Radius,
            axis.Y + cylinder.Radius,
            (x, y, centre) =>
            {
                float away = Vector2.Distance(centre, axis);
                if (away > cylinder.Radius)
                    return;
                if (!cylinder.Dome)
                {
                    AddNodesAt(grid, pieces, x, y, cylinder.Base.Z + cylinder.Height, flat);
                    return;
                }
                var spot = new List<int>();
                AddNodesAt(grid, pieces, x, y, cylinder.Base.Z + cylinder.Radius + MathF.Sqrt((cylinder.Radius * cylinder.Radius) - (away * away)), spot);
                if (spot.Count > 0)
                    faces.Add(spot);
            });
        if (flat.Count > 0)
            faces.Add(flat);
    }

    private static void ForColumns(NavGrid grid, float minimumX, float minimumY, float maximumX, float maximumY, Action<int, int, Vector2> visit)
    {
        int firstX = Math.Max(0, (int)MathF.Floor((minimumX - grid.OriginX) / grid.CellSize));
        int lastX = Math.Min(grid.Side - 1, (int)MathF.Floor((maximumX - grid.OriginX) / grid.CellSize));
        int firstY = Math.Max(0, (int)MathF.Floor((minimumY - grid.OriginY) / grid.CellSize));
        int lastY = Math.Min(grid.Side - 1, (int)MathF.Floor((maximumY - grid.OriginY) / grid.CellSize));
        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                visit(x, y, new Vector2(
                    grid.OriginX + ((x + 0.5f) * grid.CellSize),
                    grid.OriginY + ((y + 0.5f) * grid.CellSize)));
            }
        }
    }

    /// <summary>The nodes of a column, on a piece of floor, standing within <see cref="TopStandTolerance"/> of a height.</summary>
    private static void AddNodesAt(NavGrid grid, int[] pieces, int x, int y, float height, List<int> standing)
    {
        (int first, int count) = grid.NodesInColumn(x, y);
        for (int node = first; node < first + count; node++)
        {
            if (pieces[node] >= 0 && MathF.Abs(grid.Position(node).Z - height) <= TopStandTolerance)
                standing.Add(node);
        }
    }

    /// <summary>The height of a triangle over a flat point inside it, measured straight up.</summary>
    private static bool TryHeightOver(NavTriangle triangle, Vector2 point, out float height)
    {
        height = 0f;
        Vector2 a = new(triangle.A.X, triangle.A.Y);
        Vector2 b = new(triangle.B.X, triangle.B.Y);
        Vector2 c = new(triangle.C.X, triangle.C.Y);
        float area = Cross(b - a, c - a);
        if (MathF.Abs(area) < 1e-8f)
            return false;
        float u = Cross(b - point, c - point) / area;
        float v = Cross(c - point, a - point) / area;
        float w = 1f - u - v;
        const float edge = -1e-4f;
        if (u < edge || v < edge || w < edge)
            return false;
        height = (u * triangle.A.Z) + (v * triangle.B.Z) + (w * triangle.C.Z);
        return true;
    }

    private static float Cross(Vector2 left, Vector2 right) => (left.X * right.Y) - (left.Y * right.X);

    private sealed class GoalTest
    {
        /// <summary>How many nodes near the goal are tried for a line of sight before planning goes ahead regardless.</summary>
        private const int MaximumProbes = 2048;

        private readonly NavGrid _grid;
        private readonly Vector3 _goal;
        private readonly float _reach;
        private readonly IReadOnlyList<NavAvoidance> _avoided;
        private readonly Dictionary<int, bool> _sight = [];

        /// <summary>Which nodes a route may end on, such as those on the goal's own floor, or null for any.</summary>
        private readonly Func<int, bool>? _accepts;

        /// <summary>How far the goal object's side stands from its middle, which is what a body sees of it; zero for a point.</summary>
        private readonly float _radius;

        public GoalTest(
            NavGrid grid,
            Vector3 goal,
            float reach,
            IReadOnlyList<NavAvoidance> avoided,
            Func<int, bool>? accepts = null,
            float radius = 0f)
        {
            _grid = grid;
            _goal = goal;
            _reach = reach;
            _avoided = avoided;
            _accepts = accepts;
            _radius = radius;
        }

        public bool IsReachedAt(int node)
        {
            Vector3 at = _grid.Position(node);
            float dx = at.X - _goal.X;
            float dy = at.Y - _goal.Y;
            if ((dx * dx) + (dy * dy) > _reach * _reach || MathF.Abs(at.Z - _goal.Z) > GoalHeightTolerance)
                return false;
            if (_accepts is not null && !_accepts(node))
                return false;
            return Sees(node);
        }

        /// <summary>The reached node within <see cref="FallbackReach"/> of the goal nearest to it that can see it, or -1.</summary>
        public int NearestSeeingAmong(bool[] reached)
        {
            foreach ((int node, _) in ReachedNear(reached))
            {
                if (Sees(node))
                    return node;
            }
            return -1;
        }

        /// <summary>The reached node within <see cref="FallbackReach"/> of the goal nearest to it, or -1.</summary>
        public int NearestAmong(bool[] reached)
        {
            List<(int Node, float Distance)> near = ReachedNear(reached);
            return near.Count == 0 ? -1 : near[0].Node;
        }

        private List<(int Node, float Distance)> ReachedNear(bool[] reached)
        {
            int centreX = (int)MathF.Floor((_goal.X - _grid.OriginX) / _grid.CellSize);
            int centreY = (int)MathF.Floor((_goal.Y - _grid.OriginY) / _grid.CellSize);
            int reach = (int)MathF.Ceiling(FallbackReach / _grid.CellSize);
            var candidates = new List<(int Node, float Distance)>();
            for (int y = centreY - reach; y <= centreY + reach; y++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    (int first, int count) = _grid.NodesInColumn(x, y);
                    for (int node = first; node < first + count; node++)
                    {
                        if (!reached[node] || !_grid.IsStandable(node))
                            continue;
                        Vector3 at = _grid.Position(node);
                        if (MathF.Abs(at.Z - _goal.Z) > GoalHeightTolerance)
                            continue;
                        float distance = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(_goal.X, _goal.Y));
                        if (distance <= FallbackReach)
                            candidates.Add((node, distance));
                    }
                }
            }
            candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
            return candidates;
        }

        private bool Sees(int node)
        {
            if (_sight.TryGetValue(node, out bool sees))
                return sees;
            Vector3 at = _grid.Position(node);
            Vector3 seen = SideFacing(at);
            sees = _grid.CanSee(node, seen) && !PassesAvoided(at, seen, _avoided);
            _sight[node] = sees;
            return sees;
        }

        /// <summary>
        /// The point of the goal a body at <paramref name="at"/> looks at: its middle for a point,
        /// and for an object the side of it facing the body, since the object's own collision
        /// hides its middle. The side is taken a cell and a half out from the object's radius,
        /// since the grid holds walls in whole columns and a line to the very edge of a round
        /// object's collision would graze the column it fills.
        /// </summary>
        private Vector3 SideFacing(Vector3 at)
        {
            var toward = new Vector2(at.X - _goal.X, at.Y - _goal.Y);
            float away = toward.Length();
            if (_radius <= 0f || away < 1e-3f)
                return _goal;
            float side = MathF.Min(_radius + (_grid.CellSize * 1.5f), away);
            return _goal + new Vector3(toward * (side / away), 0f);
        }

        /// <summary>False only when every clear node near enough to the goal was tried and none can see it.</summary>
        public bool MaySucceed()
        {
            int centreX = (int)MathF.Floor((_goal.X - _grid.OriginX) / _grid.CellSize);
            int centreY = (int)MathF.Floor((_goal.Y - _grid.OriginY) / _grid.CellSize);
            int reach = (int)MathF.Ceiling(_reach / _grid.CellSize);
            int probes = 0;
            for (int y = centreY - reach; y <= centreY + reach; y++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    (int first, int count) = _grid.NodesInColumn(x, y);
                    for (int node = first; node < first + count; node++)
                    {
                        if (!_grid.IsStandable(node))
                            continue;
                        if (++probes > MaximumProbes)
                            return true;
                        if (IsReachedAt(node))
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
