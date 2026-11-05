//! The UI on the GPU: one pipeline (screen-pixel triangles, straight
//! alpha, no depth), a texture per render surface the UI names, sampled
//! linear or nearest and repeating (a plugin's own images: clamped, with
//! their smaller levels sampled between), and one pass drawn over the
//! world with what the frame's batch holds. A texture whose owner lets go
//! of it is dropped, and its memory with it.
//!
//! OpenAC's `TextRenderer` and the UI half of `TextureCache`
//! (`GetOrUploadRenderSurface`, `UploadUiTexture`): a surface the dats do
//! not hold draws as the 1 x 1 magenta placeholder (named once); a runtime
//! texture nobody assigned draws nothing.

use std::collections::{HashMap, HashSet};

use ac_dat::texture::DecodedTexture;
use ac_ui::draw::{Filter, TextureRef};

use super::batch::{UiBatch, UiTexture, UiTextureKey};

/// Where the UI's texture pixels come from.
pub trait UiTextureSource {
    /// The pixels of `texture`, `None` when there are none.
    fn decode(&mut self, texture: TextureRef) -> Option<UiPixels>;

    /// Textures their owner let go of since the last call, to drop from
    /// the GPU.
    fn released(&mut self) -> Vec<TextureRef> {
        Vec::new()
    }
}

/// A UI texture's pixels as they are uploaded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UiPixels {
    /// 8-bit RGBA.
    Rgba(DecodedTexture),
    /// One byte a texel, drawn as white with that alpha (a font's glyphs):
    /// exactly what the four-channel image (255, 255, 255, value) draws, at
    /// a quarter of the memory.
    Coverage {
        /// Width in texels.
        width: u32,
        /// Height in texels.
        height: u32,
        /// The alpha of each texel, rows top to bottom.
        alpha: Vec<u8>,
    },
    /// A plugin's own 8-bit RGBA image, with its smaller levels (mipmaps)
    /// when it has them, the image first: sampled linearly between
    /// levels too (trilinear), so a large image drawn small shrinks
    /// smoothly, and clamped at its edges, so a part cut from the edge of a
    /// sheet does not pick up the opposite edge.
    Plugin(Vec<DecodedTexture>),
}

/// One level of a texture: its width, height and bytes.
type Level<'a> = (u32, u32, &'a [u8]);

impl UiPixels {
    /// Width and height in texels.
    #[must_use]
    pub fn size(&self) -> (u32, u32) {
        match self {
            Self::Rgba(t) => (t.width, t.height),
            Self::Coverage { width, height, .. } => (*width, *height),
            Self::Plugin(levels) => levels.first().map_or((0, 0), |t| (t.width, t.height)),
        }
    }

    /// Each level's size and bytes, the image first; bytes a texel and the
    /// texture format they upload as.
    fn levels(&self) -> (Vec<Level<'_>>, u32, wgpu::TextureFormat) {
        match self {
            Self::Rgba(t) => (
                vec![(t.width, t.height, t.rgba.as_slice())],
                4,
                wgpu::TextureFormat::Rgba8Unorm,
            ),
            Self::Coverage {
                width,
                height,
                alpha,
            } => (
                vec![(*width, *height, alpha.as_slice())],
                1,
                wgpu::TextureFormat::R8Unorm,
            ),
            Self::Plugin(levels) => (
                levels
                    .iter()
                    .map(|t| (t.width, t.height, t.rgba.as_slice()))
                    .collect(),
                4,
                wgpu::TextureFormat::Rgba8Unorm,
            ),
        }
    }

    /// Every level's bytes fill its size, and each level is half the one
    /// above (rounded down, at least 1), the way the GPU sizes them.
    #[must_use]
    pub fn is_whole(&self) -> bool {
        let (levels, per_texel, _) = self.levels();
        let Some(&(w, h, _)) = levels.first() else {
            return false;
        };
        let max_levels = 32 - w.max(h).leading_zeros();
        w > 0
            && h > 0
            && levels.len() as u32 <= max_levels
            && levels.iter().enumerate().all(|(i, (lw, lh, bytes))| {
                *lw == (w >> i).max(1)
                    && *lh == (h >> i).max(1)
                    && bytes.len() as u64 == u64::from(per_texel) * u64::from(*lw) * u64::from(*lh)
            })
    }

