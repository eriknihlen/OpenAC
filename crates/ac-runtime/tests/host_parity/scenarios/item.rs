//! Using, applying, moving, salvaging, selling and looting, through the
//! plugin surface on both arms (OpenAC `Scenarios/ItemParityTests.cs`):
//! each step asserts what this arm answered and what really left it, so two
//! arms agreeing about doing nothing prove nothing.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::{
    ItemAmount, ItemResult, ItemStatus, MoveRequest, PickupRequest, SalvageRequest,
};

use crate::host_parity::arm::{Arm, PACING, Setup, run, run_with};
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// How many things the character carries once staged.
const CARRIED_ITEMS: usize = 5;

const USE_ACTION: u32 = 0x0036;
const APPLY_ACTION: u32 = 0x0035;
const MOVE_ACTION: u32 = 0x0019;
const SALVAGE_ACTION: u32 = 0x027D;
const MERGE_ACTION: u32 = 0x0054;
const SPLIT_TO_CONTAINER_ACTION: u32 = 0x0055;
const ADD_SHORTCUT_ACTION: u32 = 0x019C;
const REMOVE_SHORTCUT_ACTION: u32 = 0x019D;

/// Past the appraisal slot's wait for an answer (4990 ms), as OpenAC's
/// scenario waits 400 ticks.
const PAST_THE_DESCRIPTION_WAIT: std::time::Duration = std::time::Duration::from_millis(5500);

pub fn sent(messages: &[Outbound], action: u32) -> Vec<Outbound> {
    messages
        .iter()
        .filter(|m| m.game_action() == Some(action))
        .cloned()
        .collect()
}

/// The one message of this action, which must be there exactly once.
pub fn one(messages: &[Outbound], action: u32) -> Outbound {
    let found = sent(messages, action);
    assert_eq!(found.len(), 1, "one 0x{action:04X} in {messages:?}");
    found[0].clone()
}

pub fn record(transcript: &mut Transcript, key: &str, result: &ItemResult) {
    transcript.record(&format!("{key}.status"), result.status);
    transcript.record(&format!("{key}.notice"), &result.notice);
}

fn record_item_state(transcript: &mut Transcript, arm: &mut Arm) {
    let state = arm.call::<calls::ItemsState>(&());
    transcript.record("available", state.is_available);
    transcript.record("busy", state.is_busy);
    transcript.record("owned", arm.call::<calls::ItemsOwned>(&()).len());
}

fn record_loot_state(transcript: &mut Transcript, arm: &mut Arm) {
    let loot = arm.call::<calls::LootState>(&());
    transcript.record("available", loot.is_available);
    transcript.record("busy", loot.is_busy);
    transcript.record_id("requested", loot.requested_container_id);
    transcript.record_id("current", loot.current_container_id);
    transcript.record("ready", loot.current_contents_ready);
}

fn record_appraisal(transcript: &mut Transcript, arm: &mut Arm) {
    let appraisal = arm.call::<calls::LootState>(&()).appraisal;
    transcript.record_id("appraisal.awaiting", appraisal.awaiting_id);
    transcript.record_id("appraisal.current", appraisal.current_id);
    transcript.record_id("appraisal.abandoned", appraisal.last_abandoned_id);
}

/// Lets the 200 ms pacing after the last use lapse before "busy" is read.
///
/// Once the server has answered a use, "busy" is only that pacing, and it
/// runs on each arm's own session clock in real time. Read straight after
/// the answer, it says how long the answer took to get through on that arm,
/// so the two arms disagree whenever one of them is slow (OpenAC reads it
/// one frame after the answer, on a clock both arms share). Read after
/// [`PACING`], it is the settled state, the same on every arm.
pub fn settle_the_pacing(arm: &mut Arm) {
    arm.wait(PACING);
}

/// The world staged and the packs filled, with the outbound record wiped.
pub fn stage_and_take_items(arm: &mut Arm) {
    world::stage(arm);
    world::stage_carried_items(arm);
    let _ = arm.outbound();
}

