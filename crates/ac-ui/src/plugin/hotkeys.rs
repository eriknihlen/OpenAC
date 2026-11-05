//! Plugin hotkeys in the window: the client's key map told to the session
//! (plugin chords bind around it), and a key press turned into the plugin
//! hotkeys it fires (OpenAC `AppHotkeyRegistry.MapKey`, `CollidesWithClient`,
//! `OnKeyDown`).

use ac_plugin_ui::hotkeys::{BoundHotkey, Chord, HotkeyGate, matching};

use crate::event::{Key, Modifiers};
use crate::input::{InputKey, KeyBindings};

/// OpenAC's `PluginKey` numbering (the plugin ABI's `keys::PluginKey`;
/// the window does not link the plugin ABI, so the numbers are spelled
/// here and checked against it by `openac`'s `plugin_keys` test).
#[derive(Clone, Copy)]
#[repr(u32)]
enum PluginKeyCode {
    Unknown = 0,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    Number0,
    Number1,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    Space,
    Enter,
    Escape,
    Tab,
    Backspace,
    Delete,
    Insert,
    Home,
    End,
    PageUp,
    PageDown,
    Up,
    Down,
    Left,
    Right,
    Minus,
    Equal,
    LeftBracket,
    RightBracket,
    BackSlash,
    Semicolon,
    Apostrophe,
    Comma,
    Period,
    Slash,
    Numpad0,
    Numpad1,
    Numpad2,
    Numpad3,
    Numpad4,
    Numpad5,
    Numpad6,
    Numpad7,
    Numpad8,
    Numpad9,
    NumpadDecimal,
    NumpadDivide,
    NumpadMultiply,
    NumpadSubtract,
    NumpadAdd,
    NumpadEnter,
    Grave,
    PrintScreen,
    Pause,
}

use PluginKeyCode as P;

/// The plugin key number for a window key (OpenAC `MapKey`, reversed); 0
/// for a key a plugin cannot bind.
#[must_use]
pub fn plugin_key(key: Key) -> u32 {
    plugin_key_of(key) as u32
}

#[allow(clippy::too_many_lines)]
fn plugin_key_of(key: Key) -> P {
    match key {
        Key::A => P::A,
        Key::B => P::B,
        Key::C => P::C,
        Key::D => P::D,
        Key::E => P::E,
        Key::F => P::F,
        Key::G => P::G,
        Key::H => P::H,
        Key::I => P::I,
        Key::J => P::J,
        Key::K => P::K,
        Key::L => P::L,
        Key::M => P::M,
        Key::N => P::N,
        Key::O => P::O,
        Key::P => P::P,
        Key::Q => P::Q,
        Key::R => P::R,
        Key::S => P::S,
        Key::T => P::T,
        Key::U => P::U,
        Key::V => P::V,
        Key::W => P::W,
        Key::X => P::X,
        Key::Y => P::Y,
        Key::Z => P::Z,
        Key::Number0 => P::Number0,
        Key::Number1 => P::Number1,
        Key::Number2 => P::Number2,
        Key::Number3 => P::Number3,
        Key::Number4 => P::Number4,
        Key::Number5 => P::Number5,
        Key::Number6 => P::Number6,
        Key::Number7 => P::Number7,
        Key::Number8 => P::Number8,
        Key::Number9 => P::Number9,
        Key::F1 => P::F1,
        Key::F2 => P::F2,
        Key::F3 => P::F3,
        Key::F4 => P::F4,
        Key::F5 => P::F5,
        Key::F6 => P::F6,
        Key::F7 => P::F7,
        Key::F8 => P::F8,
        Key::F9 => P::F9,
        Key::F10 => P::F10,
        Key::F11 => P::F11,
        Key::F12 => P::F12,
        Key::Space => P::Space,
        Key::Enter => P::Enter,
        Key::Escape => P::Escape,
        Key::Tab => P::Tab,
        Key::Backspace => P::Backspace,
        Key::Delete => P::Delete,
        Key::Insert => P::Insert,
        Key::Home => P::Home,
        Key::End => P::End,
        Key::PageUp => P::PageUp,
        Key::PageDown => P::PageDown,
        Key::Up => P::Up,
        Key::Down => P::Down,
        Key::Left => P::Left,
        Key::Right => P::Right,
        Key::Minus => P::Minus,
        Key::Equal => P::Equal,
        Key::LeftBracket => P::LeftBracket,
        Key::RightBracket => P::RightBracket,
        Key::BackSlash => P::BackSlash,
        Key::Semicolon => P::Semicolon,
        Key::Apostrophe => P::Apostrophe,
        Key::Comma => P::Comma,
        Key::Period => P::Period,
        Key::Slash => P::Slash,
        Key::Keypad0 => P::Numpad0,
        Key::Keypad1 => P::Numpad1,
        Key::Keypad2 => P::Numpad2,
        Key::Keypad3 => P::Numpad3,
        Key::Keypad4 => P::Numpad4,
        Key::Keypad5 => P::Numpad5,
        Key::Keypad6 => P::Numpad6,
        Key::Keypad7 => P::Numpad7,
        Key::Keypad8 => P::Numpad8,
        Key::Keypad9 => P::Numpad9,
        Key::KeypadDecimal => P::NumpadDecimal,
        Key::KeypadDivide => P::NumpadDivide,
        Key::KeypadMultiply => P::NumpadMultiply,
        Key::KeypadSubtract => P::NumpadSubtract,
        Key::KeypadAdd => P::NumpadAdd,
        Key::KeypadEnter => P::NumpadEnter,
        Key::GraveAccent => P::Grave,
        Key::PrintScreen => P::PrintScreen,
        Key::Pause => P::Pause,
        _ => P::Unknown,
    }
}

