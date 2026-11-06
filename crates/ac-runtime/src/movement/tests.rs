//! The movement owner wired together against the retail dats: the
//! character enters the world in Holtburg with its real setup and motion
//! table, and the console's moves walk, run, turn and jump it through the
//! real collision, sending the movement messages OpenAC's frame sends
//! (`RuntimeLocalPlayerFrameControllerTests`, `LocalPlayerCycleQuantumTests`
//! and the headless parity scenarios `Movement`/`LocalMotion`, here as
//! end-to-end checks of the same owners). These tests need the dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run
//! with `cargo test -- --include-ignored`.

use std::sync::{Arc, OnceLock};
use std::time::Duration;

use ac_physics::content::publish_landblock;
use ac_physics::{CollisionWorld, PhysicsEngine};
use ac_proto::{CreateObject, ModelData, PhysicsTimestamps, Quaternion, Vector3};

use super::*;
use crate::session::Outbox;
use glam::Vec2;

const PLAYER: u32 = 0x5000_000A;
const HUMAN_SETUP: u32 = 0x0200_0001;
const HUMAN_MOTION: u32 = 0x0900_0001;
/// Flat open ground in Holtburg (terrain normal straight up).
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

fn ground_z(content: &WorldContent, x: f32, y: f32) -> f32 {
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

struct Rig {
    world: World,
    movement: Movement,
    outbox: Outbox,
    now: Duration,
}

impl Rig {
    fn entered() -> Self {
        Self::entered_with(Some(HUMAN_MOTION))
    }

    fn entered_with(motion: Option<u32>) -> Self {
        Self::created_at(motion, SPOT)
    }

    /// The character created at (`x`, `y`) in Holtburg's landblock, one
    /// frame run.
    fn created_at(motion: Option<u32>, (x, y): (f32, f32)) -> Self {
        let content = content();
        let z = ground_z(&content, x, y);
        let mut world = World::new();
        world.set_player(PLAYER);
        let mut physics = crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                position: 1,
                ..PhysicsTimestamps::default()
            },
            Some(Position {
                cell_id: outdoor_cell(x, y),
                origin: Vector3 { x, y, z },
                orientation: Quaternion::IDENTITY,
            }),
        );
        physics.setup_id = Some(HUMAN_SETUP);
        physics.motion_table_id = motion;
        physics.state = 0x0000_0408; // gravity, report collisions
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
        let mut rig = Self {
            world,
            movement,
            outbox,
            now: Duration::ZERO,
        };
        rig.frames(1);
        rig
    }

    /// Run frames of 1/60 s (the network pass in between does nothing).
    fn frames(&mut self, count: u32) {
        for _ in 0..count {
            self.now += Duration::from_micros(16_667);
            self.movement
                .advance_before_network(&mut self.world, &mut self.outbox, self.now);
            self.movement.after_network(&self.world, &mut self.outbox);
        }
    }

    fn body(&self) -> (Vec3, f32, bool) {
        let c = self.movement.controller().unwrap();
        (
            c.position(),
            get_heading(c.body_orientation()),
            c.can_send_position_event(),
        )
    }

    fn command(&mut self, command: MovementCommand) {
        self.movement
            .command(command, &self.world, &mut self.outbox);
    }
}

fn run(seconds: f32) -> MovementCommand {
    MovementCommand::Move(MoveRequest {
        direction: MoveDirection::Forward,
        pace: MovePace::Run,
        amount: seconds,
        unit: MoveUnit::Seconds,
    })
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_creature_created_before_its_landblock_is_loaded_gets_its_body_once_it_is() {
    // OpenAC admits a create at once on a host without presentation
    // (`RuntimeLiveEntitySessionController.OnSpawned` -> `RegisterEntity`);
    // its remote body waits for the collision (`RuntimeRemoteFirstEntryState`
    // `AwaitingCollisionSource`). The server sends the creatures around the
    // player before or after the player's own create, in any order.
    const CREATURE: u32 = 0x7000_0300;
    let content = content();
    let (x, y) = SPOT;
    let z = ground_z(&content, x, y);
    let mut world = World::new();
    world.set_player(PLAYER);
    let mut movement = Movement::new(true);
    movement.install_content(content);
    let mut outbox = Outbox::new();

    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            movement: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(x + 5.0, y),
            origin: Vector3 { x: x + 5.0, y, z },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    physics.motion_table_id = Some(HUMAN_MOTION);
    physics.state = 0x0000_0408;
    physics.movement = Some(ac_proto::PhysicsMovement::Present {
        raw: vec![0; 4],
        data: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState::default(),
                sticky_object: None,
            },
        },
        is_autonomous: false,
    });
    let creature = Inbound::CreateObject(Box::new(CreateObject {
        guid: CREATURE,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    let mark = world.change_count();
    world.apply(&creature);
    movement.observe(&mut world, &creature, mark, &mut outbox, Duration::ZERO);
    assert!(world.object(CREATURE).is_some(), "admitted at once");
    assert!(movement.remotes.is_empty(), "no collision loaded yet");

    let mut player = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(x, y),
            origin: Vector3 { x, y, z },
            orientation: Quaternion::IDENTITY,
        }),
    );
    player.setup_id = Some(HUMAN_SETUP);
    player.motion_table_id = Some(HUMAN_MOTION);
    player.state = 0x0000_0408;
    let enter = Inbound::CreateObject(Box::new(CreateObject {
        guid: PLAYER,
        model: ModelData::default(),
        physics: player,
        weenie: None,
    }));
    let mark = world.change_count();
    world.apply(&enter);
    movement.observe(&mut world, &enter, mark, &mut outbox, Duration::ZERO);

    assert_eq!(movement.residency().resident().count(), 9);
    assert!(
        movement.remotes.values().any(|body| body.guid == CREATURE),
        "the creature's body is armed once its landblock is resident"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_character_enters_standing_on_the_ground_with_collision_around_it() {
    let rig = Rig::entered();
    let (_, _, on_ground) = rig.body();
    assert!(on_ground, "settled on the terrain");
    assert_eq!(rig.movement.residency().resident().count(), 9);
    // The scenery plugins read: what the nine landblocks placed.
    let scenery: Vec<_> = rig.movement.residency().placed().collect();
    assert!(scenery.len() > 500, "{}", scenery.len());
    assert!(
        scenery
            .iter()
            .any(|s| s.landblock_id == 0xA9B4_FFFF && s.source_id >> 24 == 0x01)
    );
    let live = rig
        .world
        .object(PLAYER)
        .unwrap()
        .position()
        .copied()
        .unwrap();
    assert_eq!(live.cell_id >> 16, 0xA9B4);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_scripted_run_moves_the_body_by_its_animation_and_tells_the_server() {
    let mut rig = Rig::entered();
    let _ = rig.movement.take_outbound_records();
    let (start, heading, _) = rig.body();
    rig.command(run(2.0));
    rig.frames(150);
    let (end, _, on_ground) = rig.body();
    let travelled = (end - start).truncate().length();
    println!("ran {travelled:.2} m");
    // The run cycle at the fallback run skill (200): 4 m/s x run rate 2.375,
    // about 9.5 m/s, for two seconds (the stop takes a few frames more).
    assert!((16.0..23.0).contains(&travelled), "travelled {travelled}");
    assert!(on_ground);
    // It ran the way it faced (heading 0 is north, +y).
    let dir = (end - start).truncate().normalize();
    let facing = Vec2::new(heading.to_radians().sin(), heading.to_radians().cos());
    assert!(dir.dot(facing) > 0.99, "ran along its facing");

    let records = rig.movement.take_outbound_records();
    let kinds: Vec<OutboundKind> = records.iter().map(|r| r.kind).collect();
    assert_eq!(kinds.first(), Some(&OutboundKind::MoveToState));
    assert_eq!(
        records[0].forward_command,
        ac_motion::motion_command::WALK_FORWARD
    );
    let stop = records
        .iter()
        .rfind(|r| r.kind == OutboundKind::MoveToState)
        .unwrap();
    assert_eq!(stop.forward_command, ac_motion::motion_command::READY);
    let positions = kinds
        .iter()
        .filter(|k| **k == OutboundKind::AutonomousPosition)
        .count();
    assert!(
        positions >= 2,
        "autonomous positions while running: {positions}"
    );
    // Every action number is the next one.
    for (i, r) in records.iter().enumerate() {
        assert_eq!(r.sequence, records[0].sequence + i as u32);
    }
    // The world (and an attached window) sees the body where it is.
    let live = rig
        .world
        .object(PLAYER)
        .unwrap()
        .position()
        .copied()
        .unwrap();
    let local = Vec3::new(live.origin.x, live.origin.y, live.origin.z);
    assert!((local.truncate() - end.truncate()).length() < 0.01);
    assert!(rig.movement.drain_events().iter().any(|e| matches!(
        e,
        MovementEvent::MoveEnded {
            state: ScriptedMoveState::Completed,
            ..
        }
    )));
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_scripted_turn_lands_on_its_angle() {
    let mut rig = Rig::entered();
    let (_, before, _) = rig.body();
    rig.command(MovementCommand::Move(MoveRequest {
        direction: MoveDirection::TurnRight,
        pace: MovePace::Run,
        amount: 90.0,
        unit: MoveUnit::MetersOrDegrees,
    }));
    rig.frames(240);
    let (_, after, _) = rig.body();
    let turned = (after - before).rem_euclid(360.0);
    println!("turned {turned:.2} degrees");
    assert!((turned - 90.0).abs() < 1.0, "turned {turned}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_jump_leaves_the_ground_and_lands_and_the_server_hears_it() {
    let mut rig = Rig::entered();
    let _ = rig.movement.take_outbound_records();
    let (start, _, _) = rig.body();
    rig.command(MovementCommand::Jump {
        power: 1.0,
        leave_at: None,
    });
    let mut highest = start.z;
    let mut left_ground = false;
    for _ in 0..240 {
        rig.frames(1);
        let (p, _, on_ground) = rig.body();
        highest = highest.max(p.z);
        left_ground |= !on_ground;
    }
    let (end, _, on_ground) = rig.body();
    println!("rose {:.2} m", highest - start.z);
    assert!(left_ground, "the body left the ground");
    assert!(highest - start.z > 0.3, "rose {}", highest - start.z);

    assert!(on_ground && (end.z - start.z).abs() < 0.2, "landed again");
    let records = rig.movement.take_outbound_records();
    assert!(records.iter().any(|r| r.kind == OutboundKind::Jump));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_player_killer_status_reaches_the_controller() {
    // OpenAC `LiveSessionEventRouter` → `RuntimeMovementSkillState.
    // UpdatePlayerKillerStatus` → `PlayerMovementController.
    // SetPlayerKillerStatus`: unknown is -1, then the server's value.
    let mut rig = Rig::entered();
    assert_eq!(rig.movement.applied_pk, Some((-1, None)));
    rig.world.apply(&Inbound::PrivateUpdatePropertyInt(
        ac_proto::PrivateUpdatePropertyInt {
            sequence: 1,
            property: ac_core::properties::PropertyInt::PLAYER_KILLER_STATUS.0,
            value: 4,
        },
    ));
    rig.frames(1);
    assert_eq!(rig.movement.applied_pk, Some((4, None)));
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_pose_in_speech_plays_its_motion_and_the_server_hears_it() {
    // OpenAC `RuntimeLocalPlayerMovementState.ExecuteMotion`: the pose's
    // motion is asked of the controller as the player's own, and the
    // movement event carries it.
    let mut rig = Rig::entered();
    rig.frames(30);
    let _ = rig.movement.take_outbound_records();
    assert!(
        rig.movement
            .execute_motion(ac_motion::motion_command::WAVE, &rig.world)
    );
    rig.frames(1);
    let records = rig.movement.take_outbound_records();
    assert!(
        records.iter().any(|r| r.kind == OutboundKind::MoveToState),
        "{records:?}"
    );
    let mut no_body = Movement::new(true);
    assert!(!no_body.execute_motion(ac_motion::motion_command::WAVE, &rig.world));
}

#[test]
#[ignore = "needs the retail dat files"]
fn an_emote_key_plays_its_motion_and_the_server_hears_it() {
    // OpenAC `GameplayInputActionRouter.HandlePressedMovementAction`: an
    // emote action's motion goes to `ExecuteMotion`.
    let mut rig = Rig::entered();
    rig.frames(30);
    let _ = rig.movement.take_outbound_records();
    rig.command(MovementCommand::Motion(ac_motion::motion_command::CHEER));
    rig.frames(1);
    let records = rig.movement.take_outbound_records();
    assert!(
        records.iter().any(|r| r.kind == OutboundKind::MoveToState),
        "{records:?}"
    );
}

/// The body moves in fixed steps (30 a second) while a window redraws 60
/// times a second or more, on its own clock: the window draws the
/// character, and its camera follows it, between the last two steps by the
/// time since the last one ended at its own frame's time, so it moves on
/// every frame, between the session's passes too, instead of holding still
/// one frame and jumping the next. What it blends comes in the object's
/// view, in the body's cell; the view's place, like the world's (gameplay,
/// plugins, the server), is the body on its step.
#[test]
#[ignore = "needs the retail dat files"]
fn a_window_draws_the_running_character_between_its_steps() {
    let mut rig = Rig::entered();
    rig.command(run(2.0));
    rig.frames(30);
    let origin = |v: Vector3| Vec3::new(v.x, v.y, v.z);
    let mut between = 0;
    for _ in 0..10 {
        rig.frames(1);
        let c = rig.movement.controller().unwrap();
        let (body, smoothed) = (c.position(), c.render_position());
        let steps = c.render_steps();
        let outbound = super::outbound::wire_position(&c.try_get_outbound_position().unwrap());
        let live = rig
            .world
            .object(PLAYER)
            .unwrap()
            .position()
            .copied()
            .unwrap();
        assert_eq!(live, outbound, "the world keeps the body on its step");
        let view = rig
            .world
            .snapshot()
            .objects
            .into_iter()
            .find(|o| o.guid == PLAYER)
            .unwrap();
        assert_eq!(view.position, Some(live), "the view's place is the step");
        let blend = view.step_blend.expect("the view carries the blend");
        // At this pass's time the blend is the physics' smoothed place.
        let now = origin(blend.origin_at(rig.now)) - origin(live.origin);
        assert!(
            (now - (smoothed - body)).length() < 1e-4,
            "drawn {now} from the body, smoothed {}",
            smoothed - body
        );
        // A frame 8 ms later, before the next pass: further along, as the
        // physics would place it with that much more time carried.
        let later = Duration::from_millis(8);
        let alpha = ((steps.pending_seconds + later.as_secs_f64()) / f64::from(steps.step_seconds))
            .clamp(0.0, 1.0) as f32;
        let expected = steps.previous.lerp(steps.current, alpha) - body;
        let drawn = origin(blend.origin_at(rig.now + later)) - origin(live.origin);
        assert!(
            (drawn - expected).length() < 1e-4,
            "drawn {drawn} from the body 8 ms on, expected {expected}"
        );
        if (smoothed - body).length() > 0.01 {
            between += 1;
        }
    }
    assert!(between > 0, "running, the drawn place trails the step");
}

#[test]
#[ignore = "needs the retail dat files"]
fn held_keys_walk_and_letting_go_stops() {
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        walk: true,
        ..MovementKeys::default()
    }));
    rig.frames(120);
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(60);
    let (mid, _, _) = rig.body();
    rig.frames(60);
    let (end, _, _) = rig.body();
    let walked = (mid - start).truncate().length();
    println!("walked {walked:.2} m");
    // About 3.12 m/s for two seconds.
    assert!((4.0..8.0).contains(&walked), "walked {walked}");
    assert!((end - mid).truncate().length() < 0.05, "stopped");
}

/// OpenAC `DispatcherMovementInputSource.Capture`: `Run = (RunAsDefaultMovement
/// != walking) || AutoRunActive`. With the ToggleRun option off the held
/// keys walk and the walk key runs.
#[test]
#[ignore = "needs the retail dat files"]
fn with_run_as_default_off_the_keys_walk_and_the_walk_key_runs() {
    let distance = |run_as_default: bool, walk: bool| {
        let mut rig = Rig::entered();
        rig.movement.set_run_as_default(run_as_default);
        let (start, _, _) = rig.body();
        rig.command(MovementCommand::Keys(MovementKeys {
            forward: true,
            walk,
            ..MovementKeys::default()
        }));
        rig.frames(120);
        let (end, _, _) = rig.body();
        (end - start).truncate().length()
    };
    let walked = distance(true, true);
    let ran = distance(true, false);
    println!("default run: walk key {walked:.2} m, plain {ran:.2} m");
    assert!(ran > walked + 1.0, "the plain keys run: {ran} vs {walked}");
    let plain = distance(false, false);
    let with_walk_key = distance(false, true);
    println!("default walk: plain {plain:.2} m, walk key {with_walk_key:.2} m");
    assert!((plain - walked).abs() < 0.5, "{plain} vs {walked}");
    assert!(
        (with_walk_key - ran).abs() < 0.5,
        "{with_walk_key} vs {ran}"
    );
}

/// The Escape key's movement part (OpenAC
/// `GameplayInputActionRouter.ResolveEscapeMovementCommand` and
/// `RuntimeLocalPlayerMovementState.Execute`): a jump being charged ends
/// without a jump; a moving body stops completely and stays stopped.
#[test]
#[ignore = "needs the retail dat files"]
fn escape_ends_a_charged_jump_and_stops_a_moving_body() {
    let mut rig = Rig::entered();
    assert!(rig.movement.is_standing_still());
    assert!(!rig.movement.jump_charging());
    rig.command(MovementCommand::Keys(MovementKeys {
        jump: true,
        ..MovementKeys::default()
    }));
    rig.frames(10);
    assert!(rig.movement.jump_charging());
    rig.movement.finish_jump();
    assert!(!rig.movement.jump_charging());
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(30);
    assert!(rig.body().2, "never left the ground");

    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }));
    rig.frames(30);
    assert!(!rig.movement.is_standing_still());
    rig.movement.stop_completely(&rig.world);
    rig.frames(30);
    let (mid, _, _) = rig.body();
    rig.frames(30);
    let (end, _, _) = rig.body();
    assert!(rig.movement.is_standing_still());
    assert!((end - mid).truncate().length() < 0.05, "stopped");
}

/// The Escape key keeps the keys the player holds (OpenAC
/// `RuntimeLocalPlayerMovementState.Execute(StopCompletely)` ends autorun
/// and a plugin's held keys only; its window reads the keyboard every
/// frame): a forward key still down after Escape does not start the run
/// again when another key changes, and moves again only once pressed again.
#[test]
#[ignore = "needs the retail dat files"]
fn escape_keeps_the_held_keys_held() {
    let mut rig = Rig::entered();
    let forward = MovementKeys {
        forward: true,
        ..MovementKeys::default()
    };
    rig.command(MovementCommand::Keys(forward));
    rig.frames(30);
    assert!(!rig.movement.is_standing_still());
    rig.movement.stop_completely(&rig.world);
    rig.frames(30);
    // The window sends keys only when they change: the jump key goes down
    // with the forward key still held.
    rig.command(MovementCommand::Keys(MovementKeys {
        jump: true,
        ..forward
    }));
    rig.frames(30);
    let (mid, _, _) = rig.body();
    rig.frames(30);
    let (end, _, _) = rig.body();
    assert!(
        (end - mid).truncate().length() < 0.05,
        "the held forward key started the run again"
    );
}

/// The stop key while the forward key is held (OpenAC
/// `PlayerRunSidestepInstalledDatTests.TheStopKeyStopsARunWhileTheForwardKeyIsStillHeld`):
/// the forward axis goes back to ready and stays there while the key is
/// still down; pressed again, it moves again.
#[test]
#[ignore = "needs the retail dat files"]
fn the_stop_key_stops_a_run_while_the_forward_key_is_held() {
    let mut rig = Rig::entered();
    let forward = MovementKeys {
        forward: true,
        ..MovementKeys::default()
    };
    rig.command(MovementCommand::Keys(forward));
    rig.frames(30);
    assert!(!rig.movement.is_standing_still());
    // The window sends keys only when they change: the stop key alone.
    rig.command(MovementCommand::Stop);
    // The run settles into the ready pose first.
    rig.frames(30);
    let (mid, _, _) = rig.body();
    rig.frames(30);
    let (end, _, _) = rig.body();
    assert!(rig.movement.is_standing_still());
    assert!(
        (end - mid).truncate().length() < 0.05,
        "still moving with the forward key held"
    );
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(5);
    rig.command(MovementCommand::Keys(forward));
    rig.frames(30);
    let (again, _, _) = rig.body();
    assert!(
        (again - end).truncate().length() > 1.0,
        "pressed again, it moves"
    );
}

