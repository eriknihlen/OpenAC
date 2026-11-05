//! A remote object's motion: accepted server motion and position updates in,
//! the object's body moved over time out. Between the server's positions
//! the body moves by its animation's root motion; interpolation (catching
//! up to the server's latest positions) replaces it while it runs; the
//! move-to, sticky and leash managers adjust it; and the movement sweep
//! collides it with the world.
//!
//! Placement (a teleport, a far snap, interpolation's blip recovery) and
//! shadow registration belong to the runtime; the driver reports when they
//! are due.

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::command::reconstruct_full_command;
use ac_motion::motion_command::NON_COMBAT;
use ac_motion::server_locomotion::{can_apply_velocity_cycle, plan_from_velocity};
use ac_motion::{
    AnimationSequencer, Frame, MotionDeltaFrame, MovementParameters, compose_offset, set_rotate,
};
use glam::{Quat, Vec3};

use crate::body::{CellPosition, PhysicsBody};
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::interpolation::{InterpolationManager, InterpolationRecoveryTarget};
use crate::movement::interp::{
    InboundInterpretedState, InboundMotionAction, InterpreterHost, MotionInterp, MovementStruct,
    SinkArg,
};
use crate::movement::movement_manager::MovementOps;
use crate::movement::object::{MotionSink, MovementObject, ObjectCx, TargetWorld};
use crate::movement::position::{PositionManagerHost, PositionOps, constraint_distance};
use crate::movement::target::{ObjectToken, TargetOps, TargetStatus};
use crate::movement::weenie::{RemoteWeenie, WeenieObject};
use crate::movement::weenie_error::WeenieError;
use crate::obj_update::{
    commit_set_position_contact_prefix, commit_set_position_post_ground, handle_all_collisions,
};
use crate::quantum::{RetailObjectActivityResult, evaluate_activity};
use crate::set_position::{
    PhysicsPlacementClass, PhysicsSetPositionFlags, PhysicsSetPositionRequest,
    PhysicsSetPositionResult,
};
use crate::state::ObjectInfoState;
use crate::transition::CollisionSphere;

/// A server velocity older than this is dropped, seconds.
const SERVER_CONTROLLED_VELOCITY_STALE_SECONDS: f64 = 0.60;
/// A position further than this from the body is snapped to, metres.
const BODY_SNAP_THRESHOLD: f32 = 4.0;
/// Positions closer together than this derive no velocity, seconds.
const MINIMUM_DERIVATION_INTERVAL: f64 = 0.001;
/// A body with a smaller radius sweeps as an ordinary person.
const MINIMUM_SWEEP_RADIUS: f32 = 0.05;

/// One queued action as the wire carries it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WireMotionItem {
    /// Command, low 16 bits.
    pub command: u16,
    /// Stamp (low 15 bits) and autonomy (bit 15).
    pub packed_sequence: u16,
    /// Speed.
    pub speed: f32,
}

/// The interpreted motion fields of a server motion update (absent fields
/// are `None`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct WireMotionState {
    /// Stance, low 16 bits (0: non-combat).
    pub stance: u16,
    /// Forward command, low 16 bits.
    pub forward_command: Option<u16>,
    /// Forward speed.
    pub forward_speed: Option<f32>,
    /// Sidestep command, low 16 bits.
    pub side_step_command: Option<u16>,
    /// Sidestep speed.
    pub side_step_speed: Option<f32>,
    /// Turn command, low 16 bits.
    pub turn_command: Option<u16>,
    /// Turn speed.
    pub turn_speed: Option<f32>,
    /// Actions.
    pub commands: Vec<WireMotionItem>,
}

/// The interpreted state an update describes: absent fields take the
/// constructor's defaults (non-combat, ready, speeds 1), commands are
/// restored to their full form (unknown forward commands take
/// `fallback_forward_class`, unknown axis commands the 0x65 class, unknown
/// actions the action class).
#[must_use]
pub fn inbound_interpreted_state(
    wire: &WireMotionState,
    fallback_forward_class: u32,
) -> InboundInterpretedState {
    let resolve_axis = |c: Option<u16>| match c {
        None | Some(0) => 0,
        Some(c) => match reconstruct_full_command(c) {
            0 => 0x6500_0000 | u32::from(c),
            r => r,
        },
    };
    let forward = match wire.forward_command {
        None | Some(0) => ac_motion::motion_command::READY,
        Some(c) => match reconstruct_full_command(c) {
            0 => {
                let class = fallback_forward_class & 0xFF00_0000;
                (if class != 0 { class } else { 0x4000_0000 }) | u32::from(c)
            }
            r => r,
        },
    };
    let actions = (!wire.commands.is_empty()).then(|| {
        wire.commands
            .iter()
            .map(|item| {
                let mut command = reconstruct_full_command(item.command);
                if command == 0 {
                    command = 0x1000_0000 | u32::from(item.command);
                }
                InboundMotionAction {
                    command,
                    stamp: i32::from(item.packed_sequence & 0x7FFF),
                    autonomous: item.packed_sequence & 0x8000 != 0,
                    speed: item.speed,
                }
            })
            .collect()
    });
    InboundInterpretedState {
        current_style: if wire.stance != 0 {
            0x8000_0000 | u32::from(wire.stance)
        } else {
            NON_COMBAT
        },
        forward_command: forward,
        forward_speed: wire.forward_speed.unwrap_or(1.0),
        side_step_command: resolve_axis(wire.side_step_command),
        side_step_speed: wire.side_step_speed.unwrap_or(1.0),
        turn_command: resolve_axis(wire.turn_command),
        turn_speed: wire.turn_speed.unwrap_or(1.0),
        actions,
    }
}

/// The object a motion update sticks the body to, with the size of its
/// body: the sticky follow keeps its gap to that body's edge, not its
/// centre.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct StickyTarget {
    /// Object id.
    pub id: u32,
    /// Its body's radius, metres.
    pub radius: f32,
    /// Its body's height, metres.
    pub height: f32,
}

