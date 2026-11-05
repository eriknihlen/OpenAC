//! The game's action map as the dats hold it: every player-bindable action
//! (its input map, action id, class and name hashes) with its default keys
//! from both master input maps, and which input maps conflict.

use std::collections::{HashMap, HashSet};

use ac_dat::objects::{
    ACTION_MAP_ID, ActionMap, GAMEPLAY_MASTER_INPUT_MAP_ID, MasterInputMap,
    SYSTEM_MASTER_INPUT_MAP_ID,
};

/// An action's class (what the key configuration screen groups it under).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RetailActionClass {
    /// Not bindable.
    None,
    /// Movement.
    Movement,
    /// Camera.
    Camera,
    /// UI.
    Ui,
    /// Combat.
    Combat,
    /// Emotes.
    Emote,
    /// Character settings (7; 6 is absent from the shipped dat).
    CharacterSettings,
    /// A class the dat names that has no name here.
    Other(u32),
}

impl RetailActionClass {
    /// The class of a dat class number.
    #[must_use]
    pub fn from_id(id: u32) -> Self {
        match id {
            0 => Self::None,
            1 => Self::Movement,
            2 => Self::Camera,
            3 => Self::Ui,
            4 => Self::Combat,
            5 => Self::Emote,
            7 => Self::CharacterSettings,
            other => Self::Other(other),
        }
    }
}

/// A default key: scan code, device, retail modifier word, activation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RetailKeyChord {
    /// Scan code.
    pub scan: u32,
    /// Device (0 keyboard, 1 mouse).
    pub device: u32,
    /// Retail modifier word.
    pub modifier: u32,
    /// Activation.
    pub activation: u32,
}

/// One bindable action.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RetailActionMapRow {
    /// Input map.
    pub input_map: u32,
    /// Action id.
    pub action_id: u32,
    /// Class.
    pub class: RetailActionClass,
    /// Name hash.
    pub label_hash: u32,
    /// Description hash.
    pub tooltip_hash: u32,
    /// Default keys (gameplay map first, then system map).
    pub default_bindings: Vec<RetailKeyChord>,
}

/// The action map.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RetailActionMapSnapshot {
    /// The bindable actions, in dat order.
    pub rows: Vec<RetailActionMapRow>,
    /// Input map → the input maps it conflicts with.
    pub conflicting_input_maps: HashMap<u32, HashSet<u32>>,
}

impl RetailActionMapSnapshot {
    /// A key bound in `left` and in `right` conflicts: the same input map,
    /// or one the dat lists as conflicting.
    #[must_use]
    pub fn input_maps_conflict(&self, left: u32, right: u32) -> bool {
        left == right
            || self
                .conflicting_input_maps
                .get(&left)
                .is_some_and(|c| c.contains(&right))
    }
}

/// The string table holding the input map names.
pub const INPUT_MAP_STRING_TABLE_ID: u32 = 0x2300_0005;

/// The name of each input map in [`INPUT_MAP_STRING_TABLE_ID`].
pub const INPUT_MAP_NAMES: [(u32, &str); 19] = [
    (0x0000_0004, "ID_InputMap_MovementCommands"),
    (0x0000_0005, "ID_InputMap_CameraControls"),
    (0x0000_0006, "ID_InputMap_CameraAlternateControls"),
    (0x0000_0009, "ID_InputMap_DialogBoxes"),
    (0x0000_000B, "ID_InputMap_DebugConsole"),
    (0x0000_000C, "ID_InputMap_ProfilerUI"),
    (0x0000_000D, "ID_InputMap_UIDebugger"),
    (0x0000_000E, "ID_InputMap_DebugCommands"),
    (0x1000_0002, "ID_InputMap_Combat"),
    (0x1000_0003, "ID_InputMap_MeleeCombat"),
    (0x1000_0004, "ID_InputMap_MissileCombat"),
    (0x1000_0005, "ID_InputMap_MagicCombat"),
    (0x1000_0006, "ID_InputMap_Emotes"),
    (0x1000_0007, "ID_InputMap_ItemSelectionCommands"),
    (0x1000_0008, "ID_InputMap_CharacterOptionCommands"),
    (0x1000_0009, "ID_InputMap_UICommands"),
    (0x1000_000A, "ID_InputMap_ChatCommands"),
    (0x1000_000C, "ID_InputMap_QuickslotCommands"),
    (0x1000_000D, "ID_InputMap_ToggleChatEntry"),
];

/// The snapshot of an action map and its master maps (either may be
/// missing; a missing master map contributes no defaults).
#[must_use]
pub fn snapshot(
    action_map: &ActionMap,
    gameplay: Option<&MasterInputMap>,
    system: Option<&MasterInputMap>,
) -> RetailActionMapSnapshot {
    let mut rows = Vec::new();
    for (input_map, actions) in &action_map.input_maps {
        for (action_id, value) in actions {
            let class = value.user_binding.action_class;
            if class == 0 {
                continue;
            }
            let mut defaults = Vec::new();
            for map in [gameplay, system].into_iter().flatten() {
                collect_defaults(map, *input_map, *action_id, &mut defaults);
            }
            rows.push(RetailActionMapRow {
                input_map: *input_map,
                action_id: *action_id,
                class: RetailActionClass::from_id(class),
                label_hash: value.user_binding.action_name,
                tooltip_hash: value.user_binding.action_description,
                default_bindings: defaults,
            });
        }
    }
    let conflicting_input_maps = action_map
        .conflicting_maps
        .iter()
        .map(|(key, value)| {
            let input_map = if value.input_map != 0 {
                value.input_map
            } else {
                *key
            };
            (
                input_map,
                value.conflicting_input_maps.iter().copied().collect(),
            )
        })
        .collect();
    RetailActionMapSnapshot {
        rows,
        conflicting_input_maps,
    }
}

