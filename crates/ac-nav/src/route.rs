//! Routes over a [`NavGrid`], found with A*. A route starts from a clear
//! node the start can walk to without passing through a wall, and ends at a
//! clear node near the goal from which a body can see the goal, so a wall
//! between the two never counts as arriving.

use std::collections::HashMap;
use std::time::Instant;

use glam::{Vec2, Vec3};

use crate::geometry::{NavCylinder, NavSurfaces, NavTriangle};
use crate::grid::{DIRECTION_COUNT, NavGrid};
use crate::leaps::{NavLeap, NavLeapAbility, NavLeapFinder, NavLeapLimits, NavRouteLeap};
use crate::order::{
    InsertionSet, QuaternaryHeap, at_most_one_decimal, compare_f32, fixed, intro_sort,
};

/// How a route search ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NavRouteOutcome {
    /// A route was found.
    Routed,
    /// No clear node near the start can be walked to without passing a
    /// wall.
    NoStart,
    /// No clear node near enough to the goal can see it.
    NoGoal,
    /// No clear path joins the start to a node that can see the goal.
    NoPath,
}

/// A spot a route keeps out of, such as where a walk last stopped making
/// progress, or where a creature stands.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavAvoidance {
    /// The spot's middle.
    pub centre: Vec3,
    /// How far it reaches, metres.
    pub radius: f32,
}

impl NavAvoidance {
    /// A spot from its middle and radius.
    #[must_use]
    pub const fn new(centre: Vec3, radius: f32) -> Self {
        Self { centre, radius }
    }
}

/// A route over a [`NavGrid`]: the nodes it steps through, and the straight
/// legs between them that a body can walk.
#[derive(Debug, Clone, PartialEq)]
pub struct NavRoute {
    /// How the search ended.
    pub outcome: NavRouteOutcome,
    /// Why, in words: `"routed"` for a route that arrives.
    pub reason: String,
    /// Every node stepped through.
    pub path: Vec<Vec3>,
    /// The corners of the straight legs, starting where the body starts.
    pub legs: Vec<Vec3>,
    /// The length of the legs, metres.
    pub length: f32,
    /// How many nodes the search expanded.
    pub expansions: i32,
    /// How long the search took.
    pub milliseconds: f64,
    /// The legs flown as standing long jumps rather than walked, in order.
    pub leaps: Vec<NavRouteLeap>,
    /// Whether the route ends where the body can see its goal. False only
    /// for a route that ends at the nearest spot it reaches because no spot
    /// it reaches can see the goal.
    pub ends_in_sight: bool,
    /// How much of the route's legs is walked nearer walls and ledges than
    /// is comfortable, square metres: the shortfall at each step, times the
    /// step.
    pub scrape: f32,
    /// How much of the route's legs passes through the spots it was asked
    /// to pass creatures and players in, metres, each step counted by how
    /// deep in it is.
    pub crowding: f32,
}

/// The distance a body starts from its first node, metres.
pub const START_RADIUS: f32 = 1.5;
/// How far above or below its start a body's first node may stand, metres.
pub const START_HEIGHT_TOLERANCE: f32 = 1.0;
/// How far above or below the goal an arrival node may stand, metres.
pub const GOAL_HEIGHT_TOLERANCE: f32 = 1.2;
/// When no node within the arrival radius can be both reached and see the
/// goal, as for a vendor behind a counter, the route ends at the nearest
/// reachable node within this far that can see it. When none can see it,
/// as through a window whose collision fills the opening, and the route is
/// not keeping out of a spot where a walk was blocked, it ends at the
/// nearest reachable node within this far, without a line of sight.
pub const FALLBACK_REACH: f32 = 10.0;
/// The search gives up after expanding this many nodes.
pub const MAXIMUM_EXPANSIONS: i32 = 4_000_000;

/// How a search weighs walking near walls and ledges: a node nearer one
/// than `clearance` costs `weight` more to step onto for each metre it falls
/// short, so routes keep to the middle of doorways.
#[derive(Debug, Clone, Copy)]
struct SearchProfile {
    clearance: f32,
    weight: f32,
}

/// A route is planned in each of these ways at once, from one keeping well
/// clear of walls to the shortest, and the tidiest of the routes found is
/// kept. A route toward a goal beyond the grid is planned the first way
/// only.
const PROFILES: [SearchProfile; 3] = [
    SearchProfile {
        clearance: 1.25,
        weight: 0.5,
    },
    SearchProfile {
        clearance: 2.0,
        weight: 1.0,
    },
    SearchProfile {
        clearance: 0.0,
        weight: 0.0,
    },
];

/// What makes a route untidy to walk, in metres added to its length: each
/// leg, each turn sharper than [`SHARP_TURN_DEGREES`], and each square metre
/// of shortfall from [`COMFORTABLE_LEG_CLEARANCE`] along its legs.
const LEG_UNTIDINESS: f32 = 0.5;
const TURN_UNTIDINESS: f32 = 2.0;
const SCRAPE_UNTIDINESS: f32 = 2.0;
const SHARP_TURN_DEGREES: f32 = 30.0;

/// What a leap costs a route beyond the distance it covers, metres, and
/// more for each share of full power.
const LEAP_COST: f32 = 3.0;
const LEAP_POWER_COST: f32 = 2.0;
/// What each unit of a leap's risk costs a route, metres, so a route takes
/// an easy leap over a risky one wherever it can.
const LEAP_RISK_COST: f32 = 6.0;
/// A route toward a goal beyond the grid found with leaps is taken only when
/// it ends this much nearer the goal, metres.
const LEAP_WORTH_METERS: f32 = 1.0;
/// How far from where a walk that did not arrive came nearest its goal the
/// leaps sought after it may take off, metres: those that cross what stopped
/// the walk there.
pub const LEAP_TAKEOFF_METERS: f32 = 48.0;

/// A route may pass through the spots creatures and players stand in, since
/// they move aside when bumped, but it pays to. Stepping onto a node inside
/// one costs up to [`CROWD_COST`] more: [`CROWD_EDGE_SHARE`] of it just
/// inside the edge, so grazing a creature costs as surely as walking into
/// it, and the rest by how deep in. Each metre through one makes a route
/// [`CROWD_UNTIDINESS`] metres untidier. Legs shaped from a searched path go
/// no deeper into a spot than the path did, less [`CROWD_SHAPE_SLACK`]. A
/// route around them is taken only when it walks no nearer walls and ledges
/// than the route through them, give or take [`CROWD_SCRAPE_ALLOWANCE`]
/// square metres.
const CROWD_COST: f32 = 2.0;
const CROWD_EDGE_SHARE: f32 = 0.5;
const CROWD_SHAPE_SLACK: f32 = 0.05;
const CROWD_UNTIDINESS: f32 = 8.0;
const CROWD_SCRAPE_ALLOWANCE: f32 = 0.25;

const REACHED_EDGE_REASON: &str =
    "the goal lies beyond this grid, so the route ends at its edge toward the goal";

/// A corner a route turns at is moved, when it helps and there is room, so
/// that both legs through it keep up to this far from walls and ledges, the
/// way a walker takes a corner wide instead of brushing it. A corner moves
/// at most [`CORNER_SHIFT`], and only for a gain of at least
/// [`CORNER_GAIN`].
const COMFORTABLE_LEG_CLEARANCE: f32 = 1.0;
const CORNER_SHIFT: f32 = 1.5;
const CORNER_GAIN: f32 = 0.1;

/// How far above or below an avoided spot a node is still kept out of it,
/// metres.
const AVOIDANCE_HEIGHT: f32 = 2.0;
/// A start this far from its node walks to the node first when it cannot
/// walk the first leg straight, metres.
const START_OFF_NODE: f32 = 0.3;
const DIAGONAL_STEP: f32 = std::f32::consts::SQRT_2;

