//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/WaterSemanticsTests.cs`
//! (the contact half; the depth table is a slice-1 terrain test),
//! `OpenSeaBarrierTests.cs`, `FramesStationaryFallTests.cs`,
//! `LandingBounceSeedingTests.cs`, `SteepSlopeGlideTests.cs`,
//! `PerfectClipTailContainmentTests.cs` and `AdjustOffsetConformanceTests.cs`.
//!
//! `PerfectClipTailContainmentTests` also counts OpenAC diagnostic probes
//! (the camera reaching the time-of-impact tail); those counters do not
//! exist here, so only the positions are checked.

use std::f32::consts::FRAC_1_SQRT_2;

use glam::{Quat, Vec2, Vec3};

use super::support::*;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::Plane;
use crate::globals::{EPSILON, FLOOR_Z};
use crate::obj_update::{
    apply_set_position_contact, commit_set_position_transition, handle_all_collisions,
};
use crate::polygon::ResolvedPolygon;
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::transition::Transition;
use crate::world::{CollisionWorld, Placement};
use crate::{PhysicsBody, PhysicsState, TransientState};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const WATER_VERTEX: u8 = 0x10 << 2;
const LAND_VERTEX: u8 = 0x01 << 2;

fn flags(bits: u32) -> ObjectInfoState {
    ObjectInfoState(bits)
}

fn player_slide() -> ObjectInfoState {
    flags(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

fn world_engine(landblock: u32, terrain: TerrainSurface) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(landblock, terrain, Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

fn zero_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None)
}

#[allow(clippy::too_many_arguments)]
fn resolve(
    engine: &PhysicsEngine,
    current: Vec3,
    target: Vec3,
    cell: u32,
    radius: f32,
    height: f32,
    up: f32,
    down: f32,
    grounded: bool,
    mover: ObjectInfoState,
    body: Option<&mut PhysicsBody>,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: current,
            target_pos: target,
            cell_id: cell,
            sphere_radius: radius,
            sphere_height: height,
            step_up_height: up,
            step_down_height: down,
            is_on_ground: grounded,
            mover_flags: mover,
            ..ResolveRequest::default()
        },
        body,
    )
}

fn floor_body(position: Vec3, cell: u32) -> PhysicsBody {
    let floor = Plane::new(Vec3::Z, 0.0);
    let mut body = PhysicsBody::new();
    body.set_position(position);
    body.orientation = Quat::IDENTITY;
    body.contact_plane_valid = true;
    body.contact_plane = floor;
    body.contact_plane_cell_id = cell;
    body.walkable_polygon_valid = true;
    body.walkable_plane = floor;
    body.walkable_vertices = Some(vec![
        Vec3::new(-100.0, -100.0, 0.0),
        Vec3::new(100.0, -100.0, 0.0),
        Vec3::new(100.0, 100.0, 0.0),
        Vec3::new(-100.0, 100.0, 0.0),
    ]);
    body.walkable_up = Vec3::Z;
    body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    body
}

// ── WaterSemanticsTests (contact) ──────────────────────────────────────

fn water_body(contact_plane_is_water: bool) -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::default();
    body.contact_plane_is_water = contact_plane_is_water;
    body
}

#[test]
fn apply_set_position_contact_water_plane_sets_water_contact() {
    let mut body = water_body(true);
    apply_set_position_contact(&mut body, true, true);
    assert!(body.is_water_contact());
}

#[test]
fn apply_set_position_contact_dry_plane_clears_water_contact() {
    let mut body = water_body(false);
    body.transient_state.insert(TransientState::WATER_CONTACT);
    apply_set_position_contact(&mut body, true, true);
    assert!(!body.is_water_contact());
}

#[test]
fn apply_set_position_contact_water_mirrors_independently_of_contact() {
    let mut body = water_body(true);
    apply_set_position_contact(&mut body, false, false);
    assert!(!body.in_contact());
    assert!(body.is_water_contact());
}

#[test]
fn commit_set_position_transition_water_plane_sets_water_contact() {
    let mut body = water_body(true);
    commit_set_position_transition(
        &mut body,
        true,
        true,
        false,
        Vec3::ZERO,
        false,
        false,
        None,
        None,
        None,
        None,
    );
    assert!(body.is_water_contact());
}

