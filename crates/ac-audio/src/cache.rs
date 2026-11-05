//! Decoded waves and sound tables, read from the dats once and shared.

use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};

use ac_dat::objects::{EnumIdMap, SoundTable, Wave};
use ac_dat::{DatCollection, DatError};

use crate::error::AudioError;
use crate::wave::{self, WaveData};

/// Where sound data comes from. [`DatCollection`] is the real source; tests
/// supply their own.
pub trait SoundDats: Send + Sync {
    /// A wave by id; `Ok(None)` when it does not exist.
    fn wave(&self, id: u32) -> Result<Option<Wave>, AudioError>;
    /// A sound table by id; `Ok(None)` when it does not exist.
    fn sound_table(&self, id: u32) -> Result<Option<SoundTable>, AudioError>;
    /// An enum id map by id; `Ok(None)` when it does not exist.
    fn enum_id_map(&self, id: u32) -> Result<Option<EnumIdMap>, AudioError>;
    /// The portal dat's master enum map id.
    fn master_map_id(&self) -> u32;
}

fn dat_err(kind: &'static str, id: u32) -> impl FnOnce(DatError) -> AudioError {
    move |e| AudioError::Dat {
        kind,
        id,
        source: Arc::new(e),
    }
}

impl SoundDats for DatCollection {
    fn wave(&self, id: u32) -> Result<Option<Wave>, AudioError> {
        self.get(id).map_err(dat_err("wave", id))
    }
    fn sound_table(&self, id: u32) -> Result<Option<SoundTable>, AudioError> {
        self.get(id).map_err(dat_err("sound table", id))
    }
    fn enum_id_map(&self, id: u32) -> Result<Option<EnumIdMap>, AudioError> {
        self.get(id).map_err(dat_err("enum id map", id))
    }
    fn master_map_id(&self) -> u32 {
        self.portal.header().master_map_id as u32
    }
}

/// The default budget for decoded waves held in memory: 32 MiB.
pub const DEFAULT_MAX_WAVE_BYTES: u64 = 32 * 1024 * 1024;

/// Counters for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SoundCacheDiagnostics {
    /// Decoded waves held.
    pub cached_wave_count: usize,
    /// Their bytes.
    pub resident_wave_bytes: u64,
    /// The budget.
    pub budget_bytes: u64,
    /// Lookups answered from memory (including known-missing waves).
    pub hits: u64,
    /// Lookups that read the dats.
    pub misses: u64,
    /// Waves dropped to stay inside the budget.
    pub evictions: u64,
}

type Decoded = Result<Option<Arc<WaveData>>, AudioError>;

#[derive(Debug, Default)]
struct Lru {
    entries: HashMap<u32, (Arc<WaveData>, u64, u64)>,
    next_stamp: u64,
    resident: u64,
}

impl Lru {
    fn touch(&mut self, id: u32) -> Option<Arc<WaveData>> {
        self.next_stamp += 1;
        let stamp = self.next_stamp;
        let entry = self.entries.get_mut(&id)?;
        entry.2 = stamp;
        Some(entry.0.clone())
    }
}

/// Decoded waves (bounded, least recently used dropped first) and sound
/// tables (all kept). Safe to share between threads; a wave several threads
/// ask for at once is read and decoded once.
pub struct SoundCache {
    dats: Arc<dyn SoundDats>,
    max_wave_bytes: u64,
    tables: Mutex<HashMap<u32, Option<Arc<SoundTable>>>>,
    lru: Mutex<Lru>,
    missing: Mutex<HashSet<u32>>,
    inflight: Mutex<HashMap<u32, Arc<OnceLock<Decoded>>>>,
    hits: AtomicU64,
    misses: AtomicU64,
    evictions: AtomicU64,
}

impl std::fmt::Debug for SoundCache {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SoundCache")
            .field("diagnostics", &self.diagnostics())
            .finish_non_exhaustive()
    }
}

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    // These locks are never held across code that can panic; a poisoned one
    // means a bug elsewhere, and the panic is carried on.
    m.lock().expect("sound cache lock poisoned")
}

impl SoundCache {
    /// A cache with the default budget.
    #[must_use]
    pub fn new(dats: Arc<dyn SoundDats>) -> Self {
        Self::with_budget(dats, DEFAULT_MAX_WAVE_BYTES)
    }

