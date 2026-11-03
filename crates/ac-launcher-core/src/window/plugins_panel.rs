//! The Plugins tab's state: Installed and Discover, their searches, the
//! remove dialog, Add from URL (and its pick of one plugin from a repository
//! publishing several) and Show beta plugins. The slow steps (a
//! Check pass, Discover's per-plugin release check, installs) are run by
//! the window; this holds what they produced.

use std::collections::HashMap;
use std::time::{Duration, SystemTime};

use super::icon::PluginIcon;
use super::install_dialog::PluginInstallDialog;
use super::plugins::{PluginCheckOutcome, PluginDiscoverEntry, PluginUpdateAvailability};
use crate::plugins::{
    COMPATIBLE_PREFIX, InstalledPluginInfo, InstalledPluginSource, PermissionDeclaration,
    PluginInstallSource, PluginReleaseChannel, PluginReleaseResolution, permissions_match,
    without_client_version,
};
use crate::updates::LauncherVersion;

/// Wide panels put Discover beside Installed; narrow ones stack them,
/// Installed first.
pub const SIDE_BY_SIDE_WIDTH: f32 = 820.0;

/// The picker's Stable entry.
pub const STABLE_CHANNEL_LABEL: &str = "Stable";
/// The picker's Beta entry.
pub const BETA_CHANNEL_LABEL: &str = "Beta";

/// The letters on a plugin tile without an icon: the first letters of the
/// first two words, or of a single word's first two capitalised parts
/// (`BuffBot` is `BB`).
pub fn monogram(name: &str) -> String {
    let words: Vec<&str> = name.split_whitespace().collect();
    let Some(first) = words.first() else {
        return "?".into();
    };
    let initial = |word: &str| {
        word.chars()
            .next()
            .map(|c| c.to_uppercase().collect::<String>())
            .unwrap_or_default()
    };
    if words.len() > 1 {
        return format!("{}{}", initial(first), initial(words[1]));
    }
    if let Some(capital) = first.chars().skip(1).find(|c| c.is_uppercase()) {
        return format!("{}{capital}", initial(first));
    }
    initial(first)
}

/// The source badge.
pub fn describe_source(
    source: InstalledPluginSource,
    listed_source: Option<PluginInstallSource>,
) -> &'static str {
    match source {
        InstalledPluginSource::Managed => {
            if listed_source == Some(PluginInstallSource::Unlisted) {
                "Unlisted"
            } else {
                "Listed"
            }
        }
        InstalledPluginSource::Direct => "Direct install",
        InstalledPluginSource::Bundled => "Bundled",
    }
}

/// `3 day(s)`, `2 hour(s)`, `5 minute(s)` (at least one minute).
pub fn format_age(age: Duration) -> String {
    let seconds = age.as_secs_f64();
    let days = seconds / 86_400.0;
    let hours = seconds / 3_600.0;
    if days >= 1.0 {
        format!("{days:.0} day(s)")
    } else if hours >= 1.0 {
        format!("{hours:.0} hour(s)")
    } else {
        format!("{:.0} minute(s)", (seconds / 60.0).max(1.0))
    }
}

/// `1 permission`, `3 permissions`, or empty.
pub fn permission_count_text(count: usize) -> String {
    match count {
        0 => String::new(),
        1 => "1 permission".into(),
        count => format!("{count} permissions"),
    }
}

/// One channel's resolved release for a Discover row, kept with the row so
/// switching the picker never fetches again.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoverChannelOption {
    /// Its tag.
    pub tag: String,
    /// Its version.
    pub version: String,
    /// Its compatibility line.
    pub compatibility: String,
    /// Whether that line is a warning.
    pub compatibility_is_warning: bool,
    /// What it declares.
    pub permissions: Vec<PermissionDeclaration>,
}

/// What one release check found for a Discover row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoverDetails {
    /// The stable channel's release.
    pub stable: Option<DiscoverChannelOption>,
    /// The beta channel's release (only read with Show beta plugins on).
    pub beta: Option<DiscoverChannelOption>,
    /// Its icon.
    pub icon: Option<PluginIcon>,
}