// OpenAC `ItemParityTests.AnItemsIconLayersReachAPluginOnBothClients`.
#[test]
fn an_items_icon_layers_reach_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);
        let owned = arm.call::<calls::ItemsOwned>(&());
        let kit = owned
            .iter()
            .find(|i| i.object_id == world::KIT)
            .unwrap_or_else(|| panic!("{}: the kit is owned", arm.name()));
        transcript.record_id("kit.underlay", kit.icon_underlay_id);
        transcript.record_id("kit.overlay", kit.icon_overlay_id);
        transcript.record_id("kit.coverage", kit.coverage_mask);
        transcript.record_id("kit.hookItemTypes", kit.hook_item_types);
        transcript.record("kit.plural", &kit.plural_name);
        assert_eq!(kit.icon_underlay_id, world::KIT_ICON_UNDERLAY);
        assert_eq!(kit.icon_overlay_id, world::KIT_ICON_OVERLAY);
        assert_eq!(kit.coverage_mask, world::KIT_COVERAGE);
        assert_eq!(kit.plural_name, "Healing Kits");
        assert_eq!(kit.hook_item_types, world::KIT_HOOK_ITEM_TYPES);

        // The same bits on the world-object record, before any appraisal.
        let object = arm
            .call::<calls::WorldObject>(&world::KIT)
            .expect("the kit is known");
        transcript.record_id("kit.worldCoverage", object.coverage_mask);
        assert!(!object.has_appraisal_data);
        transcript.record_id("kit.worldHookItemTypes", object.hook_item_types);
        assert_eq!(object.coverage_mask, world::KIT_COVERAGE);
        assert_eq!(object.hook_item_types, world::KIT_HOOK_ITEM_TYPES);
    });
}

// OpenAC `ItemParityTests.UsingACarriedItemLooksTheSameOnBothClients`.
#[test]
fn using_a_carried_item_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("before");
        record_item_state(transcript, arm);
        let state = arm.call::<calls::ItemsState>(&());
        assert!(state.is_available, "{} has no item surface", arm.name());
        assert!(!state.is_busy);
        assert_eq!(arm.call::<calls::ItemsOwned>(&()).len(), CARRIED_ITEMS);

        transcript.step("use the kit");
        let used = arm.call::<calls::ItemsUse>(&world::KIT);
        record(transcript, "use", &used);
        record_item_state(transcript, arm);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert!(arm.call::<calls::ItemsState>(&()).is_busy);
        let out = arm.record_outbound(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::KIT);

        transcript.step("the server answers");
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        arm.sync();
        settle_the_pacing(arm);
        record_item_state(transcript, arm);
        assert!(
            !arm.call::<calls::ItemsState>(&()).is_busy,
            "{}: the answer did not release the use",
            arm.name()
        );
        let completion = arm.call::<calls::ItemsState>(&()).last_completion;
        transcript.record("completion.error", completion.weenie_error);
        transcript.record_id("completion.source", completion.source_id);
        arm.record_outbound(transcript);

        transcript.step("and the next use is let through");
        let again = arm.call::<calls::ItemsUse>(&world::KIT);
        record(transcript, "use", &again);
        record_item_state(transcript, arm);
        assert_eq!(again.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::KIT);
    });
}

// OpenAC `ItemParityTests.ApplyingAnItemToATargetLooksTheSameOnBothClients`.
#[test]
fn applying_an_item_to_a_target_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("apply the stone to the kit");
        let applied = arm.call::<calls::ItemsApply>(&(world::TARGETED_ITEM, world::KIT));
        record(transcript, "apply", &applied);
        record_item_state(transcript, arm);
        assert_eq!(applied.status, ItemStatus::Started, "{}", arm.name());
        assert!(arm.call::<calls::ItemsState>(&()).is_busy);
        let out = arm.record_outbound(transcript);
        let apply = one(&out, APPLY_ACTION);
        assert_eq!(
            (apply.u32_at(12), apply.u32_at(16)),
            (world::TARGETED_ITEM, world::KIT)
        );

        transcript.step("apply it to something that is not there");
        let nowhere = arm.call::<calls::ItemsApply>(&(world::TARGETED_ITEM, 0x5000_00FF));
        record(transcript, "apply", &nowhere);
        assert_eq!(nowhere.status, ItemStatus::InvalidTarget, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, APPLY_ACTION).is_empty());
    });
}

// OpenAC `ItemParityTests.RefusingAnItemLooksTheSameOnBothClients`.
#[test]
fn refusing_an_item_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("use a targeted item with no target");
        let bare = arm.call::<calls::ItemsUse>(&world::TARGETED_ITEM);
        record(transcript, "use", &bare);
        assert_eq!(bare.status, ItemStatus::Refused, "{}", arm.name());
        assert_eq!(
            bare.notice.as_deref(),
            Some("This item requires a target; call Apply(objectId, targetObjectId) instead.")
        );

        transcript.step("use something that is not there");
        let missing = arm.call::<calls::ItemsUse>(&0x5000_00FF);
        record(transcript, "use", &missing);
        assert_eq!(missing.status, ItemStatus::InvalidItem);

        transcript.step("use a creature the player does not own");
        let creature = arm.call::<calls::ItemsUse>(&world::MONSTER);
        record(transcript, "use", &creature);
        assert_eq!(creature.status, ItemStatus::Refused);
        assert_eq!(creature.notice.as_deref(), Some("That cannot be used."));
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, USE_ACTION).is_empty());
        assert!(sent(&out, APPLY_ACTION).is_empty());
    });
}

