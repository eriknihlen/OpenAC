//! The session's movement owner: the physics of the resident world, the
//! local player's body and movement controller, other objects' bodies, the
//! movement the player asks for (window keys, console moves), and what the
//! movement tells the server.
//!
//! One frame on the session thread (OpenAC `RuntimeLocalPlayerFrameController`):
//! before the network pass the player's input becomes motion, the body
//! moves quantum by quantum (the retail 30 Hz object clock slices the
//! frame's time), the movement event and jump go out, other bodies advance,
//! and every simulated position reaches the world (and from there an
//! attached window); after the network pass the autonomous position goes
//! out when its gate opens. Accepted server messages are applied in
//! between: the player's forced positions and teleports, server-driven
//! moves, other objects' motion and positions.

mod approach;
mod collision;
mod content;
mod entry;
pub mod input;
mod ordinary;
pub mod outbound;
pub mod position_log;
mod projectile;
mod readiness;
mod remote;
pub mod residency;
mod restrictions;
pub mod route;
pub mod server_move;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Duration;

use ac_core::physics::{PhysicsState, PositionDisposition};
use ac_motion::motion_command::{CROUCH, READY, SITTING, SLEEPING};
use ac_motion::move_to_math::get_heading;
use ac_physics::collision_flags::resolve_mover_pvp_state;
use ac_physics::movement::{
    InterpreterHost, MovementEnv, MovementInput, MovementManagerHost, PlayerMovementController,
    PlayerMovementOptions, PlayerState, TargetMessage, TargetOps, jump_refusal_text,
};
use ac_physics::{
    CellPosition, CollisionSphere, EntityCollisionFlags, ObjectInfoState, PhysicsEngine,
    PhysicsSetPositionFlags, PhysicsSetPositionRequest, Quat, Vec3,
};
use ac_proto::movement_out::MotionTimestamps;
use ac_proto::{Inbound, Position};

pub use ac_nav::motor::{
    MoveChannel, MoveChannelSnapshot, MoveDirection, MovePace, MoveRequest, MoveUnit,
    ScriptedMoveSnapshot, ScriptedMoveState, ScriptedMovement,
};
pub use content::{WorldContent, WorldContentError};
use input::ScriptedInput;
pub use outbound::{LocalPlayerOutbound, OutboundKind, OutboundRecord};
pub use readiness::BodyMotion;
pub(crate) use remote::wire_motion_state;
use remote::{
    BodiesInWorld, BodiesView, RemoteBody, body_shape_of, live_position, remote_motion_update,
    sticky_body_of,
};
pub use residency::Residency;
use route::{
    AcceptedPositionRouteRequest, AcceptedPositionSource, PositionAuthority, PositionEntityKind,
    classify_accepted_position,
};

use crate::session::Outbox;
use crate::world::{MotionView, ObjectKey, StepBlend, World, WorldChange, WorldObject};

/// The run skill id.
const SKILL_RUN: u32 = 24;
/// The jump skill id.
const SKILL_JUMP: u32 = 22;
/// The public weenie bitfield's player bit.
const PWD_PLAYER: u32 = 0x8;

/// The movement keys a window holds down (OpenAC's retail default key map:
/// W or Up forward, X or Down back, A/D or Left/Right turn, Z and C
/// sidestep, Shift held walks, Space jumps).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MovementKeys {
    /// Forward.
    pub forward: bool,
    /// Back up.
    pub backward: bool,
    /// Sidestep left.
    pub strafe_left: bool,
    /// Sidestep right.
    pub strafe_right: bool,
    /// Turn left.
    pub turn_left: bool,
    /// Turn right.
    pub turn_right: bool,
    /// Walk instead of the default run (Shift held).
    pub walk: bool,
    /// Jump held (charging; released jumps).
    pub jump: bool,
}

/// A posture the character can take.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Posture {
    /// Stand.
    Ready,
    /// Sit.
    Sit,
    /// Crouch.
    Crouch,
    /// Sleep (lie down).
    Sleep,
}

/// What a host asks of the movement.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum MovementCommand {
    /// The movement keys a window holds now.
    Keys(MovementKeys),
    /// Toggle the run lock (autorun).
    ToggleRunLock,
    /// Stop: end autorun, the keys a host holds and every scripted move.
    Stop,
    /// Take a posture.
    Posture(Posture),
    /// Begin a move (walk, run, strafe or turn, for a distance, angle or
    /// time).
    Move(MoveRequest),
    /// Jump at `power` (0..1] of a full charge; with `leave_at` the body
    /// presses forward at that pace as it leaves the ground.
    Jump {
        /// Charge, 0..1.
        power: f32,
        /// Leave the ground moving.
        leave_at: Option<MovePace>,
    },
    /// End every scripted move.
    StopMoves,
    /// Turn to a compass heading, degrees.
    TurnToHeading(f32),
    /// A rotate in the first-person view: turn this many compass degrees
    /// from the current heading, sending no movement of its own (the
    /// window re-issues it each frame a rotate is held; the heading goes
    /// with the position heartbeat). Silent when the character cannot move.
    FirstPersonTurn(f32),
    /// Reply with [`MovementEvent::Status`].
    Status,
    /// Instant mouse look began: held turns become sidesteps and the mouse
    /// turns the character (the movement goes to the server at once).
    BeginMouseLook,
    /// A filtered sideways mouse movement while looking (negative turns
    /// right).
    MouseTurn(f32),
    /// The mouse stopped while looking: the mouse turn stops.
    StopMouseDrift,
    /// Instant mouse look ended (the movement goes to the server at once).
    EndMouseLook,
    /// Modern mouse turning began (the right button pressed over the
    /// world): the view, not the mouse's motion, owns the turn, and the
    /// turn keys keep turning (the movement goes to the server at once).
    /// [`MovementCommand::EndMouseLook`] ends it.
    BeginModernMouseLook,
    /// Modern mouse turning: the yaw (radians from +x, counter-clockwise)
    /// the view points along. With the turn keys up, the character turns
    /// toward it with the ordinary turn motion at the full turn rate and
    /// stops on it.
    MouseLookFacing(f32),
    /// Play an action or emote motion as the player's own (an emote key);
    /// nothing when it cannot play now.
    Motion(u32),
}

/// Where the player is, for a host.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MovementStatus {
    /// Cell and landblock-relative position (as the server stores it).
    pub position: Position,
    /// Compass heading, degrees.
    pub heading_degrees: f32,
    /// On walkable ground.
    pub on_ground: bool,
    /// Velocity, metres per second.
    pub velocity: [f32; 3],
    /// Landblocks with collision resident.
    pub resident_landblocks: usize,
    /// Autorun on.
    pub auto_run: bool,
}

/// What the movement tells a host.
#[derive(Debug, Clone, PartialEq)]
pub enum MovementEvent {
    /// A scripted move ended.
    MoveEnded {
        /// Which channel.
        channel: MoveChannel,
        /// How it ended.
        state: ScriptedMoveState,
        /// Metres (degrees for a turn) covered.
        covered: f32,
        /// Seconds it took.
        elapsed_seconds: f32,
    },
    /// A command could not be carried out.
    Refused(String),
    /// The reply to [`MovementCommand::Status`].
    Status(Option<MovementStatus>),
    /// Collision for these landblocks was loaded and these dropped.
    Residency {
        /// Loaded.
        loaded: Vec<u32>,
        /// Dropped.
        dropped: Vec<u32>,
    },
}

/// What a walk reads and moves of the movement owner for one tick.
pub(crate) struct NavigationParts<'a> {
    /// The physics world.
    pub engine: &'a PhysicsEngine,
    /// The resident landblocks and the engine's frame.
    pub residency: &'a Residency,
    /// The local player's controller, once placed.
    pub controller: Option<&'a PlayerMovementController>,
    /// The scripted moves.
    pub input: &'a mut ScriptedInput,
    /// Autorun, which a scripted move ends.
    pub auto_run: &'a mut bool,
    /// Where each body the runtime moves stands (world frame), by client
    /// id.
    pub remote_positions: HashMap<u32, Vec3>,
}

/// The local player's body.
#[derive(Debug)]
struct LocalPlayer {
    guid: u32,
    key: ObjectKey,
    controller: PlayerMovementController,
    hidden: bool,
    /// How many server interpreted states the body took up (see
    /// [`crate::world::MotionView::serial`]).
    motions_applied: u32,
}

/// The character's entry into the world, waiting for its placement to
/// find a clear spot.
#[derive(Debug, Clone, Copy)]
struct PendingEntry {
    guid: u32,
    key: ObjectKey,
    accepted: Position,
}

/// A teleport's placement of the player, waiting for the destination's
/// collision.
#[derive(Debug, Clone, Copy)]
struct PendingPlace {
    guid: u32,
    accepted: Position,
    route: route::PositionRoute,
}

/// The session's movement owner.
#[derive(Debug)]
pub struct Movement {
    content: Option<Arc<WorldContent>>,
    engine: PhysicsEngine,
    residency: Residency,
    player: Option<LocalPlayer>,
    remotes: HashMap<u32, RemoteBody>,
    /// Missiles in flight, by client id.
    projectiles: HashMap<u32, projectile::Projectile>,
    /// Ordinary bodies (no motion, no missile) moving by a velocity, by
    /// client id.
    ordinaries: HashMap<u32, ordinary::OrdinaryBody>,
    /// Objects without a body of their own that entered the world, by
    /// client id.
    resting: HashMap<u32, entry::Resting>,
    /// Objects whose entry found no clear spot (not in the world for their
    /// incarnation), by client id.
    refused: HashSet<u32>,
    /// Objects whose create's first entry into the world is done, by client
    /// id: a later entry (the landblock back, the frame centred again)
    /// that finds no clear spot enters at the server's position instead of
    /// refusing it.
    first_entered: HashSet<u32>,
    /// The character's entry waiting for a clear spot: its create's
    /// position, placed again each frame until one is found.
    pending_entry: Option<PendingEntry>,
    keys: MovementKeys,
    auto_run: bool,
    /// Running is the default pace (the ToggleRun character option);
    /// the walk key then walks, else it runs.
    run_as_default: bool,
    input: ScriptedInput,
    collision: collision::ObjectCollision,
    outbound: LocalPlayerOutbound,
    last_frame: Option<Duration>,
    events: Vec<MovementEvent>,
    interface_text: Vec<String>,
    in_portal_space: bool,
    /// A portal arrival waits for the attached window.
    arrival_held: bool,
    /// A portal arrival waits for its destination's collision (no window
    /// holds it): the teleport's placement waits in `pending_place`.
    destination_hold: bool,
    /// The teleport placement waiting for its destination's collision.
    pending_place: Option<PendingPlace>,
    /// The held destination's square dropped or loaded something (the
    /// objects' collision follows once it is in).
    destination_loaded: bool,
    applied_stats: Option<(i32, i32, f32, i32)>,
    /// The player-killer status and last PK attack time the controller
    /// has (they price a jump's stamina).
    applied_pk: Option<(i32, Option<f32>)>,
    /// The walks to objects the character acts on.
    approaches: approach::Approaches,
    /// The movement keys a plugin holds (it replaces the keyboard while
    /// held).
    intent: Option<MovementInput>,
    /// The window went away during a modern mouse look: it ends at the
    /// next frame.
    end_modern_mouse_look: bool,
    /// The position log (off unless `OPENAC_POSITION_LOG=1`).
    position_log: position_log::PositionLog,
}

impl Default for Movement {
    fn default() -> Self {
        Self::new(false)
    }
}

/// What kind of object `item` is placed as: a hook (a hook type and the
/// item types it takes), a house's storage chest (its weenie class is the
/// dats' storage class) and a corpse (the corpse bit of its public
/// bitfield) are put into their cell as they are, never searched aside;
/// anything else is ordinary. Checked in that order.
fn placement_class(
    item: Option<&ac_core::items::ClientObject>,
    storage_weenie_class: u32,
) -> ac_physics::PhysicsPlacementClass {
    use ac_physics::PhysicsPlacementClass;
    let Some(item) = item else {
        return PhysicsPlacementClass::Ordinary;
    };
    if item.hook_type != 0 && item.hook_item_types != 0 {
        PhysicsPlacementClass::Hook
    } else if storage_weenie_class != 0 && item.weenie_class_id == storage_weenie_class {
        PhysicsPlacementClass::Storage
    } else if item
        .public_weenie_bitfield
        .is_some_and(|b| b.contains(ac_core::items::PublicWeenieFlags::CORPSE))
    {
        PhysicsPlacementClass::Corpse
    } else {
        PhysicsPlacementClass::Ordinary
    }
}

/// Whether an object with physics `state` that its placement put down as
/// `placed` accelerates from there with no velocity of its own: gravity
/// pulls a body that has gravity unless it rests on walkable ground (in
/// contact, the ground walkable, and not sledding), the body's own
/// acceleration rule.
fn falls_after_placement(state: u32, placed: &ac_physics::PhysicsSetPositionResult) -> bool {
    let mut body = ac_physics::PhysicsBody::default();
    body.state = ac_physics::PhysicsState::from(state);
    body.transient_state
        .set(ac_physics::TransientState::CONTACT, placed.in_contact);
    body.transient_state.set(
        ac_physics::TransientState::ON_WALKABLE,
        placed.in_contact && placed.on_walkable,
    );
    body.calc_acceleration();
    body.acceleration != Vec3::ZERO
}

/// Commit a placement's result to a body as the set-position commit does
/// (OpenAC `RuntimeSetPositionState` dormant commit): orientation, cell
/// frame, the contact plane the placement found, then the contact bits.
fn commit_placement(
    body: &mut ac_physics::PhysicsBody,
    result: &ac_physics::PhysicsSetPositionResult,
    orientation: Quat,
) {
    let previous_on_walkable = body.on_walkable();
    body.orientation = orientation;
    body.snap_to_cell(result.cell_id, result.position, result.cell_local_position);
    if result.forced_into_cell {
        // Forced into its cell: the frame and the cell only; it keeps the
        // contact it had.
        return;
    }
    body.contact_plane_valid = result.in_contact;
    body.contact_plane = result.contact_plane;
    body.contact_plane_cell_id = result.contact_plane_cell_id;
    body.contact_plane_is_water = result.contact_plane_is_water;
    if result.in_contact {
        body.ground_normal = result.contact_plane.normal;
    }
    ac_physics::obj_update::commit_set_position_contact_prefix(
        body,
        result.in_contact,
        result.on_walkable,
        previous_on_walkable,
    );
}

