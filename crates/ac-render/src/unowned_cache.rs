//! A budget for GPU resources nothing draws any more: they stay resident,
//! oldest first out, until the unowned set passes its byte or count budget
//! (so walking back to where one was costs no reload), and are released
//! from the oldest only beyond it. Taking a resource back into use removes
//! it from the budget.

use std::collections::{BTreeMap, HashMap};
use std::hash::Hash;

/// Mesh budget for unowned object meshes (OpenAC `ResidencyBudgetOptions`
/// default: 1 GiB of object meshes, 50 unowned entries).
pub const OBJECT_MESH_UNOWNED_BYTES: u64 = 1024 * 1024 * 1024;
/// Unowned object meshes kept.
pub const OBJECT_MESH_UNOWNED_ENTRIES: usize = 50;
/// Unowned standalone textures kept, bytes (OpenAC: 32 MiB).
pub const STANDALONE_UNOWNED_BYTES: u64 = 32 * 1024 * 1024;
/// Unowned standalone textures kept, entries (OpenAC: 256).
pub const STANDALONE_UNOWNED_ENTRIES: usize = 256;

/// One mebibyte.
const MIB: u64 = 1024 * 1024;

/// The unowned budgets the window keeps, from the defaults above or the
/// startup environment.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ResidencyBudgets {
    /// Unowned object meshes, bytes.
    pub object_mesh_bytes: u64,
    /// Unowned object meshes, entries.
    pub object_mesh_entries: usize,
    /// Unowned standalone textures, bytes.
    pub standalone_bytes: u64,
    /// Unowned standalone textures, entries.
    pub standalone_entries: usize,
}

impl Default for ResidencyBudgets {
    fn default() -> Self {
        Self {
            object_mesh_bytes: OBJECT_MESH_UNOWNED_BYTES,
            object_mesh_entries: OBJECT_MESH_UNOWNED_ENTRIES,
            standalone_bytes: STANDALONE_UNOWNED_BYTES,
            standalone_entries: STANDALONE_UNOWNED_ENTRIES,
        }
    }
}

impl ResidencyBudgets {
    /// The budgets with `OPENAC_RESIDENCY_MESH_GPU_MIB`,
    /// `OPENAC_RESIDENCY_MESH_UNOWNED_ENTRIES`,
    /// `OPENAC_RESIDENCY_STANDALONE_UNOWNED_MIB` and
    /// `OPENAC_RESIDENCY_STANDALONE_UNOWNED_ENTRIES` over the defaults; a
    /// value that is not a positive whole number (or whose bytes overflow)
    /// keeps the default.
    #[must_use]
    pub fn from_vars(env: impl Fn(&str) -> Option<String>) -> Self {
        let d = Self::default();
        let mib = |name: &str, fallback: u64| {
            env(name)
                .and_then(|v| v.trim().parse::<i64>().ok())
                .filter(|v| *v > 0)
                .and_then(|v| u64::try_from(v).ok())
                .and_then(|v| v.checked_mul(MIB))
                .filter(|bytes| i64::try_from(*bytes).is_ok())
                .unwrap_or(fallback)
        };
        let count = |name: &str, fallback: usize| {
            env(name)
                .and_then(|v| v.trim().parse::<i32>().ok())
                .filter(|v| *v > 0)
                .and_then(|v| usize::try_from(v).ok())
                .unwrap_or(fallback)
        };
        Self {
            object_mesh_bytes: mib("OPENAC_RESIDENCY_MESH_GPU_MIB", d.object_mesh_bytes),
            object_mesh_entries: count(
                "OPENAC_RESIDENCY_MESH_UNOWNED_ENTRIES",
                d.object_mesh_entries,
            ),
            standalone_bytes: mib(
                "OPENAC_RESIDENCY_STANDALONE_UNOWNED_MIB",
                d.standalone_bytes,
            ),
            standalone_entries: count(
                "OPENAC_RESIDENCY_STANDALONE_UNOWNED_ENTRIES",
                d.standalone_entries,
            ),
        }
    }
}

/// What went wrong.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum UnownedCacheError {
    /// A resource was marked again with another size.
    #[error("a cached resource changed size from {0} to {1} bytes")]
    SizeChanged(u64, u64),
    /// A resource of zero bytes.
    #[error("a cached resource needs a size")]
    ZeroSize,
}

/// The unowned resources and their least-recently-used order.
#[derive(Debug, Clone)]
pub struct BoundedUnownedResourceCache<K: Eq + Hash + Clone> {
    budget_bytes: u64,
    maximum_count: usize,
    entries: HashMap<K, (u64, u64)>,
    order: BTreeMap<u64, K>,
    next: u64,
    resident_bytes: u64,
    /// Keep unowned resources within the budget (off: release them all).
    pub retain_unowned: bool,
}

