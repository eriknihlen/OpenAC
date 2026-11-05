//! Another creature's body between the server's updates, on both arms
//! (OpenAC `Scenarios/RemoteBodyParityTests.cs`): a creature the server
//! says is walking is carried on between its sparse words, a far snap and
//! a teleport put the body where the server said, and a plugin is told
//! where the body is.
//!
//! OpenAC drives a remote body by hand at a fixed step on two arms and
//! compares positions exactly. Here the creature is one the server
//! creates and sets walking, and the session carries it on its own clock
//! (real time), so each arm is held to the effect outright and the
//! transcripts record it. Not ported: `ACreatureThatTurnsIsToldToAPlugin
//! FacingItsNewWayOnBoth` (OpenAC turns the body by hand; a server word
//! with a new facing is a placement, which the snap case covers) and
//! `ACreatureBeyondTheBubbleIsNotCarriedOnEither` (OpenAC moves the
//! character 500 m off by hand; here the character is where the server
//! put it, and a landblock that far away is not loaded).

use std::time::Duration;

use ac_plugin::abi::calls;
use ac_plugin::abi::world::Position;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::wire::{self, Object};
use crate::host_parity::{dats, world};

/// A creature with a body of its own, ten metres west of the character.
const WALKER: u32 = 0x5000_0070;
/// Walk forward (a motion command's low 16 bits).
const WALK_FORWARD: u16 = 0x0005;

fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

fn start_x() -> f32 {
    world::PLAYER_X - 10.0
}

/// The walker arrives: a human body standing on the ground, facing north.
fn walker_arrives(arm: &mut Arm) {
    let walker = Object {
        setup: Some(dats::HUMAN_SETUP),
        motion_table: Some(dats::HUMAN_MOTION),
        physics_state: dats::BODY_STATE,
        ..world::monster(WALKER, start_x())
    };
    arm.say_grounded(&walker);
    arm.sync();
}

fn walker(arm: &mut Arm) -> Position {
    arm.call::<calls::NavigationObject>(&WALKER)
        .unwrap_or_else(|| panic!("{} does not know the walker", arm.name()))
        .position
}

/// The server's word about where the walker is (`teleport` counts its
/// moves outright).
fn server_says_it_is_at(arm: &mut Arm, x: f32, y: f32, position_sequence: u16, teleport: u16) {
    let z = dats::ground_z(world::LANDBLOCK, x, y);
    let cell = world::outdoor_cell(world::LANDBLOCK, x, y);
    arm.server.send(&wire::update_position(
        WALKER,
        (cell, x, y, z),
        1,
        position_sequence,
        teleport,
        0,
    ));
    arm.sync();
}

fn metres(a: &Position, b: &Position) -> f64 {
    a.horizontal_distance_metres(b)
}

// OpenAC `RemoteBodyParityTests.ACreatureWalkingBetweenSparseUpdatesLandsInTheSamePlaceOnBoth`
// and `ACreatureArrivingLaterStillGetsABodyAndWalksOnBoth` (every creature
// here arrives after the character).
#[test]
#[ignore = "needs the retail dats: a creature's body and its cycles"]
fn a_walking_creature_is_carried_between_the_servers_words_on_both() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        walker_arrives(arm);
        let staged = walker(arm);

        transcript.step("the server says it walks");
        arm.server
            .send(&wire::interpreted_motion(WALKER, 2, WALK_FORWARD, 1.0));
        arm.sync();
        arm.wait(Duration::from_millis(600));
        let carried = walker(arm);
        // With no word from the server in between, the body walked on:
        // north, the way it faces, and not off sideways.
        let went_north = carried.north_south > staged.north_south;
        let walked = metres(&staged, &carried);
        transcript.record("carriedNorth", went_north);
        assert!(
            went_north && walked > 0.5,
            "{}: the creature never walked ({walked:.2} m)",
            arm.name()
        );
    });
}

// OpenAC `RemoteBodyParityTests.AFarSnapPutsTheBodyInTheSamePlaceOnBoth`.
#[test]
#[ignore = "needs the retail dats: a creature's body"]
fn a_far_snap_puts_the_body_in_the_same_place_on_both() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        walker_arrives(arm);

        transcript.step("the server says it is twenty metres away");
        let (x, y) = (start_x(), world::PLAYER_Y - 20.0);
        server_says_it_is_at(arm, x, y, 2, 0);
        arm.wait(Duration::from_millis(150));
        let there = walker(arm);
        let expected = Position::from_cell(
            world::outdoor_cell(world::LANDBLOCK, x, y),
            [x, y, 0.0],
            0.0,
        );
        let off = metres(&there, &expected);
        transcript.record("placedThere", off < 0.5);
        assert!(
            off < 0.5,
            "{}: {off:.2} m from where the server said",
            arm.name()
        );
    });
}

// OpenAC `RemoteBodyParityTests.ATeleportPutsTheBodyInTheSamePlaceOnBoth`.
#[test]
#[ignore = "needs the retail dats: a creature's body"]
fn a_teleport_puts_the_body_in_the_same_place_on_both() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        walker_arrives(arm);

        transcript.step("the server moves it outright");
        let (x, y) = (start_x(), world::PLAYER_Y + 12.0);
        server_says_it_is_at(arm, x, y, 2, 1);
        let put = walker(arm);
        let expected = Position::from_cell(
            world::outdoor_cell(world::LANDBLOCK, x, y),
            [x, y, 0.0],
            0.0,
        );
        let off = metres(&put, &expected);
        transcript.record("putThere", off < 0.01);
        assert!(off < 0.01, "{}: {off:.3} m off", arm.name());

        transcript.step("and a moment later");
        arm.wait(Duration::from_millis(150));
        let stays = metres(&walker(arm), &expected) < 1.0;
        transcript.record("staysThere", stays);
        assert!(stays, "{}", arm.name());
    });
}
