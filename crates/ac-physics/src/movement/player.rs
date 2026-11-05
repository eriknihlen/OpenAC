//! The local player's movement controller: input edges become raw motion
//! commands through the movement manager (the interpreter normalises them),
//! each physics quantum moves the body by the animation's root motion (or,
//! without animation, by the state velocity) through the collision sweep,
//! and each update reports what the outbound layer should send: a movement
//! event (MoveToState) on motion changes and the mouse-look cadence, a jump,
//! and an autonomous position when the position or contact plane changed.

use ac_core::combat::{
    ATTACK_POWER_UP_SECONDS, DUAL_WIELD_COMBAT_STYLE, DUAL_WIELD_POWER_UP_SECONDS,
};
use ac_core::physics::is_valid_position;
use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::motion_command::{
    CROUCH, READY, SIDE_STEP_LEFT, SIDE_STEP_RIGHT, SITTING, SLEEPING, TURN_LEFT, TURN_RIGHT,
    WALK_BACKWARDS, WALK_FORWARD,
};
use ac_motion::move_to_math::{
    EPSILON as MOVE_TO_EPSILON, get_heading, heading_from_yaw, set_heading, yaw_from_heading,
};
use ac_motion::{
    HoldKey, MotionDeltaFrame, MovementParameters, MovementType, RUN_ANIM_SPEED, RawMotionState,
    set_rotate,
};
use glam::{Quat, Vec3};

use crate::body::{CellPosition, PhysicsBody};
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::Plane;
use crate::movement::interp::{MotionInterp, MovementStruct};
use crate::movement::movement_manager::MovementOps;
use crate::movement::object::{MovementObject, TargetWorld};
use crate::movement::position::{PositionManagerHost, PositionOps, constraint_distance};
use crate::movement::target::{ObjectToken, TargetOps};
use crate::movement::weenie::{PlayerWeenie, WeenieObject};
use crate::movement::weenie_error::WeenieError;
use crate::obj_update::handle_all_collisions;
use crate::quantum::RetailObjectQuantumBatch;
use crate::state::ObjectInfoState;
use crate::transition::CollisionSphere;

/// Half the width of an ordinary person, metres.
pub const DEFAULT_PLAYER_RADIUS: f32 = 0.48;
/// The height of an ordinary person, metres.
pub const DEFAULT_PLAYER_HEIGHT: f32 = 1.835;

/// One update's input.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MovementInput {
    /// Forward key held.
    pub forward: bool,
    /// Backward key held.
    pub backward: bool,
    /// Strafe-left key held.
    pub strafe_left: bool,
    /// Strafe-right key held.
    pub strafe_right: bool,
    /// Turn-left key held.
    pub turn_left: bool,
    /// Turn-right key held.
    pub turn_right: bool,
    /// Run toggle on.
    pub run: bool,
    /// Mouse x movement (unused by movement).
    pub mouse_delta_x: f32,
    /// Jump key held.
    pub jump: bool,
    /// A retained automation command (replaces the directional intent).
    pub is_persistent_command: bool,
    /// The extent a scripted jump release leaves at.
    pub jump_extent: Option<f32>,
}

/// Run and jump skills the controller starts with.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlayerMovementOptions {
    /// Run skill.
    pub run_skill: i32,
    /// Jump skill.
    pub jump_skill: i32,
}

impl PlayerMovementOptions {
    /// Run skill used until the character's is known.
    pub const FALLBACK_RUN_SKILL: i32 = 200;
    /// Jump skill used until the character's is known.
    pub const FALLBACK_JUMP_SKILL: i32 = 300;

    /// Skills from the character (negative means unknown: the fallback).
    #[must_use]
    pub fn from_skills(run_skill: i32, jump_skill: i32) -> Self {
        Self {
            run_skill: if run_skill >= 0 {
                run_skill
            } else {
                Self::FALLBACK_RUN_SKILL
            },
            jump_skill: if jump_skill >= 0 {
                jump_skill
            } else {
                Self::FALLBACK_JUMP_SKILL
            },
        }
    }
}

impl Default for PlayerMovementOptions {
    fn default() -> Self {
        Self {
            run_skill: Self::FALLBACK_RUN_SKILL,
            jump_skill: Self::FALLBACK_JUMP_SKILL,
        }
    }
}

/// The jump being charged.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct JumpChargeSnapshot {
    /// Charging.
    pub is_charging: bool,
    /// Power so far (0..1).
    pub power: f32,
}

/// In the world or in portal space.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlayerState {
    /// In the world.
    #[default]
    InWorld,
    /// Between worlds: no movement.
    PortalSpace,
}

/// What the place to draw is blended from
/// ([`PlayerMovementController::render_steps`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RenderSteps {
    /// The body after the quantum before the last one (world).
    pub previous: Vec3,
    /// The body after the last quantum (world).
    pub current: Vec3,
    /// Time carried past the last quantum, seconds.
    pub pending_seconds: f64,
    /// The last quantum's length, seconds.
    pub step_seconds: f32,
}

/// What one update produced.
#[derive(Debug, Clone, PartialEq)]
pub struct MovementResult {
    /// Body position (world).
    pub position: Vec3,
    /// Position to draw (interpolated between quanta).
    pub render_position: Vec3,
    /// The body's cell.
    pub cell_id: u32,
    /// On walkable ground.
    pub is_on_ground: bool,
    /// The outbound motion state changed.
    pub motion_state_changed: bool,
    /// Forward command to send.
    pub forward_command: Option<u32>,
    /// Sidestep command to send.
    pub sidestep_command: Option<u32>,
    /// Turn command to send.
    pub turn_command: Option<u32>,
    /// Forward speed to send.
    pub forward_speed: Option<f32>,
    /// Sidestep speed to send.
    pub sidestep_speed: Option<f32>,
    /// Turn speed to send.
    pub turn_speed: Option<f32>,
    /// Run held.
    pub is_running: bool,
    /// Landed during this update.
    pub just_landed: bool,
    /// A jump was taken with this extent.
    pub jump_extent: Option<f32>,
    /// ... and this body-axes velocity.
    pub jump_velocity: Option<Vec3>,
    /// Send a movement event (MoveToState).
    pub should_send_movement_event: bool,
    /// The turn axis carries the run key (mouse turns).
    pub turn_uses_run_hold: bool,
    /// The sidestep axis carries the run key (mouse-look remap).
    pub sidestep_uses_run_hold: bool,
    /// The event is the mouse-look cadence one.
    pub is_mouse_look_movement_event: bool,
    /// Stance.
    pub current_style: u32,
    /// Send this raw state instead of one built from the fields.
    pub raw_motion_state_override: Option<RawMotionState>,
}

impl MovementResult {
    fn bare(
        position: Vec3,
        render_position: Vec3,
        cell_id: u32,
        is_on_ground: bool,
        style: u32,
    ) -> Self {
        Self {
            position,
            render_position,
            cell_id,
            is_on_ground,
            motion_state_changed: false,
            forward_command: None,
            sidestep_command: None,
            turn_command: None,
            forward_speed: None,
            sidestep_speed: None,
            turn_speed: None,
            is_running: false,
            just_landed: false,
            jump_extent: None,
            jump_velocity: None,
            should_send_movement_event: false,
            turn_uses_run_hold: false,
            sidestep_uses_run_hold: false,
            is_mouse_look_movement_event: false,
            current_style: style,
            raw_motion_state_override: None,
        }
    }
}

/// The raw motion state a movement event carries: the override when there
/// is one, else the result's axes with the run key on each held axis (the
/// mouse axes carry run always).
#[must_use]
pub fn build_raw_motion_state(movement: &MovementResult) -> RawMotionState {
    if let Some(raw) = &movement.raw_motion_state_override {
        return raw.clone();
    }
    let defaults = RawMotionState::default();
    let axis_hold_key = if movement.is_running {
        HoldKey::Run
    } else {
        HoldKey::None
    };
    let mut raw = RawMotionState::default();
    raw.current_hold_key = axis_hold_key;
    raw.current_style = movement.current_style;
    raw.forward_command = movement.forward_command.unwrap_or(defaults.forward_command);
    raw.forward_hold_key = if movement.forward_command.is_some() {
        axis_hold_key
    } else {
        HoldKey::Invalid
    };
    raw.forward_speed = movement.forward_speed.unwrap_or(defaults.forward_speed);
    raw.sidestep_command = movement
        .sidestep_command
        .unwrap_or(defaults.sidestep_command);
    raw.sidestep_hold_key = match movement.sidestep_command {
        Some(_) if movement.sidestep_uses_run_hold => HoldKey::Run,
        Some(_) => axis_hold_key,
        None => HoldKey::Invalid,
    };
    raw.sidestep_speed = movement.sidestep_speed.unwrap_or(defaults.sidestep_speed);
    raw.turn_command = movement.turn_command.unwrap_or(defaults.turn_command);
    raw.turn_hold_key = match movement.turn_command {
        Some(_) if movement.turn_uses_run_hold => HoldKey::Run,
        Some(_) => axis_hold_key,
        None => HoldKey::Invalid,
    };
    raw.turn_speed = movement.turn_speed.unwrap_or(defaults.turn_speed);
    raw
}

/// What the controller needs from the session for a call.
pub struct MovementEnv<'a> {
    /// The physics of the resident world.
    pub engine: &'a mut PhysicsEngine,
    /// The other objects.
    pub world: &'a dyn TargetWorld,
}

impl std::fmt::Debug for MovementEnv<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MovementEnv").finish_non_exhaustive()
    }
}

impl<'a> MovementEnv<'a> {
    /// An environment over `engine` and `world`.
    pub fn new(engine: &'a mut PhysicsEngine, world: &'a dyn TargetWorld) -> Self {
        Self { engine, world }
    }
}

/// A movement source that moves the body by the animation's root motion.
pub type RootMotionSource = Box<dyn FnMut(f32, &mut MotionDeltaFrame)>;
/// Takes the animation hooks a step reached.
pub type HookProcessor = Box<dyn FnMut()>;

const MOUSE_TURN_DEAD_ZONE: f32 = 0.02;
const MOUSE_TURN_SPEED_SCALE: f32 = 2.0;
const MOUSE_TURN_MAXIMUM_SPEED: f32 = 1.5;
const PHYSICS_STEP_AVERAGING: f32 = 0.1;
#[allow(clippy::excessive_precision)]
const SEND_EPSILON: f32 = 0.000_199_999_995;

