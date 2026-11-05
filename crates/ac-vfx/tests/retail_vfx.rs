//! Effects against the retail dat files: every particle emitter and every
//! physics script runs for a few seconds without NaN or runaway counts, and
//! OpenAC's installed-dat golden values hold (from
//! `tests/AcDream.App.Tests/Rendering/RetailParticleGeometryClassifierTests.cs`).
//! These tests need the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::BTreeSet;
use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_dat::objects::{HookData, PhysicsScript};
use ac_vfx::{
    ActivationInfo, EffectOwnerQueries, EmitterDescFailureKind, EmitterDescRegistry, LiveRootState,
    ParticleCamera, ParticleFrame, ParticleGeometryKind, ParticleGfxCache, ParticleRenderPass,
    ParticleSystem, PhysicsScriptTableResolver, SpawnOptions, VfxDats, VfxWorld,
    build_particle_frame, load_physics_script,
};
use glam::{Mat4, Quat, Vec3};

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn dats() -> Arc<DatCollection> {
    Arc::new(DatCollection::open(dat_dir()).expect("open the retail dats"))
}

fn portal_ids(dats: &DatCollection, high_byte: u32) -> Vec<u32> {
    dats.portal
        .entries()
        .map(|e| e.expect("directory entry").id)
        .filter(|id| id & 0xFF00_0000 == high_byte)
        .collect()
}

struct NoLiveOwners;

impl EffectOwnerQueries for NoLiveOwners {
    fn live_root(&self, _: u32) -> Option<LiveRootState> {
        None
    }
}

