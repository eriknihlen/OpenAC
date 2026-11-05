//! What the local player's movement tells the server: the motion state
//! when it changes (move-to-state), a jump when one is taken, and the
//! position while the character moves on its own (autonomous position),
//! each a game action through the session's outbox.
//!
//! The cadence is the movement controller's: a movement event when the
//! controller says one is due (input edges, the mouse-look interval), a
//! jump on release, and a position after the network pass when the
//! position-event gate opens (always first; within a second only on a cell
//! or contact-plane change; after it on any frame change).

use ac_motion::{HoldKey, RawMotionState as MotionRawState};
use ac_physics::CellPosition;
use ac_physics::movement::{MovementResult, PlayerMovementController, build_raw_motion_state};
use ac_proto::movement_out::{
    MotionTimestamps, RawMotionAction, RawMotionState, build_autonomous_position, build_jump,
    build_move_to_state,
};
use ac_proto::{Position, Quaternion, Vector3};

use crate::session::Outbox;

/// What kind of movement message went out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OutboundKind {
    /// Move-to-state (the motion state).
    MoveToState,
    /// Autonomous position.
    AutonomousPosition,
    /// Jump.
    Jump,
}

/// One movement message sent, for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct OutboundRecord {
    /// Which message.
    pub kind: OutboundKind,
    /// Its action number.
    pub sequence: u32,
    /// The position it carried.
    pub position: Position,
    /// Its contact byte.
    pub contact: bool,
    /// The forward command it carried (move-to-state only).
    pub forward_command: u32,
}

fn hold_key(key: HoldKey) -> u32 {
    key as u32
}

/// The wire form of a raw motion state.
#[must_use]
pub fn wire_raw_motion_state(raw: &MotionRawState) -> RawMotionState {
    RawMotionState {
        current_hold_key: hold_key(raw.current_hold_key),
        current_style: raw.current_style,
        forward_command: raw.forward_command,
        forward_hold_key: hold_key(raw.forward_hold_key),
        forward_speed: raw.forward_speed,
        sidestep_command: raw.sidestep_command,
        sidestep_hold_key: hold_key(raw.sidestep_hold_key),
        sidestep_speed: raw.sidestep_speed,
        turn_command: raw.turn_command,
        turn_hold_key: hold_key(raw.turn_hold_key),
        turn_speed: raw.turn_speed,
        actions: raw
            .actions()
            .iter()
            .map(|a| RawMotionAction {
                command: a.command,
                stamp: a.stamp,
                autonomous: a.autonomous,
                speed: a.speed,
            })
            .collect(),
    }
}

/// The wire form of a cell position.
#[must_use]
pub fn wire_position(p: &CellPosition) -> Position {
    let o = p.frame.origin;
    let q = p.frame.orientation;
    Position {
        cell_id: p.obj_cell_id,
        origin: Vector3 {
            x: o.x,
            y: o.y,
            z: o.z,
        },
        orientation: Quaternion {
            w: q.w,
            x: q.x,
            y: q.y,
            z: q.z,
        },
    }
}

/// Sends the local player's movement messages.
#[derive(Debug, Default)]
pub struct LocalPlayerOutbound {
    records: Vec<OutboundRecord>,
    keep_records: bool,
}

impl LocalPlayerOutbound {
    /// A sender; with `keep_records` every message sent is remembered for
    /// [`take_records`](Self::take_records).
    #[must_use]
    pub fn new(keep_records: bool) -> Self {
        Self {
            records: Vec::new(),
            keep_records,
        }
    }

    /// The messages sent since the last call (only when records are kept).
    pub fn take_records(&mut self) -> Vec<OutboundRecord> {
        std::mem::take(&mut self.records)
    }

    fn record(&mut self, record: OutboundRecord) {
        if self.keep_records {
            self.records.push(record);
        }
    }

