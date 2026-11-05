//! The install and update dialog a Discover row's Install, an installed
//! plugin's Update, or Add from URL opens: the responsibility notice, what
//! the plugin will be allowed to do (its permissions, which the client
//! enforces) with the author's reasons, and who to enable it for. A
//! repository is shown as text only; nothing here opens a browser or runs a
//! plugin.

use crate::plugins::{Permission, PermissionDeclaration, PluginReleaseChannel, permissions_match};
use crate::updates::LauncherVersion;

/// Who to enable a newly installed plugin for. An install never writes a
/// character's plugin list itself; only a choice other than `None` does,
/// and only after the install succeeded.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PluginEnableChoice {
    /// Nobody yet.
    None,
    /// Every character.
    All,
    /// The ticked characters.
    Choose,
}

/// A character offered in the Choose list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginCharacterOption {
    /// Its server.
    pub server_name: String,
    /// Its account.
    pub account_name: String,
    /// Its name.
    pub character_name: String,
    /// `Name (account@server)`.
    pub display_name: String,
}

/// One entry of the Choose list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginCharacterChoice {
    /// The character.
    pub option: PluginCharacterOption,
    /// Ticked.
    pub is_checked: bool,
}

/// One permission chip: something the client will let the plugin do (the
/// client links only the functions its permissions allow), with the
/// author's note on why. `is_new` marks one an update adds.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginPermissionChip {
    /// The chip's text.
    pub label: String,
    /// The author's note.
    pub note: String,
    /// Added by this update.
    pub is_new: bool,
    /// What a screen reader says.
    pub automation_name: String,
}

impl PluginPermissionChip {
    /// The chip for `declaration`; a network permission names the hosts it
    /// may reach without asking.
    pub fn new(declaration: &PermissionDeclaration, is_new: bool) -> Self {
        let mut label = describe_permission(declaration.permission).to_owned();
        if !declaration.hosts.is_empty() {
            label = format!("{label} ({})", declaration.hosts.join(", "));
        }
        if is_new {
            label.push_str(" (new)");
        }
        Self {
            automation_name: format!("{label}: {}", declaration.note),
            label,
            note: declaration.note.clone(),
            is_new,
        }
    }
}

/// A permission in a player's words, completing "This plugin may:".
pub fn describe_permission(permission: Permission) -> &'static str {
    match permission {
        Permission::Chat => "use chat",
        Permission::Network => "use the network",
        Permission::Listen => "accept network connections",
        Permission::Profiles => "use the shared VTank profiles",
        Permission::Clipboard => "set the clipboard",
    }
}

/// What opens the dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallDialogRequest {
    /// `owner/name`.
    pub repo: String,
    /// The plugin id.
    pub plugin_id: String,
    /// Its name.
    pub display_name: String,
    /// On the curated list.
    pub is_listed: bool,
    /// An installed plugin's update.
    pub is_update: bool,
    /// The release version offered.
    pub offered_version: Option<String>,
    /// The tag already resolved, when known; `None` resolves at confirm.
    pub pinned_tag: Option<String>,
    /// The channel the player picked (Discover's picker).
    pub channel: Option<PluginReleaseChannel>,
    /// Every character, for the Choose list.
    pub characters: Vec<PluginCharacterOption>,
    /// The declared permissions, when known; `None` means they are being
    /// fetched.
    pub permissions: Option<Vec<PermissionDeclaration>>,
    /// What the installed version declared (an update only).
    pub installed_permissions: Option<Vec<PermissionDeclaration>>,
    /// Who already has it enabled (an update only).
    pub affected_characters: Vec<String>,
    /// A local ZIP the player picked (its install runs from the file, not
    /// a release); `None` for a release.
    pub local_zip: Option<LocalZipSource>,
}

/// A reviewed local plugin ZIP: the file, and the hash of what was
/// reviewed (the install refuses a file that changed since).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalZipSource {
    /// The ZIP.
    pub path: std::path::PathBuf,
    /// Its SHA-256 when reviewed.
    pub sha256: String,
}

