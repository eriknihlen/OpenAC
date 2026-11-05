//! Plugin hotkeys as session state (OpenAC `AppHotkeyRegistry`, scoped by
//! `ScopedPluginHost`).
//!
//! A plugin registers a chord under its own id; the registry keeps it,
//! scoped as `<plugin id>:<id>`, whether or not a window is attached. A chord
//! binds only while a window is attached and has said which chords its own
//! key map uses (OpenAC binds once the keyboard and the client's bindings
//! exist, and a host with no keyboard binds nothing): one that collides
//! with a client binding or with another plugin's bound chord is refused,
//! first come first bound, and a refused one binds by itself once the chord
//! is free. A stored override (the player's rebind, kept under the scoped
//! id) replaces the plugin's default. The window keeps a copy of the bound
//! table and reports a pressed chord ([`matching`] decides which), and the
//! owning plugin hears it.

use std::collections::BTreeMap;

/// The highest key number a plugin can bind (OpenAC `PluginKey.Pause`; the
/// numbering is the plugin ABI's `PluginKey`). 0 is no key.
pub const LAST_KEY: u32 = 92;

/// The key numbered `code` is one a hotkey can bind to (OpenAC `MapKey`
/// has no key for `Unknown` or a number outside the enum).
#[must_use]
pub const fn is_bindable(code: u32) -> bool {
    code != 0 && code <= LAST_KEY
}

/// A key and the modifiers it needs (OpenAC `PluginKeyChord`); Ctrl, Alt
/// and Shift are side-independent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Chord {
    /// The key: OpenAC's `PluginKey` number (0: none).
    pub key: u32,
    /// Ctrl held.
    pub ctrl: bool,
    /// Alt held.
    pub alt: bool,
    /// Shift held.
    pub shift: bool,
}

impl Chord {
    /// A bare key.
    #[must_use]
    pub const fn key(key: u32) -> Self {
        Self {
            key,
            ctrl: false,
            alt: false,
            shift: false,
        }
    }

    /// The same key with Ctrl.
    #[must_use]
    pub const fn with_ctrl(self) -> Self {
        Self { ctrl: true, ..self }
    }
}

/// Every key's name by its number (OpenAC `PluginKey`'s member names; the
/// plugin ABI's `PluginKey` has the same list): what a `<hotkey>` box's
/// text spells a key as.
pub const KEY_NAMES: [&str; 93] = [
    "Unknown",
    "A",
    "B",
    "C",
    "D",
    "E",
    "F",
    "G",
    "H",
    "I",
    "J",
    "K",
    "L",
    "M",
    "N",
    "O",
    "P",
    "Q",
    "R",
    "S",
    "T",
    "U",
    "V",
    "W",
    "X",
    "Y",
    "Z",
    "Number0",
    "Number1",
    "Number2",
    "Number3",
    "Number4",
    "Number5",
    "Number6",
    "Number7",
    "Number8",
    "Number9",
    "F1",
    "F2",
    "F3",
    "F4",
    "F5",
    "F6",
    "F7",
    "F8",
    "F9",
    "F10",
    "F11",
    "F12",
    "Space",
    "Enter",
    "Escape",
    "Tab",
    "Backspace",
    "Delete",
    "Insert",
    "Home",
    "End",
    "PageUp",
    "PageDown",
    "Up",
    "Down",
    "Left",
    "Right",
    "Minus",
    "Equal",
    "LeftBracket",
    "RightBracket",
    "BackSlash",
    "Semicolon",
    "Apostrophe",
    "Comma",
    "Period",
    "Slash",
    "Numpad0",
    "Numpad1",
    "Numpad2",
    "Numpad3",
    "Numpad4",
    "Numpad5",
    "Numpad6",
    "Numpad7",
    "Numpad8",
    "Numpad9",
    "NumpadDecimal",
    "NumpadDivide",
    "NumpadMultiply",
    "NumpadSubtract",
    "NumpadAdd",
    "NumpadEnter",
    "Grave",
    "PrintScreen",
    "Pause",
];

/// The modifiers as a `<hotkey>` box writes them: `Ctrl+`, `Alt+`,
/// `Shift+`, in that order.
fn push_modifiers(text: &mut String, ctrl: bool, alt: bool, shift: bool) {
    if ctrl {
        text.push_str("Ctrl+");
    }
    if alt {
        text.push_str("Alt+");
    }
    if shift {
        text.push_str("Shift+");
    }
}

