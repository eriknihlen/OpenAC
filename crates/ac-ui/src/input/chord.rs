//! Chords (a key or mouse button with the modifiers held), how a binding
//! fires, the scopes bindings live in, and one binding.

use crate::event::{Key, Modifiers};

use super::action::InputAction;

macro_rules! key_names {
    ($($name:ident,)+) => {
        /// The stored name of a keyboard key (the names the binding file
        /// uses).
        #[must_use]
        pub fn key_name(key: Key) -> &'static str {
            match key {
                $(Key::$name => stringify!($name),)+
            }
        }

        /// The keyboard key a stored name names.
        #[must_use]
        pub fn key_from_name(name: &str) -> Option<Key> {
            match name {
                $(stringify!($name) => Some(Key::$name),)+
                _ => None,
            }
        }
    };
}

key_names! {
    Unknown, Space, Apostrophe, Comma, Minus, Period, Slash, Number0, Number1, Number2,
    Number3, Number4, Number5, Number6, Number7, Number8, Number9, Semicolon, Equal, A, B,
    C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, LeftBracket,
    BackSlash, RightBracket, GraveAccent, Escape, Enter, Tab, Backspace, Insert, Delete,
    Right, Left, Down, Up, PageUp, PageDown, Home, End, CapsLock, ScrollLock, NumLock,
    PrintScreen, Pause, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15,
    Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8,
    Keypad9, KeypadDecimal, KeypadDivide, KeypadMultiply, KeypadSubtract, KeypadAdd,
    KeypadEnter, KeypadEqual, ShiftLeft, ControlLeft, AltLeft, SuperLeft, ShiftRight,
    ControlRight, AltRight, SuperRight, Menu,
}

/// Mouse button numbers: left, right, middle, then the side buttons.
pub mod mouse {
    /// Left button.
    pub const LEFT: u8 = 0;
    /// Right button.
    pub const RIGHT: u8 = 1;
    /// Middle button.
    pub const MIDDLE: u8 = 2;
    /// First side button.
    pub const BUTTON4: u8 = 3;
    /// Second side button.
    pub const BUTTON5: u8 = 4;
}

/// What a chord presses: a keyboard key or a mouse button (by number,
/// [`mouse`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum InputKey {
    /// A keyboard key.
    Key(Key),
    /// A mouse button.
    Mouse(u8),
}

impl InputKey {
    /// The stored name: the key's name, or for a mouse button its number
    /// in the key space (left -1001, right -1002, middle -1003, side
    /// buttons -1004 and -1005).
    #[must_use]
    pub fn name(self) -> String {
        match self {
            InputKey::Key(key) => key_name(key).to_owned(),
            InputKey::Mouse(button) => mouse_code(button).to_string(),
        }
    }

    /// The key or button a stored name names.
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        if let Some(key) = key_from_name(name) {
            return Some(InputKey::Key(key));
        }
        let code: i32 = name.parse().ok()?;
        (-1005..=-1001)
            .contains(&code)
            .then(|| InputKey::Mouse((-1001 - code) as u8))
    }
}

/// A mouse button's number in the key space.
#[must_use]
pub fn mouse_code(button: u8) -> i32 {
    if button <= mouse::BUTTON5 {
        -1001 - i32::from(button)
    } else {
        -1000 - i32::from(button)
    }
}

impl From<Key> for InputKey {
    fn from(key: Key) -> Self {
        InputKey::Key(key)
    }
}

/// A key or button with exactly these modifiers held, on a device (0 the
/// keyboard, 1 the mouse).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct KeyChord {
    /// The key or button.
    pub key: InputKey,
    /// The modifiers held.
    pub modifiers: Modifiers,
    /// The device: 0 keyboard, 1 mouse.
    pub device: u8,
}

impl KeyChord {
    /// A keyboard chord.
    #[must_use]
    pub fn key(key: Key, modifiers: Modifiers) -> Self {
        Self {
            key: InputKey::Key(key),
            modifiers,
            device: 0,
        }
    }

    /// A mouse chord.
    #[must_use]
    pub fn mouse(button: u8, modifiers: Modifiers) -> Self {
        Self {
            key: InputKey::Mouse(button),
            modifiers,
            device: 1,
        }
    }

