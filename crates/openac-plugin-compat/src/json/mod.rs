//! JSON as the plugins' two .NET libraries write and read it.
//!
//! - [`newtonsoft`]: Newtonsoft.Json 13 output (MosswartMassacre's backend
//!   frames, settings and inventory cache), with modern or .NET Framework
//!   number text.
//! - [`system_text_json`]: System.Text.Json output (MossTank's side-cars,
//!   stores and global variables), and its camel-case naming policy.
//! - [`parse`]: a strict reader for what the plugins receive and load.
//!
//! A document is a [`JsonValue`] tree whose objects keep their members in
//! order, so the bytes follow the C# declaration order. The writers are the
//! kit's own rather than `serde_json` with a formatter: both libraries'
//! escaping and number text differ from `serde_json`'s in nearly every rule,
//! and the plugins build their frames field by field anyway.

mod read;
mod write;

pub use read::{JsonParseError, parse, parse_with_max_depth};
pub use write::{JsonWriteError, newtonsoft, system_text_json};

use crate::number::{self, NumberStyles};

/// A JSON number: as the program built it, or as the reader found it.
#[derive(Debug, Clone, PartialEq)]
pub enum JsonNumber {
    /// A signed integer (`int`, `long`, `short`, ...).
    I64(i64),
    /// An unsigned integer (`uint`, `ulong`, `byte`, ...).
    U64(u64),
    /// A `double`.
    F64(f64),
    /// A `float`.
    F32(f32),
    /// Text read from a document, kept as written.
    Text(String),
}

/// A JSON document or part of one.
#[derive(Debug, Clone, PartialEq)]
pub enum JsonValue {
    /// `null`.
    Null,
    /// `true` or `false`.
    Bool(bool),
    /// A number.
    Number(JsonNumber),
    /// A string.
    String(String),
    /// An array.
    Array(Vec<JsonValue>),
    /// An object; members in order, duplicates kept as read.
    Object(Vec<(String, JsonValue)>),
}

/// A value that does not convert the way a Newtonsoft `JToken` cast would.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum JsonConversionError {
    /// The token is an array or object (`ArgumentException`).
    #[error("cannot convert a JSON {0} to the requested type")]
    WrongType(&'static str),
    /// The text is not a number (`FormatException`).
    #[error("{0:?} is not a number")]
    NotANumber(String),
    /// The value does not fit (`OverflowException`).
    #[error("{0} does not fit the requested type")]
    Overflow(String),
}

impl JsonValue {
    /// An object from `(name, value)` pairs, in order.
    pub fn object<K: Into<String>>(members: impl IntoIterator<Item = (K, JsonValue)>) -> Self {
        Self::Object(members.into_iter().map(|(k, v)| (k.into(), v)).collect())
    }

    /// An array from values.
    pub fn array(items: impl IntoIterator<Item = JsonValue>) -> Self {
        Self::Array(items.into_iter().collect())
    }

    /// The last member of an object named exactly `name` (later duplicates
    /// win, as both libraries bind them).
    pub fn get(&self, name: &str) -> Option<&JsonValue> {
        match self {
            Self::Object(members) => members
                .iter()
                .rev()
                .find(|(k, _)| k == name)
                .map(|(_, v)| v),
            _ => None,
        }
    }

    /// The last member whose name matches ignoring case, as System.Text.Json's
    /// `PropertyNameCaseInsensitive` binds (`StringComparison.OrdinalIgnoreCase`).
    pub fn get_ignore_case(&self, name: &str) -> Option<&JsonValue> {
        match self {
            Self::Object(members) => members
                .iter()
                .rev()
                .find(|(k, _)| ordinal_ignore_case_eq(k, name))
                .map(|(_, v)| v),
            _ => None,
        }
    }

    /// Whether this is `null`.
    pub fn is_null(&self) -> bool {
        matches!(self, Self::Null)
    }