/// A posture key during a jump's charge (OpenAC
/// `RuntimeLocalPlayerMovementState.Execute(Crouch)`: the held keys come in
/// on their own): the charge goes on, no jump goes.
#[test]
#[ignore = "needs the retail dat files"]
fn a_posture_key_leaves_a_charging_jump_charging() {
    let mut rig = Rig::entered();
    rig.command(MovementCommand::Keys(MovementKeys {
        jump: true,
        ..MovementKeys::default()
    }));
    rig.frames(10);
    assert!(rig.movement.jump_charging());
    rig.command(MovementCommand::Posture(Posture::Crouch));
    rig.frames(10);
    assert!(rig.movement.jump_charging(), "the charge goes on");
    assert!(!rig.movement.controller().unwrap().is_airborne(), "no jump");
}

/// The stop key during a jump's charge (OpenAC
/// `RuntimeLocalPlayerMovementState.Execute(Stop)`: the keys the player
/// holds come in on their own): the charge goes on until the jump key is
/// released, then the jump goes.
#[test]
#[ignore = "needs the retail dat files"]
fn the_stop_key_leaves_a_charging_jump_charging() {
    let mut rig = Rig::entered();
    rig.command(MovementCommand::Keys(MovementKeys {
        jump: true,
        ..MovementKeys::default()
    }));
    rig.frames(10);
    assert!(rig.movement.jump_charging());
    // The window sends keys only when they change: the stop key alone.
    rig.command(MovementCommand::Stop);
    rig.frames(10);
    assert!(rig.movement.jump_charging(), "the charge goes on");
    assert!(
        !rig.movement.controller().unwrap().is_airborne(),
        "still on the ground"
    );
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(5);
    assert!(!rig.movement.jump_charging());
    assert!(
        rig.movement.controller().unwrap().is_airborne(),
        "jumped once the key was released"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_forced_position_moves_the_body_and_answers_at_once() {
    let mut rig = Rig::entered();
    let _ = rig.movement.take_outbound_records();
    let (start, _, _) = rig.body();
    let target = Vec3::new(start.x + 5.0, start.y, start.z);
    let z = ground_z(&content(), target.x, target.y);
    let force = Inbound::UpdatePosition(ac_proto::UpdatePosition {
        guid: PLAYER,
        flags: 0,
        position: Position {
            cell_id: outdoor_cell(target.x, target.y),
            origin: Vector3 {
                x: target.x,
                y: target.y,
                z,
            },
            orientation: Quaternion::IDENTITY,
        },
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: 2,
        teleport_sequence: 0,
        force_position_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&force);
    rig.movement
        .observe(&mut rig.world, &force, mark, &mut rig.outbox, rig.now);
    let (moved, _, _) = rig.body();
    assert!((moved.truncate() - target.truncate()).length() < 0.05);
    let records = rig.movement.take_outbound_records();
    assert_eq!(records.len(), 1);
    assert_eq!(records[0].kind, OutboundKind::AutonomousPosition);
    assert!(records[0].contact);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_turn_to_a_heading_goes_through_the_move_to_manager() {
    let mut rig = Rig::entered();
    rig.command(MovementCommand::TurnToHeading(270.0));
    assert!(
        !rig.movement
            .drain_events()
            .iter()
            .any(|e| matches!(e, MovementEvent::Refused(_))),
        "the turn was taken"
    );
    rig.frames(240);
    let (_, heading, _) = rig.body();
    assert!((heading - 270.0).abs() < 1.0, "heading {heading}");
}

#[test]
fn a_first_person_turn_step_wraps_the_compass() {
    // RetailChaseCamera.FirstPersonTurnTargetHeading.
    assert_eq!(super::first_person_turn_target_heading(90.0, 8.0), 98.0);
    assert_eq!(super::first_person_turn_target_heading(4.0, -8.0), 356.0);
    assert_eq!(super::first_person_turn_target_heading(356.0, 8.0), 4.0);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_first_person_rotate_turns_the_character_without_a_movement_message_of_its_own() {
    // CameraFrameControllerTests.FirstPersonKeypadRotate_TurnsTheCharacterAndKeepsTheHeadView
    // and FirstPersonRotate_DoesNotRequestAMovementEventOfItsOwn:
    // the step turns from the current heading; the turn sends no
    // move-to-state of its own (a held key re-issues it every frame).
    let mut rig = Rig::entered();
    rig.frames(2);
    rig.movement.take_outbound_records();
    let (_, start, _) = rig.body();
    rig.command(MovementCommand::FirstPersonTurn(8.0));
    rig.frames(1);
    assert!(
        !rig.movement
            .take_outbound_records()
            .iter()
            .any(|r| r.kind == OutboundKind::MoveToState),
        "no movement message for the turn"
    );
    rig.frames(30);
    let (_, heading, _) = rig.body();
    let turned = (heading - start + 540.0) % 360.0 - 180.0;
    assert!(turned > 1.0, "heading moved {turned} degrees");
}

/// A server object with a setup collides where the server placed it (as
/// OpenAC's client registers it), and stops colliding once the server turns
/// it ethereal, as a door does when it opens.
#[test]
#[ignore = "needs the retail dat files"]
fn a_server_object_stands_in_the_way_until_it_turns_ethereal() {
    const POST: u32 = 0x7000_0123;
    let mut rig = Rig::entered();
    let (start, heading, _) = rig.body();
    assert!(
        heading.abs() < 1.0 || (heading - 360.0).abs() < 1.0,
        "faces north"
    );
    let at = Vec3::new(start.x, start.y + 4.0, start.z);
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(at.x, at.y),
            origin: Vector3 {
                x: at.x,
                y: at.y,
                z: ground_z(&content(), at.x, at.y),
            },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid: POST,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    let mark = rig.world.change_count();
    rig.world.apply(&create);
    rig.movement
        .observe(&mut rig.world, &create, mark, &mut rig.outbox, rig.now);
    let local = rig.world.object(POST).unwrap().key().local_id;
    assert!(rig.movement.collision.is_registered(local));

    rig.command(run(1.5));
    rig.frames(150);
    let (stopped, _, _) = rig.body();
    let gap = at.y - stopped.y;
    println!("stopped {gap:.2} m short of the object");
    assert!(gap > 0.5, "the object stops the body short of it: {gap}");

    let open = Inbound::SetState(ac_proto::SetState {
        guid: POST,
        physics_state: PhysicsState::ETHEREAL.bits(),
        instance_sequence: 1,
        state_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&open);
    rig.movement
        .observe(&mut rig.world, &open, mark, &mut rig.outbox, rig.now);
    assert!(
        rig.movement
            .engine()
            .shadows
            .capture_entries_for(local)
            .iter()
            .all(|e| e.state == PhysicsState::ETHEREAL.bits())
    );
    // A body pressed straight against what stopped it keeps sliding along
    // that face until it moves any other way (as the game adjusts it): a
    // short sidestep frees it.
    rig.command(MovementCommand::Move(MoveRequest {
        direction: MoveDirection::StrafeRight,
        pace: MovePace::Run,
        amount: 0.2,
        unit: MoveUnit::Seconds,
    }));
    rig.frames(30);
    rig.command(run(1.5));
    rig.frames(150);
    let (through, _, _) = rig.body();
    println!("ran on to {:.2} m past it", through.y - at.y);
    assert!(through.y > at.y + 2.0, "an ethereal object stops nothing");

    let leave = Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: POST,
        instance_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&leave);
    rig.movement
        .observe(&mut rig.world, &leave, mark, &mut rig.outbox, rig.now);
    assert!(!rig.movement.collision.is_registered(local));
}

/// A server object with no motion (so nothing simulates it) `metres`
/// north of the body.
fn place_still_object(rig: &mut Rig, guid: u32, metres: f32) -> Vec3 {
    let (start, _, _) = rig.body();
    let at = Vec3::new(start.x, start.y + metres, start.z);
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(at.x, at.y),
            origin: Vector3 {
                x: at.x,
                y: at.y,
                z: ground_z(&content(), at.x, at.y),
            },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    let mark = rig.world.change_count();
    rig.world.apply(&create);
    rig.movement
        .observe(&mut rig.world, &create, mark, &mut rig.outbox, rig.now);
    at
}

fn apply(rig: &mut Rig, message: &Inbound<'_>) {
    let mark = rig.world.change_count();
    rig.world.apply(message);
    rig.movement
        .observe(&mut rig.world, message, mark, &mut rig.outbox, rig.now);
}

/// OpenAC `RuntimeServerControlledLocalMovement.TryApply`: the server's
/// move-to names a thing nothing simulates here (a chest, a corpse); the
/// character follows that thing through the same minimal stand-in body
/// (`RuntimePhysicsState.ResolveObjectTableHost`) a walk it starts gets,
/// not the place the order was written at.
#[test]
#[ignore = "needs the retail dat files"]
fn a_server_move_to_a_thing_nothing_simulates_follows_the_thing() {
    const CHEST: u32 = 0x7000_0456;
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    let chest = place_still_object(&mut rig, CHEST, 6.0);
    // The order is written at a place 8 m south: the wrong way.
    let written = Vec3::new(start.x, start.y - 8.0, start.z);
    let order = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: 2,
        server_control_sequence: 1,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 6,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::MoveToObject {
                target: CHEST,
                path: ac_proto::MoveToPath {
                    origin_cell_id: outdoor_cell(written.x, written.y),
                    origin: Vector3 {
                        x: written.x,
                        y: written.y,
                        z: written.z,
                    },
                    bitfield: 0x403,
                    distance_to_object: 1.0,
                    min_distance: 0.0,
                    fail_distance: f32::MAX,
                    speed: 1.0,
                    walk_run_threshold: 15.0,
                    desired_heading: 0.0,
                    run_rate: 1.0,
                },
            },
        },
    }));
    apply(&mut rig, &order);
    let move_to = &rig.movement.controller().unwrap().object().move_to;
    assert!(move_to.is_moving_to());
    assert_eq!(move_to.top_level_object_id, CHEST);
    rig.frames(300);
    let (end, _, _) = rig.body();
    println!(
        "ended {:.2} m from the chest, {:.2} m from the written place",
        (end - chest).truncate().length(),
        (end - written).truncate().length()
    );
    assert!(end.y > start.y + 2.0, "walked north, toward the chest");
    // The gap that ends the walk (1 m) is between the two bodies' cylinders.
    assert!((end - chest).truncate().length() < 2.5);
}

/// The tester's spot in Yanshi (#303): a landblock away from Holtburg's,
/// so a teleport there waits for its collision to load.
const YANSHI: Position = Position {
    cell_id: 0xB46F_001E,
    origin: Vector3 {
        x: 75.2,
        y: 124.1,
        z: 34.688_33,
    },
    orientation: Quaternion::IDENTITY,
};

/// A portal arrival no window holds waits for its destination's
/// collision. The server's position has put the character there, so the
/// body waits there too, in portal space, from the moment the position
/// comes: everything said of where the character is meanwhile (the
/// positions sent to the server among them) is the destination, never the
/// spot it left. Once the collision is in, it is placed there.
#[test]
#[ignore = "needs the retail dat files"]
fn a_body_waiting_for_its_destination_waits_there() {
    let mut rig = Rig::entered();
    let left = rig
        .movement
        .controller()
        .unwrap()
        .current_cell_position()
        .obj_cell_id;
    let _ = rig.movement.take_outbound_records();
    apply(
        &mut rig,
        &Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        }),
    );
    rig.movement.hold_destination();
    apply(
        &mut rig,
        &Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid: PLAYER,
            flags: 0,
            position: YANSHI,
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence: 2,
            teleport_sequence: 1,
            force_position_sequence: 0,
        }),
    );
    let at = rig.movement.controller().unwrap().current_cell_position();
    assert_eq!(at.obj_cell_id, YANSHI.cell_id, "the body waits there");
    let origin = Vec3::new(YANSHI.origin.x, YANSHI.origin.y, YANSHI.origin.z);
    assert!((at.frame.origin - origin).length() < 0.01, "{at:?}");
    assert_eq!(
        rig.movement.controller().unwrap().state,
        PlayerState::PortalSpace
    );
    rig.frames(30);
    assert_eq!(
        rig.world
            .object(PLAYER)
            .and_then(|o| o.position())
            .map(|p| p.cell_id),
        Some(YANSHI.cell_id),
        "the world has the character there"
    );
    let sent = rig.movement.take_outbound_records();
    assert!(
        sent.iter().all(|r| r.position.cell_id != left),
        "nothing names the spot it left: {sent:?}"
    );
    assert!(
        sent.iter()
            .any(|r| r.kind == OutboundKind::AutonomousPosition
                && r.position.cell_id == YANSHI.cell_id),
        "the server hears the destination: {sent:?}"
    );

    let mut passes = 0;
    while !rig
        .movement
        .prepare_destination(&mut rig.world, &mut rig.outbox)
    {
        passes += 1;
        assert!(passes < 200, "the destination loads");
    }
    rig.movement.arrive();
    rig.frames(1);
    let placed = rig.movement.controller().unwrap();
    assert_eq!(placed.state, PlayerState::InWorld);
    assert_eq!(placed.current_cell_position().obj_cell_id, YANSHI.cell_id);
}

/// While an attached window holds a portal arrival the body is placed at
/// the destination but stays in portal space (OpenAC's window keeps the
/// player in portal space until the presentation's end,
/// `PlayerModeController.EnterWorld`); it moves again once it arrives.
#[test]
#[ignore = "needs the retail dat files"]
fn a_held_arrival_keeps_the_placed_body_in_portal_space_until_it_arrives() {
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    apply(
        &mut rig,
        &Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        }),
    );
    rig.movement.set_arrival_held(true);
    let to = Vec3::new(start.x + 20.0, start.y, start.z);
    let teleport = Inbound::UpdatePosition(ac_proto::UpdatePosition {
        guid: PLAYER,
        flags: 0,
        position: Position {
            cell_id: outdoor_cell(to.x, to.y),
            origin: Vector3 {
                x: to.x,
                y: to.y,
                z: ground_z(&content(), to.x, to.y),
            },
            orientation: Quaternion::IDENTITY,
        },
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: 2,
        teleport_sequence: 1,
        force_position_sequence: 0,
    });
    apply(&mut rig, &teleport);
    let (placed, _, _) = rig.body();
    assert!(
        (placed.truncate() - to.truncate()).length() < 0.05,
        "placed"
    );
    assert_eq!(
        rig.movement.controller().unwrap().state,
        PlayerState::PortalSpace
    );
    rig.command(run(1.0));
    rig.frames(90);
    let (held, _, _) = rig.body();
    assert!((held - placed).truncate().length() < 0.01, "does not move");

    rig.movement.arrive();
    assert_eq!(
        rig.movement.controller().unwrap().state,
        PlayerState::InWorld
    );
    rig.command(run(1.0));
    rig.frames(90);
    let (moved, _, _) = rig.body();
    assert!((moved - placed).truncate().length() > 1.0, "moves again");
}

/// A creature beside the entered character, with its motion sent (so it
/// gets a body).
fn creature_beside(rig: &mut Rig, guid: u32, dx: f32, weenie: Option<ac_proto::PublicWeenieDesc>) {
    creature_created_in(
        rig,
        guid,
        dx,
        weenie,
        ac_proto::InterpretedMotionState::default(),
    );
}

/// A creature beside the entered character, created in `state` (in the
/// NonCombat stance).
fn creature_created_in(
    rig: &mut Rig,
    guid: u32,
    dx: f32,
    weenie: Option<ac_proto::PublicWeenieDesc>,
    state: ac_proto::InterpretedMotionState,
) {
    let (x, y) = SPOT;
    let z = ground_z(rig.movement.content.as_ref().unwrap(), x + dx, y);
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            movement: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(x + dx, y),
            origin: Vector3 { x: x + dx, y, z },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    physics.motion_table_id = Some(HUMAN_MOTION);
    physics.state = 0x0000_0408;
    physics.movement = Some(ac_proto::PhysicsMovement::Present {
        raw: vec![0; 4],
        data: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state,
                sticky_object: None,
            },
        },
        is_autonomous: false,
    });
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie,
    }));
    let mark = rig.world.change_count();
    rig.world.apply(&create);
    rig.movement
        .observe(&mut rig.world, &create, mark, &mut rig.outbox, rig.now);
}

