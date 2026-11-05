//! Applying a new physics state to an object: what changes along with it.

use ac_core::physics::PhysicsState;

/// How the hidden bit changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum RetailHiddenTransition {
    /// It did not.
    #[default]
    None,
    /// The object became hidden.
    BecameHidden,
    /// The object became visible.
    BecameVisible,
}

/// The outcome of applying a physics state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RetailPhysicsStateTransition {
    /// The state before.
    pub previous_state: PhysicsState,
    /// The state asked for.
    pub requested_state: PhysicsState,
    /// The state that results.
    pub final_state: PhysicsState,
    /// The lighting bit changed.
    pub lighting_changed: bool,
    /// The no-draw bit changed.
    pub no_draw_changed: bool,
    /// How the hidden bit changed.
    pub hidden_transition: RetailHiddenTransition,
}

impl RetailPhysicsStateTransition {
    /// The final state differs from the previous one.
    #[must_use]
    pub fn has_changes(&self) -> bool {
        self.previous_state != self.final_state
    }
}

/// The state a new physics object starts with.
pub const CONSTRUCTOR_STATE: PhysicsState = PhysicsState(
    PhysicsState::EDGE_SLIDE.0
        | PhysicsState::LIGHTING.0
        | PhysicsState::GRAVITY.0
        | PhysicsState::REPORT_COLLISIONS.0,
);

/// Applies `requested_state` over `previous_state`. Hiding an object also
/// makes it ignore collisions and stop reporting them; showing it undoes
/// both. Only the low 16 bits are compared for changes.
#[must_use]
pub fn apply(
    previous_state: PhysicsState,
    requested_state: PhysicsState,
) -> RetailPhysicsStateTransition {
    let changed_low = (previous_state.bits() ^ requested_state.bits()) & 0xFFFF;
    let lighting_changed = changed_low & PhysicsState::LIGHTING.bits() != 0;
    let no_draw_changed = changed_low & PhysicsState::NO_DRAW.bits() != 0;
    let hidden_changed = changed_low & PhysicsState::HIDDEN.bits() != 0;

    let mut final_state = requested_state;
    let mut hidden_transition = RetailHiddenTransition::None;
    if hidden_changed {
        if requested_state.contains(PhysicsState::HIDDEN) {
            final_state.remove(PhysicsState::REPORT_COLLISIONS);
            final_state.insert(PhysicsState::HIDDEN | PhysicsState::IGNORE_COLLISIONS);
            hidden_transition = RetailHiddenTransition::BecameHidden;
        } else {
            final_state.remove(PhysicsState::HIDDEN | PhysicsState::IGNORE_COLLISIONS);
            final_state.insert(PhysicsState::REPORT_COLLISIONS);
            hidden_transition = RetailHiddenTransition::BecameVisible;
        }
    }

    RetailPhysicsStateTransition {
        previous_state,
        requested_state,
        final_state,
        lighting_changed,
        no_draw_changed,
        hidden_transition,
    }
}
