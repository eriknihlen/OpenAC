//! The motion interpreter: turns motion requests (raw input commands, or
//! the server's interpreted state) into the object's interpreted motion
//! state, animation commands for its motion table, and body velocity.
//!
//! It keeps three things per object ([`MotionInterpreter`]): the raw state
//! (what the input asks for), the interpreted state (what the object is
//! doing, normalised), and the queue of motions still waiting for their
//! animations (each with the jump-refusal code it carries).
//!
//! The interpreter reaches its body, its weenie, the motion table and the
//! movement managers through [`InterpreterHost`]; its operations are the
//! provided methods of [`MotionInterp`], available on every host. Retail's
//! interpreter holds pointers to all of these; here the host is passed in,
//! so a callback can reach back into the interpreter safely.

use std::collections::VecDeque;

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::command::ACTION_BIT;
use ac_motion::motion_command::{
    CROUCH, DEAD, FALLEN, FALLING, NON_COMBAT, READY, RUN_FORWARD, SIDE_STEP_LEFT, SIDE_STEP_RIGHT,
    SITTING, SLEEPING, TURN_LEFT, TURN_RIGHT, WALK_BACKWARDS, WALK_FORWARD,
};
use ac_motion::{
    AnimationSequencer, BACKWARDS_FACTOR, HoldKey, InterpretedMotionState, MAX_SIDESTEP_ANIM_RATE,
    MotionTableMovement, MovementParameters, MovementType, RUN_ANIM_SPEED, RUN_TURN_FACTOR,
    RawMotionState, SIDESTEP_ANIM_SPEED, SIDESTEP_FACTOR, WALK_ANIM_SPEED,
};
use glam::{Quat, Vec3};

use crate::body::{CellPosition, PhysicsBody};
use crate::geometry::normalize;
use crate::movement::weenie::WeenieObject;
use crate::movement::weenie_error::WeenieError;

/// Jump extents below this are no jump.
#[allow(clippy::excessive_precision)]
pub const JUMP_VZ_EPSILON: f32 = 0.000_199_999_995;
/// Take-off speed without a weenie, metres per second.
pub const DEFAULT_JUMP_VZ: f32 = 10.0;
/// Largest jump extent.
pub const MAX_JUMP_EXTENT: f32 = 1.0;
/// Lowest forward command of the crouch/sit/sleep range (exclusive).
pub const CROUCH_LOWER_BOUND: u32 = 0x4100_0011;
/// Upper bound of the crouch/sit/sleep range (exclusive).
pub const CROUCH_UPPER_EXCLUSIVE: u32 = 0x4100_0015;
/// The chat-emote class bit of a motion command.
const CHAT_EMOTE_BIT: u32 = 0x0200_0000;
/// The smallest positive `f32` (.NET `float.Epsilon`).
const FLOAT_EPSILON: f32 = f32::from_bits(1);

/// A motion queued until its animations finish, with the jump-refusal code
/// a jump attempted meanwhile returns.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct MotionNode {
    /// Request context.
    pub context_id: u32,
    /// Motion command.
    pub motion: u32,
    /// Jump-refusal code (0 when a jump is allowed).
    pub jump_error_code: u32,
}

/// Where interpreted motions go to be animated (the object's motion table).
pub trait InterpretedMotionSink {
    /// Start `motion` at `speed`; false when the table refuses it.
    fn apply_motion(&mut self, motion: u32, speed: f32) -> bool;
    /// Stop `motion`; false when the table refuses it.
    fn stop_motion(&mut self, motion: u32) -> bool;
    /// Stop everything.
    fn stop_completely(&mut self) -> bool {
        true
    }
}

/// The motion table as a sink: each motion becomes an interpreted movement
/// request on the sequencer.
impl InterpretedMotionSink for AnimationSequencer {
    fn apply_motion(&mut self, motion: u32, speed: f32) -> bool {
        self.perform_movement(MotionTableMovement::interpreted(motion, speed))
            .is_ok()
    }

    fn stop_motion(&mut self, motion: u32) -> bool {
        self.perform_movement(MotionTableMovement::stop_interpreted(motion, 1.0))
            .is_ok()
    }

    fn stop_completely(&mut self) -> bool {
        self.perform_movement(MotionTableMovement::stop_completely())
            .is_ok()
    }
}

/// A movement request.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MovementStruct {
    /// What to do.
    pub kind: MovementType,
    /// Motion command (commands and stops).
    pub motion: u32,
    /// Speed (when no parameters are given).
    pub speed: f32,
    /// From the object itself (when no parameters are given).
    pub autonomous: bool,
    /// Change the interpreted state (when no parameters are given).
    pub modify_interpreted_state: bool,
    /// Change the raw state (when no parameters are given).
    pub modify_raw_state: bool,
    /// Object to move or turn to.
    pub object_id: u32,
    /// Its top-level object.
    pub top_level_id: u32,
    /// Position to move to (cell, world origin, orientation).
    pub pos: CellPosition,
    /// Target radius, metres.
    pub radius: f32,
    /// Target height, metres.
    pub height: f32,
    /// Request options; when absent they are built from the fields above.
    pub params: Option<MovementParameters>,
}

impl Default for MovementStruct {
    fn default() -> Self {
        Self {
            kind: MovementType::Invalid,
            motion: 0,
            speed: 0.0,
            autonomous: false,
            modify_interpreted_state: false,
            modify_raw_state: false,
            object_id: 0,
            top_level_id: 0,
            pos: CellPosition::default(),
            radius: 0.0,
            height: 0.0,
            params: None,
        }
    }
}

/// A queued action as the server relays it in an interpreted state.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct InboundMotionAction {
    /// Full motion command.
    pub command: u32,
    /// Action stamp (low 15 bits count).
    pub stamp: i32,
    /// Requested by the object itself.
    pub autonomous: bool,
    /// Speed factor.
    pub speed: f32,
}

/// An interpreted motion state from the server.
#[derive(Debug, Clone, PartialEq)]
pub struct InboundInterpretedState {
    /// Stance.
    pub current_style: u32,
    /// Forward command.
    pub forward_command: u32,
    /// Forward speed.
    pub forward_speed: f32,
    /// Sidestep command.
    pub side_step_command: u32,
    /// Sidestep speed.
    pub side_step_speed: f32,
    /// Turn command.
    pub turn_command: u32,
    /// Turn speed.
    pub turn_speed: f32,
    /// Actions to replay, oldest first.
    pub actions: Option<Vec<InboundMotionAction>>,
}

