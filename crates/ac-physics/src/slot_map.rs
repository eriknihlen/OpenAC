//! A map from `u32` keys that iterates in slot order, where a new key takes
//! the most recently freed slot (else a new one at the end).
//!
//! OpenAC keeps several of its tables in .NET dictionaries and some results
//! depend on their enumeration order (the first resident landblock holding
//! a point wins; captured shadow rows come out owner by owner). A .NET
//! dictionary enumerates its entry array in index order and refills freed
//! entries last-freed first; this map does the same.

use std::collections::HashMap;

/// A `u32`-keyed map with .NET dictionary enumeration order.
#[derive(Debug, Clone)]
pub(crate) struct SlotMap<V> {
    slots: Vec<Option<(u32, V)>>,
    index: HashMap<u32, usize>,
    free: Vec<usize>,
}

impl<V> Default for SlotMap<V> {
    fn default() -> Self {
        Self {
            slots: Vec::new(),
            index: HashMap::new(),
            free: Vec::new(),
        }
    }
}

impl<V> SlotMap<V> {
    pub(crate) fn len(&self) -> usize {
        self.index.len()
    }

    pub(crate) fn contains_key(&self, key: u32) -> bool {
        self.index.contains_key(&key)
    }

    pub(crate) fn get(&self, key: u32) -> Option<&V> {
        let slot = *self.index.get(&key)?;
        self.slots[slot].as_ref().map(|(_, v)| v)
    }

    pub(crate) fn get_mut(&mut self, key: u32) -> Option<&mut V> {
        let slot = *self.index.get(&key)?;
        self.slots[slot].as_mut().map(|(_, v)| v)
    }

    /// Sets the value for `key`: in place when present, else in the most
    /// recently freed slot, else at the end.
    pub(crate) fn insert(&mut self, key: u32, value: V) {
        if let Some(&slot) = self.index.get(&key) {
            self.slots[slot] = Some((key, value));
            return;
        }
        let slot = if let Some(slot) = self.free.pop() {
            self.slots[slot] = Some((key, value));
            slot
        } else {
            self.slots.push(Some((key, value)));
            self.slots.len() - 1
        };
        self.index.insert(key, slot);
    }

    pub(crate) fn remove(&mut self, key: u32) -> Option<V> {
        let slot = self.index.remove(&key)?;
        self.free.push(slot);
        self.slots[slot].take().map(|(_, v)| v)
    }

    pub(crate) fn iter(&self) -> impl Iterator<Item = (u32, &V)> {
        self.slots
            .iter()
            .filter_map(|s| s.as_ref().map(|(k, v)| (*k, v)))
    }

    pub(crate) fn iter_mut(&mut self) -> impl Iterator<Item = (u32, &mut V)> {
        self.slots
            .iter_mut()
            .filter_map(|s| s.as_mut().map(|(k, v)| (*k, v)))
    }

    pub(crate) fn keys(&self) -> impl Iterator<Item = u32> + '_ {
        self.iter().map(|(k, _)| k)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn freed_slots_are_refilled_last_freed_first() {
        let mut m = SlotMap::default();
        for k in 1..=4 {
            m.insert(k, k * 10);
        }
        m.remove(2);
        m.remove(3);
        m.insert(5, 50);
        m.insert(6, 60);
        assert_eq!(m.keys().collect::<Vec<_>>(), vec![1, 6, 5, 4]);
        m.insert(1, 11);
        assert_eq!(m.get(1), Some(&11));
        assert_eq!(m.len(), 4);
    }
}
