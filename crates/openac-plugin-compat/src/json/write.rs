//! The two writers. Both lay a document out the same way (compact, or
//! indented by two spaces with `": "` after names and empty containers as
//! `[]`/`{}`); they differ in string escaping and number text.

use super::{JsonNumber, JsonValue, NewtonsoftNumber};
use crate::number::{self, framework};

/// A value System.Text.Json refuses to write.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum JsonWriteError {
    /// NaN or an infinity without `AllowNamedFloatingPointLiterals`
    /// (.NET throws `ArgumentException`).
    #[error("{0} is not a valid JSON number")]
    NonFiniteNumber(String),
}

/// The line break .NET writes between indented lines: `Environment.NewLine`,
/// which is `"\r\n"` on Windows, where the C# builds run.
pub const WINDOWS_NEW_LINE: &str = "\r\n";

trait Flavor {
    fn string(&self, out: &mut String, text: &str);
    fn number(&self, out: &mut String, number: &JsonNumber) -> Result<(), JsonWriteError>;
}

fn write_value(
    flavor: &dyn Flavor,
    out: &mut String,
    value: &JsonValue,
    indent: Option<&str>,
    depth: usize,
) -> Result<(), JsonWriteError> {
    let line = |out: &mut String, depth: usize| {
        if let Some(newline) = indent {
            out.push_str(newline);
            for _ in 0..depth {
                out.push_str("  ");
            }
        }
    };
    match value {
        JsonValue::Null => out.push_str("null"),
        JsonValue::Bool(true) => out.push_str("true"),
        JsonValue::Bool(false) => out.push_str("false"),
        JsonValue::Number(n) => flavor.number(out, n)?,
        JsonValue::String(s) => flavor.string(out, s),
        JsonValue::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                line(out, depth + 1);
                write_value(flavor, out, item, indent, depth + 1)?;
            }
            if !items.is_empty() {
                line(out, depth);
            }
            out.push(']');
        }
        JsonValue::Object(members) => {
            out.push('{');
            for (i, (name, item)) in members.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                line(out, depth + 1);
                flavor.string(out, name);
                out.push(':');
                if indent.is_some() {
                    out.push(' ');
                }
                write_value(flavor, out, item, indent, depth + 1)?;
            }
            if !members.is_empty() {
                line(out, depth);
            }
            out.push('}');
        }
    }
    Ok(())
}

fn push_hex4(out: &mut String, unit: u16, upper: bool) {
    if upper {
        out.push_str(&format!("\\u{unit:04X}"));
    } else {
        out.push_str(&format!("\\u{unit:04x}"));
    }
}

/// Newtonsoft.Json 13 (`JsonTextWriter`, `JsonConvert` defaults).
pub mod newtonsoft {
    use super::*;

