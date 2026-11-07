//! The world renderer's side of the UI's 3-D viewports
//! (`crate::viewport`): what each shows, built from the models,
//! appearances and poses the world already has, and drawn with the
//! world's meshes, textures and pipelines into each viewport's texture.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Instant;

use ac_dat::objects::{Animation, EnumIdMap};
use ac_ui::pregame::chargen::appearance::PreviewRequest;
use glam::{Mat4, Vec3};

use super::WorldRenderer;
use crate::animation::source::AnimationSource;
use crate::appearance::AppearanceKey;
use crate::loader::LoadRequest;
use crate::meshing::MeshRef;
use crate::scene::Scene;
use crate::viewport::camera::{EXAMINE_FAR, FIGURE_FAR, ViewportCamera, doll_eye, examine_eye};
use crate::viewport::chargen::{ChargenPreviewState, RotateDirection};
use crate::viewport::examine::{MAX_HELD, creature_box, figure_turn, on_copy, turned_box};
use crate::viewport::gpu::{PartDraw, ViewportGpu, instance};
use crate::viewport::pose::{
    ChargenFigure, held_frame, hold, idle_enum, is_animation, pose_enum, resolve_pose_did,
};
use crate::viewport::{
    ChargenLook, ViewportOutcome, ViewportRequest, ViewportSlot, ViewportSubject, undrawn,
};

/// The viewports' state, kept with the world renderer (dropped with the
/// window).
pub(crate) struct Viewports {
    format: wgpu::TextureFormat,
    gpu: Option<Vec<ViewportGpu>>,
    /// The appearance page's preview, the summary page's and the barber's.
    chargen: [ChargenPreviewState; 3],
    /// Pose animations by pose enum (`None`: the dats have none).
    poses: HashMap<u32, Option<Arc<Animation>>>,
    /// Models the previews draw that no world object holds (the scenery
    /// behind them), kept from the sweep.
    models: HashSet<u32>,
    /// The looks each creation preview shows and last asked for, kept
    /// from the sweep.
    shown_looks: [Option<AppearanceKey>; 3],
    wanted_looks: [Option<AppearanceKey>; 3],
    epoch: Instant,
    generation: u64,
}

impl Viewports {
    pub(crate) fn new(format: wgpu::TextureFormat) -> Self {
        Self {
            format,
            gpu: None,
            chargen: [
                ChargenPreviewState::new(false),
                ChargenPreviewState::new(true),
                ChargenPreviewState::barber(),
            ],
            poses: HashMap::new(),
            models: HashSet::new(),
            shown_looks: [None, None, None],
            wanted_looks: [None, None, None],
            epoch: Instant::now(),
            generation: 0,
        }
    }

    /// The models the viewports draw that must not be released.
    pub(crate) fn models(&self) -> impl Iterator<Item = u32> + '_ {
        self.models.iter().copied()
    }

    /// Character creation is over: its previews let their looks go and
    /// start afresh next time (their figures name the looks' variants).
    pub(crate) fn end_creation(&mut self) {
        if self.shown_looks[..BARBER]
            .iter()
            .chain(&self.wanted_looks[..BARBER])
            .any(Option::is_some)
        {
            for index in 0..BARBER {
                self.shown_looks[index] = None;
                self.wanted_looks[index] = None;
            }
            self.chargen[0] = ChargenPreviewState::new(false);
            self.chargen[1] = ChargenPreviewState::new(true);
        }
    }

    /// The barber window is not showing: its preview lets its look go and
    /// starts afresh next time.
    fn end_barber(&mut self) {
        if self.shown_looks[BARBER].is_some() || self.wanted_looks[BARBER].is_some() {
            self.shown_looks[BARBER] = None;
            self.wanted_looks[BARBER] = None;
            self.chargen[BARBER] = ChargenPreviewState::barber();
        }
    }

    /// The looks the viewports draw that no world object may hold (the
    /// creation previews'); the paperdoll and the examine portrait draw
    /// world objects, whose looks the world keeps.
    pub(crate) fn appearance_keys(&self) -> impl Iterator<Item = &AppearanceKey> + '_ {
        self.shown_looks.iter().chain(&self.wanted_looks).flatten()
    }
}

/// The barber preview's place among the look previews.
const BARBER: usize = 2;

