//! Navigation against the movement and navigation owners. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Navigation/RuntimeNavigationAutomationTests.cs`
//! and `RuntimeNavigationOwnershipTests.cs` (the arbitration itself is
//! tested in `ac-nav`'s `automation`).

use std::sync::mpsc;

use ac_core::items::{ClientObject, PublicWeenieFlags};
use ac_nav::walk::{WalkReport, WalkState};
use ac_plugin::abi::navigation::{
    GoToState, MoveDirection, MovePace, MoveUnit, MovementIntent, NavigationObject,
    NavigationStatus, PauseChange, RoomAhead, RoomAheadStatus,
};
use ac_plugin::abi::{Event, Wire, calls};
use ac_plugin::{Caller, SessionApi};

use super::PluginBinding;
use super::navigation::{enrich_with, follow, go_to_state, project_report};
use super::tests::{in_world, with_binding};
use crate::navigation::walk_tests::Session;
use crate::session::Outbox;

const DOOR: u32 = 0x7A00_0001;
/// The physics state bit of a passable object.
const ETHEREAL: u32 = 0x4;

fn call_as<C: ac_plugin::abi::Call>(
    binding: &mut PluginBinding<'_>,
    plugin_id: &str,
    request: &C::Request,
) -> C::Reply {
    let bytes = binding
        .call(
            Caller {
                plugin_id,
                api_version: ac_plugin::abi::API_CURRENT,
            },
            C::IMPORT,
            &request.to_bytes(),
        )
        .expect("a well-formed request");
    C::Reply::from_bytes(&bytes).expect("a well-formed reply")
}

fn door(appraised_open: Option<bool>) -> ClientObject {
    let mut door = ClientObject::new(DOOR);
    door.public_weenie_bitfield = Some(PublicWeenieFlags::DOOR);
    if let Some(open) = appraised_open {
        door.properties.bools.insert(2, open);
    }
    door
}

fn seen(item: &ClientObject, state: Option<u32>) -> NavigationObject {
    enrich_with(
        NavigationObject {
            object_id: DOOR,
            name: "Door".into(),
            ..NavigationObject::default()
        },
        Some(item),
        state,
    )
}

// OpenAC `ADoorReadsOpenWhenTheWorldShowsItOpenWhateverItsAppraisalSaid`.
#[test]
fn a_door_reads_open_when_the_world_shows_it_open_whatever_its_appraisal_said() {
    for (passable, appraised_open, open) in [
        (true, false, true),
        (false, true, false),
        (true, true, true),
        (false, false, false),
    ] {
        let state = if passable { ETHEREAL } else { 0 };
        let seen = seen(&door(Some(appraised_open)), Some(state));
        assert!(seen.is_door);
        assert_eq!(
            seen.is_open, open,
            "passable={passable} appraised={appraised_open}"
        );
    }
}

// OpenAC `AnAppraisedDoorKnowsItsLockStateEvenWithNoLockProperty`.
#[test]
fn an_appraised_door_knows_its_lock_state_even_with_no_lock_property() {
    let mut item = door(None);
    let nothing_yet = seen(&item, Some(0));
    item.last_appraisal_time_ms = 1;
    let appraised = seen(&item, Some(0));
    assert!(!nothing_yet.has_lock_state);
    assert!(appraised.has_lock_state);
    assert!(!appraised.is_locked);
}

// OpenAC `HowAWalkEndedReachesPluginsAsTheMatchingState`,
// `TerminalWalkOutcomesRemainDistinctForPlugins` and
// `AWalkWaitingOnSomethingElseIsReportedToPluginsAsWaiting`.
#[test]
fn how_a_walk_stands_reaches_plugins_as_the_matching_state() {
    for (state, expected) in [
        (WalkState::Arrived, GoToState::Arrived),
        (
            WalkState::ArrivedWithoutSight,
            GoToState::ArrivedWithoutSight,
        ),
        (WalkState::Planned, GoToState::None),
        (WalkState::NoRoute, GoToState::NoRoute),
        (WalkState::Blocked, GoToState::Blocked),
        (WalkState::Stopped, GoToState::Stopped),
        (WalkState::Interrupted, GoToState::Interrupted),
        (WalkState::Lost, GoToState::Lost),
        (WalkState::Waiting, GoToState::Waiting),
    ] {
        assert_eq!(go_to_state(state), expected, "{state:?}");
    }
    let report = project_report(
        &WalkReport {
            sequence: 12,
            state: WalkState::Waiting,
            object_id: 0x5000_0001,
            remaining_meters: f32::NAN,
            replans: 3,
            reason: "waiting: a plugin is running Attack".into(),
            blocked_by_object_id: 0x7000_0002,
        },
        GoToState::Waiting,
        None,
        1,
    );
    assert_eq!(report.state, GoToState::Waiting);
    assert_eq!(report.sequence, 12);
    assert_eq!(report.replans, 3);
    assert_eq!(
        report.reason.as_deref(),
        Some("waiting: a plugin is running Attack")
    );
    assert_eq!(report.blocked_by_object_id, 0x7000_0002);
}