impl Chord {
    /// The text a `<hotkey>` box holds for this chord (OpenAC
    /// `PluginKeyChord.ToHotkeyText`): the modifiers in the order Ctrl, Alt,
    /// Shift, then the key's name; empty when it has no key a hotkey can
    /// bind.
    #[must_use]
    pub fn to_hotkey_text(self) -> String {
        if !is_bindable(self.key) {
            return String::new();
        }
        let mut text = String::new();
        push_modifiers(&mut text, self.ctrl, self.alt, self.shift);
        text.push_str(KEY_NAMES[self.key as usize]);
        text
    }

    /// Reads a `<hotkey>` box's text (OpenAC
    /// `PluginKeyChord.TryParseHotkeyText`): modifiers in any order and any
    /// case, each once, then a key's name in any case. `None` for empty
    /// text (an unbound box) and for text it cannot read.
    #[must_use]
    pub fn parse_hotkey_text(text: &str) -> Option<Self> {
        if text.is_empty() {
            return None;
        }
        let parts: Vec<&str> = text.split('+').collect();
        let (name, modifiers) = parts.split_last()?;
        let mut chord = Self::default();
        for part in modifiers {
            if !chord.ctrl && part.eq_ignore_ascii_case("Ctrl") {
                chord.ctrl = true;
            } else if !chord.alt && part.eq_ignore_ascii_case("Alt") {
                chord.alt = true;
            } else if !chord.shift && part.eq_ignore_ascii_case("Shift") {
                chord.shift = true;
            } else {
                return None;
            }
        }
        chord.key = key_named(name)?;
        Some(chord)
    }

    /// What a `<hotkey>` box shows (OpenAC `MarkupHotkeyText.Describe`):
    /// the modifiers as Ctrl, Alt and Shift, then the key by `key_name`.
    #[must_use]
    pub fn describe(self, key_name: impl FnOnce(u32) -> String) -> String {
        let mut text = String::new();
        push_modifiers(&mut text, self.ctrl, self.alt, self.shift);
        text.push_str(&key_name(self.key));
        text
    }
}

/// The key a name in any case stands for: a letter first, then letters and
/// digits, naming a key a hotkey can bind (not `Unknown`).
#[must_use]
pub fn key_named(name: &str) -> Option<u32> {
    let first = name.chars().next()?;
    if !first.is_ascii_alphabetic() || !name.chars().all(|c| c.is_ascii_alphanumeric()) {
        return None;
    }
    KEY_NAMES
        .iter()
        .position(|k| k.eq_ignore_ascii_case(name))
        .and_then(|i| u32::try_from(i).ok())
        .filter(|code| is_bindable(*code))
}

/// How a registration stands (OpenAC `IPluginHotkeyRegistration.IsBound`,
/// `EffectiveChord`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct HotkeyState {
    /// It fires: a window is attached and nothing else holds its chord.
    pub is_bound: bool,
    /// The chord it binds to: a stored override, else the plugin's default;
    /// the empty chord while no window is attached (OpenAC's headless
    /// registration).
    pub effective: Chord,
}

/// One bound hotkey, as the window keeps it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoundHotkey {
    /// The plugin.
    pub owner: String,
    /// The plugin's own id for it.
    pub id: String,
    /// The chord.
    pub chord: Chord,
}

#[derive(Debug, Clone)]
struct Entry {
    owner: String,
    id: String,
    display_name: String,
    default: Chord,
    effective: Chord,
    bound: bool,
}

impl Entry {
    fn scoped(&self) -> String {
        scoped_id(&self.owner, &self.id)
    }
}

/// The id overrides are stored under: `<plugin id>:<id>` (OpenAC
/// `ScopedPluginHost.Hotkeys`).
#[must_use]
pub fn scoped_id(owner: &str, id: &str) -> String {
    format!("{owner}:{id}")
}

/// The registry. See the module documentation.
#[derive(Debug, Default)]
pub struct Hotkeys {
    entries: Vec<Entry>,
    overrides: BTreeMap<String, Chord>,
    /// The chords the attached window's own key map uses; `None` with no
    /// window (nothing binds).
    client: Option<Vec<Chord>>,
    overrides_changed: bool,
}

