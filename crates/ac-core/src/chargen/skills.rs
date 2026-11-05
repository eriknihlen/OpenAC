//! Character-creation skills: the training state of each skill, the
//! credits spent, the score a skill would start with, and its name.

use std::collections::HashMap;

use super::attributes::ChargenAttributeValues;
use super::options::{ChargenOptions, ChargenSkillCost, ChargenSkillDetail};
use crate::player::{skill_names, try_calculate};

/// How far a skill is taken.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub enum ChargenSkillAdvancementClass {
    /// Not available.
    #[default]
    Inactive = 0,
    /// Usable untrained.
    Untrained = 1,
    /// Trained.
    Trained = 2,
    /// Specialized.
    Specialized = 3,
}

impl ChargenSkillAdvancementClass {
    /// The wire value.
    #[must_use]
    pub fn wire_value(self) -> u32 {
        self as u32
    }
}

/// A skill id outside 1..=54 was given to [`ChargenSkillAdvancementSet::set`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SkillIdOutOfRange(pub u32);

impl std::fmt::Display for SkillIdOutOfRange {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "Skill id must be in 1..{}, got {}.",
            ChargenSkillAdvancementSet::SLOT_COUNT - 1,
            self.0
        )
    }
}

impl std::error::Error for SkillIdOutOfRange {}

/// The training state of skills 1..=54 (slot 0 is reserved and always
/// inactive), as the character-creation message sends it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ChargenSkillAdvancementSet {
    slots: [ChargenSkillAdvancementClass; Self::SLOT_COUNT],
}

impl Default for ChargenSkillAdvancementSet {
    fn default() -> Self {
        Self {
            slots: [ChargenSkillAdvancementClass::Inactive; Self::SLOT_COUNT],
        }
    }
}

impl ChargenSkillAdvancementSet {
    /// Number of slots on the wire.
    pub const SLOT_COUNT: usize = 55;

    /// Every skill inactive.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A skill's state; inactive for slot 0 and ids out of range.
    #[must_use]
    pub fn get(&self, skill_id: u32) -> ChargenSkillAdvancementClass {
        match usize::try_from(skill_id) {
            Ok(index @ 1..Self::SLOT_COUNT) => self.slots[index],
            _ => ChargenSkillAdvancementClass::Inactive,
        }
    }

    /// Sets a skill's state.
    ///
    /// # Errors
    /// [`SkillIdOutOfRange`] for slot 0 or an id of 55 or more.
    pub fn set(
        &mut self,
        skill_id: u32,
        value: ChargenSkillAdvancementClass,
    ) -> Result<(), SkillIdOutOfRange> {
        match usize::try_from(skill_id) {
            Ok(index @ 1..Self::SLOT_COUNT) => {
                self.slots[index] = value;
                Ok(())
            }
            _ => Err(SkillIdOutOfRange(skill_id)),
        }
    }

    /// The 55 wire values.
    #[must_use]
    pub fn to_wire_classes(&self) -> Vec<u32> {
        self.slots.iter().map(|class| class.wire_value()).collect()
    }
}

/// Credits spent on trained and specialized skills: the heritage's cost
/// when it lists the skill, else the skill table's; a skill neither lists
/// costs nothing.
#[must_use]
pub fn compute_spent(
    advancement: &ChargenSkillAdvancementSet,
    costs_by_skill_id: &HashMap<u32, ChargenSkillCost>,
    global_costs_by_skill_id: &HashMap<u32, ChargenSkillCost>,
) -> i64 {
    let mut spent = 0i64;
    for skill_id in 1..ChargenSkillAdvancementSet::SLOT_COUNT as u32 {
        let class = advancement.get(skill_id);
        if !matches!(
            class,
            ChargenSkillAdvancementClass::Trained | ChargenSkillAdvancementClass::Specialized
        ) {
            continue;
        }
        let Some(cost) = costs_by_skill_id
            .get(&skill_id)
            .or_else(|| global_costs_by_skill_id.get(&skill_id))
        else {
            continue;
        };
        spent += i64::from(if class == ChargenSkillAdvancementClass::Specialized {
            cost.primary_cost
        } else {
            cost.normal_cost
        });
    }
    spent
}

/// Total credits minus the credits spent (may go negative, unlike
/// attributes).
#[must_use]
pub fn remaining_skill_credits(
    total_skill_credits: u32,
    advancement: &ChargenSkillAdvancementSet,
    costs_by_skill_id: &HashMap<u32, ChargenSkillCost>,
    global_costs_by_skill_id: &HashMap<u32, ChargenSkillCost>,
) -> i64 {
    i64::from(total_skill_credits)
        - compute_spent(advancement, costs_by_skill_id, global_costs_by_skill_id)
}

