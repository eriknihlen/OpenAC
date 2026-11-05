//! A skill's attribute-derived term: `(x * attribute1 + y * attribute2 + w)
//! / z`, rounded half up, from the formula the skill table authors; the
//! tooltip text that spells the formula out; and the live resolver that
//! scores a character's skills from its current attributes.

use std::collections::{BTreeMap, HashMap};

/// A skill's formula as the skill table stores it (signed words the game
/// reads as unsigned).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SkillFormula {
    /// `w`.
    pub additive_bonus: i32,
    /// `x`.
    pub attribute1_multiplier: i32,
    /// `y`.
    pub attribute2_multiplier: i32,
    /// `z`; zero means the skill has no attribute term.
    pub divisor: i32,
    /// First attribute id (1 strength .. 6 self).
    pub attribute1: u32,
    /// Second attribute id.
    pub attribute2: u32,
}

/// One skill-table entry.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SkillBase {
    /// Name.
    pub name: String,
    /// Description.
    pub description: String,
    /// The advancement class (1 untrained, 2 trained, 3 specialized) at
    /// which the skill starts to count.
    pub min_level: u32,
    /// Credits to train.
    pub trained_cost: i32,
    /// Credits to specialize.
    pub specialized_cost: i32,
    /// The attribute formula.
    pub formula: SkillFormula,
}

/// The formula's value for two attribute values, or `None` when the
/// divisor is zero (the only failure). The numerator wraps at 32 bits.
#[must_use]
pub fn try_calculate(formula: &SkillFormula, attribute1: u32, attribute2: u32) -> Option<u32> {
    let divisor = formula.divisor.cast_unsigned();
    if divisor == 0 {
        return None;
    }
    let x = formula.attribute1_multiplier.cast_unsigned();
    let y = formula.attribute2_multiplier.cast_unsigned();
    let w = formula.additive_bonus.cast_unsigned();
    let numerator = x
        .wrapping_mul(attribute1)
        .wrapping_add(y.wrapping_mul(attribute2))
        .wrapping_add(w);
    // Below 2^32 + 0.5, so the conversion never saturates.
    Some((f64::from(numerator) / f64::from(divisor) + 0.5).floor() as u32)
}

/// An attribute's name ("" for an unknown id).
#[must_use]
pub fn attribute_name(attribute_id: u32) -> &'static str {
    match attribute_id {
        1 => "Strength",
        2 => "Endurance",
        3 => "Quickness",
        4 => "Coordination",
        5 => "Focus",
        6 => "Self",
        _ => "",
    }
}

/// The formula as the skill tooltip writes it, e.g.
/// `"( (Strength + Coordination) / 2 )"`, or `None` when it names no
/// attribute.
#[must_use]
pub fn format_formula(formula: &SkillFormula) -> Option<String> {
    let x = formula.attribute1_multiplier.cast_unsigned();
    let y = formula.attribute2_multiplier.cast_unsigned();
    let w = formula.additive_bonus.cast_unsigned();
    let divisor = formula.divisor.cast_unsigned();

    let has1 = x >= 1 && formula.attribute1 != 0;
    let has2 = y >= 1 && formula.attribute2 != 0;
    if !has1 && !has2 {
        return None;
    }

    let term = |multiplier: u32, attribute: u32| {
        let name = attribute_name(attribute);
        if multiplier <= 1 {
            name.to_owned()
        } else {
            format!("({multiplier} x {name})")
        }
    };

    let mut text = String::from("( ");
    if has1 && has2 {
        text.push('(');
    }
    if has1 {
        text.push_str(&term(x, formula.attribute1));
        if has2 {
            text.push_str(" + ");
        }
    }
    if has2 {
        text.push_str(&term(y, formula.attribute2));
    }
    if has1 && has2 {
        text.push(')');
    }
    if divisor != 1 {
        text.push_str(&format!(" / {divisor}"));
    }
    if w != 0 {
        text.push_str(&format!("+{w}"));
    }
    text.push_str(" )");
    Some(text)
}

