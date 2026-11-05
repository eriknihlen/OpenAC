//! The toolbar's shortcut slots, and what dropping something on a slot
//! changes.

/// Shortcut slots on the toolbar (two rows of nine).
pub const SLOT_COUNT: usize = 18;

/// One shortcut record as the server stores it: its slot, the object it
/// points at (0 for none) and a spell id.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ShortcutEntry {
    pub index: i32,
    pub object_id: u32,
    pub spell_id: u32,
}

impl ShortcutEntry {
    /// The same record at another slot.
    #[must_use]
    pub fn with_index(self, index: i32) -> Self {
        Self { index, ..self }
    }
}

fn slot_index(slot: i32) -> Option<usize> {
    usize::try_from(slot).ok().filter(|&slot| slot < SLOT_COUNT)
}

/// The eighteen slots. Every change bumps the revision.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ShortcutStore {
    entries: [Option<ShortcutEntry>; SLOT_COUNT],
    revision: u64,
}

impl ShortcutStore {
    /// All slots empty.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Bumped by every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Replaces every slot with the given records; a record whose slot is
    /// out of range is dropped.
    pub fn load(&mut self, entries: impl IntoIterator<Item = ShortcutEntry>) {
        self.entries = [None; SLOT_COUNT];
        for entry in entries {
            self.set_core(entry);
        }
        self.revision += 1;
    }

    /// The record at `slot`; none for an empty or out-of-range slot.
    #[must_use]
    pub fn get_entry(&self, slot: i32) -> Option<ShortcutEntry> {
        slot_index(slot).and_then(|slot| self.entries[slot])
    }

    /// Every slot.
    #[must_use]
    pub fn snapshot(&self) -> [Option<ShortcutEntry>; SLOT_COUNT] {
        self.entries
    }

    /// The present records, in slot order.
    #[must_use]
    pub fn items(&self) -> Vec<ShortcutEntry> {
        self.entries.iter().flatten().copied().collect()
    }

    /// How many slots hold a record.
    #[must_use]
    pub fn count(&self) -> usize {
        self.entries.iter().flatten().count()
    }

    /// The object at `slot`, or 0.
    #[must_use]
    pub fn get(&self, slot: i32) -> u32 {
        self.get_entry(slot).map_or(0, |entry| entry.object_id)
    }

    /// No object at `slot`.
    #[must_use]
    pub fn is_empty(&self, slot: i32) -> bool {
        self.get(slot) == 0
    }

    /// Stores a record at its own slot. Returns whether anything changed.
    pub fn set(&mut self, entry: ShortcutEntry) -> bool {
        let changed = self.set_core(entry);
        if changed {
            self.revision += 1;
        }
        changed
    }

    /// Points `slot` at an object (no spell).
    pub fn set_item(&mut self, slot: i32, object_id: u32) -> bool {
        self.set(ShortcutEntry {
            index: slot,
            object_id,
            spell_id: 0,
        })
    }

    /// Empties `slot`. Returns whether it held a record.
    pub fn remove(&mut self, slot: i32) -> bool {
        let Some(slot) = slot_index(slot) else {
            return false;
        };
        if self.entries[slot].take().is_none() {
            return false;
        }
        self.revision += 1;
        true
    }

    /// Empties every slot (always counts as a change).
    pub fn clear(&mut self) {
        self.entries = [None; SLOT_COUNT];
        self.revision += 1;
    }

    fn set_core(&mut self, entry: ShortcutEntry) -> bool {
        let Some(slot) = slot_index(entry.index) else {
            return false;
        };
        if self.entries[slot] == Some(entry) {
            return false;
        }
        self.entries[slot] = Some(entry);
        true
    }
}

/// What was dropped on the toolbar.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShortcutDropSource {
    /// An item dragged from somewhere else.
    FreshItem,
    /// A shortcut dragged from another slot (already lifted off it).
    ShortcutAlias,
}

/// One change to send, in order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShortcutMutation {
    /// Empty the slot.
    Remove(i32),
    /// Store the record at its slot.
    Add(ShortcutEntry),
}

type Slots = [Option<ShortcutEntry>; SLOT_COUNT];

fn visible(entry: Option<ShortcutEntry>) -> Option<ShortcutEntry> {
    entry.filter(|entry| entry.object_id != 0)
}

fn copy_slots(slots: &[Option<ShortcutEntry>]) -> Option<Slots> {
    <Slots>::try_from(slots).ok()
}

fn remove_visible_at(state: &mut Slots, slot: usize, mutations: &mut Vec<ShortcutMutation>) {
    if visible(state[slot]).is_some() {
        state[slot] = None;
        mutations.push(ShortcutMutation::Remove(slot as i32));
    }
}

