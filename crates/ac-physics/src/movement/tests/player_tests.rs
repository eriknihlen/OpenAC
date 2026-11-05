//! OpenAC `tests/AcDream.Runtime.Tests/Gameplay/PlayerMovementControllerTests.cs`,
//! `LocalPlayerCycleQuantumTests.cs`, `LocalPlayerImmediatePositionTests.cs`
//! (the decision; the bytes are the runtime's), `DispatchedMotionDrainTests.cs`
//! and `LocalPlayerOutboundCombatStyleTests.cs` (the raw state; its packing
//! is `ac-proto`'s). `PublicationLifecycleRequiresExplicitActivationAfterOwnershipCommit`
//! tests OpenAC's runtime publication state machine, which is not ported.
//! Interface text comes back as refusals mapped through
//! `jump_refusal_text`.

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use ac_core::chat::client_text;
use ac_core::physics::PhysicsState;
use ac_motion::MotionDeltaFrame;
use ac_motion::motion_command::READY;
use glam::{Quat, Vec2, Vec3};

use super::support::eq_prec;
use crate::body::PhysicsBody;
use crate::engine::PhysicsEngine;
use crate::movement::{
    InterpretedMotionSink, MotionInterp, MotionSink, MovementEnv, MovementInput, MovementObject,
    MovementResult, MovementStruct, NoTargets, ObjectToken, PlayerMovementController, PlayerWeenie,
    PositionOps, WeenieError, WeenieObject, build_raw_motion_state, jump_refusal_text,
};
use crate::terrain::TerrainSurface;

pub(super) fn object_tick() -> f32 {
    PhysicsBody::MIN_QUANTUM + 0.001
}

pub(super) fn engine_with(heights: [u8; 81]) -> PhysicsEngine {
    let mut table = [0.0f32; 256];
    for (i, t) in table.iter_mut().enumerate() {
        *t = i as f32;
    }
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&heights, &table, 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

pub(super) fn flat_engine() -> PhysicsEngine {
    engine_with([50; 81])
}

/// A controller and the physics it moves in.
pub(super) struct Rig {
    pub engine: PhysicsEngine,
    pub c: PlayerMovementController,
}

impl Rig {
    pub fn new() -> Self {
        Self::on(flat_engine())
    }

    pub fn on(engine: PhysicsEngine) -> Self {
        Self {
            engine,
            c: PlayerMovementController::new(),
        }
    }

    pub fn seeded() -> Self {
        let mut r = Self::new();
        r.seed(
            Vec3::new(96.0, 96.0, 50.0),
            0x0001,
            Vec3::new(96.0, 96.0, 50.0),
        );
        r
    }

    pub fn seed(&mut self, pos: Vec3, cell: u32, local: Vec3) {
        let mut env = MovementEnv::new(&mut self.engine, &NoTargets);
        self.c.seed_placement(&mut env, pos, cell, local);
    }

    pub fn update(&mut self, dt: f32, input: MovementInput) -> MovementResult {
        let mut env = MovementEnv::new(&mut self.engine, &NoTargets);
        self.c.update(&mut env, dt, input)
    }

    pub fn tick_hidden(&mut self, dt: f32) -> MovementResult {
        let mut env = MovementEnv::new(&mut self.engine, &NoTargets);
        self.c.tick_hidden(&mut env, dt)
    }

    pub fn obj(&mut self) -> &mut MovementObject<PlayerWeenie> {
        self.c.object_mut()
    }

    pub fn attach(&mut self, advance: impl FnMut(f32, &mut MotionDeltaFrame) + 'static) {
        self.c
            .attach_animation_root_motion_source(Box::new(advance), None);
    }

    pub fn attach_with_hooks(
        &mut self,
        advance: impl FnMut(f32, &mut MotionDeltaFrame) + 'static,
        hooks: impl FnMut() + 'static,
    ) {
        self.c
            .attach_animation_root_motion_source(Box::new(advance), Some(Box::new(hooks)));
    }

    pub fn refusal_texts(&mut self) -> Vec<&'static str> {
        self.c
            .take_interface_refusals()
            .into_iter()
            .filter_map(jump_refusal_text)
            .collect()
    }
}

fn input() -> MovementInput {
    MovementInput::default()
}

fn forward() -> MovementInput {
    MovementInput {
        forward: true,
        ..input()
    }
}

fn jump(held: bool) -> MovementInput {
    MovementInput {
        jump: held,
        ..input()
    }
}

/// Plays everything (an animation sink that accepts every motion).
pub(super) struct PlaysEverything;

impl InterpretedMotionSink for PlaysEverything {
    fn apply_motion(&mut self, _motion: u32, _speed: f32) -> bool {
        true
    }
    fn stop_motion(&mut self, _motion: u32) -> bool {
        true
    }
}

fn alignment(a: Quat, b: Quat) -> f32 {
    a.normalize().dot(b.normalize()).abs()
}

// ── PlayerMovementControllerTests ─────────────────────────────────────────

#[test]
fn a_movement_request_wakes_the_clock_unless_static() {
    let mut r = Rig::new();
    r.obj().clock.deactivate();
    r.c.apply_physics_state(PhysicsState::NONE);
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.perform_movement(&mut env, &MovementStruct::default());
    assert!(r.obj().clock.is_active());
    r.obj().clock.deactivate();
    r.c.apply_physics_state(PhysicsState::STATIC);
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.perform_movement(&mut env, &MovementStruct::default());
    assert!(!r.obj().clock.is_active());
}

