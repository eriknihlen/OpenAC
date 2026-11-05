//! The local player's character numbers: vitals, primary attributes and
//! skills as the server sent them, and the formulas that turn them into
//! what the character sheet shows: vital maxima from Endurance and Self,
//! attribute and skill values with spells and vitae applied, and the
//! skill formula terms recomputed whenever an attribute changes.

use std::collections::BTreeMap;
use std::sync::Arc;

use super::skill_math::{AugmentationBonuses, SkillValue, calculate};
use crate::properties::PropertyInt;
use crate::spells::{
    STAT_KEY_MAX_HEALTH, STAT_KEY_MAX_MANA, STAT_KEY_MAX_STAMINA, Spellbook, VitalMod,
    enchant_attribute, get_vitae_multiplier,
};

/// The skill id of Run.
pub const SKILL_RUN: u32 = 24;
/// The skill id of Jump.
pub const SKILL_JUMP: u32 = 22;

/// A vital.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum VitalKind {
    /// Health.
    Health,
    /// Stamina.
    Stamina,
    /// Mana.
    Mana,
}

impl VitalKind {
    /// Every vital, in order.
    pub const ALL: [Self; 3] = [Self::Health, Self::Stamina, Self::Mana];
}

/// A primary attribute.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum AttributeKind {
    /// Strength (id 1).
    Strength,
    /// Endurance (id 2).
    Endurance,
    /// Quickness (id 3).
    Quickness,
    /// Coordination (id 4).
    Coordination,
    /// Focus (id 5).
    Focus,
    /// Self (id 6).
    Self_,
}

impl AttributeKind {
    /// Every attribute, in id order.
    pub const ALL: [Self; 6] = [
        Self::Strength,
        Self::Endurance,
        Self::Quickness,
        Self::Coordination,
        Self::Focus,
        Self::Self_,
    ];

    /// The attribute's id (1..=6).
    #[must_use]
    pub fn id(self) -> u32 {
        match self {
            Self::Strength => 1,
            Self::Endurance => 2,
            Self::Quickness => 3,
            Self::Coordination => 4,
            Self::Focus => 5,
            Self::Self_ => 6,
        }
    }

    fn index(self) -> usize {
        // Ids are 1..=6.
        (self.id() - 1) as usize
    }
}

/// The vital an id names: the update ids (1/2 health, 3/4 stamina, 5/6
/// mana, maximum and current) and the character-description ids (7, 8, 9).
#[must_use]
pub fn vital_id_to_kind(vital_id: u32) -> Option<VitalKind> {
    match vital_id {
        1 | 2 | 7 => Some(VitalKind::Health),
        3 | 4 | 8 => Some(VitalKind::Stamina),
        5 | 6 | 9 => Some(VitalKind::Mana),
        _ => None,
    }
}

/// The primary attribute an id names.
#[must_use]
pub fn attribute_id_to_kind(attribute_id: u32) -> Option<AttributeKind> {
    AttributeKind::ALL
        .into_iter()
        .find(|kind| kind.id() == attribute_id)
}

/// A primary attribute's record.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AttributeSnapshot {
    /// Ranks bought with experience.
    pub ranks: u32,
    /// Starting value.
    pub start: u32,
    /// Experience spent.
    pub xp: u32,
}

impl AttributeSnapshot {
    /// The attribute's value before spells: ranks plus start.
    #[must_use]
    pub fn current(&self) -> u32 {
        self.ranks.wrapping_add(self.start)
    }
}

/// A vital's record.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VitalSnapshot {
    /// Ranks bought with experience.
    pub ranks: u32,
    /// Starting value.
    pub start: u32,
    /// Experience spent.
    pub xp: u32,
    /// Current value.
    pub current: u32,
}

/// A skill's record.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkillSnapshot {
    /// Skill id.
    pub skill_id: u32,
    /// Ranks bought with experience.
    pub ranks: u32,
    /// Advancement class (1 untrained, 2 trained, 3 specialized).
    pub status: u32,
    /// Experience spent.
    pub xp: u32,
    /// Starting value.
    pub init: u32,
    /// Resistance at last use.
    pub resistance: u32,
    /// When last used.
    pub last_used: f64,
    /// The attribute formula term.
    pub formula_bonus: u32,
}

impl SkillSnapshot {
    /// Formula term plus starting value plus ranks.
    #[must_use]
    pub fn base_level(&self) -> u32 {
        self.formula_bonus
            .wrapping_add(self.init)
            .wrapping_add(self.ranks)
    }

