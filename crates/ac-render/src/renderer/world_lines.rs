//! The plugins' world lines on the GPU: this frame's bars, uploaded after
//! the camera has moved and drawn in the world pass after the rest of the
//! world, with its depth.

use glam::Vec3;

use super::WorldRenderer;
use super::pipelines::vertex_buffer;
use super::shadows::PassPipelines;
use crate::world_frame::{LandblockCoord, landblock_offset};
use crate::world_lines::{PlacedLine, WorldLineVertex, build};

/// A landblock's side, metres.
const LANDBLOCK_METRES: f32 = 192.0;

/// This frame's bars.
#[derive(Default)]
pub(super) struct WorldLinesGpu {
    vertices: Option<wgpu::Buffer>,
    capacity: usize,
    count: u32,
    scratch: Vec<WorldLineVertex>,
}

impl WorldLinesGpu {
    /// Nothing to draw (until this frame's lines are set).
    pub(super) fn clear(&mut self) {
        self.count = 0;
    }
}

impl WorldRenderer {
    /// This frame's plugin lines, placed in the drawing frame: built into
    /// bars around the camera and uploaded. Nothing without a camera.
    pub(crate) fn set_plugin_lines(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        lines: &[PlacedLine],
    ) {
        self.world_lines.count = 0;
        let Some((eye, _)) = self.camera else {
            return;
        };
        if lines.is_empty() || !self.origin.is_known() {
            return;
        }
        let mut scratch = std::mem::take(&mut self.world_lines.scratch);
        build(lines, eye, |x, y| self.sample_terrain_z(x, y), &mut scratch);
        let gpu = &mut self.world_lines;
        if !scratch.is_empty() {
            if gpu.vertices.is_none() || scratch.len() > gpu.capacity {
                gpu.capacity = scratch.len().next_power_of_two();
                gpu.vertices = Some(vertex_buffer(
                    device,
                    "plugin world lines",
                    gpu.capacity * size_of::<WorldLineVertex>(),
                ));
            }
            if let Some(buffer) = &gpu.vertices {
                queue.write_buffer(buffer, 0, bytemuck::cast_slice(&scratch));
                gpu.count = scratch.len() as u32;
            }
        }
        gpu.scratch = scratch;
    }

    /// The ground's height under a drawing-frame point, where its
    /// landblock's terrain is resident.
    pub(crate) fn sample_terrain_z(&self, x: f32, y: f32) -> Option<f32> {
        let (cx, cy) = self.origin.center();
        let bx = cx + (x / LANDBLOCK_METRES).floor() as i32;
        let by = cy + (y / LANDBLOCK_METRES).floor() as i32;
        let coord = LandblockCoord {
            x: u8::try_from(bx).ok()?,
            y: u8::try_from(by).ok()?,
        };
        let tile = self.residents.get(&coord)?.tile.as_ref()?;
        let corner = landblock_offset(&self.origin, coord);
        let local = Vec3::new(x, y, 0.0) - corner;
        let inside = |v: f32| (0.0..LANDBLOCK_METRES).contains(&v);
        (inside(local.x) && inside(local.y)).then(|| tile.ground.sample_z(local.x, local.y))
    }

    /// The lines, in a world pass that drew the world (not the portal
    /// tunnel).
    pub(super) fn draw_world_lines(&self, pass: &mut wgpu::RenderPass<'_>, p: &PassPipelines<'_>) {
        if self.world_lines.count == 0 || self.portal.hides_world() {
            return;
        }
        let Some(buffer) = &self.world_lines.vertices else {
            return;
        };
        pass.set_pipeline(p.world_line);
        pass.set_bind_group(0, &self.globals_group, &[]);
        pass.set_vertex_buffer(0, buffer.slice(..));
        pass.draw(0..self.world_lines.count, 0..1);
    }
}

#[cfg(test)]
mod tests {
    //! The lines on a real GPU the test makes itself (ignored by default,
    //! as the other GPU tests; skipped when the machine has none).