    /// The same key on another device.
    #[must_use]
    pub fn on_device(self, device: u8) -> Self {
        Self { device, ..self }
    }

    /// The same key with other modifiers.
    #[must_use]
    pub fn with_modifiers(self, modifiers: Modifiers) -> Self {
        Self { modifiers, ..self }
    }
}

/// The stored form of a modifier set: `None`, else the held names joined
/// by `, ` (Shift, Ctrl, Alt, Win).
#[must_use]
pub fn modifiers_name(modifiers: Modifiers) -> String {
    if modifiers.0 == 0 {
        return "None".to_owned();
    }
    let names: Vec<&str> = [
        (Modifiers::SHIFT, "Shift"),
        (Modifiers::CTRL, "Ctrl"),
        (Modifiers::ALT, "Alt"),
        (Modifiers::WIN, "Win"),
    ]
    .iter()
    .filter(|(bit, _)| modifiers.contains(*bit))
    .map(|(_, name)| *name)
    .collect();
    names.join(", ")
}

/// A stored modifier set: names separated by `|`, `,` or spaces, any case;
/// unknown names are skipped.
#[must_use]
pub fn parse_modifiers(text: &str) -> Modifiers {
    let mut result = Modifiers::NONE;
    for part in text.split(['|', ',', ' ']).filter(|part| !part.is_empty()) {
        result = result
            | match part.to_ascii_lowercase().as_str() {
                "shift" => Modifiers::SHIFT,
                "ctrl" => Modifiers::CTRL,
                "alt" => Modifiers::ALT,
                "win" => Modifiers::WIN,
                _ => Modifiers::NONE,
            };
    }
    result
}

/// When a binding fires.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ActivationType {
    /// On the press (most actions).
    #[default]
    Press,
    /// On the release.
    Release,
    /// A press when it goes down, a hold each tick, a release when it
    /// comes up.
    Hold,
    /// A second press of the same mouse button soon after the first.
    DoubleClick,
    /// A mouse press and release without dragging (a key press for a
    /// keyboard chord).
    Click,
    /// An analog axis (not emitted).
    Analog,
}

impl ActivationType {
    /// Every activation.
    pub const ALL: [ActivationType; 6] = [
        ActivationType::Press,
        ActivationType::Release,
        ActivationType::Hold,
        ActivationType::DoubleClick,
        ActivationType::Click,
        ActivationType::Analog,
    ];

    /// The stored name.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            ActivationType::Press => "Press",
            ActivationType::Release => "Release",
            ActivationType::Hold => "Hold",
            ActivationType::DoubleClick => "DoubleClick",
            ActivationType::Click => "Click",
            ActivationType::Analog => "Analog",
        }
    }

    /// The activation a stored name names.
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|a| a.name() == name)
    }
}

/// Where a binding applies. The dispatcher keeps a stack of these; the top
/// wins.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum InputScope {
    /// Bottom of the stack: works whatever else has the keys.
    Always,
    /// Ordinary play.
    #[default]
    Game,
    /// The chat entry has the keys.
    Chat,
    /// A text field has the keys.
    EditField,
    /// A dialog has the keys.
    Dialog,
    /// Melee combat keys (over `Game`).
    MeleeCombat,
    /// Missile combat keys (over `Game`).
    MissileCombat,
    /// Magic combat keys (over `Game`).
    MagicCombat,
    /// The alternate camera keys (while the alternate-mode key is held).
    Camera,
}

impl InputScope {
    /// Every scope.
    pub const ALL: [InputScope; 9] = [
        InputScope::Always,
        InputScope::Game,
        InputScope::Chat,
        InputScope::EditField,
        InputScope::Dialog,
        InputScope::MeleeCombat,
        InputScope::MissileCombat,
        InputScope::MagicCombat,
        InputScope::Camera,
    ];

    /// The stored name.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            InputScope::Always => "Always",
            InputScope::Game => "Game",
            InputScope::Chat => "Chat",
            InputScope::EditField => "EditField",
            InputScope::Dialog => "Dialog",
            InputScope::MeleeCombat => "MeleeCombat",
            InputScope::MissileCombat => "MissileCombat",
            InputScope::MagicCombat => "MagicCombat",
            InputScope::Camera => "Camera",
        }
    }

    /// The scope a stored name names.
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|s| s.name() == name)
    }
}

