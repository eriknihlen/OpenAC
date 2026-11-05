//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/SlidingNormalLifecycleTests.cs`,
//! `FindEnvCollisionsMultiCellTests.cs` and `IndoorContactPlaneRetentionTests.cs`.

use glam::{Quat, Vec3};

use super::bsp_step_up::{SPHERE_RADIUS, make_grounded_transition, tall_wall};
use super::support::*;
use crate::TransientState;
use crate::body::PhysicsBody;
use crate::bsp::{CellBsp, CellBspNode};
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::{Plane, Sphere};
use crate::globals::EPSILON;
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::state::{ObjectInfoState, TransitionState};
use crate::transition::Transition;
use crate::world::{CellPhysics, CollisionWorld, Placement};

fn leaf_cell_bsp() -> CellBsp {
    CellBsp::new(vec![CellBspNode::Leaf]).unwrap()
}

fn engine_with_cells(cells: Vec<CellPhysics>) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        0xA9B4_FFFF,
        terrain_from_heights(&[0u8; 81]),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    for cell in cells {
        engine.world_mut().unwrap().insert_cell(cell);
    }
    engine
}

// ── SlidingNormalLifecycleTests ────────────────────────────────────────

#[test]
fn contact_foot_full_hit_step_up_unavailable_real_slide_no_sliding_normal_write() {
    let bsp = tall_wall();
    let from = Vec3::new(0.10, 0.0, SPHERE_RADIUS);
    let to = Vec3::new(0.35, 0.0, SPHERE_RADIUS);
    let mut t = make_grounded_transition(from, to, 0.30);
    let result = bsp_find(
        &bsp,
        &mut t,
        Sphere::new(to, SPHERE_RADIUS),
        None,
        from,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert!(!t.collision_info.sliding_normal_valid);
    assert!(t.collision_info.collision_normal_valid);
    assert_eq!(result, TransitionState::Collided);
}

#[test]
fn contact_head_full_hit_real_slide_no_sliding_normal_write() {
    let floor_poly = floor(
        1,
        &[
            Vec3::new(-2.0, -1.0, 0.0),
            Vec3::new(2.0, -1.0, 0.0),
            Vec3::new(2.0, 1.0, 0.0),
            Vec3::new(-2.0, 1.0, 0.0),
        ],
    );
    let wall = ResolvedPolygon::new(
        2,
        vec![
            Vec3::new(0.5, -1.0, 0.6),
            Vec3::new(0.5, -1.0, 5.0),
            Vec3::new(0.5, 1.0, 5.0),
            Vec3::new(0.5, 1.0, 0.6),
        ],
        Plane::new(Vec3::new(-1.0, 0.0, 0.0), 0.5),
    );
    let bsp = single_leaf(Vec3::new(0.0, 0.0, 2.5), 10.0, vec![floor_poly, wall]);
    let from = Vec3::new(0.10, 0.0, SPHERE_RADIUS);
    let to = Vec3::new(0.35, 0.0, SPHERE_RADIUS);
    let mut t = make_grounded_transition(from, to, 0.30);
    let result = bsp_find(
        &bsp,
        &mut t,
        Sphere::new(to, SPHERE_RADIUS),
        Some(Sphere::new(Vec3::new(to.x, to.y, 0.8), SPHERE_RADIUS)),
        from,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert!(!t.collision_info.sliding_normal_valid);
    assert!(t.collision_info.collision_normal_valid);
    assert_eq!(result, TransitionState::Collided);
}

#[test]
fn slide_sphere_opposing_normals_returns_collided_with_reversed_displacement_normal() {
    let mut t = Transition::new();
    t.sphere_path.init_path_simple(
        Vec3::new(0.0, 0.0, 0.2),
        Vec3::new(0.3, 0.0, 0.2),
        0xA9B4_0001,
        SPHERE_RADIUS,
    );
    t.collision_info
        .set_contact_plane(Plane::new(Vec3::Z, 0.0), 0xA9B4_0001, false);
    let curr_pos = t.sphere_path.global_sphere[0].center - Vec3::new(0.4, 0.0, 0.0);
    let result = t.slide_sphere(Vec3::new(0.0, 0.0, -1.0), curr_pos, 0);
    assert_eq!(result, TransitionState::Collided);
    assert!(t.collision_info.collision_normal_valid);
    assert!(t.collision_info.collision_normal.x < -0.99);
}

const WALL_CELL_ID: u32 = 0xA9B4_0157;

fn wall_engine() -> PhysicsEngine {
    let mut cell = cell_with_bsp(WALL_CELL_ID, tall_wall(), Placement::default());
    cell.containment_bsp = leaf_cell_bsp();
    engine_with_cells(vec![cell])
}

fn grounded_body() -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.contact_plane_valid = true;
    body.contact_plane = Plane::new(Vec3::Z, 0.0);
    body.transient_state
        .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
    body
}

fn resolve_forward(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    from: Vec3,
    to: Vec3,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: WALL_CELL_ID,
            sphere_radius: SPHERE_RADIUS,
            sphere_height: 0.0,
            step_up_height: 0.04,
            step_down_height: 0.04,
            is_on_ground: true,
            ..ResolveRequest::default()
        },
        Some(body),
    )
}

