//! The lines plugins put in the world, built as solid bars for the world
//! pass, so they sit in the scene with its depth, drawn after the rest of
//! the world.
//!
//! A plugin may send any number of lines of any length, so the draw is
//! bounded here, not by the plugin: only the part of a line within
//! [`DRAW_RANGE_METRES`] of the camera is cut into pieces, and the bars of
//! one frame never exceed [`VERTEX_BUDGET`]. When the lines in range would
//! exceed it, the lines nearest the camera are drawn and the farthest are
//! left out.
//!
//! A bar is a box around the segment, as wide and as tall as the line's
//! width in metres. A line that follows the ground is cut into pieces about
//! [`PIECE_METRES`] long, fitted to what is under them:
//!
//! - When both of its ends stand on floors (the faces of interior cells and
//!   of the objects the landblocks place, such as stairs and bridges), each
//!   piece end stands on the floor under it, stairs and ramps included,
//!   walked piece by piece from the start so a floor overhead is never
//!   taken. When both ends are level with the terrain too, the terrain
//!   counts as a floor. Where no floor is under a piece end, it stays on the
//!   straight line. Unless an end is under the terrain (in a cellar or a
//!   dungeon), no piece end is ever below the terrain: a line between two
//!   porches goes over a hill between them, not through it.
//! - Otherwise, when both ends are on the terrain, each piece end sits on
//!   the terrain under it, down into dips as well as up over rises.
//! - In both, a piece end is as high over the surface as the line's ends are
//!   over theirs (taken evenly along it), and at least
//!   [`GROUND_LIFT_METRES`].
//! - When either end is above the terrain, on a bridge or a ledge, piece
//!   ends are only raised to the terrain, never lowered, so a route over a
//!   bridge keeps its height.
//!
//! The floors are only known in the landblocks around the player (the
//! camera's collision neighbourhood, see
//! [`crate::camera_collision::NEIGHBOURHOOD_RADIUS`]). Past them there are no
//! floors, so a line there follows the terrain as above. Inside a sealed
//! dungeon there is no terrain to follow ([`followed_terrain`]).

use ac_nav::walk::ON_THE_FLOOR;
use bytemuck::{Pod, Zeroable};
use glam::Vec3;

// An end is on the terrain when it is no more than `ON_THE_LAND_METRES`
// above it, as a shape's anchor is (at or below counts). An end stands on a
// floor when it is within `ON_THE_FLOOR` of it, as a route's node is, so a
// route's corners on floors give a line that follows the floors.
use crate::world_shapes::ON_THE_LAND_METRES;

/// Lines further than this from the camera are not drawn, metres.
pub const DRAW_RANGE_METRES: f32 = 250.0;

/// A line that follows the terrain is cut into pieces about this long,
/// metres.
pub const PIECE_METRES: f32 = 0.75;

/// Keeps the piece arithmetic of an absurdly long line in range. Only the
/// pieces within the draw range are drawn, so this never costs vertices; it
/// only makes the pieces of such a line longer.
const MAX_PIECES_PER_LINE: f64 = (1 << 20) as f64;

/// A bar is a box: six faces of two triangles.
pub const VERTICES_PER_BAR: usize = 36;

/// The most vertices one frame's lines take: 4 MiB of them.
pub const VERTEX_BUDGET: usize = 4 * 1024 * 1024 / size_of::<WorldLineVertex>();

/// How far above the ground a terrain-following line is kept, metres.
pub const GROUND_LIFT_METRES: f32 = 0.05;

/// How much higher than the floor under one piece end the floor under the
/// next may be, metres: more than the steepest floor rises over a piece
/// (about 0.85 m: a face is a floor up to about 48 degrees, a piece at most
/// [`PIECE_METRES`] long), much less than a storey. Not a character's step
/// height: that is a property of each creature's model, and a walkable ramp
/// rises more than it in a piece.
pub const FLOOR_STEP_METRES: f32 = 1.0;

/// A line's width when the plugin's is not a number, metres.
pub const DEFAULT_WIDTH_METRES: f32 = 0.25;

/// The narrowest and widest a line is drawn, metres.
pub const WIDTH_RANGE_METRES: (f32, f32) = (0.01, 2.0);

/// One vertex of a bar: drawing-frame position and colour (0..1).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct WorldLineVertex {
    /// Drawing frame, metres.
    pub position: [f32; 3],
    /// Red, green, blue.
    pub color: [f32; 3],
}

