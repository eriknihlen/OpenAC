//! Strings as the plugins' .NET calls treat them.
//!
//! - [`utf16`]: `Length`, indexes and `Substring` in UTF-16 code units.
//! - [`ordinal`]: `StringComparison.Ordinal` and `OrdinalIgnoreCase`
//!   (`Compare`, `Equals`, `IndexOf`, `StartsWith`, ...).
//! - [`casing`]: `ToUpperInvariant`, `ToLowerInvariant`, `char.IsWhiteSpace`.
//! - [`collation`]: `StringComparer.InvariantCulture` (ICU's root order) for
//!   Latin text, punctuation and currency signs.
//!
//! Decoding, in this module: the plugins read and write text only as UTF-8:
//! `Encoding.UTF8.GetString` for frames off the socket, and the host's
//! `File.ReadAllText(path, Encoding.UTF8)` (a `StreamReader` that honours
//! byte-order marks) for profiles, side-cars and downloaded metas. They write
//! UTF-8 without a BOM, which is a Rust string's bytes. `Encoding.Default` is
//! UTF-8 on .NET Core; neither plugin uses Latin-1 or Windows-1252 (the
//! client's wire strings use `ac_bits::cp1252`).

pub mod casing;
pub mod collation;
pub mod ordinal;
pub mod utf16;

/// `Encoding.UTF8.GetString(bytes)`: invalid sequences become U+FFFD, one
/// per maximal ill-formed subpart; a byte-order mark is kept as U+FEFF.
pub fn utf8_get_string(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).into_owned()
}

/// The encoding a byte-order mark selected.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DetectedEncoding {
    /// No mark (or a UTF-8 mark): UTF-8.
    Utf8,
    /// `FF FE`: UTF-16 little-endian.
    Utf16Le,
    /// `FE FF`: UTF-16 big-endian.
    Utf16Be,
    /// `FF FE 00 00`: UTF-32 little-endian.
    Utf32Le,
    /// `00 00 FE FF`: UTF-32 big-endian.
    Utf32Be,
}

/// `StreamReader`'s byte-order-mark detection on a whole file: the encoding
/// and the length of the mark.
pub fn detect_encoding(bytes: &[u8]) -> (DetectedEncoding, usize) {
    match bytes {
        [0xEF, 0xBB, 0xBF, ..] => (DetectedEncoding::Utf8, 3),
        [0xFE, 0xFF, ..] => (DetectedEncoding::Utf16Be, 2),
        [0xFF, 0xFE, 0x00, 0x00, ..] => (DetectedEncoding::Utf32Le, 4),
        [0xFF, 0xFE, ..] => (DetectedEncoding::Utf16Le, 2),
        [0x00, 0x00, 0xFE, 0xFF, ..] => (DetectedEncoding::Utf32Be, 4),
        _ => (DetectedEncoding::Utf8, 0),
    }
}

/// `File.ReadAllText(path, Encoding.UTF8)` (and `new StreamReader(stream,
/// Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd()`): the
/// byte-order mark picks UTF-8, UTF-16 or UTF-32 and is dropped; without one
/// the bytes are UTF-8. Invalid input becomes U+FFFD.
pub fn read_all_text(bytes: &[u8]) -> String {
    let (encoding, mark) = detect_encoding(bytes);
    let body = &bytes[mark..];
    match encoding {
        DetectedEncoding::Utf8 => utf8_get_string(body),
        DetectedEncoding::Utf16Le => utf16(body, u16::from_le_bytes),
        DetectedEncoding::Utf16Be => utf16(body, u16::from_be_bytes),
        DetectedEncoding::Utf32Le => utf32(body, u32::from_le_bytes),
        DetectedEncoding::Utf32Be => utf32(body, u32::from_be_bytes),
    }
}

/// UTF-16 with replacement: an unpaired surrogate or a trailing odd byte
/// becomes U+FFFD.
fn utf16(body: &[u8], unit: fn([u8; 2]) -> u16) -> String {
    let units = body.chunks_exact(2).map(|c| unit([c[0], c[1]]));
    let mut out: String = char::decode_utf16(units)
        .map(|r| r.unwrap_or(char::REPLACEMENT_CHARACTER))
        .collect();
    if !body.len().is_multiple_of(2) {
        out.push(char::REPLACEMENT_CHARACTER);
    }
    out
}

/// UTF-32 with replacement: a value that is not a scalar value, or a trailing
/// partial unit, becomes U+FFFD.
fn utf32(body: &[u8], unit: fn([u8; 4]) -> u32) -> String {
    let mut out: String = body
        .chunks_exact(4)
        .map(|c| {
            char::from_u32(unit([c[0], c[1], c[2], c[3]])).unwrap_or(char::REPLACEMENT_CHARACTER)
        })
        .collect();
    if !body.len().is_multiple_of(4) {
        out.push(char::REPLACEMENT_CHARACTER);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_order_marks() {
        assert_eq!(read_all_text(b"\xEF\xBB\xBFabc"), "abc");
        assert_eq!(utf8_get_string(b"\xEF\xBB\xBFabc"), "\u{FEFF}abc");
        assert_eq!(read_all_text(b"\xFF\xFEa\x00b\x00"), "ab");
        assert_eq!(read_all_text(b"\xFE\xFF\x00a\x00b"), "ab");
        assert_eq!(read_all_text(b"\xFF\xFE\x00\x00a\x00\x00\x00"), "a");
        assert_eq!(read_all_text(b"caf\xE9"), "caf\u{FFFD}");
    }
}
