//! The per-character journal: its pages, the page being read, the running
//! countdown, and the file it lives in between sessions.
//!
//! The journal is the client's alone; nothing goes to the server.

use std::path::{Path, PathBuf};

use ac_core::journal::{self, JournalPage};

/// The journal as a host shows it: the pages, the page being read
/// (1-based, 0 when empty), the running countdown and the revision.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct JournalView {
    /// Every page, in file order.
    pub pages: Vec<JournalPage>,
    /// The page being read, 1-based; 0 when there are none.
    pub current_page: usize,
    /// Advances on every change.
    pub revision: u64,
    /// When the running countdown started, Unix milliseconds.
    pub timer_started_unix_ms: Option<i64>,
    /// Its length then, seconds.
    pub timer_seconds_at_start: f64,
}

impl JournalView {
    /// The page being read, or an empty page.
    #[must_use]
    pub fn current(&self) -> JournalPage {
        self.current_page
            .checked_sub(1)
            .and_then(|i| self.pages.get(i))
            .cloned()
            .unwrap_or_default()
    }

    /// Seconds left on the countdown at `now_unix_ms`, never negative.
    #[must_use]
    pub fn remaining_timer_seconds(&self, now_unix_ms: i64) -> f64 {
        remaining(
            self.timer_started_unix_ms,
            self.timer_seconds_at_start,
            self.current().running_timer_seconds,
            now_unix_ms,
        )
    }
}

fn remaining(started: Option<i64>, at_start: f64, stored: f64, now_unix_ms: i64) -> f64 {
    match started {
        None => stored,
        Some(started) => {
            #[allow(clippy::cast_precision_loss)]
            let elapsed = (now_unix_ms - started) as f64 / 1000.0;
            (at_start - elapsed).max(0.0)
        }
    }
}

/// The journal's pages and the page being read (1-based, 0 when empty).
#[derive(Debug, Default)]
pub struct JournalState {
    pages: Vec<JournalPage>,
    current_page: usize,
    revision: u64,
    dirty: bool,
    timer_started_unix_ms: Option<i64>,
    timer_seconds_at_start: f64,
}

impl JournalState {
    /// No pages.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Every page, in file order.
    #[must_use]
    pub fn pages(&self) -> &[JournalPage] {
        &self.pages
    }

    /// The page being read, 1-based; 0 when there are none.
    #[must_use]
    pub fn current_page(&self) -> usize {
        self.current_page
    }

    /// The page being read, or an empty page.
    #[must_use]
    pub fn current(&self) -> JournalPage {
        self.current_page
            .checked_sub(1)
            .and_then(|i| self.pages.get(i))
            .cloned()
            .unwrap_or_default()
    }

    /// Advances on every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Changed since the last save.
    #[must_use]
    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    /// True when the page being read is the last one.
    #[must_use]
    pub fn is_last_page(&self) -> bool {
        self.current_page != 0 && self.current_page == self.pages.len()
    }

    /// Replace every page, as a file load does; opens on the first page. A
    /// load is not an edit.
    pub fn load(&mut self, pages: Vec<JournalPage>) {
        self.current_page = usize::from(!pages.is_empty());
        self.pages = pages;
        self.clear_timer();
        self.dirty = false;
        self.revision += 1;
    }

    /// The pages to write, with the running countdown's time left folded
    /// into the page it runs on.
    #[must_use]
    pub fn capture_for_save(&self, now_unix_ms: i64) -> Vec<JournalPage> {
        let remaining = self.remaining_timer_seconds(now_unix_ms);
        self.pages
            .iter()
            .enumerate()
            .map(|(i, page)| {
                let mut page = page.clone();
                if i + 1 == self.current_page {
                    page.running_timer_seconds = remaining;
                }
                page
            })
            .collect()
    }

    /// The file now says what the journal says.
    pub fn mark_saved(&mut self) {
        self.dirty = false;
    }

    /// Add an empty page at the end and read it.
    pub fn new_page(&mut self) {
        self.pages.push(JournalPage::default());
        self.current_page = self.pages.len();
        self.clear_timer();
        self.dirty = true;
        self.revision += 1;
    }

    /// Delete page `number` (1-based); false when there is no such page.
    pub fn delete_page(&mut self, number: usize) -> bool {
        if number < 1 || number > self.pages.len() {
            return false;
        }
        self.pages.remove(number - 1);
        self.current_page = self.current_page.min(self.pages.len());
        self.clear_timer();
        self.dirty = true;
        self.revision += 1;
        true
    }

    /// Read page `number` (1-based); false when there is no such page.
    pub fn goto_page(&mut self, number: usize) -> bool {
        if number < 1 || number > self.pages.len() {
            return false;
        }
        if self.current_page != number {
            self.current_page = number;
            self.clear_timer();
            self.revision += 1;
        }
        true
    }

