//! Magic rules: the spell table, formulas and their per-account tapers,
//! the component check, target validity, the spellbook with its favorite
//! bars and cooldowns, and how active enchantments stack into stat
//! modifiers.
//!
//! Everything here is pure: the runtime reads the dat tables and the
//! object table and passes plain values in; time comes from the caller.

mod components;
mod enchantment;
mod formula;
mod metadata;
mod projector;
mod spellbook;
mod table;
mod target_policy;

pub use components::{
    ComponentObject, SpellComponentRequirements, SpellComponentWorld, SpellFormulaDefinition,
    school_infusion_property,
};
pub use enchantment::{
    ATTACK_SKILLS, ActiveEnchantmentRecord, BUCKET_ADDITIVE, BUCKET_COOLDOWN,
    BUCKET_MULTIPLICATIVE, BUCKET_VITAE, DEFENSE_SKILLS, EnchantStep, EnchantmentTypeFlags,
    STAT_KEY_MAX_HEALTH, STAT_KEY_MAX_MANA, STAT_KEY_MAX_STAMINA, VitalMod, affects_attack_skills,
    affects_defense_skills, affects_stat, candidate_wins, cull_enchantments, duel,
    enchant_attribute, enchant_skill, get_enchantments_in_effect, get_mod, get_skill_mod,
    get_vitae_multiplier, make_identity, skill_vitae_modifier, vitae_modifier,
};
pub use formula::{
    MAX_FORMULA_COMPONENTS, PRISMATIC_TAPER_ID, compute_name_hash, customize_for_account,
    determine_power_level_of_component, get_target_type_from_component_id, get_targeting_type,
    inq_scarab_only_formula, inq_spell_level_by_rough_heuristic,
};
pub use metadata::{
    ComponentType, MagicSchool, SpellComponentSet, SpellFlags, SpellMetadata, SpellType,
};
pub use projector::{
    SpellBaseRecord, SpellComponentInfo, SpellFieldOutOfRange, project_spell, school_name,
};
pub use spellbook::{
    COOLDOWN_SPELL_OFFSET, CooldownCheck, DEFAULT_SPELLBOOK_FILTERS, FAVORITE_BAR_COUNT,
    LearnOutcome, LearnedSpellRecord, MetadataAlreadyInstalled, Spellbook,
};
pub use table::{DuplicateSpellId, SpellTable, parse_row};
pub use target_policy::{SpellTargetCandidate, SpellTargetRefusal, can_target_self, evaluate};