fn find_first_visible_object(state: &Slots, object_id: u32) -> Option<usize> {
    state
        .iter()
        .position(|&entry| visible(entry).is_some_and(|entry| entry.object_id == object_id))
}

fn remove_first_visible_object(
    state: &mut Slots,
    object_id: u32,
    mutations: &mut Vec<ShortcutMutation>,
) {
    if let Some(slot) = find_first_visible_object(state, object_id) {
        state[slot] = None;
        mutations.push(ShortcutMutation::Remove(slot as i32));
    }
}

fn add(state: &mut Slots, entry: ShortcutEntry, mutations: &mut Vec<ShortcutMutation>) {
    if let Some(slot) = slot_index(entry.index) {
        state[slot] = Some(entry);
        mutations.push(ShortcutMutation::Add(entry));
    }
}

/// The first visually empty slot to the right of `target`, wrapping round
/// to the slots from 0 up to the target.
fn first_empty_to_right(state: &Slots, target: usize) -> Option<usize> {
    (target + 1..SLOT_COUNT)
        .chain(0..=target)
        .find(|&slot| visible(state[slot]).is_none())
}

/// The changes a drop of `dragged` on `target_slot` makes. A fresh item
/// replaces the target (dropping any other shortcut to the same object)
/// and the displaced shortcut moves to the first empty slot to the right;
/// a shortcut moved from `source_slot` swaps with the displaced one. A
/// record with no object counts as an empty slot. Nothing for a slot list
/// of the wrong size, an out-of-range target or a drag with no object.
#[must_use]
pub fn plan_shortcut_drop(
    slots: &[Option<ShortcutEntry>],
    source: ShortcutDropSource,
    source_slot: i32,
    target_slot: i32,
    dragged: ShortcutEntry,
) -> Vec<ShortcutMutation> {
    let (Some(mut state), Some(target)) = (copy_slots(slots), slot_index(target_slot)) else {
        return Vec::new();
    };
    if dragged.object_id == 0 {
        return Vec::new();
    }

    let mut mutations = Vec::with_capacity(4);
    let displaced = visible(state[target]);
    remove_visible_at(&mut state, target, &mut mutations);

    match source {
        ShortcutDropSource::FreshItem => {
            remove_first_visible_object(&mut state, dragged.object_id, &mut mutations);
            add(&mut state, dragged.with_index(target_slot), &mut mutations);
            if let Some(entry) = displaced
                && entry.object_id != dragged.object_id
                && let Some(empty) = first_empty_to_right(&state, target)
            {
                add(&mut state, entry.with_index(empty as i32), &mut mutations);
            }
        }
        ShortcutDropSource::ShortcutAlias => {
            add(&mut state, dragged.with_index(target_slot), &mut mutations);
            if let Some(entry) = displaced
                && entry.object_id != dragged.object_id
                && let Some(source) = slot_index(source_slot)
                && visible(state[source]).is_none()
            {
                add(&mut state, entry.with_index(source_slot), &mut mutations);
            }
        }
    }
    mutations
}

/// The changes a drop of `dropped_object_id` on `target_slot` makes when
/// that object may not have a shortcut: nothing is added, but the target's
/// shortcut still makes way, to the first empty slot to the right of it
/// (wrapping round, the target itself last), unless it was the dropped
/// object's own. Nothing for a slot list of the wrong size or an
/// out-of-range target.
#[must_use]
pub fn plan_refused_shortcut_drop(
    slots: &[Option<ShortcutEntry>],
    target_slot: i32,
    dropped_object_id: u32,
) -> Vec<ShortcutMutation> {
    let (Some(mut state), Some(target)) = (copy_slots(slots), slot_index(target_slot)) else {
        return Vec::new();
    };
    let mut mutations = Vec::with_capacity(2);
    let displaced = visible(state[target]);
    remove_visible_at(&mut state, target, &mut mutations);
    if let Some(entry) = displaced
        && entry.object_id != dropped_object_id
        && let Some(empty) = first_empty_to_right(&state, target)
    {
        add(&mut state, entry.with_index(empty as i32), &mut mutations);
    }
    mutations
}

