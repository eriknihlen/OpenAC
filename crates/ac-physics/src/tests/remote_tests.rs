//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/RemoteDeOverlapMechanismTests.cs`,
//! `RemoteWallPenetrationDiagnosticTests.cs` and `TeleportFarTownRunawayTests.cs`:
//! remote objects swept toward each other and through obstacles, and the
//! carried landblock anchor at unstreamed edges. The real-loop cases drive
//! each remote by the interpolation catch-up through the remote motion
//! combiner, as OpenAC's do.

use ac_motion::compute_offset;
use glam::{Quat, Vec3};

use super::support::*;
use crate::cell_array::CellArray;
use crate::cell_transit::{CellCollector, build_cell_set_and_pick_containing};
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::{Plane, Sphere};
use crate::interpolation::InterpolationManager;
use crate::shadow::ShadowCollisionType;
use crate::shadow_registry::RegistrationSite;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::world::CollisionWorld;
use crate::{PhysicsBody, PhysicsState, TransientState};

fn flat_engine(landblock: u32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        landblock,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

fn register_ball(
    engine: &mut PhysicsEngine,
    id: u32,
    center: Vec3,
    radius: f32,
    landblock: u32,
    flags: u8,
) {
    register_shadow(
        engine,
        id,
        0,
        center,
        Quat::IDENTITY,
        radius,
        landblock,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        0,
        EntityCollisionFlags(flags),
        0,
        false,
    );
}

fn edge_slide() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::EDGE_SLIDE)
}

// ── RemoteDeOverlapMechanismTests ──────────────────────────────────────

const LB: u32 = 0xA9B4_0000;
const CELL: u32 = LB | 0x0001;
const R: f32 = 0.48;
const H: f32 = 1.835;
const CONTACT: f32 = 2.0 * R;
const STEP_PER_TICK: f32 = 0.03;

fn grounded_body(pos: Vec3) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.set_position(pos);
    b.orientation = Quat::IDENTITY;
    b.state = PhysicsState::REPORT_COLLISIONS;
    b.transient_state =
        TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
    b.contact_plane_valid = true;
    b.contact_plane = Plane::new(Vec3::Z, 0.0);
    b.ground_normal = Vec3::Z;
    b
}

#[allow(clippy::too_many_arguments)]
fn step_toward(
    engine: &PhysicsEngine,
    id: u32,
    body: &mut PhysicsBody,
    cell: &mut u32,
    target: Vec3,
    radius: f32,
    height: f32,
) -> Vec3 {
    let pre = body.position();
    let flat_target = Vec3::new(target.x, target.y, pre.z);
    let delta = flat_target - pre;
    let dist = delta.length();
    let post = if dist <= STEP_PER_TICK {
        flat_target
    } else {
        pre + delta / dist * STEP_PER_TICK
    };
    let r = engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: pre,
            target_pos: post,
            cell_id: *cell,
            sphere_radius: radius,
            sphere_height: height,
            step_up_height: 0.60,
            step_down_height: 0.60,
            is_on_ground: true,
            mover_flags: edge_slide(),
            moving_entity_id: id,
            ..ResolveRequest::default()
        },
        Some(body),
    );
    body.set_position(r.position);
    if r.cell_id != 0 {
        *cell = r.cell_id;
    }
    body.position()
}

fn sync_shadow(engine: &mut PhysicsEngine, id: u32, pos: Vec3, cell: u32) {
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .update_position(
            world,
            id,
            RegistrationSite {
                world_pos: pos,
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: LB,
                seed_cell_id: cell,
            },
        )
        .unwrap();
}

fn separation(a: &PhysicsBody, b: &PhysicsBody) -> f32 {
    a.position().truncate().distance(b.position().truncate())
}

