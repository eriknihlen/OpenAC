//! A portal arrival in a session no window holds waits for its
//! destination's collision, as OpenAC's headless host waits for its
//! collision neighbourhood: a plugin sees portal space meanwhile. Cases
//! from OpenAC `HeadlessSessionHostTests`
//! (`HeadlessPortalPrepareDestinationParksThenCommitsOnCollisionGenerationWake`,
//! `WorldProjectionHydratesCanonicalMovementAndTeleportState`) and
//! `RuntimeLiveEntitySessionControllerTests`
//! (`DirectSinkRetiresAPortalRevealReplacedWhileItWaitsForItsDestination`,
//! `DirectSinkEndsAWaitingPortalRevealAtTheSessionResetEdge`), run against
//! the retail dats. These tests need the dats: set `OPENAC_DAT_DIR` or
//! install them under `Documents\Asheron's Call`; run with
//! `cargo test -- --include-ignored`.

use ac_physics::movement::PlayerState;
use ac_physics::{CollisionWorld, PhysicsEngine};
use ac_plugin::abi::{Wire, calls};
use ac_plugin::{Caller, SessionApi};
use ac_proto::{Inbound, Position, Quaternion, Vector3, build_login_complete};
use glam::Vec3;

use super::PluginBinding;
use super::tests::with_binding;
use crate::navigation::walk_tests::{PLAYER, Session, content};
use crate::world::transit::PortalSnapshot;

/// Yaraq, far from Holtburg: none of its collision is loaded.
const FAR: u32 = 0x7D64_FFFF;
/// Another far landblock (Rithwic).
const OTHER_FAR: u32 = 0xC98C_FFFF;

fn call_as<C: ac_plugin::abi::Call>(
    binding: &mut PluginBinding<'_>,
    request: &C::Request,
) -> C::Reply {
    let bytes = binding
        .call(
            Caller {
                plugin_id: "a",
                api_version: ac_plugin::abi::API_CURRENT,
            },
            C::IMPORT,
            &request.to_bytes(),
        )
        .expect("a well-formed request");
    C::Reply::from_bytes(&bytes).expect("a well-formed reply")
}

/// What a plugin reads now: whether the character is in portal space.
fn plugin_sees_portal_space(session: &mut Session) -> bool {
    let Session { state, outbox, .. } = session;
    with_binding(state, outbox, |b| {
        call_as::<calls::NavigationSnapshot>(b, &()).is_portal_space
    })
}

/// The cell holding landblock-local `(x, y)` and the ground there.
fn spot(landblock: u32, x: f32, y: f32) -> Position {
    let content = content();
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    ac_physics::content::publish_landblock(
        &mut engine,
        content.dats(),
        content.height_table(),
        landblock,
        Vec3::ZERO,
    )
    .unwrap();
    let z = engine.sample_terrain_z(x, y).unwrap();
    Position {
        cell_id: (landblock & 0xFFFF_0000) | (1 + (x / 24.0) as u32 * 8 + (y / 24.0) as u32),
        origin: Vector3 { x, y, z },
        orientation: Quaternion::IDENTITY,
    }
}

/// The server's teleport start and the destination it sends with it.
fn teleport(session: &mut Session, sequence: u16, to: Position) {
    let clocks = session.clocks();
    let start = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
        teleport_sequence: sequence,
    });
    session
        .state
        .apply(&start, &mut session.outbox, true, clocks);
    let position = Inbound::UpdatePosition(ac_proto::UpdatePosition {
        guid: PLAYER,
        flags: 0,
        position: to,
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: 1 + sequence,
        teleport_sequence: sequence,
        force_position_sequence: 0,
    });
    session
        .state
        .apply(&position, &mut session.outbox, true, clocks);
}

/// How many login completes went out since the last call.
fn login_completes(session: &mut Session) -> usize {
    session
        .outbox
        .drain()
        .filter(|o| o.body == build_login_complete())
        .count()
}

/// One pass after the network: the movement step, the owners' tick (the
/// held arrival's retry), and what goes out.
fn pass(session: &mut Session) -> usize {
    session.now += std::time::Duration::from_micros(16_667);
    let clocks = session.clocks();
    session
        .state
        .advance_movement(&mut session.outbox, true, clocks);
    session.state.tick(&mut session.outbox, true, clocks);
    session.events.extend(session.state.drain_host_events());
    login_completes(session)
}

