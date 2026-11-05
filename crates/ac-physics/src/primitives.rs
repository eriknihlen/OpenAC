//! Sphere, ray and plane tests against single polygons and spheres.
//!
//! These are the stand-alone geometric tests; the BSP walks use the
//! polygon tests in [`crate::polygon`], which have their own tolerances.

use glam::Vec3;

use crate::geometry::Plane;

/// Tolerance of these tests.
pub const EPSILON: f32 = 1e-4;

/// Squared-length tolerance of these tests.
pub const EPSILON_SQ: f32 = 1e-8;

/// Where a ray from `ray_origin` along `ray_dir` first enters the sphere,
/// as a multiple of `ray_dir`. `None` when the origin is inside the sphere
/// (or on it), the ray is degenerate, or it misses.
#[must_use]
pub fn sphere_intersects_ray(
    sphere_center: Vec3,
    sphere_radius: f32,
    ray_origin: Vec3,
    ray_dir: Vec3,
) -> Option<f64> {
    let delta = ray_origin - sphere_center;
    // Positive when the origin is outside the sphere.
    let c =
        delta.x * delta.x + delta.y * delta.y + delta.z * delta.z - sphere_radius * sphere_radius;
    if c <= 0.0 {
        return None;
    }
    let a = ray_dir.x * ray_dir.x + ray_dir.y * ray_dir.y + ray_dir.z * ray_dir.z;
    if a < EPSILON_SQ {
        return None;
    }
    let b = -(delta.x * ray_dir.x + delta.y * ray_dir.y + delta.z * ray_dir.z);
    let disc = b * b - c * a;
    if disc < 0.0 {
        return None;
    }
    let sqrt_disc = disc.sqrt();
    let t = if b < sqrt_disc {
        (b + sqrt_disc) / a
    } else {
        (b - sqrt_disc) / a
    };
    Some(f64::from(t))
}

/// Where a ray meets a plane, as a multiple of `ray_dir`; `None` when the
/// ray is (nearly) parallel or the plane is behind the origin.
#[must_use]
pub fn ray_plane_intersect(plane: Plane, ray_origin: Vec3, ray_dir: Vec3) -> Option<f64> {
    let denom = ray_dir.dot(plane.normal);
    if denom.abs() < EPSILON {
        return None;
    }
    let num = -(ray_origin.dot(plane.normal) + plane.d);
    let t = num / denom;
    (t >= 0.0).then_some(f64::from(t))
}

/// The unit normal of a polygon (fan cross products summed) and its plane
/// offset (minus the mean of `normal · vertex`). Zero normal and offset for
/// fewer than three vertices or a degenerate polygon.
#[must_use]
pub fn calc_normal(vertices: &[Vec3]) -> (Vec3, f32) {
    let n = vertices.len();
    if n < 3 {
        return (Vec3::ZERO, 0.0);
    }
    let (mut acc_x, mut acc_y, mut acc_z) = (0.0f32, 0.0f32, 0.0f32);
    let v0 = vertices[0];
    for i in 1..n - 1 {
        let vi = vertices[i];
        let vi1 = vertices[i + 1];
        let (ax, ay, az) = (vi.x - v0.x, vi.y - v0.y, vi.z - v0.z);
        let (bx, by, bz) = (vi1.x - v0.x, vi1.y - v0.y, vi1.z - v0.z);
        acc_x += ay * bz - az * by;
        acc_y += az * bx - ax * bz;
        acc_z += ax * by - ay * bx;
    }
    let len = (acc_x * acc_x + acc_y * acc_y + acc_z * acc_z).sqrt();
    if len < EPSILON_SQ {
        return (Vec3::ZERO, 0.0);
    }
    let inv_len = 1.0 / len;
    let normal = Vec3::new(acc_x * inv_len, acc_y * inv_len, acc_z * inv_len);
    let dot_sum: f32 = vertices.iter().map(|v| normal.dot(*v)).sum();
    (normal, -(dot_sum / n as f32))
}

