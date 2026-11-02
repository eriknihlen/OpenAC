//! A region's terrain atlas is written over several frames, and goes into
//! use only once all of it is written.

use std::path::PathBuf;
use std::sync::Arc;

use ac_host::settings::DisplaySettings;

use super::*;
use crate::assets::{MipChain, TerrainAtlasData};
use crate::camera::FlyInput;
use crate::content::ContentSlot;
use ac_core::terrain::TERRAIN_LAYER_CAPACITY;

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn renderer(device: &wgpu::Device, queue: &wgpu::Queue) -> WorldRenderer {
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
        &DisplaySettings::default(),
        &[1],
    )
}

/// An atlas the size of a region's: 33 layers of 512 by 512 texels with
/// their mips (about 46 MB) and 8 alpha maps.
fn region_sized_atlas() -> TerrainAtlasData {
    let chain = || MipChain {
        width: 512,
        height: 512,
        levels: (0..10)
            .map(|l| vec![0u8; ((512usize >> l).max(1)).pow(2) * 4])
            .collect(),
    };
    TerrainAtlasData {
        layers: (0..33).map(|_| chain()).collect(),
        alpha_width: 512,
        alpha_height: 512,
        alpha_layers: vec![vec![0u8; 512 * 512]; 8],
        tiling: [1.0; TERRAIN_LAYER_CAPACITY],
        blending: ac_core::terrain::TerrainBlendingContext::empty(),
        details: [None, None],
        landscape_detail: None,
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_regions_atlas_goes_up_over_several_frames() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    let mut world = renderer(&device, &queue);
    world.adopt_loaded(
        &device,
        &queue,
        Loaded::Atlas(Box::new(region_sized_atlas())),
    );
    assert!(
        world.terrain_atlas.is_none(),
        "the atlas is not in use before it is written"
    );
    world.prepare(&device, &queue, None, FlyInput::default());
    assert!(
        world.terrain_atlas.is_none() && world.atlas_upload.is_some(),
        "one frame's budget writes only part of it"
    );
    let mut frames = 1;
    while world.terrain_atlas.is_none() {
        world.prepare(&device, &queue, None, FlyInput::default());
        frames += 1;
        assert!(frames < 200, "never finished ({:?})", world.failure());
    }
    assert!(world.atlas_upload.is_none());
    // 8 MiB a frame: the 46 MB take at least six frames.
    assert!(frames >= 6, "written in {frames} frames");
}
