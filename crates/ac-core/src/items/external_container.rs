//! The one outside container the player is looking into (a chest, a
//! corpse, a container on the ground) and the corpses opened this session.
//!
//! Opening is two steps: the use request names the container, and the
//! server's contents list commits it. Asking for another container while
//! one is open retires the old one at once.

use std::collections::BTreeSet;

/// What happened to the viewed container.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExternalContainerTransitionKind {
    /// The requested container's contents arrived; it is now open.
    Opened,
    /// Another container was requested; the open one is retired.
    ReplacementRequested,
    /// The open container was closed.
    Closed,
    /// Session teardown.
    Reset,
}

/// A change of the viewed container.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ExternalContainerTransition {
    pub kind: ExternalContainerTransitionKind,
    pub previous_container_id: u32,
    pub container_id: u32,
}

/// The outcome of asking to open a container.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OpenRequestOutcome {
    /// Container 0, or already the requested one.
    Ignored,
    /// Now the requested container.
    Requested,
    /// Now the requested container; the open one was retired.
    ReplacementRequested(ExternalContainerTransition),
}

/// A session reset: whether anything was held, and the reset transition
/// (always announced).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ExternalContainerReset {
    pub changed: bool,
    pub transition: ExternalContainerTransition,
}

/// The requested and the open outside container, and opened corpses.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ExternalContainerState {
    requested_container_id: u32,
    current_container_id: u32,
    opened_corpses: BTreeSet<u32>,
}

impl ExternalContainerState {
    /// Nothing requested or open.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The container last asked for; 0 for none.
    #[must_use]
    pub fn requested_container_id(&self) -> u32 {
        self.requested_container_id
    }

    /// The open container; 0 for none.
    #[must_use]
    pub fn current_container_id(&self) -> u32 {
        self.current_container_id
    }

    /// How many corpses were opened this session (and not deleted since).
    #[must_use]
    pub fn opened_corpse_count(&self) -> usize {
        self.opened_corpses.len()
    }

    /// The player asked to open `container_id`. A corpse is remembered as
    /// opened even when the request repeats.
    pub fn request_open(&mut self, container_id: u32, is_corpse: bool) -> OpenRequestOutcome {
        if container_id == 0 {
            return OpenRequestOutcome::Ignored;
        }
        if is_corpse {
            self.opened_corpses.insert(container_id);
        }
        if self.requested_container_id == container_id {
            return OpenRequestOutcome::Ignored;
        }
        let previous = self.current_container_id;
        self.requested_container_id = container_id;
        if previous != 0 && previous != container_id {
            self.current_container_id = 0;
            return OpenRequestOutcome::ReplacementRequested(ExternalContainerTransition {
                kind: ExternalContainerTransitionKind::ReplacementRequested,
                previous_container_id: previous,
                container_id,
            });
        }
        OpenRequestOutcome::Requested
    }

    /// Whether the player opened this corpse this session.
    #[must_use]
    pub fn has_corpse_been_opened(&self, object_id: u32) -> bool {
        object_id != 0 && self.opened_corpses.contains(&object_id)
    }

    /// The corpse is gone; forget it. Returns whether it was remembered.
    pub fn set_corpse_deleted(&mut self, object_id: u32) -> bool {
        object_id != 0 && self.opened_corpses.remove(&object_id)
    }

    /// The server sent a contents list for `container_id`; it opens if it
    /// is the requested container and not open already.
    pub fn apply_view_contents(
        &mut self,
        container_id: u32,
    ) -> Option<ExternalContainerTransition> {
        if container_id == 0 || container_id != self.requested_container_id {
            return None;
        }
        let previous = self.current_container_id;
        self.current_container_id = container_id;
        self.requested_container_id = container_id;
        (previous != container_id).then_some(ExternalContainerTransition {
            kind: ExternalContainerTransitionKind::Opened,
            previous_container_id: previous,
            container_id,
        })
    }

    /// The server closed `container_id`; ignored unless it is the open one.
    pub fn apply_close(&mut self, container_id: u32) -> Option<ExternalContainerTransition> {
        if container_id == 0 || container_id != self.current_container_id {
            return None;
        }
        let previous = self.current_container_id;
        self.current_container_id = 0;
        self.requested_container_id = 0;
        Some(ExternalContainerTransition {
            kind: ExternalContainerTransitionKind::Closed,
            previous_container_id: previous,
            container_id: 0,
        })
    }

    /// A use finished with `weenie_error`: a refused open drops the request
    /// (back to whatever is open). Returns whether it did.
    pub fn apply_use_done(&mut self, weenie_error: u32) -> bool {
        if weenie_error == 0 || self.requested_container_id == self.current_container_id {
            return false;
        }
        self.requested_container_id = self.current_container_id;
        true
    }

