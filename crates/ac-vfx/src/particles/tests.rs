//! Cases from OpenAC `tests/AcDream.Core.Tests/Vfx/ParticleSystemTests.cs`,
//! seeded the same way (`new Random(42)`; [`DotNetRandom`] is that
//! generator). The death-callback cases become cases on the died list.

use std::collections::HashMap;
use std::f32::consts::FRAC_PI_2;
use std::sync::atomic::{AtomicUsize, Ordering};

use ac_dat::objects::{
    GfxObj, GfxObjDegradeInfo, GfxObjFlags, GfxObjInfo, ParticleEmitter as DatEmitter,
};

use super::*;
use crate::dats::VfxDats;
use crate::desc::{EmitterDescFailureKind, particle_degrade_distance};
use crate::random::DotNetRandom;

fn make_system() -> ParticleSystem {
    ParticleSystem::new(EmitterDescRegistry::new(), Box::new(DotNetRandom::new(42)))
}

fn make_desc(
    particle_type: ParticleType,
    max_particles: i32,
    emit_rate: f32,
    lifetime: f32,
) -> Arc<EmitterDesc> {
    Arc::new(EmitterDesc {
        dat_id: 0x3200_0001,
        particle_type,
        emit_rate,
        max_particles,
        lifetime_min: lifetime,
        lifetime_max: lifetime,
        offset_dir: Vec3::Z,
        initial_velocity: Vec3::new(0.0, 0.0, 1.0),
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 0.0,
        gravity: Vec3::ZERO,
        ..EmitterDesc::default()
    })
}

fn rate_desc(emit_rate: f32, max_particles: i32, lifetime: f32) -> Arc<EmitterDesc> {
    make_desc(
        ParticleType::LocalVelocity,
        max_particles,
        emit_rate,
        lifetime,
    )
}

fn initial_particle_desc(
    particle_type: ParticleType,
    a: Vec3,
    b: Vec3,
    c: Vec3,
) -> Arc<EmitterDesc> {
    Arc::new(EmitterDesc {
        dat_id: 0x3200_AA01,
        particle_type,
        max_particles: 1,
        initial_particles: 1,
        lifetime_min: 10.0,
        lifetime_max: 10.0,
        lifespan: 10.0,
        offset_dir: Vec3::Z,
        initial_velocity: Vec3::ZERO,
        gravity: Vec3::ZERO,
        a,
        b,
        c,
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..EmitterDesc::default()
    })
}

fn still(dat_id: u32, max_degrade_distance: f32) -> EmitterDesc {
    EmitterDesc {
        dat_id,
        particle_type: ParticleType::Still,
        max_degrade_distance,
        max_particles: 1,
        ..EmitterDesc::default()
    }
}

fn attached(owner: u32) -> SpawnOptions {
    SpawnOptions {
        attached_object_id: owner,
        ..SpawnOptions::default()
    }
}

fn yaw_90() -> SpawnOptions {
    SpawnOptions {
        rotation: Quat::from_axis_angle(Vec3::Z, FRAC_PI_2),
        ..SpawnOptions::default()
    }
}

fn single_position(sys: &ParticleSystem) -> Vec3 {
    let live: Vec<_> = sys.live_particles().collect();
    assert_eq!(live.len(), 1);
    live[0].0.particles[live[0].1].position
}

fn set(cells: &[u32]) -> HashSet<u32> {
    cells.iter().copied().collect()
}

#[test]
fn spawn_emitter_returns_positive_handle_and_tracks_emitter() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        initial_particle_desc(ParticleType::Still, Vec3::ZERO, Vec3::ZERO, Vec3::ZERO),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    assert!(h > 0);
    assert_eq!(sys.active_emitter_count(), 1);
    let live: Vec<_> = sys.live_particles().collect();
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].0.handle, h);
}

#[test]
fn tick_emits_particles_over_time() {
    let mut sys = make_system();
    // Lifetime 2 s so none die in the 1 s window; 10 a second.
    sys.spawn_emitter(
        rate_desc(10.0, 100, 2.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(0.5);
    sys.tick(0.5);
    assert!((8..=12).contains(&sys.active_particle_count()));
}

#[test]
fn tick_particles_die_at_lifetime() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        rate_desc(20.0, 100, 0.5),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    for _ in 0..20 {
        sys.tick(0.05);
    }
    assert!((7..=13).contains(&sys.active_particle_count()));

    sys.stop_emitter(h, true);
    for _ in 0..30 {
        sys.tick(0.05);
    }
    assert_eq!(sys.active_particle_count(), 0);
}

#[test]
fn local_velocity_integration_moves_particles() {
    let mut sys = make_system();
    sys.spawn_emitter(
        rate_desc(20.0, 16, 1.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(0.1);
    sys.tick(0.5);
    assert!(
        sys.live_particles()
            .any(|(em, i)| em.particles[i].position.z > 0.3)
    );
}

#[test]
fn parabolic_gravity_applied() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        dat_id: 0x3200_0002,
        particle_type: ParticleType::ParabolicLVGA,
        emit_rate: 10.0,
        max_particles: 100,
        lifetime_min: 2.0,
        lifetime_max: 2.0,
        offset_dir: Vec3::Z,
        initial_velocity: Vec3::new(0.0, 0.0, 5.0),
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 0.0,
        gravity: Vec3::new(0.0, 0.0, -10.0),
        ..EmitterDesc::default()
    });
    sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.tick(0.1);
    sys.tick(0.5);
    for _ in 0..20 {
        sys.tick(0.1);
    }
    let any_below = sys
        .live_particles()
        .any(|(em, i)| em.particles[i].position.z < 0.0);
    assert!(any_below || sys.active_particle_count() == 0);
}

#[test]
fn explode_moves_outward_from_anchor() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        dat_id: 0x3200_0003,
        particle_type: ParticleType::Explode,
        emit_rate: 20.0,
        max_particles: 100,
        lifetime_min: 2.0,
        lifetime_max: 2.0,
        offset_dir: Vec3::Z,
        min_offset: 0.5,
        max_offset: 0.5,
        spawn_disk_radius: 0.5,
        initial_velocity: Vec3::new(1.0, 0.0, 0.0),
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 0.0,
        ..EmitterDesc::default()
    });
    sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.tick(0.1);
    sys.tick(0.5);
    let live: Vec<_> = sys.live_particles().collect();
    assert!(!live.is_empty());
    for (em, i) in live {
        assert!(em.particles[i].position.length() > 0.3);
    }
}

#[test]
fn stop_emitter_kills_all_particles() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        rate_desc(10.0, 20, 1.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(0.5);
    assert!(sys.active_particle_count() > 0);
    sys.stop_emitter(h, false);
    sys.tick(0.01);
    assert_eq!(sys.active_particle_count(), 0);
}

#[test]
fn stop_emitter_fade_out_preserves_current_particles() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        rate_desc(10.0, 20, 1.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(0.3);
    let before = sys.active_particle_count();
    assert!(before > 0);
    sys.stop_emitter(h, true);
    sys.tick(0.1);
    assert_eq!(sys.active_particle_count(), before);
}

