//! Retail `.keymap` files (the game's own key map format, as the retail
//! Configure Keyboard screen saves them) and the store of named key map
//! profiles beside the dats, with the active one remembered.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::io;
use std::path::Path;
#[cfg(not(target_arch = "wasm32"))]
use std::path::PathBuf;
use std::sync::OnceLock;

use crate::event::Modifiers;
#[cfg(not(target_arch = "wasm32"))]
use crate::storage::FileUiStorage;
use crate::storage::{UiStorage, keys};

use super::action::InputAction;
use super::bindings::KeyBindings;
use super::chord::{Binding, KeyChord};
use super::{identity, scan_codes};

/// The binding groups a key map file holds, in the order the game writes
/// them.
const GROUP_ORDER: [(u32, &str); 14] = [
    (0x0000_0004, "MovementCommands"),
    (0x1000_0007, "ItemSelectionCommands"),
    (0x1000_0009, "UICommands"),
    (0x1000_000C, "QuickslotCommands"),
    (0x1000_000D, "ToggleChatEntry"),
    (0x1000_000A, "ChatCommands"),
    (0x1000_0002, "Combat"),
    (0x1000_0003, "MeleeCombat"),
    (0x1000_0004, "MissileCombat"),
    (0x1000_0005, "MagicCombat"),
    (0x1000_0006, "Emotes"),
    (0x0000_0005, "CameraControls"),
    (0x0000_0006, "CameraAlternateControls"),
    (0x1000_0008, "CharacterOptionCommands"),
];

/// Why a key map file could not be read.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum KeymapError {
    /// The file has no `Bindings` section.
    #[error("the file does not contain a retail Bindings section")]
    NoBindings,
    /// A binding line does not follow the grammar.
    #[error("malformed retail key binding at line {0}")]
    Malformed(usize),
    /// A binding names a control this client cannot bind.
    #[error("unsupported retail control '{control}' at line {line}")]
    UnsupportedControl {
        /// The control as written.
        control: String,
        /// The line.
        line: usize,
    },
    /// A modifier is not a number.
    #[error("invalid modifier at line {0}")]
    InvalidModifier(usize),
    /// A chord cannot be written in the file's DirectInput names.
    #[error("{0:?} cannot be represented by the retail DirectInput keymap")]
    Unrepresentable(KeyChord),
}

/// The name the file gives an action (the game's spelling).
fn file_action_name(action: InputAction) -> String {
    if let Some(name) = character_option_name(action) {
        return name.to_owned();
    }
    match action {
        InputAction::SelectionPlaceInInventory => return "SelectionPickUp".to_owned(),
        InputAction::UseSelected => return "USE".to_owned(),
        _ => {}
    }
    let name = action.name();
    if let Some(rest) = name.strip_prefix("CameraAlternate") {
        return format!("Camera{rest}");
    }
    let Some(emote) = name.strip_prefix("Emote") else {
        return name.to_owned();
    };
    match emote {
        "AfkState" => "AFKState",
        "AToyotState" => "ATOYOT",
        "MimeDrinking" => "MimeDrink",
        "MimeEating" => "MimeEat",
        "TalkToTheHandState" => "TalktotheHandState",
        "YawnAndStretch" => "YawnStretch",
        "Ymca" => "YMCA",
        other => other,
    }
    .to_owned()
}

