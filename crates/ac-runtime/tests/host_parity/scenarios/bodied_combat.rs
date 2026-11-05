//! What a bot does once its character stands in the world (OpenAC
//! `Scenarios/BodiedCombatParityTests.cs`): build a swing and let it go,
//! kill what it swung at and move straight on, and swing while running.
//!
//! The arms run on the session's own clock in real time, so the bar's
//! climb is held to what does not depend on how many milliseconds a read
//! took: it starts empty, it climbs, and the swing that leaves carries the
//! power asked for. OpenAC also reads the host's own readiness answers
//! (`PlayerReadyForAttack`, `IsDualWield`), which a plugin here does not
//! see; the stance they stand for shows in the swing being taken at all.

use std::time::Duration;

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::{
    AttackHeight, AttackRequest, CombatMode, CombatResult, CombatSnapshot, CombatStatus,
};
use ac_plugin::abi::navigation::{MovementIntent, NavigationStatus};

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::scenarios::item::sent;
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

const MELEE_ATTACK_ACTION: u32 = 0x0008;
/// The client action that says where the character is going.
const MOVE_TO_STATE_ACTION: u32 = 0xF61C;
/// The power the plugin asks the bar to be loaded to.
const HELD_POWER: f32 = 0.85;
/// Long enough for a bar let go of early to finish loading and fire.
const FULL_POWER_BAR: Duration = Duration::from_millis(1_300);

fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

fn record(transcript: &mut Transcript, result: &CombatResult) {
    transcript.record("status", result.status);
    transcript.record("notice", &result.notice);
}

fn record_snapshot(transcript: &mut Transcript, s: &CombatSnapshot) {
    transcript.record_id("snapshot.selected", s.selected_id);
    transcript.record("snapshot.mode", s.mode);
    transcript.record("snapshot.height", s.attack_height);
    transcript.record("snapshot.desiredPower", s.desired_power);
    transcript.record("snapshot.building", s.build_in_progress);
}

fn begin(arm: &mut Arm, target: u32, height: AttackHeight, power: f32) -> CombatResult {
    arm.call::<calls::CombatBeginAttack>(&AttackRequest {
        target_id: target,
        height,
        power,
    })
}

fn wire_height(height: AttackHeight) -> u32 {
    match height {
        AttackHeight::High => 1,
        AttackHeight::Medium => 2,
        AttackHeight::Low => 3,
    }
}

fn assert_the_swing(arm: &Arm, out: &[Outbound], target: u32, height: AttackHeight, power: f32) {
    let swings = sent(out, MELEE_ATTACK_ACTION);
    assert_eq!(swings.len(), 1, "{}: {out:?}", arm.name());
    let swing = &swings[0];
    assert_eq!(swing.u32_at(12), target, "{}", arm.name());
    assert_eq!(swing.u32_at(16), wire_height(height), "{}", arm.name());
    assert!(
        (swing.f32_at(20) - power).abs() < 0.001,
        "the {} client swung at {} power",
        arm.name(),
        swing.f32_at(20)
    );
}

fn record_targets(transcript: &mut Transcript, arm: &mut Arm) {
    let targets = arm.call::<calls::CombatHostileTargets>(&25.0);
    transcript.record("targets.count", targets.len());
    for (index, target) in targets.iter().enumerate() {
        transcript.record_id(&format!("target[{index}].id"), target.object_id);
        transcript.record(&format!("target[{index}].dead"), target.is_dead);
    }
}

// OpenAC `BodiedCombatParityTests.HoldingAndReleasingASwingRunsTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn holding_and_releasing_a_swing_runs_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);

        transcript.step("enter melee");
        let stance = arm.call::<calls::CombatEnterMode>(&CombatMode::Melee);
        record(transcript, &stance);
        assert_eq!(
            stance.status,
            CombatStatus::ModeChangeSent,
            "{}",
            arm.name()
        );
        arm.sync();
        arm.record_outbound(transcript);

        transcript.step("begin");
        let begun = begin(arm, world::MONSTER, AttackHeight::High, HELD_POWER);
        record(transcript, &begun);
        let now = arm.call::<calls::CombatSnapshot>(&());
        record_snapshot(transcript, &now);
        // The client took the creature, the height and the power the
        // plugin named, and has started loading the bar.
        assert_eq!(begun.status, CombatStatus::Started, "{}", arm.name());
        assert_eq!(now.selected_id, world::MONSTER);
        assert_eq!(now.mode, CombatMode::Melee);
        assert_eq!(now.attack_height, AttackHeight::High);
        assert!((now.desired_power - HELD_POWER).abs() < 0.001);
        assert!(now.build_in_progress);

        transcript.step("hold");
        // The bar fills in a second on the session's clock: it climbs.
        let first = arm.call::<calls::CombatSnapshot>(&()).power_bar_level;
        arm.wait(Duration::from_millis(120));
        let second = arm.call::<calls::CombatSnapshot>(&()).power_bar_level;
        let climbing = second > first && second < HELD_POWER;
        transcript.record("barClimbing", climbing);
        assert!(climbing, "{}: {first} then {second}", arm.name());

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);

        transcript.step("the swing goes out");
        arm.wait(FULL_POWER_BAR);
        let out = arm.outbound();
        assert_the_swing(arm, &out, world::MONSTER, AttackHeight::High, HELD_POWER);
        transcript.record("swings", sent(&out, MELEE_ATTACK_ACTION));
    });
}

