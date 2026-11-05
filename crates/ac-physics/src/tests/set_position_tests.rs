//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/PhysicsSetPositionTests.cs`.
//!
//! `EleventhNestedSetPosition_ReturnsGeneralFailureWithoutThrowing` is not
//! ported: it re-enters `SetPosition` from the collision hook, which the
//! borrow rules forbid here (the hook cannot reach the engine that is
//! placing), so the nesting it guards against cannot happen.

use std::collections::VecDeque;
use std::sync::Arc;

use ac_core::physics::adjust_to_outside;
use glam::{Quat, Vec3};

use super::support::*;
use crate::PhysicsState;
use crate::bsp::{CellBsp, CellBspNode};
use crate::cell_array::CellArray;
use crate::cell_transit::{
    CellCollector, build_cell_set_and_pick_containing, check_building_transit,
};
use crate::engine::{PhysicsEngine, accept_no_slide_placement};
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::set_position::{
    PhysicsPlacementClass, PhysicsResidenceDisposition, PhysicsSetPositionCollisionReport,
    PhysicsSetPositionError, PhysicsSetPositionFlags, PhysicsSetPositionRequest,
    PhysicsSetPositionResult,
};
use crate::shadow_registry::PhysicsShadowCommitAction;
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::{
    CollisionSphere, Transition, TransitionCellCollisionPhase, get_step_down_probe_plan,
};
use crate::world::{BldPortalInfo, BuildingPhysics, CellPhysics, CollisionWorld, Placement};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const P10: Vec3 = Vec3::new(10.0, 10.0, 10.0);

fn flags(bits: u32) -> PhysicsSetPositionFlags {
    PhysicsSetPositionFlags(bits)
}

fn request(cell_id: u32, local: Vec3, position: Vec3) -> PhysicsSetPositionRequest {
    PhysicsSetPositionRequest {
        position,
        orientation: Quat::from_axis_angle(Vec3::Z, 0.75),
        cell_id,
        cell_local_position: local,
        scale: 1.0,
        step_up_height: 0.4,
        step_down_height: 0.4,
        flags: flags(PhysicsSetPositionFlags::PLACEMENT | PhysicsSetPositionFlags::SLIDE),
        ..PhysicsSetPositionRequest::default()
    }
}

fn request10() -> PhysicsSetPositionRequest {
    request(CELL, P10, P10)
}

fn zero_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None)
}

fn add_flat(engine: &mut PhysicsEngine, landblock: u32, x: f32, y: f32) {
    engine.add_landblock(landblock, zero_terrain(), Vec::new(), Vec::new(), x, y);
}

fn flat_engine(with_world: bool) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    if with_world {
        engine.attach_world(CollisionWorld::new());
    }
    add_flat(&mut engine, LANDBLOCK, 0.0, 0.0);
    engine
}

fn place(engine: &mut PhysicsEngine, r: &PhysicsSetPositionRequest) -> PhysicsSetPositionResult {
    engine.set_position(r, None)
}

/// Counts every hook call (or only environment ones) and passes the state
/// through.
fn count_passes(engine: &mut PhysicsEngine, environment_only: bool) -> Arc<SyncCell<i32>> {
    let passes = Arc::new(SyncCell::new(0));
    let p = passes.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |_, phase, _, observed| {
        if !environment_only || phase == TransitionCellCollisionPhase::Environment {
            p.set(p.get() + 1);
        }
        observed
    })));
    passes
}

fn queued_random(engine: &mut PhysicsEngine, values: &[f64]) -> Arc<SyncCell<i32>> {
    let queue = Arc::new(SyncRefCell::new(
        values.iter().copied().collect::<VecDeque<f64>>(),
    ));
    let draws = Arc::new(SyncCell::new(0));
    let d = draws.clone();
    engine.set_position_random(Box::new(move || {
        d.set(d.get() + 1);
        queue.borrow_mut().pop_front().expect("random value")
    }));
    draws
}

