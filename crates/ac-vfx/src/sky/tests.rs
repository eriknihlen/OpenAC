//! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/SkyPesFrameControllerTests.cs`.

use std::sync::{Arc, Mutex};

use ac_dat::objects::{AnimationHook, HookData};
use ac_dat::types::Frame;

use super::*;
use crate::desc::{EmitterDesc, EmitterDescRegistry, EmitterFlags, EmitterKind, ParticleType};
use crate::poses::EffectPoseSource;
use crate::random::{ConstantRandom, DotNetRandom};
use crate::script::{LoadedPhysicsScript, ScriptHookSink};

const AURORA_SETUP: u32 = 0x0200_0714;
const AURORA_SCRIPT: u32 = 0x3300_07DB;
const LIGHTNING_EMITTER: u32 = 0x3200_02C2;

struct TestSink<'a> {
    particles: &'a mut ParticleSystem,
    sink: &'a mut ParticleHookSink,
    poses: &'a EffectPoseRegistry,
    calls: &'a mut Vec<(u32, Vec3, AnimationHook)>,
}

impl ScriptHookSink for TestSink<'_> {
    fn on_script_hook(
        &mut self,
        _runner: &mut PhysicsScriptRunner,
        owner: u32,
        anchor: Vec3,
        hook: &AnimationHook,
    ) {
        self.sink.on_hook(
            self.particles,
            self.poses as &dyn EffectPoseSource,
            owner,
            hook,
        );
        self.calls.push((owner, anchor, *hook));
    }
}

struct Harness {
    resolved: Arc<Mutex<Vec<u32>>>,
    diagnostics: Vec<String>,
    stopped: Vec<u32>,
    calls: Vec<(u32, Vec3, AnimationHook)>,
    runner: PhysicsScriptRunner,
    controller: SkyPesFrameController,
    particles: ParticleSystem,
    sink: ParticleHookSink,
    poses: EffectPoseRegistry,
    disable_weather: bool,
}

impl Harness {
    fn new(hook: Option<AnimationHook>, hook_time: f64) -> Self {
        let mut registry = EmitterDescRegistry::new();
        registry.register(EmitterDesc {
            dat_id: LIGHTNING_EMITTER,
            particle_type: ParticleType::Still,
            flags: EmitterFlags(EmitterFlags::BILLBOARD),
            emitter_kind: EmitterKind::BirthratePerSec,
            max_particles: 2,
            initial_particles: 1,
            lifetime_min: 0.01,
            lifetime_max: 0.01,
            lifespan: 0.01,
            start_size: 1.0,
            end_size: 1.0,
            start_alpha: 1.0,
            end_alpha: 1.0,
            birthrate: 1000.0,
            ..EmitterDesc::default()
        });
        let resolved: Arc<Mutex<Vec<u32>>> = Arc::default();
        let record = Arc::clone(&resolved);
        let hook = hook.unwrap_or(AnimationHook {
            direction: 0,
            data: HookData::Sound { id: 0 },
        });
        let runner = PhysicsScriptRunner::new(
            move |id| {
                record.lock().unwrap().push(id);
                Ok(Some(Arc::new(LoadedPhysicsScript {
                    id,
                    hooks: vec![(hook_time, hook)],
                })))
            },
            Box::new(ConstantRandom(0.5)),
        );
        Self {
            resolved,
            diagnostics: Vec::new(),
            stopped: Vec::new(),
            calls: Vec::new(),
            runner,
            controller: SkyPesFrameController::new(),
            particles: ParticleSystem::new(registry, Box::new(DotNetRandom::new(42))),
            sink: ParticleHookSink::new(),
            poses: EffectPoseRegistry::new(),
            disable_weather: false,
        }
    }

    fn update(
        &mut self,
        day_fraction: f32,
        group: Option<&DayGroupData>,
        camera: Vec3,
        active: bool,
    ) {
        let mut targets = SkyTargets {
            scripts: &mut self.runner,
            sink: &mut self.sink,
            particles: &mut self.particles,
            poses: &mut self.poses,
            effects: None,
            stopped_owners: &mut self.stopped,
            diagnostics: &mut self.diagnostics,
        };
        self.controller.update(
            &mut targets,
            day_fraction,
            group,
            camera,
            active,
            self.disable_weather,
        );
        for owner in self.poses.take_changed_owners() {
            self.sink.on_pose_changed(owner);
        }
    }