/// An accepted server motion update.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RemoteMotionUpdate {
    /// The wire motion state.
    pub state: WireMotionState,
    /// Movement type (0: interpreted state; 6..9: move-to and turn-to).
    pub movement_type: u8,
    /// A move-to or turn-to the runtime built from the update (its target
    /// resolved), with the run rate it carries.
    pub move_to: Option<(MovementStruct, Option<f32>)>,
    /// Stick to this object afterwards, kept apart from its body: its id,
    /// radius and height, metres (0 when its shape is unknown). The runtime
    /// leaves it out when the object is not in the world to follow.
    pub sticky_object: Option<StickyTarget>,
    /// A standing long jump is charging.
    pub standing_long_jump: bool,
    /// Class for forward commands the catalog does not know.
    pub fallback_forward_class: u32,
}

/// What applying a motion update did.
#[derive(Debug, Clone, PartialEq)]
pub struct RemoteMotionApplied {
    /// A move-to or turn-to took it.
    pub routed_move_to: bool,
    /// The interpreted state was adopted.
    pub applied_interpreted_state: bool,
    /// The forward command before.
    pub previous_forward_command: u32,
    /// The forward command after.
    pub current_forward_command: u32,
    /// The interpreted axes once the movement under way was interrupted
    /// (a move-to's turn stopped), the sticky target released and the
    /// stance entered, before the update's own motion: what a copy of the
    /// object's animation that follows only its states must pass through
    /// to stay in step (no actions).
    pub interrupted: InboundInterpretedState,
}

impl RemoteMotionApplied {
    /// The adopted state changed the forward command.
    #[must_use]
    pub fn forward_command_changed(&self) -> bool {
        self.applied_interpreted_state
            && self.previous_forward_command != self.current_forward_command
    }
}

/// How the runtime classified an accepted position.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RemotePositionRoute {
    /// A teleport: the runtime places the body (set-position).
    Teleport,
    /// Far from the body: the runtime places it.
    FarSnap,
    /// Near: catch up by interpolation.
    NearInterpolate,
    /// Not classified: catch up by interpolation.
    UnroutedCatchUp,
    /// The body is in the air: nothing is written.
    AirborneNoOperation,
}

/// An accepted server position.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RemotePositionUpdate {
    /// World position.
    pub world_position: Vec3,
    /// Orientation.
    pub orientation: Quat,
    /// The wire cell.
    pub cell_id: u32,
    /// The velocity the update carried.
    pub wire_velocity: Option<Vec3>,
    /// The runtime's classification.
    pub route: RemotePositionRoute,
    /// The body will be advanced (it is in the active area).
    pub will_be_advanced: bool,
    /// The update says the object is on the ground.
    pub is_grounded: bool,
    /// Game time, seconds.
    pub now: f64,
}

/// What applying a position did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RemotePositionOutcome {
    /// Nothing (airborne no-op).
    Nothing,
    /// The runtime must place the body (teleport or far snap) and then call
    /// [`RemoteMotionDriver::arm_leash`].
    PlacementRequired,
    /// The body was in the air and took the pose outright.
    AirborneSnapped,
    /// The body was snapped to the position (first update, not advanced, or
    /// more than 4 m off).
    Snapped,
    /// Queued for interpolation.
    Enqueued,
}

/// What one advance did.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RemoteAdvance {
    /// The body advanced at least one quantum.
    pub advanced: bool,
    /// Quanta run.
    pub steps: i32,
    /// The body's shadow should be re-registered (cell or pose changed).
    pub shadow_sync_due: bool,
    /// Interpolation asks for a recovery placement at its target.
    pub recovery_placement_due: bool,
}

/// One remote object's motion.
#[derive(Debug)]
pub struct RemoteMotionDriver {
    /// Body, interpreter, managers and motion table.
    pub obj: MovementObject<RemoteWeenie>,
    /// Catch-up to the server's positions.
    pub interpolation: InterpolationManager,
    /// In the air (the last sweep left walkable ground).
    pub airborne: bool,
    /// The server's velocity (wire or derived).
    pub server_velocity: Vec3,
    /// A server velocity is known.
    pub has_server_velocity: bool,
    /// The last accepted server position.
    pub last_server_pos: Vec3,
    /// When it was accepted, seconds (0: never).
    pub last_server_pos_time: f64,
    /// Fastest root motion since the last position update, metres per
    /// second.
    pub max_root_motion_speed_since_last_up: f32,
    /// The size the object wears.
    pub object_scale: f32,
    /// A player (sweeps as one: players pass through each other).
    pub is_player: bool,
    /// The mover's PK bits for the sweep.
    pub mover_pvp_state: u32,
    /// Collision spheres (empty: a cylinder of the body's radius/height).
    pub sphere_list: Vec<CollisionSphere>,
    /// Sphere scale.
    pub sphere_scale: f32,
    /// Step-up height, metres.
    pub step_up_height: f32,
    /// Step-down height, metres.
    pub step_down_height: f32,
    /// Local entity id (the sweep skips it).
    pub local_entity_id: u32,
    /// What kind of object it is for a placement (a hook, a storage
    /// chest and a corpse are forced into their cell).
    pub placement_class: PhysicsPlacementClass,
    /// Stop the cycle when the server velocity goes stale.
    pub apply_stale_velocity_cycle: bool,
    /// Hidden (not drawn: no animation advance).
    pub hidden: bool,
    /// Where the shadow was last registered.
    pub last_shadow_sync_pos: Vec3,
    /// Its orientation then.
    pub last_shadow_sync_orientation: Quat,
}

