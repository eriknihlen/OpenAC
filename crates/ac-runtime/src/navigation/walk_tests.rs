//! Walks against the retail dats: a session in Holtburg with its real
//! collision, a character placed on open ground, and `/nav` walking it. The
//! walk plans on its worker threads, drives the body through the movement
//! owner's scripted moves and ends where it was asked to. These tests need
//! the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::sync::{Arc, OnceLock};
use std::time::Duration;

use ac_physics::content::publish_landblock;
use ac_physics::{CollisionWorld, PhysicsEngine};
use ac_proto::{
    CreateObject, Inbound, ModelData, PhysicsTimestamps, Position, Quaternion, Vector3,
};
use glam::{Vec2, Vec3};

use super::{NavigationEvent, WalkState};
use crate::communication::CommunicationPaths;
use crate::movement::{MovementCommand, MovementKeys, WorldContent};
use crate::session::{Clocks, HostEvent, Outbox, SessionCommand, SessionState};

pub(crate) const PLAYER: u32 = 0x5000_000A;
const HUMAN_SETUP: u32 = 0x0200_0001;
const HUMAN_MOTION: u32 = 0x0900_0001;

pub(crate) fn content() -> Arc<WorldContent> {
    static CONTENT: OnceLock<Arc<WorldContent>> = OnceLock::new();
    CONTENT
        .get_or_init(|| {
            let dir = std::env::var_os("OPENAC_DAT_DIR").map(std::path::PathBuf::from);
            Arc::new(WorldContent::open(dir.as_deref()).expect("open the retail dats"))
        })
        .clone()
}

fn ground_z(x: f32, y: f32) -> f32 {
    let content = content();
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(
        &mut engine,
        content.dats(),
        content.height_table(),
        0xA9B4_FFFF,
        Vec3::ZERO,
    )
    .unwrap();
    engine.sample_terrain_z(x, y).unwrap()
}

fn outdoor_cell(x: f32, y: f32) -> u32 {
    0xA9B4_0000 | (1 + (x / 24.0) as u32 * 8 + (y / 24.0) as u32)
}

pub(crate) struct Session {
    pub state: SessionState,
    pub outbox: Outbox,
    pub now: Duration,
    pub events: Vec<HostEvent>,
}

impl Session {
    pub fn clocks(&self) -> Clocks {
        Clocks {
            wall_unix_ms: 1_700_000_000_000 + i64::try_from(self.now.as_millis()).unwrap_or(0),
            session: self.now,
        }
    }

    /// A character standing on open ground in Holtburg (landblock-local
    /// metres).
    pub fn at(x: f32, y: f32) -> Self {
        Self::in_cell(
            outdoor_cell(x, y),
            Vector3 {
                x,
                y,
                z: ground_z(x, y),
            },
        )
    }

