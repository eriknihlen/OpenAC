//! The interpreted motion state: what an object is actually doing on each
//! axis after the interpreter has normalised the input (left turns and
//! sidesteps become right ones at negative speed, walking backwards becomes
//! walking forwards at negative speed, the run key turns walks into runs).
//! This is what the server relays to observers and what drives velocity.

use crate::command::{ACTION_BIT, SUBSTATE_BIT};
use crate::motion_command::{NON_COMBAT, READY, SIDE_STEP_RIGHT, TURN_RIGHT};
use crate::movement_parameters::MovementParameters;
use crate::raw_motion_state::RawMotionAction;

/// Walking speed of the walk cycle at speed 1, metres per second.
#[allow(clippy::excessive_precision)]
pub const WALK_ANIM_SPEED: f32 = 3.119_999_89;
/// Running speed of the run cycle at speed 1, metres per second.
pub const RUN_ANIM_SPEED: f32 = 4.0;
/// Sidestep speed at speed 1, metres per second.
pub const SIDESTEP_ANIM_SPEED: f32 = 1.25;
/// Walking backwards runs at this fraction of the forward speed.
#[allow(clippy::excessive_precision)]
pub const BACKWARDS_FACTOR: f32 = 0.649_999_976;
/// Holding run turns this much faster.
pub const RUN_TURN_FACTOR: f32 = 1.5;
/// Fastest sidestep animation rate.
pub const MAX_SIDESTEP_ANIM_RATE: f32 = 3.0;
/// Sidestep speed factor before the walk/sidestep ratio.
pub const SIDESTEP_FACTOR: f32 = 0.5;

/// The interpreted (normalised) motion state of an object.
#[derive(Debug, Clone, PartialEq)]
pub struct InterpretedMotionState {
    /// Forward axis command (a looping state; ready when idle).
    pub forward_command: u32,
    /// Forward axis speed (signed).
    pub forward_speed: f32,
    /// Sidestep axis command (sidestep-right or 0).
    pub side_step_command: u32,
    /// Sidestep axis speed (signed).
    pub side_step_speed: f32,
    /// Turn axis command (turn-right or 0).
    pub turn_command: u32,
    /// Turn axis speed (signed).
    pub turn_speed: f32,
    /// Stance.
    pub current_style: u32,
    actions: Vec<RawMotionAction>,
}

impl Default for InterpretedMotionState {
    /// Idle: ready, no sidestep or turn, speeds 1, non-combat stance.
    fn default() -> Self {
        Self {
            forward_command: READY,
            forward_speed: 1.0,
            side_step_command: 0,
            side_step_speed: 1.0,
            turn_command: 0,
            turn_speed: 1.0,
            current_style: NON_COMBAT,
            actions: Vec::new(),
        }
    }
}

impl InterpretedMotionState {
    /// Queued actions, oldest first.
    #[must_use]
    pub fn actions(&self) -> &[RawMotionAction] {
        &self.actions
    }

    /// Queue an action; command and stamp keep their low 16 bits.
    pub fn add_action(&mut self, motion: u32, speed: f32, action_stamp: u32, autonomous: bool) {
        self.actions.push(RawMotionAction {
            command: motion as u16,
            stamp: action_stamp as u16,
            autonomous,
            speed,
        });
    }

    /// Pop the oldest action's command; 0 when there is none.
    pub fn remove_action(&mut self) -> u32 {
        if self.actions.is_empty() {
            0
        } else {
            u32::from(self.actions.remove(0).command)
        }
    }

    /// How many actions are queued.
    #[must_use]
    pub fn get_num_actions(&self) -> u32 {
        self.actions.len() as u32
    }

    /// Record `motion` on its axis: turn-right and sidestep-right on their
    /// axes, other looping states on the forward axis, a stance as the
    /// style (forward back to ready), actions in the queue. Only the
    /// right/forward forms exist here: the interpreter has already mapped
    /// the left and backward ones.
    pub fn apply_motion(&mut self, motion: u32, p: &MovementParameters) {
        if motion == TURN_RIGHT {
            self.turn_command = motion;
            self.turn_speed = p.speed;
            return;
        }
        if motion == SIDE_STEP_RIGHT {
            self.side_step_command = motion;
            self.side_step_speed = p.speed;
            return;
        }
        if motion & SUBSTATE_BIT != 0 {
            self.forward_command = motion;
            self.forward_speed = p.speed;
            return;
        }
        if (motion as i32) < 0 {
            self.forward_command = READY;
            self.current_style = motion;
            return;
        }
        if motion & ACTION_BIT != 0 {
            self.add_action(motion, p.speed, p.action_stamp, p.autonomous);
        }
    }