/// A new object enters the world in the motion it was created in, the
/// links into that motion dropped (OpenAC `SpawnMotionInitializer.Create`:
/// `InitializeState`, `SetCycle`, then `MotionTableManager
/// .HandleEnterWorld`). A corpse the server creates dead (its current
/// motion state NonCombat, Dead) is in the dead cycle at once,
/// waiting on no fall; a creature first seen walking walks at once, with
/// no stand-to-walk link before its cycle.
#[test]
#[ignore = "needs the retail dat files"]
fn a_new_object_enters_the_world_in_its_motion_without_its_links() {
    use ac_motion::motion_command::{DEAD, WALK_FORWARD};
    const CORPSE: u32 = 0x7000_0401;
    const WALKER: u32 = 0x7000_0402;
    let mut rig = Rig::entered();
    let created_in = |forward: u32| ac_proto::InterpretedMotionState {
        current_style: Some(0x003D),
        forward_command: Some((forward & 0xFFFF) as u16),
        forward_speed: Some(1.0),
        ..ac_proto::InterpretedMotionState::default()
    };
    creature_created_in(&mut rig, CORPSE, 3.0, None, created_in(DEAD));
    creature_created_in(&mut rig, WALKER, -3.0, None, created_in(WALK_FORWARD));
    for (guid, motion) in [(CORPSE, DEAD), (WALKER, WALK_FORWARD)] {
        let body = rig
            .movement
            .remotes
            .values()
            .find(|body| body.guid == guid)
            .unwrap_or_else(|| panic!("{guid:#X} has a body"));
        let driver = &body.driver;
        assert_eq!(
            driver.obj.interp.interpreted_state.forward_command, motion,
            "{guid:#X}: its interpreter is in the motion it was created in"
        );
        assert_eq!(
            driver.obj.interp.pending_motion_count(),
            0,
            "{guid:#X}: its interpreter waits on no motion"
        );
        let sequencer = driver.obj.sequencer.as_ref().expect("a sequencer");
        assert_eq!(sequencer.current_motion(), motion, "{guid:#X}");
        assert_eq!(
            sequencer.manager().pending_animations().len(),
            0,
            "{guid:#X}: its motion table waits on no animation"
        );
        let sequence = sequencer.sequence();
        assert!(sequence.curr_anim_index().is_some(), "{guid:#X} animates");
        assert_eq!(
            sequence.curr_anim_index(),
            sequence.first_cyclic_index(),
            "{guid:#X}: it plays its cycle, not a link into it"
        );
        assert_eq!(
            sequence.first_cyclic_index(),
            Some(0),
            "{guid:#X}: no link is queued before its cycle"
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_vector_update_throws_a_remote_body_into_the_air() {
    // OpenAC `LiveEntityNetworkUpdateController.OnVector`: a remote body
    // takes the server's velocity, and one rising faster than 0.5 m/s
    // leaves the ground.
    const CREATURE: u32 = 0x7000_0301;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 4.0, None);
    rig.frames(2);
    let local = rig.world.object(CREATURE).unwrap().key().local_id;
    let before = rig.movement.remotes[&local].driver.obj.body.position();
    let vector = Inbound::VectorUpdate(ac_proto::VectorUpdate {
        guid: CREATURE,
        velocity: Vector3 {
            x: 0.0,
            y: 0.0,
            z: 6.0,
        },
        omega: Vector3::default(),
        instance_sequence: 1,
        vector_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&vector);
    rig.movement
        .observe(&mut rig.world, &vector, mark, &mut rig.outbox, rig.now);
    assert!(rig.movement.remotes[&local].driver.airborne);
    rig.frames(10);
    let after = rig.movement.remotes[&local].driver.obj.body.position();
    println!("rose {:.2} m", after.z - before.z);
    assert!(after.z > before.z + 0.5, "{before} -> {after}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_remote_teleport_runs_the_hook_and_places_the_body_at_its_destination() {
    // OpenAC `LiveEntityNetworkUpdateController.RunRemoteTeleportHook` and
    // `LiveEntityNetworkRemoteTeleportPresentationTests.TeleportCommit_*`:
    // a position whose teleport counter moved on runs the hook (the
    // interpolation queue goes) and places the body where the server says.
    const CREATURE: u32 = 0x7000_0302;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 4.0, None);
    rig.frames(2);
    let local = rig.world.object(CREATURE).unwrap().key().local_id;
    let (x, y) = SPOT;
    let content = content();
    let step = |rig: &mut Rig, dx: f32, position_sequence: u16, teleport_sequence: u16| {
        let z = ground_z(&content, x + dx, y);
        let update = Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid: CREATURE,
            flags: ac_proto::POSITION_IS_GROUNDED,
            position: Position {
                cell_id: outdoor_cell(x + dx, y),
                origin: Vector3 { x: x + dx, y, z },
                orientation: Quaternion::IDENTITY,
            },
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence,
            teleport_sequence,
            force_position_sequence: 0,
        });
        let mark = rig.world.change_count();
        rig.world.apply(&update);
        rig.movement
            .observe(&mut rig.world, &update, mark, &mut rig.outbox, rig.now);
    };
    // The body is stuck to something (as a creature chasing a target).
    {
        use ac_physics::movement::PositionOps;
        let driver = &mut rig.movement.remotes.get_mut(&local).unwrap().driver;
        driver
            .obj
            .cx(&ac_physics::movement::NoTargets)
            .stick_to(PLAYER, 0.5, 1.0);
        assert_eq!(driver.obj.position.get_sticky_object_id(), PLAYER);
    }
    // A teleport 20 m on.
    step(&mut rig, 24.0, 2, 1);
    let driver = &rig.movement.remotes[&local].driver;
    assert_eq!(driver.obj.position.get_sticky_object_id(), 0);
    assert!(!driver.interpolation.is_active());
    let at = driver.obj.body.position();
    assert!((at.x - (x + 24.0)).abs() < 0.05, "{at}");
    assert!(
        driver
            .obj
            .position
            .constraint
            .is_some_and(|c| c.is_constrained)
    );
}

/// A missile with setup 0x02000124 (one sphere; OpenAC's projectile
/// fixture) at `height` metres above the ground beside the character,
/// flying at `velocity`.
fn missile_beside(rig: &mut Rig, guid: u32, height: f32, velocity: Vector3) {
    // Missile, report collisions, align path, path clipped, gravity.
    object_beside(rig, guid, height, velocity, 0x0000_0748);
}

/// An object of setup 0x02000124 with physics state `state`, `height` metres
/// above the ground beside the character, moving at `velocity`.
fn object_beside(rig: &mut Rig, guid: u32, height: f32, velocity: Vector3, state: u32) {
    let (x, y) = SPOT;
    let z = ground_z(rig.movement.content.as_ref().unwrap(), x + 2.0, y) + height;
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(x + 2.0, y),
            origin: Vector3 { x: x + 2.0, y, z },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(0x0200_0124);
    physics.state = state;
    physics.velocity = Some(velocity);
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    let mark = rig.world.change_count();
    rig.world.apply(&create);
    rig.movement
        .observe(&mut rig.world, &create, mark, &mut rig.outbox, rig.now);
}

fn live_of(rig: &Rig, guid: u32) -> Vec3 {
    let o = rig.world.object(guid).unwrap().position().unwrap().origin;
    Vec3::new(o.x, o.y, o.z)
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_missile_flies_by_its_velocity_and_falls() {
    // OpenAC `ProjectileControllerTests.Tick_MovesCanonicalProjectionWithoutRecreatingResources`
    // and `RuntimeProjectilePhysicsUpdater`: between the server's updates a
    // missile flies on its own physics, its velocity and gravity.
    const ARROW: u32 = 0x8000_0401;
    let mut rig = Rig::entered();
    missile_beside(
        &mut rig,
        ARROW,
        3.0,
        Vector3 {
            x: 10.0,
            y: 0.0,
            z: 0.0,
        },
    );
    let start = live_of(&rig, ARROW);
    rig.frames(12);
    let flown = live_of(&rig, ARROW);
    println!("{start} -> {flown}");
    assert!(flown.x > start.x + 1.0, "{start} -> {flown}");
    assert!(
        flown.z < start.z,
        "gravity pulls it down: {start} -> {flown}"
    );

    // The server's velocity replaces the flight's.
    let vector = Inbound::VectorUpdate(ac_proto::VectorUpdate {
        guid: ARROW,
        velocity: Vector3 {
            x: -10.0,
            y: 0.0,
            z: 0.0,
        },
        omega: Vector3::default(),
        instance_sequence: 1,
        vector_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&vector);
    rig.movement
        .observe(&mut rig.world, &vector, mark, &mut rig.outbox, rig.now);
    rig.frames(12);
    let back = live_of(&rig, ARROW);
    assert!(back.x < flown.x - 1.0, "{flown} -> {back}");

    // Deleting it ends the flight.
    let delete = Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: ARROW,
        instance_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&delete);
    rig.movement
        .observe(&mut rig.world, &delete, mark, &mut rig.outbox, rig.now);
    assert!(rig.movement.projectiles.is_empty());
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_player_killer_status_update_refreshes_the_registered_collision_flags() {
    // OpenAC `LiveEntityPvpBitfieldSyncTests.ObjectUpdated_WithLiveBitfield_
    // RefreshesRegisteredTargetFlags`: the registered rows take the player-
    // killer bits of a live status update; the other bits stay.
    const OTHER: u32 = 0x5000_0310;
    let mut rig = Rig::entered();
    creature_beside(
        &mut rig,
        OTHER,
        4.0,
        Some(ac_proto::PublicWeenieDesc {
            name: "Horan".into(),
            object_description_flags: 0x0000_0008, // a player
            ..ac_proto::PublicWeenieDesc::default()
        }),
    );
    let local = rig.world.object(OTHER).unwrap().key().local_id;
    assert!(rig.movement.collision.is_registered(local));
    let flags = |rig: &Rig| {
        rig.movement
            .engine()
            .shadows
            .capture_entries_for(local)
            .first()
            .map(|e| e.flags)
            .unwrap()
    };
    assert_eq!(
        flags(&rig),
        EntityCollisionFlags(EntityCollisionFlags::HAS_WEENIE | EntityCollisionFlags::IS_PLAYER)
    );
    let update = Inbound::PublicUpdatePropertyInt(ac_proto::PublicUpdatePropertyInt {
        sequence: 1,
        guid: OTHER,
        property: 134, // PlayerKillerStatus
        value: ac_core::items::pk_status::PK_LITE,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&update);
    rig.movement
        .observe(&mut rig.world, &update, mark, &mut rig.outbox, rig.now);
    assert_eq!(
        flags(&rig),
        EntityCollisionFlags(
            EntityCollisionFlags::HAS_WEENIE
                | EntityCollisionFlags::IS_PLAYER
                | EntityCollisionFlags::IS_PK_LITE
        )
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_characters_own_rows_follow_it_so_other_bodies_meet_it() {
    // OpenAC `LocalPlayerShadowSynchronizer.SyncPose`: the player is
    // registered like any object and its rows follow the body once it moved
    // more than a centimetre; a hidden player keeps its rows (OpenAC
    // `RuntimePlacementShadowCompositionTests
    // .PortalPlace_HiddenPlayerKeepsItsCollisionAtTheDestination`).
    let mut rig = Rig::entered();
    let local = rig.world.object(PLAYER).unwrap().key().local_id;
    assert!(rig.movement.collision.is_registered(local));
    let rows_at = |rig: &Rig| {
        rig.movement
            .engine()
            .shadows
            .capture_entries_for(local)
            .first()
            .map(|e| e.position)
    };
    let (start, _, _) = rig.body();
    let first = rows_at(&rig).expect("registered in its cells");
    assert!(
        first.distance(start) < 2.0,
        "rows at the body: {first} {start}"
    );

    rig.command(run(1.0));
    rig.frames(90);
    let (moved, _, _) = rig.body();
    assert!(moved.distance(start) > 2.0, "the character ran");
    let followed = rows_at(&rig).unwrap();
    assert!(
        followed.distance(moved) < 2.0 && followed.distance(first) > 2.0,
        "the rows followed: {first} -> {followed}, body {moved}"
    );

    let hide = Inbound::SetState(ac_proto::SetState {
        guid: PLAYER,
        physics_state: 0x0000_4408, // hidden
        instance_sequence: 1,
        state_sequence: 1,
    });
    let mark = rig.world.change_count();
    rig.world.apply(&hide);
    rig.movement
        .observe(&mut rig.world, &hide, mark, &mut rig.outbox, rig.now);
    rig.frames(1);
    let hidden = rows_at(&rig).expect("a hidden character keeps its rows");
    assert!(
        hidden.distance(moved) < 2.0,
        "its rows stay at the body: {hidden}, body {moved}"
    );
    assert!(rig.movement.collision.is_registered(local));
}

/// The server's lifestone: its setup and physics state (gravity, ignore
/// collisions).
const LIFESTONE: u32 = 0x7AA0_C34F;
const LIFESTONE_SETUP: u32 = 0x0200_02EE;
const LIFESTONE_STATE: u32 = 0x0000_0410;
/// The server's teleporting character: hidden, ignore collisions, gravity.
const PORTAL_SPACE_STATE: u32 = 0x0000_4410;

fn lifestone_at(rig: &mut Rig, position: Position) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(position),
    );
    physics.setup_id = Some(LIFESTONE_SETUP);
    physics.state = LIFESTONE_STATE;
    apply(
        rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid: LIFESTONE,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
}

/// The character's teleport to `to` (its notice, then the position), the
/// arrival held.
fn teleport_to(rig: &mut Rig, to: Position) {
    apply(
        rig,
        &Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        }),
    );
    rig.movement.set_arrival_held(true);
    apply(
        rig,
        &Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid: PLAYER,
            flags: 0,
            position: to,
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence: 2,
            teleport_sequence: 1,
            force_position_sequence: 0,
        }),
    );
}

/// OpenAC `RuntimePlacementShadowCompositionTests
/// .PortalPlace_HiddenPlayerKeepsItsCollisionAtTheDestination`: the server
/// hides the character for the whole portal-space stay; hidden first, the
/// teleport's placement still leaves its rows at the destination.
#[test]
#[ignore = "needs the retail dat files"]
fn a_hidden_character_keeps_its_collision_at_the_portal_destination() {
    let mut rig = Rig::entered();
    set_state(&mut rig, PORTAL_SPACE_STATE, 1);
    rig.frames(1);
    let to = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 20.0, SPOT.1);
    teleport_to(&mut rig, to);
    rig.frames(1);
    let destination = Vec2::new(to.origin.x, to.origin.y);
    let rows = rows_at(&rig, PLAYER).expect("the hidden character keeps its rows");
    assert!(
        rows.distance(destination) < 0.05,
        "its rows stand at the destination: {rows}, {destination}"
    );
}

/// OpenAC `RuntimePlacementShadowCompositionTests
/// .PortalPlace_ThenHide_PlayerKeepsItsCollisionAtTheDestination`: the
/// server sends the teleport's position before the hidden state, so the
/// placement lands a frame before the hide; the hide does not take the
/// placed rows away.
#[test]
#[ignore = "needs the retail dat files"]
fn hiding_after_the_portal_placement_keeps_the_characters_collision() {
    let mut rig = Rig::entered();
    let to = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 20.0, SPOT.1);
    teleport_to(&mut rig, to);
    rig.frames(1);
    let destination = Vec2::new(to.origin.x, to.origin.y);
    let placed = rows_at(&rig, PLAYER).expect("placed at the destination");
    assert!(placed.distance(destination) < 0.05, "placed: {placed}");
    set_state(&mut rig, PORTAL_SPACE_STATE, 1);
    rig.frames(1);
    let rows = rows_at(&rig, PLAYER).expect("the hide keeps the character's rows");
    assert!(
        rows.distance(destination) < 0.05,
        "its rows stay at the destination: {rows}, {destination}"
    );
}

/// OpenAC `LifestoneLateCreateOverlapInstalledDatTests
/// .LifestoneArrivingOnAHiddenPlayer_SlidesClearAndThePlayerCanWalk`: a
/// recall places the character before the server creates the lifestone it
/// arrives on. The hidden character still blocks, so the stone's create
/// placement (placement | slide) puts it clear of the character, and the
/// character walks off once it arrives.
#[test]
#[ignore = "needs the retail dat files"]
fn a_lifestone_created_on_a_hidden_character_slides_clear_and_it_can_walk() {
    let mut rig = Rig::entered();
    let to = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 20.0, SPOT.1);
    teleport_to(&mut rig, to);
    set_state(&mut rig, PORTAL_SPACE_STATE, 1);
    rig.frames(1);
    let (placed, _, _) = rig.body();
    lifestone_at(&mut rig, to);
    let stone = shown_at(&rig, LIFESTONE).expect("the lifestone entered the world");
    let apart = stone.distance(placed.truncate());
    println!("stone {stone:.3}, character {placed:.3}, apart {apart:.3}");
    assert!(
        apart > 2.0,
        "the stone slid clear of the character: {stone}, {placed}"
    );

    rig.movement.arrive();
    set_state(&mut rig, 0x0000_0408, 2);
    rig.frames(1);
    rig.command(run(1.0));
    rig.frames(90);
    let (walked, _, _) = rig.body();
    assert!(
        (walked - placed).truncate().length() > 0.4,
        "the character walks off: {placed} -> {walked}"
    );
}

/// OpenAC `LifestoneLateCreateOverlapInstalledDatTests
/// .LifestoneKnownBeforeThePlacement_PlacesThePlayerBesideIt`: with the
/// stone already there, the teleport's placement puts the character beside
/// it.
#[test]
#[ignore = "needs the retail dat files"]
fn a_lifestone_known_before_the_teleport_has_the_character_placed_beside_it() {
    let mut rig = Rig::entered();
    let to = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 20.0, SPOT.1);
    lifestone_at(&mut rig, to);
    teleport_to(&mut rig, to);
    let (placed, _, _) = rig.body();
    let spot = Vec2::new(to.origin.x, to.origin.y);
    println!("character {placed:.3}, spot {spot:.3}");
    assert!(
        placed.truncate().distance(spot) > 2.0,
        "placed beside the stone: {placed}, {spot}"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn mouse_look_turns_the_character_and_tells_the_server_on_each_toggle() {
    // OpenAC `MouseLookController.Begin`/`EndAndRestoreCursor` with
    // `PlayerMouseLookMovementTests`: the toggles send the movement at once;
    // a negative adjustment turns right through the motion interpreter.
    let mut rig = Rig::entered();
    rig.frames(10);
    let _ = rig.movement.take_outbound_records();
    let (_, heading_before, _) = rig.body();
    rig.command(MovementCommand::BeginMouseLook);
    let begun = rig.movement.take_outbound_records();
    assert!(
        begun.iter().any(|r| r.kind == OutboundKind::MoveToState),
        "beginning mouse look sends the movement: {begun:?}"
    );
    for _ in 0..30 {
        rig.command(MovementCommand::MouseTurn(-0.5));
        rig.frames(1);
    }
    let (_, heading_after, _) = rig.body();
    let turned = (heading_after - heading_before).rem_euclid(360.0);
    println!("turned {turned:.1} degrees");
    assert!(
        turned > 5.0 && turned < 180.0,
        "a negative adjustment turns right (compass heading grows): {turned}"
    );
    rig.command(MovementCommand::StopMouseDrift);
    rig.frames(5);
    let (_, settled, _) = rig.body();
    rig.frames(15);
    let (_, still, _) = rig.body();
    assert!(
        (still - settled).abs() < 0.5,
        "the drift stopped: {settled} {still}"
    );
    let _ = rig.movement.take_outbound_records();
    rig.command(MovementCommand::EndMouseLook);
    let ended = rig.movement.take_outbound_records();
    assert!(ended.iter().any(|r| r.kind == OutboundKind::MoveToState));
    rig.command(MovementCommand::EndMouseLook);
    assert!(
        rig.movement.take_outbound_records().is_empty(),
        "a second end sends nothing"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn an_ordinary_object_given_a_velocity_moves_falls_and_lands() {
    // OpenAC `LiveEntityOrdinaryPhysicsUpdaterTests` through the session:
    // an object with no motion that is not a missile, given a velocity,
    // moves by its own physics, gravity bringing it down to the ground.
    const THROWN: u32 = 0x8000_0402;
    let mut rig = Rig::entered();
    // Not a missile: gravity only.
    object_beside(
        &mut rig,
        THROWN,
        3.0,
        Vector3 {
            x: 2.0,
            y: 0.0,
            z: 0.0,
        },
        0x0000_0400,
    );
    assert!(rig.movement.projectiles.is_empty());
    assert_eq!(rig.movement.ordinaries.len(), 1);
    let start = live_of(&rig, THROWN);
    rig.frames(90);
    let landed = live_of(&rig, THROWN);
    let (x, y) = (landed.x, landed.y);
    let ground = ground_z(rig.movement.content.as_ref().unwrap(), x, y);
    assert!(landed.x > start.x + 0.5, "{start} -> {landed}");
    assert!(landed.z < start.z - 1.0, "{start} -> {landed}");
    assert!(landed.z >= ground - 0.05, "{landed} above {ground}");
}

/// The server's run skill, then its stamina, as the wire carries them.
fn skill_update(skill: u32, ranks: u16) -> Inbound<'static> {
    Inbound::PrivateUpdateSkill(ac_proto::PrivateUpdateSkill {
        sequence: 1,
        skill,
        ranks,
        adjust_pp: 1,
        advancement_class: 2,
        xp: 0,
        init: 0,
        resistance: 0,
        last_used: 0.0,
    })
}

fn stamina_update(current: u32) -> Inbound<'static> {
    Inbound::PrivateUpdateVital(ac_proto::PrivateUpdateVital {
        sequence: 1,
        vital: 4,
        ranks: 0,
        start: 100,
        xp: 0,
        current,
    })
}

/// Hold forward (running) for half a second and answer how far the body
/// got, then let go and settle.
fn run_half_a_second(rig: &mut Rig) -> f32 {
    let (from, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }));
    rig.frames(30);
    let (to, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(60);
    (to - from).truncate().length()
}

/// OpenAC `RuntimeMovementStatsApplier.Apply` (and the parity scenarios of
/// `MovementStatsParityTests`): what the server says of the run and jump
/// skills and the stamina reaches the controller's weenie, nothing before
/// both skills are known, and a better runner covers more ground while an
/// exhausted one runs as though it had no skill. Run rates from OpenAC
/// `MovementSystem.GetRunRate` at no load: skill 10 →
/// (10/210·11 + 4)/4 = 1.1310, skill 300 → (300/500·11 + 4)/4 = 2.65, no
/// skill → 1.0.
#[test]
#[ignore = "needs the retail dat files"]
fn the_servers_run_skill_and_stamina_set_how_fast_the_character_runs() {
    let mut rig = Rig::entered();
    rig.world
        .apply_player_description(&crate::world::tests::empty_description());
    rig.frames(1);
    let rate = |rig: &Rig| {
        use ac_physics::movement::WeenieObject;
        rig.movement
            .controller()
            .unwrap()
            .object()
            .weenie
            .inq_run_rate()
            .unwrap()
    };
    // The run skill alone is not applied: the applier waits for both.
    apply(&mut rig, &skill_update(24, 10));
    rig.frames(1);
    assert_eq!(rig.movement.applied_stats, None);
    apply(&mut rig, &skill_update(22, 50));
    apply(&mut rig, &stamina_update(100));
    rig.frames(1);
    assert_eq!(rig.movement.applied_stats, Some((10, 50, 0.0, 100)));
    assert!((rate(&rig) - 1.131_0).abs() < 1e-3, "rate {}", rate(&rig));
    let plodded = run_half_a_second(&mut rig);

    apply(&mut rig, &skill_update(24, 300));
    rig.frames(1);
    assert!((rate(&rig) - 2.65).abs() < 1e-3, "rate {}", rate(&rig));
    let ran = run_half_a_second(&mut rig);
    println!("plodded {plodded:.3} m, ran {ran:.3} m");
    assert!(ran > plodded * 1.5, "ran {ran} against {plodded}");

    // Out of stamina mid-run: the exhaustion edge re-applies the running
    // motion at no skill without the key being pressed again.
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }));
    rig.frames(30);
    let (a, _, _) = rig.body();
    rig.frames(15);
    let (b, _, _) = rig.body();
    apply(&mut rig, &stamina_update(0));
    rig.frames(15);
    let (c, _, _) = rig.body();
    rig.frames(15);
    let (d, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.frames(60);
    assert!((rate(&rig) - 1.0).abs() < 1e-6, "rate {}", rate(&rig));
    let fresh = (b - a).truncate().length();
    let laboured = (d - c).truncate().length();
    println!("fresh {fresh:.3} m, laboured {laboured:.3} m per quarter second");
    assert!(
        laboured < fresh * 0.6,
        "laboured {laboured} against {fresh}"
    );
}

fn own_stance(stance: u16, sequence: u16, autonomous: bool) -> Inbound<'static> {
    Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: sequence,
        server_control_sequence: 1,
        is_autonomous: autonomous,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: stance,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    current_style: Some(stance),
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: None,
            },
        },
    }))
}