    /// Replace the current page's texts, clipped to the boxes' sizes. An
    /// edit that changes nothing is not an edit.
    pub fn update_current(&mut self, label: &str, title: &str, notes: &str) {
        let Some(page) = self.current_mut() else {
            return;
        };
        let updated = JournalPage {
            label: label.to_owned(),
            title: title.to_owned(),
            notes: notes.to_owned(),
            ..page.clone()
        }
        .clipped();
        if updated == *page {
            return;
        }
        *page = updated;
        self.dirty = true;
        self.revision += 1;
    }

    /// Record a map location on the current page; (0, 0) is a real place.
    pub fn record_location(&mut self, x: f32, y: f32) {
        let Some(page) = self.current_mut() else {
            return;
        };
        page.location_x = x;
        page.location_y = y;
        page.has_location = true;
        self.dirty = true;
        self.revision += 1;
    }

    /// Set the current page's countdown length; negatives count as zero.
    pub fn set_timer(&mut self, days: i32, hours: i32, minutes: i32) {
        let Some(page) = self.current_mut() else {
            return;
        };
        page.timer_days = days.max(0);
        page.timer_hours = hours.max(0);
        page.timer_minutes = minutes.max(0);
        self.dirty = true;
        self.revision += 1;
    }

    /// Start the current page's countdown; false when it has no length.
    pub fn start_timer(&mut self, now_unix_ms: i64) -> bool {
        let Some(page) = self.current_mut() else {
            return false;
        };
        #[allow(clippy::cast_precision_loss)]
        let seconds = page.timer_duration_seconds() as f64;
        if seconds <= 0.0 {
            return false;
        }
        page.running_timer_seconds = seconds;
        self.timer_started_unix_ms = Some(now_unix_ms);
        self.timer_seconds_at_start = seconds;
        self.dirty = true;
        self.revision += 1;
        true
    }

    /// Stop the countdown.
    pub fn reset_timer(&mut self) {
        let was_running = self.timer_started_unix_ms.is_some();
        self.clear_timer();
        if let Some(page) = self.current_mut() {
            page.running_timer_seconds = 0.0;
        }
        if was_running {
            self.dirty = true;
        }
        self.revision += 1;
    }

    /// Seconds left on the countdown at `now_unix_ms`, never negative.
    #[must_use]
    pub fn remaining_timer_seconds(&self, now_unix_ms: i64) -> f64 {
        remaining(
            self.timer_started_unix_ms,
            self.timer_seconds_at_start,
            self.current().running_timer_seconds,
            now_unix_ms,
        )
    }

    /// The journal as owned data.
    #[must_use]
    pub fn view(&self) -> JournalView {
        JournalView {
            pages: self.pages.clone(),
            current_page: self.current_page,
            revision: self.revision,
            timer_started_unix_ms: self.timer_started_unix_ms,
            timer_seconds_at_start: self.timer_seconds_at_start,
        }
    }

    /// Forget every page: the journal belongs to the character.
    pub fn reset_session(&mut self) {
        let changed = !self.pages.is_empty() || self.current_page != 0;
        self.pages.clear();
        self.current_page = 0;
        self.dirty = false;
        self.clear_timer();
        if changed {
            self.revision += 1;
        }
    }

    fn current_mut(&mut self) -> Option<&mut JournalPage> {
        let index = self.current_page.checked_sub(1)?;
        self.pages.get_mut(index)
    }

    fn clear_timer(&mut self) {
        self.timer_started_unix_ms = None;
        self.timer_seconds_at_start = 0.0;
    }
}

/// Where a character's journal file is, and loading and saving it.
#[derive(Debug)]
pub struct JournalFile {
    directory: PathBuf,
    current_path: Option<PathBuf>,
}

impl JournalFile {
    /// Journals live in `directory`.
    #[must_use]
    pub fn new(directory: impl Into<PathBuf>) -> Self {
        Self {
            directory: directory.into(),
            current_path: None,
        }
    }

    /// The file of the loaded character.
    #[must_use]
    pub fn current_path(&self) -> Option<&Path> {
        self.current_path.as_deref()
    }

