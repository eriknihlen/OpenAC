use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::cache::{
    CacheStats, DEFAULT_OBJECT_BYTE_LIMIT, DEFAULT_OBJECT_ENTRY_LIMIT, ObjectCache,
};
use crate::database::{DatFile, Presence};
use crate::error::{DatError, Result};
use crate::header::DatFileType;
use crate::objects::{DatObject, SurfaceTexture};
use crate::pack::DatPack;

/// File name of the portal dat.
pub const PORTAL_DAT: &str = "client_portal.dat";
/// File name of the cell dat.
pub const CELL_DAT: &str = "client_cell_1.dat";
/// File name of the high-resolution portal dat.
pub const HIGHRES_DAT: &str = "client_highres.dat";
/// File name of the English language dat.
pub const LOCAL_DAT: &str = "client_local_English.dat";

/// The dats a client reads, opened from one directory: portal, cell and
/// language always, the high-resolution dat when the install has it.
#[derive(Debug)]
pub struct DatCollection {
    /// Models, textures, animations and tables.
    pub portal: DatFile,
    /// Landblocks and interior cells.
    pub cell: DatFile,
    /// High-resolution textures (a portal-type dat); `None` when the
    /// directory has no `client_highres.dat`.
    pub high_res: Option<DatFile>,
    /// English strings and fonts.
    pub local: DatFile,
}

/// Open `path` as a dat of kind `expected`, or `None` when there is no
/// such file. A file that is there but cannot be read is still an error.
#[cfg(not(target_arch = "wasm32"))]
fn open_if_present(path: PathBuf, expected: DatFileType) -> Result<Option<DatFile>> {
    match DatFile::open_as(path, expected) {
        Ok(dat) => Ok(Some(dat)),
        Err(DatError::Io { source, .. }) if source.kind() == std::io::ErrorKind::NotFound => {
            Ok(None)
        }
        Err(error) => Err(error),
    }
}

impl DatCollection {
    /// Open the dats in `dir`, checking each header's kind. The portal,
    /// cell and language dats must be there; the high-resolution dat is
    /// opened only when the file exists. Not on WebAssembly, which has no
    /// dat files: build a collection of packs there
    /// ([`DatCollection::from_packs`]).
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open(dir: impl AsRef<Path>) -> Result<Self> {
        let dir = dir.as_ref();
        Ok(Self {
            portal: DatFile::open_as(dir.join(PORTAL_DAT), DatFileType::Portal)?,
            cell: DatFile::open_as(dir.join(CELL_DAT), DatFileType::Cell)?,
            high_res: open_if_present(dir.join(HIGHRES_DAT), DatFileType::Portal)?,
            local: DatFile::open_as(dir.join(LOCAL_DAT), DatFileType::Local)?,
        })
    }

    /// Read a typed object from the dat its type lives in. A portal object
    /// the high-resolution dat holds is read from there; any other comes
    /// from the portal dat.
    /// Over packs, `None` while the dat to read from is not known yet (see
    /// [`DatCollection::portal_source`]).
    pub fn get<T: DatObject>(&self, id: u32) -> Result<Option<T>> {
        match T::DAT {
            DatFileType::Cell => self.cell.get(id),
            DatFileType::Local => self.local.get(id),
            DatFileType::Portal => match self.portal_pick(id)? {
                Some((_, file)) => file.get(id),
                None => Ok(None),
            },
            DatFileType::Undefined | DatFileType::Other(_) => Ok(None),
        }
    }

    /// The dat a portal-type id is read from: the high-resolution dat when
    /// it is open and its directory holds the id, else the portal dat
    /// (whether or not the portal dat holds it). A high-resolution
    /// directory that cannot be read is an error.
    ///
    /// Over packs the answer waits for the high-resolution pack: while it
    /// does not know whether it holds the id, the answer is
    /// [`DatError::NotLoaded`], and the id is asked of it (and of the
    /// portal pack, which is read if the high-resolution pack has none).
    pub fn portal_source(&self, id: u32) -> Result<DatSource> {
        match self.portal_pick(id)? {
            Some((source, _)) => Ok(source),
            None => Err(DatError::NotLoaded { id }),
        }
    }

    /// The file a portal-type id is read from (see
    /// [`DatCollection::portal_source`], including its wait over packs),
    /// for readers that decode the raw bytes themselves.
    pub fn portal_file(&self, id: u32) -> Result<&DatFile> {
        match self.portal_pick(id)? {
            Some((_, file)) => Ok(file),
            None => Err(DatError::NotLoaded { id }),
        }
    }