/// The skill tooltip: the formula line, a newline, then the description;
/// `None` when both are empty.
#[must_use]
pub fn build_tooltip(skill: &SkillBase) -> Option<String> {
    let tooltip = match format_formula(&skill.formula) {
        Some(formula) => format!("{formula}\n{}", skill.description),
        None => skill.description.clone(),
    };
    (!tooltip.is_empty()).then_some(tooltip)
}

/// Scores a live character's skills from its current attributes, with the
/// formulas of the skill table.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LiveSkillCreditResolver {
    skills: HashMap<u32, SkillBase>,
}

impl LiveSkillCreditResolver {
    /// A resolver over the skill table (empty when the table is missing).
    #[must_use]
    pub fn new(skills: HashMap<u32, SkillBase>) -> Self {
        Self { skills }
    }

    /// The skill's attribute term. Zero for a skill the table does not
    /// describe, for one the character has not taken to its minimum
    /// advancement class, and for a formula with no divisor; an attribute
    /// missing from `attribute_currents` counts as zero.
    #[must_use]
    pub fn resolve(
        &self,
        skill_id: u32,
        advancement_class: u32,
        attribute_currents: &BTreeMap<u32, u32>,
    ) -> u32 {
        let Some(skill) = self.skills.get(&skill_id) else {
            return 0;
        };
        if advancement_class < skill.min_level {
            return 0;
        }
        let formula = &skill.formula;
        let attribute = |id: u32| attribute_currents.get(&id).copied().unwrap_or(0);
        try_calculate(
            formula,
            attribute(formula.attribute1),
            attribute(formula.attribute2),
        )
        .unwrap_or(0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Content.Tests/Skills/RetailSkillFormulaTests.cs` and
    // `LiveSkillCreditResolverTests.cs`.

    fn formula(w: i32, x: i32, y: i32, z: u32) -> SkillFormula {
        SkillFormula {
            additive_bonus: w,
            attribute1_multiplier: x,
            attribute2_multiplier: y,
            divisor: z.cast_signed(),
            attribute1: 0,
            attribute2: 0,
        }
    }

    #[test]
    fn zero_divisor_is_the_only_failure() {
        assert_eq!(try_calculate(&formula(7, 1, 1, 0), 10, 20), None);
        assert_eq!(try_calculate(&formula(7, 0, 2, 3), 10, 4), Some(5));
    }

    #[test]
    fn division_rounds_half_up() {
        for (numerator, divisor, expected) in [(9, 4, 2), (10, 4, 3), (11, 4, 3), (12, 4, 3)] {
            assert_eq!(
                try_calculate(&formula(0, 1, 0, divisor), numerator, 0),
                Some(expected)
            );
        }
    }

    #[test]
    fn additive_is_inside_the_numerator() {
        assert_eq!(try_calculate(&formula(3, 1, 1, 4), 4, 2), Some(2));
    }

    #[test]
    fn numerator_wraps_at_32_bits() {
        assert_eq!(
            try_calculate(&formula(1, 2, 0, 2), u32::MAX, 0),
            Some(0x8000_0000)
        );
    }

    #[test]
    fn signed_storage_reads_unsigned() {
        assert_eq!(try_calculate(&formula(-1, 0, 0, 1), 0, 0), Some(u32::MAX));
    }

    #[test]
    fn format_reads_multiplier_unsigned() {
        let f = SkillFormula {
            attribute1: 1,
            ..formula(0, -1, 0, 1)
        };
        let text = format_formula(&f).expect("formula");
        assert!(text.contains(&u32::MAX.to_string()));
        assert!(!text.contains("-1"));
    }

    #[test]
    fn format_reads_divisor_and_additive_unsigned() {
        let f = SkillFormula {
            attribute1: 1,
            ..formula(-1, 1, 0, (-2i32).cast_unsigned())
        };
        let text = format_formula(&f).expect("formula");
        assert!(text.contains(&format!("/ {}", (-2i32).cast_unsigned())));
        assert!(text.contains(&format!("+{}", u32::MAX)));
        assert!(!text.contains("-1"));
        assert!(!text.contains("-2"));
    }

    #[test]
    fn format_two_attributes() {
        let f = SkillFormula {
            attribute1: 1,
            attribute2: 4,
            ..formula(0, 1, 1, 2)
        };
        assert_eq!(
            format_formula(&f).as_deref(),
            Some("( (Strength + Coordination) / 2 )")
        );
        let weighted = SkillFormula {
            attribute1: 5,
            attribute2: 6,
            ..formula(0, 2, 1, 4)
        };
        assert_eq!(
            format_formula(&weighted).as_deref(),
            Some("( ((2 x Focus) + Self) / 4 )")
        );
    }

    #[test]
    fn tooltip_formula_then_newline_then_description() {
        let skill = SkillBase {
            formula: SkillFormula {
                attribute1: 1,
                attribute2: 4,
                ..formula(0, 1, 1, 2)
            },
            description: "Description text.".to_owned(),
            ..SkillBase::default()
        };
        let tooltip = build_tooltip(&skill).expect("tooltip");
        assert!(!tooltip.starts_with('\n'));
        let split = tooltip.find('\n').expect("newline");
        assert!(split > 0);
        assert_eq!(&tooltip[split + 1..], "Description text.");
    }

    #[test]
    fn formula_less_skill_shows_bare_description() {
        let skill = SkillBase {
            formula: formula(7, 1, 1, 0),
            description: "Salvage things.".to_owned(),
            ..SkillBase::default()
        };
        assert_eq!(build_tooltip(&skill).as_deref(), Some("Salvage things."));
        assert_eq!(build_tooltip(&SkillBase::default()), None);
    }

    #[test]
    fn live_resolver_reads_the_formula_and_treats_missing_attributes_as_zero() {
        let resolver = LiveSkillCreditResolver::new(HashMap::from([(
            0x345,
            SkillBase {
                formula: SkillFormula {
                    attribute1: 1,
                    attribute2: 2,
                    ..formula(1, 1, 2, 3)
                },
                ..SkillBase::default()
            },
        )]));
        assert_eq!(resolver.resolve(0x345, 2, &BTreeMap::from([(1, 8)])), 3);
        assert_eq!(resolver.resolve(0x999, 2, &BTreeMap::new()), 0);
    }

    fn run_table(minimum: u32) -> LiveSkillCreditResolver {
        LiveSkillCreditResolver::new(HashMap::from([(
            24,
            SkillBase {
                min_level: minimum,
                formula: SkillFormula {
                    attribute1: 1,
                    attribute2: 4,
                    ..formula(0, 1, 1, 2)
                },
                ..SkillBase::default()
            },
        )]))
    }

    fn maxed() -> BTreeMap<u32, u32> {
        BTreeMap::from([(1, 315), (4, 315)])
    }

    #[test]
    fn usable_skill_scores_its_attribute_average() {
        for class in [1, 2, 3] {
            assert_eq!(run_table(1).resolve(24, class, &maxed()), 315);
        }
    }

    #[test]
    fn skill_below_its_minimum_scores_nothing() {
        for (class, minimum) in [(1, 2), (1, 3), (2, 3)] {
            assert_eq!(run_table(minimum).resolve(24, class, &maxed()), 0);
        }
        assert_eq!(run_table(2).resolve(24, 2, &maxed()), 315);
    }

    #[test]
    fn undescribed_skill_scores_nothing() {
        assert_eq!(run_table(1).resolve(0x999, 3, &maxed()), 0);
        assert_eq!(
            LiveSkillCreditResolver::default().resolve(24, 3, &maxed()),
            0
        );
    }
}
