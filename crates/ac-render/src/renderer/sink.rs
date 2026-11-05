//! The frame walk's sink: what the walk reaches becomes the frame's draw
//! stream.

use std::collections::{HashMap, HashSet};

use ac_core::world::LiveWorldOrigin;
use ac_vfx::{
    EmitterHandle, ParticleCamera, ParticleFrame, ParticleGfxCache, ParticleRenderPass,
    ParticleSubmissionKind, ParticleSystem, build_particle_frame,
};
use glam::{Mat4, Quat, Vec3};

use super::{CellRef, GfxGpu, NearResident, ObjectDraw, SurfaceGpu};
use crate::animation::{AnimationPresenter, HookOwner, StaticKey};
use crate::draws::{
    ALL_LAND_CELLS, ActiveView, AlphaDraw, BillboardGpu, MeshKey, PUNCH_DEPTH, ParticleMeshGpu,
    PendingParticle, StreamBuilder, active_views, cell_mesh_key, cull_views, instance,
    land_cell_bit,
};
use crate::effects::ledger::ParticleResourceLedger;
use crate::effects::routing::{AlphaList, ParticleSubmission, mesh_particle_blend, route_particle};
use crate::lights::{LightSet, NO_LIGHTS, WindowLights};
use crate::loader::ModelInfo;
use crate::meshing::MeshRef;
use crate::selection::{SelectionLighting, SelectionScene};
use crate::walk::{Bounding, Plane, ViewSet, WalkSink, viewcone_check};
use crate::world_frame::{LandblockCoord, landblock_offset};

/// Unlit by the selection flash.
const NORMAL_LIGHT: SelectionLighting = SelectionLighting::NORMAL;

/// A part of a server object the frame drew, for the world pick.
#[derive(Debug, Clone, Copy)]
pub(super) struct DrawnSelectionPart {
    pub guid: u32,
    pub local_id: u32,
    pub part_index: u32,
    pub gfx_obj_id: u32,
    pub world: Mat4,
}

/// A building as this frame draws it.
pub(super) struct BuildingDraw<'a> {
    /// The shell's model.
    pub model_id: u32,
    /// Building to drawing frame.
    pub world: Mat4,
    /// Part 0's model at this distance (0: not drawn).
    pub part0: u32,
    /// Part 0's placement turned toward the eye when that level faces the
    /// camera (drawing frame); otherwise part 0 stands as the model places it.
    pub part0_turned: Option<Mat4>,
    /// Its bounds (centre, radius), drawing frame.
    pub sphere: (Vec3, f32),
    /// Its data.
    pub _data: &'a crate::cells::BuildingData,
}

/// An animating static as drawn: its posed parts, its placement and its
/// parts' opacities (none when all are solid).
type StaticPose = (Vec<MeshRef>, Mat4, Option<Vec<f32>>);

/// Everything the sink reads.
pub(super) struct SinkInputs<'a> {
    pub width: f32,
    pub height: f32,
    pub eye: Vec3,
    pub cy: Plane,
    pub origin: &'a LiveWorldOrigin,
    pub blocks: &'a [(LandblockCoord, Option<&'a NearResident>)],
    pub buildings: &'a [BuildingDraw<'a>],
    pub cells: &'a HashMap<u32, CellRef<'a>>,
    pub objects: &'a HashMap<u32, Vec<ObjectDraw<'a>>>,
    /// The server objects each cell draws: (the cell `objects` files it
    /// under, its index there).
    pub object_cells: &'a HashMap<u32, Vec<(u32, usize)>>,
    pub models: &'a crate::IdMap<u32, Option<Vec<MeshRef>>>,
    pub model_info: &'a crate::IdMap<u32, ModelInfo>,
    pub meshes: &'a crate::IdMap<MeshKey, GfxGpu>,
    pub lights: &'a WindowLights,
    pub sky_pre: &'a [usize],
    pub sky_post: &'a [usize],
    pub terrain_present: &'a HashSet<LandblockCoord>,
    pub animations: &'a AnimationPresenter,
    /// The selection flash.
    pub selection: &'a SelectionScene,
    /// The atmospheric wind runs: procedural scenery with leaves is marked
    /// in its instances.
    pub wind: bool,
}

