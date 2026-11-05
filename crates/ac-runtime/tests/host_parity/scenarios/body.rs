//! What a plugin asks of the character's body and the collision world
//! around it, on both arms (OpenAC `Scenarios/NavigationEventParityTests.cs`,
//! `ProjectilePathParityTests.cs`, `RoomAheadParityTests.cs`): a walk
//! reported back, whether a shot gets there, and whether there is room
//! ahead. Each answer comes from the session's own physics world, which a
//! body and the ground need the retail dats for (OpenAC's harness always
//! lays a body and flat ground down), so these run with them.
//!
//! OpenAC fires walk reports into the event sink by hand, because its
//! harness has no world to walk in; here the arms have one, so the reports
//! come from a real walk the plugin asks for, which is the half OpenAC's
//! file says it cannot reach. Its throwing-handler case is a plugin trapped
//! inside the event here: the trapped plugin is unloaded, and another
//! plugin still hears the walk.

use std::time::{Duration, Instant};

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::AttackHeight;
use ac_plugin::abi::navigation::{GoToReport, GoToState, NavigationStatus, RoomAheadStatus};
use ac_plugin::abi::projectile::{
    ProjectilePathKind, ProjectilePathQuery, ProjectilePathResult, ProjectilePathStatus,
};
use ac_plugin::abi::{Event, Wire};

use crate::host_parity::arm::{Arm, PARITY, Setup, run_with};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::Object;
use crate::host_parity::{dats, world};

fn with_a_body() -> Setup {
    Setup {
        dats: true,
        ..Setup::default()
    }
}

/// A second copy of the relay, which keeps hearing when the first traps.
const LISTENER: &str = "openac.paritylistener";

/// The walk reports a copy of the relay heard, in order.
fn walk_reports(arm: &Arm, plugin: &str) -> Vec<GoToReport> {
    arm.logs
        .iter()
        .filter(|(p, _)| p == plugin)
        .filter_map(|(_, m)| m.strip_prefix("parity event "))
        .filter_map(
            |hex| match Event::from_bytes(&crate::host_parity::arm::unhex(hex)) {
                Ok(Event::NavigationChanged(report)) => Some(report),
                _ => None,
            },
        )
        .collect()
}

fn walk_until_done(arm: &mut Arm, plugin: &str) -> Vec<GoToReport> {
    let deadline = Instant::now() + Duration::from_secs(20);
    loop {
        let reports = walk_reports(arm, plugin);
        if reports.iter().any(|r| r.state == GoToState::Arrived) {
            return reports;
        }
        assert!(
            Instant::now() < deadline,
            "{}: the walk never arrived: {reports:?}",
            arm.name()
        );
        arm.wait(Duration::from_millis(20));
    }
}

fn record_states(transcript: &mut Transcript, reports: &[GoToReport]) {
    let mut states: Vec<GoToState> = reports.iter().map(|r| r.state).collect();
    // How many frames a walk takes is real time; what a plugin decides on is
    // the order of the states and the report it ends with.
    states.dedup();
    transcript.record("states", &states);
}

// OpenAC `NavigationEventParityTests.AWalkIsReportedToAPluginOnBothClients`.
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn a_walk_is_reported_to_a_plugin_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        transcript.step("the walk starts");
        let status = arm.call::<calls::NavigationGoTo>(&(world::MONSTER, 1.0));
        transcript.record("status", status);
        assert_eq!(status, NavigationStatus::Accepted, "{}", arm.name());

        transcript.step("and the character arrives");
        let reports = walk_until_done(arm, PARITY);
        record_states(transcript, &reports);
        let arrived = reports.last().unwrap();
        // The whole report survives the crossing: a bot decides on the
        // object and who asked as well (the asker is named while the walk
        // is under way, and no one once it has ended).
        assert_eq!(arrived.state, GoToState::Arrived);
        assert_eq!(arrived.object_id, world::MONSTER);
        assert_eq!(arrived.owner, None, "{}", arm.name());
        let walking = reports
            .iter()
            .find(|r| r.state == GoToState::Walking)
            .unwrap_or_else(|| panic!("{}: {reports:?}", arm.name()));
        assert_eq!(walking.owner.as_deref(), Some(PARITY), "{}", arm.name());
        assert!(arrived.revision > walking.revision);
        transcript.record_id("object", arrived.object_id);
        transcript.record("walkingOwner", &walking.owner);
    });
}

