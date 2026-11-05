//! The move-to manager: walks or runs an object to a position or another
//! object, or turns it to a heading or to face an object, as a queue of
//! turn and move steps driven once per update. Object moves wait for the
//! target's first report before planning.

use std::collections::VecDeque;

use glam::Vec3;

use ac_motion::motion_command::{TURN_LEFT, TURN_RIGHT};
use ac_motion::move_to_math::{
    EPSILON, cylinder_distance, get_heading, heading_diff, heading_greater, position_heading,
    set_heading,
};
use ac_motion::{MovementParameters, MovementType};

use crate::body::CellPosition;
use crate::movement::interp::{InterpreterHost, MotionInterp, MovementStruct};
use crate::movement::target::{TargetInfo, TargetStatus};
use crate::movement::weenie_error::WeenieError;

/// One queued step.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MoveToNode {
    /// Turn to heading or move to the position.
    pub kind: MovementType,
    /// Heading for a turn, degrees.
    pub heading: f32,
}

/// Move-to state.
#[derive(Debug, Clone, PartialEq)]
pub struct MoveToManager {
    /// The request in progress (invalid when idle).
    pub movement_type_state: MovementType,
    /// Where (or which heading) the move seeks.
    pub sought_position: CellPosition,
    /// The target's current position.
    pub current_target_position: CellPosition,
    /// Where the move started.
    pub starting_position: CellPosition,
    /// The request's options.
    pub params: MovementParameters,
    /// Last heading difference (turns), degrees.
    pub previous_heading: f32,
    /// Distance at the last progress checkpoint, metres.
    pub previous_distance: f32,
    /// Time of that checkpoint, seconds.
    pub previous_distance_time: f64,
    /// Distance when the move started, metres.
    pub original_distance: f32,
    /// Time it started, seconds.
    pub original_distance_time: f64,
    /// Updates without progress.
    pub fail_progress_count: u32,
    /// Object moved to.
    pub sought_object_id: u32,
    /// Its top-level object.
    pub top_level_object_id: u32,
    /// Its radius, metres.
    pub sought_object_radius: f32,
    /// Its height, metres.
    pub sought_object_height: f32,
    /// The motion moving or turning (raw form).
    pub current_command: u32,
    /// A steering turn while moving.
    pub aux_command: u32,
    /// Backing away from the target.
    pub moving_away: bool,
    /// An object move has its plan.
    pub initialized: bool,
    /// The object has a body.
    pub has_physics_obj: bool,
    pending_actions: VecDeque<MoveToNode>,
}

impl Default for MoveToManager {
    fn default() -> Self {
        let mut m = Self {
            movement_type_state: MovementType::Invalid,
            sought_position: identity_position(),
            current_target_position: identity_position(),
            starting_position: identity_position(),
            params: MovementParameters::default(),
            previous_heading: 0.0,
            previous_distance: f32::MAX,
            previous_distance_time: 0.0,
            original_distance: f32::MAX,
            original_distance_time: 0.0,
            fail_progress_count: 0,
            sought_object_id: 0,
            top_level_object_id: 0,
            sought_object_radius: 0.0,
            sought_object_height: 0.0,
            current_command: 0,
            aux_command: 0,
            moving_away: false,
            initialized: false,
            has_physics_obj: true,
            pending_actions: VecDeque::new(),
        };
        clear_param_bits(&mut m.params);
        m
    }
}

fn identity_position() -> CellPosition {
    CellPosition::new(0, Vec3::ZERO, glam::Quat::IDENTITY)
}

fn clear_param_bits(p: &mut MovementParameters) {
    p.can_walk = false;
    p.can_run = false;
    p.can_sidestep = false;
    p.can_walk_backwards = false;
    p.can_charge = false;
    p.fail_walk = false;
    p.use_final_heading = false;
    p.sticky = false;
    p.move_away = false;
    p.move_towards = false;
    p.use_spheres = false;
    p.set_hold_key = false;
    p.autonomous = false;
    p.modify_raw_state = false;
    p.modify_interpreted_state = false;
    p.cancel_move_to = false;
    p.stop_completely = false;
    p.disable_jump_during_link = false;
    p.context_id = 0;
}

