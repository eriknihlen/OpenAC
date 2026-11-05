//! The world renderer's GPU pipelines and small resource helpers.

use crate::assets::{MeshGpuVertex, TerrainGpuVertex};
use crate::draws::{InstanceGpu, PortalVertexGpu, TerrainInstanceGpu};
use crate::meshing::{Cull, Translucency};

/// Format of the depth buffer.
pub const DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth32Float;

/// A shader with the clip-region helpers in front of it.
pub fn shader(device: &wgpu::Device, label: &str, source: &str) -> wgpu::ShaderModule {
    let full = format!("{}\n{}", include_str!("../shaders/clip.wgsl"), source);
    device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(full.into()),
    })
}

pub fn depth_view(
    device: &wgpu::Device,
    width: u32,
    height: u32,
    samples: u32,
) -> wgpu::TextureView {
    device
        .create_texture(&wgpu::TextureDescriptor {
            label: Some("depth"),
            size: wgpu::Extent3d {
                width: width.max(1),
                height: height.max(1),
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: samples.max(1),
            dimension: wgpu::TextureDimension::D2,
            format: DEPTH_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        })
        .create_view(&wgpu::TextureViewDescriptor::default())
}

/// The multisampled colour target the world pass draws into and resolves
/// from (`None` without multisampling).
pub fn msaa_color_view(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    width: u32,
    height: u32,
    samples: u32,
) -> Option<wgpu::TextureView> {
    (samples > 1).then(|| {
        device
            .create_texture(&wgpu::TextureDescriptor {
                label: Some("world msaa colour"),
                size: wgpu::Extent3d {
                    width: width.max(1),
                    height: height.max(1),
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: samples,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
                view_formats: &[],
            })
            .create_view(&wgpu::TextureViewDescriptor::default())
    })
}

/// The multisample state of a world pipeline: `samples` per pixel (1 or
/// less: none), alpha-to-coverage only when asked and multisampled.
pub fn multisample(samples: u32, alpha_to_coverage: bool) -> wgpu::MultisampleState {
    let count = samples.max(1);
    wgpu::MultisampleState {
        count,
        mask: !0,
        alpha_to_coverage_enabled: alpha_to_coverage && count > 1,
    }
}

pub fn vertex_buffer(device: &wgpu::Device, label: &str, bytes: usize) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(label),
        size: bytes.max(16) as u64,
        usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    })
}

pub fn storage_buffer(device: &wgpu::Device, label: &str, bytes: usize) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(label),
        size: bytes.max(16) as u64,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    })
}

pub fn write_layer(
    queue: &wgpu::Queue,
    texture: &wgpu::Texture,
    mip_level: u32,
    layer: u32,
    width: u32,
    height: u32,
    pixels: &[u8],
) {
    write_layer_texels(queue, texture, mip_level, layer, (width, height, 4), pixels);
}

/// [`write_layer`] for `bytes_per_texel` bytes a texel (`size` is width,
/// height and bytes per texel).
pub fn write_layer_texels(
    queue: &wgpu::Queue,
    texture: &wgpu::Texture,
    mip_level: u32,
    layer: u32,
    (width, height, bytes_per_texel): (u32, u32, u32),
    pixels: &[u8],
) {
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture,
            mip_level,
            origin: wgpu::Origin3d {
                x: 0,
                y: 0,
                z: layer,
            },
            aspect: wgpu::TextureAspect::All,
        },
        pixels,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(bytes_per_texel * width),
            rows_per_image: Some(height),
        },
        wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
    );
}

pub fn uniform_entry(binding: u32, visibility: wgpu::ShaderStages) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

pub fn storage_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Storage { read_only: true },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

pub fn texture_entry(binding: u32, dim: wgpu::TextureViewDimension) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: dim,
            multisampled: false,
        },
        count: None,
    }
}

pub fn sampler_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}

fn depth_state(write: bool, compare: wgpu::CompareFunction) -> wgpu::DepthStencilState {
    wgpu::DepthStencilState {
        format: DEPTH_FORMAT,
        depth_write_enabled: Some(write),
        depth_compare: Some(compare),
        stencil: wgpu::StencilState::default(),
        bias: wgpu::DepthBiasState::default(),
    }
}

