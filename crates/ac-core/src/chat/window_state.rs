//! The chat windows' filters: which text classes each of the five chat
//! windows (the main one and four floating ones) shows, and which floating
//! windows are open.
//!
//! A filter is a 64-bit mask with one bit per text class
//! (bit `n` = [`super::LogTextType`] value `n`).

/// A chat window: 0 is the main window, 1 to 4 the floating ones.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ChatWindowId(u8);

impl ChatWindowId {
    /// The main chat window.
    pub const MAIN: Self = Self(0);

    /// The window with this number, or `None` outside 0..=4.
    #[must_use]
    pub fn new(id: i32) -> Option<Self> {
        u8::try_from(id)
            .ok()
            .filter(|&id| id <= MAX_FLOATING_WINDOW_ID)
            .map(Self)
    }

    /// The window's number.
    #[must_use]
    pub const fn get(self) -> u8 {
        self.0
    }

    /// Every window, main first.
    pub fn all() -> impl Iterator<Item = Self> {
        (0..=MAX_FLOATING_WINDOW_ID).map(Self)
    }
}

/// The first floating window's number.
pub const MIN_FLOATING_WINDOW_ID: u8 = 1;
/// The last floating window's number.
pub const MAX_FLOATING_WINDOW_ID: u8 = 4;
const WINDOW_COUNT: usize = MAX_FLOATING_WINDOW_ID as usize + 1;

/// Main window: everything but `ClientLocal` (0x1A) and from 0x20 up
/// (Society is opt-in).
pub const MAIN_WINDOW_DEFAULT_FILTER: u64 = 0xFBFF_FFFF;
/// Floating window 1: speech, tells, sent tells, emotes.
pub const FLOATY1_DEFAULT_FILTER: u64 = 0x0000_101C;
/// Floating window 2: social, social sent, allegiance.
pub const FLOATY2_DEFAULT_FILTER: u64 = 0x0004_0C00;
/// Floating window 3: fellowship.
pub const FLOATY3_DEFAULT_FILTER: u64 = 0x0008_0000;
/// Floating window 4: the General, Trade, LFG and Roleplay rooms.
pub const FLOATY4_DEFAULT_FILTER: u64 = 0x7800_0000;

/// The target window that means "every window whose filter accepts it".
pub const BROADCAST_TARGET_WINDOW: u32 = u32::MAX;

/// The filters and open state of the five chat windows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatWindowState {
    filters: [u64; WINDOW_COUNT],
    open: [bool; WINDOW_COUNT],
    revision: u64,
}

impl Default for ChatWindowState {
    fn default() -> Self {
        Self::new()
    }
}

impl ChatWindowState {
    /// The game's starting filters: the main window open, the floating
    /// windows closed.
    #[must_use]
    pub fn new() -> Self {
        let mut state = Self {
            filters: [0; WINDOW_COUNT],
            open: [false; WINDOW_COUNT],
            revision: 0,
        };
        state.reset_to_defaults();
        state
    }

    /// Advances on every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Restores the starting filters and open state.
    pub fn reset_to_defaults(&mut self) {
        self.filters = [
            MAIN_WINDOW_DEFAULT_FILTER,
            FLOATY1_DEFAULT_FILTER,
            FLOATY2_DEFAULT_FILTER,
            FLOATY3_DEFAULT_FILTER,
            FLOATY4_DEFAULT_FILTER,
        ];
        self.open = [true, false, false, false, false];
        self.revision += 1;
    }

    /// The window's filter.
    #[must_use]
    pub fn filter(&self, window: ChatWindowId) -> u64 {
        self.filters[usize::from(window.0)]
    }

    /// Sets the window's filter.
    pub fn set_filter(&mut self, window: ChatWindowId, filter: u64) {
        let slot = &mut self.filters[usize::from(window.0)];
        if *slot != filter {
            *slot = filter;
            self.revision += 1;
        }
    }

    /// Whether the window is open; the main window always is.
    #[must_use]
    pub fn is_open(&self, window: ChatWindowId) -> bool {
        window == ChatWindowId::MAIN || self.open[usize::from(window.0)]
    }

    /// Opens or closes a floating window; the main window ignores it.
    pub fn set_open(&mut self, window: ChatWindowId, open: bool) {
        if window == ChatWindowId::MAIN {
            return;
        }
        let slot = &mut self.open[usize::from(window.0)];
        if *slot != open {
            *slot = open;
            self.revision += 1;
        }
    }

    /// Flips a floating window and returns whether it is now open; the
    /// main window stays open and returns true.
    pub fn toggle(&mut self, window: ChatWindowId) -> bool {
        if window == ChatWindowId::MAIN {
            return true;
        }
        let slot = &mut self.open[usize::from(window.0)];
        *slot = !*slot;
        self.revision += 1;
        *slot
    }

    /// Whether the window's filter accepts the text class. A class of 64 or
    /// more never is.
    #[must_use]
    pub fn type_is_active(&self, window: ChatWindowId, log_text_type: u32) -> bool {
        log_text_type < 64 && (self.filter(window) >> log_text_type) & 1 != 0
    }

