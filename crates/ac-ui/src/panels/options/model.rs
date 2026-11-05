//! The option page model the game's option pages share: every row keeps
//! its current, saved and default value; a change applies live at once and
//! only Apply commits it; Reset puts the changed rows back, Defaults puts
//! every row to its default; showing a page applies it, hiding it resets.
//!
//! The rows hold values only. What a row's value does (the widget it moves,
//! the setting it writes, where its live value is read from) is the page
//! owner's, reached through [`RowHost`] by row index.

/// A row's value.
#[derive(Debug, Clone, PartialEq)]
pub enum OptionValue {
    /// A checkbox.
    Bool(bool),
    /// A slider, in the setting's own unit.
    Float(f32),
    /// A menu choice by number.
    Int(i32),
    /// A menu choice by name.
    Text(String),
    /// A bitfield (the chat windows' filters).
    Bits(u64),
}

/// What a page's rows act on.
pub trait RowHost {
    /// Make row `row`'s value `value` live: move its widget and change the
    /// setting it edits.
    fn apply(&mut self, row: usize, value: &OptionValue);
    /// The live value of row `row`'s setting (`None`: the row keeps its
    /// own).
    fn read(&mut self, row: usize) -> Option<OptionValue>;
    /// Show `value` on row `row`'s widget without changing anything else.
    fn refresh(&mut self, row: usize, value: &OptionValue);
    /// Rows whose live value another row's change moved (a linked pair):
    /// each takes the value without applying it back.
    fn take_links(&mut self) -> Vec<(usize, OptionValue)> {
        Vec::new()
    }
    /// The page was applied (the owner saves what must be saved).
    fn after_apply(&mut self) {}
    /// A row or the page changed; `changed` is whether any row now differs
    /// from its saved value.
    fn option_changed(&mut self, _changed: bool) {}
}

#[derive(Debug, Clone, PartialEq)]
struct Row {
    current: OptionValue,
    saved: OptionValue,
    default: OptionValue,
}

/// One option page.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct OptionPage {
    rows: Vec<Row>,
    /// Whether an Apply tells the owner ([`RowHost::after_apply`]).
    pub after_apply: bool,
}

impl OptionPage {
    /// An empty page.
    #[must_use]
    pub fn new(after_apply: bool) -> Self {
        Self {
            rows: Vec::new(),
            after_apply,
        }
    }

    /// Add a row starting at `initial` (also its saved value); its index.
    pub fn register(&mut self, initial: OptionValue, default: OptionValue) -> usize {
        self.rows.push(Row {
            saved: initial.clone(),
            current: initial,
            default,
        });
        self.rows.len() - 1
    }

    /// Number of rows.
    #[must_use]
    pub fn len(&self) -> usize {
        self.rows.len()
    }