/// The local player's movement.
pub struct PlayerMovementController {
    obj: MovementObject<PlayerWeenie>,
    /// Step-up height, metres.
    pub step_up_height: f32,
    /// Step-down height, metres.
    pub step_down_height: f32,
    /// The player's own PK bits for the sweep (`ObjectInfoState` bits).
    pub own_pvp_flags: u32,
    /// The body's collision spheres (empty: a 0.48 m by 1.835 m cylinder).
    pub sphere_list: Vec<CollisionSphere>,
    /// The size the character wears.
    pub object_scale: f32,
    /// In the world or portal space.
    pub state: PlayerState,
    /// The player's local entity id (the sweep skips it).
    pub local_entity_id: u32,
    cell_id: u32,
    advanced_object_quantum_last_tick: bool,
    presented_delta_seconds: f32,
    last_quantum_seconds: f32,
    physics_step_seconds: f32,
    jump_charging: bool,
    jump_extent: f32,
    prev_jump_held: bool,
    was_airborne_last_frame: bool,
    prev_forward_cmd: Option<u32>,
    prev_sidestep_cmd: Option<u32>,
    prev_turn_cmd: Option<u32>,
    prev_forward_speed: Option<f32>,
    prev_run_hold: bool,
    prev_forward_held: bool,
    prev_backward_held: bool,
    prev_strafe_left_held: bool,
    prev_strafe_right_held: bool,
    prev_turn_left_held: bool,
    prev_turn_right_held: bool,
    prev_run_held: bool,
    has_input_snapshot: bool,
    mouse_look_active: bool,
    /// The mouse look is modern mouse turning's (the view owns the turn).
    modern_mouse_look: bool,
    /// Modern mouse turning: the compass heading (degrees) the view points at.
    mouse_look_facing_target: Option<f32>,
    mouse_turn_sample_pending: bool,
    mouse_turn_adjustment: f32,
    active_input_turn_command: Option<u32>,
    active_input_turn_speed: f32,
    active_input_turn_from_mouse: bool,
    active_input_sidestep_command: Option<u32>,
    active_input_sidestep_uses_run_hold: bool,
    mouse_movement_event_candidate: bool,
    mouse_movement_event_pending: bool,
    last_mouse_movement_event_time: f32,
    controlled_by_server: bool,
    last_sent_position: CellPosition,
    last_sent_contact_plane: Plane,
    last_sent_time: f32,
    last_sent_initialized: bool,
    sim_time_seconds: f32,
    prev_physics_pos: Vec3,
    curr_physics_pos: Vec3,
    root_motion: Option<RootMotionSource>,
    process_hooks: Option<HookProcessor>,
    animation_root_motion: bool,
    external_movement_event_pending: bool,
    external_raw_motion_state_pending: Option<RawMotionState>,
    local_action_stamp: u32,
    interface_refusals: Vec<WeenieError>,
    tail_passes: u64,
}

impl std::fmt::Debug for PlayerMovementController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayerMovementController")
            .field("position", &self.obj.body.position())
            .field("cell_id", &self.cell_id)
            .finish_non_exhaustive()
    }
}

impl PlayerMovementController {
    /// Time between mouse-look movement events, seconds.
    pub const MOUSE_MOVEMENT_EVENT_INTERVAL: f32 = 0.5;
    /// Unchanged positions are re-sent after this long, seconds.
    pub const HEARTBEAT_INTERVAL: f32 = 1.0;

    /// A controller with the fallback skills.
    #[must_use]
    pub fn new() -> Self {
        Self::with_options(PlayerMovementOptions::default())
    }

    /// A controller with `options`' skills: a body under gravity reporting
    /// collisions, moving autonomously.
    #[must_use]
    pub fn with_options(options: PlayerMovementOptions) -> Self {
        let mut body = PhysicsBody::new();
        body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
        body.last_move_was_autonomous = true;
        let weenie = PlayerWeenie::new(options.run_skill, options.jump_skill, 0.0);
        let mut obj = MovementObject::new(ObjectToken::default(), body, weenie);
        obj.cell_from_body = true;
        obj.radius = DEFAULT_PLAYER_RADIUS;
        obj.height = DEFAULT_PLAYER_HEIGHT;
        obj.interp.is_local_player = false;
        Self {
            obj,
            step_up_height: 0.4,
            step_down_height: 0.4,
            own_pvp_flags: 0,
            sphere_list: Vec::new(),
            object_scale: 1.0,
            state: PlayerState::InWorld,
            local_entity_id: 0,
            cell_id: 0,
            advanced_object_quantum_last_tick: false,
            presented_delta_seconds: 0.0,
            last_quantum_seconds: PhysicsBody::MIN_QUANTUM,
            physics_step_seconds: PhysicsBody::MIN_QUANTUM,
            jump_charging: false,
            jump_extent: 0.0,
            prev_jump_held: false,
            was_airborne_last_frame: false,
            prev_forward_cmd: None,
            prev_sidestep_cmd: None,
            prev_turn_cmd: None,
            prev_forward_speed: None,
            prev_run_hold: false,
            prev_forward_held: false,
            prev_backward_held: false,
            prev_strafe_left_held: false,
            prev_strafe_right_held: false,
            prev_turn_left_held: false,
            prev_turn_right_held: false,
            prev_run_held: false,
            has_input_snapshot: false,
            mouse_look_active: false,
            modern_mouse_look: false,
            mouse_look_facing_target: None,
            mouse_turn_sample_pending: false,
            mouse_turn_adjustment: 0.0,
            active_input_turn_command: None,
            active_input_turn_speed: 0.0,
            active_input_turn_from_mouse: false,
            active_input_sidestep_command: None,
            active_input_sidestep_uses_run_hold: false,
            mouse_movement_event_candidate: false,
            mouse_movement_event_pending: false,
            last_mouse_movement_event_time: 0.0,
            controlled_by_server: true,
            last_sent_position: CellPosition::default(),
            last_sent_contact_plane: Plane::default(),
            last_sent_time: 0.0,
            last_sent_initialized: false,
            sim_time_seconds: 0.0,
            prev_physics_pos: Vec3::ZERO,
            curr_physics_pos: Vec3::ZERO,
            root_motion: None,
            process_hooks: None,
            animation_root_motion: false,
            external_movement_event_pending: false,
            external_raw_motion_state_pending: None,
            local_action_stamp: 0,
            interface_refusals: Vec::new(),
            tail_passes: 0,
        }
    }

    // ── state ──────────────────────────────────────────────────────────

    /// The movement object (body, interpreter, managers, motion table), for
    /// the runtime's wiring.
    pub fn object_mut(&mut self) -> &mut MovementObject<PlayerWeenie> {
        &mut self.obj
    }

    /// The movement object.
    #[must_use]
    pub fn object(&self) -> &MovementObject<PlayerWeenie> {
        &self.obj
    }

    /// The body.
    #[must_use]
    pub fn body(&self) -> &PhysicsBody {
        &self.obj.body
    }

    /// The body, mutably.
    pub fn body_mut(&mut self) -> &mut PhysicsBody {
        &mut self.obj.body
    }

    /// Facing as a yaw, radians (0 faces +x, counter-clockwise).
    #[must_use]
    pub fn yaw(&self) -> f32 {
        yaw_from_heading(get_heading(self.obj.body.orientation))
    }

    /// Face `yaw` (radians; wrapped to `[-π, π]`).
    pub fn set_yaw(&mut self, value: f32) {
        use std::f32::consts::PI;
        let mut wrapped = value;
        while wrapped > PI {
            wrapped -= 2.0 * PI;
        }
        while wrapped < -PI {
            wrapped += 2.0 * PI;
        }
        self.obj.body.orientation = set_heading(heading_from_yaw(wrapped));
    }

    /// Body position (world).
    #[must_use]
    pub fn position(&self) -> Vec3 {
        self.obj.body.position()
    }

    /// Position to draw: between the last two quanta by the time carried.
    #[must_use]
    pub fn render_position(&self) -> Vec3 {
        let alpha = ((self.obj.clock.pending_seconds() / f64::from(self.last_quantum_seconds))
            as f32)
            .clamp(0.0, 1.0);
        self.prev_physics_pos.lerp(self.curr_physics_pos, alpha)
    }

    /// What [`render_position`](Self::render_position) blends: the places
    /// of the last two quanta, the time carried past the last one and its
    /// length, so a caller with its own clock can blend at its own time.
    #[must_use]
    pub fn render_steps(&self) -> RenderSteps {
        RenderSteps {
            previous: self.prev_physics_pos,
            current: self.curr_physics_pos,
            pending_seconds: self.obj.clock.pending_seconds(),
            step_seconds: self.last_quantum_seconds,
        }
    }

    /// The body's cell.
    #[must_use]
    pub fn cell_id(&self) -> u32 {
        self.cell_id
    }

    /// The body's cell position.
    #[must_use]
    pub fn cell_position(&self) -> CellPosition {
        self.obj.body.cell_position()
    }

    /// The cell position with the body's current orientation.
    #[must_use]
    pub fn current_cell_position(&self) -> CellPosition {
        let carried = self.obj.body.cell_position();
        CellPosition::new(
            carried.obj_cell_id,
            carried.frame.origin,
            self.obj.body.orientation,
        )
    }

    /// The last update ran at least one quantum.
    #[must_use]
    pub fn advanced_object_quantum_last_tick(&self) -> bool {
        self.advanced_object_quantum_last_tick
    }

    /// Simulated time the last update presented, seconds.
    #[must_use]
    pub fn presented_delta_seconds(&self) -> f32 {
        self.presented_delta_seconds
    }

    /// Simulated time, seconds.
    #[must_use]
    pub fn sim_time_seconds(&self) -> f32 {
        self.sim_time_seconds
    }

    /// In the air.
    #[must_use]
    pub fn is_airborne(&self) -> bool {
        !self.obj.body.on_walkable()
    }

    /// Run speed at the run skill and burden, metres per second.
    #[must_use]
    pub fn run_speed(&self) -> f32 {
        RUN_ANIM_SPEED * self.obj.weenie.inq_run_rate().unwrap_or(1.0)
    }

