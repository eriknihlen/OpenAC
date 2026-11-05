//! The contract tracker's progress column.

use super::ContractEntry;
use crate::text::format_duration;

/// The progress text for one tracked contract.
///
/// `stage` is the wire stage: 1 available, 2 in progress, 3 done, and 4 or
/// more a progress counter of `stage - 4`. `time_when_repeats` is the
/// seconds-until-repeatable the server sent once, when the state arrived;
/// `received_at_seconds` is when it arrived and `now_seconds` is now, both
/// on the caller's clock. The countdown is anchored at arrival because the
/// server never sends the instant it measured from.
///
/// - Stage 3 with no timer: `"Done"`, or `"Available"` when the contract
///   can be repeated; with a timer still running,
///   `"Done (1h 1m 1s to Repeat)"`; with the timer run out, `"Available"`.
/// - Stage 4 and up: the authored progress format with its first `%d`
///   replaced by the counter (only the first: the game passes one number),
///   or `"In Progress"` when nothing is authored.
/// - Any other stage: empty.
#[must_use]
pub fn build_progress_text(
    stage: u32,
    time_when_repeats: f64,
    received_at_seconds: f64,
    entry: &ContractEntry,
    now_seconds: f64,
) -> String {
    match stage {
        1 => "Available".to_owned(),
        2 => "In Progress".to_owned(),
        3 => {
            if time_when_repeats <= 0.0 {
                return if entry.questflag_repeat_time.is_empty() {
                    "Done"
                } else {
                    "Available"
                }
                .to_owned();
            }
            let elapsed = now_seconds - received_at_seconds;
            let remaining = time_when_repeats - elapsed;
            if remaining <= 0.0 {
                return "Available".to_owned();
            }
            format!("Done ({} to Repeat)", format_duration(remaining))
        }
        4.. => {
            if entry.description_progress.is_empty() {
                "In Progress".to_owned()
            } else {
                entry
                    .description_progress
                    .replacen("%d", &(stage - 4).to_string(), 1)
            }
        }
        _ => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ARRIVAL: f64 = 1_000_000.0;

    fn entry(description_progress: &str, questflag_repeat_time: &str) -> ContractEntry {
        ContractEntry {
            description_progress: description_progress.to_owned(),
            questflag_repeat_time: questflag_repeat_time.to_owned(),
            ..ContractEntry::default()
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Quests/ContractProgressTextTests.cs
    #[test]
    fn countdown_has_no_double_space() {
        let text = build_progress_text(3, 30.0, ARRIVAL, &entry("", "flag"), ARRIVAL);
        assert!(!text.contains("  "), "{text}");
    }

    #[test]
    fn stage_one_is_available() {
        assert_eq!(
            build_progress_text(1, 0.0, ARRIVAL, &entry("", ""), ARRIVAL),
            "Available"
        );
    }

    #[test]
    fn stage_two_is_in_progress() {
        assert_eq!(
            build_progress_text(2, 0.0, ARRIVAL, &entry("", ""), ARRIVAL),
            "In Progress"
        );
    }

    #[test]
    fn stage_three_with_no_repeat_flag_is_done_for_good() {
        assert_eq!(
            build_progress_text(3, 0.0, ARRIVAL, &entry("", ""), ARRIVAL),
            "Done"
        );
    }

    #[test]
    fn stage_three_with_a_repeat_flag_and_no_timer_is_available_again() {
        assert_eq!(
            build_progress_text(3, 0.0, ARRIVAL, &entry("", "SomeQuestRepeat"), ARRIVAL),
            "Available"
        );
    }

    #[test]
    fn stage_three_with_a_running_timer_counts_down_to_the_repeat() {
        assert_eq!(
            build_progress_text(3, 3661.0, ARRIVAL, &entry("", "SomeQuestRepeat"), ARRIVAL),
            "Done (1h 1m 1s to Repeat)"
        );
    }

    #[test]
    fn the_countdown_is_anchored_at_arrival() {
        let e = entry("", "f");
        assert_eq!(
            build_progress_text(3, 600.0, ARRIVAL, &e, ARRIVAL),
            "Done (10m 0s to Repeat)"
        );
        assert_eq!(
            build_progress_text(3, 600.0, ARRIVAL, &e, ARRIVAL + 300.0),
            "Done (5m 0s to Repeat)"
        );
    }

    #[test]
    fn a_timer_that_has_run_out_since_arrival_reads_as_available() {
        assert_eq!(
            build_progress_text(3, 600.0, ARRIVAL, &entry("", "f"), ARRIVAL + 3600.0),
            "Available"
        );
    }

    #[test]
    fn stage_four_and_above_substitutes_the_count() {
        for (stage, expected) in [
            (4, "0/20 Tuskers"),
            (9, "5/20 Tuskers"),
            (24, "20/20 Tuskers"),
        ] {
            assert_eq!(
                build_progress_text(stage, 0.0, ARRIVAL, &entry("%d/20 Tuskers", ""), ARRIVAL),
                expected
            );
        }
    }

    #[test]
    fn a_progress_stage_with_no_authored_format_falls_back_to_in_progress() {
        assert_eq!(
            build_progress_text(7, 0.0, ARRIVAL, &entry("", ""), ARRIVAL),
            "In Progress"
        );
    }

    #[test]
    fn a_format_without_a_specifier_is_shown_verbatim() {
        assert_eq!(
            build_progress_text(6, 0.0, ARRIVAL, &entry("Gathering herbs", ""), ARRIVAL),
            "Gathering herbs"
        );
    }

    #[test]
    fn only_the_first_specifier_is_substituted() {
        assert_eq!(
            build_progress_text(7, 0.0, ARRIVAL, &entry("%d of %d", ""), ARRIVAL),
            "3 of %d"
        );
    }

    #[test]
    fn an_unknown_stage_produces_nothing() {
        assert_eq!(
            build_progress_text(0, 0.0, ARRIVAL, &entry("", ""), ARRIVAL),
            ""
        );
    }
}