#[test]
fn no_input_leaves_the_position() {
    let mut r = Rig::seeded();
    let result = r.update(0.016, input());
    eq_prec(96.0, result.position.x, 1);
    eq_prec(96.0, result.position.y, 1);
}

#[test]
fn a_resting_body_is_bit_stable() {
    let mut r = Rig::seeded();
    let settled = r.update(1.0 / 60.0, input());
    let (mut max_render, mut max_physics) = (0.0f32, 0.0f32);
    for _ in 0..600 {
        let x = r.update(1.0 / 60.0, input());
        max_render = max_render.max((x.render_position - settled.render_position).length());
        max_physics = max_physics.max((x.position - settled.position).length());
    }
    assert_eq!((max_render, max_physics), (0.0, 0.0));
}

#[test]
fn walking_then_stopping_settles_bit_stable() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    for _ in 0..30 {
        r.update(1.0 / 60.0, forward());
    }
    for _ in 0..30 {
        r.update(1.0 / 60.0, input());
    }
    let settled = r.update(1.0 / 60.0, input());
    let (mut max_pos, mut max_render) = (0.0f32, 0.0f32);
    for _ in 0..600 {
        let x = r.update(1.0 / 60.0, input());
        max_pos = max_pos.max((x.position - settled.position).length());
        max_render = max_render.max((x.render_position - settled.render_position).length());
    }
    assert_eq!((max_pos, max_render), (0.0, 0.0));
}

#[test]
fn forward_moves_in_the_facing_direction() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let ticks = (1.0 / PhysicsBody::MAX_QUANTUM).ceil() as i32 + 1;
    let mut result = r.update(PhysicsBody::MAX_QUANTUM, forward());
    for _ in 1..ticks {
        result = r.update(PhysicsBody::MAX_QUANTUM, forward());
    }
    assert!(result.position.x > 96.0 + 2.0, "x={}", result.position.x);
}

#[test]
fn an_attached_animation_without_travel_does_not_glide() {
    let mut r = Rig::seeded();
    let start = Vec3::new(96.0, 96.0, 50.0);
    r.c.set_yaw(0.0);
    r.attach(|_, _| {});
    let result = r.update(object_tick(), forward());
    assert_eq!(result.position, start);
    assert_eq!(r.c.body_velocity().x, 0.0);
    assert_eq!(r.c.body_velocity().y, 0.0);
}

#[test]
fn root_motion_moves_a_grounded_body_at_its_scale() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.c.object_scale = 2.0;
    r.attach(|_, f| f.origin = Vec3::new(0.0, 0.1, 0.0));
    let result = r.update(object_tick(), forward());
    eq_prec(96.0 + 0.2, result.position.x, 3);
    eq_prec(96.0, result.position.y, 3);
}

#[test]
fn root_motion_publishes_the_realised_velocity() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.c.object_scale = 2.0;
    r.attach(|_, f| f.origin = Vec3::new(0.0, 0.1, 0.0));
    r.update(object_tick(), forward());
    eq_prec(0.2 / object_tick(), r.c.cached_velocity().x, 2);
    eq_prec(0.0, r.c.cached_velocity().y, 2);
    eq_prec(0.0, r.c.cached_velocity().z, 2);
    assert_eq!(r.c.body_velocity().x, 0.0);
    assert_eq!(r.c.body_velocity().y, 0.0);
}

#[test]
fn sub_quantum_frames_advance_the_animation_once() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let advances = Rc::new(Cell::new(0));
    let a = Rc::clone(&advances);
    r.attach(move |_, f| {
        a.set(a.get() + 1);
        f.origin = Vec3::new(0.0, 0.1, 0.0);
    });
    let first = r.update(object_tick() * 0.5, forward());
    let second = r.update(object_tick() * 0.5, forward());
    assert_eq!(first.position, Vec3::new(96.0, 96.0, 50.0));
    eq_prec(96.1, second.position.x, 3);
    eq_prec(96.0, second.position.y, 3);
    assert_eq!(advances.get(), 1);
}

#[test]
fn root_motion_translates_before_it_turns() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.attach(|_, f| {
        f.origin = Vec3::new(0.0, 0.1, 0.0);
        f.orientation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
    });
    let result = r.update(object_tick(), input());
    eq_prec(96.1, result.position.x, 3);
    eq_prec(96.0, result.position.y, 3);
    eq_prec(std::f32::consts::FRAC_PI_2, r.c.yaw(), 3);
}

#[test]
fn root_motion_keeps_a_non_commuting_orientation() {
    let mut r = Rig::seeded();
    let initial = Quat::from_axis_angle(Vec3::X, 1.1);
    let delta = Quat::from_axis_angle(Vec3::Y, -0.9);
    r.c.set_body_orientation(initial);
    r.attach(move |_, f| f.orientation = delta);
    r.update(object_tick(), input());
    let a = alignment(initial * delta, r.c.body_orientation());
    assert!((0.99999..=1.00001).contains(&a));
    assert!(alignment(delta * initial, r.c.body_orientation()) < 0.999);
}

#[test]
fn multi_quantum_frames_compose_in_order() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let sample = Rc::new(Cell::new(0));
    let s = Rc::clone(&sample);
    r.attach(move |_, f| {
        if s.get() == 0 {
            f.orientation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
        } else {
            f.origin = Vec3::new(0.0, 0.1, 0.0);
        }
        s.set(s.get() + 1);
    });
    let result = r.update(PhysicsBody::MAX_QUANTUM * 2.0, input());
    eq_prec(96.0, result.position.x, 3);
    eq_prec(96.1, result.position.y, 3);
    eq_prec(std::f32::consts::FRAC_PI_2, r.c.yaw(), 3);
}

