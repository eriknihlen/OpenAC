//! A chord's name as the key configuration screen shows it: the modifier
//! names (Shift, Ctrl, Alt, Win in that order) and the key's name joined
//! by the authored delimiter. A key is named from the dat's key-name table
//! by its DirectInput name, else by the operating system's localized name
//! for its scan code, else by the DirectInput name without `DIK_`; a mouse
//! button from the same table by its `DIMOFS_BUTTON` name, else
//! "Mouse Button n".

use crate::event::{Key, Modifiers};
use crate::strings::compute_hash;

use super::chord::{InputKey, KeyChord, key_name, modifiers_name};

/// The string table naming keys.
pub const KEY_NAME_TABLE_ID: u32 = 0x2300_000A;
/// The string table naming the modifier keys.
pub const META_KEY_NAME_TABLE_ID: u32 = 0x2300_000B;
/// The string table holding the delimiter.
pub const DELIMITER_TABLE_ID: u32 = 0x2300_0007;

/// A string-table lookup: (table, string hash) → text.
pub type ResolveString = dyn Fn(u32, u32) -> Option<String>;
/// The operating system's name for a scan code (the low seven bits, and
/// whether it is an extended key).
pub type OsKeyName = dyn Fn(u8, bool) -> Option<String>;

/// See the module documentation.
pub struct RetailKeyNames {
    resolve: Box<ResolveString>,
    os_key_name: Option<Box<OsKeyName>>,
    delimiter: String,
}

impl std::fmt::Debug for RetailKeyNames {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RetailKeyNames")
            .field("delimiter", &self.delimiter)
            .finish_non_exhaustive()
    }
}

/// The modifiers in the order their names are put before the key, with
/// the key each is named after.
const META_ORDER: [(Modifiers, Key); 4] = [
    (Modifiers::SHIFT, Key::ShiftLeft),
    (Modifiers::CTRL, Key::ControlLeft),
    (Modifiers::ALT, Key::AltLeft),
    (Modifiers::WIN, Key::SuperLeft),
];

impl RetailKeyNames {
    /// Names from `resolve`, falling back to `os_key_name` (none: straight
    /// to the DirectInput spelling).
    #[must_use]
    pub fn new(resolve: Box<ResolveString>, os_key_name: Option<Box<OsKeyName>>) -> Self {
        let delimiter = resolve(DELIMITER_TABLE_ID, compute_hash("ID_KeyDescDelimiter"))
            .unwrap_or_else(|| "+".to_owned());
        Self {
            resolve,
            os_key_name,
            delimiter,
        }
    }

    /// The name of `chord` (empty for the empty chord).
    #[must_use]
    pub fn describe(&self, chord: KeyChord) -> String {
        if is_empty(chord) {
            return String::new();
        }
        if let InputKey::Mouse(button) = chord.key {
            if chord.device == 1 && button <= 4 {
                let semantic = format!("DIMOFS_BUTTON{button}");
                let name = (self.resolve)(KEY_NAME_TABLE_ID, compute_hash(&semantic))
                    .unwrap_or_else(|| format!("Mouse Button {}", button + 1));
                return self.compose(chord, &name);
            }
            return fallback_spelling(chord);
        }
        let InputKey::Key(key) = chord.key else {
            return fallback_spelling(chord);
        };
        let Some((dik, dik_name)) = dik(key) else {
            return fallback_spelling(chord);
        };
        let name = self.lookup(dik_name, dik, KEY_NAME_TABLE_ID);
        self.compose(chord, &name)
    }

    fn compose(&self, chord: KeyChord, key_name: &str) -> String {
        let mut composed = String::new();
        for (flag, meta_key) in META_ORDER {
            if !chord.modifiers.contains(flag) || is_self_modifier(chord.key, flag) {
                continue;
            }
            let Some((meta_dik, meta_name)) = dik(meta_key) else {
                continue;
            };
            composed.push_str(&self.lookup(meta_name, meta_dik, META_KEY_NAME_TABLE_ID));
            composed.push_str(&self.delimiter);
        }
        composed.push_str(key_name);
        composed
    }

    fn lookup(&self, dik_name: &str, dik: u8, table: u32) -> String {
        (self.resolve)(table, compute_hash(dik_name))
            .or_else(|| {
                self.os_key_name
                    .as_ref()
                    .and_then(|os| os(dik & 0x7F, dik & 0x80 != 0))
            })
            .unwrap_or_else(|| dik_name["DIK_".len()..].to_owned())
    }
}

/// The empty chord (no key): what an unbound slot holds.
#[must_use]
pub fn is_empty(chord: KeyChord) -> bool {
    chord.key == InputKey::Key(Key::Unknown) && chord.modifiers.0 == 0 && chord.device == 0
}

