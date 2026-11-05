//! One moving object's movement side, assembled: body, weenie, interpreter,
//! move-to, position and target managers, motion table and object clock,
//! wired to each other the way OpenAC wires the local player and a remote
//! body (`RuntimeLocalPlayerPhysicsPublicationState`, `RuntimeRemoteArming`).
//!
//! The managers' operations run on an [`ObjectCx`]: the object plus a view
//! of the other objects (which incarnation an id names, where it is).

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::move_to_math::{get_heading, set_heading};
use ac_motion::{AnimationSequencer, MotionDoneSink};
use glam::Vec3;

use crate::body::{CellPosition, PhysicsBody};
use crate::movement::interp::{
    InterpretedMotionSink, InterpreterHost, MotionInterp, MotionInterpreter,
};
use crate::movement::move_to::{MoveToHost, MoveToManager};
use crate::movement::movement_manager::{MovementManagerHost, MovementOps};
use crate::movement::position::{PositionManager, PositionManagerHost, PositionOps};
use crate::movement::target::{
    ObjectToken, PhysicsObjHost, TargetInfo, TargetManager, TargetMessage, TargetOps,
};
use crate::movement::weenie::WeenieObject;
use crate::movement::weenie_error::WeenieError;
use crate::quantum::RetailObjectQuantumClock;

/// The other objects, as an object sees them.
pub trait TargetWorld {
    /// The live incarnation `id` names, when it can be targeted.
    fn resolve(&self, id: u32) -> Option<ObjectToken>;
    /// Where an exact incarnation is now (cell, world origin, orientation).
    fn position_of(&self, token: ObjectToken) -> Option<CellPosition>;
}

/// A world with no other objects.
#[derive(Debug, Clone, Copy, Default)]
pub struct NoTargets;

impl TargetWorld for NoTargets {
    fn resolve(&self, _id: u32) -> Option<ObjectToken> {
        None
    }
    fn position_of(&self, _token: ObjectToken) -> Option<CellPosition> {
        None
    }
}

/// Where the interpreter's motions are animated.
#[derive(Default)]
pub enum MotionSink {
    /// Nowhere (motions complete without animating).
    #[default]
    None,
    /// The object's motion table ([`MovementObject::sequencer`]).
    Sequencer,
    /// Another sink.
    Custom(Box<dyn InterpretedMotionSink>),
}

impl std::fmt::Debug for MotionSink {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::None => "None",
            Self::Sequencer => "Sequencer",
            Self::Custom(_) => "Custom",
        })
    }
}

/// How a move-to ended, for the owner.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MoveToEvent {
    /// Arrived (or ended with this code).
    Complete(WeenieError),
    /// Cancelled with this code.
    Cancelled(WeenieError),
}

/// The motions a motion table reported finished, collected so the
/// interpreter hears of them after the table is done.
#[derive(Debug, Default)]
struct DoneMotions(Vec<(u32, bool)>);

impl MotionDoneSink for DoneMotions {
    fn motion_done(&mut self, motion: u32, success: bool) {
        self.0.push((motion, success));
    }
}

/// At most this many dispatched motions complete per pass without a motion
/// table.
const MAXIMUM_MOTION_COMPLETIONS_PER_PASS: usize = 64;

/// One object's movement side.
#[derive(Debug)]
pub struct MovementObject<W: WeenieObject> {
    /// This object.
    pub token: ObjectToken,
    /// The body.
    pub body: PhysicsBody,
    /// The cell its movement is measured in (move-to and target positions),
    /// unless it is the body's own cell.
    pub cell_id: u32,
    /// Movement is measured in the body's own cell (the local player).
    pub cell_from_body: bool,
    /// The game object behind it.
    pub weenie: W,
    /// The motion interpreter.
    pub interp: MotionInterpreter,
    /// The move-to manager (in use once made).
    pub move_to: MoveToManager,
    move_to_made: bool,
    /// The object can move to things (OpenAC's move-to factory is set).
    pub can_move_to: bool,
    /// Sticky follow and the leash.
    pub position: PositionManager,
    /// The position manager is in use (OpenAC's `PositionManager` is set).
    pub position_attached: bool,
    /// The manager tail runs target tracking (OpenAC's targeting callback
    /// is set).
    pub targeting_attached: bool,
    /// Target subscriptions.
    pub target: TargetManager,
    /// The motion table.
    pub sequencer: Option<AnimationSequencer>,
    /// Where motions are animated.
    pub sink: MotionSink,
    /// The cycle's own velocity sets the walk and run speeds.
    pub cycle_velocity_from_sequencer: bool,
    /// Without a sink, completed dispatched motions are reported here (the
    /// local player's fallback).
    pub complete_without_sink: bool,
    /// The object's clock.
    pub clock: RetailObjectQuantumClock,
    /// Cylinder radius, metres.
    pub radius: f32,
    /// Cylinder height, metres.
    pub height: f32,
    /// "In contact" means on walkable ground (remote bodies) rather than
    /// touching anything (the local player).
    pub contact_means_walkable: bool,
    /// Catching up to server positions.
    pub interpolating: bool,
    /// Targets see the interpreter's plain top speed rather than the
    /// adjusted one (OpenAC's chase test harness answers that way).
    pub minterp_max_speed_unadjusted: bool,
    /// Game time, seconds.
    pub sim_time: f64,
    outbox: Vec<TargetMessage>,
    events: Vec<MoveToEvent>,
}