/// The changes when a whole stack merges into another: the old object's
/// shortcut is re-pointed at the new object in its own slot, and any
/// shortcut the new object already had is dropped.
#[must_use]
pub fn plan_full_stack_merge(
    slots: &[Option<ShortcutEntry>],
    old_object_id: u32,
    new_object_id: u32,
) -> Vec<ShortcutMutation> {
    let Some(mut state) = copy_slots(slots) else {
        return Vec::new();
    };
    if old_object_id == 0 || new_object_id == 0 {
        return Vec::new();
    }
    let Some(source_slot) = find_first_visible_object(&state, old_object_id) else {
        return Vec::new();
    };
    let Some(source_entry) = state[source_slot] else {
        return Vec::new();
    };
    let mut mutations = Vec::with_capacity(3);
    remove_visible_at(&mut state, source_slot, &mut mutations);
    remove_first_visible_object(&mut state, new_object_id, &mut mutations);
    add(
        &mut state,
        ShortcutEntry {
            object_id: new_object_id,
            ..source_entry
        },
        &mut mutations,
    );
    mutations
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ShortcutStoreTests.cs`
    //! and `ShortcutDropPlannerTests.cs`. The observer and disposal half of
    //! `RevisionObserversAndDisposalTrackTheOneCanonicalSlotMap` has no
    //! counterpart; its revision half is kept.

    use super::*;

    fn entry(index: i32, object_id: u32, spell_id: u32) -> ShortcutEntry {
        ShortcutEntry {
            index,
            object_id,
            spell_id,
        }
    }

    #[test]
    fn load_preserves_raw_fields_and_rejects_out_of_range_index() {
        let mut store = ShortcutStore::new();
        store.load([
            entry(0, 0x5001, 0x1122_3344),
            entry(3, 0x5002, 0),
            entry(5, 0, 0xA5C3_1234),
            entry(-1, 0x5003, 1),
            entry(18, 0x5004, 2),
        ]);
        assert_eq!(store.get(0), 0x5001);
        assert_eq!(store.get(3), 0x5002);
        assert_eq!(store.get(5), 0);
        assert_eq!(store.get_entry(5), Some(entry(5, 0, 0xA5C3_1234)));
        assert_eq!(store.get_entry(0).map(|e| e.spell_id), Some(0x1122_3344));
        assert!(store.is_empty(1));
        assert_eq!(store.get_entry(-1), None);
        assert_eq!(store.get_entry(18), None);
    }

    #[test]
    fn set_remove_get_round_trip_and_bounds_safe() {
        let mut store = ShortcutStore::new();
        store.set_item(4, 0xABCD);
        assert_eq!(store.get(4), 0xABCD);
        assert!(!store.is_empty(4));
        store.remove(4);
        assert!(store.is_empty(4));
        assert_eq!(store.get(-1), 0);
        assert_eq!(store.get(18), 0);
        assert!(!store.set_item(18, 1));
        assert!(!store.remove(-1));
    }

    #[test]
    fn revision_tracks_the_one_canonical_slot_map() {
        let mut store = ShortcutStore::new();
        store.load([entry(2, 0x5001, 0)]);
        assert_eq!(store.revision(), 1);
        assert_eq!(store.count(), 1);
        assert!(!store.set(entry(2, 0x5001, 0)));
        assert_eq!(store.revision(), 1);
        assert!(store.set(entry(2, 0x5002, 0)));
        assert_eq!(store.revision(), 2);
        assert_eq!(store.items(), vec![entry(2, 0x5002, 0)]);
    }

    fn empty() -> Slots {
        [None; SLOT_COUNT]
    }

    fn full() -> Slots {
        let mut slots = empty();
        for (i, slot) in slots.iter_mut().enumerate() {
            *slot = Some(entry(i as i32, 0xC000 + i as u32, 0));
        }
        slots
    }

    use ShortcutMutation::{Add, Remove};

    #[test]
    fn fresh_item_to_empty_adds_only_at_target() {
        let plan = plan_shortcut_drop(
            &empty(),
            ShortcutDropSource::FreshItem,
            11,
            4,
            entry(11, 0xA001, 0),
        );
        assert_eq!(plan, vec![Add(entry(4, 0xA001, 0))]);
    }

    #[test]
    fn a_refused_drop_adds_nothing_but_the_target_still_makes_way_to_the_right() {
        // GitHub #45: the target's shortcut makes way before the dropped
        // object is judged, so a refused object leaves the target empty.
        assert_eq!(plan_refused_shortcut_drop(&empty(), 4, 0xA001), vec![]);
        let mut slots = empty();
        slots[5] = Some(entry(5, 0xB001, 0x1122_3344));
        assert_eq!(
            plan_refused_shortcut_drop(&slots, 5, 0xA001),
            vec![Remove(5), Add(entry(6, 0xB001, 0x1122_3344))]
        );
        // Its own shortcut is only taken off.
        assert_eq!(
            plan_refused_shortcut_drop(&slots, 5, 0xB001),
            vec![Remove(5)]
        );
        // With every other slot full it wraps round to the target itself.
        let mut slots = full();
        slots[5] = Some(entry(5, 0xB001, 0));
        assert_eq!(
            plan_refused_shortcut_drop(&slots, 5, 0xA001),
            vec![Remove(5), Add(entry(5, 0xB001, 0))]
        );
        assert_eq!(plan_refused_shortcut_drop(&slots, 18, 0xA001), vec![]);
    }

    #[test]
    fn fresh_item_to_occupied_moves_displaced_to_first_empty_on_right() {
        let mut slots = empty();
        slots[5] = Some(entry(5, 0xB001, 0x1122_3344));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::FreshItem,
            0,
            5,
            entry(0, 0xA001, 0),
        );
        assert_eq!(
            plan,
            vec![
                Remove(5),
                Add(entry(5, 0xA001, 0)),
                Add(entry(6, 0xB001, 0x1122_3344))
            ]
        );
    }

    #[test]
    fn fresh_item_to_occupied_cyclic_search_wraps_to_slot_zero_side() {
        let mut slots = full();
        slots[2] = None;
        slots[16] = Some(entry(16, 0xB001, 0));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::FreshItem,
            0,
            16,
            entry(0, 0xA001, 0),
        );
        assert_eq!(plan.last(), Some(&Add(entry(2, 0xB001, 0))));
    }

    #[test]
    fn fresh_item_to_full_bar_drops_displaced_when_no_empty_slot_exists() {
        let mut slots = full();
        slots[5] = Some(entry(5, 0xB001, 0));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::FreshItem,
            0,
            5,
            entry(0, 0xA001, 0),
        );
        assert_eq!(plan, vec![Remove(5), Add(entry(5, 0xA001, 0))]);
    }

    #[test]
    fn shortcut_alias_to_occupied_restores_displaced_to_vacated_source() {
        let mut slots = empty();
        slots[5] = Some(entry(5, 0xB001, 0x5566_7788));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::ShortcutAlias,
            3,
            5,
            entry(3, 0xA001, 0x1122_3344),
        );
        assert_eq!(
            plan,
            vec![
                Remove(5),
                Add(entry(5, 0xA001, 0x1122_3344)),
                Add(entry(3, 0xB001, 0x5566_7788))
            ]
        );
    }

    #[test]
    fn fresh_duplicate_removes_old_occurrence_before_searching_for_displaced_slot() {
        let mut slots = full();
        slots[5] = Some(entry(5, 0xB001, 0));
        slots[7] = Some(entry(7, 0xA001, 0xA5C3_1234));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::FreshItem,
            0,
            5,
            entry(0, 0xA001, 0),
        );
        assert_eq!(
            plan,
            vec![
                Remove(5),
                Remove(7),
                Add(entry(5, 0xA001, 0)),
                Add(entry(7, 0xB001, 0))
            ]
        );
    }

    #[test]
    fn non_object_record_is_visually_empty_for_cyclic_search() {
        let mut slots = full();
        slots[6] = Some(entry(6, 0, 0x0102_0304));
        slots[5] = Some(entry(5, 0xB001, 0));
        let plan = plan_shortcut_drop(
            &slots,
            ShortcutDropSource::FreshItem,
            0,
            5,
            entry(0, 0xA001, 0),
        );
        assert_eq!(plan.last(), Some(&Add(entry(6, 0xB001, 0))));
    }

    #[test]
    fn full_stack_merge_removes_existing_target_shortcut_then_rekeys_source_slot() {
        let mut slots = empty();
        slots[2] = Some(entry(2, 0xA001, 0x1122_3344));
        slots[9] = Some(entry(9, 0xB001, 0x5566_7788));
        let plan = plan_full_stack_merge(&slots, 0xA001, 0xB001);
        assert_eq!(
            plan,
            vec![Remove(2), Remove(9), Add(entry(2, 0xB001, 0x1122_3344))]
        );
    }

    #[test]
    fn invalid_drop_returns_no_mutation() {
        assert!(
            plan_shortcut_drop(
                &empty(),
                ShortcutDropSource::FreshItem,
                0,
                -1,
                entry(0, 0xA001, 0)
            )
            .is_empty()
        );
        assert!(
            plan_shortcut_drop(
                &empty(),
                ShortcutDropSource::FreshItem,
                0,
                0,
                entry(0, 0, 0)
            )
            .is_empty()
        );
        assert!(
            plan_shortcut_drop(
                &empty()[..17],
                ShortcutDropSource::FreshItem,
                0,
                0,
                entry(0, 0xA001, 0)
            )
            .is_empty()
        );
    }
}