fn character_option_name(action: InputAction) -> Option<&'static str> {
    use InputAction as A;
    Some(match action {
        A::ToggleCharacterOptionAutoRepeatAttack => "AutoRepeatAttacks",
        A::ToggleCharacterOptionIgnoreAllegianceRequests => "IgnoreAllegianceRequests",
        A::ToggleCharacterOptionIgnoreFellowshipRequests => "IgnoreFellowshipRequests",
        A::ToggleCharacterOptionIgnoreTradeRequests => "IgnoreTradeRequests",
        A::ToggleCharacterOptionPersistentAtDay => "PersistentAtDay",
        A::ToggleCharacterOptionAllowGive => "LetPlayersGiveYouItems",
        A::ToggleCharacterOptionViewCombatTarget => "AutoTrackCombatTargets",
        A::ToggleCharacterOptionShowTooltips => "DisplayTooltips",
        A::ToggleCharacterOptionUseDeception => "AttemptToDeceivePlayers",
        A::ToggleCharacterOptionToggleRun => "RunAsDefaultMovement",
        A::ToggleCharacterOptionStayInChatMode => "StayInChatModeAfterSend",
        A::ToggleCharacterOptionAdvancedCombatUi => "AdvancedCombatInterface",
        A::ToggleCharacterOptionAutoTarget => "AutoTarget",
        A::ToggleCharacterOptionVividTargetingIndicator => "VividTargetIndicator",
        A::ToggleCharacterOptionFellowshipShareXp => "ShareFellowshipXP",
        A::ToggleCharacterOptionAcceptLootPermits => "AcceptCorpseLooting",
        A::ToggleCharacterOptionFellowshipShareLoot => "ShareFellowshipLoot",
        A::ToggleCharacterOptionFellowshipAutoAcceptRequests => {
            "AutomaticallyAcceptFellowshipRequests"
        }
        A::ToggleCharacterOptionCoordinatesOnRadar => "ShowRadarCoordinates",
        A::ToggleCharacterOptionSpellDuration => "ShowSpellDurations",
        A::ToggleCharacterOptionDisableHouseRestrictionEffects => "DisableHouseEffect",
        A::ToggleCharacterOptionDragItemOnPlayerOpensSecureTrade => {
            "DragItemOnPlayerOpensSecureTrade"
        }
        A::ToggleCharacterOptionDisplayAllegianceLogonNotifications => {
            "DisplayAllegianceLogonNotifications"
        }
        A::ToggleCharacterOptionUseChargeAttack => "UseChargeAttack",
        A::ToggleCharacterOptionUseCraftSuccessDialog => "ToggleCraftingChanceOfSuccessDialog",
        A::ToggleCharacterOptionListenToAllegianceChat => "AllegianceChat",
        A::ToggleCharacterOptionDisplayDateOfBirth => "DisplayDateOfBirth",
        A::ToggleCharacterOptionDisplayAge => "DisplayAge",
        A::ToggleCharacterOptionDisplayChessRank => "DisplayChessRank",
        A::ToggleCharacterOptionDisplayFishingSkill => "Fishing",
        A::ToggleCharacterOptionDisplayNumberDeaths => "DisplayNumberDeaths",
        A::ToggleCharacterOptionDisplayTimeStamps => "DisplayTimeStamps",
        A::ToggleCharacterOptionSalvageMultiple => "SalvageMultiple",
        A::ToggleCharacterOptionListenToGeneralChat => "GeneralChat",
        A::ToggleCharacterOptionListenToTradeChat => "TradeChat",
        A::ToggleCharacterOptionListenToLfgChat => "LFGChat",
        A::ToggleCharacterOptionListenToRoleplayChat => "RoleplayChat",
        A::ToggleCharacterOptionDisplayNumberCharacterTitles => "DisplayNumberCharacterTitles",
        A::ToggleCharacterOptionMainPackPreferred => "MainPackPreferred",
        A::ToggleCharacterOptionLeadMissileTargets => "LeadMissileTargets",
        A::ToggleCharacterOptionUseFastMissiles => "UseFastMissiles",
        A::ToggleCharacterOptionFilterLanguage => "FilterLanguage",
        A::ToggleCharacterOptionConfirmVolatileRareUse => "ConfirmVolatileRareUse",
        A::ToggleCharacterOptionListenToSocietyChat => "SocietyChat",
        A::ToggleCharacterOptionShowHelm => "ShowHelm",
        A::ToggleCharacterOptionDisableDistanceFog => "DisableDistanceFog",
        A::ToggleCharacterOptionShowCloak => "ShowCloak",
        A::ToggleCharacterOptionSideBySideVitals => "SideBySideVitals",
        _ => return None,
    })
}

/// (input map, lower-cased file name) → action.
fn actions_by_file_name() -> &'static HashMap<(u32, String), InputAction> {
    static TABLE: OnceLock<HashMap<(u32, String), InputAction>> = OnceLock::new();
    TABLE.get_or_init(|| {
        identity::all()
            .iter()
            .map(|&((map, _), action)| {
                ((map, file_action_name(action).to_ascii_lowercase()), action)
            })
            .collect()
    })
}

/// One parsed binding line.
struct BindingLine<'a> {
    action: &'a str,
    device: &'a str,
    control: &'a str,
    modifier: Option<&'a str>,
}