fn containment_cell(id: u32, plane: Plane, visible: &[u32]) -> CellPhysics {
    let mut cell = cell_with_bsp(
        id,
        single_leaf(Vec3::ZERO, 0.0, Vec::new()),
        Placement::default(),
    );
    cell.containment_bsp = CellBsp::new(vec![
        CellBspNode::Split {
            plane,
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap();
    cell.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    for v in visible {
        cell.visible_cells.add(*v);
    }
    cell
}

fn index_of_landblock(cells: &[u32], landblock: u32) -> Option<usize> {
    cells
        .iter()
        .position(|c| c & 0xFFFF_0000 == landblock & 0xFFFF_0000)
}

#[test]
fn set_position_error_values_match_retail_header() {
    assert_eq!(PhysicsSetPositionError::Ok as i32, 0);
    assert_eq!(PhysicsSetPositionError::GeneralFailure as i32, 1);
    assert_eq!(PhysicsSetPositionError::NoValidPosition as i32, 2);
    assert_eq!(PhysicsSetPositionError::NoCell as i32, 3);
    assert_eq!(PhysicsSetPositionError::Collided as i32, 4);
    assert_eq!(PhysicsSetPositionError::InvalidArguments as i32, 0x100);
}

#[test]
fn missing_outdoor_cell_adjusts_frame_then_defers_with_ok() {
    let mut engine = PhysicsEngine::new();
    let local = Vec3::new(193.0, 12.0, 7.0);
    let mut expected_cell = CELL;
    let mut expected_local = local.to_array();
    assert!(adjust_to_outside(&mut expected_cell, &mut expected_local));

    let result = place(&mut engine, &request(CELL, local, local));
    assert_eq!(result.error, PhysicsSetPositionError::Ok);
    assert_eq!(result.residence, PhysicsResidenceDisposition::DeferredCell);
    assert_eq!(result.cell_id, expected_cell);
    assert_eq!(result.cell_local_position, Vec3::from_array(expected_local));
    assert_eq!(result.position, local);
    assert_eq!(result.queried_cell_ids, vec![CELL, expected_cell]);
}

#[test]
fn resident_cross_landblock_placement_rebases_local_frame() {
    let mut engine = flat_engine(true);
    add_flat(&mut engine, 0xAAB4_0000, 192.0, 0.0);
    let world = Vec3::new(193.0, 12.0, 7.0);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            ..request(CELL, world, world)
        },
    );
    assert!(result.is_committed(), "{result:?}");
    assert_eq!(result.position, world);
    assert_eq!(result.cell_id, 0xAAB4_0001);
    assert_eq!(result.cell_local_position, Vec3::new(1.0, 12.0, 7.0));
}

#[test]
fn outdoor_map_edge_failure_zeroes_cell_and_defers_exact_frame() {
    let mut engine = PhysicsEngine::new();
    let local = Vec3::new(-1.0, 12.0, 7.0);
    let result = place(&mut engine, &request(0x0000_0001, local, local));
    assert!(result.is_deferred());
    assert_eq!(result.cell_id, 0);
    assert_eq!(result.cell_local_position, local);
    assert_eq!(result.position, local);
    assert_eq!(result.queried_cell_ids, vec![0x0000_0001, 0]);
}

#[test]
fn query_footprint_includes_adjust_position_visible_child_probes() {
    const START: u32 = LANDBLOCK | 0x0101;
    const SIBLING: u32 = LANDBLOCK | 0x0102;
    let mut world = CollisionWorld::new();
    world.insert_cell(containment_cell(
        START,
        Plane::new(-Vec3::Y, 3.0),
        &[SIBLING],
    ));
    world.insert_cell(containment_cell(SIBLING, Plane::new(Vec3::Y, -7.0), &[]));
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    let at = Vec3::new(0.0, 8.0, 1.0);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            ..request(START, at, at)
        },
    );
    let q = &result.queried_cell_ids;
    let start = q.iter().position(|c| *c == START).expect("start probed");
    let sibling = q
        .iter()
        .position(|c| *c == SIBLING)
        .expect("sibling probed");
    assert!(start < sibling);
}

#[test]
fn lateral_visible_child_recovery_records_rejected_and_winning_siblings_footprint_only() {
    const START: u32 = LANDBLOCK | 0x0101;
    const REJECTED: u32 = LANDBLOCK | 0x0102;
    const WINNER: u32 = LANDBLOCK | 0x0103;
    let mut world = CollisionWorld::new();
    world.insert_cell(containment_cell(
        START,
        Plane::new(-Vec3::Y, 3.0),
        &[REJECTED, WINNER],
    ));
    world.insert_cell(containment_cell(REJECTED, Plane::new(Vec3::Y, -20.0), &[]));
    world.insert_cell(containment_cell(WINNER, Plane::new(Vec3::Y, -7.0), &[]));
    let mut cells = CellArray::new();
    let mut footprint = CellArray::new();
    let (containing, _) = {
        let mut collector = CellCollector::with_footprint(&mut cells, Some(&mut footprint));
        build_cell_set_and_pick_containing(
            &world,
            &[Sphere::new(Vec3::new(0.0, 8.0, 1.0), 0.48)],
            1,
            START,
            None,
            &mut collector,
        )
    };
    assert_eq!(containing, WINNER);
    assert_eq!(cells.ordered_ids(), &[START]);
    assert_eq!(footprint.ordered_ids(), &[START, REJECTED, WINNER]);
}

