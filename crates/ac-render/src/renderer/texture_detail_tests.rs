//! A texture-detail change (Potato Mode, or the two texture-detail rows)
//! reaches the textures the running world already has.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_host::settings::DisplaySettings;

use super::*;
use crate::camera::FlyInput;
use crate::content::ContentSlot;
use crate::quality::effective_display;

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn renderer(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    display: &DisplaySettings,
) -> WorldRenderer {
    let content = Arc::new(ContentSlot::new(dat_dir()));
    content.get().expect("open the retail dats");
    let settings = WorldSettings {
        content,
        quality_overrides: crate::quality::QualityOverrides {
            near_radius: Some(0),
            far_radius: Some(0),
            msaa_samples: Some(0),
            ..Default::default()
        },
        residency: Default::default(),
        orbit: Default::default(),
        reveal_radius: None,
        sky: Default::default(),
        audio: Default::default(),
    };
    WorldRenderer::new(
        device,
        queue,
        wgpu::TextureFormat::Rgba8Unorm,
        96,
        64,
        &settings,
        &effective_display(display),
        &[1],
    )
}

/// Prepare frames until `done` holds (at most a minute).
fn frames_until(
    world: &mut WorldRenderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    what: &str,
    mut done: impl FnMut(&WorldRenderer) -> bool,
) {
    let started = Instant::now();
    loop {
        world.prepare(device, queue, None, FlyInput::default());
        if done(world) {
            return;
        }
        assert!(
            started.elapsed() < Duration::from_secs(60),
            "never: {what} ({:?})",
            world.failure()
        );
    }
}

/// Turning Potato Mode on in a running world brings every world texture
/// it holds down to an eighth (a 64th of the bytes), without a restart;
/// turning it off brings them back to full size.
#[test]
#[ignore = "needs the retail dat files"]
fn potato_mode_resizes_the_textures_of_the_running_world() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    let stored = DisplaySettings::default();
    let mut world = renderer(&device, &queue, &stored);
    // Holtburg's cells: their surfaces are sent ahead of the landblock.
    world
        .loader
        .request(LoadRequest::Near(crate::world_frame::LandblockCoord {
            x: 0xA9,
            y: 0xB4,
        }));
    frames_until(&mut world, &device, &queue, "the cells' surfaces", |w| {
        w.surfaces.values().filter(|s| s.bytes > 4096).count() >= 4
    });
    let full: HashMap<u32, u64> = world
        .surfaces
        .iter()
        .filter(|(_, s)| s.bytes > 4096)
        .map(|(id, s)| (*id, s.bytes))
        .collect();
    let potato = DisplaySettings {
        potato_mode: true,
        ..stored.clone()
    };
    world.apply_display(&device, &queue, &effective_display(&potato));
    frames_until(
        &mut world,
        &device,
        &queue,
        "the surfaces at an eighth",
        |w| {
            full.iter().all(|(id, bytes)| {
                w.surfaces
                    .get(id)
                    .is_some_and(|s| s.bytes.saturating_mul(16) <= *bytes)
            })
        },
    );
    world.apply_display(&device, &queue, &effective_display(&stored));
    frames_until(
        &mut world,
        &device,
        &queue,
        "the surfaces back at full size",
        |w| {
            full.iter()
                .all(|(id, bytes)| w.surfaces.get(id).is_some_and(|s| s.bytes == *bytes))
        },
    );
}