// OpenAC `BodiedCombatParityTests.AKillBurstMovesOnToTheNextCreatureTheSameWay`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn a_kill_burst_moves_on_to_the_next_creature_the_same_way() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let _ = arm.call::<calls::CombatEnterMode>(&CombatMode::Melee);
        arm.sync();
        let _ = arm.outbound();

        transcript.step("begin on the first creature");
        let begun = begin(arm, world::MONSTER, AttackHeight::Medium, 0.5);
        record(transcript, &begun);
        record_targets(transcript, arm);

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);
        arm.wait(FULL_POWER_BAR);
        let out = arm.outbound();
        transcript.record("swings", sent(&out, MELEE_ATTACK_ACTION));

        // The kill arrives as the server sends it: a health update and a
        // line of text, through the client's own inbound route.
        transcript.step("it dies");
        let chat_before = arm.chat.len();
        arm.server.game_event(
            world::PLAYER,
            0x01C0,
            &wire::update_health(world::MONSTER, 0.0),
        );
        arm.server
            .send(&wire::server_message("You have slain the creature!", 0x00));
        arm.sync();
        let selected = arm.call::<calls::SelectionState>(&()).selected;
        transcript.record("selected", selected);
        let dead = arm
            .call::<calls::CombatHostileTargets>(&25.0)
            .into_iter()
            .find(|t| t.object_id == world::MONSTER);
        transcript.record("dead", dead.as_ref().map(|t| t.is_dead));
        let health = dead
            .as_ref()
            .map(|t| (t.is_health_known, t.health_fraction));
        transcript.record("health", health);
        let heard = arm.chat[chat_before..]
            .iter()
            .any(|c| c.text.contains("You have slain the creature!"));
        transcript.record("heard", heard);
        // Said outright: two clients that heard nothing would agree.
        assert!(heard, "{}", arm.name());
        if let Some((known, fraction)) = health {
            assert!(known && fraction == 0.0, "{}: {health:?}", arm.name());
        }
        record_targets(transcript, arm);

        transcript.step("begin on the next creature");
        let next = begin(arm, world::SECOND_MONSTER, AttackHeight::Low, 1.0);
        record(transcript, &next);
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);
        assert_eq!(next.status, CombatStatus::Started, "{}", arm.name());
    });
}

// OpenAC `BodiedCombatParityTests.AskingForASwingWhileRunningStopsTheSameWayOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that runs"]
fn asking_for_a_swing_while_running_stops_the_same_way_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let _ = arm.call::<calls::CombatEnterMode>(&CombatMode::Melee);
        arm.sync();
        // The plugin holds run forward for a fifth of a second.
        let held = arm.call::<calls::NavigationSetIntent>(&MovementIntent {
            forward: true,
            run: true,
            ..MovementIntent::default()
        });
        assert_eq!(held, NavigationStatus::Accepted, "{}", arm.name());
        arm.wait(Duration::from_millis(200));
        let _ = arm.outbound();

        transcript.step("begin while running");
        let begun = begin(arm, world::MONSTER, AttackHeight::Medium, 0.5);
        record(transcript, &begun);
        let out = arm.outbound();
        // Before it asks the server for a swing the client stops the
        // character, and tells the server so.
        assert_eq!(begun.status, CombatStatus::Started, "{}", arm.name());
        let stops = sent(&out, MOVE_TO_STATE_ACTION).len();
        transcript.record("toldTheServerItStopped", stops > 0);
        assert!(stops > 0, "{}: {out:?}", arm.name());

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);
        arm.wait(FULL_POWER_BAR);
        let out = arm.outbound();
        // And the swing still goes out, at the named creature.
        assert_the_swing(arm, &out, world::MONSTER, AttackHeight::Medium, 0.5);
        let _ = arm.call::<calls::NavigationClearIntent>(&());
    });
}
