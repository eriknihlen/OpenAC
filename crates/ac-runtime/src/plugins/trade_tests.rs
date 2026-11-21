//! Trade and vendor against the inventory owner. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeTradeAutomationTests.cs`
//! and `RuntimeVendorAutomationTests.cs`.

use ac_core::items::{ItemType, ItemUseable, PublicWeenieFlags, WeenieData};
use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::trade::{TradeEvent, TradeStatus, VendorEvent, VendorStatus};
use ac_proto::{GameEventType, Inbound};

use super::events_tests::described;
use super::tests::{CLOCKS, PLAYER, call, in_world, with_binding};
use super::trade;
use crate::inventory::{SIDE_PARTNER, SIDE_SELF};
use crate::session::{Outbox, SessionState};
use crate::world::tests::game_event;

const PARTNER: u32 = 0x5000_00BB;
const VENDOR: u32 = 0x7000_0003;
const OTHER_VENDOR: u32 = 0x7000_0004;
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

fn vendor_events(state: &mut SessionState) -> Vec<VendorEvent> {
    trade::follow(state)
        .into_iter()
        .filter_map(|e| match e {
            Event::Vendor(v) => Some(v),
            _ => None,
        })
        .collect()
}

/// The server's shop listing for `vendor`, with no items.
fn approach_vendor(state: &mut SessionState, vendor: u32) {
    // Vendor, item types, value range, magic, rates, alternate currency,
    // its empty plural name, no items.
    server(
        state,
        GameEventType::APPROACH_VENDOR,
        &[vendor, 0, 0, 1000, 0, 0, 0, 0, 0, 0, 0],
    );
}

/// The vendor is used (as a plugin's use, which goes out at once), and the
/// server finishes the use.
fn use_vendor(state: &mut SessionState, outbox: &mut Outbox) {
    let sent = outbox.len();
    with_binding(state, outbox, |b| {
        call::<calls::ItemsUse>(b, &VENDOR);
    });
    assert_eq!(outbox.len(), sent + 1, "the use went out");
    server(state, GameEventType::USE_DONE, &[0]);
}

/// A vendor open with the gem on the plugin's sell list.
fn vendor_with_a_sell_list(outbox: &mut Outbox) -> SessionState {
    let mut state = in_world();
    // The vendor stands in the world: not to be picked up, and useable
    // from a distance, as a vendor is.
    let mut vendor = described(
        VENDOR,
        1,
        "Vendor",
        ItemType::CREATURE.bits(),
        PublicWeenieFlags::STUCK.bits(),
        Some(0x0101_0001),
    );
    if let Inbound::CreateObject(create) = &mut vendor
        && let Some(weenie) = &mut create.weenie
    {
        weenie.useability = Some(ItemUseable::REMOTE.bits());
    }
    state.apply(&vendor, outbox, true, CLOCKS);
    state.world.ingest_item(&WeenieData {
        guid: GEM,
        name: Some("Gem".into()),
        container_id: Some(PLAYER),
        ..WeenieData::default()
    });
    approach_vendor(&mut state, VENDOR);
    assert_eq!(vendor_events(&mut state), [VendorEvent::Opened(VENDOR)]);
    with_binding(&mut state, outbox, |b| {
        assert_eq!(
            call::<calls::VendorAddSell>(b, &GEM).status,
            VendorStatus::Sent
        );
    });
    state
}

fn sell_list(state: &mut SessionState, outbox: &mut Outbox) -> Vec<u32> {
    with_binding(state, outbox, |b| {
        call::<calls::VendorState>(b, &()).sell_list
    })
}

#[test]
fn a_vendor_opens_once_and_no_approach_raises_nothing() {
    let mut state = in_world();
    assert!(vendor_events(&mut state).is_empty());
    approach_vendor(&mut state, VENDOR);
    assert_eq!(vendor_events(&mut state), [VendorEvent::Opened(VENDOR)]);
    assert!(vendor_events(&mut state).is_empty());
}

// OpenAC RuntimeVendorAutomationTests
// .UsingTheOpenVendorAgainOpensItForPluginsButABuyRefreshDoesNot, its first
// half. The lists stay, as the vendor window keeps what is staged when its
// own vendor's listing arrives again.
#[test]
fn using_the_open_vendor_again_opens_it_again_keeping_the_lists() {
    let mut outbox = Outbox::new();
    let mut state = vendor_with_a_sell_list(&mut outbox);
    assert!(vendor_events(&mut state).is_empty());
    use_vendor(&mut state, &mut outbox);
    assert!(
        vendor_events(&mut state).is_empty(),
        "not before the answer"
    );
    approach_vendor(&mut state, VENDOR);
    assert_eq!(vendor_events(&mut state), [VendorEvent::Opened(VENDOR)]);
    assert_eq!(sell_list(&mut state, &mut outbox), [GEM]);
    // The use is spent: the next listing is a refresh.
    approach_vendor(&mut state, VENDOR);
    assert!(vendor_events(&mut state).is_empty());
}

// The same test's second half: the server sends the listing again after
// every purchase and sale, and that is not the player coming to the vendor.
#[test]
fn the_listing_after_a_sale_does_not_open_the_vendor_again() {
    let mut outbox = Outbox::new();
    let mut state = vendor_with_a_sell_list(&mut outbox);
    approach_vendor(&mut state, VENDOR);
    assert!(vendor_events(&mut state).is_empty());

    // Nor when the player used the vendor before selling and the answer to
    // that use never came: the sale's listing is the sale's.
    use_vendor(&mut state, &mut outbox);
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::VendorSellAll>(b, &()).status,
            VendorStatus::Sent
        );
    });
    approach_vendor(&mut state, VENDOR);
    assert!(
        vendor_events(&mut state)
            .iter()
            .all(|e| !matches!(e, VendorEvent::Opened(_)))
    );
}

#[test]
fn another_vendor_closes_the_first_and_clears_the_lists() {
    let mut outbox = Outbox::new();
    let mut state = vendor_with_a_sell_list(&mut outbox);
    approach_vendor(&mut state, OTHER_VENDOR);
    assert_eq!(
        vendor_events(&mut state),
        [VendorEvent::Closed, VendorEvent::Opened(OTHER_VENDOR)]
    );
    assert!(sell_list(&mut state, &mut outbox).is_empty());
    assert!(vendor_events(&mut state).is_empty());
}
