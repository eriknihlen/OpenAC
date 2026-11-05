//! Whether the anchors of the plugins' icons can be seen from the camera, for
//! the icons that ask to be hidden behind the world. Icons are drawn over the
//! world by the interface, so the world pass tells them: each anchor gets a
//! tiny invisible quad, tested against the world's depth, and an occlusion
//! query counts whether any of it passed.
//!
//! The answer is read back when the GPU has finished, a frame or two later, so
//! a probe's first answer and every change come that much late. An anchor with
//! no answer yet counts as out of sight, so an icon never flashes through a
//! wall as it is set. Answers are kept by anchor (an object, or a position),
//! never by an icon's place among the icons, which changes as plugins set
//! theirs.

use std::cell::Cell;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

use glam::Vec3;

use super::WorldRenderer;
use super::pipelines::vertex_buffer;
use super::shadows::PassPipelines;
use crate::world_triangles::WorldTriangleVertex;

/// The most anchors tested in one frame; those past it count as out of sight.
pub(crate) const MAXIMUM_PROBES: usize = 1024;

/// How far above its anchor a probe sits, metres, so the ground or floor the
/// anchor stands on does not count as hiding it.
const RISE_METRES: f32 = 0.25;

/// How far toward the camera a probe sits, metres, so the object it stands on
/// does not count as hiding it.
const PULL_METRES: f32 = 0.3;

/// A probe's half-width at one metre from the camera: a few pixels on screen
/// at any distance, and still more than one in a small window.
const HALF_WIDTH_PER_METRE: f32 = 0.006;

const VERTICES_PER_PROBE: usize = 6;
const SLOTS: usize = 3;
const QUERY_BYTES: u64 = wgpu::QUERY_SIZE as u64;

const IDLE: u8 = 0;
const WAITING: u8 = 1;
const READY: u8 = 2;
const FAILED: u8 = 3;

/// Which anchor a probe answers for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) enum IconProbeKey {
    /// An object, whichever of its icons asked.
    Object(u32),
    /// A fixed position, by the bits of its map coordinates (east-west,
    /// north-south, elevation): every icon there shares the answer, and an
    /// icon put somewhere else, even in the place of one taken away, starts
    /// with none.
    Position([u64; 3]),
}

/// An anchor to test this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct IconProbe {
    /// What the answer is kept under.
    pub(crate) key: IconProbeKey,
    /// The anchor, drawing frame.
    pub(crate) anchor: Vec3,
}

/// Where the probe of `anchor` sits, seen from `eye`.
#[must_use]
pub(crate) fn probe_point(anchor: Vec3, eye: Vec3) -> Vec3 {
    let raised = anchor + Vec3::Z * RISE_METRES;
    let to_eye = eye - raised;
    let distance = to_eye.length();
    if distance < 1e-3 {
        return raised;
    }
    raised + to_eye / distance * PULL_METRES.min(distance * 0.5)
}

/// The two triangles of a probe at `point`, facing `eye`.
#[must_use]
pub(crate) fn probe_vertices(point: Vec3, eye: Vec3) -> [WorldTriangleVertex; VERTICES_PER_PROBE] {
    let to_eye = eye - point;
    let half = to_eye.length().max(1.0) * HALF_WIDTH_PER_METRE;
    let direction = to_eye.try_normalize().unwrap_or(Vec3::Y);
    let side = direction.cross(Vec3::Z).try_normalize().unwrap_or(Vec3::X) * half;
    let up = side.cross(direction).normalize_or_zero() * half;
    let corner = |c: Vec3| WorldTriangleVertex {
        position: c.to_array(),
        color: [0.0; 4],
    };
    let (a, b, c, d) = (
        corner(point - side - up),
        corner(point + side - up),
        corner(point + side + up),
        corner(point - side + up),
    );
    [a, b, c, a, c, d]
}

#[derive(Debug)]
struct Slot {
    buffer: wgpu::Buffer,
    state: Arc<AtomicU8>,
    keys: Vec<IconProbeKey>,
    frame: u64,
}