    /// After the update, before the network pass: the movement event when
    /// due, then the jump when one was taken. Nothing when hidden or when
    /// the position is not valid.
    pub fn send_pre_network_actions(
        &mut self,
        outbox: &mut Outbox,
        controller: &mut PlayerMovementController,
        movement: &MovementResult,
        timestamps: &MotionTimestamps,
        hidden: bool,
    ) {
        if hidden {
            return;
        }
        let Some(position) = controller.try_get_outbound_position() else {
            return;
        };
        if movement.should_send_movement_event {
            self.try_send_movement(outbox, controller, movement, timestamps);
        }
        if let (Some(extent), Some(velocity)) = (movement.jump_extent, movement.jump_velocity) {
            let wire = wire_position(&position);
            let sequence = outbox.send_action(|seq| {
                build_jump(
                    seq,
                    extent,
                    Vector3 {
                        x: velocity.x,
                        y: velocity.y,
                        z: velocity.z,
                    },
                    &wire,
                    timestamps,
                )
            });
            self.record(OutboundRecord {
                kind: OutboundKind::Jump,
                sequence,
                position: wire,
                contact: false,
                forward_command: 0,
            });
        }
    }

    /// After the network pass: an autonomous position when the gate opens
    /// and the body stands on walkable ground.
    pub fn send_post_network_position(
        &mut self,
        outbox: &mut Outbox,
        controller: &mut PlayerMovementController,
        timestamps: &MotionTimestamps,
        hidden: bool,
    ) {
        if hidden {
            return;
        }
        let Some(position) = controller.try_get_outbound_position() else {
            return;
        };
        if !controller.should_send_position_event(
            position,
            controller.contact_plane(),
            controller.sim_time_seconds(),
        ) || !controller.can_send_position_event()
        {
            return;
        }
        let contact = controller.capture_presentation_result().is_on_ground;
        let wire = wire_position(&position);
        let sequence =
            outbox.send_action(|seq| build_autonomous_position(seq, &wire, timestamps, contact));
        self.record(OutboundRecord {
            kind: OutboundKind::AutonomousPosition,
            sequence,
            position: wire,
            contact,
            forward_command: 0,
        });
        let plane = controller.contact_plane();
        let now = controller.sim_time_seconds();
        controller.note_position_sent(position, plane, now);
    }

    /// A position at once (the answer to a forced position), with contact,
    /// when the body stands on walkable ground.
    pub fn send_immediate_position(
        &mut self,
        outbox: &mut Outbox,
        controller: &mut PlayerMovementController,
        timestamps: &MotionTimestamps,
    ) {
        if !controller.can_send_position_event() {
            return;
        }
        let Some(position) = controller.try_get_outbound_position() else {
            return;
        };
        let wire = wire_position(&position);
        let sequence =
            outbox.send_action(|seq| build_autonomous_position(seq, &wire, timestamps, true));
        self.record(OutboundRecord {
            kind: OutboundKind::AutonomousPosition,
            sequence,
            position: wire,
            contact: true,
            forward_command: 0,
        });
        let plane = controller.contact_plane();
        let now = controller.sim_time_seconds();
        controller.note_position_sent(position, plane, now);
    }

    /// A movement event now. False when the position is not valid.
    pub fn try_send_movement(
        &mut self,
        outbox: &mut Outbox,
        controller: &mut PlayerMovementController,
        movement: &MovementResult,
        timestamps: &MotionTimestamps,
    ) -> bool {
        let Some(position) = controller.try_get_outbound_position() else {
            return false;
        };
        let wire = wire_position(&position);
        let raw = wire_raw_motion_state(&build_raw_motion_state(movement));
        let contact = movement.is_on_ground;
        // A charge begun standing still keeps the character in place while
        // movement keys are pressed; the server is told so.
        let standing_long_jump = controller.object().interp.standing_long_jump;
        let sequence = outbox.send_action(|seq| {
            build_move_to_state(seq, &raw, &wire, timestamps, contact, standing_long_jump)
        });
        self.record(OutboundRecord {
            kind: OutboundKind::MoveToState,
            sequence,
            position: wire,
            contact,
            forward_command: raw.forward_command,
        });
        let now = controller.sim_time_seconds();
        controller.note_movement_sent(now, movement.is_mouse_look_movement_event);
        true
    }
}

#[cfg(test)]
#[path = "outbound_tests.rs"]
mod tests;
