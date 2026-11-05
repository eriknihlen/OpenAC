//! The client's own goal source stands a place in a dungeon cell on the
//! floor under it, not only the walk's test double. Ported from OpenAC PR
//! #222 `tests/AcDream.App.Tests/Navigation/DungeonPlaceFloorTests.cs`
//! (its cases through `RuntimeNavigationGoalSource`; the rest are in
//! `ac-nav`'s `tests/walk.rs`, `dungeon_place_floor`).

use std::collections::HashMap;

use ac_nav::NavBody;
use ac_nav::geometry;
use ac_nav::motor::{MoveChannel, MovePace, MoveRequest, ScriptedMoveSnapshot};
use ac_nav::sources::on_the_cell_floor;
use ac_nav::walk::{
    NavGoalSource, PlanMode, PlanStatus, PreviewTarget, WalkBody, WalkBodySample, WalkController,
    WalkWorld,
};
use ac_physics::movement::{MovementEnv, NoTargets, PlayerMovementController};
use ac_physics::{
    CellSurface, PhysicsEngine, RegistrationSite, ShadowCollisionType, SingleShape, TerrainSurface,
};
use glam::{Quat, Vec3};

use super::sources::Goals;
use crate::movement::residency::Residency;
use crate::world::World;

const LOW_ROOM: u32 = 0xA9B4_0100;
const TALL_ROOM: u32 = 0xA9B4_0101;
const STACKED_LOWER: u32 = 0xA9B4_0102;
const STACKED_UPPER: u32 = 0xA9B4_0103;
const FLOOR: f32 = -30.0;
const PLATFORM_TOP: f32 = FLOOR + 2.0;
const PLATFORM_RADIUS: f32 = 3.0;
const LANDBLOCK: u32 = 0xA9B4_FFFF;

/// The middle of the 5 m room's bounds, 2.5 m over its floor, as a dungeon
/// map gives it.
const TALL_MIDDLE: Vec3 = Vec3::new(30.0, 10.0, -27.5);
/// Where the character stands, on the low room's floor.
const START: Vec3 = Vec3::new(10.0, 10.0, FLOOR);

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

fn cell(
    cell_id: u32,
    x0: f32,
    x1: f32,
    floor: Option<f32>,
    low: f32,
    top: f32,
    roofed: bool,
) -> CellSurface {
    let mut vertices = HashMap::new();
    let mut polygons = Vec::new();
    let mut add = |corners: [Vec3; 4]| {
        let mut ids = Vec::new();
        for corner in corners {
            let id = vertices.len() as u16;
            ids.push(id);
            vertices.insert(id, corner);
        }
        polygons.push(ids);
    };
    if let Some(z) = floor {
        add([v(x0, 0.0, z), v(x1, 0.0, z), v(x1, 20.0, z), v(x0, 20.0, z)]);
    }
    if roofed {
        add([
            v(x0, 0.0, top),
            v(x0, 20.0, top),
            v(x1, 20.0, top),
            v(x1, 0.0, top),
        ]);
    }
    add([
        v(x0, 0.0, low),
        v(x1, 0.0, low),
        v(x1, 0.0, top),
        v(x0, 0.0, top),
    ]);
    add([
        v(x0, 20.0, low),
        v(x1, 20.0, low),
        v(x1, 20.0, top),
        v(x0, 20.0, top),
    ]);
    CellSurface::new(cell_id, &vertices, &polygons)
}

/// Three rooms in a row 30 m down: a low room 3 m high, a room 5 m high, and
/// a room 10 m high authored as a cell on top of a cell, the upper one with
/// walls and a ceiling but no floor.
fn dungeon_with_tall_rooms() -> PhysicsEngine {
    let mut physics = PhysicsEngine::new();
    physics.add_landblock(
        LANDBLOCK,
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
        vec![
            cell(LOW_ROOM, 0.0, 20.0, Some(FLOOR), FLOOR, -27.0, true),
            cell(TALL_ROOM, 20.0, 40.0, Some(FLOOR), FLOOR, -25.0, true),
            cell(STACKED_LOWER, 40.0, 60.0, Some(FLOOR), FLOOR, -25.0, false),
            cell(STACKED_UPPER, 40.0, 60.0, None, -25.0, -20.0, true),
        ],
        Vec::new(),
        0.0,
        0.0,
    );
    physics
}

