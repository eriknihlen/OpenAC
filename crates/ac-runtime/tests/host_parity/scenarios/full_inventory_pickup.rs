//! Taking from a corpse with no room left (OpenAC
//! `Scenarios/FullInventoryPickupParityTests.cs`): coins that join a
//! carried stack need no slot and go; a gem that needs one is refused and
//! says why.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::{ItemStatus, PickupRequest};

use crate::host_parity::arm::{Arm, PACING, run};
use crate::host_parity::scenarios::item::record;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

fn open_the_corpse_with_no_room_left(arm: &mut Arm, transcript: &mut Transcript) {
    world::stage(arm);
    world::stage_a_full_inventory_over_a_stack(arm);
    world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
    let _ = arm.outbound();

    transcript.step("open the corpse");
    let open = arm.call::<calls::LootOpen>(&world::CORPSE);
    record(transcript, "open", &open);
    arm.record_outbound(transcript);

    transcript.step("the contents arrive");
    arm.server
        .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
    world::deliver_corpse_contents(arm);
    arm.sync();
    let loot = arm.call::<calls::LootState>(&());
    transcript.record("ready", loot.current_contents_ready);
    transcript.record("contents", arm.call::<calls::LootContents>(&()).len());
    arm.wait(PACING);
}

// OpenAC `FullInventoryPickupParityTests.TakingWhatJoinsACarriedStackLooksTheSameOnBothClients`.
#[test]
fn taking_what_joins_a_carried_stack_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        open_the_corpse_with_no_room_left(arm, transcript);
        transcript.step("take coins that join the carried stack");
        let coins = arm.call::<calls::LootPickup>(&PickupRequest {
            object_id: world::CORPSE_COIN,
            main_pack: false,
        });
        record(transcript, "pickup", &coins);
        arm.record_outbound(transcript);
        assert_eq!(coins.status, ItemStatus::Started, "{}", arm.name());
    });
}

// OpenAC `FullInventoryPickupParityTests.TakingWhatNeedsASlotIsRefusedTheSameOnBothClients`.
#[test]
fn taking_what_needs_a_slot_is_refused_the_same_on_both_clients() {
    run(|arm, transcript| {
        open_the_corpse_with_no_room_left(arm, transcript);
        transcript.step("take a gem, which needs a slot of its own");
        let gem = arm.call::<calls::LootPickup>(&PickupRequest {
            object_id: world::CORPSE_GEM,
            main_pack: false,
        });
        record(transcript, "pickup", &gem);
        arm.record_outbound(transcript);
        assert_eq!(gem.status, ItemStatus::Refused, "{}", arm.name());
        assert!(gem.notice.as_ref().is_some_and(|n| !n.trim().is_empty()));
    });
}
