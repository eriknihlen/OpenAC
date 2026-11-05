//! The JSON writers and reader against Newtonsoft.Json 13.0.3 and
//! System.Text.Json on .NET 10 (`tests/data/json*.tsv`, from a .NET
//! generator program). Each document below is built
//! the same way as the C# object of the same case name.

use std::collections::HashMap;

use openac_plugin_compat::json::{
    self, JsonValue, newtonsoft, system_text_json, system_text_json::camel_case,
};

/// Undoes the generator's escaping: `\\`, `\t`, `\n`, `\r`, `\uXXXX`, `\u{X}`.
fn unescape(text: &str) -> String {
    let mut out = String::new();
    let mut chars = text.chars().peekable();
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
                let hex: String = if chars.peek() == Some(&'{') {
                    chars.next();
                    chars.by_ref().take_while(|&c| c != '}').collect()
                } else {
                    chars.by_ref().take(4).collect()
                };
                out.push(char::from_u32(u32::from_str_radix(&hex, 16).unwrap()).unwrap());
            }
            other => panic!("bad escape {other:?}"),
        }
    }
    out
}

fn goldens() -> HashMap<String, String> {
    include_str!("data/json.tsv")
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
        .map(|l| {
            let (name, value) = l.split_once('\t').unwrap();
            (name.to_owned(), unescape(value))
        })
        .collect()
}

fn all_characters() -> String {
    let mut s: String = (0u8..128).map(char::from).collect();
    for cp in [
        0x85, 0xA0, 0xE9, 0x2028, 0x2029, 0x20AC, 0x1F600, 0xFEFF, 0xFF, 0x100,
    ] {
        s.push(char::from_u32(cp).unwrap());
    }
    s
}

fn doubles() -> JsonValue {
    JsonValue::from(vec![
        0.0,
        -0.0,
        1.0,
        -12.0,
        0.1,
        42.5,
        1e15,
        1e16,
        1e17,
        1e-5,
        123.456,
        0.300_000_000_000_000_04,
        f64::from(33.65f32),
        -0.066_666_670_143_604_28,
        f64::MAX,
        f64::from_bits(1),
        1e21,
        75.0,
    ])
}

fn non_finite() -> JsonValue {
    JsonValue::from(vec![f64::NAN, f64::INFINITY, f64::NEG_INFINITY])
}

fn floats() -> JsonValue {
    JsonValue::from(vec![
        0.0f32,
        -0.0,
        1.0,
        3.0,
        0.1,
        2.711_539_7,
        1.000_000_1,
        1e8,
        1e9,
        16_777_216.0,
        f32::MAX,
    ])
}

fn integers() -> JsonValue {
    JsonValue::object([
        ("i", i32::MIN.into()),
        ("j", i32::MAX.into()),
        ("l", i64::MIN.into()),
        ("m", i64::MAX.into()),
        ("u", u32::MAX.into()),
        ("ul", u64::MAX.into()),
        ("b", 255u8.into()),
        ("sh", (-5i16).into()),
    ])
}

fn list() -> JsonValue {
    JsonValue::array([
        1.into(),
        "a\"b".into(),
        JsonValue::Null,
        JsonValue::object([("k", 2.into()), ("inner", vec![1.0, 2.5].into())]),
    ])
}

/// The C# `Nested()` object of the Newtonsoft cases.
fn nested() -> JsonValue {
    JsonValue::object([
        ("type", "x".into()),
        ("n", JsonValue::Null),
        ("t", true.into()),
        ("f", false.into()),
        ("empty_list", JsonValue::array([])),
        ("empty_obj", JsonValue::object::<&str>([])),
        (
            "dict",
            JsonValue::object([("1", 1.5.into()), ("2", (-0.0).into()), ("-3", 1e15.into())]),
        ),
        ("list", list()),
        ("nullable", Option::<f64>::None.into()),
        ("f32", 0.1f32.into()),
    ])
}