impl MoveToManager {
    /// An idle manager (the clock starts at `now`).
    #[must_use]
    pub fn new(now: f64) -> Self {
        Self {
            previous_distance_time: now,
            original_distance_time: now,
            ..Self::default()
        }
    }

    /// The queued steps, oldest first.
    pub fn pending_actions(&self) -> impl ExactSizeIterator<Item = &MoveToNode> {
        self.pending_actions.iter()
    }

    /// A move or turn is in progress.
    #[must_use]
    pub fn is_moving_to(&self) -> bool {
        self.movement_type_state != MovementType::Invalid
    }

    /// Queue a turn to `heading`.
    pub fn add_turn_to_heading_node(&mut self, heading: f32) {
        self.pending_actions.push_back(MoveToNode {
            kind: MovementType::TurnToHeading,
            heading,
        });
    }

    /// Queue a move to the target position.
    pub fn add_move_to_position_node(&mut self) {
        self.pending_actions.push_back(MoveToNode {
            kind: MovementType::MoveToPosition,
            heading: 0.0,
        });
    }

    /// Drop the oldest step.
    pub fn remove_pending_actions_head(&mut self) {
        self.pending_actions.pop_front();
    }

    fn reset_locals(&mut self, now: f64) {
        self.movement_type_state = MovementType::Invalid;
        clear_param_bits(&mut self.params);
        self.previous_distance = f32::MAX;
        self.previous_distance_time = now;
        self.original_distance = f32::MAX;
        self.original_distance_time = now;
        self.previous_heading = 0.0;
        self.fail_progress_count = 0;
        self.current_command = 0;
        self.aux_command = 0;
        self.moving_away = false;
        self.initialized = false;
        self.sought_position = identity_position();
        self.current_target_position = identity_position();
        self.sought_object_id = 0;
        self.top_level_object_id = 0;
        self.sought_object_radius = 0.0;
        self.sought_object_height = 0.0;
    }

    fn copy_params(&mut self, p: &MovementParameters) {
        self.params = *p;
    }
}

/// What the move-to manager reaches through its object, beyond the
/// interpreter.
pub trait MoveToHost: InterpreterHost {
    /// The move-to state.
    fn move_to(&mut self) -> &mut MoveToManager;
    /// The move-to state, read-only.
    fn move_to_ref(&self) -> &MoveToManager;
    /// Stop everything (through the object's movement request path).
    fn move_to_stop_completely(&mut self);
    /// Where the object is (cell, world origin, orientation).
    fn move_to_position(&self) -> CellPosition;
    /// Its heading, degrees.
    fn move_to_heading(&self) -> f32;
    /// Set its heading, degrees (`send`: tell the server).
    fn move_to_set_heading(&mut self, heading: f32, send: bool);
    /// Its radius, metres.
    fn move_to_own_radius(&self) -> f32;
    /// Its height, metres.
    fn move_to_own_height(&self) -> f32;
    /// Standing on something (moves wait for it).
    fn move_to_contact(&self) -> bool;
    /// Catching up to server positions.
    fn move_to_is_interpolating(&self) -> bool;
    /// Its velocity.
    fn move_to_velocity(&self) -> Vec3;
    /// Its id.
    fn move_to_self_id(&self) -> u32;
    /// Watch an object.
    fn move_to_set_target(&mut self, context_id: u32, object_id: u32, radius: f32, quantum: f64);
    /// Stop watching.
    fn move_to_clear_target(&mut self);
    /// The watch look-ahead, seconds.
    fn move_to_target_quantum(&self) -> f64;
    /// Change the watch look-ahead.
    fn move_to_set_target_quantum(&mut self, quantum: f64);
    /// Game time, seconds.
    fn move_to_cur_time(&mut self) -> f64;
    /// Release a sticky target.
    fn move_to_unstick(&mut self) {}
    /// Stick to the target on arrival.
    fn move_to_stick_to(&mut self, _object_id: u32, _radius: f32, _height: f32) {}
    /// The move finished (with an error code, none on arrival).
    fn move_to_complete(&mut self, _error: WeenieError) {}
    /// The move was cancelled.
    fn move_to_cancelled(&mut self, _error: WeenieError) {}
}

