//! The encoding's building blocks: the [`Wire`] trait, the reader, the
//! contexts, the errors, the primitive encodings and the macros every type
//! of the stream is written with.
//!
//! Integers and floats are little-endian (floats by their bits, so every
//! NaN payload survives); `bool` and `Option` are one byte, 0 or 1; strings,
//! lists and maps are a `u32` count and their items; a struct is its fields
//! in order; an enum is a one-byte tag and its variant's fields. Maps go out
//! in key order, so the same value always gives the same bytes.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_core::player::SkillFormulaBonusResolver;
use ac_core::spells::SpellTable;

pub use ac_plugin_abi::wire::{Shape, Writer};

/// How deep values may nest inside each other when read (a binding value
/// holding item records holding binding values): far past anything a
/// session sends, and short of what would exhaust the reader's stack.
pub const MAX_DEPTH: u32 = 64;

/// Why a value could not be written.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum EncodeError {
    /// The command only means something inside one process (it carries a
    /// channel, or ends the session): it has no encoding.
    #[error("{0} is not a wire command")]
    NotAWireCommand(&'static str),
    /// A generation-stamped command inside another one.
    #[error("a generation-stamped command cannot hold another")]
    NestedAtGeneration,
    /// The value cannot be carried faithfully (see the crate docs).
    #[error("{0} cannot be carried on the wire")]
    Unsupported(&'static str),
    /// A list, string or map longer than a `u32` count can say.
    #[error("{0} items are more than a count can say")]
    CountTooLarge(usize),
}

/// Why bytes could not be read as the expected value.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum DecodeError {
    /// The bytes ended inside a value.
    #[error("the bytes end inside a value")]
    Truncated,
    /// Bytes were left over after the value.
    #[error("{0} bytes left over after the value")]
    Trailing(usize),
    /// A count larger than the bytes left could hold.
    #[error("a count of {count} with only {left} bytes left")]
    CountTooLarge {
        /// The count read.
        count: usize,
        /// The bytes left after it.
        left: usize,
    },
    /// A string was not UTF-8.
    #[error("a string is not UTF-8")]
    InvalidUtf8,
    /// A `bool` or `Option` byte was neither 0 nor 1.
    #[error("flag byte {0} is neither 0 nor 1")]
    InvalidFlag(u8),
    /// An enum tag this side does not know.
    #[error("unknown {what} tag {tag}")]
    UnknownTag {
        /// What was being read.
        what: &'static str,
        /// The tag.
        tag: u32,
    },
    /// A number that does not fit what it is read into (a count on a 32-bit
    /// target, an instant before this process could have one, a character
    /// that is not a Unicode scalar).
    #[error("{0} out of range")]
    OutOfRange(&'static str),
    /// A map or set named the same key twice.
    #[error("a key appears twice")]
    DuplicateKey,
    /// Values nested deeper than [`MAX_DEPTH`].
    #[error("values nest deeper than {MAX_DEPTH}")]
    TooDeep,
    /// The stream-format version byte is not this build's.
    #[error("stream format {found}, this build reads {expected}")]
    Version {
        /// The version in the bytes.
        found: u8,
        /// This build's version.
        expected: u8,
    },
    /// A generation-stamped command inside another one.
    #[error("a generation-stamped command holds another")]
    NestedAtGeneration,
    /// A plugin font could not be baked again from what was sent.
    #[error("a plugin font cannot be baked: {0}")]
    Font(String),
}

/// What the encoder needs besides the values: the instant the stream counts
/// from. Instants go out as signed microseconds from it.
#[derive(Debug, Clone, Copy)]
pub struct EncodeContext {
    /// The stream's epoch, on the sender's clock.
    pub epoch: Instant,
}

impl EncodeContext {
    /// A stream that counts from `epoch`.
    #[must_use]
    pub fn new(epoch: Instant) -> Self {
        Self { epoch }
    }
}

/// What the decoder needs besides the bytes: the instant on its own clock
/// that stands for the stream's epoch (taken when the stream starts), and
/// the receiver's own copy of two pieces of game data the session holds but
/// never sends, built from the same tables: the spell table a spellbook
/// refers to, and the skill formulas that score a skill from buffed
/// attributes.
#[derive(Clone)]
pub struct DecodeContext {
    /// The stream's epoch, on the receiver's clock.
    pub epoch: Instant,
    /// The spell table decoded spellbooks get. `None`: the receiver has none
    /// yet, and a decoded book has no table installed, so one can be
    /// installed later.
    pub spell_table: Option<Arc<SpellTable>>,
    /// What decoded player numbers recompute a skill's attribute term
    /// with. `None`: they have none (a buffed attribute then does not
    /// change a skill).
    pub skill_formula_bonus_resolver: Option<SkillFormulaBonusResolver>,
}

impl std::fmt::Debug for DecodeContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DecodeContext")
            .field("epoch", &self.epoch)
            .field("spell_table", &self.spell_table.as_ref().map(|t| t.len()))
            .field(
                "skill_formula_bonus_resolver",
                &self.skill_formula_bonus_resolver.is_some(),
            )
            .finish()
    }
}

