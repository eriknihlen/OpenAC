//! Where a body can stand in a square region of the world, and where it can
//! step next.
//!
//! The region's collision is rasterized into columns 0.25 m square, each a
//! list of solid spans (think of a stack of slabs over each paving stone).
//! A node is the top of a walkable span with headroom for the whole body; a
//! link joins nodes in neighbouring columns whose heights differ by no more
//! than the body steps up or down. A node is clear to walk through when no
//! wall between step height and head height comes nearer its centre than
//! [`NavGrid::nearest_wall`], and it is not on the very edge of a ledge. The
//! walls are kept, so straight walks and lines of sight can be tested
//! against them.

use std::collections::VecDeque;
use std::sync::OnceLock;
use std::time::Instant;

use ac_physics::globals::FLOOR_Z;
use glam::{Vec2, Vec3};

use crate::geometry::{LANDBLOCK_SIZE, NavBody, NavCylinder, NavGeometry, NavTerrain, NavTriangle};
use crate::leaps::Surfaces;
use crate::tiles::{ColumnTiles, TILE_COLUMNS};

/// The width of a column, metres.
pub const DEFAULT_CELL_SIZE: f32 = 0.25;
/// Steps a node links in: east, north, west, south, then the diagonals.
pub const DIRECTION_COUNT: i32 = 8;
/// How high a standing body's eyes are above its feet, metres.
pub const EYE_HEIGHT: f32 = 1.6;
/// How high above an object's feet a body looks when it looks at it, metres.
pub const TARGET_HEIGHT: f32 = 1.2;
/// How near a wall a body passing along a path with
/// [`NavGrid::can_brush_along`] keeps its centre, metres: nearer than a
/// walked leg keeps, since a wall the body touches slides it along rather
/// than stopping it.
pub const BRUSH_MARGIN: f32 = 0.25;

/// Terrain is a surface with this much solid ground under it, metres.
const TERRAIN_THICKNESS: f32 = 0.5;
/// How far above the highest cell polygon in a column terrain stands to be
/// ground over the cell, such as over a cellar, rather than the building's.
const TERRAIN_ABOVE_CELLS: f32 = 0.25;
/// Overlapping spans whose tops are this close share walkability, metres.
const FLAG_MERGE_HEIGHT: f32 = 0.1;
/// A walkable polygon covering less of a column than this only touches its
/// edge, square metres.
const MINIMUM_FLOOR_AREA: f32 = 1e-5;
/// How much nearer than the body's radius a wall may come, as a share of a
/// column.
const CLEARANCE_TOLERANCE_COLUMNS: f32 = 0.25;
/// A node must be at least this many steps from a node beside a ledge or a
/// wall.
const EDGE_MARGIN: i32 = 1;
/// An edge of a floor with a wall this many columns from a node is the
/// wall's; with none that near, it is a ledge.
const LEDGE_WALL_COLUMNS: f32 = 2.5;
/// How near a straight walk may pass a wall's footprint before it counts as
/// passing through it, metres.
const WALK_MARGIN: f32 = 0.05;
/// How near a line of sight may pass a wall's footprint before the wall
/// hides what is behind it, metres.
const SIGHT_MARGIN: f32 = 0.02;
/// How far a surface may reach into a body in the air, or a landing lie
/// past its feet, and still count as clear, metres.
const AIR_TOLERANCE: f32 = 0.05;

const UNBOUNDED_DISTANCE: u8 = u8::MAX;
const CLIP_CAPACITY: usize = 32;

/// What a span was rasterized from, so a building's shell can be told from
/// the rooms inside it.
const FROM_CELL: u8 = 1;
const FROM_SHELL: u8 = 2;
const FROM_OBJECT: u8 = 4;
const FROM_TERRAIN: u8 = 8;

const STEP_X: [i32; 8] = [1, 0, -1, 0, 1, -1, -1, 1];
const STEP_Y: [i32; 8] = [0, 1, 0, -1, 1, 1, -1, -1];

/// How much of a dome's radius a body stands on: the part of its top no
/// steeper than a floor.
fn dome_stand_share() -> f32 {
    (1.0 - FLOOR_Z * FLOOR_Z).max(0.0).sqrt()
}

/// Whether a face with this normal (of this length) is floor rather than
/// wall: no steeper than a floor, whichever way it faces.
pub(crate) fn is_floor(normal: Vec3, length: f32) -> bool {
    normal.z.abs() / length >= FLOOR_Z
}

/// Whether a cylinder or dome fills any room at all; one with no radius or
/// height is left out of a grid.
pub(crate) fn has_extent(cylinder: &NavCylinder) -> bool {
    cylinder.radius > 0.0 && cylinder.height > 0.0
}

/// How far from its middle a cylinder or dome fills a grid's columns: half
/// a column beyond its radius, so a column whose middle lies just outside
/// its edge still holds its top.
pub(crate) fn reach_of(cylinder: &NavCylinder, cell_size: f32) -> f32 {
    cylinder.radius + cell_size * 0.5
}

/// How high over its base a cylinder's or a dome's top stands at a point
/// `across_squared` (the square of the horizontal distance) from its
/// middle, and whether a body stands there: anywhere on a cylinder's flat
/// top, and only on the part of a dome's top no steeper than a floor.
pub(crate) fn top_over(cylinder: &NavCylinder, across_squared: f32) -> (f32, bool) {
    if !cylinder.dome {
        return (cylinder.height, true);
    }
    let inside = cylinder.radius * cylinder.radius - across_squared;
    let stand_within = cylinder.radius * dome_stand_share();
    let stands = across_squared <= stand_within * stand_within;
    (
        cylinder.radius + if inside > 0.0 { inside.sqrt() } else { 0.0 },
        stands,
    )
}

/// What building one grid produced, and what it cost.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavGridBuildReport {
    /// Triangles rasterized.
    pub triangles: i32,
    /// Cylinders rasterized.
    pub cylinders: i32,
    /// Solid spans kept.
    pub spans: i32,
    /// Wall pieces kept.
    pub wall_pieces: i32,
    /// Standing points.
    pub nodes: i32,
    /// Standing points clear to walk through.
    pub clear_nodes: i32,
    /// How long the build took.
    pub milliseconds: f64,
}

/// Why a grid cannot be built.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NavGridError {
    /// The column width is not positive or is larger than the region.
    BadCellSize,
}

/// The nodes standing in one column: the index of the lowest and how many
/// there are.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
struct ColumnNodes {
    first: i32,
    count: i32,
}

/// Where a body can stand in a square region of the world, and where it can
/// step next. See the module documentation.
#[derive(Debug)]
pub struct NavGrid {
    origin_x: f32,
    origin_y: f32,
    cell_size: f32,
    side: i32,
    landblock_ids: Vec<u32>,
    object_fingerprint: u64,
    object_ids: std::sync::Arc<std::collections::HashSet<u32>>,
    body: NavBody,
    nearest_wall: f32,
    column_nodes: ColumnTiles<ColumnNodes>,
    node_column: Vec<i32>,
    node_z: Vec<f32>,
    node_ceiling: Vec<f32>,
    links: Vec<i32>,
    border_distance: Vec<u8>,
    wall_distance: Vec<f32>,
    clear: Vec<bool>,
    terrain: Vec<bool>,
    walls: WallPieces,
    report: NavGridBuildReport,
    surfaces: OnceLock<Surfaces>,
}

impl NavGrid {
    /// World x of the region's south-west corner.
    #[must_use]
    pub fn origin_x(&self) -> f32 {
        self.origin_x
    }