    /// Height of a full-power jump, metres (0 when too loaded to jump).
    #[must_use]
    pub fn full_jump_height(&self) -> f32 {
        if !self.obj.weenie.can_jump(1.0) {
            return 0.0;
        }
        self.obj
            .weenie
            .inq_jump_velocity(1.0)
            .map_or(0.0, |rise| rise * rise / 19.6)
    }

    /// Average length of recent physics steps, seconds.
    #[must_use]
    pub fn physics_step_seconds(&self) -> f32 {
        self.physics_step_seconds
    }

    /// Vertical velocity, metres per second.
    #[must_use]
    pub fn vertical_velocity(&self) -> f32 {
        self.obj.body.velocity.z
    }

    /// Body velocity.
    #[must_use]
    pub fn body_velocity(&self) -> Vec3 {
        self.obj.body.velocity
    }

    /// Velocity the last quantum realised.
    #[must_use]
    pub fn cached_velocity(&self) -> Vec3 {
        self.obj.body.cached_velocity
    }

    /// The contact plane.
    #[must_use]
    pub fn contact_plane(&self) -> Plane {
        self.obj.body.contact_plane
    }

    /// Body orientation.
    #[must_use]
    pub fn body_orientation(&self) -> Quat {
        self.obj.body.orientation
    }

    /// Touching something.
    #[must_use]
    pub fn body_in_contact(&self) -> bool {
        self.obj.body.in_contact()
    }

    /// An autonomous position may be sent (touching walkable ground).
    #[must_use]
    pub fn can_send_position_event(&self) -> bool {
        self.obj.body.in_contact() && self.obj.body.on_walkable()
    }

    /// The jump being charged.
    #[must_use]
    pub fn jump_charge(&self) -> JumpChargeSnapshot {
        JumpChargeSnapshot {
            is_charging: self.jump_charging,
            power: if self.jump_charging {
                self.jump_extent
            } else {
                0.0
            },
        }
    }

    /// Standing still on the ground.
    #[must_use]
    pub fn is_standing_still(&self) -> bool {
        let body = &self.obj.body;
        let s = &self.obj.interp.interpreted_state;
        body.transient_state
            .contains(TransientState::CONTACT | TransientState::ON_WALKABLE)
            && s.forward_command == READY
            && s.side_step_command == 0
            && s.turn_command == 0
    }

    /// Manager-tail passes run (targeting, move-to, completions, sticky).
    #[must_use]
    pub fn tail_passes(&self) -> u64 {
        self.tail_passes
    }

    /// Take the jump refusals to report ("You can't jump ...").
    pub fn take_interface_refusals(&mut self) -> Vec<WeenieError> {
        std::mem::take(&mut self.interface_refusals)
    }

    /// End a jump: no charge, no standing long jump.
    pub fn finish_jump(&mut self) {
        self.jump_charging = false;
        self.jump_extent = 0.0;
        self.obj.interp.standing_long_jump = false;
    }

    /// Replace the physics state bits.
    pub fn apply_physics_state(&mut self, state: PhysicsState) {
        self.obj.body.state = state;
        self.obj.body.calc_acceleration();
    }

    /// Replace the run and jump skills.
    pub fn set_character_skills(&mut self, run_skill: i32, jump_skill: i32) {
        self.obj.weenie.set_skills(run_skill, jump_skill);
    }

    /// Replace the burden (load ratio).
    pub fn set_character_burden(&mut self, burden: f32) {
        self.obj.weenie.set_burden(burden);
    }

    /// Current stamina (negative: unknown).
    pub fn set_character_stamina(&mut self, current_stamina: i32) {
        self.obj
            .weenie
            .set_stamina(u32::try_from(current_stamina).ok());
    }

    /// PK status (negative: unknown) and the last PK attack time.
    pub fn set_character_pk_status(
        &mut self,
        player_killer_status: i32,
        last_pk_attack_timestamp: Option<f32>,
    ) {
        self.obj.weenie.set_player_killer_status(
            (player_killer_status >= 0).then_some(player_killer_status),
            last_pk_attack_timestamp,
        );
    }

    /// Stamina ran out: re-apply the movement.
    pub fn report_exhaustion(&mut self, env: &mut MovementEnv<'_>) {
        self.obj.cx(env.world).report_exhaustion();
    }

    /// Turn the body toward `orientation` (keeping it valid).
    pub fn set_body_orientation(&mut self, orientation: Quat) {
        self.obj.body.orientation = set_rotate(
            self.obj.body.position(),
            self.obj.body.orientation,
            orientation,
        );
    }

    /// Mark the last move as autonomous (the player's own) or the
    /// server's.
    pub fn set_last_move_was_autonomous(&mut self, autonomous: bool) {
        self.obj.body.last_move_was_autonomous = autonomous;
        self.controlled_by_server = !autonomous;
    }

    /// Move the body by the animation's root motion each quantum, and take
    /// the animation's hooks after the body moved.
    pub fn attach_animation_root_motion_source(
        &mut self,
        advance: RootMotionSource,
        process_hooks: Option<HookProcessor>,
    ) {
        self.root_motion = Some(advance);
        self.process_hooks = process_hooks;
    }

    /// Animate the player with `sequencer` at `scale`: its cycles move the
    /// body (root motion) and set the walk and run speeds, its hooks are
    /// taken after each step, motions go to it and finished ones come back
    /// to the interpreter. Queued link animations and motions are dropped.
    pub fn arm_animation(
        &mut self,
        env: &mut MovementEnv<'_>,
        sequencer: ac_motion::AnimationSequencer,
        scale: f32,
    ) {
        self.obj.sequencer = Some(sequencer);
        self.obj.sink = crate::movement::object::MotionSink::Sequencer;
        self.obj.cycle_velocity_from_sequencer = true;
        self.object_scale = scale;
        self.root_motion = None;
        self.process_hooks = None;
        self.animation_root_motion = true;
        let mut cx = self.obj.cx(env.world);
        crate::movement::interp::InterpreterHost::remove_link_animations(&mut cx);
        cx.handle_exit_world();
    }

    fn advance_root_motion(&mut self, dt: f32, out: &mut MotionDeltaFrame) -> bool {
        if let Some(advance) = &mut self.root_motion {
            advance(dt, out);
            return true;
        }
        if self.animation_root_motion {
            let mut frame = ac_motion::Frame::IDENTITY;
            if let Some(seq) = self.obj.sequencer.as_mut() {
                seq.advance_root_motion_only(dt, Some(&mut frame));
            }
            out.origin = frame.origin;
            out.orientation = frame.orientation;
            return true;
        }
        false
    }

    fn take_animation_hooks(&mut self, env: &mut MovementEnv<'_>) {
        if let Some(hooks) = &mut self.process_hooks {
            hooks();
        } else if self.animation_root_motion {
            self.obj.cx(env.world).take_reached_points();
        }
    }

    fn sync_time(&mut self) {
        self.obj.sim_time = f64::from(self.sim_time_seconds);
        self.obj.weenie.set_clock_seconds(self.sim_time_seconds);
    }

    // ── movement requests ──────────────────────────────────────────────

    /// A movement request on the player (the movement manager).
    pub fn perform_movement(
        &mut self,
        env: &mut MovementEnv<'_>,
        mvs: &MovementStruct,
    ) -> WeenieError {
        self.sync_time();
        self.obj.cx(env.world).movement_perform(mvs)
    }

    /// Stop every motion at once (the Escape key while moving).
    pub fn stop_completely(&mut self, env: &mut MovementEnv<'_>) -> WeenieError {
        self.stop_completely_at_boundary(env)
    }

    fn stop_completely_at_boundary(&mut self, env: &mut MovementEnv<'_>) -> WeenieError {
        self.obj.cx(env.world).movement_perform(&MovementStruct {
            kind: MovementType::StopCompletely,
            ..MovementStruct::default()
        })
    }

    fn do_motion_at_boundary(
        &mut self,
        env: &mut MovementEnv<'_>,
        motion: u32,
        p: MovementParameters,
    ) -> WeenieError {
        self.obj.body.last_move_was_autonomous = true;
        self.obj.cx(env.world).movement_perform(&MovementStruct {
            kind: MovementType::RawCommand,
            motion,
            params: Some(p),
            ..MovementStruct::default()
        })
    }

    fn stop_motion_at_boundary(
        &mut self,
        env: &mut MovementEnv<'_>,
        motion: u32,
        p: MovementParameters,
    ) -> WeenieError {
        self.obj.body.last_move_was_autonomous = true;
        self.obj.cx(env.world).movement_perform(&MovementStruct {
            kind: MovementType::StopRawCommand,
            motion,
            params: Some(p),
            ..MovementStruct::default()
        })
    }

    fn mouse_axis_parameters(speed: f32) -> MovementParameters {
        MovementParameters {
            speed,
            set_hold_key: false,
            hold_key_to_apply: HoldKey::Run,
            ..MovementParameters::default()
        }
    }

    // ── mouse look ─────────────────────────────────────────────────────

    /// Start mouse look (not in portal space, not twice); `modern` for
    /// modern mouse turning, where the view, not the mouse's motion, owns
    /// the turn and the turn keys keep turning.
    pub fn begin_mouse_look(
        &mut self,
        env: &mut MovementEnv<'_>,
        input: MovementInput,
        modern: bool,
    ) -> bool {
        if self.mouse_look_active || self.state != PlayerState::InWorld {
            return false;
        }
        self.sync_time();
        self.mouse_look_active = true;
        self.modern_mouse_look = modern;
        self.mouse_look_facing_target = None;
        self.take_control_from_server(env, Some(input));
        self.mouse_turn_sample_pending = false;
        self.mouse_turn_adjustment = 0.0;
        self.apply_mouse_look_toggle_movement(env, input, true);
        true
    }

    /// Modern mouse turning: the yaw (radians) the view points along. While
    /// a modern mouse look is on and no turn key is held, the character
    /// turns toward it with the ordinary turn motion at the full turn rate,
    /// and stops on it. The view never waits on the character, and nothing
    /// else sets the character's heading.
    pub fn set_mouse_look_facing_target(&mut self, yaw: f32) {
        if !self.mouse_look_active || !self.modern_mouse_look || !yaw.is_finite() {
            return;
        }
        self.mouse_look_facing_target = Some(heading_from_yaw(yaw));
    }