impl Hotkeys {
    /// The stored overrides (read from the overrides file at the session's
    /// start), by scoped id.
    pub fn set_overrides(&mut self, overrides: BTreeMap<String, Chord>) {
        self.overrides = overrides;
        self.resolve_all();
    }

    /// The overrides as they stand, by scoped id.
    #[must_use]
    pub fn overrides(&self) -> &BTreeMap<String, Chord> {
        &self.overrides
    }

    /// Whether a rebind changed the overrides since the last call (the
    /// session writes the file).
    pub fn take_overrides_changed(&mut self) -> bool {
        std::mem::take(&mut self.overrides_changed)
    }

    /// A window attached and said which chords its key map uses (`Some`),
    /// or the window left (`None`): every registration is resolved again,
    /// in registration order (OpenAC `Bind`).
    pub fn set_client_chords(&mut self, chords: Option<Vec<Chord>>) {
        self.client = chords;
        self.resolve_all();
    }

    /// Registers a hotkey for plugin `owner` (OpenAC `Register`): a second
    /// registration under the same id replaces the first, which frees its
    /// chord before the new one resolves.
    ///
    /// # Errors
    /// A blank id or display name.
    pub fn register(
        &mut self,
        owner: &str,
        id: &str,
        display_name: &str,
        default: Chord,
    ) -> Result<HotkeyState, String> {
        if id.trim().is_empty() {
            return Err("a hotkey needs an id".to_owned());
        }
        if display_name.trim().is_empty() {
            return Err("a hotkey needs a display name".to_owned());
        }
        self.entries.retain(|e| !(e.owner == owner && e.id == id));
        self.entries.push(Entry {
            owner: owner.to_owned(),
            id: id.to_owned(),
            display_name: display_name.to_owned(),
            default,
            effective: default,
            bound: false,
        });
        let index = self.entries.len() - 1;
        if self.client.is_some() {
            self.resolve(index);
            self.resolve_unbound(Some(index));
        }
        Ok(self.state_at(index))
    }

    /// Revokes one of `owner`'s hotkeys; false when it has none by that id.
    /// A registration refused for the chord binds now if it is free.
    pub fn unregister(&mut self, owner: &str, id: &str) -> bool {
        let before = self.entries.len();
        self.entries.retain(|e| !(e.owner == owner && e.id == id));
        let removed = self.entries.len() != before;
        if removed {
            self.resolve_unbound(None);
        }
        removed
    }

    /// Every hotkey `owner` registered goes (it was unloaded).
    pub fn release(&mut self, owner: &str) {
        let before = self.entries.len();
        self.entries.retain(|e| e.owner != owner);
        if self.entries.len() != before {
            self.resolve_unbound(None);
        }
    }

    /// Rebinds one of `owner`'s hotkeys (OpenAC `Registration.Rebind` and
    /// `SetOverride`): the chord is stored as the player's override under
    /// the scoped id, and everything is resolved again. With no window
    /// there is nothing to bind to and nothing changes (OpenAC's headless
    /// registration); `None` when the plugin has no such hotkey.
    pub fn rebind(&mut self, owner: &str, id: &str, chord: Chord) -> Option<HotkeyState> {
        let index = self
            .entries
            .iter()
            .position(|e| e.owner == owner && e.id == id)?;
        if self.client.is_none() {
            return Some(self.state_at(index));
        }
        self.overrides.insert(scoped_id(owner, id), chord);
        self.overrides_changed = true;
        self.resolve(index);
        self.resolve_unbound(None);
        Some(self.state_at(index))
    }

    /// How one of `owner`'s hotkeys stands.
    #[must_use]
    pub fn state(&self, owner: &str, id: &str) -> Option<HotkeyState> {
        let index = self
            .entries
            .iter()
            .position(|e| e.owner == owner && e.id == id)?;
        Some(self.state_at(index))
    }

    /// The display name a hotkey was registered with.
    #[must_use]
    pub fn display_name(&self, owner: &str, id: &str) -> Option<&str> {
        self.entries
            .iter()
            .find(|e| e.owner == owner && e.id == id)
            .map(|e| e.display_name.as_str())
    }

