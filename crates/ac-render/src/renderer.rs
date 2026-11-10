//! The world renderer of an attached window: the landscape around the
//! player in two tiers, the near tier's statics, scenery, buildings and
//! their interiors, and server objects, seen through the chase camera.
//!
//! Built when a window attaches and dropped when it detaches; it holds only
//! what it can rebuild (GPU buffers and textures, its own copy of what the
//! loader prepared). Dat reading and meshing happen on the loader thread.
//! Each frame this takes finished items for a few milliseconds and uploads
//! them, follows the player with the streaming window, finds the viewer's
//! cell, walks the frame from it (portal visibility) and draws what the
//! walk reached.

mod atlas_upload;
pub(crate) mod atmosphere;
mod detail;
pub(crate) mod icon_probes;
mod particles;
pub(crate) mod pipelines;
mod portal;
mod shadows;
mod sink;
mod viewport;
mod world_lines;
pub(crate) mod world_pipelines;
mod world_triangles;

use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_audio::ambient::AmbientRegion;
use ac_core::lighting::{FLOATS_PER_LIGHT, SceneLightingUbo};
use ac_core::physics::PhysicsState;
use ac_core::sky::{SkyStateProvider, WeatherSystem, sun_direction};
use ac_core::world::LiveWorldOrigin;
use bytemuck::{Pod, Zeroable};
use glam::{Mat4, Quat, Vec3};
use wgpu::util::DeviceExt;

use detail::DetailGpu;
use particles::ParticleGpu;
use pipelines::{
    depth_view, msaa_color_view, sampler_entry, storage_buffer, storage_entry, texture_entry,
    uniform_entry, vertex_buffer,
};
use sink::{BuildingDraw, FrameSink, SinkInputs, SinkParticles, sphere_of};
use world_pipelines::{PipelineLayouts, WorldPipelines, usable_sample_count};

use crate::animation::PartPose;
use crate::animation::attachment::{AttachmentUpdateOrder, compose_child};
use crate::animation::pose::rest_poses;
use crate::animation::source::{AnimationSource, ContentAnimationSource};
use crate::animation::{AnimationPresenter, FiredHook, HookOwner, StaticKey};
use crate::appearance::AppearanceKey;
use crate::appearance_residency::FIRST_SURFACE_HANDLE;
use crate::assets::{
    GfxObjData, LandblockData, NearData, StaticInstance, SubMeshRange, SurfaceData,
};
use crate::camera::{
    CameraController, CameraMode, ChaseCollision, ChaseSubject, FlyInput, Lens, OrbitOverrides,
    PlayerModeFacts, yaw_of,
};
use crate::camera_collision::CameraCollision;
use crate::cells::{BuildingData, CellData};
use crate::content::ContentSlot;
use crate::degrade::{
    DegradeLevel, DegradeSettings, camera_facing_placement, has_humanoid_null_part_layout,
    select_building_level,
};
use crate::draws::{
    AlphaItem, ClipRegionGpu, FrameDraws, MeshKey, Step, StreamBuilder, cell_mesh_key,
};
use crate::effects::{EffectSound, EffectsFrame, OwnerPose, ServerEffect, WindowEffects};
use crate::environment::{EnvironmentFrame, SkyOptions, WindowEnvironment};
use crate::lights::{
    GPU_POINT_LIGHTS, LightSet, NO_LIGHTS, STATIC_STATE, StaticLightOwner, WindowLights,
    indoor_object_receives_torches,
};
use crate::loader::ModelInfo;
use crate::loader::{LoadRequest, Loaded, Loader};
use crate::meshing::{MeshRef, PLACEMENT_DEFAULT, Translucency, untextured_subset_draws};
use crate::reveal::{
    LoginReveal, RevealFacts, RevealWindow, apply_reveal_radius_override, neighborhood_loaded,
};
use crate::scene::Scene;
use crate::sky::{sky_draws, sky_projection, sky_view, weather_pass_open};
use crate::streaming::{StreamingRegion, Tier, TwoTierDiff, dungeon_gate};
use crate::translucency;
use crate::walk::{BuildingRef, FrameWalk, WalkBlock, WalkCamera};
use crate::world_frame::{LandblockCoord, OriginMove, follow_player, landblock_offset, to_frame};

fn loaded_cost(item: &Loaded) -> crate::work_budget::StreamingWorkCost {
    item.work_cost()
}

fn loaded_stage(item: &Loaded) -> &'static str {
    item.stage()
}

/// What releasing a landblock that left the window costs: one GPU
/// retirement (its buffers go together) and an entity operation per
/// placement it held, as adopting it was counted.
fn retirement_cost(r: &Resident) -> crate::work_budget::StreamingWorkCost {
    let placements = r.near.as_ref().map_or(0, |n| {
        n.statics.len()
            + n.buildings.len()
            + n.cells.iter().map(|c| 1 + c.statics.len()).sum::<usize>()
    });
    crate::work_budget::StreamingWorkCost {
        retire_operations: 1,
        entity_operations: i32::try_from(placements).unwrap_or(i32::MAX),
        ..crate::work_budget::StreamingWorkCost::default()
    }
}

/// Drop `value` on a short-lived thread of its own: freeing a structure of
/// thousands of allocations does not hold up the frame. Should no thread
/// start, it is dropped here.
fn release_in_background<T: Send + 'static>(value: T) {
    let _ = std::thread::Builder::new()
        .name("release".into())
        .spawn(move || drop(value));
}

/// How long a frame may spend building the camera's collision for server
/// objects new to its neighbourhood (the first is always built): the
/// neighbourhood that arrives at login holds the whole town's objects.
const CAMERA_OBJECT_BUILD_BUDGET: Duration = Duration::from_millis(1);

/// How often meshes, textures and models nothing uses any more are
/// released.
const SWEEP_INTERVAL: Duration = Duration::from_secs(5);

/// A model's bounding box (low and high corners, model-local), when known.
type Bounds = Option<(Vec3, Vec3)>;

/// The radius of the sphere the viewer's cell is found with, metres (the
/// chase camera's sweep sphere).
const VIEWER_SPHERE_RADIUS: f32 = crate::camera_collision::VIEWER_SPHERE_RADIUS;

/// Settings of the world renderer.
#[derive(Debug, Clone)]
pub struct WorldSettings {
    /// The process's dats and caches, shared by every window.
    pub content: Arc<ContentSlot>,
    /// Startup overrides of the quality preset (radii, anti-aliasing,
    /// filtering, streaming completions).
    pub quality_overrides: crate::quality::QualityOverrides,
    /// Budgets for meshes and textures nothing draws any more.
    pub residency: crate::unowned_cache::ResidencyBudgets,
    /// Where the orbit camera starts.
    pub orbit: OrbitOverrides,
    /// The reveal-radius diagnostic (`OPENAC_PROBE_REVEAL_RADIUS`).
    pub reveal_radius: Option<u32>,
    /// The pinned time of day and forced day group (launch options).
    pub sky: SkyOptions,
    /// The window's sound.
    pub audio: crate::audio::AudioOptions,
}

/// What the renderer reports about itself (for the window title and logs).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct WorldStats {
    /// Landblocks with terrain on the GPU.
    pub landblocks: usize,
    /// Landblocks drawn last frame (after culling).
    pub landblocks_drawn: usize,
    /// Objects (statics, scenery, buildings and server objects) drawn last
    /// frame.
    pub objects_drawn: usize,
    /// Interior cells drawn last frame.
    pub cells_drawn: usize,
    /// The cell the frame was walked from.
    pub viewer_cell: u32,
    /// GfxObj and cell meshes on the GPU.
    pub meshes: usize,
    /// Surface textures on the GPU.
    pub surfaces: usize,
    /// Server objects' looks held (appearances), as of the last sweep.
    pub appearances: usize,
    /// Their part variants, as of the last sweep.
    pub variants: usize,
    /// Recoloured surface textures on the GPU, as of the last sweep.
    pub recoloured_surfaces: usize,
    /// Their bytes.
    pub recoloured_surface_bytes: u64,
    /// CPU time of the last frame's preparation (loader uploads, the walk,
    /// instance lists), microseconds.
    pub prepare_micros: u64,
    /// Server objects the window animates.
    pub animated_objects: usize,
    /// Statics the window animates.
    pub animated_statics: usize,
    /// Live particle emitters.
    pub emitters: usize,
    /// Live particles.
    pub particles: usize,
    /// Particle draws (quads and model sub-meshes) last frame.
    pub particles_drawn: usize,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct Globals {
    view_proj: [[f32; 4]; 4],
    camera_pos: [f32; 4],
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct Material {
    alpha_cutoff: f32,
    opacity: f32,
    /// 1 when the surface is fogged (not additive), for the detail path.
    fog_enabled: f32,
    /// 1 when the surface blends.
    blended: f32,
}

/// The per-draw block of a sky sub-mesh (192 bytes, one 256-byte slot).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct SkyParamsGpu {
    model: [f32; 16],
    view_proj: [f32; 16],
    ambient_emissive: [f32; 4],
    sun_color_diffuse: [f32; 4],
    sun_dir_transparency: [f32; 4],
    scroll_fog_opacity: [f32; 4],
}

/// Distance between sky parameter blocks: the uniform offset alignment.
const SKY_PARAMS_STRIDE: usize = 256;

/// The world's instance buffer is made this many instances big (about
/// 7.6 MB) when the window opens. Growing it later costs a fresh GPU memory
/// block now and then, 8 to 13 ms in one frame, and the first frame of a
/// new area is where it grows: the first world frame after a teleport.
/// Twice the densest frame measured (a teleport destination of 16 to 32
/// thousand instances) is made up front; a denser frame still grows it.
const INITIAL_INSTANCE_CAPACITY: usize = 65_536;

/// Bytes of the scene-lighting block.
const SCENE_BYTES: u64 = SceneLightingUbo::SIZE_IN_BYTES as u64;

/// One sky sub-mesh template of this frame.
#[derive(Debug, Clone, Copy)]
struct SkyDraw {
    post_scene: bool,
    gfx_id: u32,
    sub_index: usize,
    surface: u32,
    repeat: bool,
    additive: bool,
    params: SkyParamsGpu,
}

struct TerrainTile {
    vertices: wgpu::Buffer,
    min_z: f32,
    max_z: f32,
    /// The ground under a point, as physics samples it.
    ground: Box<ac_physics::TerrainSurface>,
}

/// A mesh on the GPU and what drawing it needs to know.
pub(crate) struct GfxGpu {
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    sub_meshes: Vec<SubMeshRange>,
    /// Per sub-mesh: its surface blends (drawn sorted, after the opaque).
    pub(crate) sub_blended: Vec<bool>,
    /// Some sub-mesh is opaque or clip-mapped.
    pub(crate) has_opaque: bool,
    /// Some sub-mesh is clip-mapped (leaves, for the atmospheric wind).
    pub(crate) has_clip_map: bool,
    /// Its level-of-detail ladder.
    pub(crate) degrades: Vec<DegradeLevel>,
    /// An editor-only placement marker, never drawn.
    pub(crate) hidden_marker: bool,
    /// The point distance and sorting are measured to.
    pub(crate) sort_center: Vec3,
    /// Its box, model-local.
    pub(crate) bounds: Option<(Vec3, Vec3)>,
    /// The sphere a view admits it by, model-local.
    pub(crate) drawing_sphere: Option<(Vec3, f32)>,
}

impl GfxGpu {
    /// A mesh in `vertices` and `indices` with what the frame reads of
    /// `gfx`; `translucency` gives a surface's blending (`None` while the
    /// surface is not on the GPU: drawn as solid).
    fn new(
        vertices: wgpu::Buffer,
        indices: wgpu::Buffer,
        gfx: &GfxObjData,
        translucency: impl Fn(u32) -> Option<Translucency>,
    ) -> Self {
        let sub_blended: Vec<bool> = gfx
            .sub_meshes
            .iter()
            .map(|sub| translucency(sub.surface_id).is_some_and(is_blended))
            .collect();
        let has_opaque = sub_blended.iter().any(|b| !b);
        let has_clip_map = gfx
            .sub_meshes
            .iter()
            .any(|sub| translucency(sub.surface_id) == Some(Translucency::ClipMap));
        Self {
            vertices,
            indices,
            sub_meshes: gfx.sub_meshes.clone(),
            sub_blended,
            has_opaque: has_opaque && !gfx.indices.is_empty(),
            has_clip_map,
            hidden_marker: crate::degrade::is_runtime_hidden_marker(&gfx.degrades),
            degrades: gfx.degrades.clone(),
            sort_center: gfx.sort_center,
            bounds: crate::assets::gfx_bounds(gfx),
            drawing_sphere: gfx.drawing_sphere,
        }
    }
}

/// Anisotropic filtering of world textures that have mips (OpenAC's world
/// texture arrays use 16 regardless of the quality preset).
const WORLD_TEXTURE_ANISOTROPY: u16 = 16;

struct SurfaceGpu {
    clamp: wgpu::BindGroup,
    repeat: wgpu::BindGroup,
    translucency: Translucency,
    untextured: bool,
    opacity: f32,
    luminosity: f32,
    diffuse: f32,
    disable_fog: bool,
    /// Its texture's bytes (for the unowned budget).
    bytes: u64,
}

/// A landblock's near-tier content, kept on the CPU.
pub(crate) struct NearResident {
    /// Statics (not shells) and scenery, landblock-local.
    pub(crate) statics: Vec<StaticInstance>,
    /// Indices into `statics` per land cell (`x * 8 + y`).
    pub(crate) by_cell: Vec<Vec<usize>>,
    /// Buildings (their walk matrices in the drawing frame).
    buildings: Vec<BuildingData>,
    /// Interior cells (their meshes are on the GPU).
    cells: Vec<CellData>,
    /// Every model it places.
    model_ids: Vec<u32>,
    /// Shells and statics as placed (for their lights).
    light_statics: Vec<StaticInstance>,
    /// Where scenery starts in `statics` (the landblock's own statics come
    /// first).
    pub(crate) scenery_start: usize,
}

/// One resident landblock.
struct Resident {
    tier: Tier,
    tile: Option<TerrainTile>,
    /// The cell dat has no such landblock.
    missing: bool,
    near: Option<NearResident>,
    /// Near content was asked for and has not arrived.
    near_requested: bool,
}

/// Where an interior cell's data is.
pub(crate) struct CellRef<'a> {
    coord: LandblockCoord,
    data: &'a CellData,
}

/// A server object as this frame draws it.
pub(crate) struct ObjectDraw<'a> {
    parts: std::borrow::Cow<'a, [MeshRef]>,
    world: Mat4,
    lights: LightSet,
    indoor: bool,
    is_player: bool,
    /// Each part's opacity when the object or a part fade makes some
    /// translucent (else every part is solid).
    opacities: Option<Vec<f32>>,
    /// The object's id.
    guid: u32,
    /// Its incarnation (local id).
    local_id: u32,
    /// Its root: position and turn, no scale (a held object's own).
    root: Mat4,
    /// A held object (drawn on its parent).
    held: bool,
    /// Whether the world draws it: neither it nor what holds it is hidden
    /// or flagged not to draw ([`draws_by_state`]).
    visible: bool,
    /// The top-level object (by local id) whose cells it is drawn in: its
    /// own, or for a held object the one at the top of what holds it.
    render_owner: u32,
}

/// Where each server object is drawn: for every cell, the objects drawn in
/// it as (the cell `objects` files it under, its index there), in the
/// order `objects` lists them. An object is drawn in every cell its drawn
/// parts reach, as its owner's registration flooded them
/// (`render_cells`); one without rows is drawn in the cell it stands in.
pub(crate) fn file_object_draws<'c>(
    objects: &HashMap<u32, Vec<ObjectDraw<'_>>>,
    render_cells: impl Fn(u32) -> Option<&'c [u32]>,
) -> HashMap<u32, Vec<(u32, usize)>> {
    let mut homes: Vec<u32> = objects.keys().copied().collect();
    homes.sort_unstable();
    let mut out: HashMap<u32, Vec<(u32, usize)>> = HashMap::new();
    for home in homes {
        for (index, draw) in objects[&home].iter().enumerate() {
            let own = [home];
            let cells: &[u32] = match render_cells(draw.render_owner) {
                Some(cells) => cells,
                None => &own,
            };
            for &cell in cells {
                out.entry(cell).or_default().push((home, index));
            }
        }
    }
    out
}

/// Whether an object's own physics state lets the world draw it: neither
/// hidden (the server hides a character for a teleport until it is placed
/// again) nor flagged not to draw. Its pose is still kept, so its emitters
/// and scripts go on where it stands.
fn draws_by_state(state: u32) -> bool {
    state & (PhysicsState::HIDDEN.bits() | PhysicsState::NO_DRAW.bits()) == 0
}

/// Whether a held object is drawn: only while its holder is drawn, and by
/// its own state.
fn held_draws(own_state: u32, holder_draws: bool) -> bool {
    holder_draws && draws_by_state(own_state)
}

/// The renderer. See the module documentation.
pub(crate) struct WorldRenderer {
    /// The texture detail the world's textures are made at.
    texture_detail: crate::texture_detail::WorldTextureDetail,
    /// The particle range setting's multiplier of every emitter's authored
    /// degrade distance.
    particle_range: f32,
    loader: Loader,
    failure: Option<String>,
    near_radius: u32,
    far_radius: u32,

    depth: wgpu::TextureView,
    /// The multisampled colour target (`None` without anti-aliasing).
    msaa_color: Option<wgpu::TextureView>,
    size: (u32, u32),
    color_format: wgpu::TextureFormat,
    /// Samples per pixel the world is drawn with (1: none).
    samples: u32,
    /// The sample counts the adapter supports for the colour and depth
    /// formats.
    msaa_supported: Vec<u32>,
    alpha_to_coverage: bool,
    /// The terrain's anisotropic filtering (1: none).
    anisotropic_level: u16,
    /// The terrain atlas's views and tiling table, kept so its sampler can
    /// change.
    terrain_atlas: Option<(wgpu::TextureView, wgpu::TextureView, wgpu::Buffer)>,
    /// A region's atlas being written a piece a frame; the one in use
    /// stays until it is done.
    atlas_upload: Option<atlas_upload::AtlasUpload>,
    /// The streaming work budget's profile, scaled by the preset's
    /// completions per frame.
    work_budget: crate::work_budget::StreamingWorkBudgetOptions,
    /// A finished load that did not fit the last frame's budget.
    held_loaded: Option<Loaded>,
    /// The startup overrides the quality is resolved with.
    quality_overrides: crate::quality::QualityOverrides,
    /// The quality the window runs with.
    quality: crate::quality::QualitySettings,
    /// Every world pipeline for the current sample count.
    pipelines: WorldPipelines,
    detail_layout: wgpu::BindGroupLayout,
    /// The retail detail textures of building shells and interior cells.
    detail: DetailGpu,

    globals: wgpu::Buffer,
    /// The scene-lighting block (sun, ambient, fog, flash, camera, time).
    scene_lighting: wgpu::Buffer,
    /// The point lights of this frame's snapshot.
    point_lights: wgpu::Buffer,
    /// The frame's clip regions.
    clips: wgpu::Buffer,
    clips_capacity: usize,
    globals_layout: wgpu::BindGroupLayout,
    globals_group: wgpu::BindGroup,

    /// The sky's per-draw blocks and the bind group reading them.
    sky_params: wgpu::Buffer,
    sky_params_capacity: usize,
    sky_layout: wgpu::BindGroupLayout,
    sky_group: wgpu::BindGroup,
    /// This frame's sky sub-mesh templates.
    sky_templates: Vec<SkyDraw>,
    /// The region's sky and weather on the session's clock, once the region
    /// has arrived.
    environment: Option<WindowEnvironment>,
    /// A fog override the server set before the region's sky arrived,
    /// applied when it does.
    pending_fog: Option<u32>,
    sky_options: SkyOptions,
    /// Every model the region's sky can show (never released).
    sky_models: HashSet<u32>,
    /// The region's ambient sounds, until the window's audio takes them.
    ambient_region: Option<AmbientRegion>,
    lights: WindowLights,
    /// Bounds and lights of each model.
    model_info: crate::IdMap<u32, ModelInfo>,
    /// The player's cell and whether it is sealed (no view outside).
    player_cell_sealed: Option<(u32, bool)>,
    /// The frame's clear colour (the fog colour once the sky is known).
    clear: Option<wgpu::Color>,
    /// The camera this frame: eye and look direction.
    camera: Option<(Vec3, Vec3)>,
    /// The camera's view and projection this frame (the overlays project
    /// with them).
    view_matrices: Option<(glam::Mat4, glam::Mat4)>,
    content: Arc<ContentSlot>,

    terrain_layout: wgpu::BindGroupLayout,
    terrain_group: Option<wgpu::BindGroup>,

