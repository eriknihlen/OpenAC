//! Bounded caches of what is read from the dats: decoded objects (per dat)
//! and decoded texture pixels.
//!
//! Both caches keep the most recently used entries within an entry limit
//! and a byte limit, evict the least recently used first, never admit an
//! entry larger than the whole byte limit, and keep one canonical copy per
//! key: when two threads decode the same thing at once, the first to publish
//! wins and the other is handed the published copy. Entries are shared as
//! [`Arc`]s, so a reader keeps its copy alive after eviction.

use std::any::{Any, TypeId};
use std::collections::{BTreeMap, HashMap};
use std::hash::Hash;
use std::ops::Add;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};

use crate::btree::Node;
use crate::objects::{PixelFormat, RenderSurface};
use crate::texture::{DecodeOptions, DecodedTexture};

/// Hits, misses and evictions of a cache since it was made.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CacheStats {
    /// Lookups that found an entry.
    pub hits: u64,
    /// Lookups that found nothing.
    pub misses: u64,
    /// Entries dropped to make room.
    pub evictions: u64,
}

impl Add for CacheStats {
    type Output = Self;

    fn add(self, other: Self) -> Self {
        Self {
            hits: self.hits + other.hits,
            misses: self.misses + other.misses,
            evictions: self.evictions + other.evictions,
        }
    }
}

#[derive(Debug, Default)]
struct Counters {
    hits: AtomicU64,
    misses: AtomicU64,
    evictions: AtomicU64,
}

impl Counters {
    fn snapshot(&self) -> CacheStats {
        CacheStats {
            hits: self.hits.load(Ordering::Relaxed),
            misses: self.misses.load(Ordering::Relaxed),
            evictions: self.evictions.load(Ordering::Relaxed),
        }
    }

    fn bump(counter: &AtomicU64) {
        counter.fetch_add(1, Ordering::Relaxed);
    }
}

/// Least-recently-used bookkeeping: entries with their charged size, and a
/// use order (oldest first).
#[derive(Debug)]
struct Lru<K, V> {
    entries: HashMap<K, Slot<V>>,
    order: BTreeMap<u64, K>,
    clock: u64,
    bytes: u64,
}

#[derive(Debug)]
struct Slot<V> {
    value: V,
    bytes: u64,
    last_use: u64,
}

impl<K: Copy + Eq + Hash, V: Clone> Lru<K, V> {
    fn new() -> Self {
        Self {
            entries: HashMap::new(),
            order: BTreeMap::new(),
            clock: 0,
            bytes: 0,
        }
    }

    fn tick(&mut self) -> u64 {
        self.clock += 1;
        self.clock
    }

    /// The entry, now the most recently used.
    fn touch(&mut self, key: &K) -> Option<V> {
        let now = self.tick();
        let slot = self.entries.get_mut(key)?;
        self.order.remove(&slot.last_use);
        slot.last_use = now;
        self.order.insert(now, *key);
        Some(slot.value.clone())
    }

    fn len(&self) -> usize {
        self.entries.len()
    }

    /// Drop the least recently used entry; false when empty.
    fn evict_oldest(&mut self) -> bool {
        let Some((_, key)) = self.order.pop_first() else {
            return false;
        };
        if let Some(slot) = self.entries.remove(&key) {
            self.bytes -= slot.bytes;
        }
        true
    }

