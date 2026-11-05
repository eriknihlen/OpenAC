//! Cases from OpenAC `tests/AcDream.Core.Tests/Vfx/ParticleHookSinkTests.cs`.
//! The two cases about emitter-death callbacks that throw or re-enter the
//! sink have no counterpart: deaths are a list the sink drains, not
//! callbacks.

use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};

use ac_dat::types::{Frame, Quaternion, Vector3};

use super::*;
use crate::desc::{EmitterDesc, EmitterDescRegistry, EmitterFlags, EmitterKind, ParticleType};
use crate::random::DotNetRandom;

const OWNER: u32 = 0xCAFE;

type PoseEntry = (Mat4, Vec<Mat4>, Option<u32>);

/// A pose source that records changes like the registry and can publish
/// from inside a root read.
#[derive(Default)]
struct MutablePoses {
    poses: RefCell<HashMap<u32, PoseEntry>>,
    changed: RefCell<Vec<u32>>,
    /// (owner whose root read triggers it, owner to move, new root)
    root_read_publish: Option<(u32, u32, Mat4)>,
    raised: Cell<bool>,
}

impl MutablePoses {
    fn publish(&self, owner: u32, root: Mat4, parts: &[Mat4]) {
        self.poses
            .borrow_mut()
            .insert(owner, (root, parts.to_vec(), None));
        self.changed.borrow_mut().push(owner);
    }

    fn publish_with_cell(&self, owner: u32, root: Mat4, cell: u32) {
        self.poses
            .borrow_mut()
            .insert(owner, (root, vec![Mat4::IDENTITY], Some(cell)));
        self.changed.borrow_mut().push(owner);
    }

    fn remove(&self, owner: u32) {
        self.poses.borrow_mut().remove(&owner);
        self.changed.borrow_mut().push(owner);
    }

    /// Deliver the recorded changes, as the registry's owner does.
    fn forward(&self, sink: &mut ParticleHookSink) {
        for owner in self.changed.borrow_mut().drain(..) {
            sink.on_pose_changed(owner);
        }
    }
}

impl EffectPoseSource for MutablePoses {
    fn root_pose(&self, owner: u32) -> Option<Mat4> {
        if let Some((trigger, target, root)) = self.root_read_publish
            && owner == trigger
            && !self.raised.get()
        {
            self.raised.set(true);
            let parts = self.poses.borrow().get(&target).map(|p| p.1.clone());
            self.publish(target, root, &parts.unwrap_or_default());
        }
        self.poses.borrow().get(&owner).map(|p| p.0)
    }

    fn part_pose(&self, owner: u32, part_index: i32) -> Option<Mat4> {
        let index = usize::try_from(part_index).ok()?;
        self.poses.borrow().get(&owner)?.1.get(index).copied()
    }

    fn cell_id(&self, owner: u32) -> Option<u32> {
        self.poses.borrow().get(&owner)?.2
    }
}

fn make_desc(
    id: u32,
    attach_local: bool,
    total_particles: i32,
    total_duration: f32,
) -> EmitterDesc {
    let attach = if attach_local {
        EmitterFlags::ATTACH_LOCAL
    } else {
        0
    };
    EmitterDesc {
        dat_id: id,
        particle_type: ParticleType::Still,
        flags: EmitterFlags(EmitterFlags::BILLBOARD | attach),
        emitter_kind: EmitterKind::BirthratePerSec,
        max_particles: 4,
        initial_particles: 1,
        total_particles,
        total_duration,
        lifetime_min: 0.05,
        lifetime_max: 0.05,
        lifespan: 0.05,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        birthrate: 1000.0,
        ..EmitterDesc::default()
    }
}

fn system_with(descs: Vec<EmitterDesc>) -> ParticleSystem {
    let mut registry = EmitterDescRegistry::new();
    for desc in descs {
        registry.register(desc);
    }
    ParticleSystem::new(registry, Box::new(DotNetRandom::new(42)))
}

fn harness(
    emitter_id: u32,
    attach_local: bool,
    total_particles: i32,
    total_duration: f32,
) -> (ParticleSystem, ParticleHookSink, MutablePoses) {
    let system = system_with(vec![make_desc(
        emitter_id,
        attach_local,
        total_particles,
        total_duration,
    )]);
    let poses = MutablePoses::default();
    poses.publish(OWNER, Mat4::IDENTITY, &[Mat4::IDENTITY]);
    let mut sink = ParticleHookSink::new();
    poses.forward(&mut sink);
    (system, sink, poses)
}

