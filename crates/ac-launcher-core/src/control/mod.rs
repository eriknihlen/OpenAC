//! The launcher's optional control interface for repeatable test runs
//! (`--control-pipe <name>`, off unless named): a same-user local endpoint
//! that reports the saved account selections and the sessions, starts the
//! character selected on a saved headless row, asks a headless session to
//! log off, and revalidates the installed client. It goes through the same
//! orchestrator as the window, never takes credentials or game commands,
//! never edits profiles and never kills a process.
//!
//! One UTF-8 JSON request per connection, prefixed by its byte count as a
//! four-byte little-endian integer (at most [`MAXIMUM_REQUEST_BYTES`]); the
//! reply is framed the same way. Requests are `{"command": "status" |
//! "start" | "stop" | "reload-client", "server", "account", "session"}`;
//! replies are `{"ok", "data", "error"}`.

mod server;
#[cfg(unix)]
mod unix_socket;
#[cfg(windows)]
mod windows_pipe;

use std::sync::Arc;

use ac_host::json::{self, Member, Node, Value};

use crate::cancel::CancelToken;
use crate::orchestration::{LauncherSessionSnapshot, LauncherStateSnapshot};
use crate::profiles::{LaunchMode, find_account};
use crate::updates::ClientVersionStore;
use crate::window::Orchestration;

pub use server::{
    ControlHandle, ControlServer, DEFAULT_REQUEST_TIMEOUT, MAXIMUM_REQUEST_BYTES, ReportError,
    is_valid_pipe_name,
};

/// The reply to a request the orchestrator refused (OpenAC's catch of a
/// launcher operation or update failure).
pub const REFUSED: &str = "Launcher refused the operation; inspect its session status.";

/// One request.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ControlRequest {
    /// `status`, `start`, `stop` or `reload-client`.
    pub command: Option<String>,
    /// A saved server's name (`start`).
    pub server: Option<String>,
    /// A saved account's name (`start`).
    pub account: Option<String>,
    /// A session id (`stop`).
    pub session: Option<String>,
}

/// Why a request's bytes are not a request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestError {
    /// The JSON is `null`.
    Empty,
    /// Not JSON, not an object, or a member of the wrong type.
    InvalidJson,
}

impl ControlRequest {
    /// A request of `command` alone.
    #[must_use]
    pub fn command(command: &str) -> Self {
        Self {
            command: Some(command.to_owned()),
            ..Self::default()
        }
    }

    /// Reads a request: member names in any case, unknown members ignored,
    /// each known member a string or `null`.
    ///
    /// # Errors
    /// [`RequestError`].
    pub fn parse(bytes: &[u8]) -> Result<Self, RequestError> {
        let node = json::parse(bytes).map_err(|_| RequestError::InvalidJson)?;
        let members = match &node.value {
            Value::Null => return Err(RequestError::Empty),
            Value::Object(members) => members,
            _ => return Err(RequestError::InvalidJson),
        };
        let mut request = Self::default();
        for member in members {
            let slot = match member.name.to_ascii_lowercase().as_str() {
                "command" => &mut request.command,
                "server" => &mut request.server,
                "account" => &mut request.account,
                "session" => &mut request.session,
                _ => continue,
            };
            *slot = match &member.value.value {
                Value::Null => None,
                Value::String(text) => Some(text.clone()),
                _ => return Err(RequestError::InvalidJson),
            };
        }
        Ok(request)
    }
}

/// One reply.
#[derive(Debug, Clone, PartialEq)]
pub struct ControlReply {
    /// Whether the request was carried out.
    pub ok: bool,
    /// What it returned.
    pub data: Option<Value>,
    /// Why it was not.
    pub error: Option<String>,
}

impl ControlReply {
    /// Carried out, returning `data`.
    #[must_use]
    pub fn done(data: Value) -> Self {
        Self {
            ok: true,
            data: Some(data),
            error: None,
        }
    }

    /// Carried out, returning nothing.
    #[must_use]
    pub fn empty() -> Self {
        Self {
            ok: true,
            data: None,
            error: None,
        }
    }

    /// Not carried out, saying why.
    #[must_use]
    pub fn refused(error: &str) -> Self {
        Self {
            ok: false,
            data: None,
            error: Some(error.to_owned()),
        }
    }

    /// The reply as JSON.
    #[must_use]
    pub fn to_json(&self) -> String {
        let node = Node::made(object(vec![
            ("ok", Value::Bool(self.ok)),
            ("data", self.data.clone().unwrap_or(Value::Null)),
            (
                "error",
                self.error.clone().map_or(Value::Null, Value::String),
            ),
        ]));
        let mut out = String::new();
        json::write_value(&mut out, &node, 0);
        out
    }
}

