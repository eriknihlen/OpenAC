use ac_bits::Reader;

use super::DatObject;
use super::read::{self, compressed_string, hash_table_count, keyed};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// A map from client and server enum values to dat ids and names. File id
/// `0x25......`; the portal header's master map id names the root one,
/// whose entries name further maps by slot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnumIdMap {
    /// File id.
    pub id: u32,
    /// Client enum value to dat id, in file order.
    pub client_enum_to_id: Vec<(u32, u32)>,
    /// Client enum value to name, in file order.
    pub client_enum_to_name: Vec<(u32, String)>,
    /// Server enum value to dat id, in file order.
    pub server_enum_to_id: Vec<(u32, u32)>,
    /// Server enum value to name, in file order.
    pub server_enum_to_name: Vec<(u32, String)>,
}

impl DatObject for EnumIdMap {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = hash_table_count(r)?;
        let client_enum_to_id = keyed(r, "client enum ids", count, read::u32, read::u32)?;
        let count = hash_table_count(r)?;
        let client_enum_to_name =
            keyed(r, "client enum names", count, read::u32, compressed_string)?;
        let count = hash_table_count(r)?;
        let server_enum_to_id = keyed(r, "server enum ids", count, read::u32, read::u32)?;
        let count = hash_table_count(r)?;
        let server_enum_to_name =
            keyed(r, "server enum names", count, read::u32, compressed_string)?;
        Ok(Self {
            id,
            client_enum_to_id,
            client_enum_to_name,
            server_enum_to_id,
            server_enum_to_name,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A map from enum values to dat ids and names whose entries are other
/// maps or names (file id `0x27......`; the material names are one). It
/// is laid out as an [`EnumIdMap`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DualEnumIdMap {
    /// The map.
    pub map: EnumIdMap,
}

impl DatObject for DualEnumIdMap {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            map: EnumIdMap::unpack(r)?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.map.id = id;
    }
}

impl DualEnumIdMap {
    /// The name stored for client enum value `key`.
    #[must_use]
    pub fn client_name(&self, key: u32) -> Option<&str> {
        self.map
            .client_enum_to_name
            .iter()
            .find(|(k, _)| *k == key)
            .map(|(_, v)| v.as_str())
    }
}

impl EnumIdMap {
    /// The dat id stored for client enum value `key`.
    #[must_use]
    pub fn client_id(&self, key: u32) -> Option<u32> {
        self.client_enum_to_id
            .iter()
            .find(|(k, _)| *k == key)
            .map(|(_, v)| *v)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn map() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x2500_0001);
        // Client ids: two entries.
        w.u8(1);
        w.compressed_u32(2).unwrap();
        for (k, v) in [(7u32, 0x2500_0010u32), (3, 0x2500_0020)] {
            w.u32(k);
            w.u32(v);
        }
        // Client names: one entry.
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.u32(7);
        w.compressed_u32(5).unwrap();
        w.bytes(b"Sound");
        // Server ids and names: empty.
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.into_vec()
    }

    #[test]
    fn unpacks_an_enum_id_map() {
        let bytes = map();
        let r = &mut Reader::new(&bytes);
        let m = EnumIdMap::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(m.id, 0x2500_0001);
        assert_eq!(m.client_id(7), Some(0x2500_0010));
        assert_eq!(m.client_id(3), Some(0x2500_0020));
        assert_eq!(m.client_id(4), None);
        assert_eq!(m.client_enum_to_name, vec![(7, "Sound".to_owned())]);
        assert!(m.server_enum_to_id.is_empty() && m.server_enum_to_name.is_empty());
        for len in 0..bytes.len() {
            assert!(EnumIdMap::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