// OpenAC `ItemParityTests.MovingAnItemIntoAPackLooksTheSameOnBothClients`.
#[test]
fn moving_an_item_into_a_pack_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("move the kit into the side pack");
        let moved = arm.call::<calls::ItemsMove>(&MoveRequest {
            object_id: world::KIT,
            container_id: world::SIDE_PACK,
            amount: 0,
            placement: 0,
        });
        record(transcript, "move", &moved);
        record_item_state(transcript, arm);
        assert_eq!(moved.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        let message = one(&out, MOVE_ACTION);
        assert_eq!(
            (message.u32_at(12), message.u32_at(16)),
            (world::KIT, world::SIDE_PACK)
        );

        transcript.step("and again while the first is in flight");
        let second = arm.call::<calls::ItemsMove>(&MoveRequest {
            object_id: world::SCRAP_ITEM,
            container_id: world::SIDE_PACK,
            amount: 0,
            placement: 0,
        });
        record(transcript, "move", &second);
        assert_eq!(second.status, ItemStatus::Busy, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, MOVE_ACTION).is_empty());
    });
}

// OpenAC `ItemParityTests.MovingAStackOntoOneInAPackLooksTheSameOnBothClients`.
#[test]
fn moving_a_stack_onto_one_in_a_pack_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_two_stacks_of_one_kind(arm);
        let _ = arm.outbound();

        transcript.step("move part of the loose coins, joining a stack");
        let join = arm.call::<calls::ItemsMoveJoinStack>(&(
            MoveRequest {
                object_id: world::CARRIED_COIN,
                container_id: world::SIDE_PACK,
                amount: 15,
                placement: 0,
            },
            true,
        ));
        record(transcript, "join", &join);
        // The join really went out as a merge naming the stack in the pack
        // and the amount asked for, and no plain move went with it.
        assert_eq!(join.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        let merge = one(&out, MERGE_ACTION);
        assert_eq!(
            (merge.u32_at(12), merge.u32_at(16), merge.u32_at(20)),
            (world::CARRIED_COIN, world::SIDE_PACK_COIN, 15)
        );
        assert!(sent(&out, MOVE_ACTION).is_empty());
        assert!(sent(&out, SPLIT_TO_CONTAINER_ACTION).is_empty());
    });
}

// OpenAC issue #219
// `ItemParityTests.MergingAStackOnTheToolbarMovesItsShortcutOnBothClients`:
// a stack on the shortcut bar that a plugin pours into another keeps its
// slot, now naming the stack it joined, and the server is told of the swap;
// when the server then takes the poured stack away, nothing more leaves for
// the bar. The owner is the runtime, so both arms run the same code; with
// the runtime's retarget taken away the slot is cleared instead when the
// poured stack goes.
#[test]
fn merging_a_stack_on_the_toolbar_moves_its_shortcut_on_both_clients() {
    const SLOT: i32 = 2;
    let setup = Setup {
        description: wire::Description {
            shortcuts: vec![(SLOT, world::CARRIED_COIN, 0)],
            ..wire::Description::default()
        },
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        world::stage(arm);
        world::stage_two_stacks_of_one_kind(arm);
        let _ = arm.outbound();

        transcript.step("pour the whole loose stack into the pack's stack");
        let join = arm.call::<calls::ItemsMoveJoinStack>(&(
            MoveRequest {
                object_id: world::CARRIED_COIN,
                container_id: world::SIDE_PACK,
                amount: 40,
                placement: 0,
            },
            true,
        ));
        record(transcript, "join", &join);
        assert_eq!(join.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        let merge = one(&out, MERGE_ACTION);
        assert_eq!(
            (merge.u32_at(12), merge.u32_at(16), merge.u32_at(20)),
            (world::CARRIED_COIN, world::SIDE_PACK_COIN, 40)
        );
        let remove = one(&out, REMOVE_SHORTCUT_ACTION);
        assert_eq!(remove.u32_at(12), SLOT as u32, "{}", arm.name());
        let add = one(&out, ADD_SHORTCUT_ACTION);
        assert_eq!(
            (add.u32_at(12), add.u32_at(16)),
            (SLOT as u32, world::SIDE_PACK_COIN),
            "{}",
            arm.name()
        );

        transcript.step("the server takes the poured stack away");
        arm.server
            .send(&wire::delete_object(world::CARRIED_COIN, 1));
        arm.sync();
        let out = arm.record_outbound(transcript);
        assert!(
            sent(&out, REMOVE_SHORTCUT_ACTION).is_empty(),
            "{}",
            arm.name()
        );
        assert!(sent(&out, ADD_SHORTCUT_ACTION).is_empty(), "{}", arm.name());
    });
}