    /// World y of the region's south-west corner.
    #[must_use]
    pub fn origin_y(&self) -> f32 {
        self.origin_y
    }

    /// The width of a column, metres.
    #[must_use]
    pub fn cell_size(&self) -> f32 {
        self.cell_size
    }

    /// Columns along each edge of the region.
    #[must_use]
    pub fn side(&self) -> i32 {
        self.side
    }

    /// The length of the region's sides, metres.
    #[must_use]
    pub fn size(&self) -> f32 {
        self.side as f32 * self.cell_size
    }

    /// The resident landblocks whose geometry the grid was built from.
    #[must_use]
    pub fn landblock_ids(&self) -> &[u32] {
        &self.landblock_ids
    }

    /// What the objects the server placed in the region came to when it was
    /// built.
    #[must_use]
    pub fn object_fingerprint(&self) -> u64 {
        self.object_fingerprint
    }

    /// The objects the server placed whose collision the grid holds,
    /// shared rather than copied by the searches that run over the grid.
    #[must_use]
    pub fn object_ids(&self) -> &std::sync::Arc<std::collections::HashSet<u32>> {
        &self.object_ids
    }

    /// The body the grid was built for.
    #[must_use]
    pub fn body(&self) -> NavBody {
        self.body
    }

    /// The nearest a wall may come to a body walking the grid: the body's
    /// radius, less a quarter of a column, because the body can stand
    /// anywhere in its column and slides along walls.
    #[must_use]
    pub fn nearest_wall(&self) -> f32 {
        self.nearest_wall
    }

    /// What building the grid produced.
    #[must_use]
    pub fn report(&self) -> NavGridBuildReport {
        self.report
    }

    /// How many standing points the grid holds.
    #[must_use]
    pub fn node_count(&self) -> i32 {
        self.node_z.len() as i32
    }

    /// The pieces of floor and the spots leaps aim at, worked out once.
    pub(crate) fn surfaces(&self) -> &Surfaces {
        self.surfaces.get_or_init(|| Surfaces::new(self))
    }

    /// The columns east and north one step in a direction moves.
    #[must_use]
    pub fn step_of(direction: i32) -> (i32, i32) {
        (STEP_X[direction as usize], STEP_Y[direction as usize])
    }

    /// The direction of a step, or -1 when it is not one.
    #[must_use]
    pub fn direction_of(step_x: i32, step_y: i32) -> i32 {
        for direction in 0..DIRECTION_COUNT {
            if STEP_X[direction as usize] == step_x && STEP_Y[direction as usize] == step_y {
                return direction;
            }
        }
        -1
    }

    /// Whether a point lies inside the region, at least `margin` from its
    /// edges.
    #[must_use]
    pub fn contains(&self, point: Vec3, margin: f32) -> bool {
        point.x >= self.origin_x + margin
            && point.y >= self.origin_y + margin
            && point.x <= self.origin_x + self.size() - margin
            && point.y <= self.origin_y + self.size() - margin
    }

    /// The node a step from `node` lands on, or -1.
    #[must_use]
    pub fn link(&self, node: i32, direction: i32) -> i32 {
        self.links[(node * DIRECTION_COUNT + direction) as usize]
    }

    /// Whether a node is clear to walk through.
    #[must_use]
    pub fn is_clear(&self, node: i32) -> bool {
        self.clear[node as usize]
    }

    /// Whether a node's floor is bare terrain, rather than an object, a
    /// building or a room standing on or over it.
    #[must_use]
    pub fn is_terrain(&self, node: i32) -> bool {
        self.terrain[node as usize]
    }

    /// Whether a body stands on a node at all: no wall crowds it, though its
    /// floor may be too small to stand clear of the edges. The top of a post
    /// is standable without being clear: a body perches there, and never
    /// walks across it.
    #[must_use]
    pub fn is_standable(&self, node: i32) -> bool {
        self.wall_distance[node as usize] >= self.nearest_wall
    }

    /// Steps from `node` to the nearest node beside a ledge or a wall.
    #[must_use]
    pub fn border_distance(&self, node: i32) -> i32 {
        i32::from(self.border_distance[node as usize])
    }

    /// Horizontal distance from the node's centre to the nearest wall the
    /// body would touch, or infinity.
    #[must_use]
    pub fn wall_distance(&self, node: i32) -> f32 {
        self.wall_distance[node as usize]
    }

    /// The height of the underside of whatever stands above a node, or
    /// infinity.
    #[must_use]
    pub fn ceiling(&self, node: i32) -> f32 {
        self.node_ceiling[node as usize]
    }

    /// Whether a body in the air with its feet at `feet` touches nothing: no
    /// wall nearer its centre than [`Self::nearest_wall`] overlaps its
    /// height, and no floor or ceiling in the columns under it cuts into it.
    #[must_use]
    pub fn is_open_air(&self, feet: Vec3) -> bool {
        let local_x = feet.x - self.origin_x;
        let local_y = feet.y - self.origin_y;
        let top = feet.z + self.body.height;
        let centre_x = (local_x / self.cell_size).floor() as i32;
        let centre_y = (local_y / self.cell_size).floor() as i32;
        let reach = (self.body.radius / self.cell_size).ceil() as i32;
        let under_squared = self.nearest_wall * self.nearest_wall;
        for y in (centre_y - reach).max(0)..=(centre_y + reach).min(self.side - 1) {
            for x in (centre_x - reach).max(0)..=(centre_x + reach).min(self.side - 1) {
                let mut piece = self.walls.first(x, y);
                while piece != -1 {
                    if self.walls.high[piece as usize] > feet.z + AIR_TOLERANCE
                        && self.walls.low[piece as usize] < top
                        && self.walls.distance(piece, local_x, local_y) < self.nearest_wall
                    {
                        return false;
                    }
                    piece = self.walls.next[piece as usize];
                }
                let dx = (x as f32 + 0.5) * self.cell_size - local_x;
                let dy = (y as f32 + 0.5) * self.cell_size - local_y;
                if dx * dx + dy * dy > under_squared {
                    continue;
                }
                let (first, count) = self.nodes_in_column(x, y);
                let mut below = -1;
                for node in first..first + count {
                    if self.node_z[node as usize] <= feet.z + AIR_TOLERANCE {
                        below = node;
                    } else if self.node_z[node as usize] < top {
                        return false;
                    }
                }
                // The ceiling over the floor below cuts into the body only
                // where it lies between its feet and its head: a body above a
                // room's ceiling, as one flying over the roof above the room,
                // is not under it.
                if below >= 0
                    && self.node_ceiling[below as usize] < top
                    && self.node_ceiling[below as usize] > feet.z + AIR_TOLERANCE
                {
                    return false;
                }
            }
        }
        true
    }

    /// The node in the column under a falling body whose top its feet
    /// crossed on the way down from `from_height` to `feet`, or -1.
    #[must_use]
    pub fn landing_under(&self, feet: Vec3, from_height: f32) -> i32 {
        let x = ((feet.x - self.origin_x) / self.cell_size).floor() as i32;
        let y = ((feet.y - self.origin_y) / self.cell_size).floor() as i32;
        let (first, count) = self.nodes_in_column(x, y);
        let mut node = first + count - 1;
        while node >= first {
            let z = self.node_z[node as usize];
            if z <= from_height + AIR_TOLERANCE && z >= feet.z - AIR_TOLERANCE {
                return node;
            }
            node -= 1;
        }
        -1
    }

