use ac_bits::{BitsError, Reader};

/// Which kind of dat a file is, from its header.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DatFileType {
    /// Type 0: not set.
    Undefined,
    /// Type 1: portal and high-resolution dats (models, textures, tables).
    Portal,
    /// Type 2: cell dats (landblocks and interior cells).
    Cell,
    /// Type 3: language dats (strings, fonts).
    Local,
    /// Any other value.
    Other(u32),
}

impl DatFileType {
    /// Decode the header's type field.
    #[must_use]
    pub fn from_raw(raw: u32) -> Self {
        match raw {
            0 => Self::Undefined,
            1 => Self::Portal,
            2 => Self::Cell,
            3 => Self::Local,
            other => Self::Other(other),
        }
    }

    /// The header's type field.
    #[must_use]
    pub fn raw(self) -> u32 {
        match self {
            Self::Undefined => 0,
            Self::Portal => 1,
            Self::Cell => 2,
            Self::Local => 3,
            Self::Other(other) => other,
        }
    }
}

/// The fixed 400-byte header at the start of every dat file.
///
/// Signed fields stay signed because the format stores them that way;
/// unused fields often hold the fill pattern `0xCDCDCDCD`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DatHeader {
    /// Version text, ASCII, trailing NULs removed. Empty in retail dats.
    pub version: String,
    /// Last transaction record.
    pub transactions: [u8; 64],
    /// Always [`DatHeader::RETAIL_MAGIC`] in a valid dat.
    pub magic: i32,
    /// Size of every block in bytes, including its 4-byte next pointer.
    pub block_size: i32,
    /// File size the header records, in bytes.
    pub file_size: i32,
    /// Which kind of dat this is.
    pub file_type: DatFileType,
    /// Sub-kind: the region for cell dats, 0 for the portal dat.
    pub subset: u32,
    /// Offset of the first free block.
    pub first_free_block: i32,
    /// Offset of the last free block.
    pub last_free_block: i32,
    /// Number of free blocks.
    pub free_block_count: i32,
    /// Offset of the directory's root node; 0 for an empty directory.
    pub root_block: i32,
    /// Recently-used cache bookkeeping (unused by readers).
    pub new_lru: i32,
    /// Recently-used cache bookkeeping (unused by readers).
    pub old_lru: i32,
    /// Recently-used cache switch (unused by readers).
    pub use_lru: i32,
    /// File id of the master map.
    pub master_map_id: i32,
    /// Engine version.
    pub engine_version: i32,
    /// Game version.
    pub game_version: i32,
    /// Major version, a GUID in its 16 stored bytes.
    pub major_version: [u8; 16],
    /// Minor version.
    pub minor_version: u32,
}

impl DatHeader {
    /// Size of the header on disk.
    pub const SIZE: usize = 400;

    /// The magic every retail dat carries.
    pub const RETAIL_MAGIC: i32 = 0x0000_5442;

    /// Decode the header from the first [`DatHeader::SIZE`] bytes of a dat.
    pub fn unpack(data: &[u8]) -> Result<Self, BitsError> {
        let mut r = Reader::new(data);
        let version = ascii_trim_nuls(r.bytes(256)?);
        Ok(Self {
            version,
            transactions: r.array()?,
            magic: r.i32()?,
            block_size: r.i32()?,
            file_size: r.i32()?,
            file_type: DatFileType::from_raw(r.u32()?),
            subset: r.u32()?,
            first_free_block: r.i32()?,
            last_free_block: r.i32()?,
            free_block_count: r.i32()?,
            root_block: r.i32()?,
            new_lru: r.i32()?,
            old_lru: r.i32()?,
            use_lru: r.i32()?,
            master_map_id: r.i32()?,
            engine_version: r.i32()?,
            game_version: r.i32()?,
            major_version: r.array()?,
            minor_version: r.u32()?,
        })
    }

