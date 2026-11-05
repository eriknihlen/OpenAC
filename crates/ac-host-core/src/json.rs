//! A strict JSON reader for configuration documents, and the string escaping
//! used by the JSON lines this crate writes.
//!
//! Strict means RFC 8259 and nothing more: no comments, no trailing commas,
//! no leading zeros, no raw control characters inside strings, at most
//! [`MAX_DEPTH`] levels of nesting, and a member name appears at most once in
//! an object. Every problem is reported with the line and column where it was
//! found and never with the document's own text, so an error message cannot
//! repeat a secret someone pasted into the file.

use std::collections::HashSet;
use std::fmt;

/// Deepest nesting of objects and arrays a document may have.
pub const MAX_DEPTH: usize = 64;

/// Where in the document something is: 1-based line and column (characters).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Position {
    /// Line, counting from 1.
    pub line: u32,
    /// Column in characters, counting from 1.
    pub column: u32,
}

/// One JSON value.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    /// `null`.
    Null,
    /// `true` or `false`.
    Bool(bool),
    /// A number, kept as written; converted by the reader that knows its type.
    Number(String),
    /// A string, unescaped.
    String(String),
    /// An array.
    Array(Vec<Node>),
    /// An object, members in document order.
    Object(Vec<Member>),
}

/// A value and where it starts.
#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    /// The value.
    pub value: Value,
    /// Where the value starts.
    pub at: Position,
}

impl Node {
    /// A value made by the program rather than read (position line 0).
    #[must_use]
    pub fn made(value: Value) -> Self {
        Self {
            value,
            at: Position { line: 0, column: 0 },
        }
    }
}

impl Member {
    /// A member made by the program rather than read.
    #[must_use]
    pub fn made(name: &str, value: Value) -> Self {
        Self {
            name: name.to_owned(),
            value: Node::made(value),
            at: Position { line: 0, column: 0 },
        }
    }
}

/// One object member.
#[derive(Debug, Clone, PartialEq)]
pub struct Member {
    /// The member name, unescaped.
    pub name: String,
    /// The member's value.
    pub value: Node,
    /// Where the member name starts.
    pub at: Position,
}

/// Why a document is not acceptable, and where.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("line {}, column {}: {kind}", .at.line, .at.column)]
pub struct JsonError {
    /// What is wrong.
    pub kind: JsonErrorKind,
    /// Where it is.
    pub at: Position,
}

/// What is wrong with a document. No variant carries text from the document.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum JsonErrorKind {
    /// The bytes are not UTF-8.
    NotUtf8,
    /// There is no value at all.
    Empty,
    /// Not JSON; the text says what was expected.
    Syntax(&'static str),
    /// Nested deeper than [`MAX_DEPTH`].
    TooDeep,
    /// An object names the same member twice.
    DuplicateMember,
    /// An object has a member the format does not define.
    UnknownMember,
    /// A required member is missing.
    MissingMember(&'static str),
    /// A value has the wrong type; the text names the expected one.
    WrongType(&'static str),
    /// A number does not fit its type.
    OutOfRange,
    /// A string is not one of the names the member allows.
    UnknownEnumValue,
}

impl fmt::Display for JsonErrorKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotUtf8 => f.write_str("the text is not UTF-8"),
            Self::Empty => f.write_str("the document is empty"),
            Self::Syntax(expected) => write!(f, "{expected}"),
            Self::TooDeep => write!(f, "nested deeper than {MAX_DEPTH} levels"),
            Self::DuplicateMember => f.write_str("a member name is repeated"),
            Self::UnknownMember => f.write_str("a member name is not part of the format"),
            Self::MissingMember(name) => write!(f, "required member '{name}' is missing"),
            Self::WrongType(expected) => write!(f, "expected {expected}"),
            Self::OutOfRange => f.write_str("a number is out of range"),
            Self::UnknownEnumValue => f.write_str("a value is not one of the allowed names"),
        }
    }
}