/// A request that ran out of time before the handler started on it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Cancelled;

/// Revalidates the installed client for future launches.
pub type ReloadClient = Arc<dyn Fn(&CancelToken) -> ControlReply + Send + Sync>;

/// The full active client version, when one is known.
pub type ClientVersion = Arc<dyn Fn() -> Option<String> + Send + Sync>;

/// Carries out requests through the launcher's own orchestrator, the one
/// the window uses; callers cannot supply credentials or commands.
pub struct LauncherControlHandler {
    launcher: Arc<dyn Orchestration>,
    reload_client: Option<ReloadClient>,
    client_version: Option<ClientVersion>,
}

impl std::fmt::Debug for LauncherControlHandler {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherControlHandler")
            .field("reload_client", &self.reload_client.is_some())
            .finish_non_exhaustive()
    }
}

impl LauncherControlHandler {
    /// A handler over `launcher`; `reload-client` is unavailable without
    /// `reload_client`.
    #[must_use]
    pub fn new(
        launcher: Arc<dyn Orchestration>,
        reload_client: Option<ReloadClient>,
        client_version: Option<ClientVersion>,
    ) -> Self {
        Self {
            launcher,
            reload_client,
            client_version,
        }
    }

    /// Carries out one request.
    ///
    /// # Errors
    /// [`Cancelled`] when `cancel` was set before the request was looked at:
    /// nothing was done.
    pub fn handle(
        &self,
        request: &ControlRequest,
        cancel: &CancelToken,
    ) -> Result<ControlReply, Cancelled> {
        if cancel.is_cancelled() {
            return Err(Cancelled);
        }
        if self.launcher.poll_status().is_err() {
            return Ok(ControlReply::refused(REFUSED));
        }
        let Ok(state) = self.launcher.snapshot() else {
            return Ok(ControlReply::refused(REFUSED));
        };
        if cancel.is_cancelled() {
            return Err(Cancelled);
        }
        Ok(match request.command.as_deref() {
            Some("status") => ControlReply::done(self.status(&state)),
            Some("reload-client") => {
                if state
                    .sessions
                    .iter()
                    .any(LauncherSessionSnapshot::is_active)
                {
                    ControlReply::refused("Stop every session before reloading the client version.")
                } else {
                    match &self.reload_client {
                        Some(reload) => reload(cancel),
                        None => ControlReply::refused("Client reload is unavailable."),
                    }
                }
            }
            Some("start") => self.start(&state, request, cancel),
            Some("stop") => self.stop(&state, request),
            _ => {
                ControlReply::refused("Unknown command. Use status, start, stop or reload-client.")
            }
        })
    }

    fn status(&self, state: &LauncherStateSnapshot) -> Value {
        let accounts = state
            .servers
            .iter()
            .flat_map(|server| {
                server.accounts.iter().map(|account| {
                    object(vec![
                        ("server", Value::String(server.name.clone())),
                        ("account", Value::String(account.account_name.clone())),
                        ("character", optional(account.selected_character.as_deref())),
                        (
                            "mode",
                            optional(account.selected_launch_mode.map(mode_name)),
                        ),
                        ("active", Value::Bool(account.has_running_activity)),
                    ])
                })
            })
            .map(Node::made)
            .collect();
        let sessions = state
            .sessions
            .iter()
            .map(|session| Node::made(project_session(session)))
            .collect();
        let version = self.client_version.as_ref().and_then(|version| version());
        object(vec![
            ("ready", Value::Bool(state.is_installation_ready)),
            ("clientVersion", optional(version.as_deref())),
            ("accounts", Value::Array(accounts)),
            ("sessions", Value::Array(sessions)),
        ])
    }

