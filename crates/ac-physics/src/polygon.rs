//! Collision polygons and the per-polygon tests the BSP walks run.
//!
//! A [`ResolvedPolygon`] is a dat polygon with its vertex positions looked
//! up and its plane computed once, at load time.

use std::collections::HashMap;

use glam::Vec3;

use crate::geometry::{Plane, Sphere};
use crate::globals::EPSILON;

/// A collision polygon: its vertices in order and its plane.
#[derive(Debug, Clone, PartialEq)]
pub struct ResolvedPolygon {
    /// Polygon id within its model or cell structure.
    pub id: u16,
    /// Vertices in winding order (at least three).
    pub vertices: Vec<Vec3>,
    /// The plane through the vertices; the normal follows the winding.
    pub plane: Plane,
    /// Which faces are drawn (kept for diagnostics; collision ignores it).
    pub sides_type: i32,
}

impl ResolvedPolygon {
    /// Resolve a polygon from its vertex ids and the vertex positions.
    ///
    /// `None` (the polygon takes no part in collision) when it has fewer
    /// than three vertices, names a vertex that does not exist, or is
    /// degenerate (its fan cross products sum to almost nothing).
    ///
    /// The normal is the normalised sum of the fan cross products; the
    /// offset is minus the mean of `normal · vertex`.
    #[must_use]
    pub fn resolve(
        id: u16,
        vertex_ids: impl IntoIterator<Item = u16>,
        vertex_positions: &HashMap<u16, Vec3>,
        sides_type: i32,
    ) -> Option<Self> {
        let mut vertices = Vec::new();
        for vid in vertex_ids {
            vertices.push(*vertex_positions.get(&vid)?);
        }
        let n = vertices.len();
        if n < 3 {
            return None;
        }
        let mut normal = Vec3::ZERO;
        for i in 1..n - 1 {
            normal += (vertices[i] - vertices[0]).cross(vertices[i + 1] - vertices[0]);
        }
        let len = normal.length();
        if len < 1e-8 {
            return None;
        }
        normal /= len;
        let dot_sum: f32 = vertices.iter().map(|v| normal.dot(*v)).sum();
        let d = -(dot_sum / n as f32);
        Some(Self {
            id,
            vertices,
            plane: Plane::new(normal, d),
            sides_type,
        })
    }

    /// A polygon from vertices and a plane given directly (tests and
    /// prepared data).
    #[must_use]
    pub fn new(id: u16, vertices: Vec<Vec3>, plane: Plane) -> Self {
        Self {
            id,
            vertices,
            plane,
            sides_type: 0,
        }
    }
}

/// Whether a sphere touches a polygon, exactly: the sphere (shrunk by
/// [`EPSILON`]) must reach the plane, and the centre projected onto the
/// plane must lie inside the polygon or within reach of an edge or corner.
/// `contact` receives the projected centre once the plane test passes.
///
/// No vertices counts as a hit.
pub fn polygon_hits_sphere_precise(
    plane: &Plane,
    verts: &[Vec3],
    sphere_center: Vec3,
    sphere_radius: f32,
    contact: &mut Vec3,
) -> bool {
    let n = verts.len();
    if n == 0 {
        return true;
    }
    let dist = plane.normal.dot(sphere_center) + plane.d;
    let rad = sphere_radius - EPSILON;
    if dist.abs() > rad {
        return false;
    }
    let diff = rad * rad - dist * dist;
    *contact = sphere_center - plane.normal * dist;

    let mut prev = n - 1;
    for i in 0..n {
        let v = verts[i];
        let lv = verts[prev];
        prev = i;
        let edge = v - lv;
        let disp = *contact - lv;
        let cross = plane.normal.cross(edge);
        if disp.dot(cross) >= 0.0 {
            continue;
        }
        // The projected centre is outside this edge: decide by the edges
        // and corners within reach.
        let mut prev = n - 1;
        for j in 0..n {
            let v = verts[j];
            let lv = verts[prev];
            prev = j;
            let edge = v - lv;
            let disp = *contact - lv;
            let cross = plane.normal.cross(edge);
            let disp_dot = disp.dot(cross);
            if disp_dot < 0.0 {
                if cross.length_squared() * diff < disp_dot * disp_dot {
                    return false;
                }
                let disp_edge = disp.dot(edge);
                if disp_edge >= 0.0 && disp_edge <= edge.length_squared() {
                    return true;
                }
            }
            if disp.length_squared() <= diff {
                return true;
            }
        }
        return false;
    }
    true
}

