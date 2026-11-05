//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/RetailEdgeResponseOrderingTests.cs`.
//!
//! OpenAC runs each multi-frame trace over its graph and its prepared-flat
//! trees and requires bit-identical frames; with one representation here
//! the traces run once and keep every behavioral assertion.

use std::sync::Arc;

use glam::{Quat, Vec2, Vec3};

use super::bsp_step_up::{
    SLOPED_SLOPE_ID, SPHERE_RADIUS, TALL_WALL_FLOOR_ID, flat_roof, make_grounded_transition_in,
    sloped_unwalkable, tall_wall,
};
use super::support::*;
use crate::bsp::PhysicsBsp;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::Plane;
use crate::globals::{EPSILON, FLOOR_Z};
use crate::shadow::ShadowCollisionType;
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::{Transition, TransitionCellCollisionPhase, get_step_down_probe_plan};
use crate::world::CollisionWorld;
use crate::{PhysicsBody, PhysicsState, TransientState};

const CELL: u32 = 0xA9B4_0001;

fn steep() -> Plane {
    Plane::new(Vec3::new(1.0, 0.0, 0.25).normalize(), 0.0)
}

fn add_state(t: &mut Transition, bits: u32) {
    t.object_info.state = ObjectInfoState(t.object_info.state.0 | bits);
}

#[test]
fn transitional_insert_valid_steep_contact_returns_before_ordinary_step_down_tail() {
    let current = Vec3::new(2.0, 3.0, 4.0);
    let target = current + Vec3::new(0.1, 0.0, 0.0);
    let mut t = make_grounded_transition_in(current, target, 0.30, CELL);
    add_state(&mut t, ObjectInfoState::EDGE_SLIDE);
    t.object_info.step_down = true;
    t.object_info.step_down_height = 2.0;
    let untouched = Vec3::new(97.0, 98.0, 99.0);
    t.sphere_path.backup_check_pos = untouched;
    t.sphere_path.backup_check_cell_id = 0xA9B4_0044;

    let mut engine = PhysicsEngine::new();
    engine.set_cell_collision_hook(Some(Box::new(|c, phase, _, actual| {
        if phase == TransitionCellCollisionPhase::Objects {
            c.collision_info.set_contact_plane(steep(), CELL, true);
        }
        actual
    })));
    assert_eq!(t.transitional_insert(1, &engine), TransitionState::Ok);
    assert!(t.collision_info.contact_plane_valid);
    assert!(t.collision_info.contact_plane_is_water);
    assert_eq!(t.collision_info.contact_plane, steep());
    assert_eq!(t.sphere_path.backup_check_pos, untouched);
    assert_eq!(t.sphere_path.backup_check_cell_id, 0xA9B4_0044);
}

#[test]
fn step_down_probe_plan_preserves_retail_one_versus_two_sphere_split() {
    assert_eq!(get_step_down_probe_plan(1, 0.5, 2.0), (0.25, 1));
    assert_eq!(get_step_down_probe_plan(2, 0.5, 2.0), (1.0, 2));
    assert_eq!(get_step_down_probe_plan(1, 0.5, 0.75), (0.75, 1));
    assert_eq!(get_step_down_probe_plan(2, 0.5, 0.75), (0.75, 1));
}

fn failed_step_down_transition() -> Transition {
    let failed = Vec3::X;
    let mut t = make_grounded_transition_in(Vec3::ZERO, failed, 0.30, CELL);
    add_state(&mut t, ObjectInfoState::EDGE_SLIDE);
    t.sphere_path.set_check_pos(failed, CELL);
    t.sphere_path.save_check_pos();
    t.sphere_path
        .add_offset_to_check_pos(Vec3::new(0.0, 0.0, -0.25));
    t
}

#[test]
fn edge_slide_not_on_walkable_steep_contact_restores_before_cliff_slide() {
    let mut t = failed_step_down_transition();
    t.object_info.state = ObjectInfoState(ObjectInfoState::EDGE_SLIDE);
    t.collision_info.contact_plane_valid = true;
    t.collision_info.contact_plane = steep();
    t.collision_info.contact_plane_is_water = true;
    t.collision_info.last_known_contact_plane_valid = true;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    let failed = t.sphere_path.backup_check_pos;
    let (stop, result) = t.edge_slide_after_step_down_failed(&PhysicsEngine::new(), 0.04, FLOOR_Z);
    assert!(stop);
    assert_eq!(result, TransitionState::Ok);
    assert_eq!(t.sphere_path.check_pos, failed);
    assert!(!t.collision_info.contact_plane_valid);
    assert!(!t.collision_info.contact_plane_is_water);
    assert!(!t.collision_info.collision_normal_valid);
}

