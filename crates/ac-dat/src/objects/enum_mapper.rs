//! Enumeration names: id → text tables that may extend another table.

use ac_bits::Reader;

use super::DatObject;
use super::ui_property::id_to_string;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// Names of one enumeration. File id `0x22......`, in the portal dat.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnumMapper {
    /// File id.
    pub id: u32,
    /// The mapping this one extends (0 for none).
    pub base_enum_map: u32,
    /// Id to name, in file order.
    pub id_to_string: Vec<(u32, String)>,
}

impl EnumMapper {
    /// The name of `id` in this mapping alone (the base is not consulted).
    #[must_use]
    pub fn get(&self, id: u32) -> Option<&str> {
        self.id_to_string
            .iter()
            .find(|(k, _)| *k == id)
            .map(|(_, s)| s.as_str())
    }
}

impl DatObject for EnumMapper {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            id: r.u32()?,
            base_enum_map: r.u32()?,
            id_to_string: id_to_string(r)?,
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
    fn unpacks_names_and_base() {
        let mut w = Writer::new();
        w.u32(0x2200_000E);
        w.u32(0x2200_0001);
        w.u8(2);
        w.compressed_u32(2).unwrap();
        w.u32(1);
        w.compressed_u32(6).unwrap();
        w.bytes(b"Olthoi");
        w.u32(0x12);
        w.compressed_u32(9).unwrap();
        w.bytes(b"Bunny_Rat");
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let m = EnumMapper::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(m.base_enum_map, 0x2200_0001);
        assert_eq!(m.get(0x12), Some("Bunny_Rat"));
        assert_eq!(m.get(2), None);
        for len in 0..bytes.len() {
            assert!(EnumMapper::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
