//! Active enchantments and how they combine: the stacking rule that keeps
//! the strongest         .filter_map(EnchantStep::of); of each spell category, the steps a stat query
//! takes on its value, the death penalty (vitae), and the "in effect" list
//! the effects window shows.

use crate::wire_enum::wire_enum;

/// Bucket of multiplicative enchantments.
pub const BUCKET_MULTIPLICATIVE: u32 = 1;
/// Bucket of additive enchantments.
pub const BUCKET_ADDITIVE: u32 = 2;
/// Bucket of the death penalty (vitae).
pub const BUCKET_VITAE: u32 = 4;
/// Bucket of spell cooldowns.
pub const BUCKET_COOLDOWN: u32 = 8;

/// The stat key of maximum health in the secondary-attribute family.
pub const STAT_KEY_MAX_HEALTH: u32 = 1;
/// The stat key of maximum stamina.
pub const STAT_KEY_MAX_STAMINA: u32 = 3;
/// The stat key of maximum mana.
pub const STAT_KEY_MAX_MANA: u32 = 5;

wire_enum! {
    flags
    /// An enchantment's stat-mod type: the stat family it targets (the low
    /// bits), how it applies and what kind of enchantment it is.
    EnchantmentTypeFlags {
        ATTRIBUTE = 0x0000_0001 => "Attribute",
        /// A vital maximum (health, stamina, mana).
        SECOND_ATT = 0x0000_0002 => "SecondAtt",
        SKILL = 0x0000_0010 => "Skill",
        /// Applies to every key of the queried family, not one key.
        MULTIPLE_STAT = 0x0000_2000 => "MultipleStat",
        /// Multiplies the stat by its value.
        MULTIPLICATIVE = 0x0000_4000 => "Multiplicative",
        /// Adds its value to the stat.
        ADDITIVE = 0x0000_8000 => "Additive",
        /// Applies to every attack skill ([`ATTACK_SKILLS`]).
        ATTACK_SKILLS = 0x0001_0000 => "AttackSkills",
        /// Applies to every defense skill ([`DEFENSE_SKILLS`]).
        DEFENSE_SKILLS = 0x0002_0000 => "DefenseSkills",
        /// A beneficial enchantment (kept by a purge of bad enchantments).
        BENEFICIAL = 0x0200_0000 => "Beneficial",
    }
}

/// One active enchantment as the server described it. Fields the server
/// may leave out are `None`.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ActiveEnchantmentRecord {
    /// Spell id.
    pub spell_id: u32,
    /// Layer (several casts of one spell stack as layers).
    pub layer_id: u32,
    /// Duration in seconds; -1 means permanent.
    pub duration: f64,
    /// Who cast it.
    pub caster_guid: u32,
    /// Stat-mod type bits (see [`EnchantmentTypeFlags`]).
    pub stat_mod_type: Option<u32>,
    /// Stat-mod key (which attribute, vital or skill).
    pub stat_mod_key: Option<u32>,
    /// Stat-mod value (a factor or an amount).
    pub stat_mod_value: Option<f32>,
    /// Bucket: 1 multiplicative, 2 additive, 4 vitae, 8 cooldown.
    pub bucket: u32,
    /// Start time on the client's clock (seconds).
    pub start_time: f64,
    /// Spell category (stacking family on the wire).
    pub spell_category: u32,
    /// Power level.
    pub power_level: u32,
    /// Degrade modifier.
    pub degrade_modifier: f32,
    /// Degrade limit.
    pub degrade_limit: f32,
    /// Last time degraded (client clock).
    pub last_time_degraded: f64,
    /// Spell-set id, when the enchantment comes from an item set.
    pub spell_set_id: Option<u32>,
}

impl ActiveEnchantmentRecord {
    /// A record with only its identity, duration and caster.
    #[must_use]
    pub fn new(spell_id: u32, layer_id: u32, duration: f64, caster_guid: u32) -> Self {
        Self {
            spell_id,
            layer_id,
            duration,
            caster_guid,
            ..Self::default()
        }
    }

    /// The packed `(spell, layer)` key the registry stores it under.
    #[must_use]
    pub fn identity(&self) -> u32 {
        make_identity(self.spell_id, self.layer_id)
    }
}