// OpenAC `EveryMemberIsUnavailableUntilARuntimeIsBound`: with no connection
// every command is unavailable and the reads are empty.
#[test]
fn every_member_is_unavailable_without_a_connection() {
    let mut state = in_world();
    let (events, _host) = mpsc::channel();
    let mut binding = PluginBinding::offline(&mut state, &events, 0);
    let b = &mut binding;
    assert_eq!(
        call_as::<calls::NavigationFace>(b, "a", &90.0),
        NavigationStatus::Unavailable
    );
    let forward = ac_plugin::abi::navigation::MoveRequest {
        direction: MoveDirection::Forward,
        pace: MovePace::Run,
        amount: 5.0,
        unit: MoveUnit::MetersOrDegrees,
    };
    assert_eq!(
        call_as::<calls::NavigationMove>(b, "a", &forward),
        NavigationStatus::Unavailable
    );
    for status in [
        call_as::<calls::NavigationGoTo>(b, "a", &(0x5000_0001, 2.5)),
        call_as::<calls::NavigationStandOn>(b, "a", &(0x5000_0001, 2.5)),
        call_as::<calls::NavigationFollow>(b, "a", &(0x5000_0001, 3.0)),
        call_as::<calls::NavigationStopGoTo>(b, "a", &()),
        call_as::<calls::NavigationSetIntent>(b, "a", &MovementIntent::running()),
    ] {
        assert_eq!(status, NavigationStatus::Unavailable);
    }
    assert!(!call_as::<calls::NavigationSnapshot>(b, "a", &()).is_available);
    assert_eq!(
        call_as::<calls::NavigationGoToReport>(b, "a", &()),
        ac_plugin::abi::navigation::GoToReport::default()
    );
}

// OpenAC `SnapshotChanged_PublishesOnceWithAMonotonicRevision`.
#[test]
fn the_snapshot_is_published_once_with_a_growing_revision() {
    let mut state = crate::session::SessionState::new(Default::default());
    let first = follow(&mut state, false);
    let again = follow(&mut state, false);
    let [Event::NavigationSnapshotChanged(snapshot)] = first.as_slice() else {
        panic!("one snapshot, got {first:?}");
    };
    assert_eq!(snapshot.revision, 1);
    assert!(!snapshot.is_available);
    assert!(again.is_empty());
}

// OpenAC `WalksWaitOnWhatAPluginSaysItNeedsUntilThePluginLetsGo`: a pause
// names what its plugin needs until the plugin lets go; only its owner
// changes or releases it.
#[test]
fn walks_wait_on_what_a_plugin_says_it_needs_until_it_lets_go() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let pause = call_as::<calls::NavigationPause>(b, "a", &"a plugin is running Attack".into());
        assert_ne!(pause, 0);
        assert_eq!(
            b.state.navigation.pause_reason().as_deref(),
            Some("a plugin is running Attack")
        );
        let clear = PauseChange {
            handle: pause,
            reason: String::new(),
        };
        assert!(!call_as::<calls::NavigationPauseSet>(b, "b", &clear));
        assert!(call_as::<calls::NavigationPauseSet>(b, "a", &clear));
        assert_eq!(b.state.navigation.pause_reason(), None);
        let buffing = PauseChange {
            handle: pause,
            reason: "a plugin is buffing".into(),
        };
        assert!(call_as::<calls::NavigationPauseSet>(b, "a", &buffing));
        assert!(!call_as::<calls::NavigationPauseRelease>(b, "b", &pause));
        assert!(call_as::<calls::NavigationPauseRelease>(b, "a", &pause));
        assert!(!call_as::<calls::NavigationPauseRelease>(b, "a", &pause));
        assert_eq!(b.state.navigation.pause_reason(), None);
    });
}