/// How far across and up or down from a place the node it stands on may
/// lie, metres.
const GOAL_FLOOR_REACH: f32 = 0.75;
const GOAL_FLOOR_HEIGHT: f32 = 0.5;
/// How far above or below an object's surface a node still stands on it.
const TOP_STAND_TOLERANCE: f32 = 0.2;
/// How far below the highest spot on an object its top still reaches: less
/// than a step.
const TOP_BAND: f32 = 0.25;
/// The steepest surface of an object counted as something to stand on, as
/// the cosine of its slope.
const TOP_SLOPE_COSINE: f32 = 0.5;

/// What a route search may end on besides being near enough and in sight.
#[derive(Clone, Copy)]
enum Accepts<'a> {
    /// Any node.
    Any,
    /// Only nodes of an object's top.
    Onto(&'a InsertionSet),
    /// Only nodes on the goal's own piece of floor.
    Piece(&'a [i32], i32),
}

impl Accepts<'_> {
    fn is_any(&self) -> bool {
        matches!(self, Self::Any)
    }

    fn accepts(&self, node: i32) -> bool {
        match self {
            Self::Any => true,
            Self::Onto(top) => top.contains(node),
            Self::Piece(pieces, piece) => pieces[node as usize] == *piece,
        }
    }
}

/// What a caller asks of a route besides where it goes.
#[derive(Debug, Clone, Copy, Default)]
pub struct NavRouteRequest<'a> {
    /// Spots the route keeps out of, such as where a walk was blocked.
    pub avoid: &'a [NavAvoidance],
    /// What the body can leap; `None` plans walked routes only.
    pub leaps: Option<NavLeapAbility>,
    /// Spots creatures and players stand in, passed around where that keeps
    /// as clear of walls as going through them.
    pub crowd: &'a [NavAvoidance],
    /// Whether the route must end on the floor the goal stands on.
    pub arrive_on_goal_floor: bool,
    /// How far the goal object's side stands from its middle; zero for a
    /// point.
    pub goal_radius: f32,
}

/// Plans a route in each of the search profiles at once and returns the
/// tidiest: a route that arrives before one that ends short of the goal, and
/// of those alike, the one least untidy to walk. When no walked route
/// arrives and the request says what the body can leap, the route is
/// planned again the first way with leaps, and the better of the two is
/// returned. Those leaps take off within [`LEAP_TAKEOFF_METERS`] of where the
/// walk came nearest the goal, so a body that jumps far does not aim at
/// every rock and roof of the grid.
#[must_use]
pub fn find(
    grid: &NavGrid,
    from: Vec3,
    to: Vec3,
    arrival_radius: f32,
    request: &NavRouteRequest<'_>,
) -> NavRoute {
    find_with(
        grid,
        from,
        to,
        arrival_radius,
        request.avoid,
        request.leaps,
        request.crowd,
        request.arrive_on_goal_floor,
        None,
        request.goal_radius,
    )
}

/// Finds a route onto an object whose collision is `surfaces`, ending on
/// its top: the highest floor standing on the object's own surfaces that a
/// body stands on, within `arrival_radius` of that floor's middle. A route
/// never ends on the ground beside the object, and fails when nothing on
/// top of it can be stood on or no route, leaping where the request allows,
/// gets up there.
#[must_use]
pub fn find_onto(
    grid: &NavGrid,
    from: Vec3,
    surfaces: &NavSurfaces,
    arrival_radius: f32,
    request: &NavRouteRequest<'_>,
) -> NavRoute {
    let clock = Instant::now();
    let Some((goal, top)) = try_find_top(grid, surfaces) else {
        return failed(
            NavRouteOutcome::NoGoal,
            "nothing on top of the object can be stood on".to_owned(),
            0,
            clock,
        );
    };
    find_with(
        grid,
        from,
        goal,
        arrival_radius,
        request.avoid,
        request.leaps,
        request.crowd,
        false,
        Some(&top),
        0.0,
    )
}

#[allow(clippy::too_many_arguments)]
fn find_with(
    grid: &NavGrid,
    from: Vec3,
    to: Vec3,
    arrival_radius: f32,
    avoid: &[NavAvoidance],
    leaps: Option<NavLeapAbility>,
    crowd: &[NavAvoidance],
    arrive_on_goal_floor: bool,
    onto: Option<&InsertionSet>,
    goal_radius: f32,
) -> NavRoute {
    let clock = Instant::now();
    let priced = !crowd.is_empty();
    let count = if priced {
        PROFILES.len() * 2
    } else {
        PROFILES.len()
    };
    let (plans, nearest): (Vec<(NavRoute, f32)>, Vec<Vec3>) = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..count)
            .map(|index| {
                scope.spawn(move || {
                    let mut nearest = from;
                    let planned = plan(
                        grid,
                        from,
                        to,
                        arrival_radius,
                        avoid,
                        crowd,
                        priced && index < PROFILES.len(),
                        PROFILES[index % PROFILES.len()],
                        None,
                        arrive_on_goal_floor,
                        onto,
                        goal_radius,
                        &mut nearest,
                    );
                    (planned, nearest)
                })
            })
            .collect();
        handles
            .into_iter()
            .map(|handle| handle.join().expect("a route search panicked"))
            .unzip()
    });
    let mut tidiest = tidiest_of(&plans[..PROFILES.len()]);
    if priced {
        let through = tidiest_of(&plans[PROFILES.len()..]);
        if rank(&through.0) < rank(&tidiest.0)
            || (rank(&through.0) == rank(&tidiest.0)
                && tidiest.0.scrape > through.0.scrape + CROWD_SCRAPE_ALLOWANCE)
        {
            tidiest = through;
        }
    }
    if rank(&tidiest.0) > 0
        && let Some(ability) = leaps
    {
        let frontier = nearest
            .into_iter()
            .min_by(|a, b| away(*a, to).total_cmp(&away(*b, to)))
            .unwrap_or(from);
        let finder = NavLeapFinder::limited(
            grid,
            ability,
            NavLeapLimits {
                takeoff_near: frontier,
                takeoff_radius: LEAP_TAKEOFF_METERS,
                toward: to,
                landing_within: f32::INFINITY,
            },
        );
        let mut came_nearest = from;
        let leapt = plan(
            grid,
            from,
            to,
            arrival_radius,
            avoid,
            crowd,
            priced,
            PROFILES[0],
            Some(&finder),
            arrive_on_goal_floor,
            onto,
            goal_radius,
            &mut came_nearest,
        );
        if rank(&leapt.0) < rank(&tidiest.0) {
            tidiest = leapt;
        }
    }
    let mut route = tidiest.0;
    route.milliseconds = elapsed(clock);
    route
}

/// The plan that ranks first, and of plans alike, the least untidy.
fn tidiest_of(plans: &[(NavRoute, f32)]) -> (NavRoute, f32) {
    let mut tidiest = &plans[0];
    for plan in plans {
        let rank_here = rank(&plan.0);
        if rank_here < rank(&tidiest.0) || (rank_here == rank(&tidiest.0) && plan.1 < tidiest.1) {
            tidiest = plan;
        }
    }
    tidiest.clone()
}

/// A route that arrives ranks first, then one that ends short of the goal,
/// then one that failed.
fn rank(route: &NavRoute) -> i32 {
    if route.outcome != NavRouteOutcome::Routed {
        2
    } else if route.reason == "routed" {
        0
    } else {
        1
    }
}