/// One binding: a chord fires an action with an activation in a scope.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Binding {
    /// The chord.
    pub chord: KeyChord,
    /// The action.
    pub action: InputAction,
    /// When it fires.
    pub activation: ActivationType,
    /// Where it applies.
    pub scope: InputScope,
}

impl Binding {
    /// A press in the game scope.
    #[must_use]
    pub fn new(chord: KeyChord, action: InputAction) -> Self {
        Self {
            chord,
            action,
            activation: ActivationType::Press,
            scope: InputScope::Game,
        }
    }

    /// The same binding with another activation.
    #[must_use]
    pub fn activation(self, activation: ActivationType) -> Self {
        Self { activation, ..self }
    }

    /// The same binding in another scope.
    #[must_use]
    pub fn scope(self, scope: InputScope) -> Self {
        Self { scope, ..self }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.UI.Abstractions.Tests/Input/KeyChordTests.cs`
    //! and `ModifierMaskTests.cs`.

    use super::*;

    #[test]
    fn same_key_no_modifier_equal() {
        assert_eq!(
            KeyChord::key(Key::W, Modifiers::NONE),
            KeyChord::key(Key::W, Modifiers::NONE)
        );
    }

    #[test]
    fn ctrl_a_does_not_match_bare_a_or_shift_ctrl_a() {
        let ctrl_a = KeyChord::key(Key::A, Modifiers::CTRL);
        assert_ne!(ctrl_a, KeyChord::key(Key::A, Modifiers::NONE));
        assert_ne!(
            ctrl_a,
            KeyChord::key(Key::A, Modifiers::SHIFT | Modifiers::CTRL)
        );
    }

    #[test]
    fn different_keys_with_same_modifier_distinct() {
        assert_ne!(
            KeyChord::key(Key::A, Modifiers::SHIFT),
            KeyChord::key(Key::B, Modifiers::SHIFT)
        );
    }

    #[test]
    fn default_device_is_the_keyboard_and_devices_are_distinct() {
        let keyboard = KeyChord::key(Key::A, Modifiers::NONE);
        assert_eq!(keyboard.device, 0);
        assert_ne!(keyboard, keyboard.on_device(1));
    }

    #[test]
    fn modifier_bits_match_the_retail_keymap_metakey_table() {
        assert_eq!(Modifiers::NONE.0, 0);
        assert_eq!(Modifiers::SHIFT.0, 0x01);
        assert_eq!(Modifiers::CTRL.0, 0x02);
        assert_eq!(Modifiers::ALT.0, 0x04);
        assert_eq!(Modifiers::WIN.0, 0x08);
        let combo = Modifiers::SHIFT | Modifiers::CTRL;
        assert!(combo.contains(Modifiers::SHIFT) && combo.contains(Modifiers::CTRL));
        assert!(!combo.contains(Modifiers::ALT));
        assert_ne!(Modifiers::SHIFT, Modifiers::CTRL);
        assert_eq!(combo, Modifiers::CTRL | Modifiers::SHIFT);
    }

    #[test]
    fn modifier_and_key_names_round_trip() {
        assert_eq!(modifiers_name(Modifiers::NONE), "None");
        assert_eq!(
            modifiers_name(Modifiers::SHIFT | Modifiers::CTRL),
            "Shift, Ctrl"
        );
        assert_eq!(
            parse_modifiers("Shift, Ctrl"),
            Modifiers::SHIFT | Modifiers::CTRL
        );
        assert_eq!(
            parse_modifiers("ctrl|alt"),
            Modifiers::CTRL | Modifiers::ALT
        );
        assert_eq!(parse_modifiers("Bogus"), Modifiers::NONE);
        assert_eq!(
            InputKey::from_name("Semicolon"),
            Some(InputKey::Key(Key::Semicolon))
        );
        assert_eq!(InputKey::Mouse(mouse::MIDDLE).name(), "-1003");
        assert_eq!(
            InputKey::from_name("-1002"),
            Some(InputKey::Mouse(mouse::RIGHT))
        );
        assert_eq!(InputKey::from_name("-1006"), None);
    }
}
