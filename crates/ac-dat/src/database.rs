use std::collections::BTreeSet;
#[cfg(not(target_arch = "wasm32"))]
use std::fs::File;
use std::marker::PhantomData;
use std::path::{Path, PathBuf};
#[cfg(not(target_arch = "wasm32"))]
use std::sync::Arc;
use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::btree::DatEntry;
#[cfg(not(target_arch = "wasm32"))]
use crate::btree::{Node, is_valid_offset};
use crate::cache::CacheStats;
#[cfg(not(target_arch = "wasm32"))]
use crate::cache::{DEFAULT_NODE_CACHE_LIMIT, NodeCache};
use crate::error::{DatError, Result};
#[cfg(not(target_arch = "wasm32"))]
use crate::header::DatFileType;
use crate::header::DatHeader;
use crate::objects::{DatObject, Iteration};
use crate::pack::{self, DatPack, Lookup, Read};

/// File id of the iteration record every dat carries.
pub const ITERATION_ID: u32 = 0xFFFF_0001;

/// One dat: header, directory and reads.
///
/// A dat has one of two backings:
///
/// - **A mapped file** ([`DatFile::open`], not on WebAssembly): the file is
///   mapped into memory read-only, and a read copies the bytes of a file's
///   blocks straight from the mapping, with no system call. The mapped
///   pages are the system's file cache, shared by every process that reads
///   the dats, not this process's own memory.
/// - **A pack** ([`DatFile::new_pack`]): single files by id, handed in by a
///   host that has no dat files (a browser), filled while the dat is read
///   (see [`DatPack`]). A pack has no directory: it answers each method from
///   the files it holds, as each method's documentation says. A read of a
///   file it does not hold yet answers "not found" and asks for the file.
///
/// `DatFile` is `Send + Sync`. Every read goes through `&self`; a mapped
/// dat keeps, besides the header and the directory's root node, a bounded,
/// thread-safe cache of the directory nodes lookups have read, so any
/// number of threads may read at once.
#[derive(Debug)]
pub struct DatFile {
    /// Empty for a pack.
    path: PathBuf,
    header: DatHeader,
    /// A mapped dat's iteration record; a pack's until it holds one.
    iteration: Iteration,
    backing: Backing,
    /// The ids read while recording; `None` (the default) when not
    /// recording, so a read then takes no lock for it.
    recorder: Option<Mutex<BTreeSet<u32>>>,
}

#[derive(Debug)]
enum Backing {
    #[cfg(not(target_arch = "wasm32"))]
    Mapped(Mapped),
    Pack(DatPack),
}

