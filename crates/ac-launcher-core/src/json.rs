//! JSON for the launcher's own files: reading on top of the host's strict
//! reader, and an indented writer whose layout matches the files OpenAC's
//! launcher writes (two-space indent, `"name": value`).

use std::collections::HashSet;

use ac_host_core::json::{self, JsonError, JsonErrorKind, Node, Value};

/// A JSON value to write. Members keep the order they were added in.
#[derive(Debug, Clone, PartialEq)]
pub enum Out {
    /// `null`.
    Null,
    /// `true` or `false`.
    Bool(bool),
    /// A signed integer.
    Int(i64),
    /// A string.
    Str(String),
    /// An array.
    Array(Vec<Out>),
    /// An object.
    Object(Vec<(String, Out)>),
}

impl Out {
    /// A string value.
    pub fn str(text: impl Into<String>) -> Self {
        Self::Str(text.into())
    }

    /// An array of strings.
    pub fn strings<I, S>(items: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        Self::Array(
            items
                .into_iter()
                .map(|item| Self::Str(item.into()))
                .collect(),
        )
    }
}

/// Builds an object member by member.
#[derive(Debug, Default)]
pub struct ObjectBuilder(Vec<(String, Out)>);

impl ObjectBuilder {
    /// An empty object.
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds a member.
    #[must_use]
    pub fn member(mut self, name: &str, value: Out) -> Self {
        self.0.push((name.to_owned(), value));
        self
    }

    /// Adds a member only when `value` is present.
    #[must_use]
    pub fn optional(self, name: &str, value: Option<Out>) -> Self {
        match value {
            Some(value) => self.member(name, value),
            None => self,
        }
    }

    /// The finished object.
    pub fn build(self) -> Out {
        Out::Object(self.0)
    }
}

/// Writes `value` indented by two spaces per level, lines ending in `\n`.
pub fn to_pretty(value: &Out) -> String {
    let mut out = String::new();
    write_value(&mut out, value, 0);
    out
}

fn indent(out: &mut String, level: usize) {
    for _ in 0..level {
        out.push_str("  ");
    }
}

fn write_value(out: &mut String, value: &Out, level: usize) {
    match value {
        Out::Null => out.push_str("null"),
        Out::Bool(true) => out.push_str("true"),
        Out::Bool(false) => out.push_str("false"),
        Out::Int(number) => out.push_str(&number.to_string()),
        Out::Str(text) => json::write_string(out, text),
        Out::Array(items) if items.is_empty() => out.push_str("[]"),
        Out::Array(items) => {
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                out.push('\n');
                indent(out, level + 1);
                write_value(out, item, level + 1);
            }
            out.push('\n');
            indent(out, level);
            out.push(']');
        }
        Out::Object(members) if members.is_empty() => out.push_str("{}"),
        Out::Object(members) => {
            out.push('{');
            for (index, (name, item)) in members.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                out.push('\n');
                indent(out, level + 1);
                json::write_string(out, name);
                out.push_str(": ");
                write_value(out, item, level + 1);
            }
            out.push('\n');
            indent(out, level);
            out.push('}');
        }
    }
}

/// Writes `text` as one quoted JSON string.
pub fn quote(text: &str) -> String {
    let mut out = String::new();
    json::write_string(&mut out, text);
    out
}

// ---------------------------------------------------------------------------
// Reading.

fn error(node: &Node, kind: JsonErrorKind) -> JsonError {
    JsonError { kind, at: node.at }
}

/// A string; `null` and other types are refused.
pub fn string(node: &Node) -> Result<String, JsonError> {
    match &node.value {
        Value::String(text) => Ok(text.clone()),
        _ => Err(error(node, JsonErrorKind::WrongType("a string"))),
    }
}

/// A string or `null`.
pub fn string_or_null(node: &Node) -> Result<Option<String>, JsonError> {
    json::string_or_null(node)
}

/// A signed 64-bit integer written without fraction or exponent.
pub fn int64(node: &Node) -> Result<i64, JsonError> {
    match &node.value {
        Value::Number(text) if !text.contains(['.', 'e', 'E']) => text
            .parse::<i64>()
            .map_err(|_| error(node, JsonErrorKind::OutOfRange)),
        Value::Number(_) => Err(error(node, JsonErrorKind::OutOfRange)),
        _ => Err(error(node, JsonErrorKind::WrongType("an integer"))),
    }
}

/// A list of strings; `null` items are refused.
pub fn string_list(node: &Node) -> Result<Vec<String>, JsonError> {
    json::array_items(node)?.iter().map(string).collect()
}

/// Refuses an object anywhere in the document that names the same member
/// twice when names are compared ignoring case.
pub fn reject_case_insensitive_duplicates(node: &Node) -> Result<(), JsonError> {
    match &node.value {
        Value::Object(members) => {
            let mut seen = HashSet::new();
            for member in members {
                if !seen.insert(member.name.to_lowercase()) {
                    return Err(JsonError {
                        kind: JsonErrorKind::DuplicateMember,
                        at: member.at,
                    });
                }
                reject_case_insensitive_duplicates(&member.value)?;
            }
            Ok(())
        }
        Value::Array(items) => items
            .iter()
            .try_for_each(reject_case_insensitive_duplicates),
        _ => Ok(()),
    }
}

/// How deep a document nests (a scalar is depth 0, `[]` is depth 1).
pub fn depth(node: &Node) -> usize {
    match &node.value {
        Value::Array(items) => 1 + items.iter().map(depth).max().unwrap_or(0),
        Value::Object(members) => {
            1 + members
                .iter()
                .map(|member| depth(&member.value))
                .max()
                .unwrap_or(0)
        }
        _ => 0,
    }
}

/// The members of an object with names matched ignoring case, for formats
/// whose OpenAC reader matched property names that way.
#[derive(Debug)]
pub struct CaseInsensitiveObject<'a> {
    members: &'a [ac_host_core::json::Member],
}

impl<'a> CaseInsensitiveObject<'a> {
    /// Starts reading `node`, which must be an object.
    pub fn new(node: &'a Node) -> Result<Self, JsonError> {
        Ok(Self {
            members: json::object_members(node)?,
        })
    }

    /// The member whose name equals `name` ignoring case.
    pub fn get(&self, name: &str) -> Option<&'a Node> {
        self.members
            .iter()
            .find(|member| member.name.eq_ignore_ascii_case(name))
            .map(|member| &member.value)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pretty_layout_matches_indented_writer() {
        let value = ObjectBuilder::new()
            .member("version", Out::Int(1))
            .member("servers", Out::Array(Vec::new()))
            .member(
                "list",
                Out::Array(vec![Out::str("a"), Out::Object(Vec::new())]),
            )
            .build();
        assert_eq!(
            to_pretty(&value),
            "{\n  \"version\": 1,\n  \"servers\": [],\n  \"list\": [\n    \"a\",\n    {}\n  ]\n}"
        );
    }

    #[test]
    fn duplicates_are_found_ignoring_case() {
        let node = json::parse(br#"{"a":{"Id":1,"id":2}}"#).unwrap();
        assert!(reject_case_insensitive_duplicates(&node).is_err());
        let node = json::parse(br#"{"a":[{"Id":1},{"id":2}]}"#).unwrap();
        assert!(reject_case_insensitive_duplicates(&node).is_ok());
    }

    #[test]
    fn int64_reads_large_sizes() {
        let node = json::parse(b"4294967296").unwrap();
        assert_eq!(int64(&node).unwrap(), 4_294_967_296);
        assert!(int64(&json::parse(b"1.5").unwrap()).is_err());
    }
}