/// A look preview's place: the appearance page's, the summary page's, the
/// barber's.
fn preview_index(slot: ViewportSlot) -> usize {
    match slot {
        ViewportSlot::ChargenSummary => 1,
        ViewportSlot::Barber => BARBER,
        _ => 0,
    }
}

/// The paperdoll figure's id in the selection flash (it is no world
/// object).
pub(crate) const DOLL_GUID: u32 = 0xDA11_D011;
/// The paperdoll figure's render id in the selection flash.
pub(crate) const DOLL_RENDER_ID: u32 = 0xDA11_D012;

/// A scene to draw: the camera and each part with where it stands.
struct SceneDraw {
    camera: ViewportCamera,
    parts: Vec<(MeshRef, Mat4)>,
    /// The parts are the paperdoll figure's (the selection flash lights
    /// them).
    doll: bool,
}

impl WorldRenderer {
    /// Flash the paperdoll figure through the shared selection flash: the
    /// whole figure, or the parts whose bits `part_mask` sets (OpenAC
    /// `DollFigureLighting`).
    pub(crate) fn flash_doll(&mut self, whole_figure: bool, part_mask: u32) {
        let now = self.selection_epoch.elapsed().as_secs_f64();
        if whole_figure {
            self.selection
                .begin_whole_figure_lighting_pulse(DOLL_GUID, DOLL_RENDER_ID, now);
        } else {
            self.selection
                .begin_part_lighting_pulse(DOLL_GUID, DOLL_RENDER_ID, part_mask, now);
        }
    }

    /// How part `index` of the paperdoll figure is lit by the flash.
    fn doll_lighting(&self, index: usize) -> crate::selection::SelectionLighting {
        let normal = crate::selection::SelectionLighting::NORMAL;
        if self.selection.has_part_lighting(DOLL_GUID, DOLL_RENDER_ID) {
            return self
                .selection
                .part_lighting(DOLL_GUID, DOLL_RENDER_ID, index as u32)
                .unwrap_or(normal);
        }
        self.selection
            .lighting(DOLL_GUID, DOLL_RENDER_ID)
            .unwrap_or(normal)
    }

    /// Build and upload what each requested viewport shows this frame;
    /// `controls` are the creation preview's button presses (the
    /// appearance page's). Viewports not requested draw nothing.
    pub(crate) fn prepare_viewports(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: Option<&Scene>,
        requests: &[ViewportRequest],
        controls: &[(ViewportSlot, PreviewRequest)],
    ) -> Vec<(ViewportSlot, ViewportOutcome)> {
        if !requests.iter().any(|r| r.slot == ViewportSlot::Barber) {
            self.viewports.end_barber();
        }
        for (slot, control) in controls {
            let preview = &mut self.viewports.chargen[preview_index(*slot)];
            match control {
                PreviewRequest::RotateClockwise => preview.rotate(RotateDirection::Clockwise),
                PreviewRequest::RotateCounterClockwise => {
                    preview.rotate(RotateDirection::CounterClockwise);
                }
                PreviewRequest::ZoomIn => preview.zoom_in(),
                PreviewRequest::ZoomOut => preview.zoom_out(),
            }
        }
        if self.viewports.gpu.is_none() {
            let layout = &self.globals_layout;
            self.viewports.gpu = Some(
                [
                    "paperdoll viewport",
                    "examine viewport",
                    "chargen viewport",
                    "summary viewport",
                    "barber viewport",
                ]
                .into_iter()
                .map(|label| ViewportGpu::new(device, layout, label))
                .collect(),
            );
        }
        let now = self.viewports.epoch.elapsed().as_secs_f64();
        let mut drawn = [false; ViewportSlot::ALL.len()];
        let mut out = Vec::with_capacity(requests.len());
        for request in requests {
            let built = match &request.subject {
                ViewportSubject::Paperdoll { heritage } => self.paperdoll_scene(scene, *heritage),
                ViewportSubject::Examine { guid } => {
                    self.examine_scene(scene, *guid, request.aspect)
                }
                ViewportSubject::Chargen(look) => self.chargen_scene(request.slot, look, now),
            };
            let outcome = match built {
                Some(mut built) if !built.parts.is_empty() => {
                    built.camera.aspect = request.aspect;
                    let doll = built.doll;
                    let (parts, instances): (Vec<PartDraw>, Vec<_>) = built
                        .parts
                        .iter()
                        .enumerate()
                        .map(|(i, (part, world))| {
                            let mut inst = instance(*world * part.transform);
                            // The paperdoll figure flashes with the selection.
                            if doll {
                                inst = inst.with_selection(self.doll_lighting(i));
                            }
                            (
                                PartDraw {
                                    gfx_obj_id: part.gfx_obj_id,
                                    variant: part.variant,
                                },
                                inst,
                            )
                        })
                        .unzip();
                    let format = self.viewports.format;
                    let Viewports {
                        gpu, generation, ..
                    } = &mut self.viewports;
                    if let Some(gpu) = gpu.as_mut().and_then(|g| g.get_mut(request.slot.index())) {
                        gpu.prepare(
                            device,
                            queue,
                            format,
                            request.size,
                            &built.camera,
                            parts,
                            &instances,
                            generation,
                        );
                    }
                    drawn[request.slot.index()] = true;
                    ViewportOutcome::Drawn
                }
                _ => undrawn(request.slot),
            };
            out.push((request.slot, outcome));
        }
        for (gpu, drawn) in self.viewports.gpu.iter_mut().flatten().zip(drawn) {
            if !drawn {
                gpu.skip();
            }
        }
        out
    }