    /// A character standing in a cell, at a point in its landblock-local
    /// metres.
    pub fn in_cell(cell_id: u32, origin: Vector3) -> Self {
        let mut state = SessionState::new(CommunicationPaths::default());
        state.install_content(content());
        state.world.set_player(PLAYER);
        let mut physics = crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                position: 1,
                ..PhysicsTimestamps::default()
            },
            Some(Position {
                cell_id,
                origin,
                orientation: Quaternion::IDENTITY,
            }),
        );
        physics.setup_id = Some(HUMAN_SETUP);
        physics.motion_table_id = Some(HUMAN_MOTION);
        physics.state = 0x0000_0408;
        let create = Inbound::CreateObject(Box::new(CreateObject {
            guid: PLAYER,
            model: ModelData::default(),
            physics,
            weenie: None,
        }));
        let mut session = Self {
            state,
            outbox: Outbox::new(),
            now: Duration::from_secs(1),
            events: Vec::new(),
        };
        let clocks = session.clocks();
        session
            .state
            .apply(&create, &mut session.outbox, true, clocks);
        session.frames(2);
        session
    }

    pub fn frames(&mut self, count: u32) {
        for _ in 0..count {
            // The simulated clock runs far faster than the wall clock here;
            // a grid build or route search on its worker is given the wall
            // time it would have had at the session's real pace.
            let walk = self.state.navigation.walk();
            let waited = std::time::Instant::now();
            while walk.is_searching()
                && !walk.search_finished()
                && waited.elapsed() < Duration::from_secs(60)
            {
                std::thread::sleep(Duration::from_millis(1));
            }
            self.now += Duration::from_micros(16_667);
            let clocks = self.clocks();
            self.state.advance_movement(&mut self.outbox, true, clocks);
            self.state.tick(&mut self.outbox, true, clocks);
            self.events.extend(self.state.drain_host_events());
            self.outbox.drain().for_each(drop);
        }
    }

    pub fn typed(&mut self, line: &str) {
        let clocks = self.clocks();
        self.state.command(
            SessionCommand::Chat(line.to_owned()),
            &mut self.outbox,
            true,
            clocks,
        );
        self.events.extend(self.state.drain_host_events());
    }

    pub fn position(&self) -> Vec3 {
        self.state.movement.controller().unwrap().position()
    }

    pub fn run_until_settled(&mut self, seconds: f32) -> WalkState {
        let frames = (seconds * 60.0) as u32;
        for _ in 0..frames {
            self.frames(1);
            let state = self.state.navigation.report().state;
            if !matches!(
                state,
                WalkState::Planning | WalkState::Walking | WalkState::Waiting
            ) {
                return state;
            }
        }
        self.state.navigation.report().state
    }

    pub fn chat(&self) -> Vec<String> {
        self.events
            .iter()
            .filter_map(|e| match e {
                HostEvent::Chat(line) => Some(line.text.clone()),
                _ => None,
            })
            .collect()
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_walk_to_a_place_plans_on_its_workers_and_ends_there() {
    let mut session = Session::at(60.0, 150.0);
    let start = session.position();
    session.typed("/nav debug on");
    session.typed(&format!(
        "/nav go to [80 170 {}] within 1",
        ground_z(80.0, 170.0)
    ));
    let state = session.run_until_settled(40.0);
    let end = session.position();
    println!(
        "{state:?} after walking from {start} to {end}: {:?}",
        session.state.navigation.report()
    );
    for line in session.chat() {
        println!("  {line}");
    }
    assert_eq!(state, WalkState::Arrived);
    let goal = Vec2::new(80.0, 170.0);
    let off = end.truncate().distance(goal);
    assert!(off <= 1.5, "ended {off} m from the place");
    assert!(
        session
            .chat()
            .iter()
            .any(|l| l.starts_with("[nav] Walk to 0xA9B4") && l.contains("arrived")),
        "the walk said how it ended"
    );
    assert!(session.events.iter().any(|e| matches!(
        e,
        HostEvent::Navigation(NavigationEvent::Report(r)) if r.state == WalkState::Walking
    )));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_player_moving_the_character_ends_the_walk() {
    let mut session = Session::at(60.0, 150.0);
    session.typed(&format!("/nav go to [80 190 {}]", ground_z(80.0, 190.0)));
    for _ in 0..600 {
        session.frames(1);
        if session.state.navigation.report().state == WalkState::Walking {
            break;
        }
    }
    assert_eq!(session.state.navigation.report().state, WalkState::Walking);
    let clocks = session.clocks();
    session.state.command(
        SessionCommand::Movement(MovementCommand::Keys(MovementKeys {
            turn_left: true,
            ..MovementKeys::default()
        })),
        &mut session.outbox,
        true,
        clocks,
    );
    let state = session.run_until_settled(2.0);
    assert_eq!(state, WalkState::Interrupted);
}

/// A corpse lying on open ground at (`x`, `y`), with no simulated body of
/// its own (the server sent it no motion), in a corpse's physics state:
/// ethereal, ignoring collisions, with gravity (0x414, a corpse's
/// `PhysicsState`), so it enters the world where it lies, beside
/// the character too.
fn lay_corpse(session: &mut Session, guid: u32, x: f32, y: f32) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(x, y),
            origin: Vector3 {
                x,
                y,
                z: ground_z(x, y),
            },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.state = 0x0000_0414;
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie: Some(ac_proto::PublicWeenieDesc {
            name: "Corpse of Drudge".into(),
            item_type: ac_core::items::ItemType::CONTAINER.bits(),
            object_description_flags: (ac_core::items::PublicWeenieFlags::CORPSE
                | ac_core::items::PublicWeenieFlags::OPENABLE)
                .bits(),
            useability: Some(ac_core::items::ItemUseable::REMOTE.bits()),
            items_capacity: Some(120),
            ..ac_proto::PublicWeenieDesc::default()
        }),
    }));
    let clocks = session.clocks();
    session
        .state
        .apply(&create, &mut session.outbox, true, clocks);
}

/// The uses of `guid` among the sent bodies.
fn uses_of(bodies: &[Vec<u8>], guid: u32) -> usize {
    bodies
        .iter()
        .filter(|b| {
            b.len() >= 16
                && u32::from_le_bytes([b[8], b[9], b[10], b[11]]) == 0x36
                && u32::from_le_bytes([b[12], b[13], b[14], b[15]]) == guid
        })
        .count()
}