/// A touch that counts for a moving sphere: the polygon is touched and the
/// sphere moves into its front (`movement · normal < 0`). The polygon is
/// recorded in `hit_poly` whenever it is touched, before the direction test,
/// so a sphere touching a face it moves away from still leaves a record
/// (the caller's "near miss").
pub(crate) fn pos_hits_sphere<'a>(
    poly: &'a ResolvedPolygon,
    sphere: &Sphere,
    movement: Vec3,
    contact: &mut Vec3,
    hit_poly: &mut Option<&'a ResolvedPolygon>,
) -> bool {
    let hit = polygon_hits_sphere_precise(
        &poly.plane,
        &poly.vertices,
        sphere.center,
        sphere.radius,
        contact,
    );
    if hit {
        *hit_poly = Some(poly);
    }
    if movement.dot(poly.plane.normal) >= 0.0 {
        return false;
    }
    hit
}

/// Whether the sphere touches the polygon, regardless of movement.
pub(crate) fn hits_sphere(poly: &ResolvedPolygon, sphere: &Sphere) -> bool {
    let mut contact = Vec3::ZERO;
    polygon_hits_sphere_precise(
        &poly.plane,
        &poly.vertices,
        sphere.center,
        sphere.radius,
        &mut contact,
    )
}

/// Whether the polygon is walkable (its normal is more than
/// `walkable_allowance` up) and the sphere touches it.
pub(crate) fn walkable_hits_sphere(
    poly: &ResolvedPolygon,
    walkable_allowance: f32,
    sphere: &Sphere,
    up: Vec3,
) -> bool {
    let dp = up.dot(poly.plane.normal);
    if dp <= walkable_allowance {
        return false;
    }
    hits_sphere(poly, sphere)
}

/// Whether the sphere stands on the polygon: its centre dropped along `up`
/// onto the plane lies inside, or within the sphere's radius (half of it
/// when `small`) of an edge or corner.
pub(crate) fn check_walkable(
    poly: &ResolvedPolygon,
    sphere: &Sphere,
    up: Vec3,
    small: bool,
) -> bool {
    let support_radius = if small {
        sphere.radius * 0.5
    } else {
        sphere.radius
    };
    check_walkable_support(
        &poly.plane,
        &poly.vertices,
        sphere.center,
        support_radius,
        up,
    )
}

/// The support test behind [`check_walkable`], on a plane and vertices
/// directly (the walkable polygon a moving body remembers is tested this
/// way).
#[must_use]
pub fn check_walkable_support(
    plane: &Plane,
    vertices: &[Vec3],
    center: Vec3,
    support_radius: f32,
    up: Vec3,
) -> bool {
    let angle_up = plane.normal.dot(up);
    if angle_up < EPSILON {
        return false;
    }
    let angle = (plane.normal.dot(center) + plane.d) / angle_up;
    let center = center - up * angle;
    let radsum = support_radius * support_radius;

    let n = vertices.len();
    let mut prev = n.wrapping_sub(1);
    for i in 0..n {
        let v = vertices[i];
        let lv = vertices[prev];
        prev = i;
        let edge = v - lv;
        let disp = center - lv;
        let cross = plane.normal.cross(edge);
        let diff = disp.dot(cross);
        if diff < 0.0 {
            if cross.length_squared() * radsum < diff * diff {
                return false;
            }
            let disp_edge = disp.dot(edge);
            return disp_edge >= 0.0 && disp_edge <= edge.length_squared();
        }
        if disp.length_squared() <= radsum {
            return true;
        }
    }
    true
}