/// Parses a whole document. A leading UTF-8 byte-order mark is skipped.
pub fn parse(bytes: &[u8]) -> Result<Node, JsonError> {
    let text = match std::str::from_utf8(bytes) {
        Ok(text) => text,
        Err(error) => {
            let valid = &bytes[..error.valid_up_to()];
            // The prefix is valid UTF-8 by definition.
            let prefix = std::str::from_utf8(valid).unwrap_or_default();
            return Err(JsonError {
                kind: JsonErrorKind::NotUtf8,
                at: position_of(prefix, prefix.len()),
            });
        }
    };
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let mut parser = Parser {
        text,
        bytes: text.as_bytes(),
        offset: 0,
        line: 1,
        line_start: 0,
    };
    parser.skip_whitespace();
    if parser.offset == parser.bytes.len() {
        return Err(parser.error(JsonErrorKind::Empty));
    }
    let node = parser.value(0)?;
    parser.skip_whitespace();
    if parser.offset != parser.bytes.len() {
        return Err(parser.error(JsonErrorKind::Syntax("unexpected text after the document")));
    }
    Ok(node)
}

fn position_of(text: &str, offset: usize) -> Position {
    let before = &text[..offset];
    let line = before.matches('\n').count() + 1;
    let line_start = before.rfind('\n').map_or(0, |index| index + 1);
    let column = before[line_start..].chars().count() + 1;
    Position {
        line: u32::try_from(line).unwrap_or(u32::MAX),
        column: u32::try_from(column).unwrap_or(u32::MAX),
    }
}

struct Parser<'a> {
    text: &'a str,
    bytes: &'a [u8],
    offset: usize,
    line: usize,
    line_start: usize,
}

