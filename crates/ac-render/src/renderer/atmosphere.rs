//! The atmospheric rendering on the GPU: the HDR world target and its
//! pipelines, the shadow maps and their casters' pipelines, and the post
//! passes (sun mask and rays, light shafts, bloom, the filmic composite).
//!
//! Built when the option turns on and dropped when it turns off; with it
//! off the world pass is the plain one and none of this exists. The rules
//! (what runs, how strongly) are `crate::atmosphere`.

use std::collections::HashMap;
use std::time::Instant;

use bytemuck::{Pod, Zeroable};

use super::particles::{ParticleGpu, ParticlePipelines};
use super::pipelines::{
    DEPTH_FORMAT, icon_probe_pipeline, mesh_pipeline_with, navigation_line_pipeline,
    portal_pipeline, shader, sky_pipeline, uniform_entry, world_line_pipeline,
    world_triangle_pipeline,
};
use crate::assets::{MeshGpuVertex, TerrainGpuVertex};
use crate::atmosphere::auto_quality::{
    AutoQualityController, PerformanceWindow, QualityLevel, QualityMeasurement,
};
use crate::atmosphere::shadow::{Cascade, ShadowEnvironmentState, ShadowUniforms};
use crate::atmosphere::wind::WindState;
use crate::atmosphere::{AtmosphereConfig, FrameInputs, color, volumetric};
use crate::draws::{InstanceGpu, TerrainInstanceGpu};
use crate::meshing::{Cull, Translucency};

/// The star layer's model, drawn as the enhanced night sky (OpenAC
/// `SkyRenderer.StarLayerGfxObjId`).
pub(crate) const STAR_LAYER_GFX_OBJ_ID: u32 = 0x0100_15EF;

/// The world target's format: half floats, so light can pass 1.
pub(crate) const HDR_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
const DEPTH_COPY_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::R32Float;
const MASK_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
const SHADOW_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth32Float;

/// Distance between uniform slots read with a dynamic offset.
const SLOT: u64 = 256;
/// Post-pass parameter slots: occlusion, rays, shafts, bloom down, blur
/// across, blur down, filmic.
const PASS_SLOTS: u64 = 8;

/// The atmospheric frame block (OpenAC `AtmosphericFrameUniforms`, 192
/// bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub(crate) struct AtmosphericFrameGpu {
    sun_screen: [f32; 4],
    sun_color: [f32; 4],
    viewport: [f32; 4],
    weather: [f32; 4],
    sun_direction: [f32; 4],
    policy: [f32; 4],
    inverse_view_projection: [f32; 16],
    clock_wind: [f32; 4],
    wind_amplitude: [f32; 4],
}

/// One post pass's parameters (OpenAC `AtmosphericPackPassUniforms`).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable, Default)]
struct PassParamsGpu {
    p: [[f32; 4]; 4],
}

/// One cascade's caster block.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct CascadeGpu {
    world_to_clip: [f32; 16],
    clock_wind: [f32; 4],
    wind_amplitude: [f32; 4],
}

/// The pipelines the world pass draws with when the option is on: the
/// plain world's, into the HDR target, with the shadow receivers, the
/// wind and the night sky.
pub(crate) struct HdrWorldPipelines {
    pub terrain: wgpu::RenderPipeline,
    pub portal: wgpu::RenderPipeline,
    pub sky: [wgpu::RenderPipeline; 2],
    pub sky_night: wgpu::RenderPipeline,
    /// By blend (clip maps apart, for the wind) and culling.
    pub meshes: HashMap<(Translucency, Cull), wgpu::RenderPipeline>,
    pub billboard: [wgpu::RenderPipeline; 2],
    pub particle_meshes: HashMap<(crate::draws::ParticleBlend, Cull), wgpu::RenderPipeline>,
    /// The plugins' world lines.
    pub world_line: wgpu::RenderPipeline,
    pub navigation_line: wgpu::RenderPipeline,
    /// The plugins' world triangles.
    pub world_triangle: wgpu::RenderPipeline,
    /// The invisible probes of the icons hidden behind the world.
    pub icon_probe: wgpu::RenderPipeline,
}

/// The layouts the plain world's pipelines are built against.
pub(crate) struct WorldLayouts<'a> {
    pub globals: &'a wgpu::BindGroupLayout,
    pub terrain: &'a wgpu::BindGroupLayout,
    pub material: &'a wgpu::BindGroupLayout,
    pub sky: &'a wgpu::BindGroupLayout,
    pub detail: &'a wgpu::BindGroupLayout,
}

/// A shader with the atmosphere's common pieces in front; a world
/// `receiver` (drawn through portal views) also gets the shadow receiver
/// and the clip-region helpers.
fn atmosphere_shader(
    device: &wgpu::Device,
    label: &str,
    body: &str,
    receiver: bool,
) -> wgpu::ShaderModule {
    let mut source = String::from(include_str!("../shaders/atmosphere_common.wgsl"));
    if receiver {
        source.push('\n');
        source.push_str(include_str!("../shaders/shadow_receiver.wgsl"));
    }
    source.push('\n');
    source.push_str(body);
    if receiver {
        shader(device, label, &source)
    } else {
        device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some(label),
            source: wgpu::ShaderSource::Wgsl(source.into()),
        })
    }
}

impl HdrWorldPipelines {
    fn build(
        device: &wgpu::Device,
        samples: u32,
        alpha_to_coverage: bool,
        layouts: &WorldLayouts<'_>,
        atmosphere: &wgpu::BindGroupLayout,
    ) -> Self {
        let format = HDR_FORMAT;
        let terrain = terrain_receiver_pipeline(device, samples, layouts, atmosphere);
        let portal = portal_pipeline(device, format, samples, layouts.globals);
        let mesh_shader = atmosphere_shader(
            device,
            "mesh atmospheric",
            include_str!("../shaders/mesh_atmospheric.wgsl"),
            true,
        );
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("mesh atmospheric"),
            bind_group_layouts: &[
                Some(layouts.globals),
                Some(layouts.material),
                Some(layouts.detail),
                Some(atmosphere),
            ],
            immediate_size: 0,
        });
        let mut meshes = HashMap::new();
        for (kind, wind) in [
            (Translucency::Opaque, 4.0),
            (Translucency::ClipMap, 2.0),
            (Translucency::AlphaBlend, 0.0),
            (Translucency::Additive, 0.0),
            (Translucency::InvAlpha, 0.0),
        ] {
            for cull in [Cull::None, Cull::Front, Cull::Back] {
                meshes.insert(
                    (kind, cull),
                    mesh_pipeline_with(
                        device,
                        format,
                        samples,
                        alpha_to_coverage,
                        &mesh_shader,
                        &mesh_layout,
                        kind,
                        cull,
                        &[("WIND_KIND", wind)],
                    ),
                );
            }
        }
        let sky_shader = shader(device, "sky", include_str!("../shaders/sky.wgsl"));
        let sky_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("sky hdr"),
            bind_group_layouts: &[Some(layouts.sky), Some(layouts.material)],
            immediate_size: 0,
        });
        let sky = [false, true].map(|additive| {
            sky_pipeline(device, format, samples, &sky_shader, &sky_layout, additive)
        });
        let night_shader = shader(
            device,
            "sky night",
            include_str!("../shaders/sky_night.wgsl"),
        );
        let sky_night = sky_pipeline(device, format, samples, &night_shader, &sky_layout, true);
        let (billboard, particle_meshes): ParticlePipelines =
            ParticleGpu::pipelines(device, format, samples, layouts.globals, layouts.material);
        let world_line = world_line_pipeline(device, format, samples, layouts.globals);
        let navigation_line = navigation_line_pipeline(device, format, samples, layouts.globals);
        let world_triangle = world_triangle_pipeline(device, format, samples, layouts.globals);
        let icon_probe = icon_probe_pipeline(device, format, samples, layouts.globals);
        Self {
            terrain,
            portal,
            sky,
            sky_night,
            meshes,
            billboard,
            particle_meshes,
            world_line,
            navigation_line,
            world_triangle,
            icon_probe,
        }
    }
}

