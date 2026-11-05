//! Builds a [`SpellMetadata`] from the portal dat's spell record and the
//! spell component table. The runtime reads the dat and passes the fields
//! as plain values.

use std::collections::HashMap;

use super::formula::{
    MAX_FORMULA_COMPONENTS, get_targeting_type, inq_spell_level_by_rough_heuristic,
};
use super::metadata::{
    ComponentType, MagicSchool, SpellComponentSet, SpellFlags, SpellMetadata, SpellType,
};

/// The spell table record's fields the client uses.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpellBaseRecord {
    /// Name.
    pub name: String,
    /// Description.
    pub description: String,
    /// Formula components.
    pub components: Vec<u32>,
    /// School (1 war .. 5 void).
    pub school: u32,
    /// Icon.
    pub icon: u32,
    /// Category (stacking family).
    pub category: u32,
    /// Bit field.
    pub bitfield: u32,
    /// Base mana.
    pub base_mana: u32,
    /// Range constant.
    pub base_range_constant: f32,
    /// Range modifier.
    pub base_range_mod: f32,
    /// Power.
    pub power: u32,
    /// Economy modifier.
    pub spell_economy_mod: f32,
    /// Formula version.
    pub formula_version: u32,
    /// Component loss.
    pub component_loss: f32,
    /// Meta spell type.
    pub meta_spell_type: u32,
    /// Duration (seconds) of the meta spell, for enchantments.
    pub duration: f64,
    /// Caster effect script.
    pub caster_effect: u32,
    /// Target effect script.
    pub target_effect: u32,
    /// Fizzle effect script.
    pub fizzle_effect: u32,
    /// Recovery interval.
    pub recovery_interval: f64,
    /// Recovery amount.
    pub recovery_amount: f32,
    /// Display order.
    pub display_order: u32,
    /// Target type without components.
    pub non_component_target_type: u32,
    /// Mana per extra target.
    pub mana_mod: u32,
    /// Degrade modifier.
    pub degrade_modifier: f32,
    /// Degrade limit.
    pub degrade_limit: f32,
    /// Portal lifetime.
    pub portal_lifetime: f64,
}

/// One entry of the spell component table.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SpellComponentInfo {
    /// What kind of reagent it is.
    pub component_type: ComponentType,
    /// The syllable it adds to the spell words.
    pub text: String,
}

/// A spell record field does not fit the client's signed 32-bit field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SpellFieldOutOfRange {
    /// Which field.
    pub field: &'static str,
    /// Its value.
    pub value: u32,
}

impl std::fmt::Display for SpellFieldOutOfRange {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "spell field {} = {} does not fit a signed 32-bit value",
            self.field, self.value
        )
    }
}

impl std::error::Error for SpellFieldOutOfRange {}

/// The one spell whose saying is not built from its components.
const CURSE_OF_RAVEN_FURY: &str = "Curse of Raven Fury";
const CURSE_OF_RAVEN_FURY_SAYING: &str = "tugakquati";

/// Projects one spell record.
///
/// # Errors
/// [`SpellFieldOutOfRange`] when base mana, power or meta type exceeds
/// `i32::MAX`.
pub fn project_spell(
    spell_id: u32,
    spell: &SpellBaseRecord,
    components: Option<&HashMap<u32, SpellComponentInfo>>,
) -> Result<SpellMetadata, SpellFieldOutOfRange> {
    let formula: Vec<u32> = spell
        .components
        .iter()
        .copied()
        .take(MAX_FORMULA_COMPONENTS)
        .collect();
    let flags = SpellFlags(spell.bitfield);
    let formula_target_type = get_targeting_type(&formula);
    let self_targeted = flags.contains(SpellFlags::SELF_TARGETED);
    let beneficial = flags.contains(SpellFlags::BENEFICIAL);
    let signed = |field: &'static str, value: u32| {
        i32::try_from(value).map_err(|_| SpellFieldOutOfRange { field, value })
    };

    Ok(SpellMetadata {
        spell_id,
        name: spell.name.clone(),
        school: school_name(spell.school).to_owned(),
        family: spell.category,
        icon_id: spell.icon,
        spell_words: build_spell_words(&formula, components),
        // The client keeps durations in single precision.
        duration: spell.duration as f32,
        mana_cost: signed("base_mana", spell.base_mana)?,
        is_debuff: flags.contains(SpellFlags::REVERSED),
        is_fellowship: flags.contains(SpellFlags::FELLOWSHIP_SPELL),
        description: spell.description.clone(),
        sort_key: spell.display_order.cast_signed(),
        difficulty: signed("power", spell.power)?,
        flags,
        generation: inq_spell_level_by_rough_heuristic(&formula),
        is_fast_windup: flags.contains(SpellFlags::FAST_CAST),
        is_offensive: !beneficial && !self_targeted,
        is_untargeted: formula_target_type == 0,
        speed: 0.0,
        caster_effect: spell.caster_effect,
        target_effect: spell.target_effect,
        target_mask: formula_target_type,
        spell_type: SpellType(signed("meta_spell_type", spell.meta_spell_type)?.cast_unsigned()),
        saying: build_saying(&spell.name, &formula, components),
        component_set: build_component_set(&formula, components),
        school_id: school_id(spell.school),
        formula_components: formula,
        formula_version: spell.formula_version,
        component_loss: spell.component_loss,
        base_range_constant: spell.base_range_constant,
        base_range_modifier: spell.base_range_mod,
        spell_economy_modifier: spell.spell_economy_mod,
        fizzle_effect: spell.fizzle_effect,
        recovery_interval: spell.recovery_interval,
        recovery_amount: spell.recovery_amount,
        non_component_target_type: spell.non_component_target_type,
        formula_target_type,
        mana_modifier: spell.mana_mod,
        degrade_modifier: spell.degrade_modifier,
        degrade_limit: spell.degrade_limit,
        portal_lifetime: spell.portal_lifetime,
    })
}