impl Parser<'_> {
    fn here(&self) -> Position {
        let column = self.text[self.line_start..self.offset].chars().count() + 1;
        Position {
            line: u32::try_from(self.line).unwrap_or(u32::MAX),
            column: u32::try_from(column).unwrap_or(u32::MAX),
        }
    }

    fn error(&self, kind: JsonErrorKind) -> JsonError {
        JsonError {
            kind,
            at: self.here(),
        }
    }

    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.offset).copied()
    }

    fn skip_whitespace(&mut self) {
        while let Some(byte) = self.peek() {
            match byte {
                b' ' | b'\t' | b'\r' => self.offset += 1,
                b'\n' => {
                    self.offset += 1;
                    self.line += 1;
                    self.line_start = self.offset;
                }
                _ => break,
            }
        }
    }

    fn expect(&mut self, byte: u8, what: &'static str) -> Result<(), JsonError> {
        if self.peek() == Some(byte) {
            self.offset += 1;
            Ok(())
        } else {
            Err(self.error(JsonErrorKind::Syntax(what)))
        }
    }

    fn value(&mut self, depth: usize) -> Result<Node, JsonError> {
        let at = self.here();
        let value = match self.peek() {
            Some(b'{') => self.object(depth + 1)?,
            Some(b'[') => self.array(depth + 1)?,
            Some(b'"') => Value::String(self.string()?),
            Some(b't') => self.literal("true", Value::Bool(true))?,
            Some(b'f') => self.literal("false", Value::Bool(false))?,
            Some(b'n') => self.literal("null", Value::Null)?,
            Some(b'-' | b'0'..=b'9') => Value::Number(self.number()?),
            _ => return Err(self.error(JsonErrorKind::Syntax("expected a value"))),
        };
        Ok(Node { value, at })
    }

    fn literal(&mut self, word: &'static str, value: Value) -> Result<Value, JsonError> {
        if self.text[self.offset..].starts_with(word) {
            self.offset += word.len();
            Ok(value)
        } else {
            Err(self.error(JsonErrorKind::Syntax("expected a value")))
        }
    }

    fn object(&mut self, depth: usize) -> Result<Value, JsonError> {
        if depth > MAX_DEPTH {
            return Err(self.error(JsonErrorKind::TooDeep));
        }
        self.offset += 1;
        self.skip_whitespace();
        let mut members = Vec::new();
        let mut names = HashSet::new();
        if self.peek() == Some(b'}') {
            self.offset += 1;
            return Ok(Value::Object(members));
        }
        loop {
            let at = self.here();
            if self.peek() != Some(b'"') {
                return Err(self.error(JsonErrorKind::Syntax("expected a member name")));
            }
            let name = self.string()?;
            if !names.insert(name.clone()) {
                return Err(JsonError {
                    kind: JsonErrorKind::DuplicateMember,
                    at,
                });
            }
            self.skip_whitespace();
            self.expect(b':', "expected ':' after a member name")?;
            self.skip_whitespace();
            let value = self.value(depth)?;
            members.push(Member { name, value, at });
            self.skip_whitespace();
            match self.peek() {
                Some(b',') => {
                    self.offset += 1;
                    self.skip_whitespace();
                }
                Some(b'}') => {
                    self.offset += 1;
                    return Ok(Value::Object(members));
                }
                _ => return Err(self.error(JsonErrorKind::Syntax("expected ',' or '}'"))),
            }
        }
    }

    fn array(&mut self, depth: usize) -> Result<Value, JsonError> {
        if depth > MAX_DEPTH {
            return Err(self.error(JsonErrorKind::TooDeep));
        }
        self.offset += 1;
        self.skip_whitespace();
        let mut items = Vec::new();
        if self.peek() == Some(b']') {
            self.offset += 1;
            return Ok(Value::Array(items));
        }
        loop {
            items.push(self.value(depth)?);
            self.skip_whitespace();
            match self.peek() {
                Some(b',') => {
                    self.offset += 1;
                    self.skip_whitespace();
                }
                Some(b']') => {
                    self.offset += 1;
                    return Ok(Value::Array(items));
                }
                _ => return Err(self.error(JsonErrorKind::Syntax("expected ',' or ']'"))),
            }
        }
    }

    fn number(&mut self) -> Result<String, JsonError> {
        let start = self.offset;
        if self.peek() == Some(b'-') {
            self.offset += 1;
        }
        match self.peek() {
            Some(b'0') => self.offset += 1,
            Some(b'1'..=b'9') => self.digits(),
            _ => return Err(self.error(JsonErrorKind::Syntax("expected a digit"))),
        }
        if self.peek() == Some(b'.') {
            self.offset += 1;
            if !matches!(self.peek(), Some(b'0'..=b'9')) {
                return Err(self.error(JsonErrorKind::Syntax("expected a digit")));
            }
            self.digits();
        }
        if matches!(self.peek(), Some(b'e' | b'E')) {
            self.offset += 1;
            if matches!(self.peek(), Some(b'+' | b'-')) {
                self.offset += 1;
            }
            if !matches!(self.peek(), Some(b'0'..=b'9')) {
                return Err(self.error(JsonErrorKind::Syntax("expected a digit")));
            }
            self.digits();
        }
        if matches!(self.peek(), Some(b'0'..=b'9')) {
            // A leading zero followed by more digits.
            return Err(self.error(JsonErrorKind::Syntax("a number has a leading zero")));
        }
        Ok(self.text[start..self.offset].to_owned())
    }

    fn digits(&mut self) {
        while matches!(self.peek(), Some(b'0'..=b'9')) {
            self.offset += 1;
        }
    }

    fn string(&mut self) -> Result<String, JsonError> {
        self.offset += 1;
        let mut out = String::new();
        let mut run_start = self.offset;
        loop {
            let Some(byte) = self.peek() else {
                return Err(self.error(JsonErrorKind::Syntax("a string is not closed")));
            };
            match byte {
                b'"' => {
                    out.push_str(&self.text[run_start..self.offset]);
                    self.offset += 1;
                    return Ok(out);
                }
                b'\\' => {
                    out.push_str(&self.text[run_start..self.offset]);
                    self.offset += 1;
                    self.escape(&mut out)?;
                    run_start = self.offset;
                }
                0x00..=0x1F => {
                    return Err(self.error(JsonErrorKind::Syntax(
                        "a control character must be escaped inside a string",
                    )));
                }
                _ => self.offset += 1,
            }
        }
    }

    fn escape(&mut self, out: &mut String) -> Result<(), JsonError> {
        let Some(byte) = self.peek() else {
            return Err(self.error(JsonErrorKind::Syntax("a string is not closed")));
        };
        self.offset += 1;
        let simple = match byte {
            b'"' => '"',
            b'\\' => '\\',
            b'/' => '/',
            b'b' => '\u{8}',
            b'f' => '\u{c}',
            b'n' => '\n',
            b'r' => '\r',
            b't' => '\t',
            b'u' => {
                let unit = self.hex4()?;
                let code = match unit {
                    0xD800..=0xDBFF => {
                        if !self.text[self.offset..].starts_with("\\u") {
                            return Err(self.error(JsonErrorKind::Syntax(
                                "a high surrogate is not followed by a low surrogate",
                            )));
                        }
                        self.offset += 2;
                        let low = self.hex4()?;
                        if !(0xDC00..=0xDFFF).contains(&low) {
                            return Err(self.error(JsonErrorKind::Syntax(
                                "a high surrogate is not followed by a low surrogate",
                            )));
                        }
                        0x10000 + ((u32::from(unit) - 0xD800) << 10) + (u32::from(low) - 0xDC00)
                    }
                    0xDC00..=0xDFFF => {
                        return Err(self.error(JsonErrorKind::Syntax(
                            "a low surrogate has no high surrogate",
                        )));
                    }
                    other => u32::from(other),
                };
                let Some(character) = char::from_u32(code) else {
                    return Err(self.error(JsonErrorKind::Syntax("an escape is not a character")));
                };
                out.push(character);
                return Ok(());
            }
            _ => return Err(self.error(JsonErrorKind::Syntax("an escape sequence is not valid"))),
        };
        out.push(simple);
        Ok(())
    }

    fn hex4(&mut self) -> Result<u16, JsonError> {
        let digits = self.bytes.get(self.offset..self.offset + 4);
        let value = digits.and_then(|digits| {
            digits.iter().try_fold(0u16, |acc, &digit| {
                let nibble = char::from(digit).to_digit(16)?;
                Some(acc << 4 | u16::try_from(nibble).ok()?)
            })
        });
        match value {
            Some(value) => {
                self.offset += 4;
                Ok(value)
            }
            None => Err(self.error(JsonErrorKind::Syntax("expected four hex digits"))),
        }
    }
}