    /// No modern facing turn is under way: the character faces the view's
    /// target, or there is no modern mouse look (it ended, or a teleport or
    /// reset cleared it).
    #[must_use]
    pub fn mouse_look_facing_settled(&self) -> bool {
        let Some(target) = self.mouse_look_facing_target else {
            return true;
        };
        !self.mouse_look_active
            || !self.modern_mouse_look
            || (!self.facing_turn_active() && self.facing_error(target).abs() <= MOVE_TO_EPSILON)
    }

    /// A modern mouse look is on.
    #[must_use]
    pub fn modern_mouse_look_active(&self) -> bool {
        self.mouse_look_active && self.modern_mouse_look
    }

    /// A modern facing turn is under way: the view's target, not a key,
    /// owns the turn.
    fn facing_turn_active(&self) -> bool {
        self.mouse_look_active
            && self.modern_mouse_look
            && self.active_input_turn_from_mouse
            && self.active_input_turn_command.is_some()
    }

    /// Compass degrees from the facing to `target_heading`, in (-180, 180];
    /// positive is a right turn (a right turn raises the compass heading).
    fn facing_error(&self, target_heading: f32) -> f32 {
        let mut error = (target_heading - get_heading(self.obj.body.orientation)) % 360.0;
        if error > 180.0 {
            error -= 360.0;
        } else if error <= -180.0 {
            error += 360.0;
        }
        error
    }

    /// How far the target still lies ahead along `turn`, degrees.
    fn facing_ahead(turn: u32, error: f32) -> f32 {
        if turn == TURN_RIGHT { error } else { -error }
    }

    /// The turn a modern mouse look wants this frame. A turn under way
    /// keeps going while the target is on or ahead of the facing: it is
    /// stopped at the next physics step boundary that finds the target not
    /// ahead, so a drag that pauses for a frame or two does not stop and
    /// restart it. A view swung back past the facing turns the other way.
    /// One full-rate turn at a time, no speed changes: the turn motion is
    /// never re-issued.
    fn desired_facing_turn(
        &mut self,
        env: &mut MovementEnv<'_>,
        input: MovementInput,
    ) -> Option<u32> {
        let target = self.mouse_look_facing_target?;
        let error = self.facing_error(target);
        if self.facing_turn_active()
            && let Some(active) = self.active_input_turn_command
            && Self::facing_ahead(active, error) >= -MOVE_TO_EPSILON
        {
            return Some(active);
        }
        if error.abs() <= MOVE_TO_EPSILON {
            return None;
        }
        if self.controlled_by_server {
            self.take_control_from_server(env, Some(input));
        }
        Some(if error > 0.0 { TURN_RIGHT } else { TURN_LEFT })
    }

    /// At a physics step boundary: a facing turn whose target is no longer
    /// ahead stops before the step runs.
    fn stop_facing_turn_if_on_target(&mut self, env: &mut MovementEnv<'_>) -> bool {
        if !self.facing_turn_active() {
            return false;
        }
        let (Some(target), Some(active)) = (
            self.mouse_look_facing_target,
            self.active_input_turn_command,
        ) else {
            return false;
        };
        if Self::facing_ahead(active, self.facing_error(target)) > MOVE_TO_EPSILON {
            return false;
        }
        self.apply_input_turn(env, None, 1.0, true, false);
        true
    }

    /// After a physics step turned the body: a facing turn that passed its
    /// target lands on it, the way the game's own turn-to-heading finishes,
    /// so the character never turns past where the view points.
    fn land_facing_turn_on_target(&mut self) {
        if !self.facing_turn_active() {
            return;
        }
        let (Some(target), Some(active)) = (
            self.mouse_look_facing_target,
            self.active_input_turn_command,
        ) else {
            return;
        };
        let error = self.facing_error(target);
        if Self::facing_ahead(active, error) < 0.0 && error.abs() < 90.0 {
            self.obj.body.orientation = set_heading(target);
        }
    }

    /// A filtered horizontal mouse movement while looking (negative turns
    /// right).
    pub fn submit_mouse_turn_adjustment(
        &mut self,
        env: &mut MovementEnv<'_>,
        signed_adjustment: f32,
        input: MovementInput,
    ) {
        if !self.mouse_look_active || self.modern_mouse_look || !signed_adjustment.is_finite() {
            return;
        }
        self.sync_time();
        self.take_control_from_server(env, Some(input));
        self.mouse_turn_adjustment = signed_adjustment;
        self.mouse_turn_sample_pending = true;
        self.mouse_movement_event_candidate = true;
    }

    /// The mouse stopped while looking: stop the mouse turn.
    pub fn stop_mouse_drift(&mut self, env: &mut MovementEnv<'_>, input: MovementInput) {
        if !self.mouse_look_active {
            return;
        }
        self.sync_time();
        self.take_control_from_server(env, Some(input));
        let speed = if self.active_input_turn_speed == 0.0 {
            1.0
        } else {
            self.active_input_turn_speed
        };
        let p = Self::mouse_axis_parameters(speed);
        self.stop_motion_at_boundary(env, TURN_RIGHT, p);
        self.stop_motion_at_boundary(env, TURN_LEFT, p);
        self.active_input_turn_command = None;
        self.active_input_turn_speed = 0.0;
        self.active_input_turn_from_mouse = false;
        self.mouse_turn_sample_pending = false;
        self.mouse_turn_adjustment = 0.0;
        self.mouse_movement_event_candidate = true;
    }

    /// End mouse look.
    pub fn end_mouse_look(&mut self, env: &mut MovementEnv<'_>, input: MovementInput) -> bool {
        if !self.mouse_look_active && !self.active_input_turn_from_mouse {
            return false;
        }
        self.sync_time();
        self.mouse_look_active = false;
        self.modern_mouse_look = false;
        self.mouse_look_facing_target = None;
        self.take_control_from_server(env, Some(input));
        self.mouse_turn_sample_pending = false;
        self.mouse_turn_adjustment = 0.0;
        self.apply_mouse_look_toggle_movement(env, input, false);
        true
    }

    fn apply_mouse_look_toggle_movement(
        &mut self,
        env: &mut MovementEnv<'_>,
        input: MovementInput,
        entering: bool,
    ) {
        let (sidestep, use_run_hold) =
            Self::desired_input_sidestep(input, entering && !self.modern_mouse_look);
        self.apply_input_sidestep(env, sidestep, use_run_hold, !entering);

        let turn = if entering && !self.modern_mouse_look {
            None
        } else if input.turn_right {
            Some(TURN_RIGHT)
        } else if input.turn_left {
            Some(TURN_LEFT)
        } else {
            None
        };
        self.apply_input_turn(env, turn, 1.0, false, !entering);

        let run_held = self.obj.interp.raw_state.current_hold_key == HoldKey::Run;
        if run_held != input.run {
            self.obj.cx(env.world).set_hold_run(input.run, true);
        }

        let desired_forward = if input.forward {
            Some(WALK_FORWARD)
        } else if input.backward {
            Some(WALK_BACKWARDS)
        } else {
            None
        };
        let raw_forward = self.obj.interp.raw_state.forward_command;
        let active_forward =
            (raw_forward != RawMotionState::default().forward_command).then_some(raw_forward);
        if let Some(active) = active_forward
            && Some(active) != desired_forward
        {
            self.stop_motion_at_boundary(env, active, MovementParameters::default());
        }
        if let Some(command) = desired_forward
            && (active_forward != desired_forward || !entering)
        {
            self.do_motion_at_boundary(env, command, MovementParameters::default());
        }

        self.has_input_snapshot = true;
        self.prev_forward_held = input.forward;
        self.prev_backward_held = input.backward;
        self.prev_strafe_left_held = input.strafe_left;
        self.prev_strafe_right_held = input.strafe_right;
        self.prev_turn_left_held = input.turn_left;
        self.prev_turn_right_held = input.turn_right;
        self.prev_run_held = input.run;
        self.prev_run_hold = input.run;
        self.obj.body.last_move_was_autonomous = true;
    }

    fn desired_input_sidestep(
        input: MovementInput,
        mouse_look_active: bool,
    ) -> (Option<u32>, bool) {
        if input.strafe_right {
            return (Some(SIDE_STEP_RIGHT), false);
        }
        if input.strafe_left {
            return (Some(SIDE_STEP_LEFT), false);
        }
        if mouse_look_active && input.turn_right {
            return (Some(SIDE_STEP_RIGHT), true);
        }
        if mouse_look_active && input.turn_left {
            return (Some(SIDE_STEP_LEFT), true);
        }
        (None, false)
    }

    fn apply_input_sidestep(
        &mut self,
        env: &mut MovementEnv<'_>,
        desired: Option<u32>,
        use_run_hold: bool,
        reapply: bool,
    ) -> bool {
        let changed = desired != self.active_input_sidestep_command;
        if let Some(active) = self.active_input_sidestep_command
            && (changed || reapply)
        {
            let p = if self.active_input_sidestep_uses_run_hold {
                Self::mouse_axis_parameters(1.0)
            } else {
                MovementParameters::default()
            };
            self.stop_motion_at_boundary(env, active, p);
        }
        if let Some(command) = desired
            && (changed || reapply)
        {
            let p = if use_run_hold {
                Self::mouse_axis_parameters(1.0)
            } else {
                MovementParameters::default()
            };
            self.do_motion_at_boundary(env, command, p);
        }
        self.active_input_sidestep_command = desired;
        self.active_input_sidestep_uses_run_hold = desired.is_some() && use_run_hold;
        changed || (reapply && desired.is_some())
    }

