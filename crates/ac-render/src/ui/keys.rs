//! The window's keys as the UI names them.

use ac_ui::event::{Key, Modifiers};
use winit::keyboard::{KeyCode, ModifiersState};

/// The UI key of a physical key (`Unknown` for keys the UI has no name
/// for; they still fall through to the world).
#[must_use]
pub fn ui_key(code: KeyCode) -> Key {
    use KeyCode as C;
    match code {
        C::Space => Key::Space,
        C::Quote => Key::Apostrophe,
        C::Comma => Key::Comma,
        C::Minus => Key::Minus,
        C::Period => Key::Period,
        C::Slash => Key::Slash,
        C::Digit0 => Key::Number0,
        C::Digit1 => Key::Number1,
        C::Digit2 => Key::Number2,
        C::Digit3 => Key::Number3,
        C::Digit4 => Key::Number4,
        C::Digit5 => Key::Number5,
        C::Digit6 => Key::Number6,
        C::Digit7 => Key::Number7,
        C::Digit8 => Key::Number8,
        C::Digit9 => Key::Number9,
        C::Semicolon => Key::Semicolon,
        C::Equal => Key::Equal,
        C::KeyA => Key::A,
        C::KeyB => Key::B,
        C::KeyC => Key::C,
        C::KeyD => Key::D,
        C::KeyE => Key::E,
        C::KeyF => Key::F,
        C::KeyG => Key::G,
        C::KeyH => Key::H,
        C::KeyI => Key::I,
        C::KeyJ => Key::J,
        C::KeyK => Key::K,
        C::KeyL => Key::L,
        C::KeyM => Key::M,
        C::KeyN => Key::N,
        C::KeyO => Key::O,
        C::KeyP => Key::P,
        C::KeyQ => Key::Q,
        C::KeyR => Key::R,
        C::KeyS => Key::S,
        C::KeyT => Key::T,
        C::KeyU => Key::U,
        C::KeyV => Key::V,
        C::KeyW => Key::W,
        C::KeyX => Key::X,
        C::KeyY => Key::Y,
        C::KeyZ => Key::Z,
        C::BracketLeft => Key::LeftBracket,
        C::Backslash => Key::BackSlash,
        C::BracketRight => Key::RightBracket,
        C::Backquote => Key::GraveAccent,
        C::Escape => Key::Escape,
        C::Enter => Key::Enter,
        C::Tab => Key::Tab,
        C::Backspace => Key::Backspace,
        C::Insert => Key::Insert,
        C::Delete => Key::Delete,
        C::ArrowRight => Key::Right,
        C::ArrowLeft => Key::Left,
        C::ArrowDown => Key::Down,
        C::ArrowUp => Key::Up,
        C::PageUp => Key::PageUp,
        C::PageDown => Key::PageDown,
        C::Home => Key::Home,
        C::End => Key::End,
        C::CapsLock => Key::CapsLock,
        C::ScrollLock => Key::ScrollLock,
        C::NumLock => Key::NumLock,
        C::PrintScreen => Key::PrintScreen,
        C::Pause => Key::Pause,
        C::F1 => Key::F1,
        C::F2 => Key::F2,
        C::F3 => Key::F3,
        C::F4 => Key::F4,
        C::F5 => Key::F5,
        C::F6 => Key::F6,
        C::F7 => Key::F7,
        C::F8 => Key::F8,
        C::F9 => Key::F9,
        C::F10 => Key::F10,
        C::F11 => Key::F11,
        C::F12 => Key::F12,
        C::F13 => Key::F13,
        C::F14 => Key::F14,
        C::F15 => Key::F15,
        C::Numpad0 => Key::Keypad0,
        C::Numpad1 => Key::Keypad1,
        C::Numpad2 => Key::Keypad2,
        C::Numpad3 => Key::Keypad3,
        C::Numpad4 => Key::Keypad4,
        C::Numpad5 => Key::Keypad5,
        C::Numpad6 => Key::Keypad6,
        C::Numpad7 => Key::Keypad7,
        C::Numpad8 => Key::Keypad8,
        C::Numpad9 => Key::Keypad9,
        C::NumpadDecimal => Key::KeypadDecimal,
        C::NumpadDivide => Key::KeypadDivide,
        C::NumpadMultiply => Key::KeypadMultiply,
        C::NumpadSubtract => Key::KeypadSubtract,
        C::NumpadAdd => Key::KeypadAdd,
        C::NumpadEnter => Key::KeypadEnter,
        C::NumpadEqual => Key::KeypadEqual,
        C::ShiftLeft => Key::ShiftLeft,
        C::ControlLeft => Key::ControlLeft,
        C::AltLeft => Key::AltLeft,
        C::SuperLeft => Key::SuperLeft,
        C::ShiftRight => Key::ShiftRight,
        C::ControlRight => Key::ControlRight,
        C::AltRight => Key::AltRight,
        C::SuperRight => Key::SuperRight,
        C::ContextMenu => Key::Menu,
        _ => Key::Unknown,
    }
}

/// The UI's modifier bits.
#[must_use]
pub fn ui_modifiers(state: ModifiersState) -> Modifiers {
    let mut m = Modifiers::NONE;
    if state.shift_key() {
        m = m | Modifiers::SHIFT;
    }
    if state.control_key() {
        m = m | Modifiers::CTRL;
    }
    if state.alt_key() {
        m = m | Modifiers::ALT;
    }
    if state.super_key() {
        m = m | Modifiers::WIN;
    }
    m
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn letters_digits_and_editing_keys_have_their_names() {
        assert_eq!(ui_key(KeyCode::KeyW), Key::W);
        assert_eq!(ui_key(KeyCode::Digit7), Key::Number7);
        assert_eq!(ui_key(KeyCode::Enter), Key::Enter);
        assert_eq!(ui_key(KeyCode::NumpadEnter), Key::KeypadEnter);
        assert_eq!(ui_key(KeyCode::Backspace), Key::Backspace);
        assert_eq!(ui_key(KeyCode::F24), Key::Unknown);
    }

    #[test]
    fn modifiers_map_bit_by_bit() {
        let state = ModifiersState::SHIFT | ModifiersState::CONTROL;
        let m = ui_modifiers(state);
        assert!(m.contains(Modifiers::SHIFT) && m.contains(Modifiers::CTRL));
        assert!(!m.contains(Modifiers::ALT));
    }
}