// OpenAC `ItemParityTests.MovingAStackWithoutJoiningLooksTheSameOnBothClients`.
#[test]
fn moving_a_stack_without_joining_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_two_stacks_of_one_kind(arm);
        let _ = arm.outbound();

        transcript.step("move part of the loose coins, not joining");
        let split = arm.call::<calls::ItemsMoveJoinStack>(&(
            MoveRequest {
                object_id: world::CARRIED_COIN,
                container_id: world::SIDE_PACK,
                amount: 15,
                placement: 0,
            },
            false,
        ));
        record(transcript, "split", &split);
        assert_eq!(split.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, MERGE_ACTION).is_empty());
        let message = one(&out, SPLIT_TO_CONTAINER_ACTION);
        assert_eq!(message.u32_at(12), world::CARRIED_COIN);
        assert_eq!(message.u32_at(16), world::SIDE_PACK);
    });
}

// OpenAC `ItemParityTests.SalvagingAndSellingLookTheSameOnBothClients`.
#[test]
fn salvaging_and_selling_look_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("salvage the scrap with the tool");
        let salvage = arm.call::<calls::ItemsSalvage>(&SalvageRequest {
            tool_id: world::SALVAGE_TOOL,
            item_ids: vec![world::SCRAP_ITEM],
        });
        record(transcript, "salvage", &salvage);
        assert_eq!(salvage.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        let message = one(&out, SALVAGE_ACTION);
        assert_eq!(
            (message.u32_at(12), message.u32_at(20)),
            (world::SALVAGE_TOOL, world::SCRAP_ITEM)
        );

        transcript.step("salvage with something that is not a tool");
        let not_a_tool = arm.call::<calls::ItemsSalvage>(&SalvageRequest {
            tool_id: world::KIT,
            item_ids: vec![world::SCRAP_ITEM],
        });
        record(transcript, "salvage", &not_a_tool);
        assert_eq!(
            not_a_tool.status,
            ItemStatus::InvalidTarget,
            "{}",
            arm.name()
        );

        transcript.step("sell with no vendor trading");
        let vendor = arm.call::<calls::ItemsState>(&()).active_vendor_id;
        transcript.record_id("vendor", vendor);
        let sell = arm.call::<calls::ItemsSell>(&ItemAmount {
            object_id: world::SCRAP_ITEM,
            target_id: 0,
            amount: 0,
        });
        record(transcript, "sell", &sell);
        assert_eq!(vendor, 0);
        assert_eq!(sell.status, ItemStatus::InvalidTarget, "{}", arm.name());
        assert_eq!(sell.notice.as_deref(), Some("No vendor is open."));
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, SALVAGE_ACTION).is_empty());
    });
}

// OpenAC `ItemParityTests.APacedUseIsReportedBusyOnBothClients`.
#[test]
fn a_paced_use_is_reported_busy_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);

        transcript.step("open the corpse");
        let first = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &first);
        assert_eq!(first.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::CORPSE);

        transcript.step("open it again straight away");
        let too_soon = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &too_soon);
        assert_eq!(too_soon.status, ItemStatus::Busy, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, USE_ACTION).is_empty());

        transcript.step("and again once the pacing has lapsed");
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        arm.sync();
        arm.wait(PACING);
        let later = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &later);
        assert_eq!(later.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::CORPSE);
    });
}

