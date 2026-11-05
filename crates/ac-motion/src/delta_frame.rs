//! A movement accumulated over one update: how far the object moved
//! (metres, in its own axes at the start of the update) and how it turned.

use glam::{Quat, Vec3};

use crate::frame::set_rotate;

/// An accumulated relative movement.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MotionDeltaFrame {
    /// Translation, metres.
    pub origin: Vec3,
    /// Rotation.
    pub orientation: Quat,
}

impl Default for MotionDeltaFrame {
    fn default() -> Self {
        Self {
            origin: Vec3::ZERO,
            orientation: Quat::IDENTITY,
        }
    }
}

impl MotionDeltaFrame {
    /// Back to no movement.
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    /// Append a relative step: move by `local_origin × origin_scale` in the
    /// current axes, then turn by `local_orientation`. Only the translation
    /// is scaled.
    pub fn combine(&mut self, local_origin: Vec3, local_orientation: Quat, origin_scale: f32) {
        self.origin += self.orientation * (local_origin * origin_scale);
        self.orientation = set_rotate(
            self.origin,
            self.orientation,
            self.orientation * local_orientation,
        );
    }

    /// Append another accumulated movement.
    pub fn combine_delta(&mut self, delta: &MotionDeltaFrame, origin_scale: f32) {
        self.combine(delta.origin, delta.orientation, origin_scale);
    }

    /// The compass heading of the orientation, degrees.
    #[must_use]
    pub fn heading(&self) -> f32 {
        get_heading(self.orientation)
    }

    /// Replace the orientation with a pure turn to `heading_deg`.
    pub fn set_heading(&mut self, heading_deg: f32) {
        self.orientation = set_heading(heading_deg);
    }
}

/// Compass heading of `orientation`, degrees in `[0, 360)`: 0 is north
/// (+y), 90 is east (+x), measured clockwise seen from above.
#[must_use]
pub fn get_heading(orientation: Quat) -> f32 {
    let forward = orientation * Vec3::Y;
    let yaw_deg = forward.y.atan2(forward.x) * (180.0 / std::f32::consts::PI);
    let mut heading = (90.0 - yaw_deg) % 360.0;
    if heading < 0.0 {
        heading += 360.0;
    }
    heading
}

/// A pure turn about z that faces compass heading `heading_deg`.
#[must_use]
pub fn set_heading(heading_deg: f32) -> Quat {
    use std::f32::consts::PI;
    let yaw_deg = 90.0 - heading_deg;
    let yaw = yaw_deg * (PI / 180.0);
    Quat::from_axis_angle(Vec3::Z, yaw - PI / 2.0)
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `MotionDeltaFrameTests`.
    use super::*;
    use crate::frame::Frame;
    use std::f32::consts::PI;

    fn assert_vec(expected: Vec3, actual: Vec3) {
        assert!(
            (expected - actual).abs().max_element() < 1e-5,
            "{expected} vs {actual}"
        );
    }

    fn assert_equivalent(expected: Quat, actual: Quat) {
        let alignment = expected.normalize().dot(actual.normalize()).abs();
        assert!(
            (0.99999..=1.00001).contains(&alignment),
            "{expected} vs {actual}"
        );
    }

    #[test]
    fn combine_matches_the_frame_combine() {
        let base = Quat::from_axis_angle(Vec3::Z, PI / 2.0);
        let delta = Quat::from_axis_angle(Vec3::Z, PI / 4.0);
        let mut expected = Frame::new(Vec3::new(10.0, 20.0, 30.0), base);
        expected.combine(&Frame::new(Vec3::new(2.0, 0.0, 1.0), delta));
        let mut actual = MotionDeltaFrame {
            origin: Vec3::new(10.0, 20.0, 30.0),
            orientation: base,
        };
        actual.combine(Vec3::new(2.0, 0.0, 1.0), delta, 1.0);
        assert_vec(expected.origin, actual.origin);
        assert_equivalent(expected.orientation, actual.orientation);
    }

    #[test]
    fn a_later_step_goes_along_the_earlier_turn() {
        let mut acc = MotionDeltaFrame::default();
        acc.combine(Vec3::ZERO, Quat::from_axis_angle(Vec3::Z, PI / 2.0), 1.0);
        acc.combine(Vec3::new(0.0, 2.0, 0.0), Quat::IDENTITY, 1.0);
        assert_vec(Vec3::new(-2.0, 0.0, 0.0), acc.origin);
        assert_equivalent(Quat::from_axis_angle(Vec3::Z, PI / 2.0), acc.orientation);
    }

    #[test]
    fn rotations_post_multiply() {
        let around_x = Quat::from_axis_angle(Vec3::X, 0.7);
        let around_y = Quat::from_axis_angle(Vec3::Y, -0.4);
        let mut acc = MotionDeltaFrame {
            origin: Vec3::ZERO,
            orientation: around_x,
        };
        acc.combine(Vec3::ZERO, around_y, 1.0);
        assert_equivalent(
            set_rotate(Vec3::ZERO, around_x, around_x * around_y),
            acc.orientation,
        );
        assert!(acc.orientation.dot((around_y * around_x).normalize()).abs() < 0.999);
    }

    #[test]
    fn an_invalid_rotation_keeps_the_previous_one() {
        let previous = Quat::from_axis_angle(Vec3::Y, 0.35);
        for invalid in [
            Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            Quat::from_xyzw(f32::NAN, 0.0, 0.0, 1.0),
            Quat::from_xyzw(f32::INFINITY, 0.0, 0.0, 1.0),
        ] {
            let mut acc = MotionDeltaFrame {
                origin: Vec3::ZERO,
                orientation: previous,
            };
            acc.combine(Vec3::ZERO, invalid, 1.0);
            assert_equivalent(previous, acc.orientation);
        }
    }

    #[test]
    fn a_tiny_rotation_normalises_like_retail() {
        let mut acc = MotionDeltaFrame::default();
        acc.combine(Vec3::ZERO, Quat::from_xyzw(1e-30, 0.0, 0.0, 0.0), 1.0);
        assert_equivalent(Quat::from_axis_angle(Vec3::X, PI), acc.orientation);
    }

    #[test]
    fn origin_scale_does_not_scale_rotation() {
        let mut acc = MotionDeltaFrame::default();
        let turn = Quat::from_axis_angle(Vec3::Z, PI / 3.0);
        acc.combine(Vec3::new(0.0, 0.5, 0.0), turn, 2.0);
        assert_vec(Vec3::new(0.0, 1.0, 0.0), acc.origin);
        assert_equivalent(turn, acc.orientation);
    }

    #[test]
    fn reset_restores_identity() {
        let mut f = MotionDeltaFrame {
            origin: Vec3::ONE,
            orientation: Quat::from_axis_angle(Vec3::X, 0.5),
        };
        f.reset();
        assert_eq!(f, MotionDeltaFrame::default());
    }

    #[test]
    fn heading_round_trips() {
        for heading in [0.0f32, 45.0, 90.0, 180.0, 270.0, 359.0] {
            let mut f = MotionDeltaFrame::default();
            f.set_heading(heading);
            let got = f.heading();
            let diff = (got - heading).abs();
            assert!(
                diff < 1e-3 || (diff - 360.0).abs() < 1e-3,
                "{heading} -> {got}"
            );
        }
    }
}
