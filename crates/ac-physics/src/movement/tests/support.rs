//! Shared helpers for the ported movement tests: bodies in the states the
//! OpenAC tests build, a configurable fake weenie, callback recorders and
//! xUnit's rounding float comparison.

use std::cell::Cell;
use std::rc::Rc;

use ac_core::physics::{PhysicsState, TransientState};

use crate::body::PhysicsBody;
use crate::movement::{StandaloneInterpreter, WeenieObject};

/// xUnit `Assert.Equal(expected, actual, precision)`: both rounded to
/// `precision` decimals (ties to even) must be equal.
#[track_caller]
pub fn eq_prec(expected: f32, actual: f32, precision: i32) {
    let scale = 10f64.powi(precision);
    let round = |x: f32| (f64::from(x) * scale).round_ties_even() / scale;
    assert_eq!(
        round(expected),
        round(actual),
        "expected {expected} to {precision} decimals, got {actual}"
    );
}

/// A default body with these state bits.
pub fn body_with(state: PhysicsState, transient: TransientState) -> PhysicsBody {
    let mut body = PhysicsBody::default();
    body.state = state;
    body.transient_state = transient;
    body
}

/// Gravity and collision reports, standing on walkable ground, awake.
pub fn make_grounded() -> PhysicsBody {
    body_with(
        PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS,
        TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE,
    )
}

/// Gravity and collision reports, in the air, awake.
pub fn make_airborne() -> PhysicsBody {
    body_with(
        PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS,
        TransientState::ACTIVE,
    )
}

/// An interpreter on `body` (grounded by default) with an optional weenie.
pub fn make_interp(
    body: Option<PhysicsBody>,
    weenie: Option<Box<dyn WeenieObject>>,
) -> StandaloneInterpreter {
    StandaloneInterpreter::new(body.unwrap_or_else(make_grounded), weenie)
}

/// An interpreter with no body.
pub fn bodiless() -> StandaloneInterpreter {
    StandaloneInterpreter::default()
}

/// A weenie whose every answer the test sets.
#[derive(Debug)]
pub struct FakeWeenie {
    pub run_rate: f32,
    pub jump_vz: f32,
    pub can_jump_result: bool,
    pub inq_run_rate_result: bool,
    pub inq_jump_velocity_result: bool,
    pub jump_stamina_cost_result: bool,
    pub jump_stamina_cost_calls: Cell<i32>,
    pub is_the_player_result: bool,
    pub is_creature_result: bool,
    /// Scale the jump velocity by the extent (the interpreter tests' fake)
    /// or not (the lifecycle tests' fake answers a flat 10).
    pub scale_jump_by_extent: bool,
}

impl Default for FakeWeenie {
    fn default() -> Self {
        Self {
            run_rate: 1.0,
            jump_vz: 10.0,
            can_jump_result: true,
            inq_run_rate_result: true,
            inq_jump_velocity_result: true,
            jump_stamina_cost_result: true,
            jump_stamina_cost_calls: Cell::new(0),
            is_the_player_result: false,
            is_creature_result: true,
            scale_jump_by_extent: true,
        }
    }
}

impl WeenieObject for FakeWeenie {
    fn inq_jump_velocity(&self, extent: f32) -> Option<f32> {
        let vz = if self.scale_jump_by_extent {
            self.jump_vz * extent
        } else {
            self.jump_vz
        };
        self.inq_jump_velocity_result.then_some(vz)
    }
    fn inq_run_rate(&self) -> Option<f32> {
        self.inq_run_rate_result.then_some(self.run_rate)
    }
    fn can_jump(&self, _extent: f32) -> bool {
        self.can_jump_result
    }
    fn is_creature(&self) -> bool {
        self.is_creature_result
    }
    fn is_the_player(&self) -> bool {
        self.is_the_player_result
    }
    fn jump_stamina_cost(&self, _extent: f32) -> Option<i32> {
        self.jump_stamina_cost_calls
            .set(self.jump_stamina_cost_calls.get() + 1);
        self.jump_stamina_cost_result.then_some(0)
    }
}

/// A weenie with a run rate (and no jump velocity).
pub fn run_rate_weenie(rate: f32) -> Box<dyn WeenieObject> {
    Box::new(FakeWeenie {
        run_rate: rate,
        inq_jump_velocity_result: false,
        ..FakeWeenie::default()
    })
}

/// A flag a callback sets.
pub fn flag() -> (Rc<Cell<bool>>, Box<dyn FnMut()>) {
    let fired = Rc::new(Cell::new(false));
    let f = Rc::clone(&fired);
    (fired, Box::new(move || f.set(true)))
}

/// A counter a callback bumps.
pub fn counter() -> (Rc<Cell<i32>>, Box<dyn FnMut()>) {
    let count = Rc::new(Cell::new(0));
    let c = Rc::clone(&count);
    (count, Box::new(move || c.set(c.get() + 1)))
}
