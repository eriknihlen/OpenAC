//! A dat held in memory as single files, for a process with no dat files
//! on disk (a browser): the host fetches files by id and hands their bytes
//! in, and the reader says which ids it asked for and did not have.

use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::sync::{Mutex, MutexGuard, OnceLock, PoisonError};

use ac_bits::Reader;

use crate::btree::DatEntry;
use crate::database::ITERATION_ID;
use crate::objects::{DatObject, Iteration};

/// The files of one dat, by id, each exactly as a mapped dat's
/// [`DatFile::get_bytes`](crate::DatFile::get_bytes) returns it (the
/// entry's data, its block chain already joined).
///
/// A pack can be filled after it was made, through `&self`, while readers
/// read it: [`DatPack::insert`] adds a file, [`DatPack::mark_missing`]
/// records that the dat has no such file. A read of an id the pack has
/// neither answers "not found", as a mapped dat answers for an absent id,
/// and records the id as wanted; [`DatPack::take_wanted`] hands those ids
/// to whoever fetches them. `Send + Sync`.
#[derive(Debug, Default)]
pub struct DatPack {
    state: Mutex<PackState>,
    /// The iteration record, decoded from the first bytes inserted for
    /// [`ITERATION_ID`] that decode.
    iteration: OnceLock<Iteration>,
}

#[derive(Debug, Default)]
struct PackState {
    files: BTreeMap<u32, Vec<u8>>,
    /// The bytes of `files` together.
    bytes: u64,
    /// Ids the dat has no file for.
    missing: HashSet<u32>,
    /// Ids read and not held, not yet taken.
    wanted: BTreeSet<u32>,
    /// Ids taken and not yet answered.
    asked: HashSet<u32>,
}

/// What a pack knows of one id.
pub(crate) enum Lookup {
    /// It holds the file: its bytes' length.
    Held(u32),
    /// The dat has no such file.
    Missing,
    /// Not known yet; the id is now wanted.
    NotLoaded,
}

/// A read from a pack: [`Lookup`] with the bytes.
pub(crate) enum Read {
    /// The file's bytes.
    Held(Vec<u8>),
    /// The dat has no such file.
    Missing,
    /// Not known yet; the id is now wanted.
    NotLoaded,
}

fn lock(mutex: &Mutex<PackState>) -> MutexGuard<'_, PackState> {
    // Every update is a few infallible steps, so a poisoned lock still
    // holds a usable state.
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

impl PackState {
    /// Note `id` as wanted unless it is already asked for.
    fn want(&mut self, id: u32) {
        if !self.asked.contains(&id) {
            self.wanted.insert(id);
        }
    }

    fn answered(&mut self, id: u32) {
        self.wanted.remove(&id);
        self.asked.remove(&id);
    }
}

impl DatPack {
    /// An empty pack.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Put file `id` into the pack (replacing any bytes held for it). An
    /// id marked missing before is held from now on.
    pub fn insert(&self, id: u32, bytes: Vec<u8>) {
        if id == ITERATION_ID
            && self.iteration.get().is_none()
            && let Ok(iteration) = Iteration::unpack(&mut Reader::new(&bytes))
        {
            let _ = self.iteration.set(iteration);
        }
        let mut state = lock(&self.state);
        state.answered(id);
        state.missing.remove(&id);
        state.bytes += bytes.len() as u64;
        if let Some(old) = state.files.insert(id, bytes) {
            state.bytes -= old.len() as u64;
        }
    }

    /// Record that the dat has no file `id`: reads of it answer "not
    /// found" and it is not wanted again. Bytes held for it are dropped.
    pub fn mark_missing(&self, id: u32) {
        let mut state = lock(&self.state);
        state.answered(id);
        if let Some(old) = state.files.remove(&id) {
            state.bytes -= old.len() as u64;
        }
        state.missing.insert(id);
    }

    /// The ids read since the last call that the pack neither holds nor
    /// knows to be missing, in ascending order, each once. An id taken
    /// here is not wanted again until it is inserted or marked missing
    /// (then it is answered, and never wanted again).
    pub fn take_wanted(&self) -> Vec<u32> {
        let mut state = lock(&self.state);
        let wanted = std::mem::take(&mut state.wanted);
        state.asked.extend(wanted.iter().copied());
        wanted.into_iter().collect()
    }

    /// Whether the pack holds file `id` (looking only: the id is not
    /// wanted when it does not).
    #[must_use]
    pub fn contains(&self, id: u32) -> bool {
        lock(&self.state).files.contains_key(&id)
    }

    /// Files held.
    #[must_use]
    pub fn len(&self) -> usize {
        lock(&self.state).files.len()
    }

    /// True when no file is held.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// The bytes of every file held, together.
    #[must_use]
    pub fn byte_len(&self) -> u64 {
        lock(&self.state).bytes
    }

    /// The iteration record, once inserted.
    pub(crate) fn iteration(&self) -> Option<&Iteration> {
        self.iteration.get()
    }

    /// What the pack knows of `id`, wanting it when it knows nothing.
    pub(crate) fn lookup(&self, id: u32) -> Lookup {
        let mut state = lock(&self.state);
        if let Some(bytes) = state.files.get(&id) {
            // A file held in memory fits a dat entry's size field: a dat
            // file is addressed by 32-bit offsets.
            return Lookup::Held(u32::try_from(bytes.len()).unwrap_or(u32::MAX));
        }
        if state.missing.contains(&id) {
            return Lookup::Missing;
        }
        state.want(id);
        Lookup::NotLoaded
    }

    /// The bytes of `id`, wanting it when the pack knows nothing of it.
    pub(crate) fn read(&self, id: u32) -> Read {
        let mut state = lock(&self.state);
        if let Some(bytes) = state.files.get(&id) {
            return Read::Held(bytes.clone());
        }
        if state.missing.contains(&id) {
            return Read::Missing;
        }
        state.want(id);
        Read::NotLoaded
    }

    /// An entry for every file held, in id order. Only the id and the
    /// size mean anything; the rest is zero.
    pub(crate) fn entries(&self) -> Vec<DatEntry> {
        lock(&self.state)
            .files
            .iter()
            .map(|(&id, bytes)| entry(id, bytes.len()))
            .collect()
    }
}

/// The directory entry a pack gives a file it holds.
pub(crate) fn entry(id: u32, len: usize) -> DatEntry {
    DatEntry {
        id,
        size: u32::try_from(len).unwrap_or(u32::MAX),
        ..DatEntry::default()
    }
}