/// OpenAC `RuntimeLiveEntitySessionController.ApplyServerMotionToLocalBody`:
/// the stance the server puts the character's own body in after
/// a combat-mode change is unpacked into the body (through the same
/// dispatcher as another object's motion), and the body takes it up over
/// its animation; the echo of the character's own movement (autonomous)
/// is not replayed.
#[test]
#[ignore = "needs the retail dat files"]
fn the_server_sets_the_characters_own_stance_after_a_combat_mode_change() {
    const HAND_COMBAT: u16 = 0x003C;
    let mut rig = Rig::entered();
    rig.frames(30);
    let style = |rig: &Rig| rig.movement.body_motion().unwrap().current_style;
    assert_eq!(style(&rig), ac_motion::motion_command::NON_COMBAT);

    apply(&mut rig, &own_stance(HAND_COMBAT, 2, true));
    rig.frames(1);
    assert_eq!(
        style(&rig),
        ac_motion::motion_command::NON_COMBAT,
        "the echo of its own movement is not replayed"
    );

    apply(&mut rig, &own_stance(HAND_COMBAT, 3, false));
    assert_eq!(style(&rig), 0x8000_0000 | u32::from(HAND_COMBAT));
    assert!(
        rig.movement.body_motion().unwrap().motions_pending,
        "the stance change plays out over its animation"
    );
    rig.frames(180);
    let settled = rig.movement.body_motion().unwrap();
    assert_eq!(settled.current_style, 0x8000_003C);
    assert!(!settled.motions_pending, "the stance change finished");

    apply(&mut rig, &own_stance(0x003D, 4, false));
    rig.frames(180);
    assert_eq!(style(&rig), ac_motion::motion_command::NON_COMBAT);
}

/// The gesture the server plays on the character's own body (the recall's,
/// an attack's or a cast's: an action in a non-autonomous motion state) is
/// replayed into the body's motion table through the same own-motion
/// dispatch (OpenAC `ApplyServerMotionToLocalBody` →
/// `RemoteInboundMotionDispatcher.Apply` → `move_to_interpreted_state`).
#[test]
#[ignore = "needs the retail dat files"]
fn the_server_plays_the_recall_gesture_on_the_characters_own_body() {
    use ac_motion::motion_command::LIFESTONE_RECALL;
    let mut rig = Rig::entered();
    rig.frames(30);
    let playing = |rig: &Rig| {
        rig.movement
            .controller()
            .unwrap()
            .object()
            .sequencer
            .as_ref()
            .unwrap()
            .manager()
            .pending_animations()
            .any(|p| p.motion == LIFESTONE_RECALL)
    };
    assert!(!playing(&rig));
    let recall = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: 2,
        server_control_sequence: 1,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    current_style: Some(0x003D),
                    commands: vec![ac_proto::MotionItem {
                        command: (LIFESTONE_RECALL & 0xFFFF) as u16,
                        packed_sequence: 1,
                        speed: 1.0,
                    }],
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: None,
            },
        },
    }));
    apply(&mut rig, &recall);
    rig.frames(1);
    assert!(playing(&rig), "the recall gesture plays on the body");
}

/// The server's log out: the
/// character's NonCombat Ready state with the LogOut action, not
/// autonomous. While the log out runs the session no longer steps the
/// character (its phase is no longer in the world), so the state reaches
/// the window as it arrives: taken up, with the action in it (OpenAC plays
/// it on the player in the world during the logout hold,
/// `LocalPlayerTeleportController.TickLogout`).
#[test]
#[ignore = "needs the retail dat files"]
fn the_servers_log_out_motion_reaches_the_window_without_a_step() {
    use ac_motion::motion_command::LOG_OUT;
    let mut rig = Rig::entered();
    rig.frames(30);
    rig.world.drain_applied_motions().for_each(drop);
    let logout = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: 2,
        server_control_sequence: 1,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    current_style: Some(0x003D),
                    commands: vec![ac_proto::MotionItem {
                        command: (LOG_OUT & 0xFFFF) as u16,
                        packed_sequence: 1,
                        speed: 1.0,
                    }],
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: None,
            },
        },
    }));
    apply(&mut rig, &logout);
    let applied: Vec<_> = rig.world.drain_applied_motions().collect();
    let taken = applied
        .iter()
        .find(|m| m.guid == PLAYER && m.taken_up)
        .unwrap_or_else(|| panic!("the state is taken up: {applied:?}"));
    assert!(
        taken
            .motion
            .state
            .actions
            .as_ref()
            .is_some_and(|a| a.iter().any(|a| a.command == LOG_OUT)),
        "{taken:?}"
    );
}

/// A melee swing as the server sends it, for players and monsters alike:
/// the swing is the forward command of a non-autonomous
/// interpreted state in the combat stance.
fn swing(guid: u32, sequence: u16, autonomous: bool) -> Inbound<'static> {
    sword_motion(
        guid,
        sequence,
        autonomous,
        ac_motion::motion_command::SLASH_HIGH,
    )
}

/// A non-autonomous interpreted state in the sword stance with `forward`.
fn sword_motion(guid: u32, sequence: u16, autonomous: bool, forward: u32) -> Inbound<'static> {
    const SWORD_COMBAT: u16 = 0x003E;
    Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid,
        instance_sequence: 1,
        movement_sequence: sequence,
        server_control_sequence: 1,
        is_autonomous: autonomous,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: SWORD_COMBAT,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    current_style: Some(SWORD_COMBAT),
                    forward_command: Some((forward & 0xFFFF) as u16),
                    forward_speed: Some(1.25),
                    turn_speed: Some(2.25),
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: None,
            },
        },
    }))
}

/// A repeated attack at the same height and power sends the same swing
/// again, and the game plays it again (OpenAC `RemoteInboundMotionDispatcher
/// .Apply` -> `MotionInterpreter.MoveToInterpretedState` on every accepted
/// update). The state the session publishes reads the same each time, so
/// its serial counts the swings for the window; the echo of the character's
/// own movement is not taken up and does not count.
#[test]
#[ignore = "needs the retail dat files"]
fn every_swing_the_server_plays_reaches_the_window() {
    use ac_motion::motion_command::SLASH_HIGH;
    const CREATURE: u32 = 0x7000_0303;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 3.0, None);
    rig.frames(30);
    let view = |rig: &Rig, guid: u32| {
        rig.world
            .object(guid)
            .unwrap()
            .motion_view()
            .expect("a motion view")
    };
    for guid in [PLAYER, CREATURE] {
        let before = view(&rig, guid).serial;
        let mut seen = None;
        for (n, sequence) in (2..=4).enumerate() {
            apply(&mut rig, &swing(guid, sequence, false));
            rig.frames(1);
            let now = view(&rig, guid);
            assert!(now.body.is_some(), "{guid:#X}: the session moves the body");
            assert_eq!(now.state.forward_command, SLASH_HIGH, "{guid:#X}");
            assert_eq!(now.serial, before + n as u32 + 1, "{guid:#X} swing {n}");
            if let Some(seen) = &seen {
                assert_eq!(&now.state, seen, "{guid:#X}: the state reads the same");
            }
            seen = Some(now.state);
            rig.frames(90);
        }
        if guid == PLAYER {
            let counted = view(&rig, guid).serial;
            apply(&mut rig, &swing(guid, 5, true));
            rig.frames(1);
            assert_eq!(view(&rig, guid).serial, counted, "the echo is not counted");
        }
    }
}

/// The game takes up every motion state in the order it arrives and plays
/// a swing by taking it up (OpenAC `RemoteInboundMotionDispatcher.Apply`
/// per `UpdateMotion`, the presentation sequencer its animation sink). The
/// session records each state it takes up, in order, published at once: a
/// swing and the ready after it in one pass are both recorded, and a turn
/// the character makes after the swing (keys, a plugin) neither hides it
/// nor counts as a server state.
#[test]
#[ignore = "needs the retail dat files"]
fn every_motion_state_taken_up_is_recorded_in_order() {
    use ac_motion::motion_command::{READY, SLASH_HIGH, TURN_RIGHT};
    const CREATURE: u32 = 0x7000_0304;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 3.0, None);
    rig.frames(30);
    rig.world.drain_applied_motions().for_each(drop);
    let taken = |rig: &mut Rig, guid: u32| -> Vec<MotionView> {
        rig.world
            .drain_applied_motions()
            .filter(|m| m.guid == guid && m.taken_up)
            .map(|m| m.motion)
            .collect()
    };
    for guid in [PLAYER, CREATURE] {
        let before = rig
            .world
            .object(guid)
            .unwrap()
            .motion_view()
            .unwrap()
            .serial;
        // Two states in one pass: the swing, then ready.
        apply(&mut rig, &swing(guid, 2, false));
        apply(&mut rig, &sword_motion(guid, 3, false, READY));
        let motions = taken(&mut rig, guid);
        let forwards: Vec<u32> = motions.iter().map(|m| m.state.forward_command).collect();
        assert_eq!(forwards, [SLASH_HIGH, READY], "{guid:#X}");
        let serials: Vec<u32> = motions.iter().map(|m| m.serial).collect();
        assert_eq!(serials, [before + 1, before + 2], "{guid:#X}");
        assert!(motions.iter().all(|m| m.body.is_some()), "{guid:#X}");
        // A body standing on the ground stays on it: a window landing it
        // again would drop the swing's animation.
        assert!(motions.iter().all(|m| m.grounded), "{guid:#X}");
        rig.frames(90);
    }
    // The character's swing, then a turn of its own in the same frame.
    apply(&mut rig, &swing(PLAYER, 4, false));
    rig.command(MovementCommand::Keys(MovementKeys {
        turn_right: true,
        ..MovementKeys::default()
    }));
    rig.frames(1);
    let motions = taken(&mut rig, PLAYER);
    assert_eq!(motions.len(), 1);
    assert_eq!(motions[0].state.forward_command, SLASH_HIGH);
    let now = rig.world.object(PLAYER).unwrap().motion_view().unwrap();
    assert_eq!(now.state.turn_command, TURN_RIGHT, "the turn is followed");
    assert_eq!(
        now.serial, motions[0].serial,
        "the turn is not a server state"
    );
}

/// Sticky melee wired end to end: a creature whose motion names the
/// character as its sticky object (a melee stick: the movement's case 0
/// sticks the creature to that object) subscribes to
/// the character's target reports and keeps to it while it walks (OpenAC
/// `RemoteChaseEndToEndHarnessTests` with the real owners).
#[test]
#[ignore = "needs the retail dat files"]
fn a_creature_stuck_to_the_character_keeps_to_it_while_it_walks() {
    const CREATURE: u32 = 0x7000_0302;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 1.2, None);
    rig.frames(2);
    let local = rig.world.object(CREATURE).unwrap().key().local_id;
    let stick = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: CREATURE,
        instance_sequence: 1,
        movement_sequence: 2,
        server_control_sequence: 1,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003C,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    current_style: Some(0x003C),
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: Some(PLAYER),
            },
        },
    }));
    apply(&mut rig, &stick);
    let sticky = |rig: &Rig| {
        rig.movement.remotes[&local]
            .driver
            .obj
            .position
            .get_sticky_object_id()
    };
    assert_eq!(sticky(&rig), PLAYER);
    let (start, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        walk: true,
        ..MovementKeys::default()
    }));
    rig.frames(54);
    let (end, _, _) = rig.body();
    let creature = rig.movement.remotes[&local].driver.obj.body.position();
    let gap = (creature - end).truncate().length();
    println!(
        "the character walked {:.2} m; the creature is {gap:.2} m from it",
        (end - start).truncate().length()
    );
    assert!(
        (end - start).truncate().length() > 1.5,
        "the character moved"
    );
    assert_eq!(sticky(&rig), PLAYER, "the character's reports keep it");
    // It keeps the sticky gap to the character's body (both bodies are
    // the human setup).
    let radius = content().setup(HUMAN_SETUP).unwrap().radius;
    let keep = 2.0 * radius + ac_physics::movement::StickyManager::STICKY_RADIUS;
    assert!(
        gap < keep + 0.1,
        "the creature kept to the character: {gap} m, keeps {keep} m"
    );
    // The stick is a one-second lease the server renews with each motion
    // (OpenAC `StickyTime`).
    rig.frames(12);
    assert_eq!(sticky(&rig), 0, "the lease ran out");
}

/// The first setup in the dat with the one collision sphere a missile flies
/// with.
fn one_sphere_setup() -> u32 {
    let content = content();
    (0x0200_0002..0x0200_2000)
        .find(|&id| {
            content
                .setup(id)
                .is_some_and(|s| s.spheres.len() == 1 && s.spheres[0].radius > 0.02)
        })
        .expect("a one-sphere setup")
}

fn launch(rig: &mut Rig, guid: u32, from: Vec3, velocity: Vec3, state: u32) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(Position {
            cell_id: outdoor_cell(from.x, from.y),
            origin: Vector3 {
                x: from.x,
                y: from.y,
                z: from.z,
            },
            orientation: Quaternion::IDENTITY,
        }),
    );
    physics.setup_id = Some(one_sphere_setup());
    physics.state = state;
    physics.velocity = Some(Vector3 {
        x: velocity.x,
        y: velocity.y,
        z: velocity.z,
    });
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie: None,
    }));
    apply(rig, &create);
}

fn live(rig: &Rig, guid: u32) -> Vec3 {
    let p = rig.world.object(guid).unwrap().position().copied().unwrap();
    Vec3::new(p.origin.x, p.origin.y, p.origin.z)
}

/// A missile flies with the session's physics (OpenAC
/// `ProjectileController.TryBind` / `Tick` over `ProjectilePhysicsStepper`):
/// launched north and a little up it climbs, falls under gravity, comes to
/// rest on the ground ahead and stays there; the world (and a window) sees
/// it where its body is. A vector update throws it again.
#[test]
#[ignore = "needs the retail dat files"]
fn a_missile_flies_falls_and_comes_to_rest_on_the_ground() {
    const ARROW: u32 = 0x8000_0400;
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    // Ahead of the character, over the open ground it runs on.
    let from = Vec3::new(start.x, start.y + 2.0, start.z + 1.5);
    let state =
        (PhysicsState::MISSILE | PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS).bits();
    launch(&mut rig, ARROW, from, Vec3::new(0.0, 6.0, 2.0), state);
    let local = rig.world.object(ARROW).unwrap().key().local_id;
    assert!(rig.movement.projectiles.contains_key(&local), "bound");
    rig.frames(6);
    let early = live(&rig, ARROW);
    assert!(early.y > from.y + 0.4, "flew north: {from} -> {early}");
    let mut highest = early.z;
    let mut previous = early;
    let mut rest_frames = 0;
    for _ in 0..240 {
        rig.frames(1);
        let now = live(&rig, ARROW);
        highest = highest.max(now.z);
        if (now - previous).length() < 1e-4 {
            rest_frames += 1;
        } else {
            rest_frames = 0;
        }
        previous = now;
    }
    let ground = ground_z(&content(), previous.x, previous.y);
    println!(
        "flew {:.2} m, rose to {:.2} m above the launch, rests {:.2} m above the ground",
        (previous - from).truncate().length(),
        highest - from.z,
        previous.z - ground
    );
    assert!(highest > from.z + 0.05, "climbed first");
    assert!(rest_frames > 30, "came to rest");
    assert!(
        (previous.z - ground).abs() < 0.5,
        "rests on the ground ({} vs {ground})",
        previous.z
    );
    assert!((previous - from).truncate().length() > 2.5, "landed ahead");

    // The server throws it again.
    let vector = Inbound::VectorUpdate(ac_proto::VectorUpdate {
        guid: ARROW,
        velocity: Vector3 {
            x: 10.0,
            y: 0.0,
            z: 4.0,
        },
        omega: Vector3::default(),
        instance_sequence: 1,
        vector_sequence: 1,
    });
    apply(&mut rig, &vector);
    rig.frames(10);
    let thrown = live(&rig, ARROW);
    assert!(
        thrown.x > previous.x + 1.0,
        "thrown east: {previous} -> {thrown}"
    );

    // Leaving the world drops the body.
    let delete = Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: ARROW,
        instance_sequence: 1,
    });
    apply(&mut rig, &delete);
    assert!(rig.movement.projectiles.is_empty());
}

/// OpenAC `RestrictionObjPrevalenceInspectionTests.
/// CountRestrictionObjCells_InstalledCellDat`: how many rooms in the
/// installed cell dat carry a house barrier (the cells the barrier guards).
#[test]
#[ignore = "needs the retail dat files"]
fn count_the_rooms_that_carry_a_house_barrier() {
    use ac_dat::objects::{EnvCell, LandBlockInfo};
    let content = content();
    let dats = content.dats();
    let (mut total, mut restricted) = (0u32, 0u32);
    let mut landblocks = std::collections::BTreeSet::new();
    let mut samples = Vec::new();
    // The landblocks around Holtburg (OpenAC scans the whole dat; this
    // band keeps the run to seconds).
    for x in 0xA0..=0xB0u32 {
        for y in 0xA8..=0xBFu32 {
            let info_id = (x << 24) | (y << 16) | 0xFFFE;
            let Some(info) = dats.get::<LandBlockInfo>(info_id).ok().flatten() else {
                continue;
            };
            for offset in 0..info.num_cells {
                let cell_id = (info_id & 0xFFFF_0000) | (0x0100 + offset);
                let Some(cell) = dats.get::<EnvCell>(cell_id).ok().flatten() else {
                    continue;
                };
                total += 1;
                if cell.restriction_obj != 0 {
                    restricted += 1;
                    landblocks.insert(cell_id & 0xFFFF_0000);
                    if samples.len() < 10 {
                        samples.push(format!(
                            "cell 0x{cell_id:08X} restrictionObj 0x{:08X}",
                            cell.restriction_obj
                        ));
                    }
                }
            }
        }
    }
    println!(
        "EnvCells scanned: {total}; with RestrictionObj != 0: {restricted}; distinct landblocks: {}",
        landblocks.len()
    );
    for s in samples {
        println!("{s}");
    }
    assert!(total > 0, "the installed cell dat enumerated no EnvCells");
    assert!(restricted > 0, "no house barrier in the band");
}

fn set_state(rig: &mut Rig, state: u32, sequence: u16) {
    apply(
        rig,
        &Inbound::SetState(ac_proto::SetState {
            guid: PLAYER,
            physics_state: state,
            instance_sequence: 1,
            state_sequence: sequence,
        }),
    );
}

/// A character running into a portal arrives standing still. The server's
/// teleport hides the character after placing it at the destination and
/// shows it again once the arrival completed; each hidden edge re-enters
/// its animation into the world (OpenAC
/// `LiveEntityPresentationController.ApplyPendingTransitions` ->
/// `LiveEntityPartArrayLifecycle.HandleEnterWorld`), dropping the stop
/// animation the teleport's stop queued. Kept, that stop walked the body
/// on about 0.8 m once it was shown.
#[test]
#[ignore = "needs the retail dat files"]
fn a_body_arriving_from_portal_space_keeps_no_momentum() {
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }));
    rig.frames(60);
    assert!((rig.body().0 - start).truncate().length() > 3.0, "ran");
    apply(
        &mut rig,
        &Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        }),
    );
    rig.command(MovementCommand::Keys(MovementKeys::default()));
    rig.movement.set_arrival_held(true);
    let to = Vec3::new(start.x + 20.0, start.y + 30.0, start.z);
    let teleport = Inbound::UpdatePosition(ac_proto::UpdatePosition {
        guid: PLAYER,
        flags: 0,
        position: Position {
            cell_id: outdoor_cell(to.x, to.y),
            origin: Vector3 {
                x: to.x,
                y: to.y,
                z: ground_z(&content(), to.x, to.y),
            },
            orientation: Quaternion::IDENTITY,
        },
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: 2,
        teleport_sequence: 1,
        force_position_sequence: 0,
    });
    apply(&mut rig, &teleport);
    // Hidden, ignoring collisions, gravity (the server's teleport state).
    set_state(&mut rig, 0x0000_4410, 1);
    rig.frames(30);
    rig.movement.arrive();
    rig.frames(10);
    let (arrived, _, _) = rig.body();
    // Shown again: gravity and report collisions.
    set_state(&mut rig, 0x0000_0408, 2);
    rig.frames(120);
    let (end, _, _) = rig.body();
    let c = rig.movement.controller().unwrap();
    assert_eq!(c.object().body.velocity, Vec3::ZERO);
    assert_eq!(c.object().interp.interpreted_state.forward_command, READY);
    let drift = (end - arrived).truncate().length();
    assert!(drift < 0.01, "walked on {drift:.3} m after arriving");
}