    /// The streaming window (once the player's position is known).
    region: Option<StreamingRegion>,
    /// Inside a sealed dungeon: the landblock streaming collapsed onto.
    dungeon: Option<u32>,
    residents: HashMap<LandblockCoord, Resident>,
    /// Landblocks that left the window, released a few per frame.
    retiring: crate::retirement::RetirementQueue<Resident>,
    /// The frame walk and the cells it can reach.
    walk: FrameWalk,
    /// Interior cells and buildings for finding the viewer's cell when the
    /// eye is not swept (the fly and orbit cameras).
    collision: ac_physics::CollisionWorld,
    /// What the chase camera's eye collides with.
    camera_collision: CameraCollision,
    /// Messages for the owner since the last call.
    notices: Vec<String>,
    /// The cell the last frame was walked from.
    viewer_cell: u32,
    portals_drawn_count: u32,
    degrade: DegradeSettings,
    /// The frame rate and the automatic level-of-detail multiplier.
    degrade_controller: crate::degrade_controller::BuildingDegradeController,
    /// "Automatic degrades" (else the "graphics performance" slider is the
    /// multiplier).
    automatic_degrades: bool,
    graphics_performance: f32,
    /// UI Only: the world pass is off, nothing of it is built or picked,
    /// and nothing unowned is kept.
    ui_only: bool,

    /// The window icon, until the window takes it.
    icon: Option<Arc<ac_dat::texture::DecodedTexture>>,
    /// The login reveal (the world is withheld until the area has loaded).
    reveal: LoginReveal,
    reveal_started: Option<Instant>,
    revealed_after: Option<Duration>,
    /// Every server object's model has arrived.
    server_models_ready: bool,
    /// Server objects (guid, setup) whose models hold back the reveal.
    server_models_missing: Vec<(u32, u32)>,
    reveal_radius_override: Option<u32>,
    terrain_instances: wgpu::Buffer,
    terrain_instances_capacity: usize,
    portal_vertices: wgpu::Buffer,
    portal_vertices_capacity: usize,
    /// The plugins' world lines as this frame draws them.
    world_lines: world_lines::WorldLinesGpu,
    world_triangles: world_triangles::WorldTrianglesGpu,
    icon_probes: icon_probes::IconProbes,

    models: crate::IdMap<u32, Option<Vec<MeshRef>>>,
    requested_models: crate::model_requests::ModelRequests,
    /// Server objects' models drawn with their appearance, their part
    /// variants and which recoloured surfaces they own.
    looks: crate::appearance_residency::HeldLooks,
    meshes: crate::IdMap<MeshKey, GfxGpu>,
    /// GfxObjs and surfaces asked for again after a release.
    requested_gfx: HashSet<u32>,
    requested_surfaces: HashSet<u32>,
    last_sweep: Instant,
    /// Meshes and textures nothing draws, kept within their budgets.
    unowned_meshes: crate::unowned_cache::BoundedUnownedResourceCache<u32>,
    unowned_surfaces: crate::unowned_cache::BoundedUnownedResourceCache<u32>,
    surfaces: HashMap<u32, SurfaceGpu>,
    material_layout: wgpu::BindGroupLayout,
    clamp_sampler: wgpu::Sampler,
    repeat_sampler: wgpu::Sampler,
    /// The samplers of world textures with mips (16 x anisotropic).
    world_clamp_sampler: wgpu::Sampler,
    world_repeat_sampler: wgpu::Sampler,
    instances: wgpu::Buffer,
    instances_capacity: usize,

    origin: LiveWorldOrigin,
    lens: Lens,
    cameras: CameraController,
    last_update: Option<Instant>,
    stats: WorldStats,
    /// Draw stream built by `prepare`, consumed by `draw`.
    frame: FrameDraws,
    /// The room earlier frames' draws took, for the next frame's.
    draw_scratch: crate::draws::DrawScratch,
    /// Part poses of the animated objects and statics.
    animations: AnimationPresenter,
    /// Setups, motion tables and animations for them (once the dats are
    /// open).
    animation_source: Option<ContentAnimationSource>,
    /// Particles, physics scripts and what starts them (once the dats are
    /// open).
    effects: Option<WindowEffects>,
    /// The particle pipelines and buffers.
    particle_gpu: ParticleGpu,
    /// Sounds the effects asked for, until the window's audio takes them.
    effect_sounds: Vec<EffectSound>,
    /// Server effects that arrived before the effects could run.
    pending_server_effects: Vec<ServerEffect>,
    /// Portal space: what the window shows while the player is in it.
    portal: crate::portal_space::PortalSpace,
    /// The portal tunnel, once its assets are looked for.
    tunnel: portal::TunnelAssets,
    /// A teleport's placement puts the chase camera's eye back on the
    /// player.
    viewer_reseat: portal::ViewerReseat,
    /// The tunnel's Setup (its models are never released).
    tunnel_setup: Option<u32>,
    /// Portal space's sounds and wait lines, until the window takes them.
    portal_cues: Vec<crate::portal_space::PortalCue>,
    /// Where each held object hung when last drawn (root, cell), for its
    /// lights.
    held_roots: HashMap<u32, (Mat4, u32)>,
    /// The UI's 3-D viewports (`crate::viewport`).
    viewports: viewport::Viewports,
    /// What the world frame drew, for the world pick, and the selection
    /// flash.
    selection: crate::selection::SelectionScene,
    /// The pick shapes read so far.
    selection_geometry: crate::selection::DatSelectionGeometry,
    /// The flash's clock.
    selection_epoch: Instant,
    /// The atmospheric rendering's GPU side, while the option is on.
    atmosphere: Option<atmosphere::AtmosphereGpu>,
    /// The atmospheric rendering to build at the next frame.
    atmosphere_wanted: Option<crate::atmosphere::AtmosphereConfig>,
    /// The selection last applied.
    atmosphere_selection: Option<ac_host::settings::RenderPackSelection>,
    /// Automatic quality turned it off for this selection.
    atmosphere_failed: bool,
    /// The sample counts the HDR world target supports.
    hdr_msaa_supported: Vec<u32>,
}

