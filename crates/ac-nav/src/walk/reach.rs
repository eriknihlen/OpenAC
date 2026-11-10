//! Reach searches: inside a sealed dungeon, every cell the character can
//! walk to from where it stands, each with the length of its cheapest
//! route, from one search over the grid walks and previews use. Like a
//! preview it moves nothing and touches no walk; what the world holds is
//! read on the calling thread, the search runs on a worker.

use std::time::Instant;

use ac_physics::globals::FLOOR_Z;
use ac_physics::{CellSurface, point_in_triangle_xy};
use glam::Vec3;

use super::preview::PreviewGround;
use super::{
    Job, MAXIMUM_DUNGEON_REGION, NavAvoidance, NavGrid, PlanStatus, WalkController, WalkWorld,
    flat, route, try_choose_square,
};

/// How far a node may stand off a cell's floor and still count as on it,
/// metres: a grid node stands at its column's middle, a little off a
/// sloping face's height there.
pub const ON_THE_FLOOR: f32 = 0.35;

/// One cell a reach search reached.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CellReach {
    /// The cell.
    pub cell_id: u32,
    /// The cost of the cheapest route from the character to the cell (see
    /// [`route::NavFlood::cost`]): metres along the grid's steps before the
    /// route is straightened, by the shortest way rather than the one a walk
    /// takes to keep off walls. In installed dungeons it comes within about
    /// 15% of a preview's length to the same place, usually a little under.
    pub route_meters: f32,
    /// The point on the cell's floor that cost is measured to, metres from
    /// the corner of the first landblock: a place to walk to.
    pub place: Vec3,
    /// The cheapest route steps or leaps down farther than the character
    /// steps up, so it could not walk back up the way it came.
    pub by_drop: bool,
}

/// What a reach search found.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavReach {
    /// How it ended: `Routed` with the cells it reached, else why not.
    pub status: PlanStatus,
    /// Every cell reached, cheapest first; a cell not reached is left out.
    pub cells: Vec<CellReach>,
    /// Why, in words.
    pub reason: String,
    /// Whether the search ran over a grid already built or under way.
    pub grid_reused: bool,
    /// How long the worker took, milliseconds.
    pub took_ms: f32,
}

impl NavReach {
    /// A search with no cells.
    #[must_use]
    pub fn failed(status: PlanStatus, reason: &str) -> Self {
        Self {
            status,
            reason: reason.to_owned(),
            ..Self::default()
        }
    }
}

/// A reach search under way.
pub struct ReachSearch(Job<NavReach>);

impl std::fmt::Debug for ReachSearch {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ReachSearch")
            .field("finished", &self.is_finished())
            .finish()
    }
}

impl ReachSearch {
    /// An answer known at once.
    #[must_use]
    pub fn ready(reach: NavReach) -> Self {
        Self(Job::Ready(reach))
    }

    /// The search has ended.
    #[must_use]
    pub fn is_finished(&self) -> bool {
        self.0.is_finished()
    }

    /// What it found (waiting for the search when it is still running); a
    /// search that broke down is `Failed`.
    #[must_use]
    pub fn take(self) -> NavReach {
        self.0.take().unwrap_or_else(|error| {
            NavReach::failed(PlanStatus::Failed, &format!("the search failed: {error}"))
        })
    }
}

