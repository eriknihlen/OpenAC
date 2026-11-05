//! Movement data: what an object is doing — the motions it is playing, or a
//! server-driven move or turn toward a target. Carried by the motion-update
//! message and, as a length-prefixed blob, by the create-object message.
//!
//! Stances and motion commands are the 16-bit wire values; the runtime maps
//! them to full command ids.

use ac_bits::Reader;

use crate::{Result, Vector3};

/// Interpreted motion state flag: a stance follows.
pub const IMS_CURRENT_STYLE: u32 = 0x01;
/// Interpreted motion state flag: a forward command follows.
pub const IMS_FORWARD_COMMAND: u32 = 0x02;
/// Interpreted motion state flag: a forward speed follows.
pub const IMS_FORWARD_SPEED: u32 = 0x04;
/// Interpreted motion state flag: a sidestep command follows.
pub const IMS_SIDESTEP_COMMAND: u32 = 0x08;
/// Interpreted motion state flag: a sidestep speed follows.
pub const IMS_SIDESTEP_SPEED: u32 = 0x10;
/// Interpreted motion state flag: a turn command follows.
pub const IMS_TURN_COMMAND: u32 = 0x20;
/// Interpreted motion state flag: a turn speed follows.
pub const IMS_TURN_SPEED: u32 = 0x40;

/// Motion flag: an interpreted state is followed by the guid of the object
/// to stick to.
pub const MOTION_FLAG_STICK_TO_OBJECT: u8 = 0x01;
/// Motion flag: a standing long jump is in progress.
pub const MOTION_FLAG_STANDING_LONG_JUMP: u8 = 0x02;

/// Movement parameter bit: the mover may run.
pub const MOVE_PARAM_CAN_RUN: u32 = 0x2;
/// Movement parameter bit: the mover may charge.
pub const MOVE_PARAM_CAN_CHARGE: u32 = 0x10;
/// Movement parameter bit: move toward the target (not away).
pub const MOVE_PARAM_MOVE_TOWARDS: u32 = 0x200;

/// One queued action in an interpreted motion state (a wave, an attack).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MotionItem {
    /// Motion command.
    pub command: u16,
    /// Action sequence in the low 15 bits; the top bit marks an
    /// autonomous action.
    pub packed_sequence: u16,
    /// Playback speed multiplier.
    pub speed: f32,
}

/// The motions an object plays: stance, forward/sidestep/turn commands with
/// their speeds, and queued actions. An absent field means the default.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct InterpretedMotionState {
    /// The raw flags word: field flags in the low 7 bits, action count above.
    pub packed_flags: u32,
    /// Stance.
    pub current_style: Option<u16>,
    /// Forward (or backward) motion.
    pub forward_command: Option<u16>,
    /// Sidestep motion.
    pub sidestep_command: Option<u16>,
    /// Turn motion.
    pub turn_command: Option<u16>,
    /// Forward speed multiplier.
    pub forward_speed: Option<f32>,
    /// Sidestep speed multiplier.
    pub sidestep_speed: Option<f32>,
    /// Turn speed multiplier.
    pub turn_speed: Option<f32>,
    /// Queued actions, in wire order.
    pub commands: Vec<MotionItem>,
}

