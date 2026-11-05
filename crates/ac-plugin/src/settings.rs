//! The startup settings a session was given for each plugin (the session
//! document's `pluginSettings`, or a settings file), keyed by plugin id and
//! then by setting name. One snapshot per session; a plugin reads only its
//! own.

use std::collections::BTreeMap;
use std::path::Path;

use ac_host::json::{self, Value};

/// The settings of every plugin of one session.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PluginSessionSettings {
    by_plugin: BTreeMap<String, BTreeMap<String, String>>,
}

/// Why a settings file cannot be used. A client stops instead of starting a
/// plugin without the settings the player asked for.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{0}")]
pub struct SettingsError(pub String);

impl PluginSessionSettings {
    /// Settings for nobody.
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }

    /// A copy of a declared map (already checked for holes by the session
    /// document reader).
    #[must_use]
    pub fn from_map(declared: &BTreeMap<String, BTreeMap<String, String>>) -> Self {
        Self {
            by_plugin: declared.clone(),
        }
    }

    /// The plugin ids that have settings.
    pub fn plugin_ids(&self) -> impl Iterator<Item = &str> {
        self.by_plugin.keys().map(String::as_str)
    }

    /// One plugin's settings; empty when it has none.
    #[must_use]
    pub fn for_plugin(&self, plugin_id: &str) -> Option<&BTreeMap<String, String>> {
        self.by_plugin.get(plugin_id)
    }

    /// One setting of one plugin.
    #[must_use]
    pub fn get(&self, plugin_id: &str, key: &str) -> Option<&str> {
        self.by_plugin.get(plugin_id)?.get(key).map(String::as_str)
    }

    /// Every setting of plugin `plugin_id`, by key.
    #[must_use]
    pub fn all(&self, plugin_id: &str) -> Vec<(String, String)> {
        self.by_plugin
            .get(plugin_id)
            .map(|map| map.iter().map(|(k, v)| (k.clone(), v.clone())).collect())
            .unwrap_or_default()
    }

    /// Reads a file whose whole content is the map: one object of string
    /// settings per plugin id.
    ///
    /// # Errors
    /// The file is missing, unreadable, not JSON, not that shape, or has a
    /// null where a value belongs; the message names the file.
    pub fn read_file(path: &Path) -> Result<Self, SettingsError> {
        let shown = path.display();
        let bytes = std::fs::read(path).map_err(|error| {
            if error.kind() == std::io::ErrorKind::NotFound {
                SettingsError(format!("Plugin settings file '{shown}' does not exist."))
            } else {
                SettingsError(format!(
                    "Plugin settings file '{shown}' could not be read: {error}"
                ))
            }
        })?;
        let root = json::parse(&bytes).map_err(|error| {
            SettingsError(format!(
                "Plugin settings file '{shown}' is not valid JSON: {error}"
            ))
        })?;
        let shape = || {
            SettingsError(format!(
                "Plugin settings file '{shown}' must hold an object mapping plugin ids to their \
                 settings."
            ))
        };
        let Value::Object(plugins) = &root.value else {
            return Err(shape());
        };
        let mut by_plugin = BTreeMap::new();
        for plugin in plugins {
            let settings = match &plugin.value.value {
                Value::Null => {
                    return Err(SettingsError(format!(
                        "Plugin settings file '{shown}': pluginSettings['{}'] cannot be null.",
                        plugin.name
                    )));
                }
                Value::Object(members) => members,
                _ => return Err(shape()),
            };
            let mut map = BTreeMap::new();
            for setting in settings {
                match &setting.value.value {
                    Value::String(text) => {
                        map.insert(setting.name.clone(), text.clone());
                    }
                    Value::Null => {
                        return Err(SettingsError(format!(
                            "Plugin settings file '{shown}': pluginSettings['{}']['{}'] cannot \
                             be null.",
                            plugin.name, setting.name
                        )));
                    }
                    _ => return Err(shape()),
                }
            }
            by_plugin.insert(plugin.name.clone(), map);
        }
        Ok(Self { by_plugin })
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Runtime.Tests/Plugins/PluginSessionSettingsTests.cs`.

    use super::*;

    fn write(tag: &str, text: &str) -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!(
            "ac-plugin-settings-{tag}-{}.json",
            std::process::id()
        ));
        std::fs::write(&path, text).unwrap();
        path
    }

    #[test]
    fn an_unconfigured_client_answers_every_plugin_nothing() {
        let settings = PluginSessionSettings::empty();
        assert_eq!(settings.for_plugin("acdream.any"), None);
        assert_eq!(settings.plugin_ids().count(), 0);
    }

    #[test]
    fn each_plugin_reads_its_own_settings_and_nobody_elses() {
        let mut declared = BTreeMap::new();
        declared.insert(
            "acdream.alpha".to_owned(),
            BTreeMap::from([("startMacro".to_owned(), "true".to_owned())]),
        );
        declared.insert(
            "acdream.beta".to_owned(),
            BTreeMap::from([("startMacro".to_owned(), "false".to_owned())]),
        );
        let settings = PluginSessionSettings::from_map(&declared);
        // Editing the declared map afterwards changes nothing a plugin reads.
        declared.clear();
        assert_eq!(settings.get("acdream.alpha", "startMacro"), Some("true"));
        assert_eq!(settings.get("acdream.beta", "startMacro"), Some("false"));
        assert_eq!(settings.for_plugin("acdream.gamma"), None);
    }

    #[test]
    fn a_file_holding_the_map_is_read_plugin_by_plugin() {
        let path = write(
            "map",
            r#"{
              "acdream.alpha": { "startMacro": "true", "profile": "tank" },
              "acdream.beta": { "startMacro": "false" }
            }"#,
        );
        let settings = PluginSessionSettings::read_file(&path).unwrap();
        let _ = std::fs::remove_file(&path);
        assert_eq!(settings.get("acdream.alpha", "startMacro"), Some("true"));
        assert_eq!(settings.get("acdream.alpha", "profile"), Some("tank"));
        assert_eq!(settings.get("acdream.beta", "startMacro"), Some("false"));
        assert_eq!(settings.for_plugin("acdream.gamma"), None);
    }

    #[test]
    fn an_empty_map_configures_nobody() {
        let path = write("empty", "{}");
        let settings = PluginSessionSettings::read_file(&path).unwrap();
        let _ = std::fs::remove_file(&path);
        assert_eq!(settings.plugin_ids().count(), 0);
    }

    #[test]
    fn faults_name_the_file() {
        let absent = std::env::temp_dir().join("ac-plugin-settings-absent-none.json");
        let error = PluginSessionSettings::read_file(&absent).unwrap_err().0;
        assert!(error.contains(&absent.display().to_string()) && error.contains("does not exist"));

        for (tag, text, needle) in [
            ("notjson", "this is not json", "not valid JSON"),
            (
                "shape",
                r#"{ "acdream.alpha": "startMacro" }"#,
                "object mapping plugin ids",
            ),
            ("null", "null", "object mapping plugin ids"),
            (
                "hole",
                r#"{ "acdream.alpha": null }"#,
                "pluginSettings['acdream.alpha'] cannot be null.",
            ),
            (
                "value",
                r#"{ "acdream.alpha": { "startMacro": null } }"#,
                "pluginSettings['acdream.alpha']['startMacro'] cannot be null.",
            ),
        ] {
            let path = write(tag, text);
            let error = PluginSessionSettings::read_file(&path).unwrap_err().0;
            let _ = std::fs::remove_file(&path);
            assert!(error.contains(&path.display().to_string()), "{error}");
            assert!(error.contains(needle), "{error}");
        }
    }
}