/// The sink.
pub(super) struct FrameSink<'a> {
    pub input: SinkInputs<'a>,
    pub out: StreamBuilder,
    pub views: Vec<ActiveView>,
    pub portals_drawn_count: u32,
    /// Part models whose mesh is not here (to ask the loader again).
    pub missing: HashSet<u32>,
    /// The particles, once the effects run.
    pub particles: Option<SinkParticles<'a>>,
    /// Server objects' parts drawn this frame, for the world pick.
    pub selection_parts: Vec<DrawnSelectionPart>,
    /// The outdoor land cells the walk reached (for the shadow casters).
    pub land_cells: Vec<u32>,
    /// The model being drawn is procedural scenery with leaves (its
    /// instances carry the wind bit).
    pub scenery_wind: bool,
    /// What the current stage has drawn.
    pub stage: StageDrawn,
}

/// What one stage of the frame has drawn, so that nothing a stage reaches
/// through several portal views, or through several floods, is drawn
/// twice (a blended surface would blend twice). The stages are the
/// landscape with the buildings' look-ins, and the viewer's interior after
/// the depth clear, which draws its cells again (OpenAC
/// `WalkFrameDriver._cellShellsDrawnThisFrame` and the drawn-part stamp,
/// both cleared at the interior turn).
#[derive(Debug, Default)]
pub(super) struct StageDrawn {
    /// Interior cells whose room mesh is drawn.
    cells: crate::IdSet<u32>,
    /// Parts drawn: their object and index.
    parts: crate::IdSet<(DrawnObject, u32)>,
}

/// The point lights a model is drawn with.
#[derive(Debug, Clone, Copy)]
enum ModelLights {
    /// These.
    Set(LightSet),
    /// Those reaching an object standing in `cell` whose bounds are
    /// `sphere` (`(centre, radius)`), picked only once a part is drawn.
    Object { cell: u32, sphere: (Vec3, f32) },
}

/// Which of a model's parts are in view.
#[derive(Debug, Clone, Copy)]
enum Admission<'v> {
    /// The caller admitted the whole model: every part is drawn.
    Whole,
    /// Each part on its own: its drawing sphere, where the part is drawn,
    /// must be in one of `views`; with `stamp`, each part of that object
    /// is drawn once a stage (the player is not stamped: it is drawn again
    /// in every cell it reaches, as the game does).
    PerPart {
        views: &'v [ActiveView],
        stamp: Option<DrawnObject>,
    },
}

/// A part's drawing sphere (`(centre, radius)`, model-local) where the part
/// is placed by `world`: the centre carried by the transform, the radius
/// scaled by its largest axis scale.
pub(super) fn transform_drawing_sphere(sphere: (Vec3, f32), world: Mat4) -> (Vec3, f32) {
    let scale = world
        .x_axis
        .truncate()
        .length()
        .max(world.y_axis.truncate().length())
        .max(world.z_axis.truncate().length());
    (world.transform_point3(sphere.0), sphere.1 * scale)
}

/// An object a stage drew.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
enum DrawnObject {
    /// A dat static or a piece of scenery.
    Static(StaticKey),
    /// A server object: its id and incarnation.
    Server(u32, u32),
}

impl StageDrawn {
    /// A new stage: everything may be drawn again.
    pub(super) fn clear(&mut self) {
        self.cells.clear();
        self.parts.clear();
    }
}

/// The instance flag bit of procedural scenery with clip-mapped (leaf)
/// sub-meshes: the atmospheric wind sways it.
pub(crate) const SCENERY_WIND_BIT: u32 = 2;

/// A model has a clip-mapped sub-mesh in one of its parts (OpenAC
/// `FoliageWindClassification.ComputeEntityHasCutoutSubset`).
pub(super) fn has_cutout_subset(parts: &[MeshRef], meshes: &crate::IdMap<MeshKey, GfxGpu>) -> bool {
    parts.iter().any(|p| {
        meshes
            .get(&u64::from(p.gfx_obj_id))
            .is_some_and(|g| g.has_clip_map)
    })
}

