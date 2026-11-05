//! Where a host keeps everything: one install root and the folders beneath
//! it, and the platform facts that decide it.
//!
//! The root holds `app/`, `game/`, `settings/`, `plugins/`, `logs/`,
//! `screenshots/`, `journal/` and `cache/`. Each plugin's folder holds its
//! code and the player's `data/` for it; MossTank's folder also holds the
//! shared VTank profiles (`vtank/`). `logs/` holds one folder per
//! launcher-started session (`<server>/<character>/<start time>/`), the
//! launcher's own log, crash reports and the plugin logs an older layout
//! kept. The three members (settings, data, cache) stay separately
//! overridable so automation can isolate one on its own; the data member is
//! the root itself.

use std::path::{Component, Path, PathBuf};

use crate::config::ConfigError;

/// Environment variable naming the install root for this process and its
/// children.
pub const ROOT_DIR_VAR: &str = "OPENAC_ROOT_DIR";
/// Environment variable naming the settings folder alone.
pub const CONFIG_DIR_VAR: &str = "OPENAC_CONFIG_DIR";
/// Environment variable naming the data folder (the root itself) alone.
pub const DATA_DIR_VAR: &str = "OPENAC_DATA_DIR";
/// Environment variable naming the cache folder alone.
pub const CACHE_DIR_VAR: &str = "OPENAC_CACHE_DIR";

/// The name of the per-plugin folder that holds its private files.
pub const PLUGIN_FILES_FOLDER_NAME: &str = "data";

/// The name of the folder inside [`VTANK_PLUGIN_ID`]'s folder that holds the
/// shared VTank profiles.
pub const VTANK_FOLDER_NAME: &str = "vtank";

/// The plugin whose folder holds the shared VTank profiles. The folder is
/// the plugins' `profiles` storage root, shared by every plugin allowed it,
/// and it is kept there even while that plugin is not installed.
pub const VTANK_PLUGIN_ID: &str = "acdream.mosstank";

/// The launcher's profile document in the settings folder.
pub const LAUNCHER_PROFILES_FILE_NAME: &str = "launcher-profiles.v2.json";

/// A session folder's session-config document.
pub const SESSION_CONFIG_FILE_NAME: &str = "session.json";
/// A session folder's status file.
pub const SESSION_STATUS_FILE_NAME: &str = "status.jsonl";
/// A session folder's client log: the client's standard output and error.
pub const SESSION_CLIENT_LOG_FILE_NAME: &str = "client.log";
/// A session folder's folder of plugin logs, one `<plugin id>.log` each.
pub const SESSION_PLUGIN_LOGS_FOLDER_NAME: &str = "plugins";

/// The folders directly in `logs/` that are not a server's sessions. A
/// server whose name is one of these gets its folder name changed (see
/// [`PathSet::session_logs_directory`]).
pub const RESERVED_LOG_FOLDER_NAMES: [&str; 4] =
    ["crash", "launcher", "old plugin logs", "unsorted"];

/// Whether a top-level folder in a plugin's folder holds the player's
/// files rather than the plugin's code: the plugin's own `data` folder and
/// the shared VTank profiles' `vtank` folder. Installs, updates and code
/// removal never touch them, a package may not ship them, and a plugin
/// folder holding only them is not an install.
pub fn is_player_folder(name: &str) -> bool {
    name.eq_ignore_ascii_case(PLUGIN_FILES_FOLDER_NAME)
        || name.eq_ignore_ascii_case(VTANK_FOLDER_NAME)
}

/// `name` as a single file or folder name every supported system accepts:
/// characters Windows reserves and control characters become `_`, as do
/// trailing dots and spaces; a reserved device name (`CON`, `NUL`, `COM1`,
/// ... with or without an extension) gets a `_` after its stem; an empty
/// name is `_`.
pub fn file_name_safe(name: &str) -> String {
    let mut safe: Vec<char> = name
        .chars()
        .map(|c| match c {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '_',
            c if c.is_control() => '_',
            c => c,
        })
        .collect();
    for c in safe.iter_mut().rev() {
        if *c == '.' || *c == ' ' {
            *c = '_';
        } else {
            break;
        }
    }
    let mut safe: String = safe.into_iter().collect();
    if safe.is_empty() {
        safe.push('_');
    }
    let stem_end = safe.find('.').unwrap_or(safe.len());
    let stem = safe[..stem_end].to_ascii_uppercase();
    let numbered = (stem.starts_with("COM") || stem.starts_with("LPT"))
        && stem.len() == 4
        && matches!(stem.as_bytes()[3], b'1'..=b'9');
    if numbered || matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL") {
        safe.insert(stem_end, '_');
    }
    safe
}