    /// The level before spells (the same as [`base_level`](Self::base_level)).
    #[must_use]
    pub fn current_level(&self) -> u32 {
        self.base_level()
    }
}

/// What an attribute update affects.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AttributeChange {
    /// The attribute.
    pub kind: AttributeKind,
    /// The vitals whose maximum derives from it (Endurance: health and
    /// stamina; Self: mana).
    pub affected_vitals: &'static [VitalKind],
}

/// Computes a skill's attribute term from its id, advancement class and
/// the attribute values by id (see
/// [`LiveSkillCreditResolver`](super::LiveSkillCreditResolver)).
pub type SkillFormulaBonusResolver =
    Arc<dyn Fn(u32, u32, &BTreeMap<u32, u32>) -> u32 + Send + Sync>;

/// The local player's character numbers.
#[derive(Clone, Default)]
pub struct LocalPlayerState {
    health: Option<VitalSnapshot>,
    stamina: Option<VitalSnapshot>,
    mana: Option<VitalSnapshot>,
    attributes: [Option<AttributeSnapshot>; 6],
    skills: BTreeMap<u32, SkillSnapshot>,
    int_properties: BTreeMap<u32, i32>,
    spellbook: Option<Spellbook>,
    skill_formula_bonus_resolver: Option<SkillFormulaBonusResolver>,
}

impl std::fmt::Debug for LocalPlayerState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LocalPlayerState")
            .field("health", &self.health)
            .field("stamina", &self.stamina)
            .field("mana", &self.mana)
            .field("attributes", &self.attributes)
            .field("skills", &self.skills)
            .field("int_properties", &self.int_properties)
            .field("spellbook", &self.spellbook)
            .field(
                "skill_formula_bonus_resolver",
                &self.skill_formula_bonus_resolver.is_some(),
            )
            .finish()
    }
}

impl LocalPlayerState {
    /// No character yet. Without a spellbook, attributes, skills and vitals
    /// read unenchanted.
    #[must_use]
    pub fn new(spellbook: Option<Spellbook>) -> Self {
        Self {
            spellbook,
            ..Self::default()
        }
    }

    /// The spellbook whose enchantments apply.
    #[must_use]
    pub fn spellbook(&self) -> Option<&Spellbook> {
        self.spellbook.as_ref()
    }

    /// The spellbook, to apply enchantment updates.
    pub fn spellbook_mut(&mut self) -> Option<&mut Spellbook> {
        self.spellbook.as_mut()
    }

    /// Sets the resolver that recomputes skill formula terms. The runtime
    /// installs one built on the skill table.
    pub fn set_skill_formula_bonus_resolver(
        &mut self,
        resolver: Option<SkillFormulaBonusResolver>,
    ) {
        self.skill_formula_bonus_resolver = resolver;
    }

    /// A vital's record, `None` until received.
    #[must_use]
    pub fn get(&self, kind: VitalKind) -> Option<VitalSnapshot> {
        match kind {
            VitalKind::Health => self.health,
            VitalKind::Stamina => self.stamina,
            VitalKind::Mana => self.mana,
        }
    }

    fn vital_slot(&mut self, kind: VitalKind) -> &mut Option<VitalSnapshot> {
        match kind {
            VitalKind::Health => &mut self.health,
            VitalKind::Stamina => &mut self.stamina,
            VitalKind::Mana => &mut self.mana,
        }
    }

    /// A primary attribute's record, `None` until received.
    #[must_use]
    pub fn get_attribute(&self, kind: AttributeKind) -> Option<AttributeSnapshot> {
        self.attributes[kind.index()]
    }

    /// The attribute after spells (attributes ignore vitae), floored at 1
    /// below 10 and at 10 otherwise; unenchanted without a spellbook.
    #[must_use]
    pub fn get_effective_attribute(&self, kind: AttributeKind) -> Option<i32> {
        let base = self.get_attribute(kind)?.current();
        Some(match &self.spellbook {
            None => base.cast_signed(),
            Some(book) => enchant_attribute(&book.get_attribute_mod(kind.id()), base),
        })
    }

    /// The skill's effective level.
    #[must_use]
    pub fn get_effective_skill(&self, skill_id: u32) -> Option<i32> {
        self.get_skill_value(skill_id, None)
            .map(|value| value.effective_level)
    }