#[test]
fn rejected_portal_containment_probe_is_recorded_footprint_only() {
    const START: u32 = LANDBLOCK | 0x0101;
    const REJECTED: u32 = LANDBLOCK | 0x0102;
    let mut start = cell_with_bsp(
        START,
        single_leaf(Vec3::ZERO, 0.0, Vec::new()),
        Placement::default(),
    );
    start.containment_bsp = CellBsp::new(vec![CellBspNode::Leaf]).unwrap();
    start.portals = vec![PortalInfo::new((REJECTED & 0xFFFF) as u16, 10, 0)];
    start.portal_polygons.insert(
        10,
        ResolvedPolygon::new(
            10,
            vec![
                Vec3::new(10.0, -1.0, 0.0),
                Vec3::new(10.0, 1.0, 0.0),
                Vec3::new(10.0, 1.0, 2.0),
            ],
            Plane::new(Vec3::X, -10.0),
        ),
    );
    let mut world = CollisionWorld::new();
    world.insert_cell(start);
    world.insert_cell(containment_cell(REJECTED, Plane::new(Vec3::X, -100.0), &[]));
    let mut cells = CellArray::new();
    let mut footprint = CellArray::new();
    let (containing, _) = {
        let mut collector = CellCollector::with_footprint(&mut cells, Some(&mut footprint));
        build_cell_set_and_pick_containing(
            &world,
            &[Sphere::new(Vec3::ZERO, 0.48)],
            1,
            START,
            None,
            &mut collector,
        )
    };
    assert_eq!(containing, START);
    assert_eq!(cells.ordered_ids(), &[START]);
    assert_eq!(footprint.ordered_ids(), &[START, REJECTED]);
}

#[test]
fn rejected_building_containment_probe_is_recorded_footprint_only() {
    const REJECTED: u32 = LANDBLOCK | 0x0102;
    let building = BuildingPhysics {
        portals: vec![BldPortalInfo {
            other_cell_id: REJECTED,
            other_portal_id: 0,
            flags: 0,
        }],
        ..BuildingPhysics::default()
    };
    let mut cells = CellArray::new();
    let mut footprint = CellArray::new();
    {
        let mut collector = CellCollector::with_footprint(&mut cells, Some(&mut footprint));
        check_building_transit(
            &CollisionWorld::new(),
            &building,
            &[Sphere::new(Vec3::ZERO, 0.48)],
            1,
            &mut collector,
        );
    }
    assert!(cells.is_empty());
    assert_eq!(footprint.ordered_ids(), &[REJECTED]);
}

#[test]
fn outdoor_block_sentinel_is_an_indoor_shaped_lost_cell_sentinel() {
    let mut engine = PhysicsEngine::new();
    let local = Vec3::new(25.0, 49.0, 7.0);
    let result = place(&mut engine, &request(LANDBLOCK | 0xFFFF, local, local));
    assert!(result.is_deferred());
    assert_eq!(result.cell_id, LANDBLOCK | 0xFFFF);
    assert_eq!(result.cell_local_position, local);
}

#[test]
fn invalid_low_cell_id_parks_without_rewriting_frame() {
    let mut engine = PhysicsEngine::new();
    let local = Vec3::new(11.0, 13.0, 17.0);
    let result = place(&mut engine, &request(LANDBLOCK | 0x0050, local, local));
    assert!(result.is_deferred());
    assert_eq!(result.error, PhysicsSetPositionError::Ok);
    assert_eq!(result.cell_id, LANDBLOCK | 0x0050);
    assert_eq!(result.cell_local_position, local);
}

#[test]
fn missing_indoor_cell_parks_exact_cell_and_frame() {
    let mut engine = PhysicsEngine::new();
    let local = Vec3::new(31.0, -4.0, 9.0);
    let result = place(&mut engine, &request(LANDBLOCK | 0x0107, local, local));
    assert!(result.is_deferred());
    assert_eq!(result.cell_id, LANDBLOCK | 0x0107);
    assert_eq!(result.cell_local_position, local);
}