#[allow(clippy::too_many_arguments)]
fn register_cylinder(
    physics: &mut PhysicsEngine,
    entity_id: u32,
    at: Vec3,
    radius: f32,
    height: f32,
    cell_id: u32,
    is_static: bool,
) {
    let (shadows, world) = physics.shadows_and_world();
    shadows
        .register(
            world,
            entity_id,
            &SingleShape {
                gfx_obj_id: 0,
                radius,
                collision_type: ShadowCollisionType::Cylinder,
                cyl_height: height,
                scale: 1.0,
            },
            RegistrationSite {
                world_pos: at,
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: LANDBLOCK,
                seed_cell_id: cell_id,
            },
            0,
            Default::default(),
            is_static,
            None,
        )
        .expect("the cylinder registers");
}

/// The 5 m room with a platform 2 m high and 6 m across in its middle, which
/// the landblock holds.
fn dungeon_with_a_platform() -> PhysicsEngine {
    let mut physics = dungeon_with_tall_rooms();
    register_cylinder(
        &mut physics,
        0x7A9B_4001,
        v(30.0, 10.0, FLOOR),
        PLATFORM_RADIUS,
        2.0,
        TALL_ROOM,
        true,
    );
    physics
}

/// The character standing in the low room.
fn character(physics: &mut PhysicsEngine) -> PlayerMovementController {
    let mut controller = PlayerMovementController::new();
    controller.prepare_position_for_commit(
        &mut MovementEnv::new(physics, &NoTargets),
        START,
        LOW_ROOM,
        START,
    );
    controller
}

/// What the client's goal source reads: the physics world, the objects the
/// client knows and the character.
struct Client {
    physics: PhysicsEngine,
    controller: PlayerMovementController,
    residency: Residency,
    world: World,
    bodies: HashMap<u32, Vec3>,
}

impl Client {
    fn new(mut physics: PhysicsEngine, world: World) -> Self {
        let controller = character(&mut physics);
        Self {
            physics,
            controller,
            residency: Residency::new(),
            world,
            bodies: HashMap::new(),
        }
    }

    fn goals(&self) -> Goals<'_> {
        Goals {
            engine: &self.physics,
            residency: &self.residency,
            world: &self.world,
            controller: Some(&self.controller),
            bodies: &self.bodies,
        }
    }

    /// A preview through the client's goal source.
    fn preview(&self, cell_id: u32, local: Vec3) -> ac_nav::walk::NavPlan {
        let goals = self.goals();
        let mut body = Standing;
        let sealed = |_: u32| true;
        let world = WalkWorld {
            physics: &self.physics,
            body: &mut body,
            goals: &goals,
            doors: None,
            paused_by: None,
            is_sealed_dungeon: Some(&sealed),
        };
        WalkController::new(PlanMode::Inline, None)
            .preview_path(&world, PreviewTarget::Place { cell_id, local }, 2.5)
            .take()
    }
}

/// A character standing still in the low room.
struct Standing;

impl WalkBody for Standing {
    fn try_sample(&self) -> Option<WalkBodySample> {
        Some(WalkBodySample {
            position: START,
            heading_degrees: 0.0,
            body: NavBody::player(0.6, 1.5),
            moves: ScriptedMoveSnapshot::default(),
            in_portal_space: false,
            cell_id: LOW_ROOM,
            leaps: None,
            airborne: false,
            turning: None,
            walk_stop_meters: 0.0,
            run_stop_meters: 0.0,
            player_movement_input_frames: 0,
        })
    }

    fn begin_move(&mut self, _request: MoveRequest) -> bool {
        false
    }

    fn stop_move(&mut self, _channel: MoveChannel) -> bool {
        false
    }

    fn begin_jump(&mut self, _power: f32, _leave_at: Option<MovePace>) -> bool {
        false
    }
}

/// A preview through the client's goal source to the middle of a tall room
/// routes to that room's floor.
#[test]
fn the_clients_goal_source_stands_a_place_at_the_middle_of_a_tall_room_on_its_floor() {
    // TheClientsGoalSourceStandsAPlaceAtTheMiddleOfATallRoomOnItsFloor.
    let client = Client::new(dungeon_with_tall_rooms(), World::new());

    let placed = client.goals().try_locate_place(TALL_ROOM, TALL_MIDDLE);
    let plan = client.preview(TALL_ROOM, TALL_MIDDLE);

    assert_eq!(placed, Some(v(30.0, 10.0, FLOOR)));
    assert_eq!(plan.status, PlanStatus::Routed, "{}", plan.reason);
    let end = plan.path.last().unwrap().global.z;
    assert!((FLOOR - 0.1..=FLOOR + 0.1).contains(&end), "ends at {end}");
}

