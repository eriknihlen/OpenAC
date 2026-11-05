//! The physics engine of one session: the resident landblocks' terrain, the
//! collision world, the object shadows, and the two entry points that move
//! things through them (resolve a move, place an object).

use ac_core::physics::{PhysicsState, TransientState, adjust_to_outside, get_block_offset};
use glam::{Quat, Vec3};

use crate::body::PhysicsBody;
use crate::cell_array::CellArray;
use crate::cell_surface::CellSurface;
use crate::cell_transit;
use crate::geometry::Plane;
use crate::globals::{EPSILON, EPSILON_SQ, FLOOR_Z};
use crate::portal::PortalPlane;
use crate::set_position::{
    PhysicsPlacementClass, PhysicsResidenceDisposition, PhysicsSetPositionCollisionReport,
    PhysicsSetPositionError, PhysicsSetPositionFlags, PhysicsSetPositionRequest,
    PhysicsSetPositionResult,
};
use crate::shadow_registry::{PhysicsShadowCommitAction, ShadowObjectRegistry};
use crate::slot_map::SlotMap;
use crate::state::ObjectInfoState;
use crate::state::TransitionState;
use crate::terrain::TerrainSurface;
use crate::transition::{
    CollisionSphere, ObjectRestrictions, Transition, TransitionCellCollisionPhase,
};
use crate::world::CollisionWorld;

/// Side of a landblock, metres.
const BLOCK_SIZE: f32 = 192.0;

/// One resident landblock: its ground, its cells' surfaces and portals, and
/// where its corner is in the world.
#[derive(Debug, Clone, PartialEq)]
pub struct LandblockPhysics {
    /// The ground.
    pub terrain: TerrainSurface,
    /// Interior cell surfaces (coarse floor samples).
    pub cells: Vec<CellSurface>,
    /// Portal planes.
    pub portals: Vec<PortalPlane>,
    /// World x of the corner.
    pub world_offset_x: f32,
    /// World y of the corner.
    pub world_offset_y: f32,
}

/// The ground triangle under a point, as the sweep tests it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TerrainWalkableSample {
    /// The triangle's plane (world frame).
    pub plane: Plane,
    /// Its corners (world frame).
    pub vertices: [Vec3; 3],
    /// How deep a body sinks here, metres.
    pub water_depth: f32,
    /// The point is in deep water (0.45 m or more).
    pub is_water: bool,
    /// The outdoor cell holding the point.
    pub cell_id: u32,
    /// The whole landblock is open sea.
    pub block_entirely_water: bool,
}

/// What a resolved move reports.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ResolveResult {
    /// Where the body ended.
    pub position: Vec3,
    /// The cell it ended in.
    pub cell_id: u32,
    /// It stands on something.
    pub is_on_ground: bool,
    /// It hit something.
    pub collision_normal_valid: bool,
    /// The outward normal of what it hit.
    pub collision_normal: Vec3,
    /// The sweep succeeded (else the body should hold where it was).
    pub ok: bool,
    /// The orientation it ended with.
    pub orientation: Quat,
    /// Touching a surface.
    pub in_contact: bool,
    /// On walkable ground.
    pub on_walkable: bool,
    /// The surface touched.
    pub contact_plane: Plane,
    /// Its cell.
    pub contact_plane_cell_id: u32,
    /// It is water.
    pub contact_plane_is_water: bool,
    /// The last object hit, 0 for none.
    pub last_collided_object_id: u32,
    /// The environment blocked the sweep.
    pub collided_with_environment: bool,
}

impl Default for ResolveResult {
    fn default() -> Self {
        Self {
            position: Vec3::ZERO,
            cell_id: 0,
            is_on_ground: false,
            collision_normal_valid: false,
            collision_normal: Vec3::ZERO,
            ok: true,
            orientation: Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            in_contact: false,
            on_walkable: false,
            contact_plane: Plane::default(),
            contact_plane_cell_id: 0,
            contact_plane_is_water: false,
            last_collided_object_id: 0,
            collided_with_environment: false,
        }
    }
}

/// A move to resolve.
#[derive(Debug, Clone, PartialEq)]
pub struct ResolveRequest {
    /// Where the body is.
    pub current_pos: Vec3,
    /// Where it wants to go.
    pub target_pos: Vec3,
    /// Its cell.
    pub cell_id: u32,
    /// Sphere radius (when no sphere list is given).
    pub sphere_radius: f32,
    /// Body height: above 0 gives a head sphere (when no list is given).
    pub sphere_height: f32,
    /// Step-up height.
    pub step_up_height: f32,
    /// Step-down height.
    pub step_down_height: f32,
    /// Standing on the ground (used when no body is given).
    pub is_on_ground: bool,
    /// The mover's state bits.
    pub mover_flags: ObjectInfoState,
    /// The mover's id.
    pub moving_entity_id: u32,
    /// The foot sphere's centre in the body frame (default one radius up).
    pub local_sphere_origin: Option<Vec3>,
    /// Orientation at the start.
    pub begin_orientation: Option<Quat>,
    /// Orientation at the end.
    pub end_orientation: Option<Quat>,
    /// A missile's designated target.
    pub designated_target_id: u32,
    /// The body's own sphere list (overrides radius and height).
    pub sphere_list: Vec<CollisionSphere>,
    /// The sphere list's scale.
    pub sphere_scale: f32,
}