#[test]
fn commit_set_position_transition_dry_plane_clears_water_contact() {
    let mut body = water_body(false);
    body.transient_state.insert(TransientState::WATER_CONTACT);
    commit_set_position_transition(
        &mut body,
        true,
        true,
        false,
        Vec3::ZERO,
        true,
        true,
        None,
        None,
        None,
        None,
    );
    assert!(!body.is_water_contact());
}

fn water_engine(water: bool) -> PhysicsEngine {
    let mut types = [if water { WATER_VERTEX } else { 0 }; 81];
    // One dry corner: the cell under the body stays flooded (wading), but
    // the block is not the open sea, which no walker may enter.
    if water {
        types[80] = 0;
    }
    world_engine(
        LANDBLOCK,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, Some(&types)),
    )
}

fn settle_onto_terrain(engine: &PhysicsEngine, body: &mut PhysicsBody) {
    let mut pos = body.position();
    let mut cell = CELL;
    let mut grounded = true;
    for _ in 0..60 {
        let target = pos + Vec3::new(0.0, 0.001, -0.05);
        let r = resolve(
            engine,
            pos,
            target,
            cell,
            0.4,
            1.2,
            0.04,
            0.04,
            grounded,
            flags(ObjectInfoState::IS_PLAYER),
            Some(body),
        );
        body.set_position(r.position);
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
    }
}

#[test]
fn settle_onto_entirely_water_terrain_sets_body_water_contact() {
    let engine = water_engine(true);
    let mut body = floor_body(Vec3::new(12.0, 12.0, 1.0), CELL);
    settle_onto_terrain(&engine, &mut body);
    assert!(
        (body.position().z + 0.9).abs() < 0.05,
        "z {}",
        body.position().z
    );
    assert!(body.contact_plane_is_water);
    assert!(body.is_water_contact());
}

#[test]
fn settle_onto_dry_terrain_leaves_body_water_contact_clear() {
    let engine = water_engine(false);
    let mut body = floor_body(Vec3::new(12.0, 12.0, 1.0), CELL);
    body.transient_state.insert(TransientState::WATER_CONTACT);
    settle_onto_terrain(&engine, &mut body);
    assert!(body.position().z.abs() < 0.05, "z {}", body.position().z);
    assert!(!body.contact_plane_is_water);
    assert!(!body.is_water_contact());
}

// ── OpenSeaBarrierTests ────────────────────────────────────────────────

const SEA_START: Vec3 = Vec3::new(100.0, 100.0, 0.0);
const SEA_CELL: u32 = 0xA9B4_0025;

fn sea_engine(types: [u8; 81]) -> PhysicsEngine {
    world_engine(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0xA9, 0xB4, Some(&types)),
    )
}

fn sea_step(
    engine: &PhysicsEngine,
    mover: ObjectInfoState,
    body: Option<&mut PhysicsBody>,
) -> ResolveResult {
    resolve(
        engine,
        SEA_START,
        SEA_START + Vec3::X,
        SEA_CELL,
        0.48,
        1.835,
        0.4,
        0.4,
        true,
        mover,
        body,
    )
}

#[test]
fn player_cannot_walk_onto_the_open_sea() {
    let engine = sea_engine([WATER_VERTEX; 81]);
    let r = sea_step(&engine, player_slide(), None);
    assert_eq_prec(SEA_START.x, r.position.x, 3);
    assert_eq_prec(SEA_START.y, r.position.y, 3);
}