    /// The string, if this is one.
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Self::String(s) => Some(s),
            _ => None,
        }
    }

    /// The boolean, if this is one.
    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Self::Bool(b) => Some(*b),
            _ => None,
        }
    }

    /// The elements, if this is an array.
    pub fn as_array(&self) -> Option<&[JsonValue]> {
        match self {
            Self::Array(items) => Some(items),
            _ => None,
        }
    }

    /// The members, if this is an object.
    pub fn as_object(&self) -> Option<&[(String, JsonValue)]> {
        match self {
            Self::Object(members) => Some(members),
            _ => None,
        }
    }

    /// A number as a `double` (any number; read text is correctly rounded).
    pub fn as_f64(&self) -> Option<f64> {
        match self {
            Self::Number(n) => Some(n.to_f64()),
            _ => None,
        }
    }

    /// A number written as an integer that fits `i64` (System.Text.Json's
    /// `GetInt64`: `1.0` is not an integer).
    pub fn as_i64(&self) -> Option<i64> {
        match self {
            Self::Number(JsonNumber::I64(v)) => Some(*v),
            Self::Number(JsonNumber::U64(v)) => i64::try_from(*v).ok(),
            Self::Number(JsonNumber::Text(t)) => t.parse().ok(),
            _ => None,
        }
    }

    /// `(int?)token` in Newtonsoft.Json: `None` for null; an integer that
    /// fits; a floating-point number rounded half to even
    /// (`Convert.ToInt32(double)`); a string parsed as an integer; `true` as 1.
    pub fn newtonsoft_i32(&self) -> Result<Option<i32>, JsonConversionError> {
        match self {
            Self::Null => Ok(None),
            Self::Bool(b) => Ok(Some(i32::from(*b))),
            Self::String(s) => number::parse_i32(s, NumberStyles::INTEGER)
                .map(Some)
                .ok_or_else(|| JsonConversionError::NotANumber(s.clone())),
            Self::Number(n) => match n.newtonsoft_kind() {
                NewtonsoftNumber::Integer(v) => i32::try_from(v)
                    .map(Some)
                    .map_err(|_| JsonConversionError::Overflow(v.to_string())),
                NewtonsoftNumber::Float(v) => number::convert_to_int32(v)
                    .map(Some)
                    .ok_or_else(|| JsonConversionError::Overflow(number::to_string_f64(v))),
                NewtonsoftNumber::Big(t) => Err(JsonConversionError::Overflow(t)),
            },
            Self::Array(_) => Err(JsonConversionError::WrongType("array")),
            Self::Object(_) => Err(JsonConversionError::WrongType("object")),
        }
    }

    /// `(double?)token` in Newtonsoft.Json: `None` for null; any number; a
    /// string parsed with `NumberStyles.Float | AllowThousands`; `true` as 1.
    pub fn newtonsoft_f64(&self) -> Result<Option<f64>, JsonConversionError> {
        match self {
            Self::Null => Ok(None),
            Self::Bool(b) => Ok(Some(if *b { 1.0 } else { 0.0 })),
            Self::String(s) => {
                number::parse_f64(s, NumberStyles::FLOAT | NumberStyles::ALLOW_THOUSANDS)
                    .map(Some)
                    .ok_or_else(|| JsonConversionError::NotANumber(s.clone()))
            }
            // "-0" is the integer 0 to Newtonsoft, so it converts to +0.0.
            Self::Number(n) => Ok(Some(match n.newtonsoft_kind() {
                NewtonsoftNumber::Integer(v) => v as f64,
                NewtonsoftNumber::Float(v) => v,
                // `(double)BigInteger` truncates instead of rounding.
                NewtonsoftNumber::Big(t) => number::digits::truncating_integer_to_f64(&t),
            })),
            Self::Array(_) => Err(JsonConversionError::WrongType("array")),
            Self::Object(_) => Err(JsonConversionError::WrongType("object")),
        }
    }

    /// `(string?)token` in Newtonsoft.Json: `None` for null; a string as is;
    /// a number in its invariant text; `True`/`False` for booleans.
    pub fn newtonsoft_string(&self) -> Result<Option<String>, JsonConversionError> {
        match self {
            Self::Null => Ok(None),
            Self::Bool(b) => Ok(Some(if *b { "True" } else { "False" }.to_owned())),
            Self::String(s) => Ok(Some(s.clone())),
            Self::Number(n) => Ok(Some(match n.newtonsoft_kind() {
                NewtonsoftNumber::Integer(v) => v.to_string(),
                NewtonsoftNumber::Float(v) => number::to_string_f64(v),
                NewtonsoftNumber::Big(t) => t,
            })),
            Self::Array(_) => Err(JsonConversionError::WrongType("array")),
            Self::Object(_) => Err(JsonConversionError::WrongType("object")),
        }
    }
}

