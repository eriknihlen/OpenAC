//! The collision data of the resident world: interior cells, building
//! shells, model collision trees, setups, and which landblocks' outdoor
//! cells exist.
//!
//! One store, filled as landblocks stream in and emptied as they leave. The
//! movement sweep and the object-shadow flood only read it.

use std::collections::{HashMap, HashSet};
use std::sync::LazyLock;

use ac_dat::objects::{CellStruct, EnvCell, EnvCellFlags, GfxObj, GfxObjFlags, Setup, VertexArray};
use glam::{Quat, Vec3};

use crate::bsp::{BspError, CellBsp, PhysicsBsp, PhysicsBspNode};
use crate::cell_array::CellArray;
use crate::dat::{resolve_polygons, vec3};
use crate::geometry::Sphere;
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;

/// A rotation and an origin: where a cell, building or part sits in the
/// world.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Placement {
    /// Rotation from the local frame to the world.
    pub rotation: Quat,
    /// World position of the local origin.
    pub origin: Vec3,
}

impl Default for Placement {
    fn default() -> Self {
        Self {
            rotation: Quat::IDENTITY,
            origin: Vec3::ZERO,
        }
    }
}

impl Placement {
    /// A placement from its rotation and origin.
    #[must_use]
    pub const fn new(rotation: Quat, origin: Vec3) -> Self {
        Self { rotation, origin }
    }

    /// A placement with no rotation.
    #[must_use]
    pub const fn at(origin: Vec3) -> Self {
        Self {
            rotation: Quat::IDENTITY,
            origin,
        }
    }

    /// A world point carried into the local frame.
    #[must_use]
    pub fn to_local(&self, world: Vec3) -> Vec3 {
        self.rotation.inverse() * (world - self.origin)
    }

    /// A local point carried into the world.
    #[must_use]
    pub fn to_world(&self, local: Vec3) -> Vec3 {
        self.rotation * local + self.origin
    }
}

/// The axis-aligned bounds of a model's drawn vertices.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct VisualBounds {
    /// Smallest corner.
    pub min: Vec3,
    /// Largest corner.
    pub max: Vec3,
    /// Centre of the box.
    pub center: Vec3,
    /// Half the box diagonal.
    pub radius: f32,
    /// Half the box size on each axis.
    pub half_extents: Vec3,
}

impl VisualBounds {
    /// The bounds of a vertex array; all zero when it has no vertices.
    #[must_use]
    pub fn of_vertices(vertices: &VertexArray) -> Self {
        if vertices.vertices.is_empty() {
            return Self::default();
        }
        let mut min = Vec3::splat(f32::MAX);
        let mut max = Vec3::splat(f32::MIN);
        for (_, v) in &vertices.vertices {
            let p = vec3(v.origin);
            if p.x < min.x {
                min.x = p.x;
            }
            if p.y < min.y {
                min.y = p.y;
            }
            if p.z < min.z {
                min.z = p.z;
            }
            if p.x > max.x {
                max.x = p.x;
            }
            if p.y > max.y {
                max.y = p.y;
            }
            if p.z > max.z {
                max.z = p.z;
            }
        }
        let center = (min + max) * 0.5;
        let half_extents = (max - min) * 0.5;
        Self {
            min,
            max,
            center,
            radius: half_extents.length(),
            half_extents,
        }
    }
}

/// The collision of a model (GfxObj).
#[derive(Debug, Clone, PartialEq)]
pub struct GfxObjPhysics {
    /// The model id.
    pub id: u32,
    /// The collision tree and its polygons.
    pub bsp: PhysicsBsp,
    /// The root node's bounding sphere.
    pub bounding_sphere: Sphere,
    /// The drawn vertices' bounds, when known.
    pub visual_bounds: Option<VisualBounds>,
}

impl GfxObjPhysics {
    /// The collision of a model from the dat: `None` when it has no
    /// collision tree (no physics flag, or an empty tree).
    pub fn from_dat(gfx: &GfxObj) -> Option<Result<Self, BspError>> {
        if !gfx.flags.contains(GfxObjFlags::HAS_PHYSICS) {
            return None;
        }
        let tree = gfx.physics_bsp.as_ref()?;
        if tree.nodes.is_empty() {
            return None;
        }
        Some(
            PhysicsBsp::from_dat(tree, &gfx.physics_polygons, &gfx.vertices).map(|bsp| {
                let bounding_sphere =
                    bsp.nodes()
                        .first()
                        .map_or_else(Sphere::default, |n| match n {
                            PhysicsBspNode::Split { sphere, .. }
                            | PhysicsBspNode::Leaf { sphere, .. } => *sphere,
                        });
                Self {
                    id: gfx.id,
                    bsp,
                    bounding_sphere,
                    visual_bounds: Some(VisualBounds::of_vertices(&gfx.vertices)),
                }
            }),
        )
    }