    /// The texture of `slot` and its generation (a new generation is a new
    /// texture), once one was made.
    pub(crate) fn viewport_texture(&self, slot: ViewportSlot) -> Option<(&wgpu::TextureView, u64)> {
        self.viewports.gpu.as_ref()?.get(slot.index())?.texture()
    }

    /// The texture of `slot` itself.
    #[cfg(test)]
    pub(crate) fn viewport_color(&self, slot: ViewportSlot) -> Option<&wgpu::Texture> {
        self.viewports.gpu.as_ref()?.get(slot.index())?.color()
    }

    /// Record the viewports prepared this frame: each into its texture,
    /// the solid sub-meshes first, then the blended ones in part order.
    pub(crate) fn draw_viewports(&self, encoder: &mut wgpu::CommandEncoder) {
        let Some(gpus) = self.viewports.gpu.as_ref() else {
            return;
        };
        for gpu in gpus {
            let Some(mut pass) = gpu.begin(encoder) else {
                continue;
            };
            let mut state = super::MeshPassState::default();
            for blended in [false, true] {
                for (i, part) in gpu.parts.iter().enumerate() {
                    let Some(mesh) = self.meshes.get(&u64::from(part.gfx_obj_id)) else {
                        continue;
                    };
                    pass.set_vertex_buffer(0, mesh.vertices.slice(..));
                    pass.set_index_buffer(mesh.indices.slice(..), wgpu::IndexFormat::Uint32);
                    let at = i as u32;
                    for (s, sub) in mesh.sub_meshes.iter().enumerate() {
                        if mesh.sub_blended.get(s).copied().unwrap_or(false) != blended {
                            continue;
                        }
                        self.draw_sub_with(
                            &self.pipelines.viewport_meshes,
                            false,
                            &mut pass,
                            &mut state,
                            sub,
                            part.variant,
                            false,
                            at..at + 1,
                            false,
                        );
                    }
                }
            }
        }
    }

    /// The inventory figure: the player's parts with his look, holding his
    /// heritage's pose, turned toward the eye.
    fn paperdoll_scene(&mut self, scene: Option<&Scene>, heritage: u32) -> Option<SceneDraw> {
        let player = scene?.player()?;
        let setup_id = player.setup_id?;
        let scale = player.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let parts = self.object_parts(setup_id, &player.model)?.0.to_vec();
        let turn = Mat4::from_quat(figure_turn());
        let posed = self.pose_animation(pose_enum(heritage)).and_then(|pose| {
            let setup = self.animation_source()?.setup(setup_id)?;
            Some(hold(&setup, held_frame(&pose)?, &parts, scale))
        });
        let parts = match posed {
            Some(posed) => posed.into_iter().map(|p| (p, turn)).collect(),
            // No pose: the parts as they rest, at the body's size.
            None => {
                let world = turn * Mat4::from_scale(Vec3::splat(scale));
                parts.into_iter().map(|p| (p, world)).collect()
            }
        };
        Some(SceneDraw {
            camera: ViewportCamera::new(doll_eye(heritage), FIGURE_FAR),
            parts,
            doll: true,
        })
    }