/// One listed plugin not yet installed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginDiscoverRow {
    /// What the list says.
    pub entry: PluginDiscoverEntry,
    /// `ab` monogram.
    pub initials: String,
    /// The icon, when one was fetched.
    pub icon: Option<PluginIcon>,
    /// The picker is shown (Show beta plugins on).
    pub show_channel_picker: bool,
    /// The picked channel.
    pub selected_channel: PluginReleaseChannel,
    /// A usable release was found; only then is the row shown.
    pub is_visible: bool,
    stable_option: Option<DiscoverChannelOption>,
    beta_option: Option<DiscoverChannelOption>,
}

impl PluginDiscoverRow {
    /// A row for `entry`, hidden until its release is checked.
    pub fn new(entry: PluginDiscoverEntry) -> Self {
        Self {
            initials: monogram(&entry.name),
            entry,
            icon: None,
            show_channel_picker: false,
            selected_channel: PluginReleaseChannel::Stable,
            is_visible: false,
            stable_option: None,
            beta_option: None,
        }
    }

    /// `by author · owner/name`.
    pub fn author_and_repo(&self) -> String {
        format!("by {} · {}", self.entry.author, self.entry.repo)
    }

    /// What Install pins to: the selected channel's release, never the
    /// other channel's.
    pub fn selected_option(&self) -> Option<&DiscoverChannelOption> {
        match self.selected_channel {
            PluginReleaseChannel::Beta => self.beta_option.as_ref(),
            PluginReleaseChannel::Stable => self.stable_option.as_ref(),
        }
    }

    /// The picker's text.
    pub fn selected_channel_label(&self) -> &'static str {
        match self.selected_channel {
            PluginReleaseChannel::Beta => BETA_CHANNEL_LABEL,
            PluginReleaseChannel::Stable => STABLE_CHANNEL_LABEL,
        }
    }

    /// Picks a channel.
    pub fn set_selected_channel(&mut self, channel: PluginReleaseChannel) {
        self.selected_channel = channel;
    }

    /// Why Install is refused for the picked channel.
    pub fn channel_unavailable_text(&self) -> Option<&'static str> {
        match (self.selected_option(), self.selected_channel) {
            (Some(_), _) => None,
            (None, PluginReleaseChannel::Beta) => Some("No beta release yet."),
            (None, PluginReleaseChannel::Stable) => Some("No stable release yet."),
        }
    }

    /// Whether Install may run for the picked channel.
    pub fn can_install_selected_channel(&self) -> bool {
        self.selected_option().is_some()
    }

    /// Both channels' releases from one check; the picked channel stays.
    pub fn set_channel_options(
        &mut self,
        stable: Option<DiscoverChannelOption>,
        beta: Option<DiscoverChannelOption>,
    ) {
        self.stable_option = stable;
        self.beta_option = beta;
    }

    /// Forgets both releases and goes back to Stable.
    pub fn clear_channel_options(&mut self) {
        self.stable_option = None;
        self.beta_option = None;
        self.selected_channel = PluginReleaseChannel::Stable;
    }

    /// The picked release's version.
    pub fn latest_version(&self) -> Option<&str> {
        self.selected_option().map(|option| option.version.as_str())
    }

    /// The picked release's compatibility line.
    pub fn compatibility(&self) -> Option<&str> {
        self.selected_option()
            .map(|option| option.compatibility.as_str())
            .filter(|text| !text.trim().is_empty())
    }

    /// That line without the client's version.
    pub fn compatibility_text(&self) -> String {
        without_client_version(self.compatibility().unwrap_or_default())
    }

    /// The line is a warning.
    pub fn show_compatibility_warning(&self) -> bool {
        self.compatibility().is_some()
            && self
                .selected_option()
                .is_some_and(|option| option.compatibility_is_warning)
    }

    /// The line is information worth showing (not just "compatible").
    pub fn show_compatibility_info(&self) -> bool {
        self.compatibility().is_some_and(|text| {
            !self.show_compatibility_warning() && !text.starts_with(COMPATIBLE_PREFIX)
        })
    }

    /// The picked release's permissions (a count only: browsing is not a
    /// consent surface).
    pub fn permissions(&self) -> &[PermissionDeclaration] {
        self.selected_option()
            .map_or(&[][..], |option| &option.permissions)
    }
}