    fn tick(&mut self, time: f64) {
        let mut sink = TestSink {
            particles: &mut self.particles,
            sink: &mut self.sink,
            poses: &self.poses,
            calls: &mut self.calls,
        };
        self.runner.tick(time, &mut sink).unwrap();
    }

    fn resolved(&self) -> Vec<u32> {
        self.resolved.lock().unwrap().clone()
    }
}

fn carrier(gfx_obj_id: u32, script_id: u32, pes_column: u32, properties: u32) -> SkyObjectData {
    SkyObjectData {
        gfx_obj_id,
        default_script_id: script_id,
        pes_object_id: pes_column,
        properties,
        ..SkyObjectData::default()
    }
}

fn aurora() -> SkyObjectData {
    carrier(AURORA_SETUP, AURORA_SCRIPT, AURORA_SCRIPT, 0)
}

fn group(objects: Vec<SkyObjectData>) -> DayGroupData {
    DayGroupData {
        name: "Test".into(),
        sky_objects: objects,
        ..DayGroupData::default()
    }
}

fn tweaked_sound() -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::SoundTweaked {
            sound_id: 0x0A00_038B,
            priority: 1.0,
            probability: 0.0,
            volume: 0.1,
        },
    }
}

#[test]
fn plays_once_per_slot_and_persists_across_equivalent_day_groups() {
    let mut h = Harness::new(None, 0.0);
    h.update(0.1, Some(&group(vec![aurora()])), Vec3::ZERO, true);
    assert_eq!(h.resolved(), [AURORA_SCRIPT]);
    assert_eq!(h.runner.active_script_count(), 1);

    h.update(0.9, Some(&group(vec![aurora()])), Vec3::splat(10.0), true);
    assert_eq!(h.runner.active_script_count(), 1);
}

#[test]
fn slot_identity_change_stops_the_old_script_and_plays_the_new() {
    let mut h = Harness::new(None, 0.0);
    h.update(0.1, Some(&group(vec![aurora()])), Vec3::ZERO, true);
    h.update(
        0.1,
        Some(&group(vec![carrier(
            0x0200_0589,
            0x3300_042C,
            0x3300_042C,
            0,
        )])),
        Vec3::ZERO,
        true,
    );
    assert_eq!(h.resolved(), [AURORA_SCRIPT, 0x3300_042C]);
    assert_eq!(h.runner.active_script_count(), 1);
}