    /// The dat a portal-type id is read from; `None` while a
    /// high-resolution pack does not know the id yet.
    pub(crate) fn portal_pick(&self, id: u32) -> Result<Option<(DatSource, &DatFile)>> {
        let Some(high_res) = &self.high_res else {
            return Ok(Some((DatSource::Portal, &self.portal)));
        };
        Ok(match high_res.presence(id)? {
            Presence::Held => Some((DatSource::HighRes, high_res)),
            Presence::Absent => Some((DatSource::Portal, &self.portal)),
            Presence::NotLoaded => {
                // Ask the portal pack now too, so a file the
                // high-resolution pack lacks takes one round trip, not two.
                self.portal.presence(id)?;
                None
            }
        })
    }

    /// The raw bytes of portal-type file `id`, from the dat
    /// [`DatCollection::portal_source`] picks; `None` when it has none (or,
    /// over packs, while that dat is not known yet).
    pub fn portal_bytes(&self, id: u32) -> Result<Option<Vec<u8>>> {
        match self.portal_pick(id)? {
            Some((_, file)) => file.get_bytes(id),
            None => Ok(None),
        }
    }

    /// Whether the high-resolution dat is open.
    #[must_use]
    pub fn has_high_res(&self) -> bool {
        self.high_res.is_some()
    }

    /// The render surface a surface texture is drawn from with these dats
    /// (see [`SurfaceTexture::source_level`]).
    #[must_use]
    pub fn texture_source(&self, texture: &SurfaceTexture) -> Option<u32> {
        texture.source_level(self.has_high_res())
    }

    /// The open dats with their display names, in a fixed order.
    #[must_use]
    pub fn all(&self) -> Vec<(&'static str, &DatFile)> {
        let mut dats = vec![("portal", &self.portal), ("cell", &self.cell)];
        if let Some(high_res) = &self.high_res {
            dats.push(("highres", high_res));
        }
        dats.push(("local", &self.local));
        dats
    }

    /// The dat an id is read from when its type is not known in advance:
    /// the first of high-resolution, portal, language and cell that holds
    /// it. `None` when no dat has it.
    ///
    /// Over packs, also `None` while a pack earlier in that order does not
    /// know the id yet; the id is asked of every pack up to the first that
    /// holds it.
    pub fn resolve(&self, id: u32) -> Result<Option<DatSource>> {
        let mut unsettled = false;
        for source in DatSource::PRECEDENCE {
            let Some(file) = self.file(source) else {
                continue;
            };
            match file.presence(id)? {
                Presence::Held => return Ok((!unsettled).then_some(source)),
                Presence::Absent => {}
                Presence::NotLoaded => unsettled = true,
            }
        }
        Ok(None)
    }

    /// One of the dats; `None` for the high-resolution dat when the
    /// install has none.
    #[must_use]
    pub fn file(&self, source: DatSource) -> Option<&DatFile> {
        match source {
            DatSource::Portal => Some(&self.portal),
            DatSource::HighRes => self.high_res.as_ref(),
            DatSource::Local => Some(&self.local),
            DatSource::Cell => Some(&self.cell),
        }
    }

    /// Build a collection from dats already made, usually packs
    /// ([`DatFile::new_pack`]) for a process with no dat files, checking
    /// each header's kind as [`DatCollection::open`] does. `high_res` is
    /// `None` when there is no high-resolution dat; with one, every
    /// portal-type read waits until the high-resolution pack knows whether
    /// it holds the id (see [`DatCollection::portal_source`]).
    pub fn from_packs(
        portal: DatFile,
        cell: DatFile,
        high_res: Option<DatFile>,
        local: DatFile,
    ) -> Result<Self> {
        let check = |dat: DatFile, expected: DatFileType| {
            let found = dat.header().file_type;
            if found == expected {
                Ok(dat)
            } else {
                Err(DatError::WrongType {
                    path: dat.path().to_path_buf(),
                    expected,
                    found,
                })
            }
        };
        Ok(Self {
            portal: check(portal, DatFileType::Portal)?,
            cell: check(cell, DatFileType::Cell)?,
            high_res: high_res
                .map(|dat| check(dat, DatFileType::Portal))
                .transpose()?,
            local: check(local, DatFileType::Local)?,
        })
    }

    /// The pack behind one of the dats, to fill it; `None` when that dat
    /// is a mapped file or (the high-resolution dat) is not there.
    #[must_use]
    pub fn pack(&self, source: DatSource) -> Option<&DatPack> {
        self.file(source)?.as_pack()
    }