fn use_object(session: &mut Session, guid: u32) -> Vec<Vec<u8>> {
    session.outbox.drain().for_each(drop);
    let clocks = session.clocks();
    session.state.command(
        SessionCommand::Item(crate::inventory::ItemCommand::Use(
            crate::inventory::ObjectRef::Guid(guid),
        )),
        &mut session.outbox,
        true,
        clocks,
    );
    session.outbox.drain().map(|o| o.body).collect()
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_use_of_a_corpse_out_of_reach_walks_there_and_uses_it_on_arrival() {
    // OpenAC WalkThenUseParityTests.AskingToUseACorpseOutOfReachWalksThereOnBothClients
    // and ArrivingSendsTheArmedUseOnBothClients: a corpse three metres
    // off; the walk is aimed at it, nothing is sent under way, the use goes
    // out on arrival with the character within reach.
    const CORPSE: u32 = 0x8000_C001;
    let mut session = Session::at(60.0, 150.0);
    lay_corpse(&mut session, CORPSE, 63.0, 150.0);
    let mut sent = use_object(&mut session, CORPSE);
    let walk = &session
        .state
        .movement
        .controller()
        .unwrap()
        .object()
        .move_to;
    assert_eq!(
        walk.movement_type_state,
        ac_motion::MovementType::MoveToObject
    );
    assert_eq!(walk.sought_object_id, CORPSE);
    assert_eq!(
        uses_of(&sent, CORPSE),
        0,
        "nothing is sent from out of reach"
    );
    assert!(session.state.inventory.has_pending_use());
    let mut arrived_after = None;
    for frame in 0..600 {
        session.now += Duration::from_micros(16_667);
        let clocks = session.clocks();
        session
            .state
            .advance_movement(&mut session.outbox, true, clocks);
        session.state.tick(&mut session.outbox, true, clocks);
        session.events.extend(session.state.drain_host_events());
        sent.extend(session.outbox.drain().map(|o| o.body));
        if uses_of(&sent, CORPSE) > 0 {
            arrived_after = Some(frame);
            break;
        }
    }
    let frames = arrived_after.expect("the use went out on arrival");
    // The walk ends when the gap between the two bodies (both the human
    // setup here, 0.68 m wide) is within the default use radius, 0.6 m.
    let own = session.state.movement.controller().unwrap().object().radius;
    let centres = session
        .position()
        .truncate()
        .distance(Vec2::new(63.0, 150.0));
    let gap = centres - 2.0 * own;
    println!("used after {frames} frames, {centres:.2} m between centres, gap {gap:.2} m");
    assert!(
        gap <= crate::inventory::DEFAULT_USE_RADIUS,
        "the walk stopped {gap:.2} m short of the corpse"
    );
    assert!(gap > 0.0, "the walk ran into the corpse");
    assert!(!session.state.inventory.has_pending_use());
    assert_eq!(
        session
            .state
            .inventory
            .external_containers()
            .requested_container_id(),
        CORPSE
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_use_of_a_corpse_within_reach_goes_out_at_once() {
    const CORPSE: u32 = 0x8000_C002;
    let mut session = Session::at(60.0, 150.0);
    lay_corpse(&mut session, CORPSE, 60.3, 150.0);
    let sent = use_object(&mut session, CORPSE);
    assert_eq!(uses_of(&sent, CORPSE), 1);
    assert!(!session.state.inventory.has_pending_use());
}

/// The navigation view an attached window draws (OpenAC
/// `NavMeshDebugOverlay`): with the grid shown, its points around the
/// character in the physics frame, centred on the character's landblock;
/// during a walk, the magenta route from the character.
#[test]
#[ignore = "needs the retail dat files"]
fn with_a_window_the_grid_around_the_character_and_the_walks_route_are_drawn() {
    use super::NavLineKind;

    let mut session = Session::at(60.0, 150.0);
    session.state.set_window_attached(true, &mut Outbox::new());
    session.typed("/nav grid");
    assert!(session.chat().iter().any(|l| l == "Navigation: grid shown"));
    for _ in 0..600 {
        session.frames(1);
        if session
            .state
            .navigation
            .view()
            .lines
            .iter()
            .any(|l| l.kind == NavLineKind::Clear)
        {
            break;
        }
    }
    let here = session.position();
    let view = session.state.navigation.view().clone();
    assert_eq!(view.frame_center, 0xA9B4_FFFF);
    let marks: Vec<_> = view
        .lines
        .iter()
        .filter(|l| matches!(l.kind, NavLineKind::Clear | NavLineKind::Tight))
        .collect();
    assert!(marks.len() > 100, "{} grid segments", marks.len());
    assert!(marks.iter().all(|l| {
        let middle = (l.from + l.to) * 0.5;
        middle.truncate().distance(here.truncate()) <= ac_nav::view::DRAW_RADIUS + 0.01
    }));

    session.typed(&format!("/nav go to [80 170 {}]", ground_z(80.0, 170.0)));
    for _ in 0..600 {
        session.frames(1);
        if session.state.navigation.report().state == WalkState::Walking {
            break;
        }
    }
    let here = session.position();
    let view = session.state.navigation.view();
    let route: Vec<_> = view
        .lines
        .iter()
        .filter(|l| l.kind == NavLineKind::Route)
        .collect();
    assert!(!route.is_empty());
    assert!(
        route
            .iter()
            .any(|l| (l.from.truncate() - here.truncate()).length() < 0.01
                && (l.from.z - here.z - ac_nav::view::ROUTE_LIFT).abs() < 0.01),
        "a route segment starts at the character"
    );

    // Without the window nothing is drawn.
    session.state.set_window_attached(false, &mut Outbox::new());
    session.frames(1);
    assert!(session.state.navigation.view().lines.is_empty());
}
