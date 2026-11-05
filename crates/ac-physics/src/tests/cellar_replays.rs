//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CellarUpReplayTests.cs`,
//! `CellarUpTrajectoryReplayTests.cs` and `CellarLipWedgeTests.cs`: replays
//! over the captured Holtburg cottage-cellar geometry and live `resolve`
//! records.
//!
//! Not ported: the cases that drive OpenAC's player movement controller
//! (they belong to the local-player slice), the resolve-capture writer
//! cases and the diagnostic dumps (OpenAC tooling).

use std::time::Instant;

use glam::{Quat, Vec3};

use super::fixtures::*;
use super::support::*;
use crate::bsp::{PhysicsBsp, PhysicsBspNode};
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::Plane;
use crate::globals::FLOOR_Z;
use crate::polygon::{ResolvedPolygon, find_crossed_edge};
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::world::{CellPhysics, CollisionWorld};
use crate::{PhysicsBody, TransientState};

const ASCENT: &str = "cellar-ascent";
const LIP: &str = "cellar-lip";
const CELLAR: u32 = 0xA9B4_0147;
const COTTAGE_A: u32 = 0xA9B4_0143;
const COTTAGE_B: u32 = 0xA9B4_0146;

fn player() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

fn deep_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None)
}

// ── CellarUpReplayTests ────────────────────────────────────────────────

const FAILING_SPHERE: Vec3 = Vec3::new(141.7164, 8.3937, 92.0093);
const FAILING_RADIUS: f32 = 0.48;

struct NearestWalkable<'a> {
    polygon: Option<&'a ResolvedPolygon>,
    overlaps: bool,
    inside_edges: bool,
}

/// The polygons of a one-leaf tree in the leaf's (file) order.
fn leaf_polygons(bsp: &PhysicsBsp) -> Vec<&ResolvedPolygon> {
    match bsp.nodes().first() {
        Some(PhysicsBspNode::Leaf { polygons, .. }) => {
            polygons.iter().filter_map(|id| bsp.polygon(*id)).collect()
        }
        _ => Vec::new(),
    }
}

fn nearest_walkable(cell: &CellPhysics, local: Vec3, radius: f32) -> NearestWalkable<'_> {
    let mut best = NearestWalkable {
        polygon: None,
        overlaps: false,
        inside_edges: false,
    };
    let mut nearest_abs = f32::MAX;
    for poly in leaf_polygons(&cell.physics_bsp) {
        if Vec3::Z.dot(poly.plane.normal) <= FLOOR_Z {
            continue;
        }
        let abs = (poly.plane.normal.dot(local) + poly.plane.d).abs();
        if abs >= nearest_abs {
            continue;
        }
        nearest_abs = abs;
        best = NearestWalkable {
            polygon: Some(poly),
            overlaps: abs <= radius - 1e-4,
            inside_edges: find_crossed_edge(&poly.plane, &poly.vertices, local, Vec3::Z).is_none(),
        };
    }
    best
}

#[test]
fn fixtures_all_three_cells_load_and_share_origin() {
    for id in [CELLAR, COTTAGE_A, COTTAGE_B] {
        let cell = load_cell(ASCENT, id);
        assert!(!cell.physics_bsp.polygons().is_empty());
        let o = cell.placement.origin;
        assert!((130.49..=130.51).contains(&o.x));
        assert!((11.49..=11.51).contains(&o.y));
        assert!((93.99..=94.01).contains(&o.z));
    }
}

#[test]
fn cellar_has_most_polygons_cottage_neighbour_b_is_sparse() {
    assert!((30..=50).contains(&load_cell(ASCENT, CELLAR).physics_bsp.polygons().len()));
    assert!((10..=20).contains(&load_cell(ASCENT, COTTAGE_A).physics_bsp.polygons().len()));
    assert!((1..=8).contains(&load_cell(ASCENT, COTTAGE_B).physics_bsp.polygons().len()));
}

#[test]
fn failing_frame_cellar_has_ramp_as_nearest_walkable() {
    let cell = load_cell(ASCENT, CELLAR);
    let n = nearest_walkable(
        &cell,
        cell.placement.to_local(FAILING_SPHERE),
        FAILING_RADIUS,
    );
    let poly = n.polygon.expect("a walkable candidate");
    assert!(poly.plane.normal.z > FLOOR_Z);
}

#[test]
fn failing_frame_cottage_a_nearest_walkable_is_outside_sphere_and_edges() {
    let cell = load_cell(ASCENT, COTTAGE_A);
    let n = nearest_walkable(
        &cell,
        cell.placement.to_local(FAILING_SPHERE),
        FAILING_RADIUS,
    );
    assert!(n.polygon.is_some());
    assert!(!n.overlaps);
    assert!(!n.inside_edges);
}