/// The school's display name.
#[must_use]
pub fn school_name(school: u32) -> &'static str {
    match MagicSchool(school) {
        MagicSchool::WAR_MAGIC => "War Magic",
        MagicSchool::LIFE_MAGIC => "Life Magic",
        MagicSchool::ITEM_ENCHANTMENT => "Item Enchantment",
        MagicSchool::CREATURE_ENCHANTMENT => "Creature Enchantment",
        MagicSchool::VOID_MAGIC => "Void Magic",
        _ => "None",
    }
}

fn school_id(school: u32) -> MagicSchool {
    match MagicSchool(school) {
        known @ (MagicSchool::WAR_MAGIC
        | MagicSchool::LIFE_MAGIC
        | MagicSchool::ITEM_ENCHANTMENT
        | MagicSchool::CREATURE_ENCHANTMENT
        | MagicSchool::VOID_MAGIC) => known,
        _ => MagicSchool::NONE,
    }
}

fn build_saying(
    name: &str,
    formula: &[u32],
    components: Option<&HashMap<u32, SpellComponentInfo>>,
) -> String {
    if name == CURSE_OF_RAVEN_FURY {
        return CURSE_OF_RAVEN_FURY_SAYING.to_owned();
    }
    let Some(components) = components else {
        return String::new();
    };
    formula
        .iter()
        .filter_map(|id| components.get(id))
        .map(|component| component.text.as_str())
        .collect()
}

fn build_component_set(
    formula: &[u32],
    components: Option<&HashMap<u32, SpellComponentInfo>>,
) -> SpellComponentSet {
    let Some(components) = components else {
        return SpellComponentSet::default();
    };
    let mut set = SpellComponentSet::default();
    for &id in formula {
        let Some(component) = components.get(&id) else {
            continue;
        };
        match component.component_type {
            ComponentType::HERB => set.herb = id,
            ComponentType::POWDER => set.powder = id,
            ComponentType::POTION => set.potion = id,
            ComponentType::TALISMAN => set.talisman = id,
            _ => {}
        }
    }
    set
}

/// "Herb Powderpotion": the herb's syllable, a space, then the powder's
/// syllable followed by the potion's in lower case, capitalised.
fn build_spell_words(
    formula: &[u32],
    components: Option<&HashMap<u32, SpellComponentInfo>>,
) -> String {
    let Some(components) = components else {
        return String::new();
    };
    let (mut first, mut second, mut third) = ("", "", "");
    for id in formula {
        let Some(component) = components.get(id) else {
            continue;
        };
        match component.component_type {
            ComponentType::HERB => first = &component.text,
            ComponentType::POWDER => second = &component.text,
            ComponentType::POTION => third = &component.text,
            _ => {}
        }
    }

    let tail = format!("{second}{}", third.to_lowercase());
    let tail = capitalise_first(&tail);
    format!("{first} {tail}").trim().to_owned()
}