    /// True when the model has a collision tree.
    #[must_use]
    pub fn has_physics(&self) -> bool {
        !self.bsp.is_empty()
    }
}

/// The collision numbers of a setup (a multi-part object).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SetupPhysics {
    /// The setup id.
    pub id: u32,
    /// Upright cylinders: base centre, radius, height.
    pub cyl_spheres: Vec<(Vec3, f32, f32)>,
    /// Spheres.
    pub spheres: Vec<Sphere>,
    /// Height, metres.
    pub height: f32,
    /// Radius, metres.
    pub radius: f32,
    /// How high the object may step up, metres.
    pub step_up_height: f32,
    /// How far the object may step down, metres.
    pub step_down_height: f32,
}

impl SetupPhysics {
    /// The collision numbers of a setup from the dat.
    #[must_use]
    pub fn from_dat(setup: &Setup) -> Self {
        Self {
            id: setup.id,
            cyl_spheres: setup
                .cyl_spheres
                .iter()
                .map(|c| (vec3(c.origin), c.radius, c.height))
                .collect(),
            spheres: setup
                .spheres
                .iter()
                .map(|s| Sphere::new(vec3(s.origin), s.radius))
                .collect(),
            height: setup.height,
            radius: setup.radius,
            step_up_height: setup.step_up_height,
            step_down_height: setup.step_down_height,
        }
    }
}

/// The collision of one interior cell (a room of a building or dungeon).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CellPhysics {
    /// The cell id.
    pub id: u32,
    /// The room's collision tree.
    pub physics_bsp: PhysicsBsp,
    /// The room's containment tree.
    pub containment_bsp: CellBsp,
    /// Where the room sits in the world.
    pub placement: Placement,
    /// The room's portals, in file order.
    pub portals: Vec<PortalInfo>,
    /// The room's drawn polygons, which the portals name.
    pub portal_polygons: HashMap<u16, ResolvedPolygon>,
    /// The cells visible from this one, in file order (full ids).
    pub visible_cells: CellArray,
    /// The room can see outdoors.
    pub seen_outside: bool,
    /// The object that guards entry (a house), 0 for none.
    pub restriction_obj: u32,
}

impl CellPhysics {
    /// The collision of an interior cell from its dat record and shape,
    /// placed in the world. `None` when the shape has no containment tree
    /// (such a cell is never cached).
    pub fn from_dat(
        id: u32,
        env_cell: &EnvCell,
        cell_struct: &CellStruct,
        placement: Placement,
    ) -> Option<Result<Self, BspError>> {
        if cell_struct.cell_bsp.nodes.is_empty() {
            return None;
        }
        let build = || -> Result<Self, BspError> {
            let physics_bsp = PhysicsBsp::from_cell_struct(cell_struct)?;
            let containment_bsp = CellBsp::from_dat(&cell_struct.cell_bsp)?;
            let portal_polygons =
                resolve_polygons(&cell_struct.polygons, &cell_struct.vertex_array);
            let portals = env_cell
                .cell_portals
                .iter()
                .map(|p| PortalInfo::new(p.other_cell_id, p.polygon_id, p.flags))
                .collect();
            let mut visible_cells = CellArray::new();
            let prefix = id & 0xFFFF_0000;
            for low in &env_cell.visible_cells {
                visible_cells.add(prefix | u32::from(*low));
            }
            Ok(Self {
                id,
                physics_bsp,
                containment_bsp,
                placement,
                portals,
                portal_polygons,
                visible_cells,
                seen_outside: env_cell.flags.contains(EnvCellFlags::SEEN_OUTSIDE),
                restriction_obj: env_cell.restriction_obj,
            })
        };
        Some(build())
    }

    /// True when the room has a collision tree.
    #[must_use]
    pub fn has_physics(&self) -> bool {
        !self.physics_bsp.is_empty()
    }

