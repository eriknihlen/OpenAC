//! The plugins' world triangles and ground shapes on the GPU: this frame's
//! triangles, uploaded after the camera has moved and drawn in the world
//! pass after the world lines, tested against the world's depth without
//! writing it.

use super::WorldRenderer;
use super::pipelines::vertex_buffer;
use super::shadows::PassPipelines;
use crate::frustum::FrustumPlanes;
use crate::world_shapes::{self, PlacedShape};
use crate::world_triangles::{PlacedTriangle, WorldTriangleVertex, build};

/// This frame's triangles.
#[derive(Default)]
pub(super) struct WorldTrianglesGpu {
    vertices: Option<wgpu::Buffer>,
    capacity: usize,
    count: u32,
    scratch: Vec<WorldTriangleVertex>,
}

impl WorldTrianglesGpu {
    /// Nothing to draw (until this frame's triangles are set).
    pub(super) fn clear(&mut self) {
        self.count = 0;
    }
}

impl WorldRenderer {
    /// This frame's plugin triangles and ground shapes, placed in the
    /// drawing frame: those around the camera and in its view, uploaded
    /// together. Nothing without a camera.
    pub(crate) fn set_plugin_triangles(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        triangles: &[PlacedTriangle],
        shapes: &[PlacedShape],
    ) {
        self.world_triangles.count = 0;
        let Some((eye, _)) = self.camera else {
            return;
        };
        if (triangles.is_empty() && shapes.is_empty()) || !self.origin.is_known() {
            return;
        }
        let frustum = self
            .view_matrices
            .map(|(view, projection)| FrustumPlanes::from_view_projection(projection * view));
        let mut scratch = std::mem::take(&mut self.world_triangles.scratch);
        build(triangles, eye, frustum.as_ref(), &mut scratch);
        world_shapes::build(
            shapes,
            eye,
            frustum.as_ref(),
            |x, y| self.sample_terrain_z(x, y),
            &mut scratch,
        );
        let gpu = &mut self.world_triangles;
        if !scratch.is_empty() {
            if gpu.vertices.is_none() || scratch.len() > gpu.capacity {
                gpu.capacity = scratch.len().next_power_of_two();
                gpu.vertices = Some(vertex_buffer(
                    device,
                    "plugin world triangles",
                    gpu.capacity * size_of::<WorldTriangleVertex>(),
                ));
            }
            if let Some(buffer) = &gpu.vertices {
                queue.write_buffer(buffer, 0, bytemuck::cast_slice(&scratch));
                gpu.count = scratch.len() as u32;
            }
        }
        gpu.scratch = scratch;
    }

    /// The triangles, in a world pass that drew the world (not the portal
    /// tunnel).
    pub(super) fn draw_world_triangles(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &PassPipelines<'_>,
    ) {
        if self.world_triangles.count == 0 || self.portal.hides_world() {
            return;
        }
        let Some(buffer) = &self.world_triangles.vertices else {
            return;
        };
        pass.set_pipeline(p.world_triangle);
        pass.set_bind_group(0, &self.globals_group, &[]);
        pass.set_vertex_buffer(0, buffer.slice(..));
        pass.draw(0..self.world_triangles.count, 0..1);
    }
}

#[cfg(test)]
mod tests {
    //! The triangles on a real GPU the test makes itself (ignored by
    //! default, as the other GPU tests; skipped when the machine has none).

    use std::path::PathBuf;
    use std::sync::Arc;

    use glam::Vec3;

    use super::*;
    use crate::camera::FlyInput;
    use crate::content::ContentSlot;
    use crate::renderer::WorldSettings;
    use crate::world_lines::PlacedLine;
    use crate::world_shapes::ShapeAnchor;
    use ac_host::settings::DisplaySettings;
    use ac_plugin_ui::{GroundShape, GroundShapeKind, MarkerAnchor};

    const SIZE: (u32, u32) = (64, 32);
    const FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;