    /// Load `character_name`'s journal on `server_name` into `state`. A
    /// character without a file starts empty. A file that cannot be read or
    /// does not parse leaves the journal empty and returns the message to
    /// show: half a notebook would be saved back over the whole one.
    pub fn load(
        &mut self,
        state: &mut JournalState,
        server_name: &str,
        character_name: &str,
    ) -> Option<String> {
        let file_name = journal::file_name_for(server_name, character_name)?;
        let path = self.directory.join(file_name);
        self.current_path = Some(path.clone());
        let text = match std::fs::read_to_string(&path) {
            Ok(text) => text,
            Err(_) if !path.exists() => String::new(),
            Err(error) => {
                state.load(Vec::new());
                return Some(format!("Problem loading journal: {error}"));
            }
        };
        let read = journal::read(&text);
        if let Some(message) = read.error {
            state.load(Vec::new());
            return Some(message.to_owned());
        }
        state.load(read.pages);
        None
    }

    /// Write the journal if it changed. `Ok(true)` when written, `Ok(false)`
    /// when there was nothing to write or no character is loaded, `Err` with
    /// the message to show when the write failed.
    pub fn save(&self, state: &mut JournalState, now_unix_ms: i64) -> Result<bool, String> {
        let Some(path) = &self.current_path else {
            return Ok(false);
        };
        if !state.is_dirty() {
            return Ok(false);
        }
        let text = journal::write(&state.capture_for_save(now_unix_ms));
        std::fs::create_dir_all(&self.directory)
            .and_then(|()| std::fs::write(path, text))
            .map_err(|error| format!("Problem saving journal: {error}"))?;
        state.mark_saved();
        Ok(true)
    }