#[test]
fn exactly_the_minimum_quantum_waits() {
    let mut r = Rig::seeded();
    let advances = Rc::new(Cell::new(0));
    let a = Rc::clone(&advances);
    r.attach(move |_, f| {
        a.set(a.get() + 1);
        f.origin = Vec3::new(0.0, 1.0, 0.0);
    });
    let at = r.update(PhysicsBody::MIN_QUANTUM, input());
    assert_eq!(at.position, Vec3::new(96.0, 96.0, 50.0));
    assert_eq!(advances.get(), 0);
    r.update(0.001, input());
    assert_eq!(advances.get(), 1);
}

#[test]
fn a_large_frame_matches_separate_quanta() {
    let mut combined = Rig::seeded();
    let mut split = Rig::seeded();
    fn advance(dt: f32, f: &mut MotionDeltaFrame) {
        f.origin = Vec3::new(0.0, dt * 2.0, 0.0);
        f.orientation = Quat::from_axis_angle(Vec3::Z, dt * 0.7);
    }
    let ch = Rc::new(Cell::new(0));
    let sh = Rc::new(Cell::new(0));
    let (c2, s2) = (Rc::clone(&ch), Rc::clone(&sh));
    combined.attach_with_hooks(advance, move || c2.set(c2.get() + 1));
    split.attach_with_hooks(advance, move || s2.set(s2.get() + 1));
    combined.update(PhysicsBody::MAX_QUANTUM * 2.0, input());
    split.update(PhysicsBody::MAX_QUANTUM, input());
    split.update(PhysicsBody::MAX_QUANTUM, input());
    eq_prec(split.c.position().x, combined.c.position().x, 5);
    eq_prec(split.c.position().y, combined.c.position().y, 5);
    eq_prec(split.c.position().z, combined.c.position().z, 5);
    let a = alignment(split.c.body_orientation(), combined.c.body_orientation());
    assert!((0.99999..=1.00001).contains(&a));
    assert_eq!((ch.get(), sh.get()), (2, 2));
}

#[test]
fn a_hidden_tick_takes_hooks_but_does_not_advance_the_animation() {
    let mut r = Rig::seeded();
    let advances = Rc::new(Cell::new(0));
    let hooks = Rc::new(Cell::new(0));
    let (a, h) = (Rc::clone(&advances), Rc::clone(&hooks));
    r.attach_with_hooks(
        move |_, f| {
            a.set(a.get() + 1);
            f.origin = Vec3::new(0.0, 1.0, 0.0);
        },
        move || h.set(h.get() + 1),
    );
    r.tick_hidden(object_tick());
    assert_eq!(advances.get(), 0);
    assert_eq!(hooks.get(), 1);
}

#[test]
fn an_invalid_elapsed_time_only_reads() {
    let mut r = Rig::seeded();
    let initial_time = r.c.sim_time_seconds();
    let initial_yaw = r.c.yaw();
    let initial_position = r.c.position();
    let initial_motion = r.c.object().interp.raw_state.clone();
    let hostile = MovementInput {
        forward: true,
        turn_left: true,
        jump: true,
        run: true,
        ..input()
    };
    for elapsed in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY, -0.1, 0.0] {
        assert!(!r.update(elapsed, hostile).should_send_movement_event);
    }
    assert_eq!(r.c.sim_time_seconds(), initial_time);
    assert_eq!(r.c.yaw(), initial_yaw);
    assert_eq!(r.c.position(), initial_position);
    assert_eq!(r.c.object().interp.raw_state, initial_motion);
    r.update(object_tick(), input());
    r.update(object_tick(), input());
    assert!(r.c.advanced_object_quantum_last_tick());
    r.update(f32::NAN, hostile);
    assert!(!r.c.advanced_object_quantum_last_tick());
}

#[test]
fn an_invalid_hidden_elapsed_time_does_not_run_the_tail() {
    let mut r = Rig::seeded();
    let initial_time = r.c.sim_time_seconds();
    let tail_before = r.c.tail_passes();
    r.tick_hidden(f32::NAN);
    r.tick_hidden(f32::INFINITY);
    r.tick_hidden(-0.1);
    assert_eq!(r.c.sim_time_seconds(), initial_time);
    assert_eq!(r.c.tail_passes(), tail_before);
    assert!(!r.c.advanced_object_quantum_last_tick());
    r.tick_hidden(object_tick());
    r.tick_hidden(object_tick());
    assert!(r.c.advanced_object_quantum_last_tick());
    r.tick_hidden(f32::INFINITY);
    assert!(!r.c.advanced_object_quantum_last_tick());
}

#[test]
fn a_scripted_release_leaves_at_its_exact_extent() {
    for (exact, expected) in [(None, 0.6), (Some(0.37), 0.37)] {
        let mut r = Rig::seeded();
        r.update(0.6, jump(true));
        let released = r.update(
            object_tick(),
            MovementInput {
                jump: false,
                jump_extent: exact,
                ..input()
            },
        );
        eq_prec(expected, released.jump_extent.unwrap(), 3);
    }
}

#[test]
fn in_the_air_root_motion_turns_but_does_not_travel() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    let before = r.c.position();
    let before_orientation = r.c.body_orientation();
    let delta = Quat::from_axis_angle(Vec3::Y, 0.4);
    let advances = Rc::new(Cell::new(0));
    let a = Rc::clone(&advances);
    r.attach(move |_, f| {
        a.set(a.get() + 1);
        f.origin = Vec3::new(0.0, 10.0, 0.0);
        f.orientation = delta;
    });
    r.update(object_tick(), jump(false));
    assert!(r.c.is_airborne());
    eq_prec(before.x, r.c.position().x, 5);
    eq_prec(before.y, r.c.position().y, 5);
    let a = alignment(before_orientation * delta, r.c.body_orientation());
    assert!((0.99999..=1.00001).contains(&a));
    assert_eq!(advances.get(), 1);
}

