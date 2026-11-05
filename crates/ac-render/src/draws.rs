//! One frame's draw stream: what the frame walk reached, turned into GPU
//! instances in the order depth needs.
//!
//! Opaque draws between two doorway punches, terrain included, may be
//! reordered freely (the depth buffer sorts them), so they are grouped by
//! mesh (terrain by landblock) into one segment and drawn instanced. A punch resets depth inside a doorway so the interior
//! behind it can be drawn; everything after it (the interior, and the
//! nearer land cells) starts a new segment. Blended surfaces and particles
//! are held back: sorted far to near within each cell, appended to the
//! clip-map or alpha list in cell order, and drawn when the lists drain (a
//! building, a full list at a sort cell's exit, the end of the
//! landscape and of the frame).
//! Draws reached through a portal view are drawn whole (the depth buffer
//! sorts them); only the portal polygons are limited to their view.

use std::ops::Range;

use bytemuck::{Pod, Zeroable};
use glam::{Mat4, Vec2, Vec3};

use crate::effects::routing::AlphaList;
use crate::lights::LightSet;
use crate::meshing::Cull;
use crate::walk::{self, Plane, ViewSet};
use crate::world_frame::LandblockCoord;

/// Edges a clip region holds (a view has at most 31).
pub const CLIP_EDGES: usize = 32;

/// One clip region as the shaders read it (528 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct ClipRegionGpu {
    /// Edges in use.
    pub count: u32,
    /// Padding.
    pub _pad: [u32; 3],
    /// `(a, b, c, 0)`: a pixel is outside when `a x + b y + c > 0`.
    pub edges: [[f32; 4]; CLIP_EDGES],
}

/// One drawn instance of a mesh (116 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct InstanceGpu {
    /// Model to drawing frame.
    pub model: [f32; 16],
    /// Point lights reaching it.
    pub lights: [i32; 8],
    /// 1 inside a cell (torch-lit, no sun).
    pub indoor: u32,
    /// How opaque the instance is drawn, 0..=1 (its object's and part's
    /// translucency): multiplies the surface's alpha.
    pub opacity: f32,
    /// Its detail texture ([`DetailCategory`]).
    pub detail: u32,
    /// The selection flash's light: luminosity added to every surface,
    /// then the factor on the scene's light (0 and 1 unflashed).
    pub selection: [f32; 2],
}

/// Which retail detail texture an instance takes (OpenAC's detail
/// category: building shells, and interior cells with the environment's).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum DetailCategory {
    /// No detail texture.
    None = 0,
    /// A building's outside shell.
    Building = 1,
    /// An interior cell's walls.
    Environment = 2,
}

impl DetailCategory {
    /// The category of a mesh drawn as `mesh` (a cell's mesh is an
    /// environment's), `shell` when it is a building's shell.
    #[must_use]
    pub fn of(mesh: MeshKey, shell: bool) -> Self {
        if shell {
            Self::Building
        } else if mesh >> 32 == 1 {
            Self::Environment
        } else {
            Self::None
        }
    }
}

impl InstanceGpu {
    /// This instance drawn at `opacity`.
    #[must_use]
    pub fn with_opacity(mut self, opacity: f32) -> Self {
        self.opacity = opacity;
        self
    }

    /// This instance with detail texture `category`.
    #[must_use]
    pub fn with_detail(mut self, category: DetailCategory) -> Self {
        self.detail = category as u32;
        self
    }

    /// This instance lit by the selection flash's `lighting`.
    #[must_use]
    pub fn with_selection(mut self, lighting: crate::selection::SelectionLighting) -> Self {
        self.selection = [lighting.luminosity, lighting.diffuse];
        self
    }
}

/// One terrain draw: the landblock's corner.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct TerrainInstanceGpu {
    /// The landblock's south-west corner, drawing frame.
    pub offset: [f32; 3],
    /// 1 when the landblock is drawn at full detail (the landscape detail
    /// texture may draw on it), else 0.
    pub full_detail: u32,
}

impl TerrainInstanceGpu {
    /// The landblock whose corner is at `offset`, not at full detail.
    #[must_use]
    pub fn at(offset: [f32; 3]) -> Self {
        Self {
            offset,
            full_detail: 0,
        }
    }
}

/// Whether `block` is drawn at full detail with the landscape centred on
/// `center` (the player's landblock): the centre landblock and its eight
/// neighbours are, every landblock further out is drawn coarser. The
/// landscape detail texture draws only on full-detail landblocks.
#[must_use]
pub fn full_detail_landblock(block: LandblockCoord, center: LandblockCoord) -> bool {
    let dx = (i32::from(block.x) - i32::from(center.x)).abs();
    let dy = (i32::from(block.y) - i32::from(center.y)).abs();
    dx.max(dy) <= 1
}

/// Mark each of a frame's terrain instances (`terrain`, one per entry of
/// `draws`) full detail or not, with the landscape centred on `center`
/// (none: no landblock is at full detail).
pub fn mark_full_detail(
    terrain: &mut [TerrainInstanceGpu],
    draws: &[TerrainDraw],
    center: Option<LandblockCoord>,
) {
    for (instance, draw) in terrain.iter_mut().zip(draws) {
        instance.full_detail =
            u32::from(center.is_some_and(|c| full_detail_landblock(draw.block, c)));
    }
}

/// Every land cell of a landblock, as a [`TerrainDraw::cells`] mask.
pub const ALL_LAND_CELLS: u64 = u64::MAX;

/// The [`TerrainDraw::cells`] bit of land cell `cell` (`x * 8 + y`, the
/// cell id's low word less one). The terrain mesh lists its cells row by
/// row (`y * 8 + x`), six vertices each, and the bits follow the mesh.
#[must_use]
pub fn land_cell_bit(cell: usize) -> u64 {
    let (x, y) = (cell / 8, cell % 8);
    1 << (y * 8 + x)
}

/// What one terrain draw covers: some land cells of one landblock.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TerrainDraw {
    /// The landblock.
    pub block: LandblockCoord,
    /// Its cells, one bit each in mesh order ([`land_cell_bit`]).
    pub cells: u64,
}

