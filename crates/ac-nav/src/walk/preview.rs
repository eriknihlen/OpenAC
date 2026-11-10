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

use ac_physics::globals::FLOOR_Z;
use ac_physics::{CellSurface, PhysicsEngine, TerrainSurface, point_in_triangle_xy};
use glam::{Quat, Vec3};

use crate::geometry::surfaces_of;

use super::reach::ON_THE_FLOOR;

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

    /// Whether the grid is the controller's, kept or being built, rather
    /// than one the search builds for itself and drops.
    pub(super) fn is_kept(&self) -> bool {
        !matches!(self, Self::Capture(..))
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
    /// The square the grid covers, which may be larger than the one asked
    /// for when a kept grid is searched: `(origin_x, origin_y, size)`.
    pub(super) square: (f32, f32, f32),
}

impl PreviewGround {
    /// The middle of the grid and the radius that takes in all of it, for
    /// finding the portals and objects a route over it must keep out of.
    pub(super) fn around(&self) -> (Vec3, f32) {
        let (origin_x, origin_y, size) = self.square;
        let half = size * 0.5;
        (
            Vec3::new(origin_x + half, origin_y + half, 0.0),
            half * std::f32::consts::SQRT_2,
        )
    }
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
        // The portals and objects are looked for over the whole grid the
        // route is searched on, which a kept grid may make larger than the
        // region asked for.
        let (centre, reach) = ground.around();
        let PreviewGround {
            source,
            reused,
            landblock_ids,
            object_ids,
            ..
        } = ground;
        let cells = self.cells_of(cx, &landblock_ids, source.is_kept());
        let crowd = self.crowd(cx, &sample, object_id);
        let body = sample.body;
        let start = sample.position;
        let leaps = sample.leaps;
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
        let ground: Vec<PlacedGround> = if sealed {
            Vec::new()
        } else {
            landblock_ids
                .iter()
                .filter_map(|&id| cx.physics.landblock(id))
                .map(|landblock| PlacedGround {
                    corner_x: landblock.world_offset_x,
                    corner_y: landblock.world_offset_y,
                    terrain: landblock.terrain.clone(),
                })
                .collect()
        };
        let object_floors: Arc<[CellSurface]> = if sealed {
            Arc::from([])
        } else {
            self.object_floors_for(cx, &landblock_ids, source.is_kept())
        };
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
            let namer = PointNamer {
                start,
                global_start,
                start_cell,
                cells: &cells,
                object_floors: &object_floors,
                ground: &ground,
                sealed_dungeon: sealed,
            };
            let mut came_from = start_cell;
            NavPlan {
                status: PlanStatus::Routed,
                path: found
                    .legs
                    .iter()
                    .map(|&point| {
                        let named = namer.name(point, came_from);
                        came_from = named.cell_id;
                        named
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
                object_ids: Arc::clone(grid.object_ids()),
                square: (grid.origin_x(), grid.origin_y(), grid.size()),
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
                square: (building.origin_x, building.origin_y, building.size),
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
        let square = (captured.origin_x, captured.origin_y, captured.size);
        // A walk asked for meanwhile waits for this build rather than starting
        // its own: over a sealed dungeon it covers every cell, as the walk's
        // would.
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
            square,
        })
    }

    /// The cell surfaces of the landblocks a grid covers. For the grid the
    /// controller keeps (`kept`) they are copied once while those landblocks
    /// stay where they are loaded, and let go with the grid; for a grid a
    /// search builds for itself they are copied for that search alone. The
    /// surfaces are in the world frame, so a landblock loaded again at
    /// another offset is copied again.
    pub(super) fn cells_of(
        &mut self,
        cx: &WalkWorld<'_>,
        landblock_ids: &[u32],
        kept: bool,
    ) -> Arc<[CellSurface]> {
        let copy = || -> Arc<[CellSurface]> {
            landblock_ids
                .iter()
                .filter_map(|&id| cx.physics.landblock(id))
                .flat_map(|landblock| landblock.cells.iter().cloned())
                .collect()
        };
        if !kept {
            return copy();
        }
        let placed = placed_landblocks(cx, landblock_ids);
        if let Some((kept, cells)) = &self.grid_cells
            && *kept == placed
        {
            return Arc::clone(cells);
        }
        let cells = copy();
        self.grid_cells = Some((placed, Arc::clone(&cells)));
        cells
    }

