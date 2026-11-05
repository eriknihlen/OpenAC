//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeHouseStateTests.cs`. Dates
//! print as the C runtime's `%c`, in UTC here.

use ac_core::physics::lcoord_to_gid;
use ac_proto::Position;
use ac_proto::house::{AvailableHouses, HouseAccess, HouseGuest};

use super::*;

/// 2026-08-17 00:00 UTC, the test clock's start.
const NOW: i64 = 1_786_924_800;
const DAY: i64 = 86_400;

fn clock(now: i64, purchase_timestamp: i32) -> HouseClock {
    HouseClock {
        purchase_timestamp,
        now_unix_s: now,
        utc_offset_seconds: 0,
    }
}

fn payment(num: i32, paid: i32, name: &str, plural: &str) -> HousePayment {
    HousePayment {
        num,
        paid,
        weenie_id: 1,
        name: name.into(),
        plural_name: plural.into(),
    }
}

fn sample() -> HouseData {
    HouseData {
        buy_time: 0,
        rent_time: 0,
        house_type: 0,
        maintenance_free: false,
        buy: Vec::new(),
        rent: Vec::new(),
        position: Position::default(),
    }
}

fn at(cell_id: u32) -> Position {
    Position {
        cell_id,
        ..Position::default()
    }
}

fn texts(house: &HouseState) -> Vec<&str> {
    house.lines().iter().map(|(t, _)| t.as_str()).collect()
}

fn colors(house: &HouseState) -> Vec<HouseLineColor> {
    house.lines().iter().map(|(_, c)| *c).collect()
}

#[test]
fn empty_before_any_notice() {
    let house = HouseState::default();
    assert!(house.lines().is_empty());
    assert!(!house.has_received_notice());
}

#[test]
fn the_revision_moves_on_each_house_change() {
    let mut house = HouseState::default();
    assert_eq!(house.view().revision, 0);
    house.apply_house_data(&sample(), clock(NOW, 0));
    let learned = house.view().revision;
    assert!(learned > 0);
    house.apply_rent_time(123, clock(NOW, 0));
    assert!(house.view().revision > learned);
    house.apply_house_status(clock(NOW, 0));
    assert!(house.view().revision > learned);
}

#[test]
fn a_fresh_houseless_character_reads_both_lines() {
    let mut house = HouseState::default();
    house.apply_house_status(clock(NOW, 0));
    assert!(house.has_received_notice());
    assert_eq!(
        texts(&house),
        [
            "You do not currently own a house.",
            "You may buy another house immediately."
        ]
    );
}

#[test]
fn an_owned_unpaid_cottage_composes_every_line_in_order() {
    let mut house = HouseState::default();
    let buy_time = 1_700_000_000u32;
    let rent_time = 1_700_086_400u32;
    let data = HouseData {
        buy_time,
        rent_time,
        house_type: 1,
        buy: vec![
            payment(1, 0, "Pyreal", "Pyreals"),
            payment(2, 0, "Trade Note", ""),
        ],
        rent: vec![
            payment(10, 4, "Pyreal", "Pyreals"),
            payment(2, 2, "Box", ""),
        ],
        position: at(cell(1200, 800)),
        ..sample()
    };
    house.apply_house_data(&data, clock(NOW, 0));
    let rent_end = i64::from(rent_time) + 2_592_000;
    assert_eq!(
        texts(&house),
        [
            "The purchase price for this dwelling is:\n1 Pyreal, 2 Trade Notes".to_owned(),
            "Rent:\n4/10 Pyreals, 2/2 Boxes".to_owned(),
            format!("Bought: {}", convert_time(i64::from(buy_time), 0)),
            format!("This maintenance period ends: {}", convert_time(rent_end, 0)),
            format!("Maintenance is next due: {}", convert_time(rent_end, 0)),
            "Location: 21.9S, 18.1E".to_owned(),
            "Warning!  You have not paid your maintenance costs for the last 30 day maintenance period.  Please pay these costs by this deadline or you will lose your house, and all your items within it.".to_owned(),
            "You may buy another house immediately after you abandon this one.".to_owned(),
        ]
    );
    let colors = colors(&house);
    assert!(colors[..6].iter().all(|&c| c == HouseLineColor::Normal));
    assert_eq!(
        (colors[6], colors[7]),
        (HouseLineColor::RentNotPaid, HouseLineColor::Normal)
    );
}

