//! A map that iterates in the order a .NET `Dictionary` enumerates: entries
//! sit in slots in insertion order, a removed entry frees its slot, and the
//! next insert reuses the most recently freed slot. Where OpenAC walks a
//! dictionary and the walk order is observable (which owner's physics
//! scripts fire first in a tick), this keeps the same order.

use std::collections::HashMap;
use std::hash::Hash;

#[derive(Debug, Clone)]
pub(crate) struct SlotMap<K, V> {
    slots: Vec<Option<(K, V)>>,
    free: Vec<usize>,
    index: HashMap<K, usize>,
}

impl<K, V> Default for SlotMap<K, V> {
    fn default() -> Self {
        Self {
            slots: Vec::new(),
            free: Vec::new(),
            index: HashMap::new(),
        }
    }
}

impl<K: Copy + Eq + Hash, V> SlotMap<K, V> {
    pub(crate) fn len(&self) -> usize {
        self.index.len()
    }

    pub(crate) fn contains_key(&self, key: &K) -> bool {
        self.index.contains_key(key)
    }

    pub(crate) fn get(&self, key: &K) -> Option<&V> {
        let slot = *self.index.get(key)?;
        self.slots[slot].as_ref().map(|(_, v)| v)
    }

    pub(crate) fn get_mut(&mut self, key: &K) -> Option<&mut V> {
        let slot = *self.index.get(key)?;
        self.slots[slot].as_mut().map(|(_, v)| v)
    }

    /// Insert a new entry; an existing key keeps its slot and gets the new
    /// value.
    pub(crate) fn insert(&mut self, key: K, value: V) {
        if let Some(&slot) = self.index.get(&key) {
            self.slots[slot] = Some((key, value));
            return;
        }
        let slot = match self.free.pop() {
            Some(slot) => {
                self.slots[slot] = Some((key, value));
                slot
            }
            None => {
                self.slots.push(Some((key, value)));
                self.slots.len() - 1
            }
        };
        self.index.insert(key, slot);
    }

    pub(crate) fn remove(&mut self, key: &K) -> Option<V> {
        let slot = self.index.remove(key)?;
        self.free.push(slot);
        self.slots[slot].take().map(|(_, v)| v)
    }

    pub(crate) fn clear(&mut self) {
        self.slots.clear();
        self.free.clear();
        self.index.clear();
    }

    pub(crate) fn keys(&self) -> impl Iterator<Item = K> + '_ {
        self.slots
            .iter()
            .filter_map(|s| s.as_ref().map(|(k, _)| *k))
    }

    pub(crate) fn values(&self) -> impl Iterator<Item = &V> {
        self.slots.iter().filter_map(|s| s.as_ref().map(|(_, v)| v))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reuses_the_most_recently_freed_slot_like_a_dotnet_dictionary() {
        let mut m = SlotMap::default();
        for k in 1..=4 {
            m.insert(k, ());
        }
        m.remove(&2);
        m.remove(&3);
        m.insert(5, ());
        m.insert(6, ());
        assert_eq!(m.keys().collect::<Vec<_>>(), [1, 6, 5, 4]);
        assert_eq!(m.len(), 4);
    }
}
