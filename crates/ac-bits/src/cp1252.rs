//! Windows-1252 text, the code page of every game string.
//!
//! Bytes below 0x80 and from 0xA0 up are the same code points as Latin-1.
//! The 0x80..=0x9F block holds typographic characters; its five undefined
//! slots (0x81, 0x8D, 0x8F, 0x90, 0x9D) decode to the matching C1 control
//! code point, as Windows does, so decoding never fails and round-trips.

/// Code points for bytes 0x80..=0x9F.
const HIGH_BLOCK: [char; 32] = [
    '\u{20AC}', '\u{0081}', '\u{201A}', '\u{0192}', '\u{201E}', '\u{2026}', '\u{2020}', '\u{2021}',
    '\u{02C6}', '\u{2030}', '\u{0160}', '\u{2039}', '\u{0152}', '\u{008D}', '\u{017D}', '\u{008F}',
    '\u{0090}', '\u{2018}', '\u{2019}', '\u{201C}', '\u{201D}', '\u{2022}', '\u{2013}', '\u{2014}',
    '\u{02DC}', '\u{2122}', '\u{0161}', '\u{203A}', '\u{0153}', '\u{009D}', '\u{017E}', '\u{0178}',
];

/// Byte written for a character that has no Windows-1252 encoding.
pub const REPLACEMENT: u8 = b'?';

/// Decode one byte.
#[must_use]
pub fn decode_byte(byte: u8) -> char {
    match byte {
        0x80..=0x9F => HIGH_BLOCK[usize::from(byte - 0x80)],
        _ => char::from(byte),
    }
}

/// Decode a byte string.
#[must_use]
pub fn decode(bytes: &[u8]) -> String {
    bytes.iter().copied().map(decode_byte).collect()
}

/// Encode one character, or `None` if Windows-1252 cannot represent it.
#[must_use]
pub fn encode_char(c: char) -> Option<u8> {
    let code = u32::from(c);
    if code < 0x80 || (0xA0..=0xFF).contains(&code) {
        return u8::try_from(code).ok();
    }
    HIGH_BLOCK
        .iter()
        .position(|&h| h == c)
        .and_then(|i| u8::try_from(0x80 + i).ok())
}

/// Encode a string; characters with no encoding become [`REPLACEMENT`].
#[must_use]
pub fn encode(text: &str) -> Vec<u8> {
    text.chars()
        .map(|c| encode_char(c).unwrap_or(REPLACEMENT))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_byte_round_trips() {
        for byte in 0..=u8::MAX {
            assert_eq!(
                encode_char(decode_byte(byte)),
                Some(byte),
                "byte {byte:#04x}"
            );
        }
    }

    #[test]
    fn high_block_matches_the_windows_table() {
        // Spot values from the published Windows-1252 table.
        assert_eq!(decode_byte(0x80), '€');
        assert_eq!(decode_byte(0x92), '\u{2019}');
        assert_eq!(decode_byte(0x9F), 'Ÿ');
        assert_eq!(decode_byte(0xE9), 'é');
    }

    #[test]
    fn unmappable_characters_become_question_marks() {
        assert_eq!(encode("a→b"), b"a?b");
        assert_eq!(decode(&encode("Café €5")), "Café €5");
    }
}
