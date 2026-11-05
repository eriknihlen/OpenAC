//! A strict JSON reader (RFC 8259), with the 64-level depth limit both .NET
//! libraries apply by default.

use super::{JsonNumber, JsonValue};

/// The nesting limit of `JsonSerializerOptions.MaxDepth` and Newtonsoft's
/// `JsonSerializerSettings.MaxDepth`.
const DEFAULT_MAX_DEPTH: usize = 64;

/// Why a document did not parse; `offset` is a byte offset into the text.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum JsonParseError {
    /// The text ended inside a value.
    #[error("unexpected end of JSON at byte {offset}")]
    UnexpectedEnd {
        /// Where the text ended.
        offset: usize,
    },
    /// A character that cannot start or continue a value here.
    #[error("unexpected {found:?} at byte {offset}")]
    Unexpected {
        /// The offending character.
        found: char,
        /// Where it is.
        offset: usize,
    },
    /// Nesting deeper than the limit.
    #[error("JSON nested deeper than {limit} levels at byte {offset}")]
    TooDeep {
        /// The limit.
        limit: usize,
        /// Where the limit was crossed.
        offset: usize,
    },
    /// Something other than whitespace after the document.
    #[error("additional text after the JSON value at byte {offset}")]
    TrailingText {
        /// Where it starts.
        offset: usize,
    },
}

/// Reads one JSON document: only RFC 8259 whitespace around it, no comments,
/// no trailing commas, no single quotes or bare names, no `NaN` literals.
/// An escaped lone surrogate (`"\ud800"`) becomes U+FFFD.
pub fn parse(text: &str) -> Result<JsonValue, JsonParseError> {
    parse_with_max_depth(text, DEFAULT_MAX_DEPTH)
}

/// [`parse`] with another nesting limit.
pub fn parse_with_max_depth(text: &str, max_depth: usize) -> Result<JsonValue, JsonParseError> {
    let mut reader = Reader {
        text,
        bytes: text.as_bytes(),
        pos: 0,
        max_depth,
    };
    reader.skip_whitespace();
    let value = reader.value(0)?;
    reader.skip_whitespace();
    if reader.pos < reader.bytes.len() {
        return Err(JsonParseError::TrailingText { offset: reader.pos });
    }
    Ok(value)
}

struct Reader<'a> {
    text: &'a str,
    bytes: &'a [u8],
    pos: usize,
    max_depth: usize,
}

