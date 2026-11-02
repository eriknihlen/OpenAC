//! Plan-only path previews (OpenAC `NavigationWalkController.Preview`): a
//! route to an object or a place searched over the collision loaded now,
//! without moving the character, owning navigation or touching a walk
//! under way. The grid a walk keeps is searched when it still stands for
//! the world around the character and the goal; inside a sealed dungeon a
//! grid built for a preview is kept the same way, for the walks and
//! previews after it. What the world holds is read on the calling thread;
//! the grid build and the route search run like a walk's (on a worker, or
//! at once inline). The answer is a detached list of route points from the
//! character toward the goal, each with its cell, measured from the corner
//! of the first landblock.

use std::collections::HashSet;
use std::sync::Arc;
use std::time::Instant;

use ac_physics::{CellSurface, TerrainSurface};
use glam::Vec3;

use super::{
    COVER_MARGIN, DEFAULT_CELL_SIZE, Job, LANDBLOCK_SIZE, MAXIMUM_DUNGEON_REGION, NavAvoidance,
    NavGeometry, NavGrid, NavRouteOutcome, NavRouteRequest, SharedBuild, WalkBodySample,
    WalkController, WalkWorld, around_obstacles, flat, planning_radius, radius_of, route,
    try_choose_region, try_choose_square,
};

/// The grid a search runs over: one already built, one being built that it
/// waits for, or one it builds for itself from geometry captured now.
pub(super) enum GridSource {
    Built(Arc<NavGrid>),
    Building(SharedBuild),
    Capture(Box<NavGeometry>, super::NavBody),
}

impl GridSource {
    /// The grid, built on the calling thread when it is the search's own.
    pub(super) fn grid(self) -> Result<Arc<NavGrid>, String> {
        match self {
            Self::Built(grid) => Ok(grid),
            Self::Building(build) => build.wait(),
            Self::Capture(geometry, body) => NavGrid::build(&geometry, body, DEFAULT_CELL_SIZE)
                .map(Arc::new)
                .map_err(|e| format!("{e:?}")),
        }
    }
}

/// What a preview plans toward.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum PreviewTarget {
    /// An object, ending beside it.
    Object(u32),
    /// A place: a point in the landblock-local frame of `cell_id` (for a
    /// cell of zero, measured from the corner of the first landblock),
    /// ending on the floor it stands on.
    Place {
        /// The cell.
        cell_id: u32,
        /// The point.
        local: Vec3,
    },
}

/// The ground a preview searches, and what it was built over.
pub(super) struct PreviewGround {
    pub(super) source: GridSource,
    /// Whether the grid was there or under way before the preview asked.
    pub(super) reused: bool,
    pub(super) landblock_ids: Vec<u32>,
    pub(super) object_ids: Arc<HashSet<u32>>,
}

/// How a preview ended (OpenAC `PluginNavigationPlanStatus`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlanStatus {
    /// A path to the destination was found.
    Routed,
    /// No path to the destination could be planned.
    #[default]
    NoRoute,
    /// The character or the navigation world is unavailable.
    Unavailable,
    /// The destination or the arrival distance is invalid.
    InvalidTarget,
    /// The grid build or the route search failed.
    Failed,
}

/// One point of a previewed path.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlanPoint {
    /// The cell it stands in.
    pub cell_id: u32,
    /// Where, metres from the corner of the first landblock.
    pub global: Vec3,
}

/// A previewed path (OpenAC `PluginNavigationPlan`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavPlan {
    /// How it ended.
    pub status: PlanStatus,
    /// The route's points from the character toward the goal; empty
    /// unless routed.
    pub path: Vec<PlanPoint>,
    /// Why, in words.
    pub reason: String,
    /// The route's length, metres.
    pub length_meters: f32,
    /// Whether the search ran over a grid already built or under way,
    /// rather than one built for this preview.
    pub grid_reused: bool,
    /// How long the worker took, milliseconds: waiting for a grid under
    /// way, building one, and searching it.
    pub took_ms: f32,
}