fn terrain_receiver_pipeline(
    device: &wgpu::Device,
    samples: u32,
    layouts: &WorldLayouts<'_>,
    atmosphere: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    let module = atmosphere_shader(
        device,
        "terrain atmospheric",
        include_str!("../shaders/terrain_atmospheric.wgsl"),
        true,
    );
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("terrain atmospheric"),
        bind_group_layouts: &[
            Some(layouts.globals),
            Some(layouts.terrain),
            Some(atmosphere),
        ],
        immediate_size: 0,
    });
    let vertex_attributes = terrain_vertex_attributes();
    let offset_attributes = wgpu::vertex_attr_array![6 => Float32x3, 7 => Uint32];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("terrain atmospheric"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &terrain_buffers(&vertex_attributes, &offset_attributes),
        },
        primitive: wgpu::PrimitiveState {
            cull_mode: Some(wgpu::Face::Back),
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(true)),
        multisample: super::pipelines::multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some("fs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format: HDR_FORMAT,
                blend: None,
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        multiview_mask: None,
        cache: None,
    })
}

fn terrain_vertex_attributes() -> [wgpu::VertexAttribute; 6] {
    wgpu::vertex_attr_array![
        0 => Float32x3,
        1 => Float32x3,
        2 => Uint8x4,
        3 => Uint8x4,
        4 => Uint8x4,
        5 => Uint8x4,
    ]
}

fn terrain_buffers<'a>(
    vertex: &'a [wgpu::VertexAttribute],
    offset: &'a [wgpu::VertexAttribute],
) -> [Option<wgpu::VertexBufferLayout<'a>>; 2] {
    [
        Some(wgpu::VertexBufferLayout {
            array_stride: size_of::<TerrainGpuVertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: vertex,
        }),
        Some(wgpu::VertexBufferLayout {
            array_stride: size_of::<TerrainInstanceGpu>() as u64,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: offset,
        }),
    ]
}

fn mesh_instance_attributes() -> [wgpu::VertexAttribute; 10] {
    wgpu::vertex_attr_array![
        3 => Float32x4,
        4 => Float32x4,
        5 => Float32x4,
        6 => Float32x4,
        7 => Sint32x4,
        8 => Sint32x4,
        9 => Uint32,
        11 => Float32,
        12 => Uint32,
        13 => Float32x2,
    ]
}

fn depth_state(write: bool) -> wgpu::DepthStencilState {
    wgpu::DepthStencilState {
        format: DEPTH_FORMAT,
        depth_write_enabled: Some(write),
        depth_compare: Some(wgpu::CompareFunction::Less),
        stencil: wgpu::StencilState::default(),
        bias: wgpu::DepthBiasState::default(),
    }
}

/// The shadow maps' caster pipelines: terrain, solid sub-meshes and
/// clip-mapped ones, each culled as the world culls them.
pub(crate) struct CasterPipelines {
    pub terrain: wgpu::RenderPipeline,
    pub opaque: HashMap<Cull, wgpu::RenderPipeline>,
    pub cutout: HashMap<Cull, wgpu::RenderPipeline>,
}

impl CasterPipelines {
    fn build(
        device: &wgpu::Device,
        cascade_layout: &wgpu::BindGroupLayout,
        material_layout: &wgpu::BindGroupLayout,
    ) -> Self {
        let module = atmosphere_shader(
            device,
            "shadow caster",
            include_str!("../shaders/shadow_caster.wgsl"),
            false,
        );
        let terrain_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("shadow terrain"),
            bind_group_layouts: &[Some(cascade_layout)],
            immediate_size: 0,
        });
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("shadow mesh"),
            bind_group_layouts: &[Some(cascade_layout), Some(material_layout)],
            immediate_size: 0,
        });
        let shadow_depth = wgpu::DepthStencilState {
            format: SHADOW_FORMAT,
            depth_write_enabled: Some(true),
            depth_compare: Some(wgpu::CompareFunction::Less),
            stencil: wgpu::StencilState::default(),
            bias: wgpu::DepthBiasState::default(),
        };
        let vertex_attributes = terrain_vertex_attributes();
        let offset_attributes = wgpu::vertex_attr_array![6 => Float32x3];
        let terrain = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("shadow terrain"),
            layout: Some(&terrain_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_terrain"),
                compilation_options: wgpu::PipelineCompilationOptions::default(),
                buffers: &terrain_buffers(&vertex_attributes, &offset_attributes),
            },
            primitive: wgpu::PrimitiveState {
                cull_mode: Some(wgpu::Face::Back),
                front_face: wgpu::FrontFace::Ccw,
                ..Default::default()
            },
            depth_stencil: Some(shadow_depth.clone()),
            multisample: wgpu::MultisampleState::default(),
            fragment: None,
            multiview_mask: None,
            cache: None,
        });
        let mesh_vertex = wgpu::vertex_attr_array![
            0 => Float32x3,
            1 => Float32x3,
            2 => Float32x2,
        ];
        let instance = mesh_instance_attributes();
        let mesh = |cull: Cull, cutout: bool| {
            let constants = [("WIND_KIND", if cutout { 2.0 } else { 4.0 })];
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(if cutout {
                    "shadow cutout"
                } else {
                    "shadow opaque"
                }),
                layout: Some(&mesh_layout),
                vertex: wgpu::VertexState {
                    module: &module,
                    entry_point: Some("vs_mesh"),
                    compilation_options: wgpu::PipelineCompilationOptions {
                        constants: &constants,
                        ..Default::default()
                    },
                    buffers: &[
                        Some(wgpu::VertexBufferLayout {
                            array_stride: size_of::<MeshGpuVertex>() as u64,
                            step_mode: wgpu::VertexStepMode::Vertex,
                            attributes: &mesh_vertex,
                        }),
                        Some(wgpu::VertexBufferLayout {
                            array_stride: size_of::<InstanceGpu>() as u64,
                            step_mode: wgpu::VertexStepMode::Instance,
                            attributes: &instance,
                        }),
                    ],
                },
                primitive: wgpu::PrimitiveState {
                    cull_mode: match cull {
                        Cull::None => None,
                        Cull::Front => Some(wgpu::Face::Front),
                        Cull::Back => Some(wgpu::Face::Back),
                    },
                    front_face: wgpu::FrontFace::Ccw,
                    ..Default::default()
                },
                depth_stencil: Some(shadow_depth.clone()),
                multisample: wgpu::MultisampleState::default(),
                fragment: cutout.then(|| wgpu::FragmentState {
                    module: &module,
                    entry_point: Some("fs_cutout"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    targets: &[],
                }),
                multiview_mask: None,
                cache: None,
            })
        };
        let culls = [Cull::None, Cull::Front, Cull::Back];
        Self {
            terrain,
            opaque: culls.iter().map(|&c| (c, mesh(c, false))).collect(),
            cutout: culls.iter().map(|&c| (c, mesh(c, true))).collect(),
        }
    }
}

