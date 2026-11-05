//! Retail `.keymap` files (the game's own key map format, as the retail
//! Configure Keyboard screen saves them) and the store of named key map
//! profiles beside the dats, with the active one remembered.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use crate::event::Modifiers;

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

/// The named key map profiles (`*.keymap` in the game's folder) and the
/// active one (`active-keymap.txt` beside the bindings file).
#[derive(Debug, Clone)]
pub struct KeymapProfileStore {
    directory: PathBuf,
    selector: PathBuf,
}

/// The profile used when none was chosen.
pub const DEFAULT_FILE_NAME: &str = "openac.keymap";

impl KeymapProfileStore {
    /// Profiles in `keymap_directory`; the active one is remembered beside
    /// `bindings_path`.
    #[must_use]
    pub fn new(bindings_path: &Path, keymap_directory: &Path) -> Self {
        let config = bindings_path.parent().unwrap_or(Path::new("."));
        Self {
            directory: keymap_directory.to_path_buf(),
            selector: config.join("active-keymap.txt"),
        }
    }

    /// The profile folder.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// The active profile's file name ([`DEFAULT_FILE_NAME`] when none was
    /// chosen or the choice cannot be read).
    #[must_use]
    pub fn current_file_name(&self) -> String {
        std::fs::read_to_string(&self.selector)
            .ok()
            .map(|text| normalize_file_name(&text))
            .filter(|name| !name.is_empty())
            .unwrap_or_else(|| DEFAULT_FILE_NAME.to_owned())
    }

    /// The profiles in the folder, sorted ignoring case.
    ///
    /// # Errors
    /// The folder exists but cannot be listed.
    pub fn list_files(&self) -> std::io::Result<Vec<String>> {
        if !self.directory.is_dir() {
            return Ok(Vec::new());
        }
        let mut names = Vec::new();
        for entry in std::fs::read_dir(&self.directory)? {
            let entry = entry?;
            let name = entry.file_name().to_string_lossy().into_owned();
            if entry.file_type()?.is_file()
                && Path::new(&name)
                    .extension()
                    .is_some_and(|e| e.eq_ignore_ascii_case("keymap"))
            {
                names.push(name);
            }
        }
        names.sort_by_key(|n| n.to_ascii_lowercase());
        Ok(names)
    }

    /// Load a profile over `base` and make it the active one.
    ///
    /// # Errors
    /// The name is not a plain file name, or the file cannot be read or
    /// parsed (the text of the failure).
    pub fn load(&self, file_name: &str, base: &KeyBindings) -> Result<KeyBindings, String> {
        let name = normalize_file_name(file_name);
        if name.is_empty() {
            return Err("The keymap filename is invalid.".to_owned());
        }
        let text =
            std::fs::read_to_string(self.directory.join(&name)).map_err(|e| e.to_string())?;
        let bindings = parse(&text, base).map_err(|e| e.to_string())?;
        self.write_selector(&name).map_err(|e| e.to_string())?;
        Ok(bindings)
    }

    /// Save `bindings` as a profile and make it the active one.
    #[must_use]
    pub fn save(
        &self,
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
        let path = self.directory.join(&name);
        if let Ok(meta) = std::fs::metadata(&path) {
            if !overwrite {
                return result(KeymapSaveStatus::Exists, None);
            }
            if meta.permissions().readonly() {
                return result(KeymapSaveStatus::ReadOnly, None);
            }
        }
        let text = match write(bindings) {
            Ok(text) => text,
            Err(error) => return result(KeymapSaveStatus::Failed, Some(error.to_string())),
        };
        let written = std::fs::create_dir_all(&self.directory)
            .and_then(|()| atomic_write(&path, &text))
            .and_then(|()| self.write_selector(&name));
        match written {
            Ok(()) => result(KeymapSaveStatus::Saved, None),
            Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
                result(KeymapSaveStatus::ReadOnly, Some(error.to_string()))
            }
            Err(error) => result(KeymapSaveStatus::Failed, Some(error.to_string())),
        }
    }

    fn write_selector(&self, name: &str) -> std::io::Result<()> {
        if let Some(dir) = self.selector.parent() {
            std::fs::create_dir_all(dir)?;
        }
        atomic_write(&self.selector, &format!("{name}\n"))
    }
}

/// The active profile over the bindings file: the bindings file (or the
/// defaults) when no profile file exists; a profile that fails to load is
/// reported and the bindings file is used.
#[must_use]
pub fn load_active_or_json(
    bindings_path: &Path,
    keymap_directory: &Path,
) -> (KeyBindings, Vec<String>) {
    let mut notices = Vec::new();
    let (fallback, error) = KeyBindings::load_or_default(bindings_path);
    if let Some(error) = error {
        notices.push(format!("{error}; using the retail defaults"));
    }
    let store = KeymapProfileStore::new(bindings_path, keymap_directory);
    let name = store.current_file_name();
    if !store.directory().join(&name).is_file() {
        return (fallback, notices);
    }
    match store.load(&name, &fallback) {
        Ok(loaded) => (loaded, notices),
        Err(error) => {
            notices.push(format!(
                "keymap: '{name}' could not be loaded; using the bindings file: {error}"
            ));
            (fallback, notices)
        }
    }
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

fn atomic_write(path: &Path, content: &str) -> std::io::Result<()> {
    let temp = path.with_extension(format!(
        "tmp-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos())
    ));
    let written = std::fs::write(&temp, content).and_then(|()| std::fs::rename(&temp, path));
    if written.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    written
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
    fn file_names_are_normalized() {
        assert_eq!(normalize_file_name(" friends "), "friends.keymap");
        assert_eq!(normalize_file_name("A.KEYMAP"), "A.KEYMAP");
        assert_eq!(normalize_file_name("a/b"), "");
        assert_eq!(normalize_file_name("   "), "");
    }
}
