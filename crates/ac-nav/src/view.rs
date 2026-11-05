//! What a window draws of navigation, as coloured line segments in the
//! walk's frame.
//!
//! The route of a walk under way, or of a route asked for alone, is a
//! magenta line from the character along the legs still to walk, a leap
//! drawn as an arc, with a dimmer line on to a goal beyond a stage. While
//! the grid is shown it adds the grid around the character (every tight
//! point in red, every second clear point in green as a small cross), the
//! path the route was straightened from, a post at the end of every leg and
//! a violet cylinder around each portal routes keep out of. The goal is a
//! flat magenta ring as wide as its arrival distance.

use glam::Vec3;

use crate::grid::NavGrid;
use crate::route::{NavAvoidance, NavRoute, NavRouteOutcome};
use crate::walk::{WalkController, WalkState};

/// How far across from the character grid points are drawn, metres.
pub const DRAW_RADIUS: f32 = 20.0;
/// Clear points are drawn on every second column and row; tight points
/// (beside a ledge or near a wall) are all drawn.
pub const DRAW_STRIDE: i32 = 2;
/// Half the arm of a grid point's cross, metres.
pub const MARKER_HALF_SIZE: f32 = 0.08;
/// A portal cylinder's height, metres.
pub const PORTAL_HEIGHT: f32 = 2.0;
/// The goal ring's height, metres.
pub const GOAL_HEIGHT: f32 = 0.1;
/// The segments of a leap's arc.
pub const LEAP_ARC_SEGMENTS: i32 = 8;
/// A leap arc's rise at its middle, metres.
pub const LEAP_ARC_HEIGHT: f32 = 1.0;
/// The segments of a cylinder's rings.
pub const CYLINDER_SEGMENTS: usize = 16;
/// Grid points sit this far above their floor, metres.
pub const NODE_LIFT: f32 = 0.04;
/// The path the route was straightened from, above the floor, metres.
pub const PATH_LIFT: f32 = 0.15;
/// The route, above the floor, metres.
pub const ROUTE_LIFT: f32 = 0.1;
/// A leg end's post, metres.
pub const LEG_POST: f32 = 1.0;

/// What a segment shows, which picks its colour.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum NavLineKind {
    /// A clear grid point.
    Clear,
    /// A tight grid point.
    Tight,
    /// The path a route was straightened from.
    Path,
    /// The route, its leg posts and its goal.
    Route,
    /// The line on from a stage's end to the goal beyond it.
    Beyond,
    /// A portal routes keep out of.
    Portal,
}

impl NavLineKind {
    /// The colour, red, green and blue from 0 to 1.
    #[must_use]
    pub const fn rgb(self) -> [f32; 3] {
        match self {
            Self::Clear => [0.2, 0.95, 0.35],
            Self::Tight => [0.9, 0.25, 0.15],
            Self::Path => [0.85, 0.85, 0.85],
            Self::Route => [1.0, 0.0, 1.0],
            Self::Beyond => [0.5, 0.0, 0.5],
            // A warm violet, apart from every other mark.
            Self::Portal => [0.8, 0.3, 1.0],
        }
    }
}

/// One segment, in the walk's frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavLine {
    /// One end.
    pub from: Vec3,
    /// The other end.
    pub to: Vec3,
    /// What it shows.
    pub kind: NavLineKind,
}

/// What the drawing reads of a walk.
#[derive(Debug, Clone, Copy)]
pub struct NavDrawState<'a> {
    /// The grid most recently built.
    pub grid: Option<&'a NavGrid>,
    /// The route most recently searched for.
    pub route: Option<&'a NavRoute>,
    /// The goal and how near counts as arriving.
    pub goal: Option<(Vec3, f32)>,
    /// The leg end the character walks toward, while it walks.
    pub leg_index: Option<i32>,
    /// The route is one stage of a walk beyond it.
    pub staged: bool,
}

impl<'a> NavDrawState<'a> {
    /// What `walk` shows now.
    #[must_use]
    pub fn of(walk: &'a WalkController) -> Self {
        Self {
            grid: walk.grid().map(AsRef::as_ref),
            route: walk.route(),
            goal: walk.goal(),
            leg_index: walk.leg_index(),
            staged: walk.is_staged(),
        }
    }
}

/// Whether there is a route to draw while the grid is hidden: a request
/// under way, or a route found alone.
#[must_use]
pub fn has_route_to_show(walk: &WalkController) -> bool {
    walk.is_busy() || walk.report().state == WalkState::Planned
}

/// Every segment to draw: the grid around `character` and `portals` while
/// `show_grid`, then the goal and the route.
#[must_use]
pub fn lines(
    state: &NavDrawState<'_>,
    character: Option<Vec3>,
    portals: &[NavAvoidance],
    show_grid: bool,
) -> Vec<NavLine> {
    let mut out = Vec::new();
    if show_grid && let (Some(grid), Some(at)) = (state.grid, character) {
        let along_x = Vec3::new(MARKER_HALF_SIZE, 0.0, 0.0);
        let along_y = Vec3::new(0.0, MARKER_HALF_SIZE, 0.0);
        for (at, clear) in grid_marks(grid, at) {
            let kind = if clear {
                NavLineKind::Clear
            } else {
                NavLineKind::Tight
            };
            out.push(line(at - along_x, at + along_x, kind));
            out.push(line(at - along_y, at + along_y, kind));
        }
    }
    if show_grid && character.is_some() {
        for portal in portals {
            cylinder(
                &mut out,
                portal.centre,
                portal.radius,
                PORTAL_HEIGHT,
                NavLineKind::Portal,
            );
        }
    }
    route_lines(&mut out, state, character, show_grid);
    out
}

