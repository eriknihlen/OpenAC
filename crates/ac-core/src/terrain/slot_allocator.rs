//! Slots in the renderer's shared terrain buffer, one landblock each.
//!
//! Freed slots are reused first, oldest free first; otherwise slots are
//! handed out in increasing order. The allocator only keeps the books; the
//! renderer grows the GPU buffer when told a slot lies past the capacity.

use std::collections::{HashSet, VecDeque};
use std::fmt;

/// Errors from [`TerrainSlotAllocator`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SlotError {
    /// The initial capacity was zero.
    ZeroCapacity,
    /// The slot is not currently allocated (double free or unknown slot).
    NotAllocated(u32),
    /// Capacity can only grow.
    Shrink {
        /// The capacity now.
        current: u32,
        /// The smaller capacity asked for.
        requested: u32,
    },
}

impl fmt::Display for SlotError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SlotError::ZeroCapacity => f.write_str("terrain slot capacity must be > 0"),
            SlotError::NotAllocated(slot) => {
                write!(
                    f,
                    "slot {slot} was not allocated (double free or unknown slot)"
                )
            }
            SlotError::Shrink { current, requested } => {
                write!(f, "capacity can only grow ({current} -> {requested})")
            }
        }
    }
}

impl std::error::Error for SlotError {}

/// The slot bookkeeping.
#[derive(Debug, Clone)]
pub struct TerrainSlotAllocator {
    free_slots: VecDeque<u32>,
    live_slots: HashSet<u32>,
    next_free_slot: u32,
    capacity: u32,
}

/// A slot handed out by [`TerrainSlotAllocator::allocate`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Allocation {
    /// The slot index.
    pub slot: u32,
    /// True if the slot lies at or past the capacity: grow the buffer (and
    /// call [`TerrainSlotAllocator::grow_to`]) before using it.
    pub needs_grow: bool,
}

impl TerrainSlotAllocator {
    /// An allocator for a buffer of `initial_capacity` slots.
    ///
    /// # Errors
    ///
    /// [`SlotError::ZeroCapacity`] for a zero capacity.
    pub fn new(initial_capacity: u32) -> Result<Self, SlotError> {
        if initial_capacity == 0 {
            return Err(SlotError::ZeroCapacity);
        }
        Ok(Self {
            free_slots: VecDeque::new(),
            live_slots: HashSet::new(),
            next_free_slot: 0,
            capacity: initial_capacity,
        })
    }

    /// Slots the buffer holds now.
    #[must_use]
    pub fn capacity(&self) -> u32 {
        self.capacity
    }

    /// Slots in use.
    #[must_use]
    pub fn loaded_count(&self) -> usize {
        self.live_slots.len()
    }

    /// Hands out a slot: the oldest freed one, else the next new index.
    pub fn allocate(&mut self) -> Allocation {
        let slot = self.free_slots.pop_front().unwrap_or_else(|| {
            let slot = self.next_free_slot;
            self.next_free_slot += 1;
            slot
        });
        self.live_slots.insert(slot);
        Allocation {
            slot,
            needs_grow: slot >= self.capacity,
        }
    }

    /// Returns a slot for reuse.
    ///
    /// # Errors
    ///
    /// [`SlotError::NotAllocated`] if the slot is not in use.
    pub fn free(&mut self, slot: u32) -> Result<(), SlotError> {
        if !self.live_slots.remove(&slot) {
            return Err(SlotError::NotAllocated(slot));
        }
        self.free_slots.push_back(slot);
        Ok(())
    }

    /// Records that the buffer now holds `new_capacity` slots.
    ///
    /// # Errors
    ///
    /// [`SlotError::Shrink`] if `new_capacity` is below the current one.
    pub fn grow_to(&mut self, new_capacity: u32) -> Result<(), SlotError> {
        if new_capacity < self.capacity {
            return Err(SlotError::Shrink {
                current: self.capacity,
                requested: new_capacity,
            });
        }
        self.capacity = new_capacity;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden behaviour: OpenAC tests/AcDream.Core.Tests/Terrain/TerrainSlotAllocatorTests.cs.

    fn alloc(capacity: u32) -> TerrainSlotAllocator {
        TerrainSlotAllocator::new(capacity).unwrap()
    }

    #[test]
    fn fresh_allocations_count_up_from_zero() {
        let mut a = alloc(8);
        assert_eq!(a.allocate().slot, 0);
        assert_eq!(a.allocate().slot, 1);
    }

    #[test]
    fn freed_slots_are_reused_oldest_first() {
        let mut a = alloc(8);
        let s0 = a.allocate().slot;
        let _s1 = a.allocate().slot;
        let s2 = a.allocate().slot;
        a.free(s0).unwrap();
        a.free(s2).unwrap();
        assert_eq!(a.allocate().slot, s0);
        assert_eq!(a.allocate().slot, s2);
    }

    #[test]
    fn allocation_past_capacity_signals_grow() {
        let mut a = alloc(2);
        let grows = [a.allocate(), a.allocate(), a.allocate()].map(|x| x.needs_grow);
        assert_eq!(grows, [false, false, true]);
    }

    #[test]
    fn grow_to_raises_capacity_and_refuses_to_shrink() {
        let mut a = alloc(4);
        a.grow_to(8).unwrap();
        assert_eq!(a.capacity(), 8);
        a.grow_to(64).unwrap();
        assert_eq!(a.capacity(), 64);
        assert!(a.grow_to(32).is_err());
    }

    #[test]
    fn loaded_count_tracks_allocate_and_free() {
        let mut a = alloc(8);
        assert_eq!(a.loaded_count(), 0);
        let s0 = a.allocate().slot;
        let _ = a.allocate();
        assert_eq!(a.loaded_count(), 2);
        a.free(s0).unwrap();
        assert_eq!(a.loaded_count(), 1);
    }

    #[test]
    fn double_free_is_an_error() {
        let mut a = alloc(8);
        let s0 = a.allocate().slot;
        a.free(s0).unwrap();
        assert_eq!(a.free(s0), Err(SlotError::NotAllocated(s0)));
    }

    #[test]
    fn zero_capacity_is_an_error() {
        assert_eq!(
            TerrainSlotAllocator::new(0).unwrap_err(),
            SlotError::ZeroCapacity
        );
    }
}
