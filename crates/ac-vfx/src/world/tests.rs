//! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Vfx/EntityScriptActivatorTests.cs`
//! (activation and removal; the retry-stage and rebind cases have no
//! counterpart here), plus the routing of script hooks.

use std::collections::HashMap;

use ac_core::sky::SkyObjectData;
use ac_dat::types::{Frame, Quaternion, Vector3};

use super::*;
use crate::controller::LiveRootState;
use crate::desc::{EmitterDesc, EmitterKind, ParticleType};
use crate::random::ConstantRandom;
use crate::script::LoadedPhysicsScript;

const SCRIPT: u32 = 0x3300_00AA;
const EMITTER: u32 = 0x3200_0100;
const CELL: u32 = 0x0101_0100;

#[derive(Default)]
struct Owners(HashMap<u32, LiveRootState>);

impl EffectOwnerQueries for Owners {
    fn live_root(&self, local_id: u32) -> Option<LiveRootState> {
        self.0.get(&local_id).copied()
    }
}

fn in_cell(owners: &mut Owners, local: u32, position: Vec3) {
    owners.0.insert(
        local,
        LiveRootState {
            in_cell: true,
            frozen: false,
            position,
        },
    );
}

fn persistent_emitter() -> EmitterDesc {
    EmitterDesc {
        dat_id: EMITTER,
        particle_type: ParticleType::Still,
        emitter_kind: EmitterKind::BirthratePerSec,
        max_particles: 4,
        initial_particles: 1,
        lifespan: 999.0,
        lifetime_min: 999.0,
        lifetime_max: 999.0,
        birthrate: 0.5,
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..EmitterDesc::default()
    }
}

fn create_hook(part_index: u32, origin: Vec3) -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::CreateParticle {
            emitter_info_id: EMITTER,
            part_index,
            offset: Frame {
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
            },
            emitter_id: 0,
        },
    }
}

fn world(scripts: Vec<(u32, Vec<(f64, AnimationHook)>)>) -> VfxWorld {
    let mut registry = EmitterDescRegistry::new();
    registry.register(persistent_emitter());
    let table: HashMap<u32, Arc<LoadedPhysicsScript>> = scripts
        .into_iter()
        .map(|(id, hooks)| (id, Arc::new(LoadedPhysicsScript { id, hooks })))
        .collect();
    let mut w = VfxWorld::from_parts(
        ParticleSystem::new(registry, Box::new(DotNetRandom::new(42))),
        PhysicsScriptRunner::new(
            move |id| Ok(table.get(&id).cloned()),
            Box::new(ConstantRandom(0.5)),
        ),
        PhysicsScriptTableResolver::new(|_| Ok(None)),
    );
    // A completed world view: interior cells are always in it.
    let v = w.visibility_mut();
    v.begin_frame(Vec3::ZERO);
    v.use_world_view();
    v.complete_frame();
    w
}

fn activation(
    local_id: u32,
    is_dat_static: bool,
    position: Vec3,
    script_id: u32,
) -> ActivationInfo {
    ActivationInfo {
        local_id,
        is_dat_static,
        position,
        rotation: Quat::IDENTITY,
        cell_id: CELL,
        script_id,
        part_transforms: Vec::new(),
        part_availability: None,
        effect_profile: None,
    }
}

fn single_position(w: &VfxWorld) -> Vec3 {
    let live: Vec<_> = w.particles().live_particles().collect();
    assert_eq!(live.len(), 1);
    live[0].0.particles[live[0].1].position
}

#[test]
fn activation_with_default_script_plays_it_on_the_local_owner_at_its_position() {
    let mut w = world(vec![(
        SCRIPT,
        vec![(0.0, create_hook(u32::MAX, Vec3::ZERO))],
    )]);
    let mut owners = Owners::default();
    in_cell(&mut owners, 0xCAFE, Vec3::new(1.0, 2.0, 3.0));

    w.activate_entity(activation(0xCAFE, false, Vec3::new(1.0, 2.0, 3.0), SCRIPT))
        .unwrap();
    assert_eq!(w.scripts().active_script_count(), 1);
    w.tick(&owners, 0.001, 0.001, 1.0).unwrap();

    assert_eq!(w.particles().active_emitter_count(), 1);
    let em = w.particles().emitters().next().unwrap();
    assert_eq!(em.attached_object_id, 0xCAFE);
    assert_eq!(single_position(&w), Vec3::new(1.0, 2.0, 3.0));
}

#[test]
fn activation_without_default_script_plays_nothing() {
    let mut w = world(vec![]);
    w.activate_entity(activation(0xCAFE, false, Vec3::ZERO, 0))
        .unwrap();
    assert_eq!(w.scripts().active_script_count(), 0);
}

#[test]
fn dat_static_owner_advances_without_a_live_record() {
    let mut w = world(vec![(
        SCRIPT,
        vec![(0.0, create_hook(u32::MAX, Vec3::ZERO))],
    )]);
    let mut info = activation(0x40A9_B401, true, Vec3::splat(5.0), SCRIPT);
    info.effect_profile = Some(EntityEffectProfile::dat_static(0, 0, 0));
    w.activate_entity(info).unwrap();

    w.tick(&Owners::default(), 0.001, 0.001, 1.0).unwrap();

    assert_eq!(single_position(&w), Vec3::splat(5.0));
}