#[test]
fn camera_and_missiles_cross_the_open_sea() {
    let engine = sea_engine([WATER_VERTEX; 81]);
    // The camera sweeps as the chase camera does: one sphere, off the
    // ground, no stepping (a camera has no step down to keep it on the
    // ground; set up as a walking body, its step down would be moved back
    // along its step).
    let lift = Vec3::new(0.0, 0.0, 0.5);
    let viewer = resolve(
        &engine,
        SEA_START + lift,
        SEA_START + Vec3::X + lift,
        SEA_CELL,
        0.48,
        0.0,
        0.0,
        0.0,
        false,
        flags(ObjectInfoState::IS_VIEWER | ObjectInfoState::PATH_CLIPPED),
        None,
    );
    assert!(viewer.ok);
    assert!(
        viewer.position.x > SEA_START.x + 0.9,
        "camera held at {}",
        viewer.position.x
    );

    let mut missile = PhysicsBody::new();
    missile.state = PhysicsState::MISSILE | PhysicsState::GRAVITY;
    missile.snap_to_cell(SEA_CELL, SEA_START, SEA_START);
    let arrow = sea_step(
        &engine,
        flags(ObjectInfoState::PATH_CLIPPED),
        Some(&mut missile),
    );
    assert!(arrow.ok);
    assert!(
        arrow.position.x > SEA_START.x + 0.9,
        "missile held at {}",
        arrow.position.x
    );
}

#[test]
fn player_can_wade_where_the_block_is_only_partly_water() {
    let mut shore = [WATER_VERTEX; 81];
    shore[0] = LAND_VERTEX;
    let engine = sea_engine(shore);
    let r = sea_step(&engine, player_slide(), None);
    assert!(r.ok);
    assert!(
        r.position.x > SEA_START.x + 0.3,
        "wader held at {}",
        r.position.x
    );
}

// ── FramesStationaryFallTests ──────────────────────────────────────────

fn register_creature(engine: &mut PhysicsEngine, id: u32, center: Vec3) {
    register_shadow(
        engine,
        id,
        0,
        center,
        Quat::IDENTITY,
        0.48,
        LANDBLOCK,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        0,
        EntityCollisionFlags(EntityCollisionFlags::IS_CREATURE),
        0,
        false,
    );
}

#[test]
fn airborne_jump_blocked_overhead_fsf_climbs_to_3() {
    let mut engine = world_engine(LANDBLOCK, zero_terrain());
    register_creature(&mut engine, 0xC0B0, Vec3::new(12.0, 10.0, 4.5));
    let mut body = PhysicsBody::new();
    body.set_position(Vec3::new(12.0, 10.0, 1.0));
    body.orientation = Quat::IDENTITY;
    body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    body.transient_state = TransientState::default();
    let mut cell = CELL;
    let up = Vec3::new(0.0, 0.0, 0.6);

    let mut first_fsf = -1;
    let mut max_fsf = 0;
    for i in 0..14 {
        let pos = body.position();
        let r = resolve(
            &engine,
            pos,
            pos + up,
            cell,
            0.48,
            1.835,
            0.6,
            0.04,
            false,
            player_slide(),
            Some(&mut body),
        );
        body.set_position(r.position);
        cell = r.cell_id;
        if i == 0 {
            first_fsf = body.frames_stationary_fall;
        }
        max_fsf = max_fsf.max(body.frames_stationary_fall);
    }
    assert_eq!(first_fsf, 0);
    assert_eq!(max_fsf, 3);
    assert!(body.contact_plane_valid);
    assert!(body.contact_plane.normal.z > 0.99);
}

#[test]
fn grounded_wall_slide_does_not_accumulate_fsf() {
    let mut engine = world_engine(LANDBLOCK, zero_terrain());
    register_creature(&mut engine, 0xC0C0, Vec3::new(12.0, 11.5, 0.48));
    let mut body = floor_body(Vec3::new(12.0, 10.0, 0.0), CELL);
    body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    let mut cell = CELL;
    let mut max_fsf = 0;
    for _ in 0..40 {
        let pos = body.position();
        let r = resolve(
            &engine,
            pos,
            pos + Vec3::new(0.0, 0.08, 0.0),
            cell,
            0.48,
            1.835,
            0.6,
            0.04,
            true,
            player_slide(),
            Some(&mut body),
        );
        body.set_position(r.position);
        cell = r.cell_id;
        max_fsf = max_fsf.max(body.frames_stationary_fall);
    }
    assert_eq!(max_fsf, 0);
}

// ── LandingBounceSeedingTests ──────────────────────────────────────────