/// A place on top of an object in a cell, such as a route waypoint recorded
/// where the character stood on a platform, stays on the platform; a place
/// a little under the top is lifted onto it.
#[test]
fn a_place_on_a_platform_in_a_cell_stays_on_the_platform() {
    // APlaceOnAPlatformInACellStaysOnThePlatform.
    let client = Client::new(dungeon_with_a_platform(), World::new());
    let world = &client.physics;
    let on_top = v(30.0, 10.0, PLATFORM_TOP);
    let just_over = on_top.with_z(PLATFORM_TOP + 0.4);

    assert!(
        !geometry::capture_dungeon(world, LANDBLOCK, 0.0, 0.0, 64.0, None)
            .unwrap()
            .cylinders
            .is_empty()
    );
    assert_eq!(on_the_cell_floor(world, TALL_ROOM, on_top, None), on_top);
    assert_eq!(
        on_the_cell_floor(world, TALL_ROOM, on_top.with_z(PLATFORM_TOP - 0.2), None),
        on_top
    );
    assert_eq!(
        on_the_cell_floor(world, TALL_ROOM, just_over, None),
        just_over
    );
    assert_eq!(
        client.goals().try_locate_place(TALL_ROOM, on_top),
        Some(on_top)
    );
    assert_eq!(
        on_the_cell_floor(world, TALL_ROOM, v(36.0, 10.0, PLATFORM_TOP), None),
        v(36.0, 10.0, FLOOR)
    );
}

/// The middle of a 3 m room is 1.5 m over its floor and 0.6 m over the top
/// of a table standing there. A table top is no floor a waypoint was
/// recorded on, so the place drops to the room's floor and a route reaches
/// it.
#[test]
fn the_middle_of_a_low_room_over_a_table_drops_to_the_floor_and_routes() {
    // TheMiddleOfALowRoomOverATableDropsToTheFloorAndRoutes.
    let mut physics = dungeon_with_tall_rooms();
    register_cylinder(
        &mut physics,
        0x7A9B_4002,
        v(15.0, 12.0, FLOOR),
        0.6,
        0.9,
        LOW_ROOM,
        true,
    );
    let client = Client::new(physics, World::new());
    let middle = v(15.0, 12.0, -28.5);

    let placed = client.goals().try_locate_place(LOW_ROOM, middle);
    let plan = client.preview(LOW_ROOM, middle);

    assert_eq!(placed, Some(middle.with_z(FLOOR)));
    assert_eq!(plan.status, PlanStatus::Routed, "{}", plan.reason);
    let end = plan.path.last().unwrap().global.z;
    assert!((FLOOR - 0.1..=FLOOR + 0.1).contains(&end), "ends at {end}");
}

/// An object the server placed is no part of the landblock, so a place is
/// kept on its top only because the walk names it as something a body
/// stands on, as it does when it builds its grid. The client's goal source
/// passes that on.
#[test]
fn a_place_on_a_platform_the_server_placed_stays_on_it() {
    // APlaceOnAPlatformTheServerPlacedStaysOnIt.
    const PLATFORM: u32 = 0x7000_0001;
    let mut world = World::new();
    world.apply(&crate::world::tests::named(PLATFORM, 1, "Platform"));
    let local = world.object(PLATFORM).unwrap().key().local_id;
    let mut physics = dungeon_with_tall_rooms();
    register_cylinder(
        &mut physics,
        local,
        v(30.0, 10.0, FLOOR),
        PLATFORM_RADIUS,
        2.0,
        TALL_ROOM,
        false,
    );
    let client = Client::new(physics, world);
    let on_top = v(30.0, 10.0, PLATFORM_TOP);

    assert!(client.goals().stands_still(local));
    assert_eq!(
        client.goals().try_locate_place(TALL_ROOM, on_top),
        Some(on_top)
    );
    assert_eq!(
        on_the_cell_floor(&client.physics, TALL_ROOM, on_top, None),
        on_top.with_z(FLOOR)
    );
}
