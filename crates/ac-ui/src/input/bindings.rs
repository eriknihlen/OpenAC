//! The key bindings: the retail default key map and the player's own
//! bindings file (`keybinds.json`), merged over the defaults action by
//! action.

use std::collections::{BTreeMap, HashSet};
use std::path::Path;

use ac_host::json::{self, Node, Value};

use crate::event::{Key, Modifiers};

use super::action::InputAction;
use super::chord::{
    ActivationType, Binding, InputKey, InputScope, KeyChord, modifiers_name, mouse, parse_modifiers,
};
use super::identity;

use InputAction as A;

/// The bindings file's schema version this code writes.
pub const CURRENT_SCHEMA_VERSION: i32 = 7;

/// The bindings, in insertion order (the first match wins).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct KeyBindings {
    bindings: Vec<Binding>,
}

/// Why a bindings file could not be used.
#[derive(Debug, thiserror::Error)]
pub enum KeyBindingsError {
    /// The file could not be read or written.
    #[error("key bindings file: {0}")]
    Io(#[from] std::io::Error),
    /// The file is not the bindings document.
    #[error("key bindings file: {0}")]
    Json(#[from] json::JsonError),
}

impl KeyBindings {
    /// No bindings.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// All bindings in insertion order.
    #[must_use]
    pub fn all(&self) -> &[Binding] {
        &self.bindings
    }

    /// Append a binding (duplicates allowed; the first match wins).
    pub fn add(&mut self, binding: Binding) {
        self.bindings.push(binding);
    }

    /// Remove the first equal binding; false when there was none.
    pub fn remove(&mut self, binding: &Binding) -> bool {
        match self.bindings.iter().position(|b| b == binding) {
            Some(index) => {
                self.bindings.remove(index);
                true
            }
            None => false,
        }
    }

    /// Drop every binding.
    pub fn clear(&mut self) {
        self.bindings.clear();
    }

    /// The first binding of a chord with an activation, in any scope.
    #[must_use]
    pub fn find(&self, chord: KeyChord, activation: ActivationType) -> Option<Binding> {
        self.bindings
            .iter()
            .find(|b| b.chord == chord && b.activation == activation)
            .copied()
    }

    /// The first binding of a chord with an activation in a scope.
    #[must_use]
    pub fn find_in(
        &self,
        chord: KeyChord,
        activation: ActivationType,
        scope: InputScope,
    ) -> Option<Binding> {
        self.bindings
            .iter()
            .find(|b| b.chord == chord && b.activation == activation && b.scope == scope)
            .copied()
    }

    /// Every binding of an action.
    pub fn for_action(&self, action: InputAction) -> impl Iterator<Item = &Binding> {
        self.bindings.iter().filter(move |b| b.action == action)
    }

    /// The retail default key map (`retail-default.keymap`), plus the
    /// client's own debug actions on Ctrl+F-keys and the right-button orbit.
    #[must_use]
    pub fn retail_defaults() -> Self {
        let mut b = Self::new();
        let k = |key: Key| KeyChord::key(key, Modifiers::NONE);
        let km = KeyChord::key;
        let press = |chord: KeyChord, action: InputAction| Binding::new(chord, action);
        let hold = |chord: KeyChord, action: InputAction| {
            Binding::new(chord, action).activation(ActivationType::Hold)
        };

        // Movement.
        b.add(press(k(Key::W), A::MovementForward));
        b.add(press(k(Key::Up), A::MovementForward));
        b.add(press(k(Key::X), A::MovementBackup));
        b.add(press(k(Key::Down), A::MovementBackup));
        b.add(press(k(Key::A), A::MovementTurnLeft));
        b.add(press(k(Key::Left), A::MovementTurnLeft));
        b.add(press(k(Key::D), A::MovementTurnRight));
        b.add(press(k(Key::Right), A::MovementTurnRight));
        b.add(press(k(Key::Z), A::MovementStrafeLeft));
        b.add(press(km(Key::A, Modifiers::ALT), A::MovementStrafeLeft));
        b.add(press(km(Key::Left, Modifiers::ALT), A::MovementStrafeLeft));
        b.add(press(k(Key::C), A::MovementStrafeRight));
        b.add(press(km(Key::D, Modifiers::ALT), A::MovementStrafeRight));
        b.add(press(
            km(Key::Right, Modifiers::ALT),
            A::MovementStrafeRight,
        ));
        b.add(hold(k(Key::ShiftLeft), A::MovementWalkMode));
        b.add(press(k(Key::Q), A::MovementRunLock));
        b.add(press(k(Key::S), A::MovementStop));
        b.add(press(k(Key::Y), A::Ready));
        b.add(press(k(Key::G), A::Sitting));
        b.add(press(k(Key::H), A::Crouch));
        b.add(press(k(Key::B), A::Sleeping));
        b.add(press(k(Key::Space), A::MovementJump));

        // Item selection.
        b.add(press(k(Key::F), A::SelectionPlaceInInventory));
        b.add(press(k(Key::T), A::SelectionSplitStack));
        b.add(press(k(Key::P), A::SelectionPreviousSelection));
        b.add(press(k(Key::Backspace), A::SelectionClosestCompassItem));
        b.add(press(k(Key::Minus), A::SelectionPreviousCompassItem));
        b.add(press(k(Key::Equal), A::SelectionNextCompassItem));
        b.add(press(k(Key::BackSlash), A::SelectionClosestItem));
        b.add(press(k(Key::LeftBracket), A::SelectionPreviousItem));
        b.add(press(k(Key::RightBracket), A::SelectionNextItem));
        b.add(press(k(Key::Apostrophe), A::SelectionClosestMonster));
        b.add(press(k(Key::L), A::SelectionPreviousMonster));
        b.add(press(k(Key::Semicolon), A::SelectionNextMonster));
        b.add(press(k(Key::Home), A::SelectionLastAttacker));
        b.add(press(k(Key::Slash), A::SelectionClosestPlayer));
        b.add(press(k(Key::Comma), A::SelectionPreviousPlayer));
        b.add(press(k(Key::Period), A::SelectionNextPlayer));
        b.add(press(k(Key::N), A::SelectionPreviousFellow));
        b.add(press(k(Key::M), A::SelectionNextFellow));

        // UI.
        b.add(press(k(Key::E), A::SelectionExamine));
        b.add(press(k(Key::KeypadMultiply), A::CaptureScreenshot));
        b.add(press(k(Key::F1), A::ToggleHelp));
        b.add(press(
            km(Key::F1, Modifiers::SHIFT | Modifiers::CTRL),
            A::TogglePluginManager,
        ));
        b.add(press(k(Key::F3), A::ToggleAllegiancePanel));
        b.add(press(k(Key::F4), A::ToggleFellowshipPanel));
        b.add(press(k(Key::F5), A::ToggleSpellbookPanel));
        b.add(press(k(Key::F6), A::ToggleSpellComponentsPanel));
        b.add(press(k(Key::F8), A::ToggleAttributesPanel));
        b.add(press(k(Key::F9), A::ToggleSkillsPanel));
        b.add(press(k(Key::F10), A::ToggleWorldPanel));
        b.add(press(k(Key::F11), A::ToggleOptionsPanel));
        b.add(press(k(Key::F12), A::ToggleInventoryPanel));
        b.add(press(
            km(Key::Number1, Modifiers::ALT),
            A::ToggleFloatingChatWindow1,
        ));
        b.add(press(
            km(Key::Number2, Modifiers::ALT),
            A::ToggleFloatingChatWindow2,
        ));
        b.add(press(
            km(Key::Number3, Modifiers::ALT),
            A::ToggleFloatingChatWindow3,
        ));
        b.add(press(
            km(Key::Number4, Modifiers::ALT),
            A::ToggleFloatingChatWindow4,
        ));
        b.add(press(k(Key::R), A::UseSelected));
        b.add(press(k(Key::Escape), A::EscapeKey));
        b.add(press(km(Key::Escape, Modifiers::SHIFT), A::LOGOUT));

        // Quick slots: 1..9 bare and with Ctrl use slots 1..9; Alt+1..4
        // are slots 10..13, Alt+5..9 slots 14..18; 0 makes a shortcut.
        const DIGITS: [Key; 9] = [
            Key::Number1,
            Key::Number2,
            Key::Number3,
            Key::Number4,
            Key::Number5,
            Key::Number6,
            Key::Number7,
            Key::Number8,
            Key::Number9,
        ];
        const USE_SLOTS: [InputAction; 9] = [
            A::UseQuickSlot_1,
            A::UseQuickSlot_2,
            A::UseQuickSlot_3,
            A::UseQuickSlot_4,
            A::UseQuickSlot_5,
            A::UseQuickSlot_6,
            A::UseQuickSlot_7,
            A::UseQuickSlot_8,
            A::UseQuickSlot_9,
        ];
        for (key, action) in DIGITS.into_iter().zip(USE_SLOTS) {
            b.add(press(k(key), action));
            b.add(press(km(key, Modifiers::CTRL), action));
        }
        b.add(press(km(Key::Number1, Modifiers::ALT), A::UseQuickSlot_10));
        b.add(press(km(Key::Number2, Modifiers::ALT), A::UseQuickSlot_11));
        b.add(press(km(Key::Number3, Modifiers::ALT), A::UseQuickSlot_12));
        b.add(press(km(Key::Number4, Modifiers::ALT), A::UseQuickSlot_13));
        for (key, action) in DIGITS[4..].iter().zip([
            A::UseQuickSlot_14,
            A::UseQuickSlot_15,
            A::UseQuickSlot_16,
            A::UseQuickSlot_17,
            A::UseQuickSlot_18,
        ]) {
            b.add(press(km(*key, Modifiers::ALT), action));
        }
        b.add(press(k(Key::Number0), A::CreateShortcut));
        b.add(press(km(Key::Number0, Modifiers::CTRL), A::CreateShortcut));

        b.add(press(k(Key::Tab), A::ToggleChatEntry));
        b.add(press(k(Key::Enter), A::EnterChatMode));

        // Combat.
        b.add(press(k(Key::GraveAccent), A::CombatToggleCombat));
        let melee = InputScope::MeleeCombat;
        let missile = InputScope::MissileCombat;
        let magic = InputScope::MagicCombat;
        b.add(press(k(Key::Insert), A::CombatDecreaseAttackPower).scope(melee));
        b.add(press(k(Key::PageUp), A::CombatIncreaseAttackPower).scope(melee));
        b.add(hold(k(Key::Delete), A::CombatLowAttack).scope(melee));
        b.add(hold(k(Key::End), A::CombatMediumAttack).scope(melee));
        b.add(hold(k(Key::PageDown), A::CombatHighAttack).scope(melee));
        b.add(press(k(Key::Insert), A::CombatDecreaseMissileAccuracy).scope(missile));
        b.add(press(k(Key::PageUp), A::CombatIncreaseMissileAccuracy).scope(missile));
        b.add(hold(k(Key::Delete), A::CombatAimLow).scope(missile));
        b.add(hold(k(Key::End), A::CombatAimMedium).scope(missile));
        b.add(hold(k(Key::PageDown), A::CombatAimHigh).scope(missile));
        b.add(press(k(Key::Insert), A::CombatPrevSpellTab).scope(magic));
        b.add(press(k(Key::PageUp), A::CombatNextSpellTab).scope(magic));
        b.add(press(k(Key::Delete), A::CombatPrevSpell).scope(magic));
        b.add(press(k(Key::End), A::CombatCastCurrentSpell).scope(magic));
        b.add(press(k(Key::PageDown), A::CombatNextSpell).scope(magic));
        b.add(press(km(Key::Insert, Modifiers::CTRL), A::CombatFirstSpellTab).scope(magic));
        b.add(press(km(Key::PageUp, Modifiers::CTRL), A::CombatLastSpellTab).scope(magic));
        b.add(press(km(Key::Delete, Modifiers::CTRL), A::CombatFirstSpell).scope(magic));
        b.add(press(km(Key::PageDown, Modifiers::CTRL), A::CombatLastSpell).scope(magic));
        for (key, action) in DIGITS.into_iter().zip([
            A::UseSpellSlot_1,
            A::UseSpellSlot_2,
            A::UseSpellSlot_3,
            A::UseSpellSlot_4,
            A::UseSpellSlot_5,
            A::UseSpellSlot_6,
            A::UseSpellSlot_7,
            A::UseSpellSlot_8,
            A::UseSpellSlot_9,
        ]) {
            b.add(press(k(key), action).scope(magic));
        }

        // Emotes.
        b.add(press(k(Key::U), A::Cry));
        b.add(press(k(Key::I), A::Laugh));
        b.add(press(k(Key::J), A::Wave));
        b.add(press(k(Key::O), A::Cheer));
        b.add(press(k(Key::K), A::PointState));

        // Camera.
        b.add(hold(k(Key::KeypadDivide), A::CameraActivateAlternateMode));
        b.add(hold(k(Key::F2), A::CameraActivateAlternateMode));
        b.add(hold(
            KeyChord::mouse(mouse::MIDDLE, Modifiers::NONE),
            A::CameraInstantMouseLook,
        ));
        b.add(hold(k(Key::Keypad4), A::CameraRotateLeft));
        b.add(hold(k(Key::Keypad6), A::CameraRotateRight));
        b.add(hold(k(Key::Keypad8), A::CameraRotateUp));
        b.add(hold(k(Key::Keypad2), A::CameraRotateDown));
        b.add(hold(k(Key::KeypadSubtract), A::CameraMoveToward));
        b.add(hold(k(Key::KeypadAdd), A::CameraMoveAway));
        b.add(press(k(Key::Keypad0), A::CameraViewDefault));
        b.add(press(k(Key::KeypadDecimal), A::CameraViewFirstPerson));
        b.add(press(k(Key::Keypad5), A::CameraViewLookDown));
        b.add(press(k(Key::KeypadEnter), A::CameraViewMapMode));
        let camera = InputScope::Camera;
        b.add(hold(k(Key::Left), A::CameraAlternateRotateLeft).scope(camera));
        b.add(hold(k(Key::Right), A::CameraAlternateRotateRight).scope(camera));
        b.add(hold(k(Key::Up), A::CameraAlternateRotateUp).scope(camera));
        b.add(hold(k(Key::Down), A::CameraAlternateRotateDown).scope(camera));

        // Mouse selection.
        let m = |button: u8| KeyChord::mouse(button, Modifiers::NONE);
        b.add(press(m(mouse::LEFT), A::SelectLeft));
        b.add(press(m(mouse::RIGHT), A::SelectRight).activation(ActivationType::Click));
        b.add(press(m(mouse::MIDDLE), A::SelectMid));
        b.add(press(m(mouse::LEFT), A::SelectDblLeft).activation(ActivationType::DoubleClick));
        b.add(press(m(mouse::RIGHT), A::SelectDblRight).activation(ActivationType::DoubleClick));
        b.add(press(m(mouse::MIDDLE), A::SelectDblMid).activation(ActivationType::DoubleClick));

        // Scrolling (the wheel is the dispatcher's own).
        b.add(press(km(Key::Up, Modifiers::CTRL), A::ScrollUp));
        b.add(press(km(Key::Down, Modifiers::CTRL), A::ScrollDown));

        // The client's own debug actions.
        b.add(press(
            km(Key::F1, Modifiers::CTRL),
            A::AcdreamToggleDebugPanel,
        ));
        b.add(press(
            km(Key::F2, Modifiers::CTRL),
            A::AcdreamToggleCollisionWires,
        ));
        b.add(press(km(Key::F3, Modifiers::CTRL), A::AcdreamDumpNearby));
        b.add(press(
            km(Key::F7, Modifiers::CTRL),
            A::AcdreamCycleTimeOfDay,
        ));
        b.add(press(
            km(Key::F8, Modifiers::CTRL),
            A::AcdreamSensitivityDown,
        ));
        b.add(press(km(Key::F9, Modifiers::CTRL), A::AcdreamSensitivityUp));
        b.add(press(km(Key::F10, Modifiers::CTRL), A::AcdreamCycleWeather));
        b.add(press(
            km(Key::M, Modifiers::CTRL),
            A::AcdreamToggleAudioMute,
        ));

        b.add(hold(m(mouse::RIGHT), A::AcdreamRmbOrbitHold));
        b
    }

    /// The bindings in `path` merged over the retail defaults: an action
    /// the file lists takes exactly the file's bindings (none, if its list
    /// is empty); every other action keeps its defaults. A missing file is
    /// the defaults; a file that cannot be read or parsed is the defaults
    /// with the reason (the file is left as it is).
    #[must_use]
    pub fn load_or_default(path: &Path) -> (Self, Option<KeyBindingsError>) {
        let bytes = match std::fs::read(path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return (Self::retail_defaults(), None);
            }
            Err(error) => return (Self::retail_defaults(), Some(error.into())),
        };
        match Self::parse(&bytes) {
            Ok(bindings) => (bindings, None),
            Err(error) => (Self::retail_defaults(), Some(error)),
        }
    }

    /// The bindings a bindings document holds, merged over the defaults.
    ///
    /// # Errors
    /// The document is not JSON, or a member that must be a string or a
    /// number is not.
    pub fn parse(bytes: &[u8]) -> Result<Self, KeyBindingsError> {
        let root = json::parse(bytes)?;
        let members = json::object_members(&root)?;
        let member = |name: &str| members.iter().find(|m| m.name == name).map(|m| &m.value);
        let version = match member("version") {
            Some(node) => json::int32(node)?,
            None => 0,
        };
        let defaults = Self::retail_defaults();
        let mut loaded = Self::new();
        let mut stored: HashSet<InputAction> = HashSet::new();

        if let Some(actions) = member("actions").filter(|n| matches!(n.value, Value::Object(_))) {
            for action_member in json::object_members(actions)? {
                let Some(mut action) = InputAction::from_name(&action_member.name) else {
                    continue;
                };
                let Value::Array(items) = &action_member.value.value else {
                    continue;
                };
                stored.insert(action);
                for item in items {
                    let fields = json::object_members(item)?;
                    let field =
                        |name: &str| fields.iter().find(|m| m.name == name).map(|m| &m.value);
                    let Some(key_node) = field("key") else {
                        continue;
                    };
                    let Some(key_name) = string_value(key_node)? else {
                        continue;
                    };
                    let Some(key) = InputKey::from_name(&key_name) else {
                        continue;
                    };
                    let modifiers = match field("mod").map(|n| &n.value) {
                        Some(Value::String(text)) if !text.is_empty() => parse_modifiers(text),
                        _ => Modifiers::NONE,
                    };
                    let activation = match field("activation").map(|n| &n.value) {
                        Some(Value::String(text)) => {
                            ActivationType::from_name(text).unwrap_or_default()
                        }
                        _ => ActivationType::Press,
                    };
                    let device = match field("device") {
                        Some(
                            node @ Node {
                                value: Value::Number(_),
                                ..
                            },
                        ) => json::int32(node)? as u8,
                        _ => 0,
                    };
                    let chord = KeyChord {
                        key,
                        modifiers,
                        device,
                    };
                    action = migrate_quick_slot_intent(version, action, chord, activation);
                    stored.insert(action);
                    let activation = migrate_combat_attack_activation(version, action, activation);
                    let activation = migrate_select_right_activation(version, action, activation);
                    let mut scope = defaults
                        .for_action(action)
                        .map(|b| b.scope)
                        .next()
                        .unwrap_or(InputScope::Game);
                    if let Some(Value::String(text)) = field("scope").map(|n| &n.value)
                        && let Some(parsed) = InputScope::from_name(text)
                    {
                        scope = parsed;
                    }
                    loaded.add(Binding {
                        chord,
                        action,
                        activation,
                        scope,
                    });
                }
            }
        }

        for &action in InputAction::ALL {
            if !stored.contains(&action) {
                for binding in defaults.for_action(action) {
                    loaded.add(*binding);
                }
            }
        }
        Ok(loaded)
    }

    /// The bindings document: every retail action listed (an empty list
    /// for one with no binding, so it stays unbound), names sorted.
    #[must_use]
    pub fn to_json(&self) -> String {
        let mut actions: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for &(_, action) in identity::all() {
            actions.entry(action.name().to_owned()).or_default();
        }
        for binding in &self.bindings {
            let mut entry: BTreeMap<&str, String> = BTreeMap::new();
            entry.insert("key", quoted(&binding.chord.key.name()));
            if binding.chord.modifiers != Modifiers::NONE {
                entry.insert("mod", quoted(&modifiers_name(binding.chord.modifiers)));
            }
            if binding.chord.device != 0 {
                entry.insert("device", binding.chord.device.to_string());
            }
            if binding.activation != ActivationType::Press {
                entry.insert("activation", quoted(binding.activation.name()));
            }
            if binding.scope != InputScope::Game {
                entry.insert("scope", quoted(binding.scope.name()));
            }
            let fields: Vec<String> = entry
                .iter()
                .map(|(name, value)| format!("{}: {value}", quoted(name)))
                .collect();
            actions
                .entry(binding.action.name().to_owned())
                .or_default()
                .push(format!("{{ {} }}", fields.join(", ")));
        }
        let mut out = String::from("{\n  \"actions\": {\n");
        let count = actions.len();
        for (i, (name, entries)) in actions.iter().enumerate() {
            out.push_str("    ");
            out.push_str(&quoted(name));
            if entries.is_empty() {
                out.push_str(": []");
            } else {
                out.push_str(": [\n");
                for (j, entry) in entries.iter().enumerate() {
                    out.push_str("      ");
                    out.push_str(entry);
                    out.push_str(if j + 1 < entries.len() { ",\n" } else { "\n" });
                }
                out.push_str("    ]");
            }
            out.push_str(if i + 1 < count { ",\n" } else { "\n" });
        }
        out.push_str("  },\n");
        out.push_str(&format!("  \"version\": {CURRENT_SCHEMA_VERSION}\n}}\n"));
        out
    }

    /// Write the bindings document to `path` (creating its directory).
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

fn quoted(text: &str) -> String {
    let mut out = String::new();
    json::write_string(&mut out, text);
    out
}

/// A string member (`None` for `null`); another type is an error, as the
/// file reader of the original fails the whole file on it.
fn string_value(node: &Node) -> Result<Option<String>, KeyBindingsError> {
    Ok(json::string_or_null(node)?)
}

/// Before schema 6 a Ctrl+digit binding was stored as the select-slot
/// action; the game uses the slot.
fn migrate_quick_slot_intent(
    version: i32,
    action: InputAction,
    chord: KeyChord,
    activation: ActivationType,
) -> InputAction {
    if version >= 6
        || activation != ActivationType::Press
        || chord.device != 0
        || chord.modifiers != Modifiers::CTRL
    {
        return action;
    }
    const SELECT: [InputAction; 9] = [
        A::SelectQuickSlot_1,
        A::SelectQuickSlot_2,
        A::SelectQuickSlot_3,
        A::SelectQuickSlot_4,
        A::SelectQuickSlot_5,
        A::SelectQuickSlot_6,
        A::SelectQuickSlot_7,
        A::SelectQuickSlot_8,
        A::SelectQuickSlot_9,
    ];
    const USE: [InputAction; 9] = [
        A::UseQuickSlot_1,
        A::UseQuickSlot_2,
        A::UseQuickSlot_3,
        A::UseQuickSlot_4,
        A::UseQuickSlot_5,
        A::UseQuickSlot_6,
        A::UseQuickSlot_7,
        A::UseQuickSlot_8,
        A::UseQuickSlot_9,
    ];
    const DIGITS: [Key; 9] = [
        Key::Number1,
        Key::Number2,
        Key::Number3,
        Key::Number4,
        Key::Number5,
        Key::Number6,
        Key::Number7,
        Key::Number8,
        Key::Number9,
    ];
    let Some(offset) = SELECT.iter().position(|a| *a == action) else {
        return action;
    };
    if chord.key == InputKey::Key(DIGITS[offset]) {
        USE[offset]
    } else {
        action
    }
}

/// Before schema 3 the melee attack heights were presses; they are holds.
fn migrate_combat_attack_activation(
    version: i32,
    action: InputAction,
    activation: ActivationType,
) -> ActivationType {
    if version >= 3 || activation != ActivationType::Press {
        return activation;
    }
    if matches!(
        action,
        A::CombatLowAttack | A::CombatMediumAttack | A::CombatHighAttack
    ) {
        ActivationType::Hold
    } else {
        activation
    }
}

/// Before schema 5 the right-button select was a press; it is a complete
/// click.
fn migrate_select_right_activation(
    version: i32,
    action: InputAction,
    activation: ActivationType,
) -> ActivationType {
    if version < 5 && action == A::SelectRight && activation == ActivationType::Press {
        ActivationType::Click
    } else {
        activation
    }
}

#[cfg(test)]
#[path = "bindings_tests.rs"]
mod tests;