impl RemoteMotionDriver {
    /// A remote body at `position` in `cell_id`: reports collisions, in
    /// contact on walkable ground, in the world, moving to things, with the
    /// position and target managers in use.
    #[must_use]
    pub fn new(token: ObjectToken, cell_id: u32, position: Vec3, orientation: Quat) -> Self {
        let mut body = PhysicsBody::new();
        body.state = PhysicsState::REPORT_COLLISIONS;
        body.transient_state =
            TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
        body.in_world = true;
        body.orientation = orientation;
        body.set_position(position);
        let mut obj = MovementObject::new(token, body, RemoteWeenie);
        obj.cell_id = cell_id;
        obj.contact_means_walkable = true;
        obj.can_move_to = true;
        obj.position_attached = true;
        obj.targeting_attached = true;
        Self {
            obj,
            interpolation: InterpolationManager::new(),
            airborne: false,
            server_velocity: Vec3::ZERO,
            has_server_velocity: false,
            last_server_pos: Vec3::ZERO,
            last_server_pos_time: 0.0,
            max_root_motion_speed_since_last_up: 0.0,
            object_scale: 1.0,
            is_player: (token.id & 0xFF00_0000) == 0x5000_0000,
            mover_pvp_state: 0,
            sphere_list: Vec::new(),
            sphere_scale: 1.0,
            step_up_height: 0.4,
            step_down_height: 0.4,
            local_entity_id: 0,
            placement_class: PhysicsPlacementClass::Ordinary,
            apply_stale_velocity_cycle: false,
            hidden: false,
            last_shadow_sync_pos: position,
            last_shadow_sync_orientation: orientation,
        }
    }

    /// Animate the object with `sequencer` (its motions go to it, finished
    /// motions come back from it).
    pub fn attach_sequencer(&mut self, sequencer: AnimationSequencer) {
        self.obj.sequencer = Some(sequencer);
        self.obj.sink = MotionSink::Sequencer;
    }

    /// Run operations on the object against `world`.
    pub fn cx<'a>(&'a mut self, world: &'a dyn TargetWorld) -> ObjectCx<'a, RemoteWeenie> {
        self.obj.cx(world)
    }

    fn mover_flags(&self) -> ObjectInfoState {
        let base = if self.is_player {
            ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE
        } else {
            ObjectInfoState::EDGE_SLIDE
        };
        ObjectInfoState(base | self.mover_pvp_state)
    }

    // ── motion updates ─────────────────────────────────────────────────

    /// Apply an accepted motion update: cancel the move-to and release the
    /// sticky target, enter the stance, then either start the move-to or
    /// turn-to it carries, or (movement type 0) adopt its interpreted state
    /// and replay its actions, stick to its sticky object and record the
    /// standing long jump.
    pub fn apply_motion(
        &mut self,
        world: &dyn TargetWorld,
        now: f64,
        update: &RemoteMotionUpdate,
    ) -> RemoteMotionApplied {
        self.obj.sim_time = now;
        apply_inbound_motion(&mut self.obj.cx(world), update)
    }
}

/// Apply an accepted motion update to any object's managers (another
/// object's body, or the character's own when the server moves it): cancel
/// the move-to and release the sticky target, enter the stance, then either
/// start the move-to or turn-to it carries, or (movement type 0) adopt its
/// interpreted state and replay its actions, stick to its sticky object and
/// record the standing long jump.
pub fn apply_inbound_motion<W: WeenieObject>(
    cx: &mut ObjectCx<'_, W>,
    update: &RemoteMotionUpdate,
) -> RemoteMotionApplied {
    {
        let previous_forward = cx.obj.interp.interpreted_state.forward_command;
        cx.interrupt_current_movement();
        cx.unstick_from_object();
        let wire_style = if update.state.stance != 0 {
            0x8000_0000 | u32::from(update.state.stance)
        } else {
            NON_COMBAT
        };
        if cx.obj.interp.interpreted_state.current_style != wire_style {
            cx.do_motion(wire_style, &MovementParameters::default());
        }
        let interrupted = {
            let s = &cx.obj.interp.interpreted_state;
            InboundInterpretedState {
                current_style: s.current_style,
                forward_command: s.forward_command,
                forward_speed: s.forward_speed,
                side_step_command: s.side_step_command,
                side_step_speed: s.side_step_speed,
                turn_command: s.turn_command,
                turn_speed: s.turn_speed,
                actions: None,
            }
        };
        if let Some((walk, run_rate)) = &update.move_to {
            if let Some(rate) = run_rate {
                cx.obj.interp.my_run_rate = *rate;
            }
            cx.movement_perform(walk);
            return RemoteMotionApplied {
                routed_move_to: true,
                applied_interpreted_state: false,
                previous_forward_command: previous_forward,
                current_forward_command: cx.obj.interp.interpreted_state.forward_command,
                interrupted,
            };
        }
        if update.movement_type != 0 {
            return RemoteMotionApplied {
                routed_move_to: false,
                applied_interpreted_state: false,
                previous_forward_command: previous_forward,
                current_forward_command: cx.obj.interp.interpreted_state.forward_command,
                interrupted,
            };
        }
        let interpreted = inbound_interpreted_state(&update.state, update.fallback_forward_class);
        cx.move_to_interpreted_state(&interpreted, SinkArg::Default);
        if let Some(sticky) = update.sticky_object.filter(|s| s.id != 0) {
            cx.stick_to(sticky.id, sticky.radius, sticky.height);
        }
        cx.obj.interp.standing_long_jump = update.standing_long_jump;
        RemoteMotionApplied {
            routed_move_to: false,
            applied_interpreted_state: true,
            previous_forward_command: previous_forward,
            current_forward_command: interpreted.forward_command,
            interrupted,
        }
    }
}

impl RemoteMotionDriver {
    // ── position updates ───────────────────────────────────────────────