impl FrameSink<'_> {
    /// The particles of emitters whose owner is in `cell_id`, once per
    /// frame, into the pending blended draws.
    fn cell_particles(&mut self, cell_id: u32) {
        let Some(p) = self.particles.as_mut() else {
            return;
        };
        if !p.drawn_cells.insert(cell_id) {
            return;
        }
        p.system.copy_renderable_emitters_in_cell(
            ParticleRenderPass::Scene,
            cell_id,
            &mut p.handles,
        );
        if p.handles.is_empty() {
            return;
        }
        for (distance, list, item) in p.items(self.input.meshes) {
            self.out.push_particle(distance, list, item);
        }
    }

    /// The particles of a sky pass, drawn now.
    fn sky_particles(&mut self, pass: ParticleRenderPass) {
        let Some(p) = self.particles.as_mut() else {
            return;
        };
        p.handles.clear();
        p.handles
            .extend(p.system.renderable_emitters(pass).map(|e| e.handle));
        if p.handles.is_empty() {
            return;
        }
        let items = p
            .items(self.input.meshes)
            .into_iter()
            .map(|(distance, _, item)| (distance, item))
            .collect();
        self.out.push_particles_now(items);
    }

    /// Whether some view of `views` holds `sphere` in its cone.
    fn admitted(&self, sphere: (Vec3, f32), views: &[ActiveView]) -> bool {
        views.iter().any(|view| {
            viewcone_check(sphere.0, sphere.1, &self.input.cy, &view.planes) != Bounding::Outside
        })
    }

    /// Draw a model's parts at `world` (scale included); `opacities`, when
    /// given, is each part's opacity (else every part is solid). A server
    /// object's (`owner`: its id and incarnation) drawn parts are recorded
    /// for the world pick and lit by the selection flash. `admission` says
    /// which parts are in view; returns whether any part was drawn.
    #[allow(clippy::too_many_arguments)]
    fn draw_model(
        &mut self,
        parts: &[MeshRef],
        world: Mat4,
        shell: bool,
        lights: ModelLights,
        indoor: bool,
        part0: Option<(u32, Option<Mat4>)>,
        opacities: Option<&[f32]>,
        owner: Option<(u32, u32)>,
        admission: Admission<'_>,
    ) -> bool {
        let mut drawn = false;
        let window_lights = self.input.lights;
        let mut picked: Option<LightSet> = None;
        let whole = owner.and_then(|(guid, local)| self.input.selection.lighting(guid, local));
        let by_part =
            owner.is_some_and(|(guid, local)| self.input.selection.has_part_lighting(guid, local));
        for (i, part) in parts.iter().enumerate() {
            let opacity = opacities.and_then(|o| o.get(i)).copied().unwrap_or(1.0);
            if opacity <= 0.0 {
                continue;
            }
            // A building's shell draws the level its distance chose, turned
            // toward the eye when that level faces the camera; every other
            // part draws its own model at any distance (no ladder), except
            // an editor-only marker, which is never drawn.
            let (gfx_id, part_world) = match (i, part0) {
                (0, Some((id, turned))) => (id, turned.unwrap_or(world * part.transform)),
                _ => (part.gfx_obj_id, world * part.transform),
            };
            if gfx_id == 0 {
                continue;
            }
            let gpu = self.input.meshes.get(&u64::from(gfx_id));
            // An editor-only marker is never drawn (a mesh not here yet is
            // not known to be one).
            if gpu.is_some_and(|g| g.hidden_marker) {
                continue;
            }
            // Each part is admitted on its own: its drawing sphere where it
            // is drawn must be in a view, and a stamped part is drawn once a
            // stage, however many cells or views reach it.
            if let Admission::PerPart { views, stamp } = admission {
                let Some(g) = gpu else {
                    self.missing.insert(gfx_id);
                    continue;
                };
                let visible = match g.drawing_sphere.filter(|(_, radius)| *radius > 0.0) {
                    Some(sphere) => {
                        self.admitted(transform_drawing_sphere(sphere, part_world), views)
                    }
                    // Without a sphere a part is in view wherever a view is.
                    None => !views.is_empty(),
                };
                if !visible {
                    continue;
                }
                if let Some(object) = stamp
                    && !self.stage.parts.insert((object, i as u32))
                {
                    continue;
                }
            }
            drawn = true;
            let mut lighting = whole.unwrap_or(NORMAL_LIGHT);
            if let Some((guid, local)) = owner {
                if by_part {
                    lighting = self
                        .input
                        .selection
                        .part_lighting(guid, local, i as u32)
                        .unwrap_or(NORMAL_LIGHT);
                }
                self.selection_parts.push(DrawnSelectionPart {
                    guid,
                    local_id: local,
                    part_index: i as u32,
                    gfx_obj_id: part.gfx_obj_id,
                    world: part_world,
                });
            }
            // An object's lights are picked once one of its parts is drawn.
            let part_lights = match lights {
                ModelLights::Set(set) => set,
                ModelLights::Object { cell, sphere } => *picked.get_or_insert_with(|| {
                    window_lights.object_lights(Some(cell), sphere.0, sphere.1)
                }),
            };
            self.draw_found_mesh(
                u64::from(gfx_id),
                gpu,
                part.variant,
                shell,
                part_world,
                part_lights,
                indoor,
                opacity,
                lighting,
            );
        }
        drawn
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_mesh(
        &mut self,
        mesh: MeshKey,
        variant: u32,
        shell: bool,
        world: Mat4,
        lights: LightSet,
        indoor: bool,
        opacity: f32,
        lighting: SelectionLighting,
    ) {
        let gpu = self.input.meshes.get(&mesh);
        self.draw_found_mesh(
            mesh, gpu, variant, shell, world, lights, indoor, opacity, lighting,
        );
    }

    /// [`Self::draw_mesh`] with `mesh`'s GPU mesh already looked up
    /// (`None`: not here yet, and asked for).
    #[allow(clippy::too_many_arguments)]
    fn draw_found_mesh(
        &mut self,
        mesh: MeshKey,
        gpu: Option<&GfxGpu>,
        variant: u32,
        shell: bool,
        world: Mat4,
        lights: LightSet,
        indoor: bool,
        opacity: f32,
        lighting: SelectionLighting,
    ) {
        let Some(gpu) = gpu else {
            if mesh >> 32 == 0 {
                self.missing.insert(mesh as u32);
            }
            return;
        };
        let mut inst = instance(world, lights, indoor)
            .with_opacity(opacity)
            .with_detail(crate::draws::DetailCategory::of(mesh, shell))
            .with_selection(lighting);
        if self.scenery_wind {
            inst.indoor |= SCENERY_WIND_BIT;
        }
        // A translucent instance draws every sub-mesh blended, sorted with
        // the rest (OpenAC turns its solid batches into alpha blending).
        let faded = opacity < 1.0;
        if gpu.has_opaque && !faded {
            self.out.push_opaque(mesh, shell, variant, inst);
        }
        if faded || gpu.sub_blended.iter().any(|b| *b) {
            let distance = (world.transform_point3(gpu.sort_center) - self.input.eye).length();
            for (sub, blended) in gpu.sub_blended.iter().enumerate() {
                if *blended || faded {
                    self.out.push_alpha(
                        distance,
                        AlphaDraw {
                            mesh,
                            variant,
                            shell,
                            sub: sub as u32,
                            instance: 0,
                            faded: !*blended,
                        },
                        inst,
                    );
                }
            }
        }
    }

    /// The sphere around a model placed at `world`.
    fn model_sphere(&self, model_id: u32, world: Mat4) -> (Vec3, f32) {
        sphere_of(
            self.input.model_info.get(&model_id).and_then(|i| i.bounds),
            world,
        )
    }

    /// An animating static's parts in their pose (without the parts a
    /// fade hook hid), its placement turned by its spin, and its parts'
    /// opacities when a fade made some translucent; `None` when the static
    /// does not animate.
    fn animated_static(
        &self,
        key: StaticKey,
        parts: &[MeshRef],
        transform: Mat4,
    ) -> Option<StaticPose> {
        static_pose(self.input.animations, key, parts, transform)
    }

    fn draw_objects(&mut self, key: u32, views: &[ActiveView]) {
        self.draw_objects_in(key, views);
    }
}