    /// `Formatting.None` or `Formatting.Indented`.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum Formatting {
        /// No whitespace.
        None,
        /// Two-space indentation, one member or element per line.
        Indented,
    }

    /// How `double` and `float` values are written.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum Floats {
        /// Newtonsoft's own rule on modern .NET: the shortest round-trip text
        /// (`ToString("R")`), plus `.0` when it has no point or exponent.
        Modern,
        /// MosswartMassacre's `FrameworkNumberConverter`: the .NET Framework
        /// `"R"` digits (`G15`/`G17`, `G7`/`G9`, no negative zero), plus `.0`.
        Framework,
    }

    /// Writer settings.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct Options {
        /// Compact or indented.
        pub formatting: Formatting,
        /// Floating-point text.
        pub floats: Floats,
        /// Line break for indented output.
        pub new_line: &'static str,
    }

    impl Options {
        /// `JsonConvert.SerializeObject(value)`: compact, modern numbers.
        pub const COMPACT: Self = Self {
            formatting: Formatting::None,
            floats: Floats::Modern,
            new_line: WINDOWS_NEW_LINE,
        };
        /// `JsonConvert.SerializeObject(value, Formatting.Indented)` on Windows.
        pub const INDENTED: Self = Self {
            formatting: Formatting::Indented,
            floats: Floats::Modern,
            new_line: WINDOWS_NEW_LINE,
        };
        /// MosswartMassacre's `Wire.Serialize`: compact, Framework numbers.
        pub const MOSSWART_WIRE: Self = Self {
            formatting: Formatting::None,
            floats: Floats::Framework,
            new_line: WINDOWS_NEW_LINE,
        };
    }

    struct Newtonsoft(Floats);

    /// Newtonsoft's rule: a number with no point and no exponent gets `.0`.
    fn ensure_decimal_place(mut text: String) -> String {
        if !text.contains(['.', 'E', 'e']) {
            text.push_str(".0");
        }
        text
    }

    fn non_finite(out: &mut String, value: f64) -> bool {
        // FloatFormatHandling.String: the symbol in quotes.
        if value.is_finite() {
            return false;
        }
        out.push('"');
        out.push_str(&number::to_string_f64(value));
        out.push('"');
        true
    }

    impl Flavor for Newtonsoft {
        fn string(&self, out: &mut String, text: &str) {
            out.push('"');
            for c in text.chars() {
                match c {
                    '"' => out.push_str("\\\""),
                    '\\' => out.push_str("\\\\"),
                    '\u{8}' => out.push_str("\\b"),
                    '\t' => out.push_str("\\t"),
                    '\n' => out.push_str("\\n"),
                    '\u{c}' => out.push_str("\\f"),
                    '\r' => out.push_str("\\r"),
                    c if (c as u32) < 0x20 || matches!(c, '\u{85}' | '\u{2028}' | '\u{2029}') => {
                        push_hex4(out, c as u16, false);
                    }
                    c => out.push(c),
                }
            }
            out.push('"');
        }

        fn number(&self, out: &mut String, number: &JsonNumber) -> Result<(), JsonWriteError> {
            match (number, self.0) {
                (JsonNumber::I64(v), _) => out.push_str(&v.to_string()),
                (JsonNumber::U64(v), _) => out.push_str(&v.to_string()),
                (JsonNumber::F32(v), floats) => {
                    if !non_finite(out, f64::from(*v)) {
                        out.push_str(&ensure_decimal_place(match floats {
                            Floats::Modern => number::to_string_f32(*v),
                            Floats::Framework => framework::round_trip_f32(*v),
                        }));
                    }
                }
                (JsonNumber::F64(v), floats) => self.double(out, *v, floats),
                // A number read from a document: Newtonsoft holds it as a
                // long, a BigInteger or a double and writes that.
                (JsonNumber::Text(_), floats) => match number.newtonsoft_kind() {
                    NewtonsoftNumber::Integer(v) => out.push_str(&v.to_string()),
                    NewtonsoftNumber::Big(t) => out.push_str(&t),
                    NewtonsoftNumber::Float(v) => self.double(out, v, floats),
                },
            }
            Ok(())
        }
    }

    impl Newtonsoft {
        fn double(&self, out: &mut String, v: f64, floats: Floats) {
            if !non_finite(out, v) {
                out.push_str(&ensure_decimal_place(match floats {
                    Floats::Modern => number::to_string_f64(v),
                    Floats::Framework => framework::round_trip_f64(v),
                }));
            }
        }
    }

    /// The document as Newtonsoft.Json writes it with these options.
    pub fn to_string(value: &JsonValue, options: &Options) -> String {
        let mut out = String::new();
        let indent = match options.formatting {
            Formatting::None => None,
            Formatting::Indented => Some(options.new_line),
        };
        // Newtonsoft writes every value; nothing can fail.
        let _ = write_value(&Newtonsoft(options.floats), &mut out, value, indent, 0);
        out
    }

    /// `JsonConvert.ToString(string)`: one string as a JSON literal.
    pub fn string_literal(text: &str) -> String {
        let mut out = String::new();
        Newtonsoft(Floats::Modern).string(&mut out, text);
        out
    }
}

/// System.Text.Json (`JsonSerializer`, `Utf8JsonWriter` defaults).
pub mod system_text_json {
    use super::*;

