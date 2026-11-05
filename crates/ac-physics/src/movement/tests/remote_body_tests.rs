//! A remote body carried by `RemoteMotionDriver` over real ground: OpenAC
//! `tests/AcDream.Runtime.Tests/Physics/RuntimeRemoteFailedSweepTests.cs`,
//! `RuntimeRemoteSlopeProjectionTests.cs`, `RuntimeRemoteSteepContactSlideTests.cs`,
//! `RuntimeRemoteUphillProgressTests.cs` (with their `RemoteRampHarness`) and
//! the drive cases of `RuntimeRemoteBodyDriveTests.cs` (the allocation,
//! cost and arming cases are the runtime's).
//!
//! OpenAC's ramp harness ticks its updater with one 1/30 s frame per tick,
//! which its object clock spends as one quantum; here each tick is that one
//! quantum (`tick_body`). OpenAC counts landings and leavings on the
//! interpreter's link-animation removal; here they are counted as the
//! walkable flag's changes between ticks (one sweep per tick, and the
//! interpreter's ground edges fire exactly when that sweep changes it).
//! A counting motion sink would not do: an object with a sink replays its
//! movement into it instead of taking the state velocity.

use std::sync::Arc;

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::MotionDeltaFrame;
use glam::{Quat, Vec3};

use super::motion_fixtures::{MapLoader, TableBuilder, moving_anim};
use crate::body::PhysicsBody;
use crate::engine::{PhysicsEngine, ResolveResult};
use crate::globals::FLOOR_Z;
use crate::movement::remote::commit_sweep_outcome;
use crate::movement::{MotionInterp, MovementOps, NoTargets, ObjectToken, RemoteMotionDriver};
use crate::settle::try_settle;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::world::CollisionWorld;

const LANDBLOCK: u32 = 0x0101_FFFF;
const START_X: f32 = 96.0;
const START_Y: f32 = 96.0;
const RADIUS: f32 = 0.48;
const HEIGHT: f32 = 1.835;

// ── RuntimeRemoteFailedSweepTests (the commit) ────────────────────────────

const COMMITTED_CELL: u32 = 0x0101_0001;

#[test]
fn a_failed_sweep_holds_the_pre_step_origin_zeroes_the_step_velocity_and_keeps_the_cell() {
    let pre = Vec3::new(10.0, 10.0, 5.0);
    let mut body = PhysicsBody::new();
    body.set_position(pre);
    body.cached_velocity = Vec3::ONE;
    let failed = ResolveResult {
        position: pre + Vec3::new(0.0, -0.2, 0.0),
        cell_id: 0x0101_000A,
        is_on_ground: true,
        ok: false,
        ..ResolveResult::default()
    };

    let cell = commit_sweep_outcome(&mut body, &failed, pre, 1.0 / 30.0, COMMITTED_CELL);

    assert_eq!(body.position(), pre);
    assert_eq!(body.cached_velocity, Vec3::ZERO);
    assert_eq!(cell, COMMITTED_CELL);
}

#[test]
fn a_successful_sweep_commits_the_resolved_origin_cell_and_step_velocity() {
    let pre = Vec3::new(10.0, 10.0, 5.0);
    let mut body = PhysicsBody::new();
    body.set_position(pre);
    let resolved = pre + Vec3::new(0.0, -0.2, 0.0);
    let ok = ResolveResult {
        position: resolved,
        cell_id: 0x0101_000A,
        is_on_ground: true,
        ok: true,
        ..ResolveResult::default()
    };
    let dt = 1.0 / 30.0;

    let cell = commit_sweep_outcome(&mut body, &ok, pre, dt, COMMITTED_CELL);

    assert_eq!(body.position(), resolved);
    assert_eq!(body.cached_velocity, (resolved - pre) / dt);
    assert_eq!(cell, 0x0101_000A);
}

// ── RemoteRampHarness ─────────────────────────────────────────────────────

