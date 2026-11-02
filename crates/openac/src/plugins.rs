//! What the program sets up for a session's plugins (the plugin roots, the
//! settings, the process's engine) and how plugin events print.
//!
//! The plugins themselves run inside each session (`ac-runtime`); the
//! program only says where they are.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use ac_plugin::abi::LogLevel;
use ac_plugin::storage::StorageRoots;
use ac_plugin::{
    HostVersion, PluginEngine, PluginReport, PluginSessionOptions, PluginSessionSettings,
};
use ac_runtime::plugins::{PluginEvent, PluginSetup};

/// The environment variable naming a settings file used when a session
/// document declares no `pluginSettings` (OpenAC's
/// `ACDREAM_PLUGIN_SETTINGS_FILE`).
pub const SETTINGS_FILE_VARIABLE: &str = "OPENAC_PLUGIN_SETTINGS_FILE";

/// Where plugins are found, in order: the `plugins` folder next to the
/// program (bundled plugins), then the data directory's (installed ones),
/// as OpenAC searches.
#[must_use]
pub fn roots(data_directory: Option<&Path>) -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(folder) = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(Path::to_path_buf))
    {
        roots.push(folder.join("plugins"));
    }
    if let Some(data) = data_directory {
        roots.push(data.join("plugins"));
    }
    roots
}

/// A session's plugin settings: the document's `pluginSettings` when it
/// declares them (the file is then not read), else the file named by
/// [`SETTINGS_FILE_VARIABLE`], else none.
///
/// # Errors
/// The file is missing or unusable: the client stops rather than start a
/// plugin without the settings the player asked for.
pub fn settings(
    declared: Option<&BTreeMap<String, BTreeMap<String, String>>>,
    env: impl Fn(&str) -> Option<String>,
) -> Result<PluginSessionSettings, String> {
    if let Some(declared) = declared {
        return Ok(PluginSessionSettings::from_map(declared));
    }
    match env(SETTINGS_FILE_VARIABLE).filter(|path| !path.trim().is_empty()) {
        None => Ok(PluginSessionSettings::empty()),
        Some(path) => PluginSessionSettings::read_file(Path::new(&path)).map_err(|e| e.0),
    }
}

/// The setup one session gets.
#[must_use]
pub fn setup(
    engine: &Arc<PluginEngine>,
    paths: Option<&ac_host::PathSet>,
    allow_list: Option<Vec<String>>,
    settings: PluginSessionSettings,
    peer_tags: Vec<String>,
) -> PluginSetup {
    PluginSetup {
        engine: Arc::clone(engine),
        peer_tags,
        hotkey_overrides: paths.map(|p| p.plugin_hotkeys_file()),
        peer_directory: paths.map(ac_host::PathSet::plugin_peers_directory),
        options: PluginSessionOptions {
            roots: roots(paths.map(|p| p.data_directory.as_path())),
            allow_list,
            settings,
            host_version: HostVersion::current(),
            storage: storage_roots(paths, |name| std::env::var(name).ok()),
            slow_log: slow_log(|name| std::env::var(name).ok()),
        },
    }
}

/// The slow log's threshold (milliseconds), from
/// [`ac_plugin::watchdog::SLOW_LOG_VARIABLE`]: off when unset, blank or not
/// a whole number. A diagnostic: every plugin call over it is written to
/// the client log.
#[must_use]
pub fn slow_log(env: impl Fn(&str) -> Option<String>) -> Option<std::time::Duration> {
    ac_plugin::watchdog::slow_log_threshold(env(ac_plugin::watchdog::SLOW_LOG_VARIABLE).as_deref())
}

/// Whether a plugin event also goes to the client log (standard error, the
/// launcher's `client.log`) whatever the console shows: a plugin that
/// failed or stopped with a fault, and the slow log.
#[must_use]
pub fn for_client_log(event: &PluginEvent) -> bool {
    matches!(
        event,
        PluginEvent::Report(PluginReport::Failed { .. } | PluginReport::SlowCall { .. })
    )
}

/// The environment variable naming the labels this client answers to among
/// the clients on this computer when a session document declares none
/// (OpenAC's `ACDREAM_PLUGIN_TAGS`): comma separated.
pub const TAGS_VARIABLE: &str = "OPENAC_PLUGIN_TAGS";

