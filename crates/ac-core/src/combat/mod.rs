//! Combat rules: modes, attack heights and types, the default combat mode
//! and the ready-position rule, who may be attacked, maneuver selection
//! from the combat table, combat animation kinds, and the combat state
//! the notifications update.
//!
//! The chat lines for combat live in [`crate::chat::combat_text`].

mod animation;
mod maneuver;
mod model;
mod state;
mod target_policy;

pub use animation::{
    CombatAnimationEvent, CombatAnimationKind, animation_for_event, classify_motion_command,
    motion_command,
};
pub use maneuver::{
    CombatManeuver, CombatManeuverSelection, DEFAULT_SUBDIVISION, THRUST_SLASH_SUBDIVISION,
    find_motions, select_motion,
};
pub use model::{
    ATTACK_POWER_UP_SECONDS, AttackHeight, AttackType, BodyPart, CarriedItem, CombatAttackAction,
    CombatMode, DUAL_WIELD_COMBAT_STYLE, DUAL_WIELD_POWER_UP_SECONDS, DamageType,
    DefaultCombatModeDecision, READY_FORWARD_COMMAND, default_combat_mode,
    default_combat_mode_decision, height_for, player_in_ready_position,
    player_in_ready_position_for_attack, supports_targeted_attack, toggle_mode,
};
pub use state::{CombatEvent, CombatState, DamageDealt, DamageIncoming};
pub use target_policy::{
    CombatObject, is_hostile_monster, object_is_attackable, should_query_health,
};