#[test]
fn max_particles_cap_enforced() {
    let mut sys = make_system();
    sys.spawn_emitter(
        rate_desc(100.0, 5, 10.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(1.0);
    assert!((1..=5).contains(&sys.active_particle_count()));
}

#[test]
fn registry_rejects_unknown_id_without_inventing_fallback() {
    let mut reg = EmitterDescRegistry::new();
    assert!(reg.get(0xDEAD_BEEF).is_err());
}

#[test]
fn registry_register_stores_by_id() {
    let mut reg = EmitterDescRegistry::new();
    let desc = reg.register(EmitterDesc {
        dat_id: 0x3200_1234,
        particle_type: ParticleType::Still,
        ..EmitterDesc::default()
    });
    assert!(Arc::ptr_eq(&desc, &reg.get(0x3200_1234).unwrap()));
}

#[test]
fn registry_negative_caches_missing_dat_and_register_clears_failure() {
    let calls = Arc::new(AtomicUsize::new(0));
    let counter = Arc::clone(&calls);
    let mut reg = EmitterDescRegistry::with_resolver(move |_| {
        counter.fetch_add(1, Ordering::SeqCst);
        None
    });
    const ID: u32 = 0x3200_DEAD;
    let first = reg.get(ID).unwrap_err();
    let second = reg.get(ID).unwrap_err();
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    assert_eq!(first.kind, EmitterDescFailureKind::MissingEmitterInfo);
    assert_eq!(first, second);
    assert_eq!(reg.failure_count(), 1);

    let desc = reg.register(EmitterDesc {
        dat_id: ID,
        ..EmitterDesc::default()
    });
    assert!(Arc::ptr_eq(&desc, &reg.get(ID).unwrap()));
    assert_eq!(reg.failure_count(), 0);
}

#[test]
fn local_velocity_transforms_a_by_spawn_rotation() {
    let mut sys = make_system();
    sys.spawn_emitter(
        initial_particle_desc(ParticleType::LocalVelocity, Vec3::X, Vec3::ZERO, Vec3::ZERO),
        Vec3::ZERO,
        yaw_90(),
    );
    sys.tick(1.0);
    let p = single_position(&sys);
    assert!(p.x.abs() <= 0.0001 && (p.y - 1.0).abs() <= 0.0001, "{p}");
}

#[test]
fn global_velocity_does_not_transform_a_by_spawn_rotation() {
    let mut sys = make_system();
    sys.spawn_emitter(
        initial_particle_desc(
            ParticleType::GlobalVelocity,
            Vec3::X,
            Vec3::ZERO,
            Vec3::ZERO,
        ),
        Vec3::ZERO,
        yaw_90(),
    );
    sys.tick(1.0);
    let p = single_position(&sys);
    assert!((p.x - 1.0).abs() <= 0.0001 && p.y.abs() <= 0.0001, "{p}");
}

#[test]
fn parabolic_lvla_transforms_local_acceleration() {
    let mut sys = make_system();
    sys.spawn_emitter(
        initial_particle_desc(ParticleType::ParabolicLVLA, Vec3::ZERO, Vec3::X, Vec3::ZERO),
        Vec3::ZERO,
        yaw_90(),
    );
    sys.tick(1.0);
    let p = single_position(&sys);
    assert!(p.x.abs() <= 0.0001 && (p.y - 0.5).abs() <= 0.0001, "{p}");
}

#[test]
fn parabolic_lvga_keeps_global_acceleration() {
    let mut sys = make_system();
    sys.spawn_emitter(
        initial_particle_desc(ParticleType::ParabolicLVGA, Vec3::ZERO, Vec3::X, Vec3::ZERO),
        Vec3::ZERO,
        yaw_90(),
    );
    sys.tick(1.0);
    let p = single_position(&sys);
    assert!((p.x - 0.5).abs() <= 0.0001 && p.y.abs() <= 0.0001, "{p}");
}

fn dat_emitter() -> DatEmitter {
    DatEmitter {
        id: 0,
        unknown: 0,
        emitter_type: 0,
        particle_type: 0,
        gfx_obj_id: 0,
        hw_gfx_obj_id: 0,
        birthrate: 0.0,
        max_particles: 0,
        initial_particles: 0,
        total_particles: 0,
        total_seconds: 0.0,
        lifespan: 0.0,
        lifespan_rand: 0.0,
        offset_dir: Default::default(),
        min_offset: 0.0,
        max_offset: 0.0,
        a: Default::default(),
        min_a: 0.0,
        max_a: 0.0,
        b: Default::default(),
        min_b: 0.0,
        max_b: 0.0,
        c: Default::default(),
        min_c: 0.0,
        max_c: 0.0,
        start_scale: 0.0,
        final_scale: 0.0,
        scale_rand: 0.0,
        start_trans: 0.0,
        final_trans: 0.0,
        trans_rand: 0.0,
        is_parent_local: false,
    }
}

#[test]
fn desc_from_dat_preserves_retail_enum_values_and_rates() {
    let mut dat = dat_emitter();
    dat.emitter_type = 1;
    dat.particle_type = 5;
    dat.gfx_obj_id = 0x0100_0001;
    dat.hw_gfx_obj_id = 0x0100_0002;
    dat.birthrate = 0.25;
    dat.max_particles = 17;
    dat.initial_particles = 3;
    dat.total_particles = 9;
    dat.total_seconds = 4.0;
    dat.lifespan = 2.0;
    dat.lifespan_rand = 0.5;
    dat.a = ac_dat::types::Vector3 {
        x: 1.0,
        y: 0.0,
        z: 0.0,
    };
    dat.min_a = 0.5;
    dat.max_a = 2.0;
    dat.start_scale = 0.2;
    dat.final_scale = 0.8;
    dat.start_trans = 1.0;
    dat.final_trans = 0.0;
    dat.is_parent_local = true;

    let desc = EmitterDesc::from_dat(0x3200_0099, &dat, 100.0);

    assert_eq!(desc.particle_type, ParticleType::Swarm);
    assert_eq!(desc.emitter_kind, EmitterKind::BirthratePerSec);
    assert_eq!(desc.emit_rate, 4.0);
    assert_eq!(desc.gfx_obj_id, 0x0100_0001);
    assert_eq!(desc.hw_gfx_obj_id, 0x0100_0002);
    assert_eq!(desc.initial_particles, 3);
    assert_eq!(desc.total_particles, 9);
    assert_eq!(desc.lifetime_min, 1.5);
    assert_eq!(desc.lifetime_max, 2.5);
    assert_eq!(desc.start_alpha, 0.0);
    assert_eq!(desc.end_alpha, 1.0);
    assert_eq!(
        desc.flags.0,
        EmitterFlags::BILLBOARD | EmitterFlags::FACE_CAMERA | EmitterFlags::ATTACH_LOCAL
    );
}

/// Dats in memory, counting emitter reads.
#[derive(Default)]
struct FakeDats {
    emitters: HashMap<u32, DatEmitter>,
    gfx: HashMap<u32, GfxObj>,
    degrades: HashMap<u32, GfxObjDegradeInfo>,
    emitter_reads: AtomicUsize,
}

impl VfxDats for FakeDats {
    fn particle_emitter(&self, id: u32) -> Result<Option<Arc<DatEmitter>>, ac_dat::DatError> {
        self.emitter_reads.fetch_add(1, Ordering::SeqCst);
        Ok(self.emitters.get(&id).map(|e| Arc::new(*e)))
    }
    fn gfx_obj(&self, id: u32) -> Result<Option<Arc<GfxObj>>, ac_dat::DatError> {
        Ok(self.gfx.get(&id).cloned().map(Arc::new))
    }
    fn gfx_obj_degrade_info(
        &self,
        id: u32,
    ) -> Result<Option<Arc<GfxObjDegradeInfo>>, ac_dat::DatError> {
        Ok(self.degrades.get(&id).cloned().map(Arc::new))
    }
    fn surface(&self, _: u32) -> Result<Option<Arc<ac_dat::objects::Surface>>, ac_dat::DatError> {
        Ok(None)
    }
    fn physics_script_table(
        &self,
        _: u32,
    ) -> Result<Option<Arc<ac_dat::objects::PhysicsScriptTable>>, ac_dat::DatError> {
        Ok(None)
    }
    fn portal_bytes(&self, _: u32) -> Result<Option<Vec<u8>>, ac_dat::DatError> {
        Ok(None)
    }
}

#[test]
fn registry_classifies_and_caches_authored_hardwareless_emitter() {
    const ID: u32 = 0x3200_02D6;
    let mut dats = FakeDats::default();
    let mut dat = dat_emitter();
    dat.gfx_obj_id = 0x0100_16C9;
    dats.emitters.insert(ID, dat);
    let dats = Arc::new(dats);
    let mut reg = EmitterDescRegistry::with_dats(dats.clone());

    let first = reg.get(ID).unwrap_err();
    let second = reg.get(ID).unwrap_err();

    assert_eq!(first.kind, EmitterDescFailureKind::InvalidHardwareGfxObjId);
    assert_eq!(first, second);
    assert_eq!(dats.emitter_reads.load(Ordering::SeqCst), 1);
    assert_eq!(reg.failure_count(), 1);
}

fn gfx_with_degrade(id: u32, did_degrade: u32) -> GfxObj {
    GfxObj {
        id,
        flags: GfxObjFlags(GfxObjFlags::HAS_DID_DEGRADE),
        surfaces: Vec::new(),
        vertices: ac_dat::objects::VertexArray {
            vertex_type: 1,
            vertices: Vec::new(),
        },
        physics_polygons: Vec::new(),
        physics_bsp: None,
        sort_center: Default::default(),
        polygons: Vec::new(),
        drawing_bsp: None,
        did_degrade,
    }
}

#[test]
fn registry_reads_the_hardware_model_degrade_distance() {
    const ID: u32 = 0x3200_0223;
    let mut dats = FakeDats::default();
    let mut dat = dat_emitter();
    dat.hw_gfx_obj_id = 0x0100_0001;
    dats.emitters.insert(ID, dat);
    dats.gfx
        .insert(0x0100_0001, gfx_with_degrade(0x0100_0001, 0x1100_0001));
    let info = |max_dist| GfxObjInfo {
        id: 0,
        degrade_mode: 2,
        min_dist: 0.0,
        ideal_dist: 0.0,
        max_dist,
    };
    dats.degrades.insert(
        0x1100_0001,
        GfxObjDegradeInfo {
            id: 0x1100_0001,
            degrades: vec![info(64.0), info(80.0)],
        },
    );
    let mut reg = EmitterDescRegistry::with_dats(Arc::new(dats));
    assert_eq!(reg.get(ID).unwrap().max_degrade_distance, 64.0);

    let mut missing = FakeDats::default();
    missing.emitters.insert(ID, dat);
    let mut reg = EmitterDescRegistry::with_dats(Arc::new(missing));
    let failure = reg.get(ID).unwrap_err();
    assert_eq!(failure.kind, EmitterDescFailureKind::MissingHardwareGfxObj);
    assert_eq!(failure.related_dat_id, 0x0100_0001);
}

fn infos(values: &[f32]) -> Vec<GfxObjInfo> {
    values
        .iter()
        .map(|&max_dist| GfxObjInfo {
            id: 0,
            degrade_mode: 1,
            min_dist: 0.0,
            ideal_dist: 0.0,
            max_dist,
        })
        .collect()
}

#[test]
fn degrade_distance_matches_retail_entry_selection() {
    assert_eq!(particle_degrade_distance(None), 100.0);
    assert_eq!(particle_degrade_distance(Some(&infos(&[11.0]))), 11.0);
    assert_eq!(particle_degrade_distance(Some(&infos(&[21.0, 22.0]))), 21.0);
    assert_eq!(
        particle_degrade_distance(Some(&infos(&[31.0, 32.0, 33.0, 34.0]))),
        33.0
    );
}

#[test]
fn degrade_distance_preserves_raw_authored_value() {
    assert_eq!(particle_degrade_distance(Some(&infos(&[-1.0]))), -1.0);
    assert_eq!(
        particle_degrade_distance(Some(&infos(&[f32::INFINITY]))),
        f32::INFINITY
    );
    assert!(particle_degrade_distance(Some(&infos(&[f32::NAN]))).is_nan());
}

#[test]
fn retail_view_uses_owner_visibility_and_inclusive_authored_distance() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        initial_particles: 1,
        lifetime_min: 10.0,
        lifetime_max: 10.0,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x3200_0076, 10.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::new(10.0, 0.0, 0.0), attached(7));
    sys.update_emitter_owner_cell(h, 0x0101_0001);

    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_0001]), true, 1.0);
    assert!(sys.emitter(h).unwrap().view_eligible);
    sys.apply_retail_view(Vec3::ZERO, &set(&[]), true, 1.0);
    assert!(!sys.emitter(h).unwrap().view_eligible);
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_0001]), true, 0.5);
    assert!(!sys.emitter(h).unwrap().view_eligible);
}