    /// The examine portrait: the creature as the world draws it this frame
    /// (its pose and look) and what it holds, moved to the origin and
    /// turned toward the eye, the eye fitted to its box.
    fn examine_scene(&self, scene: Option<&Scene>, guid: u32, aspect: f32) -> Option<SceneDraw> {
        let object = scene?.object_by_guid(guid)?;
        let bounds = self.object_parts(object.setup_id?, &object.model)?.1;
        let (draws, _) = self.object_draws_of(scene, Some(guid));
        let mut all: Vec<_> = draws.into_values().flatten().collect();
        let main = all.swap_remove(all.iter().position(|d| d.guid == guid && !d.held)?);
        all.retain(|d| d.held);
        all.sort_by_key(|d| d.guid);
        all.truncate(MAX_HELD);
        let turn = figure_turn();
        // The copy is drawn at the creature's size, so the box it is
        // framed by is too: the model's box grown by the size, or the
        // parts where they stand in the creature's own frame.
        let scale = object.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let own = main.root.inverse() * main.world;
        let offsets = main
            .parts
            .iter()
            .map(|p| (own * p.transform).w_axis.truncate());
        let (min, max) = turned_box(creature_box(bounds, scale, offsets), turn);
        let mut parts = Vec::new();
        // The creature shows as the world shows it (not at all while
        // hidden); what it holds always shows.
        let shown = std::iter::once(&main).filter(|main| main.visible);
        for draw in shown.chain(&all) {
            let world = on_copy(draw.world, main.root, turn);
            parts.extend(draw.parts.iter().map(|p| (*p, world)));
        }
        Some(SceneDraw {
            camera: ViewportCamera::new(examine_eye(min, max, aspect), EXAMINE_FAR),
            parts,
            doll: false,
        })
    }

    /// A creation preview: the look's figure (rebuilt when the look
    /// changed and its parts have arrived) in front of the heritage's
    /// scenery.
    fn chargen_scene(
        &mut self,
        slot: ViewportSlot,
        look: &ChargenLook,
        now: f64,
    ) -> Option<SceneDraw> {
        let index = preview_index(slot);
        // A new body with the same choices (the barber's toggle) is a new
        // look too.
        let rebuild = self.viewports.chargen[index].needs_rebuild(
            look.heritage,
            look.gender,
            &look.selection,
        ) || self.viewports.shown_looks[index].as_ref() != Some(&look.key);
        if rebuild {
            self.viewports.wanted_looks[index] = Some(look.key.clone());
        }
        if rebuild && let Some(figure) = self.chargen_figure(look) {
            self.viewports.shown_looks[index] = Some(look.key.clone());
            if look.backdrop_setup != 0 {
                self.viewports.models.insert(look.backdrop_setup);
            }
            self.viewports.chargen[index].rebuild(
                look.heritage,
                look.gender,
                look.selection,
                figure,
                look.backdrop_setup,
            );
        }
        self.viewports.chargen[index].tick(now);
        let backdrop = self.viewports.chargen[index].backdrop();
        let mut parts = Vec::new();
        if backdrop != 0
            && let Some(scenery) = self.wanted_model(backdrop)
        {
            parts.extend(scenery.into_iter().map(|p| (p, Mat4::IDENTITY)));
        }
        let state = &self.viewports.chargen[index];
        let figure = state.figure()?;
        let world = Mat4::from_quat(state.orientation());
        parts.extend(figure.parts().iter().map(|p| (*p, world)));
        Some(SceneDraw {
            camera: ViewportCamera::new(state.eye, FIGURE_FAR),
            parts,
            doll: false,
        })
    }

    /// The figure of a creation look: `None` until its parts have arrived
    /// (they are asked for) or when its body cannot be read.
    fn chargen_figure(&mut self, look: &ChargenLook) -> Option<ChargenFigure> {
        let parts = self.wanted_parts(&look.key)?;
        let setup = self.animation_source()?.setup(look.key.setup_id)?;
        let rest = self.pose_animation(pose_enum(look.heritage));
        let idle = self.pose_animation(idle_enum(look.heritage));
        Some(ChargenFigure::new(
            &setup,
            &parts,
            rest.as_deref().and_then(held_frame),
            idle,
        ))
    }