/// Packs a spell id and layer into one key: spell in the low 16 bits,
/// layer in the high 16.
#[must_use]
pub fn make_identity(spell_id: u32, layer_id: u32) -> u32 {
    (spell_id & 0xFFFF) | ((layer_id & 0xFFFF) << 16)
}

/// Skills an all-attack-skills enchantment raises: life magic, war magic,
/// two-handed combat, void magic, heavy, light and finesse weapons,
/// missile weapons and dual wield.
pub const ATTACK_SKILLS: [u32; 9] = [33, 34, 41, 43, 44, 45, 46, 47, 49];
/// Skills an all-defense-skills enchantment raises: melee defense, missile
/// defense, magic defense and shield.
pub const DEFENSE_SKILLS: [u32; 4] = [6, 7, 15, 48];

/// One step an enchantment takes on a value, in single precision.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum EnchantStep {
    /// The value is multiplied by this factor.
    Multiply(f32),
    /// This amount is added to the value.
    Add(f32),
}

impl EnchantStep {
    /// The step applied to `value`, rounded to single precision.
    #[must_use]
    pub fn apply(self, value: f32) -> f32 {
        match self {
            Self::Multiply(factor) => factor * value,
            Self::Add(amount) => amount + value,
        }
    }

    /// The step a record takes: its type's additive bit adds (checked
    /// first), its multiplicative bit multiplies; a record with neither bit,
    /// or with no value, takes none.
    #[must_use]
    pub fn of(record: &ActiveEnchantmentRecord) -> Option<Self> {
        let value = record.stat_mod_value?;
        let mod_type = record.stat_mod_type.unwrap_or(0);
        if mod_type & EnchantmentTypeFlags::ADDITIVE.bits() != 0 {
            Some(Self::Add(value))
        } else if mod_type & EnchantmentTypeFlags::MULTIPLICATIVE.bits() != 0 {
            Some(Self::Multiply(value))
        } else {
            None
        }
    }
}

/// What the active enchantments do to one stat: the steps taken on its
/// value, in order (vitae first, where it counts, then each record in
/// effect). Every step rounds to single precision, so the order shows in
/// the last bit, and a truncated total can differ by one from scaling by
/// the product of the factors.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct VitalMod {
    steps: Vec<EnchantStep>,
}

impl VitalMod {
    /// No enchantment applies.
    pub const IDENTITY: Self = Self { steps: Vec::new() };

    /// A multiplication followed by an addition.
    #[must_use]
    pub fn new(multiplier: f32, additive: f32) -> Self {
        Self::from_steps([
            EnchantStep::Multiply(multiplier),
            EnchantStep::Add(additive),
        ])
    }

    /// These steps, in this order.
    #[must_use]
    pub fn from_steps(steps: impl IntoIterator<Item = EnchantStep>) -> Self {
        Self {
            steps: steps.into_iter().collect(),
        }
    }

    /// The steps, in the order they are taken.
    #[must_use]
    pub fn steps(&self) -> &[EnchantStep] {
        &self.steps
    }

    /// `value` after every step in turn.
    #[must_use]
    pub fn apply(&self, value: f32) -> f32 {
        self.steps
            .iter()
            .fold(value, |value, step| step.apply(value))
    }

    /// The product of the multiplications (1 with none).
    #[must_use]
    pub fn multiplier(&self) -> f32 {
        self.steps.iter().fold(1.0, |product, step| match step {
            EnchantStep::Multiply(factor) => product * factor,
            EnchantStep::Add(_) => product,
        })
    }

    /// The sum of the additions (0 with none).
    #[must_use]
    pub fn additive(&self) -> f32 {
        self.steps.iter().fold(0.0, |sum, step| match step {
            EnchantStep::Multiply(_) => sum,
            EnchantStep::Add(amount) => sum + amount,
        })
    }
}

/// Whether an all-attack-skills record raises the skill.
#[must_use]
pub fn affects_attack_skills(record: &ActiveEnchantmentRecord, skill_id: u32) -> bool {
    record.stat_mod_type.unwrap_or(0) & EnchantmentTypeFlags::ATTACK_SKILLS.bits() != 0
        && ATTACK_SKILLS.contains(&skill_id)
}

