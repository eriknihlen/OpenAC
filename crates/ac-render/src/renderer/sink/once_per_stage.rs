//! A room, an object, the terrain or the sky that a frame reaches through
//! several portal views, or through several floods, is drawn once a stage
//! (OpenAC `WalkFrameDriver`: the drawn-cell set and the drawn-part stamp,
//! cleared at the interior turn; the sky once a frame). Real dats and a
//! GPU (the meshes are GPU buffers); ignored by default as the other
//! real-dat tests, skipped on a machine without a GPU.

use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;

use ac_core::world::LiveWorldOrigin;
use ac_dat::objects::Region;
use glam::{Mat4, Vec3};

use super::{BuildingDraw, FrameSink, SinkInputs, sphere_of};
use crate::animation::AnimationPresenter;
use crate::assets::{self, GfxObjData, NearData, REGION_ID};
use crate::content::Content;
use crate::draws::{
    ALL_LAND_CELLS, AlphaItem, FrameDraws, MeshKey, Step, StreamBuilder, cell_mesh_key,
    land_cell_bit,
};
use crate::lights::{NO_LIGHTS, WindowLights};
use crate::loader::ModelInfo;
use crate::meshing::{MeshRef, Translucency};
use crate::renderer::{CellRef, GfxGpu, NearResident, ObjectDraw};
use crate::selection::SelectionScene;
use crate::walk::{BuildingRef, FrameWalk, ViewSet, WalkBlock, WalkCamera, WalkSink};
use crate::world_frame::LandblockCoord;

fn content() -> Content {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Content::open(&dir).expect("open the retail dats")
}

fn gpu() -> Option<wgpu::Device> {
    crate::test_gpu::device().map(|(device, _)| device)
}

/// One landblock's near content as the renderer holds it.
struct Area {
    coord: LandblockCoord,
    near: NearData,
    resident: NearResident,
    models: crate::IdMap<u32, Option<Vec<MeshRef>>>,
    model_info: crate::IdMap<u32, ModelInfo>,
    meshes: crate::IdMap<MeshKey, GfxGpu>,
}

