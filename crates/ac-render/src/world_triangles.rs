//! The filled triangles plugins put in the world, for the world pass: see
//! through by their colour's alpha, unlit, drawn from both sides after the
//! rest of the world, so walls and hills hide them but they never hide each
//! other.
//!
//! A plugin may send thousands of triangles, so the draw is bounded here,
//! not by the plugin: a triangle no part of which is within
//! [`DRAW_RANGE_METRES`] of the camera, or which lies wholly outside its
//! view, is skipped, and the triangles of one frame never exceed
//! [`VERTEX_BUDGET`]. When the triangles in range and in view would, the
//! nearest are drawn and the farthest are left out. A triangle with a
//! corner that is not a finite position is dropped.
//!
//! A triangle does not follow the ground: a plugin lifts one meant to lie
//! on it. Each corner is moved [`DEPTH_NUDGE`] of the way toward the camera,
//! so a triangle laid on a surface is drawn over it rather than into it.

use bytemuck::{Pod, Zeroable};
use glam::Vec3;

use crate::frustum::FrustumPlanes;

/// Triangles further than this from the camera are not drawn, metres: the
/// same reach as the plugins' world lines.
pub const DRAW_RANGE_METRES: f32 = crate::world_lines::DRAW_RANGE_METRES;

/// How far of the way toward the camera each corner is moved.
pub const DEPTH_NUDGE: f32 = 0.002;

/// The most vertices one frame's triangles take: 1 MiB of them, some twelve
/// thousand triangles.
pub const VERTEX_BUDGET: usize = 1024 * 1024 / size_of::<WorldTriangleVertex>();

/// One corner: drawing-frame position and colour (0..1, alpha last).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct WorldTriangleVertex {
    /// Drawing frame, metres.
    pub position: [f32; 3],
    /// Red, green, blue, alpha.
    pub color: [f32; 4],
}

/// A plugin's triangle placed in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedTriangle {
    /// The corners, drawing frame.
    pub corners: [Vec3; 3],
    /// Each corner's colour (0..1, alpha last), blended across the face.
    pub colors: [[f32; 4]; 3],
}

/// A triangle that is drawn this frame, and how far it is.
struct Planned<'a> {
    triangle: &'a PlacedTriangle,
    distance_squared: f32,
}

/// Builds this frame's vertices into `out` (cleared first): the triangles
/// with finite corners, some part within [`DRAW_RANGE_METRES`] of `camera`
/// and some part inside `frustum` (when there is one), nearest first once
/// they do not all fit in [`VERTEX_BUDGET`].
#[expect(
    clippy::neg_cmp_op_on_partial_ord,
    reason = "a distance that is not a number must fail the range check"
)]
pub fn build(
    triangles: &[PlacedTriangle],
    camera: Vec3,
    frustum: Option<&FrustumPlanes>,
    out: &mut Vec<WorldTriangleVertex>,
) {
    out.clear();
    let range_squared = DRAW_RANGE_METRES * DRAW_RANGE_METRES;
    let mut planned: Vec<Planned<'_>> = triangles
        .iter()
        .filter(|t| t.corners.iter().all(|c| c.is_finite()))
        .filter_map(|triangle| {
            let [a, b, c] = triangle.corners;
            let distance_squared = camera.distance_squared(closest_point(camera, a, b, c));
            if !(distance_squared <= range_squared) {
                return None;
            }
            if let Some(frustum) = frustum
                && !frustum.is_aabb_visible(a.min(b).min(c), a.max(b).max(c))
            {
                return None;
            }
            Some(Planned {
                triangle,
                distance_squared,
            })
        })
        .collect();
    // Only when the triangles do not all fit is the order worth a sort.
    if planned.len() * 3 > VERTEX_BUDGET {
        planned.sort_by(|a, b| a.distance_squared.total_cmp(&b.distance_squared));
        planned.truncate(VERTEX_BUDGET / 3);
    }
    out.reserve(planned.len() * 3);
    for Planned { triangle, .. } in planned {
        for (corner, color) in triangle.corners.into_iter().zip(triangle.colors) {
            out.push(WorldTriangleVertex {
                position: (corner + (camera - corner) * DEPTH_NUDGE).to_array(),
                color,
            });
        }
    }
}

/// The point of triangle `a`, `b`, `c` nearest `p`: on its face, an edge or
/// a corner.
#[must_use]
#[expect(
    clippy::neg_cmp_op_on_partial_ord,
    reason = "a denominator that is not a number counts as no area"
)]
pub fn closest_point(p: Vec3, a: Vec3, b: Vec3, c: Vec3) -> Vec3 {
    let (ab, ac, ap) = (b - a, c - a, p - a);
    let (d1, d2) = (ab.dot(ap), ac.dot(ap));
    if d1 <= 0.0 && d2 <= 0.0 {
        return a;
    }
    let bp = p - b;
    let (d3, d4) = (ab.dot(bp), ac.dot(bp));
    if d3 >= 0.0 && d4 <= d3 {
        return b;
    }
    let vc = d1 * d4 - d3 * d2;
    if vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0 {
        return a + ab * (d1 / (d1 - d3));
    }
    let cp = p - c;
    let (d5, d6) = (ab.dot(cp), ac.dot(cp));
    if d6 >= 0.0 && d5 <= d6 {
        return c;
    }
    let vb = d5 * d2 - d1 * d6;
    if vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0 {
        return a + ac * (d2 / (d2 - d6));
    }
    let va = d3 * d6 - d5 * d4;
    if va <= 0.0 && d4 - d3 >= 0.0 && d5 - d6 >= 0.0 {
        return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
    }
    let denominator = va + vb + vc;
    // A triangle with no area: its corners are as near as any of it.
    if !(denominator.abs() > 1e-12) {
        return [b, c].into_iter().fold(a, |best, corner| {
            if p.distance_squared(corner) < p.distance_squared(best) {
                corner
            } else {
                best
            }
        });
    }
    a + ab * (vb / denominator) + ac * (vc / denominator)
}

#[cfg(test)]
mod tests;
