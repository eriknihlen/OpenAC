//! What plugin markup windows' controls remember per character (an
//! expander left folded, a split bar moved, a list's column widths and
//! sort): `markupControls[character][window][control][kind]`, each a
//! string (OpenAC `SettingsStore.TryLoadMarkupControlState`,
//! `SaveMarkupControlState`).

use std::collections::BTreeMap;

use super::{SettingsStore, object_member, set_member};
use crate::json::{Member, Value};

/// One remembered value's place: which window, which named control, which
/// kind of state (OpenAC `MarkupControlKey`).
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct MarkupControlKey {
    /// The window (`plugin:<id>:<window>`, or `*` for what every window
    /// shares).
    pub window: String,
    /// The control's `name`.
    pub control: String,
    /// What it remembers (`expanded`, `position`, `widths`, ...).
    pub kind: String,
}

impl MarkupControlKey {
    /// A key from its parts.
    #[must_use]
    pub fn new(window: &str, control: &str, kind: &str) -> Self {
        Self {
            window: window.to_owned(),
            control: control.to_owned(),
            kind: kind.to_owned(),
        }
    }
}

fn blank(text: &str) -> bool {
    text.trim().is_empty()
}

fn invalid(what: &str) -> std::io::Error {
    std::io::Error::new(std::io::ErrorKind::InvalidInput, what.to_owned())
}

impl SettingsStore {
    /// What character `toon`'s markup controls remember. Anything that is
    /// not an object where one is expected, or not a string value, is
    /// skipped.
    ///
    /// # Errors
    /// The file exists but cannot be read or parsed (the message says why):
    /// nothing may then be saved over it.
    ///
    /// # Panics
    /// `toon` is blank (a caller's bug).
    pub fn try_load_markup_control_state(
        &self,
        toon: &str,
    ) -> Result<BTreeMap<MarkupControlKey, String>, String> {
        assert!(!blank(toon), "a character key must not be blank");
        let mut values = BTreeMap::new();
        let Some(root) = self.read_root().map_err(|why| self.failed_markup(&why))? else {
            return Ok(values);
        };
        let windows = root
            .iter()
            .find(|m| m.name == "markupControls")
            .and_then(|m| object(&m.value.value))
            .and_then(|characters| characters.iter().find(|m| m.name == toon))
            .and_then(|m| object(&m.value.value));
        let Some(windows) = windows else {
            return Ok(values);
        };
        for window in windows {
            let Some(controls) = object(&window.value.value) else {
                continue;
            };
            for control in controls {
                let Some(kinds) = object(&control.value.value) else {
                    continue;
                };
                for kind in kinds {
                    if let Value::String(text) = &kind.value.value {
                        values.insert(
                            MarkupControlKey::new(&window.name, &control.name, &kind.name),
                            text.clone(),
                        );
                    }
                }
            }
        }
        Ok(values)
    }

    fn failed_markup(&self, why: &str) -> String {
        format!(
            "settings: failed to load markup control state from {}: {why}",
            self.path.display()
        )
    }

    /// Replaces everything character `toon`'s markup controls remember,
    /// keeping every other section and every other character.
    ///
    /// # Errors
    /// A blank character, a file that exists but cannot be read, or the
    /// write failing.
    pub fn save_markup_control_state(
        &self,
        toon: &str,
        values: &BTreeMap<MarkupControlKey, String>,
    ) -> std::io::Result<()> {
        if blank(toon) {
            return Err(invalid("markup control state needs a character"));
        }
        let mut root = self.load_mutable_root()?;
        let mut windows: Vec<Member> = Vec::new();
        for (key, value) in values {
            let controls = object_member(&mut windows, &key.window);
            let kinds = object_member(controls, &key.control);
            set_member(kinds, &key.kind, Value::String(value.clone()));
        }
        let characters = object_member(&mut root, "markupControls");
        set_member(characters, toon, Value::Object(windows));
        self.write_mutable_root(root)
    }
}