    fn insert(&mut self, key: K, value: V, bytes: u64) {
        let now = self.tick();
        self.order.insert(now, key);
        self.entries.insert(
            key,
            Slot {
                value,
                bytes,
                last_use: now,
            },
        );
        self.bytes += bytes;
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    // The bookkeeping is never left half-updated by a panic (every update is
    // a few infallible steps), so a poisoned lock still holds a usable state.
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Directory nodes a dat keeps after reading them.
pub const DEFAULT_NODE_CACHE_LIMIT: usize = 500;

/// A bounded cache of decoded directory nodes, keyed by the block offset
/// they were read from: a lookup walks root to leaf, and the nodes near the
/// root (and the leaves of ids read together) are read again and again.
/// Keeps the most recently used `limit` nodes; thread-safe.
#[derive(Debug)]
pub(crate) struct NodeCache {
    limit: usize,
    lru: Mutex<Lru<i32, Arc<Node>>>,
    counters: Counters,
}

impl NodeCache {
    /// A cache of at most `limit` nodes; 0 keeps none.
    pub(crate) fn new(limit: usize) -> Self {
        Self {
            limit,
            lru: Mutex::new(Lru::new()),
            counters: Counters::default(),
        }
    }

    /// The node read from `offset`, if held (now the most recently used).
    pub(crate) fn get(&self, offset: i32) -> Option<Arc<Node>> {
        let found = lock(&self.lru).touch(&offset);
        Counters::bump(if found.is_some() {
            &self.counters.hits
        } else {
            &self.counters.misses
        });
        found
    }

    /// Keep `node` unless one is already held for its offset, evicting the
    /// least recently used beyond the limit; returns the node held (or
    /// `node` when the cache keeps nothing).
    pub(crate) fn insert(&self, node: Arc<Node>) -> Arc<Node> {
        if self.limit == 0 {
            return node;
        }
        let mut lru = lock(&self.lru);
        if let Some(existing) = lru.touch(&node.offset) {
            return existing;
        }
        while lru.len() >= self.limit && lru.evict_oldest() {
            Counters::bump(&self.counters.evictions);
        }
        lru.insert(node.offset, node.clone(), 1);
        node
    }

    /// Nodes held.
    pub(crate) fn len(&self) -> usize {
        lock(&self.lru).len()
    }

    /// Hits, misses and evictions so far.
    pub(crate) fn stats(&self) -> CacheStats {
        self.counters.snapshot()
    }
}

/// What a cached object is charged against the byte limit when its type
/// has no better estimate.
pub const UNKNOWN_OBJECT_ESTIMATE: u64 = 128 * 1024;

/// Entries a dat's object cache keeps by default.
pub const DEFAULT_OBJECT_ENTRY_LIMIT: usize = 256;

/// Estimated bytes a dat's object cache keeps by default.
pub const DEFAULT_OBJECT_BYTE_LIMIT: u64 = 64 * 1024 * 1024;

/// The production size estimate: a render surface is charged its stored
/// pixels (plus a little for the object itself), at least
/// [`UNKNOWN_OBJECT_ESTIMATE`]; anything else is charged
/// [`UNKNOWN_OBJECT_ESTIMATE`].
#[must_use]
pub fn estimate_retained_bytes(value: &(dyn Any + Send + Sync)) -> u64 {
    match value.downcast_ref::<RenderSurface>() {
        Some(surface) => UNKNOWN_OBJECT_ESTIMATE.max(surface.data.len() as u64 + 256),
        None => UNKNOWN_OBJECT_ESTIMATE,
    }
}

type Estimator = Box<dyn Fn(&(dyn Any + Send + Sync)) -> u64 + Send + Sync>;
type AnyObject = Arc<dyn Any + Send + Sync>;

/// A bounded cache of decoded dat objects, keyed by object type and file
/// id (the same id read as two different types are two entries).
pub struct ObjectCache {
    entry_limit: usize,
    byte_limit: u64,
    estimate: Estimator,
    lru: Mutex<Lru<(TypeId, u32), AnyObject>>,
    counters: Counters,
}

impl std::fmt::Debug for ObjectCache {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ObjectCache")
            .field("entry_limit", &self.entry_limit)
            .field("byte_limit", &self.byte_limit)
            .field("count", &self.len())
            .field("estimated_bytes", &self.estimated_bytes())
            .field("stats", &self.stats())
            .finish_non_exhaustive()
    }
}

impl Default for ObjectCache {
    fn default() -> Self {
        Self::new(DEFAULT_OBJECT_ENTRY_LIMIT, DEFAULT_OBJECT_BYTE_LIMIT)
    }
}

impl ObjectCache {
    /// A cache holding at most `entry_limit` objects and `byte_limit`
    /// estimated bytes (each at least 1), sized by
    /// [`estimate_retained_bytes`].
    #[must_use]
    pub fn new(entry_limit: usize, byte_limit: u64) -> Self {
        Self::with_estimator(entry_limit, byte_limit, estimate_retained_bytes)
    }

