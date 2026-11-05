//! The game's DirectInput key names and scan codes: the retail key map
//! file names keys `DIK_*` (keyboard, device 0) and `DIMOFS_BUTTON*`
//! (mouse, device 1), and the dat key maps store scan codes.

use crate::event::{Key, Modifiers};

use super::chord::{InputKey, KeyChord, mouse};

/// The modifiers in a retail modifier word (Shift 0x8000_0000, Ctrl
/// 0x4000_0000, Alt 0x2000_0000).
#[must_use]
pub fn to_modifier_mask(retail_modifier: u32) -> Modifiers {
    let mut mask = Modifiers::NONE;
    if retail_modifier & 0x8000_0000 != 0 {
        mask = mask | Modifiers::SHIFT;
    }
    if retail_modifier & 0x4000_0000 != 0 {
        mask = mask | Modifiers::CTRL;
    }
    if retail_modifier & 0x2000_0000 != 0 {
        mask = mask | Modifiers::ALT;
    }
    mask
}

/// The key a scan code names on a device (mouse buttons are scans 0x0C to
/// 0x0F on device 1).
#[must_use]
pub fn to_key(scan: u32, device: u32) -> Option<InputKey> {
    if device == 1 {
        return Some(InputKey::Mouse(match scan {
            0x0C => mouse::LEFT,
            0x0D => mouse::RIGHT,
            0x0E => mouse::MIDDLE,
            0x0F => mouse::BUTTON4,
            _ => return None,
        }));
    }
    if device != 0 {
        return None;
    }
    Some(InputKey::Key(match scan {
        0x01 => Key::Escape,
        0x02 => Key::Number1,
        0x03 => Key::Number2,
        0x04 => Key::Number3,
        0x05 => Key::Number4,
        0x06 => Key::Number5,
        0x07 => Key::Number6,
        0x08 => Key::Number7,
        0x09 => Key::Number8,
        0x0A => Key::Number9,
        0x0B => Key::Number0,
        0x0C => Key::Minus,
        0x0D => Key::Equal,
        0x0E => Key::Backspace,
        0x0F => Key::Tab,
        0x10 => Key::Q,
        0x11 => Key::W,
        0x12 => Key::E,
        0x13 => Key::R,
        0x14 => Key::T,
        0x15 => Key::Y,
        0x16 => Key::U,
        0x17 => Key::I,
        0x18 => Key::O,
        0x19 => Key::P,
        0x1A => Key::LeftBracket,
        0x1B => Key::RightBracket,
        0x1C => Key::Enter,
        0x1D => Key::ControlLeft,
        0x1E => Key::A,
        0x1F => Key::S,
        0x20 => Key::D,
        0x21 => Key::F,
        0x22 => Key::G,
        0x23 => Key::H,
        0x24 => Key::J,
        0x25 => Key::K,
        0x26 => Key::L,
        0x27 => Key::Semicolon,
        0x28 => Key::Apostrophe,
        0x29 => Key::GraveAccent,
        0x2A => Key::ShiftLeft,
        0x2B => Key::BackSlash,
        0x2C => Key::Z,
        0x2D => Key::X,
        0x2E => Key::C,
        0x2F => Key::V,
        0x30 => Key::B,
        0x31 => Key::N,
        0x32 => Key::M,
        0x33 => Key::Comma,
        0x34 => Key::Period,
        0x35 => Key::Slash,
        0x36 => Key::ShiftRight,
        0x37 => Key::KeypadMultiply,
        0x38 => Key::AltLeft,
        0x39 => Key::Space,
        0x3A => Key::CapsLock,
        0x3B => Key::F1,
        0x3C => Key::F2,
        0x3D => Key::F3,
        0x3E => Key::F4,
        0x3F => Key::F5,
        0x40 => Key::F6,
        0x41 => Key::F7,
        0x42 => Key::F8,
        0x43 => Key::F9,
        0x44 => Key::F10,
        0x45 => Key::NumLock,
        0x46 => Key::ScrollLock,
        0x47 => Key::Keypad7,
        0x48 => Key::Keypad8,
        0x49 => Key::Keypad9,
        0x4A => Key::KeypadSubtract,
        0x4B => Key::Keypad4,
        0x4C => Key::Keypad5,
        0x4D => Key::Keypad6,
        0x4E => Key::KeypadAdd,
        0x4F => Key::Keypad1,
        0x50 => Key::Keypad2,
        0x51 => Key::Keypad3,
        0x52 => Key::Keypad0,
        0x53 => Key::KeypadDecimal,
        0x57 => Key::F11,
        0x58 => Key::F12,
        0x64 => Key::F13,
        0x65 => Key::F14,
        0x66 => Key::F15,
        0x9C => Key::KeypadEnter,
        0x9D => Key::ControlRight,
        0xB5 => Key::KeypadDivide,
        0xB7 => Key::PrintScreen,
        0xB8 => Key::AltRight,
        0xC5 => Key::Pause,
        0xC7 => Key::Home,
        0xC8 => Key::Up,
        0xC9 => Key::PageUp,
        0xCB => Key::Left,
        0xCD => Key::Right,
        0xCF => Key::End,
        0xD0 => Key::Down,
        0xD1 => Key::PageDown,
        0xD2 => Key::Insert,
        0xD3 => Key::Delete,
        0xDB => Key::SuperLeft,
        0xDC => Key::SuperRight,
        0xDD => Key::Menu,
        _ => return None,
    }))
}

