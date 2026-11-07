use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::cache::{
    CacheStats, DEFAULT_OBJECT_BYTE_LIMIT, DEFAULT_OBJECT_ENTRY_LIMIT, ObjectCache,
};
use crate::database::DatFile;
use crate::error::{DatError, Result};
use crate::header::DatFileType;
use crate::objects::DatObject;

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
    /// opened only when the file exists.
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
    pub fn get<T: DatObject>(&self, id: u32) -> Result<Option<T>> {
        match T::DAT {
            DatFileType::Cell => self.cell.get(id),
            DatFileType::Local => self.local.get(id),
            DatFileType::Portal => match self.portal_source(id)? {
                DatSource::HighRes => self.high_res.as_ref().map_or(Ok(None), |d| d.get(id)),
                _ => self.portal.get(id),
            },
            DatFileType::Undefined | DatFileType::Other(_) => Ok(None),
        }
    }

    /// The dat a portal-type id is read from: the high-resolution dat when
    /// it is open and its directory holds the id, else the portal dat
    /// (whether or not the portal dat holds it).
    pub fn portal_source(&self, id: u32) -> Result<DatSource> {
        match &self.high_res {
            Some(high_res) if high_res.find_entry(id)?.is_some() => Ok(DatSource::HighRes),
            _ => Ok(DatSource::Portal),
        }
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
    pub fn resolve(&self, id: u32) -> Result<Option<DatSource>> {
        for source in DatSource::PRECEDENCE {
            if let Some(file) = self.file(source)
                && file.find_entry(id)?.is_some()
            {
                return Ok(Some(source));
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
}

/// Which of the four dats something was found in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
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
    /// of the default size.
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
    /// given sizes.
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

    fn get_from<T: DatObject + Send + Sync + 'static>(
        &self,
        source: DatSource,
        id: u32,
    ) -> Result<Option<Arc<T>>> {
        let Some(file) = self.dats.file(source) else {
            return Ok(None);
        };
        let cache = self.cache(source);
        if let Some(object) = cache.get::<T>(id) {
            return Ok(Some(object));
        }
        Ok(file
            .get::<T>(id)?
            .map(|object| cache.get_or_add(id, Arc::new(object))))
    }

    /// Read a typed object as [`DatCollection::get`] does (a portal object
    /// the high-resolution dat holds comes from there, any other from the
    /// portal dat), through the cache.
    pub fn get<T: DatObject + Send + Sync + 'static>(&self, id: u32) -> Result<Option<Arc<T>>> {
        match T::DAT {
            DatFileType::Cell => self.get_from(DatSource::Cell, id),
            DatFileType::Local => self.get_from(DatSource::Local, id),
            DatFileType::Portal => self.get_from(self.dats.portal_source(id)?, id),
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
