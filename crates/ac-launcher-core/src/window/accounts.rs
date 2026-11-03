//! The Accounts tab: one card per account name (its profile tags and
//! plugins), one row per server the
//! account is on. A row remembers its check box, character and launch mode,
//! shows the server's health and ping, and the row's session.

use crate::orchestration::{
    LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState, LauncherServerSnapshot,
    LauncherSessionSnapshot, LauncherSessionWindow,
};
use crate::profiles::LaunchMode;

/// The character box's first entry: pick the character in the client.
pub const CHARACTER_SELECT: &str = "Character select";

/// A ping above this fills one bar.
pub const FAIR_LATENCY_MILLISECONDS: f64 = 200.0;
/// A ping at or below this fills three bars.
pub const GOOD_LATENCY_MILLISECONDS: f64 = 80.0;
/// An unfilled ping bar.
pub const EMPTY_BAR_COLOR: &str = "#36444C";

/// The row's launch-mode box.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RowLaunchMode {
    /// With a window.
    Graphical,
    /// Without a window.
    Headless,
}

impl RowLaunchMode {
    /// Both, in the box's order.
    pub const ALL: [Self; 2] = [Self::Graphical, Self::Headless];

    /// The box's text.
    pub const fn label(self) -> &'static str {
        match self {
            Self::Graphical => "Graphical",
            Self::Headless => "Headless",
        }
    }
}

/// One chip of the profile filter above the accounts: "All", or one
/// profile tag.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileFilterChip {
    /// The chip's text.
    pub label: String,
    /// The tag this chip shows, or `None` for "All".
    pub profile: Option<String>,
    /// The chip in force.
    pub is_selected: bool,
}

/// One account name with a launch row for each configured server.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountGroup {
    /// The account name.
    pub account_name: String,
    /// The first server the account is on.
    pub server_name: String,
    /// Whether its rows are shown.
    pub is_expanded: bool,
    /// False while the profile filter hides this account.
    pub is_visible: bool,
    /// The account's profile tags.
    pub profiles: Vec<String>,
    /// The account's plugins by name, such as "Combat · Stats", or "none".
    pub plugins_summary: String,
    /// "sawato · 7 characters".
    pub subtitle: String,
    /// One row per server.
    pub servers: Vec<AccountServerRow>,
}

impl AccountGroup {
    /// An expanded, empty group.
    pub fn new(account_name: impl Into<String>) -> Self {
        Self {
            account_name: account_name.into(),
            server_name: String::new(),
            is_expanded: true,
            is_visible: true,
            profiles: Vec::new(),
            plugins_summary: "none".into(),
            subtitle: String::new(),
            servers: Vec::new(),
        }
    }

    /// The account is on one server only.
    pub fn has_one_server(&self) -> bool {
        self.servers.len() == 1
    }

    /// The account carries profile tags.
    pub fn has_profiles(&self) -> bool {
        !self.profiles.is_empty()
    }

    /// Whether the account carries `profile`, ignoring case.
    pub fn has_profile(&self, profile: &str) -> bool {
        self.profiles
            .iter()
            .any(|known| known.eq_ignore_ascii_case(profile))
    }

    /// Fills the card from the account on each of its servers (in row
    /// order): its tags, and on one server its plugins and character count.
    pub fn update(
        &mut self,
        accounts: &[&LauncherAccountSnapshot],
        plugin_display_name: impl Fn(&str) -> String,
    ) {
        if let Some(first) = self.servers.first() {
            self.server_name.clone_from(&first.server_name);
        }
        let mut profiles: Vec<String> = Vec::new();
        for tag in accounts.iter().flat_map(|account| account.profiles.iter()) {
            if !profiles.iter().any(|known| known.eq_ignore_ascii_case(tag)) {
                profiles.push(tag.clone());
            }
        }
        self.profiles = profiles;
        if let [account] = accounts {
            self.plugins_summary = if account.plugins.is_empty() {
                "none".into()
            } else {
                account
                    .plugins
                    .iter()
                    .map(|id| plugin_display_name(id))
                    .collect::<Vec<_>>()
                    .join(" · ")
            };
            let count = account.characters.len();
            self.subtitle = format!(
                "{} · {count} {}",
                self.server_name,
                if count == 1 {
                    "character"
                } else {
                    "characters"
                }
            );
        } else {
            self.plugins_summary = "configured per server".into();
            self.subtitle = format!("{} servers", accounts.len());
        }
    }
}
/// One account on one server.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountServerRow {
    /// The account.
    pub account_name: String,
    /// The server.
    pub server_name: String,
    /// `host:port`.
    pub endpoint: String,
    /// Ticked for Play selected.
    pub is_checked: bool,
    /// `Character select`, then the account's characters.
    pub character_choices: Vec<String>,
    /// The saved choice in the character box.
    pub selected_character: String,
    /// The character the running session is playing, once reported.
    pub active_character_name: Option<String>,
    /// The launch-mode box.
    pub selected_launch_mode: RowLaunchMode,
    /// The session's status, or the account's.
    pub status: String,
    /// The last launch's error.
    pub launch_error: Option<String>,
    /// Whether the server answered the last check (`None`: not checked).
    pub is_server_online: Option<bool>,
    /// Players and staleness, or `Not checked`.
    pub server_status_text: String,
    /// The last reply time.
    pub latency_milliseconds: Option<f64>,
    /// Why Play is refused, empty when it may run (refreshed by the window).
    pub disabled_reason: String,
    /// Play waits only for the server to let go of the account after an
    /// unclean exit: the countdown the row shows in place of its status
    /// (refreshed by the window every frame).
    pub hold_text: Option<String>,
    /// False while the profile filter hides this row.
    pub is_visible: bool,
    /// The account's profile tags on this server.
    pub profiles: Vec<String>,
    active_session_id: Option<String>,
    /// The running play session's window, while it can be switched.
    active_window: Option<LauncherSessionWindow>,
    selection_loaded: bool,
}

