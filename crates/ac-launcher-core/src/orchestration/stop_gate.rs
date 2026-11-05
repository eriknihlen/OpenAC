//! Which sessions a Stop was requested for, held until each one's process
//! and start-up work actually end.

use std::collections::HashSet;

use super::snapshot::LauncherStateSnapshot;

/// Tracks each stop request until its process and start-up work finish.
#[derive(Debug, Default, Clone)]
pub struct LauncherStopGate {
    sessions: HashSet<String>,
}

impl LauncherStopGate {
    /// No stop pending.
    pub fn new() -> Self {
        Self::default()
    }

    /// Some stop is still completing.
    pub fn is_pending(&self) -> bool {
        !self.sessions.is_empty()
    }

    /// A stop is already pending for `session_id`.
    pub fn is_stopping(&self, session_id: &str) -> bool {
        self.sessions.contains(session_id)
    }

    /// Records a stop for `session_id`; false when one is already pending
    /// or the session's process and start-up have ended.
    pub fn try_begin(&mut self, session_id: &str, snapshot: &LauncherStateSnapshot) -> bool {
        self.observe(snapshot);
        if self.is_stopping(session_id)
            || !snapshot.sessions.iter().any(|session| {
                session.session_id == session_id && session.is_process_or_start_active()
            })
        {
            return false;
        }
        self.sessions.insert(session_id.to_owned());
        true
    }

    /// Forgets the stops whose session's process and start-up ended.
    pub fn observe(&mut self, snapshot: &LauncherStateSnapshot) {
        self.sessions.retain(|id| {
            snapshot
                .sessions
                .iter()
                .any(|session| &session.session_id == id && session.is_process_or_start_active())
        });
    }
}