/// The default root's folder name under each platform folder. It differs
/// from OpenAC's (`OpenAC`, `openac` on Linux) so the two programs never
/// share an install: their stored formats are not the same.
pub(crate) const APP_LEAF: &str = "openac-rust";

/// Which operating system the rules follow.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Os {
    /// Windows: local application data.
    Windows,
    /// macOS: `~/Library/Application Support`.
    MacOs,
    /// Linux: the XDG data home.
    Linux,
    /// Any other Unix: the XDG data home.
    OtherUnix,
}

impl Os {
    /// The system this program was built for.
    pub const fn current() -> Self {
        if cfg!(windows) {
            Self::Windows
        } else if cfg!(target_os = "macos") {
            Self::MacOs
        } else if cfg!(target_os = "linux") {
            Self::Linux
        } else {
            Self::OtherUnix
        }
    }

    /// True for every Unix-like system.
    pub const fn is_unix(self) -> bool {
        !matches!(self, Self::Windows)
    }

    /// Whether folder names compare ignoring case (Windows and macOS).
    pub const fn ignores_case(self) -> bool {
        matches!(self, Self::Windows | Self::MacOs)
    }
}

/// A folder the operating system names.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KnownFolder {
    /// The user's home directory.
    UserProfile,
    /// Windows roaming application data (`%APPDATA%`).
    ApplicationData,
    /// Windows local application data (`%LOCALAPPDATA%`).
    LocalApplicationData,
}

impl KnownFolder {
    const fn name(self) -> &'static str {
        match self {
            Self::UserProfile => "UserProfile",
            Self::ApplicationData => "ApplicationData",
            Self::LocalApplicationData => "LocalApplicationData",
        }
    }
}

/// The platform facts startup reads. Tests supply their own.
pub trait Platform {
    /// The operating system whose rules apply.
    fn os(&self) -> Os;
    /// The working directory relative paths resolve against.
    fn current_dir(&self) -> Option<PathBuf>;
    /// An environment variable, if set and valid Unicode.
    fn var(&self, name: &str) -> Option<String>;
    /// A folder the operating system names, if it names one.
    fn known_folder(&self, folder: KnownFolder) -> Option<PathBuf>;
    /// The text of a small file, or `None` when it does not exist. The
    /// root pointer is read through this so tests can supply one without
    /// touching the real user profile.
    fn read_text_file(&self, path: &Path) -> Option<String> {
        std::fs::read_to_string(path).ok()
    }
}

/// The real process environment.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemPlatform;

impl Platform for SystemPlatform {
    fn os(&self) -> Os {
        Os::current()
    }

    fn current_dir(&self) -> Option<PathBuf> {
        std::env::current_dir().ok()
    }

    fn var(&self, name: &str) -> Option<String> {
        std::env::var(name).ok()
    }

    fn known_folder(&self, folder: KnownFolder) -> Option<PathBuf> {
        let found = match folder {
            KnownFolder::UserProfile => std::env::home_dir(),
            KnownFolder::ApplicationData => std::env::var_os("APPDATA").map(PathBuf::from),
            KnownFolder::LocalApplicationData => {
                std::env::var_os("LOCALAPPDATA").map(PathBuf::from)
            }
        };
        found.filter(|path| !path.as_os_str().is_empty())
    }
}

/// Folder overrides, from the document's `process.paths` or the command
/// line. Absent (or blank) means "use the next source".
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PathOverrides {
    /// `rootDirectory` / `--root-dir`: the whole install.
    pub root_directory: Option<String>,
    /// `configDirectory` / `--config-dir`.
    pub config_directory: Option<String>,
    /// `dataDirectory` / `--data-dir`.
    pub data_directory: Option<String>,
    /// `cacheDirectory` / `--cache-dir`.
    pub cache_directory: Option<String>,
}

impl PathOverrides {
    /// These overrides with `command_line` on top: a command-line value
    /// wins, and a root named there replaces the whole configured set,
    /// members included, since it names a different install.
    pub fn merge(&self, command_line: &Self) -> Self {
        if command_line.root_directory.is_some() {
            return command_line.clone();
        }
        Self {
            root_directory: self.root_directory.clone(),
            config_directory: command_line
                .config_directory
                .clone()
                .or_else(|| self.config_directory.clone()),
            data_directory: command_line
                .data_directory
                .clone()
                .or_else(|| self.data_directory.clone()),
            cache_directory: command_line
                .cache_directory
                .clone()
                .or_else(|| self.cache_directory.clone()),
        }
    }

