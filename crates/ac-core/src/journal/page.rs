//! One journal page.

/// The label box's maximum length, in characters.
pub const MAX_LABEL_LENGTH: usize = 16;
/// The title box's maximum length, in characters.
pub const MAX_TITLE_LENGTH: usize = 32;
/// The notes box's maximum length, in characters.
pub const MAX_NOTES_LENGTH: usize = 2048;

/// One page of the journal. The default is an untouched page, what "New"
/// produces.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct JournalPage {
    /// The short label shown in the page list.
    pub label: String,
    /// The title.
    pub title: String,
    /// The notes (may span lines).
    pub notes: String,
    /// The countdown's days.
    pub timer_days: i32,
    /// The countdown's hours.
    pub timer_hours: i32,
    /// The countdown's minutes.
    pub timer_minutes: i32,
    /// The recorded location's east-west map coordinate.
    pub location_x: f32,
    /// The recorded location's north-south map coordinate.
    pub location_y: f32,
    /// True when a location was recorded; (0, 0) is a real place.
    pub has_location: bool,
    /// Seconds left on a running countdown; 0 when none is running.
    pub running_timer_seconds: f64,
}

impl JournalPage {
    /// Whether the timer fields describe any duration at all.
    #[must_use]
    pub fn has_timer(&self) -> bool {
        self.timer_days != 0 || self.timer_hours != 0 || self.timer_minutes != 0
    }

    /// The timer fields as one duration, in seconds.
    #[must_use]
    pub fn timer_duration_seconds(&self) -> i64 {
        i64::from(self.timer_days) * 86_400
            + i64::from(self.timer_hours) * 3_600
            + i64::from(self.timer_minutes) * 60
    }

    /// Whether a countdown is running on this page.
    #[must_use]
    pub fn is_timer_running(&self) -> bool {
        self.running_timer_seconds > 0.0
    }

    /// The page with every text clipped to its box's maximum. Applied where
    /// outside text comes in: a load from disk, a paste.
    #[must_use]
    pub fn clipped(mut self) -> Self {
        clip(&mut self.label, MAX_LABEL_LENGTH);
        clip(&mut self.title, MAX_TITLE_LENGTH);
        clip(&mut self.notes, MAX_NOTES_LENGTH);
        self
    }
}

fn clip(value: &mut String, max: usize) {
    if let Some((cut, _)) = value.char_indices().nth(max) {
        value.truncate(cut);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timer_fields_make_one_duration() {
        let page = JournalPage {
            timer_days: 1,
            timer_hours: 2,
            timer_minutes: 3,
            ..JournalPage::default()
        };
        assert!(page.has_timer());
        assert_eq!(page.timer_duration_seconds(), 86_400 + 7_200 + 180);
        assert!(!JournalPage::default().has_timer());
    }

    #[test]
    fn clipping_counts_characters_not_bytes() {
        let page = JournalPage {
            label: "å".repeat(20),
            ..JournalPage::default()
        }
        .clipped();
        assert_eq!(page.label.chars().count(), MAX_LABEL_LENGTH);
    }
}