#[test]
fn empty_sphere_list_uses_retail_dummy_at_scale_one() {
    let mut engine = flat_engine(true);
    let captured = Arc::new(SyncRefCell::new((Vec3::ZERO, 0.0f32)));
    let c = captured.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, observed| {
        if phase == TransitionCellCollisionPhase::Environment {
            let s = t.sphere_path.local_sphere[0];
            *c.borrow_mut() = (s.center, s.radius);
        }
        observed
    })));
    place(
        &mut engine,
        &PhysicsSetPositionRequest {
            scale: -7.0,
            ..request10()
        },
    );
    assert_eq!(*captured.borrow(), (Vec3::new(0.0, 0.0, 0.1), 0.1));
}

#[test]
fn authored_sphere_list_caps_at_two_and_preserves_non_positive_scale() {
    let mut engine = flat_engine(true);
    let captured = Arc::new(SyncRefCell::new((0usize, [Sphere::default(); 2])));
    let c = captured.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, _| {
        if phase == TransitionCellCollisionPhase::Environment {
            *c.borrow_mut() = (t.sphere_path.num_sphere, t.sphere_path.local_sphere);
        }
        TransitionState::Collided
    })));
    place(
        &mut engine,
        &PhysicsSetPositionRequest {
            spheres: vec![
                CollisionSphere::new(Vec3::new(1.0, 2.0, 3.0), 4.0),
                CollisionSphere::new(Vec3::new(5.0, 6.0, 7.0), 8.0),
                CollisionSphere::new(Vec3::splat(9.0), 10.0),
            ],
            scale: -2.0,
            ..request10()
        },
    );
    let (count, spheres) = *captured.borrow();
    assert_eq!(count, 2);
    assert_eq!(spheres[0].center, Vec3::new(-2.0, -4.0, -6.0));
    assert_eq!(spheres[0].radius, -8.0);
    assert_eq!(spheres[1].center, Vec3::new(-10.0, -12.0, -14.0));
    assert_eq!(spheres[1].radius, -16.0);
}

/// Places `request` and returns what `read` saw on the transition at the
/// first environment pass.
fn observe<T: Copy + Send + 'static>(
    request: &PhysicsSetPositionRequest,
    read: impl Fn(&Transition) -> T + Send + 'static,
) -> Option<T> {
    let mut engine = flat_engine(true);
    let captured: Arc<SyncCell<Option<T>>> = Arc::new(SyncCell::new(None));
    let c = captured.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, observed| {
        if phase == TransitionCellCollisionPhase::Environment && c.get().is_none() {
            c.set(Some(read(t)));
        }
        observed
    })));
    place(&mut engine, request);
    captured.get()
}

#[test]
fn step_down_is_disabled_only_for_missiles() {
    for (missile, expected) in [(false, true), (true, false)] {
        let state = if missile {
            PhysicsState::MISSILE
        } else {
            PhysicsState::GRAVITY
        };
        let seen = observe(
            &PhysicsSetPositionRequest {
                mover_physics_state: state,
                ..request10()
            },
            |t| t.object_info.step_down,
        );
        assert_eq!(seen, Some(expected), "missile {missile}");
    }
}

#[test]
fn object_info_ethereal_is_seeded_from_physics_state() {
    for (ethereal, expected) in [(false, false), (true, true)] {
        let state = if ethereal {
            PhysicsState::ETHEREAL
        } else {
            PhysicsState::GRAVITY
        };
        let seen = observe(
            &PhysicsSetPositionRequest {
                mover_physics_state: state,
                ..request10()
            },
            |t| t.object_info.ethereal,
        );
        assert_eq!(seen, Some(expected));
    }
}

#[test]
fn path_clipped_comes_only_from_explicit_object_info_flags() {
    for (explicit, expected) in [(false, false), (true, true)] {
        let bits = if explicit {
            ObjectInfoState::PATH_CLIPPED
        } else {
            ObjectInfoState::NONE
        };
        let seen = observe(
            &PhysicsSetPositionRequest {
                mover_physics_state: PhysicsState::MISSILE,
                mover_flags: ObjectInfoState(bits),
                ..request10()
            },
            |t| t.object_info.path_clipped(),
        );
        assert_eq!(seen, Some(expected));
    }
}

#[test]
fn do_not_create_cells_missing_destination_still_defers() {
    let mut engine = PhysicsEngine::new();
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            flags: flags(
                PhysicsSetPositionFlags::PLACEMENT
                    | PhysicsSetPositionFlags::SLIDE
                    | PhysicsSetPositionFlags::DO_NOT_CREATE_CELLS,
            ),
            ..request10()
        },
    );
    assert!(result.is_deferred());
    assert_eq!(engine.landblock_count(), 0);
}