fn landing_body(velocity: Vec3) -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.set_position(Vec3::new(96.0, 96.0, 0.48));
    body.orientation = Quat::IDENTITY;
    body.state = PhysicsState::GRAVITY;
    body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    body.velocity = velocity;
    body.contact_plane_valid = true;
    body.contact_plane = Plane::new(Vec3::Z, 0.0);
    body.ground_normal = Vec3::Z;
    body
}

fn zero_move(engine: &PhysicsEngine, body: &mut PhysicsBody) -> ResolveResult {
    let pos = body.position();
    let grounded = body.on_walkable();
    resolve(
        engine,
        pos,
        pos,
        0x0001,
        0.48,
        1.835,
        0.55,
        0.55,
        grounded,
        ObjectInfoState::default(),
        Some(body),
    )
}

fn landing_engine() -> PhysicsEngine {
    world_engine(0x0001_0000, zero_terrain())
}

#[test]
fn check_contact_at_rest_grounded_body_keeps_contact_on_zero_move() {
    let engine = landing_engine();
    let mut body = landing_body(Vec3::ZERO);
    let r = zero_move(&engine, &mut body);
    assert!(r.is_on_ground);
    assert!(r.in_contact);
}

#[test]
fn check_contact_ascending_jumper_seeds_no_contact() {
    let engine = landing_engine();
    let mut body = landing_body(Vec3::new(0.0, 0.0, 5.4));
    let r = zero_move(&engine, &mut body);
    assert!(!r.is_on_ground);
    assert!(!r.in_contact);
}

#[test]
fn check_contact_without_stored_plane_seeds_nothing() {
    let engine = landing_engine();
    let mut body = landing_body(Vec3::ZERO);
    body.contact_plane_valid = false;
    let r = zero_move(&engine, &mut body);
    assert!(!r.is_on_ground);
}

#[test]
fn reflect_downhill_slope_landing_five_percent_normal_reversal() {
    let n = Vec3::new(0.0, 0.5, 0.866_025_4).normalize();
    let v = Vec3::new(0.0, 4.0, -6.0);
    let mut body = PhysicsBody::new();
    body.velocity = v;
    body.elasticity = 0.05;
    body.frames_stationary_fall = 0;
    handle_all_collisions(&mut body, true, n, false, false, true);
    let before = v.dot(n);
    let after = body.velocity.dot(n);
    assert!(before < 0.0);
    assert!(
        (after - (-before * 0.05)).abs() < 1e-5,
        "{before} -> {after}"
    );
    let tangent_before = v - n * before;
    let tangent_after = body.velocity - n * after;
    assert!((tangent_after - tangent_before).length() < 1e-5);
}

#[test]
fn reflect_sledding_overrides_grounded_suppression() {
    let mut body = PhysicsBody::new();
    body.velocity = Vec3::new(3.0, 0.0, -2.0);
    body.elasticity = 0.05;
    body.state = PhysicsState::SLEDDING;
    body.frames_stationary_fall = 0;
    handle_all_collisions(&mut body, true, Vec3::Z, true, true, true);
    assert!((body.velocity.z - 0.1).abs() < 1e-5, "{}", body.velocity.z);
    assert_eq_prec(3.0, body.velocity.x, 5);
}

// ── SteepSlopeGlideTests ───────────────────────────────────────────────

const DXY_PER_TICK: f32 = 0.23;
const GLIDE_TICKS: usize = 30;
const GLIDE_START: Vec2 = Vec2::new(80.4, 79.8);
const LATERAL: Vec2 = Vec2::new(FRAC_1_SQRT_2, FRAC_1_SQRT_2);
const INTO_FACE: Vec2 = Vec2::new(-FRAC_1_SQRT_2, FRAC_1_SQRT_2);
const GLIDE_MOVER: u32 = 0x5000_000A;

fn glide_engine(heights: &[u8; 81]) -> PhysicsEngine {
    // OpenAC builds these with no collision world.
    engine_with_terrain(terrain_from_heights(heights))
}

fn glide_body() -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.state = PhysicsState::GRAVITY;
    body.transient_state =
        TransientState::ACTIVE | TransientState::CONTACT | TransientState::ON_WALKABLE;
    body
}