fn frame(origin: Vec3) -> Frame {
    Frame {
        origin: Vector3 {
            x: origin.x,
            y: origin.y,
            z: origin.z,
        },
        orientation: Quaternion {
            w: 1.0,
            x: 0.0,
            y: 0.0,
            z: 0.0,
        },
    }
}

fn create(emitter_info_id: u32, logical_id: u32, part_index: i32) -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::CreateParticle {
            emitter_info_id,
            part_index: part_index as u32,
            offset: frame(Vec3::ZERO),
            emitter_id: logical_id,
        },
    }
}

fn blocking(emitter_info_id: u32, logical_id: u32) -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::CreateBlockingParticle {
            emitter_info_id,
            part_index: 0,
            offset: frame(Vec3::ZERO),
            emitter_id: logical_id,
        },
    }
}

fn hook(data: HookData) -> AnimationHook {
    AnimationHook { direction: 0, data }
}

fn single_position(system: &ParticleSystem) -> Vec3 {
    let live: Vec<_> = system.live_particles().collect();
    assert_eq!(live.len(), 1);
    let (em, i) = live[0];
    em.particles[i].position
}

fn translation(x: f32, y: f32, z: f32) -> Mat4 {
    Mat4::from_translation(Vec3::new(x, y, z))
}

#[test]
fn refresh_with_attach_local_moves_existing_particle_to_current_pose() {
    const ID: u32 = 0x3200_0010;
    let (mut system, mut sink, poses) = harness(ID, true, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 0, 0));
    system.tick(0.01);
    assert_eq!(single_position(&system), Vec3::ZERO);

    poses.publish(OWNER, translation(5.0, 7.0, 0.0), &[Mat4::IDENTITY]);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    system.tick(0.01);

    assert_eq!(single_position(&system), Vec3::new(5.0, 7.0, 0.0));
}

#[test]
fn world_released_particles_keep_birth_position_while_new_anchor_moves() {
    const ID: u32 = 0x3200_0011;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 0, 0));
    system.tick(0.01);

    poses.publish(OWNER, translation(9.0, 0.0, 0.0), &[Mat4::IDENTITY]);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    system.tick(0.01);

    assert_eq!(single_position(&system), Vec3::ZERO);
}

#[test]
fn spawn_uses_current_indexed_part_and_root_in_order() {
    const ID: u32 = 0x3200_0030;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    // OpenAC's row-vector `RotationZ(90°) * Translation(10, 20, 30)`.
    let root = translation(10.0, 20.0, 30.0) * Mat4::from_rotation_z(std::f32::consts::FRAC_PI_2);
    poses.publish(OWNER, root, &[Mat4::IDENTITY, translation(0.0, 0.0, 2.0)]);

    let mut h = create(ID, 0, 1);
    if let HookData::CreateParticle { offset, .. } = &mut h.data {
        *offset = frame(Vec3::new(1.0, 0.0, 0.0));
        let q = Quat::from_axis_angle(Vec3::X, 1.1);
        offset.orientation = Quaternion {
            w: q.w,
            x: q.x,
            y: q.y,
            z: q.z,
        };
    }
    sink.on_hook(&mut system, &poses, OWNER, &h);
    system.tick(0.001);

    let p = single_position(&system);
    assert!((9.99..=10.01).contains(&p.x), "{p}");
    assert!((20.99..=21.01).contains(&p.y), "{p}");
    assert!((31.99..=32.01).contains(&p.z), "{p}");
}

#[test]
fn part_minus_one_uses_root_and_invalid_indexed_part_does_not_fall_back() {
    const ID: u32 = 0x3200_0031;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    poses.publish(
        OWNER,
        translation(3.0, 4.0, 5.0),
        &[translation(50.0, 0.0, 0.0)],
    );

    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 0, -1));
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 0, 99));
    system.tick(0.001);

    assert_eq!(single_position(&system), Vec3::new(3.0, 4.0, 5.0));
}

#[test]
fn normal_logical_id_replaces_blocking_suppresses_then_creates_after_retirement() {
    const ID: u32 = 0x3200_0040;
    const LOGICAL: u32 = 0x1234;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);

    sink.on_hook(&mut system, &poses, OWNER, &create(ID, LOGICAL, 0));
    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, LOGICAL));
    assert_eq!(system.active_emitter_count(), 1);

    sink.on_hook(&mut system, &poses, OWNER, &create(ID, LOGICAL, 0));
    system.tick(0.001);
    assert_eq!(system.active_emitter_count(), 1);

    let destroy = hook(HookData::DestroyParticle {
        emitter_id: LOGICAL,
    });
    sink.on_hook(&mut system, &poses, OWNER, &destroy);
    system.tick(0.001);
    assert_eq!(system.active_emitter_count(), 0);

    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, LOGICAL));
    assert_eq!(system.active_emitter_count(), 1);
}

