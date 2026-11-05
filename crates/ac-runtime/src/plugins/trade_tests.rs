//! Trade and vendor against the inventory owner. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeTradeAutomationTests.cs`
//! and `RuntimeVendorAutomationTests.cs`.

use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::trade::{TradeEvent, TradeStatus, VendorStatus};
use ac_proto::{GameEventType, Inbound};

use super::tests::{CLOCKS, PLAYER, call, in_world, with_binding};
use super::trade;
use crate::inventory::{SIDE_PARTNER, SIDE_SELF};
use crate::session::{Outbox, SessionState};
use crate::world::tests::game_event;

const PARTNER: u32 = 0x5000_00BB;
const OTHER: u32 = 0x5000_00CC;
const GEM: u32 = 0x8000_0001;
const ORE: u32 = 0x8000_0002;

fn server(state: &mut SessionState, event: GameEventType, payload: &[u32]) {
    let body = game_event(event, payload);
    let message = Inbound::parse(&body).expect("a game event");
    let mut outbox = Outbox::new();
    state.apply(&message, &mut outbox, true, CLOCKS);
}

fn trade_events(state: &mut SessionState) -> Vec<TradeEvent> {
    trade::follow(state)
        .into_iter()
        .filter_map(|e| match e {
            Event::Trade(t) => Some(t),
            _ => None,
        })
        .collect()
}

// OpenAC `CommandsReportNotOpenWhenNoTradeIsRegistered` and
// `RejectsAddingAnInvalidItem`.
#[test]
fn trade_commands_need_an_open_trade_and_an_item() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        for status in [
            call::<calls::TradeAdd>(b, &GEM).status,
            call::<calls::TradeAccept>(b, &()).status,
            call::<calls::TradeDecline>(b, &()).status,
            call::<calls::TradeReset>(b, &()).status,
            call::<calls::TradeEnd>(b, &()).status,
        ] {
            assert_eq!(status, TradeStatus::NotOpen);
        }
        assert!(!call::<calls::TradeState>(b, &()).is_open);
    });
    server(
        &mut state,
        GameEventType::REGISTER_TRADE,
        &[PLAYER, PARTNER, 0, 0],
    );
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::TradeAdd>(b, &0).status,
            TradeStatus::InvalidItem
        );
        let trade = call::<calls::TradeState>(b, &());
        assert!(trade.is_open);
        assert_eq!(trade.partner_id, PARTNER);
    });
}

// OpenAC `PollRaisesOpenedClosedAndItemAddedFromStateTransitions`,
// `PollRaisesPartnerTradeAcceptedOnlyOnTheRisingEdge` and
// `APartnerSwapWithinOneOpenWindowClosesTheOldTradeAndOpensTheNew`.
#[test]
fn trade_events_follow_the_trade_window() {
    let mut state = in_world();
    assert!(trade_events(&mut state).is_empty());
    server(
        &mut state,
        GameEventType::REGISTER_TRADE,
        &[PLAYER, PARTNER, 0, 0],
    );
    assert_eq!(
        trade_events(&mut state),
        [TradeEvent::Opened((PLAYER, PARTNER))]
    );
    server(
        &mut state,
        GameEventType::ADD_TO_TRADE,
        &[GEM, SIDE_SELF, 0],
    );
    server(
        &mut state,
        GameEventType::ADD_TO_TRADE,
        &[ORE, SIDE_PARTNER, 0],
    );
    assert_eq!(
        trade_events(&mut state),
        [
            TradeEvent::ItemAdded((GEM, true)),
            TradeEvent::ItemAdded((ORE, false))
        ]
    );
    server(&mut state, GameEventType::ACCEPT_TRADE, &[PARTNER]);
    assert_eq!(
        trade_events(&mut state),
        [TradeEvent::PartnerAccepted(PARTNER)]
    );
    server(&mut state, GameEventType::ACCEPT_TRADE, &[PARTNER]);
    assert!(trade_events(&mut state).is_empty());
    server(
        &mut state,
        GameEventType::REGISTER_TRADE,
        &[PLAYER, OTHER, 0, 0],
    );
    assert_eq!(
        trade_events(&mut state),
        [TradeEvent::Closed, TradeEvent::Opened((PLAYER, OTHER))]
    );
    server(&mut state, GameEventType::CLOSE_TRADE, &[0]);
    assert_eq!(trade_events(&mut state), [TradeEvent::Closed]);
}

// OpenAC `ReportsClosedUntilAVendorOpens`,
// `WithNoVendorOpenTheValueLimitsReadNoLimitRatherThanZero`,
// `BuyAllReportsInvalidItemWhenTheBuyListIsEmpty`.
#[test]
fn with_no_vendor_open_the_lists_are_refused_and_limits_read_none() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let vendor = call::<calls::VendorState>(b, &());
        assert!(!vendor.is_open);
        assert_eq!(vendor.profile.minimum_value, u32::MAX);
        assert_eq!(vendor.profile.maximum_value, u32::MAX);
        assert_eq!(
            call::<calls::VendorAddBuy>(b, &(GEM, 1)).status,
            VendorStatus::NotOpen
        );
        assert_eq!(
            call::<calls::VendorAddSell>(b, &GEM).status,
            VendorStatus::NotOpen
        );
        assert_eq!(
            call::<calls::VendorBuyAll>(b, &()).status,
            VendorStatus::NotOpen
        );
        assert_eq!(
            call::<calls::VendorClearBuy>(b, &()).status,
            VendorStatus::Sent
        );
    });
}
