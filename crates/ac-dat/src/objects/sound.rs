use ac_bits::Reader;

use super::DatObject;
use super::read::{self, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// The sounds an object makes, by sound type: each type picks among
/// weighted waves. File id `0x20......`.
#[derive(Debug, Clone, PartialEq)]
pub struct SoundTable {
    /// File id.
    pub id: u32,
    /// Hash key.
    pub hash_key: i32,
    /// Default tweaks keyed by hash, in file order.
    pub hashes: Vec<(u32, SoundHashData)>,
    /// Waves per sound type, in file order.
    pub sounds: Vec<(u32, SoundData)>,
}

impl DatObject for SoundTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let hash_key = r.i32()?;
        let count = u64::from(r.u32()?);
        let hashes = keyed(r, "sound hashes", count, read::u32, SoundHashData::unpack)?;
        let count = u64::from(r.u32()?);
        let sounds = keyed(r, "sounds", count, read::u32, SoundData::unpack)?;
        Ok(Self {
            id,
            hash_key,
            hashes,
            sounds,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// Priority, probability and volume of a sound.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SoundHashData {
    /// Priority.
    pub priority: f32,
    /// Chance to play.
    pub probability: f32,
    /// Volume.
    pub volume: f32,
}

impl SoundHashData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            priority: r.f32()?,
            probability: r.f32()?,
            volume: r.f32()?,
        })
    }
}

/// The waves one sound type picks from.
#[derive(Debug, Clone, PartialEq)]
pub struct SoundData {
    /// Candidate waves.
    pub entries: Vec<SoundEntry>,
    /// A stored value of unknown use.
    pub unknown: i32,
}

impl SoundData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = u64::from(r.u32()?);
        let entries = list(r, count, SoundEntry::unpack)?;
        Ok(Self {
            entries,
            unknown: r.i32()?,
        })
    }
}

/// One candidate wave.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SoundEntry {
    /// Wave id (`0x0A......`).
    pub id: u32,
    /// Priority.
    pub priority: f32,
    /// Chance to be picked.
    pub probability: f32,
    /// Volume.
    pub volume: f32,
}

impl SoundEntry {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            id: r.u32()?,
            priority: r.f32()?,
            probability: r.f32()?,
            volume: r.f32()?,
        })
    }
}

/// A sound sample: a format header and the encoded data, kept as stored.
/// File id `0x0A......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Wave {
    /// File id.
    pub id: u32,
    /// Format header (a Windows wave format record).
    pub header: Vec<u8>,
    /// Sample data.
    pub data: Vec<u8>,
}

impl DatObject for Wave {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let header_size = r.u32()? as usize;
        let data_size = r.u32()? as usize;
        let header = r.bytes(header_size)?.to_vec();
        let data = r.bytes(data_size)?.to_vec();
        Ok(Self { id, header, data })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

impl Wave {
    /// The format fields of the header.
    pub fn format(&self) -> Result<WaveFormat> {
        WaveFormat::unpack(&self.header)
    }
}

/// The fields of a Windows wave format record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WaveFormat {
    /// Encoding (1 = PCM, 0x55 = MPEG layer 3).
    pub format_tag: u16,
    /// Channels.
    pub channels: u16,
    /// Samples per second.
    pub samples_per_sec: u32,
    /// Bytes per second.
    pub avg_bytes_per_sec: u32,
    /// Bytes per sample frame.
    pub block_align: u16,
    /// Bits per sample.
    pub bits_per_sample: u16,
    /// Encoding-specific bytes after the size field; empty for a 16-byte
    /// record.
    pub extra: Vec<u8>,
}

impl WaveFormat {
    /// Decode a wave format record: 16 fixed bytes, then optionally a u16
    /// size and that many extra bytes.
    pub fn unpack(header: &[u8]) -> Result<Self> {
        let r = &mut Reader::new(header);
        let mut format = Self {
            format_tag: r.u16()?,
            channels: r.u16()?,
            samples_per_sec: r.u32()?,
            avg_bytes_per_sec: r.u32()?,
            block_align: r.u16()?,
            bits_per_sample: r.u16()?,
            extra: Vec::new(),
        };
        if r.remaining() > 0 {
            let size = usize::from(r.u16()?);
            format.extra = r.bytes(size)?.to_vec();
        }
        Ok(format)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn table() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x2000_0001);
        w.i32(0);
        w.u32(1);
        w.u32(0);
        for v in [0.0f32, 1.0, 1.0] {
            w.f32(v);
        }
        w.u32(2);
        for key in [88u32, 154] {
            w.u32(key);
            w.u32(2);
            for wave in [0x0A00_0262u32, 0x0A00_0263] {
                w.u32(wave);
                for v in [0.7f32, 0.5, 1.0] {
                    w.f32(v);
                }
            }
            w.i32(-1);
        }
        w.into_vec()
    }

    #[test]
    fn unpacks_a_sound_table() {
        let bytes = table();
        let r = &mut Reader::new(&bytes);
        let t = SoundTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.hashes[0].1.probability, 1.0);
        assert_eq!(t.sounds.len(), 2);
        let (key, data) = &t.sounds[1];
        assert_eq!(*key, 154);
        assert_eq!(data.entries[1].id, 0x0A00_0263);
        assert_eq!(data.entries[1].priority, 0.7);
        assert_eq!(data.unknown, -1);
        for len in 0..bytes.len() {
            assert!(SoundTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn unpacks_a_wave_and_its_format() {
        let mut w = Writer::new();
        w.u32(0x0A00_0001);
        w.u32(18);
        w.u32(3);
        w.u16(1);
        w.u16(1);
        w.u32(22050);
        w.u32(44100);
        w.u16(2);
        w.u16(16);
        w.u16(0);
        w.bytes(&[1, 2, 3]);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let wave = Wave::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(wave.data, vec![1, 2, 3]);
        let f = wave.format().unwrap();
        assert_eq!(
            (
                f.format_tag,
                f.channels,
                f.samples_per_sec,
                f.bits_per_sample
            ),
            (1, 1, 22050, 16)
        );
        assert!(f.extra.is_empty());
        for len in 0..bytes.len() {
            assert!(Wave::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
        assert!(WaveFormat::unpack(&wave.header[..15]).is_err());
        // A size field that claims more than is stored.
        let mut header = wave.header.clone();
        header[16] = 2;
        assert!(WaveFormat::unpack(&header).is_err());
    }
}