/// Moves the sphere back along `movement` until it rests tangent to the
/// polygon's plane, shrinking `walk_interp` (the unused fraction of the
/// step) in proportion. Refused when the movement is parallel to the plane
/// or the adjustment would not shorten the step or would overshoot it by
/// more than half.
pub(crate) fn adjust_sphere_to_plane(
    poly: &ResolvedPolygon,
    walk_interp: &mut f32,
    valid_pos: &mut Sphere,
    movement: Vec3,
) -> bool {
    let dp_pos = valid_pos.center.dot(poly.plane.normal) + poly.plane.d;
    let dp_move = movement.dot(poly.plane.normal);
    let dist = if dp_move <= EPSILON {
        if dp_move >= -EPSILON {
            return false;
        }
        dp_pos - valid_pos.radius
    } else {
        -valid_pos.radius - dp_pos
    };
    let i_dist = dist / dp_move;
    let interp = (1.0 - i_dist) * *walk_interp;
    if interp >= *walk_interp || interp < -0.5 {
        return false;
    }
    valid_pos.center -= movement * i_dist;
    *walk_interp = interp;
    true
}

/// The unit perpendicular of the first polygon edge the centre (dropped
/// along `up` onto the plane) lies outside of; `None` when it is inside
/// every edge or the plane is edge-on to `up`.
#[must_use]
pub fn find_crossed_edge(
    plane: &Plane,
    verts: &[Vec3],
    sphere_center: Vec3,
    up: Vec3,
) -> Option<Vec3> {
    let angle_up = plane.normal.dot(up);
    if angle_up.abs() < EPSILON {
        return None;
    }
    let angle = (plane.normal.dot(sphere_center) + plane.d) / angle_up;
    let center = sphere_center - up * angle;
    let n = verts.len();
    let mut prev = n.wrapping_sub(1);
    for i in 0..n {
        let v = verts[i];
        let lv = verts[prev];
        prev = i;
        let edge = v - lv;
        let disp = center - lv;
        let cross = plane.normal.cross(edge);
        if disp.dot(cross) < 0.0 {
            let len = cross.length();
            return Some(if len > 0.0 {
                cross * (1.0 / len)
            } else {
                Vec3::ZERO
            });
        }
    }
    None
}

/// Pushes `sphere` (and `other` by the same amount) out of a polygon along
/// its normal, during placement.
///
/// With `clear_cell` (solid space counts), the target distance from the
/// plane is the radius, negated once when the centre is in solid space and
/// once more when the centre is on or behind the plane; otherwise the
/// target is the radius in front.
pub(crate) fn adjust_to_placement_poly(
    poly: &ResolvedPolygon,
    sphere: &mut Sphere,
    other: &mut Sphere,
    radius: f32,
    center_solid: bool,
    clear_cell: bool,
) {
    let dist = sphere.center.dot(poly.plane.normal) + poly.plane.d;
    let push = if clear_cell {
        let mut target = radius;
        if center_solid {
            target *= -1.0;
        }
        if dist <= 0.0 {
            target = -target;
        }
        target - dist
    } else {
        radius - dist
    };
    let offset = poly.plane.normal * push;
    sphere.center += offset;
    other.center += offset;
}

/// Fraction of `movement` (from `cur_pos`) at which a sphere of
/// `check_pos.radius` comes to rest against the polygon's plane: 1 when it
/// already touches at `cur_pos`, 0 when moving parallel.
pub(crate) fn adjust_sphere_to_poly(
    poly: &ResolvedPolygon,
    check_pos: &Sphere,
    cur_pos: Vec3,
    movement: Vec3,
) -> f32 {
    let dp_pos = cur_pos.dot(poly.plane.normal) + poly.plane.d;
    if dp_pos.abs() < check_pos.radius {
        return 1.0;
    }
    let dp_move = movement.dot(poly.plane.normal);
    if dp_move.abs() <= EPSILON {
        return 0.0;
    }
    let r = if dp_pos < 0.0 {
        -check_pos.radius
    } else {
        check_pos.radius
    };
    (r - dp_pos) / dp_move
}

