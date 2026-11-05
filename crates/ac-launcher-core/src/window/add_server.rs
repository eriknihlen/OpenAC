//! Add a server: one of your own by name, host and port, or one from the
//! list of known public servers with its player count. The known list is
//! saved each time it loads, so it also opens offline; with nothing saved,
//! only your own server can be added.

use std::time::SystemTime;

use ac_host_core::local_time::LocalZone;
use ac_host_core::status::civil_from_days;

use super::Orchestration;
use crate::orchestration::{LauncherServerSnapshot, OrchestratorError};
use crate::profiles::ProfileError;
use crate::status::{KnownServer, KnownServerList};
use crate::url::WebUrl;

/// The type filter's "every type" entry.
pub const ALL_TYPES: &str = "All types";

/// One known server in the dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KnownServerRow {
    /// The server.
    pub server: KnownServer,
    /// A profile server has its name, or its host and port.
    pub is_added: bool,
}

impl KnownServerRow {
    /// Its name.
    pub fn name(&self) -> &str {
        &self.server.name
    }

    /// `host:port`.
    pub fn endpoint(&self) -> String {
        format!("{}:{}", self.server.host, self.server.port)
    }

    /// Its type, or empty.
    pub fn server_type(&self) -> &str {
        self.server.server_type.as_deref().unwrap_or_default()
    }

    /// `1,234`, or `—` when unknown.
    pub fn players(&self) -> String {
        self.server
            .player_count
            .map_or_else(|| "—".to_owned(), group_thousands)
    }

    /// Its description, or empty.
    pub fn about(&self) -> &str {
        self.server.description.as_deref().unwrap_or_default()
    }

    /// Its website.
    pub fn website(&self) -> Option<&WebUrl> {
        self.server.website.as_ref()
    }

    /// Its Discord.
    pub fn discord(&self) -> Option<&WebUrl> {
        self.server.discord.as_ref()
    }
}

/// The dialog's state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AddServerDialog {
    /// Shown.
    pub is_open: bool,
    /// The known list is loading.
    pub is_loading: bool,
    /// Your own server's name.
    pub name: String,
    /// Your own server's host.
    pub host: String,
    /// Your own server's port, as typed.
    pub port: String,
    search: String,
    selected_type: String,
    /// `All types` and each type the list has, in the order they appear.
    pub types: Vec<String>,
    all: Vec<KnownServerRow>,
    /// `47 servers · list and player counts from TreeStats`, or why there
    /// is no list.
    pub list_status: String,
    /// Why the last add failed.
    pub error: Option<String>,
    /// What was just added, and what to do next.
    pub notice: Option<String>,
}

impl Default for AddServerDialog {
    fn default() -> Self {
        Self {
            is_open: false,
            is_loading: false,
            name: String::new(),
            host: String::new(),
            port: "9000".into(),
            search: String::new(),
            selected_type: ALL_TYPES.into(),
            types: vec![ALL_TYPES.into()],
            all: Vec::new(),
            list_status: String::new(),
            error: None,
            notice: None,
        }
    }
}

impl AddServerDialog {
    /// Opens the dialog with your own server's fields cleared; the caller
    /// loads the known servers.
    pub fn open(&mut self) {
        self.error = None;
        self.notice = None;
        self.name.clear();
        self.host.clear();
        self.port = "9000".into();
        self.is_open = true;
    }

    /// Closes it.
    pub fn close(&mut self) {
        self.is_open = false;
    }

    /// The search text.
    pub fn search(&self) -> &str {
        &self.search
    }

    /// Narrows the list by name, address or description.
    pub fn set_search(&mut self, value: &str) {
        value.clone_into(&mut self.search);
    }

    /// The type filter.
    pub fn selected_type(&self) -> &str {
        &self.selected_type
    }

    /// Narrows the list by type.
    pub fn set_selected_type(&mut self, value: &str) {
        value.clone_into(&mut self.selected_type);
    }

    /// There is a known list to pick from.
    pub fn has_known_servers(&self) -> bool {
        !self.all.is_empty()
    }

    /// There is an error to show.
    pub fn has_error(&self) -> bool {
        self.error.as_deref().is_some_and(|error| !error.is_empty())
    }

    /// The known servers the search and type filter show.
    pub fn servers(&self) -> Vec<&KnownServerRow> {
        let search = self.search.trim().to_uppercase();
        self.all
            .iter()
            .filter(|row| {
                let type_matches = self.selected_type == ALL_TYPES
                    || row.server_type().to_uppercase() == self.selected_type.to_uppercase();
                let search_matches = search.is_empty()
                    || [
                        row.name().to_owned(),
                        row.endpoint(),
                        row.about().to_owned(),
                    ]
                    .iter()
                    .any(|text| text.to_uppercase().contains(&search));
                type_matches && search_matches
            })
            .collect()
    }

    /// The list could not be read in a way the catalog did not expect;
    /// your own server can still be added.
    pub fn show_failure(&mut self, reason: &str) {
        self.is_loading = false;
        self.all.clear();
        self.list_status = format!(
            "The known server list could not be loaded ({reason}). You can still add your own \
             server."
        );
    }