impl TerrainDraw {
    /// The vertex ranges of the landblock's mesh that draw its cells: one
    /// per run of neighbouring cells in mesh order.
    #[must_use]
    pub fn vertex_runs(&self) -> Vec<Range<u32>> {
        const PER_CELL: u32 = ac_core::terrain::VERTICES_PER_CELL as u32;
        let mut runs = Vec::new();
        let mut rest = self.cells;
        while rest != 0 {
            let start = rest.trailing_zeros();
            let length = (rest >> start).trailing_ones();
            runs.push(start * PER_CELL..(start + length) * PER_CELL);
            rest &= if start + length >= 64 {
                0
            } else {
                !0u64 << (start + length)
            };
        }
        runs
    }
}

/// One vertex of a depth-only portal polygon.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct PortalVertexGpu {
    /// Drawing frame (or clip space when `screen` is 1).
    pub position: [f32; 3],
    /// Clip region.
    pub clip: u32,
    /// Depth to force (0..1); negative keeps the polygon's own.
    pub depth: f32,
    /// 1 when `position` is in clip space.
    pub screen: u32,
}

/// The far depth a punch writes.
pub const PUNCH_DEPTH: f32 = f32::from_bits(0x3F7F_FFEF);

/// A mesh: a GfxObj, or an interior cell's room.
pub type MeshKey = u64;

/// The key of an interior cell's mesh.
#[must_use]
pub fn cell_mesh_key(cell_id: u32) -> MeshKey {
    (1 << 32) | u64::from(cell_id)
}

/// A batch of instances of one mesh.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Group {
    /// The mesh.
    pub mesh: MeshKey,
    /// Part surface substitutions.
    pub variant: u32,
    /// A building shell (its untextured subsets are not drawn).
    pub shell: bool,
    /// Instances.
    pub instances: Range<u32>,
}

/// One blended sub-mesh of one instance.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AlphaDraw {
    /// The mesh.
    pub mesh: MeshKey,
    /// Part surface substitutions.
    pub variant: u32,
    /// A building shell.
    pub shell: bool,
    /// The sub-mesh.
    pub sub: u32,
    /// The instance.
    pub instance: u32,
    /// A solid or clip-map sub-mesh of a translucent instance: drawn alpha
    /// blended.
    pub faded: bool,
}

/// One particle drawn as a quad (64 bytes): the corners are
/// `center ± axis_x / 2 ± axis_y / 2`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct BillboardGpu {
    /// Centre, drawing frame; w is 1 with a texture, 0 for the soft sprite.
    pub center: [f32; 4],
    /// Full X span.
    pub axis_x: [f32; 4],
    /// Full Y span.
    pub axis_y: [f32; 4],
    /// Colour and opacity (straight RGBA, 0..1), multiplying the texel.
    pub color: [f32; 4],
}

/// One particle drawn as its model (80 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct ParticleMeshGpu {
    /// Model to drawing frame.
    pub model: [f32; 16],
    /// Colour and opacity (straight RGBA, 0..1), multiplying every texel.
    pub color: [f32; 4],
}

/// How a particle model's surface is blended.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ParticleBlend {
    /// No blending, depth written.
    Opaque,
    /// Straight alpha.
    Alpha,
    /// Added onto what is behind.
    Additive,
    /// Blended by one minus its alpha.
    InvAlpha,
}

/// A particle quad in the alpha list.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BillboardDraw {
    /// Its instance in [`FrameDraws::billboards`].
    pub instance: u32,
    /// Its texture's surface (0: the soft round sprite).
    pub surface: u32,
    /// Blended additively (else straight alpha).
    pub additive: bool,
}

/// One sub-mesh of a particle model in the alpha list.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ParticleMeshDraw {
    /// The particle model.
    pub gfx_obj_id: u32,
    /// The sub-mesh.
    pub sub: u32,
    /// Its instance in [`FrameDraws::particle_meshes`].
    pub instance: u32,
    /// The blend.
    pub blend: ParticleBlend,
    /// The faces it culls.
    pub cull: Cull,
}
/// One entry of a drained alpha list (blended draws in drawing order).
/// One entry of the far-to-near list of blended draws.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum AlphaItem {
    /// A blended sub-mesh of an object.
    Mesh(AlphaDraw),
    /// A particle quad.
    Billboard(BillboardDraw),
    /// A sub-mesh of a particle model.
    ParticleMesh(ParticleMeshDraw),
}

/// A particle draw waiting for the sort.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum PendingParticle {
    /// A quad with its texture's surface (0: none) and blend.
    Billboard {
        /// The instance.
        gpu: BillboardGpu,
        /// Texture surface.
        surface: u32,
        /// Additive.
        additive: bool,
    },
    /// A particle model's sub-mesh.
    Mesh {
        /// The instance.
        gpu: ParticleMeshGpu,
        /// The model.
        gfx_obj_id: u32,
        /// The sub-mesh.
        sub: u32,
        /// The blend.
        blend: ParticleBlend,
        /// The faces it culls.
        cull: Cull,
    },
}

/// What the frame draws, in order.
#[derive(Debug, Clone, PartialEq)]
pub enum Step {
    /// Sky objects before (`post` false) or after the world.
    Sky {
        /// After the world (weather).
        post: bool,
        /// The sky draws.
        range: Range<usize>,
    },
    /// Terrain draws.
    Terrain(Range<usize>),
    /// Opaque and clip-mapped instances, grouped.
    Groups(Range<usize>),
    /// Blended sub-meshes and particles, in drawing order.
    Alpha(Range<usize>),
    /// Depth-only portal polygons (punches, seals, a depth clear).
    Portals(Range<u32>),
}