/// An animating static's posed parts, its placement turned by its spin,
/// and its parts' opacities when a fade made some translucent; `None` when
/// the static does not animate.
pub(super) fn static_pose(
    animations: &AnimationPresenter,
    key: StaticKey,
    parts: &[MeshRef],
    transform: Mat4,
) -> Option<StaticPose> {
    let animation = animations.static_animation(key)?;
    let placed = if animation.step.spin == Quat::IDENTITY {
        transform
    } else {
        let (scale, rotation, translation) = transform.to_scale_rotation_translation();
        Mat4::from_scale_rotation_translation(scale, animation.step.spin * rotation, translation)
    };
    let owner = HookOwner::Static(key);
    let (posed, opacities) = animations.faded_parts(owner, 1.0, parts, |i, part| {
        animation.poses.get(i).map_or(part.transform, |p| p.visual)
    });
    Some((posed, placed, opacities))
}

impl FrameSink<'_> {
    fn draw_objects_in(&mut self, key: u32, views: &[ActiveView]) {
        let Some(list) = self.input.object_cells.get(&key) else {
            return;
        };
        let mut drawn = 0;
        for &(home, index) in list {
            let Some(object) = self
                .input
                .objects
                .get(&home)
                .and_then(|draws| draws.get(index))
            else {
                continue;
            };
            // The player is not stamped: it is drawn in every cell it
            // reaches.
            let stamp =
                (!object.is_player).then_some(DrawnObject::Server(object.guid, object.local_id));
            if self.draw_model(
                &object.parts,
                object.world,
                false,
                ModelLights::Set(object.lights),
                object.indoor,
                None,
                object.opacities.as_deref(),
                Some((object.guid, object.local_id)),
                Admission::PerPart { views, stamp },
            ) {
                drawn += 1;
            }
        }
        self.out.draws.objects_drawn += drawn;
    }
}

