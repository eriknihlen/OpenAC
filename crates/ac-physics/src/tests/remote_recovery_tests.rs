//! A remote body's blip recovery and the server's velocity: OpenAC
//! `tests/AcDream.Runtime.Tests/Physics/RuntimeSetPositionStateTests.cs`
//! (the `InterpolationRecovery*` cases: the placement is a teleport that
//! may slide, a solid destination keeps the target, a committed placement
//! takes the queued pose and clears the queue) and the authoritative vector
//! commit of `RuntimePhysicsState.TryCommitAuthoritativeVector` with the
//! ordinary-vector airborne rule of OpenAC
//! `src/AcDream.App/Physics/LiveEntityNetworkUpdateController.cs`.
//!
//! OpenAC's recovery runs through its placement state machine (tokens,
//! projections, acknowledgements); here the caller places the body with the
//! engine's set-position and completes the recovery directly.

use glam::{Quat, Vec3};

use super::support::*;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::movement::{NoTargets, ObjectToken, RemoteMotionDriver};
use crate::set_position::PhysicsSetPositionFlags;
use crate::shadow::ShadowCollisionType;
use crate::terrain::TerrainSurface;
use crate::world::CollisionWorld;
use crate::{PhysicsState, TransientState};

const LB: u32 = 0xA9B4_0000;
const CELL: u32 = LB | 0x0001;

fn flat_engine() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        LB,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

fn origin_of(cell: u32) -> Option<Vec3> {
    (cell & 0xFFFF_0000 == LB).then_some(Vec3::ZERO)
}

fn remote(at: Vec3) -> RemoteMotionDriver {
    let token = ObjectToken {
        id: 0x7000_1F05,
        incarnation: 1,
    };
    let mut d = RemoteMotionDriver::new(token, CELL, at, Quat::IDENTITY);
    d.local_entity_id = 0x1F05;
    d.obj.radius = 0.48;
    d.obj.height = 1.835;
    d
}

/// Enqueue `target` more than the blip distance (1 m) away, which asks for
/// a recovery at once.
fn blip(d: &mut RemoteMotionDriver, target: Vec3, orientation: Quat) {
    let body = d.obj.body.position();
    let body_orientation = d.obj.body.orientation;
    d.interpolation.enqueue(
        target,
        orientation,
        false,
        Some(body),
        Some(body_orientation),
        CELL,
        1.0,
    );
}

fn recover(engine: &mut PhysicsEngine, d: &mut RemoteMotionDriver) -> bool {
    let Some((target, request)) = d.recovery_request(origin_of) else {
        return false;
    };
    let placed = engine.set_position(&request, None);
    d.complete_recovery(target, &placed)
}

#[test]
fn the_recovery_placement_is_a_teleport_that_may_slide_with_the_default_cylinder() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    let target = Vec3::new(12.1, 20.0, 7.0);
    blip(&mut d, target, Quat::IDENTITY);
    let (t, request) = d
        .recovery_request(origin_of)
        .expect("a blip asks for recovery");
    assert_eq!(t.position, target);
    assert_eq!(request.cell_id, CELL);
    assert_eq!(request.cell_local_position, target);
    assert_eq!(
        request.flags.0,
        PhysicsSetPositionFlags::TELEPORT
            | PhysicsSetPositionFlags::SLIDE
            | PhysicsSetPositionFlags::SEND_POSITION_EVENT
    );
    // No spheres of its own: two of the body's radius, at its foot and head.
    assert_eq!(request.spheres.len(), 2);
    assert_eq!(request.spheres[0].origin, Vec3::new(0.0, 0.0, 0.48));
    assert_eq!(request.spheres[1].origin, Vec3::new(0.0, 0.0, 1.835 - 0.48));
    assert_eq!(request.scale, 1.0);
}

