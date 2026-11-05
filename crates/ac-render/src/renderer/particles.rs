//! The particle pass's GPU state: the quad and particle-model pipelines,
//! the soft-sprite stand-in texture and the frame's instance buffers.
//!
//! Quads test depth without writing it and cull nothing, blended
//! additively or by straight alpha. Particle models blend by their
//! surface (opaque ones write depth) and cull as their model says.

use std::collections::HashMap;

use super::pipelines::{DEPTH_FORMAT, shader, vertex_buffer, write_layer};
use crate::assets::MeshGpuVertex;
use crate::draws::{BillboardGpu, FrameDraws, ParticleBlend, ParticleMeshGpu};
use crate::meshing::Cull;

/// The particle pipelines and buffers of one window.
pub(super) struct ParticleGpu {
    /// Straight alpha (0) and additive (1).
    pub billboard: [wgpu::RenderPipeline; 2],
    /// By blend and culling.
    pub meshes: HashMap<(ParticleBlend, Cull), wgpu::RenderPipeline>,
    /// A white texel: the soft sprite's (unused) texture.
    pub white: wgpu::BindGroup,
    pub billboards: wgpu::Buffer,
    billboards_capacity: usize,
    pub mesh_instances: wgpu::Buffer,
    mesh_instances_capacity: usize,
}

fn blend_state(blend: ParticleBlend) -> Option<wgpu::BlendState> {
    let over = wgpu::BlendComponent::OVER;
    match blend {
        ParticleBlend::Opaque => None,
        ParticleBlend::Alpha => Some(wgpu::BlendState::ALPHA_BLENDING),
        ParticleBlend::Additive => Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::SrcAlpha,
                dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: over,
        }),
        ParticleBlend::InvAlpha => Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                dst_factor: wgpu::BlendFactor::SrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: over,
        }),
    }
}

fn depth(write: bool) -> wgpu::DepthStencilState {
    wgpu::DepthStencilState {
        format: DEPTH_FORMAT,
        depth_write_enabled: Some(write),
        depth_compare: Some(wgpu::CompareFunction::Less),
        stencil: wgpu::StencilState::default(),
        bias: wgpu::DepthBiasState::default(),
    }
}

pub(super) type ParticlePipelines = (
    [wgpu::RenderPipeline; 2],
    HashMap<(ParticleBlend, Cull), wgpu::RenderPipeline>,
);