/// A live log (a two-handed sword): the server's
/// swing (SlashMed at speed 2, stuck to the target), then its turn to the
/// target (movement
/// type 8). The character's body plays the swing to its last frame and
/// turns after it (OpenAC `MoveToManager.BeginTurnToHeading` waits while
/// `MotionsPending`), on the ground throughout; the window is handed the
/// swing as taken up and then the turn.
#[test]
#[ignore = "needs the retail dat files"]
fn a_turn_to_the_target_after_a_swing_waits_for_the_swing() {
    use ac_motion::motion_command::{SLASH_MED, TURN_RIGHT};
    const CREATURE: u32 = 0x7000_0305;
    // The swing's stick turns the body to face its target, so the turn
    // after it is ordered at another creature, to the south-east.
    const NEXT: u32 = 0x7000_0306;
    const TWO_HANDED: u16 = 0x0044;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 1.5, None);
    creature_beside(&mut rig, NEXT, 1.0, None);
    let (x, y) = SPOT;
    remote_at(&mut rig, NEXT, x + 1.0, y - 2.0, 2);
    rig.frames(30);
    apply(&mut rig, &own_stance(TWO_HANDED, 2, false));
    rig.frames(120);
    rig.world.drain_applied_motions().for_each(drop);
    let mut swing = sword_motion(PLAYER, 3, false, SLASH_MED);
    if let Inbound::UpdateMotion(u) = &mut swing {
        u.movement.current_style = TWO_HANDED;
        u.movement.motion_flags = 1;
        if let ac_proto::Movement::Interpreted {
            state,
            sticky_object,
        } = &mut u.movement.movement
        {
            state.current_style = Some(TWO_HANDED);
            state.forward_speed = Some(2.0);
            *sticky_object = Some(CREATURE);
        }
    }
    apply(&mut rig, &swing);
    let drawing = |rig: &Rig| {
        let obj = rig.movement.controller().unwrap().object();
        let sequence = obj.sequencer.as_ref().unwrap().sequence();
        let node = sequence.curr_anim().unwrap();
        (
            node.anim().map_or(0, |a| a.id),
            sequence.frame_number(),
            node.high_frame,
        )
    };
    let (swing_anim, _, last) = drawing(&rig);
    rig.frames(3);
    let turn = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: 4,
        server_control_sequence: 2,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 8,
            motion_flags: 0,
            current_style: TWO_HANDED,
            movement: ac_proto::Movement::TurnToObject {
                target: NEXT,
                wire_heading: 145.0,
                params: ac_proto::TurnToParams {
                    bitfield: 0,
                    speed: 1.0,
                    desired_heading: 0.0,
                },
            },
        },
    }));
    apply(&mut rig, &turn);
    let mut reached = 0.0;
    for _ in 0..120 {
        let (anim, frame, _) = drawing(&rig);
        if anim != swing_anim {
            break;
        }
        reached = frame;
        assert!(
            rig.world
                .object(PLAYER)
                .unwrap()
                .motion_view()
                .unwrap()
                .grounded,
            "on the ground"
        );
        rig.frames(1);
    }
    assert!(
        reached + 2.0 >= f64::from(last),
        "the swing was left at frame {reached:.1} of {last}"
    );
    let mut turned = false;
    for _ in 0..30 {
        let view = rig.world.object(PLAYER).unwrap().motion_view().unwrap();
        turned |= view.state.turn_command == TURN_RIGHT;
        rig.frames(1);
    }
    assert!(turned, "then it turns");
    let taken: Vec<u32> = rig
        .world
        .drain_applied_motions()
        .filter(|m| m.guid == PLAYER && m.taken_up)
        .map(|m| m.motion.state.forward_command)
        .collect();
    assert_eq!(taken, [SLASH_MED], "the swing is handed to the window");
}

/// A live log: the server turns
/// the character to its target (movement type 8) and swings 0.43 s into
/// the turn. The swing interrupts the turn first (OpenAC
/// `RemoteInboundMotionDispatcher.Apply`: `InterruptCurrentMovement`
/// before `MoveToInterpretedState`), so the body swings from ready. The
/// window follows only states, so the session hands it the state the
/// interrupt left (ready, not turning) before the swing, in order.
#[test]
#[ignore = "needs the retail dat files"]
fn a_swing_while_turning_to_the_target_hands_the_window_the_stopped_turn_first() {
    use ac_motion::motion_command::SLASH_MED;
    use ac_motion::motion_command::{READY, TURN_RIGHT};
    const CREATURE: u32 = 0x7000_0306;
    const TWO_HANDED: u16 = 0x0044;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, CREATURE, 1.5, None);
    rig.frames(30);
    apply(&mut rig, &own_stance(TWO_HANDED, 2, false));
    rig.frames(120);
    let turn = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: 3,
        server_control_sequence: 2,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 8,
            motion_flags: 0,
            current_style: TWO_HANDED,
            movement: ac_proto::Movement::TurnToObject {
                target: CREATURE,
                wire_heading: 90.0,
                params: ac_proto::TurnToParams {
                    bitfield: 0,
                    speed: 1.0,
                    desired_heading: 0.0,
                },
            },
        },
    }));
    apply(&mut rig, &turn);
    rig.frames(26);
    let turning = rig.world.object(PLAYER).unwrap().motion_view().unwrap();
    assert_eq!(
        turning.state.turn_command, TURN_RIGHT,
        "turning to the target"
    );
    rig.world.drain_applied_motions().for_each(drop);
    let mut swing = sword_motion(PLAYER, 4, false, SLASH_MED);
    if let Inbound::UpdateMotion(u) = &mut swing {
        u.server_control_sequence = 3;
        u.movement.current_style = TWO_HANDED;
        u.movement.motion_flags = 1;
        if let ac_proto::Movement::Interpreted {
            state,
            sticky_object,
        } = &mut u.movement.movement
        {
            state.current_style = Some(TWO_HANDED);
            state.forward_speed = Some(2.0);
            *sticky_object = Some(CREATURE);
        }
    }
    apply(&mut rig, &swing);
    let obj = rig.movement.controller().unwrap().object();
    let s = obj.sequencer.as_ref().unwrap();
    assert!(
        s.manager()
            .pending_animations()
            .any(|p| p.motion == SLASH_MED),
        "the body swings"
    );
    let events: Vec<_> = rig
        .world
        .drain_applied_motions()
        .filter(|m| m.guid == PLAYER)
        .collect();
    assert_eq!(events.len(), 2, "{events:?}");
    let (between, swung) = (&events[0], &events[1]);
    assert!(!between.taken_up && swung.taken_up);
    assert_eq!(between.motion.state.forward_command, READY);
    assert_eq!(between.motion.state.turn_command, 0, "the turn stopped");
    assert_eq!(between.motion.serial, turning.serial, "not a server state");
    assert_eq!(swung.motion.state.forward_command, SLASH_MED);
    assert_eq!(swung.motion.serial, turning.serial + 1);
}

/// Tell the world `guid` stands at (`x`, `y`) on the ground.
fn remote_at(rig: &mut Rig, guid: u32, x: f32, y: f32, position_sequence: u16) {
    let z = ground_z(rig.movement.content.as_ref().unwrap(), x, y);
    apply(
        rig,
        &Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid,
            flags: ac_proto::POSITION_IS_GROUNDED,
            position: Position {
                cell_id: outdoor_cell(x, y),
                origin: Vector3 { x, y, z },
                orientation: Quaternion::IDENTITY,
            },
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence,
            teleport_sequence: 0,
            force_position_sequence: 0,
        }),
    );
}

/// Where the body of `guid` stands, flat.
fn remote_body(rig: &Rig, guid: u32) -> Vec2 {
    let local = rig.world.object(guid).unwrap().key().local_id;
    rig.movement.remotes[&local]
        .driver
        .obj
        .body
        .position()
        .truncate()
}

/// A pack the server walks onto one spot spreads out to the bodies' size:
/// each body's catch-up is swept against the others' rows, which follow
/// the swept bodies (OpenAC `RemoteDeOverlapMechanismTests`' converging
/// pair, here through the owner's own frame).
#[test]
#[ignore = "needs the retail dat files"]
fn a_pack_the_server_walks_onto_one_spot_spreads_to_the_bodies_size() {
    const PACK: [u32; 6] = [
        0x7000_0601,
        0x7000_0602,
        0x7000_0603,
        0x7000_0604,
        0x7000_0605,
        0x7000_0606,
    ];
    let mut rig = Rig::entered();
    let (x, y) = SPOT;
    // The spot is 8 m north of the character; the pack starts on a 5 m
    // ring around it.
    let spot = Vec2::new(x, y + 8.0);
    let ring = |i: usize| {
        let angle = i as f32 * std::f32::consts::TAU / PACK.len() as f32;
        spot + 5.0 * Vec2::new(angle.cos(), angle.sin())
    };
    for (i, guid) in PACK.into_iter().enumerate() {
        let at = ring(i);
        creature_beside(&mut rig, guid, at.x - x, None);
        remote_at(&mut rig, guid, at.x, at.y, 2);
    }
    rig.frames(5);
    // The server walks each one 1 m closer every quarter second (4 m/s)
    // until all stand on the spot, then keeps saying so.
    for update in 0..24u16 {
        for (i, guid) in PACK.into_iter().enumerate() {
            let from = ring(i);
            let t = ((f32::from(update) + 1.0) / 5.0).min(1.0);
            let at = from.lerp(spot, t);
            remote_at(&mut rig, guid, at.x, at.y, 3 + update);
        }
        rig.frames(15);
    }
    let bodies: Vec<Vec2> = PACK.iter().map(|g| remote_body(&rig, *g)).collect();
    let content = content();
    let setup = content.setup(HUMAN_SETUP).unwrap();
    let foot = setup.spheres.first().map_or(setup.radius, |s| s.radius);
    let mut closest = f32::INFINITY;
    for (i, a) in bodies.iter().enumerate() {
        for b in &bodies[i + 1..] {
            closest = closest.min(a.distance(*b));
        }
    }
    let spread = bodies.iter().map(|b| b.distance(spot)).fold(0.0, f32::max);
    println!(
        "pack of {}: closest pair {closest:.3} m apart (two bodies touch at {:.3} m), \
         farthest {spread:.2} m from the spot",
        PACK.len(),
        2.0 * foot
    );
    assert!(
        closest >= 2.0 * foot - 0.1,
        "the pack kept apart: closest pair {closest} m, contact {} m",
        2.0 * foot
    );
}

/// The server's motion that keeps `guid` in its combat stance stuck to the
/// character (a monster fighting it), the `sequence`th.
fn stick_to_player(rig: &mut Rig, guid: u32, sequence: u16) {
    apply(
        rig,
        &Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
            guid,
            instance_sequence: 1,
            movement_sequence: sequence,
            server_control_sequence: 1,
            is_autonomous: false,
            movement: ac_proto::MovementData {
                movement_type: 0,
                motion_flags: 0,
                current_style: 0x003C,
                movement: ac_proto::Movement::Interpreted {
                    state: ac_proto::InterpretedMotionState {
                        current_style: Some(0x003C),
                        ..ac_proto::InterpretedMotionState::default()
                    },
                    sticky_object: Some(PLAYER),
                },
            },
        })),
    );
}

/// Monsters fighting the character stick to it and keep the sticky gap of
/// 0.3 m between their bodies and its body: the stick takes the
/// character's own girth (OpenAC `RuntimeRemoteArming
/// .StickToObjectFromWire`: `StickTo(target, radius, height)` with the
/// target's body shape). Stuck as a point, each aimed inside the
/// character's body and pressed the pack against it and into each other,
/// so the character had no room to move (seen live: monsters packed too
/// tight, the character stuck among many).
#[test]
#[ignore = "needs the retail dat files"]
fn monsters_stuck_to_the_character_keep_the_sticky_gap_to_its_body() {
    const PACK: [u32; 5] = [
        0x7000_0701,
        0x7000_0702,
        0x7000_0703,
        0x7000_0704,
        0x7000_0705,
    ];
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    // Five fighters 2 m around the character, in front and to the sides.
    for (i, guid) in PACK.into_iter().enumerate() {
        let angle = (i as f32 - 2.0) * 0.6 + std::f32::consts::FRAC_PI_2;
        let at = start.truncate() + 2.0 * Vec2::new(angle.cos(), angle.sin());
        creature_beside(&mut rig, guid, at.x - start.x, None);
        remote_at(&mut rig, guid, at.x, at.y, 2);
    }
    rig.frames(2);
    // The server renews each fighter's stick with each swing, twice a
    // second.
    for sequence in 2..8u16 {
        for guid in PACK {
            stick_to_player(&mut rig, guid, sequence);
        }
        rig.frames(30);
    }
    let content = content();
    let setup = content.setup(HUMAN_SETUP).unwrap();
    let keep = 2.0 * setup.radius + ac_physics::movement::StickyManager::STICKY_RADIUS;
    let (me, _, _) = rig.body();
    let gaps: Vec<f32> = PACK
        .iter()
        .map(|guid| remote_body(&rig, *guid).distance(me.truncate()))
        .collect();
    println!("centres from the character: {gaps:.3?} (the sticky gap keeps {keep:.3} m)");
    for (guid, gap) in PACK.into_iter().zip(gaps) {
        let local = rig.world.object(guid).unwrap().key().local_id;
        let sticky = rig.movement.remotes[&local].driver.obj.position.sticky;
        assert_eq!(
            sticky.map(|s| (s.target_id, s.target_radius)),
            Some((PLAYER, setup.radius)),
            "{guid:#X} is stuck to the character's body"
        );
        assert!(
            (gap - keep).abs() < 0.1,
            "{guid:#X} keeps the sticky gap: {gap} m, not {keep} m"
        );
    }
}

/// The character runs on into monsters fighting it: each keeps its sticky
/// gap to the character's body, so it gives way as the character comes
/// on (1.57 m in the second). Stuck as a point, each pressed against the
/// character and never gave way, and the character moved 0.03 m (live,
/// a run into drudges around the character covered 0.00 m: the character
/// stuck among many monsters).
#[test]
#[ignore = "needs the retail dat files"]
fn the_character_runs_on_into_monsters_stuck_to_it() {
    const PACK: [u32; 5] = [
        0x7000_0711,
        0x7000_0712,
        0x7000_0713,
        0x7000_0714,
        0x7000_0715,
    ];
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    // Five fighters 2 m around the character, in front (north, the way it
    // faces) and to the sides.
    for (i, guid) in PACK.into_iter().enumerate() {
        let angle = (i as f32 - 2.0) * 0.6 + std::f32::consts::FRAC_PI_2;
        let at = start.truncate() + 2.0 * Vec2::new(angle.cos(), angle.sin());
        creature_beside(&mut rig, guid, at.x - start.x, None);
        remote_at(&mut rig, guid, at.x, at.y, 2);
    }
    rig.frames(2);
    let mut sequence = 2u16;
    for _ in 0..4 {
        for guid in PACK {
            stick_to_player(&mut rig, guid, sequence);
        }
        sequence += 1;
        rig.frames(30);
    }
    let (from, _, _) = rig.body();
    rig.command(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }));
    for _ in 0..4 {
        for guid in PACK {
            stick_to_player(&mut rig, guid, sequence);
        }
        sequence += 1;
        rig.frames(15);
    }
    let (to, _, _) = rig.body();
    let ran = (to - from).truncate().length();
    println!("the character ran {ran:.2} m in 1 s into the fighters");
    assert!(ran > 1.5, "the character ran on: {ran} m");
}

/// A row of fighters closing on the character, each stuck to it (the
/// stick moves a body at five times its top speed), arrive beside each
/// other, not inside each other: one body at a time moves and its rows
/// follow it before the next body moves, so each is swept against where
/// the others stand now (OpenAC `RuntimeRemoteBodyOwner.TickBody` ->
/// `RuntimeRemotePhysicsUpdater.Tick`, which re-registers the body's
/// shadow inside its own step). Swept against where the others stood when
/// the frame began, two converging neighbours both stepped into the gap
/// between them and ended 0.943 m apart, inside each other (the bodies
/// touch at 0.960 m); now 0.971 m.
#[test]
#[ignore = "needs the retail dat files"]
fn a_row_of_fighters_closing_on_the_character_do_not_step_into_each_other() {
    const ROW: [u32; 6] = [
        0x7000_0801,
        0x7000_0802,
        0x7000_0803,
        0x7000_0804,
        0x7000_0805,
        0x7000_0806,
    ];
    let mut rig = Rig::entered();
    let (start, _, _) = rig.body();
    // A row 1 m apart, 4 m north of the character.
    for (i, guid) in ROW.into_iter().enumerate() {
        let at = start.truncate() + Vec2::new(i as f32 - 2.5, 4.0);
        creature_beside(&mut rig, guid, at.x - start.x, None);
        remote_at(&mut rig, guid, at.x, at.y, 2);
    }
    rig.frames(2);
    for sequence in 2..8u16 {
        for guid in ROW {
            stick_to_player(&mut rig, guid, sequence);
        }
        rig.frames(30);
    }
    let bodies: Vec<Vec2> = ROW.iter().map(|g| remote_body(&rig, *g)).collect();
    let content = content();
    let setup = content.setup(HUMAN_SETUP).unwrap();
    let contact = 2.0 * setup.spheres.first().map_or(setup.radius, |s| s.radius);
    let mut closest = f32::INFINITY;
    for (i, a) in bodies.iter().enumerate() {
        for b in &bodies[i + 1..] {
            closest = closest.min(a.distance(*b));
        }
    }
    println!("closest fighters {closest:.3} m apart (two bodies touch at {contact:.3} m)");
    assert!(
        closest >= contact - 0.01,
        "the fighters kept apart: closest pair {closest} m, contact {contact} m"
    );
}

/// A creature as the server describes one: its public description says
/// so (its body collides as a creature's).
fn creature_weenie() -> ac_proto::PublicWeenieDesc {
    ac_proto::PublicWeenieDesc {
        name: "Drudge".into(),
        item_type: 0x10,
        ..ac_proto::PublicWeenieDesc::default()
    }
}

/// The closest two of `guids`' bodies, flat, metres.
fn closest_pair(rig: &Rig, guids: &[u32]) -> f32 {
    let bodies: Vec<Vec2> = guids.iter().map(|g| remote_body(rig, *g)).collect();
    let mut closest = f32::INFINITY;
    for (i, a) in bodies.iter().enumerate() {
        for b in &bodies[i + 1..] {
            closest = closest.min(a.distance(*b));
        }
    }
    closest
}

/// Where two human bodies touch, centre to centre: twice the radius of
/// the setup's spheres.
fn human_contact() -> f32 {
    let content = content();
    let setup = content.setup(HUMAN_SETUP).unwrap();
    2.0 * setup.spheres.first().map_or(setup.radius, |s| s.radius)
}

/// A creature the server creates where another already stands enters the
/// world beside it, not inside it: the game puts every object entering
/// the world with a placement that may slide aside (set-position flags
/// 0x11; OpenAC
/// `RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate`,
/// `InitialCreateFlags`, submitted by `RuntimeRemoteFirstEntryState`), so
/// the two bodies end at least their spheres' radii apart. Put down
/// where the server said, the two stood 0.000 m apart (seen live: monsters
/// overlap each other in a pack).
#[test]
#[ignore = "needs the retail dat files"]
fn a_creature_created_where_another_stands_enters_the_world_beside_it() {
    const FIRST: u32 = 0x7000_0901;
    const SECOND: u32 = 0x7000_0902;
    let mut rig = Rig::entered();
    creature_beside(&mut rig, FIRST, 3.0, Some(creature_weenie()));
    creature_beside(&mut rig, SECOND, 3.0, Some(creature_weenie()));
    let contact = human_contact();
    let apart = closest_pair(&rig, &[FIRST, SECOND]);
    println!("created on one spot: {apart:.3} m apart (two bodies touch at {contact:.3} m)");
    assert!(
        apart >= contact - 0.01,
        "the second body entered beside the first: {apart} m, contact {contact} m"
    );
    // Both stand on the ground where they were put.
    let content = rig.movement.content.clone().unwrap();
    for guid in [FIRST, SECOND] {
        let local = rig.world.object(guid).unwrap().key().local_id;
        let driver = &rig.movement.remotes[&local].driver;
        let at = driver.obj.body.position();
        assert!(
            driver.obj.body.on_walkable(),
            "{guid:#X} stands on the ground"
        );
        assert!(!driver.airborne, "{guid:#X} is not falling");
        assert!(
            (at.z - ground_z(&content, at.x, at.y)).abs() < 0.05,
            "{guid:#X} stands at ground height: {at}"
        );
    }
}