fn heading_to(h: &(impl MoveToHost + ?Sized), target: Vec3) -> (f32, f32) {
    let to_target = position_heading(h.move_to_position().frame.origin, target);
    let mut diff = to_target - h.move_to_heading();
    if diff.abs() < EPSILON {
        diff = 0.0;
    }
    if diff < -EPSILON {
        diff += 360.0;
    }
    (to_target, diff)
}

/// Move-to operations, on any [`MoveToHost`].
pub trait MoveToOps: MoveToHost {
    /// Reset to idle (the progress clocks restart now).
    fn move_to_initialize_local_variables(&mut self) {
        let now = self.move_to_cur_time();
        let now2 = self.move_to_cur_time();
        let mt = self.move_to();
        mt.reset_locals(now);
        mt.original_distance_time = now2;
    }

    /// Drop the queue and reset.
    fn move_to_destroy(&mut self) {
        self.move_to().pending_actions.clear();
        self.move_to_initialize_local_variables();
    }

    /// Start a request: cancel the one in progress, release a sticky
    /// target, then dispatch by kind.
    fn move_to_perform_movement(&mut self, mvs: &MovementStruct) -> WeenieError {
        self.cancel_move_to(WeenieError::ACTION_CANCELLED);
        self.move_to_unstick();
        let p = mvs.params.unwrap_or_default();
        match mvs.kind {
            MovementType::MoveToObject => {
                self.move_to_object(mvs.object_id, mvs.top_level_id, mvs.radius, mvs.height, &p);
            }
            MovementType::MoveToPosition => self.move_to_position_request(mvs.pos, &p),
            MovementType::TurnToObject => self.turn_to_object(mvs.object_id, mvs.top_level_id, &p),
            MovementType::TurnToHeading => self.turn_to_heading(&p),
            _ => {}
        }
        WeenieError::NONE
    }

    /// Move to an object: stop, remember it, and watch it (planning waits
    /// for its first report). Moving to oneself ends at once.
    fn move_to_object(
        &mut self,
        object_id: u32,
        top_level_id: u32,
        radius: f32,
        height: f32,
        p: &MovementParameters,
    ) {
        if !self.move_to_ref().has_physics_obj {
            return;
        }
        self.move_to_stop_completely();
        let start = self.move_to_position();
        {
            let mt = self.move_to();
            mt.starting_position = start;
            mt.sought_object_id = object_id;
            mt.sought_object_radius = radius;
            mt.sought_object_height = height;
            mt.movement_type_state = MovementType::MoveToObject;
            mt.top_level_object_id = top_level_id;
            mt.copy_params(p);
            mt.initialized = false;
        }
        if top_level_id == self.move_to_self_id() {
            self.move_to_clean_up();
            self.move_to_stop_completely();
        } else {
            let tl = self.move_to_ref().top_level_object_id;
            self.move_to_set_target(0, tl, 0.5, 0.0);
        }
    }

    /// Move to a position: stop, plan (turn to face it and move, when not
    /// already there; then the final heading when asked), and begin.
    fn move_to_position_request(&mut self, target: CellPosition, p: &MovementParameters) {
        if !self.move_to_ref().has_physics_obj {
            return;
        }
        self.move_to_stop_completely();
        {
            let mt = self.move_to();
            mt.current_target_position = target;
            mt.sought_object_radius = 0.0;
        }
        let dist = self.get_current_distance();
        let (to_target, diff) = heading_to(self, target.frame.origin);
        let (cmd, _, _) = p.get_command(dist, diff);
        let start = self.move_to_position();
        let mt = self.move_to();
        if cmd != 0 {
            mt.add_turn_to_heading_node(to_target);
            mt.add_move_to_position_node();
        }
        if p.use_final_heading {
            mt.add_turn_to_heading_node(p.desired_heading);
        }
        mt.sought_position = target;
        mt.starting_position = start;
        mt.movement_type_state = MovementType::MoveToPosition;
        mt.copy_params(p);
        mt.params.sticky = false;
        self.begin_next_node();
    }