fn glide_step(
    engine: &PhysicsEngine,
    pos: Vec3,
    d: Vec2,
    cell: u32,
    body: &mut PhysicsBody,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: pos,
            target_pos: Vec3::new(pos.x + d.x, pos.y + d.y, pos.z),
            cell_id: cell,
            sphere_radius: 0.47,
            sphere_height: 1.20,
            step_up_height: 0.60,
            step_down_height: 1.50,
            is_on_ground: true,
            mover_flags: player_slide(),
            moving_entity_id: GLIDE_MOVER,
            ..ResolveRequest::default()
        },
        Some(body),
    )
}

fn lateral_advance(p: Vec3) -> f32 {
    (p.x - GLIDE_START.x) * LATERAL.x + (p.y - GLIDE_START.y) * LATERAL.y
}

fn run_approach(angle_deg: f32) -> (Vec3, usize) {
    let mut heights = [0u8; 81];
    heights[3 * 9 + 4] = 32;
    let engine = glide_engine(&heights);
    let mut body = glide_body();
    let rad = angle_deg * std::f32::consts::PI / 180.0;
    let dir = rad.cos() * INTO_FACE + rad.sin() * LATERAL;
    let d = DXY_PER_TICK * dir;
    let mut pos = Vec3::new(GLIDE_START.x, GLIDE_START.y, 0.0);
    let mut cell =
        TerrainSurface::compute_outdoor_cell_id_in(0xA9B4_FFFF, GLIDE_START.x, GLIDE_START.y);
    let mut stuck = 0;
    for _ in 0..GLIDE_TICKS {
        let r = glide_step(&engine, pos, d, cell, &mut body);
        if r.position.x == pos.x && r.position.y == pos.y {
            stuck += 1;
        }
        pos = r.position;
        cell = r.cell_id;
    }
    (pos, stuck)
}

#[test]
fn angled_45_approach_glides_along_the_diagonal() {
    let (pos, stuck) = run_approach(45.0);
    assert!(
        lateral_advance(pos) > 1.0,
        "lateral {} at {pos}",
        lateral_advance(pos)
    );
    assert!(pos.z < 1.0, "climbed to {}", pos.z);
    assert!(pos.z > -0.05, "sank to {}", pos.z);
    assert!(
        (1..=GLIDE_TICKS / 2 + 2).contains(&stuck),
        "stuck ticks {stuck}"
    );
}

#[test]
fn steeper_approach_angle_yields_more_lateral_advance() {
    let (p30, _) = run_approach(30.0);
    let (p60, _) = run_approach(60.0);
    assert!(lateral_advance(p60) > lateral_advance(p30));
}

#[test]
fn perpendicular_approach_stops() {
    let (pos, _) = run_approach(0.0);
    assert!(lateral_advance(pos).abs() < 0.15);
    let travel = (pos.truncate() - GLIDE_START).length();
    assert!(travel < 1.2, "travelled {travel}");
    assert!(pos.z < 1.0);
    assert!(pos.z > -0.05);
}

#[test]
fn cell_boundary_face_angled_45_also_glides() {
    let mut heights = [0u8; 81];
    for x in 5..9 {
        for y in 0..9 {
            heights[x * 9 + y] = 32;
        }
    }
    let engine = glide_engine(&heights);
    let mut body = glide_body();
    let mut pos = Vec3::new(91.0, 36.0, 0.0);
    let mut cell = TerrainSurface::compute_outdoor_cell_id_in(0xA9B4_FFFF, 91.0, 36.0);
    let d = DXY_PER_TICK * FRAC_1_SQRT_2;
    for _ in 0..40 {
        let r = glide_step(&engine, pos, Vec2::splat(d), cell, &mut body);
        pos = r.position;
        cell = r.cell_id;
    }
    assert!(pos.y - 36.0 > 0.5, "lateral {}", pos.y - 36.0);
    assert!(pos.z < 1.0, "climbed to {}", pos.z);
}

// ── PerfectClipTailContainmentTests ────────────────────────────────────