impl DecodeContext {
    /// A stream whose epoch is `epoch` here, decoding spellbooks against
    /// `spell_table` and player numbers with `skill_formula_bonus_resolver`
    /// (each `None` when the receiver has none; nothing is assumed).
    #[must_use]
    pub fn new(
        epoch: Instant,
        spell_table: Option<Arc<SpellTable>>,
        skill_formula_bonus_resolver: Option<SkillFormulaBonusResolver>,
    ) -> Self {
        Self {
            epoch,
            spell_table,
            skill_formula_bonus_resolver,
        }
    }
}

/// Writes a `u32` count.
///
/// # Errors
/// [`EncodeError::CountTooLarge`] past `u32::MAX`.
pub fn put_count(w: &mut Writer, count: usize) -> Result<(), EncodeError> {
    let count = u32::try_from(count).map_err(|_| EncodeError::CountTooLarge(count))?;
    w.put_raw(&count.to_le_bytes());
    Ok(())
}

/// Reads values from a byte slice, front to back. Every read checks what is
/// left; nothing is allocated for a count the bytes cannot back.
#[derive(Debug, Clone)]
pub struct Reader<'a> {
    bytes: &'a [u8],
    depth: u32,
}

impl<'a> Reader<'a> {
    /// Reads `bytes`.
    #[must_use]
    pub fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, depth: 0 }
    }

    /// The bytes not read yet.
    #[must_use]
    pub fn remaining(&self) -> usize {
        self.bytes.len()
    }

    /// The next `n` bytes.
    ///
    /// # Errors
    /// [`DecodeError::Truncated`] when fewer are left.
    pub fn take(&mut self, n: usize) -> Result<&'a [u8], DecodeError> {
        let (head, rest) = self
            .bytes
            .split_at_checked(n)
            .ok_or(DecodeError::Truncated)?;
        self.bytes = rest;
        Ok(head)
    }

    /// The next `N` bytes.
    ///
    /// # Errors
    /// [`DecodeError::Truncated`] when fewer are left.
    pub fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        let (head, rest) = self
            .bytes
            .split_first_chunk::<N>()
            .ok_or(DecodeError::Truncated)?;
        self.bytes = rest;
        Ok(*head)
    }

    /// The next byte.
    ///
    /// # Errors
    /// [`DecodeError::Truncated`] at the end.
    pub fn byte(&mut self) -> Result<u8, DecodeError> {
        Ok(self.array::<1>()?[0])
    }

    /// A `u32` count of items of at least `min_item` bytes each, checked
    /// against the bytes left before anything is allocated for it.
    ///
    /// # Errors
    /// [`DecodeError::Truncated`]; [`DecodeError::CountTooLarge`].
    pub fn count(&mut self, min_item: usize) -> Result<usize, DecodeError> {
        let count = usize::try_from(u32::from_le_bytes(self.array()?))
            .map_err(|_| DecodeError::OutOfRange("count"))?;
        let left = self.remaining();
        if count.saturating_mul(min_item.max(1)) > left {
            return Err(DecodeError::CountTooLarge { count, left });
        }
        Ok(count)
    }

    /// Reads a nested value, one level deeper.
    ///
    /// # Errors
    /// [`DecodeError::TooDeep`] past [`MAX_DEPTH`]; whatever `read` returns.
    pub fn nested<T>(
        &mut self,
        read: impl FnOnce(&mut Self) -> Result<T, DecodeError>,
    ) -> Result<T, DecodeError> {
        if self.depth >= MAX_DEPTH {
            return Err(DecodeError::TooDeep);
        }
        self.depth += 1;
        let value = read(self);
        self.depth -= 1;
        value
    }

    /// The end: an error when bytes are left.
    ///
    /// # Errors
    /// [`DecodeError::Trailing`].
    pub fn finish(self) -> Result<(), DecodeError> {
        if self.bytes.is_empty() {
            Ok(())
        } else {
            Err(DecodeError::Trailing(self.bytes.len()))
        }
    }
}