    /// Shows a loaded list (`None`: offline with no saved copy), marking the
    /// servers already in `servers`.
    pub fn show_list(
        &mut self,
        list: Option<&KnownServerList>,
        servers: &[LauncherServerSnapshot],
        zone: &LocalZone,
    ) {
        self.is_loading = false;
        self.all = list.map_or_else(Vec::new, |list| {
            list.servers
                .iter()
                .map(|server| KnownServerRow {
                    server: server.clone(),
                    is_added: false,
                })
                .collect()
        });
        let mut types = vec![ALL_TYPES.to_owned()];
        for row in &self.all {
            let kind = row.server_type();
            if !kind.is_empty()
                && !types
                    .iter()
                    .any(|known| known.to_uppercase() == kind.to_uppercase())
            {
                types.push(kind.to_owned());
            }
        }
        if !types.contains(&self.selected_type) {
            self.selected_type = ALL_TYPES.into();
        }
        self.types = types;
        self.list_status = match list {
            None => "Offline: the known server list could not be loaded. You can still add your \
                     own server."
                .into(),
            Some(list) => {
                let mut status = format!(
                    "{} servers · list and player counts from TreeStats",
                    list.servers.len()
                );
                if list.is_from_cache {
                    status.push_str(" · offline copy from ");
                    status.push_str(&format_saved_date(list.saved_at, zone));
                }
                status
            }
        };
        self.refresh_added(servers);
    }

    /// A known server shows as added when a profile server has its name,
    /// or its host and port.
    pub fn refresh_added(&mut self, servers: &[LauncherServerSnapshot]) {
        for row in &mut self.all {
            row.is_added = servers.iter().any(|server| {
                server.name.to_uppercase() == row.server.name.to_uppercase()
                    || (server.host.to_uppercase() == row.server.host.to_uppercase()
                        && server.port == i32::from(row.server.port))
            });
        }
    }

    /// Adds the known server `name`; true when it was added.
    pub fn add_known(&mut self, name: &str, orchestrator: &dyn Orchestration) -> bool {
        let Some(row) = self
            .all
            .iter()
            .find(|row| row.name() == name && !row.is_added)
        else {
            return false;
        };
        let (name, host, port) = (
            row.server.name.clone(),
            row.server.host.clone(),
            row.server.port,
        );
        let added = self.try_add(&name, &host, i32::from(port), orchestrator);
        if added && let Ok(snapshot) = orchestrator.snapshot() {
            self.refresh_added(&snapshot.servers);
        }
        added
    }

    /// Adds your own server from the fields; true when it was added.
    pub fn add_own(&mut self, orchestrator: &dyn Orchestration) -> bool {
        let name = self.name.trim().to_owned();
        let host = self.host.trim().to_owned();
        if name.is_empty() || host.is_empty() {
            self.error = Some("Enter a name and a host for your server.".into());
            return false;
        }
        let port = self.port.trim();
        let Some(port) = port
            .bytes()
            .all(|byte| byte.is_ascii_digit())
            .then(|| port.parse::<i32>().ok())
            .flatten()
            .filter(|port| (1..=65535).contains(port))
        else {
            self.error = Some("The port is a number from 1 to 65535.".into());
            return false;
        };
        if !self.try_add(&name, &host, port, orchestrator) {
            return false;
        }
        self.name.clear();
        self.host.clear();
        self.port = "9000".into();
        if let Ok(snapshot) = orchestrator.snapshot() {
            self.refresh_added(&snapshot.servers);
        }
        true
    }

    fn try_add(
        &mut self,
        name: &str,
        host: &str,
        port: i32,
        orchestrator: &dyn Orchestration,
    ) -> bool {
        if let Err(error) = orchestrator.add_server(name, host, port) {
            self.notice = None;
            self.error = Some(match error {
                OrchestratorError::Profile(ProfileError::Io(_)) => {
                    "Unable to save. Check that the profile folder is writable.".into()
                }
                other => other.to_string(),
            });
            return false;
        }
        self.error = None;
        let accounts = orchestrator
            .snapshot()
            .ok()
            .and_then(|snapshot| {
                snapshot
                    .servers
                    .into_iter()
                    .find(|server| server.name == name)
            })
            .map_or(0, |server| server.accounts.len());
        self.notice = Some(if accounts == 0 {
            format!("Added {name}. Add your accounts on it with Accounts.")
        } else {
            format!(
                "Added {name} with {accounts} saved accounts. Character lists will refresh on \
                 that server."
            )
        });
        true
    }
}

/// `1,234` (the player count, as OpenAC's `N0` writes it).
fn group_thousands(value: i32) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::new();
    if value < 0 {
        out.push('-');
    }
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    out
}

/// `Sep 20, 2026` in the player's time zone, the same in every language
/// (OpenAC's `MMM d, yyyy`, invariant culture).
fn format_saved_date(saved_at: SystemTime, zone: &LocalZone) -> String {
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    let unix_ms = saved_at
        .duration_since(SystemTime::UNIX_EPOCH)
        .map_or(0, |elapsed| {
            i64::try_from(elapsed.as_millis()).unwrap_or(i64::MAX)
        });
    let seconds = unix_ms.div_euclid(1000) + i64::from(zone.utc_offset_seconds(unix_ms));
    let (year, month, day) = civil_from_days(seconds.div_euclid(86_400));
    format!("{} {day}, {year}", MONTHS[(month - 1) as usize])
}
