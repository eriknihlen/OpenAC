//! `StringComparison.Ordinal` and `OrdinalIgnoreCase`, as .NET 10 has them.
//!
//! Ordinal comparison orders by UTF-16 code unit, which is not code point
//! order: a character outside the BMP (two surrogates, `D800`–`DFFF`) sorts
//! before `U+E000`–`U+FFFF`. `OrdinalIgnoreCase` compares the
//! [`ordinal_ignore_case_key`] of every character, in code point order (as
//! .NET 10 does; checked against it). Positions are UTF-16
//! indexes, as .NET returns them (see [`super::utf16`]); a search that
//! finds nothing is `None` where .NET returns -1.
//!
//! Texts that are all ASCII (nearly every spell, monster and item name) are
//! answered directly from their bytes: the same answers (the tests compare
//! them with the general rule), without folding each text into a new
//! UTF-16 buffer through the casing tables, which costs a plugin's
//! interpreter hundreds of instructions a character.

use std::cmp::Ordering;

use super::casing::ordinal_ignore_case_key;
use super::utf16;

fn both_ascii(a: &str, b: &str) -> bool {
    a.is_ascii() && b.is_ascii()
}

/// Whether every character of `text` is below `U+E000`, where UTF-16 code
/// unit order, code point order and UTF-8 byte order agree (the lead byte
/// of `U+E000` and above is `0xEE` or more).
fn below_e000(text: &str) -> bool {
    text.is_ascii() || text.bytes().all(|byte| byte < 0xEE)
}

/// `string.CompareOrdinal(a, b)` and `string.Compare(a, b,
/// StringComparison.Ordinal)` (the sign).
pub fn compare(a: &str, b: &str) -> Ordering {
    if below_e000(a) && below_e000(b) {
        return a.as_bytes().cmp(b.as_bytes());
    }
    compare_utf16(a, b)
}

fn compare_utf16(a: &str, b: &str) -> Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

/// The UTF-16 code units `OrdinalIgnoreCase` compares.
fn folded(text: &str) -> Vec<u16> {
    let mut out = Vec::with_capacity(text.len());
    let mut buf = [0u16; 2];
    for c in text.chars() {
        out.extend_from_slice(ordinal_ignore_case_key(c).encode_utf16(&mut buf));
    }
    out
}

/// `string.Compare(a, b, StringComparison.OrdinalIgnoreCase)` (the sign).
/// Unlike [`compare`], this orders by code point: .NET 10 compares the upper
/// case of whole characters, so `U+FF01` sorts before an emoji here.
pub fn compare_ignore_case(a: &str, b: &str) -> Ordering {
    if both_ascii(a, b) {
        return a
            .bytes()
            .map(|c| c.to_ascii_uppercase())
            .cmp(b.bytes().map(|c| c.to_ascii_uppercase()));
    }
    compare_ignore_case_by_keys(a, b)
}

fn compare_ignore_case_by_keys(a: &str, b: &str) -> Ordering {
    a.chars()
        .map(ordinal_ignore_case_key)
        .cmp(b.chars().map(ordinal_ignore_case_key))
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
pub fn equals_ignore_case(a: &str, b: &str) -> bool {
    if both_ascii(a, b) {
        return a.eq_ignore_ascii_case(b);
    }
    equals_ignore_case_by_keys(a, b)
}

fn equals_ignore_case_by_keys(a: &str, b: &str) -> bool {
    a.chars()
        .map(ordinal_ignore_case_key)
        .eq(b.chars().map(ordinal_ignore_case_key))
}

/// `haystack.IndexOf(needle, StringComparison.Ordinal)`; an empty needle is
/// found at 0.
pub fn index_of(haystack: &str, needle: &str) -> Option<usize> {
    haystack
        .find(needle)
        .map(|at| utf16::index_of_byte(haystack, at))
}

/// `haystack.LastIndexOf(needle, StringComparison.Ordinal)`; an empty
/// needle is found at the end (`Length`).
pub fn last_index_of(haystack: &str, needle: &str) -> Option<usize> {
    haystack
        .rfind(needle)
        .map(|at| utf16::index_of_byte(haystack, at))
}

fn find_units(haystack: &[u16], needle: &[u16], last: bool) -> Option<usize> {
    if needle.len() > haystack.len() {
        return None;
    }
    let last_start = haystack.len() - needle.len();
    let matches = |&start: &usize| haystack[start..start + needle.len()] == *needle;
    if last {
        (0..=last_start).rev().find(matches)
    } else {
        (0..=last_start).find(matches)
    }
}

/// Where `needle` first (or last) starts in `haystack` ignoring ASCII
/// case, both ASCII: a byte index, which is the UTF-16 index.
fn find_ascii(haystack: &[u8], needle: &[u8], last: bool) -> Option<usize> {
    if needle.len() > haystack.len() {
        return None;
    }
    // The first byte rules out nearly every start before the whole needle
    // is compared.
    let first = needle.first().map(u8::to_ascii_lowercase);
    let matches = |start: &usize| {
        first.is_none_or(|first| haystack[*start].to_ascii_lowercase() == first)
            && haystack[*start..*start + needle.len()].eq_ignore_ascii_case(needle)
    };
    let starts = 0..=haystack.len() - needle.len();
    if last {
        starts.rev().find(matches)
    } else {
        starts.clone().find(matches)
    }
}

/// `haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase)`.
pub fn index_of_ignore_case(haystack: &str, needle: &str) -> Option<usize> {
    if both_ascii(haystack, needle) {
        return find_ascii(haystack.as_bytes(), needle.as_bytes(), false);
    }
    find_units(&folded(haystack), &folded(needle), false)
}

/// `haystack.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase)`.
pub fn last_index_of_ignore_case(haystack: &str, needle: &str) -> Option<usize> {
    if both_ascii(haystack, needle) {
        return find_ascii(haystack.as_bytes(), needle.as_bytes(), true);
    }
    find_units(&folded(haystack), &folded(needle), true)
}

/// `text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`.
pub fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    if both_ascii(text, prefix) {
        return text.len() >= prefix.len()
            && text.as_bytes()[..prefix.len()].eq_ignore_ascii_case(prefix.as_bytes());
    }
    folded(text).starts_with(&folded(prefix))
}

