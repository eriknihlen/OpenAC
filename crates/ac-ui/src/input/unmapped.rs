//! Bindings the player gave retail actions this client has no action for
//! (kept so the key configuration screen can show and save them), stored
//! in their own file by (input map, action id).

use std::collections::BTreeMap;
use std::path::Path;

use ac_host::json::{self, Value};

use super::bindings::KeyBindingsError;
use super::chord::{InputKey, KeyChord, modifiers_name, parse_modifiers};

/// The unmapped bindings.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RetailUnmappedKeyBindings {
    bindings: BTreeMap<(u32, u32), Vec<KeyChord>>,
}

impl RetailUnmappedKeyBindings {
    /// None.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The chords of a retail action (none when it has none).
    #[must_use]
    pub fn get(&self, input_map: u32, action_id: u32) -> &[KeyChord] {
        self.bindings
            .get(&(input_map, action_id))
            .map_or(&[], Vec::as_slice)
    }

    /// Set a retail action's chords (no chords removes it).
    pub fn set(&mut self, input_map: u32, action_id: u32, chords: &[KeyChord]) {
        if chords.is_empty() {
            self.bindings.remove(&(input_map, action_id));
        } else {
            self.bindings
                .insert((input_map, action_id), chords.to_vec());
        }
    }

    /// The bindings in `path` (none when the file is missing); a file that
    /// cannot be read or parsed gives none, with the reason.
    #[must_use]
    pub fn load_or_empty(path: &Path) -> (Self, Option<KeyBindingsError>) {
        let bytes = match std::fs::read(path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return (Self::new(), None);
            }
            Err(error) => return (Self::new(), Some(error.into())),
        };
        match Self::parse(&bytes) {
            Ok(result) => (result, None),
            Err(error) => (Self::new(), Some(error)),
        }
    }

    /// The bindings a document holds: `rows` of `inputMap`, `action` (hex
    /// text) and `chords`; rows missing a member are skipped, as are chords
    /// naming an unknown key.
    ///
    /// # Errors
    /// The document is not JSON, or an id is not hexadecimal text.
    pub fn parse(bytes: &[u8]) -> Result<Self, KeyBindingsError> {
        let mut result = Self::new();
        let root = json::parse(bytes)?;
        let members = json::object_members(&root)?;
        let Some(rows) = members.iter().find(|m| m.name == "rows").map(|m| &m.value) else {
            return Ok(result);
        };
        let Value::Array(rows) = &rows.value else {
            return Ok(result);
        };
        for row in rows {
            let fields = json::object_members(row)?;
            let field = |name: &str| fields.iter().find(|m| m.name == name).map(|m| &m.value);
            let (Some(map), Some(action), Some(chords)) =
                (field("inputMap"), field("action"), field("chords"))
            else {
                continue;
            };
            let Value::Array(chords) = &chords.value else {
                continue;
            };
            let hex = |node| -> Result<u32, KeyBindingsError> {
                let text = json::string_or_null(node)?.unwrap_or_default();
                let digits = text
                    .strip_prefix("0x")
                    .or_else(|| text.strip_prefix("0X"))
                    .unwrap_or(&text);
                u32::from_str_radix(digits, 16).map_err(|_| {
                    KeyBindingsError::Json(json::JsonError {
                        kind: json::JsonErrorKind::OutOfRange,
                        at: node.at,
                    })
                })
            };
            let key = (hex(map)?, hex(action)?);
            let mut list = Vec::new();
            for chord in chords {
                let fields = json::object_members(chord)?;
                let field = |name: &str| fields.iter().find(|m| m.name == name).map(|m| &m.value);
                let Some(key_node) = field("key") else {
                    continue;
                };
                let Some(name) = json::string_or_null(key_node)? else {
                    continue;
                };
                let Some(input_key) = InputKey::from_name(&name) else {
                    continue;
                };
                let modifiers = match field("mod").map(|n| &n.value) {
                    Some(Value::String(text)) => parse_modifiers(text),
                    _ => crate::event::Modifiers::NONE,
                };
                let device = match field("device") {
                    Some(node) if matches!(node.value, Value::Number(_)) => {
                        json::int32(node)? as u8
                    }
                    _ => 0,
                };
                list.push(KeyChord {
                    key: input_key,
                    modifiers,
                    device,
                });
            }
            result.bindings.insert(key, list);
        }
        Ok(result)
    }

    /// The document.
    #[must_use]
    pub fn to_json(&self) -> String {
        let quoted = |text: &str| {
            let mut out = String::new();
            json::write_string(&mut out, text);
            out
        };
        let mut rows = Vec::new();
        for (&(map, action), chords) in &self.bindings {
            let chords: Vec<String> = chords
                .iter()
                .map(|c| {
                    let mut fields = Vec::new();
                    if c.device != 0 {
                        fields.push(format!("\"device\": {}", c.device));
                    }
                    fields.push(format!("\"key\": {}", quoted(&c.key.name())));
                    if c.modifiers.0 != 0 {
                        fields.push(format!("\"mod\": {}", quoted(&modifiers_name(c.modifiers))));
                    }
                    format!("{{ {} }}", fields.join(", "))
                })
                .collect();
            rows.push(format!(
                "    {{ \"action\": \"0x{action:X}\", \"chords\": [ {} ], \"inputMap\": \"0x{map:X}\" }}",
                chords.join(", ")
            ));
        }
        format!(
            "{{\n  \"version\": 1,\n  \"rows\": [\n{}\n  ]\n}}\n",
            rows.join(",\n")
        )
    }

    /// Write the document to `path` (creating its directory).
    ///
    /// # Errors
    /// The directory or file could not be written.
    pub fn save_to_file(&self, path: &Path) -> Result<(), KeyBindingsError> {
        if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::write(path, self.to_json())?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::event::{Key, Modifiers};
    use crate::input::chord::mouse;

    #[test]
    fn set_get_and_round_trip() {
        let mut u = RetailUnmappedKeyBindings::new();
        let chords = [
            KeyChord::key(Key::F13, Modifiers::SHIFT | Modifiers::CTRL),
            KeyChord::mouse(mouse::BUTTON4, Modifiers::NONE),
        ];
        u.set(0x0000_000E, 0x7E, &chords);
        assert_eq!(u.get(0x0E, 0x7E), chords);
        assert!(u.get(0x0E, 0x7F).is_empty());
        let back = RetailUnmappedKeyBindings::parse(u.to_json().as_bytes()).unwrap();
        assert_eq!(back, u);
        u.set(0x0E, 0x7E, &[]);
        assert!(u.get(0x0E, 0x7E).is_empty());
    }

    #[test]
    fn a_missing_or_corrupt_file_is_empty() {
        let path = std::env::temp_dir().join(format!(
            "openac_unmapped_{}_{}.json",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let (u, error) = RetailUnmappedKeyBindings::load_or_empty(&path);
        assert!(error.is_none() && u == RetailUnmappedKeyBindings::new());
        std::fs::write(&path, "{ nope").unwrap();
        let (u, error) = RetailUnmappedKeyBindings::load_or_empty(&path);
        assert!(error.is_some() && u == RetailUnmappedKeyBindings::new());
        let _ = std::fs::remove_file(&path);
    }
}