impl AccountServerRow {
    /// A fresh row, not yet filled from a snapshot.
    pub fn new(account_name: impl Into<String>, server_name: impl Into<String>) -> Self {
        Self {
            account_name: account_name.into(),
            server_name: server_name.into(),
            endpoint: String::new(),
            is_checked: false,
            character_choices: vec![CHARACTER_SELECT.to_owned()],
            selected_character: CHARACTER_SELECT.to_owned(),
            active_character_name: None,
            selected_launch_mode: RowLaunchMode::Graphical,
            status: "Ready".into(),
            launch_error: None,
            is_server_online: None,
            server_status_text: "Not checked".into(),
            latency_milliseconds: None,
            disabled_reason: String::new(),
            hold_text: None,
            is_visible: true,
            profiles: Vec::new(),
            active_session_id: None,
            active_window: None,
            selection_loaded: false,
        }
    }

    /// What a launch from this row asks for: headless, the character
    /// screen, or a named character with a window.
    pub fn mode(&self) -> LaunchMode {
        if self.selected_launch_mode == RowLaunchMode::Headless {
            LaunchMode::Headless
        } else if self.selected_character == CHARACTER_SELECT {
            LaunchMode::GuiSelect
        } else {
            LaunchMode::Gui
        }
    }

    /// The chosen character, or `None` for the character screen.
    pub fn character_name(&self) -> Option<&str> {
        (self.selected_character != CHARACTER_SELECT).then_some(self.selected_character.as_str())
    }

    /// What the character box shows: whoever is in the world during a
    /// session, the saved choice otherwise.
    pub fn displayed_character(&self) -> &str {
        match self.active_character_name.as_deref() {
            Some(name)
                if self.is_active()
                    && !name.is_empty()
                    && self.character_choices.iter().any(|choice| choice == name) =>
            {
                name
            }
            _ => &self.selected_character,
        }
    }

    /// Whether the account carries `profile` on this server, ignoring case.
    pub fn has_profile(&self, profile: &str) -> bool {
        self.profiles
            .iter()
            .any(|known| known.eq_ignore_ascii_case(profile))
    }

    /// The Options button's name for a screen reader: which row it acts on.
    pub fn options_automation_name(&self) -> String {
        format!(
            "Options for {} on {}",
            self.character_name().unwrap_or(&self.account_name),
            self.server_name
        )
    }

    /// The running session's id.
    pub fn active_session_id(&self) -> Option<&str> {
        self.active_session_id.as_deref()
    }

    /// Whether a session runs for this row.
    pub fn is_active(&self) -> bool {
        self.active_session_id.is_some()
    }

    /// A session with no window is talked to through its console; one with
    /// a window has the game itself.
    pub fn can_open_console(&self) -> bool {
        self.active_window
            .as_ref()
            .is_some_and(|window| !window.shows_window())
    }

    /// What the launch-mode box shows: while a play session runs, whether
    /// it has a window (the asked-for state while a switch is pending);
    /// otherwise the saved choice.
    pub fn displayed_launch_mode(&self) -> RowLaunchMode {
        match &self.active_window {
            Some(window) if window.shows_window() => RowLaunchMode::Graphical,
            Some(_) => RowLaunchMode::Headless,
            None => self.selected_launch_mode,
        }
    }

    /// Whether the launch-mode box may switch the running session (an
    /// addition over OpenAC: no restart, no reconnect).
    pub fn can_switch_window(&self) -> bool {
        self.active_window
            .as_ref()
            .is_some_and(|window| window.switching_to.is_none())
    }

