//! Walks the character is sent on, on both arms (OpenAC
//! `Scenarios/ServerMoveToParityTests.cs`, `WalkThenUseParityTests.cs`):
//! the server ordering it to a creature, a plugin taking it back, a swing
//! from out of range, and a plugin's use of something out of reach, which
//! goes to the server at once for the server to walk the character there.
//!
//! OpenAC reads the walk off the body's own move-to manager (what kind of
//! walk was installed, what it seeks). A plugin sees the walk as the
//! character moving and closing the distance, which is what is recorded and
//! held here; the arms run in real time, so what is recorded is the
//! outcome of each step, not distances to the millimetre.
//!
//! OpenAC lays everything along one line east of the character, the corpse
//! on the spot the first creature stands on: its harness has no shapes for
//! objects, so nothing is in anyone's way. With the dats every body here is
//! solid, so what a walk is sent to is placed west, with nothing between:
//! a creature seven metres off (OpenAC's second creature) and the corpse
//! three.

use std::time::{Duration, Instant};

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::{AttackHeight, AttackRequest, CombatMode, CombatStatus};
use ac_plugin::abi::items::{ItemResult, ItemStatus};
use ac_plugin::abi::navigation::MovementIntent;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::scenarios::item::sent;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// How close the order tells the character to get, metres.
const ORDERED_DISTANCE: f32 = 1.5;
/// The client action that says "use that".
const USE_ACTION: u32 = 0x0036;

fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

/// How far off `id` is across the ground, metres.
fn distance(arm: &mut Arm, id: u32) -> f64 {
    let me = arm.call::<calls::NavigationSnapshot>(&()).position;
    arm.call::<calls::NavigationObject>(&id)
        .map_or(f64::NAN, |o| me.horizontal_distance_metres(&o.position))
}

/// The character is under way: over a moment it turns or its place
/// changes. (The snapshot's `is_moving` is about held movement input; a
/// walk the client carries out shows as the body turning to face where it
/// goes, then going.)
fn moving(arm: &mut Arm) -> bool {
    let before = arm.call::<calls::NavigationSnapshot>(&()).position;
    arm.wait(Duration::from_millis(150));
    let after = arm.call::<calls::NavigationSnapshot>(&()).position;
    let turned = (after.heading_degrees - before.heading_degrees).abs() > 0.5;
    turned || before.horizontal_distance_metres(&after) > 0.05
}

/// A creature seven metres west, with nothing between it and the
/// character.
const FAR_CREATURE: u32 = 0x5000_0014;

fn stage_with_a_far_creature(arm: &mut Arm) {
    world::stage(arm);
    arm.say_grounded(&world::monster(FAR_CREATURE, world::PLAYER_X - 7.0));
    arm.sync();
}

/// The server's order to walk to the far creature.
fn order_to_the_second_creature(arm: &mut Arm) {
    let player = world::PLAYER;
    let origin = (world::PLAYER_X - 7.0, world::PLAYER_Y, world::GROUND_HEIGHT);
    let cell = world::outdoor_cell(world::LANDBLOCK, origin.0, origin.1);
    arm.server.send(&wire::move_to_object(
        player,
        2,
        FAR_CREATURE,
        cell,
        origin,
        ORDERED_DISTANCE,
    ));
    arm.sync();
}

/// Pumps until `done`, failing after `limit`.
fn until(arm: &mut Arm, what: &str, limit: Duration, mut done: impl FnMut(&mut Arm) -> bool) {
    let deadline = Instant::now() + limit;
    while !done(arm) {
        assert!(Instant::now() < deadline, "{}: {what}", arm.name());
        arm.wait(Duration::from_millis(50));
    }
}

// OpenAC `ServerMoveToParityTests.AnOrderToWalkToACreatureIsObeyedTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn an_order_to_walk_to_a_creature_is_obeyed_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_with_a_far_creature(arm);
        let _ = arm.outbound();
        let started = distance(arm, FAR_CREATURE);

        transcript.step("the order arrives");
        order_to_the_second_creature(arm);
        arm.wait(Duration::from_millis(200));
        let under_way = moving(arm);
        transcript.record("moving", under_way);
        assert!(under_way, "{} did not obey the order", arm.name());

        transcript.step("the rest of the way");
        until(arm, "the walk never ended", Duration::from_secs(10), |a| {
            !moving(a)
        });
        let ended = distance(arm, FAR_CREATURE);
        let closed = ended < started - 1.0 && ended <= f64::from(ORDERED_DISTANCE) + 1.0;
        transcript.record("closedTheGap", closed);
        assert!(closed, "{}: {started:.2} m to {ended:.2} m", arm.name());
    });
}

