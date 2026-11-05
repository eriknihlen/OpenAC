//! The cycle a server-moved object should play for the velocity the server
//! reports: stand below 0.2 m/s, walk below 1.25 m/s, run above, with the
//! speed factor that makes the cycle cover the ground at that velocity.

use glam::Vec3;

use crate::interpreted_state::{RUN_ANIM_SPEED, WALK_ANIM_SPEED};
use crate::motion_command::{READY, RUN_FORWARD, WALK_FORWARD};

/// Below this horizontal speed the object stands, metres per second.
pub const STOP_SPEED: f32 = 0.20;
/// At or above this horizontal speed the object runs, metres per second.
pub const RUN_THRESHOLD: f32 = 1.25;
/// Smallest speed factor.
pub const MIN_SPEED_MOD: f32 = 0.25;
/// Largest speed factor.
pub const MAX_SPEED_MOD: f32 = 3.00;

/// The cycle to play and how fast.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LocomotionCycle {
    /// Ready, walk forward or run forward.
    pub motion: u32,
    /// Speed factor.
    pub speed_mod: f32,
    /// Walking or running.
    pub is_moving: bool,
}

/// The cycle for a world velocity (only the horizontal part counts).
#[must_use]
pub fn plan_from_velocity(world_velocity: Vec3) -> LocomotionCycle {
    let horizontal_speed =
        (world_velocity.x * world_velocity.x + world_velocity.y * world_velocity.y).sqrt();
    if horizontal_speed < STOP_SPEED {
        return LocomotionCycle {
            motion: READY,
            speed_mod: 1.0,
            is_moving: false,
        };
    }
    if horizontal_speed < RUN_THRESHOLD {
        return LocomotionCycle {
            motion: WALK_FORWARD,
            speed_mod: (horizontal_speed / WALK_ANIM_SPEED).clamp(MIN_SPEED_MOD, MAX_SPEED_MOD),
            is_moving: true,
        };
    }
    LocomotionCycle {
        motion: RUN_FORWARD,
        speed_mod: (horizontal_speed / RUN_ANIM_SPEED).clamp(MIN_SPEED_MOD, MAX_SPEED_MOD),
        is_moving: true,
    }
}

/// Whether a velocity-driven cycle may replace `current_motion`: only
/// ready and the locomotion family (walk, backwards, run, sidesteps).
#[must_use]
pub fn can_apply_velocity_cycle(current_motion: u32) -> bool {
    current_motion == READY || is_locomotion(current_motion)
}

/// Walk, walk backwards, run, sidestep right or left (by low byte).
#[must_use]
pub fn is_locomotion(motion: u32) -> bool {
    matches!(motion & 0xFF, 0x05 | 0x06 | 0x07 | 0x0F | 0x10)
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `ServerControlledLocomotionTests`
    //! (`tests/AcDream.Core.Tests/Physics/`).
    use super::*;
    use crate::motion_command::DEAD;

    #[test]
    fn stops_below_the_noise_threshold() {
        let plan = plan_from_velocity(Vec3::new(0.10, 0.12, 3.0));
        assert!(!plan.is_moving);
        assert_eq!(plan.motion, READY);
        assert_eq!(plan.speed_mod, 1.0);
    }

    #[test]
    fn walks_for_slow_motion() {
        let plan = plan_from_velocity(Vec3::new(0.0, 0.80, 0.0));
        assert!(plan.is_moving);
        assert_eq!(plan.motion, WALK_FORWARD);
        assert!((0.25..=0.27).contains(&plan.speed_mod));
    }

    #[test]
    fn runs_at_the_run_scale() {
        let plan = plan_from_velocity(Vec3::new(0.0, RUN_ANIM_SPEED, 0.0));
        assert!(plan.is_moving);
        assert_eq!(plan.motion, RUN_FORWARD);
        assert!((plan.speed_mod - 1.0).abs() < 5e-5);
    }

    #[test]
    fn clamps_very_fast_snapshots() {
        let plan = plan_from_velocity(Vec3::new(0.0, 30.0, 0.0));
        assert_eq!(plan.motion, RUN_FORWARD);
        assert_eq!(plan.speed_mod, MAX_SPEED_MOD);
    }

    #[test]
    fn only_the_locomotion_family_takes_a_velocity_cycle() {
        for m in [READY, WALK_FORWARD, RUN_FORWARD] {
            assert!(can_apply_velocity_cycle(m));
        }
        for m in [DEAD, 0x1000_0058, 0x1000_0051] {
            assert!(!can_apply_velocity_cycle(m));
        }
    }
}