    /// Whether any folder is named.
    pub fn any(&self) -> bool {
        self.root_directory.is_some()
            || self.config_directory.is_some()
            || self.data_directory.is_some()
            || self.cache_directory.is_some()
    }
}

/// How the install root of a resolved set was chosen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RootSource {
    /// A caller argument named the root or one of its members.
    Explicit,
    /// An environment variable named the root or one of its members.
    Environment,
    /// The pointer file in the default root named the root.
    Pointer,
    /// Nothing named a root; the per-user default is used.
    Default,
}

/// Every folder the client, the windowless host and the launcher read or
/// write, absolute and normalized, derived from one install root.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PathSet {
    /// Settings, key bindings, credentials files and launcher profiles
    /// (`<root>/settings`).
    pub config_directory: PathBuf,
    /// The install root itself.
    pub data_directory: PathBuf,
    /// Anything that can be rebuilt (`<root>/cache`).
    pub cache_directory: PathBuf,
    /// How the root was chosen.
    pub root_source: RootSource,
}

impl PathSet {
    /// The standard member layout of one root.
    pub fn for_root(root: &Path, source: RootSource) -> Self {
        Self {
            config_directory: root.join("settings"),
            data_directory: root.to_path_buf(),
            cache_directory: root.join("cache"),
            root_source: source,
        }
    }

    /// A set whose three members are named one by one.
    pub fn from_members(config: PathBuf, data: PathBuf, cache: PathBuf) -> Self {
        Self {
            config_directory: config,
            data_directory: data,
            cache_directory: cache,
            root_source: RootSource::Explicit,
        }
    }

    /// The install root; every other member lives beneath it.
    pub fn root_directory(&self) -> &Path {
        &self.data_directory
    }

    /// The player's settings file (display, audio, chat, ...).
    pub fn settings_file(&self) -> PathBuf {
        self.config_directory.join("settings.json")
    }

    /// The player's key bindings.
    pub fn key_bindings_file(&self) -> PathBuf {
        self.config_directory.join("keybinds.json")
    }

    /// The player's plugin hotkey overrides, beside the key bindings file
    /// and the settings file.
    pub fn plugin_hotkeys_file(&self) -> PathBuf {
        self.config_directory.join("plugin-hotkeys.json")
    }

    /// Client versions, the current-version records and the update lock.
    pub fn app_directory(&self) -> PathBuf {
        self.data_directory.join("app")
    }

    /// The prepared game content and its install record.
    pub fn game_data_directory(&self) -> PathBuf {
        self.data_directory.join("game")
    }

    /// The marker saying which folder layout the root is in.
    pub fn layout_marker_file(&self) -> PathBuf {
        self.data_directory.join("layout.json")
    }

    /// The lock a process holds while it moves the root into the current
    /// layout.
    pub fn layout_lock_file(&self) -> PathBuf {
        self.data_directory.join("layout.lock")
    }

    /// The launcher's profile document, which names the servers.
    pub fn launcher_profiles_file(&self) -> PathBuf {
        self.config_directory.join(LAUNCHER_PROFILES_FILE_NAME)
    }

    /// Session folders, chat logs, and the crash and launcher folders.
    pub fn logs_directory(&self) -> PathBuf {
        self.data_directory.join("logs")
    }

    /// Client and launcher crash reports.
    pub fn crash_reports_directory(&self) -> PathBuf {
        self.logs_directory().join("crash")
    }

    /// The launcher's own log folder.
    pub fn launcher_log_directory(&self) -> PathBuf {
        self.logs_directory().join("launcher")
    }

    /// The launcher's own log.
    pub fn launcher_log_file(&self) -> PathBuf {
        self.launcher_log_directory().join("launcher.log")
    }

    /// The plugin debug logs an older layout kept in a plugin's data.
    pub fn old_plugin_logs_directory(&self) -> PathBuf {
        self.logs_directory().join("old plugin logs")
    }

    /// Session folders from an older layout whose server and character
    /// could not be told.
    pub fn unsorted_logs_directory(&self) -> PathBuf {
        self.logs_directory().join("unsorted")
    }