impl NavPlan {
    /// A plan with no path.
    #[must_use]
    pub fn failed(status: PlanStatus, reason: &str) -> Self {
        Self {
            status,
            path: Vec::new(),
            reason: reason.to_owned(),
            length_meters: 0.0,
            grid_reused: false,
            took_ms: 0.0,
        }
    }
}

/// A preview under way.
pub struct PathPreview(Job<NavPlan>);

impl std::fmt::Debug for PathPreview {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PathPreview")
            .field("finished", &self.is_finished())
            .finish()
    }
}

impl PathPreview {
    /// An answer known at once.
    #[must_use]
    pub fn ready(plan: NavPlan) -> Self {
        Self(Job::Ready(plan))
    }

    /// The search has ended.
    #[must_use]
    pub fn is_finished(&self) -> bool {
        self.0.is_finished()
    }

    /// The plan (waiting for the search when it is still running); a
    /// search that broke down is `Failed`.
    #[must_use]
    pub fn take(self) -> NavPlan {
        self.0.take().unwrap_or_else(|error| {
            NavPlan::failed(PlanStatus::Failed, &format!("the search failed: {error}"))
        })
    }
}

impl WalkController {
    /// Plans a path to `target` without walking it, ending within
    /// `arrival_meters` (OpenAC `NavigationWalkController.PreviewPathAsync`).
    pub fn preview_path(
        &mut self,
        cx: &WalkWorld<'_>,
        target: PreviewTarget,
        arrival_meters: f32,
    ) -> PathPreview {
        match target {
            PreviewTarget::Object(object_id) => {
                let Some(goal) = cx.goals.try_locate(object_id) else {
                    return PathPreview::ready(NavPlan::failed(
                        PlanStatus::NoRoute,
                        "the client has no position for the object",
                    ));
                };
                let radius = cx
                    .goals
                    .try_get_surfaces(object_id)
                    .map_or(0.0, |s| radius_of(&s, goal));
                self.preview_to(cx, goal, object_id, arrival_meters, false, radius)
            }
            PreviewTarget::Place { cell_id, local } => {
                let Some(goal) = cx.goals.try_locate_place(cell_id, local) else {
                    return PathPreview::ready(NavPlan::failed(
                        PlanStatus::NoRoute,
                        "the client cannot place the destination",
                    ));
                };
                self.preview_to(cx, goal, 0, arrival_meters, true, 0.0)
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn preview_to(
        &mut self,
        cx: &WalkWorld<'_>,
        goal: Vec3,
        object_id: u32,
        arrival_meters: f32,
        on_goal_floor: bool,
        goal_radius: f32,
    ) -> PathPreview {
        let standing = cx
            .body
            .try_sample()
            .filter(|s| !s.in_portal_space && !s.airborne)
            .and_then(|s| cx.goals.try_global_of(s.position).map(|g| (s, g)));
        let Some((sample, global_start)) = standing else {
            return PathPreview::ready(NavPlan::failed(
                PlanStatus::Unavailable,
                "the character is not standing in the world",
            ));
        };
        let dungeon_cells = self.try_measure_dungeon(cx, sample.cell_id);
        let region = match dungeon_cells {
            Some((minimum, maximum)) => {
                let near = flat(sample.position).min(flat(goal));
                let far = flat(sample.position).max(flat(goal));
                try_choose_square(minimum.min(near), maximum.max(far), MAXIMUM_DUNGEON_REGION)
            }
            None => try_choose_region(sample.position, goal),
        };
        let Some((origin_x, origin_y, size)) = region else {
            return PathPreview::ready(NavPlan::failed(
                PlanStatus::NoRoute,
                "the destination is outside one planning region",
            ));
        };
        let sealed = dungeon_cells.is_some();
        let grid_dungeon = if sealed {
            (sample.cell_id & 0xFFFF_0000) | 0xFFFF
        } else {
            0
        };
        let Some(ground) =
            self.preview_ground(cx, &sample, grid_dungeon, goal, (origin_x, origin_y, size))
        else {
            return PathPreview::ready(NavPlan::failed(
                PlanStatus::NoRoute,
                "no collision is loaded around the character",
            ));
        };
        let PreviewGround {
            source,
            reused,
            landblock_ids,
            object_ids,
        } = ground;
        let cells = self.cells_of(cx, &landblock_ids);
        let crowd = self.crowd(cx, &sample, object_id);
        let body = sample.body;
        let start = sample.position;
        let leaps = sample.leaps;
        let half = size * 0.5;
        let centre = Vec3::new(origin_x + half, origin_y + half, 0.0);
        let reach = half * std::f32::consts::SQRT_2;
        let widen = |spot: NavAvoidance| NavAvoidance::new(spot.centre, spot.radius + body.radius);
        let portals: Vec<NavAvoidance> = cx
            .goals
            .find_portals(centre, reach, object_id, goal)
            .into_iter()
            .map(widen)
            .collect();
        let obstacles: Vec<NavAvoidance> = cx
            .goals
            .find_obstacles(centre, reach, object_id, Some(&object_ids))
            .into_iter()
            .map(widen)
            .collect();
        let start_cell = sample.cell_id;
        PathPreview(Job::start(self.mode, move || {
            let begun = Instant::now();
            let took = || begun.elapsed().as_secs_f32() * 1000.0;
            let grid = match source.grid() {
                Ok(grid) => grid,
                Err(error) => {
                    return NavPlan {
                        grid_reused: reused,
                        took_ms: took(),
                        ..NavPlan::failed(
                            PlanStatus::Failed,
                            &format!("the search failed: {error}"),
                        )
                    };
                }
            };
            let found = around_obstacles(goal, &portals, &obstacles, |spots| {
                route::find(
                    &grid,
                    start,
                    goal,
                    planning_radius(arrival_meters),
                    &NavRouteRequest {
                        avoid: spots,
                        leaps,
                        crowd: &crowd,
                        arrive_on_goal_floor: on_goal_floor,
                        goal_radius,
                    },
                )
            });
            if found.outcome != NavRouteOutcome::Routed || !found.ends_in_sight {
                return NavPlan {
                    grid_reused: reused,
                    took_ms: took(),
                    ..NavPlan::failed(PlanStatus::NoRoute, &found.reason)
                };
            }
            NavPlan {
                status: PlanStatus::Routed,
                path: found
                    .legs
                    .iter()
                    .map(|&point| {
                        plan_point(point, start, global_start, start_cell, &cells, sealed)
                    })
                    .collect(),
                reason: found.reason,
                length_meters: found.length,
                grid_reused: reused,
                took_ms: took(),
            }
        }))
    }

    /// The grid a preview from `sample` to `goal` searches: the one kept
    /// when it still stands for the world there (the same body, dungeon and
    /// resident landblocks, both ends inside it, and the objects a body
    /// stands on unchanged), else the one being built when it will cover
    /// both ends, else one built from what is captured now. Inside a sealed
    /// dungeon a grid built for a preview is kept, as a walk's is, when
    /// nothing else is being built. `None` when no collision is loaded.
    pub(super) fn preview_ground(
        &mut self,
        cx: &WalkWorld<'_>,
        sample: &WalkBodySample,
        grid_dungeon: u32,
        goal: Vec3,
        (origin_x, origin_y, size): (f32, f32, f32),
    ) -> Option<PreviewGround> {
        if let Some(grid) = self.grid.clone().filter(|grid| {
            grid.body() == sample.body
                && self.grid_dungeon == grid_dungeon
                && grid.contains(sample.position, COVER_MARGIN)
                && grid.contains(goal, COVER_MARGIN)
                && !self.is_stale(cx, grid, grid_dungeon)
                && !self.objects_moved(cx, grid)
        }) {
            self.grid_idle_since = f64::NAN;
            return Some(PreviewGround {
                landblock_ids: grid.landblock_ids().to_vec(),
                object_ids: Arc::new(grid.object_ids().clone()),
                source: GridSource::Built(grid),
                reused: true,
            });
        }
        if let Some(building) = self.building.as_ref().filter(|building| {
            building.body == sample.body
                && building.dungeon == grid_dungeon
                && building.covers(sample.position, COVER_MARGIN)
                && building.covers(goal, COVER_MARGIN)
        }) {
            return Some(PreviewGround {
                source: GridSource::Building(building.build.clone()),
                reused: true,
                landblock_ids: building.landblock_ids.clone(),
                object_ids: Arc::clone(&building.object_ids),
            });
        }
        let stands_on = |id: u32| cx.goals.stands_still(id);
        let captured = if grid_dungeon != 0 {
            super::geometry::capture_dungeon(
                cx.physics,
                grid_dungeon,
                origin_x,
                origin_y,
                size,
                Some(&stands_on),
            )
        } else {
            super::geometry::capture(cx.physics, origin_x, origin_y, size, Some(&stands_on))
        }?;
        let landblock_ids = captured.landblock_ids.clone();
        let object_ids = Arc::new(captured.object_ids.clone());
        let source = if grid_dungeon != 0 && self.building.is_none() {
            self.grid_idle_since = f64::NAN;
            GridSource::Building(self.share_build(
                captured,
                sample.body,
                grid_dungeon,
                "a preview needed one over the dungeon".to_owned(),
                false,
            ))
        } else {
            GridSource::Capture(Box::new(captured), sample.body)
        };
        Some(PreviewGround {
            source,
            reused: false,
            landblock_ids,
            object_ids,
        })
    }

    /// The cell surfaces of the landblocks a grid covers, copied once per
    /// grid: they do not change while their landblock stays loaded.
    pub(super) fn cells_of(
        &mut self,
        cx: &WalkWorld<'_>,
        landblock_ids: &[u32],
    ) -> Arc<[CellSurface]> {
        if let Some((ids, cells)) = &self.grid_cells
            && ids.as_slice() == landblock_ids
        {
            return Arc::clone(cells);
        }
        let cells: Arc<[CellSurface]> = landblock_ids
            .iter()
            .filter_map(|&id| cx.physics.landblock(id))
            .flat_map(|landblock| landblock.cells.iter().cloned())
            .collect();
        self.grid_cells = Some((landblock_ids.to_vec(), Arc::clone(&cells)));
        cells
    }
}

/// A route point as a preview reports it: measured from the corner of the
/// first landblock, in the cell whose floor lies nearest its height, else
/// the start's cell in a sealed dungeon, else the outdoor cell under it
/// (OpenAC `PreviewPosition`).
fn plan_point(
    point: Vec3,
    start: Vec3,
    global_start: Vec3,
    start_cell: u32,
    cells: &[CellSurface],
    sealed_dungeon: bool,
) -> PlanPoint {
    let global = global_start + point - start;
    let mut cell_id = 0;
    let mut nearest = f32::INFINITY;
    for cell in cells {
        let Some(z) = cell.sample_floor_z(point.x, point.y) else {
            continue;
        };
        if (z - point.z).abs() >= nearest {
            continue;
        }
        nearest = (z - point.z).abs();
        cell_id = cell.cell_id();
    }
    if cell_id == 0 && sealed_dungeon {
        cell_id = start_cell;
    }
    if cell_id == 0 {
        let bx = (global.x / LANDBLOCK_SIZE).floor().clamp(0.0, 255.0);
        let by = (global.y / LANDBLOCK_SIZE).floor().clamp(0.0, 255.0);
        let landblock = ((bx as u32) << 24) | ((by as u32) << 16);
        cell_id = TerrainSurface::compute_outdoor_cell_id_in(
            landblock,
            global.x - bx * LANDBLOCK_SIZE,
            global.y - by * LANDBLOCK_SIZE,
        );
    }
    PlanPoint { cell_id, global }
}
