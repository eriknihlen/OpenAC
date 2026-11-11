//! The page's keys as the UI names them. A page names a physical key by
//! its `KeyboardEvent.code` ("KeyW", "Digit1", "NumpadEnter", ...), the
//! same names as the native window's key codes, and each maps to the UI key
//! the native window maps the code of that name to.

use ac_ui::event::{Key, Modifiers as UiModifiers};

use crate::host::Modifiers;

/// Every key code the UI has a name for, with that name.
const CODES: &[(&str, Key)] = &[
    ("Space", Key::Space),
    ("Quote", Key::Apostrophe),
    ("Comma", Key::Comma),
    ("Minus", Key::Minus),
    ("Period", Key::Period),
    ("Slash", Key::Slash),
    ("Digit0", Key::Number0),
    ("Digit1", Key::Number1),
    ("Digit2", Key::Number2),
    ("Digit3", Key::Number3),
    ("Digit4", Key::Number4),
    ("Digit5", Key::Number5),
    ("Digit6", Key::Number6),
    ("Digit7", Key::Number7),
    ("Digit8", Key::Number8),
    ("Digit9", Key::Number9),
    ("Semicolon", Key::Semicolon),
    ("Equal", Key::Equal),
    ("KeyA", Key::A),
    ("KeyB", Key::B),
    ("KeyC", Key::C),
    ("KeyD", Key::D),
    ("KeyE", Key::E),
    ("KeyF", Key::F),
    ("KeyG", Key::G),
    ("KeyH", Key::H),
    ("KeyI", Key::I),
    ("KeyJ", Key::J),
    ("KeyK", Key::K),
    ("KeyL", Key::L),
    ("KeyM", Key::M),
    ("KeyN", Key::N),
    ("KeyO", Key::O),
    ("KeyP", Key::P),
    ("KeyQ", Key::Q),
    ("KeyR", Key::R),
    ("KeyS", Key::S),
    ("KeyT", Key::T),
    ("KeyU", Key::U),
    ("KeyV", Key::V),
    ("KeyW", Key::W),
    ("KeyX", Key::X),
    ("KeyY", Key::Y),
    ("KeyZ", Key::Z),
    ("BracketLeft", Key::LeftBracket),
    ("Backslash", Key::BackSlash),
    ("BracketRight", Key::RightBracket),
    ("Backquote", Key::GraveAccent),
    ("Escape", Key::Escape),
    ("Enter", Key::Enter),
    ("Tab", Key::Tab),
    ("Backspace", Key::Backspace),
    ("Insert", Key::Insert),
    ("Delete", Key::Delete),
    ("ArrowRight", Key::Right),
    ("ArrowLeft", Key::Left),
    ("ArrowDown", Key::Down),
    ("ArrowUp", Key::Up),
    ("PageUp", Key::PageUp),
    ("PageDown", Key::PageDown),
    ("Home", Key::Home),
    ("End", Key::End),
    ("CapsLock", Key::CapsLock),
    ("ScrollLock", Key::ScrollLock),
    ("NumLock", Key::NumLock),
    ("PrintScreen", Key::PrintScreen),
    ("Pause", Key::Pause),
    ("F1", Key::F1),
    ("F2", Key::F2),
    ("F3", Key::F3),
    ("F4", Key::F4),
    ("F5", Key::F5),
    ("F6", Key::F6),
    ("F7", Key::F7),
    ("F8", Key::F8),
    ("F9", Key::F9),
    ("F10", Key::F10),
    ("F11", Key::F11),
    ("F12", Key::F12),
    ("F13", Key::F13),
    ("F14", Key::F14),
    ("F15", Key::F15),
    ("Numpad0", Key::Keypad0),
    ("Numpad1", Key::Keypad1),
    ("Numpad2", Key::Keypad2),
    ("Numpad3", Key::Keypad3),
    ("Numpad4", Key::Keypad4),
    ("Numpad5", Key::Keypad5),
    ("Numpad6", Key::Keypad6),
    ("Numpad7", Key::Keypad7),
    ("Numpad8", Key::Keypad8),
    ("Numpad9", Key::Keypad9),
    ("NumpadDecimal", Key::KeypadDecimal),
    ("NumpadDivide", Key::KeypadDivide),
    ("NumpadMultiply", Key::KeypadMultiply),
    ("NumpadSubtract", Key::KeypadSubtract),
    ("NumpadAdd", Key::KeypadAdd),
    ("NumpadEnter", Key::KeypadEnter),
    ("NumpadEqual", Key::KeypadEqual),
    ("ShiftLeft", Key::ShiftLeft),
    ("ControlLeft", Key::ControlLeft),
    ("AltLeft", Key::AltLeft),
    ("SuperLeft", Key::SuperLeft),
    ("ShiftRight", Key::ShiftRight),
    ("ControlRight", Key::ControlRight),
    ("AltRight", Key::AltRight),
    ("SuperRight", Key::SuperRight),
    ("ContextMenu", Key::Menu),
];