    /// The floors of the objects placed in the interior cells of the
    /// landblocks a grid covers ([`object_floors_of`]), kept with the grid
    /// the controller keeps as its cell surfaces are.
    fn object_floors_for(
        &mut self,
        cx: &WalkWorld<'_>,
        landblock_ids: &[u32],
        kept: bool,
    ) -> Arc<[CellSurface]> {
        if !kept {
            return object_floors_of(cx.physics, landblock_ids).into();
        }
        let placed = placed_landblocks(cx, landblock_ids);
        if let Some((kept, floors)) = &self.grid_object_floors
            && *kept == placed
        {
            return Arc::clone(floors);
        }
        let floors: Arc<[CellSurface]> = object_floors_of(cx.physics, landblock_ids).into();
        self.grid_object_floors = Some((placed, Arc::clone(&floors)));
        floors
    }
}

/// Each resident landblock among `landblock_ids` with where it is loaded.
fn placed_landblocks(cx: &WalkWorld<'_>, landblock_ids: &[u32]) -> Vec<super::PlacedLandblock> {
    landblock_ids
        .iter()
        .filter_map(|&id| {
            cx.physics.landblock(id).map(|landblock| {
                (
                    id,
                    landblock.world_offset_x.to_bits(),
                    landblock.world_offset_y.to_bits(),
                )
            })
        })
        .collect()
}

/// One landblock's ground, placed in the world frame.
struct PlacedGround {
    corner_x: f32,
    corner_y: f32,
    terrain: TerrainSurface,
}

/// Floors whose heights differ by less than this are as near as each other
/// to a point, metres.
const SAME_HEIGHT: f32 = 0.01;

/// Names the points of a found route for the cells they stand in. Outside
/// a sealed dungeon a point is in an interior cell only when it stands on
/// one of that cell's floors; this differs from the C# client, which took
/// the cell whose floor lay nearest at any distance, so open ground over a
/// cellar was named for the cellar.
struct PointNamer<'a> {
    start: Vec3,
    global_start: Vec3,
    start_cell: u32,
    cells: &'a [CellSurface],
    /// The floors of the objects placed in interior cells (a building's
    /// stairs, a dais), each under the id of every interior cell the
    /// collision files it in.
    object_floors: &'a [CellSurface],
    ground: &'a [PlacedGround],
    sealed_dungeon: bool,
}

impl PointNamer<'_> {
    /// A route point as a preview reports it, measured from the corner of
    /// the first landblock. In a sealed dungeon it is in the cell whose
    /// floor lies nearest its height, else the start's cell. Elsewhere it
    /// is in an interior cell only when it stands on one of that cell's
    /// floors, within [`ON_THE_FLOOR`], or on the floor of an object placed
    /// in it, such as a staircase; else in the outdoor cell under it.
    /// Where the ground and a floor, or two floors, are both that close,
    /// the nearer wins, and on a tie (within [`SAME_HEIGHT`]) the cell the
    /// route `came_from`. So a route that comes from outdoors onto a floor
    /// within 0.01 m of the ground stays outdoors across it, through a
    /// building whose floor lies that flush with the ground, until it
    /// reaches a floor that is nearer than the ground.
    fn name(&self, point: Vec3, came_from: u32) -> PlanPoint {
        let global = self.global_start + point - self.start;
        let cell_id = if self.sealed_dungeon {
            nearest_floor_cell(point, self.cells).unwrap_or(self.start_cell)
        } else {
            let mut best = (
                outdoor_cell_at(global),
                self.ground_height(point)
                    .map_or(f32::INFINITY, |z| (z - point.z).abs()),
            );
            for cell in self.cells.iter().chain(self.object_floors) {
                let Some(off) = off_the_floor(cell, point) else {
                    continue;
                };
                let id = cell.cell_id();
                let nearer = off < best.1 - SAME_HEIGHT;
                let tied = off <= best.1 + SAME_HEIGHT && id == came_from && best.0 != came_from;
                if off <= ON_THE_FLOOR && (nearer || tied) {
                    best = (id, off);
                }
            }
            best.0
        };
        PlanPoint { cell_id, global }
    }

    /// The ground's height under a world-frame point, when its landblock
    /// is among those captured.
    fn ground_height(&self, point: Vec3) -> Option<f32> {
        self.ground.iter().find_map(|ground| {
            let x = point.x - ground.corner_x;
            let y = point.y - ground.corner_y;
            ((0.0..LANDBLOCK_SIZE).contains(&x) && (0.0..LANDBLOCK_SIZE).contains(&y))
                .then(|| ground.terrain.sample_z(x, y))
        })
    }
}