/// A value with an encoding on the remote window's link.
///
/// This is the crate's own trait rather than the plugin calling
/// convention's: the stream's types live in other crates, and only a trait
/// of this crate may be implemented for them here.
pub trait Wire: Sized {
    /// A hash of the encoding's layout ([`Shape`]): each field's name and
    /// shape in order, and each variant's tag, name and fields. It changes
    /// whenever the layout of this type, or of anything inside it, changes,
    /// a rename included (both ends are built from the same source).
    const SHAPE: u64;

    /// The fewest bytes a value takes: a count is checked against it.
    const MIN_SIZE: usize = 1;

    /// Appends the value.
    ///
    /// # Errors
    /// A value with no faithful encoding ([`EncodeError`]).
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError>;

    /// Reads one value.
    ///
    /// # Errors
    /// Bytes that do not read as one ([`DecodeError`]).
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError>;

    /// `items` in a row (a list's items). Bytes go out as one copy.
    #[doc(hidden)]
    fn encode_slice(items: &[Self], w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        for item in items {
            item.encode(w, cx)?;
        }
        Ok(())
    }

    /// `count` values in a row (a list's items, the count already checked
    /// against the bytes left).
    #[doc(hidden)]
    fn decode_vec(
        r: &mut Reader<'_>,
        cx: &DecodeContext,
        count: usize,
    ) -> Result<Vec<Self>, DecodeError> {
        // The count is bounded by the bytes left, not by what an item
        // occupies in memory: grow as items arrive.
        let mut items = Vec::with_capacity(count.min(1024));
        for _ in 0..count {
            items.push(Self::decode(r, cx)?);
        }
        Ok(items)
    }
}

/// The bytes of `value` on their own.
///
/// # Errors
/// [`EncodeError`].
pub fn to_bytes<T: Wire>(value: &T, cx: &EncodeContext) -> Result<Vec<u8>, EncodeError> {
    let mut w = Writer::new();
    value.encode(&mut w, cx)?;
    Ok(w.into_bytes())
}

/// Exactly one value from `bytes`.
///
/// # Errors
/// [`DecodeError`], including bytes left over.
pub fn from_bytes<T: Wire>(bytes: &[u8], cx: &DecodeContext) -> Result<T, DecodeError> {
    let mut r = Reader::new(bytes);
    let value = T::decode(&mut r, cx)?;
    r.finish()?;
    Ok(value)
}

macro_rules! wire_le {
    ($($t:ty),*) => {$(
        impl Wire for $t {
            const SHAPE: u64 = Shape::new(stringify!($t)).finish();
            const MIN_SIZE: usize = std::mem::size_of::<$t>();
            fn encode(&self, w: &mut Writer, _cx: &EncodeContext) -> Result<(), EncodeError> {
                w.put_raw(&self.to_le_bytes());
                Ok(())
            }
            fn decode(r: &mut Reader<'_>, _cx: &DecodeContext) -> Result<Self, DecodeError> {
                Ok(<$t>::from_le_bytes(r.array()?))
            }
        }
    )*};
}

wire_le!(u16, u32, u64, i8, i16, i32, i64, i128, f32, f64);

impl Wire for u8 {
    const SHAPE: u64 = Shape::new("u8").finish();
    fn encode(&self, w: &mut Writer, _cx: &EncodeContext) -> Result<(), EncodeError> {
        w.put_raw(&[*self]);
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, _cx: &DecodeContext) -> Result<Self, DecodeError> {
        r.byte()
    }
    fn encode_slice(
        items: &[Self],
        w: &mut Writer,
        _cx: &EncodeContext,
    ) -> Result<(), EncodeError> {
        w.put_raw(items);
        Ok(())
    }
    fn decode_vec(
        r: &mut Reader<'_>,
        _cx: &DecodeContext,
        count: usize,
    ) -> Result<Vec<Self>, DecodeError> {
        Ok(r.take(count)?.to_vec())
    }
}