/// The post passes' pipelines.
struct PostPipelines {
    /// Depth copy from a single-sample (0) or multisampled (1) world.
    depth_copy: [wgpu::RenderPipeline; 2],
    occlusion: wgpu::RenderPipeline,
    rays: wgpu::RenderPipeline,
    volumetric: wgpu::RenderPipeline,
    bloom_down: wgpu::RenderPipeline,
    blur: wgpu::RenderPipeline,
    filmic: wgpu::RenderPipeline,
}

/// The bind group layouts of the atmospheric passes.
pub(crate) struct AtmosphereLayouts {
    /// Shadow block, shadow maps, their sampler, the frame block.
    pub atmosphere: wgpu::BindGroupLayout,
    /// Frame block, pass parameters (dynamic), linear sampler.
    post_globals: wgpu::BindGroupLayout,
    /// Four filterable textures.
    post_textures: wgpu::BindGroupLayout,
    /// The scene depth (not filterable), then three filterable textures.
    post_depth_textures: wgpu::BindGroupLayout,
    /// The world's depth, single-sample (0) or multisampled (1).
    depth_copy: [wgpu::BindGroupLayout; 2],
    /// One cascade's block (dynamic).
    cascade: wgpu::BindGroupLayout,
}

fn texture_binding(
    binding: u32,
    sample_type: wgpu::TextureSampleType,
    dimension: wgpu::TextureViewDimension,
    multisampled: bool,
) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type,
            view_dimension: dimension,
            multisampled,
        },
        count: None,
    }
}

impl AtmosphereLayouts {
    fn new(device: &wgpu::Device) -> Self {
        let both = wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT;
        let float = wgpu::TextureSampleType::Float { filterable: true };
        let unfiltered = wgpu::TextureSampleType::Float { filterable: false };
        let d2 = wgpu::TextureViewDimension::D2;
        let atmosphere = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("atmosphere"),
            entries: &[
                uniform_entry(0, both),
                texture_binding(
                    1,
                    wgpu::TextureSampleType::Depth,
                    wgpu::TextureViewDimension::D2Array,
                    false,
                ),
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::NonFiltering),
                    count: None,
                },
                uniform_entry(3, both),
            ],
        });
        let post_globals = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("atmosphere post"),
            entries: &[
                uniform_entry(0, wgpu::ShaderStages::FRAGMENT),
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: true,
                        min_binding_size: wgpu::BufferSize::new(size_of::<PassParamsGpu>() as u64),
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let textures = |label, first| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some(label),
                entries: &[
                    texture_binding(0, first, d2, false),
                    texture_binding(1, float, d2, false),
                    texture_binding(2, float, d2, false),
                    texture_binding(3, float, d2, false),
                ],
            })
        };
        let depth_copy = [false, true].map(|multisampled| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("depth copy"),
                entries: &[texture_binding(
                    0,
                    wgpu::TextureSampleType::Depth,
                    d2,
                    multisampled,
                )],
            })
        });
        let cascade = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("shadow cascade"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: true,
                    min_binding_size: wgpu::BufferSize::new(size_of::<CascadeGpu>() as u64),
                },
                count: None,
            }],
        });
        Self {
            atmosphere,
            post_globals,
            post_textures: textures("atmosphere textures", float),
            post_depth_textures: textures("atmosphere depth textures", unfiltered),
            depth_copy,
            cascade,
        }
    }
}

impl PostPipelines {
    fn build(
        device: &wgpu::Device,
        layouts: &AtmosphereLayouts,
        output: wgpu::TextureFormat,
    ) -> Self {
        let module = atmosphere_shader(
            device,
            "atmosphere post",
            include_str!("../shaders/atmosphere_post.wgsl"),
            false,
        );
        let plain = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("atmosphere post"),
            bind_group_layouts: &[Some(&layouts.post_globals), Some(&layouts.post_textures)],
            immediate_size: 0,
        });
        let depth = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("atmosphere post depth"),
            bind_group_layouts: &[
                Some(&layouts.post_globals),
                Some(&layouts.post_depth_textures),
            ],
            immediate_size: 0,
        });
        let shafts = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("atmosphere shafts"),
            bind_group_layouts: &[
                Some(&layouts.post_globals),
                Some(&layouts.post_depth_textures),
                Some(&layouts.atmosphere),
            ],
            immediate_size: 0,
        });
        let pass = |label: &str,
                    layout: &wgpu::PipelineLayout,
                    module: &wgpu::ShaderModule,
                    vs: &str,
                    fs: &str,
                    format: wgpu::TextureFormat| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(layout),
                vertex: wgpu::VertexState {
                    module,
                    entry_point: Some(vs),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    buffers: &[],
                },
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                fragment: Some(wgpu::FragmentState {
                    module,
                    entry_point: Some(fs),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: None,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
        };
        let depth_copy = [false, true].map(|multisampled| {
            let source = include_str!("../shaders/depth_copy.wgsl").replace(
                "DEPTH_TYPE",
                if multisampled {
                    "texture_depth_multisampled_2d"
                } else {
                    "texture_depth_2d"
                },
            );
            let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("depth copy"),
                source: wgpu::ShaderSource::Wgsl(source.into()),
            });
            let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("depth copy"),
                bind_group_layouts: &[Some(&layouts.depth_copy[usize::from(multisampled)])],
                immediate_size: 0,
            });
            pass(
                "depth copy",
                &layout,
                &module,
                "vs_fullscreen",
                "fs_copy",
                DEPTH_COPY_FORMAT,
            )
        });
        let fs = "vs_fullscreen";
        Self {
            depth_copy,
            occlusion: pass(
                "atmospheric sun occlusion",
                &depth,
                &module,
                fs,
                "fs_sun_occlusion",
                MASK_FORMAT,
            ),
            rays: pass(
                "atmospheric sun rays",
                &plain,
                &module,
                fs,
                "fs_sun_rays",
                HDR_FORMAT,
            ),
            volumetric: pass(
                "atmospheric volumetric",
                &shafts,
                &module,
                fs,
                "fs_volumetric",
                HDR_FORMAT,
            ),
            bloom_down: pass(
                "atmospheric bloom downsample",
                &plain,
                &module,
                fs,
                "fs_bloom_downsample",
                HDR_FORMAT,
            ),
            blur: pass(
                "atmospheric bloom blur",
                &plain,
                &module,
                fs,
                "fs_bloom_blur",
                HDR_FORMAT,
            ),
            filmic: pass(
                "atmospheric filmic",
                &plain,
                &module,
                fs,
                "fs_filmic",
                output,
            ),
        }
    }
}