#[test]
fn wall_lifecycle_persist_on_block_absorb_exact_anti_parallel_clear_on_escape() {
    let engine = wall_engine();
    let mut body = grounded_body();

    let r1 = resolve_forward(
        &engine,
        &mut body,
        Vec3::new(0.10, 0.0, 0.0),
        Vec3::new(0.35, 0.0, 0.0),
    );
    assert!(body.transient_state.contains(TransientState::SLIDING));
    assert!(body.sliding_normal.x < -0.9, "{}", body.sliding_normal);
    assert!(r1.position.x + SPHERE_RADIUS <= 0.5 + EPSILON * 20.0);

    let r2 = resolve_forward(
        &engine,
        &mut body,
        r1.position,
        r1.position + Vec3::new(0.15, 0.0, 0.0),
    );
    assert!(!r2.ok);
    assert!(body.transient_state.contains(TransientState::SLIDING));

    let r3 = resolve_forward(
        &engine,
        &mut body,
        r2.position,
        r2.position + Vec3::new(0.10, 0.15, 0.0),
    );
    assert!(r3.ok);
    assert!(r3.position.y > r2.position.y + 0.05, "y {}", r3.position.y);
    assert!(!body.transient_state.contains(TransientState::SLIDING));
    assert_eq!(body.sliding_normal, Vec3::ZERO);
}

// ── FindEnvCollisionsMultiCellTests ────────────────────────────────────

#[test]
fn indoor_sphere_overlapping_adjacent_cell_with_wall_halts_transition() {
    const VESTIBULE: u32 = 0xA9B4_0157;
    const INTERIOR: u32 = 0xA9B4_0164;
    let mut interior = cell_with_bsp(
        INTERIOR,
        tall_wall(),
        Placement::at(Vec3::new(0.3, 0.0, 0.0)),
    );
    interior.containment_bsp = leaf_cell_bsp();

    let portal_poly = ResolvedPolygon::new(
        10,
        vec![
            Vec3::new(0.5, -2.5, 0.0),
            Vec3::new(0.5, 2.5, 0.0),
            Vec3::new(0.5, 2.5, 5.0),
            Vec3::new(0.5, -2.5, 5.0),
        ],
        Plane::new(Vec3::X, -0.5),
    );
    let mut vestibule = cell_with_bsp(
        VESTIBULE,
        single_leaf(Vec3::ZERO, 10.0, Vec::new()),
        Placement::default(),
    );
    vestibule.containment_bsp = leaf_cell_bsp();
    vestibule.portal_polygons.insert(10, portal_poly);
    vestibule.portals = vec![PortalInfo::new((INTERIOR & 0xFFFF) as u16, 10, 0)];

    let engine = engine_with_cells(vec![vestibule, interior]);
    let mut t = Transition::new();
    t.sphere_path.init_path_simple(
        Vec3::new(0.1, 0.0, 0.0),
        Vec3::new(0.7, 0.0, 0.0),
        VESTIBULE,
        SPHERE_RADIUS,
    );
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    t.object_info.step_up_height = 0.04;
    t.object_info.step_down_height = 0.04;
    t.object_info.step_down = true;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    t.collision_info.last_known_contact_plane_valid = true;

    let _ = t.find_transitional_position(&engine);
    assert!(
        t.sphere_path.cur_pos.x <= 0.6 + EPSILON * 20.0,
        "x {}",
        t.sphere_path.cur_pos.x
    );
}

// ── IndoorContactPlaneRetentionTests ───────────────────────────────────

#[test]
fn indoor_flat_floor_walking_60_frames_produces_at_most_5_extra_cp_writes() {
    const INDOOR_CELL: u32 = 0xA9B4_0166;
    const RADIUS: f32 = 0.48;
    let floor_z = 0.0;
    let world_pos_z = floor_z - 0.05;
    let sphere_center_z = world_pos_z + RADIUS;
    let floor_plane = Plane::new(Vec3::Z, -floor_z);
    let world_pos = Vec3::new(0.0, 0.0, world_pos_z);

    let floor_poly = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-10.0, -10.0, floor_z),
            Vec3::new(10.0, -10.0, floor_z),
            Vec3::new(10.0, 10.0, floor_z),
            Vec3::new(-10.0, 10.0, floor_z),
        ],
        Plane::new(Vec3::Z, -floor_z),
    );
    let cell = cell_with_bsp(
        INDOOR_CELL,
        single_leaf(Vec3::new(0.0, 0.0, sphere_center_z), 10.0, vec![floor_poly]),
        Placement::default(),
    );
    let engine = engine_with_cells(vec![cell]);

    let mut t = Transition::new();
    t.sphere_path.init_path_simple(
        world_pos,
        world_pos + Vec3::new(0.001, 0.0, 0.0),
        INDOOR_CELL,
        RADIUS,
    );
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    t.object_info.step_up_height = 0.04;
    t.object_info.step_down_height = 0.04;
    t.object_info.step_down = true;
    t.collision_info.last_known_contact_plane = floor_plane;
    t.collision_info.last_known_contact_plane_valid = true;
    t.collision_info
        .set_contact_plane(floor_plane, INDOOR_CELL, false);

    let seeded = t.collision_info.contact_plane_write_count;
    assert_eq!(seeded, 1);
    for frame in 0..60 {
        let new_pos = Vec3::new(frame as f32 * 0.001, 0.0, world_pos_z);
        t.sphere_path.set_check_pos(new_pos, INDOOR_CELL);
        let _ = t.find_env_collisions(&engine, INDOOR_CELL);
    }
    let additional = t.collision_info.contact_plane_write_count - seeded;
    assert!(additional <= 5, "additional writes {additional}");
}