/// The dialog's state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginInstallDialog {
    /// Shown.
    pub is_open: bool,
    /// The install is running.
    pub is_busy: bool,
    /// What opened it.
    pub request: Option<InstallDialogRequest>,
    /// The Choose list.
    pub characters: Vec<PluginCharacterChoice>,
    /// The permission chips, in the vocabulary's order.
    pub permissions: Vec<PluginPermissionChip>,
    /// The permissions shown (passed to the install as the consent seen).
    pub displayed_permissions: Vec<PermissionDeclaration>,
    /// Fetching the permissions.
    pub is_loading_permissions: bool,
    /// Why the fetch failed.
    pub permissions_load_error: Option<String>,
    /// The update's permissions differ from the installed version's.
    pub permissions_changed: bool,
    /// Keep the plugin enabled where it already is (default off).
    pub keep_enabled: bool,
    /// Who to enable it for.
    pub choice: PluginEnableChoice,
    /// Why the install failed.
    pub error: Option<String>,
    /// Bumped on every open, so a stale fetch is ignored.
    pub generation: u64,
}

impl Default for PluginInstallDialog {
    fn default() -> Self {
        Self {
            is_open: false,
            is_busy: false,
            request: None,
            characters: Vec::new(),
            permissions: Vec::new(),
            displayed_permissions: Vec::new(),
            is_loading_permissions: false,
            permissions_load_error: None,
            permissions_changed: false,
            keep_enabled: false,
            choice: PluginEnableChoice::None,
            error: None,
            generation: 0,
        }
    }
}

impl PluginInstallDialog {
    /// Opens for `request`. The returned generation names a permission
    /// fetch the caller must start when the permissions are not known.
    pub fn open(&mut self, request: InstallDialogRequest) -> u64 {
        self.generation += 1;
        // A first install turns the plugin on for every account and
        // character; an update keeps each one's choice.
        let choice = if request.is_update {
            PluginEnableChoice::None
        } else {
            PluginEnableChoice::All
        };
        self.keep_enabled = false;
        self.characters = request
            .characters
            .iter()
            .cloned()
            .map(|option| PluginCharacterChoice {
                option,
                is_checked: false,
            })
            .collect();
        self.permissions_load_error = None;
        let known = request.permissions.clone();
        self.request = Some(request);
        match known {
            Some(permissions) => {
                self.is_loading_permissions = false;
                self.set_permissions(permissions);
            }
            None => {
                self.set_permissions(Vec::new());
                self.is_loading_permissions = true;
            }
        }
        self.choice = choice;
        self.error = None;
        self.is_open = true;
        self.generation
    }

    /// Closes, unless the install is running.
    pub fn close(&mut self) {
        if !self.is_busy {
            self.is_open = false;
            self.generation += 1;
        }
    }

    /// Applies a permission fetch started for `generation`.
    pub fn permissions_loaded(
        &mut self,
        generation: u64,
        result: Result<Vec<PermissionDeclaration>, String>,
    ) {
        if generation != self.generation {
            return;
        }
        self.is_loading_permissions = false;
        match result {
            Ok(permissions) => self.set_permissions(permissions),
            Err(error) => {
                self.permissions_load_error = Some(if error.trim().is_empty() {
                    "This plugin's details could not be checked.".into()
                } else {
                    error
                });
            }
        }
    }

    fn set_permissions(&mut self, permissions: Vec<PermissionDeclaration>) {
        let installed = self
            .request
            .as_ref()
            .and_then(|request| request.installed_permissions.clone());
        let mut ordered = permissions.clone();
        ordered.sort_by_key(|declaration| declaration.permission);
        self.permissions = ordered
            .iter()
            .map(|declaration| {
                let is_new = installed.as_ref().is_some_and(|installed| {
                    !installed
                        .iter()
                        .any(|existing| existing.permission == declaration.permission)
                });
                PluginPermissionChip::new(declaration, is_new)
            })
            .collect();
        self.permissions_changed = installed
            .as_ref()
            .is_some_and(|installed| !permissions_match(installed, &permissions));
        self.displayed_permissions = permissions;
    }

    /// The plugin's name (its id when it has none).
    pub fn display_name(&self) -> &str {
        self.request.as_ref().map_or("", |request| {
            if request.display_name.trim().is_empty() {
                &request.plugin_id
            } else {
                &request.display_name
            }
        })
    }