/// The frame's draw stream and the data it draws from.
#[derive(Debug, Default)]
pub struct FrameDraws {
    /// Clip regions; 0 is the whole screen.
    pub clips: Vec<ClipRegionGpu>,
    /// Instances.
    pub instances: Vec<InstanceGpu>,
    /// Instance groups.
    pub groups: Vec<Group>,
    /// Blended draws and particles.
    pub alpha: Vec<AlphaItem>,
    /// Particle quads.
    pub billboards: Vec<BillboardGpu>,
    /// Particle model instances.
    pub particle_meshes: Vec<ParticleMeshGpu>,
    /// Terrain draws (their landblocks' corners).
    pub terrain: Vec<TerrainInstanceGpu>,
    /// The landblock and cells of each terrain draw.
    pub terrain_draws: Vec<TerrainDraw>,
    /// Sky draws (template indices) in order.
    pub sky: Vec<usize>,
    /// Portal polygon vertices (triangle lists).
    pub portals: Vec<PortalVertexGpu>,
    /// The order.
    pub steps: Vec<Step>,
    /// Interior cells drawn.
    pub cells_drawn: usize,
    /// Objects (not parts) drawn.
    pub objects_drawn: usize,
}

impl FrameDraws {
    /// An empty stream with the whole-screen clip region.
    #[must_use]
    pub fn new() -> Self {
        Self {
            clips: vec![ClipRegionGpu::zeroed()],
            ..Self::default()
        }
    }

    /// Add the clip region of view `index` of `views`; the whole screen
    /// (`width` x `height`) is region 0.
    pub fn clip_for(&mut self, views: &ViewSet, index: usize, width: f32, height: f32) -> u32 {
        let points = views.points(index);
        if covers_screen(&points, width, height) {
            return 0;
        }
        let mut region = ClipRegionGpu::zeroed();
        for (i, e) in walk::view_edges(&points)
            .into_iter()
            .take(CLIP_EDGES)
            .enumerate()
        {
            region.edges[i] = [e[0], e[1], e[2], 0.0];
            region.count = i as u32 + 1;
        }
        self.clips.push(region);
        (self.clips.len() - 1) as u32
    }

    /// Empty again, as [`Self::new`] makes it, keeping the lists' room.
    fn reset(&mut self) {
        let Self {
            clips,
            instances,
            groups,
            alpha,
            billboards,
            particle_meshes,
            terrain,
            terrain_draws,
            sky,
            portals,
            steps,
            cells_drawn,
            objects_drawn,
        } = self;
        clips.clear();
        clips.push(ClipRegionGpu::zeroed());
        instances.clear();
        groups.clear();
        alpha.clear();
        billboards.clear();
        particle_meshes.clear();
        terrain.clear();
        terrain_draws.clear();
        sky.clear();
        portals.clear();
        steps.clear();
        *cells_drawn = 0;
        *objects_drawn = 0;
    }
}

/// The room a frame's draws took, kept for the next frame's builder: a
/// frame of about the same size as the last then allocates nothing. Holds
/// the lists of the frame before last (the last one is being drawn) and
/// the opaque groups' lists by mesh.
#[derive(Debug, Default)]
pub struct DrawScratch {
    draws: Option<FrameDraws>,
    groups: crate::IdMap<(MeshKey, bool, u32), Vec<InstanceGpu>>,
    keys: Vec<(MeshKey, bool, u32)>,
}

/// The most meshes whose group lists are kept from frame to frame; past
/// it the kept lists are let go (a view of many different meshes).
const KEPT_GROUP_LISTS: usize = 8192;

impl DrawScratch {
    /// Keep a drawn frame's lists for a later frame.
    pub fn keep(&mut self, drawn: FrameDraws) {
        self.draws = Some(drawn);
    }
}

/// Whether a view polygon is the whole viewport.
fn covers_screen(points: &[Vec2], width: f32, height: f32) -> bool {
    points.len() == 4
        && points
            .iter()
            .all(|p| (p.x <= 0.5 || p.x >= width - 0.5) && (p.y <= 0.5 || p.y >= height - 0.5))
}

/// A view to draw through: its clip region and its culling planes.
#[derive(Debug, Clone)]
pub struct ActiveView {
    /// Clip region.
    pub clip: u32,
    /// Edge planes.
    pub planes: Vec<Plane>,
}

/// The views of one portal view slot.
#[must_use]
pub fn active_views(
    draws: &mut FrameDraws,
    views: &ViewSet,
    count: usize,
    width: f32,
    height: f32,
) -> Vec<ActiveView> {
    (0..count)
        .map(|v| ActiveView {
            clip: draws.clip_for(views, v, width, height),
            planes: views.planes(v),
        })
        .collect()
}

/// The views of one portal view slot for culling only: their planes, with
/// no clip region (what is culled by them is drawn whole).
#[must_use]
pub fn cull_views(views: &ViewSet, count: usize) -> Vec<ActiveView> {
    (0..count)
        .map(|v| ActiveView {
            clip: 0,
            planes: views.planes(v),
        })
        .collect()
}

/// Terrain and groups being gathered for the current segment.
#[derive(Debug, Default)]
pub struct Segment {
    groups: crate::IdMap<(MeshKey, bool, u32), Vec<InstanceGpu>>,
    /// Land cells by landblock, in the order the landblocks were first
    /// reached: (landblock, its corner, its cells' mask).
    terrain: Vec<(LandblockCoord, Vec3, u64)>,
    terrain_index: crate::IdMap<LandblockCoord, usize>,
}

/// What waits for the sort.
#[derive(Debug, Clone, Copy)]
pub enum PendingItem {
    /// A blended sub-mesh (its instance index is filled at the flush).
    Mesh {
        /// The draw.
        draw: AlphaDraw,
        /// The instance.
        instance: InstanceGpu,
    },
    /// A particle.
    Particle(PendingParticle),
}

/// A blended draw waiting for the sort.
#[derive(Debug, Clone, Copy)]
pub struct PendingAlpha {
    /// Distance from the eye, metres.
    pub distance: f32,
    /// The draw.
    pub item: PendingItem,
}

/// Entries each of the two alpha lists holds; an entry beyond is dropped.
pub const ALPHA_LIST_CAPACITY: usize = 3000;

/// The share of a list's capacity at which a sort cell's exit drains the
/// lists (2250 entries).
pub const SORT_CELL_EXIT_THRESHOLD: f32 = 0.75;

