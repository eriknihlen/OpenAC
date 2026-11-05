//! Burden: how much a character can carry and how slowed it is by a load.
//!
//! "Load" is carried burden divided by capacity: 1.0 is a full load, and
//! movement slows from there until it stops at 2.0. The burden meter fills
//! to the top at 3.0 (300%).

/// Capacity per point of strength.
pub const BURDEN_PER_STRENGTH: i32 = 150;
/// Extra capacity per strength point for each rank of the burden augmentation.
pub const AUG_BURDEN_PER_RANK: i32 = 30;
/// The augmentation's extra capacity per strength point never exceeds this.
pub const AUG_BURDEN_CAP: i32 = 150;

/// Carrying capacity: 150 per strength point plus the augmentation bonus
/// (30 per rank, at most 150) per strength point; 0 without strength.
#[must_use]
pub fn encumbrance_capacity(strength: i32, augmentation: i32) -> i32 {
    if strength <= 0 {
        return 0;
    }
    let bonus = augmentation
        .wrapping_mul(AUG_BURDEN_PER_RANK)
        .clamp(0, AUG_BURDEN_CAP);
    strength
        .wrapping_mul(BURDEN_PER_STRENGTH)
        .wrapping_add(bonus.wrapping_mul(strength))
}

/// Burden over capacity; 0 without capacity.
#[must_use]
pub fn load_ratio(capacity: i32, burden: i32) -> f32 {
    if capacity <= 0 {
        0.0
    } else {
        burden as f32 / capacity as f32
    }
}

/// The movement multiplier for a load: 1 up to a full load, falling to 0
/// at twice a full load.
#[must_use]
pub fn load_modifier(load: f32) -> f32 {
    if load <= 1.0 {
        1.0
    } else if load < 2.0 {
        2.0 - load
    } else {
        0.0
    }
}

/// The penalty the character sheet shows, in whole tens of percent.
#[must_use]
pub fn load_penalty_percent(load: f32) -> i32 {
    (10 - (load_modifier(load) * 10.0) as i32) * 10
}

/// How full the burden meter is, 0..1 (full at a load of 3).
#[must_use]
pub fn load_to_fill(load: f32) -> f32 {
    (load / 3.0).clamp(0.0, 1.0)
}

/// The burden meter's percentage, 0..=300.
#[must_use]
pub fn load_to_percent(load: f32) -> i32 {
    (load_to_fill(load) * 300.0).floor() as i32
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/BurdenMathTests.cs`.

    use super::*;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 0.000_05
    }

    #[test]
    fn encumbrance_capacity_matches_retail() {
        for (strength, aug, expected) in [
            (100, 0, 15000),
            (100, 3, 24000),
            (100, 10, 30000),
            (0, 5, 0),
        ] {
            assert_eq!(encumbrance_capacity(strength, aug), expected);
        }
    }

    #[test]
    fn load_ratio_is_burden_over_capacity() {
        assert!(close(load_ratio(15000, 7500), 0.5));
        assert!(close(load_ratio(15000, 0), 0.0));
        assert!(close(load_ratio(0, 100), 0.0));
    }

    #[test]
    fn load_modifier_and_character_info_penalty_match_retail() {
        for (load, modifier, penalty) in [
            (0.5, 1.0, 0),
            (1.0, 1.0, 0),
            (1.25, 0.75, 30),
            (1.5, 0.5, 50),
            (2.0, 0.0, 100),
            (3.0, 0.0, 100),
        ] {
            assert!(close(load_modifier(load), modifier), "{load}");
            assert_eq!(load_penalty_percent(load), penalty, "{load}");
        }
    }

    #[test]
    fn load_to_fill_is_third_clamped() {
        for (load, expected) in [
            (0.0, 0.0),
            (0.5, 0.16667),
            (1.0, 0.33333),
            (3.0, 1.0),
            (4.0, 1.0),
        ] {
            assert!(close(load_to_fill(load), expected), "{load}");
        }
    }

    #[test]
    fn load_to_percent_saturates_at_300() {
        for (load, expected) in [(0.0, 0), (0.5, 50), (1.0, 100), (3.0, 300), (4.0, 300)] {
            assert_eq!(load_to_percent(load), expected, "{load}");
        }
    }
}
