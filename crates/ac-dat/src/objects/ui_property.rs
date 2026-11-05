//! Typed UI property values (the property bags of layout states) and the
//! master property table that says which type each property id holds.

use std::collections::HashMap;

use ac_bits::Reader;

use super::DatObject;
use super::read::{self, bool8, compressed_string, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::{ColorArgb, Vector3};

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the one master property table in the portal dat.
pub const MASTER_PROPERTY_ID: u32 = 0x3900_0001;

/// Property arrays and structs nest at most this deep; deeper input is
/// refused rather than recursed into.
const MAX_PROPERTY_DEPTH: usize = 16;

/// The value type of a property, as the master property table records it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct BasePropertyType(pub u32);

impl BasePropertyType {
    /// No type.
    pub const INVALID: Self = Self(0);
    /// One-byte boolean.
    pub const BOOL: Self = Self(1);
    /// Signed 32-bit integer.
    pub const INTEGER: Self = Self(2);
    /// 64-bit integer (no UI property uses it).
    pub const LONG_INTEGER: Self = Self(3);
    /// 32-bit float.
    pub const FLOAT: Self = Self(4);
    /// Three floats.
    pub const VECTOR: Self = Self(5);
    /// Colour, blue first.
    pub const COLOR: Self = Self(6);
    /// Plain string (no UI property uses it).
    pub const STRING: Self = Self(7);
    /// Reference into a string table.
    pub const STRING_INFO: Self = Self(8);
    /// Enumeration value.
    pub const ENUM: Self = Self(9);
    /// Dat file id.
    pub const DATA_ID: Self = Self(10);
    /// Waveform (no UI property uses it).
    pub const WAVEFORM: Self = Self(11);
    /// Object instance id.
    pub const INSTANCE_ID: Self = Self(12);
    /// Position (no UI property uses it).
    pub const POSITION: Self = Self(13);
    /// Time stamp (no UI property uses it).
    pub const TIME_STAMP: Self = Self(14);
    /// 32 flag bits.
    pub const BITFIELD32: Self = Self(15);
    /// 64 flag bits.
    pub const BITFIELD64: Self = Self(16);
    /// List of properties.
    pub const ARRAY: Self = Self(17);
    /// Properties keyed by member id.
    pub const STRUCT: Self = Self(18);
    /// String token (no UI property uses it).
    pub const STRING_TOKEN: Self = Self(19);
    /// Property name (no UI property uses it).
    pub const PROPERTY_NAME: Self = Self(20);
    /// Three-state value (no UI property uses it).
    pub const TRI_STATE: Self = Self(21);
}

/// A reference to localised text: a string id in a string table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct StringInfo {
    /// Which of the entry's strings to use.
    pub token: u8,
    /// String id within the table.
    pub string_id: u32,
    /// String table file id (`0x23......` / `0x24......`).
    pub table_id: u32,
    /// Override flag (0 none, 1 literal, 2 generated).
    pub override_flag: u8,
    /// Whether the English text is stored.
    pub english: u8,
    /// Whether a comment is stored.
    pub comment: u8,
}

impl StringInfo {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            token: r.u8()?,
            string_id: r.u32()?,
            table_id: r.u32()?,
            override_flag: r.u8()?,
            english: r.u8()?,
            comment: r.u8()?,
        })
    }
}

/// One property value.
#[derive(Debug, Clone, PartialEq)]
pub enum PropertyValue {
    /// Enumeration value.
    Enum(u32),
    /// Boolean.
    Bool(bool),
    /// Dat file id.
    DataId(u32),
    /// Float.
    Float(f32),
    /// Signed integer.
    Integer(i32),
    /// String-table reference.
    StringInfo(StringInfo),
    /// Colour.
    Color(ColorArgb),
    /// List of properties, in file order.
    Array(Vec<Property>),
    /// Members keyed by member id, in file order.
    Struct(Vec<(u32, Property)>),
    /// Three floats.
    Vector(Vector3),
    /// 32 flag bits.
    Bitfield32(u32),
    /// 64 flag bits.
    Bitfield64(u64),
    /// Object instance id.
    InstanceId(u32),
}

/// A property value with the master property id it was stored under.
#[derive(Debug, Clone, PartialEq)]
pub struct Property {
    /// Master property id (0 for a master table's default/min/max value).
    pub master_property_id: u32,
    /// The value.
    pub value: PropertyValue,
}

impl Property {
    /// Read a stored property: its master property id, then a value of the
    /// type the master table gives that id.
    pub(crate) fn unpack(r: &mut Reader<'_>, master: &MasterProperty) -> Result<Self> {
        unpack_property(r, master, 0)
    }
}