/// The floors of the fixed objects the landblocks place inside interior
/// cells, such as a building's stairs: each object's walkable faces
/// (facing up, no steeper than a floor), once under the id of every
/// interior cell the collision files it in. An object placed outdoors is
/// left out even where the collision files it in a room too, as it does a
/// step or a barrel across a doorway: its top is outdoors. Read on the
/// thread that owns the physics world.
fn object_floors_of(physics: &PhysicsEngine, landblock_ids: &[u32]) -> Vec<CellSurface> {
    let mut floors = Vec::new();
    for &landblock_id in landblock_ids {
        for owner in physics
            .shadows
            .capture_static_owners_for_landblock(landblock_id)
        {
            if !placed_indoors(physics, owner) {
                continue;
            }
            let mut indoors = physics
                .shadows
                .owner_cells(owner)
                .iter()
                .copied()
                .filter(|&cell| cell & 0xFFFF >= 0x0100)
                .peekable();
            if indoors.peek().is_none() {
                continue;
            }
            let Some(surfaces) = surfaces_of(physics, owner) else {
                continue;
            };
            let faces: Vec<Vec<Vec3>> = surfaces
                .triangles
                .iter()
                .filter(|t| {
                    let normal = (t.b - t.a).cross(t.c - t.a);
                    let length = normal.length();
                    length > 1e-6 && normal.z / length >= FLOOR_Z
                })
                .map(|t| vec![t.a, t.b, t.c])
                .collect();
            if faces.is_empty() {
                continue;
            }
            for cell in indoors {
                floors.push(CellSurface::transformed(
                    cell,
                    &faces,
                    Quat::IDENTITY,
                    Vec3::ZERO,
                ));
            }
        }
    }
    floors
}

/// Whether an object was placed in an interior cell, rather than outdoors.
fn placed_indoors(physics: &PhysicsEngine, owner: u32) -> bool {
    physics
        .shadows
        .registration(owner)
        .is_some_and(|placed| placed.seed_cell_id & 0xFFFF >= 0x0100)
}

/// The cell whose floor lies nearest a point's height, among those with a
/// floor under it.
fn nearest_floor_cell(point: Vec3, cells: &[CellSurface]) -> Option<u32> {
    let mut found = None;
    let mut nearest = f32::INFINITY;
    for cell in cells {
        let Some(z) = cell.sample_floor_z(point.x, point.y) else {
            continue;
        };
        if (z - point.z).abs() >= nearest {
            continue;
        }
        nearest = (z - point.z).abs();
        found = Some(cell.cell_id());
    }
    found
}

/// How far a point stands off the nearest of a cell's walkable faces under
/// it, metres, or `None` where the cell has none there. Ceilings, which
/// face down, are not floors.
fn off_the_floor(cell: &CellSurface, point: Vec3) -> Option<f32> {
    let mut nearest: Option<f32> = None;
    for [a, b, c] in cell.triangles() {
        let normal = (*b - *a).cross(*c - *a);
        let length = normal.length();
        if length <= 1e-6 || normal.z / length < FLOOR_Z {
            continue;
        }
        if let Some(z) = point_in_triangle_xy(point.x, point.y, *a, *b, *c) {
            let off = (z - point.z).abs();
            if nearest.is_none_or(|n| off < n) {
                nearest = Some(off);
            }
        }
    }
    nearest
}