    /// Start recording: from now on every file read from any of the dats
    /// (its bytes found, through a typed or a byte read, here or through
    /// the dats themselves) is remembered with its dat, for building the
    /// list of files a pack is preloaded with. Off until called; while off
    /// a read takes no lock for it. Not on WebAssembly.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn record_reads(&mut self) {
        self.portal.record_reads();
        self.cell.record_reads();
        if let Some(high_res) = &mut self.high_res {
            high_res.record_reads();
        }
        self.local.record_reads();
    }

    /// Whether reads are being recorded.
    #[cfg(not(target_arch = "wasm32"))]
    #[must_use]
    pub fn is_recording(&self) -> bool {
        self.portal.is_recording()
    }

    /// The files read since recording began or since the last take,
    /// sorted by dat in [`DatSource`] order and then by id, each once.
    /// Empty when not recording.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn take_recorded(&self) -> Vec<(DatSource, u32)> {
        DatSource::ALL
            .into_iter()
            .filter_map(|source| Some((source, self.file(source)?)))
            .flat_map(|(source, file)| file.take_recorded().into_iter().map(move |id| (source, id)))
            .collect()
    }

    /// The ids every pack was asked for and does not hold (see
    /// [`DatPack::take_wanted`]), sorted by dat in [`DatSource`] order and
    /// then by id, each once. Empty for mapped files.
    pub fn take_wanted(&self) -> Vec<(DatSource, u32)> {
        DatSource::ALL
            .into_iter()
            .filter_map(|source| Some((source, self.pack(source)?)))
            .flat_map(|(source, pack)| pack.take_wanted().into_iter().map(move |id| (source, id)))
            .collect()
    }
}

/// Which of the four dats something was found in. Ordered as declared
/// (portal, high-resolution, language, cell), the order lists of ids by
/// dat are sorted in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum DatSource {
    /// `client_portal.dat`.
    Portal,
    /// `client_highres.dat`.
    HighRes,
    /// `client_local_English.dat`.
    Local,
    /// `client_cell_1.dat`.
    Cell,
}

impl DatSource {
    /// The order an id of unknown type is looked up in. The
    /// high-resolution dat comes before the portal dat: it holds better
    /// copies of portal objects, and a portal object it holds is read
    /// from it.
    pub const PRECEDENCE: [Self; 4] = [Self::HighRes, Self::Portal, Self::Local, Self::Cell];

    /// The four in their sort order.
    const ALL: [Self; 4] = [Self::Portal, Self::HighRes, Self::Local, Self::Cell];

    /// Where this dat's object cache sits in [`CachedDats`].
    const fn cache_index(self) -> usize {
        match self {
            Self::Portal => 0,
            Self::HighRes => 1,
            Self::Local => 2,
            Self::Cell => 3,
        }
    }
}

/// The first dat in [`DatSource::PRECEDENCE`] that `holds` the id: the
/// high-resolution dat wins over everything, then the portal dat, then the
/// language dat, then the cell dat.
pub fn preferred_source(holds: impl Fn(DatSource) -> bool) -> Option<DatSource> {
    DatSource::PRECEDENCE.into_iter().find(|s| holds(*s))
}

/// The size of one object cache: entries and estimated bytes (see
/// [`ObjectCache::new`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CacheLimit {
    /// Objects kept.
    pub entries: usize,
    /// Estimated bytes kept.
    pub bytes: u64,
}

impl Default for CacheLimit {
    /// OpenAC's per-dat object cache: 256 objects, 64 MiB.
    fn default() -> Self {
        Self {
            entries: DEFAULT_OBJECT_ENTRY_LIMIT,
            bytes: DEFAULT_OBJECT_BYTE_LIMIT,
        }
    }
}

/// The sizes of the four dats' object caches.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ObjectCacheLimits {
    /// `client_portal.dat`.
    pub portal: CacheLimit,
    /// `client_highres.dat`.
    pub high_res: CacheLimit,
    /// `client_local_English.dat`.
    pub local: CacheLimit,
    /// `client_cell_1.dat`.
    pub cell: CacheLimit,
}

/// The dats with a bounded cache of decoded objects in front of each,
/// for a process that reads the same objects again and again (every window
/// and every session of the process shares one). `Send + Sync`: any thread
/// may read.
#[derive(Debug)]
pub struct CachedDats {
    dats: DatCollection,
    caches: [ObjectCache; 4],
    directory: PathBuf,
}

