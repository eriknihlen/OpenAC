//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/RetailStepDownPlacementTests.cs`.
//! The placement-dispatcher case runs once (one tree representation).

use std::sync::Arc;

use glam::{Quat, Vec3};

use super::support::*;
use crate::engine::PhysicsEngine;
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON, FLOOR_Z};
use crate::polygon::ResolvedPolygon;
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::transition::{Transition, TransitionCellCollisionPhase};

const CELL: u32 = 0xA9B4_0001;
const RADIUS: f32 = 0.48;

fn make_grounded_transition(two_spheres: bool) -> Transition {
    let current = Vec3::new(2.0, 3.0, 4.0);
    let target = current + Vec3::new(0.1, 0.0, 0.0);
    let mut t = Transition::new();
    t.sphere_path.init_path(
        current,
        target,
        CELL,
        RADIUS,
        if two_spheres { 1.835 } else { 0.0 },
        None,
        None,
        None,
    );
    t.sphere_path.set_check_pos(target, CELL);
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    t.object_info.step_down = true;
    t.object_info.step_down_height = 0.04;
    t.object_info.step_up_height = 0.60;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    t.collision_info.last_known_contact_plane_valid = true;
    t
}

fn assert_vector_bits(expected: Vec3, actual: Vec3) {
    assert_eq!(expected.x.to_bits(), actual.x.to_bits());
    assert_eq!(expected.y.to_bits(), actual.y.to_bits());
    assert_eq!(expected.z.to_bits(), actual.z.to_bits());
}

fn ordinary_contact_maintenance_always_runs_final_placement(two_spheres: bool) {
    let mut t = make_grounded_transition(two_spheres);
    let support_walk_interp: f32 = 0.375;
    let support_passes = Arc::new(SyncCell::new(0));
    let placement_passes = Arc::new(SyncCell::new(0));
    let placement_bits = Arc::new(SyncCell::new(0u32));
    let mut engine = PhysicsEngine::new();
    {
        let (sp, pp, pb) = (
            support_passes.clone(),
            placement_passes.clone(),
            placement_bits.clone(),
        );
        engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
            if phase != TransitionCellCollisionPhase::Environment {
                return actual;
            }
            if c.sphere_path.step_down {
                sp.set(sp.get() + 1);
                c.sphere_path.walk.walk_interp = support_walk_interp;
                c.collision_info
                    .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
            } else if c.sphere_path.insert_type == InsertType::Placement {
                pp.set(pp.get() + 1);
                pb.set(c.sphere_path.walk.walk_interp.to_bits());
            }
            actual
        })));
    }
    let result = t.transitional_insert(1, &engine);
    assert_eq!(result, TransitionState::Ok);
    assert_eq!(support_passes.get(), 1);
    assert_eq!(placement_passes.get(), 1);
    assert_eq!(placement_bits.get(), support_walk_interp.to_bits());
    assert_eq!(
        t.sphere_path.walk.walk_interp.to_bits(),
        support_walk_interp.to_bits()
    );
    assert_eq!(t.sphere_path.insert_type, InsertType::Transition);
    assert!(!t.sphere_path.step_down);
}

#[test]
fn ordinary_contact_maintenance_always_runs_final_placement_one_sphere() {
    ordinary_contact_maintenance_always_runs_final_placement(false);
}

#[test]
fn ordinary_contact_maintenance_always_runs_final_placement_two_spheres() {
    ordinary_contact_maintenance_always_runs_final_placement(true);
}

#[test]
fn check_walkable_failed_nested_probe_preserves_outer_backup_for_edge_slide() {
    let mut t = make_grounded_transition(true);
    let outer_backup = Vec3::new(91.125, -17.25, 333.5);
    let outer_backup_cell = 0xA9B4_0077;
    let nested_origin = Vec3::new(2.125, 3.25, 4.5);
    t.sphere_path.set_check_pos(nested_origin, CELL);
    t.sphere_path.set_walkable(
        Plane::new(Vec3::Z, 0.0),
        &[
            Vec3::new(100.0, 100.0, 0.0),
            Vec3::new(101.0, 100.0, 0.0),
            Vec3::new(101.0, 101.0, 0.0),
            Vec3::new(100.0, 101.0, 0.0),
        ],
        Vec3::Z,
    );
    t.sphere_path.backup_check_pos = outer_backup;
    t.sphere_path.backup_check_cell_id = outer_backup_cell;

    let nested_probes = Arc::new(SyncCell::new(0));
    let mut engine = PhysicsEngine::new();
    {
        let np = nested_probes.clone();
        engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
            if phase == TransitionCellCollisionPhase::Environment && c.sphere_path.check_walkable {
                np.set(np.get() + 1);
            }
            actual
        })));
    }

    let walkable = t.do_check_walkable(FLOOR_Z, &engine);
    assert!(!walkable);
    assert!(nested_probes.get() > 0);
    assert_vector_bits(nested_origin, t.sphere_path.check_pos);
    assert_eq!(t.sphere_path.check_cell_id, CELL);
    assert_vector_bits(outer_backup, t.sphere_path.backup_check_pos);
    assert_eq!(t.sphere_path.backup_check_cell_id, outer_backup_cell);

    t.object_info.state.remove(ObjectInfoState::EDGE_SLIDE);
    t.sphere_path
        .set_check_pos(Vec3::new(-8.0, -9.0, -10.0), CELL);
    let (stop, state) = t.edge_slide_after_step_down_failed(&engine, 0.04, FLOOR_Z);
    assert!(stop);
    assert_eq!(state, TransitionState::Ok);
    assert_vector_bits(outer_backup, t.sphere_path.check_pos);
    assert_eq!(t.sphere_path.check_cell_id, outer_backup_cell);
}