    /// Apply an accepted server position: derive the server velocity (from
    /// the wire, else from the last position; none for a teleport), then
    /// route it — a teleport is the runtime's placement; a body out of
    /// contact takes the pose outright; a far snap is the runtime's
    /// placement; otherwise the body snaps on the
    /// first update, when it will not be advanced or when more than 4 m off,
    /// and catches up by interpolation when near. The wire cell is adopted
    /// when the body took the pose; the leash is re-armed after an operation.
    pub fn apply_server_position(
        &mut self,
        update: &RemotePositionUpdate,
    ) -> RemotePositionOutcome {
        let teleport = update.route == RemotePositionRoute::Teleport;
        if update.route == RemotePositionRoute::AirborneNoOperation
            || (!update.is_grounded && !teleport)
        {
            self.obj.cell_id = update.cell_id;
            self.stamp(update.world_position, update.now);
            return RemotePositionOutcome::Nothing;
        }
        self.derive_velocity(
            update.wire_velocity,
            update.world_position,
            update.now,
            teleport,
        );
        // A teleport is placed whatever the body is doing; a body out of
        // contact takes any other pose outright, a far one included; a
        // far snap in contact is placed.
        let outcome = if teleport {
            RemotePositionOutcome::PlacementRequired
        } else if !self.obj.body.in_contact() {
            self.obj.body.set_position(update.world_position);
            self.obj.body.orientation = update.orientation;
            RemotePositionOutcome::AirborneSnapped
        } else if update.route == RemotePositionRoute::FarSnap {
            RemotePositionOutcome::PlacementRequired
        } else {
            self.apply_interpolate(update)
        };
        if outcome != RemotePositionOutcome::PlacementRequired {
            self.arm_leash();
        }
        if matches!(
            outcome,
            RemotePositionOutcome::Snapped | RemotePositionOutcome::AirborneSnapped
        ) {
            self.obj.cell_id = update.cell_id;
        }
        self.stamp(update.world_position, update.now);
        outcome
    }

    fn stamp(&mut self, world_position: Vec3, now: f64) {
        self.last_server_pos = world_position;
        self.last_server_pos_time = now;
    }

    fn derive_velocity(
        &mut self,
        wire_velocity: Option<Vec3>,
        world_position: Vec3,
        now: f64,
        teleport: bool,
    ) {
        if teleport {
            return;
        }
        let mut velocity = wire_velocity;
        if velocity.is_none() && self.last_server_pos_time > 0.0 {
            let elapsed = now - self.last_server_pos_time;
            if elapsed > MINIMUM_DERIVATION_INTERVAL {
                velocity = Some((world_position - self.last_server_pos) / elapsed as f32);
            }
        }
        match velocity {
            Some(v) => {
                self.server_velocity = v;
                self.has_server_velocity = true;
            }
            None => {
                self.server_velocity = Vec3::ZERO;
                self.has_server_velocity = false;
            }
        }
    }

    fn apply_interpolate(&mut self, update: &RemotePositionUpdate) -> RemotePositionOutcome {
        let first_up = self.last_server_pos_time <= 0.0;
        let body_to_target = self.obj.body.position().distance(update.world_position);
        if first_up || !update.will_be_advanced || body_to_target > BODY_SNAP_THRESHOLD {
            self.interpolation.clear();
            self.obj.body.set_position(update.world_position);
            self.obj.body.orientation = update.orientation;
            return RemotePositionOutcome::Snapped;
        }
        let is_moving_to = self.obj.move_to_made() && self.obj.move_to.is_moving_to();
        let blip = if (self.obj.cell_id & 0xFFFF) >= 0x100 {
            20.0
        } else {
            100.0
        };
        if let Some(close) = self.interpolation.enqueue(
            update.world_position,
            update.orientation,
            is_moving_to,
            Some(self.obj.body.position()),
            Some(self.obj.body.orientation),
            update.cell_id,
            blip,
        ) {
            self.obj.body.orientation = close;
        }
        RemotePositionOutcome::Enqueued
    }

    /// Put the leash on at the body's position (after a placement or an
    /// accepted position).
    pub fn arm_leash(&mut self) {
        if !self.obj.position_attached {
            return;
        }
        let anchor = self.obj.position();
        self.obj
            .cx(&crate::movement::object::NoTargets)
            .constrain_to(
                anchor,
                constraint_distance::get_start_constraint_distance(anchor.obj_cell_id),
                constraint_distance::get_max_constraint_distance(anchor.obj_cell_id),
            );
    }

    /// A teleport reached this body: run the teleport steps on it before
    /// it is placed (its collision reports are the runtime's; this body has
    /// none to end).
    pub fn teleport_hook(&mut self) {
        run_teleport_hook(&mut DriverTeleportSteps(self));
    }
}

/// The steps a teleport takes on a body.
pub trait TeleportHookSteps {
    /// Cancel a move-to with `error`.
    fn cancel_move_to(&mut self, error: WeenieError);
    /// Let go of the object it is stuck to.
    fn un_stick(&mut self);
    /// Drop the interpolation queue.
    fn stop_interpolating(&mut self);
    /// Take the leash off.
    fn un_constrain(&mut self);
    /// Clear its target and tell its watchers it teleported.
    fn notify_teleported(&mut self);
    /// End its collision reports.
    fn report_collision_end(&mut self);
}

/// Run the teleport steps in the game's order, cancelling a move-to with
/// "you teleported".
pub fn run_teleport_hook(steps: &mut impl TeleportHookSteps) {
    steps.cancel_move_to(WeenieError::I_TELEPORTED);
    steps.un_stick();
    steps.stop_interpolating();
    steps.un_constrain();
    steps.notify_teleported();
    steps.report_collision_end();
}

struct DriverTeleportSteps<'a>(&'a mut RemoteMotionDriver);

impl TeleportHookSteps for DriverTeleportSteps<'_> {
    fn cancel_move_to(&mut self, error: WeenieError) {
        self.0
            .obj
            .cx(&crate::movement::object::NoTargets)
            .movement_cancel_move_to(error);
    }
    fn un_stick(&mut self) {
        self.0
            .obj
            .cx(&crate::movement::object::NoTargets)
            .un_stick();
    }
    fn stop_interpolating(&mut self) {
        self.0.interpolation.clear();
    }
    fn un_constrain(&mut self) {
        self.0
            .obj
            .cx(&crate::movement::object::NoTargets)
            .position_manager()
            .un_constrain();
    }
    fn notify_teleported(&mut self) {
        let mut cx = self.0.obj.cx(&crate::movement::object::NoTargets);
        cx.clear_target();
        cx.notify_voyeur_of_event(TargetStatus::Teleported);
    }
    fn report_collision_end(&mut self) {}
}