/// The score a skill starts with: its formula term plus 5 trained or 10
/// specialized; zero for a formula with no divisor.
#[must_use]
pub fn calculate_chargen_score(
    detail: &ChargenSkillDetail,
    attribute1: u32,
    attribute2: u32,
    level: ChargenSkillAdvancementClass,
) -> u32 {
    let Some(result) = try_calculate(&detail.formula, attribute1, attribute2) else {
        return 0;
    };
    match level {
        ChargenSkillAdvancementClass::Trained => result.wrapping_add(5),
        ChargenSkillAdvancementClass::Specialized => result.wrapping_add(10),
        _ => result,
    }
}

/// Scores a skill from the attribute values being chosen: zero for a skill
/// the skill table does not describe.
#[must_use]
pub fn chargen_skill_score(
    options: &ChargenOptions,
    skill_id: u32,
    attributes: &ChargenAttributeValues,
    level: ChargenSkillAdvancementClass,
) -> u32 {
    let Some(detail) = options.try_get_skill_detail(skill_id) else {
        return 0;
    };
    calculate_chargen_score(
        detail,
        attribute_value(detail.formula.attribute1, attributes),
        attribute_value(detail.formula.attribute2, attributes),
        level,
    )
}

fn attribute_value(attribute_id: u32, attributes: &ChargenAttributeValues) -> u32 {
    let value = match attribute_id {
        1 => attributes.strength,
        2 => attributes.endurance,
        3 => attributes.quickness,
        4 => attributes.coordination,
        5 => attributes.focus,
        6 => attributes.self_,
        _ => 0,
    };
    value.max(0).cast_unsigned()
}