/// `text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)`.
pub fn ends_with_ignore_case(text: &str, suffix: &str) -> bool {
    if both_ascii(text, suffix) {
        return text.len() >= suffix.len()
            && text.as_bytes()[text.len() - suffix.len()..]
                .eq_ignore_ascii_case(suffix.as_bytes());
    }
    folded(text).ends_with(&folded(suffix))
}

/// `text.Contains(value, StringComparison.OrdinalIgnoreCase)`.
pub fn contains_ignore_case(text: &str, value: &str) -> bool {
    index_of_ignore_case(text, value).is_some()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn utf16_order_and_case() {
        // .NET 10 (tests/data/strings.tsv).
        assert_eq!(compare("\u{1F600}", "\u{FF01}"), Ordering::Less);
        assert_eq!(compare("Zebra", "apple"), Ordering::Less);
        assert_eq!(compare_ignore_case("apple", "Banana"), Ordering::Less);
        assert!(equals_ignore_case("Adja's Gift", "ADJA'S GIFT"));
        assert_eq!(index_of("a\u{1F600}b", "b"), Some(3));
        assert_eq!(last_index_of("hello", ""), Some(5));
        assert_eq!(index_of_ignore_case("hello", "LL"), Some(2));
    }

    /// Texts drawn from ASCII (every character, the case boundaries `@[`
    /// and `` `{ `` among them) and from characters the casing rules treat
    /// specially: `ß`, the long `ſ`, the dotless `ı` and dotted `İ`, the
    /// Kelvin sign, a Greek iota subscript, `U+E000`–`U+FFFF` and a
    /// character outside the BMP (UTF-16 orders them the other way round).
    fn texts() -> Vec<String> {
        const SPECIAL: [char; 12] = [
            'ß',
            'ſ',
            'ı',
            'İ',
            '\u{212A}',
            'ᾳ',
            'é',
            'É',
            '\u{E000}',
            '\u{FF01}',
            '\u{1F600}',
            '\u{10400}',
        ];
        // A fixed xorshift, so a failure names a text that comes back.
        let mut state: u64 = 0x9E37_79B9_7F4A_7C15;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut texts: Vec<String> = ["", "a", "A", "Incantation of Frost Bolt VII", "[Z]_^`{@"]
            .iter()
            .map(|t| (*t).to_owned())
            .collect();
        for index in 0..400 {
            let len = usize::try_from(next() % 6).unwrap_or(0);
            let mixed = index % 3 == 0;
            let text: String = (0..len)
                .map(|_| {
                    let roll = next();
                    if mixed && roll % 4 == 0 {
                        SPECIAL[usize::try_from(roll / 4 % 12).unwrap_or(0)]
                    } else {
                        // Mostly letters, so matches happen; sometimes any
                        // ASCII character.
                        let pick = u8::try_from(roll / 4 % 128).unwrap_or(0);
                        if roll % 3 == 0 {
                            char::from(pick)
                        } else {
                            char::from(b"aAbBzZ"[usize::from(pick % 6)])
                        }
                    }
                })
                .collect();
            texts.push(text);
        }
        texts
    }

    // Property-style: every fast path answers what the general rule answers,
    // over ASCII texts and texts mixing in the special characters.
    #[test]
    fn the_fast_paths_answer_as_the_general_rule() {
        let texts = texts();
        for a in &texts {
            for b in texts.iter().step_by(7) {
                let pair = format!("{a:?} {b:?}");
                assert_eq!(compare(a, b), compare_utf16(a, b), "{pair}");
                assert_eq!(
                    compare_ignore_case(a, b),
                    compare_ignore_case_by_keys(a, b),
                    "{pair}"
                );
                assert_eq!(
                    equals_ignore_case(a, b),
                    equals_ignore_case_by_keys(a, b),
                    "{pair}"
                );
                let (fa, fb) = (folded(a), folded(b));
                assert_eq!(
                    index_of_ignore_case(a, b),
                    find_units(&fa, &fb, false),
                    "{pair}"
                );
                assert_eq!(
                    last_index_of_ignore_case(a, b),
                    find_units(&fa, &fb, true),
                    "{pair}"
                );
                assert_eq!(starts_with_ignore_case(a, b), fa.starts_with(&fb), "{pair}");
                assert_eq!(ends_with_ignore_case(a, b), fa.ends_with(&fb), "{pair}");
            }
        }
    }

    // The byte order is UTF-16's only below U+E000: above it a character
    // outside the BMP sorts first.
    #[test]
    fn byte_order_stands_in_for_utf16_order_only_below_e000() {
        assert_eq!(compare("\u{D7FF}", "\u{10000}"), Ordering::Less);
        assert_eq!(compare("\u{E000}", "\u{10000}"), Ordering::Greater);
        assert_eq!(compare("a\u{FFFF}", "a\u{1F600}"), Ordering::Greater);
        assert_eq!(compare("é", "z"), Ordering::Greater);
    }
}
