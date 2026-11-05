//! Invariant casing and white space as .NET 10 has them.
//!
//! `ToUpperInvariant`/`ToLowerInvariant` map one character to one character
//! (simple case mapping, never `ß` → `SS`), with .NET's own exceptions: the
//! dotless `ı` and dotted `İ` keep their case (as Windows' invariant culture
//! always did), and .NET's Unicode tables are older than Rust's, so letters
//! Unicode added since have no case in .NET. `StringComparison.OrdinalIgnoreCase`
//! compares the upper case of each character, except that `ſ` (long s) stays
//! itself. All of it is checked against every code point .NET maps
//! (`tests/data/casing.tsv`).

/// Characters Rust upper-cases and .NET does not, as inclusive ranges,
/// sorted: the dotless `ı`, and letters newer than .NET's tables (Unicode 16
/// and 17: Latin additions, Cyrillic `ᲊ`, Garay, Beria Erfe). Rust's tables
/// follow the Rust release; the golden test lists any new difference.
const NO_UPPER: &[(char, char)] = &[
    ('\u{131}', '\u{131}'),
    ('\u{19B}', '\u{19B}'),
    ('\u{264}', '\u{264}'),
    ('\u{1C8A}', '\u{1C8A}'),
    ('\u{A7CD}', '\u{A7CD}'),
    ('\u{A7CF}', '\u{A7CF}'),
    ('\u{A7D3}', '\u{A7D3}'),
    ('\u{A7D5}', '\u{A7D5}'),
    ('\u{A7DB}', '\u{A7DB}'),
    ('\u{10D70}', '\u{10D85}'),
    ('\u{16EBB}', '\u{16ED3}'),
];

/// As [`NO_UPPER`], for the lower case.
const NO_LOWER: &[(char, char)] = &[
    ('\u{1C89}', '\u{1C89}'),
    ('\u{A7CB}', '\u{A7CC}'),
    ('\u{A7CE}', '\u{A7CE}'),
    ('\u{A7D2}', '\u{A7D2}'),
    ('\u{A7D4}', '\u{A7D4}'),
    ('\u{A7DA}', '\u{A7DA}'),
    ('\u{A7DC}', '\u{A7DC}'),
    ('\u{10D50}', '\u{10D65}'),
    ('\u{16EA0}', '\u{16EB8}'),
];

/// Characters whose full upper case is several characters in Rust (Greek
/// with iota subscript: `ᾳ` → `ΑΙ`) but which .NET maps one to one (the
/// Unicode simple mapping): `(character, .NET upper case)`, sorted.
const SIMPLE_UPPER: &[(char, char)] = &[
    ('\u{1F80}', '\u{1F88}'),
    ('\u{1F81}', '\u{1F89}'),
    ('\u{1F82}', '\u{1F8A}'),
    ('\u{1F83}', '\u{1F8B}'),
    ('\u{1F84}', '\u{1F8C}'),
    ('\u{1F85}', '\u{1F8D}'),
    ('\u{1F86}', '\u{1F8E}'),
    ('\u{1F87}', '\u{1F8F}'),
    ('\u{1F90}', '\u{1F98}'),
    ('\u{1F91}', '\u{1F99}'),
    ('\u{1F92}', '\u{1F9A}'),
    ('\u{1F93}', '\u{1F9B}'),
    ('\u{1F94}', '\u{1F9C}'),
    ('\u{1F95}', '\u{1F9D}'),
    ('\u{1F96}', '\u{1F9E}'),
    ('\u{1F97}', '\u{1F9F}'),
    ('\u{1FA0}', '\u{1FA8}'),
    ('\u{1FA1}', '\u{1FA9}'),
    ('\u{1FA2}', '\u{1FAA}'),
    ('\u{1FA3}', '\u{1FAB}'),
    ('\u{1FA4}', '\u{1FAC}'),
    ('\u{1FA5}', '\u{1FAD}'),
    ('\u{1FA6}', '\u{1FAE}'),
    ('\u{1FA7}', '\u{1FAF}'),
    ('\u{1FB3}', '\u{1FBC}'),
    ('\u{1FC3}', '\u{1FCC}'),
    ('\u{1FF3}', '\u{1FFC}'),
];

/// The long `ſ`, which `ToUpperInvariant` maps to `S` but
/// `OrdinalIgnoreCase` leaves as it is.
const LONG_S: char = '\u{17F}';

fn in_ranges(ranges: &[(char, char)], c: char) -> bool {
    ranges
        .iter()
        .any(|&(first, last)| (first..=last).contains(&c))
}

fn lookup(table: &[(char, char)], c: char) -> Option<char> {
    table
        .binary_search_by_key(&c, |&(from, _)| from)
        .ok()
        .map(|i| table[i].1)
}