/// What a simulated body is doing, for presentation: its interpreter's
/// axes (the actions are the server's, added by the world) and whether it
/// stands on walkable ground.
fn live_motion<W: ac_physics::movement::WeenieObject>(
    obj: &ac_physics::movement::MovementObject<W>,
    serial: u32,
) -> MotionView {
    let s = &obj.interp.interpreted_state;
    MotionView {
        serial,
        state: ac_physics::movement::InboundInterpretedState {
            current_style: s.current_style,
            forward_command: s.forward_command,
            forward_speed: s.forward_speed,
            side_step_command: s.side_step_command,
            side_step_speed: s.side_step_speed,
            turn_command: s.turn_command,
            turn_speed: s.turn_speed,
            actions: None,
        },
        grounded: obj.body.in_contact() && obj.body.on_walkable(),
        standing_long_jump: obj.interp.standing_long_jump,
        body: Some(crate::world::BodyFacts {
            velocity: obj.body.cached_velocity.to_array(),
            in_contact: obj.body.in_contact(),
            contact_normal: obj.body.contact_plane.normal.to_array(),
        }),
    }
}

/// The motion a body the session moves passed through before it took a
/// server state up: the motion it showed (`shown`, the view a window last
/// had) with the axes it had once its movement under way was interrupted
/// (a move-to's turn stopped) and its stance entered. A window applies
/// only the server's states and the latest axes, never the interrupt
/// itself; without this step its copy would take the swing up while still
/// turning, where the motion table has no swing, and drop it. `None` when
/// the session did not move the body before.
fn interrupted_view(
    shown: Option<MotionView>,
    applied: &ac_physics::movement::RemoteMotionApplied,
) -> Option<MotionView> {
    let mut between = shown.filter(|m| m.body.is_some())?;
    let actions = between.state.actions.take();
    between.state = applied.interrupted.clone();
    between.state.actions = actions;
    Some(between)
}

/// The landblock-relative origin of a wire position.
fn local_origin(p: &Position) -> Vec3 {
    Vec3::new(p.origin.x, p.origin.y, p.origin.z)
}

/// An object's name for the position log (empty when none is known).
fn log_name(world: &World, guid: u32) -> String {
    world
        .item(guid)
        .map(|i| i.name.clone())
        .filter(|n| !n.is_empty())
        .or_else(|| world.name(guid).map(str::to_owned))
        .unwrap_or_default()
}

/// The heading the character turns to for one first-person rotate step,
/// compass degrees in [0, 360) (OpenAC
/// `RetailChaseCamera.FirstPersonTurnTargetHeading`).
#[must_use]
pub fn first_person_turn_target_heading(current_degrees: f32, step_degrees: f32) -> f32 {
    let target = (current_degrees + step_degrees) % 360.0;
    if target < 0.0 { target + 360.0 } else { target }
}

impl Movement {
    /// No content yet; with `keep_records` every movement message sent is
    /// remembered (tests read them with `take_outbound_records`).
    #[must_use]
    pub fn new(keep_records: bool) -> Self {
        Self {
            content: None,
            engine: PhysicsEngine::new(),
            residency: Residency::new(),
            player: None,
            remotes: HashMap::new(),
            projectiles: HashMap::new(),
            ordinaries: HashMap::new(),
            resting: HashMap::new(),
            refused: HashSet::new(),
            first_entered: HashSet::new(),
            pending_entry: None,
            keys: MovementKeys::default(),
            auto_run: false,
            run_as_default: true,
            input: ScriptedInput::new(),
            collision: collision::ObjectCollision::default(),
            outbound: LocalPlayerOutbound::new(keep_records),
            last_frame: None,
            events: Vec::new(),
            interface_text: Vec::new(),
            in_portal_space: false,
            arrival_held: false,
            destination_hold: false,
            pending_place: None,
            destination_loaded: false,
            applied_stats: None,
            applied_pk: None,
            approaches: approach::Approaches::default(),
            intent: None,
            end_modern_mouse_look: false,
            position_log: position_log::PositionLog::default(),
        }
    }

    /// Turn the position log on (`OPENAC_POSITION_LOG=1`).
    pub fn enable_position_log(&mut self) {
        self.position_log.enable();
    }

    /// The position log's lines since the last call, oldest first (none
    /// while it is off).
    pub fn take_position_log_lines(&mut self) -> Vec<String> {
        self.position_log.take_lines()
    }

    /// A portal arrival waits for the attached window: a body placed at the
    /// teleport's destination stays in portal space (it does not move)
    /// until [`Movement::arrive`]. False withdraws the wait without
    /// arriving (a newer teleport replaced it).
    pub fn set_arrival_held(&mut self, held: bool) {
        self.arrival_held = held;
    }

    /// A portal arrival no window holds waits for its destination's
    /// collision: the teleport's placement of the player waits until the
    /// destination's 3 x 3 square is loaded (OpenAC's headless host
    /// places through `PrepareDestination` once its collision
    /// neighbourhood is ready). Released by [`Movement::prepare_destination`]
    /// reporting it ready, by [`Movement::arrive`], or by
    /// [`Movement::release_destination_hold`].
    pub fn hold_destination(&mut self) {
        self.destination_hold = true;
        self.destination_loaded = false;
    }

    /// The held destination was abandoned (a newer teleport replaced it):
    /// its waiting placement is dropped.
    pub fn release_destination_hold(&mut self) {
        self.destination_hold = false;
        self.pending_place = None;
    }

    /// One step toward the held destination (OpenAC
    /// `HeadlessSessionWorldProjection.PrepareDestination`): the
    /// destination's square is asked for and loaded one landblock further;
    /// once it is loaded the waiting placement puts the player there. True
    /// when the arrival may complete: the player is placed, or there is
    /// nothing to place it with (no dats, no character, no placement, or
    /// the destination could not be loaded, which is refused).
    pub fn prepare_destination(&mut self, world: &mut World, outbox: &mut Outbox) -> bool {
        let Some(content) = self.content.clone() else {
            return true;
        };
        let Some(pending) = self.pending_place else {
            return true;
        };
        let cell = pending.accepted.cell_id;
        self.residency.request_center(&mut self.engine, cell);
        match self.residency.advance(&mut self.engine, &content) {
            Ok(step) => {
                if step.reset {
                    self.drop_remotes(world);
                    self.collision.forget_all();
                }
                self.destination_loaded |= step.reset || step.loaded.is_some();
                if !self.residency.is_ready(&self.engine, cell) {
                    return false;
                }
            }
            Err(error) => {
                self.refuse(&format!("collision could not be loaded: {error}"));
                self.release_destination_hold();
                return true;
            }
        }
        if std::mem::take(&mut self.destination_loaded) {
            self.sync_collision(world);
            self.enter_waiting(world);
        }
        self.release_destination_hold();
        self.place_player(
            world,
            pending.guid,
            &pending.accepted,
            &pending.route,
            outbox,
        );
        true
    }

    /// The portal arrival completed: the body leaves portal space and moves
    /// again.
    pub fn arrive(&mut self) {
        self.release_destination_hold();
        self.arrival_held = false;
        self.in_portal_space = false;
        if let Some(player) = self.player.as_mut() {
            player.controller.state = PlayerState::InWorld;
        }
    }

    /// Give the owner the dats it loads collision and motion from.
    pub fn install_content(&mut self, content: Arc<WorldContent>) {
        self.content = Some(content);
    }

    /// An object's body as an upright cylinder (radius, height), metres,
    /// from its setup and scale; `None` without the content or a setup.
    #[must_use]
    pub fn body_shape(&self, world: &World, guid: u32) -> Option<(f32, f32)> {
        body_shape_of(world, self.content.as_ref()?, guid)
    }

    /// Whether the dats are installed, so this owner simulates the world
    /// (the character enters it with a placement).
    #[must_use]
    pub fn has_content(&self) -> bool {
        self.content.is_some()
    }

    /// Whether the collision of the destination a teleport's placement
    /// waits for is loaded (diagnostics). `None` while no placement waits.
    #[must_use]
    pub fn destination_collision_ready(&self) -> Option<bool> {
        let pending = self.pending_place?;
        Some(
            self.residency
                .is_ready(&self.engine, pending.accepted.cell_id),
        )
    }

    /// The physics engine (diagnostics and tests).
    #[must_use]
    pub fn engine(&self) -> &PhysicsEngine {
        &self.engine
    }

    /// The resident landblocks.
    #[must_use]
    pub fn residency(&self) -> &Residency {
        &self.residency
    }

    /// The local player's controller, once the character is placed.
    #[must_use]
    pub fn controller(&self) -> Option<&PlayerMovementController> {
        self.player.as_ref().map(|p| &p.controller)
    }

    /// Room for a body the size of the character `distance_meters` straight
    /// ahead of where it faces (the room a pet needs before it is
    /// summoned), asked of this owner's physics world; nothing moves.
    /// Unknown before the character is placed in a cell. The result comes
    /// with the orientation the body faces.
    pub fn room_ahead(
        &mut self,
        distance_meters: f32,
    ) -> (ac_physics::placement_room::PlacementRoomResult, Quat) {
        let Some(controller) = self
            .player
            .as_ref()
            .map(|p| &p.controller)
            .filter(|c| c.cell_id() != 0)
        else {
            return Default::default();
        };
        let here = controller.current_cell_position();
        let body = ac_physics::placement_room::PlacementRoomBody {
            spheres: &controller.sphere_list,
            scale: controller.object_scale,
            step_up_height: controller.step_up_height,
            step_down_height: controller.step_down_height,
            self_entity_id: controller.local_entity_id,
        };
        let result = ac_physics::placement_room::check(
            &mut self.engine,
            &body,
            controller.position(),
            here.frame.orientation,
            here.obj_cell_id,
            here.frame.origin,
            distance_meters,
        );
        (result, here.frame.orientation)
    }

    /// The physics engine, to stage collision in tests.
    #[cfg(test)]
    pub(crate) fn engine_mut(&mut self) -> &mut PhysicsEngine {
        &mut self.engine
    }

    /// Take what the host should hear.
    pub fn drain_events(&mut self) -> Vec<MovementEvent> {
        std::mem::take(&mut self.events)
    }

    /// Take the client text to show (jump refusals).
    pub fn drain_interface_text(&mut self) -> Vec<String> {
        std::mem::take(&mut self.interface_text)
    }

    /// Take the movement messages sent (only with records kept).
    pub fn take_outbound_records(&mut self) -> Vec<OutboundRecord> {
        self.outbound.take_records()
    }

