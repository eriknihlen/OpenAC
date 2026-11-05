use crate::{BitsError, Result, cp1252};

/// A cursor over a borrowed byte slice that reads little-endian values.
///
/// Positions are relative to the start of the slice; [`Reader::align`]
/// pads relative to that start too, so a reader should begin where the
/// format's alignment is measured from (a message body, a dat object).
#[derive(Debug, Clone)]
pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

macro_rules! read_le {
    ($($name:ident -> $ty:ty),* $(,)?) => {
        $(
            #[doc = concat!("Read a little-endian `", stringify!($ty), "`.")]
            pub fn $name(&mut self) -> Result<$ty> {
                Ok(<$ty>::from_le_bytes(self.array()?))
            }
        )*
    };
}

impl<'a> Reader<'a> {
    /// Start reading at the beginning of `data`.
    #[must_use]
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    /// Current position from the start of the slice.
    #[must_use]
    pub fn position(&self) -> usize {
        self.pos
    }

    /// Total length of the slice.
    #[must_use]
    pub fn len(&self) -> usize {
        self.data.len()
    }

    /// True when the slice is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }

    /// Bytes left to read.
    #[must_use]
    pub fn remaining(&self) -> usize {
        self.data.len() - self.pos
    }

    /// The unread bytes, without consuming them.
    #[must_use]
    pub fn peek_rest(&self) -> &'a [u8] {
        &self.data[self.pos..]
    }

    /// Move to an absolute position (at most the slice length).
    pub fn seek(&mut self, pos: usize) -> Result<()> {
        if pos > self.data.len() {
            return Err(BitsError::UnexpectedEnd {
                offset: pos,
                needed: 0,
                available: 0,
            });
        }
        self.pos = pos;
        Ok(())
    }

    /// Skip `count` bytes.
    pub fn skip(&mut self, count: usize) -> Result<()> {
        self.bytes(count).map(|_| ())
    }

    /// Read `count` raw bytes.
    pub fn bytes(&mut self, count: usize) -> Result<&'a [u8]> {
        let available = self.remaining();
        if count > available {
            return Err(BitsError::UnexpectedEnd {
                offset: self.pos,
                needed: count,
                available,
            });
        }
        let out = &self.data[self.pos..self.pos + count];
        self.pos += count;
        Ok(out)
    }

    /// Read a fixed-size byte array.
    pub fn array<const N: usize>(&mut self) -> Result<[u8; N]> {
        let mut out = [0; N];
        out.copy_from_slice(self.bytes(N)?);
        Ok(out)
    }

    read_le! {
        u8 -> u8, i8 -> i8,
        u16 -> u16, i16 -> i16,
        u32 -> u32, i32 -> i32,
        u64 -> u64, i64 -> i64,
        f32 -> f32, f64 -> f64,
    }

    /// Skip padding so the position is a multiple of `alignment`.
    pub fn align(&mut self, alignment: usize) -> Result<()> {
        let rem = self.pos % alignment;
        if rem == 0 {
            Ok(())
        } else {
            self.skip(alignment - rem)
        }
    }

    /// Wire "packed dword": one u16 if the top bit is clear, otherwise that
    /// u16 holds the high 15 bits and a second u16 the low 16 bits.
    pub fn packed_dword(&mut self) -> Result<u32> {
        let first = self.u16()?;
        if first & 0x8000 == 0 {
            return Ok(u32::from(first));
        }
        let low = self.u16()?;
        Ok((u32::from(first & 0x7FFF) << 16) | u32::from(low))
    }

    /// Packed dword written with a known type prefix stripped (a dat id
    /// such as a palette or texture). Zero stays zero ("no value").
    pub fn packed_dword_of_known_type(&mut self, known_type: u32) -> Result<u32> {
        let packed = self.packed_dword()?;
        Ok(if packed == 0 { 0 } else { packed | known_type })
    }

    /// Dat id stored relative to its type's base id: one u16 when the top
    /// bit is clear, otherwise the low 14 bits of that u16 are bits 16..30
    /// and a second u16 holds the low 16 bits. The result is added to
    /// `known_type`, so a stored zero yields `known_type` itself (unlike
    /// [`Reader::packed_dword_of_known_type`], which keeps zero).
    pub fn data_id_of_known_type(&mut self, known_type: u32) -> Result<u32> {
        let first = self.u16()?;
        if first & 0x8000 == 0 {
            return Ok(known_type.wrapping_add(u32::from(first)));
        }
        let low = self.u16()?;
        Ok(known_type.wrapping_add((u32::from(first & 0x3FFF) << 16) | u32::from(low)))
    }

    /// Dat "compressed u32": one byte below 0x80; two bytes (15 bits) when
    /// the first byte is `10xxxxxx`; four bytes (30 bits) when it is
    /// `11xxxxxx`, the last two being the low 16 bits.
    pub fn compressed_u32(&mut self) -> Result<u32> {
        let b0 = self.u8()?;
        if b0 & 0x80 == 0 {
            return Ok(u32::from(b0));
        }
        let b1 = self.u8()?;
        if b0 & 0x40 == 0 {
            return Ok((u32::from(b0 & 0x7F) << 8) | u32::from(b1));
        }
        let low = self.u16()?;
        Ok((((u32::from(b0 & 0x3F) << 8) | u32::from(b1)) << 16) | u32::from(low))
    }

    /// Wire string: u16 length, the bytes, then zero padding so the whole
    /// record (length prefix included) is a multiple of 4 bytes.
    pub fn string16l_bytes(&mut self) -> Result<&'a [u8]> {
        let len = usize::from(self.u16()?);
        let text = self.bytes(len)?;
        self.skip((4 - ((2 + len) & 3)) & 3)?;
        Ok(text)
    }

    /// [`Reader::string16l_bytes`] decoded from Windows-1252.
    pub fn string16l(&mut self) -> Result<String> {
        self.string16l_bytes().map(cp1252::decode)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_little_endian_integers_and_floats() {
        let data = [
            0x01, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0x00, 0x00, 0x80, 0x3F,
        ];
        let mut r = Reader::new(&data);
        assert_eq!(r.u8(), Ok(1));
        assert_eq!(r.u16(), Ok(0x1234));
        assert_eq!(r.u32(), Ok(0x1234_5678));
        assert_eq!(r.f32(), Ok(1.0));
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn short_input_is_an_error_not_a_panic() {
        let mut r = Reader::new(&[1, 2, 3]);
        assert_eq!(r.u8(), Ok(1));
        assert_eq!(
            r.u32(),
            Err(BitsError::UnexpectedEnd {
                offset: 1,
                needed: 4,
                available: 2
            })
        );
        // A failed read does not move the cursor.
        assert_eq!(r.position(), 1);
    }

    #[test]
    fn packed_dword_short_and_long_forms() {
        // Golden values worked from the packed-dword format:
        // high half first with the marker bit, then the low half.
        assert_eq!(Reader::new(&[0x34, 0x12]).packed_dword(), Ok(0x1234));
        assert_eq!(
            Reader::new(&[0x01, 0x80, 0x78, 0x56]).packed_dword(),
            Ok(0x0001_5678)
        );
        assert_eq!(
            Reader::new(&[0xFF, 0xFF, 0xFF, 0xFF]).packed_dword(),
            Ok(0x7FFF_FFFF)
        );
    }

    #[test]
    fn packed_dword_of_known_type_keeps_zero() {
        let mut r = Reader::new(&[0x00, 0x00, 0x34, 0x12]);
        assert_eq!(r.packed_dword_of_known_type(0x0400_0000), Ok(0));
        assert_eq!(r.packed_dword_of_known_type(0x0400_0000), Ok(0x0400_1234));
    }

    #[test]
    fn data_id_of_known_type_short_long_and_zero() {
        // Golden values worked from the dat format's known-type data id:
        // the long form keeps only 14 bits of the first half, and the base
        // is added (zero becomes the base id).
        const GFX_OBJ: u32 = 0x0100_0000;
        assert_eq!(
            Reader::new(&[0x34, 0x12]).data_id_of_known_type(GFX_OBJ),
            Ok(0x0100_1234)
        );
        assert_eq!(
            Reader::new(&[0x01, 0x80, 0x78, 0x56]).data_id_of_known_type(GFX_OBJ),
            Ok(0x0101_5678)
        );
        assert_eq!(
            Reader::new(&[0xFF, 0xFF, 0xFF, 0xFF]).data_id_of_known_type(GFX_OBJ),
            Ok(0x40FF_FFFF)
        );
        assert_eq!(
            Reader::new(&[0x00, 0x00]).data_id_of_known_type(GFX_OBJ),
            Ok(GFX_OBJ)
        );
        assert!(
            Reader::new(&[0x01, 0x80, 0x78])
                .data_id_of_known_type(GFX_OBJ)
                .is_err()
        );
    }

    #[test]
    fn compressed_u32_one_two_and_four_byte_forms() {
        // Golden values of the dat format's compressed unsigned integer.
        assert_eq!(Reader::new(&[0x7F]).compressed_u32(), Ok(0x7F));
        assert_eq!(Reader::new(&[0x81, 0x02]).compressed_u32(), Ok(0x0102));
        assert_eq!(
            Reader::new(&[0xC1, 0x02, 0x04, 0x03]).compressed_u32(),
            Ok(0x0102_0304)
        );
    }

    #[test]
    fn string16l_pads_the_record_to_four_bytes() {
        // "ab": 2 length + 2 text = 4, no padding; "abc": 5 -> 3 padding.
        let data = [
            2, 0, b'a', b'b', 3, 0, b'a', b'b', b'c', 0, 0, 0, 0, 0, 0, 0, 0xEE,
        ];
        let mut r = Reader::new(&data);
        assert_eq!(r.string16l().as_deref(), Ok("ab"));
        assert_eq!(r.string16l().as_deref(), Ok("abc"));
        assert_eq!(r.position(), 12);
        // An empty string still pads its 2-byte prefix to 4.
        assert_eq!(r.string16l().as_deref(), Ok(""));
        assert_eq!(r.position(), 16);
    }

    #[test]
    fn string16l_decodes_windows_1252() {
        let mut r = Reader::new(&[1, 0, 0xE9, 0]);
        assert_eq!(r.string16l().as_deref(), Ok("é"));
    }

    #[test]
    fn align_is_relative_to_the_slice_start() {
        let mut r = Reader::new(&[0; 8]);
        r.skip(1).unwrap();
        r.align(4).unwrap();
        assert_eq!(r.position(), 4);
        r.align(4).unwrap();
        assert_eq!(r.position(), 4);
    }
}