/// A `usize` goes out as a `u64`, so both ends agree whatever their width.
impl Wire for usize {
    const SHAPE: u64 = Shape::new("usize").finish();
    const MIN_SIZE: usize = 8;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        (*self as u64).encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        usize::try_from(u64::decode(r, cx)?).map_err(|_| DecodeError::OutOfRange("usize"))
    }
}

impl Wire for bool {
    const SHAPE: u64 = Shape::new("bool").finish();
    fn encode(&self, w: &mut Writer, _cx: &EncodeContext) -> Result<(), EncodeError> {
        w.put_raw(&[u8::from(*self)]);
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, _cx: &DecodeContext) -> Result<Self, DecodeError> {
        match r.byte()? {
            0 => Ok(false),
            1 => Ok(true),
            other => Err(DecodeError::InvalidFlag(other)),
        }
    }
}

impl Wire for char {
    const SHAPE: u64 = Shape::new("char").finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        u32::from(*self).encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        char::from_u32(u32::decode(r, cx)?).ok_or(DecodeError::OutOfRange("char"))
    }
}

impl Wire for String {
    const SHAPE: u64 = Shape::new("string").finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, _cx: &EncodeContext) -> Result<(), EncodeError> {
        put_count(w, self.len())?;
        w.put_raw(self.as_bytes());
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, _cx: &DecodeContext) -> Result<Self, DecodeError> {
        let len = r.count(1)?;
        let bytes = r.take(len)?;
        std::str::from_utf8(bytes)
            .map(str::to_owned)
            .map_err(|_| DecodeError::InvalidUtf8)
    }
}

/// A path goes out as its text; a path that is not Unicode cannot.
impl Wire for PathBuf {
    const SHAPE: u64 = Shape::new("path").finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        self.to_str()
            .ok_or(EncodeError::Unsupported("a path that is not Unicode"))?
            .to_owned()
            .encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        Ok(PathBuf::from(String::decode(r, cx)?))
    }
}

impl<T: Wire> Wire for Vec<T> {
    const SHAPE: u64 = Shape::new("list").of(T::SHAPE).finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        put_count(w, self.len())?;
        T::encode_slice(self, w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let count = r.count(T::MIN_SIZE)?;
        T::decode_vec(r, cx, count)
    }
}

impl<T: Wire, const N: usize> Wire for [T; N] {
    const SHAPE: u64 = Shape::new("array").tag(N as u64).of(T::SHAPE).finish();
    const MIN_SIZE: usize = T::MIN_SIZE.saturating_mul(N);
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        T::encode_slice(self, w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let items = T::decode_vec(r, cx, N)?;
        items.try_into().map_err(|_| DecodeError::Truncated)
    }
}

impl<T: Wire> Wire for Option<T> {
    const SHAPE: u64 = Shape::new("option").of(T::SHAPE).finish();
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        match self {
            None => {
                w.put_raw(&[0]);
                Ok(())
            }
            Some(value) => {
                w.put_raw(&[1]);
                value.encode(w, cx)
            }
        }
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        match r.byte()? {
            0 => Ok(None),
            1 => Ok(Some(T::decode(r, cx)?)),
            other => Err(DecodeError::InvalidFlag(other)),
        }
    }
}

/// A box is its value.
impl<T: Wire> Wire for Box<T> {
    const SHAPE: u64 = T::SHAPE;
    const MIN_SIZE: usize = T::MIN_SIZE;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        (**self).encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        Ok(Box::new(T::decode(r, cx)?))
    }
}

/// A shared value goes out by value and comes back in a fresh `Arc`.
impl<T: Wire> Wire for Arc<T> {
    const SHAPE: u64 = T::SHAPE;
    const MIN_SIZE: usize = T::MIN_SIZE;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        (**self).encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        Ok(Arc::new(T::decode(r, cx)?))
    }
}