/// Terrain rising along -y at `gradient` (metres per metre).
fn ramp(gradient: f32) -> TerrainSurface {
    let mut table = [0.0f32; 256];
    for (i, h) in table.iter_mut().enumerate() {
        *h = i as f32 * gradient * TerrainSurface::CELL_SIZE;
    }
    let mut heights = [0u8; 81];
    for x in 0..9 {
        for y in 0..9 {
            heights[x * 9 + y] = (8 - y) as u8;
        }
    }
    TerrainSurface::new(&heights, &table, 0, 0, None)
}

struct RampHarness {
    remote: RemoteMotionDriver,
    engine: PhysicsEngine,
    surface: TerrainSurface,
    now: f64,
    /// Landings and leavings seen by [`RampHarness::tick`].
    ground_edges: usize,
}

impl RampHarness {
    fn create(gradient: f32, height_above_surface: f32) -> Self {
        let surface = ramp(gradient);
        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        engine.add_landblock(LANDBLOCK, surface.clone(), Vec::new(), Vec::new(), 0.0, 0.0);
        let position = Vec3::new(
            START_X,
            START_Y,
            surface.sample_z(START_X, START_Y) + height_above_surface,
        );
        let cell = TerrainSurface::compute_outdoor_cell_id_in(LANDBLOCK, START_X, START_Y);
        let mut remote = RemoteMotionDriver::new(
            ObjectToken {
                id: 0x7000_0101,
                incarnation: 1,
            },
            cell,
            position,
            Quat::IDENTITY,
        );
        remote.obj.body.state =
            PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS | PhysicsState::EDGE_SLIDE;
        remote.obj.body.transient_state = TransientState::NONE;
        remote.obj.radius = RADIUS;
        remote.obj.height = HEIGHT;
        remote.local_entity_id = 1;
        remote.last_server_pos = position;
        remote.last_server_pos_time = 1.0;
        Self {
            remote,
            engine,
            surface,
            now: 1.0,
            ground_edges: 0,
        }
    }

    fn on_ramp(gradient: f32) -> Self {
        let mut h = Self::create(gradient, 0.0);
        let mut body = h.remote.obj.body.clone();
        let position = body.position();
        let (mut hit, mut left) = (false, false);
        try_settle(
            &h.engine,
            &mut body,
            position,
            h.remote.obj.cell_id,
            RADIUS,
            HEIGHT,
            ObjectInfoState(ObjectInfoState::EDGE_SLIDE),
            h.remote.local_entity_id,
            &mut |_| hit = true,
            &mut |_| left = true,
        );
        h.remote.obj.body = body;
        if hit {
            h.remote.cx(&NoTargets).movement_hit_ground();
        }
        if left {
            MotionInterp::leave_ground(&mut h.remote.cx(&NoTargets));
        }
        h.remote.airborne = !h.remote.obj.body.on_walkable();
        h
    }

    fn airborne(gradient: f32, height: f32) -> Self {
        let mut h = Self::create(gradient, height);
        h.remote
            .obj
            .body
            .transient_state
            .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
        h.remote.obj.body.contact_plane_valid = false;
        h.remote.airborne = true;
        h
    }

    fn body(&self) -> &PhysicsBody {
        &self.remote.obj.body
    }

    fn surface_z_under_body(&self) -> f32 {
        let p = self.body().position();
        self.surface.sample_z(p.x, p.y)
    }

    fn tick(&mut self, count: usize, root_motion_local: Vec3) {
        self.tick_dt(count, root_motion_local, 1.0 / 30.0);
    }

    fn tick_dt(&mut self, count: usize, root_motion_local: Vec3, dt: f32) {
        for _ in 0..count {
            self.now += f64::from(dt);
            let delta = MotionDeltaFrame {
                origin: root_motion_local,
                ..MotionDeltaFrame::default()
            };
            let was_walkable = self.remote.obj.body.on_walkable();
            self.remote
                .tick_body(Some(&self.engine), &NoTargets, dt, self.now, delta);
            if self.remote.obj.body.on_walkable() != was_walkable {
                self.ground_edges += 1;
            }
        }
    }
}

