//! Invariant casing, white space and ordinal string operations against what
//! .NET 10 does (`tests/data/casing.tsv`, `tests/data/strings.tsv`, from a
//! .NET generator program).

use std::cmp::Ordering;
use std::collections::BTreeMap;

use openac_plugin_compat::text::{casing, ordinal, utf16};

fn rows(data: &str) -> impl Iterator<Item = Vec<&str>> {
    data.lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
        .map(|l| l.split('\t').collect())
}

/// Undoes the generator's escaping (`\\`, `\t`, `\n`, `\r`, `\uXXXX` per
/// UTF-16 code unit, so a surrogate pair is two escapes).
fn unescape(text: &str) -> String {
    let mut units: Vec<u16> = Vec::new();
    let mut chars = text.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            let mut buf = [0u16; 2];
            units.extend_from_slice(c.encode_utf16(&mut buf));
            continue;
        }
        match chars.next() {
            Some('\\') => units.push(u16::from(b'\\')),
            Some('t') => units.push(u16::from(b'\t')),
            Some('n') => units.push(u16::from(b'\n')),
            Some('r') => units.push(u16::from(b'\r')),
            Some('u') => {
                let hex: String = chars.by_ref().take(4).collect();
                units.push(u16::from_str_radix(&hex, 16).unwrap());
            }
            other => panic!("bad escape {other:?}"),
        }
    }
    String::from_utf16(&units).expect("the goldens hold whole characters")
}

fn units_text(hex: &str) -> String {
    let units: Vec<u16> = (0..hex.len())
        .step_by(4)
        .map(|i| u16::from_str_radix(&hex[i..i + 4], 16).unwrap())
        .collect();
    String::from_utf16(&units).unwrap()
}

fn one_char(hex: &str) -> char {
    let text = units_text(hex);
    let mut chars = text.chars();
    let c = chars.next().unwrap();
    assert!(chars.next().is_none(), "one character: {hex}");
    c
}

/// Every scalar value, against the mappings .NET lists (the ones it does not
/// list map to themselves).
#[test]
fn casing_matches_dotnet_for_every_code_point() {
    let mut upper = BTreeMap::new();
    let mut lower = BTreeMap::new();
    let mut ordinal_key = BTreeMap::new();
    let mut white = Vec::new();
    for row in rows(include_str!("data/casing.tsv")) {
        if row[0] == "whitespace" {
            white = row[1]
                .split(',')
                .map(|h| char::from_u32(u32::from_str_radix(h, 16).unwrap()).unwrap())
                .collect();
            continue;
        }
        let c = char::from_u32(u32::from_str_radix(row[0], 16).unwrap()).unwrap();
        upper.insert(c, one_char(row[1].strip_prefix("upper=").unwrap()));
        lower.insert(c, one_char(row[2].strip_prefix("lower=").unwrap()));
        ordinal_key.insert(c, one_char(row[3].strip_prefix("ordinal=").unwrap()));
    }
    assert!(upper.len() > 2000 && !white.is_empty());
    let mut failures = Vec::new();
    for c in (0..=0x10FFFF).filter_map(char::from_u32) {
        let expect = |map: &BTreeMap<char, char>| map.get(&c).copied().unwrap_or(c);
        for (what, ours, dotnet) in [
            ("upper", casing::to_upper_char(c), expect(&upper)),
            ("lower", casing::to_lower_char(c), expect(&lower)),
            (
                "ordinal",
                casing::ordinal_ignore_case_key(c),
                expect(&ordinal_key),
            ),
        ] {
            if ours != dotnet {
                failures.push(format!(
                    "{what} U+{:04X}: .NET U+{:04X}, ours U+{:04X}",
                    c as u32, dotnet as u32, ours as u32
                ));
            }
        }
        if casing::is_white_space(c) != white.contains(&c) {
            failures.push(format!("white space U+{:04X}", c as u32));
        }
    }
    assert!(
        failures.is_empty(),
        "{} differ:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

fn sign(o: Ordering) -> &'static str {
    match o {
        Ordering::Less => "-1",
        Ordering::Equal => "0",
        Ordering::Greater => "1",
    }
}

fn index(found: Option<usize>) -> String {
    found.map_or_else(|| "-1".to_owned(), |i| i.to_string())
}

fn truth(b: bool) -> &'static str {
    if b { "True" } else { "False" }
}

#[test]
fn ordinal_operations_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/strings.tsv")) {
        let (a, b) = (unescape(row[0]), unescape(row[1]));
        for cell in &row[2..] {
            let (op, expected) = cell.split_once('=').unwrap();
            total += 1;
            let actual = match op {
                "len" => utf16::len(&a).to_string(),
                "ordinal" => sign(ordinal::compare(&a, &b)).to_owned(),
                "ignore_case" => sign(ordinal::compare_ignore_case(&a, &b)).to_owned(),
                "equals_ignore_case" => truth(ordinal::equals_ignore_case(&a, &b)).to_owned(),
                "index_of" => index(ordinal::index_of(&a, &b)),
                "last_index_of" => index(ordinal::last_index_of(&a, &b)),
                "index_of_ignore_case" => index(ordinal::index_of_ignore_case(&a, &b)),
                "last_index_of_ignore_case" => index(ordinal::last_index_of_ignore_case(&a, &b)),
                "starts_with_ignore_case" => {
                    truth(ordinal::starts_with_ignore_case(&a, &b)).to_owned()
                }
                "ends_with_ignore_case" => truth(ordinal::ends_with_ignore_case(&a, &b)).to_owned(),
                "contains_ignore_case" => truth(ordinal::contains_ignore_case(&a, &b)).to_owned(),
                other => panic!("op {other}"),
            };
            if actual != expected {
                failures.push(format!("{a:?} {b:?} {op}: .NET {expected}, ours {actual}"));
            }
        }
    }
    assert!(total > 1000);
    assert!(
        failures.is_empty(),
        "{} of {total} differ:\n{}",
        failures.len(),
        failures.join("\n")
    );
}