/// Upper-cases the first character when it maps to exactly one character.
fn capitalise_first(text: &str) -> String {
    let mut chars = text.chars();
    let Some(first) = chars.next() else {
        return String::new();
    };
    let mut upper = first.to_uppercase();
    let replacement = match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => first,
    };
    let mut result = String::with_capacity(text.len());
    result.push(replacement);
    result.push_str(chars.as_str());
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Content.Tests/Spells/RetailSpellMetadataProjectorTests.cs`.

    fn component(component_type: ComponentType, text: &str) -> SpellComponentInfo {
        SpellComponentInfo {
            component_type,
            text: text.to_owned(),
        }
    }

    #[test]
    fn preserves_fields_and_derives_presentation_metadata() {
        let components = HashMap::from([
            (10, component(ComponentType::HERB, "Malar")),
            (11, component(ComponentType::POWDER, "Caza")),
            (12, component(ComponentType::POTION, "El")),
        ]);
        let source = SpellBaseRecord {
            name: "Incantation of Test".to_owned(),
            description: "A projected retail spell.".to_owned(),
            components: vec![0xC1, 10, 11, 12, 63, 0x31],
            school: 1,
            icon: 0x0600_1234,
            category: 77,
            bitfield: (SpellFlags::RESISTABLE | SpellFlags::PROJECTILE | SpellFlags::FAST_CAST)
                .bits(),
            base_mana: 50,
            base_range_constant: 40.0,
            base_range_mod: 0.25,
            power: 400,
            spell_economy_mod: -1.0,
            formula_version: 3,
            component_loss: 0.2,
            meta_spell_type: SpellType::PROJECTILE.bits(),
            caster_effect: 6,
            target_effect: 7,
            fizzle_effect: 8,
            recovery_interval: 1.5,
            recovery_amount: 2.5,
            display_order: 1234,
            non_component_target_type: 0x10,
            mana_mod: 14,
            ..SpellBaseRecord::default()
        };

        let actual = project_spell(5000, &source, Some(&components)).expect("in range");
        assert_eq!(actual.name, "Incantation of Test");
        assert_eq!(actual.description, "A projected retail spell.");
        assert_eq!(actual.school, "War Magic");
        assert_eq!(actual.school_id, MagicSchool::WAR_MAGIC);
        assert_eq!(actual.family, 77);
        assert_eq!(actual.icon_id, 0x0600_1234);
        assert_eq!(actual.spell_words, "Malar Cazael");
        assert_eq!(actual.saying, "MalarCazaEl");
        assert_eq!(actual.generation, 8);
        assert_eq!(actual.target_mask, 0x10);
        assert!(!actual.is_untargeted);
        assert!(actual.is_projectile());
        assert!(actual.is_fast_windup);
        assert!(actual.is_offensive);
        assert_eq!(actual.formula_components, source.components);
        assert_eq!(actual.formula_version, 3);
        assert_eq!(actual.mana_modifier, 14);
        assert_eq!(actual.non_component_target_type, 0x10);
        assert_eq!(actual.formula_target_type, 0x10);
        assert_eq!(actual.difficulty, 400);
        assert_eq!(actual.sort_key, 1234);
    }

    fn fixture(components: &[u32]) -> SpellBaseRecord {
        SpellBaseRecord {
            name: "Fixture".to_owned(),
            description: "Fixture".to_owned(),
            components: components.to_vec(),
            school: 4,
            bitfield: SpellFlags::SELF_TARGETED.bits(),
            category: 1,
            ..SpellBaseRecord::default()
        }
    }

    #[test]
    fn component_set_takes_herb_powder_potion_talisman() {
        let components = HashMap::from([
            (1, component(ComponentType::SCARAB, "")),
            (6, component(ComponentType::SCARAB, "")),
            (7, component(ComponentType::HERB, "Malar")),
            (33, component(ComponentType::POWDER, "Caza")),
            (44, component(ComponentType::POTION, "El")),
            (49, component(ComponentType::TALISMAN, "")),
            (60, component(ComponentType::TALISMAN, "")),
            (63, component(ComponentType::TAPER, "")),
            (72, component(ComponentType::TAPER, "")),
        ]);
        let set = |id: u32, formula: &[u32]| {
            project_spell(id, &fixture(formula), Some(&components))
                .expect("in range")
                .component_set
        };
        let weak_self = set(2, &[1, 7, 33, 44, 60]);
        let strong_self = set(0x0534, &[6, 63, 7, 72, 33, 44, 63, 60]);
        let strong_other = set(0x0539, &[6, 63, 7, 72, 33, 44, 63, 49]);
        let expected = |talisman| SpellComponentSet {
            herb: 7,
            powder: 33,
            potion: 44,
            talisman,
        };
        assert_eq!(weak_self, expected(60));
        assert_eq!(weak_self, strong_self);
        assert_ne!(weak_self, strong_other);
        assert_eq!(strong_other, expected(49));
    }

    #[test]
    fn without_a_component_table_words_and_set_are_empty() {
        let meta = project_spell(1, &fixture(&[1, 7]), None).expect("in range");
        assert_eq!(meta.spell_words, "");
        assert_eq!(meta.saying, "");
        assert_eq!(meta.component_set, SpellComponentSet::default());
        assert!(meta.is_untargeted);
        assert!(!meta.is_offensive);
    }

    #[test]
    fn raven_fury_has_its_fixed_saying() {
        let record = SpellBaseRecord {
            name: CURSE_OF_RAVEN_FURY.to_owned(),
            ..SpellBaseRecord::default()
        };
        assert_eq!(
            project_spell(1, &record, None).expect("ok").saying,
            "tugakquati"
        );
    }

    #[test]
    fn oversized_signed_fields_are_errors() {
        let record = SpellBaseRecord {
            power: u32::MAX,
            ..SpellBaseRecord::default()
        };
        assert_eq!(
            project_spell(1, &record, None),
            Err(SpellFieldOutOfRange {
                field: "power",
                value: u32::MAX
            })
        );
    }
}