    /// The GPU memory it takes once uploaded, bytes: every level counted.
    #[must_use]
    pub fn gpu_bytes(&self) -> u64 {
        let (levels, per_texel, _) = self.levels();
        levels
            .iter()
            .map(|(w, h, _)| u64::from(per_texel) * u64::from(*w) * u64::from(*h))
            .sum()
    }

    /// Sampled clamped and between levels (a plugin's own image).
    fn clamped(&self) -> bool {
        matches!(self, Self::Plugin(_))
    }
}

struct GpuTexture {
    /// The texture the UI uploaded (`None`: one the renderer draws into,
    /// bound by [`UiRenderer::bind_view`]).
    _texture: Option<wgpu::Texture>,
    /// One byte a texel, drawn by the coverage pipeline.
    coverage: bool,
    /// The GPU memory it takes, every level counted.
    bytes: u64,
    linear: wgpu::BindGroup,
    nearest: wgpu::BindGroup,
}

impl GpuTexture {
    fn bind_group(&self, filter: Filter) -> &wgpu::BindGroup {
        match filter {
            Filter::Linear => &self.linear,
            Filter::Nearest => &self.nearest,
        }
    }
}

/// A run ready to draw.
#[derive(Debug, Clone, Copy)]
struct PreparedRun {
    key: UiTextureKey,
    first: u32,
    count: u32,
}

/// The UI's GPU objects. Built with the window, dropped with it.
pub struct UiRenderer {
    pipeline: wgpu::RenderPipeline,
    /// The pipeline of one-byte coverage textures.
    coverage_pipeline: wgpu::RenderPipeline,
    texture_layout: wgpu::BindGroupLayout,
    screen_buffer: wgpu::Buffer,
    screen_group: wgpu::BindGroup,
    samplers: UiSamplers,
    /// The GPU memory the uploaded textures take, every level counted.
    texture_bytes: u64,
    vertices: wgpu::Buffer,
    vertex_capacity: u64,
    white: GpuTexture,
    textures: HashMap<TextureRef, GpuTexture>,
    /// Textures drawn from a view the renderer draws into, by the view's
    /// generation.
    views: HashMap<TextureRef, u64>,
    missing: HashSet<u32>,
    new_missing: Vec<u32>,
    prepared: Vec<PreparedRun>,
}

impl std::fmt::Debug for UiRenderer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UiRenderer")
            .field("textures", &self.textures.len())
            .field("missing", &self.missing.len())
            .field("runs", &self.prepared.len())
            .finish_non_exhaustive()
    }
}

/// The magenta placeholder of a surface that cannot be read.
fn magenta() -> UiPixels {
    UiPixels::Rgba(DecodedTexture {
        width: 1,
        height: 1,
        rgba: vec![0xFF, 0x00, 0xFF, 0xFF],
    })
}

fn sampler(
    device: &wgpu::Device,
    label: &str,
    filter: wgpu::FilterMode,
    address: wgpu::AddressMode,
    mipmap: wgpu::MipmapFilterMode,
) -> wgpu::Sampler {
    device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some(label),
        address_mode_u: address,
        address_mode_v: address,
        address_mode_w: address,
        mag_filter: filter,
        min_filter: filter,
        mipmap_filter: mipmap,
        ..Default::default()
    })
}

/// The UI's samplers: repeating (dat art, tiled), and clamped with
/// filtering between levels (a plugin's own images).
struct UiSamplers {
    linear: wgpu::Sampler,
    nearest: wgpu::Sampler,
    clamp_linear: wgpu::Sampler,
    clamp_nearest: wgpu::Sampler,
}

impl UiSamplers {
    fn new(device: &wgpu::Device) -> Self {
        use wgpu::{AddressMode, FilterMode, MipmapFilterMode};
        Self {
            linear: sampler(
                device,
                "ui linear repeat",
                FilterMode::Linear,
                AddressMode::Repeat,
                MipmapFilterMode::Nearest,
            ),
            nearest: sampler(
                device,
                "ui nearest repeat",
                FilterMode::Nearest,
                AddressMode::Repeat,
                MipmapFilterMode::Nearest,
            ),
            clamp_linear: sampler(
                device,
                "ui trilinear clamp",
                FilterMode::Linear,
                AddressMode::ClampToEdge,
                MipmapFilterMode::Linear,
            ),
            clamp_nearest: sampler(
                device,
                "ui nearest clamp",
                FilterMode::Nearest,
                AddressMode::ClampToEdge,
                MipmapFilterMode::Nearest,
            ),
        }
    }
}

