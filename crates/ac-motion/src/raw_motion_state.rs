//! The raw motion state: what the player's input asks for (forward,
//! sidestep and turn commands with their hold keys and speeds, the stance,
//! and queued actions), before the interpreter turns it into motions.

use crate::command::{ACTION_BIT, SUBSTATE_BIT};
use crate::motion_command::{
    NON_COMBAT, READY, RUN_FORWARD, SIDE_STEP_LEFT, SIDE_STEP_RIGHT, TURN_LEFT, TURN_RIGHT,
};
use crate::movement_parameters::MovementParameters;

/// Walk/run modifier key state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum HoldKey {
    /// Unset.
    #[default]
    Invalid = 0,
    /// Not held (walk).
    None = 1,
    /// Held (run).
    Run = 2,
}

/// A queued action as the wire carries it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RawMotionAction {
    /// Command, low 16 bits.
    pub command: u16,
    /// Action sequence stamp, low 16 bits.
    pub stamp: u16,
    /// Requested by the object itself.
    pub autonomous: bool,
    /// Speed factor.
    pub speed: f32,
}

/// The input-level motion state.
#[derive(Debug, Clone, PartialEq)]
pub struct RawMotionState {
    /// The walk/run key.
    pub current_hold_key: HoldKey,
    /// Stance.
    pub current_style: u32,
    /// Forward axis command.
    pub forward_command: u32,
    /// Forward axis hold key.
    pub forward_hold_key: HoldKey,
    /// Forward axis speed.
    pub forward_speed: f32,
    /// Sidestep axis command (0 for none).
    pub sidestep_command: u32,
    /// Sidestep axis hold key.
    pub sidestep_hold_key: HoldKey,
    /// Sidestep axis speed.
    pub sidestep_speed: f32,
    /// Turn axis command (0 for none).
    pub turn_command: u32,
    /// Turn axis hold key.
    pub turn_hold_key: HoldKey,
    /// Turn axis speed.
    pub turn_speed: f32,
    actions: Vec<RawMotionAction>,
}

impl Default for RawMotionState {
    fn default() -> Self {
        Self {
            current_hold_key: HoldKey::None,
            current_style: NON_COMBAT,
            forward_command: READY,
            forward_hold_key: HoldKey::Invalid,
            forward_speed: 1.0,
            sidestep_command: 0,
            sidestep_hold_key: HoldKey::Invalid,
            sidestep_speed: 1.0,
            turn_command: 0,
            turn_hold_key: HoldKey::Invalid,
            turn_speed: 1.0,
            actions: Vec::new(),
        }
    }
}

impl RawMotionState {
    /// Queued actions, oldest first.
    #[must_use]
    pub fn actions(&self) -> &[RawMotionAction] {
        &self.actions
    }