/// The C# `stjNested` object.
fn stj_nested() -> JsonValue {
    JsonValue::object([
        ("type", "x".into()),
        ("n", JsonValue::Null),
        ("t", true.into()),
        ("empty_list", JsonValue::array([])),
        ("empty_obj", JsonValue::object::<&str>([])),
        (
            "dict",
            JsonValue::object([("k\u{e9}y<", "v&'+`".into()), ("b", "".into())]),
        ),
        ("list", list()),
        (
            "nested_empty",
            JsonValue::object([("a", JsonValue::array([]))]),
        ),
    ])
}

#[test]
fn newtonsoft_writes_the_same_bytes() {
    let g = goldens();
    let compact = newtonsoft::Options::COMPACT;
    let wire = newtonsoft::Options::MOSSWART_WIRE;
    let strings = JsonValue::object([("s", all_characters().into())]);
    let cases: [(&str, JsonValue, newtonsoft::Options); 12] = [
        ("newtonsoft.strings", strings, compact),
        ("newtonsoft.doubles", doubles(), compact),
        ("newtonsoft.non_finite", non_finite(), compact),
        ("newtonsoft.floats", floats(), compact),
        ("newtonsoft.integers", integers(), compact),
        ("newtonsoft.nested", nested(), compact),
        (
            "newtonsoft.nested_indented",
            nested(),
            newtonsoft::Options::INDENTED,
        ),
        (
            "newtonsoft.convert_indented",
            nested(),
            newtonsoft::Options::INDENTED,
        ),
        ("framework.doubles", doubles(), wire),
        ("framework.non_finite", non_finite(), wire),
        ("framework.floats", floats(), wire),
        ("framework.nested", nested(), wire),
    ];
    for (name, value, options) in cases {
        assert_eq!(newtonsoft::to_string(&value, &options), g[name], "{name}");
    }
}

#[test]
fn system_text_json_writes_the_same_bytes() {
    let g = goldens();
    let compact = system_text_json::Options::COMPACT;
    let named = system_text_json::Options {
        named_floating_point_literals: true,
        ..compact
    };
    let strings = JsonValue::object([("s", all_characters().into())]);
    let cases: [(&str, JsonValue, system_text_json::Options); 8] = [
        ("stj.strings", strings, compact),
        ("stj.doubles", doubles(), compact),
        ("stj.non_finite_named", non_finite(), named),
        ("stj.floats", floats(), compact),
        ("stj.integers", integers(), compact),
        ("stj.nested", stj_nested(), compact),
        (
            "stj.nested_indented",
            stj_nested(),
            system_text_json::Options::INDENTED,
        ),
        ("stj.nested", stj_nested(), compact),
    ];
    for (name, value, options) in cases {
        assert_eq!(
            system_text_json::to_string(&value, &options).unwrap(),
            g[name],
            "{name}"
        );
    }
    assert_eq!(g["stj.non_finite_refused"], "ArgumentException");
    assert!(system_text_json::to_string(&non_finite(), &compact).is_err());
}

#[test]
fn camel_case_names_match() {
    let g = goldens();
    let mut total = 0;
    for (key, expected) in &g {
        if let Some(name) = key.strip_prefix("camel.") {
            assert_eq!(&camel_case(name), expected, "{name:?}");
            total += 1;
        }
    }
    assert_eq!(total, 18);
}

#[test]
fn reader_accepts_what_system_text_json_accepts() {
    let mut total = 0;
    let mut lenient_newtonsoft = 0;
    for line in include_str!("data/json_read.tsv")
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
    {
        let cols: Vec<&str> = line.split('\t').collect();
        let doc = unescape(cols[0]);
        let stj = cols[1] == "stj=ok";
        let newtonsoft = cols[2] == "newtonsoft=ok";
        assert_eq!(json::parse(&doc).is_ok(), stj, "{:?}", cols[0]);
        // Newtonsoft accepts more; it never refuses what is strict JSON.
        assert!(newtonsoft || !stj, "{:?}", cols[0]);
        lenient_newtonsoft += usize::from(newtonsoft && !stj);
        total += 1;
    }
    assert_eq!(total, 48);
    assert_eq!(lenient_newtonsoft, 19);
}

