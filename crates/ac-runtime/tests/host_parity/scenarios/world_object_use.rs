//! Using things in the world through the plugin surface (OpenAC
//! `Scenarios/WorldObjectUseParityTests.cs`): a corpse a few metres off, a
//! guid nothing answers to, the character itself, two uses in a row, a
//! loose book and something in the open corpse.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::ItemStatus;

use crate::host_parity::arm::{Arm, Setup, run, run_with};
use crate::host_parity::scenarios::item::{one, record, sent};
use crate::host_parity::wire::{self, Object};
use crate::host_parity::world;

const LOOSE_BOOK: u32 = 0x5000_0060;
const USE_ACTION: u32 = 0x0036;
const PICK_UP_ACTION: u32 = 0x0019;
/// Item type of a book (`ac-core` `ItemType::WRITABLE`).
const WRITABLE: u32 = 0x2000;
/// The "no" use rule.
const USEABLE_NO: u32 = 0x1;

fn stage_world(arm: &mut Arm) {
    world::stage(arm);
    world::stage_corpse(arm, 3.0);
    let _ = arm.outbound();
}

fn busy(arm: &mut Arm) -> bool {
    arm.call::<calls::ItemsState>(&()).is_busy
}

/// With the dats the character has a body; a plugin's use of a corpse out
/// of reach still goes out at once, and the server does the walking.
fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

// OpenAC `WorldObjectUseParityTests.UsingACorpseSeveralMetresOffLooksTheSameOnBothClients`
// (where the client walks there first and sends nothing meanwhile): the use
// goes out at once and holds the client busy until the server answers.
#[test]
#[ignore = "needs the retail dats: a body that could walk to the corpse"]
fn using_a_corpse_several_metres_off_looks_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_world(arm);

        transcript.step("use the corpse");
        let used = arm.call::<calls::ItemsUse>(&world::CORPSE);
        record(transcript, "use", &used);
        let is_busy = busy(arm);
        transcript.record("busy", is_busy);
        let out = arm.record_outbound_while_walking(transcript);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert!(is_busy);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::CORPSE);

        transcript.step("a few frames later");
        arm.wait(std::time::Duration::from_millis(150));
        let is_busy = busy(arm);
        transcript.record("busy", is_busy);
        let out = arm.record_outbound_while_walking(transcript);
        assert!(is_busy);
        assert!(sent(&out, USE_ACTION).is_empty());
    });
}

// OpenAC `WorldObjectUseParityTests.UsingSomethingThatIsNotThereIsRefusedTheSameOnBothClients`.
#[test]
fn using_something_that_is_not_there_is_refused_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("use a guid nothing answers to");
        let used = arm.call::<calls::ItemsUse>(&0x5000_00FF);
        record(transcript, "use", &used);
        let is_busy = busy(arm);
        transcript.record("busy", is_busy);
        let out = arm.record_outbound(transcript);
        assert_eq!(used.status, ItemStatus::InvalidItem, "{}", arm.name());
        assert!(!is_busy);
        assert!(sent(&out, USE_ACTION).is_empty());
    });
}

// OpenAC `WorldObjectUseParityTests.UsingTheCharacterItselfIsSentTheSameWayOnBothClients`.
#[test]
fn using_the_character_itself_is_sent_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("use the character");
        let used = arm.call::<calls::ItemsUse>(&world::PLAYER);
        record(transcript, "use", &used);
        let is_busy = busy(arm);
        transcript.record("busy", is_busy);
        let out = arm.record_outbound(transcript);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert!(is_busy);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::PLAYER);
    });
}

// OpenAC `WorldObjectUseParityTests.TwoUsesInARowAreRefusedTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that could walk to the corpse"]
fn two_uses_in_a_row_are_refused_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_world(arm);
        transcript.step("use the corpse");
        let first = arm.call::<calls::ItemsUse>(&world::CORPSE);
        record(transcript, "first", &first);
        assert_eq!(first.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound_while_walking(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::CORPSE);

        transcript.step("ask again straight away");
        let second = arm.call::<calls::ItemsUse>(&world::CORPSE);
        record(transcript, "second", &second);
        let is_busy = busy(arm);
        transcript.record("busy", is_busy);
        let out = arm.record_outbound_while_walking(transcript);
        assert_eq!(second.status, ItemStatus::Busy, "{}", arm.name());
        assert!(is_busy);
        assert!(sent(&out, USE_ACTION).is_empty());
    });
}

// OpenAC `WorldObjectUseParityTests.UsingALooseBookPicksItUpTheSameOnBothClients`.
#[test]
fn using_a_loose_book_picks_it_up_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        let book = Object {
            stack_size: Some(1),
            useability: Some(USEABLE_NO),
            ..world::on_the_ground(LOOSE_BOOK, "Damaged Tome", WRITABLE, world::PLAYER_X + 2.0)
        };
        arm.server.send(&book.build());
        arm.sync();
        let _ = arm.outbound();

        transcript.step("use the book");
        let used = arm.call::<calls::ItemsUse>(&LOOSE_BOOK);
        record(transcript, "use", &used);
        let out = arm.record_outbound(transcript);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert!(sent(&out, USE_ACTION).is_empty());
        let pickup = one(&out, PICK_UP_ACTION);
        assert_eq!(
            (pickup.u32_at(12), pickup.u32_at(16)),
            (LOOSE_BOOK, world::PLAYER)
        );
    });
}

// OpenAC `WorldObjectUseParityTests.UsingSomethingInTheOpenCorpsePicksItUpTheSameOnBothClients`:
// OpenAC marks the corpse open on the runtime; here the client opens it and
// the server answers, which is how a corpse comes to be open.
#[test]
fn using_something_in_the_open_corpse_picks_it_up_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
        assert_eq!(
            arm.call::<calls::LootOpen>(&world::CORPSE).status,
            ItemStatus::Started,
            "{}",
            arm.name()
        );
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        world::deliver_corpse_contents(arm);
        arm.sync();
        arm.wait(crate::host_parity::arm::PACING);
        let _ = arm.outbound();

        transcript.step("use the gem inside");
        let used = arm.call::<calls::ItemsUse>(&world::CORPSE_GEM);
        record(transcript, "use", &used);
        let out = arm.record_outbound(transcript);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert!(sent(&out, USE_ACTION).is_empty());
        let pickup = one(&out, PICK_UP_ACTION);
        assert_eq!(
            (pickup.u32_at(12), pickup.u32_at(16)),
            (world::CORPSE_GEM, world::PLAYER)
        );
    });
}
