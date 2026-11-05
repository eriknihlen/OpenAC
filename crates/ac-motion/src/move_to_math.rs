//! Heading and distance arithmetic for moving to and turning toward things.
//!
//! A *heading* is a compass bearing in degrees, `[0, 360)`: 0 faces north
//! (+y), 90 east (+x), measured clockwise seen from above. A *yaw* is the
//! same direction as a counter-clockwise angle from +x in radians.

use glam::{Quat, Vec3};

use crate::motion_command::TURN_RIGHT;

pub use crate::delta_frame::{get_heading, set_heading};

/// Heading tolerance, degrees.
pub const EPSILON: f32 = 0.000_2;

/// How far `h1` is past `h2` in the direction of `turn_cmd`, degrees in
/// `[0, 360)`: clockwise for turn-right, otherwise mirrored to measure
/// counter-clockwise. Differences inside the tolerance are 0.
#[must_use]
pub fn heading_diff(h1: f32, h2: f32, turn_cmd: u32) -> f32 {
    let mut d = h1 - h2;
    if (h1 - h2).abs() < EPSILON {
        d = 0.0;
    }
    if d < -EPSILON {
        d += 360.0;
    }
    if EPSILON < d && turn_cmd != TURN_RIGHT {
        d = 360.0 - d;
    }
    d
}

/// Whether heading `a` is past heading `b` in the direction of `turn_cmd`
/// (clockwise for turn-right), taking the shorter way round.
#[must_use]
pub fn heading_greater(a: f32, b: f32, turn_cmd: u32) -> bool {
    let greater = if (a - b).abs() > 180.0 { b > a } else { a > b };
    if turn_cmd == TURN_RIGHT {
        greater
    } else {
        !greater
    }
}

/// The compass heading from `from` to `to`, ignoring height.
#[must_use]
pub fn position_heading(from: Vec3, to: Vec3) -> f32 {
    let dx = to.x - from.x;
    let dy = to.y - from.y;
    let mut heading = 450.0 - dy.atan2(dx) * (180.0 / std::f32::consts::PI);
    heading %= 360.0;
    if heading < 0.0 {
        heading += 360.0;
    }
    heading
}

/// The heading of a yaw (radians, counter-clockwise from +x).
#[must_use]
pub fn heading_from_yaw(yaw_rad: f32) -> f32 {
    let mut heading = 90.0 - yaw_rad * (180.0 / std::f32::consts::PI);
    heading %= 360.0;
    if heading < 0.0 {
        heading += 360.0;
    }
    heading
}

/// The yaw (radians, in `[-π, π]`) of a heading.
#[must_use]
pub fn yaw_from_heading(heading_deg: f32) -> f32 {
    use std::f32::consts::PI;
    let mut yaw = (90.0 - heading_deg) * (PI / 180.0);
    while yaw > PI {
        yaw -= 2.0 * PI;
    }
    while yaw < -PI {
        yaw += 2.0 * PI;
    }
    yaw
}

/// The gap between two upright cylinders standing at `own_pos` and
/// `target_pos` (their bases), metres: the centre distance less both radii,
/// combined with the vertical gap when both gaps have the same sign
/// (negative when they overlap both ways).
#[must_use]
pub fn cylinder_distance(
    own_radius: f32,
    own_height: f32,
    own_pos: Vec3,
    target_radius: f32,
    target_height: f32,
    target_pos: Vec3,
) -> f32 {
    let radial_gap = own_pos.distance(target_pos) - (own_radius + target_radius);
    let vertical_gap = if own_pos.z <= target_pos.z {
        target_pos.z - (own_pos.z + own_height)
    } else {
        own_pos.z - (target_pos.z + target_height)
    };
    if vertical_gap > 0.0 && radial_gap > 0.0 {
        return (vertical_gap * vertical_gap + radial_gap * radial_gap).sqrt();
    }
    if vertical_gap < 0.0 && radial_gap < 0.0 {
        return -(vertical_gap * vertical_gap + radial_gap * radial_gap).sqrt();
    }
    radial_gap
}

/// The horizontal gap between two cylinders, metres.
#[must_use]
pub fn cylinder_distance_no_z(
    own_radius: f32,
    own_pos: Vec3,
    target_radius: f32,
    target_pos: Vec3,
) -> f32 {
    let dx = target_pos.x - own_pos.x;
    let dy = target_pos.y - own_pos.y;
    (dx * dx + dy * dy).sqrt() - own_radius - target_radius
}

