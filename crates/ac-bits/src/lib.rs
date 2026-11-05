//! Little-endian binary reading and writing shared by the dat and wire formats.
//!
//! [`Reader`] walks a borrowed byte slice and never panics on short input:
//! every read returns [`Result`]. [`Writer`] appends to a growable buffer.
//! Both know the few variable-length encodings the game uses (packed and
//! compressed integers, length-prefixed strings with 4-byte padding).
//! Strings on the wire and in the dats are Windows-1252; [`cp1252`] converts.
//! [`png`] writes RGBA pixels as a PNG file (texture dumps, screenshots).

pub mod cp1252;
pub mod png;
mod reader;
mod writer;

pub use reader::Reader;
pub use writer::Writer;

/// Errors from reading or writing binary data.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum BitsError {
    /// A read needed more bytes than remain.
    #[error(
        "unexpected end of data at offset {offset}: needed {needed} bytes, {available} available"
    )]
    UnexpectedEnd {
        /// Position of the read that failed.
        offset: usize,
        /// Bytes the read needed.
        needed: usize,
        /// Bytes that were left.
        available: usize,
    },
    /// A value does not fit the encoding it is being written with.
    #[error("{value:#x} does not fit in a {encoding}")]
    ValueOutOfRange {
        /// Name of the encoding.
        encoding: &'static str,
        /// The value that did not fit.
        value: u64,
    },
}

/// Shorthand for results in this crate.
pub type Result<T> = core::result::Result<T, BitsError>;
