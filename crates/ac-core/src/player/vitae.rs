//! The death penalty as the player reads it: how much of their life force
//! (vitae) is lost, and how much experience wins back the next percent.
//!
//! Vitae is a multiplier on health, stamina, mana and skills: 0.9 after a
//! death means everything reads 10% lower. Earning experience fills a pool;
//! when the pool reaches the threshold for the current vitae, one percent
//! comes back.

/// The penalty in whole percent and what the next percent costs.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VitaeStatus {
    /// The vitae multiplier (1.0 at full strength).
    pub multiplier: f32,
    /// Percent of life force lost; 0 at full strength.
    pub lost_percent: i32,
    /// Experience still to earn before one percent comes back (the
    /// threshold less the pool); 0 at full strength.
    pub experience_to_next_percent: i32,
}

/// The experience pool that wins back one percent at `vitae` for a
/// character of `level` (the level they died at): rounded to the nearest.
#[must_use]
pub fn vitae_cp_pool_threshold(vitae: f32, level: i32) -> i32 {
    ((f64::from(level).powf(2.5) * 2.5 + 20.0) * f64::from(vitae).powi(5) + 0.5) as i32
}

/// The status for a vitae multiplier, the experience already pooled and
/// the level the threshold is measured at (the death level, else the
/// character's level).
#[must_use]
pub fn vitae_status(multiplier: f32, pool: i32, level: i32) -> VitaeStatus {
    let lost_percent = 100 - (multiplier * 100.0) as i32;
    if lost_percent <= 0 {
        return VitaeStatus {
            multiplier,
            lost_percent: 0,
            experience_to_next_percent: 0,
        };
    }
    VitaeStatus {
        multiplier,
        lost_percent,
        experience_to_next_percent: vitae_cp_pool_threshold(multiplier, level).wrapping_sub(pool),
    }
}

/// The vitae panel's text, in English, with the experience grouped by
/// thousands.
#[must_use]
pub fn vitae_text(status: &VitaeStatus) -> String {
    if status.lost_percent <= 0 {
        return "Your Vitae, or life force, is at full strength.".to_owned();
    }
    let lost = status.lost_percent;
    format!(
        "Due to your recent death, you have temporarily lost {lost}% of your Vitae, or life force.\n\nThis means that your health, stamina, mana, and skills are temporarily reduced by {lost}%.  A reduction of less than 15% will not hinder you much, but beware losing much more than that.\n\nYou will regain 1% of your Vitae once you earn {} more experience.",
        group_thousands(status.experience_to_next_percent)
    )
}

fn group_thousands(value: i32) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3 + 1);
    if value < 0 {
        out.push('-');
    }
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

#[cfg(test)]
mod tests {
    //! Golden values from OpenAC
    //! `tests/AcDream.App.Tests/UI/Layout/IndicatorDetailPanelControllerTests.cs`
    //! (`Vitae_UsesDeathLevelPoolAndRetailThresholdFormula`) and the strings of
    //! `src/AcDream.App/UI/Layout/VitaeUiController.cs` (`VitaeStrings.English`).

    use super::*;

    #[test]
    fn the_threshold_follows_the_retail_formula() {
        assert_eq!(vitae_cp_pool_threshold(0.9, 10), 479);
    }

    #[test]
    fn a_death_level_and_pool_give_the_experience_still_to_earn() {
        let status = vitae_status(0.9, 100, 10);
        assert_eq!(status.lost_percent, 10);
        assert_eq!(status.experience_to_next_percent, 379);
        let text = vitae_text(&status);
        assert!(text.contains("lost 10%"));
        assert!(text.contains("health, stamina, mana, and skills are temporarily reduced by 10%"));
        assert!(text.contains("earn 379 more experience"));
    }

    #[test]
    fn full_strength_has_no_penalty() {
        let status = vitae_status(1.0, 100, 10);
        assert_eq!(status.lost_percent, 0);
        assert!(vitae_text(&status).contains("full strength"));
    }

    #[test]
    fn experience_is_grouped_by_thousands() {
        assert_eq!(group_thousands(61_000_000), "61,000,000");
        assert_eq!(group_thousands(379), "379");
    }
}