#[test]
fn paid_rent_is_next_due_a_period_later_and_reads_paid() {
    let rent_time = 1_700_086_400u32;
    let mut house = HouseState::default();
    house.apply_house_data(
        &HouseData {
            rent_time,
            house_type: 1,
            rent: vec![payment(5, 5, "Token", "Tokens")],
            ..sample()
        },
        clock(NOW, 0),
    );
    assert_eq!(
        texts(&house)[4],
        format!(
            "Maintenance is next due: {}",
            convert_time(i64::from(rent_time) + 5_184_000, 0)
        )
    );
    let n = house.lines().len();
    assert_eq!(
        house.lines()[n - 2],
        (
            "The maintenance has already been paid for this period. You may not prepay next period's maintenance.".to_owned(),
            HouseLineColor::RentPaid
        )
    );
}

#[test]
fn an_apartment_has_a_ninety_day_period_and_no_location() {
    let rent_time = 1_700_086_400u32;
    let mut house = HouseState::default();
    house.apply_house_data(
        &HouseData {
            rent_time,
            house_type: 4,
            maintenance_free: true,
            position: at(cell(1200, 800)),
            ..sample()
        },
        clock(NOW, 0),
    );
    let lines = texts(&house);
    assert_eq!(
        lines[3],
        format!(
            "This maintenance period ends: {}",
            convert_time(i64::from(rent_time) + 7_776_000, 0)
        )
    );
    assert_eq!(
        lines[4],
        format!(
            "Maintenance is next due: {}",
            convert_time(i64::from(rent_time) + 15_552_000, 0)
        )
    );
    assert!(lines.iter().all(|l| !l.starts_with("Location: ")));
    let colors = colors(&house);
    assert_eq!(colors[colors.len() - 2], HouseLineColor::RentPaid);
}

#[test]
fn rent_notices_update_the_house_and_its_lines() {
    let mut house = HouseState::default();
    house.apply_house_data(
        &HouseData {
            rent_time: 1_700_000_000,
            rent: vec![payment(10, 10, "Pyreal", "Pyreals")],
            ..sample()
        },
        clock(NOW, 0),
    );
    let paid = |h: &HouseState| colors(h)[h.lines().len() - 2];
    assert_eq!(paid(&house), HouseLineColor::RentPaid);
    house.apply_rent_time(1_710_000_000, clock(NOW, 0));
    assert_eq!(texts(&house)[1], "Rent:\n0/10 Pyreals");
    assert_eq!(paid(&house), HouseLineColor::RentNotPaid);
    assert_eq!(
        texts(&house)[3],
        format!(
            "This maintenance period ends: {}",
            convert_time(1_712_592_000, 0)
        )
    );
    house.apply_rent_payment(&[payment(10, 10, "Pyreal", "Pyreals")], clock(NOW, 0));
    assert_eq!(texts(&house)[1], "Rent:\n10/10 Pyreals");
    assert_eq!(paid(&house), HouseLineColor::RentPaid);
}

#[test]
fn a_recent_purchase_names_when_another_may_be_bought() {
    let bought = i32::try_from(NOW).unwrap();
    let mut house = HouseState::default();
    house.apply_house_status(clock(NOW + 29 * DAY, bought));
    let lines = texts(&house);
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0], "You do not currently own a house.");
    assert_eq!(
        lines[1],
        format!(
            "You may buy another landscape house at {}. This restriction does not apply to apartments.",
            convert_time(NOW + 0x0027_8D00, 0)
        )
    );
}

