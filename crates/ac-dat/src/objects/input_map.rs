//! The game's key-map tables: the action map (which actions each input map
//! has, which of them a player may bind, and which input maps conflict)
//! and the master input maps (the default keys of each input map).

use ac_bits::Reader;

use super::DatObject;
use super::read::{self, compressed_string, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// The action map's file id.
pub const ACTION_MAP_ID: u32 = 0x2600_0000;
/// The gameplay master input map's file id.
pub const GAMEPLAY_MASTER_INPUT_MAP_ID: u32 = 0x1400_0000;
/// The system master input map's file id.
pub const SYSTEM_MASTER_INPUT_MAP_ID: u32 = 0x1400_0002;

/// What the key configuration screen shows of an action.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct UserBindingData {
    /// The action class (0: the player cannot bind it).
    pub action_class: u32,
    /// The action's name (a string table hash).
    pub action_name: u32,
    /// The action's description (a string table hash).
    pub action_description: u32,
}

/// One action of an input map.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ActionMapValue {
    /// Magic.
    pub magic: u32,
    /// Unknown byte.
    pub unknown: u8,
    /// Toggle type.
    pub toggle_type: u32,
    /// Dummy list length.
    pub dummy_list_length: u32,
    /// What the player sees of it.
    pub user_binding: UserBindingData,
}

/// Which input maps conflict with one.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct InputsConflictsValue {
    /// The input map.
    pub input_map: u32,
    /// The input maps it conflicts with.
    pub conflicting_input_maps: Vec<u32>,
}

/// The action map. File id `0x26000000`, in the portal dat.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ActionMap {
    /// File id.
    pub id: u32,
    /// Input map id → (action id → action), in file order.
    pub input_maps: Vec<(u32, Vec<(u32, ActionMapValue)>)>,
    /// The string table of the names.
    pub string_table_id: u32,
    /// Input map id → its conflicts, in file order.
    pub conflicting_maps: Vec<(u32, InputsConflictsValue)>,
}

impl DatObject for ActionMap {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = hash_table_count(r)?;
        let input_maps = keyed(r, "action map input maps", count, read::u32, |r| {
            let count = hash_table_count(r)?;
            keyed(r, "action map actions", count, read::u32, action_map_value)
        })?;
        let string_table_id = r.u32()?;
        let count = hash_table_count(r)?;
        let conflicting_maps = keyed(r, "action map conflicts", count, read::u32, |r| {
            let input_map = r.u32()?;
            let n = r.u32()?;
            Ok(InputsConflictsValue {
                input_map,
                conflicting_input_maps: list(r, u64::from(n), read::u32)?,
            })
        })?;
        Ok(Self {
            id,
            input_maps,
            string_table_id,
            conflicting_maps,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

fn action_map_value(r: &mut Reader<'_>) -> Result<ActionMapValue> {
    Ok(ActionMapValue {
        magic: r.u32()?,
        unknown: r.u8()?,
        toggle_type: r.u32()?,
        dummy_list_length: r.u32()?,
        user_binding: UserBindingData {
            action_class: r.u32()?,
            action_name: r.u32()?,
            action_description: r.u32()?,
        },
    })
}

/// A key: (scan << 16 | device) and the retail modifier word.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ControlSpecification {
    /// Scan code in the high word, device in the low word.
    pub key: u32,
    /// Modifier word.
    pub modifier: u32,
}

/// One default binding of an input map.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct QualifiedControl {
    /// The key.
    pub key: ControlSpecification,
    /// How it fires.
    pub activation: u32,
    /// The action id it fires.
    pub unknown: u32,
}

/// A device the map names.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DeviceKeyMapEntry {
    /// Device type.
    pub kind: u8,
    /// Device GUID bytes.
    pub guid: [u8; 16],
}