/// Two creatures 2 m apart walking to the same point; with `sync` their
/// shadows follow each resolved step.
fn converge(sync: bool, radius: f32, height: f32, start: f32, ticks: usize) -> (f32, f32) {
    let mut engine = flat_engine(LB);
    let (id_a, id_b) = (0xA1, 0xB2);
    let target = Vec3::new(10.0, 10.0, radius);
    let mut a = grounded_body(Vec3::new(10.0 - start, 10.0, radius));
    let mut b = grounded_body(Vec3::new(10.0 + start, 10.0, radius));
    register_ball(
        &mut engine,
        id_a,
        a.position(),
        radius,
        LB,
        EntityCollisionFlags::IS_CREATURE,
    );
    register_ball(
        &mut engine,
        id_b,
        b.position(),
        radius,
        LB,
        EntityCollisionFlags::IS_CREATURE,
    );
    let (mut cell_a, mut cell_b) = (CELL, CELL);
    let mut sep_at_200 = 0.0;
    for tick in 0..ticks {
        let pa = step_toward(&engine, id_a, &mut a, &mut cell_a, target, radius, height);
        if sync {
            sync_shadow(&mut engine, id_a, pa, cell_a);
        }
        let pb = step_toward(&engine, id_b, &mut b, &mut cell_b, target, radius, height);
        if sync {
            sync_shadow(&mut engine, id_b, pb, cell_b);
        }
        if tick == 200 {
            sep_at_200 = separation(&a, &b);
        }
    }
    (separation(&a, &b), sep_at_200)
}

#[test]
fn converging_creatures_with_shadow_following_resolved_settle_at_contact_distance() {
    let (sep, sep_at) = converge(true, R, H, 1.0, 250);
    assert!(sep >= CONTACT - 0.16, "separation {sep}");
    assert!((sep - sep_at).abs() < 0.02, "drifted {sep_at} -> {sep}");
}

#[test]
fn converging_creatures_without_shadow_sync_overlap() {
    let (sep, _) = converge(false, R, H, 1.0, 250);
    assert!(sep < 0.40, "separation {sep}");
}

#[test]
fn converging_large_creatures_de_overlap_wider_than_human() {
    const BIG_R: f32 = 0.9;
    let (sep, _) = converge(true, BIG_R, 3.2, 2.5, 300);
    assert!(sep >= 2.0 * BIG_R - 0.30, "separation {sep}");
    assert!(sep > CONTACT + 0.4, "separation {sep}");
}

/// One remote in OpenAC's real loop: a server position queued every 10
/// ticks toward the shared point, each 1/60 s tick moved by the
/// interpolation catch-up (no root motion, 4 m/s), resolved, and its shadow
/// moved to the result.
struct InterpRemote {
    id: u32,
    body: PhysicsBody,
    cell: u32,
    interp: InterpolationManager,
    mover: ObjectInfoState,
}

impl InterpRemote {
    fn new(engine: &mut PhysicsEngine, id: u32, x: f32, flags: u8, mover: ObjectInfoState) -> Self {
        let body = grounded_body(Vec3::new(x, 10.0, R));
        register_ball(engine, id, body.position(), R, LB, flags);
        Self {
            id,
            body,
            cell: CELL,
            interp: InterpolationManager::new(),
            mover,
        }
    }

    fn enqueue(&mut self, target: Vec3) {
        let _ = self.interp.enqueue_heading(
            target,
            0.0,
            false,
            Some(self.body.position()),
            0,
            InterpolationManager::AUTONOMY_BLIP_DISTANCE,
        );
    }

    /// One tick; returns the position before it.
    fn tick(&mut self, engine: &mut PhysicsEngine) -> Vec3 {
        let pre = self.body.position();
        let offset = compute_offset(
            1.0 / 60.0,
            pre,
            Vec3::ZERO,
            self.body.orientation,
            &mut self.interp,
            4.0,
        );
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pre,
                target_pos: pre + offset,
                cell_id: self.cell,
                sphere_radius: R,
                sphere_height: H,
                step_up_height: 0.60,
                step_down_height: 0.60,
                is_on_ground: true,
                mover_flags: self.mover,
                moving_entity_id: self.id,
                ..ResolveRequest::default()
            },
            Some(&mut self.body),
        );
        self.body.set_position(r.position);
        if r.cell_id != 0 {
            self.cell = r.cell_id;
        }
        sync_shadow(engine, self.id, self.body.position(), self.cell);
        pre
    }
}

