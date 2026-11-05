//! The retail detail textures: the region's building, environment and
//! landscape detail images. The building and environment images repeat
//! over building shells and interior cell walls and are blended into the
//! lit surface by the mesh shader; they draw only with the "building detail
//! textures" option on and a non-zero tiling. The landscape image repeats
//! over the outdoor terrain near the player and is blended in by the
//! terrain shader the same way, fading out between 10 m and 50 m from the
//! camera; it draws only with the "landscape detail textures" option on
//! and a non-zero tiling.

use super::pipelines::write_layer;
use crate::assets::DetailTexture;

/// Which detail options are on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DetailOptions {
    /// Building shells and interior cells.
    pub building: bool,
    /// The outdoor terrain.
    pub landscape: bool,
}

/// The tilings the shaders read: x building shells, y interior cells, z
/// the landscape; 0 where the detail is off (its option, a missing texture
/// or a zero tiling). `tilings` is building, environment, landscape.
#[must_use]
pub fn detail_tiling(options: DetailOptions, tilings: [Option<f32>; 3]) -> [f32; 4] {
    let on = |enabled: bool, t: Option<f32>| t.filter(|t| enabled && *t != 0.0).unwrap_or(0.0);
    [
        on(options.building, tilings[0]),
        on(options.building, tilings[1]),
        on(options.landscape, tilings[2]),
        0.0,
    ]
}

/// How strongly the landscape detail draws at a terrain vertex
/// `depth_metres` in front of the camera (its view-space depth), 0..=255:
/// full strength nearer than 10 m, fading linearly to nothing at 50 m
/// (truncated to a byte), nothing beyond. The terrain shaders compute the
/// same value per vertex (`landscape_detail_fade` in `terrain.wgsl` and
/// `terrain_atmospheric.wgsl`, as a fraction of 255) and let it
/// interpolate across the triangle; this is the reference they follow.
#[cfg(test)]
#[must_use]
pub fn landscape_detail_fade(depth_metres: f32) -> u8 {
    const NEAR: f32 = 10.0;
    const FAR: f32 = 50.0;
    if depth_metres < NEAR {
        255
    } else if depth_metres <= FAR {
        // Truncated, not rounded.
        ((1.0 - (depth_metres - NEAR) * (1.0 / (FAR - NEAR))) * 255.0) as u8
    } else {
        0
    }
}

/// The detail textures on the GPU and their bind group.
pub(super) struct DetailGpu {
    white: wgpu::TextureView,
    sampler: wgpu::Sampler,
    params: wgpu::Buffer,
    pub group: wgpu::BindGroup,
    /// Building (0), environment (1) and landscape (2): the image and its
    /// tiling.
    textures: [Option<(wgpu::TextureView, f32)>; 3],
    options: DetailOptions,
}