    /// Where a node stands: the middle of its column, at its floor's height.
    #[must_use]
    pub fn position(&self, node: i32) -> Vec3 {
        let column = self.node_column[node as usize];
        Vec3::new(
            self.origin_x + ((column % self.side) as f32 + 0.5) * self.cell_size,
            self.origin_y + ((column / self.side) as f32 + 0.5) * self.cell_size,
            self.node_z[node as usize],
        )
    }

    /// A node's column.
    #[must_use]
    pub fn column_of(&self, node: i32) -> (i32, i32) {
        let column = self.node_column[node as usize];
        (column % self.side, column / self.side)
    }

    /// The nodes standing in one column, lowest first: the first and how
    /// many.
    #[must_use]
    pub fn nodes_in_column(&self, x: i32, y: i32) -> (i32, i32) {
        if (x as u32) >= (self.side as u32) || (y as u32) >= (self.side as u32) {
            return (0, 0);
        }
        let nodes = self.column_nodes.get(x, y);
        (nodes.first, nodes.count)
    }

    /// The clear node nearest `position` within a horizontal radius and a
    /// height tolerance, or -1 when there is none.
    #[must_use]
    pub fn find_node(&self, position: Vec3, radius: f32, height_tolerance: f32) -> i32 {
        self.nodes_near(position, radius, height_tolerance, false)
            .first()
            .map_or(-1, |&(node, _)| node)
    }

    /// The nearest node a body stands on that it could reach in a straight
    /// line without passing through a wall: a clear one where there is one,
    /// and otherwise a perch, such as the top of a post a body stands on
    /// without room to walk.
    #[must_use]
    pub fn find_standing_node(&self, position: Vec3, radius: f32, height_tolerance: f32) -> i32 {
        let walkable = self.find_walkable_node(position, radius, height_tolerance);
        if walkable >= 0 {
            return walkable;
        }
        for (node, _) in self.nodes_near(position, radius, height_tolerance, true) {
            if self.is_open_line(position, self.position(node)) {
                return node;
            }
        }
        -1
    }

    /// The clear node nearest `position`, within a horizontal radius and a
    /// height tolerance, that a body standing there could walk to in a
    /// straight line without passing through a wall, or -1.
    #[must_use]
    pub fn find_walkable_node(&self, position: Vec3, radius: f32, height_tolerance: f32) -> i32 {
        for (node, _) in self.nodes_near(position, radius, height_tolerance, false) {
            if self.is_open_line(position, self.position(node)) {
                return node;
            }
        }
        -1
    }

    /// Whether a body walking straight between two points, standing at their
    /// heights, keeps at least [`Self::nearest_wall`] from every wall it
    /// would touch.
    #[must_use]
    pub fn can_sweep(&self, from: Vec3, to: Vec3) -> bool {
        !self.wall_near(from, to, self.nearest_wall, false)
    }

    /// Whether a body walking straight between two points, standing at their
    /// heights, passes through no wall.
    #[must_use]
    pub fn is_open_line(&self, from: Vec3, to: Vec3) -> bool {
        !self.wall_near(from, to, WALK_MARGIN, false)
    }

    /// Whether a body standing on `node` can see an object whose feet are
    /// at `target`: no wall crosses the line from the body's eyes to the
    /// object, the object is not above the node's ceiling, and it is not so
    /// far below the node that the node's own floor hides it.
    #[must_use]
    pub fn can_see(&self, node: i32, target: Vec3) -> bool {
        let feet = self.position(node);
        let eye = Vec3::new(feet.x, feet.y, feet.z + EYE_HEIGHT);
        let seen = Vec3::new(target.x, target.y, target.z + TARGET_HEIGHT);
        seen.z < self.node_ceiling[node as usize]
            && seen.z >= feet.z
            && !self.wall_near(eye, seen, SIGHT_MARGIN, true)
    }

    /// The wall pieces that hide `target` from a body standing on `node`:
    /// for each, its column, its height range, and the sight line's height
    /// where they are nearest. For diagnostics.
    #[must_use]
    pub fn sight_blockers(&self, node: i32, target: Vec3) -> Vec<(i32, i32, f32, f32, f32)> {
        let feet = self.position(node);
        let eye = Vec3::new(feet.x, feet.y, feet.z + EYE_HEIGHT);
        let seen = Vec3::new(target.x, target.y, target.z + TARGET_HEIGHT);
        let start = Vec2::new(eye.x - self.origin_x, eye.y - self.origin_y);
        let end = Vec2::new(seen.x - self.origin_x, seen.y - self.origin_y);
        let x0 = (((start.x.min(end.x) - SIGHT_MARGIN) / self.cell_size).floor() as i32).max(0);
        let x1 = (((start.x.max(end.x) + SIGHT_MARGIN) / self.cell_size).floor() as i32)
            .min(self.side - 1);
        let y0 = (((start.y.min(end.y) - SIGHT_MARGIN) / self.cell_size).floor() as i32).max(0);
        let y1 = (((start.y.max(end.y) + SIGHT_MARGIN) / self.cell_size).floor() as i32)
            .min(self.side - 1);
        let mut blockers = Vec::new();
        for y in y0..=y1 {
            for x in x0..=x1 {
                let mut piece = self.walls.first(x, y);
                while piece != -1 {
                    let (distance, along) = self.walls.distance_to_segment(piece, start, end);
                    if distance < SIGHT_MARGIN {
                        let height = eye.z + (seen.z - eye.z) * along;
                        let (low, high) = (
                            self.walls.low[piece as usize],
                            self.walls.high[piece as usize],
                        );
                        if low <= height && high >= height {
                            blockers.push((x, y, low, high, height));
                        }
                    }
                    piece = self.walls.next[piece as usize];
                }
            }
        }
        blockers
    }

    /// Whether a body can walk straight from one node to another: every
    /// column along the line between them holds a clear node linked to the
    /// last, and no wall comes nearer the line than
    /// [`Self::nearest_wall`].
    #[must_use]
    pub fn can_walk_straight(&self, from: i32, to: i32) -> bool {
        self.can_walk_straight_keeping(from, to, self.nearest_wall, EDGE_MARGIN)
    }

    /// The nodes a straight walk from one node to another steps onto, one
    /// for each column along the line after the first, following links;
    /// empty when a column along the line holds no node linked to the last.
    #[must_use]
    pub fn nodes_along(&self, from: i32, to: i32) -> Vec<i32> {
        let (mut x, mut y) = self.column_of(from);
        let (target_x, target_y) = self.column_of(to);
        let distance_x = (target_x - x).abs();
        let distance_y = (target_y - y).abs();
        let sign_x = (target_x - x).signum();
        let sign_y = (target_y - y).signum();
        let mut error = distance_x - distance_y;
        let mut node = from;
        let mut nodes = Vec::with_capacity(distance_x.max(distance_y) as usize);
        while x != target_x || y != target_y {
            let mut move_x = 0;
            let mut move_y = 0;
            let doubled = error * 2;
            if doubled > -distance_y {
                error -= distance_y;
                x += sign_x;
                move_x = sign_x;
            }
            if doubled < distance_x {
                error += distance_x;
                y += sign_y;
                move_y = sign_y;
            }
            node = self.link(node, Self::direction_of(move_x, move_y));
            if node < 0 {
                return Vec::new();
            }
            nodes.push(node);
        }
        nodes
    }