/// The GPU's side of the probes, made when the first is wanted.
#[derive(Debug)]
struct Resources {
    queries: wgpu::QuerySet,
    resolve: wgpu::Buffer,
    vertices: wgpu::Buffer,
    slots: Vec<Slot>,
}

impl Resources {
    fn new(device: &wgpu::Device) -> Self {
        let bytes = MAXIMUM_PROBES as u64 * QUERY_BYTES;
        Self {
            queries: device.create_query_set(&wgpu::QuerySetDescriptor {
                label: Some("icon probes"),
                ty: wgpu::QueryType::Occlusion,
                count: MAXIMUM_PROBES as u32,
            }),
            resolve: device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("icon probes resolve"),
                size: bytes,
                usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: false,
            }),
            vertices: vertex_buffer(
                device,
                "icon probes",
                MAXIMUM_PROBES * VERTICES_PER_PROBE * size_of::<WorldTriangleVertex>(),
            ),
            slots: (0..SLOTS)
                .map(|_| Slot {
                    buffer: device.create_buffer(&wgpu::BufferDescriptor {
                        label: Some("icon probes readback"),
                        size: bytes,
                        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                        mapped_at_creation: false,
                    }),
                    state: Arc::new(AtomicU8::new(IDLE)),
                    keys: Vec::new(),
                    frame: 0,
                })
                .collect(),
        }
    }
}

/// The probes of this frame and the last answers.
#[derive(Debug, Default)]
pub(super) struct IconProbes {
    gpu: Option<Resources>,
    /// The slot this frame's answers go to, when there are probes and one is
    /// free.
    current: Option<usize>,
    /// This frame's probes are in a command buffer already.
    resolved: Cell<bool>,
    count: u32,
    frame: u64,
    newest: u64,
    visible: HashMap<IconProbeKey, bool>,
    scratch: Vec<WorldTriangleVertex>,
}

impl IconProbes {
    /// The query set for the world pass, when this frame has probes to draw.
    pub(super) fn query_set(&self) -> Option<&wgpu::QuerySet> {
        let gpu = self.gpu.as_ref()?;
        (self.current.is_some() && !self.resolved.get() && self.count > 0).then_some(&gpu.queries)
    }

    /// After the world pass: copy the answers out, once.
    pub(super) fn resolve(&self, encoder: &mut wgpu::CommandEncoder) {
        let (Some(gpu), Some(index)) = (self.gpu.as_ref(), self.current) else {
            return;
        };
        if self.resolved.get() || self.count == 0 {
            return;
        }
        self.resolved.set(true);
        encoder.resolve_query_set(&gpu.queries, 0..self.count, &gpu.resolve, 0);
        encoder.copy_buffer_to_buffer(
            &gpu.resolve,
            0,
            &gpu.slots[index].buffer,
            0,
            u64::from(self.count) * QUERY_BYTES,
        );
    }

    /// Takes in every finished read-back; the newest frame's answers stand.
    fn collect(&mut self) {
        let Some(gpu) = self.gpu.as_mut() else {
            return;
        };
        for slot in &mut gpu.slots {
            match slot.state.load(Ordering::Acquire) {
                READY => {
                    if slot.frame > self.newest
                        && let Ok(view) = slot.buffer.get_mapped_range(..)
                    {
                        self.newest = slot.frame;
                        self.visible = slot
                            .keys
                            .iter()
                            .zip(view.chunks_exact(QUERY_BYTES as usize))
                            .map(|(key, samples)| (*key, samples.iter().any(|b| *b != 0)))
                            .collect();
                    }
                    slot.buffer.unmap();
                    slot.state.store(IDLE, Ordering::Release);
                }
                FAILED => slot.state.store(IDLE, Ordering::Release),
                _ => {}
            }
        }
    }
}

impl WorldRenderer {
    /// Whether the anchor of `key` was in sight when last tested; `None`
    /// before an answer.
    pub(crate) fn icon_probe_visible(&self, key: IconProbeKey) -> Option<bool> {
        self.icon_probes.visible.get(&key).copied()
    }

