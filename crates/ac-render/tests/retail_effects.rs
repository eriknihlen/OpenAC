//! The window's effects against the retail dats: Holtburg's statics (the
//! landblock 0xA9B4 around the lifestone) start their default scripts, run
//! for ten seconds of frames with the sky at noon, and every particle the
//! frame would draw is finite. Ignored by default; `scripts/check.*` runs it
//! when the dats are present.

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::objects::Region;
use ac_render::animation::source::ContentAnimationSource;
use ac_render::animation::{AnimationPresenter, StaticKey};
use ac_render::assets::{self, REGION_ID};
use ac_render::content::Content;
use ac_render::effects::{EffectsFrame, SHELL_CELL, WindowEffects};
use ac_render::world_frame::LandblockCoord;
use ac_vfx::{ParticleCamera, ParticleFrame, ParticleRenderPass, build_particle_frame};
use glam::Vec3;

fn content() -> Arc<Content> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(Content::open(&dir).expect("open the retail dats"))
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_statics_run_their_effects_and_every_particle_is_finite() {
    let content = content();
    let region = content.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let sky = assets::region_sky(&content, &region).expect("the region's sky");
    let coord = LandblockCoord { x: 0xA9, y: 0xB4 };
    let landblock = 0xA9B4_0000;
    let near = assets::build_near(&content, coord, &region);
    let mut source = ContentAnimationSource::new(Arc::clone(&content));
    let mut animations = AnimationPresenter::new();
    let mut effects = WindowEffects::new(Arc::clone(&content), 42);

    // The keys the window uses: its own statics (shells excluded) by
    // index, the shells apart, each cell's statics by cell.
    let key = |cell: u32, index: usize| StaticKey {
        landblock,
        cell,
        index: index as u32,
    };
    let own: Vec<_> = near
        .statics
        .iter()
        .filter(|s| !s.is_building_shell)
        .enumerate()
        .map(|(i, s)| (key(0, i), s))
        .collect();
    let shells: Vec<_> = near
        .statics
        .iter()
        .enumerate()
        .filter(|(_, s)| s.is_building_shell)
        .map(|(i, s)| (key(SHELL_CELL, i), s))
        .collect();
    let cells: Vec<_> = near
        .cells
        .iter()
        .flat_map(|c| {
            c.statics
                .iter()
                .enumerate()
                .map(move |(i, s)| (key(c.id, i), s))
        })
        .collect();
    for (k, s) in own.iter().chain(&cells) {
        if s.model_id >> 24 == 0x02 {
            animations.register_static(*k, s.model_id, s.transform.w_axis.truncate(), &mut source);
        }
    }
    effects.register_statics(
        landblock,
        Vec3::ZERO,
        own.iter().chain(&shells).chain(&cells).copied(),
        |k| animations.static_animation(k).is_some(),
        &source,
    );
    let (_, statics) = effects.owner_counts();
    assert!(statics > 0, "Holtburg has statics with effects");

    // Noon, the camera above the lifestone looking north.
    let camera = ParticleCamera {
        position: Vec3::new(84.0, 0.0, 100.0),
        right: Vec3::X,
        up: Vec3::Z,
    };
    let group = sky.day_groups.first();
    let dt = 1.0 / 30.0;
    for _ in 0..300 {
        animations.tick(f64::from(dt), None, |_| None, |_, local| Some(local));
        let hooks = animations.take_effects();
        effects.frame(EffectsFrame {
            dt,
            scene: None,
            to_frame: &|_, local| local,
            poses: &[],
            animations: &animations,
            landblock_offset: &|_| Vec3::ZERO,
            resident: &|_| true,
            hooks,
            sky: Some((0.5, group)),
            sky_active: true,
            camera: camera.position,
            source: &source,
            particle_range: ac_vfx::EXTENDED_RANGE_MULTIPLIER,
        });
        // Every outdoor cell of the landblock was drawn.
        let view = effects.vfx_mut().visibility_mut();
        view.begin_frame(camera.position);
        view.use_world_view();
        view.mark_visible_landscape_cells((1..=64).map(|c| landblock | c))
            .unwrap();
        view.complete_frame();
    }
    let (system, gfx, _) = effects.draw_sources();
    assert!(system.active_emitter_count() > 0, "emitters run");
    let mut frame = ParticleFrame::default();
    for pass in [
        ParticleRenderPass::SkyPreScene,
        ParticleRenderPass::Scene,
        ParticleRenderPass::SkyPostScene,
    ] {
        build_particle_frame(system.renderable_emitters(pass), &camera, gfx, &mut frame);
    }
    assert!(!frame.submissions.is_empty(), "particles to draw");
    for b in &frame.billboards {
        assert!(
            b.center.is_finite() && b.axis_x.is_finite() && b.axis_y.is_finite(),
            "{b:?}"
        );
        assert!(b.distance_sq.is_finite());
    }
    for m in &frame.meshes {
        assert!(m.model.is_finite(), "{m:?}");
        assert!(m.distance_sq.is_finite());
    }
    for em in system.emitters() {
        let live = em.particles.iter().filter(|p| p.alive).count();
        println!(
            "emitter 0x{:08X} owner 0x{:08X} part {} cell 0x{:08X} at {:?}: {live} live, eligible {}",
            em.desc.dat_id,
            em.attached_object_id,
            em.attached_part_index,
            em.owner_cell_id,
            em.anchor_pos,
            em.view_eligible
        );
    }
    println!(
        "{} emitters, {} particles, {} quads and {} models to draw",
        system.active_emitter_count(),
        system.active_particle_count(),
        frame.billboards.len(),
        frame.meshes.len()
    );
}