/// Reads the members of one object, refusing any member nobody asked for.
#[derive(Debug)]
pub struct ObjectReader<'a> {
    members: &'a [Member],
    at: Position,
    used: Vec<bool>,
}

impl<'a> ObjectReader<'a> {
    /// Starts reading `node`, which must be an object.
    pub fn new(node: &'a Node) -> Result<Self, JsonError> {
        match &node.value {
            Value::Object(members) => Ok(Self {
                members,
                at: node.at,
                used: vec![false; members.len()],
            }),
            _ => Err(wrong_type(node, "an object")),
        }
    }

    /// The member called `name`, if present.
    pub fn take(&mut self, name: &str) -> Option<&'a Node> {
        let index = self.members.iter().position(|member| member.name == name)?;
        self.used[index] = true;
        Some(&self.members[index].value)
    }

    /// The member called `name`; missing is an error.
    pub fn required(&mut self, name: &'static str) -> Result<&'a Node, JsonError> {
        self.take(name).ok_or(JsonError {
            kind: JsonErrorKind::MissingMember(name),
            at: self.at,
        })
    }

    /// Ends the read; any member not taken is an error.
    pub fn finish(self) -> Result<(), JsonError> {
        match self.used.iter().position(|used| !used) {
            Some(index) => Err(JsonError {
                kind: JsonErrorKind::UnknownMember,
                at: self.members[index].at,
            }),
            None => Ok(()),
        }
    }
}

/// The members of an object read as a map: any names, document order.
pub fn object_members(node: &Node) -> Result<&[Member], JsonError> {
    match &node.value {
        Value::Object(members) => Ok(members),
        _ => Err(wrong_type(node, "an object")),
    }
}

/// The items of an array.
pub fn array_items(node: &Node) -> Result<&[Node], JsonError> {
    match &node.value {
        Value::Array(items) => Ok(items),
        _ => Err(wrong_type(node, "an array")),
    }
}

/// True when the value is `null`.
pub fn is_null(node: &Node) -> bool {
    matches!(node.value, Value::Null)
}

/// A string, or `None` for `null`.
pub fn string_or_null(node: &Node) -> Result<Option<String>, JsonError> {
    match &node.value {
        Value::String(text) => Ok(Some(text.clone())),
        Value::Null => Ok(None),
        _ => Err(wrong_type(node, "a string")),
    }
}