/// Whether an all-defense-skills record raises the skill.
#[must_use]
pub fn affects_defense_skills(record: &ActiveEnchantmentRecord, skill_id: u32) -> bool {
    record.stat_mod_type.unwrap_or(0) & EnchantmentTypeFlags::DEFENSE_SKILLS.bits() != 0
        && DEFENSE_SKILLS.contains(&skill_id)
}

/// Whether a record applies to the stat `stat_key` of the family `family`
/// (attribute, vital maximum or skill): its type must name the family, and
/// then it applies when it is a multiple-stat record (whatever its key),
/// when its key is the stat's, or when it raises all attack or all defense
/// skills and the stat is one of them.
#[must_use]
pub fn affects_stat(
    record: &ActiveEnchantmentRecord,
    family: EnchantmentTypeFlags,
    stat_key: u32,
) -> bool {
    let mod_type = record.stat_mod_type.unwrap_or(0);
    if mod_type & family.bits() == 0 {
        return false;
    }
    mod_type & EnchantmentTypeFlags::MULTIPLE_STAT.bits() != 0
        || record.stat_mod_key == Some(stat_key)
        || affects_attack_skills(record, stat_key)
        || affects_defense_skills(record, stat_key)
}

/// The records in effect for one stat. The records that apply to it
/// ([`affects_stat`]) are taken from the multiplicative list, then from the
/// additive list, each walked from its head, into one list that holds one
/// record per spell category (category 0 included); a newcomer duels the
/// record of its category already there ([`duel`]). So a multiplication and
/// an addition of one category compete, and the spell table plays no part.
#[must_use]
pub fn cull_enchantments<'a>(
    multiplicative: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
    additive: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
    family: EnchantmentTypeFlags,
    stat_key: u32,
) -> Vec<&'a ActiveEnchantmentRecord> {
    let mut in_effect = Vec::new();
    for record in multiplicative.into_iter().chain(additive) {
        if affects_stat(record, family, stat_key) {
            duel(record, &mut in_effect);
        }
    }
    in_effect
}

/// Enters `candidate` into `in_effect`. When a record of its spell category
/// is there and beats it ([`candidate_wins`]), nothing changes; otherwise
/// that record is taken out (unless its identity is zero) and the candidate
/// is appended.
pub fn duel<'a>(
    candidate: &'a ActiveEnchantmentRecord,
    in_effect: &mut Vec<&'a ActiveEnchantmentRecord>,
) {
    if let Some(index) = in_effect
        .iter()
        .position(|existing| existing.spell_category == candidate.spell_category)
    {
        let incumbent = in_effect[index];
        if !candidate_wins(incumbent, candidate) {
            return;
        }
        if incumbent.identity() != 0 {
            in_effect.remove(index);
        }
    }
    in_effect.push(candidate);
}

/// Whether a newcomer beats the record of its category: the higher power
/// level wins (compared as signed); at equal power the newcomer wins unless
/// it started strictly earlier. Lists are walked from the head, where new
/// records go, so an exact tie goes to the record that arrived first.
#[must_use]
pub fn candidate_wins(
    incumbent: &ActiveEnchantmentRecord,
    candidate: &ActiveEnchantmentRecord,
) -> bool {
    let incumbent_power = incumbent.power_level.cast_signed();
    let candidate_power = candidate.power_level.cast_signed();
    candidate_power > incumbent_power
        || (candidate_power == incumbent_power && candidate.start_time >= incumbent.start_time)
}

/// The modifier the active enchantments give one stat. `enchantments` are
/// in list order: the multiplicative list, the additive list and the
/// vitae, each from its head. With `include_vitae`, each vitae record
/// takes its step first; then the records in effect for the stat
/// ([`cull_enchantments`]) take theirs in the order they ended up in. Every
/// step is the record's own ([`EnchantStep::of`]), vitae included, so a
/// vitae record with neither operation bit changes nothing.
#[must_use]
pub fn get_mod<'a>(
    enchantments: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
    stat_key: u32,
    family: EnchantmentTypeFlags,
    include_vitae: bool,
) -> VitalMod {
    let enchantments: Vec<&ActiveEnchantmentRecord> = enchantments.into_iter().collect();
    let in_list = |bucket: u32| {
        enchantments
            .iter()
            .copied()
            .filter(move |record| record.bucket == bucket)
    };
    let vitae = in_list(BUCKET_VITAE)
        .filter(|_| include_vitae)
        .filter_map(EnchantStep::of);
    let in_effect = cull_enchantments(
        in_list(BUCKET_MULTIPLICATIVE),
        in_list(BUCKET_ADDITIVE),
        family,
        stat_key,
    );
    VitalMod::from_steps(vitae.chain(in_effect.into_iter().filter_map(EnchantStep::of)))
}