impl Default for ResolveRequest {
    fn default() -> Self {
        Self {
            current_pos: Vec3::ZERO,
            target_pos: Vec3::ZERO,
            cell_id: 0,
            sphere_radius: 0.0,
            sphere_height: 0.0,
            step_up_height: 0.0,
            step_down_height: 0.0,
            is_on_ground: false,
            mover_flags: ObjectInfoState(ObjectInfoState::NONE),
            moving_entity_id: 0,
            local_sphere_origin: None,
            begin_orientation: None,
            end_orientation: None,
            designated_target_id: 0,
            sphere_list: Vec::new(),
            sphere_scale: 1.0,
        }
    }
}

/// Observes (and may replace) each primary-cell collision phase result.
pub type CellCollisionHook = Box<
    dyn Fn(&mut Transition, TransitionCellCollisionPhase, u32, TransitionState) -> TransitionState
        + Send,
>;

/// The scatter placement's source of random numbers (0..1).
pub type PositionRandom = Box<dyn FnMut() -> f64 + Send>;

/// The physics of one session. `Send`: an engine can be built on one thread
/// and used on another (a window builds its camera's collision on its
/// loader thread).
pub struct PhysicsEngine {
    landblocks: SlotMap<LandblockPhysics>,
    world: Option<CollisionWorld>,
    /// The per-cell object lists.
    pub shadows: ShadowObjectRegistry,
    objects: Option<ObjectRestrictions>,
    set_position_random: PositionRandom,
    pub(crate) cell_collision_hook: Option<CellCollisionHook>,
}

impl std::fmt::Debug for PhysicsEngine {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PhysicsEngine")
            .field("landblocks", &self.landblocks.len())
            .field("world", &self.world.is_some())
            .field("shadows", &self.shadows.total_registered())
            .finish_non_exhaustive()
    }
}

impl Default for PhysicsEngine {
    fn default() -> Self {
        Self::new()
    }
}

/// The default scatter source: a small generator seeded from the process's
/// hash randomness.
fn default_random() -> PositionRandom {
    use std::collections::hash_map::RandomState;
    use std::hash::{BuildHasher, Hasher};
    let mut state = RandomState::new().build_hasher().finish() | 1;
    Box::new(move || {
        // splitmix64
        state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        (z >> 11) as f64 / (1u64 << 53) as f64
    })
}

fn is_outdoor_low(cell: u32) -> bool {
    (1..=0x40).contains(&(cell & 0xFFFF))
}

impl PhysicsEngine {
    /// An engine with no landblocks and no collision world.
    #[must_use]
    pub fn new() -> Self {
        Self {
            landblocks: SlotMap::default(),
            world: None,
            shadows: ShadowObjectRegistry::new(),
            objects: None,
            set_position_random: default_random(),
            cell_collision_hook: None,
        }
    }

    // ── World and objects ──────────────────────────────────────────────

    /// The collision world, when attached.
    #[must_use]
    pub fn world(&self) -> Option<&CollisionWorld> {
        self.world.as_ref()
    }

    /// The collision world, mutably, when attached.
    pub fn world_mut(&mut self) -> Option<&mut CollisionWorld> {
        self.world.as_mut()
    }

    /// Attaches the collision world. Landblocks added before it was
    /// attached are not entered into its cell graph (as OpenAC: a landblock
    /// registers its outdoor cells only with a world present).
    pub fn attach_world(&mut self, world: CollisionWorld) {
        self.world = Some(world);
    }

    /// The world the shadow floods use: the attached one, else an empty one.
    #[must_use]
    pub fn flood_world(&self) -> &CollisionWorld {
        self.world.as_ref().unwrap_or(CollisionWorld::empty())
    }

    /// The shadow registry and the world its floods use, together.
    pub fn shadows_and_world(&mut self) -> (&mut ShadowObjectRegistry, &CollisionWorld) {
        (
            &mut self.shadows,
            self.world.as_ref().unwrap_or(CollisionWorld::empty()),
        )
    }

    /// The client objects entry checks consult, when bound.
    #[must_use]
    pub fn objects(&self) -> Option<&ObjectRestrictions> {
        self.objects.as_ref()
    }

    /// Binds (or unbinds) the client objects.
    pub fn set_objects(&mut self, objects: Option<ObjectRestrictions>) {
        self.objects = objects;
    }

    /// Replaces the source of random numbers (0..1) scatter placement uses.
    pub fn set_position_random(&mut self, random: PositionRandom) {
        self.set_position_random = random;
    }

    /// Observes each primary-cell collision phase of every transition.
    pub fn set_cell_collision_hook(&mut self, hook: Option<CellCollisionHook>) {
        self.cell_collision_hook = hook;
    }

    // ── Landblocks ─────────────────────────────────────────────────────

    /// Number of resident landblocks.
    #[must_use]
    pub fn landblock_count(&self) -> usize {
        self.landblocks.len()
    }