impl WorldRenderer {
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        color_format: wgpu::TextureFormat,
        width: u32,
        height: u32,
        settings: &WorldSettings,
        display: &ac_host::settings::DisplaySettings,
        msaa_supported: &[u32],
    ) -> Self {
        let uniform = |label: &str, size: u64| {
            device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        };
        let globals = uniform("globals", size_of::<Globals>() as u64);
        let scene_lighting = uniform("scene lighting", SCENE_BYTES);
        let point_lights = uniform(
            "point lights",
            (GPU_POINT_LIGHTS * FLOATS_PER_LIGHT * 4) as u64,
        );
        let clips_capacity = 64;
        let clips = storage_buffer(
            device,
            "clip regions",
            clips_capacity * size_of::<ClipRegionGpu>(),
        );
        let both = wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT;
        let globals_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("globals"),
            entries: &[
                uniform_entry(0, both),
                uniform_entry(1, both),
                uniform_entry(2, both),
                storage_entry(3),
            ],
        });
        let globals_group = globals_group(
            device,
            &globals_layout,
            &globals,
            &scene_lighting,
            &point_lights,
            &clips,
        );
        let sky_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("sky"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: both,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: true,
                        min_binding_size: wgpu::BufferSize::new(size_of::<SkyParamsGpu>() as u64),
                    },
                    count: None,
                },
                uniform_entry(1, both),
                storage_entry(2),
            ],
        });
        let sky_params_capacity = 64;
        let sky_params = uniform(
            "sky params",
            (sky_params_capacity * SKY_PARAMS_STRIDE) as u64,
        );
        let sky_group = sky_group(device, &sky_layout, &sky_params, &scene_lighting, &clips);

        let terrain_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("terrain"),
            entries: &[
                texture_entry(0, wgpu::TextureViewDimension::D2Array),
                sampler_entry(1),
                texture_entry(2, wgpu::TextureViewDimension::D2Array),
                sampler_entry(3),
                uniform_entry(4, wgpu::ShaderStages::FRAGMENT),
                // The landscape detail texture, its sampler and the detail
                // tilings.
                texture_entry(5, wgpu::TextureViewDimension::D2),
                sampler_entry(6),
                uniform_entry(7, wgpu::ShaderStages::FRAGMENT),
            ],
        });
        let material_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("material"),
            entries: &[
                texture_entry(0, wgpu::TextureViewDimension::D2),
                sampler_entry(1),
                uniform_entry(2, wgpu::ShaderStages::FRAGMENT),
            ],
        });
        let detail_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("detail"),
            entries: &[
                texture_entry(0, wgpu::TextureViewDimension::D2),
                texture_entry(1, wgpu::TextureViewDimension::D2),
                sampler_entry(2),
                uniform_entry(3, wgpu::ShaderStages::FRAGMENT),
            ],
        });
        let detail = DetailGpu::new(
            device,
            queue,
            &detail_layout,
            detail::DetailOptions {
                building: display.building_detail_textures,
                landscape: display.landscape_detail_textures,
            },
        );
        let quality = crate::quality::resolve(display, &settings.quality_overrides);
        let samples = usable_sample_count(quality.msaa_samples, msaa_supported);
        let pipelines = WorldPipelines::build(
            device,
            color_format,
            samples,
            quality.alpha_to_coverage,
            &PipelineLayouts {
                globals: &globals_layout,
                terrain: &terrain_layout,
                material: &material_layout,
                sky: &sky_layout,
                detail: &detail_layout,
            },
        );

        let sampler = |mode: wgpu::AddressMode, anisotropy_clamp: u16, label: &str| {
            device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some(label),
                address_mode_u: mode,
                address_mode_v: mode,
                address_mode_w: mode,
                mag_filter: wgpu::FilterMode::Linear,
                min_filter: wgpu::FilterMode::Linear,
                mipmap_filter: wgpu::MipmapFilterMode::Linear,
                anisotropy_clamp,
                ..Default::default()
            })
        };
        let clamp_sampler = sampler(wgpu::AddressMode::ClampToEdge, 1, "surface clamp");
        let repeat_sampler = sampler(wgpu::AddressMode::Repeat, 1, "surface repeat");
        // World textures with a mip chain sample as OpenAC's world texture
        // arrays do: trilinear, 16 x anisotropic.
        let world_clamp_sampler = sampler(
            wgpu::AddressMode::ClampToEdge,
            WORLD_TEXTURE_ANISOTROPY,
            "world surface clamp",
        );
        let world_repeat_sampler = sampler(
            wgpu::AddressMode::Repeat,
            WORLD_TEXTURE_ANISOTROPY,
            "world surface repeat",
        );

        let particle_gpu = ParticleGpu::new(
            device,
            queue,
            color_format,
            samples,
            &globals_layout,
            &material_layout,
            &clamp_sampler,
        );
        let terrain_instances_capacity = 256;
        let portal_vertices_capacity = 256;
        let instances_capacity = INITIAL_INSTANCE_CAPACITY;
        let (near_radius, far_radius) = quality.radii();
        let work_budget = crate::work_budget::StreamingWorkBudgetOptions::default()
            .scale_for_legacy_completion_count(
                i32::try_from(quality.max_completions_per_frame.max(1)).unwrap_or(i32::MAX),
            )
            .unwrap_or_default();
        // WebGPU guarantees four samples for half-float targets; the window
        // tells the adapter's own counts (`set_hdr_sample_counts`).
        let hdr_msaa_supported = msaa_supported.iter().copied().filter(|&n| n == 4).collect();
        let texture_detail = crate::texture_detail::WorldTextureDetail::from_display(display);
        let mut renderer = Self {
            texture_detail,
            particle_range: crate::quality::particle_range_multiplier(display.particle_range),
            loader: Loader::spawn(
                Arc::clone(&settings.content),
                texture_detail,
                device
                    .features()
                    .contains(wgpu::Features::TEXTURE_COMPRESSION_BC),
            ),
            failure: None,
            near_radius,
            far_radius,
            depth: depth_view(device, width, height, samples),
            msaa_color: msaa_color_view(device, color_format, width, height, samples),
            size: (width, height),
            color_format,
            samples,
            msaa_supported: msaa_supported.to_vec(),
            alpha_to_coverage: quality.alpha_to_coverage,
            anisotropic_level: quality.anisotropic_level,
            terrain_atlas: None,
            atlas_upload: None,
            work_budget,
            held_loaded: None,
            quality_overrides: settings.quality_overrides,
            quality,
            pipelines,
            detail_layout,
            detail,
            globals,
            scene_lighting,
            point_lights,
            clips,
            clips_capacity,
            globals_layout,
            globals_group,
            sky_params,
            sky_params_capacity,
            sky_layout,
            sky_group,
            sky_templates: Vec::new(),
            environment: None,
            pending_fog: None,
            sky_options: settings.sky,
            sky_models: HashSet::new(),
            ambient_region: None,
            lights: WindowLights::new(),
            model_info: crate::IdMap::default(),
            player_cell_sealed: None,
            clear: None,
            camera: None,
            view_matrices: None,
            content: Arc::clone(&settings.content),
            terrain_layout,
            terrain_group: None,
            region: None,
            dungeon: None,
            residents: HashMap::new(),
            retiring: crate::retirement::RetirementQueue::default(),
            walk: FrameWalk::new(),
            collision: ac_physics::CollisionWorld::new(),
            camera_collision: CameraCollision::new(),
            notices: Vec::new(),
            viewer_cell: 0,
            portals_drawn_count: 0,
            degrade: DegradeSettings {
                degrade_distance: display.degrade_distance,
                multiplier: 0.0,
                keep_distant_buildings: display.keep_distant_buildings,
                degrades_disabled: false,
            },
            degrade_controller: Default::default(),
            automatic_degrades: display.automatic_degrades,
            graphics_performance: display.graphics_performance,
            ui_only: display.ui_only,
            icon: None,
            reveal: LoginReveal::default(),
            reveal_started: None,
            revealed_after: None,
            server_models_ready: true,
            server_models_missing: Vec::new(),
            reveal_radius_override: settings.reveal_radius,
            terrain_instances: vertex_buffer(
                device,
                "terrain instances",
                terrain_instances_capacity * size_of::<crate::draws::TerrainInstanceGpu>(),
            ),
            terrain_instances_capacity,
            portal_vertices: vertex_buffer(
                device,
                "portal vertices",
                portal_vertices_capacity * size_of::<crate::draws::PortalVertexGpu>(),
            ),
            portal_vertices_capacity,
            world_lines: world_lines::WorldLinesGpu::default(),
            world_triangles: world_triangles::WorldTrianglesGpu::default(),
            icon_probes: icon_probes::IconProbes::default(),
            models: crate::IdMap::default(),
            requested_models: crate::model_requests::ModelRequests::default(),
            looks: crate::appearance_residency::HeldLooks::default(),
            meshes: crate::IdMap::default(),
            requested_gfx: HashSet::new(),
            requested_surfaces: HashSet::new(),
            last_sweep: Instant::now(),
            unowned_meshes: crate::unowned_cache::BoundedUnownedResourceCache::new(
                settings.residency.object_mesh_bytes,
                settings.residency.object_mesh_entries,
            ),
            unowned_surfaces: crate::unowned_cache::BoundedUnownedResourceCache::new(
                settings.residency.standalone_bytes,
                settings.residency.standalone_entries,
            ),
            surfaces: HashMap::new(),
            material_layout,
            clamp_sampler,
            repeat_sampler,
            world_clamp_sampler,
            world_repeat_sampler,
            instances: vertex_buffer(
                device,
                "instances",
                instances_capacity * size_of::<crate::draws::InstanceGpu>(),
            ),
            instances_capacity,
            origin: LiveWorldOrigin::new(),
            lens: {
                let mut lens = Lens::default();
                if width > 0 && height > 0 {
                    lens.set_aspect(width as f32 / height as f32);
                }
                lens
            },
            cameras: CameraController::new(settings.orbit.orbit()),
            last_update: None,
            stats: WorldStats::default(),
            frame: FrameDraws::new(),
            draw_scratch: crate::draws::DrawScratch::default(),
            animations: AnimationPresenter::new(),
            animation_source: None,
            effects: None,
            particle_gpu,
            effect_sounds: Vec::new(),
            pending_server_effects: Vec::new(),
            portal: crate::portal_space::PortalSpace::new(),
            tunnel: portal::TunnelAssets::default(),
            viewer_reseat: portal::ViewerReseat::default(),
            tunnel_setup: None,
            portal_cues: Vec::new(),
            held_roots: HashMap::new(),
            viewports: viewport::Viewports::new(color_format),
            selection: crate::selection::SelectionScene::new(),
            selection_geometry: crate::selection::DatSelectionGeometry::default(),
            selection_epoch: Instant::now(),
            atmosphere: None,
            atmosphere_wanted: None,
            atmosphere_selection: None,
            atmosphere_failed: false,
            hdr_msaa_supported,
        };
        renderer.apply_atmosphere_display(device, display);
        renderer
    }

    /// The effects, made when the dats are open.
    fn ensure_effects(&mut self) -> bool {
        if self.effects.is_none()
            && let Some(content) = self.content.get_if_open()
        {
            // Unseeded in the game; any seed will do.
            let seed = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.subsec_nanos() as i32);
            let mut effects = WindowEffects::new(content, seed);
            for effect in self.pending_server_effects.drain(..) {
                effects.queue_server(effect);
            }
            self.effects = Some(effects);
        }
        self.effects.is_some()
    }

    /// The motion states objects took up since the last frame, in order,
    /// for the next frame's animations.
    pub(crate) fn queue_motions(&mut self, motions: Vec<ac_runtime::world::AppliedMotion>) {
        self.animations.queue_motions(motions);
    }

    /// Turn the motion log (`OPENAC_MOTION_LOG`) on or off.
    pub(crate) fn set_motion_log(&mut self, on: bool) {
        self.animations.set_motion_log(on);
    }

    /// A server effect message for the next frame.
    pub(crate) fn queue_server_effect(&mut self, effect: ServerEffect) {
        // Before the dats are open (the first frames) messages wait; a
        // window whose dats never open drops them past a bound.
        const PENDING_LIMIT: usize = 4096;
        match self.effects.as_mut() {
            Some(effects) => effects.queue_server(effect),
            None if self.pending_server_effects.len() < PENDING_LIMIT => {
                self.pending_server_effects.push(effect);
            }
            None => {}
        }
    }

    /// Take the sounds the effects asked for.
    pub(crate) fn take_effect_sounds(&mut self) -> Vec<EffectSound> {
        std::mem::take(&mut self.effect_sounds)
    }

    /// The window icon, once, when the loader has read it.
    pub(crate) fn take_icon(&mut self) -> Option<Arc<ac_dat::texture::DecodedTexture>> {
        self.icon.take()
    }

    /// Messages for the owner since the last call.
    pub(crate) fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }

    /// Why the loader stopped, if it did.
    pub(crate) fn failure(&self) -> Option<&str> {
        self.failure.as_deref()
    }

    /// The level-of-detail owner as of the last frame: the frame rate it
    /// measured and the degrade multiplier in force (the frame-rate
    /// display's two lines, OpenAC `BuildingDegradeController.Fps` and
    /// `ActiveMultiplier`).
    pub(crate) fn level_of_detail(&self) -> (f32, f32) {
        (self.degrade_controller.fps(), self.degrade.multiplier)
    }

    /// The root pose last published for the live object `guid` (a held
    /// object's hangs from its holder's hand), drawing frame.
    pub(crate) fn published_root_pose(&self, guid: u32) -> Option<Mat4> {
        self.effects.as_ref()?.published_root_pose(guid)
    }

    /// The live object `guid` stands in the resident world (its landblock
    /// is loaded).
    pub(crate) fn is_live_resident(&self, guid: u32) -> bool {
        self.effects
            .as_ref()
            .is_some_and(|e| e.is_live_resident(guid))
    }

    pub(crate) fn stats(&self) -> WorldStats {
        self.stats
    }

    /// Objects the loaded landblocks place: statics, scenery, buildings
    /// and the statics of their interior cells (server objects not
    /// included).
    pub(crate) fn loaded_objects(&self) -> usize {
        self.residents
            .values()
            .filter_map(|r| r.near.as_ref())
            .map(|n| {
                n.statics.len()
                    + n.buildings.len()
                    + n.cells.iter().map(|c| c.statics.len()).sum::<usize>()
            })
            .sum()
    }

    /// Follow a new window size.
    pub(crate) fn resize(&mut self, device: &wgpu::Device, width: u32, height: u32) {
        if self.size == (width, height) || width == 0 || height == 0 {
            return;
        }
        self.depth = depth_view(device, width, height, self.samples);
        self.msaa_color = msaa_color_view(device, self.color_format, width, height, self.samples);
        self.size = (width, height);
        self.lens.set_aspect(width as f32 / height as f32);
    }

    /// Run with the quality `display` resolves to (the effective display
    /// settings): the streaming window, the work budget, anti-aliasing, the
    /// terrain's filtering, the particle range and the texture detail
    /// follow at once (a new texture detail has the loader make the terrain
    /// atlas and every world texture again, which replace the old ones as
    /// they arrive).
    pub(crate) fn apply_display(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        display: &ac_host::settings::DisplaySettings,
    ) {
        let texture_detail = crate::texture_detail::WorldTextureDetail::from_display(display);
        if texture_detail != self.texture_detail {
            self.texture_detail = texture_detail;
            self.loader
                .request(LoadRequest::TextureDetail(texture_detail));
        }
        self.particle_range = crate::quality::particle_range_multiplier(display.particle_range);
        self.degrade.degrade_distance = display.degrade_distance;
        self.detail.set_options(
            queue,
            detail::DetailOptions {
                building: display.building_detail_textures,
                landscape: display.landscape_detail_textures,
            },
        );
        self.degrade.keep_distant_buildings = display.keep_distant_buildings;
        self.automatic_degrades = display.automatic_degrades;
        self.graphics_performance = display.graphics_performance;
        self.ui_only = display.ui_only;
        let quality = crate::quality::resolve(display, &self.quality_overrides);
        if quality == self.quality {
            self.apply_atmosphere_display(device, display);
            return;
        }
        let (near, far) = quality.radii();
        if (near, far) != (self.near_radius, self.far_radius) {
            self.near_radius = near;
            self.far_radius = far;
            if let Some(region) = self.region.as_mut() {
                let diff = region.reconfigure_radii(near, far);
                self.apply_diff(diff);
            }
        }
        if quality.max_completions_per_frame != self.quality.max_completions_per_frame {
            self.work_budget = crate::work_budget::StreamingWorkBudgetOptions::default()
                .scale_for_legacy_completion_count(
                    i32::try_from(quality.max_completions_per_frame.max(1)).unwrap_or(i32::MAX),
                )
                .unwrap_or_default();
        }
        let samples = usable_sample_count(quality.msaa_samples, &self.msaa_supported);
        if samples != self.samples || quality.alpha_to_coverage != self.alpha_to_coverage {
            self.samples = samples;
            self.alpha_to_coverage = quality.alpha_to_coverage;
            self.pipelines = WorldPipelines::build(
                device,
                self.color_format,
                samples,
                quality.alpha_to_coverage,
                &PipelineLayouts {
                    globals: &self.globals_layout,
                    terrain: &self.terrain_layout,
                    material: &self.material_layout,
                    sky: &self.sky_layout,
                    detail: &self.detail_layout,
                },
            );
            self.particle_gpu.rebuild_pipelines(
                device,
                self.color_format,
                samples,
                &self.globals_layout,
                &self.material_layout,
            );
            let (width, height) = self.size;
            self.depth = depth_view(device, width, height, samples);
            self.msaa_color = msaa_color_view(device, self.color_format, width, height, samples);
        }
        if quality.anisotropic_level != self.anisotropic_level {
            self.anisotropic_level = quality.anisotropic_level;
            self.rebuild_terrain_group(device);
        }
        self.quality = quality;
        self.apply_atmosphere_display(device, display);
    }

    /// Use the player's field of view (radians); a value the retail rule
    /// refuses for this aspect keeps the previous one.
    pub(crate) fn set_game_fov(&mut self, radians: f32) {
        self.lens.set_game_fov(radians);
    }

    /// Zoom the chase camera: positive moves out.
    pub(crate) fn zoom(&mut self, steps: f32) {
        self.cameras.zoom(steps);
    }

    /// The cameras, for the window's camera keys and mouse.
    pub(crate) fn cameras_mut(&mut self) -> &mut CameraController {
        &mut self.cameras
    }

    /// Take loader results, follow the player, move the fly camera with
    /// `fly`, walk the frame and build its draw stream and uniforms.
    pub(crate) fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: Option<&Scene>,
        fly: FlyInput,
    ) {
        let started = Instant::now();
        // The plugins' lines and triangles are set again for this frame's
        // camera, after the camera has moved.
        self.world_lines.clear();
        self.world_triangles.clear();
        self.pump_loader(device, queue);
        self.ensure_effects();

        let now = Instant::now();
        let dt = self
            .last_update
            .map_or(0.0, |t| now.duration_since(t).as_secs_f32());
        self.last_update = Some(now);
        self.degrade_controller
            .tick(f64::from(dt), self.automatic_degrades);
        self.degrade.multiplier = self
            .degrade_controller
            .active_multiplier(self.automatic_degrades, self.graphics_performance);
        self.tick_animations(scene, dt);
        self.tick_portal(dt);

        let player = scene
            .and_then(Scene::player)
            .and_then(|p| p.position.map(|pos| (pos, p.scale)));
        self.cameras.follow_player(PlayerModeFacts {
            live_in_world: scene.and_then(Scene::player_guid).is_some(),
            player_present: player.is_some(),
            // The session's physics publishes the body's motion once its
            // movement controller is in charge of the character.
            controller_ready: scene
                .and_then(Scene::player)
                .and_then(|p| p.motion.as_ref())
                .is_some_and(|m| m.body.is_some()),
            // Player mode waits for the end of the login presentation.
            world_ready: self.reveal.is_complete() && !self.presents_login(),
            world_entry: scene.map_or(0, Scene::world_entries),
        });
        if let Some((pos, _)) = player {
            let block = LandblockCoord::of_cell(pos.cell_id);
            // The origin stays put while the player is within the far
            // window of it; beyond it (a teleport) it moves to the player.
            let recentered = if let OriginMove::Recentered { shift } =
                follow_player(&mut self.origin, block, self.far_radius + 1)
            {
                // Everything placed per frame from landblock-relative data
                // follows by itself; the cameras, the walk's cells and
                // buildings and the lights are moved or placed again.
                self.cameras.translate(shift);
                self.lights.clear_placed();
                self.walk.translate(shift);
                let t = Mat4::from_translation(shift);
                for near in self.residents.values_mut().filter_map(|r| r.near.as_mut()) {
                    for b in &mut near.buildings {
                        b.walk.world = t * b.walk.world;
                        b.walk.inverse = b.walk.world.inverse();
                    }
                }
                let origin = &self.origin;
                if let Some(effects) = self.effects.as_mut() {
                    effects.origin_moved(&|landblock| {
                        landblock_offset(origin, LandblockCoord::of_cell(landblock))
                    });
                }
                // The collision world is placed in the drawing frame, so it
                // is built again, once the landblocks leaving the window
                // are gone: a teleport, whose whole old window leaves,
                // builds none of it only to drop it. The old one is freed
                // off the frame.
                release_in_background(std::mem::take(&mut self.collision));
                true
            } else {
                false
            };
            self.stream(block, pos.cell_id);
            if recentered {
                self.install_resident_collision();
            }
            self.begin_reveal(pos.cell_id);
            let feet = to_frame(
                &self.origin,
                pos.cell_id,
                Vec3::new(pos.origin.x, pos.origin.y, pos.origin.z),
            );
            let o = pos.orientation;
            let yaw = yaw_of(Quat::from_xyzw(o.x, o.y, o.z, o.w));
            if let Some(center) = self.camera_collision.wanted(block) {
                self.loader.request(LoadRequest::CameraCollision(center));
            }
            // The session's server objects collide with the eye as they do
            // in the session's engine (OpenAC sweeps that engine itself).
            if let (Some(scene), Some(content)) = (scene, self.content.get_if_open()) {
                let deadline = Instant::now() + CAMERA_OBJECT_BUILD_BUDGET;
                for (id, error) in
                    self.camera_collision
                        .sync_objects(&content.dats, scene.objects(), || Instant::now() < deadline)
                {
                    self.notices
                        .push(format!("camera collision of object {id}: {error}"));
                }
            }
            let probe = self.camera_collision.probe(&self.origin, pos.cell_id);
            let collision = probe.as_ref().map(|probe| ChaseCollision {
                probe,
                cell_id: pos.cell_id,
                // The player's own rows are skipped by its client id.
                self_id: scene.and_then(Scene::player).map_or(0, |p| p.key.local_id),
            });
            let body = scene
                .and_then(Scene::player)
                .and_then(|p| p.motion.as_ref())
                .and_then(|m| m.body);
            // OpenAC `CombatCameraTargetSource` and
            // `WorldSelectionQuery.GetCombatCameraTargetPoint`: the combat
            // target half a metre up its own vertical axis.
            let tracked_target = scene
                .and_then(|s| {
                    let guid = s.player_view()?.combat_camera_target?;
                    s.object_by_guid(guid)?.position
                })
                .map(|p| {
                    let o = p.orientation;
                    let rotation = Quat::from_xyzw(o.x, o.y, o.z, o.w);
                    to_frame(
                        &self.origin,
                        p.cell_id,
                        Vec3::new(p.origin.x, p.origin.y, p.origin.z),
                    ) + rotation * Vec3::new(0.0, 0.0, 0.5)
                });
            let subject = ChaseSubject {
                velocity: body.map_or(Vec3::ZERO, |b| Vec3::from_array(b.velocity)),
                in_contact: body.is_none_or(|b| b.in_contact),
                contact_normal: body.map_or(Vec3::Z, |b| Vec3::from_array(b.contact_normal)),
                tracked_target,
                ..ChaseSubject::standing(feet, yaw)
            };
            // The session placed the body at a teleport's destination: the
            // eye restarts on the player and the boom re-extends from there
            // (OpenAC `LocalPlayerTeleportPlacement.Place`).
            if !self.portal.is_active() {
                self.viewer_reseat.clear();
            } else if self.viewer_reseat.take(pos.cell_id) {
                self.cameras.chase.reset_viewer_to_player(feet, yaw);
            }
            self.cameras.chase.update(&subject, dt, collision);
        }
        if self.cameras.mode() == CameraMode::Fly {
            self.cameras.fly.update(dt, fly);
        }

        let (view, eye) = self.cameras.view_and_eye();
        // Portal space warps the world's view as it leaves and returns.
        let projection = self.portal.world_projection(self.lens.projection());
        let view_proj = projection * view;
        let globals = Globals {
            view_proj: view_proj.to_cols_array_2d(),
            camera_pos: eye.extend(1.0).to_array(),
        };
        queue.write_buffer(&self.globals, 0, bytemuck::bytes_of(&globals));
        let look = view
            .inverse()
            .transform_vector3(Vec3::NEG_Z)
            .normalize_or_zero();
        self.camera = Some((eye, look));
        self.view_matrices = Some((view, projection));

        // The overhead map view keeps every building at full detail and is
        // drawn without distance fog, its eye being hundreds of metres away
        // (OpenAC `WorldCameraViewPolicy`).
        let overhead_view = self.cameras.is_overhead_view();
        self.degrade.degrades_disabled = overhead_view;
        // The sky and its light at the session's time of day, unfogged when
        // the player turned distance fog off (OpenAC `GameWindow` binds
        // `Weather.DisableDistanceFogSource` to the character option) or
        // in the overhead view.
        let disable_distance_fog = overhead_view
            || scene
                .and_then(Scene::player_view)
                .is_some_and(ac_runtime::world::PlayerView::disable_distance_fog);
        let env = match self.environment.as_mut() {
            Some(env) => env.frame(
                scene.and_then(Scene::synced_ticks),
                scene.and_then(|s| s.game_ticks(now)),
                now,
                disable_distance_fog,
            ),
            None => fallback_environment(),
        };
        let atmosphere = env.atmosphere;
        let fog = atmosphere.fog_color.clamp(Vec3::ZERO, Vec3::ONE);
        self.clear = self.environment.as_ref().map(|_| wgpu::Color {
            r: f64::from(fog.x),
            g: f64::from(fog.y),
            b: f64::from(fog.z),
            a: 1.0,
        });
        let player_frame = player.map(|(pos, _)| {
            to_frame(
                &self.origin,
                pos.cell_id,
                Vec3::new(pos.origin.x, pos.origin.y, pos.origin.z),
            )
        });
        let sealed = player.is_some_and(|(pos, _)| self.player_cell_sealed(pos.cell_id));
        self.place_lights(scene);
        // `/day` (persistent daylight): the landscape is lit as at noon;
        // the sky and its clock run on (OpenAC
        // `RuntimeWorldFrameEnvironmentPreparation.Prepare`).
        let persistent_daylight = scene
            .and_then(Scene::player_view)
            .is_some_and(|v| v.persistent_daylight());
        let landscape = self.environment.as_ref().map_or(env.keyframe, |e| {
            e.landscape_keyframe(&env, persistent_daylight)
        });
        let ubo = self.lights.frame(
            &landscape,
            &atmosphere,
            sealed,
            player_frame,
            eye,
            env.day_fraction,
        );
        queue.write_buffer(
            &self.scene_lighting,
            0,
            bytemuck::cast_slice(&ubo.to_floats()),
        );
        queue.write_buffer(
            &self.point_lights,
            0,
            bytemuck::cast_slice(&self.lights.point_floats()),
        );
        let disable_weather = scene
            .and_then(Scene::player_view)
            .is_some_and(|v| v.disable_most_weather_effects);
        self.build_sky_templates(view, projection, &env, now, disable_weather);

        // The frame is walked from the viewer's (camera's) cell: the cell
        // the chase camera's sweep reached, else (fly, orbit, or no
        // collision resident yet) the cell found around the eye.
        let player_cell = player.map(|(pos, _)| pos.cell_id);
        let swept_cell = match self.cameras.mode() {
            CameraMode::Chase if player.is_some() => self.cameras.chase.viewer_cell(),
            _ => None,
        };
        self.viewer_cell = swept_cell.unwrap_or_else(|| self.resolve_viewer_cell(player_cell, eye));
        let camera = WalkCamera {
            view_proj,
            inverse_view_proj: view_proj.inverse(),
            eye,
            forward: look,
            width: self.size.0.max(1) as f32,
            height: self.size.1.max(1) as f32,
        };
        let inverse_view = view.inverse();
        let particle_camera = ac_vfx::ParticleCamera {
            position: eye,
            right: inverse_view.x_axis.truncate().normalize_or_zero(),
            up: inverse_view.y_axis.truncate().normalize_or_zero(),
        };
        self.prepare_atmosphere(device, queue, scene, &env, view, projection, eye, dt);
        if self.portal.hides_world() {
            // The world is not drawn behind portal space (its models are
            // still asked for, so the destination can be readied).
            self.request_missing_models(scene);
            // Nothing of the world is drawn, so nothing can be picked.
            self.selection
                .begin_frame(None, self.selection_epoch.elapsed().as_secs_f64());
            self.selection.complete_frame();
            self.build_tunnel_frame(device, queue);
        } else if self.ui_only {
            // UI Only: the world frame is not built and nothing is
            // pickable (OpenAC `WorldSceneRenderer` with `DrawWorld` off).
            self.selection
                .begin_frame(None, self.selection_epoch.elapsed().as_secs_f64());
            self.selection.complete_frame();
        } else {
            self.build_frame(
                device,
                queue,
                scene,
                &camera,
                &particle_camera,
                env.day_fraction,
                dt,
            );
        }
        if let Some(a) = self.atmosphere.as_mut() {
            let started = Instant::now();
            a.upload_casters(device, queue);
            a.cpu_added += started.elapsed();
        }
        self.advance_reveal();
        if now.duration_since(self.last_sweep) >= SWEEP_INTERVAL {
            self.last_sweep = now;
            self.sweep(scene);
        }
        self.stats.viewer_cell = self.viewer_cell;
        self.stats.prepare_micros =
            u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX);
    }

    /// Whether the player's cell is sealed: an interior cell that cannot
    /// see outside (then the sun is off and the ambient flat). Read from
    /// the cell dat when the cell changes.
    fn player_cell_sealed(&mut self, cell: u32) -> bool {
        if let Some((known, sealed)) = self.player_cell_sealed
            && known == cell
        {
            return sealed;
        }
        let sealed = cell & 0xFFFF >= 0x0100
            && self.content.get_if_open().is_some_and(|content| {
                content
                    .dats
                    .get::<ac_dat::objects::EnvCell>(cell)
                    .ok()
                    .flatten()
                    .is_some_and(|c| {
                        !c.flags
                            .contains(ac_dat::objects::EnvCellFlags::SEEN_OUTSIDE)
                    })
            });
        self.player_cell_sealed = Some((cell, sealed));
        sealed
    }

    // ── Streaming ──────────────────────────────────────────────────────

    /// Follow the player with the streaming window: two tiers outdoors,
    /// the one landblock inside a sealed dungeon.
    fn stream(&mut self, block: LandblockCoord, cell: u32) {
        let gate = dungeon_gate(false, self.player_cell_sealed(cell), cell);
        if let Some(lb) = gate.observer_landblock {
            if self.dungeon != Some(lb) {
                self.dungeon = Some(lb);
                self.region = None;
                let gone: Vec<LandblockCoord> = self.residents.keys().copied().collect();
                for c in gone.into_iter().filter(|c| *c != block) {
                    self.unload(c);
                }
                self.load(block, Tier::Near);
            }
            return;
        }
        if self.dungeon.take().is_some() {
            let gone: Vec<LandblockCoord> = self.residents.keys().copied().collect();
            for c in gone {
                self.unload(c);
            }
        }
        let (bx, by) = (i32::from(block.x), i32::from(block.y));
        let diff = match self.region.as_mut() {
            Some(region) if region.center() == (bx, by) => return,
            Some(region) => region.recenter_to(bx, by),
            None => {
                let mut region = StreamingRegion::new(bx, by, self.near_radius, self.far_radius);
                let diff = region.first_tick_diff();
                region.mark_resident_from_bootstrap();
                self.region = Some(region);
                diff
            }
        };
        self.apply_diff(diff);
    }

    fn apply_diff(&mut self, diff: TwoTierDiff) {
        for c in diff.unload {
            self.unload(c);
        }
        for c in diff.demote {
            self.demote(c);
        }
        for c in diff.load_near {
            self.load(c, Tier::Near);
        }
        for c in diff.promote {
            self.promote(c);
        }
        for c in diff.load_far {
            self.load(c, Tier::Far);
        }
    }

    fn load(&mut self, coord: LandblockCoord, tier: Tier) {
        // Wanted again before it was released: released whole first, so
        // the new copy never meets what is left of the old one.
        if let Some(r) = self.retiring.take(coord) {
            self.retire_landblock(coord, r);
        }
        if self.residents.contains_key(&coord) {
            if tier == Tier::Near {
                self.promote(coord);
            }
            return;
        }
        self.loader.request(LoadRequest::Landblock {
            coord,
            near: tier == Tier::Near,
        });
        let near_requested = tier == Tier::Near;
        if near_requested {
            self.loader.request(LoadRequest::Near(coord));
        }
        self.residents.insert(
            coord,
            Resident {
                tier,
                tile: None,
                missing: false,
                near: None,
                near_requested,
            },
        );
    }

    fn promote(&mut self, coord: LandblockCoord) {
        let Some(r) = self.residents.get_mut(&coord) else {
            return;
        };
        r.tier = Tier::Near;
        if r.near.is_none() && !r.near_requested {
            r.near_requested = true;
            self.loader.request(LoadRequest::Near(coord));
        }
    }

    fn demote(&mut self, coord: LandblockCoord) {
        let Some(r) = self.residents.get_mut(&coord) else {
            return;
        };
        r.tier = Tier::Far;
        r.near_requested = false;
        if let Some(near) = r.near.take() {
            self.drop_near(coord, &near);
        }
    }

    fn unload(&mut self, coord: LandblockCoord) {
        // It leaves the window now (nothing of it is drawn or waited for);
        // what it holds is released over the next frames.
        match self.residents.remove(&coord) {
            Some(r) => self.retiring.push(coord, r),
            None => self
                .collision
                .remove_landblock_from_graph(coord.terrain_id()),
        }
    }

    /// Release what a landblock that left the window still holds: its
    /// terrain buffer, its cells' meshes and walk cells, its collision, its
    /// lights, animations and effects. Whole, in one step.
    fn retire_landblock(&mut self, coord: LandblockCoord, r: Resident) {
        if let Some(near) = &r.near {
            self.drop_near(coord, near);
        }
        self.collision
            .remove_landblock_from_graph(coord.terrain_id());
    }

    /// Release landblocks that left the window, oldest first, while
    /// `meter` admits them.
    fn advance_retirement(
        &mut self,
        meter: &mut crate::work_budget::StreamingWorkMeter<crate::work_budget::InstantClock>,
    ) {
        while let Some((coord, r)) = self.retiring.next_admitted(meter, retirement_cost) {
            self.retire_landblock(coord, r);
            let _ = meter.complete();
        }
    }

    fn drop_near(&mut self, coord: LandblockCoord, near: &NearResident) {
        for cell in &near.cells {
            self.meshes.remove(&cell_mesh_key(cell.id));
        }
        let lb = coord.terrain_id();
        self.walk.remove_landblock(lb >> 16);
        self.collision.remove_cells_for_landblock(lb);
        self.collision.remove_buildings_for_landblock(lb);
        self.collision.remove_env_cells_from_graph(lb);
        self.lights.drop_static_block(coord);
        self.animations.drop_statics(lb & 0xFFFF_0000);
        if let Some(effects) = self.effects.as_mut() {
            effects.drop_statics(lb & 0xFFFF_0000);
        }
    }

    /// The animation source, made when the dats are open.
    fn animation_source(&mut self) -> Option<&mut ContentAnimationSource> {
        if self.animation_source.is_none() {
            self.animation_source = self.content.get_if_open().map(ContentAnimationSource::new);
        }
        self.animation_source.as_mut()
    }

    /// Animate a landblock's statics and its cells' statics that have a
    /// default animation.
    fn register_static_animations(&mut self, coord: LandblockCoord, near: &NearResident) {
        let landblock = coord.terrain_id() & 0xFFFF_0000;
        let Some(source) = self.animation_source.as_mut() else {
            return;
        };
        let lists = std::iter::once((0, &near.statics))
            .chain(near.cells.iter().map(|c| (c.id, &c.statics)));
        for (cell, statics) in lists {
            for (index, s) in statics.iter().enumerate() {
                if s.model_id >> 24 != 0x02 {
                    continue;
                }
                let key = StaticKey {
                    landblock,
                    cell,
                    index: index as u32,
                };
                self.animations.register_static(
                    key,
                    s.model_id,
                    s.transform.w_axis.truncate(),
                    source,
                );
            }
        }
    }

    /// Start the effects of a landblock's statics, procedural scenery,
    /// building shells and cells' statics.
    fn register_static_effects(&mut self, coord: LandblockCoord, near: &NearResident) {
        let landblock = coord.terrain_id() & 0xFFFF_0000;
        let offset = landblock_offset(&self.origin, coord);
        let (Some(effects), Some(source)) = (self.effects.as_mut(), self.animation_source.as_ref())
        else {
            return;
        };
        let animations = &self.animations;
        effects.register_statics(
            landblock,
            offset,
            static_effect_candidates(landblock, near),
            |k| animations.static_animation(k).is_some(),
            source,
        );
    }

    /// Follow the scene's objects with their animations and advance every
    /// animation by `dt` seconds.
    fn tick_animations(&mut self, scene: Option<&Scene>, dt: f32) {
        let Some(scene) = scene else {
            self.animations.clear_motions();
            return;
        };
        if self.animation_source().is_none() {
            self.animations.clear_motions();
            return;
        }
        let Some(source) = self.animation_source.as_mut() else {
            self.animations.clear_motions();
            return;
        };
        self.animations.watch([
            scene.player_guid(),
            scene.player_view().and_then(|v| v.selected),
        ]);
        self.animations.sync(scene.objects(), source);
        let origin = &self.origin;
        let roots: HashMap<u32, Vec3> = scene
            .objects()
            .filter_map(|o| {
                let p = o.position?;
                Some((
                    o.key.local_id,
                    to_frame(
                        origin,
                        p.cell_id,
                        Vec3::new(p.origin.x, p.origin.y, p.origin.z),
                    ),
                ))
            })
            .collect();
        let player = scene
            .player()
            .and_then(|p| roots.get(&p.key.local_id).copied());
        self.animations.tick(
            f64::from(dt),
            player,
            |local| roots.get(&local).copied(),
            |key, local| {
                Some(landblock_offset(origin, LandblockCoord::of_cell(key.landblock)) + local)
            },
        );
        let (objects, statics) = self.animations.counts();
        self.stats.animated_objects = objects;
        self.stats.animated_statics = statics;
    }

    /// Take the sounds the animations fired this frame.
    pub(crate) fn take_animation_sounds(&mut self) -> Vec<FiredHook> {
        self.animations.take_sounds()
    }

    /// Put a landblock's interior cells and buildings into the walk and
    /// the collision world, placed in the drawing frame.
    fn install_near(&mut self, coord: LandblockCoord, near: &mut NearResident) {
        let offset = landblock_offset(&self.origin, coord);
        let t = Mat4::from_translation(offset);
        for cell in &near.cells {
            let mut walk_cell = cell.walk.clone();
            walk_cell.world = t * walk_cell.world;
            walk_cell.inverse = walk_cell.world.inverse();
            self.walk.insert_cell(walk_cell);
        }
        for b in &mut near.buildings {
            b.walk.world = t * b.walk.world;
            b.walk.inverse = b.walk.world.inverse();
        }
        self.install_collision(coord, near);
    }

    fn install_collision(&mut self, coord: LandblockCoord, near: &NearResident) {
        let offset = landblock_offset(&self.origin, coord);
        let prefix = coord.terrain_id() & 0xFFFF_0000;
        for cell in &near.cells {
            if let Some(physics) = &cell.physics {
                let mut placed = physics.clone();
                placed.placement.origin += offset;
                self.collision.insert_cell(placed);
            }
        }
        for b in &near.buildings {
            let mut physics = b.physics.clone();
            physics.placement.origin += offset;
            let landcell = prefix | (b.land_cell as u32 + 1);
            self.collision.insert_building(landcell, physics);
        }
    }

    /// Put every resident landblock's collision into the collision world,
    /// placed in the drawing frame: its outdoor cells where its terrain
    /// arrived, its interior cells and buildings where its near content
    /// did.
    fn install_resident_collision(&mut self) {
        let residents = std::mem::take(&mut self.residents);
        for (coord, r) in &residents {
            if r.tile.is_some() {
                let offset = landblock_offset(&self.origin, *coord);
                self.collision.register_terrain(coord.terrain_id(), offset);
            }
            if let Some(near) = &r.near {
                self.install_collision(*coord, near);
            }
        }
        self.residents = residents;
    }

    /// The cell the camera's eye is in, found the way physics finds a
    /// body's cell, starting from the player's cell.
    fn resolve_viewer_cell(&self, player_cell: Option<u32>, eye: Vec3) -> u32 {
        let Some(seed) = player_cell else {
            return 0;
        };
        if seed & 0xFFFF >= 0x0100 && self.collision.cell(seed).is_none() {
            return seed;
        }
        let mut cells = ac_physics::CellArray::new();
        let mut collector = ac_physics::cell_transit::CellCollector::new(&mut cells);
        let spheres = [ac_physics::Sphere::new(eye, VIEWER_SPHERE_RADIUS)];
        let (cell, _) = ac_physics::cell_transit::build_cell_set_and_pick_containing(
            &self.collision,
            &spheres,
            1,
            seed,
            None,
            &mut collector,
        );
        cell
    }

    /// Register the lights of near landblocks whose models have all
    /// arrived, and place server objects' lights where the objects are.
    fn place_lights(&mut self, scene: Option<&Scene>) {
        if !self.origin.is_known() {
            return;
        }
        let mut register = Vec::new();
        for (coord, r) in &self.residents {
            let Some(near) = &r.near else {
                continue;
            };
            if self.lights.has_static_block(*coord)
                || !near.model_ids.iter().all(|m| self.models.contains_key(m))
            {
                continue;
            }
            let offset = Mat4::from_translation(landblock_offset(&self.origin, *coord));
            let mut owners = Vec::new();
            let statics = near.light_statics.iter().map(|s| (s, 0u32)).chain(
                near.cells
                    .iter()
                    .flat_map(|c| c.statics.iter().map(move |s| (s, c.id))),
            );
            for (s, cell_id) in statics {
                let Some(info) = self.model_info.get(&s.model_id) else {
                    continue;
                };
                if info.lights.is_empty() {
                    continue;
                }
                owners.push((
                    StaticLightOwner {
                        transform: offset * s.transform,
                        cell_id,
                    },
                    info.lights.clone(),
                ));
            }
            register.push((*coord, owners));
        }
        for (coord, owners) in register {
            self.lights.register_static_block(coord, owners);
        }
        let mut seen = HashSet::new();
        let mut present = HashSet::new();
        for object in scene.into_iter().flat_map(Scene::objects) {
            let local = object.key.local_id;
            present.insert(local);
            self.lights.follow_state(local, object.state);
            let Some(setup_id) = object.setup_id else {
                continue;
            };
            // A held object (a carried torch) hangs where its parent held it
            // when last drawn; a placed one stands at its position.
            let place = match (object.parent, object.position) {
                (Some(_), _) => self.held_roots.get(&local).copied(),
                (None, Some(pos)) => {
                    let o = pos.orientation;
                    Some((
                        Mat4::from_rotation_translation(
                            Quat::from_xyzw(o.x, o.y, o.z, o.w),
                            to_frame(
                                &self.origin,
                                pos.cell_id,
                                Vec3::new(pos.origin.x, pos.origin.y, pos.origin.z),
                            ),
                        ),
                        pos.cell_id,
                    ))
                }
                (None, None) => None,
            };
            let Some((transform, cell_id)) = place else {
                continue;
            };
            let Some(info) = self.model_info.get(&setup_id) else {
                continue;
            };
            if info.lights.is_empty() {
                continue;
            }
            seen.insert(local);
            let is_dynamic = object.state & STATIC_STATE == 0;
            self.lights
                .place_object(local, transform, cell_id, &info.lights, is_dynamic);
        }
        self.lights.retain_objects(|id| seen.contains(&id));
        self.lights.retain_object_states(|id| present.contains(&id));
    }

    /// Switch server objects' lights by the "set light" hooks their
    /// animations and scripts fired (OpenAC `LightingHookSink.OnHook`).
    fn switch_lights(&mut self, switches: impl IntoIterator<Item = (u32, bool)>) {
        for (local, on) in switches {
            self.lights.set_owner_lighting(local, on);
        }
    }

    /// The sky sub-meshes this frame may draw (pre- and post-scene), each
    /// with its parameter block; `disable_weather` is the player's "disable
    /// most weather effects" option.
    fn build_sky_templates(
        &mut self,
        view: Mat4,
        projection: Mat4,
        env: &EnvironmentFrame,
        now: Instant,
        disable_weather: bool,
    ) {
        self.sky_templates.clear();
        let Some(environment) = self.environment.as_ref() else {
            return;
        };
        let Some(group) = environment.day_group() else {
            return;
        };
        let seconds = environment.seconds(now);
        // The world's projection, warped with it in portal space (the sky
        // takes the camera's projection with its own depth range).
        let view_proj =
            sky_projection(projection, crate::sky::SKY_NEAR, crate::sky::SKY_FAR) * sky_view(view);
        let sun_dir = sun_direction(&env.keyframe);
        for post_scene in [false, true] {
            for object in sky_draws(
                group,
                env.day_fraction,
                seconds,
                post_scene,
                env.environ_override,
                disable_weather,
            ) {
                let Some(Some(parts)) = self.models.get(&object.model_id) else {
                    continue;
                };
                for part in parts {
                    let Some(gfx) = self.meshes.get(&u64::from(part.gfx_obj_id)) else {
                        continue;
                    };
                    let model = object.transform * part.transform;
                    for (sub_index, sub) in gfx.sub_meshes.iter().enumerate() {
                        let Some(surface) = self.surfaces.get(&sub.surface_id) else {
                            continue;
                        };
                        let fog = env.environ_override && !surface.disable_fog;
                        let ambient = env.keyframe.ambient_color();
                        let sun = env.keyframe.sun_color();
                        self.sky_templates.push(SkyDraw {
                            post_scene,
                            gfx_id: part.gfx_obj_id,
                            sub_index,
                            surface: sub.surface_id,
                            repeat: sub.repeat || object.scrolls,
                            additive: surface.translucency == Translucency::Additive,
                            params: SkyParamsGpu {
                                model: model.to_cols_array(),
                                view_proj: view_proj.to_cols_array(),
                                ambient_emissive: [
                                    ambient.x,
                                    ambient.y,
                                    ambient.z,
                                    object.luminosity.unwrap_or(surface.luminosity),
                                ],
                                sun_color_diffuse: [
                                    sun.x,
                                    sun.y,
                                    sun.z,
                                    object.diffuse.unwrap_or(surface.diffuse),
                                ],
                                sun_dir_transparency: [
                                    sun_dir.x,
                                    sun_dir.y,
                                    sun_dir.z,
                                    object.transparency,
                                ],
                                scroll_fog_opacity: [
                                    object.uv_scroll.x,
                                    object.uv_scroll.y,
                                    if fog { 1.0 } else { 0.0 },
                                    surface.opacity,
                                ],
                            },
                        });
                    }
                }
            }
        }
    }

    // ── The frame ──────────────────────────────────────────────────────

    /// One frame of effects (see [`WindowEffects::frame`]), after the
    /// animations advanced and with the drawn objects' poses.
    #[allow(clippy::too_many_arguments)]
    fn run_effects(
        &self,
        effects: &mut WindowEffects,
        scene: Option<&Scene>,
        poses: &[OwnerPose],
        hooks: Vec<FiredHook>,
        eye: Vec3,
        day_fraction: f32,
        dt: f32,
    ) {
        let Some(source) = self.animation_source.as_ref() else {
            return;
        };
        let origin = &self.origin;
        let place = |cell: u32, local: Vec3| to_frame(origin, cell, local);
        let offset = |landblock: u32| landblock_offset(origin, LandblockCoord::of_cell(landblock));
        // A landblock is in the resident world once its load arrived.
        let resident = |cell: u32| {
            self.residents
                .get(&LandblockCoord::of_cell(cell))
                .is_some_and(|r| r.tile.is_some() || r.missing)
        };
        effects.frame(EffectsFrame {
            dt,
            scene,
            to_frame: &place,
            poses,
            animations: &self.animations,
            landblock_offset: &offset,
            resident: &resident,
            hooks,
            sky: self
                .environment
                .as_ref()
                .map(|env| (day_fraction, env.day_group())),
            sky_active: player_outdoors(scene),
            camera: eye,
            source,
            particle_range: self.particle_range,
        });
    }

    /// Walk the frame from the viewer's cell and build its draw stream.
    #[allow(clippy::too_many_arguments)]
    fn build_frame(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: Option<&Scene>,
        camera: &WalkCamera,
        particle_camera: &ac_vfx::ParticleCamera,
        day_fraction: f32,
        dt: f32,
    ) {
        if !self.origin.is_known() || self.terrain_group.is_none() {
            self.frame = FrameDraws::new();
            self.stats.objects_drawn = 0;
            self.stats.landblocks_drawn = 0;
            self.stats.cells_drawn = 0;
            // Nothing drawn: nothing to pick.
            self.selection
                .begin_frame(None, self.selection_epoch.elapsed().as_secs_f64());
            self.selection.complete_frame();
            return;
        }
        self.request_missing_models(scene);
        // The world frame publishes what can be picked and runs the flash.
        self.selection.begin_frame(
            Some(crate::frustum::FrustumPlanes::from_view_projection(
                camera.view_proj,
            )),
            self.selection_epoch.elapsed().as_secs_f64(),
        );
        let hooks = self.animations.take_effects();
        let mut light_switches: Vec<(u32, bool)> = hooks
            .iter()
            .filter_map(|fired| match (fired.owner, fired.hook.data) {
                (HookOwner::Object(local), ac_dat::objects::HookData::SetLight { lights_on }) => {
                    Some((local, lights_on))
                }
                _ => None,
            })
            .collect();
        let mut effects = self.effects.take();
        let mut walk = std::mem::take(&mut self.walk);
        // Last frame's shadow casters (their still part is kept while it
        // holds).
        let previous_casters = self
            .atmosphere
            .as_mut()
            .map(|a| std::mem::take(&mut a.caster_frame))
            .unwrap_or_default();
        let scratch = std::mem::take(&mut self.draw_scratch);
        let (mut objects, poses) = self.object_draws(scene);
        // A hidden object is not drawn, casts no shadow and cannot be
        // picked; its pose stays in `poses` for its emitters.
        for draws in objects.values_mut() {
            draws.retain(|draw| draw.visible);
        }
        // Each object in every cell its drawn parts reach (OpenAC files a
        // live object by its registration's cell array).
        let object_cells =
            file_object_draws(&objects, |owner| self.camera_collision.render_cells(owner));
        let held: HashSet<u32> = scene
            .into_iter()
            .flat_map(Scene::objects)
            .filter(|o| o.parent.is_some())
            .map(|o| o.key.local_id)
            .collect();
        let held_roots: HashMap<u32, (Mat4, u32)> = poses
            .iter()
            .filter(|p| held.contains(&p.local))
            .map(|p| (p.local, (p.root, p.cell)))
            .collect();
        if let Some(effects) = effects.as_mut() {
            self.run_effects(effects, scene, &poses, hooks, camera.eye, day_fraction, dt);
            // The view this frame draws is the next frame's particle view.
            let view = effects.vfx_mut().visibility_mut();
            view.begin_frame(camera.eye);
            if !self.reveal.is_withholding() {
                view.use_world_view();
            }
        }
        // The weather (objects after the world and their particles) only
        // with the player outdoors.
        let weather = weather_pass_open(
            self.environment.is_some(),
            scene
                .and_then(Scene::player)
                .and_then(|p| p.position)
                .map(|p| p.cell_id),
        );
        let particles = effects.as_mut().map(|e| {
            let (system, gfx, ledger) = e.draw_sources();
            SinkParticles::new(
                system,
                gfx,
                *particle_camera,
                &self.surfaces,
                ledger,
                weather,
            )
        });

        // The landblocks and buildings the walk may reach.
        let mut coords: Vec<LandblockCoord> = self
            .residents
            .iter()
            .filter(|(_, r)| r.tile.is_some() || r.near.is_some())
            .map(|(c, _)| *c)
            .collect();
        coords.sort();
        let terrain_present: HashSet<LandblockCoord> = self
            .residents
            .iter()
            .filter(|(_, r)| r.tile.is_some())
            .map(|(c, _)| *c)
            .collect();
        let mut blocks = Vec::with_capacity(coords.len());
        let mut block_refs: Vec<(LandblockCoord, Option<&NearResident>)> =
            Vec::with_capacity(coords.len());
        let mut buildings: Vec<BuildingDraw<'_>> = Vec::new();
        let mut building_refs: Vec<BuildingRef<'_>> = Vec::new();
        let mut cells: HashMap<u32, CellRef<'_>> = HashMap::new();
        for coord in &coords {
            let r = &self.residents[coord];
            let offset = landblock_offset(&self.origin, *coord);
            let near = r.near.as_ref().filter(|_| r.tier == Tier::Near);
            let mut block_buildings = Vec::new();
            if let Some(near) = near {
                for b in &near.buildings {
                    let world = Mat4::from_translation(offset) * b.transform;
                    let (part0, part0_turned) = shell_part0(b, world, camera.eye, &self.degrade);
                    let tree = if part0 == 0 {
                        None
                    } else {
                        b.trees.get(&part0).or_else(|| b.trees.get(&b.part0.0))
                    };
                    let sphere = sphere_of(
                        self.model_info.get(&b.model_id).and_then(|i| i.bounds),
                        world,
                    );
                    block_buildings.push((b.land_cell, buildings.len()));
                    buildings.push(BuildingDraw {
                        model_id: b.model_id,
                        world,
                        part0,
                        part0_turned,
                        sphere,
                        _data: b,
                    });
                    building_refs.push(BuildingRef {
                        building: &b.walk,
                        tree,
                    });
                }
                for cell in &near.cells {
                    cells.insert(
                        cell.id,
                        CellRef {
                            coord: *coord,
                            data: cell,
                        },
                    );
                }
            }
            let (min_z, max_z) = r.tile.as_ref().map_or((0.0, 0.0), |t| (t.min_z, t.max_z));
            blocks.push(WalkBlock {
                coord: (coord.x, coord.y),
                origin: offset,
                min_z,
                max_z,
                near: near.is_some(),
                buildings: block_buildings,
            });
            block_refs.push((*coord, near));
        }
        let sky_pre: Vec<usize> = (0..self.sky_templates.len())
            .filter(|&i| !self.sky_templates[i].post_scene)
            .collect();
        let sky_post: Vec<usize> = (0..self.sky_templates.len())
            .filter(|&i| weather && self.sky_templates[i].post_scene)
            .collect();
        let mut sink = FrameSink {
            input: SinkInputs {
                width: camera.width,
                height: camera.height,
                eye: camera.eye,
                cy: camera.cy_plane(),
                origin: &self.origin,
                blocks: &block_refs,
                buildings: &buildings,
                cells: &cells,
                objects: &objects,
                object_cells: &object_cells,
                models: &self.models,
                model_info: &self.model_info,
                meshes: &self.meshes,
                lights: &self.lights,
                sky_pre: &sky_pre,
                sky_post: &sky_post,
                terrain_present: &terrain_present,
                animations: &self.animations,
                selection: &self.selection,
                wind: self
                    .atmosphere
                    .as_ref()
                    .is_some_and(|a| a.config.wind.enabled),
            },
            out: StreamBuilder::with_scratch(scratch),
            views: Vec::new(),
            portals_drawn_count: self.portals_drawn_count,
            missing: HashSet::new(),
            particles,
            selection_parts: Vec::new(),
            land_cells: Vec::new(),
            scenery_wind: false,
            stage: Default::default(),
        };
        // Objects and buildings are drawn out to the near radius from the
        // viewer's landblock (never past ring 4: the walk stops there),
        // however far the near tier still reaches.
        walk.object_ring_limit = self.near_radius;
        walk.walk_frame(self.viewer_cell, camera, &blocks, &building_refs, &mut sink);
        sink.walk_done();
        let portals_drawn_count = sink.portals_drawn_count;
        let missing = std::mem::take(&mut sink.missing);
        let selection_parts = std::mem::take(&mut sink.selection_parts);
        let (visible_cells, missing_surfaces, missing_effect_gfx) =
            sink.particles.take().map_or_else(
                || (Vec::new(), HashSet::new(), HashSet::new()),
                |p| (p.visible_cells, p.missing_surfaces, p.missing_gfx),
            );
        let land_cells = std::mem::take(&mut sink.land_cells);
        let (mut frame, scratch) = sink.out.finish_keeping();
        // The landscape is centred on the player's landblock: it and its
        // neighbours are at full detail, where the landscape detail draws.
        let full_detail_center = self.region.as_ref().map(|r| {
            let (x, y) = r.center();
            LandblockCoord {
                x: x as u8,
                y: y as u8,
            }
        });
        crate::draws::mark_full_detail(
            &mut frame.terrain,
            &frame.terrain_draws,
            full_detail_center,
        );
        // The shadow casters stand in the land cells the walk reached.
        let caster_started = Instant::now();
        let casters = if self.atmosphere.as_ref().is_some_and(|a| a.shadows_on()) {
            Some(self.collect_shadow_casters(previous_casters, &land_cells, &objects, camera.eye))
        } else {
            None
        };
        let caster_time = caster_started.elapsed();
        drop(objects);
        drop(cells);
        drop(buildings);
        drop(building_refs);
        drop(block_refs);
        drop(blocks);
        self.walk = walk;
        self.draw_scratch = scratch;
        if let Some(a) = self.atmosphere.as_mut() {
            a.caster_frame = casters.unwrap_or_default();
            a.cpu_added += caster_time;
        }
        self.portals_drawn_count = portals_drawn_count;
        for part in selection_parts {
            self.selection.add_visible_part(
                part.guid,
                part.local_id,
                part.part_index,
                part.gfx_obj_id,
                part.world,
            );
        }
        self.selection.complete_frame();
        let mut fades = Vec::new();
        if let Some(effects) = effects.as_mut() {
            let view = effects.vfx_mut().visibility_mut();
            if let Err(error) = view.mark_visible_landscape_cells(visible_cells.iter().copied()) {
                eprintln!("vfx: {error}");
            }
            view.complete_frame();
            self.effect_sounds.extend(effects.take_sounds());
            fades = effects.take_part_fades();
            light_switches.extend(effects.take_light_switches());
            let particles = effects.vfx().particles();
            self.stats.emitters = particles.active_emitter_count();
            self.stats.particles = usize::try_from(particles.active_particle_count()).unwrap_or(0);
        }
        self.effects = effects;
        self.held_roots = held_roots;
        self.switch_lights(light_switches);
        for fade in fades {
            self.animations
                .start_part_fade(fade.owner, fade.part, fade.start, fade.end, fade.time);
        }
        for id in missing {
            if self.requested_gfx.insert(id) {
                self.loader.request(LoadRequest::GfxObj(id));
            }
        }
        // Particle models and textures keep their full detail.
        for id in missing_effect_gfx {
            if self.requested_gfx.insert(id) {
                self.loader.request(LoadRequest::EffectGfxObj(id));
            }
        }
        for id in missing_surfaces {
            if self.requested_surfaces.insert(id) {
                self.loader.request(LoadRequest::EffectSurface(id));
            }
        }
        self.upload_frame(device, queue, &frame);
        self.particle_gpu.upload(device, queue, &frame);
        self.stats.landblocks_drawn = frame
            .terrain_draws
            .iter()
            .map(|d| d.block)
            .collect::<HashSet<_>>()
            .len();
        self.stats.objects_drawn = frame.objects_drawn;
        self.stats.cells_drawn = frame.cells_drawn;
        self.stats.particles_drawn = frame.billboards.len() + frame.particle_meshes.len();
        let drawn = std::mem::replace(&mut self.frame, frame);
        self.draw_scratch.keep(drawn);
    }

    /// The model a building's part 0 draws at its distance from the eye
    /// (0: the building is not drawn).
    fn building_level(&self, b: &BuildingData, world: Mat4, eye: Vec3) -> u32 {
        shell_part0(b, world, eye, &self.degrade).0
    }

    /// Server objects by the cell they are in (outdoor cells by landblock
    /// and land cell), asking for models not yet here.
    fn request_missing_models(&mut self, scene: Option<&Scene>) {
        self.server_models_missing.clear();
        for (object, setup_id) in drawn_objects(scene) {
            // Held objects do not hold back the login reveal.
            let gates_reveal = object.parent.is_none();
            let key = AppearanceKey::in_world(setup_id, &object.model);
            // A plain object needs its setup first: whether a humanoid's
            // parts take their closest level is read from it.
            if key.is_plain() && !self.models.contains_key(&setup_id) {
                if gates_reveal {
                    self.server_models_missing.push((object.guid, setup_id));
                }
                if self.requested_models.ask(setup_id) {
                    self.loader.request(LoadRequest::Model(setup_id));
                }
            } else if self.drawn_by_appearance(&key) && !self.looks.contains(&key) {
                if gates_reveal {
                    self.server_models_missing.push((object.guid, setup_id));
                }
                if self.looks.ask(&key) {
                    self.loader.request(LoadRequest::Appearance(Box::new(key)));
                }
            }
        }
        self.server_models_ready = self.server_models_missing.is_empty();
    }

    /// Whether a server object's parts are its appearance's: it has an
    /// appearance description, or its setup (here already) has the
    /// humanoid layout whose parts take their closest level.
    fn drawn_by_appearance(&self, key: &AppearanceKey) -> bool {
        !key.is_plain()
            || self
                .models
                .get(&key.setup_id)
                .and_then(Option::as_deref)
                .is_some_and(|parts| {
                    has_humanoid_null_part_layout(parts.iter().map(|p| p.gfx_obj_id))
                })
    }

    /// An object's parts with its appearance, and their bounds; `None`
    /// until they have arrived.
    fn object_parts(
        &self,
        setup_id: u32,
        model: &ac_proto::ModelData,
    ) -> Option<(&[MeshRef], Bounds)> {
        let key = AppearanceKey::in_world(setup_id, model);
        if !self.drawn_by_appearance(&key) {
            let parts = self.models.get(&setup_id).and_then(Option::as_deref)?;
            Some((parts, self.model_info.get(&setup_id).and_then(|i| i.bounds)))
        } else {
            self.looks.parts(&key)
        }
    }

    /// `parts` in the poses of an animation, without the parts a fade hook
    /// hid, and their opacities at the object's `opacity`.
    fn posed_parts(
        &self,
        parts: &[MeshRef],
        poses: &[PartPose],
        owner: HookOwner,
        opacity: f32,
    ) -> (Vec<MeshRef>, Option<Vec<f32>>) {
        self.animations
            .faded_parts(owner, opacity, parts, |i, part| {
                poses.get(i).map_or(part.transform, |p| p.visual)
            })
    }

    /// Server objects by the cell they are in (outdoor cells by landblock
    /// and land cell id): top-level objects at their place (animated ones
    /// in their pose), then held objects on their parent's holding
    /// location, parents first.
    fn object_draws(
        &self,
        scene: Option<&Scene>,
    ) -> (HashMap<u32, Vec<ObjectDraw<'_>>>, Vec<OwnerPose>) {
        self.object_draws_of(scene, None)
    }

    /// [`Self::object_draws`] of every object, or (`only`) of one object
    /// and what it holds.
    fn object_draws_of(
        &self,
        scene: Option<&Scene>,
        only: Option<u32>,
    ) -> (HashMap<u32, Vec<ObjectDraw<'_>>>, Vec<OwnerPose>) {
        /// What a held object needs of its parent.
        struct Root {
            /// The parent's root, drawing frame, no scale.
            world: Mat4,
            /// Its part frames, when posed.
            rigid: Option<Vec<Mat4>>,
            setup_id: u32,
            placement: Option<u32>,
            scale: f32,
            cell: u32,
            lights: LightSet,
            indoor: bool,
            /// Whether the parent is drawn.
            visible: bool,
            /// The top-level object whose cells it is drawn in.
            owner: u32,
        }

        let player_guid = scene.and_then(Scene::player).map(|p| p.guid);
        let mut out: HashMap<u32, Vec<ObjectDraw<'_>>> = HashMap::new();
        let mut poses: Vec<OwnerPose> = Vec::new();
        let mut roots: HashMap<u32, Root> = HashMap::new();
        let mut held: Vec<(u32, u32)> = Vec::new();
        let mut held_views = HashMap::new();
        for object in scene.into_iter().flat_map(Scene::objects) {
            let Some(setup_id) = object.setup_id else {
                continue;
            };
            if let Some(parent) = object.parent {
                if only.is_none_or(|guid| guid == parent.guid) {
                    held.push((object.guid, parent.guid));
                    held_views.insert(object.guid, object);
                }
                continue;
            }
            if only.is_some_and(|guid| guid != object.guid) {
                continue;
            }
            let Some(pos) = object.position else {
                continue;
            };
            let Some((parts, bounds)) = self.object_parts(setup_id, &object.model) else {
                continue;
            };
            let o = pos.orientation;
            let animation = self.animations.object(object.key.local_id);
            let rotation = animation.map_or(Quat::IDENTITY, |a| a.spin())
                * Quat::from_xyzw(o.x, o.y, o.z, o.w);
            let scale = object.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
            let translation = to_frame(
                &self.origin,
                pos.cell_id,
                Vec3::new(pos.origin.x, pos.origin.y, pos.origin.z),
            );
            let root = Mat4::from_rotation_translation(rotation, translation);
            let scaled =
                Mat4::from_scale_rotation_translation(Vec3::splat(scale), rotation, translation);
            let sphere = sphere_of(bounds, scaled);
            let indoor = indoor_object_receives_torches(Some(pos.cell_id));
            let lights = if indoor {
                self.lights
                    .object_lights(Some(pos.cell_id), sphere.0, sphere.1)
            } else {
                NO_LIGHTS
            };
            // The player fades as the chase camera's eye comes close.
            let opacity = translucency::opacity(translucency::object_translucency(
                object.guid,
                player_guid,
                self.cameras.chase.player_translucency(),
                object.translucency,
            ));
            let (world, parts, opacities) = match animation {
                Some(a) => {
                    let (posed, opacities) = self.posed_parts(
                        parts,
                        &a.poses,
                        HookOwner::Object(object.key.local_id),
                        opacity,
                    );
                    (root, Cow::Owned(posed), opacities)
                }
                None => (
                    scaled,
                    Cow::Borrowed(parts),
                    (opacity < 1.0).then(|| vec![opacity; parts.len()]),
                ),
            };
            let visible = draws_by_state(object.state);
            out.entry(pos.cell_id).or_default().push(ObjectDraw {
                parts,
                world,
                lights,
                indoor,
                is_player: Some(object.guid) == player_guid,
                opacities,
                guid: object.guid,
                local_id: object.key.local_id,
                root,
                held: false,
                visible,
                render_owner: object.key.local_id,
            });
            poses.push(OwnerPose {
                local: object.key.local_id,
                root,
                parts: animation.map(|a| a.poses.iter().map(|p| p.rigid).collect()),
                cell: pos.cell_id,
            });
            roots.insert(
                object.guid,
                Root {
                    world: root,
                    rigid: animation.map(|a| a.poses.iter().map(|p| p.rigid).collect()),
                    setup_id,
                    placement: Some(object.rest_placement),
                    scale,
                    cell: pos.cell_id,
                    lights,
                    indoor,
                    visible,
                    owner: object.key.local_id,
                },
            );
        }
        if held.is_empty() {
            return (out, poses);
        }
        let Some(source) = self.animation_source.as_ref() else {
            return (out, poses);
        };
        let mut order = AttachmentUpdateOrder::default();
        order.for_each_parent_first(
            &held,
            |parent| Some(*parent),
            |guid| {
                let Some(child) = held_views.get(&guid) else {
                    return false;
                };
                let (Some(attachment), Some(child_setup_id)) = (child.parent, child.setup_id)
                else {
                    return false;
                };
                let Some(parent) = roots.get(&attachment.guid) else {
                    return false;
                };
                let (Some(parent_setup), Some(child_setup)) =
                    (source.setup(parent.setup_id), source.setup(child_setup_id))
                else {
                    return false;
                };
                let rigid = parent.rigid.clone().unwrap_or_else(|| {
                    rest_poses(&parent_setup, parent.placement, parent.scale)
                        .into_iter()
                        .map(|p| p.rigid)
                        .collect()
                });
                let scale = child.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
                let Some(pose) = compose_child(
                    &parent_setup,
                    &rigid,
                    None,
                    &child_setup,
                    attachment.location_id,
                    child.placement.unwrap_or(PLACEMENT_DEFAULT),
                    scale,
                ) else {
                    return false;
                };
                let Some((parts, _)) = self.object_parts(child_setup_id, &child.model) else {
                    return false;
                };
                let world = parent.world;
                let child_root = world * pose.root_local;
                let posed: Vec<MeshRef> = parts
                    .iter()
                    .zip(&pose.attached_parts)
                    .map(|(part, transform)| MeshRef {
                        gfx_obj_id: part.gfx_obj_id,
                        transform: *transform,
                        variant: part.variant,
                    })
                    .collect();
                let (cell, lights, indoor, owner) =
                    (parent.cell, parent.lights, parent.indoor, parent.owner);
                // A held object keeps its own translucency.
                let opacity = translucency::opacity(translucency::object_translucency(
                    child.guid,
                    player_guid,
                    0.0,
                    child.translucency,
                ));
                let opacities = (opacity < 1.0).then(|| vec![opacity; posed.len()]);
                let visible = held_draws(child.state, parent.visible);
                out.entry(cell).or_default().push(ObjectDraw {
                    parts: Cow::Owned(posed),
                    world,
                    lights,
                    indoor,
                    is_player: false,
                    opacities,
                    guid,
                    local_id: child.key.local_id,
                    root: child_root,
                    held: true,
                    visible,
                    render_owner: owner,
                });
                poses.push(OwnerPose {
                    local: child.key.local_id,
                    root: child_root,
                    parts: Some(pose.part_local.clone()),
                    cell,
                });
                roots.insert(
                    guid,
                    Root {
                        world: child_root,
                        rigid: Some(pose.part_local),
                        setup_id: child_setup_id,
                        placement: Some(child.rest_placement),
                        scale,
                        cell,
                        lights,
                        indoor,
                        visible,
                        owner,
                    },
                );
                true
            },
        );
        (out, poses)
    }

    /// Upload the frame's instance, clip, terrain, portal and sky data.
    fn upload_frame(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, frame: &FrameDraws) {
        if frame.instances.len() > self.instances_capacity {
            self.instances_capacity = frame.instances.len().next_power_of_two();
            self.instances = vertex_buffer(
                device,
                "instances",
                self.instances_capacity * size_of::<crate::draws::InstanceGpu>(),
            );
        }
        if !frame.instances.is_empty() {
            queue.write_buffer(&self.instances, 0, bytemuck::cast_slice(&frame.instances));
        }
        if frame.terrain.len() > self.terrain_instances_capacity {
            self.terrain_instances_capacity = frame.terrain.len().next_power_of_two();
            self.terrain_instances = vertex_buffer(
                device,
                "terrain instances",
                self.terrain_instances_capacity * size_of::<crate::draws::TerrainInstanceGpu>(),
            );
        }
        if !frame.terrain.is_empty() {
            queue.write_buffer(
                &self.terrain_instances,
                0,
                bytemuck::cast_slice(&frame.terrain),
            );
        }
        if frame.portals.len() > self.portal_vertices_capacity {
            self.portal_vertices_capacity = frame.portals.len().next_power_of_two();
            self.portal_vertices = vertex_buffer(
                device,
                "portal vertices",
                self.portal_vertices_capacity * size_of::<crate::draws::PortalVertexGpu>(),
            );
        }
        if !frame.portals.is_empty() {
            queue.write_buffer(
                &self.portal_vertices,
                0,
                bytemuck::cast_slice(&frame.portals),
            );
        }
        let mut regrouped = false;
        if frame.clips.len() > self.clips_capacity {
            self.clips_capacity = frame.clips.len().next_power_of_two();
            self.clips = storage_buffer(
                device,
                "clip regions",
                self.clips_capacity * size_of::<ClipRegionGpu>(),
            );
            regrouped = true;
        }
        queue.write_buffer(&self.clips, 0, bytemuck::cast_slice(&frame.clips));
        // The sky blocks: one per draw.
        if frame.sky.len() > self.sky_params_capacity {
            self.sky_params_capacity = frame.sky.len().next_power_of_two();
            self.sky_params = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("sky params"),
                size: (self.sky_params_capacity * SKY_PARAMS_STRIDE) as u64,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            regrouped = true;
        }
        if regrouped {
            self.globals_group = globals_group(
                device,
                &self.globals_layout,
                &self.globals,
                &self.scene_lighting,
                &self.point_lights,
                &self.clips,
            );
            self.sky_group = sky_group(
                device,
                &self.sky_layout,
                &self.sky_params,
                &self.scene_lighting,
                &self.clips,
            );
        }
        if !frame.sky.is_empty() {
            let mut bytes = vec![0u8; frame.sky.len() * SKY_PARAMS_STRIDE];
            for (i, &template) in frame.sky.iter().enumerate() {
                let params = self.sky_templates[template].params;
                let at = i * SKY_PARAMS_STRIDE;
                bytes[at..at + size_of::<SkyParamsGpu>()]
                    .copy_from_slice(bytemuck::bytes_of(&params));
            }
            queue.write_buffer(&self.sky_params, 0, &bytes);
        }
    }

    /// Release meshes, textures and models nothing resident uses, and tell
    /// the loader so it sends them again when they are wanted.
    fn sweep(&mut self, scene: Option<&Scene>) {
        let mut models: HashSet<u32> = self.sky_models.clone();
        models.extend(self.tunnel_setup);
        for r in self.residents.values() {
            if let Some(near) = &r.near {
                models.extend(near.model_ids.iter().copied());
                models.extend(near.buildings.iter().map(|b| b.model_id));
            }
        }
        for object in scene.into_iter().flat_map(Scene::objects) {
            if let Some(id) = object.setup_id {
                models.insert(id);
            }
        }
        models.extend(self.viewports.models());
        let used = looks_in_use(scene, &mut self.viewports);
        let mut gfx: HashSet<u32> = HashSet::new();
        // Meshes and textures nothing draws stay within their unowned
        // budgets (OpenAC's residency budgets); the oldest beyond go. Looks
        // nothing draws any more go, with the variants only they named, and
        // a recoloured surface no kept look draws with is held in the
        // texture budget like any other (OpenAC releases a composite
        // texture's last owner into its unowned budget).
        // UI Only keeps nothing unowned (OpenAC
        // `SetUnownedContentRetained(false)`).
        let retain = !self.ui_only;
        let swept = crate::appearance_residency::sweep_looks(
            &mut self.looks,
            &mut self.unowned_surfaces,
            |key| used.contains(key),
            |looks| {
                for id in &models {
                    if let Some(Some(parts)) = self.models.get(id) {
                        gfx.extend(parts.iter().map(|p| p.gfx_obj_id));
                    }
                }
                gfx.extend(looks.all_parts().map(|p| p.gfx_obj_id));
                // Particle models and textures live emitters draw with.
                let mut surfaces = HashSet::new();
                if let Some(effects) = self.effects.as_ref() {
                    gfx.extend(effects.ledger().gfx_objs());
                    surfaces.extend(effects.ledger().surfaces());
                }
                for r in self.residents.values() {
                    for b in r.near.iter().flat_map(|n| &n.buildings) {
                        gfx.insert(b.part0.0);
                        gfx.extend(b.levels.iter().map(|l| l.gfx_obj_id));
                    }
                }
                let levels: Vec<u32> = gfx
                    .iter()
                    .filter_map(|id| self.meshes.get(&u64::from(*id)))
                    .flat_map(|m| m.degrades.iter().map(|l| l.gfx_obj_id))
                    .collect();
                gfx.extend(levels);
                for (key, mesh) in &self.meshes {
                    if *key >> 32 != 0 || gfx.contains(&(*key as u32)) {
                        surfaces.extend(mesh.sub_meshes.iter().map(|s| s.surface_id));
                    }
                }
                surfaces
            },
            self.surfaces.iter().map(|(id, s)| (*id, s.bytes)),
            retain,
        );
        // Surfaces a kept mesh needs that are not here (released while the
        // mesh was being sent): ask again.
        let lost: Vec<u32> = swept
            .owned
            .iter()
            .copied()
            .filter(|id| *id < FIRST_SURFACE_HANDLE && !self.surfaces.contains_key(id))
            .collect();
        for id in lost {
            if self.requested_surfaces.insert(id) {
                self.loader.request(LoadRequest::Surface(id));
            }
        }
        let gone_models: Vec<u32> = self
            .models
            .keys()
            .copied()
            .filter(|id| !models.contains(id))
            .collect();
        let gone_gfx = self.unowned_meshes.settle(
            self.meshes
                .iter()
                .filter(|(key, _)| *key >> 32 == 0)
                .map(|(key, mesh)| {
                    let bytes = (mesh.vertices.size() + mesh.indices.size()).max(1);
                    (*key as u32, bytes)
                }),
            |id| gfx.contains(id),
            retain,
        );
        for id in &gone_models {
            self.models.remove(id);
            self.model_info.remove(id);
            self.requested_models.release(*id);
        }
        for id in &gone_gfx {
            self.meshes.remove(&u64::from(*id));
            self.requested_gfx.remove(id);
        }
        for id in &swept.gone_surfaces {
            self.surfaces.remove(id);
            self.requested_surfaces.remove(id);
        }
        // Everything released is forgotten in one request, so the loader
        // sends each again when next wanted.
        if let Some(forget) = swept.into_forget(gone_models, gone_gfx) {
            self.loader.request(forget);
        }
        let (count, bytes) = self
            .surfaces
            .iter()
            .filter(|(id, _)| **id >= FIRST_SURFACE_HANDLE)
            .fold((0, 0), |(n, b), (_, s)| (n + 1, b + s.bytes));
        self.stats.recoloured_surfaces = count;
        self.stats.recoloured_surface_bytes = bytes;
        self.stats.appearances = self.looks.appearance_count();
        self.stats.variants = self.looks.variant_count();
    }

    /// Whether the player's interior cell `cell` can see outside (as last
    /// read for the light).
    pub(crate) fn player_cell_seen_outside(&self, cell: u32) -> bool {
        matches!(self.player_cell_sealed, Some((known, sealed)) if known == cell && !sealed)
    }

    /// The camera's eye and look direction this frame, drawing frame.
    pub(crate) fn camera(&self) -> Option<(Vec3, Vec3)> {
        self.camera
    }

    /// The camera's view and projection, drawing frame, this frame.
    pub(crate) fn view_matrices(&self) -> Option<(glam::Mat4, glam::Mat4)> {
        self.view_matrices
    }

    /// The server object drawn under window pixel (`x`, `y`) in the last
    /// world frame (OpenAC `RetailSelectionScene.Pick`), leaving out
    /// `skip_guid`.
    pub(crate) fn pick(
        &mut self,
        x: f32,
        y: f32,
        skip_guid: u32,
    ) -> Option<crate::selection::SelectionHit> {
        let (view, projection) = self.view_matrices?;
        let (eye, _) = self.camera?;
        let camera = crate::selection::PickCamera {
            inverse_view_projection: (projection * view).inverse(),
            eye,
            width: self.size.0 as f32,
            height: self.size.1 as f32,
        };
        let content = self.content.get_if_open()?;
        let mut geometry = crate::selection::DatsGeometry {
            cache: &mut self.selection_geometry,
            dats: &content.dats,
        };
        self.selection.pick(x, y, &camera, skip_guid, &mut geometry)
    }

    /// Flash an object picked in the world.
    pub(crate) fn begin_selection_flash(&mut self, guid: u32, local_id: u32) {
        let now = self.selection_epoch.elapsed().as_secs_f64();
        self.selection.begin_lighting_pulse(guid, local_id, now);
    }

    /// A position from the session in the drawing frame.
    /// The "dump nearby" lines (Ctrl+F3): around the player (or the
    /// camera's eye without one), the near statics and server objects
    /// drawn and the camera collision's objects.
    pub(crate) fn nearby_dump(&self, scene: Option<&Scene>) -> Vec<String> {
        use crate::nearby_dump::{NearbyEntity, NearbyShadow, nearby_dump};
        let player = scene.and_then(|s| s.player()).and_then(|p| p.position);
        let observer = player
            .and_then(|p| {
                self.frame_position(p.cell_id, Vec3::new(p.origin.x, p.origin.y, p.origin.z))
            })
            .or(self.camera.map(|(eye, _)| eye))
            .unwrap_or(Vec3::ZERO);
        let mut entities = Vec::new();
        for (coord, resident) in &self.residents {
            let Some(near) = resident.near.as_ref() else {
                continue;
            };
            let offset = Mat4::from_translation(landblock_offset(&self.origin, *coord));
            for (i, s) in near.statics.iter().enumerate() {
                let world = offset * s.transform;
                entities.push(NearbyEntity {
                    id: (u32::from(coord.x) << 24) | (u32::from(coord.y) << 16) | i as u32,
                    source: s.model_id,
                    position: world.w_axis.truncate(),
                    scale: world.x_axis.truncate().length(),
                });
            }
        }
        for o in scene.into_iter().flat_map(Scene::objects) {
            let Some(p) = o.position else {
                continue;
            };
            if let Some(position) =
                self.frame_position(p.cell_id, Vec3::new(p.origin.x, p.origin.y, p.origin.z))
            {
                entities.push(NearbyEntity {
                    id: o.guid,
                    source: o.setup_id.unwrap_or(0),
                    position,
                    scale: o.scale.unwrap_or(1.0),
                });
            }
        }
        let (shadows, total) = self.camera_collision.shadows(&self.origin).map_or(
            (Vec::new(), 0),
            |(entries, offset)| {
                let total = entries.len();
                let near = entries
                    .into_iter()
                    .map(|e| NearbyShadow {
                        id: e.entity_id,
                        kind: format!("{:?}", e.collision_type),
                        radius: e.radius,
                        height: e.cyl_height,
                        position: e.position + offset,
                    })
                    .collect();
                (near, total)
            },
        );
        nearby_dump(observer, self.origin.center(), &entities, &shadows, total)
    }

    pub(crate) fn frame_position(&self, cell_id: u32, local: Vec3) -> Option<Vec3> {
        self.origin
            .is_known()
            .then(|| to_frame(&self.origin, cell_id, local))
    }

    /// The region's ambient sounds, once, when the loader has read them.
    pub(crate) fn take_ambient_region(&mut self) -> Option<AmbientRegion> {
        self.ambient_region.take()
    }

    /// The environment, for the server's changes and the debug cycles.
    pub(crate) fn environment_mut(&mut self) -> Option<&mut WindowEnvironment> {
        self.environment.as_mut()
    }

    /// The server's environment change: a fog override (kept, and applied
    /// when the region's sky arrives if it has not yet) or a sound cue.
    pub(crate) fn admin_environs(&mut self, change: u32) -> ac_core::sky::EnvironmentEffect {
        use ac_core::sky::{EnvironOverride, EnvironSoundCue, EnvironmentEffect};
        if EnvironOverride::from_raw(change).is_some() {
            self.pending_fog = Some(change);
        }
        if let Some(env) = self.environment.as_mut() {
            return env.apply_admin_environs(change);
        }
        match (
            EnvironOverride::from_raw(change),
            EnvironSoundCue::from_raw(change),
        ) {
            (Some(fog), _) => EnvironmentEffect::FogOverride(fog),
            (None, Some(cue)) => EnvironmentEffect::SoundCue(cue),
            (None, None) => EnvironmentEffect::Unknown(change),
        }
    }

    /// The in-game date and hour at `now` on `scene`'s clock (the map's
    /// date line); `None` before the region's calendar is known.
    pub(crate) fn calendar_date(
        &self,
        scene: Option<&Scene>,
        now: Instant,
    ) -> Option<ac_core::world::CalendarDate> {
        self.environment
            .as_ref()?
            .calendar_date(scene.and_then(|s| s.game_ticks(now)), now)
    }

    /// The session handed the window a new world (attach, entering or
    /// leaving the world): the next login is revealed afresh.
    pub(crate) fn restart_login_reveal(&mut self) {
        self.reveal.restart();
        self.reveal_started = None;
    }

    /// The player's cell is known: start withholding the world until the
    /// area is loaded (once per login).
    fn begin_reveal(&mut self, cell: u32) {
        if self.reveal.is_withholding() || self.reveal.is_complete() {
            return;
        }
        // The reveal is taken out while the facts borrow the rest.
        let mut reveal = std::mem::take(&mut self.reveal);
        reveal.begin(&mut self.reveal_facts(), cell);
        self.reveal = reveal;
        self.reveal_started = Some(Instant::now());
    }

    fn advance_reveal(&mut self) {
        let was_withholding = self.reveal.is_withholding();
        let mut reveal = std::mem::take(&mut self.reveal);
        reveal.advance(&mut self.reveal_facts());
        self.reveal = reveal;
        if was_withholding && self.reveal.is_complete() {
            self.revealed_after = self.reveal_started.map(|t| t.elapsed());
        }
    }

    fn reveal_facts(&self) -> RevealState<'_> {
        RevealState {
            residents: &self.residents,
            models: &self.models,
            walk: &self.walk,
            server_models_ready: self.server_models_ready,
            retiring: !self.retiring.is_empty(),
            unavailable: self.failure.is_some(),
            window: apply_reveal_radius_override(
                RevealWindow {
                    near_radius: self.near_radius,
                    far_radius: self.far_radius,
                },
                self.reveal_radius_override,
            ),
        }
    }

    /// How long the world was withheld, once, when it is first shown.
    pub(crate) fn take_revealed_after(&mut self) -> Option<Duration> {
        self.revealed_after.take()
    }

    /// The world is being withheld until the area around the player has
    /// loaded.
    pub(crate) fn is_withholding(&self) -> bool {
        self.reveal.is_withholding()
    }

    /// The login reveal's readiness barrier has passed (the area around
    /// the player loaded and the world shown) since the last restart.
    pub(crate) fn has_revealed(&self) -> bool {
        self.reveal.is_complete()
    }

    // ── The loader's results ───────────────────────────────────────────

    /// Adopt what the loader finished, within the frame's streaming work
    /// budget (widened while a reveal holds for the area); what does not
    /// fit waits for the next frame.
    fn pump_loader(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        use crate::work_budget::{
            InstantClock, StreamingWorkAdmission, StreamingWorkBudgetOptions, StreamingWorkMeter,
        };
        let ordinary = self.work_budget.to_budget().unwrap_or_else(|_| {
            StreamingWorkBudgetOptions::default()
                .to_budget()
                .expect("the authored profile is valid")
        });
        // Landblocks that left the window are released with what adoption
        // leaves of an ordinary frame's budget, counted from the frame's
        // start: a reveal's widened budget is for its destination.
        let Ok(mut retirement) = StreamingWorkMeter::new(ordinary, InstantClock::default(), false)
        else {
            return;
        };
        let mut budget = ordinary;
        if self.reveal.upload_priority() {
            budget =
                budget.widen_for_destination_hold(self.work_budget.hold_destination_ceiling_ms);
        }
        let Ok(mut meter) = StreamingWorkMeter::new(budget, InstantClock::default(), false) else {
            return;
        };
        loop {
            // A region's atlas goes up a piece at a time, before anything
            // the loader sent after it.
            if let Some(cost) = self
                .atlas_upload
                .as_ref()
                .and_then(atlas_upload::AtlasUpload::next_cost)
            {
                match meter.try_reserve(cost, "terrain atlas", false) {
                    Ok(StreamingWorkAdmission::Yielded) | Err(_) => break,
                    Ok(_) => {}
                }
                self.advance_atlas(device, queue);
                let _ = meter.complete();
                continue;
            }
            let Some(item) = self.held_loaded.take().or_else(|| self.loader.try_next()) else {
                break;
            };
            match meter.try_reserve(loaded_cost(&item), loaded_stage(&item), false) {
                Ok(StreamingWorkAdmission::Yielded) | Err(_) => {
                    self.held_loaded = Some(item);
                    break;
                }
                Ok(_) => {}
            }
            self.adopt_loaded(device, queue, item);
            let _ = meter.complete();
        }
        let _ = meter.finish_frame();
        self.advance_retirement(&mut retirement);
        let _ = retirement.finish_frame();
        self.stats.landblocks = self.residents.values().filter(|r| r.tile.is_some()).count();
        self.stats.meshes = self.meshes.len();
        self.stats.surfaces = self.surfaces.len();
    }

    fn adopt_loaded(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, item: Loaded) {
        {
            match item {
                Loaded::Failed(reason) => self.failure = Some(reason),
                Loaded::Atlas(atlas) => self.begin_atlas(device, atlas),
                Loaded::Landblock(block) => self.upload_landblock(device, *block),
                Loaded::NoLandblock(coord) => {
                    if let Some(r) = self.residents.get_mut(&coord) {
                        r.missing = true;
                    }
                }
                Loaded::Near(coord, near) => self.upload_near(device, coord, *near),
                Loaded::CameraCollision { center, engine } => {
                    if let Err(error) = self.camera_collision.arrived(center, engine) {
                        self.notices.push(format!(
                            "camera collision around {:08X}: {error}",
                            center.terrain_id()
                        ));
                    }
                }
                Loaded::Icon(icon) => self.icon = Some(icon),
                Loaded::Surface(surface) => {
                    self.requested_surfaces.remove(&surface.id);
                    let held = self.surfaces.contains_key(&surface.id);
                    self.looks.surface_arrived(surface.id, held);
                    self.upload_surface(device, queue, &surface);
                }
                Loaded::GfxObj(gfx) => {
                    self.requested_gfx.remove(&gfx.id);
                    let key = u64::from(gfx.id);
                    self.upload_mesh(device, key, &gfx);
                }
                Loaded::Model { id, parts, info } => {
                    self.requested_models.arrived(id);
                    self.models.insert(id, parts);
                    self.model_info.insert(id, info);
                }
                Loaded::Variant { id, surfaces } => self.looks.variant_arrived(id, surfaces),
                Loaded::Appearance { key, parts, bounds } => {
                    let surfaces = &self.surfaces;
                    // One that crossed a forget in flight is refused: the
                    // loader forgets it and what it named that is gone, and
                    // the next frame asks for it again.
                    if let Some(forget) = self
                        .looks
                        .appearance_arrived(*key, parts, bounds, |h| surfaces.contains_key(&h))
                    {
                        self.loader.request(forget);
                    }
                }
                Loaded::Region {
                    sky,
                    ambient,
                    zero_time_of_year,
                } => {
                    if let Some(sky) = sky.as_deref() {
                        self.sky_models = sky
                            .day_groups
                            .iter()
                            .flat_map(|g| {
                                g.sky_objects.iter().map(|o| o.gfx_obj_id).chain(
                                    g.sky_times
                                        .iter()
                                        .flat_map(|t| t.replaces.iter().map(|r| r.gfx_obj_id)),
                                )
                            })
                            .filter(|&id| id != 0)
                            .collect();
                    }
                    self.environment = sky.and_then(|sky| {
                        WindowEnvironment::new(*sky, zero_time_of_year, self.sky_options)
                    });
                    if let (Some(env), Some(fog)) = (self.environment.as_mut(), self.pending_fog) {
                        env.apply_admin_environs(fog);
                    }
                    self.ambient_region = Some(*ambient);
                }
            }
        }
    }

    /// The terrain's bind group: the atlas with the quality's anisotropic
    /// filtering; alpha maps clamp, without mips.
    fn rebuild_terrain_group(&mut self, device: &wgpu::Device) {
        let Some((terrain, alpha, tiling)) = self.terrain_atlas.as_ref() else {
            return;
        };
        let terrain_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("terrain"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::Repeat,
            address_mode_w: wgpu::AddressMode::Repeat,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::MipmapFilterMode::Linear,
            anisotropy_clamp: self.anisotropic_level.clamp(1, 16),
            ..Default::default()
        });
        let alpha_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("terrain alpha"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        self.terrain_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("terrain"),
            layout: &self.terrain_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(terrain),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&terrain_sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(alpha),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::Sampler(&alpha_sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: tiling.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: wgpu::BindingResource::TextureView(self.detail.landscape_view()),
                },
                wgpu::BindGroupEntry {
                    binding: 6,
                    resource: wgpu::BindingResource::Sampler(self.detail.sampler()),
                },
                wgpu::BindGroupEntry {
                    binding: 7,
                    resource: self.detail.params().as_entire_binding(),
                },
            ],
        }));
    }

    fn upload_landblock(&mut self, device: &wgpu::Device, block: LandblockData) {
        let Some(r) = self.residents.get_mut(&block.coord) else {
            // Out of range by the time it arrived.
            return;
        };
        let vertices = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("terrain tile"),
            contents: bytemuck::cast_slice(&block.vertices),
            usage: wgpu::BufferUsages::VERTEX,
        });
        r.tile = Some(TerrainTile {
            vertices,
            min_z: block.min_z,
            max_z: block.max_z,
            ground: block.ground,
        });
        let offset = landblock_offset(&self.origin, block.coord);
        self.collision
            .register_terrain(block.coord.terrain_id(), offset);
    }

    fn upload_near(&mut self, device: &wgpu::Device, coord: LandblockCoord, data: NearData) {
        let wanted = self
            .residents
            .get(&coord)
            .is_some_and(|r| r.tier == Tier::Near && r.near.is_none());
        if !wanted {
            return;
        }
        let NearData {
            statics,
            scenery,
            buildings,
            mut cells,
            membership_failures,
        } = data;
        for failure in membership_failures {
            self.notices.push(format!(
                "cells of landblock {:08X}: {failure}",
                coord.terrain_id()
            ));
        }
        for cell in &mut cells {
            if let Some(mesh) = cell.mesh.take() {
                self.upload_mesh(device, cell_mesh_key(cell.id), &mesh);
            }
        }
        let mut model_ids: Vec<u32> = statics
            .iter()
            .chain(&scenery)
            .map(|s| s.model_id)
            .chain(
                cells
                    .iter()
                    .flat_map(|c| c.statics.iter().map(|s| s.model_id)),
            )
            .collect();
        model_ids.sort_unstable();
        model_ids.dedup();
        // A model released while this landblock was on its way may have
        // been passed over by the loader: ask for it.
        let models = &self.models;
        for id in self
            .requested_models
            .landblock_arrived(model_ids.iter().copied(), |id| models.contains_key(&id))
        {
            self.loader.request(LoadRequest::Model(id));
        }
        let light_statics = statics.clone();
        let scenery_start = statics.iter().filter(|s| !s.is_building_shell).count();
        let placed: Vec<StaticInstance> = statics
            .into_iter()
            .filter(|s| !s.is_building_shell)
            .chain(scenery)
            .collect();
        let mut by_cell = vec![Vec::new(); 64];
        for (i, s) in placed.iter().enumerate() {
            let p = s.transform.w_axis;
            let cell = |v: f32| ((v / 24.0).floor().max(0.0) as usize).min(7);
            by_cell[cell(p.x) * 8 + cell(p.y)].push(i);
        }
        let mut near = NearResident {
            statics: placed,
            by_cell,
            buildings,
            cells,
            model_ids,
            light_statics,
            scenery_start,
        };
        self.install_near(coord, &mut near);
        if self.animation_source().is_some() {
            self.register_static_animations(coord, &near);
            if self.ensure_effects() {
                self.register_static_effects(coord, &near);
            }
        }
        if let Some(r) = self.residents.get_mut(&coord) {
            r.near = Some(near);
            r.near_requested = false;
        }
    }

    fn upload_surface(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, s: &SurfaceData) {
        let texture = crate::surface_image::create_texture(device, queue, &s.texture, "surface");
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let material = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("material"),
            size: size_of::<Material>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(
            &material,
            0,
            bytemuck::bytes_of(&Material {
                alpha_cutoff: s.alpha_cutoff,
                opacity: s.opacity,
                fog_enabled: if s.disable_fog { 0.0 } else { 1.0 },
                blended: if matches!(s.translucency, Translucency::Opaque | Translucency::ClipMap) {
                    0.0
                } else {
                    1.0
                },
            }),
        );
        let group = |sampler: &wgpu::Sampler| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("material"),
                layout: &self.material_layout,
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
                        resource: material.as_entire_binding(),
                    },
                ],
            })
        };
        // A world texture with its mip chain samples like OpenAC's world
        // texture arrays; one level (sky, particles, solid colours) like its
        // single textures.
        let (clamp, repeat) = if s.texture.level_count() > 1 {
            (&self.world_clamp_sampler, &self.world_repeat_sampler)
        } else {
            (&self.clamp_sampler, &self.repeat_sampler)
        };
        let surface = SurfaceGpu {
            clamp: group(clamp),
            repeat: group(repeat),
            translucency: s.translucency,
            untextured: s.untextured,
            opacity: s.opacity,
            luminosity: s.luminosity,
            diffuse: s.diffuse,
            disable_fog: s.disable_fog,
            bytes: s.texture.gpu_bytes().max(1),
        };
        self.surfaces.insert(s.id, surface);
    }

    fn upload_mesh(&mut self, device: &wgpu::Device, key: MeshKey, gfx: &GfxObjData) {
        let (vertices, indices) = if gfx.indices.is_empty() {
            (
                vertex_buffer(device, "empty", 16),
                vertex_buffer(device, "empty", 16),
            )
        } else {
            let buffer = |label: &str, bytes: &[u8], usage: wgpu::BufferUsages| {
                device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some(label),
                    contents: bytes,
                    usage,
                })
            };
            (
                buffer(
                    "mesh vertices",
                    bytemuck::cast_slice(&gfx.vertices),
                    wgpu::BufferUsages::VERTEX,
                ),
                buffer(
                    "mesh indices",
                    bytemuck::cast_slice(&gfx.indices),
                    wgpu::BufferUsages::INDEX,
                ),
            )
        };
        let gpu = GfxGpu::new(vertices, indices, gfx, |id| {
            self.surfaces.get(&id).map(|s| s.translucency)
        });
        self.meshes.insert(key, gpu);
    }

    /// The colour the frame clears to: the fog colour once the sky is
    /// known, else `fallback`.
    pub(crate) fn clear_color(&self, fallback: wgpu::Color) -> wgpu::Color {
        if self.portal.hides_world() {
            return crate::portal_space::tunnel::PORTAL_SPACE_CLEAR;
        }
        self.clear.unwrap_or(fallback)
    }

    /// The surface a sub-mesh of a part with `variant` is drawn with.
    fn surface_of(&self, variant: u32, surface_id: u32) -> Option<&SurfaceGpu> {
        let handle = if variant == 0 {
            surface_id
        } else {
            self.looks.handle(variant, surface_id).unwrap_or(surface_id)
        };
        self.surfaces.get(&handle)
    }

    // ── Drawing ────────────────────────────────────────────────────────

    /// Draw the prepared frame into `target` (which must match the window
    /// size), clearing it to `clear`: the draw stream in order.
    pub(crate) fn draw(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
        timestamps: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) {
        self.draw_passes(encoder, target, clear, timestamps);
        self.icon_probes.resolve(encoder);
    }

    fn draw_passes(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
        timestamps: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) {
        // The atmospheric rendering draws the world through its own
        // targets and composes it into `target`.
        if let Some(a) = self
            .atmosphere
            .as_ref()
            .filter(|a| a.world_targets().is_some())
        {
            self.draw_atmospheric(encoder, target, clear, timestamps, a);
            return;
        }
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("world"),
            // Multisampled, the pass draws into its own target and resolves
            // into `target`.
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: self.msaa_color.as_ref().unwrap_or(target),
                depth_slice: None,
                resolve_target: self.msaa_color.as_ref().map(|_| target),
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(clear),
                    store: if self.msaa_color.is_some() {
                        wgpu::StoreOp::Discard
                    } else {
                        wgpu::StoreOp::Store
                    },
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: &self.depth,
                depth_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            timestamp_writes: timestamps,
            occlusion_query_set: self.icon_probes.query_set(),
            ..Default::default()
        });
        // Behind portal space the frame is the tunnel's (built in place of
        // the world's), whatever the reveal.
        if self.reveal.is_withholding() && !self.portal.hides_world() {
            // The world is withheld until the area has loaded: the pass
            // only clears.
            return;
        }
        let pipelines = self.plain_pipelines();
        self.draw_steps(&mut pass, &pipelines);
        self.draw_world_lines(&mut pass, &pipelines);
        self.draw_world_triangles(&mut pass, &pipelines);
        self.draw_icon_probes(&mut pass, &pipelines);
    }

    /// The draw stream in order, with `p`'s pipelines.
    fn draw_steps(&self, pass: &mut wgpu::RenderPass<'_>, p: &shadows::PassPipelines<'_>) {
        // The vertex and index buffers bound so far in this pass, kept
        // across the steps: a buffer already bound is not bound again.
        let mut buffers = BufferBinds::default();
        for step in &self.frame.steps {
            match step {
                Step::Sky { range, .. } => self.draw_sky(pass, p, &mut buffers, range.clone()),
                Step::Terrain(range) => self.draw_terrain(pass, p, &mut buffers, range.clone()),
                Step::Groups(range) => self.draw_groups(pass, p, &mut buffers, range.clone()),
                Step::Alpha(range) => self.draw_alpha(pass, p, &mut buffers, range.clone()),
                Step::Portals(range) => {
                    pass.set_pipeline(p.portal);
                    pass.set_bind_group(0, &self.globals_group, &[]);
                    buffers.vertex(pass, 0, &self.portal_vertices);
                    pass.draw(range.clone(), 0..1);
                }
            }
        }
    }

    fn draw_sky<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &shadows::PassPipelines<'_>,
        buffers: &mut BufferBinds<'a>,
        range: std::ops::Range<usize>,
    ) {
        for i in range {
            let template = self.frame.sky[i];
            let draw = &self.sky_templates[template];
            let (Some(gfx), Some(surface)) = (
                self.meshes.get(&u64::from(draw.gfx_id)),
                self.surfaces.get(&draw.surface),
            ) else {
                continue;
            };
            let Some(sub) = gfx.sub_meshes.get(draw.sub_index) else {
                continue;
            };
            pass.set_pipeline(Self::sky_pipeline_for(p, draw.gfx_id, draw.additive));
            pass.set_bind_group(0, &self.sky_group, &[(i * SKY_PARAMS_STRIDE) as u32]);
            pass.set_bind_group(
                1,
                if draw.repeat {
                    &surface.repeat
                } else {
                    &surface.clamp
                },
                &[],
            );
            buffers.mesh(pass, &gfx.vertices, &gfx.indices);
            pass.draw_indexed(
                sub.first_index..sub.first_index + sub.index_count,
                sub.base_vertex,
                0..1,
            );
        }
    }

    fn draw_terrain<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &shadows::PassPipelines<'_>,
        buffers: &mut BufferBinds<'a>,
        range: std::ops::Range<usize>,
    ) {
        let Some(terrain) = &self.terrain_group else {
            return;
        };
        pass.set_pipeline(p.terrain);
        pass.set_bind_group(0, &self.globals_group, &[]);
        pass.set_bind_group(1, terrain, &[]);
        if let Some(atmosphere) = p.atmosphere {
            pass.set_bind_group(2, atmosphere, &[]);
        }
        buffers.vertex(pass, 1, &self.terrain_instances);
        for i in range {
            let draw = &self.frame.terrain_draws[i];
            let Some(tile) = self
                .residents
                .get(&draw.block)
                .and_then(|r| r.tile.as_ref())
            else {
                continue;
            };
            buffers.vertex(pass, 0, &tile.vertices);
            let i = i as u32;
            for run in draw.vertex_runs() {
                pass.draw(run, i..i + 1);
            }
        }
    }

    fn draw_groups<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &shadows::PassPipelines<'a>,
        buffers: &mut BufferBinds<'a>,
        range: std::ops::Range<usize>,
    ) {
        pass.set_bind_group(0, &self.globals_group, &[]);
        if let Some(atmosphere) = p.atmosphere {
            pass.set_bind_group(3, atmosphere, &[]);
        }
        buffers.vertex(pass, 1, &self.instances);
        let mut state = MeshPassState::default();
        for group in &self.frame.groups[range] {
            let Some(gfx) = self.meshes.get(&group.mesh) else {
                continue;
            };
            buffers.mesh(pass, &gfx.vertices, &gfx.indices);
            for (i, sub) in gfx.sub_meshes.iter().enumerate() {
                if gfx.sub_blended.get(i).copied().unwrap_or(false) {
                    continue;
                }
                self.draw_sub(
                    pass,
                    p,
                    &mut state,
                    sub,
                    group.variant,
                    group.shell,
                    group.instances.clone(),
                    false,
                );
            }
        }
    }

    /// Blended sub-meshes and particles, in list order. Neighbouring
    /// particle quads with the same texture and blend, and neighbouring
    /// draws of the same blended sub-mesh, are drawn as one instanced draw
    /// (their instances are consecutive, and a draw's instances are drawn
    /// in order, so the picture is the same).
    fn draw_alpha<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &shadows::PassPipelines<'a>,
        buffers: &mut BufferBinds<'a>,
        range: std::ops::Range<usize>,
    ) {
        pass.set_bind_group(0, &self.globals_group, &[]);
        let items = &self.frame.alpha[range];
        let mut state = MeshPassState::default();
        let mut i = 0;
        while i < items.len() {
            match items[i] {
                AlphaItem::Mesh(draw) => {
                    let end = i + alpha_mesh_run(&items[i..]);
                    let count = (end - i) as u32;
                    i = end;
                    let Some(gfx) = self.meshes.get(&draw.mesh) else {
                        continue;
                    };
                    let Some(sub) = gfx.sub_meshes.get(draw.sub as usize) else {
                        continue;
                    };
                    buffers.mesh(pass, &gfx.vertices, &gfx.indices);
                    buffers.vertex(pass, 1, &self.instances);
                    self.draw_sub(
                        pass,
                        p,
                        &mut state,
                        sub,
                        draw.variant,
                        draw.shell,
                        draw.instance..draw.instance + count,
                        draw.faded,
                    );
                }
                AlphaItem::Billboard(first) => {
                    let mut end = i + 1;
                    while let Some(AlphaItem::Billboard(next)) = items.get(end) {
                        let consecutive = next.instance == first.instance + (end - i) as u32;
                        if !consecutive
                            || next.surface != first.surface
                            || next.additive != first.additive
                        {
                            break;
                        }
                        end += 1;
                    }
                    let count = (end - i) as u32;
                    i = end;
                    let material = if first.surface == 0 {
                        Some(&self.particle_gpu.white)
                    } else {
                        self.surfaces.get(&first.surface).map(|s| &s.clamp)
                    };
                    let Some(material) = material else {
                        continue;
                    };
                    // Set directly: the mesh draws set everything again.
                    state = MeshPassState::default();
                    pass.set_pipeline(&p.billboard[usize::from(first.additive)]);
                    pass.set_bind_group(1, material, &[]);
                    buffers.vertex(pass, 0, &self.particle_gpu.billboards);
                    pass.draw(0..6, first.instance..first.instance + count);
                }
                AlphaItem::ParticleMesh(draw) => {
                    i += 1;
                    let Some(gfx) = self.meshes.get(&u64::from(draw.gfx_obj_id)) else {
                        continue;
                    };
                    let Some(sub) = gfx.sub_meshes.get(draw.sub as usize) else {
                        continue;
                    };
                    let Some(surface) = self.surfaces.get(&sub.surface_id) else {
                        continue;
                    };
                    let Some(pipeline) = p.particle_meshes.get(&(draw.blend, draw.cull)) else {
                        continue;
                    };
                    // Set directly: the mesh draws set everything again.
                    state = MeshPassState::default();
                    pass.set_pipeline(pipeline);
                    pass.set_bind_group(
                        1,
                        if sub.repeat {
                            &surface.repeat
                        } else {
                            &surface.clamp
                        },
                        &[],
                    );
                    buffers.mesh(pass, &gfx.vertices, &gfx.indices);
                    buffers.vertex(pass, 1, &self.particle_gpu.mesh_instances);
                    pass.draw_indexed(
                        sub.first_index..sub.first_index + sub.index_count,
                        sub.base_vertex,
                        draw.instance..draw.instance + 1,
                    );
                }
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_sub<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'_>,
        p: &shadows::PassPipelines<'a>,
        state: &mut MeshPassState<'a>,
        sub: &SubMeshRange,
        variant: u32,
        shell: bool,
        instances: std::ops::Range<u32>,
        faded: bool,
    ) {
        if let Some(atmosphere) = p.atmosphere
            && !state.atmosphere_set
        {
            pass.set_bind_group(3, atmosphere, &[]);
            state.atmosphere_set = true;
        }
        self.draw_sub_with(
            p.meshes,
            p.clip_map_meshes,
            pass,
            state,
            sub,
            variant,
            shell,
            instances,
            faded,
        );
    }

    /// [`Self::draw_sub`] with the pipelines of `meshes` (the world's, or
    /// the viewports' single-sampled ones); with `clip_map_meshes` a
    /// clip-mapped surface takes its own pipeline (the atmospheric wind's
    /// leaves) instead of the solid one.
    #[allow(clippy::too_many_arguments)]
    fn draw_sub_with<'a>(
        &'a self,
        meshes: &'a HashMap<(Translucency, crate::meshing::Cull), wgpu::RenderPipeline>,
        clip_map_meshes: bool,
        pass: &mut wgpu::RenderPass<'_>,
        state: &mut MeshPassState<'a>,
        sub: &SubMeshRange,
        variant: u32,
        shell: bool,
        instances: std::ops::Range<u32>,
        faded: bool,
    ) {
        let Some(surface) = self.surface_of(variant, sub.surface_id) else {
            return;
        };
        if !untextured_subset_draws(shell, surface.untextured) {
            return;
        }
        let kind = match faded_translucency(surface.translucency, faded) {
            Translucency::Opaque
                if clip_map_meshes && surface.translucency == Translucency::ClipMap =>
            {
                Translucency::ClipMap
            }
            kind => kind,
        };
        let pipeline = &meshes[&(kind, sub.cull)];
        if !state.pipeline.is_some_and(|p| std::ptr::eq(p, pipeline)) {
            pass.set_pipeline(pipeline);
            state.pipeline = Some(pipeline);
        }
        if !state.detail_set {
            pass.set_bind_group(2, &self.detail.group, &[]);
            state.detail_set = true;
        }
        let material = if sub.repeat {
            &surface.repeat
        } else {
            &surface.clamp
        };
        if !state.material.is_some_and(|m| std::ptr::eq(m, material)) {
            pass.set_bind_group(1, material, &[]);
            state.material = Some(material);
        }
        pass.draw_indexed(
            sub.first_index..sub.first_index + sub.index_count,
            sub.base_vertex,
            instances,
        );
    }
}