/// Collects one frame's draws.
///
/// Blended draws are gathered per cell, sorted far to near within the cell
/// (equal distances keep their order), then appended to the clip-map list
/// or the alpha list in the order the cells are drawn. A drain draws the
/// clip-map list and then the alpha list, each in append order: there is no
/// sort across cells.
#[derive(Debug, Default)]
pub struct StreamBuilder {
    /// The output.
    pub draws: FrameDraws,
    segment: Segment,
    /// The current segment's group keys, sorted when it ends.
    keys: Vec<(MeshKey, bool, u32)>,
    cell: Vec<(AlphaList, PendingAlpha)>,
    clip: Vec<PendingAlpha>,
    alpha: Vec<PendingAlpha>,
    /// Entries dropped this frame because their list was full.
    pub alpha_dropped: u32,
}

impl StreamBuilder {
    /// An empty builder.
    #[must_use]
    pub fn new() -> Self {
        Self {
            draws: FrameDraws::new(),
            ..Self::default()
        }
    }

    /// An empty builder that keeps its lists in the room `scratch` holds.
    #[must_use]
    pub fn with_scratch(scratch: DrawScratch) -> Self {
        let DrawScratch {
            draws,
            groups,
            keys,
        } = scratch;
        let mut draws = draws.unwrap_or_default();
        draws.reset();
        Self {
            draws,
            segment: Segment {
                groups,
                ..Segment::default()
            },
            keys,
            ..Self::default()
        }
    }

    /// Add an opaque instance of `mesh` to the current segment.
    pub fn push_opaque(&mut self, mesh: MeshKey, shell: bool, variant: u32, instance: InstanceGpu) {
        self.segment
            .groups
            .entry((mesh, shell, variant))
            .or_default()
            .push(instance);
    }

    /// Hold a blended sub-mesh of the current cell (alpha list).
    pub fn push_alpha(&mut self, distance: f32, draw: AlphaDraw, instance: InstanceGpu) {
        self.cell.push((
            AlphaList::Alpha,
            PendingAlpha {
                distance,
                item: PendingItem::Mesh { draw, instance },
            },
        ));
    }

    /// Hold a particle of the current cell for `list`; it sorts with the
    /// cell's blended sub-meshes by its distance from the eye (metres).
    pub fn push_particle(&mut self, distance: f32, list: AlphaList, particle: PendingParticle) {
        self.cell.push((
            list,
            PendingAlpha {
                distance,
                item: PendingItem::Particle(particle),
            },
        ));
    }

    /// The current cell is done: its blended draws, far to near (equal
    /// distances in the order given), join the end of their lists; a full
    /// list drops the entry.
    pub fn end_cell(&mut self) {
        if self.cell.is_empty() {
            return;
        }
        let mut cell = std::mem::take(&mut self.cell);
        cell.sort_by(|a, b| b.1.distance.total_cmp(&a.1.distance));
        for (list, pending) in cell.drain(..) {
            let target = match list {
                AlphaList::Clip => &mut self.clip,
                AlphaList::Alpha => &mut self.alpha,
            };
            if target.len() >= ALPHA_LIST_CAPACITY {
                self.alpha_dropped += 1;
            } else {
                target.push(pending);
            }
        }
        self.cell = cell;
    }

    /// Whether a drain at `threshold` (a share of a list's capacity) would
    /// draw: either list holds at least that many entries.
    #[must_use]
    pub fn would_flush(&self, threshold: f32) -> bool {
        let at = threshold * ALPHA_LIST_CAPACITY as f32;
        self.clip.len() as f32 >= at || self.alpha.len() as f32 >= at
    }

    /// A landscape sort cell is done: end it, and drain the lists when one
    /// is three quarters full.
    pub fn sort_cell_exit(&mut self) {
        self.end_cell();
        if self.would_flush(SORT_CELL_EXIT_THRESHOLD) {
            self.flush_alpha();
        }
    }

    /// Entries waiting in the clip-map and alpha lists.
    #[must_use]
    pub fn pending_alpha(&self) -> (usize, usize) {
        (self.clip.len(), self.alpha.len())
    }

    /// Draw `particles` now, far to near (equal distances in the order
    /// given), as a step of their own: the sky's particles.
    pub fn push_particles_now(&mut self, particles: Vec<(f32, PendingParticle)>) {
        self.flush_segment();
        let pending = particles
            .into_iter()
            .map(|(distance, p)| PendingAlpha {
                distance,
                item: PendingItem::Particle(p),
            })
            .collect();
        self.emit_sorted(pending);
    }

    /// End the current segment: its terrain becomes one step, then its
    /// groups another.
    pub fn flush_segment(&mut self) {
        if !self.segment.terrain.is_empty() {
            let first = self.draws.terrain.len();
            self.segment.terrain_index.clear();
            for (block, offset, cells) in self.segment.terrain.drain(..) {
                self.draws
                    .terrain
                    .push(TerrainInstanceGpu::at(offset.to_array()));
                self.draws.terrain_draws.push(TerrainDraw { block, cells });
            }
            let last = self.draws.terrain.len();
            self.draws.steps.push(Step::Terrain(first..last));
        }
        if self.segment.groups.is_empty() {
            return;
        }
        // The lists stay in the map, emptied: their room serves the next
        // segment and the next frame.
        let mut keys = std::mem::take(&mut self.keys);
        keys.clear();
        keys.extend(
            self.segment
                .groups
                .iter()
                .filter(|(_, list)| !list.is_empty())
                .map(|(key, _)| *key),
        );
        if keys.is_empty() {
            self.keys = keys;
            return;
        }
        keys.sort_unstable();
        let first = self.draws.groups.len();
        for key in &keys {
            let Some(list) = self.segment.groups.get_mut(key) else {
                continue;
            };
            let start = self.draws.instances.len() as u32;
            self.draws.instances.extend_from_slice(list);
            self.draws.groups.push(Group {
                mesh: key.0,
                shell: key.1,
                variant: key.2,
                instances: start..start + list.len() as u32,
            });
            list.clear();
        }
        self.keys = keys;
        let last = self.draws.groups.len();
        self.draws.steps.push(Step::Groups(first..last));
    }

    /// Drain the lists: the current cell joins them, then the clip-map list
    /// and the alpha list are drawn in append order.
    pub fn flush_alpha(&mut self) {
        self.end_cell();
        // The opaque draws so far go first: the blended ones are drawn over
        // them.
        self.flush_segment();
        if self.clip.is_empty() && self.alpha.is_empty() {
            return;
        }
        let mut pending = std::mem::take(&mut self.clip);
        pending.append(&mut self.alpha);
        self.emit(pending);
    }