    /// The resident landblock ids, in table order.
    pub fn landblock_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.landblocks.keys()
    }

    /// A resident landblock.
    #[must_use]
    pub fn landblock(&self, landblock_id: u32) -> Option<&LandblockPhysics> {
        self.landblocks.get(landblock_id)
    }

    /// True when a landblock with the same prefix is resident.
    #[must_use]
    pub fn is_landblock_terrain_resident(&self, cell_or_landblock_id: u32) -> bool {
        let prefix = cell_or_landblock_id & 0xFFFF_0000;
        self.landblocks.keys().any(|k| k & 0xFFFF_0000 == prefix)
    }

    /// True when every landblock within `radius` (on the map) is resident.
    #[must_use]
    pub fn is_neighborhood_terrain_resident(&self, cell_or_landblock_id: u32, radius: i32) -> bool {
        let resident: std::collections::HashSet<u32> =
            self.landblocks.keys().map(|k| k & 0xFFFF_0000).collect();
        let cx = ((cell_or_landblock_id >> 24) & 0xFF) as i32;
        let cy = ((cell_or_landblock_id >> 16) & 0xFF) as i32;
        for dx in -radius..=radius {
            for dy in -radius..=radius {
                let (nx, ny) = (cx + dx, cy + dy);
                if !(0..=254).contains(&nx) || !(0..=254).contains(&ny) {
                    continue;
                }
                let prefix = ((nx as u32) << 24) | ((ny as u32) << 16);
                if !resident.contains(&prefix) {
                    return false;
                }
            }
        }
        true
    }

    /// Makes a landblock resident (replacing one with the same id). Its
    /// outdoor cells enter the attached world's cell graph.
    pub fn add_landblock(
        &mut self,
        landblock_id: u32,
        terrain: TerrainSurface,
        cells: Vec<CellSurface>,
        portals: Vec<PortalPlane>,
        world_offset_x: f32,
        world_offset_y: f32,
    ) {
        self.landblocks.insert(
            landblock_id,
            LandblockPhysics {
                terrain,
                cells,
                portals,
                world_offset_x,
                world_offset_y,
            },
        );
        if let Some(world) = &mut self.world {
            world.register_terrain(landblock_id, Vec3::new(world_offset_x, world_offset_y, 0.0));
        }
    }

    /// Removes a landblock with its static objects, cells and buildings.
    pub fn remove_landblock(&mut self, landblock_id: u32) {
        self.landblocks.remove(landblock_id);
        self.shadows
            .deregister_static_owners_for_landblock(landblock_id);
        self.shadows.remove_landblock(landblock_id);
        if let Some(world) = &mut self.world {
            world.remove_cells_for_landblock(landblock_id);
            world.remove_buildings_for_landblock(landblock_id);
            if world
                .curr_cell()
                .is_some_and(|c| c & 0xFFFF_0000 == landblock_id & 0xFFFF_0000)
            {
                world.set_curr_cell(None);
            }
            world.remove_landblock_from_graph(landblock_id);
        }
    }

    /// Removes every landblock and every shadow.
    pub fn clear(&mut self) {
        let ids: Vec<u32> = self.landblocks.keys().collect();
        for id in ids {
            self.remove_landblock(id);
        }
        self.shadows.clear();
    }

    /// Keeps a landblock's terrain but drops its cells, portals, statics,
    /// interior cells and buildings.
    pub fn demote_landblock_to_terrain(&mut self, landblock_id: u32) {
        let canonical = (landblock_id & 0xFFFF_0000) | 0xFFFF;
        if let Some(lb) = self.landblocks.get_mut(canonical) {
            lb.cells.clear();
            lb.portals.clear();
        }
        self.shadows
            .deregister_static_owners_for_landblock(canonical);
        self.shadows.remove_landblock(canonical);
        if let Some(world) = &mut self.world {
            world.remove_cells_for_landblock(canonical);
            world.remove_buildings_for_landblock(canonical);
            world.remove_env_cells_from_graph(canonical);
        }
    }

    fn landblock_at(
        &self,
        world_x: f32,
        world_y: f32,
    ) -> Option<(u32, &LandblockPhysics, f32, f32)> {
        for (key, lb) in self.landblocks.iter() {
            let local_x = world_x - lb.world_offset_x;
            let local_y = world_y - lb.world_offset_y;
            if (0.0..BLOCK_SIZE).contains(&local_x) && (0.0..BLOCK_SIZE).contains(&local_y) {
                return Some((key, lb, local_x, local_y));
            }
        }
        None
    }

    /// The landblock holding a world point and its corner.
    #[must_use]
    pub fn try_get_landblock_context(&self, world_x: f32, world_y: f32) -> Option<(u32, f32, f32)> {
        self.landblock_at(world_x, world_y)
            .map(|(k, lb, _, _)| (k, lb.world_offset_x, lb.world_offset_y))
    }

    /// Ground height under a world point.
    #[must_use]
    pub fn sample_terrain_z(&self, world_x: f32, world_y: f32) -> Option<f32> {
        self.landblock_at(world_x, world_y)
            .map(|(_, lb, lx, ly)| lb.terrain.sample_z(lx, ly))
    }

    /// Water depth under a world point (0 off the resident world).
    #[must_use]
    pub fn sample_water_depth(&self, world_x: f32, world_y: f32) -> f32 {
        self.landblock_at(world_x, world_y)
            .map_or(0.0, |(_, lb, lx, ly)| lb.terrain.sample_water_depth(lx, ly))
    }

    /// The ground plane under a world point.
    #[must_use]
    pub fn sample_terrain_plane(&self, world_x: f32, world_y: f32) -> Option<Plane> {
        self.landblock_at(world_x, world_y).map(|(_, lb, lx, ly)| {
            let (z, normal) = lb.terrain.sample_surface(lx, ly);
            let d = -(normal.x * world_x + normal.y * world_y + normal.z * z);
            Plane::new(normal, d)
        })
    }

    /// The ground triangle under a world point.
    #[must_use]
    pub fn sample_terrain_walkable(
        &self,
        world_x: f32,
        world_y: f32,
    ) -> Option<TerrainWalkableSample> {
        self.landblock_at(world_x, world_y)
            .map(|(k, lb, lx, ly)| build_terrain_walkable_sample(k, lb, lx, ly))
    }

    /// The ground triangle under a world point, only when the point is in
    /// the given outdoor cell (a cell id with no landblock prefix matches
    /// whichever resident landblock holds the point).
    #[must_use]
    pub fn sample_terrain_walkable_in_cell(
        &self,
        cell_id: u32,
        world_x: f32,
        world_y: f32,
    ) -> Option<TerrainWalkableSample> {
        let low_cell_id = cell_id & 0xFFFF;
        if !(1..=0x40).contains(&low_cell_id) {
            return None;
        }
        let requested_prefix = cell_id & 0xFFFF_0000;
        for (key, lb) in self.landblocks.iter() {
            if requested_prefix != 0 && key & 0xFFFF_0000 != requested_prefix {
                continue;
            }
            let local_x = world_x - lb.world_offset_x;
            let local_y = world_y - lb.world_offset_y;
            if requested_prefix == 0
                && (!(0.0..BLOCK_SIZE).contains(&local_x) || !(0.0..BLOCK_SIZE).contains(&local_y))
            {
                continue;
            }
            let cell_index = low_cell_id as i32 - 1;
            let cell_x = cell_index / TerrainSurface::CELLS_PER_SIDE as i32;
            let cell_y = cell_index % TerrainSurface::CELLS_PER_SIDE as i32;
            let min_x = cell_x as f32 * TerrainSurface::CELL_SIZE;
            let min_y = cell_y as f32 * TerrainSurface::CELL_SIZE;
            let max_x = min_x + TerrainSurface::CELL_SIZE;
            let max_y = min_y + TerrainSurface::CELL_SIZE;
            if local_x < min_x || local_x >= max_x || local_y < min_y || local_y >= max_y {
                return None;
            }
            return Some(build_terrain_walkable_sample(key, lb, local_x, local_y));
        }
        None
    }

    /// Sets the player's cell in the cell graph, when the cell exists.
    pub fn update_player_curr_cell(&mut self, cell_id: u32) {
        if let Some(world) = &mut self.world
            && world.is_visible(cell_id)
        {
            world.set_curr_cell(Some(cell_id));
        }
    }

    /// The outdoor cell under a world point (indoor and zero fallbacks pass
    /// through; off the resident world the fallback is kept).
    #[must_use]
    pub fn resolve_cell_id(
        &self,
        world_pos: Vec3,
        _sphere_radius: f32,
        fallback_cell_id: u32,
    ) -> u32 {
        if fallback_cell_id == 0 {
            return 0;
        }
        if fallback_cell_id & 0xFFFF >= 0x0100 {
            return fallback_cell_id;
        }
        match self.landblock_at(world_pos.x, world_pos.y) {
            Some((key, lb, lx, ly)) => {
                (key & 0xFFFF_0000) | lb.terrain.compute_outdoor_cell_id(lx, ly)
            }
            None => fallback_cell_id,
        }
    }

    /// True when a spawn cell is ready: any outdoor cell, or a cached
    /// interior cell.
    #[must_use]
    pub fn is_spawn_cell_ready(&self, cell_id: u32) -> bool {
        if cell_id & 0xFFFF < 0x0100 {
            return true;
        }
        self.world
            .as_ref()
            .is_some_and(|w| w.cell(cell_id).is_some())
    }

    /// The cell holding a world point, from a seed cell: indoors the seed or
    /// a cell visible from it (falling out to the outdoors only when the seed
    /// sees outside), outdoors the cell under the point.
    #[must_use]
    pub fn adjust_position(&self, seed_cell_id: u32, world_point: Vec3) -> (u32, bool) {
        if seed_cell_id == 0 {
            return (seed_cell_id, false);
        }
        if seed_cell_id & 0xFFFF >= 0x0100 {
            let Some(world) = &self.world else {
                return (seed_cell_id, false);
            };
            let child =
                cell_transit::find_visible_child_cell(world, seed_cell_id, world_point, true, None);
            if child != 0 {
                return (child, true);
            }
            match world.cell(seed_cell_id) {
                Some(c) if c.seen_outside => {}
                _ => return (seed_cell_id, false),
            }
        }
        match self.landblock_at(world_point.x, world_point.y) {
            Some((key, lb, lx, ly)) => (
                (key & 0xFFFF_0000) | lb.terrain.compute_outdoor_cell_id(lx, ly),
                true,
            ),
            None => (seed_cell_id, false),
        }
    }

    // ── Set position ───────────────────────────────────────────────────

    fn adjust_set_position(
        &self,
        seed_cell_id: u32,
        cell_local_position: Vec3,
        first_world_sphere_center: Vec3,
        footprint: &mut CellArray,
    ) -> (u32, Vec3, bool) {
        footprint.add(seed_cell_id);
        let low = seed_cell_id & 0xFFFF;
        let low_in_range = matches!(low, 1..=0x40 | 0x100..=0xFFFD | 0xFFFF);
        if !low_in_range {
            return (seed_cell_id, cell_local_position, false);
        }

        let mut adjusted_cell = seed_cell_id;
        let mut adjusted_local = cell_local_position;
        if low >= 0x0100 {
            let Some(world) = self
                .world
                .as_ref()
                .filter(|w| w.cell(seed_cell_id).is_some())
            else {
                return (seed_cell_id, cell_local_position, false);
            };
            let child = cell_transit::find_visible_child_cell(
                world,
                seed_cell_id,
                first_world_sphere_center,
                true,
                Some(footprint),
            );
            if child != 0 {
                return (child, adjusted_local, world.cell(child).is_some());
            }
            match world.cell(seed_cell_id) {
                Some(c) if c.seen_outside => {}
                _ => return (seed_cell_id, adjusted_local, false),
            }
        }

        let mut local = adjusted_local.to_array();
        let adjusted = adjust_to_outside(&mut adjusted_cell, &mut local);
        adjusted_local = Vec3::from_array(local);
        footprint.add(adjusted_cell);
        let resident = adjusted && self.is_landblock_terrain_resident(adjusted_cell);
        (adjusted_cell, adjusted_local, resident)
    }

    /// Places an object: finds a free spot at (or, when allowed, near) the
    /// requested position. `handle_collisions` sees a failed placement's
    /// collisions and may claim them.
    pub fn set_position(
        &mut self,
        request: &PhysicsSetPositionRequest,
        mut handle_collisions: Option<&mut dyn FnMut(&PhysicsSetPositionCollisionReport) -> bool>,
    ) -> PhysicsSetPositionResult {
        let mut transition = Transition::new();
        transition.sphere_path.footprint_active = true;
        initialize_set_position_transition(&mut transition, request);
        let random_only = request
            .flags
            .contains(PhysicsSetPositionFlags::RANDOM_SCATTER);
        let mut result = if random_only {
            self.set_scatter_position_internal(&mut transition, request, &mut handle_collisions)
        } else {
            let r = self.set_position_internal(&mut transition, request, &mut handle_collisions);
            if r.error != PhysicsSetPositionError::Ok
                && request.flags.contains(PhysicsSetPositionFlags::SCATTER)
            {
                self.set_scatter_position_internal(&mut transition, request, &mut handle_collisions)
            } else {
                r
            }
        };
        result.queried_cell_ids = transition
            .sphere_path
            .set_position_query_footprint
            .ordered_ids()
            .to_vec();
        result
    }

    fn set_scatter_position_internal(
        &mut self,
        transition: &mut Transition,
        request: &PhysicsSetPositionRequest,
        handle_collisions: &mut Option<&mut dyn FnMut(&PhysicsSetPositionCollisionReport) -> bool>,
    ) -> PhysicsSetPositionResult {
        let mut result = error_result(request, PhysicsSetPositionError::GeneralFailure);
        for _ in 0..request.scatter_attempts {
            let dx = (((self.set_position_random)() * 2.0) - 1.0) as f32 * request.scatter_radius_x;
            let dy = (((self.set_position_random)() * 2.0) - 1.0) as f32 * request.scatter_radius_y;
            let scattered = PhysicsSetPositionRequest {
                position: request.position + Vec3::new(dx, dy, 0.0),
                cell_local_position: request.cell_local_position + Vec3::new(dx, dy, 0.0),
                ..request.clone()
            };
            result = self.set_position_internal(transition, &scattered, handle_collisions);
            if result.error == PhysicsSetPositionError::Ok {
                break;
            }
        }
        result
    }

    fn set_position_internal(
        &self,
        transition: &mut Transition,
        request: &PhysicsSetPositionRequest,
        handle_collisions: &mut Option<&mut dyn FnMut(&PhysicsSetPositionCollisionReport) -> bool>,
    ) -> PhysicsSetPositionResult {
        transition.sphere_path.cell_candidates.clear();
        transition.sphere_path.clear_walkable();
        let spheres = &request.spheres;
        let sphere_scale = if spheres.is_empty() {
            1.0
        } else {
            request.scale
        };
        let first_local_center = if spheres.is_empty() {
            Vec3::new(0.0, 0.0, crate::globals::DUMMY_SPHERE_RADIUS)
        } else {
            spheres[0].origin * sphere_scale
        };
        let first_world_center = request.orientation * first_local_center + request.position;
        let (adjusted_cell, adjusted_local, resident) = self.adjust_set_position(
            request.cell_id,
            request.cell_local_position,
            first_world_center,
            &mut transition.sphere_path.set_position_query_footprint,
        );
        if !resident {
            return PhysicsSetPositionResult {
                error: PhysicsSetPositionError::Ok,
                residence: PhysicsResidenceDisposition::DeferredCell,
                position: request.position,
                orientation: request.orientation,
                cell_id: adjusted_cell,
                cell_local_position: adjusted_local,
                ..PhysicsSetPositionResult::default()
            };
        }

        let force_into_cell = matches!(
            request.placement_class,
            PhysicsPlacementClass::Hook
                | PhysicsPlacementClass::Storage
                | PhysicsPlacementClass::Corpse
        );
        if force_into_cell {
            if adjusted_cell == 0 {
                return error_result(request, PhysicsSetPositionError::NoCell);
            }
            let changed_cell = request.current_cell_id != Some(adjusted_cell);
            return PhysicsSetPositionResult {
                error: PhysicsSetPositionError::Ok,
                residence: PhysicsResidenceDisposition::Committed,
                position: request.position,
                orientation: request.orientation,
                cell_id: adjusted_cell,
                cell_local_position: adjusted_local,
                cell_changed: changed_cell,
                shadow_action: if changed_cell {
                    PhysicsShadowCommitAction::Recalculate
                } else {
                    PhysicsShadowCommitAction::None
                },
                forced_into_cell: true,
                ..PhysicsSetPositionResult::default()
            };
        }

        transition.sphere_path.init_path_spheres(
            request.position,
            request.position,
            adjusted_cell,
            spheres,
            sphere_scale,
            Some(request.orientation),
            Some(request.orientation),
        );
        transition.sphere_path.insert_type = crate::state::InsertType::Placement;
        transition.sphere_path.placement_allows_sliding =
            request.flags.contains(PhysicsSetPositionFlags::SLIDE);

        let mut valid = transition.find_valid_position(self);
        let sp = &transition.sphere_path;
        if valid && !request.flags.contains(PhysicsSetPositionFlags::SLIDE) {
            valid = accept_no_slide_placement(
                sp.cur_pos,
                request.position,
                sp.cur_cell_id,
                adjusted_cell,
            );
        }

        let ci = &transition.collision_info;
        let report = PhysicsSetPositionCollisionReport {
            contact_plane_valid: ci.contact_plane_valid,
            contact_plane: ci.contact_plane,
            contact_plane_cell_id: ci.contact_plane_cell_id,
            contact_plane_is_water: ci.contact_plane_is_water,
            last_known_contact_plane_valid: ci.last_known_contact_plane_valid,
            last_known_contact_plane: ci.last_known_contact_plane,
            last_known_contact_plane_cell_id: ci.last_known_contact_plane_cell_id,
            last_known_contact_plane_is_water: ci.last_known_contact_plane_is_water,
            sliding_normal_valid: ci.sliding_normal_valid,
            sliding_normal: ci.sliding_normal,
            collision_normal_valid: ci.collision_normal_valid,
            collision_normal: ci.collision_normal,
            collided_with_environment: ci.collided_with_environment,
            frames_stationary_fall: ci.frames_stationary_fall,
            adjust_offset: ci.adjust_offset,
            last_collided_object_id: ci.last_collided_object_guid,
            collided_object_ids: ci.collide_object_guids.clone(),
        };
        let collision_handler_result =
            !valid && handle_collisions.as_mut().is_some_and(|h| h(&report));
        if !valid {
            return PhysicsSetPositionResult {
                error: if collision_handler_result {
                    PhysicsSetPositionError::Collided
                } else {
                    PhysicsSetPositionError::NoValidPosition
                },
                residence: PhysicsResidenceDisposition::Unchanged,
                position: request.position,
                orientation: request.orientation,
                cell_id: request.cell_id,
                cell_local_position: request.cell_local_position,
                in_contact: ci.contact_plane_valid,
                on_walkable: is_walkable_contact(ci.contact_plane_valid, ci.contact_plane.normal),
                contact_plane: ci.contact_plane,
                contact_plane_cell_id: ci.contact_plane_cell_id,
                contact_plane_is_water: ci.contact_plane_is_water,
                sliding_normal_valid: ci.sliding_normal_valid,
                sliding_normal: ci.sliding_normal,
                collision_normal_valid: ci.collision_normal_valid,
                collision_normal: ci.collision_normal,
                frames_stationary_fall: ci.frames_stationary_fall,
                collision_handler_result,
                collided_with_environment: ci.collided_with_environment,
                collided_object_ids: report.collided_object_ids,
                ..PhysicsSetPositionResult::default()
            };
        }
        if sp.cur_cell_id == 0 {
            return error_result(request, PhysicsSetPositionError::NoCell);
        }

        let in_contact = ci.contact_plane_valid;
        let on_walkable = is_walkable_contact(in_contact, ci.contact_plane.normal);
        let offset = get_block_offset(adjusted_cell, sp.cur_cell_id);
        let result_local =
            adjusted_local + (sp.cur_pos - request.position) - Vec3::from_array(offset);
        let has_physics_bsp = request
            .mover_physics_state
            .contains(PhysicsState::HAS_PHYSICS_BSP);
        let transition_cells: Vec<u32> = sp.cell_candidates.ordered_ids().to_vec();
        let shadow_action = if has_physics_bsp {
            PhysicsShadowCommitAction::Recalculate
        } else if !transition_cells.is_empty() {
            PhysicsShadowCommitAction::Replace
        } else {
            PhysicsShadowCommitAction::Preserve
        };
        PhysicsSetPositionResult {
            error: PhysicsSetPositionError::Ok,
            residence: PhysicsResidenceDisposition::Committed,
            position: sp.cur_pos,
            orientation: request.orientation,
            cell_id: sp.cur_cell_id,
            cell_local_position: result_local,
            in_contact,
            on_walkable,
            contact_plane: ci.contact_plane,
            contact_plane_cell_id: ci.contact_plane_cell_id,
            contact_plane_is_water: ci.contact_plane_is_water,
            sliding_normal_valid: ci.sliding_normal_valid,
            sliding_normal: ci.sliding_normal,
            collision_normal_valid: ci.collision_normal_valid,
            collision_normal: ci.collision_normal,
            frames_stationary_fall: ci.frames_stationary_fall,
            collided_with_environment: ci.collided_with_environment,
            collision_handler_result,
            cell_changed: request.current_cell_id != Some(sp.cur_cell_id),
            shadow_action,
            cross_cell_ids: if shadow_action == PhysicsShadowCommitAction::Replace {
                transition_cells
            } else {
                Vec::new()
            },
            collided_object_ids: ci.collide_object_guids.clone(),
            queried_cell_ids: Vec::new(),
            forced_into_cell: false,
        }
    }

    // ── Resolve a move ─────────────────────────────────────────────────

    /// Moves a body from `current_pos` toward `target_pos` through the
    /// world, colliding, stepping and sliding as retail does. With a body,
    /// its contact state seeds the sweep and the sweep's findings are
    /// written back to it (position is left to the caller).
    pub fn resolve_with_transition(
        &self,
        request: &ResolveRequest,
        body: Option<&mut PhysicsBody>,
    ) -> ResolveResult {
        let mut transition = Transition::new();
        let oi = &mut transition.object_info;
        oi.step_up_height = request.step_up_height;
        oi.step_down_height = request.step_down_height;
        oi.step_down = true;
        oi.self_entity_id = request.moving_entity_id;
        oi.mover_physics_state = body.as_ref().map_or(PhysicsState::NONE, |b| b.state);
        // Every move of a body takes its ethereal bit from the body's own
        // state, as a placement does: an ethereal mover passes through
        // objects that are not static.
        oi.ethereal = oi.mover_physics_state.contains(PhysicsState::ETHEREAL);
        oi.target_id = request.designated_target_id;
        oi.state.insert(request.mover_flags.0);
        if oi.mover_physics_state.contains(PhysicsState::MISSILE) {
            oi.state.insert(ObjectInfoState::PATH_CLIPPED);
        }
        oi.mover_has_gravity = body.as_ref().is_some_and(|b| b.has_gravity());

        match body.as_deref() {
            Some(b) if b.in_contact() && b.contact_plane_valid => {
                let away_rate = b.velocity.dot(b.contact_plane.normal);
                if away_rate <= EPSILON {
                    transition
                        .object_info
                        .state
                        .insert(ObjectInfoState::CONTACT);
                    if b.on_walkable() {
                        transition
                            .object_info
                            .state
                            .insert(ObjectInfoState::ON_WALKABLE);
                    }
                    transition.collision_info.init_contact_plane(
                        b.contact_plane,
                        b.contact_plane_cell_id,
                        b.contact_plane_is_water,
                    );
                } else {
                    let ci = &mut transition.collision_info;
                    ci.last_known_contact_plane_valid = true;
                    ci.last_known_contact_plane = b.contact_plane;
                    ci.last_known_contact_plane_cell_id = b.contact_plane_cell_id;
                    ci.last_known_contact_plane_is_water = b.contact_plane_is_water;
                }
            }
            None if request.is_on_ground => {
                transition
                    .object_info
                    .state
                    .insert(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
            }
            _ => {}
        }

        if let Some(b) = body.as_deref()
            && b.transient_state.contains(TransientState::SLIDING)
            && b.sliding_normal.length_squared() > EPSILON_SQ
        {
            transition
                .collision_info
                .set_sliding_normal(b.sliding_normal);
        }

        if request.sphere_list.is_empty() {
            transition.sphere_path.init_path(
                request.current_pos,
                request.target_pos,
                request.cell_id,
                request.sphere_radius,
                request.sphere_height,
                request.local_sphere_origin,
                request.begin_orientation,
                request.end_orientation,
            );
        } else {
            transition.sphere_path.init_path_spheres(
                request.current_pos,
                request.target_pos,
                request.cell_id,
                &request.sphere_list,
                request.sphere_scale,
                request.begin_orientation,
                request.end_orientation,
            );
        }

        transition.sphere_path.carried_block_origin = body.as_deref().and_then(|b| {
            let body_cell = b.cell_position().obj_cell_id;
            (is_outdoor_low(request.cell_id)
                && is_outdoor_low(body_cell)
                && request.cell_id >> 16 == body_cell >> 16)
                .then(|| b.position() - b.cell_position().frame.origin)
        });

        if request.is_on_ground
            && let Some(b) = body.as_deref()
            && b.walkable_polygon_valid
            && let Some(vertices) = b.walkable_vertices.as_ref().filter(|v| v.len() >= 3)
        {
            transition
                .sphere_path
                .set_walkable(b.walkable_plane, vertices, b.walkable_up);
        }

        if let Some(b) = body.as_deref() {
            transition.collision_info.frames_stationary_fall =
                if b.transient_state.contains(TransientState::STATIONARY_STUCK) {
                    3
                } else if b.transient_state.contains(TransientState::STATIONARY_STOP) {
                    2
                } else if b.transient_state.contains(TransientState::STATIONARY_FALL) {
                    1
                } else {
                    0
                };
        }

        let ok = transition.find_transitional_position(self);

        let sp = &transition.sphere_path;
        let ci = &transition.collision_info;

        if let Some(b) = body {
            if ok {
                if ci.contact_plane_valid {
                    b.contact_plane_valid = true;
                    b.contact_plane = ci.contact_plane;
                    b.contact_plane_cell_id = ci.contact_plane_cell_id;
                    b.contact_plane_is_water = ci.contact_plane_is_water;
                    b.ground_normal = ci.contact_plane.normal;
                } else if ci.last_known_contact_plane_valid {
                    b.contact_plane_valid = true;
                    b.contact_plane = ci.last_known_contact_plane;
                    b.contact_plane_cell_id = ci.last_known_contact_plane_cell_id;
                    b.contact_plane_is_water = ci.last_known_contact_plane_is_water;
                    b.ground_normal = ci.last_known_contact_plane.normal;
                } else {
                    b.contact_plane_valid = false;
                }

                b.transient_state
                    .set(TransientState::WATER_CONTACT, b.contact_plane_is_water);

                b.frames_stationary_fall = ci.frames_stationary_fall;
                b.transient_state.remove(
                    TransientState::STATIONARY_FALL
                        | TransientState::STATIONARY_STOP
                        | TransientState::STATIONARY_STUCK,
                );
                match ci.frames_stationary_fall {
                    1 => b.transient_state.insert(TransientState::STATIONARY_FALL),
                    2 => b.transient_state.insert(TransientState::STATIONARY_STOP),
                    3 => b.transient_state.insert(TransientState::STATIONARY_STUCK),
                    _ => {}
                }

                if sp.has_last_walkable_polygon()
                    && let Some(v) = &sp.last_walkable_vertices
                {
                    b.walkable_polygon_valid = true;
                    b.walkable_plane = sp.last_walkable_plane;
                    b.set_walkable_vertices_exact(v);
                    b.walkable_up = sp.last_walkable_up;
                } else if !request.is_on_ground
                    && !ci.contact_plane_valid
                    && !ci.last_known_contact_plane_valid
                {
                    b.walkable_polygon_valid = false;
                    b.walkable_vertices = None;
                }

                if ci.sliding_normal_valid && ci.sliding_normal.length_squared() > EPSILON_SQ {
                    b.sliding_normal = ci.sliding_normal;
                    b.transient_state.insert(TransientState::SLIDING);
                } else {
                    b.sliding_normal = Vec3::ZERO;
                    b.transient_state.remove(TransientState::SLIDING);
                }
            }

            if transition.object_info.velocity_killed {
                b.velocity = Vec3::ZERO;
            }
        }

        let collision_normal_valid = ci.collision_normal_valid;
        let collision_normal = ci.collision_normal;

        let mut result = if ok {
            let in_contact = ci.contact_plane_valid;
            let on_walkable = is_walkable_contact(in_contact, ci.contact_plane.normal);
            let on_ground = in_contact
                || transition
                    .object_info
                    .state
                    .contains(ObjectInfoState::ON_WALKABLE);
            ResolveResult {
                position: sp.check_pos,
                cell_id: sp.cur_cell_id,
                is_on_ground: on_ground,
                collision_normal_valid,
                collision_normal,
                orientation: sp.cur_orientation,
                in_contact,
                on_walkable,
                ..ResolveResult::default()
            }
        } else {
            let partial_on_ground = ci.contact_plane_valid
                || transition
                    .object_info
                    .state
                    .contains(ObjectInfoState::ON_WALKABLE)
                || request.is_on_ground;
            let partial_cell_id = if sp.check_cell_id != 0 {
                sp.check_cell_id
            } else {
                request.cell_id
            };
            ResolveResult {
                position: sp.check_pos,
                cell_id: if sp.cur_cell_id != 0 {
                    sp.cur_cell_id
                } else {
                    partial_cell_id
                },
                is_on_ground: partial_on_ground,
                collision_normal_valid,
                collision_normal,
                ok: false,
                orientation: sp.cur_orientation,
                ..ResolveResult::default()
            }
        };
        result.last_collided_object_id = ci.last_collided_object_guid.unwrap_or(0);
        result.collided_with_environment = ci.collided_with_environment;
        result
    }
}