/// One installed plugin.
#[derive(Debug, Clone, PartialEq)]
pub struct PluginInstalledRow {
    /// What the inventory says.
    pub info: InstalledPluginInfo,
    /// The source badge.
    pub source_badge: &'static str,
    /// `ab` monogram.
    pub initials: String,
    /// The icon on disk.
    pub icon: Option<PluginIcon>,
    /// Remove is offered (managed and direct installs).
    pub can_remove: bool,
    /// The beta toggle is shown (managed plugins).
    pub show_beta_toggle: bool,
    /// The plugin follows the beta channel.
    pub is_beta_channel: bool,
    /// The installed release is a pre-release.
    pub is_prerelease: bool,
    /// A newer release is offered.
    pub update: Option<PluginUpdateAvailability>,
    /// The update declares different permissions.
    pub update_permissions_changed: bool,
    /// Why a newer release is withheld.
    pub update_withheld_reason: Option<String>,
    /// What the installed version declares.
    pub permissions: Vec<PermissionDeclaration>,
    /// Show beta plugins is on (the channel label shows only then).
    pub show_beta_plugins: bool,
}

impl PluginInstalledRow {
    /// `id · vversion · badge`.
    pub fn summary(&self) -> String {
        format!(
            "{} · v{} · {}",
            self.info.id, self.info.version, self.source_badge
        )
    }

    /// The compatibility line without the client's version.
    pub fn compatibility_text(&self) -> String {
        without_client_version(&self.info.compatibility)
    }

    fn has_compatibility_note(&self) -> bool {
        !self.info.compatibility.trim().is_empty()
    }

    /// The line is a warning.
    pub fn show_compatibility_warning(&self) -> bool {
        self.has_compatibility_note() && self.info.compatibility_is_warning
    }

    fn show_compatibility_info(&self) -> bool {
        self.has_compatibility_note()
            && !self.info.compatibility_is_warning
            && !self.info.compatibility.starts_with(COMPATIBLE_PREFIX)
    }

    /// Refused by the direct-install rules.
    pub fn is_refused(&self) -> bool {
        self.info
            .refusal
            .as_deref()
            .is_some_and(|refusal| !refusal.trim().is_empty())
    }

    /// Blocked by the curated list.
    pub fn is_blocked(&self) -> bool {
        self.info
            .blocked
            .as_deref()
            .is_some_and(|blocked| !blocked.trim().is_empty())
    }

    /// `Blocked: reason`.
    pub fn blocked_text(&self) -> Option<String> {
        self.info
            .blocked
            .as_ref()
            .filter(|_| self.is_blocked())
            .map(|reason| format!("Blocked: {reason}"))
    }

    /// A version warning on a plugin that is not refused: `Skipped`.
    pub fn show_skipped(&self) -> bool {
        self.show_compatibility_warning() && !self.is_refused()
    }

    /// An information line on a plugin that is not refused.
    pub fn show_compatibility_info_line(&self) -> bool {
        self.show_compatibility_info() && !self.is_refused()
    }

    /// An update chip is shown.
    pub fn has_update_chip(&self) -> bool {
        self.update.is_some()
    }

    /// `Update available: vX[ · new permissions]`.
    pub fn update_chip_text(&self) -> Option<String> {
        self.update.as_ref().map(|update| {
            if self.update_permissions_changed {
                format!("Update available: v{} · new permissions", update.version)
            } else {
                format!("Update available: v{}", update.version)
            }
        })
    }

    /// The update's compatibility line, when it reads differently from the
    /// row's own.
    pub fn update_compatibility_note(&self) -> Option<&str> {
        self.update
            .as_ref()
            .and_then(|update| update.compatibility_note.as_deref())
            .filter(|note| !note.trim().is_empty() && *note != self.info.compatibility)
    }

    /// `Update withheld: reason` is shown.
    pub fn has_update_withheld_reason(&self) -> bool {
        self.update.is_none()
            && self
                .update_withheld_reason
                .as_deref()
                .is_some_and(|reason| !reason.trim().is_empty())
    }

