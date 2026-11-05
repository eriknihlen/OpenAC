//! The vendor window's buy and sell lists: items staged with a quantity
//! before the player confirms the deal.

/// One staged row: a shop or pack item and its quantity.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VendorStagingEntry {
    pub item_guid: u32,
    pub quantity: i32,
}

/// The outcome of staging.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VendorStagingAddOutcome {
    /// Staged: a row was appended, accumulated or replaced.
    Added,
    /// Accumulating would pass [`MAX_STAGED_QUANTITY`]; nothing changed.
    Capped,
    /// Guid 0 or a non-positive quantity; nothing changed.
    Ignored,
}

/// The most one buy row may accumulate to.
pub const MAX_STAGED_QUANTITY: i32 = 0x1388;

/// The vendor's reply to a capped buy row.
pub const TOO_MUCH_MESSAGE: &str =
    "I can't possibly sell you that much! Please be a little more reasonable.";

/// Staged rows in order. Every mutator returns whether it changed the list.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VendorStagingList {
    entries: Vec<VendorStagingEntry>,
}

impl VendorStagingList {
    /// An empty list.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The rows, oldest first.
    #[must_use]
    pub fn entries(&self) -> &[VendorStagingEntry] {
        &self.entries
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    fn position(&self, item_guid: u32) -> Option<usize> {
        self.entries
            .iter()
            .position(|entry| entry.item_guid == item_guid)
    }

    /// Stages a buy row: a new guid is appended (no cap on a new row); a
    /// staged one accumulates unless the total would pass the cap.
    pub fn add(&mut self, item_guid: u32, quantity: i32) -> VendorStagingAddOutcome {
        if item_guid == 0 || quantity <= 0 {
            return VendorStagingAddOutcome::Ignored;
        }
        match self.position(item_guid) {
            Some(index) => {
                let total = self.entries[index].quantity.saturating_add(quantity);
                if total > MAX_STAGED_QUANTITY {
                    return VendorStagingAddOutcome::Capped;
                }
                self.entries[index].quantity = total;
            }
            None => self.entries.push(VendorStagingEntry {
                item_guid,
                quantity,
            }),
        }
        VendorStagingAddOutcome::Added
    }

    /// Stages a sell row. A guid already listed is replaced and moved to
    /// the end rather than summed: the quantity is the caller's current
    /// view of the stack, so a second drop never sells more than is owned.
    pub fn stage(&mut self, item_guid: u32, quantity: i32) -> VendorStagingAddOutcome {
        if item_guid == 0 || quantity <= 0 {
            return VendorStagingAddOutcome::Ignored;
        }
        self.entries.retain(|entry| entry.item_guid != item_guid);
        self.entries.push(VendorStagingEntry {
            item_guid,
            quantity,
        });
        VendorStagingAddOutcome::Added
    }

    /// Drops the newest row whatever its quantity.
    pub fn remove_tail(&mut self) -> bool {
        self.entries.pop().is_some()
    }

    /// Takes `amount` off a row; -1, or at least the row's quantity,
    /// removes the row.
    pub fn remove(&mut self, item_guid: u32, amount: i32) -> bool {
        let Some(index) = self.position(item_guid) else {
            return false;
        };
        let entry = &mut self.entries[index];
        if amount == -1 || amount >= entry.quantity {
            self.entries.remove(index);
        } else {
            entry.quantity = entry.quantity.wrapping_sub(amount);
        }
        true
    }

    /// The row for a guid.
    #[must_use]
    pub fn get(&self, item_guid: u32) -> Option<VendorStagingEntry> {
        self.position(item_guid).map(|index| self.entries[index])
    }

    /// Re-keys a row to another guid in place (a split placeholder), unless
    /// either guid is 0, they are equal, the row is missing or the new
    /// guid is already listed.
    pub fn replace(&mut self, item_guid: u32, replacement_guid: u32) -> bool {
        if item_guid == 0 || replacement_guid == 0 || item_guid == replacement_guid {
            return false;
        }
        let Some(index) = self.position(item_guid) else {
            return false;
        };
        if self.position(replacement_guid).is_some() {
            return false;
        }
        self.entries[index].item_guid = replacement_guid;
        true
    }

    /// Empties the list.
    pub fn clear(&mut self) -> bool {
        if self.entries.is_empty() {
            return false;
        }
        self.entries.clear();
        true
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/VendorStagingListTests.cs`;
    //! "Changed fired" becomes the returned changed flag or outcome.

    use super::*;

    const ITEM_A: u32 = 0x6000_0101;
    const ITEM_B: u32 = 0x6000_0102;

    fn entry(item_guid: u32, quantity: i32) -> VendorStagingEntry {
        VendorStagingEntry {
            item_guid,
            quantity,
        }
    }

    #[test]
    fn remove_tail_drops_the_newest_row_outright() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        list.add(ITEM_B, 7);
        assert!(list.remove_tail());
        assert_eq!(list.entries(), &[entry(ITEM_A, 5)]);
    }

    #[test]
    fn remove_tail_on_an_empty_list_does_nothing() {
        let mut list = VendorStagingList::new();
        assert!(!list.remove_tail());
        assert!(list.is_empty());
    }

    #[test]
    fn add_appends_a_new_entry() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        assert_eq!(list.entries(), &[entry(ITEM_A, 5)]);
        assert!(!list.is_empty());
    }

    #[test]
    fn adding_the_same_guid_twice_accumulates() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        list.add(ITEM_A, 20);
        assert_eq!(list.entries(), &[entry(ITEM_A, 25)]);
    }

    #[test]
    fn add_returns_added_on_a_successful_stage() {
        let mut list = VendorStagingList::new();
        assert_eq!(list.add(ITEM_A, 5), VendorStagingAddOutcome::Added);
        assert_eq!(list.add(ITEM_A, 5), VendorStagingAddOutcome::Added);
    }

    #[test]
    fn add_accumulating_past_the_cap_is_rejected_and_leaves_the_entry() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, MAX_STAGED_QUANTITY - 10);
        assert_eq!(list.add(ITEM_A, 11), VendorStagingAddOutcome::Capped);
        assert_eq!(list.entries(), &[entry(ITEM_A, MAX_STAGED_QUANTITY - 10)]);
    }

    #[test]
    fn add_accumulating_exactly_to_the_cap_succeeds() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, MAX_STAGED_QUANTITY - 10);
        assert_eq!(list.add(ITEM_A, 10), VendorStagingAddOutcome::Added);
        assert_eq!(list.entries(), &[entry(ITEM_A, MAX_STAGED_QUANTITY)]);
    }

    #[test]
    fn add_of_a_brand_new_entry_has_no_cap() {
        let mut list = VendorStagingList::new();
        assert_eq!(
            list.add(ITEM_A, MAX_STAGED_QUANTITY + 500),
            VendorStagingAddOutcome::Added
        );
        assert_eq!(list.entries(), &[entry(ITEM_A, MAX_STAGED_QUANTITY + 500)]);
    }

    #[test]
    fn add_ignores_a_zero_guid_or_non_positive_quantity() {
        for (guid, quantity) in [(0, 5), (ITEM_A, 0), (ITEM_A, -1)] {
            let mut list = VendorStagingList::new();
            assert_eq!(list.add(guid, quantity), VendorStagingAddOutcome::Ignored);
            assert!(list.is_empty());
        }
    }

    #[test]
    fn remove_with_minus_one_removes_the_whole_entry() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 100);
        assert!(list.remove(ITEM_A, -1));
        assert!(list.is_empty());
    }

    #[test]
    fn remove_with_an_amount_at_or_above_the_quantity_removes_the_entry() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        assert!(list.remove(ITEM_A, 5));
        assert!(list.is_empty());
    }

    #[test]
    fn remove_with_a_partial_amount_decrements_in_place() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 10);
        assert!(list.remove(ITEM_A, 3));
        assert_eq!(list.entries(), &[entry(ITEM_A, 7)]);
    }

    #[test]
    fn remove_of_an_unstaged_guid_is_a_no_op() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        assert!(!list.remove(ITEM_B, -1));
        assert_eq!(list.entries().len(), 1);
    }

    #[test]
    fn remove_only_touches_the_matching_entry() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        list.add(ITEM_B, 9);
        list.remove(ITEM_A, -1);
        assert_eq!(list.entries(), &[entry(ITEM_B, 9)]);
    }

    #[test]
    fn get_finds_a_staged_entry_by_guid() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        assert_eq!(list.get(ITEM_A), Some(entry(ITEM_A, 5)));
        assert_eq!(list.get(ITEM_B), None);
    }

    #[test]
    fn replace_preserves_the_split_placeholder_position_and_quantity() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 2);
        list.add(ITEM_B, 9);
        assert!(list.replace(ITEM_A, 0x6000_0003));
        assert_eq!(list.entries(), &[entry(0x6000_0003, 2), entry(ITEM_B, 9)]);
    }

    #[test]
    fn clear_removes_every_entry_and_reports_change_once() {
        let mut list = VendorStagingList::new();
        list.add(ITEM_A, 5);
        list.add(ITEM_B, 9);
        assert!(list.clear());
        assert!(list.is_empty());
        assert!(!list.clear());
    }

    #[test]
    fn staging_an_already_staged_guid_replaces_the_row_and_moves_it_last() {
        let mut list = VendorStagingList::new();
        list.stage(ITEM_A, 1);
        list.stage(ITEM_B, 3);
        assert_eq!(list.stage(ITEM_A, 1), VendorStagingAddOutcome::Added);
        assert_eq!(list.entries(), &[entry(ITEM_B, 3), entry(ITEM_A, 1)]);
    }

    #[test]
    fn staging_replaces_the_quantity_instead_of_summing_it() {
        let mut list = VendorStagingList::new();
        list.stage(ITEM_A, 20);
        list.stage(ITEM_A, 5);
        assert_eq!(list.entries(), &[entry(ITEM_A, 5)]);
    }

    #[test]
    fn stage_ignores_an_empty_guid_or_non_positive_quantity() {
        let mut list = VendorStagingList::new();
        assert_eq!(list.stage(0, 1), VendorStagingAddOutcome::Ignored);
        assert_eq!(list.stage(ITEM_A, 0), VendorStagingAddOutcome::Ignored);
        assert!(list.is_empty());
    }
}
