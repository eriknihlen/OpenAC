//! Closing the launcher over running clients: every client is logged out
//! at once, each on its own worker, and the window stays open on a
//! "Closing" view with one line per client until all have ended (an
//! addition over OpenAC, which closes at once).
//!
//! [`LauncherClosing`] only follows the sessions; the window starts each
//! stop it asks for and feeds it every snapshot.

use crate::orchestration::{LauncherSessionSnapshot, LauncherSessionStop, LauncherStateSnapshot};

/// Where one client is in the closing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ClosingClientState {
    /// Its stop runs: a graceful logout, then a kill after the timeout.
    LoggingOut,
    /// It ended.
    Closed,
    /// It did not log out within the timeout and was killed.
    DidNotRespond,
    /// "Close now" killed it.
    ClosedNow,
    /// Its stop failed; the text says why.
    Failed(String),
}

impl ClosingClientState {
    /// The line's state text.
    pub fn text(&self) -> String {
        match self {
            Self::LoggingOut => "Logging out…".into(),
            Self::Closed => "Closed".into(),
            Self::DidNotRespond => "Did not respond, closed by force".into(),
            Self::ClosedNow => "Closed by force".into(),
            Self::Failed(error) => format!("Could not close: {error}"),
        }
    }

    /// Still waiting for the client.
    pub fn is_pending(&self) -> bool {
        *self == Self::LoggingOut
    }
}

/// One client being closed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClosingClient {
    /// Its session.
    pub session_id: String,
    /// Its account.
    pub account_name: String,
    /// Its server.
    pub server_name: String,
    /// The character, once one is known.
    pub character_name: Option<String>,
    /// Where it is.
    pub state: ClosingClientState,
}

impl ClosingClient {
    /// `account / character` (or `account / server` before a character is
    /// known).
    pub fn title(&self) -> String {
        match self
            .character_name
            .as_deref()
            .filter(|name| !name.is_empty())
        {
            Some(character) => format!("{} / {character}", self.account_name),
            None => format!("{} / {}", self.account_name, self.server_name),
        }
    }
}

/// The closing's progress.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LauncherClosing {
    clients: Vec<ClosingClient>,
    force_requested: bool,
}

impl LauncherClosing {
    /// A closing with nothing followed yet.
    pub fn new() -> Self {
        Self::default()
    }

    /// The clients, in the order their stops started.
    pub fn clients(&self) -> &[ClosingClient] {
        &self.clients
    }

    /// Follows `snapshot`: every session whose process or start still runs
    /// and is not yet followed joins (its id is returned: the caller starts
    /// its stop, and a forced one after "Close now"); a followed session
    /// whose process and start have ended is closed.
    pub fn observe(&mut self, snapshot: &LauncherStateSnapshot) -> Vec<String> {
        let mut started = Vec::new();
        for session in &snapshot.sessions {
            if !session.is_process_or_start_active()
                || self
                    .clients
                    .iter()
                    .any(|client| client.session_id == session.session_id)
            {
                continue;
            }
            self.clients.push(ClosingClient {
                session_id: session.session_id.clone(),
                account_name: session.account_name.clone(),
                server_name: session.server_name.clone(),
                character_name: session.character_name.clone(),
                state: ClosingClientState::LoggingOut,
            });
            started.push(session.session_id.clone());
        }
        let force_requested = self.force_requested;
        for client in &mut self.clients {
            if matches!(client.state, ClosingClientState::Failed(_)) {
                continue;
            }
            let session = snapshot
                .sessions
                .iter()
                .find(|session| session.session_id == client.session_id);
            if let Some(name) = session.and_then(|session| session.character_name.clone()) {
                client.character_name = Some(name);
            }
            client.state = ended_state(session, force_requested);
        }
        started
    }

    /// A stop could not be carried out.
    pub fn stop_failed(&mut self, session_id: &str, error: String) {
        if let Some(client) = self
            .clients
            .iter_mut()
            .find(|client| client.session_id == session_id && client.state.is_pending())
        {
            client.state = ClosingClientState::Failed(error);
        }
    }

    /// "Close now": the clients still logging out, to be killed at once.
    pub fn request_force(&mut self) -> Vec<String> {
        self.force_requested = true;
        self.clients
            .iter()
            .filter(|client| client.state.is_pending())
            .map(|client| client.session_id.clone())
            .collect()
    }

    /// "Close now" was pressed.
    pub fn is_force_requested(&self) -> bool {
        self.force_requested
    }

    /// No client is still logging out.
    pub fn is_finished(&self) -> bool {
        !self.clients.iter().any(|client| client.state.is_pending())
    }

    /// `Closing 3 of 8 clients…` (how many still log out, of all), or that
    /// all have closed.
    pub fn summary(&self) -> String {
        let total = self.clients.len();
        let pending = self
            .clients
            .iter()
            .filter(|client| client.state.is_pending())
            .count();
        if pending == 0 {
            return if total == 1 {
                "The client has closed.".into()
            } else {
                "All clients have closed.".into()
            };
        }
        let noun = if total == 1 { "client" } else { "clients" };
        format!("Closing {pending} of {total} {noun}…")
    }
}

/// A followed session's state from its snapshot: still logging out while
/// its process or start runs; otherwise closed, by force when its stop had
/// to kill it. A session no longer listed has ended.
fn ended_state(
    session: Option<&LauncherSessionSnapshot>,
    force_requested: bool,
) -> ClosingClientState {
    match session {
        Some(session) if session.is_process_or_start_active() => ClosingClientState::LoggingOut,
        Some(session) if session.stop == Some(LauncherSessionStop::Forced) => {
            if force_requested {
                ClosingClientState::ClosedNow
            } else {
                ClosingClientState::DidNotRespond
            }
        }
        _ => ClosingClientState::Closed,
    }
}