    /// Turn to face an object (planning waits for its first report).
    fn turn_to_object(&mut self, object_id: u32, top_level_id: u32, p: &MovementParameters) {
        if !self.move_to_ref().has_physics_obj {
            return;
        }
        if p.stop_completely {
            self.move_to_stop_completely();
        }
        {
            let mt = self.move_to();
            mt.movement_type_state = MovementType::TurnToObject;
            mt.sought_object_id = object_id;
            mt.current_target_position.frame.orientation = set_heading(p.desired_heading);
            mt.top_level_object_id = top_level_id;
            mt.copy_params(p);
        }
        if top_level_id == self.move_to_self_id() {
            self.move_to_clean_up();
            self.move_to_stop_completely();
        } else {
            self.move_to().initialized = false;
            self.move_to_set_target(0, top_level_id, 0.5, 0.0);
        }
    }

    /// Turn to `p.desired_heading`.
    fn turn_to_heading(&mut self, p: &MovementParameters) {
        if !self.move_to_ref().has_physics_obj {
            return;
        }
        if p.stop_completely {
            self.move_to_stop_completely();
        }
        let mt = self.move_to();
        mt.copy_params(p);
        mt.params.sticky = false;
        mt.sought_position.frame.orientation = set_heading(p.desired_heading);
        mt.movement_type_state = MovementType::TurnToHeading;
        mt.pending_actions.push_back(MoveToNode {
            kind: MovementType::TurnToHeading,
            heading: p.desired_heading,
        });
        self.begin_next_node();
    }

    /// Start the next step; with none left the move is complete (sticking
    /// to the target first when asked).
    fn begin_next_node(&mut self) {
        if let Some(head) = self.move_to_ref().pending_actions.front().copied() {
            match head.kind {
                MovementType::MoveToPosition => self.begin_move_forward(),
                MovementType::TurnToHeading => self.begin_turn_to_heading(),
                _ => {}
            }
            return;
        }
        if self.move_to_ref().params.sticky {
            let (height, radius, tlid) = {
                let mt = self.move_to_ref();
                (
                    mt.sought_object_height,
                    mt.sought_object_radius,
                    mt.top_level_object_id,
                )
            };
            self.move_to_clean_up();
            if self.move_to_ref().has_physics_obj {
                self.move_to_stop_completely();
            }
            self.move_to_stick_to(tlid, radius, height);
            self.move_to_complete(WeenieError::NONE);
            return;
        }
        self.move_to_clean_up();
        if self.move_to_ref().has_physics_obj {
            self.move_to_stop_completely();
        }
        self.move_to_complete(WeenieError::NONE);
    }