fn target(
    device: &wgpu::Device,
    label: &str,
    (width, height): (u32, u32),
    format: wgpu::TextureFormat,
    samples: u32,
    sampled: bool,
) -> wgpu::TextureView {
    let mut usage = wgpu::TextureUsages::RENDER_ATTACHMENT;
    if sampled {
        usage |= wgpu::TextureUsages::TEXTURE_BINDING;
    }
    device
        .create_texture(&wgpu::TextureDescriptor {
            label: Some(label),
            size: wgpu::Extent3d {
                width: width.max(1),
                height: height.max(1),
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: samples,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage,
            view_formats: &[],
        })
        .create_view(&wgpu::TextureViewDescriptor::default())
}

fn scaled((width, height): (u32, u32), scale: f32) -> (u32, u32) {
    let side = |v: u32| ((v as f32 * scale).ceil() as u32).max(1);
    (side(width), side(height))
}

/// The window-sized targets and the post passes' texture groups.
struct Targets {
    size: (u32, u32),
    samples: u32,
    post_scale: f32,
    ray_scale: f32,
    volumetric_scale: f32,
    /// The resolved world (sampled by the post).
    hdr: wgpu::TextureView,
    /// The multisampled world (`None` at one sample).
    hdr_msaa: Option<wgpu::TextureView>,
    /// The world's depth (sampled by the depth copy).
    depth: wgpu::TextureView,
    depth_copy: wgpu::TextureView,
    mask: wgpu::TextureView,
    rays: wgpu::TextureView,
    volumetric: wgpu::TextureView,
    bloom_a: wgpu::TextureView,
    bloom_b: wgpu::TextureView,
    rays_size: (u32, u32),
    volumetric_size: (u32, u32),
    bloom_size: (u32, u32),
    depth_copy_group: wgpu::BindGroup,
    occlusion_group: wgpu::BindGroup,
    rays_group: wgpu::BindGroup,
    volumetric_group: wgpu::BindGroup,
    bloom_down_group: wgpu::BindGroup,
    blur_a_group: wgpu::BindGroup,
    blur_b_group: wgpu::BindGroup,
    filmic_group: wgpu::BindGroup,
}

impl Targets {
    #[allow(clippy::too_many_arguments)]
    fn new(
        device: &wgpu::Device,
        layouts: &AtmosphereLayouts,
        size: (u32, u32),
        samples: u32,
        config: &AtmosphereConfig,
        black: &wgpu::TextureView,
    ) -> Self {
        let hdr = target(device, "world hdr", size, HDR_FORMAT, 1, true);
        let hdr_msaa = (samples > 1)
            .then(|| target(device, "world hdr msaa", size, HDR_FORMAT, samples, false));
        let depth = target(device, "world hdr depth", size, DEPTH_FORMAT, samples, true);
        let depth_copy = target(device, "world depth copy", size, DEPTH_COPY_FORMAT, 1, true);
        let rays_size = scaled(size, config.preset.ray_scale);
        let volumetric_size = config.volumetric.target_size(size.0, size.1);
        let bloom_size = scaled(size, config.preset.post_scale);
        let mask = target(device, "sun mask", rays_size, MASK_FORMAT, 1, true);
        let rays = target(device, "sun rays", rays_size, HDR_FORMAT, 1, true);
        let volumetric = target(device, "volumetric", volumetric_size, HDR_FORMAT, 1, true);
        let bloom_a = target(device, "bloom a", bloom_size, HDR_FORMAT, 1, true);
        let bloom_b = target(device, "bloom b", bloom_size, HDR_FORMAT, 1, true);
        let group = |layout: &wgpu::BindGroupLayout, views: [&wgpu::TextureView; 4]| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("atmosphere pass textures"),
                layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(views[0]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(views[1]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(views[2]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: wgpu::BindingResource::TextureView(views[3]),
                    },
                ],
            })
        };
        let depth_copy_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("depth copy"),
            layout: &layouts.depth_copy[usize::from(samples > 1)],
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::TextureView(&depth),
            }],
        });
        let plain = &layouts.post_textures;
        let with_depth = &layouts.post_depth_textures;
        Self {
            occlusion_group: group(with_depth, [&depth_copy, black, black, black]),
            rays_group: group(plain, [&mask, black, black, black]),
            volumetric_group: group(with_depth, [&depth_copy, black, black, black]),
            bloom_down_group: group(plain, [&hdr, &rays, &volumetric, black]),
            blur_a_group: group(plain, [&bloom_a, black, black, black]),
            blur_b_group: group(plain, [&bloom_b, black, black, black]),
            filmic_group: group(plain, [&hdr, &bloom_a, &rays, &volumetric]),
            depth_copy_group,
            size,
            samples,
            post_scale: config.preset.post_scale,
            ray_scale: config.preset.ray_scale,
            volumetric_scale: config.volumetric.resolution_scale,
            hdr,
            hdr_msaa,
            depth,
            depth_copy,
            mask,
            rays,
            volumetric,
            bloom_a,
            bloom_b,
            rays_size,
            volumetric_size,
            bloom_size,
        }
    }

    fn fits(&self, size: (u32, u32), samples: u32, config: &AtmosphereConfig) -> bool {
        self.size == size
            && self.samples == samples
            && self.post_scale == config.preset.post_scale
            && self.ray_scale == config.preset.ray_scale
            && self.volumetric_scale == config.volumetric.resolution_scale
    }
}

