//! A viewport's GPU side: the texture it is drawn into (with its depth),
//! and its own camera, light and instance blocks laid out as the world's
//! mesh pipelines read them.
//!
//! The light is one white sun at twice full strength shining from behind
//! the viewer's right shoulder (toward +Y, a little right and down), over a
//! 30 % grey ambient, with no fog and no point lights.

use ac_core::lighting::{FLOATS_PER_LIGHT, SceneLightingUbo, UboLight};
use bytemuck::{Pod, Zeroable};
use glam::{Mat4, Vec3};

use super::camera::ViewportCamera;
use crate::draws::{ClipRegionGpu, InstanceGpu};
use crate::lights::{GPU_POINT_LIGHTS, NO_LIGHTS};
use crate::renderer::pipelines::{DEPTH_FORMAT, storage_buffer, vertex_buffer};

/// The direction the viewport sun shines (unit).
#[must_use]
pub fn sun_direction() -> Vec3 {
    Vec3::new(0.3, 1.9, 0.65).normalize()
}

/// The viewport scenes' light, seen from `eye`.
#[must_use]
pub fn figure_lighting(eye: Vec3) -> SceneLightingUbo {
    let mut ubo = SceneLightingUbo::default();
    let d = sun_direction();
    ubo.lights[0] = UboLight {
        // Kind 0: directional.
        pos_and_kind: [0.0; 4],
        dir_and_range: [d.x, d.y, d.z, 1e9],
        color_and_intensity: [1.0, 1.0, 1.0, 2.0],
        cone_angle_etc: [0.0; 4],
    };
    // xyz ambient, w the one active light.
    ubo.cell_ambient = [0.3, 0.3, 0.3, 1.0];
    // Fog mode 0: off.
    ubo.fog_params = [1e9, 1e9, 0.0, 0.0];
    ubo.fog_color = [0.0; 4];
    ubo.camera_and_time = [eye.x, eye.y, eye.z, 0.0];
    ubo
}

/// The camera block the mesh shader reads (as the world's).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct Globals {
    view_proj: [[f32; 4]; 4],
    camera_pos: [f32; 4],
}

/// One instance of a viewport scene: a part placed in the scene.
#[must_use]
pub fn instance(model: Mat4) -> InstanceGpu {
    // Outside (the sun lights it), no point lights, no clip region.
    crate::draws::instance(model, NO_LIGHTS, false)
}

/// A part to draw: its mesh, its surface variant and its instance.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PartDraw {
    /// The GfxObj.
    pub gfx_obj_id: u32,
    /// Its surface substitutions.
    pub variant: u32,
}

/// The texture a viewport is drawn into.
pub(crate) struct Target {
    size: (u32, u32),
    _color: wgpu::Texture,
    view: wgpu::TextureView,
    depth: wgpu::TextureView,
    /// Changes whenever the texture is made again.
    generation: u64,
}

/// One viewport's GPU objects.
pub(crate) struct ViewportGpu {
    label: &'static str,
    globals: wgpu::Buffer,
    lighting: wgpu::Buffer,
    _points: wgpu::Buffer,
    _clips: wgpu::Buffer,
    group: wgpu::BindGroup,
    instances: wgpu::Buffer,
    capacity: usize,
    target: Option<Target>,
    /// What this frame draws (empty: nothing).
    pub(crate) parts: Vec<PartDraw>,
}