#[test]
fn a_real_sweep_on_the_harness_publishes_the_step_velocity() {
    let mut h = RampHarness::on_ramp(0.0);
    let before = h.body().position();
    let dt = 1.0 / 30.0;

    h.tick_dt(1, Vec3::new(0.0, 0.1, 0.0), dt);

    let moved = h.body().position() - before;
    assert_ne!(moved, Vec3::ZERO);
    assert_eq!(h.body().cached_velocity, moved / dt);
}

// ── RuntimeRemoteSlopeProjectionTests ─────────────────────────────────────

const WALKABLE_SLOPE: f32 = 0.6;
const ROOT_MOTION_PER_TICK: f32 = 0.10;
const TRACKED_TICKS: usize = 30;
const SURFACE_TOLERANCE: f32 = 0.005;

fn ramp_normal(gradient: f32) -> Vec3 {
    Vec3::new(0.0, gradient, 1.0).normalize()
}

#[test]
fn the_fixture_ramp_is_walkable_and_its_plane_is_the_geometric_one() {
    let h = RampHarness::on_ramp(WALKABLE_SLOPE);
    assert!(h.body().on_walkable());
    assert!(h.body().contact_plane_valid);
    let expected = ramp_normal(WALKABLE_SLOPE);
    let actual = h.body().contact_plane.normal;
    assert!(
        expected.distance(actual) < 0.001,
        "contact plane normal {actual}, expected {expected}"
    );
    assert!(actual.z >= FLOOR_Z);
}

#[test]
fn the_remote_tick_tracks_the_surface_while_running_downhill() {
    let mut h = RampHarness::on_ramp(WALKABLE_SLOPE);
    assert!(h.body().on_walkable());
    let resting = h.body().position().z - h.surface_z_under_body();
    for tick in 1..=TRACKED_TICKS {
        h.tick(1, Vec3::new(0.0, ROOT_MOTION_PER_TICK, 0.0));
        let offset = h.body().position().z - h.surface_z_under_body();
        assert!(
            (offset - resting).abs() < SURFACE_TOLERANCE,
            "tick {tick}: {offset} above the terrain, expected {resting} (pos {})",
            h.body().position()
        );
    }
}

#[test]
fn the_tracking_fixture_actually_requires_the_body_to_change_z() {
    let mut h = RampHarness::on_ramp(WALKABLE_SLOPE);
    let start_z = h.body().position().z;
    h.tick(TRACKED_TICKS, Vec3::new(0.0, ROOT_MOTION_PER_TICK, 0.0));
    let dz = h.body().position().z - start_z;
    assert!(dz < -1.0, "not descending: dz = {dz}");
}

// ── RuntimeRemoteSteepContactSlideTests ───────────────────────────────────

const STEEP: f32 = 1.30;
const GENTLE: f32 = 0.10;

#[test]
fn steep_terrain_produces_a_non_walkable_contact_plane() {
    let h = RampHarness::on_ramp(STEEP);
    assert!(h.body().contact_plane_valid);
    let z = h.body().contact_plane.normal.z;
    assert!((0.55..=FLOOR_Z - 0.001).contains(&z), "normal z {z}");
}

#[test]
fn steep_contact_does_not_latch_a_landing() {
    let mut h = RampHarness::on_ramp(STEEP);
    h.remote.airborne = true;
    h.tick(40, Vec3::ZERO);
    assert!(h.body().in_contact());
    assert!(!h.body().on_walkable());
    assert_eq!(h.ground_edges, 0);
}

#[test]
fn gravity_persists_across_ticks_on_a_steep_contact() {
    let mut h = RampHarness::on_ramp(STEEP);
    h.remote.airborne = true;
    h.tick(40, Vec3::ZERO);
    assert!(h.body().has_gravity());
    assert!(h.body().acceleration.z < -1.0);
}