/// Normalise `v` in place; true (and `v` untouched) when it is too short
/// to have a direction.
pub fn normalize_check_small(v: &mut Vec3) -> bool {
    let length = v.length();
    if length < 1e-8 {
        return true;
    }
    *v /= length;
    false
}

/// A world vector in the axes of a frame with orientation
/// `frame_orientation`.
#[must_use]
pub fn global_to_local_vec(frame_orientation: Quat, world_vec: Vec3) -> Vec3 {
    frame_orientation.conjugate() * world_vec
}

/// A landblock-relative position in the world frame centred on landblock
/// (`live_center_landblock_x`, `live_center_landblock_y`): 192 m per
/// landblock.
#[must_use]
pub fn origin_to_world(
    origin_cell_id: u32,
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    live_center_landblock_x: i32,
    live_center_landblock_y: i32,
) -> Vec3 {
    let lb_x = ((origin_cell_id >> 24) & 0xFF) as i32;
    let lb_y = ((origin_cell_id >> 16) & 0xFF) as i32;
    Vec3::new(
        origin_x + (lb_x - live_center_landblock_x) as f32 * 192.0,
        origin_y + (lb_y - live_center_landblock_y) as f32 * 192.0,
        origin_z,
    )
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `MoveToMathHeadingDiffTests`,
    //! `MoveToMathHeadingGreaterTests`, `MoveToMathCylinderDistanceTests`
    //! and `MoveToMathPositionHeadingTests`
    //! (`tests/AcDream.Core.Tests/Physics/Motion/`).
    use super::*;
    use crate::motion_command::{TURN_LEFT, WALK_FORWARD};

    /// xUnit `Assert.Equal(expected, actual, precision)`.
    #[track_caller]
    fn eq_prec(expected: f32, actual: f32, precision: i32) {
        let scale = 10f64.powi(precision);
        let round = |x: f32| (f64::from(x) * scale).round_ties_even() / scale;
        assert_eq!(round(expected), round(actual), "{expected} vs {actual}");
    }

    #[allow(clippy::excessive_precision)]
    const EPS: f32 = 0.000_199_999_995;

    #[test]
    fn heading_diff_turn_right() {
        eq_prec(60.0, heading_diff(90.0, 30.0, TURN_RIGHT), 3);
        eq_prec(300.0, heading_diff(30.0, 90.0, TURN_RIGHT), 3);
        eq_prec(0.0, heading_diff(45.0, 45.0, TURN_RIGHT), 3);
        eq_prec(340.0, heading_diff(350.0, 10.0, TURN_RIGHT), 3);
    }

    #[test]
    fn heading_diff_epsilon_boundaries_are_strict() {
        let d = heading_diff(EPS, 0.0, TURN_RIGHT);
        assert_ne!(d, 0.0);
        eq_prec(EPS, d, 6);
        assert_eq!(heading_diff(EPS * 0.5, 0.0, TURN_RIGHT), 0.0);
        let raw = -EPS * 1.5;
        eq_prec(raw + 360.0, heading_diff(raw, 0.0, TURN_RIGHT), 3);
        eq_prec(-EPS, heading_diff(-EPS, 0.0, TURN_RIGHT), 6);
    }

    #[test]
    fn heading_diff_mirrors_for_anything_but_turn_right() {
        eq_prec(300.0, heading_diff(90.0, 30.0, TURN_LEFT), 3);
        eq_prec(60.0, heading_diff(30.0, 90.0, TURN_LEFT), 3);
        assert_eq!(heading_diff(45.0, 45.0, TURN_LEFT), 0.0);
        eq_prec(EPS, heading_diff(EPS, 0.0, TURN_LEFT), 6);
        let raw = EPS * 2.0;
        eq_prec(360.0 - raw, heading_diff(raw, 0.0, TURN_LEFT), 3);
        eq_prec(300.0, heading_diff(90.0, 30.0, 0), 3);
        eq_prec(300.0, heading_diff(90.0, 30.0, WALK_FORWARD), 3);
        eq_prec(20.0, heading_diff(350.0, 10.0, TURN_LEFT), 3);
    }

    #[test]
    fn heading_greater_cases() {
        assert!(heading_greater(90.0, 30.0, TURN_RIGHT));
        assert!(!heading_greater(30.0, 90.0, TURN_RIGHT));
        assert!(!heading_greater(350.0, 10.0, TURN_RIGHT));
        assert!(heading_greater(10.0, 350.0, TURN_RIGHT));
        assert!(heading_greater(200.0, 20.0, TURN_RIGHT));
        assert!(!heading_greater(90.0, 30.0, TURN_LEFT));
        assert!(heading_greater(30.0, 90.0, TURN_LEFT));
        assert!(heading_greater(350.0, 10.0, TURN_LEFT));
        assert!(!heading_greater(10.0, 350.0, TURN_LEFT));
        assert!(!heading_greater(45.0, 45.0, TURN_RIGHT));
        assert!(heading_greater(45.0, 45.0, TURN_LEFT));
        assert!(!heading_greater(90.0, 30.0, 0));
        assert!(!heading_greater(90.0, 30.0, WALK_FORWARD));
    }

    #[test]
    fn cylinder_distance_cases() {
        let d = cylinder_distance(1.0, 2.0, Vec3::ZERO, 2.0, 2.0, Vec3::new(10.0, 0.0, 0.0));
        eq_prec(7.0, d, 3);
        let d = cylinder_distance(5.0, 2.0, Vec3::ZERO, 5.0, 2.0, Vec3::new(1.0, 0.0, 0.0));
        eq_prec(-(85f32.sqrt()), d, 3);
        let d = cylinder_distance(0.0, 2.0, Vec3::ZERO, 0.0, 2.0, Vec3::new(3.0, 4.0, 0.0));
        eq_prec(5.0, d, 3);
        let d = cylinder_distance(1.0, 2.0, Vec3::ZERO, 1.0, 2.0, Vec3::new(3.0, 0.0, 4.0));
        eq_prec(13f32.sqrt(), d, 3);
        let d = cylinder_distance(0.5, 2.0, Vec3::ZERO, 0.5, 2.0, Vec3::ZERO);
        eq_prec(-(5f32.sqrt()), d, 3);
        let d = cylinder_distance(5.0, 1.0, Vec3::ZERO, 5.0, 1.0, Vec3::new(0.0, 0.0, 3.0));
        eq_prec(-7.0, d, 3);
    }

    #[test]
    fn position_heading_cardinals() {
        eq_prec(
            0.0,
            position_heading(Vec3::ZERO, Vec3::new(0.0, 1.0, 0.0)),
            2,
        );
        eq_prec(
            90.0,
            position_heading(Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0)),
            2,
        );
        eq_prec(
            180.0,
            position_heading(Vec3::ZERO, Vec3::new(0.0, -1.0, 0.0)),
            2,
        );
        eq_prec(
            270.0,
            position_heading(Vec3::ZERO, Vec3::new(-1.0, 0.0, 0.0)),
            2,
        );
        let h = position_heading(Vec3::ZERO, Vec3::new(1.0, 1.0, 0.0));
        assert!((0.0..=360.0).contains(&h));
        eq_prec(45.0, h, 2);
    }

    #[test]
    fn position_heading_ignores_height() {
        let h1 = position_heading(Vec3::new(0.0, 0.0, 5.0), Vec3::new(1.0, 0.0, -10.0));
        let h2 = position_heading(Vec3::new(0.0, 0.0, -3.0), Vec3::new(1.0, 0.0, 100.0));
        eq_prec(h1, h2, 2);
        eq_prec(90.0, h1, 2);
    }

    #[test]
    fn get_and_set_heading_round_trip() {
        eq_prec(0.0, get_heading(Quat::IDENTITY), 1);
        for heading in [0.0f32, 90.0, 180.0, 270.0, 45.0, 359.0] {
            let back = get_heading(set_heading(heading));
            let mut diff = (back - heading).abs();
            if diff > 180.0 {
                diff = 360.0 - diff;
            }
            assert!(diff < 0.5, "heading {heading} round-tripped to {back}");
        }
        assert!((set_heading(0.0) * Vec3::Y).y > 0.9);
        assert!((set_heading(90.0) * Vec3::Y).x > 0.9);
    }
}