    /// The skill's displayed values, `None` until the skill is received.
    /// `augmentations` overrides the ones read from the character's
    /// properties.
    #[must_use]
    pub fn get_skill_value(
        &self,
        skill_id: u32,
        augmentations: Option<AugmentationBonuses>,
    ) -> Option<SkillValue> {
        let skill = self.skills.get(&skill_id)?;
        let augmentations = augmentations.unwrap_or_else(|| self.augmentations());
        let (modifier, vitae) = match &self.spellbook {
            Some(book) => (
                book.get_skill_mod(skill_id),
                get_vitae_multiplier(book.active_enchantments()),
            ),
            None => (VitalMod::IDENTITY, 1.0),
        };
        let intrinsic = saturating_i32(skill.current_level());
        Some(calculate(
            intrinsic,
            intrinsic.wrapping_add(self.attribute_enchantment_skill_delta(skill_id)),
            skill_id,
            skill.status,
            &augmentations,
            modifier,
            vitae,
        ))
    }

    /// The augmentations the character's properties grant.
    #[must_use]
    pub fn augmentations(&self) -> AugmentationBonuses {
        AugmentationBonuses::from_int_properties(|key| self.int_property(key))
    }

    /// How much attribute spells raise the skill's formula term: the term
    /// from enchanted attributes minus the cached term. Zero without a
    /// resolver, a spellbook or the skill.
    #[must_use]
    pub fn attribute_enchantment_skill_delta(&self, skill_id: u32) -> i32 {
        let (Some(resolver), Some(_), Some(skill)) = (
            &self.skill_formula_bonus_resolver,
            &self.spellbook,
            self.skills.get(&skill_id),
        ) else {
            return 0;
        };
        let enchanted = resolver(
            skill_id,
            skill.status,
            &self.enchanted_attribute_currents_by_id(),
        );
        saturating_i32(enchanted) - saturating_i32(skill.formula_bonus)
    }

    /// Each received attribute's value after spells, by id (negative reads
    /// as zero).
    #[must_use]
    pub fn enchanted_attribute_currents_by_id(&self) -> BTreeMap<u32, u32> {
        AttributeKind::ALL
            .into_iter()
            .filter(|&kind| self.get_attribute(kind).is_some())
            .map(|kind| {
                let value = self.get_effective_attribute(kind).unwrap_or(0);
                (kind.id(), value.max(0).cast_unsigned())
            })
            .collect()
    }

    /// Each received attribute's unenchanted value, by id.
    #[must_use]
    pub fn attribute_currents_by_id(&self) -> BTreeMap<u32, u32> {
        AttributeKind::ALL
            .into_iter()
            .filter_map(|kind| Some((kind.id(), self.get_attribute(kind)?.current())))
            .collect()
    }

    /// What vitae takes off the skill.
    #[must_use]
    pub fn get_skill_vitae_modifier(&self, skill_id: u32) -> i32 {
        self.get_skill_value(skill_id, None)
            .map_or(0, |value| value.vitae_modifier)
    }

    /// The character's int properties.
    #[must_use]
    pub fn int_properties(&self) -> &BTreeMap<u32, i32> {
        &self.int_properties
    }

    /// One int property (zero when absent).
    #[must_use]
    pub fn int_property(&self, key: PropertyInt) -> i32 {
        self.int_properties.get(&key.0).copied().unwrap_or(0)
    }

    /// Every received skill, by id.
    #[must_use]
    pub fn skills(&self) -> &BTreeMap<u32, SkillSnapshot> {
        &self.skills
    }

    /// One skill's record.
    #[must_use]
    pub fn get_skill(&self, skill_id: u32) -> Option<SkillSnapshot> {
        self.skills.get(&skill_id).copied()
    }

    /// The vital's maximum: ranks + start + the attribute term (from
    /// enchanted attributes) + the health bonus (gear, Enlightenment), then
    /// the vital spells; floored at 5 (1 when the unbuffed maximum is below
    /// 5) and rounded half up, so a fractional maximum agrees with the
    /// rounded vital values, including those reduced by vitae.
    #[must_use]
    pub fn get_max_approx(&self, kind: VitalKind) -> Option<u32> {
        let unbuffed = self.max_before_secondary_enchantments(kind)?;
        if unbuffed == 0 {
            return Some(0);
        }
        let modifier = self.spellbook.as_ref().map_or(VitalMod::IDENTITY, |book| {
            book.get_vital_mod(stat_key_for(kind))
        });
        let mut buffed = modifier.apply(unbuffed as f32);
        let floor = if unbuffed >= 5 { 5.0 } else { 1.0 };
        if buffed < floor {
            buffed = floor;
        }
        Some((f64::from(buffed) + 0.5) as u32)
    }