impl WalkController {
    /// Every cell of the sealed dungeon the character stands in that it can
    /// walk to along routes of at most `max_meters`, from one search over
    /// the grid walks and previews use (built and kept when there is none).
    /// The character's own cell is among them. Portals and the objects the
    /// server placed are kept out of, as a walk keeps out of them; doors are
    /// not in the grid, so a shut or locked door stops nothing. A cell with
    /// no floor of its own (the upper half of a tall room) is never reached:
    /// the cell under it is. `None` when there is nothing to search from:
    /// the character is not standing in the world (in portal space or in
    /// the air) or is not in a sealed dungeon.
    pub fn reachable_cells(&mut self, cx: &WalkWorld<'_>, max_meters: f32) -> Option<ReachSearch> {
        let standing = cx
            .body
            .try_sample()
            .filter(|s| !s.in_portal_space && !s.airborne)
            .and_then(|s| cx.goals.try_global_of(s.position).map(|g| (s, g)));
        let (sample, global_start) = standing?;
        let (minimum, maximum) = self.try_measure_dungeon(cx, sample.cell_id)?;
        let at = flat(sample.position);
        let Some((origin_x, origin_y, size)) =
            try_choose_square(minimum.min(at), maximum.max(at), MAXIMUM_DUNGEON_REGION)
        else {
            return Some(ReachSearch::ready(NavReach::failed(
                PlanStatus::NoRoute,
                "the dungeon is too large for one planning region",
            )));
        };
        let dungeon = (sample.cell_id & 0xFFFF_0000) | 0xFFFF;
        let Some(ground) = self.preview_ground(
            cx,
            &sample,
            dungeon,
            sample.position,
            (origin_x, origin_y, size),
        ) else {
            return Some(ReachSearch::ready(NavReach::failed(
                PlanStatus::NoRoute,
                "no collision is loaded around the character",
            )));
        };
        let (centre, reach) = ground.around();
        let PreviewGround {
            source,
            reused,
            landblock_ids,
            object_ids,
            ..
        } = ground;
        let cells = self.cells_of(cx, &landblock_ids, source.is_kept());
        let body = sample.body;
        let widen = |spot: NavAvoidance| NavAvoidance::new(spot.centre, spot.radius + body.radius);
        let mut avoid: Vec<NavAvoidance> = cx
            .goals
            .find_portals(centre, reach, 0, sample.position)
            .into_iter()
            .map(widen)
            .collect();
        avoid.extend(
            cx.goals
                .find_obstacles(centre, reach, 0, Some(&object_ids))
                .into_iter()
                .map(widen),
        );
        let start = sample.position;
        let leaps = sample.leaps;
        Some(ReachSearch(Job::start(self.mode, move || {
            let begun = Instant::now();
            let took = || begun.elapsed().as_secs_f32() * 1000.0;
            let finish = |mut reach: NavReach| {
                reach.grid_reused = reused;
                reach.took_ms = took();
                reach
            };
            let grid = match source.grid() {
                Ok(grid) => grid,
                Err(error) => {
                    return finish(NavReach::failed(
                        PlanStatus::Failed,
                        &format!("the search failed: {error}"),
                    ));
                }
            };
            let Some(flood) = route::flood(&grid, start, max_meters, &avoid, leaps) else {
                return finish(NavReach::failed(
                    PlanStatus::NoRoute,
                    "the character stands nowhere on the grid",
                ));
            };
            let mut reached = cells_reached(&grid, &flood, &cells);
            for cell in &mut reached {
                cell.place = global_start + cell.place - start;
            }
            let reason = if flood.gave_up {
                format!(
                    "{} cells reached before the search stopped at its limit",
                    reached.len()
                )
            } else {
                format!("{} cells reached", reached.len())
            };
            finish(NavReach {
                status: PlanStatus::Routed,
                cells: reached,
                reason,
                ..NavReach::default()
            })
        })))
    }
}

/// Each cell whose floor holds a node the flood reached, at its cheapest
/// such node (in the grid's frame), cheapest first. A node counts as on a
/// cell's floor when it stands within [`ON_THE_FLOOR`] of the height of one
/// of the cell's walkable faces at its column's middle.
#[must_use]
pub fn cells_reached(
    grid: &NavGrid,
    flood: &route::NavFlood,
    cells: &[CellSurface],
) -> Vec<CellReach> {
    let size = grid.cell_size();
    let columns = grid.size() / size;
    let mut reached = Vec::new();
    for cell in cells {
        // The cheapest node on the cell's floor, and that floor's height
        // at its column.
        let mut best: Option<(f32, i32, f32)> = None;
        for [a, b, c] in cell.triangles() {
            let normal = (*b - *a).cross(*c - *a);
            let length = normal.length();
            if length <= 1e-6 || normal.z / length < FLOOR_Z {
                continue;
            }
            let low = a.min(b.min(*c));
            let high = a.max(b.max(*c));
            let first = |v: f32, origin: f32| ((v - origin) / size).floor().max(0.0) as i32;
            let last =
                |v: f32, origin: f32| (((v - origin) / size).floor()).min(columns - 1.0) as i32;
            for y in first(low.y, grid.origin_y())..=last(high.y, grid.origin_y()) {
                for x in first(low.x, grid.origin_x())..=last(high.x, grid.origin_x()) {
                    let px = grid.origin_x() + (x as f32 + 0.5) * size;
                    let py = grid.origin_y() + (y as f32 + 0.5) * size;
                    let Some(floor) = point_in_triangle_xy(px, py, *a, *b, *c) else {
                        continue;
                    };
                    let (node, count) = grid.nodes_in_column(x, y);
                    for node in node..node + count {
                        let cost = flood.cost(node);
                        if !cost.is_finite()
                            || (grid.position(node).z - floor).abs() > ON_THE_FLOOR
                            || best.is_some_and(|(b, _, _)| b <= cost)
                        {
                            continue;
                        }
                        best = Some((cost, node, floor));
                    }
                }
            }
        }
        if let Some((cost, node, floor)) = best {
            reached.push(CellReach {
                cell_id: cell.cell_id(),
                route_meters: cost,
                place: grid.position(node).with_z(floor),
                by_drop: flood.by_drop(node),
            });
        }
    }
    reached.sort_by(|a, b| {
        a.route_meters
            .total_cmp(&b.route_meters)
            .then(a.cell_id.cmp(&b.cell_id))
    });
    reached
}