impl Area {
    fn load(d: &Content, device: &wgpu::Device, coord: LandblockCoord) -> Self {
        let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
        let near = assets::build_near(d, coord, &region);
        let mut translucency: HashMap<u32, Translucency> = HashMap::new();
        let mut gpu_mesh = |gfx: &GfxObjData| {
            for sub in &gfx.sub_meshes {
                translucency.entry(sub.surface_id).or_insert_with(|| {
                    assets::build_surface_with(d, sub.surface_id, sub.surface_id, None, None, true)
                        .translucency
                });
            }
            // The sink reads only the mesh's facts: its buffers stay empty.
            let buffer = || {
                device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("once-per-stage test mesh"),
                    size: 16,
                    usage: wgpu::BufferUsages::VERTEX,
                    mapped_at_creation: false,
                })
            };
            GfxGpu::new(buffer(), buffer(), gfx, |id| translucency.get(&id).copied())
        };
        let mut meshes = crate::IdMap::default();
        for cell in &near.cells {
            if let Some(mesh) = &cell.mesh {
                meshes.insert(cell_mesh_key(cell.id), gpu_mesh(mesh));
            }
        }
        let mut models = crate::IdMap::default();
        let mut model_info = crate::IdMap::default();
        let mut gfx_bounds = HashMap::new();
        let ids: HashSet<u32> = near.model_ids().collect();
        for id in ids {
            let parts = assets::model_parts(d, id);
            let mut todo: Vec<u32> = parts.iter().flatten().map(|p| p.gfx_obj_id).collect();
            while let Some(gfx_id) = todo.pop() {
                if meshes.contains_key(&u64::from(gfx_id)) {
                    continue;
                }
                let Some(gfx) = assets::build_gfx_obj(d, gfx_id) else {
                    continue;
                };
                todo.extend(
                    gfx.degrades
                        .iter()
                        .map(|l| l.gfx_obj_id)
                        .filter(|&l| l != 0),
                );
                if let Some(b) = assets::gfx_bounds(&gfx) {
                    gfx_bounds.insert(gfx_id, b);
                }
                meshes.insert(u64::from(gfx_id), gpu_mesh(&gfx));
            }
            let bounds = parts
                .as_deref()
                .and_then(|p| assets::parts_bounds(p, |g| gfx_bounds.get(&g).copied()));
            model_info.insert(
                id,
                ModelInfo {
                    bounds,
                    lights: Vec::new(),
                },
            );
            models.insert(id, parts);
        }
        // The landblock's own statics and scenery by land cell, as the
        // renderer files them.
        let placed: Vec<_> = near
            .statics
            .iter()
            .filter(|s| !s.is_building_shell)
            .chain(&near.scenery)
            .cloned()
            .collect();
        let mut by_cell = vec![Vec::new(); 64];
        for (i, s) in placed.iter().enumerate() {
            let p = s.transform.w_axis;
            let cell = |v: f32| ((v / 24.0).floor().max(0.0) as usize).min(7);
            by_cell[cell(p.x) * 8 + cell(p.y)].push(i);
        }
        let resident = NearResident {
            scenery_start: near.statics.iter().filter(|s| !s.is_building_shell).count(),
            statics: placed,
            by_cell,
            buildings: near.buildings.clone(),
            cells: near.cells.clone(),
            model_ids: Vec::new(),
            light_statics: Vec::new(),
        };
        Self {
            coord,
            near,
            resident,
            models,
            model_info,
            meshes,
        }
    }

    fn walk(&self) -> FrameWalk {
        let mut walk = FrameWalk::new();
        for cell in &self.near.cells {
            walk.insert_cell(cell.walk.clone());
        }
        walk
    }

    fn block(&self) -> WalkBlock {
        WalkBlock {
            coord: (self.coord.x, self.coord.y),
            origin: Vec3::ZERO,
            min_z: 0.0,
            max_z: 200.0,
            near: true,
            buildings: self
                .near
                .buildings
                .iter()
                .enumerate()
                .map(|(i, b)| (b.land_cell, i))
                .collect(),
        }
    }

    /// One frame through the sink, rooted at `root`; `objects` are the
    /// server objects by cell.
    fn frame(
        &self,
        root: u32,
        camera: &WalkCamera,
        objects: &HashMap<u32, Vec<ObjectDraw<'_>>>,
    ) -> FrameDraws {
        let mut origin = LiveWorldOrigin::new();
        origin.try_initialize(i32::from(self.coord.x), i32::from(self.coord.y));
        let block_refs = [(self.coord, Some(&self.resident))];
        let buildings: Vec<BuildingDraw<'_>> = self
            .resident
            .buildings
            .iter()
            .map(|b| BuildingDraw {
                model_id: b.model_id,
                world: b.transform,
                part0: b.part0.0,
                part0_turned: None,
                sphere: sphere_of(
                    self.model_info.get(&b.model_id).and_then(|i| i.bounds),
                    b.transform,
                ),
                _data: b,
            })
            .collect();
        let building_refs: Vec<BuildingRef<'_>> = self
            .resident
            .buildings
            .iter()
            .map(|b| BuildingRef {
                building: &b.walk,
                tree: b.trees.get(&b.part0.0),
            })
            .collect();
        let cells: HashMap<u32, CellRef<'_>> = self
            .resident
            .cells
            .iter()
            .map(|c| {
                (
                    c.id,
                    CellRef {
                        coord: self.coord,
                        data: c,
                    },
                )
            })
            .collect();
        let lights = WindowLights::default();
        let animations = AnimationPresenter::default();
        let selection = SelectionScene::default();
        let terrain_present: HashSet<LandblockCoord> = [self.coord].into();
        let object_cells = crate::renderer::file_object_draws(objects, |_| None);
        let mut sink = FrameSink {
            input: SinkInputs {
                width: camera.width,
                height: camera.height,
                eye: camera.eye,
                cy: camera.cy_plane(),
                origin: &origin,
                blocks: &block_refs,
                buildings: &buildings,
                cells: &cells,
                objects,
                object_cells: &object_cells,
                models: &self.models,
                model_info: &self.model_info,
                meshes: &self.meshes,
                lights: &lights,
                // One sky object before and one after the world.
                sky_pre: &[0],
                sky_post: &[0],
                terrain_present: &terrain_present,
                animations: &animations,
                selection: &selection,
                wind: false,
            },
            out: StreamBuilder::new(),
            views: Vec::new(),
            portals_drawn_count: 0,
            missing: HashSet::new(),
            particles: None,
            selection_parts: Vec::new(),
            land_cells: Vec::new(),
            scenery_wind: false,
            stage: Default::default(),
        };
        let mut walk = self.walk();
        walk.walk_frame(root, camera, &[self.block()], &building_refs, &mut sink);
        if !sink.views.is_empty() {
            sink.landscape_end();
        }
        sink.out.finish()
    }

    /// What the walk reaches from `root`: each flood's cells with their
    /// view counts, and the landscape's view count.
    fn reached(&self, root: u32, camera: &WalkCamera) -> Reached {
        let building_refs: Vec<BuildingRef<'_>> = self
            .near
            .buildings
            .iter()
            .map(|b| BuildingRef {
                building: &b.walk,
                tree: b.trees.get(&b.part0.0),
            })
            .collect();
        let mut reached = Reached::default();
        self.walk()
            .walk_frame(root, camera, &[self.block()], &building_refs, &mut reached);
        reached
    }
}