    /// As [`ObjectCache::new`] with a custom size estimate.
    #[must_use]
    pub fn with_estimator(
        entry_limit: usize,
        byte_limit: u64,
        estimate: impl Fn(&(dyn Any + Send + Sync)) -> u64 + Send + Sync + 'static,
    ) -> Self {
        Self {
            entry_limit: entry_limit.max(1),
            byte_limit: byte_limit.max(1),
            estimate: Box::new(estimate),
            lru: Mutex::new(Lru::new()),
            counters: Counters::default(),
        }
    }

    /// Hits, misses and evictions so far.
    #[must_use]
    pub fn stats(&self) -> CacheStats {
        self.counters.snapshot()
    }

    /// Objects held.
    #[must_use]
    pub fn len(&self) -> usize {
        lock(&self.lru).entries.len()
    }

    /// True when nothing is held.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Estimated bytes held.
    #[must_use]
    pub fn estimated_bytes(&self) -> u64 {
        lock(&self.lru).bytes
    }

    /// The cached object of type `T` with this file id, now the most
    /// recently used. Counts a hit or a miss.
    pub fn get<T: Any + Send + Sync>(&self, id: u32) -> Option<Arc<T>> {
        let found = lock(&self.lru).touch(&(TypeId::of::<T>(), id));
        match found {
            Some(value) => {
                Counters::bump(&self.counters.hits);
                value.downcast::<T>().ok()
            }
            None => {
                Counters::bump(&self.counters.misses);
                None
            }
        }
    }

    /// Publish `candidate` unless an object is already cached under the key,
    /// and return the canonical one. An object estimated larger than the
    /// whole byte limit is returned without being kept.
    pub fn get_or_add<T: Any + Send + Sync>(&self, id: u32, candidate: Arc<T>) -> Arc<T> {
        let key = (TypeId::of::<T>(), id);
        let bytes = (self.estimate)(&*candidate).max(1);
        let mut lru = lock(&self.lru);
        if let Some(existing) = lru.touch(&key)
            && let Ok(existing) = existing.downcast::<T>()
        {
            return existing;
        }
        if bytes > self.byte_limit {
            return candidate;
        }
        while lru.entries.len() >= self.entry_limit || lru.bytes > self.byte_limit - bytes {
            if !lru.evict_oldest() {
                break;
            }
            Counters::bump(&self.counters.evictions);
        }
        lru.insert(key, candidate.clone(), bytes);
        candidate
    }
}

/// What a decoded texture is keyed by: the render surface and the decode
/// options that change its pixels for that surface's format.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DecodedTextureKey {
    /// Render surface id.
    pub render_surface_id: u32,
    /// Holes were made transparent.
    pub clip_map: bool,
    /// Alpha-only pixels went to every channel.
    pub additive: bool,
}

impl DecodedTextureKey {
    /// The key for decoding `format` with `options`: an option is part of
    /// the key only for the formats it changes (a clip map changes paletted
    /// and block-compressed pixels, the additive flag alpha-only ones), so
    /// surfaces that differ only in an option that does nothing share one
    /// decode.
    #[must_use]
    pub fn new(render_surface_id: u32, format: PixelFormat, options: DecodeOptions) -> Self {
        let clip_changes = matches!(
            format,
            PixelFormat::P8
                | PixelFormat::INDEX16
                | PixelFormat::DXT1
                | PixelFormat::DXT3
                | PixelFormat::DXT5
        );
        let additive_changes = matches!(format, PixelFormat::A8 | PixelFormat::CUSTOM_LSCAPE_ALPHA);
        Self {
            render_surface_id,
            clip_map: clip_changes && options.clip_map,
            additive: additive_changes && options.additive,
        }
    }
}

/// Bytes the decoded-texture cache keeps by default.
pub const DEFAULT_TEXTURE_BYTE_LIMIT: u64 = 64 * 1024 * 1024;

/// Textures the decoded-texture cache keeps by default.
pub const DEFAULT_TEXTURE_ENTRY_LIMIT: usize = 128;