/// A pack the server creates on one spot, then sets on the character
/// (each stuck to it), fights it shoulder to shoulder: each body entered
/// the world clear of the ones before it (placement 0x11) and the sweep
/// keeps them so. Put down inside each
/// other, two bodies never sweep apart: the pack moved as one clump and
/// its closest pair stayed 0.000 m apart (seen live: drudges
/// created by one command overlapped in pairs).
#[test]
#[ignore = "needs the retail dat files"]
fn a_pack_created_on_one_spot_fights_the_character_without_overlapping() {
    const PACK: [u32; 8] = [
        0x7000_0911,
        0x7000_0912,
        0x7000_0913,
        0x7000_0914,
        0x7000_0915,
        0x7000_0916,
        0x7000_0917,
        0x7000_0918,
    ];
    let mut rig = Rig::entered();
    for guid in PACK {
        creature_beside(&mut rig, guid, 3.0, Some(creature_weenie()));
    }
    rig.frames(2);
    for sequence in 2..8u16 {
        for guid in PACK {
            stick_to_player(&mut rig, guid, sequence);
        }
        rig.frames(30);
    }
    let contact = human_contact();
    let closest = closest_pair(&rig, &PACK);
    println!(
        "pack of {}: closest pair {closest:.3} m apart (two bodies touch at {contact:.3} m)",
        PACK.len()
    );
    assert!(
        closest >= contact - 0.01,
        "the pack kept apart: closest pair {closest} m, contact {contact} m"
    );
}

/// The drudge the server's `@create 10776` makes (Unconquered Drudge: setup
/// 0x020007DD, motion table 0x09000008, scale 1.3), the creature of the
/// reported fights.
const DRUDGE_SETUP: u32 = 0x0200_07DD;
const DRUDGE_MOTION: u32 = 0x0900_0008;
const DRUDGE_SCALE: f32 = 1.3;
/// The landblock north of the character's, more than 96 m away from it
/// at its middle (so its positions route as far snaps).
const NORTH: u32 = 0xA9B5_0000;

/// The outdoor cell of `landblock` holding (`x`, `y`), landblock-relative.
fn cell_in(landblock: u32, x: f32, y: f32) -> u32 {
    landblock | (1 + (x / 24.0) as u32 * 8 + (y / 24.0) as u32)
}

/// A wire position on the ground at (`x`, `y`) in `landblock`.
fn ground_position(rig: &Rig, landblock: u32, x: f32, y: f32) -> Position {
    let cell = cell_in(landblock, x, y);
    let origin = rig.movement.residency.origin_of(cell).unwrap();
    let z = rig
        .movement
        .engine
        .sample_terrain_z(origin.x + x, origin.y + y)
        .unwrap();
    Position {
        cell_id: cell,
        origin: Vector3 { x, y, z },
        orientation: Quaternion::IDENTITY,
    }
}

/// A drudge created standing at `position` in its NonCombat stance.
fn drudge_created(rig: &mut Rig, guid: u32, position: Position) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            movement: 1,
            ..PhysicsTimestamps::default()
        },
        Some(position),
    );
    physics.setup_id = Some(DRUDGE_SETUP);
    physics.motion_table_id = Some(DRUDGE_MOTION);
    physics.scale = Some(DRUDGE_SCALE);
    physics.state = 0x0000_0408;
    physics.movement = Some(ac_proto::PhysicsMovement::Present {
        raw: vec![0; 4],
        data: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState::default(),
                sticky_object: None,
            },
        },
        is_autonomous: false,
    });
    apply(
        rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics,
            weenie: Some(creature_weenie()),
        })),
    );
}

/// A drudge created standing at (`x`, `y`) in the character's landblock.
fn drudge_at(rig: &mut Rig, guid: u32, x: f32, y: f32) {
    let position = ground_position(rig, 0xA9B4_0000, x, y);
    drudge_created(rig, guid, position);
}

/// The server tells where `guid` is (on the ground), with its position,
/// teleport and forced-position stamps.
fn position_update(rig: &mut Rig, guid: u32, position: Position, stamps: (u16, u16, u16)) {
    let (position_sequence, teleport_sequence, force_position_sequence) = stamps;
    apply(
        rig,
        &Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid,
            flags: ac_proto::POSITION_IS_GROUNDED,
            position,
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence,
            teleport_sequence,
            force_position_sequence,
        }),
    );
}

/// The drudge's collision spheres (unscaled).
fn drudge_spheres() -> Vec<ac_physics::CollisionSphere> {
    let content = content();
    let setup = content.setup(DRUDGE_SETUP).unwrap();
    setup
        .spheres
        .iter()
        .map(|s| {
            ac_physics::CollisionSphere::new(
                Vec3::new(s.origin.x, s.origin.y, s.origin.z),
                s.radius,
            )
        })
        .collect()
}

/// How far apart the rings of a sliding placement's search are for a
/// drudge: OpenAC `Transition.FindPlacementPos`, like the game, searches
/// rings out to 4 m in
/// ceil(4 / r) steps, r the foot sphere's radius (0.436 m at scale 1.3:
/// 0.567 m, so 8 rings of 0.5 m), each ring's first sample at heading 0
/// (north, +y).
fn drudge_placement_ring() -> f32 {
    let r = drudge_spheres()[0].radius * DRUDGE_SCALE;
    4.0 / (4.0 / r).ceil()
}

/// Where a drudge creeping south at `x` from y = 143 comes to touch the
/// north wall of the Holtburg building 0x01000830 (at 104.5, 135.5): the
/// collision's own contact, found in 1 cm steps (a long sweep stops a
/// step short of it).
fn drudge_flush_with_north_wall(rig: &Rig, x: f32) -> f32 {
    let z = ground_z(&content(), x, 143.0);
    let mut at = Vec3::new(x, 143.0, z);
    for _ in 0..300 {
        let request = ac_physics::ResolveRequest {
            current_pos: at,
            target_pos: at - Vec3::new(0.0, 0.01, 0.0),
            cell_id: outdoor_cell(at.x, at.y),
            step_up_height: 0.4,
            step_down_height: 0.4,
            is_on_ground: true,
            mover_flags: ac_physics::ObjectInfoState(ac_physics::ObjectInfoState::EDGE_SLIDE),
            sphere_list: drudge_spheres(),
            sphere_scale: DRUDGE_SCALE,
            ..ac_physics::ResolveRequest::default()
        };
        let swept = rig.movement.engine.resolve_with_transition(&request, None);
        if !swept.ok || swept.position.y > at.y - 0.005 {
            return at.y;
        }
        at = Vec3::new(x, swept.position.y, z);
    }
    panic!("no wall south of ({x}, 143)");
}

/// The x of the wall tests: a stretch of the north wall with no door.
const WALL_X: f32 = 100.5;
/// The drudge of the wall tests.
const WALL_DRUDGE: u32 = 0x7000_0C01;

/// A drudge standing 4 m north of the wall, then the server's position of
/// it 0.29 m into the wall (flush with it less 0.29 m) with the given
/// stamps. Returns the rig, the flush y and the server's y.
fn drudge_sent_into_the_wall(stamps: (u16, u16, u16)) -> (Rig, f32, f32) {
    let mut rig = Rig::entered();
    let flush = drudge_flush_with_north_wall(&rig, WALL_X);
    drudge_at(&mut rig, WALL_DRUDGE, WALL_X, flush + 4.0);
    rig.frames(2);
    let into_wall = flush - 0.29;
    let position = ground_position(&rig, 0xA9B4_0000, WALL_X, into_wall);
    position_update(&mut rig, WALL_DRUDGE, position, stamps);
    (rig, flush, into_wall)
}

/// A monster's position the server sends with a new teleport stamp is put
/// down with a placement that may slide aside (OpenAC
/// `RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition`:
/// a remote's teleport-advanced position routes `SetPosition` with
/// `AuthoritativeTeleportFlags` = teleport | slide | send position event,
/// submitted by `RuntimeRemotePlacementDriveController
/// .ApplyAcceptedRemoteTeleport`, as the game moves or teleports a body).
/// The server sends one at each monster's first swing after it moved (its
/// position update at the swing advances the teleport stamp), so every
/// monster that closes to melee takes one. The server's spot overlaps the wall, so the placement's
/// search takes its first clear sample: one ring north (0.5 m). Put down
/// where the server said, a drudge stood 0.29 m inside the wall (seen live:
/// monsters close to walls are partly inside them).
#[test]
#[ignore = "needs the retail dat files"]
fn a_monster_teleported_into_a_wall_is_placed_beside_it() {
    let (mut rig, flush, into_wall) = drudge_sent_into_the_wall((2, 1, 0));
    let placed = remote_body(&rig, WALL_DRUDGE);
    let expected = Vec2::new(WALL_X, into_wall + drudge_placement_ring());
    println!(
        "server put it at y {into_wall:.3}; the body stands at {placed:.3}, expected \
         {expected:.3} (flush with the wall at y {flush:.3})"
    );
    assert!(
        placed.distance(expected) < 0.01,
        "placed one ring north of the server's spot: {placed}, expected {expected}"
    );
    assert!(
        placed.y >= flush - 0.01,
        "the drudge stands beside the wall, not in it: y {} (flush {flush})",
        placed.y
    );
    // It stays there.
    rig.frames(30);
    let after = remote_body(&rig, WALL_DRUDGE);
    assert!(after.y >= flush - 0.01, "still beside the wall: {after}");
}

/// A placed monster that stands still takes no speed from the server's
/// next position: the server's own position is the last one it sent,
/// stamped after the placement (OpenAC `LiveEntityNetworkUpdateController`
/// runs `RuntimeRemoteServerPosition.Stamp(worldPos)` after routing, over
/// `RuntimeSetPositionState.CommitCanonical`'s placed spot). Stamped with
/// the placed spot, the next position without a velocity derived 2 m/s
/// from the 0.5 m between the two over a quarter second.
#[test]
#[ignore = "needs the retail dat files"]
fn a_placed_monster_standing_still_takes_no_speed_from_its_next_position() {
    let (mut rig, _, into_wall) = drudge_sent_into_the_wall((2, 1, 0));
    rig.frames(15);
    let position = ground_position(&rig, 0xA9B4_0000, WALL_X, into_wall);
    position_update(&mut rig, WALL_DRUDGE, position, (3, 1, 0));
    let local = rig.world.object(WALL_DRUDGE).unwrap().key().local_id;
    let driver = &rig.movement.remotes[&local].driver;
    println!("server velocity {:.4}", driver.server_velocity);
    assert!(driver.has_server_velocity);
    assert!(
        driver.server_velocity.length() < 1e-3,
        "standing still: {}",
        driver.server_velocity
    );
}

/// A forced position is the character's alone: a monster's position
/// carrying a new forced-position stamp is an ordinary one (the gate never
/// consumes that stamp for another object, and OpenAC
/// `RuntimeAuthoritativePositionRouteClassifier.ValidAcceptedAuthority`
/// takes `ForcePosition` only for the local player), so it is caught up
/// to through the sweep, not placed: the drudge is held at the wall.
#[test]
#[ignore = "needs the retail dat files"]
fn a_monster_position_with_a_forced_stamp_is_caught_up_to_not_placed() {
    let mut rig = Rig::entered();
    let flush = drudge_flush_with_north_wall(&rig, WALL_X);
    drudge_at(&mut rig, WALL_DRUDGE, WALL_X, flush + 2.0);
    // A first position where it stands (the first one is taken outright).
    let standing = ground_position(&rig, 0xA9B4_0000, WALL_X, flush + 2.0);
    position_update(&mut rig, WALL_DRUDGE, standing, (2, 0, 0));
    rig.frames(2);
    let before = remote_body(&rig, WALL_DRUDGE);
    let position = ground_position(&rig, 0xA9B4_0000, WALL_X, flush - 0.29);
    position_update(&mut rig, WALL_DRUDGE, position, (3, 0, 1));
    let queued = remote_body(&rig, WALL_DRUDGE);
    assert!(
        queued.distance(before) < 0.01,
        "queued, not placed: {before} -> {queued}"
    );
    rig.frames(60);
    let after = remote_body(&rig, WALL_DRUDGE);
    println!("caught up to {after:.3} (flush {flush:.3})");
    assert!(
        after.y >= flush - 0.05 && after.y < flush + 0.5,
        "held at the wall: {after} (flush {flush})"
    );
}

/// A monster sent where the placement finds no clear spot within 4 m
/// (inside the Holtburg building 0x01000BC3 at 31.5, 159.5) stays where it
/// was (OpenAC `RuntimeRemotePlacementDriveController.SubmitAndResolve`:
/// `RejectedByPlacement` keeps the body).
#[test]
#[ignore = "needs the retail dat files"]
fn a_monster_teleported_where_nothing_is_clear_stays_where_it_was() {
    const DRUDGE: u32 = 0x7000_0C11;
    let mut rig = Rig::entered();
    drudge_at(&mut rig, DRUDGE, 31.0, 145.0);
    rig.frames(2);
    let before = remote_body(&rig, DRUDGE);
    let inside = ground_position(&rig, 0xA9B4_0000, 31.0, 159.0);
    position_update(&mut rig, DRUDGE, inside, (2, 1, 0));
    let after = remote_body(&rig, DRUDGE);
    println!("before {before:.3}, after {after:.3}");
    assert!(
        after.distance(before) < 0.01,
        "a refused placement leaves the body: {before} -> {after}"
    );
}

/// Two drudges more than 96 m from the character; the second's body
/// thrown into the air when `airborne`; then the server says the second
/// stands on the first's spot. Returns the first's body and the second's.
fn far_drudge_sent_onto_another(airborne: bool) -> (Vec2, Vec2) {
    const FIRST: u32 = 0x7000_0C21;
    const SECOND: u32 = 0x7000_0C22;
    let mut rig = Rig::entered();
    let at = ground_position(&rig, NORTH, 60.0, 58.0);
    drudge_created(&mut rig, FIRST, at);
    let beside = ground_position(&rig, NORTH, 66.0, 58.0);
    drudge_created(&mut rig, SECOND, beside);
    rig.frames(2);
    let (me, _, _) = rig.body();
    let first = remote_body(&rig, FIRST);
    assert!(
        first.distance(me.truncate()) > 96.0,
        "far from the character"
    );
    if airborne {
        apply(
            &mut rig,
            &Inbound::VectorUpdate(ac_proto::VectorUpdate {
                guid: SECOND,
                velocity: Vector3 {
                    x: 0.0,
                    y: 0.0,
                    z: 6.0,
                },
                omega: Vector3::default(),
                instance_sequence: 1,
                vector_sequence: 1,
            }),
        );
        let local = rig.world.object(SECOND).unwrap().key().local_id;
        assert!(rig.movement.remotes[&local].driver.airborne);
    }
    let onto = ground_position(&rig, NORTH, 60.0, 58.0);
    position_update(&mut rig, SECOND, onto, (2, 0, 0));
    (first, remote_body(&rig, SECOND))
}

/// A far monster's position (more than 96 m from the character) is a far
/// snap, put down with a placement that may slide aside (OpenAC
/// `ClassifyAcceptedPosition`: `SetPositionSimple` with
/// `AuthoritativeTeleportFlags` beyond `MaxPhysicsDistance`, through
/// `ApplyAcceptedRemoteFarSnap`). Sent onto another drudge, it is placed
/// on the first ring clear of it: two drudges touch at 2 x 0.567 m, so
/// the third ring north (1.5 m).
#[test]
#[ignore = "needs the retail dat files"]
fn a_far_monster_snapped_onto_another_is_placed_beside_it() {
    let (first, second) = far_drudge_sent_onto_another(false);
    let expected = first + Vec2::new(0.0, 3.0 * drudge_placement_ring());
    println!("first {first:.3}, second {second:.3}, expected {expected:.3}");
    assert!(
        second.distance(expected) < 0.01,
        "placed on the third ring north: {second}, expected {expected}"
    );
}

/// A far monster out of contact takes the server's position outright: the
/// not-in-contact test comes before the far snap (OpenAC
/// `RuntimeRemoteArming`: `AirborneSnap` before
/// `RuntimeRemoteFarSnapPosition.ResolveArm`), so it lands on the spot
/// the server gave, overlap and all.
#[test]
#[ignore = "needs the retail dat files"]
fn a_far_monster_in_the_air_takes_the_servers_position_outright() {
    let (first, second) = far_drudge_sent_onto_another(true);
    let expected = Vec2::new(60.0, 58.0 + 192.0);
    println!("first {first:.3}, second {second:.3}");
    assert!(
        second.distance(expected) < 0.01,
        "at the server's spot: {second}, expected {expected}"
    );
}

// ── entering the world ─────────────────────────────────────────────────

/// How far apart the rings of a sliding placement's search are for a body
/// of the human setup at scale 1: OpenAC `Transition.FindPlacementPos`
/// searches rings out to 4 m in
/// ceil(4 / r) steps, r the foot sphere's radius (0.48 m: 9 rings of
/// 0.444 m), each ring's first sample at heading 0 (north, +y).
fn human_placement_ring() -> f32 {
    let r = human_contact() / 2.0;
    4.0 / (4.0 / r).ceil()
}

/// An object of the human setup with no motion (nothing animates it)
/// created standing at `position`.
fn still_object_at(rig: &mut Rig, guid: u32, position: Position) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(position),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    apply(
        rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
}

/// Where the world (and a window) has `guid`, flat.
fn shown_at(rig: &Rig, guid: u32) -> Option<Vec2> {
    rig.world
        .snapshot()
        .objects
        .iter()
        .find(|o| o.guid == guid)
        .and_then(|o| o.position)
        .map(|p| Vec2::new(p.origin.x, p.origin.y))
}

/// Where `guid`'s collision rows stand, flat (its first part's object
/// origin).
fn rows_at(rig: &Rig, guid: u32) -> Option<Vec2> {
    let local = rig.world.object(guid).unwrap().key().local_id;
    rig.movement
        .engine
        .shadows
        .capture_entries_for(local)
        .first()
        .map(|e| (e.position - e.rotation * e.local_position).truncate())
}

/// Two objects nothing moves (an item dropped twice on one spot, say) the
/// server creates on one spot enter the world apart: every top-level
/// object enters with its create's placement, placement | slide (OpenAC
/// `RuntimeInitialCreateResidenceState.Begin` routes every top-level
/// create through `RuntimeAuthoritativePositionRouteClassifier
/// .ClassifyCreate`, `InitialCreateFlags`, and `RuntimeRemoteFirstEntryState`
/// submits it). The first stands where
/// the server said; the second's search takes its first clear sample, the
/// third ring north (two human-setup bodies touch at 0.96 m: 3 x 0.444 =
/// 1.333 m). The world sees it there and its rows stand there. Put down
/// where the server said, the two stood 0.000 m apart. A later position
/// from the server stands as sent.
#[test]
#[ignore = "needs the retail dat files"]
fn two_objects_created_on_one_spot_enter_the_world_apart() {
    const FIRST: u32 = 0x7000_0D01;
    const SECOND: u32 = 0x7000_0D02;
    let mut rig = Rig::entered();
    let spot = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    let server = Vec2::new(spot.origin.x, spot.origin.y);
    still_object_at(&mut rig, FIRST, spot);
    still_object_at(&mut rig, SECOND, spot);
    let first = shown_at(&rig, FIRST).unwrap();
    let second = shown_at(&rig, SECOND).unwrap();
    let expected = server + Vec2::new(0.0, 3.0 * human_placement_ring());
    println!("first {first:.3}, second {second:.3}, expected {expected:.3}");
    assert!(
        first.distance(server) < 0.01,
        "the first at the server's spot: {first}"
    );
    assert!(
        second.distance(expected) < 0.01,
        "the second on the third ring north: {second}, expected {expected}"
    );
    let rows = rows_at(&rig, SECOND).unwrap();
    assert!(rows.distance(second) < 0.01, "its rows stand there: {rows}");
    // They rest there.
    rig.frames(30);
    assert!(shown_at(&rig, SECOND).unwrap().distance(expected) < 0.01);

    // The server's next position for it stands as sent.
    let moved = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 6.0, SPOT.1);
    position_update(&mut rig, SECOND, moved, (2, 0, 0));
    let now = shown_at(&rig, SECOND).unwrap();
    assert!(
        now.distance(Vec2::new(moved.origin.x, moved.origin.y)) < 0.01,
        "at the server's position: {now}"
    );
    let rows = rows_at(&rig, SECOND).unwrap();
    assert!(rows.distance(now) < 0.01, "its rows follow: {rows}");
}