/// The edge-perpendicular of `edge` in the plane with `normal` (the
/// component form both edge loops below use).
fn edge_perp(edge: Vec3, normal: Vec3) -> Vec3 {
    Vec3::new(
        edge.z * normal.y - edge.y * normal.z,
        edge.x * normal.z - edge.z * normal.x,
        edge.y * normal.x - edge.x * normal.y,
    )
}

/// Whether a sphere touches a polygon; on a hit, the centre projected onto
/// the polygon's plane.
#[must_use]
pub fn sphere_intersects_poly(
    poly_plane: Plane,
    vertices: &[Vec3],
    sphere_center: Vec3,
    sphere_radius: f32,
) -> Option<Vec3> {
    let dist = poly_plane.normal.dot(sphere_center) + poly_plane.d;
    let rad = sphere_radius - EPSILON;
    if dist.abs() > rad {
        return None;
    }
    let contact = sphere_center - poly_plane.normal * dist;
    // Slack left for the edge tests, squared.
    let rad_sq = rad * rad - dist * dist;
    let num_verts = vertices.len();
    if num_verts == 0 {
        return Some(contact);
    }
    let mut inside = true;
    let mut prev = num_verts - 1;
    for i in 0..num_verts {
        let v0 = vertices[prev];
        let v1 = vertices[i];
        prev = i;
        let edge = v1 - v0;
        let disp = contact - v0;
        let perp = edge_perp(edge, poly_plane.normal);
        let dp = disp.dot(perp);
        if dp < 0.0 {
            if perp.length_squared() * rad_sq < dp * dp {
                return None;
            }
            let disp_edge = disp.dot(edge);
            if disp_edge >= 0.0 && disp_edge < edge.length_squared() {
                return Some(contact);
            }
            inside = false;
        }
        if disp.length_squared() <= rad_sq {
            return Some(contact);
        }
    }
    inside.then_some(contact)
}

/// Time (a multiple of `ray_dir`, signed) at which a sphere moving along
/// `ray_dir` reaches the polygon's plane, when the point it reaches lies
/// within the polygon (or within the radius of an edge or corner).
#[must_use]
pub fn find_time_of_collision(
    poly_plane: Plane,
    vertices: &[Vec3],
    sphere_origin: Vec3,
    sphere_radius: f32,
    ray_dir: Vec3,
) -> Option<f32> {
    let denom = ray_dir.dot(poly_plane.normal);
    if denom.abs() < EPSILON {
        return None;
    }
    let num = sphere_origin.dot(poly_plane.normal) + poly_plane.d;
    let t = num / denom;
    let contact = sphere_origin - ray_dir * t;
    let rad_sq = sphere_radius * sphere_radius;
    let num_verts = vertices.len();
    let mut inside = true;
    let mut prev = num_verts.wrapping_sub(1);
    for i in 0..num_verts {
        let v0 = vertices[prev];
        let v1 = vertices[i];
        prev = i;
        let edge = v1 - v0;
        let disp = contact - v0;
        let perp = edge_perp(edge, poly_plane.normal);
        let dp = disp.dot(perp);
        if dp < 0.0 {
            if perp.length_squared() * rad_sq < dp * dp {
                return None;
            }
            let disp_edge = disp.dot(edge);
            if disp_edge >= 0.0 && disp_edge < edge.length_squared() {
                return Some(t);
            }
            inside = false;
        }
        if disp.length_squared() < rad_sq {
            return Some(t);
        }
    }
    inside.then_some(t)
}

/// Whether a sphere moving along `movement_dir` (with the polygon's normal,
/// i.e. from below a floor) touches the polygon.
#[must_use]
pub fn hits_walkable(
    poly_plane: Plane,
    vertices: &[Vec3],
    sphere_center: Vec3,
    sphere_radius: f32,
    movement_dir: Vec3,
) -> bool {
    if poly_plane.normal.dot(movement_dir) < 0.0 {
        return false;
    }
    sphere_intersects_poly(poly_plane, vertices, sphere_center, sphere_radius).is_some()
}