/// Parse `Action [ "" [ device CONTROL [sub] ] [modifier] [activation] ]`.
fn parse_binding_line(line: &str) -> Option<BindingLine<'_>> {
    let word = |s: &str, ok: fn(char) -> bool| -> usize {
        s.char_indices()
            .find(|(_, c)| !ok(*c))
            .map_or(s.len(), |(i, _)| i)
    };
    let ident = |c: char| c.is_ascii_alphanumeric() || c == '_';
    let n = word(line, ident);
    if n == 0 {
        return None;
    }
    let action = &line[..n];
    let rest = line[n..].trim_start();
    let rest = rest.strip_prefix('[')?.trim_start();
    let rest = rest.strip_prefix("\"\"")?.trim_start();
    let rest = rest.strip_prefix('[')?.trim_start();
    let n = word(rest, |c| c.is_ascii_digit());
    if n == 0 {
        return None;
    }
    let device = &rest[..n];
    let after = &rest[n..];
    let rest = after.trim_start();
    if rest.len() == after.len() {
        return None; // the control needs whitespace before it
    }
    let n = word(rest, ident);
    if n == 0 {
        return None;
    }
    let control = &rest[..n];
    let mut rest = &rest[n..];
    // An optional sub-control (letters).
    let trimmed = rest.trim_start();
    if trimmed.len() < rest.len() {
        let n = word(trimmed, |c| c.is_ascii_alphabetic());
        if n > 0 {
            rest = &trimmed[n..];
        }
    }
    let rest = rest.trim_start().strip_prefix(']')?;
    let mut modifier = None;
    let mut rest = rest;
    let trimmed = rest.trim_start();
    if trimmed.len() < rest.len() {
        if let Some(hex) = trimmed
            .strip_prefix("0x")
            .or_else(|| trimmed.strip_prefix("0X"))
        {
            let n = word(hex, |c| c.is_ascii_hexdigit());
            if n > 0 {
                modifier = Some(&trimmed[..n + 2]);
                rest = &hex[n..];
            }
        } else {
            let n = word(trimmed, |c| c.is_ascii_digit());
            if n > 0 {
                modifier = Some(&trimmed[..n]);
                rest = &trimmed[n..];
            }
        }
    }
    // An optional activation (letters).
    let trimmed = rest.trim_start();
    if trimmed.len() < rest.len() {
        let n = word(trimmed, |c| c.is_ascii_alphabetic());
        if n > 0 {
            rest = &trimmed[n..];
        }
    }
    let rest = rest.trim_start().strip_prefix(']')?;
    rest.is_empty().then_some(BindingLine {
        action,
        device,
        control,
        modifier,
    })
}

/// The bindings of a key map file: every binding of `base` whose action
/// has no retail identity (the client's own), then every binding the file
/// gives the retail actions (with their retail activation and scope).
/// Unknown groups and actions are skipped.
///
/// # Errors
/// No `Bindings` section, a malformed binding line, an unsupported control
/// or modifier.
pub fn parse(text: &str, base: &KeyBindings) -> Result<KeyBindings, KeymapError> {
    let mut result = KeyBindings::new();
    for b in base.all() {
        if identity::retail_identity(b.action).is_none() {
            result.add(*b);
        }
    }
    let mut found = false;
    let mut in_bindings = false;
    let mut group: Option<u32> = None;
    for (index, raw) in text.replace("\r\n", "\n").split('\n').enumerate() {
        let line_number = index + 1;
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if line.eq_ignore_ascii_case("Bindings") {
            found = true;
            in_bindings = true;
            group = None;
            continue;
        }
        if !in_bindings {
            continue;
        }
        let is_header = line.chars().next().is_some_and(|c| c.is_ascii_alphabetic())
            && line.chars().all(|c| c.is_ascii_alphanumeric() || c == '_');
        if is_header {
            group = GROUP_ORDER
                .iter()
                .find(|(_, name)| name.eq_ignore_ascii_case(line))
                .map(|(id, _)| *id);
            continue;
        }
        let Some(input_map) = group else {
            continue;
        };
        if line == "[" || line == "]" {
            continue;
        }
        let parsed = parse_binding_line(line).ok_or(KeymapError::Malformed(line_number))?;
        let Some(&action) =
            actions_by_file_name().get(&(input_map, parsed.action.to_ascii_lowercase()))
        else {
            continue;
        };
        let unsupported = || KeymapError::UnsupportedControl {
            control: parsed.control.to_owned(),
            line: line_number,
        };
        let (scan, token_device) =
            scan_codes::from_file_control(parsed.control).ok_or_else(unsupported)?;
        let device: u32 = parsed.device.parse().map_err(|_| unsupported())?;
        if device != token_device {
            return Err(unsupported());
        }
        let key = scan_codes::to_key(scan, device).ok_or_else(unsupported)?;
        let modifier = match parsed.modifier {
            None => 0,
            Some(text) => match text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
                Some(hex) => u32::from_str_radix(hex, 16),
                None => text.parse(),
            }
            .map_err(|_| KeymapError::InvalidModifier(line_number))?,
        };
        let chord = KeyChord {
            key,
            modifiers: Modifiers((modifier & 0x0F) as u8),
            device: device as u8,
        };
        let (_, action_id) = identity::retail_identity(action).unwrap_or((input_map, 0));
        result.add(Binding {
            chord,
            action,
            activation: identity::activation_for(input_map, action_id),
            scope: identity::scope_for_input_map(input_map),
        });
    }
    if found {
        Ok(result)
    } else {
        Err(KeymapError::NoBindings)
    }
}