#[test]
fn successful_placement_retains_requested_orientation() {
    let mut engine = flat_engine(true);
    let r = request10();
    let result = place(&mut engine, &r);
    assert!(result.is_committed());
    assert_eq!(result.orientation, r.orientation);
}

#[test]
fn outer_placement_starts_with_initial_placement_insert() {
    assert_eq!(
        observe(&request10(), |t| t.sphere_path.insert_type),
        Some(InsertType::InitialPlacement)
    );
}

#[test]
fn step_down_probe_plan_preserves_retail_equality_boundaries() {
    assert_eq!(get_step_down_probe_plan(1, 0.5, 1.0), (0.25, 1));
    assert_eq!(get_step_down_probe_plan(2, 0.5, 1.0), (0.5, 2));
    assert_eq!(get_step_down_probe_plan(1, 0.5, 0.4), (0.4, 1));
}

#[test]
fn no_slide_acceptance_is_signed_xy_same_cell_and_ignores_z() {
    for (dx, dy, dz, cell, expected) in [
        (-100.0, 0.0, 7.0, 7, true),
        // OpenAC writes 0.0500000007, the f32 nearest 0.05.
        (0.05, 0.05, 500.0, 7, true),
        (0.0501, 0.0, 0.0, 7, false),
        (0.0, 0.0501, 0.0, 7, false),
        (0.0, 0.0, 0.0, 8, false),
    ] {
        assert_eq!(
            accept_no_slide_placement(Vec3::new(dx, dy, dz), Vec3::ZERO, cell, 7),
            expected,
            "({dx}, {dy}, {dz}) cell {cell}"
        );
    }
}

#[test]
fn placement_compass_late_sample_matches_retail_float_bits() {
    let mut engine = flat_engine(true);
    let last_check = Arc::new(SyncCell::new(Vec3::ZERO));
    let l = last_check.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, _| {
        if phase == TransitionCellCollisionPhase::Environment {
            l.set(t.sphere_path.check_pos);
        }
        TransitionState::Collided
    })));
    let mut transition = Transition::new();
    transition
        .sphere_path
        .init_path_simple(Vec3::ZERO, Vec3::ZERO, CELL, 0.48);
    transition.sphere_path.insert_type = InsertType::Placement;
    transition.sphere_path.placement_allows_sliding = true;
    assert!(!transition.find_placement_pos(&engine));
    assert_eq!(last_check.get().x.to_bits(), 0xBEED_C24F);
    assert_eq!(last_check.get().y.to_bits(), 0x407E_44DE);
}

fn placement_transition() -> Transition {
    let mut t = Transition::new();
    t.sphere_path.init_path_simple(P10, P10, CELL, 0.48);
    t.sphere_path.insert_type = InsertType::Placement;
    t
}

#[test]
fn inner_placement_validator_resets_collision_info_for_every_failure() {
    for state in [
        TransitionState::Collided,
        TransitionState::Adjusted,
        TransitionState::Slid,
    ] {
        let mut t = placement_transition();
        t.sphere_path.placement_allows_sliding = true;
        t.collision_info
            .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
        t.collision_info.set_sliding_normal(Vec3::X);
        assert_eq!(t.validate_placement_transition(state), state);
        assert!(!t.collision_info.contact_plane_valid);
        assert!(!t.collision_info.sliding_normal_valid);
    }
}

#[test]
fn inner_placement_validator_does_not_reset_when_sliding_disabled() {
    let mut t = placement_transition();
    t.sphere_path.placement_allows_sliding = false;
    t.collision_info
        .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
    t.validate_placement_transition(TransitionState::Collided);
    assert!(t.collision_info.contact_plane_valid);
}

#[test]
fn outer_placement_validator_collided_never_retries_or_resets() {
    let mut engine = flat_engine(true);
    let passes = count_passes(&mut engine, false);
    let mut t = placement_transition();
    t.collision_info
        .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
    assert_eq!(
        t.validate_placement(&engine, TransitionState::Collided, true),
        TransitionState::Collided
    );
    assert_eq!(passes.get(), 0);
    assert!(t.collision_info.contact_plane_valid);
}

#[test]
fn outer_placement_validator_adjusted_and_slid_retry_exactly_once() {
    for state in [TransitionState::Adjusted, TransitionState::Slid] {
        let mut engine = flat_engine(true);
        let passes = Arc::new(SyncCell::new(0));
        let p = passes.clone();
        engine.set_cell_collision_hook(Some(Box::new(move |_, phase, _, _| {
            if phase == TransitionCellCollisionPhase::Environment {
                p.set(p.get() + 1);
            }
            TransitionState::Ok
        })));
        let mut t = placement_transition();
        assert_eq!(
            t.validate_placement(&engine, state, true),
            TransitionState::Ok
        );
        assert_eq!(passes.get(), 1);
    }
}