#[test]
fn an_animated_turn_is_not_also_integrated() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.attach(|dt, f| {
        f.orientation = Quat::from_axis_angle(Vec3::Z, -std::f32::consts::FRAC_PI_2 * dt);
    });
    r.update(
        object_tick(),
        MovementInput {
            turn_right: true,
            ..input()
        },
    );
    eq_prec(-std::f32::consts::FRAC_PI_2 * object_tick(), r.c.yaw(), 4);
}

#[test]
fn a_sub_quantum_frame_interpolates_the_render_position() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let first = r.update(object_tick(), forward());
    assert!(first.position.x > 96.0);
    eq_prec(96.0, first.render_position.x, 4);
    let half = r.update(PhysicsBody::MIN_QUANTUM * 0.5, forward());
    eq_prec(first.position.x, half.position.x, 4);
    assert!(half.render_position.x > 96.0);
    assert!(half.render_position.x < first.position.x);
    let alpha = (PhysicsBody::MIN_QUANTUM * 0.5) / object_tick();
    let expected = 96.0 + (first.position.x - 96.0) * alpha;
    eq_prec(expected, half.render_position.x, 3);
}

#[test]
fn a_placement_resnaps_the_render_endpoints() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.update(object_tick(), forward());
    r.update(PhysicsBody::MIN_QUANTUM * 0.5, forward());
    let snapped = Vec3::new(120.0, 80.0, 50.0);
    r.seed(snapped, 0x0001, snapped);
    let result = r.update(PhysicsBody::MIN_QUANTUM * 0.5, input());
    assert_eq!(result.position, snapped);
    assert_eq!(result.render_position, snapped);
}

/// What the place to draw is blended from, for a caller blending at its
/// own time (a window's frame): the same blend gives the render position,
/// and a placement leaves both ends on the new place, so no blend crosses
/// the jump.
#[test]
fn the_render_steps_blend_to_the_render_position_and_a_placement_snaps_both() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.update(object_tick(), forward());
    let half = r.update(PhysicsBody::MIN_QUANTUM * 0.5, forward());
    let steps = r.c.render_steps();
    assert!(steps.current.x > steps.previous.x, "running east");
    let alpha = (steps.pending_seconds / f64::from(steps.step_seconds)) as f32;
    assert!(alpha > 0.0 && alpha < 1.0, "between steps: {alpha}");
    let blended = steps.previous.lerp(steps.current, alpha);
    assert!((blended - half.render_position).length() < 1e-5);

    let snapped = Vec3::new(120.0, 80.0, 50.0);
    r.seed(snapped, 0x0001, snapped);
    let steps = r.c.render_steps();
    assert_eq!((steps.previous, steps.current), (snapped, snapped));
}

#[test]
fn a_force_position_keeps_the_motion() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.update(object_tick(), forward());
    let velocity = r.c.body_velocity();
    assert!(velocity.length_squared() > 0.0);
    let corrected = Vec3::new(100.0, 98.0, 50.0);
    r.c.body_mut().snap_to_cell(0x0001, corrected, corrected);
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.commit_canonical_force_position_frame(&mut env);
    assert_eq!(r.c.position(), corrected);
    assert_eq!(r.c.render_position(), corrected);
    assert_eq!(r.c.body_velocity(), velocity);
}

#[test]
fn a_force_position_publishes_the_canonical_outdoor_cell() {
    let mut r = Rig::new();
    let world = Vec3::new(150.0, 193.0, 50.0);
    r.c.body_mut().snap_to_cell(0xA9B3_0038, world, world);
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.commit_canonical_force_position_frame(&mut env);
    assert_eq!(r.c.cell_id(), 0xA9B4_0031);
    assert_eq!(r.c.cell_position().obj_cell_id, r.c.cell_id());
    assert_eq!(
        r.c.cell_position().frame.origin,
        Vec3::new(150.0, 1.0, 50.0)
    );
    assert_eq!(r.c.position(), world);
}

#[test]
fn a_teleport_publishes_the_canonical_outdoor_cell() {
    let mut r = Rig::new();
    let world = Vec3::new(12.0, 12.0, 50.0);
    r.seed(world, 0xA9B4_0031, world);
    assert_eq!(r.c.cell_id(), 0xA9B4_0001);
    assert_eq!(r.c.cell_position().obj_cell_id, r.c.cell_id());
    assert_eq!(r.c.cell_position().frame.origin, world);
}

#[test]
fn a_huge_frame_is_discarded_and_resnaps_the_render_endpoints() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let advances = Rc::new(Cell::new(0));
    let hooks = Rc::new(Cell::new(0));
    let (a, h) = (Rc::clone(&advances), Rc::clone(&hooks));
    r.attach_with_hooks(
        move |_, f| {
            a.set(a.get() + 1);
            f.origin = Vec3::new(0.0, 0.1, 0.0);
        },
        move || h.set(h.get() + 1),
    );
    let moved = r.update(object_tick(), forward());
    let (a0, h0) = (advances.get(), hooks.get());
    let stale = r.update(PhysicsBody::HUGE_QUANTUM + 0.1, forward());
    eq_prec(moved.position.x, stale.position.x, 4);
    assert_eq!(stale.position, stale.render_position);
    assert_eq!((advances.get(), hooks.get()), (a0, h0));
}

