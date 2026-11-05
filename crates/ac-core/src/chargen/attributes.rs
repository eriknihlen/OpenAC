//! Character-creation attribute points: six attributes from 10 to 100
//! each, and a heritage budget the finish gate requires spent exactly.

/// Lowest value an attribute can take.
pub const ATTRIBUTE_MIN: i32 = 10;
/// Highest value an attribute can take.
pub const ATTRIBUTE_MAX: i32 = 100;

/// The six attribute values chosen.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenAttributeValues {
    /// Strength.
    pub strength: i32,
    /// Endurance.
    pub endurance: i32,
    /// Coordination.
    pub coordination: i32,
    /// Quickness.
    pub quickness: i32,
    /// Focus.
    pub focus: i32,
    /// Self.
    pub self_: i32,
}

impl ChargenAttributeValues {
    /// The sum of all six.
    #[must_use]
    pub fn total(&self) -> i64 {
        [
            self.strength,
            self.endurance,
            self.coordination,
            self.quickness,
            self.focus,
            self.self_,
        ]
        .into_iter()
        .map(i64::from)
        .sum()
    }
}

/// Budget minus the points spent (negative when overspent).
#[must_use]
pub fn remaining_credits(attribute_credit_budget: u32, values: &ChargenAttributeValues) -> i64 {
    i64::from(attribute_credit_budget) - values.total()
}

/// True when the budget is spent exactly, as finishing requires.
#[must_use]
pub fn is_fully_spent(attribute_credit_budget: u32, values: &ChargenAttributeValues) -> bool {
    remaining_credits(attribute_credit_budget, values) == 0
}

/// True when a value is within 10..=100.
#[must_use]
pub fn is_within_range(value: i32) -> bool {
    (ATTRIBUTE_MIN..=ATTRIBUTE_MAX).contains(&value)
}

/// True when all six values are within range.
#[must_use]
pub fn are_all_within_range(values: &ChargenAttributeValues) -> bool {
    [
        values.strength,
        values.endurance,
        values.coordination,
        values.quickness,
        values.focus,
        values.self_,
    ]
    .into_iter()
    .all(is_within_range)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/CharGen/ChargenAttributeMathTests.cs`.

    fn values(s: i32, e: i32, c: i32, q: i32, f: i32, w: i32) -> ChargenAttributeValues {
        ChargenAttributeValues {
            strength: s,
            endurance: e,
            coordination: c,
            quickness: q,
            focus: f,
            self_: w,
        }
    }

    #[test]
    fn remaining_is_budget_minus_sum() {
        assert_eq!(remaining_credits(180, &values(10, 10, 10, 10, 10, 10)), 120);
        let exact = values(40, 40, 40, 20, 20, 20);
        assert_eq!(exact.total(), 180);
        assert_eq!(remaining_credits(180, &exact), 0);
    }

    #[test]
    fn fully_spent_only_at_exactly_zero() {
        assert!(!is_fully_spent(180, &values(10, 10, 10, 10, 10, 10)));
        assert!(is_fully_spent(180, &values(40, 40, 40, 20, 20, 20)));
        let one_under = values(40, 40, 40, 20, 20, 19);
        assert!(!is_fully_spent(180, &one_under));
        assert_eq!(remaining_credits(180, &one_under), 1);
    }

    #[test]
    fn range_floor_and_ceiling() {
        for (value, expected) in [(9, false), (10, true), (100, true), (101, false)] {
            assert_eq!(is_within_range(value), expected);
        }
    }

    #[test]
    fn any_attribute_out_of_range_fails() {
        let ok = values(10, 100, 50, 50, 50, 50);
        assert!(are_all_within_range(&ok));
        assert!(!are_all_within_range(&ChargenAttributeValues {
            focus: 9,
            ..ok
        }));
        assert!(!are_all_within_range(&ChargenAttributeValues {
            self_: 101,
            ..ok
        }));
    }

    #[test]
    fn total_sums_all_six() {
        assert_eq!(values(1, 2, 3, 4, 5, 6).total(), 21);
    }
}