/// The shadow maps: one depth layer per cascade.
struct ShadowMaps {
    resolution: u32,
    cascades: usize,
    layers: Vec<wgpu::TextureView>,
    array: wgpu::TextureView,
}

impl ShadowMaps {
    fn new(device: &wgpu::Device, resolution: u32, cascades: usize) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("directional shadow depth"),
            size: wgpu::Extent3d {
                width: resolution,
                height: resolution,
                depth_or_array_layers: cascades as u32,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: SHADOW_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let layers = (0..cascades as u32)
            .map(|layer| {
                texture.create_view(&wgpu::TextureViewDescriptor {
                    label: Some("shadow cascade"),
                    dimension: Some(wgpu::TextureViewDimension::D2),
                    base_array_layer: layer,
                    array_layer_count: Some(1),
                    ..Default::default()
                })
            })
            .collect();
        let array = texture.create_view(&wgpu::TextureViewDescriptor {
            label: Some("shadow cascades"),
            dimension: Some(wgpu::TextureViewDimension::D2Array),
            ..Default::default()
        });
        Self {
            resolution,
            cascades,
            layers,
            array,
        }
    }
}

/// What the still casters of a frame depend on: the land cells the walk
/// reached, what is loaded and where the drawing frame is.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct CasterFingerprint {
    pub cells: Vec<u32>,
    pub residents: usize,
    pub models: usize,
    pub meshes: usize,
    pub surfaces: usize,
    pub origin: [u32; 3],
}

/// The shadow casters of a frame: instances grouped by mesh, drawn with
/// each sub-mesh's material. The still casters (statics that do not
/// animate, the terrain) come first and are kept while their fingerprint
/// holds; the moving ones (animating statics, buildings by their level of
/// detail, server objects) follow, gathered every frame.
#[derive(Debug, Default)]
pub(crate) struct CasterFrame {
    /// The instances, in group order.
    pub instances: Vec<InstanceGpu>,
    /// Mesh, surface variant, building shell, instance range.
    pub groups: Vec<(crate::draws::MeshKey, u32, bool, std::ops::Range<u32>)>,
    /// The landblocks whose terrain casts (their corners in the drawing
    /// frame).
    pub terrain: Vec<(crate::world_frame::LandblockCoord, TerrainInstanceGpu)>,
    /// What the still part was gathered for.
    pub fingerprint: Option<CasterFingerprint>,
    /// The still part: its instances and groups.
    pub still_instances: u32,
    pub still_groups: usize,
    /// The still part is last frame's (already on the GPU).
    pub still_reused: bool,
}

/// The atmospheric rendering's GPU state. See the module documentation.
pub(crate) struct AtmosphereGpu {
    /// What runs (at automatic quality's current level).
    pub config: AtmosphereConfig,
    auto: Option<AutoQualityController>,
    performance: PerformanceWindow,
    wind: WindState,
    wind_clock: Instant,
    pub layouts: AtmosphereLayouts,
    /// The world pass's pipelines.
    pub world: HdrWorldPipelines,
    world_samples: (u32, bool),
    pub casters: CasterPipelines,
    post: PostPipelines,
    shadow_maps: ShadowMaps,
    shadow_uniform: wgpu::Buffer,
    frame_uniform: wgpu::Buffer,
    pass_params: wgpu::Buffer,
    cascade_uniform: wgpu::Buffer,
    /// Shadow block, maps and frame block, for the world receivers.
    pub group: wgpu::BindGroup,
    pub cascade_group: wgpu::BindGroup,
    post_globals: wgpu::BindGroup,
    nearest: wgpu::Sampler,
    black: wgpu::TextureView,
    targets: Option<Targets>,
    /// This frame's shadow verdict and cascades.
    pub shadow_state: Option<ShadowEnvironmentState>,
    pub cascades: Vec<Cascade>,
    /// This frame's light shafts: (density, strength), when drawn.
    shafts: Option<(f32, f32)>,
    /// This frame's casters, uploaded.
    pub caster_frame: CasterFrame,
    pub caster_instances: wgpu::Buffer,
    caster_instances_capacity: usize,
    pub caster_terrain: wgpu::Buffer,
    caster_terrain_capacity: usize,
    /// The frame's inputs (after prepare).
    pub inputs: Option<FrameInputs>,
    /// Messages for the window.
    pub notices: Vec<String>,
    /// Automatic quality gave up: turn the option off for the session.
    pub failed: bool,
    /// The CPU time the atmospheric work added since the last judgement
    /// (the frame inputs, the shadow fit and casters, the uploads).
    pub cpu_added: std::time::Duration,
}