/// The walk's turns, recorded.
#[derive(Default)]
struct Reached {
    floods: Vec<Vec<(u32, usize)>>,
    outside_views: usize,
}

impl WalkSink for Reached {
    fn landscape_begin(&mut self, _: &ViewSet, count: usize, _: bool) {
        self.outside_views = count;
    }
    fn land_block(&mut self, _: usize) {}
    fn land_cell_terrain(&mut self, _: usize, _: usize) {}
    fn land_cell(&mut self, _: usize, _: usize) {}
    fn sort_cell_exit(&mut self, _: usize, _: usize) {}
    fn building_drawable(&mut self, _: usize) -> bool {
        true
    }
    fn punch(&mut self, _: &[Vec3], _: usize) {}
    fn draw_cells(&mut self, cells: &[(u32, ViewSet, usize)]) {
        self.floods
            .push(cells.iter().map(|(id, _, count)| (*id, *count)).collect());
    }
    fn building_shell(&mut self, _: usize) {}
    fn interior_landscape_done(&mut self, _: &[(Vec<Vec3>, ViewSet, usize)]) {}
}

impl Reached {
    /// The most floods any one cell is in.
    fn most_floods(&self) -> usize {
        let mut times: HashMap<u32, usize> = HashMap::new();
        for (id, _) in self.floods.iter().flatten() {
            *times.entry(*id).or_default() += 1;
        }
        times.values().copied().max().unwrap_or(0)
    }
}

/// A mesh drawing: the mesh, its placement's bits and the sub-mesh
/// (`u32::MAX` for a solid instance, which draws all its solid ones).
type Drawing = (MeshKey, [u32; 16], u32);

/// The frame's mesh drawings per stage (a stage ends at the sky drawn
/// after the world), each with how often it is drawn.
fn drawings_by_stage(frame: &FrameDraws) -> Vec<HashMap<Drawing, usize>> {
    let model = |i: u32| frame.instances[i as usize].model.map(f32::to_bits);
    let mut stages = vec![HashMap::new()];
    for step in &frame.steps {
        let stage = stages.last_mut().unwrap();
        match step {
            Step::Groups(range) => {
                for group in &frame.groups[range.clone()] {
                    for i in group.instances.clone() {
                        *stage.entry((group.mesh, model(i), u32::MAX)).or_default() += 1;
                    }
                }
            }
            Step::Alpha(range) => {
                for item in &frame.alpha[range.clone()] {
                    if let AlphaItem::Mesh(d) = item {
                        *stage.entry((d.mesh, model(d.instance), d.sub)).or_default() += 1;
                    }
                }
            }
            Step::Sky { post: true, .. } => stages.push(HashMap::new()),
            _ => {}
        }
    }
    stages
}