fn square_on_z0() -> [Vec3; 4] {
    [
        Vec3::new(-2.0, -2.0, 0.0),
        Vec3::new(2.0, -2.0, 0.0),
        Vec3::new(2.0, 2.0, 0.0),
        Vec3::new(-2.0, 2.0, 0.0),
    ]
}

#[test]
fn cliff_slide_uses_only_last_known_contact_plane_normal() {
    let mut t = failed_step_down_transition();
    t.sphere_path.set_walkable(
        Plane::new(Vec3::new(0.0, 1.0, 1.0).normalize(), 0.0),
        &square_on_z0(),
        Vec3::Z,
    );
    t.sphere_path.clear_walkable();
    t.collision_info.last_known_contact_plane_valid = true;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    let result = t.cliff_slide(Plane::new(Vec3::new(1.0, 0.0, 0.5).normalize(), 0.0));
    assert_eq!(result, TransitionState::Adjusted);
    assert!(t.collision_info.collision_normal_valid);
    assert!((-Vec3::X).distance(t.collision_info.collision_normal) < 0.0001);
}

#[test]
fn cliff_slide_invalid_default_last_known_plane_takes_degenerate_ok_return() {
    let mut t = failed_step_down_transition();
    t.collision_info.last_known_contact_plane_valid = false;
    t.collision_info.last_known_contact_plane = Plane::default();
    let result = t.cliff_slide(Plane::new(Vec3::new(1.0, 0.0, 0.5).normalize(), 0.0));
    assert_eq!(result, TransitionState::Ok);
    assert!(!t.collision_info.collision_normal_valid);
}

#[test]
fn edge_slide_stored_steep_walkable_always_routes_to_precipice_slide() {
    let mut t = failed_step_down_transition();
    t.object_info.state = ObjectInfoState(
        ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE | ObjectInfoState::EDGE_SLIDE,
    );
    t.collision_info.contact_plane_valid = false;
    t.collision_info.last_known_contact_plane_valid = true;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    let steep_plane = Plane::new(Vec3::new(-2.0, 0.0, 1.0).normalize(), 0.0);
    let quad = [
        Vec3::new(0.0, -1.0, 0.0),
        Vec3::new(1.0, -1.0, 2.0),
        Vec3::new(1.0, 1.0, 2.0),
        Vec3::new(0.0, 1.0, 0.0),
    ];
    t.sphere_path.set_walkable(steep_plane, &quad, Vec3::Z);
    let failed = Vec3::new(0.5, 0.0, 1.0);
    t.sphere_path.set_check_pos(failed, CELL);
    t.sphere_path.save_check_pos();
    t.sphere_path
        .add_offset_to_check_pos(Vec3::new(0.0, 0.0, -0.25));

    let (stop, result) = t.edge_slide_after_step_down_failed(&PhysicsEngine::new(), 0.04, FLOOR_Z);
    assert!(stop);
    assert_eq!(result, TransitionState::Collided);
    assert_eq!(t.sphere_path.check_pos, failed);
    assert!(!t.sphere_path.has_walkable_polygon());
    assert!(!t.collision_info.collision_normal_valid);
}

#[test]
fn transitional_insert_degenerate_cliff_slide_ok_continues_outer_retry() {
    let current = Vec3::new(2.0, 3.0, 4.0);
    let target = current + Vec3::new(0.1, 0.0, 0.0);
    let mut t = make_grounded_transition_in(current, target, 0.30, CELL);
    add_state(&mut t, ObjectInfoState::EDGE_SLIDE);
    t.object_info.step_down = true;
    t.object_info.step_down_height = 0.04;

    let outer_passes = Arc::new(SyncCell::new(0));
    let o = outer_passes.clone();
    let mut engine = PhysicsEngine::new();
    engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
        if phase != TransitionCellCollisionPhase::Objects {
            return actual;
        }
        if c.sphere_path.step_down {
            c.collision_info.set_contact_plane(steep(), CELL, false);
        } else {
            o.set(o.get() + 1);
            if o.get() == 2 {
                c.collision_info
                    .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
            }
        }
        actual
    })));
    assert_eq!(t.transitional_insert(2, &engine), TransitionState::Ok);
    assert_eq!(outer_passes.get(), 2);
    assert!(t.collision_info.contact_plane_valid);
    assert_eq!(t.collision_info.contact_plane.normal, Vec3::Z);
}

// ── Multi-frame traces ─────────────────────────────────────────────────