/// One decode in progress, which other threads asking for the same key
/// wait on instead of decoding again.
#[derive(Debug, Default)]
struct InFlight {
    done: Mutex<Option<Option<Arc<DecodedTexture>>>>,
    finished: Condvar,
}

/// A bounded cache of decoded texture pixels shared by every reader.
#[derive(Debug)]
pub struct DecodedTextureCache {
    byte_limit: u64,
    entry_limit: usize,
    lru: Mutex<Lru<DecodedTextureKey, Arc<DecodedTexture>>>,
    in_flight: Mutex<HashMap<DecodedTextureKey, Arc<InFlight>>>,
    counters: Counters,
}

impl Default for DecodedTextureCache {
    fn default() -> Self {
        Self::new(DEFAULT_TEXTURE_BYTE_LIMIT, DEFAULT_TEXTURE_ENTRY_LIMIT)
    }
}

impl DecodedTextureCache {
    /// A cache holding at most `byte_limit` bytes of pixels in at most
    /// `entry_limit` textures; a limit of 0 keeps nothing.
    #[must_use]
    pub fn new(byte_limit: u64, entry_limit: usize) -> Self {
        Self {
            byte_limit,
            entry_limit,
            lru: Mutex::new(Lru::new()),
            in_flight: Mutex::new(HashMap::new()),
            counters: Counters::default(),
        }
    }

    /// Hits, misses and evictions so far.
    #[must_use]
    pub fn stats(&self) -> CacheStats {
        self.counters.snapshot()
    }

    /// Textures held.
    #[must_use]
    pub fn len(&self) -> usize {
        lock(&self.lru).entries.len()
    }

    /// True when nothing is held.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Pixel bytes held.
    #[must_use]
    pub fn resident_bytes(&self) -> u64 {
        lock(&self.lru).bytes
    }

    /// The cached texture, now the most recently used. Counts a hit or a
    /// miss.
    pub fn get(&self, key: DecodedTextureKey) -> Option<Arc<DecodedTexture>> {
        let found = lock(&self.lru).touch(&key);
        Counters::bump(if found.is_some() {
            &self.counters.hits
        } else {
            &self.counters.misses
        });
        found
    }

    /// Keep `texture` unless the key already has one, and return the
    /// canonical texture and whether it is held by the cache. A texture
    /// larger than the byte limit, or any texture when the entry limit is
    /// 0, is returned without being kept.
    pub fn retain_or_use(
        &self,
        key: DecodedTextureKey,
        texture: Arc<DecodedTexture>,
    ) -> (Arc<DecodedTexture>, bool) {
        let bytes = texture.rgba.len() as u64;
        let mut lru = lock(&self.lru);
        if let Some(existing) = lru.touch(&key) {
            return (existing, true);
        }
        if self.entry_limit == 0 || bytes > self.byte_limit {
            return (texture, false);
        }
        while !lru.entries.is_empty()
            && (lru.entries.len() >= self.entry_limit || lru.bytes + bytes > self.byte_limit)
        {
            lru.evict_oldest();
            Counters::bump(&self.counters.evictions);
        }
        lru.insert(key, texture.clone(), bytes);
        (texture, true)
    }

