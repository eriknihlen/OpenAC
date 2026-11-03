//! The byte encoding every request, reply and event uses across the plugin
//! boundary: little-endian integers, `bool` as one byte (0 or 1), strings
//! and lists as a `u32` count followed by their items (strings as UTF-8),
//! `Option` as one byte (0 none, 1 some) followed by the value.
//!
//! Each type is written once with [`crate::wire_record!`]; the reader never
//! panics and refuses trailing bytes, a bad tag, a bad `bool` or invalid
//! UTF-8 with a [`DecodeError`].
//!
//! A value is read without a check per field ([`Wire::read`]): the
//! [`Reader`] keeps the first error and reads nothing after it, and the
//! error is looked at once, when the value is complete
//! ([`Wire::from_bytes`], [`Wire::decode`]). Decoding runs inside the
//! plugin, and a check after every field cost it about as much as the
//! reading itself.
//!
//! Every type also carries its [`Wire::SHAPE`]: a hash of its encoding
//! (each field's name and shape in order, each tag and variant), computed
//! by the compiler from the same definition, so no shape is written by
//! hand. [`crate::layout`] folds the shapes of everything that crosses the
//! boundary into the plugin API's layout fingerprint.

use std::fmt;

use crate::version::ApiVersion;

/// Why bytes could not be read as the expected value.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DecodeError {
    /// The bytes ended inside a value.
    Truncated,
    /// Bytes were left over after the value.
    Trailing(usize),
    /// A string was not UTF-8.
    InvalidUtf8,
    /// A `bool` or `Option` byte was neither 0 nor 1.
    InvalidFlag(u8),
    /// An enum or event tag this side does not know.
    UnknownTag {
        /// What was being read.
        what: &'static str,
        /// The tag.
        tag: u32,
    },
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Truncated => f.write_str("the bytes end inside a value"),
            Self::Trailing(n) => write!(f, "{n} bytes left over after the value"),
            Self::InvalidUtf8 => f.write_str("a string is not UTF-8"),
            Self::InvalidFlag(b) => write!(f, "flag byte {b} is neither 0 nor 1"),
            Self::UnknownTag { what, tag } => write!(f, "unknown {what} tag {tag}"),
        }
    }
}

impl std::error::Error for DecodeError {}

/// Appends values to a byte buffer.
#[derive(Debug, Default, Clone)]
pub struct Writer {
    bytes: Vec<u8>,
}

impl Writer {
    /// An empty buffer.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The bytes written so far.
    #[must_use]
    pub fn into_bytes(self) -> Vec<u8> {
        self.bytes
    }

    /// Raw bytes, as they are.
    pub fn put_raw(&mut self, bytes: &[u8]) {
        self.bytes.extend_from_slice(bytes);
    }

    /// A `u32` count. Counts above `u32::MAX` cannot cross the boundary
    /// (wasm32 memory is smaller), so they are clamped by the caller's types.
    pub fn put_count(&mut self, count: usize) {
        let count = u32::try_from(count).unwrap_or(u32::MAX);
        self.put_raw(&count.to_le_bytes());
    }
}

/// Reads values from a byte slice, front to back. A read the bytes cannot
/// satisfy records its error ([`Self::fail`]) and answers zeros; the first
/// error is kept and every read after it sees no bytes left.
#[derive(Debug, Clone)]
pub struct Reader<'a> {
    bytes: &'a [u8],
    error: Option<DecodeError>,
}

impl<'a> Reader<'a> {
    /// Reads `bytes`.
    #[must_use]
    pub fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, error: None }
    }

    /// The bytes are malformed: keeps `error` unless an earlier one is kept,
    /// and leaves no bytes to read, so whatever reads next ends at once.
    /// Out of line, so the path every read takes stays short.
    #[cold]
    #[inline(never)]
    pub fn fail(&mut self, error: DecodeError) {
        if self.error.is_none() {
            self.error = Some(error);
        }
        self.bytes = &[];
    }

    /// The first error met, if any.
    #[must_use]
    pub fn error(&self) -> Option<&DecodeError> {
        self.error.as_ref()
    }

    /// The next `n` bytes; none (and [`DecodeError::Truncated`] recorded)
    /// when fewer are left.
    #[inline]
    pub fn take(&mut self, n: usize) -> &'a [u8] {
        if let Some((head, rest)) = self.bytes.split_at_checked(n) {
            self.bytes = rest;
            head
        } else {
            self.fail(DecodeError::Truncated);
            &[]
        }
    }

    /// The next `N` bytes; zeros (and [`DecodeError::Truncated`] recorded)
    /// when fewer are left.
    #[inline]
    pub fn array<const N: usize>(&mut self) -> [u8; N] {
        if let Some((head, rest)) = self.bytes.split_first_chunk::<N>() {
            self.bytes = rest;
            *head
        } else {
            self.fail(DecodeError::Truncated);
            [0; N]
        }
    }

    /// A `u32` count.
    #[inline]
    pub fn count(&mut self) -> usize {
        // A count can never exceed the bytes left (each item is at least
        // one byte, except empty records, which carry no data anyway); a
        // larger one meets the end of the bytes and stops there.
        usize::try_from(u32::from_le_bytes(self.array())).unwrap_or(usize::MAX)
    }

    /// The first error met, or (none met) an error when bytes are left.
    ///
    /// # Errors
    /// The first error met; [`DecodeError::Trailing`].
    pub fn finish(self) -> Result<(), DecodeError> {
        if let Some(error) = self.error {
            return Err(error);
        }
        if self.bytes.is_empty() {
            Ok(())
        } else {
            Err(DecodeError::Trailing(self.bytes.len()))
        }
    }
}

