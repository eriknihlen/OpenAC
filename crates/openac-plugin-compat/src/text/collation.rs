//! The invariant culture's string order, as .NET 10 on Windows sorts:
//! `StringComparer.InvariantCulture`, `string.Compare(a, b,
//! StringComparison.InvariantCulture)`, and the default comparison of an
//! English (en-US) machine, whose order is the same.
//!
//! .NET hands this comparison to ICU's root collation. Each character has up
//! to three levels of weights: the letter (primary), its accents
//! (secondary), its case and variant (tertiary). Two strings compare by all
//! their primary weights first, then by all secondary ones, then tertiary;
//! characters with no weight at all (controls, the soft hyphen, zero-width
//! characters) are ignored, so different strings can compare equal (.NET
//! answers 0 for them too). So `"co-op" < "coop"`, `"a" < "A" < "á"`,
//! `"Æ"` sorts as `"AE"` (just after it), and `"ß"` as `"ss"`.
//!
//! The weights are the ICU sort keys .NET prints for each covered character
//! (`collation_keys.tsv`, from `CompareInfo.GetSortKey`); the only
//! contraction among them is `l·`/`L·`, which sorts as `ŀ`/`Ŀ`. Covered:
//! U+0000–U+024F (ASCII, Latin-1 and Latin Extended-A and -B),
//! U+2000–U+206F (General Punctuation) and U+20A0–U+20CF (Currency Symbols),
//! the assigned characters. Checked against .NET on 7,700 string pairs (and,
//! when the table was made, on two million). [`try_compare`] refuses other
//! characters; [`compare`] places them after every covered character, in
//! code point order, which is not .NET's order.

use std::cmp::Ordering;
use std::sync::OnceLock;

/// `.NET`'s sort keys of the covered characters: code point, then the key
/// bytes in hex.
const KEYS: &str = include_str!("collation_keys.tsv");

/// The middle dot that forms the `l·` contraction, and what `l·`/`L·` sort as.
const MIDDLE_DOT: char = '\u{B7}';
const SMALL_L_MIDDLE_DOT: char = '\u{140}';
const CAPITAL_L_MIDDLE_DOT: char = '\u{13F}';

/// ICU sort-key bytes: the level separator and the end of the key.
const LEVEL_SEPARATOR: u8 = 0x01;
const KEY_END: u8 = 0x00;
/// The common secondary and tertiary weight, and how ICU writes a run of
/// them in a key: one byte, counting up from `COMMON` for a run before a
/// smaller weight (or the end), down from the top for a run before a larger.
const COMMON: u8 = 0x05;
const SECONDARY_RUN_MIDDLE: u8 = 0x25;
const SECONDARY_RUN_TOP: u8 = 0x45;
const TERTIARY_RUN_MIDDLE: u8 = 0x65;
const TERTIARY_RUN_TOP: u8 = 0xC5;
/// The primary lead byte [`compare`] gives an uncovered character: above
/// every covered one (whose highest is 0x60).
const UNCOVERED_LEAD: u8 = 0xFE;

/// One character's weights, each level written out in full (a run of
/// common weights as that many `COMMON` bytes).
#[derive(Debug, Clone, PartialEq, Eq)]
struct Weights {
    levels: [Vec<u8>; 3],
}

/// The covered characters and their weights, sorted by character.
fn table() -> &'static [(char, Weights)] {
    static TABLE: OnceLock<Vec<(char, Weights)>> = OnceLock::new();
    TABLE.get_or_init(|| parse_table(KEYS).expect("the embedded collation table is well formed"))
}

fn parse_table(text: &str) -> Option<Vec<(char, Weights)>> {
    let mut out = Vec::new();
    for line in text
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
    {
        let (code, key) = line.split_once('\t')?;
        let c = char::from_u32(u32::from_str_radix(code, 16).ok()?)?;
        let bytes = (0..key.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(key.get(i..i + 2)?, 16).ok())
            .collect::<Option<Vec<u8>>>()?;
        out.push((c, decode_key(&bytes)?));
    }
    out.sort_by_key(|&(c, _)| c);
    Some(out)
}

