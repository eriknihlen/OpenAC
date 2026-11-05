//! Reads the game's dat files.
//!
//! A dat file is a 400-byte [`DatHeader`], then fixed-size blocks. Every
//! stored item (a directory node or a file) is a chain of blocks: each block
//! starts with the offset of the next one, and the rest is data. The
//! directory is a B-tree of [`DatEntry`] records keyed by file id; an entry
//! gives the offset of the file's first block and its size.
//!
//! [`DatFile`] opens one dat; [`DatCollection`] opens the four a client
//! uses. Typed objects ([`objects`]) decode from a file's bytes.
//!
//! [`DatFile`] is `Send + Sync`: reads go by file position through `&self`,
//! so many threads may read one open dat at once.

mod btree;
pub mod cache;
mod collection;
mod database;
mod error;
mod header;
pub mod objects;
pub mod texture;
pub mod types;

pub use btree::DatEntry;
pub use collection::{
    CELL_DAT, CacheLimit, CachedDats, DatCollection, DatSource, HIGHRES_DAT, LOCAL_DAT,
    ObjectCacheLimits, PORTAL_DAT, default_dat_dir, preferred_source,
};
pub use database::{DatFile, Entries, ITERATION_ID};
pub use error::{DatError, DecodeError, Result};
pub use header::{DatFileType, DatHeader};
pub use objects::DatObject;