    /// True when the room has a containment tree.
    #[must_use]
    pub fn has_containment(&self) -> bool {
        !self.containment_bsp.nodes().is_empty()
    }
}

/// A portal from a building's outside into one of its cells.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BldPortalInfo {
    /// The full id of the cell on the other side.
    pub other_cell_id: u32,
    /// The portal's index in that cell; negative for none.
    pub other_portal_id: i16,
    /// Portal flags.
    pub flags: u16,
}

impl BldPortalInfo {
    /// The portal must match its partner exactly.
    #[must_use]
    pub fn exact_match(&self) -> bool {
        self.flags & 0x1 != 0
    }
}

/// A building's outside shell, as the outdoor cell it stands in sees it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct BuildingPhysics {
    /// Where the building sits in the world.
    pub placement: Placement,
    /// Portals into the building's cells.
    pub portals: Vec<BldPortalInfo>,
    /// The shell model (the first part of a setup shell), 0 for none.
    pub model_id: u32,
}

/// The collision data of the resident world.
#[derive(Debug, Clone, Default)]
pub struct CollisionWorld {
    gfx_objs: HashMap<u32, GfxObjPhysics>,
    setups: HashMap<u32, SetupPhysics>,
    cells: HashMap<u32, CellPhysics>,
    buildings: HashMap<u32, BuildingPhysics>,
    visual_bounds: HashMap<u32, VisualBounds>,
    /// The landblocks (by prefix) whose outdoor cells exist, with their
    /// world origin.
    terrain_origins: HashMap<u32, Vec3>,
    /// Interior cells known to the cell graph (cached from dat records).
    graph_env_cells: HashSet<u32>,
    curr_cell: Option<u32>,
}

static EMPTY: LazyLock<CollisionWorld> = LazyLock::new(CollisionWorld::default);