#[test]
fn retail_view_uses_landscape_membership_but_interior_cells_always_in_view() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_007B, 10.0));
    let outdoor = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(21));
    let interior = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(22));
    let cellless = sys.spawn_emitter(desc, Vec3::ZERO, attached(23));
    sys.update_emitter_owner_cell(outdoor, 0x0101_0001);
    sys.update_emitter_owner_cell(interior, 0x0101_0100);

    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_0001]), true, 1.0);
    assert!(sys.emitter(outdoor).unwrap().view_eligible);
    assert!(sys.emitter(interior).unwrap().view_eligible);
    assert!(!sys.emitter(cellless).unwrap().view_eligible);

    sys.apply_retail_view(Vec3::ZERO, &set(&[]), true, 1.0);
    assert!(!sys.emitter(outdoor).unwrap().view_eligible);
    assert!(sys.emitter(interior).unwrap().view_eligible);
    assert!(!sys.emitter(cellless).unwrap().view_eligible);
}

#[test]
fn retail_view_without_completed_view_rejects_both_cell_families() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_007D, 10.0));
    let outdoor = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(31));
    let interior = sys.spawn_emitter(desc, Vec3::ZERO, attached(32));
    sys.update_emitter_owner_cell(outdoor, 0x0101_0001);
    sys.update_emitter_owner_cell(interior, 0x0101_0100);
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_0001]), false, 1.0);
    assert!(sys.emitters().all(|e| !e.view_eligible));
}

#[test]
fn retail_view_unordered_owner_distance_passes() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        Arc::new(still(0x3200_0079, 100.0)),
        Vec3::ZERO,
        attached(12),
    );
    sys.update_emitter_owner_position(h, Vec3::new(f32::NAN, 0.0, 0.0));
    sys.update_emitter_owner_cell(h, 0x0101_000C);
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_000C]), true, 1.0);
    assert!(sys.emitter(h).unwrap().view_eligible);
}