    /// True without rows.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.rows.is_empty()
    }

    /// Row `i`'s current value.
    #[must_use]
    pub fn current(&self, i: usize) -> Option<&OptionValue> {
        self.rows.get(i).map(|r| &r.current)
    }

    /// Row `i`'s saved value.
    #[must_use]
    pub fn saved(&self, i: usize) -> Option<&OptionValue> {
        self.rows.get(i).map(|r| &r.saved)
    }

    /// Row `i`'s default value.
    #[must_use]
    pub fn default_value(&self, i: usize) -> Option<&OptionValue> {
        self.rows.get(i).map(|r| &r.default)
    }

    /// Whether row `i` differs from its saved value.
    #[must_use]
    pub fn row_changed(&self, i: usize) -> bool {
        self.rows.get(i).is_some_and(|r| r.current != r.saved)
    }

    /// Whether any row differs from its saved value.
    #[must_use]
    pub fn changed(&self) -> bool {
        self.rows.iter().any(|r| r.current != r.saved)
    }

    /// The player changed row `i`: live at once, not committed.
    pub fn set_current(&mut self, i: usize, value: OptionValue, host: &mut dyn RowHost) {
        let Some(row) = self.rows.get_mut(i) else {
            return;
        };
        row.current = value.clone();
        host.apply(i, &value);
        self.follow_links(host);
        host.option_changed(self.changed());
    }

    /// Row `i` takes a value it did not choose (its live setting changed
    /// elsewhere), without applying it back.
    pub fn refresh_from_link(&mut self, i: usize, value: OptionValue, host: &mut dyn RowHost) {
        let Some(row) = self.rows.get_mut(i) else {
            return;
        };
        row.current = value.clone();
        host.refresh(i, &value);
        host.option_changed(self.changed());
    }

    fn follow_links(&mut self, host: &mut dyn RowHost) {
        for (j, value) in host.take_links() {
            self.refresh_from_link(j, value, host);
        }
    }

    /// Row `i` re-reads its live value and commits it.
    fn save_current(&mut self, i: usize, host: &mut dyn RowHost) {
        if let Some(value) = host.read(i) {
            self.rows[i].current = value.clone();
            host.refresh(i, &value);
        }
        let row = &mut self.rows[i];
        row.saved = row.current.clone();
    }

    fn restore_saved(&mut self, i: usize, host: &mut dyn RowHost) {
        let Some(row) = self.rows.get_mut(i) else {
            return;
        };
        row.current = row.saved.clone();
        let value = row.current.clone();
        host.apply(i, &value);
        self.follow_links(host);
    }

    fn restore_default(&mut self, i: usize, host: &mut dyn RowHost) {
        let Some(row) = self.rows.get_mut(i) else {
            return;
        };
        row.current = row.default.clone();
        let value = row.current.clone();
        host.apply(i, &value);
        self.follow_links(host);
    }

    /// Apply: every row commits its live value; then the owner is told.
    pub fn apply(&mut self, host: &mut dyn RowHost) {
        for i in 0..self.rows.len() {
            self.save_current(i, host);
        }
        if self.after_apply {
            host.after_apply();
        }
        host.option_changed(self.changed());
    }

    /// Reset: the rows changed now go back to their saved values.
    pub fn reset(&mut self, host: &mut dyn RowHost) {
        let changed: Vec<usize> = (0..self.rows.len())
            .filter(|&i| self.row_changed(i))
            .collect();
        for i in changed {
            self.restore_saved(i, host);
        }
        host.option_changed(self.changed());
    }

    /// Defaults: every row to its default, live, not committed.
    pub fn defaults(&mut self, host: &mut dyn RowHost) {
        for i in 0..self.rows.len() {
            self.restore_default(i, host);
        }
        host.option_changed(self.changed());
    }

    /// The page was shown: it is applied.
    pub fn on_shown(&mut self, host: &mut dyn RowHost) {
        self.apply(host);
    }

    /// The page was hidden: what was not applied goes back.
    pub fn on_hidden(&mut self, host: &mut dyn RowHost) {
        self.reset(host);
    }

    /// The live values changed underneath (the server's options arrived):
    /// every row re-reads and commits them; the owner is not told.
    pub fn reload_from_live(&mut self, host: &mut dyn RowHost) {
        for i in 0..self.rows.len() {
            self.save_current(i, host);
        }
        host.option_changed(self.changed());
    }

    /// Keep the first `retained` rows.
    pub fn remove_tail(&mut self, retained: usize, host: &mut dyn RowHost) {
        self.rows.truncate(retained);
        host.option_changed(self.changed());
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/OptionPageModelTests.cs`.
    //! The row's `apply`, `read` and `refresh` delegates are the host's;
    //! `OnOptionChanged` and `AfterApply` are the host's two calls. The
    //! render-pack tail case (a row whose apply removes rows mid-Defaults)
    //! has no counterpart: render packs are not ported.

    use std::collections::HashMap;

    use super::*;

    #[derive(Default)]
    struct Host {
        applied: Vec<(usize, OptionValue)>,
        refreshed: Vec<(usize, OptionValue)>,
        live: HashMap<usize, OptionValue>,
        events: Vec<&'static str>,
        flushes: usize,
        notifies: usize,
    }

    impl RowHost for Host {
        fn apply(&mut self, row: usize, value: &OptionValue) {
            self.applied.push((row, value.clone()));
        }
        fn read(&mut self, row: usize) -> Option<OptionValue> {
            self.live.get(&row).cloned()
        }
        fn refresh(&mut self, row: usize, value: &OptionValue) {
            self.refreshed.push((row, value.clone()));
        }
        fn after_apply(&mut self) {
            self.flushes += 1;
            self.events.push("afterApply");
        }
        fn option_changed(&mut self, _changed: bool) {
            self.notifies += 1;
            self.events.push("onOptionChanged");
        }
    }

    use OptionValue::Bool;

    #[test]
    fn an_empty_page_is_never_changed_and_its_verbs_do_nothing() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        assert!(!page.changed());
        page.apply(&mut host);
        page.reset(&mut host);
        page.defaults(&mut host);
        page.on_shown(&mut host);
        page.on_hidden(&mut host);
        assert!(page.is_empty());
        assert!(!page.changed());
        assert_eq!(host.flushes, 0);
    }

    #[test]
    fn an_empty_page_with_after_apply_still_flushes_on_apply_and_on_shown() {
        let mut page = OptionPage::new(true);
        let mut host = Host::default();
        page.apply(&mut host);
        assert_eq!(host.flushes, 1);
        page.on_shown(&mut host);
        assert_eq!(host.flushes, 2);
        page.reset(&mut host);
        page.defaults(&mut host);
        assert_eq!(host.flushes, 2, "reset and defaults never flush");
    }

    #[test]
    fn set_current_applies_live_without_committing() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(false));
        page.set_current(i, Bool(true), &mut host);
        assert_eq!(page.current(i), Some(&Bool(true)));
        assert_eq!(page.saved(i), Some(&Bool(false)));
        assert!(page.row_changed(i));
        assert_eq!(host.applied, vec![(i, Bool(true))]);
    }

    #[test]
    fn apply_commits_the_baseline() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(false));
        page.set_current(i, Bool(true), &mut host);
        page.apply(&mut host);
        assert_eq!(page.saved(i), Some(&Bool(true)));
        assert!(!page.row_changed(i));
    }

    #[test]
    fn reset_reverts_and_reapplies() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(false));
        page.set_current(i, Bool(true), &mut host);
        host.applied.clear();
        page.reset(&mut host);
        assert_eq!(page.current(i), Some(&Bool(false)));
        assert!(!page.row_changed(i));
        assert_eq!(host.applied, vec![(i, Bool(false))]);
    }

    #[test]
    fn defaults_apply_live_without_committing() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(true));
        page.defaults(&mut host);
        assert_eq!(page.current(i), Some(&Bool(true)));
        assert_eq!(page.saved(i), Some(&Bool(false)));
        assert!(page.row_changed(i));
        assert_eq!(host.applied, vec![(i, Bool(true))]);
    }

    fn two() -> (OptionPage, usize, usize) {
        let mut page = OptionPage::new(false);
        let a = page.register(Bool(true), Bool(true));
        let b = page.register(Bool(false), Bool(true));
        (page, a, b)
    }

    #[test]
    fn a_page_is_changed_when_any_row_is() {
        let (mut page, a, _) = two();
        let mut host = Host::default();
        assert!(!page.changed());
        page.set_current(a, Bool(false), &mut host);
        assert!(page.changed());
    }

    #[test]
    fn apply_commits_every_row() {
        let (mut page, a, b) = two();
        let mut host = Host::default();
        page.set_current(a, Bool(false), &mut host);
        page.apply(&mut host);
        assert!(!page.changed());
        assert_eq!(page.saved(a), page.current(a));
        assert_eq!(page.saved(b), page.current(b));
    }

    #[test]
    fn reset_reverts_only_changed_rows() {
        let (mut page, a, b) = two();
        let mut host = Host::default();
        page.set_current(a, Bool(false), &mut host);
        host.applied.clear();
        page.reset(&mut host);
        assert_eq!(page.current(a), Some(&Bool(true)));
        assert_eq!(page.current(b), Some(&Bool(false)));
        assert!(!page.changed());
        assert_eq!(host.applied, vec![(a, Bool(true))]);
    }

    #[test]
    fn defaults_restore_every_row_without_committing() {
        let (mut page, a, b) = two();
        let mut host = Host::default();
        page.set_current(a, Bool(false), &mut host);
        page.apply(&mut host);
        page.defaults(&mut host);
        assert_eq!(page.current(a), Some(&Bool(true)));
        assert_eq!(page.current(b), Some(&Bool(true)));
        assert!(page.changed());
    }

    #[test]
    fn hiding_reverts_and_showing_commits() {
        let (mut page, a, _) = two();
        let mut host = Host::default();
        page.set_current(a, Bool(false), &mut host);
        page.on_hidden(&mut host);
        assert_eq!(page.current(a), Some(&Bool(true)));
        assert!(!page.changed());
        page.set_current(a, Bool(false), &mut host);
        page.on_shown(&mut host);
        assert!(!page.changed());
    }

    #[test]
    fn the_change_notice_is_the_last_step_of_apply_and_comes_on_every_verb() {
        let (mut page, a, _) = two();
        page.after_apply = true;
        let mut host = Host::default();
        page.set_current(a, Bool(false), &mut host);
        host.events.clear();
        page.apply(&mut host);
        assert_eq!(host.events, vec!["afterApply", "onOptionChanged"]);
        host.notifies = 0;
        page.reset(&mut host);
        assert_eq!(host.notifies, 1);
        page.defaults(&mut host);
        assert_eq!(host.notifies, 2);
        let mut empty = OptionPage::new(false);
        host.notifies = 0;
        empty.apply(&mut host);
        empty.reset(&mut host);
        empty.defaults(&mut host);
        assert_eq!(host.notifies, 3);
    }

    #[test]
    fn a_live_edit_notifies_the_page_and_restores_do_not_on_their_own() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(true));
        page.set_current(i, Bool(true), &mut host);
        assert_eq!(host.notifies, 1);
        host.notifies = 0;
        page.restore_saved(i, &mut host);
        page.restore_default(i, &mut host);
        assert_eq!(host.notifies, 0);
    }

    #[test]
    fn saving_re_reads_the_live_source_and_refreshes_the_widget() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let i = page.register(Bool(true), Bool(false));
        host.live.insert(i, Bool(false));
        page.save_current(i, &mut host);
        assert_eq!(page.current(i), Some(&Bool(false)));
        assert!(!page.row_changed(i));
        assert_eq!(host.refreshed.last(), Some(&(i, Bool(false))));
        host.live.insert(i, Bool(true));
        page.save_current(i, &mut host);
        assert_eq!(page.current(i), Some(&Bool(true)));
        assert_eq!(host.refreshed.last(), Some(&(i, Bool(true))));
    }

    #[test]
    fn reload_from_live_re_reads_without_after_apply() {
        let mut page = OptionPage::new(true);
        let mut host = Host::default();
        let i = page.register(Bool(false), Bool(false));
        host.live.insert(i, Bool(true));
        page.reload_from_live(&mut host);
        assert_eq!(page.current(i), Some(&Bool(true)));
        assert_eq!(host.refreshed, vec![(i, Bool(true))]);
        assert!(!page.row_changed(i));
        assert_eq!(host.flushes, 0);
        assert_eq!(host.notifies, 1);
    }

    #[test]
    fn remove_tail_drops_the_rows() {
        let mut page = OptionPage::new(false);
        let mut host = Host::default();
        let kept = page.register(Bool(false), Bool(false));
        let removed = page.register(Bool(false), Bool(false));
        page.remove_tail(1, &mut host);
        host.notifies = 0;
        page.set_current(removed, Bool(true), &mut host);
        assert_eq!(page.len(), 1);
        assert_eq!(page.current(kept), Some(&Bool(false)));
        assert_eq!(host.notifies, 0);
        assert!(!page.changed());
    }

    struct Linked {
        inner: Host,
        links: Vec<(usize, OptionValue)>,
    }

    impl RowHost for Linked {
        fn apply(&mut self, row: usize, value: &OptionValue) {
            self.inner.apply(row, value);
            // Row 0 drags row 1 up to it (the chat opacity pair).
            if row == 0 {
                self.links.push((1, value.clone()));
            }
        }
        fn read(&mut self, row: usize) -> Option<OptionValue> {
            self.inner.read(row)
        }
        fn refresh(&mut self, row: usize, value: &OptionValue) {
            self.inner.refresh(row, value);
        }
        fn take_links(&mut self) -> Vec<(usize, OptionValue)> {
            std::mem::take(&mut self.links)
        }
    }

    #[test]
    fn a_linked_row_takes_the_value_without_applying_it_back() {
        let mut page = OptionPage::new(false);
        let mut host = Linked {
            inner: Host::default(),
            links: Vec::new(),
        };
        let a = page.register(OptionValue::Float(0.2), OptionValue::Float(0.5));
        let b = page.register(OptionValue::Float(0.4), OptionValue::Float(1.0));
        page.set_current(a, OptionValue::Float(0.9), &mut host);
        assert_eq!(page.current(b), Some(&OptionValue::Float(0.9)));
        assert_eq!(host.inner.applied, vec![(a, OptionValue::Float(0.9))]);
        assert_eq!(host.inner.refreshed, vec![(b, OptionValue::Float(0.9))]);
    }
}