impl<K: Eq + Hash + Clone> BoundedUnownedResourceCache<K> {
    /// A cache of at most `budget_bytes` and `maximum_count` (at least 1).
    #[must_use]
    pub fn new(budget_bytes: u64, maximum_count: usize) -> Self {
        Self {
            budget_bytes,
            maximum_count: maximum_count.max(1),
            entries: HashMap::new(),
            order: BTreeMap::new(),
            next: 0,
            resident_bytes: 0,
            retain_unowned: true,
        }
    }

    /// Unowned resources held.
    #[must_use]
    pub fn count(&self) -> usize {
        self.entries.len()
    }

    /// Their bytes.
    #[must_use]
    pub fn resident_bytes(&self) -> u64 {
        self.resident_bytes
    }

    /// Whether `key` is held unowned.
    #[must_use]
    pub fn contains(&self, key: &K) -> bool {
        self.entries.contains_key(key)
    }

    /// Nothing uses `key` (of `bytes`): hold it, newest; marking it again
    /// refreshes its place.
    ///
    /// # Errors
    /// Zero bytes, or a size other than the one it was marked with.
    pub fn mark_unowned(&mut self, key: K, bytes: u64) -> Result<(), UnownedCacheError> {
        if bytes == 0 {
            return Err(UnownedCacheError::ZeroSize);
        }
        let seq = self.next;
        self.next += 1;
        if let Some((size, old)) = self.entries.get_mut(&key) {
            if *size != bytes {
                return Err(UnownedCacheError::SizeChanged(*size, bytes));
            }
            self.order.remove(old);
            *old = seq;
            self.order.insert(seq, key);
            return Ok(());
        }
        self.order.insert(seq, key.clone());
        self.entries.insert(key, (bytes, seq));
        self.resident_bytes += bytes;
        Ok(())
    }

    /// `key` is in use again: it leaves the budget. False when it was not
    /// held.
    pub fn mark_owned(&mut self, key: &K) -> bool {
        self.take(key)
    }

    /// The oldest unowned resource when the set is over its budget (or
    /// retention is off), taken out of the cache.
    pub fn try_take_oldest_over_budget(&mut self) -> Option<K> {
        if self.retain_unowned
            && self.resident_bytes <= self.budget_bytes
            && self.entries.len() <= self.maximum_count
        {
            return None;
        }
        self.try_take_oldest()
    }

    /// The oldest unowned resource, taken out of the cache.
    pub fn try_take_oldest(&mut self) -> Option<K> {
        let (_, key) = self.order.pop_first()?;
        if let Some((bytes, _)) = self.entries.remove(&key) {
            self.resident_bytes -= bytes;
        }
        Some(key)
    }

    /// Take `key` out of the cache. False when it was not held.
    pub fn take(&mut self, key: &K) -> bool {
        match self.entries.remove(key) {
            Some((bytes, seq)) => {
                self.order.remove(&seq);
                self.resident_bytes -= bytes;
                true
            }
            None => false,
        }
    }