    /// The vital's maximum without any spell: ranks + start + the attribute
    /// term from unenchanted attributes + the health bonus.
    #[must_use]
    pub fn get_base_max_approx(&self, kind: VitalKind) -> Option<u32> {
        self.vital_max(kind, false)
    }

    fn max_before_secondary_enchantments(&self, kind: VitalKind) -> Option<u32> {
        self.vital_max(kind, true)
    }

    fn vital_max(&self, kind: VitalKind, effective: bool) -> Option<u32> {
        let vital = self.get(kind)?;
        Some(
            vital
                .ranks
                .wrapping_add(vital.start)
                .wrapping_add(self.attribute_contribution(kind, effective))
                .wrapping_add(self.health_bonus(kind)),
        )
    }

    /// What vitae takes off the vital's base maximum: the penalized base
    /// rounded half up, less the base (zero without vitae). Rounded as
    /// [`Self::get_max_approx`] rounds, so the two agree.
    #[must_use]
    pub fn get_vital_vitae_modifier(&self, kind: VitalKind) -> i32 {
        let (Some(book), Some(base)) = (&self.spellbook, self.get_base_max_approx(kind)) else {
            return 0;
        };
        let vitae = get_vitae_multiplier(book.active_enchantments());
        if vitae >= 1.0 {
            return 0;
        }
        let penalized = base as f32 * vitae;
        ((f64::from(penalized) + 0.5) as i32).wrapping_sub(base.cast_signed())
    }

    /// Health as a fraction of its maximum, `None` until known.
    #[must_use]
    pub fn health_percent(&self) -> Option<f32> {
        self.percent(VitalKind::Health)
    }

    /// Stamina as a fraction of its maximum.
    #[must_use]
    pub fn stamina_percent(&self) -> Option<f32> {
        self.percent(VitalKind::Stamina)
    }

    /// Mana as a fraction of its maximum.
    #[must_use]
    pub fn mana_percent(&self) -> Option<f32> {
        self.percent(VitalKind::Mana)
    }

    fn percent(&self, kind: VitalKind) -> Option<f32> {
        let vital = self.get(kind)?;
        let max = self.get_max_approx(kind).filter(|&max| max != 0)?;
        Some((vital.current as f32 / max as f32).clamp(0.0, 1.0))
    }

    /// A full vital record arrived. Returns the vital, `None` for an
    /// unknown id.
    pub fn on_vital_update(
        &mut self,
        vital_id: u32,
        ranks: u32,
        start: u32,
        xp: u32,
        current: u32,
    ) -> Option<VitalKind> {
        let kind = vital_id_to_kind(vital_id)?;
        *self.vital_slot(kind) = Some(VitalSnapshot {
            ranks,
            start,
            xp,
            current,
        });
        Some(kind)
    }

    /// A vital's current value changed. Ignored before the full record.
    pub fn on_vital_current(&mut self, vital_id: u32, current: u32) -> Option<VitalKind> {
        let kind = vital_id_to_kind(vital_id)?;
        let slot = self.vital_slot(kind).as_mut()?;
        slot.current = current;
        Some(kind)
    }