/// `true` or `false`.
pub fn boolean(node: &Node) -> Result<bool, JsonError> {
    match node.value {
        Value::Bool(value) => Ok(value),
        _ => Err(wrong_type(node, "true or false")),
    }
}

/// A 32-bit signed integer written without fraction or exponent.
pub fn int32(node: &Node) -> Result<i32, JsonError> {
    integer_text(node)?.parse::<i32>().map_err(|_| JsonError {
        kind: JsonErrorKind::OutOfRange,
        at: node.at,
    })
}

/// A 32-bit unsigned integer written without fraction or exponent.
pub fn uint32(node: &Node) -> Result<u32, JsonError> {
    integer_text(node)?.parse::<u32>().map_err(|_| JsonError {
        kind: JsonErrorKind::OutOfRange,
        at: node.at,
    })
}

fn integer_text(node: &Node) -> Result<&str, JsonError> {
    match &node.value {
        Value::Number(text) if !text.contains(['.', 'e', 'E']) => Ok(text),
        Value::Number(_) => Err(JsonError {
            kind: JsonErrorKind::OutOfRange,
            at: node.at,
        }),
        _ => Err(wrong_type(node, "an integer")),
    }
}

/// One of `names`, matched ignoring ASCII case; numbers are not accepted.
pub fn enumeration<T: Copy>(node: &Node, names: &[(&str, T)]) -> Result<T, JsonError> {
    let Value::String(text) = &node.value else {
        return Err(wrong_type(node, "a name"));
    };
    names
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case(text))
        .map(|&(_, value)| value)
        .ok_or(JsonError {
            kind: JsonErrorKind::UnknownEnumValue,
            at: node.at,
        })
}

fn wrong_type(node: &Node, expected: &'static str) -> JsonError {
    JsonError {
        kind: JsonErrorKind::WrongType(expected),
        at: node.at,
    }
}

/// Appends `text` as a quoted JSON string. Besides the characters JSON
/// requires escaped, everything outside printable ASCII and the characters
/// `& ' < > + `` ` are written as `\uXXXX`, so a line is plain ASCII and safe
/// to paste anywhere.
pub fn write_string(out: &mut String, text: &str) {
    use fmt::Write as _;
    out.push('"');
    for character in text.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            '&' | '\'' | '<' | '>' | '+' | '`' => {
                let _ = write!(out, "\\u{:04X}", u32::from(character));
            }
            ' '..='~' => out.push(character),
            _ => {
                let mut units = [0u16; 2];
                for unit in character.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{unit:04X}");
                }
            }
        }
    }
    out.push('"');
}

