//! Which session the process's game window is on, told to that session's
//! status file as it changes (`windowOpened`, `windowClosed`,
//! `windowFailed`), so the launcher can show whether a running row has a
//! window from what the client reports, not from what it asked for.
//!
//! The console asks for an attach; the window host answers later on its
//! event channel. The tracker joins the two: the session an attach was
//! asked for waits as `pending` until the window reports it open or failed,
//! and the open window's session is `current` until it reports closed.

use std::collections::BTreeMap;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use ac_render::{DetachReason, HostEvent};

/// What a session's status file is told about its window.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum WindowNote {
    /// A window opened on the session.
    Opened,
    /// The session's window closed, for this reason.
    Closed(&'static str),
    /// A window was asked for and could not be opened.
    Failed(String),
}

impl WindowNote {
    /// Writes the note as its status event.
    pub(crate) fn report(&self, id: &str, status: &mut ac_host::StatusWriter<ac_host::FileSink>) {
        match self {
            Self::Opened => status.window_opened(id),
            Self::Closed(reason) => status.window_closed(id, reason),
            Self::Failed(error) => status.window_failed(id, error),
        }
    }
}

#[derive(Debug, Default)]
struct Slots {
    /// The session an attach was asked for, until the window answers.
    pending: Option<String>,
    /// The session the open window shows.
    current: Option<String>,
}

/// Shared by the console, which asks for attaches, and the thread that
/// reads the window host's events.
#[derive(Debug, Clone)]
pub(crate) struct WindowTracker {
    slots: Arc<Mutex<Slots>>,
    notes: Arc<BTreeMap<String, Sender<WindowNote>>>,
}

/// The status word for why a window closed.
fn reason_name(reason: DetachReason) -> &'static str {
    match reason {
        DetachReason::Requested => "requested",
        DetachReason::ClosedByUser => "closedByUser",
        DetachReason::Shutdown => "shutdown",
        DetachReason::RenderFailed => "renderFailed",
    }
}

impl WindowTracker {
    /// A tracker for the sessions `ids`, and for each the receiver its
    /// monitor drains into its status file.
    pub(crate) fn new<'a>(
        ids: impl IntoIterator<Item = &'a str>,
    ) -> (Self, BTreeMap<String, Receiver<WindowNote>>) {
        let mut senders = BTreeMap::new();
        let mut receivers = BTreeMap::new();
        for id in ids {
            let (sender, receiver) = mpsc::channel();
            senders.insert(id.to_owned(), sender);
            receivers.insert(id.to_owned(), receiver);
        }
        (
            Self {
                slots: Arc::default(),
                notes: Arc::new(senders),
            },
            receivers,
        )
    }

    fn slots(&self) -> MutexGuard<'_, Slots> {
        self.slots.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn note(&self, id: Option<&str>, note: WindowNote) {
        // A monitor that has ended no longer listens; its session is over.
        if let Some(sender) = id.and_then(|id| self.notes.get(id)) {
            let _ = sender.send(note);
        }
    }

    /// An attach to session `id` is about to be asked for.
    pub(crate) fn attaching(&self, id: &str) {
        self.slots().pending = Some(id.to_owned());
    }

    /// The session the window shows, or is opening on.
    pub(crate) fn session(&self) -> Option<String> {
        let slots = self.slots();
        slots.current.clone().or_else(|| slots.pending.clone())
    }

    /// A window for session `id` could not even be asked for.
    pub(crate) fn refused(&self, id: &str, error: &str) {
        self.note(Some(id), WindowNote::Failed(error.to_owned()));
    }

    /// Follows one window event. Returns the session whose window the
    /// player closed, if this event says so.
    pub(crate) fn follow(&self, event: &HostEvent) -> Option<String> {
        let change = match event {
            HostEvent::Attached(_) => Change::Opened,
            HostEvent::AttachFailed(error) => Change::Failed(error.to_string()),
            HostEvent::AttachRejected => Change::Failed("another window is already open".into()),
            HostEvent::Detached { reason, .. } => Change::Closed(*reason),
            _ => return None,
        };
        self.change(change)
    }

    pub(crate) fn change(&self, change: Change) -> Option<String> {
        let mut slots = self.slots();
        match change {
            Change::Opened => {
                let id = slots.pending.take();
                slots.current.clone_from(&id);
                drop(slots);
                self.note(id.as_deref(), WindowNote::Opened);
                None
            }
            Change::Failed(error) => {
                let id = slots.pending.take();
                drop(slots);
                self.note(id.as_deref(), WindowNote::Failed(error));
                None
            }
            Change::Closed(reason) => {
                let id = slots.current.take();
                drop(slots);
                self.note(id.as_deref(), WindowNote::Closed(reason_name(reason)));
                (reason == DetachReason::ClosedByUser)
                    .then_some(id)
                    .flatten()
            }
        }
    }
}

/// What a window event means for the tracker.
pub(crate) enum Change {
    Opened,
    Failed(String),
    Closed(DetachReason),
}

/// Every note waiting for one session, oldest first.
pub(crate) fn drain(receiver: Option<&Receiver<WindowNote>>) -> Vec<WindowNote> {
    receiver.map_or_else(Vec::new, |receiver| receiver.try_iter().collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn detached(reason: DetachReason) -> Change {
        Change::Closed(reason)
    }

    fn attached() -> Change {
        Change::Opened
    }

    #[test]
    fn an_attach_reports_open_and_a_close_reports_its_reason() {
        let (tracker, notes) = WindowTracker::new(["a", "b"]);
        tracker.attaching("a");
        assert_eq!(tracker.session().as_deref(), Some("a"));
        assert_eq!(tracker.change(attached()), None);
        assert_eq!(tracker.change(detached(DetachReason::Requested)), None);
        assert_eq!(tracker.session(), None);
        assert_eq!(
            drain(notes.get("a")),
            [WindowNote::Opened, WindowNote::Closed("requested")]
        );
        assert!(drain(notes.get("b")).is_empty());
    }

    #[test]
    fn moving_the_window_closes_it_on_one_session_and_opens_it_on_the_next() {
        let (tracker, notes) = WindowTracker::new(["a", "b"]);
        tracker.attaching("a");
        tracker.change(attached());
        // The console detaches A and asks for B before the host answers.
        tracker.attaching("b");
        tracker.change(detached(DetachReason::Requested));
        tracker.change(attached());
        assert_eq!(
            drain(notes.get("a")),
            [WindowNote::Opened, WindowNote::Closed("requested")]
        );
        assert_eq!(drain(notes.get("b")), [WindowNote::Opened]);
        assert_eq!(tracker.session().as_deref(), Some("b"));
    }

    #[test]
    fn the_player_closing_the_window_names_its_session() {
        let (tracker, notes) = WindowTracker::new(["a"]);
        tracker.attaching("a");
        tracker.change(attached());
        assert_eq!(
            tracker
                .change(detached(DetachReason::ClosedByUser))
                .as_deref(),
            Some("a")
        );
        assert_eq!(
            drain(notes.get("a")),
            [WindowNote::Opened, WindowNote::Closed("closedByUser")]
        );
    }

    #[test]
    fn a_failed_or_refused_window_reports_why() {
        let (tracker, notes) = WindowTracker::new(["a"]);
        tracker.attaching("a");
        tracker.change(Change::Failed("another window is already open".into()));
        tracker.refused("a", "this client runs without window support");
        assert_eq!(
            drain(notes.get("a")),
            [
                WindowNote::Failed("another window is already open".to_owned()),
                WindowNote::Failed("this client runs without window support".to_owned()),
            ]
        );
        assert_eq!(tracker.session(), None);
    }
}