    /// Start moving toward (or away from) the target: pick the motion and
    /// walk/run key for the distance; none needed ends the step.
    fn begin_move_forward(&mut self) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        let dist = self.get_current_distance();
        let target = self.move_to_ref().current_target_position.frame.origin;
        let (_, heading) = heading_to(self, target);
        let (cmd, hold_key, moving_away) = self.move_to_ref().params.get_command(dist, heading);
        if cmd == 0 {
            self.move_to().remove_pending_actions_head();
            self.begin_next_node();
            return;
        }
        let local = MovementParameters {
            hold_key_to_apply: hold_key,
            cancel_move_to: false,
            speed: self.move_to_ref().params.speed,
            ..MovementParameters::default()
        };
        let err = self.move_to_do_motion(cmd, local);
        if !err.is_none() {
            self.cancel_move_to(err);
            return;
        }
        let now = self.move_to_cur_time();
        let now2 = self.move_to_cur_time();
        let mt = self.move_to();
        mt.current_command = cmd;
        mt.moving_away = moving_away;
        mt.params.hold_key_to_apply = hold_key;
        mt.previous_distance = dist;
        mt.previous_distance_time = now;
        mt.original_distance = dist;
        mt.original_distance_time = now2;
    }

    /// Start turning toward the head step's heading (waiting while motions
    /// are pending); already there ends the step.
    fn begin_turn_to_heading(&mut self) {
        let head = self.move_to_ref().pending_actions.front().copied();
        let Some(head) = head.filter(|_| self.move_to_ref().has_physics_obj) else {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        };
        if self.interp().motions_pending() {
            return;
        }
        let diff = heading_diff(head.heading, self.move_to_heading(), TURN_RIGHT);
        let turn = if diff > 180.0 {
            if diff + EPSILON >= 360.0 {
                self.move_to().remove_pending_actions_head();
                self.begin_next_node();
                return;
            }
            TURN_LEFT
        } else {
            if diff <= EPSILON {
                self.move_to().remove_pending_actions_head();
                self.begin_next_node();
                return;
            }
            TURN_RIGHT
        };
        let local = MovementParameters {
            cancel_move_to: false,
            speed: self.move_to_ref().params.speed,
            hold_key_to_apply: self.move_to_ref().params.hold_key_to_apply,
            ..MovementParameters::default()
        };
        let err = self.move_to_do_motion(turn, local);
        if !err.is_none() {
            self.cancel_move_to(err);
            return;
        }
        let mt = self.move_to();
        mt.current_command = turn;
        mt.previous_heading = diff;
    }

    /// Distance to the target: between the cylinders when the request uses
    /// spheres, else between centres.
    fn get_current_distance(&self) -> f32 {
        let mt = self.move_to_ref();
        if !mt.has_physics_obj {
            return 0.0;
        }
        let me = self.move_to_position().frame.origin;
        let target = mt.current_target_position.frame.origin;
        if !mt.params.use_spheres {
            return me.distance(target);
        }
        cylinder_distance(
            self.move_to_own_radius(),
            self.move_to_own_height(),
            me,
            mt.sought_object_radius,
            mt.sought_object_height,
            target,
        )
    }

    /// Whether the move progressed: always within a second of the last
    /// checkpoint; after that, at 0.25 m/s since the checkpoint (which then
    /// advances) and overall.
    fn check_progress_made(&mut self, current_distance: f32) -> bool {
        let now = self.move_to_cur_time();
        let elapsed = now - self.move_to_ref().previous_distance_time;
        if elapsed <= 1.0 {
            return true;
        }
        let (moving_away, prev, orig) = {
            let mt = self.move_to_ref();
            (mt.moving_away, mt.previous_distance, mt.original_distance)
        };
        let progress = if moving_away {
            current_distance - prev
        } else {
            prev - current_distance
        };
        if progress / elapsed as f32 >= 0.25 {
            let now = self.move_to_cur_time();
            {
                let mt = self.move_to();
                mt.previous_distance = current_distance;
                mt.previous_distance_time = now;
            }
            let total = if moving_away {
                current_distance - orig
            } else {
                orig - current_distance
            };
            let now = self.move_to_cur_time();
            let total_rate = total / (now - self.move_to_ref().original_distance_time) as f32;
            if total_rate >= 0.25 {
                return true;
            }
        }
        false
    }

    /// Per-update driver (on the ground, with a step queued, an object move
    /// planned): run the head step.
    fn move_to_use_time(&mut self) {
        if !self.move_to_ref().has_physics_obj || !self.move_to_contact() {
            return;
        }
        let Some(head) = self.move_to_ref().pending_actions.front().copied() else {
            return;
        };
        let mt = self.move_to_ref();
        let gate = mt.top_level_object_id == 0
            || mt.movement_type_state == MovementType::Invalid
            || mt.initialized;
        if !gate {
            return;
        }
        match head.kind {
            MovementType::MoveToPosition => self.handle_move_to_position(),
            MovementType::TurnToHeading => self.handle_turn_to_heading(),
            _ => {}
        }
    }

    /// Moving: steer with a turn when more than 20° off course (none while
    /// motions are pending), check progress, arrive (next step) or give up
    /// past the fail distance, and keep the target's look-ahead at the time
    /// to reach it.
    fn handle_move_to_position(&mut self) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        let cur_pos = self.move_to_position().frame.origin;
        let local = MovementParameters {
            speed: self.move_to_ref().params.speed,
            cancel_move_to: false,
            hold_key_to_apply: self.move_to_ref().params.hold_key_to_apply,
            ..MovementParameters::default()
        };
        if self.interp().motions_pending() {
            let aux = self.move_to_ref().aux_command;
            if aux != 0 {
                self.move_to_stop_motion(aux, local);
                self.move_to().aux_command = 0;
            }
        } else {
            let (target, current, moving_away) = {
                let mt = self.move_to_ref();
                (
                    mt.current_target_position.frame.origin,
                    mt.current_command,
                    mt.moving_away,
                )
            };
            let to_target = position_heading(cur_pos, target);
            let mut heading = self
                .move_to_ref()
                .params
                .get_desired_heading(current, moving_away)
                + to_target;
            if heading >= 360.0 {
                heading -= 360.0;
            }
            let mut diff = heading - self.move_to_heading();
            if diff.abs() < EPSILON {
                diff = 0.0;
            }
            if diff < -EPSILON {
                diff += 360.0;
            }
            if diff <= 20.0 || diff >= 340.0 {
                let aux = self.move_to_ref().aux_command;
                if aux != 0 {
                    self.move_to_stop_motion(aux, local);
                    self.move_to().aux_command = 0;
                }
            } else {
                let turn = if diff >= 180.0 { TURN_LEFT } else { TURN_RIGHT };
                if turn != self.move_to_ref().aux_command {
                    self.move_to_do_motion(turn, local);
                    self.move_to().aux_command = turn;
                }
            }
        }

        let dist = self.get_current_distance();
        if !self.check_progress_made(dist) {
            if !self.move_to_is_interpolating() && !self.interp().motions_pending() {
                self.move_to().fail_progress_count += 1;
            }
        } else {
            self.move_to().fail_progress_count = 0;
            let (moving_away, min, dto) = {
                let mt = self.move_to_ref();
                (
                    mt.moving_away,
                    mt.params.min_distance,
                    mt.params.distance_to_object,
                )
            };
            let arrived = if moving_away {
                dist >= min
            } else {
                dist <= dto
            };
            if arrived {
                self.move_to().remove_pending_actions_head();
                let current = self.move_to_ref().current_command;
                self.move_to_stop_motion(current, local);
                self.move_to().current_command = 0;
                let aux = self.move_to_ref().aux_command;
                if aux != 0 {
                    self.move_to_stop_motion(aux, local);
                    self.move_to().aux_command = 0;
                }
                self.begin_next_node();
            } else {
                let start = self.move_to_ref().starting_position.frame.origin;
                let start_dist = start.distance(self.move_to_position().frame.origin);
                if start_dist > self.move_to_ref().params.fail_distance {
                    self.cancel_move_to(WeenieError::YOU_CHARGED_TOO_FAR);
                }
            }
        }

        let mt = self.move_to_ref();
        if mt.top_level_object_id != 0 && mt.movement_type_state != MovementType::Invalid {
            let speed = self.move_to_velocity().length();
            if f64::from(speed) > 0.1 {
                let eta = dist / speed;
                if (eta - self.move_to_target_quantum() as f32).abs() >= 1.0 {
                    self.move_to_set_target_quantum(f64::from(eta));
                }
            }
        }
    }

    /// Turning: past the head step's heading, snap to it exactly and go on;
    /// otherwise count the update as progress or not.
    fn handle_turn_to_heading(&mut self) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        let cmd = self.move_to_ref().current_command;
        if cmd != TURN_LEFT && cmd != TURN_RIGHT {
            self.begin_turn_to_heading();
            return;
        }
        let Some(head) = self.move_to_ref().pending_actions.front().copied() else {
            return;
        };
        let heading = self.move_to_heading();
        if heading_greater(heading, head.heading, cmd) {
            self.move_to().fail_progress_count = 0;
            self.move_to_set_heading(head.heading, true);
            self.move_to().remove_pending_actions_head();
            let local = MovementParameters {
                cancel_move_to: false,
                hold_key_to_apply: self.move_to_ref().params.hold_key_to_apply,
                ..MovementParameters::default()
            };
            let current = self.move_to_ref().current_command;
            self.move_to_stop_motion(current, local);
            self.move_to().current_command = 0;
            self.begin_next_node();
            return;
        }
        let diff = heading_diff(heading, self.move_to_ref().previous_heading, cmd);
        if diff < 180.0 && diff > EPSILON {
            let mt = self.move_to();
            mt.fail_progress_count = 0;
            mt.previous_heading = heading;
            return;
        }
        self.move_to().previous_heading = heading;
        if !self.move_to_is_interpolating() && !self.interp().motions_pending() {
            self.move_to().fail_progress_count += 1;
        }
    }

    /// A report on the target: the first plans the move (or ends a move to
    /// oneself; a bad status cancels as no object); later ones retarget an
    /// object move (a bad status cancels as gone).
    fn move_to_handle_update_target(&mut self, info: TargetInfo) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        if self.move_to_ref().top_level_object_id != info.object_id {
            return;
        }
        let kind = self.move_to_ref().movement_type_state;
        if !self.move_to_ref().initialized {
            if self.move_to_ref().top_level_object_id == self.move_to_self_id() {
                let me = self.move_to_position();
                {
                    let mt = self.move_to();
                    mt.sought_position = me;
                    mt.current_target_position = me;
                }
                self.clean_up_and_call_weenie(WeenieError::NONE);
                return;
            }
            if info.status != TargetStatus::Ok {
                self.cancel_move_to(WeenieError::NO_OBJECT);
                return;
            }
            if kind == MovementType::MoveToObject {
                self.move_to_object_internal(info.target_position, info.interpolated_position);
            } else if kind == MovementType::TurnToObject {
                self.turn_to_object_internal(info.target_position);
            }
        } else {
            if info.status != TargetStatus::Ok {
                self.cancel_move_to(WeenieError::OBJECT_GONE);
                return;
            }
            if kind == MovementType::MoveToObject {
                let now = self.move_to_cur_time();
                let now2 = self.move_to_cur_time();
                let mt = self.move_to();
                mt.sought_position = info.interpolated_position;
                mt.current_target_position = info.target_position;
                mt.previous_distance = f32::MAX;
                mt.previous_distance_time = now;
                mt.original_distance = f32::MAX;
                mt.original_distance_time = now2;
            }
        }
    }

    /// Landing restarts the current step.
    fn move_to_hit_ground(&mut self) {
        if self.move_to_ref().movement_type_state == MovementType::Invalid {
            return;
        }
        self.begin_next_node();
    }

    /// Plan an object move from the target's first report: turn to face
    /// it and move when not already there, then the final heading relative
    /// to it.
    fn move_to_object_internal(&mut self, target: CellPosition, interpolated: CellPosition) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        {
            let mt = self.move_to();
            mt.sought_position = interpolated;
            mt.current_target_position = target;
        }
        let (i_heading, diff) = heading_to(self, interpolated.frame.origin);
        let dist = self.get_current_distance();
        let (cmd, _, _) = self.move_to_ref().params.get_command(dist, diff);
        let mt = self.move_to();
        if cmd != 0 {
            mt.add_turn_to_heading_node(i_heading);
            mt.add_move_to_position_node();
        }
        if mt.params.use_final_heading {
            let mut final_heading = i_heading + mt.params.desired_heading;
            if final_heading >= 360.0 {
                final_heading -= 360.0;
            }
            mt.add_turn_to_heading_node(final_heading);
        }
        mt.initialized = true;
        self.begin_next_node();
    }

    /// Plan a turn to face an object from its first report (plus the
    /// requested offset heading).
    fn turn_to_object_internal(&mut self, target: CellPosition) {
        if !self.move_to_ref().has_physics_obj {
            self.cancel_move_to(WeenieError::NO_PHYSICS_OBJECT);
            return;
        }
        self.move_to().current_target_position = target;
        let sought_heading = get_heading(self.move_to_ref().sought_position.frame.orientation);
        let target_heading =
            position_heading(self.move_to_position().frame.origin, target.frame.origin);
        let mut final_heading = (target_heading + sought_heading) % 360.0;
        if final_heading < 0.0 {
            final_heading += 360.0;
        }
        let mt = self.move_to();
        mt.sought_position.frame.orientation = set_heading(final_heading);
        mt.pending_actions.push_back(MoveToNode {
            kind: MovementType::TurnToHeading,
            heading: final_heading,
        });
        mt.initialized = true;
        self.begin_next_node();
    }

    #[doc(hidden)]
    fn move_to_do_motion(&mut self, motion: u32, mut p: MovementParameters) -> WeenieError {
        if !self.move_to_ref().has_physics_obj {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let mut motion = motion;
        let mut speed = p.speed;
        self.adjust_motion(&mut motion, &mut speed, p.hold_key_to_apply);
        p.speed = speed;
        self.do_interpreted_motion(motion, &p)
    }

    #[doc(hidden)]
    fn move_to_stop_motion(&mut self, motion: u32, mut p: MovementParameters) -> WeenieError {
        if !self.move_to_ref().has_physics_obj {
            return WeenieError::NO_PHYSICS_OBJECT;
        }
        let mut motion = motion;
        let mut speed = p.speed;
        self.adjust_motion(&mut motion, &mut speed, p.hold_key_to_apply);
        p.speed = speed;
        self.stop_interpreted_motion(motion, &p)
    }

    /// Cancel the move in progress (nothing when idle): drop the queue,
    /// clean up, stop, and report the cancellation.
    fn cancel_move_to(&mut self, error: WeenieError) {
        if self.move_to_ref().movement_type_state == MovementType::Invalid {
            return;
        }
        self.move_to().pending_actions.clear();
        self.move_to_clean_up();
        if self.move_to_ref().has_physics_obj {
            self.move_to_stop_completely();
        }
        self.move_to_cancelled(error);
    }

    /// Stop the move's motions, stop watching an object target, and reset.
    fn move_to_clean_up(&mut self) {
        let local = MovementParameters {
            hold_key_to_apply: self.move_to_ref().params.hold_key_to_apply,
            cancel_move_to: false,
            ..MovementParameters::default()
        };
        if self.move_to_ref().has_physics_obj {
            let current = self.move_to_ref().current_command;
            if current != 0 {
                self.move_to_stop_motion(current, local);
            }
            let aux = self.move_to_ref().aux_command;
            if aux != 0 {
                self.move_to_stop_motion(aux, local);
            }
            let mt = self.move_to_ref();
            if mt.top_level_object_id != 0 && mt.movement_type_state != MovementType::Invalid {
                self.move_to_clear_target();
            }
        }
        self.move_to_initialize_local_variables();
    }

    /// Clean up, stop and report completion with `error`.
    fn clean_up_and_call_weenie(&mut self, error: WeenieError) {
        self.move_to_clean_up();
        if self.move_to_ref().has_physics_obj {
            self.move_to_stop_completely();
        }
        self.move_to_complete(error);
    }
}

impl<T: MoveToHost + ?Sized> MoveToOps for T {}