    /// The cached texture, or the result of `decode` kept in the cache.
    /// While one thread decodes a key, others asking for it wait for that
    /// result instead of decoding again; a failed decode is not remembered,
    /// so the next request tries again. Returns the texture and whether the
    /// cache holds it.
    pub fn get_or_create<E>(
        &self,
        key: DecodedTextureKey,
        decode: impl FnOnce() -> Result<DecodedTexture, E>,
    ) -> Result<(Arc<DecodedTexture>, bool), E> {
        if let Some(cached) = self.get(key) {
            return Ok((cached, true));
        }
        let (flight, leader) = {
            let mut in_flight = lock(&self.in_flight);
            match in_flight.get(&key) {
                Some(flight) => (flight.clone(), false),
                None => {
                    let flight = Arc::new(InFlight::default());
                    in_flight.insert(key, flight.clone());
                    (flight, true)
                }
            }
        };
        if !leader {
            let mut done = lock(&flight.done);
            while done.is_none() {
                done = flight
                    .finished
                    .wait(done)
                    .unwrap_or_else(PoisonError::into_inner);
            }
            if let Some(Some(texture)) = done.as_ref() {
                return Ok(self.retain_or_use(key, texture.clone()));
            }
            // The leader's decode failed; try for ourselves.
            drop(done);
            return decode().map(|t| self.retain_or_use(key, Arc::new(t)));
        }
        let result = decode().map(|t| self.retain_or_use(key, Arc::new(t)));
        *lock(&flight.done) = Some(result.as_ref().ok().map(|(t, _)| t.clone()));
        flight.finished.notify_all();
        lock(&self.in_flight).remove(&key);
        result
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.Content.Tests/BoundedDatObjectCacheTests.cs`
    //! and `DecodedTextureCacheTests.cs`.
    use super::*;
    use crate::objects::{Palette, Surface, SurfaceBase, SurfaceType};
    use crate::types::ColorArgb;

    fn surface(id: u32) -> Arc<Surface> {
        Arc::new(Surface {
            id,
            surface_type: SurfaceType::default(),
            base: SurfaceBase::Solid(ColorArgb::default()),
            translucency: 0.0,
            luminosity: 0.0,
            diffuse: 0.0,
        })
    }

    fn cache(entry_limit: usize, byte_limit: u64) -> ObjectCache {
        ObjectCache::with_estimator(entry_limit, byte_limit, |_| 1)
    }

    #[test]
    fn get_or_add_evicts_the_least_recently_used_at_the_entry_limit() {
        let c = cache(2, 100);
        let (first, second, third) = (surface(1), surface(2), surface(3));
        c.get_or_add(1, first.clone());
        c.get_or_add(2, second);
        assert!(c.get::<Surface>(1).is_some()); // first is now the hottest
        c.get_or_add(3, third.clone());
        assert!(Arc::ptr_eq(&c.get::<Surface>(1).unwrap(), &first));
        assert!(c.get::<Surface>(2).is_none());
        assert!(Arc::ptr_eq(&c.get::<Surface>(3).unwrap(), &third));
        assert_eq!(c.len(), 2);
    }

    #[test]
    fn get_or_add_enforces_the_byte_budget_and_never_keeps_an_oversize_entry() {
        let c = ObjectCache::with_estimator(10, 10, |v| {
            u64::from(v.downcast_ref::<Surface>().map_or(0, |s| s.id))
        });
        c.get_or_add(6, surface(6));
        c.get_or_add(5, surface(5));
        assert!(c.get::<Surface>(6).is_none());
        assert!(c.get::<Surface>(5).is_some());
        assert_eq!(c.estimated_bytes(), 5);

        let oversize = surface(11);
        assert!(Arc::ptr_eq(&c.get_or_add(11, oversize.clone()), &oversize));
        assert!(c.get::<Surface>(11).is_none());
        assert_eq!(c.len(), 1);
        assert_eq!(c.estimated_bytes(), 5);
    }

    #[test]
    fn production_estimator_charges_a_render_surface_its_pixels() {
        const LIMIT: u64 = 512 * 1024;
        let c = ObjectCache::new(10, LIMIT);
        let oversize = Arc::new(RenderSurface {
            id: 12,
            data_category: 0,
            width: 0,
            height: 0,
            format: PixelFormat::default(),
            data: vec![0; LIMIT as usize + 1],
            default_palette_id: 0,
        });
        assert!(Arc::ptr_eq(&c.get_or_add(12, oversize.clone()), &oversize));
        assert!(c.get::<RenderSurface>(12).is_none());
        assert_eq!((c.len(), c.estimated_bytes()), (0, 0));
    }

    #[test]
    fn a_duplicate_key_keeps_one_canonical_object() {
        let c = cache(4, 100);
        let (canonical, duplicate) = (surface(7), surface(7));
        assert!(Arc::ptr_eq(&c.get_or_add(7, canonical.clone()), &canonical));
        assert!(Arc::ptr_eq(&c.get_or_add(7, duplicate), &canonical));
        assert_eq!((c.len(), c.estimated_bytes()), (1, 1));
    }

    #[test]
    fn concurrent_duplicates_publish_one_canonical_object() {
        let c = Arc::new(cache(4, 100));
        let results: Vec<Arc<Surface>> = std::thread::scope(|scope| {
            let handles: Vec<_> = (0..8)
                .map(|t| {
                    let c = &c;
                    scope.spawn(move || {
                        (0..125)
                            .map(|i| c.get_or_add(42, surface(1000 + t * 125 + i)))
                            .collect::<Vec<_>>()
                    })
                })
                .collect();
            handles
                .into_iter()
                .flat_map(|h| h.join().unwrap())
                .collect()
        });
        let canonical = &results[0];
        assert!(results.iter().all(|r| Arc::ptr_eq(r, canonical)));
        assert!(Arc::ptr_eq(&c.get::<Surface>(42).unwrap(), canonical));
        assert_eq!(c.len(), 1);
    }

    #[test]
    fn the_same_id_as_different_types_does_not_alias() {
        let c = cache(4, 100);
        let s = surface(99);
        let p = Arc::new(Palette {
            id: 99,
            colors: Vec::new(),
        });
        c.get_or_add(99, s.clone());
        c.get_or_add(99, p.clone());
        assert!(Arc::ptr_eq(&c.get::<Surface>(99).unwrap(), &s));
        assert!(Arc::ptr_eq(&c.get::<Palette>(99).unwrap(), &p));
        assert_eq!(c.len(), 2);
    }

    #[test]
    fn object_stats_count_hits_misses_and_evictions() {
        let c = cache(1, 100);
        assert!(c.get::<Surface>(1).is_none()); // miss
        c.get_or_add(1, surface(1));
        assert!(c.get::<Surface>(1).is_some()); // hit
        c.get_or_add(2, surface(2)); // the entry limit of 1 evicts id 1
        assert_eq!(
            c.stats(),
            CacheStats {
                hits: 1,
                misses: 1,
                evictions: 1
            }
        );
    }

    fn key(id: u32) -> DecodedTextureKey {
        DecodedTextureKey {
            render_surface_id: id,
            clip_map: false,
            additive: false,
        }
    }

    fn pixels(len: usize) -> Arc<DecodedTexture> {
        Arc::new(DecodedTexture {
            width: 1,
            height: 1,
            rgba: vec![0; len],
        })
    }

    #[test]
    fn retain_or_use_evicts_the_least_recently_used_to_meet_the_byte_budget() {
        let c = DecodedTextureCache::new(8, 8);
        let (first, second, third) = (pixels(4), pixels(4), pixels(4));
        assert!(c.retain_or_use(key(1), first.clone()).1);
        assert!(c.retain_or_use(key(2), second).1);
        assert!(c.get(key(1)).is_some()); // id 2 is now the least recently used
        let (kept, cached) = c.retain_or_use(key(3), third.clone());
        assert!(cached && Arc::ptr_eq(&kept, &third));
        assert!(c.get(key(1)).is_some());
        assert!(c.get(key(2)).is_none());
        assert!(c.get(key(3)).is_some());
        assert_eq!(c.resident_bytes(), 8);
    }

    #[test]
    fn retain_or_use_never_admits_an_oversize_texture() {
        let c = DecodedTextureCache::new(3, 8);
        let p = pixels(4);
        let (used, cached) = c.retain_or_use(key(1), p.clone());
        assert!(!cached && Arc::ptr_eq(&used, &p));
        assert!(c.get(key(1)).is_none());
        assert_eq!((c.len(), c.resident_bytes()), (0, 0));
    }

    #[test]
    fn an_existing_canonical_texture_wins_a_decode_race() {
        let c = DecodedTextureCache::new(16, 8);
        let canonical = pixels(4);
        c.retain_or_use(key(1), canonical.clone());
        let (result, cached) = c.retain_or_use(key(1), pixels(4));
        assert!(cached && Arc::ptr_eq(&result, &canonical));
        assert_eq!((c.len(), c.resident_bytes()), (1, 4));
    }

    #[test]
    fn retain_or_use_enforces_the_entry_budget() {
        let c = DecodedTextureCache::new(1024, 2);
        for id in 1..=3 {
            c.retain_or_use(key(id), pixels(1));
        }
        assert!(c.get(key(1)).is_none());
        assert!(c.get(key(2)).is_some());
        assert!(c.get(key(3)).is_some());
        assert_eq!(c.len(), 2);
    }

    #[test]
    fn surface_flags_that_change_the_decode_do_not_alias() {
        let c = DecodedTextureCache::new(1024, 8);
        let k = |clip_map, additive| DecodedTextureKey {
            render_surface_id: 1,
            clip_map,
            additive,
        };
        let (opaque, clip, additive) = (pixels(1), pixels(1), pixels(1));
        c.retain_or_use(k(false, false), opaque.clone());
        c.retain_or_use(k(true, false), clip.clone());
        c.retain_or_use(k(false, true), additive.clone());
        assert!(Arc::ptr_eq(&c.get(k(false, false)).unwrap(), &opaque));
        assert!(Arc::ptr_eq(&c.get(k(true, false)).unwrap(), &clip));
        assert!(Arc::ptr_eq(&c.get(k(false, true)).unwrap(), &additive));
    }

    #[test]
    fn the_key_keeps_only_flags_that_change_the_format() {
        let clip = DecodeOptions {
            clip_map: true,
            additive: true,
        };
        let k = |format| DecodedTextureKey::new(5, format, clip);
        assert_eq!(
            (k(PixelFormat::P8).clip_map, k(PixelFormat::P8).additive),
            (true, false)
        );
        assert!(k(PixelFormat::DXT1).clip_map);
        assert_eq!(
            (k(PixelFormat::A8).clip_map, k(PixelFormat::A8).additive),
            (false, true)
        );
        assert!(k(PixelFormat::CUSTOM_LSCAPE_ALPHA).additive);
        assert_eq!(k(PixelFormat::R8G8B8), key(5));
    }

    #[test]
    fn a_concurrent_miss_decodes_once() {
        let c = DecodedTextureCache::new(1024, 8);
        let calls = AtomicU64::new(0);
        let results: Vec<Arc<DecodedTexture>> = std::thread::scope(|scope| {
            let handles: Vec<_> = (0..8)
                .map(|_| {
                    scope.spawn(|| {
                        c.get_or_create(key(1), || {
                            calls.fetch_add(1, Ordering::SeqCst);
                            std::thread::sleep(std::time::Duration::from_millis(50));
                            Ok::<_, ()>(DecodedTexture {
                                width: 1,
                                height: 1,
                                rgba: vec![1, 2, 3, 4],
                            })
                        })
                        .unwrap()
                        .0
                    })
                })
                .collect();
            handles.into_iter().map(|h| h.join().unwrap()).collect()
        });
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        assert!(results.iter().all(|r| Arc::ptr_eq(r, &results[0])));
    }

    #[test]
    fn a_failed_decode_can_be_retried() {
        let c = DecodedTextureCache::new(1024, 8);
        let mut calls = 0;
        assert!(
            c.get_or_create(key(1), || {
                calls += 1;
                Err("decode failed")
            })
            .is_err()
        );
        let (texture, kept) = c
            .get_or_create(key(1), || {
                calls += 1;
                Ok::<_, &str>(DecodedTexture {
                    width: 1,
                    height: 1,
                    rgba: vec![1],
                })
            })
            .unwrap();
        assert_eq!(calls, 2);
        assert!(kept);
        assert_eq!(texture.rgba, [1]);
    }

    #[test]
    fn texture_stats_count_hits_misses_and_evictions() {
        let c = DecodedTextureCache::new(4, 1);
        assert!(c.get(key(1)).is_none()); // miss
        c.retain_or_use(key(1), pixels(4));
        assert!(c.get(key(1)).is_some()); // hit
        c.retain_or_use(key(2), pixels(4)); // the entry limit of 1 evicts key 1
        assert_eq!(
            c.stats(),
            CacheStats {
                hits: 1,
                misses: 1,
                evictions: 1
            }
        );
    }
}