/// The modifier a skill gets: [`get_mod`] for the skill family, vitae
/// included.
#[must_use]
pub fn get_skill_mod<'a>(
    enchantments: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
    skill_id: u32,
) -> VitalMod {
    get_mod(enchantments, skill_id, EnchantmentTypeFlags::SKILL, true)
}

/// An attribute after enchantments: the modifier's steps on the base,
/// floored at 1 for a base below 10 and at 10 otherwise, then truncated.
#[must_use]
pub fn enchant_attribute(modifier: &VitalMod, base_value: u32) -> i32 {
    let mut value = modifier.apply(base_value as f32);
    let floor = if base_value < 10 { 1.0 } else { 10.0 };
    if value < floor {
        value = floor;
    }
    value as i32
}

/// A skill after enchantments: the modifier's steps on the base, zero below
/// one half, then truncated.
#[must_use]
pub fn enchant_skill(modifier: &VitalMod, base_value: u32) -> i32 {
    let mut value = modifier.apply(base_value as f32);
    if value < 0.5 {
        value = 0.0;
    }
    value as i32
}

/// The product of every vitae record's value (1 when there is none).
#[must_use]
pub fn get_vitae_multiplier<'a>(
    enchantments: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
) -> f32 {
    enchantments
        .into_iter()
        .filter(|e| e.bucket == BUCKET_VITAE)
        .filter_map(|e| e.stat_mod_value)
        .fold(1.0, |vitae, value| vitae * value)
}

/// How much vitae takes off a value: `trunc(base * vitae) - base`, zero
/// with no vitae.
#[must_use]
pub fn vitae_modifier(vitae_multiplier: f32, base_value: u32) -> i32 {
    if vitae_multiplier == 1.0 {
        return 0;
    }
    // The game works in 32-bit signed integers here; a base above
    // `i32::MAX` wraps as it does there.
    ((base_value as f32 * vitae_multiplier) as i32).wrapping_sub(base_value.cast_signed())
}

/// [`vitae_modifier`] with the vitae taken from the active enchantments.
#[must_use]
pub fn skill_vitae_modifier<'a>(
    enchantments: impl IntoIterator<Item = &'a ActiveEnchantmentRecord>,
    base_value: u32,
) -> i32 {
    vitae_modifier(get_vitae_multiplier(enchantments), base_value)
}

