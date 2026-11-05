//! Text decoding and date/time text against what .NET 10 prints
//! (`tests/data/text.tsv`, `tests/data/datetime.tsv`, from a .NET
//! generator program).

use openac_plugin_compat::datetime::{DateTime, DateTimeKind, TimeSpan};
use openac_plugin_compat::text;

fn rows(data: &str) -> impl Iterator<Item = Vec<&str>> {
    data.lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
        .map(|l| l.split('\t').collect())
}

fn units(s: &str) -> String {
    s.encode_utf16().map(|u| format!("{u:04X}")).collect()
}

fn bytes(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
        .collect()
}

#[test]
fn decoding_matches_dotnet() {
    let mut total = 0;
    for row in rows(include_str!("data/text.tsv")) {
        let input = bytes(row[0]);
        let get_string = row[1].strip_prefix("GetString=").unwrap();
        let read_all = row[2].strip_prefix("ReadAllText=").unwrap();
        assert_eq!(
            units(&text::utf8_get_string(&input)),
            get_string,
            "GetString {}",
            row[0]
        );
        assert_eq!(
            units(&text::read_all_text(&input)),
            read_all,
            "ReadAllText {}",
            row[0]
        );
        total += 1;
    }
    assert!(total > 40);
}

#[test]
fn dates_and_spans_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/datetime.tsv")) {
        let ticks: i64 = row[1].parse().unwrap();
        for cell in &row[2..] {
            let (format, expected) = cell.split_once('=').unwrap();
            total += 1;
            let actual = if row[0] == "TimeSpan" {
                TimeSpan::from_ticks(ticks).format(format)
            } else {
                let kind = match row[0] {
                    "Utc" => DateTimeKind::Utc,
                    "Unspecified" => DateTimeKind::Unspecified,
                    other => panic!("kind {other}"),
                };
                let base = DateTime::from_parts(1, 1, 1, 0, 0, 0, 0, kind).unwrap();
                let date = base.add_ticks(ticks);
                let format = format.strip_prefix("en-US:").unwrap_or(format);
                date.format(format)
            };
            match actual {
                Ok(text) if text == expected => {}
                other => failures.push(format!(
                    "{} {ticks} {format:?}: .NET {expected:?}, ours {other:?}",
                    row[0]
                )),
            }
        }
    }
    assert!(total > 600);
    assert!(
        failures.is_empty(),
        "{} differ:\n{}",
        failures.len(),
        failures[..failures.len().min(30)].join("\n")
    );
}