/// Plans one route in one profile, with its untidiness; `nearest` is moved
/// to each node expanded that lies nearer the goal, across, than it.
#[allow(clippy::too_many_arguments)]
fn plan(
    grid: &NavGrid,
    from: Vec3,
    to: Vec3,
    arrival_radius: f32,
    avoided: &[NavAvoidance],
    crowd: &[NavAvoidance],
    price_crowd: bool,
    profile: SearchProfile,
    leaps: Option<&NavLeapFinder<&NavGrid>>,
    arrive_on_goal_floor: bool,
    onto: Option<&InsertionSet>,
    goal_radius: f32,
    nearest: &mut Vec3,
) -> (NavRoute, f32) {
    let clock = Instant::now();
    let object_radius = if goal_radius.is_finite() {
        goal_radius.max(0.0)
    } else {
        0.0
    };
    let reach = arrival_radius.max(grid.cell_size()) + object_radius;
    let mut start = grid.find_walkable_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    if start < 0 {
        start = grid.find_standing_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    }
    if start < 0 {
        return (no_start(clock), f32::INFINITY);
    }
    let goal_floor = if arrive_on_goal_floor && onto.is_none() {
        Some(goal_floor(grid, to))
    } else {
        None
    };
    let accepts = match (onto, &goal_floor) {
        (Some(top), _) => Accepts::Onto(top),
        (None, Some((pieces, piece))) if *piece >= 0 => Accepts::Piece(pieces, *piece),
        _ => Accepts::Any,
    };
    let mut goal = GoalTest::new(grid, to, reach, avoided, accepts, object_radius);
    let near_goal_seen = goal.may_succeed();

    let crowd_priced: &[NavAvoidance] = if price_crowd { crowd } else { &[] };
    let mut search = Search::new(
        grid,
        start,
        to,
        reach,
        avoided,
        crowd_priced,
        profile,
        leaps,
    );
    let mut nearest_away = away(*nearest, to);
    let end = search.run(|node, _| {
        let at = grid.position(node);
        let away_here = away(at, to);
        if away_here < nearest_away {
            *nearest = at;
            nearest_away = away_here;
        }
        near_goal_seen && goal.is_reached_at(node)
    });
    if end >= 0 {
        return routed(
            grid,
            from,
            start,
            end,
            &search,
            avoided,
            crowd,
            price_crowd,
            clock,
            "routed".to_owned(),
        );
    }
    if end == Search::GAVE_UP {
        return (search_gave_up(&search, clock), f32::INFINITY);
    }
    if !accepts.is_any() {
        // Ending anywhere but the floor the goal stands on would be arriving
        // beside it, such as on the ground below a rock top, so no nearer
        // spot will do.
        let reason = if onto.is_some() {
            "no route reaches the top of the object"
        } else {
            "no route reaches the floor the goal stands on"
        };
        return (
            failed(
                NavRouteOutcome::NoPath,
                reason.to_owned(),
                search.expansions,
                clock,
            ),
            f32::INFINITY,
        );
    }
    let nearest = goal.nearest_seeing_among(&search.closed);
    if nearest >= 0 {
        let at = grid.position(nearest);
        let away = at.truncate().distance(to.truncate());
        return routed(
            grid,
            from,
            start,
            nearest,
            &search,
            avoided,
            crowd,
            price_crowd,
            clock,
            format!(
                "nothing within {} m of the goal can be reached and see it, so the route ends at the nearest spot that can, {} m from it",
                at_most_one_decimal(reach),
                fixed(away, 1)
            ),
        );
    }
    let nearest_unseeing = if avoided.is_empty() {
        goal.nearest_among(&search.closed)
    } else {
        -1
    };
    if nearest_unseeing >= 0 {
        let at = grid.position(nearest_unseeing);
        let away = at.truncate().distance(to.truncate());
        let (mut unseeing, untidiness) = routed(
            grid,
            from,
            start,
            nearest_unseeing,
            &search,
            avoided,
            crowd,
            price_crowd,
            clock,
            format!(
                "no reachable spot can see the goal, so the route ends at the nearest spot, {} m from it, without a line of sight",
                fixed(away, 1)
            ),
        );
        unseeing.ends_in_sight = false;
        return (unseeing, untidiness);
    }
    let failure = if near_goal_seen {
        failed(
            NavRouteOutcome::NoPath,
            format!(
                "no clear path leads from the start to a spot within {} m that can see the goal",
                fixed(FALLBACK_REACH, 0)
            ),
            search.expansions,
            clock,
        )
    } else {
        failed(
            NavRouteOutcome::NoGoal,
            format!(
                "no spot within {} m of the goal that the start can reach can see it",
                fixed(FALLBACK_REACH, 0)
            ),
            search.expansions,
            clock,
        )
    };
    (failure, f32::INFINITY)
}

/// Finds a route toward a goal that lies beyond the grid, for a walk too
/// long for one grid to hold: to the first reachable node within `band` of
/// the nearest the grid comes to the goal, or, when none is reachable, to
/// the reachable node nearest the goal. When no walked route reaches the
/// grid's edge toward the goal (the edge of its loaded ground) and the
/// request says what the body can leap, it is found again with leaps, and
/// taken when it ends nearer the goal. Those leaps take off within
/// [`LEAP_TAKEOFF_METERS`] of where the walk came nearest the goal
/// and come down nearer it, so a body that jumps far does not aim at every
/// rock and roof of the grid.
#[must_use]
pub fn find_toward(
    grid: &NavGrid,
    from: Vec3,
    goal: Vec3,
    band: f32,
    request: &NavRouteRequest<'_>,
) -> NavRoute {
    let clock = Instant::now();
    let mut route = find_toward_passes(grid, from, goal, band, request);
    // The whole search, its leaps' pass with it.
    route.milliseconds = elapsed(clock);
    route
}

/// [`find_toward`]'s walked pass and, where it falls short, its leaps'.
fn find_toward_passes(
    grid: &NavGrid,
    from: Vec3,
    goal: Vec3,
    band: f32,
    request: &NavRouteRequest<'_>,
) -> NavRoute {
    let crowd = request.crowd;
    let priced = !crowd.is_empty();
    let mut walked = find_toward_with(grid, from, goal, band, request.avoid, crowd, priced, None);
    if priced {
        let through = find_toward_with(grid, from, goal, band, request.avoid, crowd, false, None);
        if through.outcome == NavRouteOutcome::Routed
            && (walked.outcome != NavRouteOutcome::Routed
                || away(last(&through.legs), goal) + LEAP_WORTH_METERS
                    < away(last(&walked.legs), goal)
                || walked.scrape > through.scrape + CROWD_SCRAPE_ALLOWANCE)
        {
            walked = through;
        }
    }
    let Some(ability) = request.leaps else {
        return walked;
    };
    if walked.reason.starts_with(REACHED_EDGE_REASON) {
        return walked;
    }
    let frontier = if walked.outcome == NavRouteOutcome::Routed {
        last(&walked.legs)
    } else {
        from
    };
    let finder = NavLeapFinder::limited(
        grid,
        ability,
        NavLeapLimits {
            takeoff_near: frontier,
            takeoff_radius: LEAP_TAKEOFF_METERS,
            toward: goal,
            landing_within: away(frontier, goal),
        },
    );
    let leapt = find_toward_with(
        grid,
        from,
        goal,
        band,
        request.avoid,
        crowd,
        priced,
        Some(&finder),
    );
    if leapt.outcome != NavRouteOutcome::Routed {
        return walked;
    }
    if walked.outcome != NavRouteOutcome::Routed
        || away(last(&leapt.legs), goal) + LEAP_WORTH_METERS < away(last(&walked.legs), goal)
    {
        leapt
    } else {
        walked
    }
}

fn last(points: &[Vec3]) -> Vec3 {
    *points.last().expect("a routed route has legs")
}

