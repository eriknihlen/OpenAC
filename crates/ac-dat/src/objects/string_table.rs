use ac_bits::Reader;

use super::DatObject;
use super::read::{self, bool8, compressed_string16, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// User-interface text of one language, keyed by string id. File id
/// `0x23......` or `0x24......`, in the local (language) dat.
#[derive(Debug, Clone, PartialEq)]
pub struct StringTable {
    /// File id.
    pub id: u32,
    /// Language number.
    pub language: u32,
    /// Strings keyed by string id, in file order.
    pub strings: Vec<(u32, StringTableString)>,
}

impl DatObject for StringTable {
    const DAT: DatFileType = DatFileType::Local;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let language = r.u32()?;
        let count = hash_table_count(r)?;
        let strings = keyed(r, "strings", count, read::u32, StringTableString::unpack)?;
        Ok(Self {
            id,
            language,
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
    /// Whether the variable-name table is worth packing.
    pub is_var_name_table_worth_packing: bool,
}

impl StringTableString {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let data_id = r.u32()?;
        let count = u64::from(r.u32()?);
        let strings = list(r, count, compressed_string16)?;
        let count = u64::from(r.u32()?);
        let variables = list(r, count, read::u32)?;
        let is_var_name_table_worth_packing = bool8(r)?;
        Ok(Self {
            data_id,
            strings,
            variables,
            is_var_name_table_worth_packing,
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
        w.into_vec()
    }

    #[test]
    fn unpacks_entries_with_text_and_variables() {
        let bytes = sample();
        let r = &mut Reader::new(&bytes);
        let t = StringTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.language, 1);
        assert_eq!(t.get(0x052B_A517).unwrap().strings, vec!["Low"]);
        let s = t.get(0x052B_A518).unwrap();
        assert_eq!(s.strings, vec!["Hello ", "\u{e9}t\u{e9}"]);
        assert_eq!(s.variables, vec![0x1234]);
        assert!(s.is_var_name_table_worth_packing);
        assert!(t.get(1).is_none());
        for len in 0..bytes.len() {
            assert!(StringTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