/// The key map file for `bindings` (the retail actions only; the fixed
/// system, mouse, scrolling, editing and dialog maps as the game writes
/// them).
///
/// # Errors
/// A binding whose chord the file cannot name.
pub fn write(bindings: &KeyBindings) -> Result<String, KeymapError> {
    let mut out = String::with_capacity(24_000);
    out.push_str(concat!(
        "#Asheron's Call: Throne of Destiny Keymap File\n",
        "#\n",
        "#Generated by openac's retail Configure Keyboard screen.\n",
        "#This file is compatible with the Sept-2013 retail PFile keymap grammar.\n",
        "#\n",
        "\"User Defined Keymap\" [ 00000000-0000-0000-0000-000000000000 ]\n",
        "\n",
        "Devices\n",
        "[\n",
        "  Keyboard [ GUID_SysKeyboard ]\n",
        "  Mouse [ GUID_SysMouse ]\n",
        "  Virtual [ GUID_Virtual ]\n",
        "]\n",
        "\n",
        "MetaKeys\n",
        "[\n",
        "  1 [ 0 DIK_LSHIFT ]\n",
        "  2 [ 0 DIK_LCONTROL ]\n",
        "  2 [ 0 DIK_RCONTROL ]\n",
        "  3 [ 0 DIK_LMENU ]\n",
        "  3 [ 0 DIK_RALT ]\n",
        "  4 [ 0 DIK_LWIN ]\n",
        "  4 [ 0 DIK_RWIN ]\n",
        "  5 [ 1 DIMOFS_BUTTON3 ]\n",
        "  6 [ 1 DIMOFS_BUTTON4 ]\n",
        "]\n",
        "\n",
        "Bindings\n",
        "[\n",
    ));
    for (input_map, group_name) in GROUP_ORDER {
        let _ = writeln!(out, "  {group_name}\n  [");
        let mut rows: Vec<(u32, InputAction)> = identity::all()
            .iter()
            .filter(|((map, _), _)| *map == input_map)
            .map(|&((_, id), action)| (id, action))
            .collect();
        rows.sort_by_key(|(id, _)| *id);
        for (_, action) in rows {
            for binding in bindings.for_action(action) {
                let control = scan_codes::to_file_control(binding.chord)
                    .ok_or(KeymapError::Unrepresentable(binding.chord))?;
                let _ = write!(
                    out,
                    "    {} [ \"\" [ {} {control} ]",
                    file_action_name(action),
                    binding.chord.device
                );
                let modifier = u32::from(binding.chord.modifiers.0) & 0x0F;
                if modifier != 0 {
                    let _ = write!(out, " 0x{modifier:08X}");
                }
                out.push_str(" ]\n");
            }
        }
        if input_map == 0x1000_0009 {
            out.push_str("    EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]\n");
        }
        out.push_str("  ]\n\n");
    }
    out.push_str(FIXED_RETAIL_MAPS);
    out.push_str("]\n");
    Ok(out)
}

const FIXED_RETAIL_MAPS: &str = concat!(
    "  TargetedUsage\n",
    "  [\n",
    "    SelectLeft [ \"\" [ 1 DIMOFS_BUTTON0 ] ]\n",
    "    SelectRight [ \"\" [ 1 DIMOFS_BUTTON1 ] ]\n",
    "  ]\n",
    "\n",
    "  SystemKeys\n",
    "  [\n",
    "    AltEnter [ \"\" [ 0 DIK_RETURN ] 0x00000004 ]\n",
    "    AltTab [ \"\" [ 0 DIK_TAB ] 0x00000004 ]\n",
    "    AltF4 [ \"\" [ 0 DIK_F4 ] 0x00000004 ]\n",
    "    CtrlShiftEsc [ \"\" [ 0 DIK_ESCAPE ] 0x00000003 ]\n",
    "  ]\n",
    "\n",
    "  MouseCommands\n",
    "  [\n",
    "    PointerX [ \"\" [ 1 DIMOFS_X ] 0x00000000 Analog ]\n",
    "    PointerY [ \"\" [ 1 DIMOFS_Y ] 0x00000000 Analog ]\n",
    "    SelectLeft [ \"\" [ 1 DIMOFS_BUTTON0 ] ]\n",
    "    SelectRight [ \"\" [ 1 DIMOFS_BUTTON1 ] ]\n",
    "    SelectMid [ \"\" [ 1 DIMOFS_BUTTON2 ] ]\n",
    "    SelectDblLeft [ \"\" [ 1 DIMOFS_BUTTON0 ] 0x00000000 MouseDblClick ]\n",
    "    SelectDblRight [ \"\" [ 1 DIMOFS_BUTTON1 ] 0x00000000 MouseDblClick ]\n",
    "    SelectDblMid [ \"\" [ 1 DIMOFS_BUTTON2 ] 0x00000000 MouseDblClick ]\n",
    "  ]\n",
    "\n",
    "  ScrollableControls\n",
    "  [\n",
    "    ScrollUp [ \"\" [ 1 DIMOFS_Z AxisPositive ] ]\n",
    "    ScrollDown [ \"\" [ 1 DIMOFS_Z AxisNegative ] ]\n",
    "    ScrollUp [ \"\" [ 0 DIK_UPARROW ] 0x00000002 ]\n",
    "    ScrollDown [ \"\" [ 0 DIK_DOWNARROW ] 0x00000002 ]\n",
    "  ]\n",
    "\n",
    "  EditControls\n",
    "  [\n",
    "    CursorCharLeft [ \"\" [ 0 DIK_LEFT ] ]\n",
    "    CursorCharRight [ \"\" [ 0 DIK_RIGHTARROW ] ]\n",
    "    CursorPreviousLine [ \"\" [ 0 DIK_UPARROW ] ]\n",
    "    CursorNextLine [ \"\" [ 0 DIK_DOWNARROW ] ]\n",
    "    CursorPreviousPage [ \"\" [ 0 DIK_PGUP ] ]\n",
    "    CursorNextPage [ \"\" [ 0 DIK_PGDN ] ]\n",
    "    CursorWordLeft [ \"\" [ 0 DIK_LEFT ] 0x00000002 ]\n",
    "    CursorWordRight [ \"\" [ 0 DIK_RIGHTARROW ] 0x00000002 ]\n",
    "    CursorStartOfLine [ \"\" [ 0 DIK_HOME ] ]\n",
    "    CursorStartOfDocument [ \"\" [ 0 DIK_HOME ] 0x00000002 ]\n",
    "    CursorEndOfLine [ \"\" [ 0 DIK_END ] ]\n",
    "    CursorEndOfDocument [ \"\" [ 0 DIK_END ] 0x00000002 ]\n",
    "    EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]\n",
    "    AcceptInput [ \"\" [ 0 DIK_RETURN ] ]\n",
    "    DeleteKey [ \"\" [ 0 DIK_DELETE ] ]\n",
    "    BackspaceKey [ \"\" [ 0 DIK_BACK ] ]\n",
    "  ]\n",
    "\n",
    "  CopyAndPasteControls\n",
    "  [\n",
    "    CopyText [ \"\" [ 0 DIK_C ] 0x00000002 ]\n",
    "    CopyText [ \"\" [ 0 DIK_INSERT ] 0x00000002 ]\n",
    "    CutText [ \"\" [ 0 DIK_X ] 0x00000002 ]\n",
    "    CutText [ \"\" [ 0 DIK_DELETE ] 0x00000001 ]\n",
    "    PasteText [ \"\" [ 0 DIK_V ] 0x00000002 ]\n",
    "    PasteText [ \"\" [ 0 DIK_INSERT ] 0x00000001 ]\n",
    "  ]\n",
    "\n",
    "  DialogBoxes\n",
    "  [\n",
    "    EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]\n",
    "    AcceptInput [ \"\" [ 0 DIK_RETURN ] ]\n",
    "  ]\n",
    "\n",
);