    /// Save, then forget the character.
    pub fn close(&mut self, state: &mut JournalState, now_unix_ms: i64) -> Result<bool, String> {
        let saved = self.save(state, now_unix_ms);
        self.current_path = None;
        saved
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::communication::log_file::tests::TempDir;

    const NOW: i64 = 1_787_313_600_000;

    fn with_pages(labels: &[&str]) -> JournalState {
        let mut state = JournalState::new();
        state.load(
            labels
                .iter()
                .map(|l| JournalPage {
                    label: (*l).into(),
                    ..JournalPage::default()
                })
                .collect(),
        );
        state
    }

    // Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Gameplay/RuntimeJournalStateTests.cs

    #[test]
    fn a_fresh_journal_is_empty_and_a_load_opens_page_one() {
        let state = JournalState::new();
        assert_eq!((state.pages().len(), state.current_page()), (0, 0));
        assert_eq!(state.current(), JournalPage::default());
        let state = with_pages(&["a", "b"]);
        assert_eq!(state.current_page(), 1);
        assert_eq!(state.current().label, "a");
        assert!(!state.is_dirty());
    }

    #[test]
    fn new_page_appends_and_moves_to_it() {
        let mut state = with_pages(&["a"]);
        state.new_page();
        assert_eq!((state.pages().len(), state.current_page()), (2, 2));
        assert_eq!(state.current(), JournalPage::default());
        assert!(state.is_dirty());
    }

    #[test]
    fn edits_land_on_the_current_page_clipped_and_only_when_they_change_it() {
        let mut state = with_pages(&["a", "b"]);
        state.goto_page(2);
        state.update_current("label", "title", "notes");
        assert_eq!(state.pages()[0].label, "a");
        assert_eq!(state.pages()[1].title, "title");
        let mut state = with_pages(&["a"]);
        state.update_current("a", "", "");
        assert!(!state.is_dirty());
        state.update_current(&"x".repeat(100), &"y".repeat(100), "notes");
        assert_eq!(
            state.current().label.len(),
            ac_core::journal::MAX_LABEL_LENGTH
        );
        assert_eq!(
            state.current().title.len(),
            ac_core::journal::MAX_TITLE_LENGTH
        );
    }

    #[test]
    fn deleting_pages_and_out_of_range_pages() {
        let mut state = with_pages(&["only"]);
        assert!(state.delete_page(1));
        assert_eq!((state.pages().len(), state.current_page()), (0, 0));
        let mut state = with_pages(&["a", "b", "c"]);
        state.goto_page(3);
        state.delete_page(3);
        assert_eq!(state.current_page(), 2);
        assert_eq!(state.current().label, "b");
        let mut state = with_pages(&["a", "b", "c"]);
        for page in [0, 4] {
            assert!(!state.goto_page(page));
            assert!(!state.delete_page(page));
        }
        assert_eq!(state.current_page(), 1);
        assert!(!state.is_last_page());
        state.goto_page(3);
        assert!(state.is_last_page());
    }

    #[test]
    fn a_location_at_zero_is_recorded() {
        let mut state = with_pages(&["a"]);
        state.record_location(0.0, 0.0);
        assert!(state.current().has_location);
    }

    #[test]
    fn the_countdown_runs_against_the_clock() {
        let mut state = with_pages(&["a"]);
        assert!(!state.start_timer(NOW));
        state.set_timer(0, 1, 0);
        assert!(state.start_timer(NOW));
        assert!((state.remaining_timer_seconds(NOW) - 3600.0).abs() < f64::EPSILON);
        assert!((state.remaining_timer_seconds(NOW + 60_000) - 3540.0).abs() < f64::EPSILON);
        assert!(state.remaining_timer_seconds(NOW + 7_200_000).abs() < f64::EPSILON);
        let saved = state.capture_for_save(NOW + 1_800_000);
        assert!((saved[0].running_timer_seconds - 1800.0).abs() < f64::EPSILON);
    }

    #[test]
    fn leaving_the_page_stops_its_countdown() {
        let mut state = with_pages(&["a", "b"]);
        state.set_timer(0, 1, 0);
        state.start_timer(NOW);
        state.goto_page(2);
        assert!(state.remaining_timer_seconds(NOW).abs() < f64::EPSILON);
    }

    #[test]
    fn mark_saved_reset_and_revision() {
        let mut state = with_pages(&["a"]);
        let start = state.revision();
        state.new_page();
        assert!(state.revision() > start && state.is_dirty());
        state.mark_saved();
        assert!(!state.is_dirty());
        state.reset_session();
        assert_eq!((state.pages().len(), state.current_page()), (0, 0));
    }

    // Golden behaviour: OpenAC tests/AcDream.App.Tests/UI/JournalPersistenceTests.cs

    #[test]
    fn a_page_written_in_one_session_is_there_in_the_next() {
        let dir = TempDir::new("journal");
        let mut first = JournalState::new();
        let mut file = JournalFile::new(&dir.0);
        assert_eq!(file.load(&mut first, "World", "Acdream"), None);
        first.new_page();
        first.update_current("label", "A title", "Some notes.");
        assert_eq!(file.save(&mut first, NOW), Ok(true));
        assert_eq!(file.save(&mut first, NOW), Ok(false));

        let mut second = JournalState::new();
        let mut file = JournalFile::new(&dir.0);
        file.load(&mut second, "World", "Acdream");
        assert_eq!(second.pages().len(), 1);
        assert_eq!(second.pages()[0].title, "A title");
        assert_eq!(second.pages()[0].notes, "Some notes.");

        file.load(&mut second, "World", "Someone Else");
        assert!(second.pages().is_empty());
    }

    #[test]
    fn a_malformed_file_reports_and_leaves_the_journal_empty() {
        let dir = TempDir::new("journal-bad");
        std::fs::write(
            dir.0
                .join(ac_core::journal::file_name_for("World", "Acdream").unwrap()),
            "<TITL> no page marker first\n",
        )
        .unwrap();
        let mut state = with_pages(&["stale"]);
        let mut file = JournalFile::new(&dir.0);
        assert_eq!(
            file.load(&mut state, "World", "Acdream").as_deref(),
            Some(ac_core::journal::MALFORMED_FILE_MESSAGE)
        );
        assert!(state.pages().is_empty());
    }

    #[test]
    fn saving_needs_a_character_and_close_forgets_it() {
        let dir = TempDir::new("journal-close");
        let journal_dir = dir.0.join("journal");
        let mut state = JournalState::new();
        let mut file = JournalFile::new(&journal_dir);
        assert_eq!(file.save(&mut state, NOW), Ok(false));
        assert!(!journal_dir.exists());
        file.load(&mut state, "World", "Acdream");
        state.new_page();
        assert_eq!(file.close(&mut state, NOW), Ok(true));
        assert_eq!(file.current_path(), None);
        assert_eq!(file.save(&mut state, NOW), Ok(false));
    }

    #[test]
    fn a_running_timer_persists_its_remaining_time() {
        let dir = TempDir::new("journal-timer");
        let mut state = JournalState::new();
        let mut file = JournalFile::new(&dir.0);
        file.load(&mut state, "World", "Acdream");
        state.new_page();
        state.set_timer(0, 1, 0);
        state.start_timer(NOW);
        file.save(&mut state, NOW + 1_800_000).unwrap();
        let mut reloaded = JournalState::new();
        JournalFile::new(&dir.0).load(&mut reloaded, "World", "Acdream");
        assert!((reloaded.pages()[0].running_timer_seconds - 1800.0).abs() < f64::EPSILON);
    }

    #[test]
    fn an_unwritable_directory_reports_rather_than_panics() {
        let dir = TempDir::new("journal-blocked");
        let blocked = dir.0.join("blocked");
        std::fs::write(&blocked, "not a directory").unwrap();
        let mut state = JournalState::new();
        let mut file = JournalFile::new(&blocked);
        file.load(&mut state, "World", "Acdream");
        state.new_page();
        assert!(file.save(&mut state, NOW).is_err());
    }
}