    /// The bound hotkeys, in registration order: what the window keeps.
    #[must_use]
    pub fn bound(&self) -> Vec<BoundHotkey> {
        self.entries
            .iter()
            .filter(|e| e.bound)
            .map(|e| BoundHotkey {
                owner: e.owner.clone(),
                id: e.id.clone(),
                chord: e.effective,
            })
            .collect()
    }

    /// Whether `owner`'s hotkey `id` is bound now (a press the window
    /// reports for anything else is dropped).
    #[must_use]
    pub fn is_bound(&self, owner: &str, id: &str) -> bool {
        self.entries
            .iter()
            .any(|e| e.owner == owner && e.id == id && e.bound)
    }

    fn state_at(&self, index: usize) -> HotkeyState {
        let entry = &self.entries[index];
        if self.client.is_none() {
            return HotkeyState::default();
        }
        HotkeyState {
            is_bound: entry.bound,
            effective: entry.effective,
        }
    }

    fn resolve_all(&mut self) {
        for entry in &mut self.entries {
            entry.bound = false;
        }
        for index in 0..self.entries.len() {
            self.resolve(index);
        }
    }

    /// OpenAC `Resolve`: the override or the default; bound when a window
    /// has a key map, the key is a real one, and neither the client nor
    /// another plugin's bound hotkey uses the chord.
    fn resolve(&mut self, index: usize) {
        let scoped = self.entries[index].scoped();
        let effective = self
            .overrides
            .get(&scoped)
            .copied()
            .unwrap_or(self.entries[index].default);
        let bound =
            match &self.client {
                None => false,
                Some(client) => {
                    is_bindable(effective.key)
                        && !client.contains(&effective)
                        && !self.entries.iter().enumerate().any(|(i, other)| {
                            i != index && other.bound && other.effective == effective
                        })
                }
            };
        let entry = &mut self.entries[index];
        entry.effective = effective;
        entry.bound = bound;
    }

    /// OpenAC `ReResolveUnboundEntries`: after a chord was freed, every
    /// registration still refused (but `skip`) tries again, in order.
    fn resolve_unbound(&mut self, skip: Option<usize>) {
        if self.client.is_none() {
            return;
        }
        for index in 0..self.entries.len() {
            if Some(index) != skip && !self.entries[index].bound {
                self.resolve(index);
            }
        }
    }
}

/// Why a key press may not reach the plugins' hotkeys (OpenAC
/// `AppHotkeyRegistry.OnKeyDown`'s gates).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct HotkeyGate {
    /// The key configuration screen is capturing the next key.
    pub capturing: bool,
    /// A modal dialog or an edit field has the keys.
    pub modal: bool,
    /// The chat entry has the keyboard: only chords with Ctrl or Alt fire.
    pub chat_focused: bool,
}