impl InterpretedMotionState {
    /// Read the state, then pad to 4 bytes (measured from the reader's
    /// start). Field order on the wire: stance, forward, sidestep and turn
    /// commands, then the three speeds, then the actions; the action count
    /// is 5 bits wide.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let packed_flags = r.u32()?;
        let flag = |bit: u32| packed_flags & bit != 0;
        let opt_u16 = |r: &mut Reader<'_>, bit| -> Result<Option<u16>> {
            Ok(if flag(bit) { Some(r.u16()?) } else { None })
        };
        let opt_f32 = |r: &mut Reader<'_>, bit| -> Result<Option<f32>> {
            Ok(if flag(bit) { Some(r.f32()?) } else { None })
        };
        let current_style = opt_u16(r, IMS_CURRENT_STYLE)?;
        let forward_command = opt_u16(r, IMS_FORWARD_COMMAND)?;
        let sidestep_command = opt_u16(r, IMS_SIDESTEP_COMMAND)?;
        let turn_command = opt_u16(r, IMS_TURN_COMMAND)?;
        let forward_speed = opt_f32(r, IMS_FORWARD_SPEED)?;
        let sidestep_speed = opt_f32(r, IMS_SIDESTEP_SPEED)?;
        let turn_speed = opt_f32(r, IMS_TURN_SPEED)?;

        let count = (packed_flags >> 7) & 0x1F;
        let mut commands = Vec::with_capacity(count as usize);
        for _ in 0..count {
            commands.push(MotionItem {
                command: r.u16()?,
                packed_sequence: r.u16()?,
                speed: r.f32()?,
            });
        }
        r.align(4)?;
        Ok(Self {
            packed_flags,
            current_style,
            forward_command,
            sidestep_command,
            turn_command,
            forward_speed,
            sidestep_speed,
            turn_speed,
            commands,
        })
    }
}

/// Where and how a server-driven move goes: the origin it is measured from
/// and the movement parameters, then the mover's run rate.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MoveToPath {
    /// Cell of the origin.
    pub origin_cell_id: u32,
    /// Origin inside that cell's landblock, meters.
    pub origin: Vector3,
    /// Movement parameter bits (`MOVE_PARAM_*` and others).
    pub bitfield: u32,
    /// Distance to stop at from the target, meters.
    pub distance_to_object: f32,
    /// Closest allowed distance, meters.
    pub min_distance: f32,
    /// Distance at which the move gives up, meters.
    pub fail_distance: f32,
    /// Speed multiplier.
    pub speed: f32,
    /// Distance above which the mover runs instead of walking, meters.
    pub walk_run_threshold: f32,
    /// Heading to face on arrival, degrees.
    pub desired_heading: f32,
    /// The mover's run rate.
    pub run_rate: f32,
}

impl MoveToPath {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            origin_cell_id: r.u32()?,
            origin: Vector3::read(r)?,
            bitfield: r.u32()?,
            distance_to_object: r.f32()?,
            min_distance: r.f32()?,
            fail_distance: r.f32()?,
            speed: r.f32()?,
            walk_run_threshold: r.f32()?,
            desired_heading: r.f32()?,
            run_rate: r.f32()?,
        })
    }
}

/// The three-word turn parameters.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TurnToParams {
    /// Movement parameter bits.
    pub bitfield: u32,
    /// Speed multiplier.
    pub speed: f32,
    /// Heading to turn to, degrees.
    pub desired_heading: f32,
}

impl TurnToParams {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            bitfield: r.u32()?,
            speed: r.f32()?,
            desired_heading: r.f32()?,
        })
    }
}

/// What the movement data says the object does, by movement type.
#[derive(Debug, Clone, PartialEq)]
pub enum Movement {
    /// Type 0: play these motions.
    Interpreted {
        /// The motions.
        state: InterpretedMotionState,
        /// Object to stick to, when the stick-to-object motion flag is set.
        sticky_object: Option<u32>,
    },
    /// Type 6: move to an object.
    MoveToObject {
        /// The object to move to.
        target: u32,
        /// Origin, parameters and run rate.
        path: MoveToPath,
    },
    /// Type 7: move to a position.
    MoveToPosition {
        /// Origin, parameters and run rate.
        path: MoveToPath,
    },
    /// Type 8: turn to face an object.
    TurnToObject {
        /// The object to face.
        target: u32,
        /// Heading to use when the object is not known here, degrees.
        wire_heading: f32,
        /// Turn parameters.
        params: TurnToParams,
    },
    /// Type 9: turn to a heading.
    TurnToHeading {
        /// Turn parameters.
        params: TurnToParams,
    },
    /// Any other type: nothing follows the header that is read.
    Other,
}