/// Runs two remotes 600 ticks toward (10, 10); returns the final
/// separation and each one's largest per-tick move after tick 120.
fn interp_loop(
    engine: &mut PhysicsEngine,
    a: &mut InterpRemote,
    b: &mut InterpRemote,
) -> (f32, f32, f32) {
    let target = Vec3::new(10.0, 10.0, R);
    let (mut spike_a, mut spike_b) = (0.0f32, 0.0f32);
    for tick in 0..600 {
        if tick % 10 == 0 {
            a.enqueue(target);
            b.enqueue(target);
        }
        let pre_a = a.tick(engine);
        let pre_b = b.tick(engine);
        if tick > 120 {
            let moved =
                |body: &PhysicsBody, pre: Vec3| body.position().truncate().distance(pre.truncate());
            spike_a = spike_a.max(moved(&a.body, pre_a));
            spike_b = spike_b.max(moved(&b.body, pre_b));
        }
    }
    (separation(&a.body, &b.body), spike_a, spike_b)
}

fn player_mover() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

#[test]
fn converging_creatures_in_the_real_interpolation_loop_de_overlap_and_absorb_the_stall_blip() {
    let mut engine = flat_engine(LB);
    let creature = EntityCollisionFlags::IS_CREATURE;
    let mut a = InterpRemote::new(&mut engine, 0xA1, 9.0, creature, edge_slide());
    let mut b = InterpRemote::new(&mut engine, 0xB2, 11.0, creature, edge_slide());
    let (sep, spike_a, spike_b) = interp_loop(&mut engine, &mut a, &mut b);
    assert!(sep >= CONTACT - 0.16, "separation {sep}");
    assert!(
        spike_a < 0.30 && spike_b < 0.30,
        "spikes {spike_a} {spike_b}"
    );
}

#[test]
fn converging_players_walk_through_each_other() {
    let mut engine = flat_engine(LB);
    let player = EntityCollisionFlags::IS_PLAYER | EntityCollisionFlags::IS_CREATURE;
    let mut a = InterpRemote::new(&mut engine, 0x5000_0001, 9.0, player, player_mover());
    let mut b = InterpRemote::new(&mut engine, 0x5000_0002, 11.0, player, player_mover());
    let (sep, _, _) = interp_loop(&mut engine, &mut a, &mut b);
    assert!(sep < 0.40, "separation {sep}");
}

#[test]
fn a_player_and_a_monster_de_overlap_and_absorb_the_stall_blip() {
    let mut engine = flat_engine(LB);
    let player = EntityCollisionFlags::IS_PLAYER | EntityCollisionFlags::IS_CREATURE;
    let mut p = InterpRemote::new(&mut engine, 0x5000_0001, 9.0, player, player_mover());
    let mut m = InterpRemote::new(
        &mut engine,
        0x8000_0002,
        11.0,
        EntityCollisionFlags::IS_CREATURE,
        edge_slide(),
    );
    let (sep, spike_p, spike_m) = interp_loop(&mut engine, &mut p, &mut m);
    assert!(sep >= CONTACT - 0.16, "separation {sep}");
    assert!(
        spike_p < 0.30 && spike_m < 0.30,
        "spikes {spike_p} {spike_m}"
    );
}

// ── RemoteWallPenetrationDiagnosticTests ───────────────────────────────

const WALL_LB: u32 = 0xA9D6_0000;
const WALL_CELL: u32 = WALL_LB | 0x0001;