#[test]
fn outer_placement_validator_retry_false_returns_original_state() {
    for state in [TransitionState::Adjusted, TransitionState::Slid] {
        let mut engine = flat_engine(true);
        let passes = count_passes(&mut engine, false);
        let mut t = placement_transition();
        assert_eq!(t.validate_placement(&engine, state, false), state);
        assert_eq!(passes.get(), 0);
    }
}

#[test]
fn failed_check_maps_collision_handler_result_to_retail_error() {
    for (handled, expected) in [
        (false, PhysicsSetPositionError::NoValidPosition),
        (true, PhysicsSetPositionError::Collided),
    ] {
        let mut engine = flat_engine(true);
        engine.set_cell_collision_hook(Some(Box::new(|t, phase, _, _| {
            if phase == TransitionCellCollisionPhase::Environment {
                let ci = &mut t.collision_info;
                ci.collided_with_environment = true;
                ci.init_contact_plane(Plane::new(Vec3::Z, -3.0), CELL, true);
                ci.set_sliding_normal(Vec3::X);
                ci.set_collision_normal(-Vec3::Y);
                ci.frames_stationary_fall = 2;
                ci.adjust_offset = Vec3::new(1.0, 2.0, 3.0);
                ci.collide_object_guids.push(0x1234_5678);
                ci.last_collided_object_guid = Some(0x1234_5678);
            }
            TransitionState::Collided
        })));
        let mut observed = PhysicsSetPositionCollisionReport::default();
        let mut handler = |report: &PhysicsSetPositionCollisionReport| {
            observed = report.clone();
            handled
        };
        let result = engine.set_position(&request10(), Some(&mut handler));

        assert_eq!(result.error, expected);
        assert_eq!(result.collision_handler_result, handled);
        assert!(observed.collided_with_environment);
        assert!(observed.contact_plane_valid);
        assert_eq!(observed.contact_plane, Plane::new(Vec3::Z, -3.0));
        assert_eq!(observed.contact_plane_cell_id, CELL);
        assert!(observed.contact_plane_is_water);
        assert!(observed.last_known_contact_plane_valid);
        assert_eq!(observed.last_known_contact_plane, observed.contact_plane);
        assert_eq!(observed.last_known_contact_plane_cell_id, CELL);
        assert!(observed.last_known_contact_plane_is_water);
        assert!(observed.sliding_normal_valid);
        assert_eq!(observed.sliding_normal, Vec3::X);
        assert!(observed.collision_normal_valid);
        assert_eq!(observed.collision_normal, -Vec3::Y);
        assert_eq!(observed.frames_stationary_fall, 2);
        assert_eq!(observed.adjust_offset, Vec3::new(1.0, 2.0, 3.0));
        assert_eq!(observed.last_collided_object_id, Some(0x1234_5678));
        assert_eq!(observed.collided_object_ids, vec![0x1234_5678]);
        assert!(result.collided_with_environment);
        assert!(result.in_contact);
        assert!(result.on_walkable);
        assert!(result.contact_plane_is_water);
        assert!(result.sliding_normal_valid);
        assert!(result.collision_normal_valid);
        assert_eq!(result.frames_stationary_fall, 2);
        assert_eq!(result.collided_object_ids, vec![0x1234_5678]);
        assert_eq!(result.residence, PhysicsResidenceDisposition::Unchanged);
    }
}

#[test]
fn retail_force_classes_bypass_placement_collision() {
    for class in [
        PhysicsPlacementClass::Hook,
        PhysicsPlacementClass::Storage,
        PhysicsPlacementClass::Corpse,
    ] {
        let mut engine = flat_engine(true);
        let passes = count_passes(&mut engine, false);
        let result = place(
            &mut engine,
            &PhysicsSetPositionRequest {
                placement_class: class,
                ..request10()
            },
        );
        assert!(result.is_committed());
        assert_eq!(passes.get(), 0);
        assert!(result.cell_changed);
        assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Recalculate);
        assert!(result.cross_cell_ids.is_empty());
    }
}

#[test]
fn ordinary_object_cannot_request_force_by_flags() {
    let mut engine = flat_engine(true);
    let passes = count_passes(&mut engine, false);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            placement_class: PhysicsPlacementClass::Ordinary,
            flags: flags(
                !(PhysicsSetPositionFlags::RANDOM_SCATTER | PhysicsSetPositionFlags::SCATTER),
            ),
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert!(passes.get() > 0);
}