#[test]
fn failing_frame_cottage_b_has_no_walkable_candidate() {
    let cell = load_cell(ASCENT, COTTAGE_B);
    let n = nearest_walkable(
        &cell,
        cell.placement.to_local(FAILING_SPHERE),
        FAILING_RADIUS,
    );
    assert!(n.polygon.is_none());
}

#[test]
fn failing_frame_no_cottage_neighbour_yields_accepted_walkable() {
    for id in [COTTAGE_A, COTTAGE_B] {
        let cell = load_cell(ASCENT, id);
        let n = nearest_walkable(
            &cell,
            cell.placement.to_local(FAILING_SPHERE),
            FAILING_RADIUS,
        );
        assert!(
            !(n.polygon.is_some() && n.inside_edges && n.overlaps),
            "cell {id:#010X}"
        );
    }
}

#[test]
fn failing_frame_cottage_a_poly_4_has_expected_shape() {
    let cell = load_cell(ASCENT, COTTAGE_A);
    let poly = cell.physics_bsp.polygon(4).expect("poly 4");
    assert_eq!(poly.vertices.len(), 3);
    assert_eq_prec(0.0, poly.plane.normal.x, 4);
    assert_eq_prec(0.0, poly.plane.normal.y, 4);
    assert_eq_prec(1.0, poly.plane.normal.z, 4);
    assert_eq_prec(0.0, poly.plane.d, 4);
}

// ── CellarUpTrajectoryReplayTests ──────────────────────────────────────

const CELLAR_FLOOR_Z: f32 = 90.95;
const RADIUS: f32 = 0.48;
const HEIGHT: f32 = 1.20;
const STEP_UP: f32 = 0.60;
const STEP_DOWN: f32 = 0.04;
const INITIAL_SPHERE: Vec3 = Vec3::new(141.5, 9.5, CELLAR_FLOOR_Z + RADIUS);
const PER_TICK: Vec3 = Vec3::new(0.0, -0.10, 0.0);