    /// Whether a body can walk straight from one node to another and keep at
    /// least `clearance` from walls and `border_steps` steps from ledges:
    /// every column along the line between them holds a clear node, linked
    /// to the last, that is at least that far from both, and no wall comes
    /// nearer the line than `clearance` or [`Self::nearest_wall`],
    /// whichever is farther.
    #[must_use]
    pub fn can_walk_straight_keeping(
        &self,
        from: i32,
        to: i32,
        clearance: f32,
        border_steps: i32,
    ) -> bool {
        let (mut x, mut y) = self.column_of(from);
        let (target_x, target_y) = self.column_of(to);
        let distance_x = (target_x - x).abs();
        let distance_y = (target_y - y).abs();
        let sign_x = (target_x - x).signum();
        let sign_y = (target_y - y).signum();
        let mut error = distance_x - distance_y;
        let mut node = from;
        while x != target_x || y != target_y {
            let mut move_x = 0;
            let mut move_y = 0;
            let doubled = error * 2;
            if doubled > -distance_y {
                error -= distance_y;
                x += sign_x;
                move_x = sign_x;
            }
            if doubled < distance_x {
                error += distance_x;
                y += sign_y;
                move_y = sign_y;
            }
            node = self.link(node, Self::direction_of(move_x, move_y));
            if node < 0
                || !self.is_clear(node)
                || self.wall_distance(node) < clearance
                || self.border_distance(node) < border_steps
            {
                return false;
            }
        }
        node == to
            && !self.wall_near(
                self.position(from),
                self.position(to),
                self.nearest_wall.max(clearance),
                false,
            )
    }

    /// Whether a body can pass along a path of points brushing walls, as it
    /// does running around a corner: the first point stands on a node within
    /// a step of its height, every column along the path holds a node linked
    /// to the last and not at a ledge, and no wall comes nearer the path than
    /// [`BRUSH_MARGIN`].
    #[must_use]
    pub fn can_brush_along(&self, path: &[Vec3]) -> bool {
        if path.is_empty() {
            return false;
        }
        let mut node = self.node_at(path[0], self.body.step_up_height);
        if node < 0 || self.at_ledge(node) {
            return false;
        }
        for index in 1..path.len() {
            let start = node;
            let (mut x, mut y) = self.column_of(node);
            let target_x = ((path[index].x - self.origin_x) / self.cell_size).floor() as i32;
            let target_y = ((path[index].y - self.origin_y) / self.cell_size).floor() as i32;
            let distance_x = (target_x - x).abs();
            let distance_y = (target_y - y).abs();
            let sign_x = (target_x - x).signum();
            let sign_y = (target_y - y).signum();
            let mut error = distance_x - distance_y;
            while x != target_x || y != target_y {
                let mut move_x = 0;
                let mut move_y = 0;
                let doubled = error * 2;
                if doubled > -distance_y {
                    error -= distance_y;
                    x += sign_x;
                    move_x = sign_x;
                }
                if doubled < distance_x {
                    error += distance_x;
                    y += sign_y;
                    move_y = sign_y;
                }
                node = self.link(node, Self::direction_of(move_x, move_y));
                if node < 0 || self.at_ledge(node) {
                    return false;
                }
            }
            let from = path[index - 1].with_z(self.node_z[start as usize]);
            let to = path[index].with_z(self.node_z[node as usize]);
            if self.wall_near(from, to, BRUSH_MARGIN, false) {
                return false;
            }
        }
        true
    }

    /// Whether a node stands at a ledge: on the very edge of its floor, with
    /// no wall within [`LEDGE_WALL_COLUMNS`] columns to account for the
    /// edge.
    fn at_ledge(&self, node: i32) -> bool {
        self.border_distance(node) < EDGE_MARGIN
            && self.wall_distance(node) > self.cell_size * LEDGE_WALL_COLUMNS
    }

    /// The node in a point's column nearest its height, within
    /// `height_tolerance`, or -1.
    fn node_at(&self, point: Vec3, height_tolerance: f32) -> i32 {
        let (first, count) = self.nodes_in_column(
            ((point.x - self.origin_x) / self.cell_size).floor() as i32,
            ((point.y - self.origin_y) / self.cell_size).floor() as i32,
        );
        let mut nearest = -1;
        let mut nearest_rise = height_tolerance;
        for node in first..first + count {
            let rise = (self.node_z[node as usize] - point.z).abs();
            if rise <= nearest_rise {
                nearest = node;
                nearest_rise = rise;
            }
        }
        nearest
    }

    /// Builds the grid of a region for a body, with columns `cell_size`
    /// metres wide.
    ///
    /// # Errors
    ///
    /// When `cell_size` is not positive or is larger than the region.
    pub fn build(
        geometry: &NavGeometry,
        body: NavBody,
        cell_size: f32,
    ) -> Result<Self, NavGridError> {
        if !(cell_size > 0.0) || cell_size > geometry.size {
            return Err(NavGridError::BadCellSize);
        }
        let clock = Instant::now();
        let side = ((geometry.size / cell_size) - 0.001).ceil() as i32;
        let mut spans = SpanColumns::new(side);
        let mut walls = WallPieces::new(side);
        let mut highest_cell = ColumnTiles::<f32>::new(side, f32::NEG_INFINITY);
        let origin = Vec3::new(geometry.origin_x, geometry.origin_y, 0.0);

        for triangle in &geometry.cell_triangles {
            rasterize_triangle(
                &mut spans,
                &mut walls,
                triangle,
                origin,
                cell_size,
                side,
                Some(&mut highest_cell),
                FROM_CELL,
            );
        }
        for triangle in &geometry.shell_triangles {
            rasterize_triangle(
                &mut spans, &mut walls, triangle, origin, cell_size, side, None, FROM_SHELL,
            );
        }
        for triangle in &geometry.object_triangles {
            rasterize_triangle(
                &mut spans,
                &mut walls,
                triangle,
                origin,
                cell_size,
                side,
                None,
                FROM_OBJECT,
            );
        }
        for cylinder in &geometry.cylinders {
            rasterize_cylinder(
                &mut spans,
                &mut walls,
                cylinder,
                origin,
                cell_size,
                side,
                FROM_OBJECT,
            );
        }
        for terrain in &geometry.terrains {
            rasterize_terrain(&mut spans, terrain, origin, cell_size, side, &highest_cell);
        }

        let mut column_nodes = ColumnTiles::<ColumnNodes>::new(side, ColumnNodes::default());
        let mut node_columns = Vec::new();
        let mut node_heights = Vec::new();
        let mut node_ceilings = Vec::new();
        let mut node_terrain = Vec::new();
        let tiles = spans.head.tiles_per_side();
        let mut column = Vec::with_capacity(8);
        let mut cell_above = Vec::with_capacity(8);
        for tile_y in 0..tiles {
            for tile_x in 0..tiles {
                if !spans.head.has_tile(tile_x, tile_y) {
                    continue;
                }
                let last_y = ((tile_y + 1) * TILE_COLUMNS).min(side);
                let last_x = ((tile_x + 1) * TILE_COLUMNS).min(side);
                for y in tile_y * TILE_COLUMNS..last_y {
                    for x in tile_x * TILE_COLUMNS..last_x {
                        let first = node_heights.len() as i32;
                        column.clear();
                        let mut span = spans.head.get(x, y);
                        while span != -1 {
                            column.push(span);
                            span = spans.next[span as usize];
                        }
                        cell_above.clear();
                        let mut seen_cell = false;
                        for index in (0..column.len()).rev() {
                            cell_above.push(seen_cell);
                            seen_cell |= (spans.source[column[index] as usize] & FROM_CELL) != 0;
                        }
                        cell_above.reverse();
                        for index in 0..column.len() {
                            let span = column[index];
                            if !spans.walkable[span as usize]
                                || shrouded(&spans, span, cell_above[index])
                            {
                                continue;
                            }
                            let mut ceiling = f32::INFINITY;
                            for higher in index + 1..column.len() {
                                if shrouded(&spans, column[higher], cell_above[higher]) {
                                    continue;
                                }
                                ceiling = spans.min[column[higher] as usize];
                                break;
                            }
                            if ceiling - spans.max[span as usize] < body.height {
                                continue;
                            }
                            node_columns.push(y * side + x);
                            node_heights.push(spans.max[span as usize]);
                            node_ceilings.push(ceiling);
                            node_terrain.push(spans.source[span as usize] == FROM_TERRAIN);
                        }
                        if node_heights.len() as i32 > first {
                            *column_nodes.slot(x, y) = ColumnNodes {
                                first,
                                count: node_heights.len() as i32 - first,
                            };
                        }
                    }
                }
            }
        }

        let node_count = node_heights.len();
        let mut links = vec![-1; node_count * DIRECTION_COUNT as usize];
        link_orthogonal_neighbours(
            side,
            body,
            &column_nodes,
            &node_columns,
            &node_heights,
            &node_ceilings,
            &mut links,
        );
        link_diagonal_neighbours(node_count, &mut links);
        let border_distance = measure_border_distance(&links, node_count);
        let wall_distance =
            measure_wall_distance(side, cell_size, body, &node_columns, &node_heights, &walls);

        let nearest_wall = nearest_wall_for(body, cell_size);
        let mut clear = vec![false; node_count];
        let mut clear_count = 0;
        for node in 0..node_count {
            clear[node] = i32::from(border_distance[node]) >= EDGE_MARGIN
                && wall_distance[node] >= nearest_wall;
            if clear[node] {
                clear_count += 1;
            }
        }

        let report = NavGridBuildReport {
            triangles: (geometry.cell_triangles.len()
                + geometry.shell_triangles.len()
                + geometry.object_triangles.len()) as i32,
            cylinders: geometry.cylinders.len() as i32,
            spans: spans.live,
            wall_pieces: walls.count(),
            nodes: node_count as i32,
            clear_nodes: clear_count,
            milliseconds: clock.elapsed().as_secs_f64() * 1000.0,
        };
        Ok(Self {
            origin_x: geometry.origin_x,
            origin_y: geometry.origin_y,
            cell_size,
            side,
            landblock_ids: geometry.landblock_ids.clone(),
            object_fingerprint: geometry.object_fingerprint,
            object_ids: std::sync::Arc::new(geometry.object_ids.clone()),
            body,
            nearest_wall,
            column_nodes,
            node_column: node_columns,
            node_z: node_heights,
            node_ceiling: node_ceilings,
            links,
            border_distance,
            wall_distance,
            clear,
            terrain: node_terrain,
            walls,
            report,
            surfaces: OnceLock::new(),
        })
    }