pub fn terrain_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
    terrain: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    let shader = shader(device, "terrain", include_str!("../shaders/terrain.wgsl"));
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("terrain"),
        bind_group_layouts: &[Some(globals), Some(terrain)],
        immediate_size: 0,
    });
    let vertex_attributes = wgpu::vertex_attr_array![
        0 => Float32x3,
        1 => Float32x3,
        2 => Uint8x4,
        3 => Uint8x4,
        4 => Uint8x4,
        5 => Uint8x4,
    ];
    let offset_attributes = wgpu::vertex_attr_array![6 => Float32x3, 7 => Uint32];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("terrain"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &[
                Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<TerrainGpuVertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &vertex_attributes,
                }),
                Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<TerrainInstanceGpu>() as u64,
                    step_mode: wgpu::VertexStepMode::Instance,
                    attributes: &offset_attributes,
                }),
            ],
        },
        primitive: wgpu::PrimitiveState {
            cull_mode: Some(wgpu::Face::Back),
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(true, wgpu::CompareFunction::Less)),
        multisample: multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some("fs_main"),
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
}

#[allow(clippy::too_many_arguments)]
pub fn mesh_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    alpha_to_coverage: bool,
    shader: &wgpu::ShaderModule,
    layout: &wgpu::PipelineLayout,
    kind: Translucency,
    cull: Cull,
) -> wgpu::RenderPipeline {
    mesh_pipeline_with(
        device,
        format,
        samples,
        alpha_to_coverage,
        shader,
        layout,
        kind,
        cull,
        &[],
    )
}

/// [`mesh_pipeline`] with the shader's override constants set.
#[allow(clippy::too_many_arguments)]
pub fn mesh_pipeline_with(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    alpha_to_coverage: bool,
    shader: &wgpu::ShaderModule,
    layout: &wgpu::PipelineLayout,
    kind: Translucency,
    cull: Cull,
    constants: &[(&str, f64)],
) -> wgpu::RenderPipeline {
    let over = wgpu::BlendComponent::OVER;
    let blend = match kind {
        Translucency::Opaque | Translucency::ClipMap => None,
        Translucency::AlphaBlend => Some(wgpu::BlendState::ALPHA_BLENDING),
        Translucency::Additive => Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::SrcAlpha,
                dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: over,
        }),
        Translucency::InvAlpha => Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                dst_factor: wgpu::BlendFactor::SrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: over,
        }),
    };
    // Blended surfaces test depth but do not write it.
    let depth_write = blend.is_none();
    let cull_mode = match cull {
        Cull::None => None,
        Cull::Front => Some(wgpu::Face::Front),
        Cull::Back => Some(wgpu::Face::Back),
    };
    let label = format!("mesh {kind:?} cull {cull:?}");
    let vertex_attributes = wgpu::vertex_attr_array![
        0 => Float32x3,
        1 => Float32x3,
        2 => Float32x2,
    ];
    let instance_attributes = wgpu::vertex_attr_array![
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
    ];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(&label),
        layout: Some(layout),
        vertex: wgpu::VertexState {
            module: shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions {
                constants,
                ..Default::default()
            },
            buffers: &[
                Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<MeshGpuVertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &vertex_attributes,
                }),
                Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<InstanceGpu>() as u64,
                    step_mode: wgpu::VertexStepMode::Instance,
                    attributes: &instance_attributes,
                }),
            ],
        },
        primitive: wgpu::PrimitiveState {
            cull_mode,
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(depth_write, wgpu::CompareFunction::Less)),
        multisample: multisample(samples, alpha_to_coverage && blend.is_none()),
        fragment: Some(wgpu::FragmentState {
            module: shader,
            entry_point: Some("fs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format,
                blend,
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        multiview_mask: None,
        cache: None,
    })
}

/// The plugins' world lines: solid unlit triangles in their own colour,
/// depth tested (nearer or equal wins) and written like opaque geometry,
/// no blending, both faces drawn.
pub fn world_line_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("world line"),
        source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/world_line.wgsl").into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("world line"),
        bind_group_layouts: &[Some(globals)],
        immediate_size: 0,
    });
    let attributes = wgpu::vertex_attr_array![0 => Float32x3, 1 => Float32x3];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("world line"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: size_of::<crate::world_lines::WorldLineVertex>() as u64,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &attributes,
            })],
        },
        primitive: wgpu::PrimitiveState {
            topology: wgpu::PrimitiveTopology::TriangleList,
            cull_mode: None,
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(true, wgpu::CompareFunction::LessEqual)),
        multisample: multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some("fs_main"),
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
}