#[test]
fn retail_view_uses_overflow_safe_direct_distance_and_raw_range() {
    let visible = set(&[0x0101_000D]);
    let eligible = |max: f32, owner: Vec3| {
        let mut sys = make_system();
        let h = sys.spawn_emitter(Arc::new(still(0x3200_007A, max)), owner, attached(13));
        sys.update_emitter_owner_position(h, owner);
        sys.update_emitter_owner_cell(h, 0x0101_000D);
        sys.apply_retail_view(Vec3::ZERO, &visible, true, 1.0);
        sys.emitter(h).unwrap().view_eligible
    };
    assert!(eligible(f32::MAX, Vec3::new(1e20, 0.0, 0.0)));
    assert!(!eligible(-1.0, Vec3::ZERO));
    assert!(eligible(0.0, Vec3::ZERO));
    assert!(eligible(f32::NAN, Vec3::new(100.0, 0.0, 0.0)));
    assert!(eligible(f32::INFINITY, Vec3::new(f32::MAX, 0.0, 0.0)));
}

#[test]
fn infinite_emitter_degraded_time_freezes_particle_age_without_catch_up() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        initial_particles: 1,
        lifetime_min: 10.0,
        lifetime_max: 10.0,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x3200_0077, 5.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::ZERO, attached(9));
    sys.update_emitter_owner_cell(h, 0x0101_0009);
    let visible = set(&[0x0101_0009]);

    sys.apply_retail_view(Vec3::ZERO, &visible, true, 1.0);
    sys.tick(1.0);
    assert!((sys.emitter(h).unwrap().particles[0].age - 1.0).abs() < 1e-4);

    sys.apply_retail_view(Vec3::new(6.0, 0.0, 0.0), &visible, true, 1.0);
    sys.tick(5.0);
    assert!(sys.emitter(h).unwrap().degraded_out);

    sys.apply_retail_view(Vec3::ZERO, &visible, true, 1.0);
    sys.tick(1.0);
    let em = sys.emitter(h).unwrap();
    assert!(!em.degraded_out);
    assert!((em.particles[0].age - 2.0).abs() < 1e-4);
}

#[test]
fn retail_view_examination_and_pass_owned_emitters_bypass_world_gate() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_007C, 1.0));
    sys.spawn_emitter(
        desc.clone(),
        Vec3::new(100.0, 0.0, 0.0),
        SpawnOptions {
            visibility_policy: ParticleVisibilityPolicy::Examination,
            ..SpawnOptions::default()
        },
    );
    sys.spawn_emitter(
        desc,
        Vec3::new(100.0, 0.0, 0.0),
        SpawnOptions {
            render_pass: ParticleRenderPass::SkyPreScene,
            visibility_policy: ParticleVisibilityPolicy::PassOwned,
            ..SpawnOptions::default()
        },
    );
    sys.apply_retail_view(Vec3::ZERO, &set(&[]), false, 1.0);
    assert!(sys.emitters().all(|e| e.view_eligible));
}

#[test]
fn dense_suspended_set_is_excluded_from_simulation_view_and_render_worksets() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        initial_particles: 1,
        lifetime_min: 100.0,
        lifetime_max: 100.0,
        ..still(0x3200_0080, 100.0)
    });
    for i in 0..2_000u32 {
        let h = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(i + 1));
        sys.update_emitter_owner_cell(h, 0x0101_0001);
        if i >= 3 {
            sys.set_emitter_presentation_visible(h, false);
            sys.set_emitter_simulation_enabled(h, false);
        }
    }
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0101_0001]), true, 1.0);
    sys.tick(0.01);

    assert_eq!(sys.last_retail_view_emitter_visit_count(), 3);
    assert_eq!(sys.last_tick_emitter_visit_count(), 3);
    assert_eq!(
        sys.renderable_emitters(ParticleRenderPass::Scene).count(),
        3
    );
    assert_eq!(sys.active_emitter_count(), 2_000);
}

#[test]
fn owner_render_scope_visits_only_requested_owners_and_unattached_in_spawn_order() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_0083, 100.0));
    let unattached = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    let (mut seven, mut nine) = (0, 0);
    for owner in 1..=2_000u32 {
        let h = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(owner));
        if owner == 7 {
            seven = h;
        }
        if owner == 9 {
            nine = h;
        }
    }

    let mut out = Vec::new();
    sys.copy_renderable_emitters_for_owners(
        ParticleRenderPass::Scene,
        &set(&[9, 7, 4_000]),
        true,
        &mut out,
        None,
        UnattachedEmitterCellScope::Any,
    );
    assert_eq!(sys.last_render_scope_emitter_visit_count(), 3);
    assert_eq!(out.clone(), [unattached, seven, nine]);

    sys.copy_renderable_emitters_for_owners(
        ParticleRenderPass::Scene,
        &set(&[7, 9]),
        false,
        &mut out,
        Some(&set(&[7])),
        UnattachedEmitterCellScope::Any,
    );
    assert_eq!(sys.last_render_scope_emitter_visit_count(), 1);
    assert_eq!(out.clone(), [nine]);
}

#[test]
fn unattached_cell_scope_splits_emitters_by_owner_cell_kind() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_0083, 100.0));
    let outdoor = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    sys.update_emitter_owner_cell(outdoor, 0xA9B4_0021);
    let interior = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    sys.update_emitter_owner_cell(interior, 0xA9B4_0100);
    let cellless = sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());

    let none = set(&[]);
    let mut out = Vec::new();
    for (scope, expected) in [
        (UnattachedEmitterCellScope::OutdoorCells, vec![outdoor]),
        (UnattachedEmitterCellScope::InteriorCells, vec![interior]),
        (
            UnattachedEmitterCellScope::Any,
            vec![outdoor, interior, cellless],
        ),
    ] {
        sys.copy_renderable_emitters_for_owners(
            ParticleRenderPass::Scene,
            &none,
            true,
            &mut out,
            None,
            scope,
        );
        assert_eq!(out, expected);
    }
}

#[test]
fn spatial_reentry_waits_for_fresh_retail_view_before_becoming_renderable() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        Arc::new(still(0x3200_0084, 100.0)),
        Vec3::ZERO,
        attached(84),
    );
    sys.update_emitter_owner_cell(h, 0x0101_0001);
    let visible = set(&[0x0101_0001]);
    let owners = set(&[84]);
    let mut out = Vec::new();

    sys.apply_retail_view(Vec3::ZERO, &visible, true, 1.0);
    assert_eq!(
        sys.renderable_emitters(ParticleRenderPass::Scene).count(),
        1
    );

    sys.set_emitter_presentation_visible(h, false);
    sys.set_emitter_simulation_enabled(h, false);
    assert!(!sys.emitter(h).unwrap().view_eligible);

    sys.set_emitter_presentation_visible(h, true);
    sys.set_emitter_simulation_enabled(h, true);
    sys.copy_renderable_emitters_for_owners(
        ParticleRenderPass::Scene,
        &owners,
        false,
        &mut out,
        None,
        Default::default(),
    );
    assert!(out.is_empty());

    sys.apply_retail_view(Vec3::ZERO, &visible, true, 1.0);
    sys.copy_renderable_emitters_for_owners(
        ParticleRenderPass::Scene,
        &owners,
        false,
        &mut out,
        None,
        Default::default(),
    );
    assert_eq!(out.len(), 1);
}