    /// Any note line under the title.
    pub fn has_notes(&self) -> bool {
        self.update_compatibility_note().is_some()
            || self.info.conflict
            || self.has_update_withheld_reason()
    }

    /// The corner's `Beta Channel` label.
    pub fn show_beta_channel_label(&self) -> bool {
        self.is_beta_channel && self.show_beta_plugins
    }
}

/// One plugin Add from URL can offer when a repository publishes several.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AddFromUrlChoice {
    /// `owner/name`.
    pub repo: String,
    /// Its name.
    pub display_name: String,
    /// The release version offered.
    pub version: String,
    /// The release.
    pub resolution: Box<PluginReleaseResolution>,
}

/// The Plugins tab.
#[derive(Debug, Default)]
pub struct PluginsPanel {
    /// Every installed row.
    pub installed: Vec<PluginInstalledRow>,
    /// Every listed, not-installed row (shown once visible).
    pub discover: Vec<PluginDiscoverRow>,
    /// Installed search.
    pub installed_filter: String,
    /// Discover search.
    pub discover_filter: String,
    /// A check, install, toggle or Add from URL runs.
    pub is_busy: bool,
    /// The last failure.
    pub error: Option<String>,
    /// An informational result (`... is already installed.`).
    pub status_text: Option<String>,
    /// GitHub is rate limiting.
    pub is_rate_limited: bool,
    /// Show beta plugins (a launcher setting).
    pub show_beta_plugins: bool,
    /// Discover rows still being checked.
    pub discover_pending_count: usize,
    /// A Discover check is running with nothing shown yet.
    pub is_discover_checking: bool,
    /// Why some rows could not be checked.
    pub discover_status_line: Option<String>,
    /// Discover has been looked at this session.
    pub discover_details_requested: bool,
    /// Releases found this session, by plugin id (lower case).
    pub discover_details_cache: HashMap<String, DiscoverDetails>,
    /// Installed icons decoded this session, by (id lower case, version).
    pub installed_icon_cache: HashMap<(String, String), Option<PluginIcon>>,
    /// The Add from URL field.
    pub add_from_url_text: String,
    /// The plugins Add from URL found in a repository publishing several
    /// that are not installed, by name, for the player to pick one; empty
    /// otherwise.
    pub add_from_url_choices: Vec<AddFromUrlChoice>,
    /// The Install from ZIP field: a plugin ZIP's path.
    pub local_zip_text: String,
    /// When the cached list in use was written.
    pub list_age: Option<SystemTime>,
    /// The remove dialog is open.
    pub is_remove_dialog_open: bool,
    /// The plugin the remove dialog is about.
    pub remove_target: Option<InstalledPluginInfo>,
    /// Also delete the plugin's saved files.
    pub remove_delete_storage: bool,
    /// The install and update dialog.
    pub install_dialog: PluginInstallDialog,
    /// The panel is wide enough for two columns.
    pub is_side_by_side: bool,
}

fn matches(filter: &str, fields: &[&str]) -> bool {
    let filter = filter.trim().to_lowercase();
    filter.is_empty()
        || fields
            .iter()
            .any(|field| field.to_lowercase().contains(&filter))
}

impl PluginsPanel {
    /// The Installed rows the search shows, as indexes into `installed`.
    pub fn installed_view(&self) -> Vec<usize> {
        self.installed
            .iter()
            .enumerate()
            .filter(|(_, row)| {
                matches(
                    &self.installed_filter,
                    &[&row.info.display_name, &row.info.id, row.source_badge],
                )
            })
            .map(|(index, _)| index)
            .collect()
    }

    /// The Discover rows shown: resolved and matching the search.
    pub fn discover_view(&self) -> Vec<usize> {
        self.discover
            .iter()
            .enumerate()
            .filter(|(_, row)| {
                row.is_visible
                    && matches(
                        &self.discover_filter,
                        &[
                            &row.entry.name,
                            &row.entry.id,
                            &row.entry.author,
                            &row.entry.description,
                            &row.entry.repo,
                        ],
                    )
            })
            .map(|(index, _)| index)
            .collect()
    }