/// Builds a [`Wire::SHAPE`] at compile time: FNV-1a (64-bit) over a kind
/// word, then names, tags and the shapes of the parts, names
/// length-prefixed so no two sequences read the same.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Shape(u64);

impl Shape {
    const OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
    const PRIME: u64 = 0x0000_0100_0000_01b3;

    /// A shape of the kind `kind` (`"record"`, `"u32"`, `"list"`, ...).
    #[must_use]
    pub const fn new(kind: &str) -> Self {
        Self(Self::OFFSET).name(kind)
    }

    const fn bytes(self, bytes: &[u8]) -> Self {
        let mut hash = self.0;
        let mut at = 0;
        while at < bytes.len() {
            hash ^= bytes[at] as u64;
            hash = hash.wrapping_mul(Self::PRIME);
            at += 1;
        }
        Self(hash)
    }

    /// Adds a name (a field's or a variant's).
    #[must_use]
    pub const fn name(self, name: &str) -> Self {
        self.bytes(&(name.len() as u64).to_le_bytes())
            .bytes(name.as_bytes())
    }

    /// Adds a number (a tag).
    #[must_use]
    pub const fn tag(self, tag: u64) -> Self {
        self.bytes(&tag.to_le_bytes())
    }

    /// Adds a part's shape (a field's type, a list's item, a payload).
    #[must_use]
    pub const fn of(self, shape: u64) -> Self {
        self.bytes(&shape.to_le_bytes())
    }

    /// The hash.
    #[must_use]
    pub const fn finish(self) -> u64 {
        self.0
    }
}

/// A value with a byte encoding.
pub trait Wire: Sized {
    /// A hash of the encoding's shape ([`Shape`]): it changes whenever a
    /// field, a variant or a tag of this type, or of anything inside it, is
    /// added, removed, renamed, reordered or retyped.
    const SHAPE: u64;

    /// Appends the value.
    fn encode(&self, w: &mut Writer);