#[test]
fn steep_contact_keeps_the_body_sliding_downhill() {
    let mut h = RampHarness::on_ramp(STEEP);
    let start = h.body().position();
    h.tick(40, Vec3::ZERO);
    let travelled = h.body().position() - start;
    assert!(travelled.length() > 0.25, "moved {}", travelled.length());
    assert!(travelled.z < -0.1, "dz = {}", travelled.z);
}

#[test]
fn the_tick_never_asserts_contact_or_walkable_without_a_sweep() {
    let mut h = RampHarness::on_ramp(GENTLE);
    h.remote.obj.cell_id = 0;
    h.remote
        .obj
        .body
        .transient_state
        .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
    h.remote.airborne = false;
    h.tick(1, Vec3::ZERO);
    assert!(!h.body().in_contact());
    assert!(!h.body().on_walkable());
}

#[test]
fn a_grounded_tick_on_a_steep_face_releases_the_body_instead_of_pinning_it() {
    let mut h = RampHarness::on_ramp(STEEP);
    h.remote
        .obj
        .body
        .transient_state
        .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
    h.remote.airborne = false;
    h.remote.obj.body.velocity = Vec3::new(0.0, 1.0, -STEEP).normalize() * 3.0;
    let start = h.body().position();
    h.tick(40, Vec3::ZERO);
    assert!(!h.body().on_walkable());
    let travelled = (h.body().position() - start).length();
    assert!(travelled > 2.0, "travelled {travelled}");
}

#[test]
fn authoritative_velocity_is_not_discarded_on_a_grounded_tick() {
    let mut h = RampHarness::on_ramp(GENTLE);
    assert!(!h.remote.airborne);
    h.remote.obj.body.velocity = Vec3::new(2.146, 2.264, -3.549);
    h.tick(1, Vec3::ZERO);
    assert_ne!(h.body().velocity, Vec3::ZERO);
    assert!(
        h.body().velocity.x > 0.5,
        "velocity x {}",
        h.body().velocity.x
    );
}

fn assert_transients_are_contact_plane_derived(body: &PhysicsBody, expect_walkable: bool) {
    assert!(
        !body.on_walkable() || body.in_contact(),
        "walkable without contact"
    );
    assert!(
        !body.in_contact() || body.contact_plane_valid,
        "contact without a contact plane"
    );
    assert_eq!(expect_walkable, body.contact_plane.normal.z >= FLOOR_Z);
    assert_eq!(expect_walkable, body.on_walkable());
}

#[test]
fn committed_transients_agree_with_the_committed_contact_plane() {
    let mut steep = RampHarness::on_ramp(STEEP);
    steep.tick(20, Vec3::ZERO);
    assert_transients_are_contact_plane_derived(steep.body(), false);

    let mut gentle = RampHarness::on_ramp(GENTLE);
    gentle.tick(20, Vec3::ZERO);
    assert_transients_are_contact_plane_derived(gentle.body(), true);
}

#[test]
fn a_walkable_landing_still_lands_and_fires_the_ground_edge_once() {
    let mut h = RampHarness::airborne(GENTLE, 3.0);
    h.tick(60, Vec3::ZERO);
    assert!(h.body().on_walkable());
    assert!(!h.remote.airborne);
    assert_eq!(h.ground_edges, 1);
}

#[test]
fn a_walkable_landing_does_not_clear_the_gravity_state_bit() {
    let mut h = RampHarness::airborne(GENTLE, 3.0);
    h.tick(60, Vec3::ZERO);
    assert!(h.body().on_walkable());
    assert!(h.body().has_gravity());
}

// ── RuntimeRemoteUphillProgressTests ──────────────────────────────────────

const UPHILL_ROOT_MOTION: Vec3 = Vec3::new(0.02588, -0.09659, 0.0);
const EXACTLY_UP_SLOPE_ROOT_MOTION: Vec3 = Vec3::new(0.0, -0.1, 0.0);

