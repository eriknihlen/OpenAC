//! Which way a particle's quad faces. The particle model's first degrade
//! entry carries a "mode": 2 turns the quad to face the viewer with no roll
//! (like a sign that always turns its face to you but stays upright), 3, 4
//! and 5 spin it only around its own X, Y or Z axis until it faces the
//! viewer as far as that one axis allows (like a door on its hinge), and
//! any other mode keeps the authored orientation.

use glam::{Quat, Vec3};

/// True when the degrade mode turns the quad toward the viewer at all.
#[must_use]
pub fn faces(degrade_mode: u32) -> bool {
    (2..=5).contains(&degrade_mode)
}

/// The world directions of a particle quad's X and Y spans.
///
/// `orientation` is the particle's world orientation, `local_axis_x` and
/// `local_axis_y` the quad's in-plane axes in model space (unit vectors),
/// `to_viewer_unit` the normalized particle-to-viewer direction, and the
/// fallbacks (camera right and up) are used when mode 2's construction is
/// degenerate (viewer straight above or below).
#[must_use]
pub fn orient_quad(
    degrade_mode: u32,
    orientation: Quat,
    local_axis_x: Vec3,
    local_axis_y: Vec3,
    to_viewer_unit: Vec3,
    fallback_right: Vec3,
    fallback_up: Vec3,
) -> (Vec3, Vec3) {
    if degrade_mode == 2 {
        return face_viewer_roll_free(to_viewer_unit, fallback_right, fallback_up);
    }

    let world_x = orientation * local_axis_x;
    let world_y = orientation * local_axis_y;
    if !(3..=5).contains(&degrade_mode) {
        return (world_x, world_y);
    }

    let local_axis = match degrade_mode {
        3 => Vec3::X,
        4 => Vec3::Y,
        _ => Vec3::Z,
    };
    let axis = orientation * local_axis;
    let normal = world_x.cross(world_y);
    if normal.length_squared() < 1e-10 {
        return (world_x, world_y);
    }
    let normal = normal / normal.length();

    let target_in_plane = to_viewer_unit - axis * to_viewer_unit.dot(axis);
    let normal_in_plane = normal - axis * normal.dot(axis);
    if target_in_plane.length_squared() < 1e-8 || normal_in_plane.length_squared() < 1e-8 {
        return (world_x, world_y);
    }
    let target_in_plane = target_in_plane / target_in_plane.length();
    let normal_in_plane = normal_in_plane / normal_in_plane.length();
    let cos = normal_in_plane.dot(target_in_plane).clamp(-1.0, 1.0);
    let sin = normal_in_plane.cross(target_in_plane).dot(axis);
    let angle = sin.atan2(cos);
    let spin = Quat::from_axis_angle(axis, angle);
    (spin * world_x, spin * world_y)
}

/// Mode 2: the quad's face points at the viewer with no roll against world
/// up (+Z); its X span stays horizontal.
fn face_viewer_roll_free(
    to_viewer_unit: Vec3,
    fallback_right: Vec3,
    fallback_up: Vec3,
) -> (Vec3, Vec3) {
    let right = to_viewer_unit.cross(Vec3::Z);
    if right.length_squared() < 1e-8 {
        return (fallback_right, fallback_up);
    }
    let right = right / right.length();
    let up = right.cross(to_viewer_unit);
    (right, up)
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.Core.Tests/Vfx/RetailParticleFacingTests.cs`.
    use super::*;

    const EPS: f32 = 1e-4;

    fn assert_vector(expected: Vec3, actual: Vec3) {
        assert!(
            expected.distance(actual) < 1e-3,
            "expected {expected}, got {actual}"
        );
    }

    #[test]
    fn faces_matches_retail_mode_window() {
        for (mode, expected) in [
            (0, false),
            (1, false),
            (2, true),
            (3, true),
            (4, true),
            (5, true),
            (6, false),
        ] {
            assert_eq!(faces(mode), expected, "mode {mode}");
        }
    }

    #[test]
    fn mode2_viewer_north_quad_x_stays_east_and_y_is_world_up() {
        let (xd, yd) = orient_quad(
            2,
            Quat::IDENTITY,
            Vec3::X,
            Vec3::Y,
            Vec3::Y,
            Vec3::X,
            Vec3::Z,
        );
        assert_vector(Vec3::X, xd);
        assert_vector(Vec3::Z, yd);
    }

    #[test]
    fn mode2_quad_plane_is_perpendicular_to_viewer_with_normal_toward_them() {
        let to_viewer = Vec3::new(0.4, -0.7, 0.59).normalize();
        let (xd, yd) = orient_quad(
            2,
            Quat::IDENTITY,
            Vec3::X,
            Vec3::Y,
            to_viewer,
            Vec3::X,
            Vec3::Z,
        );
        assert!(xd.dot(to_viewer).abs() < EPS);
        assert!(yd.dot(to_viewer).abs() < EPS);
        assert!(xd.z.abs() < EPS);
        assert!(xd.cross(yd).dot(to_viewer).abs() > 0.99);
    }

    #[test]
    fn mode2_viewer_straight_overhead_falls_back_to_camera_plane() {
        let fallback_right = Vec3::new(1.0, 1.0, 0.0).normalize();
        let (xd, yd) = orient_quad(
            2,
            Quat::IDENTITY,
            Vec3::X,
            Vec3::Y,
            Vec3::Z,
            fallback_right,
            Vec3::Z,
        );
        assert_vector(fallback_right, xd);
        assert_vector(Vec3::Z, yd);
    }

    #[test]
    fn non_facing_modes_keep_the_authored_orientation() {
        let orientation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
        for mode in [0, 1, 7] {
            let (xd, yd) = orient_quad(
                mode,
                orientation,
                Vec3::X,
                Vec3::Z,
                Vec3::Y,
                Vec3::X,
                Vec3::Z,
            );
            assert_vector(Vec3::Y, xd);
            assert_vector(Vec3::Z, yd);
        }
    }

    #[test]
    fn mode5_spins_around_local_z_until_the_normal_faces_the_viewer() {
        let (xd, yd) = orient_quad(
            5,
            Quat::IDENTITY,
            Vec3::X,
            Vec3::Z,
            Vec3::X,
            Vec3::X,
            Vec3::Z,
        );
        assert_vector(Vec3::Z, yd);
        assert_vector(Vec3::X, xd.cross(yd));
    }

    #[test]
    fn mode4_viewer_along_the_constrained_axis_keeps_authored_orientation() {
        let (xd, yd) = orient_quad(
            4,
            Quat::IDENTITY,
            Vec3::X,
            Vec3::Z,
            Vec3::Y,
            Vec3::X,
            Vec3::Z,
        );
        assert_vector(Vec3::X, xd);
        assert_vector(Vec3::Z, yd);
    }

    #[test]
    fn mode3_honors_the_particle_orientation_when_spinning() {
        let orientation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
        let axis_world = orientation * Vec3::X;
        let to_viewer = Vec3::new(0.3, 0.1, 0.95).normalize();
        let (xd, yd) = orient_quad(
            3,
            orientation,
            Vec3::X,
            Vec3::Z,
            to_viewer,
            Vec3::X,
            Vec3::Z,
        );
        let normal = xd.cross(yd).normalize();
        let target_in_plane = (to_viewer - axis_world * to_viewer.dot(axis_world)).normalize();
        assert!(normal.dot(axis_world).abs() < 1e-3);
        assert!(normal.dot(target_in_plane) > 0.999);
    }
}