    /// Reads one value without checking as it goes: bytes that do not
    /// read as the value are recorded in `r` ([`Reader::fail`]) and a
    /// stand-in comes back, which means nothing. [`Self::decode`] and
    /// [`Self::from_bytes`] look at the record once the value is read.
    fn read(r: &mut Reader<'_>) -> Self;

    /// `items` in a row (a list's items). Integers write the whole run
    /// at once: a list of bytes (a socket send, a stored file) is one copy,
    /// not a call per byte. The bytes are the same either way.
    #[doc(hidden)]
    fn encode_list(items: &[Self], w: &mut Writer) {
        for item in items {
            item.encode(w);
        }
    }

    /// `count` values in a row (a list's items). Types of a fixed size
    /// read the whole run at once.
    #[doc(hidden)]
    fn read_list(r: &mut Reader<'_>, count: usize) -> Vec<Self> {
        // Do not trust the count for the allocation: it is bounded by the
        // bytes actually present, and the first failed item ends the list.
        let mut items = Vec::with_capacity(count.min(1024));
        for _ in 0..count {
            let item = Self::read(r);
            if r.error.is_some() {
                break;
            }
            items.push(item);
        }
        items
    }

    /// Reads one value.
    ///
    /// # Errors
    /// Any [`DecodeError`], including one met earlier in `r`.
    fn decode(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let value = Self::read(r);
        match &r.error {
            Some(error) => Err(error.clone()),
            None => Ok(value),
        }
    }

    /// The value's bytes on their own.
    #[must_use]
    fn to_bytes(&self) -> Vec<u8> {
        let mut w = Writer::new();
        self.encode(&mut w);
        w.into_bytes()
    }

    /// Reads exactly one value from `bytes`.
    ///
    /// # Errors
    /// Any [`DecodeError`], including bytes left over.
    fn from_bytes(bytes: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(bytes);
        let value = Self::read(&mut r);
        r.finish()?;
        Ok(value)
    }

    /// Whether the type, or anything inside it, has a field or variant a
    /// later API minor version added (`since`): only then does its
    /// encoding at an older version ([`Self::encode_at`]) differ from
    /// [`Self::encode`].
    const VERSIONED: bool = false;

    /// The shape a plugin built for API `version` knows: without the
    /// fields and variants a later version added ([`Self::SHAPE`] when the
    /// type has none).
    #[must_use]
    fn shape_at(version: ApiVersion) -> u64 {
        let _ = version;
        Self::SHAPE
    }

    /// Appends the value as a plugin built for API `version` reads it:
    /// the fields a later version added are left out.
    fn encode_at(&self, version: ApiVersion, w: &mut Writer) {
        let _ = version;
        self.encode(w);
    }

    /// Reads one value a plugin built for API `version` wrote: the fields
    /// a later version added are not on the wire and read as their
    /// default.
    fn read_at(r: &mut Reader<'_>, version: ApiVersion) -> Self {
        let _ = version;
        Self::read(r)
    }

    /// The value's bytes as a plugin built for API `version` reads them
    /// ([`Self::encode_at`]).
    #[must_use]
    fn to_bytes_at(&self, version: ApiVersion) -> Vec<u8> {
        if !Self::VERSIONED {
            return self.to_bytes();
        }
        let mut w = Writer::new();
        self.encode_at(version, &mut w);
        w.into_bytes()
    }

    /// Reads exactly one value a plugin built for API `version` wrote
    /// ([`Self::read_at`]).
    ///
    /// # Errors
    /// Any [`DecodeError`], including bytes left over.
    fn from_bytes_at(bytes: &[u8], version: ApiVersion) -> Result<Self, DecodeError> {
        if !Self::VERSIONED {
            return Self::from_bytes(bytes);
        }
        let mut r = Reader::new(bytes);
        let value = Self::read_at(&mut r, version);
        r.finish()?;
        Ok(value)
    }
}

/// `count` values of a versioned type in a row, as written at `version`
/// (a list's items; bounded as [`Wire::read_list`] is).
fn read_list_at<T: Wire>(r: &mut Reader<'_>, count: usize, version: ApiVersion) -> Vec<T> {
    let mut items = Vec::with_capacity(count.min(1024));
    for _ in 0..count {
        let item = T::read_at(r, version);
        if r.error.is_some() {
            break;
        }
        items.push(item);
    }
    items
}

macro_rules! wire_int {
    ($($t:ty),*) => {$(
        impl Wire for $t {
            const SHAPE: u64 = Shape::new(stringify!($t)).finish();
            fn encode(&self, w: &mut Writer) {
                w.put_raw(&self.to_le_bytes());
            }
            #[inline]
            fn read(r: &mut Reader<'_>) -> Self {
                <$t>::from_le_bytes(r.array())
            }
            fn read_list(r: &mut Reader<'_>, count: usize) -> Vec<Self> {
                const SIZE: usize = std::mem::size_of::<$t>();
                <$t as ListEncoding>::get(r.take(count.saturating_mul(SIZE)))
            }
            fn encode_list(items: &[Self], w: &mut Writer) {
                <$t as ListEncoding>::put(items, w);
            }
        }
    )*};
}

wire_int!(u8, u16, u32, u64, i32, i64, f32, f64);

/// How a run of integers is written: bytes as they are, wider integers
/// little-endian one after another, and read back the same way: a list of
/// bytes is one copy, not a step per byte.
trait ListEncoding: Sized {
    fn put(items: &[Self], w: &mut Writer);
    fn get(bytes: &[u8]) -> Vec<Self>;
}

impl ListEncoding for u8 {
    fn put(items: &[Self], w: &mut Writer) {
        w.put_raw(items);
    }
    fn get(bytes: &[u8]) -> Vec<Self> {
        bytes.to_vec()
    }
}

macro_rules! list_le {
    ($($t:ty),*) => {$(
        impl ListEncoding for $t {
            fn put(items: &[Self], w: &mut Writer) {
                w.bytes.reserve(items.len() * std::mem::size_of::<$t>());
                for item in items {
                    w.bytes.extend_from_slice(&item.to_le_bytes());
                }
            }
            fn get(bytes: &[u8]) -> Vec<Self> {
                const SIZE: usize = std::mem::size_of::<$t>();
                let (items, _) = bytes.as_chunks::<SIZE>();
                items.iter().map(|b| <$t>::from_le_bytes(*b)).collect()
            }
        }
    )*};
}

list_le!(u16, u32, u64, i32, i64, f32, f64);

impl Wire for bool {
    const SHAPE: u64 = Shape::new("bool").finish();
    fn encode(&self, w: &mut Writer) {
        w.put_raw(&[u8::from(*self)]);
    }
    #[inline]
    fn read(r: &mut Reader<'_>) -> Self {
        match u8::read(r) {
            0 => false,
            1 => true,
            other => {
                r.fail(DecodeError::InvalidFlag(other));
                false
            }
        }
    }
}

impl Wire for String {
    const SHAPE: u64 = Shape::new("string").finish();
    fn encode(&self, w: &mut Writer) {
        w.put_count(self.len());
        w.put_raw(self.as_bytes());
    }
    fn read(r: &mut Reader<'_>) -> Self {
        let len = r.count();
        let bytes = r.take(len);
        if let Ok(text) = std::str::from_utf8(bytes) {
            text.to_owned()
        } else {
            r.fail(DecodeError::InvalidUtf8);
            Self::new()
        }
    }
}

impl<T: Wire> Wire for Vec<T> {
    const SHAPE: u64 = Shape::new("list").of(T::SHAPE).finish();
    fn encode(&self, w: &mut Writer) {
        w.put_count(self.len());
        T::encode_list(self, w);
    }
    fn read(r: &mut Reader<'_>) -> Self {
        let count = r.count();
        T::read_list(r, count)
    }
    const VERSIONED: bool = T::VERSIONED;
    fn shape_at(version: ApiVersion) -> u64 {
        Shape::new("list").of(T::shape_at(version)).finish()
    }
    fn encode_at(&self, version: ApiVersion, w: &mut Writer) {
        if !T::VERSIONED {
            return self.encode(w);
        }
        w.put_count(self.len());
        for item in self {
            item.encode_at(version, w);
        }
    }
    fn read_at(r: &mut Reader<'_>, version: ApiVersion) -> Self {
        if !T::VERSIONED {
            return Self::read(r);
        }
        let count = r.count();
        read_list_at(r, count, version)
    }
}

impl<T: Wire> Wire for Option<T> {
    const SHAPE: u64 = Shape::new("option").of(T::SHAPE).finish();
    fn encode(&self, w: &mut Writer) {
        match self {
            None => w.put_raw(&[0]),
            Some(value) => {
                w.put_raw(&[1]);
                value.encode(w);
            }
        }
    }
    fn read(r: &mut Reader<'_>) -> Self {
        match u8::read(r) {
            0 => None,
            1 => Some(T::read(r)),
            other => {
                r.fail(DecodeError::InvalidFlag(other));
                None
            }
        }
    }
    const VERSIONED: bool = T::VERSIONED;
    fn shape_at(version: ApiVersion) -> u64 {
        Shape::new("option").of(T::shape_at(version)).finish()
    }
    fn encode_at(&self, version: ApiVersion, w: &mut Writer) {
        match self {
            None => w.put_raw(&[0]),
            Some(value) => {
                w.put_raw(&[1]);
                value.encode_at(version, w);
            }
        }
    }
    fn read_at(r: &mut Reader<'_>, version: ApiVersion) -> Self {
        match u8::read(r) {
            0 => None,
            1 => Some(T::read_at(r, version)),
            other => {
                r.fail(DecodeError::InvalidFlag(other));
                None
            }
        }
    }
}

impl Wire for () {
    const SHAPE: u64 = Shape::new("unit").finish();
    fn encode(&self, _w: &mut Writer) {}
    fn read(_r: &mut Reader<'_>) -> Self {}
}

/// `Result<T, E>`: tag 0 then the value, or tag 1 then the error (most
/// often a message, `E = String`).
impl<T: Wire, E: Wire> Wire for Result<T, E> {
    const SHAPE: u64 = Shape::new("result").of(T::SHAPE).of(E::SHAPE).finish();
    fn encode(&self, w: &mut Writer) {
        match self {
            Ok(value) => {
                w.put_raw(&[0]);
                value.encode(w);
            }
            Err(error) => {
                w.put_raw(&[1]);
                error.encode(w);
            }
        }
    }
    fn read(r: &mut Reader<'_>) -> Self {
        match u8::read(r) {
            0 => Ok(T::read(r)),
            1 => Err(E::read(r)),
            other => {
                r.fail(DecodeError::InvalidFlag(other));
                // A stand-in read from the failed reader (no bytes left).
                Err(E::read(r))
            }
        }
    }
    const VERSIONED: bool = T::VERSIONED || E::VERSIONED;
    fn shape_at(version: ApiVersion) -> u64 {
        Shape::new("result")
            .of(T::shape_at(version))
            .of(E::shape_at(version))
            .finish()
    }
    fn encode_at(&self, version: ApiVersion, w: &mut Writer) {
        match self {
            Ok(value) => {
                w.put_raw(&[0]);
                value.encode_at(version, w);
            }
            Err(error) => {
                w.put_raw(&[1]);
                error.encode_at(version, w);
            }
        }
    }
    fn read_at(r: &mut Reader<'_>, version: ApiVersion) -> Self {
        match u8::read(r) {
            0 => Ok(T::read_at(r, version)),
            1 => Err(E::read_at(r, version)),
            other => {
                r.fail(DecodeError::InvalidFlag(other));
                Err(E::read_at(r, version))
            }
        }
    }
}

/// A plain record: its fields encoded in order. Writes the struct, its
/// derives and its [`Wire`] implementation from one field list.
///
/// A field added by a later API minor version goes at the end, marked
/// `since (MAJOR, MINOR)` before its `pub`: a plugin built for an older
/// version is sent the record without it ([`Wire::encode_at`]), what such
/// a plugin sends is read with the field at its default
/// ([`Wire::read_at`]), and the record's shape as the older version knew
/// it ([`Wire::shape_at`]) leaves it out, so plugins built for the older
/// version still load. An unmarked field came with API 1.0.
#[macro_export]
macro_rules! wire_record {
    (
        $(#[$meta:meta])*
        pub struct $name:ident {
            $(
                $(#[$fmeta:meta])*
                $(since ($smaj:literal, $smin:literal))? pub $field:ident : $ty:ty
            ),* $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, PartialEq, Default)]
        pub struct $name {
            $( $(#[$fmeta])* pub $field: $ty, )*
        }

        impl $crate::wire::Wire for $name {
            const SHAPE: u64 = $crate::wire::Shape::new("record")
                $( .name(stringify!($field)).of(<$ty as $crate::wire::Wire>::SHAPE) )*
                .finish();
            fn encode(&self, w: &mut $crate::wire::Writer) {
                let _ = &w;
                $( $crate::wire::Wire::encode(&self.$field, w); )*
            }
            fn read(r: &mut $crate::wire::Reader<'_>) -> Self {
                let _ = &r;
                Self { $( $field: <$ty as $crate::wire::Wire>::read(r), )* }
            }
            const VERSIONED: bool = false
                $( || $crate::__wire_marked!($(($smaj, $smin))?)
                   || <$ty as $crate::wire::Wire>::VERSIONED )*;
            fn shape_at(version: $crate::ApiVersion) -> u64 {
                let _ = version;
                if !<Self as $crate::wire::Wire>::VERSIONED {
                    return <Self as $crate::wire::Wire>::SHAPE;
                }
                let mut shape = $crate::wire::Shape::new("record");
                $(
                    if $crate::__wire_since!($(($smaj, $smin))?).is_at_most(version) {
                        shape = shape
                            .name(stringify!($field))
                            .of(<$ty as $crate::wire::Wire>::shape_at(version));
                    }
                )*
                shape.finish()
            }
            fn encode_at(&self, version: $crate::ApiVersion, w: &mut $crate::wire::Writer) {
                let _ = version;
                if !<Self as $crate::wire::Wire>::VERSIONED {
                    return $crate::wire::Wire::encode(self, w);
                }
                $(
                    if $crate::__wire_since!($(($smaj, $smin))?).is_at_most(version) {
                        $crate::wire::Wire::encode_at(&self.$field, version, w);
                    }
                )*
            }
            fn read_at(r: &mut $crate::wire::Reader<'_>, version: $crate::ApiVersion) -> Self {
                let _ = version;
                if !<Self as $crate::wire::Wire>::VERSIONED {
                    return <Self as $crate::wire::Wire>::read(r);
                }
                Self {
                    $(
                        $field: if $crate::__wire_since!($(($smaj, $smin))?).is_at_most(version) {
                            <$ty as $crate::wire::Wire>::read_at(r, version)
                        } else {
                            ::core::default::Default::default()
                        },
                    )*
                }
            }
        }
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_marked {
    () => {
        false
    };
    (($major:literal, $minor:literal)) => {
        true
    };
}

/// A plain enum carried as one byte: each variant names its tag; the first
/// is the default. An unknown tag is a [`DecodeError::UnknownTag`].
#[macro_export]
macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        pub enum $name:ident : $what:literal {
            $( $(#[$vmeta:meta])* $variant:ident = $tag:literal ),+ $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
        pub enum $name {
            $( $(#[$vmeta])* $variant, )+
        }

        impl $name {
            /// Every variant, in tag order.
            pub const ALL: &'static [$name] = &[ $( $name::$variant, )+ ];

            /// The variant's tag on the wire.
            #[must_use]
            pub const fn tag(self) -> u8 {
                match self { $( $name::$variant => $tag, )+ }
            }

            /// The variant with this tag.
            #[must_use]
            pub const fn from_tag(tag: u8) -> Option<Self> {
                match tag { $( $tag => Some($name::$variant), )+ _ => None }
            }
        }

        impl Default for $name {
            fn default() -> Self {
                Self::ALL[0]
            }
        }

        impl $crate::wire::Wire for $name {
            const SHAPE: u64 = $crate::wire::Shape::new("enum")
                $( .tag($tag as u64).name(stringify!($variant)) )+
                .finish();
            fn encode(&self, w: &mut $crate::wire::Writer) {
                w.put_raw(&[self.tag()]);
            }
            fn read(r: &mut $crate::wire::Reader<'_>) -> Self {
                let tag = <u8 as $crate::wire::Wire>::read(r);
                if let Some(value) = Self::from_tag(tag) {
                    value
                } else {
                    r.fail($crate::wire::DecodeError::UnknownTag {
                        what: $what,
                        tag: u32::from(tag),
                    });
                    Self::ALL[0]
                }
            }
        }
    };
}

/// An enum whose variants carry nothing or one value, tagged with a `u32`.
///
/// A variant added by a later API minor version is marked
/// `since (MAJOR, MINOR)` after its payload: a plugin built for an older
/// version cannot read it, so the host sends it only to plugins built for
/// that version or later (the union's generated `since`), and the union's
/// shape as the older version knew it ([`Wire::shape_at`]) leaves it out,
/// so plugins built for the older version still load. An unmarked variant
/// came with API 1.0. A payload is sent to an older plugin as that version
/// knew it ([`Wire::encode_at`]).
#[macro_export]
macro_rules! wire_union {
    (
        $(#[$meta:meta])*
        pub enum $name:ident : $what:literal {
            $(
                $(#[$vmeta:meta])* $tag:literal => $variant:ident $(($payload:ty))?
                    $(since ($smaj:literal, $smin:literal))?
            ),+ $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, PartialEq)]
        pub enum $name {
            $( $(#[$vmeta])* $variant $(($payload))?, )+
        }

        // A union only the ABI's own tests define may leave these unused.
        #[allow(dead_code)]
        impl $name {
            /// The API version that added this variant (1.0 for the
            /// first version's): a plugin built for an older version
            /// cannot read it.
            #[must_use]
            pub const fn since(&self) -> $crate::ApiVersion {
                match self {
                    $( $name::$variant { .. } => $crate::__wire_since!($(($smaj, $smin))?), )+
                }
            }
        }

        impl $crate::wire::Wire for $name {
            const SHAPE: u64 = $crate::wire::Shape::new("union")
                $(
                    .tag($tag as u64)
                    .name(stringify!($variant))
                    $( .of(<$payload as $crate::wire::Wire>::SHAPE) )?
                )+
                .finish();
            fn encode(&self, w: &mut $crate::wire::Writer) {
                $( $crate::__wire_union_encode!(self, w, $name, $variant, $tag $(, $payload)?); )+
            }
            fn read(r: &mut $crate::wire::Reader<'_>) -> Self {
                let tag = <u32 as $crate::wire::Wire>::read(r);
                $( if tag == $tag {
                    return $crate::__wire_union_read!(r, $name, $variant $(, $payload)?);
                } )+
                r.fail($crate::wire::DecodeError::UnknownTag { what: $what, tag });
                // A stand-in: the first variant, read from the failed
                // reader (no bytes left).
                $crate::__wire_union_first!(r, $name, $( $tag => $variant $(($payload))? ),+)
            }
            const VERSIONED: bool = false
                $( || $crate::__wire_marked!($(($smaj, $smin))?)
                   $( || <$payload as $crate::wire::Wire>::VERSIONED )? )+;
            /// The variants that version had (every variant at the
            /// newest), each payload as that version knew it.
            fn shape_at(version: $crate::ApiVersion) -> u64 {
                let mut shape = $crate::wire::Shape::new("union");
                $(
                    if $crate::__wire_since!($(($smaj, $smin))?).is_at_most(version) {
                        shape = shape
                            .tag($tag as u64)
                            .name(stringify!($variant))
                            $( .of(<$payload as $crate::wire::Wire>::shape_at(version)) )?;
                    }
                )+
                shape.finish()
            }
            fn encode_at(&self, version: $crate::ApiVersion, w: &mut $crate::wire::Writer) {
                let _ = version;
                if !<Self as $crate::wire::Wire>::VERSIONED {
                    return $crate::wire::Wire::encode(self, w);
                }
                $( $crate::__wire_union_encode_at!(
                    self, version, w, $name, $variant, $tag $(, $payload)?
                ); )+
            }
            fn read_at(r: &mut $crate::wire::Reader<'_>, version: $crate::ApiVersion) -> Self {
                let _ = version;
                if !<Self as $crate::wire::Wire>::VERSIONED {
                    return <Self as $crate::wire::Wire>::read(r);
                }
                let tag = <u32 as $crate::wire::Wire>::read(r);
                $( if tag == $tag {
                    return $crate::__wire_union_read_at!(r, version, $name, $variant $(, $payload)?);
                } )+
                r.fail($crate::wire::DecodeError::UnknownTag { what: $what, tag });
                $crate::__wire_union_first!(r, $name, $( $tag => $variant $(($payload))? ),+)
            }
        }
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_since {
    () => {
        $crate::ApiVersion::new(1, 0)
    };
    (($major:literal, $minor:literal)) => {
        $crate::ApiVersion::new($major, $minor)
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_union_first {
    ($r:ident, $name:ident, $tag:tt => $variant:ident $(($payload:ty))? $(, $($rest:tt)*)?) => {
        $crate::__wire_union_read!($r, $name, $variant $(, $payload)?)
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_union_encode {
    ($self:ident, $w:ident, $name:ident, $variant:ident, $tag:literal) => {
        if let $name::$variant = $self {
            <u32 as $crate::wire::Wire>::encode(&$tag, $w);
            return;
        }
    };
    ($self:ident, $w:ident, $name:ident, $variant:ident, $tag:literal, $payload:ty) => {
        if let $name::$variant(value) = $self {
            <u32 as $crate::wire::Wire>::encode(&$tag, $w);
            <$payload as $crate::wire::Wire>::encode(value, $w);
            return;
        }
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_union_read {
    ($r:ident, $name:ident, $variant:ident) => {
        $name::$variant
    };
    ($r:ident, $name:ident, $variant:ident, $payload:ty) => {
        $name::$variant(<$payload as $crate::wire::Wire>::read($r))
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_union_encode_at {
    ($self:ident, $version:ident, $w:ident, $name:ident, $variant:ident, $tag:literal) => {
        if let $name::$variant = $self {
            <u32 as $crate::wire::Wire>::encode(&$tag, $w);
            return;
        }
    };
    ($self:ident, $version:ident, $w:ident, $name:ident, $variant:ident, $tag:literal, $payload:ty) => {
        if let $name::$variant(value) = $self {
            <u32 as $crate::wire::Wire>::encode(&$tag, $w);
            <$payload as $crate::wire::Wire>::encode_at(value, $version, $w);
            return;
        }
    };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __wire_union_read_at {
    ($r:ident, $version:ident, $name:ident, $variant:ident) => {
        $name::$variant
    };
    ($r:ident, $version:ident, $name:ident, $variant:ident, $payload:ty) => {
        $name::$variant(<$payload as $crate::wire::Wire>::read_at($r, $version))
    };
}

/// Tuples: their parts in order.
macro_rules! wire_tuple {
    ($( ($($part:ident . $at:tt),+) )+) => {$(
        impl<$($part: Wire),+> Wire for ($($part,)+) {
            const SHAPE: u64 = Shape::new("tuple") $( .of($part::SHAPE) )+ .finish();
            fn encode(&self, w: &mut Writer) {
                $( self.$at.encode(w); )+
            }
            fn read(r: &mut Reader<'_>) -> Self {
                ($($part::read(r),)+)
            }
            const VERSIONED: bool = false $( || $part::VERSIONED )+;
            fn shape_at(version: ApiVersion) -> u64 {
                Shape::new("tuple") $( .of($part::shape_at(version)) )+ .finish()
            }
            fn encode_at(&self, version: ApiVersion, w: &mut Writer) {
                $( self.$at.encode_at(version, w); )+
            }
            fn read_at(r: &mut Reader<'_>, version: ApiVersion) -> Self {
                ($($part::read_at(r, version),)+)
            }
        }
    )+};
}

wire_tuple! {
    (A.0, B.1)
    (A.0, B.1, C.2)
    (A.0, B.1, C.2, D.3)
}

#[cfg(test)]
mod tests {
    use super::*;

    // A list of integers writes the bytes its items write one by one.
    #[test]
    fn integer_lists_write_their_items_bytes() {
        fn one_by_one<T: Wire>(items: &[T]) -> Vec<u8> {
            let mut w = Writer::new();
            w.put_count(items.len());
            for item in items {
                item.encode(&mut w);
            }
            w.into_bytes()
        }
        let bytes: Vec<u8> = (0..=255).collect();
        assert_eq!(bytes.to_bytes(), one_by_one(&bytes));
        let words: Vec<u32> = vec![0, 1, 0xDEAD_BEEF, u32::MAX];
        assert_eq!(words.to_bytes(), one_by_one(&words));
        let doubles: Vec<f64> = vec![0.5, -2.25, f64::MAX];
        assert_eq!(doubles.to_bytes(), one_by_one(&doubles));
        assert_eq!(Vec::<u8>::from_bytes(&bytes.to_bytes()), Ok(bytes));
    }

    crate::wire_enum! {
        /// An enum.
        pub enum Colour: "colour" {
            /// First.
            Red = 0,
            /// Second.
            Green = 3,
        }
    }

    crate::wire_union! {
        /// A union.
        #[derive(Default)]
        pub enum Shape: "shape" {
            /// Nothing.
            #[default]
            0 => Point,
            /// A radius.
            1 => Circle(f32),
            /// A label.
            7 => Named(String),
        }
    }

    #[test]
    fn enums_and_unions_round_trip_and_refuse_unknown_tags() {
        assert_eq!(Colour::default(), Colour::Red);
        assert_eq!(Colour::Green.to_bytes(), vec![3]);
        assert_eq!(Colour::from_bytes(&[3]), Ok(Colour::Green));
        assert_eq!(
            Colour::from_bytes(&[1]),
            Err(DecodeError::UnknownTag {
                what: "colour",
                tag: 1
            })
        );
        for shape in [Shape::Point, Shape::Circle(2.5), Shape::Named("x".into())] {
            assert_eq!(Shape::from_bytes(&shape.to_bytes()), Ok(shape));
        }
        assert_eq!(Shape::Point.to_bytes(), vec![0, 0, 0, 0]);
        assert_eq!(
            Shape::from_bytes(&[2, 0, 0, 0]),
            Err(DecodeError::UnknownTag {
                what: "shape",
                tag: 2
            })
        );
        let pair = (5u32, "a".to_owned());
        assert_eq!(<(u32, String)>::from_bytes(&pair.to_bytes()), Ok(pair));
    }

    crate::wire_record! {
        /// A record with one of everything.
        pub struct Sample {
            pub a: u8,
            pub b: u32,
            pub c: i64,
            pub d: f32,
            pub e: bool,
            pub f: String,
            pub g: Vec<u16>,
            pub h: Option<String>,
        }
    }

    #[test]
    fn values_encode_little_endian_and_length_prefixed() {
        let sample = Sample {
            a: 1,
            b: 0x0403_0201,
            c: -2,
            d: 1.5,
            e: true,
            f: "hé".to_owned(),
            g: vec![7, 8],
            h: None,
        };
        let bytes = sample.to_bytes();
        let expected: Vec<u8> = [
            &[1u8][..],
            &[1, 2, 3, 4],
            &(-2i64).to_le_bytes(),
            &1.5f32.to_le_bytes(),
            &[1],
            &[3, 0, 0, 0, b'h', 0xC3, 0xA9],
            &[2, 0, 0, 0, 7, 0, 8, 0],
            &[0],
        ]
        .concat();
        assert_eq!(bytes, expected);
        assert_eq!(Sample::from_bytes(&bytes), Ok(sample));
    }

    #[test]
    fn malformed_bytes_are_errors_not_panics() {
        assert_eq!(u32::from_bytes(&[1, 2]), Err(DecodeError::Truncated));
        assert_eq!(bool::from_bytes(&[2]), Err(DecodeError::InvalidFlag(2)));
        assert_eq!(
            String::from_bytes(&[2, 0, 0, 0, 0xFF, 0xFE]),
            Err(DecodeError::InvalidUtf8)
        );
        assert_eq!(u8::from_bytes(&[1, 2]), Err(DecodeError::Trailing(1)));
        // A huge count with no items behind it is truncated, not an
        // allocation of four billion entries.
        assert_eq!(
            Vec::<u32>::from_bytes(&[0xFF, 0xFF, 0xFF, 0xFF]),
            Err(DecodeError::Truncated)
        );
    }

    #[test]
    fn results_and_options_round_trip() {
        let ok: Result<u32, String> = Ok(5);
        let err: Result<u32, String> = Err("no".to_owned());
        assert_eq!(Result::<u32, String>::from_bytes(&ok.to_bytes()), Ok(ok));
        assert_eq!(Result::<u32, String>::from_bytes(&err.to_bytes()), Ok(err));
        // An error may be any record; its shape is part of the result's.
        let typed: Result<u32, Colour> = Err(Colour::Red);
        assert_eq!(
            Result::<u32, Colour>::from_bytes(&typed.to_bytes()),
            Ok(typed)
        );
        assert_ne!(Result::<u32, Colour>::SHAPE, Result::<u32, String>::SHAPE);
        let some = Some("x".to_owned());
        assert_eq!(Option::<String>::from_bytes(&some.to_bytes()), Ok(some));
    }

    crate::wire_record! {
        /// A record with lists, a union and an enum inside.
        pub struct Nested {
            pub ids: Vec<u32>,
            pub flags: Vec<bool>,
            pub shape: Shape,
            pub colour: Colour,
            pub label: String,
        }
    }

    #[test]
    fn lists_of_numbers_read_as_one_run_and_round_trip() {
        let value = Nested {
            ids: vec![1, 0xFFFF_FFFF, 7],
            flags: vec![true, false],
            shape: Shape::Named("n".into()),
            colour: Colour::Green,
            label: "l".into(),
        };
        let bytes = value.to_bytes();
        assert_eq!(
            &bytes[..16],
            &[3, 0, 0, 0, 1, 0, 0, 0, 255, 255, 255, 255, 7, 0, 0, 0]
        );
        assert_eq!(Nested::from_bytes(&bytes), Ok(value));
        for list in [vec![], vec![-1.5f64, 2.0]] {
            assert_eq!(Vec::<f64>::from_bytes(&list.to_bytes()), Ok(list));
        }
        assert_eq!(Vec::<u8>::from_bytes(&[2, 0, 0, 0, 9, 8]), Ok(vec![9, 8]));
        // A run cut short, and a count far past the bytes.
        assert_eq!(
            Vec::<u32>::from_bytes(&[2, 0, 0, 0, 1, 0, 0, 0, 2]),
            Err(DecodeError::Truncated)
        );
        assert_eq!(
            Vec::<u64>::from_bytes(&[0xFF, 0xFF, 0xFF, 0xFF, 1]),
            Err(DecodeError::Truncated)
        );
    }

    // Reading on after a fault (no check per field) still reports the
    // first fault, wherever in the record it was.
    #[test]
    fn the_first_fault_inside_a_record_is_the_one_reported() {
        let good = Nested {
            ids: vec![5],
            flags: vec![true, true],
            shape: Shape::Circle(1.0),
            colour: Colour::Red,
            label: "ok".into(),
        }
        .to_bytes();
        // ids: 4 + 4 bytes; flags: count at 8, items at 12 and 13; the
        // shape's tag at 14; its payload at 18; the colour at 22.
        let mut bad_flag = good.clone();
        bad_flag[13] = 2;
        assert_eq!(
            Nested::from_bytes(&bad_flag),
            Err(DecodeError::InvalidFlag(2))
        );
        let mut bad_shape = good.clone();
        bad_shape[14] = 9;
        assert_eq!(
            Nested::from_bytes(&bad_shape),
            Err(DecodeError::UnknownTag {
                what: "shape",
                tag: 9
            })
        );
        let mut bad_colour = good.clone();
        bad_colour[22] = 1;
        assert_eq!(
            Nested::from_bytes(&bad_colour),
            Err(DecodeError::UnknownTag {
                what: "colour",
                tag: 1
            })
        );
        let mut bad_text = good.clone();
        let last = bad_text.len() - 1;
        bad_text[last] = 0xFF;
        assert_eq!(Nested::from_bytes(&bad_text), Err(DecodeError::InvalidUtf8));
        assert_eq!(
            Nested::from_bytes(&good[..good.len() - 1]),
            Err(DecodeError::Truncated)
        );
        // Two faults: the first is reported, and `decode` after it says so
        // too.
        let mut both = bad_flag.clone();
        both[22] = 1;
        let mut r = Reader::new(&both);
        assert_eq!(Nested::decode(&mut r), Err(DecodeError::InvalidFlag(2)));
        assert_eq!(u8::decode(&mut r), Err(DecodeError::InvalidFlag(2)));
        assert_eq!(r.error(), Some(&DecodeError::InvalidFlag(2)));
        assert_eq!(r.finish(), Err(DecodeError::InvalidFlag(2)));
    }
}