/// How a profile save went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeymapSaveStatus {
    /// Written and selected.
    Saved,
    /// A file of that name exists and overwriting was not asked for.
    Exists,
    /// The file is read-only.
    ReadOnly,
    /// The name is not a plain file name.
    InvalidName,
    /// The write failed.
    Failed,
}

/// A profile save's result.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeymapSaveResult {
    /// How it went.
    pub status: KeymapSaveStatus,
    /// The normalized file name.
    pub file_name: String,
    /// What failed.
    pub error: Option<String>,
}

/// The profile used when none was chosen.
pub const DEFAULT_FILE_NAME: &str = "openac.keymap";

/// The key a profile of that (normalized) file name is stored under.
fn profile_key(name: &str) -> String {
    format!("{}{name}", keys::KEYMAPS)
}

/// The active profile's file name ([`DEFAULT_FILE_NAME`] when none was
/// chosen or the choice cannot be read).
#[must_use]
pub fn active_profile_name(storage: &dyn UiStorage) -> String {
    storage
        .get(keys::ACTIVE_KEYMAP)
        .and_then(text)
        .ok()
        .map(|text| normalize_file_name(&text))
        .filter(|name| !name.is_empty())
        .unwrap_or_else(|| DEFAULT_FILE_NAME.to_owned())
}

/// Stored bytes as text, failing as reading a file as text does.
fn text(bytes: Vec<u8>) -> io::Result<String> {
    String::from_utf8(bytes).map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "stream did not contain valid UTF-8",
        )
    })
}

/// The profiles stored, sorted ignoring case.
///
/// # Errors
/// They cannot be listed.
pub fn list_profiles(storage: &dyn UiStorage) -> io::Result<Vec<String>> {
    let mut names: Vec<String> = storage
        .list(keys::KEYMAPS)?
        .into_iter()
        .filter_map(|key| key.strip_prefix(keys::KEYMAPS).map(str::to_owned))
        .filter(|name| {
            Path::new(name)
                .extension()
                .is_some_and(|e| e.eq_ignore_ascii_case("keymap"))
        })
        .collect();
    names.sort_by_key(|n| n.to_ascii_lowercase());
    Ok(names)
}

/// Load a profile over `base` and make it the active one.
///
/// # Errors
/// The name is not a plain file name, or the profile cannot be read or
/// parsed (the text of the failure).
pub fn load_profile(
    storage: &mut dyn UiStorage,
    file_name: &str,
    base: &KeyBindings,
) -> Result<KeyBindings, String> {
    let name = normalize_file_name(file_name);
    if name.is_empty() {
        return Err("The keymap filename is invalid.".to_owned());
    }
    let text = storage
        .get(&profile_key(&name))
        .and_then(text)
        .map_err(|e| e.to_string())?;
    let bindings = parse(&text, base).map_err(|e| e.to_string())?;
    select(storage, &name).map_err(|e| e.to_string())?;
    Ok(bindings)
}