impl<W: WeenieObject> MovementObject<W> {
    /// An object with `body` and `weenie`, nothing else attached.
    #[must_use]
    pub fn new(token: ObjectToken, body: PhysicsBody, weenie: W) -> Self {
        Self {
            token,
            body,
            cell_id: 0,
            cell_from_body: false,
            weenie,
            interp: MotionInterpreter::new(),
            move_to: MoveToManager::new(0.0),
            move_to_made: false,
            can_move_to: false,
            position: PositionManager::default(),
            position_attached: false,
            targeting_attached: false,
            target: TargetManager::default(),
            sequencer: None,
            sink: MotionSink::None,
            cycle_velocity_from_sequencer: false,
            complete_without_sink: false,
            clock: RetailObjectQuantumClock::new(),
            radius: 0.0,
            height: 0.0,
            contact_means_walkable: false,
            interpolating: false,
            minterp_max_speed_unadjusted: false,
            sim_time: 0.0,
            outbox: Vec::new(),
            events: Vec::new(),
        }
    }

    /// Run operations against `world`.
    pub fn cx<'a>(&'a mut self, world: &'a dyn TargetWorld) -> ObjectCx<'a, W> {
        ObjectCx { obj: self, world }
    }

    /// The move-to manager has been made.
    #[must_use]
    pub fn move_to_made(&self) -> bool {
        self.move_to_made
    }

    /// Take the messages posted for other objects.
    pub fn take_target_messages(&mut self) -> Vec<TargetMessage> {
        std::mem::take(&mut self.outbox)
    }

    /// Take the move-to completions and cancellations.
    pub fn take_move_to_events(&mut self) -> Vec<MoveToEvent> {
        std::mem::take(&mut self.events)
    }

    /// Where the object is (cell, world origin, orientation).
    #[must_use]
    pub fn position(&self) -> CellPosition {
        let cell = if self.cell_from_body {
            self.body.cell_position().obj_cell_id
        } else {
            self.cell_id
        };
        CellPosition::new(cell, self.body.position(), self.body.orientation)
    }
}

/// An object and its view of the world, for the duration of a call.
pub struct ObjectCx<'a, W: WeenieObject> {
    /// The object.
    pub obj: &'a mut MovementObject<W>,
    world: &'a dyn TargetWorld,
}

impl<W: WeenieObject> std::fmt::Debug for ObjectCx<'_, W> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ObjectCx").finish_non_exhaustive()
    }
}

impl<W: WeenieObject> ObjectCx<'_, W> {
    /// Run `f` on the motion table and tell the interpreter about every
    /// motion it reported finished, in order.
    fn with_sequencer(&mut self, f: impl FnOnce(&mut AnimationSequencer, &mut dyn MotionDoneSink)) {
        let mut done = DoneMotions::default();
        if let Some(seq) = self.obj.sequencer.as_mut() {
            f(seq, &mut done);
        }
        for (motion, success) in done.0 {
            MotionInterp::motion_done(self, motion, success);
        }
    }

    /// A finished animation (the motion table's animation-done hook).
    pub fn animation_done(&mut self, success: bool) {
        self.with_sequencer(|seq, sink| seq.animation_done(success, sink));
    }

    /// Take the hooks the last advance reached and report the animations
    /// that finished among them (a host that draws nothing does this after
    /// each step).
    pub fn take_reached_points(&mut self) {
        let Some(seq) = self.obj.sequencer.as_mut() else {
            return;
        };
        let reached = seq.consume_pending_hooks();
        for hook in reached {
            if matches!(hook.data, ac_dat::objects::HookData::AnimationDone) {
                self.animation_done(true);
            }
        }
    }

    /// Complete dispatched motions when nothing animates them
    /// ([`complete_dispatched_motions`]).
    pub fn complete_dispatched_motions(&mut self) {
        complete_dispatched_motions(self);
    }
}