// OpenAC `NavigationEventParityTests.AThrowingPluginCostsNoOtherPluginTheNewsOnBothClients`
// (OpenAC also unsubscribes a handler; a plugin here hears every event it
// is sent, so that half has no counterpart).
#[test]
#[ignore = "needs the retail dats: a body that walks"]
fn a_trapping_plugin_costs_no_other_plugin_the_news_on_both_clients() {
    let setup = Setup {
        more_plugins: vec![LISTENER],
        ..with_a_body()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        world::stage(arm);
        transcript.step("one plugin traps on the walk's news");
        // Event tag 17: the walk changed. The relay traps before it logs.
        arm.type_line("/paritylistener trap-on 17");
        arm.sync();
        let status = arm.call::<calls::NavigationGoTo>(&(world::MONSTER, 1.0));
        assert_eq!(status, NavigationStatus::Accepted, "{}", arm.name());

        transcript.step("the other still hears the walk to its end");
        let reports = walk_until_done(arm, PARITY);
        record_states(transcript, &reports);
        assert_eq!(reports.last().unwrap().state, GoToState::Arrived);
        let heard_by_the_trapped = walk_reports(arm, LISTENER).len();
        transcript.record("heardByTheTrapped", heard_by_the_trapped);
        assert_eq!(heard_by_the_trapped, 0, "{}", arm.name());
    });
}

fn ask(arm: &mut Arm, target: u32, kind: ProjectilePathKind) -> ProjectilePathResult {
    arm.call::<calls::ProjectilesEvaluatePath>(&ProjectilePathQuery {
        target_id: target,
        kind,
        target_height: AttackHeight::Medium,
        projectile_radius: 0.4,
        step_distance: 0.7,
        maximum_collision_checks: 500,
        diagnostics: false,
    })
}

fn record_shot(transcript: &mut Transcript, result: &ProjectilePathResult) {
    transcript.record("status", result.status);
    transcript.record("checks", result.collision_checks);
    transcript.record_id("stopped by", result.blocking_object_id);
}

const BURIED: u32 = 0x5000_0060;

// OpenAC `ProjectilePathParityTests.AskingWhetherAShotGetsThereIsAnsweredTheSameOnBothClients`.
#[test]
#[ignore = "needs the retail dats: the ground a shot can hit"]
fn asking_whether_a_shot_gets_there_is_answered_the_same_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        // A creature six metres under the ground, twelve metres out.
        let x = world::PLAYER_X + 12.0;
        let ground = dats::ground_z(world::LANDBLOCK, x, world::PLAYER_Y);
        let buried = Object {
            position: Some((
                world::outdoor_cell(world::LANDBLOCK, x, world::PLAYER_Y),
                x,
                world::PLAYER_Y,
                ground - 6.0,
            )),
            ..world::monster(BURIED, x)
        };
        arm.server.send(&buried.build());
        arm.sync();

        transcript.step("can it be asked");
        let available = arm.call::<calls::ProjectilesAvailable>(&());
        transcript.record("available", available);
        assert!(available, "{}", arm.name());

        for kind in [
            ProjectilePathKind::Straight,
            ProjectilePathKind::Arc,
            ProjectilePathKind::Missile,
        ] {
            transcript.step(&format!("nearest creature, {kind:?}"));
            let near = ask(arm, world::MONSTER, kind);
            record_shot(transcript, &near);
            assert_ne!(near.status, ProjectilePathStatus::Unavailable);

            transcript.step(&format!("creature behind others, {kind:?}"));
            let far = ask(arm, world::SECOND_MONSTER, kind);
            record_shot(transcript, &far);
            assert_ne!(far.status, ProjectilePathStatus::Unavailable);
        }

        // The ground is in the way of a creature under it.
        transcript.step("creature with the ground in the way");
        let under = ask(arm, BURIED, ProjectilePathKind::Straight);
        record_shot(transcript, &under);
        assert_eq!(
            under.status,
            ProjectilePathStatus::Blocked,
            "{}",
            arm.name()
        );
        assert_eq!(
            ask(arm, world::MONSTER, ProjectilePathKind::Straight).status,
            ProjectilePathStatus::Clear
        );

        // A shot at a named creature passes the other creatures on its line
        // (the nearer monster stands between): only the target stops it
        // (`ac-physics` `ObjectInfo::missile_ignore`).
        let far = ask(arm, world::SECOND_MONSTER, ProjectilePathKind::Straight);
        assert_eq!(far.status, ProjectilePathStatus::Clear, "{}", arm.name());
        assert_eq!(far.blocking_object_id, world::SECOND_MONSTER);

        transcript.step("nothing by that id");
        let nothing = ask(arm, 0x5000_FFFF, ProjectilePathKind::Straight);
        record_shot(transcript, &nothing);
    });
}