#[test]
fn a_remote_climbs_a_walkable_slope_and_keeps_its_feet_on_it() {
    let mut h = RampHarness::on_ramp(WALKABLE_SLOPE);
    assert!(h.body().on_walkable());

    h.tick(1, UPHILL_ROOT_MOTION);
    assert!(
        !h.body().transient_state.contains(TransientState::SLIDING),
        "the landing sliding latch survived its first off-gradient step"
    );

    let start_z = h.body().position().z;
    let mut previous_z = start_z;
    let resting = h.body().position().z - h.surface_z_under_body();
    for tick in 1..=TRACKED_TICKS {
        h.tick(1, UPHILL_ROOT_MOTION);
        let z = h.body().position().z;
        assert!(
            z > previous_z,
            "tick {tick}: no height gained ({previous_z} -> {z})"
        );
        let offset = z - h.surface_z_under_body();
        assert!(
            (offset - resting).abs() < SURFACE_TOLERANCE,
            "tick {tick}: {offset} above the terrain, expected {resting}"
        );
        previous_z = z;
    }
    let ascent = h.body().position().z - start_z;
    assert!(ascent > 1.0, "not climbing: dz = {ascent}");
}

#[test]
fn an_exactly_up_slope_offset_is_absorbed_by_the_persisted_sliding_normal() {
    let mut h = RampHarness::on_ramp(WALKABLE_SLOPE);
    assert!(h.body().transient_state.contains(TransientState::SLIDING));
    assert!(
        h.body().sliding_normal.distance(Vec3::new(0.0, 1.0, 0.0)) < 0.001,
        "sliding normal {}",
        h.body().sliding_normal
    );
    let latched = h.body().position();

    h.tick(5, EXACTLY_UP_SLOPE_ROOT_MOTION);

    assert_eq!(h.body().position().x, latched.x);
    assert_eq!(h.body().position().y, latched.y);
    super::support::eq_prec(latched.z, h.body().position().z, 4);
    assert!(h.body().transient_state.contains(TransientState::SLIDING));

    h.tick(1, UPHILL_ROOT_MOTION);

    assert!(!h.body().transient_state.contains(TransientState::SLIDING));
    assert!(
        h.body().position().x > latched.x,
        "the cross-slope component was absorbed too (pos {})",
        h.body().position()
    );
    super::support::eq_prec(latched.z, h.body().position().z, 4);

    h.tick(1, UPHILL_ROOT_MOTION);

    assert!(
        h.body().position().z > latched.z,
        "no climb once the latch cleared (pos {})",
        h.body().position()
    );
}

// ── RuntimeRemoteBodyDriveTests (the drive) ───────────────────────────────

/// A tenth of a metre a frame at thirty frames a second.
const AUTHORED_METRES_PER_FRAME: f32 = 0.1;
const AUTHORED_FRAMES_PER_SECOND: f32 = 30.0;
const DEFAULT_ANIMATION: u32 = 0x0300_AA01;

/// One creature on flat ground at (96, 96) playing a default animation
/// that walks 0.1 m along +x per frame, with the player beside it.
struct Crowd {
    remote: RemoteMotionDriver,
    engine: PhysicsEngine,
    player: Vec3,
    last_advanced: bool,
}

impl Crowd {
    fn create() -> Self {
        let surface = TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None);
        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        engine.add_landblock(LANDBLOCK, surface.clone(), Vec::new(), Vec::new(), 0.0, 0.0);
        let position = Vec3::new(START_X, START_Y, surface.sample_z(START_X, START_Y));
        let mut remote = RemoteMotionDriver::new(
            ObjectToken {
                id: 0x7000_0001,
                incarnation: 1,
            },
            TerrainSurface::compute_outdoor_cell_id_in(LANDBLOCK, START_X, START_Y),
            position,
            Quat::IDENTITY,
        );
        remote.obj.body.state =
            PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS | PhysicsState::EDGE_SLIDE;
        remote.last_server_pos = position;
        remote.last_server_pos_time = 1.0;
        let mut loader = MapLoader::default();
        loader.add(
            DEFAULT_ANIMATION,
            moving_anim(4, 1, Vec3::new(AUTHORED_METRES_PER_FRAME, 0.0, 0.0)),
        );
        remote.attach_sequencer(ac_motion::AnimationSequencer::new(
            1,
            DEFAULT_ANIMATION,
            TableBuilder::new(0x8000_003D).rules(),
            Arc::new(loader),
        ));
        Self {
            remote,
            engine,
            player: position,
            last_advanced: false,
        }
    }

    fn tick(&mut self, elapsed: f32) {
        let out = self.remote.advance(
            Some(&self.engine),
            &NoTargets,
            elapsed,
            1.0,
            Some(self.player),
            true,
            true,
        );
        self.last_advanced = out.advanced;
    }

    fn position(&self) -> Vec3 {
        self.remote.obj.body.position()
    }
}