fn unpack_property(r: &mut Reader<'_>, master: &MasterProperty, depth: usize) -> Result<Property> {
    let key = r.u32()?;
    let kind = master
        .property_type(key)
        .ok_or(DecodeError::UnknownProperty(key))?;
    let value = unpack_value(r, kind, Some(master), depth)?;
    Ok(Property {
        master_property_id: key,
        value,
    })
}

/// Read a value of type `kind`. Arrays and structs hold stored properties,
/// which need the master table; without one (while the table itself is
/// being read) they are refused.
fn unpack_value(
    r: &mut Reader<'_>,
    kind: BasePropertyType,
    master: Option<&MasterProperty>,
    depth: usize,
) -> Result<PropertyValue> {
    if depth > MAX_PROPERTY_DEPTH {
        return Err(DecodeError::TooDeep("UI properties"));
    }
    Ok(match kind {
        BasePropertyType::ENUM => PropertyValue::Enum(r.u32()?),
        BasePropertyType::BOOL => PropertyValue::Bool(bool8(r)?),
        BasePropertyType::DATA_ID => PropertyValue::DataId(r.u32()?),
        BasePropertyType::FLOAT => PropertyValue::Float(r.f32()?),
        BasePropertyType::INTEGER => PropertyValue::Integer(r.i32()?),
        BasePropertyType::STRING_INFO => PropertyValue::StringInfo(StringInfo::unpack(r)?),
        BasePropertyType::COLOR => PropertyValue::Color(ColorArgb::unpack(r)?),
        BasePropertyType::VECTOR => PropertyValue::Vector(Vector3::unpack(r)?),
        BasePropertyType::BITFIELD32 => PropertyValue::Bitfield32(r.u32()?),
        BasePropertyType::BITFIELD64 => PropertyValue::Bitfield64(r.u64()?),
        BasePropertyType::INSTANCE_ID => PropertyValue::InstanceId(r.u32()?),
        BasePropertyType::ARRAY => {
            let master = master.ok_or(DecodeError::Unsupported("array master default"))?;
            let count = u64::from(r.u32()?);
            PropertyValue::Array(list(r, count, |r| unpack_property(r, master, depth + 1))?)
        }
        BasePropertyType::STRUCT => {
            let master = master.ok_or(DecodeError::Unsupported("struct master default"))?;
            let _buckets = r.u8()?;
            let count = u64::from(r.u8()?);
            PropertyValue::Struct(keyed(r, "struct members", count, read::u32, |r| {
                unpack_property(r, master, depth + 1)
            })?)
        }
        other => return Err(DecodeError::UnsupportedPropertyType(other.0)),
    })
}

/// Id-to-name table of an enumeration, layered on another by id.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct EnumMapperData {
    /// The mapping this one extends (0 for none).
    pub base_enum_map: u32,
    /// Unnamed word stored after the base id.
    pub unknown: u32,
    /// Id to name, in file order.
    pub id_to_string: Vec<(u32, String)>,
}

impl EnumMapperData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            base_enum_map: r.u32()?,
            unknown: r.u32()?,
            id_to_string: id_to_string(r)?,
        })
    }
}

/// An auto-growing hash table of id to one-byte name.
pub(crate) fn id_to_string(r: &mut Reader<'_>) -> Result<Vec<(u32, String)>> {
    let count = hash_table_count(r)?;
    keyed(r, "enum names", count, read::u32, compressed_string)
}

/// What the master table says about one property id.
#[derive(Debug, Clone, PartialEq)]
pub struct BasePropertyDesc {
    /// Name hash.
    pub name: u32,
    /// Value type.
    pub kind: BasePropertyType,
    /// Property group.
    pub group: u32,
    /// Provider.
    pub provider: u32,
    /// Extra data (for an enum, the enum mapper id).
    pub data: u32,
    /// Patch flags.
    pub patch_flags: i32,
    /// Default value, when stored.
    pub default_value: Option<PropertyValue>,
    /// Largest value, when stored.
    pub max_value: Option<PropertyValue>,
    /// Smallest value, when stored.
    pub min_value: Option<PropertyValue>,
    /// Prediction timeout in seconds.
    pub prediction_timeout: f32,
    /// Inheritance kind.
    pub inheritance_type: u8,
    /// Which dat the value lives in.
    pub dat_file_type: u8,
    /// Propagation kind.
    pub propagation_type: u8,
    /// Caching kind.
    pub caching_type: u8,
    /// Required.
    pub required: bool,
    /// Read only.
    pub read_only: bool,
    /// Not checkpointed.
    pub no_checkpoint: bool,
    /// Recorded.
    pub recorded: bool,
    /// Not replayed.
    pub do_not_replay: bool,
    /// Absolute time stamp.
    pub absolute_time_stamp: bool,
    /// Groupable.
    pub groupable: bool,
    /// Propagates to children.
    pub propagate_to_children: bool,
    /// Available sub-properties, in file order.
    pub available_properties: Vec<(u32, u32)>,
}

