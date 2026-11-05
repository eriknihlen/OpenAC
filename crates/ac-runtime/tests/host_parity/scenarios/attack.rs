//! A plugin naming a creature and swinging at it (OpenAC
//! `Scenarios/AttackParityTests.cs`): what the plugin is answered and the
//! swing that leaves the client, with the creature, the height and the
//! power it asked for.
//!
//! The power bar fills on the session's own clock, so "the frames until
//! the swing leaves" are real time here: the arm waits as long as a full
//! bar takes. What is recorded of the snapshot is what does not depend on
//! how far a bar got in real time.

use std::time::Duration;

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::{
    AttackHeight, AttackRequest, CombatMode, CombatResult, CombatSnapshot, CombatStatus,
};

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::scenarios::item::sent;
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::world;

/// The client action that carries a melee swing.
const MELEE_ATTACK_ACTION: u32 = 0x0008;
/// Long enough for a bar let go of early to finish loading and fire (a
/// bar fills in a second).
const FULL_POWER_BAR: Duration = Duration::from_millis(1_300);

/// A stance is taken by the character's body (OpenAC's harness always lays
/// one down), which needs the retail dats here.
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

fn record_snapshot(transcript: &mut Transcript, snapshot: &CombatSnapshot) {
    transcript.record_id("snapshot.selected", snapshot.selected_id);
    transcript.record("snapshot.mode", snapshot.mode);
    transcript.record("snapshot.height", snapshot.attack_height);
    transcript.record("snapshot.desiredPower", snapshot.desired_power);
    transcript.record("snapshot.building", snapshot.build_in_progress);
}

fn snapshot(arm: &mut Arm) -> CombatSnapshot {
    arm.call::<calls::CombatSnapshot>(&())
}

fn begin(arm: &mut Arm, target: u32, height: AttackHeight, power: f32) -> CombatResult {
    arm.call::<calls::CombatBeginAttack>(&AttackRequest {
        target_id: target,
        height,
        power,
    })
}

fn enter_melee(arm: &mut Arm) -> CombatResult {
    let entered = arm.call::<calls::CombatEnterMode>(&CombatMode::Melee);
    arm.sync();
    entered
}

/// The swings that left the arm while the bar finished, and everything
/// else sent with them.
fn until_the_swing_leaves(arm: &mut Arm) -> Vec<Outbound> {
    arm.wait(FULL_POWER_BAR);
    arm.outbound()
}

/// That one swing left, at the creature, the height and the power asked.
fn assert_the_swing(arm: &Arm, out: &[Outbound], target: u32, height: AttackHeight, power: f32) {
    let swings = sent(out, MELEE_ATTACK_ACTION);
    assert_eq!(swings.len(), 1, "{}: {out:?}", arm.name());
    let swing = &swings[0];
    assert_eq!(
        swing.u32_at(12),
        target,
        "the {} client swung at 0x{:08X}",
        arm.name(),
        swing.u32_at(12)
    );
    // The wire's heights: high 1, medium 2, low 3.
    let wire_height = match height {
        AttackHeight::High => 1,
        AttackHeight::Medium => 2,
        AttackHeight::Low => 3,
    };
    assert_eq!(swing.u32_at(16), wire_height, "{}", arm.name());
    assert!((swing.f32_at(20) - power).abs() < 0.001, "{}", arm.name());
}

// OpenAC `AttackParityTests.NamingATargetWithNothingSelectedSwingsAtIt`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn naming_a_target_with_nothing_selected_swings_at_it() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);

        transcript.step("enter melee");
        let entered = enter_melee(arm);
        record(transcript, &entered);
        arm.record_outbound(transcript);

        transcript.step("begin");
        let begun = begin(arm, world::MONSTER, AttackHeight::Medium, 0.5);
        record(transcript, &begun);
        let now = snapshot(arm);
        record_snapshot(transcript, &now);
        assert_eq!(begun.status, CombatStatus::Started, "{}", arm.name());

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);

        transcript.step("the swing goes out");
        let out = until_the_swing_leaves(arm);
        assert_the_swing(arm, &out, world::MONSTER, AttackHeight::Medium, 0.5);
        transcript.record("sent", &out);
    });
}