/// What the mesh draws of one pass have set so far, so that a pipeline or
/// a texture already set is not set again: every set costs wgpu's
/// validation and a driver call, and neighbouring draws mostly share both.
/// Anything else that sets the pass's state starts a new one.
#[derive(Default)]
pub(crate) struct MeshPassState<'a> {
    pipeline: Option<&'a wgpu::RenderPipeline>,
    material: Option<&'a wgpu::BindGroup>,
    detail_set: bool,
    atmosphere_set: bool,
}

/// How many blended mesh draws from the start of `items` are one instanced
/// draw: the same sub-mesh drawn the same way, on consecutive instances.
fn alpha_mesh_run(items: &[AlphaItem]) -> usize {
    let Some(AlphaItem::Mesh(first)) = items.first() else {
        return 0;
    };
    1 + items[1..]
        .iter()
        .enumerate()
        .take_while(|(k, item)| match item {
            AlphaItem::Mesh(next) => {
                next.instance == first.instance + 1 + *k as u32
                    && (next.mesh, next.sub, next.variant, next.shell, next.faded)
                        == (
                            first.mesh,
                            first.sub,
                            first.variant,
                            first.shell,
                            first.faded,
                        )
            }
            _ => false,
        })
        .count()
}

/// The vertex buffers (slots 0 and 1, whole) and the index buffer (whole,
/// 32-bit) a pass has bound, so that a buffer already bound is not bound
/// again: every bind costs wgpu's validation and tracking when the frame's
/// commands are encoded, and consecutive draws often share the instance
/// buffer or a mesh.
#[derive(Default)]
pub(crate) struct BufferBinds<'a> {
    vertex: [Option<&'a wgpu::Buffer>; 2],
    index: Option<&'a wgpu::Buffer>,
}

