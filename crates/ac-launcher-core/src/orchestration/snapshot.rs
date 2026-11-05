//! Read-only views of launcher state for the window. None carries a
//! password.

use std::time::SystemTime;

use super::capabilities::LauncherPlatformCapabilities;
use crate::profiles::LaunchMode;

/// One character row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherCharacterSnapshot {
    /// Its server.
    pub server_name: String,
    /// Its account.
    pub account_name: String,
    /// Its name.
    pub name: String,
    /// Its id, when known.
    pub id: Option<String>,
    /// How it launches.
    pub launch_mode: LaunchMode,
    /// The plugins it launches with: its own list, or its account's.
    pub plugins: Vec<String>,
    /// The commands run after it logs in: its account's shared ones, then
    /// its own.
    pub login_commands: Vec<String>,
    /// The profiles it is in.
    pub profiles: Vec<String>,
    /// Whether a play session for it is running.
    pub has_running_session: bool,
    /// That session's status, or `Not running`.
    pub session_status: String,
    /// It has a plugin list of its own instead of its account's.
    pub has_own_plugins: bool,
}

/// One account row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherAccountSnapshot {
    /// Its server.
    pub server_name: String,
    /// The account name.
    pub account_name: String,
    /// Its characters.
    pub characters: Vec<LauncherCharacterSnapshot>,
    /// Whether a session or refresh is running for it.
    pub has_running_activity: bool,
    /// That activity's status, or `Idle`.
    pub activity_status: String,
    /// The character the row is set to.
    pub selected_character: Option<String>,
    /// The launch mode the row is set to.
    pub selected_launch_mode: Option<LaunchMode>,
    /// The plugins every character on the account launches with unless it
    /// has its own.
    pub plugins: Vec<String>,
    /// The account's profile tags.
    pub profiles: Vec<String>,
    /// The shared commands, run in order after any character on the
    /// account logs in, before the character's own.
    pub login_commands: Vec<String>,
}

/// One server row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherServerSnapshot {
    /// Its name.
    pub name: String,
    /// Its host.
    pub host: String,
    /// Its port.
    pub port: i32,
    /// Its accounts.
    pub accounts: Vec<LauncherAccountSnapshot>,
}

/// A session that enters the world, or a character refresh.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherActivityKind {
    /// A play session.
    Play,
    /// A character refresh.
    Probe,
}

impl LauncherActivityKind {
    /// `play` or `probe`, for messages.
    pub const fn lower_name(self) -> &'static str {
        match self {
            Self::Play => "play",
            Self::Probe => "probe",
        }
    }
}

/// Where an activity is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherActivityState {
    /// Being prepared or started.
    Starting,
    /// The process runs.
    Running,
    /// Connected to the server.
    Connected,
    /// A character is in the world.
    InWorld,
    /// The connection dropped.
    Disconnected,
    /// Being stopped.
    Stopping,
    /// Ended.
    Exited,
    /// Could not start or failed.
    Failed,
    /// Cancelled before it ran.
    Cancelled,
}

impl LauncherActivityState {
    /// Not ended, failed or cancelled.
    pub const fn is_active(self) -> bool {
        !matches!(self, Self::Exited | Self::Failed | Self::Cancelled)
    }
}

/// Whether a running play session has a game window, as its client last
/// reported, and a switch the row asked for (an addition: OpenAC's
/// launcher cannot switch a running client).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LauncherSessionWindow {
    /// The client reports a game window open on the session (a graphical
    /// launch counts as open until the client says otherwise).
    pub is_open: bool,
    /// The state a switch asked for, until the client answers or the switch
    /// times out.
    pub switching_to: Option<bool>,
    /// Why the last switch did not happen, until the next one.
    pub error: Option<String>,
}

impl LauncherSessionWindow {
    /// What the row shows: the asked-for state while a switch is pending,
    /// otherwise the reported one.
    pub fn shows_window(&self) -> bool {
        self.switching_to.unwrap_or(self.is_open)
    }
}

/// A Stop the player asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherSessionStop {
    /// Asked for: a graceful logout, then a kill after the timeout.
    Requested,
    /// The client did not log out in time and was killed. It is a stop, not
    /// a crash (no relaunch), but the server may still hold the account.
    Forced,
}

/// One session or refresh.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherSessionSnapshot {
    /// Its id.
    pub session_id: String,
    /// Play or refresh.
    pub kind: LauncherActivityKind,
    /// Its server.
    pub server_name: String,
    /// Its account.
    pub account_name: String,
    /// The character, when one was named or entered the world.
    pub character_name: Option<String>,
    /// The launch mode (play sessions).
    pub launch_mode: Option<LaunchMode>,
    /// Where it is.
    pub state: LauncherActivityState,
    /// A line for the player.
    pub status: String,
    /// The exit code, once known.
    pub exit_code: Option<i32>,
    /// The last error, when there was one.
    pub error: Option<String>,
    /// When it was created.
    pub created_at: SystemTime,
    /// The reason the client gave when it reported its own exit.
    pub exit_reason: Option<String>,
    /// The client reported its own exit with code 0.
    pub exited_gracefully: bool,
    /// The server's own words, when a refusal from it ended the session.
    pub server_message: Option<String>,
    /// That refusal only said the account, or one of its characters, was
    /// still logged on: trying again once the server lets go can succeed.
    pub account_in_use: bool,
    /// The player stopped it, and how that went.
    pub stop: Option<LauncherSessionStop>,
    /// In the world, its client has stopped reporting progress (it may
    /// be frozen); detection only.
    pub not_responding: bool,
    /// Plugins left out at launch (blocked ids), kept apart from the status
    /// so later updates do not hide it.
    pub plugin_notice: Option<String>,
    /// Whether its process has started and not actually exited; `None`
    /// when the source does not know (then the activity state decides).
    pub has_live_process: Option<bool>,
    /// Its start-up work is still running.
    pub startup_in_flight: bool,
    /// Its game window (play sessions).
    pub window: LauncherSessionWindow,
}

impl LauncherSessionSnapshot {
    /// Not ended, failed or cancelled.
    pub fn is_active(&self) -> bool {
        self.state.is_active()
    }

    /// The process or its start-up still runs, whatever the client
    /// reported: an early exit notice does not end it.
    pub fn is_process_or_start_active(&self) -> bool {
        self.startup_in_flight || self.has_live_process.unwrap_or_else(|| self.is_active())
    }
}

/// Everything the window shows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherStateSnapshot {
    /// The servers, in document order.
    pub servers: Vec<LauncherServerSnapshot>,
    /// The sessions, newest first.
    pub sessions: Vec<LauncherSessionSnapshot>,
    /// What this platform may launch.
    pub platform: LauncherPlatformCapabilities,
    /// Whether client content is configured.
    pub is_installation_ready: bool,
    /// A line about client content.
    pub installation_status: String,
    /// Whether beta plugins are offered.
    pub show_beta_plugins: bool,
    /// Every profile name, in the order the filter shows them.
    pub profiles: Vec<String>,
}