/// The outdoor cell over which a point lies, by its position from the
/// corner of the first landblock.
fn outdoor_cell_at(global: Vec3) -> u32 {
    let bx = (global.x / LANDBLOCK_SIZE).floor().clamp(0.0, 255.0);
    let by = (global.y / LANDBLOCK_SIZE).floor().clamp(0.0, 255.0);
    let landblock = ((bx as u32) << 24) | ((by as u32) << 16);
    TerrainSurface::compute_outdoor_cell_id_in(
        landblock,
        global.x - bx * LANDBLOCK_SIZE,
        global.y - by * LANDBLOCK_SIZE,
    )
}

#[cfg(test)]
mod tests {
    use glam::{Quat, vec3};

    use super::*;

    /// Eastham's landblock, whose corner the world frame's origin is.
    const LANDBLOCK: u32 = 0xCE94_0000;
    const BUILDING: u32 = 0xCE94_0100;
    const CELLAR: u32 = 0xCE94_0101;
    const DUNGEON_START: u32 = 0x0123_0100;
    const DUNGEON_ROOM: u32 = 0x0123_0102;

    /// A cell holding a level square floor over `x0..x1`, `y0..y1` at
    /// height `z`, and its ceiling 4 m above.
    fn room(cell_id: u32, (x0, x1): (f32, f32), (y0, y1): (f32, f32), z: f32) -> CellSurface {
        let up = 4.0;
        CellSurface::transformed(
            cell_id,
            &[
                vec![
                    vec3(x0, y0, z),
                    vec3(x1, y0, z),
                    vec3(x1, y1, z),
                    vec3(x0, y1, z),
                ],
                vec![
                    vec3(x0, y0, z + up),
                    vec3(x0, y1, z + up),
                    vec3(x1, y1, z + up),
                    vec3(x1, y0, z + up),
                ],
            ],
            Quat::IDENTITY,
            Vec3::ZERO,
        )
    }

    /// Level ground at height 0 over the landblock.
    fn flat_ground() -> Vec<PlacedGround> {
        vec![PlacedGround {
            corner_x: 0.0,
            corner_y: 0.0,
            terrain: TerrainSurface::new(&[0; 81], &[0.0; 256], 0xCE, 0x94, None),
        }]
    }

    fn name(
        point: Vec3,
        came_from: u32,
        cells: &[CellSurface],
        ground: &[PlacedGround],
        sealed_dungeon: bool,
        start_cell: u32,
    ) -> u32 {
        let corner = vec3(
            f32::from(0xCE_u8) * LANDBLOCK_SIZE,
            f32::from(0x94_u8) * LANDBLOCK_SIZE,
            0.0,
        );
        let start = vec3(1.0, 1.0, 0.0);
        let namer = PointNamer {
            start,
            global_start: corner + start,
            start_cell,
            cells,
            object_floors: &[],
            ground,
            sealed_dungeon,
        };
        let named = namer.name(point, came_from);
        assert_eq!(named.global, corner + point);
        named.cell_id
    }

    fn outdoors_at(x: f32, y: f32) -> u32 {
        TerrainSurface::compute_outdoor_cell_id_in(LANDBLOCK, x, y)
    }

    /// Open ground over a cellar whose floor is 10 m down is outdoors.
    #[test]
    fn open_ground_over_a_cellar_is_the_outdoor_cell() {
        let cells = [room(CELLAR, (40.0, 60.0), (40.0, 60.0), -10.0)];
        let ground = flat_ground();
        let point = vec3(50.0, 50.0, 0.0);
        let outdoors = outdoors_at(50.0, 50.0);
        assert_eq!(
            name(point, outdoors, &cells, &ground, false, outdoors),
            outdoors
        );
        assert_eq!(
            name(point, CELLAR, &cells, &ground, false, outdoors),
            outdoors
        );
    }