impl RemoteMotionDriver {
    fn compose_and_apply(
        &mut self,
        dt: f32,
        delta: &mut MotionDeltaFrame,
        world: &dyn TargetWorld,
    ) {
        let max_speed = self.obj.cx(world).get_adjusted_max_speed();
        let in_contact = self.obj.body.in_contact();
        let is_sticky = self.obj.position.get_sticky_object_id() != 0;
        let root = *delta;
        compose_offset(
            f64::from(dt),
            self.obj.body.position(),
            self.obj.body.orientation,
            &root,
            &mut self.interpolation,
            max_speed,
            delta,
            in_contact,
            is_sticky,
        );
        if self.obj.position_attached {
            self.obj
                .cx(world)
                .position_adjust_offset(delta, f64::from(dt));
            self.obj.body.is_fully_constrained = self.obj.position.is_fully_constrained();
        }
        apply_delta(&mut self.obj.body, delta);
    }

    fn sweep_request(&self, current: Vec3, target: Vec3, on_ground: bool) -> ResolveRequest {
        let (mut radius, mut height) = (self.obj.radius, self.obj.height);
        if radius < MINIMUM_SWEEP_RADIUS {
            radius = crate::movement::player::DEFAULT_PLAYER_RADIUS;
            height = crate::movement::player::DEFAULT_PLAYER_HEIGHT;
        }
        ResolveRequest {
            current_pos: current,
            target_pos: target,
            cell_id: self.obj.cell_id,
            sphere_radius: radius,
            sphere_height: height,
            step_up_height: self.step_up_height,
            step_down_height: self.step_down_height,
            is_on_ground: on_ground,
            mover_flags: self.mover_flags(),
            moving_entity_id: self.local_entity_id,
            sphere_list: self.sphere_list.clone(),
            sphere_scale: self.sphere_scale,
            ..ResolveRequest::default()
        }
    }

    fn manager_tail(&mut self, world: &dyn TargetWorld) -> bool {
        let mut cx = self.obj.cx(world);
        if cx.obj.targeting_attached {
            cx.handle_targetting();
        }
        cx.movement_use_time();
        if cx.obj.sequencer.is_some() {
            InterpreterHost::check_for_completed_motions(&mut cx);
        }
        let recovery = self.interpolation.try_get_recovery_target().is_some();
        let mut cx = self.obj.cx(world);
        if cx.obj.position_attached {
            cx.position_use_time();
        }
        recovery
    }

    fn should_sync_shadow(&self, cell_changed: bool) -> bool {
        cell_changed
            || should_synchronize_shadow_pose(
                self.obj.body.position(),
                self.obj.body.orientation,
                self.last_shadow_sync_pos,
                self.last_shadow_sync_orientation,
            )
    }

    /// The runtime re-registered the shadow at the body's pose.
    pub fn note_shadow_synced(&mut self) {
        self.last_shadow_sync_pos = self.obj.body.position();
        self.last_shadow_sync_orientation = self.obj.body.orientation;
    }

    /// Advance the object by `elapsed` seconds: the activity gate (asleep
    /// beyond 96 m of the player, static or not in a root cell), then per
    /// quantum the animation's root motion (or, hidden, none) through
    /// interpolation, the managers, integration and the sweep, then the
    /// manager tail.
    #[allow(clippy::too_many_arguments)]
    pub fn advance(
        &mut self,
        engine: Option<&PhysicsEngine>,
        world: &dyn TargetWorld,
        elapsed: f32,
        now: f64,
        player_position: Option<Vec3>,
        root_clock_advances: bool,
        has_part_array: bool,
    ) -> RemoteAdvance {
        let mut out = RemoteAdvance::default();
        self.obj.sim_time = now;
        let is_static = self.obj.body.state.contains(PhysicsState::STATIC);
        let position = self.obj.body.position();
        let gate = evaluate_activity(
            &mut self.obj.clock,
            Some(&mut self.obj.body),
            root_clock_advances,
            has_part_array,
            is_static,
            position,
            player_position,
            f64::from(elapsed),
        );
        if gate != RetailObjectActivityResult::Active {
            return out;
        }
        let batch = self.obj.clock.advance(f64::from(elapsed));
        if batch.count() == 0 {
            return out;
        }
        for qi in 0..batch.count() {
            let step = batch.get_quantum(qi).unwrap_or(0.0);
            let (sync, recovery) = if self.hidden {
                self.tick_hidden(engine, world, step, now)
            } else {
                let mut root = Frame::IDENTITY;
                if let Some(seq) = self.obj.sequencer.as_mut() {
                    seq.advance_root_motion_only(step, Some(&mut root));
                }
                if step > 0.0 {
                    let speed = root.origin.length() * self.object_scale / step;
                    self.max_root_motion_speed_since_last_up =
                        self.max_root_motion_speed_since_last_up.max(speed);
                }
                let delta = MotionDeltaFrame {
                    origin: root.origin,
                    orientation: root.orientation,
                };
                self.tick_body(engine, world, step, now, delta)
            };
            out.shadow_sync_due |= sync;
            out.recovery_placement_due |= recovery;
        }
        out.advanced = true;
        out.steps = batch.count();
        out
    }