/// The labels this client answers to: the document's
/// `pluginTags` when it declares them, else [`TAGS_VARIABLE`] split on
/// commas (OpenAC `RuntimeOptions.ParsePluginTags`: trimmed, empties and
/// labels over 128 characters dropped, repeats ignoring case folded, at
/// most 128). The session normalizes them again as they travel.
#[must_use]
pub fn peer_tags(declared: Option<&[String]>, env: impl Fn(&str) -> Option<String>) -> Vec<String> {
    if let Some(declared) = declared {
        return declared.to_vec();
    }
    let mut tags: Vec<String> = Vec::new();
    for tag in env(TAGS_VARIABLE).unwrap_or_default().split(',') {
        let tag = tag.trim();
        if tag.is_empty()
            || tag.encode_utf16().count() > 128
            || tags.iter().any(|t| t.eq_ignore_ascii_case(tag))
        {
            continue;
        }
        tags.push(tag.to_owned());
        if tags.len() == 128 {
            break;
        }
    }
    tags
}

/// The process's plugin engine: with a path set, compiled plugins are kept
/// in `<cache>/plugin-modules` (`docs/launch-options.md`), so a plugin is
/// compiled once per plugin and client build rather than at every start.
#[must_use]
pub fn engine(paths: Option<&ac_host::PathSet>) -> std::sync::Arc<ac_plugin::PluginEngine> {
    std::sync::Arc::new(paths.map_or_else(ac_plugin::PluginEngine::new, |p| {
        ac_plugin::PluginEngine::with_module_cache(p.plugin_modules_directory())
    }))
}

/// The environment variable naming another folder for the shared VTank
/// profiles (OpenAC's `ACDREAM_VTANK_PROFILE_DIR`).
pub const VTANK_PROFILE_DIR_VARIABLE: &str = "OPENAC_VTANK_PROFILE_DIR";

/// Where plugin storage lives: each plugin's data in `data` inside its own
/// folder under the plugins folder, the shared VTank profiles in `vtank`
/// inside MossTank's (`ac_host::PathSet`), or in the folder
/// [`VTANK_PROFILE_DIR_VARIABLE`] names (unset or empty: the default); none
/// without a path set.
#[must_use]
pub fn storage_roots(
    paths: Option<&ac_host::PathSet>,
    env: impl Fn(&str) -> Option<String>,
) -> StorageRoots {
    let profiles_override = env(VTANK_PROFILE_DIR_VARIABLE)
        .filter(|dir| !dir.is_empty())
        .map(PathBuf::from);
    StorageRoots {
        plugin_data: paths.map(ac_host::PathSet::plugin_storage_directory),
        profiles: profiles_override
            .or_else(|| paths.map(ac_host::PathSet::vtank_profiles_directory)),
    }
}

