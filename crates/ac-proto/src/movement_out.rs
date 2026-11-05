//! The local player's movement as the client reports it: the raw motion
//! state (what the player is pressing), the move-to-state and
//! autonomous-position actions, and jumps.
//!
//! All three are game actions: they take the session's next action
//! sequence number and are sent on the UI queue.

use ac_bits::Writer;

use crate::wire::action;
use crate::{GameActionType, Position, Vector3};

/// Hold-key values: whether a motion is held, and with the run key.
pub mod hold_key {
    /// Not set.
    pub const INVALID: u32 = 0;
    /// Held without the run key.
    pub const NONE: u32 = 1;
    /// Held with the run key.
    pub const RUN: u32 = 2;
}

/// Style a new motion state starts in (non-combat).
pub const DEFAULT_STYLE: u32 = 0x8000_003D;

/// Forward command a new motion state starts with (ready).
pub const DEFAULT_FORWARD_COMMAND: u32 = 0x4100_0003;

/// Flag bits of the packed motion state, one per field that differs from
/// its default.
mod flag {
    pub const CURRENT_HOLD_KEY: u32 = 0x001;
    pub const CURRENT_STYLE: u32 = 0x002;
    pub const FORWARD_COMMAND: u32 = 0x004;
    pub const FORWARD_HOLD_KEY: u32 = 0x008;
    pub const FORWARD_SPEED: u32 = 0x010;
    pub const SIDESTEP_COMMAND: u32 = 0x020;
    pub const SIDESTEP_HOLD_KEY: u32 = 0x040;
    pub const SIDESTEP_SPEED: u32 = 0x080;
    pub const TURN_COMMAND: u32 = 0x100;
    pub const TURN_HOLD_KEY: u32 = 0x200;
    pub const TURN_SPEED: u32 = 0x400;
    /// The action count sits in bits 11..16.
    pub const ACTION_COUNT_SHIFT: u32 = 11;
    pub const ACTION_COUNT_MASK: u32 = 0xF800;
}

/// One discrete action queued in a motion state (an emote, an attack).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RawMotionAction {
    /// The motion command, low 16 bits.
    pub command: u16,
    /// The action stamp, 15 bits.
    pub stamp: u16,
    /// The client started it.
    pub autonomous: bool,
    /// Speed.
    pub speed: f32,
}

/// What the player is pressing: the motion state the client sends. A field
/// equal to its default is left out of the packed form.
#[derive(Debug, Clone, PartialEq)]
pub struct RawMotionState {
    /// Hold key of the whole state (see [`hold_key`]).
    pub current_hold_key: u32,
    /// Stance.
    pub current_style: u32,
    /// Forward motion command.
    pub forward_command: u32,
    /// Hold key of the forward motion.
    pub forward_hold_key: u32,
    /// Forward speed.
    pub forward_speed: f32,
    /// Sidestep command.
    pub sidestep_command: u32,
    /// Hold key of the sidestep.
    pub sidestep_hold_key: u32,
    /// Sidestep speed.
    pub sidestep_speed: f32,
    /// Turn command.
    pub turn_command: u32,
    /// Hold key of the turn.
    pub turn_hold_key: u32,
    /// Turn speed.
    pub turn_speed: f32,
    /// Queued actions, oldest first.
    pub actions: Vec<RawMotionAction>,
}

impl Default for RawMotionState {
    fn default() -> Self {
        Self {
            current_hold_key: hold_key::NONE,
            current_style: DEFAULT_STYLE,
            forward_command: DEFAULT_FORWARD_COMMAND,
            forward_hold_key: hold_key::INVALID,
            forward_speed: 1.0,
            sidestep_command: 0,
            sidestep_hold_key: hold_key::INVALID,
            sidestep_speed: 1.0,
            turn_command: 0,
            turn_hold_key: hold_key::INVALID,
            turn_speed: 1.0,
            actions: Vec::new(),
        }
    }
}

