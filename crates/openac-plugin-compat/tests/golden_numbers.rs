//! Number formatting, parsing and rounding against what .NET 10 prints
//! (`tests/data/*.tsv`, from a .NET generator program). Each file holds a few hundred
//! values, chosen at random over the whole range and at the sizes the plugins
//! print, plus the ties and edge cases.

use openac_plugin_compat::number::{
    self, MidpointRounding, NumberStyles, framework, parse_f32, parse_f64, parse_i32, parse_i64,
    parse_u32, parse_u64,
};

fn rows(data: &str) -> impl Iterator<Item = Vec<&str>> {
    data.lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
        .map(|l| l.split('\t').collect())
}

/// Splits `format=output` at the first `=` (the format may be empty).
fn pair(cell: &str) -> (&str, &str) {
    cell.split_once('=').expect("format=output")
}

fn f64_of(hex: &str) -> f64 {
    f64::from_bits(u64::from_str_radix(hex, 16).expect("hex bits"))
}

fn f32_of(hex: &str) -> f32 {
    f32::from_bits(u32::from_str_radix(hex, 16).expect("hex bits"))
}

/// Fails with the first mismatches listed, so one run shows the pattern.
fn check(failures: Vec<String>, total: usize) {
    assert!(total > 0, "no goldens read");
    assert!(
        failures.is_empty(),
        "{} of {} goldens differ:\n{}",
        failures.len(),
        total,
        failures
            .iter()
            .take(40)
            .cloned()
            .collect::<Vec<_>>()
            .join("\n")
    );
}

#[test]
fn double_formats_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/format_double.tsv")) {
        let value = f64_of(row[0]);
        for cell in &row[1..] {
            let (format, expected) = pair(cell);
            total += 1;
            match number::format_f64(value, format) {
                Ok(actual) if actual == expected => {}
                other => failures.push(format!(
                    "{value:?} ({}) {format:?}: .NET {expected:?}, ours {other:?}",
                    row[0]
                )),
            }
        }
    }
    check(failures, total);
}

#[test]
fn float_formats_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/format_float.tsv")) {
        let value = f32_of(row[0]);
        for cell in &row[1..] {
            let (format, expected) = pair(cell);
            total += 1;
            match number::format_f32(value, format) {
                Ok(actual) if actual == expected => {}
                other => failures.push(format!(
                    "{value:?} ({}) {format:?}: .NET {expected:?}, ours {other:?}",
                    row[0]
                )),
            }
        }
    }
    check(failures, total);
}

#[test]
fn plain_to_string_is_the_round_trip_form() {
    let mut total = 0;
    for row in rows(include_str!("data/format_double.tsv")) {
        let value = f64_of(row[0]);
        let (_, expected) = pair(row[1]);
        assert_eq!(number::to_string_f64(value), expected, "{value:?}");
        total += 1;
    }
    for row in rows(include_str!("data/format_float.tsv")) {
        let value = f32_of(row[0]);
        let (_, expected) = pair(row[1]);
        assert_eq!(number::to_string_f32(value), expected, "{value:?}");
        total += 1;
    }
    assert!(total > 500);
}

#[test]
fn integer_formats_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/format_integer.tsv")) {
        let (ty, value) = (row[0], row[1]);
        for cell in &row[2..] {
            let (format, expected) = pair(cell);
            total += 1;
            let actual = match ty {
                "i32" => number::format_i32(value.parse().unwrap(), format),
                "u32" => number::format_u32(value.parse().unwrap(), format),
                "i64" => number::format_i64(value.parse().unwrap(), format),
                "u64" => number::format_u64(value.parse().unwrap(), format),
                other => panic!("type {other}"),
            };
            match actual {
                Ok(actual) if actual == expected => {}
                other => failures.push(format!(
                    "{ty} {value} {format:?}: .NET {expected:?}, ours {other:?}"
                )),
            }
        }
    }
    check(failures, total);
}

#[test]
fn framework_rules_match_mosswart_massacres_converter() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/framework.tsv")) {
        let value = f64_of(row[0]);
        for cell in &row[1..] {
            let (rule, expected) = pair(cell);
            total += 1;
            let actual = match rule {
                "R" => Ok(framework::round_trip_f64(value)),
                "Rf" => Ok(framework::round_trip_f32(value as f32)),
                format => framework::fixed(value, format),
            };
            match actual {
                Ok(actual) if actual == expected => {}
                other => failures.push(format!(
                    "{value:?} ({}) {rule}: .NET {expected:?}, ours {other:?}",
                    row[0]
                )),
            }
        }
    }
    check(failures, total);
}