/// A skill's name for character creation: the skill table's name, or the
/// built-in list's as a last resort (never needed with real data, where
/// both tables come from the same file).
#[must_use]
pub fn chargen_skill_name(options: &ChargenOptions, skill_id: u32) -> String {
    match options.try_get_skill_detail(skill_id) {
        Some(detail) if !detail.name.trim().is_empty() => detail.name.clone(),
        _ => skill_names::describe(skill_id.cast_signed()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::player::SkillFormula;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/CharGen/ChargenSkillAdvancementSetTests.cs`,
    // `ChargenSkillCreditMathTests.cs` and
    // `tests/AcDream.Content.Tests/Skills/RetailSkillFormulaTests.cs`.

    use ChargenSkillAdvancementClass as C;

    #[test]
    fn wire_has_exactly_55_entries() {
        let set = ChargenSkillAdvancementSet::new();
        assert_eq!(set.to_wire_classes().len(), 55);
        assert_eq!(ChargenSkillAdvancementSet::SLOT_COUNT, 55);
    }

    #[test]
    fn default_every_slot_inactive() {
        let set = ChargenSkillAdvancementSet::new();
        assert!(set.to_wire_classes().iter().all(|&v| v == 0));
        assert_eq!(set.get(1), C::Inactive);
        assert_eq!(set.get(54), C::Inactive);
    }

    #[test]
    fn round_trips_assigned_state() {
        let mut set = ChargenSkillAdvancementSet::new();
        set.set(1, C::Trained).expect("in range");
        set.set(54, C::Specialized).expect("in range");
        assert_eq!(set.get(1), C::Trained);
        assert_eq!(set.get(54), C::Specialized);
        let wire = set.to_wire_classes();
        assert_eq!(wire[0], 0);
        assert_eq!(wire[1], 2);
        assert_eq!(wire[54], 3);
    }

    #[test]
    fn reserved_and_out_of_range_slots() {
        let mut set = ChargenSkillAdvancementSet::new();
        assert_eq!(set.get(0), C::Inactive);
        assert_eq!(set.set(0, C::Trained), Err(SkillIdOutOfRange(0)));
        assert_eq!(set.set(55, C::Trained), Err(SkillIdOutOfRange(55)));
        assert_eq!(set.set(1000, C::Trained), Err(SkillIdOutOfRange(1000)));
        assert_eq!(set.get(55), C::Inactive);
        assert_eq!(set.get(u32::MAX), C::Inactive);
    }

    fn costs() -> HashMap<u32, ChargenSkillCost> {
        let cost = |skill_id, normal_cost, primary_cost| {
            (
                skill_id,
                ChargenSkillCost {
                    skill_id,
                    normal_cost,
                    primary_cost,
                },
            )
        };
        // No entry for skill 2 (Bow): the heritage does not offer it.
        HashMap::from([cost(1, 4, 12), cost(11, 4, 12), cost(24, 1, 3)])
    }

    fn set_of(entries: &[(u32, C)]) -> ChargenSkillAdvancementSet {
        let mut set = ChargenSkillAdvancementSet::new();
        for &(id, class) in entries {
            set.set(id, class).expect("in range");
        }
        set
    }

    #[test]
    fn spent_ignores_inactive_and_untrained() {
        let set = set_of(&[(1, C::Inactive), (11, C::Untrained)]);
        assert_eq!(compute_spent(&set, &costs(), &HashMap::new()), 0);
    }

    #[test]
    fn spent_charges_normal_and_primary_costs() {
        assert_eq!(
            compute_spent(&set_of(&[(1, C::Trained)]), &costs(), &HashMap::new()),
            4
        );
        assert_eq!(
            compute_spent(&set_of(&[(1, C::Specialized)]), &costs(), &HashMap::new()),
            12
        );
        let mixed = set_of(&[(1, C::Trained), (11, C::Specialized), (24, C::Trained)]);
        assert_eq!(compute_spent(&mixed, &costs(), &HashMap::new()), 17);
    }

    #[test]
    fn spent_skips_skill_with_no_cost() {
        assert_eq!(
            compute_spent(&set_of(&[(2, C::Trained)]), &costs(), &HashMap::new()),
            0
        );
    }

    #[test]
    fn heritage_cost_wins_over_global() {
        let global = HashMap::from([(
            1,
            ChargenSkillCost {
                skill_id: 1,
                normal_cost: 999,
                primary_cost: 999,
            },
        )]);
        assert_eq!(
            compute_spent(&set_of(&[(1, C::Trained)]), &costs(), &global),
            4
        );
    }

    #[test]
    fn falls_back_to_global_cost() {
        let global = HashMap::from([(
            2,
            ChargenSkillCost {
                skill_id: 2,
                normal_cost: 6,
                primary_cost: 18,
            },
        )]);
        assert_eq!(
            compute_spent(&set_of(&[(2, C::Trained)]), &costs(), &global),
            6
        );
        assert_eq!(
            compute_spent(&set_of(&[(2, C::Specialized)]), &costs(), &global),
            18
        );
    }

    #[test]
    fn remaining_may_go_negative() {
        let set = set_of(&[(1, C::Trained), (11, C::Specialized)]);
        assert_eq!(
            remaining_skill_credits(100, &set, &costs(), &HashMap::new()),
            84
        );
        assert_eq!(
            remaining_skill_credits(0, &set, &costs(), &HashMap::new()),
            -16
        );
    }

    fn detail(attribute1: u32, w: i32, x: i32, z: i32) -> ChargenSkillDetail {
        ChargenSkillDetail {
            skill_id: 0x10,
            name: "Mana Conversion".to_owned(),
            min_level: 1,
            description: String::new(),
            formula: SkillFormula {
                additive_bonus: w,
                attribute1_multiplier: x,
                attribute2_multiplier: 0,
                divisor: z,
                attribute1,
                attribute2: 0,
            },
        }
    }

    #[test]
    fn score_adds_level_bonus() {
        for (level, expected) in [(C::Untrained, 5), (C::Trained, 10), (C::Specialized, 15)] {
            assert_eq!(
                calculate_chargen_score(&detail(0, 0, 1, 1), 5, 0, level),
                expected
            );
        }
    }

    #[test]
    fn zero_divisor_scores_zero_at_every_level() {
        let d = ChargenSkillDetail {
            formula: SkillFormula {
                attribute2_multiplier: 1,
                ..detail(0, 7, 1, 0).formula
            },
            ..detail(0, 7, 1, 0)
        };
        for level in [C::Untrained, C::Trained, C::Specialized] {
            assert_eq!(calculate_chargen_score(&d, 10, 20, level), 0);
        }
    }

    #[test]
    fn score_resolver_reads_each_attribute() {
        for attribute in 1..=6u32 {
            let options = ChargenOptions {
                global_skill_details_by_skill_id: HashMap::from([(
                    0x10,
                    detail(attribute, 0, 1, 1),
                )]),
                ..ChargenOptions::default()
            };
            let mut values = ChargenAttributeValues::default();
            match attribute {
                1 => values.strength = 42,
                2 => values.endurance = 42,
                3 => values.quickness = 42,
                4 => values.coordination = 42,
                5 => values.focus = 42,
                _ => values.self_ = 42,
            }
            assert_eq!(
                chargen_skill_score(&options, 0x10, &values, C::Untrained),
                42
            );
        }
    }

    #[test]
    fn score_resolver_missing_skill_is_zero() {
        let values = ChargenAttributeValues {
            strength: 10,
            endurance: 20,
            coordination: 30,
            quickness: 40,
            focus: 50,
            self_: 60,
        };
        assert_eq!(
            chargen_skill_score(&ChargenOptions::default(), 0x10, &values, C::Specialized),
            0
        );
    }

    #[test]
    fn skill_name_prefers_the_skill_table() {
        let options = ChargenOptions {
            global_skill_details_by_skill_id: HashMap::from([(
                19,
                ChargenSkillDetail {
                    name: "Assess Person".to_owned(),
                    ..ChargenSkillDetail::default()
                },
            )]),
            ..ChargenOptions::default()
        };
        assert_eq!(chargen_skill_name(&options, 19), "Assess Person");
        assert_eq!(
            chargen_skill_name(&ChargenOptions::default(), 19),
            "Person Appraisal"
        );
        assert_eq!(
            chargen_skill_name(&ChargenOptions::default(), 48),
            "Skill 48"
        );
    }
}