impl BasePropertyDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = r.u32()?;
        let kind = BasePropertyType(r.u32()?);
        let group = r.u32()?;
        let provider = r.u32()?;
        let data = r.u32()?;
        let patch_flags = r.i32()?;
        let optional = |r: &mut Reader<'_>| -> Result<Option<PropertyValue>> {
            if bool8(r)? {
                unpack_value(r, kind, None, 0).map(Some)
            } else {
                Ok(None)
            }
        };
        let default_value = optional(r)?;
        let max_value = optional(r)?;
        let min_value = optional(r)?;
        let prediction_timeout = r.f32()?;
        let inheritance_type = r.u8()?;
        let dat_file_type = r.u8()?;
        let propagation_type = r.u8()?;
        let caching_type = r.u8()?;
        let required = bool8(r)?;
        let read_only = bool8(r)?;
        let no_checkpoint = bool8(r)?;
        let recorded = bool8(r)?;
        let do_not_replay = bool8(r)?;
        let absolute_time_stamp = bool8(r)?;
        let groupable = bool8(r)?;
        let propagate_to_children = bool8(r)?;
        let _buckets = r.u8()?;
        let count = u64::from(r.u8()?);
        let available_properties = keyed(r, "available properties", count, read::u32, read::u32)?;
        Ok(Self {
            name,
            kind,
            group,
            provider,
            data,
            patch_flags,
            default_value,
            max_value,
            min_value,
            prediction_timeout,
            inheritance_type,
            dat_file_type,
            propagation_type,
            caching_type,
            required,
            read_only,
            no_checkpoint,
            recorded,
            do_not_replay,
            absolute_time_stamp,
            groupable,
            propagate_to_children,
            available_properties,
        })
    }
}

/// The table of every property id: its value type and limits. File id
/// [`MASTER_PROPERTY_ID`] in the portal dat. UI layouts cannot be read
/// without it, because a stored property names only its id.
#[derive(Debug, Clone, PartialEq)]
pub struct MasterProperty {
    /// File id.
    pub id: u32,
    /// Names of the property ids.
    pub enum_mapper: EnumMapperData,
    /// Property descriptions keyed by property id, in file order.
    pub properties: Vec<(u32, BasePropertyDesc)>,
    index: HashMap<u32, usize>,
}

impl MasterProperty {
    /// A table built from descriptions (tests and tools).
    #[must_use]
    pub fn new(
        id: u32,
        enum_mapper: EnumMapperData,
        properties: Vec<(u32, BasePropertyDesc)>,
    ) -> Self {
        let index = properties
            .iter()
            .enumerate()
            .map(|(i, (key, _))| (*key, i))
            .collect();
        Self {
            id,
            enum_mapper,
            properties,
            index,
        }
    }

    /// The description of property `id`.
    #[must_use]
    pub fn get(&self, id: u32) -> Option<&BasePropertyDesc> {
        self.index.get(&id).map(|&i| &self.properties[i].1)
    }

    /// The value type of property `id`.
    #[must_use]
    pub fn property_type(&self, id: u32) -> Option<BasePropertyType> {
        self.get(id).map(|d| d.kind)
    }
}