#[test]
fn a_leftover_above_the_minimum_interpolates_over_its_interval() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let start = Vec3::new(96.0, 96.0, 50.0);
    let result = r.update(
        PhysicsBody::MAX_QUANTUM + PhysicsBody::MIN_QUANTUM,
        forward(),
    );
    let alpha = PhysicsBody::MIN_QUANTUM / PhysicsBody::MAX_QUANTUM;
    let expected = start.lerp(result.position, alpha);
    assert!((expected - result.render_position).abs().max_element() <= 1e-3);
}

#[test]
fn running_is_faster_than_walking() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    let walk = r.update(1.0, forward()).position.x - 96.0;
    r.seed(
        Vec3::new(96.0, 96.0, 50.0),
        0x0001,
        Vec3::new(96.0, 96.0, 50.0),
    );
    let run =
        r.update(
            1.0,
            MovementInput {
                forward: true,
                run: true,
                ..input()
            },
        )
        .position
        .x - 96.0;
    assert!(run > walk, "run {run} walk {walk}");
}

#[test]
fn turning_changes_the_yaw() {
    let mut r = Rig::seeded();
    let initial = r.c.yaw();
    r.update(
        0.5,
        MovementInput {
            turn_right: true,
            ..input()
        },
    );
    assert_ne!(r.c.yaw(), initial);
}

#[test]
fn starting_to_walk_changes_the_motion_state() {
    let mut r = Rig::seeded();
    r.update(0.016, input());
    assert!(r.update(0.016, forward()).motion_state_changed);
}

#[test]
fn a_jump_on_flat_ground_leaves_it() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    assert!(r.c.is_airborne());
    assert!(r.c.vertical_velocity() > 0.0);
}

#[test]
fn positions_are_sent_only_from_walkable_ground() {
    let mut r = Rig::seeded();
    assert!(r.c.can_send_position_event());
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    assert!(r.c.is_airborne());
    assert!(!r.c.can_send_position_event());
}

#[test]
fn the_jump_charge_is_reported_and_reset() {
    let mut r = Rig::seeded();
    assert_eq!(r.c.jump_charge(), Default::default());
    r.update(0.25, jump(true));
    assert!(r.c.jump_charge().is_charging);
    eq_prec(0.25, r.c.jump_charge().power, 3);
    r.update(0.016, jump(false));
    assert!(!r.c.jump_charge().is_charging);
    assert_eq!(r.c.jump_charge().power, 0.0);
}

#[test]
fn a_jump_rises_then_lands() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    let z1 = r.c.position().z;
    r.update(0.1, input());
    assert!(r.c.position().z > z1);
    for _ in 0..50 {
        r.update(0.05, input());
    }
    assert!(!r.c.is_airborne());
    eq_prec(50.0, r.c.position().z, 1);
}

#[test]
fn walking_off_a_ledge_falls_and_lands() {
    let mut heights = [0u8; 81];
    for x in 0..9 {
        for y in 0..9 {
            heights[x * 9 + y] = if x < 5 { 50 } else { 20 };
        }
    }
    let mut r = Rig::on(engine_with(heights));
    r.seed(
        Vec3::new(118.0, 96.0, 50.0),
        0x0001,
        Vec3::new(118.0, 96.0, 50.0),
    );
    r.c.set_yaw(0.0);
    r.update(0.05, forward());
    assert!(r.c.is_airborne());
    for _ in 0..60 {
        r.update(0.05, forward());
    }
    assert!(!r.c.is_airborne());
    eq_prec(20.0, r.c.position().z, 1);
}

#[test]
fn the_burden_gates_jumping() {
    let mut r = Rig::new();
    assert!(r.c.object().weenie.can_jump(1.0));
    r.c.set_character_burden(2.5);
    assert!(!r.c.object().weenie.can_jump(1.0));
    r.c.set_character_burden(0.5);
    assert!(r.c.object().weenie.can_jump(1.0));
}

#[test]
fn zero_stamina_zeroes_the_run_skill() {
    let mut r = Rig::new();
    r.c.set_character_skills(200, 100);
    let baseline = r.c.object().weenie.inq_run_rate().unwrap();
    r.c.set_character_stamina(0);
    assert!(r.c.object().weenie.inq_run_rate().unwrap() < baseline);
    r.c.set_character_stamina(-1);
    eq_prec(baseline, r.c.object().weenie.inq_run_rate().unwrap(), 4);
}

#[test]
fn an_overloaded_charge_reports_too_loaded_down() {
    let mut r = Rig::seeded();
    r.c.set_character_burden(2.5);
    r.update(0.016, jump(true));
    assert_eq!(r.refusal_texts(), vec![client_text::CANT_JUMP_LOAD]);
    assert!(!r.c.is_airborne());
}

#[test]
fn a_good_jump_reports_nothing() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    assert!(r.refusal_texts().is_empty());
    assert!(r.c.is_airborne());
}

#[test]
fn a_refusal_without_anyone_listening_is_fine() {
    let mut r = Rig::seeded();
    r.c.set_character_burden(2.5);
    r.update(0.016, jump(true));
}