/// The empty chord.
#[must_use]
pub fn empty_chord() -> KeyChord {
    KeyChord::key(Key::Unknown, Modifiers::NONE)
}

fn fallback_spelling(chord: KeyChord) -> String {
    let key = match chord.key {
        InputKey::Key(k) => key_name(k).to_owned(),
        other @ InputKey::Mouse(_) => other.name(),
    };
    if chord.modifiers.0 == 0 {
        key
    } else {
        format!("{}+{key}", modifiers_name(chord.modifiers))
    }
}

fn is_self_modifier(key: InputKey, flag: Modifiers) -> bool {
    let InputKey::Key(key) = key else {
        return false;
    };
    match flag {
        f if f == Modifiers::SHIFT => matches!(key, Key::ShiftLeft | Key::ShiftRight),
        f if f == Modifiers::CTRL => matches!(key, Key::ControlLeft | Key::ControlRight),
        f if f == Modifiers::ALT => matches!(key, Key::AltLeft | Key::AltRight),
        f if f == Modifiers::WIN => matches!(key, Key::SuperLeft | Key::SuperRight),
        _ => false,
    }
}

/// A key's DirectInput scan code and name.
#[must_use]
pub fn dik(key: Key) -> Option<(u8, &'static str)> {
    Some(match key {
        Key::Escape => (0x01, "DIK_ESCAPE"),
        Key::Number1 => (0x02, "DIK_1"),
        Key::Number2 => (0x03, "DIK_2"),
        Key::Number3 => (0x04, "DIK_3"),
        Key::Number4 => (0x05, "DIK_4"),
        Key::Number5 => (0x06, "DIK_5"),
        Key::Number6 => (0x07, "DIK_6"),
        Key::Number7 => (0x08, "DIK_7"),
        Key::Number8 => (0x09, "DIK_8"),
        Key::Number9 => (0x0A, "DIK_9"),
        Key::Number0 => (0x0B, "DIK_0"),
        Key::Minus => (0x0C, "DIK_MINUS"),
        Key::Equal => (0x0D, "DIK_EQUALS"),
        Key::Backspace => (0x0E, "DIK_BACK"),
        Key::Tab => (0x0F, "DIK_TAB"),
        Key::Q => (0x10, "DIK_Q"),
        Key::W => (0x11, "DIK_W"),
        Key::E => (0x12, "DIK_E"),
        Key::R => (0x13, "DIK_R"),
        Key::T => (0x14, "DIK_T"),
        Key::Y => (0x15, "DIK_Y"),
        Key::U => (0x16, "DIK_U"),
        Key::I => (0x17, "DIK_I"),
        Key::O => (0x18, "DIK_O"),
        Key::P => (0x19, "DIK_P"),
        Key::LeftBracket => (0x1A, "DIK_LBRACKET"),
        Key::RightBracket => (0x1B, "DIK_RBRACKET"),
        Key::Enter => (0x1C, "DIK_RETURN"),
        Key::ControlLeft => (0x1D, "DIK_LCONTROL"),
        Key::A => (0x1E, "DIK_A"),
        Key::S => (0x1F, "DIK_S"),
        Key::D => (0x20, "DIK_D"),
        Key::F => (0x21, "DIK_F"),
        Key::G => (0x22, "DIK_G"),
        Key::H => (0x23, "DIK_H"),
        Key::J => (0x24, "DIK_J"),
        Key::K => (0x25, "DIK_K"),
        Key::L => (0x26, "DIK_L"),
        Key::Semicolon => (0x27, "DIK_SEMICOLON"),
        Key::Apostrophe => (0x28, "DIK_APOSTROPHE"),
        Key::GraveAccent => (0x29, "DIK_GRAVE"),
        Key::ShiftLeft => (0x2A, "DIK_LSHIFT"),
        Key::BackSlash => (0x2B, "DIK_BACKSLASH"),
        Key::Z => (0x2C, "DIK_Z"),
        Key::X => (0x2D, "DIK_X"),
        Key::C => (0x2E, "DIK_C"),
        Key::V => (0x2F, "DIK_V"),
        Key::B => (0x30, "DIK_B"),
        Key::N => (0x31, "DIK_N"),
        Key::M => (0x32, "DIK_M"),
        Key::Comma => (0x33, "DIK_COMMA"),
        Key::Period => (0x34, "DIK_PERIOD"),
        Key::Slash => (0x35, "DIK_SLASH"),
        Key::ShiftRight => (0x36, "DIK_RSHIFT"),
        Key::KeypadMultiply => (0x37, "DIK_MULTIPLY"),
        Key::AltLeft => (0x38, "DIK_LMENU"),
        Key::Space => (0x39, "DIK_SPACE"),
        Key::CapsLock => (0x3A, "DIK_CAPITAL"),
        Key::F1 => (0x3B, "DIK_F1"),
        Key::F2 => (0x3C, "DIK_F2"),
        Key::F3 => (0x3D, "DIK_F3"),
        Key::F4 => (0x3E, "DIK_F4"),
        Key::F5 => (0x3F, "DIK_F5"),
        Key::F6 => (0x40, "DIK_F6"),
        Key::F7 => (0x41, "DIK_F7"),
        Key::F8 => (0x42, "DIK_F8"),
        Key::F9 => (0x43, "DIK_F9"),
        Key::F10 => (0x44, "DIK_F10"),
        Key::NumLock => (0x45, "DIK_NUMLOCK"),
        Key::ScrollLock => (0x46, "DIK_SCROLL"),
        Key::Keypad7 => (0x47, "DIK_NUMPAD7"),
        Key::Keypad8 => (0x48, "DIK_NUMPAD8"),
        Key::Keypad9 => (0x49, "DIK_NUMPAD9"),
        Key::KeypadSubtract => (0x4A, "DIK_SUBTRACT"),
        Key::Keypad4 => (0x4B, "DIK_NUMPAD4"),
        Key::Keypad5 => (0x4C, "DIK_NUMPAD5"),
        Key::Keypad6 => (0x4D, "DIK_NUMPAD6"),
        Key::KeypadAdd => (0x4E, "DIK_ADD"),
        Key::Keypad1 => (0x4F, "DIK_NUMPAD1"),
        Key::Keypad2 => (0x50, "DIK_NUMPAD2"),
        Key::Keypad3 => (0x51, "DIK_NUMPAD3"),
        Key::Keypad0 => (0x52, "DIK_NUMPAD0"),
        Key::KeypadDecimal => (0x53, "DIK_DECIMAL"),
        Key::F11 => (0x57, "DIK_F11"),
        Key::F12 => (0x58, "DIK_F12"),
        Key::F13 => (0x64, "DIK_F13"),
        Key::F14 => (0x65, "DIK_F14"),
        Key::F15 => (0x66, "DIK_F15"),
        Key::KeypadEnter => (0x9C, "DIK_NUMPADENTER"),
        Key::ControlRight => (0x9D, "DIK_RCONTROL"),
        Key::KeypadDivide => (0xB5, "DIK_DIVIDE"),
        Key::PrintScreen => (0xB7, "DIK_SYSRQ"),
        Key::AltRight => (0xB8, "DIK_RMENU"),
        Key::Pause => (0xC5, "DIK_PAUSE"),
        Key::Home => (0xC7, "DIK_HOME"),
        Key::Up => (0xC8, "DIK_UP"),
        Key::PageUp => (0xC9, "DIK_PRIOR"),
        Key::Left => (0xCB, "DIK_LEFT"),
        Key::Right => (0xCD, "DIK_RIGHT"),
        Key::End => (0xCF, "DIK_END"),
        Key::Down => (0xD0, "DIK_DOWN"),
        Key::PageDown => (0xD1, "DIK_NEXT"),
        Key::Insert => (0xD2, "DIK_INSERT"),
        Key::Delete => (0xD3, "DIK_DELETE"),
        Key::SuperLeft => (0xDB, "DIK_LWIN"),
        Key::SuperRight => (0xDC, "DIK_RWIN"),
        Key::Menu => (0xDD, "DIK_APPS"),
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    //! Golden values: OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailKeyNamesTests.cs`.

    use super::*;
    use crate::input::chord::mouse;
    use std::cell::Cell;
    use std::rc::Rc;

    fn no_strings() -> Box<ResolveString> {
        Box::new(|_, _| None)
    }

    fn table(entries: &'static [(u32, &'static str, &'static str)]) -> Box<ResolveString> {
        Box::new(move |t, hash| {
            entries
                .iter()
                .find(|(table, key, _)| *table == t && compute_hash(key) == hash)
                .map(|(_, _, value)| (*value).to_owned())
        })
    }

    fn os(f: impl Fn(u8, bool) -> Option<&'static str> + 'static) -> Option<Box<OsKeyName>> {
        Some(Box::new(move |scan, ext| f(scan, ext).map(str::to_owned)))
    }

    fn chord(key: Key, modifiers: Modifiers) -> KeyChord {
        KeyChord::key(key, modifiers)
    }

    #[test]
    fn the_dat_table_wins_over_the_os_name() {
        let names = RetailKeyNames::new(
            table(&[(KEY_NAME_TABLE_ID, "DIK_LCONTROL", "Left Ctrl")]),
            os(|_, _| panic!("the OS lookup must not run")),
        );
        assert_eq!(
            names.describe(chord(Key::ControlLeft, Modifiers::NONE)),
            "Left Ctrl"
        );
    }

    #[test]
    fn the_os_localized_name_is_used_when_the_dat_table_misses() {
        let names = RetailKeyNames::new(
            no_strings(),
            os(|scan, ext| (scan == 0x2A && !ext).then_some("SKIFT")),
        );
        assert_eq!(
            names.describe(chord(Key::ShiftLeft, Modifiers::NONE)),
            "SKIFT"
        );
    }

    #[test]
    fn a_self_modifier_shows_only_the_key_name() {
        let names = RetailKeyNames::new(
            no_strings(),
            os(|scan, _| (scan == 0x2A).then_some("SKIFT")),
        );
        assert_eq!(
            names.describe(chord(Key::ShiftLeft, Modifiers::SHIFT)),
            "SKIFT"
        );
        assert_eq!(
            names.describe(chord(Key::ShiftRight, Modifiers::SHIFT)),
            "RSHIFT"
        );
    }

    #[test]
    fn modifier_prefixes_join_with_the_authored_delimiter_in_meta_order() {
        let names = RetailKeyNames::new(
            table(&[(DELIMITER_TABLE_ID, "ID_KeyDescDelimiter", "+")]),
            os(|scan, _| match scan {
                0x2A => Some("SKIFT"),
                0x1D => Some("CTRL"),
                0x38 => Some("ALT"),
                0x32 => Some("M"),
                _ => None,
            }),
        );
        assert_eq!(
            names.describe(chord(
                Key::M,
                Modifiers::SHIFT | Modifiers::CTRL | Modifiers::ALT
            )),
            "SKIFT+CTRL+ALT+M"
        );
    }

    #[test]
    fn the_meta_table_wins_for_the_modifier_prefix() {
        let names = RetailKeyNames::new(
            table(&[
                (DELIMITER_TABLE_ID, "ID_KeyDescDelimiter", "+"),
                (META_KEY_NAME_TABLE_ID, "DIK_LSHIFT", "Shift"),
            ]),
            os(|scan, _| (scan == 0x32).then_some("M")),
        );
        assert_eq!(names.describe(chord(Key::M, Modifiers::SHIFT)), "Shift+M");
    }

    #[test]
    fn extended_keys_pass_the_extended_flag_to_the_os_lookup() {
        // DIK_UP = 0xC8: scan 0x48 and the extended bit.
        let seen = Rc::new(Cell::new(None));
        let seen_in = seen.clone();
        let names = RetailKeyNames::new(
            no_strings(),
            Some(Box::new(move |scan, ext| {
                seen_in.set(Some((scan, ext)));
                Some("UP ARROW".to_owned())
            })),
        );
        assert_eq!(names.describe(chord(Key::Up, Modifiers::NONE)), "UP ARROW");
        assert_eq!(seen.get(), Some((0x48, true)));
    }

    #[test]
    fn the_dik_suffix_is_the_spelling_when_dat_and_os_miss() {
        let names = RetailKeyNames::new(no_strings(), os(|_, _| None));
        assert_eq!(names.describe(chord(Key::W, Modifiers::NONE)), "W");
        assert_eq!(
            names.describe(chord(Key::KeypadEnter, Modifiers::NONE)),
            "NUMPADENTER"
        );
    }

    #[test]
    fn keymap_interchange_controls_use_their_retail_dik_names() {
        let names = RetailKeyNames::new(no_strings(), os(|_, _| None));
        assert_eq!(names.describe(chord(Key::F13, Modifiers::NONE)), "F13");
        assert_eq!(
            names.describe(chord(Key::F13, Modifiers::SHIFT)),
            "LSHIFT+F13"
        );
        assert_eq!(names.describe(chord(Key::F13, Modifiers::WIN)), "LWIN+F13");
    }

    #[test]
    fn a_mouse_button_uses_the_semantic_table_then_a_readable_fallback() {
        let authored = RetailKeyNames::new(
            table(&[(KEY_NAME_TABLE_ID, "DIMOFS_BUTTON0", "Primary Mouse")]),
            os(|_, _| None),
        );
        let fallback = RetailKeyNames::new(no_strings(), os(|_, _| None));
        assert_eq!(
            authored.describe(KeyChord::mouse(mouse::LEFT, Modifiers::NONE)),
            "Primary Mouse"
        );
        assert_eq!(
            fallback.describe(KeyChord::mouse(mouse::RIGHT, Modifiers::CTRL)),
            "LCONTROL+Mouse Button 2"
        );
    }

    #[test]
    fn the_empty_chord_describes_as_empty() {
        let names = RetailKeyNames::new(no_strings(), os(|_, _| None));
        assert_eq!(names.describe(empty_chord()), "");
    }
}