#[allow(clippy::too_many_arguments)]
fn find_toward_with(
    grid: &NavGrid,
    from: Vec3,
    goal: Vec3,
    band: f32,
    avoided: &[NavAvoidance],
    crowd: &[NavAvoidance],
    price_crowd: bool,
    leaps: Option<&NavLeapFinder<&NavGrid>>,
) -> NavRoute {
    let clock = Instant::now();
    let mut start = grid.find_walkable_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    if start < 0 {
        start = grid.find_standing_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    }
    if start < 0 {
        return no_start(clock);
    }

    // The edge toward the goal is that of the ground the grid was built
    // over: a landblock the client has not loaded yet offers no floor to
    // reach.
    let enough = grid.distance_beyond_ground(goal) + band.max(grid.cell_size());
    let mut nearest = start;
    let mut nearest_away = away(grid.position(start), goal);
    let crowd_priced: &[NavAvoidance] = if price_crowd { crowd } else { &[] };
    let mut search = Search::new(
        grid,
        start,
        goal,
        0.0,
        avoided,
        crowd_priced,
        PROFILES[0],
        leaps,
    );
    let end = search.run(|node, _| {
        let away_here = away(grid.position(node), goal);
        if away_here < nearest_away {
            nearest = node;
            nearest_away = away_here;
        }
        away_here <= enough
    });
    if end >= 0 {
        return routed(
            grid,
            from,
            start,
            end,
            &search,
            avoided,
            crowd,
            price_crowd,
            clock,
            format!(
                "{REACHED_EDGE_REASON}, {} m from it",
                fixed(nearest_away, 0)
            ),
        )
        .0;
    }
    if nearest != start && away(from, goal) - nearest_away >= grid.cell_size() {
        return routed(
            grid,
            from,
            start,
            nearest,
            &search,
            avoided,
            crowd,
            price_crowd,
            clock,
            format!(
                "the goal lies beyond this grid, and the route ends at the reachable spot nearest it, {} m from it",
                fixed(nearest_away, 0)
            ),
        )
        .0;
    }
    if end == Search::GAVE_UP {
        search_gave_up(&search, clock)
    } else {
        failed(
            NavRouteOutcome::NoPath,
            "no clear path leads any nearer the goal".to_owned(),
            search.expansions,
            clock,
        )
    }
}

#[allow(clippy::too_many_arguments)]
fn routed(
    grid: &NavGrid,
    from: Vec3,
    start: i32,
    end: i32,
    search: &Search<'_, '_>,
    avoided: &[NavAvoidance],
    crowd: &[NavAvoidance],
    price_crowd: bool,
    clock: Instant,
    reason: String,
) -> (NavRoute, f32) {
    let mut nodes = Vec::new();
    let mut node = end;
    loop {
        nodes.push(node);
        if node == start {
            break;
        }
        node = search.parent[node as usize];
    }
    nodes.reverse();

    let path: Vec<Vec3> = nodes.iter().map(|&node| grid.position(node)).collect();

    // Legs are shaped no deeper into crowd spots than the search went, so a
    // straighter leg never cuts back into a creature the route kept clear
    // of.
    let shaping: Vec<NavAvoidance> = if price_crowd {
        let mut shaping = avoided.to_vec();
        shaping.extend(kept_out(crowd, &path));
        shaping
    } else {
        avoided.to_vec()
    };

    // The walked stretches between leaps are straightened on their own, so a
    // leap's takeoff and landing stay where the search found them.
    let mut corners = Vec::new();
    let mut leaps: Vec<(usize, NavLeap)> = Vec::new();
    let mut stretch_start = 0;
    for index in 1..=nodes.len() {
        let mut leap = None;
        if index < nodes.len()
            && let Some(found) = search.leap_into.get(&nodes[index])
            && found.from == nodes[index - 1]
        {
            leap = Some(*found);
        }
        if index < nodes.len() && leap.is_none() {
            continue;
        }
        let stretch = &nodes[stretch_start..index];
        let mut stretch_corners = if stretch.len() == 1 {
            vec![stretch[0]]
        } else {
            straighten(grid, stretch, &path[stretch_start..index], &shaping)
        };
        drop_needless_corners(grid, &mut stretch_corners, &shaping);
        widen_corners(grid, &mut stretch_corners, &shaping);
        drop_needless_corners(grid, &mut stretch_corners, &shaping);
        corners.extend(stretch_corners);
        if let Some(leap) = leap {
            leaps.push((corners.len(), leap));
        }
        stretch_start = index;
    }
    let mut legs: Vec<Vec3> = corners
        .iter()
        .map(|&corner| grid.position(corner))
        .collect();

    let off_node = from.truncate().distance(path[0].truncate());
    let mut inserted = 0;
    if legs.len() > 1 && off_node > START_OFF_NODE && !grid.can_sweep(from, legs[1]) {
        legs.insert(0, from);
        inserted = 1;
    }

    let mut length = 0.0;
    for index in 1..legs.len() {
        length += legs[index - 1].distance(legs[index]);
    }
    let mut scrape_total = 0.0;
    for index in 1..corners.len() {
        scrape_total += scrape(grid, corners[index - 1], corners[index]);
    }
    let crowding_total = crowding(grid, &corners, crowd);
    let route = NavRoute {
        outcome: NavRouteOutcome::Routed,
        reason,
        path,
        legs,
        length,
        expansions: search.expansions,
        milliseconds: elapsed(clock),
        leaps: leaps
            .iter()
            .map(|(corner, leap)| NavRouteLeap {
                leg_index: (*corner + inserted) as i32,
                power: leap.power,
                run: leap.run,
            })
            .collect(),
        ends_in_sight: true,
        scrape: scrape_total,
        crowding: crowding_total,
    };
    let mut untidiness = untidiness_of(grid, &corners)
        + if price_crowd {
            CROWD_UNTIDINESS * crowding_total
        } else {
            0.0
        };
    for (_, leap) in &leaps {
        untidiness += LEAP_COST + leap.power * LEAP_POWER_COST + leap.risk * LEAP_RISK_COST;
    }
    (route, untidiness)
}

/// The nodes a route's straight legs join: from the start, each leg runs to
/// the farthest node along the path a body can walk to in a straight line.
fn straighten(grid: &NavGrid, nodes: &[i32], path: &[Vec3], avoided: &[NavAvoidance]) -> Vec<i32> {
    let mut corners = vec![nodes[0]];
    let mut anchor = 0;
    while anchor < nodes.len() - 1 {
        let mut furthest = anchor + 1;
        while furthest + 1 < nodes.len()
            && grid.can_walk_straight(nodes[anchor], nodes[furthest + 1])
            && !passes_avoided(path[anchor], path[furthest + 1], avoided)
        {
            furthest += 1;
        }
        corners.push(nodes[furthest]);
        anchor = furthest;
    }
    corners
}

/// Removes each corner whose neighbours a body can walk between in a
/// straight line keeping as far from walls and ledges as the two legs
/// through it did.
fn drop_needless_corners(grid: &NavGrid, corners: &mut Vec<i32>, avoided: &[NavAvoidance]) {
    let mut index = 1;
    while index + 1 < corners.len() {
        let before = corners[index - 1];
        let after = corners[index + 1];
        let kept = leg_clearance(grid, before, corners[index]).min(leg_clearance(
            grid,
            corners[index],
            after,
        ));
        if grid.can_walk_straight_keeping(before, after, kept, border_steps_for(grid, kept))
            && !passes_avoided(grid.position(before), grid.position(after), avoided)
        {
            corners.remove(index);
            index = (index - 1).max(1);
        } else {
            index += 1;
        }
    }
}

/// Moves each corner to a nearby node on the same floor from which both of
/// its legs keep farther from walls and ledges, up to
/// [`COMFORTABLE_LEG_CLEARANCE`], preferring the shortest such turn.
fn widen_corners(grid: &NavGrid, corners: &mut [i32], avoided: &[NavAvoidance]) {
    let mut index = 1;
    while index + 1 < corners.len() {
        let before = corners[index - 1];
        let corner = corners[index];
        let after = corners[index + 1];
        let kept = leg_clearance(grid, before, corner).min(leg_clearance(grid, corner, after));
        if kept + CORNER_GAIN > COMFORTABLE_LEG_CLEARANCE {
            index += 1;
            continue;
        }

        let at = grid.position(corner);
        let from = grid.position(before);
        let to = grid.position(after);
        let (centre_x, centre_y) = grid.column_of(corner);
        let reach = (CORNER_SHIFT / grid.cell_size()).ceil() as i32;
        let mut candidates: Vec<(i32, f32)> = Vec::new();
        for y in centre_y - reach..=centre_y + reach {
            for x in centre_x - reach..=centre_x + reach {
                let (first, count) = grid.nodes_in_column(x, y);
                for node in first..first + count {
                    let there = grid.position(node);
                    if node == corner
                        || !grid.is_clear(node)
                        || grid.wall_distance(node) < kept + CORNER_GAIN
                        || (there.z - at.z).abs() > grid.body().step_up_height
                        || there.truncate().distance(at.truncate()) > CORNER_SHIFT
                        || is_avoided(there, avoided, NOWHERE)
                    {
                        continue;
                    }
                    candidates.push((node, from.distance(there) + there.distance(to)));
                }
            }
        }
        intro_sort(&mut candidates, |l, r| compare_f32(l.1, r.1));

        let mut moved = false;
        let mut wanted = COMFORTABLE_LEG_CLEARANCE;
        while !moved && wanted >= kept + CORNER_GAIN {
            let border = border_steps_for(grid, wanted);
            for &(node, _) in &candidates {
                if grid.wall_distance(node) < wanted || grid.border_distance(node) < border {
                    continue;
                }
                let there = grid.position(node);
                if grid.can_walk_straight_keeping(before, node, wanted, border)
                    && grid.can_walk_straight_keeping(node, after, wanted, border)
                    && !passes_avoided(from, there, avoided)
                    && !passes_avoided(there, to, avoided)
                {
                    corners[index] = node;
                    moved = true;
                    break;
                }
            }
            wanted -= CORNER_GAIN;
        }
        index += 1;
    }
}