/// Where the grid's points around `centre` are drawn, and whether each is
/// clear: every tight point within [`DRAW_RADIUS`] across, and the clear
/// ones on every [`DRAW_STRIDE`]th column and row; floors above and below
/// alike.
#[must_use]
pub fn grid_marks(grid: &NavGrid, centre: Vec3) -> Vec<(Vec3, bool)> {
    let mut marks = Vec::new();
    let cell = grid.cell_size();
    let reach = (DRAW_RADIUS / cell) as i32;
    let centre_x = ((centre.x - grid.origin_x()) / cell).floor() as i32;
    let centre_y = ((centre.y - grid.origin_y()) / cell).floor() as i32;
    let start_x = (centre_x - reach).max(0);
    let start_y = (centre_y - reach).max(0);
    let end_x = (centre_x + reach).min(grid.side() - 1);
    let end_y = (centre_y + reach).min(grid.side() - 1);
    for y in start_y..=end_y {
        for x in start_x..=end_x {
            let on_stride = x % DRAW_STRIDE == 0 && y % DRAW_STRIDE == 0;
            let (first, count) = grid.nodes_in_column(x, y);
            for node in first..first + count {
                let clear = grid.is_clear(node);
                if clear && !on_stride {
                    continue;
                }
                let position = grid.position(node);
                let dx = position.x - centre.x;
                let dy = position.y - centre.y;
                if dx * dx + dy * dy > DRAW_RADIUS * DRAW_RADIUS {
                    continue;
                }
                marks.push((position + Vec3::new(0.0, 0.0, NODE_LIFT), clear));
            }
        }
    }
    marks
}

fn line(from: Vec3, to: Vec3, kind: NavLineKind) -> NavLine {
    NavLine { from, to, kind }
}

/// A cylinder standing on `base`: its two rings and four uprights.
fn cylinder(out: &mut Vec<NavLine>, base: Vec3, radius: f32, height: f32, kind: NavLineKind) {
    let top = base + Vec3::new(0.0, 0.0, height);
    let ring = |centre: Vec3, i: usize| {
        let theta = i as f32 * (std::f32::consts::PI * 2.0 / CYLINDER_SEGMENTS as f32);
        centre + Vec3::new(theta.cos() * radius, theta.sin() * radius, 0.0)
    };
    for centre in [base, top] {
        for i in 0..CYLINDER_SEGMENTS {
            out.push(line(
                ring(centre, i),
                ring(centre, (i + 1) % CYLINDER_SEGMENTS),
                kind,
            ));
        }
    }
    for i in 0..4 {
        let index = i * (CYLINDER_SEGMENTS / 4);
        out.push(line(ring(base, index), ring(top, index), kind));
    }
}

fn route_lines(
    out: &mut Vec<NavLine>,
    state: &NavDrawState<'_>,
    character: Option<Vec3>,
    show_grid: bool,
) {
    let lift = |p: Vec3, by: f32| p + Vec3::new(0.0, 0.0, by);
    if let Some((goal, arrival)) = state.goal {
        cylinder(out, goal, arrival, GOAL_HEIGHT, NavLineKind::Route);
    }
    let Some(route) = state
        .route
        .filter(|r| r.outcome == NavRouteOutcome::Routed && !r.legs.is_empty())
    else {
        return;
    };
    if show_grid {
        for pair in route.path.windows(2) {
            out.push(line(
                lift(pair[0], PATH_LIFT),
                lift(pair[1], PATH_LIFT),
                NavLineKind::Path,
            ));
        }
        for &end in &route.legs {
            out.push(line(end, lift(end, LEG_POST), NavLineKind::Route));
        }
    }
    let mut next = 1;
    if let (Some(walking), Some(at)) = (state.leg_index, character)
        && let Ok(walking) = usize::try_from(walking)
        && walking < route.legs.len()
    {
        out.push(line(
            lift(at, ROUTE_LIFT),
            lift(route.legs[walking], ROUTE_LIFT),
            NavLineKind::Route,
        ));
        next = walking + 1;
    }
    for index in next.max(1)..route.legs.len() {
        leg(out, route, index);
    }
    if state.staged
        && let (Some((beyond, _)), Some(&last)) = (state.goal, route.legs.last())
    {
        out.push(line(
            lift(last, ROUTE_LIFT),
            lift(beyond, ROUTE_LIFT),
            NavLineKind::Beyond,
        ));
    }
}

/// One leg of a route: a straight line, or an arc for a leap.
fn leg(out: &mut Vec<NavLine>, route: &NavRoute, index: usize) {
    let lift = Vec3::new(0.0, 0.0, ROUTE_LIFT);
    let from = route.legs[index - 1] + lift;
    let to = route.legs[index] + lift;
    if !route
        .leaps
        .iter()
        .any(|leap| usize::try_from(leap.leg_index).ok() == Some(index))
    {
        out.push(line(from, to, NavLineKind::Route));
        return;
    }
    let mut previous = from;
    for segment in 1..=LEAP_ARC_SEGMENTS {
        let along = segment as f32 / LEAP_ARC_SEGMENTS as f32;
        let point = from.lerp(to, along)
            + Vec3::new(0.0, 0.0, LEAP_ARC_HEIGHT * 4.0 * along * (1.0 - along));
        out.push(line(previous, point, NavLineKind::Route));
        previous = point;
    }
}