// OpenAC `ServerMoveToParityTests.APluginTakingTheCharacterBackEndsTheOrderTheSameWay`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn a_plugin_taking_the_character_back_ends_the_order_the_same_way() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_with_a_far_creature(arm);
        transcript.step("the order arrives");
        order_to_the_second_creature(arm);
        arm.wait(Duration::from_millis(200));
        assert!(moving(arm), "{}", arm.name());

        transcript.step("the plugin holds a key");
        let held = arm.call::<calls::NavigationSetIntent>(&MovementIntent {
            backward: true,
            ..MovementIntent::default()
        });
        transcript.record("plugin.status", held);
        arm.wait(Duration::from_millis(300));
        let before = distance(arm, FAR_CREATURE);
        arm.wait(Duration::from_millis(300));
        let after = distance(arm, FAR_CREATURE);
        // The character is the player's again: it backs away from where the
        // order was taking it.
        let backing = after > before;
        transcript.record("backingAway", backing);
        assert!(backing, "{}: {before:.2} then {after:.2}", arm.name());

        transcript.step("let go");
        transcript.record(
            "plugin.status",
            arm.call::<calls::NavigationClearIntent>(&()),
        );
        transcript.record(
            "plugin.stop",
            arm.call::<calls::NavigationStopMoving>(&None),
        );
        until(arm, "never stood still", Duration::from_secs(5), |a| {
            !moving(a) && !moving(a)
        });
        // The order is not taken up again once the key is let go.
        let rest = distance(arm, FAR_CREATURE);
        arm.wait(Duration::from_millis(400));
        let now = distance(arm, FAR_CREATURE);
        let still = (now - rest).abs() < 0.05;
        transcript.record("staysPut", still);
        assert!(still, "{}: {rest:.3} then {now:.3}", arm.name());
    });
}

// OpenAC `ServerMoveToParityTests.ASwingFromOutOfRangeWalksInAndResolvesTheSameWay`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn a_swing_from_out_of_range_walks_in_and_resolves_the_same_way() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_with_a_far_creature(arm);
        let _ = arm.call::<calls::CombatEnterMode>(&CombatMode::Melee);
        arm.sync();
        let _ = arm.outbound();

        transcript.step("swing at something seven metres off");
        let begun = arm.call::<calls::CombatBeginAttack>(&AttackRequest {
            target_id: FAR_CREATURE,
            height: AttackHeight::Medium,
            power: 0.75,
        });
        transcript.record("begin.status", begun.status);
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        transcript.record("release.status", released.status);
        assert_eq!(begun.status, CombatStatus::Started, "{}", arm.name());

        transcript.step("the server sends the character in");
        order_to_the_second_creature(arm);

        transcript.step("it arrives");
        until(arm, "never got into range", Duration::from_secs(10), |a| {
            distance(a, FAR_CREATURE) <= f64::from(ORDERED_DISTANCE) + 1.0 && !moving(a)
        });
        transcript.record("inRange", true);

        transcript.step("the swing lands");
        arm.server.game_event(
            world::PLAYER,
            0x01C0,
            &wire::update_health(FAR_CREATURE, 0.4),
        );
        arm.sync();
        let target = arm
            .call::<calls::CombatHostileTargets>(&25.0)
            .into_iter()
            .find(|t| t.object_id == FAR_CREATURE);
        let health = target.map(|t| (t.is_health_known, t.health_fraction));
        transcript.record("health", health);
        transcript.record("snapshot.mode", arm.call::<calls::CombatSnapshot>(&()).mode);
        assert_eq!(health, Some((true, 0.4)), "{}", arm.name());
    });
}

fn record_item(transcript: &mut Transcript, key: &str, result: &ItemResult) {
    transcript.record(&format!("{key}.status"), result.status);
    transcript.record(&format!("{key}.notice"), &result.notice);
}

fn record_loot(transcript: &mut Transcript, arm: &mut Arm) -> u32 {
    let loot = arm.call::<calls::LootState>(&());
    transcript.record("available", loot.is_available);
    transcript.record("busy", loot.is_busy);
    transcript.record_id("requested", loot.requested_container_id);
    transcript.record_id("current", loot.current_container_id);
    transcript.record("ready", loot.current_contents_ready);
    loot.requested_container_id
}

/// A character with a body and a full pack, and a corpse three metres out
/// with its contents, nothing sent yet (OpenAC `WalkThenUse.Stage`).
fn stage_corpse_out_of_reach(arm: &mut Arm) {
    world::stage(arm);
    world::stage_carried_items(arm);
    world::stage_corpse(arm, -3.0);
    world::stage_corpse_contents(arm);
    let _ = arm.outbound();
}

fn uses(out: &[crate::host_parity::server::Outbound]) -> usize {
    sent(out, USE_ACTION).len()
}