/// How a plugin event prints on the console.
#[must_use]
pub fn event_line(event: &PluginEvent) -> String {
    match event {
        PluginEvent::Log {
            plugin,
            level,
            message,
        } => match level {
            LogLevel::Info => format!("plugin {plugin}: {message}"),
            other => format!("plugin {plugin} {}: {message}", other.name()),
        },
        PluginEvent::Report(PluginReport::Loaded { id }) => format!("plugin {id} loaded"),
        PluginEvent::Report(PluginReport::Failed { id, error }) => {
            format!("plugin {id} failed: {error}")
        }
        PluginEvent::Report(PluginReport::SlowCall { id, message }) => {
            format!("plugin {id} slow: {message}")
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `RuntimeOptions.ParsePluginTags` and
    // `SessionConfigPluginFieldParityTests`: the document's
    // labels win; the variable is split on commas, trimmed, folded ignoring
    // case, over-long labels dropped.
    #[test]
    fn plugin_tags_come_from_the_document_else_the_variable() {
        let env = |_: &str| Some(format!(" healer, Tank ,,HEALER,{} ,buff", "x".repeat(129)));
        assert_eq!(peer_tags(None, env), ["healer", "Tank", "buff"]);
        let declared = vec!["one".to_owned()];
        assert_eq!(peer_tags(Some(&declared), env), ["one"]);
        assert!(peer_tags(None, |_| None).is_empty());
    }

    // OpenAC `RuntimeOptionsTests.VtankProfileDirectoryOverrideIsNullUnlessSet`
    // and `Program`'s `VtankProfileDirectoryOverride ?? VtankProfilesDirectory`
    // (`ACDREAM_VTANK_PROFILE_DIR`, here `OPENAC_VTANK_PROFILE_DIR`): an
    // empty value is unset; a set one replaces the default folder, with or
    // without a path set.
    #[test]
    fn the_vtank_profile_folder_can_be_moved() {
        let paths =
            ac_host::PathSet::for_root(Path::new("C:/root"), ac_host::paths::RootSource::Explicit);
        let default = storage_roots(Some(&paths), |_| None);
        assert_eq!(default.profiles, Some(paths.vtank_profiles_directory()));
        assert_eq!(default.plugin_data, Some(paths.plugin_storage_directory()));
        let blank = storage_roots(Some(&paths), |_| Some(String::new()));
        assert_eq!(blank.profiles, Some(paths.vtank_profiles_directory()));
        let set = |name: &str| {
            (name == VTANK_PROFILE_DIR_VARIABLE)
                .then(|| "C:/Games/VirindiPlugins/VirindiTank".to_owned())
        };
        let moved = storage_roots(Some(&paths), set);
        assert_eq!(
            moved.profiles,
            Some(PathBuf::from("C:/Games/VirindiPlugins/VirindiTank"))
        );
        assert_eq!(moved.plugin_data, Some(paths.plugin_storage_directory()));
        let without_paths = storage_roots(None, set);
        assert_eq!(
            without_paths.profiles,
            Some(PathBuf::from("C:/Games/VirindiPlugins/VirindiTank"))
        );
        assert_eq!(without_paths.plugin_data, None);
        assert_eq!(storage_roots(None, |_| None), StorageRoots::default());
    }

    // OpenAC `HeadlessPluginSettingsFileTests`: the document wins and leaves
    // the file unread; no file means no settings; a missing file stops.
    #[test]
    fn the_document_wins_over_the_settings_file() {
        let declared = BTreeMap::from([(
            "openac.hello".to_owned(),
            BTreeMap::from([("greeting".to_owned(), "Hi".to_owned())]),
        )]);
        let missing = |_: &str| Some("Z:/no/such/settings.json".to_owned());
        let settings = settings(Some(&declared), missing).unwrap();
        assert_eq!(settings.get("openac.hello", "greeting"), Some("Hi"));

        assert_eq!(
            super::settings(None, |_| None).unwrap(),
            PluginSessionSettings::empty()
        );
        let error = super::settings(None, missing).unwrap_err();
        assert!(error.contains("does not exist"), "{error}");
    }

    #[test]
    fn plugins_are_found_next_to_the_program_then_in_the_data_directory() {
        let roots = roots(Some(Path::new("data")));
        assert_eq!(roots.len(), 2);
        assert!(roots[0].ends_with("plugins"));
        assert_eq!(roots[1], Path::new("data").join("plugins"));
    }

    #[test]
    fn events_print_with_the_plugin_id() {
        let log = |level| PluginEvent::Log {
            plugin: "openac.hello".into(),
            level,
            message: "hi".into(),
        };
        assert_eq!(event_line(&log(LogLevel::Info)), "plugin openac.hello: hi");
        assert_eq!(
            event_line(&log(LogLevel::Error)),
            "plugin openac.hello error: hi"
        );
        assert_eq!(
            event_line(&PluginEvent::Report(PluginReport::Failed {
                id: "x.y".into(),
                error: "no".into()
            })),
            "plugin x.y failed: no"
        );
        let slow = PluginEvent::Report(PluginReport::SlowCall {
            id: "x.y".into(),
            message: "tick took 23.4 ms; imports: none".into(),
        });
        assert_eq!(
            event_line(&slow),
            "plugin x.y slow: tick took 23.4 ms; imports: none"
        );
        assert!(for_client_log(&slow));
        assert!(!for_client_log(&log(LogLevel::Error)));
    }

    #[test]
    fn the_slow_log_is_off_unless_the_variable_holds_a_number() {
        let env = |value: &'static str| {
            move |name: &str| (name == "OPENAC_PLUGIN_SLOW_LOG").then(|| value.to_owned())
        };
        assert_eq!(
            slow_log(env("20")),
            Some(std::time::Duration::from_millis(20))
        );
        assert_eq!(slow_log(env("")), None);
        assert_eq!(slow_log(env("on")), None);
        assert_eq!(slow_log(|_| None), None);
    }
}