// OpenAC `AttackParityTests.NamingATargetWhileAnotherIsSelectedSwingsAtTheNamedOne`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn naming_a_target_while_another_is_selected_swings_at_the_named_one() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let _ = enter_melee(arm);
        let _ = arm.call::<calls::SelectionSelect>(&world::SECOND_MONSTER);
        let _ = arm.outbound();

        transcript.step("begin on the other creature");
        let begun = begin(arm, world::MONSTER, AttackHeight::High, 1.0);
        record(transcript, &begun);
        transcript.record("selected", arm.call::<calls::SelectionState>(&()).selected);

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);
        let out = until_the_swing_leaves(arm);
        let selected = arm.call::<calls::SelectionState>(&()).selected;
        transcript.record("selected", selected);
        // The swing names the creature the plugin named, not the one that
        // happened to be selected.
        assert_the_swing(arm, &out, world::MONSTER, AttackHeight::High, 1.0);
        assert_eq!(selected, Some(world::MONSTER), "{}", arm.name());
        transcript.record("sent", &out);
    });
}

/// Naming one creature and swinging at it: what both answer, and which
/// creature a swing really goes out at (none when none leaves).
fn target_scenario(target: u32, expected: CombatStatus, swings_at: Option<u32>) {
    run_with(&with_a_body(), &[], true, move |arm, transcript| {
        world::stage(arm);
        let _ = enter_melee(arm);
        let _ = arm.outbound();

        transcript.step("begin");
        let begun = begin(arm, target, AttackHeight::Medium, 0.5);
        record(transcript, &begun);
        transcript.record("selected", arm.call::<calls::SelectionState>(&()).selected);
        let now = snapshot(arm);
        record_snapshot(transcript, &now);
        assert_eq!(begun.status, expected, "{}", arm.name());
        assert_eq!(now.build_in_progress, swings_at.is_some(), "{}", arm.name());

        transcript.step("release");
        let released = arm.call::<calls::CombatReleaseAttack>(&());
        record(transcript, &released);
        let out = until_the_swing_leaves(arm);
        match swings_at {
            Some(swung) => assert_the_swing(arm, &out, swung, AttackHeight::Medium, 0.5),
            None => assert!(
                sent(&out, MELEE_ATTACK_ACTION).is_empty(),
                "{}: {out:?}",
                arm.name()
            ),
        }
        transcript.record("sent", &out);
    });
}

// OpenAC `AttackParityTests.ADeadTargetIsTakenTheSameWayOnBothClients`:
// neither client turns the swing away, and the swing goes out at the live
// creature standing next to the dead one.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn a_dead_target_is_taken_the_same_way_on_both_clients() {
    target_scenario(
        world::DEAD_MONSTER,
        CombatStatus::Started,
        Some(world::MONSTER),
    );
}

// OpenAC `AttackParityTests.AHiddenTargetIsTakenTheSameWayOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn a_hidden_target_is_taken_the_same_way_on_both_clients() {
    target_scenario(
        world::HIDDEN_MONSTER,
        CombatStatus::Started,
        Some(world::MONSTER),
    );
}

// OpenAC `AttackParityTests.ACreatureThatIsNotHostileIsRefusedTheSameWay`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn a_creature_that_is_not_hostile_is_refused_the_same_way() {
    target_scenario(world::BYSTANDER, CombatStatus::InvalidTarget, None);
}

// OpenAC `AttackParityTests.AnUnknownGuidIsRefusedTheSameWay`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn an_unknown_guid_is_refused_the_same_way() {
    target_scenario(0x5000_BEEF, CombatStatus::InvalidTarget, None);
}

// OpenAC `AttackParityTests.OutOfCombatModeTheStanceIsWhatRefuses`.
#[test]
#[ignore = "needs the retail dats: a body that takes a stance"]
fn out_of_combat_mode_the_stance_is_what_refuses() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let _ = arm.outbound();
        transcript.step("begin without a stance");
        let begun = begin(arm, world::MONSTER, AttackHeight::Medium, 0.5);
        record(transcript, &begun);
        let now = snapshot(arm);
        record_snapshot(transcript, &now);
        let out = until_the_swing_leaves(arm);
        assert_eq!(begun.status, CombatStatus::WrongMode, "{}", arm.name());
        assert_eq!(now.mode, CombatMode::Peace);
        assert_eq!(arm.call::<calls::SelectionState>(&()).selected, None);
        assert!(sent(&out, MELEE_ATTACK_ACTION).is_empty());
        transcript.record("sent", &out);
    });
}