/// Rust's case mapping when it is one character, else `c`.
fn single(mut mapped: impl Iterator<Item = char>, c: char) -> char {
    match (mapped.next(), mapped.next()) {
        (Some(one), None) => one,
        _ => c,
    }
}

/// `char.ToUpperInvariant` (and, for a character outside the BMP, what
/// `string.ToUpperInvariant` does to its surrogate pair).
pub fn to_upper_char(c: char) -> char {
    // ASCII has no exception in either table: its upper case is the ASCII
    // one (and the tables cost a plugin's interpreter hundreds of
    // instructions a character).
    if c.is_ascii() {
        return c.to_ascii_uppercase();
    }
    to_upper_char_by_tables(c)
}

/// [`to_upper_char`] for any character, through the tables.
fn to_upper_char_by_tables(c: char) -> char {
    if in_ranges(NO_UPPER, c) {
        return c;
    }
    lookup(SIMPLE_UPPER, c).unwrap_or_else(|| single(c.to_uppercase(), c))
}

/// `char.ToLowerInvariant`, as [`to_upper_char`]. (Rust's only lower case
/// longer than one character, `İ` → `i̇`, stays `İ` as in .NET.)
pub fn to_lower_char(c: char) -> char {
    if c.is_ascii() {
        return c.to_ascii_lowercase();
    }
    to_lower_char_by_tables(c)
}

/// [`to_lower_char`] for any character, through the tables.
fn to_lower_char_by_tables(c: char) -> char {
    if in_ranges(NO_LOWER, c) {
        return c;
    }
    single(c.to_lowercase(), c)
}

/// `string.ToUpperInvariant`.
pub fn to_upper_invariant(text: &str) -> String {
    if text.is_ascii() {
        return text.to_ascii_uppercase();
    }
    text.chars().map(to_upper_char).collect()
}

/// `string.ToLowerInvariant`.
pub fn to_lower_invariant(text: &str) -> String {
    if text.is_ascii() {
        return text.to_ascii_lowercase();
    }
    text.chars().map(to_lower_char).collect()
}

/// The character `StringComparison.OrdinalIgnoreCase` compares in place of
/// `c` (.NET's `OrdinalCasing.ToUpper`).
pub fn ordinal_ignore_case_key(c: char) -> char {
    if c == LONG_S { c } else { to_upper_char(c) }
}

/// `char.IsWhiteSpace`: the Unicode `White_Space` characters, which are the
/// ones .NET lists (space separators, U+0009–U+000D, U+0085, U+2028, U+2029).
pub fn is_white_space(c: char) -> bool {
    c.is_whitespace()
}

/// `string.IsNullOrWhiteSpace` for a present string: empty or only white
/// space. (`string.Trim()` removes the same characters as `str::trim`.)
pub fn is_white_space_only(text: &str) -> bool {
    text.chars().all(is_white_space)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tables_are_sorted() {
        for table in [NO_UPPER, NO_LOWER, SIMPLE_UPPER] {
            assert!(table.windows(2).all(|w| w[0].0 < w[1].0));
        }
    }

    #[test]
    fn dotless_and_dotted_i_and_long_s() {
        // .NET 10 (tests/data/casing.tsv).
        assert_eq!(to_upper_char('\u{131}'), '\u{131}');
        assert_eq!(to_lower_char('\u{130}'), '\u{130}');
        assert_eq!(to_upper_char('\u{17F}'), 'S');
        assert_eq!(ordinal_ignore_case_key('\u{17F}'), '\u{17F}');
        assert_eq!(to_upper_invariant("straße"), "STRAßE");
        assert_eq!(to_upper_char('\u{1FB3}'), '\u{1FBC}');
    }

    // The ASCII answers are the tables' answers, for every ASCII character
    // and for whole texts.
    #[test]
    fn the_ascii_fast_paths_answer_as_the_tables() {
        let all: String = (0u8..128).map(char::from).collect();
        for c in all.chars() {
            assert_eq!(to_upper_char(c), to_upper_char_by_tables(c), "{c:?}");
            assert_eq!(to_lower_char(c), to_lower_char_by_tables(c), "{c:?}");
        }
        let by_tables =
            |text: &str, f: fn(char) -> char| -> String { text.chars().map(f).collect() };
        for text in [
            all.as_str(),
            "",
            "Incantation of Frost Bolt VII",
            "[Z]_^`{@",
        ] {
            assert_eq!(
                to_upper_invariant(text),
                by_tables(text, to_upper_char_by_tables)
            );
            assert_eq!(
                to_lower_invariant(text),
                by_tables(text, to_lower_char_by_tables)
            );
        }
    }
}