/// An engine over terrain far below, with `bsp` registered as a model at
/// the origin (or with nothing, for the no-geometry control).
fn collision_engine(bsp: PhysicsBsp, gfx_id: u32, include_geometry: bool) -> PhysicsEngine {
    let root = *bsp.nodes()[0].sphere();
    let mut world = CollisionWorld::new();
    if include_geometry {
        let mut gfx = gfx_obj(gfx_id, bsp, root.radius);
        gfx.bounding_sphere = root;
        world.insert_gfx_obj(gfx);
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        0xA9B4_0000,
        TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    if include_geometry {
        register_single(
            &mut engine,
            gfx_id,
            gfx_id,
            Vec3::ZERO,
            Quat::IDENTITY,
            root.radius,
            0xA9B4_FFFF,
            ShadowCollisionType::Bsp,
            1.0,
            1.0,
            0,
            EntityCollisionFlags::default(),
            0,
        );
    }
    engine
}

#[allow(clippy::too_many_arguments)]
fn resolve(
    engine: &PhysicsEngine,
    from: Vec3,
    to: Vec3,
    height: f32,
    step_up: f32,
    grounded: bool,
    body: &mut PhysicsBody,
    mover_id: u32,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: CELL,
            sphere_radius: SPHERE_RADIUS,
            sphere_height: height,
            step_up_height: step_up,
            step_down_height: 0.04,
            is_on_ground: grounded,
            mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
            moving_entity_id: mover_id,
            ..ResolveRequest::default()
        },
        Some(body),
    )
}

fn apply_contact_result(body: &mut PhysicsBody, r: &ResolveResult) {
    body.transient_state
        .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
    if r.in_contact {
        body.transient_state.insert(TransientState::CONTACT);
    }
    if r.on_walkable {
        body.transient_state.insert(TransientState::ON_WALKABLE);
    }
}

fn falling_body(position: Vec3) -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.set_position(position);
    body.orientation = Quat::IDENTITY;
    body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    body.transient_state = TransientState::ACTIVE;
    body
}

#[track_caller]
fn assert_no_long_frozen_streak(frames: &[ResolveResult], maximum: usize) {
    let mut streak = 0;
    for i in 1..frames.len() {
        streak = if frames[i - 1].position.distance(frames[i].position) < 0.001 {
            streak + 1
        } else {
            0
        };
        assert!(
            streak <= maximum,
            "froze {streak} ticks at {}",
            frames[i].position
        );
    }
}

fn steep_roof_trace(horizontal: Vec2) -> Vec<ResolveResult> {
    let engine = collision_engine(sloped_unwalkable(), 0x0100_E101, true);
    const DT: f32 = 1.0 / 30.0;
    let mut body = falling_body(Vec3::new(0.5, 0.0, 3.0));
    let mut position = Vec3::new(0.5, 0.0, 3.0);
    let mut velocity_z = 0.0f32;
    let mut trace = Vec::new();
    for _ in 0..90 {
        velocity_z += -9.8 * DT;
        body.velocity = Vec3::new(horizontal.x, horizontal.y, velocity_z);
        let grounded = body.on_walkable();
        let to = position + body.velocity * DT;
        let r = resolve(
            &engine,
            position,
            to,
            SPHERE_RADIUS * 2.0,
            0.30,
            grounded,
            &mut body,
            0x0100_0000,
        );
        position = r.position;
        body.set_position(position);
        if r.is_on_ground {
            velocity_z = 0.0;
            body.velocity = Vec3::new(horizontal.x, horizontal.y, 0.0);
        }
        apply_contact_result(&mut body, &r);
        trace.push(r);
        if position.x < 0.0 && position.z <= SPHERE_RADIUS + 0.05 {
            break;
        }
    }
    trace
}

#[track_caller]
fn assert_roof_trace_sane(trace: &[ResolveResult], label: &str) {
    let slope = sloped_unwalkable().polygon(SLOPED_SLOPE_ID).unwrap().plane;
    for (i, r) in trace.iter().enumerate() {
        let p = r.position;
        assert!(p.is_finite(), "{label} frame {i}: {p}");
        if i > 0 {
            assert!(
                (0.0..=1.1).contains(&trace[i - 1].position.distance(p)),
                "{label} frame {i} jumped"
            );
        }
        if (0.0..=1.0).contains(&p.x) && p.y.abs() <= 1.0 {
            let foot = p + Vec3::new(0.0, 0.0, SPHERE_RADIUS);
            let distance = slope.normal.dot(foot) + slope.d;
            assert!(
                distance >= SPHERE_RADIUS - 0.015,
                "{label} penetration at frame {i}: {distance} at {p}"
            );
        }
    }
}