#[cfg(test)]
mod tests {
    use super::*;

    fn square(z: f32) -> ResolvedPolygon {
        ResolvedPolygon::new(
            0,
            vec![
                Vec3::new(0.0, 0.0, z),
                Vec3::new(1.0, 0.0, z),
                Vec3::new(1.0, 1.0, z),
                Vec3::new(0.0, 1.0, z),
            ],
            Plane::new(Vec3::Z, -z),
        )
    }

    #[test]
    fn resolve_computes_the_fan_plane() {
        let positions: HashMap<u16, Vec3> = [
            (0, Vec3::new(0.0, 0.0, 2.0)),
            (1, Vec3::new(1.0, 0.0, 2.0)),
            (2, Vec3::new(1.0, 1.0, 2.0)),
            (3, Vec3::new(0.0, 1.0, 2.0)),
        ]
        .into_iter()
        .collect();
        let p = ResolvedPolygon::resolve(7, [0, 1, 2, 3], &positions, 1).unwrap();
        assert_eq!(p.id, 7);
        assert_eq!(p.plane, Plane::new(Vec3::Z, -2.0));
        // Too few vertices, a missing vertex, or a degenerate fan.
        assert!(ResolvedPolygon::resolve(0, [0, 1], &positions, 1).is_none());
        assert!(ResolvedPolygon::resolve(0, [0, 1, 9], &positions, 1).is_none());
        assert!(ResolvedPolygon::resolve(0, [0, 0, 0], &positions, 1).is_none());
    }

    #[test]
    fn precise_hit_inside_and_outside() {
        let p = square(0.0);
        let mut c = Vec3::ZERO;
        assert!(polygon_hits_sphere_precise(
            &p.plane,
            &p.vertices,
            Vec3::new(0.5, 0.5, 0.3),
            0.5,
            &mut c
        ));
        assert_eq!(c, Vec3::new(0.5, 0.5, 0.0));
        // Beside the square, within reach of its edge.
        assert!(polygon_hits_sphere_precise(
            &p.plane,
            &p.vertices,
            Vec3::new(1.3, 0.5, 0.0),
            0.5,
            &mut c
        ));
        // Beside the square, out of reach.
        assert!(!polygon_hits_sphere_precise(
            &p.plane,
            &p.vertices,
            Vec3::new(1.6, 0.5, 0.0),
            0.5,
            &mut c
        ));
        // Exactly the radius away is not a touch (the radius shrinks by EPSILON).
        assert!(!polygon_hits_sphere_precise(
            &p.plane,
            &p.vertices,
            Vec3::new(0.5, 0.5, 0.5),
            0.5,
            &mut c
        ));
    }

    #[test]
    fn pos_hits_records_the_polygon_before_the_direction_test() {
        let p = square(0.0);
        let s = Sphere::new(Vec3::new(0.5, 0.5, 0.3), 0.5);
        let mut c = Vec3::ZERO;
        let mut hit = None;
        // Moving up, away from the front: not a hit, but recorded.
        assert!(!pos_hits_sphere(&p, &s, Vec3::Z, &mut c, &mut hit));
        assert_eq!(hit.map(|h| h.id), Some(0));
        let mut hit = None;
        assert!(pos_hits_sphere(&p, &s, -Vec3::Z, &mut c, &mut hit));
        assert!(hit.is_some());
    }