#[test]
fn cell_index_add_remove_move_tracks_renderable_emitters_per_cell() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(
        Arc::new(still(0x3200_0090, 100.0)),
        Vec3::ZERO,
        attached(501),
    );
    sys.update_emitter_owner_cell(h, 0x0102_0001);
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0102_0001, 0x0102_0002]), true, 1.0);
    let mut out = Vec::new();
    let pass = ParticleRenderPass::Scene;

    sys.copy_renderable_emitters_in_cell(pass, 0x0102_0001, &mut out);
    assert_eq!(out.clone(), [h]);
    sys.copy_renderable_emitters_in_cell(pass, 0x0102_0002, &mut out);
    assert!(out.is_empty());
    assert!(sys.has_renderable_emitters_in_cell(pass, 0x0102_0001));

    sys.update_emitter_owner_cell(h, 0x0102_0002);
    sys.copy_renderable_emitters_in_cell(pass, 0x0102_0001, &mut out);
    assert!(out.is_empty());
    sys.copy_renderable_emitters_in_cell(pass, 0x0102_0002, &mut out);
    assert_eq!(out.clone(), [h]);
    assert!(!sys.has_renderable_emitters_in_cell(pass, 0x0102_0001));
    assert!(sys.has_renderable_emitters_in_cell(pass, 0x0102_0002));

    sys.stop_emitter(h, false);
    sys.copy_renderable_emitters_in_cell(pass, 0x0102_0002, &mut out);
    assert!(out.is_empty());
    assert!(!sys.has_renderable_emitters_in_cell(pass, 0x0102_0002));
}

#[test]
fn cell_index_multiple_emitters_in_one_cell_enumerate_in_spawn_order() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_0091, 100.0));
    let first = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(601));
    sys.update_emitter_owner_cell(first, 0x0203_0005);
    let second = sys.spawn_emitter(desc.clone(), Vec3::ZERO, attached(602));
    sys.update_emitter_owner_cell(second, 0x0203_0005);
    let unattached = sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.update_emitter_owner_cell(unattached, 0x0203_0005);
    sys.apply_retail_view(Vec3::ZERO, &set(&[0x0203_0005]), true, 1.0);

    let mut out = Vec::new();
    sys.copy_renderable_emitters_in_cell(ParticleRenderPass::Scene, 0x0203_0005, &mut out);
    assert_eq!(out.clone(), [first, second, unattached]);
}

#[test]
fn ordered_indexes_preserve_spawn_order_across_hard_removal_and_pass_filtering() {
    let mut sys = make_system();
    let desc = Arc::new(still(0x3200_0081, 100.0));
    let sky = SpawnOptions {
        render_pass: ParticleRenderPass::SkyPreScene,
        visibility_policy: ParticleVisibilityPolicy::PassOwned,
        ..SpawnOptions::default()
    };
    let first = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    let removed_scene = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    let sky_handle = sys.spawn_emitter(desc.clone(), Vec3::ZERO, sky);
    let removed_sky = sys.spawn_emitter(desc.clone(), Vec3::ZERO, sky);
    let last = sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.stop_emitter(removed_scene, false);
    sys.stop_emitter(removed_sky, false);

    let handles = |it: Vec<&ParticleEmitter>| it.iter().map(|e| e.handle).collect::<Vec<_>>();
    assert_eq!(handles(sys.emitters().collect()), [first, sky_handle, last]);
    assert_eq!(
        handles(sys.renderable_emitters(ParticleRenderPass::Scene).collect()),
        [first, last]
    );
    assert_eq!(
        handles(
            sys.renderable_emitters(ParticleRenderPass::SkyPreScene)
                .collect()
        ),
        [sky_handle]
    );
}

#[test]
fn finite_emitter_degraded_branch_expires_and_removes_it() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        initial_particles: 1,
        total_particles: 1,
        lifetime_min: 0.5,
        lifetime_max: 0.5,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x3200_0078, 1.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::ZERO, attached(10));
    sys.update_emitter_owner_cell(h, 0x0101_000A);
    sys.apply_retail_view(Vec3::new(2.0, 0.0, 0.0), &set(&[0x0101_000A]), true, 1.0);
    sys.tick(1.0);
    assert_eq!(sys.active_emitter_count(), 1);
    assert!(sys.emitter(h).unwrap().finished);
    sys.tick(0.01);
    assert_eq!(sys.active_emitter_count(), 0);
}

#[test]
fn finite_emitter_degraded_branch_does_not_stop_before_authored_limit() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        emitter_kind: EmitterKind::BirthratePerSec,
        birthrate: 10.0,
        max_particles: 4,
        initial_particles: 1,
        total_particles: 4,
        total_duration: 100.0,
        lifetime_min: 50.0,
        lifetime_max: 50.0,
        ..still(0x3200_007D, 1.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::ZERO, attached(15));
    sys.update_emitter_owner_cell(h, 0x0101_000F);
    sys.apply_retail_view(Vec3::new(2.0, 0.0, 0.0), &set(&[0x0101_000F]), true, 1.0);
    sys.tick(1.0);
    let em = sys.emitter(h).unwrap();
    assert!(!em.finished);
    assert_eq!(em.active_count, 1);
    assert_eq!(em.total_emitted, 1);
}

#[test]
fn finite_emitter_degraded_due_emission_records_both_retail_counters() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        emitter_kind: EmitterKind::BirthratePerSec,
        birthrate: 0.5,
        max_particles: 4,
        total_particles: 3,
        total_duration: 100.0,
        lifetime_min: 5.0,
        lifetime_max: 5.0,
        ..still(0x3200_007E, 1.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::ZERO, attached(16));
    sys.update_emitter_owner_cell(h, 0x0101_0010);
    sys.apply_retail_view(Vec3::new(2.0, 0.0, 0.0), &set(&[0x0101_0010]), true, 1.0);

    sys.tick(0.25);
    let em = sys.emitter(h).unwrap();
    assert_eq!(
        (em.active_count, em.total_emitted, em.finished),
        (0, 0, false)
    );

    sys.tick(0.5);
    let em = sys.emitter(h).unwrap();
    assert_eq!(
        (em.active_count, em.total_emitted, em.finished),
        (1, 1, false)
    );
    assert_eq!(sys.live_particles().count(), 0);
}

#[test]
fn finite_emitter_degraded_recorded_count_persists_until_owner_teardown() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        emitter_kind: EmitterKind::BirthratePerSec,
        birthrate: 0.5,
        max_particles: 1,
        total_particles: 1,
        total_duration: 100.0,
        lifetime_min: 5.0,
        lifetime_max: 5.0,
        ..still(0x3200_007F, 1.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::ZERO, attached(17));
    sys.update_emitter_owner_cell(h, 0x0101_0011);
    sys.apply_retail_view(Vec3::new(2.0, 0.0, 0.0), &set(&[0x0101_0011]), true, 1.0);

    sys.tick(0.75);
    let em = sys.emitter(h).unwrap();
    assert!(em.finished);
    assert_eq!(em.active_count, 1);
    assert_eq!(sys.live_particles().count(), 0);

    sys.tick(10.0);
    assert!(sys.is_emitter_alive(h));
    assert_eq!(sys.emitter(h).unwrap().active_count, 1);

    sys.stop_emitter(h, false);
    assert!(!sys.is_emitter_alive(h));
}