#[test]
fn force_into_same_cell_sets_frame_without_recalculating() {
    let mut engine = flat_engine(true);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            placement_class: PhysicsPlacementClass::Corpse,
            current_cell_id: Some(CELL),
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert!(!result.cell_changed);
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::None);
    assert!(result.cross_cell_ids.is_empty());
}

#[test]
fn force_into_changed_cell_requests_canonical_recalculation() {
    let mut engine = flat_engine(true);
    let at = Vec3::new(23.8, 12.0, 2.5);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            spheres: vec![CollisionSphere::new(Vec3::ZERO, 0.5)],
            placement_class: PhysicsPlacementClass::Hook,
            current_cell_id: Some(LANDBLOCK | 0x0040),
            ..request(CELL, at, at)
        },
    );
    assert!(result.is_committed());
    assert!(result.cell_changed);
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Recalculate);
    assert!(result.cross_cell_ids.is_empty());
}

#[test]
fn force_into_retained_cell_with_no_current_cell_refloods() {
    let mut engine = flat_engine(true);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            placement_class: PhysicsPlacementClass::Corpse,
            current_cell_id: None,
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert!(result.cell_changed);
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Recalculate);
}

#[test]
fn ordinary_physics_bsp_placement_requests_canonical_recalculation() {
    let mut engine = flat_engine(true);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::HAS_PHYSICS_BSP | PhysicsState::MISSILE,
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Recalculate);
    assert!(result.cross_cell_ids.is_empty());
}

#[test]
fn ordinary_sphere_placement_replaces_shadows_with_transition_cells() {
    let mut engine = flat_engine(true);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Replace);
    assert!(result.cross_cell_ids.contains(&CELL));
}

#[test]
fn ordinary_sphere_placement_with_no_transition_cells_preserves_shadows() {
    let mut engine = flat_engine(false);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            ..request10()
        },
    );
    assert!(result.is_committed());
    assert_eq!(result.shadow_action, PhysicsShadowCommitAction::Preserve);
    assert!(result.cross_cell_ids.is_empty());
}

#[test]
fn random_scatter_is_direct_and_stops_on_deferred_ok() {
    let mut engine = PhysicsEngine::new();
    let draws = queued_random(&mut engine, &[1.0, 0.0, 0.25, 0.75]);
    let at = Vec3::new(10.0, 10.0, 3.0);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            flags: flags(PhysicsSetPositionFlags::RANDOM_SCATTER),
            scatter_radius_x: 4.0,
            scatter_radius_y: 2.0,
            scatter_attempts: 2,
            ..request(CELL, at, at)
        },
    );
    assert!(result.is_deferred());
    assert_eq!(draws.get(), 2);
    assert_eq!(result.position, Vec3::new(14.0, 8.0, 3.0));
}

#[test]
fn scatter_fallback_runs_only_after_normal_error_and_uses_exact_attempts() {
    let mut engine = flat_engine(true);
    let passes = Arc::new(SyncCell::new(0));
    let p = passes.clone();
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, _| {
        if phase == TransitionCellCollisionPhase::Environment {
            p.set(p.get() + 1);
            t.collision_info.collided_with_environment = true;
        }
        TransitionState::Collided
    })));
    let draws = queued_random(&mut engine, &[0.0, 0.0, 1.0, 1.0]);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            flags: flags(PhysicsSetPositionFlags::PLACEMENT | PhysicsSetPositionFlags::SCATTER),
            scatter_radius_x: 1.0,
            scatter_radius_y: 1.0,
            scatter_attempts: 2,
            ..request10()
        },
    );
    assert_eq!(result.error, PhysicsSetPositionError::NoValidPosition);
    assert_eq!(passes.get(), 3);
    assert_eq!(draws.get(), 4);
    assert_eq!(result.position, Vec3::new(11.0, 11.0, 10.0));
}

/// Fails the first environment pass, then passes states through.
fn fail_first_environment_pass(engine: &mut PhysicsEngine) {
    let pass = Arc::new(SyncCell::new(0));
    engine.set_cell_collision_hook(Some(Box::new(move |_, phase, _, observed| {
        if phase == TransitionCellCollisionPhase::Environment {
            let n = pass.get();
            pass.set(n + 1);
            if n == 0 {
                return TransitionState::Collided;
            }
        }
        observed
    })));
}