fn build_terrain_walkable_sample(
    landblock_id: u32,
    landblock: &LandblockPhysics,
    local_x: f32,
    local_y: f32,
) -> TerrainWalkableSample {
    let sample = landblock.terrain.sample_surface_polygon(local_x, local_y);
    let offset = |v: Vec3| {
        Vec3::new(
            v.x + landblock.world_offset_x,
            v.y + landblock.world_offset_y,
            v.z,
        )
    };
    let vertices = [
        offset(sample.vertices[0]),
        offset(sample.vertices[1]),
        offset(sample.vertices[2]),
    ];
    let normal = sample.normal;
    let d = -normal.dot(vertices[0]);
    let water_depth = landblock.terrain.sample_water_depth(local_x, local_y);
    let low = landblock.terrain.compute_outdoor_cell_id(local_x, local_y);
    TerrainWalkableSample {
        plane: Plane::new(normal, d),
        vertices,
        water_depth,
        is_water: water_depth >= 0.45,
        cell_id: (landblock_id & 0xFFFF_0000) | low,
        block_entirely_water: landblock.terrain.is_entirely_water(),
    }
}

fn initialize_set_position_transition(
    transition: &mut Transition,
    request: &PhysicsSetPositionRequest,
) {
    let oi = &mut transition.object_info;
    oi.step_up_height = request.step_up_height;
    oi.step_down_height = request.step_down_height;
    oi.step_down = !request.mover_physics_state.contains(PhysicsState::MISSILE);
    oi.mover_physics_state = request.mover_physics_state;
    oi.self_entity_id = request.moving_entity_id;
    oi.state = request.mover_flags;
    oi.ethereal = request.mover_physics_state.contains(PhysicsState::ETHEREAL);
    transition.sphere_path.placement_allows_sliding =
        request.flags.contains(PhysicsSetPositionFlags::SLIDE);
}

fn error_result(
    request: &PhysicsSetPositionRequest,
    error: PhysicsSetPositionError,
) -> PhysicsSetPositionResult {
    PhysicsSetPositionResult {
        error,
        residence: PhysicsResidenceDisposition::Unchanged,
        position: request.position,
        orientation: request.orientation,
        cell_id: request.cell_id,
        cell_local_position: request.cell_local_position,
        ..PhysicsSetPositionResult::default()
    }
}

/// A placement that may not slide is accepted only when it moved at most
/// 5 cm on x and y (signed, as retail) and stayed in its cell.
#[must_use]
pub fn accept_no_slide_placement(
    resolved_position: Vec3,
    requested_position: Vec3,
    resolved_cell_id: u32,
    adjusted_cell_id: u32,
) -> bool {
    let displacement = resolved_position - requested_position;
    displacement.x <= 0.05 && displacement.y <= 0.05 && resolved_cell_id == adjusted_cell_id
}

/// Touching a surface whose normal is walkable.
#[must_use]
pub fn is_walkable_contact(in_contact: bool, contact_normal: Vec3) -> bool {
    in_contact && contact_normal.z >= FLOOR_Z
}