#[test]
fn an_airborne_release_reports_cant_jump_in_the_air() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    assert!(r.c.is_airborne());
    r.update(0.05, input());
    r.refusal_texts();
    r.update(0.016, jump(true));
    assert!(r.refusal_texts().is_empty());
    assert!(r.c.jump_charge().is_charging);
    r.update(0.016, jump(true));
    r.update(0.016, jump(true));
    assert!(r.refusal_texts().is_empty());
    assert!(r.c.is_airborne());
    r.update(0.016, jump(false));
    assert_eq!(r.refusal_texts(), vec![client_text::CANT_JUMP_IN_AIR]);
    assert!(!r.c.jump_charge().is_charging);
}

#[test]
fn a_charge_held_through_landing_jumps_on_release() {
    let mut r = Rig::seeded();
    r.update(1.0, jump(true));
    r.update(0.016, jump(false));
    assert!(r.c.is_airborne());
    r.refusal_texts();
    let mut i = 0;
    while i < 120 && r.c.is_airborne() {
        r.update(0.05, jump(true));
        i += 1;
    }
    assert!(!r.c.is_airborne());
    assert!(r.c.jump_charge().is_charging);
    assert!(r.refusal_texts().is_empty());
    r.update(0.016, jump(false));
    assert!(r.refusal_texts().is_empty());
    assert!(r.c.is_airborne());
}

fn rig_with_host() -> Rig {
    let mut r = Rig::new();
    r.obj().position_attached = true;
    r.obj().token = ObjectToken {
        id: 0x5000_000A,
        incarnation: 1,
    };
    r
}

#[test]
fn a_teleport_arms_the_leash_outdoors() {
    let mut r = rig_with_host();
    r.seed(
        Vec3::new(96.0, 96.0, 50.0),
        0x0001,
        Vec3::new(96.0, 96.0, 50.0),
    );
    let c = r.c.object().position.constraint.unwrap();
    assert!(c.is_constrained);
    assert_eq!(c.constraint_distance_start, 10.0);
    assert_eq!(c.constraint_distance_max, 50.0);
    assert_eq!(c.constraint_pos.frame.origin, r.c.position());
    eq_prec(0.0, c.constraint_pos_offset, 3);
    assert_eq!(r.c.body_velocity(), Vec3::ZERO);
}

#[test]
fn an_indoor_teleport_uses_the_tighter_leash() {
    let mut r = rig_with_host();
    r.seed(
        Vec3::new(10.0, 10.0, 5.0),
        0x0100_0105,
        Vec3::new(10.0, 10.0, 5.0),
    );
    let c = r.c.object().position.constraint.unwrap();
    assert_eq!(c.constraint_distance_start, 5.0);
    assert_eq!(c.constraint_distance_max, 20.0);
}

#[test]
fn a_teleport_rearms_a_fully_constrained_leash() {
    let mut r = rig_with_host();
    r.seed(
        Vec3::new(96.0, 96.0, 50.0),
        0x0001,
        Vec3::new(96.0, 96.0, 50.0),
    );
    let anchor = r.c.cell_position();
    {
        let mut cx = r.c.object_mut().cx(&NoTargets);
        cx.constrain_to(anchor, 0.1, 0.2);
        let mut f = MotionDeltaFrame {
            origin: Vec3::new(5.0, 0.0, 0.0),
            ..MotionDeltaFrame::default()
        };
        cx.constraint_adjust_offset(&mut f, 0.1);
    }
    assert!(r.c.object().position.is_fully_constrained());
    r.seed(
        Vec3::new(150.0, 150.0, 50.0),
        0x0001,
        Vec3::new(150.0, 150.0, 50.0),
    );
    assert!(!r.c.object().position.is_fully_constrained());
    eq_prec(
        0.0,
        r.c.object()
            .position
            .constraint
            .unwrap()
            .constraint_pos_offset,
        3,
    );
}

#[test]
fn a_force_position_does_not_rearm_the_leash_or_touch_velocity() {
    let mut r = rig_with_host();
    let initial = Vec3::new(96.0, 96.0, 50.0);
    r.seed(initial, 0x0001, initial);
    r.update(object_tick(), forward());
    let velocity = r.c.body_velocity();
    assert_ne!(velocity, Vec3::ZERO);
    let anchor =
        r.c.object()
            .position
            .constraint
            .unwrap()
            .constraint_pos
            .frame
            .origin;
    let corrected = Vec3::new(150.0, 150.0, 50.0);
    r.c.body_mut().snap_to_cell(0x0001, corrected, corrected);
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.commit_canonical_force_position_frame(&mut env);
    assert_eq!(r.c.body_velocity(), velocity);
    let after =
        r.c.object()
            .position
            .constraint
            .unwrap()
            .constraint_pos
            .frame
            .origin;
    assert_eq!(after, anchor);
    assert_ne!(after, corrected);
}

#[test]
fn the_leash_tapers_a_large_step_on_the_second_tick() {
    let mut r = rig_with_host();
    r.seed(
        Vec3::new(96.0, 96.0, 50.0),
        0x0001,
        Vec3::new(96.0, 96.0, 50.0),
    );
    let anchor = r.c.cell_position();
    r.c.object_mut()
        .cx(&NoTargets)
        .constrain_to(anchor, 1.0, 10.0);
    r.attach(|_, f| f.origin = Vec3::new(5.0, 0.0, 0.0));
    let b1 = r.c.position();
    r.update(object_tick(), input());
    let d1 = (r.c.position() - b1).length();
    assert!(d1 > 4.0, "{d1}");
    let b2 = r.c.position();
    r.update(object_tick(), input());
    let d2 = (r.c.position() - b2).length();
    assert!(d2 > 0.0 && d2 < 4.0, "{d2}");
}