#[test]
fn interpolation_recovery_retains_target_when_destination_is_solid() {
    let mut engine = flat_engine();
    register_shadow(
        &mut engine,
        0x8601,
        0,
        Vec3::new(12.1, 20.0, 7.0),
        Quat::IDENTITY,
        10.0,
        LB,
        ShadowCollisionType::Cylinder,
        10.0,
        1.0,
        PhysicsState::STATIC.bits(),
        EntityCollisionFlags(0),
        CELL,
        true,
    );
    let start = Vec3::new(1.0, 20.0, 7.0);
    let mut d = remote(start);
    let target = Vec3::new(12.1, 20.0, 7.0);
    blip(&mut d, target, Quat::IDENTITY);
    for _ in 0..2 {
        assert!(!recover(&mut engine, &mut d));
        assert_eq!(d.obj.body.position(), start);
        let pending = d.interpolation.try_get_recovery_target().unwrap();
        assert_eq!(pending.position, target);
    }
}

#[test]
fn interpolation_recovery_publishes_the_queued_pose_and_clears_the_queue() {
    let mut engine = flat_engine();
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    let target = Vec3::new(130.0, 20.0, 7.0);
    let target_cell = LB | 0x0029; // column 5 (x 120..144), row 0
    let orientation = Quat::from_axis_angle(Vec3::Z, 0.7);
    let body = d.obj.body.position();
    d.interpolation.enqueue(
        target,
        orientation,
        false,
        Some(body),
        Some(Quat::IDENTITY),
        target_cell,
        1.0,
    );
    assert!(recover(&mut engine, &mut d));
    assert_eq!(d.obj.body.position(), target);
    assert_eq!(d.obj.body.orientation, orientation);
    assert_eq!(d.obj.cell_id, target_cell);
    assert!(d.interpolation.try_get_recovery_target().is_none());
}

#[test]
fn interpolation_recovery_adjusts_near_an_obstruction() {
    let mut engine = flat_engine();
    register_shadow(
        &mut engine,
        0x8600,
        0,
        Vec3::new(12.0, 20.0, 7.0),
        Quat::IDENTITY,
        0.5,
        LB,
        ShadowCollisionType::Cylinder,
        3.0,
        1.0,
        PhysicsState::STATIC.bits(),
        EntityCollisionFlags(0),
        CELL,
        true,
    );
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    let penetrating = Vec3::new(12.6, 20.0, 7.0);
    blip(&mut d, penetrating, Quat::IDENTITY);
    assert!(recover(&mut engine, &mut d));
    let placed = d.obj.body.position();
    assert_ne!(placed, penetrating);
    let moved = placed.distance(penetrating);
    assert!((0.01..=0.7).contains(&moved), "slid {moved} m: {placed}");
    let gap = Vec3::new(placed.x - 12.0, placed.y - 20.0, 0.0).length();
    assert!(gap >= 0.5 + 0.479, "still overlaps the post: {placed}");
}

#[test]
fn a_stale_recovery_target_is_refused() {
    let mut engine = flat_engine();
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    blip(&mut d, Vec3::new(30.0, 20.0, 7.0), Quat::IDENTITY);
    let (target, request) = d.recovery_request(origin_of).unwrap();
    let placed = engine.set_position(&request, None);
    // A newer correction arrives before the placement is committed.
    blip(&mut d, Vec3::new(60.0, 20.0, 7.0), Quat::IDENTITY);
    assert!(!d.complete_recovery(target, &placed));
    assert_eq!(d.obj.body.position(), Vec3::new(1.0, 20.0, 7.0));
    assert_eq!(
        d.interpolation.try_get_recovery_target().unwrap().position,
        Vec3::new(60.0, 20.0, 7.0)
    );
}

#[test]
fn no_recovery_without_a_blip_or_outside_the_resident_world() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    assert!(d.recovery_request(origin_of).is_none());
    let body = d.obj.body.position();
    d.interpolation.enqueue(
        Vec3::new(30.0, 20.0, 7.0),
        Quat::IDENTITY,
        false,
        Some(body),
        Some(Quat::IDENTITY),
        0xAAB4_0001,
        1.0,
    );
    assert!(d.recovery_request(origin_of).is_none());
}

// ── the server's velocity ──────────────────────────────────────────────