impl Default for InboundInterpretedState {
    fn default() -> Self {
        Self {
            current_style: NON_COMBAT,
            forward_command: READY,
            forward_speed: 1.0,
            side_step_command: 0,
            side_step_speed: 1.0,
            turn_command: 0,
            turn_speed: 1.0,
            actions: None,
        }
    }
}

/// The interpreter's per-object state.
#[derive(Debug, Clone, PartialEq)]
pub struct MotionInterpreter {
    /// What the input asks for.
    pub raw_state: RawMotionState,
    /// What the object is doing (normalised).
    pub interpreted_state: InterpretedMotionState,
    /// Extent of the jump being taken (0..1).
    pub jump_extent: f32,
    /// The last run rate seen (forward speed of a run), used when the
    /// weenie cannot say.
    pub my_run_rate: f32,
    /// A standing jump is charging: walk, run and sidestep only change state.
    pub standing_long_jump: bool,
    /// Initialised (the default state was entered).
    pub initted: bool,
    /// The local player (skips its own autonomous action echoes).
    pub is_local_player: bool,
    /// Newest server action stamp seen.
    pub server_action_stamp: i32,
    pending_motions: VecDeque<MotionNode>,
}

impl Default for MotionInterpreter {
    fn default() -> Self {
        Self {
            raw_state: RawMotionState::default(),
            interpreted_state: InterpretedMotionState::default(),
            jump_extent: 0.0,
            my_run_rate: 1.0,
            standing_long_jump: false,
            initted: true,
            is_local_player: false,
            server_action_stamp: 0,
            pending_motions: VecDeque::new(),
        }
    }
}

impl MotionInterpreter {
    /// A fresh interpreter: idle states, run rate 1, initialised.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The motions waiting for their animations, oldest first.
    pub fn pending_motions(&self) -> impl ExactSizeIterator<Item = &MotionNode> {
        self.pending_motions.iter()
    }

    /// The oldest waiting motion.
    #[must_use]
    pub fn pending_motion_head(&self) -> Option<MotionNode> {
        self.pending_motions.front().copied()
    }

    /// How many motions are waiting (two can hold equal values).
    #[must_use]
    pub fn pending_motion_count(&self) -> usize {
        self.pending_motions.len()
    }

    /// Queue a motion.
    pub fn add_to_queue(&mut self, context_id: u32, motion: u32, jump_error_code: u32) {
        self.pending_motions.push_back(MotionNode {
            context_id,
            motion,
            jump_error_code,
        });
    }

    /// Something is waiting.
    #[must_use]
    pub fn motions_pending(&self) -> bool {
        !self.pending_motions.is_empty()
    }

    /// The walk/run key.
    #[must_use]
    pub fn current_hold_key(&self) -> HoldKey {
        self.raw_state.current_hold_key
    }

    /// Record the run key's state without re-deriving motion: for the first
    /// observation of a key that was already down, which is not an edge (a
    /// move-to's turn under way must survive it).
    pub fn seed_hold_run(&mut self, holding_run: bool) {
        self.raw_state.current_hold_key = if holding_run {
            HoldKey::Run
        } else {
            HoldKey::None
        };
    }

    /// Whether a jump is allowed out of `motion` (none, or "you can't jump
    /// from this position" for the blocked ranges).
    #[must_use]
    pub fn motion_allows_jump(motion: u32) -> WeenieError {
        if motion > 0x4000_0018 {
            if motion > 0x4100_0014 {
                return WeenieError::NONE;
            }
            if motion < 0x4100_0012 && !(0x4000_001E..=0x4000_0039).contains(&motion) {
                return WeenieError::NONE;
            }
        } else if motion < 0x4000_0016 {
            if motion > 0x1000_0131 {
                if motion != 0x4000_0008 {
                    return WeenieError::NONE;
                }
            } else if motion < 0x1000_0128 && !(0x1000_006F..=0x1000_0078).contains(&motion) {
                return WeenieError::NONE;
            }
        }
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
    }

    fn pop_head(&mut self) -> Option<MotionNode> {
        self.pending_motions.pop_front()
    }
}

/// What the interpreter reaches through its object. Callbacks default to
/// nothing (the object has no such manager).
pub trait InterpreterHost {
    /// The interpreter state.
    fn interp(&self) -> &MotionInterpreter;
    /// The interpreter state, mutably.
    fn interp_mut(&mut self) -> &mut MotionInterpreter;
    /// The body; `None` when the object has none.
    fn physics_obj(&self) -> Option<&PhysicsBody>;
    /// The body, mutably.
    fn physics_obj_mut(&mut self) -> Option<&mut PhysicsBody>;
    /// The weenie; `None` when the object has none.
    fn weenie_obj(&self) -> Option<&dyn WeenieObject>;
    /// Release a sticky target (an action finished).
    fn unstick_from_object(&mut self) {}
    /// Cancel a move-to in progress.
    fn interrupt_current_movement(&mut self) {}
    /// Drop the animation link animations (entering the world, landing,
    /// dying) and abandon their queued motions.
    fn remove_link_animations(&mut self) {}
    /// Reset the motion table to its default state.
    fn initialize_motion_tables(&mut self) {}
    /// Report motions whose animations have finished.
    fn check_for_completed_motions(&mut self) {}
    /// The velocity of the cycle playing, metres per second in the body's
    /// axes, when an animation is playing.
    fn cycle_velocity(&mut self) -> Option<Vec3> {
        None
    }
    /// The default animation sink.
    fn default_sink(&mut self) -> Option<&mut dyn InterpretedMotionSink> {
        None
    }
}

/// Which sink a call dispatches to.
#[derive(Default)]
pub enum SinkArg<'a> {
    /// The host's default sink (none: dispatch succeeds without animating).
    #[default]
    Default,
    /// This sink, or none.
    Given(Option<&'a mut dyn InterpretedMotionSink>),
}

impl std::fmt::Debug for SinkArg<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Default => f.write_str("Default"),
            Self::Given(s) => write!(f, "Given({})", if s.is_some() { "sink" } else { "none" }),
        }
    }
}

impl SinkArg<'_> {
    /// The same choice, reborrowed for one more call.
    pub fn reborrow(&mut self) -> SinkArg<'_> {
        match self {
            Self::Default => SinkArg::Default,
            Self::Given(s) => SinkArg::Given(s.as_deref_mut().map(|s| s as _)),
        }
    }
}

fn sink_apply<H: InterpreterHost + ?Sized>(
    h: &mut H,
    sink: SinkArg<'_>,
    motion: u32,
    speed: f32,
) -> bool {
    match sink {
        SinkArg::Default => h
            .default_sink()
            .is_none_or(|s| s.apply_motion(motion, speed)),
        SinkArg::Given(Some(s)) => s.apply_motion(motion, speed),
        SinkArg::Given(None) => true,
    }
}