#[test]
fn a_purchase_past_thirty_days_reads_both_houseless_lines() {
    let bought = i32::try_from(NOW).unwrap();
    let mut house = HouseState::default();
    house.apply_house_status(clock(NOW + 31 * DAY, bought));
    assert_eq!(
        texts(&house),
        [
            "You do not currently own a house.",
            "You may buy another house immediately."
        ]
    );
}

#[test]
fn a_session_reset_empties_the_house() {
    let mut house = HouseState::default();
    house.apply_house_status(clock(NOW, 0));
    house.reset_session();
    assert!(house.lines().is_empty());
    assert!(!house.has_received_notice());
}

#[test]
fn dates_print_as_the_c_runtime_s_default_c_format() {
    // 1700000000 = 2023-11-14 22:13:20 UTC.
    assert_eq!(convert_time(1_700_000_000, 0), "11/14/23 22:13:20");
    assert_eq!(convert_time(1_700_000_000, 3600), "11/14/23 23:13:20");
    assert_eq!(convert_time(0, 0), "N/A");
}

#[test]
fn the_available_houses_list_their_map_coordinates() {
    // OpenAC `ClientCommandResponses.FormatAvailableHousesLines`.
    let houses = AvailableHouses {
        house_type: 1,
        locations: vec![cell(1200, 800)],
        total_available: 1,
    };
    assert_eq!(
        available_houses_lines(&houses),
        ["There are 1 cottages available.", "     21.9S, 18.1E"]
    );
    let apartments = AvailableHouses {
        house_type: 4,
        locations: vec![cell(1200, 800)],
        total_available: 401,
    };
    assert_eq!(
        available_houses_lines(&apartments),
        ["There are 401 apartments available."]
    );
}

fn cell(lx: i32, ly: i32) -> u32 {
    lcoord_to_gid(lx, ly).unwrap()
}

// OpenAC `tests/AcDream.Core.Net.Tests/HouseGuestListTextTests.cs`.

fn access(bitmask: u32, guests: &[(&str, bool)], roommates: &[u32]) -> HouseAccess {
    HouseAccess {
        bitmask,
        monarch_id: 0x5000_0001,
        guests: guests
            .iter()
            .zip(0x5000_0002u32..)
            .map(|(&(name, storage), guid)| HouseGuest {
                guid,
                name: name.into(),
                storage,
            })
            .collect(),
        roommates: roommates.to_vec(),
    }
}

#[test]
fn an_empty_guest_list_reads_guests_then_none() {
    assert_eq!(guest_list_text(&access(0, &[], &[])), "Guests:\n  None\n");
}

#[test]
fn a_storage_guest_is_marked_with_an_asterisk() {
    assert_eq!(
        guest_list_text(&access(0, &[("Alice", false), ("Bob", true)], &[])),
        "Guests:\n  Alice\n  Bob *\n"
    );
}

#[test]
fn one_allegiance_bit_adds_its_permission_line() {
    for (bitmask, line) in [
        (0x2, "Allegiance Permissions: guest access\n"),
        (0x4, "Allegiance Permissions: storage access\n"),
    ] {
        assert_eq!(
            guest_list_text(&access(bitmask, &[], &[])),
            format!("Guests:\n  None\n{line}")
        );
    }
}

#[test]
fn both_allegiance_bits_run_together_with_no_separator() {
    assert_eq!(
        guest_list_text(&access(0x6, &[("Alice", true)], &[])),
        "Guests:\n  Alice *\nAllegiance Permissions: guest accessstorage access\n"
    );
}

#[test]
fn an_open_house_ends_with_the_open_house_line() {
    assert_eq!(
        guest_list_text(&access(0x1, &[], &[])),
        "Guests:\n  None\n[ open house ]\n"
    );
}

#[test]
fn all_flags_print_permissions_before_open_house_and_no_roommates() {
    assert_eq!(
        guest_list_text(&access(0x7, &[("Alice", false)], &[0x5000_0004])),
        "Guests:\n  Alice\nAllegiance Permissions: guest accessstorage access\n[ open house ]\n"
    );
}