impl CachedDats {
    /// Open the dats in `dir` (as [`DatCollection::open`]), each with a cache
    /// of the default size. Not on WebAssembly.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open(dir: impl AsRef<Path>) -> Result<Self> {
        let directory = dir.as_ref().to_path_buf();
        Ok(Self::new(DatCollection::open(&directory)?, directory))
    }

    /// Put caches in front of already opened dats.
    #[must_use]
    pub fn new(dats: DatCollection, directory: PathBuf) -> Self {
        Self::with_limits(dats, directory, &ObjectCacheLimits::default())
    }

    /// Open the dats in `dir` (as [`DatCollection::open`]) with caches of the
    /// given sizes. Not on WebAssembly.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open_with_limits(dir: impl AsRef<Path>, limits: &ObjectCacheLimits) -> Result<Self> {
        let directory = dir.as_ref().to_path_buf();
        Ok(Self::with_limits(
            DatCollection::open(&directory)?,
            directory,
            limits,
        ))
    }

    /// Put caches of the given sizes in front of already opened dats.
    #[must_use]
    pub fn with_limits(
        dats: DatCollection,
        directory: PathBuf,
        limits: &ObjectCacheLimits,
    ) -> Self {
        let cache = |l: CacheLimit| ObjectCache::new(l.entries, l.bytes);
        Self {
            dats,
            // At `DatSource::cache_index`.
            caches: [
                cache(limits.portal),
                cache(limits.high_res),
                cache(limits.local),
                cache(limits.cell),
            ],
            directory,
        }
    }

    /// Objects and estimated bytes each dat's cache holds, in
    /// [`DatSource::PRECEDENCE`] order.
    #[must_use]
    pub fn occupancy(&self) -> [(DatSource, usize, u64); 4] {
        DatSource::PRECEDENCE.map(|source| {
            let cache = self.cache(source);
            (source, cache.len(), cache.estimated_bytes())
        })
    }

    /// The directory the dats were opened from.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// The dats themselves, uncached.
    #[must_use]
    pub fn uncached(&self) -> &DatCollection {
        &self.dats
    }

    /// Whether the high-resolution dat is open.
    #[must_use]
    pub fn has_high_res(&self) -> bool {
        self.dats.has_high_res()
    }

    /// The render surface a surface texture is drawn from with these dats
    /// (see [`SurfaceTexture::source_level`]).
    #[must_use]
    pub fn texture_source(&self, texture: &SurfaceTexture) -> Option<u32> {
        self.dats.texture_source(texture)
    }

    /// Start recording every file read (see
    /// [`DatCollection::record_reads`]); an object the cache answers
    /// counts as a read of its file, so the list is whole whenever this is
    /// called. Not on WebAssembly.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn record_reads(&mut self) {
        self.dats.record_reads();
    }

    /// Whether reads are being recorded.
    #[cfg(not(target_arch = "wasm32"))]
    #[must_use]
    pub fn is_recording(&self) -> bool {
        self.dats.is_recording()
    }

    /// The files read since recording began or since the last take (see
    /// [`DatCollection::take_recorded`]).
    #[cfg(not(target_arch = "wasm32"))]
    pub fn take_recorded(&self) -> Vec<(DatSource, u32)> {
        self.dats.take_recorded()
    }

    /// Hits, misses and evictions of the caches together.
    #[must_use]
    pub fn stats(&self) -> CacheStats {
        self.caches
            .iter()
            .fold(CacheStats::default(), |sum, c| sum + c.stats())
    }

    fn cache(&self, source: DatSource) -> &ObjectCache {
        &self.caches[source.cache_index()]
    }

    /// Read `id` from one dat's file into its cache (the cache was asked).
    fn read_into_cache<T: DatObject + Send + Sync + 'static>(
        &self,
        file: &DatFile,
        source: DatSource,
        id: u32,
    ) -> Result<Option<Arc<T>>> {
        let cache = self.cache(source);
        Ok(file
            .get::<T>(id)?
            .map(|object| cache.get_or_add(id, Arc::new(object))))
    }

    fn get_from<T: DatObject + Send + Sync + 'static>(
        &self,
        file: &DatFile,
        source: DatSource,
        id: u32,
    ) -> Result<Option<Arc<T>>> {
        if let Some(object) = self.cache(source).get::<T>(id) {
            file.note_read(id);
            return Ok(Some(object));
        }
        self.read_into_cache(file, source, id)
    }

    /// A portal-type object: memory first, then the dat
    /// [`DatCollection::portal_source`] picks. The portal cache only ever
    /// holds ids the high-resolution dat lacks (the dats do not change), so
    /// a hit in either cache needs no directory lookup. One hit or one miss
    /// is counted per read. Over packs that holds too: nothing is read
    /// while the high-resolution pack does not know the id (the miss is
    /// counted against the portal cache), so the portal cache never takes
    /// a copy the high-resolution pack turns out to hold.
    fn get_portal_type<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>> {
        for source in [DatSource::HighRes, DatSource::Portal] {
            let cache = self.cache(source);
            if let Some(object) = cache.get_uncounted::<T>(id) {
                cache.count_hit();
                if let Some(file) = self.dats.file(source) {
                    file.note_read(id);
                }
                return Ok(Some(object));
            }
        }
        let Some((source, file)) = self.dats.portal_pick(id)? else {
            self.cache(DatSource::Portal).count_miss();
            return Ok(None);
        };
        self.cache(source).count_miss();
        self.read_into_cache(file, source, id)
    }

    /// Read a typed object as [`DatCollection::get`] does (a portal object
    /// the high-resolution dat holds comes from there, any other from the
    /// portal dat), through the cache.
    pub fn get<T: DatObject + Send + Sync + 'static>(&self, id: u32) -> Result<Option<Arc<T>>> {
        match T::DAT {
            DatFileType::Cell => self.get_from(&self.dats.cell, DatSource::Cell, id),
            DatFileType::Local => self.get_from(&self.dats.local, DatSource::Local, id),
            DatFileType::Portal => self.get_portal_type(id),
            DatFileType::Undefined | DatFileType::Other(_) => Ok(None),
        }
    }
}