#[test]
fn multi_frame_steep_roof_pure_vertical_slides_downhill_without_wedge() {
    let trace = steep_roof_trace(Vec2::ZERO);
    assert!(trace.iter().any(|r| r.in_contact));
    assert_no_long_frozen_streak(&trace, 15);
    assert!(
        trace.last().unwrap().position.x < trace[0].position.x - 0.20,
        "did not descend: {} -> {}",
        trace[0].position,
        trace.last().unwrap().position
    );
    assert_roof_trace_sane(&trace, "vertical");
}

#[test]
fn multi_frame_steep_roof_directional_motion_preserves_retail_response() {
    for (vx, vy, direction) in [
        (-0.30, 0.0, "downhill"),
        (0.30, 0.0, "uphill"),
        (0.0, 0.30, "tangential"),
    ] {
        let velocity = Vec2::new(vx, vy);
        let trace = steep_roof_trace(velocity);
        assert!(trace.iter().any(|r| r.in_contact), "{direction}");
        assert_no_long_frozen_streak(&trace, 15);
        let first = trace[0].position;
        let last = trace.last().unwrap().position;
        let progress = Vec2::new(last.x - first.x, last.y - first.y);
        if direction == "uphill" {
            let contact = trace.iter().position(|r| r.in_contact).unwrap();
            let peak = trace[contact..]
                .iter()
                .map(|r| r.position.z)
                .fold(f32::NEG_INFINITY, f32::max);
            assert!(
                peak <= trace[contact].position.z + 0.001,
                "uphill bounced: contact z {}, peak {peak}",
                trace[contact].position.z
            );
        } else {
            assert!(
                progress.dot(velocity.normalize()) > 0.10,
                "{direction} lost progress: {first} -> {last}"
            );
        }
        assert_roof_trace_sane(&trace, direction);
    }
}

struct LedgeTrace {
    frames: Vec<(ResolveResult, bool, Plane, bool, TransientState)>,
    landed: Option<usize>,
    ledge_start: usize,
}

fn flat_roof_ledge_trace(include_roof: bool) -> LedgeTrace {
    let engine = collision_engine(flat_roof(), 0x0100_E102, include_roof);
    const DT: f32 = 1.0 / 30.0;
    let mut position = Vec3::new(1.55, -0.75, 3.6);
    let mut velocity_z = 0.0f32;
    let mut body = falling_body(position);
    let mut frames = Vec::new();
    let mut landed = None;
    let capture = |r: ResolveResult, b: &PhysicsBody| {
        (
            r,
            b.contact_plane_valid,
            b.contact_plane,
            b.walkable_polygon_valid,
            b.transient_state,
        )
    };
    for _ in 0..60 {
        velocity_z += PhysicsBody::GRAVITY * DT;
        body.velocity = Vec3::new(0.0, 0.0, velocity_z);
        let grounded = body.on_walkable();
        let r = resolve(
            &engine,
            position,
            position + Vec3::new(0.0, 0.0, velocity_z * DT),
            SPHERE_RADIUS * 2.0,
            0.30,
            grounded,
            &mut body,
            0x0100_0001,
        );
        position = r.position;
        body.set_position(position);
        apply_contact_result(&mut body, &r);
        let done = r.in_contact && r.on_walkable;
        frames.push(capture(r, &body));
        if done {
            landed = Some(frames.len() - 1);
            body.velocity = Vec3::ZERO;
            break;
        }
    }
    let ledge_start = frames.len();
    if landed.is_some() {
        for _ in 0..12 {
            let grounded = body.on_walkable();
            let r = resolve(
                &engine,
                position,
                position + Vec3::new(0.12, 0.08, 0.0),
                SPHERE_RADIUS * 2.0,
                0.30,
                grounded,
                &mut body,
                0x0100_0001,
            );
            position = r.position;
            body.set_position(position);
            apply_contact_result(&mut body, &r);
            frames.push(capture(r, &body));
        }
    }
    LedgeTrace {
        frames,
        landed,
        ledge_start,
    }
}