impl<'a> BufferBinds<'a> {
    /// Bind `buffer` whole to vertex `slot` (0 or 1) unless it is bound there.
    pub(crate) fn vertex(
        &mut self,
        pass: &mut wgpu::RenderPass<'_>,
        slot: usize,
        buffer: &'a wgpu::Buffer,
    ) {
        if newly_bound(&mut self.vertex[slot], buffer) {
            pass.set_vertex_buffer(slot as u32, buffer.slice(..));
        }
    }

    /// Bind a mesh's vertices (slot 0) and 32-bit indices, each unless it
    /// is bound.
    pub(crate) fn mesh(
        &mut self,
        pass: &mut wgpu::RenderPass<'_>,
        vertices: &'a wgpu::Buffer,
        indices: &'a wgpu::Buffer,
    ) {
        self.vertex(pass, 0, vertices);
        if newly_bound(&mut self.index, indices) {
            pass.set_index_buffer(indices.slice(..), wgpu::IndexFormat::Uint32);
        }
    }
}

/// Make `next` what `bound` holds; true when it held something else (the
/// bind is needed).
fn newly_bound<'a, T>(bound: &mut Option<&'a T>, next: &'a T) -> bool {
    if bound.is_some_and(|b| std::ptr::eq(b, next)) {
        return false;
    }
    *bound = Some(next);
    true
}