/// The bound hotkeys a press of `chord` fires, in registration order
/// (OpenAC `OnKeyDown`: modifiers must match exactly; nothing fires during a
/// capture or under a modal scope; with the chat entry focused only a
/// chord with Ctrl or Alt fires).
#[must_use]
pub fn matching(table: &[BoundHotkey], chord: Chord, gate: HotkeyGate) -> Vec<&BoundHotkey> {
    if gate.capturing || gate.modal || !is_bindable(chord.key) {
        return Vec::new();
    }
    table
        .iter()
        .filter(|h| h.chord == chord)
        .filter(|h| !gate.chat_focused || h.chord.ctrl || h.chord.alt)
        .collect()
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Input/AppHotkeyRegistryTests.cs` and
    //! `tests/AcDream.Core.Tests/Plugins/ScopedHotkeyRegistryTests.cs`; the
    //! keyboard is the window's report of a press, the client bindings the
    //! chords its key map uses.
    use super::*;

    /// OpenAC `PluginKey` numbers of the keys these tests press.
    mod keys {
        pub const H: u32 = 8;
        pub const J: u32 = 10;
        pub const F9: u32 = 45;
        pub const F10: u32 = 46;
        pub const ESCAPE: u32 = 51;
        pub const NUMPAD5: u32 = 79;
        pub const NUMPAD_ENTER: u32 = 89;
        pub const GRAVE: u32 = 90;
        pub const PRINT_SCREEN: u32 = 91;
        pub const PAUSE: u32 = 92;
    }

    const ESCAPE: Chord = Chord::key(keys::ESCAPE);
    const F9: Chord = Chord::key(keys::F9);
    const F10: Chord = Chord::key(keys::F10);

    fn attached() -> Hotkeys {
        let mut hotkeys = Hotkeys::default();
        hotkeys.set_client_chords(Some(Vec::new()));
        hotkeys
    }

    fn fires(hotkeys: &Hotkeys, chord: Chord, gate: HotkeyGate) -> Vec<String> {
        matching(&hotkeys.bound(), chord, gate)
            .into_iter()
            .map(|h| scoped_id(&h.owner, &h.id))
            .collect()
    }

    fn press(hotkeys: &Hotkeys, chord: Chord) -> Vec<String> {
        fires(hotkeys, chord, HotkeyGate::default())
    }

    #[test]
    fn a_registration_before_a_window_resolves_once_one_attaches() {
        let mut hotkeys = Hotkeys::default();
        let state = hotkeys.register("p", "test", "Test", F9).unwrap();
        assert!(!state.is_bound, "no keyboard yet");
        assert_eq!(
            state.effective,
            Chord::default(),
            "headless reports no chord"
        );
        hotkeys.set_client_chords(Some(Vec::new()));
        assert!(hotkeys.state("p", "test").unwrap().is_bound);
        assert_eq!(press(&hotkeys, F9), ["p:test"]);
        // Attaching again (bind is idempotent) does not fire twice.
        hotkeys.set_client_chords(Some(Vec::new()));
        assert_eq!(press(&hotkeys, F9), ["p:test"]);
        // The window leaves: nothing is bound.
        hotkeys.set_client_chords(None);
        assert!(!hotkeys.state("p", "test").unwrap().is_bound);
        assert!(press(&hotkeys, F9).is_empty());
    }

    #[test]
    fn a_registration_colliding_with_a_client_binding_is_refused() {
        let mut hotkeys = Hotkeys::default();
        hotkeys.set_client_chords(Some(vec![ESCAPE]));
        let state = hotkeys
            .register("p", "colliding", "Colliding", ESCAPE)
            .unwrap();
        assert!(!state.is_bound);
        assert!(press(&hotkeys, ESCAPE).is_empty());
    }

    #[test]
    fn revoking_the_registration_stops_it_firing() {
        let mut hotkeys = attached();
        hotkeys.register("p", "test", "Test", F9).unwrap();
        assert!(hotkeys.unregister("p", "test"));
        assert!(!hotkeys.unregister("p", "test"));
        assert!(press(&hotkeys, F9).is_empty());
    }

    #[test]
    fn a_plain_chord_does_not_fire_while_the_chat_bar_has_focus() {
        let mut hotkeys = attached();
        hotkeys.register("p", "plain", "Plain", F9).unwrap();
        hotkeys
            .register("p", "ctrl", "Ctrl", F10.with_ctrl())
            .unwrap();
        let chat = HotkeyGate {
            chat_focused: true,
            ..HotkeyGate::default()
        };
        assert!(fires(&hotkeys, F9, chat).is_empty());
        assert_eq!(fires(&hotkeys, F10.with_ctrl(), chat), ["p:ctrl"]);
    }

    #[test]
    fn a_second_registration_for_the_same_id_replaces_the_first() {
        let mut hotkeys = attached();
        assert!(
            hotkeys
                .register("p", "same-id", "First", F9)
                .unwrap()
                .is_bound
        );
        let second = hotkeys.register("p", "same-id", "Second", F10).unwrap();
        assert!(second.is_bound);
        assert_eq!(hotkeys.display_name("p", "same-id"), Some("Second"));
        assert!(press(&hotkeys, F9).is_empty());
        assert_eq!(press(&hotkeys, F10), ["p:same-id"]);
    }

    #[test]
    fn a_chord_another_plugin_holds_is_refused_until_it_is_freed() {
        let mut hotkeys = attached();
        let heal = Chord::key(keys::H).with_ctrl();
        assert!(
            hotkeys
                .register("plugin-a", "heal", "Heal", heal)
                .unwrap()
                .is_bound
        );
        assert!(
            !hotkeys
                .register("plugin-b", "heal", "Heal", heal)
                .unwrap()
                .is_bound
        );
        assert_eq!(press(&hotkeys, heal), ["plugin-a:heal"]);
        hotkeys.unregister("plugin-a", "heal");
        assert!(hotkeys.state("plugin-b", "heal").unwrap().is_bound);
        assert!(
            !hotkeys
                .register("plugin-c", "heal", "Heal", heal)
                .unwrap()
                .is_bound
        );
        // Unloading the holder frees it too.
        hotkeys.release("plugin-b");
        assert!(hotkeys.state("plugin-c", "heal").unwrap().is_bound);
    }

    #[test]
    fn a_rebind_is_stored_under_the_scoped_id_and_survives_a_restart() {
        let mut hotkeys = attached();
        let heal = Chord::key(keys::H).with_ctrl();
        let moved = Chord::key(keys::J).with_ctrl();
        assert_eq!(
            hotkeys
                .register("p", "quick-heal", "Quick Heal", heal)
                .unwrap()
                .effective,
            heal
        );
        assert_eq!(
            hotkeys.rebind("p", "quick-heal", moved).unwrap().effective,
            moved
        );
        assert!(hotkeys.take_overrides_changed());
        assert_eq!(press(&hotkeys, moved), ["p:quick-heal"]);
        assert!(press(&hotkeys, heal).is_empty());
        // A fresh registry with the stored overrides takes the override at
        // registration, not the plugin's default.
        let mut restarted = attached();
        restarted.set_overrides(hotkeys.overrides().clone());
        assert_eq!(
            restarted
                .register("p", "quick-heal", "Quick Heal", heal)
                .unwrap()
                .effective,
            moved
        );
        // The override is scoped: another plugin's same id is untouched.
        assert_eq!(
            restarted
                .register("q", "quick-heal", "Quick Heal", heal)
                .unwrap()
                .effective,
            heal
        );
    }

    #[test]
    fn nothing_fires_while_a_capture_or_a_modal_scope_has_the_keys() {
        let mut hotkeys = attached();
        let heal = Chord::key(keys::H).with_ctrl();
        hotkeys
            .register("p", "quick-heal", "Quick Heal", heal)
            .unwrap();
        let capturing = HotkeyGate {
            capturing: true,
            ..HotkeyGate::default()
        };
        let modal = HotkeyGate {
            modal: true,
            ..HotkeyGate::default()
        };
        assert!(fires(&hotkeys, heal, capturing).is_empty());
        assert!(fires(&hotkeys, heal, modal).is_empty());
        assert_eq!(press(&hotkeys, heal), ["p:quick-heal"]);
    }

    #[test]
    fn numpad_grave_and_system_keys_bind_and_fire() {
        for key in [
            keys::NUMPAD5,
            keys::NUMPAD_ENTER,
            keys::GRAVE,
            keys::PRINT_SCREEN,
            keys::PAUSE,
        ] {
            let mut hotkeys = attached();
            assert!(
                hotkeys
                    .register("p", "test", "Test", Chord::key(key))
                    .unwrap()
                    .is_bound
            );
            assert_eq!(press(&hotkeys, Chord::key(key)), ["p:test"]);
        }
    }

    #[test]
    fn an_unknown_key_is_never_bound() {
        let mut hotkeys = attached();
        assert!(
            !hotkeys
                .register("p", "test", "Test", Chord::default())
                .unwrap()
                .is_bound
        );
    }

    #[test]
    fn a_rebind_with_no_window_changes_nothing() {
        let mut hotkeys = Hotkeys::default();
        hotkeys.register("p", "test", "Test", F9).unwrap();
        assert_eq!(
            hotkeys.rebind("p", "test", F10),
            Some(HotkeyState::default())
        );
        assert!(!hotkeys.take_overrides_changed());
        assert!(hotkeys.overrides().is_empty());
        assert_eq!(hotkeys.rebind("p", "other", F10), None);
    }

    #[test]
    fn blank_ids_and_names_are_refused() {
        let mut hotkeys = attached();
        assert!(hotkeys.register("p", " ", "Test", F9).is_err());
        assert!(hotkeys.register("p", "test", "", F9).is_err());
    }

    // ── Hotkey text: OpenAC `tests/AcDream.Plugin.Tests/PluginKeyChordTextTests.cs`
    // and `tests/AcDream.App.Tests/UI/MarkupHotkeyTextTests.cs`.

    fn chord(key: u32, ctrl: bool, alt: bool, shift: bool) -> Chord {
        Chord {
            key,
            ctrl,
            alt,
            shift,
        }
    }

    #[test]
    fn hotkey_text_reads_a_hotkey_box_string() {
        // PluginKeyChordTextTests.TryParseHotkeyText_ReadsAHotkeyBoxString
        for (text, expected) in [
            ("Ctrl+Alt+Shift+F", chord(6, true, true, true)),
            (
                "shift+ctrl+numpad5",
                chord(keys::NUMPAD5, true, false, true),
            ),
            ("Numpad5", Chord::key(keys::NUMPAD5)),
            ("Grave", Chord::key(keys::GRAVE)),
            ("NumpadEnter", Chord::key(keys::NUMPAD_ENTER)),
            ("F12", Chord::key(48)),
        ] {
            assert_eq!(Chord::parse_hotkey_text(text), Some(expected), "{text}");
        }
    }

    #[test]
    fn hotkey_text_refuses_what_no_plugin_hotkey_can_hold() {
        // PluginKeyChordTextTests.TryParseHotkeyText_RefusesWhatNoPluginHotkeyCanHold,
        // MarkupHotkeyTextTests.Parse_UnreadableIsUnbound
        for text in [
            "",
            "F13",
            "CapsLock",
            "Keypad5",
            "GraveAccent",
            "Ctrl+",
            "+F",
            "Ctrl+Ctrl+F",
            "Meta+F",
            "5",
            "A,B",
            "Unknown",
            "Ctrl+Nope",
            "ShiftLeft",
        ] {
            assert_eq!(Chord::parse_hotkey_text(text), None, "{text}");
        }
    }

    #[test]
    fn hotkey_text_spells_the_key_by_its_name() {
        // PluginKeyChordTextTests.ToHotkeyText_SpellsTheKeyAsItsPluginKeyName,
        // MarkupHotkeyTextTests.Format_SpellsTheKeyAsItsPluginKeyName
        assert_eq!(
            chord(6, true, true, true).to_hotkey_text(),
            "Ctrl+Alt+Shift+F"
        );
        assert_eq!(Chord::key(keys::NUMPAD5).to_hotkey_text(), "Numpad5");
        assert_eq!(Chord::key(keys::GRAVE).to_hotkey_text(), "Grave");
        assert_eq!(
            chord(keys::NUMPAD_ENTER, false, true, false).to_hotkey_text(),
            "Alt+NumpadEnter"
        );
        assert_eq!(
            chord(28, false, true, false).to_hotkey_text(),
            "Alt+Number1"
        );
        // ToHotkeyText_NoKeyIsEmpty
        assert_eq!(Chord::default().to_hotkey_text(), "");
        assert_eq!(chord(999, true, false, false).to_hotkey_text(), "");
    }

    #[test]
    fn every_key_round_trips_with_every_modifier_set() {
        // PluginKeyChordTextTests.EveryPluginKey_RoundTripsWithEveryModifierSet,
        // MarkupHotkeyTextTests.Parse_ReadsAndRoundTripsToCanonical
        for key in 1..=LAST_KEY {
            for mods in 0..8 {
                let c = chord(key, mods & 1 != 0, mods & 2 != 0, mods & 4 != 0);
                let text = c.to_hotkey_text();
                assert_eq!(Chord::parse_hotkey_text(&text), Some(c), "{text}");
            }
        }
        let read = Chord::parse_hotkey_text("shift+ctrl+f").unwrap();
        assert_eq!(read.to_hotkey_text(), "Ctrl+Shift+F");
        assert_eq!(
            Chord::parse_hotkey_text("Ctrl+grave")
                .unwrap()
                .to_hotkey_text(),
            "Ctrl+Grave"
        );
    }

    #[test]
    fn describe_names_modifiers_plainly_and_the_key_by_the_game() {
        // MarkupHotkeyTextTests.Describe_names_modifiers_plainly_and_only_the_key_by_the_game
        let mut asked = Vec::new();
        let text = chord(6, true, true, true).describe(|k| {
            asked.push(k);
            "F-KEY".to_owned()
        });
        assert_eq!(text, "Ctrl+Alt+Shift+F-KEY");
        assert_eq!(asked, [6]);
        assert_eq!(
            Chord::key(keys::NUMPAD5).describe(|_| "NUMPAD 5".to_owned()),
            "NUMPAD 5"
        );
    }
}
