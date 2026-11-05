//! How an observed (remote) object's body moves in one update: by its
//! animation's root motion, unless position interpolation (chasing the
//! server's latest position) takes over and replaces it.

use glam::{Quat, Vec3};

use crate::delta_frame::MotionDeltaFrame;

/// The position-interpolation step a remote body runs after its animation.
/// The physics side implements it (the interpolation queue lives there).
pub trait OffsetAdjuster {
    /// Given the body's position and orientation and the offset the
    /// animation produced for `dt` seconds, replace `offset` with a
    /// catch-up step when interpolation is active. Returns whether it
    /// replaced it.
    #[allow(clippy::too_many_arguments)]
    fn adjust_offset(
        &mut self,
        dt: f64,
        current_body_position: Vec3,
        current_body_orientation: Quat,
        max_speed: f32,
        offset: &mut MotionDeltaFrame,
        in_contact: bool,
        is_sticky: bool,
    ) -> bool;
}

/// Compose root motion and interpolation into `output`: start from
/// `root_motion_local_frame` and let `interp` replace it. Returns whether
/// interpolation replaced it.
#[allow(clippy::too_many_arguments)]
pub fn compose_offset(
    dt: f64,
    current_body_position: Vec3,
    ori: Quat,
    root_motion_local_frame: &MotionDeltaFrame,
    interp: &mut dyn OffsetAdjuster,
    max_speed: f32,
    output: &mut MotionDeltaFrame,
    in_contact: bool,
    is_sticky: bool,
) -> bool {
    *output = *root_motion_local_frame;
    interp.adjust_offset(
        dt,
        current_body_position,
        ori,
        max_speed,
        output,
        in_contact,
        is_sticky,
    )
}

/// The world-space offset for one update of a body in contact: the local
/// root-motion step (or interpolation's replacement) rotated by `ori`.
#[must_use]
pub fn compute_offset(
    dt: f64,
    current_body_position: Vec3,
    root_motion_local_delta: Vec3,
    ori: Quat,
    interp: &mut dyn OffsetAdjuster,
    max_speed: f32,
) -> Vec3 {
    let root = MotionDeltaFrame {
        origin: root_motion_local_delta,
        ..MotionDeltaFrame::default()
    };
    let mut output = MotionDeltaFrame::default();
    compose_offset(
        dt,
        current_body_position,
        ori,
        &root,
        interp,
        max_speed,
        &mut output,
        true,
        false,
    );
    ori * output.origin
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `RemoteMotionCombinerTests` that need no live
    //! interpolation queue (an idle adjuster leaves the root motion alone);
    //! the chase cases come with the interpolation port.
    use super::*;
    use std::f32::consts::PI;

    /// An interpolation queue with nothing queued.
    struct Idle;

    impl OffsetAdjuster for Idle {
        fn adjust_offset(
            &mut self,
            _dt: f64,
            _pos: Vec3,
            _ori: Quat,
            _max_speed: f32,
            _offset: &mut MotionDeltaFrame,
            _in_contact: bool,
            _is_sticky: bool,
        ) -> bool {
            false
        }
    }

    /// Replaces the offset with a fixed body-local step.
    struct Replace(Vec3);

    impl OffsetAdjuster for Replace {
        fn adjust_offset(
            &mut self,
            _dt: f64,
            _pos: Vec3,
            _ori: Quat,
            _max_speed: f32,
            offset: &mut MotionDeltaFrame,
            _in_contact: bool,
            _is_sticky: bool,
        ) -> bool {
            offset.origin = self.0;
            true
        }
    }

    fn close(a: Vec3, b: Vec3) -> bool {
        (a - b).abs().max_element() < 1e-4
    }

    #[test]
    fn stationary_body_does_not_move() {
        let v = compute_offset(0.1, Vec3::ZERO, Vec3::ZERO, Quat::IDENTITY, &mut Idle, 4.0);
        assert_eq!(v, Vec3::ZERO);
    }

    #[test]
    fn animation_only_moves_forward() {
        let v = compute_offset(
            0.1,
            Vec3::ZERO,
            Vec3::new(0.0, 0.4, 0.0),
            Quat::IDENTITY,
            &mut Idle,
            0.0,
        );
        assert!(close(v, Vec3::new(0.0, 0.4, 0.0)));
    }

    #[test]
    fn animation_only_follows_the_orientation() {
        let south = Quat::from_axis_angle(Vec3::Z, PI);
        let v = compute_offset(
            0.1,
            Vec3::ZERO,
            Vec3::new(0.0, 0.4, 0.0),
            south,
            &mut Idle,
            0.0,
        );
        assert!(close(v, Vec3::new(0.0, -0.4, 0.0)));

        let west = Quat::from_axis_angle(Vec3::Z, PI / 2.0);
        let v = compute_offset(
            1.0,
            Vec3::ZERO,
            Vec3::new(0.0, 1.0, 0.0),
            west,
            &mut Idle,
            0.0,
        );
        assert!(close(v, Vec3::new(-1.0, 0.0, 0.0)));
    }

    #[test]
    fn interpolation_replaces_the_root_motion() {
        let v = compute_offset(
            0.1,
            Vec3::ZERO,
            Vec3::new(0.0, 0.4, 0.0),
            Quat::IDENTITY,
            &mut Replace(Vec3::new(0.8, 0.0, 0.0)),
            4.0,
        );
        assert!(close(v, Vec3::new(0.8, 0.0, 0.0)));
    }
}