    /// One visible quantum: root motion (scaled, only on walkable ground),
    /// a stale server velocity dropped, interpolation, managers,
    /// integration, hooks, the sweep and its ground edges, then the tail.
    /// Returns (shadow sync due, recovery placement due).
    pub fn tick_body(
        &mut self,
        engine: Option<&PhysicsEngine>,
        world: &dyn TargetWorld,
        dt: f32,
        now: f64,
        root_motion: MotionDeltaFrame,
    ) -> (bool, bool) {
        self.obj.sim_time = now;
        let on_walkable_at_start = self.obj.body.on_walkable();
        let scaled_origin = if on_walkable_at_start {
            root_motion.origin * self.object_scale
        } else {
            Vec3::ZERO
        };
        self.obj.body.transient_state.insert(TransientState::ACTIVE);

        if !self.airborne {
            let move_to_armed = self.obj.move_to_made() && self.obj.move_to.is_moving_to();
            let sticky_armed = self.obj.position.get_sticky_object_id() != 0;
            if !self.is_player && self.has_server_velocity && !move_to_armed && !sticky_armed {
                let age = now - self.last_server_pos_time;
                if age > SERVER_CONTROLLED_VELOCITY_STALE_SECONDS {
                    self.server_velocity = Vec3::ZERO;
                    self.has_server_velocity = false;
                    if self.apply_stale_velocity_cycle {
                        self.apply_velocity_cycle(Vec3::ZERO);
                    }
                }
            }
        }

        let pre_integrate = self.obj.body.position();
        let mut delta = MotionDeltaFrame {
            origin: scaled_origin,
            orientation: root_motion.orientation,
        };
        self.compose_and_apply(dt, &mut delta, world);
        self.obj.body.calc_acceleration();
        self.obj.body.update_physics_internal(dt);
        self.obj.cx(world).take_reached_points();

        let post_integrate = self.obj.body.position();
        let mut committed_cell = self.obj.cell_id;
        if let Some(engine) = engine.filter(|e| self.obj.cell_id != 0 && e.landblock_count() > 0) {
            let previous_contact = self.obj.body.in_contact();
            let previous_on_walkable = self.obj.body.on_walkable();
            let request = self.sweep_request(pre_integrate, post_integrate, previous_on_walkable);
            let resolved = engine.resolve_with_transition(&request, Some(&mut self.obj.body));
            committed_cell = commit_sweep_outcome(
                &mut self.obj.body,
                &resolved,
                pre_integrate,
                dt,
                committed_cell,
            );
            let candidate_moved = post_integrate != pre_integrate;
            if resolved.ok && candidate_moved {
                let final_on_walkable = commit_set_position_contact_prefix(
                    &mut self.obj.body,
                    resolved.in_contact,
                    resolved.on_walkable,
                    previous_on_walkable,
                );
                if !previous_on_walkable && final_on_walkable {
                    self.obj.cx(world).movement_hit_ground();
                    self.interpolation.clear();
                } else if previous_on_walkable && !final_on_walkable {
                    self.obj.cx(world).leave_ground();
                }
                commit_set_position_post_ground(&mut self.obj.body);
                let now_on_walkable = self.obj.body.on_walkable();
                handle_all_collisions(
                    &mut self.obj.body,
                    resolved.collision_normal_valid,
                    resolved.collision_normal,
                    previous_contact,
                    previous_on_walkable,
                    now_on_walkable,
                );
                self.airborne = !self.obj.body.on_walkable();
            }
        }

        let cell_changed = committed_cell != 0 && committed_cell != self.obj.cell_id;
        if cell_changed {
            self.obj.cell_id = committed_cell;
        }
        let sync = self.should_sync_shadow(cell_changed);
        let recovery = self.manager_tail(world);
        (sync, recovery)
    }

    /// One hidden quantum: no root motion; interpolation and the managers
    /// move the body, the sweep runs only when it moved.
    pub fn tick_hidden(
        &mut self,
        engine: Option<&PhysicsEngine>,
        world: &dyn TargetWorld,
        dt: f32,
        now: f64,
    ) -> (bool, bool) {
        self.obj.sim_time = now;
        let pre_compose = self.obj.body.position();
        let mut delta = MotionDeltaFrame::default();
        self.compose_and_apply(dt, &mut delta, world);
        self.obj.cx(world).take_reached_points();
        let composed = self.obj.body.position();
        let mut committed_cell = self.obj.cell_id;
        if let Some(engine) = engine
            .filter(|e| self.obj.cell_id != 0 && composed != pre_compose && e.landblock_count() > 0)
        {
            let previous_contact = self.obj.body.in_contact();
            let previous_on_walkable = self.obj.body.on_walkable();
            let request = self.sweep_request(pre_compose, composed, previous_on_walkable);
            let resolved = engine.resolve_with_transition(&request, Some(&mut self.obj.body));
            self.obj.body.set_position(resolved.position);
            if resolved.cell_id != 0 {
                committed_cell = resolved.cell_id;
            }
            let final_on_walkable = commit_set_position_contact_prefix(
                &mut self.obj.body,
                resolved.in_contact,
                resolved.on_walkable,
                previous_on_walkable,
            );
            if !previous_on_walkable && final_on_walkable {
                self.obj.cx(world).movement_hit_ground();
            } else if previous_on_walkable && !final_on_walkable {
                self.obj.cx(world).leave_ground();
            }
            commit_set_position_post_ground(&mut self.obj.body);
            let now_on_walkable = self.obj.body.on_walkable();
            handle_all_collisions(
                &mut self.obj.body,
                resolved.collision_normal_valid,
                resolved.collision_normal,
                previous_contact,
                previous_on_walkable,
                now_on_walkable,
            );
            self.airborne = !self.obj.body.on_walkable();
        }
        if committed_cell != 0 && committed_cell != self.obj.cell_id {
            self.obj.cell_id = committed_cell;
        }
        let recovery = self.manager_tail(world);
        (false, recovery)
    }

    /// Play the cycle the server velocity implies (non-players, on the
    /// ground, not moving to something, playing ready or locomotion).
    pub fn apply_velocity_cycle(&mut self, velocity: Vec3) {
        if self.airborne || self.is_player {
            return;
        }
        if self.obj.move_to_made() && self.obj.move_to.is_moving_to() {
            return;
        }
        let Some(seq) = self.obj.sequencer.as_mut() else {
            return;
        };
        let current = seq.current_motion();
        if !can_apply_velocity_cycle(current) {
            return;
        }
        let plan = plan_from_velocity(velocity);
        let style = if seq.current_style() != 0 {
            seq.current_style()
        } else {
            NON_COMBAT
        };
        // The table's refusal leaves the cycle as it was.
        let _ = seq.set_cycle(style, plan.motion, plan.speed_mod);
    }