/// The sphere around a box `bounds` (model-local) placed at `world`; a
/// 1 m sphere at the origin without bounds.
pub(super) fn sphere_of(bounds: Option<(Vec3, Vec3)>, world: Mat4) -> (Vec3, f32) {
    match bounds {
        Some((lo, hi)) => {
            let center = world.transform_point3((lo + hi) * 0.5);
            let scale = world
                .x_axis
                .truncate()
                .length()
                .max(world.y_axis.truncate().length())
                .max(world.z_axis.truncate().length());
            (center, (hi - lo).length() * 0.5 * scale)
        }
        None => (world.w_axis.truncate(), 1.0),
    }
}

impl WalkSink for FrameSink<'_> {
    fn landscape_begin(&mut self, views: &ViewSet, view_count: usize, full_screen: bool) {
        self.views = if full_screen {
            vec![ActiveView {
                clip: 0,
                planes: views.planes(0),
            }]
        } else {
            active_views(
                &mut self.out.draws,
                views,
                view_count,
                self.input.width,
                self.input.height,
            )
        };
        // The sky, once: it is drawn whole, however many views look out.
        self.out.push_sky(false, self.input.sky_pre.iter().copied());
        // The sky's particles, once, unclipped, after the sky objects.
        if let Some(p) = self.particles.as_mut()
            && !p.sky_pre_done
        {
            p.sky_pre_done = true;
            self.sky_particles(ParticleRenderPass::SkyPreScene);
        }
    }

    fn land_block(&mut self, block: usize) {
        let (coord, _) = self.input.blocks[block];
        if !self.input.terrain_present.contains(&coord) {
            return;
        }
        let offset = landblock_offset(self.input.origin, coord);
        self.out.push_terrain(coord, offset, ALL_LAND_CELLS);
    }

    fn land_cell_terrain(&mut self, block: usize, cell: usize) {
        let (coord, _) = self.input.blocks[block];
        if !self.input.terrain_present.contains(&coord) {
            return;
        }
        let offset = landblock_offset(self.input.origin, coord);
        self.out.push_terrain(coord, offset, land_cell_bit(cell));
    }

    fn land_cell(&mut self, block: usize, cell: usize) {
        let (coord, near) = self.input.blocks[block];
        // The views are lent out for the cell (nothing it draws changes them).
        let views = std::mem::take(&mut self.views);
        if let Some(near) = near {
            let offset = Mat4::from_translation(landblock_offset(self.input.origin, coord));
            let landblock = coord.terrain_id() & 0xFFFF_0000;
            for &i in &near.by_cell[cell] {
                let s = &near.statics[i];
                let Some(Some(parts)) = self.input.models.get(&s.model_id) else {
                    continue;
                };
                let key = StaticKey {
                    landblock,
                    cell: 0,
                    index: i as u32,
                };
                let animated = self.animated_static(key, parts, s.transform);
                let (parts, transform, opacities) = match &animated {
                    Some((posed, placed, opacities)) => {
                        (posed.as_slice(), *placed, opacities.as_deref())
                    }
                    None => (parts.as_slice(), s.transform, None),
                };
                let world = offset * transform;
                let sphere = self.model_sphere(s.model_id, world);
                // A landscape static is listed in one land cell and a frame
                // walks the landscape once, so it needs no stamp: a view
                // admitting it is enough.
                if !self.admitted(sphere, &views) {
                    continue;
                }
                self.scenery_wind = self.input.wind
                    && i >= near.scenery_start
                    && has_cutout_subset(parts, self.input.meshes);
                self.draw_model(
                    parts,
                    world,
                    false,
                    ModelLights::Set(NO_LIGHTS),
                    false,
                    None,
                    opacities,
                    None,
                    Admission::Whole,
                );
                self.out.draws.objects_drawn += 1;
                self.scenery_wind = false;
            }
        }
        let key = (coord.terrain_id() & 0xFFFF_0000) | (cell as u32 + 1);
        self.land_cells.push(key);
        self.draw_objects(key, &views);
        if let Some(p) = self.particles.as_mut() {
            p.visible_cells.push(key);
        }
        self.cell_particles(key);
        self.views = views;
    }

    fn sort_cell_exit(&mut self, _block: usize, _cell: usize) {
        self.out.sort_cell_exit();
    }

    fn building_drawable(&mut self, building: usize) -> bool {
        let b = &self.input.buildings[building];
        let drawable = b.part0 != 0 && self.input.meshes.contains_key(&u64::from(b.part0));
        // A building begins with the blended draws so far drawn.
        if drawable {
            self.out.flush_alpha();
        }
        drawable
    }

    fn punch(&mut self, polygon: &[Vec3], view: usize) {
        let clip = self.views.get(view).map_or(0, |v| v.clip);
        self.out.push_portal(polygon, clip, PUNCH_DEPTH);
    }

    fn draw_cells(&mut self, cells: &[(u32, ViewSet, usize)]) {
        for (cell_id, views, count) in cells {
            let Some(cell) = self.input.cells.get(cell_id) else {
                continue;
            };
            // The cell's views only cull its objects: everything is drawn
            // whole, so no clip region is made for them.
            let active = cull_views(views, *count);
            let offset = Mat4::from_translation(landblock_offset(self.input.origin, cell.coord));
            let world = offset * cell.data.transform;
            let key = cell_mesh_key(*cell_id);
            // The room once a stage, however many views or floods reach it.
            let first = self.stage.cells.insert(*cell_id);
            if first && let Some(gpu) = self.input.meshes.get(&key) {
                let sphere = sphere_of(gpu.bounds, world);
                let lights = self
                    .input
                    .lights
                    .object_lights(Some(*cell_id), sphere.0, sphere.1);
                self.draw_mesh(key, 0, false, world, lights, true, 1.0, NORMAL_LIGHT);
            }
            let landblock = cell_id & 0xFFFF_0000;
            // Every static whose drawn parts reach into the cell, whichever
            // cell lists it (a staircase listed in the room above is drawn
            // in the room below too).
            for member in &cell.data.members {
                let Some(s) = self
                    .input
                    .cells
                    .get(&member.cell)
                    .and_then(|owner| owner.data.statics.get(member.index as usize))
                else {
                    continue;
                };
                let Some(Some(parts)) = self.input.models.get(&s.model_id) else {
                    continue;
                };
                let key = StaticKey {
                    landblock,
                    cell: member.cell,
                    index: member.index,
                };
                let animated = self.animated_static(key, parts, s.transform);
                let (parts, transform, opacities) = match &animated {
                    Some((posed, placed, opacities)) => {
                        (posed.as_slice(), *placed, opacities.as_deref())
                    }
                    None => (parts.as_slice(), s.transform, None),
                };
                let world = offset * transform;
                // Lit by the lights its whole sphere reaches, as the cell it
                // stands in; each part is admitted by its own sphere.
                let lights = ModelLights::Object {
                    cell: member.cell,
                    sphere: self.model_sphere(s.model_id, world),
                };
                if self.draw_model(
                    parts,
                    world,
                    false,
                    lights,
                    true,
                    None,
                    opacities,
                    None,
                    Admission::PerPart {
                        views: &active,
                        stamp: Some(DrawnObject::Static(key)),
                    },
                ) {
                    self.out.draws.objects_drawn += 1;
                }
            }
            self.draw_objects(*cell_id, &active);
            self.cell_particles(*cell_id);
            self.out.end_cell();
            if first {
                self.out.draws.cells_drawn += 1;
            }
        }
    }

    fn building_shell(&mut self, building: usize) {
        let b = &self.input.buildings[building];
        let Some(Some(parts)) = self.input.models.get(&b.model_id) else {
            return;
        };
        let (world, sphere, part0) = (b.world, b.sphere, (b.part0, b.part0_turned));
        if !self.admitted(sphere, &self.views) {
            return;
        }
        self.draw_model(
            parts,
            world,
            true,
            ModelLights::Set(NO_LIGHTS),
            false,
            Some(part0),
            None,
            None,
            Admission::Whole,
        );
        self.out.draws.objects_drawn += 1;
    }

    fn interior_landscape_done(&mut self, seals: &[(Vec<Vec3>, ViewSet, usize)]) {
        self.landscape_end();
        // The viewer's interior is drawn again after the depth clear: its
        // cells, objects and particles may be drawn once more (OpenAC
        // clears its drawn-cell, drawn-part and particle-turn sets here).
        self.stage.clear();
        if let Some(p) = self.particles.as_mut() {
            p.drawn_cells.clear();
        }
        if self.portals_drawn_count != 0 {
            self.out.push_depth_clear();
            self.portals_drawn_count = 0;
        }
        for (polygon, views, count) in seals {
            for v in 0..*count {
                let clip = self
                    .out
                    .draws
                    .clip_for(views, v, self.input.width, self.input.height);
                self.out.push_portal(polygon, clip, -1.0);
                self.portals_drawn_count += 1;
            }
        }
    }
}