/// A master input map. File ids `0x14000000..=0x1400FFFF`, in the portal
/// dat.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MasterInputMap {
    /// File id.
    pub id: u32,
    /// Name.
    pub name: String,
    /// The map's GUID bytes.
    pub guid_map: [u8; 16],
    /// Devices.
    pub devices: Vec<DeviceKeyMapEntry>,
    /// Modifier keys.
    pub meta_keys: Vec<ControlSpecification>,
    /// Input map id → its default bindings, in file order.
    pub input_maps: Vec<(u32, Vec<QualifiedControl>)>,
}

fn control_specification(r: &mut Reader<'_>) -> Result<ControlSpecification> {
    Ok(ControlSpecification {
        key: r.u32()?,
        modifier: r.u32()?,
    })
}

impl DatObject for MasterInputMap {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let name = compressed_string(r)?;
        let guid_map = r.array::<16>()?;
        let n = r.u32()?;
        let devices = list(r, u64::from(n), |r| {
            Ok(DeviceKeyMapEntry {
                kind: r.u8()?,
                guid: r.array::<16>()?,
            })
        })?;
        let n = r.u32()?;
        let meta_keys = list(r, u64::from(n), control_specification)?;
        let n = r.u32()?;
        let input_maps = keyed(r, "master input maps", u64::from(n), read::u32, |r| {
            let n = r.u32()?;
            list(r, u64::from(n), |r| {
                Ok(QualifiedControl {
                    key: control_specification(r)?,
                    activation: r.u32()?,
                    unknown: r.u32()?,
                })
            })
        })?;
        Ok(Self {
            id,
            name,
            guid_map,
            devices,
            meta_keys,
            input_maps,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn an_action_map_unpacks_its_tables() {
        let mut w = Writer::new();
        w.u32(ACTION_MAP_ID);
        w.u8(23);
        w.compressed_u32(1).unwrap();
        w.u32(4);
        w.u8(11);
        w.compressed_u32(1).unwrap();
        w.u32(0x29);
        w.u32(0xAABB_CCDD); // magic
        w.u8(7);
        w.u32(2); // toggle type
        w.u32(0);
        w.u32(1); // movement
        w.u32(0x111);
        w.u32(0x222);
        w.u32(0x2300_0005);
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.u32(0x1000_0003);
        w.u32(0x1000_0003);
        w.u32(2);
        w.u32(0x1000_0003);
        w.u32(0x1000_0002);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let map = ActionMap::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        let (input_map, actions) = &map.input_maps[0];
        assert_eq!(*input_map, 4);
        assert_eq!(actions[0].0, 0x29);
        assert_eq!(actions[0].1.user_binding.action_class, 1);
        assert_eq!(actions[0].1.user_binding.action_description, 0x222);
        assert_eq!(map.string_table_id, 0x2300_0005);
        assert_eq!(
            map.conflicting_maps[0].1.conflicting_input_maps,
            [0x1000_0003, 0x1000_0002]
        );
        for len in 0..bytes.len() {
            assert!(ActionMap::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn a_master_input_map_unpacks_its_bindings() {
        let mut w = Writer::new();
        w.u32(GAMEPLAY_MASTER_INPUT_MAP_ID);
        w.compressed_u32(4).unwrap();
        w.bytes(b"Main");
        w.bytes(&[9; 16]);
        w.u32(1);
        w.u8(1);
        w.bytes(&[3; 16]);
        w.u32(1);
        w.u32(0x002A_0000);
        w.u32(1);
        w.u32(1);
        w.u32(5);
        w.u32(1);
        w.u32(0x00B5_0000);
        w.u32(0);
        w.u32(3);
        w.u32(0x3E);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let map = MasterInputMap::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(map.name, "Main");
        assert_eq!(map.devices[0].kind, 1);
        assert_eq!(map.meta_keys[0].key >> 16, 0x2A);
        let (input_map, controls) = &map.input_maps[0];
        assert_eq!(*input_map, 5);
        assert_eq!(controls[0].key.key >> 16, 0xB5);
        assert_eq!(controls[0].unknown, 0x3E);
        for len in 0..bytes.len() {
            assert!(MasterInputMap::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