fn sink_stop<H: InterpreterHost + ?Sized>(h: &mut H, sink: SinkArg<'_>, motion: u32) -> bool {
    match sink {
        SinkArg::Default => h.default_sink().is_none_or(|s| s.stop_motion(motion)),
        SinkArg::Given(Some(s)) => s.stop_motion(motion),
        SinkArg::Given(None) => true,
    }
}

fn is_walk_run_or_sidestep(motion: u32) -> bool {
    motion == WALK_FORWARD || motion == RUN_FORWARD || motion == SIDE_STEP_RIGHT
}

/// The interpreter's operations, on any [`InterpreterHost`].
pub trait MotionInterp: InterpreterHost {
    /// Dispatch a movement request; every dispatched request ends with a
    /// completed-motions check. An unknown kind fails without one.
    fn perform_movement(&mut self, mvs: &MovementStruct) -> WeenieError {
        let p = mvs.params.unwrap_or(MovementParameters {
            speed: mvs.speed,
            autonomous: mvs.autonomous,
            modify_interpreted_state: mvs.modify_interpreted_state,
            modify_raw_state: mvs.modify_raw_state,
            ..MovementParameters::default()
        });
        let result = match mvs.kind {
            MovementType::RawCommand => self.do_motion(mvs.motion, &p),
            MovementType::InterpretedCommand => self.do_interpreted_motion(mvs.motion, &p),
            MovementType::StopRawCommand => self.stop_motion(mvs.motion, &p),
            MovementType::StopInterpretedCommand => self.stop_interpreted_motion(mvs.motion, &p),
            MovementType::StopCompletely => self.stop_completely(),
            _ => return WeenieError::GENERAL_MOVEMENT_FAILURE,
        };
        self.check_for_completed_motions();
        result
    }

    /// Start a raw (input) motion: cancel a move-to and set the hold key
    /// when asked, normalise the motion, refuse postures and emotes outside
    /// non-combat and a seventh queued action, run it as an interpreted
    /// motion with fresh options, and on success record the original motion
    /// in the raw state.
    fn do_motion(&mut self, motion: u32, p: &MovementParameters) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let original = motion;
        let mut motion = motion;
        let mut speed = p.speed;
        let mut local = MovementParameters::default();
        if p.cancel_move_to {
            self.interrupt_current_movement();
        }
        if p.set_hold_key {
            self.set_hold_key(p.hold_key_to_apply, p.cancel_move_to);
        }
        self.adjust_motion(&mut motion, &mut speed, p.hold_key_to_apply);

        if self.interp().interpreted_state.current_style != NON_COMBAT {
            if original == CROUCH {
                return WeenieError::CROUCH_IN_COMBAT_STANCE;
            }
            if original == SITTING {
                return WeenieError::SIT_IN_COMBAT_STANCE;
            }
            if original == SLEEPING {
                return WeenieError::SLEEP_IN_COMBAT_STANCE;
            }
            if original & CHAT_EMOTE_BIT != 0 {
                return WeenieError::CHAT_EMOTE_OUTSIDE_NON_COMBAT;
            }
        }
        if original & ACTION_BIT != 0 && self.interp().interpreted_state.get_num_actions() >= 6 {
            return WeenieError::ACTION_DEPTH_EXCEEDED;
        }