/// The usual install location of the dats: `Documents\Asheron's Call` under
/// the user's profile (`%USERPROFILE%`, or `$HOME` elsewhere). `None` when
/// neither variable is set.
#[must_use]
pub fn default_dat_dir() -> Option<PathBuf> {
    let home = std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME"))?;
    Some(PathBuf::from(home).join("Documents").join("Asheron's Call"))
}

#[cfg(test)]
mod tests {
    //! Precedence cases after OpenAC `tests/AcDream.Content.Tests/DatResolutionPrecedenceTests.cs`,
    //! with the high-resolution dat ahead of the portal dat as the original
    //! client reads it; then the same order on small dats built here.
    use super::*;
    use crate::btree::tests::{entry, node_bytes};
    use crate::database::tests::DatBuilder;
    use ac_bits::Reader;

    #[test]
    fn high_res_wins_even_when_every_other_dat_also_holds_the_id() {
        assert_eq!(preferred_source(|_| true), Some(DatSource::HighRes));
    }

    #[test]
    fn the_rest_go_portal_then_language_then_cell() {
        assert_eq!(
            preferred_source(|s| s != DatSource::HighRes),
            Some(DatSource::Portal)
        );
        assert_eq!(
            preferred_source(|s| matches!(s, DatSource::Local | DatSource::Cell)),
            Some(DatSource::Local)
        );
        assert_eq!(
            preferred_source(|s| s == DatSource::Cell),
            Some(DatSource::Cell)
        );
    }

    #[test]
    fn a_missing_id_resolves_to_nothing() {
        assert_eq!(preferred_source(|_| false), None);
    }

    #[test]
    fn cached_dats_are_send_and_sync() {
        const fn assert_send_sync<T: Send + Sync>() {}
        assert_send_sync::<CachedDats>();
    }

    /// A portal-type object that is just its file's bytes, to see which dat
    /// a read came from.
    #[derive(Debug, PartialEq, Eq)]
    struct Raw(Vec<u8>);

    impl DatObject for Raw {
        const DAT: DatFileType = DatFileType::Portal;
        fn unpack(r: &mut Reader<'_>) -> core::result::Result<Self, crate::error::DecodeError> {
            Ok(Self(r.bytes(r.remaining())?.to_vec()))
        }
    }

    /// A one-leaf dat of `file_type` whose files are `(id, bytes)`.
    fn dat_bytes(file_type: u32, files: &[(u32, &[u8])]) -> Vec<u8> {
        let mut b = DatBuilder::new(64);
        let entries: Vec<_> = files
            .iter()
            .map(|(id, data)| entry(*id, b.chain(data), data.len() as u32))
            .collect();
        let root = b.chain(&node_bytes(&[], &entries));
        b.finish(file_type, root)
    }

    /// A fresh directory holding the portal, cell and language dats, and
    /// the high-resolution dat when `high_res` is given.
    fn dat_dir(name: &str, high_res: Option<&[(u32, &[u8])]>) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("ac-dat-collection-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let portal: &[(u32, &[u8])] = &[(0x0600_0001, b"portal"), (0x0600_0002, b"portal only")];
        std::fs::write(dir.join(PORTAL_DAT), dat_bytes(1, portal)).unwrap();
        std::fs::write(dir.join(CELL_DAT), dat_bytes(2, &[(0x0101_FFFF, b"cell")])).unwrap();
        std::fs::write(
            dir.join(LOCAL_DAT),
            dat_bytes(3, &[(0x2300_0001, b"local")]),
        )
        .unwrap();
        if let Some(files) = high_res {
            std::fs::write(dir.join(HIGHRES_DAT), dat_bytes(1, files)).unwrap();
        }
        dir
    }

    const HIGH_RES: &[(u32, &[u8])] = &[(0x0600_0001, b"high res"), (0x0600_0003, b"hr only")];