impl DetailGpu {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        layout: &wgpu::BindGroupLayout,
        options: DetailOptions,
    ) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("detail none"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        write_layer(queue, &texture, 0, 0, 1, 1, &[255; 4]);
        let white = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("detail"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::Repeat,
            address_mode_w: wgpu::AddressMode::Repeat,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::MipmapFilterMode::Linear,
            ..Default::default()
        });
        let params = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("detail params"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let group = Self::group(device, layout, &white, &white, &sampler, &params);
        let this = Self {
            white,
            sampler,
            params,
            group,
            textures: [None, None, None],
            options,
        };
        this.write_params(queue);
        this
    }

    fn group(
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        building: &wgpu::TextureView,
        environment: &wgpu::TextureView,
        sampler: &wgpu::Sampler,
        params: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("detail"),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(building),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(environment),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: params.as_entire_binding(),
                },
            ],
        })
    }

    fn write_params(&self, queue: &wgpu::Queue) {
        let tiling = detail_tiling(
            self.options,
            [
                self.textures[0].as_ref().map(|t| t.1),
                self.textures[1].as_ref().map(|t| t.1),
                self.textures[2].as_ref().map(|t| t.1),
            ],
        );
        queue.write_buffer(&self.params, 0, bytemuck::cast_slice(&tiling));
    }

    /// The region's detail textures arrived: upload them with their mips.
    /// The terrain's bind group holds the landscape image, so it is built
    /// again after this.
    pub fn set_textures(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        layout: &wgpu::BindGroupLayout,
        details: &[Option<DetailTexture>; 2],
        landscape: Option<&DetailTexture>,
    ) {
        let sources = [details[0].as_ref(), details[1].as_ref(), landscape];
        for (slot, detail) in self.textures.iter_mut().zip(sources) {
            *slot = detail.map(|d| {
                let image = &d.image;
                let texture = device.create_texture(&wgpu::TextureDescriptor {
                    label: Some("detail"),
                    size: wgpu::Extent3d {
                        width: image.width,
                        height: image.height,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: image.levels.len() as u32,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                    view_formats: &[],
                });
                let (mut w, mut h) = (image.width, image.height);
                for (level, pixels) in image.levels.iter().enumerate() {
                    write_layer(queue, &texture, level as u32, 0, w, h, pixels);
                    (w, h) = ((w / 2).max(1), (h / 2).max(1));
                }
                (
                    texture.create_view(&wgpu::TextureViewDescriptor::default()),
                    d.tiling,
                )
            });
        }
        let view = |i: usize| self.textures[i].as_ref().map_or(&self.white, |t| &t.0);
        self.group = Self::group(
            device,
            layout,
            view(0),
            view(1),
            &self.sampler,
            &self.params,
        );
        self.write_params(queue);
    }

    /// The detail options changed.
    pub fn set_options(&mut self, queue: &wgpu::Queue, options: DetailOptions) {
        if self.options != options {
            self.options = options;
            self.write_params(queue);
        }
    }

    /// The landscape detail image (a white texel without one).
    pub fn landscape_view(&self) -> &wgpu::TextureView {
        self.textures[2].as_ref().map_or(&self.white, |t| &t.0)
    }

    /// The detail images' sampler: repeating, linear with linear mips.
    pub fn sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }

    /// The tilings the shaders read ([`detail_tiling`]).
    pub fn params(&self) -> &wgpu::Buffer {
        &self.params
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `RetailDetailTextureContract.ShouldRender` (the option and an
    //! available texture) and the dispatchers' `Tiling != 0` gate; the
    //! landscape detail behind its own option.

    use super::*;

    const BOTH: DetailOptions = DetailOptions {
        building: true,
        landscape: true,
    };

    #[test]
    fn detail_draws_only_with_the_option_a_texture_and_a_tiling() {
        assert_eq!(
            detail_tiling(BOTH, [Some(10.0), Some(4.0), None]),
            [10.0, 4.0, 0.0, 0.0]
        );
        assert_eq!(
            detail_tiling(DetailOptions::default(), [Some(10.0), Some(4.0), None]),
            [0.0; 4]
        );
        assert_eq!(detail_tiling(BOTH, [None, Some(0.0), None]), [0.0; 4]);
    }

    // The fade the landscape detail takes with distance from the camera
    // (the research note on landscape detail textures): 255 nearer than
    // 10 m, (1 - (z - 10) / 40) * 255 truncated up to 50 m, 0 beyond.
    #[test]
    fn the_landscape_detail_fades_from_ten_to_fifty_metres() {
        let curve: Vec<u8> = [0.0, 10.0, 30.0, 50.0, 60.0]
            .into_iter()
            .map(landscape_detail_fade)
            .collect();
        assert_eq!(curve, [255, 255, 127, 0, 0]);
    }

    /// The text of the shader function `landscape_detail_fade` in `source`:
    /// from its `fn` line to the closing brace at the line's start.
    fn wgsl_fade_function(source: &str) -> &str {
        let start = source
            .find("fn landscape_detail_fade(")
            .expect("the shader has landscape_detail_fade");
        let end = source[start..].find("\n}").expect("the function ends") + start + 2;
        &source[start..end]
    }

    /// `function` (a WGSL `landscape_detail_fade`) run on the GPU at each
    /// of `depths`, as bytes (the fraction of 255 it returns, rounded).
    fn run_on_gpu(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        function: &str,
        depths: &[f32],
    ) -> Vec<u8> {
        let source = format!(
            "@group(0) @binding(0) var<storage, read> depths: array<f32>;\n\
             @group(0) @binding(1) var<storage, read_write> fades: array<f32>;\n\
             {function}\n\
             @compute @workgroup_size(1)\n\
             fn main(@builtin(global_invocation_id) id: vec3<u32>) {{\n\
                 fades[id.x] = landscape_detail_fade(depths[id.x]);\n\
             }}\n"
        );
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("landscape detail fade test"),
            source: wgpu::ShaderSource::Wgsl(source.into()),
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("landscape detail fade test"),
            layout: None,
            module: &module,
            entry_point: Some("main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            cache: None,
        });
        let size = std::mem::size_of_val(depths) as u64;
        let input = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("depths"),
            size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&input, 0, bytemuck::cast_slice(depths));
        let output = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("fades"),
            size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("fades readback"),
            size,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("landscape detail fade test"),
            layout: &pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: input.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: output.as_entire_binding(),
                },
            ],
        });
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(&pipeline);
            pass.set_bind_group(0, &group, &[]);
            pass.dispatch_workgroups(depths.len() as u32, 1, 1);
        }
        encoder.copy_buffer_to_buffer(&output, 0, &readback, 0, size);
        queue.submit([encoder.finish()]);
        readback.map_async(wgpu::MapMode::Read, .., |_| {});
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        let mapped = readback.slice(..).get_mapped_range().expect("mapped");
        let fades: &[f32] = bytemuck::cast_slice(&mapped);
        fades.iter().map(|f| (f * 255.0).round() as u8).collect()
    }

    // Both terrain shaders' fade, run on the GPU, gives the reference's
    // bytes either side of each edge; and the two copies are the same text.
    #[test]
    fn the_terrain_shaders_fade_as_the_reference_does() {
        let shaders = [
            ("terrain.wgsl", include_str!("../shaders/terrain.wgsl")),
            (
                "terrain_atmospheric.wgsl",
                include_str!("../shaders/terrain_atmospheric.wgsl"),
            ),
        ];
        let depths = [0.0, 9.99, 10.0, 30.0, 49.99, 50.0, 60.0];
        let expected: Vec<u8> = depths.into_iter().map(landscape_detail_fade).collect();
        if let Some((device, queue)) = crate::test_gpu::device() {
            for (name, source) in shaders {
                let on_gpu = run_on_gpu(&device, &queue, wgsl_fade_function(source), &depths);
                assert_eq!(on_gpu, expected, "{name} at {depths:?}");
            }
        }
        assert_eq!(
            wgsl_fade_function(shaders[0].1),
            wgsl_fade_function(shaders[1].1),
            "the two terrain shaders' fades differ"
        );
    }

    #[test]
    fn the_landscape_detail_draws_only_with_its_own_option_a_texture_and_a_tiling() {
        assert_eq!(
            detail_tiling(BOTH, [None, None, Some(4.0)]),
            [0.0, 0.0, 4.0, 0.0]
        );
        // Off by its own option, whatever the building option says.
        let building_only = DetailOptions {
            building: true,
            landscape: false,
        };
        assert_eq!(
            detail_tiling(building_only, [Some(10.0), Some(4.0), Some(4.0)]),
            [10.0, 4.0, 0.0, 0.0]
        );
        // On by its own option alone; the building details stay off.
        let landscape_only = DetailOptions {
            building: false,
            landscape: true,
        };
        assert_eq!(
            detail_tiling(landscape_only, [Some(10.0), Some(4.0), Some(4.0)]),
            [0.0, 0.0, 4.0, 0.0]
        );
        // No texture, or a zero tiling: off.
        assert_eq!(detail_tiling(BOTH, [None, None, None]), [0.0; 4]);
        assert_eq!(detail_tiling(BOTH, [None, None, Some(0.0)]), [0.0; 4]);
    }
}