#[test]
fn normal_then_scatter_unions_every_landblock_probe_in_stable_order() {
    let mut engine = flat_engine(true);
    add_flat(&mut engine, 0xAAB4_0000, 192.0, 0.0);
    fail_first_environment_pass(&mut engine);
    queued_random(&mut engine, &[1.0, 0.5]);
    let at = Vec3::new(180.0, 12.0, 7.0);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            flags: flags(PhysicsSetPositionFlags::PLACEMENT | PhysicsSetPositionFlags::SCATTER),
            scatter_radius_x: 20.0,
            scatter_attempts: 1,
            ..request(CELL, at, at)
        },
    );
    assert!(result.is_committed(), "{result:?}");
    let source = index_of_landblock(&result.queried_cell_ids, LANDBLOCK).unwrap();
    let east = index_of_landblock(&result.queried_cell_ids, 0xAAB4_0000).unwrap();
    assert!(east > source);
}

#[test]
fn multi_scatter_unions_failed_and_successful_attempt_landblocks() {
    let mut engine = flat_engine(true);
    add_flat(&mut engine, 0xA8B4_0000, -192.0, 0.0);
    add_flat(&mut engine, 0xAAB4_0000, 192.0, 0.0);
    fail_first_environment_pass(&mut engine);
    queued_random(&mut engine, &[0.0, 0.5, 1.0, 0.5]);
    let at = Vec3::new(10.0, 12.0, 7.0);
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            mover_physics_state: PhysicsState::MISSILE,
            flags: flags(PhysicsSetPositionFlags::RANDOM_SCATTER),
            scatter_radius_x: 200.0,
            scatter_attempts: 2,
            ..request(CELL, at, at)
        },
    );
    assert!(result.is_committed(), "{result:?}");
    let west = index_of_landblock(&result.queried_cell_ids, 0xA8B4_0000).unwrap();
    let east = index_of_landblock(&result.queried_cell_ids, 0xAAB4_0000).unwrap();
    assert!(east > west);
    assert!(
        !result
            .cross_cell_ids
            .iter()
            .any(|c| c & 0xFFFF_0000 == 0xA8B4_0000)
    );
    assert!(
        result
            .cross_cell_ids
            .iter()
            .any(|c| c & 0xFFFF_0000 == 0xAAB4_0000)
    );
}

#[test]
fn scatter_reuses_one_transition_and_failed_inner_probe_cannot_leak_into_success() {
    let mut engine = flat_engine(true);
    let placement_passes = Arc::new(SyncCell::new(0));
    let injected = Arc::new(SyncCell::new(false));
    let (pp, inj) = (placement_passes.clone(), injected.clone());
    engine.set_cell_collision_hook(Some(Box::new(move |t, phase, _, observed| {
        if phase != TransitionCellCollisionPhase::Environment {
            return observed;
        }
        if t.sphere_path.insert_type == InsertType::Placement {
            pp.set(pp.get() + 1);
            if !inj.get() {
                inj.set(true);
                t.collision_info.collided_with_environment = true;
                t.collision_info
                    .set_contact_plane(Plane::new(Vec3::Z, 0.0), CELL, false);
                t.collision_info.set_sliding_normal(Vec3::X);
                return TransitionState::Collided;
            }
        }
        TransitionState::Ok
    })));
    engine.set_position_random(Box::new(|| 0.5));
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            flags: flags(PhysicsSetPositionFlags::RANDOM_SCATTER | PhysicsSetPositionFlags::SLIDE),
            scatter_attempts: 2,
            mover_physics_state: PhysicsState::MISSILE,
            ..request10()
        },
    );
    assert!(result.is_committed(), "{result:?}");
    assert!(injected.get());
    assert!(placement_passes.get() >= 2);
    assert!(!result.in_contact);
    assert!(!result.sliding_normal_valid);
    assert!(!result.collided_with_environment);
    assert!(result.collided_object_ids.is_empty());
}

#[test]
fn zero_scatter_attempts_returns_general_failure_without_random_draw() {
    let mut engine = PhysicsEngine::new();
    let draws = Arc::new(SyncCell::new(0));
    let d = draws.clone();
    engine.set_position_random(Box::new(move || {
        d.set(d.get() + 1);
        0.5
    }));
    let result = place(
        &mut engine,
        &PhysicsSetPositionRequest {
            flags: flags(PhysicsSetPositionFlags::RANDOM_SCATTER),
            scatter_attempts: 0,
            ..request(CELL, Vec3::ONE, Vec3::ONE)
        },
    );
    assert_eq!(result.error, PhysicsSetPositionError::GeneralFailure);
    assert_eq!(result.residence, PhysicsResidenceDisposition::Unchanged);
    assert_eq!(draws.get(), 0);
}
