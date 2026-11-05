use std::path::PathBuf;

use ac_bits::BitsError;

use crate::header::DatFileType;

/// Errors from opening a dat file or reading its container structures.
#[derive(Debug, thiserror::Error)]
pub enum DatError {
    /// The operating system failed to open or read the file.
    #[error("i/o error on {path}: {source}")]
    Io {
        /// The dat file.
        path: PathBuf,
        /// The underlying error.
        #[source]
        source: std::io::Error,
    },
    /// The file is too short to hold a dat header.
    #[error("{path} is too short for a dat header ({len} bytes)")]
    TooShort {
        /// The dat file.
        path: PathBuf,
        /// Its length in bytes.
        len: u64,
    },
    /// The header magic is not the retail dat magic.
    #[error("{path} is not a dat file (magic {magic:#010x})")]
    BadMagic {
        /// The dat file.
        path: PathBuf,
        /// The magic that was found.
        magic: i32,
    },
    /// The dat is of a different kind than the caller asked for.
    #[error("{path} is a {found:?} dat, expected {expected:?}")]
    WrongType {
        /// The dat file.
        path: PathBuf,
        /// The kind that was asked for.
        expected: DatFileType,
        /// The kind the header names.
        found: DatFileType,
    },
    /// The header's block size cannot hold a next-block pointer and data.
    #[error("{path} has an unusable block size {block_size}")]
    BadBlockSize {
        /// The dat file.
        path: PathBuf,
        /// The block size from the header.
        block_size: i32,
    },
    /// A block offset points outside the file.
    #[error("block at offset {offset:#x} is outside the file ({file_len} bytes)")]
    BlockOutOfRange {
        /// The block offset.
        offset: i64,
        /// The file length.
        file_len: u64,
    },
    /// A block chain ended before it delivered the bytes it should hold.
    #[error("block chain starting at {start:#x} ended after {read} of {needed} bytes")]
    ChainEnded {
        /// First block of the chain.
        start: i32,
        /// Bytes the chain should hold.
        needed: usize,
        /// Bytes it held.
        read: usize,
    },
    /// A directory node claims more entries than a node can hold.
    #[error("directory node at {offset:#x} claims {count} entries")]
    BadNodeEntryCount {
        /// The node's block offset.
        offset: i32,
        /// The entry count it claims.
        count: i32,
    },
    /// A directory node points at a child that cannot exist.
    #[error("directory node at {offset:#x} has an invalid child offset {child:#x}")]
    BadChild {
        /// The node's block offset.
        offset: i32,
        /// The child offset.
        child: i32,
    },
    /// A directory entry claims to be larger than the whole file.
    #[error("entry {id:#010x} claims {size} bytes, more than the file holds")]
    EntryTooLarge {
        /// The entry's file id.
        id: u32,
        /// The size it claims.
        size: u32,
    },
    /// The entry is stored compressed, which this reader does not decode.
    #[error("entry {id:#010x} is stored compressed; compressed entries are not supported")]
    Compressed {
        /// The entry's file id.
        id: u32,
    },
    /// The header bytes could not be decoded.
    #[error("bad dat header: {0}")]
    Header(#[from] BitsError),
    /// A typed object could not be decoded from its bytes.
    #[error("cannot decode object {id:#010x}: {source}")]
    Decode {
        /// The object's file id.
        id: u32,
        /// What went wrong.
        #[source]
        source: DecodeError,
    },
}

/// Errors from decoding a typed object from its bytes.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum DecodeError {
    /// The bytes ended early.
    #[error(transparent)]
    Bits(#[from] BitsError),
    /// A table keyed by id holds the same key twice.
    #[error("{table} holds key {key:#x} twice")]
    DuplicateKey {
        /// Which table.
        table: &'static str,
        /// The repeated key.
        key: i64,
    },
    /// A table keyed by text holds the same key twice.
    #[error("{table} holds key {key:?} twice")]
    DuplicateName {
        /// Which table.
        table: &'static str,
        /// The repeated key.
        key: String,
    },
    /// A string of 16-bit units is not valid UTF-16.
    #[error("text is not valid UTF-16")]
    BadUtf16,
    /// The object uses a layout this reader does not decode.
    #[error("unsupported layout: {0}")]
    Unsupported(&'static str),
    /// A vertex array that holds vertices names a layout other than the one
    /// known layout.
    #[error("unknown vertex type {0}")]
    UnknownVertexType(i32),
    /// A stored length is negative.
    #[error("negative length {0}")]
    NegativeLength(i32),
    /// A UI property names a master property id the master property table
    /// does not define, so its value type is unknown.
    #[error("unknown UI property {0:#010x}")]
    UnknownProperty(u32),
    /// A UI property's master entry has a value type this reader does not
    /// decode.
    #[error("unsupported UI property type {0}")]
    UnsupportedPropertyType(u32),
    /// A media entry of a UI state has a kind this reader does not decode.
    #[error("unsupported UI media type {0}")]
    UnsupportedMediaType(i32),
    /// Nested elements or properties go deeper than any real file does.
    #[error("{0} nest too deeply")]
    TooDeep(&'static str),
}

/// Result of a container operation.
pub type Result<T> = core::result::Result<T, DatError>;
