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
//! width in metres. A line that follows the terrain is cut into pieces
//! about [`PIECE_METRES`] long, each end raised to just above the ground
//! under it (never lowered: a route over a bridge keeps its height).

use bytemuck::{Pod, Zeroable};
use glam::Vec3;

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

/// A plugin's line placed in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedLine {
    /// One end, drawing frame.
    pub start: Vec3,
    /// The other end, drawing frame.
    pub end: Vec3,
    /// `0xRRGGBB`.
    pub color_rgb: u32,
    /// Kept just above the ground along its length.
    pub follow_terrain: bool,
    /// Thickness, metres.
    pub width_metres: f32,
}

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

/// Builds this frame's bars into `out` (cleared first): the lines seen from
/// `camera`, each terrain-following end raised by `ground`, the height of
/// the ground under a drawing-frame point where it is known.
pub fn build(
    lines: &[PlacedLine],
    camera: Vec3,
    ground: impl Fn(f32, f32) -> Option<f32>,
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
        vertices += pieces.drawn() as usize * VERTICES_PER_BAR;
    }
    // Only when the lines do not all fit is the order worth a sort.
    if vertices > VERTEX_BUDGET {
        planned.sort_by(|a, b| {
            a.pieces
                .distance_squared
                .total_cmp(&b.pieces.distance_squared)
        });
    }
    let mut bars = Bars { out };
    for line in &planned {
        if line.pieces.drawn() as usize * VERTICES_PER_BAR > bars.remaining() {
            break;
        }
        bars.line(line, &ground);
    }
}

/// The bars of one frame, bounded by [`VERTEX_BUDGET`].
struct Bars<'a> {
    out: &'a mut Vec<WorldLineVertex>,
}

impl Bars<'_> {
    fn remaining(&self) -> usize {
        VERTEX_BUDGET.saturating_sub(self.out.len())
    }

    fn line(&mut self, line: &Planned, ground: &impl Fn(f32, f32) -> Option<f32>) {
        let pieces = line.pieces;
        let along = line.end - line.start;
        let at = |i: u32| {
            let t = (f64::from(i) / f64::from(pieces.count)) as f32;
            raise(line.start + along * t, line.follow_terrain, ground)
        };
        let mut previous = at(pieces.first);
        for i in pieces.first + 1..=pieces.last {
            let next = at(i);
            self.bar(previous, next, line.color, line.thickness);
            previous = next;
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

/// A terrain-following point raised to just above the ground under it.
/// Outdoor routes can cross raised geometry above the heightmap, so the
/// recorded height is kept; the ground only lifts a buried point.
fn raise(mut point: Vec3, follow: bool, ground: &impl Fn(f32, f32) -> Option<f32>) -> Vec3 {
    if follow && let Some(z) = ground(point.x, point.y) {
        point.z = point.z.max(z + GROUND_LIFT_METRES);
    }
    point
}

#[cfg(test)]
mod tests;
