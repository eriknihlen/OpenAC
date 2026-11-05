use crate::{BitsError, Result, cp1252};

/// Appends little-endian values to a growable buffer.
#[derive(Debug, Clone, Default)]
pub struct Writer {
    buf: Vec<u8>,
}

macro_rules! write_le {
    ($($name:ident: $ty:ty),* $(,)?) => {
        $(
            #[doc = concat!("Write a little-endian `", stringify!($ty), "`.")]
            pub fn $name(&mut self, value: $ty) {
                self.buf.extend_from_slice(&value.to_le_bytes());
            }
        )*
    };
}

impl Writer {
    /// An empty writer.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// An empty writer with room for `capacity` bytes.
    #[must_use]
    pub fn with_capacity(capacity: usize) -> Self {
        Self {
            buf: Vec::with_capacity(capacity),
        }
    }

    /// Bytes written so far.
    #[must_use]
    pub fn len(&self) -> usize {
        self.buf.len()
    }

    /// True when nothing has been written.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.buf.is_empty()
    }

    /// The bytes written so far.
    #[must_use]
    pub fn as_slice(&self) -> &[u8] {
        &self.buf
    }

    /// The bytes written so far, mutable (for patching a header in place).
    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        &mut self.buf
    }

    /// Take the buffer.
    #[must_use]
    pub fn into_vec(self) -> Vec<u8> {
        self.buf
    }

    /// Append raw bytes.
    pub fn bytes(&mut self, bytes: &[u8]) {
        self.buf.extend_from_slice(bytes);
    }

    write_le! {
        u8: u8, i8: i8,
        u16: u16, i16: i16,
        u32: u32, i32: i32,
        u64: u64, i64: i64,
        f32: f32, f64: f64,
    }

    /// Append `count` zero bytes.
    pub fn pad(&mut self, count: usize) {
        self.buf.resize(self.buf.len() + count, 0);
    }

    /// Zero-pad until the length is a multiple of `alignment`.
    pub fn align(&mut self, alignment: usize) {
        let rem = self.buf.len() % alignment;
        if rem != 0 {
            self.pad(alignment - rem);
        }
    }

    /// Wire "packed dword" (see [`crate::Reader::packed_dword`]); values
    /// above 0x7FFF_FFFF cannot be encoded.
    pub fn packed_dword(&mut self, value: u32) -> Result<()> {
        if value <= 0x7FFF {
            // Fits in the 15-bit short form.
            self.u16(value as u16);
        } else if value <= 0x7FFF_FFFF {
            self.u16(((value >> 16) as u16) | 0x8000);
            self.u16(value as u16);
        } else {
            return Err(BitsError::ValueOutOfRange {
                encoding: "packed dword",
                value: u64::from(value),
            });
        }
        Ok(())
    }

    /// Dat "compressed u32" (see [`crate::Reader::compressed_u32`]); values
    /// above 0x3FFF_FFFF cannot be encoded.
    pub fn compressed_u32(&mut self, value: u32) -> Result<()> {
        if value <= 0x7F {
            self.u8(value as u8);
        } else if value <= 0x3FFF {
            self.u8(((value >> 8) as u8) | 0x80);
            self.u8(value as u8);
        } else if value <= 0x3FFF_FFFF {
            self.u8(((value >> 24) as u8) | 0xC0);
            self.u8((value >> 16) as u8);
            self.u16(value as u16);
        } else {
            return Err(BitsError::ValueOutOfRange {
                encoding: "compressed u32",
                value: u64::from(value),
            });
        }
        Ok(())
    }

    /// Wire string from raw bytes: u16 length, the bytes, then zero padding
    /// so the record (length prefix included) is a multiple of 4 bytes.
    pub fn string16l_bytes(&mut self, text: &[u8]) -> Result<()> {
        let len = u16::try_from(text.len()).map_err(|_| BitsError::ValueOutOfRange {
            encoding: "string16l length",
            value: text.len() as u64,
        })?;
        self.u16(len);
        self.bytes(text);
        self.pad((4 - ((2 + text.len()) & 3)) & 3);
        Ok(())
    }

    /// Wire string encoded as Windows-1252.
    pub fn string16l(&mut self, text: &str) -> Result<()> {
        self.string16l_bytes(&cp1252::encode(text))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Reader;

    #[test]
    fn integers_are_little_endian() {
        let mut w = Writer::new();
        w.u16(0x1234);
        w.u32(0xAABB_CCDD);
        w.f32(1.0);
        assert_eq!(
            w.as_slice(),
            [0x34, 0x12, 0xDD, 0xCC, 0xBB, 0xAA, 0x00, 0x00, 0x80, 0x3F]
        );
    }

    #[test]
    fn packed_dword_round_trips_at_the_boundaries() {
        for value in [0, 1, 0x7FFF, 0x8000, 0x0001_5678, 0x7FFF_FFFF] {
            let mut w = Writer::new();
            w.packed_dword(value).unwrap();
            let expected_len = if value <= 0x7FFF { 2 } else { 4 };
            assert_eq!(w.len(), expected_len, "{value:#x}");
            assert_eq!(Reader::new(w.as_slice()).packed_dword(), Ok(value));
        }
        assert!(Writer::new().packed_dword(0x8000_0000).is_err());
    }

    #[test]
    fn compressed_u32_round_trips_at_the_boundaries() {
        for value in [0, 0x7F, 0x80, 0x3FFF, 0x4000, 0x3FFF_FFFF] {
            let mut w = Writer::new();
            w.compressed_u32(value).unwrap();
            assert_eq!(Reader::new(w.as_slice()).compressed_u32(), Ok(value));
        }
        assert!(Writer::new().compressed_u32(0x4000_0000).is_err());
    }

    #[test]
    fn string16l_matches_the_padding_rule() {
        // Same layout OpenAC's PacketWriter.WriteString16L produces.
        let mut w = Writer::new();
        w.string16l("abc").unwrap();
        assert_eq!(w.as_slice(), [3, 0, b'a', b'b', b'c', 0, 0, 0]);
        let mut w = Writer::new();
        w.string16l("").unwrap();
        assert_eq!(w.as_slice(), [0, 0, 0, 0]);
    }

    #[test]
    fn align_pads_with_zeros() {
        let mut w = Writer::new();
        w.u8(7);
        w.align(4);
        assert_eq!(w.as_slice(), [7, 0, 0, 0]);
    }
}