    /// The player's positions sent since the last call, oldest first
    /// (always kept, for the session's position trail).
    pub fn drain_sent_positions(&mut self) -> std::vec::Drain<'_, OutboundRecord> {
        self.outbound.drain_sent()
    }

    /// Objects the world forgot on its own (their time out of range ran
    /// out, recorded after `mark`) lose their bodies.
    pub fn observe_expiry(&mut self, world: &mut World, mark: usize) {
        let removed: Vec<ObjectKey> = world
            .changes_since(mark)
            .iter()
            .filter_map(|c| match c {
                WorldChange::Removed(key) => Some(*key),
                _ => None,
            })
            .collect();
        for key in removed {
            self.remotes.remove(&key.local_id);
        }
    }

    /// The player's indoor cell as the physics has it, with what it sees
    /// (none outdoors or before the world is loaded).
    #[must_use]
    pub fn indoor_cell(&self) -> Option<crate::world::liveness::IndoorCell<'_>> {
        let world = self.engine.world()?;
        let cell = world.cell(world.curr_cell()?)?;
        Some(crate::world::liveness::IndoorCell {
            id: cell.id,
            seen_outside: cell.seen_outside,
            visible_cells: cell.visible_cells.ordered_ids(),
        })
    }

    /// The character left the world: everything is forgotten.
    pub fn left_world(&mut self) {
        self.approaches.retire_lifetime();
        self.residency.reset(&mut self.engine);
        self.collision.forget_all();
        self.player = None;
        self.remotes.clear();
        self.projectiles.clear();
        self.ordinaries.clear();
        self.resting.clear();
        self.refused.clear();
        self.first_entered.clear();
        self.pending_entry = None;
        self.engine.set_objects(None);
        self.intent = None;
        self.keys = MovementKeys::default();
        self.auto_run = false;
        self.input.lose();
        self.end_modern_mouse_look = false;
        self.last_frame = None;
        self.in_portal_space = false;
        self.arrival_held = false;
        self.release_destination_hold();
        self.applied_stats = None;
        self.applied_pk = None;
    }

    /// The window that held movement keys went away (a modern mouse look
    /// it held ends at the next frame).
    pub fn release_keys(&mut self) {
        self.keys = MovementKeys::default();
        self.end_modern_mouse_look = true;
    }

    /// Whether held movement runs by default (the ToggleRun character
    /// option; on until the character's options say otherwise). Off, the
    /// keys walk and the walk key runs.
    pub fn set_run_as_default(&mut self, on: bool) {
        self.run_as_default = on;
    }

    fn timestamps(world: &World, guid: u32) -> MotionTimestamps {
        world
            .object(guid)
            .map(|o| {
                let s = o.timestamps();
                MotionTimestamps {
                    instance: s.instance,
                    server_control: s.server_controlled_move,
                    teleport: s.teleport,
                    force_position: s.force_position,
                }
            })
            .unwrap_or_default()
    }

    // ── commands ───────────────────────────────────────────────────────

    /// Play an action or emote motion as the player's own (a pose in
    /// speech); its movement event goes out on the next pass. False when
    /// there is no body or the motion cannot play now.
    pub fn execute_motion(&mut self, motion: u32, world: &World) -> bool {
        self.with_controller(world, |c, env| c.request_command_motion(env, motion)) == Some(true)
    }

    /// A jump is being charged.
    #[must_use]
    pub fn jump_charging(&self) -> bool {
        self.controller()
            .is_some_and(|c| c.jump_charge().is_charging)
    }

    /// The body stands still on the ground (true with no body).
    #[must_use]
    pub fn is_standing_still(&self) -> bool {
        self.controller()
            .is_none_or(PlayerMovementController::is_standing_still)
    }

    /// End the jump being charged without jumping (the Escape key).
    pub fn finish_jump(&mut self) {
        if let Some(player) = self.player.as_mut() {
            player.controller.finish_jump();
        }
    }

    /// Stop everything at once (the Escape key while moving): autorun, a
    /// plugin's held keys, the scripted moves and the body's own motion.
    /// The keys the player holds stay held, as for the stop key: a forward
    /// key still down moves again only once pressed again (the controller
    /// acts on key changes).
    pub fn stop_completely(&mut self, world: &World) {
        self.auto_run = false;
        self.intent = None;
        self.input.scripted_mut().stop();
        self.with_controller(world, |c, env| c.stop_completely(env));
    }

    /// Carry out a host's movement command.
    pub fn command(&mut self, command: MovementCommand, world: &World, outbox: &mut Outbox) {
        match command {
            MovementCommand::Keys(keys) => {
                // Retail ends autorun for a forward or back-up press (a
                // sidestep or turn steers the run instead).
                if self.auto_run
                    && ((keys.forward && !self.keys.forward)
                        || (keys.backward && !self.keys.backward))
                {
                    self.auto_run = false;
                }
                self.keys = keys;
            }
            MovementCommand::ToggleRunLock => self.auto_run = !self.auto_run,
            MovementCommand::Stop => {
                // Autorun and a plugin's held keys end, and the forward
                // axis goes back to ready as the Ready posture puts it. The
                // keys the player holds stay held: a forward key still down
                // moves again only once pressed again (the controller acts
                // on key changes), and a jump being charged keeps charging.
                self.auto_run = false;
                self.intent = None;
                self.input.scripted_mut().stop();
                self.with_controller(world, |c, env| c.request_posture(env, READY));
            }
            MovementCommand::Posture(posture) => {
                // As the stop key: the held keys stay held (a jump being
                // charged keeps charging).
                self.auto_run = false;
                self.intent = None;
                let motion = match posture {
                    Posture::Ready => READY,
                    Posture::Sit => SITTING,
                    Posture::Crouch => CROUCH,
                    Posture::Sleep => SLEEPING,
                };
                let accepted = self.with_controller(world, |c, env| c.request_posture(env, motion));
                if accepted != Some(true) {
                    self.refuse("That posture is not possible now.");
                }
            }
            MovementCommand::Move(request) => {
                if self.player.is_none() {
                    self.refuse("The character is not in the world.");
                } else if !self.begin_move(request) {
                    self.refuse("That move is past what the client carries out.");
                }
            }
            MovementCommand::Jump { power, leave_at } => {
                if self.player.is_none() {
                    self.refuse("The character is not in the world.");
                } else if !self.begin_jump(power, leave_at) {
                    self.refuse("A jump takes a power above 0 and at most 1, one at a time.");
                }
            }
            MovementCommand::StopMoves => {
                if !self.stop_moves() {
                    self.refuse("No move to stop.");
                }
            }
            MovementCommand::TurnToHeading(heading) => {
                if !self.turn_to_heading(heading) {
                    self.refuse("The character cannot turn now.");
                }
            }
            MovementCommand::FirstPersonTurn(step) => {
                self.first_person_turn(step);
            }
            MovementCommand::Status => {
                let status = self.status();
                self.events.push(MovementEvent::Status(status));
            }
            MovementCommand::BeginMouseLook => {
                let input = self.key_input();
                let begun =
                    self.with_controller(world, |c, env| c.begin_mouse_look(env, input, false));
                if begun == Some(true) {
                    self.send_movement_now(world, outbox);
                }
            }
            MovementCommand::BeginModernMouseLook => {
                let input = self.key_input();
                let begun =
                    self.with_controller(world, |c, env| c.begin_mouse_look(env, input, true));
                if begun == Some(true) {
                    self.send_movement_now(world, outbox);
                }
            }
            MovementCommand::MouseLookFacing(yaw) => {
                if let Some(player) = self.player.as_mut() {
                    player.controller.set_mouse_look_facing_target(yaw);
                }
            }
            MovementCommand::MouseTurn(adjustment) => {
                let input = self.key_input();
                self.with_controller(world, |c, env| {
                    c.submit_mouse_turn_adjustment(env, adjustment, input);
                });
            }
            MovementCommand::StopMouseDrift => {
                let input = self.key_input();
                self.with_controller(world, |c, env| c.stop_mouse_drift(env, input));
            }
            MovementCommand::EndMouseLook => {
                let input = self.key_input();
                let ended = self.with_controller(world, |c, env| c.end_mouse_look(env, input));
                if ended == Some(true) {
                    self.send_movement_now(world, outbox);
                }
            }
            MovementCommand::Motion(motion) => {
                self.execute_motion(motion, world);
            }
        }
    }

    /// The movement as it stands, to the server now (a mouse-look toggle);
    /// nothing while hidden.
    fn send_movement_now(&mut self, world: &World, outbox: &mut Outbox) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        if player.hidden {
            return;
        }
        let stamps = Self::timestamps(world, player.guid);
        let movement = player.controller.capture_movement_result(false);
        self.outbound
            .try_send_movement(outbox, &mut player.controller, &movement, &stamps);
    }

    /// Begins a scripted move on its channel, replacing only a move already
    /// on that channel; ends autorun. False with no character or a move
    /// past the limits.
    pub(crate) fn begin_move(&mut self, request: MoveRequest) -> bool {
        if self.player.is_none() || !self.input.scripted_mut().begin(request) {
            return false;
        }
        self.auto_run = false;
        true
    }

    /// Holds a plugin's movement keys until they are replaced or cleared;
    /// false with no character.
    pub(crate) fn set_intent(&mut self, intent: MovementInput) -> bool {
        if self.player.is_none() {
            return false;
        }
        self.intent = Some(MovementInput {
            mouse_delta_x: 0.0,
            is_persistent_command: false,
            jump_extent: None,
            ..intent
        });
        true
    }

    /// Releases a plugin's held keys; false when none were held.
    pub(crate) fn clear_intent(&mut self) -> bool {
        self.intent.take().is_some()
    }

    /// A plugin's held keys steer the character (a direction or turn is
    /// held).
    #[must_use]
    pub fn intent_steers(&self) -> bool {
        self.intent.is_some_and(|i| {
            i.forward
                || i.backward
                || i.strafe_left
                || i.strafe_right
                || i.turn_left
                || i.turn_right
        })
    }

    /// Whether a plugin holds movement keys.
    #[must_use]
    pub fn has_intent(&self) -> bool {
        self.intent.is_some()
    }

    /// Ends every scripted move; false when none was under way.
    pub(crate) fn stop_moves(&mut self) -> bool {
        self.input.scripted_mut().stop()
    }

    /// Ends the scripted move on one channel; false when it had none.
    pub(crate) fn stop_channel(&mut self, channel: MoveChannel) -> bool {
        self.input.scripted_mut().stop_channel(channel)
    }

    /// Charges a jump for `power` of a full charge, then releases it.
    pub(crate) fn begin_jump(&mut self, power: f32, leave_at: Option<MovePace>) -> bool {
        self.player.is_some() && self.input.scripted_mut().begin_jump(power, leave_at)
    }

    /// The client changed the character's motion table on its own (the
    /// barber's Empyrean toggle): the physics record takes the table, and
    /// the body animates by it under a new movement manager, as a body
    /// that changes its table does. False without a character body, or when
    /// the table's animations cannot be read (the record still takes it).
    pub fn set_player_motion_table(&mut self, world: &mut World, table: u32) -> bool {
        let Some(guid) = world.player_guid() else {
            return false;
        };
        world.set_motion_table(guid, table);
        let Some(content) = self.content.clone() else {
            return false;
        };
        let Some(sequencer) = world
            .object(guid)
            .and_then(|o| o.physics().setup_id)
            .and_then(|setup| content.sequencer(setup, table))
        else {
            return false;
        };
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return false;
        };
        let mut env = MovementEnv::new(&mut self.engine, &view);
        player.controller.replace_motion_table(&mut env, sequencer);
        true
    }

    /// Turns the character to a compass heading through its move-to
    /// manager; false when it cannot turn now.
    pub(crate) fn turn_to_heading(&mut self, heading: f32) -> bool {
        let normalized = heading.rem_euclid(360.0);
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return false;
        };
        let mut env = MovementEnv::new(&mut self.engine, &view);
        player
            .controller
            .request_turn_to_heading(&mut env, normalized, false, true)
    }

    /// One first-person rotate step: turn `step` degrees from the current
    /// heading with no movement event of its own.
    fn first_person_turn(&mut self, step: f32) -> bool {
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return false;
        };
        let heading = ac_motion::move_to_math::heading_from_yaw(player.controller.yaw());
        let target = first_person_turn_target_heading(heading, step);
        let mut env = MovementEnv::new(&mut self.engine, &view);
        player
            .controller
            .request_turn_to_heading(&mut env, target, false, false)
    }

    /// The latest scripted move on each channel and the latest jump.
    #[must_use]
    pub fn scripted_snapshot(&self) -> ScriptedMoveSnapshot {
        self.input.snapshot()
    }

    /// The dats, once installed.
    #[must_use]
    pub(crate) fn content(&self) -> Option<&Arc<WorldContent>> {
        self.content.as_ref()
    }

    /// What a walk reads and moves for one tick: the physics world, the
    /// resident landblocks, the local player's body, its scripted moves and
    /// autorun, and where every body the runtime moves stands.
    pub(crate) fn navigation_parts(&mut self) -> NavigationParts<'_> {
        NavigationParts {
            engine: &self.engine,
            residency: &self.residency,
            controller: self.player.as_ref().map(|p| &p.controller),
            input: &mut self.input,
            auto_run: &mut self.auto_run,
            remote_positions: self
                .remotes
                .values()
                .filter(|r| r.driver.obj.cell_id != 0)
                .map(|r| (r.key.local_id, r.driver.obj.body.position()))
                .collect(),
        }
    }

    /// Report something the owner could not do (printed as a movement line).
    pub(crate) fn refuse(&mut self, text: &str) {
        self.events.push(MovementEvent::Refused(text.to_owned()));
    }

    fn status(&self) -> Option<MovementStatus> {
        let player = self.player.as_ref()?;
        let c = &player.controller;
        let p = c.try_get_outbound_position()?;
        Some(MovementStatus {
            position: outbound::wire_position(&p),
            heading_degrees: get_heading(c.body_orientation()),
            on_ground: c.can_send_position_event(),
            velocity: c.body_velocity().to_array(),
            resident_landblocks: self.residency.resident().count(),
            auto_run: self.auto_run,
        })
    }

    fn with_controller<R>(
        &mut self,
        world: &World,
        f: impl FnOnce(&mut PlayerMovementController, &mut MovementEnv<'_>) -> R,
    ) -> Option<R> {
        let _ = world;
        let view = self.bodies_view();
        let player = self.player.as_mut()?;
        let mut env = MovementEnv::new(&mut self.engine, &view);
        Some(f(&mut player.controller, &mut env))
    }

    // ── inbound ────────────────────────────────────────────────────────

    /// Follow what one server message changed in the world (the world's
    /// changes after `mark`), then the message itself where the world does
    /// not record it (the teleport notice).
    pub fn observe(
        &mut self,
        world: &mut World,
        message: &Inbound<'_>,
        mark: usize,
        outbox: &mut Outbox,
        now: Duration,
    ) {
        if self.content.is_none() {
            return;
        }
        if let Inbound::PlayerTeleport(_) = message {
            self.begin_portal_space();
        }
        let changes: Vec<WorldChange> = world.changes_since(mark).to_vec();
        if self.position_log.is_on()
            && let Inbound::UpdatePosition(update) = message
            && !changes.iter().any(|c| {
                matches!(c, WorldChange::Moved { key, .. }
                    if Self::guid_of(world, *key) == Some(update.guid))
            })
        {
            // The world kept the object where it was: the update's counters
            // were not newer, or its place was not a valid one.
            self.log_unapplied_position(world, update, now);
        }
        // House barriers read the objects the world knows: the table follows
        // objects arriving and leaving and item records changing.
        let restrictions_changed = changes.iter().any(|c| {
            matches!(
                c,
                WorldChange::Spawned(_)
                    | WorldChange::Respawned(_)
                    | WorldChange::Removed(_)
                    | WorldChange::Item(
                        crate::world::ItemChange::Added(_)
                            | crate::world::ItemChange::Updated(_)
                            | crate::world::ItemChange::Removed { .. }
                    )
            )
        });
        if restrictions_changed {
            self.engine.set_objects(Some(restrictions::build(world)));
        }
        for change in changes {
            match change {
                WorldChange::Spawned(key) | WorldChange::Respawned(key) => {
                    self.on_spawn(world, key, outbox, now);
                }
                WorldChange::Removed(key)
                | WorldChange::PickedUp(key)
                | WorldChange::Parented(key) => {
                    self.on_leave(world, key);
                }
                WorldChange::Moved {
                    key,
                    teleported,
                    forced,
                } => self.on_position(world, key, teleported, forced, message, outbox, now),
                WorldChange::MotionChanged(key) => self.on_motion(world, key, message, now),
                WorldChange::StateChanged(key) => self.on_state(world, key, now),
                WorldChange::VectorChanged(key) => self.on_vector(world, key, now),
                WorldChange::Item(crate::world::ItemChange::Updated(guid)) => {
                    self.on_item_updated(world, guid);
                }
                _ => {}
            }
        }
    }

    fn guid_of(world: &World, key: ObjectKey) -> Option<u32> {
        world
            .object_by_local_id(key.local_id)
            .filter(|o| o.key() == key)
            .map(crate::world::WorldObject::guid)
    }

    fn begin_portal_space(&mut self) {
        self.in_portal_space = true;
        self.input.lose();
        if let Some(player) = self.player.as_mut() {
            player.controller.state = PlayerState::PortalSpace;
        }
    }

    fn on_spawn(&mut self, world: &mut World, key: ObjectKey, outbox: &mut Outbox, now: Duration) {
        let Some(guid) = Self::guid_of(world, key) else {
            return;
        };
        if world.player_guid() == Some(guid) {
            self.enter_world(world, guid, key, outbox);
            return;
        }
        // A respawn starts a new body and enters the world again.
        self.remotes.remove(&key.local_id);
        self.projectiles.remove(&key.local_id);
        self.ordinaries.remove(&key.local_id);
        if let Some(rest) = self.resting.remove(&key.local_id) {
            world.clear_live_position(rest.guid);
        }
        self.refused.remove(&key.local_id);
        self.first_entered.remove(&key.local_id);
        // Its rows first, so an object entering the world moves them to
        // where its placement put it.
        self.register_collision(world, guid);
        self.enter_object(world, guid, now);
    }

    /// A missile the runtime does not animate starts flying where it
    /// entered the world.
    fn bind_projectile(&mut self, world: &mut World, guid: u32, now: Duration) -> bool {
        let placed = match self.entry_placement(world, guid) {
            entry::Entry::Placed(placed) => Some(placed),
            entry::Entry::AtServer => None,
            entry::Entry::Refused | entry::Entry::Waiting => return false,
        };
        self.bind_projectile_at(world, guid, now, placed.as_ref())
    }

    /// A missile starts flying at its entry placement `placed`, else where
    /// the server put it.
    fn bind_projectile_at(
        &mut self,
        world: &mut World,
        guid: u32,
        now: Duration,
        placed: Option<&ac_physics::PhysicsSetPositionResult>,
    ) -> bool {
        let Some(content) = self.content.clone() else {
            return false;
        };
        let Some(object) = world.object(guid) else {
            return false;
        };
        let Some(accepted) = object.accepted_position() else {
            return false;
        };
        if !self.residency.is_resident(accepted.cell_id) {
            return false;
        }
        let Some(origin) = self.residency.origin_of(accepted.cell_id) else {
            return false;
        };
        let Some(mut missile) =
            projectile::Projectile::bind(object, &content, origin, now.as_secs_f64())
        else {
            return false;
        };
        let mut origin = origin;
        if let Some(placed) = placed {
            commit_placement(&mut missile.body, placed, placed.orientation);
            missile.cell_id = placed.cell_id;
            origin = self.residency.origin_of(placed.cell_id).unwrap_or(origin);
        }
        let live = missile.live_position(origin);
        let local_id = missile.key.local_id;
        let at = (
            missile.body.position(),
            missile.body.orientation,
            missile.cell_id,
        );
        self.projectiles.insert(local_id, missile);
        self.resting.remove(&local_id);
        world.set_live_position(guid, live);
        if placed.is_some() {
            self.follow_rows(local_id, at);
        }
        true
    }

    /// An object's rows follow it to `at` (world position, orientation,
    /// cell).
    fn follow_rows(&mut self, local_id: u32, at: (Vec3, Quat, u32)) {
        if let Err(error) = self.collision.follow(
            &mut self.engine,
            &self.residency,
            local_id,
            at.0,
            at.1,
            at.2,
        ) {
            self.refuse(&format!(
                "an object's collision could not follow it: {error}"
            ));
        }
    }

    /// An object the runtime neither animates nor flies gets a body of its
    /// own once it has a velocity or spin to move by (`force`: a velocity
    /// update just arrived), or once its placement leaves it where gravity
    /// pulls it (it has gravity and does not rest on walkable ground),
    /// starting where it entered the world; at rest it stays where it
    /// entered.
    fn bind_ordinary(&mut self, world: &mut World, guid: u32, now: Duration, force: bool) -> bool {
        let placed = match self.entry_placement(world, guid) {
            entry::Entry::Placed(placed) => Some(placed),
            entry::Entry::AtServer => None,
            entry::Entry::Refused | entry::Entry::Waiting => return false,
        };
        self.bind_ordinary_at(world, guid, now, force, placed.as_ref())
    }

    /// [`Self::bind_ordinary`] at the entry placement `placed`, else where
    /// the server put it.
    fn bind_ordinary_at(
        &mut self,
        world: &mut World,
        guid: u32,
        now: Duration,
        force: bool,
        placed: Option<&ac_physics::PhysicsSetPositionResult>,
    ) -> bool {
        let Some(content) = self.content.clone() else {
            return false;
        };
        let Some(object) = world.object(guid) else {
            return false;
        };
        let physics = object.physics();
        let moving = |v: Option<ac_proto::Vector3>| {
            v.is_some_and(|v| v.x != 0.0 || v.y != 0.0 || v.z != 0.0)
        };
        if object.movement().is_some() || physics.state & PhysicsState::MISSILE.bits() != 0 {
            return false;
        }
        let still = !force && !moving(physics.velocity) && !moving(physics.omega);
        if still && !placed.is_some_and(|p| falls_after_placement(physics.state, p)) {
            return false;
        }
        let Some(accepted) = object.accepted_position() else {
            return false;
        };
        if !self.residency.is_resident(accepted.cell_id) {
            return false;
        }
        let Some(origin) = self.residency.origin_of(accepted.cell_id) else {
            return false;
        };
        let Some(mut body) =
            ordinary::OrdinaryBody::bind(object, &content, origin, now.as_secs_f64())
        else {
            return false;
        };
        let mut origin = origin;
        if let Some(placed) = placed {
            commit_placement(&mut body.body, placed, placed.orientation);
            body.cell_id = placed.cell_id;
            origin = self.residency.origin_of(placed.cell_id).unwrap_or(origin);
        }
        let live = body.live_position(origin);
        let local_id = body.key.local_id;
        let at = (body.body.position(), body.body.orientation, body.cell_id);
        self.ordinaries.insert(local_id, body);
        self.resting.remove(&local_id);
        world.set_live_position(guid, live);
        if placed.is_some() {
            self.follow_rows(local_id, at);
        }
        true
    }

    /// Ordinary bodies move, their collision follows them, and the world
    /// sees where they are; one that left the resident world is dropped.
    fn advance_ordinaries(&mut self, world: &mut World, dt: f32) {
        if self.ordinaries.is_empty() {
            return;
        }
        let player_position = self.player.as_ref().map(|p| p.controller.position());
        let engine = &self.engine;
        let mut moved = Vec::new();
        for (local_id, body) in &mut self.ordinaries {
            if body.advance(engine, dt, player_position) {
                moved.push(*local_id);
            }
        }
        for local_id in moved {
            let Some(body) = self.ordinaries.get(&local_id) else {
                continue;
            };
            let (guid, at, orientation, cell_id) = (
                body.guid,
                body.body.position(),
                body.body.orientation,
                body.cell_id,
            );
            let origin = self
                .residency
                .is_resident(cell_id)
                .then(|| self.residency.origin_of(cell_id))
                .flatten();
            let Some(origin) = origin else {
                self.ordinaries.remove(&local_id);
                world.clear_live_position(guid);
                continue;
            };
            world.set_live_position(guid, body.live_position(origin));
            if let Err(error) = self.collision.follow(
                &mut self.engine,
                &self.residency,
                local_id,
                at,
                orientation,
                cell_id,
            ) {
                self.refuse(&format!(
                    "an object's collision could not follow it: {error}"
                ));
            }
        }
    }

    /// Registers (again) a server object's collision where it stands.
    fn register_collision(&mut self, world: &World, guid: u32) {
        let Some(content) = self.content.clone() else {
            return;
        };
        if let Err(error) =
            self.collision
                .register(&mut self.engine, &content, &self.residency, world, guid)
        {
            self.refuse(&format!(
                "the collision of 0x{guid:08X} could not be registered: {error}"
            ));
        }
        // An object resting where its entry put it keeps its rows there.
        if let Some(local_id) = world.object(guid).map(|o| o.key().local_id) {
            self.follow_resting(local_id);
        }
    }

    /// A body's rows carry the body's own state, which its animation may
    /// have turned ethereal or solid since the server last set it.
    fn rows_take_body_state(&mut self, local_id: u32) {
        if let Some(body) = self.remotes.get(&local_id) {
            self.collision.update_state(
                &mut self.engine,
                local_id,
                body.driver.obj.body.state.bits(),
            );
        }
    }

    /// The resident world changed: objects in landblocks that became
    /// resident collide, those in landblocks that left do not.
    fn sync_collision(&mut self, world: &World) {
        let Some(content) = self.content.clone() else {
            return;
        };
        for (guid, error) in self
            .collision
            .sync(&mut self.engine, &content, &self.residency, world)
        {
            self.refuse(&format!(
                "the collision of 0x{guid:08X} could not be registered: {error}"
            ));
        }
        let resting: Vec<u32> = self.resting.keys().copied().collect();
        for local_id in resting {
            self.follow_resting(local_id);
        }
        let bodies: Vec<u32> = self.remotes.keys().copied().collect();
        for local_id in bodies {
            self.rows_take_body_state(local_id);
        }
    }

    fn on_leave(&mut self, world: &mut World, key: ObjectKey) {
        self.collision.deregister(&mut self.engine, key.local_id);
        if world.object_by_local_id(key.local_id).is_none() {
            self.refused.remove(&key.local_id);
            self.first_entered.remove(&key.local_id);
        }
        if self.pending_entry.is_some_and(|p| p.key == key) {
            self.pending_entry = None;
        }
        if self.position_log.is_on()
            && let Some(guid) = Self::guid_of(world, key)
        {
            self.position_log.forget(guid);
        }
        let flew = self.projectiles.remove(&key.local_id).is_some()
            | self.ordinaries.remove(&key.local_id).is_some()
            | self.resting.remove(&key.local_id).is_some();
        if (self.remotes.remove(&key.local_id).is_some() || flew)
            && let Some(guid) = Self::guid_of(world, key)
        {
            world.clear_live_position(guid);
        }
        if self.player.as_ref().is_some_and(|p| p.key == key) {
            self.player = None;
        }
    }

    /// A body for an object with motion, starting where it entered the
    /// world (its create's placement, which may slide it aside so it does
    /// not start inside another body); none while it waits to enter, or
    /// when its entry found no clear spot.
    fn arm_remote(&mut self, world: &mut World, guid: u32) -> bool {
        let Some(content) = self.content.clone() else {
            return false;
        };
        let placed = match self.entry_placement(world, guid) {
            entry::Entry::Placed(placed) => Some(placed),
            entry::Entry::AtServer => None,
            entry::Entry::Refused | entry::Entry::Waiting => return false,
        };
        let Some(object) = world.object(guid) else {
            return false;
        };
        let Some(accepted) = object.accepted_position() else {
            return false;
        };
        let Some(origin) = self.residency.origin_of(accepted.cell_id) else {
            return false;
        };
        let Some(mut body) =
            RemoteBody::arm(object, &self.engine, &content, origin, placed.as_ref())
        else {
            return false;
        };
        body.driver.placement_class =
            placement_class(world.item(guid), content.storage_weenie_class());
        let described = object
            .movement()
            .map(|data| self.remote_update(world, &content, data, accepted.cell_id));
        body.enter_world(&self.bodies_view(), described.as_ref());
        let cell_id = body.driver.obj.cell_id;
        let at = (
            body.driver.obj.body.position(),
            body.driver.obj.body.orientation,
        );
        let live = live_position(&body, self.residency.origin_of(cell_id).unwrap_or(origin));
        let key = body.key;
        self.remotes.insert(key.local_id, body);
        self.resting.remove(&key.local_id);
        world.set_live_position(guid, live);
        if let Err(error) = self.collision.follow(
            &mut self.engine,
            &self.residency,
            key.local_id,
            at.0,
            at.1,
            cell_id,
        ) {
            self.refuse(&format!("a body's collision could not follow it: {error}"));
        }
        true
    }

    fn remote_update(
        &self,
        world: &World,
        content: &WorldContent,
        data: &ac_proto::MovementData,
        cell_id: u32,
    ) -> ac_physics::movement::RemoteMotionUpdate {
        let residency = &self.residency;
        remote_motion_update(
            data,
            |guid| {
                self.body_known(guid)
                    .then(|| body_shape_of(world, content, guid))
                    .flatten()
            },
            |guid| sticky_body_of(world, content, guid),
            |cell| residency.origin_of(cell),
            cell_id,
        )
    }

    fn body_known(&self, guid: u32) -> bool {
        self.player.as_ref().is_some_and(|p| p.guid == guid)
            || self.remotes.values().any(|r| r.guid == guid)
    }

    fn on_motion(
        &mut self,
        world: &mut World,
        key: ObjectKey,
        message: &Inbound<'_>,
        now: Duration,
    ) {
        let Some(guid) = Self::guid_of(world, key) else {
            return;
        };
        let Some(content) = self.content.clone() else {
            return;
        };
        if self.entry_refused(key.local_id) {
            // Not in the world: its updates do not move it.
            return;
        }
        if self.player.as_ref().is_some_and(|p| p.guid == guid) {
            // The character's own movement comes back marked autonomous and
            // is not replayed; a server-driven move or turn is carried out.
            let Inbound::UpdateMotion(update) = message else {
                return;
            };
            if update.is_autonomous {
                return;
            }
            let data = update.movement.clone();
            if self.position_log.is_on() {
                self.log_server_order(world, &content, &data, now);
            }
            let cell = self.player.as_ref().map_or(0, |p| p.controller.cell_id());
            let residency = &self.residency;
            // The thing named is followed through its simulated body, else
            // through the minimal stand-in body a walk to it gets; only a
            // thing with neither is walked to by the order's place.
            let resolved = server_move::resolve(
                &data,
                |target| {
                    if self.remotes.values().any(|r| r.guid == target) {
                        return body_shape_of(world, &content, target);
                    }
                    self.stand_in_place(world, target)
                        .map(|_| body_shape_of(world, &content, target).unwrap_or((0.0, 0.0)))
                },
                |c| residency.origin_of(c),
                cell,
            );
            let Some((request, run_rate)) = resolved else {
                self.apply_own_interpreted_motion(world, &data);
                return;
            };
            if matches!(
                request.kind,
                ac_motion::MovementType::MoveToObject | ac_motion::MovementType::TurnToObject
            ) {
                self.ensure_table_host(world, request.object_id);
            }
            let view = self.bodies_view();
            let Some(player) = self.player.as_mut() else {
                return;
            };
            let mut env = MovementEnv::new(&mut self.engine, &view);
            let c = &mut player.controller;
            let style = if data.stance() != 0 {
                0x8000_0000 | u32::from(data.stance())
            } else {
                ac_motion::motion_command::NON_COMBAT
            };
            {
                let obj = c.object_mut();
                let mut cx = obj.cx(&view);
                cx.make_move_to_manager();
                if let Some(rate) = run_rate {
                    cx.obj.interp.my_run_rate = rate;
                }
            }
            c.set_last_move_was_autonomous(false);
            {
                let obj = c.object_mut();
                if obj.interp.interpreted_state.current_style != style {
                    let mut cx = obj.cx(&view);
                    ac_physics::movement::MotionInterp::do_motion(
                        &mut cx,
                        style,
                        &ac_motion::MovementParameters::default(),
                    );
                }
            }
            c.perform_movement(&mut env, &request);
            return;
        }
        let Some(data) = world.object(guid).and_then(|o| o.movement().cloned()) else {
            return;
        };
        if !self.remotes.contains_key(&key.local_id) && !self.arm_remote(world, guid) {
            return;
        }
        let cell = world
            .object(guid)
            .and_then(|o| o.accepted_position())
            .map_or(0, |p| p.cell_id);
        let update = self.remote_update(world, &content, &data, cell);
        let view = self.bodies_view();
        if let Some(body) = self.remotes.get_mut(&key.local_id) {
            let shown = world.object(guid).and_then(WorldObject::motion_view);
            let applied = body.apply_motion(&view, now.as_secs_f64(), &update);
            if applied.applied_interpreted_state {
                if let Some(between) = interrupted_view(shown, &applied) {
                    world.note_motion_between(guid, between);
                }
                world.set_live_motion(guid, live_motion(&body.driver.obj, body.motions_applied));
                world.note_motion_applied(guid);
            }
        }
        self.deliver_target_messages();
    }

    /// A motion state the server sets on the character's own body that is
    /// not the echo of its own movement (the stance it puts the body in
    /// after a combat-mode change, a motion it plays on it) is unpacked
    /// into the body as another object's would be; a forward command the
    /// catalog does not know takes the class of the body's current one.
    fn apply_own_interpreted_motion(&mut self, world: &mut World, data: &ac_proto::MovementData) {
        if data.movement_type != 0 {
            return;
        }
        let Some(content) = self.content.clone() else {
            return;
        };
        let cell = self.player.as_ref().map_or(0, |p| p.controller.cell_id());
        let mut update = remote_motion_update(
            data,
            |guid| body_shape_of(world, &content, guid),
            |guid| sticky_body_of(world, &content, guid),
            |c| self.residency.origin_of(c),
            cell,
        );
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let c = &mut player.controller;
        c.set_last_move_was_autonomous(false);
        let class = c.object().interp.interpreted_state.forward_command & 0xFF00_0000;
        update.fallback_forward_class = if class == 0 { 0x4100_0000 } else { class };
        let shown = world.object(player.guid).and_then(WorldObject::motion_view);
        let applied =
            ac_physics::movement::apply_inbound_motion(&mut c.object_mut().cx(&view), &update);
        if applied.applied_interpreted_state {
            // Published at once and recorded in order: a local change
            // later in the frame (a turn the keys or a plugin make) must
            // not hide the swing from a window. The turn a move-to was
            // making, stopped before the swing, goes first.
            if let Some(between) = interrupted_view(shown, &applied) {
                world.note_motion_between(player.guid, between);
            }
            player.motions_applied = player.motions_applied.wrapping_add(1);
            world.set_live_motion(
                player.guid,
                live_motion(player.controller.object(), player.motions_applied),
            );
            world.note_motion_applied(player.guid);
        }
        self.deliver_target_messages();
    }

    fn on_state(&mut self, world: &mut World, key: ObjectKey, now: Duration) {
        let Some(guid) = Self::guid_of(world, key) else {
            return;
        };
        let Some(state) = world.object(guid).map(|o| o.physics().state) else {
            return;
        };
        if self.entry_refused(key.local_id) {
            return;
        }
        let hidden = state & PhysicsState::HIDDEN.bits() != 0;
        self.collision
            .update_state(&mut self.engine, key.local_id, state);
        let view = self.bodies_view();
        if let Some(player) = self.player.as_mut().filter(|p| p.guid == guid) {
            player
                .controller
                .apply_physics_state(PhysicsState::from(state));
            if player.hidden != hidden {
                // Going hidden or coming back re-enters the body's
                // animation into the world: its link animations (the stop
                // a teleport queued, say) are dropped, so they do not walk
                // it on once it is shown.
                let mut cx = player.controller.object_mut().cx(&view);
                InterpreterHost::remove_link_animations(&mut cx);
            }
            player.hidden = hidden;
        } else if let Some(body) = self.remotes.get_mut(&key.local_id) {
            body.driver.obj.body.state = PhysicsState::from(state);
            body.driver.obj.body.calc_acceleration();
            if body.driver.hidden != hidden {
                let mut cx = body.driver.obj.cx(&view);
                InterpreterHost::remove_link_animations(&mut cx);
            }
            body.driver.hidden = hidden;
        } else if let Some(missile) = self.projectiles.get_mut(&key.local_id) {
            missile.apply_state(state, now.as_secs_f64());
        } else if state & PhysicsState::MISSILE.bits() != 0 {
            // An object turned into a missile starts flying.
            self.ordinaries.remove(&key.local_id);
            self.bind_projectile(world, guid, now);
        } else if let Some(body) = self.ordinaries.get_mut(&key.local_id) {
            body.apply_state(state);
        }
    }

    /// The server's velocity and spin for an object: a remote body takes
    /// them (the character's own jump is its own physics).
    fn on_vector(&mut self, world: &mut World, key: ObjectKey, now: Duration) {
        let Some(guid) = Self::guid_of(world, key) else {
            return;
        };
        if world.player_guid() == Some(guid) || self.entry_refused(key.local_id) {
            return;
        }
        let Some(physics) = world.object(guid).map(|o| o.physics().clone()) else {
            return;
        };
        let vector =
            |v: Option<ac_proto::Vector3>| v.map_or(Vec3::ZERO, |v| Vec3::new(v.x, v.y, v.z));
        let view = self.bodies_view();
        if let Some(body) = self.remotes.get_mut(&key.local_id) {
            let applied = body.driver.apply_authoritative_vector(
                &view,
                vector(physics.velocity),
                vector(physics.omega),
                now.as_secs_f64(),
            );
            if !applied {
                self.refuse(&format!(
                    "a non-finite velocity for 0x{guid:08X} was not applied"
                ));
            }
        } else if let Some(missile) = self.projectiles.get_mut(&key.local_id) {
            if !missile.apply_vector(
                vector(physics.velocity),
                vector(physics.omega),
                now.as_secs_f64(),
            ) {
                self.refuse(&format!(
                    "a non-finite velocity for 0x{guid:08X} was not applied"
                ));
            }
        } else if let Some(body) = self.ordinaries.get_mut(&key.local_id) {
            if !body.apply_vector(vector(physics.velocity), vector(physics.omega)) {
                self.refuse(&format!(
                    "a non-finite velocity for 0x{guid:08X} was not applied"
                ));
            }
        } else {
            // An object at rest the server sets moving gets a body.
            self.bind_ordinary(world, guid, now, true);
        }
        self.deliver_target_messages();
    }

    /// An item record changed: a registered object's collision takes its
    /// public bitfield (a player-killer status update changes who passes
    /// through whom).
    fn on_item_updated(&mut self, world: &World, guid: u32) {
        let Some(bits) = world
            .item(guid)
            .and_then(|i| i.public_weenie_bitfield)
            .map(|b| b.bits())
        else {
            return;
        };
        let Some(local_id) = world.object(guid).map(|o| o.key().local_id) else {
            return;
        };
        self.collision
            .update_pwd_bitfield(&mut self.engine, local_id, bits);
    }

    #[allow(clippy::too_many_arguments)]
    fn on_position(
        &mut self,
        world: &mut World,
        key: ObjectKey,
        teleported: bool,
        forced: bool,
        message: &Inbound<'_>,
        outbox: &mut Outbox,
        now: Duration,
    ) {
        let Some(guid) = Self::guid_of(world, key) else {
            return;
        };
        if self.entry_refused(key.local_id) {
            // Not in the world: its positions do not move it.
            return;
        }
        let Some(object) = world.object(guid) else {
            return;
        };
        let Some(accepted) = object.accepted_position().copied() else {
            return;
        };
        let stamps = object.timestamps();
        let (velocity, grounded) = match message {
            Inbound::UpdatePosition(u) if u.guid == guid => (
                u.velocity.map(|v| Vec3::new(v.x, v.y, v.z)),
                u.is_grounded(),
            ),
            // A same-incarnation create counts as in contact.
            _ => (None, true),
        };
        let authority = PositionAuthority {
            entity: key,
            previous_teleport_sequence: if teleported {
                stamps.teleport.wrapping_sub(1)
            } else {
                stamps.teleport
            },
            accepted_teleport_sequence: stamps.teleport,
            timestamp_disposition: if forced {
                PositionDisposition::ForcePosition
            } else {
                PositionDisposition::Apply
            },
        };
        let source = if matches!(message, Inbound::UpdatePosition(_)) {
            AcceptedPositionSource::PositionEvent
        } else {
            AcceptedPositionSource::SameIncarnationCreate
        };
        let is_player = self.player.as_ref().is_some_and(|p| p.guid == guid);
        if world.player_guid() == Some(guid) && !is_player {
            // Positions before the body exists: the character enters with
            // its first placement.
            return;
        }
        let player_world = self.player.as_ref().map(|p| p.controller.position());
        let remote_world = self
            .residency
            .origin_of(accepted.cell_id)
            .map(|o| o + local_origin(&accepted));
        let distance = match (player_world, remote_world) {
            (Some(a), Some(b)) => a.distance(b),
            _ => f32::INFINITY,
        };
        let flying = self.projectiles.get(&key.local_id).map(|p| p.cell_id);
        let committed = if is_player {
            self.player.as_ref().map(|p| p.controller.cell_id())
        } else if flying.is_some() {
            flying
        } else {
            self.remotes
                .get(&key.local_id)
                .map(|r| r.driver.obj.cell_id)
                .or_else(|| self.ordinaries.get(&key.local_id).map(|o| o.cell_id))
        };
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            authority,
            entity_kind: if is_player {
                PositionEntityKind::LocalPlayer
            } else if flying.is_some() && object.physics().state & PhysicsState::MISSILE.bits() != 0
            {
                PositionEntityKind::Projectile
            } else {
                PositionEntityKind::Remote
            },
            source,
            accepted_position: accepted,
            placement_frame: object.physics().animation_frame,
            committed_cell_id: committed,
            has_contact: grounded,
            player_distance: if is_player { 0.0 } else { distance },
            use_position_from_server: false,
            has_animations: object.has_animations(),
            physics_state: object.physics().state,
        });
        // The position log follows server positions for objects near the
        // character, other than the character and projectiles (its body's
        // place before the update, when it has one).
        let logged = match message {
            Inbound::UpdatePosition(update)
                if update.guid == guid
                    && self.position_log.is_on()
                    && !is_player
                    && flying.is_none()
                    && distance <= position_log::NEAR_METRES =>
            {
                Some((update, self.body_snapshot(key.local_id)))
            }
            _ => None,
        };
        if !route.accepted() {
            if let Some((update, before)) = logged {
                let cell = before.map_or(0, |b| b.0);
                let handling = position_log::classify(
                    ac_physics::movement::RemotePositionOutcome::Nothing,
                    route.disposition,
                    true,
                    false,
                    (cell, cell),
                );
                self.log_position_update(world, key.local_id, update, handling, before, now);
            }
            return;
        }
        if is_player {
            if route.performs_set_position() {
                self.place_player(world, guid, &accepted, &route, outbox);
            }
            return;
        }
        if flying.is_some() {
            // A missile is placed only by a placement; an ordinary
            // position leaves its flight alone.
            if !matches!(
                route.disposition,
                route::PositionDispositionRoute::SetPosition
                    | route::PositionDispositionRoute::SetPositionSimple
            ) {
                return;
            }
            let origin = self
                .residency
                .is_resident(accepted.cell_id)
                .then(|| self.residency.origin_of(accepted.cell_id))
                .flatten();
            let Some(origin) = origin else {
                self.projectiles.remove(&key.local_id);
                world.clear_live_position(guid);
                return;
            };
            if let Some(missile) = self.projectiles.get_mut(&key.local_id) {
                missile.place(&accepted, origin, now.as_secs_f64());
                world.set_live_position(guid, missile.live_position(origin));
            }
            return;
        }
        if let Some(ordinary) = self.ordinaries.remove(&key.local_id) {
            // An object moving by its own physics that the server sends a
            // position for: its body becomes the body the server's
            // positions drive, as it is, and the position routes for it
            // as for every other body (catch up to a near one on the
            // ground, re-place it at a far one or a teleport, where the
            // placement says what it touches; ignore one sent while it was
            // off the ground).
            if let Some(content) = self.content.clone() {
                let mut body =
                    RemoteBody::from_ordinary(object, &content, ordinary, now.as_secs_f64());
                body.driver.placement_class =
                    placement_class(world.item(guid), content.storage_weenie_class());
                self.remotes.insert(key.local_id, body);
            }
        }
        if !self.remotes.contains_key(&key.local_id) {
            if let Some((update, _)) = logged {
                let handling =
                    position_log::Handling::NoBody(if self.resting.contains_key(&key.local_id) {
                        "resting where the server put it"
                    } else {
                        "entering the world"
                    });
                self.log_position_update(world, key.local_id, update, handling, None, now);
            }
            if self.resting.contains_key(&key.local_id) {
                // An object the runtime does not move stands where the
                // server put it.
                self.unrest(world, key.local_id);
                self.register_collision(world, guid);
            } else {
                // One that has not entered the world enters it now if it
                // stands in it.
                self.register_collision(world, guid);
                self.enter_object(world, guid, now);
            }
            return;
        }
        if !self.residency.is_resident(accepted.cell_id) {
            // Out of the resident world: the body is dropped and the
            // server's position stands.
            self.remotes.remove(&key.local_id);
            world.clear_live_position(guid);
            if let Some((update, before)) = logged {
                let handling = position_log::Handling::NoBody("left the resident landblocks");
                self.log_position_update(world, key.local_id, update, handling, before, now);
            }
            return;
        }
        let Some(origin) = self.residency.origin_of(accepted.cell_id) else {
            return;
        };
        let Some(body) = self.remotes.get_mut(&key.local_id) else {
            return;
        };
        let first_update = body.driver.last_server_pos_time <= 0.0;
        let advanced = distance < route::MAX_PHYSICS_DISTANCE;
        let outcome = body.apply_position(
            &accepted,
            origin,
            route.disposition,
            route.teleport_hook_phase,
            velocity,
            grounded,
            advanced,
            now.as_secs_f64(),
        );
        if outcome == ac_physics::movement::RemotePositionOutcome::PlacementRequired {
            self.place_remote(world, guid, key, &accepted, route.set_position_flags.0, now);
        }
        if let Some((update, before)) = logged {
            let cell_before = before.map_or(0, |b| b.0);
            let cell_after = self.body_snapshot(key.local_id).map_or(0, |b| b.0);
            let handling = position_log::classify(
                outcome,
                route.disposition,
                advanced,
                first_update,
                (cell_before, cell_after),
            );
            self.log_position_update(world, key.local_id, update, handling, before, now);
        }
    }

    // ── the position log ───────────────────────────────────────────────

    /// A simulated body's cell and place in the engine's frame.
    fn body_snapshot(&self, local_id: u32) -> Option<(u32, Vec3)> {
        self.remotes
            .get(&local_id)
            .map(|b| (b.driver.obj.cell_id, b.driver.obj.body.position()))
    }

    /// A place in the engine's frame as a cell and its landblock offset.
    fn log_place(&self, cell: u32, at: Vec3) -> Option<position_log::Place> {
        let origin = self.residency.origin_of(cell)?;
        Some(position_log::Place {
            cell,
            local: (at - origin).to_array(),
        })
    }

    /// Where a wire position is in the engine's frame.
    fn wire_world(&self, p: &Position) -> Option<Vec3> {
        self.residency
            .origin_of(p.cell_id)
            .map(|o| o + local_origin(p))
    }

    /// Log one server position for an object: `before` is its body's cell
    /// and place before the update; its place after is read now.
    fn log_position_update(
        &mut self,
        world: &World,
        local_id: u32,
        update: &ac_proto::UpdatePosition,
        handling: position_log::Handling,
        before: Option<(u32, Vec3)>,
        now: Duration,
    ) {
        let server = self.wire_world(&update.position);
        let after = self.body_snapshot(local_id);
        let gap = |body: Option<(u32, Vec3)>| Some(body?.1.distance(server?));
        let player = self.player.as_ref().map(|p| p.controller.position());
        let line = position_log::UpdateLine {
            guid: update.guid,
            name: log_name(world, update.guid),
            sequences: [
                update.instance_sequence,
                update.position_sequence,
                update.teleport_sequence,
                update.force_position_sequence,
            ],
            grounded: update.is_grounded(),
            handling,
            server: position_log::Place::of(&update.position),
            body_before: before.and_then(|(cell, at)| self.log_place(cell, at)),
            body_after: after.and_then(|(cell, at)| self.log_place(cell, at)),
            gap_before: gap(before),
            gap_after: gap(after),
            from_player: match (player, server) {
                (Some(a), Some(b)) => a.distance(b),
                _ => f32::INFINITY,
            },
        };
        self.position_log.update(now, &line);
    }

    /// Log a server position the world did not apply (its counters were
    /// not newer, or it named no valid place), for an object near the
    /// character.
    fn log_unapplied_position(
        &mut self,
        world: &World,
        update: &ac_proto::UpdatePosition,
        now: Duration,
    ) {
        if world.player_guid() == Some(update.guid) {
            return;
        }
        let near = match (
            self.player.as_ref().map(|p| p.controller.position()),
            self.wire_world(&update.position),
        ) {
            (Some(a), Some(b)) => a.distance(b) <= position_log::NEAR_METRES,
            _ => false,
        };
        if !near {
            return;
        }
        let Some(object) = world.object(update.guid) else {
            return;
        };
        let handling =
            position_log::Handling::Rejected(if route::valid_position(&update.position) {
                "sequence"
            } else {
                "bad position"
            });
        let before = self.body_snapshot(object.key().local_id);
        self.log_position_update(world, object.key().local_id, update, handling, before, now);
    }

    /// Log a movement the server ordered for the character: what it steers
    /// to, chosen as [`server_move::resolve`] chooses it.
    fn log_server_order(
        &mut self,
        world: &World,
        content: &WorldContent,
        data: &ac_proto::MovementData,
        now: Duration,
    ) {
        use ac_proto::Movement as Order;
        use position_log::{Place, Steer};
        let path_place = |path: &ac_proto::MoveToPath| Place {
            cell: path.origin_cell_id,
            local: [path.origin.x, path.origin.y, path.origin.z],
        };
        let path_fields = |path: &ac_proto::MoveToPath| {
            vec![
                ("distance", path.distance_to_object),
                ("min", path.min_distance),
                ("fail", path.fail_distance),
                ("speed", path.speed),
                ("walk_run", path.walk_run_threshold),
                ("run_rate", path.run_rate),
            ]
        };
        let turn_fields = |params: &ac_proto::TurnToParams| {
            vec![("speed", params.speed), ("heading", params.desired_heading)]
        };
        let (kind, target, steer, fields) = match &data.movement {
            Order::MoveToObject { target, path } => (
                "MoveToObject",
                *target,
                self.order_steer(world, content, *target)
                    .unwrap_or_else(|why| Steer::OrderPlace {
                        place: path_place(path),
                        why,
                    }),
                path_fields(path),
            ),
            Order::MoveToPosition { path } => (
                "MoveToPosition",
                0,
                Steer::OrderPlace {
                    place: path_place(path),
                    why: "a place order",
                },
                path_fields(path),
            ),
            Order::TurnToObject {
                target,
                wire_heading,
                params,
            } => (
                "TurnToObject",
                *target,
                self.order_steer(world, content, *target)
                    .unwrap_or_else(|why| Steer::OrderHeading {
                        heading: *wire_heading,
                        why,
                    }),
                turn_fields(params),
            ),
            Order::TurnToHeading { params } => (
                "TurnToHeading",
                0,
                Steer::Heading(params.desired_heading),
                turn_fields(params),
            ),
            _ => return,
        };
        let line = position_log::OrderLine {
            kind,
            target,
            name: if target == 0 {
                String::new()
            } else {
                log_name(world, target)
            },
            steer,
            fields,
        };
        self.position_log.order(now, &line);
    }

    /// What an order naming `target` steers the character to, chosen as
    /// [`server_move::resolve`] chooses it: its simulated body, else a
    /// stand-in where it stands; `Err` says why the order's own place or
    /// heading is used instead.
    fn order_steer(
        &self,
        world: &World,
        content: &WorldContent,
        target: u32,
    ) -> Result<position_log::Steer, &'static str> {
        if let Some(body) = self.remotes.values().find(|r| r.guid == target) {
            if body_shape_of(world, content, target).is_none() {
                return Err("its body has no shape");
            }
            let at = body.driver.obj.body.position();
            let gap = world
                .object(target)
                .and_then(WorldObject::accepted_position)
                .and_then(|p| self.wire_world(p))
                .map(|server| server.distance(at));
            return Ok(position_log::Steer::Body {
                place: self.log_place(body.driver.obj.cell_id, at),
                gap,
            });
        }
        if let Some(p) = self.stand_in_place(world, target) {
            return Ok(position_log::Steer::StandIn(
                self.log_place(p.obj_cell_id, p.frame.origin),
            ));
        }
        let Some(object) = world.object(target) else {
            return Err("no object here");
        };
        Err(
            if object.physics().state & PhysicsState::HIDDEN.bits() != 0 {
                "hidden"
            } else if object.parent().is_some() {
                "held"
            } else if object.position().is_none() {
                "contained"
            } else if world.player_guid() == Some(target) {
                "the character itself"
            } else {
                "outside the loaded landblocks"
            },
        )
    }

    /// Log each object near the character whose body stands more than
    /// [`position_log::APART_METRES`] from the server's last place for it,
    /// at most every [`position_log::APART_EVERY`] each.
    fn log_apart_bodies(&mut self, world: &World, now: Duration) {
        let Some(player) = self.player.as_ref().map(|p| p.controller.position()) else {
            return;
        };
        let mut lines = Vec::new();
        for body in self.remotes.values() {
            let at = body.driver.obj.body.position();
            let from_player = at.distance(player);
            if from_player > position_log::NEAR_METRES
                || !self.position_log.apart_due(body.guid, now)
            {
                continue;
            }
            let Some(accepted) = world.object(body.guid).and_then(|o| o.accepted_position()) else {
                continue;
            };
            let Some(server_at) = self.wire_world(accepted) else {
                continue;
            };
            let gap = at.distance(server_at);
            if gap <= position_log::APART_METRES {
                continue;
            }
            let Some(place) = self.log_place(body.driver.obj.cell_id, at) else {
                continue;
            };
            lines.push(position_log::ApartLine {
                guid: body.guid,
                name: log_name(world, body.guid),
                gap,
                server: position_log::Place::of(accepted),
                body: place,
                since_update: None,
                since_moved: None,
                from_player,
            });
        }
        for line in lines {
            self.position_log.apart(now, line);
        }
    }

    /// Put a remote body down at the server's position with a placement
    /// that may slide aside (a teleport, or a far snap of a body in
    /// contact, with the route's flags: teleport | slide | send position
    /// event), as OpenAC's placement drive does
    /// (`RuntimeRemotePlacementDriveController.ApplyAcceptedRemoteTeleport`
    /// / `ApplyAcceptedRemoteFarSnap`). A placement that finds no spot
    /// leaves the body where it was. The leash goes on after, the server's
    /// position is stamped as the last one it sent, and the body's rows
    /// follow it at once.
    fn place_remote(
        &mut self,
        world: &mut World,
        guid: u32,
        key: ObjectKey,
        accepted: &Position,
        flags: u32,
        now: Duration,
    ) {
        let placed = self
            .placement_request(
                world,
                guid,
                key.local_id,
                accepted,
                remote::wire_orientation(accepted),
                flags,
                0,
            )
            .map(|(request, ..)| self.engine.set_position(&request, None));
        let view = self.bodies_view();
        let Some(body) = self.remotes.get_mut(&key.local_id) else {
            return;
        };
        if let Some(placed) = placed {
            body.driver
                .commit_placement(&view, &placed, now.as_secs_f64());
        }
        body.driver.arm_leash();
        // The server's own position is the last one it sent, wherever the
        // placement put the body (OpenAC stamps it after routing).
        if let Some(origin) = self.residency.origin_of(accepted.cell_id) {
            body.driver
                .stamp_server_position(origin + local_origin(accepted), now.as_secs_f64());
        }
        let cell_id = body.driver.obj.cell_id;
        let at = (
            body.driver.obj.body.position(),
            body.driver.obj.body.orientation,
        );
        if let Some(origin) = self.residency.origin_of(cell_id) {
            world.set_live_position(guid, live_position(body, origin));
        }
        if let Err(error) = self.collision.follow(
            &mut self.engine,
            &self.residency,
            key.local_id,
            at.0,
            at.1,
            cell_id,
        ) {
            self.refuse(&format!("a body's collision could not follow it: {error}"));
        }
    }

    // ── the local player ───────────────────────────────────────────────

    /// The placement of `guid`'s body at `position` with set-position
    /// `flags`: its setup's spheres at its scale, its step heights, its
    /// physics state and the mover bits of its public bitfield
    /// (`default_pwd` when it has none). With the step heights, spheres and
    /// scale it was built from.
    #[allow(clippy::too_many_arguments)]
    fn placement_request(
        &self,
        world: &World,
        guid: u32,
        local_id: u32,
        position: &Position,
        orientation: Quat,
        flags: u32,
        default_pwd: u32,
    ) -> Option<(
        PhysicsSetPositionRequest,
        f32,
        f32,
        Vec<CollisionSphere>,
        f32,
    )> {
        let content = self.content.as_ref()?;
        let object = world.object(guid)?;
        let physics = object.physics();
        let origin = self.residency.origin_of(position.cell_id)?;
        let local = local_origin(position);
        let scale = physics.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let setup = physics.setup_id.and_then(|id| content.setup(id));
        let spheres: Vec<CollisionSphere> = setup
            .as_ref()
            .map(|s| {
                s.spheres
                    .iter()
                    .map(|sp| {
                        CollisionSphere::new(
                            Vec3::new(sp.origin.x, sp.origin.y, sp.origin.z),
                            sp.radius,
                        )
                    })
                    .collect()
            })
            .unwrap_or_default();
        let step_up = setup.as_ref().map_or(0.0, |s| s.step_up_height * scale);
        let step_down = setup.as_ref().map_or(0.0, |s| s.step_down_height * scale);
        let pwd = world
            .item(guid)
            .and_then(|i| i.public_weenie_bitfield)
            .map_or(default_pwd, |b| b.bits());
        let mut mover = EntityCollisionFlags::from_pwd_bitfield(pwd)
            .to_mover_state()
            .0;
        if EntityCollisionFlags::from_pwd_bitfield(pwd).contains(EntityCollisionFlags::IS_PLAYER) {
            mover |= ObjectInfoState::IS_PLAYER;
        }
        Some((
            PhysicsSetPositionRequest {
                position: local + origin,
                orientation,
                cell_id: position.cell_id,
                cell_local_position: local,
                spheres: spheres.clone(),
                scale,
                step_up_height: step_up,
                step_down_height: step_down,
                mover_physics_state: PhysicsState::from(physics.state),
                mover_flags: ObjectInfoState(mover),
                moving_entity_id: local_id,
                flags: PhysicsSetPositionFlags(flags),
                placement_class: placement_class(world.item(guid), content.storage_weenie_class()),
                ..PhysicsSetPositionRequest::default()
            },
            step_up,
            step_down,
            spheres,
            scale,
        ))
    }

    fn enter_world(&mut self, world: &mut World, guid: u32, key: ObjectKey, outbox: &mut Outbox) {
        let Some(content) = self.content.clone() else {
            return;
        };
        let Some(object) = world.object(guid) else {
            return;
        };
        let Some(accepted) = object.accepted_position().copied() else {
            return;
        };
        if let Err(error) = self
            .residency
            .center_on(&mut self.engine, &content, accepted.cell_id)
        {
            self.refuse(&format!("collision could not be loaded: {error}"));
            return;
        }
        self.drop_remotes(world);
        self.collision.forget_all();
        self.sync_collision(world);
        self.enter_waiting(world);
        self.pending_entry = Some(PendingEntry {
            guid,
            key,
            accepted,
        });
        self.enter_pending(world);
        let _ = outbox;
    }

    /// The character enters the world at its create's position, put down
    /// by the create's placement (placement | slide, so it does not start
    /// inside another body or a wall). While the placement finds no clear
    /// spot the character waits out of the world, and is placed again each
    /// frame until one is found.
    fn enter_pending(&mut self, world: &mut World) {
        let Some(PendingEntry {
            guid,
            key,
            accepted,
        }) = self.pending_entry
        else {
            return;
        };
        let Some(content) = self.content.clone() else {
            return;
        };
        let Some(object) = world.object(guid).filter(|o| o.key() == key) else {
            self.pending_entry = None;
            return;
        };
        let physics = object.physics().clone();
        let route = entry::create_route(object, true);
        if !route.performs_set_position() {
            return;
        }
        let orientation = remote::wire_orientation(&accepted);
        let Some((request, step_up, step_down, spheres, scale)) = self.placement_request(
            world,
            guid,
            key.local_id,
            &accepted,
            orientation,
            route.set_position_flags.0,
            PWD_PLAYER,
        ) else {
            return;
        };
        let placed = self.engine.set_position(&request, None);
        if !placed.is_committed() {
            return;
        }
        self.pending_entry = None;
        let (pos, cell, local) = (placed.position, placed.cell_id, placed.cell_local_position);

        let (run, jump) = self.movement_skills(world);
        let mut controller =
            PlayerMovementController::with_options(PlayerMovementOptions::from_skills(run, jump));
        {
            let obj = controller.object_mut();
            obj.token = ac_physics::movement::ObjectToken {
                id: guid,
                incarnation: u64::from(key.incarnation),
            };
            obj.can_move_to = true;
            obj.position_attached = true;
            obj.targeting_attached = true;
            if let Some(setup) = physics.setup_id.and_then(|id| content.setup(id)) {
                obj.radius = setup.radius * scale;
                obj.height = setup.height * scale;
            }
        }
        controller.local_entity_id = key.local_id;
        controller.step_up_height = step_up;
        controller.step_down_height = step_down;
        controller.sphere_list = spheres;
        controller.object_scale = scale;
        let pwd = world.item(guid).and_then(|i| i.public_weenie_bitfield);
        controller.own_pvp_flags = resolve_mover_pvp_state(pwd.map(|b| b.bits())).0;
        {
            let view = self.bodies_view();
            let mut env = MovementEnv::new(&mut self.engine, &view);
            controller.seed_placement(&mut env, pos, cell, local);
            controller.set_body_orientation(orientation);
            controller.apply_physics_state(PhysicsState::from(physics.state));
            // The character can be moved to things (a server move-to, a
            // turn to a heading) from the start.
            controller.object_mut().cx(&view).make_move_to_manager();
            // The ground the placement found is the body's contact plane
            // (the set-position commit), so its first step can follow the
            // ground down a slope.
            let orientation = controller.body_orientation();
            commit_placement(controller.body_mut(), &placed, orientation);
            if let Some(friction) = physics.friction {
                controller.body_mut().friction = friction;
            }
            if let Some(elasticity) = physics.elasticity {
                controller.body_mut().elasticity = elasticity;
            }
            if let (Some(setup), Some(table)) = (physics.setup_id, physics.motion_table_id)
                && let Some(sequencer) = content.sequencer(setup, table)
            {
                controller.arm_animation(&mut env, sequencer, scale);
            }
        }
        let hidden = physics.state & PhysicsState::HIDDEN.bits() != 0;
        self.approaches.begin_lifetime();
        self.player = Some(LocalPlayer {
            guid,
            key,
            controller,
            hidden,
            motions_applied: 0,
        });
        self.applied_stats = None;
        self.applied_pk = None;
        self.apply_stats(world);
        self.publish_player(world);
    }

    fn drop_remotes(&mut self, world: &mut World) {
        for (_, rest) in self.resting.drain() {
            world.clear_live_position(rest.guid);
        }
        for (_, body) in self.remotes.drain() {
            world.clear_live_position(body.guid);
        }
        for (_, missile) in self.projectiles.drain() {
            world.clear_live_position(missile.guid);
        }
        for (_, ordinary) in self.ordinaries.drain() {
            world.clear_live_position(ordinary.guid);
        }
    }

    fn place_player(
        &mut self,
        world: &mut World,
        guid: u32,
        accepted: &Position,
        route: &route::PositionRoute,
        outbox: &mut Outbox,
    ) {
        let Some(content) = self.content.clone() else {
            return;
        };
        if self.destination_hold && route.teleport_hook_phase != route::TeleportHookPhase::None {
            // The arrival waits for the destination's collision: the
            // placement waits with it (`prepare_destination`).
            self.pending_place = Some(PendingPlace {
                guid,
                accepted: *accepted,
                route: *route,
            });
            self.wait_at_destination(world, accepted, route);
            return;
        }
        if !self.residency.is_resident(accepted.cell_id)
            || route.teleport_hook_phase != route::TeleportHookPhase::None
        {
            // A place out of the resident world (or a teleport): the frame
            // is centred on it and the square loaded afresh.
            if !self.residency.is_resident(accepted.cell_id) {
                if let Err(error) =
                    self.residency
                        .center_on(&mut self.engine, &content, accepted.cell_id)
                {
                    self.refuse(&format!("collision could not be loaded: {error}"));
                    return;
                }
                self.drop_remotes(world);
                self.collision.forget_all();
                self.sync_collision(world);
                self.enter_waiting(world);
            }
        }
        let Some(local_id) = self.player.as_ref().map(|p| p.key.local_id) else {
            return;
        };
        let orientation = if route.preserve_heading {
            self.player
                .as_ref()
                .map_or(Quat::IDENTITY, |p| p.controller.body_orientation())
        } else {
            remote::wire_orientation(accepted)
        };
        let Some((request, ..)) = self.placement_request(
            world,
            guid,
            local_id,
            accepted,
            orientation,
            route.set_position_flags.0,
            PWD_PLAYER,
        ) else {
            return;
        };
        let placed = self.engine.set_position(&request, None);
        let stamps = Self::timestamps(world, guid);
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let mut env = MovementEnv::new(&mut self.engine, &view);
        let c = &mut player.controller;
        if placed.is_committed() {
            commit_placement(c.body_mut(), &placed, orientation);
        } else {
            // No free spot was found (or the cell is not there): the body
            // takes the server's frame as sent.
            let body = c.body_mut();
            body.snap_to_cell(
                request.cell_id,
                request.position,
                request.cell_local_position,
            );
            body.orientation = orientation;
        }
        if route.preserve_heading {
            // A forced position: the frame follows, motion is kept, and the
            // server hears where the body is at once.
            c.commit_canonical_force_position_frame(&mut env);
            if route.send_position_immediately {
                self.outbound.send_immediate_position(outbox, c, &stamps);
            }
        } else {
            c.commit_canonical_teleport_frame(
                &mut env,
                route.zero_velocity,
                route.constrain_after_routing(),
                route.runs_teleport_hook(),
            );
            // While an attached window holds the arrival the body waits at
            // the destination in portal space (it does not move) until the
            // arrival completes.
            c.state = if self.arrival_held {
                PlayerState::PortalSpace
            } else {
                PlayerState::InWorld
            };
            self.auto_run = false;
            let movement = c.capture_presentation_result();
            self.outbound
                .try_send_movement(outbox, c, &movement, &stamps);
            // The body is out of portal space (or waits for the window); the
            // transit owner tells the server (login complete) when the
            // arrival completes.
            self.in_portal_space = self.arrival_held;
        }
        self.publish_player(world);
    }

    /// The teleport's position has put the character at its destination
    /// while that destination's collision still loads: the body waits
    /// there, in portal space (it does not move), as the server's frame
    /// gives it, until the placement runs once the collision is in.
    /// Everything that reads where the character is meanwhile (the
    /// positions sent to the server among them) reads the destination,
    /// never the spot it left: a server that took a position from that
    /// spot after the teleport would see the character cross the world in
    /// a moment.
    fn wait_at_destination(
        &mut self,
        world: &mut World,
        accepted: &Position,
        route: &route::PositionRoute,
    ) {
        let Some(origin) = self.residency.origin_of(accepted.cell_id) else {
            return;
        };
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let c = &mut player.controller;
        if c.state != PlayerState::PortalSpace {
            return;
        }
        let local = local_origin(accepted);
        let orientation = (!route.preserve_heading).then(|| remote::wire_orientation(accepted));
        c.wait_in_portal_space_at(accepted.cell_id, local + origin, local, orientation);
        self.publish_player(world);
    }

    fn movement_skills(&self, world: &World) -> (i32, i32) {
        world
            .player()
            .map(|p| {
                let n = p.numbers();
                (
                    n.get_effective_skill(SKILL_RUN).unwrap_or(-1),
                    n.get_effective_skill(SKILL_JUMP).unwrap_or(-1),
                )
            })
            .unwrap_or((-1, -1))
    }

    /// The player-killer status (unknown: -1) and the last PK attack time
    /// reach the controller when they change.
    fn apply_pk_status(&mut self, world: &World) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let item = world.item(player.guid);
        let status = item
            .and_then(|i| {
                i.properties
                    .ints
                    .get(&ac_core::properties::PropertyInt::PLAYER_KILLER_STATUS.0)
                    .copied()
            })
            .unwrap_or(-1);
        let last_attack = item.and_then(|i| {
            i.properties
                .floats
                .get(&ac_core::properties::PropertyFloat::LAST_PK_ATTACK_TIMESTAMP.0)
                .map(|&t| t as f32)
        });
        let pk = (status, last_attack);
        if self.applied_pk != Some(pk) {
            self.applied_pk = Some(pk);
            player
                .controller
                .set_character_pk_status(status, last_attack);
        }
    }

    /// Run and jump skills, burden and stamina reach the controller when
    /// they change (OpenAC `RuntimeMovementStatsApplier`).
    fn apply_stats(&mut self, world: &World) {
        self.apply_pk_status(world);
        let Some(player) = self.player.as_ref() else {
            return;
        };
        let (run, jump) = self.movement_skills(world);
        if run < 0 || jump < 0 {
            return;
        }
        let guid = player.guid;
        let numbers = world.player().map(|p| p.numbers());
        let strength = numbers
            .and_then(|n| n.get_effective_attribute(ac_core::player::AttributeKind::Strength))
            .unwrap_or(0);
        let item = world.item(guid);
        let aug = item.map_or(0, |i| {
            i.properties.get_int(
                ac_core::properties::PropertyInt::AUGMENTATION_INCREASED_CARRYING_CAPACITY.0,
                0,
            )
        });
        let capacity = ac_core::items::burden::encumbrance_capacity(strength, aug);
        let burden = item
            .and_then(|i| {
                i.properties
                    .ints
                    .get(&ac_core::properties::PropertyInt::ENCUMBRANCE_VAL.0)
                    .copied()
            })
            .unwrap_or_else(|| world.items().sum_carried_burden(guid));
        let load = ac_core::items::burden::load_ratio(capacity, burden);
        let stamina = numbers
            .and_then(|n| n.get(ac_core::player::VitalKind::Stamina))
            .map_or(-1, |v| i32::try_from(v.current).unwrap_or(i32::MAX));
        let stats = (run, jump, load, stamina);
        if self.applied_stats == Some(stats) {
            return;
        }
        let exhausted_edge = self
            .applied_stats
            .is_some_and(|(_, _, _, s)| (s == 0) != (stamina == 0));
        self.applied_stats = Some(stats);
        let view = self.bodies_view();
        if let Some(player) = self.player.as_mut() {
            let c = &mut player.controller;
            c.set_character_skills(run, jump);
            c.set_character_burden(load);
            c.set_character_stamina(stamina);
            if exhausted_edge {
                let mut env = MovementEnv::new(&mut self.engine, &view);
                c.report_exhaustion(&mut env);
            }
        }
    }

    fn publish_player(&self, world: &mut World) {
        let Some(player) = self.player.as_ref() else {
            return;
        };
        if let Some(p) = player.controller.try_get_outbound_position() {
            let wire = outbound::wire_position(&p);
            world.set_live_position(player.guid, wire);
            // A window draws the body (and aims its camera) between its
            // last two steps at its own frame's time, in the body's cell
            // with the body's facing; the cell frame and the world differ
            // by a constant offset, so each step's place is the body's cell
            // origin moved by the step's offset from the body. The last
            // step ended the carried time before this pass, on the session
            // clock the window reads too.
            let c = &player.controller;
            let steps = c.render_steps();
            let body = c.position();
            let in_cell = |at: Vec3| {
                let o = at - body;
                ac_proto::Vector3 {
                    x: wire.origin.x + o.x,
                    y: wire.origin.y + o.y,
                    z: wire.origin.z + o.z,
                }
            };
            let pending = Duration::try_from_secs_f64(steps.pending_seconds.max(0.0))
                .unwrap_or(Duration::ZERO);
            let blend = StepBlend {
                previous: in_cell(steps.previous),
                current: in_cell(steps.current),
                step_at: self
                    .last_frame
                    .unwrap_or(Duration::ZERO)
                    .saturating_sub(pending),
                step_seconds: steps.step_seconds,
            };
            world.set_step_blend(player.guid, blend);
        }
        world.set_live_motion(
            player.guid,
            live_motion(player.controller.object(), player.motions_applied),
        );
    }

    fn bodies_view(&self) -> BodiesView {
        let mut view = BodiesView::default();
        if let Some(player) = self.player.as_ref() {
            let obj = player.controller.object();
            view.insert(obj.token, obj.position());
        }
        for body in self.remotes.values() {
            view.insert(body.driver.obj.token, body.driver.position());
        }
        self.add_table_hosts(&mut view);
        view
    }

    fn deliver_target_messages(&mut self) {
        for _ in 0..8 {
            let mut messages: Vec<TargetMessage> = Vec::new();
            if let Some(player) = self.player.as_mut() {
                messages.extend(player.controller.object_mut().take_target_messages());
            }
            for body in self.remotes.values_mut() {
                messages.extend(body.driver.obj.take_target_messages());
            }
            messages.extend(self.take_table_host_messages());
            if messages.is_empty() {
                return;
            }
            let view = self.bodies_view();
            for message in messages {
                let to = message.addressee();
                if let Some(player) = self
                    .player
                    .as_mut()
                    .filter(|p| p.controller.object().token == to)
                {
                    player
                        .controller
                        .object_mut()
                        .cx(&view)
                        .deliver_target_message(message);
                } else if let Some(body) =
                    self.remotes.values_mut().find(|b| b.driver.obj.token == to)
                {
                    body.driver.cx(&view).deliver_target_message(message);
                } else {
                    self.deliver_to_table_host(message);
                }
            }
        }
    }

    // ── the frame ──────────────────────────────────────────────────────

    /// The held keys (and autorun) as the controller's input.
    fn key_input(&self) -> MovementInput {
        // A plugin's held keys replace the keyboard (an overlay that has
        // the keyboard must not freeze the character it drives).
        if let Some(intent) = self.intent {
            return MovementInput {
                is_persistent_command: true,
                ..intent
            };
        }
        MovementInput {
            forward: self.keys.forward || self.auto_run,
            backward: self.keys.backward,
            strafe_left: self.keys.strafe_left,
            strafe_right: self.keys.strafe_right,
            turn_left: self.keys.turn_left,
            turn_right: self.keys.turn_right,
            // The default pace (run, unless the ToggleRun option is off);
            // the walk key held takes the other one; autorun runs.
            run: self.run_as_default != self.keys.walk || self.auto_run,
            jump: self.keys.jump,
            ..MovementInput::default()
        }
    }

    fn capture_input(&mut self) -> MovementInput {
        let frame = self.key_input();
        let before = self.input.snapshot();
        let controller = self.player.as_mut().map(|p| &mut p.controller);
        let scripted = self.input.capture(&frame, controller);
        self.report_moves(&before);
        scripted.unwrap_or(frame)
    }

    fn report_moves(&mut self, before: &ScriptedMoveSnapshot) {
        let after = self.input.snapshot();
        for channel in [MoveChannel::Travel, MoveChannel::Strafe, MoveChannel::Turn] {
            let (b, a) = (before.channel(channel), after.channel(channel));
            if b.state == ScriptedMoveState::Moving && a.state != ScriptedMoveState::Moving {
                self.events.push(MovementEvent::MoveEnded {
                    channel,
                    state: a.state,
                    covered: a.covered,
                    elapsed_seconds: a.elapsed_seconds,
                });
            }
        }
    }

    /// Before the network pass: input, the body's quanta, the movement
    /// event and jump, other bodies, and the positions reaching the world.
    pub fn advance_before_network(
        &mut self,
        world: &mut World,
        outbox: &mut Outbox,
        now: Duration,
    ) {
        let dt = self
            .last_frame
            .map_or(0.0, |last| now.saturating_sub(last).as_secs_f32());
        self.last_frame = Some(now);
        if self.content.is_none() || dt <= 0.0 {
            return;
        }
        if self.player.is_none() {
            // The character waiting for a clear spot to enter at.
            self.enter_pending(world);
        }
        self.apply_stats(world);
        if self.player.is_some() {
            self.follow_sought_object(world);
            if std::mem::take(&mut self.end_modern_mouse_look)
                && self
                    .player
                    .as_ref()
                    .is_some_and(|p| p.controller.modern_mouse_look_active())
            {
                let input = self.key_input();
                let ended = self.with_controller(world, |c, env| c.end_mouse_look(env, input));
                if ended == Some(true) {
                    self.send_movement_now(world, outbox);
                }
            }
            let input = self.capture_input();
            let view = self.bodies_view();
            let guid = self.player.as_ref().map_or(0, |p| p.guid);
            let stamps = Self::timestamps(world, guid);
            if let Some(player) = self.player.as_mut() {
                let mut env = MovementEnv::new(&mut self.engine, &view);
                let c = &mut player.controller;
                let movement = if player.hidden {
                    c.tick_hidden(&mut env, dt)
                } else {
                    c.update(&mut env, dt, input)
                };
                self.outbound.send_pre_network_actions(
                    outbox,
                    c,
                    &movement,
                    &stamps,
                    player.hidden,
                );
                for refusal in c.take_interface_refusals() {
                    if let Some(text) = jump_refusal_text(refusal) {
                        self.interface_text.push(text.to_owned());
                    }
                }
            }
            self.publish_move_to_events();
            self.drive_table_hosts();
            self.deliver_target_messages();
            self.follow_residency(world);
            self.publish_player(world);
            self.follow_player_shadow();
        }
        self.advance_remotes(world, dt, now);
        self.advance_projectiles(world, dt);
        self.advance_ordinaries(world, dt);
    }

    /// Missiles fly, their collision follows them, and the world sees
    /// where they are; one that left the resident world is dropped.
    fn advance_projectiles(&mut self, world: &mut World, dt: f32) {
        if self.projectiles.is_empty() {
            return;
        }
        let player_position = self.player.as_ref().map(|p| p.controller.position());
        let engine = &self.engine;
        for missile in self.projectiles.values_mut() {
            missile.advance(engine, dt, player_position);
        }
        let mut lives = Vec::with_capacity(self.projectiles.len());
        let mut gone = Vec::new();
        let mut follows = Vec::with_capacity(self.projectiles.len());
        for (local_id, missile) in &self.projectiles {
            match self.residency.origin_of(missile.cell_id) {
                Some(origin) if self.residency.is_resident(missile.cell_id) => {
                    lives.push((missile.guid, missile.live_position(origin)));
                    follows.push((
                        *local_id,
                        missile.body.position(),
                        missile.body.orientation,
                        missile.cell_id,
                    ));
                }
                _ => gone.push((*local_id, missile.guid)),
            }
        }
        for (local_id, guid) in gone {
            self.projectiles.remove(&local_id);
            world.clear_live_position(guid);
        }
        for (local_id, at, orientation, cell_id) in follows {
            if let Err(error) = self.collision.follow(
                &mut self.engine,
                &self.residency,
                local_id,
                at,
                orientation,
                cell_id,
            ) {
                self.refuse(&format!(
                    "a missile's collision could not follow it: {error}"
                ));
            }
        }
        for (guid, live) in lives {
            world.set_live_position(guid, live);
        }
    }

    /// The character's own rows follow its body, so other bodies meet it.
    fn follow_player_shadow(&mut self) {
        let Some(player) = self.player.as_ref() else {
            return;
        };
        let c = &player.controller;
        let (local_id, at, orientation, cell_id) = (
            player.key.local_id,
            c.position(),
            c.body_orientation(),
            c.cell_id(),
        );
        if let Err(error) = self.collision.follow_player(
            &mut self.engine,
            &self.residency,
            local_id,
            at,
            orientation,
            cell_id,
        ) {
            self.refuse(&format!(
                "the character's collision could not follow it: {error}"
            ));
        }
    }

    fn follow_residency(&mut self, world: &mut World) {
        let Some(content) = self.content.clone() else {
            return;
        };
        let Some(cell) = self.player.as_ref().map(|p| p.controller.cell_id()) else {
            return;
        };
        match self.residency.follow(&mut self.engine, &content, cell) {
            Ok((loaded, dropped)) => {
                if !loaded.is_empty() || !dropped.is_empty() {
                    let gone: Vec<u32> = self
                        .remotes
                        .iter()
                        .filter(|(_, r)| !self.residency.is_resident(r.driver.obj.cell_id))
                        .map(|(k, _)| *k)
                        .collect();
                    for key in gone {
                        if let Some(body) = self.remotes.remove(&key) {
                            world.clear_live_position(body.guid);
                        }
                    }
                    self.drop_resting_outside(world);
                    self.sync_collision(world);
                    if !loaded.is_empty() {
                        self.enter_waiting(world);
                    }
                    self.events
                        .push(MovementEvent::Residency { loaded, dropped });
                }
            }
            Err(error) => self.refuse(&format!("collision could not be loaded: {error}")),
        }
    }

    fn advance_remotes(&mut self, world: &mut World, dt: f32, now: Duration) {
        if self.remotes.is_empty() {
            return;
        }
        let view = self.bodies_view();
        let player_position = self.player.as_ref().map(|p| p.controller.position());
        let mut lives = Vec::with_capacity(self.remotes.len());
        let mut errors = Vec::new();
        // One body at a time, as the game moves its objects: each body
        // moves, is placed if its catch-up stalled, and its rows follow it
        // before the next body moves, so the next one is swept against
        // where this one now stands. Two bodies swept against where both
        // stood when the frame began could step into the same space, and
        // two that overlapped moved on together, never apart.
        let Some(content) = self.content.clone() else {
            return;
        };
        for body in self.remotes.values_mut() {
            let state_before = body.driver.obj.body.state;
            let seen = BodiesInWorld {
                bodies: &view,
                engine: &self.engine,
                world: &*world,
                content: &content,
            };
            let advance = body.driver.advance(
                Some(&self.engine),
                &seen,
                dt,
                now.as_secs_f64(),
                player_position,
                true,
                true,
            );
            let _ = body.driver.obj.take_move_to_events();
            // Its animation turned it ethereal or solid: its rows follow.
            if body.driver.obj.body.state != state_before {
                self.collision.update_state(
                    &mut self.engine,
                    body.key.local_id,
                    body.driver.obj.body.state.bits(),
                );
            }
            // A stalled catch-up asks to be placed at its target (a
            // teleport that may slide aside); a refused placement keeps the
            // target and is tried again on the next frame.
            if advance.recovery_placement_due {
                recover_remote(&mut self.engine, &self.residency, body);
            }
            if let Some(origin) = self.residency.origin_of(body.driver.obj.cell_id) {
                lives.push((
                    body.guid,
                    live_position(body, origin),
                    live_motion(&body.driver.obj, body.motions_applied),
                ));
            }
            if let Err(error) = self.collision.follow(
                &mut self.engine,
                &self.residency,
                body.key.local_id,
                body.driver.obj.body.position(),
                body.driver.obj.body.orientation,
                body.driver.obj.cell_id,
            ) {
                errors.push(error);
            }
        }
        for error in errors {
            self.refuse(&format!("a body's collision could not follow it: {error}"));
        }
        for (guid, live, motion) in lives {
            world.set_live_position(guid, live);
            world.set_live_motion(guid, motion);
        }
        self.deliver_target_messages();
        if self.position_log.is_on() {
            self.log_apart_bodies(world, now);
        }
    }

    /// After the network pass: the autonomous position when its gate opens.
    pub fn after_network(&mut self, world: &World, outbox: &mut Outbox) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        if self.content.is_none() {
            return;
        }
        let stamps = Self::timestamps(world, player.guid);
        self.outbound.send_post_network_position(
            outbox,
            &mut player.controller,
            &stamps,
            player.hidden,
        );
    }
}

/// Place a body whose catch-up stalled at its catch-up target.
fn recover_remote(engine: &mut PhysicsEngine, residency: &Residency, body: &mut RemoteBody) {
    let Some((target, request)) = body
        .driver
        .recovery_request(|cell| residency.origin_of(cell))
    else {
        return;
    };
    let placed = engine.set_position(&request, None);
    body.driver.complete_recovery(target, &placed);
}

/// A body's world position as the character's own (cell, landblock
/// origin), for tests and probes.
#[must_use]
pub fn cell_position_of(controller: &PlayerMovementController) -> CellPosition {
    controller.current_cell_position()
}

#[cfg(test)]
mod tests;
