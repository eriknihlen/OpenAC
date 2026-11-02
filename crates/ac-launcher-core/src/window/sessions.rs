//! A session as the session log shows it: who is playing, and are they in
//! yet, not which launch mode started the process.

use crate::orchestration::{LauncherActivityKind, LauncherActivityState, LauncherSessionSnapshot};

/// One line of the session log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionRow {
    /// The session's id.
    pub session_id: String,
    /// Its account.
    pub account: String,
    /// Its server.
    pub server: String,
    /// The character, `Character select` before one is known, or
    /// `Character refresh`.
    pub character: String,
    /// Where it is, in plain words.
    pub state: String,
    /// What it is doing or how it ended.
    pub status: String,
    /// Its error, when it has one.
    pub error: Option<String>,
    /// Plugins left out at launch.
    pub plugin_notice: Option<String>,
    /// Whether it is still running (Stop is offered only then).
    pub is_active: bool,
}

impl SessionRow {
    /// The row for `snapshot`.
    pub fn new(snapshot: &LauncherSessionSnapshot) -> Self {
        Self {
            session_id: snapshot.session_id.clone(),
            account: snapshot.account_name.clone(),
            server: snapshot.server_name.clone(),
            character: describe_character(snapshot),
            state: describe_state(snapshot).to_owned(),
            status: describe_status(snapshot),
            error: snapshot.error.clone(),
            plugin_notice: snapshot.plugin_notice.clone(),
            is_active: snapshot.is_process_or_start_active(),
        }
    }

    /// Whether it has an error to show.
    pub fn has_error(&self) -> bool {
        self.error
            .as_deref()
            .is_some_and(|error| !error.trim().is_empty())
    }
}

/// A finished session says how it ended; a running one shows its status.
pub fn describe_status(snapshot: &LauncherSessionSnapshot) -> String {
    if snapshot.is_active() {
        return snapshot.status.clone();
    }
    if snapshot.state == LauncherActivityState::Cancelled {
        return "Cancelled before it started.".into();
    }
    if snapshot.exited_gracefully {
        return if snapshot.kind == LauncherActivityKind::Probe {
            "Finished reading characters.".into()
        } else {
            "Exited gracefully — logged out cleanly.".into()
        };
    }
    // Not graceful: the host never reported its own exit, or reported a failure. The server may
    // still be holding the account, and saying so here is what stops the next login failure
    // being a mystery. A refusal from the server is told in its own words.
    if let Some(message) = snapshot
        .server_message
        .as_deref()
        .map(|message| message.trim().trim_end_matches('.'))
        .filter(|message| !message.is_empty())
    {
        return format!("{message} — the server may hold this account for a short while.");
    }
    let detail = match snapshot.exit_reason.as_deref() {
        Some("connection-error") => "Could not reach the server",
        Some("credential-error") => "The account or password was rejected",
        Some("configuration-error") => "The session configuration was rejected",
        Some("usage-error") => "The client rejected how it was started",
        None | Some("") => "Closed unexpectedly",
        Some(_) => "Stopped unexpectedly",
    };
    format!("{detail} — the server may hold this account for a short while.")
}

/// `Character refresh` for a refresh, the character once known, else
/// `Character select`.
pub fn describe_character(snapshot: &LauncherSessionSnapshot) -> String {
    if snapshot.kind == LauncherActivityKind::Probe {
        return "Character refresh".into();
    }
    match snapshot.character_name.as_deref() {
        Some(name) if !name.trim().is_empty() => name.to_owned(),
        _ => "Character select".into(),
    }
}

/// The state in plain words.
pub fn describe_state(snapshot: &LauncherSessionSnapshot) -> &'static str {
    match snapshot.state {
        LauncherActivityState::Starting | LauncherActivityState::Running => "Starting",
        LauncherActivityState::Connected => {
            if snapshot.kind == LauncherActivityKind::Probe {
                "Reading characters"
            } else {
                "Character select"
            }
        }
        LauncherActivityState::InWorld => "In game",
        LauncherActivityState::Disconnected | LauncherActivityState::Stopping => "Stopping",
        LauncherActivityState::Failed => "Failed",
        LauncherActivityState::Exited | LauncherActivityState::Cancelled => "Stopped",
    }
}
