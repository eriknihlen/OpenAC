//! An ordered set of cell ids.

use std::collections::HashSet;

/// Cell ids in the order first added, each once.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CellArray {
    order: Vec<u32>,
    seen: HashSet<u32>,
}

impl CellArray {
    /// An empty array.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds `id` unless it is already present (it keeps its first place).
    /// True when it was added.
    pub fn add(&mut self, id: u32) -> bool {
        let added = self.seen.insert(id);
        if added {
            self.order.push(id);
        }
        added
    }

    /// True when `id` is present.
    #[must_use]
    pub fn contains(&self, id: u32) -> bool {
        self.seen.contains(&id)
    }

    /// Removes `id`; true when it was present.
    pub fn remove(&mut self, id: u32) -> bool {
        if !self.seen.remove(&id) {
            return false;
        }
        self.order.retain(|x| *x != id);
        true
    }

    /// Removes everything (keeping the storage).
    pub fn clear(&mut self) {
        self.order.clear();
        self.seen.clear();
    }

    /// Number of ids.
    #[must_use]
    pub fn len(&self) -> usize {
        self.order.len()
    }

    /// True when there are none.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.order.is_empty()
    }

    /// The ids in the order added.
    #[must_use]
    pub fn ordered_ids(&self) -> &[u32] {
        &self.order
    }

    /// Walks the ids in the order added.
    pub fn iter(&self) -> std::iter::Copied<std::slice::Iter<'_, u32>> {
        self.order.iter().copied()
    }
}

impl<'a> IntoIterator for &'a CellArray {
    type Item = u32;
    type IntoIter = std::iter::Copied<std::slice::Iter<'a, u32>>;

    fn into_iter(self) -> Self::IntoIter {
        self.iter()
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CellArrayTests.cs`
    //! (the allocation tests have no Rust counterpart: walking a slice
    //! allocates nothing).

    use super::*;

    #[test]
    fn keeps_insertion_order() {
        let mut a = CellArray::new();
        a.add(0xA9B4_0170);
        a.add(0xA9B4_0031);
        a.add(0xA9B4_0171);
        assert_eq!(a.ordered_ids(), &[0xA9B4_0170, 0xA9B4_0031, 0xA9B4_0171]);
    }

    #[test]
    fn dedups_keeping_the_first_position() {
        let mut a = CellArray::new();
        assert!(a.add(0xA9B4_0170));
        assert!(a.add(0xA9B4_0171));
        assert!(!a.add(0xA9B4_0170));
        assert_eq!(a.len(), 2);
        assert_eq!(a.ordered_ids(), &[0xA9B4_0170, 0xA9B4_0171]);
    }

    #[test]
    fn membership_and_removal() {
        let mut a = CellArray::new();
        a.add(0xA9B4_0170);
        assert!(a.contains(0xA9B4_0170));
        assert!(!a.contains(0xA9B4_0171));
        assert!(a.remove(0xA9B4_0170));
        assert!(!a.remove(0xA9B4_0170));
        assert!(a.is_empty());
    }

    #[test]
    fn walks_in_order() {
        let mut a = CellArray::new();
        for id in [3, 1, 2, 1] {
            a.add(id);
        }
        assert_eq!(a.iter().collect::<Vec<_>>(), vec![3, 1, 2]);
        let mut walked = Vec::new();
        for id in &a {
            walked.push(id);
        }
        assert_eq!(walked, vec![3, 1, 2]);
        a.clear();
        assert_eq!(a.len(), 0);
    }
}