/// Remember `name` as the active profile.
fn select(storage: &mut dyn UiStorage, name: &str) -> io::Result<()> {
    storage.put(keys::ACTIVE_KEYMAP, format!("{name}\n").as_bytes())
}

/// Save `bindings` as a profile and make it the active one.
#[must_use]
pub fn save_profile(
    storage: &mut dyn UiStorage,
    file_name: &str,
    bindings: &KeyBindings,
    overwrite: bool,
) -> KeymapSaveResult {
    let name = normalize_file_name(file_name);
    let result = |status, error: Option<String>| KeymapSaveResult {
        status,
        file_name: name.clone(),
        error,
    };
    if name.is_empty() {
        return result(KeymapSaveStatus::InvalidName, None);
    }
    let key = profile_key(&name);
    if let Some(stored) = storage.presence(&key) {
        if !overwrite {
            return result(KeymapSaveStatus::Exists, None);
        }
        if stored.read_only {
            return result(KeymapSaveStatus::ReadOnly, None);
        }
    }
    let text = match write(bindings) {
        Ok(text) => text,
        Err(error) => return result(KeymapSaveStatus::Failed, Some(error.to_string())),
    };
    let written = storage
        .put(&key, text.as_bytes())
        .and_then(|()| select(storage, &name));
    match written {
        Ok(()) => result(KeymapSaveStatus::Saved, None),
        Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
            result(KeymapSaveStatus::ReadOnly, Some(error.to_string()))
        }
        Err(error) => result(KeymapSaveStatus::Failed, Some(error.to_string())),
    }
}

/// The active profile over the stored bindings: the bindings (or the
/// defaults) when no profile is stored under the active name; a profile
/// that fails to load is reported and the bindings are used.
#[must_use]
pub fn load_active(storage: &mut dyn UiStorage) -> (KeyBindings, Vec<String>) {
    let mut notices = Vec::new();
    let (fallback, error) = KeyBindings::load_from(storage, keys::KEY_BINDINGS);
    if let Some(error) = error {
        notices.push(format!("{error}; using the retail defaults"));
    }
    let name = active_profile_name(storage);
    if !storage
        .presence(&profile_key(&name))
        .is_some_and(|p| p.file)
    {
        return (fallback, notices);
    }
    match load_profile(storage, &name, &fallback) {
        Ok(loaded) => (loaded, notices),
        Err(error) => {
            notices.push(format!(
                "keymap: '{name}' could not be loaded; using the bindings file: {error}"
            ));
            (fallback, notices)
        }
    }
}

/// The named key map profiles (`*.keymap` in the game's folder) and the
/// active one (`active-keymap.txt` beside the bindings file), as files.
#[cfg(not(target_arch = "wasm32"))]
#[derive(Debug, Clone)]
pub struct KeymapProfileStore {
    directory: PathBuf,
    files: FileUiStorage,
}

#[cfg(not(target_arch = "wasm32"))]
impl KeymapProfileStore {
    /// Profiles in `keymap_directory`; the active one is remembered beside
    /// `bindings_path`.
    #[must_use]
    pub fn new(bindings_path: &Path, keymap_directory: &Path) -> Self {
        Self {
            directory: keymap_directory.to_path_buf(),
            files: FileUiStorage::for_key_bindings(bindings_path, keymap_directory),
        }
    }

    /// The profile folder.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// See [`active_profile_name`].
    #[must_use]
    pub fn current_file_name(&self) -> String {
        active_profile_name(&self.files)
    }

    /// See [`list_profiles`].
    ///
    /// # Errors
    /// The folder exists but cannot be listed.
    pub fn list_files(&self) -> io::Result<Vec<String>> {
        list_profiles(&self.files)
    }

    /// See [`load_profile`].
    ///
    /// # Errors
    /// The name is not a plain file name, or the file cannot be read or
    /// parsed (the text of the failure).
    pub fn load(&self, file_name: &str, base: &KeyBindings) -> Result<KeyBindings, String> {
        load_profile(&mut self.files.clone(), file_name, base)
    }

    /// See [`save_profile`].
    #[must_use]
    pub fn save(
        &self,
        file_name: &str,
        bindings: &KeyBindings,
        overwrite: bool,
    ) -> KeymapSaveResult {
        save_profile(&mut self.files.clone(), file_name, bindings, overwrite)
    }
}

/// [`load_active`] over the bindings file and the profile folder.
#[cfg(not(target_arch = "wasm32"))]
#[must_use]
pub fn load_active_or_json(
    bindings_path: &Path,
    keymap_directory: &Path,
) -> (KeyBindings, Vec<String>) {
    load_active(&mut FileUiStorage::for_key_bindings(
        bindings_path,
        keymap_directory,
    ))
}