    fn start(
        &self,
        state: &LauncherStateSnapshot,
        request: &ControlRequest,
        cancel: &CancelToken,
    ) -> ControlReply {
        // The server by its exact name; the account in any case, as the
        // server matches it.
        let selected = state
            .servers
            .iter()
            .filter(|server| Some(server.name.as_str()) == request.server.as_deref())
            .find_map(|server| {
                let name = request.account.as_deref()?;
                find_account(&server.accounts, |account| &account.account_name, name)
                    .map(|index| &server.accounts[index])
            });
        let Some(selected) = selected else {
            return ControlReply::refused("Saved server/account not found.");
        };
        let character = selected
            .selected_character
            .as_deref()
            .filter(|name| !name.trim().is_empty());
        let Some(character) =
            character.filter(|_| selected.selected_launch_mode == Some(LaunchMode::Headless))
        else {
            return ControlReply::refused(
                "Select a saved character and Headless mode in the launcher first.",
            );
        };
        if selected.has_running_activity {
            return ControlReply::refused("Account already has an active session.");
        }
        // The server may still hold the account after an unclean exit: say
        // so, with the seconds left, rather than start a login it refuses.
        match self.launcher.account_launch_capability(
            &selected.server_name,
            &selected.account_name,
            LaunchMode::Headless,
        ) {
            Ok(capability) if capability.is_available => {}
            Ok(capability) => {
                return ControlReply::refused(capability.reason.as_deref().unwrap_or(REFUSED));
            }
            Err(_) => return ControlReply::refused(REFUSED),
        }
        match self.launcher.launch(
            &selected.server_name,
            &selected.account_name,
            Some(character),
            LaunchMode::Headless,
            cancel,
        ) {
            Ok(started) => ControlReply::done(project_session(&started)),
            Err(_) => ControlReply::refused(REFUSED),
        }
    }

    fn stop(&self, state: &LauncherStateSnapshot, request: &ControlRequest) -> ControlReply {
        let mut matching = state
            .sessions
            .iter()
            .filter(|session| Some(session.session_id.as_str()) == request.session.as_deref());
        let (Some(session), None) = (matching.next(), matching.next()) else {
            return ControlReply::refused("Session not found.");
        };
        if !session.is_active() {
            return ControlReply::done(project_session(session));
        }
        if session.launch_mode != Some(LaunchMode::Headless) {
            return ControlReply::refused(
                "Only headless sessions can be stopped through this interface.",
            );
        }
        // The log off goes through the session's console; the caller polls
        // the status. A slow log off never becomes a forced kill.
        if self
            .launcher
            .try_send_console_line(&session.session_id, "/quit")
        {
            ControlReply::done(object(vec![
                ("session", Value::String(session.session_id.clone())),
                ("logoutRequested", Value::Bool(true)),
            ]))
        } else {
            ControlReply::refused("Session cannot accept a logout request yet.")
        }
    }
}

/// `reload-client`: revalidates the active client in `versions` for future
/// launches, holding the store's exclusive update lease as an install does.
/// It neither installs nor moves the version pointer.
#[must_use]
pub fn reload_client(versions: Arc<ClientVersionStore>, rid: String) -> ReloadClient {
    Arc::new(move |cancel: &CancelToken| {
        let Ok(_lease) = versions.barrier().acquire_exclusive() else {
            return ControlReply::refused(REFUSED);
        };
        match versions.load_current_read_only(&rid, cancel) {
            Ok(resolution) if resolution.is_verified() => ControlReply::done(object(vec![(
                "version",
                optional(
                    resolution
                        .record
                        .as_ref()
                        .map(|record| record.version.as_str()),
                ),
            )])),
            Ok(_) => ControlReply::refused("The active client failed installed-file verification."),
            Err(_) => ControlReply::refused(REFUSED),
        }
    })
}

/// The status's `clientVersion`: the full active version (local build
/// metadata included) the store last resolved.
#[must_use]
pub fn client_version(versions: Arc<ClientVersionStore>) -> ClientVersion {
    Arc::new(move || {
        versions
            .cached_resolution()
            .record
            .map(|record| record.version)
    })
}

fn object(members: Vec<(&str, Value)>) -> Value {
    Value::Object(
        members
            .into_iter()
            .map(|(name, value)| Member::made(name, value))
            .collect(),
    )
}

fn optional(text: Option<&str>) -> Value {
    text.map_or(Value::Null, |text| Value::String(text.to_owned()))
}

fn number(value: i64) -> Value {
    Value::Number(value.to_string())
}

/// A launch mode as OpenAC names it (`Gui`, `GuiSelect`, `Headless`).
fn mode_name(mode: LaunchMode) -> &'static str {
    match mode {
        LaunchMode::Gui => "Gui",
        LaunchMode::GuiSelect => "GuiSelect",
        LaunchMode::Headless => "Headless",
    }
}

fn project_session(session: &LauncherSessionSnapshot) -> Value {
    object(vec![
        ("session", Value::String(session.session_id.clone())),
        ("server", Value::String(session.server_name.clone())),
        ("account", Value::String(session.account_name.clone())),
        ("character", optional(session.character_name.as_deref())),
        ("mode", optional(session.launch_mode.map(mode_name))),
        ("state", Value::String(format!("{:?}", session.state))),
        ("active", Value::Bool(session.is_active())),
        (
            "exitCode",
            session
                .exit_code
                .map_or(Value::Null, |code| number(i64::from(code))),
        ),
        ("graceful", Value::Bool(session.exited_gracefully)),
    ])
}