const VIEWER_RADIUS: f32 = 0.3;

fn camera_flags() -> ObjectInfoState {
    flags(
        ObjectInfoState::IS_VIEWER
            | ObjectInfoState::PATH_CLIPPED
            | ObjectInfoState::FREE_ROTATE
            | ObjectInfoState::PERFECT_CLIP,
    )
}

fn sweep_viewer(engine: &PhysicsEngine) -> ResolveResult {
    let lift = Vec3::new(0.0, 0.0, VIEWER_RADIUS);
    resolve(
        engine,
        Vec3::new(12.0, 10.0, 1.0) - lift,
        Vec3::new(12.0, 20.0, 1.0) - lift,
        CELL,
        VIEWER_RADIUS,
        0.0,
        0.0,
        0.0,
        false,
        camera_flags(),
        None,
    )
}

// The camera's eye swept down into flat ground (z = 0) stops on its own
// line where its sphere first touches the ground: it is moved back along
// the step, never lifted straight up over the ground. Golden value worked
// by hand: the centre falls 0.2 m per meter of y from z = 1 at y = 10, so
// the 0.3 m sphere touches at y = 13.5, z = 0.3.
#[test]
fn a_camera_swept_into_the_ground_stops_on_its_line_where_it_touches() {
    let engine = world_engine(LANDBLOCK, zero_terrain());
    let lift = Vec3::new(0.0, 0.0, VIEWER_RADIUS);
    let r = resolve(
        &engine,
        Vec3::new(12.0, 10.0, 1.0) - lift,
        Vec3::new(12.0, 20.0, -1.0) - lift,
        CELL,
        VIEWER_RADIUS,
        0.0,
        0.0,
        0.0,
        false,
        camera_flags(),
        None,
    );
    let eye = r.position + lift;
    assert!(r.ok, "{r:?}");
    assert!((eye.z - VIEWER_RADIUS).abs() < 1e-3, "eye {eye}");
    assert!((eye.y - 13.5).abs() < 1e-3, "eye {eye} is off its line");
    assert!((eye.x - 12.0).abs() < 1e-4, "eye {eye}");
}

fn prop_engine(
    ty: ShadowCollisionType,
    center: Vec3,
    height: f32,
    creature: bool,
) -> PhysicsEngine {
    let mut engine = world_engine(LANDBLOCK, zero_terrain());
    let f = if creature {
        EntityCollisionFlags::IS_CREATURE
    } else {
        0
    };
    register_shadow(
        &mut engine,
        0x5001,
        0,
        center,
        Quat::IDENTITY,
        1.0,
        LANDBLOCK,
        ty,
        height,
        1.0,
        0,
        EntityCollisionFlags(f),
        0,
        true,
    );
    engine
}

#[test]
fn camera_sweep_stops_at_non_creature_sphere_prop() {
    let engine = prop_engine(
        ShadowCollisionType::Sphere,
        Vec3::new(12.0, 14.0, 1.0),
        0.0,
        false,
    );
    let r = sweep_viewer(&engine);
    assert!(r.position.y < 14.0, "passed through to {}", r.position.y);
    assert!(r.position.y > 11.0, "stopped early at {}", r.position.y);
}

#[test]
fn camera_sweep_passes_creature_flagged_sphere() {
    let engine = prop_engine(
        ShadowCollisionType::Sphere,
        Vec3::new(12.0, 14.0, 1.0),
        0.0,
        true,
    );
    let r = sweep_viewer(&engine);
    assert!(r.position.y > 19.0, "held at {}", r.position.y);
}

#[test]
fn camera_sweep_stops_at_non_creature_cylinder_prop() {
    let engine = prop_engine(
        ShadowCollisionType::Cylinder,
        Vec3::new(12.0, 14.0, 0.0),
        2.0,
        false,
    );
    let r = sweep_viewer(&engine);
    assert!(r.position.y < 14.0, "passed through to {}", r.position.y);
    assert!(r.position.y > 11.0, "stopped early at {}", r.position.y);
}