    fn renderer(device: &wgpu::Device, queue: &wgpu::Queue) -> WorldRenderer {
        let settings = WorldSettings {
            // No dats: the world draws nothing but the plugins' shapes.
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

    /// A triangle `distance` metres ahead of the camera, facing it and
    /// covering the middle of the view.
    fn facing(world: &WorldRenderer, distance: f32, color: [f32; 4]) -> PlacedTriangle {
        let (eye, look) = world.camera.expect("a camera after prepare");
        let side = look.cross(Vec3::Z).try_normalize().unwrap_or(Vec3::X);
        let up = side.cross(look).normalize();
        let centre = eye + look * distance;
        PlacedTriangle {
            corners: [
                centre - side * 50.0 - up * 50.0,
                centre + side * 50.0 - up * 50.0,
                centre + up * 50.0,
            ],
            colors: [color; 3],
        }
    }

    /// A solid line `distance` metres ahead, across the middle of the view.
    fn line_across(world: &WorldRenderer, distance: f32, color_rgb: u32) -> PlacedLine {
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

    /// The centre pixel (RGBA) of one frame, over black, with the lines and
    /// triangles `scene` places.
    fn centre_pixel(
        world: &mut WorldRenderer,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: impl Fn(&WorldRenderer) -> (Vec<PlacedLine>, Vec<PlacedTriangle>),
        shapes: impl Fn(&WorldRenderer) -> Vec<PlacedShape>,
    ) -> [u8; 4] {
        world.prepare(device, queue, None, FlyInput::default());
        world.origin.try_initialize(127, 127);
        let (lines, triangles) = scene(world);
        world.set_plugin_lines(device, queue, &lines);
        world.set_plugin_triangles(device, queue, &triangles, &shapes(world));
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("world triangle test target"),
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
            label: Some("world triangle test readback"),
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

    fn near(actual: [u8; 4], expected: [u8; 3]) -> bool {
        actual[..3]
            .iter()
            .zip(expected)
            .all(|(a, e)| a.abs_diff(e) <= 2)
    }

    #[test]
    #[ignore = "needs a GPU"]
    fn triangles_are_see_through_from_both_sides_under_the_worlds_depth() {
        let Some((device, queue)) = crate::test_gpu::device() else {
            eprintln!("no GPU adapter on this machine: the world triangles are not checked");
            return;
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut world = renderer(&device, &queue);
        let half_red = [1.0, 0.0, 0.0, 0.5];
        let half_blue = [0.0, 0.0, 1.0, 0.5];

        // Half see-through red over black is half red.
        let one = centre_pixel(
            &mut world,
            &device,
            &queue,
            |w| (vec![], vec![facing(w, 10.0, half_red)]),
            |_| vec![],
        );
        assert!(near(one, [128, 0, 0]), "{one:?}");

        // Seen from its back (corners in the other order) it is drawn too.
        let back = centre_pixel(
            &mut world,
            &device,
            &queue,
            |w| {
                let mut t = facing(w, 10.0, half_red);
                t.corners.swap(0, 1);
                (vec![], vec![t])
            },
            |_| vec![],
        );
        assert!(near(back, [128, 0, 0]), "{back:?}");

        // Two overlapping triangles blend: the nearer, drawn first, does
        // not hide the farther, as it writes no depth.
        let both = centre_pixel(
            &mut world,
            &device,
            &queue,
            |w| {
                (
                    vec![],
                    vec![facing(w, 10.0, half_red), facing(w, 20.0, half_blue)],
                )
            },
            |_| vec![],
        );
        assert!(both[0] > 40 && both[2] > 100, "{both:?}");

        // A solid line in front hides the triangle behind it.
        let hidden = centre_pixel(
            &mut world,
            &device,
            &queue,
            |w| {
                (
                    vec![line_across(w, 10.0, 0x00FF00)],
                    vec![facing(w, 20.0, half_red)],
                )
            },
            |_| vec![],
        );
        assert!(near(hidden, [0, 255, 0]), "{hidden:?}");

        // A ground shape is drawn with them, see-through in the same way:
        // a disc around the point the camera looks at.
        let disc = centre_pixel(
            &mut world,
            &device,
            &queue,
            |_| (vec![], vec![]),
            |w| {
                let (eye, look) = w.camera.expect("a camera after prepare");
                let at = ShapeAnchor {
                    centre: eye + look * 10.0,
                    heading_degrees: 0.0,
                    is_outdoor: false,
                };
                let shape = GroundShape {
                    anchor: MarkerAnchor::Object(1),
                    kind: GroundShapeKind::Disc,
                    radius: 100.0,
                    width: 0.0,
                    start_degrees: 0.0,
                    sweep_degrees: 360.0,
                    filled: true,
                    faces_object: false,
                    color_rgba: 0xFF00_0080,
                };
                vec![world_shapes::place(&shape, at, |_, _| None).unwrap()]
            },
        );
        assert!(near(disc, [128, 0, 0]), "{disc:?}");
        assert!(crate::surface::block_on(scope.pop()).is_none());
    }
}