fn record_room(transcript: &mut Transcript, arm: &mut Arm, distance: f32) -> RoomAheadStatus {
    let room = arm.call::<calls::NavigationRoomAhead>(&distance);
    transcript.record("status", room.status);
    transcript.record_id("cell", room.position.cell_id);
    transcript.record("east", format!("{:.2}", room.position.east_west * 240.0));
    transcript.record("north", format!("{:.2}", room.position.north_south * 240.0));
    room.status
}

/// Something solid where the body would stand, and a creature in that same
/// spot (OpenAC `RoomAheadParityTests.Wall`, `Creature`).
const WALL: u32 = 0x5000_0070;
const CREATURE_AHEAD: u32 = 0x5000_0071;

/// The server creates `guid` three metres north of the character, who faces
/// north, with its feet at the character's own height.
///
/// OpenAC registers a bare four-metre cylinder there, which nothing outside
/// the session can do here; the server's object stands in for it. It is
/// not set on the ground: the retail ground there lies 1.75 m below the
/// character's feet, and the check tries the spot at heights measured from
/// the character's feet (`ac-physics` `placement_room::LIFTS`), so a
/// man-sized object on that ground is under every height tried.
fn say_ahead(arm: &mut Arm, guid: u32, name: &str, item_type: u32) {
    let y = world::PLAYER_Y + 3.0;
    let feet = dats::ground_z(world::LANDBLOCK, world::PLAYER_X, world::PLAYER_Y);
    let object = Object {
        position: Some((
            world::outdoor_cell(world::LANDBLOCK, world::PLAYER_X, y),
            world::PLAYER_X,
            y,
            feet,
        )),
        setup: Some(world::SETUP),
        ..Object::new(guid, name, item_type)
    };
    arm.server.send(&object.build());
    arm.sync();
}

// OpenAC `RoomAheadParityTests.AWallAheadLeavesNoRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dats: ground to stand on"]
fn a_wall_ahead_leaves_no_room_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        say_ahead(arm, WALL, "Wall", world::MISC);

        transcript.step("look three metres ahead at a wall");
        let status = record_room(transcript, arm, 3.0);
        assert_eq!(status, RoomAheadStatus::Blocked, "{}", arm.name());
    });
}

// OpenAC `RoomAheadParityTests.ACreatureAheadStillLeavesRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dats: ground to stand on"]
fn a_creature_ahead_still_leaves_room_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        say_ahead(arm, CREATURE_AHEAD, "Creature", world::CREATURE);

        transcript.step("look three metres ahead at a creature");
        let status = record_room(transcript, arm, 3.0);
        assert_eq!(status, RoomAheadStatus::Clear, "{}", arm.name());
    });
}

// OpenAC `RoomAheadParityTests.OpenGroundAheadIsRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dats: ground to stand on"]
fn open_ground_ahead_is_room_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        let north = arm
            .call::<calls::NavigationSnapshot>(&())
            .position
            .north_south;
        transcript.step("look three metres ahead");
        let room = arm.call::<calls::NavigationRoomAhead>(&3.0);
        assert_eq!(room.status, RoomAheadStatus::Clear, "{}", arm.name());
        let _ = record_room(transcript, arm, 3.0);
        assert!(((room.position.north_south - north) * 240.0 - 3.0).abs() < 0.01);
    });
}

// OpenAC `RoomAheadParityTests.ADistanceOutOfRangeIsUnknownOnBothClients`.
#[test]
#[ignore = "needs the retail dats: ground to stand on"]
fn a_distance_out_of_range_is_unknown_on_both_clients() {
    run_with(&with_a_body(), &[], true, |arm, transcript| {
        world::stage(arm);
        transcript.step("look eleven metres ahead");
        let status = record_room(transcript, arm, 11.0);
        assert_eq!(status, RoomAheadStatus::Unknown, "{}", arm.name());
    });
}