/// The farthest from walls and ledges, up to
/// [`COMFORTABLE_LEG_CLEARANCE`], that a body walking straight between two
/// nodes keeps.
fn leg_clearance(grid: &NavGrid, from: i32, to: i32) -> f32 {
    let mut low = grid.nearest_wall();
    let mut high = COMFORTABLE_LEG_CLEARANCE;
    if grid.can_walk_straight_keeping(from, to, high, border_steps_for(grid, high)) {
        return high;
    }
    for _ in 0..5 {
        let middle = (low + high) * 0.5;
        if grid.can_walk_straight_keeping(from, to, middle, border_steps_for(grid, middle)) {
            low = middle;
        } else {
            high = middle;
        }
    }
    low
}

/// How untidy a route's legs are to walk, metres: their length, with
/// [`LEG_UNTIDINESS`] for each leg, [`TURN_UNTIDINESS`] for each sharp
/// turn, and [`SCRAPE_UNTIDINESS`] for each square metre of shortfall from
/// [`COMFORTABLE_LEG_CLEARANCE`] along them.
fn untidiness_of(grid: &NavGrid, corners: &[i32]) -> f32 {
    let mut untidiness = LEG_UNTIDINESS * (corners.len() as f32 - 1.0);
    let sharp = (SHARP_TURN_DEGREES * (std::f32::consts::PI / 180.0)).cos();
    for index in 1..corners.len() {
        let a = grid.position(corners[index - 1]);
        let b = grid.position(corners[index]);
        untidiness +=
            a.distance(b) + SCRAPE_UNTIDINESS * scrape(grid, corners[index - 1], corners[index]);
        if index + 1 >= corners.len() {
            continue;
        }
        let c = grid.position(corners[index + 1]);
        let inbound = Vec2::new(b.x - a.x, b.y - a.y);
        let outbound = Vec2::new(c.x - b.x, c.y - b.y);
        if inbound.length_squared() > 1e-6
            && outbound.length_squared() > 1e-6
            && inbound.normalize().dot(outbound.normalize()) < sharp
        {
            untidiness += TURN_UNTIDINESS;
        }
    }
    untidiness
}

/// How much of a leg is walked nearer walls or ledges than
/// [`COMFORTABLE_LEG_CLEARANCE`], square metres: the shortfall at each step
/// along it, times the step.
fn scrape(grid: &NavGrid, from: i32, to: i32) -> f32 {
    let mut total = 0.0;
    for node in grid.nodes_along(from, to) {
        let clearance = grid
            .wall_distance(node)
            .min(grid.border_distance(node) as f32 * grid.cell_size());
        total += (COMFORTABLE_LEG_CLEARANCE - clearance).max(0.0) * grid.cell_size();
    }
    total
}

/// How much of a route's legs passes through crowd spots, metres, each step
/// counted by how deep in it is.
fn crowding(grid: &NavGrid, corners: &[i32], crowd: &[NavAvoidance]) -> f32 {
    if crowd.is_empty() {
        return 0.0;
    }
    let mut total = 0.0;
    for index in 1..corners.len() {
        for node in grid.nodes_along(corners[index - 1], corners[index]) {
            total += crowd_depth(grid.position(node), crowd) * grid.cell_size();
        }
    }
    total
}

/// How deep a point stands in crowd spots, summed over them: one at a
/// spot's middle, falling to nothing at its edge.
fn crowd_depth(point: Vec3, crowd: &[NavAvoidance]) -> f32 {
    let mut depth = 0.0;
    for spot in crowd {
        if spot.radius <= 0.0 || (point.z - spot.centre.z).abs() > AVOIDANCE_HEIGHT {
            continue;
        }
        let distance = flat_distance_squared(point, spot.centre).sqrt();
        if distance < spot.radius {
            depth += 1.0 - distance / spot.radius;
        }
    }
    depth
}

/// What stepping onto a point costs in crowd spots, summed over the spots
/// it is inside.
fn crowd_price(point: Vec3, crowd: &[NavAvoidance]) -> f32 {
    let mut price = 0.0;
    for spot in crowd {
        if spot.radius <= 0.0 || (point.z - spot.centre.z).abs() > AVOIDANCE_HEIGHT {
            continue;
        }
        let distance = flat_distance_squared(point, spot.centre).sqrt();
        if distance < spot.radius {
            price += CROWD_EDGE_SHARE + (1.0 - CROWD_EDGE_SHARE) * (1.0 - distance / spot.radius);
        }
    }
    price
}

/// The part of each crowd spot that legs shaped from a searched path keep
/// out of: all of a spot the path kept out of, and of one it went into,
/// what lies deeper than the path went.
fn kept_out(crowd: &[NavAvoidance], path: &[Vec3]) -> Vec<NavAvoidance> {
    let mut kept_spots = Vec::new();
    for spot in crowd {
        let mut nearest = spot.radius;
        for point in path {
            if (point.z - spot.centre.z).abs() <= AVOIDANCE_HEIGHT {
                nearest = nearest.min(flat_distance_squared(*point, spot.centre).sqrt());
            }
        }
        let kept = nearest - CROWD_SHAPE_SLACK;
        if kept > 0.0 {
            kept_spots.push(NavAvoidance::new(spot.centre, kept));
        }
    }
    kept_spots
}

/// How many steps from a ledge a body keeps when it keeps `clearance` from
/// it.
fn border_steps_for(grid: &NavGrid, clearance: f32) -> i32 {
    ((clearance / grid.cell_size()).floor() as i32).max(1)
}

fn wall_cost(grid: &NavGrid, node: i32, profile: SearchProfile) -> f32 {
    if profile.weight <= 0.0 {
        return 0.0;
    }
    let clearance = grid
        .wall_distance(node)
        .min(grid.border_distance(node) as f32 * grid.cell_size());
    if clearance >= profile.clearance {
        0.0
    } else {
        (profile.clearance - clearance) * profile.weight
    }
}

/// A point no step is taken from, for tests of avoided spots that allow no
/// way out of them.
const NOWHERE: Vec3 = Vec3::splat(f32::INFINITY);

/// Whether a step onto a point enters a spot a route keeps out of. A route
/// that starts in such a spot, as a walk that stopped against an object
/// does, may leave it: a step inside the spot counts only when it comes no
/// farther from the spot's middle than the point it was taken from.
fn is_avoided(point: Vec3, avoided: &[NavAvoidance], from: Vec3) -> bool {
    for avoidance in avoided {
        let here = flat_distance_squared(point, avoidance.centre);
        if here > avoidance.radius * avoidance.radius
            || (point.z - avoidance.centre.z).abs() > AVOIDANCE_HEIGHT
        {
            continue;
        }
        if here > flat_distance_squared(from, avoidance.centre) {
            continue;
        }
        return true;
    }
    false
}

fn flat_distance_squared(a: Vec3, b: Vec3) -> f32 {
    let dx = a.x - b.x;
    let dy = a.y - b.y;
    dx * dx + dy * dy
}

