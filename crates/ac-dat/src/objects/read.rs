//! Reading helpers shared by the object decoders: counted lists, keyed
//! tables and the dat string encodings.

use std::collections::HashSet;
use std::hash::Hash;

use ac_bits::{Reader, cp1252};

use crate::error::DecodeError;

type Result<T> = core::result::Result<T, DecodeError>;

/// Read `count` items in file order.
pub(crate) fn list<T>(
    r: &mut Reader<'_>,
    count: u64,
    mut item: impl FnMut(&mut Reader<'_>) -> Result<T>,
) -> Result<Vec<T>> {
    let mut out = Vec::new();
    for _ in 0..count {
        out.push(item(r)?);
    }
    Ok(out)
}

/// Read `count` `(key, value)` pairs in file order; a repeated key is an
/// error.
pub(crate) fn keyed<K, V>(
    r: &mut Reader<'_>,
    table: &'static str,
    count: u64,
    mut key: impl FnMut(&mut Reader<'_>) -> Result<K>,
    mut value: impl FnMut(&mut Reader<'_>) -> Result<V>,
) -> Result<Vec<(K, V)>>
where
    K: Copy + Eq + Hash + Into<i64>,
{
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for _ in 0..count {
        let k = key(r)?;
        let v = value(r)?;
        if !seen.insert(k) {
            return Err(DecodeError::DuplicateKey {
                table,
                key: k.into(),
            });
        }
        out.push((k, v));
    }
    Ok(out)
}

/// A table with a u16 entry count and a u16 bucket count; returns the
/// entry count.
pub(crate) fn packable_table_count(r: &mut Reader<'_>) -> Result<u64> {
    let count = r.u16()?;
    let _buckets = r.u16()?;
    Ok(u64::from(count))
}

/// A table with a one-byte bucket size index and a compressed entry count;
/// returns the entry count.
pub(crate) fn hash_table_count(r: &mut Reader<'_>) -> Result<u64> {
    let _bucket_size_index = r.u8()?;
    Ok(u64::from(r.compressed_u32()?))
}

/// One-byte text: a u16 length (`0xFFFF` means a u32 length follows), the
/// Windows-1252 bytes, then padding to 4 bytes from the object start.
pub(crate) fn legacy_string(r: &mut Reader<'_>) -> Result<String> {
    let short = r.u16()?;
    let len = if short == 0xFFFF {
        r.u32()? as usize
    } else {
        usize::from(short)
    };
    let text = cp1252::decode(r.bytes(len)?);
    r.align(4)?;
    Ok(text)
}

/// One-byte text: a compressed length, then the Windows-1252 bytes. No
/// padding.
pub(crate) fn compressed_string(r: &mut Reader<'_>) -> Result<String> {
    let len = r.compressed_u32()? as usize;
    Ok(cp1252::decode(r.bytes(len)?))
}

/// Two-byte text: a compressed length in units, then UTF-16 units. No
/// padding.
pub(crate) fn compressed_string16(r: &mut Reader<'_>) -> Result<String> {
    let len = r.compressed_u32()?;
    let mut units = Vec::new();
    for _ in 0..len {
        units.push(r.u16()?);
    }
    String::from_utf16(&units).map_err(|_| DecodeError::BadUtf16)
}

/// Scrambled one-byte text: a u16 length, bytes with their two nibbles
/// swapped, then padding to 4 bytes from the object start.
pub(crate) fn scrambled_string(r: &mut Reader<'_>) -> Result<String> {
    let len = usize::from(r.u16()?);
    let text = r
        .bytes(len)?
        .iter()
        .map(|&b| cp1252::decode_byte(b.rotate_left(4)))
        .collect();
    r.align(4)?;
    Ok(text)
}

/// A u32, as an item reader.
pub(crate) fn u32(r: &mut Reader<'_>) -> Result<u32> {
    Ok(r.u32()?)
}

/// An i32, as an item reader.
pub(crate) fn i32(r: &mut Reader<'_>) -> Result<i32> {
    Ok(r.i32()?)
}

/// A u16, as an item reader.
pub(crate) fn u16(r: &mut Reader<'_>) -> Result<u16> {
    Ok(r.u16()?)
}

/// A 1-byte boolean (any non-zero value is true).
pub(crate) fn bool8(r: &mut Reader<'_>) -> Result<bool> {
    Ok(r.u8()? != 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn legacy_string_pads_from_the_object_start() {
        let mut w = Writer::new();
        w.u16(3);
        w.bytes(b"P.Y");
        w.align(4);
        w.u32(7);
        let mut r = Reader::new(w.as_slice());
        assert_eq!(legacy_string(&mut r).unwrap(), "P.Y");
        assert_eq!(r.position(), 8);
        assert_eq!(r.u32().unwrap(), 7);
    }

    #[test]
    fn legacy_string_long_form() {
        let mut w = Writer::new();
        w.u16(0xFFFF);
        w.u32(2);
        w.bytes(b"ab");
        let mut r = Reader::new(w.as_slice());
        assert_eq!(legacy_string(&mut r).unwrap(), "ab");
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn legacy_string_decodes_windows_1252() {
        let mut r = Reader::new(&[1, 0, 0x92, 0]);
        assert_eq!(legacy_string(&mut r).unwrap(), "\u{2019}");
    }

    #[test]
    fn compressed_strings() {
        let mut r = Reader::new(&[4, b'M', b'a', b'l', b'e']);
        assert_eq!(compressed_string(&mut r).unwrap(), "Male");
        let mut r = Reader::new(&[2, b'L', 0, b'o', 0]);
        assert_eq!(compressed_string16(&mut r).unwrap(), "Lo");
        assert_eq!(
            compressed_string16(&mut Reader::new(&[1, 0x00, 0xD8])),
            Err(DecodeError::BadUtf16)
        );
        assert!(compressed_string16(&mut Reader::new(&[2, b'L', 0, b'o'])).is_err());
    }

    #[test]
    fn scrambled_string_swaps_nibbles() {
        // "Lo" is 0x4C 0x6F, stored 0xC4 0xF6; 4 bytes in all, no padding.
        let mut r = Reader::new(&[2, 0, 0xC4, 0xF6, 0, 0]);
        assert_eq!(scrambled_string(&mut r).unwrap(), "Lo");
        assert_eq!(r.remaining(), 2);
        // "A": 3 bytes, padded to 4.
        let mut r = Reader::new(&[1, 0, 0x14, 0, 9]);
        assert_eq!(scrambled_string(&mut r).unwrap(), "A");
        assert_eq!(r.remaining(), 1);
    }

    #[test]
    fn keyed_rejects_a_repeated_key() {
        let mut w = Writer::new();
        for (k, v) in [(1u32, 10u32), (2, 20), (1, 30)] {
            w.u32(k);
            w.u32(v);
        }
        let bytes = w.into_vec();
        let pairs = keyed(&mut Reader::new(&bytes), "t", 2, u32, u32);
        assert_eq!(pairs.unwrap(), vec![(1, 10), (2, 20)]);
        assert_eq!(
            keyed(&mut Reader::new(&bytes), "t", 3, u32, u32),
            Err(DecodeError::DuplicateKey { table: "t", key: 1 })
        );
    }

    #[test]
    fn table_counts() {
        assert_eq!(
            packable_table_count(&mut Reader::new(&[5, 0, 64, 0])).unwrap(),
            5
        );
        assert_eq!(
            hash_table_count(&mut Reader::new(&[1, 0x81, 0x00])).unwrap(),
            0x100
        );
    }
}
