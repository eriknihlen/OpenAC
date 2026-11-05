//! The character moving the way a plugin moves it (OpenAC
//! `Scenarios/MovementParityTests.cs`, `LocalMotionParityTests.cs`,
//! `MovementStatsParityTests.cs`): holding forward, turning on the spot,
//! running round a corner, a sent movement finishing, and how far the
//! server's run skill and stamina let the character get.
//!
//! OpenAC steps both arms through the same simulated frames and compares
//! where each ended to the last digit. Here each arm runs its session in
//! real time, so how far a held key took the character depends on how long
//! the hold really lasted; the transcripts record what does not (that it
//! moved, that it turned, that it stopped, where a movement with an end
//! ended) and each arm is held to the effect outright. OpenAC's local
//! motion scenarios also bind hand-built animation cycles; these use the
//! retail ones out of the dats.

use std::time::{Duration, Instant};

use ac_plugin::abi::calls;
use ac_plugin::abi::navigation::{
    GoToPlace, GoToState, MovementIntent, NavigationSnapshot, NavigationStatus,
};
use ac_plugin::abi::world::Position;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::scenarios::item::sent;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// The client action that says where the character is going.
const MOVE_TO_STATE_ACTION: u32 = 0xF61C;
/// A quarter turn to the right.
const EAST: f32 = 90.0;

fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

fn here(arm: &mut Arm) -> NavigationSnapshot {
    arm.call::<calls::NavigationSnapshot>(&())
}

/// Metres between two places across the ground.
fn metres(a: &Position, b: &Position) -> f64 {
    a.horizontal_distance_metres(b)
}

fn heading_change(a: f32, b: f32) -> f32 {
    let d = (b - a).rem_euclid(360.0);
    d.min(360.0 - d)
}

fn hold(arm: &mut Arm, intent: MovementIntent) -> NavigationStatus {
    arm.call::<calls::NavigationSetIntent>(&intent)
}

fn forward_run() -> MovementIntent {
    MovementIntent {
        forward: true,
        run: true,
        ..MovementIntent::default()
    }
}

/// Lets go and waits until the character stands still.
fn let_go(arm: &mut Arm, transcript: &mut Transcript) {
    transcript.record(
        "plugin.status",
        arm.call::<calls::NavigationClearIntent>(&()),
    );
    transcript.record(
        "plugin.stop",
        arm.call::<calls::NavigationStopMoving>(&None),
    );
    settle(arm);
}

fn settle(arm: &mut Arm) {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        arm.wait(Duration::from_millis(100));
        if !here(arm).is_moving {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "{} never stood still",
            arm.name()
        );
    }
}

/// How far off a staged creature is, metres.
fn distance_to(arm: &mut Arm, snapshot: &NavigationSnapshot, id: u32) -> f64 {
    arm.call::<calls::NavigationObject>(&id)
        .map_or(f64::NAN, |o| metres(&snapshot.position, &o.position))
}

// OpenAC `MovementParityTests.HoldingForwardWalksTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn holding_forward_walks_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let start = here(arm);
        transcript.step("standing");
        transcript.record("available", start.is_available);
        transcript.record("moving", start.is_moving);
        let _ = arm.outbound();

        transcript.step("hold forward");
        transcript.record("plugin.status", hold(arm, forward_run()));
        arm.wait(Duration::from_millis(200));
        let moving = here(arm);
        let went = moving.position.north_south > start.position.north_south;
        transcript.record("movedNorth", went);
        transcript.record("moving", moving.is_moving);
        let out = arm.outbound();
        let told = !sent(&out, MOVE_TO_STATE_ACTION).is_empty();
        transcript.record("toldTheServer", told);
        // The plugin's own call moved it, and the client said so.
        assert!(went && moving.is_moving && told, "{}: {out:?}", arm.name());

        transcript.step("let go");
        let_go(arm, transcript);
        let stopped = here(arm);
        transcript.record("moving", stopped.is_moving);
        transcript.record("airborne", stopped.is_airborne);
    });
}

