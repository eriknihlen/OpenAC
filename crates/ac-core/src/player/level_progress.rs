//! Where a character stands between its level and the next, measured on the
//! experience table's level curve. The character sheet's experience meter
//! and the plugins read it from here, so the two cannot disagree.

/// The distance to the next level.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LevelProgress {
    /// Experience still needed for the next level; 0 at the top of the
    /// table.
    pub to_next: i64,
    /// How far through the current level, 0 to 1.
    pub fraction: f32,
    /// Nothing left to earn: the top of the table, or experience that
    /// already reaches the next level's threshold (the sheet's
    /// "Infinity!").
    pub no_next_level: bool,
    /// The table has no next level for this character at all.
    pub at_top_of_table: bool,
}

impl LevelProgress {
    /// Measure `total_experience` against the level curve `levels` (the
    /// total experience at which each level starts, by level). `None`
    /// without a curve or for a negative level.
    #[must_use]
    pub fn measure(levels: Option<&[u64]>, level: i32, total_experience: i64) -> Option<Self> {
        let levels = levels?;
        let level = usize::try_from(level).ok()?;
        let top = Self {
            to_next: 0,
            fraction: 0.0,
            no_next_level: true,
            at_top_of_table: true,
        };
        if level + 1 >= levels.len() {
            return Some(top);
        }
        let clamp = |v: u64| i64::try_from(v).unwrap_or(i64::MAX);
        let (current, next) = (clamp(levels[level]), clamp(levels[level + 1]));
        if next <= current {
            return Some(top);
        }
        let clamped = total_experience.clamp(current, next);
        let to_next = next - clamped;
        let fraction = (clamped - current) as f32 / (next - current) as f32;
        Some(Self {
            to_next,
            fraction,
            no_next_level: to_next <= 0,
            at_top_of_table: false,
        })
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC v0.1.25
    //! `tests/AcDream.Core.Tests/Player/LevelProgressTests.cs`.
    use super::*;

    const LEVELS: [u64; 4] = [0, 1_000, 3_000, 6_000];

    fn measure(level: i32, total: i64) -> LevelProgress {
        LevelProgress::measure(Some(&LEVELS), level, total).expect("measured")
    }

    #[test]
    fn measures_the_distance_to_the_next_level() {
        let p = measure(1, 2_000);
        assert_eq!(p.to_next, 1_000);
        assert_eq!(p.fraction, 0.5);
        assert!(!p.no_next_level && !p.at_top_of_table);
    }

    #[test]
    fn experience_past_the_next_level_is_nothing_left_but_not_the_top() {
        let p = measure(1, 3_500);
        assert_eq!(p.to_next, 0);
        assert!(p.no_next_level && !p.at_top_of_table);
    }

    #[test]
    fn experience_below_the_level_clamps_to_its_start() {
        let p = measure(2, 10);
        assert_eq!(p.to_next, 3_000);
        assert_eq!(p.fraction, 0.0);
    }

    #[test]
    fn the_last_level_is_the_top_of_the_table() {
        let p = measure(3, 9_000);
        assert!(p.at_top_of_table && p.no_next_level);
    }

    #[test]
    fn no_curve_or_a_negative_level_measures_nothing() {
        assert_eq!(LevelProgress::measure(None, 1, 0), None);
        assert_eq!(LevelProgress::measure(Some(&LEVELS), -1, 0), None);
    }
}