    /// Whether a line addressed to `target_window` with this class shows in
    /// `window`: always when addressed to it, through its filter when
    /// broadcast, never when addressed to another window.
    #[must_use]
    pub fn should_display(
        &self,
        window: ChatWindowId,
        target_window: u32,
        log_text_type: u32,
    ) -> bool {
        if target_window == u32::from(window.0) {
            return true;
        }
        target_window == BROADCAST_TARGET_WINDOW && self.type_is_active(window, log_text_type)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn w(id: i32) -> ChatWindowId {
        ChatWindowId::new(id).expect("valid window id")
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatWindowStateTests.cs
    #[test]
    fn defaults_match_the_starting_table() {
        let state = ChatWindowState::new();
        let expected = [
            0xFBFF_FFFF,
            0x0000_101C,
            0x0004_0C00,
            0x0008_0000,
            0x7800_0000,
        ];
        for (id, filter) in expected.into_iter().enumerate() {
            assert_eq!(state.filter(w(id as i32)), filter);
        }
        assert_eq!(MAIN_WINDOW_DEFAULT_FILTER, 0xFBFF_FFFF);
        assert_eq!(FLOATY1_DEFAULT_FILTER, 0x0000_101C);
        assert_eq!(FLOATY2_DEFAULT_FILTER, 0x0004_0C00);
        assert_eq!(FLOATY3_DEFAULT_FILTER, 0x0008_0000);
        assert_eq!(FLOATY4_DEFAULT_FILTER, 0x7800_0000);
    }

    #[test]
    fn main_window_always_open_floating_windows_start_closed() {
        let state = ChatWindowState::new();
        assert!(state.is_open(w(0)));
        for id in 1..=4 {
            assert!(!state.is_open(w(id)));
        }
    }

    #[test]
    fn floating_default_filters_hold_their_classes() {
        let state = ChatWindowState::new();
        for t in [0x02, 0x03, 0x04, 0x0C] {
            assert!(state.type_is_active(w(1), t));
        }
        assert!(!state.type_is_active(w(1), 0x0A));
        for t in [0x0A, 0x0B, 0x12] {
            assert!(state.type_is_active(w(2), t));
        }
        assert!(!state.type_is_active(w(2), 0x13));
        assert!(state.type_is_active(w(3), 0x13));
        assert!(!state.type_is_active(w(3), 0x0A));
        assert!(!state.type_is_active(w(3), 0x02));
        for t in [0x1B, 0x1C, 0x1D, 0x1E] {
            assert!(state.type_is_active(w(4), t));
        }
        assert!(!state.type_is_active(w(4), 0x20));
        assert!(!state.type_is_active(w(4), 0x12));
    }

    #[test]
    fn no_window_shows_society_or_reserved_by_default() {
        let state = ChatWindowState::new();
        for window in ChatWindowId::all() {
            assert!(!state.type_is_active(window, 0x20));
            assert!(!state.type_is_active(window, 0x21));
        }
    }

    #[test]
    fn a_class_at_or_above_64_is_never_active() {
        let mut state = ChatWindowState::new();
        state.set_filter(w(1), u64::MAX);
        assert!(!state.type_is_active(w(1), 64));
        assert!(!state.type_is_active(w(1), 1000));
    }

    #[test]
    fn display_rule_matrix() {
        let state = ChatWindowState::new();
        assert!(state.should_display(w(3), 3, 0x02));
        assert!(!state.should_display(w(1), 2, 0x02));
        assert!(state.should_display(w(1), BROADCAST_TARGET_WINDOW, 0x02));
        assert!(!state.should_display(w(1), BROADCAST_TARGET_WINDOW, 0x0A));
        assert!(!state.should_display(w(0), BROADCAST_TARGET_WINDOW, 0x1A));
        assert!(!state.should_display(w(0), BROADCAST_TARGET_WINDOW, 0x20));
        assert!(state.should_display(w(0), BROADCAST_TARGET_WINDOW, 0x02));
        assert!(state.should_display(w(0), 0, 0x1A));
    }

    #[test]
    fn set_filter_persists() {
        let mut state = ChatWindowState::new();
        state.set_filter(w(0), 0x1);
        assert_eq!(state.filter(w(0)), 0x1);
        assert!(state.type_is_active(w(0), 0x00));
        assert!(!state.type_is_active(w(0), 0x02));
        state.set_filter(w(2), 0x1);
        assert_eq!(state.filter(w(2)), 0x1);
        assert!(state.type_is_active(w(2), 0x00));
    }

    #[test]
    fn main_window_cannot_be_closed() {
        let mut state = ChatWindowState::new();
        state.set_open(w(0), false);
        assert!(state.is_open(w(0)));
        assert!(state.toggle(w(0)));
        assert!(state.is_open(w(0)));
    }

    #[test]
    fn toggle_flips_a_floating_window_and_returns_the_new_value() {
        let mut state = ChatWindowState::new();
        assert!(!state.is_open(w(1)));
        assert!(state.toggle(w(1)));
        assert!(state.is_open(w(1)));
        assert!(!state.toggle(w(1)));
        assert!(!state.is_open(w(1)));
    }

    #[test]
    fn reset_restores_filters_and_open_state() {
        let mut state = ChatWindowState::new();
        state.set_filter(w(1), 0);
        state.set_open(w(1), true);
        state.reset_to_defaults();
        assert_eq!(state.filter(w(1)), 0x0000_101C);
        assert!(!state.is_open(w(1)));
    }

    #[test]
    fn out_of_range_window_ids_do_not_exist() {
        assert_eq!(ChatWindowId::new(-1), None);
        assert_eq!(ChatWindowId::new(5), None);
        assert_eq!(ChatWindowId::new(4).map(ChatWindowId::get), Some(4));
    }

    #[test]
    fn revision_advances_on_changes_not_on_no_op_writes() {
        let mut state = ChatWindowState::new();
        let baseline = state.revision();
        state.set_filter(w(1), 0x1);
        assert!(state.revision() > baseline);
        let after_filter = state.revision();
        state.set_filter(w(1), 0x1);
        assert_eq!(state.revision(), after_filter);
        state.set_open(w(1), true);
        assert!(state.revision() > after_filter);
    }
}
