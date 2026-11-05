//! The invariant culture's string order against .NET 10's
//! `string.Compare(a, b, StringComparison.InvariantCulture)`
//! (`tests/data/collation.tsv`, from a .NET generator program): names, random strings over the
//! covered characters, near-duplicates and long runs.

use std::cmp::Ordering;

use openac_plugin_compat::text::collation;

/// Undoes the generator's escaping (`\\`, `\t`, `\n`, `\r`, `\uXXXX` per
/// UTF-16 code unit).
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

#[test]
fn invariant_order_matches_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for line in include_str!("data/collation.tsv")
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
    {
        let cells: Vec<&str> = line.split('\t').collect();
        let (a, b) = (unescape(cells[0]), unescape(cells[1]));
        let expected = match cells[2] {
            "-1" => Ordering::Less,
            "0" => Ordering::Equal,
            "1" => Ordering::Greater,
            other => panic!("sign {other}"),
        };
        total += 1;
        let actual = collation::try_compare(&a, &b);
        if actual != Some(expected) {
            failures.push(format!("{a:?} {b:?}: .NET {expected:?}, ours {actual:?}"));
        }
        if collation::compare(&a, &b) != expected {
            failures.push(format!("{a:?} {b:?}: compare differs from try_compare"));
        }
    }
    assert!(total > 7000);
    assert!(
        failures.is_empty(),
        "{} of {total} differ:\n{}",
        failures.len(),
        failures
            .iter()
            .take(40)
            .cloned()
            .collect::<Vec<_>>()
            .join("\n")
    );
}
