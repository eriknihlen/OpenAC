//! Letting go of an item the server refuses to describe (OpenAC
//! `Scenarios/StaleItemParityTests.cs`): the refusal marks it, a plugin may
//! let go of it (and only of such an item), and nothing about it reaches
//! the server. OpenAC's windowed host let go through its own delete route;
//! here there is one route, whatever is attached.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::ItemStatus;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::scenarios::item::record;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::Object;
use crate::host_parity::{wire, world};

const GHOST: u32 = 0x5000_0060;
const KEEPER: u32 = 0x5000_0061;
const VALUE_PROPERTY: u32 = 19;

fn stage(arm: &mut Arm) {
    world::stage(arm);
    world::stage_carried_items(arm);
    for (guid, name) in [(GHOST, "Lost Gem"), (KEEPER, "Kept Gem")] {
        let carried = Object {
            container: Some(world::PLAYER),
            ..Object::new(guid, name, world::GEM)
        };
        arm.server.send(&carried.build());
    }
    arm.sync();
    let _ = arm.outbound();
}

fn record_appraisal(transcript: &mut Transcript, arm: &mut Arm) {
    let appraisal = arm.call::<calls::LootState>(&()).appraisal;
    transcript.record_id("appraisal.awaiting", appraisal.awaiting_id);
    transcript.record_id("appraisal.current", appraisal.current_id);
    transcript.record(
        "appraisal.unsuccessful",
        appraisal.current_object_unsuccessful,
    );
}

fn record_object(transcript: &mut Transcript, arm: &mut Arm, id: u32) {
    let object = arm.call::<calls::WorldObject>(&id);
    transcript.record(&format!("known[0x{id:08X}]"), object.is_some());
    transcript.record(
        &format!("unsuccessful[0x{id:08X}]"),
        object.is_some_and(|o| o.last_appraisal_unsuccessful),
    );
}

// OpenAC `StaleItemParityTests.AnItemTheServerRefusesToAppraiseIsLetGoOfTheSameOnBothClients`.
#[test]
fn an_item_the_server_refuses_to_appraise_is_let_go_of_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("ask about the lost item; the server refuses");
        let identify = arm.call::<calls::WorldIdentify>(&GHOST);
        record(transcript, "identify", &identify);
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(GHOST, &[], false),
        );
        arm.sync();
        record_appraisal(transcript, arm);
        record_object(transcript, arm, GHOST);
        arm.record_outbound(transcript);
        let appraisal = arm.call::<calls::LootState>(&()).appraisal;
        assert_eq!(appraisal.current_id, GHOST, "{}", arm.name());
        assert!(appraisal.current_object_unsuccessful);
        let ghost = arm.call::<calls::WorldObject>(&GHOST).expect("known");
        assert!(ghost.last_appraisal_unsuccessful);

        transcript.step("ask about the kept item; the server answers");
        let identify = arm.call::<calls::WorldIdentify>(&KEEPER);
        record(transcript, "identify", &identify);
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(KEEPER, &[(VALUE_PROPERTY, 40)], true),
        );
        arm.sync();
        record_appraisal(transcript, arm);
        record_object(transcript, arm, KEEPER);
        arm.record_outbound(transcript);
        let appraisal = arm.call::<calls::LootState>(&()).appraisal;
        assert_eq!(appraisal.current_id, KEEPER, "{}", arm.name());
        assert!(!appraisal.current_object_unsuccessful);

        transcript.step("let go of the kept item");
        let kept = arm.call::<calls::ItemsForgetStale>(&KEEPER);
        record(transcript, "forget", &kept);
        assert_eq!(kept.status, ItemStatus::Refused, "{}", arm.name());
        assert!(arm.call::<calls::WorldObject>(&KEEPER).is_some());

        transcript.step("let go of the lost item");
        let _ = arm.outbound();
        let forgot = arm.call::<calls::ItemsForgetStale>(&GHOST);
        record(transcript, "forget", &forgot);
        record_object(transcript, arm, GHOST);
        let owned_ghost = arm
            .call::<calls::ItemsOwned>(&())
            .iter()
            .any(|i| i.object_id == GHOST);
        transcript.record("owned.has-ghost", owned_ghost);
        assert_eq!(forgot.status, ItemStatus::Completed, "{}", arm.name());
        assert!(
            arm.call::<calls::WorldObject>(&GHOST).is_none(),
            "{}: the lost item is still known",
            arm.name()
        );
        assert!(!owned_ghost);
        assert!(arm.call::<calls::WorldObject>(&KEEPER).is_some());
        // Nothing about the item may reach the server.
        let out = arm.record_outbound(transcript);
        let ghost_bytes = GHOST.to_le_bytes();
        assert!(
            !out.iter()
                .any(|m| m.body.windows(4).any(|w| w == ghost_bytes)),
            "{}: {out:?}",
            arm.name()
        );

        transcript.step("let go of it a second time");
        let again = arm.call::<calls::ItemsForgetStale>(&GHOST);
        record(transcript, "forget", &again);
        assert_eq!(again.status, ItemStatus::InvalidItem);

        transcript.step("let go of the character");
        let itself = arm.call::<calls::ItemsForgetStale>(&world::PLAYER);
        record(transcript, "forget", &itself);
        assert_eq!(itself.status, ItemStatus::InvalidItem);
        assert!(arm.call::<calls::WorldObject>(&world::PLAYER).is_some());

        transcript.step("let go of something not carried");
        let monster = arm.call::<calls::ItemsForgetStale>(&world::MONSTER);
        record(transcript, "forget", &monster);
        assert_eq!(monster.status, ItemStatus::InvalidItem);
        assert!(arm.call::<calls::WorldObject>(&world::MONSTER).is_some());
        arm.record_outbound(transcript);
    });
}