    /// The clear nodes within a horizontal radius and a height tolerance of
    /// a point, nearest first.
    fn nodes_near(
        &self,
        position: Vec3,
        radius: f32,
        height_tolerance: f32,
        standable: bool,
    ) -> Vec<(i32, f32)> {
        let centre_x = ((position.x - self.origin_x) / self.cell_size).floor() as i32;
        let centre_y = ((position.y - self.origin_y) / self.cell_size).floor() as i32;
        let reach = (radius / self.cell_size).ceil() as i32;
        let mut near = Vec::new();
        for y in (centre_y - reach).max(0)..=(centre_y + reach).min(self.side - 1) {
            for x in (centre_x - reach).max(0)..=(centre_x + reach).min(self.side - 1) {
                let (first, count) = self.nodes_in_column(x, y);
                for node in first..first + count {
                    let usable = if standable {
                        self.is_standable(node)
                    } else {
                        self.is_clear(node)
                    };
                    if !usable {
                        continue;
                    }
                    let at = self.position(node);
                    let rise = (at.z - position.z).abs();
                    if rise > height_tolerance {
                        continue;
                    }
                    let dx = at.x - position.x;
                    let dy = at.y - position.y;
                    let horizontal = dx * dx + dy * dy;
                    if horizontal > radius * radius {
                        continue;
                    }
                    near.push((node, horizontal + rise * rise));
                }
            }
        }
        crate::order::intro_sort(&mut near, |l, r| crate::order::compare_f32(l.1, r.1));
        near
    }

    /// Whether a wall piece comes within `margin` of the straight line
    /// between two points. For a body the points are where it stands, and a
    /// piece counts when it rises into the body standing on the line where
    /// they are nearest. For a line of sight a piece counts when it spans
    /// the line's height there.
    fn wall_near(&self, from: Vec3, to: Vec3, margin: f32, sight: bool) -> bool {
        let start = Vec2::new(from.x - self.origin_x, from.y - self.origin_y);
        let end = Vec2::new(to.x - self.origin_x, to.y - self.origin_y);
        let reach = margin / self.cell_size;
        let start_x = start.x / self.cell_size;
        let start_y = start.y / self.cell_size;
        let end_x = end.x / self.cell_size;
        let end_y = end.y / self.cell_size;
        let first_row = ((start_y.min(end_y) - reach).floor() as i32).max(0);
        let last_row = ((start_y.max(end_y) + reach).floor() as i32).min(self.side - 1);
        for row in first_row..=last_row {
            let (low_x, high_x) = if (end_y - start_y).abs() < 1e-6 {
                (start_x.min(end_x), start_x.max(end_x))
            } else {
                let enter = ((row as f32 - reach - start_y) / (end_y - start_y)).clamp(0.0, 1.0);
                let leave =
                    ((row as f32 + 1.0 + reach - start_y) / (end_y - start_y)).clamp(0.0, 1.0);
                let enter_x = start_x + (end_x - start_x) * enter;
                let leave_x = start_x + (end_x - start_x) * leave;
                (enter_x.min(leave_x), enter_x.max(leave_x))
            };
            let first_column = ((low_x - reach).floor() as i32).max(0);
            let last_column = ((high_x + reach).floor() as i32).min(self.side - 1);
            for column in first_column..=last_column {
                let mut piece = self.walls.first(column, row);
                while piece != -1 {
                    let (distance, along) = self.walls.distance_to_segment(piece, start, end);
                    if distance < margin {
                        let height = from.z + (to.z - from.z) * along;
                        let low = self.walls.low[piece as usize];
                        let high = self.walls.high[piece as usize];
                        let counts = if sight {
                            low <= height && high >= height
                        } else {
                            high > height + self.body.step_up_height
                                && low < height + self.body.height
                        };
                        if counts {
                            return true;
                        }
                    }
                    piece = self.walls.next[piece as usize];
                }
            }
        }
        false
    }
}

/// Whether a span is a building's shell where the building's own cells stand
/// above it. That is the shell's floor and roof over rooms the cells already
/// hold, and a body inside walks those cells' floors and stairs instead,
/// under their ceilings.
fn shrouded(spans: &SpanColumns, span: i32, cell_above: bool) -> bool {
    cell_above && (spans.source[span as usize] & !FROM_SHELL) == 0
}

fn nearest_wall_for(body: NavBody, cell_size: f32) -> f32 {
    body.radius - cell_size * CLEARANCE_TOLERANCE_COLUMNS
}