fn state(session: &Session) -> PlayerState {
    session.state.movement.controller().unwrap().state
}

fn active(portal: PortalSnapshot) -> bool {
    portal.generation != 0 && !portal.completed && !portal.cancelled
}

/// OpenAC `HeadlessPortalPrepareDestinationParksThenCommitsOnCollisionGenerationWake`:
/// the destination's collision is not loaded, so the arrival parks in
/// portal space (no login complete), and a plugin sees portal space on
/// its ticks; the destination's square loads over the passes, the centre
/// first, and once it is in the player is placed there, leaves portal
/// space and the server hears login complete.
#[test]
#[ignore = "needs the retail dat files"]
fn a_teleport_to_an_unloaded_landblock_holds_portal_space_until_its_collision_is_loaded() {
    let mut session = Session::at(60.0, 150.0);
    login_completes(&mut session);
    let to = spot(FAR, 100.0, 100.0);
    teleport(&mut session, 1, to);

    assert_eq!(
        login_completes(&mut session),
        0,
        "parked: no login complete"
    );
    assert!(active(session.state.transit.snapshot()));
    assert_eq!(state(&session), PlayerState::PortalSpace);
    assert!(session.state.movement.residency().is_recentring());
    assert!(plugin_sees_portal_space(&mut session));
    // The network pass loaded the destination's centre first.
    let resident: Vec<u32> = session.state.movement.residency().resident().collect();
    assert_eq!(resident, [FAR]);

    let mut held_passes = 0;
    let mut sent = 0;
    for _ in 0..20 {
        sent = pass(&mut session);
        if session.state.transit.snapshot().completed {
            break;
        }
        held_passes += 1;
        assert!(plugin_sees_portal_space(&mut session), "pass {held_passes}");
        assert_eq!(state(&session), PlayerState::PortalSpace);
        assert_eq!(sent, 0);
    }
    println!("held for {held_passes} passes after the network pass");
    assert!(held_passes >= 1, "a plugin tick sees portal space");
    let portal = session.state.transit.snapshot();
    assert!(portal.completed && portal.materialized && !portal.cancelled);
    assert_eq!(portal.readiness.destination_cell, to.cell_id);
    assert_eq!(sent, 1, "login complete once, at the arrival");
    assert!(!session.state.transit.is_teleport_active());
    assert!(!plugin_sees_portal_space(&mut session));

    let residency = session.state.movement.residency();
    assert!(!residency.is_recentring());
    assert_eq!(residency.frame_center(), Some(FAR));
    assert_eq!(residency.resident().count(), 9);
    let controller = session.state.movement.controller().unwrap();
    assert_eq!(controller.state, PlayerState::InWorld);
    assert_eq!(controller.cell_id() & 0xFFFF_0000, FAR & 0xFFFF_0000);
    let placed = controller.current_cell_position().frame.origin;
    assert!((placed.x - 100.0).abs() < 0.05 && (placed.y - 100.0).abs() < 0.05);

    // It walks on the new collision.
    for _ in 0..3 {
        assert_eq!(pass(&mut session), 0);
    }
    assert_eq!(state(&session), PlayerState::InWorld);
}

/// OpenAC `WorldProjectionHydratesCanonicalMovementAndTeleportState`: a
/// teleport within the landblock the collision is built around is ready
/// at once, so the arrival completes in the pass it arrives.
#[test]
#[ignore = "needs the retail dat files"]
fn a_teleport_within_the_loaded_landblock_completes_in_the_same_pass() {
    let mut session = Session::at(60.0, 150.0);
    login_completes(&mut session);
    let to = spot(0xA9B4_FFFF, 96.0, 97.0);
    teleport(&mut session, 1, to);

    assert_eq!(login_completes(&mut session), 1);
    let portal = session.state.transit.snapshot();
    assert!(portal.completed && !portal.cancelled);
    assert!(!plugin_sees_portal_space(&mut session));
    assert!(!session.state.movement.residency().is_recentring());
    let controller = session.state.movement.controller().unwrap();
    assert_eq!(controller.state, PlayerState::InWorld);
    let placed = controller.current_cell_position().frame.origin;
    assert!((placed.x - 96.0).abs() < 0.05 && (placed.y - 97.0).abs() < 0.05);
}