impl UiRenderer {
    /// The pipeline and samplers for a target of `format`.
    #[must_use]
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue, format: wgpu::TextureFormat) -> Self {
        use crate::renderer::pipelines::{sampler_entry, texture_entry, uniform_entry};
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ui"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/ui.wgsl").into()),
        });
        let screen_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui screen"),
            entries: &[uniform_entry(0, wgpu::ShaderStages::VERTEX)],
        });
        let texture_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui texture"),
            entries: &[
                texture_entry(0, wgpu::TextureViewDimension::D2),
                sampler_entry(1),
            ],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui"),
            bind_group_layouts: &[Some(&screen_layout), Some(&texture_layout)],
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![
            0 => Float32x2,
            1 => Float32x2,
            2 => Float32x4,
        ];
        // Straight alpha: colour = src * a + dst * (1 - a).
        let blend = wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::SrcAlpha,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent::OVER,
        };
        let pipeline_for = |label: &str, fragment: &str| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_main"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    buffers: &[Some(wgpu::VertexBufferLayout {
                        array_stride: size_of::<super::batch::UiVertex>() as u64,
                        step_mode: wgpu::VertexStepMode::Vertex,
                        attributes: &attributes,
                    })],
                },
                primitive: wgpu::PrimitiveState {
                    cull_mode: None,
                    ..Default::default()
                },
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some(fragment),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: Some(blend),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
        };
        let pipeline = pipeline_for("ui", "fs_main");
        let coverage_pipeline = pipeline_for("ui coverage", "fs_coverage");
        let screen_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui screen"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let screen_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ui screen"),
            layout: &screen_layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: screen_buffer.as_entire_binding(),
            }],
        });
        let samplers = UiSamplers::new(device);
        let vertex_capacity = 64 * 1024;
        let vertices = crate::renderer::pipelines::vertex_buffer(
            device,
            "ui vertices",
            vertex_capacity as usize,
        );
        let white = upload(
            device,
            queue,
            &texture_layout,
            &samplers,
            "ui white",
            &UiPixels::Rgba(DecodedTexture {
                width: 1,
                height: 1,
                rgba: vec![0xFF; 4],
            }),
        );
        Self {
            pipeline,
            coverage_pipeline,
            texture_layout,
            screen_buffer,
            screen_group,
            samplers,
            texture_bytes: 0,
            vertices,
            vertex_capacity,
            white,
            textures: HashMap::new(),
            views: HashMap::new(),
            missing: HashSet::new(),
            new_missing: Vec::new(),
            prepared: Vec::new(),
        }
    }

    /// Upload what `batch` draws for a target of `size` pixels; textures
    /// first seen are read from `source`.
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        batch: &UiBatch,
        size: (u32, u32),
        source: &mut dyn UiTextureSource,
    ) {
        for texture in source.released() {
            self.release(texture);
        }
        self.prepared.clear();
        if batch.is_empty() || size.0 == 0 || size.1 == 0 {
            return;
        }
        let screen = [size.0 as f32, size.1 as f32, 0.0, 0.0];
        queue.write_buffer(&self.screen_buffer, 0, bytemuck::cast_slice(&screen));
        let bytes: &[u8] = bytemuck::cast_slice(&batch.vertices);
        let needed = bytes.len() as u64;
        if needed > self.vertex_capacity {
            self.vertex_capacity = needed.next_power_of_two();
            self.vertices = crate::renderer::pipelines::vertex_buffer(
                device,
                "ui vertices",
                self.vertex_capacity as usize,
            );
        }
        queue.write_buffer(&self.vertices, 0, bytes);
        for run in &batch.runs {
            let ready = match run.key.texture {
                UiTexture::White => true,
                UiTexture::Named(texture) => self.ensure(device, queue, texture, source),
            };
            if ready {
                self.prepared.push(PreparedRun {
                    key: run.key,
                    first: run.first,
                    count: run.count,
                });
            }
        }
    }

    /// Draw `texture` from `view`, a texture the renderer draws into (a 3-D
    /// viewport's); `generation` names the view, and the same generation
    /// again keeps the binding.
    pub fn bind_view(
        &mut self,
        device: &wgpu::Device,
        texture: TextureRef,
        view: &wgpu::TextureView,
        generation: u64,
    ) {
        if self.views.get(&texture) == Some(&generation) && self.textures.contains_key(&texture) {
            return;
        }
        let label = match texture {
            TextureRef::Surface(id) => format!("ui view {id:#010x}"),
            TextureRef::Runtime(h) => format!("ui view {h}"),
        };
        let gpu = bind(
            device,
            &self.texture_layout,
            (&self.samplers.linear, &self.samplers.nearest),
            &label,
            view,
        );
        self.textures.insert(texture, gpu);
        self.views.insert(texture, generation);
    }

    /// Gives back a texture its owner let go of (a plugin's image): the GPU
    /// copy goes, and its memory with it. False for one not uploaded.
    pub fn release(&mut self, texture: TextureRef) -> bool {
        self.views.remove(&texture);
        match self.textures.remove(&texture) {
            Some(gpu) => {
                self.texture_bytes -= gpu.bytes;
                true
            }
            None => false,
        }
    }

    /// The GPU memory the uploaded textures take, bytes, every level
    /// counted.
    #[must_use]
    pub fn texture_bytes(&self) -> u64 {
        self.texture_bytes
    }

    /// Render surfaces the dats could not give since the last call (each
    /// named once per window).
    pub fn take_missing(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.new_missing)
    }

    /// How many textures are on the GPU.
    #[must_use]
    pub fn texture_count(&self) -> usize {
        self.textures.len()
    }

    /// Record the prepared UI over what `target` holds.
    pub fn draw(&self, encoder: &mut wgpu::CommandEncoder, target: &wgpu::TextureView) {
        if self.prepared.is_empty() {
            return;
        }
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ui"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            ..Default::default()
        });
        pass.set_pipeline(&self.pipeline);
        let mut coverage = false;
        pass.set_bind_group(0, &self.screen_group, &[]);
        pass.set_vertex_buffer(0, self.vertices.slice(..));
        for run in &self.prepared {
            let texture = match run.key.texture {
                UiTexture::White => &self.white,
                UiTexture::Named(t) => match self.textures.get(&t) {
                    Some(texture) => texture,
                    None => continue,
                },
            };
            if texture.coverage != coverage {
                coverage = texture.coverage;
                pass.set_pipeline(if coverage {
                    &self.coverage_pipeline
                } else {
                    &self.pipeline
                });
            }
            pass.set_bind_group(1, texture.bind_group(run.key.filter), &[]);
            pass.draw(run.first..run.first + run.count, 0..1);
        }
    }

    /// Make sure `texture` is on the GPU; false when it draws nothing.
    fn ensure(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        texture: TextureRef,
        source: &mut dyn UiTextureSource,
    ) -> bool {
        if self.textures.contains_key(&texture) {
            return true;
        }
        let pixels = match (source.decode(texture), texture) {
            (Some(pixels), _) => pixels,
            (None, TextureRef::Surface(id)) => {
                if self.missing.insert(id) {
                    self.new_missing.push(id);
                }
                magenta()
            }
            // A runtime texture nobody has assigned yet: nothing to draw.
            (None, TextureRef::Runtime(_)) => return false,
        };
        let label = match texture {
            TextureRef::Surface(id) => format!("ui surface {id:#010x}"),
            TextureRef::Runtime(h) => format!("ui runtime {h}"),
        };
        let gpu = upload(
            device,
            queue,
            &self.texture_layout,
            &self.samplers,
            &label,
            &pixels,
        );
        self.texture_bytes += gpu.bytes;
        self.textures.insert(texture, gpu);
        true
    }
}