    /// Where the object is (cell, world origin, orientation).
    #[must_use]
    pub fn position(&self) -> CellPosition {
        self.obj.position()
    }

    /// Apply the server's velocity and spin (an accepted vector update):
    /// the body takes them, a sleeping non-static body wakes with its clock
    /// rebased to `now`, and an upward velocity above 0.5 m/s lifts it off
    /// the ground (an animated body leaves the ground through its
    /// interpreter and then takes the server's velocity again). Returns
    /// false for a non-finite vector, which changes nothing.
    pub fn apply_authoritative_vector(
        &mut self,
        world: &dyn TargetWorld,
        velocity: Vec3,
        omega: Vec3,
        now: f64,
    ) -> bool {
        if !velocity.is_finite() || !omega.is_finite() || !now.is_finite() {
            return false;
        }
        self.commit_authoritative_vector(velocity, omega, now);
        if velocity.z > 0.5 {
            self.airborne = true;
            self.obj
                .body
                .transient_state
                .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
            if self.obj.sequencer.is_some() {
                self.obj.cx(world).leave_ground();
                self.commit_authoritative_vector(velocity, omega, now);
            }
        }
        true
    }

    fn commit_authoritative_vector(&mut self, velocity: Vec3, omega: Vec3, now: f64) {
        let body = &mut self.obj.body;
        let was_active = body.transient_state.contains(TransientState::ACTIVE);
        body.set_velocity(velocity);
        if body.state.contains(PhysicsState::STATIC) {
            if !was_active {
                body.transient_state.remove(TransientState::ACTIVE);
            }
        } else {
            let reactivated = self.obj.clock.activate();
            if reactivated || !was_active {
                self.obj.body.last_update_time = now;
            }
        }
        self.obj.body.omega = omega;
    }

    /// The placement a stalled interpolation asks for: a teleport to its
    /// target that may slide aside, or `None` when it asks for none. The
    /// caller places the body (`PhysicsEngine::set_position`) and, once
    /// it is placed, calls [`Self::complete_recovery`].
    #[must_use]
    pub fn recovery_request(
        &self,
        landblock_origin: impl Fn(u32) -> Option<Vec3>,
    ) -> Option<(InterpolationRecoveryTarget, PhysicsSetPositionRequest)> {
        let target = self.interpolation.try_get_recovery_target()?;
        if target.cell_id == 0 {
            return None;
        }
        let origin = landblock_origin(target.cell_id)?;
        let (spheres, scale) = if self.sphere_list.is_empty() {
            let (mut radius, mut height) = (self.obj.radius, self.obj.height);
            if radius < MINIMUM_SWEEP_RADIUS {
                radius = crate::movement::player::DEFAULT_PLAYER_RADIUS;
                height = crate::movement::player::DEFAULT_PLAYER_HEIGHT;
            }
            let spheres = if height > 0.0 {
                vec![
                    CollisionSphere::new(Vec3::new(0.0, 0.0, radius), radius),
                    CollisionSphere::new(Vec3::new(0.0, 0.0, height - radius), radius),
                ]
            } else {
                vec![CollisionSphere::new(Vec3::new(0.0, 0.0, radius), radius)]
            };
            (spheres, 1.0)
        } else {
            (self.sphere_list.clone(), self.sphere_scale)
        };
        Some((
            target,
            PhysicsSetPositionRequest {
                position: target.position,
                orientation: target.orientation,
                cell_id: target.cell_id,
                cell_local_position: target.position - Vec3::new(origin.x, origin.y, 0.0),
                spheres,
                scale,
                step_up_height: self.step_up_height,
                step_down_height: self.step_down_height,
                mover_physics_state: self.obj.body.state,
                mover_flags: self.mover_flags(),
                moving_entity_id: self.local_entity_id,
                flags: PhysicsSetPositionFlags(
                    PhysicsSetPositionFlags::TELEPORT
                        | PhysicsSetPositionFlags::SLIDE
                        | PhysicsSetPositionFlags::SEND_POSITION_EVENT,
                ),
                placement_class: self.placement_class,
                ..PhysicsSetPositionRequest::default()
            },
        ))
    }