/// The (scan, device) a key map file control names (`DIK_*`, any case,
/// with the file's aliases; `DIMOFS_BUTTON0` to `DIMOFS_BUTTON4`).
#[must_use]
pub fn from_file_control(control: &str) -> Option<(u32, u32)> {
    let token = control.trim().to_ascii_uppercase();
    if token.is_empty() {
        return None;
    }
    if let Some(button) = token.strip_prefix("DIMOFS_BUTTON") {
        let button: u32 = button.parse().ok()?;
        return (button <= 4).then_some((0x0C + button, 1));
    }
    let token = token.strip_prefix("DIK_")?;
    let scan = match token {
        "ESCAPE" => 0x01,
        "1" => 0x02,
        "2" => 0x03,
        "3" => 0x04,
        "4" => 0x05,
        "5" => 0x06,
        "6" => 0x07,
        "7" => 0x08,
        "8" => 0x09,
        "9" => 0x0A,
        "0" => 0x0B,
        "MINUS" => 0x0C,
        "EQUALS" => 0x0D,
        "BACK" => 0x0E,
        "TAB" => 0x0F,
        "Q" => 0x10,
        "W" => 0x11,
        "E" => 0x12,
        "R" => 0x13,
        "T" => 0x14,
        "Y" => 0x15,
        "U" => 0x16,
        "I" => 0x17,
        "O" => 0x18,
        "P" => 0x19,
        "LBRACKET" => 0x1A,
        "RBRACKET" => 0x1B,
        "RETURN" => 0x1C,
        "LCONTROL" => 0x1D,
        "A" => 0x1E,
        "S" => 0x1F,
        "D" => 0x20,
        "F" => 0x21,
        "G" => 0x22,
        "H" => 0x23,
        "J" => 0x24,
        "K" => 0x25,
        "L" => 0x26,
        "SEMICOLON" => 0x27,
        "APOSTROPHE" => 0x28,
        "GRAVE" => 0x29,
        "LSHIFT" => 0x2A,
        "BACKSLASH" => 0x2B,
        "Z" => 0x2C,
        "X" => 0x2D,
        "C" => 0x2E,
        "V" => 0x2F,
        "B" => 0x30,
        "N" => 0x31,
        "M" => 0x32,
        "COMMA" => 0x33,
        "PERIOD" => 0x34,
        "SLASH" => 0x35,
        "RSHIFT" => 0x36,
        "MULTIPLY" | "NUMPADSTAR" => 0x37,
        "LMENU" | "LALT" => 0x38,
        "SPACE" => 0x39,
        "CAPITAL" => 0x3A,
        "F1" => 0x3B,
        "F2" => 0x3C,
        "F3" => 0x3D,
        "F4" => 0x3E,
        "F5" => 0x3F,
        "F6" => 0x40,
        "F7" => 0x41,
        "F8" => 0x42,
        "F9" => 0x43,
        "F10" => 0x44,
        "NUMLOCK" => 0x45,
        "SCROLL" => 0x46,
        "NUMPAD7" => 0x47,
        "NUMPAD8" => 0x48,
        "NUMPAD9" => 0x49,
        "SUBTRACT" | "NUMPADMINUS" => 0x4A,
        "NUMPAD4" => 0x4B,
        "NUMPAD5" => 0x4C,
        "NUMPAD6" => 0x4D,
        "ADD" | "NUMPADPLUS" => 0x4E,
        "NUMPAD1" => 0x4F,
        "NUMPAD2" => 0x50,
        "NUMPAD3" => 0x51,
        "NUMPAD0" => 0x52,
        "DECIMAL" | "NUMPADPERIOD" => 0x53,
        "F11" => 0x57,
        "F12" => 0x58,
        "F13" => 0x64,
        "F14" => 0x65,
        "F15" => 0x66,
        "NUMPADENTER" => 0x9C,
        "RCONTROL" => 0x9D,
        "DIVIDE" | "NUMPADSLASH" => 0xB5,
        "SYSRQ" => 0xB7,
        "RMENU" | "RALT" => 0xB8,
        "PAUSE" => 0xC5,
        "HOME" => 0xC7,
        "UP" | "UPARROW" => 0xC8,
        "PRIOR" | "PGUP" => 0xC9,
        "LEFT" => 0xCB,
        "RIGHT" | "RIGHTARROW" => 0xCD,
        "END" => 0xCF,
        "DOWN" | "DOWNARROW" => 0xD0,
        "NEXT" | "PGDN" => 0xD1,
        "INSERT" => 0xD2,
        "DELETE" => 0xD3,
        "LWIN" => 0xDB,
        "RWIN" => 0xDC,
        "APPS" => 0xDD,
        _ => return None,
    };
    Some((scan, 0))
}