#[test]
fn multi_frame_flat_roof_ledge_lands_and_slides_along_edge() {
    let run = flat_roof_ledge_trace(true);
    let no_roof = flat_roof_ledge_trace(false);
    let landed = run.landed.expect("never landed on the roof");
    assert!(landed > 0);
    assert!(no_roof.landed.is_none());
    assert!(run.frames[..landed].iter().all(|f| !f.0.on_walkable));
    let (landing, contact_valid, contact_plane, walkable_valid, transient) = &run.frames[landed];
    assert!(landing.ok);
    assert!(landing.is_on_ground);
    assert!(landing.in_contact);
    assert!(landing.on_walkable);
    assert!(*contact_valid);
    assert!(*walkable_valid);
    assert_eq!(contact_plane.normal, Vec3::Z);
    assert!(transient.contains(TransientState::CONTACT | TransientState::ON_WALKABLE));

    let ledge: Vec<ResolveResult> = run.frames[run.ledge_start..].iter().map(|f| f.0).collect();
    assert_no_long_frozen_streak(&ledge, 15);
    let rejection = ledge
        .iter()
        .position(|r| r.collision_normal_valid && r.collision_normal.x < -0.9)
        .expect("no outward-x rejection normal");
    let rejected_x = ledge[rejection].position.x;
    assert!(
        ledge[rejection..]
            .iter()
            .all(|r| r.position.x <= rejected_x + 0.001)
    );
    let unobstructed = ledge[0].position.x + 0.12 * (ledge.len() - 1) as f32;
    assert!(ledge.last().unwrap().position.x < unobstructed - 0.25);
    assert!(ledge.last().unwrap().position.y > ledge[rejection].position.y + 0.25);
}

#[test]
fn multi_frame_grounded_floor_wall_slide_keeps_contact_and_tangential_motion() {
    for two_spheres in [false, true] {
        let bsp = tall_wall();
        let floor = bsp.polygon(TALL_WALL_FLOOR_ID).unwrap().clone();
        let mut engine = collision_engine(bsp, 0x0100_E103, true);
        let support = Arc::new(SyncCell::new(0));
        let placement = Arc::new(SyncCell::new(0));
        let (s, p) = (support.clone(), placement.clone());
        engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
            if phase == TransitionCellCollisionPhase::Environment {
                if c.sphere_path.step_down
                    && actual == TransitionState::Ok
                    && c.collision_info.contact_plane_valid
                    && c.collision_info.contact_plane.normal.z
                        >= c.sphere_path.walk.walkable_allowance
                {
                    s.set(s.get() + 1);
                } else if c.sphere_path.insert_type == InsertType::Placement {
                    p.set(p.get() + 1);
                }
            }
            actual
        })));

        let mut position = Vec3::new(0.5 - SPHERE_RADIUS, -0.55, 0.0);
        let mut body = PhysicsBody::new();
        body.set_position(position);
        body.orientation = Quat::IDENTITY;
        body.ground_normal = Vec3::Z;
        body.contact_plane_valid = true;
        body.contact_plane = floor.plane;
        body.contact_plane_cell_id = CELL;
        body.walkable_polygon_valid = true;
        body.walkable_plane = floor.plane;
        body.walkable_vertices = Some(floor.vertices.clone());
        body.walkable_up = Vec3::Z;
        body.transient_state =
            TransientState::ACTIVE | TransientState::CONTACT | TransientState::ON_WALKABLE;

        let mut frames = Vec::new();
        for _ in 0..10 {
            body.velocity = Vec3::new(1.8, 2.1, 0.0);
            let r = engine.resolve_with_transition(
                &ResolveRequest {
                    current_pos: position,
                    target_pos: position + Vec3::new(0.06, 0.07, 0.0),
                    cell_id: CELL,
                    sphere_radius: SPHERE_RADIUS,
                    sphere_height: if two_spheres { 1.835 } else { 0.0 },
                    step_up_height: 0.04,
                    step_down_height: 0.04,
                    is_on_ground: true,
                    mover_flags: ObjectInfoState(
                        ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE,
                    ),
                    moving_entity_id: 0x0100_0002,
                    ..ResolveRequest::default()
                },
                Some(&mut body),
            );
            position = r.position;
            body.set_position(position);
            apply_contact_result(&mut body, &r);
            frames.push(r);
        }

        assert!(support.get() > 0, "never maintained ground support");
        assert_eq!(support.get(), frames.len());
        assert_eq!(support.get(), placement.get());
        let wall_limit = 0.5 - SPHERE_RADIUS + EPSILON * 10.0;
        for (i, r) in frames.iter().enumerate() {
            assert!(r.ok, "frame {i}");
            assert!(r.in_contact, "frame {i} lost contact");
            assert!(r.on_walkable, "frame {i} lost walkable");
            assert!(
                r.position.x <= wall_limit,
                "frame {i} penetrated: {}",
                r.position.x
            );
        }
        assert_no_long_frozen_streak(&frames, 1);
        assert!(frames.last().unwrap().position.y > frames[0].position.y + 0.25);
    }
}