/// Where a sphere moving along `movement_dir` meets the polygon's plane
/// outside one of its edges: the unit perpendicular of the first such edge.
#[must_use]
pub fn find_walkable_collision(
    poly_plane: Plane,
    vertices: &[Vec3],
    sphere_origin: Vec3,
    movement_dir: Vec3,
) -> Option<Vec3> {
    let denom = poly_plane.normal.dot(movement_dir);
    if denom.abs() < EPSILON {
        return None;
    }
    let num_verts = vertices.len();
    let t = (poly_plane.normal.dot(sphere_origin) + poly_plane.d) / denom;
    let contact = sphere_origin - movement_dir * t;
    let mut prev = num_verts.wrapping_sub(1);
    for i in 0..num_verts {
        let v0 = vertices[prev];
        let v1 = vertices[i];
        prev = i;
        let edge = v1 - v0;
        let disp = contact - v0;
        let perp = edge_perp(edge, poly_plane.normal);
        let dp = disp.x * perp.x + disp.y * perp.y + disp.z * perp.z;
        if dp < 0.0 {
            let len = perp.length();
            if len < EPSILON_SQ {
                return None;
            }
            return Some(perp / len);
        }
    }
    None
}

/// How far (a multiple of `movement_dir`) a sphere moves before it rests
/// against `plane` from the side it is on. `f32::MAX` when it already
/// touches the plane; zero when it moves parallel to it.
#[must_use]
pub fn slide_sphere(
    plane: Plane,
    sphere_radius: f32,
    sphere_center: Vec3,
    movement_dir: Vec3,
) -> f32 {
    let dist = sphere_center.dot(plane.normal) + plane.d;
    if dist.abs() < sphere_radius {
        return f32::MAX;
    }
    let denom = movement_dir.dot(plane.normal);
    if denom.abs() < EPSILON {
        return 0.0;
    }
    let offset = if dist <= 0.0 {
        -sphere_radius
    } else {
        sphere_radius
    };
    (offset - dist) / denom
}

/// When (as a fraction of `sweep_delta`, in `(0, 1]`) a sphere sweeping
/// from `mover_center` first touches a still sphere. `None` for a
/// degenerate sweep, spheres already overlapping, or a miss.
#[must_use]
pub fn swept_sphere_hits_sphere(
    mover_center: Vec3,
    mover_radius: f32,
    sweep_delta: Vec3,
    target_center: Vec3,
    target_radius: f32,
) -> Option<f32> {
    let rad_sum = mover_radius + target_radius;
    let (mx, my, mz) = (sweep_delta.x, sweep_delta.y, sweep_delta.z);
    let dist_sq = mx * mx + my * my + mz * mz;
    if dist_sq < EPSILON_SQ {
        return None;
    }
    let (sx, sy, sz) = (
        target_center.x - mover_center.x,
        target_center.y - mover_center.y,
        target_center.z - mover_center.z,
    );
    let gap = sx * sx + sy * sy + sz * sz - rad_sum * rad_sum;
    if gap < EPSILON_SQ {
        return None;
    }
    // Positive when the target is ahead.
    let similar = -(sx * mx + sy * my + sz * mz);
    let disc = similar * similar - gap * dist_sq;
    if disc < 0.0 {
        return None;
    }
    let c_dist = disc.sqrt();
    let root = if similar - c_dist < 0.0 {
        -(c_dist + similar)
    } else {
        -(similar - c_dist)
    };
    let t = root / dist_sq;
    (t > 0.0 && t <= 1.0).then_some(t)
}