    /// The folder holding one character's launcher-started sessions, one
    /// folder per session named by its start time:
    /// `logs/<server>/<character>`. Both names are made file-name safe
    /// ([`file_name_safe`]); a server named like one of the other folders
    /// in `logs/` ([`RESERVED_LOG_FOLDER_NAMES`]) gets a `_` after it.
    pub fn session_logs_directory(&self, server: &str, character: &str) -> PathBuf {
        let mut server = file_name_safe(server);
        if RESERVED_LOG_FOLDER_NAMES
            .iter()
            .any(|reserved| reserved.eq_ignore_ascii_case(&server))
        {
            server.push('_');
        }
        self.logs_directory()
            .join(server)
            .join(file_name_safe(character))
    }

    /// Screenshots.
    pub fn screenshots_directory(&self) -> PathBuf {
        self.data_directory.join("screenshots")
    }

    /// The player's quest journal notes.
    pub fn journal_directory(&self) -> PathBuf {
        self.data_directory.join("journal")
    }

    /// Installed plugin code, one folder per plugin id.
    pub fn plugins_directory(&self) -> PathBuf {
        self.data_directory.join("plugins")
    }

    /// The root plugin private storage is keyed under: a plugin's own
    /// files live at `<id>/data` beneath it, inside the plugin's folder,
    /// where installs, updates and code removal never touch them. The same
    /// place on every host, so a plugin run by a bot and by a window reads
    /// and writes the same files.
    pub fn plugin_storage_directory(&self) -> PathBuf {
        self.plugins_directory()
    }

    /// The private files folder of one plugin.
    pub fn plugin_files_directory(&self, plugin_id: &str) -> PathBuf {
        self.plugin_storage_directory()
            .join(plugin_id)
            .join(PLUGIN_FILES_FOLDER_NAME)
    }

    /// Shared VTank profiles, in MossTank's plugin folder
    /// ([`VTANK_PLUGIN_ID`], [`VTANK_FOLDER_NAME`]).
    pub fn vtank_profiles_directory(&self) -> PathBuf {
        self.plugins_directory()
            .join(VTANK_PLUGIN_ID)
            .join(VTANK_FOLDER_NAME)
    }

    /// Local diagnostics: the GPU capability report.
    pub fn diagnostics_directory(&self) -> PathBuf {
        self.cache_directory.join("diagnostics")
    }

    /// Where the clients on this machine leave notes for one another.
    pub fn plugin_peers_directory(&self) -> PathBuf {
        self.cache_directory.join("plugin-peers")
    }

    /// Plugins compiled to machine code, one file per plugin build and
    /// client build (`ac_plugin::ModuleCache`); safe to delete.
    pub fn plugin_modules_directory(&self) -> PathBuf {
        self.cache_directory.join("plugin-modules")
    }

    /// The per-user default root: `%LOCALAPPDATA%\openac-rust` on Windows,
    /// `~/Library/Application Support/openac-rust` on macOS and
    /// `$XDG_DATA_HOME/openac-rust` (or `~/.local/share/openac-rust`)
    /// elsewhere. It is the only place the pointer file is read from.
    pub fn default_root(platform: &dyn Platform) -> Result<PathBuf, ConfigError> {
        let root = match platform.os() {
            Os::Windows => {
                require_folder(platform, KnownFolder::LocalApplicationData)?.join(APP_LEAF)
            }
            Os::MacOs => require_folder(platform, KnownFolder::UserProfile)?
                .join("Library")
                .join("Application Support")
                .join(APP_LEAF),
            Os::Linux | Os::OtherUnix => {
                let home = require_folder(platform, KnownFolder::UserProfile)?;
                xdg(platform, "XDG_DATA_HOME", home.join(".local").join("share"))
            }
        };
        normalize(&root, &working_directory(platform)?)
    }

