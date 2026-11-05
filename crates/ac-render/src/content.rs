//! What every window of the process reads the world from: the dats with
//! their object caches and the decoded-texture cache.
//!
//! One [`ContentSlot`] per process. The first window that needs the dats
//! opens them (on its loader thread). They are kept only while a window is
//! open: when it closes the host swaps in an empty slot
//! ([`ContentSlot::emptied`]), so a session with no window holds no copy of
//! the world's art (a headless bot opened a window once and kept about
//! 100 MB of caches), and the next window opens them again.

use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock};

use ac_dat::cache::{CacheStats, DecodedTextureCache, UNKNOWN_OBJECT_ESTIMATE};
use ac_dat::{CacheLimit, CachedDats, DatCollection, ObjectCacheLimits};

/// The streaming area the caches are sized for when nothing else is
/// asked: the High quality preset's near and far radius (OpenAC's High
/// preset and this client's default).
pub const DEFAULT_AREA: (u32, u32) = (4, 12);

/// The object caches sized to keep what one area load reads, so loading
/// the same area again (a window re-attached, or moved to another session
/// in the same place) reads no object twice. OpenAC keeps 256 objects and
/// 64 MiB per dat, and one Holtburg load (near 4, far 12) evicted 8,076.
///
/// Measured working sets (spike `asset-loading-bench`, caches unbounded):
/// Holtburg near 4 / far 12 holds 2,113 portal, 230 high-resolution and
/// 1,196 cell objects; near 5 / far 15: 2,191, 239 and 1,591; the Town
/// Network dungeon (one landblock): 600, 27 and 1,985. Sized from the area
/// with about 1.5 times Holtburg's density per near landblock: portal
/// 512 + 32 per near landblock, high-resolution 256 + 4 per near landblock,
/// cell one terrain object per far landblock + 32 per near landblock +
/// 2,048 for a large dungeon; the language dat keeps OpenAC's size. The
/// byte limits are the entry limits at the estimate charged an object of
/// unknown size ([`UNKNOWN_OBJECT_ESTIMATE`]), so an area of typical
/// objects is bounded by its count, and textures (charged their pixels)
/// by bytes. Holding Holtburg's set takes about 40 MB more than OpenAC's
/// sizes.
#[must_use]
pub fn object_cache_limits(near: u32, far: u32) -> ObjectCacheLimits {
    let side = |r: u32| 2 * r as usize + 1;
    let near_blocks = side(near) * side(near);
    let far_blocks = side(far.max(near)) * side(far.max(near));
    let limit = |entries: usize| CacheLimit {
        entries,
        bytes: (entries as u64).saturating_mul(UNKNOWN_OBJECT_ESTIMATE),
    };
    ObjectCacheLimits {
        portal: limit(512 + 32 * near_blocks),
        high_res: limit(256 + 4 * near_blocks),
        local: CacheLimit::default(),
        cell: limit(far_blocks + 32 * near_blocks + 2048),
    }
}

/// The area a process's caches are sized for: the stored display settings'
/// preset and draw distance with the startup overrides (without
/// "interface only", which a player turns off again), never smaller than
/// [`DEFAULT_AREA`], since the player can raise the preset while the
/// process runs and the caches keep their size.
#[must_use]
pub fn cache_area(
    display: &ac_host::settings::DisplaySettings,
    overrides: &crate::quality::QualityOverrides,
) -> (u32, u32) {
    let display = crate::quality::effective_display(display);
    let (near, far) = crate::quality::QualitySettings::from_preset(display.quality)
        .with_landscape_draw_distance(display.landscape_draw_distance)
        .with_overrides(overrides)
        .radii();
    (near.max(DEFAULT_AREA.0), far.max(DEFAULT_AREA.1))
}

/// The dats and the caches in front of them.
#[derive(Debug)]
pub struct Content {
    /// The four dats, each behind a bounded object cache.
    pub dats: CachedDats,
    /// Decoded texture pixels, bounded.
    pub textures: DecodedTextureCache,
}

impl Content {
    /// Open the dats in `dir` with object caches sized for the default
    /// streaming area ([`DEFAULT_AREA`]).
    pub fn open(dir: &Path) -> Result<Self, ac_dat::DatError> {
        Self::open_for_area(dir, DEFAULT_AREA.0, DEFAULT_AREA.1)
    }

    /// Open the dats in `dir` with object caches sized for an area of
    /// `near` and `far` landblock radius ([`object_cache_limits`]).
    pub fn open_for_area(dir: &Path, near: u32, far: u32) -> Result<Self, ac_dat::DatError> {
        Ok(Self {
            dats: CachedDats::open_with_limits(dir, &object_cache_limits(near, far))?,
            textures: DecodedTextureCache::default(),
        })
    }

    /// Object-cache and texture-cache counters.
    #[must_use]
    pub fn stats(&self) -> (CacheStats, CacheStats) {
        (self.dats.stats(), self.textures.stats())
    }
}

/// The process's content, opened on first use.
#[derive(Debug)]
pub struct ContentSlot {
    dir: PathBuf,
    /// The near and far radius its caches are sized for.
    area: (u32, u32),
    opened: OnceLock<Result<Arc<Content>, String>>,
    ui: OnceLock<Result<Arc<DatCollection>, String>>,
}