/// Every drawing appears once in its stage.
fn assert_each_drawn_once(frame: &FrameDraws) {
    for (s, stage) in drawings_by_stage(frame).iter().enumerate() {
        let twice: Vec<String> = stage
            .iter()
            .filter(|(_, n)| **n > 1)
            .map(|((mesh, _, sub), n)| format!("{mesh:#x} sub {sub} x{n}"))
            .collect();
        assert!(twice.is_empty(), "stage {s} draws twice: {twice:?}");
    }
}

/// How often `mesh` is drawn: solid instances and blended entries.
fn count(frame: &FrameDraws, mesh: MeshKey) -> (usize, usize) {
    let solid = frame
        .groups
        .iter()
        .filter(|g| g.mesh == mesh)
        .map(|g| g.instances.len())
        .sum();
    let blended = frame
        .alpha
        .iter()
        .filter(|a| matches!(a, AlphaItem::Mesh(d) if d.mesh == mesh))
        .count();
    (solid, blended)
}

/// A 1280 x 720 camera at `eye` looking along `heading` (degrees
/// counter-clockwise from east), level.
fn camera(eye: Vec3, heading: f32) -> WalkCamera {
    let forward = Vec3::new(heading.to_radians().cos(), heading.to_radians().sin(), 0.0);
    let view = glam::camera::rh::view::look_at_mat4(eye, eye + forward, Vec3::Z);
    let proj = glam::camera::rh::proj::directx::perspective(1.2, 16.0 / 9.0, 0.1, 5000.0);
    let view_proj = proj * view;
    WalkCamera {
        view_proj,
        inverse_view_proj: view_proj.inverse(),
        eye,
        forward,
        width: 1280.0,
        height: 720.0,
    }
}

/// The Town Network: from room 0132 the room 0144 is seen through two
/// doorways (two views). It holds a static with a blended part; a
/// translucent server object is placed in it too. Each is drawn once.
#[test]
#[ignore = "needs the retail dat files and a GPU"]
fn a_room_seen_through_two_doorways_is_drawn_once() {
    let Some(device) = gpu() else {
        eprintln!("no GPU adapter on this machine: the sink is not checked");
        return;
    };
    let d = content();
    let area = Area::load(&d, &device, LandblockCoord { x: 0x00, y: 0x07 });
    const ROOT: u32 = 0x0007_0132;
    const ROOM: u32 = 0x0007_0144;
    let camera = camera(Vec3::new(60.0, -60.0, 1.6), 300.0);
    let reached = area.reached(ROOT, &camera);
    let room_views = reached
        .floods
        .iter()
        .flatten()
        .find(|(id, _)| *id == ROOM)
        .map(|(_, count)| *count);
    assert_eq!(room_views, Some(2), "room 0144 through two doorways");

    // A translucent server object standing in the room: its parts are all
    // blended.
    let room = area.near.cells.iter().find(|c| c.id == ROOM).unwrap();
    let blended_static = room
        .statics
        .iter()
        .find(|s| s.model_id == 0x0200_0AA3)
        .expect("the room's static with a blended part");
    let parts = area.models[&blended_static.model_id].clone().unwrap();
    // Beside the static (a drawing at the very same place would read as a
    // double draw).
    let world = Mat4::from_translation(Vec3::new(0.25, 0.0, 0.0)) * blended_static.transform;
    let object = ObjectDraw {
        parts: Cow::Owned(parts.clone()),
        world,
        lights: NO_LIGHTS,
        indoor: true,
        is_player: false,
        opacities: Some(vec![0.5; parts.len()]),
        guid: 0x8000_0001,
        local_id: 1,
        root: world,
        held: false,
        visible: true,
        render_owner: 1,
    };
    let objects = HashMap::from([(ROOM, vec![object])]);
    let frame = area.frame(ROOT, &camera, &objects);

    assert_each_drawn_once(&frame);
    // The room's mesh: one solid instance.
    let (solid, _) = count(&frame, cell_mesh_key(ROOM));
    assert_eq!(solid, 1, "the room is drawn once");
    // The static's blended part and the translucent object (all its
    // sub-meshes blended, "faded") are in the blended list, each once
    // (checked above).
    let (faded, blended): (Vec<_>, Vec<_>) = frame
        .alpha
        .iter()
        .filter_map(|a| match a {
            AlphaItem::Mesh(d) => Some(d.faded),
            _ => None,
        })
        .partition(|faded| *faded);
    assert!(!faded.is_empty(), "the translucent object is drawn");
    assert!(!blended.is_empty(), "the blended static is drawn");
    let distinct: HashSet<u32> = reached.floods.iter().flatten().map(|(id, _)| *id).collect();
    assert_eq!(frame.cells_drawn, distinct.len());
}