macro_rules! wire_tuple {
    ($( ($($part:ident . $at:tt),+) )+) => {$(
        impl<$($part: Wire),+> Wire for ($($part,)+) {
            const SHAPE: u64 = Shape::new("tuple") $( .of($part::SHAPE) )+ .finish();
            const MIN_SIZE: usize = 0 $( + $part::MIN_SIZE )+;
            fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
                $( self.$at.encode(w, cx)?; )+
                Ok(())
            }
            fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
                Ok(($($part::decode(r, cx)?,)+))
            }
        }
    )+};
}

wire_tuple! {
    (A.0, B.1)
    (A.0, B.1, C.2)
}

/// Map entries go out in key order; a key named twice is an error, so a
/// map reads back only as the one value that wrote it.
impl<K: Wire + Ord, V: Wire> Wire for BTreeMap<K, V> {
    const SHAPE: u64 = Shape::new("map").of(K::SHAPE).of(V::SHAPE).finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        put_count(w, self.len())?;
        for (key, value) in self {
            key.encode(w, cx)?;
            value.encode(w, cx)?;
        }
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let count = r.count(K::MIN_SIZE + V::MIN_SIZE)?;
        let mut map = BTreeMap::new();
        for _ in 0..count {
            let key = K::decode(r, cx)?;
            let value = V::decode(r, cx)?;
            if map.insert(key, value).is_some() {
                return Err(DecodeError::DuplicateKey);
            }
        }
        Ok(map)
    }
}

/// A hash map goes out as a sorted map does (its iteration order is not
/// stable), with the same shape.
impl<K: Wire + Ord + std::hash::Hash, V: Wire, S: std::hash::BuildHasher + Default> Wire
    for HashMap<K, V, S>
{
    const SHAPE: u64 = Shape::new("map").of(K::SHAPE).of(V::SHAPE).finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let mut entries: Vec<(&K, &V)> = self.iter().collect();
        entries.sort_by(|a, b| a.0.cmp(b.0));
        put_count(w, entries.len())?;
        for (key, value) in entries {
            key.encode(w, cx)?;
            value.encode(w, cx)?;
        }
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let count = r.count(K::MIN_SIZE + V::MIN_SIZE)?;
        let mut map = HashMap::with_capacity_and_hasher(count.min(1024), S::default());
        for _ in 0..count {
            let key = K::decode(r, cx)?;
            let value = V::decode(r, cx)?;
            if map.insert(key, value).is_some() {
                return Err(DecodeError::DuplicateKey);
            }
        }
        Ok(map)
    }
}

/// A set goes out sorted; a member named twice is an error.
impl<T: Wire + Ord + std::hash::Hash, S: std::hash::BuildHasher + Default> Wire for HashSet<T, S> {
    const SHAPE: u64 = Shape::new("set").of(T::SHAPE).finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let mut items: Vec<&T> = self.iter().collect();
        items.sort();
        put_count(w, items.len())?;
        for item in items {
            item.encode(w, cx)?;
        }
        Ok(())
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let count = r.count(T::MIN_SIZE)?;
        let mut set = HashSet::with_capacity_and_hasher(count.min(1024), S::default());
        for _ in 0..count {
            if !set.insert(T::decode(r, cx)?) {
                return Err(DecodeError::DuplicateKey);
            }
        }
        Ok(set)
    }
}

/// A duration exactly: whole seconds and nanoseconds.
impl Wire for Duration {
    const SHAPE: u64 = Shape::new("duration").finish();
    const MIN_SIZE: usize = 12;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        self.as_secs().encode(w, cx)?;
        self.subsec_nanos().encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let secs = u64::decode(r, cx)?;
        let nanos = u32::decode(r, cx)?;
        if nanos >= 1_000_000_000 {
            return Err(DecodeError::OutOfRange("duration nanoseconds"));
        }
        Ok(Duration::new(secs, nanos))
    }
}

/// An instant goes out as signed microseconds from the stream's epoch and
/// comes back at that offset from the receiver's epoch (below a microsecond
/// is dropped).
impl Wire for Instant {
    const SHAPE: u64 = Shape::new("instant-micros").finish();
    const MIN_SIZE: usize = 8;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let micros = |d: Duration| i64::try_from(d.as_micros()).unwrap_or(i64::MAX);
        let offset = match self.checked_duration_since(cx.epoch) {
            Some(after) => micros(after),
            None => -micros(cx.epoch.duration_since(*self)),
        };
        offset.encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let offset = i64::decode(r, cx)?;
        let magnitude = Duration::from_micros(offset.unsigned_abs());
        if offset >= 0 {
            cx.epoch.checked_add(magnitude)
        } else {
            cx.epoch.checked_sub(magnitude)
        }
        .ok_or(DecodeError::OutOfRange("instant"))
    }
}