    /// The major version in the usual GUID text form
    /// (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), whose first three groups
    /// are stored little-endian.
    #[must_use]
    pub fn major_version_text(&self) -> String {
        let b = &self.major_version;
        let d1 = u32::from_le_bytes([b[0], b[1], b[2], b[3]]);
        let d2 = u16::from_le_bytes([b[4], b[5]]);
        let d3 = u16::from_le_bytes([b[6], b[7]]);
        let mut text = format!("{d1:08x}-{d2:04x}-{d3:04x}-{:02x}{:02x}-", b[8], b[9]);
        for byte in &b[10..] {
            text.push_str(&format!("{byte:02x}"));
        }
        text
    }
}

/// ASCII decode as the header's version text is read: bytes above 0x7F
/// become `?`, and trailing NULs are dropped (interior ones stay).
fn ascii_trim_nuls(bytes: &[u8]) -> String {
    let text: String = bytes
        .iter()
        .map(|&b| if b < 0x80 { char::from(b) } else { '?' })
        .collect();
    text.trim_end_matches('\0').to_owned()
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use ac_bits::Writer;

    /// A header with recognisable values in every field.
    pub(crate) fn sample_header_bytes() -> Vec<u8> {
        let mut w = Writer::new();
        let mut version = [0u8; 256];
        version[..3].copy_from_slice(b"v1\xE9");
        w.bytes(&version);
        let mut transactions = [0u8; 64];
        transactions[..4].copy_from_slice(&[0x00, 0x50, 0x4C, 0x00]);
        w.bytes(&transactions);
        w.i32(DatHeader::RETAIL_MAGIC);
        w.i32(256); // block size
        w.i32(0x1000); // file size
        w.u32(2); // cell
        w.u32(1); // subset
        w.i32(0x800);
        w.i32(0xF00);
        w.i32(3);
        w.i32(0x400); // root
        w.i32(0);
        w.i32(0);
        w.i32(0xCDCD_CDCDu32 as i32);
        w.i32(0x2500_0000);
        w.i32(22);
        w.i32(0);
        // Bytes of the GUID 34a7d7d2-722f-4c46-8ab4-ef514f856ffd as stored.
        w.bytes(&[
            0xD2, 0xD7, 0xA7, 0x34, 0x2F, 0x72, 0x46, 0x4C, 0x8A, 0xB4, 0xEF, 0x51, 0x4F, 0x85,
            0x6F, 0xFD,
        ]);
        w.u32(6657);
        w.into_vec()
    }

    #[test]
    fn unpacks_every_field_in_order() {
        let bytes = sample_header_bytes();
        assert_eq!(bytes.len(), DatHeader::SIZE);
        let h = DatHeader::unpack(&bytes).unwrap();
        assert_eq!(h.version, "v1?");
        assert_eq!(&h.transactions[..4], &[0x00, 0x50, 0x4C, 0x00]);
        assert_eq!(h.magic, DatHeader::RETAIL_MAGIC);
        assert_eq!(h.block_size, 256);
        assert_eq!(h.file_size, 0x1000);
        assert_eq!(h.file_type, DatFileType::Cell);
        assert_eq!(h.subset, 1);
        assert_eq!(h.first_free_block, 0x800);
        assert_eq!(h.last_free_block, 0xF00);
        assert_eq!(h.free_block_count, 3);
        assert_eq!(h.root_block, 0x400);
        assert_eq!(h.use_lru, -842_150_451);
        assert_eq!(h.master_map_id, 0x2500_0000);
        assert_eq!(h.engine_version, 22);
        assert_eq!(h.minor_version, 6657);
        // Golden text from an independent reader on the retail dats.
        assert_eq!(
            h.major_version_text(),
            "34a7d7d2-722f-4c46-8ab4-ef514f856ffd"
        );
    }

    #[test]
    fn truncated_header_is_an_error() {
        let bytes = sample_header_bytes();
        assert!(DatHeader::unpack(&bytes[..DatHeader::SIZE - 1]).is_err());
        assert!(DatHeader::unpack(&[]).is_err());
    }

    #[test]
    fn file_type_round_trips() {
        for raw in 0..6 {
            assert_eq!(DatFileType::from_raw(raw).raw(), raw);
        }
    }
}