#[test]
fn blocking_logical_id_zero_always_creates_anonymous_emitter() {
    const ID: u32 = 0x3200_0041;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, 0));
    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, 0));
    assert_eq!(system.active_emitter_count(), 2);
}

#[test]
fn stopped_logical_emitter_remains_blocking_until_final_particle_retires() {
    const ID: u32 = 0x3200_0042;
    const LOGICAL: u32 = 0x777;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, LOGICAL, 0));
    let stop = hook(HookData::StopParticle {
        emitter_id: LOGICAL,
    });
    sink.on_hook(&mut system, &poses, OWNER, &stop);
    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, LOGICAL));
    assert_eq!(system.active_emitter_count(), 1);
}

#[test]
fn stopped_logical_emitter_is_still_replaced_by_a_normal_create() {
    const ID: u32 = 0x3200_0043;
    const LOGICAL: u32 = 0x778;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, LOGICAL, 0));
    let stop = hook(HookData::StopParticle {
        emitter_id: LOGICAL,
    });
    sink.on_hook(&mut system, &poses, OWNER, &stop);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, LOGICAL, 0));
    system.tick(0.001);
    assert_eq!(system.active_emitter_count(), 1);
}

#[test]
fn natural_retirement_prunes_logical_id_for_blocking_reuse() {
    const ID: u32 = 0x3200_0020;
    const LOGICAL: u32 = 0xABCD;
    let (mut system, mut sink, poses) = harness(ID, false, 1, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, LOGICAL));
    for _ in 0..5 {
        system.tick(0.05);
    }
    assert_eq!(system.active_emitter_count(), 0);

    sink.on_hook(&mut system, &poses, OWNER, &blocking(ID, LOGICAL));
    assert_eq!(system.active_emitter_count(), 1);
    assert_eq!(sink.logical_emitter_count(), 1);
}

#[test]
fn missing_emitter_asset_reports_diagnostic_without_fabricating_effect() {
    const MISSING: u32 = 0x32DE_AD00;
    let (mut system, mut sink, poses) = harness(0x3200_0020, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(MISSING, 7, 0));
    assert_eq!(system.active_emitter_count(), 0);
    let diagnostics = sink.take_diagnostics();
    assert_eq!(diagnostics.len(), 1);
    assert!(diagnostics[0].contains("0x32DEAD00"));
    assert!(diagnostics[0].contains(&format!("0x{OWNER:08X}")));
}

#[test]
fn missing_emitter_asset_reports_only_once_across_many_owners() {
    const MISSING: u32 = 0x32DE_AD00;
    const SECOND: u32 = OWNER + 1;
    let mut system = system_with(vec![]);
    let poses = MutablePoses::default();
    poses.publish(OWNER, Mat4::IDENTITY, &[Mat4::IDENTITY]);
    poses.publish(SECOND, Mat4::IDENTITY, &[Mat4::IDENTITY]);
    let mut sink = ParticleHookSink::new();

    sink.on_hook(&mut system, &poses, OWNER, &create(MISSING, 0, 0));
    sink.on_hook(&mut system, &poses, SECOND, &create(MISSING, 0, 0));
    sink.on_hook(&mut system, &poses, OWNER, &create(MISSING, 0, 0));

    assert_eq!(system.emitters().count(), 0);
    let diagnostics = sink.take_diagnostics();
    assert_eq!(diagnostics.len(), 1);
    assert!(diagnostics[0].contains("0x32DEAD00"));
}

#[test]
fn missing_live_pose_hides_presentation_without_ending_emitter_lifetime() {
    const ID: u32 = 0x3200_0055;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 11, 0));
    let handle = system.emitters().next().unwrap().handle;
    assert!(system.emitter(handle).unwrap().presentation_visible);

    poses.remove(OWNER);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(system.active_emitter_count(), 1);
    assert!(!system.emitter(handle).unwrap().presentation_visible);

    poses.publish(OWNER, translation(8.0, 0.0, 0.0), &[Mat4::IDENTITY]);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert!(system.emitter(handle).unwrap().presentation_visible);
}