    /// A primary attribute's record arrived. Every skill formula term is
    /// recomputed; the change names the vitals whose maximum derives from
    /// the attribute.
    pub fn on_attribute_update(
        &mut self,
        attribute_id: u32,
        ranks: u32,
        start: u32,
        xp: u32,
    ) -> Option<AttributeChange> {
        let kind = attribute_id_to_kind(attribute_id)?;
        self.attributes[kind.index()] = Some(AttributeSnapshot { ranks, start, xp });
        self.recompute_skill_formula_bonuses();
        let affected_vitals: &'static [VitalKind] = match kind {
            AttributeKind::Endurance => &[VitalKind::Health, VitalKind::Stamina],
            AttributeKind::Self_ => &[VitalKind::Mana],
            _ => &[],
        };
        Some(AttributeChange {
            kind,
            affected_vitals,
        })
    }

    /// Replaces the character's int properties (from the character
    /// description).
    pub fn on_int_properties(&mut self, properties: BTreeMap<u32, i32>) {
        self.int_properties = properties;
    }

    /// One int property changed.
    pub fn on_int_property_update(&mut self, key: u32, value: i32) {
        self.int_properties.insert(key, value);
    }

    /// A skill record from the character description, with its formula
    /// term.
    #[allow(clippy::too_many_arguments)]
    pub fn on_skill_update(
        &mut self,
        skill_id: u32,
        ranks: u32,
        status: u32,
        xp: u32,
        init: u32,
        resistance: u32,
        last_used: f64,
        formula_bonus: u32,
    ) {
        self.skills.insert(
            skill_id,
            SkillSnapshot {
                skill_id,
                ranks,
                status,
                xp,
                init,
                resistance,
                last_used,
                formula_bonus,
            },
        );
    }

    /// A skill update from the server (no formula term on the wire): the
    /// resolver computes the term; without one the previous term is kept.
    #[allow(clippy::too_many_arguments)]
    pub fn on_skill_wire_update(
        &mut self,
        skill_id: u32,
        ranks: u32,
        status: u32,
        xp: u32,
        init: u32,
        resistance: u32,
        last_used: f64,
    ) {
        let formula_bonus = match &self.skill_formula_bonus_resolver {
            Some(resolver) => resolver(skill_id, status, &self.attribute_currents_by_id()),
            None => self.skills.get(&skill_id).map_or(0, |s| s.formula_bonus),
        };
        self.on_skill_update(
            skill_id,
            ranks,
            status,
            xp,
            init,
            resistance,
            last_used,
            formula_bonus,
        );
    }

    /// Recomputes every skill's formula term from the current attributes.
    pub fn recompute_skill_formula_bonuses(&mut self) {
        let Some(resolver) = self.skill_formula_bonus_resolver.clone() else {
            return;
        };
        if self.skills.is_empty() {
            return;
        }
        let currents = self.attribute_currents_by_id();
        for skill in self.skills.values_mut() {
            skill.formula_bonus = resolver(skill.skill_id, skill.status, &currents);
        }
    }

    /// Run and Jump levels before spells (formula + start + ranks), -1 for
    /// a skill not yet received.
    #[must_use]
    pub fn movement_skill_totals(&self) -> (i32, i32) {
        let total = |id: u32| {
            self.skills
                .get(&id)
                .map_or(-1, |s| s.base_level().cast_signed())
        };
        (total(SKILL_RUN), total(SKILL_JUMP))
    }

    /// Forgets the character (vitals, attributes, skills, properties). The
    /// spellbook and resolver stay.
    pub fn clear(&mut self) {
        self.health = None;
        self.stamina = None;
        self.mana = None;
        self.attributes = [None; 6];
        self.skills.clear();
        self.int_properties.clear();
    }

    fn attribute_contribution(&self, kind: VitalKind, effective: bool) -> u32 {
        let endurance = self.attribute_current(AttributeKind::Endurance, effective);
        match kind {
            // Health's formula is (Endurance / 2) rounded half up.
            VitalKind::Health => endurance / 2 + (endurance & 1),
            VitalKind::Stamina => endurance,
            VitalKind::Mana => self.attribute_current(AttributeKind::Self_, effective),
        }
    }

    fn attribute_current(&self, kind: AttributeKind, effective: bool) -> u32 {
        let Some(attribute) = self.get_attribute(kind) else {
            return 0;
        };
        if !effective || self.spellbook.is_none() {
            return attribute.current();
        }
        self.get_effective_attribute(kind)
            .unwrap_or(0)
            .max(0)
            .cast_unsigned()
    }

    // Health also counts the gear Max Health rating and 2 per Enlightenment
    // level, both before the vital spells and vitae, as the server does.
    fn health_bonus(&self, kind: VitalKind) -> u32 {
        if kind != VitalKind::Health {
            return 0;
        }
        let gear = self.int_property(PropertyInt::GEAR_MAX_HEALTH).max(0);
        let enlightenment = self.int_property(PropertyInt::ENLIGHTENMENT).max(0);
        gear.cast_unsigned()
            .wrapping_add(enlightenment.cast_unsigned().wrapping_mul(2))
    }
}

fn stat_key_for(kind: VitalKind) -> u32 {
    match kind {
        VitalKind::Health => STAT_KEY_MAX_HEALTH,
        VitalKind::Stamina => STAT_KEY_MAX_STAMINA,
        VitalKind::Mana => STAT_KEY_MAX_MANA,
    }
}

fn saturating_i32(value: u32) -> i32 {
    i32::try_from(value).unwrap_or(i32::MAX)
}

#[cfg(test)]
#[path = "local_player_tests.rs"]
mod tests;