// OpenAC `ReleasingAPluginStopsItsWalkAndDropsItsPausesButNotAnothers`
// (the pause half; the walk half is `ac-nav`'s).
#[test]
fn releasing_a_plugin_drops_its_pauses_but_not_anothers() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        call_as::<calls::NavigationPause>(b, "a", &"a needs it".into());
        let kept = call_as::<calls::NavigationPause>(b, "b", &"b needs it".into());
        b.released("a");
        assert_eq!(
            b.state.navigation.pause_reason().as_deref(),
            Some("b needs it")
        );
        assert!(call_as::<calls::NavigationPauseRelease>(b, "b", &kept));
    });
}

// OpenAC `RuntimeLocalPlayerMovementState.SetCommandInput`: a held intent
// replaces the keyboard until cleared; with no character it is refused.
#[test]
fn a_held_intent_needs_a_character_and_clears() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let forward = MovementIntent {
            forward: true,
            ..MovementIntent::running()
        };
        // The fixture has no body: the movement owner refuses.
        assert_eq!(
            call_as::<calls::NavigationSetIntent>(b, "a", &forward),
            NavigationStatus::Rejected
        );
        assert!(!b.state.movement.intent_steers());
        assert_eq!(
            call_as::<calls::NavigationClearIntent>(b, "a", &()),
            NavigationStatus::Accepted
        );
    });
}

// OpenAC `RuntimeNavigationAutomation.PreviewPathAsync`:
// a target or arrival out of range is `InvalidTarget`, a session that
// cannot walk (here: no dats) is `Unavailable`; every preview gets its own
// handle, and its plan reaches only the plugin that asked.
#[test]
fn a_preview_answers_only_the_plugin_that_asked_with_its_handle() {
    use super::navigation::follow_previews;
    use ac_plugin::abi::navigation::{NavigationPlanStatus, PathPreviewRequest};
    use ac_plugin::abi::world::Position;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let (zero, far, unloaded, place) = with_binding(&mut state, &mut outbox, |b| {
        let ask = |b: &mut PluginBinding<'_>, object_id: u32, arrival: f32| {
            call_as::<calls::NavigationPreviewPath>(
                b,
                "test.a",
                &PathPreviewRequest {
                    object_id,
                    place: None,
                    arrival_meters: arrival,
                },
            )
        };
        let zero = ask(b, 0, 2.5);
        let far = ask(b, 0x5000_0001, 51.0);
        let unloaded = ask(b, 0x5000_0001, 2.5);
        let place = call_as::<calls::NavigationPreviewPath>(
            b,
            "test.b",
            &PathPreviewRequest {
                object_id: 0,
                place: Some(Position {
                    east_west: f64::NAN,
                    ..Position::default()
                }),
                arrival_meters: 2.5,
            },
        );
        (zero, far, unloaded, place)
    });
    assert!(zero != 0 && far != 0 && unloaded != 0 && place != 0);
    assert!(zero != far && far != unloaded && unloaded != place);
    let mut plans = Vec::new();
    for (owner, event) in follow_previews(&mut state) {
        let Event::NavigationPlan(plan) = event else {
            panic!("a plan, got {event:?}");
        };
        plans.push((owner, plan.handle, plan.status, plan.path.len()));
    }
    assert_eq!(
        plans,
        [
            (
                "test.a".to_owned(),
                zero,
                NavigationPlanStatus::InvalidTarget,
                0
            ),
            (
                "test.a".to_owned(),
                far,
                NavigationPlanStatus::InvalidTarget,
                0
            ),
            (
                "test.a".to_owned(),
                unloaded,
                NavigationPlanStatus::Unavailable,
                0
            ),
            (
                "test.b".to_owned(),
                place,
                NavigationPlanStatus::InvalidTarget,
                0
            ),
        ]
    );
    assert!(follow_previews(&mut state).is_empty(), "told once");
}

