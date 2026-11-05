//! The player's plugin hotkey overrides on disk (OpenAC
//! `AppHotkeyRegistry.LoadOverrides`, `SaveOverrides`): `plugin-hotkeys.json`
//! beside the key bindings file, one member per scoped hotkey id,
//! `{"Key": "J", "Ctrl": true, "Alt": false, "Shift": false}`. It is a
//! sibling of the client's own bindings file rather than part of it, so a
//! plugin can never corrupt the client's binding schema.

use std::collections::BTreeMap;
use std::path::Path;

use ac_host::json::{self, Member, Node, Value};
use ac_plugin::abi::keys::PluginKey;
use ac_plugin_ui::hotkeys::Chord;

/// The file's name, beside `keybinds.json`.
pub const FILE_NAME: &str = "plugin-hotkeys.json";

/// The stored overrides; none when the file is missing or unreadable, as
/// OpenAC reads it (a bad file binds every hotkey to its plugin's default).
#[must_use]
pub fn load(path: &Path) -> BTreeMap<String, Chord> {
    std::fs::read(path)
        .ok()
        .and_then(|bytes| parse(&bytes))
        .unwrap_or_default()
}

/// Parses the document; `None` when it is not the overrides object. A key
/// name the client does not know is skipped (OpenAC `Enum.TryParse`, which
/// also takes a number).
#[must_use]
pub fn parse(bytes: &[u8]) -> Option<BTreeMap<String, Chord>> {
    let root = json::parse(bytes).ok()?;
    let Value::Object(members) = &root.value else {
        return None;
    };
    let mut overrides = BTreeMap::new();
    for member in members {
        let Value::Object(fields) = &member.value.value else {
            return None;
        };
        let mut key = None;
        let mut chord = Chord::default();
        for field in fields {
            match (field.name.as_str(), &field.value.value) {
                ("Key", Value::String(name)) => key = Some(name.clone()),
                ("Key", Value::Null) => {}
                ("Ctrl", Value::Bool(on)) => chord.ctrl = *on,
                ("Alt", Value::Bool(on)) => chord.alt = *on,
                ("Shift", Value::Bool(on)) => chord.shift = *on,
                ("Key" | "Ctrl" | "Alt" | "Shift", _) => return None,
                _ => {}
            }
        }
        let Some(name) = key else {
            continue;
        };
        let parsed = PluginKey::from_name(&name)
            .map(PluginKey::code)
            .or_else(|| {
                name.trim()
                    .parse::<i64>()
                    .ok()
                    .map(|n| u32::try_from(n).unwrap_or(0))
            });
        if let Some(key) = parsed {
            chord.key = key;
            overrides.insert(member.name.clone(), chord);
        }
    }
    Some(overrides)
}

/// A key's stored name: OpenAC's enum name, or the number of one the
/// enum does not name (as `ToString` writes it).
fn key_name(code: u32) -> String {
    let key = PluginKey::from_code(code);
    if key.code() == code {
        key.name().to_owned()
    } else {
        code.to_string()
    }
}

/// The document for `overrides`, indented.
#[must_use]
pub fn render(overrides: &BTreeMap<String, Chord>) -> String {
    let members = overrides
        .iter()
        .map(|(id, chord)| {
            Member::made(
                id,
                Value::Object(vec![
                    Member::made("Key", Value::String(key_name(chord.key))),
                    Member::made("Ctrl", Value::Bool(chord.ctrl)),
                    Member::made("Alt", Value::Bool(chord.alt)),
                    Member::made("Shift", Value::Bool(chord.shift)),
                ]),
            )
        })
        .collect();
    let mut out = String::new();
    json::write_value(&mut out, &Node::made(Value::Object(members)), 0);
    out
}

/// Writes `overrides` over what the file holds (another session of this
/// process may have stored its own since this one read it), creating the
/// folder.
///
/// # Errors
/// The folder or the file cannot be written.
pub fn save(path: &Path, overrides: &BTreeMap<String, Chord>) -> std::io::Result<()> {
    let mut merged = load(path);
    merged.extend(overrides.iter().map(|(k, v)| (k.clone(), *v)));
    if let Some(folder) = path.parent()
        && !folder.as_os_str().is_empty()
    {
        std::fs::create_dir_all(folder)?;
    }
    std::fs::write(path, render(&merged))
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `AppHotkeyRegistryTests.RebindPersistsAcrossARegistryRestart`:
    // what is written reads back as the same chords.
    #[test]
    fn overrides_round_trip_through_the_file() {
        let folder = std::env::temp_dir().join(format!(
            "openac-plugin-hotkeys-{}-{}",
            std::process::id(),
            line!()
        ));
        let path = folder.join(FILE_NAME);
        let quick = Chord::key(PluginKey::J.code()).with_ctrl();
        let other = Chord {
            key: PluginKey::NumpadEnter.code(),
            ctrl: false,
            alt: true,
            shift: true,
        };
        let mut overrides = BTreeMap::new();
        overrides.insert("edwards.tank:quick-heal".to_owned(), quick);
        save(&path, &overrides).unwrap();
        // Another session's override is kept when this one writes again.
        let mut second = BTreeMap::new();
        second.insert("other.plugin:go".to_owned(), other);
        save(&path, &second).unwrap();
        let back = load(&path);
        assert_eq!(back.get("edwards.tank:quick-heal"), Some(&quick));
        assert_eq!(back.get("other.plugin:go"), Some(&other));
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("\"Key\": \"J\""), "{text}");
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn a_missing_or_bad_file_reads_as_no_overrides() {
        assert!(load(Path::new("no/such/plugin-hotkeys.json")).is_empty());
        assert_eq!(parse(b"[1, 2]"), None);
        assert_eq!(parse(br#"{"a:b": {"Key": 5, "Ctrl": true}}"#), None);
        // An unknown name is skipped; a number is taken as OpenAC's
        // `Enum.TryParse` takes it.
        let read =
            parse(br#"{"a:b": {"Key": "Nope"}, "a:c": {"Key": "10", "Shift": true}}"#).unwrap();
        assert_eq!(read.len(), 1);
        assert_eq!(
            read.get("a:c"),
            Some(&Chord {
                key: PluginKey::J.code(),
                shift: true,
                ..Chord::default()
            })
        );
    }
}