// OpenAC `ViewerSphereContactTests`: the viewer swept into a sphere prop (a
// lifestone has one sphere, 1.504 m, centred 1.504 m up, and no physics BSP)
// stops where the two spheres touch, on its own path, however far beyond the
// contact the boom reaches.
#[test]
fn viewer_swept_into_a_sphere_prop_stops_touching_it() {
    const PROP_RADIUS: f32 = 1.504;
    let prop_center = Vec3::new(12.0, 12.0, PROP_RADIUS);
    let mut engine = world_engine(LANDBLOCK, zero_terrain());
    register_shadow(
        &mut engine,
        0x5001,
        0,
        prop_center,
        Quat::IDENTITY,
        PROP_RADIUS,
        LANDBLOCK,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        0x410,
        EntityCollisionFlags(EntityCollisionFlags::HAS_WEENIE),
        0,
        false,
    );
    let pivot = Vec3::new(12.0, 15.7, 1.5);
    let boom = Vec3::new(0.0, -2.5, 0.75).normalize();
    let lift = Vec3::new(0.0, 0.0, VIEWER_RADIUS);
    for reach in [2.1_f32, 2.2, 2.6, 3.5] {
        let eye = pivot + boom * reach;
        let r = resolve(
            &engine,
            pivot - lift,
            eye - lift,
            CELL,
            VIEWER_RADIUS,
            0.0,
            0.0,
            0.0,
            false,
            camera_flags(),
            None,
        );
        let stop = r.position + lift;
        let gap = stop.distance(prop_center) - (PROP_RADIUS + VIEWER_RADIUS);
        let along = (stop - pivot).dot(boom);
        let off = stop.distance(pivot + boom * along);
        assert!(r.ok, "reach {reach}: no valid position");
        assert!(
            off < 0.001,
            "reach {reach}: {off} m off the boom, stop {stop}"
        );
        assert!(
            (-0.001..0.01).contains(&gap),
            "reach {reach}: stopped {along} m along the boom, {gap} m from the prop"
        );
    }
}

#[test]
fn camera_sweep_passes_creature_flagged_cylinder() {
    let engine = prop_engine(
        ShadowCollisionType::Cylinder,
        Vec3::new(12.0, 14.0, 0.0),
        2.0,
        true,
    );
    let r = sweep_viewer(&engine);
    assert!(r.position.y > 19.0, "held at {}", r.position.y);
}

// ── AdjustOffsetConformanceTests ───────────────────────────────────────

const SLOPE_NORMAL: Vec3 = Vec3::new(0.5, 0.0, 0.866_025_4);

fn transition_on(normal: Vec3, cell: u32) -> Transition {
    let mut t = Transition::new();
    t.collision_info
        .set_contact_plane(Plane::new(normal, 0.0), cell, false);
    t
}

#[test]
fn adjust_offset_away_from_plane_snaps_preserving_xy() {
    let mut t = transition_on(SLOPE_NORMAL, 0);
    let r = t.adjust_offset(Vec3::X);
    assert_near(1.0, r.x, 1e-5);
    assert_near(0.0, r.y, 1e-5);
    assert_near(-0.577_350_26, r.z, 1e-5);
}

#[test]
fn adjust_offset_away_from_near_vertical_plane_is_no_op() {
    let mut t = transition_on(Vec3::new(1.0, 0.0, 0.0001), 0);
    assert_eq!(t.adjust_offset(Vec3::X), Vec3::X);
}

#[test]
fn adjust_offset_into_plane_subtracts_full_normal_component() {
    let mut t = transition_on(SLOPE_NORMAL, 0);
    let r = t.adjust_offset(-Vec3::X);
    assert_near(-0.75, r.x, 1e-5);
    assert_near(0.0, r.y, 1e-5);
    assert_near(0.433_012_7, r.z, 1e-5);
}