    /// A point on a building's floor is in the building, whatever lies
    /// above it or below it, and on its cellar's floor in the cellar.
    #[test]
    fn a_point_on_a_floor_keeps_its_cell() {
        let cells = [
            room(CELLAR, (40.0, 60.0), (40.0, 60.0), -10.0),
            room(BUILDING, (40.0, 60.0), (40.0, 60.0), 1.5),
        ];
        let ground = flat_ground();
        let outdoors = outdoors_at(50.0, 50.0);
        let on_the_floor = vec3(50.0, 50.0, 1.5);
        assert_eq!(
            name(on_the_floor, outdoors, &cells, &ground, false, outdoors),
            BUILDING
        );
        let a_node_off_it = vec3(50.0, 50.0, 1.5 + ON_THE_FLOOR - 0.05);
        assert_eq!(
            name(a_node_off_it, outdoors, &cells, &ground, false, outdoors),
            BUILDING
        );
        let in_the_cellar = vec3(50.0, 50.0, -10.0);
        assert_eq!(
            name(in_the_cellar, BUILDING, &cells, &ground, false, outdoors),
            CELLAR
        );
    }

    /// Where the ground and a floor are both under foot, the nearer wins,
    /// and on a tie the cell the route came from.
    #[test]
    fn a_doorway_takes_the_nearer_floor_then_the_one_it_came_from() {
        let ground = flat_ground();
        let outdoors = outdoors_at(50.0, 50.0);
        let raised = [room(BUILDING, (40.0, 60.0), (40.0, 60.0), 0.2)];
        let on_the_ground = vec3(50.0, 50.0, 0.0);
        let on_the_step = vec3(50.0, 50.0, 0.2);
        assert_eq!(
            name(on_the_ground, BUILDING, &raised, &ground, false, outdoors),
            outdoors
        );
        assert_eq!(
            name(on_the_step, outdoors, &raised, &ground, false, outdoors),
            BUILDING
        );
        let flush = [room(BUILDING, (40.0, 60.0), (40.0, 60.0), 0.0)];
        assert_eq!(
            name(on_the_ground, outdoors, &flush, &ground, false, outdoors),
            outdoors
        );
        assert_eq!(
            name(on_the_ground, BUILDING, &flush, &ground, false, outdoors),
            BUILDING
        );
    }

    /// Where a lower room's ceiling is the floor of the room over it, as
    /// at the top of a stair, a point on that floor is in the upper room,
    /// even when the route came up from the lower one and the lower room
    /// lists its ceiling before its floor: a ceiling faces down and is not
    /// a floor.
    #[test]
    fn a_ceiling_is_not_a_floor() {
        const UPPER: u32 = 0xCE94_0102;
        let (x0, x1, y0, y1) = (40.0, 60.0, 40.0, 60.0);
        let lower = CellSurface::transformed(
            BUILDING,
            &[
                vec![
                    vec3(x0, y0, 3.5),
                    vec3(x0, y1, 3.5),
                    vec3(x1, y1, 3.5),
                    vec3(x1, y0, 3.5),
                ],
                vec![
                    vec3(x0, y0, 0.5),
                    vec3(x1, y0, 0.5),
                    vec3(x1, y1, 0.5),
                    vec3(x0, y1, 0.5),
                ],
            ],
            Quat::IDENTITY,
            Vec3::ZERO,
        );
        let upper = room(UPPER, (x0, x1), (y0, y1), 3.5);
        let ground = flat_ground();
        let outdoors = outdoors_at(50.0, 50.0);
        let at_the_top = vec3(50.0, 50.0, 3.5);
        for cells in [[lower.clone(), upper.clone()], [upper, lower]] {
            assert_eq!(
                name(at_the_top, BUILDING, &cells, &ground, false, outdoors),
                UPPER
            );
        }
    }

    /// In a sealed dungeon a point is in the cell whose floor lies nearest
    /// its height, however far, else in the start's cell, as before.
    #[test]
    fn a_sealed_dungeon_names_points_as_before() {
        let cells = [
            room(DUNGEON_START, (0.0, 10.0), (0.0, 10.0), 0.0),
            room(DUNGEON_ROOM, (20.0, 30.0), (0.0, 10.0), -10.0),
        ];
        let high_over_the_room = vec3(25.0, 5.0, 0.0);
        assert_eq!(
            name(
                high_over_the_room,
                DUNGEON_START,
                &cells,
                &[],
                true,
                DUNGEON_START
            ),
            DUNGEON_ROOM
        );
        let between_rooms = vec3(15.0, 5.0, 0.0);
        assert_eq!(
            name(
                between_rooms,
                DUNGEON_ROOM,
                &cells,
                &[],
                true,
                DUNGEON_START
            ),
            DUNGEON_START
        );
    }