/// Loot on the ground is ethereal and ignores collisions (the server's
/// ground items, corpses too: `PhysicsState` 0x414, ethereal,
/// ignore collisions, gravity), so two pieces dropped on one spot enter
/// the world on it, piled: the placement meets nothing.
#[test]
#[ignore = "needs the retail dat files"]
fn two_loot_items_dropped_on_one_spot_stay_piled_on_it() {
    const FIRST: u32 = 0x7000_0D41;
    const SECOND: u32 = 0x7000_0D42;
    let mut rig = Rig::entered();
    let spot = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    let server = Vec2::new(spot.origin.x, spot.origin.y);
    for guid in [FIRST, SECOND] {
        let mut physics = crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                position: 1,
                ..PhysicsTimestamps::default()
            },
            Some(spot),
        );
        physics.setup_id = Some(HUMAN_SETUP);
        physics.state = 0x0000_0414;
        apply(
            &mut rig,
            &Inbound::CreateObject(Box::new(CreateObject {
                guid,
                model: ModelData::default(),
                physics,
                weenie: None,
            })),
        );
    }
    for guid in [FIRST, SECOND] {
        let at = shown_at(&rig, guid).unwrap();
        println!("{guid:#X} at {at:.3}");
        assert!(at.distance(server) < 0.01, "{guid:#X} on the spot: {at}");
    }
}

/// Where the world (and a window) has `guid`, height included.
fn shown_at_3d(rig: &Rig, guid: u32) -> Option<Vec3> {
    rig.world
        .snapshot()
        .objects
        .iter()
        .find(|o| o.guid == guid)
        .and_then(|o| o.position)
        .map(|p| Vec3::new(p.origin.x, p.origin.y, p.origin.z))
}

/// A gem token (its real setup, 0x02000520: one 0.1 m sphere 0.073 m up,
/// step down 0.09 m) the server creates 1 m above flat ground, with no
/// velocity, as a respawned token can arrive: its state (0x414: ethereal,
/// ignore collisions, gravity) gives it gravity and its placement leaves it
/// touching nothing (the step down reaches 0.09 m). The game's create
/// enters it with placement | slide and makes it active, and every update
/// pulls a body with gravity that does not rest on walkable ground (OpenAC
/// steps every object's body, `RuntimeOrdinaryPhysicsUpdater.TryBegin`),
/// so it falls and lies on the ground. Left where the placement put it, it
/// hung 1 m in the air.
#[test]
#[ignore = "needs the retail dat files"]
fn an_item_created_in_the_air_with_gravity_falls_to_the_ground() {
    const TOKEN: u32 = 0x7000_0D51;
    const TOKEN_SETUP: u32 = 0x0200_0520;
    let mut rig = Rig::entered();
    let ground = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    let mut aloft = ground;
    aloft.origin.z += 1.0;
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(aloft),
    );
    physics.setup_id = Some(TOKEN_SETUP);
    physics.state = 0x0000_0414;
    apply(
        &mut rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid: TOKEN,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
    let entered = shown_at_3d(&rig, TOKEN).unwrap();
    println!("entered at {entered:.3} (ground z {:.3})", ground.origin.z);
    // A fall of 1 m takes 0.45 s; two seconds of frames land it.
    rig.frames(120);
    let landed = shown_at_3d(&rig, TOKEN).unwrap();
    println!("after 2 s at {landed:.3}");
    let height = landed.z - ground.origin.z;
    assert!(
        (0.0..0.05).contains(&height),
        "it lies on the ground: {height:.3} m above it"
    );
    assert!(
        Vec2::new(landed.x, landed.y).distance(Vec2::new(aloft.origin.x, aloft.origin.y)) < 0.01,
        "it fell straight down: {landed}"
    );
    let rows = rig.world.object(TOKEN).unwrap().key().local_id;
    let rows = rig.movement.engine.shadows.capture_entries_for(rows);
    let row = rows.first().unwrap();
    let row_z = (row.position - row.rotation * row.local_position).z;
    assert!(
        (row_z - landed.z).abs() < 0.01,
        "its rows lie with it: {row_z:.3}"
    );
    // It stays there.
    rig.frames(60);
    let still = shown_at_3d(&rig, TOKEN).unwrap();
    assert!(still.distance(landed) < 0.001, "it rests: {still}");
}

/// A gem token (setup 0x02000520, state 0x414: gravity) created 1 m above
/// flat ground, left two seconds to fall and land. With the rig, the
/// ground position under it and the spot it was created at.
fn landed_token(guid: u32) -> (Rig, Position, Position) {
    let mut rig = Rig::entered();
    let ground = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    let mut aloft = ground;
    aloft.origin.z += 1.0;
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(aloft),
    );
    physics.setup_id = Some(0x0200_0520);
    physics.state = 0x0000_0414;
    apply(
        &mut rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
    rig.frames(120);
    let landed = shown_at_3d(&rig, guid).unwrap();
    assert!(landed.z - ground.origin.z < 0.05, "it landed: {landed}");
    (rig, ground, aloft)
}

/// The server's position for `guid`, sent off the ground (no grounded
/// flag), with its position, teleport and force-position sequences.
fn airborne_position_update(rig: &mut Rig, guid: u32, position: Position, stamps: (u16, u16, u16)) {
    let (position_sequence, teleport_sequence, force_position_sequence) = stamps;
    apply(
        rig,
        &Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid,
            flags: 0,
            position,
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            position_sequence,
            teleport_sequence,
            force_position_sequence,
        }),
    );
}

/// The landed token is teleported back to the spot 1 m up it was created
/// at: a teleport puts the server's position down with a placement, and
/// the placement says what the body touches there: nothing, so gravity
/// pulls it again and it falls back to the ground. Snapped there keeping
/// the contact it had on the ground, it hung 1 m in the air.
#[test]
#[ignore = "needs the retail dat files"]
fn a_landed_item_teleported_back_into_the_air_falls_again() {
    const TOKEN: u32 = 0x7000_0D53;
    let (mut rig, ground, aloft) = landed_token(TOKEN);
    position_update(&mut rig, TOKEN, aloft, (2, 1, 0));
    let put_back = shown_at_3d(&rig, TOKEN).unwrap();
    println!("put back at {put_back:.3}");
    assert!(
        (put_back.z - aloft.origin.z).abs() < 0.01,
        "placed at the server's position: {put_back}"
    );
    rig.frames(120);
    let fallen = shown_at_3d(&rig, TOKEN).unwrap();
    println!("after 2 s at {fallen:.3}");
    let height = fallen.z - ground.origin.z;
    assert!(
        (0.0..0.05).contains(&height),
        "it fell back to the ground: {height:.3} m above it"
    );
}

/// A position for the landed token sent while the server has it off the
/// ground (no grounded flag) and no teleport changes nothing: the body
/// stays where it lies. Put where the server said, it hung 1 m up.
#[test]
#[ignore = "needs the retail dat files"]
fn a_position_sent_off_the_ground_leaves_a_landed_item_where_it_lies() {
    const TOKEN: u32 = 0x7000_0D54;
    let (mut rig, _, aloft) = landed_token(TOKEN);
    let landed = shown_at_3d(&rig, TOKEN).unwrap();
    airborne_position_update(&mut rig, TOKEN, aloft, (2, 0, 0));
    assert!(
        shown_at_3d(&rig, TOKEN).unwrap().distance(landed) < 0.001,
        "it lies where it was"
    );
    rig.frames(60);
    let still = shown_at_3d(&rig, TOKEN).unwrap();
    assert!(
        still.distance(landed) < 0.001,
        "it still lies there: {still}"
    );
}

/// A grounded position near the character for the landed token, 1 m up:
/// the body is not put there at once; it is caught up to from where it
/// lies, as every body catches up to a near position. Walking on the
/// ground it cannot rise to it, so the catch-up stalls, and a stalled
/// catch-up re-places the body at the server's position with a placement
/// that says what it touches there: nothing, so it falls back and lies on
/// the ground again. Put up there at once with the contact it had, it hung
/// 1 m in the air.
#[test]
#[ignore = "needs the retail dat files"]
fn a_near_grounded_position_for_a_landed_item_is_caught_up_to_not_snapped() {
    const TOKEN: u32 = 0x7000_0D55;
    let (mut rig, ground, aloft) = landed_token(TOKEN);
    position_update(&mut rig, TOKEN, aloft, (2, 0, 0));
    let at_once = shown_at_3d(&rig, TOKEN).unwrap();
    assert!(
        at_once.z - ground.origin.z < 0.05,
        "not put up there at once: {at_once}"
    );
    let mut highest = at_once.z;
    for _ in 0..180 {
        rig.frames(1);
        highest = highest.max(shown_at_3d(&rig, TOKEN).unwrap().z);
    }
    assert!(
        (highest - aloft.origin.z).abs() < 0.01,
        "the stalled catch-up put it at the server's position: {highest:.3}"
    );
    let lies = shown_at_3d(&rig, TOKEN).unwrap();
    println!("after 3 s at {lies:.3}");
    assert!(
        (0.0..0.05).contains(&(lies.z - ground.origin.z)),
        "it fell back to the ground: {lies}"
    );
}

/// An object without gravity (a sign hung on a wall: state 0) created in
/// the air stays there: nothing accelerates it.
#[test]
#[ignore = "needs the retail dat files"]
fn an_item_created_in_the_air_without_gravity_stays_there() {
    const SIGN: u32 = 0x7000_0D52;
    let mut rig = Rig::entered();
    let mut aloft = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    aloft.origin.z += 1.0;
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(aloft),
    );
    physics.setup_id = Some(0x0200_0520);
    physics.state = 0;
    apply(
        &mut rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid: SIGN,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
    rig.frames(120);
    let at = shown_at_3d(&rig, SIGN).unwrap();
    assert!(
        (at.z - aloft.origin.z).abs() < 0.001,
        "it stays where it was put: {at}"
    );
}

/// A missile the server creates where another object stands enters the
/// world clear of it: a projectile's create routes as every other
/// (OpenAC `ClassifyCreate`: `ProjectileAuthoritative`, `InitialCreateFlags`),
/// so its flight starts on its placement's first clear sample, north (2 m
/// for the first one-sphere setup, 1.133 m, beside a human-setup post).
/// Put down where the server said, it started inside the post.
#[test]
#[ignore = "needs the retail dat files"]
fn a_missile_created_inside_another_object_starts_clear_of_it() {
    const POST: u32 = 0x7000_0D11;
    const ARROW: u32 = 0x8000_0D12;
    let mut rig = Rig::entered();
    let spot = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 3.0, SPOT.1);
    still_object_at(&mut rig, POST, spot);
    let from = Vec3::new(spot.origin.x, spot.origin.y, spot.origin.z + 0.5);
    // Missile, report collisions, align path, path clipped; no gravity, at
    // rest, so it stays where it entered.
    launch(&mut rig, ARROW, from, Vec3::ZERO, 0x0000_0348);
    let local = rig.world.object(ARROW).unwrap().key().local_id;
    assert!(rig.movement.projectiles.contains_key(&local), "it flies");
    let content = content();
    let setup = content.setup(one_sphere_setup()).unwrap();
    let r = setup.spheres[0].radius;
    let ring = 4.0 / (4.0 / r).ceil();
    let placed = live(&rig, ARROW);
    let off = placed - from;
    println!(
        "missile sphere {r:.3} m, ring {ring:.3} m: entered at {placed:.3}, {off:.3} from the server's"
    );
    assert!(off.x.abs() < 0.01, "due north: {off}");
    assert!(off.y > 0.01, "moved off the object: {off}");
    // The first ring whose north sample clears the object's foot sphere:
    // the missile's 1.133 m sphere and the human 0.48 m one need 1.613 m,
    // two rings of 1.0 m.
    let expected = ((r + human_contact() / 2.0) / ring).ceil() * ring;
    assert!(
        (off.y - expected).abs() < 0.01,
        "on the first clear ring north: {off}, expected {expected} m"
    );
}

/// An object of the drudge's setup with no motion, solid (state 0), created
/// at `position` with the public description `weenie`.
fn drudge_shaped_at(
    rig: &mut Rig,
    guid: u32,
    position: Position,
    weenie: Option<ac_proto::PublicWeenieDesc>,
) {
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 1,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(position),
    );
    physics.setup_id = Some(DRUDGE_SETUP);
    physics.scale = Some(DRUDGE_SCALE);
    apply(
        rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics,
            weenie,
        })),
    );
}

/// Retail never searches a corpse, a hook or a house's storage chest aside
/// when it is placed: retail forces it into its cell as it is. A corpse the server
/// creates 0.29 m into the north wall of Holtburg's 0x01000830 (a drudge
/// that died pressed against it) stays where the server put it and is
/// drawn; the same shape without the corpse bit is placed one ring
/// (0.5 m) north of the spot, beside the wall.
#[test]
#[ignore = "needs the retail dat files"]
fn a_corpse_created_against_a_wall_stays_where_the_server_put_it() {
    const CORPSE: u32 = 0x7000_0D51;
    const BOULDER: u32 = 0x7000_0D52;
    let mut corpse_at = None;
    let mut corpse_rig = None;
    for (guid, corpse) in [(CORPSE, true), (BOULDER, false)] {
        // Each in a world of its own, on the same stretch of wall.
        let mut rig = Rig::entered();
        let x = WALL_X;
        let flush = drudge_flush_with_north_wall(&rig, x);
        let spot = ground_position(&rig, 0xA9B4_0000, x, flush - 0.29);
        let weenie = ac_proto::PublicWeenieDesc {
            name: if corpse {
                "Corpse of Drudge"
            } else {
                "Boulder"
            }
            .into(),
            item_type: ac_core::items::ItemType::CONTAINER.bits(),
            object_description_flags: if corpse {
                ac_core::items::PublicWeenieFlags::CORPSE.bits()
            } else {
                0
            },
            ..ac_proto::PublicWeenieDesc::default()
        };
        drudge_shaped_at(&mut rig, guid, spot, Some(weenie));
        let shown = shown_at(&rig, guid).expect("drawn");
        let server = Vec2::new(spot.origin.x, spot.origin.y);
        println!("{guid:#X}: server {server:.3}, shown {shown:.3} (flush {flush:.3})");
        if corpse {
            corpse_at = Some(server);
            assert!(shown.distance(server) < 0.01, "the corpse stays: {shown}");
        } else {
            let expected = server + Vec2::new(0.0, drudge_placement_ring());
            assert!(
                shown.distance(expected) < 0.01,
                "the boulder is placed beside the wall: {shown}, expected {expected}"
            );
        }
        if corpse {
            corpse_rig = Some(rig);
        }
    }
    let mut rig = corpse_rig.unwrap();
    rig.frames(10);
    let shown = shown_at(&rig, CORPSE).unwrap();
    assert!(shown.distance(corpse_at.unwrap()) < 0.01);
}

/// What an object is placed as follows retail's checks, in order: a hook (a hook type and
/// hook item types), the house storage chest (its weenie class is the
/// dats' storage class), a corpse (bit 0x2000 of the public bitfield);
/// else ordinary.
#[test]
fn hooks_storage_and_corpses_are_put_into_their_cell() {
    use ac_core::items::{ClientObject, PublicWeenieFlags};
    use ac_physics::PhysicsPlacementClass as C;
    const STORAGE: u32 = 9687;
    let mut hook = ClientObject::new(1);
    hook.hook_type = 2;
    hook.hook_item_types = 0x1;
    let mut empty_hook = ClientObject::new(2);
    empty_hook.hook_type = 2;
    let mut storage = ClientObject::new(3);
    storage.weenie_class_id = STORAGE;
    let mut corpse = ClientObject::new(4);
    corpse.public_weenie_bitfield = Some(PublicWeenieFlags::CORPSE);
    let other = ClientObject::new(5);
    assert_eq!(placement_class(Some(&hook), STORAGE), C::Hook);
    assert_eq!(placement_class(Some(&empty_hook), STORAGE), C::Ordinary);
    assert_eq!(placement_class(Some(&storage), STORAGE), C::Storage);
    assert_eq!(placement_class(Some(&storage), 0), C::Ordinary);
    assert_eq!(placement_class(Some(&corpse), STORAGE), C::Corpse);
    assert_eq!(placement_class(Some(&other), STORAGE), C::Ordinary);
    assert_eq!(placement_class(None, STORAGE), C::Ordinary);
}

/// The dats name the house storage chest's weenie class (enum map
/// category 12, key 0x10000001, as retail's storage test reads it).
#[test]
#[ignore = "needs the retail dat files"]
fn the_dats_name_the_storage_chests_weenie_class() {
    let class = content().storage_weenie_class();
    println!("storage weenie class {class} (0x{class:X})");
    assert_ne!(class, 0);
}

/// Inside the Holtburg building 0x01000BC3 (31, 159) the placement finds
/// no clear spot within 4 m.
const NOWHERE: (f32, f32) = (31.0, 159.0);

/// An object whose create's placement finds no clear spot does not enter
/// the world (OpenAC `RuntimeRemoteFirstEntryState.AdvanceCore`: the
/// placement rejected, the entry ends `RejectedAuthority` and the
/// residence lease stays: the object is in the table but has no cell, the
/// window does not project it, and its later updates queue behind the
/// lease): no body, not drawn, and its later positions do not move it.
/// Its collision stays where the window registered it, at the server's
/// position. A new incarnation enters again.
#[test]
#[ignore = "needs the retail dat files"]
fn an_object_created_where_nothing_is_clear_does_not_enter_the_world() {
    const CHEST: u32 = 0x7000_0D21;
    const DRUDGE: u32 = 0x7000_0D22;
    let mut rig = Rig::entered();
    let inside = ground_position(&rig, 0xA9B4_0000, NOWHERE.0, NOWHERE.1);
    still_object_at(&mut rig, CHEST, inside);
    drudge_created(&mut rig, DRUDGE, inside);
    for guid in [CHEST, DRUDGE] {
        let object = rig.world.object(guid).unwrap();
        let local = object.key().local_id;
        assert!(object.entry_refused(), "{guid:#X} did not enter");
        assert!(shown_at(&rig, guid).is_none(), "{guid:#X} is not drawn");
        assert!(
            !rig.movement.remotes.contains_key(&local),
            "{guid:#X}: no body"
        );
        assert!(!rig.movement.resting.contains_key(&local));
        let rows = rows_at(&rig, guid).unwrap();
        assert!(
            rows.distance(Vec2::new(inside.origin.x, inside.origin.y)) < 0.01,
            "{guid:#X}'s rows at the server's position: {rows}"
        );
    }
    // A position somewhere clear does not bring it in.
    let clear = ground_position(&rig, 0xA9B4_0000, SPOT.0 + 4.0, SPOT.1);
    position_update(&mut rig, DRUDGE, clear, (2, 0, 0));
    position_update(&mut rig, CHEST, clear, (2, 0, 0));
    rig.frames(5);
    let local = rig.world.object(DRUDGE).unwrap().key().local_id;
    assert!(!rig.movement.remotes.contains_key(&local), "still no body");
    assert!(shown_at(&rig, DRUDGE).is_none() && shown_at(&rig, CHEST).is_none());
    let rows = rows_at(&rig, CHEST).unwrap();
    assert!(
        rows.distance(Vec2::new(inside.origin.x, inside.origin.y)) < 0.01,
        "its rows stay: {rows}"
    );

    // A new incarnation somewhere clear enters.
    let mut physics = crate::world::tests::physics(
        PhysicsTimestamps {
            instance: 2,
            position: 1,
            ..PhysicsTimestamps::default()
        },
        Some(clear),
    );
    physics.setup_id = Some(HUMAN_SETUP);
    apply(
        &mut rig,
        &Inbound::CreateObject(Box::new(CreateObject {
            guid: CHEST,
            model: ModelData::default(),
            physics,
            weenie: None,
        })),
    );
    assert!(!rig.world.object(CHEST).unwrap().entry_refused());
    assert!(
        shown_at(&rig, CHEST).is_some(),
        "the new incarnation entered"
    );
}

/// Only the create's first entry refuses an object (OpenAC: only
/// `RuntimeRemoteFirstEntryState` ends an entry without a body; a later
/// placement that is rejected keeps the object, `RuntimeRemotePlacementDriveController
/// .SubmitAndResolve` `RejectedByPlacement`). A drudge that entered the
/// world, lost its body (its landblock left the resident world, here
/// dropped by hand) and enters again where nothing is clear gets its body
/// where the server put it, and stays in the world.
#[test]
#[ignore = "needs the retail dat files"]
fn an_object_entering_again_where_nothing_is_clear_stays_in_the_world() {
    const DRUDGE: u32 = 0x7000_0D31;
    let mut rig = Rig::entered();
    drudge_at(&mut rig, DRUDGE, SPOT.0 + 4.0, SPOT.1);
    let local = rig.world.object(DRUDGE).unwrap().key().local_id;
    assert!(rig.movement.remotes.contains_key(&local), "it entered");
    rig.movement.drop_remotes(&mut rig.world);
    assert!(!rig.movement.remotes.contains_key(&local));
    let inside = ground_position(&rig, 0xA9B4_0000, NOWHERE.0, NOWHERE.1);
    position_update(&mut rig, DRUDGE, inside, (2, 0, 0));
    assert!(
        !rig.world.object(DRUDGE).unwrap().entry_refused(),
        "not refused"
    );
    assert!(rig.movement.remotes.contains_key(&local), "a body again");
    let body = remote_body(&rig, DRUDGE);
    let server = Vec2::new(inside.origin.x, inside.origin.y);
    assert!(
        body.distance(server) < 0.01,
        "at the server's position: {body}"
    );
    assert!(shown_at(&rig, DRUDGE).is_some(), "drawn");
}