/// How a line is drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LineShape {
    /// A solid bar as wide as the line says.
    Bar,
    /// One pixel wide at any distance, however wide the line says it is.
    Hairline,
}

impl LineShape {
    /// The vertices one piece of a line takes.
    const fn vertices(self) -> usize {
        match self {
            Self::Bar => VERTICES_PER_BAR,
            Self::Hairline => 2,
        }
    }
}

pub use ac_ui_window::world::PlacedLine;
/// The pieces of one line that are drawn: the line is cut into `count`
/// equal pieces and pieces `first` up to, not including, `last` are drawn.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LinePieces {
    /// Pieces the whole line is cut into.
    pub count: u32,
    /// The first piece drawn.
    pub first: u32,
    /// One past the last piece drawn.
    pub last: u32,
    /// Squared distance from the camera to the nearest point of the line in
    /// range, square metres.
    pub distance_squared: f32,
}

impl LinePieces {
    /// How many pieces are drawn.
    #[must_use]
    pub fn drawn(&self) -> u32 {
        self.last - self.first
    }
}

/// Where a line meets the draw range around `camera`, and which of its
/// pieces are drawn. A straight line is one piece, drawn whole when any of
/// it is in range. A line that follows the terrain is cut into pieces of
/// about [`PIECE_METRES`] along its whole length, so the pieces stay put
/// as the camera moves, and only the pieces that reach into the range are
/// drawn. `None` when no part of the line is within range.
#[must_use]
pub fn plan_pieces(
    start: Vec3,
    end: Vec3,
    follow_terrain: bool,
    camera: Vec3,
    range: f32,
) -> Option<LinePieces> {
    // Where the line enters and leaves the sphere of the range, as
    // fractions of the way from start to end. Squared in doubles so a wild
    // position cannot overflow.
    let dx = f64::from(end.x - start.x);
    let dy = f64::from(end.y - start.y);
    let dz = f64::from(end.z - start.z);
    let mx = f64::from(start.x - camera.x);
    let my = f64::from(start.y - camera.y);
    let mz = f64::from(start.z - camera.z);
    let a = dx * dx + dy * dy + dz * dz;
    let b = mx * dx + my * dy + mz * dz;
    let c = mx * mx + my * my + mz * mz - f64::from(range) * f64::from(range);
    let (enter, leave) = if a < 1e-12 {
        if c > 0.0 {
            return None;
        }
        (0.0, 1.0)
    } else {
        let discriminant = b * b - a * c;
        if !discriminant.is_finite() || discriminant < 0.0 {
            return None;
        }
        let root = discriminant.sqrt();
        let enter = (-b - root) / a;
        let leave = (-b + root) / a;
        if leave < 0.0 || enter > 1.0 {
            return None;
        }
        (enter.max(0.0), leave.min(1.0))
    };

    let nearest = if a < 1e-12 {
        0.0
    } else {
        (-b / a).max(enter).min(leave)
    };
    let nx = mx + dx * nearest;
    let ny = my + dy * nearest;
    let nz = mz + dz * nearest;
    let distance_squared = (nx * nx + ny * ny + nz * nz) as f32;

    if !follow_terrain {
        return Some(LinePieces {
            count: 1,
            first: 0,
            last: 1,
            distance_squared,
        });
    }
    let ground = (dx * dx + dy * dy).sqrt();
    let count = (ground / f64::from(PIECE_METRES))
        .ceil()
        .clamp(1.0, MAX_PIECES_PER_LINE);
    let first = (enter * count).floor().clamp(0.0, count - 1.0);
    let last = (leave * count).ceil().clamp(first + 1.0, count);
    Some(LinePieces {
        count: count as u32,
        first: first as u32,
        last: last as u32,
        distance_squared,
    })
}

/// A line's drawn thickness: its width clamped to
/// [`WIDTH_RANGE_METRES`], or [`DEFAULT_WIDTH_METRES`] when it is not a
/// number.
#[must_use]
pub fn thickness(width_metres: f32) -> f32 {
    if width_metres.is_finite() {
        width_metres.clamp(WIDTH_RANGE_METRES.0, WIDTH_RANGE_METRES.1)
    } else {
        DEFAULT_WIDTH_METRES
    }
}

/// `0xRRGGBB` as red, green, blue (0..1).
#[must_use]
pub fn color(rgb: u32) -> [f32; 3] {
    [
        ((rgb >> 16) & 255) as f32 / 255.0,
        ((rgb >> 8) & 255) as f32 / 255.0,
        (rgb & 255) as f32 / 255.0,
    ]
}