    /// Replace the queued actions.
    pub fn set_actions(&mut self, actions: &[RawMotionAction]) {
        self.actions.clear();
        self.actions.extend_from_slice(actions);
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

    /// Record `motion` on the axis it belongs to: turns and sidesteps on
    /// their axes, other looping states (except run, which input never
    /// sets directly) on the forward axis, stances as the style (resetting
    /// forward to ready), actions in the queue.
    pub fn apply_motion(&mut self, motion: u32, p: &MovementParameters) {
        let hold_key = if p.set_hold_key {
            HoldKey::Invalid
        } else {
            p.hold_key_to_apply
        };
        match motion {
            TURN_RIGHT | TURN_LEFT => {
                self.turn_command = motion;
                self.turn_hold_key = hold_key;
                self.turn_speed = p.speed;
            }
            SIDE_STEP_RIGHT | SIDE_STEP_LEFT => {
                self.sidestep_command = motion;
                self.sidestep_hold_key = hold_key;
                self.sidestep_speed = p.speed;
            }
            _ if motion & SUBSTATE_BIT == 0 => {
                if (motion as i32) >= 0 {
                    if motion & ACTION_BIT != 0 {
                        self.add_action(motion, p.speed, p.action_stamp, p.autonomous);
                    }
                } else if self.current_style != motion {
                    self.forward_command = READY;
                    self.current_style = motion;
                }
            }
            RUN_FORWARD => {}
            _ => {
                self.forward_command = motion;
                self.forward_hold_key = hold_key;
                self.forward_speed = p.speed;
            }
        }
    }

    /// Clear `motion` from its axis: turns and sidesteps go to none, the
    /// forward axis back to ready at speed 1 when it held `motion`, the
    /// stance back to non-combat when it was `motion`.
    pub fn remove_motion(&mut self, motion: u32) {
        match motion {
            TURN_RIGHT | TURN_LEFT => self.turn_command = 0,
            SIDE_STEP_RIGHT | SIDE_STEP_LEFT => self.sidestep_command = 0,
            _ if motion & SUBSTATE_BIT == 0 => {
                if (motion as i32) < 0 && motion == self.current_style {
                    self.current_style = NON_COMBAT;
                }
            }
            _ => {
                if motion == self.forward_command {
                    self.forward_command = READY;
                    self.forward_speed = 1.0;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `RawMotionStateActionFifoTests`.
    use super::*;

    #[test]
    fn actions_are_a_fifo_of_16_bit_commands() {
        let mut raw = RawMotionState::default();
        raw.add_action(0x1000_004B, 1.0, 1, false);
        raw.add_action(0x1000_0050, 1.5, 2, true);
        assert_eq!(raw.actions()[0].command, 0x004B);
        assert_eq!(raw.actions()[1].command, 0x0050);
        assert_eq!(raw.remove_action(), 0x004B);
        assert_eq!(raw.remove_action(), 0x0050);
        assert!(raw.actions().is_empty());
        assert_eq!(raw.remove_action(), 0);
    }

    #[test]
    fn add_action_stores_speed_stamp_and_autonomy() {
        let mut raw = RawMotionState::default();
        raw.add_action(0x1000_004B, 2.5, 0x7FFF, true);
        let a = raw.actions()[0];
        assert_eq!((a.speed, a.stamp, a.autonomous), (2.5, 0x7FFF, true));
    }

    #[test]
    fn turn_and_sidestep_follow_the_hold_key_flag() {
        let mut raw = RawMotionState::default();
        let p = MovementParameters {
            speed: 1.5,
            ..MovementParameters::default()
        };
        raw.apply_motion(TURN_RIGHT, &p);
        assert_eq!((raw.turn_command, raw.turn_speed), (TURN_RIGHT, 1.5));
        assert_eq!(raw.turn_hold_key, HoldKey::Invalid);

        let p = MovementParameters {
            speed: 1.5,
            set_hold_key: false,
            hold_key_to_apply: HoldKey::Run,
            ..MovementParameters::default()
        };
        raw.apply_motion(TURN_RIGHT, &p);
        assert_eq!(raw.turn_hold_key, HoldKey::Run);

        let p = MovementParameters {
            speed: 1.248,
            ..MovementParameters::default()
        };
        raw.apply_motion(SIDE_STEP_RIGHT, &p);
        assert_eq!(
            (
                raw.sidestep_command,
                raw.sidestep_speed,
                raw.sidestep_hold_key
            ),
            (SIDE_STEP_RIGHT, 1.248, HoldKey::Invalid)
        );
    }

    #[test]
    fn forward_class_motions_set_the_forward_axis_except_run() {
        let mut raw = RawMotionState::default();
        raw.apply_motion(0x4500_0005, &MovementParameters::default());
        assert_eq!(
            (raw.forward_command, raw.forward_speed, raw.forward_hold_key),
            (0x4500_0005, 1.0, HoldKey::Invalid)
        );

        let mut raw = RawMotionState::default();
        let p = MovementParameters {
            speed: 3.0,
            ..MovementParameters::default()
        };
        raw.apply_motion(RUN_FORWARD, &p);
        assert_eq!((raw.forward_command, raw.forward_speed), (READY, 1.0));
    }

    #[test]
    fn a_new_stance_resets_forward_to_ready() {
        let mut raw = RawMotionState {
            forward_command: 0x4500_0005,
            ..RawMotionState::default()
        };
        raw.apply_motion(0x8000_0042, &MovementParameters::default());
        assert_eq!(
            (raw.forward_command, raw.current_style),
            (READY, 0x8000_0042)
        );

        let mut raw = RawMotionState {
            current_style: 0x8000_0042,
            forward_command: 0x4500_0005,
            ..RawMotionState::default()
        };
        raw.apply_motion(0x8000_0042, &MovementParameters::default());
        assert_eq!(
            (raw.forward_command, raw.current_style),
            (0x4500_0005, 0x8000_0042)
        );
    }

    #[test]
    fn action_class_motions_are_queued() {
        let mut raw = RawMotionState::default();
        let p = MovementParameters {
            action_stamp: 42,
            autonomous: true,
            ..MovementParameters::default()
        };
        raw.apply_motion(0x1000_004B, &p);
        let a = raw.actions()[0];
        assert_eq!(
            (a.command, a.speed, a.stamp, a.autonomous),
            (0x004B, 1.0, 42, true)
        );
    }

    #[test]
    fn remove_motion_clears_the_matching_axis() {
        let mut raw = RawMotionState {
            turn_command: TURN_RIGHT,
            ..RawMotionState::default()
        };
        raw.remove_motion(TURN_RIGHT);
        assert_eq!(raw.turn_command, 0);
        raw.turn_command = TURN_LEFT;
        raw.remove_motion(TURN_LEFT);
        assert_eq!(raw.turn_command, 0);
        raw.sidestep_command = SIDE_STEP_RIGHT;
        raw.remove_motion(SIDE_STEP_RIGHT);
        assert_eq!(raw.sidestep_command, 0);

        let mut raw = RawMotionState {
            forward_command: 0x4500_0005,
            forward_speed: 3.0,
            ..RawMotionState::default()
        };
        raw.remove_motion(RUN_FORWARD);
        assert_eq!((raw.forward_command, raw.forward_speed), (0x4500_0005, 3.0));
        raw.remove_motion(0x4500_0005);
        assert_eq!((raw.forward_command, raw.forward_speed), (READY, 1.0));

        let mut raw = RawMotionState {
            current_style: 0x8000_0042,
            ..RawMotionState::default()
        };
        raw.remove_motion(0x8000_0099);
        assert_eq!(raw.current_style, 0x8000_0042);
        raw.remove_motion(0x8000_0042);
        assert_eq!(raw.current_style, NON_COMBAT);
    }
}