/// Put `pixels` on the GPU, every level, with a linear and a nearest
/// binding: repeating, or for a plugin's own image clamped and sampled
/// between levels. Pixels that do not fill their size (a broken decode)
/// upload as magenta.
fn upload(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    layout: &wgpu::BindGroupLayout,
    samplers: &UiSamplers,
    label: &str,
    pixels: &UiPixels,
) -> GpuTexture {
    let fallback;
    let pixels = if pixels.is_whole() {
        pixels
    } else {
        fallback = magenta();
        &fallback
    };
    let (width, height) = pixels.size();
    let (levels, per_texel, format) = pixels.levels();
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
        mip_level_count: levels.len() as u32,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    for (level, (w, h, bytes)) in levels.iter().enumerate() {
        crate::renderer::pipelines::write_layer_texels(
            queue,
            &texture,
            level as u32,
            0,
            (*w, *h, per_texel),
            bytes,
        );
    }
    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
    let pair = if pixels.clamped() {
        (&samplers.clamp_linear, &samplers.clamp_nearest)
    } else {
        (&samplers.linear, &samplers.nearest)
    };
    GpuTexture {
        _texture: Some(texture),
        coverage: matches!(pixels, UiPixels::Coverage { .. }),
        bytes: pixels.gpu_bytes(),
        ..bind(device, layout, pair, label, &view)
    }
}

