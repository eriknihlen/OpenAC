//! The mana level of items, as the server answers mana queries.

use std::collections::BTreeMap;

/// One mana query answer, as applied.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ItemManaChange {
    pub item_id: u32,
    /// 0..1 of the item's maximum mana.
    pub fraction: f32,
    /// False when the item has no mana to report; its cached value is dropped.
    pub valid: bool,
}

/// The last known mana fraction of each queried item.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ItemManaState {
    mana_by_item: BTreeMap<u32, f32>,
    revision: u64,
}

impl ItemManaState {
    /// No mana known.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The item's mana fraction, or 0 when unknown.
    #[must_use]
    pub fn mana_fraction(&self, item_id: u32) -> f32 {
        self.mana_by_item.get(&item_id).copied().unwrap_or(0.0)
    }

    /// The item's mana fraction, when known.
    #[must_use]
    pub fn try_mana_fraction(&self, item_id: u32) -> Option<f32> {
        self.mana_by_item.get(&item_id).copied()
    }

    /// The item's mana is known.
    #[must_use]
    pub fn has_mana(&self, item_id: u32) -> bool {
        self.mana_by_item.contains_key(&item_id)
    }

    /// How many items have known mana.
    #[must_use]
    pub fn count(&self) -> usize {
        self.mana_by_item.len()
    }

    /// Bumped by every answer and every clear.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Applies a mana query answer: a valid one caches the fraction, an
    /// invalid one drops the item. Every answer is a change.
    pub fn on_query_item_mana_response(
        &mut self,
        item_id: u32,
        fraction: f32,
        valid: bool,
    ) -> ItemManaChange {
        if valid {
            self.mana_by_item.insert(item_id, fraction);
        } else {
            self.mana_by_item.remove(&item_id);
        }
        self.revision += 1;
        ItemManaChange {
            item_id,
            fraction,
            valid,
        }
    }

    /// Forgets every item (session end).
    pub fn clear(&mut self) {
        self.mana_by_item.clear();
        self.revision += 1;
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ItemManaStateTests.cs`.

    use super::*;

    #[test]
    fn valid_response_caches_and_reports_change() {
        let mut state = ItemManaState::new();
        let change = state.on_query_item_mana_response(0x5000_0A01, 0.75, true);
        assert_eq!(
            change,
            ItemManaChange {
                item_id: 0x5000_0A01,
                fraction: 0.75,
                valid: true
            }
        );
        assert!(state.has_mana(0x5000_0A01));
        assert_eq!(state.mana_fraction(0x5000_0A01), 0.75);
    }

    #[test]
    fn invalid_response_clears_cached_value() {
        let mut state = ItemManaState::new();
        let _ = state.on_query_item_mana_response(0x5000_0A01, 0.75, true);
        let _ = state.on_query_item_mana_response(0x5000_0A01, 0.0, false);
        assert!(!state.has_mana(0x5000_0A01));
        assert_eq!(state.mana_fraction(0x5000_0A01), 0.0);
        assert_eq!(state.revision(), 2);
    }
}