impl DatFile {
    /// Open a dat file of any kind.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        Self::open_with_node_cache(path, DEFAULT_NODE_CACHE_LIMIT)
    }

    /// Open a dat file keeping at most `node_limit` directory nodes besides
    /// the root (0: every lookup reads its nodes from the file).
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open_with_node_cache(path: impl AsRef<Path>, node_limit: usize) -> Result<Self> {
        let path = path.as_ref().to_path_buf();
        let (header, mapped) = Mapped::open(&path, node_limit)?;
        let mut dat = Self {
            path,
            header,
            iteration: Iteration::default(),
            backing: Backing::Mapped(mapped),
            recorder: None,
        };
        if let Some(iteration) = dat.get::<Iteration>(ITERATION_ID)? {
            dat.iteration = iteration;
        }
        Ok(dat)
    }

    /// Open a dat file and check that its header names the expected kind.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open_as(path: impl AsRef<Path>, expected: DatFileType) -> Result<Self> {
        let dat = Self::open(path)?;
        let found = dat.header.file_type;
        if found != expected {
            return Err(DatError::WrongType {
                path: dat.path,
                expected,
                found,
            });
        }
        Ok(dat)
    }

    /// An empty pack: the dat whose header is `header` (the header of the
    /// dat file the pack's files come from, which says the dat's kind and
    /// its master map), holding no files yet. Fill it through
    /// [`DatFile::as_pack`].
    #[must_use]
    pub fn new_pack(header: DatHeader) -> Self {
        Self {
            path: PathBuf::new(),
            header,
            iteration: Iteration::default(),
            backing: Backing::Pack(DatPack::new()),
            recorder: None,
        }
    }

    /// Start remembering the id of every file read from now on (see
    /// [`DatCollection::record_reads`](crate::DatCollection::record_reads)).
    /// Not on WebAssembly.
    #[cfg(not(target_arch = "wasm32"))]
    pub(crate) fn record_reads(&mut self) {
        self.recorder.get_or_insert_with(Mutex::default);
    }

    /// Whether reads are being recorded.
    #[cfg(not(target_arch = "wasm32"))]
    pub(crate) fn is_recording(&self) -> bool {
        self.recorder.is_some()
    }

    /// The ids recorded since the last take, ascending.
    #[cfg(not(target_arch = "wasm32"))]
    pub(crate) fn take_recorded(&self) -> Vec<u32> {
        self.recorder.as_ref().map_or_else(Vec::new, |recorder| {
            std::mem::take(&mut *lock(recorder)).into_iter().collect()
        })
    }

    /// Remember that file `id` was read, when recording. When not
    /// recording this is one test of an `Option`: no lock is taken.
    pub(crate) fn note_read(&self, id: u32) {
        if let Some(recorder) = &self.recorder {
            lock(recorder).insert(id);
        }
    }

    /// The pack behind this dat, to fill it and take the ids it was asked
    /// for; `None` for a mapped file.
    #[must_use]
    pub fn as_pack(&self) -> Option<&DatPack> {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(_) => None,
            Backing::Pack(pack) => Some(pack),
        }
    }

    /// Path the file was opened from; empty for a pack.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Length of the file on disk, in bytes; for a pack, the bytes of the
    /// files it holds, together.
    #[must_use]
    pub fn file_len(&self) -> u64 {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => mapped.file_len,
            Backing::Pack(pack) => pack.byte_len(),
        }
    }

    /// The decoded header; for a pack, the header it was made with.
    #[must_use]
    pub fn header(&self) -> &DatHeader {
        &self.header
    }

    /// The iteration record, or the default (iteration 1) when the dat has
    /// none. A pack answers the record once it holds file
    /// [`ITERATION_ID`], the default before.
    #[must_use]
    pub fn iteration(&self) -> &Iteration {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(_) => &self.iteration,
            Backing::Pack(pack) => pack.iteration().unwrap_or(&self.iteration),
        }
    }

    /// Look up a file's directory entry by id. A pack gives an entry for a
    /// file it holds, with the id and the size of its bytes (every other
    /// field zero), and `None` for any other id, asking for it unless the
    /// dat is known to have no such file.
    pub fn find_entry(&self, id: u32) -> Result<Option<DatEntry>> {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => mapped.find_entry(self.header.root_block, id),
            Backing::Pack(pack) => Ok(match pack.lookup(id) {
                Lookup::Held(size) => Some(DatEntry {
                    size,
                    ..pack::entry(id, 0)
                }),
                Lookup::Missing | Lookup::NotLoaded => None,
            }),
        }
    }

    /// What the dat knows of `id`: held, absent, or (a pack only) not
    /// loaded yet, in which case the pack now wants it.
    pub(crate) fn presence(&self, id: u32) -> Result<Presence> {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(_) => Ok(match self.find_entry(id)? {
                Some(_) => Presence::Held,
                None => Presence::Absent,
            }),
            Backing::Pack(pack) => Ok(match pack.lookup(id) {
                Lookup::Held(_) => Presence::Held,
                Lookup::Missing => Presence::Absent,
                Lookup::NotLoaded => Presence::NotLoaded,
            }),
        }
    }

    /// Every directory entry, in id order. A pack walks the files it holds
    /// now (entries as [`DatFile::find_entry`] gives them).
    #[must_use]
    pub fn entries(&self) -> Entries<'_> {
        let walk = match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => Walk::Tree {
                dat: mapped,
                stack: mapped
                    .root
                    .iter()
                    .map(|root| Frame {
                        node: Node::clone(root),
                        next: 0,
                        descended: false,
                    })
                    .collect(),
            },
            Backing::Pack(pack) => Walk::Pack(pack.entries().into_iter(), PhantomData),
        };
        Entries { walk }
    }

    /// Read the stored bytes of an entry. A pack reads the file of the
    /// entry's id; one it does not hold is [`DatError::NotLoaded`] (and is
    /// asked for unless known to be absent).
    pub fn read_entry(&self, entry: &DatEntry) -> Result<Vec<u8>> {
        let bytes = match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => mapped.read_entry(entry)?,
            Backing::Pack(pack) => match pack.read(entry.id) {
                Read::Held(bytes) => bytes,
                Read::Missing | Read::NotLoaded => {
                    return Err(DatError::NotLoaded { id: entry.id });
                }
            },
        };
        self.note_read(entry.id);
        Ok(bytes)
    }

    /// Read a file's bytes by id; `None` when the directory has no such
    /// file, and for a pack when it does not hold the file (it asks for
    /// it unless the dat is known to have none).
    pub fn get_bytes(&self, id: u32) -> Result<Option<Vec<u8>>> {
        let bytes = match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => match mapped.find_entry(self.header.root_block, id)? {
                Some(entry) => Some(mapped.read_entry(&entry)?),
                None => None,
            },
            Backing::Pack(pack) => match pack.read(id) {
                Read::Held(bytes) => Some(bytes),
                Read::Missing | Read::NotLoaded => None,
            },
        };
        if bytes.is_some() {
            self.note_read(id);
        }
        Ok(bytes)
    }

    /// Read and decode a typed object by id; `None` when the directory has
    /// no such file (or a pack does not hold it, as for
    /// [`DatFile::get_bytes`]). The object's id is set to the file id it
    /// was read from.
    pub fn get<T: DatObject>(&self, id: u32) -> Result<Option<T>> {
        let Some(bytes) = self.get_bytes(id)? else {
            return Ok(None);
        };
        let mut object =
            T::unpack_owned(bytes).map_err(|source| DatError::Decode { id, source })?;
        object.set_id(id);
        Ok(Some(object))
    }

    /// Hits, misses and evictions of the directory-node cache; none for a
    /// pack.
    #[must_use]
    pub fn node_cache_stats(&self) -> CacheStats {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => mapped.nodes.stats(),
            Backing::Pack(_) => CacheStats::default(),
        }
    }

    /// Directory nodes the cache holds (the root is kept apart); 0 for a
    /// pack.
    #[must_use]
    pub fn cached_nodes(&self) -> usize {
        match &self.backing {
            #[cfg(not(target_arch = "wasm32"))]
            Backing::Mapped(mapped) => mapped.nodes.len(),
            Backing::Pack(_) => 0,
        }
    }
}