        local.speed = speed;
        local.hold_key_to_apply = p.hold_key_to_apply;
        let result = self.do_interpreted_motion(motion, &local);
        if result.is_none() && p.modify_raw_state {
            self.interp_mut().raw_state.apply_motion(original, p);
        }
        result
    }

    /// [`MotionInterp::do_motion`] with default options at `speed`.
    fn do_motion_at(&mut self, motion: u32, speed: f32) -> WeenieError {
        self.do_motion(
            motion,
            &MovementParameters {
                speed,
                ..MovementParameters::default()
            },
        )
    }

    /// Stop a raw motion: cancel a move-to when asked, normalise, stop the
    /// interpreted motion, and on success clear the original motion from the
    /// raw state. Postures stop in any stance.
    fn stop_motion(&mut self, motion: u32, p: &MovementParameters) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let original = motion;
        let mut motion = motion;
        let mut speed = p.speed;
        let mut local = MovementParameters::default();
        if p.cancel_move_to {
            self.interrupt_current_movement();
        }
        self.adjust_motion(&mut motion, &mut speed, p.hold_key_to_apply);
        local.speed = speed;
        local.hold_key_to_apply = p.hold_key_to_apply;
        let result = self.stop_interpreted_motion(motion, &local);
        if result.is_none() && p.modify_raw_state {
            self.interp_mut().raw_state.remove_motion(original);
        }
        result
    }

    /// Stop everything: cancel a move-to, set every axis of both states to
    /// idle (speeds of sidestep and turn are kept), stop the motion table,
    /// zero the velocity and queue a ready motion carrying the jump code of
    /// the forward command it replaced. Out of the world, the link
    /// animations go at once.
    fn stop_completely(&mut self) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        self.interrupt_current_movement();
        let jump_snapshot =
            MotionInterpreter::motion_allows_jump(self.interp().interpreted_state.forward_command);
        {
            let interp = self.interp_mut();
            interp.raw_state.forward_command = READY;
            interp.raw_state.forward_speed = 1.0;
            interp.raw_state.sidestep_command = 0;
            interp.raw_state.turn_command = 0;
            interp.interpreted_state.forward_command = READY;
            interp.interpreted_state.forward_speed = 1.0;
            interp.interpreted_state.side_step_command = 0;
            interp.interpreted_state.turn_command = 0;
        }
        if let Some(sink) = self.default_sink() {
            sink.stop_completely();
        }
        if let Some(body) = self.physics_obj_mut() {
            body.set_velocity(Vec3::ZERO);
        }
        self.interp_mut()
            .add_to_queue(0, READY, jump_snapshot.code());
        if self.physics_obj().is_some_and(|b| !b.in_world) {
            self.remove_link_animations();
        }
        WeenieError::NONE
    }

    /// The velocity the interpreted state asks for, metres per second in
    /// the body's axes: sidestep on x, walk or run on y (the playing
    /// cycle's own speed when it has one), capped at the run speed.
    fn get_state_velocity(&mut self) -> Vec3 {
        let mut velocity = Vec3::ZERO;
        let (side, side_speed) = {
            let s = &self.interp().interpreted_state;
            (s.side_step_command, s.side_step_speed)
        };
        if side == SIDE_STEP_RIGHT {
            velocity.x = SIDESTEP_ANIM_SPEED * side_speed;
        }
        let cycle = self.cycle_velocity();
        let have_cycle_forward = cycle.is_some_and(|c| c.y.abs() > FLOAT_EPSILON);
        let (forward, forward_speed) = {
            let s = &self.interp().interpreted_state;
            (s.forward_command, s.forward_speed)
        };
        if forward == WALK_FORWARD {
            velocity.y = match cycle {
                Some(c) if have_cycle_forward => c.y,
                _ => WALK_ANIM_SPEED * forward_speed,
            };
        } else if forward == RUN_FORWARD {
            velocity.y = match cycle {
                Some(c) if have_cycle_forward => c.y,
                _ => RUN_ANIM_SPEED * forward_speed,
            };
        }

        let mut rate = self.interp().my_run_rate;
        if let Some(queried) = self.weenie_obj().and_then(WeenieObject::inq_run_rate) {
            rate = queried;
        }
        let max_speed = RUN_ANIM_SPEED * rate;
        let len = velocity.length();
        if len > max_speed && len > 0.0 {
            velocity = normalize(velocity) * max_speed;
        }
        velocity
    }

    /// Normalise a motion: left turns and sidesteps become right ones at
    /// negative speed, walking backwards becomes walking forwards at −0.65
    /// of the speed, sidesteps are scaled to the walk speed, and with the
    /// run key (`hold_key`, or the current one when invalid) the run rate
    /// applies. Run forward is left as it is; non-creatures are not
    /// normalised.
    fn adjust_motion(&self, motion: &mut u32, speed: &mut f32, hold_key: HoldKey) {
        if self.weenie_obj().is_some_and(|w| !w.is_creature()) {
            return;
        }
        match *motion {
            RUN_FORWARD => return,
            WALK_BACKWARDS => {
                *motion = WALK_FORWARD;
                *speed *= -BACKWARDS_FACTOR;
            }
            TURN_LEFT => {
                *motion = TURN_RIGHT;
                *speed *= -1.0;
            }
            SIDE_STEP_LEFT => {
                *motion = SIDE_STEP_RIGHT;
                *speed *= -1.0;
            }
            _ => {}
        }
        if *motion == SIDE_STEP_RIGHT {
            *speed *= SIDESTEP_FACTOR * (WALK_ANIM_SPEED / SIDESTEP_ANIM_SPEED);
        }
        let hold_key = if hold_key == HoldKey::Invalid {
            self.interp().current_hold_key()
        } else {
            hold_key
        };
        if hold_key == HoldKey::Run {
            self.apply_run_to_command(motion, speed);
        }
    }

    /// Apply the run key: walking forward at positive speed becomes running,
    /// and walk speed is scaled by the run rate; turns are 1.5 times faster;
    /// sidesteps are scaled by the run rate and capped at ±3.
    fn apply_run_to_command(&self, motion: &mut u32, speed: &mut f32) {
        let mut speed_mod = 1.0;
        if let Some(w) = self.weenie_obj() {
            speed_mod = w.inq_run_rate().unwrap_or(self.interp().my_run_rate);
        }
        match *motion {
            WALK_FORWARD => {
                if *speed > 0.0 {
                    *motion = RUN_FORWARD;
                }
                *speed *= speed_mod;
            }
            TURN_RIGHT => *speed *= RUN_TURN_FACTOR,
            SIDE_STEP_RIGHT => {
                *speed *= speed_mod;
                if speed.abs() > MAX_SIDESTEP_ANIM_RATE {
                    *speed = if *speed > 0.0 {
                        MAX_SIDESTEP_ANIM_RATE
                    } else {
                        -MAX_SIDESTEP_ANIM_RATE
                    };
                }
            }
            _ => {}
        }
    }

    /// Derive the interpreted state from `raw`: copy each axis and
    /// normalise it with its own hold key.
    fn apply_raw_movement_state(&mut self, raw: &RawMotionState) {
        {
            let interp = self.interp_mut();
            interp.raw_state.current_hold_key = raw.current_hold_key;
            let s = &mut interp.interpreted_state;
            s.forward_command = raw.forward_command;
            s.forward_speed = raw.forward_speed;
            s.side_step_command = raw.sidestep_command;
            s.side_step_speed = raw.sidestep_speed;
            s.turn_command = raw.turn_command;
            s.turn_speed = raw.turn_speed;
        }
        let (mut c, mut s) = {
            let st = &self.interp().interpreted_state;
            (st.forward_command, st.forward_speed)
        };
        self.adjust_motion(&mut c, &mut s, raw.forward_hold_key);
        {
            let st = &mut self.interp_mut().interpreted_state;
            st.forward_command = c;
            st.forward_speed = s;
        }
        let (mut c, mut s) = {
            let st = &self.interp().interpreted_state;
            (st.side_step_command, st.side_step_speed)
        };
        self.adjust_motion(&mut c, &mut s, raw.sidestep_hold_key);
        {
            let st = &mut self.interp_mut().interpreted_state;
            st.side_step_command = c;
            st.side_step_speed = s;
        }
        let (mut c, mut s) = {
            let st = &self.interp().interpreted_state;
            (st.turn_command, st.turn_speed)
        };
        self.adjust_motion(&mut c, &mut s, raw.turn_hold_key);
        let st = &mut self.interp_mut().interpreted_state;
        st.turn_command = c;
        st.turn_speed = s;
    }

    /// Re-apply the current movement: the player's own (autonomous) motion
    /// is re-derived from the raw state first; everything else replays the
    /// interpreted state.
    fn apply_current_movement(&mut self, cancel_move_to: bool, allow_jump: bool) {
        let Some(body) = self.physics_obj() else {
            return;
        };
        if !self.interp().initted {
            return;
        }
        let autonomous = body.last_move_was_autonomous;
        let is_the_player = self.weenie_obj().is_none_or(|w| w.is_the_player());
        if is_the_player && autonomous {
            self.apply_raw_movement(cancel_move_to, allow_jump);
            return;
        }
        self.apply_current_movement_interpreted(cancel_move_to, allow_jump);
    }

    /// Re-derive the interpreted state from the raw state, then replay it.
    fn apply_raw_movement(&mut self, cancel_move_to: bool, allow_jump: bool) {
        if self.physics_obj().is_none() {
            return;
        }
        let raw = self.interp().raw_state.clone();
        self.apply_raw_movement_state(&raw);
        self.apply_current_movement_interpreted(cancel_move_to, allow_jump);
    }

    /// Replay the interpreted state into the motion table (with a default
    /// sink), or (without one) set the body's velocity from it on walkable
    /// ground.
    fn apply_current_movement_interpreted(&mut self, cancel_move_to: bool, allow_jump: bool) {
        if self.physics_obj().is_none() {
            return;
        }
        if self.default_sink().is_some() {
            let style = self.interp().interpreted_state.current_style;
            self.apply_interpreted_movement(style, SinkArg::Default, cancel_move_to, allow_jump);
            return;
        }
        {
            let interp = self.interp_mut();
            if interp.interpreted_state.forward_command == RUN_FORWARD {
                interp.my_run_rate = interp.interpreted_state.forward_speed;
            }
        }
        if self.physics_obj().is_some_and(PhysicsBody::on_walkable) {
            let local_velocity = self.get_state_velocity();
            if let Some(body) = self.physics_obj_mut() {
                let autonomous = body.last_move_was_autonomous;
                body.set_local_velocity(local_velocity, autonomous);
            }
        }
    }

    /// Stamina ran out: re-apply the current movement (no cancel, jumps
    /// allowed).
    fn report_exhaustion(&mut self) {
        let Some(body) = self.physics_obj() else {
            return;
        };
        if !self.interp().initted {
            return;
        }
        let autonomous = body.last_move_was_autonomous;
        let is_the_player = self.weenie_obj().is_none_or(|w| w.is_the_player());
        if is_the_player && autonomous {
            self.apply_raw_movement(false, true);
            return;
        }
        self.apply_current_movement_interpreted(false, true);
    }

    /// Whether a jump of `extent` may start charging: not too loaded down,
    /// and not fallen, crouching, sitting or sleeping.
    fn jump_charge_is_allowed(&self, extent: f32) -> WeenieError {
        if self.weenie_obj().is_some_and(|w| !w.can_jump(extent)) {
            return WeenieError::CANT_JUMP_LOADED_DOWN;
        }
        let forward = self.interp().interpreted_state.forward_command;
        if forward != FALLEN && (forward <= CROUCH_LOWER_BOUND || forward > SLEEPING) {
            return WeenieError::NONE;
        }
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
    }

    /// Start charging a jump: refused as [`MotionInterp::jump_charge_is_allowed`];
    /// standing still on the ground arms the standing long jump.
    fn charge_jump(&mut self) -> WeenieError {
        let extent = self.interp().jump_extent;
        if self.weenie_obj().is_some_and(|w| !w.can_jump(extent)) {
            return WeenieError::CANT_JUMP_LOADED_DOWN;
        }
        let (forward, side, turn) = {
            let s = &self.interp().interpreted_state;
            (s.forward_command, s.side_step_command, s.turn_command)
        };
        if forward == FALLEN || (forward > CROUCH_LOWER_BOUND && forward <= SLEEPING) {
            return WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION;
        }
        if let Some(body) = self.physics_obj() {
            let on_ground = body.transient_state.contains(TransientState::CONTACT)
                && body.transient_state.contains(TransientState::ON_WALKABLE);
            if on_ground && forward == READY && side == 0 && turn == 0 {
                self.interp_mut().standing_long_jump = true;
            }
        }
        WeenieError::NONE
    }

    /// Jump with `extent`: cancel a move-to, check
    /// [`MotionInterp::jump_is_allowed`], and on success record the extent
    /// and leave walkable ground; on failure the standing long jump is
    /// dropped.
    fn jump(&mut self, extent: f32) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        self.interrupt_current_movement();
        let (result, _) = self.jump_is_allowed(extent);
        if result.is_none() {
            self.interp_mut().jump_extent = extent;
            if let Some(body) = self.physics_obj_mut() {
                body.set_on_walkable(false);
            }
            return WeenieError::NONE;
        }
        self.interp_mut().standing_long_jump = false;
        result
    }

    /// Take-off speed of the jump being taken, metres per second: 0 below
    /// the jump epsilon, the weenie's answer for the extent (at most 1), 10
    /// without a weenie, 0 when the weenie cannot say.
    fn get_jump_vz(&self) -> f32 {
        let mut extent = self.interp().jump_extent;
        if extent < JUMP_VZ_EPSILON {
            return 0.0;
        }
        if extent > MAX_JUMP_EXTENT {
            extent = MAX_JUMP_EXTENT;
        }
        match self.weenie_obj() {
            None => DEFAULT_JUMP_VZ,
            Some(w) => w.inq_jump_velocity(extent).unwrap_or(0.0),
        }
    }

    /// The body-axes velocity a body leaves the ground with: the state
    /// velocity plus the take-off speed, or (when all three are below the
    /// epsilon) the body's current velocity in its own axes.
    fn get_leave_ground_velocity(&mut self) -> Vec3 {
        let mut velocity = self.get_state_velocity();
        velocity.z = self.get_jump_vz();
        let eps = JUMP_VZ_EPSILON;
        if velocity.x.abs() < eps
            && velocity.y.abs() < eps
            && velocity.z.abs() < eps
            && let Some(body) = self.physics_obj()
        {
            let q = body.orientation;
            let inv = q.conjugate() * (1.0 / q.length_squared());
            velocity = Quat::from_xyzw(inv.x, inv.y, inv.z, inv.w) * body.velocity;
        }
        velocity
    }

    /// Whether a jump of `extent` is allowed and what it costs: grounded
    /// (or a non-creature, or no gravity), not fully leashed, the oldest
    /// queued motion's jump code clear, the charge allowed, the forward
    /// motion allowing it, and the stamina paid.
    fn jump_is_allowed(&self, extent: f32) -> (WeenieError, i32) {
        let mut stamina_cost = 0;
        if let Some(body) = self.physics_obj() {
            let non_creature = self.weenie_obj().is_some_and(|w| !w.is_creature());
            let gravity_off = !body.state.contains(PhysicsState::GRAVITY);
            let grounded = body.transient_state.contains(TransientState::CONTACT)
                && body.transient_state.contains(TransientState::ON_WALKABLE);
            if non_creature || gravity_off || grounded {
                let result = self.jump_is_allowed_shared_gate(extent, &mut stamina_cost);
                return (result, stamina_cost);
            }
        }
        (WeenieError::NOT_GROUNDED, stamina_cost)
    }

    #[doc(hidden)]
    fn jump_is_allowed_shared_gate(&self, extent: f32, stamina_cost: &mut i32) -> WeenieError {
        if self.physics_obj().is_some_and(|b| b.is_fully_constrained) {
            return WeenieError::GENERAL_MOVEMENT_FAILURE;
        }
        let head = self.interp().pending_motion_head();
        let peeked = head.map_or(0, |n| n.jump_error_code);
        if head.is_none() || peeked == 0 {
            let charge = self.jump_charge_is_allowed(extent);
            if charge.is_none() {
                let motion = MotionInterpreter::motion_allows_jump(
                    self.interp().interpreted_state.forward_command,
                );
                if !motion.is_none() {
                    return motion;
                }
                let Some(w) = self.weenie_obj() else {
                    return motion;
                };
                return match w.jump_stamina_cost(extent) {
                    Some(cost) => {
                        *stamina_cost = cost;
                        motion
                    }
                    None => WeenieError::GENERAL_MOVEMENT_FAILURE,
                };
            }
            return charge;
        }
        WeenieError(peeked)
    }

    /// Whether `motion` may start given the body's contact: turns, falling
    /// and dead always; anything for non-creatures and gravity-free bodies;
    /// otherwise only on walkable ground.
    fn contact_allows_move(&self, motion: u32) -> bool {
        let Some(body) = self.physics_obj() else {
            return false;
        };
        if motion > FALLING {
            if motion == TURN_RIGHT || motion == TURN_LEFT {
                return true;
            }
        } else if motion == FALLING || motion == DEAD {
            return true;
        }
        if self.weenie_obj().is_some_and(|w| !w.is_creature()) {
            return true;
        }
        if !body.state.contains(PhysicsState::GRAVITY) {
            return true;
        }
        body.transient_state.contains(TransientState::CONTACT)
            && body.transient_state.contains(TransientState::ON_WALKABLE)
    }

    /// The oldest queued motion's animations finished (whatever `motion`
    /// and `success` say): an action releases a sticky target and pops both
    /// action queues; the head is dropped.
    fn motion_done(&mut self, _motion: u32, _success: bool) {
        if self.physics_obj().is_none() {
            return;
        }
        let Some(head) = self.interp().pending_motion_head() else {
            return;
        };
        if head.motion & ACTION_BIT != 0 {
            self.unstick_from_object();
            let interp = self.interp_mut();
            interp.interpreted_state.remove_action();
            interp.raw_state.remove_action();
        }
        self.interp_mut().pop_head();
    }

    /// Leaving the world: drop every queued motion (actions release the
    /// sticky target and pop the action queues).
    fn handle_exit_world(&mut self) {
        while let Some(head) = self.interp().pending_motion_head() {
            if head.motion & ACTION_BIT != 0 {
                self.unstick_from_object();
                let interp = self.interp_mut();
                interp.interpreted_state.remove_action();
                interp.raw_state.remove_action();
            }
            self.interp_mut().pop_head();
        }
    }

    /// On walkable ground, ready, not sidestepping or turning.
    fn is_standing_still(&self) -> bool {
        let Some(body) = self.physics_obj() else {
            return false;
        };
        let grounded = body
            .transient_state
            .contains(TransientState::CONTACT | TransientState::ON_WALKABLE);
        if !grounded {
            return false;
        }
        let s = &self.interp().interpreted_state;
        s.forward_command == READY && s.side_step_command == 0 && s.turn_command == 0
    }

    /// The body left the ground (a creature under gravity): set the
    /// leave-ground velocity, end the jump, drop the link animations and
    /// re-apply the movement.
    fn leave_ground(&mut self) {
        let Some(body) = self.physics_obj() else {
            return;
        };
        let has_gravity = body.state.contains(PhysicsState::GRAVITY);
        if !self.weenie_obj().is_none_or(|w| w.is_creature()) {
            return;
        }
        if !has_gravity {
            return;
        }
        let velocity = self.get_leave_ground_velocity();
        if let Some(body) = self.physics_obj_mut() {
            body.set_local_velocity(velocity, true);
        }
        {
            let interp = self.interp_mut();
            interp.standing_long_jump = false;
            interp.jump_extent = 0.0;
        }
        self.remove_link_animations();
        self.apply_current_movement(false, true);
    }

    /// The body landed (a creature under gravity): drop the link animations
    /// and re-apply the movement.
    fn hit_ground(&mut self) {
        let Some(body) = self.physics_obj() else {
            return;
        };
        let has_gravity = body.state.contains(PhysicsState::GRAVITY);
        if !self.weenie_obj().is_none_or(|w| w.is_creature()) {
            return;
        }
        if !has_gravity {
            return;
        }
        self.remove_link_animations();
        self.apply_current_movement(false, true);
    }

    /// Reset to the default state: fresh raw and interpreted states, the
    /// motion table reset, a ready motion queued (behind any already
    /// queued), initialised, then [`MotionInterp::leave_ground`].
    fn enter_default_state(&mut self) {
        {
            let interp = self.interp_mut();
            interp.raw_state = RawMotionState::default();
            interp.interpreted_state = InterpretedMotionState::default();
        }
        self.initialize_motion_tables();
        let interp = self.interp_mut();
        interp.add_to_queue(0, READY, 0);
        interp.initted = true;
        self.leave_ground();
    }

    /// The run key went down or up: on a change, record it and re-apply
    /// the movement (`interrupt` cancels a move-to).
    fn set_hold_run(&mut self, holding_run: bool, interrupt: bool) {
        let run_key_up = !holding_run;
        let not_currently_run = self.interp().raw_state.current_hold_key != HoldKey::Run;
        if run_key_up != not_currently_run {
            self.interp_mut().raw_state.current_hold_key = if holding_run {
                HoldKey::Run
            } else {
                HoldKey::None
            };
            self.apply_current_movement(interrupt, true);
        }
    }

    /// Set the hold key from a request: none only replaces run, run replaces
    /// anything else; a change re-applies the movement.
    fn set_hold_key(&mut self, key: HoldKey, cancel_move_to: bool) {
        let current = self.interp().raw_state.current_hold_key;
        if key == current {
            return;
        }
        if key == HoldKey::None {
            if current == HoldKey::Run {
                self.interp_mut().raw_state.current_hold_key = HoldKey::None;
                self.apply_current_movement(cancel_move_to, true);
            }
        } else if key == HoldKey::Run && current != HoldKey::Run {
            self.interp_mut().raw_state.current_hold_key = HoldKey::Run;
            self.apply_current_movement(cancel_move_to, true);
        }
    }

    /// Run speed at the run rate, metres per second (rate 1 without a
    /// weenie; the last seen rate when the weenie cannot say).
    fn get_max_speed(&self) -> f32 {
        let rate = match self.weenie_obj() {
            None => 1.0,
            Some(w) => w.inq_run_rate().unwrap_or(self.interp().my_run_rate),
        };
        RUN_ANIM_SPEED * rate
    }

    /// While running, the run speed at the forward speed; otherwise the bare
    /// run rate (1 without a weenie).
    fn get_adjusted_max_speed(&self) -> f32 {
        let s = &self.interp().interpreted_state;
        if s.forward_command == RUN_FORWARD {
            return s.forward_speed * RUN_ANIM_SPEED;
        }
        match self.weenie_obj() {
            None => 1.0,
            Some(w) => w.inq_run_rate().unwrap_or(self.interp().my_run_rate),
        }
    }

    /// Adopt the server's interpreted state: copy it, replay it into `sink`
    /// (cancelling a move-to; jumps allowed when the old forward motion
    /// allowed them), then replay the actions newer than the last stamp
    /// seen (the local player skips its own autonomous echoes). Returns 0
    /// without a body, else 1.
    fn move_to_interpreted_state(
        &mut self,
        ims: &InboundInterpretedState,
        mut sink: SinkArg<'_>,
    ) -> i32 {
        if self.physics_obj().is_none() {
            return 0;
        }
        self.interp_mut().raw_state.current_style = ims.current_style;
        let allow_jump =
            MotionInterpreter::motion_allows_jump(self.interp().interpreted_state.forward_command)
                .is_none();
        {
            let s = &mut self.interp_mut().interpreted_state;
            s.current_style = ims.current_style;
            s.forward_command = ims.forward_command;
            s.forward_speed = ims.forward_speed;
            s.side_step_command = ims.side_step_command;
            s.side_step_speed = ims.side_step_speed;
            s.turn_command = ims.turn_command;
            s.turn_speed = ims.turn_speed;
        }
        self.apply_interpreted_movement(ims.current_style, sink.reborrow(), true, allow_jump);

        if let Some(actions) = &ims.actions {
            for a in actions {
                let incoming = a.stamp & 0x7FFF;
                let stored = self.interp().server_action_stamp & 0x7FFF;
                let diff = (incoming - stored).abs();
                let newer = if diff <= 0x3FFF {
                    stored < incoming
                } else {
                    incoming < stored
                };
                if !newer {
                    continue;
                }
                if self.interp().is_local_player && a.autonomous {
                    continue;
                }
                self.interp_mut().server_action_stamp = incoming;
                let p = MovementParameters {
                    speed: a.speed,
                    autonomous: a.autonomous,
                    ..MovementParameters::default()
                };
                self.do_interpreted_motion_into(a.command, &p, sink.reborrow());
            }
        }
        1
    }

    /// Replay the interpreted state into `sink`: the stance; falling when
    /// the forward motion cannot start off the ground, ready (no sidestep)
    /// while a standing jump charges, else the forward and sidestep axes;
    /// then the turn axis. Nothing here changes the interpreted state.
    fn apply_interpreted_movement(
        &mut self,
        current_style: u32,
        mut sink: SinkArg<'_>,
        cancel_move_to: bool,
        allow_jump: bool,
    ) {
        if self.physics_obj().is_none() {
            return;
        }
        let mut p = MovementParameters {
            set_hold_key: false,
            modify_interpreted_state: false,
            cancel_move_to,
            disable_jump_during_link: !allow_jump,
            ..MovementParameters::default()
        };
        {
            let interp = self.interp_mut();
            if interp.interpreted_state.forward_command == RUN_FORWARD {
                interp.my_run_rate = interp.interpreted_state.forward_speed;
            }
        }
        p.speed = 1.0;
        self.do_interpreted_motion_into(current_style, &p, sink.reborrow());

        let forward = self.interp().interpreted_state.forward_command;
        if !self.contact_allows_move(forward) {
            p.speed = 1.0;
            self.do_interpreted_motion_into(FALLING, &p, sink.reborrow());
        } else if self.interp().standing_long_jump {
            p.speed = 1.0;
            self.do_interpreted_motion_into(READY, &p, sink.reborrow());
            self.stop_interpreted_motion_into(SIDE_STEP_RIGHT, &p, sink.reborrow());
        } else {
            let (forward, forward_speed) = {
                let s = &self.interp().interpreted_state;
                (s.forward_command, s.forward_speed)
            };
            p.speed = forward_speed;
            self.do_interpreted_motion_into(forward, &p, sink.reborrow());
            let (side, side_speed) = {
                let s = &self.interp().interpreted_state;
                (s.side_step_command, s.side_step_speed)
            };
            if side == 0 {
                self.stop_interpreted_motion_into(SIDE_STEP_RIGHT, &p, sink.reborrow());
            } else {
                p.speed = side_speed;
                self.do_interpreted_motion_into(side, &p, sink.reborrow());
            }
        }

        let (turn, turn_speed) = {
            let s = &self.interp().interpreted_state;
            (s.turn_command, s.turn_speed)
        };
        if turn != 0 {
            p.speed = turn_speed;
            self.do_interpreted_motion_into(turn, &p, sink.reborrow());
            return;
        }
        self.stop_interpreted_motion_into(TURN_RIGHT, &p, sink);
    }

    /// Start an interpreted motion through the default sink.
    fn do_interpreted_motion(&mut self, motion: u32, p: &MovementParameters) -> WeenieError {
        self.do_interpreted_motion_into(motion, p, SinkArg::Default)
    }

    /// Start an interpreted motion: when contact allows it, animate it (a
    /// standing jump charging only records walk, run and sidestep), queue it
    /// with its jump code and record it; without contact, looping motions
    /// are only recorded and actions fail as not grounded. Out of the world,
    /// the link animations go at once.
    fn do_interpreted_motion_into(
        &mut self,
        motion: u32,
        p: &MovementParameters,
        sink: SinkArg<'_>,
    ) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let result;
        if self.contact_allows_move(motion) {
            if self.interp().standing_long_jump && is_walk_run_or_sidestep(motion) {
                if p.modify_interpreted_state {
                    self.interp_mut().interpreted_state.apply_motion(motion, p);
                }
                return WeenieError::NONE;
            }
            if motion == DEAD {
                self.remove_link_animations();
            }
            if sink_apply(self, sink, motion, p.speed) {
                let jump_err = if p.disable_jump_during_link {
                    WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
                } else {
                    let e = MotionInterpreter::motion_allows_jump(motion);
                    if e.is_none() && motion & ACTION_BIT == 0 {
                        MotionInterpreter::motion_allows_jump(
                            self.interp().interpreted_state.forward_command,
                        )
                    } else {
                        e
                    }
                };
                self.interp_mut()
                    .add_to_queue(p.context_id, motion, jump_err.code());
                if p.modify_interpreted_state {
                    self.interp_mut().interpreted_state.apply_motion(motion, p);
                }
                result = WeenieError::NONE;
            } else {
                result = WeenieError::GENERAL_MOVEMENT_FAILURE;
            }
        } else if motion & ACTION_BIT == 0 {
            if p.modify_interpreted_state {
                self.interp_mut().interpreted_state.apply_motion(motion, p);
            }
            result = WeenieError::NONE;
        } else {
            result = WeenieError::NOT_GROUNDED;
        }
        if self.physics_obj().is_some_and(|b| !b.in_world) {
            self.remove_link_animations();
        }
        result
    }

    /// Stop an interpreted motion through the default sink.
    fn stop_interpreted_motion(&mut self, motion: u32, p: &MovementParameters) -> WeenieError {
        self.stop_interpreted_motion_into(motion, p, SinkArg::Default)
    }

    /// Stop an interpreted motion: without contact (or for walk, run and
    /// sidestep while a standing jump charges) only the state changes;
    /// otherwise stop the animation, queue a ready motion and clear the
    /// state. Out of the world, the link animations go at once.
    fn stop_interpreted_motion_into(
        &mut self,
        motion: u32,
        p: &MovementParameters,
        sink: SinkArg<'_>,
    ) -> WeenieError {
        if self.physics_obj().is_none() {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let result;
        let state_only = self.interp().standing_long_jump && is_walk_run_or_sidestep(motion);
        if !self.contact_allows_move(motion) || state_only {
            if p.modify_interpreted_state {
                self.interp_mut().interpreted_state.remove_motion(motion);
            }
            result = WeenieError::NONE;
        } else if sink_stop(self, sink, motion) {
            result = WeenieError::NONE;
            self.interp_mut()
                .add_to_queue(p.context_id, READY, result.code());
            if p.modify_interpreted_state {
                self.interp_mut().interpreted_state.remove_motion(motion);
            }
        } else {
            result = WeenieError::GENERAL_MOVEMENT_FAILURE;
        }
        if self.physics_obj().is_some_and(|b| !b.in_world) {
            self.remove_link_animations();
        }
        result
    }
}