    /// An update rather than a first install.
    pub fn is_update(&self) -> bool {
        self.request
            .as_ref()
            .is_some_and(|request| request.is_update)
    }

    /// A first install, which turns the plugin on everywhere.
    pub fn show_enable_choice(&self) -> bool {
        !self.is_update()
    }

    /// What a first install does with the plugin, said in the dialog.
    pub fn enable_note(&self) -> Option<&'static str> {
        self.show_enable_choice().then_some(
            "It is turned on for every account and character. To turn it off for one, \
             open that character's or account's options.",
        )
    }

    /// The offered release is a pre-release.
    pub fn is_offered_prerelease(&self) -> bool {
        self.request
            .as_ref()
            .and_then(|request| request.offered_version.as_deref())
            .and_then(LauncherVersion::try_parse)
            .is_some_and(|version| version.is_pre_release())
    }

    /// Who already has it enabled.
    pub fn affected_characters(&self) -> &[String] {
        self.request
            .as_ref()
            .map_or(&[][..], |request| &request.affected_characters)
    }

    /// `Currently enabled for: ...`.
    pub fn affected_characters_text(&self) -> String {
        format!(
            "Currently enabled for: {}.",
            self.affected_characters().join(", ")
        )
    }

    /// Shown when an update changes permissions of a plugin some character
    /// already has enabled: the new permissions apply there only if the
    /// player says so here.
    pub fn show_keep_enabled_choice(&self) -> bool {
        self.is_update() && self.permissions_changed && !self.affected_characters().is_empty()
    }

    /// `Update and allow` when an update changes permissions, else
    /// `Install`.
    pub fn confirm_label(&self) -> &'static str {
        if self.is_update() && self.permissions_changed {
            "Update and allow"
        } else {
            "Install"
        }
    }

    /// The confirm button's text while the work runs.
    pub fn busy_label(&self) -> &'static str {
        if self.is_update() {
            "Updating…"
        } else {
            "Installing…"
        }
    }

    /// The responsibility notice every install and update shows, every time.
    pub fn warning_text(&self) -> String {
        let listed = self
            .request
            .as_ref()
            .is_some_and(|request| request.is_listed);
        let mut notice = String::from(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and \
             your responsibility. Only install plugins from authors you trust.",
        );
        if !listed {
            notice.push_str("\nThis plugin is not on the OpenAC plugin list.");
        }
        if self.is_offered_prerelease() {
            notice.push_str("\nThis version is a pre-release.");
        }
        notice
    }

    /// The heading over the chips.
    pub fn permissions_heading(&self) -> &'static str {
        "This plugin may:"
    }

    /// Shown instead of the chips when the release declares no permission:
    /// the client then links it none of the guarded functions.
    pub fn no_permissions_text(&self) -> Option<&'static str> {
        (self.is_open
            && !self.is_loading_permissions
            && self.permissions_load_error.is_none()
            && self.permissions.is_empty())
        .then_some("This plugin asks for no permissions: no chat, network, profiles or clipboard.")
    }

    /// Shown while the permissions are fetched.
    pub fn loading_permissions_text(&self) -> &'static str {
        "Checking what this plugin does…"
    }

    /// Whether Confirm may run.
    pub fn can_confirm(&self) -> bool {
        self.is_open
            && !self.is_busy
            && !self.is_loading_permissions
            && self.permissions_load_error.is_none()
    }

    /// The characters an install enables it for.
    pub fn chosen_characters(&self) -> Vec<PluginCharacterOption> {
        match self.choice {
            PluginEnableChoice::None => Vec::new(),
            PluginEnableChoice::All => self
                .characters
                .iter()
                .map(|choice| choice.option.clone())
                .collect(),
            PluginEnableChoice::Choose => self
                .characters
                .iter()
                .filter(|choice| choice.is_checked)
                .map(|choice| choice.option.clone())
                .collect(),
        }
    }

    /// Whether a finished update should take the plugin off every character:
    /// an update that adds permissions does not carry the old consent
    /// forward unless the player ticked Keep enabled.
    pub fn disables_after_update(&self) -> bool {
        self.show_keep_enabled_choice() && !self.keep_enabled
    }
}