/// Moves a sphere along `movement_dir` until it rests on `plane`, scaling
/// `walk_interp` (the fraction of the step still unused) to match. Refused
/// (nothing changes) when moving parallel to the plane, or when the landing
/// would not shorten the step or would overshoot it by more than half.
pub fn land_on_sphere(
    plane: Plane,
    sphere_radius: f32,
    sphere_center: &mut Vec3,
    movement_dir: Vec3,
    walk_interp: &mut f32,
) -> bool {
    let dist_to_plane = sphere_center.dot(plane.normal) + plane.d;
    let denom = movement_dir.dot(plane.normal);
    let t_land = if denom > EPSILON {
        // Moving out of the plane's front.
        (-sphere_radius - dist_to_plane) / denom
    } else if denom >= -EPSILON {
        return false;
    } else {
        (dist_to_plane - sphere_radius) / denom
    };
    let new_interp = (1.0 - t_land) * *walk_interp;
    if new_interp >= *walk_interp || new_interp < -0.5 {
        return false;
    }
    *sphere_center -= movement_dir * t_land;
    *walk_interp = new_interp;
    true
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CollisionPrimitivesTests.cs`
    //! and `SphereIntersectsSphereConformanceTests.cs`.

    use super::*;

    fn unit_square_xy() -> (Plane, [Vec3; 4]) {
        (
            Plane::new(Vec3::Z, 0.0),
            [
                Vec3::new(0.0, 0.0, 0.0),
                Vec3::new(1.0, 0.0, 0.0),
                Vec3::new(1.0, 1.0, 0.0),
                Vec3::new(0.0, 1.0, 0.0),
            ],
        )
    }

    #[test]
    fn sphere_intersects_ray_direct_hit() {
        let t = sphere_intersects_ray(Vec3::ZERO, 1.0, Vec3::new(0.0, 0.0, 5.0), -Vec3::Z).unwrap();
        assert!((3.9..=4.1).contains(&t), "t = {t}");
    }

    #[test]
    fn sphere_intersects_ray_miss() {
        assert!(
            sphere_intersects_ray(Vec3::new(0.0, 10.0, 0.0), 1.0, Vec3::ZERO, Vec3::X).is_none()
        );
    }

    #[test]
    fn sphere_intersects_ray_origin_inside() {
        assert!(sphere_intersects_ray(Vec3::ZERO, 5.0, Vec3::X, Vec3::X).is_none());
    }

    #[test]
    fn sphere_intersects_ray_grazing() {
        assert!(
            sphere_intersects_ray(Vec3::ZERO, 1.0, Vec3::new(1.0, 5.0, 0.0), -Vec3::Y).is_some()
        );
    }

    #[test]
    fn ray_plane_perpendicular() {
        let t = ray_plane_intersect(Plane::new(Vec3::Z, 0.0), Vec3::new(0.0, 0.0, 5.0), -Vec3::Z)
            .unwrap();
        assert!((t - 5.0).abs() < 1e-5);
    }

    #[test]
    fn ray_plane_parallel() {
        assert!(ray_plane_intersect(Plane::new(Vec3::Z, 0.0), Vec3::ZERO, Vec3::X).is_none());
    }

    #[test]
    fn ray_plane_behind() {
        // z = -5 is behind a ray going +z from the origin.
        assert!(ray_plane_intersect(Plane::new(Vec3::Z, 5.0), Vec3::ZERO, Vec3::Z).is_none());
    }

    #[test]
    fn calc_normal_square() {
        let (_, verts) = unit_square_xy();
        let (n, d) = calc_normal(&verts);
        assert!(n.x.abs() < 1e-5 && n.y.abs() < 1e-5);
        assert!((n.z.abs() - 1.0).abs() < 0.001);
        assert!(d.abs() < 1e-5);
    }

    #[test]
    fn calc_normal_is_unit_length() {
        let verts = [
            Vec3::ZERO,
            Vec3::new(2.0, 0.0, 0.0),
            Vec3::new(0.0, 2.0, 0.0),
        ];
        let (n, _) = calc_normal(&verts);
        assert!((n.length() - 1.0).abs() < 0.001);
    }

    #[test]
    fn calc_normal_degenerate_is_zero() {
        let verts = [Vec3::ZERO, Vec3::X, Vec3::new(2.0, 0.0, 0.0)];
        assert_eq!(calc_normal(&verts).0, Vec3::ZERO);
        assert_eq!(calc_normal(&verts[..2]), (Vec3::ZERO, 0.0));
    }

    #[test]
    fn sphere_intersects_poly_above_centre() {
        let (plane, verts) = unit_square_xy();
        let c = sphere_intersects_poly(plane, &verts, Vec3::new(0.5, 0.5, 0.5), 0.6).unwrap();
        assert!(c.z.abs() < 1e-4);
    }

    #[test]
    fn sphere_intersects_poly_far_above() {
        let (plane, verts) = unit_square_xy();
        assert!(sphere_intersects_poly(plane, &verts, Vec3::new(0.5, 0.5, 5.0), 0.5).is_none());
    }

    #[test]
    fn sphere_intersects_poly_outside_edge() {
        let (plane, verts) = unit_square_xy();
        assert!(sphere_intersects_poly(plane, &verts, Vec3::new(5.0, 0.5, 0.0), 0.3).is_none());
    }

    #[test]
    fn find_time_of_collision_approaching() {
        let (plane, verts) = unit_square_xy();
        let t =
            find_time_of_collision(plane, &verts, Vec3::new(0.5, 0.5, 3.0), 1.0, -Vec3::Z).unwrap();
        assert!((t - -3.0).abs() < 1e-4);
    }

    #[test]
    fn find_time_of_collision_parallel() {
        let (plane, verts) = unit_square_xy();
        assert!(
            find_time_of_collision(plane, &verts, Vec3::new(0.5, 0.5, 0.5), 0.2, Vec3::X).is_none()
        );
    }

    #[test]
    fn hits_walkable_from_below() {
        let (plane, verts) = unit_square_xy();
        assert!(hits_walkable(
            plane,
            &verts,
            Vec3::new(0.5, 0.5, -0.3),
            0.5,
            Vec3::Z
        ));
    }

    #[test]
    fn hits_walkable_moving_away() {
        let (plane, verts) = unit_square_xy();
        assert!(!hits_walkable(
            plane,
            &verts,
            Vec3::new(0.5, 0.5, 0.3),
            0.5,
            -Vec3::Z
        ));
    }

    #[test]
    fn find_walkable_collision_inside() {
        let (plane, verts) = unit_square_xy();
        assert!(
            find_walkable_collision(plane, &verts, Vec3::new(0.5, 0.5, 1.0), -Vec3::Z).is_none()
        );
    }

    #[test]
    fn find_walkable_collision_parallel() {
        let (plane, verts) = unit_square_xy();
        assert!(
            find_walkable_collision(plane, &verts, Vec3::new(0.5, 0.5, 0.0), Vec3::X).is_none()
        );
    }

    #[test]
    fn find_walkable_collision_crosses_edge() {
        let (plane, verts) = unit_square_xy();
        let n = find_walkable_collision(
            plane,
            &verts,
            Vec3::new(2.5, 0.5, 1.0),
            Vec3::new(-1.0, 0.0, -1.0).normalize(),
        )
        .unwrap();
        assert!(n.length_squared() > 0.5);
        assert!((n.length() - 1.0).abs() < 0.01);
    }

    #[test]
    fn slide_sphere_above_plane() {
        let t = slide_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            Vec3::new(0.0, 0.0, 3.0),
            -Vec3::Z,
        );
        assert!(t > 0.0);
    }

    #[test]
    fn slide_sphere_already_touching() {
        let t = slide_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            Vec3::new(0.0, 0.0, 0.3),
            -Vec3::Z,
        );
        assert_eq!(t, f32::MAX);
    }

    #[test]
    fn slide_sphere_parallel() {
        let t = slide_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            Vec3::new(0.0, 0.0, 3.0),
            Vec3::X,
        );
        assert_eq!(t, 0.0);
    }

    #[test]
    fn land_on_sphere_just_inside_radius() {
        let mut c = Vec3::new(0.0, 0.0, 0.3);
        let mut wi = 1.0;
        assert!(land_on_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            &mut c,
            -Vec3::Z,
            &mut wi
        ));
        assert!((c.z - 0.5).abs() < 0.01, "z = {}", c.z);
    }

    #[test]
    fn land_on_sphere_parallel() {
        let mut c = Vec3::new(0.0, 0.0, 2.0);
        let mut wi = 1.0;
        assert!(!land_on_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            &mut c,
            Vec3::X,
            &mut wi
        ));
    }

    #[test]
    fn land_on_sphere_invalid_walk_interp() {
        let mut c = Vec3::new(0.0, 0.0, 100.0);
        let mut wi = 0.0001;
        assert!(!land_on_sphere(
            Plane::new(Vec3::Z, 0.0),
            0.5,
            &mut c,
            -Vec3::Z,
            &mut wi
        ));
    }

    // SphereIntersectsSphereConformanceTests.cs: hand-verified anchors.

    #[test]
    fn swept_sphere_head_on() {
        let t = swept_sphere_hits_sphere(
            Vec3::ZERO,
            1.0,
            Vec3::new(5.0, 0.0, 0.0),
            Vec3::new(5.0, 0.0, 0.0),
            1.0,
        )
        .unwrap();
        assert!((t - 0.6).abs() < 1e-4, "t = {t}");
    }

    #[test]
    fn swept_sphere_perpendicular_too_far() {
        assert!(
            swept_sphere_hits_sphere(
                Vec3::ZERO,
                1.0,
                Vec3::new(0.0, 10.0, 0.0),
                Vec3::new(3.0, 0.0, 0.0),
                1.0
            )
            .is_none()
        );
    }

    #[test]
    fn swept_sphere_tangent() {
        let t = swept_sphere_hits_sphere(
            Vec3::ZERO,
            1.0,
            Vec3::new(0.0, 6.0, 0.0),
            Vec3::new(2.0, 3.0, 0.0),
            1.0,
        )
        .unwrap();
        assert!(t > 0.0 && t <= 1.0);
    }

    #[test]
    fn swept_sphere_misses() {
        let miss = |c: Vec3, r: f32, d: Vec3, tc: Vec3, tr: f32| {
            swept_sphere_hits_sphere(c, r, d, tc, tr).is_none()
        };
        // Lateral offset 2.1 is beyond the combined radius 2.
        assert!(miss(
            Vec3::ZERO,
            1.0,
            Vec3::new(0.0, 6.0, 0.0),
            Vec3::new(2.1, 3.0, 0.0),
            1.0
        ));
        // Sweeping away.
        assert!(miss(
            Vec3::ZERO,
            1.0,
            Vec3::new(-5.0, 0.0, 0.0),
            Vec3::new(5.0, 0.0, 0.0),
            1.0
        ));
        // Target beyond the step (t > 1).
        assert!(miss(
            Vec3::ZERO,
            1.0,
            Vec3::new(3.0, 0.0, 0.0),
            Vec3::new(10.0, 0.0, 0.0),
            1.0
        ));
        // Degenerate sweep.
        assert!(miss(
            Vec3::ZERO,
            1.0,
            Vec3::ZERO,
            Vec3::new(0.5, 0.0, 0.0),
            0.1
        ));
        // Already overlapping.
        assert!(miss(
            Vec3::ZERO,
            1.0,
            Vec3::X,
            Vec3::new(0.5, 0.0, 0.0),
            1.0
        ));
    }

    #[test]
    fn swept_sphere_vertical_uses_full_3d_distance() {
        let t = swept_sphere_hits_sphere(
            Vec3::new(0.0, 0.0, 5.0),
            1.0,
            Vec3::new(0.0, 0.0, -5.0),
            Vec3::ZERO,
            1.0,
        )
        .unwrap();
        assert!((t - 0.6).abs() < 1e-4, "t = {t}");
    }
}