fn passes_avoided(from: Vec3, to: Vec3, avoided: &[NavAvoidance]) -> bool {
    for avoidance in avoided {
        let start = from.truncate();
        let along = to.truncate() - start;
        let centre = avoidance.centre.truncate();
        let length_squared = along.length_squared();
        let t = if length_squared > 1e-12 {
            ((centre - start).dot(along) / length_squared).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let height = from.z + (to.z - from.z) * t;
        let closest = centre.distance(start + along * t);
        if closest > avoidance.radius || (height - avoidance.centre.z).abs() > AVOIDANCE_HEIGHT {
            continue;
        }
        let away_from_start = centre.distance(start);
        if away_from_start <= avoidance.radius && closest >= away_from_start - 1e-3 {
            continue;
        }
        return true;
    }
    false
}

fn remaining(position: Vec3, goal: Vec3, reach: f32) -> f32 {
    let dx = position.x - goal.x;
    let dy = position.y - goal.y;
    ((dx * dx + dy * dy).sqrt() - reach).max(0.0)
}

fn elapsed(clock: Instant) -> f64 {
    clock.elapsed().as_secs_f64() * 1000.0
}

fn failed(outcome: NavRouteOutcome, reason: String, expansions: i32, clock: Instant) -> NavRoute {
    NavRoute {
        outcome,
        reason,
        path: Vec::new(),
        legs: Vec::new(),
        length: 0.0,
        expansions,
        milliseconds: elapsed(clock),
        leaps: Vec::new(),
        ends_in_sight: true,
        scrape: 0.0,
        crowding: 0.0,
    }
}

fn no_start(clock: Instant) -> NavRoute {
    failed(
        NavRouteOutcome::NoStart,
        "no clear spot near the start can be walked to without passing a wall".to_owned(),
        0,
        clock,
    )
}

fn search_gave_up(search: &Search<'_, '_>, clock: Instant) -> NavRoute {
    failed(
        NavRouteOutcome::NoPath,
        format!("the search gave up after {} expansions", search.expansions),
        search.expansions,
        clock,
    )
}

fn away(point: Vec3, goal: Vec3) -> f32 {
    point.truncate().distance(goal.truncate())
}

/// Every node a body reaches from one start, each by its cheapest route:
/// what [`flood`] found.
#[derive(Debug, Clone)]
pub struct NavFlood {
    /// The node the routes start from.
    pub start: i32,
    cost: Vec<f32>,
    by_drop: Vec<bool>,
    /// The nodes expanded.
    pub expansions: i32,
    /// Whether the search stopped at [`MAXIMUM_EXPANSIONS`] before it had
    /// gone as far as it was asked to.
    pub gave_up: bool,
}

impl NavFlood {
    /// Whether the node was reached within the distance asked for.
    #[must_use]
    pub fn reached(&self, node: i32) -> bool {
        self.cost(node).is_finite()
    }

    /// The cost of the cheapest route to the node, or infinity when it was
    /// not reached. The cost is metres along the grid's steps, before the
    /// route is straightened, plus the height climbed or dropped, plus each
    /// leap's penalty (a few metres more for a harder or riskier one), as a
    /// route search counts it.
    #[must_use]
    pub fn cost(&self, node: i32) -> f32 {
        self.cost
            .get(node as usize)
            .copied()
            .unwrap_or(f32::INFINITY)
    }

    /// Whether the cheapest route to the node steps or leaps down farther
    /// than the body steps up, so the character could not walk back up the
    /// way it came (a jump might still take it back).
    #[must_use]
    pub fn by_drop(&self, node: i32) -> bool {
        self.by_drop.get(node as usize).copied().unwrap_or(false)
    }
}

/// Every node a body reaches from `from` along routes costing at most
/// `max_meters` (see [`NavFlood::cost`]), keeping out of the avoided spots
/// and leaping where `leaps` allows, as [`find`] would route to each: one
/// search, with no goal, cheapest first. `None` when no node stands near
/// enough to `from` to start from.
#[must_use]
pub fn flood(
    grid: &NavGrid,
    from: Vec3,
    max_meters: f32,
    avoid: &[NavAvoidance],
    leaps: Option<NavLeapAbility>,
) -> Option<NavFlood> {
    let mut start = grid.find_walkable_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    if start < 0 {
        start = grid.find_standing_node(from, START_RADIUS, START_HEIGHT_TOLERANCE);
    }
    if start < 0 {
        return None;
    }
    let finder = leaps.map(|ability| NavLeapFinder::new(grid, ability));
    let mut search = Search::new(
        grid,
        start,
        from,
        f32::INFINITY,
        avoid,
        &[],
        PROFILES[2],
        finder.as_ref(),
    );
    let mut order = Vec::new();
    let end = search.run(|node, cost| {
        if cost > max_meters {
            return true;
        }
        order.push(node);
        false
    });
    let mut cost = vec![f32::INFINITY; search.cost.len()];
    let mut by_drop = vec![false; search.cost.len()];
    let rise_limit = grid.body().step_up_height + 1e-3;
    for &node in &order {
        let index = node as usize;
        cost[index] = search.cost[index];
        let parent = search.parent[index];
        if parent != node {
            let drop = grid.position(parent).z - grid.position(node).z;
            by_drop[index] = by_drop[parent as usize] || drop > rise_limit;
        }
    }
    Some(NavFlood {
        start,
        cost,
        by_drop,
        expansions: search.expansions,
        gave_up: end == Search::GAVE_UP,
    })
}

/// A best-first expansion of the clear nodes reachable from a start,
/// ordered by the cost so far plus the distance left to a point.
struct Search<'g, 'f> {
    grid: &'g NavGrid,
    toward: Vec3,
    reach: f32,
    avoided: &'g [NavAvoidance],
    crowd: &'g [NavAvoidance],
    profile: SearchProfile,
    leaps: Option<&'f NavLeapFinder<&'f NavGrid>>,
    cost: Vec<f32>,
    open: QuaternaryHeap<i32>,
    parent: Vec<i32>,
    /// The leap each node reached by one was last reached with.
    leap_into: HashMap<i32, NavLeap>,
    /// The nodes expanded so far.
    closed: Vec<bool>,
    expansions: i32,
}

impl<'g, 'f> Search<'g, 'f> {
    /// What [`Self::run`] returns once every reachable node has been
    /// expanded.
    const EXHAUSTED: i32 = -1;
    /// What [`Self::run`] returns when it stops at [`MAXIMUM_EXPANSIONS`].
    const GAVE_UP: i32 = -2;

    #[allow(clippy::too_many_arguments)]
    fn new(
        grid: &'g NavGrid,
        start: i32,
        toward: Vec3,
        reach: f32,
        avoided: &'g [NavAvoidance],
        crowd: &'g [NavAvoidance],
        profile: SearchProfile,
        leaps: Option<&'f NavLeapFinder<&'f NavGrid>>,
    ) -> Self {
        let count = grid.node_count() as usize;
        let mut search = Self {
            grid,
            toward,
            reach,
            avoided,
            crowd,
            profile,
            leaps,
            cost: vec![f32::INFINITY; count],
            open: QuaternaryHeap::default(),
            parent: vec![0; count],
            leap_into: HashMap::new(),
            closed: vec![false; count],
            expansions: 0,
        };
        search.cost[start as usize] = 0.0;
        search.parent[start as usize] = start;
        search
            .open
            .enqueue(start, remaining(grid.position(start), toward, reach));
        search
    }

    /// Expands nodes until one is an end and returns it, or returns
    /// [`Self::EXHAUSTED`] or [`Self::GAVE_UP`]. The end test is given each
    /// node as it is expanded, with the cost of the cheapest route to it.
    fn run(&mut self, mut is_end: impl FnMut(i32, f32) -> bool) -> i32 {
        while let Some((node, _)) = self.open.dequeue() {
            if self.closed[node as usize] {
                continue;
            }
            self.closed[node as usize] = true;
            self.expansions += 1;
            if is_end(node, self.cost[node as usize]) {
                return node;
            }
            if self.expansions >= MAXIMUM_EXPANSIONS {
                return Self::GAVE_UP;
            }

            let here = self.grid.position(node);
            for direction in 0..DIRECTION_COUNT {
                let next = self.grid.link(node, direction);
                if next < 0 || self.closed[next as usize] || !self.grid.is_clear(next) {
                    continue;
                }
                let there = self.grid.position(next);
                if is_avoided(there, self.avoided, here) {
                    continue;
                }
                let step = self.grid.cell_size() * if direction < 4 { 1.0 } else { DIAGONAL_STEP }
                    + (there.z - here.z).abs()
                    + wall_cost(self.grid, next, self.profile)
                    + self.crowd_cost_at(there);
                let total = self.cost[node as usize] + step;
                if total >= self.cost[next as usize] {
                    continue;
                }
                self.cost[next as usize] = total;
                self.parent[next as usize] = node;
                self.leap_into.remove(&next);
                self.open
                    .enqueue(next, total + remaining(there, self.toward, self.reach));
            }
            if self.leaps.is_some() {
                self.relax_leaps(node, here);
            }
        }
        Self::EXHAUSTED
    }

    fn crowd_cost_at(&self, point: Vec3) -> f32 {
        if self.crowd.is_empty() {
            0.0
        } else {
            CROWD_COST * crowd_price(point, self.crowd)
        }
    }

    fn relax_leaps(&mut self, node: i32, here: Vec3) {
        let leaps = self.leaps.expect("checked by the caller").from(node);
        for leap in leaps.iter() {
            let next = leap.to;
            if self.closed[next as usize] || !self.grid.is_standable(next) {
                continue;
            }
            let there = self.grid.position(next);
            if is_avoided(there, self.avoided, here) {
                continue;
            }
            let total = self.cost[node as usize]
                + here.truncate().distance(there.truncate())
                + (there.z - here.z).abs()
                + LEAP_COST
                + leap.power * LEAP_POWER_COST
                + leap.risk * LEAP_RISK_COST
                + self.crowd_cost_at(there);
            if total >= self.cost[next as usize] {
                continue;
            }
            self.cost[next as usize] = total;
            self.parent[next as usize] = node;
            self.leap_into.insert(next, *leap);
            self.open
                .enqueue(next, total + remaining(there, self.toward, self.reach));
        }
    }
}

/// The floor a place stands on: the piece of floor of the nearest node that
/// stands under the point, or -1 with no such node, when any floor within
/// reach of the goal will do.
fn goal_floor(grid: &NavGrid, goal: Vec3) -> (&[i32], i32) {
    let pieces = grid.surfaces().piece_of();
    let centre_x = ((goal.x - grid.origin_x()) / grid.cell_size()).floor() as i32;
    let centre_y = ((goal.y - grid.origin_y()) / grid.cell_size()).floor() as i32;
    let reach = (GOAL_FLOOR_REACH / grid.cell_size()).ceil() as i32;
    let mut piece = -1;
    let mut nearest = f32::INFINITY;
    for y in centre_y - reach..=centre_y + reach {
        for x in centre_x - reach..=centre_x + reach {
            let (first, count) = grid.nodes_in_column(x, y);
            for node in first..first + count {
                if pieces[node as usize] < 0 {
                    continue;
                }
                let at = grid.position(node);
                if (at.z - goal.z).abs() > GOAL_FLOOR_HEIGHT {
                    continue;
                }
                let away_here = at.truncate().distance(goal.truncate());
                if away_here > GOAL_FLOOR_REACH || away_here >= nearest {
                    continue;
                }
                nearest = away_here;
                piece = pieces[node as usize];
            }
        }
    }
    (pieces, piece)
}

/// The top of an object, and the spot on it nearest its middle. Of the
/// nodes a body stands on over the object's own upward-facing surfaces, the
/// top is those on the piece of floor of the highest, over the faces that
/// lie wholly within [`TOP_BAND`] of it, so a ramp or stair up to a deck is
/// not taken for the deck. Where no face does, as on one sloping face, it is
/// the nodes within that band. `None` when no node stands on the object.
#[must_use]
pub fn try_find_top(grid: &NavGrid, surfaces: &NavSurfaces) -> Option<(Vec3, InsertionSet)> {
    let pieces = grid.surfaces().piece_of();
    let mut faces: Vec<Vec<i32>> = Vec::new();
    for triangle in &surfaces.triangles {
        add_standing_on_triangle(grid, pieces, triangle, &mut faces);
    }
    for cylinder in &surfaces.cylinders {
        add_standing_on_cylinder(grid, pieces, cylinder, &mut faces);
    }

    let mut highest = -1;
    for face in &faces {
        for &node in face {
            if highest < 0 || grid.position(node).z > grid.position(highest).z {
                highest = node;
            }
        }
    }
    if highest < 0 {
        return None;
    }

    let piece = pieces[highest as usize];
    let floor = grid.position(highest).z - TOP_BAND;
    let mut top = InsertionSet::new();
    for face in &faces {
        if face
            .iter()
            .all(|&node| pieces[node as usize] != piece || grid.position(node).z >= floor)
        {
            for &node in face {
                if pieces[node as usize] == piece {
                    top.insert(node);
                }
            }
        }
    }
    if top.is_empty() {
        for face in &faces {
            for &node in face {
                if pieces[node as usize] == piece && grid.position(node).z >= floor {
                    top.insert(node);
                }
            }
        }
    }

    let mut middle = Vec2::ZERO;
    for node in top.iter() {
        middle += grid.position(node).truncate();
    }
    middle /= top.len() as f32;
    let mut nearest = f32::INFINITY;
    let mut goal = Vec3::ZERO;
    for node in top.iter() {
        let at = grid.position(node);
        let away_here = at.truncate().distance(middle);
        if away_here < nearest {
            nearest = away_here;
            goal = at;
        }
    }
    Some((goal, top))
}

fn add_standing_on_triangle(
    grid: &NavGrid,
    pieces: &[i32],
    triangle: &NavTriangle,
    faces: &mut Vec<Vec<i32>>,
) {
    let normal = (triangle.b - triangle.a).cross(triangle.c - triangle.a);
    let length = normal.length();
    if length < 1e-6 || normal.z.abs() < TOP_SLOPE_COSINE * length {
        return;
    }
    let minimum_x = triangle.a.x.min(triangle.b.x.min(triangle.c.x));
    let maximum_x = triangle.a.x.max(triangle.b.x.max(triangle.c.x));
    let minimum_y = triangle.a.y.min(triangle.b.y.min(triangle.c.y));
    let maximum_y = triangle.a.y.max(triangle.b.y.max(triangle.c.y));
    let mut face = Vec::new();
    for_columns(
        grid,
        minimum_x,
        minimum_y,
        maximum_x,
        maximum_y,
        |x, y, centre| {
            if let Some(height) = height_over(triangle, centre) {
                add_nodes_at(grid, pieces, x, y, height, &mut face);
            }
        },
    );
    if !face.is_empty() {
        faces.push(face);
    }
}

/// A cylinder's flat top is one face; a dome curves away, so each spot on it
/// is a face of its own.
fn add_standing_on_cylinder(
    grid: &NavGrid,
    pieces: &[i32],
    cylinder: &NavCylinder,
    faces: &mut Vec<Vec<i32>>,
) {
    let axis = cylinder.base.truncate();
    let mut flat = Vec::new();
    for_columns(
        grid,
        axis.x - cylinder.radius,
        axis.y - cylinder.radius,
        axis.x + cylinder.radius,
        axis.y + cylinder.radius,
        |x, y, centre| {
            let away_here = centre.distance(axis);
            if away_here > cylinder.radius {
                return;
            }
            if !cylinder.dome {
                add_nodes_at(
                    grid,
                    pieces,
                    x,
                    y,
                    cylinder.base.z + cylinder.height,
                    &mut flat,
                );
                return;
            }
            let mut spot = Vec::new();
            add_nodes_at(
                grid,
                pieces,
                x,
                y,
                cylinder.base.z
                    + cylinder.radius
                    + (cylinder.radius * cylinder.radius - away_here * away_here).sqrt(),
                &mut spot,
            );
            if !spot.is_empty() {
                faces.push(spot);
            }
        },
    );
    if !flat.is_empty() {
        faces.push(flat);
    }
}

fn for_columns(
    grid: &NavGrid,
    minimum_x: f32,
    minimum_y: f32,
    maximum_x: f32,
    maximum_y: f32,
    mut visit: impl FnMut(i32, i32, Vec2),
) {
    let first_x = (((minimum_x - grid.origin_x()) / grid.cell_size()).floor() as i32).max(0);
    let last_x =
        (((maximum_x - grid.origin_x()) / grid.cell_size()).floor() as i32).min(grid.side() - 1);
    let first_y = (((minimum_y - grid.origin_y()) / grid.cell_size()).floor() as i32).max(0);
    let last_y =
        (((maximum_y - grid.origin_y()) / grid.cell_size()).floor() as i32).min(grid.side() - 1);
    for y in first_y..=last_y {
        for x in first_x..=last_x {
            visit(
                x,
                y,
                Vec2::new(
                    grid.origin_x() + (x as f32 + 0.5) * grid.cell_size(),
                    grid.origin_y() + (y as f32 + 0.5) * grid.cell_size(),
                ),
            );
        }
    }
}

/// The nodes of a column, on a piece of floor, standing within
/// [`TOP_STAND_TOLERANCE`] of a height.
fn add_nodes_at(
    grid: &NavGrid,
    pieces: &[i32],
    x: i32,
    y: i32,
    height: f32,
    standing: &mut Vec<i32>,
) {
    let (first, count) = grid.nodes_in_column(x, y);
    for node in first..first + count {
        if pieces[node as usize] >= 0
            && (grid.position(node).z - height).abs() <= TOP_STAND_TOLERANCE
        {
            standing.push(node);
        }
    }
}

/// The height of a triangle over a flat point inside it, measured straight
/// up.
fn height_over(triangle: &NavTriangle, point: Vec2) -> Option<f32> {
    let a = triangle.a.truncate();
    let b = triangle.b.truncate();
    let c = triangle.c.truncate();
    let area = cross(b - a, c - a);
    if area.abs() < 1e-8 {
        return None;
    }
    let u = cross(b - point, c - point) / area;
    let v = cross(c - point, a - point) / area;
    let w = 1.0 - u - v;
    const EDGE: f32 = -1e-4;
    if u < EDGE || v < EDGE || w < EDGE {
        return None;
    }
    Some(u * triangle.a.z + v * triangle.b.z + w * triangle.c.z)
}

fn cross(left: Vec2, right: Vec2) -> f32 {
    left.x * right.y - left.y * right.x
}

/// Which nodes count as arriving at a goal: near enough to it, and able to
/// see it along a line that crosses no wall and no avoided spot.
struct GoalTest<'a> {
    grid: &'a NavGrid,
    goal: Vec3,
    reach: f32,
    avoided: &'a [NavAvoidance],
    sight: HashMap<i32, bool>,
    /// Which nodes a route may end on, such as those on the goal's own
    /// floor.
    accepts: Accepts<'a>,
    /// How far the goal object's side stands from its middle, which is what
    /// a body sees of it; zero for a point.
    radius: f32,
}