    /// Installed's empty text.
    pub fn installed_empty_text(&self) -> &'static str {
        if self.installed_filter.is_empty() {
            "No plugins are installed yet."
        } else {
            "No installed plugin matches that search."
        }
    }

    /// Discover's empty text for a search that matched nothing.
    pub fn discover_empty_text(&self) -> &'static str {
        if self.discover_filter.is_empty() {
            "No listed plugins are available to install."
        } else {
            "No listed plugin matches that search."
        }
    }

    /// The empty state shows only once checking is done.
    pub fn show_discover_empty_text(&self) -> bool {
        self.discover_view().is_empty() && !self.is_discover_checking
    }

    /// `Checking N plugins…`.
    pub fn discover_checking_text(&self) -> String {
        if self.discover_pending_count == 1 {
            "Checking 1 plugin…".into()
        } else {
            format!("Checking {} plugins…", self.discover_pending_count)
        }
    }

    /// Recomputes whether Discover is still checking with nothing shown.
    pub fn update_discover_checking_state(&mut self) {
        self.is_discover_checking =
            self.discover_pending_count > 0 && self.discover_view().is_empty();
    }

    /// `Showing the plugin list from ... ago.` while a cached list is used.
    pub fn list_age_text(&self, now: SystemTime) -> Option<String> {
        self.list_age.map(|age| {
            format!(
                "Showing the plugin list from {} ago.",
                format_age(now.duration_since(age).unwrap_or_default())
            )
        })
    }

    /// The channel Discover and Add from URL resolve with.
    pub fn discover_channel(&self) -> PluginReleaseChannel {
        if self.show_beta_plugins {
            PluginReleaseChannel::Beta
        } else {
            PluginReleaseChannel::Stable
        }
    }

    /// Sets the panel's width.
    pub fn set_panel_width(&mut self, width: f32) {
        self.is_side_by_side = width >= SIDE_BY_SIDE_WIDTH;
    }

    /// Builds one Installed row. `record_is_beta` is the managed record's
    /// channel; `installed_permissions` what its `plugin.json` declares.
    pub fn build_installed_row(
        &self,
        info: InstalledPluginInfo,
        update: Option<PluginUpdateAvailability>,
        withheld_reason: Option<String>,
        record_is_beta: bool,
        installed_permissions: Vec<PermissionDeclaration>,
        icon: Option<PluginIcon>,
    ) -> PluginInstalledRow {
        let managed = info.source == InstalledPluginSource::Managed;
        let update = update.filter(|_| managed);
        let update_permissions_changed = update
            .as_ref()
            .is_some_and(|update| !permissions_match(&installed_permissions, &update.permissions));
        PluginInstalledRow {
            source_badge: describe_source(info.source, info.listed_source),
            initials: monogram(&info.display_name),
            icon,
            can_remove: matches!(
                info.source,
                InstalledPluginSource::Managed | InstalledPluginSource::Direct
            ),
            show_beta_toggle: managed,
            is_beta_channel: managed && record_is_beta,
            is_prerelease: LauncherVersion::try_parse(&info.version)
                .is_some_and(|version| version.is_pre_release()),
            update,
            update_permissions_changed,
            update_withheld_reason: withheld_reason,
            permissions: installed_permissions,
            show_beta_plugins: self.show_beta_plugins,
            info,
        }
    }

    /// Applies a Check pass's Discover half: rows for the listed plugins,
    /// shown at once when already checked this session.
    pub fn apply_discover(&mut self, outcome: &PluginCheckOutcome) {
        self.discover = outcome
            .discover
            .iter()
            .cloned()
            .map(|entry| {
                let mut row = PluginDiscoverRow::new(entry);
                row.show_channel_picker = self.show_beta_plugins;
                if let Some(cached) = self
                    .discover_details_cache
                    .get(&row.entry.id.to_lowercase())
                {
                    row.icon = cached.icon.clone();
                    row.set_channel_options(cached.stable.clone(), cached.beta.clone());
                    row.is_visible = true;
                }
                row
            })
            .collect();
        // A fresh list replaces whatever the last refresh pass was doing.
        self.discover_pending_count = 0;
        self.discover_status_line = None;
        self.update_discover_checking_state();
    }
}