/// A plain file name with the `.keymap` extension, or empty when the name
/// is not a plain file name.
#[must_use]
pub fn normalize_file_name(value: &str) -> String {
    let trimmed = value.trim();
    if trimmed.is_empty()
        || trimmed.chars().any(|c| {
            c.is_control() || matches!(c, '/' | '\\' | '"' | '<' | '>' | '|' | ':' | '*' | '?')
        })
    {
        return String::new();
    }
    if Path::new(trimmed)
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("keymap"))
    {
        trimmed.to_owned()
    } else {
        format!("{trimmed}.keymap")
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Input/RetailKeymapFileTests.cs`.

    use super::*;
    use crate::event::Key;
    use crate::input::chord::{InputKey, KeyChord};

    #[test]
    fn write_then_parse_round_trips_every_retail_action_identity() {
        let mut source = KeyBindings::new();
        for &((map, id), action) in identity::all() {
            source.add(Binding {
                chord: KeyChord::key(
                    Key::SuperRight,
                    Modifiers::SHIFT | Modifiers::CTRL | Modifiers::WIN,
                ),
                action,
                activation: identity::activation_for(map, id),
                scope: identity::scope_for_input_map(map),
            });
        }
        let text = write(&source).unwrap();
        let loaded = parse(&text, &KeyBindings::new()).unwrap();
        assert_eq!(loaded.all().len(), 306);
        for expected in source.all() {
            assert!(loaded.all().contains(expected), "{expected:?}");
        }
        for needle in [
            "CharacterOptionCommands",
            "AutoRepeatAttacks",
            "AFKState",
            "DIK_RWIN",
            "EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]",
            "TargetedUsage",
        ] {
            assert!(text.contains(needle), "{needle}");
        }
    }

    #[test]
    fn the_client_s_own_bindings_survive_a_parse_and_malformed_lines_are_refused() {
        let base = KeyBindings::retail_defaults();
        let loaded = parse("Bindings\n[\n  MovementCommands\n  [\n    MovementForward [ \"\" [ 0 DIK_UPARROW ] ]\n  ]\n]\n", &base).unwrap();
        let forward: Vec<_> = loaded.for_action(InputAction::MovementForward).collect();
        assert_eq!(forward.len(), 1);
        assert_eq!(forward[0].chord.key, InputKey::Key(Key::Up));
        assert!(
            loaded
                .for_action(InputAction::AcdreamToggleAudioMute)
                .count()
                > 0
        );
        assert!(loaded.for_action(InputAction::MovementBackup).count() == 0);
        assert_eq!(
            parse(
                "Bindings\n[\n MovementCommands\n [\n MovementForward [ 0 DIK_W ]\n",
                &base
            )
            .unwrap_err(),
            KeymapError::Malformed(5)
        );
        assert_eq!(
            parse("nothing", &base).unwrap_err(),
            KeymapError::NoBindings
        );
        assert!(matches!(
            parse(
                "Bindings\n MovementCommands\n MovementForward [ \"\" [ 1 DIK_W ] ]\n",
                &base
            ),
            Err(KeymapError::UnsupportedControl { line: 3, .. })
        ));
    }

    #[test]
    fn a_profile_saves_selects_lists_and_loads() {
        let root = std::env::temp_dir().join(format!(
            "openac-keymap-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let config = root.join("config");
        let documents = root.join("documents").join("Asheron's Call");
        let json = config.join("keybinds.json");
        let source = KeyBindings::retail_defaults();
        let store = KeymapProfileStore::new(&json, &documents);
        assert_eq!(store.current_file_name(), DEFAULT_FILE_NAME);
        let saved = store.save("friends", &source, false);
        assert_eq!(saved.status, KeymapSaveStatus::Saved, "{saved:?}");
        assert_eq!(store.current_file_name(), "friends.keymap");
        assert_eq!(store.list_files().unwrap(), ["friends.keymap"]);
        let loaded = store.load("friends.keymap", &KeyBindings::new()).unwrap();
        let a: Vec<_> = source.for_action(InputAction::MovementForward).collect();
        let b: Vec<_> = loaded.for_action(InputAction::MovementForward).collect();
        assert_eq!(a, b);
        assert_eq!(
            store.save("friends", &source, false).status,
            KeymapSaveStatus::Exists
        );
        assert_eq!(
            store.save("../x", &source, true).status,
            KeymapSaveStatus::InvalidName
        );
        let (active, notices) = load_active_or_json(&json, &documents);
        assert!(notices.is_empty(), "{notices:?}");
        assert_eq!(
            active.for_action(InputAction::MovementForward).count(),
            a.len()
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn profiles_through_the_storage_are_the_bytes_the_files_hold() {
        use crate::storage::{MemoryUiStorage, StorageWrite};
        let root = std::env::temp_dir().join(format!(
            "openac-keymap-storage-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let json = root.join("config").join("keybinds.json");
        let documents = root.join("documents");
        let source = KeyBindings::retail_defaults();
        let files = KeymapProfileStore::new(&json, &documents);
        assert_eq!(
            files.save("friends", &source, false).status,
            KeymapSaveStatus::Saved
        );
        let profile = std::fs::read(documents.join("friends.keymap")).unwrap();
        let selector = std::fs::read(root.join("config").join("active-keymap.txt")).unwrap();
        let _ = std::fs::remove_dir_all(&root);

        let mut memory = MemoryUiStorage::new();
        assert_eq!(active_profile_name(&memory), DEFAULT_FILE_NAME);
        let saved = save_profile(&mut memory, "friends", &source, false);
        assert_eq!(saved.status, KeymapSaveStatus::Saved, "{saved:?}");
        assert_eq!(saved.file_name, "friends.keymap");
        assert_eq!(
            memory.take_writes(),
            [
                StorageWrite::Put {
                    key: "keymaps/friends.keymap".into(),
                    bytes: profile
                },
                StorageWrite::Put {
                    key: keys::ACTIVE_KEYMAP.into(),
                    bytes: selector.clone()
                },
            ]
        );
        assert_eq!(active_profile_name(&memory), "friends.keymap");
        assert_eq!(
            save_profile(&mut memory, "friends", &source, false).status,
            KeymapSaveStatus::Exists
        );
        assert_eq!(
            save_profile(&mut memory, "../x", &source, true).status,
            KeymapSaveStatus::InvalidName
        );
        assert!(memory.take_writes().is_empty());

        // Listing: only `.keymap` names, sorted ignoring case.
        memory.insert("keymaps/Alpha.KEYMAP", b"x".to_vec());
        memory.insert("keymaps/notes.txt", b"x".to_vec());
        assert_eq!(
            list_profiles(&memory).unwrap(),
            ["Alpha.KEYMAP", "friends.keymap"]
        );

        // Loading makes it the active one; a broken one fails with the
        // reason and changes nothing.
        let loaded = load_profile(&mut memory, "friends.keymap", &KeyBindings::new()).unwrap();
        let a: Vec<_> = source.for_action(InputAction::MovementForward).collect();
        let b: Vec<_> = loaded.for_action(InputAction::MovementForward).collect();
        assert_eq!(a, b);
        assert_eq!(
            memory.take_writes(),
            [StorageWrite::Put {
                key: keys::ACTIVE_KEYMAP.into(),
                bytes: selector
            }]
        );
        assert!(load_profile(&mut memory, "Alpha.KEYMAP", &KeyBindings::new()).is_err());
        assert!(load_profile(&mut memory, "missing", &KeyBindings::new()).is_err());
        assert!(memory.take_writes().is_empty());

        // The active profile over the stored bindings, and the bindings
        // when the active one is not stored.
        let (active, notices) = load_active(&mut memory);
        assert!(notices.is_empty(), "{notices:?}");
        assert_eq!(
            active.for_action(InputAction::MovementForward).count(),
            a.len()
        );
        let mut fresh = MemoryUiStorage::new();
        let mut custom = KeyBindings::new();
        custom.add(Binding::new(
            KeyChord::key(Key::Q, Modifiers::NONE),
            InputAction::MovementForward,
        ));
        custom.save_to(&mut fresh, keys::KEY_BINDINGS).unwrap();
        let (active, notices) = load_active(&mut fresh);
        assert!(notices.is_empty(), "{notices:?}");
        assert_eq!(active, KeyBindings::load_from(&fresh, keys::KEY_BINDINGS).0);
    }

    #[test]
    fn a_read_only_profile_is_not_overwritten() {
        use crate::storage::{FileUiStorage, WriteMode};
        let root = std::env::temp_dir().join(format!(
            "openac-keymap-read-only-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let mut files = FileUiStorage::new().with_folder(keys::KEYMAPS, &root, WriteMode::Replace);
        let source = KeyBindings::retail_defaults();
        files.put("keymaps/locked.keymap", b"old").unwrap();
        let path = root.join("locked.keymap");
        let mut permissions = std::fs::metadata(&path).unwrap().permissions();
        permissions.set_readonly(true);
        std::fs::set_permissions(&path, permissions.clone()).unwrap();
        let result = save_profile(&mut files, "locked", &source, true);
        assert_eq!(result.status, KeymapSaveStatus::ReadOnly);
        assert_eq!(result.error, None);
        assert_eq!(std::fs::read(&path).unwrap(), b"old");
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
        std::fs::set_permissions(&path, permissions).unwrap();
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn file_names_are_normalized() {
        assert_eq!(normalize_file_name(" friends "), "friends.keymap");
        assert_eq!(normalize_file_name("A.KEYMAP"), "A.KEYMAP");
        assert_eq!(normalize_file_name("a/b"), "");
        assert_eq!(normalize_file_name("   "), "");
    }
}