#[test]
fn pending_projection_hides_presentation_while_continuing_live_anchor_refresh() {
    const ID: u32 = 0x3200_0056;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.05);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 12, 0));
    let handle = system.emitters().next().unwrap().handle;
    let emitted_before_pause = system.emitter(handle).unwrap().total_emitted;
    let spawned_at = system.emitter(handle).unwrap().particles[0].spawned_at;

    sink.set_entity_presentation_visible(&mut system, OWNER, false);
    poses.publish(OWNER, translation(12.0, 3.0, 0.0), &[Mat4::IDENTITY]);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    system.tick(1.0);

    let em = system.emitter(handle).unwrap();
    assert_eq!(system.active_emitter_count(), 1);
    assert_eq!(em.anchor_pos, Vec3::new(12.0, 3.0, 0.0));
    assert_eq!(em.total_emitted, emitted_before_pause);
    assert_eq!(em.particles[0].spawned_at, spawned_at);
    assert_eq!(em.last_emit_offset, Vec3::new(12.0, 3.0, 0.0));
    assert!(!em.simulation_enabled);
    assert!(!em.presentation_visible);

    sink.set_entity_presentation_visible(&mut system, OWNER, true);
    sink.refresh_attached_emitters(&mut system, &poses);
    let em = system.emitter(handle).unwrap();
    assert!(em.simulation_enabled);
    assert!(em.presentation_visible);
    assert_eq!(em.particles[0].spawned_at, spawned_at);

    system.tick(0.01);
    assert_eq!(system.active_emitter_count(), 1);
    system.tick(0.01);
    assert_eq!(system.active_emitter_count(), 0);
}

#[test]
fn logical_teardown_removes_emitter_that_was_spatially_paused() {
    const ID: u32 = 0x3200_0057;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 13, 0));
    sink.set_entity_presentation_visible(&mut system, OWNER, false);

    sink.stop_all_for_entity(&mut system, OWNER, false);

    assert_eq!(system.active_emitter_count(), 0);
    assert_eq!(sink.take_retired_emitters(&mut system).len(), 1);
}

#[test]
fn logical_teardown_stops_every_emitter_and_clears_owner_bookkeeping() {
    const ID: u32 = 0x3200_005B;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 31, 0));
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 32, 0));

    sink.stop_all_for_entity(&mut system, OWNER, false);

    assert_eq!(sink.take_retired_emitters(&mut system).len(), 2);
    assert_eq!(system.active_emitter_count(), 0);
    assert_eq!(sink.active_binding_count(), 0);
    assert_eq!(sink.logical_emitter_count(), 0);
    assert_eq!(sink.tracked_owner_count(), 0);

    sink.stop_all_for_entity(&mut system, OWNER, false);
    assert!(sink.take_retired_emitters(&mut system).is_empty());
}

#[test]
fn examination_object_policy_applies_to_existing_and_future_emitters_and_tears_down() {
    const ID: u32 = 0x3200_0058;
    let (mut system, mut sink, poses) = harness(ID, false, 0, 0.0);
    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 21, 0));

    sink.set_entity_examination_object(&mut system, OWNER, true);
    assert_eq!(
        system.emitters().next().unwrap().visibility_policy,
        ParticleVisibilityPolicy::Examination
    );

    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 22, 0));
    assert!(
        system
            .emitters()
            .all(|e| e.visibility_policy == ParticleVisibilityPolicy::Examination)
    );
    assert_eq!(sink.examination_owner_count(), 1);

    sink.stop_all_for_entity(&mut system, OWNER, false);
    assert_eq!(sink.examination_owner_count(), 0);
}

#[test]
fn pose_change_workset_refreshes_only_changed_owners_and_visibility_edges() {
    const ID: u32 = 0x3200_0059;
    let mut system = system_with(vec![make_desc(ID, true, 0, 0.0)]);
    let poses = MutablePoses::default();
    let mut sink = ParticleHookSink::new();

    for owner in 1..=2_000u32 {
        poses.publish(owner, Mat4::IDENTITY, &[Mat4::IDENTITY]);
        poses.forward(&mut sink);
        sink.on_hook(&mut system, &poses, owner, &create(ID, 0, 0));
    }

    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 0);
    assert_eq!(sink.last_refresh_binding_visit_count(), 0);

    poses.publish(777, translation(1.0, 2.0, 3.0), &[Mat4::IDENTITY]);
    poses.publish(777, translation(7.0, 8.0, 9.0), &[Mat4::IDENTITY]);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 1);
    assert_eq!(sink.last_refresh_binding_visit_count(), 1);
    let moved = system
        .emitters()
        .find(|e| e.attached_object_id == 777)
        .unwrap();
    assert_eq!(moved.anchor_pos, Vec3::new(7.0, 8.0, 9.0));

    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 0);

    sink.set_entity_presentation_visible(&mut system, 777, false);
    sink.set_entity_presentation_visible(&mut system, 777, true);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 1);
    assert!(
        system
            .emitters()
            .find(|e| e.attached_object_id == 777)
            .unwrap()
            .presentation_visible
    );
}