impl<'a> GoalTest<'a> {
    /// How many nodes near the goal are tried for a line of sight before
    /// planning goes ahead regardless.
    const MAXIMUM_PROBES: i32 = 2048;

    fn new(
        grid: &'a NavGrid,
        goal: Vec3,
        reach: f32,
        avoided: &'a [NavAvoidance],
        accepts: Accepts<'a>,
        radius: f32,
    ) -> Self {
        Self {
            grid,
            goal,
            reach,
            avoided,
            sight: HashMap::new(),
            accepts,
            radius,
        }
    }

    fn is_reached_at(&mut self, node: i32) -> bool {
        let at = self.grid.position(node);
        let dx = at.x - self.goal.x;
        let dy = at.y - self.goal.y;
        if dx * dx + dy * dy > self.reach * self.reach
            || (at.z - self.goal.z).abs() > GOAL_HEIGHT_TOLERANCE
        {
            return false;
        }
        if !self.accepts.accepts(node) {
            return false;
        }
        self.sees(node)
    }

    /// The reached node within [`FALLBACK_REACH`] of the goal nearest to it
    /// that can see it, or -1.
    fn nearest_seeing_among(&mut self, reached: &[bool]) -> i32 {
        for (node, _) in self.reached_near(reached) {
            if self.sees(node) {
                return node;
            }
        }
        -1
    }

