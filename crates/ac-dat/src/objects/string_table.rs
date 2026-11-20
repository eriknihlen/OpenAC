use ac_bits::Reader;

use super::DatObject;
use super::read::{self, compressed_string16, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// User-interface text of one language, keyed by string id. File id
/// `0x23......` or `0x24......`, in the local (language) dat.
#[derive(Debug, Clone, PartialEq)]
pub struct StringTable {
    /// File id.
    pub id: u32,
    /// Table version.
    pub version: u32,
    /// Strings keyed by string id, in file order.
    pub strings: Vec<(u32, StringTableString)>,
}

impl DatObject for StringTable {
    const DAT: DatFileType = DatFileType::Local;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let version = r.u32()?;
        let count = hash_table_count(r)?;
        let strings = keyed(r, "strings", count, read::u32, StringTableString::unpack)?;
        Ok(Self {
            id,
            version,
            strings,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

impl StringTable {
    /// The entry with string id `key`.
    #[must_use]
    pub fn get(&self, key: u32) -> Option<&StringTableString> {
        self.strings.iter().find(|(k, _)| *k == key).map(|(_, s)| s)
    }
}

/// One entry: text pieces and the variables substituted between them.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StringTableString {
    /// The table the entry belongs to (0 when not stored).
    pub data_id: u32,
    /// Text pieces.
    pub strings: Vec<String>,
    /// Variable ids.
    pub variables: Vec<u32>,
    /// Whether the variable-name table is worth packing: when set, the
    /// variable names follow.
    pub is_var_name_table_worth_packing: bool,
    /// Variable names (empty when the flag is clear).
    pub var_names: Vec<String>,
}

impl StringTableString {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let data_id = r.u32()?;
        let count = u64::from(r.u32()?);
        let strings = list(r, count, compressed_string16)?;
        let count = u64::from(r.u32()?);
        let variables = list(r, count, read::u32)?;
        // The flag byte is 0 or 1; any other value is a broken entry.
        let is_var_name_table_worth_packing = match r.u8()? {
            0 => false,
            1 => true,
            _ => return Err(DecodeError::Unsupported("string table variable-name flag")),
        };
        let var_names = if is_var_name_table_worth_packing {
            let count = u64::from(r.u32()?);
            list(r, count, compressed_string16)?
        } else {
            Vec::new()
        };
        Ok(Self {
            data_id,
            strings,
            variables,
            is_var_name_table_worth_packing,
            var_names,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn text16(w: &mut Writer, text: &str) {
        let units: Vec<u16> = text.encode_utf16().collect();
        w.compressed_u32(units.len() as u32).unwrap();
        for u in units {
            w.u16(u);
        }
    }

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x2300_0001);
        w.u32(1);
        w.u8(1);
        w.compressed_u32(2).unwrap();
        w.u32(0x052B_A517);
        w.u32(0);
        w.u32(1);
        text16(&mut w, "Low");
        w.u32(0);
        w.u8(0);
        w.u32(0x052B_A518);
        w.u32(0);
        w.u32(2);
        text16(&mut w, "Hello ");
        text16(&mut w, "\u{e9}t\u{e9}");
        w.u32(1);
        w.u32(0x1234);
        w.u8(1);
        // The flag is set, so the variable names follow.
        w.u32(1);
        text16(&mut w, "name");
        w.into_vec()
    }

    #[test]
    fn unpacks_entries_with_text_and_variables() {
        let bytes = sample();
        let r = &mut Reader::new(&bytes);
        let t = StringTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.version, 1);
        assert_eq!(t.get(0x052B_A517).unwrap().strings, vec!["Low"]);
        let s = t.get(0x052B_A518).unwrap();
        assert_eq!(s.strings, vec!["Hello ", "\u{e9}t\u{e9}"]);
        assert_eq!(s.variables, vec![0x1234]);
        assert!(s.is_var_name_table_worth_packing);
        assert_eq!(s.var_names, vec!["name"]);
        assert!(t.get(0x052B_A517).unwrap().var_names.is_empty());
        assert!(t.get(1).is_none());
        for len in 0..bytes.len() {
            assert!(StringTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn a_flag_other_than_zero_or_one_is_an_error() {
        let mut bytes = sample();
        // The first entry's flag byte sits after its one string and its
        // empty variable list.
        let flag_at = 4 + 4 + 1 + 1 + 4 + 4 + 4 + 1 + 6 + 4;
        assert_eq!(bytes[flag_at], 0);
        bytes[flag_at] = 2;
        assert!(StringTable::unpack(&mut Reader::new(&bytes)).is_err());
    }
}
