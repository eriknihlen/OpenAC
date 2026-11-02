//! The retail detail textures: the region's building, environment and
//! landscape detail images. The building and environment images repeat
//! over building shells and interior cell walls and are blended into the
//! lit surface by the mesh shader; they draw only with the "building detail
//! textures" option on and a non-zero tiling. The landscape image repeats
//! over the outdoor terrain near the player and is blended in by the
//! terrain shader the same way; it draws only with the "landscape detail
//! textures" option on and a non-zero tiling.

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