/// The key map file control for a chord (`None` when the file cannot name
/// it). Where the file has several spellings, the game's own is used.
#[must_use]
pub fn to_file_control(chord: KeyChord) -> Option<String> {
    if chord.device == 1 {
        let InputKey::Mouse(button) = chord.key else {
            return None;
        };
        return (button <= mouse::BUTTON5).then(|| format!("DIMOFS_BUTTON{button}"));
    }
    if chord.device != 0 {
        return None;
    }
    for scan in 1..=0xDDu32 {
        if to_key(scan, 0) != Some(chord.key) {
            continue;
        }
        let control = match scan {
            0x37 => "DIK_NUMPADSTAR".to_owned(),
            0x4A => "DIK_NUMPADMINUS".to_owned(),
            0x4E => "DIK_NUMPADPLUS".to_owned(),
            0xB5 => "DIK_NUMPADSLASH".to_owned(),
            0xC8 => "DIK_UPARROW".to_owned(),
            0xC9 => "DIK_PGUP".to_owned(),
            0xCD => "DIK_RIGHTARROW".to_owned(),
            0xD0 => "DIK_DOWNARROW".to_owned(),
            0xD1 => "DIK_PGDN".to_owned(),
            _ => file_token(scan),
        };
        return (!control.is_empty()).then_some(control);
    }
    None
}