fn link_orthogonal_neighbours(
    side: i32,
    body: NavBody,
    column_nodes: &ColumnTiles<ColumnNodes>,
    node_column: &[i32],
    heights: &[f32],
    ceilings: &[f32],
    links: &mut [i32],
) {
    for node in 0..heights.len() {
        let x = node_column[node] % side;
        let y = node_column[node] / side;
        for direction in 0..4usize {
            let neighbour_x = x + STEP_X[direction];
            let neighbour_y = y + STEP_Y[direction];
            if (neighbour_x as u32) >= (side as u32) || (neighbour_y as u32) >= (side as u32) {
                continue;
            }
            let neighbour = column_nodes.get(neighbour_x, neighbour_y);
            let mut best = -1;
            let mut best_rise = f32::INFINITY;
            for other in neighbour.first..neighbour.first + neighbour.count {
                let other_index = other as usize;
                let rise = heights[other_index] - heights[node];
                if rise > body.step_up_height || rise < -body.step_down_height {
                    continue;
                }
                let headroom = ceilings[node].min(ceilings[other_index])
                    - heights[node].max(heights[other_index]);
                if headroom < body.height {
                    continue;
                }
                if rise.abs() < best_rise {
                    best_rise = rise.abs();
                    best = other;
                }
            }
            links[node * DIRECTION_COUNT as usize + direction] = best;
        }
    }
}

/// A diagonal step needs both of the square steps around its corner.
fn link_diagonal_neighbours(nodes: usize, links: &mut [i32]) {
    let stride = DIRECTION_COUNT as usize;
    for node in 0..nodes {
        for direction in 4..stride {
            let along_x = if STEP_X[direction] > 0 { 0 } else { 2 };
            let along_y = if STEP_Y[direction] > 0 { 1 } else { 3 };
            let via_x = links[node * stride + along_x];
            let via_y = links[node * stride + along_y];
            if via_x == -1 || via_y == -1 {
                continue;
            }
            let corner = links[via_x as usize * stride + along_y];
            if corner != -1 && corner == links[via_y as usize * stride + along_x] {
                links[node * stride + direction] = corner;
            }
        }
    }
}

fn measure_border_distance(links: &[i32], nodes: usize) -> Vec<u8> {
    let stride = DIRECTION_COUNT as usize;
    let mut distance = vec![UNBOUNDED_DISTANCE; nodes];
    let mut frontier = VecDeque::new();
    for node in 0..nodes {
        if links[node * stride..node * stride + stride].contains(&-1) {
            distance[node] = 0;
            frontier.push_back(node);
        }
    }
    while let Some(node) = frontier.pop_front() {
        let next = i32::from(distance[node]) + 1;
        if next >= i32::from(UNBOUNDED_DISTANCE) {
            continue;
        }
        for direction in 0..stride {
            let other = links[node * stride + direction];
            if other != -1 && i32::from(distance[other as usize]) > next {
                distance[other as usize] = next as u8;
                frontier.push_back(other as usize);
            }
        }
    }
    distance
}

/// The horizontal distance from each node's centre to the nearest wall
/// piece that rises into the body: above what the body steps over and below
/// its head.
fn measure_wall_distance(
    side: i32,
    cell_size: f32,
    body: NavBody,
    node_column: &[i32],
    heights: &[f32],
    walls: &WallPieces,
) -> Vec<f32> {
    let reach = ((body.radius + cell_size) / cell_size).ceil() as i32;
    let mut distance = vec![0.0; heights.len()];
    for node in 0..heights.len() {
        let x = node_column[node] % side;
        let y = node_column[node] / side;
        let x0 = (x - reach).max(0);
        let x1 = (x + reach).min(side - 1);
        let y0 = (y - reach).max(0);
        let y1 = (y + reach).min(side - 1);
        if !walls.any_in(x0, y0, x1, y1) {
            distance[node] = f32::INFINITY;
            continue;
        }
        let centre_x = (x as f32 + 0.5) * cell_size;
        let centre_y = (y as f32 + 0.5) * cell_size;
        let low = heights[node] + body.step_up_height;
        let high = heights[node] + body.height;
        let mut nearest = f32::INFINITY;
        for neighbour_y in y0..=y1 {
            for neighbour_x in x0..=x1 {
                let mut piece = walls.first(neighbour_x, neighbour_y);
                while piece != -1 {
                    let index = piece as usize;
                    if !(walls.high[index] <= low || walls.low[index] >= high) {
                        nearest = nearest.min(walls.distance(piece, centre_x, centre_y));
                    }
                    piece = walls.next[index];
                }
            }
        }
        distance[node] = nearest;
    }
    distance
}

/// Adds the part of a triangle inside each column it crosses as a solid
/// span, and remembers the part of a wall as a wall piece. A column owns its
/// west and south edges and not its east and north ones, so a wall lying on
/// a column boundary fills exactly one column.
#[allow(clippy::too_many_arguments)]
fn rasterize_triangle(
    spans: &mut SpanColumns,
    walls: &mut WallPieces,
    triangle: &NavTriangle,
    origin: Vec3,
    cell_size: f32,
    side: i32,
    mut highest_cell: Option<&mut ColumnTiles<f32>>,
    source: u8,
) {
    let a = triangle.a - origin;
    let b = triangle.b - origin;
    let c = triangle.c - origin;
    let normal = (b - a).cross(c - a);
    let length = normal.length();
    if !(length > 1e-6) {
        return;
    }
    let walkable = is_floor(normal, length);

    let x0 = ((a.x.min(b.x.min(c.x)) / cell_size).floor() as i32).max(0);
    let x1 = ((a.x.max(b.x.max(c.x)) / cell_size).floor() as i32).min(side - 1);
    let y0 = ((a.y.min(b.y.min(c.y)) / cell_size).floor() as i32).max(0);
    let y1 = ((a.y.max(b.y.max(c.y)) / cell_size).floor() as i32).min(side - 1);
    if x0 > x1 || y0 > y1 {
        return;
    }

    let mut polygon = [Vec3::ZERO; CLIP_CAPACITY];
    let mut scratch = [Vec3::ZERO; CLIP_CAPACITY];
    for y in y0..=y1 {
        let bottom = y as f32 * cell_size;
        let top = bottom + cell_size;
        for x in x0..=x1 {
            let left = x as f32 * cell_size;
            let right = left + cell_size;
            polygon[0] = a;
            polygon[1] = b;
            polygon[2] = c;
            let mut count = clip(&polygon, 3, &mut scratch, true, left, true);
            count = clip(&scratch, count, &mut polygon, true, right, false);
            count = clip(&polygon, count, &mut scratch, false, bottom, true);
            count = clip(&scratch, count, &mut polygon, false, top, false);
            if count == 0 {
                continue;
            }
            if walkable && area_xy(&polygon[..count], left, bottom) < MINIMUM_FLOOR_AREA {
                continue;
            }

            let mut low = f32::INFINITY;
            let mut high = f32::NEG_INFINITY;
            for point in &polygon[..count] {
                low = low.min(point.z);
                high = high.max(point.z);
            }
            spans.add(x, y, low, high, walkable, source);
            if !walkable {
                walls.add(x, y, &polygon[..count], low, high);
            }
            if let Some(highest) = highest_cell.as_deref_mut() {
                let previous = highest.get(x, y);
                *highest.slot(x, y) = previous.max(high);
            }
        }
    }
}

