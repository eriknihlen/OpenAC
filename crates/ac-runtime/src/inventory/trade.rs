//! The secure-trade window: who the partner is, what each side staged, who
//! accepted. The server drives it; this keeps what it said.

use ac_proto::trade::{AddToTrade, RegisterTrade, RemoveFromTrade, TradeFailure};

/// Which side of the window an item was staged on (the wire's side word).
pub const SIDE_SELF: u32 = 1;
/// The partner's side.
pub const SIDE_PARTNER: u32 = 2;

/// The trade as a host shows it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TradeView {
    /// The window is open.
    pub open: bool,
    /// The other player.
    pub partner_guid: u32,
    /// The player accepted.
    pub self_accepted: bool,
    /// The partner accepted.
    pub partner_accepted: bool,
    /// The player's staged items, in stage order.
    pub self_items: Vec<u32>,
    /// The partner's staged items, in stage order.
    pub partner_items: Vec<u32>,
    /// The last item the server refused.
    pub last_failure_item: u32,
    /// Why (an error code).
    pub last_failure_reason: u32,
    /// Bumped by every change.
    pub revision: u64,
    /// The partner's name, when known (filled in for a host; the state
    /// itself keeps none).
    pub partner_name: Option<String>,
    /// The staged items' names, where known (filled in for a host).
    pub item_names: std::collections::BTreeMap<u32, String>,
}

/// The trade window's state. Staging marks the player's own items as being
/// traded (their item records' trade state), which the caller applies to
/// the item table from the returned guids.
#[derive(Debug, Default)]
pub struct TradeState {
    view: TradeView,
}

/// A trade-state mark to set on an item record: (guid, 1 staged / 0 not).
pub type TradeMark = (u32, i32);

impl TradeState {
    /// No trade open.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The trade as it stands.
    #[must_use]
    pub fn view(&self) -> &TradeView {
        &self.view
    }

    /// The window opened on both clients. The partner is whichever of the
    /// two named players is not this one; staged items and acceptances
    /// from an earlier trade are dropped.
    pub fn apply_register(&mut self, update: &RegisterTrade, self_guid: u32) {
        let v = &mut self.view;
        v.open = true;
        v.partner_guid = if update.initiator != self_guid && update.initiator != 0 {
            update.initiator
        } else {
            update.partner
        };
        v.self_items.clear();
        v.partner_items.clear();
        v.self_accepted = false;
        v.partner_accepted = false;
        v.last_failure_item = 0;
        v.last_failure_reason = 0;
        v.revision += 1;
    }

    /// Either side closed the window: everything goes.
    pub fn apply_close(&mut self) -> Vec<TradeMark> {
        self.clear()
    }

    /// An item was staged: side 1 is the player's own offer coming back,
    /// side 2 the partner's. A staging change withdraws both acceptances.
    pub fn apply_add(&mut self, update: &AddToTrade) -> Vec<TradeMark> {
        let v = &mut self.view;
        if !v.open {
            return Vec::new();
        }
        let partner = update.side == SIDE_PARTNER;
        let items = if partner {
            &mut v.partner_items
        } else {
            &mut v.self_items
        };
        if !items.contains(&update.item_guid) {
            items.push(update.item_guid);
        }
        v.self_accepted = false;
        v.partner_accepted = false;
        v.revision += 1;
        if partner {
            Vec::new()
        } else {
            vec![(update.item_guid, 1)]
        }
    }

    /// An item left the trade.
    pub fn apply_remove(&mut self, update: &RemoveFromTrade) -> Vec<TradeMark> {
        let v = &mut self.view;
        if !v.open {
            return Vec::new();
        }
        let before = v.self_items.len() + v.partner_items.len();
        v.self_items.retain(|&id| id != update.item_guid);
        v.partner_items.retain(|&id| id != update.item_guid);
        if v.self_items.len() + v.partner_items.len() == before {
            return Vec::new();
        }
        v.revision += 1;
        vec![(update.item_guid, 0)]
    }

    /// Someone accepted; the player when it is their guid.
    pub fn apply_accept(&mut self, who: u32, self_guid: u32) {
        let v = &mut self.view;
        if !v.open {
            return;
        }
        if who == self_guid {
            v.self_accepted = true;
        } else {
            v.partner_accepted = true;
        }
        v.revision += 1;
    }

    /// Someone withdrew their acceptance.
    pub fn apply_decline(&mut self, who: u32, self_guid: u32) {
        let v = &mut self.view;
        if !v.open {
            return;
        }
        if who == self_guid {
            v.self_accepted = false;
        } else {
            v.partner_accepted = false;
        }
        v.revision += 1;
    }

    /// Either player cleared the trade: both sides' items and acceptances
    /// go; the window stays open.
    pub fn apply_reset(&mut self) -> Vec<TradeMark> {
        if !self.view.open {
            return Vec::new();
        }
        let marks = self.unmark_self_items();
        let v = &mut self.view;
        v.self_items.clear();
        v.partner_items.clear();
        v.self_accepted = false;
        v.partner_accepted = false;
        v.revision += 1;
        marks
    }

    /// The server refused an item: it leaves the trade and the reason is
    /// kept.
    pub fn apply_failure(&mut self, failure: &TradeFailure) -> Vec<TradeMark> {
        let v = &mut self.view;
        if !v.open {
            return Vec::new();
        }
        v.self_items.retain(|&id| id != failure.item_guid);
        v.partner_items.retain(|&id| id != failure.item_guid);
        v.last_failure_item = failure.item_guid;
        v.last_failure_reason = failure.reason;
        v.revision += 1;
        vec![(failure.item_guid, 0)]
    }

