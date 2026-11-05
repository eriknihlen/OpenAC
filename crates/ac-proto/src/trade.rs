//! Secure trade between two players: the requests and the events.
//!
//! The clear-acceptance event ([`crate::GameEventType::CLEAR_TRADE_ACCEPTANCE`])
//! carries nothing to read. Game-action builders take the session's next
//! action sequence number and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, bare_action, u32_action};
use crate::{GameActionType, Result};

/// A trade window opened ([`crate::GameEventType::REGISTER_TRADE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegisterTrade {
    /// Who opened it.
    pub initiator: u32,
    /// The other player.
    pub partner: u32,
    /// Trade stamp.
    pub stamp: u64,
}

impl RegisterTrade {
    /// Parse the event payload: initiator, partner, stamp.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            initiator: r.u32()?,
            partner: r.u32()?,
            stamp: r.u64()?,
        })
    }
}

/// An item entered the trade ([`crate::GameEventType::ADD_TO_TRADE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AddToTrade {
    /// The item.
    pub item_guid: u32,
    /// Whose side (1 self, 2 partner).
    pub side: u32,
    /// Slot index.
    pub slot_index: u32,
}

impl AddToTrade {
    /// Parse the event payload: item, side, slot.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            side: r.u32()?,
            slot_index: r.u32()?,
        })
    }
}

/// An item left the trade ([`crate::GameEventType::REMOVE_FROM_TRADE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RemoveFromTrade {
    /// The item.
    pub item_guid: u32,
    /// Removal mode.
    pub mode: u32,
}

impl RemoveFromTrade {
    /// Parse the event payload: item, mode.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            mode: r.u32()?,
        })
    }
}

/// An item could not be traded ([`crate::GameEventType::TRADE_FAILURE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TradeFailure {
    /// The item.
    pub item_guid: u32,
    /// Why (an error code).
    pub reason: u32,
}

impl TradeFailure {
    /// Parse the event payload: item, reason.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            reason: r.u32()?,
        })
    }
}

/// The single word of a close ([`crate::GameEventType::CLOSE_TRADE`], the
/// reason), accept ([`crate::GameEventType::ACCEPT_TRADE`]), decline
/// ([`crate::GameEventType::DECLINE_TRADE`]) or reset
/// ([`crate::GameEventType::RESET_TRADE`]) event (who did it).
pub fn parse_trade_word(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// Open a trade with a player.
#[must_use]
pub fn build_open_trade_negotiations(sequence: u32, partner_guid: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::OPEN_TRADE_NEGOTIATIONS,
        partner_guid,
    )
}

/// Close the trade window.
#[must_use]
pub fn build_close_trade_negotiations(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLOSE_TRADE_NEGOTIATIONS)
}

/// Put an item into the trade: item, then slot.
#[must_use]
pub fn build_add_to_trade(sequence: u32, item_guid: u32, trade_slot: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::ADD_TO_TRADE);
    w.u32(item_guid);
    w.u32(trade_slot);
    w.into_vec()
}

/// The trade state the client accepts.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TradeAcceptance {
    /// The partner.
    pub partner_guid: u32,
    /// Trade stamp.
    pub trade_stamp: f64,
    /// Trade status.
    pub trade_status: u32,
    /// Who opened the trade.
    pub initiator_guid: u32,
    /// The initiator accepts.
    pub initiator_accepts: bool,
    /// The partner accepts.
    pub partner_accepts: bool,
}

/// Accept the trade. The body is a trade record: partner, stamp, status,
/// initiator, the two acceptance words, then two empty item lists.
#[must_use]
pub fn build_accept_trade(sequence: u32, trade: &TradeAcceptance) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::ACCEPT_TRADE);
    w.u32(trade.partner_guid);
    w.f64(trade.trade_stamp);
    w.u32(trade.trade_status);
    w.u32(trade.initiator_guid);
    w.u32(u32::from(trade.initiator_accepts));
    w.u32(u32::from(trade.partner_accepts));
    w.u32(0);
    w.u32(0);
    w.into_vec()
}

/// Decline the trade.
#[must_use]
pub fn build_decline_trade(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::DECLINE_TRADE)
}

/// Clear the trade.
#[must_use]
pub fn build_reset_trade(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::RESET_TRADE)
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/TradeRequestsTests.cs`.

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn requests() {
        assert_eq!(
            build_open_trade_negotiations(7, 0x5000_1234),
            words(&[0xF7B1, 7, 0x01F6, 0x5000_1234])
        );
        assert_eq!(
            build_close_trade_negotiations(3),
            words(&[0xF7B1, 3, 0x01F7])
        );
        assert_eq!(build_decline_trade(3), words(&[0xF7B1, 3, 0x01FB]));
        assert_eq!(build_reset_trade(3), words(&[0xF7B1, 3, 0x0204]));
        assert_eq!(
            build_add_to_trade(5, 0x6000_0001, 2),
            words(&[0xF7B1, 5, 0x01F8, 0x6000_0001, 2])
        );
    }

    #[test]
    fn accept_writes_the_trade_record_and_two_empty_lists() {
        let body = build_accept_trade(
            11,
            &TradeAcceptance {
                partner_guid: 0x5000_0B0B,
                trade_stamp: 0.0,
                trade_status: 0,
                initiator_guid: 0x5000_A0A0,
                initiator_accepts: true,
                partner_accepts: false,
            },
        );
        assert_eq!(body.len(), 48);
        assert_eq!(body[..16], words(&[0xF7B1, 11, 0x01FA, 0x5000_0B0B]));
        assert_eq!(body[16..24], 0f64.to_le_bytes());
        assert_eq!(body[24..], words(&[0, 0x5000_A0A0, 1, 0, 0, 0]));
    }

    #[test]
    fn events() {
        let register = words(&[0x5000_0001, 0x5000_0002, 0, 0]);
        assert_eq!(
            RegisterTrade::parse(&register),
            Ok(RegisterTrade {
                initiator: 0x5000_0001,
                partner: 0x5000_0002,
                stamp: 0
            })
        );
        assert_every_prefix_fails(&register, RegisterTrade::parse);
        let add = words(&[0x6000_0009, 2, 0]);
        assert_eq!(
            AddToTrade::parse(&add),
            Ok(AddToTrade {
                item_guid: 0x6000_0009,
                side: 2,
                slot_index: 0
            })
        );
        assert_every_prefix_fails(&add, AddToTrade::parse);
        let failure = words(&[0x6000_0042, 0x426]);
        assert_eq!(
            TradeFailure::parse(&failure),
            Ok(TradeFailure {
                item_guid: 0x6000_0042,
                reason: 0x426
            })
        );
        assert_every_prefix_fails(&failure, TradeFailure::parse);
        assert_every_prefix_fails(&words(&[1, 2]), RemoveFromTrade::parse);
        assert_eq!(parse_trade_word(&words(&[0x5000_0C0C])), Ok(0x5000_0C0C));
        assert!(parse_trade_word(&[0; 3]).is_err());
    }
}