/// The player stands in an outdoor land cell: the sky's effects and the
/// weather's particles run.
fn player_outdoors(scene: Option<&Scene>) -> bool {
    scene
        .and_then(Scene::player)
        .and_then(|p| p.position)
        .is_some_and(|p| p.cell_id & 0xFFFF < 0x0100)
}

/// Whether a surface blends (drawn sorted after the opaque world).
/// The pipeline a sub-mesh of `surface` translucency is drawn with: clip
/// maps share the solid one; a solid or clip-map sub-mesh of a translucent
/// instance (`faded`) is alpha blended (OpenAC `TryClassifyBatch`).
fn faded_translucency(surface: Translucency, faded: bool) -> Translucency {
    match surface {
        Translucency::Opaque | Translucency::ClipMap if faded => Translucency::AlphaBlend,
        Translucency::ClipMap => Translucency::Opaque,
        other => other,
    }
}

fn is_blended(t: Translucency) -> bool {
    !matches!(t, Translucency::Opaque | Translucency::ClipMap)
}

/// What a building's part 0 draws at its distance from `eye`: the model of
/// the level its ladder chooses (0: the building is not drawn) and, when
/// that level turns to face the camera (modes 2 to 5), part 0's placement
/// turned toward the eye (`world` is the building's placement in the
/// drawing frame).
fn shell_part0(
    b: &BuildingData,
    world: Mat4,
    eye: Vec3,
    degrade: &DegradeSettings,
) -> (u32, Option<Mat4>) {
    if b.levels.is_empty() {
        return (b.part0.0, None);
    }
    let part_world = world * b.part0.1;
    let center = part_world.transform_point3(b.sort_center);
    let scale_z = if b.part0_scale_z > 0.0 {
        b.part0_scale_z
    } else {
        1.0
    };
    let distance = (center - eye).length() / scale_z;
    let Some(level) = b
        .levels
        .get(select_building_level(&b.levels, distance, degrade))
    else {
        return (0, None);
    };
    let turned = (2..=5)
        .contains(&level.mode)
        .then(|| camera_facing_placement(part_world, b.sort_center, eye, level.mode));
    (level.gfx_obj_id, turned)
}