/// A struct with named fields: each field in order. The encoder takes the
/// struct apart with every field named, and the decoder builds it with
/// every field named, so a field added to the type breaks this crate's
/// build instead of the stream.
macro_rules! record {
    ($( $name:ident { $($field:ident : $ty:ty),* $(,)? } )*) => {$(
        impl $crate::codec::Wire for $name {
            const SHAPE: u64 = $crate::codec::Shape::new("record")
                $( .name(stringify!($field)).of(<$ty as $crate::codec::Wire>::SHAPE) )*
                .finish();
            const MIN_SIZE: usize = 0 $( + <$ty as $crate::codec::Wire>::MIN_SIZE )*;
            fn encode(
                &self,
                w: &mut $crate::codec::Writer,
                cx: &$crate::codec::EncodeContext,
            ) -> Result<(), $crate::codec::EncodeError> {
                let _ = (&w, cx);
                let $name { $($field),* } = self;
                $( <$ty as $crate::codec::Wire>::encode($field, w, cx)?; )*
                Ok(())
            }
            fn decode(
                r: &mut $crate::codec::Reader<'_>,
                cx: &$crate::codec::DecodeContext,
            ) -> Result<Self, $crate::codec::DecodeError> {
                let _ = (&r, cx);
                Ok($name { $( $field: <$ty as $crate::codec::Wire>::decode(r, cx)?, )* })
            }
        }
    )*};
}

/// A tuple struct (a newtype), its fields given names for the macro.
macro_rules! tuple_record {
    ($( $name:ident ( $($field:ident : $ty:ty),* $(,)? ) )*) => {$(
        impl $crate::codec::Wire for $name {
            const SHAPE: u64 = $crate::codec::Shape::new("record")
                $( .of(<$ty as $crate::codec::Wire>::SHAPE) )*
                .finish();
            const MIN_SIZE: usize = 0 $( + <$ty as $crate::codec::Wire>::MIN_SIZE )*;
            fn encode(
                &self,
                w: &mut $crate::codec::Writer,
                cx: &$crate::codec::EncodeContext,
            ) -> Result<(), $crate::codec::EncodeError> {
                let $name ( $($field),* ) = self;
                $( <$ty as $crate::codec::Wire>::encode($field, w, cx)?; )*
                Ok(())
            }
            fn decode(
                r: &mut $crate::codec::Reader<'_>,
                cx: &$crate::codec::DecodeContext,
            ) -> Result<Self, $crate::codec::DecodeError> {
                Ok($name ( $( <$ty as $crate::codec::Wire>::decode(r, cx)?, )* ))
            }
        }
    )*};
}

/// An enum: a one-byte tag, then the variant's fields in order. Tuple
/// variants name their fields for the macro (`Pair(a: u32, b: bool)`).
/// The encoder matches every variant with no wildcard and every field
/// named, so a new variant or field breaks this crate's build.
macro_rules! union {
    ($(
        $name:ident : $what:literal {
            $(
                $tag:literal => $variant:ident
                    $( ( $($a:ident : $at:ty),* $(,)? ) )?
                    $( { $($f:ident : $ft:ty),* $(,)? } )?
            ),+ $(,)?
        }
    )*) => {$(
        impl $crate::codec::Wire for $name {
            const SHAPE: u64 = $crate::codec::Shape::new("union")
                $(
                    .tag($tag)
                    .name(stringify!($variant))
                    $( $( .of(<$at as $crate::codec::Wire>::SHAPE) )* )?
                    $( $( .name(stringify!($f)).of(<$ft as $crate::codec::Wire>::SHAPE) )* )?
                )+
                .finish();
            fn encode(
                &self,
                w: &mut $crate::codec::Writer,
                cx: &$crate::codec::EncodeContext,
            ) -> Result<(), $crate::codec::EncodeError> {
                let _ = cx;
                match self {
                    $(
                        $name::$variant $( ( $($a),* ) )? $( { $($f),* } )? => {
                            w.put_raw(&[$tag]);
                            $( $( <$at as $crate::codec::Wire>::encode($a, w, cx)?; )* )?
                            $( $( <$ft as $crate::codec::Wire>::encode($f, w, cx)?; )* )?
                        }
                    )+
                }
                Ok(())
            }
            fn decode(
                r: &mut $crate::codec::Reader<'_>,
                cx: &$crate::codec::DecodeContext,
            ) -> Result<Self, $crate::codec::DecodeError> {
                let _ = cx;
                Ok(match r.byte()? {
                    $(
                        $tag => $name::$variant
                            $( ( $( <$at as $crate::codec::Wire>::decode(r, cx)? ),* ) )?
                            $( { $( $f: <$ft as $crate::codec::Wire>::decode(r, cx)? ),* } )?,
                    )+
                    tag => {
                        return Err($crate::codec::DecodeError::UnknownTag {
                            what: $what,
                            tag: u32::from(tag),
                        })
                    }
                })
            }
        }
    )*};
}

