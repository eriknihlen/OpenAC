//! The animation core: how an object's motion table turns motion commands
//! into animations, and how the animation sequence plays them.
//!
//! - [`AnimationSequencer`] is the per-object surface: start cycles and
//!   actions, advance time, read the pose, collect hooks and root motion.
//! - [`MotionTableManager`] owns the object's [`MotionState`] and the queue
//!   of motions waiting for their animations; [`MotionTableRules`] is the
//!   motion table's logic; [`Sequence`] is the animation list and cursor.
//! - [`motion_command`] names every motion command; [`command`] holds the
//!   class bits and the wire catalogs.
//! - [`RawMotionState`] and [`MovementParameters`] are the input-level
//!   motion state and request options the motion interpreter builds on;
//!   [`InterpretedMotionState`] is its normalised output.
//! - [`move_to_math`] is heading and distance arithmetic for moving to
//!   things; [`server_locomotion`] picks a cycle for a server velocity.
//!
//! Pure logic: no I/O, no clock. Time comes in as seconds; distances are
//! metres; rotations are unit quaternions; angular speeds are radians per
//! second.

mod anim_node;
pub mod command;
mod command_router;
mod cycle_playback;
mod delta_frame;
mod frame;
mod hook_router;
mod interpreted_state;
mod manager;
pub mod motion_command;
mod motion_state;
mod motion_table;
pub mod move_to_math;
mod movement_parameters;
mod raw_motion_state;
mod remote_combiner;
mod sequence;
mod sequencer;
pub mod server_locomotion;
#[cfg(test)]
mod test_support;

pub use anim_node::{AnimSequenceNode, AnimationLoader};
pub use command_router::{
    AnimationCommandRouteKind, classify, route_full_command, route_wire_command,
};
pub use cycle_playback::{advance as advance_cycle, try_interpolate_part};
pub use delta_frame::{MotionDeltaFrame, get_heading, set_heading};
pub use frame::{F_EPSILON, Frame, quat_from_dat, set_rotate, vec3_from_dat};
pub use glam::{Quat, Vec3};
pub use hook_router::{AnimationHookRouter, AnimationHookSink, NullAnimationHookSink, SinkId};
pub use interpreted_state::{
    BACKWARDS_FACTOR, InterpretedMotionState, MAX_SIDESTEP_ANIM_RATE, RUN_ANIM_SPEED,
    RUN_TURN_FACTOR, SIDESTEP_ANIM_SPEED, SIDESTEP_FACTOR, WALK_ANIM_SPEED,
};
pub use manager::{
    MotionDoneSink, MotionTableError, MotionTableManager, MotionTableMovement, MovementType,
    NoMotionDone, PendingMotion, READY_SENTINEL,
};
pub use motion_state::{MotionEntry, MotionState};
pub use motion_table::{
    MotionTableRules, add_motion, change_cycle_speed, combine_motion, same_sign, subtract_motion,
};
pub use movement_parameters::MovementParameters;
pub use raw_motion_state::{HoldKey, RawMotionAction, RawMotionState};
pub use remote_combiner::{OffsetAdjuster, compose_offset, compute_offset};
pub use sequence::{ANIMATION_DONE_HOOK, AnimHookQueue, Sequence};
pub use sequencer::{AnimationSequencer, PartTransform, slerp_retail_client};