/// Holtburg's weapon shop seen from the west outside: its doorways open
/// several floods into the same rooms. Each room, and everything in it,
/// is drawn once.
#[test]
#[ignore = "needs the retail dat files and a GPU"]
fn rooms_behind_several_doorways_are_drawn_once() {
    let Some(device) = gpu() else {
        eprintln!("no GPU adapter on this machine: the sink is not checked");
        return;
    };
    let d = content();
    let area = Area::load(&d, &device, LandblockCoord { x: 0xA9, y: 0xB4 });
    const OUTSIDE: u32 = 0xA9B4_0001;
    let camera = camera(Vec3::new(69.09, 131.54, 67.8), 0.0);
    let reached = area.reached(OUTSIDE, &camera);
    assert!(
        reached.most_floods() >= 2,
        "a room in two floods: {:X?}",
        reached.floods
    );
    let frame = area.frame(OUTSIDE, &camera, &HashMap::new());
    assert_each_drawn_once(&frame);
    let distinct: HashSet<u32> = reached.floods.iter().flatten().map(|(id, _)| *id).collect();
    for id in &distinct {
        let (solid, _) = count(&frame, cell_mesh_key(*id));
        assert!(solid <= 1, "room {id:08X} drawn {solid} times");
    }
    assert_eq!(frame.cells_drawn, distinct.len());
    // Terrain once, the sky once before and once after the world.
    assert_terrain_drawn_once(&frame);
    assert_eq!(frame.sky, vec![0, 0]);
}

/// Each land cell's terrain is drawn exactly once in the frame.
fn assert_terrain_drawn_once(frame: &FrameDraws) {
    let mut drawn = 0u64;
    for draw in &frame.terrain_draws {
        assert_eq!(drawn & draw.cells, 0, "a land cell's terrain drawn twice");
        drawn |= draw.cells;
    }
    assert_eq!(drawn, ALL_LAND_CELLS, "every land cell's terrain is drawn");
}

/// Holtburg's weapon shop from the west outside: its doorways are punched
/// to far depth so its rooms can be drawn into them. The viewer's own land
/// cell, the nearest, has its terrain drawn after every punch, so terrain
/// in front of a doorway covers it again and what stands in the doorway
/// (a door) is not seen through the ground.
#[test]
#[ignore = "needs the retail dat files and a GPU"]
fn nearer_terrain_is_drawn_after_a_doorway_punch() {
    let Some(device) = gpu() else {
        eprintln!("no GPU adapter on this machine: the sink is not checked");
        return;
    };
    let d = content();
    let area = Area::load(&d, &device, LandblockCoord { x: 0xA9, y: 0xB4 });
    const OUTSIDE: u32 = 0xA9B4_0001;
    let eye = Vec3::new(69.09, 131.54, 67.8);
    let frame = area.frame(OUTSIDE, &camera(eye, 0.0), &HashMap::new());
    let last_punch = frame
        .steps
        .iter()
        .rposition(|s| matches!(s, Step::Portals(_)))
        .expect("the shop's doorways are punched");
    let viewer_cell = (eye.x / 24.0) as usize * 8 + (eye.y / 24.0) as usize;
    let bit = land_cell_bit(viewer_cell);
    let viewer_terrain = frame
        .steps
        .iter()
        .position(|s| match s {
            Step::Terrain(range) => frame.terrain_draws[range.clone()]
                .iter()
                .any(|t| t.cells & bit != 0),
            _ => false,
        })
        .expect("the viewer's land cell has terrain");
    assert!(
        viewer_terrain > last_punch,
        "the viewer's cell terrain (step {viewer_terrain}) precedes a punch (step {last_punch})"
    );
    assert_terrain_drawn_once(&frame);
}