    /// Holtburg's two-storey building north-east of the town's middle climbs
    /// from its ground floor, at 66 m, to the floor above, at 69 m, by a
    /// staircase that is an object placed in its ground-floor cell
    /// (0xA9B40117, model 0x0100082A) rather than the cell's own polygons.
    /// A route point on any of its treads is inside the building, as the
    /// cells' own containment puts it, not outdoors: the stairs' treads
    /// stand up to 3 m over the cell's floor, too far from it, and from the
    /// ground, to be named for either by the cells' polygons alone.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn a_staircase_object_is_inside_its_building() {
        use ac_dat::DatCollection;
        use ac_dat::objects::Region;
        use ac_physics::CollisionWorld;
        use ac_physics::content::publish_landblock;

        const HOLTBURG: u32 = 0xA9B4_FFFF;
        const GROUND_FLOOR: u32 = 0xA9B4_0117;
        const STAIRS: u32 = 0x0100_082A;

        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = DatCollection::open(dir).expect("open the retail dats");
        let region: Region = dats.get(0x1300_0000).unwrap().expect("the region");
        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        publish_landblock(
            &mut engine,
            &dats,
            &region.land_defs.land_height_table,
            HOLTBURG,
            Vec3::ZERO,
        )
        .expect("Holtburg publishes")
        .expect("Holtburg exists");
        let landblock = engine.landblock(HOLTBURG).expect("Holtburg is resident");
        let cells = landblock.cells.clone();
        let ground = vec![PlacedGround {
            corner_x: 0.0,
            corner_y: 0.0,
            terrain: landblock.terrain.clone(),
        }];
        let object_floors = object_floors_of(&engine, &[HOLTBURG]);

        let mut stairs: Vec<u32> = engine
            .shadows
            .objects_in_cell(GROUND_FLOOR)
            .iter()
            .filter(|entry| entry.gfx_obj_id == STAIRS)
            .map(|entry| entry.entity_id)
            .collect();
        stairs.sort_unstable();
        stairs.dedup();
        assert_eq!(stairs.len(), 1, "one staircase stands in the cell");
        let staircase = stairs[0];
        let stair_cells = engine.shadows.owner_cells(staircase).to_vec();

        // The middle of each tread, where the cells' containment puts a
        // point a little above it inside the building.
        let treads: Vec<(Vec3, u32)> = surfaces_of(&engine, staircase)
            .expect("the staircase collides")
            .triangles
            .iter()
            .filter(|t| {
                let normal = (t.b - t.a).cross(t.c - t.a);
                normal.length() > 1e-6 && normal.z / normal.length() >= FLOOR_Z
            })
            .map(|t| (t.a + t.b + t.c) / 3.0)
            .filter_map(|tread| {
                let (inside, _) =
                    engine.adjust_position(GROUND_FLOOR, tread + Vec3::new(0.0, 0.0, 0.5));
                (inside & 0xFFFF >= 0x0100).then_some((tread, inside))
            })
            .collect();
        let floor = |tread: Vec3| {
            cells
                .iter()
                .filter_map(|cell| cell.sample_level_z_at_or_below(tread.x, tread.y, tread.z))
                .fold(f32::NEG_INFINITY, f32::max)
        };
        let highest = treads
            .iter()
            .map(|&(tread, _)| tread.z - floor(tread))
            .fold(0.0, f32::max);
        assert!(treads.len() >= 8, "{} treads", treads.len());
        assert!(highest > 2.5, "the treads rise {highest} m over the floor");