    /// The parts of a look, asking the loader for them when they are not
    /// here yet.
    fn wanted_parts(&mut self, key: &AppearanceKey) -> Option<Vec<MeshRef>> {
        if key.is_plain() {
            self.viewports.models.insert(key.setup_id);
            return self.wanted_model(key.setup_id);
        }
        match self.looks.get(key) {
            Some(parts) => parts.clone(),
            None => {
                if self.looks.ask(key) {
                    self.loader
                        .request(LoadRequest::Appearance(Box::new(key.clone())));
                }
                None
            }
        }
    }

    /// A model's parts at rest, asking the loader for them when they are
    /// not here yet.
    fn wanted_model(&mut self, id: u32) -> Option<Vec<MeshRef>> {
        match self.models.get(&id) {
            Some(parts) => parts.clone(),
            None => {
                if self.requested_models.ask(id) {
                    self.loader.request(LoadRequest::Model(id));
                }
                None
            }
        }
    }

    /// The animation of a pose enum, found through the dats' enum maps
    /// once.
    fn pose_animation(&mut self, pose: u32) -> Option<Arc<Animation>> {
        if let Some(known) = self.viewports.poses.get(&pose) {
            return known.clone();
        }
        let content = self.content.get_if_open()?;
        let master = content.dats.uncached().portal.header().master_map_id as u32;
        let did = resolve_pose_did(
            master,
            |id| content.dats.get::<EnumIdMap>(id).ok().flatten(),
            pose,
        );
        let animation = is_animation(did)
            .then(|| content.dats.get::<Animation>(did).ok().flatten())
            .flatten();
        self.viewports.poses.insert(pose, animation.clone());
        animation
    }
}

#[cfg(test)]
mod tests {
    //! The viewport scenes against the retail dats, drawn offscreen on a
    //! GPU the test makes itself (skipped when the machine has none).
    use std::path::PathBuf;
    use std::time::Duration;

    use super::*;
    use crate::camera::FlyInput;
    use crate::content::ContentSlot;
    use crate::renderer::WorldSettings;
    use ac_core::chargen::ChargenAppearanceSelection;

    const HUMAN_MALE_SETUP: u32 = 0x0200_0001;
    /// The Aluvian (heritage 1) creation scenery Setup is read from the
    /// dats' creation table in `ac-runtime`; this test draws the body
    /// alone.
    const SIZE: (u32, u32) = (100, 214);

    fn dat_dir() -> PathBuf {
        std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR")
    }

    fn gpu() -> Option<(wgpu::Device, wgpu::Queue)> {
        crate::test_gpu::device()
    }