impl<T: InterpreterHost + ?Sized> MotionInterp for T {}

/// An interpreter with its own body, weenie, sink and optional callbacks:
/// the shape of OpenAC's free-standing `MotionInterpreter`, for objects that
/// have no movement managers and for tests.
#[derive(Default)]
pub struct StandaloneInterpreter {
    /// The interpreter state.
    pub interp: MotionInterpreter,
    /// The body.
    pub physics_obj: Option<PhysicsBody>,
    /// The weenie.
    pub weenie_obj: Option<Box<dyn WeenieObject>>,
    /// The default sink.
    pub default_sink: Option<Box<dyn InterpretedMotionSink>>,
    /// Release-sticky callback.
    pub unstick_from_object: Option<Box<dyn FnMut()>>,
    /// Cancel-move-to callback.
    pub interrupt_current_movement: Option<Box<dyn FnMut()>>,
    /// Drop-link-animations callback.
    pub remove_link_animations: Option<Box<dyn FnMut()>>,
    /// Reset-motion-table callback.
    pub initialize_motion_tables: Option<Box<dyn FnMut()>>,
    /// Completed-motions callback.
    pub check_for_completed_motions: Option<Box<dyn FnMut()>>,
    /// The playing cycle's velocity.
    pub get_cycle_velocity: Option<Box<dyn FnMut() -> Vec3>>,
}

