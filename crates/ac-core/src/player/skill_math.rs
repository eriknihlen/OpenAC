//! A skill's displayed values: the unenchanted level (intrinsic plus the
//! permanent augmentation bonuses), the effective level (spells applied,
//! then the augmentations that come after spells), and what vitae takes
//! off.

use crate::properties::PropertyInt;
use crate::spells::{VitalMod, enchant_skill, vitae_modifier};

/// The skill augmentations a character has bought.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AugmentationBonuses {
    /// Added to every skill.
    pub all_skills: i32,
    /// Jack of all trades: +5 after spells.
    pub jack_of_all_trades: bool,
    /// Twice this is added to specialized skills after spells.
    pub skilled_specialized: i32,
    /// +10 to the melee weapon skills.
    pub skilled_melee: bool,
    /// +10 to missile weapons.
    pub skilled_missile: bool,
    /// +10 to the magic schools.
    pub skilled_magic: bool,
}

impl AugmentationBonuses {
    /// Reads the augmentations from the character's int properties (a
    /// missing property reads as zero).
    #[must_use]
    pub fn from_int_properties(get_int: impl Fn(PropertyInt) -> i32) -> Self {
        Self {
            all_skills: get_int(PropertyInt::LUM_AUG_ALL_SKILLS).max(0),
            jack_of_all_trades: get_int(PropertyInt::AUGMENTATION_JACK_OF_ALL_TRADES) > 0,
            skilled_specialized: get_int(PropertyInt::LUM_AUG_SKILLED_SPEC).max(0),
            skilled_melee: get_int(PropertyInt::AUGMENTATION_SKILLED_MELEE) > 0,
            skilled_missile: get_int(PropertyInt::AUGMENTATION_SKILLED_MISSILE) > 0,
            skilled_magic: get_int(PropertyInt::AUGMENTATION_SKILLED_MAGIC) > 0,
        }
    }

    /// The bonus added before spells: all-skills plus the category bonus
    /// (melee: two-handed, heavy, light, finesse weapons, dual wield;
    /// missile: missile weapons; magic: the five schools).
    #[must_use]
    pub fn before_enchantments(&self, skill_id: u32) -> i32 {
        let category = match skill_id {
            0x29 | 0x2C | 0x2D | 0x2E | 0x31 if self.skilled_melee => 10,
            0x2F if self.skilled_missile => 10,
            0x1F | 0x20 | 0x21 | 0x22 | 0x2B if self.skilled_magic => 10,
            _ => 0,
        };
        self.all_skills.saturating_add(category)
    }

    /// The bonus added after spells: jack of all trades, plus twice the
    /// specialized augmentation for a specialized skill (class 3).
    #[must_use]
    pub fn after_enchantments(&self, advancement_class: u32) -> i32 {
        let mut result: i32 = if self.jack_of_all_trades { 5 } else { 0 };
        if advancement_class == 3 {
            result = result.saturating_add(self.skilled_specialized.saturating_mul(2));
        }
        result
    }
}

/// A skill's displayed values.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SkillValue {
    /// Intrinsic level plus the pre-spell augmentations.
    pub unenchanted_level: i32,
    /// After spells and the post-spell augmentations.
    pub effective_level: i32,
    /// What vitae takes off the unenchanted level (zero or negative).
    pub vitae_modifier: i32,
}

/// A skill's values. `intrinsic_level` is the formula term plus starting
/// value plus ranks; `enchanted_intrinsic_level` is the same with the
/// formula term computed from enchanted attributes, and feeds only the
/// effective level. `enchantment` is the skill's modifier with vitae
/// folded in.
#[must_use]
pub fn calculate(
    intrinsic_level: i32,
    enchanted_intrinsic_level: i32,
    skill_id: u32,
    advancement_class: u32,
    augmentations: &AugmentationBonuses,
    enchantment: VitalMod,
    vitae_multiplier: f32,
) -> SkillValue {
    let before = augmentations.before_enchantments(skill_id);
    let unenchanted = intrinsic_level.max(0).saturating_add(before);
    let enchanted_base = enchanted_intrinsic_level.max(0).saturating_add(before);
    let enchanted = enchant_skill(&enchantment, enchanted_base.cast_unsigned());
    let effective = enchanted.saturating_add(augmentations.after_enchantments(advancement_class));
    SkillValue {
        unenchanted_level: unenchanted,
        effective_level: effective,
        vitae_modifier: vitae_modifier(vitae_multiplier, unenchanted.cast_unsigned()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Player/PlayerSkillMathTests.cs`.

    #[test]
    fn keeps_augmentation_ordering() {
        let augmentations = AugmentationBonuses {
            all_skills: 3,
            jack_of_all_trades: true,
            skilled_specialized: 4,
            skilled_magic: true,
            ..AugmentationBonuses::default()
        };
        let value = calculate(
            100,
            100,
            0x1F,
            3,
            &augmentations,
            VitalMod::new(0.5, 0.0),
            0.8,
        );
        assert_eq!(value.unenchanted_level, 113); // 100 + all 3 + magic 10
        assert_eq!(value.effective_level, 69); // trunc(113 * .5) + JOAT 5 + spec 8
        assert_eq!(value.vitae_modifier, -23); // trunc(113 * .8) - 113
    }

    #[test]
    fn enchanted_intrinsic_feeds_only_the_effective_level() {
        let augmentations = AugmentationBonuses {
            skilled_magic: true,
            ..AugmentationBonuses::default()
        };
        let value = calculate(
            331,
            353,
            0x1F,
            2,
            &augmentations,
            VitalMod::new(1.0, 50.0),
            1.0,
        );
        assert_eq!(value.unenchanted_level, 341);
        assert_eq!(value.effective_level, 413);
        assert_eq!(value.vitae_modifier, 0);
    }

    #[test]
    fn category_switch() {
        for (skill, melee, missile, magic, bonus) in [
            (0x29, true, false, false, 10),
            (0x2F, false, true, false, 10),
            (0x22, false, false, true, 10),
            (0x18, true, true, true, 0),
        ] {
            let augmentations = AugmentationBonuses {
                all_skills: 2,
                skilled_melee: melee,
                skilled_missile: missile,
                skilled_magic: magic,
                ..AugmentationBonuses::default()
            };
            assert_eq!(
                augmentations.before_enchantments(skill),
                2 + bonus,
                "skill {skill:#X}"
            );
        }
    }

    #[test]
    fn from_int_properties_maps_named_qualities() {
        let ints = BTreeMap::from([
            (PropertyInt::LUM_AUG_ALL_SKILLS.0, 7),
            (PropertyInt::AUGMENTATION_JACK_OF_ALL_TRADES.0, 1),
            (PropertyInt::LUM_AUG_SKILLED_SPEC.0, 3),
            (PropertyInt::AUGMENTATION_SKILLED_MISSILE.0, 2),
        ]);
        let value =
            AugmentationBonuses::from_int_properties(|key| ints.get(&key.0).copied().unwrap_or(0));
        assert_eq!(value.all_skills, 7);
        assert!(value.jack_of_all_trades);
        assert_eq!(value.skilled_specialized, 3);
        assert!(value.skilled_missile);
        assert!(!value.skilled_melee);
        assert!(!value.skilled_magic);
    }
}