    /// The creation previews' looks are kept while a character is being
    /// made and let go once one is in the world.
    #[test]
    fn creation_looks_are_let_go_once_a_character_is_in_the_world() {
        let mut viewports = Viewports::new(wgpu::TextureFormat::Rgba8Unorm);
        let look = AppearanceKey {
            setup_id: HUMAN_MALE_SETUP,
            texture_changes: vec![(0, 0x0500_0001, 0x0500_0002)],
            ..AppearanceKey::default()
        };
        viewports.shown_looks[0] = Some(look.clone());
        viewports.wanted_looks[1] = Some(look.clone());
        let snapshot = |player_guid| {
            ac_runtime::session::PresentationEvent::Snapshot(ac_runtime::world::WorldSnapshot {
                player_guid,
                objects: Vec::new(),
            })
        };
        let mut scene = Scene::default();
        scene.apply(snapshot(None));
        let used = super::super::looks_in_use(Some(&scene), &mut viewports);
        assert!(used.contains(&look), "kept while making a character");
        scene.apply(snapshot(Some(0x5000_000A)));
        let used = super::super::looks_in_use(Some(&scene), &mut viewports);
        assert!(!used.contains(&look), "let go in the world");
        assert_eq!(viewports.appearance_keys().count(), 0);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_human_body_draws_into_a_viewport_texture() {
        let Some((device, queue)) = gpu() else {
            eprintln!("no GPU adapter on this machine: the offscreen draw is not checked");
            return;
        };
        let format = wgpu::TextureFormat::Rgba8Unorm;
        let settings = WorldSettings {
            content: Arc::new(ContentSlot::new(dat_dir())),
            quality_overrides: crate::quality::QualityOverrides {
                near_radius: Some(0),
                far_radius: Some(0),
                ..Default::default()
            },
            residency: Default::default(),
            orbit: Default::default(),
            reveal_radius: None,
            sky: Default::default(),
            audio: Default::default(),
        };
        // The world runs multisampled (the default preset); the viewports
        // still draw single-sampled.
        let mut world = WorldRenderer::new(
            &device,
            &queue,
            format,
            64,
            64,
            &settings,
            &ac_host::settings::DisplaySettings::default(),
            &[1, 4],
        );
        let look = ChargenLook {
            heritage: 1,
            gender: 1,
            selection: ChargenAppearanceSelection::default(),
            key: AppearanceKey {
                setup_id: HUMAN_MALE_SETUP,
                ..AppearanceKey::default()
            },
            backdrop_setup: 0,
        };
        let request = ViewportRequest {
            slot: ViewportSlot::ChargenSummary,
            size: SIZE,
            aspect: SIZE.0 as f32 / SIZE.1 as f32,
            subject: ViewportSubject::Chargen(Box::new(look)),
        };
        // The loader opens the dats and sends the body's meshes; until
        // then the preview has nothing to draw.
        let started = Instant::now();
        let outcome = loop {
            world.prepare(&device, &queue, None, FlyInput::default());
            let out =
                world.prepare_viewports(&device, &queue, None, std::slice::from_ref(&request), &[]);
            if out[0].1 == ViewportOutcome::Drawn {
                break out;
            }
            assert!(
                started.elapsed() < Duration::from_secs(60),
                "the body never arrived: {:?}",
                world.failure()
            );
            std::thread::sleep(Duration::from_millis(20));
        };
        assert_eq!(
            outcome,
            vec![(ViewportSlot::ChargenSummary, ViewportOutcome::Drawn)]
        );
        let (_, generation) = world
            .viewport_texture(ViewportSlot::ChargenSummary)
            .expect("the texture was made");
        assert!(generation > 0);

        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        world.draw_viewports(&mut encoder);
        let texture = world.viewport_color(ViewportSlot::ChargenSummary).unwrap();
        let row = SIZE.0 * 4;
        let padded =
            row.div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT) * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("viewport readback"),
            size: u64::from(padded) * u64::from(SIZE.1),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(padded),
                    rows_per_image: Some(SIZE.1),
                },
            },
            wgpu::Extent3d {
                width: SIZE.0,
                height: SIZE.1,
                depth_or_array_layers: 1,
            },
        );
        queue.submit([encoder.finish()]);
        let (tx, rx) = std::sync::mpsc::channel();
        buffer.map_async(wgpu::MapMode::Read, .., move |r| {
            let _ = tx.send(r);
        });
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        rx.recv().unwrap().expect("the readback maps");
        let mapped = buffer.slice(..).get_mapped_range().unwrap();
        // Count the pixels the body covers (alpha), and where.
        let mut covered = 0usize;
        let (mut top, mut bottom) = (u32::MAX, 0);
        let mut lit = 0usize;
        for y in 0..SIZE.1 {
            let line = &mapped[(y * padded) as usize..][..row as usize];
            for px in line.chunks_exact(4) {
                if px[3] > 0 {
                    covered += 1;
                    top = top.min(y);
                    bottom = bottom.max(y);
                    if px[0].max(px[1]).max(px[2]) > 40 {
                        lit += 1;
                    }
                }
            }
        }
        let total = (SIZE.0 * SIZE.1) as usize;
        eprintln!("covered {covered} of {total}, lit {lit}, rows {top}..{bottom}");
        // The far eye frames the whole standing body: it covers a good part
        // of the view, top to bottom, and the corners stay clear.
        assert!(covered > total / 20, "covered {covered} of {total}");
        assert!(covered < total * 3 / 4, "covered {covered} of {total}");
        assert!(
            lit > covered / 2,
            "the sun lights the front: {lit} of {covered}"
        );
        assert!(bottom - top > SIZE.1 / 2, "rows {top}..{bottom}");
        if let Some(path) = std::env::var_os("OPENAC_TEST_VIEWPORT_PNG") {
            let mut rgba = Vec::new();
            for y in 0..SIZE.1 {
                let line = &mapped[(y * padded) as usize..][..row as usize];
                for px in line.chunks_exact(4) {
                    rgba.extend_from_slice(&[px[0], px[1], px[2], 255]);
                }
            }
            std::fs::write(path, ac_bits::png::encode_rgba(SIZE.0, SIZE.1, &rgba)).unwrap();
        }
        let corner = &mapped[..4];
        assert_eq!(corner[3], 0, "the background stays clear");
    }
}