    /// Resolves every member. For each member the first of these that
    /// answers wins: an explicit member; an explicit root; the member's own
    /// `OPENAC_*_DIR` variable; `OPENAC_ROOT_DIR`; the pointer file in the
    /// default root; the default root. An explicit root ignores the
    /// environment entirely. Relative paths resolve against the working
    /// directory; a blank value counts as absent.
    pub fn resolve(
        overrides: &PathOverrides,
        platform: &dyn Platform,
    ) -> Result<Self, ConfigError> {
        let non_empty = |value: Option<String>| value.filter(|text| !text.trim().is_empty());
        let mut config = non_empty(overrides.config_directory.clone());
        let mut data = non_empty(overrides.data_directory.clone());
        let mut cache = non_empty(overrides.cache_directory.clone());
        let explicit_root = non_empty(overrides.root_directory.clone());
        let any_explicit =
            config.is_some() || data.is_some() || cache.is_some() || explicit_root.is_some();

        // An explicit root isolates the whole set from the environment: a
        // bot configuration or a launcher argument naming a folder means
        // exactly that folder, whatever the shell happened to export.
        if explicit_root.is_none() {
            config = config.or_else(|| non_empty(platform.var(CONFIG_DIR_VAR)));
            data = data.or_else(|| non_empty(platform.var(DATA_DIR_VAR)));
            cache = cache.or_else(|| non_empty(platform.var(CACHE_DIR_VAR)));
        }

        let current = working_directory(platform)?;
        let (root, mut source) = if let Some(root) = explicit_root {
            (PathBuf::from(root), RootSource::Explicit)
        } else if let Some(root) = non_empty(platform.var(ROOT_DIR_VAR)) {
            (PathBuf::from(root), RootSource::Environment)
        } else {
            let default = Self::default_root(platform)?;
            match root_pointer::try_read(&default, platform)? {
                Some(pointed) => (pointed, RootSource::Pointer),
                None => (default, RootSource::Default),
            }
        };

        // A member named on its own moves that member off the chosen root,
        // so the set is no longer purely the pointer's or the default's.
        if (config.is_some() || data.is_some() || cache.is_some())
            && matches!(source, RootSource::Pointer | RootSource::Default)
        {
            source = if any_explicit {
                RootSource::Explicit
            } else {
                RootSource::Environment
            };
        }

        let root = normalize(&root, &current)?;
        let member = |named: Option<String>, default: PathBuf| match named {
            Some(path) => normalize(Path::new(&path), &current),
            None => Ok(default),
        };
        Ok(Self {
            config_directory: member(config, root.join("settings"))?,
            data_directory: member(data, root.clone())?,
            cache_directory: member(cache, root.join("cache"))?,
            root_source: source,
        })
    }
}

/// The pointer file `root.json` in the default root:
/// `{ "version": 1, "root": "<absolute path>" }`. It exists only when the
/// player moved the install somewhere else, and it is the only thing left
/// in the default root afterwards, so a client or windowless host started
/// by hand still finds the moved install.
pub mod root_pointer {
    use std::path::{Path, PathBuf};

    use super::{Platform, full_path, same_folder};
    use crate::config::ConfigError;
    use crate::json;

    /// The pointer file's name in the default root.
    pub const FILE_NAME: &str = "root.json";

    const CURRENT_VERSION: i32 = 1;

    /// The pointer file's path for a default root.
    pub fn path_for(default_root: &Path) -> PathBuf {
        default_root.join(FILE_NAME)
    }

    /// The root the pointer names, or `None` when there is no pointer. A
    /// pointer that exists but cannot be understood is an error rather
    /// than silence: falling back to the default would make every setting
    /// and plugin file look lost.
    pub fn try_read(
        default_root: &Path,
        platform: &dyn Platform,
    ) -> Result<Option<PathBuf>, ConfigError> {
        let path = path_for(default_root);
        let Some(text) = platform.read_text_file(&path) else {
            return Ok(None);
        };
        parse(&text).map(Some).ok_or_else(|| {
            ConfigError::Invalid(format!(
                "The install folder pointer '{}' is not valid. It must be \
                 {{ \"version\": 1, \"root\": \"<absolute folder>\" }}. \
                 Fix it, or delete it to use the default folder.",
                path.display()
            ))
        })
    }

    /// The root named by pointer text, or `None` when the text is not a
    /// valid pointer.
    pub fn parse(text: &str) -> Option<PathBuf> {
        let document = json::parse(text.as_bytes()).ok()?;
        let members = json::object_members(&document).ok()?;
        let find = |name: &str| members.iter().find(|m| m.name == name).map(|m| &m.value);
        if json::int32(find("version")?).ok()? != CURRENT_VERSION {
            return None;
        }
        let folder = json::string_or_null(find("root")?).ok()??;
        let folder = PathBuf::from(folder);
        if folder.to_string_lossy().trim().is_empty() || !folder.is_absolute() {
            return None;
        }
        Some(full_path(&folder, Path::new("")))
    }

    /// Points the default root at `root`. Pointing it at itself removes
    /// the pointer instead, since the default needs none.
    pub fn write(default_root: &Path, root: &Path) -> std::io::Result<()> {
        let path = path_for(default_root);
        if same_folder(default_root, root) {
            match std::fs::remove_file(&path) {
                Err(error) if error.kind() != std::io::ErrorKind::NotFound => return Err(error),
                _ => return Ok(()),
            }
        }
        std::fs::create_dir_all(default_root)?;
        let mut text = String::from("{\n  \"version\": 1,\n  \"root\": ");
        json::write_string(&mut text, &root.to_string_lossy());
        text.push_str("\n}");
        let mut temporary = path.as_os_str().to_owned();
        temporary.push(".tmp");
        std::fs::write(&temporary, text)?;
        std::fs::rename(&temporary, &path)
    }
}

