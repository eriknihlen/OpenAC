//! .NET regular expressions against what .NET 10's `Regex` returns
//! (`tests/data/regex.jsonl`, one case per line, from a .NET generator
//! program): group names, the first match
//! and its groups, all matches, `Replace`, `Split` and `Regex.Escape`.
//! The first cases are MossTank's and MosswartMassacre's own patterns.

use openac_plugin_compat::json::{self, JsonValue};
use openac_plugin_compat::regex::{self, DotNetRegex, RegexError, RegexOptions};

/// .NET constructs the kit refuses and .NET accepts.
const KNOWN_UNSUPPORTED: [&str; 3] = ["[a-c-[b]]", "(?<o>a)(?<c-o>b)", "(a)?(?(1)b|c)"];

fn utf16_index(input: &str, byte: usize) -> i64 {
    input[..byte].encode_utf16().count() as i64
}

fn str_of<'a>(v: &'a JsonValue, key: &str) -> &'a str {
    v.get(key)
        .and_then(JsonValue::as_str)
        .unwrap_or_else(|| panic!("{key}"))
}

fn strings(v: &JsonValue) -> Vec<&str> {
    v.as_array()
        .unwrap()
        .iter()
        .map(|s| s.as_str().unwrap())
        .collect()
}

#[test]
fn every_case_matches_dotnet() {
    let mut cases = 0;
    let mut unsupported: Vec<String> = Vec::new();
    for line in include_str!("data/regex.jsonl")
        .lines()
        .filter(|l| !l.is_empty())
    {
        let case = json::parse(line).unwrap();
        let pattern = str_of(&case, "pattern");
        let input = str_of(&case, "input");
        let replacement = str_of(&case, "replacement");
        let options =
            RegexOptions::from_bits(case.get("options").unwrap().as_i64().unwrap() as u32);
        let what = format!("{pattern:?} on {input:?}");
        cases += 1;

        assert_eq!(
            regex::escape(&format!("{input}{pattern}")),
            str_of(&case, "escape"),
            "escape {what}"
        );

        let compiled = DotNetRegex::new(pattern, options);
        if case.get("error").is_some() {
            assert!(compiled.is_err(), ".NET refuses {what}, we accepted it");
            continue;
        }
        let re = match compiled {
            Ok(re) => re,
            Err(RegexError::Unsupported(why)) => {
                assert!(KNOWN_UNSUPPORTED.contains(&pattern), "{what}: {why}");
                unsupported.push(pattern.to_owned());
                continue;
            }
            Err(other) => panic!("{what}: {other}"),
        };

        assert_eq!(
            re.group_names(),
            strings(case.get("names").unwrap()),
            "names {what}"
        );

        let found = re.find(input).unwrap();
        match case.get("match").unwrap() {
            JsonValue::Null => assert!(found.is_none(), "{what} matched"),
            expected => {
                let m = found.unwrap_or_else(|| panic!("{what} did not match"));
                assert_eq!(m.value(), str_of(expected, "value"), "value {what}");
                assert_eq!(
                    utf16_index(input, m.index()),
                    expected.get("index").unwrap().as_i64().unwrap(),
                    "index {what}"
                );
                for g in expected.get("groups").unwrap().as_array().unwrap() {
                    let name = str_of(g, "name");
                    let ours = m.group(name);
                    assert_eq!(
                        ours.success,
                        g.get("success").unwrap().as_bool().unwrap(),
                        "group {name} of {what}"
                    );
                    assert_eq!(ours.value, str_of(g, "value"), "group {name} of {what}");
                    if ours.success {
                        assert_eq!(
                            utf16_index(input, ours.index),
                            g.get("index").unwrap().as_i64().unwrap(),
                            "group {name} index of {what}"
                        );
                    }
                }
            }
        }

        let all: Vec<(i64, &str)> = re
            .matches(input)
            .unwrap()
            .iter()
            .map(|m| (utf16_index(input, m.index()), m.value()))
            .collect();
        let expected: Vec<(i64, &str)> = case
            .get("matches")
            .unwrap()
            .as_array()
            .unwrap()
            .iter()
            .map(|m| {
                (
                    m.get("index").unwrap().as_i64().unwrap(),
                    str_of(m, "value"),
                )
            })
            .collect();
        assert_eq!(all, expected, "matches {what}");

        assert_eq!(
            re.replace(input, replacement).unwrap(),
            str_of(&case, "replace"),
            "replace {replacement:?} {what}"
        );
        assert_eq!(
            re.split(input).unwrap(),
            strings(case.get("split").unwrap()),
            "split {what}"
        );
    }
    assert_eq!(cases, 87);
    assert_eq!(unsupported, KNOWN_UNSUPPORTED);
}