// OpenAC `MovementParityTests.TurningOnTheSpotRunsTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that turns"]
fn turning_on_the_spot_runs_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let start = here(arm);
        let _ = arm.outbound();

        transcript.step("turn to face east");
        let status = arm.call::<calls::NavigationFace>(&EAST);
        transcript.record("plugin.status", status);
        arm.wait(Duration::from_millis(1_500));
        settle(arm);
        let end = here(arm);
        transcript.record("heading", format!("{:.0}", end.position.heading_degrees));
        let monster = distance_to(arm, &end, world::MONSTER);
        let second = distance_to(arm, &end, world::SECOND_MONSTER);
        transcript.record("monster.distance", format!("{monster:.3}"));
        transcript.record("second.distance", format!("{second:.3}"));
        // The turn got there, on the spot.
        assert_eq!(status, NavigationStatus::Accepted, "{}", arm.name());
        assert!(
            heading_change(end.position.heading_degrees, EAST) < 0.5,
            "{} faces {}",
            arm.name(),
            end.position.heading_degrees
        );
        assert!(metres(&start.position, &end.position) < 0.001);
        assert!((monster - 3.0).abs() < 0.001 && (second - 7.0).abs() < 0.001);
    });
}

// OpenAC `LocalMotionParityTests.RunningAndTurningEndsInTheSamePlaceOnBothClients`.
#[test]
#[ignore = "needs the retail dats: the character's own cycles"]
fn running_and_turning_carries_the_character_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let start = here(arm);

        transcript.step("run straight");
        transcript.record("plugin.status", hold(arm, forward_run()));
        arm.wait(Duration::from_millis(650));
        let ran = here(arm);
        let went = ran.position.north_south > start.position.north_south;
        transcript.record("ranNorth", went);

        transcript.step("run round a corner");
        let corner = MovementIntent {
            turn_right: true,
            ..forward_run()
        };
        transcript.record("plugin.status", hold(arm, corner));
        arm.wait(Duration::from_millis(650));
        let turned = here(arm);
        let came_round = heading_change(
            ran.position.heading_degrees,
            turned.position.heading_degrees,
        ) > 1.0;
        transcript.record("cameRound", came_round);

        transcript.step("run on");
        transcript.record("plugin.status", hold(arm, forward_run()));
        arm.wait(Duration::from_millis(650));
        let on = here(arm);
        let kept_on = metres(&turned.position, &on.position) > 1.0;
        transcript.record("keptGoing", kept_on);

        transcript.step("let go");
        let_go(arm, transcript);
        let end = here(arm);
        transcript.record("moving", end.is_moving);
        // It really ran and really came round.
        assert!(went && came_round && kept_on, "{}", arm.name());
    });
}

// OpenAC `LocalMotionParityTests.TurningOnTheSpotTurnsTheSameAmountOnBothClients`.
#[test]
#[ignore = "needs the retail dats: the character's own cycles"]
fn turning_on_the_spot_leaves_the_spot_on_neither_client() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let start = here(arm);

        transcript.step("turn on the spot");
        let turn = MovementIntent {
            turn_right: true,
            run: true,
            ..MovementIntent::default()
        };
        transcript.record("plugin.status", hold(arm, turn));
        arm.wait(Duration::from_millis(650));

        transcript.step("stop turning");
        let_go(arm, transcript);
        let end = here(arm);
        let turned =
            heading_change(start.position.heading_degrees, end.position.heading_degrees) > 1.0;
        let stayed = metres(&start.position, &end.position) < 0.001;
        transcript.record("turned", turned);
        transcript.record("stayed", stayed);
        assert!(turned && stayed, "{}", arm.name());
    });
}