impl ContentSlot {
    /// A slot that opens the dats in `dir` when first asked, with caches
    /// sized for the default area ([`DEFAULT_AREA`]).
    #[must_use]
    pub fn new(dir: PathBuf) -> Self {
        Self::for_area(dir, DEFAULT_AREA.0, DEFAULT_AREA.1)
    }

    /// A slot whose caches are sized for an area of `near` and `far`
    /// landblock radius.
    #[must_use]
    pub fn for_area(dir: PathBuf, near: u32, far: u32) -> Self {
        Self {
            dir,
            area: (near, far),
            opened: OnceLock::new(),
            ui: OnceLock::new(),
        }
    }

    /// The dats the UI reads (layouts, fonts, strings, its art), opened on
    /// the first call. The UI runs on the window's thread with caches of
    /// its own, so it reads through its own handles rather than the
    /// loader's.
    pub fn ui_dats(&self) -> Result<Arc<DatCollection>, String> {
        self.ui
            .get_or_init(|| {
                DatCollection::open(&self.dir)
                    .map(Arc::new)
                    .map_err(|error| {
                        format!("cannot open the dats in {}: {error}", self.dir.display())
                    })
            })
            .clone()
    }

    /// A slot for the same dats and area with nothing opened yet: what the
    /// host holds once the window that used this one has closed.
    #[must_use]
    pub fn emptied(&self) -> Self {
        Self::for_area(self.dir.clone(), self.area.0, self.area.1)
    }

    /// The directory the dats come from.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.dir
    }

    /// The content, opening it on the first call (which blocks other
    /// callers until it is open). A failure to open is remembered: the
    /// directory does not change while the process runs.
    pub fn get(&self) -> Result<Arc<Content>, String> {
        self.opened
            .get_or_init(|| {
                let (near, far) = self.area;
                Content::open_for_area(&self.dir, near, far)
                    .map(Arc::new)
                    .map_err(|error| {
                        format!("cannot open the dats in {}: {error}", self.dir.display())
                    })
            })
            .clone()
    }

    /// The content if it is already open.
    #[must_use]
    pub fn get_if_open(&self) -> Option<Arc<Content>> {
        self.opened.get()?.as_ref().ok().cloned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_host::settings::{DisplaySettings, QualityPreset};

    #[test]
    fn an_emptied_slot_keeps_the_dats_and_area_and_holds_nothing_open() {
        let slot = ContentSlot::for_area(PathBuf::from("no-such-dats"), 5, 15);
        let _ = slot.get();
        let emptied = slot.emptied();
        assert_eq!(emptied.directory(), Path::new("no-such-dats"));
        assert_eq!(emptied.area, (5, 15));
        assert!(emptied.opened.get().is_none() && emptied.ui.get().is_none());
    }

    #[test]
    fn the_default_area_holds_the_measured_working_sets_with_headroom() {
        let l = object_cache_limits(DEFAULT_AREA.0, DEFAULT_AREA.1);
        // Measured (see `object_cache_limits`): Holtburg near 4 / far 12,
        // and the Town Network dungeon's cell objects; a quarter more.
        for (limit, measured) in [(l.portal, 2113), (l.high_res, 230), (l.cell, 1985)] {
            assert!(limit.entries * 4 >= measured * 5, "{limit:?} {measured}");
            assert_eq!(limit.bytes, limit.entries as u64 * UNKNOWN_OBJECT_ESTIMATE);
        }
        assert!(l.cell.entries * 4 >= 1196 * 5);
        assert_eq!(l.local, CacheLimit::default());
        // Far larger than OpenAC's 256 objects per dat.
        assert!(l.portal.entries > 10 * CacheLimit::default().entries);
    }

    #[test]
    fn the_limits_grow_with_the_area_and_far_is_never_below_near() {
        let small = object_cache_limits(1, 3);
        let ultra = object_cache_limits(5, 15);
        let default = object_cache_limits(4, 12);
        assert!(small.portal.entries < default.portal.entries);
        assert!(default.portal.entries < ultra.portal.entries);
        assert!(default.cell.entries < ultra.cell.entries);
        assert_eq!(object_cache_limits(4, 0), object_cache_limits(4, 4));
    }

    #[test]
    fn the_cache_area_follows_the_settings_and_overrides_but_not_below_the_default() {
        let display = |quality, landscape_draw_distance| DisplaySettings {
            quality,
            landscape_draw_distance,
            ..DisplaySettings::default()
        };
        let none = crate::quality::QualityOverrides::default();
        // The stored default draw distance (8) narrows the far radius; the
        // default area is the floor.
        let stored = DisplaySettings::default().landscape_draw_distance;
        assert_eq!(
            cache_area(&display(QualityPreset::High, stored), &none),
            (4, 12)
        );
        assert_eq!(
            cache_area(&display(QualityPreset::Low, stored), &none),
            (4, 12)
        );
        assert_eq!(
            cache_area(&display(QualityPreset::Ultra, stored), &none),
            (5, 12)
        );
        assert_eq!(
            cache_area(&display(QualityPreset::Ultra, 20), &none),
            (5, 20)
        );
        let wide = crate::quality::QualityOverrides {
            near_radius: Some(6),
            far_radius: Some(20),
            ..none
        };
        assert_eq!(
            cache_area(&display(QualityPreset::High, stored), &wide),
            (6, 20)
        );
    }
}