    fn apply_input_turn(
        &mut self,
        env: &mut MovementEnv<'_>,
        desired: Option<u32>,
        speed: f32,
        from_mouse: bool,
        reapply: bool,
    ) -> bool {
        let command_changed = desired != self.active_input_turn_command;
        let both_present = desired.is_some() && self.active_input_turn_command.is_some();
        let speed_changed = both_present && (speed - self.active_input_turn_speed).abs() >= 0.0001;
        let owner_changed = both_present && from_mouse != self.active_input_turn_from_mouse;
        let apply =
            command_changed || speed_changed || owner_changed || (reapply && desired.is_some());

        if let Some(active) = self.active_input_turn_command
            && apply
        {
            let p = if self.active_input_turn_from_mouse {
                Self::mouse_axis_parameters(self.active_input_turn_speed)
            } else {
                MovementParameters::default()
            };
            self.stop_motion_at_boundary(env, active, p);
        }
        if let Some(command) = desired
            && apply
        {
            let p = if from_mouse {
                Self::mouse_axis_parameters(speed)
            } else {
                MovementParameters::default()
            };
            self.do_motion_at_boundary(env, command, p);
        }
        self.active_input_turn_command = desired;
        self.active_input_turn_speed = if desired.is_some() { speed } else { 0.0 };
        self.active_input_turn_from_mouse = desired.is_some() && from_mouse;
        apply
    }

    fn take_control_from_server(
        &mut self,
        env: &mut MovementEnv<'_>,
        current_input: Option<MovementInput>,
    ) {
        if !self.controlled_by_server {
            return;
        }
        self.controlled_by_server = false;
        self.obj.body.last_move_was_autonomous = true;
        self.stop_completely_at_boundary(env);
        self.active_input_turn_command = None;
        self.active_input_turn_speed = 0.0;
        self.active_input_turn_from_mouse = false;
        self.active_input_sidestep_command = None;
        self.active_input_sidestep_uses_run_hold = false;
        self.prev_forward_held = false;
        self.prev_backward_held = false;
        self.prev_strafe_left_held = false;
        self.prev_strafe_right_held = false;
        self.prev_turn_left_held = false;
        self.prev_turn_right_held = false;
        self.prev_run_held = self.obj.interp.raw_state.current_hold_key == HoldKey::Run;
        if let Some(input) = current_input {
            let entering = self.mouse_look_active;
            self.apply_mouse_look_toggle_movement(env, input, entering);
        }
    }

    /// Before an attack: stop everything (when the player is moving on its
    /// own). False when the server controls the movement.
    pub fn prepare_for_attack_request(&mut self, env: &mut MovementEnv<'_>) -> bool {
        if self.controlled_by_server {
            return false;
        }
        self.sync_time();
        self.stop_completely_at_boundary(env);
        self.active_input_turn_command = None;
        self.active_input_turn_speed = 0.0;
        self.active_input_turn_from_mouse = false;
        self.active_input_sidestep_command = None;
        self.active_input_sidestep_uses_run_hold = false;
        self.mouse_turn_sample_pending = false;
        self.mouse_turn_adjustment = 0.0;
        self.obj.body.last_move_was_autonomous = true;
        true
    }

    /// Stand, crouch, sit or sleep; false for any other motion or when
    /// refused.
    pub fn request_posture(&mut self, env: &mut MovementEnv<'_>, motion: u32) -> bool {
        if !matches!(motion, READY | CROUCH | SITTING | SLEEPING) {
            return false;
        }
        self.sync_time();
        self.take_control_from_server(env, None);
        if !self
            .do_motion_at_boundary(env, motion, MovementParameters::default())
            .is_none()
        {
            return false;
        }
        self.external_movement_event_pending = true;
        true
    }

    /// Turn to a heading (degrees; run key when asked) through the
    /// move-to manager; false when the player cannot move to things.
    /// `request_movement_event` false for a caller that re-issues the turn
    /// every frame (the first-person rotate): the turn itself sends nothing
    /// and the heading reaches the server with the position heartbeat.
    pub fn request_turn_to_heading(
        &mut self,
        env: &mut MovementEnv<'_>,
        heading_degrees: f32,
        apply_run_hold_key: bool,
        request_movement_event: bool,
    ) -> bool {
        if !heading_degrees.is_finite() || !self.obj.move_to_made() {
            return false;
        }
        self.sync_time();
        self.take_control_from_server(env, None);
        let mut p = MovementParameters {
            desired_heading: heading_degrees,
            speed: 1.0,
            stop_completely: false,
            ..MovementParameters::default()
        };
        if apply_run_hold_key {
            p.hold_key_to_apply = HoldKey::Run;
        }
        self.obj.body.last_move_was_autonomous = true;
        if !self
            .obj
            .cx(env.world)
            .movement_perform(&MovementStruct {
                kind: MovementType::TurnToHeading,
                params: Some(p),
                ..MovementStruct::default()
            })
            .is_none()
        {
            return false;
        }
        if request_movement_event {
            self.external_movement_event_pending = true;
        }
        true
    }

    /// An action or emote the player asked for (stamped as the player's
    /// own); the movement event then carries the whole raw state.
    pub fn request_command_motion(&mut self, env: &mut MovementEnv<'_>, motion: u32) -> bool {
        self.sync_time();
        self.take_control_from_server(env, None);
        let p = MovementParameters {
            autonomous: true,
            action_stamp: self.local_action_stamp,
            ..MovementParameters::default()
        };
        if !self.do_motion_at_boundary(env, motion, p).is_none() {
            return false;
        }
        if motion & ac_motion::command::ACTION_BIT != 0 {
            self.local_action_stamp = self.local_action_stamp.wrapping_add(1);
        }
        self.external_raw_motion_state_pending = Some(self.obj.interp.raw_state.clone());
        self.external_movement_event_pending = true;
        true
    }

    // ── outbound ───────────────────────────────────────────────────────

    /// The position an outbound message carries (cell frame, current
    /// orientation), when valid.
    #[must_use]
    pub fn try_get_outbound_position(&self) -> Option<CellPosition> {
        let p = self.current_cell_position();
        let q = p.frame.orientation;
        is_valid_position(
            p.obj_cell_id,
            p.frame.origin.to_array(),
            [q.w, q.x, q.y, q.z],
        )
        .then_some(p)
    }

    /// A movement event was sent at `now_seconds` (`mouse_look_event`: the
    /// cadence one, which acknowledges it).
    pub fn note_movement_sent(&mut self, now_seconds: f32, mouse_look_event: bool) {
        self.last_sent_time = now_seconds;
        if mouse_look_event {
            self.mouse_movement_event_pending = false;
            self.last_mouse_movement_event_time = now_seconds;
        }
    }

    /// An autonomous position was sent.
    pub fn note_position_sent(
        &mut self,
        position: CellPosition,
        contact_plane: Plane,
        now_seconds: f32,
    ) {
        self.last_sent_position = position;
        self.last_sent_contact_plane = contact_plane;
        self.last_sent_time = now_seconds;
        self.last_sent_initialized = true;
    }

    /// Whether an autonomous position is due: always first; within a
    /// second of the last send, on a cell or contact-plane change; after
    /// it, on a cell or frame change (0.0002 tolerance).
    #[must_use]
    pub fn should_send_position_event(
        &self,
        current: CellPosition,
        current_contact_plane: Plane,
        now_seconds: f32,
    ) -> bool {
        if !self.last_sent_initialized {
            return true;
        }
        let cell_changed = self.last_sent_position.obj_cell_id != current.obj_cell_id;
        if self.last_sent_time + Self::HEARTBEAT_INTERVAL >= now_seconds {
            return cell_changed
                || !approx_plane_equal(self.last_sent_contact_plane, current_contact_plane);
        }
        cell_changed || !approx_frame_equal(&self.last_sent_position, &current)
    }

    /// The autonomous position to send after the network pass, when one is
    /// due and allowed (valid position, on walkable ground).
    #[must_use]
    pub fn post_network_position(&self) -> Option<CellPosition> {
        let position = self.try_get_outbound_position()?;
        if !self.should_send_position_event(position, self.contact_plane(), self.sim_time_seconds)
            || !self.can_send_position_event()
        {
            return None;
        }
        Some(position)
    }

    /// The position for an immediate acknowledgement (a forced position),
    /// when on walkable ground and valid.
    #[must_use]
    pub fn immediate_position(&self) -> Option<CellPosition> {
        if !self.can_send_position_event() {
            return None;
        }
        self.try_get_outbound_position()
    }

    /// The current motion state as a movement event to send now.
    #[must_use]
    pub fn capture_movement_result(&self, mouse_look_event: bool) -> MovementResult {
        let raw = &self.obj.interp.raw_state;
        let defaults = RawMotionState::default();
        let forward =
            (raw.forward_command != defaults.forward_command).then_some(raw.forward_command);
        let sidestep =
            (raw.sidestep_command != defaults.sidestep_command).then_some(raw.sidestep_command);
        let turn = (raw.turn_command != defaults.turn_command).then_some(raw.turn_command);
        MovementResult {
            motion_state_changed: true,
            forward_command: forward,
            sidestep_command: sidestep,
            turn_command: turn,
            forward_speed: forward.map(|_| raw.forward_speed),
            sidestep_speed: sidestep.map(|_| raw.sidestep_speed),
            turn_speed: turn.map(|_| raw.turn_speed),
            is_running: raw.current_hold_key == HoldKey::Run,
            should_send_movement_event: true,
            turn_uses_run_hold: turn.is_some() && raw.turn_hold_key == HoldKey::Run,
            sidestep_uses_run_hold: sidestep.is_some() && raw.sidestep_hold_key == HoldKey::Run,
            is_mouse_look_movement_event: mouse_look_event,
            ..MovementResult::bare(
                self.position(),
                self.render_position(),
                self.cell_id,
                self.can_send_position_event(),
                raw.current_style,
            )
        }
    }

    /// The current motion state, for presentation (nothing to send).
    #[must_use]
    pub fn capture_presentation_result(&self) -> MovementResult {
        MovementResult {
            motion_state_changed: false,
            should_send_movement_event: false,
            is_mouse_look_movement_event: false,
            ..self.capture_movement_result(false)
        }
    }

    // ── placement ──────────────────────────────────────────────────────

    fn update_cell_id(
        &mut self,
        env: &mut MovementEnv<'_>,
        new_cell_id: u32,
        publish_render_root: bool,
    ) {
        self.cell_id = new_cell_id;
        if publish_render_root {
            env.engine.update_player_curr_cell(new_cell_id);
        }
    }

    /// Place the player (a teleport or the first placement): grounded,
    /// stopped, input edges reset, the leash re-armed at the new spot.
    pub fn seed_placement(
        &mut self,
        env: &mut MovementEnv<'_>,
        pos: Vec3,
        cell_id: u32,
        cell_local: Vec3,
    ) {
        self.set_position_core(env, pos, cell_id, cell_local, true);
    }