// OpenAC `RuntimeNavigationProjection.GlobalPosition`: a point
// measured from the first landblock's corner as map coordinates, its
// cell's outdoor flag, no heading.
#[test]
fn a_plan_point_reads_as_map_coordinates() {
    use super::navigation::project_plan;
    use ac_nav::walk::{NavPlan, PlanPoint, PlanStatus};
    use glam::Vec3;
    let plan = project_plan(
        7,
        NavPlan {
            status: PlanStatus::Routed,
            path: vec![
                PlanPoint {
                    cell_id: 0xA9B4_0031,
                    global: Vec3::new(24_468.0 + 240.0, 24_468.0 - 480.0, 60.0),
                },
                PlanPoint {
                    cell_id: 0xA9B4_0100,
                    global: Vec3::new(24_468.0, 24_468.0, -30.0),
                },
            ],
            reason: "routed".into(),
            length_meters: 12.5,
            ..NavPlan::default()
        },
    );
    assert_eq!(plan.handle, 7);
    assert_eq!(plan.path[0].east_west, 1.0);
    assert_eq!(plan.path[0].north_south, -2.0);
    assert_eq!(plan.path[0].elevation, 0.25);
    assert!(plan.path[0].is_outdoor);
    assert_eq!(plan.path[0].heading_degrees, 0.0);
    assert!(!plan.path[1].is_outdoor);
    assert_eq!(plan.length_meters, 12.5);
}

// OpenAC `RuntimeAutomationSurfaceTests.CheckRoomAhead_IsImplementedByTheRuntimeAndUnknownOutOfTheWorld`:
// the runtime answers the room check, and with no character in the world
// (or no body placed yet) it answers unknown.
#[test]
fn room_ahead_is_unknown_out_of_the_world_and_before_a_body() {
    let mut state = crate::session::SessionState::new(Default::default());
    let (events, _host) = mpsc::channel();
    let mut binding = PluginBinding::offline(&mut state, &events, 0);
    let room = call_as::<calls::NavigationRoomAhead>(&mut binding, "a", &3.0);
    assert_eq!(room.status, RoomAheadStatus::Unknown);
    assert_eq!(room, RoomAhead::default());

    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        // The fixture has no body.
        let room = call_as::<calls::NavigationRoomAhead>(b, "a", &3.0);
        assert_eq!(room.status, RoomAheadStatus::Unknown);
    });
}

// ── Room ahead on the retail dats: OpenAC
// `tests/AcDream.HostParity.Tests/Scenarios/RoomAheadParityTests.cs`. The
// character stands on open ground in Holtburg facing north; the answer comes
// from the movement owner's own collision.

/// Something solid three metres north of the character.
const WALL_AHEAD: u32 = 0x8000_7001;
/// A creature standing in that same spot.
const CREATURE_AHEAD: u32 = 0x8000_7002;

/// A tall cylinder where the body would stand: three metres north of the
/// character, who faces north.
fn register_ahead(session: &mut Session, id: u32, flags: u8) {
    use ac_physics::{
        EntityCollisionFlags, Quat, RegistrationSite, ShadowCollisionType, SingleShape, Vec3,
    };
    let controller = session.state.movement.controller().expect("placed");
    let feet = controller.position();
    let cell = controller.cell_id();
    let (shadows, world) = session.state.movement.engine_mut().shadows_and_world();
    shadows
        .register(
            world,
            id,
            &SingleShape {
                gfx_obj_id: 0,
                radius: 1.0,
                collision_type: ShadowCollisionType::Cylinder,
                cyl_height: 4.0,
                scale: 1.0,
            },
            RegistrationSite {
                world_pos: feet + Vec3::new(0.0, 3.0, 0.0),
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: (cell & 0xFFFF_0000) | 0xFFFF,
                seed_cell_id: cell,
            },
            0,
            EntityCollisionFlags(flags),
            false,
            None,
        )
        .expect("register");
}

fn look_ahead(session: &mut Session, distance: f32) -> RoomAhead {
    let Session { state, outbox, .. } = session;
    with_binding(state, outbox, |b| {
        call_as::<calls::NavigationRoomAhead>(b, "a", &distance)
    })
}

// OpenAC `OpenGroundAheadIsRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dat files"]
fn open_ground_ahead_is_room() {
    let mut session = Session::at(60.0, 150.0);
    let north = {
        let Session { state, outbox, .. } = &mut session;
        with_binding(state, outbox, |b| {
            call_as::<calls::NavigationSnapshot>(b, "a", &())
                .position
                .north_south
        })
    };

    let room = look_ahead(&mut session, 3.0);

    assert_eq!(room.status, RoomAheadStatus::Clear, "{room:?}");
    assert!(room.is_clear());
    let ahead = (room.position.north_south - north) * 240.0;
    assert!((ahead - 3.0).abs() < 0.005, "{ahead} m ahead");
}

// OpenAC `AWallAheadLeavesNoRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dat files"]
fn a_wall_ahead_leaves_no_room() {
    let mut session = Session::at(60.0, 150.0);
    register_ahead(
        &mut session,
        WALL_AHEAD,
        ac_physics::EntityCollisionFlags::NONE,
    );

    let room = look_ahead(&mut session, 3.0);

    assert_eq!(room.status, RoomAheadStatus::Blocked, "{room:?}");
    assert_eq!(room.position, ac_plugin::abi::world::Position::default());
}

// OpenAC `ACreatureAheadStillLeavesRoomOnBothClients`.
#[test]
#[ignore = "needs the retail dat files"]
fn a_creature_ahead_still_leaves_room() {
    let mut session = Session::at(60.0, 150.0);
    register_ahead(
        &mut session,
        CREATURE_AHEAD,
        ac_physics::EntityCollisionFlags::IS_CREATURE,
    );

    let room = look_ahead(&mut session, 3.0);

    assert_eq!(room.status, RoomAheadStatus::Clear, "{room:?}");
}

// OpenAC `ADistanceOutOfRangeIsUnknownOnBothClients`.
#[test]
#[ignore = "needs the retail dat files"]
fn a_distance_out_of_range_ahead_is_unknown() {
    let mut session = Session::at(60.0, 150.0);

    let room = look_ahead(&mut session, 11.0);

    assert_eq!(room.status, RoomAheadStatus::Unknown);
}

/// A dungeon's indoor cells from the installed game files, asked as a
/// plugin built for this API and as one built for API 1.4 asks: this one
/// hears each cell's doorways and the cells seen from it, every doorway
/// onto a cell the landblock lists; the older plugin is sent the records
/// as they were, byte for byte.
#[test]
#[ignore = "needs the retail dat files"]
fn a_dungeons_indoor_cells_carry_their_doorways_and_older_plugins_get_the_records_they_knew() {
    use ac_plugin::abi::world::IndoorCell;
    use ac_plugin::abi::{API_CURRENT, ApiVersion, Import};
    const DUNGEON: u32 = 0x0032_0000;
    let mut session = Session::at(60.0, 150.0);
    let Session { state, outbox, .. } = &mut session;
    with_binding(state, outbox, |b| {
        let cells = call_as::<calls::DungeonIndoorCells>(b, "a", &DUNGEON);
        assert!(cells.len() > 300, "{} cells", cells.len());
        let ids: std::collections::HashSet<u32> = cells.iter().map(|c| c.cell_id).collect();
        let doorways: usize = cells.iter().map(|c| c.doorways.len()).sum();
        assert!(doorways >= cells.len(), "{doorways} doorways");
        for cell in &cells {
            for doorway in &cell.doorways {
                assert_eq!(doorway.leads_outside, doorway.other_cell_id == 0);
                assert!(
                    doorway.leads_outside || ids.contains(&doorway.other_cell_id),
                    "0x{:08X} opens onto 0x{:08X}",
                    cell.cell_id,
                    doorway.other_cell_id
                );
            }
            assert!(
                cell.visible_cells
                    .iter()
                    .all(|id| id >> 16 == DUNGEON >> 16)
            );
        }
        assert!(cells.iter().any(|c| !c.visible_cells.is_empty()));

        let v1_4 = ApiVersion::new(1, 4);
        let ask = |b: &mut PluginBinding<'_>, version| {
            let caller = Caller {
                plugin_id: "a",
                api_version: version,
            };
            b.call(caller, Import::DungeonIndoorCells, &DUNGEON.to_bytes())
                .unwrap()
        };
        let older = ask(b, v1_4);
        assert_eq!(older, cells.to_bytes_at(v1_4));
        assert_eq!(ask(b, API_CURRENT), cells.to_bytes());
        let read = Vec::<IndoorCell>::from_bytes_at(&older, v1_4).unwrap();
        assert_eq!(read.len(), cells.len());
        assert!(
            read.iter()
                .all(|c| c.doorways.is_empty() && c.visible_cells.is_empty())
        );
        assert_eq!(read[0].cell_id, cells[0].cell_id);
    });
}