    /// Whether Play may run.
    pub fn can_play(&self) -> bool {
        self.disabled_reason.is_empty()
    }

    /// Whether the last launch failed.
    pub fn has_launch_error(&self) -> bool {
        self.launch_error
            .as_deref()
            .is_some_and(|error| !error.is_empty())
    }

    /// The dot beside the server's name.
    pub fn server_dot_color(&self) -> &'static str {
        match self.is_server_online {
            Some(true) => "#65D99B",
            Some(false) => "#F17474",
            None => "#89949C",
        }
    }

    /// `12 ms`, or empty.
    pub fn latency_text(&self) -> String {
        self.latency_milliseconds
            .map_or_else(String::new, |ms| format!("{ms:.0} ms"))
    }

    /// Whether a reply time is shown.
    pub fn has_latency(&self) -> bool {
        self.latency_milliseconds.is_some()
    }

    /// Filled bars in the ping meter: three for a fast reply, one for a slow
    /// one, none for a server that did not answer.
    pub fn ping_bars(&self) -> u8 {
        match (self.is_server_online, self.latency_milliseconds) {
            (Some(true), Some(ms)) if ms <= GOOD_LATENCY_MILLISECONDS => 3,
            (Some(true), Some(ms)) if ms <= FAIR_LATENCY_MILLISECONDS => 2,
            (Some(true), Some(_)) => 1,
            _ => 0,
        }
    }

    /// The colour of the filled bars.
    pub fn ping_color(&self) -> &'static str {
        match self.ping_bars() {
            3 => "#65D99B",
            2 => "#DBB573",
            1 => "#F17474",
            _ => EMPTY_BAR_COLOR,
        }
    }

    /// Bar `index` (1 to 3): filled or empty.
    pub fn ping_bar_color(&self, index: u8) -> &'static str {
        if self.ping_bars() >= index {
            self.ping_color()
        } else {
            EMPTY_BAR_COLOR
        }
    }

    /// The meter's tooltip.
    pub fn ping_tooltip(&self) -> String {
        let text = self.latency_text();
        match self.ping_bars() {
            3 => format!("{text} · fast"),
            2 => format!("{text} · fair"),
            1 => format!("{text} · slow"),
            _ => "No reply timed".into(),
        }
    }

    /// Fills the row from the newest snapshot. The saved selection is read
    /// once, when the row is first filled; a character that left the roster
    /// falls back to the character screen. Health from another endpoint is
    /// forgotten.
    pub fn update(
        &mut self,
        server: &LauncherServerSnapshot,
        account: &LauncherAccountSnapshot,
        session: Option<&LauncherSessionSnapshot>,
    ) {
        self.profiles.clone_from(&account.profiles);
        let endpoint = format!("{}:{}", server.host, server.port);
        if self.endpoint != endpoint {
            self.is_server_online = None;
            self.server_status_text = "Not checked".into();
            self.latency_milliseconds = None;
        }
        self.endpoint = endpoint;
        let mut choices = vec![CHARACTER_SELECT.to_owned()];
        choices.extend(
            account
                .characters
                .iter()
                .map(|character| character.name.clone()),
        );
        if self.character_choices != choices {
            if !choices.contains(&self.selected_character) {
                self.selected_character = CHARACTER_SELECT.to_owned();
            }
            self.character_choices = choices;
        }
        if !self.selection_loaded {
            self.selection_loaded = true;
            let saved = account
                .selected_character
                .as_deref()
                .unwrap_or(CHARACTER_SELECT);
            self.selected_character = if self.character_choices.iter().any(|c| c == saved) {
                saved.to_owned()
            } else {
                CHARACTER_SELECT.to_owned()
            };
            self.selected_launch_mode =
                if account.selected_launch_mode == Some(LaunchMode::Headless) {
                    RowLaunchMode::Headless
                } else {
                    RowLaunchMode::Graphical
                };
        }
        let active = session.filter(|session| session.is_process_or_start_active());
        self.active_session_id = active.map(|session| session.session_id.clone());
        // Only a play session whose process runs (not one starting or
        // stopping) has a window to switch.
        self.active_window = active
            .filter(|session| {
                session.kind == LauncherActivityKind::Play
                    && !session.startup_in_flight
                    && session.is_active()
                    && session.state != LauncherActivityState::Stopping
            })
            .map(|session| session.window.clone());
        self.active_character_name = active.and_then(|session| session.character_name.clone());
        // A client that stopped responding says so before an older error.
        self.status = active
            .filter(|session| session.not_responding)
            .map(|_| "Not responding".to_owned())
            .or_else(|| session.and_then(|session| session.error.clone()))
            .or_else(|| session.map(|session| session.status.clone()))
            .unwrap_or_else(|| account.activity_status.clone());
        self.launch_error = session.and_then(|session| session.error.clone());
    }
}