    #[test]
    fn a_portal_id_the_high_res_dat_holds_is_read_from_it() {
        let dir = dat_dir("both", Some(HIGH_RES));
        let dats = DatCollection::open(&dir).unwrap();
        assert!(dats.high_res.is_some());
        let read = |id| dats.get::<Raw>(id).unwrap().map(|r| r.0);
        // In both dats: the high-resolution copy.
        assert_eq!(read(0x0600_0001), Some(b"high res".to_vec()));
        assert_eq!(dats.resolve(0x0600_0001).unwrap(), Some(DatSource::HighRes));
        assert_eq!(dats.portal_source(0x0600_0001).unwrap(), DatSource::HighRes);
        // In one of them: that one.
        assert_eq!(read(0x0600_0002), Some(b"portal only".to_vec()));
        assert_eq!(dats.resolve(0x0600_0002).unwrap(), Some(DatSource::Portal));
        assert_eq!(read(0x0600_0003), Some(b"hr only".to_vec()));
        assert_eq!(dats.resolve(0x0600_0003).unwrap(), Some(DatSource::HighRes));
        assert_eq!(read(0x0600_0004), None);
        assert_eq!(dats.portal_source(0x0600_0004).unwrap(), DatSource::Portal);

        let cached = CachedDats::open(&dir).unwrap();
        for _ in 0..2 {
            let got = cached.get::<Raw>(0x0600_0001).unwrap().unwrap();
            assert_eq!(got.0, b"high res");
            let got = cached.get::<Raw>(0x0600_0002).unwrap().unwrap();
            assert_eq!(got.0, b"portal only");
        }
        // One miss and one hit per id, each in its own dat's cache.
        let stats = cached.stats();
        assert_eq!((stats.hits, stats.misses), (2, 2));
        let held = |source| cached.occupancy().iter().find(|o| o.0 == source).unwrap().1;
        assert_eq!((held(DatSource::HighRes), held(DatSource::Portal)), (1, 1));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn without_a_high_res_dat_portal_ids_come_from_the_portal_dat() {
        let dir = dat_dir("no-high-res", None);
        let dats = DatCollection::open(&dir).unwrap();
        assert!(dats.high_res.is_none());
        assert!(dats.file(DatSource::HighRes).is_none());
        assert_eq!(
            dats.all().iter().map(|(name, _)| *name).collect::<Vec<_>>(),
            ["portal", "cell", "local"]
        );
        let read = |id| dats.get::<Raw>(id).unwrap().map(|r| r.0);
        assert_eq!(read(0x0600_0001), Some(b"portal".to_vec()));
        assert_eq!(read(0x0600_0003), None);
        assert_eq!(dats.resolve(0x0600_0001).unwrap(), Some(DatSource::Portal));
        assert_eq!(dats.resolve(0x0101_FFFF).unwrap(), Some(DatSource::Cell));
        assert_eq!(dats.resolve(0x2300_0001).unwrap(), Some(DatSource::Local));
        assert_eq!(dats.resolve(0x0600_0003).unwrap(), None);

        let cached = CachedDats::open(&dir).unwrap();
        assert_eq!(
            cached.get::<Raw>(0x0600_0001).unwrap().unwrap().0,
            b"portal"
        );
        assert!(cached.get::<Raw>(0x0600_0003).unwrap().is_none());
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn a_high_res_file_that_is_there_but_unreadable_is_still_an_error() {
        let dir = dat_dir("bad-high-res", None);
        std::fs::write(dir.join(HIGHRES_DAT), b"not a dat").unwrap();
        assert!(matches!(
            DatCollection::open(&dir),
            Err(DatError::TooShort { .. })
        ));
        // A cell dat under the high-resolution name is the wrong kind.
        std::fs::write(dir.join(HIGHRES_DAT), dat_bytes(2, &[])).unwrap();
        assert!(matches!(
            DatCollection::open(&dir),
            Err(DatError::WrongType { .. })
        ));
        std::fs::remove_dir_all(dir).ok();
    }

    const PORTAL: &[(u32, &[u8])] = &[(0x0600_0001, b"portal"), (0x0600_0002, b"portal only")];
    const CELL: &[(u32, &[u8])] = &[(0x0101_FFFF, b"cell")];
    const LOCAL: &[(u32, &[u8])] = &[(0x2300_0001, b"local")];

    /// A pack of `file_type` holding `files`.
    fn pack(file_type: DatFileType, files: &[(u32, &[u8])]) -> DatFile {
        let dat = DatFile::new_pack(crate::database::tests::pack_header(file_type));
        for (id, bytes) in files {
            dat.as_pack().unwrap().insert(*id, bytes.to_vec());
        }
        dat
    }

    /// Packs of what `dat_dir` writes, each holding none of its files yet.
    fn empty_packs(high_res: bool) -> DatCollection {
        DatCollection::from_packs(
            pack(DatFileType::Portal, &[]),
            pack(DatFileType::Cell, &[]),
            high_res.then(|| pack(DatFileType::Portal, &[])),
            pack(DatFileType::Local, &[]),
        )
        .unwrap()
    }

    /// Answer every id the packs asked for from what `dat_dir` writes, as
    /// a host does: the bytes, or "no such file".
    fn serve(dats: &DatCollection, high_res: Option<&[(u32, &[u8])]>) {
        for (source, id) in dats.take_wanted() {
            let files = match source {
                DatSource::Portal => PORTAL,
                DatSource::HighRes => high_res.unwrap(),
                DatSource::Local => LOCAL,
                DatSource::Cell => CELL,
            };
            let pack = dats.pack(source).unwrap();
            match files.iter().find(|(held, _)| *held == id) {
                Some((_, bytes)) => pack.insert(id, bytes.to_vec()),
                None => pack.mark_missing(id),
            }
        }
    }

    /// `read` once the host has answered every id it asks for.
    fn settled<R>(
        dats: &DatCollection,
        high_res: Option<&[(u32, &[u8])]>,
        read: impl Fn() -> R,
    ) -> R {
        for _ in 0..4 {
            read();
            serve(dats, high_res);
        }
        let answer = read();
        assert!(dats.take_wanted().is_empty(), "settled");
        answer
    }

    #[test]
    fn packs_resolve_as_files_with_the_same_contents_do() {
        for high_res in [Some(HIGH_RES), None] {
            let dir = dat_dir(&format!("packs-{}", high_res.is_some()), high_res);
            let files = DatCollection::open(&dir).unwrap();
            let packs = empty_packs(high_res.is_some());
            assert_eq!(packs.has_high_res(), files.has_high_res());
            let texture = SurfaceTexture {
                id: 0x0500_0001,
                data_category: 0,
                texture_type: crate::objects::TextureType(0),
                textures: vec![0x0600_0001, 0x0600_0002],
            };
            assert_eq!(
                packs.texture_source(&texture),
                files.texture_source(&texture)
            );
            for id in [
                0x0600_0001,
                0x0600_0002,
                0x0600_0003,
                0x0600_0004,
                0x0101_FFFF,
                0x2300_0001,
                0x7700_0000,
            ] {
                assert_eq!(
                    settled(&packs, high_res, || packs.resolve(id).unwrap()),
                    files.resolve(id).unwrap(),
                    "{id:#x}"
                );
                assert_eq!(
                    settled(&packs, high_res, || packs.portal_source(id).ok()),
                    Some(files.portal_source(id).unwrap()),
                    "{id:#x}"
                );
                assert_eq!(
                    settled(&packs, high_res, || packs.get::<Raw>(id).unwrap()),
                    files.get::<Raw>(id).unwrap(),
                    "{id:#x}"
                );
                assert_eq!(
                    settled(&packs, high_res, || packs.portal_bytes(id).unwrap()),
                    files.portal_bytes(id).unwrap(),
                    "{id:#x}"
                );
            }
            std::fs::remove_dir_all(dir).ok();
        }
    }

    #[test]
    fn a_portal_read_waits_for_the_high_res_pack_to_answer() {
        let dats = DatCollection::from_packs(
            pack(DatFileType::Portal, PORTAL),
            pack(DatFileType::Cell, CELL),
            Some(pack(DatFileType::Portal, &[])),
            pack(DatFileType::Local, LOCAL),
        )
        .unwrap();
        let cached = CachedDats::new(dats, PathBuf::new());
        let read = |id| cached.get::<Raw>(id).unwrap().map(|r| r.0.clone());
        let high_res = cached.uncached().pack(DatSource::HighRes).unwrap();
        // The portal pack holds it, but the high-resolution pack may too:
        // not found until that pack knows, and neither copy is cached.
        assert_eq!(read(0x0600_0001), None);
        assert!(matches!(
            cached.uncached().portal_source(0x0600_0001),
            Err(DatError::NotLoaded { id: 0x0600_0001 })
        ));
        assert_eq!(cached.uncached().portal_bytes(0x0600_0001).unwrap(), None);
        assert_eq!(cached.uncached().resolve(0x0600_0001).unwrap(), None);
        assert_eq!(
            cached.uncached().take_wanted(),
            [(DatSource::HighRes, 0x0600_0001)]
        );
        high_res.insert(0x0600_0001, b"high res".to_vec());
        assert_eq!(read(0x0600_0001), Some(b"high res".to_vec()));
        // The high-resolution pack has none: the portal copy.
        assert_eq!(read(0x0600_0002), None);
        high_res.mark_missing(0x0600_0002);
        assert_eq!(read(0x0600_0002), Some(b"portal only".to_vec()));
        // Neither pack knows the id: both are asked at once.
        assert_eq!(read(0x0600_0009), None);
        assert_eq!(
            cached.uncached().take_wanted(),
            [
                (DatSource::Portal, 0x0600_0009),
                (DatSource::HighRes, 0x0600_0009)
            ]
        );
    }

    #[test]
    fn a_cached_read_of_a_file_not_loaded_yet_succeeds_once_it_arrives() {
        let cached = CachedDats::new(empty_packs(false), PathBuf::new());
        assert!(cached.get::<Raw>(0x0600_0005).unwrap().is_none());
        assert_eq!(
            cached.uncached().take_wanted(),
            [(DatSource::Portal, 0x0600_0005)]
        );
        cached
            .uncached()
            .pack(DatSource::Portal)
            .unwrap()
            .insert(0x0600_0005, b"late".to_vec());
        assert_eq!(cached.get::<Raw>(0x0600_0005).unwrap().unwrap().0, b"late");
        // Now cached: a second read is a hit.
        assert_eq!(cached.get::<Raw>(0x0600_0005).unwrap().unwrap().0, b"late");
        let stats = cached.stats();
        assert_eq!((stats.hits, stats.misses), (1, 2));
    }

    #[test]
    fn a_pack_of_the_wrong_kind_is_refused() {
        assert!(matches!(
            DatCollection::from_packs(
                pack(DatFileType::Cell, &[]),
                pack(DatFileType::Cell, &[]),
                None,
                pack(DatFileType::Local, &[]),
            ),
            Err(DatError::WrongType {
                expected: DatFileType::Portal,
                found: DatFileType::Cell,
                ..
            })
        ));
        assert!(matches!(
            DatCollection::from_packs(
                pack(DatFileType::Portal, &[]),
                pack(DatFileType::Cell, &[]),
                Some(pack(DatFileType::Local, &[])),
                pack(DatFileType::Local, &[]),
            ),
            Err(DatError::WrongType { .. })
        ));
        // A mapped collection has no packs.
        let dir = dat_dir("no-packs", None);
        let files = DatCollection::open(&dir).unwrap();
        assert!(
            DatSource::PRECEDENCE
                .iter()
                .all(|s| files.pack(*s).is_none())
        );
        assert!(files.take_wanted().is_empty());
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn pack_collections_are_send_and_sync() {
        const fn assert_send_sync<T: Send + Sync>() {}
        assert_send_sync::<DatCollection>();
        assert_send_sync::<CachedDats>();
        let _ = CachedDats::new(empty_packs(true), PathBuf::new());
    }

    #[test]
    fn the_recorder_notes_each_file_read_once_and_only_when_on() {
        let dir = dat_dir("recorder", Some(HIGH_RES));
        let mut dats = DatCollection::open(&dir).unwrap();
        assert!(!dats.is_recording());
        assert!(dats.get::<Raw>(0x0600_0001).unwrap().is_some());
        assert!(dats.take_recorded().is_empty());

        dats.record_reads();
        assert!(dats.is_recording());
        for _ in 0..2 {
            assert!(dats.get::<Raw>(0x0600_0001).unwrap().is_some());
            assert!(dats.get::<Raw>(0x0600_0002).unwrap().is_some());
            assert!(dats.portal_bytes(0x0600_0003).unwrap().is_some());
            assert!(dats.cell.get_bytes(0x0101_FFFF).unwrap().is_some());
        }
        // Not found: nothing read. Looking an id up reads nothing either.
        assert!(dats.get::<Raw>(0x0600_0004).unwrap().is_none());
        assert_eq!(dats.resolve(0x2300_0001).unwrap(), Some(DatSource::Local));
        let entry = dats.local.find_entry(0x2300_0001).unwrap().unwrap();
        assert_eq!(
            dats.take_recorded(),
            [
                (DatSource::Portal, 0x0600_0002),
                (DatSource::HighRes, 0x0600_0001),
                (DatSource::HighRes, 0x0600_0003),
                (DatSource::Cell, 0x0101_FFFF),
            ]
        );
        assert!(dats.take_recorded().is_empty());
        // Reading an entry's bytes is a read.
        dats.local.read_entry(&entry).unwrap();
        assert_eq!(dats.take_recorded(), [(DatSource::Local, 0x2300_0001)]);
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn cached_dats_record_what_their_caches_answer_too() {
        let dir = dat_dir("recorder-cached", Some(HIGH_RES));
        let mut cached = CachedDats::open(&dir).unwrap();
        // Cached before recording began.
        assert!(cached.get::<Raw>(0x0600_0001).unwrap().is_some());
        assert!(cached.take_recorded().is_empty());
        cached.record_reads();
        assert!(cached.get::<Raw>(0x0600_0001).unwrap().is_some()); // a hit
        assert!(cached.get::<Raw>(0x0600_0002).unwrap().is_some()); // a miss
        assert_eq!(
            cached.take_recorded(),
            [
                (DatSource::Portal, 0x0600_0002),
                (DatSource::HighRes, 0x0600_0001),
            ]
        );
        // After a take, a hit is recorded again.
        assert!(cached.get::<Raw>(0x0600_0002).unwrap().is_some());
        assert_eq!(cached.take_recorded(), [(DatSource::Portal, 0x0600_0002)]);
        let stats = cached.stats();
        assert_eq!((stats.hits, stats.misses), (2, 2));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn the_other_three_dats_are_still_required() {
        for missing in [PORTAL_DAT, CELL_DAT, LOCAL_DAT] {
            let dir = dat_dir(&format!("missing-{missing}"), Some(HIGH_RES));
            std::fs::remove_file(dir.join(missing)).unwrap();
            assert!(
                matches!(DatCollection::open(&dir), Err(DatError::Io { .. })),
                "{missing}"
            );
            std::fs::remove_dir_all(dir).ok();
        }
    }
}