/// Adds a cylinder or a dome as solid, with its top as floor a body stands
/// on: the whole of a cylinder's flat top, and the part of a dome's top no
/// steeper than a floor. Its side stays a wall, so a body cannot walk
/// through it lower down.
fn rasterize_cylinder(
    spans: &mut SpanColumns,
    walls: &mut WallPieces,
    cylinder: &NavCylinder,
    origin: Vec3,
    cell_size: f32,
    side: i32,
    source: u8,
) {
    if !has_extent(cylinder) {
        return;
    }
    let centre = cylinder.base - origin;
    let reach = reach_of(cylinder, cell_size);
    let x0 = (((centre.x - reach) / cell_size).floor() as i32).max(0);
    let x1 = (((centre.x + reach) / cell_size).floor() as i32).min(side - 1);
    let y0 = (((centre.y - reach) / cell_size).floor() as i32).max(0);
    let y1 = (((centre.y + reach) / cell_size).floor() as i32).min(side - 1);
    for y in y0..=y1 {
        for x in x0..=x1 {
            let dx = (x as f32 + 0.5) * cell_size - centre.x;
            let dy = (y as f32 + 0.5) * cell_size - centre.y;
            let across = dx * dx + dy * dy;
            if across > reach * reach {
                continue;
            }
            let (over, stands) = top_over(cylinder, across);
            let top = centre.z + over;
            spans.add(x, y, centre.z, top, stands, source);

            let left = (x as f32 * cell_size).max(centre.x - cylinder.radius);
            let right = ((x + 1) as f32 * cell_size).min(centre.x + cylinder.radius);
            let bottom = (y as f32 * cell_size).max(centre.y - cylinder.radius);
            let upper = ((y + 1) as f32 * cell_size).min(centre.y + cylinder.radius);
            if left > right || bottom > upper {
                continue;
            }
            let square = [
                Vec3::new(left, bottom, centre.z),
                Vec3::new(right, bottom, centre.z),
                Vec3::new(right, upper, centre.z),
                Vec3::new(left, upper, centre.z),
            ];
            walls.add(x, y, &square, centre.z, top);
        }
    }
}

/// Fills each column whose centre lies in a landblock with that landblock's
/// terrain where it stands above every cell polygon in the column, as ground
/// over a cellar does; terrain among or under a building's polygons is the
/// building's. The open sea, a landblock every cell of which is under
/// water, is no floor at all, since no body walks into it.
fn rasterize_terrain(
    spans: &mut SpanColumns,
    terrain: &NavTerrain,
    origin: Vec3,
    cell_size: f32,
    side: i32,
    highest_cell: &ColumnTiles<f32>,
) {
    if terrain.surface.is_entirely_water() {
        return;
    }
    let corner_x = terrain.origin_x - origin.x;
    let corner_y = terrain.origin_y - origin.y;
    let x0 = (((corner_x / cell_size) - 0.5).ceil() as i32).max(0);
    let x1 = ((((corner_x + LANDBLOCK_SIZE) / cell_size) - 0.5).ceil() as i32).min(side);
    let y0 = (((corner_y / cell_size) - 0.5).ceil() as i32).max(0);
    let y1 = ((((corner_y + LANDBLOCK_SIZE) / cell_size) - 0.5).ceil() as i32).min(side);
    for y in y0..y1 {
        let local_y = (y as f32 + 0.5) * cell_size - corner_y;
        for x in x0..x1 {
            let surface = terrain
                .surface
                .sample_surface_polygon((x as f32 + 0.5) * cell_size - corner_x, local_y);
            if surface.z < highest_cell.get(x, y) + TERRAIN_ABOVE_CELLS {
                continue;
            }
            spans.add(
                x,
                y,
                surface.z - TERRAIN_THICKNESS,
                surface.z,
                surface.normal.z >= FLOOR_Z,
                FROM_TERRAIN,
            );
        }
    }
}

/// Linear interpolation as OpenAC's vector type computes it.
fn lerp(a: Vec3, b: Vec3, t: f32) -> Vec3 {
    a * (1.0 - t) + b * t
}

/// Clips a polygon to one side of an axis-aligned line; the lower bound is
/// kept, the upper is not.
fn clip(
    input: &[Vec3; CLIP_CAPACITY],
    count: usize,
    output: &mut [Vec3; CLIP_CAPACITY],
    along_x: bool,
    bound: f32,
    keep_above: bool,
) -> usize {
    let mut written = 0;
    let mut index = 0;
    while index < count && written + 2 <= output.len() {
        let current = input[index];
        let next = input[if index + 1 == count { 0 } else { index + 1 }];
        let current_coordinate = if along_x { current.x } else { current.y };
        let next_coordinate = if along_x { next.x } else { next.y };
        let current_side = if keep_above {
            current_coordinate - bound
        } else {
            bound - current_coordinate
        };
        let next_side = if keep_above {
            next_coordinate - bound
        } else {
            bound - next_coordinate
        };
        let current_inside = if keep_above {
            current_side >= 0.0
        } else {
            current_side > 0.0
        };
        let next_inside = if keep_above {
            next_side >= 0.0
        } else {
            next_side > 0.0
        };
        if current_inside {
            output[written] = current;
            written += 1;
        }
        if current_inside != next_inside {
            output[written] = lerp(current, next, current_side / (current_side - next_side));
            written += 1;
        }
        index += 1;
    }
    written
}

fn area_xy(polygon: &[Vec3], left: f32, bottom: f32) -> f32 {
    let mut twice = 0.0;
    for index in 0..polygon.len() {
        let a = polygon[index];
        let b = polygon[if index + 1 == polygon.len() {
            0
        } else {
            index + 1
        }];
        twice += (a.x - left) * (b.y - bottom) - (b.x - left) * (a.y - bottom);
    }
    f32::abs(twice) * 0.5
}

/// Each column's solid spans as a list sorted by height, merged where they
/// overlap.
#[derive(Debug)]
struct SpanColumns {
    head: ColumnTiles<i32>,
    min: Vec<f32>,
    max: Vec<f32>,
    walkable: Vec<bool>,
    /// What each span was rasterized from, as [`FROM_CELL`] and its fellows.
    source: Vec<u8>,
    next: Vec<i32>,
    free: i32,
    live: i32,
}

impl SpanColumns {
    fn new(side: i32) -> Self {
        Self {
            head: ColumnTiles::new(side, -1),
            min: Vec::with_capacity(1 << 16),
            max: Vec::with_capacity(1 << 16),
            walkable: Vec::with_capacity(1 << 16),
            source: Vec::with_capacity(1 << 16),
            next: Vec::with_capacity(1 << 16),
            free: -1,
            live: 0,
        }
    }

    fn add(
        &mut self,
        x: i32,
        y: i32,
        mut min: f32,
        mut max: f32,
        mut walkable: bool,
        mut source: u8,
    ) {
        let head = *self.head.slot(x, y);
        let mut previous = -1;
        let mut current = head;
        while current != -1 && self.max[current as usize] < min {
            previous = current;
            current = self.next[current as usize];
        }
        while current != -1 && self.min[current as usize] <= max {
            let index = current as usize;
            if (self.max[index] - max).abs() <= FLAG_MERGE_HEIGHT {
                walkable |= self.walkable[index];
            } else if self.max[index] > max {
                walkable = self.walkable[index];
            }
            min = min.min(self.min[index]);
            max = max.max(self.max[index]);
            source |= self.source[index];
            let following = self.next[index];
            self.release(current);
            current = following;
        }

        let span = self.allocate();
        let index = span as usize;
        self.min[index] = min;
        self.max[index] = max;
        self.walkable[index] = walkable;
        self.source[index] = source;
        self.next[index] = current;
        if previous == -1 {
            *self.head.slot(x, y) = span;
        } else {
            self.next[previous as usize] = span;
        }
    }