/// Complete dispatched motions when nothing animates them (no sink): the
/// queue's head is reported done until it is empty, nothing leaves it
/// (two queued motions can be equal, so progress is the queue's length), or
/// 64 went.
pub fn complete_dispatched_motions<H: InterpreterHost + ?Sized>(h: &mut H) {
    if h.default_sink().is_some() {
        return;
    }
    for _ in 0..MAXIMUM_MOTION_COMPLETIONS_PER_PASS {
        let Some(head) = h.interp().pending_motion_head() else {
            return;
        };
        let outstanding = h.interp().pending_motion_count();
        MotionInterp::motion_done(h, head.motion, true);
        if h.interp().pending_motion_count() >= outstanding {
            return;
        }
    }
}

impl<W: WeenieObject> InterpreterHost for ObjectCx<'_, W> {
    fn interp(&self) -> &MotionInterpreter {
        &self.obj.interp
    }
    fn interp_mut(&mut self) -> &mut MotionInterpreter {
        &mut self.obj.interp
    }
    fn physics_obj(&self) -> Option<&PhysicsBody> {
        Some(&self.obj.body)
    }
    fn physics_obj_mut(&mut self) -> Option<&mut PhysicsBody> {
        Some(&mut self.obj.body)
    }
    fn weenie_obj(&self) -> Option<&dyn WeenieObject> {
        Some(&self.obj.weenie)
    }
    fn unstick_from_object(&mut self) {
        if self.obj.position_attached {
            self.un_stick();
        }
    }
    fn interrupt_current_movement(&mut self) {
        self.movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
    }
    fn remove_link_animations(&mut self) {
        self.with_sequencer(|seq, sink| seq.handle_enter_world(sink));
    }
    fn initialize_motion_tables(&mut self) {
        if let Some(seq) = self.obj.sequencer.as_mut() {
            seq.manager_initialize_state();
        }
    }
    fn check_for_completed_motions(&mut self) {
        if self.obj.sequencer.is_some() {
            self.with_sequencer(|seq, sink| seq.check_for_completed_motions(sink));
        } else if self.obj.complete_without_sink {
            self.complete_dispatched_motions();
        }
    }
    fn cycle_velocity(&mut self) -> Option<Vec3> {
        if !self.obj.cycle_velocity_from_sequencer {
            return None;
        }
        self.obj
            .sequencer
            .as_ref()
            .map(AnimationSequencer::current_velocity)
    }
    fn default_sink(&mut self) -> Option<&mut dyn InterpretedMotionSink> {
        match &mut self.obj.sink {
            MotionSink::None => None,
            MotionSink::Sequencer => self
                .obj
                .sequencer
                .as_mut()
                .map(|s| s as &mut dyn InterpretedMotionSink),
            MotionSink::Custom(s) => Some(s.as_mut()),
        }
    }
}