#[test]
fn an_authoritative_vector_sets_velocity_and_spin_and_wakes_the_body() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    d.obj.body.state = PhysicsState::GRAVITY;
    d.obj.body.transient_state.remove(TransientState::ACTIVE);
    d.obj.clock.deactivate();
    assert!(d.apply_authoritative_vector(
        &NoTargets,
        Vec3::new(1.0, 2.0, 0.0),
        Vec3::new(0.0, 0.0, 0.5),
        42.0
    ));
    assert_eq!(d.obj.body.velocity, Vec3::new(1.0, 2.0, 0.0));
    assert_eq!(d.obj.body.omega, Vec3::new(0.0, 0.0, 0.5));
    assert!(d.obj.body.transient_state.contains(TransientState::ACTIVE));
    assert_eq!(d.obj.body.last_update_time, 42.0);
    assert!(!d.airborne, "a flat velocity keeps the body on the ground");
    assert!(d.obj.body.transient_state.contains(TransientState::CONTACT));
}

#[test]
fn a_static_body_takes_the_vector_but_stays_asleep() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    d.obj.body.state = PhysicsState::STATIC;
    d.obj.body.transient_state.remove(TransientState::ACTIVE);
    d.obj.body.last_update_time = 7.0;
    assert!(d.apply_authoritative_vector(&NoTargets, Vec3::X, Vec3::ZERO, 42.0));
    assert_eq!(d.obj.body.velocity, Vec3::X);
    assert!(!d.obj.body.transient_state.contains(TransientState::ACTIVE));
    assert_eq!(d.obj.body.last_update_time, 7.0);
}

#[test]
fn an_upward_vector_lifts_the_body_off_the_ground() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    let up = Vec3::new(0.0, 3.0, 4.5);
    assert!(d.apply_authoritative_vector(&NoTargets, up, Vec3::ZERO, 1.0));
    assert!(d.airborne);
    assert!(
        !d.obj
            .body
            .transient_state
            .intersects(TransientState::CONTACT | TransientState::ON_WALKABLE)
    );
    assert_eq!(d.obj.body.velocity, up);
}

#[test]
fn a_non_finite_vector_changes_nothing() {
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    assert!(!d.apply_authoritative_vector(
        &NoTargets,
        Vec3::new(f32::NAN, 0.0, 0.0),
        Vec3::ZERO,
        1.0
    ));
    assert!(!d.apply_authoritative_vector(&NoTargets, Vec3::ZERO, Vec3::splat(f32::INFINITY), 1.0));
    assert_eq!(d.obj.body.velocity, Vec3::ZERO);
}

/// A corpse whose catch-up stalled is put into its cell at the target as it
/// is by every placement (never searched aside), and keeps the contact it
/// had: the engine forces a hook, a storage chest and a corpse into the
/// cell and sets only their frame and cell. Placed as an ordinary object,
/// a solid object at the target refused the placement and the corpse never
/// got there.
#[test]
fn a_stalled_corpse_is_forced_into_its_cell_and_keeps_its_contact() {
    let mut engine = flat_engine();
    register_shadow(
        &mut engine,
        0x8602,
        0,
        Vec3::new(12.1, 20.0, 7.0),
        Quat::IDENTITY,
        10.0,
        LB,
        ShadowCollisionType::Cylinder,
        10.0,
        1.0,
        PhysicsState::STATIC.bits(),
        EntityCollisionFlags(0),
        CELL,
        true,
    );
    let mut d = remote(Vec3::new(1.0, 20.0, 7.0));
    d.placement_class = crate::set_position::PhysicsPlacementClass::Corpse;
    d.obj.body.contact_plane_valid = true;
    assert!(d.obj.body.in_contact() && d.obj.body.on_walkable());
    let target = Vec3::new(12.1, 20.0, 7.0);
    blip(&mut d, target, Quat::IDENTITY);
    let (_, request) = d.recovery_request(origin_of).unwrap();
    assert_eq!(
        request.placement_class,
        crate::set_position::PhysicsPlacementClass::Corpse
    );
    assert!(recover(&mut engine, &mut d), "the corpse is placed");
    assert_eq!(d.obj.body.position(), target);
    assert!(d.obj.body.in_contact(), "keeps its contact");
    assert!(d.obj.body.on_walkable(), "keeps its walkable ground");
    assert!(d.obj.body.contact_plane_valid, "keeps its contact plane");
}