/// How Newtonsoft holds a number: `long`, `double`, or a `BigInteger` for
/// integers beyond `long`.
pub(crate) enum NewtonsoftNumber {
    Integer(i128),
    Float(f64),
    Big(String),
}

impl JsonNumber {
    /// The value as a `double`.
    pub fn to_f64(&self) -> f64 {
        match self {
            Self::I64(v) => *v as f64,
            Self::U64(v) => *v as f64,
            Self::F64(v) => *v,
            Self::F32(v) => f64::from(*v),
            Self::Text(t) => t.parse().unwrap_or(f64::NAN),
        }
    }

    /// The token Newtonsoft's reader makes of the number: an integer literal
    /// is a `long` (or a `BigInteger` beyond it), anything else a `double`.
    pub(crate) fn newtonsoft_kind(&self) -> NewtonsoftNumber {
        match self {
            Self::I64(v) => NewtonsoftNumber::Integer(i128::from(*v)),
            Self::U64(v) => NewtonsoftNumber::Integer(i128::from(*v)),
            Self::F64(v) => NewtonsoftNumber::Float(*v),
            Self::F32(v) => NewtonsoftNumber::Float(f64::from(*v)),
            Self::Text(t) => {
                if t.contains(['.', 'e', 'E']) {
                    NewtonsoftNumber::Float(t.parse().unwrap_or(f64::NAN))
                } else if let Ok(v) = t.parse::<i64>() {
                    NewtonsoftNumber::Integer(i128::from(v))
                } else {
                    NewtonsoftNumber::Big(t.trim_start_matches('+').to_owned())
                }
            }
        }
    }
}

/// `StringComparison.OrdinalIgnoreCase`: equal after mapping each character
/// to upper case (simple case mapping).
fn ordinal_ignore_case_eq(a: &str, b: &str) -> bool {
    fn upper(c: char) -> char {
        let mut it = c.to_uppercase();
        match (it.next(), it.next()) {
            (Some(u), None) => u,
            _ => c,
        }
    }
    a.chars().count() == b.chars().count()
        && a.chars().zip(b.chars()).all(|(x, y)| upper(x) == upper(y))
}

macro_rules! from_number {
    ($($ty:ty => $variant:ident),*) => {
        $(impl From<$ty> for JsonValue {
            fn from(v: $ty) -> Self {
                Self::Number(JsonNumber::$variant(v.into()))
            }
        })*
    };
}

from_number!(i8 => I64, i16 => I64, i32 => I64, i64 => I64, u8 => U64, u16 => U64, u32 => U64, u64 => U64, f64 => F64, f32 => F32);

impl From<bool> for JsonValue {
    fn from(v: bool) -> Self {
        Self::Bool(v)
    }
}

impl From<&str> for JsonValue {
    fn from(v: &str) -> Self {
        Self::String(v.to_owned())
    }
}

impl From<String> for JsonValue {
    fn from(v: String) -> Self {
        Self::String(v)
    }
}

impl<T: Into<JsonValue>> From<Option<T>> for JsonValue {
    fn from(v: Option<T>) -> Self {
        v.map_or(Self::Null, Into::into)
    }
}

impl<T: Into<JsonValue>> From<Vec<T>> for JsonValue {
    fn from(v: Vec<T>) -> Self {
        Self::Array(v.into_iter().map(Into::into).collect())
    }
}