    /// This frame's anchors to test, in the drawing frame. Nothing without a
    /// camera, or when the frame draws no world for them to be hidden by.
    pub(crate) fn set_icon_probes(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        probes: &[IconProbe],
    ) {
        let waiting = self.icon_probes.gpu.as_ref().is_some_and(|gpu| {
            gpu.slots
                .iter()
                .any(|s| s.state.load(Ordering::Acquire) == WAITING)
        });
        if waiting {
            // Only the answers are waited for here, and never blocked on. A
            // lost device is reported where the frame is measured and drawn.
            let _ = device.poll(wgpu::PollType::Poll);
        }
        self.icon_probes.collect();
        self.icon_probes.count = 0;
        self.icon_probes.current = None;
        self.icon_probes.resolved.set(false);
        self.icon_probes.frame += 1;
        let Some((eye, _)) = self.camera else {
            return;
        };
        if probes.is_empty()
            || !self.origin.is_known()
            || self.portal.hides_world()
            || self.reveal.is_withholding()
        {
            return;
        }
        let state = &mut self.icon_probes;
        let frame = state.frame;
        let gpu = state.gpu.get_or_insert_with(|| Resources::new(device));
        let Some(index) = gpu
            .slots
            .iter()
            .position(|s| s.state.load(Ordering::Acquire) == IDLE)
        else {
            return;
        };
        let slot = &mut gpu.slots[index];
        slot.frame = frame;
        slot.keys.clear();
        state.scratch.clear();
        let mut seen = HashSet::new();
        for probe in probes {
            if slot.keys.len() == MAXIMUM_PROBES {
                break;
            }
            if seen.insert(probe.key) {
                slot.keys.push(probe.key);
                let point = probe_point(probe.anchor, eye);
                state.scratch.extend(probe_vertices(point, eye));
            }
        }
        queue.write_buffer(&gpu.vertices, 0, bytemuck::cast_slice(&state.scratch));
        state.count = slot.keys.len() as u32;
        state.current = Some(index);
    }

    /// The probes, in a world pass given their query set.
    pub(super) fn draw_icon_probes(&self, pass: &mut wgpu::RenderPass<'_>, p: &PassPipelines<'_>) {
        let state = &self.icon_probes;
        let Some(gpu) = state.gpu.as_ref() else {
            return;
        };
        if state.query_set().is_none() {
            return;
        }
        pass.set_pipeline(p.icon_probe);
        pass.set_bind_group(0, &self.globals_group, &[]);
        pass.set_vertex_buffer(0, gpu.vertices.slice(..));
        for query in 0..state.count {
            let first = query * VERTICES_PER_PROBE as u32;
            pass.begin_occlusion_query(query);
            pass.draw(first..first + VERTICES_PER_PROBE as u32, 0..1);
            pass.end_occlusion_query();
        }
    }