impl std::fmt::Debug for StandaloneInterpreter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StandaloneInterpreter")
            .field("interp", &self.interp)
            .field("physics_obj", &self.physics_obj)
            .finish_non_exhaustive()
    }
}

impl StandaloneInterpreter {
    /// An interpreter for `body` with an optional weenie.
    #[must_use]
    pub fn new(body: PhysicsBody, weenie: Option<Box<dyn WeenieObject>>) -> Self {
        Self {
            physics_obj: Some(body),
            weenie_obj: weenie,
            ..Self::default()
        }
    }

    /// Bind a weenie and, when bound to a body and initialised, re-apply the
    /// movement (cancelling a move-to).
    pub fn set_weenie_object(&mut self, weenie: Option<Box<dyn WeenieObject>>) {
        self.weenie_obj = weenie;
        self.apply_current_movement(true, true);
    }

    /// Bind a body and, when initialised, re-apply the movement (cancelling
    /// a move-to).
    pub fn set_physics_object(&mut self, body: Option<PhysicsBody>) {
        self.physics_obj = body;
        self.apply_current_movement(true, true);
    }
}

impl InterpreterHost for StandaloneInterpreter {
    fn interp(&self) -> &MotionInterpreter {
        &self.interp
    }
    fn interp_mut(&mut self) -> &mut MotionInterpreter {
        &mut self.interp
    }
    fn physics_obj(&self) -> Option<&PhysicsBody> {
        self.physics_obj.as_ref()
    }
    fn physics_obj_mut(&mut self) -> Option<&mut PhysicsBody> {
        self.physics_obj.as_mut()
    }
    fn weenie_obj(&self) -> Option<&dyn WeenieObject> {
        self.weenie_obj.as_deref()
    }
    fn unstick_from_object(&mut self) {
        if let Some(f) = &mut self.unstick_from_object {
            f();
        }
    }
    fn interrupt_current_movement(&mut self) {
        if let Some(f) = &mut self.interrupt_current_movement {
            f();
        }
    }
    fn remove_link_animations(&mut self) {
        if let Some(f) = &mut self.remove_link_animations {
            f();
        }
    }
    fn initialize_motion_tables(&mut self) {
        if let Some(f) = &mut self.initialize_motion_tables {
            f();
        }
    }
    fn check_for_completed_motions(&mut self) {
        if let Some(f) = &mut self.check_for_completed_motions {
            f();
        }
    }
    fn cycle_velocity(&mut self) -> Option<Vec3> {
        self.get_cycle_velocity.as_mut().map(|f| f())
    }
    fn default_sink(&mut self) -> Option<&mut dyn InterpretedMotionSink> {
        self.default_sink.as_deref_mut().map(|s| s as _)
    }
}

/// A finished motion reported by the motion table reaches the interpreter.
impl ac_motion::MotionDoneSink for StandaloneInterpreter {
    fn motion_done(&mut self, motion: u32, success: bool) {
        MotionInterp::motion_done(self, motion, success);
    }
}