impl FrameSink<'_> {
    /// The landscape is done: its blended draws, then the weather.
    pub fn landscape_end(&mut self) {
        self.out.flush_alpha();
        self.out.push_sky(true, self.input.sky_post.iter().copied());
        // The weather's particles, once, when the player is outdoors.
        if let Some(p) = self.particles.as_mut()
            && p.weather
        {
            p.weather = false;
            self.sky_particles(ParticleRenderPass::SkyPostScene);
        }
        self.views.clear();
    }
}

/// What the sink needs to draw particles.
pub(super) struct SinkParticles<'a> {
    /// The particles.
    pub system: &'a ParticleSystem,
    /// Particle-model facts.
    pub gfx: &'a mut ParticleGfxCache,
    /// The camera the quads face.
    pub camera: ParticleCamera,
    /// Surfaces on the GPU.
    pub surfaces: &'a HashMap<u32, SurfaceGpu>,
    /// Emitters' models and textures, held while they draw.
    pub ledger: &'a mut ParticleResourceLedger,
    /// The player stands outdoors: the weather's particles are drawn.
    pub weather: bool,
    /// Cells whose particles were drawn this frame (each once).
    pub drawn_cells: crate::IdSet<u32>,
    /// Outdoor land cells the frame drew (the next frame's view).
    pub visible_cells: Vec<u32>,
    /// Textures to ask the loader for.
    pub missing_surfaces: HashSet<u32>,
    /// Particle models to ask the loader for.
    pub missing_gfx: HashSet<u32>,
    /// The sky's particles before the world were drawn.
    pub sky_pre_done: bool,
    frame: ParticleFrame,
    handles: Vec<EmitterHandle>,
}