    /// A cache with a byte budget for decoded waves (at least 1).
    #[must_use]
    pub fn with_budget(dats: Arc<dyn SoundDats>, max_wave_bytes: u64) -> Self {
        Self {
            dats,
            max_wave_bytes: max_wave_bytes.max(1),
            tables: Mutex::default(),
            lru: Mutex::default(),
            missing: Mutex::default(),
            inflight: Mutex::default(),
            hits: AtomicU64::new(0),
            misses: AtomicU64::new(0),
            evictions: AtomicU64::new(0),
        }
    }

    /// The source the cache reads from.
    #[must_use]
    pub fn dats(&self) -> &Arc<dyn SoundDats> {
        &self.dats
    }

    /// A decoded wave. `Ok(None)` when it does not exist or does not
    /// decode; that answer is remembered. A dat read error is returned and
    /// not remembered.
    pub fn wave(&self, id: u32) -> Decoded {
        if let Some(found) = self.lookup(id) {
            return Ok(found);
        }
        let cell = {
            let mut inflight = lock(&self.inflight);
            // Re-check under the in-flight lock: a decode that finished
            // between the first look and here has already published.
            if let Some(found) = self.lookup(id) {
                return Ok(found);
            }
            self.misses.fetch_add(1, Ordering::Relaxed);
            inflight
                .entry(id)
                .or_insert_with(|| Arc::new(OnceLock::new()))
                .clone()
        };
        let result = cell.get_or_init(|| self.decode_uncached(id)).clone();
        match &result {
            Ok(Some(w)) => {
                let w = self.admit(id, w.clone());
                self.finish_inflight(id, &cell);
                return Ok(Some(w));
            }
            Ok(None) => {
                lock(&self.missing).insert(id);
            }
            Err(_) => {}
        }
        self.finish_inflight(id, &cell);
        result
    }

    fn lookup(&self, id: u32) -> Option<Option<Arc<WaveData>>> {
        if let Some(w) = lock(&self.lru).touch(id) {
            self.hits.fetch_add(1, Ordering::Relaxed);
            return Some(Some(w));
        }
        if lock(&self.missing).contains(&id) {
            self.hits.fetch_add(1, Ordering::Relaxed);
            return Some(None);
        }
        None
    }

    fn finish_inflight(&self, id: u32, cell: &Arc<OnceLock<Decoded>>) {
        let mut inflight = lock(&self.inflight);
        if inflight.get(&id).is_some_and(|c| Arc::ptr_eq(c, cell)) {
            inflight.remove(&id);
        }
    }

    fn decode_uncached(&self, id: u32) -> Decoded {
        Ok(self
            .dats
            .wave(id)?
            .and_then(|w| wave::decode(&w.header, &w.data))
            .map(Arc::new))
    }

    fn admit(&self, id: u32, wave: Arc<WaveData>) -> Arc<WaveData> {
        let bytes = (wave.pcm_bytes.len() as u64).max(1);
        let mut lru = lock(&self.lru);
        if let Some(existing) = lru.touch(id) {
            return existing;
        }
        if bytes > self.max_wave_bytes {
            // Larger than the whole budget: served, not kept.
            return wave;
        }
        while lru.resident + bytes > self.max_wave_bytes {
            let Some((&oldest, _)) = lru.entries.iter().min_by_key(|(_, e)| e.2) else {
                break;
            };
            if let Some((_, b, _)) = lru.entries.remove(&oldest) {
                lru.resident -= b;
                self.evictions.fetch_add(1, Ordering::Relaxed);
            }
        }
        lru.next_stamp += 1;
        let stamp = lru.next_stamp;
        lru.entries.insert(id, (wave.clone(), bytes, stamp));
        lru.resident += bytes;
        wave
    }

    /// A sound table; `Ok(None)` when it does not exist. Kept once read.
    pub fn sound_table(&self, id: u32) -> Result<Option<Arc<SoundTable>>, AudioError> {
        if let Some(t) = lock(&self.tables).get(&id) {
            return Ok(t.clone());
        }
        let table = self.dats.sound_table(id)?.map(Arc::new);
        lock(&self.tables).insert(id, table.clone());
        Ok(table)
    }

    /// Decoded waves held.
    #[must_use]
    pub fn cached_wave_count(&self) -> usize {
        lock(&self.lru).entries.len()
    }

    /// Bytes of decoded waves held.
    #[must_use]
    pub fn resident_wave_bytes(&self) -> u64 {
        lock(&self.lru).resident
    }