    /// A placement of an accepted server position committed (a teleport or
    /// a far snap, placed with teleport | slide): the body takes the placed
    /// pose, cell, contact plane and sliding normal, its contact bits commit
    /// with their ground edges, the collision it met acts on its velocity,
    /// its stationary-fall count is the placement's, and the placed spot is
    /// the last server position until the caller stamps the server's own
    /// ([`Self::stamp_server_position`]). A placement that found no spot
    /// changes nothing (false).
    pub fn commit_placement(
        &mut self,
        world: &dyn TargetWorld,
        placed: &PhysicsSetPositionResult,
        now: f64,
    ) -> bool {
        if !placed.is_committed() {
            return false;
        }
        let body = &mut self.obj.body;
        let previous_contact = body.in_contact();
        let previous_on_walkable = body.on_walkable();
        body.orientation = placed.orientation;
        body.snap_to_cell(placed.cell_id, placed.position, placed.cell_local_position);
        if placed.forced_into_cell {
            // Forced into its cell: the frame and the cell only; the body
            // keeps the contact it had.
            self.obj.cell_id = placed.cell_id;
            self.last_server_pos = placed.position;
            self.last_server_pos_time = now;
            self.last_shadow_sync_pos = placed.position;
            self.last_shadow_sync_orientation = placed.orientation;
            return true;
        }
        body.contact_plane_valid = placed.in_contact;
        body.contact_plane = placed.contact_plane;
        body.contact_plane_cell_id = placed.contact_plane_cell_id;
        body.contact_plane_is_water = placed.contact_plane_is_water;
        if placed.in_contact {
            body.ground_normal = placed.contact_plane.normal;
        }
        body.sliding_normal = placed.sliding_normal;
        body.transient_state
            .set(TransientState::SLIDING, placed.sliding_normal_valid);
        self.obj.cell_id = placed.cell_id;
        self.last_server_pos = placed.position;
        self.last_server_pos_time = now;
        self.last_shadow_sync_pos = placed.position;
        self.last_shadow_sync_orientation = placed.orientation;
        let final_on_walkable = commit_set_position_contact_prefix(
            &mut self.obj.body,
            placed.in_contact,
            placed.on_walkable,
            previous_on_walkable,
        );
        if !previous_on_walkable && final_on_walkable {
            self.obj.cx(world).movement_hit_ground();
        } else if previous_on_walkable && !final_on_walkable {
            self.obj.cx(world).leave_ground();
        }
        commit_set_position_post_ground(&mut self.obj.body);
        // The collision reports between these steps are not ported (not
        // needed here).
        let body = &mut self.obj.body;
        body.frames_stationary_fall = placed.frames_stationary_fall;
        let now_on_walkable = body.on_walkable();
        handle_all_collisions(
            body,
            placed.collision_normal_valid,
            placed.collision_normal,
            previous_contact,
            previous_on_walkable,
            now_on_walkable,
        );
        body.transient_state.remove(
            TransientState::STATIONARY_FALL
                | TransientState::STATIONARY_STOP
                | TransientState::STATIONARY_STUCK,
        );
        match placed.frames_stationary_fall {
            1 => body.transient_state.insert(TransientState::STATIONARY_FALL),
            2 => body.transient_state.insert(TransientState::STATIONARY_STOP),
            3 => body
                .transient_state
                .insert(TransientState::STATIONARY_STUCK),
            _ => {}
        }
        self.airborne = !self.obj.body.on_walkable();
        true
    }

    /// Write down the server's position and when it arrived, after the
    /// body was placed for it: the next position without a velocity
    /// derives its speed from where the server said, not from where the
    /// placement slid the body.
    pub fn stamp_server_position(&mut self, world_position: Vec3, now: f64) {
        self.stamp(world_position, now);
    }

    /// The recovery placement for `target` committed: the body takes the
    /// placed cell, position, orientation and contact, and the queue is
    /// cleared (a target from an older queue is refused, false).
    pub fn complete_recovery(
        &mut self,
        target: InterpolationRecoveryTarget,
        placed: &PhysicsSetPositionResult,
    ) -> bool {
        if !placed.is_committed() {
            return false;
        }
        if !self.interpolation.complete_recovery(target) {
            return false;
        }
        let body = &mut self.obj.body;
        let previous_on_walkable = body.on_walkable();
        body.orientation = placed.orientation;
        body.snap_to_cell(placed.cell_id, placed.position, placed.cell_local_position);
        if placed.forced_into_cell {
            // Forced into its cell: the frame and the cell only; the body
            // keeps the contact it had.
            self.obj.cell_id = placed.cell_id;
            return true;
        }
        body.contact_plane_valid = placed.in_contact;
        body.contact_plane = placed.contact_plane;
        body.contact_plane_cell_id = placed.contact_plane_cell_id;
        body.contact_plane_is_water = placed.contact_plane_is_water;
        if placed.in_contact {
            body.ground_normal = placed.contact_plane.normal;
        }
        commit_set_position_contact_prefix(
            body,
            placed.in_contact,
            placed.on_walkable,
            previous_on_walkable,
        );
        self.obj.cell_id = placed.cell_id;
        self.airborne = !self.obj.body.on_walkable();
        true
    }
}

fn apply_delta(body: &mut PhysicsBody, delta: &MotionDeltaFrame) {
    if delta.origin != Vec3::ZERO {
        let p = body.position() + body.orientation * delta.origin;
        body.set_position(p);
    }
    if delta.orientation != Quat::IDENTITY {
        body.orientation = set_rotate(
            body.position(),
            body.orientation,
            body.orientation * delta.orientation,
        );
    }
}

/// Commit one quantum's sweep: a successful sweep moves the body to the
/// resolved point with the step's velocity and adopts its cell (when it
/// names one); a failed sweep holds the body at `pre_step` with no step
/// velocity and keeps `committed_cell`. Returns the cell.
pub(crate) fn commit_sweep_outcome(
    body: &mut PhysicsBody,
    resolved: &ResolveResult,
    pre_step: Vec3,
    dt: f32,
    committed_cell: u32,
) -> u32 {
    if !resolved.ok {
        body.set_position(pre_step);
        body.cached_velocity = Vec3::ZERO;
        return committed_cell;
    }
    body.set_position(resolved.position);
    body.cached_velocity = if dt > 0.0 {
        (resolved.position - pre_step) / dt
    } else {
        Vec3::ZERO
    };
    if resolved.cell_id != 0 {
        resolved.cell_id
    } else {
        committed_cell
    }
}

/// Whether a shadow at (`last_position`, `last_orientation`) is stale for a
/// body at (`current_position`, `current_orientation`): moved more than 1 cm,
/// turned (aligned below 0.99999) or an orientation unusable.
#[must_use]
pub fn should_synchronize_shadow_pose(
    current_position: Vec3,
    current_orientation: Quat,
    last_position: Vec3,
    last_orientation: Quat,
) -> bool {
    if current_position.distance_squared(last_position) > 1e-4 {
        return true;
    }
    let cl = current_orientation.length_squared();
    let ll = last_orientation.length_squared();
    if !cl.is_finite() || !ll.is_finite() || cl < 1e-12 || ll < 1e-12 {
        return true;
    }
    let dot = (current_orientation.dot(last_orientation) / (cl * ll).sqrt()).abs();
    !dot.is_finite() || dot < 0.99999
}