    /// Place the player before the runtime commits it (nothing published).
    pub fn prepare_position_for_commit(
        &mut self,
        env: &mut MovementEnv<'_>,
        pos: Vec3,
        cell_id: u32,
        cell_local: Vec3,
    ) {
        self.set_position_core(env, pos, cell_id, cell_local, false);
    }

    /// Arm the leash at the committed placement.
    pub fn arm_constraint_leash_at_committed_placement(&mut self, env: &mut MovementEnv<'_>) {
        self.rearm_constraint_leash_at_current_position(env);
    }

    fn rearm_constraint_leash_at_current_position(&mut self, env: &mut MovementEnv<'_>) {
        if !self.obj.position_attached {
            return;
        }
        let anchor = self.obj.body.cell_position();
        self.obj.cx(env.world).constrain_to(
            anchor,
            constraint_distance::get_start_constraint_distance(anchor.obj_cell_id),
            constraint_distance::get_max_constraint_distance(anchor.obj_cell_id),
        );
    }

    fn reset_input_axes(&mut self) {
        self.active_input_turn_command = None;
        self.active_input_turn_speed = 0.0;
        self.active_input_turn_from_mouse = false;
        self.active_input_sidestep_command = None;
        self.active_input_sidestep_uses_run_hold = false;
        self.mouse_look_active = false;
        self.modern_mouse_look = false;
        self.mouse_look_facing_target = None;
        self.mouse_turn_sample_pending = false;
        self.mouse_turn_adjustment = 0.0;
        self.mouse_movement_event_candidate = false;
        self.mouse_movement_event_pending = false;
    }

    fn reset_input_edges(&mut self) {
        self.prev_forward_held = false;
        self.prev_backward_held = false;
        self.prev_strafe_left_held = false;
        self.prev_strafe_right_held = false;
        self.prev_turn_left_held = false;
        self.prev_turn_right_held = false;
        self.prev_run_held = false;
        self.has_input_snapshot = false;
    }

    fn set_position_core(
        &mut self,
        env: &mut MovementEnv<'_>,
        pos: Vec3,
        cell_id: u32,
        cell_local: Vec3,
        publish: bool,
    ) {
        self.sync_time();
        self.obj.body.snap_to_cell(cell_id, pos, cell_local);
        self.prev_physics_pos = pos;
        self.curr_physics_pos = pos;
        let cell = self.obj.body.cell_position().obj_cell_id;
        self.update_cell_id(env, cell, publish);

        self.obj.body.transient_state =
            TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
        self.obj.body.velocity = Vec3::ZERO;

        self.stop_completely_at_boundary(env);
        self.reset_input_axes();
        if publish && self.obj.position_attached {
            let mut cx = self.obj.cx(env.world);
            cx.un_stick();
            cx.position_manager().un_constrain();
            self.rearm_constraint_leash_at_current_position(env);
        }
        self.reset_input_edges();
        self.obj.body.last_update_time = 0.0;
        self.obj.clock.reset_for_enter_world(false);
    }

    /// The body was moved by a forced position: the drawing frame follows
    /// it (motion and velocity are kept).
    pub fn commit_canonical_force_position_frame(&mut self, env: &mut MovementEnv<'_>) {
        self.prev_physics_pos = self.obj.body.position();
        self.curr_physics_pos = self.obj.body.position();
        let cell = self.obj.body.cell_position().obj_cell_id;
        self.update_cell_id(env, cell, true);
    }

    /// The body was teleported: the drawing frame follows, motion stops
    /// (velocity too when asked), the input edges reset and (when asked)
    /// the sticky target is released and the leash re-armed.
    pub fn commit_canonical_teleport_frame(
        &mut self,
        env: &mut MovementEnv<'_>,
        zero_velocity: bool,
        rearm_constraint_leash: bool,
        run_teleport_hook_tail: bool,
    ) {
        self.sync_time();
        self.prev_physics_pos = self.obj.body.position();
        self.curr_physics_pos = self.obj.body.position();
        let cell = self.obj.body.cell_position().obj_cell_id;
        self.update_cell_id(env, cell, true);
        if zero_velocity {
            self.obj.body.velocity = Vec3::ZERO;
        }
        self.stop_completely_at_boundary(env);
        self.reset_input_axes();
        if run_teleport_hook_tail && self.obj.position_attached {
            let mut cx = self.obj.cx(env.world);
            cx.un_stick();
            cx.position_manager().un_constrain();
            if rearm_constraint_leash {
                self.rearm_constraint_leash_at_current_position(env);
            }
        }
        self.reset_input_edges();
        self.obj.body.last_update_time = 0.0;
        self.obj.clock.reset_for_enter_world(false);
    }

    // ── updates ────────────────────────────────────────────────────────

    fn compute_presented_delta(
        &mut self,
        wall_dt: f32,
        pending_before_seconds: f64,
        batch: &RetailObjectQuantumBatch,
    ) -> f32 {
        if batch.discarded {
            self.last_quantum_seconds = PhysicsBody::MIN_QUANTUM;
            return wall_dt;
        }
        let previous_interval = self.last_quantum_seconds;
        let mut simulated = 0.0;
        if batch.count() > 0 {
            simulated = batch.full_steps as f32 * PhysicsBody::MAX_QUANTUM + batch.remainder;
            if let Some(q) = batch.get_quantum(batch.count() - 1) {
                self.last_quantum_seconds = q;
            }
        }
        let interval = self.last_quantum_seconds;
        let before = (pending_before_seconds as f32).min(previous_interval);
        let after = (self.obj.clock.pending_seconds() as f32).min(interval);
        let delta = simulated + (after - interval) - (before - previous_interval);
        delta.max(0.0)
    }

    fn resolve_request(&self, current: Vec3, target: Vec3, is_on_ground: bool) -> ResolveRequest {
        ResolveRequest {
            current_pos: current,
            target_pos: target,
            cell_id: self.cell_id,
            sphere_radius: DEFAULT_PLAYER_RADIUS,
            sphere_height: DEFAULT_PLAYER_HEIGHT,
            step_up_height: self.step_up_height,
            step_down_height: self.step_down_height,
            is_on_ground,
            mover_flags: ObjectInfoState(
                ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE | self.own_pvp_flags,
            ),
            moving_entity_id: self.local_entity_id,
            sphere_list: self.sphere_list.clone(),
            sphere_scale: self.object_scale,
            ..ResolveRequest::default()
        }
    }