    fn allocate(&mut self) -> i32 {
        self.live += 1;
        if self.free != -1 {
            let reused = self.free;
            self.free = self.next[reused as usize];
            return reused;
        }
        self.min.push(0.0);
        self.max.push(0.0);
        self.walkable.push(false);
        self.source.push(0);
        self.next.push(-1);
        self.min.len() as i32 - 1
    }

    fn release(&mut self, span: i32) {
        self.live -= 1;
        self.next[span as usize] = self.free;
        self.free = span;
    }
}

/// The footprints of walls in each column: the part of each wall polygon
/// inside it, and its height range.
#[derive(Debug)]
struct WallPieces {
    points: Vec<f32>,
    head: ColumnTiles<i32>,
    /// How many wall pieces each tile of columns holds.
    tile_count: Vec<i32>,
    next: Vec<i32>,
    low: Vec<f32>,
    high: Vec<f32>,
    point_start: Vec<i32>,
    point_count: Vec<i32>,
}

impl WallPieces {
    fn new(side: i32) -> Self {
        let head = ColumnTiles::new(side, -1);
        let tiles = (head.tiles_per_side() * head.tiles_per_side()) as usize;
        Self {
            points: Vec::with_capacity(1 << 16),
            head,
            tile_count: vec![0; tiles],
            next: Vec::with_capacity(1 << 14),
            low: Vec::with_capacity(1 << 14),
            high: Vec::with_capacity(1 << 14),
            point_start: Vec::with_capacity(1 << 14),
            point_count: Vec::with_capacity(1 << 14),
        }
    }

    fn count(&self) -> i32 {
        self.next.len() as i32
    }

    /// The first wall piece in a column, or -1.
    fn first(&self, x: i32, y: i32) -> i32 {
        self.head.get(x, y)
    }

    /// Whether any column of a rectangle of columns may hold a wall piece.
    fn any_in(&self, x0: i32, y0: i32, x1: i32, y1: i32) -> bool {
        for tile_y in y0 / TILE_COLUMNS..=y1 / TILE_COLUMNS {
            for tile_x in x0 / TILE_COLUMNS..=x1 / TILE_COLUMNS {
                if self.tile_count[(tile_y * self.head.tiles_per_side() + tile_x) as usize] > 0 {
                    return true;
                }
            }
        }
        false
    }

    fn add(&mut self, x: i32, y: i32, polygon: &[Vec3], low: f32, high: f32) {
        let head = *self.head.slot(x, y);
        let piece = self.next.len() as i32;
        self.next.push(head);
        self.low.push(low);
        self.high.push(high);
        self.point_start.push((self.points.len() / 2) as i32);
        self.point_count.push(polygon.len() as i32);
        for point in polygon {
            self.points.push(point.x);
            self.points.push(point.y);
        }
        *self.head.slot(x, y) = piece;
        let tiles_per_side = self.head.tiles_per_side();
        self.tile_count[((y / TILE_COLUMNS) * tiles_per_side + x / TILE_COLUMNS) as usize] += 1;
    }

    fn point(&self, index: i32) -> Vec2 {
        Vec2::new(
            self.points[(index * 2) as usize],
            self.points[(index * 2 + 1) as usize],
        )
    }

    /// The horizontal distance from a point to a wall piece's footprint;
    /// zero inside it.
    fn distance(&self, piece: i32, x: f32, y: f32) -> f32 {
        let start = self.point_start[piece as usize];
        let count = self.point_count[piece as usize];
        if count >= 3 && self.contains(start, count, x, y) {
            return 0.0;
        }
        let mut nearest = f32::INFINITY;
        for index in 0..count {
            let next = if index + 1 == count { 0 } else { index + 1 };
            let a = self.point(start + index);
            let b = self.point(start + next);
            nearest = nearest.min(segment_distance(x, y, a.x, a.y, b.x, b.y));
        }
        nearest
    }

    /// The horizontal distance between a segment and a wall piece's
    /// footprint, and how far along the segment, from 0 to 1, they come
    /// nearest.
    fn distance_to_segment(&self, piece: i32, start: Vec2, end: Vec2) -> (f32, f32) {
        let first = self.point_start[piece as usize];
        let count = self.point_count[piece as usize];
        if count >= 3 {
            if self.contains(first, count, start.x, start.y) {
                return (0.0, 0.0);
            }
            if self.contains(first, count, end.x, end.y) {
                return (0.0, 1.0);
            }
        }

        let mut nearest = f32::INFINITY;
        let mut nearest_along = 0.0;
        for index in 0..count {
            let next = if index + 1 == count { 0 } else { index + 1 };
            let edge_start = self.point(first + index);
            let edge_end = self.point(first + next);
            if let Some(crossing) = crosses(start, end, edge_start, edge_end) {
                return (0.0, crossing);
            }
            let (from_corner, corner_along) = point_to_segment(edge_start, start, end);
            if from_corner < nearest {
                nearest = from_corner;
                nearest_along = corner_along;
            }
            let from_start = point_to_segment(start, edge_start, edge_end).0;
            if from_start < nearest {
                nearest = from_start;
                nearest_along = 0.0;
            }
            let from_end = point_to_segment(end, edge_start, edge_end).0;
            if from_end < nearest {
                nearest = from_end;
                nearest_along = 1.0;
            }
        }
        (nearest, nearest_along)
    }

    fn contains(&self, start: i32, count: i32, x: f32, y: f32) -> bool {
        let mut positive = false;
        let mut negative = false;
        for index in 0..count {
            let next = if index + 1 == count { 0 } else { index + 1 };
            let a = self.point(start + index);
            let b = self.point(start + next);
            let cross = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x);
            if cross > 1e-6 {
                positive = true;
            } else if cross < -1e-6 {
                negative = true;
            }
            if positive && negative {
                return false;
            }
        }
        positive != negative
    }
}

fn segment_distance(x: f32, y: f32, ax: f32, ay: f32, bx: f32, by: f32) -> f32 {
    let dx = bx - ax;
    let dy = by - ay;
    let length_squared = dx * dx + dy * dy;
    let t = if length_squared > 1e-12 {
        (((x - ax) * dx + (y - ay) * dy) / length_squared).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let px = ax + t * dx - x;
    let py = ay + t * dy - y;
    (px * px + py * py).sqrt()
}

fn point_to_segment(point: Vec2, start: Vec2, end: Vec2) -> (f32, f32) {
    let along = end - start;
    let length_squared = along.length_squared();
    let t = if length_squared > 1e-12 {
        ((point - start).dot(along) / length_squared).clamp(0.0, 1.0)
    } else {
        0.0
    };
    (point.distance(start + along * t), t)
}

/// Where along a segment it crosses an edge, from 0 to 1, or `None`.
fn crosses(start: Vec2, end: Vec2, edge_start: Vec2, edge_end: Vec2) -> Option<f32> {
    let segment = end - start;
    let edge = edge_end - edge_start;
    let denominator = segment.x * edge.y - segment.y * edge.x;
    if denominator.abs() < 1e-9 {
        return None;
    }
    let offset = edge_start - start;
    let along = (offset.x * edge.y - offset.y * edge.x) / denominator;
    let on_edge = (offset.x * segment.y - offset.y * segment.x) / denominator;
    ((0.0..=1.0).contains(&along) && (0.0..=1.0).contains(&on_edge)).then_some(along)
}