/// A line planned for this frame.
#[derive(Debug, Clone, Copy)]
struct Planned {
    start: Vec3,
    end: Vec3,
    color: [f32; 3],
    thickness: f32,
    follow_terrain: bool,
    pieces: LinePieces,
}

/// The terrain world lines follow: `terrain`, but none inside a sealed
/// dungeon. Its landblock's terrain is still loaded there, but is never
/// seen, and the dungeon's floors may lie anywhere around it; lines there
/// follow the floors alone.
pub fn followed_terrain(
    in_sealed_dungeon: bool,
    terrain: impl Fn(f32, f32) -> Option<f32>,
) -> impl Fn(f32, f32) -> Option<f32> {
    move |x, y| {
        if in_sealed_dungeon {
            None
        } else {
            terrain(x, y)
        }
    }
}

/// Builds this frame's bars into `out` (cleared first): the lines seen from
/// `camera`, each ground-following piece end fitted to `terrain`, the
/// height of the terrain under a drawing-frame (x, y) where it is known, or
/// to `floor`, the height of the highest floor (of an interior cell or a
/// placed object) under a
/// drawing-frame (x, y) no higher than a height, where there is one.
pub fn build(
    lines: &[PlacedLine],
    camera: Vec3,
    terrain: impl Fn(f32, f32) -> Option<f32>,
    floor: impl Fn(f32, f32, f32) -> Option<f32>,
    out: &mut Vec<WorldLineVertex>,
) {
    build_as(LineShape::Bar, lines, camera, terrain, floor, out);
}

/// [`build`], each piece drawn as `shape`.
pub fn build_as(
    shape: LineShape,
    lines: &[PlacedLine],
    camera: Vec3,
    terrain: impl Fn(f32, f32) -> Option<f32>,
    floor: impl Fn(f32, f32, f32) -> Option<f32>,
    out: &mut Vec<WorldLineVertex>,
) {
    out.clear();
    let mut planned: Vec<Planned> = Vec::new();
    let mut vertices = 0usize;
    for line in lines {
        let (start, end) = (line.start, line.end);
        if !(start.x + start.y + start.z + end.x + end.y + end.z).is_finite() {
            continue;
        }
        let Some(pieces) = plan_pieces(start, end, line.follow_terrain, camera, DRAW_RANGE_METRES)
        else {
            continue;
        };
        planned.push(Planned {
            start,
            end,
            color: color(line.color_rgb),
            thickness: thickness(line.width_metres),
            follow_terrain: line.follow_terrain,
            pieces,
        });
        vertices += pieces.drawn() as usize * shape.vertices();
    }
    // Only when the lines do not all fit is the order worth a sort.
    if vertices > VERTEX_BUDGET {
        planned.sort_by(|a, b| {
            a.pieces
                .distance_squared
                .total_cmp(&b.pieces.distance_squared)
        });
    }
    let mut bars = Bars { out, shape };
    for line in &planned {
        if line.pieces.drawn() as usize * shape.vertices() > bars.remaining() {
            break;
        }
        bars.line(
            line,
            &Under {
                terrain: &terrain,
                floor: &floor,
            },
        );
    }
}

/// The bars of one frame, bounded by [`VERTEX_BUDGET`].
struct Bars<'a> {
    out: &'a mut Vec<WorldLineVertex>,
    shape: LineShape,
}