/// Undoes ICU's key layout for one character: splits the levels and writes
/// each compressed run of common weights out in full.
fn decode_key(key: &[u8]) -> Option<Weights> {
    let mut levels: [Vec<u8>; 3] = Default::default();
    let mut level = 0usize;
    for &b in key {
        match b {
            KEY_END => return Some(Weights { levels }),
            LEVEL_SEPARATOR => {
                level += 1;
                if level > 2 {
                    return None;
                }
            }
            _ => {
                let run = match level {
                    1 => run_length(b, SECONDARY_RUN_MIDDLE, SECONDARY_RUN_TOP),
                    2 => run_length(b, TERTIARY_RUN_MIDDLE, TERTIARY_RUN_TOP),
                    _ => None,
                };
                match run {
                    Some(n) => levels[level].extend(std::iter::repeat_n(COMMON, n)),
                    None => levels[level].push(b),
                }
            }
        }
    }
    None
}

/// How many common weights a run byte stands for, or `None` for a weight.
fn run_length(b: u8, middle: u8, top: u8) -> Option<usize> {
    if !(COMMON..=top).contains(&b) {
        return None;
    }
    Some(if b <= middle {
        usize::from(b - COMMON) + 1
    } else {
        usize::from(top - b) + 1
    })
}

fn weights(c: char) -> Option<&'static Weights> {
    let table = table();
    table
        .binary_search_by_key(&c, |&(from, _)| from)
        .ok()
        .map(|i| &table[i].1)
}

/// Whether [`try_compare`] knows `c`.
pub fn is_covered(c: char) -> bool {
    weights(c).is_some()
}

/// The three levels of a whole string; `None` when a character is not
/// covered and `fallback` is off.
fn levels(text: &str, fallback: bool) -> Option<[Vec<u8>; 3]> {
    let mut out: [Vec<u8>; 3] = Default::default();
    let mut chars = text.chars().peekable();
    while let Some(mut c) = chars.next() {
        if matches!(c, 'l' | 'L') && chars.peek() == Some(&MIDDLE_DOT) {
            chars.next();
            c = if c == 'l' {
                SMALL_L_MIDDLE_DOT
            } else {
                CAPITAL_L_MIDDLE_DOT
            };
        }
        match weights(c) {
            Some(w) => {
                for (level, bytes) in out.iter_mut().zip(&w.levels) {
                    level.extend_from_slice(bytes);
                }
            }
            None if fallback => {
                let code = u32::from(c).to_be_bytes();
                out[0].extend_from_slice(&[UNCOVERED_LEAD, code[1], code[2], code[3]]);
                out[1].push(COMMON);
                out[2].push(COMMON);
            }
            None => return None,
        }
    }
    Some(out)
}

/// `string.Compare(a, b, StringComparison.InvariantCulture)` (the sign),
/// or `None` when either string has a character outside the covered set.
pub fn try_compare(a: &str, b: &str) -> Option<Ordering> {
    Some(levels(a, false)?.cmp(&levels(b, false)?))
}

/// `string.Compare(a, b, StringComparison.InvariantCulture)` (the sign):
/// exact for covered characters; others sort after all covered ones, by
/// code point.
pub fn compare(a: &str, b: &str) -> Ordering {
    let full = |text| levels(text, true).unwrap_or_default();
    full(a).cmp(&full(b))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_table_decodes() {
        let table = table();
        assert_eq!(table.len(), 736);
        // `ß`: ICU key 4E4E 01 7005 01 C5D3 (two CEs: s and s).
        assert_eq!(
            weights('\u{DF}').unwrap().levels,
            [vec![0x4E, 0x4E], vec![0x70, 0x05], vec![0x05, 0xD3]]
        );
        assert!(weights('\u{0}').unwrap().levels.iter().all(Vec::is_empty));
        assert!(!is_covered('\u{2065}'));
    }

    #[test]
    fn names_sort_as_dotnet_sorts_them() {
        // .NET 10 (tests/data/collation.tsv).
        assert_eq!(try_compare("co-op", "coop"), Some(Ordering::Less));
        assert_eq!(try_compare("horan", "Horan"), Some(Ordering::Less));
        assert_eq!(try_compare("Zoë", "Zoe"), Some(Ordering::Greater));
        assert_eq!(try_compare("item 10", "item 2"), Some(Ordering::Less));
        assert_eq!(try_compare("l·l", "ŀl"), Some(Ordering::Equal));
        assert_eq!(try_compare("a", "\u{4E00}"), None);
        assert_eq!(compare("z", "\u{4E00}"), Ordering::Less);
    }
}