/// The server answers the use: done, nothing wrong.
fn use_done(arm: &mut Arm) {
    arm.server
        .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
    arm.sync();
}

// A plugin's use of something out of reach goes to the server at once, and
// the server walks the character there (OpenAC walks it there first:
// `WalkThenUseParityTests.AskingToUseACorpseOutOfReachWalksThereOnBothClients`
// and `ArrivingSendsTheArmedUseOnBothClients`).
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn using_a_corpse_out_of_reach_goes_to_the_server_at_once_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_corpse_out_of_reach(arm);

        transcript.step("ask to use a corpse three metres off");
        let used = arm.call::<calls::ItemsUse>(&world::CORPSE);
        record_item(transcript, "use", &used);
        let out = arm.outbound();
        transcript.record("useSent", uses(&out));
        // The use goes out now and the client walks nowhere of its own.
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert_eq!(uses(&out), 1, "{}: {out:?}", arm.name());
        arm.wait(Duration::from_millis(200));
        let walking = moving(arm);
        transcript.record("walking", walking);
        assert!(!walking, "{}", arm.name());
        // The client waits on this corpse.
        let requested = record_loot(transcript, arm);
        assert_eq!(requested, world::CORPSE);

        transcript.step("the corpse answers");
        use_done(arm);
        world::deliver_corpse_contents(arm);
        arm.sync();
        record_loot(transcript, arm);
        transcript.record("busy", arm.call::<calls::ItemsState>(&()).is_busy);
    });
}

// Opening a corpse out of reach is the same use (OpenAC
// `WalkThenUseParityTests.OpeningACorpseOutOfReachWalksThereOnBothClients`).
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn opening_a_corpse_out_of_reach_goes_to_the_server_at_once_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_corpse_out_of_reach(arm);

        transcript.step("ask to open a corpse three metres off");
        let opened = arm.call::<calls::LootOpen>(&world::CORPSE);
        record_item(transcript, "open", &opened);
        let out = arm.outbound();
        transcript.record("useSent", uses(&out));
        let requested = record_loot(transcript, arm);
        assert_eq!(uses(&out), 1, "{}: {out:?}", arm.name());
        assert_eq!(requested, world::CORPSE);

        transcript.step("the corpse answers");
        use_done(arm);
        world::deliver_corpse_contents(arm);
        arm.sync();
        record_loot(transcript, arm);
    });
}

// A corpse no walk of the client's could reach (on a ledge) is used all
// the same: the use goes to the server, which decides whether the
// character can get there. OpenAC walks, stalls and gives the use up
// unsent (`WalkThenUseParityTests.AWalkThatNeverArrivesIsGivenUpOnOnBothClients`).
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn a_corpse_no_walk_could_reach_is_used_through_the_server_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_corpse_out_of_reach(arm);
        world::stage_corpse_out_of_every_reach(arm, -3.0);
        let _ = arm.outbound();

        transcript.step("ask to use the corpse on the ledge");
        let used = arm.call::<calls::ItemsUse>(&world::CORPSE_OUT_OF_EVERY_REACH);
        record_item(transcript, "use", &used);
        let out = arm.outbound();
        transcript.record("useSent", uses(&out));
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());
        assert_eq!(uses(&out), 1, "{}: {out:?}", arm.name());
        arm.wait(Duration::from_millis(200));
        assert!(!moving(arm), "{}", arm.name());

        transcript.step("the server answers and the next request is let through");
        use_done(arm);
        arm.wait(Duration::from_millis(700));
        let again = arm.call::<calls::ItemsUse>(&world::CORPSE);
        record_item(transcript, "use", &again);
        assert_eq!(again.status, ItemStatus::Started, "{}", arm.name());
    });
}

// OpenAC `WalkThenUseParityTests.ApplyingAnOwnedItemToAWorldObjectRunsTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn applying_an_owned_item_to_a_world_object_runs_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_corpse_out_of_reach(arm);

        transcript.step("apply the stone to the corpse");
        let applied = arm.call::<calls::ItemsApply>(&(world::TARGETED_ITEM, world::CORPSE));
        record_item(transcript, "apply", &applied);
        transcript.record("busy", arm.call::<calls::ItemsState>(&()).is_busy);

        transcript.step("a few frames on");
        arm.wait(Duration::from_secs(4));
        let _ = arm.outbound();
        transcript.record("busy", arm.call::<calls::ItemsState>(&()).is_busy);

        transcript.step("the server answers");
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        arm.sync();
        let state = arm.call::<calls::ItemsState>(&());
        transcript.record("busy", state.is_busy);
        transcript.record(
            "completion.error",
            arm.call::<calls::LootState>(&())
                .last_item_use_completion
                .weenie_error,
        );
    });
}