/// The character created where its placement finds no clear spot waits out
/// of the world, placed again each frame (OpenAC
/// `RuntimeLocalPlayerFirstEntryState`: a `RejectedPlacement` activation
/// commit returns `AwaitingActivation`, and `RuntimeFirstEntryDriveController
/// .DriveAll` advances it again on every pump), rather than standing where
/// the server said.
#[test]
#[ignore = "needs the retail dat files"]
fn the_character_created_where_nothing_is_clear_waits_out_of_the_world() {
    let mut rig = Rig::created_at(Some(HUMAN_MOTION), NOWHERE);
    rig.frames(10);
    assert!(rig.movement.controller().is_none(), "no body");
    assert!(
        rig.movement.pending_entry.is_some(),
        "still waiting to enter"
    );
}

/// A missile and a moving ordinary object in the landblock north of the
/// character's are seen where their bodies are: the world's position is
/// relative to their own landblock (as a remote body's is,
/// `remote::live_position`). Their cell frame is landblock-relative
/// already; taking the landblock's corner off it again put them 192 m
/// south of where they flew.
#[test]
#[ignore = "needs the retail dat files"]
fn bodies_in_a_neighbouring_landblock_are_seen_where_they_are() {
    const ARROW: u32 = 0x8000_0E01;
    const CRATE: u32 = 0x7000_0E02;
    let mut rig = Rig::entered();
    for (guid, x, state, velocity) in [
        // Missile, report collisions, align path, path clipped; at rest.
        (ARROW, 60.0, 0x0000_0348, Vector3::default()),
        // An ordinary object drifting east, no gravity.
        (
            CRATE,
            70.0,
            0,
            Vector3 {
                x: 0.01,
                y: 0.0,
                z: 0.0,
            },
        ),
    ] {
        let mut at = ground_position(&rig, NORTH, x, 20.0);
        // Clear of the ground (the setup's sphere is 1.133 m).
        at.origin.z += 2.5;
        let mut physics = crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                position: 1,
                ..PhysicsTimestamps::default()
            },
            Some(at),
        );
        physics.setup_id = Some(one_sphere_setup());
        physics.state = state;
        physics.velocity = Some(velocity);
        apply(
            &mut rig,
            &Inbound::CreateObject(Box::new(CreateObject {
                guid,
                model: ModelData::default(),
                physics,
                weenie: None,
            })),
        );
        let local = rig.world.object(guid).unwrap().key().local_id;
        assert!(
            rig.movement.projectiles.contains_key(&local)
                || rig.movement.ordinaries.contains_key(&local),
            "{guid:#X} has a body"
        );
        let seen = rig.world.object(guid).unwrap().position().copied().unwrap();
        println!("{guid:#X}: sent {at:?}, seen {seen:?}");
        assert_eq!(seen.cell_id >> 16, NORTH >> 16, "in its own landblock");
        let off = Vec3::new(
            seen.origin.x - at.origin.x,
            seen.origin.y - at.origin.y,
            seen.origin.z - at.origin.z,
        );
        assert!(off.length() < 0.05, "{guid:#X} seen where it is: {off}");
    }
}

/// A placement forced into its cell (a hook, a storage chest, a corpse)
/// commits only the frame and the cell to the body; it keeps the contact it
/// had. Taking the placement's empty contact fields, it lost its contact
/// and gravity pulled it out of the cell it was forced into.
#[test]
fn a_placement_forced_into_its_cell_keeps_the_contact_the_body_had() {
    let mut body = ac_physics::PhysicsBody::default();
    body.transient_state
        .insert(ac_physics::TransientState::CONTACT | ac_physics::TransientState::ON_WALKABLE);
    body.contact_plane_valid = true;
    let placed = ac_physics::PhysicsSetPositionResult {
        residence: ac_physics::PhysicsResidenceDisposition::Committed,
        position: Vec3::new(12.0, 10.0, 51.0),
        orientation: Quat::IDENTITY,
        cell_id: 0xA9B4_0002,
        cell_local_position: Vec3::new(12.0, 10.0, 51.0),
        forced_into_cell: true,
        ..ac_physics::PhysicsSetPositionResult::default()
    };
    commit_placement(&mut body, &placed, Quat::IDENTITY);
    assert_eq!(body.position(), placed.position);
    assert!(body.in_contact(), "keeps its contact");
    assert!(body.on_walkable(), "keeps its walkable ground");
    assert!(body.contact_plane_valid, "keeps its contact plane");
}

/// Modern mouse turning on the real character (its own animation turns
/// it): the view 90 degrees to the right turns the body right through the
/// turn motion, one way only, frame by frame, lands exactly on the view,
/// and the server hears the start and the stop of one turn.
#[test]
#[ignore = "needs the retail dat files"]
fn modern_mouse_turning_turns_the_character_once_onto_the_view() {
    use ac_motion::motion_command::TURN_RIGHT;
    let mut rig = Rig::entered();
    rig.frames(10);
    rig.command(MovementCommand::BeginModernMouseLook);
    let begun = rig.movement.take_outbound_records();
    assert!(
        begun.iter().any(|r| r.kind == OutboundKind::MoveToState),
        "beginning sends the movement: {begun:?}"
    );
    let start_yaw = rig.movement.controller().unwrap().yaw();
    let (_, start, _) = rig.body();
    // Compass headings grow clockwise: a lower yaw is to the right.
    let target = start_yaw - std::f32::consts::FRAC_PI_2;
    let mut headings = vec![start];
    let mut turned_by_the_turn_motion = false;
    for _ in 0..180 {
        rig.command(MovementCommand::MouseLookFacing(target));
        rig.frames(1);
        headings.push(rig.body().1);
        let view = rig.world.object(PLAYER).unwrap().motion_view().unwrap();
        turned_by_the_turn_motion |= view.state.turn_command == TURN_RIGHT;
    }
    assert!(turned_by_the_turn_motion, "the turn motion turns the body");
    for pair in headings.windows(2) {
        let step = (pair[1] - pair[0] + 540.0).rem_euclid(360.0) - 180.0;
        assert!(step >= -1e-3, "the heading only grows: {pair:?}");
    }
    let landed = (start + 90.0).rem_euclid(360.0);
    let off = ((headings.last().unwrap() - landed + 540.0).rem_euclid(360.0) - 180.0).abs();
    assert!(off < 0.01, "lands on the view: {off} degrees off");
    let c = rig.movement.controller().unwrap();
    assert!(c.mouse_look_facing_settled());
    let turn: Vec<_> = rig
        .movement
        .take_outbound_records()
        .into_iter()
        .filter(|r| r.kind == OutboundKind::MoveToState)
        .collect();
    assert_eq!(turn.len(), 2, "one turn: its start and its stop: {turn:?}");
    rig.command(MovementCommand::EndMouseLook);
    assert!(
        rig.movement
            .take_outbound_records()
            .iter()
            .any(|r| r.kind == OutboundKind::MoveToState)
    );
    rig.frames(30);
    assert!((rig.body().1 - headings.last().unwrap()).abs() < 0.01);
}

/// The window going away during a modern mouse look ends it (the server
/// hears it), so the character is not left turning toward an old view.
#[test]
#[ignore = "needs the retail dat files"]
fn a_detached_window_ends_its_modern_mouse_look() {
    let mut rig = Rig::entered();
    rig.frames(10);
    rig.command(MovementCommand::BeginModernMouseLook);
    assert!(
        rig.movement
            .controller()
            .unwrap()
            .modern_mouse_look_active()
    );
    let _ = rig.movement.take_outbound_records();
    rig.movement.release_keys();
    rig.frames(1);
    assert!(
        !rig.movement
            .controller()
            .unwrap()
            .modern_mouse_look_active()
    );
    assert!(
        rig.movement
            .take_outbound_records()
            .iter()
            .any(|r| r.kind == OutboundKind::MoveToState)
    );
}

// ── the position log ───────────────────────────────────────────────────

/// The position log's drudge, 2 m east of the character.
const LOGGED_DRUDGE: u32 = 0x7000_0E01;

/// The character entered with the position log on, a drudge 2 m east of
/// it, its first server position taken (where it stands) and the lines so
/// far dropped.
fn logged_drudge() -> Rig {
    let mut rig = Rig::entered();
    rig.movement.enable_position_log();
    let (x, y) = SPOT;
    drudge_at(&mut rig, LOGGED_DRUDGE, x + 2.0, y);
    rig.frames(2);
    let standing = ground_position(&rig, 0xA9B4_0000, x + 2.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, standing, (2, 0, 0));
    rig.frames(2);
    rig.movement.take_position_log_lines();
    rig
}

/// The single position log line just written.
fn one_line(rig: &mut Rig) -> String {
    let lines = rig.movement.take_position_log_lines();
    assert_eq!(lines.len(), 1, "{lines:#?}");
    lines.into_iter().next().unwrap()
}

/// The body-to-server gaps a line gives, before and after.
fn gaps(line: &str) -> (f32, f32) {
    let rest = line.split(" gap=").nth(1).expect("a gap");
    let mut parts = rest.split_whitespace();
    let before = parts.next().unwrap().parse().unwrap();
    assert_eq!(parts.next(), Some("->"));
    (before, parts.next().unwrap().parse().unwrap())
}

/// The first server position of an object is taken outright, and the log
/// says so.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_a_first_update_a_jump() {
    let mut rig = Rig::entered();
    rig.movement.enable_position_log();
    let (x, y) = SPOT;
    drudge_at(&mut rig, LOGGED_DRUDGE, x + 2.0, y);
    rig.frames(2);
    let standing = ground_position(&rig, 0xA9B4_0000, x + 2.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, standing, (2, 0, 0));
    let line = one_line(&mut rig);
    assert!(
        line.contains("update 0x70000E01 \"Drudge\" seq=1/2/0/0 ground=yes jump (first update)"),
        "{line}"
    );
}

/// A near server position on the ground is caught up to: queued, the body
/// not moved yet, the gap as it was.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_a_near_update_a_glide_with_its_gap() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    let near = ground_position(&rig, 0xA9B4_0000, x + 3.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, near, (3, 0, 0));
    let line = one_line(&mut rig);
    assert!(line.contains("ground=yes glide server="), "{line}");
    let (before, after) = gaps(&line);
    assert!((before - 1.0).abs() < 0.05, "{line}");
    assert!((after - before).abs() < 0.01, "{line}");
}

/// A server position more than 4 m from a body on the ground puts the body
/// there at once.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_a_far_update_a_jump_and_the_gap_closes() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    let far = ground_position(&rig, 0xA9B4_0000, x + 8.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, far, (3, 0, 0));
    let line = one_line(&mut rig);
    assert!(line.contains("jump (more than 4 m)"), "{line}");
    let (before, after) = gaps(&line);
    assert!((before - 6.0).abs() < 0.05, "{line}");
    assert!(after < 0.05, "{line}");
}

/// A server position with a new teleport stamp places the body.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_a_teleport_placed() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    let there = ground_position(&rig, 0xA9B4_0000, x + 6.0, y + 6.0);
    position_update(&mut rig, LOGGED_DRUDGE, there, (3, 1, 0));
    let line = one_line(&mut rig);
    assert!(line.contains("placed (teleport)"), "{line}");
    assert!(gaps(&line).1 < 0.6, "{line}");
}

/// A server position without the on-ground flag leaves the body where it
/// was, but writes the server's cell onto it: the log says both.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_an_update_off_the_ground_ignored_and_the_cell_it_wrote() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    // 7 m east, across the outdoor cell boundary at x = 72.
    let across = ground_position(&rig, 0xA9B4_0000, x + 14.0, y);
    let here = rig
        .movement
        .remotes
        .values()
        .find(|b| b.guid == LOGGED_DRUDGE)
        .unwrap()
        .driver
        .obj
        .cell_id;
    assert_ne!(here, across.cell_id, "the test needs another cell");
    airborne_position_update(&mut rig, LOGGED_DRUDGE, across, (3, 0, 0));
    let line = one_line(&mut rig);
    assert!(line.contains("ground=no"), "{line}");
    assert!(
        line.contains(&format!(
            "ignored, no ground flag, cell written 0x{:08X} over 0x{here:08X}",
            across.cell_id
        )),
        "{line}"
    );
    let (before, after) = gaps(&line);
    assert!((before - 12.0).abs() < 0.05, "{line}");
    assert_eq!(before, after, "{line}");
}

/// A server position whose counters are not newer is not applied, and the
/// log says it was rejected.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_names_a_stale_update_rejected() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    let near = ground_position(&rig, 0xA9B4_0000, x + 3.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, near, (2, 0, 0));
    let line = one_line(&mut rig);
    assert!(
        line.contains("seq=1/2/0/0 ground=yes rejected (sequence)"),
        "{line}"
    );
    let (before, after) = gaps(&line);
    assert!((before - 1.0).abs() < 0.05 && before == after, "{line}");
}

/// An object farther than 30 m is not logged.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_leaves_out_objects_beyond_30_m() {
    let mut rig = Rig::entered();
    rig.movement.enable_position_log();
    let (x, y) = SPOT;
    drudge_at(&mut rig, LOGGED_DRUDGE, x + 40.0, y);
    rig.frames(2);
    let there = ground_position(&rig, 0xA9B4_0000, x + 40.0, y);
    position_update(&mut rig, LOGGED_DRUDGE, there, (2, 0, 0));
    rig.frames(200);
    assert!(rig.movement.take_position_log_lines().is_empty());
}

/// A body that stays more than 4 m from the server's place is reported
/// every 2 s, with the time since its last update and since one moved it.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_reports_a_body_left_apart_every_two_seconds() {
    let mut rig = logged_drudge();
    let (x, y) = SPOT;
    let away = ground_position(&rig, 0xA9B4_0000, x + 9.0, y);
    airborne_position_update(&mut rig, LOGGED_DRUDGE, away, (3, 0, 0));
    rig.movement.take_position_log_lines();
    // 4.5 s of frames.
    rig.frames(270);
    let lines = rig.movement.take_position_log_lines();
    let apart: Vec<_> = lines.iter().filter(|l| l.contains(" apart ")).collect();
    assert_eq!(apart.len(), 3, "at 0, 2 and 4 s: {lines:#?}");
    assert!(
        apart[0].contains("apart 0x70000E01 \"Drudge\" gap=7.00 m"),
        "{}",
        apart[0]
    );
    // Moved last by the update before the one off the ground.
    assert!(apart[2].contains("since update=4.0"), "{}", apart[2]);
    assert!(apart[2].contains("since moved=4.0"), "{}", apart[2]);
}

/// A server order for the character naming `target`, written at a place
/// 8 m south of it, with movement counter `sequence`.
fn move_to_order(rig: &Rig, target: u32, sequence: u16) -> Inbound<'static> {
    let (start, _, _) = rig.body();
    let written = Vec3::new(start.x, start.y - 8.0, start.z);
    Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid: PLAYER,
        instance_sequence: 1,
        movement_sequence: sequence,
        server_control_sequence: 1,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 6,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::MoveToObject {
                target,
                path: ac_proto::MoveToPath {
                    origin_cell_id: outdoor_cell(written.x, written.y),
                    origin: Vector3 {
                        x: written.x,
                        y: written.y,
                        z: written.z,
                    },
                    bitfield: 0x403,
                    distance_to_object: 0.6,
                    min_distance: 0.0,
                    fail_distance: 15.0,
                    speed: 1.0,
                    walk_run_threshold: 15.0,
                    desired_heading: 0.0,
                    run_rate: 2.5,
                },
            },
        },
    }))
}

/// A server order to a creature with a body steers to the body, and the
/// log gives the body's place and its gap from the server's place.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_says_a_move_to_a_creature_steers_to_its_body() {
    let mut rig = logged_drudge();
    let order = move_to_order(&rig, LOGGED_DRUDGE, 2);
    apply(&mut rig, &order);
    let line = one_line(&mut rig);
    assert!(
        line.contains("order MoveToObject target=0x70000E01"),
        "{line}"
    );
    assert!(line.contains(" via=body steer=0x"), "{line}");
    assert!(line.contains("target gap=0.00 m"), "{line}");
    assert!(
        line.ends_with("distance=0.60 min=0.00 fail=15.00 speed=1.00 walk_run=15.00 run_rate=2.50"),
        "{line}"
    );
}

/// A server order to a thing nothing simulates steers to a stand-in where
/// it stands; one to a thing not known here steers to the order's place.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_says_when_a_move_to_falls_back_to_the_orders_place() {
    const CHEST: u32 = 0x7000_0457;
    let mut rig = Rig::entered();
    rig.movement.enable_position_log();
    place_still_object(&mut rig, CHEST, 6.0);
    rig.movement.take_position_log_lines();
    let order = move_to_order(&rig, CHEST, 2);
    apply(&mut rig, &order);
    let line = one_line(&mut rig);
    assert!(line.contains(" via=stand-in steer=0x"), "{line}");

    let order = move_to_order(&rig, 0x7000_0999, 3);
    apply(&mut rig, &order);
    let line = one_line(&mut rig);
    assert!(
        line.contains(" via=order place (no object here) steer=0x"),
        "{line}"
    );
}

/// Off (the default), the position log keeps nothing.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_keeps_nothing_while_off() {
    let mut rig = Rig::entered();
    let (x, y) = SPOT;
    drudge_at(&mut rig, LOGGED_DRUDGE, x + 2.0, y);
    rig.frames(2);
    let away = ground_position(&rig, 0xA9B4_0000, x + 9.0, y);
    airborne_position_update(&mut rig, LOGGED_DRUDGE, away, (2, 0, 0));
    let order = move_to_order(&rig, LOGGED_DRUDGE, 2);
    apply(&mut rig, &order);
    rig.frames(150);
    assert!(rig.movement.take_position_log_lines().is_empty());
}

/// Where the drudge and the character are after each frame of one
/// scenario, with the position log on or off: the drudge's first server
/// position, a near one (a glide), a far one (a jump), one off the ground,
/// a teleport and a stale one, then a server order sending the character to
/// it; with the log on, it writes lines throughout.
fn places_through_logged_scenario(log: bool) -> Vec<(Vec3, Vec3)> {
    let mut rig = Rig::entered();
    if log {
        rig.movement.enable_position_log();
    }
    let (x, y) = SPOT;
    drudge_at(&mut rig, LOGGED_DRUDGE, x + 2.0, y);
    let mut places = Vec::new();
    let mut run = |rig: &mut Rig, frames: u32| {
        for _ in 0..frames {
            rig.frames(1);
            places.push((live(rig, LOGGED_DRUDGE), rig.body().0));
        }
    };
    run(&mut rig, 2);
    let updates = [
        (x + 2.0, y, (2, 0, 0), true),
        (x + 3.0, y, (3, 0, 0), true),
        (x + 8.0, y, (4, 0, 0), true),
        (x + 14.0, y, (5, 0, 0), false),
        (x + 6.0, y + 6.0, (6, 1, 0), true),
        (x + 9.0, y, (5, 1, 0), true),
    ];
    for (to_x, to_y, stamps, grounded) in updates {
        let to = ground_position(&rig, 0xA9B4_0000, to_x, to_y);
        if grounded {
            position_update(&mut rig, LOGGED_DRUDGE, to, stamps);
        } else {
            airborne_position_update(&mut rig, LOGGED_DRUDGE, to, stamps);
        }
        run(&mut rig, 30);
    }
    let order = move_to_order(&rig, LOGGED_DRUDGE, 2);
    apply(&mut rig, &order);
    run(&mut rig, 120);
    assert_eq!(
        rig.movement.take_position_log_lines().is_empty(),
        !log,
        "the log writes only while on"
    );
    places
}

/// The position log only watches: the same scenario with it on and off
/// moves the drudge and the character exactly alike, frame by frame.
#[test]
#[ignore = "needs the retail dat files"]
fn the_position_log_changes_nothing_about_where_bodies_go() {
    let off = places_through_logged_scenario(false);
    let on = places_through_logged_scenario(true);
    assert_eq!(on.len(), off.len());
    for (frame, (on, off)) in on.iter().zip(&off).enumerate() {
        assert_eq!(on, off, "frame {frame}");
    }
    // The scenario moved both.
    assert_ne!(off.first().unwrap().0, off.last().unwrap().0);
    assert_ne!(off.first().unwrap().1, off.last().unwrap().1);
}