/// Appends `node` as indented JSON (two spaces per level, starting at
/// `indent` levels): what was read is written back with the same values
/// (numbers exactly as they were written).
pub fn write_value(out: &mut String, node: &Node, indent: usize) {
    let pad = |out: &mut String, level: usize| {
        for _ in 0..level {
            out.push_str("  ");
        }
    };
    match &node.value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(raw) => out.push_str(raw),
        Value::String(text) => write_string(out, text),
        Value::Array(items) if items.is_empty() => out.push_str("[]"),
        Value::Array(items) => {
            out.push_str("[\n");
            for (i, item) in items.iter().enumerate() {
                pad(out, indent + 1);
                write_value(out, item, indent + 1);
                out.push_str(if i + 1 < items.len() { ",\n" } else { "\n" });
            }
            pad(out, indent);
            out.push(']');
        }
        Value::Object(members) if members.is_empty() => out.push_str("{}"),
        Value::Object(members) => {
            out.push_str("{\n");
            for (i, member) in members.iter().enumerate() {
                pad(out, indent + 1);
                write_string(out, &member.name);
                out.push_str(": ");
                write_value(out, &member.value, indent + 1);
                out.push_str(if i + 1 < members.len() { ",\n" } else { "\n" });
            }
            pad(out, indent);
            out.push('}');
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn written_values_read_back_the_same() {
        let text = br#"{"a": [1, -2.5e3, true, null, "xy\n", []], "b": {"c": {}, "d": 0.50}}"#;
        let node = parse(text).unwrap();
        let mut out = String::new();
        write_value(&mut out, &node, 0);
        let again = parse(out.as_bytes()).unwrap();
        let strip = |n: &Node| {
            let mut s = String::new();
            write_value(&mut s, n, 0);
            s
        };
        assert_eq!(strip(&again), out);
        assert!(out.contains("0.50"), "numbers keep their written form");
        assert!(out.starts_with("{\n  \"a\": [\n    1,"));
    }

    fn kind(text: &str) -> JsonErrorKind {
        parse(text.as_bytes()).unwrap_err().kind
    }

    #[test]
    fn accepts_plain_documents() {
        let node = parse(br#" {"a": [1, -2.5e3, true, null, "x\u00e5\n"]} "#).unwrap();
        let Value::Object(members) = node.value else {
            panic!("object expected");
        };
        assert_eq!(members[0].name, "a");
        let items = array_items(&members[0].value).unwrap();
        assert_eq!(items.len(), 5);
        assert_eq!(items[4].value, Value::String("x\u{e5}\n".into()));
    }

    #[test]
    fn refuses_what_strict_json_refuses() {
        assert_eq!(kind(""), JsonErrorKind::Empty);
        assert_eq!(kind("  "), JsonErrorKind::Empty);
        assert!(matches!(kind("{\"a\":1,}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("[1,]"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("{\"a\":1} // c"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("/* c */ {}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("{\"a\":01}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("{\"a\":\"\t\"}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("{'a':1}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("{\"a\":1} {}"), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("\"\\ud800\""), JsonErrorKind::Syntax(_)));
        assert!(matches!(kind("\"\\udc00\""), JsonErrorKind::Syntax(_)));
        assert_eq!(kind("{\"a\":1,\"a\":2}"), JsonErrorKind::DuplicateMember);
        assert_eq!(parse(b"\xff").unwrap_err().kind, JsonErrorKind::NotUtf8);
    }

    #[test]
    fn depth_is_capped() {
        let ok = format!("{}{}", "[".repeat(MAX_DEPTH), "]".repeat(MAX_DEPTH));
        assert!(parse(ok.as_bytes()).is_ok());
        let deep = format!("{}{}", "[".repeat(MAX_DEPTH + 1), "]".repeat(MAX_DEPTH + 1));
        assert_eq!(kind(&deep), JsonErrorKind::TooDeep);
        // Far deeper input fails the same way without exhausting the stack.
        let hostile = "[".repeat(100_000);
        assert_eq!(kind(&hostile), JsonErrorKind::TooDeep);
    }

    #[test]
    fn byte_order_mark_is_skipped() {
        assert!(parse(b"\xef\xbb\xbf{}").is_ok());
    }

    #[test]
    fn positions_are_line_and_column() {
        let error = parse(b"{\n  \"a\": 1,\n  \"a\": 2\n}").unwrap_err();
        assert_eq!(error.at, Position { line: 3, column: 3 });
        assert_eq!(
            error.to_string(),
            "line 3, column 3: a member name is repeated"
        );
    }

    #[test]
    fn surrogate_pairs_decode() {
        let node = parse(br#""\ud83d\ude00""#).unwrap();
        assert_eq!(node.value, Value::String("\u{1F600}".into()));
    }

    #[test]
    fn integers_refuse_fractions_and_overflow() {
        let read = |text: &str| int32(&parse(text.as_bytes()).unwrap());
        assert_eq!(read("-12"), Ok(-12));
        assert!(read("1.0").is_err());
        assert!(read("1e2").is_err());
        assert!(read("2147483648").is_err());
        assert!(read("\"1\"").is_err());
        assert!(uint32(&parse(b"-1").unwrap()).is_err());
        assert_eq!(uint32(&parse(b"4294967295").unwrap()), Ok(u32::MAX));
    }

    #[test]
    fn escaping_matches_the_default_web_safe_encoder() {
        let mut out = String::new();
        write_string(&mut out, "a\"b\\c\n<&'>+`\u{7f}\u{e5}\u{1F600}");
        assert_eq!(
            out,
            "\"a\\\"b\\\\c\\n\\u003C\\u0026\\u0027\\u003E\\u002B\\u0060\\u007F\\u00E5\\uD83D\\uDE00\""
        );
    }
}