/// The chord a key press makes (Ctrl, Alt and Shift; the Windows key does
/// not count, as OpenAC's chord has none).
#[must_use]
pub fn pressed(key: Key, modifiers: Modifiers) -> Chord {
    Chord {
        key: plugin_key(key),
        ctrl: modifiers.contains(Modifiers::CTRL),
        alt: modifiers.contains(Modifiers::ALT),
        shift: modifiers.contains(Modifiers::SHIFT),
    }
}

/// Every keyboard chord the client's key map uses, in any scope and any
/// activation (OpenAC `CollidesWithClient` asks for press, release and
/// hold): a plugin hotkey on one of them is refused.
#[must_use]
pub fn client_chords(bindings: &KeyBindings) -> Vec<Chord> {
    let mut chords: Vec<Chord> = Vec::new();
    for binding in bindings.all() {
        let InputKey::Key(key) = binding.chord.key else {
            continue;
        };
        let modifiers = binding.chord.modifiers;
        if modifiers.0 & !(Modifiers::CTRL.0 | Modifiers::ALT.0 | Modifiers::SHIFT.0) != 0 {
            continue;
        }
        let chord = pressed(key, modifiers);
        if chord.key != 0 && !chords.contains(&chord) {
            chords.push(chord);
        }
    }
    chords
}

/// The bound plugin hotkeys a key press fires (OpenAC `OnKeyDown`).
#[must_use]
pub fn fired(
    table: &[BoundHotkey],
    key: Key,
    modifiers: Modifiers,
    gate: HotkeyGate,
) -> Vec<&BoundHotkey> {
    matching(table, pressed(key, modifiers), gate)
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `AppHotkeyRegistryTests.ARegistrationCollidingWithAClient
    // BindingIsRefused`: Escape is a retail default binding.
    #[test]
    fn the_retail_key_map_holds_escape_and_the_movement_keys() {
        let chords = client_chords(&KeyBindings::retail_defaults());
        assert!(chords.contains(&Chord::key(P::Escape as u32)));
        assert!(chords.contains(&Chord::key(P::W as u32)));
        assert!(!chords.contains(&Chord::default()));
    }

    // OpenAC `NumpadGraveAndSystemKeysMapAndFire`.
    #[test]
    fn numpad_grave_and_system_keys_map() {
        assert_eq!(plugin_key(Key::Keypad5), 79);
        assert_eq!(plugin_key(Key::KeypadEnter), 89);
        assert_eq!(plugin_key(Key::GraveAccent), 90);
        assert_eq!(plugin_key(Key::PrintScreen), 91);
        assert_eq!(plugin_key(Key::Pause), 92);
        assert_eq!(plugin_key(Key::CapsLock), 0);
    }

    #[test]
    fn a_press_fires_the_hotkey_with_exactly_its_modifiers() {
        let table = vec![BoundHotkey {
            owner: "p".into(),
            id: "heal".into(),
            chord: Chord::key(P::H as u32).with_ctrl(),
        }];
        let gate = HotkeyGate::default();
        assert_eq!(fired(&table, Key::H, Modifiers::CTRL, gate).len(), 1);
        assert!(fired(&table, Key::H, Modifiers::NONE, gate).is_empty());
        let ctrl_shift = Modifiers(Modifiers::CTRL.0 | Modifiers::SHIFT.0);
        assert!(fired(&table, Key::H, ctrl_shift, gate).is_empty());
        let ctrl_win = Modifiers(Modifiers::CTRL.0 | Modifiers::WIN.0);
        assert_eq!(fired(&table, Key::H, ctrl_win, gate).len(), 1);
    }
}