#[test]
fn an_overstrained_leash_holds_the_body_and_blocks_jumping() {
    let mut r = rig_with_host();
    r.seed(
        Vec3::new(96.0, 96.0, 50.0),
        0x0001,
        Vec3::new(96.0, 96.0, 50.0),
    );
    let anchor = r.c.cell_position();
    r.c.object_mut()
        .cx(&NoTargets)
        .constrain_to(anchor, 1.0, 2.0);
    r.attach(|_, f| f.origin = Vec3::new(10.0, 0.0, 0.0));
    r.update(object_tick(), input());
    assert!(r.c.object().position.is_fully_constrained());
    let result = r.c.object_mut().cx(&NoTargets).jump_is_allowed(1.0).0;
    assert_eq!(result, WeenieError::GENERAL_MOVEMENT_FAILURE);
}

#[test]
fn root_motion_walk_speed_is_exact_with_no_residual_velocity() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.attach(|_, f| f.origin = Vec3::new(0.0, 0.1, 0.0));
    let mut prev = r.c.position();
    for _ in 0..30 {
        let result = r.update(object_tick(), forward());
        eq_prec(0.1, result.position.distance(prev), 4);
        prev = result.position;
    }
    eq_prec(0.0, r.c.body_velocity().x, 5);
    eq_prec(0.0, r.c.body_velocity().y, 5);
}

#[test]
fn a_running_jump_keeps_residual_speed_after_landing_then_decays() {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    r.obj().sink = MotionSink::Custom(Box::new(PlaysEverything));
    r.attach(|_, _| {});
    r.update(
        1.0,
        MovementInput {
            forward: true,
            jump: true,
            ..input()
        },
    );
    r.update(0.016, forward());
    r.update(0.05, forward());
    assert!(r.c.is_airborne());
    let launch = Vec2::new(r.c.body_velocity().x, r.c.body_velocity().y).length();
    assert!(launch > 0.5, "{launch}");
    let mut i = 0;
    while i < 50 && r.c.is_airborne() {
        r.update(0.05, input());
        i += 1;
    }
    assert!(!r.c.is_airborne());
    r.update(object_tick(), input());
    let after = Vec2::new(r.c.body_velocity().x, r.c.body_velocity().y).length();
    assert!(after > 0.01, "{after}");
    for _ in 0..12 {
        r.update(object_tick(), input());
    }
    let settled = Vec2::new(r.c.body_velocity().x, r.c.body_velocity().y).length();
    assert!(settled < launch);
}

// ── LocalPlayerCycleQuantumTests ──────────────────────────────────────────

const TRAVEL_PER_STEP: f32 = 0.1;
const TURN_PER_STEP: f32 = std::f32::consts::TAU / 40.0;

fn standing() -> Rig {
    let mut r = Rig::seeded();
    r.c.set_yaw(0.0);
    for _ in 0..5 {
        r.update(object_tick(), input());
    }
    r
}

fn attach_cycle(r: &mut Rig, travel: f32, turn: f32) -> Rc<RefCell<Vec<String>>> {
    let log = Rc::new(RefCell::new(Vec::new()));
    let (l1, l2) = (Rc::clone(&log), Rc::clone(&log));
    r.attach_with_hooks(
        move |dt, out| {
            l1.borrow_mut().push(format!("advance {dt:.6}"));
            out.reset();
            out.origin = Vec3::new(travel, 0.0, 0.0);
            out.orientation = Quat::from_axis_angle(Vec3::Z, turn);
        },
        move || l2.borrow_mut().push("hooks".to_owned()),
    );
    r.obj().sink = MotionSink::Custom(Box::new(PlaysEverything));
    log
}

fn find_its_feet(r: &mut Rig) {
    for _ in 0..3 {
        r.update(object_tick(), input());
    }
}

fn flat(p: Vec3) -> Vec2 {
    Vec2::new(p.x, p.y)
}

fn normalize_turn(radians: f32) -> f32 {
    let mut turn = radians % std::f32::consts::TAU;
    if turn < 0.0 {
        turn += std::f32::consts::TAU;
    }
    turn
}

#[test]
fn the_character_travels_as_far_as_its_cycle_carries_it() {
    let mut r = standing();
    attach_cycle(&mut r, TRAVEL_PER_STEP, 0.0);
    find_its_feet(&mut r);
    let before = flat(r.c.position());
    for _ in 0..10 {
        r.update(object_tick(), input());
    }
    eq_prec(
        10.0 * TRAVEL_PER_STEP,
        flat(r.c.position()).distance(before),
        4,
    );
    assert!(r.c.can_send_position_event());
    eq_prec(50.0, r.c.position().z, 3);
}

#[test]
fn the_character_turns_as_far_as_its_cycle_turns_it() {
    let mut r = standing();
    attach_cycle(&mut r, 0.0, TURN_PER_STEP);
    for _ in 0..10 {
        r.update(object_tick(), input());
    }
    eq_prec(10.0 * TURN_PER_STEP, normalize_turn(r.c.yaw()), 4);
}

#[test]
fn a_cycle_that_carries_nothing_leaves_the_character() {
    let mut r = standing();
    attach_cycle(&mut r, 0.0, 0.0);
    for _ in 0..20 {
        r.update(object_tick(), forward());
    }
    eq_prec(0.0, flat(r.c.position()).distance(Vec2::new(96.0, 96.0)), 5);
    eq_prec(0.0, normalize_turn(r.c.yaw()), 5);
}