/// Undoes the generator's escaping (`\\`, `\t`, `\n`, `\r`, `\uXXXX`).
fn unescape(text: &str) -> String {
    let mut out = String::new();
    let mut chars = text.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('\\') => out.push('\\'),
            Some('t') => out.push('\t'),
            Some('n') => out.push('\n'),
            Some('r') => out.push('\r'),
            Some('u') => {
                let hex: String = chars.by_ref().take(4).collect();
                out.push(char::from_u32(u32::from_str_radix(&hex, 16).unwrap()).unwrap());
            }
            other => panic!("bad escape {other:?}"),
        }
    }
    out
}

fn styles(name: &str) -> NumberStyles {
    match name {
        "Float" => NumberStyles::FLOAT,
        "Float|AllowThousands" => NumberStyles::FLOAT | NumberStyles::ALLOW_THOUSANDS,
        "Any" => NumberStyles::ANY,
        "Integer" => NumberStyles::INTEGER,
        "Number" => NumberStyles::NUMBER,
        "HexNumber" => NumberStyles::HEX_NUMBER,
        other => panic!("styles {other}"),
    }
}

#[test]
fn parsing_matches_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/parse.tsv")) {
        let text = unescape(row[0]);
        for cell in &row[1..] {
            let (key, expected) = pair(cell);
            let (ty, style) = key.split_once(':').unwrap();
            let s = styles(style);
            let fail = |o: Option<String>| o.unwrap_or_else(|| "FAIL".to_owned());
            let actual = match ty {
                "f64" => fail(parse_f64(&text, s).map(|v| format!("{:016X}", v.to_bits()))),
                "f32" => fail(parse_f32(&text, s).map(|v| format!("{:08X}", v.to_bits()))),
                "i32" => fail(parse_i32(&text, s).map(|v| v.to_string())),
                "u32" => fail(parse_u32(&text, s).map(|v| v.to_string())),
                "i64" => fail(parse_i64(&text, s).map(|v| v.to_string())),
                "u64" => fail(parse_u64(&text, s).map(|v| v.to_string())),
                other => panic!("type {other}"),
            };
            total += 1;
            // .NET's NaN has the sign bit set; any NaN is the same value.
            let same = actual == expected
                || (ty == "f64" && actual == "7FF8000000000000" && expected == "FFF8000000000000")
                || (ty == "f32" && actual == "7FC00000" && expected == "FFC00000");
            if !same {
                failures.push(format!(
                    "{:?} {key}: .NET {expected}, ours {actual}",
                    row[0]
                ));
            }
        }
    }
    check(failures, total);
}

#[test]
fn rounding_matches_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    let bits = |v: f64| format!("{:016X}", v.to_bits());
    for row in rows(include_str!("data/round.tsv")) {
        let value = f64_of(row[0]);
        for cell in &row[1..] {
            let (op, expected) = pair(cell);
            total += 1;
            let actual = match op {
                "round" => bits(number::round(value)),
                "away" => bits(number::round_mode(value, MidpointRounding::AwayFromZero)),
                "toint32" => number::convert_to_int32(value)
                    .map_or_else(|| "OVERFLOW".to_owned(), |v| v.to_string()),
                _ => {
                    let (mode, digits) =
                        op.split_at(op.find(|c: char| c.is_ascii_digit()).unwrap());
                    let mode = if mode == "even" {
                        MidpointRounding::ToEven
                    } else {
                        MidpointRounding::AwayFromZero
                    };
                    bits(number::round_digits(value, digits.parse().unwrap(), mode))
                }
            };
            let same = actual == expected || (value.is_nan() && actual.starts_with("7FF8"));
            if !same {
                failures.push(format!("{value:?} {op}: .NET {expected}, ours {actual}"));
            }
        }
    }
    check(failures, total);
}

/// `E`, `C`, `B`, integer `G<n>` and `P`, and custom formats with literal
/// text, quotes, escapes, sections, `%`, `‰` and exponents
/// (`data/format_more.tsv`); `!FormatException` is .NET throwing.
#[test]
fn more_formats_match_dotnet() {
    let mut failures = Vec::new();
    let mut total = 0;
    for row in rows(include_str!("data/format_more.tsv")) {
        let (ty, value) = (row[0], row[1]);
        for cell in &row[2..] {
            let (format, expected) = pair(cell);
            total += 1;
            let actual = match ty {
                "f64" => number::format_f64(f64_of(value), format),
                "f32" => number::format_f32(f32_of(value), format),
                "i32" => number::format_i32(value.parse().unwrap(), format),
                "u32" => number::format_u32(value.parse().unwrap(), format),
                "i64" => number::format_i64(value.parse().unwrap(), format),
                "u64" => number::format_u64(value.parse().unwrap(), format),
                other => panic!("type {other}"),
            };
            match (actual, expected) {
                (Ok(actual), expected) if actual == expected => {}
                (Err(number::FormatError::Invalid(_)), "!FormatException") => {}
                (other, expected) => failures.push(format!(
                    "{ty} {value} {format:?}: .NET {expected:?}, ours {other:?}"
                )),
            }
        }
    }
    check(failures, total);
}