fn cellar_engine() -> PhysicsEngine {
    let mut world = CollisionWorld::new();
    for id in [CELLAR, COTTAGE_A, COTTAGE_B] {
        world.insert_cell(load_cell(ASCENT, id));
    }
    const COTTAGE_GFX: u32 = 0x0100_0A2B;
    let cottage = load_gfx_obj(ASCENT, COTTAGE_GFX);
    let radius = cottage.bounding_sphere.radius;
    world.insert_gfx_obj(cottage);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        0xA9B4_0000,
        deep_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    register_single(
        &mut engine,
        0x00A9_B479,
        COTTAGE_GFX,
        Vec3::new(130.5, 11.5, 94.0),
        Quat::from_axis_angle(Vec3::Z, std::f32::consts::PI),
        radius,
        0xA9B4_0000,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

fn initial_body() -> PhysicsBody {
    let floor = Plane::new(Vec3::Z, -CELLAR_FLOOR_Z);
    let mut b = PhysicsBody::new();
    b.set_position(INITIAL_SPHERE);
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = floor;
    b.contact_plane_cell_id = CELLAR;
    b.walkable_polygon_valid = true;
    b.walkable_plane = floor;
    b.walkable_vertices = Some(vec![
        Vec3::new(142.1, 11.5, 90.95),
        Vec3::new(142.1, 8.4, 90.95),
        Vec3::new(140.1, 8.4, 90.95),
        Vec3::new(140.1, 11.5, 90.95),
    ]);
    b.walkable_up = Vec3::Z;
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

struct TrajectoryPoint {
    position: Vec3,
    is_on_ground: bool,
}

#[allow(clippy::too_many_arguments)]
fn simulate(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    cell: u32,
    ticks: usize,
    offset: Vec3,
    height: f32,
    step_up: f32,
) -> Vec<TrajectoryPoint> {
    let mut cell_id = cell;
    let mut grounded = true;
    let mut trajectory = vec![TrajectoryPoint {
        position: body.position(),
        is_on_ground: grounded,
    }];
    for _ in 0..ticks {
        let from = body.position();
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: from,
                target_pos: from + offset,
                cell_id,
                sphere_radius: RADIUS,
                sphere_height: height,
                step_up_height: step_up,
                step_down_height: STEP_DOWN,
                is_on_ground: grounded,
                mover_flags: player(),
                ..ResolveRequest::default()
            },
            Some(body),
        );
        body.set_position(r.position);
        cell_id = r.cell_id;
        grounded = r.is_on_ground;
        trajectory.push(TrajectoryPoint {
            position: r.position,
            is_on_ground: grounded,
        });
    }
    trajectory
}

#[test]
fn indoor_cellar_floor_at_rest_zero_offset_is_bit_stable() {
    let engine = cellar_engine();
    let mut body = initial_body();
    let rest = body.position();
    let trajectory = simulate(&engine, &mut body, CELLAR, 200, Vec3::ZERO, HEIGHT, STEP_UP);
    let drift = trajectory
        .iter()
        .map(|p| (p.position - rest).length())
        .fold(0.0f32, f32::max);
    assert_eq!(drift, 0.0, "rest drifted {} um", drift * 1e6);
}

#[test]
fn harness_runs_the_simulation() {
    let engine = cellar_engine();
    let mut body = initial_body();
    assert_eq!(
        simulate(&engine, &mut body, CELLAR, 200, PER_TICK, HEIGHT, STEP_UP).len(),
        201
    );
}

#[test]
fn harness_finding_sphere_goes_airborne_at_tick_1() {
    let engine = cellar_engine();
    let mut body = initial_body();
    let t = simulate(&engine, &mut body, CELLAR, 3, PER_TICK, HEIGHT, STEP_UP);
    assert!(t[0].is_on_ground);
    assert!(!t[1].is_on_ground);
}

#[test]
fn harness_simulation_runs_in_under_500_ms() {
    let engine = cellar_engine();
    let mut body = initial_body();
    let start = Instant::now();
    simulate(&engine, &mut body, CELLAR, 200, PER_TICK, HEIGHT, STEP_UP);
    assert!(
        start.elapsed().as_millis() < 500,
        "took {:?}",
        start.elapsed()
    );
}

/// OpenAC's `AssertCallMatchesCapture`: the replayed call and body must
/// match the live capture (positions within 1 mm, planes within 1e-3).
fn assert_call_matches_capture(
    engine: &PhysicsEngine,
    rec: &CaptureRecord,
    stale_is_on_ground_echo: bool,
) {
    let before = rec.body_before.as_ref().expect("body before");
    let after = rec.body_after.as_ref().expect("body after");
    let mut body = seed_body(before);
    let r = replay(engine, &rec.input, Some(&mut body));
    let mut divergences = Vec::new();
    let mut vec_check = |name: &str, live: Vec3, ours: Vec3| {
        if live.distance_squared(ours) >= 1e-6 {
            divergences.push(format!("{name}: live {live} ours {ours}"));
        }
    };
    vec_check("result position", rec.result.position, r.position);
    vec_check("body position", after.position, body.position());
    if rec.result.collision_normal_valid && r.collision_normal_valid {
        vec_check(
            "collision normal",
            rec.result.collision_normal,
            r.collision_normal,
        );
    }
    if after.contact_plane_valid && body.contact_plane_valid {
        vec_check(
            "contact normal",
            after.contact_plane.normal,
            body.contact_plane.normal,
        );
        if (after.contact_plane.d - body.contact_plane.d).abs() >= 1e-3 {
            divergences.push(format!(
                "contact d: live {} ours {}",
                after.contact_plane.d, body.contact_plane.d
            ));
        }
    }
    let expected_ground = if stale_is_on_ground_echo {
        false
    } else {
        rec.result.is_on_ground
    };
    let mut flag = |name: &str, live: String, ours: String| {
        if live != ours {
            divergences.push(format!("{name}: live {live} ours {ours}"));
        }
    };
    flag(
        "cell",
        format!("{:#010X}", rec.result.cell_id),
        format!("{:#010X}", r.cell_id),
    );
    flag(
        "is_on_ground",
        expected_ground.to_string(),
        r.is_on_ground.to_string(),
    );
    flag(
        "collision normal valid",
        rec.result.collision_normal_valid.to_string(),
        r.collision_normal_valid.to_string(),
    );
    flag(
        "contact plane valid",
        after.contact_plane_valid.to_string(),
        body.contact_plane_valid.to_string(),
    );
    flag(
        "walkable valid",
        after.walkable_polygon_valid.to_string(),
        body.walkable_polygon_valid.to_string(),
    );
    flag(
        "transient",
        format!("{:#X}", after.transient_state),
        format!("{:#X}", body.transient_state.bits()),
    );
    assert!(
        divergences.is_empty(),
        "replay of tick {} diverges:\n  {}",
        rec.tick,
        divergences.join("\n  ")
    );
}

fn ascent_capture(pred: impl Fn(&CaptureRecord) -> bool) -> CaptureRecord {
    load_captures(ASCENT, "live-capture.jsonl")
        .into_iter()
        .find(pred)
        .expect("a captured record")
}

#[test]
fn live_compare_tick_0_spawn() {
    assert_call_matches_capture(&cellar_engine(), &ascent_capture(|r| r.tick == 0), true);
}

#[test]
fn live_compare_tick_376_on_ramp() {
    assert_call_matches_capture(&cellar_engine(), &ascent_capture(|r| r.tick == 376), true);
}

#[test]
fn live_compare_first_cap_fix_closes_cottage_floor_cap() {
    let rec =
        ascent_capture(|r| r.result.collision_normal_valid && r.result.collision_normal.z < -0.99);
    let mut body = seed_body(rec.body_before.as_ref().expect("body before"));
    let r = replay(&cellar_engine(), &rec.input, Some(&mut body));
    assert!(
        !(r.collision_normal_valid && r.collision_normal.z < -0.99),
        "cottage-floor cap came back: {} (valid {})",
        r.collision_normal,
        r.collision_normal_valid
    );
}

// ── CellarLipWedgeTests ────────────────────────────────────────────────

const COTTAGE_FLOOR: u32 = 0xA9B4_0171;
const CONNECTOR_74: u32 = 0xA9B4_0174;
const THRESHOLD: u32 = 0xA9B4_0175;
const COTTAGE_FLOOR_Z: f32 = 94.0;
const WEDGE_BODY: Vec3 = Vec3::new(153.406, 9.754, 93.936 - RADIUS);

fn lip_engine() -> PhysicsEngine {
    let mut world = CollisionWorld::new();
    for id in [COTTAGE_FLOOR, CONNECTOR_74, THRESHOLD] {
        world.insert_cell(load_cell(LIP, id));
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        0xA9B4_0000,
        deep_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

fn wedge_body() -> PhysicsBody {
    let plane = Plane::new(Vec3::Z, -WEDGE_BODY.z);
    let mut b = PhysicsBody::new();
    b.set_position(WEDGE_BODY);
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = plane;
    b.contact_plane_cell_id = THRESHOLD;
    b.walkable_polygon_valid = true;
    b.walkable_plane = plane;
    let p = WEDGE_BODY;
    b.walkable_vertices = Some(vec![
        Vec3::new(p.x - 1.0, p.y - 1.0, p.z),
        Vec3::new(p.x - 1.0, p.y + 1.0, p.z),
        Vec3::new(p.x + 1.0, p.y + 1.0, p.z),
        Vec3::new(p.x + 1.0, p.y - 1.0, p.z),
    ]);
    b.walkable_up = Vec3::Z;
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

#[test]
fn documents_wedge_player_frozen_at_threshold() {
    let engine = lip_engine();
    let mut body = wedge_body();
    let t = simulate(&engine, &mut body, THRESHOLD, 30, PER_TICK, HEIGHT, 0.40);
    let last = t.last().unwrap().position;
    let y_advance = WEDGE_BODY.y - last.y;
    let z_rise = last.z - WEDGE_BODY.z;
    assert!(
        y_advance < 0.1 && z_rise < 0.1,
        "expected frozen at the threshold, advanced to {last}"
    );
}

/// Replays one wedge record: (resolved position, requested, advance).
fn replay_wedge(rec: &CaptureRecord) -> (Vec3, f32, f32) {
    let mut body = seed_body(rec.body_before.as_ref().expect("body before"));
    let r = replay(&lip_engine(), &rec.input, Some(&mut body));
    let requested = rec.input.current_pos.distance(rec.input.target_pos);
    let advance = rec.input.current_pos.distance(r.position);
    (r.position, requested, advance)
}

fn wedge_records() -> Vec<CaptureRecord> {
    load_captures(LIP, "wedge-records.jsonl")
}

#[test]
fn fix_stale_foot_center_ramp_record_climbs_cottage_floor() {
    let (res, requested, advance) = replay_wedge(&wedge_records()[9]);
    assert!(
        advance > 0.25 * requested && res.z >= COTTAGE_FLOOR_Z - 0.05,
        "advance {advance} of {requested}, at {res}"
    );
}

#[test]
fn fix_stale_foot_center_majority_of_wedge_records_advance() {
    let records = wedge_records();
    let mut advanced = 0;
    let mut total = 0;
    for rec in records.iter().filter(|r| r.body_before.is_some()) {
        total += 1;
        let (_, requested, advance) = replay_wedge(rec);
        if advance > 0.25 * requested {
            advanced += 1;
        }
    }
    assert!(advanced >= 18, "{advanced} of {total} advanced");
}

#[test]
fn documents_residual_wedge_live_floor_contact_sliding_normal_kills_plus_y() {
    let records = wedge_records();
    let rec = records
        .iter()
        .find(|r| {
            r.body_before
                .as_ref()
                .is_some_and(|b| b.contact_plane.normal.z > 0.99)
        })
        .expect("a floor-contact record");
    let (res, requested, advance) = replay_wedge(rec);
    assert!(
        advance < 0.1 * requested,
        "advanced to {res}: {advance} of {requested}"
    );
}