#[test]
fn leaving_the_visibility_window_stops_the_script_and_reentry_replays() {
    let mut h = Harness::new(None, 0.0);
    let windowed = || {
        let mut c = aurora();
        c.begin_time = 0.2;
        c.end_time = 0.4;
        group(vec![c])
    };
    h.update(0.3, Some(&windowed()), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
    h.update(0.5, Some(&windowed()), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 0);
    h.update(0.25, Some(&windowed()), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
}

#[test]
fn setup_default_script_is_the_source_and_a_mismatched_pes_column_logs_once() {
    let mut h = Harness::new(None, 0.0);
    let g = group(vec![carrier(AURORA_SETUP, AURORA_SCRIPT, 0x3300_0999, 0)]);
    h.update(0.1, Some(&g), Vec3::ZERO, true);
    h.update(0.2, Some(&g), Vec3::ZERO, true);
    assert_eq!(h.resolved(), [AURORA_SCRIPT]);
    assert_eq!(h.diagnostics.len(), 1);
    assert!(h.diagnostics[0].contains("0x330007DB"));
    assert!(h.diagnostics[0].contains("0x33000999"));
}

#[test]
fn carriers_without_a_default_script_never_play() {
    let mut h = Harness::new(None, 0.0);
    h.update(
        0.1,
        Some(&group(vec![carrier(0x0100_15F0, 0, 0, 0)])),
        Vec3::ZERO,
        true,
    );
    assert!(h.resolved().is_empty());
    assert_eq!(h.runner.active_script_count(), 0);
}

#[test]
fn no_day_group_stops_everything() {
    let mut h = Harness::new(None, 0.0);
    h.update(0.1, Some(&group(vec![aurora()])), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
    h.update(0.1, None, Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 0);
}

#[test]
fn lightning_part_zero_uses_the_live_carrier_pose_and_stops_on_day_group_flip() {
    let create = AnimationHook {
        direction: 0,
        data: HookData::CreateParticle {
            emitter_info_id: LIGHTNING_EMITTER,
            part_index: 0,
            offset: Frame::default(),
            emitter_id: 1,
        },
    };
    let mut h = Harness::new(Some(create), 0.0);
    h.update(
        0.1,
        Some(&group(vec![aurora()])),
        Vec3::new(4.0, 5.0, 6.0),
        true,
    );
    h.tick(0.0);
    assert_eq!(h.particles.active_emitter_count(), 1);
    assert!(h.diagnostics.is_empty());
    assert!(h.sink.take_diagnostics().is_empty());

    h.update(0.1, None, Vec3::ZERO, true);
    h.tick(1.0);
    assert_eq!(h.runner.active_script_count(), 0);
    assert!(h.diagnostics.is_empty());
    assert!(h.sink.take_diagnostics().is_empty());
}

#[test]
fn persistent_weather_carrier_refreshes_sound_anchor_to_current_camera() {
    let mut h = Harness::new(Some(tweaked_sound()), 1.0);
    let initial = Vec3::new(10.0, 20.0, 30.0);
    let current = Vec3::new(410.0, 520.0, 630.0);
    h.update(0.3, Some(&group(vec![aurora()])), initial, true);
    h.update(0.4, Some(&group(vec![aurora()])), current, true);
    h.tick(1.0);
    assert_eq!(h.calls.len(), 1);
    assert_eq!(h.calls[0].2, tweaked_sound());
    assert_eq!(h.calls[0].1, current);
    assert_eq!(h.resolved().len(), 1);
}

#[test]
fn enclosed_frame_stops_sky_hooks_and_audio_while_preserving_other_owners() {
    const OTHER: u32 = 0x5000_0001;
    let mut h = Harness::new(Some(tweaked_sound()), 1.0);
    h.update(0.3, Some(&group(vec![aurora()])), Vec3::ZERO, true);
    assert!(h.runner.play_direct(OTHER, AURORA_SCRIPT));

    h.update(0.4, Some(&group(vec![aurora()])), Vec3::ZERO, false);
    h.tick(1.0);
    assert_eq!(h.calls.len(), 1);
    assert_eq!(h.calls[0].0, OTHER);
    assert_eq!(h.stopped, [0xF000_0000]);
    assert_eq!(h.runner.active_script_count(), 0);

    h.update(0.5, Some(&group(vec![aurora()])), Vec3::ONE, true);
    h.tick(2.0);
    assert_eq!(h.calls.len(), 2);
    assert_eq!(h.calls[1].0, 0xF000_0000);
}

#[test]
fn weather_carrier_suppressed_while_disable_most_weather_effects_is_on_never_plays() {
    let mut h = Harness::new(None, 0.0);
    h.disable_weather = true;
    h.update(
        0.1,
        Some(&group(vec![carrier(
            AURORA_SETUP,
            AURORA_SCRIPT,
            AURORA_SCRIPT,
            0x04,
        )])),
        Vec3::ZERO,
        true,
    );
    assert!(h.resolved().is_empty());
    assert_eq!(h.runner.active_script_count(), 0);
}

#[test]
fn weather_carrier_turning_the_option_on_stops_the_playing_script_and_its_audio() {
    let mut h = Harness::new(Some(tweaked_sound()), 1.0);
    let g = group(vec![carrier(
        AURORA_SETUP,
        AURORA_SCRIPT,
        AURORA_SCRIPT,
        0x04,
    )]);
    h.update(0.3, Some(&g), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
    h.disable_weather = true;
    h.update(0.4, Some(&g), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 0);
    assert_eq!(h.stopped, [0xF000_0000]);
}

#[test]
fn weather_carrier_turning_the_option_off_replays_the_script() {
    let mut h = Harness::new(None, 0.0);
    let g = group(vec![carrier(
        AURORA_SETUP,
        AURORA_SCRIPT,
        AURORA_SCRIPT,
        0x04,
    )]);
    h.disable_weather = true;
    h.update(0.1, Some(&g), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 0);
    h.disable_weather = false;
    h.update(0.2, Some(&g), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
}

#[test]
fn non_weather_carrier_unaffected_by_disable_most_weather_effects() {
    let mut h = Harness::new(None, 0.0);
    h.disable_weather = true;
    h.update(0.1, Some(&group(vec![aurora()])), Vec3::ZERO, true);
    assert_eq!(h.runner.active_script_count(), 1);
}

#[test]
fn owner_ids_mark_post_scene_objects() {
    assert_eq!(sky_owner_id(0, 0), 0xF000_0000);
    assert_eq!(sky_owner_id(5, 0x01), 0xF800_0005);
}