// OpenAC `ItemParityTests.ACorpseCycleLooksTheSameOnBothClients`.
#[test]
fn a_corpse_cycle_looks_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);

        transcript.step("nothing open");
        record_loot_state(transcript, arm);
        let corpses = arm.call::<calls::LootCorpses>(&10.0);
        transcript.record("corpses", corpses.len());
        let loot = arm.call::<calls::LootState>(&());
        assert!(!loot.is_busy);
        assert_eq!(loot.current_container_id, 0);
        assert_eq!(corpses.len(), 1, "{}", arm.name());
        assert_eq!(corpses[0].object_id, world::CORPSE);

        transcript.step("open it");
        let open = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &open);
        record_loot_state(transcript, arm);
        assert_eq!(open.status, ItemStatus::Started, "{}", arm.name());
        assert_eq!(
            arm.call::<calls::LootState>(&()).requested_container_id,
            world::CORPSE
        );
        let out = arm.record_outbound(transcript);
        assert_eq!(one(&out, USE_ACTION).u32_at(12), world::CORPSE);

        transcript.step("the contents arrive");
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        world::deliver_corpse_contents(arm);
        arm.sync();
        settle_the_pacing(arm);
        record_loot_state(transcript, arm);
        let contents = arm.call::<calls::LootContents>(&());
        transcript.record("contents.count", contents.len());
        for (index, item) in contents.iter().enumerate() {
            transcript.record_id(&format!("contents[{index}].id"), item.object_id);
            transcript.record(&format!("contents[{index}].name"), &item.name);
        }
        let loot = arm.call::<calls::LootState>(&());
        assert_eq!(loot.current_container_id, world::CORPSE, "{}", arm.name());
        assert!(
            loot.current_contents_ready,
            "{} never finished reading the corpse",
            arm.name()
        );
        assert!(
            !loot.is_busy,
            "{}: the answer did not release the open",
            arm.name()
        );
        assert_eq!(
            contents.iter().map(|i| i.object_id).collect::<Vec<_>>(),
            [world::CORPSE_COIN, world::CORPSE_GEM]
        );

        transcript.step("look the gem over");
        let identify = arm.call::<calls::LootIdentify>(&world::CORPSE_GEM);
        record(transcript, "identify", &identify);
        record_appraisal(transcript, arm);
        assert_eq!(identify.status, ItemStatus::Started, "{}", arm.name());
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::CORPSE_GEM
        );
        arm.record_outbound(transcript);

        transcript.step("the description comes back");
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(world::CORPSE_GEM, &[], true),
        );
        arm.sync();
        record_appraisal(transcript, arm);
        let appraisal = arm.call::<calls::LootState>(&()).appraisal;
        assert_eq!(appraisal.awaiting_id, 0, "{}", arm.name());
        assert_eq!(appraisal.current_id, world::CORPSE_GEM);

        transcript.step("take the gem");
        let pickup = arm.call::<calls::LootPickup>(&PickupRequest {
            object_id: world::CORPSE_GEM,
            main_pack: false,
        });
        record(transcript, "pickup", &pickup);
        record_loot_state(transcript, arm);
        assert_eq!(pickup.status, ItemStatus::Started, "{}", arm.name());
        let out = arm.record_outbound(transcript);
        let take = one(&out, MOVE_ACTION);
        assert_eq!(
            (take.u32_at(12), take.u32_at(16)),
            (world::CORPSE_GEM, world::PLAYER)
        );

        transcript.step("try to close while the take is out");
        arm.wait(PACING);
        let close = arm.call::<calls::LootClose>(&world::CORPSE);
        record(transcript, "close", &close);
        record_loot_state(transcript, arm);
        assert_eq!(close.status, ItemStatus::Busy, "{}", arm.name());
        assert_eq!(
            arm.call::<calls::LootState>(&()).current_container_id,
            world::CORPSE
        );
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, MOVE_ACTION).is_empty());
    });
}

// OpenAC `ItemParityTests.AnUnansweredDescriptionExpiresTheSameOnBothClients`.
#[test]
fn an_unanswered_description_expires_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_and_take_items(arm);

        transcript.step("ask about the kit");
        let first = arm.call::<calls::WorldIdentify>(&world::KIT);
        record(transcript, "identify", &first);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);

        transcript.step("ask about something else while the first is out");
        let second = arm.call::<calls::WorldIdentify>(&world::SCRAP_ITEM);
        record(transcript, "identify", &second);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);

        transcript.step("nothing ever answers");
        arm.wait(PAST_THE_DESCRIPTION_WAIT);
        record_appraisal(transcript, arm);
        transcript.record("busy", arm.call::<calls::ItemsState>(&()).is_busy);

        transcript.step("ask about something else after giving up");
        let third = arm.call::<calls::WorldIdentify>(&world::SCRAP_ITEM);
        record(transcript, "identify", &third);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::SCRAP_ITEM,
            "{}",
            arm.name()
        );
    });
}