/// A dungeon's floorplan from the installed game files: each cell's box
/// holds its middle and its floor, and a plugin built for API 1.4 is sent
/// the plan as it was, byte for byte. Prints how often the band a cell is
/// drawn in and the band of its own floor differ, and which cells one list
/// has and the other has not.
#[test]
#[ignore = "needs the retail dat files"]
fn a_dungeons_floorplan_cells_carry_their_bounds_and_floor() {
    use ac_plugin::abi::world::Floorplan;
    use ac_plugin::abi::{API_CURRENT, ApiVersion, Import};
    const DUNGEON: u32 = 0x0032_0000;
    let mut session = Session::at(60.0, 150.0);
    let Session { state, outbox, .. } = &mut session;
    with_binding(state, outbox, |b| {
        let plan = call_as::<calls::DungeonFloorplan>(b, "a", &DUNGEON).expect("a plan");
        let indoor = call_as::<calls::DungeonIndoorCells>(b, "a", &DUNGEON);
        let mut floorless = 0;
        let mut off_band = 0;
        for cell in &plan.cells {
            let (low, high) = (cell.bounds_min, cell.bounds_max);
            let inside = |(x, y, z): (f32, f32, f32)| {
                x >= low.0 - 1e-3
                    && y >= low.1 - 1e-3
                    && z >= low.2 - 1e-3
                    && x <= high.0 + 1e-3
                    && y <= high.1 + 1e-3
                    && z <= high.2 + 1e-3
            };
            assert!(inside(cell.center), "0x{:08X}", cell.cell_id);
            match cell.floor_z {
                Some(floor) => {
                    assert!(
                        floor >= low.2 - 1e-3 && floor <= high.2 + 1e-3,
                        "0x{:08X}",
                        cell.cell_id
                    );
                    if ac_nav::floorplan::layer_key(floor) != cell.layer_z {
                        off_band += 1;
                    }
                }
                None => floorless += 1,
            }
        }
        let planned: std::collections::HashSet<u32> =
            plan.cells.iter().map(|c| c.cell_id).collect();
        let listed: std::collections::HashSet<u32> = indoor.iter().map(|c| c.cell_id).collect();
        println!(
            "0x{:04X}: {} cells on the plan, {} listed; {floorless} without a floor, {off_band} drawn off their floor's band; listed only: {:X?}; planned only: {:X?}",
            DUNGEON >> 16,
            planned.len(),
            listed.len(),
            listed.difference(&planned).collect::<Vec<_>>(),
            planned.difference(&listed).collect::<Vec<_>>(),
        );
        assert!(planned.is_subset(&listed));

        let v1_4 = ApiVersion::new(1, 4);
        let ask = |b: &mut PluginBinding<'_>, version| {
            let caller = Caller {
                plugin_id: "a",
                api_version: version,
            };
            b.call(caller, Import::DungeonFloorplan, &DUNGEON.to_bytes())
                .unwrap()
        };
        let older = ask(b, v1_4);
        let plan = Some(plan);
        assert_eq!(older, plan.to_bytes_at(v1_4));
        assert_eq!(ask(b, API_CURRENT), plan.to_bytes());
        let read = Option::<Floorplan>::from_bytes_at(&older, v1_4)
            .unwrap()
            .unwrap();
        assert!(read.cells.iter().all(|c| c.floor_z.is_none()));
    });
}

// A reach search asks for nothing while there is nothing to search from,
// or for a distance that is not above 0: the handle is 0 and no answer
// follows.
#[test]
fn a_reach_search_with_nothing_to_search_from_has_no_handle() {
    use super::navigation::follow_previews;
    use ac_plugin::abi::navigation::ReachRequest;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let handles = with_binding(&mut state, &mut outbox, |b| {
        [50.0, f32::INFINITY, 0.0, -1.0, f32::NAN].map(|max_meters| {
            call_as::<calls::NavigationReachableCells>(b, "test.a", &ReachRequest { max_meters })
        })
    });
    assert_eq!(handles, [0; 5], "no dats, so no walks");
    assert!(follow_previews(&mut state).is_empty());
}