#[test]
fn pose_change_raised_during_refresh_is_retained_for_next_refresh() {
    const ID: u32 = 0x3200_005A;
    let mut system = system_with(vec![make_desc(ID, true, 0, 0.0)]);
    let mut poses = MutablePoses::default();
    poses.publish(1, Mat4::IDENTITY, &[Mat4::IDENTITY]);
    poses.publish(2, Mat4::IDENTITY, &[Mat4::IDENTITY]);
    let mut sink = ParticleHookSink::new();
    poses.forward(&mut sink);
    sink.on_hook(&mut system, &poses, 1, &create(ID, 0, 0));
    sink.on_hook(&mut system, &poses, 2, &create(ID, 0, 0));

    poses.root_read_publish = Some((1, 2, translation(20.0, 0.0, 0.0)));
    poses.publish(1, translation(10.0, 0.0, 0.0), &[Mat4::IDENTITY]);

    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 1);
    poses.forward(&mut sink);
    sink.refresh_attached_emitters(&mut system, &poses);
    assert_eq!(sink.last_refresh_owner_visit_count(), 1);
    let second = system
        .emitters()
        .find(|e| e.attached_object_id == 2)
        .unwrap();
    assert_eq!(second.anchor_pos, Vec3::new(20.0, 0.0, 0.0));
}

#[test]
fn emitter_keeps_its_owner_cell_and_stays_renderable_with_no_other_owner_presence() {
    const ID: u32 = 0x3200_0777;
    const CELL: u32 = 0x8A02_015E;
    let mut system = system_with(vec![make_desc(ID, false, 0, 0.0)]);
    let poses = MutablePoses::default();
    poses.publish_with_cell(OWNER, translation(3.0, 4.0, 5.0), CELL);
    let mut sink = ParticleHookSink::new();
    poses.forward(&mut sink);
    let visible: HashSet<u32> = [CELL].into();
    let owners: HashSet<u32> = [OWNER].into();

    sink.on_hook(&mut system, &poses, OWNER, &create(ID, 7, 0));
    let handle = system.emitters().next().unwrap().handle;
    assert_eq!(system.emitter(handle).unwrap().owner_cell_id, CELL);

    sink.refresh_attached_emitters(&mut system, &poses);
    system.apply_retail_view(Vec3::new(3.0, 4.0, 5.0), &visible, true, 1.0);
    let em = system.emitter(handle).unwrap();
    assert!(em.presentation_visible);
    assert!(em.view_eligible);
    assert_eq!(em.render_pass, ParticleRenderPass::Scene);
    let mut by_owner = Vec::new();
    system.copy_renderable_emitters_for_owners(
        ParticleRenderPass::Scene,
        &owners,
        false,
        &mut by_owner,
        None,
        Default::default(),
    );
    assert_eq!(by_owner.len(), 1);
    let mut in_cell = Vec::new();
    system.copy_renderable_emitters_in_cell(ParticleRenderPass::Scene, CELL, &mut in_cell);
    assert_eq!(in_cell.len(), 1);
    assert_eq!(in_cell[0], handle);

    sink.set_entity_presentation_visible(&mut system, OWNER, false);
    system.copy_renderable_emitters_in_cell(ParticleRenderPass::Scene, CELL, &mut in_cell);
    assert!(in_cell.is_empty());
    sink.set_entity_presentation_visible(&mut system, OWNER, true);
    sink.refresh_attached_emitters(&mut system, &poses);
    system.apply_retail_view(Vec3::new(3.0, 4.0, 5.0), &visible, true, 1.0);
    system.copy_renderable_emitters_in_cell(ParticleRenderPass::Scene, CELL, &mut in_cell);
    assert_eq!(in_cell.len(), 1);
    assert_eq!(system.emitter(handle).unwrap().owner_cell_id, CELL);
}

#[test]
fn decompose_matches_rotation_and_rejects_skew() {
    let q = Quat::from_axis_angle(Vec3::new(1.0, 2.0, 3.0).normalize(), 0.7);
    let m = Mat4::from_scale_rotation_translation(Vec3::new(2.0, 3.0, 0.5), q, Vec3::ONE);
    let r = decompose_rotation(&m).unwrap();
    assert!(r.dot(q).abs() > 0.9999);
    // A collapsed axis is rebuilt, not refused.
    let flat = Mat4::from_scale(Vec3::new(1.0, 1.0, 0.0));
    assert!(decompose_rotation(&flat).is_some());
    // A skew is not a rotation.
    let skew = Mat4::from_cols_array(&[
        1.0, 0.0, 0.0, 0.0, 0.9, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
    ]);
    assert!(decompose_rotation(&skew).is_none());
}