impl RawMotionState {
    /// Append the packed form: a flag word (one bit per non-default field,
    /// the action count in bits 11..16), the non-default fields in flag
    /// order, then per action its command, its stamp with the autonomous
    /// bit on top, and its speed.
    pub fn pack(&self, w: &mut Writer) {
        let d = Self::default();
        let mut flags = 0;
        let mut set = |differs: bool, bit: u32| {
            if differs {
                flags |= bit;
            }
        };
        set(
            self.current_hold_key != d.current_hold_key,
            flag::CURRENT_HOLD_KEY,
        );
        set(self.current_style != d.current_style, flag::CURRENT_STYLE);
        set(
            self.forward_command != d.forward_command,
            flag::FORWARD_COMMAND,
        );
        set(
            self.forward_hold_key != d.forward_hold_key,
            flag::FORWARD_HOLD_KEY,
        );
        set(self.forward_speed != d.forward_speed, flag::FORWARD_SPEED);
        set(
            self.sidestep_command != d.sidestep_command,
            flag::SIDESTEP_COMMAND,
        );
        set(
            self.sidestep_hold_key != d.sidestep_hold_key,
            flag::SIDESTEP_HOLD_KEY,
        );
        set(
            self.sidestep_speed != d.sidestep_speed,
            flag::SIDESTEP_SPEED,
        );
        set(self.turn_command != d.turn_command, flag::TURN_COMMAND);
        set(self.turn_hold_key != d.turn_hold_key, flag::TURN_HOLD_KEY);
        set(self.turn_speed != d.turn_speed, flag::TURN_SPEED);
        // The count field is five bits wide; every action is still written.
        flags |=
            ((self.actions.len() as u32) << flag::ACTION_COUNT_SHIFT) & flag::ACTION_COUNT_MASK;
        w.u32(flags);
        let has = |bit: u32| flags & bit != 0;
        if has(flag::CURRENT_HOLD_KEY) {
            w.u32(self.current_hold_key);
        }
        if has(flag::CURRENT_STYLE) {
            w.u32(self.current_style);
        }
        if has(flag::FORWARD_COMMAND) {
            w.u32(self.forward_command);
        }
        if has(flag::FORWARD_HOLD_KEY) {
            w.u32(self.forward_hold_key);
        }
        if has(flag::FORWARD_SPEED) {
            w.f32(self.forward_speed);
        }
        if has(flag::SIDESTEP_COMMAND) {
            w.u32(self.sidestep_command);
        }
        if has(flag::SIDESTEP_HOLD_KEY) {
            w.u32(self.sidestep_hold_key);
        }
        if has(flag::SIDESTEP_SPEED) {
            w.f32(self.sidestep_speed);
        }
        if has(flag::TURN_COMMAND) {
            w.u32(self.turn_command);
        }
        if has(flag::TURN_HOLD_KEY) {
            w.u32(self.turn_hold_key);
        }
        if has(flag::TURN_SPEED) {
            w.f32(self.turn_speed);
        }
        for a in &self.actions {
            w.u16(a.command);
            w.u16((a.stamp & 0x7FFF) | if a.autonomous { 0x8000 } else { 0 });
            w.f32(a.speed);
        }
    }
}

/// The physics timestamps the client echoes with its position.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MotionTimestamps {
    /// Instance timestamp.
    pub instance: u16,
    /// Server-control timestamp.
    pub server_control: u16,
    /// Teleport timestamp.
    pub teleport: u16,
    /// Force-position timestamp.
    pub force_position: u16,
}

fn write_position(w: &mut Writer, position: &Position) {
    w.u32(position.cell_id);
    w.f32(position.origin.x);
    w.f32(position.origin.y);
    w.f32(position.origin.z);
    w.f32(position.orientation.w);
    w.f32(position.orientation.x);
    w.f32(position.orientation.y);
    w.f32(position.orientation.z);
}

fn write_timestamps(w: &mut Writer, ts: &MotionTimestamps) {
    w.u16(ts.instance);
    w.u16(ts.server_control);
    w.u16(ts.teleport);
    w.u16(ts.force_position);
}

/// The player's motion state changed: the packed motion state, the
/// position (cell, origin, rotation as w, x, y, z), the four timestamps,
/// then a byte holding contact (bit 0) and standing long jump (bit 1),
/// padded to 4 bytes.
#[must_use]
pub fn build_move_to_state(
    sequence: u32,
    motion: &RawMotionState,
    position: &Position,
    timestamps: &MotionTimestamps,
    contact: bool,
    standing_long_jump: bool,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::MOVE_TO_STATE);
    motion.pack(&mut w);
    write_position(&mut w, position);
    write_timestamps(&mut w, timestamps);
    w.u8(u8::from(contact) | if standing_long_jump { 0x02 } else { 0 });
    w.align(4);
    w.into_vec()
}

/// The player's position while it moves on its own: position, the four
/// timestamps, then the contact byte (1 on the ground), padded to 4 bytes.
#[must_use]
pub fn build_autonomous_position(
    sequence: u32,
    position: &Position,
    timestamps: &MotionTimestamps,
    contact: bool,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::AUTONOMOUS_POSITION);
    write_position(&mut w, position);
    write_timestamps(&mut w, timestamps);
    w.u8(u8::from(contact));
    w.align(4);
    w.into_vec()
}

