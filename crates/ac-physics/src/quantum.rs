//! When an object's physics runs: the per-object quantum clock and the
//! activity gate (objects far from the player sleep).

use ac_core::physics::TransientState;
use glam::Vec3;

use crate::body::PhysicsBody;

/// Retail's frame tolerance, seconds.
const FRAME_EPSILON: f64 = 0.000_199_999_995;

/// One update's quanta: some full 0.2 s steps and a remainder.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RetailObjectQuantumBatch {
    /// Full 0.2 s steps.
    pub full_steps: i32,
    /// The last, shorter step (0 for none), seconds.
    pub remainder: f32,
    /// The elapsed time was dropped (negative, NaN or over 2 s).
    pub discarded: bool,
}

impl RetailObjectQuantumBatch {
    /// How many quanta to run.
    #[must_use]
    pub fn count(&self) -> i32 {
        self.full_steps + i32::from(self.remainder > 0.0)
    }

    /// The length of quantum `index`, seconds; `None` past the end.
    #[must_use]
    pub fn get_quantum(&self, index: i32) -> Option<f32> {
        if index < 0 || index >= self.count() {
            return None;
        }
        Some(if index < self.full_steps {
            PhysicsBody::MAX_QUANTUM
        } else {
            self.remainder
        })
    }
}

/// An object's quantum clock: elapsed time accumulates until it is worth a
/// step (more than 1/30 s); more than 2 s at once is dropped.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RetailObjectQuantumClock {
    pending: f64,
    is_active: bool,
}

impl Default for RetailObjectQuantumClock {
    fn default() -> Self {
        Self {
            pending: 0.0,
            is_active: true,
        }
    }
}

impl RetailObjectQuantumClock {
    /// An active clock with nothing pending.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Time carried to the next update, seconds.
    #[must_use]
    pub fn pending_seconds(&self) -> f64 {
        self.pending
    }

    /// The object is awake.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.is_active
    }

    /// Adds `elapsed_seconds` and takes the quanta it makes.
    pub fn advance(&mut self, elapsed_seconds: f64) -> RetailObjectQuantumBatch {
        if elapsed_seconds.is_nan() || elapsed_seconds < 0.0 {
            self.pending = 0.0;
            return RetailObjectQuantumBatch {
                discarded: true,
                ..RetailObjectQuantumBatch::default()
            };
        }
        let mut elapsed = self.pending + elapsed_seconds;
        if elapsed <= FRAME_EPSILON {
            self.pending = 0.0;
            return RetailObjectQuantumBatch::default();
        }
        if elapsed > f64::from(PhysicsBody::HUGE_QUANTUM) {
            self.pending = 0.0;
            return RetailObjectQuantumBatch {
                discarded: true,
                ..RetailObjectQuantumBatch::default()
            };
        }
        let mut full_steps = 0;
        while elapsed > f64::from(PhysicsBody::MAX_QUANTUM) {
            full_steps += 1;
            elapsed -= f64::from(PhysicsBody::MAX_QUANTUM);
        }
        let mut remainder = 0.0f32;
        if elapsed > f64::from(PhysicsBody::MIN_QUANTUM) {
            remainder = elapsed as f32;
            elapsed = 0.0;
        }
        self.pending = elapsed;
        RetailObjectQuantumBatch {
            full_steps,
            remainder,
            discarded: false,
        }
    }

    /// Puts the object to sleep.
    pub fn deactivate(&mut self) {
        self.is_active = false;
    }

    /// Wakes the object (dropping pending time); false when it was awake.
    pub fn activate(&mut self) -> bool {
        if self.is_active {
            return false;
        }
        self.is_active = true;
        self.pending = 0.0;
        true
    }

    /// Drops pending time.
    pub fn reset(&mut self) {
        self.pending = 0.0;
    }

    /// Drops pending time on entering the world; wakes non-static objects.
    pub fn reset_for_enter_world(&mut self, is_static: bool) {
        self.pending = 0.0;
        if !is_static {
            self.is_active = true;
        }
    }
}

/// What the activity gate decided for one update.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RetailObjectActivityResult {
    /// Not eligible at all (asleep, clock untouched).
    Suspended,
    /// Asleep this update (time still accumulates).
    Inactive,
    /// Just woke up.
    Reactivated,
    /// Awake.
    Active,
}

/// Physics runs only within this distance of the player, metres.
pub const MAX_PHYSICS_DISTANCE: f32 = 96.0;

/// Decides whether an object's physics runs this update: ineligible
/// objects sleep; objects with a part array farther than 96 m from the
/// player sleep; others wake (unless static). Sleeping objects still
/// accumulate time on their clock.
#[allow(clippy::too_many_arguments)]
pub fn evaluate_activity(
    clock: &mut RetailObjectQuantumClock,
    body: Option<&mut PhysicsBody>,
    lifecycle_eligible: bool,
    has_part_array: bool,
    is_static: bool,
    object_position: Vec3,
    player_position: Option<Vec3>,
    elapsed_seconds: f64,
) -> RetailObjectActivityResult {
    if !lifecycle_eligible {
        set_inactive(clock, body);
        return RetailObjectActivityResult::Suspended;
    }

    let Some(player_position) = player_position else {
        if clock.is_active() {
            return RetailObjectActivityResult::Active;
        }
        clock.advance(elapsed_seconds);
        return RetailObjectActivityResult::Inactive;
    };

    let within_active_bubble =
        !has_part_array || object_position.distance(player_position) <= MAX_PHYSICS_DISTANCE;
    if !within_active_bubble {
        set_inactive(clock, body);
        clock.advance(elapsed_seconds);
        return RetailObjectActivityResult::Inactive;
    }

    let mut reactivated = false;
    if !is_static {
        reactivated = clock.activate();
        if let Some(b) = body {
            b.transient_state.insert(TransientState::ACTIVE);
        }
    }

    if !clock.is_active() {
        clock.advance(elapsed_seconds);
        return RetailObjectActivityResult::Inactive;
    }

    if reactivated {
        RetailObjectActivityResult::Reactivated
    } else {
        RetailObjectActivityResult::Active
    }
}

fn set_inactive(clock: &mut RetailObjectQuantumClock, body: Option<&mut PhysicsBody>) {
    clock.deactivate();
    if let Some(b) = body {
        b.transient_state.remove(TransientState::ACTIVE);
    }
}
