//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/TransitionInsertIntoCellRetryTests.cs`
//! (its graph-representation cases; the prepared-flat variant has no
//! counterpart here).

use std::sync::Arc;

use glam::{Quat, Vec3};

use super::bsp_step_up::{flat_roof, make_airborne_transition};
use super::support::*;
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::Plane;
use crate::state::{ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::TransitionCellCollisionPhase as Phase;
use crate::world::{BuildingPhysics, CollisionWorld, Placement};
use crate::{PhysicsBody, TransientState};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = 0xA9B4_0001;
const SHELL_GFX: u32 = 0x0100_F001;
const CURRENT: Vec3 = Vec3::new(10.0, 10.0, 5.0);

fn build_engine() -> PhysicsEngine {
    // The shell's bounding sphere is its root node's: (0, 0, 1.5), 10 m.
    let mut shell = gfx_obj(SHELL_GFX, flat_roof(), 10.0);
    shell.bounding_sphere.center = Vec3::new(0.0, 0.0, 1.5);
    let mut world = CollisionWorld::new();
    world.insert_gfx_obj(shell);
    world.cache_building(
        CELL,
        BuildingPhysics {
            placement: Placement::at(Vec3::splat(100.0)),
            portals: Vec::new(),
            model_id: SHELL_GFX,
        },
    );
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        LANDBLOCK,
        TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

fn resolve_airborne(engine: &PhysicsEngine, current: Vec3, target: Vec3) -> ResolveResult {
    let mut body = PhysicsBody::new();
    body.set_position(current);
    body.orientation = Quat::IDENTITY;
    body.transient_state = TransientState::ACTIVE;
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: current,
            target_pos: target,
            cell_id: CELL,
            sphere_radius: 0.48,
            sphere_height: 1.835,
            step_up_height: 0.6,
            step_down_height: 1.5,
            is_on_ground: false,
            mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
            moving_entity_id: 0x000F_4243,
            ..ResolveRequest::default()
        },
        Some(&mut body),
    )
}

#[test]
fn nested_retry_recomputes_atomic_cell_pipeline_and_exceeds_outer_budget() {
    let mut engine = build_engine();
    let phases = Arc::new(SyncRefCell::new(Vec::new()));
    let counts = Arc::new(SyncRefCell::new([0; 3]));
    let (ph, co) = (phases.clone(), counts.clone());
    engine.set_cell_collision_hook(Some(Box::new(move |_, phase, cell, actual| {
        assert_eq!(cell, CELL);
        assert_eq!(actual, TransitionState::Ok);
        ph.borrow_mut().push(phase);
        let index = match phase {
            Phase::Environment => 0,
            Phase::Building => 1,
            Phase::Objects => 2,
        };
        let mut c = co.borrow_mut();
        c[index] += 1;
        if c[index] == 1 {
            TransitionState::Adjusted
        } else {
            TransitionState::Ok
        }
    })));
    let target = CURRENT + Vec3::new(0.05, 0.0, 0.0);
    let result = resolve_airborne(&engine, CURRENT, target);
    assert!(result.ok);
    assert_eq!(result.position, target);
    use Phase::{Building as B, Environment as E, Objects as O};
    assert_eq!(*phases.borrow(), vec![E, E, B, E, B, O, E, B, O]);
    assert_eq!(*counts.borrow(), [4, 3, 2]);
}

#[test]
fn nested_retry_always_adjusted_exhausts_exact_n_by_n_budget() {
    let mut engine = build_engine();
    let calls = Arc::new(SyncCell::new(0));
    let c = calls.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |_, phase, cell, actual| {
        assert_eq!(phase, Phase::Environment);
        assert_eq!(cell, CELL);
        assert_eq!(actual, TransitionState::Ok);
        c.set(c.get() + 1);
        TransitionState::Adjusted
    })));
    let target = CURRENT + Vec3::new(0.05, 0.0, 0.0);
    let mut t = make_airborne_transition(CURRENT, target);
    t.sphere_path.set_check_pos(target, CELL);
    assert_eq!(t.transitional_insert(3, &engine), TransitionState::Adjusted);
    assert_eq!(t.sphere_path.check_pos, target);
    assert_eq!(calls.get(), 9);
}

#[test]
fn nested_retry_slid_clears_inner_contact_then_outer_neg_poly() {
    let mut engine = build_engine();
    let calls = Arc::new(SyncCell::new(0));
    let c = calls.clone();
    let contact = Plane::new(Vec3::Z, -5.0);
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, actual| {
        if phase != Phase::Environment {
            return actual;
        }
        c.set(c.get() + 1);
        if c.get() <= 3 {
            if c.get() > 1 {
                assert!(!t.collision_info.contact_plane_valid);
                assert!(!t.collision_info.contact_plane_is_water);
                assert!(t.sphere_path.neg_poly_hit);
            }
            t.collision_info.set_contact_plane(contact, CELL, true);
            t.sphere_path.neg_poly_hit = true;
            return TransitionState::Slid;
        }
        assert!(!t.collision_info.contact_plane_valid);
        assert!(!t.collision_info.contact_plane_is_water);
        assert!(!t.sphere_path.neg_poly_hit);
        actual
    })));
    let result = resolve_airborne(&engine, CURRENT, CURRENT + Vec3::new(0.05, 0.0, 0.0));
    assert!(result.ok);
    assert_eq!(calls.get(), 4);
}

#[test]
fn nested_retry_fixes_primary_cell_until_next_outer_attempt() {
    const NEXT: u32 = 0xA9B4_0002;
    let mut engine = build_engine();
    let log = Arc::new(SyncRefCell::new(Vec::new()));
    let calls = Arc::new(SyncCell::new(0));
    let (l, c) = (log.clone(), calls.clone());
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, cell, actual| {
        l.borrow_mut().push((phase, cell));
        if phase == Phase::Environment {
            c.set(c.get() + 1);
            if c.get() == 1 {
                let check = t.sphere_path.check_pos;
                t.sphere_path.set_check_pos(check, NEXT);
            }
            if c.get() <= 3 {
                return TransitionState::Adjusted;
            }
        }
        actual
    })));
    let target = CURRENT + Vec3::new(0.05, 0.0, 0.0);
    let result = resolve_airborne(&engine, CURRENT, target);
    assert!(result.ok);
    assert_eq!(result.position, target);
    use Phase::{Building as B, Environment as E, Objects as O};
    assert_eq!(
        *log.borrow(),
        vec![
            (E, CELL),
            (E, CELL),
            (E, CELL),
            (E, NEXT),
            (B, NEXT),
            (O, NEXT)
        ]
    );
}