fn lock(recorder: &Mutex<BTreeSet<u32>>) -> MutexGuard<'_, BTreeSet<u32>> {
    // An insert or a take leaves the set whole, so a poisoned lock still
    // holds a usable set.
    recorder.lock().unwrap_or_else(PoisonError::into_inner)
}

/// What a dat knows of one id (see `DatFile::presence`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Presence {
    /// The dat holds the file.
    Held,
    /// The dat has no such file.
    Absent,
    /// A pack does not know yet; it has asked for the file.
    NotLoaded,
}

/// A mapped dat file: the mapping and its directory.
#[cfg(not(target_arch = "wasm32"))]
#[derive(Debug)]
struct Mapped {
    map: memmap2::Mmap,
    file_len: u64,
    block_size: i32,
    root: Option<Arc<Node>>,
    nodes: NodeCache,
}

#[cfg(not(target_arch = "wasm32"))]
impl Mapped {
    /// Map the file at `path` and read its header and root node.
    fn open(path: &Path, node_limit: usize) -> Result<(DatHeader, Self)> {
        let io_error = |source| DatError::Io {
            path: path.to_path_buf(),
            source,
        };
        let file = File::open(path).map_err(io_error)?;
        let file_len = file.metadata().map_err(io_error)?.len();
        if file_len < DatHeader::SIZE as u64 {
            return Err(DatError::TooShort {
                path: path.to_path_buf(),
                len: file_len,
            });
        }
        let map = map_read_only(&file).map_err(io_error)?;
        // The mapping is the file's length when mapped; a file that changed
        // length since is read as mapped.
        let file_len = file_len.min(map.len() as u64);
        if file_len < DatHeader::SIZE as u64 {
            return Err(DatError::TooShort {
                path: path.to_path_buf(),
                len: file_len,
            });
        }
        let header = DatHeader::unpack(&map[..DatHeader::SIZE])?;
        if header.magic != DatHeader::RETAIL_MAGIC {
            return Err(DatError::BadMagic {
                path: path.to_path_buf(),
                magic: header.magic,
            });
        }
        // A block must hold its 4-byte next pointer and at least one data byte.
        if header.block_size <= 4 {
            return Err(DatError::BadBlockSize {
                path: path.to_path_buf(),
                block_size: header.block_size,
            });
        }
        let mut mapped = Self {
            map,
            file_len,
            block_size: header.block_size,
            root: None,
            nodes: NodeCache::new(node_limit),
        };
        if header.root_block != 0 {
            mapped.root = Some(Arc::new(mapped.read_node(header.root_block)?));
        }
        Ok((header, mapped))
    }

    fn find_entry(&self, root_block: i32, id: u32) -> Result<Option<DatEntry>> {
        let mut block = root_block;
        while is_valid_offset(block) {
            let node = self.cached_node(block)?;
            match node.search(id) {
                Ok(index) => return Ok(Some(node.files[index])),
                Err(_) if node.is_leaf() => break,
                Err(child) => block = node.child(child),
            }
        }
        Ok(None)
    }

    fn read_entry(&self, entry: &DatEntry) -> Result<Vec<u8>> {
        if entry.is_compressed() {
            return Err(DatError::Compressed { id: entry.id });
        }
        if u64::from(entry.size) > self.file_len {
            return Err(DatError::EntryTooLarge {
                id: entry.id,
                size: entry.size,
            });
        }
        self.read_chain(entry.offset, entry.size as usize)
    }

    /// The node at `offset`: the root, a cached node, or one read now and
    /// kept. A node that fails to decode is not kept.
    fn cached_node(&self, offset: i32) -> Result<Arc<Node>> {
        if let Some(root) = &self.root
            && root.offset == offset
        {
            return Ok(root.clone());
        }
        if let Some(node) = self.nodes.get(offset) {
            return Ok(node);
        }
        Ok(self.nodes.insert(Arc::new(self.read_node(offset)?)))
    }

    fn read_node(&self, offset: i32) -> Result<Node> {
        let bytes = self.read_chain(offset, Node::DISK_SIZE)?;
        Node::unpack(offset, &bytes)
    }