#[test]
fn newtonsoft_casts_match() {
    for line in include_str!("data/json_casts.tsv")
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
    {
        let cols: Vec<&str> = line.split('\t').collect();
        let token = json::parse(&unescape(cols[0])).unwrap();
        let int = match token.newtonsoft_i32() {
            Ok(Some(v)) => v.to_string(),
            Ok(None) => "NULL".to_owned(),
            Err(_) => "ERR".to_owned(),
        };
        let double = match token.newtonsoft_f64() {
            Ok(Some(v)) => format!("{:016X}", v.to_bits()),
            Ok(None) => "NULL".to_owned(),
            Err(_) => "ERR".to_owned(),
        };
        let string = match token.newtonsoft_string() {
            Ok(Some(v)) => v,
            Ok(None) => "NULL".to_owned(),
            Err(_) => "ERR".to_owned(),
        };
        assert_eq!(format!("int={int}"), cols[1], "{}", cols[0]);
        assert_eq!(format!("double={double}"), cols[2], "{}", cols[0]);
        assert_eq!(format!("string={string}"), unescape(cols[3]), "{}", cols[0]);
    }
}

#[test]
fn mosswart_massacre_frames() {
    // MosswartMassacre tests/Streams/StreamGoldenTests.cs, Vitals_frame: the
    // percentages are doubles (75.0, 0.0, 33.3), the counts ints.
    let frame = JsonValue::object([
        ("type", "vitals".into()),
        ("timestamp", "2026-09-25T08:30:15.1230000Z".into()),
        ("character_name", "Mossy".into()),
        ("health_current", 150.into()),
        ("health_max", 200.into()),
        ("health_percentage", 75.0.into()),
        ("stamina_current", 0.into()),
        ("stamina_max", 0.into()),
        ("stamina_percentage", 0.0.into()),
        ("mana_current", 333.into()),
        ("mana_max", 999.into()),
        ("mana_percentage", 33.3.into()),
        ("vitae", 0.into()),
    ]);
    assert_eq!(
        newtonsoft::to_string(&frame, &newtonsoft::Options::MOSSWART_WIRE),
        "{\"type\":\"vitals\",\"timestamp\":\"2026-09-25T08:30:15.1230000Z\",\"character_name\":\"Mossy\",\
         \"health_current\":150,\"health_max\":200,\"health_percentage\":75.0,\
         \"stamina_current\":0,\"stamina_max\":0,\"stamina_percentage\":0.0,\
         \"mana_current\":333,\"mana_max\":999,\"mana_percentage\":33.3,\
         \"vitae\":0}"
    );
    // InventoryGoldenTests: floats widened to double keep seventeen digits.
    let armor = JsonValue::object([
        ("167772160", f64::from(1.2f32).into()),
        ("167772163", f64::from(0.2f32).into()),
        ("167772164", 1.5.into()),
        ("167772165", f64::from(0.4f32).into()),
        ("167772166", f64::from(0.6f32).into()),
    ]);
    assert_eq!(
        newtonsoft::to_string(&armor, &newtonsoft::Options::MOSSWART_WIRE),
        "{\"167772160\":1.2000000476837158,\"167772163\":0.20000000298023224,\
         \"167772164\":1.5,\"167772165\":0.40000000596046448,\"167772166\":0.60000002384185791}"
    );
    // WireNumberTests: nulls and NaN keep Newtonsoft's handling.
    let v = JsonValue::object([("v", Option::<f64>::None.into())]);
    assert_eq!(
        newtonsoft::to_string(&v, &newtonsoft::Options::MOSSWART_WIRE),
        "{\"v\":null}"
    );
    let v = JsonValue::object([("v", f64::NAN.into())]);
    assert_eq!(
        newtonsoft::to_string(&v, &newtonsoft::Options::MOSSWART_WIRE),
        "{\"v\":\"NaN\"}"
    );
}