    use std::path::PathBuf;
    use std::sync::Arc;

    use super::*;
    use crate::camera::FlyInput;
    use crate::content::ContentSlot;
    use crate::renderer::WorldSettings;
    use ac_host::settings::DisplaySettings;

    const SIZE: (u32, u32) = (64, 32);
    const FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;

    fn gpu() -> Option<(wgpu::Device, wgpu::Queue)> {
        crate::test_gpu::device()
    }

    fn renderer(device: &wgpu::Device, queue: &wgpu::Queue) -> WorldRenderer {
        let settings = WorldSettings {
            // No dats: the world draws nothing but the lines.
            content: Arc::new(ContentSlot::new(PathBuf::from("no-dats-for-this-test"))),
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
            FORMAT,
            SIZE.0,
            SIZE.1,
            &settings,
            &DisplaySettings::default(),
            &[4],
        )
    }

    /// A line `distance` metres ahead of the camera, across the view.
    fn across(world: &WorldRenderer, distance: f32, color_rgb: u32) -> PlacedLine {
        let (eye, look) = world.camera.expect("a camera after prepare");
        let side = look.cross(Vec3::Z).try_normalize().unwrap_or(Vec3::X);
        let centre = eye + look * distance;
        PlacedLine {
            start: centre - side * 50.0,
            end: centre + side * 50.0,
            color_rgb,
            follow_terrain: false,
            width_metres: 2.0,
        }
    }

    /// The centre pixel (RGBA) of one frame with the lines `lines` places.
    fn centre_pixel(
        world: &mut WorldRenderer,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        lines: impl Fn(&WorldRenderer) -> Vec<PlacedLine>,
    ) -> [u8; 4] {
        world.prepare(device, queue, None, FlyInput::default());
        world.origin.try_initialize(127, 127);
        let lines = lines(world);
        world.set_plugin_lines(device, queue, &lines);
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("world line test target"),
            size: wgpu::Extent3d {
                width: SIZE.0,
                height: SIZE.1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        world.draw(&mut encoder, &view, wgpu::Color::BLACK, None);
        let padded = (SIZE.0 * 4).div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT)
            * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("world line test readback"),
            size: u64::from(padded) * u64::from(SIZE.1),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        encoder.copy_texture_to_buffer(
            texture.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(padded),
                    rows_per_image: Some(SIZE.1),
                },
            },
            texture.size(),
        );
        queue.submit([encoder.finish()]);
        buffer.map_async(wgpu::MapMode::Read, .., |_| {});
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        let mapped = buffer.slice(..).get_mapped_range().expect("mapped");
        let at = ((SIZE.1 / 2) * padded + (SIZE.0 / 2) * 4) as usize;
        [mapped[at], mapped[at + 1], mapped[at + 2], mapped[at + 3]]
    }

    #[test]
    #[ignore = "needs a GPU"]
    fn a_line_is_drawn_solid_in_its_colour_and_hides_what_is_behind_it() {
        let Some((device, queue)) = gpu() else {
            eprintln!("no GPU adapter on this machine: the world lines are not checked");
            return;
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut world = renderer(&device, &queue);
        let one = centre_pixel(&mut world, &device, &queue, |w| {
            vec![across(w, 10.0, 0xFF0000)]
        });
        assert_eq!(one, [255, 0, 0, 255]);
        // The nearer line wins whichever is sent first: lines test and
        // write the world's depth.
        let near_first = centre_pixel(&mut world, &device, &queue, |w| {
            vec![across(w, 10.0, 0x00FF00), across(w, 20.0, 0xFF0000)]
        });
        assert_eq!(near_first, [0, 255, 0, 255]);
        let near_last = centre_pixel(&mut world, &device, &queue, |w| {
            vec![across(w, 20.0, 0xFF0000), across(w, 10.0, 0x00FF00)]
        });
        assert_eq!(near_last, [0, 255, 0, 255]);
        assert!(crate::surface::block_on(scope.pop()).is_none());
    }
}