impl Reader<'_> {
    fn skip_whitespace(&mut self) {
        while let Some(b' ' | b'\t' | b'\r' | b'\n') = self.bytes.get(self.pos) {
            self.pos += 1;
        }
    }

    fn unexpected(&self) -> JsonParseError {
        match self.text[self.pos..].chars().next() {
            Some(found) => JsonParseError::Unexpected {
                found,
                offset: self.pos,
            },
            None => JsonParseError::UnexpectedEnd { offset: self.pos },
        }
    }

    fn expect(&mut self, byte: u8) -> Result<(), JsonParseError> {
        if self.bytes.get(self.pos) == Some(&byte) {
            self.pos += 1;
            Ok(())
        } else {
            Err(self.unexpected())
        }
    }

    fn literal(&mut self, word: &str, value: JsonValue) -> Result<JsonValue, JsonParseError> {
        if self.text[self.pos..].starts_with(word) {
            self.pos += word.len();
            Ok(value)
        } else {
            Err(self.unexpected())
        }
    }

    fn value(&mut self, depth: usize) -> Result<JsonValue, JsonParseError> {
        match self.bytes.get(self.pos) {
            None => Err(JsonParseError::UnexpectedEnd { offset: self.pos }),
            Some(b'{') => self.object(depth + 1),
            Some(b'[') => self.array(depth + 1),
            Some(b'"') => Ok(JsonValue::String(self.string()?)),
            Some(b't') => self.literal("true", JsonValue::Bool(true)),
            Some(b'f') => self.literal("false", JsonValue::Bool(false)),
            Some(b'n') => self.literal("null", JsonValue::Null),
            Some(b'-' | b'0'..=b'9') => self.number(),
            Some(_) => Err(self.unexpected()),
        }
    }

    fn check_depth(&self, depth: usize) -> Result<(), JsonParseError> {
        if depth > self.max_depth {
            Err(JsonParseError::TooDeep {
                limit: self.max_depth,
                offset: self.pos,
            })
        } else {
            Ok(())
        }
    }

    fn object(&mut self, depth: usize) -> Result<JsonValue, JsonParseError> {
        self.check_depth(depth)?;
        self.pos += 1;
        let mut members = Vec::new();
        self.skip_whitespace();
        if self.bytes.get(self.pos) == Some(&b'}') {
            self.pos += 1;
            return Ok(JsonValue::Object(members));
        }
        loop {
            self.skip_whitespace();
            if self.bytes.get(self.pos) != Some(&b'"') {
                return Err(self.unexpected());
            }
            let name = self.string()?;
            self.skip_whitespace();
            self.expect(b':')?;
            self.skip_whitespace();
            let value = self.value(depth)?;
            members.push((name, value));
            self.skip_whitespace();
            match self.bytes.get(self.pos) {
                Some(b',') => self.pos += 1,
                Some(b'}') => {
                    self.pos += 1;
                    return Ok(JsonValue::Object(members));
                }
                _ => return Err(self.unexpected()),
            }
        }
    }

    fn array(&mut self, depth: usize) -> Result<JsonValue, JsonParseError> {
        self.check_depth(depth)?;
        self.pos += 1;
        let mut items = Vec::new();
        self.skip_whitespace();
        if self.bytes.get(self.pos) == Some(&b']') {
            self.pos += 1;
            return Ok(JsonValue::Array(items));
        }
        loop {
            self.skip_whitespace();
            items.push(self.value(depth)?);
            self.skip_whitespace();
            match self.bytes.get(self.pos) {
                Some(b',') => self.pos += 1,
                Some(b']') => {
                    self.pos += 1;
                    return Ok(JsonValue::Array(items));
                }
                _ => return Err(self.unexpected()),
            }
        }
    }

    fn digits(&mut self) -> usize {
        let start = self.pos;
        while self.bytes.get(self.pos).is_some_and(u8::is_ascii_digit) {
            self.pos += 1;
        }
        self.pos - start
    }

    fn number(&mut self) -> Result<JsonValue, JsonParseError> {
        let start = self.pos;
        if self.bytes[self.pos] == b'-' {
            self.pos += 1;
        }
        match self.bytes.get(self.pos) {
            Some(b'0') => self.pos += 1,
            Some(b'1'..=b'9') => {
                self.digits();
            }
            _ => return Err(self.unexpected()),
        }
        if self.bytes.get(self.pos) == Some(&b'.') {
            self.pos += 1;
            if self.digits() == 0 {
                return Err(self.unexpected());
            }
        }
        if let Some(b'e' | b'E') = self.bytes.get(self.pos) {
            self.pos += 1;
            if let Some(b'+' | b'-') = self.bytes.get(self.pos) {
                self.pos += 1;
            }
            if self.digits() == 0 {
                return Err(self.unexpected());
            }
        }
        Ok(JsonValue::Number(JsonNumber::Text(
            self.text[start..self.pos].to_owned(),
        )))
    }

    fn hex4(&mut self) -> Result<u16, JsonParseError> {
        let digits = self
            .text
            .get(self.pos..self.pos + 4)
            .filter(|d| d.bytes().all(|b| b.is_ascii_hexdigit()))
            .ok_or_else(|| self.unexpected())?;
        self.pos += 4;
        u16::from_str_radix(digits, 16).map_err(|_| self.unexpected())
    }

    fn string(&mut self) -> Result<String, JsonParseError> {
        self.pos += 1;
        let mut out = String::new();
        loop {
            let run_start = self.pos;
            while let Some(&b) = self.bytes.get(self.pos) {
                if b == b'"' || b == b'\\' || b < 0x20 {
                    break;
                }
                self.pos += 1;
            }
            out.push_str(&self.text[run_start..self.pos]);
            match self.bytes.get(self.pos) {
                None => return Err(JsonParseError::UnexpectedEnd { offset: self.pos }),
                Some(b'"') => {
                    self.pos += 1;
                    return Ok(out);
                }
                Some(b'\\') => {
                    self.pos += 1;
                    let escape = self
                        .bytes
                        .get(self.pos)
                        .copied()
                        .ok_or(JsonParseError::UnexpectedEnd { offset: self.pos })?;
                    self.pos += 1;
                    match escape {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{8}'),
                        b'f' => out.push('\u{c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let unit = self.hex4()?;
                            let c = if (0xD800..0xDC00).contains(&unit)
                                && self.text[self.pos..].starts_with("\\u")
                            {
                                let save = self.pos;
                                self.pos += 2;
                                let low = self.hex4()?;
                                if (0xDC00..0xE000).contains(&low) {
                                    char::decode_utf16([unit, low])
                                        .next()
                                        .and_then(Result::ok)
                                        .unwrap_or(char::REPLACEMENT_CHARACTER)
                                } else {
                                    self.pos = save;
                                    char::REPLACEMENT_CHARACTER
                                }
                            } else {
                                char::from_u32(u32::from(unit))
                                    .unwrap_or(char::REPLACEMENT_CHARACTER)
                            };
                            out.push(c);
                        }
                        _ => {
                            self.pos -= 1;
                            return Err(self.unexpected());
                        }
                    }
                }
                Some(_) => return Err(self.unexpected()),
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_documents() {
        let v =
            parse(r#" {"a": [1, -2.5e3, "x\u00e9\ud83d\ude00", true, null], "b": {}} "#).unwrap();
        assert_eq!(v.get("a").unwrap().as_array().unwrap().len(), 5);
        assert_eq!(
            v.get("a").unwrap().as_array().unwrap()[2].as_str(),
            Some("x\u{e9}\u{1F600}")
        );
        assert_eq!(
            v.get("a").unwrap().as_array().unwrap()[1].as_f64(),
            Some(-2500.0)
        );
        assert_eq!(parse(r#""\ud800x""#).unwrap().as_str(), Some("\u{FFFD}x"));
    }

    #[test]
    fn refuses_what_strict_json_refuses() {
        for bad in [
            "",
            "{",
            "[1,]",
            "{\"a\":1,}",
            "// c\n1",
            "'a'",
            "{a:1}",
            "NaN",
            "01",
            "1.",
            ".5",
            "1e",
            "\"\t\"",
            "\"\\x\"",
            "[1] 2",
            "\u{feff}1",
            "tru",
        ] {
            assert!(parse(bad).is_err(), "{bad:?} parsed");
        }
    }

    #[test]
    fn depth_limit_is_sixty_four() {
        let ok = "[".repeat(64) + &"]".repeat(64);
        assert!(parse(&ok).is_ok());
        let deep = "[".repeat(65) + &"]".repeat(65);
        assert!(matches!(parse(&deep), Err(JsonParseError::TooDeep { .. })));
    }
}