// A reach answer as plugins read it: each cell's place as map coordinates
// with its cell, as a preview's points are.
#[test]
fn a_reach_answer_reads_as_map_coordinates() {
    use super::navigation::project_reach;
    use ac_nav::walk::{CellReach, NavReach, PlanStatus};
    use ac_plugin::abi::navigation::NavigationPlanStatus;
    use glam::Vec3;
    let reach = project_reach(
        9,
        NavReach {
            status: PlanStatus::Routed,
            cells: vec![CellReach {
                cell_id: 0xA9B4_0100,
                route_meters: 12.5,
                place: Vec3::new(24_468.0 + 240.0, 24_468.0 - 480.0, -30.0),
                by_drop: true,
            }],
            reason: "1 cells reached".into(),
            grid_reused: true,
            took_ms: 4.0,
        },
    );
    assert_eq!(reach.handle, 9);
    assert_eq!(reach.status, NavigationPlanStatus::Routed);
    assert!(reach.grid_reused);
    assert_eq!(reach.took_ms, 4.0);
    let cell = &reach.cells[0];
    assert_eq!(
        (cell.cell_id, cell.route_meters, cell.by_drop),
        (0xA9B4_0100, 12.5, true)
    );
    assert!((cell.place.east_west - 1.0).abs() < 1e-9);
    assert!((cell.place.north_south + 2.0).abs() < 1e-9);
    assert!((cell.place.elevation + 0.125).abs() < 1e-9);
    assert!(!cell.place.is_outdoor);
}

/// A reach search in an installed dungeon through the plugin call: the
/// plugin that asked hears every cell it can walk to, each placed on that
/// cell's floor, its own cell first; another plugin hears nothing.
#[test]
#[ignore = "needs the retail dat files"]
fn a_reach_search_in_a_dungeon_answers_the_plugin_that_asked() {
    use super::navigation::follow_previews;
    use ac_plugin::abi::navigation::{NavigationPlanStatus, ReachRequest};
    const CELL: u32 = 0x0032_0000;
    // Stood on the floor of a cell with one, in the middle of its box.
    let content = crate::navigation::walk_tests::content();
    let plan = ac_nav::floorplan::DungeonFloorplanBuilder::new()
        .try_build(content.dats(), CELL)
        .unwrap()
        .unwrap();
    let (cell, floor) = plan
        .cells
        .iter()
        .find_map(|c| c.floor_z.map(|z| (c, z)))
        .expect("a cell with a floor");
    let middle = (cell.bounds.min + cell.bounds.max) * 0.5;
    let mut session = Session::in_cell(
        cell.cell_id,
        ac_proto::Vector3 {
            x: middle.x,
            y: middle.y,
            z: floor + 0.05,
        },
    );
    for _ in 0..300 {
        if session.state.movement.controller().is_some() {
            break;
        }
        session.frames(1);
    }
    session.frames(60);
    let handle = {
        let Session { state, outbox, .. } = &mut session;
        with_binding(state, outbox, |b| {
            call_as::<calls::NavigationReachableCells>(
                b,
                "test.a",
                &ReachRequest {
                    max_meters: f32::INFINITY,
                },
            )
        })
    };
    assert_ne!(handle, 0, "in a sealed dungeon");
    // The search runs on a worker, much slower in a debug build.
    let mut answers = Vec::new();
    let asked = std::time::Instant::now();
    while answers.is_empty() && asked.elapsed() < std::time::Duration::from_secs(120) {
        std::thread::sleep(std::time::Duration::from_millis(10));
        session.frames(1);
        answers.extend(follow_previews(&mut session.state));
    }
    let [(owner, Event::NavigationReach(reach))] = answers.as_slice() else {
        panic!("one reach answer, got {answers:?}");
    };
    assert_eq!(owner, "test.a");
    assert_eq!(reach.handle, handle);
    assert_eq!(
        reach.status,
        NavigationPlanStatus::Routed,
        "{}",
        reach.reason
    );
    assert!(reach.cells.len() > 200, "{}", reach.reason);
    assert!(reach.cells[0].route_meters < 1.0, "{:?}", reach.cells[0]);
    assert!(
        reach
            .cells
            .windows(2)
            .all(|w| w[0].route_meters <= w[1].route_meters)
    );
    assert!(reach.cells.iter().all(|c| c.place.cell_id == c.cell_id
        && c.cell_id >> 16 == CELL >> 16
        && !c.place.is_outdoor));
}