#[test]
fn attach_local_particle_position_follows_live_anchor() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        flags: EmitterFlags(EmitterFlags::ATTACH_LOCAL | EmitterFlags::BILLBOARD),
        initial_particles: 1,
        lifetime_min: 100.0,
        lifetime_max: 100.0,
        lifespan: 100.0,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x32AA_BBCC, 100.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::new(10.0, 0.0, 0.0), SpawnOptions::default());
    sys.tick(0.01);
    assert_eq!(single_position(&sys), Vec3::new(10.0, 0.0, 0.0));

    sys.update_emitter_anchor(h, Vec3::new(50.0, 20.0, 5.0), None);
    sys.tick(0.01);
    assert_eq!(single_position(&sys), Vec3::new(50.0, 20.0, 5.0));
}

#[test]
fn released_particle_stays_at_spawn_origin() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        flags: EmitterFlags(EmitterFlags::BILLBOARD),
        initial_particles: 1,
        lifetime_min: 100.0,
        lifetime_max: 100.0,
        lifespan: 100.0,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x32AA_BBCD, 100.0)
    });
    let h = sys.spawn_emitter(desc, Vec3::new(10.0, 0.0, 0.0), SpawnOptions::default());
    sys.tick(0.01);
    sys.update_emitter_anchor(h, Vec3::splat(99.0), None);
    sys.tick(0.01);
    assert_eq!(single_position(&sys), Vec3::new(10.0, 0.0, 0.0));
}

#[test]
fn died_list_reports_each_handle_once_after_removal() {
    let mut sys = make_system();
    let h = sys.spawn_emitter(rate_desc(5.0, 4, 0.2), Vec3::ZERO, SpawnOptions::default());
    sys.stop_emitter(h, false);
    sys.tick(0.01);
    assert_eq!(sys.take_died_emitters(), [h]);
    assert!(sys.take_died_emitters().is_empty());
    assert!(!sys.is_emitter_alive(h));
}

#[test]
fn finished_emitters_retire_on_the_tick_after_their_last_particle() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        initial_particles: 1,
        total_particles: 1,
        lifetime_min: 0.01,
        lifetime_max: 0.01,
        lifespan: 0.01,
        ..still(0x3200_0082, 100.0)
    });
    let first = sys.spawn_emitter(desc.clone(), Vec3::ZERO, SpawnOptions::default());
    let second = sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.tick(0.02);
    assert_eq!(sys.active_emitter_count(), 2);
    sys.tick(0.01);
    assert_eq!(sys.take_died_emitters(), [first, second]);
    assert_eq!(sys.active_emitter_count(), 0);
}

#[test]
fn birthrate_per_sec_emits_one_per_tick_when_interval_elapsed() {
    let mut sys = make_system();
    let desc = Arc::new(EmitterDesc {
        emitter_kind: EmitterKind::BirthratePerSec,
        birthrate: 0.05,
        emit_rate: 0.0,
        max_particles: 100,
        lifetime_min: 100.0,
        lifetime_max: 100.0,
        lifespan: 100.0,
        start_size: 1.0,
        end_size: 1.0,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..still(0x32AA_AA01, 100.0)
    });
    sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
    sys.tick(1.0);
    assert_eq!(sys.active_particle_count(), 1);
    sys.tick(0.06);
    assert_eq!(sys.active_particle_count(), 2);
    sys.tick(0.01);
    assert_eq!(sys.active_particle_count(), 2);
}