/// The UI key of the physical key `code` (`Unknown` for keys the UI has no
/// name for; they still fall through to the world).
#[must_use]
pub fn ui_key(code: &str) -> Key {
    named(code).map_or(Key::Unknown, |(_, key)| key)
}

/// The code as the module keeps it (a key held is known by it): the
/// table's own name, or `""` for a code the UI does not name.
#[must_use]
pub fn physical(code: &str) -> &'static str {
    named(code).map_or("", |(name, _)| name)
}

/// The two codes a browser names differently from the native window: the
/// system keys are "Meta" in a browser and "Super" natively.
const ALIASES: &[(&str, &str)] = &[("MetaLeft", "SuperLeft"), ("MetaRight", "SuperRight")];

fn named(code: &str) -> Option<(&'static str, Key)> {
    let code = ALIASES
        .iter()
        .find(|(alias, _)| *alias == code)
        .map_or(code, |(_, name)| name);
    CODES.iter().copied().find(|(name, _)| *name == code)
}

/// The UI's modifier bits.
#[must_use]
pub fn ui_modifiers(modifiers: Modifiers) -> UiModifiers {
    let mut m = UiModifiers::NONE;
    if modifiers.shift {
        m = m | UiModifiers::SHIFT;
    }
    if modifiers.ctrl {
        m = m | UiModifiers::CTRL;
    }
    if modifiers.alt {
        m = m | UiModifiers::ALT;
    }
    if modifiers.meta {
        m = m | UiModifiers::WIN;
    }
    m
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The native window's mapping, read from its source: every
    /// `C::<code> => Key::<name>,` arm.
    fn native_arms() -> Vec<(String, String)> {
        let source = include_str!("../../ac-render/src/ui/keys.rs");
        let body = source
            .split("match code {")
            .nth(1)
            .and_then(|rest| rest.split("_ => Key::Unknown").next())
            .expect("the native mapping's match");
        body.lines()
            .filter_map(|line| {
                let (code, key) = line.trim().split_once(" => ")?;
                let code = code.strip_prefix("C::")?;
                let key = key.strip_prefix("Key::")?.trim_end_matches(',');
                Some((code.to_owned(), key.to_owned()))
            })
            .collect()
    }

    #[test]
    fn every_code_the_native_window_names_maps_to_the_same_key() {
        let arms = native_arms();
        assert!(arms.len() > 100, "read {} arms", arms.len());
        for (code, key) in &arms {
            assert_eq!(&format!("{:?}", ui_key(code)), key, "{code}");
            assert_eq!(physical(code), code);
        }
        assert_eq!(CODES.len(), arms.len(), "no code the native window lacks");
    }

    #[test]
    fn the_browsers_system_keys_are_the_native_super_keys() {
        assert_eq!(ui_key("MetaLeft"), Key::SuperLeft);
        assert_eq!(ui_key("MetaRight"), Key::SuperRight);
        assert_eq!(physical("MetaLeft"), "SuperLeft");
    }

    #[test]
    fn a_code_the_ui_does_not_name_is_unknown() {
        assert_eq!(ui_key("F24"), Key::Unknown);
        assert_eq!(ui_key(""), Key::Unknown);
        assert_eq!(physical("IntlRo"), "");
    }

    #[test]
    fn the_pages_modifier_bits_become_the_uis() {
        let m = ui_modifiers(Modifiers::from_bits(0b1011));
        assert!(m.contains(UiModifiers::SHIFT) && m.contains(UiModifiers::CTRL));
        assert!(m.contains(UiModifiers::WIN) && !m.contains(UiModifiers::ALT));
    }
}