impl<'a> SinkParticles<'a> {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        system: &'a ParticleSystem,
        gfx: &'a mut ParticleGfxCache,
        camera: ParticleCamera,
        surfaces: &'a HashMap<u32, SurfaceGpu>,
        ledger: &'a mut ParticleResourceLedger,
        weather: bool,
    ) -> Self {
        Self {
            system,
            gfx,
            camera,
            surfaces,
            ledger,
            weather,
            drawn_cells: crate::IdSet::default(),
            visible_cells: Vec::new(),
            missing_surfaces: HashSet::new(),
            missing_gfx: HashSet::new(),
            sky_pre_done: false,
            frame: ParticleFrame::default(),
            handles: Vec::new(),
        }
    }

    /// The draws of `handles`' particles, in emission order, each with its
    /// distance from the eye (metres). Models and textures not on the GPU
    /// are asked for (`missing_gfx`, `missing_surfaces`) and their
    /// particles wait.
    fn items(
        &mut self,
        meshes: &crate::IdMap<MeshKey, GfxGpu>,
    ) -> Vec<(f32, AlphaList, PendingParticle)> {
        self.frame.clear();
        let system = self.system;
        build_particle_frame(
            self.handles.iter().filter_map(|h| system.emitter(*h)),
            &self.camera,
            self.gfx,
            &mut self.frame,
        );
        let mut out = Vec::with_capacity(self.frame.submissions.len());
        for s in &self.frame.submissions {
            match s.kind {
                ParticleSubmissionKind::Billboard => {
                    let b = &self.frame.billboards[s.index];
                    if b.surface_id != 0 {
                        self.ledger.hold_surface(b.emitter, b.surface_id);
                        if !self.surfaces.contains_key(&b.surface_id) {
                            self.missing_surfaces.insert(b.surface_id);
                            continue;
                        }
                    }
                    let gpu = BillboardGpu {
                        center: b
                            .center
                            .extend(if b.surface_id != 0 { 1.0 } else { 0.0 })
                            .to_array(),
                        axis_x: b.axis_x.extend(0.0).to_array(),
                        axis_y: b.axis_y.extend(0.0).to_array(),
                        color: argb_to_rgba(b.color_argb),
                    };
                    out.push((
                        b.distance_sq.max(0.0).sqrt(),
                        route_particle(ParticleSubmission::Billboard).list,
                        PendingParticle::Billboard {
                            gpu,
                            surface: b.surface_id,
                            additive: b.additive,
                        },
                    ));
                }
                ParticleSubmissionKind::Mesh => {
                    let m = &self.frame.meshes[s.index];
                    self.ledger.hold_gfx_obj(m.emitter, m.gfx_obj_id);
                    let Some(gpu) = meshes.get(&u64::from(m.gfx_obj_id)) else {
                        self.missing_gfx.insert(m.gfx_obj_id);
                        continue;
                    };
                    let instance = ParticleMeshGpu {
                        model: m.model.to_cols_array(),
                        color: argb_to_rgba(m.color_argb),
                    };
                    let distance = m.distance_sq.max(0.0).sqrt();
                    for (sub, range) in gpu.sub_meshes.iter().enumerate() {
                        let Some(surface) = self.surfaces.get(&range.surface_id) else {
                            continue;
                        };
                        out.push((
                            distance,
                            route_particle(ParticleSubmission::Mesh(
                                surface.translucency,
                                m.color_argb,
                            ))
                            .list,
                            PendingParticle::Mesh {
                                gpu: instance,
                                gfx_obj_id: m.gfx_obj_id,
                                sub: sub as u32,
                                blend: mesh_particle_blend(surface.translucency, m.color_argb),
                                cull: range.cull,
                            },
                        ));
                    }
                }
            }
        }
        out
    }
}