fn object(value: &Value) -> Option<&[Member]> {
    match value {
        Value::Object(members) => Some(members),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.UI.Abstractions.Tests/Panels/Settings/MarkupControlStateSettingsTests.cs`
    //! and the two save tests added to `SettingsStoreTests.cs`.

    use super::*;
    use crate::settings::UiWindowPosition;

    struct Temp(std::path::PathBuf);

    impl Temp {
        fn new(name: &str) -> Self {
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos());
            let dir = std::env::temp_dir().join(format!(
                "openac-markup-controls-{name}-{}-{nanos}",
                std::process::id()
            ));
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        fn file(&self) -> std::path::PathBuf {
            self.0.join("settings.json")
        }

        fn store(&self) -> SettingsStore {
            SettingsStore::new(self.file())
        }
    }

    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn values(entries: &[(&str, &str, &str, &str)]) -> BTreeMap<MarkupControlKey, String> {
        entries
            .iter()
            .map(|(w, c, k, v)| (MarkupControlKey::new(w, c, k), (*v).to_owned()))
            .collect()
    }

    fn load(store: &SettingsStore, toon: &str) -> BTreeMap<MarkupControlKey, String> {
        store.try_load_markup_control_state(toon).unwrap()
    }

    #[test]
    fn saved_values_load_back() {
        // SavedValuesLoadBack
        let t = Temp::new("saved");
        let store = t.store();
        let saved = values(&[
            ("nav", "adv", "expanded", "false"),
            ("nav", "mobs", "widths", "120,40"),
            ("loot", "adv", "expanded", "true"),
        ]);
        store.save_markup_control_state("alice", &saved).unwrap();
        assert_eq!(load(&store, "alice"), saved);
    }

    #[test]
    fn a_save_replaces_the_characters_set_and_keeps_everything_else() {
        // ASaveReplacesTheCharactersSetAndKeepsEverythingElse
        let t = Temp::new("replace");
        let store = t.store();
        store
            .save_window_position("alice", "chat", UiWindowPosition { x: 10.0, y: 20.0 })
            .unwrap();
        store
            .save_markup_control_state("bob", &values(&[("nav", "adv", "expanded", "true")]))
            .unwrap();
        store
            .save_markup_control_state(
                "alice",
                &values(&[("nav", "adv", "expanded", "false"), ("nav", "x", "k", "1")]),
            )
            .unwrap();
        store
            .save_markup_control_state("alice", &values(&[("nav", "adv", "expanded", "true")]))
            .unwrap();
        assert_eq!(
            load(&store, "alice"),
            values(&[("nav", "adv", "expanded", "true")])
        );
        assert_eq!(
            load(&store, "bob"),
            values(&[("nav", "adv", "expanded", "true")])
        );
        assert_eq!(
            store.load_window_position("alice", "chat"),
            Some(UiWindowPosition { x: 10.0, y: 20.0 })
        );
    }

    #[test]
    fn other_sections_saves_keep_the_markup_controls() {
        // OtherSectionsSavesKeepTheMarkupControls
        let t = Temp::new("others");
        let store = t.store();
        store
            .save_markup_control_state("alice", &values(&[("nav", "adv", "expanded", "false")]))
            .unwrap();
        store
            .save_window_position("alice", "chat", UiWindowPosition { x: 1.0, y: 2.0 })
            .unwrap();
        store
            .save_display(&crate::settings::DisplaySettings::default())
            .unwrap();
        assert_eq!(
            load(&store, "alice"),
            values(&[("nav", "adv", "expanded", "false")])
        );
    }

    #[test]
    fn a_missing_file_loads_empty() {
        // AMissingFileLoadsEmpty
        let t = Temp::new("missing");
        assert!(load(&t.store(), "alice").is_empty());
    }

    #[test]
    fn malformed_entries_are_skipped() {
        // MalformedEntriesAreSkipped
        let t = Temp::new("malformed");
        std::fs::write(
            t.file(),
            r#"{"markupControls":{"alice":{
              "nav":{"adv":{"expanded":5,"open":"yes"},"bad":3},
              "loot":7
            },"bob":"nope"}}"#,
        )
        .unwrap();
        let store = t.store();
        assert_eq!(
            load(&store, "alice"),
            values(&[("nav", "adv", "open", "yes")])
        );
        assert!(load(&store, "bob").is_empty());
    }

    #[test]
    fn a_non_object_section_loads_empty() {
        // ANonObjectSectionLoadsEmpty
        let t = Temp::new("nonobject");
        std::fs::write(t.file(), r#"{"markupControls":42}"#).unwrap();
        assert!(load(&t.store(), "alice").is_empty());
    }

    #[test]
    fn a_blank_character_key_is_refused() {
        // ABlankCharacterKeyThrows (the save; a blank load is a caller's bug)
        let t = Temp::new("blank");
        for key in ["", "  "] {
            assert!(
                t.store()
                    .save_markup_control_state(key, &BTreeMap::new())
                    .is_err()
            );
        }
    }

    #[test]
    fn an_unreadable_file_reports_failure() {
        // AnUnreadableFileReportsFailure
        let t = Temp::new("unreadable");
        std::fs::write(t.file(), "{ not json").unwrap();
        assert!(t.store().try_load_markup_control_state("alice").is_err());
    }

    #[test]
    fn a_save_that_cannot_read_the_file_fails_and_leaves_it_alone() {
        // SettingsStoreTests.A_save_that_cannot_read_the_file_fails_and_leaves_it_alone:
        // a directory where the file should be cannot be read as a file.
        let t = Temp::new("cannotread");
        std::fs::create_dir_all(t.file()).unwrap();
        let store = t.store();
        assert!(
            store
                .save_markup_control_state("alice", &values(&[("nav", "adv", "expanded", "true")]))
                .is_err()
        );
        assert!(
            store
                .save_display(&crate::settings::DisplaySettings::default())
                .is_err()
        );
        assert!(t.file().is_dir());
    }

    #[test]
    fn saves_leave_no_temporary_file_beside_the_settings() {
        // SettingsStoreTests.Saves_leave_no_temporary_file_beside_the_settings
        let t = Temp::new("notemp");
        let store = t.store();
        store
            .save_window_position("alice", "chat", UiWindowPosition { x: 10.0, y: 20.0 })
            .unwrap();
        store
            .save_display(&crate::settings::DisplaySettings::default())
            .unwrap();
        let leftovers: Vec<_> = std::fs::read_dir(&t.0)
            .unwrap()
            .filter_map(Result::ok)
            .filter(|e| e.file_name().to_string_lossy().ends_with(".tmp"))
            .collect();
        assert!(leftovers.is_empty());
        assert_eq!(
            store.load_window_position("alice", "chat"),
            Some(UiWindowPosition { x: 10.0, y: 20.0 })
        );
    }
}