/// A jump: its extent (0 to 1), the launch velocity in meters per second,
/// the position, then the four timestamps, padded to 4 bytes.
#[must_use]
pub fn build_jump(
    sequence: u32,
    extent: f32,
    velocity: Vector3,
    position: &Position,
    timestamps: &MotionTimestamps,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::JUMP);
    w.f32(extent);
    w.f32(velocity.x);
    w.f32(velocity.y);
    w.f32(velocity.z);
    write_position(&mut w, position);
    write_timestamps(&mut w, timestamps);
    w.align(4);
    w.into_vec()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `RawMotionStatePackTests.cs`, `MoveToStateGoldenTests.cs`,
    //! `MoveToStateTests.cs`, `AutonomousPositionTests.cs` and
    //! `JumpActionTests.cs`. Actions are packed with their speed, as retail
    //! and the server read them.

    use super::*;
    use crate::test_util::words;
    use crate::{Quaternion, Vector3};

    fn pack(state: &RawMotionState) -> Vec<u8> {
        let mut w = Writer::new();
        state.pack(&mut w);
        w.into_vec()
    }

    fn at(cell_id: u32, x: f32, y: f32, z: f32) -> Position {
        Position {
            cell_id,
            origin: Vector3 { x, y, z },
            orientation: Quaternion::IDENTITY,
        }
    }

    #[test]
    fn default_state_packs_to_a_zero_flag_word() {
        assert_eq!(pack(&RawMotionState::default()), [0, 0, 0, 0]);
    }

    #[test]
    fn walk_and_run_forward() {
        let walk = RawMotionState {
            forward_command: 0x4500_0005,
            forward_hold_key: hold_key::NONE,
            ..RawMotionState::default()
        };
        assert_eq!(pack(&walk), words(&[0x0C, 0x4500_0005, 1]));
        let run = RawMotionState {
            current_hold_key: hold_key::RUN,
            forward_command: 0x4400_0007,
            forward_hold_key: hold_key::RUN,
            forward_speed: 3.0,
            ..RawMotionState::default()
        };
        assert_eq!(
            pack(&run),
            [
                0x1D, 0, 0, 0, 0x02, 0, 0, 0, 0x07, 0, 0, 0x44, 0x02, 0, 0, 0, 0x00, 0x00, 0x40,
                0x40
            ]
        );
    }

    #[test]
    fn style_sidestep_and_turn() {
        let style = RawMotionState {
            current_style: 0x8000_0042,
            ..RawMotionState::default()
        };
        assert_eq!(pack(&style), words(&[0x02, 0x8000_0042]));
        let strafe_and_turn = RawMotionState {
            sidestep_command: 0x4400_0009,
            sidestep_hold_key: hold_key::RUN,
            sidestep_speed: 1.248,
            turn_command: 0x4400_000D,
            turn_hold_key: hold_key::RUN,
            turn_speed: 1.5,
            ..RawMotionState::default()
        };
        let body = pack(&strafe_and_turn);
        assert_eq!(body.len(), 28);
        assert_eq!(body[..12], words(&[0x7E0, 0x4400_0009, 2]));
        assert_eq!(body[12..16], 1.248f32.to_le_bytes());
        assert_eq!(body[16..24], words(&[0x4400_000D, 2]));
        assert_eq!(body[24..], 1.5f32.to_le_bytes());
    }

    #[test]
    fn actions_carry_command_stamp_and_speed() {
        let state = RawMotionState {
            actions: vec![
                RawMotionAction {
                    command: 0x0150,
                    stamp: 0x0001,
                    autonomous: false,
                    speed: 1.0,
                },
                RawMotionAction {
                    command: 0x0163,
                    stamp: 0x7FFF,
                    autonomous: true,
                    speed: 2.0,
                },
            ],
            ..RawMotionState::default()
        };
        let body = pack(&state);
        assert_eq!(body.len(), 4 + 8 + 8);
        assert_eq!(body[..4], 0x1000u32.to_le_bytes());
        assert_eq!(body[4..8], [0x50, 0x01, 0x01, 0x00]);
        assert_eq!(body[8..12], 1.0f32.to_le_bytes());
        assert_eq!(body[12..16], [0x63, 0x01, 0xFF, 0xFF]);
        assert_eq!(body[16..], 2.0f32.to_le_bytes());
        // The count field wraps at five bits.
        let many = RawMotionState {
            actions: vec![state.actions[0]; 33],
            ..RawMotionState::default()
        };
        let body = pack(&many);
        assert_eq!(body[..4], (1u32 << 11).to_le_bytes());
        assert_eq!(body.len(), 4 + 33 * 8);
    }

    #[test]
    fn move_to_state_layout() {
        let pos = at(0xA9B4_0001, 96.0, 96.0, 50.0);
        let body = build_move_to_state(
            1,
            &RawMotionState::default(),
            &pos,
            &MotionTimestamps::default(),
            true,
            false,
        );
        assert_eq!(body.len(), 60);
        assert_eq!(body[..20], words(&[0xF7B1, 1, 0xF61C, 0, 0xA9B4_0001]));
        assert_eq!(body[56], 0x01);
        let run = RawMotionState {
            current_hold_key: hold_key::RUN,
            forward_command: 0x4400_0007,
            forward_hold_key: hold_key::RUN,
            forward_speed: 2.94,
            ..RawMotionState::default()
        };
        let body = build_move_to_state(3, &run, &pos, &MotionTimestamps::default(), true, false);
        assert_eq!(body[12..16], 0x1Du32.to_le_bytes());
        assert_eq!(body[32..36], 0xA9B4_0001u32.to_le_bytes());
        for (contact, long_jump, byte) in [
            (false, false, 0),
            (true, false, 1),
            (false, true, 2),
            (true, true, 3),
        ] {
            let body = build_move_to_state(
                6,
                &RawMotionState::default(),
                &pos,
                &MotionTimestamps::default(),
                contact,
                long_jump,
            );
            assert_eq!(body[56], byte);
            assert_eq!(body.len() % 4, 0);
        }
        let ts = MotionTimestamps {
            instance: 0x1111,
            server_control: 0x2222,
            teleport: 0x3333,
            force_position: 0x4444,
        };
        let body = build_move_to_state(7, &RawMotionState::default(), &pos, &ts, true, false);
        assert_eq!(
            body[48..56],
            [0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44]
        );
    }

    #[test]
    fn autonomous_position_layout() {
        let body = build_autonomous_position(
            2,
            &at(0xA9B4_0001, 12.5, 34.0, 56.75),
            &MotionTimestamps::default(),
            true,
        );
        assert_eq!(body.len(), 56);
        assert_eq!(body[..16], words(&[0xF7B1, 2, 0xF753, 0xA9B4_0001]));
        assert_eq!(body[16..20], 12.5f32.to_le_bytes());
        assert_eq!(body[20..24], 34.0f32.to_le_bytes());
        assert_eq!(body[24..28], 56.75f32.to_le_bytes());
        assert_eq!(body[28..32], 1.0f32.to_le_bytes());
        assert_eq!(body[32..44], [0; 12]);
        assert_eq!(body[52], 1);
        let ts = MotionTimestamps {
            instance: 0x1111,
            server_control: 0x2222,
            teleport: 0x3333,
            force_position: 0x4444,
        };
        let airborne = build_autonomous_position(8, &at(1, 0.0, 0.0, 0.0), &ts, false);
        assert_eq!(
            airborne[44..52],
            [0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44]
        );
        assert_eq!(airborne[52], 0);
    }

    #[test]
    fn jump_layout() {
        let ts = MotionTimestamps {
            instance: 0x1111,
            server_control: 0x2222,
            teleport: 0x3333,
            force_position: 0x4444,
        };
        let body = build_jump(
            1,
            0.75,
            Vector3 {
                x: 1.5,
                y: -2.5,
                z: 9.81,
            },
            &at(0xDEAD_BEEF, 12.5, 34.0, 56.75),
            &ts,
        );
        assert_eq!(body[..12], words(&[0xF7B1, 1, 0xF61B]));
        assert_eq!(body[12..16], 0.75f32.to_le_bytes());
        assert_eq!(body[16..20], 1.5f32.to_le_bytes());
        assert_eq!(body[20..24], (-2.5f32).to_le_bytes());
        assert_eq!(body[24..28], 9.81f32.to_le_bytes());
        assert_eq!(body[28..32], 0xDEAD_BEEFu32.to_le_bytes());
        assert_eq!(body[32..36], 12.5f32.to_le_bytes());
        assert_eq!(body[44..48], 1.0f32.to_le_bytes());
        assert_eq!(
            body[60..68],
            [0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44]
        );
        assert_eq!(body.len(), 68);
    }
}