fn collect_defaults(
    map: &MasterInputMap,
    input_map: u32,
    action_id: u32,
    into: &mut Vec<RetailKeyChord>,
) {
    let Some((_, controls)) = map.input_maps.iter().find(|(id, _)| *id == input_map) else {
        return;
    };
    for control in controls.iter().filter(|c| c.unknown == action_id) {
        into.push(RetailKeyChord {
            scan: (control.key.key >> 16) & 0xFFFF,
            device: control.key.key & 0xFFFF,
            modifier: control.key.modifier,
            activation: control.activation,
        });
    }
}

/// Read the action map from the dats (`None` when it is missing).
///
/// # Errors
/// A dat object could not be read.
pub fn read(
    dats: &ac_dat::DatCollection,
) -> Result<Option<RetailActionMapSnapshot>, ac_dat::DatError> {
    let Some(action_map) = dats.get::<ActionMap>(ACTION_MAP_ID)? else {
        return Ok(None);
    };
    let gameplay = dats.get::<MasterInputMap>(GAMEPLAY_MASTER_INPUT_MAP_ID)?;
    let system = dats.get::<MasterInputMap>(SYSTEM_MASTER_INPUT_MAP_ID)?;
    Ok(Some(snapshot(
        &action_map,
        gameplay.as_ref(),
        system.as_ref(),
    )))
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Input/RetailActionMapReaderTests.cs`
    //! (the dat cases are `tests/retail_input.rs`).

    use ac_dat::objects::{
        ActionMapValue, ControlSpecification, InputsConflictsValue, QualifiedControl,
        UserBindingData,
    };

    use super::*;

    fn value(class: u32, name: u32, description: u32) -> ActionMapValue {
        ActionMapValue {
            user_binding: UserBindingData {
                action_class: class,
                action_name: name,
                action_description: description,
            },
            ..ActionMapValue::default()
        }
    }

    #[test]
    fn non_bindable_actions_are_filtered() {
        let map = ActionMap {
            string_table_id: INPUT_MAP_STRING_TABLE_ID,
            input_maps: vec![(
                4,
                vec![(0x1, value(0, 0, 0)), (0x29, value(1, 0x111, 0x222))],
            )],
            ..ActionMap::default()
        };
        let s = snapshot(&map, None, None);
        assert_eq!(s.rows.len(), 1);
        let row = &s.rows[0];
        assert_eq!((row.input_map, row.action_id), (4, 0x29));
        assert_eq!(row.class, RetailActionClass::Movement);
        assert_eq!((row.label_hash, row.tooltip_hash), (0x111, 0x222));
        assert!(
            row.default_bindings.is_empty(),
            "no master maps: no defaults"
        );
    }

    #[test]
    fn both_master_maps_contribute_defaults() {
        let map = ActionMap {
            input_maps: vec![(5, vec![(0x3E, value(2, 0, 0)), (0x33, value(2, 0, 0))])],
            ..ActionMap::default()
        };
        let control = |scan: u32, action: u32| QualifiedControl {
            key: ControlSpecification {
                key: scan << 16,
                modifier: 0,
            },
            activation: 3,
            unknown: action,
        };
        let gameplay = MasterInputMap {
            input_maps: vec![(5, vec![control(0xB5, 0x3E)])],
            ..MasterInputMap::default()
        };
        let system = MasterInputMap {
            input_maps: vec![(5, vec![control(0x4A, 0x33)])],
            ..MasterInputMap::default()
        };
        let s = snapshot(&map, Some(&gameplay), Some(&system));
        assert_eq!(s.rows.len(), 2);
        let scans = |id: u32| -> Vec<u32> {
            s.rows
                .iter()
                .find(|r| r.action_id == id)
                .unwrap()
                .default_bindings
                .iter()
                .map(|c| c.scan)
                .collect()
        };
        assert_eq!(scans(0x3E), [0xB5]);
        assert_eq!(scans(0x33), [0x4A]);
    }

    #[test]
    fn the_retail_input_map_conflict_policy_is_kept() {
        let map = ActionMap {
            conflicting_maps: vec![(
                0x1000_0003,
                InputsConflictsValue {
                    input_map: 0x1000_0003,
                    conflicting_input_maps: vec![0x1000_0003, 0x1000_0002],
                },
            )],
            ..ActionMap::default()
        };
        let s = snapshot(&map, None, None);
        assert!(s.input_maps_conflict(0x1000_0003, 0x1000_0003));
        assert!(s.input_maps_conflict(0x1000_0003, 0x1000_0002));
        assert!(!s.input_maps_conflict(0x1000_0003, 0x1000_0004));
        assert!(s.input_maps_conflict(0xDEAD_BEEF, 0xDEAD_BEEF));
        assert!(!s.input_maps_conflict(0xDEAD_BEEF, 0x1000_0003));
    }

    #[test]
    fn the_nineteen_input_map_names() {
        assert_eq!(INPUT_MAP_NAMES.len(), 19);
        let name = |id| {
            INPUT_MAP_NAMES
                .iter()
                .find(|(i, _)| *i == id)
                .map(|(_, n)| *n)
        };
        assert_eq!(name(0x4), Some("ID_InputMap_MovementCommands"));
        assert_eq!(name(0x5), Some("ID_InputMap_CameraControls"));
        assert_eq!(name(0x1000_0006), Some("ID_InputMap_Emotes"));
        assert_eq!(
            name(0x1000_0008),
            Some("ID_InputMap_CharacterOptionCommands")
        );
        assert_eq!(name(0x1000_000D), Some("ID_InputMap_ToggleChatEntry"));
    }
}