#[test]
fn adjust_offset_safety_push_uses_bare_radius() {
    const RADIUS: f32 = 0.5;
    const DIST: f32 = 0.47;
    const { assert!(DIST > RADIUS * SLOPE_NORMAL.z - EPSILON) };
    const { assert!(DIST < RADIUS - EPSILON) };
    let mut t = transition_on(SLOPE_NORMAL, 0xA9B4_0001);
    t.sphere_path.global_sphere[0].center = Vec3::new(0.0, 0.0, DIST / SLOPE_NORMAL.z);
    t.sphere_path.global_sphere[0].radius = RADIUS;
    let check_before = t.sphere_path.check_pos.z;
    let sphere_before = t.sphere_path.global_sphere[0].center.z;
    t.adjust_offset(Vec3::ZERO);
    let expected = (RADIUS - DIST) / SLOPE_NORMAL.z;
    let push = t.sphere_path.check_pos.z - check_before;
    assert!(push > 0.0);
    assert_near(expected, push, 1e-5);
    assert_near(
        sphere_before + expected,
        t.sphere_path.global_sphere[0].center.z,
        1e-5,
    );
}

#[test]
fn adjust_offset_safety_push_does_not_fire_above_bare_radius() {
    let mut t = transition_on(SLOPE_NORMAL, 0xA9B4_0001);
    t.sphere_path.global_sphere[0].center = Vec3::new(0.0, 0.0, 0.6 / SLOPE_NORMAL.z);
    t.sphere_path.global_sphere[0].radius = 0.5;
    let before = t.sphere_path.check_pos.z;
    t.adjust_offset(Vec3::ZERO);
    assert_near(before, t.sphere_path.check_pos.z, 1e-5);
}

#[test]
fn uphill_no_contact_flap_across_ticks() {
    const RADIUS: f32 = 0.5;
    const SLOPE_CELL: u32 = 0xA9B4_0157;
    let theta = 42f32.to_radians();
    let (sin_t, cos_t) = theta.sin_cos();
    assert!(cos_t > FLOOR_Z);

    let z_at = |x: f32| x * sin_t / cos_t;
    let normal = Vec3::new(-sin_t, 0.0, cos_t);
    let slope = ResolvedPolygon::new(
        1,
        vec![
            Vec3::new(-10.0, -30.0, z_at(-10.0)),
            Vec3::new(60.0, -30.0, z_at(60.0)),
            Vec3::new(60.0, 30.0, z_at(60.0)),
            Vec3::new(-10.0, 30.0, z_at(-10.0)),
        ],
        Plane::new(normal, 0.0),
    );
    let mut cell = cell_with_bsp(
        SLOPE_CELL,
        single_leaf(Vec3::new(25.0, 0.0, z_at(25.0)), 100.0, vec![slope]),
        Placement::default(),
    );
    cell.containment_bsp = crate::bsp::CellBsp::new(vec![crate::bsp::CellBspNode::Leaf]).unwrap();
    let mut world = CollisionWorld::new();
    world.insert_cell(cell);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    let linear: Vec<f32> = (0..256).map(|i| i as f32).collect();
    let table: [f32; 256] = linear.try_into().unwrap();
    engine.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0u8; 81], &table, 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );

    let resting_z = |x: f32| (RADIUS * (1.0 - cos_t) + sin_t * x) / cos_t;
    let mut body = PhysicsBody::new();
    body.contact_plane_valid = true;
    body.contact_plane = Plane::new(normal, 0.0);
    body.contact_plane_cell_id = SLOPE_CELL;
    body.contact_plane_is_water = false;
    body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;

    let x0 = 1.0;
    let mut pos = Vec3::new(x0, 0.0, resting_z(x0));
    const DX: f32 = 0.12;
    const TICKS: usize = 15;
    for tick in 0..TICKS {
        let r = resolve(
            &engine,
            pos,
            pos + Vec3::new(DX, 0.0, 0.0),
            SLOPE_CELL,
            RADIUS,
            0.0,
            0.4,
            0.4,
            true,
            ObjectInfoState::default(),
            Some(&mut body),
        );
        assert!(r.ok, "tick {tick} stuck");
        assert!(r.in_contact, "tick {tick} lost contact");
        assert!(r.on_walkable, "tick {tick} lost walkable");
        pos = r.position;
    }
    let minimum = DX * cos_t * cos_t * (TICKS as f32 - 3.0);
    assert!(pos.x - x0 > minimum, "advanced {} < {minimum}", pos.x - x0);
}