    /// Sound tables read so far.
    #[must_use]
    pub fn cached_sound_table_count(&self) -> usize {
        lock(&self.tables).len()
    }

    /// Counters.
    #[must_use]
    pub fn diagnostics(&self) -> SoundCacheDiagnostics {
        let lru = lock(&self.lru);
        SoundCacheDiagnostics {
            cached_wave_count: lru.entries.len(),
            resident_wave_bytes: lru.resident,
            budget_bytes: self.max_wave_bytes,
            hits: self.hits.load(Ordering::Relaxed),
            misses: self.misses.load(Ordering::Relaxed),
            evictions: self.evictions.load(Ordering::Relaxed),
        }
    }
}

/// The enum slot the interface sound bank lives at.
pub const UI_SOUND_TABLE_ENUM_SLOT: u32 = 7;
/// The type key of the interface sound bank within that slot's map.
pub const UI_SOUND_TABLE_TYPE_KEY: u32 = 0x1000_0003;

/// The interface sound bank's sound table id, found through the dats' enum
/// maps: master map → slot 7 → type key `0x10000003`. 0 when the chain is
/// incomplete.
pub fn resolve_ui_sound_table(dats: &dyn SoundDats) -> Result<u32, AudioError> {
    let master_id = dats.master_map_id();
    if master_id == 0 {
        return Ok(0);
    }
    let Some(master) = dats.enum_id_map(master_id)? else {
        return Ok(0);
    };
    let Some(per_slot_id) = master
        .client_id(UI_SOUND_TABLE_ENUM_SLOT)
        .filter(|id| *id != 0)
    else {
        return Ok(0);
    };
    let Some(per_slot) = dats.enum_id_map(per_slot_id)? else {
        return Ok(0);
    };
    Ok(per_slot.client_id(UI_SOUND_TABLE_TYPE_KEY).unwrap_or(0))
}

#[cfg(test)]
pub(crate) mod fake {
    //! An in-memory [`SoundDats`] that counts wave reads.

    use std::collections::HashMap;
    use std::sync::Mutex;

    use super::*;

    #[derive(Default)]
    pub(crate) struct FakeDats {
        pub waves: HashMap<u32, Wave>,
        pub tables: HashMap<u32, SoundTable>,
        pub maps: HashMap<u32, EnumIdMap>,
        pub master: u32,
        pub calls: Mutex<HashMap<u32, usize>>,
        pub before_wave: Option<Box<dyn Fn(u32) + Send + Sync>>,
    }

    impl FakeDats {
        pub(crate) fn calls(&self, id: u32) -> usize {
            *self.calls.lock().unwrap().get(&id).unwrap_or(&0)
        }
    }

    impl SoundDats for FakeDats {
        fn wave(&self, id: u32) -> Result<Option<Wave>, AudioError> {
            if let Some(f) = &self.before_wave {
                f(id);
            }
            *self.calls.lock().unwrap().entry(id).or_insert(0) += 1;
            Ok(self.waves.get(&id).cloned())
        }
        fn sound_table(&self, id: u32) -> Result<Option<SoundTable>, AudioError> {
            Ok(self.tables.get(&id).cloned())
        }
        fn enum_id_map(&self, id: u32) -> Result<Option<EnumIdMap>, AudioError> {
            Ok(self.maps.get(&id).cloned())
        }
        fn master_map_id(&self) -> u32 {
            self.master
        }
    }

    pub(crate) fn pcm_header() -> Vec<u8> {
        let mut h = vec![0u8; 18];
        h[0] = 1;
        h[2] = 1;
        h[4..8].copy_from_slice(&22050u32.to_le_bytes());
        h[8..12].copy_from_slice(&44100u32.to_le_bytes());
        h[12] = 2;
        h[14] = 16;
        h
    }

    pub(crate) fn pcm_wave(id: u32, data_bytes: usize) -> Wave {
        Wave {
            id,
            header: pcm_header(),
            data: vec![0; data_bytes],
        }
    }