impl AtmosphereGpu {
    /// Build it for `config`, the world pass at `samples` (with alpha to
    /// coverage when asked) and the window's `output` format.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        config: AtmosphereConfig,
        samples: u32,
        alpha_to_coverage: bool,
        world_layouts: &WorldLayouts<'_>,
        output: wgpu::TextureFormat,
    ) -> Self {
        let layouts = AtmosphereLayouts::new(device);
        let world = HdrWorldPipelines::build(
            device,
            samples,
            alpha_to_coverage,
            world_layouts,
            &layouts.atmosphere,
        );
        let casters = CasterPipelines::build(device, &layouts.cascade, world_layouts.material);
        let post = PostPipelines::build(device, &layouts, output);
        let buffer = |label: &str, size: u64| {
            device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        };
        let shadow_uniform = buffer("shadow uniforms", ShadowUniforms::SIZE as u64);
        queue.write_buffer(
            &shadow_uniform,
            0,
            bytemuck::bytes_of(&ShadowUniforms::disabled()),
        );
        let frame_uniform = buffer("atmospheric frame", size_of::<AtmosphericFrameGpu>() as u64);
        queue.write_buffer(
            &frame_uniform,
            0,
            bytemuck::bytes_of(&AtmosphericFrameGpu::zeroed()),
        );
        let pass_params = buffer("atmospheric passes", SLOT * PASS_SLOTS);
        let cascade_uniform = buffer("shadow cascades", SLOT * 4);
        let sampler = |filter: wgpu::FilterMode, label: &str| {
            device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some(label),
                address_mode_u: wgpu::AddressMode::ClampToEdge,
                address_mode_v: wgpu::AddressMode::ClampToEdge,
                address_mode_w: wgpu::AddressMode::ClampToEdge,
                mag_filter: filter,
                min_filter: filter,
                ..Default::default()
            })
        };
        let nearest = sampler(wgpu::FilterMode::Nearest, "shadow nearest clamp");
        let linear = sampler(wgpu::FilterMode::Linear, "atmosphere linear clamp");
        let black = {
            let texture = device.create_texture(&wgpu::TextureDescriptor {
                label: Some("atmosphere black"),
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
            super::pipelines::write_layer(queue, &texture, 0, 0, 1, 1, &[0, 0, 0, 255]);
            texture.create_view(&wgpu::TextureViewDescriptor::default())
        };
        let shadow_maps = ShadowMaps::new(
            device,
            config.shadow.map_resolution,
            config.shadow.cascade_count,
        );
        let group = atmosphere_group(
            device,
            &layouts,
            &shadow_uniform,
            &shadow_maps,
            &nearest,
            &frame_uniform,
        );
        let cascade_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("shadow cascade"),
            layout: &layouts.cascade,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: &cascade_uniform,
                    offset: 0,
                    size: wgpu::BufferSize::new(size_of::<CascadeGpu>() as u64),
                }),
            }],
        });
        let post_globals = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("atmosphere post"),
            layout: &layouts.post_globals,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: frame_uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &pass_params,
                        offset: 0,
                        size: wgpu::BufferSize::new(size_of::<PassParamsGpu>() as u64),
                    }),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&linear),
                },
            ],
        });
        let auto = config.automatic.map(|range| {
            AutoQualityController::new(range.initial, QualityLevel::Low, range.maximum)
        });
        let caster_instances_capacity = 1024;
        let caster_terrain_capacity = 64;
        Self {
            config,
            auto,
            performance: PerformanceWindow::default(),
            wind: WindState::default(),
            wind_clock: Instant::now(),
            world,
            world_samples: (samples, alpha_to_coverage),
            casters,
            post,
            shadow_maps,
            shadow_uniform,
            frame_uniform,
            pass_params,
            cascade_uniform,
            group,
            cascade_group,
            post_globals,
            nearest,
            black,
            targets: None,
            shadow_state: None,
            cascades: Vec::new(),
            shafts: None,
            caster_frame: CasterFrame::default(),
            caster_instances: super::pipelines::vertex_buffer(
                device,
                "shadow caster instances",
                caster_instances_capacity * size_of::<InstanceGpu>(),
            ),
            caster_instances_capacity,
            caster_terrain: super::pipelines::vertex_buffer(
                device,
                "shadow caster terrain",
                caster_terrain_capacity * size_of::<TerrainInstanceGpu>(),
            ),
            caster_terrain_capacity,
            inputs: None,
            notices: Vec::new(),
            failed: false,
            cpu_added: std::time::Duration::ZERO,
            layouts,
        }
    }

    /// The same selection's user values changed or the window's sample
    /// count did: keep what can be kept.
    pub(crate) fn rebuild_world_pipelines(
        &mut self,
        device: &wgpu::Device,
        samples: u32,
        alpha_to_coverage: bool,
        world_layouts: &WorldLayouts<'_>,
    ) {
        if self.world_samples == (samples, alpha_to_coverage) {
            return;
        }
        self.world = HdrWorldPipelines::build(
            device,
            samples,
            alpha_to_coverage,
            world_layouts,
            &self.layouts.atmosphere,
        );
        self.world_samples = (samples, alpha_to_coverage);
        self.targets = None;
    }

    /// Run `config` from now on (a new selection or level): the shadow maps
    /// and targets follow its sizes.
    pub(crate) fn set_config(&mut self, device: &wgpu::Device, config: AtmosphereConfig) {
        if (config.shadow.map_resolution, config.shadow.cascade_count)
            != (self.shadow_maps.resolution, self.shadow_maps.cascades)
        {
            self.shadow_maps = ShadowMaps::new(
                device,
                config.shadow.map_resolution,
                config.shadow.cascade_count,
            );
            self.group = atmosphere_group(
                device,
                &self.layouts,
                &self.shadow_uniform,
                &self.shadow_maps,
                &self.nearest,
                &self.frame_uniform,
            );
        }
        let automatic_changed = config.automatic != self.config.automatic;
        self.config = config;
        if automatic_changed {
            self.auto = self.config.automatic.map(|range| {
                AutoQualityController::new(range.initial, QualityLevel::Low, range.maximum)
            });
        }
        self.performance.reset();
    }

    /// Make sure the window-sized targets fit.
    pub(crate) fn ensure_targets(&mut self, device: &wgpu::Device, size: (u32, u32), samples: u32) {
        let fits = self
            .targets
            .as_ref()
            .is_some_and(|t| t.fits(size, samples, &self.config));
        if !fits {
            self.targets = Some(Targets::new(
                device,
                &self.layouts,
                size,
                samples,
                &self.config,
                &self.black,
            ));
            self.performance.reset();
        }
    }

    /// The world pass's colour attachment, resolve target and depth.
    pub(crate) fn world_targets(
        &self,
    ) -> Option<(
        &wgpu::TextureView,
        Option<&wgpu::TextureView>,
        &wgpu::TextureView,
    )> {
        let t = self.targets.as_ref()?;
        Some(match &t.hdr_msaa {
            Some(msaa) => (msaa, Some(&t.hdr), &t.depth),
            None => (&t.hdr, None, &t.depth),
        })
    }

    /// Write this frame's blocks: the shadow block (`uniforms`), each
    /// cascade's caster block, the frame block and the passes' parameters.
    pub(crate) fn write_frame(
        &mut self,
        queue: &wgpu::Queue,
        inputs: FrameInputs,
        uniforms: &ShadowUniforms,
    ) {
        let seconds = self.wind_clock.elapsed().as_secs_f32();
        let (clock_wind, amplitude) = self.wind.advance(
            &self.config.wind,
            seconds,
            inputs.weather,
            inputs.is_outdoor,
        );
        queue.write_buffer(&self.shadow_uniform, 0, bytemuck::bytes_of(uniforms));
        let mut cascades = vec![0u8; (SLOT * 4) as usize];
        for (i, c) in self.cascades.iter().enumerate().take(4) {
            let block = CascadeGpu {
                world_to_clip: c.world_to_shadow_clip.to_cols_array(),
                clock_wind: clock_wind.to_array(),
                wind_amplitude: amplitude.to_array(),
            };
            let at = i * SLOT as usize;
            cascades[at..at + size_of::<CascadeGpu>()].copy_from_slice(bytemuck::bytes_of(&block));
        }
        queue.write_buffer(&self.cascade_uniform, 0, &cascades);

        let sun_policy = inputs.sun_policy();
        let elevation_policy = crate::atmosphere::policy::ray(
            &crate::atmosphere::policy::SUN_ELEVATION_RESPONSE,
            inputs.sun_elevation_degrees,
            1.0,
        );
        let day_group = crate::atmosphere::policy::day_group_multiplier(inputs.active_day_group);
        let shadow_elevation = crate::atmosphere::policy::directional_shadow(
            &crate::atmosphere::policy::DIRECTIONAL_SHADOW_ELEVATION_RESPONSE,
            inputs.sun_elevation_degrees,
            elevation_policy,
        );
        let shaft_elevation = crate::atmosphere::policy::volumetric_shaft(
            &crate::atmosphere::policy::VOLUMETRIC_SHAFT_ELEVATION_RESPONSE,
            inputs.sun_elevation_degrees,
            0.0,
        );
        let ray_strength = if inputs.sun_on_screen && inputs.is_outdoor {
            (self.config.post.sun_ray_strength * sun_policy).clamp(0.0, 4.0)
        } else {
            0.0
        };
        let (w, h) = inputs.viewport;
        let frame = AtmosphericFrameGpu {
            sun_screen: [
                inputs.sun_screen_uv.x,
                inputs.sun_screen_uv.y,
                ray_strength,
                inputs.sun_elevation_degrees,
            ],
            sun_color: inputs.sun_color.extend(sun_policy).to_array(),
            viewport: [
                w as f32,
                h as f32,
                1.0 / w.max(1) as f32,
                1.0 / h.max(1) as f32,
            ],
            weather: [
                inputs.weather as u32 as f32,
                inputs.weather_intensity,
                inputs.delta_seconds.clamp(0.0, 1.0),
                if inputs.is_outdoor { 1.0 } else { 0.0 },
            ],
            sun_direction: inputs
                .sun_direction
                .extend(inputs.sun_brightness)
                .to_array(),
            policy: [
                inputs.active_day_group as f32,
                day_group,
                shadow_elevation,
                shaft_elevation,
            ],
            inverse_view_projection: inputs.inverse_view_projection.to_cols_array(),
            clock_wind: clock_wind.to_array(),
            wind_amplitude: amplitude.to_array(),
        };
        queue.write_buffer(&self.frame_uniform, 0, bytemuck::bytes_of(&frame));

        // The light shafts.
        let shaft_frame = volumetric::ShaftFrame {
            shadow_current: self.shadow_state.is_some_and(|s| s.should_render()),
            is_outdoor: inputs.is_outdoor,
            sun_on_screen: inputs.sun_on_screen,
            sun_elevation_degrees: inputs.sun_elevation_degrees,
            sun_brightness: inputs.sun_brightness,
            active_day_group: inputs.active_day_group,
            weather: inputs.weather,
            weather_intensity: inputs.weather_intensity,
        };
        self.shafts = None;
        if volumetric::shaft_gate(self.config.volumetric_strength, &shaft_frame)
            == volumetric::ShaftGateReason::Rendered
        {
            let (density, strength) =
                volumetric::shaft_parameters(self.config.volumetric_strength, &shaft_frame);
            if strength > 1e-4 {
                self.shafts = Some((density, strength));
            }
        }

        let (bw, bh) = self.targets.as_ref().map_or((1, 1), |t| t.bloom_size);
        let post = &self.config.post;
        let shafts_on = if self.shafts.is_some() { 1.0 } else { 0.0 };
        let (density, strength) = self.shafts.unwrap_or((0.0, 0.0));
        let params: [PassParamsGpu; PASS_SLOTS as usize] = [
            // Sun occlusion.
            PassParamsGpu::default(),
            // Sun rays: decay, weight, density, samples.
            PassParamsGpu {
                p: [[0.965, 0.24, 0.82, 48.0], [0.0; 4], [0.0; 4], [0.0; 4]],
            },
            // Shafts: density, strength, steps, on.
            PassParamsGpu {
                p: [
                    [
                        density,
                        strength,
                        self.config.volumetric.ray_march_steps as f32,
                        1.0,
                    ],
                    [0.0; 4],
                    [0.0; 4],
                    [0.0; 4],
                ],
            },
            // Bloom downsample.
            PassParamsGpu {
                p: [
                    [
                        post.bloom_strength,
                        color::BLOOM_THRESHOLD_LINEAR,
                        color::BLOOM_KNEE_LINEAR,
                        shafts_on,
                    ],
                    [0.0; 4],
                    [0.0; 4],
                    [0.0; 4],
                ],
            },
            // Blur across.
            PassParamsGpu {
                p: [
                    [1.0 / bw as f32, 0.0, 0.0, 0.0],
                    [0.0; 4],
                    [0.0; 4],
                    [0.0; 4],
                ],
            },
            // Blur down.
            PassParamsGpu {
                p: [
                    [0.0, 1.0 / bh as f32, 0.0, 0.0],
                    [0.0; 4],
                    [0.0; 4],
                    [0.0; 4],
                ],
            },
            // Filmic.
            PassParamsGpu {
                p: [
                    [
                        post.exposure,
                        post.saturation,
                        post.contrast,
                        post.vignette_strength,
                    ],
                    [post.filmic_strength, shafts_on, 0.0, 0.0],
                    [0.0; 4],
                    [0.0; 4],
                ],
            },
            PassParamsGpu::default(),
        ];
        let mut bytes = vec![0u8; (SLOT * PASS_SLOTS) as usize];
        for (i, p) in params.iter().enumerate() {
            let at = i * SLOT as usize;
            bytes[at..at + size_of::<PassParamsGpu>()].copy_from_slice(bytemuck::bytes_of(p));
        }
        queue.write_buffer(&self.pass_params, 0, &bytes);
        self.inputs = Some(inputs);
    }

    /// Upload this frame's casters.
    pub(crate) fn upload_casters(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        let f = &self.caster_frame;
        let mut first = if f.still_reused {
            f.still_instances as usize
        } else {
            0
        };
        if f.instances.len() > self.caster_instances_capacity {
            self.caster_instances_capacity = f.instances.len().next_power_of_two();
            self.caster_instances = super::pipelines::vertex_buffer(
                device,
                "shadow caster instances",
                self.caster_instances_capacity * size_of::<InstanceGpu>(),
            );
            first = 0;
        }
        if f.instances.len() > first {
            queue.write_buffer(
                &self.caster_instances,
                (first * size_of::<InstanceGpu>()) as u64,
                bytemuck::cast_slice(&f.instances[first..]),
            );
        }
        if f.terrain.len() > self.caster_terrain_capacity {
            self.caster_terrain_capacity = f.terrain.len().next_power_of_two();
            self.caster_terrain = super::pipelines::vertex_buffer(
                device,
                "shadow caster terrain",
                self.caster_terrain_capacity * size_of::<TerrainInstanceGpu>(),
            );
        }
        if !f.terrain.is_empty() {
            let offsets: Vec<TerrainInstanceGpu> = f.terrain.iter().map(|(_, t)| *t).collect();
            queue.write_buffer(&self.caster_terrain, 0, bytemuck::cast_slice(&offsets));
        }
    }

    /// The shadows draw this frame.
    pub(crate) fn shadows_on(&self) -> bool {
        self.shadow_state.is_some_and(|s| s.should_render()) && !self.cascades.is_empty()
    }

    /// The shadow map layer `cascade` renders into.
    pub(crate) fn shadow_layer(&self, cascade: usize) -> Option<&wgpu::TextureView> {
        self.shadow_maps.layers.get(cascade)
    }

    /// The dynamic offset of cascade `i`'s block.
    pub(crate) fn cascade_offset(i: usize) -> u32 {
        (i as u64 * SLOT) as u32
    }

    /// Record the post passes: depth copy, sun mask and rays, shafts,
    /// bloom, and the filmic composite into `output`.
    pub(crate) fn encode_post(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        output: &wgpu::TextureView,
        end_timestamp: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) {
        let Some(t) = self.targets.as_ref() else {
            return;
        };
        let samples = usize::from(t.samples > 1);
        let pass = |encoder: &mut wgpu::CommandEncoder,
                    label: &str,
                    view: &wgpu::TextureView,
                    pipeline: &wgpu::RenderPipeline,
                    slot: u64,
                    textures: &wgpu::BindGroup,
                    shadow: bool,
                    timestamps: Option<wgpu::RenderPassTimestampWrites<'_>>| {
            let mut p = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some(label),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                timestamp_writes: timestamps,
                ..Default::default()
            });
            p.set_pipeline(pipeline);
            p.set_bind_group(0, &self.post_globals, &[(slot * SLOT) as u32]);
            p.set_bind_group(1, textures, &[]);
            if shadow {
                p.set_bind_group(2, &self.group, &[]);
            }
            p.draw(0..3, 0..1);
        };
        {
            let mut p = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("world depth copy"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &t.depth_copy,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::WHITE),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            p.set_pipeline(&self.post.depth_copy[samples]);
            p.set_bind_group(0, &t.depth_copy_group, &[]);
            p.draw(0..3, 0..1);
        }
        pass(
            encoder,
            "atmospheric sun occlusion",
            &t.mask,
            &self.post.occlusion,
            0,
            &t.occlusion_group,
            false,
            None,
        );
        pass(
            encoder,
            "atmospheric sun rays",
            &t.rays,
            &self.post.rays,
            1,
            &t.rays_group,
            false,
            None,
        );
        if self.shafts.is_some() {
            pass(
                encoder,
                "atmospheric volumetric shafts",
                &t.volumetric,
                &self.post.volumetric,
                2,
                &t.volumetric_group,
                true,
                None,
            );
        }
        pass(
            encoder,
            "atmospheric bloom downsample",
            &t.bloom_a,
            &self.post.bloom_down,
            3,
            &t.bloom_down_group,
            false,
            None,
        );
        pass(
            encoder,
            "atmospheric bloom blur horizontal",
            &t.bloom_b,
            &self.post.blur,
            4,
            &t.blur_a_group,
            false,
            None,
        );
        pass(
            encoder,
            "atmospheric bloom blur vertical",
            &t.bloom_a,
            &self.post.blur,
            5,
            &t.blur_b_group,
            false,
            None,
        );
        pass(
            encoder,
            "atmospheric filmic",
            output,
            &self.post.filmic,
            6,
            &t.filmic_group,
            false,
            end_timestamp,
        );
    }

    /// Automatic quality: judge the last frames' cost (`gpu_ms` the
    /// measured GPU time of the atmospheric frame, when known); the level
    /// it moves to, if it moves.
    pub(crate) fn observe(&mut self, gpu_ms: Option<f64>) -> Option<QualityLevel> {
        let resident = self.resident_bytes();
        let auto = self.auto.as_mut()?;
        let cpu_ms = std::mem::take(&mut self.cpu_added).as_secs_f64() * 1000.0;
        let gpu = gpu_ms?;
        self.performance.observe(cpu_ms, Some(gpu), resident);
        let p = self.performance.snapshot();
        let before = auto.snapshot().change_generation;
        let s = auto.observe(&QualityMeasurement {
            gpu_ms_p99: p.gpu_ms[2],
            cpu_ms_p99: p.cpu_ms[2],
            resident_gpu_bytes: p.resident_gpu_bytes,
            stable: true,
        });
        if s.change_generation == before {
            return None;
        }
        if s.fallback_to_plain {
            let budget = auto.current_budget();
            self.notices.push(format!(
                "Automatic quality turned the atmospheric rendering off: {:?} stayed over its budget for {} frames (GPU p99 {:.3} ms, budget {:.3} ms; CPU p99 {:.3} ms, budget {:.3} ms).",
                s.current,
                AutoQualityController::DOWNGRADE_HYSTERESIS_FRAMES,
                p.gpu_ms[2],
                budget.gpu_ms_p99,
                p.cpu_ms[2],
                budget.cpu_ms_p99
            ));
            self.failed = true;
            return None;
        }
        self.performance.reset();
        Some(s.current)
    }

    /// Video memory the atmospheric rendering holds, bytes (OpenAC's
    /// accounting: 12 bytes a pixel for the world, the post targets, the
    /// shadow maps).
    pub(crate) fn resident_bytes(&self) -> u64 {
        let maps = self.config.shadow.approximate_depth_map_bytes;
        let Some(t) = self.targets.as_ref() else {
            return maps;
        };
        let area = |(w, h): (u32, u32)| u64::from(w) * u64::from(h);
        maps + area(t.size) * 12
            + area(t.bloom_size) * 16
            + area(t.rays_size) * 4
            + area(t.rays_size) * 8
            + area(t.volumetric_size) * 8
    }
}

fn atmosphere_group(
    device: &wgpu::Device,
    layouts: &AtmosphereLayouts,
    shadow_uniform: &wgpu::Buffer,
    maps: &ShadowMaps,
    nearest: &wgpu::Sampler,
    frame_uniform: &wgpu::Buffer,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("atmosphere"),
        layout: &layouts.atmosphere,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: shadow_uniform.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(&maps.array),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::Sampler(nearest),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: frame_uniform.as_entire_binding(),
            },
        ],
    })
}

const _: () = assert!(size_of::<AtmosphericFrameGpu>() == 192);
const _: () = assert!(size_of::<PassParamsGpu>() == 64);
const _: () = assert!(size_of::<CascadeGpu>() <= SLOT as usize);

#[cfg(test)]
mod tests;