impl Bars<'_> {
    fn remaining(&self) -> usize {
        VERTEX_BUDGET.saturating_sub(self.out.len())
    }

    fn line(&mut self, line: &Planned, under: &Under<'_>) {
        let pieces = line.pieces;
        let along = line.end - line.start;
        let mut fit = Fit::of(line, under);
        let mut at = |i: u32| {
            let t = (f64::from(i) / f64::from(pieces.count)) as f32;
            let end = if i == 0 {
                Which::Start
            } else if i == pieces.count {
                Which::End
            } else {
                Which::Neither
            };
            fit.place(line.start + along * t, t, end, under)
        };
        let mut previous = at(pieces.first);
        for i in pieces.first + 1..=pieces.last {
            let next = at(i);
            match self.shape {
                LineShape::Bar => self.bar(previous, next, line.color, line.thickness),
                LineShape::Hairline => self.hairline(previous, next, line.color),
            }
            previous = next;
        }
    }

    fn hairline(&mut self, start: Vec3, end: Vec3, color: [f32; 3]) {
        for p in [start, end] {
            self.out.push(WorldLineVertex {
                position: p.to_array(),
                color,
            });
        }
    }

    fn bar(&mut self, start: Vec3, end: Vec3, color: [f32; 3], thickness: f32) {
        let direction = end - start;
        if direction.length_squared() < 0.000_001 {
            return;
        }
        let direction = direction / direction.length();
        let mut side = direction.cross(Vec3::Z);
        if side.length_squared() < 0.000_001 {
            side = Vec3::X;
        }
        let side = side / side.length() * (thickness * 0.5);
        let up = side.cross(direction);
        let up = up / up.length() * (thickness * 0.5);
        let v = [
            start - side - up,
            start + side - up,
            start + side + up,
            start - side + up,
            end - side - up,
            end + side - up,
            end + side + up,
            end - side + up,
        ];
        self.quad(v[0], v[1], v[2], v[3], color);
        self.quad(v[4], v[5], v[6], v[7], color);
        for i in 0..4 {
            let next = (i + 1) % 4;
            self.quad(v[i], v[next], v[next + 4], v[i + 4], color);
        }
    }

    fn quad(&mut self, a: Vec3, b: Vec3, c: Vec3, d: Vec3, color: [f32; 3]) {
        self.triangle(a, b, c, color);
        self.triangle(a, c, d, color);
    }

    fn triangle(&mut self, a: Vec3, b: Vec3, c: Vec3, color: [f32; 3]) {
        if self.remaining() < 3 {
            return;
        }
        for p in [a, b, c] {
            self.out.push(WorldLineVertex {
                position: p.to_array(),
                color,
            });
        }
    }
}

/// What a line that follows the ground can lie on, drawing frame.
struct Under<'a> {
    /// The terrain's height under (x, y), where it is known.
    terrain: &'a dyn Fn(f32, f32) -> Option<f32>,
    /// The highest floor under (x, y) no higher than a height.
    floor: &'a dyn Fn(f32, f32, f32) -> Option<f32>,
}

/// Which end of a line a point is, if either.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Which {
    Start,
    End,
    Neither,
}

/// How a line's piece ends meet what is under them.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Fit {
    /// A straight line: nothing under it is looked at.
    Straight,
    /// Both ends on floors: each piece end stands on the highest floor
    /// under it no higher than [`FLOOR_STEP_METRES`] over the floor under
    /// the piece end before it, the ends on their own floors. Floors are
    /// only known around the player; past them a piece end has none, so it
    /// keeps the line's height (raised to the terrain when
    /// `above_terrain`).
    Floor {
        /// The ends' heights over their floors.
        lift: (f32, f32),
        /// The floors under the ends.
        ends: (f32, f32),
        /// The floor under the last piece end placed.
        last: Option<f32>,
        /// Both ends are on the terrain too, outdoors on paving, a step or
        /// a ground floor: the terrain is a floor, so the line neither
        /// drops into a cellar under it nor leaves the ground where the
        /// floors end.
        terrain: bool,
        /// Neither end is under the terrain (in a cellar or a dungeon): no
        /// piece end is drawn below the terrain, so a line between two
        /// porches goes over a hill between them, not through it. Where the
        /// terrain pokes through a floor, as in a building on a slope or one
        /// whose floor is sunk a little below the terrain under it, the
        /// line is drawn at the terrain there, over the floor.
        above_terrain: bool,
    },
    /// Both ends on the terrain: each piece end sits on the terrain,
    /// lowered into dips as well as raised over rises.
    Hug {
        /// The ends' heights over the terrain.
        lift: (f32, f32),
    },
    /// An end above the terrain: outdoor routes can cross raised geometry
    /// above the heightmap, so the line's height is kept and the terrain
    /// only lifts a buried piece end.
    Raise,
}

impl Fit {
    fn of(line: &Planned, under: &Under<'_>) -> Self {
        let (start, end) = (line.start, line.end);
        if !line.follow_terrain {
            return Self::Straight;
        }
        if let (Some(a), Some(b)) = (on_a_floor(start, under), on_a_floor(end, under)) {
            let level = (
                level_with_terrain(start, under),
                level_with_terrain(end, under),
            );
            let (a, b, terrain) = match level {
                (Some(ta), Some(tb)) => (a.max(ta), b.max(tb), true),
                _ => (a, b, false),
            };
            return Self::Floor {
                lift: (lift(start.z - a), lift(end.z - b)),
                ends: (a, b),
                last: None,
                terrain,
                above_terrain: !under_the_terrain(start, under) && !under_the_terrain(end, under),
            };
        }
        if let (Some(a), Some(b)) = (on_the_terrain(start, under), on_the_terrain(end, under)) {
            return Self::Hug {
                lift: (lift(start.z - a), lift(end.z - b)),
            };
        }
        Self::Raise
    }