/// A texture view bound with a linear and a nearest sampler.
fn bind(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    (linear, nearest): (&wgpu::Sampler, &wgpu::Sampler),
    label: &str,
    view: &wgpu::TextureView,
) -> GpuTexture {
    let group = |sampler: &wgpu::Sampler| {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some(label),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
            ],
        })
    };
    GpuTexture {
        linear: group(linear),
        nearest: group(nearest),
        _texture: None,
        bytes: 0,
        coverage: false,
    }
}

#[cfg(test)]
mod tests {
    use super::super::batch::{UiRun, UiVertex};
    use super::*;
    use ac_ui::draw::Color;

    struct Source(HashMap<TextureRef, UiPixels>);

    impl UiTextureSource for Source {
        fn decode(&mut self, texture: TextureRef) -> Option<UiPixels> {
            self.0.remove(&texture)
        }
    }

    /// An 8 x 4 image's levels as the CPU mip chain builds them.
    fn plugin_image() -> UiPixels {
        UiPixels::Plugin(
            ac_plugin_ui::mips::build(vec![0x80; 8 * 4 * 4], 8, 4)
                .into_iter()
                .map(|l| DecodedTexture {
                    width: l.width,
                    height: l.height,
                    rgba: l.rgba,
                })
                .collect(),
        )
    }

    // OpenAC PR #271's TextureCacheReleasableUiTextureTests
    // (AMipmappedUploadCountsEveryLevelInTheMemoryTrackerAndGivesThemAllBack,
    // APlainUploadHasOneLevel): 8x4, 4x2, 2x1, 1x1, every level counted.
    #[test]
    fn a_plugin_image_counts_every_level_and_is_sampled_clamped() {
        let pixels = plugin_image();
        assert!(pixels.is_whole());
        assert_eq!(pixels.levels().0.len(), 4);
        assert_eq!(pixels.gpu_bytes(), (8 * 4 + 4 * 2 + 2 + 1) * 4);
        assert!(pixels.clamped());
        let plain = UiPixels::Rgba(DecodedTexture {
            width: 8,
            height: 4,
            rgba: vec![0; 8 * 4 * 4],
        });
        assert_eq!(plain.levels().0.len(), 1);
        assert_eq!(plain.gpu_bytes(), 8 * 4 * 4);
        assert!(!plain.clamped());
        // A plugin's pixels without smaller levels are clamped too.
        let one = UiPixels::Plugin(vec![DecodedTexture {
            width: 2,
            height: 2,
            rgba: vec![0; 16],
        }]);
        assert!(one.is_whole() && one.clamped());
        assert_eq!(one.gpu_bytes(), 16);
    }

    // A level that is not half the one above, or does not fill its size,
    // uploads as the magenta placeholder rather than a broken texture.
    #[test]
    fn a_broken_level_chain_is_not_whole() {
        let level = |w: u32, h: u32| DecodedTexture {
            width: w,
            height: h,
            rgba: vec![0; (w * h * 4) as usize],
        };
        assert!(!UiPixels::Plugin(vec![level(8, 4), level(3, 2)]).is_whole());
        assert!(!UiPixels::Plugin(vec![level(1, 1), level(1, 1)]).is_whole());
        assert!(!UiPixels::Plugin(Vec::new()).is_whole());
        let mut short = level(4, 4);
        short.rgba.pop();
        assert!(!UiPixels::Plugin(vec![short]).is_whole());
    }