    /// Sort far to near (stable: equal distances keep their order) and
    /// append as one alpha step.
    fn emit_sorted(&mut self, mut pending: Vec<PendingAlpha>) {
        pending.sort_by(|a, b| b.distance.total_cmp(&a.distance));
        self.emit(pending);
    }

    /// Append `pending` in its order as one alpha step.
    fn emit(&mut self, pending: Vec<PendingAlpha>) {
        if pending.is_empty() {
            return;
        }
        let first = self.draws.alpha.len();
        for p in pending {
            let item = match p.item {
                PendingItem::Mesh { draw, instance } => {
                    let index = self.draws.instances.len() as u32;
                    self.draws.instances.push(instance);
                    AlphaItem::Mesh(AlphaDraw {
                        instance: index,
                        ..draw
                    })
                }
                PendingItem::Particle(PendingParticle::Billboard {
                    gpu,
                    surface,
                    additive,
                }) => {
                    let instance = self.draws.billboards.len() as u32;
                    self.draws.billboards.push(gpu);
                    AlphaItem::Billboard(BillboardDraw {
                        instance,
                        surface,
                        additive,
                    })
                }
                PendingItem::Particle(PendingParticle::Mesh {
                    gpu,
                    gfx_obj_id,
                    sub,
                    blend,
                    cull,
                }) => {
                    let instance = self.draws.particle_meshes.len() as u32;
                    self.draws.particle_meshes.push(gpu);
                    AlphaItem::ParticleMesh(ParticleMeshDraw {
                        gfx_obj_id,
                        sub,
                        instance,
                        blend,
                        cull,
                    })
                }
            };
            self.draws.alpha.push(item);
        }
        let last = self.draws.alpha.len();
        self.draws.steps.push(Step::Alpha(first..last));
    }

    /// Add the terrain of `cells` (a [`TerrainDraw::cells`] mask) of
    /// `block`, whose corner is at `offset`, to the current segment. Terrain
    /// is opaque: within a segment the depth buffer sorts it, so a
    /// landblock's cells are drawn together; a punch ends the segment, and
    /// cells added after it are drawn after it.
    pub fn push_terrain(&mut self, block: LandblockCoord, offset: Vec3, cells: u64) {
        let segment = &mut self.segment;
        match segment.terrain_index.get(&block) {
            Some(&i) => segment.terrain[i].2 |= cells,
            None => {
                segment.terrain_index.insert(block, segment.terrain.len());
                segment.terrain.push((block, offset, cells));
            }
        }
    }

    /// Add sky draws (template indices).
    pub fn push_sky(&mut self, post: bool, templates: impl Iterator<Item = usize>) {
        let start = self.draws.sky.len();
        self.draws.sky.extend(templates);
        let end = self.draws.sky.len();
        if end > start {
            self.draws.steps.push(Step::Sky {
                post,
                range: start..end,
            });
        }
    }

    /// A depth-only polygon (fan) in the drawing frame: `depth` forces its
    /// depth (a punch); negative keeps its own (a seal). Ends the segment.
    pub fn push_portal(&mut self, polygon: &[Vec3], clip: u32, depth: f32) {
        if polygon.len() < 3 {
            return;
        }
        self.flush_segment();
        let start = self.draws.portals.len() as u32;
        for i in 1..polygon.len() - 1 {
            for p in [polygon[0], polygon[i], polygon[i + 1]] {
                self.draws.portals.push(PortalVertexGpu {
                    position: p.to_array(),
                    clip,
                    depth,
                    screen: 0,
                });
            }
        }
        self.push_portal_step(start);
    }

    /// Reset depth to the far plane over the whole screen. Ends the segment.
    pub fn push_depth_clear(&mut self) {
        self.flush_segment();
        let start = self.draws.portals.len() as u32;
        let corners = [[-1.0, -1.0], [1.0, -1.0], [1.0, 1.0], [-1.0, 1.0]];
        for i in [0usize, 1, 2, 0, 2, 3] {
            self.draws.portals.push(PortalVertexGpu {
                position: [corners[i][0], corners[i][1], 0.0],
                clip: 0,
                depth: 1.0,
                screen: 1,
            });
        }
        self.push_portal_step(start);
    }

    fn push_portal_step(&mut self, start: u32) {
        let end = self.draws.portals.len() as u32;
        match self.draws.steps.last_mut() {
            Some(Step::Portals(range)) if range.end == start => range.end = end,
            _ => self.draws.steps.push(Step::Portals(start..end)),
        }
    }

    /// Finish: flush what is held.
    #[must_use]
    pub fn finish(self) -> FrameDraws {
        self.finish_keeping().0
    }

    /// The frame's draws, and the room this builder's working lists took
    /// for the next frame's builder ([`Self::with_scratch`]).
    pub fn finish_keeping(mut self) -> (FrameDraws, DrawScratch) {
        self.flush_alpha();
        let mut groups = self.segment.groups;
        if groups.len() > KEPT_GROUP_LISTS {
            groups = crate::IdMap::default();
        }
        let scratch = DrawScratch {
            draws: None,
            groups,
            keys: self.keys,
        };
        (self.draws, scratch)
    }
}