    /// Forget everything.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.order.clear();
        self.resident_bytes = 0;
    }

    /// One sweep over the `resident` resources (with their bytes): those
    /// `owned` names leave the budget, the others not held yet are held,
    /// newest; then the oldest beyond the budget (all of them when
    /// `retain` is off) are taken out and returned, to be released.
    pub fn settle(
        &mut self,
        resident: impl IntoIterator<Item = (K, u64)>,
        owned: impl Fn(&K) -> bool,
        retain: bool,
    ) -> Vec<K> {
        for (key, bytes) in resident {
            if owned(&key) {
                self.mark_owned(&key);
            } else if !self.contains(&key) {
                // A zero-sized resource is never held (nothing to free).
                let _ = self.mark_unowned(key, bytes);
            }
        }
        std::iter::from_fn(|| {
            if retain {
                self.try_take_oldest_over_budget()
            } else {
                self.try_take_oldest()
            }
        })
        .collect()
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Rendering/BoundedUnownedResourceCacheTests.cs`.

    use super::*;

    /// OpenAC `ResidencyBudgetOptions.Parse`: positive whole numbers of
    /// MiB or entries replace the defaults; anything else keeps them.
    #[test]
    fn budgets_come_from_the_environment_or_the_defaults() {
        assert_eq!(
            ResidencyBudgets::from_vars(|_| None),
            ResidencyBudgets::default()
        );
        let set = ResidencyBudgets::from_vars(|name| {
            match name {
                "OPENAC_RESIDENCY_MESH_GPU_MIB" => Some(" 256 "),
                "OPENAC_RESIDENCY_MESH_UNOWNED_ENTRIES" => Some("7"),
                "OPENAC_RESIDENCY_STANDALONE_UNOWNED_MIB" => Some("0"),
                "OPENAC_RESIDENCY_STANDALONE_UNOWNED_ENTRIES" => Some("many"),
                _ => None,
            }
            .map(str::to_owned)
        });
        assert_eq!(set.object_mesh_bytes, 256 * MIB);
        assert_eq!(set.object_mesh_entries, 7);
        assert_eq!(set.standalone_bytes, STANDALONE_UNOWNED_BYTES);
        assert_eq!(set.standalone_entries, STANDALONE_UNOWNED_ENTRIES);
        // More MiB than fit in a signed 64-bit byte count keeps the default.
        let huge = ResidencyBudgets::from_vars(|name| {
            (name == "OPENAC_RESIDENCY_MESH_GPU_MIB").then(|| i64::MAX.to_string())
        });
        assert_eq!(huge.object_mesh_bytes, OBJECT_MESH_UNOWNED_BYTES);
    }

    fn cache(bytes: u64) -> BoundedUnownedResourceCache<&'static str> {
        BoundedUnownedResourceCache::new(bytes, usize::MAX)
    }

    #[test]
    fn taking_a_resource_back_removes_it_from_the_budget_and_the_order() {
        let mut c = cache(10);
        c.mark_unowned("first", 6).unwrap();
        c.mark_unowned("second", 6).unwrap();
        assert!(c.mark_owned(&"first"));
        assert_eq!((c.count(), c.resident_bytes()), (1, 6));
        assert_eq!(c.try_take_oldest_over_budget(), None);
    }

    #[test]
    fn with_retention_off_everything_goes_oldest_first() {
        let mut c = cache(100);
        c.mark_unowned("first", 6).unwrap();
        c.mark_unowned("second", 6).unwrap();
        assert_eq!(c.try_take_oldest_over_budget(), None);
        c.retain_unowned = false;
        assert_eq!(c.try_take_oldest_over_budget(), Some("first"));
        c.retain_unowned = true;
        assert_eq!(c.try_take_oldest_over_budget(), None);
        assert_eq!(c.count(), 1);
    }

    #[test]
    fn only_the_oldest_go_while_over_budget() {
        let mut c = cache(10);
        for k in ["first", "second", "third"] {
            c.mark_unowned(k, 6).unwrap();
        }
        assert_eq!(c.try_take_oldest_over_budget(), Some("first"));
        assert_eq!(c.resident_bytes(), 12);
        assert_eq!(c.try_take_oldest_over_budget(), Some("second"));
        assert_eq!(c.resident_bytes(), 6);
        assert_eq!(c.try_take_oldest_over_budget(), None);
    }

    #[test]
    fn marking_again_refreshes_the_order_without_counting_twice() {
        let mut c = cache(5);
        c.mark_unowned("first", 4).unwrap();
        c.mark_unowned("second", 4).unwrap();
        c.mark_unowned("first", 4).unwrap();
        assert_eq!((c.count(), c.resident_bytes()), (2, 8));
        assert_eq!(c.try_take_oldest_over_budget(), Some("second"));
    }

    #[test]
    fn a_size_change_for_the_same_key_is_refused() {
        let mut c = cache(100);
        c.mark_unowned("key", 4).unwrap();
        assert_eq!(
            c.mark_unowned("key", 8),
            Err(UnownedCacheError::SizeChanged(4, 8))
        );
    }

    #[test]
    fn the_oldest_can_be_taken_below_the_budget() {
        let mut c = cache(100);
        c.mark_unowned("old", 4).unwrap();
        c.mark_unowned("new", 4).unwrap();
        assert_eq!(c.try_take_oldest_over_budget(), None);
        assert_eq!(c.try_take_oldest(), Some("old"));
        assert_eq!(c.resident_bytes(), 4);
    }

    #[test]
    fn exact_byte_and_count_limits_stay() {
        let mut c = BoundedUnownedResourceCache::new(64, 2);
        c.mark_unowned(1, 32).unwrap();
        c.mark_unowned(2, 32).unwrap();
        assert_eq!(c.try_take_oldest_over_budget(), None);
        assert_eq!((c.count(), c.resident_bytes()), (2, 64));
    }

    #[test]
    fn a_count_overflow_takes_only_the_oldest() {
        let mut c = BoundedUnownedResourceCache::new(1_000, 2);
        for k in ["oldest", "middle", "newest"] {
            c.mark_unowned(k, 1).unwrap();
        }
        assert_eq!(c.try_take_oldest_over_budget(), Some("oldest"));
        assert_eq!(c.count(), 2);
        assert_eq!(c.try_take_oldest_over_budget(), None);
    }
}
