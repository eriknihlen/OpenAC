//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/MoveToManagerTestHarness.cs`:
//! a move-to manager over a grounded interpreter whose every callback is a
//! recorder, plus the movement-manager facade of `MovementManagerTests`
//! (a factory that hands out the harness's manager).

use ac_core::physics::TransientState;
use ac_motion::MovementType;
use glam::{Quat, Vec3};

use crate::body::{CellPosition, PhysicsBody};
use crate::movement::{
    InterpretedMotionSink, InterpreterHost, MotionInterp, MotionInterpreter, MoveToHost,
    MoveToManager, MoveToOps, MovementManagerHost, StandaloneInterpreter, WeenieError,
    WeenieObject,
};

pub struct MoveToHarness {
    pub solo: StandaloneInterpreter,
    pub manager: MoveToManager,
    pub world_position: CellPosition,
    pub heading: f32,
    pub set_heading_calls: Vec<(f32, bool)>,
    pub own_radius: f32,
    pub own_height: f32,
    pub contact_value: bool,
    pub is_interpolating_value: bool,
    pub velocity: Vec3,
    pub self_id: u32,
    pub stop_completely_calls: i32,
    pub set_target_calls: Vec<(u32, u32, f32, f64)>,
    pub clear_target_calls: i32,
    pub target_quantum: f64,
    pub set_target_quantum_calls: Vec<f64>,
    pub unstick_calls: i32,
    pub stick_to_calls: Vec<(u32, f32, f32)>,
    pub move_to_complete_calls: Vec<WeenieError>,
    /// `MoveToCancelled` is wired (OpenAC leaves it null by default).
    pub move_to_cancelled_calls: Option<Vec<WeenieError>>,
    pub cur_time: f64,
    /// The stop-completely callback re-enters `cancel_move_to`.
    pub reenter_cancel_on_stop: bool,
    pub reentrant_cancel_calls: i32,
    /// The movement type seen by the last stop-completely callback.
    pub state_during_stop: Option<MovementType>,
    /// Record whether the interpreter's queue was empty at the first
    /// link-animation drop.
    pub record_queue_on_remove_link: bool,
    pub queue_empty_at_remove_link: Option<bool>,
    pub remove_link_calls: i32,
    // Movement-manager facade.
    pub move_to_made: bool,
    pub has_factory: bool,
    pub factory_calls: i32,
    pub activations: i32,
}

pub const TICK_SECONDS: f64 = 1.0 / 30.0;

pub fn pos(x: f32, y: f32, z: f32) -> CellPosition {
    CellPosition::new(1, Vec3::new(x, y, z), Quat::IDENTITY)
}

impl MoveToHarness {
    pub fn new() -> Self {
        let mut body = PhysicsBody::default();
        body.transient_state =
            TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
        Self {
            solo: StandaloneInterpreter {
                physics_obj: Some(body),
                ..StandaloneInterpreter::default()
            },
            manager: MoveToManager::new(0.0),
            world_position: CellPosition::new(1, Vec3::ZERO, Quat::IDENTITY),
            heading: 0.0,
            set_heading_calls: Vec::new(),
            own_radius: 0.5,
            own_height: 2.0,
            contact_value: true,
            is_interpolating_value: false,
            velocity: Vec3::ZERO,
            self_id: 0x5000_0001,
            stop_completely_calls: 0,
            set_target_calls: Vec::new(),
            clear_target_calls: 0,
            target_quantum: 0.0,
            set_target_quantum_calls: Vec::new(),
            unstick_calls: 0,
            stick_to_calls: Vec::new(),
            move_to_complete_calls: Vec::new(),
            move_to_cancelled_calls: None,
            cur_time: 0.0,
            reenter_cancel_on_stop: false,
            reentrant_cancel_calls: 0,
            state_during_stop: None,
            record_queue_on_remove_link: false,
            queue_empty_at_remove_link: None,
            remove_link_calls: 0,
            move_to_made: true,
            has_factory: true,
            factory_calls: 0,
            activations: 0,
        }
    }

    /// The facade of `MovementManagerTests`: the move-to manager exists only
    /// once the factory made it.
    pub fn facade() -> Self {
        Self {
            move_to_made: false,
            ..Self::new()
        }
    }

    pub fn tick(&mut self) {
        self.cur_time += TICK_SECONDS;
    }

    pub fn advance(&mut self, seconds: f64) {
        self.cur_time += seconds;
    }

    pub fn drain_pending_motions(&mut self) {
        while self.solo.interp.motions_pending() {
            self.motion_done(0, true);
        }
    }

    pub fn forward_command(&self) -> u32 {
        self.solo.interp.interpreted_state.forward_command
    }

    pub fn forward_speed(&self) -> f32 {
        self.solo.interp.interpreted_state.forward_speed
    }

    pub fn body(&mut self) -> &mut PhysicsBody {
        self.solo.physics_obj.as_mut().unwrap()
    }