/// An ARGB colour as straight RGBA, 0..1.
fn argb_to_rgba(argb: u32) -> [f32; 4] {
    let c = |shift: u32| ((argb >> shift) & 0xFF) as f32 / 255.0;
    [c(16), c(8), c(0), c(24)]
}

#[cfg(test)]
mod once_per_stage;

#[cfg(test)]
mod tests {
    use super::*;

    /// OpenAC `WbDrawDispatcherLookInConeTests.DrawingSphereAdmissionUsesTheIndividualPartTransformAndLargestScale`:
    /// the sphere a view admits is the box's centre carried by the
    /// transform, its radius scaled by the largest axis scale.
    #[test]
    fn the_admission_sphere_uses_the_transform_and_the_largest_scale() {
        let world = Mat4::from_translation(Vec3::new(10.0, 20.0, 30.0))
            * Mat4::from_scale(Vec3::new(2.0, 3.0, 4.0));
        // A box whose own sphere is centred at (1, 2, 3) with radius 0.5.
        let half = Vec3::splat(0.5 / 3f32.sqrt());
        let centre = Vec3::new(1.0, 2.0, 3.0);
        let (c, r) = sphere_of(Some((centre - half, centre + half)), world);
        assert!(c.abs_diff_eq(Vec3::new(12.0, 26.0, 42.0), 1e-5), "{c}");
        assert!((r - 2.0).abs() < 1e-5, "{r}");
    }

    /// OpenAC `WbDrawDispatcherLookInConeTests.DrawingSphereAdmissionUsesTheIndividualPartTransformAndLargestScale`:
    /// a part's drawing sphere (centre (1, 2, 3), radius 0.5) placed by a
    /// scale of (2, 3, 4) then a move of (10, 20, 30) is centred at
    /// (12, 26, 42) with radius 2.
    #[test]
    fn a_parts_drawing_sphere_follows_its_transform_and_largest_scale() {
        let world = Mat4::from_translation(Vec3::new(10.0, 20.0, 30.0))
            * Mat4::from_scale(Vec3::new(2.0, 3.0, 4.0));
        let (c, r) = transform_drawing_sphere((Vec3::new(1.0, 2.0, 3.0), 0.5), world);
        assert_eq!(c, Vec3::new(12.0, 26.0, 42.0));
        assert_eq!(r, 2.0);
    }
}