    /// Writer settings.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct Options {
        /// `WriteIndented`.
        pub write_indented: bool,
        /// `JsonNumberHandling.AllowNamedFloatingPointLiterals`: NaN and the
        /// infinities as the strings `"NaN"`, `"Infinity"`, `"-Infinity"`.
        pub named_floating_point_literals: bool,
        /// Line break for indented output (`JsonSerializerOptions.NewLine`).
        pub new_line: &'static str,
    }

    impl Options {
        /// `new JsonSerializerOptions()`.
        pub const COMPACT: Self = Self {
            write_indented: false,
            named_floating_point_literals: false,
            new_line: WINDOWS_NEW_LINE,
        };
        /// `new JsonSerializerOptions { WriteIndented = true }` on Windows.
        pub const INDENTED: Self = Self {
            write_indented: true,
            named_floating_point_literals: false,
            new_line: WINDOWS_NEW_LINE,
        };
    }

    struct SystemTextJson {
        named_literals: bool,
    }

    impl Flavor for SystemTextJson {
        /// `JavaScriptEncoder.Default`: ASCII only; HTML-sensitive characters
        /// (`" & ' + < > \``) and DEL as `\uXXXX`; everything outside ASCII
        /// as `\uXXXX` (surrogate pairs above U+FFFF).
        fn string(&self, out: &mut String, text: &str) {
            out.push('"');
            for c in text.chars() {
                match c {
                    '\\' => out.push_str("\\\\"),
                    '\u{8}' => out.push_str("\\b"),
                    '\t' => out.push_str("\\t"),
                    '\n' => out.push_str("\\n"),
                    '\u{c}' => out.push_str("\\f"),
                    '\r' => out.push_str("\\r"),
                    '"' | '&' | '\'' | '+' | '<' | '>' | '`' | '\u{7f}' => {
                        push_hex4(out, c as u16, true);
                    }
                    c if (c as u32) < 0x20 || (c as u32) > 0x7E => {
                        let mut units = [0u16; 2];
                        for unit in c.encode_utf16(&mut units) {
                            push_hex4(out, *unit, true);
                        }
                    }
                    c => out.push(c),
                }
            }
            out.push('"');
        }

        fn number(&self, out: &mut String, number: &JsonNumber) -> Result<(), JsonWriteError> {
            let text = match number {
                JsonNumber::I64(v) => v.to_string(),
                JsonNumber::U64(v) => v.to_string(),
                JsonNumber::F64(v) => self.floating(*v, number::to_string_f64(*v))?,
                JsonNumber::F32(v) => self.floating(f64::from(*v), number::to_string_f32(*v))?,
                // A JsonElement writes its original text.
                JsonNumber::Text(t) => t.clone(),
            };
            out.push_str(&text);
            Ok(())
        }
    }

    impl SystemTextJson {
        fn floating(&self, value: f64, text: String) -> Result<String, JsonWriteError> {
            if value.is_finite() {
                Ok(text)
            } else if self.named_literals {
                Ok(format!("\"{text}\""))
            } else {
                Err(JsonWriteError::NonFiniteNumber(text))
            }
        }
    }

    /// The document as `JsonSerializer.Serialize` writes it with these
    /// options; an error where .NET throws.
    pub fn to_string(value: &JsonValue, options: &Options) -> Result<String, JsonWriteError> {
        let mut out = String::new();
        let indent = options.write_indented.then_some(options.new_line);
        let flavor = SystemTextJson {
            named_literals: options.named_floating_point_literals,
        };
        write_value(&flavor, &mut out, value, indent, 0)?;
        Ok(out)
    }

    /// `JsonNamingPolicy.CamelCase.ConvertName`: the leading run of capitals
    /// lower-cased, except the last one when a lower-case letter follows
    /// (`URLValue` → `urlValue`, `ID` → `id`, `AB_CD` → `aB_CD`).
    pub fn camel_case(name: &str) -> String {
        let mut chars: Vec<char> = name.chars().collect();
        if chars.first().is_none_or(|c| !c.is_uppercase()) {
            return name.to_owned();
        }
        let lower = |c: char| {
            let mut it = c.to_lowercase();
            match (it.next(), it.next()) {
                (Some(l), None) => l,
                _ => c,
            }
        };
        for i in 0..chars.len() {
            if i == 1 && !chars[i].is_uppercase() {
                break;
            }
            let has_next = i + 1 < chars.len();
            if i > 0 && has_next && !chars[i + 1].is_uppercase() {
                if chars[i + 1] == ' ' {
                    chars[i] = lower(chars[i]);
                }
                break;
            }
            chars[i] = lower(chars[i]);
        }
        chars.into_iter().collect()
    }
}