        let corner = vec3(
            f32::from(0xA9_u8) * LANDBLOCK_SIZE,
            f32::from(0xB4_u8) * LANDBLOCK_SIZE,
            0.0,
        );
        let namer = |object_floors| PointNamer {
            start: Vec3::ZERO,
            global_start: corner,
            start_cell: GROUND_FLOOR,
            cells: &cells,
            object_floors,
            ground: &ground,
            sealed_dungeon: false,
        };
        let with_the_stairs = namer(&object_floors);
        for &(tread, inside) in &treads {
            let named = with_the_stairs.name(tread, GROUND_FLOOR).cell_id;
            assert!(
                named & 0xFFFF >= 0x0100 && stair_cells.contains(&named),
                "the tread at {tread:?}, inside {inside:08X}, is named {named:08X}"
            );
        }
        // The cells' polygons alone leave treads outdoors.
        let without = namer(&[]);
        let outdoors = treads
            .iter()
            .filter(|&&(tread, _)| without.name(tread, GROUND_FLOOR).cell_id & 0xFFFF < 0x0100)
            .count();
        assert!(outdoors > 0);
    }

    /// The objects landblock 0x0906 places outdoors, 15 m over the ground,
    /// that the collision files in a building's room (0x09060101) too, as
    /// it does an object that reaches into a room, are not that room's
    /// floors: a point on the top of one, where the cells' containment puts
    /// it outdoors, is named outdoors.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn an_object_placed_outdoors_is_not_a_rooms_floor() {
        use ac_dat::DatCollection;
        use ac_dat::objects::Region;
        use ac_physics::CollisionWorld;
        use ac_physics::content::publish_landblock;

        const LANDBLOCK: u32 = 0x0906_FFFF;

        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = DatCollection::open(dir).expect("open the retail dats");
        let region: Region = dats.get(0x1300_0000).unwrap().expect("the region");
        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        publish_landblock(
            &mut engine,
            &dats,
            &region.land_defs.land_height_table,
            LANDBLOCK,
            Vec3::ZERO,
        )
        .expect("the landblock publishes")
        .expect("the landblock exists");
        let landblock = engine
            .landblock(LANDBLOCK)
            .expect("the landblock is resident");
        let cells = landblock.cells.clone();
        let ground = vec![PlacedGround {
            corner_x: 0.0,
            corner_y: 0.0,
            terrain: landblock.terrain.clone(),
        }];
        let object_floors = object_floors_of(&engine, &[LANDBLOCK]);

        // The tops of the objects placed outdoors but filed in a room too,
        // where the containment puts a point a little above them outdoors.
        let mut objects = 0;
        let mut tops = Vec::new();
        for owner in engine
            .shadows
            .capture_static_owners_for_landblock(LANDBLOCK)
        {
            let seed = engine.shadows.registration(owner).unwrap().seed_cell_id;
            let in_a_room = engine
                .shadows
                .owner_cells(owner)
                .iter()
                .any(|&cell| cell & 0xFFFF >= 0x0100);
            if seed & 0xFFFF >= 0x0100 || !in_a_room {
                continue;
            }
            let Some(surfaces) = surfaces_of(&engine, owner) else {
                continue;
            };
            let before = tops.len();
            for t in &surfaces.triangles {
                let normal = (t.b - t.a).cross(t.c - t.a);
                if normal.length() <= 1e-6 || normal.z / normal.length() < FLOOR_Z {
                    continue;
                }
                let top = (t.a + t.b + t.c) / 3.0;
                let (cell, _) = engine.adjust_position(seed, top + Vec3::new(0.0, 0.0, 0.5));
                if cell & 0xFFFF < 0x0100 {
                    tops.push(top);
                }
            }
            if tops.len() > before {
                objects += 1;
            }
        }
        assert_eq!(
            (objects, tops.len()),
            (4, 8),
            "four such objects, two tops each"
        );

        let corner = vec3(
            f32::from(0x09_u8) * LANDBLOCK_SIZE,
            f32::from(0x06_u8) * LANDBLOCK_SIZE,
            0.0,
        );
        let namer = PointNamer {
            start: Vec3::ZERO,
            global_start: corner,
            start_cell: 0,
            cells: &cells,
            object_floors: &object_floors,
            ground: &ground,
            sealed_dungeon: false,
        };
        for &top in &tops {
            let named = namer.name(top, 0).cell_id;
            assert!(
                named & 0xFFFF < 0x0100,
                "the top at {top:?} is named {named:08X}"
            );
        }
    }
}