/// The plugins' world triangles: alpha blended, both faces, tested against
/// the world's depth without writing it, so they never hide each other.
pub fn world_triangle_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    triangle_pipeline(
        device,
        format,
        samples,
        globals,
        Some(wgpu::BlendState::ALPHA_BLENDING),
        wgpu::ColorWrites::ALL,
    )
}

/// The invisible probes of the icons hidden behind the world: the world
/// triangles' vertices and depth test, writing nothing.
pub fn icon_probe_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    triangle_pipeline(
        device,
        format,
        samples,
        globals,
        None,
        wgpu::ColorWrites::empty(),
    )
}

fn triangle_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
    blend: Option<wgpu::BlendState>,
    write_mask: wgpu::ColorWrites,
) -> wgpu::RenderPipeline {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("world triangle"),
        source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/world_triangle.wgsl").into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("world triangle"),
        bind_group_layouts: &[Some(globals)],
        immediate_size: 0,
    });
    let attributes = wgpu::vertex_attr_array![0 => Float32x3, 1 => Float32x4];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("world triangle"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: size_of::<crate::world_triangles::WorldTriangleVertex>() as u64,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &attributes,
            })],
        },
        primitive: wgpu::PrimitiveState {
            topology: wgpu::PrimitiveTopology::TriangleList,
            cull_mode: None,
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(false, wgpu::CompareFunction::LessEqual)),
        multisample: multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some("fs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format,
                blend,
                write_mask,
            })],
        }),
        multiview_mask: None,
        cache: None,
    })
}

/// Depth-only portal polygons: depth always passes and is written, colour
/// is not, both faces are drawn.
pub fn portal_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    globals: &wgpu::BindGroupLayout,
) -> wgpu::RenderPipeline {
    let shader = shader(device, "portal", include_str!("../shaders/portal.wgsl"));
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("portal"),
        bind_group_layouts: &[Some(globals)],
        immediate_size: 0,
    });
    let attributes = wgpu::vertex_attr_array![
        0 => Float32x3,
        1 => Uint32,
        2 => Float32,
        3 => Uint32,
    ];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("portal"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: size_of::<PortalVertexGpu>() as u64,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &attributes,
            })],
        },
        primitive: wgpu::PrimitiveState {
            cull_mode: None,
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(true, wgpu::CompareFunction::Always)),
        multisample: multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some("fs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format,
                blend: None,
                write_mask: wgpu::ColorWrites::empty(),
            })],
        }),
        multiview_mask: None,
        cache: None,
    })
}

/// A sky pipeline: no culling, depth neither tested nor written, straight
/// alpha or additive blending.
pub fn sky_pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    samples: u32,
    shader: &wgpu::ShaderModule,
    layout: &wgpu::PipelineLayout,
    additive: bool,
) -> wgpu::RenderPipeline {
    let blend = if additive {
        wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::SrcAlpha,
                dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent::OVER,
        }
    } else {
        wgpu::BlendState::ALPHA_BLENDING
    };
    let vertex_attributes = wgpu::vertex_attr_array![
        0 => Float32x3,
        1 => Float32x3,
        2 => Float32x2,
    ];
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(if additive {
            "sky additive"
        } else {
            "sky alpha"
        }),
        layout: Some(layout),
        vertex: wgpu::VertexState {
            module: shader,
            entry_point: Some("vs_main"),
            compilation_options: wgpu::PipelineCompilationOptions::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: size_of::<MeshGpuVertex>() as u64,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &vertex_attributes,
            })],
        },
        primitive: wgpu::PrimitiveState {
            cull_mode: None,
            front_face: wgpu::FrontFace::Ccw,
            ..Default::default()
        },
        depth_stencil: Some(depth_state(false, wgpu::CompareFunction::Always)),
        multisample: multisample(samples, false),
        fragment: Some(wgpu::FragmentState {
            module: shader,
            entry_point: Some("fs_main"),
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
}