    /// The reached node within [`FALLBACK_REACH`] of the goal nearest to it,
    /// or -1.
    fn nearest_among(&self, reached: &[bool]) -> i32 {
        self.reached_near(reached)
            .first()
            .map_or(-1, |&(node, _)| node)
    }

    fn reached_near(&self, reached: &[bool]) -> Vec<(i32, f32)> {
        let grid = self.grid;
        let centre_x = ((self.goal.x - grid.origin_x()) / grid.cell_size()).floor() as i32;
        let centre_y = ((self.goal.y - grid.origin_y()) / grid.cell_size()).floor() as i32;
        let reach = (FALLBACK_REACH / grid.cell_size()).ceil() as i32;
        let mut candidates = Vec::new();
        for y in centre_y - reach..=centre_y + reach {
            for x in centre_x - reach..=centre_x + reach {
                let (first, count) = grid.nodes_in_column(x, y);
                for node in first..first + count {
                    if !reached[node as usize] || !grid.is_standable(node) {
                        continue;
                    }
                    let at = grid.position(node);
                    if (at.z - self.goal.z).abs() > GOAL_HEIGHT_TOLERANCE {
                        continue;
                    }
                    let distance = at.truncate().distance(self.goal.truncate());
                    if distance <= FALLBACK_REACH {
                        candidates.push((node, distance));
                    }
                }
            }
        }
        intro_sort(&mut candidates, |l, r| compare_f32(l.1, r.1));
        candidates
    }

    fn sees(&mut self, node: i32) -> bool {
        if let Some(&sees) = self.sight.get(&node) {
            return sees;
        }
        let at = self.grid.position(node);
        let seen = self.side_facing(at);
        let sees = self.grid.can_see(node, seen) && !passes_avoided(at, seen, self.avoided);
        self.sight.insert(node, sees);
        sees
    }

    /// The point of the goal a body at `at` looks at: its middle for a
    /// point, and for an object the side of it facing the body, since the
    /// object's own collision hides its middle. The side is taken a cell and
    /// a half out from the object's radius, since the grid holds walls in
    /// whole columns and a line to the very edge of a round object's
    /// collision would graze the column it fills.
    fn side_facing(&self, at: Vec3) -> Vec3 {
        let toward = Vec2::new(at.x - self.goal.x, at.y - self.goal.y);
        let away_here = toward.length();
        if self.radius <= 0.0 || away_here < 1e-3 {
            return self.goal;
        }
        let side = (self.radius + self.grid.cell_size() * 1.5).min(away_here);
        self.goal + (toward * (side / away_here)).extend(0.0)
    }

    /// False only when every clear node near enough to the goal was tried
    /// and none can see it.
    fn may_succeed(&mut self) -> bool {
        let grid = self.grid;
        let centre_x = ((self.goal.x - grid.origin_x()) / grid.cell_size()).floor() as i32;
        let centre_y = ((self.goal.y - grid.origin_y()) / grid.cell_size()).floor() as i32;
        let reach = (self.reach / grid.cell_size()).ceil() as i32;
        let mut probes = 0;
        for y in centre_y - reach..=centre_y + reach {
            for x in centre_x - reach..=centre_x + reach {
                let (first, count) = grid.nodes_in_column(x, y);
                for node in first..first + count {
                    if !grid.is_standable(node) {
                        continue;
                    }
                    probes += 1;
                    if probes > Self::MAXIMUM_PROBES {
                        return true;
                    }
                    if self.is_reached_at(node) {
                        return true;
                    }
                }
            }
        }
        false
    }
}