#[test]
fn tick_walks_the_simulation_set_only_when_its_membership_moves() {
    let mut sys = make_system();
    let first = sys.spawn_emitter(
        rate_desc(20.0, 16, 100.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    let second = sys.spawn_emitter(
        rate_desc(20.0, 16, 100.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );

    sys.tick(0.01);
    let walks = sys.simulation_snapshot_rebuilds();
    assert_eq!(sys.simulation_tick_order(), [first, second]);

    sys.tick(0.01);
    sys.tick(0.01);
    assert_eq!(sys.simulation_snapshot_rebuilds(), walks);

    sys.set_emitter_simulation_enabled(first, false);
    sys.tick(0.01);
    assert_eq!(sys.simulation_snapshot_rebuilds(), walks + 1);
    assert_eq!(sys.simulation_tick_order(), [second]);

    sys.set_emitter_simulation_enabled(first, true);
    sys.tick(0.01);
    assert_eq!(sys.simulation_snapshot_rebuilds(), walks + 2);
    assert_eq!(sys.simulation_tick_order(), [first, second]);

    let third = sys.spawn_emitter(
        rate_desc(20.0, 16, 100.0),
        Vec3::ZERO,
        SpawnOptions::default(),
    );
    sys.tick(0.01);
    assert_eq!(sys.simulation_snapshot_rebuilds(), walks + 3);
    assert_eq!(sys.simulation_tick_order(), [first, second, third]);

    sys.stop_emitter(third, false);
    sys.tick(0.01);
    assert_eq!(sys.simulation_snapshot_rebuilds(), walks + 4);
    assert_eq!(sys.simulation_tick_order(), [first, second]);
}

#[test]
fn seeded_simulation_is_deterministic() {
    let run = || {
        let mut sys = make_system();
        let desc = Arc::new(EmitterDesc {
            dat_id: 0x3200_0001,
            particle_type: ParticleType::Explode,
            emit_rate: 30.0,
            max_particles: 64,
            lifespan: 1.0,
            lifespan_rand: 0.3,
            min_offset: 0.1,
            max_offset: 0.4,
            a: Vec3::new(1.0, 0.0, 0.0),
            b: Vec3::new(0.0, 0.0, -1.0),
            c: Vec3::ONE,
            scale_rand: 0.2,
            trans_rand: 0.1,
            ..EmitterDesc::default()
        });
        sys.spawn_emitter(desc, Vec3::ZERO, SpawnOptions::default());
        for _ in 0..30 {
            sys.tick(1.0 / 30.0);
        }
        sys.live_particles()
            .map(|(em, i)| em.particles[i].position)
            .collect::<Vec<_>>()
    };
    let a = run();
    assert!(!a.is_empty());
    assert_eq!(a, run());
}

// ---- Golden values from OpenAC itself ----
//
// Printed by OpenAC's `ParticleSystem` (src/AcDream.Core/Vfx/ParticleSystem.cs,
// compiled unchanged in a throwaway console) seeded `new Random(42)`: each
// scenario spawns one emitter, then 12 times moves the anchor by `step` and
// ticks 0.1 s. Rows: (slot, x, y, z, size, colour, lifetime). The same seed
// and the same draw order give the same particles; positions agree to float
// rounding (the quaternion rotate is a different formula).

type Row = (usize, f32, f32, f32, f32, u32, f32);

fn run_scenario(desc: EmitterDesc, anchor: Vec3, rotation: Quat, step: Vec3) -> (i32, Vec<Row>) {
    let mut sys = make_system();
    let mut anchor = anchor;
    let h = sys.spawn_emitter(
        Arc::new(desc),
        anchor,
        SpawnOptions {
            rotation,
            ..SpawnOptions::default()
        },
    );
    for _ in 0..12 {
        anchor += step;
        sys.update_emitter_anchor(h, anchor, None);
        sys.tick(0.1);
    }
    let rows = sys
        .live_particles()
        .map(|(em, i)| {
            let p = em.particles[i];
            (
                i,
                p.position.x,
                p.position.y,
                p.position.z,
                p.size,
                p.color_argb,
                p.lifetime,
            )
        })
        .collect();
    (sys.active_particle_count(), rows)
}

fn assert_rows(name: &str, (active, rows): (i32, Vec<Row>), golden: &[Row]) {
    assert_eq!(active as usize, golden.len(), "{name}: active count");
    assert_eq!(rows.len(), golden.len(), "{name}: live particles");
    for (got, want) in rows.iter().zip(golden) {
        assert_eq!(
            (got.0, got.5),
            (want.0, want.5),
            "{name}: slot and colour {got:?} vs {want:?}"
        );
        for (g, w) in [
            (got.1, want.1),
            (got.2, want.2),
            (got.3, want.3),
            (got.4, want.4),
            (got.6, want.6),
        ] {
            assert!(
                (g - w).abs() <= 1e-4 * w.abs().max(1.0),
                "{name}: {got:?} vs {want:?}"
            );
        }
    }
}

#[test]
fn oracle_local_velocity_with_offsets_ranges_and_colours() {
    let desc = EmitterDesc {
        dat_id: 1,
        particle_type: ParticleType::LocalVelocity,
        emitter_kind: EmitterKind::BirthratePerSec,
        birthrate: 0.05,
        max_particles: 32,
        lifespan: 1.5,
        lifespan_rand: 0.4,
        lifetime_min: 1.1,
        lifetime_max: 1.9,
        offset_dir: Vec3::Z,
        min_offset: 0.2,
        max_offset: 0.6,
        a: Vec3::new(1.0, 0.0, 0.5),
        min_a: 0.5,
        max_a: 2.0,
        start_size: 0.3,
        end_size: 1.2,
        scale_rand: 0.1,
        start_alpha: 1.0,
        end_alpha: 0.0,
        trans_rand: 0.2,
        start_color_argb: 0xFF10_2030,
        end_color_argb: 0xFFF0_E0D0,
        ..EmitterDesc::default()
    };
    let result = run_scenario(
        desc,
        Vec3::new(10.0, 20.0, 30.0),
        Quat::from_axis_angle(Vec3::Z, 0.7),
        Vec3::ZERO,
    );
    assert_rows(
        "A",
        result,
        &[
            (
                0, 10.734839, 20.366627, 30.491638, 0.9221114, 0x5AA6A19B, 1.6344851,
            ),
            (
                1, 10.406301, 20.352858, 30.445183, 0.9275423, 0x3FBDB5AC, 1.2876707,
            ),
            (
                2, 10.899798, 20.233036, 30.58948, 0.86473715, 0x6A9E9995, 1.4179764,
            ),
            (
                3, 11.062068, 20.47247, 30.549896, 0.7204627, 0x927C7C7D, 1.6540718,
            ),
            (
                4, 10.97762, 20.5497, 30.585363, 0.65402806, 0x926C6F71, 1.6986871,
            ),
            (
                5, 10.977131, 20.289618, 30.38216, 0.64896286, 0x92747678, 1.3316853,
            ),
            (
                6, 10.474638, 20.320387, 30.394316, 0.59803855, 0x88676B6E, 1.2747564,
            ),
            (
                7, 10.575472, 20.46396, 30.103868, 0.45200673, 0xB84D545C, 1.4513435,
            ),
            (
                8, 10.496168, 20.365303, 30.085, 0.41446236, 0xD4343F4A, 1.8308114,
            ),
            (
                9, 10.350312, 19.784706, 30.160265, 0.34444988, 0xDD2D3944, 1.5258925,
            ),
            (
                10, 10.125634, 19.773912, 30.083208, 0.2834825, 0xD51D2B39, 1.66856,
            ),
            (
                11, 10.175982, 20.452997, 30.0, 0.31041202, 0xFF102030, 1.5102715,
            ),
        ],
    );
}

#[test]
fn oracle_explode_on_the_rate_path() {
    let desc = EmitterDesc {
        dat_id: 2,
        particle_type: ParticleType::Explode,
        emit_rate: 25.0,
        max_particles: 40,
        lifespan: 2.0,
        a: Vec3::new(2.0, 0.0, 1.0),
        b: Vec3::new(0.0, 0.0, -1.0),
        c: Vec3::ONE,
        min_c: 0.5,
        max_c: 1.5,
        min_offset: 0.1,
        max_offset: 0.3,
        ..EmitterDesc::default()
    };
    let result = run_scenario(desc, Vec3::ZERO, Quat::IDENTITY, Vec3::ZERO);
    assert_rows(
        "B",
        result,
        &[
            (
                0,
                -0.020812094,
                0.041355878,
                2.0845056,
                0.5,
                0x72FFFFFF,
                2.0,
            ),
            (1, -0.7784287, -0.30908325, 2.003232, 0.5, 0x72FFFFFF, 2.0),
            (2, 0.9874248, -1.7445488, -0.049693234, 0.5, 0x7FFFFFFF, 2.0),
            (3, 0.13065453, -1.312538, 1.6042299, 0.5, 0x7FFFFFFF, 2.0),
            (
                4,
                -0.41255146,
                -0.032045037,
                1.9601476,
                0.5,
                0x7FFFFFFF,
                2.0,
            ),
            (5, -0.7301836, -1.4564215, -0.29756817, 0.5, 0x8CFFFFFF, 2.0),
            (6, -0.62126076, 1.386457, 1.0805236, 0.5, 0x8CFFFFFF, 2.0),
            (7, 0.5561129, 0.94666994, -1.0381384, 0.5, 0x98FFFFFF, 2.0),
            (8, -0.68304497, 1.3562372, 0.8497595, 0.5, 0x98FFFFFF, 2.0),
            (9, 0.22326373, -0.18208943, 1.7599585, 0.5, 0x98FFFFFF, 2.0),
            (
                10,
                -0.22688843,
                -0.2502374,
                -1.1287192,
                0.5,
                0xA5FFFFFF,
                2.0,
            ),
            (11, -0.1517099, -1.0896796, -0.7606563, 0.5, 0xA5FFFFFF, 2.0),
            (
                12,
                0.786832,
                -0.013725557,
                -0.85167295,
                0.5,
                0xB2FFFFFF,
                2.0,
            ),
            (13, -1.0250714, -0.54177916, 0.6646469, 0.5, 0xB2FFFFFF, 2.0),
            (14, -0.20445144, 0.51275975, 1.164748, 0.5, 0xB2FFFFFF, 2.0),
            (
                15,
                0.03863746,
                -0.45068142,
                0.99709827,
                0.5,
                0xBFFFFFFF,
                2.0,
            ),
            (
                16,
                0.8877519,
                -0.17227273,
                -0.07272519,
                0.5,
                0xBFFFFFFF,
                2.0,
            ),
            (
                17,
                -0.58249533,
                0.15081052,
                -0.38987952,
                0.5,
                0xCBFFFFFF,
                2.0,
            ),
            (18, 0.59589344, -0.36723515, 0.7824484, 0.5, 0xCBFFFFFF, 2.0),
            (19, 0.23624921, -0.836284, 0.085813306, 0.5, 0xCBFFFFFF, 2.0),
            (
                20,
                -0.39165962,
                -0.5861274,
                -0.1413293,
                0.5,
                0xD8FFFFFF,
                2.0,
            ),
            (
                21,
                0.11005257,
                -0.065996334,
                -0.33326635,
                0.5,
                0xD8FFFFFF,
                2.0,
            ),
            (22, 0.03783694, 0.16010067, 0.5131207, 0.5, 0xE5FFFFFF, 2.0),
            (
                23,
                -0.61426365,
                0.16973695,
                0.18559629,
                0.5,
                0xE5FFFFFF,
                2.0,
            ),
            (
                24,
                -0.010919519,
                0.009679095,
                0.5501608,
                0.5,
                0xE5FFFFFF,
                2.0,
            ),
            (25, 0.025977, 0.019164108, 0.25694194, 0.5, 0xF2FFFFFF, 2.0),
            (26, 0.1919533, 0.25220108, 0.08362966, 0.5, 0xF2FFFFFF, 2.0),
            (27, 0.12012743, -0.037257943, 0.0, 0.5, 0xFFFFFFFF, 2.0),
            (28, 0.13358995, 0.24375714, 0.0, 0.5, 0xFFFFFFFF, 2.0),
            (29, 0.18920203, 0.15146793, 0.0, 0.5, 0xFFFFFFFF, 2.0),
        ],
    );
}

#[test]
fn oracle_swarm_with_initial_particles_and_rotation() {
    let desc = EmitterDesc {
        dat_id: 3,
        particle_type: ParticleType::Swarm,
        birthrate: 0.1,
        max_particles: 20,
        initial_particles: 5,
        lifespan: 5.0,
        a: Vec3::new(0.0, 0.0, 0.5),
        b: Vec3::new(3.0, 2.0, 1.0),
        c: Vec3::new(0.5, 0.5, 0.2),
        min_b: 0.8,
        max_b: 1.2,
        ..EmitterDesc::default()
    };
    let result = run_scenario(
        desc,
        Vec3::ONE,
        Quat::from_axis_angle(Vec3::X, 0.3),
        Vec3::ZERO,
    );
    assert_rows(
        "C",
        result,
        &[
            (0, 0.5016331, 1.2685395, 1.6778326, 0.5, 0xC1FFFFFF, 5.0),
            (1, 0.5014515, 1.2424446, 1.6687698, 0.5, 0xC1FFFFFF, 5.0),
            (2, 0.6934294, 1.0356668, 1.6168498, 0.5, 0xC1FFFFFF, 5.0),
            (3, 0.509771, 1.2848405, 1.6844085, 0.5, 0xC1FFFFFF, 5.0),
            (4, 0.50827765, 1.2222149, 1.6625037, 0.5, 0xC1FFFFFF, 5.0),
            (5, 0.5770776, 1.3467369, 1.6082091, 0.5, 0xCBFFFFFF, 5.0),
            (6, 0.6077221, 1.3655065, 1.5657218, 0.5, 0xD1FFFFFF, 5.0),
            (7, 0.67684704, 1.3958088, 1.4796319, 0.5, 0xDBFFFFFF, 5.0),
            (8, 1.0398164, 1.3304653, 1.4624019, 0.5, 0xE0FFFFFF, 5.0),
            (9, 0.93691516, 1.378653, 1.4076695, 0.5, 0xE5FFFFFF, 5.0),
            (10, 1.1898273, 1.2952206, 1.3757596, 0.5, 0xEAFFFFFF, 5.0),
            (11, 1.3062204, 1.2411902, 1.3341368, 0.5, 0xEFFFFFFF, 5.0),
            (12, 1.379232, 1.1983292, 1.2899611, 0.5, 0xF4FFFFFF, 5.0),
            (13, 1.4834733, 1.0707434, 1.2470287, 0.5, 0xF9FFFFFF, 5.0),
            (14, 1.5, 1.0, 1.2, 0.5, 0xFFFFFFFF, 5.0),
        ],
    );
}

#[test]
fn oracle_implode_initial_burst() {
    let desc = EmitterDesc {
        dat_id: 4,
        particle_type: ParticleType::Implode,
        max_particles: 8,
        initial_particles: 8,
        lifespan: 3.0,
        a: Vec3::new(2.0, 0.0, 0.0),
        b: Vec3::new(0.0, 0.0, 0.3),
        c: Vec3::ONE,
        offset_dir: Vec3::Y,
        min_offset: 0.5,
        max_offset: 1.0,
        ..EmitterDesc::default()
    };
    let result = run_scenario(desc, Vec3::ZERO, Quat::IDENTITY, Vec3::ZERO);
    assert_rows(
        "D",
        result,
        &[
            (0, -0.1531116, 0.0, 0.44170648, 0.5, 0x98FFFFFF, 3.0),
            (1, -0.119526416, 0.0, 0.2725875, 0.5, 0x98FFFFFF, 3.0),
            (2, 0.028029896, 0.0, 0.580075, 0.5, 0x98FFFFFF, 3.0),
            (3, -0.19552386, 0.0, 0.5522933, 0.5, 0x98FFFFFF, 3.0),
            (4, 0.13192031, 0.0, 0.5037381, 0.5, 0x98FFFFFF, 3.0),
            (5, -0.12660936, 0.0, 0.3715077, 0.5, 0x98FFFFFF, 3.0),
            (6, -0.09350541, 0.0, 0.3392457, 0.5, 0x98FFFFFF, 3.0),
            (7, -0.14833045, 0.0, 0.31539524, 0.5, 0x98FFFFFF, 3.0),
        ],
    );
}

#[test]
fn oracle_per_metre_attached_spinning_parabola_on_a_moving_anchor() {
    let desc = EmitterDesc {
        dat_id: 5,
        particle_type: ParticleType::ParabolicLVLALR,
        emitter_kind: EmitterKind::BirthratePerMeter,
        birthrate: 0.5,
        max_particles: 30,
        lifespan: 0.8,
        lifespan_rand: 0.3,
        a: Vec3::new(0.0, 1.0, 2.0),
        b: Vec3::new(0.0, 0.0, -3.0),
        c: Vec3::new(0.3, 0.2, 0.1),
        min_offset: 0.0,
        max_offset: 0.25,
        flags: EmitterFlags(EmitterFlags::ATTACH_LOCAL),
        ..EmitterDesc::default()
    };
    let result = run_scenario(
        desc,
        Vec3::ZERO,
        Quat::from_axis_angle(Vec3::new(1.0, 1.0, 0.0).normalize(), 0.5),
        Vec3::new(0.3, 0.0, 0.0),
    );
    assert_rows(
        "E",
        result,
        &[
            (
                0,
                3.5841894,
                -0.0053145736,
                0.0040543936,
                0.5,
                0xFFFFFFFF,
                0.63106734,
            ),
            (
                1, 3.7602994, 0.46042046, 0.84521097, 0.5, 0x01FFFFFF, 0.80336213,
            ),
            (
                2, 3.861245, 0.3051824, 0.76903856, 0.5, 0x64FFFFFF, 0.9897092,
            ),
            (
                3, 3.639964, 0.1868675, 0.6948645, 0.5, 0x98FFFFFF, 0.99263906,
            ),
            (
                4, 3.7528424, 0.05362954, 0.34910256, 0.5, 0xB0FFFFFF, 0.6499765,
            ),
        ],
    );
}