    /// After the frame was submitted: ask for the answers.
    pub(crate) fn icon_probes_submitted(&mut self) {
        let state = &mut self.icon_probes;
        let (Some(gpu), Some(index)) = (state.gpu.as_ref(), state.current.take()) else {
            return;
        };
        if !state.resolved.get() {
            return;
        }
        let slot = &gpu.slots[index];
        slot.state.store(WAITING, Ordering::Release);
        let done = Arc::clone(&slot.state);
        slot.buffer
            .map_async(wgpu::MapMode::Read, .., move |result| {
                done.store(
                    if result.is_ok() { READY } else { FAILED },
                    Ordering::Release,
                );
            });
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;
    use crate::camera::FlyInput;
    use crate::content::ContentSlot;
    use crate::renderer::WorldSettings;
    use crate::world_lines::PlacedLine;
    use ac_host::settings::DisplaySettings;

    #[test]
    fn a_probe_sits_above_its_anchor_and_toward_the_camera() {
        let eye = Vec3::new(10.0, 0.0, 0.25);
        let point = probe_point(Vec3::ZERO, eye);
        assert!((point - Vec3::new(PULL_METRES, 0.0, RISE_METRES)).length() < 1e-5);
        // Never past the camera, however near it stands.
        let near = probe_point(Vec3::ZERO, Vec3::new(0.2, 0.0, 0.25));
        assert!((near - Vec3::new(0.1, 0.0, RISE_METRES)).length() < 1e-5);
    }

    #[test]
    fn a_probe_grows_with_distance_and_faces_the_camera() {
        let width = |eye: Vec3| {
            let v = probe_vertices(Vec3::ZERO, eye);
            let a = Vec3::from_array(v[0].position);
            let b = Vec3::from_array(v[1].position);
            ((b - a).dot(eye).abs() < 1e-3, (b - a).length())
        };
        let (facing, near) = width(Vec3::new(10.0, 0.0, 0.0));
        assert!(facing);
        let (facing, far) = width(Vec3::new(100.0, 0.0, 0.0));
        assert!(facing);
        assert!((far / near - 10.0).abs() < 1e-3);
    }

    const SIZE: (u32, u32) = (640, 360);
    const FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;

    /// What a probe `distance` metres ahead answers, after a frame with a
    /// solid bar `wall` metres ahead, if any.
    fn answer(
        world: &mut WorldRenderer,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        distance: f32,
        wall: Option<f32>,
    ) -> Option<bool> {
        world.prepare(device, queue, None, FlyInput::default());
        world.origin.try_initialize(127, 127);
        let (eye, look) = world.camera.expect("a camera after prepare");
        let side = look.cross(Vec3::Z).try_normalize().unwrap_or(Vec3::X);
        let at = |d: f32| eye + look * d;
        let lines: Vec<PlacedLine> = wall
            .map(|d| PlacedLine {
                start: at(d) - side * 50.0,
                end: at(d) + side * 50.0,
                color_rgb: 0x00FF00,
                follow_terrain: false,
                width_metres: 2.0,
            })
            .into_iter()
            .collect();
        world.set_plugin_lines(device, queue, &lines);
        // The probe is raised off its anchor, so the anchor is put under the
        // line of sight to have the probe on it.
        let key = IconProbeKey::Position([0; 3]);
        let anchor = at(distance) - Vec3::Z * RISE_METRES;
        world.set_icon_probes(device, queue, &[IconProbe { key, anchor }]);
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("icon probe test target"),
            size: wgpu::Extent3d {
                width: SIZE.0,
                height: SIZE.1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        world.draw(&mut encoder, &view, wgpu::Color::BLACK, None);
        queue.submit([encoder.finish()]);
        world.icon_probes_submitted();
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        world.icon_probes.collect();
        world.icon_probe_visible(key)
    }

    #[test]
    #[ignore = "needs a GPU"]
    fn a_probe_answers_whether_the_world_hides_it() {
        let Some((device, queue)) = crate::test_gpu::device() else {
            eprintln!("no GPU adapter on this machine: the icon probes are not checked");
            return;
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let settings = WorldSettings {
            // No dats: the world draws nothing but the plugin's solid line.
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
        let mut world = WorldRenderer::new(
            &device,
            &queue,
            FORMAT,
            SIZE.0,
            SIZE.1,
            &settings,
            &DisplaySettings::default(),
            &[4],
        );
        assert_eq!(
            world.icon_probe_visible(IconProbeKey::Position([0; 3])),
            None
        );
        assert_eq!(
            answer(&mut world, &device, &queue, 20.0, None),
            Some(true),
            "nothing in the way"
        );
        assert_eq!(
            answer(&mut world, &device, &queue, 20.0, Some(10.0)),
            Some(false),
            "a wall in front"
        );
        assert_eq!(
            answer(&mut world, &device, &queue, 5.0, Some(10.0)),
            Some(true),
            "a wall behind"
        );
        assert!(crate::surface::block_on(scope.pop()).is_none());
    }
}