/// An instance record.
#[must_use]
pub fn instance(model: Mat4, lights: LightSet, indoor: bool) -> InstanceGpu {
    InstanceGpu {
        model: model.to_cols_array(),
        lights,
        indoor: u32::from(indoor),
        opacity: 1.0,
        detail: DetailCategory::None as u32,
        selection: [0.0, 1.0],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::lights::NO_LIGHTS;

    // The landscape is drawn at full detail on the player's landblock and
    // the eight around it (one landblock either way, diagonals too);
    // landblocks two or more away are drawn coarser.
    #[test]
    fn full_detail_is_the_players_landblock_and_its_eight_neighbours() {
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        let at = |dx: i32, dy: i32| LandblockCoord {
            x: (0xA9 + dx) as u8,
            y: (0xB4 + dy) as u8,
        };
        for dx in -1..=1 {
            for dy in -1..=1 {
                assert!(full_detail_landblock(at(dx, dy), center), "{dx} {dy}");
            }
        }
        for (dx, dy) in [(2, 0), (0, -2), (2, 2), (-2, 1), (5, 0)] {
            assert!(!full_detail_landblock(at(dx, dy), center), "{dx} {dy}");
        }
        // Each instance is marked from its draw's landblock; with no centre
        // (no outdoor landscape) none is.
        let draws = [
            TerrainDraw {
                block: at(1, 1),
                cells: ALL_LAND_CELLS,
            },
            TerrainDraw {
                block: at(2, 0),
                cells: ALL_LAND_CELLS,
            },
        ];
        let mut terrain = [TerrainInstanceGpu::at([0.0; 3]); 2];
        mark_full_detail(&mut terrain, &draws, Some(center));
        assert_eq!(terrain.map(|t| t.full_detail), [1, 0]);
        mark_full_detail(&mut terrain, &draws, None);
        assert_eq!(terrain.map(|t| t.full_detail), [0, 0]);
    }

    /// OpenAC `PortalDepthMaskRetailStateTests.SealAndPunchUseOneRetailAlwaysWritePipelineAndOneDrawEach`:
    /// a punch and a seal are one depth-only fan each through the one
    /// depth-always pipeline (`pipelines::portal_pipeline`); the punch
    /// forces far depth, the seal keeps the polygon's own.
    #[test]
    fn a_punch_and_a_seal_are_one_depth_fan_each() {
        let mut b = StreamBuilder::new();
        let triangle = [
            Vec3::new(-1.0, -1.0, 1.0),
            Vec3::new(1.0, -1.0, 1.0),
            Vec3::new(0.0, 1.0, 1.0),
        ];
        b.push_portal(&triangle, 0, PUNCH_DEPTH);
        b.push_portal(&triangle, 0, -1.0);
        let d = b.finish();
        assert_eq!(d.steps, vec![Step::Portals(0..6)]);
        assert!(d.portals[..3].iter().all(|v| v.depth == PUNCH_DEPTH));
        assert!(d.portals[3..].iter().all(|v| v.depth < 0.0));
        // A quad is a fan of two triangles; fewer than three points draw
        // nothing.
        let mut b = StreamBuilder::new();
        b.push_portal(&[Vec3::ZERO, Vec3::X, Vec3::ONE, Vec3::Y], 0, -1.0);
        b.push_portal(&[Vec3::ZERO, Vec3::X], 0, -1.0);
        assert_eq!(b.finish().portals.len(), 6);
    }

    #[test]
    /// OpenAC `WalkFrameDriverTests.OnPunchGeometry_FlushesPendingTerrainBatch_FarTerrainBeforePunchNearTerrainAfter`:
    /// a land cell's terrain reached before a building's doorway punch is
    /// drawn before it, a nearer cell's after it, so the nearer terrain
    /// covers the doorway again where it stands in front of it (a door
    /// seen through a hill otherwise).
    fn a_punch_splits_the_terrain_far_cells_before_near_cells_after() {
        let block = LandblockCoord { x: 0xF4, y: 0x18 };
        let mut b = StreamBuilder::new();
        b.push_terrain(block, Vec3::ZERO, land_cell_bit(0));
        b.push_portal(&[Vec3::ZERO, Vec3::X, Vec3::Y], 0, PUNCH_DEPTH);
        b.push_terrain(block, Vec3::ZERO, land_cell_bit(1));
        let d = b.finish();
        assert_eq!(
            d.steps,
            vec![
                Step::Terrain(0..1),
                Step::Portals(0..3),
                Step::Terrain(1..2),
            ]
        );
        assert_eq!(d.terrain_draws[0].cells, land_cell_bit(0));
        assert_eq!(d.terrain_draws[1].cells, land_cell_bit(1));
    }

    /// Between two punches a landblock's cells are one draw, before the
    /// segment's objects; landblocks keep the order they were reached in.
    #[test]
    fn a_segments_terrain_is_one_draw_per_landblock() {
        let a = LandblockCoord { x: 1, y: 1 };
        let c = LandblockCoord { x: 2, y: 1 };
        let mut b = StreamBuilder::new();
        let inst = instance(Mat4::IDENTITY, NO_LIGHTS, false);
        b.push_terrain(a, Vec3::ZERO, land_cell_bit(0));
        b.push_opaque(5, false, 0, inst);
        b.push_terrain(c, Vec3::X, land_cell_bit(0));
        b.push_terrain(a, Vec3::ZERO, land_cell_bit(9));
        let d = b.finish();
        assert_eq!(d.steps, vec![Step::Terrain(0..2), Step::Groups(0..1)]);
        assert_eq!(
            d.terrain_draws,
            vec![
                TerrainDraw {
                    block: a,
                    cells: land_cell_bit(0) | land_cell_bit(9),
                },
                TerrainDraw {
                    block: c,
                    cells: land_cell_bit(0),
                },
            ]
        );
        assert_eq!(d.terrain[1].offset, [1.0, 0.0, 0.0]);
    }

    /// A frame built in the room an earlier frame left draws exactly what
    /// a fresh builder draws: nothing of the earlier frame shows.
    #[test]
    fn a_builder_from_the_last_frames_room_draws_as_a_fresh_one() {
        let at = |x: f32| {
            instance(
                Mat4::from_translation(Vec3::new(x, 0.0, 0.0)),
                NO_LIGHTS,
                false,
            )
        };
        let block = LandblockCoord { x: 1, y: 1 };
        let frame = |b: &mut StreamBuilder, n: u64| {
            b.push_terrain(block, Vec3::ZERO, land_cell_bit(0));
            for mesh in 0..n {
                b.push_opaque(mesh % 3 + 1, mesh % 2 == 0, 0, at(mesh as f32));
            }
            b.push_portal(&[Vec3::ZERO, Vec3::X, Vec3::Y], 0, PUNCH_DEPTH);
            b.push_opaque(9, false, 1, at(-1.0));
        };
        // The earlier frames are larger and draw meshes this one does not.
        let mut scratch = DrawScratch::default();
        for n in [12, 7] {
            let mut b = StreamBuilder::with_scratch(scratch);
            frame(&mut b, n);
            b.push_opaque(40 + n, true, 2, at(5.0));
            let (drawn, kept) = b.finish_keeping();
            scratch = kept;
            scratch.keep(drawn);
        }
        let mut reused = StreamBuilder::with_scratch(scratch);
        let mut fresh = StreamBuilder::new();
        frame(&mut reused, 4);
        frame(&mut fresh, 4);
        assert_eq!(
            format!("{:?}", reused.finish()),
            format!("{:?}", fresh.finish())
        );
    }

    /// OpenAC `TerrainLandCellIndexRunsTests`: a land cell `x * 8 + y` is
    /// the six vertices at `(y * 8 + x) * 6` of its landblock's mesh, every
    /// cell covers its own, and the whole landblock is one run.
    #[test]
    fn land_cells_map_to_their_vertex_runs() {
        let block = LandblockCoord { x: 0, y: 0 };
        let runs = |cells: u64| TerrainDraw { block, cells }.vertex_runs();
        assert_eq!(runs(land_cell_bit(3 * 8 + 5)), vec![258..264]);
        assert_eq!(runs(ALL_LAND_CELLS), vec![0..384]);
        assert!(runs(0).is_empty());
        // Cells (0,0) and (1,0) neighbour in the mesh; (0,1) starts a row.
        assert_eq!(
            runs(land_cell_bit(0) | land_cell_bit(8) | land_cell_bit(1)),
            vec![0..12, 48..54]
        );
        let mut covered = [false; 384];
        for cell in 0..64 {
            let r = runs(land_cell_bit(cell));
            assert_eq!(r.len(), 1);
            assert_eq!(r[0].len(), 6);
            for v in r[0].clone() {
                assert!(!covered[v as usize], "vertex {v} twice (cell {cell})");
                covered[v as usize] = true;
            }
        }
        assert!(covered.iter().all(|c| *c));
    }

    #[test]
    fn segments_split_at_punches_and_alpha_sorts_far_to_near() {
        let mut b = StreamBuilder::new();
        let inst = instance(Mat4::IDENTITY, NO_LIGHTS, false);
        b.push_opaque(5, false, 0, inst);
        b.push_opaque(5, false, 0, inst);
        b.push_opaque(3, false, 0, inst);
        let draw = AlphaDraw {
            mesh: 9,
            variant: 0,
            shell: false,
            sub: 0,
            instance: 0,
            faded: false,
        };
        b.push_alpha(1.0, draw, inst);
        b.push_alpha(10.0, AlphaDraw { sub: 1, ..draw }, inst);
        b.push_portal(&[Vec3::ZERO, Vec3::X, Vec3::Y], 0, PUNCH_DEPTH);
        b.push_opaque(5, false, 0, inst);
        let d = b.finish();
        assert_eq!(
            d.steps,
            vec![
                Step::Groups(0..2),
                Step::Portals(0..3),
                Step::Groups(2..3),
                Step::Alpha(0..2),
            ]
        );
        assert_eq!(d.groups[0].mesh, 3);
        assert_eq!(d.groups[1].instances, 1..3);
        // Far first.
        assert!(matches!(
            d.alpha[0],
            AlphaItem::Mesh(AlphaDraw { sub: 1, .. })
        ));
        assert_eq!(d.instances.len(), 6);
    }

    #[test]
    fn particles_sort_with_blended_sub_meshes_and_ties_keep_their_order() {
        // OpenAC `RetailAlphaQueue`: world translucent parts and particles
        // share one cell's far-to-near order; `ParticleSubmissionOrdering`:
        // equal distances keep emission order.
        let mut b = StreamBuilder::new();
        let inst = instance(Mat4::IDENTITY, NO_LIGHTS, false);
        let quad = |x: f32| PendingParticle::Billboard {
            gpu: BillboardGpu {
                center: [x, 0.0, 0.0, 1.0],
                ..BillboardGpu::zeroed()
            },
            surface: 0x0800_0001,
            additive: true,
        };
        b.push_alpha(
            5.0,
            AlphaDraw {
                mesh: 9,
                variant: 0,
                shell: false,
                sub: 0,
                instance: 0,
                faded: false,
            },
            inst,
        );
        b.push_particle(2.0, AlphaList::Alpha, quad(1.0));
        b.push_particle(8.0, AlphaList::Alpha, quad(2.0));
        b.push_particle(2.0, AlphaList::Alpha, quad(3.0));
        let d = b.finish();
        assert_eq!(d.steps, vec![Step::Alpha(0..4)]);
        let centers: Vec<f32> = d
            .alpha
            .iter()
            .map(|item| match item {
                AlphaItem::Billboard(q) => d.billboards[q.instance as usize].center[0],
                _ => 0.0,
            })
            .collect();
        assert_eq!(centers, [2.0, 0.0, 1.0, 3.0]);
        // Quads take consecutive instances in draw order.
        let instances: Vec<u32> = d
            .alpha
            .iter()
            .filter_map(|item| match item {
                AlphaItem::Billboard(q) => Some(q.instance),
                _ => None,
            })
            .collect();
        assert_eq!(instances, [0, 1, 2]);
    }

    /// A quad marked by its centre's x.
    fn marked(x: f32) -> PendingParticle {
        PendingParticle::Billboard {
            gpu: BillboardGpu {
                center: [x, 0.0, 0.0, 1.0],
                ..BillboardGpu::zeroed()
            },
            surface: 0x0800_0001,
            additive: false,
        }
    }

    /// The marks of the quads in draw order.
    fn marks(d: &FrameDraws) -> Vec<f32> {
        d.alpha
            .iter()
            .filter_map(|item| match item {
                AlphaItem::Billboard(q) => Some(d.billboards[q.instance as usize].center[0]),
                _ => None,
            })
            .collect()
    }

    /// OpenAC `RetailAlphaQueueTests.Flush_FifoBeatsReversedDistanceInOneCell`
    /// and `Flush_ParticleObjectAndCellSourcesOverlapInSubmissionOrder`:
    /// once appended, entries are drawn in append order; a nearer cell's
    /// entry drawn after a farther one's stays after it, whatever the
    /// distances.
    #[test]
    fn cells_are_drawn_in_append_order_not_by_distance() {
        let mut b = StreamBuilder::new();
        for (i, distance) in [1.0, 5.0, 9.0].into_iter().enumerate() {
            b.push_particle(distance, AlphaList::Alpha, marked(i as f32));
            b.end_cell();
        }
        let d = b.finish();
        assert_eq!(marks(&d), [0.0, 1.0, 2.0]);
    }

    /// OpenAC `RetailAlphaQueueTests.Flush_EqualPrioritySourcesPreserveSubmissionInterleave`:
    /// equal distances keep their submission order within a cell.
    #[test]
    fn equal_distances_keep_their_interleave() {
        let mut b = StreamBuilder::new();
        for i in 0..3 {
            b.push_particle(4.0, AlphaList::Alpha, marked(i as f32));
        }
        let d = b.finish();
        assert_eq!(marks(&d), [0.0, 1.0, 2.0]);
    }

    /// OpenAC `RetailAlphaQueueTests.Flush_DrawBuildingSiteAtZeroThresholdAlwaysDrains`,
    /// `Flush_PreClearThenFinalFlushBothDrainInOrder` and
    /// `Flush_TwoScopesNeverInterleaveRegardlessOfGlobalOrder`: a drain
    /// draws even one entry; two drains are two steps that never mix.
    #[test]
    fn two_drains_never_interleave() {
        let mut b = StreamBuilder::new();
        b.push_particle(1.0, AlphaList::Alpha, marked(1.0));
        b.flush_alpha();
        assert_eq!(b.pending_alpha(), (0, 0));
        b.push_particle(9.0, AlphaList::Alpha, marked(2.0));
        let d = b.finish();
        assert_eq!(d.steps, vec![Step::Alpha(0..1), Step::Alpha(1..2)]);
        assert_eq!(marks(&d), [1.0, 2.0]);
    }

    /// OpenAC `RetailAlphaQueueTests.Flush_SortCellExitValveDrainsExactlyAtTwoThousandTwoHundredFifty`
    /// and `Flush_SortCellExitValveIsANoOpOneBelowTheBoundary`.
    #[test]
    fn a_sort_cell_exit_drains_at_three_quarters() {
        let mut b = StreamBuilder::new();
        for _ in 0..2249 {
            b.push_particle(1.0, AlphaList::Alpha, marked(0.0));
        }
        b.sort_cell_exit();
        assert_eq!(b.pending_alpha(), (0, 2249));
        assert!(b.draws.steps.is_empty());
        b.push_particle(1.0, AlphaList::Alpha, marked(0.0));
        b.sort_cell_exit();
        assert_eq!(b.pending_alpha(), (0, 0));
        assert_eq!(b.draws.alpha.len(), 2250);
    }

    /// OpenAC `RetailAlphaQueueTests.Flush_BatchesAdjacentSameSourceEntriesAcrossTheClipAlphaBoundary`:
    /// the clip-map list drains before the alpha list.
    #[test]
    fn the_clip_map_list_drains_first() {
        let mut b = StreamBuilder::new();
        b.push_particle(1.0, AlphaList::Alpha, marked(3.0));
        b.end_cell();
        b.push_particle(1.0, AlphaList::Clip, marked(1.0));
        b.push_particle(1.0, AlphaList::Clip, marked(2.0));
        let d = b.finish();
        assert_eq!(marks(&d), [1.0, 2.0, 3.0]);
    }

    /// OpenAC `RetailAlphaQueueTests.TryAppend_CapacityOverflowDropsTheSubsetWithoutRecovery`
    /// and `TryAppend_ClipAndAlphaCapacitiesAreIndependent`: 3000 entries
    /// per list, the one beyond dropped; the other list still takes one.
    #[test]
    fn a_full_list_drops_the_entry() {
        let mut b = StreamBuilder::new();
        for _ in 0..=ALPHA_LIST_CAPACITY {
            b.push_particle(1.0, AlphaList::Clip, marked(0.0));
        }
        b.push_particle(1.0, AlphaList::Alpha, marked(0.0));
        b.end_cell();
        assert_eq!(b.pending_alpha(), (ALPHA_LIST_CAPACITY, 1));
        assert_eq!(b.alpha_dropped, 1);
        let d = b.finish();
        assert_eq!(d.alpha.len(), ALPHA_LIST_CAPACITY + 1);
    }

    #[test]
    fn sky_particles_are_a_step_of_their_own() {
        let mut b = StreamBuilder::new();
        let mesh = PendingParticle::Mesh {
            gpu: ParticleMeshGpu::zeroed(),
            gfx_obj_id: 0x0100_0001,
            sub: 0,
            blend: ParticleBlend::Alpha,
            cull: Cull::None,
        };
        b.push_particles_now(vec![(1.0, mesh), (3.0, mesh)]);
        let d = b.finish();
        assert_eq!(d.steps, vec![Step::Alpha(0..2)]);
        assert_eq!(d.particle_meshes.len(), 2);
        assert!(
            d.alpha
                .iter()
                .all(|item| matches!(item, AlphaItem::ParticleMesh(_)))
        );
        // The GPU layouts the pipelines read.
        assert_eq!(size_of::<BillboardGpu>(), 64);
        assert_eq!(size_of::<ParticleMeshGpu>(), 80);
    }

    #[test]
    fn the_whole_screen_needs_no_clip_region() {
        let mut d = FrameDraws::new();
        let mut views = ViewSet::default();
        for p in [
            Vec2::new(0.0, 100.0),
            Vec2::new(200.0, 100.0),
            Vec2::new(200.0, 0.0),
            Vec2::new(0.0, 0.0),
        ] {
            views.vertices.push(walk::ViewVertex {
                point: p,
                plane: Plane::default(),
            });
        }
        views.polys.push(walk::ViewPoly {
            count: 4,
            base: 0,
            min: Vec2::ZERO,
            max: Vec2::new(200.0, 100.0),
        });
        assert_eq!(d.clip_for(&views, 0, 200.0, 100.0), 0);
        assert_eq!(d.clip_for(&views, 0, 400.0, 100.0), 1);
        assert_eq!(d.clips[1].count, 4);
    }
}