/// Movement data: a 4-byte header (movement type, motion flags, stance)
/// and the body for that type.
#[derive(Debug, Clone, PartialEq)]
pub struct MovementData {
    /// Movement type (0 interpreted, 6/7 move-to, 8/9 turn-to).
    pub movement_type: u8,
    /// Motion flags (`MOTION_FLAG_*`).
    pub motion_flags: u8,
    /// Stance from the header.
    pub current_style: u16,
    /// The type's body.
    pub movement: Movement,
}

impl MovementData {
    /// Read movement data. The reader's start must be 4-byte aligned with
    /// the message start; the interpreted state pads against it.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let movement_type = r.u8()?;
        let motion_flags = r.u8()?;
        let current_style = r.u16()?;
        let movement = match movement_type {
            0 => {
                let state = InterpretedMotionState::read(r)?;
                let sticky_object = if motion_flags & MOTION_FLAG_STICK_TO_OBJECT != 0 {
                    Some(r.u32()?)
                } else {
                    None
                };
                Movement::Interpreted {
                    state,
                    sticky_object,
                }
            }
            6 => Movement::MoveToObject {
                target: r.u32()?,
                path: MoveToPath::read(r)?,
            },
            7 => Movement::MoveToPosition {
                path: MoveToPath::read(r)?,
            },
            8 => Movement::TurnToObject {
                target: r.u32()?,
                wire_heading: r.f32()?,
                params: TurnToParams::read(r)?,
            },
            9 => Movement::TurnToHeading {
                params: TurnToParams::read(r)?,
            },
            _ => Movement::Other,
        };
        Ok(Self {
            movement_type,
            motion_flags,
            current_style,
            movement,
        })
    }

    /// The stance in effect: the interpreted state's own stance when it
    /// carries one, else the header's.
    #[must_use]
    pub fn stance(&self) -> u16 {
        match &self.movement {
            Movement::Interpreted { state, .. } => {
                state.current_style.unwrap_or(self.current_style)
            }
            _ => self.current_style,
        }
    }

    /// The interpreted state, for type 0.
    #[must_use]
    pub fn interpreted(&self) -> Option<&InterpretedMotionState> {
        match &self.movement {
            Movement::Interpreted { state, .. } => Some(state),
            _ => None,
        }
    }

    /// The move-to path, for types 6 and 7.
    #[must_use]
    pub fn move_to_path(&self) -> Option<&MoveToPath> {
        match &self.movement {
            Movement::MoveToObject { path, .. } | Movement::MoveToPosition { path } => Some(path),
            _ => None,
        }
    }

    /// True for a server-driven move (types 6 and 7).
    #[must_use]
    pub fn is_server_controlled_move_to(&self) -> bool {
        matches!(self.movement_type, 6 | 7)
    }

    /// True for a server-driven turn (types 8 and 9).
    #[must_use]
    pub fn is_server_controlled_turn_to(&self) -> bool {
        matches!(self.movement_type, 8 | 9)
    }

    /// True when the standing-long-jump motion flag is set.
    #[must_use]
    pub fn standing_long_jump(&self) -> bool {
        self.motion_flags & MOTION_FLAG_STANDING_LONG_JUMP != 0
    }

    /// Whether a move may run: true unless a move-to path forbids it.
    #[must_use]
    pub fn move_to_can_run(&self) -> bool {
        self.move_to_path()
            .is_none_or(|p| p.bitfield & MOVE_PARAM_CAN_RUN != 0)
    }

    /// Whether a move-to path moves toward its target.
    #[must_use]
    pub fn move_towards(&self) -> bool {
        self.move_to_path()
            .is_some_and(|p| p.bitfield & MOVE_PARAM_MOVE_TOWARDS != 0)
    }

    /// Whether a move-to path may charge.
    #[must_use]
    pub fn can_charge(&self) -> bool {
        self.move_to_path()
            .is_some_and(|p| p.bitfield & MOVE_PARAM_CAN_CHARGE != 0)
    }
}