impl CollisionWorld {
    /// An empty world.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A shared empty world, for queries made without one.
    #[must_use]
    pub fn empty() -> &'static Self {
        &EMPTY
    }

    // ── Models and setups ──────────────────────────────────────────────

    /// Caches a model from the dat: its drawn bounds always, its collision
    /// when it has any (first one cached wins).
    pub fn cache_gfx_obj(&mut self, gfx: &GfxObj) -> Result<(), BspError> {
        self.visual_bounds
            .entry(gfx.id)
            .or_insert_with(|| VisualBounds::of_vertices(&gfx.vertices));
        if self.gfx_objs.contains_key(&gfx.id) {
            return Ok(());
        }
        if let Some(physics) = GfxObjPhysics::from_dat(gfx) {
            self.gfx_objs.insert(gfx.id, physics?);
        }
        Ok(())
    }

    /// Stores a prepared model collision (replacing any).
    pub fn insert_gfx_obj(&mut self, physics: GfxObjPhysics) {
        self.gfx_objs.insert(physics.id, physics);
    }

    /// The collision of a model.
    #[must_use]
    pub fn gfx_obj(&self, id: u32) -> Option<&GfxObjPhysics> {
        self.gfx_objs.get(&id)
    }

    /// The drawn bounds of a model.
    #[must_use]
    pub fn visual_bounds(&self, id: u32) -> Option<&VisualBounds> {
        self.visual_bounds.get(&id)
    }

    /// Stores a model's drawn bounds.
    pub fn insert_visual_bounds(&mut self, id: u32, bounds: VisualBounds) {
        self.visual_bounds.insert(id, bounds);
    }

    /// Caches a setup's collision numbers (first one cached wins).
    pub fn cache_setup(&mut self, setup: &Setup) {
        self.setups
            .entry(setup.id)
            .or_insert_with(|| SetupPhysics::from_dat(setup));
    }

    /// The collision numbers of a setup.
    #[must_use]
    pub fn setup(&self, id: u32) -> Option<&SetupPhysics> {
        self.setups.get(&id)
    }

    // ── Interior cells ─────────────────────────────────────────────────

    /// Caches an interior cell from the dat (first one cached wins); a cell
    /// without a containment tree is not cached.
    pub fn cache_cell_struct(
        &mut self,
        id: u32,
        env_cell: &EnvCell,
        cell_struct: &CellStruct,
        placement: Placement,
    ) -> Result<(), BspError> {
        if cell_struct.cell_bsp.nodes.is_empty() {
            return Ok(());
        }
        self.graph_env_cells.insert(id);
        if self.cells.contains_key(&id) {
            return Ok(());
        }
        if let Some(cell) = CellPhysics::from_dat(id, env_cell, cell_struct, placement) {
            self.cells.insert(id, cell?);
        }
        Ok(())
    }

    /// Stores a prepared interior cell (replacing any). It is not entered
    /// into the cell graph.
    pub fn insert_cell(&mut self, cell: CellPhysics) {
        self.cells.insert(cell.id, cell);
    }

    /// The interior cell with this id.
    #[must_use]
    pub fn cell(&self, id: u32) -> Option<&CellPhysics> {
        self.cells.get(&id)
    }

    /// The ids of every cached interior cell.
    pub fn cell_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.cells.keys().copied()
    }

    /// Drops the interior cells of a landblock.
    pub fn remove_cells_for_landblock(&mut self, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        self.cells.retain(|id, _| id & 0xFFFF_0000 != prefix);
    }

    // ── Buildings ──────────────────────────────────────────────────────

    /// Caches a building under the outdoor cell it stands in (first one
    /// cached wins).
    pub fn cache_building(&mut self, landcell_id: u32, building: BuildingPhysics) {
        self.buildings.entry(landcell_id).or_insert(building);
    }

    /// Stores a building (replacing any).
    pub fn insert_building(&mut self, landcell_id: u32, building: BuildingPhysics) {
        self.buildings.insert(landcell_id, building);
    }

    /// The building standing in an outdoor cell.
    #[must_use]
    pub fn building(&self, landcell_id: u32) -> Option<&BuildingPhysics> {
        self.buildings.get(&landcell_id)
    }

    /// The outdoor cells that hold a building, in no particular order.
    pub fn building_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.buildings.keys().copied()
    }

    /// Drops the buildings of a landblock.
    pub fn remove_buildings_for_landblock(&mut self, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        self.buildings.retain(|id, _| id & 0xFFFF_0000 != prefix);
    }

    // ── Cell graph ─────────────────────────────────────────────────────

    /// Makes a landblock's 64 outdoor cells exist, its corner at `origin`.
    pub fn register_terrain(&mut self, landblock_id: u32, origin: Vec3) {
        self.terrain_origins
            .insert(landblock_id & 0xFFFF_0000, origin);
    }

    /// The world origin of the landblock holding `cell_id`, when its outdoor
    /// cells exist.
    #[must_use]
    pub fn terrain_origin(&self, cell_id: u32) -> Option<Vec3> {
        self.terrain_origins.get(&(cell_id & 0xFFFF_0000)).copied()
    }

    /// True when the cell exists: an outdoor cell of a registered landblock,
    /// or an interior cell entered into the graph.
    #[must_use]
    pub fn is_visible(&self, id: u32) -> bool {
        if id == 0 {
            return false;
        }
        let low = id & 0xFFFF;
        if low >= 0x100 {
            return self.graph_env_cells.contains(&id);
        }
        if !(1..=0x40).contains(&low) {
            return false;
        }
        self.terrain_origins.contains_key(&(id & 0xFFFF_0000))
    }

    /// Forgets a landblock in the cell graph: its outdoor and interior
    /// cells (and the current cell when it is one of them).
    pub fn remove_landblock_from_graph(&mut self, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        if self.curr_cell.is_some_and(|c| c & 0xFFFF_0000 == prefix) {
            self.curr_cell = None;
        }
        self.terrain_origins.remove(&prefix);
        self.graph_env_cells.retain(|id| id & 0xFFFF_0000 != prefix);
    }

    /// Forgets a landblock's interior cells in the cell graph.
    pub fn remove_env_cells_from_graph(&mut self, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        if self
            .curr_cell
            .is_some_and(|c| c & 0xFFFF_0000 == prefix && c & 0xFFFF >= 0x100)
        {
            self.curr_cell = None;
        }
        self.graph_env_cells.retain(|id| id & 0xFFFF_0000 != prefix);
    }

    /// The cell the player is in, as last set.
    #[must_use]
    pub fn curr_cell(&self) -> Option<u32> {
        self.curr_cell
    }

    /// Sets (or clears) the player's cell.
    pub fn set_curr_cell(&mut self, cell: Option<u32>) {
        self.curr_cell = cell;
    }
}