/// Every dat static of a near landblock that may own effects, with its
/// key: the landblock's own statics and its procedural scenery (cell 0, by
/// their index in `statics`, the keys their animations use), the building
/// shells and the interior cells' statics. Scenery is included as OpenAC
/// starts every dat-hydrated entity of a landblock, scenery among them.
fn static_effect_candidates(
    landblock: u32,
    near: &NearResident,
) -> impl Iterator<Item = (StaticKey, &StaticInstance)> {
    let key = move |cell: u32, index: usize| StaticKey {
        landblock,
        cell,
        index: index as u32,
    };
    let own = near
        .statics
        .iter()
        .enumerate()
        .map(move |(i, s)| (key(0, i), s));
    let shells = near
        .light_statics
        .iter()
        .enumerate()
        .filter(|(_, s)| s.is_building_shell)
        .map(move |(i, s)| (key(crate::effects::SHELL_CELL, i), s));
    let cells = near.cells.iter().flat_map(move |c| {
        c.statics
            .iter()
            .enumerate()
            .map(move |(i, s)| (key(c.id, i), s))
    });
    own.chain(shells).chain(cells)
}

/// The lighting before the region's sky has arrived: the built-in sky at
/// noon, no fog override.
fn fallback_environment() -> EnvironmentFrame {
    let keyframe = SkyStateProvider::default_sky().interpolate(0.5);
    EnvironmentFrame {
        day_fraction: 0.5,
        keyframe,
        atmosphere: WeatherSystem::new().snapshot(&keyframe, false),
        environ_override: false,
    }
}