    /// Clear `motion` from its axis: turn-right and sidestep-right to none,
    /// the forward axis to ready at speed 1 when it held `motion`, the stance
    /// to non-combat when it was `motion`. Turn-left is not a turn here and
    /// leaves the turn axis alone.
    pub fn remove_motion(&mut self, motion: u32) {
        if motion == TURN_RIGHT {
            self.turn_command = 0;
            return;
        }
        if motion == SIDE_STEP_RIGHT {
            self.side_step_command = 0;
            return;
        }
        if motion & SUBSTATE_BIT == 0 {
            if (motion as i32) < 0 && motion == self.current_style {
                self.current_style = NON_COMBAT;
            }
        } else if motion == self.forward_command {
            self.forward_command = READY;
            self.forward_speed = 1.0;
        }
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `InterpretedMotionStateActionFifoTests`
    //! (`tests/AcDream.Core.Tests/Physics/Motion/`).
    use super::*;

    #[test]
    fn default_has_no_actions() {
        let ims = InterpretedMotionState::default();
        assert!(ims.actions().is_empty());
        assert_eq!(ims.get_num_actions(), 0);
    }

    #[test]
    fn add_action_appends_in_order_and_counts() {
        let mut ims = InterpretedMotionState::default();
        ims.add_action(0x1000_004B, 1.0, 1, false);
        ims.add_action(0x1000_0050, 1.5, 2, true);
        assert_eq!(ims.get_num_actions(), 2);
        assert_eq!(ims.actions()[0].command, 0x004B);
        assert_eq!(ims.actions()[1].command, 0x0050);
    }

    #[test]
    fn remove_action_pops_head_first() {
        let mut ims = InterpretedMotionState::default();
        ims.add_action(0x1000_004B, 1.0, 1, false);
        ims.add_action(0x1000_0050, 1.5, 2, true);
        assert_eq!(ims.remove_action(), 0x004B);
        assert_eq!(ims.remove_action(), 0x0050);
        assert_eq!(ims.get_num_actions(), 0);
    }

    #[test]
    fn remove_action_on_empty_returns_zero() {
        let mut ims = InterpretedMotionState::default();
        assert_eq!(ims.remove_action(), 0);
    }

    #[test]
    fn six_queued_actions_meet_the_depth_cap() {
        let mut ims = InterpretedMotionState::default();
        for i in 0..6u32 {
            ims.add_action(0x1000_0000 + i, 1.0, i, false);
        }
        assert_eq!(ims.get_num_actions(), 6);
    }

    #[test]
    fn apply_motion_sets_each_axis() {
        let mut ims = InterpretedMotionState::default();
        let p = MovementParameters {
            speed: 1.5,
            ..MovementParameters::default()
        };
        ims.apply_motion(0x6500_000D, &p);
        assert_eq!((ims.turn_command, ims.turn_speed), (0x6500_000D, 1.5));

        let mut ims = InterpretedMotionState::default();
        let p = MovementParameters {
            speed: 1.248,
            ..MovementParameters::default()
        };
        ims.apply_motion(0x6500_000F, &p);
        assert_eq!(
            (ims.side_step_command, ims.side_step_speed),
            (0x6500_000F, 1.248)
        );

        let mut ims = InterpretedMotionState::default();
        let p = MovementParameters {
            speed: 2.94,
            ..MovementParameters::default()
        };
        ims.apply_motion(0x4400_0007, &p);
        assert_eq!(
            (ims.forward_command, ims.forward_speed),
            (0x4400_0007, 2.94)
        );
    }

    #[test]
    fn a_stance_resets_forward_to_ready() {
        let mut ims = InterpretedMotionState {
            forward_command: 0x4500_0005,
            ..InterpretedMotionState::default()
        };
        ims.apply_motion(0x8000_0042, &MovementParameters::default());
        assert_eq!(ims.forward_command, 0x4100_0003);
        assert_eq!(ims.current_style, 0x8000_0042);
    }

    #[test]
    fn an_action_is_queued_with_its_stamp() {
        let mut ims = InterpretedMotionState::default();
        let p = MovementParameters {
            speed: 1.0,
            action_stamp: 7,
            autonomous: true,
            ..MovementParameters::default()
        };
        ims.apply_motion(0x1000_004B, &p);
        assert_eq!(ims.get_num_actions(), 1);
        let a = ims.actions()[0];
        assert_eq!(
            (a.command, a.speed, a.stamp, a.autonomous),
            (0x004B, 1.0, 7, true)
        );
    }

    #[test]
    fn remove_motion_clears_the_matching_axis() {
        let mut ims = InterpretedMotionState {
            turn_command: 0x6500_000D,
            ..InterpretedMotionState::default()
        };
        ims.remove_motion(0x6500_000D);
        assert_eq!(ims.turn_command, 0);

        // Turn-left is not the turn axis here: it falls through to the
        // style check and leaves the turn alone.
        let mut ims = InterpretedMotionState {
            turn_command: 0x6500_000E,
            ..InterpretedMotionState::default()
        };
        ims.remove_motion(0x6500_000E);
        assert_eq!(ims.turn_command, 0x6500_000E);

        let mut ims = InterpretedMotionState {
            side_step_command: 0x6500_000F,
            ..InterpretedMotionState::default()
        };
        ims.remove_motion(0x6500_000F);
        assert_eq!(ims.side_step_command, 0);

        let mut ims = InterpretedMotionState {
            forward_command: 0x4500_0005,
            forward_speed: 3.0,
            ..InterpretedMotionState::default()
        };
        ims.remove_motion(0x4500_0005);
        assert_eq!((ims.forward_command, ims.forward_speed), (0x4100_0003, 1.0));

        let mut ims = InterpretedMotionState {
            current_style: 0x8000_0042,
            ..InterpretedMotionState::default()
        };
        ims.remove_motion(0x8000_0042);
        assert_eq!(ims.current_style, NON_COMBAT);
    }
}
