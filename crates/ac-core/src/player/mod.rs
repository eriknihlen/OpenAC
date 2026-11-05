//! The local player's numbers: vital maxima from attributes, attribute and
//! skill values with spells, augmentations and vitae applied, skill
//! formula terms, and the built-in skill names.
//!
//! The runtime feeds the character description and private updates in and
//! installs a skill-formula resolver built from the skill table.

mod level_progress;
mod local_player;
mod skill_formula;
mod skill_math;
pub mod skill_names;
mod vitae;

pub use level_progress::LevelProgress;
pub use local_player::{
    AttributeChange, AttributeKind, AttributeSnapshot, LocalPlayerState, SKILL_JUMP, SKILL_RUN,
    SkillFormulaBonusResolver, SkillSnapshot, VitalKind, VitalSnapshot, attribute_id_to_kind,
    vital_id_to_kind,
};
pub use skill_formula::{
    LiveSkillCreditResolver, SkillBase, SkillFormula, attribute_name, build_tooltip,
    format_formula, try_calculate,
};
pub use skill_math::{AugmentationBonuses, SkillValue, calculate};
pub use vitae::{VitaeStatus, vitae_cp_pool_threshold, vitae_status, vitae_text};