    /// Gather `len` bytes from the block chain starting at `start`.
    ///
    /// Each block begins with the offset of the next block (0 ends the
    /// chain); the remaining `block_size - 4` bytes are data. Only the bytes
    /// still needed are used from the last block. The bytes are copied from
    /// the mapping into one buffer of exactly `len` bytes.
    fn read_chain(&self, start: i32, len: usize) -> Result<Vec<u8>> {
        let data_per_block = self.block_size as usize - 4;
        let mut out = Vec::with_capacity(len);
        let mut block = start;
        while block != 0 && out.len() < len {
            let take = data_per_block.min(len - out.len());
            let need = (4 + take) as u64;
            let Some(offset) = u64::try_from(block)
                .ok()
                .filter(|&offset| offset + need <= self.file_len)
            else {
                return Err(DatError::BlockOutOfRange {
                    offset: i64::from(block),
                    file_len: self.file_len,
                });
            };
            // In range of the mapping: `file_len` is at most its length.
            let at = offset as usize;
            let span = &self.map[at..at + 4 + take];
            out.extend_from_slice(&span[4..]);
            block = i32::from_le_bytes([span[0], span[1], span[2], span[3]]);
        }
        if out.len() < len {
            return Err(DatError::ChainEnded {
                start,
                needed: len,
                read: out.len(),
            });
        }
        Ok(out)
    }
}

/// Map `file` into memory, read-only.
///
/// A mapped file is memory another process could change under a slice of
/// it, which Rust's aliasing rules do not allow for; hence `unsafe`. Here:
/// the mapping is only ever read, by copying bytes out of it, never by
/// handing out references that outlive a read; every byte copied is then
/// decoded by bounds-checked readers, so changed bytes can only make a
/// decode fail or give other values, as a file changed between two
/// `read` calls would. The dats are not written while the game reads them
/// (the game's own patcher runs before it), and on Windows a mapped file
/// cannot be shortened, so its pages stay valid for the mapping's life.
#[cfg(not(target_arch = "wasm32"))]
#[allow(unsafe_code)]
fn map_read_only(file: &File) -> std::io::Result<memmap2::Mmap> {
    // SAFETY: see above: the mapping is read-only, read by copying, and
    // the file is not written or shortened while the game runs.
    unsafe { memmap2::Mmap::map(file) }
}

/// In-order walk of a dat's directory; see [`DatFile::entries`].
///
/// Yields an error and stops if a node cannot be read.
#[derive(Debug)]
pub struct Entries<'a> {
    walk: Walk<'a>,
}

#[derive(Debug)]
enum Walk<'a> {
    #[cfg(not(target_arch = "wasm32"))]
    Tree { dat: &'a Mapped, stack: Vec<Frame> },
    /// The entries a pack held when the walk began (the walk borrows the
    /// dat as a directory walk does).
    Pack(std::vec::IntoIter<DatEntry>, PhantomData<&'a DatFile>),
}

#[cfg(not(target_arch = "wasm32"))]
#[derive(Debug)]
struct Frame {
    node: Node,
    /// Next entry to yield.
    next: usize,
    /// Whether the child before entry `next` has been walked.
    descended: bool,
}

impl Iterator for Entries<'_> {
    type Item = Result<DatEntry>;

    fn next(&mut self) -> Option<Self::Item> {
        match &mut self.walk {
            #[cfg(not(target_arch = "wasm32"))]
            Walk::Tree { dat, stack } => walk_tree(dat, stack),
            Walk::Pack(entries, _) => entries.next().map(Ok),
        }
    }
}