pub(crate) use {record, tuple_record, union};

#[cfg(test)]
mod tests {
    use super::*;

    fn cx() -> (EncodeContext, DecodeContext) {
        let now = Instant::now();
        (EncodeContext::new(now), DecodeContext::new(now, None, None))
    }

    #[test]
    fn floats_keep_their_bits() {
        let (e, d) = cx();
        let nan = f32::from_bits(0x7FC0_1234);
        let back: f32 = from_bytes(&to_bytes(&nan, &e).unwrap(), &d).unwrap();
        assert_eq!(back.to_bits(), 0x7FC0_1234);
        let nan64 = f64::from_bits(0xFFF8_0000_DEAD_BEEF);
        let back: f64 = from_bytes(&to_bytes(&nan64, &e).unwrap(), &d).unwrap();
        assert_eq!(back.to_bits(), 0xFFF8_0000_DEAD_BEEF);
        assert_eq!(to_bytes(&-0.0f32, &e).unwrap(), (-0.0f32).to_le_bytes());
    }

    #[test]
    fn a_count_the_bytes_cannot_hold_allocates_nothing() {
        let (_, d) = cx();
        let huge = [0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3];
        assert!(matches!(
            from_bytes::<Vec<u32>>(&huge, &d),
            Err(DecodeError::CountTooLarge { left: 3, .. })
        ));
        assert!(matches!(
            from_bytes::<String>(&huge, &d),
            Err(DecodeError::CountTooLarge { .. })
        ));
        assert!(matches!(
            from_bytes::<BTreeMap<u32, String>>(&huge, &d),
            Err(DecodeError::CountTooLarge { .. })
        ));
    }

    #[test]
    fn maps_go_out_in_key_order_and_refuse_a_repeated_key() {
        let (e, d) = cx();
        let mut a: HashMap<u32, String> = HashMap::new();
        for k in [9, 3, 7, 1] {
            a.insert(k, k.to_string());
        }
        let bytes = to_bytes(&a, &e).unwrap();
        let sorted: BTreeMap<u32, String> = a.clone().into_iter().collect();
        assert_eq!(bytes, to_bytes(&sorted, &e).unwrap());
        assert_eq!(from_bytes::<HashMap<u32, String>>(&bytes, &d).unwrap(), a);
        let twice = [2, 0, 0, 0, 5, 0, 0, 0, 1, 5, 0, 0, 0, 0];
        assert_eq!(
            from_bytes::<BTreeMap<u32, bool>>(&twice, &d),
            Err(DecodeError::DuplicateKey)
        );
    }

    #[test]
    fn instants_travel_as_offsets_from_the_epoch() {
        let sender_epoch = Instant::now();
        let e = EncodeContext::new(sender_epoch);
        let receiver_epoch = sender_epoch + Duration::from_secs(1000);
        let d = DecodeContext::new(receiver_epoch, None, None);
        let later = sender_epoch + Duration::from_micros(2_500_001);
        let back: Instant = from_bytes(&to_bytes(&later, &e).unwrap(), &d).unwrap();
        assert_eq!(back - receiver_epoch, Duration::from_micros(2_500_001));
        let earlier = sender_epoch - Duration::from_micros(7);
        let back: Instant = from_bytes(&to_bytes(&earlier, &e).unwrap(), &d).unwrap();
        assert_eq!(receiver_epoch - back, Duration::from_micros(7));
        // An offset no clock can hold is an error, not a panic.
        let far = i64::MIN.to_le_bytes();
        assert_eq!(
            from_bytes::<Instant>(&far, &d),
            Err(DecodeError::OutOfRange("instant"))
        );
    }