    #[test]
    fn adjust_sphere_to_plane_rests_tangent() {
        let p = square(0.0);
        let mut s = Sphere::new(Vec3::new(0.5, 0.5, 0.4), 0.48);
        let mut wi = 1.0;
        assert!(adjust_sphere_to_plane(
            &p,
            &mut wi,
            &mut s,
            Vec3::new(0.0, 0.0, -0.5)
        ));
        assert!((s.center.z - 0.48).abs() < 1e-6, "z = {}", s.center.z);
        assert!(wi < 1.0);
        // Parallel movement is refused.
        let mut s = Sphere::new(Vec3::new(0.5, 0.5, 0.4), 0.48);
        assert!(!adjust_sphere_to_plane(&p, &mut wi, &mut s, Vec3::X));
    }

    #[test]
    fn crossed_edge_and_walkable_support() {
        let p = square(0.0);
        assert!(
            find_crossed_edge(&p.plane, &p.vertices, Vec3::new(0.5, 0.5, 1.0), Vec3::Z).is_none()
        );
        let n =
            find_crossed_edge(&p.plane, &p.vertices, Vec3::new(2.0, 0.5, 1.0), Vec3::Z).unwrap();
        assert!((n.length() - 1.0).abs() < 1e-6);
        assert!(check_walkable_support(
            &p.plane,
            &p.vertices,
            Vec3::new(0.5, 0.5, 3.0),
            0.24,
            Vec3::Z
        ));
        assert!(check_walkable_support(
            &p.plane,
            &p.vertices,
            Vec3::new(1.2, 0.5, 3.0),
            0.24,
            Vec3::Z
        ));
        assert!(!check_walkable_support(
            &p.plane,
            &p.vertices,
            Vec3::new(1.3, 0.5, 3.0),
            0.24,
            Vec3::Z
        ));
        // A wall (normal across `up`) never supports.
        let wall = Plane::new(Vec3::X, 0.0);
        assert!(!check_walkable_support(
            &wall,
            &p.vertices,
            Vec3::ZERO,
            1.0,
            Vec3::Z
        ));
    }

    #[test]
    fn placement_push_follows_the_signs() {
        let p = square(0.0);
        let push = |z: f32, center_solid: bool, clear_cell: bool| {
            let mut a = Sphere::new(Vec3::new(0.5, 0.5, z), 0.5);
            let mut b = Sphere::new(Vec3::new(0.5, 0.5, z + 1.0), 0.5);
            adjust_to_placement_poly(&p, &mut a, &mut b, 0.5, center_solid, clear_cell);
            assert!(
                (b.center.z - a.center.z - 1.0).abs() < 1e-6,
                "both spheres move together"
            );
            a.center.z
        };
        // In front: to the radius in front.
        assert!((push(0.2, false, true) - 0.5).abs() < 1e-6);
        // Behind, not solid: to the radius behind.
        assert!((push(-0.2, false, true) + 0.5).abs() < 1e-6);
        // In front but solid: behind.
        assert!((push(0.2, true, true) + 0.5).abs() < 1e-6);
        // Behind and solid: the two negations cancel, so in front.
        assert!((push(-0.2, true, true) - 0.5).abs() < 1e-6);
        // Solid space ignored: always in front.
        assert!((push(-0.2, true, false) - 0.5).abs() < 1e-6);
    }

    #[test]
    fn adjust_sphere_to_poly_times() {
        let p = square(0.0);
        let s = Sphere::new(Vec3::ZERO, 0.5);
        assert_eq!(
            adjust_sphere_to_poly(&p, &s, Vec3::new(0.5, 0.5, 0.3), -Vec3::Z),
            1.0
        );
        assert_eq!(
            adjust_sphere_to_poly(&p, &s, Vec3::new(0.5, 0.5, 2.0), Vec3::X),
            0.0
        );
        let t = adjust_sphere_to_poly(&p, &s, Vec3::new(0.5, 0.5, 2.0), Vec3::new(0.0, 0.0, -2.0));
        assert!((t - 0.75).abs() < 1e-6);
    }
}