    /// Both acceptances come down.
    pub fn apply_clear_acceptance(&mut self) {
        let v = &mut self.view;
        if !v.open {
            return;
        }
        v.self_accepted = false;
        v.partner_accepted = false;
        v.revision += 1;
    }

    /// Session end or close: the trade is forgotten.
    pub fn clear(&mut self) -> Vec<TradeMark> {
        let v = &self.view;
        let changed = v.open
            || !v.self_items.is_empty()
            || !v.partner_items.is_empty()
            || v.self_accepted
            || v.partner_accepted;
        let marks = self.unmark_self_items();
        let revision = self.view.revision + u64::from(changed);
        self.view = TradeView {
            revision,
            ..TradeView::default()
        };
        marks
    }

    fn unmark_self_items(&self) -> Vec<TradeMark> {
        self.view.self_items.iter().map(|&id| (id, 0)).collect()
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeTradeStateTests.cs`.

    use super::*;

    const SELF: u32 = 0x5000_0001;
    const PARTNER: u32 = 0x5000_0002;
    const ITEM_A: u32 = 0x6000_0001;
    const ITEM_B: u32 = 0x6000_0002;

    fn registered() -> TradeState {
        let mut trade = TradeState::new();
        // The server names the partner twice; the partner is still derived.
        trade.apply_register(
            &RegisterTrade {
                initiator: PARTNER,
                partner: PARTNER,
                stamp: 0,
            },
            SELF,
        );
        trade
    }

    fn add(item: u32, side: u32) -> AddToTrade {
        AddToTrade {
            item_guid: item,
            side,
            slot_index: 0,
        }
    }

    #[test]
    fn register_opens_and_derives_the_partner_despite_the_doubled_guid() {
        let trade = registered();
        let v = trade.view();
        assert!(v.open);
        assert_eq!(v.partner_guid, PARTNER);
        assert!(!v.self_accepted && !v.partner_accepted);
    }

    #[test]
    fn add_stages_per_side_and_drops_both_acceptances() {
        let mut trade = registered();
        trade.apply_accept(SELF, SELF);
        trade.apply_accept(PARTNER, SELF);
        assert_eq!(trade.apply_add(&add(ITEM_A, SIDE_SELF)), [(ITEM_A, 1)]);
        assert!(trade.apply_add(&add(ITEM_B, SIDE_PARTNER)).is_empty());
        // A duplicate echo stages nothing new.
        trade.apply_add(&add(ITEM_A, SIDE_SELF));
        let v = trade.view();
        assert_eq!(v.self_items, [ITEM_A]);
        assert_eq!(v.partner_items, [ITEM_B]);
        assert!(!v.self_accepted && !v.partner_accepted);
    }

    #[test]
    fn accept_and_decline_track_self_versus_partner_by_guid() {
        let mut trade = registered();
        trade.apply_accept(PARTNER, SELF);
        assert!(trade.view().partner_accepted && !trade.view().self_accepted);
        trade.apply_accept(SELF, SELF);
        assert!(trade.view().self_accepted);
        trade.apply_decline(PARTNER, SELF);
        assert!(!trade.view().partner_accepted && trade.view().self_accepted);
    }

    #[test]
    fn reset_clears_both_sides_but_keeps_the_window_open() {
        let mut trade = registered();
        trade.apply_add(&add(ITEM_A, SIDE_SELF));
        trade.apply_add(&add(ITEM_B, SIDE_PARTNER));
        trade.apply_accept(SELF, SELF);
        assert_eq!(trade.apply_reset(), [(ITEM_A, 0)]);
        let v = trade.view();
        assert!(v.open);
        assert!(v.self_items.is_empty() && v.partner_items.is_empty());
        assert!(!v.self_accepted);
    }

    #[test]
    fn failure_removes_the_item_and_records_the_reason() {
        let mut trade = registered();
        trade.apply_add(&add(ITEM_A, SIDE_SELF));
        assert_eq!(
            trade.apply_failure(&TradeFailure {
                item_guid: ITEM_A,
                reason: 0x426,
            }),
            [(ITEM_A, 0)]
        );
        let v = trade.view();
        assert!(v.self_items.is_empty());
        assert_eq!(
            (v.last_failure_item, v.last_failure_reason),
            (ITEM_A, 0x426)
        );
    }

    #[test]
    fn close_and_clear_converge_the_ledger() {
        let mut trade = registered();
        trade.apply_add(&add(ITEM_A, SIDE_SELF));
        assert_eq!(trade.apply_close(), [(ITEM_A, 0)]);
        assert!(!trade.view().open);
        assert!(trade.view().self_items.is_empty());
        let mut trade = registered();
        trade.clear();
        assert!(!trade.view().open);
    }

    #[test]
    fn events_before_register_are_ignored() {
        let mut trade = TradeState::new();
        trade.apply_add(&add(ITEM_A, SIDE_SELF));
        trade.apply_accept(PARTNER, SELF);
        trade.apply_reset();
        let v = trade.view();
        assert!(!v.open);
        assert!(v.self_items.is_empty());
        assert!(!v.partner_accepted);
    }
}
