//! The selected object: what a spell, an attack and the health meter are
//! aimed at. A change is reported as a [`SelectionTransition`], which the
//! combat owner reads (health stream, auto-target).

/// Who changed the selection.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionSource {
    /// The client itself (a death, a removal).
    System,
    /// The keyboard (select-nearest, escape).
    Keyboard,
    /// A host command.
    Host,
}

/// Why the selection changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionReason {
    /// Something was selected.
    Selected,
    /// The selection was cleared.
    Cleared,
    /// The selected object left the world.
    SelectedObjectRemoved,
    /// The selected creature died.
    CombatTargetDied,
    /// The previous selection came back.
    PreviousSelection,
    /// The session ended.
    SessionReset,
}

/// One change of the selection.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectionTransition {
    /// What was selected before.
    pub previous: Option<u32>,
    /// What is selected now.
    pub selected: Option<u32>,
    /// Who changed it.
    pub source: SelectionSource,
    /// Why.
    pub reason: SelectionReason,
}

/// The selection and the two before it.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Selection {
    selected: Option<u32>,
    previous: Option<u32>,
    previous_valid: Option<u32>,
}

impl Selection {
    /// The selected object.
    #[must_use]
    pub fn selected(&self) -> Option<u32> {
        self.selected
    }

    /// The selection before the current one (may be none).
    #[must_use]
    pub fn previous(&self) -> Option<u32> {
        self.previous
    }

    /// The last object that was selected before the current selection.
    #[must_use]
    pub fn previous_valid(&self) -> Option<u32> {
        self.previous_valid
    }

    /// Select an object (0 clears). `None` when nothing changed.
    pub fn select(
        &mut self,
        object_id: u32,
        source: SelectionSource,
        reason: SelectionReason,
    ) -> Option<SelectionTransition> {
        if object_id == 0 {
            return self.clear(source, SelectionReason::Cleared);
        }
        self.set(Some(object_id), source, reason)
    }

    /// Clear the selection. `None` when nothing was selected.
    pub fn clear(
        &mut self,
        source: SelectionSource,
        reason: SelectionReason,
    ) -> Option<SelectionTransition> {
        self.set(None, source, reason)
    }

    /// Bring back the previous selection.
    pub fn select_previous(&mut self, source: SelectionSource) -> Option<SelectionTransition> {
        let previous = self.previous?;
        self.set(Some(previous), source, SelectionReason::PreviousSelection)
    }

    /// Forget everything. Always reports a transition, as observers must
    /// drop what they hold for the session.
    pub fn reset(&mut self) -> SelectionTransition {
        let old = self.selected;
        *self = Self::default();
        SelectionTransition {
            previous: old,
            selected: None,
            source: SelectionSource::System,
            reason: SelectionReason::SessionReset,
        }
    }

    fn set(
        &mut self,
        selected: Option<u32>,
        source: SelectionSource,
        reason: SelectionReason,
    ) -> Option<SelectionTransition> {
        if self.selected == selected {
            return None;
        }
        let old = self.selected;
        self.selected = selected;
        self.previous = old;
        if old.is_some() {
            self.previous_valid = old;
        }
        Some(SelectionTransition {
            previous: old,
            selected,
            source,
            reason,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Also OpenAC `tests/AcDream.Core.Tests/Selection/SelectionStateTests.cs`
    //! (observers become the returned transitions).
    use super::*;

    const A: u32 = 0x5000_0001;
    const B: u32 = 0x5000_0002;

    #[test]
    fn reset_clears_the_selection_and_its_history() {
        let mut s = Selection::default();
        s.select(A, SelectionSource::Host, SelectionReason::Selected);
        s.select(B, SelectionSource::Host, SelectionReason::Selected);
        let t = s.reset();
        assert_eq!(
            (s.selected(), s.previous(), s.previous_valid()),
            (None, None, None)
        );
        assert_eq!(t.previous, Some(B));
        assert_eq!(t.reason, SelectionReason::SessionReset);
        assert!(s.select_previous(SelectionSource::Keyboard).is_none());
    }

    #[test]
    fn a_select_is_one_transition_and_tracks_the_previous() {
        let mut s = Selection::default();
        assert!(
            s.select(A, SelectionSource::Host, SelectionReason::Selected)
                .is_some()
        );
        let t = s
            .select(B, SelectionSource::Keyboard, SelectionReason::Selected)
            .unwrap();
        assert_eq!(
            (s.selected(), s.previous(), s.previous_valid()),
            (Some(B), Some(A), Some(A))
        );
        assert_eq!(
            (t.source, t.reason),
            (SelectionSource::Keyboard, SelectionReason::Selected)
        );
    }

    #[test]
    fn the_same_object_again_is_no_change() {
        let mut s = Selection::default();
        s.select(A, SelectionSource::Host, SelectionReason::Selected);
        assert!(
            s.select(A, SelectionSource::Keyboard, SelectionReason::Selected)
                .is_none()
        );
        assert_eq!(s.previous(), None);
    }

    #[test]
    fn select_previous_swaps_the_two() {
        let mut s = Selection::default();
        s.select(A, SelectionSource::Host, SelectionReason::Selected);
        s.select(B, SelectionSource::Host, SelectionReason::Selected);
        assert!(s.select_previous(SelectionSource::Keyboard).is_some());
        assert_eq!((s.selected(), s.previous()), (Some(A), Some(B)));
    }

    #[test]
    fn a_removed_object_stays_the_previous_valid_one() {
        let mut s = Selection::default();
        s.select(A, SelectionSource::Host, SelectionReason::Selected);
        let t = s
            .clear(
                SelectionSource::System,
                SelectionReason::SelectedObjectRemoved,
            )
            .unwrap();
        assert_eq!(
            (s.selected(), s.previous(), s.previous_valid()),
            (None, Some(A), Some(A))
        );
        assert_eq!(t.reason, SelectionReason::SelectedObjectRemoved);
    }

    #[test]
    fn a_change_records_the_previous_selection() {
        // OpenAC `SelectionState.Set`: previous is the old value even when
        // it was empty; previous-valid only remembers real objects.
        let mut s = Selection::default();
        let t = s
            .select(7, SelectionSource::Host, SelectionReason::Selected)
            .unwrap();
        assert_eq!((t.previous, t.selected), (None, Some(7)));
        assert!(
            s.select(7, SelectionSource::Host, SelectionReason::Selected)
                .is_none()
        );
        s.clear(SelectionSource::Keyboard, SelectionReason::Cleared);
        assert_eq!(
            (s.selected(), s.previous(), s.previous_valid()),
            (None, Some(7), Some(7))
        );
        let back = s.select_previous(SelectionSource::Keyboard).unwrap();
        assert_eq!(back.reason, SelectionReason::PreviousSelection);
        assert_eq!(s.selected(), Some(7));
        assert_eq!(s.reset().reason, SelectionReason::SessionReset);
        assert_eq!(s, Selection::default());
    }
}