/// OpenAC `DirectSinkRetiresAPortalRevealReplacedWhileItWaitsForItsDestination`:
/// a newer teleport replaces an arrival still waiting for its collision;
/// the old reveal ends, and the newer one completes once its own
/// destination is loaded.
#[test]
#[ignore = "needs the retail dat files"]
fn a_newer_teleport_replaces_an_arrival_waiting_for_its_collision() {
    let mut session = Session::at(60.0, 150.0);
    login_completes(&mut session);
    teleport(&mut session, 1, spot(FAR, 100.0, 100.0));
    let first = session.state.transit.snapshot().generation;
    assert!(active(session.state.transit.snapshot()));

    let to = spot(OTHER_FAR, 60.0, 60.0);
    teleport(&mut session, 2, to);
    let second = session.state.transit.snapshot().generation;
    assert!(second > first);
    assert_eq!(login_completes(&mut session), 0);

    let mut sent = 0;
    for _ in 0..20 {
        sent += pass(&mut session);
        if session.state.transit.snapshot().completed {
            break;
        }
    }
    let portal = session.state.transit.snapshot();
    assert!(portal.completed);
    assert_eq!(portal.generation, second);
    assert_eq!(portal.readiness.destination_cell, to.cell_id);
    assert_eq!(sent, 1);
    assert_eq!(
        session.state.movement.residency().frame_center(),
        Some(OTHER_FAR)
    );
}

/// OpenAC `DirectSinkEndsAWaitingPortalRevealAtTheSessionResetEdge`: a
/// session that ends while its arrival waits ends that reveal, and nothing
/// is left for a later pass to finish.
#[test]
#[ignore = "needs the retail dat files"]
fn the_end_of_a_generation_ends_an_arrival_waiting_for_its_collision() {
    let mut session = Session::at(60.0, 150.0);
    login_completes(&mut session);
    teleport(&mut session, 1, spot(FAR, 100.0, 100.0));
    assert!(active(session.state.transit.snapshot()));

    let wall = session.clocks().wall_unix_ms;
    session.state.end_generation(wall);
    assert_eq!(session.state.transit.snapshot(), PortalSnapshot::IDLE);
    for _ in 0..12 {
        assert_eq!(pass(&mut session), 0);
    }
    assert_eq!(session.state.transit.snapshot(), PortalSnapshot::IDLE);
}

/// A minimized window holds nothing, so the arrival waits for its
/// collision as it does without a window (OpenAC's update loop runs on
/// while minimized and completes the arrival when the destination is
/// ready); a shown window still holds the arrival itself, on collision
/// loaded at once.
#[test]
#[ignore = "needs the retail dat files"]
fn a_minimized_window_waits_for_the_collision_as_without_a_window() {
    let mut session = Session::at(60.0, 150.0);
    login_completes(&mut session);
    let clocks = session.clocks();
    session.state.set_window_attached(true, &mut session.outbox);
    session.state.command(
        crate::session::SessionCommand::WindowMinimized(true),
        &mut session.outbox,
        true,
        clocks,
    );
    teleport(&mut session, 1, spot(FAR, 100.0, 100.0));
    assert!(session.state.held_arrival().is_none());
    assert!(session.state.movement.residency().is_recentring());
    assert_eq!(login_completes(&mut session), 0);
    let mut held_passes = 0;
    while !session.state.transit.snapshot().completed && held_passes < 20 {
        pass(&mut session);
        held_passes += 1;
    }
    assert!(session.state.transit.snapshot().completed);
    assert!(held_passes > 1);

    // Shown again, the window holds the next arrival itself.
    let clocks = session.clocks();
    session.state.command(
        crate::session::SessionCommand::WindowMinimized(false),
        &mut session.outbox,
        true,
        clocks,
    );
    teleport(&mut session, 2, spot(OTHER_FAR, 60.0, 60.0));
    assert!(session.state.held_arrival().is_some());
    assert!(!session.state.movement.residency().is_recentring());
    assert_eq!(
        session.state.movement.residency().frame_center(),
        Some(OTHER_FAR)
    );
}
