//! Movement: the motion interpreter, the move-to, sticky, constraint and
//! target managers, the local player's movement controller and the remote
//! object's motion driver.
//!
//! - [`MotionInterp`] (on any [`InterpreterHost`]) turns motion requests
//!   into the interpreted motion state, motion-table commands and body
//!   velocity.
//! - [`MoveToOps`] (on any [`MoveToHost`]) walks or turns an object to a
//!   position, a heading or another object; [`MovementOps`] routes an
//!   object's movement requests to the interpreter or the move-to.
//! - [`PositionOps`] keeps sticky follow and the server's leash;
//!   [`TargetOps`] keeps target subscriptions between objects.
//! - [`WeenieObject`] is what the interpreter asks of the game object
//!   ([`PlayerWeenie`], [`RemoteWeenie`]).
//!
//! Each manager is data; its operations are provided methods of an
//! extension trait over a host trait that hands out the object's pieces.

mod interp;
mod move_to;
mod movement_manager;
mod object;
mod player;
mod position;
mod remote;
mod target;
mod weenie;
mod weenie_error;

#[cfg(test)]
mod tests;

pub use interp::{
    CROUCH_LOWER_BOUND, CROUCH_UPPER_EXCLUSIVE, DEFAULT_JUMP_VZ, InboundInterpretedState,
    InboundMotionAction, InterpretedMotionSink, InterpreterHost, JUMP_VZ_EPSILON, MAX_JUMP_EXTENT,
    MotionInterp, MotionInterpreter, MotionNode, MovementStruct, SinkArg, StandaloneInterpreter,
};
pub use move_to::{MoveToHost, MoveToManager, MoveToNode, MoveToOps};
pub use movement_manager::{MovementManagerHost, MovementOps};
pub use object::{
    MotionSink, MoveToEvent, MovementObject, NoTargets, ObjectCx, TargetWorld,
    complete_dispatched_motions,
};
pub use player::{
    DEFAULT_PLAYER_HEIGHT, DEFAULT_PLAYER_RADIUS, HookProcessor, JumpChargeSnapshot, MovementEnv,
    MovementInput, MovementResult, PlayerMovementController, PlayerMovementOptions, PlayerState,
    RenderSteps, RootMotionSource, build_raw_motion_state, jump_refusal_text,
};
pub use position::{
    ConstraintManager, PositionManager, PositionManagerHost, PositionOps, StickyManager,
    constraint_distance,
};
pub use remote::{
    RemoteAdvance, RemoteMotionApplied, RemoteMotionDriver, RemoteMotionUpdate,
    RemotePositionOutcome, RemotePositionRoute, RemotePositionUpdate, StickyTarget,
    TeleportHookSteps, WireMotionItem, WireMotionState, apply_inbound_motion,
    inbound_interpreted_state, run_teleport_hook, should_synchronize_shadow_pose,
};
pub use target::{
    ObjectToken, PhysicsObjHost, TargetInfo, TargetManager, TargetMessage, TargetOps, TargetStatus,
    TargettedVoyeurInfo,
};
pub use weenie::{
    PlayerWeenie, RemoteWeenie, WeenieObject, get_jump_height, get_jump_power, get_run_rate,
    jump_stamina_cost,
};
pub use weenie_error::WeenieError;
