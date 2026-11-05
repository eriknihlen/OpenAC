//! The body the combat and magic owners read, against the retail dats: the
//! character enters standing at peace (ready for a mode change), and the
//! stop before a swing ends a run and tells the server at once (OpenAC
//! `GameRuntime.PrepareLocalPlayerForAttackRequest`,
//! `RuntimeLocalPlayerMovementState.IsInReadyPosition`). These tests need
//! the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::sync::{Arc, OnceLock};
use std::time::Duration;

use ac_motion::motion_command::{NON_COMBAT, READY, RUN_FORWARD, WALK_FORWARD};
use ac_physics::content::publish_landblock;
use ac_physics::{CollisionWorld, PhysicsEngine};
use ac_proto::{
    CreateObject, Inbound, ModelData, PhysicsTimestamps, Position, Quaternion, Vector3,
};
use glam::Vec3;

use super::super::{
    MoveDirection, MovePace, MoveRequest, MoveUnit, Movement, MovementCommand, OutboundKind,
    WorldContent,
};
use crate::session::Outbox;
use crate::world::World;

const PLAYER: u32 = 0x5000_000A;
const SPOT: (f32, f32) = (60.0, 150.0);

fn content() -> Arc<WorldContent> {
    static CONTENT: OnceLock<Arc<WorldContent>> = OnceLock::new();
    CONTENT
        .get_or_init(|| {
            let dir = std::env::var_os("OPENAC_DAT_DIR").map(std::path::PathBuf::from);
            Arc::new(WorldContent::open(dir.as_deref()).expect("open the retail dats"))
        })
        .clone()
}

/// The character standing in Holtburg with the human setup and motion
/// table, one frame in.
fn entered() -> (World, Movement, Outbox, Duration) {
    let content = content();
    let (x, y) = SPOT;
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
    let z = engine.sample_terrain_z(x, y).unwrap();
    let mut world = World::new();
    world.set_player(PLAYER);
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: 0xA9B4_0000 | (1 + (x / 24.0) as u32 * 8 + (y / 24.0) as u32),
            origin: Vector3 { x, y, z },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(0x0200_0001);
    physics.motion_table_id = Some(0x0900_0001);
    physics.state = 0x0000_0408;
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid: PLAYER,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    let mut movement = Movement::new(true);
    movement.install_content(content);
    let mut outbox = Outbox::new();
    let mark = world.change_count();
    world.apply(&create);
    movement.observe(&mut world, &create, mark, &mut outbox, Duration::ZERO);
    let mut now = Duration::ZERO;
    frames(&mut world, &mut movement, &mut outbox, &mut now, 2);
    (world, movement, outbox, now)
}

fn frames(
    world: &mut World,
    movement: &mut Movement,
    outbox: &mut Outbox,
    now: &mut Duration,
    n: u32,
) {
    for _ in 0..n {
        *now += Duration::from_micros(16_667);
        movement.advance_before_network(world, outbox, *now);
        movement.after_network(world, outbox);
    }
}

#[test]
fn without_a_body_there_is_no_motion_and_no_stop() {
    let mut movement = Movement::new(false);
    assert_eq!(movement.body_motion(), None);
    assert!(!movement.prepare_for_attack_request(&World::new(), &mut Outbox::new()));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_body_enters_at_peace_and_ready() {
    let (_, movement, _, _) = entered();
    let motion = movement.body_motion().unwrap();
    assert_eq!(motion.current_style, NON_COMBAT);
    assert_eq!(motion.forward_command, READY);
    assert!(!motion.motions_pending);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_stop_before_a_swing_ends_a_run_and_tells_the_server_at_once() {
    let (mut world, mut movement, mut outbox, mut now) = entered();
    movement.command(
        MovementCommand::Move(MoveRequest {
            direction: MoveDirection::Forward,
            pace: MovePace::Run,
            amount: 5.0,
            unit: MoveUnit::Seconds,
        }),
        &world,
        &mut outbox,
    );
    frames(&mut world, &mut movement, &mut outbox, &mut now, 30);
    let running = movement.body_motion().unwrap().forward_command;
    assert!(
        running == RUN_FORWARD || running == WALK_FORWARD,
        "moving: {running:#X}"
    );
    let _ = movement.take_outbound_records();

    assert!(movement.prepare_for_attack_request(&world, &mut outbox));
    let records = movement.take_outbound_records();
    assert_eq!(records.len(), 1, "one movement message, sent at once");
    assert_eq!(records[0].kind, OutboundKind::MoveToState);
    assert_eq!(records[0].forward_command, READY);
    assert_eq!(movement.body_motion().unwrap().forward_command, READY);
}