impl ViewportGpu {
    /// The blocks for one viewport, bound with the world's globals layout.
    pub(crate) fn new(
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        label: &'static str,
    ) -> Self {
        let uniform = |what: &str, size: u64| {
            device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(&format!("{label} {what}")),
                size,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        };
        let globals = uniform("globals", size_of::<Globals>() as u64);
        let lighting = uniform("lighting", SceneLightingUbo::SIZE_IN_BYTES as u64);
        // No point lights: zeros (no instance names one).
        let points = uniform(
            "point lights",
            (GPU_POINT_LIGHTS * FLOATS_PER_LIGHT * 4) as u64,
        );
        // One clip region, unused (slot 0 never clips).
        let clips = storage_buffer(
            device,
            &format!("{label} clips"),
            size_of::<ClipRegionGpu>(),
        );
        let group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some(label),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: globals.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: lighting.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: points.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: clips.as_entire_binding(),
                },
            ],
        });
        let capacity = 64;
        Self {
            label,
            instances: vertex_buffer(
                device,
                &format!("{label} instances"),
                capacity * size_of::<InstanceGpu>(),
            ),
            capacity,
            globals,
            lighting,
            _points: points,
            _clips: clips,
            group,
            target: None,
            parts: Vec::new(),
        }
    }

    /// Get ready to draw `parts` (each placed by its instance) through
    /// `camera` into a texture of `size` pixels in `format`; the texture is
    /// made again when the size changes. `next_generation` names a new
    /// texture.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        size: (u32, u32),
        camera: &ViewportCamera,
        parts: Vec<PartDraw>,
        instances: &[InstanceGpu],
        next_generation: &mut u64,
    ) {
        let size = (size.0.max(1), size.1.max(1));
        if self.target.as_ref().is_none_or(|t| t.size != size) {
            *next_generation += 1;
            self.target = Some(make_target(
                device,
                self.label,
                format,
                size,
                *next_generation,
            ));
        }
        let view_proj = camera.projection() * camera.view();
        let globals = Globals {
            view_proj: view_proj.to_cols_array_2d(),
            camera_pos: camera.eye.extend(1.0).to_array(),
        };
        queue.write_buffer(&self.globals, 0, bytemuck::bytes_of(&globals));
        queue.write_buffer(
            &self.lighting,
            0,
            bytemuck::cast_slice(&figure_lighting(camera.eye).to_floats()),
        );
        if instances.len() > self.capacity {
            self.capacity = instances.len().next_power_of_two();
            self.instances = vertex_buffer(
                device,
                &format!("{} instances", self.label),
                self.capacity * size_of::<InstanceGpu>(),
            );
        }
        if !instances.is_empty() {
            queue.write_buffer(&self.instances, 0, bytemuck::cast_slice(instances));
        }
        self.parts = parts;
    }

    /// Nothing to draw this frame.
    pub(crate) fn skip(&mut self) {
        self.parts.clear();
    }

    /// The texture, its generation, when one was made.
    pub(crate) fn texture(&self) -> Option<(&wgpu::TextureView, u64)> {
        self.target.as_ref().map(|t| (&t.view, t.generation))
    }

    /// The texture itself (for reading it back in tests).
    #[cfg(test)]
    pub(crate) fn color(&self) -> Option<&wgpu::Texture> {
        self.target.as_ref().map(|t| &t._color)
    }

    /// Begin the pass that draws this frame's parts: cleared to transparent
    /// black, depth cleared, the viewport's blocks and instances bound.
    /// `None` when there is nothing to draw.
    pub(crate) fn begin<'e>(
        &self,
        encoder: &'e mut wgpu::CommandEncoder,
    ) -> Option<wgpu::RenderPass<'e>> {
        let target = self.target.as_ref()?;
        if self.parts.is_empty() {
            return None;
        }
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some(self.label),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &target.view,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: &target.depth,
                depth_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Discard,
                }),
                stencil_ops: None,
            }),
            ..Default::default()
        });
        pass.set_bind_group(0, &self.group, &[]);
        pass.set_vertex_buffer(1, self.instances.slice(..));
        Some(pass)
    }
}

fn make_target(
    device: &wgpu::Device,
    label: &str,
    format: wgpu::TextureFormat,
    (width, height): (u32, u32),
    generation: u64,
) -> Target {
    let color = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT
            | wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
    let depth = device
        .create_texture(&wgpu::TextureDescriptor {
            label: Some(&format!("{label} depth")),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: DEPTH_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        })
        .create_view(&wgpu::TextureViewDescriptor::default());
    Target {
        size: (width, height),
        view: color.create_view(&wgpu::TextureViewDescriptor::default()),
        _color: color,
        depth,
        generation,
    }
}

#[cfg(test)]
mod tests {
    //! The light of OpenAC `PrivateEntityViewportRenderer.UploadCreatureLight`.
    use super::*;

    #[test]
    fn the_figures_are_lit_by_one_white_sun_over_grey() {
        let eye = Vec3::new(0.12, -2.4, 0.88);
        let ubo = figure_lighting(eye);
        let d = Vec3::new(0.3, 1.9, 0.65).normalize();
        assert_eq!(ubo.lights[0].dir_and_range, [d.x, d.y, d.z, 1e9]);
        assert_eq!(ubo.lights[0].pos_and_kind[3], 0.0, "directional");
        assert_eq!(ubo.lights[0].color_and_intensity, [1.0, 1.0, 1.0, 2.0]);
        assert_eq!(ubo.lights[1], UboLight::default());
        assert_eq!(ubo.cell_ambient, [0.3, 0.3, 0.3, 1.0]);
        assert_eq!(ubo.fog_params[3], 0.0, "no fog");
        assert_eq!(&ubo.camera_and_time[..3], &[0.12, -2.4, 0.88]);
    }

    #[test]
    fn a_figure_facing_the_viewer_is_lit_from_the_front() {
        // The mesh shader adds `max(0, n . -direction)`: a face turned
        // toward the eye (-Y) catches the sun.
        let toward_eye = Vec3::NEG_Y.dot(-sun_direction());
        assert!(toward_eye > 0.9);
        assert!(Vec3::Y.dot(-sun_direction()) <= 0.0);
    }

    #[test]
    fn a_viewport_instance_is_lit_by_the_sun_alone() {
        let i = instance(Mat4::from_translation(Vec3::X));
        assert_eq!(i.indoor, 0);
        assert_eq!(i.lights, [-1; 8]);
        assert_eq!(i.opacity, 1.0);
        assert_eq!(i.model[12], 1.0);
    }
}