/// From the weapon shop's ground floor the street is seen through several
/// windows and doorways (several outside views): the sky and the terrain
/// are drawn once, and each stage (the landscape with its look-ins, then
/// the shop after the depth clear) draws each thing once.
#[test]
#[ignore = "needs the retail dat files and a GPU"]
fn the_landscape_seen_through_several_openings_is_drawn_once() {
    let Some(device) = gpu() else {
        eprintln!("no GPU adapter on this machine: the sink is not checked");
        return;
    };
    let d = content();
    let area = Area::load(&d, &device, LandblockCoord { x: 0xA9, y: 0xB4 });
    const ROOT: u32 = 0xA9B4_0100;
    let camera = camera(Vec3::new(88.84, 131.54, 67.6), 150.0);
    let reached = area.reached(ROOT, &camera);
    assert!(
        reached.outside_views >= 2,
        "{} outside views",
        reached.outside_views
    );
    let frame = area.frame(ROOT, &camera, &HashMap::new());
    assert_each_drawn_once(&frame);
    assert_eq!(drawings_by_stage(&frame).len(), 2, "two stages");
    assert_terrain_drawn_once(&frame);
    assert_eq!(frame.sky, vec![0, 0]);
    let pre = frame
        .steps
        .iter()
        .filter(|s| matches!(s, Step::Sky { post: false, .. }))
        .count();
    assert_eq!(pre, 1);
}

/// The Facility Hub (landblock 0x8A02), from room 0x8A02015E at the pose
/// the stairs vanished at: the staircase (Setup 0x02000623) is listed in
/// the room above (0x8A02015F) and runs down into this one. Turning round,
/// it is drawn while the walk reaches only the lower room (it used to
/// vanish then), and each of its parts at most once a stage.
#[test]
#[ignore = "needs the retail dat files and a GPU"]
fn the_facility_hub_stairs_stay_drawn_as_the_camera_turns() {
    let Some(device) = gpu() else {
        eprintln!("no GPU adapter on this machine: the sink is not checked");
        return;
    };
    let d = content();
    let area = Area::load(&d, &device, LandblockCoord { x: 0x8A, y: 0x02 });
    const LOWER: u32 = 0x8A02_015E;
    const UPPER: u32 = 0x8A02_015F;
    const STAIR: u32 = 0x0200_0623;
    let upper = area.near.cells.iter().find(|c| c.id == UPPER).unwrap();
    let stair = upper
        .statics
        .iter()
        .find(|s| s.model_id == STAIR)
        .expect("the staircase is listed in the upper room");
    // Where each of the staircase's parts is drawn.
    let stair_parts: HashSet<(MeshKey, [u32; 16])> = area.models[&STAIR]
        .as_ref()
        .unwrap()
        .iter()
        .map(|p| {
            let world = stair.transform * p.transform;
            (
                u64::from(p.gfx_obj_id),
                world.to_cols_array().map(f32::to_bits),
            )
        })
        .collect();
    let eye = Vec3::new(60.251_43, -38.848_2, -5.995 + 1.6);
    let mut drawn_from_lower_only = 0;
    for heading in (0..360).step_by(10) {
        let camera = camera(eye, heading as f32);
        let reached = area.reached(LOWER, &camera);
        let cells: HashSet<u32> = reached.floods.iter().flatten().map(|(id, _)| *id).collect();
        assert!(cells.contains(&LOWER));
        let frame = area.frame(LOWER, &camera, &HashMap::new());
        assert_each_drawn_once(&frame);
        let stair_drawn = drawings_by_stage(&frame)
            .iter()
            .flat_map(|stage| stage.keys())
            .filter(|(mesh, model, _)| stair_parts.contains(&(*mesh, *model)))
            .count();
        if !cells.contains(&UPPER) && stair_drawn > 0 {
            drawn_from_lower_only += 1;
        }
        eprintln!(
            "heading {heading:3}: upper reached {}, stair parts drawn {stair_drawn}",
            cells.contains(&UPPER)
        );
    }
    assert!(
        drawn_from_lower_only > 0,
        "the staircase is never drawn from the lower room alone"
    );
}
