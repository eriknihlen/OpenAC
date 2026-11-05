//! The keys a plugin hotkey can name (OpenAC `PluginKey`): their numbers
//! travel on the wire ([`crate::ui::KeyChord`]) and their names are what
//! the overrides file stores.

macro_rules! plugin_keys {
    ($( $name:ident ),* $(,)?) => {
        /// A key a plugin can bind (OpenAC `PluginKey`; its numbering and
        /// names, which the overrides file stores).
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Default)]
        #[repr(u32)]
        pub enum PluginKey {
            /// No key; a chord nobody set.
            #[default]
            Unknown = 0,
            $(
                #[allow(missing_docs)]
                $name,
            )*
        }

        impl PluginKey {
            /// Every key, by number.
            pub const ALL: &'static [PluginKey] = &[PluginKey::Unknown, $( PluginKey::$name, )*];

            /// Its name (OpenAC's enum name).
            #[must_use]
            pub const fn name(self) -> &'static str {
                match self {
                    PluginKey::Unknown => "Unknown",
                    $( PluginKey::$name => stringify!($name), )*
                }
            }
        }
    };
}

plugin_keys! {
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Number0, Number1, Number2, Number3, Number4, Number5, Number6, Number7, Number8, Number9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Space, Enter, Escape, Tab, Backspace, Delete, Insert, Home, End, PageUp, PageDown,
    Up, Down, Left, Right,
    Minus, Equal, LeftBracket, RightBracket, BackSlash, Semicolon, Apostrophe, Comma, Period,
    Slash,
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadDecimal, NumpadDivide, NumpadMultiply, NumpadSubtract, NumpadAdd, NumpadEnter,
    Grave, PrintScreen, Pause,
}

impl PluginKey {
    /// Its number on the wire.
    #[must_use]
    pub const fn code(self) -> u32 {
        self as u32
    }

    /// The key numbered `code`; [`PluginKey::Unknown`] for any other.
    #[must_use]
    pub fn from_code(code: u32) -> Self {
        usize::try_from(code)
            .ok()
            .and_then(|i| Self::ALL.get(i).copied())
            .unwrap_or_default()
    }

    /// The key named `name` (exactly, as OpenAC's `Enum.TryParse`).
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.iter().copied().find(|k| k.name() == name)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `PluginKey`: its numbering (the wire) and names (the file).
    #[test]
    fn keys_keep_openac_numbers_and_names() {
        assert_eq!(PluginKey::A.code(), 1);
        assert_eq!(PluginKey::Number0.code(), 27);
        assert_eq!(PluginKey::F1.code(), 37);
        assert_eq!(PluginKey::Space.code(), 49);
        assert_eq!(PluginKey::Slash.code(), 73);
        assert_eq!(PluginKey::Numpad0.code(), 74);
        assert_eq!(PluginKey::Pause.code(), 92);
        assert_eq!(PluginKey::from_code(92), PluginKey::Pause);
        assert_eq!(PluginKey::from_code(93), PluginKey::Unknown);
        assert_eq!(
            PluginKey::from_name("BackSlash"),
            Some(PluginKey::BackSlash)
        );
        assert_eq!(PluginKey::from_name("j"), None);
    }
}
