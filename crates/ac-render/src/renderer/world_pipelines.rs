//! Every world pipeline for one sample count, rebuilt whole when the
//! anti-aliasing settings change.

use std::collections::HashMap;

use super::pipelines::{
    icon_probe_pipeline, mesh_pipeline, portal_pipeline, shader, sky_pipeline, terrain_pipeline,
    world_line_pipeline, world_triangle_pipeline,
};
use crate::meshing::{Cull, Translucency};

/// The bind group layouts the world pipelines are built against.
pub struct PipelineLayouts<'a> {
    pub globals: &'a wgpu::BindGroupLayout,
    pub terrain: &'a wgpu::BindGroupLayout,
    pub material: &'a wgpu::BindGroupLayout,
    pub sky: &'a wgpu::BindGroupLayout,
    pub detail: &'a wgpu::BindGroupLayout,
}

/// The terrain, portal-mask, sky and object pipelines.
pub struct WorldPipelines {
    pub terrain: wgpu::RenderPipeline,
    pub portal: wgpu::RenderPipeline,
    /// Straight alpha (0) and additive (1).
    pub sky: [wgpu::RenderPipeline; 2],
    /// By blend (clip maps use the opaque one) and culling.
    pub meshes: HashMap<(Translucency, Cull), wgpu::RenderPipeline>,
    /// The same at one sample, without alpha to coverage: the UI's 3-D
    /// viewports.
    pub viewport_meshes: HashMap<(Translucency, Cull), wgpu::RenderPipeline>,
    /// The plugins' world lines.
    pub world_line: wgpu::RenderPipeline,
    /// The plugins' world triangles.
    pub world_triangle: wgpu::RenderPipeline,
    /// The invisible probes of the icons hidden behind the world.
    pub icon_probe: wgpu::RenderPipeline,
}

impl WorldPipelines {
    /// Build them for `samples` per pixel, alpha-to-coverage on the solid
    /// object pipelines when asked (and multisampled).
    pub fn build(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        samples: u32,
        alpha_to_coverage: bool,
        layouts: &PipelineLayouts<'_>,
    ) -> Self {
        let terrain = terrain_pipeline(device, format, samples, layouts.globals, layouts.terrain);
        let portal = portal_pipeline(device, format, samples, layouts.globals);
        let mesh_shader = shader(device, "mesh", include_str!("../shaders/mesh.wgsl"));
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("mesh"),
            bind_group_layouts: &[
                Some(layouts.globals),
                Some(layouts.material),
                Some(layouts.detail),
            ],
            immediate_size: 0,
        });
        let mesh_set = |samples: u32, alpha_to_coverage: bool| {
            let mut set = HashMap::new();
            for kind in [
                Translucency::Opaque,
                Translucency::AlphaBlend,
                Translucency::Additive,
                Translucency::InvAlpha,
            ] {
                for cull in [Cull::None, Cull::Front, Cull::Back] {
                    let pipeline = mesh_pipeline(
                        device,
                        format,
                        samples,
                        alpha_to_coverage,
                        &mesh_shader,
                        &mesh_layout,
                        kind,
                        cull,
                    );
                    set.insert((kind, cull), pipeline);
                }
            }
            set
        };
        let meshes = mesh_set(samples, alpha_to_coverage);
        // The UI's 3-D viewports draw into single-sampled textures.
        let viewport_meshes = if samples <= 1 && !alpha_to_coverage {
            meshes.clone()
        } else {
            mesh_set(1, false)
        };
        let sky_shader = shader(device, "sky", include_str!("../shaders/sky.wgsl"));
        let sky_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("sky"),
            bind_group_layouts: &[Some(layouts.sky), Some(layouts.material)],
            immediate_size: 0,
        });
        let sky = [false, true].map(|additive| {
            sky_pipeline(device, format, samples, &sky_shader, &sky_layout, additive)
        });
        let world_line = world_line_pipeline(device, format, samples, layouts.globals);
        let world_triangle = world_triangle_pipeline(device, format, samples, layouts.globals);
        let icon_probe = icon_probe_pipeline(device, format, samples, layouts.globals);
        Self {
            terrain,
            portal,
            sky,
            meshes,
            viewport_meshes,
            world_line,
            world_triangle,
            icon_probe,
        }
    }
}

/// The sample count to run with: `requested` when the adapter supports it,
/// else the largest supported count below it, else none (1). 0 and 1 are
/// off.
#[must_use]
pub fn usable_sample_count(requested: u32, supported: &[u32]) -> u32 {
    if requested <= 1 {
        return 1;
    }
    supported
        .iter()
        .copied()
        .filter(|&n| n > 1 && n <= requested)
        .max()
        .unwrap_or(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_sample_count_falls_back_to_the_largest_supported_below_it() {
        assert_eq!(usable_sample_count(0, &[2, 4, 8]), 1);
        assert_eq!(usable_sample_count(4, &[2, 4, 8]), 4);
        assert_eq!(usable_sample_count(8, &[4]), 4);
        assert_eq!(usable_sample_count(2, &[4]), 1);
        assert_eq!(usable_sample_count(16, &[2, 4, 8]), 8);
    }
}