#[test]
fn supported_candidate_overlapping_during_placement_is_rejected() {
    let mut t = make_grounded_transition(true);
    let placement_passes = Arc::new(SyncCell::new(0));
    let mut engine = PhysicsEngine::new();
    {
        let pp = placement_passes.clone();
        engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
            if phase != TransitionCellCollisionPhase::Environment {
                return actual;
            }
            if c.sphere_path.step_down {
                c.collision_info
                    .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
                return actual;
            }
            if c.sphere_path.insert_type == InsertType::Placement {
                pp.set(pp.get() + 1);
                return TransitionState::Collided;
            }
            actual
        })));
    }
    let accepted = t.do_step_down(0.04, FLOOR_Z, &engine);
    assert!(!accepted);
    assert_eq!(placement_passes.get(), 1);
    assert_eq!(t.sphere_path.insert_type, InsertType::Transition);
    assert!(!t.sphere_path.step_down);
}

#[test]
fn step_up_uses_the_same_final_placement_pass() {
    let mut t = make_grounded_transition(true);
    let placement_passes = Arc::new(SyncCell::new(0));
    let mut engine = PhysicsEngine::new();
    {
        let pp = placement_passes.clone();
        engine.set_cell_collision_hook(Some(Box::new(move |c, phase, _, actual| {
            if phase != TransitionCellCollisionPhase::Environment {
                return actual;
            }
            if c.sphere_path.step_down {
                c.collision_info
                    .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
            } else if c.sphere_path.insert_type == InsertType::Placement {
                pp.set(pp.get() + 1);
            }
            actual
        })));
    }
    let accepted = t.do_step_up(Vec3::X, &engine);
    assert!(accepted);
    assert_eq!(placement_passes.get(), 1);
    assert_eq!(t.sphere_path.insert_type, InsertType::Transition);
    assert!(!t.sphere_path.step_up);
    assert!(!t.sphere_path.step_down);
}

fn run_placement(center_y: f32, two_spheres: bool) -> TransitionState {
    let wall = ResolvedPolygon::new(
        1,
        vec![
            Vec3::new(-2.0, 0.0, -2.0),
            Vec3::new(-2.0, 0.0, 2.0),
            Vec3::new(2.0, 0.0, 2.0),
            Vec3::new(2.0, 0.0, -2.0),
        ],
        Plane::new(Vec3::Y, 0.0),
    );
    let bsp = single_leaf(Vec3::ZERO, 4.0, vec![wall]);
    let foot = Sphere::new(Vec3::new(0.0, center_y, 0.0), RADIUS);
    let head = two_spheres.then(|| Sphere::new(Vec3::new(0.0, center_y, 0.875), RADIUS));
    let mut t = Transition::new();
    t.sphere_path.init_path(
        Vec3::ZERO,
        Vec3::ZERO,
        CELL,
        RADIUS,
        if two_spheres { 1.355 } else { 0.0 },
        None,
        None,
        None,
    );
    t.sphere_path.insert_type = InsertType::Placement;
    bsp_find(
        &bsp,
        &mut t,
        foot,
        head,
        foot.center,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    )
}

fn placement_dispatcher_allows_exact_wall_tangency_and_rejects_overlap(two_spheres: bool) {
    assert_eq!(run_placement(RADIUS, two_spheres), TransitionState::Ok);
    assert_eq!(
        run_placement(RADIUS - EPSILON * 2.0, two_spheres),
        TransitionState::Collided
    );
}

#[test]
fn placement_dispatcher_allows_exact_wall_tangency_and_rejects_overlap_one_sphere() {
    placement_dispatcher_allows_exact_wall_tangency_and_rejects_overlap(false);
}

#[test]
fn placement_dispatcher_allows_exact_wall_tangency_and_rejects_overlap_two_spheres() {
    placement_dispatcher_allows_exact_wall_tangency_and_rejects_overlap(true);
}