/// The enchantments in effect, as the effects window lists them: every
/// multiplicative record, then every additive one, each list walked in
/// order, entered with [`duel`], so one record per spell category remains.
#[must_use]
pub fn get_enchantments_in_effect(
    enchantments: &[ActiveEnchantmentRecord],
) -> Vec<ActiveEnchantmentRecord> {
    let mut in_effect = Vec::new();
    for bucket in [BUCKET_MULTIPLICATIVE, BUCKET_ADDITIVE] {
        for candidate in enchantments.iter().filter(|c| c.bucket == bucket) {
            duel(candidate, &mut in_effect);
        }
    }
    in_effect.into_iter().copied().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Spells/EnchantmentMathTests.cs` and
    // `EnchantmentRegistryProjectionTests.cs`, with the game client's
    // stacking rule (one shared list per stat, grouped by the record's spell
    // category, power then start time, an exact tie to the later
    // challenger). Test cases adapted from PR #36 by evantaylor.

    const MULTIPLY: u32 = EnchantmentTypeFlags::MULTIPLICATIVE.bits();
    const ADD: u32 = EnchantmentTypeFlags::ADDITIVE.bits();
    const VITAL: EnchantmentTypeFlags = EnchantmentTypeFlags::SECOND_ATT;

    /// A record as the server sends one: its operation bit matches its
    /// list, and its spell category is its own (the spell id).
    fn record(
        spell_id: u32,
        layer: u32,
        bucket: u32,
        stat_mod_type: u32,
        key: u32,
        value: f32,
    ) -> ActiveEnchantmentRecord {
        let operation = match bucket {
            BUCKET_MULTIPLICATIVE => MULTIPLY,
            BUCKET_ADDITIVE => ADD,
            _ => 0,
        };
        ActiveEnchantmentRecord {
            stat_mod_type: Some(stat_mod_type | operation),
            stat_mod_key: Some(key),
            stat_mod_value: Some(value),
            bucket,
            spell_category: spell_id,
            ..ActiveEnchantmentRecord::new(spell_id, layer, 60.0, 0)
        }
    }

    fn mult(spell_id: u32, layer: u32, key: u32, value: f32) -> ActiveEnchantmentRecord {
        record(spell_id, layer, 1, VITAL.bits(), key, value)
    }

    fn add(spell_id: u32, layer: u32, key: u32, value: f32) -> ActiveEnchantmentRecord {
        record(spell_id, layer, 2, VITAL.bits(), key, value)
    }

    fn vitae(spell_id: u32, layer: u32, key: u32, value: f32) -> ActiveEnchantmentRecord {
        ActiveEnchantmentRecord {
            duration: -1.0,
            ..record(spell_id, layer, 4, MULTIPLY, key, value)
        }
    }

    fn ranked(
        record: ActiveEnchantmentRecord,
        category: u32,
        power: u32,
        start: f64,
    ) -> ActiveEnchantmentRecord {
        ActiveEnchantmentRecord {
            spell_category: category,
            power_level: power,
            start_time: start,
            ..record
        }
    }

    fn vital_mod(list: &[ActiveEnchantmentRecord], key: u32) -> VitalMod {
        get_mod(list, key, VITAL, true)
    }

    fn close(actual: f32, expected: f32) -> bool {
        (actual - expected).abs() < 1e-4
    }

    #[test]
    fn empty_is_identity() {
        assert_eq!(vital_mod(&[], STAT_KEY_MAX_STAMINA), VitalMod::IDENTITY);
    }

    #[test]
    fn records_without_a_stat_mod_contribute_nothing() {
        let list = [
            ActiveEnchantmentRecord::new(9999, 1, 60.0, 0),
            ActiveEnchantmentRecord::new(8888, 2, 60.0, 0),
        ];
        assert_eq!(vital_mod(&list, STAT_KEY_MAX_STAMINA), VitalMod::IDENTITY);
    }

    #[test]
    fn stat_key_constants() {
        assert_eq!(STAT_KEY_MAX_HEALTH, 1);
        assert_eq!(STAT_KEY_MAX_STAMINA, 3);
        assert_eq!(STAT_KEY_MAX_MANA, 5);
        assert_eq!(EnchantmentTypeFlags::SKILL.bits(), 0x10);
        assert_eq!(EnchantmentTypeFlags::SECOND_ATT.bits(), 0x2);
        assert_eq!(MULTIPLY, 0x4000);
        assert_eq!(ADD, 0x8000);
        assert_eq!(EnchantmentTypeFlags::ATTACK_SKILLS.bits(), 0x1_0000);
        assert_eq!(EnchantmentTypeFlags::DEFENSE_SKILLS.bits(), 0x2_0000);
    }

    #[test]
    fn multiplicative_records_multiply() {
        let m = vital_mod(&[mult(10, 1, 3, 1.2), mult(11, 2, 3, 1.1)], 3);
        assert!(close(m.multiplier(), 1.32));
        assert!(m.additive() == 0.0);
    }

    #[test]
    fn additive_records_sum() {
        let m = vital_mod(&[add(20, 1, 5, 25.0), add(21, 2, 5, 50.0)], 5);
        assert!(m.multiplier() == 1.0);
        assert!(m.additive() == 75.0);
    }

    #[test]
    fn stat_key_mismatch_contributes_nothing() {
        assert_eq!(vital_mod(&[mult(30, 1, 1, 1.5)], 3), VitalMod::IDENTITY);
    }

    #[test]
    fn vitae_multiplies_first() {
        let m = vital_mod(&[add(40, 1, 1, 10.0), vitae(41, 2, 1, 0.85)], 1);
        assert_eq!(
            m.steps(),
            [EnchantStep::Multiply(0.85), EnchantStep::Add(10.0)]
        );
        assert!(close(m.apply(100.0), 95.0));
    }

    #[test]
    fn vitae_with_neither_operation_bit_does_nothing() {
        let bare = ActiveEnchantmentRecord {
            duration: -1.0,
            ..record(41, 2, BUCKET_VITAE, 0, 1, 0.85)
        };
        assert_eq!(vital_mod(&[bare], 1), VitalMod::IDENTITY);
    }

    #[test]
    fn vitae_applies_with_key_zero() {
        let m = vital_mod(&[vitae(666, 0, 0, 0.95)], STAT_KEY_MAX_STAMINA);
        assert!(close(m.multiplier(), 0.95));
    }

    #[test]
    fn include_vitae_false_excludes_vitae() {
        assert_eq!(
            get_mod(
                &[vitae(60, 1, 0, 0.67)],
                1,
                EnchantmentTypeFlags::ATTRIBUTE,
                false
            ),
            VitalMod::IDENTITY
        );
    }

    #[test]
    fn the_steps_round_in_turn() {
        // 200 x 0.95 = 190 and 190 x 1.1 = 209 in single precision; 200 x
        // (0.95 x 1.1) is 208.99998.
        let m = VitalMod::from_steps([EnchantStep::Multiply(0.95), EnchantStep::Multiply(1.1)]);
        assert!(m.apply(200.0) == 209.0);
        let product = VitalMod::from_steps([EnchantStep::Multiply(0.95f32 * 1.1)]);
        assert!(product.apply(200.0) < 209.0);
    }

    #[test]
    fn the_additive_bit_is_checked_first() {
        let both = ActiveEnchantmentRecord {
            stat_mod_type: Some(VITAL.bits() | MULTIPLY | ADD),
            ..mult(1, 1, 1, 10.0)
        };
        assert_eq!(EnchantStep::of(&both), Some(EnchantStep::Add(10.0)));
        let neither = ActiveEnchantmentRecord {
            stat_mod_type: Some(VITAL.bits()),
            ..mult(1, 1, 1, 10.0)
        };
        assert_eq!(EnchantStep::of(&neither), None);
        let no_value = ActiveEnchantmentRecord {
            stat_mod_value: None,
            ..mult(1, 1, 1, 10.0)
        };
        assert_eq!(EnchantStep::of(&no_value), None);
    }

    #[test]
    fn one_category_keeps_its_strongest_record() {
        // Same category: the higher power wins, whatever the spell id.
        let weak = ranked(mult(132, 2, 3, 1.5), 1, 1, 0.0);
        let strong = ranked(mult(10, 1, 3, 1.1), 1, 7, 0.0);
        assert!(close(vital_mod(&[weak, strong], 3).multiplier(), 1.1));
        assert!(close(vital_mod(&[strong, weak], 3).multiplier(), 1.1));
    }

    #[test]
    fn category_zero_groups_like_any_other() {
        let a = ranked(add(1, 1, 5, 40.0), 0, 0, 0.0);
        let b = ranked(add(2, 2, 5, 56.0), 0, 0, 0.0);
        // An exact tie goes to the later challenger in list order.
        assert!(vital_mod(&[a, b], 5).additive() == 56.0);
        assert!(vital_mod(&[b, a], 5).additive() == 40.0);
    }

    #[test]
    fn multiplicative_and_additive_records_share_one_list() {
        let scale = ranked(mult(1, 1, 5, 1.1), 9, 10, 0.0);
        let plus = ranked(add(2, 2, 5, 10.0), 9, 5, 0.0);
        let m = vital_mod(&[scale, plus], 5);
        assert_eq!(m.steps(), [EnchantStep::Multiply(1.1)]);
        let plus = ranked(plus, 9, 20, 0.0);
        let m = vital_mod(&[scale, plus], 5);
        assert_eq!(m.steps(), [EnchantStep::Add(10.0)]);
        // The additive list is culled after the multiplicative one, whatever
        // order the records come in.
        let m = vital_mod(&[plus, scale], 5);
        assert_eq!(m.steps(), [EnchantStep::Add(10.0)]);
    }

    #[test]
    fn candidate_wins_on_power_then_start() {
        let at = |power: u32, start: f64| ranked(add(1, 1, 1, 1.0), 1, power, start);
        assert!(candidate_wins(&at(1, 0.0), &at(2, 0.0)));
        assert!(!candidate_wins(&at(2, 0.0), &at(1, 9.0)));
        assert!(candidate_wins(&at(5, 10.0), &at(5, 20.0)));
        assert!(candidate_wins(&at(5, 10.0), &at(5, 10.0)));
        assert!(!candidate_wins(&at(5, 20.0), &at(5, 10.0)));
        // Power compares as signed.
        assert!(!candidate_wins(&at(1, 0.0), &at(u32::MAX, 0.0)));
    }

    #[test]
    fn a_beaten_record_of_identity_zero_stays() {
        let zero = ranked(add(0, 0, 1, 5.0), 3, 1, 0.0);
        let stronger = ranked(add(7, 1, 1, 9.0), 3, 2, 0.0);
        let mut in_effect = Vec::new();
        duel(&zero, &mut in_effect);
        duel(&stronger, &mut in_effect);
        assert_eq!(in_effect, [&zero, &stronger]);
    }

    #[test]
    fn required_type_excludes_cross_domain_key_collision() {
        let list = [record(
            50,
            1,
            1,
            EnchantmentTypeFlags::ATTRIBUTE.bits(),
            1,
            1.5,
        )];
        assert_eq!(get_mod(&list, 1, VITAL, true), VitalMod::IDENTITY);
        let m = get_mod(&list, 1, EnchantmentTypeFlags::ATTRIBUTE, true);
        assert!(close(m.multiplier(), 1.5));
    }

    #[test]
    fn multiple_stat_applies_to_every_attribute_whatever_its_key() {
        let blessing =
            (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).bits();
        for blessing_key in [0, 4] {
            let list = [
                record(70, 1, 2, EnchantmentTypeFlags::ATTRIBUTE.bits(), 1, 40.0),
                record(71, 2, 2, blessing, blessing_key, 9.0),
            ];
            for key in 1..=6 {
                let m = get_mod(&list, key, EnchantmentTypeFlags::ATTRIBUTE, true);
                assert!(
                    m.additive() == if key == 1 { 49.0 } else { 9.0 },
                    "key {key}"
                );
            }
        }
    }

    #[test]
    fn key_zero_without_multiple_stat_contributes_nothing() {
        let list = [record(
            72,
            1,
            2,
            EnchantmentTypeFlags::ATTRIBUTE.bits(),
            0,
            9.0,
        )];
        assert_eq!(
            get_mod(&list, 1, EnchantmentTypeFlags::ATTRIBUTE, true),
            VitalMod::IDENTITY
        );
    }

    #[test]
    fn multiple_stat_attribute_does_not_leak_into_vitals_or_skills() {
        let blessing =
            (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).bits();
        let list = [record(73, 1, 2, blessing, 0, 9.0)];
        assert_eq!(get_mod(&list, 1, VITAL, true), VitalMod::IDENTITY);
        assert_eq!(get_skill_mod(&list, 1), VitalMod::IDENTITY);
    }

    #[test]
    fn attack_and_defense_skill_records_name_their_skills() {
        let skill = EnchantmentTypeFlags::SKILL.bits();
        let attack = record(80, 1, 2, skill | 0x1_0000, 0, 10.0);
        let defense = record(81, 2, 2, skill | 0x2_0000, 0, 10.0);
        for id in 0..=60 {
            assert_eq!(
                affects_attack_skills(&attack, id),
                ATTACK_SKILLS.contains(&id),
                "{id}"
            );
            assert_eq!(
                affects_defense_skills(&defense, id),
                DEFENSE_SKILLS.contains(&id),
                "{id}"
            );
            assert!(!affects_attack_skills(&defense, id));
            assert!(!affects_defense_skills(&attack, id));
        }
        // The family bit is still required.
        let attack_attribute = record(82, 3, 2, 0x1_0001, 0, 10.0);
        assert!(!affects_stat(
            &attack_attribute,
            EnchantmentTypeFlags::SKILL,
            45
        ));
        assert!(affects_stat(&attack, EnchantmentTypeFlags::SKILL, 45));
    }

    #[test]
    fn enchant_attribute_rules() {
        assert_eq!(enchant_attribute(&VitalMod::IDENTITY, 200), 200);
        assert_eq!(enchant_attribute(&VitalMod::new(1.1, 0.0), 200), 220);
        assert_eq!(enchant_attribute(&VitalMod::new(0.1, 0.0), 5), 1);
        assert_eq!(enchant_attribute(&VitalMod::new(0.1, 0.0), 50), 10);
    }

    #[test]
    fn enchant_skill_rules() {
        // 33% vitae on a base-303 skill: 303 * 0.67 = 203.01 -> 203.
        assert_eq!(enchant_skill(&VitalMod::new(0.67, 0.0), 303), 203);
        assert_eq!(enchant_skill(&VitalMod::new(0.67, 50.0), 300), 251);
        assert_eq!(enchant_skill(&VitalMod::new(0.001, 0.0), 10), 0);
    }

    #[test]
    fn vitae_multiplier_and_modifier() {
        assert!(get_vitae_multiplier(&[mult(1, 1, 1, 1.5)]) == 1.0);
        let list = [vitae(1, 1, 0, 0.67), mult(2, 2, 1, 1.5)];
        assert!(close(get_vitae_multiplier(&list), 0.67));
        assert_eq!(skill_vitae_modifier(&[vitae(1, 1, 0, 0.67)], 303), -100);
        assert_eq!(skill_vitae_modifier(&[], 303), 0);
    }

    #[test]
    fn skill_mod_rules() {
        let skill = EnchantmentTypeFlags::SKILL.bits();
        assert_eq!(get_skill_mod(&[], 24), VitalMod::IDENTITY);
        assert!(close(
            get_skill_mod(&[record(50, 1, 1, skill, 24, 1.2)], 24).multiplier(),
            1.2
        ));
        assert!(close(
            get_skill_mod(&[record(51, 1, 2, skill, 22, 15.0)], 22).additive(),
            15.0
        ));
        assert_eq!(
            get_skill_mod(&[record(52, 1, 1, skill, 7, 1.5)], 24),
            VitalMod::IDENTITY
        );
        let vital_typed = [record(53, 1, 1, VITAL.bits(), 24, 1.5)];
        assert_eq!(get_skill_mod(&vital_typed, 24), VitalMod::IDENTITY);
        assert!(close(
            get_skill_mod(&[vitae(54, 0, 0, 0.9)], 24).multiplier(),
            0.9
        ));
    }

    fn effect(
        spell_id: u32,
        bucket: u32,
        category: u32,
        power: u32,
        start: f64,
    ) -> ActiveEnchantmentRecord {
        ActiveEnchantmentRecord {
            bucket,
            start_time: start,
            spell_category: category,
            power_level: power,
            ..ActiveEnchantmentRecord::new(spell_id, spell_id, 60.0, 1)
        }
    }

    fn ids(list: &[ActiveEnchantmentRecord]) -> Vec<u32> {
        list.iter().map(|r| r.spell_id).collect()
    }

    #[test]
    fn in_effect_excludes_vitae_and_cooldown() {
        let result = get_enchantments_in_effect(&[
            effect(1, 1, 1, 1, 0.0),
            effect(2, 4, 2, 1, 0.0),
            effect(3, 8, 3, 1, 0.0),
            effect(4, 2, 4, 1, 0.0),
        ]);
        assert_eq!(ids(&result), [1, 4]);
    }

    #[test]
    fn in_effect_duel_prefers_higher_power() {
        let result =
            get_enchantments_in_effect(&[effect(1, 1, 12, 1, 0.0), effect(2, 1, 12, 7, 0.0)]);
        assert_eq!(ids(&result), [2]);
    }

    #[test]
    fn in_effect_power_compares_signed() {
        let result = get_enchantments_in_effect(&[
            effect(1, 1, 12, 1, 0.0),
            effect(2, 1, 12, u32::MAX, 0.0),
        ]);
        assert_eq!(ids(&result), [1]);
    }

    #[test]
    fn in_effect_equal_power_prefers_later_start_and_an_exact_tie_the_challenger() {
        let result = get_enchantments_in_effect(&[
            effect(1, 1, 12, 7, 10.0),
            effect(2, 1, 12, 7, 20.0),
            effect(3, 2, 12, 7, 20.0),
        ]);
        assert_eq!(ids(&result), [3]);
    }

    #[test]
    fn in_effect_replacing_winner_appends() {
        let result = get_enchantments_in_effect(&[
            effect(1, 1, 10, 1, 0.0),
            effect(2, 1, 20, 1, 0.0),
            effect(3, 2, 10, 2, 0.0),
        ]);
        assert_eq!(ids(&result), [2, 3]);
    }
}