    fn apply_position_manager_delta(body: &mut PhysicsBody, delta: &MotionDeltaFrame) {
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

    fn run_manager_tail(&mut self, env: &mut MovementEnv<'_>) {
        self.tail_passes += 1;
        let mut cx = self.obj.cx(env.world);
        if cx.obj.targeting_attached {
            cx.handle_targetting();
        }
        cx.movement_use_time();
        crate::movement::interp::InterpreterHost::check_for_completed_motions(&mut cx);
        if cx.obj.position_attached {
            cx.position_use_time();
        }
    }

    /// Advance a hidden player (not drawn): no animation advance, but the
    /// position manager's movement, hooks, the sweep and the manager tail
    /// still run per quantum.
    pub fn tick_hidden(&mut self, env: &mut MovementEnv<'_>, dt: f32) -> MovementResult {
        self.advanced_object_quantum_last_tick = false;
        self.presented_delta_seconds = 0.0;
        if !dt.is_finite() || dt <= 0.0 {
            return MovementResult {
                render_position: self.obj.body.position(),
                is_on_ground: self.obj.body.on_walkable(),
                ..self.capture_presentation_result()
            };
        }
        self.sim_time_seconds += dt;
        self.sync_time();
        let pending_before = self.obj.clock.pending_seconds();
        let reactivated = self.obj.clock.activate();
        self.obj.body.transient_state.insert(TransientState::ACTIVE);
        let batch = if reactivated {
            RetailObjectQuantumBatch::default()
        } else {
            self.obj.clock.advance(f64::from(dt))
        };
        self.advanced_object_quantum_last_tick = batch.count() > 0;
        self.presented_delta_seconds = self.compute_presented_delta(dt, pending_before, &batch);
        if batch.discarded {
            self.prev_physics_pos = self.obj.body.position();
            self.curr_physics_pos = self.obj.body.position();
        }

        for qi in 0..batch.count() {
            let quantum = batch.get_quantum(qi).unwrap_or(0.0);
            let previous_position = self.obj.body.position();
            let previous_contact = self.obj.body.in_contact();
            let previous_on_walkable = self.obj.body.on_walkable();

            if self.obj.position_attached {
                let mut delta = MotionDeltaFrame::default();
                self.obj
                    .cx(env.world)
                    .position_adjust_offset(&mut delta, f64::from(quantum));
                Self::apply_position_manager_delta(&mut self.obj.body, &delta);
            }

            self.take_animation_hooks(env);

            if self.obj.body.position() != previous_position
                && self.cell_id != 0
                && env.engine.landblock_count() > 0
            {
                let request = self.resolve_request(
                    previous_position,
                    self.obj.body.position(),
                    previous_on_walkable,
                );
                let resolved = env
                    .engine
                    .resolve_with_transition(&request, Some(&mut self.obj.body));
                self.obj
                    .body
                    .commit_transition_position(resolved.cell_id, resolved.position);
                self.commit_set_position_transition(
                    env,
                    resolved.in_contact,
                    resolved.on_walkable,
                    resolved.collision_normal_valid,
                    resolved.collision_normal,
                    previous_contact,
                    previous_on_walkable,
                );
                self.update_cell_id(env, resolved.cell_id, true);
            }

            self.run_manager_tail(env);
            self.prev_physics_pos = self.obj.body.position();
            self.curr_physics_pos = self.obj.body.position();
        }

        self.prev_physics_pos = self.obj.body.position();
        self.curr_physics_pos = self.obj.body.position();
        self.was_airborne_last_frame = !self.obj.body.on_walkable();
        MovementResult::bare(
            self.obj.body.position(),
            self.obj.body.position(),
            self.cell_id,
            self.obj.body.on_walkable(),
            self.obj.interp.raw_state.current_style,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn commit_set_position_transition(
        &mut self,
        env: &mut MovementEnv<'_>,
        in_contact: bool,
        on_walkable: bool,
        collision_normal_valid: bool,
        collision_normal: Vec3,
        previous_contact: bool,
        previous_on_walkable: bool,
    ) {
        let final_on_walkable = crate::obj_update::commit_set_position_contact_prefix(
            &mut self.obj.body,
            in_contact,
            on_walkable,
            previous_on_walkable,
        );
        if !previous_on_walkable && final_on_walkable {
            self.obj.cx(env.world).movement_hit_ground();
        } else if previous_on_walkable && !final_on_walkable {
            self.obj.cx(env.world).leave_ground();
        }
        crate::obj_update::commit_set_position_post_ground(&mut self.obj.body);
        let now_on_walkable = self.obj.body.on_walkable();
        handle_all_collisions(
            &mut self.obj.body,
            collision_normal_valid,
            collision_normal,
            previous_contact,
            previous_on_walkable,
            now_on_walkable,
        );
    }

    /// One update of `dt` seconds with `input`: input edges become motion
    /// requests, a jump charges or fires, the body moves quantum by quantum
    /// through the sweep, and the result says what to send.
    #[allow(clippy::too_many_lines)]
    pub fn update(
        &mut self,
        env: &mut MovementEnv<'_>,
        dt: f32,
        input: MovementInput,
    ) -> MovementResult {
        self.advanced_object_quantum_last_tick = false;
        self.presented_delta_seconds = 0.0;
        if !dt.is_finite() || dt <= 0.0 {
            return self.capture_presentation_result();
        }
        self.sim_time_seconds += dt;
        self.sync_time();

        if self.state == PlayerState::PortalSpace {
            return MovementResult::bare(
                self.position(),
                self.render_position(),
                self.cell_id,
                self.obj.body.on_walkable(),
                self.obj.interp.raw_state.current_style,
            );
        }

        if !self.has_input_snapshot {
            self.has_input_snapshot = true;
            self.prev_run_held = input.run;
            self.prev_run_hold = input.run;
            self.obj.interp.seed_hold_run(input.run);
        }

        let externally_requested = self.external_movement_event_pending;
        self.external_movement_event_pending = false;
        let external_raw = self.external_raw_motion_state_pending.take();
        let mut motion_edge_fired = false;
        let mut movement_event_requested = externally_requested;
        {
            let persistent_movement_held = input.is_persistent_command
                && (input.forward
                    || input.backward
                    || input.strafe_left
                    || input.strafe_right
                    || input.turn_left
                    || input.turn_right);
            if self.controlled_by_server && persistent_movement_held {
                self.take_control_from_server(env, None);
            }

            let user_input_edge = input.run != self.prev_run_held
                || input.forward != self.prev_forward_held
                || input.backward != self.prev_backward_held
                || input.strafe_left != self.prev_strafe_left_held
                || input.strafe_right != self.prev_strafe_right_held
                || input.turn_left != self.prev_turn_left_held
                || input.turn_right != self.prev_turn_right_held;
            if user_input_edge {
                self.take_control_from_server(env, None);
            }

            let p = MovementParameters::default();

            if input.is_persistent_command
                && !input.forward
                && !input.backward
                && matches!(
                    self.obj.interp.raw_state.forward_command,
                    WALK_FORWARD | WALK_BACKWARDS
                )
            {
                let forward = self.obj.interp.raw_state.forward_command;
                self.stop_motion_at_boundary(env, forward, p);
                motion_edge_fired = true;
            }

            if input.run != self.prev_run_held {
                self.obj.cx(env.world).set_hold_run(input.run, true);
                motion_edge_fired = true;
            }

            if input.is_persistent_command {
                let desired_forward = if input.forward {
                    WALK_FORWARD
                } else if input.backward {
                    WALK_BACKWARDS
                } else {
                    READY
                };
                let active_forward = self.obj.interp.raw_state.forward_command;
                let active_is_directional = matches!(active_forward, WALK_FORWARD | WALK_BACKWARDS);
                if active_is_directional && active_forward != desired_forward {
                    self.stop_motion_at_boundary(env, active_forward, p);
                    motion_edge_fired = true;
                }
                if desired_forward != READY && active_forward != desired_forward {
                    self.do_motion_at_boundary(env, desired_forward, p);
                    motion_edge_fired = true;
                }
            } else {
                if input.forward && !self.prev_forward_held {
                    self.do_motion_at_boundary(env, WALK_FORWARD, p);
                    motion_edge_fired = true;
                } else if input.backward && !self.prev_backward_held && !input.forward {
                    self.do_motion_at_boundary(env, WALK_BACKWARDS, p);
                    motion_edge_fired = true;
                }
                if !input.forward && self.prev_forward_held {
                    if input.backward {
                        self.do_motion_at_boundary(env, WALK_BACKWARDS, p);
                    } else {
                        self.stop_motion_at_boundary(env, WALK_FORWARD, p);
                    }
                    motion_edge_fired = true;
                } else if !input.backward && self.prev_backward_held && !input.forward {
                    self.stop_motion_at_boundary(env, WALK_BACKWARDS, p);
                    motion_edge_fired = true;
                }
            }

            let (desired_sidestep, sidestep_uses_run_hold) = Self::desired_input_sidestep(
                input,
                self.mouse_look_active && !self.modern_mouse_look,
            );
            if self.apply_input_sidestep(env, desired_sidestep, sidestep_uses_run_hold, false) {
                motion_edge_fired = true;
            }

            movement_event_requested |= motion_edge_fired;

            let keyboard_turn_edge = input.turn_right != self.prev_turn_right_held
                || input.turn_left != self.prev_turn_left_held;
            if keyboard_turn_edge {
                movement_event_requested = true;
            }

            let legacy_mouse_look = self.mouse_look_active && !self.modern_mouse_look;
            let keyboard_owns_turn = input.turn_left != input.turn_right;
            let mut desired_turn_command = if legacy_mouse_look {
                None
            } else if input.turn_right {
                Some(TURN_RIGHT)
            } else if input.turn_left {
                Some(TURN_LEFT)
            } else {
                None
            };
            let mut desired_turn_speed = 1.0;
            let mut desired_turn_from_mouse = false;
            let mut facing_turn = false;

            if self.mouse_look_active && self.modern_mouse_look && !keyboard_owns_turn {
                // Modern mouse turning: the turn keys are up, so the view's
                // target owns the turn, as a steady full-rate mouse turn.
                desired_turn_command = self.desired_facing_turn(env, input);
                desired_turn_from_mouse = desired_turn_command.is_some();
                facing_turn = true;
            } else if legacy_mouse_look && self.mouse_turn_sample_pending {
                let adjustment = self.mouse_turn_adjustment;
                self.mouse_turn_sample_pending = false;
                self.mouse_turn_adjustment = 0.0;
                if adjustment.abs() >= MOUSE_TURN_DEAD_ZONE {
                    desired_turn_command = Some(if adjustment < 0.0 {
                        TURN_RIGHT
                    } else {
                        TURN_LEFT
                    });
                    desired_turn_speed =
                        (adjustment.abs() * MOUSE_TURN_SPEED_SCALE).min(MOUSE_TURN_MAXIMUM_SPEED);
                    desired_turn_from_mouse = true;
                }
            } else if legacy_mouse_look && self.active_input_turn_from_mouse {
                desired_turn_command = self.active_input_turn_command;
                desired_turn_speed = self.active_input_turn_speed;
                desired_turn_from_mouse = true;
            }

            if self.apply_input_turn(
                env,
                desired_turn_command,
                desired_turn_speed,
                desired_turn_from_mouse,
                false,
            ) {
                motion_edge_fired = true;
                // A modern facing turn starts and stops the way a turn key
                // does, so the server hears each edge at once.
                if facing_turn {
                    movement_event_requested = true;
                }
            }

            if motion_edge_fired {
                self.obj.body.last_move_was_autonomous = true;
                self.controlled_by_server = false;
            }
        }

        self.prev_forward_held = input.forward;
        self.prev_backward_held = input.backward;
        self.prev_strafe_left_held = input.strafe_left;
        self.prev_strafe_right_held = input.strafe_right;
        self.prev_turn_left_held = input.turn_left;
        self.prev_turn_right_held = input.turn_right;
        self.prev_run_held = input.run;

        let has_animation_root_motion = self.root_motion.is_some() || self.animation_root_motion;
        let mut out_jump_extent = None;
        let mut out_jump_velocity = None;

        if input.jump && !self.jump_charging && !self.prev_jump_held {
            let charge = self.obj.cx(env.world).charge_jump();
            if charge.is_none() {
                self.jump_charging = true;
                self.jump_extent = 0.0;
            } else {
                self.report_jump_refusal(charge);
            }
        }

        if input.jump && self.jump_charging {
            let rate = if self.obj.interp.interpreted_state.current_style == DUAL_WIELD_COMBAT_STYLE
            {
                (1.0 / DUAL_WIELD_POWER_UP_SECONDS) as f32
            } else {
                (1.0 / ATTACK_POWER_UP_SECONDS) as f32
            };
            self.jump_extent = (self.jump_extent + dt * rate).min(1.0);
        } else if self.jump_charging {
            if let Some(exact) = input.jump_extent
                && exact.is_finite()
            {
                self.jump_extent = exact.clamp(0.0, 1.0);
            }
            let extent = self.jump_extent;
            let jump_result = self.obj.cx(env.world).jump(extent);
            if jump_result.is_none() {
                let mut cx = self.obj.cx(env.world);
                let jump_vz = cx.get_jump_vz();
                let state_velocity = cx.get_state_velocity();
                let velocity = Vec3::new(state_velocity.x, state_velocity.y, jump_vz);
                out_jump_extent = Some(extent);
                out_jump_velocity = Some(velocity);
                self.obj.body.set_local_velocity(velocity, true);
            } else {
                self.report_jump_refusal(jump_result);
            }
            self.jump_charging = false;
            self.jump_extent = 0.0;
        }
        self.prev_jump_held = input.jump;

        let pending_before = self.obj.clock.pending_seconds();
        let reactivated = self.obj.clock.activate();
        self.obj.body.transient_state.insert(TransientState::ACTIVE);
        let batch = if reactivated {
            RetailObjectQuantumBatch::default()
        } else {
            self.obj.clock.advance(f64::from(dt))
        };
        self.advanced_object_quantum_last_tick = batch.count() > 0;
        if batch.remainder > 0.0 {
            self.physics_step_seconds +=
                (batch.remainder - self.physics_step_seconds) * PHYSICS_STEP_AVERAGING;
        }
        self.presented_delta_seconds = self.compute_presented_delta(dt, pending_before, &batch);
        let mut just_landed = false;
        if batch.discarded {
            self.prev_physics_pos = self.obj.body.position();
            self.curr_physics_pos = self.obj.body.position();
        }

        for qi in 0..batch.count() {
            let tick_dt = batch.get_quantum(qi).unwrap_or(0.0);
            if self.stop_facing_turn_if_on_target(env) {
                motion_edge_fired = true;
                movement_event_requested = true;
            }
            let mut pm_delta = MotionDeltaFrame::default();
            let mut root = MotionDeltaFrame::default();
            if self.advance_root_motion(tick_dt, &mut root) {
                pm_delta.origin = if self.obj.body.on_walkable() {
                    root.origin * self.object_scale
                } else {
                    Vec3::ZERO
                };
                pm_delta.orientation = root.orientation;
            } else if self.obj.interp.interpreted_state.turn_command == TURN_RIGHT {
                // A body with no animation content turns at a fixed rate
                // (OpenAC's fallback for bodies without cycles).
                let yaw = self.yaw() - 1.5 * self.obj.interp.interpreted_state.turn_speed * tick_dt;
                self.set_yaw(yaw);
            }

            if self.obj.body.on_walkable() && !has_animation_root_motion {
                let saved_world_vz = self.obj.body.velocity.z;
                let state_velocity = self.obj.cx(env.world).get_state_velocity();
                let autonomous = self.obj.body.last_move_was_autonomous;
                self.obj.body.set_local_velocity(
                    Vec3::new(state_velocity.x, state_velocity.y, saved_world_vz),
                    autonomous,
                );
            }

            let pre_integrate_pos = self.obj.body.position();
            let old_tick_end_pos = self.curr_physics_pos;

            if self.obj.position_attached {
                self.obj
                    .cx(env.world)
                    .position_adjust_offset(&mut pm_delta, f64::from(tick_dt));
            }
            self.obj.body.is_fully_constrained =
                self.obj.position_attached && self.obj.position.is_fully_constrained();
            Self::apply_position_manager_delta(&mut self.obj.body, &pm_delta);
            self.land_facing_turn_on_target();

            self.obj.body.calc_acceleration();
            self.obj.body.update_physics_internal(tick_dt);

            self.take_animation_hooks(env);
            let post_integrate_pos = self.obj.body.position();
            let candidate_moved = post_integrate_pos != pre_integrate_pos;

            let on_ground = self.obj.body.on_walkable();
            let request = self.resolve_request(pre_integrate_pos, post_integrate_pos, on_ground);
            let resolved = env
                .engine
                .resolve_with_transition(&request, Some(&mut self.obj.body));

            self.obj.body.cached_velocity = if candidate_moved {
                (resolved.position - pre_integrate_pos) / tick_dt
            } else {
                Vec3::ZERO
            };

            let prev_contact = self.obj.body.in_contact();
            let prev_on_walkable = self.obj.body.on_walkable();

            self.obj
                .body
                .commit_transition_position(resolved.cell_id, resolved.position);
            self.prev_physics_pos = old_tick_end_pos;
            self.curr_physics_pos = self.obj.body.position();

            let mut landed_this_quantum = false;
            if resolved.ok && candidate_moved {
                self.obj
                    .body
                    .transient_state
                    .set(TransientState::CONTACT, resolved.in_contact);
                self.obj.body.calc_acceleration();
                if resolved.in_contact && resolved.on_walkable {
                    let was_airborne = !self.obj.body.on_walkable();
                    self.obj
                        .body
                        .transient_state
                        .insert(TransientState::ON_WALKABLE);
                    if was_airborne {
                        self.obj.cx(env.world).movement_hit_ground();
                        landed_this_quantum = true;
                    }
                } else {
                    self.obj
                        .body
                        .transient_state
                        .remove(TransientState::ON_WALKABLE);
                }
                self.obj.body.calc_acceleration();
                let now_on_walkable = self.obj.body.on_walkable();
                handle_all_collisions(
                    &mut self.obj.body,
                    resolved.collision_normal_valid,
                    resolved.collision_normal,
                    prev_contact,
                    prev_on_walkable,
                    now_on_walkable,
                );
            }

            if !self.obj.body.on_walkable() && !self.was_airborne_last_frame {
                self.obj.cx(env.world).leave_ground();
            }
            self.was_airborne_last_frame = !self.obj.body.on_walkable();
            just_landed |= landed_this_quantum;
            self.update_cell_id(env, resolved.cell_id, true);
            self.run_manager_tail(env);
        }

        let mut out_forward_cmd = None;
        let mut out_forward_speed = None;
        let mut out_sidestep_cmd = None;
        let mut out_sidestep_speed = None;
        let mut out_turn_cmd = None;
        let mut out_turn_speed = None;

        if input.forward {
            out_forward_cmd = Some(WALK_FORWARD);
            out_forward_speed = Some(1.0);
        } else if input.backward {
            out_forward_cmd = Some(WALK_BACKWARDS);
            out_forward_speed = Some(1.0);
        } else if matches!(
            self.obj.interp.raw_state.forward_command,
            CROUCH | SITTING | SLEEPING
        ) {
            out_forward_cmd = Some(self.obj.interp.raw_state.forward_command);
            out_forward_speed = Some(self.obj.interp.raw_state.forward_speed);
        }
        if let Some(active) = self.active_input_sidestep_command {
            out_sidestep_cmd = Some(active);
            out_sidestep_speed = Some(self.obj.interp.raw_state.sidestep_speed);
        }
        if let Some(active) = self.active_input_turn_command {
            out_turn_cmd = Some(active);
            out_turn_speed = Some(self.active_input_turn_speed);
        }

        let run_hold = input.run;
        let changed = out_forward_cmd != self.prev_forward_cmd
            || out_sidestep_cmd != self.prev_sidestep_cmd
            || out_turn_cmd != self.prev_turn_cmd
            || !floats_equal(out_forward_speed, self.prev_forward_speed)
            || run_hold != self.prev_run_hold
            || motion_edge_fired
            || externally_requested;

        let mouse_movement_event_due = self.mouse_movement_event_pending
            || (self.mouse_movement_event_candidate
                && self.sim_time_seconds
                    > self.last_mouse_movement_event_time + Self::MOUSE_MOVEMENT_EVENT_INTERVAL);
        self.mouse_movement_event_candidate = false;
        if mouse_movement_event_due {
            self.mouse_movement_event_pending = true;
        }
        let should_send_movement_event = movement_event_requested || mouse_movement_event_due;

        self.prev_forward_cmd = out_forward_cmd;
        self.prev_sidestep_cmd = out_sidestep_cmd;
        self.prev_turn_cmd = out_turn_cmd;
        self.prev_forward_speed = out_forward_speed;
        self.prev_run_hold = run_hold;

        MovementResult {
            motion_state_changed: changed,
            forward_command: out_forward_cmd,
            sidestep_command: out_sidestep_cmd,
            turn_command: out_turn_cmd,
            forward_speed: out_forward_speed,
            sidestep_speed: out_sidestep_speed,
            turn_speed: out_turn_speed,
            is_running: input.run,
            just_landed,
            jump_extent: out_jump_extent,
            jump_velocity: out_jump_velocity,
            should_send_movement_event,
            turn_uses_run_hold: self.active_input_turn_from_mouse && out_turn_cmd.is_some(),
            sidestep_uses_run_hold: self.active_input_sidestep_uses_run_hold
                && out_sidestep_cmd.is_some(),
            is_mouse_look_movement_event: mouse_movement_event_due,
            raw_motion_state_override: external_raw,
            ..MovementResult::bare(
                self.position(),
                self.render_position(),
                self.cell_id,
                self.obj.body.on_walkable(),
                self.obj.interp.raw_state.current_style,
            )
        }
    }

    fn report_jump_refusal(&mut self, result: WeenieError) {
        if matches!(
            result,
            WeenieError::NOT_GROUNDED
                | WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
                | WeenieError::CANT_JUMP_LOADED_DOWN
        ) {
            self.interface_refusals.push(result);
        }
    }
}

impl Default for PlayerMovementController {
    fn default() -> Self {
        Self::new()
    }
}

/// The client text for a jump refusal ("You can't jump while in the air",
/// ...), when it has one.
#[must_use]
pub fn jump_refusal_text(result: WeenieError) -> Option<&'static str> {
    use ac_core::chat::client_text;
    match result {
        WeenieError::NOT_GROUNDED => Some(client_text::CANT_JUMP_IN_AIR),
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION => Some(client_text::CANT_JUMP_POSITION),
        WeenieError::CANT_JUMP_LOADED_DOWN => Some(client_text::CANT_JUMP_LOAD),
        _ => None,
    }
}

fn floats_equal(a: Option<f32>, b: Option<f32>) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => (a - b).abs() < 1e-4,
        (None, None) => true,
        _ => false,
    }
}

fn approx_frame_equal(a: &CellPosition, b: &CellPosition) -> bool {
    let (ao, bo) = (a.frame.origin, b.frame.origin);
    let (aq, bq) = (a.frame.orientation, b.frame.orientation);
    (ao.x - bo.x).abs() <= SEND_EPSILON
        && (ao.y - bo.y).abs() <= SEND_EPSILON
        && (ao.z - bo.z).abs() <= SEND_EPSILON
        && (aq.w - bq.w).abs() < SEND_EPSILON
        && (aq.x - bq.x).abs() < SEND_EPSILON
        && (aq.y - bq.y).abs() < SEND_EPSILON
        && (aq.z - bq.z).abs() < SEND_EPSILON
}

fn approx_plane_equal(a: Plane, b: Plane) -> bool {
    (a.normal.x - b.normal.x).abs() <= SEND_EPSILON
        && (a.normal.y - b.normal.y).abs() <= SEND_EPSILON
        && (a.normal.z - b.normal.z).abs() <= SEND_EPSILON
        && (a.d - b.d).abs() < SEND_EPSILON
}
