use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::cache::{
    CacheStats, DEFAULT_OBJECT_BYTE_LIMIT, DEFAULT_OBJECT_ENTRY_LIMIT, ObjectCache,
};
use crate::database::DatFile;
use crate::error::Result;
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

/// The four dats a client reads, opened from one directory.
#[derive(Debug)]
pub struct DatCollection {
    /// Models, textures, animations and tables.
    pub portal: DatFile,
    /// Landblocks and interior cells.
    pub cell: DatFile,
    /// High-resolution textures (a portal-type dat).
    pub high_res: DatFile,
    /// English strings and fonts.
    pub local: DatFile,
}

impl DatCollection {
    /// Open the four dats in `dir`, checking each header's kind.
    pub fn open(dir: impl AsRef<Path>) -> Result<Self> {
        let dir = dir.as_ref();
        Ok(Self {
            portal: DatFile::open_as(dir.join(PORTAL_DAT), DatFileType::Portal)?,
            cell: DatFile::open_as(dir.join(CELL_DAT), DatFileType::Cell)?,
            high_res: DatFile::open_as(dir.join(HIGHRES_DAT), DatFileType::Portal)?,
            local: DatFile::open_as(dir.join(LOCAL_DAT), DatFileType::Local)?,
        })
    }

    /// Read a typed object from the dat its type lives in. Portal objects
    /// missing from the portal dat are looked up in the high-resolution dat.
    pub fn get<T: DatObject>(&self, id: u32) -> Result<Option<T>> {
        match T::DAT {
            DatFileType::Cell => self.cell.get(id),
            DatFileType::Local => self.local.get(id),
            DatFileType::Portal => match self.portal.get(id)? {
                Some(object) => Ok(Some(object)),
                None => self.high_res.get(id),
            },
            DatFileType::Undefined | DatFileType::Other(_) => Ok(None),
        }
    }

    /// The dats with their display names, in a fixed order.
    #[must_use]
    pub fn all(&self) -> [(&'static str, &DatFile); 4] {
        [
            ("portal", &self.portal),
            ("cell", &self.cell),
            ("highres", &self.high_res),
            ("local", &self.local),
        ]
    }

    /// The dat an id is read from when its type is not known in advance:
    /// the first of portal, high-resolution, language and cell that holds
    /// it. `None` when no dat has it.
    pub fn resolve(&self, id: u32) -> Result<Option<DatSource>> {
        for source in DatSource::PRECEDENCE {
            if self.file(source).find_entry(id)?.is_some() {
                return Ok(Some(source));
            }
        }
        Ok(None)
    }

    /// One of the four dats.
    #[must_use]
    pub fn file(&self, source: DatSource) -> &DatFile {
        match source {
            DatSource::Portal => &self.portal,
            DatSource::HighRes => &self.high_res,
            DatSource::Local => &self.local,
            DatSource::Cell => &self.cell,
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
    /// The order an id of unknown type is looked up in.
    pub const PRECEDENCE: [Self; 4] = [Self::Portal, Self::HighRes, Self::Local, Self::Cell];
}

/// The first dat in [`DatSource::PRECEDENCE`] that `holds` the id: the
/// portal dat wins over everything, then the high-resolution dat, then the
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

/// The four dats with a bounded cache of decoded objects in front of each,
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
    /// Open the four dats in `dir`, each with a cache of the default size.
    pub fn open(dir: impl AsRef<Path>) -> Result<Self> {
        let directory = dir.as_ref().to_path_buf();
        Ok(Self::new(DatCollection::open(&directory)?, directory))
    }

    /// Put caches in front of already opened dats.
    #[must_use]
    pub fn new(dats: DatCollection, directory: PathBuf) -> Self {
        Self::with_limits(dats, directory, &ObjectCacheLimits::default())
    }

    /// Open the four dats in `dir` with caches of the given sizes.
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
            // In `DatSource::PRECEDENCE` order.
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
        let mut out = [(DatSource::Portal, 0, 0); 4];
        for ((slot, source), cache) in out.iter_mut().zip(DatSource::PRECEDENCE).zip(&self.caches) {
            *slot = (source, cache.len(), cache.estimated_bytes());
        }
        out
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

    /// Hits, misses and evictions of the four caches together.
    #[must_use]
    pub fn stats(&self) -> CacheStats {
        self.caches
            .iter()
            .fold(CacheStats::default(), |sum, c| sum + c.stats())
    }

    fn cache(&self, source: DatSource) -> &ObjectCache {
        let index = DatSource::PRECEDENCE
            .iter()
            .position(|s| *s == source)
            .unwrap_or(0);
        &self.caches[index]
    }

    fn get_from<T: DatObject + Send + Sync + 'static>(
        &self,
        source: DatSource,
        id: u32,
    ) -> Result<Option<Arc<T>>> {
        let cache = self.cache(source);
        if let Some(object) = cache.get::<T>(id) {
            return Ok(Some(object));
        }
        Ok(self
            .dats
            .file(source)
            .get::<T>(id)?
            .map(|object| cache.get_or_add(id, Arc::new(object))))
    }

    /// Read a typed object as [`DatCollection::get`] does (portal objects
    /// missing from the portal dat come from the high-resolution dat),
    /// through the cache.
    pub fn get<T: DatObject + Send + Sync + 'static>(&self, id: u32) -> Result<Option<Arc<T>>> {
        match T::DAT {
            DatFileType::Cell => self.get_from(DatSource::Cell, id),
            DatFileType::Local => self.get_from(DatSource::Local, id),
            DatFileType::Portal => match self.get_from(DatSource::Portal, id)? {
                Some(object) => Ok(Some(object)),
                None => self.get_from(DatSource::HighRes, id),
            },
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
    //! Cases from OpenAC `tests/AcDream.Content.Tests/DatResolutionPrecedenceTests.cs`.
    use super::*;

    #[test]
    fn portal_wins_even_when_every_other_dat_also_holds_the_id() {
        assert_eq!(preferred_source(|_| true), Some(DatSource::Portal));
    }

    #[test]
    fn the_rest_go_high_res_then_language_then_cell() {
        assert_eq!(
            preferred_source(|s| s != DatSource::Portal),
            Some(DatSource::HighRes)
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
}