fn floor_body(pos: Vec3) -> PhysicsBody {
    let floor = Plane::new(Vec3::Z, 0.0);
    let mut b = PhysicsBody::new();
    b.set_position(pos);
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = floor;
    b.contact_plane_cell_id = WALL_CELL;
    b.walkable_polygon_valid = true;
    b.walkable_plane = floor;
    b.walkable_vertices = Some(vec![
        Vec3::new(-100.0, -100.0, 0.0),
        Vec3::new(100.0, -100.0, 0.0),
        Vec3::new(100.0, 100.0, 0.0),
        Vec3::new(-100.0, 100.0, 0.0),
    ]);
    b.walkable_up = Vec3::Z;
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

fn jump(engine: &PhysicsEngine, from: Vec3, to: Vec3) -> Vec3 {
    let mut body = floor_body(from);
    engine
        .resolve_with_transition(
            &ResolveRequest {
                current_pos: from,
                target_pos: to,
                cell_id: WALL_CELL,
                sphere_radius: R,
                sphere_height: H,
                step_up_height: 0.4,
                step_down_height: 0.4,
                is_on_ground: true,
                mover_flags: edge_slide(),
                ..ResolveRequest::default()
            },
            Some(&mut body),
        )
        .position
}

fn wall_engine(obstacle: Option<u32>) -> PhysicsEngine {
    let mut engine = flat_engine(WALL_LB);
    if let Some(id) = obstacle {
        register_ball(
            &mut engine,
            id,
            Vec3::new(12.0, 11.5, R),
            R,
            WALL_LB,
            EntityCollisionFlags::IS_CREATURE,
        );
    }
    engine
}

#[test]
fn single_large_tick_jump_through_obstacle_is_still_blocked_at_surface() {
    let p = jump(
        &wall_engine(Some(0xC0F0)),
        Vec3::new(12.0, 10.0, 0.0),
        Vec3::new(12.0, 12.4, 0.0),
    );
    assert!(p.y < 10.7, "y {}", p.y);
}

#[test]
fn single_large_tick_jump_with_no_obstacle_reaches_far_target() {
    let p = jump(
        &wall_engine(None),
        Vec3::new(12.0, 10.0, 0.0),
        Vec3::new(12.0, 12.4, 0.0),
    );
    assert!(p.y > 12.2, "y {}", p.y);
}

#[test]
fn single_large_tick_jump_starting_inside_obstacle_overlap_does_not_tunnel() {
    let p = jump(
        &wall_engine(Some(0xC0F1)),
        Vec3::new(12.0, 10.84, 0.0),
        Vec3::new(12.0, 13.0, 0.0),
    );
    assert!(p.y < 11.4, "y {}", p.y);
}

// ── TeleportFarTownRunawayTests ────────────────────────────────────────

fn find_with_anchor(center: Vec3, current: u32, anchor: Option<Vec3>) -> u32 {
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    build_cell_set_and_pick_containing(
        &CollisionWorld::new(),
        &[Sphere::new(center, 0.48)],
        1,
        current,
        anchor,
        &mut collector,
    )
    .0
}

#[test]
fn south_edge_unstreamed_neighbour_carried_anchor_does_not_march() {
    let center = Vec3::new(14.8, -0.088, 12.0);
    let with_anchor = find_with_anchor(center, 0xC95A_0001, Some(Vec3::new(0.0, -192.0, 0.0)));
    assert_eq!((with_anchor >> 16) & 0xFF, 0x5A);
    assert_eq!(find_with_anchor(center, 0xC95A_0001, None), 0xC95A_0001);
}

#[test]
fn east_edge_unstreamed_neighbour_carried_anchor_does_not_march() {
    let center = Vec3::new(192.088, 14.8, 12.0);
    let with_anchor = find_with_anchor(center, 0xCA5B_0001, Some(Vec3::new(192.0, 0.0, 0.0)));
    assert_eq!((with_anchor >> 24) & 0xFF, 0xCA);
    assert_eq!(find_with_anchor(center, 0xCA5B_0001, None), 0xCA5B_0001);
}