#[test]
fn the_cycle_is_asked_once_per_step_and_hooks_taken_after() {
    let mut r = standing();
    let log = attach_cycle(&mut r, TRAVEL_PER_STEP, TURN_PER_STEP);
    r.update(object_tick(), input());
    r.update(object_tick(), input());
    let step = format!("advance {:.6}", object_tick());
    assert_eq!(
        *log.borrow(),
        vec![step.clone(), "hooks".to_owned(), step, "hooks".to_owned()]
    );
}

#[test]
fn travel_is_measured_at_the_characters_size() {
    let mut half = standing();
    half.c.object_scale = 0.5;
    attach_cycle(&mut half, TRAVEL_PER_STEP, 0.0);
    find_its_feet(&mut half);
    let half_before = flat(half.c.position());
    let mut whole = standing();
    attach_cycle(&mut whole, TRAVEL_PER_STEP, 0.0);
    find_its_feet(&mut whole);
    let whole_before = flat(whole.c.position());
    for _ in 0..10 {
        half.update(object_tick(), input());
        whole.update(object_tick(), input());
    }
    eq_prec(
        flat(whole.c.position()).distance(whole_before) * 0.5,
        flat(half.c.position()).distance(half_before),
        4,
    );
}

#[test]
fn off_its_feet_a_character_turns_but_does_not_travel() {
    let mut r = standing();
    attach_cycle(&mut r, TRAVEL_PER_STEP, TURN_PER_STEP);
    r.update(object_tick(), input());
    assert!(!r.c.can_send_position_event());
    let before = flat(r.c.position());
    let turned_before = r.c.yaw();
    r.update(object_tick(), input());
    eq_prec(0.0, flat(r.c.position()).distance(before), 5);
    eq_prec(TURN_PER_STEP, normalize_turn(r.c.yaw() - turned_before), 4);
}

// ── LocalPlayerImmediatePositionTests (the decision) ──────────────────────

#[test]
fn an_immediate_position_is_the_exact_outbound_position_and_stamps_it() {
    let mut r = Rig::new();
    r.seed(
        Vec3::new(96.0, 97.0, 50.0),
        0xA9B4_0001,
        Vec3::new(96.0, 97.0, 50.0),
    );
    assert!(r.c.can_send_position_event());
    let pre_turn = r.c.cell_position();
    let rotation = Quat::from_axis_angle(Vec3::Z, 0.7).normalize();
    r.c.set_body_orientation(rotation);
    assert_ne!(rotation, pre_turn.frame.orientation);
    let outbound = r.c.try_get_outbound_position().unwrap();
    let immediate = r.c.immediate_position().unwrap();
    assert_eq!(immediate, outbound);
    eq_prec(rotation.w, immediate.frame.orientation.w, 6);
    eq_prec(rotation.z, immediate.frame.orientation.z, 6);
    let plane = r.c.contact_plane();
    let now = r.c.sim_time_seconds();
    r.c.note_position_sent(immediate, plane, now);
    let current = r.c.try_get_outbound_position().unwrap();
    assert!(!r.c.should_send_position_event(current, plane, now));
}

#[test]
fn an_immediate_position_needs_ground_contact() {
    let r = Rig::on(PhysicsEngine::new());
    assert!(r.c.immediate_position().is_none());
}

// ── DispatchedMotionDrainTests ────────────────────────────────────────────

fn drain_object() -> MovementObject<PlayerWeenie> {
    MovementObject::new(
        ObjectToken::default(),
        PhysicsBody::new(),
        PlayerWeenie::default(),
    )
}

#[test]
fn two_identical_queued_motions_both_leave() {
    let mut obj = drain_object();
    obj.interp.add_to_queue(0, READY, 0);
    obj.interp.add_to_queue(0, READY, 0);
    obj.cx(&NoTargets).complete_dispatched_motions();
    assert_eq!(obj.interp.pending_motion_count(), 0);
}

#[test]
fn one_pass_is_bounded() {
    let mut obj = drain_object();
    for _ in 0..70 {
        obj.interp.add_to_queue(0, READY, 0);
    }
    obj.cx(&NoTargets).complete_dispatched_motions();
    assert_eq!(obj.interp.pending_motion_count(), 6);
}

#[test]
fn an_object_that_animates_finishes_its_own_motions() {
    let mut obj = drain_object();
    obj.sink = MotionSink::Custom(Box::new(PlaysEverything));
    obj.interp.add_to_queue(0, READY, 0);
    obj.cx(&NoTargets).complete_dispatched_motions();
    assert_eq!(obj.interp.pending_motion_count(), 1);
}

#[test]
fn a_drain_without_a_body_takes_nothing() {
    let mut interp = crate::movement::StandaloneInterpreter::default();
    for _ in 0..70 {
        interp.interp.add_to_queue(0, READY, 0);
    }
    crate::movement::complete_dispatched_motions(&mut interp);
    assert_eq!(interp.interp.pending_motion_count(), 70);
}

// ── LocalPlayerOutboundCombatStyleTests (the raw state) ───────────────────

#[test]
fn the_movement_event_keeps_the_raw_combat_style() {
    for style in [0x8000_003C, 0x8000_003E, 0x8000_003F, 0x8000_0041] {
        let mut r = Rig::on(PhysicsEngine::new());
        r.obj().interp.raw_state.current_style = style;
        let movement = r.c.capture_movement_result(false);
        let outbound = build_raw_motion_state(&movement);
        assert_eq!(movement.current_style, style);
        assert_eq!(outbound.current_style, style);
    }
    let r = Rig::on(PhysicsEngine::new());
    let outbound = build_raw_motion_state(&r.c.capture_movement_result(false));
    assert_eq!(
        outbound.current_style,
        ac_motion::RawMotionState::default().current_style
    );
}