    /// The point `t` of the way along the line, placed for this fit. Where
    /// nothing is known under it, it is left on the straight line.
    fn place(&mut self, mut point: Vec3, t: f32, end: Which, under: &Under<'_>) -> Vec3 {
        let along = |(start, end): (f32, f32)| start + (end - start) * t;
        match self {
            Self::Straight => {}
            Self::Floor {
                lift,
                ends,
                last,
                terrain,
                above_terrain,
            } => {
                let lift = along(*lift);
                let ground = (under.terrain)(point.x, point.y);
                let mut surface = match end {
                    Which::Start => Some(ends.0),
                    Which::End => Some(ends.1),
                    Which::Neither => {
                        // The first piece end drawn starts from the line's
                        // own height when the start is out of range.
                        let from = last.unwrap_or(point.z - lift);
                        let floor = (under.floor)(point.x, point.y, from + FLOOR_STEP_METRES);
                        // The terrain, when it counts as a floor, is taken
                        // however steeply it rises: it is not a floor
                        // overhead, and the line must not stay in the hill.
                        let ground = ground.filter(|_| *terrain);
                        match (floor, ground) {
                            (Some(f), Some(g)) => Some(f.max(g)),
                            (f, g) => f.or(g),
                        }
                    }
                };
                if let Some(z) = surface {
                    point.z = z + lift;
                }
                if *above_terrain
                    && let Some(g) = ground
                    && point.z < g + lift
                {
                    point.z = g + lift;
                    surface = Some(g);
                }
                if surface.is_some() {
                    *last = surface;
                }
            }
            Self::Hug { lift } => {
                if let Some(z) = (under.terrain)(point.x, point.y) {
                    point.z = z + along(*lift);
                }
            }
            Self::Raise => {
                if let Some(z) = (under.terrain)(point.x, point.y) {
                    point.z = point.z.max(z + GROUND_LIFT_METRES);
                }
            }
        }
        point
    }
}

/// A height over a surface as a piece end keeps it: at least
/// [`GROUND_LIFT_METRES`].
fn lift(over: f32) -> f32 {
    over.max(GROUND_LIFT_METRES)
}

/// The point a line's end was lifted from: a plugin lifts a line meant to
/// lie on the ground [`GROUND_LIFT_METRES`] off it, as the line is drawn.
/// The tests below look at that point, so an end counts as on a floor or on
/// the terrain when the point it was lifted from does.
fn lifted_from(point: Vec3) -> Vec3 {
    point - Vec3::Z * GROUND_LIFT_METRES
}

/// The floor a line's end stands on: the highest floor no more than
/// [`ON_THE_FLOOR`] over or under the point it was lifted from.
fn on_a_floor(end: Vec3, under: &Under<'_>) -> Option<f32> {
    let point = lifted_from(end);
    (under.floor)(point.x, point.y, point.z + ON_THE_FLOOR).filter(|z| point.z - z <= ON_THE_FLOOR)
}

/// The terrain a line's end is on: the terrain under it, when the point it
/// was lifted from is no more than [`ON_THE_LAND_METRES`] above it, or below
/// it. Not where the terrain is not known.
fn on_the_terrain(end: Vec3, under: &Under<'_>) -> Option<f32> {
    let point = lifted_from(end);
    (under.terrain)(point.x, point.y).filter(|z| point.z <= z + ON_THE_LAND_METRES)
}

/// The terrain under a line's end that is level with it: no more than
/// [`ON_THE_LAND_METRES`] over or under the point it was lifted from (a
/// cellar or a dungeon deep under the terrain is not).
fn level_with_terrain(end: Vec3, under: &Under<'_>) -> Option<f32> {
    let point = lifted_from(end);
    (under.terrain)(point.x, point.y).filter(|z| (point.z - z).abs() <= ON_THE_LAND_METRES)
}

/// Whether a line's end is under the terrain, the point it was lifted from
/// more than [`ON_THE_LAND_METRES`] below it: in a cellar or a dungeon. Not
/// where the terrain is not known.
fn under_the_terrain(end: Vec3, under: &Under<'_>) -> bool {
    let point = lifted_from(end);
    (under.terrain)(point.x, point.y).is_some_and(|z| point.z < z - ON_THE_LAND_METRES)
}

#[cfg(test)]
mod tests;