impl DatObject for MasterProperty {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let enum_mapper = EnumMapperData::unpack(r)?;
        let count = hash_table_count(r)?;
        let properties = keyed(
            r,
            "master properties",
            count,
            read::u32,
            BasePropertyDesc::unpack,
        )?;
        Ok(Self::new(id, enum_mapper, properties))
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A property description with only a type, for tests.
#[cfg(test)]
pub(crate) fn test_desc(kind: BasePropertyType) -> BasePropertyDesc {
    BasePropertyDesc {
        name: 0,
        kind,
        group: 0,
        provider: 0,
        data: 0,
        patch_flags: 0,
        default_value: None,
        max_value: None,
        min_value: None,
        prediction_timeout: 0.0,
        inheritance_type: 0,
        dat_file_type: 0,
        propagation_type: 0,
        caching_type: 0,
        required: false,
        read_only: false,
        no_checkpoint: false,
        recorded: false,
        do_not_replay: false,
        absolute_time_stamp: false,
        groupable: false,
        propagate_to_children: false,
        available_properties: Vec::new(),
    }
}

/// A master table with the given id → type entries, for tests.
#[cfg(test)]
pub(crate) fn test_master(entries: &[(u32, BasePropertyType)]) -> MasterProperty {
    MasterProperty::new(
        MASTER_PROPERTY_ID,
        EnumMapperData::default(),
        entries.iter().map(|&(k, t)| (k, test_desc(t))).collect(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn master_table_entry_with_default_and_available_properties() {
        let mut w = Writer::new();
        w.u32(MASTER_PROPERTY_ID);
        // Enum mapper data: base, unknown, one name.
        w.u32(0x2200_0001);
        w.u32(7);
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.u32(0x14);
        w.compressed_u32(5).unwrap();
        w.bytes(b"Horiz");
        // One property: 0x14, an enum with a default of 2.
        w.u8(6);
        w.compressed_u32(1).unwrap();
        w.u32(0x14);
        w.u32(0xAB);
        w.u32(9);
        w.u32(1);
        w.u32(2);
        w.u32(0x2200_0010);
        w.i32(-1);
        w.u8(1);
        w.u32(2);
        w.u8(0);
        w.u8(0);
        w.f32(0.5);
        for b in [1u8, 2, 3, 4, 1, 0, 1, 0, 0, 0, 1, 1] {
            w.u8(b);
        }
        w.u8(1);
        w.u8(1);
        w.u32(0x30);
        w.u32(0x31);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let m = MasterProperty::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(m.enum_mapper.id_to_string, vec![(0x14, "Horiz".to_owned())]);
        let d = m.get(0x14).unwrap();
        assert_eq!(d.kind, BasePropertyType::ENUM);
        assert_eq!(d.default_value, Some(PropertyValue::Enum(2)));
        assert_eq!(d.max_value, None);
        assert_eq!(d.patch_flags, -1);
        assert_eq!((d.inheritance_type, d.caching_type), (1, 4));
        assert!(d.required && d.no_checkpoint && d.groupable && d.propagate_to_children);
        assert!(!d.read_only);
        assert_eq!(d.available_properties, vec![(0x30, 0x31)]);
        assert_eq!(m.property_type(0x15), None);
        for len in 0..bytes.len() {
            assert!(MasterProperty::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn stored_properties_read_their_type_from_the_master_table() {
        let master = test_master(&[
            (0x1A, BasePropertyType::ARRAY),
            (0x1B, BasePropertyType::COLOR),
            (0x2E, BasePropertyType::STRUCT),
            (0x30, BasePropertyType::DATA_ID),
            (0x32, BasePropertyType::BOOL),
            (0x17, BasePropertyType::STRING_INFO),
        ]);
        let mut w = Writer::new();
        // Array of one data id.
        w.u32(0x1A);
        w.u32(1);
        w.u32(0x30);
        w.u32(0x4000_0001);
        // Struct of one bool.
        w.u32(0x2E);
        w.u8(0);
        w.u8(1);
        w.u32(0x32);
        w.u32(0x32);
        w.u8(1);
        // String info.
        w.u32(0x17);
        w.u8(1);
        w.u32(0x10);
        w.u32(0x2300_0001);
        w.u8(0);
        w.u8(1);
        w.u8(0);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let a = Property::unpack(r, &master).unwrap();
        assert_eq!(
            a.value,
            PropertyValue::Array(vec![Property {
                master_property_id: 0x30,
                value: PropertyValue::DataId(0x4000_0001)
            }])
        );
        let s = Property::unpack(r, &master).unwrap();
        assert_eq!(
            s.value,
            PropertyValue::Struct(vec![(
                0x32,
                Property {
                    master_property_id: 0x32,
                    value: PropertyValue::Bool(true)
                }
            )])
        );
        let t = Property::unpack(r, &master).unwrap();
        assert_eq!(
            t.value,
            PropertyValue::StringInfo(StringInfo {
                token: 1,
                string_id: 0x10,
                table_id: 0x2300_0001,
                override_flag: 0,
                english: 1,
                comment: 0
            })
        );
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            Property::unpack(&mut Reader::new(&[9, 0, 0, 0, 1]), &master),
            Err(DecodeError::UnknownProperty(9))
        );
    }

    #[test]
    fn unported_value_types_are_refused() {
        let master = test_master(&[(1, BasePropertyType::POSITION)]);
        assert_eq!(
            Property::unpack(&mut Reader::new(&[1, 0, 0, 0, 0, 0, 0, 0]), &master),
            Err(DecodeError::UnsupportedPropertyType(13))
        );
    }

    #[test]
    fn self_nesting_arrays_stop_at_the_depth_limit() {
        let master = test_master(&[(1, BasePropertyType::ARRAY)]);
        let mut w = Writer::new();
        for _ in 0..40 {
            w.u32(1);
            w.u32(1);
        }
        assert_eq!(
            Property::unpack(&mut Reader::new(w.as_slice()), &master),
            Err(DecodeError::TooDeep("UI properties"))
        );
    }
}