fn globals_group(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    globals: &wgpu::Buffer,
    scene: &wgpu::Buffer,
    points: &wgpu::Buffer,
    clips: &wgpu::Buffer,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("globals"),
        layout,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: globals.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: scene.as_entire_binding(),
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
    })
}

fn sky_group(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    params: &wgpu::Buffer,
    scene: &wgpu::Buffer,
    clips: &wgpu::Buffer,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("sky"),
        layout,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: params,
                    offset: 0,
                    size: wgpu::BufferSize::new(size_of::<SkyParamsGpu>() as u64),
                }),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: scene.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: clips.as_entire_binding(),
            },
        ],
    })
}

/// What the renderer has loaded, as the reveal rule asks it.
struct RevealState<'a> {
    residents: &'a HashMap<LandblockCoord, Resident>,
    models: &'a crate::IdMap<u32, Option<Vec<MeshRef>>>,
    walk: &'a FrameWalk,
    server_models_ready: bool,
    /// Landblocks that left the window are still being released.
    retiring: bool,
    unavailable: bool,
    window: RevealWindow,
}

impl RevealState<'_> {
    /// The landblock's terrain arrived, or the cell dat has none.
    fn terrain_arrived(&self, block: LandblockCoord) -> bool {
        self.residents
            .get(&block)
            .is_some_and(|r| r.tile.is_some() || r.missing)
    }

    /// The landblock's near content and all its models have arrived (a
    /// model arrives after its meshes and surfaces).
    fn statics_arrived(&self, block: LandblockCoord) -> bool {
        self.residents.get(&block).is_some_and(|r| {
            r.missing
                || r.near
                    .as_ref()
                    .is_some_and(|n| n.model_ids.iter().all(|m| self.models.contains_key(m)))
        })
    }
}

impl RevealFacts for RevealState<'_> {
    fn reveal_window(&self) -> RevealWindow {
        self.window
    }

    fn render_neighborhood_ready(&self, cell: u32, near: u32, far: u32) -> bool {
        // Nothing of the window left behind is shown with the new one.
        !self.retiring
            && neighborhood_loaded(cell, far, |b| self.terrain_arrived(b))
            && neighborhood_loaded(cell, near, |b| self.statics_arrived(b))
    }

    fn spawn_cell_ready(&self, cell: u32) -> bool {
        // An interior destination waits for its cell; outdoors there is
        // nothing more to wait for.
        cell & 0xFFFF < 0x0100 || self.walk.cell(cell).is_some()
    }

    fn terrain_neighborhood_ready(&self, cell: u32, radius: u32) -> bool {
        neighborhood_loaded(cell, radius, |b| self.terrain_arrived(b))
    }

    fn composite_textures_ready(&self) -> bool {
        // Server objects' textures arrive with their models.
        self.server_models_ready
    }

    fn prepare_composite_textures(&mut self, _cell: u32, _radius: u32) {
        // Server objects' models are requested as they are seen.
    }

    fn invalidate_composite_textures(&mut self) {}

    fn spawn_claim_unhydratable(&self, cell: u32) -> bool {
        // The dats cannot be read, or the cell dat has no such landblock:
        // waiting would never end.
        self.unavailable
            || self
                .residents
                .get(&LandblockCoord::of_cell(cell))
                .is_some_and(|r| r.missing)
    }
}

/// The server objects drawn (standing in the world or held by one) and
/// their models.
fn drawn_objects(
    scene: Option<&Scene>,
) -> impl Iterator<Item = (&ac_runtime::world::ObjectView, u32)> {
    scene
        .into_iter()
        .flat_map(Scene::objects)
        .filter_map(|object| {
            let setup_id = object.setup_id?;
            (object.position.is_some() || object.parent.is_some()).then_some((object, setup_id))
        })
}

/// The looks the sweep keeps: those of the server objects drawn (held
/// objects included) and of the creation previews. Creation is over once a
/// character is in the world: its previews' looks are let go then.
fn looks_in_use(
    scene: Option<&Scene>,
    viewports: &mut viewport::Viewports,
) -> HashSet<AppearanceKey> {
    if scene.is_some_and(Scene::in_world) {
        viewports.end_creation();
    }
    let mut used = scene_appearance_keys(scene);
    used.extend(viewports.appearance_keys().cloned());
    used
}

/// The looks of the server objects drawn (held objects included).
fn scene_appearance_keys(scene: Option<&Scene>) -> HashSet<AppearanceKey> {
    drawn_objects(scene)
        .map(|(object, setup_id)| AppearanceKey::in_world(setup_id, &object.model))
        .collect()
}

#[cfg(test)]
mod atlas_upload_tests;
#[cfg(test)]
mod retirement_tests;
#[cfg(test)]
mod texture_detail_tests;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn neighbouring_draws_of_one_blended_sub_mesh_on_consecutive_instances_are_one_draw() {
        use crate::draws::AlphaDraw;
        let mesh = |mesh: MeshKey, sub: u32, instance: u32| {
            AlphaItem::Mesh(AlphaDraw {
                mesh,
                variant: 0,
                shell: false,
                sub,
                instance,
                faded: false,
            })
        };
        let items = [
            mesh(7, 0, 10),
            mesh(7, 0, 11),
            mesh(7, 0, 12),
            // Another sub-mesh of the same mesh ends the run.
            mesh(7, 1, 13),
        ];
        assert_eq!(alpha_mesh_run(&items), 3);
        assert_eq!(alpha_mesh_run(&items[3..]), 1);
        // A gap in the instances ends it too.
        assert_eq!(alpha_mesh_run(&[mesh(7, 0, 10), mesh(7, 0, 12)]), 1);
        // So does another mesh.
        assert_eq!(alpha_mesh_run(&[mesh(7, 0, 10), mesh(8, 0, 11)]), 1);
        let faded = AlphaItem::Mesh(AlphaDraw {
            faded: true,
            ..match mesh(7, 0, 11) {
                AlphaItem::Mesh(d) => d,
                _ => unreachable!(),
            }
        });
        assert_eq!(alpha_mesh_run(&[mesh(7, 0, 10), faded]), 1);
        assert_eq!(alpha_mesh_run(&[]), 0);
    }

    #[test]
    fn a_buffer_is_bound_again_only_when_another_was_bound_in_between() {
        let (a, b) = (1_u8, 1_u8);
        let mut bound = None;
        assert!(newly_bound(&mut bound, &a), "the first bind is made");
        assert!(
            !newly_bound(&mut bound, &a),
            "the same buffer again is skipped"
        );
        // Equal contents are not the same buffer.
        assert!(newly_bound(&mut bound, &b));
        assert!(newly_bound(&mut bound, &a));
    }

    #[test]
    fn a_translucent_instance_blends_its_solid_and_clip_map_sub_meshes() {
        // OpenAC WbDrawDispatcher.TryClassifyBatch: opacity below one turns
        // an opaque batch into alpha blending; blended kinds keep theirs.
        use Translucency as T;
        assert_eq!(faded_translucency(T::Opaque, true), T::AlphaBlend);
        assert_eq!(faded_translucency(T::ClipMap, true), T::AlphaBlend);
        assert_eq!(faded_translucency(T::Additive, true), T::Additive);
        assert_eq!(faded_translucency(T::InvAlpha, true), T::InvAlpha);
        assert_eq!(faded_translucency(T::ClipMap, false), T::Opaque);
        assert_eq!(faded_translucency(T::Opaque, false), T::Opaque);
    }

    #[test]
    fn a_hidden_or_no_draw_object_is_not_drawn() {
        // OpenAC LiveEntityRuntime.RefreshPresentation: IsDrawVisible is
        // false while NoDraw or Hidden is set; WbDrawDispatcher skips it.
        // The character the server hides for a teleport (Hidden | IgnoreCollisions,
        // until the client's login complete) is drawn only after it shows.
        let teleporting = PhysicsState::HIDDEN | PhysicsState::IGNORE_COLLISIONS;
        assert!(!draws_by_state(teleporting.bits()));
        assert!(!draws_by_state(PhysicsState::NO_DRAW.bits()));
        let shown = PhysicsState::REPORT_COLLISIONS | PhysicsState::GRAVITY;
        assert!(draws_by_state(shown.bits()));
        assert!(draws_by_state(0));
    }

    #[test]
    fn a_held_object_is_drawn_only_while_its_holder_is() {
        // OpenAC EquippedChildRenderController.ApplyParentDrawVisibility:
        // IsAncestorDrawVisible = parent.IsDrawVisible &&
        // parent.IsAncestorDrawVisible; a child draws when both its own and
        // its ancestors' visibility hold.
        assert!(held_draws(0, true));
        assert!(!held_draws(0, false));
        assert!(!held_draws(PhysicsState::HIDDEN.bits(), true));
        assert!(!held_draws(PhysicsState::NO_DRAW.bits(), true));
    }

    #[test]
    fn a_camera_facing_shell_level_is_turned_toward_the_eye() {
        // OpenAC WbDrawDispatcher.ClassifyEntityForWalk: a building shell's
        // selected level of mode 2..5 draws part 0 at
        // WalkBuildingDrawTransform.Resolve(part placement, the building's
        // sort centre, the camera); other modes keep the placement.
        let level = |gfx_obj_id: u32, mode: u32, max_dist: f32| DegradeLevel {
            gfx_obj_id,
            mode,
            min_dist: 0.0,
            ideal_dist: max_dist,
            max_dist,
        };
        let building = BuildingData {
            model_id: 0x0100_0001,
            transform: Mat4::IDENTITY,
            land_cell: 0,
            walk: crate::walk::WalkBuilding {
                portals: Vec::new(),
                world: Mat4::IDENTITY,
                inverse: Mat4::IDENTITY,
            },
            part0: (0x0100_0001, Mat4::IDENTITY),
            part0_scale_z: 1.0,
            sort_center: Vec3::ZERO,
            levels: vec![level(0x0100_0001, 1, 100.0), level(0x0100_0002, 5, 1000.0)],
            trees: HashMap::new(),
            physics: ac_physics::BuildingPhysics::default(),
        };
        let settings = DegradeSettings {
            degrade_distance: 0.0,
            multiplier: 0.0,
            keep_distant_buildings: false,
            degrades_disabled: false,
        };
        let world = Mat4::from_translation(Vec3::new(0.0, 500.0, 0.0));
        // Near: the solid level, standing as placed.
        let near_eye = Vec3::new(10.0, 500.0, 0.0);
        assert_eq!(
            shell_part0(&building, world, near_eye, &settings),
            (0x0100_0001, None)
        );
        // Far: the upright sprite (mode 5) turns its y away from the eye.
        let far_eye = Vec3::new(200.0, 500.0, 0.0);
        let (id, turned) = shell_part0(&building, world, far_eye, &settings);
        assert_eq!(id, 0x0100_0002);
        let turned = turned.expect("a camera-facing level is turned");
        assert_eq!(
            turned,
            camera_facing_placement(world, Vec3::ZERO, far_eye, 5)
        );
        assert!(
            turned
                .transform_point3(Vec3::Y)
                .abs_diff_eq(Vec3::new(-1.0, 500.0, 0.0), 1e-4)
        );
        // Keeping distant buildings falls back to the solid level instead.
        let keep = DegradeSettings {
            keep_distant_buildings: true,
            ..settings
        };
        assert_eq!(
            shell_part0(&building, world, far_eye, &keep),
            (0x0100_0001, None)
        );
    }

    #[test]
    fn procedural_scenery_is_offered_as_an_effect_owner() {
        // OpenAC GpuWorldState.ActivateLandblockPresentation calls
        // EntityScriptActivator.OnCreate for every dat entity of the
        // landblock (ServerGuid 0), and LandblockBuildFactory merges the
        // procedural scenery into that list: a tree whose setup has a
        // default script runs it.
        let at = |model_id: u32, x: f32, shell: bool| StaticInstance {
            model_id,
            transform: Mat4::from_translation(Vec3::new(x, 10.0, 0.0)),
            is_building_shell: shell,
        };
        let shell = at(0x0200_0003, 30.0, true);
        let own = at(0x0200_0001, 10.0, false);
        let tree = at(0x0200_0002, 20.0, false);
        let near = NearResident {
            statics: vec![own, tree],
            by_cell: vec![Vec::new(); 64],
            buildings: Vec::new(),
            cells: Vec::new(),
            model_ids: Vec::new(),
            light_statics: vec![shell, own],
            scenery_start: 1,
        };
        let offered: Vec<(StaticKey, u32)> = static_effect_candidates(0xA9B4_0000, &near)
            .map(|(k, s)| (k, s.model_id))
            .collect();
        let key = |cell: u32, index: u32| StaticKey {
            landblock: 0xA9B4_0000,
            cell,
            index,
        };
        assert_eq!(
            offered,
            [
                (key(0, 0), 0x0200_0001),
                // The scenery keeps the key its animation is registered by.
                (key(0, 1), 0x0200_0002),
                (key(crate::effects::SHELL_CELL, 0), 0x0200_0003),
            ]
        );
    }

    /// A server object as the frame files it: drawn on `owner`'s cells.
    fn object_draw(local_id: u32, owner: u32, held: bool) -> ObjectDraw<'static> {
        ObjectDraw {
            parts: Cow::Owned(Vec::new()),
            world: Mat4::IDENTITY,
            lights: NO_LIGHTS,
            indoor: true,
            is_player: false,
            opacities: None,
            guid: 0x5000_0000 | local_id,
            local_id,
            root: Mat4::IDENTITY,
            held,
            visible: true,
            render_owner: owner,
        }
    }

    /// An object whose drawn parts reach three rooms of the Facility Hub is
    /// drawn in each of them, once each. OpenAC draws a live object in every
    /// cell its registry rows are in (`WalkProductionWorldData.GetCellDynamics`);
    /// the cells are those of a test OpenAC had before its cell views read
    /// the registry (`BucketDynamicRecord_InstallsMultipartPlayerInEveryCrossedInteriorCell`).
    #[test]
    fn an_object_is_drawn_in_every_cell_its_parts_reach() {
        const HOME: u32 = 0x8A02_015E;
        let cells = [0x8A02_015E, 0x8A02_015F, 0x8A02_01C1];
        let objects = HashMap::from([(HOME, vec![object_draw(0x000F_4243, 0x000F_4243, false)])]);
        let filed = file_object_draws(&objects, |owner| {
            (owner == 0x000F_4243).then_some(&cells[..])
        });
        let mut keys: Vec<u32> = filed.keys().copied().collect();
        keys.sort_unstable();
        assert_eq!(keys, cells);
        for list in filed.values() {
            assert_eq!(list, &[(HOME, 0)]);
        }
    }

    /// An object without rows is drawn in the cell it stands in. This is
    /// this client's choice, not OpenAC's (OpenAC draws only what its
    /// registry lists): the rows come from the window's collision mirror,
    /// which holds only the player's landblock and its eight neighbours and
    /// builds objects over a few frames, so an object outside it, not yet
    /// built, or whose build failed has none.
    #[test]
    fn an_object_without_rows_is_drawn_where_it_stands() {
        const HOME: u32 = 0x8A02_015F;
        let objects = HashMap::from([(HOME, vec![object_draw(0x1234, 0x1234, false)])]);
        let filed = file_object_draws(&objects, |_| None);
        assert_eq!(filed.len(), 1);
        assert_eq!(filed[&HOME], vec![(HOME, 0)]);
    }

    /// A held object (a wand held by an item held by a character) is drawn
    /// in its top-level holder's cells, as OpenAC's registry gives an
    /// attached child its root's cell array
    /// (`ShadowObjectRegistryRetailCellArrayTests.AttachChild_NestedChildOfAChild_ResolvesToTheRoot`);
    /// the cells are those of a test OpenAC had before its cell views read
    /// the registry (`ResolveDynamicRenderCells_EquippedDescendantInheritsRootCellArray`).
    #[test]
    fn a_held_object_is_drawn_in_its_holders_cells() {
        const ROOT: u32 = 0x5000_000A;
        const HOME: u32 = 0xF418_0104;
        let cells = [0xF418_0106, 0xF418_0104];
        let objects = HashMap::from([(
            HOME,
            vec![
                object_draw(ROOT, ROOT, false),
                object_draw(0x8000_0461, ROOT, true),
                object_draw(0x8000_45EE, ROOT, true),
            ],
        )]);
        let filed = file_object_draws(&objects, |owner| (owner == ROOT).then_some(&cells[..]));
        assert_eq!(filed.len(), 2);
        for cell in cells {
            assert_eq!(filed[&cell], vec![(HOME, 0), (HOME, 1), (HOME, 2)]);
        }
    }

    /// A server object recoloured by `texture`, standing in the world
    /// (`Some(false)`), held by another with no place of its own
    /// (`Some(true)`), or in a pack (`None`).
    fn recoloured_object(
        local_id: u32,
        texture: u32,
        held: Option<bool>,
    ) -> ac_runtime::world::ObjectView {
        use ac_runtime::world::{EffectFacts, ObjectKey, ObjectView, RadarFacts};
        ObjectView {
            key: ObjectKey {
                local_id,
                incarnation: 1,
            },
            guid: 0x8000_0000 | local_id,
            name: None,
            setup_id: Some(0x0200_0001),
            position: (held == Some(false)).then_some(ac_proto::Position {
                cell_id: 0xA9B4_0019,
                origin: ac_proto::Vector3 {
                    x: 10.0,
                    y: 20.0,
                    z: 30.0,
                },
                orientation: ac_proto::Quaternion::IDENTITY,
            }),
            parent: (held == Some(true)).then_some(ac_proto::PhysicsAttachment {
                guid: 0x5000_0001,
                location_id: 1,
            }),
            placement: None,
            rest_placement: 0,
            scale: None,
            translucency: None,
            state: 0,
            velocity: None,
            model: ac_proto::ModelData {
                texture_changes: vec![ac_proto::TextureChange {
                    part_index: 0,
                    old_texture: 0x0500_0001,
                    new_texture: texture,
                }],
                ..ac_proto::ModelData::default()
            },
            motion_table_id: None,
            sound_table_id: None,
            radar: RadarFacts::default(),
            motion: None,
            effects: EffectFacts::default(),
            step_blend: None,
        }
    }

    /// The sweep keeps the looks of the objects standing in the world and
    /// of those they hold (a wielded sword has no place of its own), not
    /// of what sits in a pack.
    #[test]
    fn held_items_keep_their_looks() {
        let standing = recoloured_object(1, 0x0500_00A0, Some(false));
        let held = recoloured_object(2, 0x0500_00B0, Some(true));
        let packed = recoloured_object(3, 0x0500_00C0, None);
        let mut scene = Scene::default();
        scene.apply(ac_runtime::session::PresentationEvent::Snapshot(
            ac_runtime::world::WorldSnapshot {
                player_guid: None,
                objects: vec![standing.clone(), held.clone(), packed.clone()],
            },
        ));
        let key =
            |o: &ac_runtime::world::ObjectView| AppearanceKey::in_world(0x0200_0001, &o.model);
        let used = scene_appearance_keys(Some(&scene));
        assert!(used.contains(&key(&standing)));
        assert!(used.contains(&key(&held)), "a held item keeps its look");
        assert!(!used.contains(&key(&packed)));

        let mut looks = crate::appearance_residency::HeldLooks::default();
        for (variant, o) in (1u32..).zip([&standing, &held, &packed]) {
            looks.variant_arrived(variant, vec![(0x0800_0100, FIRST_SURFACE_HANDLE + variant)]);
            let part = MeshRef {
                gfx_obj_id: 0x0100_0001,
                transform: Mat4::IDENTITY,
                variant,
            };
            let refused = looks.appearance_arrived(key(o), Some(vec![part]), None, |_| true);
            assert!(refused.is_none());
        }
        let released = looks.release_unused(|k| used.contains(k));
        assert_eq!(released.appearances, vec![key(&packed)]);
        assert_eq!(released.variants, vec![3]);
        assert!(looks.contains(&key(&held)));
        assert_eq!(looks.handle(2, 0x0800_0100), Some(FIRST_SURFACE_HANDLE + 2));
    }
}
