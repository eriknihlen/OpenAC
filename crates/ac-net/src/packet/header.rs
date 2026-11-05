use super::PacketFlags;
use crate::crypto::hash32;

/// The fixed 20-byte header at the start of every datagram.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PacketHeader {
    /// Packet sequence number (0 for unsequenced control packets).
    pub sequence: u32,
    /// Which optional sections follow.
    pub flags: PacketFlags,
    /// Header hash plus (possibly keystream-sealed) payload hash.
    pub checksum: u32,
    /// The client id the server assigned at connect time.
    pub id: u16,
    /// The sender's half-second interval counter.
    pub time: u16,
    /// Body length after this header.
    pub data_size: u16,
    /// The connection iteration the server assigned.
    pub iteration: u16,
}

impl PacketHeader {
    /// Wire size of the header.
    pub const SIZE: usize = 20;

    /// The value the checksum field holds while the header hash is computed.
    pub const CHECKSUM_PLACEHOLDER: u32 = 0xBADD_70DD;

    /// The header as its 20 wire bytes.
    #[must_use]
    pub fn to_bytes(&self) -> [u8; Self::SIZE] {
        let mut out = [0; Self::SIZE];
        out[0..4].copy_from_slice(&self.sequence.to_le_bytes());
        out[4..8].copy_from_slice(&self.flags.0.to_le_bytes());
        out[8..12].copy_from_slice(&self.checksum.to_le_bytes());
        out[12..14].copy_from_slice(&self.id.to_le_bytes());
        out[14..16].copy_from_slice(&self.time.to_le_bytes());
        out[16..18].copy_from_slice(&self.data_size.to_le_bytes());
        out[18..20].copy_from_slice(&self.iteration.to_le_bytes());
        out
    }

    /// Read a header from the first 20 bytes; `None` when fewer remain.
    #[must_use]
    pub fn from_bytes(data: &[u8]) -> Option<Self> {
        let b: &[u8; Self::SIZE] = data.get(..Self::SIZE)?.try_into().ok()?;
        let u16_at = |i: usize| u16::from_le_bytes([b[i], b[i + 1]]);
        let u32_at = |i: usize| u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]);
        Some(Self {
            sequence: u32_at(0),
            flags: PacketFlags(u32_at(4)),
            checksum: u32_at(8),
            id: u16_at(12),
            time: u16_at(14),
            data_size: u16_at(16),
            iteration: u16_at(18),
        })
    }

    /// The header's share of the checksum: [`hash32`] of the header with
    /// the checksum field replaced by [`Self::CHECKSUM_PLACEHOLDER`].
    #[must_use]
    pub fn hash(&self) -> u32 {
        let mut copy = *self;
        copy.checksum = Self::CHECKSUM_PLACEHOLDER;
        hash32(&copy.to_bytes())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests `Packets/PacketHeaderTests.cs`.
    #[test]
    fn round_trip_and_little_endian_layout() {
        let header = PacketHeader {
            sequence: 0x0403_0201,
            flags: PacketFlags(0x0807_0605),
            checksum: 0x0C0B_0A09,
            id: 0x0E0D,
            time: 0x100F,
            data_size: 0x1211,
            iteration: 0x1413,
        };
        let bytes = header.to_bytes();
        assert_eq!(bytes.to_vec(), (1..=20).collect::<Vec<u8>>());
        assert_eq!(PacketHeader::from_bytes(&bytes), Some(header));
        assert_eq!(PacketHeader::from_bytes(&bytes[..19]), None);
    }

    #[test]
    fn hash_ignores_the_checksum_field() {
        let a = PacketHeader {
            sequence: 42,
            checksum: 0x1111_1111,
            ..PacketHeader::default()
        };
        let b = PacketHeader {
            checksum: 0xDEAD_BEEF,
            ..a
        };
        assert_eq!(a.hash(), b.hash());
        assert_eq!(a.checksum, 0x1111_1111);
    }
}