    /// Session teardown: forget everything. The reset is announced even
    /// when nothing was held.
    pub fn reset(&mut self) -> ExternalContainerReset {
        let previous = self.current_container_id;
        let changed =
            previous != 0 || self.requested_container_id != 0 || !self.opened_corpses.is_empty();
        self.current_container_id = 0;
        self.requested_container_id = 0;
        self.opened_corpses.clear();
        ExternalContainerReset {
            changed,
            transition: ExternalContainerTransition {
                kind: ExternalContainerTransitionKind::Reset,
                previous_container_id: previous,
                container_id: 0,
            },
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ExternalContainerStateTests.cs`.
    //! The observer-failure half of
    //! `Reset_RetryRepublishesAndOneObserverCannotStarveAnother` has no
    //! counterpart (no observers); its "a second reset still announces"
    //! half is kept.

    use super::*;

    fn open(id: u32) -> ExternalContainerState {
        let mut state = ExternalContainerState::new();
        state.request_open(id, false);
        state.apply_view_contents(id);
        state
    }

    #[test]
    fn view_contents_only_commits_the_expected_root() {
        let mut state = ExternalContainerState::new();
        state.request_open(0x7000_0001, false);
        assert!(state.apply_view_contents(0x7000_0002).is_none());
        assert_eq!(state.current_container_id(), 0);
        assert!(state.apply_view_contents(0x7000_0001).is_some());
        assert_eq!(state.current_container_id(), 0x7000_0001);
    }

    #[test]
    fn replacement_retires_old_immediately_then_opens_new_on_view_contents() {
        let mut state = ExternalContainerState::new();
        let mut changes = Vec::new();
        state.request_open(1, false);
        changes.extend(state.apply_view_contents(1));
        if let OpenRequestOutcome::ReplacementRequested(t) = state.request_open(2, false) {
            changes.push(t);
        }
        assert_eq!(state.current_container_id(), 0);
        assert_eq!(state.requested_container_id(), 2);
        changes.extend(state.apply_view_contents(2));
        changes.extend(state.apply_view_contents(2));

        assert_eq!(changes.len(), 3);
        assert_eq!(changes[0].kind, ExternalContainerTransitionKind::Opened);
        assert_eq!(
            changes[1],
            ExternalContainerTransition {
                kind: ExternalContainerTransitionKind::ReplacementRequested,
                previous_container_id: 1,
                container_id: 2
            }
        );
        assert_eq!(
            changes[2],
            ExternalContainerTransition {
                kind: ExternalContainerTransitionKind::Opened,
                previous_container_id: 0,
                container_id: 2
            }
        );
    }

    #[test]
    fn close_ignores_stale_container_and_closes_current() {
        let mut state = open(2);
        assert!(state.apply_close(1).is_none());
        assert_eq!(state.current_container_id(), 2);
        assert!(state.apply_close(2).is_some());
        assert_eq!(state.current_container_id(), 0);
        assert_eq!(state.requested_container_id(), 0);
    }

    #[test]
    fn refused_replacement_leaves_the_retired_root_closed() {
        let mut state = open(1);
        state.request_open(2, false);
        assert!(state.apply_use_done(0x550));
        assert_eq!(state.current_container_id(), 0);
        assert_eq!(state.requested_container_id(), 0);
        assert!(state.apply_view_contents(2).is_none());
    }

    #[test]
    fn reset_is_announced_even_when_nothing_is_held() {
        let mut state = open(1);
        let first = state.reset();
        assert!(first.changed);
        assert_eq!(
            first.transition.kind,
            ExternalContainerTransitionKind::Reset
        );
        assert_eq!(first.transition.previous_container_id, 1);
        let second = state.reset();
        assert!(!second.changed);
        assert_eq!(
            second.transition.kind,
            ExternalContainerTransitionKind::Reset
        );
    }

    #[test]
    fn opened_corpse_history_matches_set_and_delete_lifetime() {
        let corpse = 0x7000_0010;
        let mut state = ExternalContainerState::new();
        assert_eq!(
            state.request_open(corpse, true),
            OpenRequestOutcome::Requested
        );
        assert!(state.has_corpse_been_opened(corpse));
        assert_eq!(state.opened_corpse_count(), 1);
        state.apply_view_contents(corpse);
        state.apply_close(corpse);
        assert!(state.has_corpse_been_opened(corpse));
        assert!(state.set_corpse_deleted(corpse));
        assert!(!state.has_corpse_been_opened(corpse));

        state.request_open(corpse, true);
        assert!(state.reset().changed);
        assert!(!state.has_corpse_been_opened(corpse));
        assert_eq!(state.opened_corpse_count(), 0);
    }

    #[test]
    fn repeated_ground_object_request_still_records_corpse_identity() {
        let corpse = 0x7000_0011;
        let mut state = ExternalContainerState::new();
        assert_eq!(
            state.request_open(corpse, false),
            OpenRequestOutcome::Requested
        );
        assert_eq!(
            state.request_open(corpse, true),
            OpenRequestOutcome::Ignored
        );
        assert!(state.has_corpse_been_opened(corpse));
    }
}