// OpenAC `LocalMotionParityTests.ASentMovementFinishesOnBothClients`: OpenAC
// sends the movements to the body itself; a plugin sends them as a turn to
// a heading and a walk to a place, which the client plans and walks.
#[test]
#[ignore = "needs the retail dats: the character's own cycles"]
fn a_sent_movement_finishes_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);

        transcript.step("face east");
        let status = arm.call::<calls::NavigationFace>(&EAST);
        transcript.record("accepted", status);
        arm.wait(Duration::from_millis(1_500));
        settle(arm);
        let faced = here(arm);
        transcript.record("heading", format!("{:.0}", faced.position.heading_degrees));
        assert!(
            heading_change(faced.position.heading_degrees, EAST) < 0.5,
            "{} never finished coming round",
            arm.name()
        );

        // South, not OpenAC's north: on the retail landblock the arms stand
        // on, the spot four metres north is up against something the walk
        // cannot stand beside (OpenAC's harness ground is bare).
        transcript.step("walk four metres south");
        let mut target = faced.position.clone();
        target.north_south -= 4.0 / 240.0;
        let status = arm.call::<calls::NavigationGoToPlace>(&GoToPlace {
            position: target.clone(),
            arrival_meters: 0.5,
        });
        transcript.record("accepted", status);
        let deadline = Instant::now() + Duration::from_secs(15);
        let report = loop {
            let report = arm.call::<calls::NavigationGoToReport>(&());
            if !matches!(
                report.state,
                GoToState::Planning | GoToState::Walking | GoToState::Waiting
            ) {
                break report;
            }
            assert!(
                Instant::now() < deadline,
                "{} never finished the walk",
                arm.name()
            );
            arm.wait(Duration::from_millis(50));
        };
        transcript.record("state", report.state);
        let end = here(arm);
        let short = metres(&end.position, &target);
        assert_eq!(report.state, GoToState::Arrived, "{}", arm.name());
        assert!(
            short < 1.0,
            "{}: the walk ended {short:.2} m short",
            arm.name()
        );
    });
}

/// The run skill, the jump skill (nothing applies until both are known)
/// and stamina, as the wire numbers them.
const RUN_SKILL: u32 = 24;
const JUMP_SKILL: u32 = 22;
const STAMINA: u32 = 4;

fn stage_runner(arm: &mut Arm, run_skill: u16) {
    world::stage(arm);
    arm.server.send(&wire::skill_update(1, JUMP_SKILL, 50, 0));
    arm.server
        .send(&wire::skill_update(2, RUN_SKILL, run_skill, 0));
    arm.server
        .send(&wire::vital_update(3, STAMINA, 0, 100, 0, 100));
    arm.sync();
}

/// Holds forward for half a second: how far north the character got, then
/// it lets go and settles.
fn run_half_a_second(arm: &mut Arm) -> f64 {
    let from = here(arm).position;
    let _ = hold(arm, forward_run());
    arm.wait(Duration::from_millis(500));
    let to = here(arm).position;
    let _ = arm.call::<calls::NavigationClearIntent>(&());
    let _ = arm.call::<calls::NavigationStopMoving>(&None);
    settle(arm);
    metres(&from, &to)
}

// OpenAC `MovementStatsParityTests.TheServersRunSkillChangesHowFarTheCharacterGetsOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that runs"]
fn the_servers_run_skill_changes_how_far_the_character_gets_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_runner(arm, 10);
        transcript.step("a poor runner covers this much ground");
        let plodded = run_half_a_second(arm);

        transcript.step("the server says the character got better at it");
        arm.server.send(&wire::skill_update(4, RUN_SKILL, 300, 0));
        arm.sync();

        transcript.step("and now covers more");
        let ran = run_half_a_second(arm);
        // Half a second is real time here: the comparison is the point.
        transcript.record("ranFarther", ran > plodded);
        assert!(
            ran > plodded,
            "{}: the better runner covered {ran:.3} m against {plodded:.3}",
            arm.name()
        );
    });
}

// OpenAC `MovementStatsParityTests.RunningOutOfStaminaSlowsTheCharacterOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that runs"]
fn running_out_of_stamina_slows_the_character_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        stage_runner(arm, 300);
        transcript.step("with stamina in hand");
        let ran = run_half_a_second(arm);

        transcript.step("the server drains the last of it");
        arm.server
            .send(&wire::vital_update(4, STAMINA, 0, 100, 0, 0));
        arm.sync();

        transcript.step("and the character labours");
        let laboured = run_half_a_second(arm);
        transcript.record("slower", laboured < ran);
        assert!(
            laboured < ran,
            "{}: out of stamina it covered {laboured:.3} m against {ran:.3}",
            arm.name()
        );
    });
}