impl ParticleGpu {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        samples: u32,
        globals: &wgpu::BindGroupLayout,
        material: &wgpu::BindGroupLayout,
        sampler: &wgpu::Sampler,
    ) -> Self {
        let (billboard, meshes) = Self::pipelines(device, format, samples, globals, material);
        Self::with_pipelines(device, queue, material, sampler, billboard, meshes)
    }

    /// Build the pipelines again for another sample count.
    pub fn rebuild_pipelines(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        samples: u32,
        globals: &wgpu::BindGroupLayout,
        material: &wgpu::BindGroupLayout,
    ) {
        (self.billboard, self.meshes) = Self::pipelines(device, format, samples, globals, material);
    }

    pub(super) fn pipelines(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        samples: u32,
        globals: &wgpu::BindGroupLayout,
        material: &wgpu::BindGroupLayout,
    ) -> ParticlePipelines {
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("particles"),
            bind_group_layouts: &[Some(globals), Some(material)],
            immediate_size: 0,
        });
        let quad = shader(device, "particle", include_str!("../shaders/particle.wgsl"));
        let quad_attributes = wgpu::vertex_attr_array![
            0 => Float32x4,
            1 => Float32x4,
            2 => Float32x4,
            3 => Float32x4,
        ];
        let billboard = [ParticleBlend::Alpha, ParticleBlend::Additive].map(|blend| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(if blend == ParticleBlend::Additive {
                    "particle quad additive"
                } else {
                    "particle quad alpha"
                }),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &quad,
                    entry_point: Some("vs_main"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    buffers: &[Some(wgpu::VertexBufferLayout {
                        array_stride: size_of::<BillboardGpu>() as u64,
                        step_mode: wgpu::VertexStepMode::Instance,
                        attributes: &quad_attributes,
                    })],
                },
                primitive: wgpu::PrimitiveState {
                    cull_mode: None,
                    ..Default::default()
                },
                depth_stencil: Some(depth(false)),
                multisample: super::pipelines::multisample(samples, false),
                fragment: Some(wgpu::FragmentState {
                    module: &quad,
                    entry_point: Some("fs_main"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: blend_state(blend),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
        });

        let model = shader(
            device,
            "particle mesh",
            include_str!("../shaders/particle_mesh.wgsl"),
        );
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
            7 => Float32x4,
        ];
        let mut meshes = HashMap::new();
        for blend in [
            ParticleBlend::Opaque,
            ParticleBlend::Alpha,
            ParticleBlend::Additive,
            ParticleBlend::InvAlpha,
        ] {
            for cull in [Cull::None, Cull::Front, Cull::Back] {
                let label = format!("particle mesh {blend:?} cull {cull:?}");
                let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                    label: Some(&label),
                    layout: Some(&layout),
                    vertex: wgpu::VertexState {
                        module: &model,
                        entry_point: Some("vs_main"),
                        compilation_options: wgpu::PipelineCompilationOptions::default(),
                        buffers: &[
                            Some(wgpu::VertexBufferLayout {
                                array_stride: size_of::<MeshGpuVertex>() as u64,
                                step_mode: wgpu::VertexStepMode::Vertex,
                                attributes: &vertex_attributes,
                            }),
                            Some(wgpu::VertexBufferLayout {
                                array_stride: size_of::<ParticleMeshGpu>() as u64,
                                step_mode: wgpu::VertexStepMode::Instance,
                                attributes: &instance_attributes,
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
                    depth_stencil: Some(depth(blend == ParticleBlend::Opaque)),
                    multisample: super::pipelines::multisample(samples, false),
                    fragment: Some(wgpu::FragmentState {
                        module: &model,
                        entry_point: Some("fs_main"),
                        compilation_options: wgpu::PipelineCompilationOptions::default(),
                        targets: &[Some(wgpu::ColorTargetState {
                            format,
                            blend: blend_state(blend),
                            write_mask: wgpu::ColorWrites::ALL,
                        })],
                    }),
                    multiview_mask: None,
                    cache: None,
                });
                meshes.insert((blend, cull), pipeline);
            }
        }
        (billboard, meshes)
    }

    fn with_pipelines(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        material: &wgpu::BindGroupLayout,
        sampler: &wgpu::Sampler,
        billboard: [wgpu::RenderPipeline; 2],
        meshes: HashMap<(ParticleBlend, Cull), wgpu::RenderPipeline>,
    ) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("particle white"),
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
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        // The material block the layout asks for (the particle shaders do
        // not read it).
        let block = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("particle material"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM,
            mapped_at_creation: false,
        });
        let white = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("particle white"),
            layout: material,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: block.as_entire_binding(),
                },
            ],
        });
        let billboards_capacity = 1024;
        let mesh_instances_capacity = 256;
        Self {
            billboard,
            meshes,
            white,
            billboards: vertex_buffer(
                device,
                "particle quads",
                billboards_capacity * size_of::<BillboardGpu>(),
            ),
            billboards_capacity,
            mesh_instances: vertex_buffer(
                device,
                "particle models",
                mesh_instances_capacity * size_of::<ParticleMeshGpu>(),
            ),
            mesh_instances_capacity,
        }
    }

    /// Upload the frame's particle instances.
    pub fn upload(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, frame: &FrameDraws) {
        if frame.billboards.len() > self.billboards_capacity {
            self.billboards_capacity = frame.billboards.len().next_power_of_two();
            self.billboards = vertex_buffer(
                device,
                "particle quads",
                self.billboards_capacity * size_of::<BillboardGpu>(),
            );
        }
        if !frame.billboards.is_empty() {
            queue.write_buffer(&self.billboards, 0, bytemuck::cast_slice(&frame.billboards));
        }
        if frame.particle_meshes.len() > self.mesh_instances_capacity {
            self.mesh_instances_capacity = frame.particle_meshes.len().next_power_of_two();
            self.mesh_instances = vertex_buffer(
                device,
                "particle models",
                self.mesh_instances_capacity * size_of::<ParticleMeshGpu>(),
            );
        }
        if !frame.particle_meshes.is_empty() {
            queue.write_buffer(
                &self.mesh_instances,
                0,
                bytemuck::cast_slice(&frame.particle_meshes),
            );
        }
    }
}