#[test]
fn activation_publishes_the_rotation_for_the_hook_offset() {
    let mut w = world(vec![(
        SCRIPT,
        vec![(0.0, create_hook(u32::MAX, Vec3::new(1.0, 0.0, 0.0)))],
    )]);
    let mut owners = Owners::default();
    in_cell(&mut owners, 0xCAFE, Vec3::ZERO);
    let mut info = activation(0xCAFE, false, Vec3::ZERO, SCRIPT);
    // Yawed 90° left: local +X is world +Y.
    info.rotation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
    w.activate_entity(info).unwrap();

    w.tick(&owners, 0.001, 0.001, 1.0).unwrap();

    let p = single_position(&w);
    assert!(p.x.abs() < 0.01 && (p.y - 1.0).abs() < 0.01, "{p}");
}

#[test]
fn activation_passes_part_transforms_to_the_hook_sink() {
    let mut w = world(vec![(
        SCRIPT,
        vec![(0.0, create_hook(1, Vec3::new(1.0, 0.0, 0.0)))],
    )]);
    let mut owners = Owners::default();
    in_cell(&mut owners, 0xCAFE, Vec3::ZERO);
    let mut info = activation(0xCAFE, false, Vec3::ZERO, SCRIPT);
    info.part_transforms = vec![
        Mat4::IDENTITY,
        Mat4::from_translation(Vec3::new(0.0, 0.0, 1.0)),
    ];
    w.activate_entity(info).unwrap();

    w.tick(&owners, 0.001, 0.001, 1.0).unwrap();

    let p = single_position(&w);
    assert!(p.distance(Vec3::new(1.0, 0.0, 1.0)) < 0.01, "{p}");
}

#[test]
fn deactivation_stops_scripts_and_emitters() {
    for (local, is_static) in [(0xCAFE, false), (0x40A9_B402, true)] {
        let mut w = world(vec![(
            SCRIPT,
            vec![(0.0, create_hook(u32::MAX, Vec3::ZERO))],
        )]);
        let mut owners = Owners::default();
        in_cell(&mut owners, local, Vec3::ZERO);
        let mut info = activation(local, is_static, Vec3::ZERO, SCRIPT);
        info.effect_profile = is_static.then(|| EntityEffectProfile::dat_static(0, 0, 0));
        w.activate_entity(info).unwrap();
        w.tick(&owners, 0.001, 0.001, 1.0).unwrap();
        assert!(w.particles().active_emitter_count() > 0);

        w.deactivate_entity(local);

        assert_eq!(w.scripts().active_script_count(), 0);
        w.tick(&owners, 0.01, 0.011, 1.0).unwrap();
        assert_eq!(w.particles().active_emitter_count(), 0);
        assert_eq!(w.take_retired_emitters().len(), 1);
        assert!(w.poses().root_pose(local).is_none());
    }
}

#[test]
fn colliding_dat_static_ids_fail_at_activation() {
    let mut w = world(vec![(
        SCRIPT,
        vec![(1.0, create_hook(u32::MAX, Vec3::ZERO))],
    )]);
    w.activate_entity(activation(0x80A9_B400, true, Vec3::X, SCRIPT))
        .unwrap();
    let error = w
        .activate_entity(activation(
            0x80A9_B400,
            true,
            Vec3::new(2.0, 0.0, 0.0),
            SCRIPT,
        ))
        .unwrap_err();
    assert!(error.to_string().contains("unique"));
    assert_eq!(w.scripts().active_owner_count(), 1);
}

#[test]
fn script_hooks_the_effects_code_does_not_handle_go_to_the_presentation() {
    let sound = AnimationHook {
        direction: 0,
        data: HookData::Sound { id: 0x0A00_0001 },
    };
    let mut w = world(vec![(
        SCRIPT,
        vec![(0.0, sound), (0.0, create_hook(u32::MAX, Vec3::ZERO))],
    )]);
    let mut owners = Owners::default();
    in_cell(&mut owners, 0xCAFE, Vec3::new(1.0, 2.0, 3.0));
    w.activate_entity(activation(0xCAFE, false, Vec3::new(1.0, 2.0, 3.0), SCRIPT))
        .unwrap();

    w.tick(&owners, 0.001, 0.001, 1.0).unwrap();

    let hooks: Vec<_> = w
        .take_events()
        .into_iter()
        .filter_map(|e| match e {
            VfxEvent::ScriptHook {
                owner,
                position,
                hook,
            } => Some((owner, position, hook)),
            _ => None,
        })
        .collect();
    assert_eq!(hooks, [(0xCAFE, Vec3::new(1.0, 2.0, 3.0), sound)]);
}

#[test]
fn sky_scripts_run_on_synthetic_owners_and_report_their_stop() {
    let mut w = world(vec![(SCRIPT, vec![(0.0, create_hook(0, Vec3::ZERO))])]);
    let group = DayGroupData {
        sky_objects: vec![SkyObjectData {
            gfx_obj_id: 0x0200_0714,
            default_script_id: SCRIPT,
            pes_object_id: SCRIPT,
            ..SkyObjectData::default()
        }],
        ..DayGroupData::default()
    };
    let camera = Vec3::new(10.0, 20.0, 30.0);
    w.update_sky(0.5, Some(&group), camera, true, false);
    w.tick(&Owners::default(), 0.001, 0.001, 1.0).unwrap();

    let em = w.particles().emitters().next().unwrap();
    assert_eq!(em.attached_object_id, 0xF000_0000);
    assert_eq!(em.render_pass, crate::ParticleRenderPass::SkyPreScene);
    assert_eq!(em.anchor_pos, camera);

    w.update_sky(0.5, None, camera, true, false);
    assert!(
        w.take_events()
            .contains(&VfxEvent::SkyOwnerStopped { owner: 0xF000_0000 })
    );
}