#[cfg(not(target_arch = "wasm32"))]
fn walk_tree(dat: &Mapped, stack: &mut Vec<Frame>) -> Option<Result<DatEntry>> {
    loop {
        let top = stack.last_mut()?;
        if !top.node.is_leaf() && !top.descended {
            top.descended = true;
            let child = top.node.child(top.next);
            let parent = top.node.offset;
            if !is_valid_offset(child) {
                stack.clear();
                return Some(Err(DatError::BadChild {
                    offset: parent,
                    child,
                }));
            }
            match dat.read_node(child) {
                Ok(node) => stack.push(Frame {
                    node,
                    next: 0,
                    descended: false,
                }),
                Err(error) => {
                    stack.clear();
                    return Some(Err(error));
                }
            }
            continue;
        }
        if let Some(&entry) = top.node.files.get(top.next) {
            top.next += 1;
            top.descended = false;
            return Some(Ok(entry));
        }
        stack.pop();
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::btree::tests::{entry, node_bytes};
    use std::io::Write;

    const _: () = {
        const fn assert_send_sync<T: Send + Sync>() {}
        assert_send_sync::<DatFile>();
    };

    /// A tiny in-memory dat builder: 64-byte blocks, header in the first
    /// blocks, then whatever chains the test lays out.
    pub(crate) struct DatBuilder {
        bytes: Vec<u8>,
        block_size: usize,
    }

    impl DatBuilder {
        pub(crate) fn new(block_size: usize) -> Self {
            let header_blocks = DatHeader::SIZE.div_ceil(block_size);
            Self {
                bytes: vec![0; header_blocks * block_size],
                block_size,
            }
        }

        /// Append `data` as a fresh chain; returns its first block offset.
        pub(crate) fn chain(&mut self, data: &[u8]) -> i32 {
            let per = self.block_size - 4;
            let blocks = data.len().div_ceil(per).max(1);
            let start = self.bytes.len();
            for i in 0..blocks {
                let next = if i + 1 < blocks {
                    (start + (i + 1) * self.block_size) as i32
                } else {
                    0
                };
                self.bytes.extend_from_slice(&next.to_le_bytes());
                let chunk = &data[(i * per).min(data.len())..((i + 1) * per).min(data.len())];
                self.bytes.extend_from_slice(chunk);
                self.bytes.extend(std::iter::repeat_n(0, per - chunk.len()));
            }
            start as i32
        }

        /// Append `data` as a chain whose blocks lie on disk in reverse
        /// order (every next pointer jumps backwards); returns its first
        /// block offset.
        fn scattered_chain(&mut self, data: &[u8]) -> i32 {
            let per = self.block_size - 4;
            let blocks = data.len().div_ceil(per).max(1);
            let base = self.bytes.len();
            let at = |i: usize| base + (blocks - 1 - i) * self.block_size;
            self.bytes.resize(base + blocks * self.block_size, 0);
            for i in 0..blocks {
                let next = if i + 1 < blocks { at(i + 1) as i32 } else { 0 };
                let chunk = &data[(i * per).min(data.len())..((i + 1) * per).min(data.len())];
                let o = at(i);
                self.bytes[o..o + 4].copy_from_slice(&next.to_le_bytes());
                self.bytes[o + 4..o + 4 + chunk.len()].copy_from_slice(chunk);
            }
            at(0) as i32
        }

        /// Append `data` as a chain whose block `i` lies at slot `slots[i]`
        /// of the space it takes; returns its first block offset.
        fn chain_in_slots(&mut self, data: &[u8], slots: &[usize]) -> i32 {
            let per = self.block_size - 4;
            assert_eq!(slots.len(), data.len().div_ceil(per));
            let base = self.bytes.len();
            let at = |i: usize| base + slots[i] * self.block_size;
            self.bytes.resize(base + slots.len() * self.block_size, 0);
            for i in 0..slots.len() {
                let next = if i + 1 < slots.len() {
                    at(i + 1) as i32
                } else {
                    0
                };
                let chunk = &data[i * per..((i + 1) * per).min(data.len())];
                let o = at(i);
                self.bytes[o..o + 4].copy_from_slice(&next.to_le_bytes());
                self.bytes[o + 4..o + 4 + chunk.len()].copy_from_slice(chunk);
            }
            at(0) as i32
        }

        pub(crate) fn finish(mut self, file_type: u32, root: i32) -> Vec<u8> {
            let mut header = crate::header::tests::sample_header_bytes();
            header[324..328].copy_from_slice(&(self.block_size as i32).to_le_bytes());
            header[328..332].copy_from_slice(&(self.bytes.len() as i32).to_le_bytes());
            header[332..336].copy_from_slice(&file_type.to_le_bytes());
            header[352..356].copy_from_slice(&root.to_le_bytes());
            self.bytes[..DatHeader::SIZE].copy_from_slice(&header);
            self.bytes
        }
    }

    fn write_temp(name: &str, bytes: &[u8]) -> PathBuf {
        let path =
            std::env::temp_dir().join(format!("ac-dat-test-{}-{name}.dat", std::process::id()));
        let mut file = File::create(&path).unwrap();
        file.write_all(bytes).unwrap();
        path
    }

    /// A two-level directory: root with entries 20 and 40, three leaves.
    fn two_level_dat() -> (Vec<u8>, Vec<u32>) {
        let mut b = DatBuilder::new(64);
        let payload: Vec<u8> = (0u8..150).collect();
        let data = b.chain(&payload);
        let small = b.chain(b"hello");
        let leaf =
            |ids: &[u32]| -> Vec<DatEntry> { ids.iter().map(|&id| entry(id, small, 5)).collect() };
        let left = b.chain(&node_bytes(&[], &leaf(&[5, 10])));
        let mid = b.chain(&node_bytes(&[], &[entry(30, data, 150)]));
        let right = b.chain(&node_bytes(&[], &leaf(&[50, 60, 70])));
        let root = b.chain(&node_bytes(
            &[left, mid, right],
            &[entry(20, small, 5), entry(40, small, 5)],
        ));
        (b.finish(2, root), vec![5, 10, 20, 30, 40, 50, 60, 70])
    }

    #[test]
    fn finds_entries_at_every_level_and_reads_chains() {
        let (bytes, ids) = two_level_dat();
        let path = write_temp("two-level", &bytes);
        let dat = DatFile::open_as(&path, DatFileType::Cell).unwrap();
        for id in &ids {
            assert_eq!(dat.find_entry(*id).unwrap().map(|e| e.id), Some(*id));
        }
        for missing in [0, 1, 15, 25, 45, 80, u32::MAX] {
            assert_eq!(dat.find_entry(missing).unwrap(), None);
        }
        // 150 bytes across three 60-byte data blocks.
        let bytes30 = dat.get_bytes(30).unwrap().unwrap();
        assert_eq!(bytes30, (0u8..150).collect::<Vec<_>>());
        assert_eq!(dat.get_bytes(60).unwrap().unwrap(), b"hello");
        assert_eq!(dat.get_bytes(61).unwrap(), None);
        // No iteration record: the default.
        assert_eq!(dat.iteration().current_iteration, 1);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn repeated_lookups_read_cached_nodes() {
        let (bytes, ids) = two_level_dat();
        let path = write_temp("node-cache", &bytes);
        let dat = DatFile::open(&path).unwrap();
        // Lookups of 5 and 50 visit the root (kept apart) and one leaf each.
        for id in [5, 50] {
            dat.find_entry(id).unwrap();
        }
        assert_eq!(dat.cached_nodes(), 2);
        let before = dat.node_cache_stats();
        for id in &ids {
            assert_eq!(dat.find_entry(*id).unwrap().map(|e| e.id), Some(*id));
        }
        let after = dat.node_cache_stats();
        // 20 and 40 sit in the root; the other six ids each read one leaf,
        // and only the middle leaf (30) is new.
        assert_eq!(after.hits - before.hits, 5);
        assert_eq!(after.misses - before.misses, 1);
        assert_eq!(dat.cached_nodes(), 3);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn the_node_cache_stays_within_its_limit_and_answers_the_same() {
        let (bytes, ids) = two_level_dat();
        let path = write_temp("node-limit", &bytes);
        let uncached = DatFile::open_with_node_cache(&path, 0).unwrap();
        let one = DatFile::open_with_node_cache(&path, 1).unwrap();
        for _ in 0..3 {
            for id in ids.iter().copied().chain([0, 15, 45, 80, u32::MAX]) {
                assert_eq!(
                    one.find_entry(id).unwrap(),
                    uncached.find_entry(id).unwrap()
                );
                assert!(one.cached_nodes() <= 1);
            }
        }
        assert_eq!(uncached.cached_nodes(), 0);
        assert!(one.node_cache_stats().evictions > 0);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn concurrent_lookups_through_a_small_node_cache_agree() {
        let (bytes, ids) = two_level_dat();
        let path = write_temp("node-threads", &bytes);
        let dat = DatFile::open_with_node_cache(&path, 2).unwrap();
        let reference = DatFile::open_with_node_cache(&path, 0).unwrap();
        let expected: Vec<_> = (0..80)
            .map(|id| reference.find_entry(id).unwrap())
            .collect();
        std::thread::scope(|scope| {
            for t in 0..8u32 {
                let (dat, expected) = (&dat, &expected);
                scope.spawn(move || {
                    for round in 0..200u32 {
                        let id = (t * 7 + round * 13) % 80;
                        assert_eq!(dat.find_entry(id).unwrap(), expected[id as usize]);
                        assert!(dat.cached_nodes() <= 2);
                    }
                });
            }
        });
        for id in &ids {
            assert_eq!(
                dat.get_bytes(*id).unwrap(),
                reference.get_bytes(*id).unwrap()
            );
        }
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn entries_walk_in_id_order() {
        let (bytes, ids) = two_level_dat();
        let path = write_temp("walk", &bytes);
        let dat = DatFile::open(&path).unwrap();
        let walked: Vec<u32> = dat.entries().map(|e| e.unwrap().id).collect();
        assert_eq!(walked, ids);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn empty_directory_has_no_entries() {
        let b = DatBuilder::new(64);
        let path = write_temp("empty", &b.finish(1, 0));
        let dat = DatFile::open(&path).unwrap();
        assert_eq!(dat.entries().count(), 0);
        assert_eq!(dat.find_entry(1).unwrap(), None);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn wrong_type_bad_magic_and_short_files_are_errors() {
        let (mut bytes, _) = two_level_dat();
        let path = write_temp("wrong-type", &bytes);
        assert!(matches!(
            DatFile::open_as(&path, DatFileType::Portal),
            Err(DatError::WrongType {
                found: DatFileType::Cell,
                ..
            })
        ));
        bytes[320] = 0;
        let path2 = write_temp("bad-magic", &bytes);
        assert!(matches!(
            DatFile::open(&path2),
            Err(DatError::BadMagic { .. })
        ));
        let path3 = write_temp("short", &bytes[..100]);
        assert!(matches!(
            DatFile::open(&path3),
            Err(DatError::TooShort { len: 100, .. })
        ));
        for p in [path, path2, path3] {
            std::fs::remove_file(p).ok();
        }
    }

    #[test]
    fn broken_chains_are_errors() {
        let mut b = DatBuilder::new(64);
        let data = b.chain(b"abc");
        let leaf = b.chain(&node_bytes(
            &[],
            &[
                entry(1, data, 3),
                entry(2, data, 200),      // chain too short
                entry(3, 0x7FFF_0000, 3), // past the end of the file
                entry(4, -64, 3),         // negative offset
                entry(5, data, u32::MAX), // larger than the file
            ],
        ));
        let path = write_temp("broken", &b.finish(1, leaf));
        let dat = DatFile::open(&path).unwrap();
        assert_eq!(dat.get_bytes(1).unwrap().unwrap(), b"abc");
        assert!(matches!(
            dat.get_bytes(2),
            Err(DatError::ChainEnded {
                needed: 200,
                read: 60,
                ..
            })
        ));
        assert!(matches!(
            dat.get_bytes(3),
            Err(DatError::BlockOutOfRange { .. })
        ));
        assert!(matches!(
            dat.get_bytes(4),
            Err(DatError::BlockOutOfRange { .. })
        ));
        assert!(matches!(
            dat.get_bytes(5),
            Err(DatError::EntryTooLarge { .. })
        ));
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn chains_that_jump_and_chains_at_the_end_of_the_file_read_whole() {
        let mut b = DatBuilder::new(64);
        let long: Vec<u8> = (0..1000u32).map(|i| (i * 7 % 251) as u8).collect();
        let scattered = b.scattered_chain(&long);
        let contiguous = b.chain(&long);
        // The leaf comes next; the last chain of the file follows it.
        let leaf_len = Node::DISK_SIZE.div_ceil(60) * 64;
        let tail = (b.bytes.len() + leaf_len) as i32;
        let leaf = b.chain(&node_bytes(
            &[],
            &[
                entry(1, scattered, 1000),
                entry(2, contiguous, 1000),
                entry(3, tail, 10),
            ],
        ));
        assert_eq!(b.chain(b"0123456789"), tail);
        // The file ends right after the bytes the tail's block holds.
        let mut bytes = b.finish(1, leaf);
        bytes.truncate(tail as usize + 4 + 10);
        let path = write_temp("runs", &bytes);
        let dat = DatFile::open(&path).unwrap();
        assert_eq!(dat.get_bytes(1).unwrap().unwrap(), long);
        assert_eq!(dat.get_bytes(2).unwrap().unwrap(), long);
        assert_eq!(dat.get_bytes(3).unwrap().unwrap(), b"0123456789");
        std::fs::remove_file(path).ok();
    }

    /// The blocks are gathered in place over the run they were read with:
    /// a chain that steps back to a block of the run whose bytes the
    /// gathering has already covered reads that block again from the file.
    #[test]
    fn chains_that_step_back_inside_their_run_read_whole() {
        let mut b = DatBuilder::new(64);
        let long: Vec<u8> = (0..300u32).map(|i| (i * 13 % 251) as u8).collect();
        // Five blocks: forward over slots 1 and 3, back to 2 (covered by
        // then), forward to 4, back to the first slot.
        let stepping = b.chain_in_slots(&long, &[1, 3, 2, 4, 0]);
        // Forward to the run's far end, then back to a slot not yet covered.
        let skipping = b.chain_in_slots(&long[..180], &[0, 2, 1]);
        let leaf = b.chain(&node_bytes(
            &[],
            &[entry(1, stepping, 300), entry(2, skipping, 180)],
        ));
        let path = write_temp("step-back", &b.finish(1, leaf));
        let dat = DatFile::open(&path).unwrap();
        assert_eq!(dat.get_bytes(1).unwrap().unwrap(), long);
        assert_eq!(dat.get_bytes(2).unwrap().unwrap(), &long[..180]);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn compressed_entries_are_refused() {
        let mut b = DatBuilder::new(64);
        let data = b.chain(b"abc");
        let mut e = entry(1, data, 3);
        e.flags = 1;
        let leaf = b.chain(&node_bytes(&[], &[e]));
        let path = write_temp("compressed", &b.finish(1, leaf));
        let dat = DatFile::open(&path).unwrap();
        assert!(matches!(
            dat.get_bytes(1),
            Err(DatError::Compressed { id: 1 })
        ));
        std::fs::remove_file(path).ok();
    }

    /// A header of the given kind (the sample header's other fields).
    pub(crate) fn pack_header(file_type: DatFileType) -> DatHeader {
        let mut header = DatHeader::unpack(&crate::header::tests::sample_header_bytes()).unwrap();
        header.file_type = file_type;
        header
    }

    const _: () = {
        const fn assert_send_sync<T: Send + Sync>() {}
        assert_send_sync::<DatPack>();
    };

    #[test]
    fn a_pack_reads_the_files_put_into_it() {
        let dat = DatFile::new_pack(pack_header(DatFileType::Portal));
        let pack = dat.as_pack().unwrap();
        pack.insert(7, b"seven".to_vec());
        assert_eq!(dat.get_bytes(7).unwrap().unwrap(), b"seven");
        let entry = dat.find_entry(7).unwrap().unwrap();
        assert_eq!((entry.id, entry.size), (7, 5));
        assert_eq!(dat.read_entry(&entry).unwrap(), b"seven");
        assert!(pack.take_wanted().is_empty());
    }

    #[test]
    fn a_pack_asks_once_for_each_file_it_lacks() {
        let dat = DatFile::new_pack(pack_header(DatFileType::Portal));
        let pack = dat.as_pack().unwrap();
        assert!(pack.take_wanted().is_empty());
        // Not loaded reads as not found, as a mapped dat answers an absent id.
        assert_eq!(dat.get_bytes(9).unwrap(), None);
        assert_eq!(dat.get_bytes(9).unwrap(), None);
        assert_eq!(dat.find_entry(9).unwrap(), None);
        assert_eq!(dat.get::<Iteration>(3).unwrap(), None);
        assert_eq!(pack.take_wanted(), [3, 9]);
        assert!(pack.take_wanted().is_empty());
        // Asked for and not answered yet: not asked for again.
        assert_eq!(dat.get_bytes(9).unwrap(), None);
        assert!(pack.take_wanted().is_empty());
        // The host has no such file: never asked for again.
        pack.mark_missing(9);
        assert_eq!(dat.get_bytes(9).unwrap(), None);
        assert_eq!(dat.find_entry(9).unwrap(), None);
        assert!(pack.take_wanted().is_empty());
        // The bytes arrive: found from then on.
        pack.insert(3, b"abc".to_vec());
        assert_eq!(dat.get_bytes(3).unwrap().unwrap(), b"abc");
        assert!(pack.take_wanted().is_empty());
        // Wanted and answered before it was taken: not asked for.
        assert_eq!(dat.get_bytes(20).unwrap(), None);
        pack.insert(20, b"x".to_vec());
        assert_eq!(dat.get_bytes(21).unwrap(), None);
        pack.mark_missing(21);
        assert!(pack.take_wanted().is_empty());
    }

    #[test]
    fn a_pack_asks_for_ids_in_ascending_order_once_across_threads() {
        let dat = DatFile::new_pack(pack_header(DatFileType::Local));
        std::thread::scope(|scope| {
            for t in 0..8u32 {
                let dat = &dat;
                scope.spawn(move || {
                    for round in 0..100u32 {
                        let id = (t * 7 + round * 13) % 50;
                        assert_eq!(dat.get_bytes(id).unwrap(), None);
                    }
                });
            }
        });
        assert_eq!(
            dat.as_pack().unwrap().take_wanted(),
            (0..50).collect::<Vec<_>>()
        );
    }

    #[test]
    fn a_pack_answers_the_directory_questions_from_what_it_holds() {
        let header = pack_header(DatFileType::Portal);
        let dat = DatFile::new_pack(header.clone());
        let pack = dat.as_pack().unwrap();
        assert_eq!(dat.header(), &header);
        assert_eq!(dat.path(), Path::new(""));
        assert_eq!(dat.file_len(), 0);
        assert_eq!(dat.entries().count(), 0);
        assert_eq!(dat.iteration(), &Iteration::default());
        pack.insert(30, vec![1; 10]);
        pack.insert(10, vec![2; 4]);
        pack.mark_missing(20);
        let walked: Vec<_> = dat.entries().map(|e| e.unwrap()).collect();
        assert_eq!(
            walked.iter().map(|e| (e.id, e.size)).collect::<Vec<_>>(),
            [(10, 4), (30, 10)]
        );
        assert_eq!(dat.file_len(), 14);
        assert_eq!((pack.len(), pack.is_empty()), (2, false));
        assert!(pack.contains(10) && !pack.contains(20) && !pack.contains(40));
        // `contains` only looks: it asks for nothing.
        assert!(pack.take_wanted().is_empty());
        assert_eq!(dat.node_cache_stats(), CacheStats::default());
        assert_eq!(dat.cached_nodes(), 0);
        // An entry for a file the pack does not hold reads as not loaded.
        let absent = DatEntry {
            id: 40,
            ..walked[0]
        };
        assert!(matches!(
            dat.read_entry(&absent),
            Err(DatError::NotLoaded { id: 40 })
        ));
        assert_eq!(pack.take_wanted(), [40]);
        // The iteration record is the pack's file of that id once it arrives.
        let mut record = Vec::new();
        for value in [5i32, -5, 1] {
            record.extend_from_slice(&value.to_le_bytes());
        }
        pack.insert(ITERATION_ID, record);
        assert_eq!(dat.iteration().current_iteration, 5);
    }

    #[test]
    fn a_mapped_dat_is_not_a_pack() {
        let (bytes, _) = two_level_dat();
        let path = write_temp("not-a-pack", &bytes);
        let dat = DatFile::open(&path).unwrap();
        assert!(dat.as_pack().is_none());
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn walk_reports_an_invalid_child() {
        let mut b = DatBuilder::new(64);
        let leaf = b.chain(&node_bytes(&[], &[entry(1, 0, 0)]));
        // Two entries but the second child slot is the fill pattern.
        let root = b.chain(&node_bytes(
            &[leaf, 0xCDCD_CDCDu32 as i32],
            &[entry(5, 0, 0), entry(9, 0, 0)],
        ));
        let path = write_temp("bad-child", &b.finish(1, root));
        let dat = DatFile::open(&path).unwrap();
        let walked: Vec<_> = dat.entries().collect();
        assert_eq!(walked.len(), 3);
        assert_eq!(walked[0].as_ref().unwrap().id, 1);
        assert_eq!(walked[1].as_ref().unwrap().id, 5);
        assert!(matches!(
            walked[2],
            Err(DatError::BadChild { child: 0, .. })
        ));
        // Lookup down the missing child simply finds nothing.
        assert_eq!(dat.find_entry(7).unwrap(), None);
        std::fs::remove_file(path).ok();
    }
}