    // OpenAC PR #271's AMipmappedUploadSendsEveryLevelAndSamplesThemTrilinearly
    // and the GPU memory it counts: uploaded with every level, counted, and
    // given back, memory and all, when its owner lets go of it.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_plugin_image_uploads_every_level_and_gives_its_memory_back() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let mut renderer = UiRenderer::new(&device, &queue, wgpu::TextureFormat::Rgba8Unorm);
        let texture = TextureRef::Runtime(7);
        struct Once(Option<UiPixels>, Vec<TextureRef>);
        impl UiTextureSource for Once {
            fn decode(&mut self, _texture: TextureRef) -> Option<UiPixels> {
                self.0.take()
            }
            fn released(&mut self) -> Vec<TextureRef> {
                std::mem::take(&mut self.1)
            }
        }
        let batch = UiBatch {
            vertices: quad(0.0, 8.0).to_vec(),
            runs: vec![super::super::batch::UiRun {
                key: UiTextureKey {
                    texture: UiTexture::Named(texture),
                    filter: Filter::Linear,
                },
                first: 0,
                count: 6,
            }],
            skipped_zero_ids: 0,
        };
        let before = renderer.texture_bytes();
        let mut source = Once(Some(plugin_image()), Vec::new());
        renderer.prepare(&device, &queue, &batch, (W, H), &mut source);
        assert_eq!(
            renderer.texture_bytes() - before,
            (8 * 4 + 4 * 2 + 2 + 1) * 4
        );
        assert_eq!(renderer.texture_count(), 1);
        source.1.push(texture);
        renderer.prepare(&device, &queue, &UiBatch::default(), (W, H), &mut source);
        assert_eq!(renderer.texture_bytes(), before);
        assert_eq!(renderer.texture_count(), 0);
        assert!(!renderer.release(texture));
    }

    const W: u32 = 24;
    const H: u32 = 4;

    /// A quad over pixel columns `x0..x1`, the whole texture stretched
    /// over it (so linear filtering blends texels), tinted.
    fn quad(x0: f32, x1: f32) -> [UiVertex; 6] {
        let tint = [0.9, 0.5, 0.25, 0.8];
        let v = |x: f32, y: f32, u: f32, w: f32| UiVertex {
            position: [x, y],
            uv: [u, w],
            color: tint,
        };
        let h = H as f32;
        [
            v(x0, 0.0, 0.0, 0.0),
            v(x1, 0.0, 1.0, 0.0),
            v(x0, h, 0.0, 1.0),
            v(x1, 0.0, 1.0, 0.0),
            v(x1, h, 1.0, 1.0),
            v(x0, h, 0.0, 1.0),
        ]
    }

    /// Three quads side by side: texture 1, texture 2, texture 1 again,
    /// over a grey background; the target's RGBA bytes.
    fn draw(device: &wgpu::Device, queue: &wgpu::Queue, second: UiPixels) -> Vec<u8> {
        let first = UiPixels::Rgba(DecodedTexture {
            width: 2,
            height: 2,
            rgba: vec![
                255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 64, 200, 100, 50, 255,
            ],
        });
        let mut source = Source(HashMap::from([
            (TextureRef::Surface(1), first),
            (TextureRef::Surface(2), second),
        ]));
        let run = |texture: u32, first: u32| UiRun {
            key: UiTextureKey {
                texture: UiTexture::Named(TextureRef::Surface(texture)),
                filter: Filter::Linear,
            },
            first,
            count: 6,
        };
        let batch = UiBatch {
            vertices: [quad(0.0, 8.0), quad(8.0, 16.0), quad(16.0, 24.0)].concat(),
            runs: vec![run(1, 0), run(2, 6), run(1, 12)],
            skipped_zero_ids: 0,
        };
        draw_batch(
            device,
            queue,
            &batch,
            &mut source,
            (W, H),
            (W, H),
            [40, 80, 120, 255],
        )
    }

    /// `batch` drawn for a UI of `ui_size` units over a target of `pixels`
    /// filled with `background`; the target's RGBA bytes.
    fn draw_batch(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        batch: &UiBatch,
        source: &mut dyn UiTextureSource,
        ui_size: (u32, u32),
        (w, h): (u32, u32),
        background: [u8; 4],
    ) -> Vec<u8> {
        let format = wgpu::TextureFormat::Rgba8Unorm;
        let target = device.create_texture(&wgpu::TextureDescriptor {
            label: None,
            size: wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let fill: Vec<u8> = (0..w * h).flat_map(|_| background).collect();
        crate::renderer::pipelines::write_layer(queue, &target, 0, 0, w, h, &fill);
        let mut renderer = UiRenderer::new(device, queue, format);
        renderer.prepare(device, queue, batch, ui_size, source);
        let view = target.create_view(&wgpu::TextureViewDescriptor::default());
        let row = (w * 4).next_multiple_of(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT);
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: u64::from(row * h),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        renderer.draw(&mut encoder, &view);
        encoder.copy_texture_to_buffer(
            target.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &readback,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(row),
                    rows_per_image: Some(h),
                },
            },
            target.size(),
        );
        queue.submit([encoder.finish()]);
        readback.map_async(wgpu::MapMode::Read, .., |_| {});
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        let mapped = readback.slice(..).get_mapped_range().expect("mapped");
        mapped
            .chunks(row as usize)
            .flat_map(|bytes| bytes[..(w * 4) as usize].to_vec())
            .collect()
    }

    /// What `paint` draws in white over black: a UI of `ui` units, `pixels`
    /// framebuffer pixels a unit, read back as one red byte a pixel.
    fn shape_pixels(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        ui: u32,
        pixels: u32,
        paint: impl FnOnce(&mut ac_ui::draw::RenderContext<'_>),
    ) -> Vec<u8> {
        let mut list = ac_ui::draw::DrawList {
            canvas_scale: (1.0, 1.0),
            ..Default::default()
        };
        let mut context = ac_ui::draw::RenderContext::new(
            &mut list,
            (ui as f32, ui as f32),
            &ac_ui::draw::NoSprites,
        )
        .with_pixels_per_unit(pixels as f32);
        paint(&mut context);
        let side = ui * pixels;
        let drawn = draw_batch(
            device,
            queue,
            &UiBatch::build(&list),
            &mut Source(HashMap::new()),
            (ui, ui),
            (side, side),
            [0, 0, 0, 255],
        );
        drawn.chunks(4).map(|p| p[0]).collect()
    }

    /// A rectangle's edge on a pixel boundary comes out crisp at any scale:
    /// the soft edge is one framebuffer pixel wide, its pixel centres at no
    /// coverage and at full. Half a pixel off, that pixel is half covered.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_shapes_soft_edge_is_one_framebuffer_pixel_wide() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let square = ac_ui::shapes::CornerRadii::default();
        let row = |bytes: &[u8], side: usize, y: usize| bytes[y * side..(y + 1) * side].to_vec();

        let one = shape_pixels(&device, &queue, 16, 1, |c| {
            c.fill_rounded_rect(4.0, 4.0, 8.0, 8.0, square, Color::WHITE);
        });
        assert_eq!(
            row(&one, 16, 8)[2..7],
            [0, 0, 255, 255, 255],
            "crisp at one pixel a unit"
        );

        let two = shape_pixels(&device, &queue, 16, 2, |c| {
            c.fill_rounded_rect(4.0, 4.0, 8.0, 8.0, square, Color::WHITE);
        });
        assert_eq!(
            row(&two, 32, 16)[6..11],
            [0, 0, 255, 255, 255],
            "crisp at two pixels a unit"
        );

        let off = shape_pixels(&device, &queue, 16, 1, |c| {
            c.fill_rounded_rect(4.5, 4.0, 8.0, 8.0, square, Color::WHITE);
        });
        let half = row(&off, 16, 8)[4];
        assert!((126..=129).contains(&half), "half covered, got {half}");
    }

    /// What a curve lights up comes to its own area, and its rim holds pixels
    /// between dark and full: it really is smooth.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_circle_lights_its_own_area_with_a_smooth_rim() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let drawn = shape_pixels(&device, &queue, 64, 1, |c| {
            c.fill_ellipse(12.0, 12.0, 40.0, 40.0, Color::WHITE);
        });
        let lit: f64 = drawn.iter().map(|&p| f64::from(p) / 255.0).sum();
        let area = std::f64::consts::PI * 20.0 * 20.0;
        assert!((lit - area).abs() <= area * 0.01, "lit {lit}, area {area}");
        let between = drawn.iter().filter(|&&p| p > 0 && p < 255).count();
        assert!(between > 60, "only {between} rim pixels between");
    }

    /// A gradient draws bright to dark from top to bottom, with no step
    /// larger than its share of the height.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_gradient_runs_smoothly_down_the_shape() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let drawn = shape_pixels(&device, &queue, 64, 1, |c| {
            c.fill_vertical_gradient(
                8.0,
                8.0,
                48.0,
                48.0,
                ac_ui::shapes::CornerRadii::default(),
                Color::WHITE,
                Color::BLACK,
            );
        });
        let column: Vec<u8> = (9..55).map(|y| drawn[y * 64 + 32]).collect();
        assert!(
            column[0] > 240 && column[column.len() - 1] < 15,
            "{column:?}"
        );
        for pair in column.windows(2) {
            assert!(pair[0] > pair[1], "{column:?}");
            assert!(pair[0] - pair[1] <= 7, "{column:?}");
        }
    }

    /// A horizontal gradient draws bright to dark from left to right, with no
    /// step larger than its share of the width.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_horizontal_gradient_runs_smoothly_across_the_shape() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let drawn = shape_pixels(&device, &queue, 64, 1, |c| {
            c.fill_horizontal_gradient(
                8.0,
                8.0,
                48.0,
                48.0,
                ac_ui::shapes::CornerRadii::default(),
                Color::WHITE,
                Color::BLACK,
            );
        });
        let row: Vec<u8> = (9..55).map(|x| drawn[32 * 64 + x]).collect();
        assert!(row[0] > 240 && row[row.len() - 1] < 15, "{row:?}");
        for pair in row.windows(2) {
            assert!(pair[0] > pair[1], "{row:?}");
            assert!(pair[0] - pair[1] <= 7, "{row:?}");
        }
        // A column stays one shade from top to bottom.
        let column: Vec<u8> = (9..55).map(|y| drawn[y * 64 + 32]).collect();
        assert!(column.iter().all(|&p| p == column[0]), "{column:?}");
    }

    /// A shadow darkens most beside its caster, fades smoothly and is gone
    /// past its spread.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_shadow_fades_out_from_its_caster() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let drawn = shape_pixels(&device, &queue, 64, 1, |c| {
            c.soft_shadow(20.0, 20.0, 24.0, 24.0, 4.0, 0.0, 12.0, Color::WHITE);
        });
        let across: Vec<u8> = (44..58).map(|x| drawn[32 * 64 + x]).collect();
        assert!(across[0] > 100, "{across:?}");
        assert_eq!(across[across.len() - 1], 0, "{across:?}");
        // No flat runs and no jumps: it fades, it does not step.
        let fading = &across[..12];
        for pair in fading.windows(2) {
            assert!(pair[0] > pair[1], "{across:?}");
            assert!(pair[0] - pair[1] <= 25, "{across:?}");
        }
    }

    /// A one-byte coverage texture draws exactly as its four-channel image
    /// (255, 255, 255, value) did, filtered and tinted, and the draws after
    /// it go back to the four-channel pipeline.
    #[test]
    #[ignore = "needs a GPU"]
    fn a_coverage_texture_draws_as_its_white_four_channel_image() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let alpha: Vec<u8> = vec![0, 30, 90, 160, 200, 255, 10, 250];
        let rgba = UiPixels::Rgba(DecodedTexture {
            width: 4,
            height: 2,
            rgba: alpha.iter().flat_map(|&a| [255, 255, 255, a]).collect(),
        });
        let coverage = UiPixels::Coverage {
            width: 4,
            height: 2,
            alpha,
        };
        let expected = draw(&device, &queue, rgba);
        let drawn = draw(&device, &queue, coverage);
        assert!(
            expected.chunks(4).any(|p| p != [40, 80, 120, 255]),
            "the quads drew"
        );
        assert_eq!(drawn, expected);
    }
}