#[test]
fn a_body_travels_the_distance_its_authored_cycle_asks_for_in_bounded_steps() {
    let mut world = Crowd::create();
    world.tick(0.5);
    let expected = AUTHORED_METRES_PER_FRAME * AUTHORED_FRAMES_PER_SECOND * 0.5;
    assert!(world.last_advanced);
    super::support::eq_prec(START_X + expected, world.position().x, 3);
    super::support::eq_prec(START_Y, world.position().y, 3);
}

#[test]
fn many_short_frames_end_within_one_authored_frame_of_one_long_frame() {
    let mut one_frame = Crowd::create();
    let mut many_frames = Crowd::create();
    one_frame.tick(0.9);
    for _ in 0..60 {
        many_frames.tick(0.015);
    }
    let gap = one_frame.position().x - many_frames.position().x;
    assert!(
        (0.0..=AUTHORED_METRES_PER_FRAME + 0.001).contains(&gap),
        "gap {gap}"
    );
}

/// OpenAC `RemoteBodyAdvanceSequenceTests.AFrameTooShortToBeWorthAStepLeavesTheBodyWhereItWas`:
/// a hundredth of a second is below the smallest quantum.
#[test]
fn a_frame_too_short_for_a_step_leaves_the_body_where_it_was() {
    let mut world = Crowd::create();
    let before = world.position();
    world.tick(0.01);
    assert_eq!(world.position(), before);
}

/// OpenAC `RemoteBodyAdvanceSequenceTests.AWalkingCreatureIsSweptAgainstTheGroundItStandsOn`:
/// the walk ends standing on the ground, on walkable footing.
#[test]
fn a_walking_body_is_swept_against_the_ground_it_stands_on() {
    let mut world = Crowd::create();
    let ground = world.position().z;
    world.tick(0.5);
    assert!(world.remote.obj.body.on_walkable());
    super::support::eq_prec(ground, world.position().z, 3);
}

/// OpenAC `RemoteBodyAdvanceSequenceTests.AnUnseenCreatureCatchesUpWithoutAdvancingItsCycle`:
/// a hidden body still catches up to where the server last put it (1 m
/// in half a second, as the report's 0.4, 0.4 and 0.2 m steps) and its
/// cycle carries it nowhere.
#[test]
fn an_unseen_body_catches_up_without_advancing_its_cycle() {
    let mut world = Crowd::create();
    world.remote.hidden = true;
    world.remote.obj.body.state |= PhysicsState::HIDDEN;
    let start = world.position();
    let target = start + Vec3::new(1.0, 0.0, 0.0);
    world
        .remote
        .interpolation
        .enqueue(target, Quat::IDENTITY, false, Some(start), None, 0, 100.0);
    world.remote.max_root_motion_speed_since_last_up = 0.0;
    world.tick(0.5);
    println!("{start} -> {}", world.position());
    super::support::eq_prec(target.x, world.position().x, 3);
    super::support::eq_prec(start.y, world.position().y, 3);
    assert_eq!(world.remote.max_root_motion_speed_since_last_up, 0.0);
}

#[test]
fn a_body_beyond_the_live_bubble_is_not_carried() {
    let mut world = Crowd::create();
    world.player = Vec3::new(START_X + 500.0, START_Y, 0.0);
    world.tick(0.5);
    assert!(!world.last_advanced);
    super::support::eq_prec(START_X, world.position().x, 4);
}
