//! The movement manager: the object's single entry point for movement
//! requests. Motion commands go to the interpreter; move-to and turn-to
//! requests go to the move-to manager, made on first use when the object can
//! move to things.

use ac_motion::MovementType;

use crate::movement::interp::{MotionInterp, MovementStruct};
use crate::movement::move_to::{MoveToHost, MoveToOps};
use crate::movement::target::TargetInfo;
use crate::movement::weenie_error::WeenieError;

/// An object with a movement manager.
pub trait MovementManagerHost: MoveToHost {
    /// The move-to manager has been made.
    fn move_to_exists(&self) -> bool;
    /// Make the move-to manager when the object can have one; returns
    /// whether it exists afterwards.
    fn make_move_to_manager(&mut self) -> bool;
    /// Wake the object's physics (a movement request arrived).
    fn activate_physics_object(&mut self) {}
}

/// Movement-manager operations, on any [`MovementManagerHost`].
pub trait MovementOps: MovementManagerHost {
    /// Wake the object, then route the request: commands and stops to the
    /// interpreter, move-to and turn-to requests to the move-to manager
    /// (failing when the object cannot have one).
    fn movement_perform(&mut self, mvs: &MovementStruct) -> WeenieError {
        self.activate_physics_object();
        match mvs.kind {
            MovementType::RawCommand
            | MovementType::InterpretedCommand
            | MovementType::StopRawCommand
            | MovementType::StopInterpretedCommand
            | MovementType::StopCompletely => self.perform_movement(mvs),
            MovementType::MoveToObject
            | MovementType::MoveToPosition
            | MovementType::TurnToObject
            | MovementType::TurnToHeading => {
                if !self.make_move_to_manager() {
                    return WeenieError::GENERAL_MOVEMENT_FAILURE;
                }
                self.move_to_perform_movement(mvs);
                WeenieError::NONE
            }
            MovementType::Invalid => WeenieError::GENERAL_MOVEMENT_FAILURE,
        }
    }

    /// Per-update: drive the move-to.
    fn movement_use_time(&mut self) {
        if self.move_to_exists() {
            self.move_to_use_time();
        }
    }

    /// Landed: the interpreter, then the move-to.
    fn movement_hit_ground(&mut self) {
        self.hit_ground();
        if self.move_to_exists() {
            self.move_to_hit_ground();
        }
    }

    /// Leaving the world: the interpreter drops its queue.
    fn movement_handle_exit_world(&mut self) {
        self.handle_exit_world();
    }

    /// Cancel the move-to.
    fn movement_cancel_move_to(&mut self, error: WeenieError) {
        if self.move_to_exists() {
            self.cancel_move_to(error);
        }
    }

    /// A target report for the move-to.
    fn movement_handle_update_target(&mut self, info: TargetInfo) {
        if self.move_to_exists() {
            self.move_to_handle_update_target(info);
        }
    }

    /// A move-to is in progress.
    fn movement_is_moving_to(&self) -> bool {
        self.move_to_exists() && self.move_to_ref().is_moving_to()
    }
}

impl<T: MovementManagerHost + ?Sized> MovementOps for T {}