    pub(crate) fn mp3_wave_without_frames(id: u32) -> Wave {
        let mut header = vec![0u8; 18];
        header[0] = 0x55;
        header[2] = 0x02;
        header[4] = 0x44;
        header[5] = 0xAC;
        Wave {
            id,
            header,
            data: vec![0; 1024],
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/DatSoundCacheTests.cs`
    //! (its concurrency case, marked a known failure there, passes here).

    use std::sync::Barrier;
    use std::sync::mpsc;
    use std::thread;
    use std::time::Duration;

    use super::fake::*;
    use super::*;

    fn cache(dats: FakeDats, budget: u64) -> (Arc<FakeDats>, SoundCache) {
        let dats = Arc::new(dats);
        let c = SoundCache::with_budget(dats.clone(), budget);
        (dats, c)
    }

    fn with_waves(waves: &[(u32, Wave)]) -> FakeDats {
        FakeDats {
            waves: waves.iter().cloned().collect(),
            ..FakeDats::default()
        }
    }

    #[test]
    fn get_wave_decodes_and_returns_pcm_wave() {
        let (_, c) = cache(with_waves(&[(1, pcm_wave(1, 100))]), 10_000);
        let w = c.wave(1).unwrap().unwrap();
        assert_eq!(w.pcm_bytes.len(), 100);
        assert_eq!(c.cached_wave_count(), 1);
        assert_eq!(c.resident_wave_bytes(), 100);
    }

    #[test]
    fn missing_wave_returns_none_and_memoizes_without_repeated_lookup() {
        let (d, c) = cache(FakeDats::default(), 10_000);
        for _ in 0..3 {
            assert!(c.wave(7).unwrap().is_none());
        }
        assert_eq!(d.calls(7), 1);
        assert_eq!(c.cached_wave_count(), 0);
        assert_eq!(c.resident_wave_bytes(), 0);
    }

    #[test]
    fn undecodable_wave_returns_none_and_does_not_consume_budget() {
        let (d, c) = cache(with_waves(&[(2, mp3_wave_without_frames(2))]), 10_000);
        assert!(c.wave(2).unwrap().is_none());
        assert_eq!(c.cached_wave_count(), 0);
        assert_eq!(c.resident_wave_bytes(), 0);
        assert!(c.wave(2).unwrap().is_none());
        assert_eq!(d.calls(2), 1);
    }

    #[test]
    fn cache_hit_returns_same_instance_without_redecoding() {
        let (d, c) = cache(with_waves(&[(1, pcm_wave(1, 100))]), 10_000);
        let a = c.wave(1).unwrap().unwrap();
        let b = c.wave(1).unwrap().unwrap();
        assert!(Arc::ptr_eq(&a, &b));
        assert_eq!(d.calls(1), 1);
    }

    #[test]
    fn evicts_least_recently_used_when_over_budget() {
        let (d, c) = cache(
            with_waves(&[
                (1, pcm_wave(1, 150)),
                (2, pcm_wave(2, 150)),
                (3, pcm_wave(3, 150)),
            ]),
            300,
        );
        let a1 = c.wave(1).unwrap().unwrap();
        let b1 = c.wave(2).unwrap().unwrap();
        assert_eq!((c.cached_wave_count(), c.resident_wave_bytes()), (2, 300));
        assert!(Arc::ptr_eq(&a1, &c.wave(1).unwrap().unwrap()));
        let c1 = c.wave(3).unwrap().unwrap();
        assert_eq!((c.cached_wave_count(), c.resident_wave_bytes()), (2, 300));
        assert!(Arc::ptr_eq(&a1, &c.wave(1).unwrap().unwrap()));
        assert_eq!(d.calls(1), 1);
        assert!(Arc::ptr_eq(&c1, &c.wave(3).unwrap().unwrap()));
        assert_eq!(d.calls(3), 1);
        let b2 = c.wave(2).unwrap().unwrap();
        assert!(!Arc::ptr_eq(&b1, &b2));
        assert_eq!(d.calls(2), 2);
        let diag = c.diagnostics();
        assert_eq!(diag.cached_wave_count, 2);
        assert_eq!(diag.resident_wave_bytes, 300);
        assert_eq!(diag.budget_bytes, 300);
        assert!(diag.hits >= 3);
        assert!(diag.misses >= 4);
        assert!(diag.evictions >= 2);
    }

    #[test]
    fn oversize_single_wave_is_served_but_not_retained() {
        let (d, c) = cache(with_waves(&[(9, pcm_wave(9, 500))]), 100);
        let a = c.wave(9).unwrap().unwrap();
        assert_eq!((c.cached_wave_count(), c.resident_wave_bytes()), (0, 0));
        let b = c.wave(9).unwrap().unwrap();
        assert!(!Arc::ptr_eq(&a, &b));
        assert_eq!(d.calls(9), 2);
    }

    #[test]
    fn concurrent_same_id_publishes_one_canonical_wave_and_decodes_once() {
        let (d, c) = cache(with_waves(&[(1, pcm_wave(1, 1000))]), 10_000);
        let c = Arc::new(c);
        let barrier = Arc::new(Barrier::new(16));
        let handles: Vec<_> = (0..16)
            .map(|_| {
                let (c, barrier) = (c.clone(), barrier.clone());
                thread::spawn(move || {
                    barrier.wait();
                    (0..20)
                        .map(|_| c.wave(1).unwrap().unwrap())
                        .collect::<Vec<_>>()
                })
            })
            .collect();
        let all: Vec<Arc<WaveData>> = handles
            .into_iter()
            .flat_map(|h| h.join().unwrap())
            .collect();
        assert!(all.iter().all(|w| Arc::ptr_eq(w, &all[0])));
        assert_eq!(d.calls(1), 1);
    }

    #[test]
    fn a_waiting_reader_gets_the_winners_wave_without_decoding_again() {
        let (entered_tx, entered_rx) = mpsc::channel::<()>();
        let (release_tx, release_rx) = mpsc::channel::<()>();
        let entered_tx = Mutex::new(entered_tx);
        let release_rx = Mutex::new(release_rx);
        let dats = FakeDats {
            waves: [(1, pcm_wave(1, 1000))].into_iter().collect(),
            before_wave: Some(Box::new(move |_| {
                entered_tx.lock().unwrap().send(()).unwrap();
                release_rx
                    .lock()
                    .unwrap()
                    .recv_timeout(Duration::from_secs(5))
                    .unwrap();
            })),
            ..FakeDats::default()
        };
        let (d, c) = cache(dats, 10_000);
        let c = Arc::new(c);
        let winner = {
            let c = c.clone();
            thread::spawn(move || c.wave(1).unwrap().unwrap())
        };
        entered_rx.recv_timeout(Duration::from_secs(5)).unwrap();
        let waiter = {
            let c = c.clone();
            thread::spawn(move || c.wave(1).unwrap().unwrap())
        };
        thread::sleep(Duration::from_millis(50));
        release_tx.send(()).unwrap();
        let canonical = winner.join().unwrap();
        let other = waiter.join().unwrap();
        assert!(Arc::ptr_eq(&canonical, &other));
        assert_eq!(d.calls(1), 1);
    }

    #[test]
    fn concurrent_missing_id_never_fails_and_all_return_none() {
        let (_, c) = cache(FakeDats::default(), 10_000);
        let c = Arc::new(c);
        let handles: Vec<_> = (0..8)
            .map(|_| {
                let c = c.clone();
                thread::spawn(move || (0..25).all(|_| c.wave(5).unwrap().is_none()))
            })
            .collect();
        assert!(handles.into_iter().all(|h| h.join().unwrap()));
        assert_eq!(c.cached_wave_count(), 0);
    }

    fn map(id: u32, client: &[(u32, u32)]) -> EnumIdMap {
        EnumIdMap {
            id,
            client_enum_to_id: client.to_vec(),
            client_enum_to_name: Vec::new(),
            server_enum_to_id: Vec::new(),
            server_enum_to_name: Vec::new(),
        }
    }

    #[test]
    fn ui_sound_table_resolves_through_the_enum_map_chain() {
        let dats = FakeDats {
            master: 0x2500_0000,
            maps: [
                (0x2500_0000, map(0x2500_0000, &[(7, 0x2500_0007)])),
                (0x2500_0007, map(0x2500_0007, &[(0x1000_0003, 0x2000_004B)])),
            ]
            .into_iter()
            .collect(),
            ..FakeDats::default()
        };
        assert_eq!(resolve_ui_sound_table(&dats).unwrap(), 0x2000_004B);
    }

    #[test]
    fn ui_sound_table_is_zero_when_the_chain_breaks() {
        let mut dats = FakeDats {
            master: 0x2500_0000,
            maps: [(0x2500_0000, map(0x2500_0000, &[(7, 0x2500_0007)]))]
                .into_iter()
                .collect(),
            ..FakeDats::default()
        };
        assert_eq!(resolve_ui_sound_table(&dats).unwrap(), 0);
        dats.master = 0;
        assert_eq!(resolve_ui_sound_table(&dats).unwrap(), 0);
    }
}