    pub fn pending_count(&self) -> usize {
        self.manager.pending_actions().len()
    }

    pub fn nodes(&self) -> Vec<crate::movement::MoveToNode> {
        self.manager.pending_actions().copied().collect()
    }
}

impl InterpreterHost for MoveToHarness {
    fn interp(&self) -> &MotionInterpreter {
        &self.solo.interp
    }
    fn interp_mut(&mut self) -> &mut MotionInterpreter {
        &mut self.solo.interp
    }
    fn physics_obj(&self) -> Option<&PhysicsBody> {
        self.solo.physics_obj.as_ref()
    }
    fn physics_obj_mut(&mut self) -> Option<&mut PhysicsBody> {
        self.solo.physics_obj.as_mut()
    }
    fn weenie_obj(&self) -> Option<&dyn WeenieObject> {
        self.solo.weenie_obj.as_deref()
    }
    fn unstick_from_object(&mut self) {
        InterpreterHost::unstick_from_object(&mut self.solo);
    }
    fn interrupt_current_movement(&mut self) {
        InterpreterHost::interrupt_current_movement(&mut self.solo);
    }
    fn remove_link_animations(&mut self) {
        self.remove_link_calls += 1;
        if self.record_queue_on_remove_link && self.queue_empty_at_remove_link.is_none() {
            self.queue_empty_at_remove_link = Some(!self.solo.interp.motions_pending());
        }
        InterpreterHost::remove_link_animations(&mut self.solo);
    }
    fn initialize_motion_tables(&mut self) {
        InterpreterHost::initialize_motion_tables(&mut self.solo);
    }
    fn check_for_completed_motions(&mut self) {
        InterpreterHost::check_for_completed_motions(&mut self.solo);
    }
    fn cycle_velocity(&mut self) -> Option<Vec3> {
        InterpreterHost::cycle_velocity(&mut self.solo)
    }
    fn default_sink(&mut self) -> Option<&mut dyn InterpretedMotionSink> {
        InterpreterHost::default_sink(&mut self.solo)
    }
}

impl MoveToHost for MoveToHarness {
    fn move_to(&mut self) -> &mut MoveToManager {
        &mut self.manager
    }
    fn move_to_ref(&self) -> &MoveToManager {
        &self.manager
    }
    fn move_to_stop_completely(&mut self) {
        self.stop_completely_calls += 1;
        self.state_during_stop = Some(self.manager.movement_type_state);
        if self.reenter_cancel_on_stop {
            self.reentrant_cancel_calls += 1;
            self.cancel_move_to(WeenieError::ACTION_CANCELLED);
        }
    }
    fn move_to_position(&self) -> CellPosition {
        self.world_position
    }
    fn move_to_heading(&self) -> f32 {
        self.heading
    }
    fn move_to_set_heading(&mut self, heading: f32, send: bool) {
        self.set_heading_calls.push((heading, send));
        self.heading = heading;
    }
    fn move_to_own_radius(&self) -> f32 {
        self.own_radius
    }
    fn move_to_own_height(&self) -> f32 {
        self.own_height
    }
    fn move_to_contact(&self) -> bool {
        self.contact_value
    }
    fn move_to_is_interpolating(&self) -> bool {
        self.is_interpolating_value
    }
    fn move_to_velocity(&self) -> Vec3 {
        self.velocity
    }
    fn move_to_self_id(&self) -> u32 {
        self.self_id
    }
    fn move_to_set_target(&mut self, context_id: u32, object_id: u32, radius: f32, quantum: f64) {
        self.set_target_calls
            .push((context_id, object_id, radius, quantum));
    }
    fn move_to_clear_target(&mut self) {
        self.clear_target_calls += 1;
    }
    fn move_to_target_quantum(&self) -> f64 {
        self.target_quantum
    }
    fn move_to_set_target_quantum(&mut self, quantum: f64) {
        self.target_quantum = quantum;
        self.set_target_quantum_calls.push(quantum);
    }
    fn move_to_cur_time(&mut self) -> f64 {
        self.cur_time
    }
    fn move_to_unstick(&mut self) {
        self.unstick_calls += 1;
    }
    fn move_to_stick_to(&mut self, object_id: u32, radius: f32, height: f32) {
        self.stick_to_calls.push((object_id, radius, height));
    }
    fn move_to_complete(&mut self, error: WeenieError) {
        self.move_to_complete_calls.push(error);
    }
    fn move_to_cancelled(&mut self, error: WeenieError) {
        if let Some(calls) = &mut self.move_to_cancelled_calls {
            calls.push(error);
        }
    }
}

impl MovementManagerHost for MoveToHarness {
    fn move_to_exists(&self) -> bool {
        self.move_to_made
    }
    fn make_move_to_manager(&mut self) -> bool {
        if !self.move_to_made && self.has_factory {
            self.factory_calls += 1;
            self.move_to_made = true;
        }
        self.move_to_made
    }
    fn activate_physics_object(&mut self) {
        self.activations += 1;
    }
}