impl<W: WeenieObject> MoveToHost for ObjectCx<'_, W> {
    fn move_to(&mut self) -> &mut MoveToManager {
        &mut self.obj.move_to
    }
    fn move_to_ref(&self) -> &MoveToManager {
        &self.obj.move_to
    }
    fn move_to_stop_completely(&mut self) {
        self.movement_perform(&crate::movement::interp::MovementStruct {
            kind: ac_motion::MovementType::StopCompletely,
            ..Default::default()
        });
    }
    fn move_to_position(&self) -> CellPosition {
        self.obj.position()
    }
    fn move_to_heading(&self) -> f32 {
        get_heading(self.obj.body.orientation)
    }
    fn move_to_set_heading(&mut self, heading: f32, _send: bool) {
        self.obj.body.orientation = set_heading(heading);
    }
    fn move_to_own_radius(&self) -> f32 {
        self.obj.radius
    }
    fn move_to_own_height(&self) -> f32 {
        self.obj.height
    }
    fn move_to_contact(&self) -> bool {
        self.obj_in_contact()
    }
    fn move_to_is_interpolating(&self) -> bool {
        self.obj.interpolating
    }
    fn move_to_velocity(&self) -> Vec3 {
        self.obj.body.velocity
    }
    fn move_to_self_id(&self) -> u32 {
        self.obj.token.id
    }
    fn move_to_set_target(&mut self, context_id: u32, object_id: u32, radius: f32, quantum: f64) {
        self.set_target(context_id, object_id, radius, quantum);
    }
    fn move_to_clear_target(&mut self) {
        self.clear_target();
    }
    fn move_to_target_quantum(&self) -> f64 {
        self.obj.target.get_target_quantum()
    }
    fn move_to_set_target_quantum(&mut self, quantum: f64) {
        self.set_target_quantum(quantum);
    }
    fn move_to_cur_time(&mut self) -> f64 {
        self.obj.sim_time
    }
    fn move_to_unstick(&mut self) {
        if self.obj.position_attached {
            self.un_stick();
        }
    }
    fn move_to_stick_to(&mut self, object_id: u32, radius: f32, height: f32) {
        if self.obj.position_attached {
            self.stick_to(object_id, radius, height);
        }
    }
    fn move_to_complete(&mut self, error: WeenieError) {
        self.obj.events.push(MoveToEvent::Complete(error));
    }
    fn move_to_cancelled(&mut self, error: WeenieError) {
        self.obj.events.push(MoveToEvent::Cancelled(error));
    }
}

impl<W: WeenieObject> MovementManagerHost for ObjectCx<'_, W> {
    fn move_to_exists(&self) -> bool {
        self.obj.move_to_made
    }
    fn make_move_to_manager(&mut self) -> bool {
        if !self.obj.move_to_made && self.obj.can_move_to {
            let now = self.obj.sim_time;
            self.obj.move_to = MoveToManager::new(now);
            self.obj.move_to_made = true;
        }
        self.obj.move_to_made
    }
    fn activate_physics_object(&mut self) {
        if self.obj.body.state.contains(PhysicsState::STATIC) {
            return;
        }
        self.obj.clock.activate();
        self.obj.body.transient_state.insert(TransientState::ACTIVE);
    }
}

impl<W: WeenieObject> PhysicsObjHost for ObjectCx<'_, W> {
    fn obj_token(&self) -> ObjectToken {
        self.obj.token
    }
    fn obj_position(&self) -> CellPosition {
        self.obj.position()
    }
    fn obj_velocity(&self) -> Vec3 {
        self.obj.body.velocity
    }
    fn obj_radius(&self) -> f32 {
        self.obj.radius
    }
    fn obj_in_contact(&self) -> bool {
        if self.obj.contact_means_walkable {
            self.obj.body.on_walkable()
        } else {
            self.obj.body.in_contact()
        }
    }
    fn obj_minterp_max_speed(&self) -> Option<f32> {
        Some(if self.obj.minterp_max_speed_unadjusted {
            self.get_max_speed()
        } else {
            self.get_adjusted_max_speed()
        })
    }
    fn obj_cur_time(&self) -> f64 {
        self.obj.sim_time
    }
    fn obj_physics_timer_time(&self) -> f64 {
        self.obj.sim_time
    }
    fn target_manager(&mut self) -> &mut TargetManager {
        &mut self.obj.target
    }
    fn target_manager_ref(&self) -> &TargetManager {
        &self.obj.target
    }
    fn obj_resolve(&self, id: u32) -> Option<ObjectToken> {
        self.world.resolve(id)
    }
    fn obj_position_of(&self, token: ObjectToken) -> Option<CellPosition> {
        self.world.position_of(token)
    }
    fn obj_post(&mut self, message: TargetMessage) {
        self.obj.outbox.push(message);
    }
    fn obj_handle_update_target(&mut self, info: TargetInfo) {
        self.movement_handle_update_target(info);
        if self.obj.position_attached {
            self.position_handle_update_target(info);
        }
    }
    fn obj_interrupt_current_movement(&mut self) {
        self.movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
    }
}

impl<W: WeenieObject> PositionManagerHost for ObjectCx<'_, W> {
    fn position_manager(&mut self) -> &mut PositionManager {
        &mut self.obj.position
    }
    fn position_manager_ref(&self) -> &PositionManager {
        &self.obj.position
    }
}

/// A finished motion reported by the motion table reaches the interpreter.
impl<W: WeenieObject> MotionDoneSink for ObjectCx<'_, W> {
    fn motion_done(&mut self, motion: u32, success: bool) {
        MotionInterp::motion_done(self, motion, success);
    }
}