fn file_token(scan: u32) -> String {
    let row = |letters: &str, first: u32| {
        let index = (scan - first) as usize;
        format!("DIK_{}", &letters[index..=index])
    };
    match scan {
        0x01 => "DIK_ESCAPE".to_owned(),
        0x02..=0x0A => format!("DIK_{}", scan - 1),
        0x0B => "DIK_0".to_owned(),
        0x0C => "DIK_MINUS".to_owned(),
        0x0D => "DIK_EQUALS".to_owned(),
        0x0E => "DIK_BACK".to_owned(),
        0x0F => "DIK_TAB".to_owned(),
        0x10..=0x19 => row("QWERTYUIOP", 0x10),
        0x1A => "DIK_LBRACKET".to_owned(),
        0x1B => "DIK_RBRACKET".to_owned(),
        0x1C => "DIK_RETURN".to_owned(),
        0x1D => "DIK_LCONTROL".to_owned(),
        0x1E..=0x26 => row("ASDFGHJKL", 0x1E),
        0x27 => "DIK_SEMICOLON".to_owned(),
        0x28 => "DIK_APOSTROPHE".to_owned(),
        0x29 => "DIK_GRAVE".to_owned(),
        0x2A => "DIK_LSHIFT".to_owned(),
        0x2B => "DIK_BACKSLASH".to_owned(),
        0x2C..=0x32 => row("ZXCVBNM", 0x2C),
        0x33 => "DIK_COMMA".to_owned(),
        0x34 => "DIK_PERIOD".to_owned(),
        0x35 => "DIK_SLASH".to_owned(),
        0x36 => "DIK_RSHIFT".to_owned(),
        0x38 => "DIK_LMENU".to_owned(),
        0x39 => "DIK_SPACE".to_owned(),
        0x3A => "DIK_CAPITAL".to_owned(),
        0x3B..=0x44 => format!("DIK_F{}", scan - 0x3A),
        0x45 => "DIK_NUMLOCK".to_owned(),
        0x46 => "DIK_SCROLL".to_owned(),
        0x47 => "DIK_NUMPAD7".to_owned(),
        0x48 => "DIK_NUMPAD8".to_owned(),
        0x49 => "DIK_NUMPAD9".to_owned(),
        0x4B => "DIK_NUMPAD4".to_owned(),
        0x4C => "DIK_NUMPAD5".to_owned(),
        0x4D => "DIK_NUMPAD6".to_owned(),
        0x4F => "DIK_NUMPAD1".to_owned(),
        0x50 => "DIK_NUMPAD2".to_owned(),
        0x51 => "DIK_NUMPAD3".to_owned(),
        0x52 => "DIK_NUMPAD0".to_owned(),
        0x53 => "DIK_DECIMAL".to_owned(),
        0x57 => "DIK_F11".to_owned(),
        0x58 => "DIK_F12".to_owned(),
        0x64 => "DIK_F13".to_owned(),
        0x65 => "DIK_F14".to_owned(),
        0x66 => "DIK_F15".to_owned(),
        0x9C => "DIK_NUMPADENTER".to_owned(),
        0x9D => "DIK_RCONTROL".to_owned(),
        0xB7 => "DIK_SYSRQ".to_owned(),
        0xB8 => "DIK_RALT".to_owned(),
        0xC5 => "DIK_PAUSE".to_owned(),
        0xC7 => "DIK_HOME".to_owned(),
        0xCB => "DIK_LEFT".to_owned(),
        0xCF => "DIK_END".to_owned(),
        0xD2 => "DIK_INSERT".to_owned(),
        0xD3 => "DIK_DELETE".to_owned(),
        0xDB => "DIK_LWIN".to_owned(),
        0xDC => "DIK_RWIN".to_owned(),
        0xDD => "DIK_APPS".to_owned(),
        _ => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retail_modifier_words_become_masks() {
        assert_eq!(to_modifier_mask(0), Modifiers::NONE);
        assert_eq!(to_modifier_mask(0x8000_0000), Modifiers::SHIFT);
        assert_eq!(
            to_modifier_mask(0x6000_0000),
            Modifiers::CTRL | Modifiers::ALT
        );
    }

    #[test]
    fn every_named_keyboard_scan_round_trips_through_the_file_control() {
        for scan in 1..=0xDDu32 {
            let Some(key) = to_key(scan, 0) else {
                continue;
            };
            let control = to_file_control(KeyChord {
                key,
                modifiers: Modifiers::NONE,
                device: 0,
            })
            .unwrap_or_else(|| panic!("scan {scan:#x} has no file name"));
            assert_eq!(from_file_control(&control), Some((scan, 0)), "{control}");
        }
    }

    #[test]
    fn the_file_aliases_and_mouse_buttons_resolve() {
        assert_eq!(from_file_control("dik_numpadstar"), Some((0x37, 0)));
        assert_eq!(from_file_control("DIK_MULTIPLY"), Some((0x37, 0)));
        assert_eq!(from_file_control("DIK_PRIOR"), Some((0xC9, 0)));
        assert_eq!(from_file_control("DIMOFS_BUTTON2"), Some((0x0E, 1)));
        assert_eq!(from_file_control("DIMOFS_BUTTON5"), None);
        assert_eq!(from_file_control("DIK_NOPE"), None);
        assert_eq!(to_key(0x0E, 1), Some(InputKey::Mouse(mouse::MIDDLE)));
        assert_eq!(
            to_file_control(KeyChord::mouse(mouse::RIGHT, Modifiers::NONE)).as_deref(),
            Some("DIMOFS_BUTTON1")
        );
        assert_eq!(
            to_file_control(KeyChord::key(Key::Up, Modifiers::NONE)).as_deref(),
            Some("DIK_UPARROW")
        );
        assert_eq!(
            to_file_control(KeyChord::key(Key::KeypadEqual, Modifiers::NONE)),
            None
        );
    }
}
