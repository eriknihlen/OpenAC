//! Lengths and positions in UTF-16 code units, as a .NET `string` counts
//! them (`Length`, indexes, `Substring`), over a Rust string.
//!
//! A character outside the Basic Multilingual Plane (an emoji) is one `char`
//! in Rust and two code units (a surrogate pair) in .NET. Profile files the
//! plugins write carry such lengths, and `IndexOf` answers in them. A .NET
//! position between the two halves of a pair has no Rust string position:
//! those give `None`.

/// `string.Length`: the length in UTF-16 code units.
pub fn len(text: &str) -> usize {
    text.chars().map(char::len_utf16).sum()
}

/// The UTF-16 index of a byte offset in `text` (the offset must fall on a
/// character boundary, as `str` offsets do): converts a Rust match position
/// to the index .NET reports.
pub fn index_of_byte(text: &str, byte_offset: usize) -> usize {
    len(&text[..byte_offset])
}

/// The byte offset of UTF-16 index `index`, or `None` when it is past the
/// end or between the two halves of a surrogate pair.
pub fn byte_offset(text: &str, index: usize) -> Option<usize> {
    let mut counted = 0usize;
    for (offset, c) in text.char_indices() {
        if counted == index {
            return Some(offset);
        }
        counted += c.len_utf16();
        if counted > index {
            return None;
        }
    }
    (counted == index).then_some(text.len())
}

/// `(text.Substring(0, index), text.Substring(index))`, or `None` as
/// [`byte_offset`].
pub fn split_at(text: &str, index: usize) -> Option<(&str, &str)> {
    byte_offset(text, index).map(|offset| text.split_at(offset))
}

/// `text.Substring(start, length)`, or `None` where .NET would throw
/// (`ArgumentOutOfRangeException`) or cut a surrogate pair.
pub fn substring(text: &str, start: usize, length: usize) -> Option<&str> {
    let from = byte_offset(text, start)?;
    let to = byte_offset(&text[from..], length)?;
    Some(&text[from..from + to])
}

/// `text[index]`: the UTF-16 code unit at `index`, or `None` past the end.
pub fn code_unit_at(text: &str, index: usize) -> Option<u16> {
    text.encode_utf16().nth(index)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn surrogate_pairs_count_as_two() {
        let text = "a\u{1F600}b";
        assert_eq!(len(text), 4);
        assert_eq!(byte_offset(text, 1), Some(1));
        assert_eq!(byte_offset(text, 2), None);
        assert_eq!(byte_offset(text, 3), Some(5));
        assert_eq!(byte_offset(text, 4), Some(6));
        assert_eq!(byte_offset(text, 5), None);
        assert_eq!(index_of_byte(text, 5), 3);
        assert_eq!(split_at(text, 3), Some(("a\u{1F600}", "b")));
        assert_eq!(substring(text, 1, 2), Some("\u{1F600}"));
        assert_eq!(substring(text, 1, 1), None);
        assert_eq!(substring(text, 4, 0), Some(""));
        assert_eq!(code_unit_at(text, 1), Some(0xD83D));
        assert_eq!(code_unit_at(text, 4), None);
    }
}
