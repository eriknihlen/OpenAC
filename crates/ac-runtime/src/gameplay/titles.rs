//! The character's titles: every title earned and the one displayed, as the
//! server's title table and title notices state them.
//!
//! The owner raises no events; each change returns what it changed (a
//! wholesale replace, a title added, the displayed title moved), and the
//! revision moves only on a real change.

/// What one change did.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TitleChange {
    /// The whole table was replaced (every table and every reset, even an
    /// identical one).
    pub table_replaced: bool,
    /// A title newly earned.
    pub added: Option<u32>,
    /// The displayed title moved to this one (0: none).
    pub display_changed: Option<u32>,
}

/// The titles as a host shows them.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TitlesView {
    /// The displayed title, 0 for none.
    pub display_title_id: u32,
    /// Every earned title, in the server's order then the order earned.
    pub earned: Vec<u32>,
}

/// The earned titles and the displayed one.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TitleState {
    /// In the order the server listed them, then each as earned since: a
    /// reader passes the list on in the server's order.
    earned: Vec<u32>,
    display_title_id: u32,
    revision: u64,
    tables: u64,
}

impl TitleState {
    /// The displayed title, 0 for none.
    #[must_use]
    pub fn display_title_id(&self) -> u32 {
        self.display_title_id
    }

    /// Every earned title, in the server's order then the order earned.
    pub fn earned_title_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.earned.iter().copied()
    }

    /// How many titles are earned.
    #[must_use]
    pub fn count(&self) -> usize {
        self.earned.len()
    }

    /// The title is earned.
    #[must_use]
    pub fn has_earned_title(&self, title_id: u32) -> bool {
        self.earned.contains(&title_id)
    }

    /// Moves on every real change and on every reset.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Counts whole tables (every table the server sends and every reset,
    /// the changes that report `table_replaced`).
    #[must_use]
    pub fn table_generation(&self) -> u64 {
        self.tables
    }

    /// The titles as a host shows them.
    #[must_use]
    pub fn view(&self) -> TitlesView {
        TitlesView {
            display_title_id: self.display_title_id,
            earned: self.earned.clone(),
        }
    }

    /// The server's whole table: it replaces the earned list and the
    /// displayed title. A repeat in the list is kept once, where it first
    /// appeared, and the comparison is against that folded order (a list
    /// with a repeat is no change when it arrives again).
    pub fn replace_table(&mut self, display_title_id: u32, title_ids: &[u32]) -> TitleChange {
        self.tables += 1;
        let mut new: Vec<u32> = Vec::with_capacity(title_ids.len());
        for &id in title_ids {
            if !new.contains(&id) {
                new.push(id);
            }
        }
        let set_changed = new != self.earned;
        self.earned = new;
        let display_changed = self.display_title_id != display_title_id;
        self.display_title_id = display_title_id;
        if set_changed || display_changed {
            self.revision += 1;
        }
        TitleChange {
            table_replaced: true,
            added: None,
            display_changed: display_changed.then_some(display_title_id),
        }
    }

    /// A title notice: the title is earned (always), and displayed when the
    /// server says so. A notice repeating what is held changes nothing.
    pub fn apply_update_title(&mut self, title_id: u32, set_as_display: bool) -> TitleChange {
        let added = !self.earned.contains(&title_id);
        if added {
            self.earned.push(title_id);
        }
        let display_changed = set_as_display && self.display_title_id != title_id;
        if display_changed {
            self.display_title_id = title_id;
        }
        if added {
            self.revision += 1;
        }
        if display_changed {
            self.revision += 1;
        }
        TitleChange {
            table_replaced: false,
            added: added.then_some(title_id),
            display_changed: display_changed.then_some(title_id),
        }
    }

    /// The session ended: nothing earned, nothing displayed.
    pub fn reset_session(&mut self) -> TitleChange {
        self.tables += 1;
        let previous = self.display_title_id;
        self.earned.clear();
        self.display_title_id = 0;
        self.revision += 1;
        TitleChange {
            table_replaced: true,
            added: None,
            display_changed: (previous != 0).then_some(0),
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeCharacterTitleStateTests.cs`
    //! (events become the returned [`TitleChange`]).

    use super::*;

    #[test]
    fn initial_state_is_empty_with_no_display_title() {
        let titles = TitleState::default();
        assert_eq!(titles.display_title_id(), 0);
        assert_eq!(titles.count(), 0);
        assert!(!titles.has_earned_title(13));
    }

    #[test]
    fn replace_table_sets_the_earned_ids_and_the_display_title() {
        let mut titles = TitleState::default();
        let change = titles.replace_table(13, &[1, 5, 13]);
        assert_eq!(titles.display_title_id(), 13);
        assert_eq!(titles.earned_title_ids().collect::<Vec<_>>(), [1, 5, 13]);
        assert!(titles.has_earned_title(5));
        assert!(!titles.has_earned_title(99));
        assert!(change.table_replaced);
        assert_eq!(titles.count(), 3);
    }

    // OpenAC `EarnedTitles_KeepTheServersOrderThenTheOrderEarned`.
    #[test]
    fn earned_titles_keep_the_servers_order_then_the_order_earned() {
        let mut titles = TitleState::default();
        titles.replace_table(0, &[13, 1, 5, 1]);
        titles.apply_update_title(2, false);
        titles.apply_update_title(5, false);
        assert_eq!(titles.earned_title_ids().collect::<Vec<_>>(), [13, 1, 5, 2]);
    }

    // OpenAC `ReplaceTable_SameListWithARepeat_IsNotAChange`.
    #[test]
    fn the_same_list_with_a_repeat_is_not_a_change() {
        let mut titles = TitleState::default();
        titles.replace_table(13, &[13, 1, 5, 1]);
        let revision = titles.revision();
        titles.replace_table(13, &[13, 1, 5, 1]);
        assert_eq!(titles.revision(), revision);
        assert_eq!(titles.earned_title_ids().collect::<Vec<_>>(), [13, 1, 5]);
    }

    #[test]
    fn replace_table_is_wholesale_and_drops_ids_missing_from_the_new_table() {
        let mut titles = TitleState::default();
        titles.replace_table(1, &[1, 2, 3]);
        titles.replace_table(1, &[1]);
        assert_eq!(titles.earned_title_ids().collect::<Vec<_>>(), [1]);
        assert!(!titles.has_earned_title(2));
        assert!(!titles.has_earned_title(3));
    }

    #[test]
    fn replace_table_reports_the_display_title_only_when_it_moves() {
        let mut titles = TitleState::default();
        titles.replace_table(13, &[13]);
        assert_eq!(titles.replace_table(13, &[13, 14]).display_changed, None);
        assert_eq!(titles.display_title_id(), 13);
        assert_eq!(
            titles.replace_table(14, &[13, 14]).display_changed,
            Some(14)
        );
        assert_eq!(titles.display_title_id(), 14);
    }

    #[test]
    fn an_update_always_adds_even_when_not_displayed() {
        let mut titles = TitleState::default();
        let change = titles.apply_update_title(7, false);
        assert!(titles.has_earned_title(7));
        assert_eq!(change.added, Some(7));
        assert_eq!(change.display_changed, None);
        assert_eq!(titles.display_title_id(), 0);
    }

    #[test]
    fn an_update_to_display_adds_and_displays() {
        let mut titles = TitleState::default();
        let change = titles.apply_update_title(13, true);
        assert!(titles.has_earned_title(13));
        assert_eq!(change.added, Some(13));
        assert_eq!(change.display_changed, Some(13));
        assert_eq!(titles.display_title_id(), 13);
    }

    #[test]
    fn an_update_for_a_held_title_adds_nothing_and_keeps_the_revision() {
        let mut titles = TitleState::default();
        titles.apply_update_title(7, false);
        let revision = titles.revision();
        let change = titles.apply_update_title(7, false);
        assert_eq!(change.added, None);
        assert_eq!(titles.count(), 1);
        assert_eq!(titles.revision(), revision);
    }

    #[test]
    fn displaying_the_displayed_title_again_changes_nothing() {
        let mut titles = TitleState::default();
        titles.apply_update_title(13, true);
        let revision = titles.revision();
        assert_eq!(titles.apply_update_title(13, true), TitleChange::default());
        assert_eq!(titles.revision(), revision);
    }

    #[test]
    fn a_new_displayed_title_moves_the_revision_twice() {
        let mut titles = TitleState::default();
        let before = titles.revision();
        titles.apply_update_title(13, true);
        assert_eq!(titles.revision(), before + 2);
    }

    #[test]
    fn displaying_an_unearned_title_earns_it() {
        let mut titles = TitleState::default();
        titles.apply_update_title(99, true);
        assert!(titles.has_earned_title(99));
        assert_eq!(titles.display_title_id(), 99);
    }

    #[test]
    fn an_identical_table_keeps_the_revision_but_still_replaces() {
        let mut titles = TitleState::default();
        titles.replace_table(13, &[1, 5, 13]);
        let revision = titles.revision();
        assert!(titles.replace_table(13, &[1, 5, 13]).table_replaced);
        assert_eq!(titles.revision(), revision);
    }

    #[test]
    fn a_reset_clears_and_always_moves_the_revision() {
        let mut titles = TitleState::default();
        titles.replace_table(13, &[1, 5, 13]);
        let change = titles.reset_session();
        assert_eq!(titles.display_title_id(), 0);
        assert_eq!(titles.count(), 0);
        assert!(change.table_replaced);
        assert_eq!(change.display_changed, Some(0));
        let before = titles.revision();
        // A second reset still replaces the table but moves no title.
        let change = titles.reset_session();
        assert!(titles.revision() > before);
        assert!(change.table_replaced);
        assert_eq!(change.display_changed, None);
    }

    #[test]
    fn every_table_and_reset_counts_and_a_notice_does_not() {
        // The changes OpenAC reports as `TableReplaced`.
        let mut titles = TitleState::default();
        titles.replace_table(13, &[13]);
        titles.replace_table(13, &[13]);
        assert_eq!(titles.table_generation(), 2);
        titles.apply_update_title(14, true);
        assert_eq!(titles.table_generation(), 2);
        titles.reset_session();
        assert_eq!(titles.table_generation(), 3);
    }

    #[test]
    fn the_view_carries_the_display_title_and_every_id() {
        let mut titles = TitleState::default();
        titles.replace_table(13, &[13, 1, 5]);
        assert_eq!(
            titles.view(),
            TitlesView {
                display_title_id: 13,
                earned: vec![13, 1, 5]
            }
        );
    }
}