    #[test]
    fn durations_are_exact_and_checked() {
        let (e, d) = cx();
        let value = Duration::new(12, 999_999_999);
        assert_eq!(
            from_bytes::<Duration>(&to_bytes(&value, &e).unwrap(), &d),
            Ok(value)
        );
        let mut bad = to_bytes(&Duration::ZERO, &e).unwrap();
        bad[8..12].copy_from_slice(&1_000_000_000u32.to_le_bytes());
        assert!(from_bytes::<Duration>(&bad, &d).is_err());
        let mut overflow = to_bytes(&Duration::ZERO, &e).unwrap();
        overflow[..8].copy_from_slice(&u64::MAX.to_le_bytes());
        overflow[8..12].copy_from_slice(&999_999_999u32.to_le_bytes());
        assert_eq!(
            from_bytes::<Duration>(&overflow, &d),
            Ok(Duration::new(u64::MAX, 999_999_999))
        );
    }

    #[test]
    fn the_shape_follows_order_and_type_not_names() {
        assert_ne!(<(u32, u16)>::SHAPE, <(u16, u32)>::SHAPE);
        assert_ne!(<Vec<u32>>::SHAPE, <Option<u32>>::SHAPE);
        assert_ne!(<[u8; 3]>::SHAPE, <[u8; 4]>::SHAPE);
        // The same layout under another type is the same shape.
        assert_eq!(<Box<u32>>::SHAPE, u32::SHAPE);
        assert_eq!(<HashMap<u32, bool>>::SHAPE, <BTreeMap<u32, bool>>::SHAPE);
    }

    /// Two fields of one type, encoded in either order.
    struct Ab {
        a: u32,
        b: u32,
    }
    struct Ba {
        a: u32,
        b: u32,
    }
    record! {
        Ab { a: u32, b: u32 }
        Ba { b: u32, a: u32 }
    }

    /// Two unit variants, each given the other's tag.
    enum Xy {
        X,
        Y,
    }
    enum Yx {
        X,
        Y,
    }
    union! {
        Xy: "xy" { 0 => X, 1 => Y }
        Yx: "yx" { 0 => Y, 1 => X }
    }

    #[test]
    fn the_shape_sees_swapped_fields_and_moved_tags() {
        let (e, _) = cx();
        // Bytes alike, meaning not: the shape must tell them apart.
        assert_eq!(
            to_bytes(&Ab { a: 1, b: 1 }, &e).unwrap(),
            to_bytes(&Ba { a: 1, b: 1 }, &e).unwrap()
        );
        assert_ne!(Ab::SHAPE, Ba::SHAPE);
        assert_eq!(to_bytes(&Xy::X, &e).unwrap(), [0]);
        assert_eq!(to_bytes(&Yx::Y, &e).unwrap(), [0]);
        assert_ne!(Xy::SHAPE, Yx::SHAPE);
    }

    #[cfg(target_pointer_width = "64")]
    #[test]
    fn a_count_past_u32_is_an_error_not_a_clamp() {
        let mut w = Writer::new();
        let past = u32::MAX as usize + 1;
        assert_eq!(
            put_count(&mut w, past),
            Err(EncodeError::CountTooLarge(past))
        );
        assert!(w.into_bytes().is_empty(), "nothing is written for it");
        let mut w = Writer::new();
        put_count(&mut w, u32::MAX as usize).unwrap();
        assert_eq!(w.into_bytes(), u32::MAX.to_le_bytes());
    }

    #[test]
    fn nesting_is_bounded() {
        let mut r = Reader::new(&[]);
        fn dive(r: &mut Reader<'_>) -> Result<(), DecodeError> {
            r.nested(dive)
        }
        assert_eq!(dive(&mut r), Err(DecodeError::TooDeep));
    }
}
