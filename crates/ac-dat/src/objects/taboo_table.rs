use ac_bits::Reader;

use super::DatObject;
use super::read::{self, compressed_string, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the taboo table.
pub const TABOO_TABLE_ID: u32 = 0x0E00_001E;

/// The audience whose banned patterns the chat language filter reads.
pub const CHAT_AUDIENCE: u32 = 1;

/// The words the language filter censors, per audience (chat is audience
/// 1). File id `0x0E00001E`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TabooTable {
    /// File id.
    pub id: u32,
    /// Each audience's entry, keyed by audience, in file order.
    pub audiences: Vec<(u32, TabooTableEntry)>,
}

impl TabooTable {
    /// The banned patterns of `audience`, if the table has it.
    #[must_use]
    pub fn patterns(&self, audience: u32) -> Option<&[String]> {
        self.audiences
            .iter()
            .find(|(key, _)| *key == audience)
            .map(|(_, entry)| entry.banned_patterns.as_slice())
    }
}

impl DatObject for TabooTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = hash_table_count(r)?;
        let audiences = keyed(
            r,
            "taboo audiences",
            count,
            read::u32,
            TabooTableEntry::unpack,
        )?;
        Ok(Self { id, audiences })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One audience's banned patterns (`*` matches any run of characters).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TabooTableEntry {
    /// The entry's own key.
    pub key: u32,
    /// A word the file carries; its meaning is not known.
    pub unknown2: u16,
    /// The patterns, in file order.
    pub banned_patterns: Vec<String>,
}

impl TabooTableEntry {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let key = r.u32()?;
        let unknown2 = r.u16()?;
        let count = u64::from(r.u32()?);
        let banned_patterns = list(r, count, compressed_string)?;
        Ok(Self {
            key,
            unknown2,
            banned_patterns,
        })
    }
}

#[cfg(test)]
mod tests {
    //! The layout of the taboo table and its entries, with a round-trip.

    use super::*;
    use ac_bits::Writer;

    fn pstring(w: &mut Writer, text: &str) {
        w.compressed_u32(u32::try_from(text.len()).unwrap())
            .unwrap();
        for b in text.bytes() {
            w.u8(b);
        }
    }

    #[test]
    fn audiences_hold_their_patterns() {
        let mut w = Writer::new();
        w.u32(TABOO_TABLE_ID);
        w.u8(1); // bucket size index
        w.compressed_u32(1).unwrap();
        w.u32(1); // audience
        w.u32(0); // the entry's key
        w.u16(0);
        w.u32(2);
        pstring(&mut w, "test");
        pstring(&mut w, "test2");
        let table = TabooTable::unpack(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(table.id, TABOO_TABLE_ID);
        assert_eq!(
            table.patterns(CHAT_AUDIENCE),
            Some(&["test".to_owned(), "test2".to_owned()][..])
        );
        assert_eq!(table.patterns(2), None);
    }

    #[test]
    fn a_cut_table_is_an_error() {
        let mut w = Writer::new();
        w.u32(TABOO_TABLE_ID);
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.u32(1);
        w.u32(0);
        w.u16(0);
        w.u32(1);
        assert!(TabooTable::unpack(&mut Reader::new(w.as_slice())).is_err());
    }
}