fn assert_sane(sys: &ParticleSystem, context: &str) {
    for em in sys.emitters() {
        assert!(
            em.active_count >= 0 && em.active_count <= em.desc.max_particles.max(1),
            "{context}: emitter 0x{:08X} counts {} of {}",
            em.desc.dat_id,
            em.active_count,
            em.desc.max_particles
        );
        for p in em.particles.iter().filter(|p| p.alive) {
            assert!(
                p.position.is_finite() && p.size.is_finite() && p.age.is_finite(),
                "{context}: emitter 0x{:08X} particle {p:?}",
                em.desc.dat_id
            );
        }
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_particle_emitter_simulates_without_nan_or_runaway_counts() {
    let dats = dats();
    let ids = portal_ids(&dats, 0x3200_0000);
    assert_eq!(ids.len(), 2051, "emitter count (golden sweep)");

    let mut system = ParticleSystem::new(
        EmitterDescRegistry::with_dats(dats.clone()),
        Box::new(ac_vfx::DotNetRandom::new(7)),
    );
    let mut hardwareless = BTreeSet::new();
    let mut spawned = 0;
    for (i, &id) in ids.iter().enumerate() {
        // Spread them out, each turned a little.
        let anchor = Vec3::new((i % 50) as f32 * 4.0, (i / 50) as f32 * 4.0, 10.0);
        let options = SpawnOptions {
            rotation: Quat::from_axis_angle(Vec3::Z, i as f32 * 0.1),
            ..SpawnOptions::default()
        };
        match system.spawn_emitter_by_id(id, anchor, options) {
            Ok(_) => spawned += 1,
            Err(failure) => {
                assert_eq!(
                    failure.kind,
                    EmitterDescFailureKind::InvalidHardwareGfxObjId,
                    "0x{id:08X}: {failure:?}"
                );
                hardwareless.insert(id);
            }
        }
    }
    // The authored emitters without a hardware particle model (the game
    // skips them). OpenAC's render digest names the ones met in the world
    // (0x320002D6, 0x320003B6..=0x320003B9); the whole dat has these 29
    // (the hardware model id is part of the golden-checked decode).
    for met in [
        0x3200_02D6,
        0x3200_03B6,
        0x3200_03B7,
        0x3200_03B8,
        0x3200_03B9,
    ] {
        assert!(hardwareless.contains(&met), "0x{met:08X}");
    }
    assert_eq!(
        hardwareless.into_iter().collect::<Vec<_>>(),
        [
            0x3200_0139,
            0x3200_013E,
            0x3200_0140,
            0x3200_02D6,
            0x3200_03B6,
            0x3200_03B7,
            0x3200_03B8,
            0x3200_03B9,
            0x3200_03BA,
            0x3200_0438,
            0x3200_049E,
            0x3200_050D,
            0x3200_0529,
            0x3200_059F,
            0x3200_05C8,
            0x3200_05C9,
            0x3200_05D3,
            0x3200_05D4,
            0x3200_05D5,
            0x3200_069B,
            0x3200_069C,
            0x3200_069D,
            0x3200_069E,
            0x3200_069F,
            0x3200_0736,
            0x3200_0737,
            0x3200_0A17,
            0x3200_0A18,
            0x3200_0A19,
        ]
    );
    assert_eq!(spawned, 2051 - 29);

    // Five seconds at 30 frames a second, the emitters drifting.
    let handles: Vec<_> = system
        .emitters()
        .map(|e| (e.handle, e.anchor_pos))
        .collect();
    for frame in 0..150 {
        let t = frame as f32 / 30.0;
        for (handle, base) in &handles {
            system.update_emitter_anchor(*handle, *base + Vec3::new(t, 0.0, 0.0), None);
        }
        system.tick(1.0 / 30.0);
        if frame % 15 == 0 {
            assert_sane(&system, &format!("frame {frame}"));
        }
    }
    assert_sane(&system, "end");
    assert!(system.active_particle_count() > 0);

    // The draw lists build for all of them, finite.
    let camera = ParticleCamera {
        position: Vec3::new(100.0, -50.0, 20.0),
        right: Vec3::X,
        up: Vec3::Z,
    };
    let mut gfx = ParticleGfxCache::new(Some(dats.clone() as Arc<dyn VfxDats>));
    let mut frame = ParticleFrame::default();
    build_particle_frame(
        system.renderable_emitters(ParticleRenderPass::Scene),
        &camera,
        &mut gfx,
        &mut frame,
    );
    frame.sort_far_to_near();
    assert_eq!(
        frame.billboards.len() + frame.meshes.len(),
        system.live_particles().count()
    );
    for b in &frame.billboards {
        assert!(
            b.center.is_finite() && b.axis_x.is_finite() && b.axis_y.is_finite(),
            "{b:?}"
        );
    }
    for m in &frame.meshes {
        assert!(m.model.is_finite(), "{m:?}");
    }
    assert!(gfx.take_diagnostics().is_empty());
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_physics_script_loads_and_runs_without_nan_or_runaway_counts() {
    let dats = dats();
    let ids = portal_ids(&dats, 0x3300_0000);
    assert_eq!(ids.len(), 4248, "physics script count (golden sweep)");

    let mut hooks = 0;
    let mut stored_out_of_order = Vec::new();
    for &id in &ids {
        let script = load_physics_script(dats.as_ref(), id)
            .unwrap_or_else(|e| panic!("0x{id:08X}: {e}"))
            .unwrap_or_else(|| panic!("0x{id:08X} missing"));
        assert!(script.hooks.windows(2).all(|w| w[0].0 <= w[1].0));
        hooks += script.hooks.len();
        let stored: PhysicsScript = dats.get(id).unwrap().unwrap();
        if !stored
            .script_data
            .windows(2)
            .all(|w| w[0].start_time <= w[1].start_time)
        {
            stored_out_of_order.push(id);
        }
    }
    assert_eq!(hooks, 12_268);
    // Every stored script is already in start-time order, so the loader's
    // sort (stable here, .NET's unstable one in OpenAC) never reorders.
    assert!(stored_out_of_order.is_empty(), "{stored_out_of_order:08X?}");

    // Every script on its own dat owner, in batches; a part pose for every
    // part index a hook may name.
    let parts = vec![Mat4::IDENTITY; 64];
    let (mut emitters_seen, mut peak_particles) = (0, 0);
    for (batch, chunk) in ids.chunks(500).enumerate() {
        let mut world = VfxWorld::new(dats.clone(), 11);
        let v = world.visibility_mut();
        v.begin_frame(Vec3::ZERO);
        v.use_world_view();
        v.complete_frame();
        for (i, &id) in chunk.iter().enumerate() {
            let local = 0x4000_0000 + i as u32 + 1;
            world
                .activate_entity(ActivationInfo {
                    local_id: local,
                    is_dat_static: true,
                    position: Vec3::new((i % 25) as f32 * 8.0, (i / 25) as f32 * 8.0, 0.0),
                    rotation: Quat::IDENTITY,
                    cell_id: 0xA9B4_0100,
                    script_id: id,
                    part_transforms: parts.clone(),
                    part_availability: None,
                    effect_profile: Some(ac_vfx::EntityEffectProfile::dat_static(0, 0, 0)),
                })
                .unwrap();
        }
        for frame in 1..=60 {
            let t = f64::from(frame) / 20.0;
            world.tick(&NoLiveOwners, 1.0 / 20.0, t, 1.0).unwrap();
            peak_particles = peak_particles.max(world.particles().live_particles().count());
            if frame % 10 == 0 {
                assert_sane(world.particles(), &format!("batch {batch} frame {frame}"));
            }
        }
        emitters_seen +=
            world.particles().active_emitter_count() + world.take_retired_emitters().len();
        assert_sane(world.particles(), &format!("batch {batch}"));
        // Hooks the effects code leaves to the presentation still arrive.
        let forwarded = world.take_events().len();
        assert!(forwarded > 0 || batch > 0);
        let diagnostics = world.take_diagnostics();
        for d in &diagnostics {
            // Only the known hardwareless emitters and missing assets may
            // fail; nothing else is expected to complain.
            assert!(
                d.contains("has no hardware GfxObj") || d.contains("was not found"),
                "batch {batch}: {d}"
            );
        }
    }
    eprintln!("physics scripts: {emitters_seen} emitters, peak {peak_particles} live particles");
    assert!(emitters_seen > 5_000, "{emitters_seen}");
    assert!(peak_particles > 1_000, "{peak_particles}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn installed_armor_self_shield_pieces_are_mode_one_meshes() {
    let dats = dats();
    let tables = PhysicsScriptTableResolver::with_dats(dats.clone());
    // PS_ShieldUpGrey, the Armor Self target effect.
    let script_did = tables.resolve(0x3400_0004, 0x37, 1.0).unwrap();
    assert_eq!(script_did, Some(0x3300_068F));

    let script = load_physics_script(dats.as_ref(), 0x3300_068F)
        .unwrap()
        .unwrap();
    let emitter_ids: Vec<u32> = script
        .hooks
        .iter()
        .filter_map(|(_, h)| match h.data {
            HookData::CreateParticle {
                emitter_info_id, ..
            } => Some(emitter_info_id),
            _ => None,
        })
        .collect();
    assert_eq!(
        emitter_ids,
        [
            0x3200_0350,
            0x3200_0351,
            0x3200_0355,
            0x3200_0356,
            0x3200_0379
        ]
    );

    let mut registry = EmitterDescRegistry::with_dats(dats.clone());
    let hw: Vec<u32> = emitter_ids
        .iter()
        .map(|&id| registry.get(id).unwrap().hw_gfx_obj_id)
        .collect();
    assert_eq!(
        hw,
        [
            0x0100_1666,
            0x0100_1667,
            0x0100_1668,
            0x0100_1669,
            0x0100_1098
        ]
    );

    let mut gfx = ParticleGfxCache::new(Some(dats.clone() as Arc<dyn VfxDats>));
    for &id in &hw[..4] {
        assert_eq!(gfx.first_degrade_mode(id), Some(1));
        assert_eq!(gfx.geometry_kind(id), ParticleGeometryKind::FullMesh);
        let model = dats.gfx_obj(id).unwrap().unwrap();
        assert_eq!(model.vertices.vertices.len(), 5);
        let apex = model
            .vertices
            .vertices
            .iter()
            .map(|(_, v)| v.origin.z)
            .fold(f32::MIN, f32::max);
        assert!((2.31..=2.33).contains(&apex), "{apex}");
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn installed_human_effect_table_maps_portal_visibility_scripts() {
    let tables = PhysicsScriptTableResolver::with_dats(dats());
    assert_eq!(
        tables.resolve(0x3400_0004, 0x74, 1.0),
        Ok(Some(0x3300_0332))
    );
    assert_eq!(
        tables.resolve(0x3400_0004, 0x75, 1.0),
        Ok(Some(0x3300_032F))
    );
    assert_eq!(
        tables.resolve(0x3400_0004, 0x76, 1.0),
        Ok(Some(0x3300_0331))
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn installed_aerlinthe_swarm_emitter_uses_its_hardware_gfx_degrade_distance() {
    let mut registry = EmitterDescRegistry::with_dats(dats());
    assert_eq!(
        registry.get(0x3200_0223).unwrap().max_degrade_distance,
        64.0
    );
}