/// Whether two folders are the same, with the platform's own case rule.
pub fn same_folder(left: &Path, right: &Path) -> bool {
    let base = Path::new("");
    let left = full_path(left, base);
    let right = full_path(right, base);
    if Os::current().ignores_case() {
        left.to_string_lossy().to_lowercase() == right.to_string_lossy().to_lowercase()
    } else {
        left == right
    }
}

/// Whether `candidate` is `folder` or lies beneath it, with the platform's
/// own case rule.
pub fn same_or_inside(candidate: &Path, folder: &Path) -> bool {
    let base = Path::new("");
    let folder = full_path(folder, base);
    let mut walk = Some(full_path(candidate, base));
    while let Some(path) = walk {
        if same_folder(&path, &folder) {
            return true;
        }
        walk = path.parent().map(Path::to_path_buf);
    }
    false
}

pub(crate) fn xdg(platform: &dyn Platform, variable: &str, fallback: PathBuf) -> PathBuf {
    let root = platform
        .var(variable)
        .filter(|value| !value.trim().is_empty())
        .map_or(fallback, PathBuf::from);
    root.join(APP_LEAF)
}

pub(crate) fn require_folder(
    platform: &dyn Platform,
    folder: KnownFolder,
) -> Result<PathBuf, ConfigError> {
    platform.known_folder(folder).ok_or_else(|| {
        ConfigError::Invalid(format!(
            "The operating system did not provide {}.",
            folder.name()
        ))
    })
}

fn working_directory(platform: &dyn Platform) -> Result<PathBuf, ConfigError> {
    platform.current_dir().ok_or_else(|| {
        ConfigError::Invalid("The operating system did not provide the working directory.".into())
    })
}

fn normalize(path: &Path, current: &Path) -> Result<PathBuf, ConfigError> {
    if path.to_string_lossy().trim().is_empty() {
        return Err(ConfigError::Invalid(
            "Application directories cannot be empty.".into(),
        ));
    }
    Ok(full_path(path, current))
}

/// `path` made absolute against `base`, with `.` and `..` resolved by text
/// alone (the file system is not consulted) and no trailing separator.
pub fn full_path(path: &Path, base: &Path) -> PathBuf {
    let joined = if path.is_absolute() {
        path.to_path_buf()
    } else {
        base.join(path)
    };
    let mut out = PathBuf::new();
    for component in joined.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                // Never climbs above the root.
                if matches!(out.components().next_back(), Some(Component::Normal(_))) {
                    out.pop();
                }
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn full_path_resolves_dots_and_relative_paths() {
        let base = std::env::temp_dir().join("base");
        assert_eq!(
            full_path(Path::new("a/./b/../c"), &base),
            base.join("a").join("c")
        );
        let absolute = std::env::temp_dir().join("x");
        assert_eq!(full_path(&absolute.join(".."), &base), std::env::temp_dir());
    }

    #[test]
    fn merge_prefers_the_command_line() {
        let document = PathOverrides {
            config_directory: Some("doc-cfg".into()),
            data_directory: Some("doc-data".into()),
            ..PathOverrides::default()
        };
        let command_line = PathOverrides {
            config_directory: Some("cli-cfg".into()),
            cache_directory: Some("cli-cache".into()),
            ..PathOverrides::default()
        };
        let merged = document.merge(&command_line);
        assert_eq!(merged.config_directory.as_deref(), Some("cli-cfg"));
        assert_eq!(merged.data_directory.as_deref(), Some("doc-data"));
        assert_eq!(merged.cache_directory.as_deref(), Some("cli-cache"));
    }

    // OpenAC HeadlessPathSetTests.CommandLineRootReplacesTheConfiguredFolders.
    #[test]
    fn a_command_line_root_replaces_the_configured_folders() {
        let configured = PathOverrides {
            config_directory: Some("configured-config".into()),
            root_directory: Some("configured-root".into()),
            ..PathOverrides::default()
        };
        let command_line = PathOverrides {
            root_directory: Some("cli-root".into()),
            ..PathOverrides::default()
        };
        assert_eq!(configured.merge(&command_line), command_line);
    }
}
