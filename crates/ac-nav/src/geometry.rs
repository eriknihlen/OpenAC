//! The fixed collision of a square region of the world, copied out of the
//! physics world so a grid can be built from it on another thread.

use std::collections::HashSet;

use ac_core::physics::PhysicsState;
use ac_physics::movement::{DEFAULT_PLAYER_HEIGHT, DEFAULT_PLAYER_RADIUS};
use ac_physics::{
    CellSurface, CollisionWorld, GfxObjPhysics, PhysicsEngine, ShadowCollisionType, ShadowEntry,
    TerrainSurface, point_in_triangle_xy,
};
use glam::{Quat, Vec2, Vec3};

use crate::grid;
use crate::route::NavAvoidance;

/// The side of a landblock, metres.
pub const LANDBLOCK_SIZE: f32 = 192.0;

/// How much of an object outside a region still counts as standing in it,
/// metres.
const REGION_SLACK: f32 = 4.0;

/// The shape a navigation grid is built for, metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavBody {
    /// Half the body's width.
    pub radius: f32,
    /// Its full height.
    pub height: f32,
    /// How high it steps up without jumping.
    pub step_up_height: f32,
    /// How far it steps down without falling.
    pub step_down_height: f32,
}

impl NavBody {
    /// A body of the given size and steps.
    #[must_use]
    pub const fn new(radius: f32, height: f32, step_up_height: f32, step_down_height: f32) -> Self {
        Self {
            radius,
            height,
            step_up_height,
            step_down_height,
        }
    }

    /// The player's body as the movement controller sweeps it.
    #[must_use]
    pub const fn player(step_up_height: f32, step_down_height: f32) -> Self {
        Self::new(
            DEFAULT_PLAYER_RADIUS,
            DEFAULT_PLAYER_HEIGHT,
            step_up_height,
            step_down_height,
        )
    }
}

/// One triangle of collision, world frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavTriangle {
    /// First corner.
    pub a: Vec3,
    /// Second corner.
    pub b: Vec3,
    /// Third corner.
    pub c: Vec3,
}

impl NavTriangle {
    /// A triangle from its corners.
    #[must_use]
    pub const fn new(a: Vec3, b: Vec3, c: Vec3) -> Self {
        Self { a, b, c }
    }
}

/// A cylinder or sphere a body meets, as the upright extent it fills. A
/// body stands anywhere on a cylinder's flat top; a dome is a sphere, whose
/// top curves away, so a body stands only over the part of it no steeper
/// than a floor.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavCylinder {
    /// The middle of its bottom.
    pub base: Vec3,
    /// Its radius, metres.
    pub radius: f32,
    /// Its height, metres.
    pub height: f32,
    /// Whether it is a sphere rather than a cylinder.
    pub dome: bool,
}

impl NavCylinder {
    /// An upright cylinder.
    #[must_use]
    pub const fn new(base: Vec3, radius: f32, height: f32) -> Self {
        Self {
            base,
            radius,
            height,
            dome: false,
        }
    }

    /// A sphere, given by the bottom of its upright extent.
    #[must_use]
    pub const fn dome(base: Vec3, radius: f32, height: f32) -> Self {
        Self {
            base,
            radius,
            height,
            dome: true,
        }
    }
}

/// The collision of one object, as the triangles and upright extents a grid
/// builds its floors and walls from, for finding the part of a grid that
/// stands on it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavSurfaces {
    /// Its polygons as triangles.
    pub triangles: Vec<NavTriangle>,
    /// Its cylinders and spheres.
    pub cylinders: Vec<NavCylinder>,
}

/// One landblock's terrain, and the world position of its south-west corner.
#[derive(Debug, Clone, PartialEq)]
pub struct NavTerrain {
    /// The ground.
    pub surface: TerrainSurface,
    /// World x of the corner.
    pub origin_x: f32,
    /// World y of the corner.
    pub origin_y: f32,
}

impl NavTerrain {
    /// A landblock's ground with its corner.
    #[must_use]
    pub const fn new(surface: TerrainSurface, origin_x: f32, origin_y: f32) -> Self {
        Self {
            surface,
            origin_x,
            origin_y,
        }
    }
}

/// The fixed collision geometry of a square region of the world, copied out
/// of the physics world so a grid can be built from it on another thread:
/// the terrain, interior cell polygons, building shells and placed objects
/// of every resident landblock the region overlaps, and the objects the
/// server placed there that a body stands on, such as the rocks a jump
/// puzzle is built from. Doors, creatures and players are left out; a walk
/// steers around those where they stand now.
#[derive(Debug, Clone, PartialEq)]
pub struct NavGeometry {
    /// World x of the region's south-west corner.
    pub origin_x: f32,
    /// World y of the region's south-west corner.
    pub origin_y: f32,
    /// The length of the region's sides, metres.
    pub size: f32,
    /// The terrain of each landblock.
    pub terrains: Vec<NavTerrain>,
    /// Interior cell polygons. Terrain is left out of every column where it
    /// does not stand above them.
    pub cell_triangles: Vec<NavTriangle>,
    /// Placed objects, and the objects the server placed that a body stands
    /// on.
    pub object_triangles: Vec<NavTriangle>,
    /// The outside of buildings. A building's shell bounds the ground around
    /// it and carries its roof, but not the rooms inside it, which its cells
    /// hold.
    pub shell_triangles: Vec<NavTriangle>,
    /// Cylinders and spheres.
    pub cylinders: Vec<NavCylinder>,
    /// The resident landblocks the region overlaps.
    pub landblock_ids: Vec<u32>,
    /// What the objects a body meets here came to: their ids and where they
    /// stood, so a grid is known to be out of date once one arrives, leaves
    /// or moves. Every object the caller's rule names counts, whether a
    /// landblock owns it or the server placed it, so this and
    /// [`fingerprint_objects`] always weigh the same objects.
    pub object_fingerprint: u64,
    /// The objects the server placed whose collision this geometry holds. A
    /// route has no need to keep out of them: they are already floors and
    /// walls in the grid.
    pub object_ids: HashSet<u32>,
}

/// Why a region cannot be built from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NavGeometryError {
    /// The size is not a positive, finite number of metres.
    BadSize,
}

impl NavGeometry {
    /// A region from its parts; no landblocks, no objects.
    ///
    /// # Errors
    ///
    /// When `size` is not positive and finite.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        origin_x: f32,
        origin_y: f32,
        size: f32,
        terrains: Vec<NavTerrain>,
        cell_triangles: Vec<NavTriangle>,
        object_triangles: Vec<NavTriangle>,
        cylinders: Vec<NavCylinder>,
        shell_triangles: Vec<NavTriangle>,
    ) -> Result<Self, NavGeometryError> {
        if !(size > 0.0) || !size.is_finite() {
            return Err(NavGeometryError::BadSize);
        }
        Ok(Self {
            origin_x,
            origin_y,
            size,
            terrains,
            cell_triangles,
            object_triangles,
            shell_triangles,
            cylinders,
            landblock_ids: Vec::new(),
            object_fingerprint: 0,
            object_ids: HashSet::new(),
        })
    }
}

/// Copies one resident landblock's fixed collision geometry, or `None` when
/// the landblock has no collision in the physics world.
#[must_use]
pub fn capture_landblock(engine: &PhysicsEngine, landblock_id: u32) -> Option<NavGeometry> {
    let landblock = engine.landblock(landblock_id)?;
    capture(
        engine,
        landblock.world_offset_x,
        landblock.world_offset_y,
        LANDBLOCK_SIZE,
        None,
    )
}

/// Copies the fixed collision geometry of a square region, or `None` when
/// no resident landblock overlaps it. `stands_on` names the objects the
/// server placed whose collision a body meets as floor and wall. Call it
/// on the thread that owns the physics world.
#[must_use]
pub fn capture(
    engine: &PhysicsEngine,
    origin_x: f32,
    origin_y: f32,
    size: f32,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> Option<NavGeometry> {
    let landblocks = overlapping_landblocks(engine, origin_x, origin_y, size);
    if landblocks.is_empty() {
        return None;
    }
    Some(capture_from(
        engine, origin_x, origin_y, size, landblocks, true, stands_on,
    ))
}

/// Copies the fixed collision geometry of a sealed dungeon over a square
/// region: the cells and placed objects of the one landblock that holds the
/// dungeon, without terrain, since nothing in a sealed dungeon stands on
/// it. `None` when that landblock is not resident.
#[must_use]
pub fn capture_dungeon(
    engine: &PhysicsEngine,
    landblock_id: u32,
    origin_x: f32,
    origin_y: f32,
    size: f32,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> Option<NavGeometry> {
    let canonical = (landblock_id & 0xFFFF_0000) | 0xFFFF;
    engine.landblock(canonical)?;
    Some(capture_from(
        engine,
        origin_x,
        origin_y,
        size,
        vec![canonical],
        false,
        stands_on,
    ))
}

fn capture_from(
    engine: &PhysicsEngine,
    origin_x: f32,
    origin_y: f32,
    size: f32,
    landblocks: Vec<u32>,
    include_terrain: bool,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> NavGeometry {
    let mut terrains = Vec::new();
    let mut cell_triangles = Vec::new();
    let mut object_triangles = Vec::new();
    let mut shell_triangles = Vec::new();
    let mut owners = HashSet::new();
    let world = engine.world();
    for &landblock_id in &landblocks {
        let Some(landblock) = engine.landblock(landblock_id) else {
            continue;
        };
        if include_terrain {
            terrains.push(NavTerrain::new(
                landblock.terrain.clone(),
                landblock.world_offset_x,
                landblock.world_offset_y,
            ));
        }
        for cell in &landblock.cells {
            for [a, b, c] in cell.triangles() {
                cell_triangles.push(NavTriangle::new(*a, *b, *c));
            }
        }
        add_building_shells(world, landblock_id, &mut shell_triangles);
        owners.extend(
            engine
                .shadows
                .capture_static_owners_for_landblock(landblock_id),
        );
    }

    let mut cylinders = Vec::new();
    let mut fingerprint = 0u64;
    let mut counted = 0u64;
    let mut stood_on_ids = HashSet::new();
    for entry in engine.shadows.capture_entries() {
        if !meets(&entry) {
            continue;
        }
        let named = stands_on.is_some_and(|rule| rule(entry.entity_id))
            && within(entry.position, origin_x, origin_y, size);
        if named {
            fingerprint ^= fingerprint_of(&entry);
            counted += 1;
        }
        if !owners.contains(&entry.entity_id) {
            if !named {
                continue;
            }
            stood_on_ids.insert(entry.entity_id);
        }
        add_collision(world, &entry, &mut cylinders, &mut object_triangles);
    }

    NavGeometry {
        origin_x,
        origin_y,
        size,
        terrains,
        cell_triangles,
        object_triangles,
        shell_triangles,
        cylinders,
        landblock_ids: landblocks,
        object_fingerprint: fold(fingerprint, counted),
        object_ids: stood_on_ids,
    }
}

/// The highest floor under a point, from `lowest` to `highest`, among the
/// surfaces a grid captured over the landblock `landblock_id` holds: a face
/// of one of its cells, or of an object the landblock holds or `stands_on`
/// names, that faces up and is no steeper than a floor, or the top of such
/// an object's cylinder or dome where a body stands on it. The objects, the
/// floor slope and a cylinder's reach are the grid's own. Only a face that
/// faces up counts, so the underside of an arch or of something hanging is
/// never one. Whether a body has room to stand there is not checked.
/// `None` where there is none. Call it on the thread that owns the physics
/// world.
#[must_use]
pub fn highest_floor_under(
    engine: &PhysicsEngine,
    landblock_id: u32,
    x: f32,
    y: f32,
    lowest: f32,
    highest: f32,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> Option<f32> {
    let canonical = (landblock_id & 0xFFFF_0000) | 0xFFFF;
    let landblock = engine.landblock(canonical)?;

    let mut best: Option<f32> = None;
    let mut consider = |z: f32| {
        if z >= lowest && z <= highest && best.is_none_or(|b| z > b) {
            best = Some(z);
        }
    };
    let face_height = |a: Vec3, b: Vec3, c: Vec3| -> Option<f32> {
        let normal = (b - a).cross(c - a);
        let length = normal.length();
        if length > 1e-6 && normal.z > 0.0 && grid::is_floor(normal, length) {
            point_in_triangle_xy(x, y, a, b, c)
        } else {
            None
        }
    };

    for cell in &landblock.cells {
        for [a, b, c] in cell.triangles() {
            if let Some(z) = face_height(*a, *b, *c) {
                consider(z);
            }
        }
    }

    let point = Vec2::new(x, y);
    let slack = grid::DEFAULT_CELL_SIZE * 0.5;
    let world = engine.world();
    let mut owners: Option<HashSet<u32>> = None;
    let mut triangles = Vec::new();
    let mut cylinders = Vec::new();
    for entry in engine.shadows.capture_entries() {
        if !meets(&entry) {
            continue;
        }
        let footprint = footprint_of(&entry, world);
        if footprint.centre.truncate().distance(point) > footprint.radius + slack {
            continue;
        }
        let owners = owners.get_or_insert_with(|| {
            engine
                .shadows
                .capture_static_owners_for_landblock(canonical)
                .into_iter()
                .collect()
        });
        if !owners.contains(&entry.entity_id)
            && !stands_on.is_some_and(|rule| rule(entry.entity_id))
        {
            continue;
        }
        add_collision(world, &entry, &mut cylinders, &mut triangles);
    }

    for triangle in &triangles {
        if let Some(z) = face_height(triangle.a, triangle.b, triangle.c) {
            consider(z);
        }
    }
    for cylinder in &cylinders {
        if !grid::has_extent(cylinder) {
            continue;
        }
        let across = cylinder.base.truncate().distance_squared(point);
        let reach = grid::reach_of(cylinder, grid::DEFAULT_CELL_SIZE);
        if across > reach * reach {
            continue;
        }
        let (over, stands) = grid::top_over(cylinder, across);
        if stands {
            consider(cylinder.base.z + over);
        }
    }
    best
}

/// The collision of the object the physics world knows as `entity_id`,
/// every part of it that a body meets, or `None` when it has none.
#[must_use]
pub fn surfaces_of(engine: &PhysicsEngine, entity_id: u32) -> Option<NavSurfaces> {
    let mut surfaces = NavSurfaces::default();
    for entry in engine.shadows.capture_entries_for(entity_id) {
        if meets(&entry) {
            add_collision(
                engine.world(),
                &entry,
                &mut surfaces.cylinders,
                &mut surfaces.triangles,
            );
        }
    }
    if surfaces.triangles.is_empty() && surfaces.cylinders.is_empty() {
        None
    } else {
        Some(surfaces)
    }
}

fn add_collision(
    world: Option<&CollisionWorld>,
    entry: &ShadowEntry,
    cylinders: &mut Vec<NavCylinder>,
    triangles: &mut Vec<NavTriangle>,
) {
    match entry.collision_type {
        ShadowCollisionType::Cylinder => cylinders.push(NavCylinder::new(
            entry.position,
            entry.radius,
            if entry.cyl_height > 0.0 {
                entry.cyl_height
            } else {
                entry.radius * 2.0
            },
        )),
        ShadowCollisionType::Sphere => cylinders.push(NavCylinder::dome(
            entry.position - Vec3::new(0.0, 0.0, entry.radius),
            entry.radius,
            entry.radius * 2.0,
        )),
        ShadowCollisionType::Bsp => {
            let scale = entry.scale;
            let rotation = entry.rotation;
            let position = entry.position;
            add_object_triangles(
                world.and_then(|w| w.gfx_obj(entry.gfx_obj_id)),
                &|v| rotation * (v * scale) + position,
                triangles,
            );
        }
    }
}

/// Whether a body meets an object where it stands: not one that is
/// ethereal, which a body walks through, and not a missile in flight. An
/// arrow, a thrown blade or a spell's bolt stands nowhere: it is on its way
/// and gone in a moment, and it moves every frame, which would leave a grid
/// built with one in it out of date the moment it was built.
fn meets(entry: &ShadowEntry) -> bool {
    entry.state & PhysicsState::ETHEREAL.bits() == 0
        && entry.state & PhysicsState::MISSILE.bits() == 0
}

/// Whether a point stands in a region, with room to spare for an object's
/// own width.
fn within(point: Vec3, origin_x: f32, origin_y: f32, size: f32) -> bool {
    point.x >= origin_x - REGION_SLACK
        && point.x <= origin_x + size + REGION_SLACK
        && point.y >= origin_y - REGION_SLACK
        && point.y <= origin_y + size + REGION_SLACK
}

/// What the objects the server placed in a region come to now, to compare
/// against what a grid was built from. Call it on the thread that owns the
/// physics world.
#[must_use]
pub fn fingerprint_objects(
    engine: &PhysicsEngine,
    origin_x: f32,
    origin_y: f32,
    size: f32,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> u64 {
    let Some(stands_on) = stands_on else {
        return 0;
    };
    let mut fingerprint = 0u64;
    let mut counted = 0u64;
    for entry in engine.shadows.capture_entries() {
        if !meets(&entry)
            || !within(entry.position, origin_x, origin_y, size)
            || !stands_on(entry.entity_id)
        {
            continue;
        }
        fingerprint ^= fingerprint_of(&entry);
        counted += 1;
    }
    fold(fingerprint, counted)
}

/// Folds how many objects were counted into their fingerprint.
fn fold(fingerprint: u64, counted: u64) -> u64 {
    if counted == 0 {
        0
    } else {
        fingerprint ^ counted.wrapping_mul(0x9E37_79B9_7F4A_7C15)
    }
}

/// What one server object comes to: its id and where it stands, to a tenth
/// of a metre, so the objects a region holds can be compared without
/// keeping them.
fn fingerprint_of(entry: &ShadowEntry) -> u64 {
    let tenth = |v: f32| (v * 10.0).round_ties_even() as i64 as u64;
    let mut value = u64::from(entry.entity_id);
    value = value.wrapping_mul(1_000_003) ^ tenth(entry.position.x);
    value = value.wrapping_mul(1_000_003) ^ tenth(entry.position.y);
    value = value.wrapping_mul(1_000_003) ^ tenth(entry.position.z);
    value
}

/// The horizontal footprint of one part of an object's collision, as the
/// point at its middle and the radius it fills: a cylinder or sphere as it
/// was registered, and a part with a collision model of its own as that
/// model's bounding sphere, placed, turned and scaled with the part, since a
/// part's own position can lie well off the middle of its model.
#[must_use]
pub fn footprint_of(entry: &ShadowEntry, world: Option<&CollisionWorld>) -> NavAvoidance {
    let model = match entry.collision_type {
        ShadowCollisionType::Bsp => world.and_then(|w| w.gfx_obj(entry.gfx_obj_id)),
        _ => None,
    };
    let Some(model) = model else {
        return NavAvoidance::new(entry.position, entry.radius);
    };
    let sphere = model.bounding_sphere;
    let scale = if entry.scale > 0.0 { entry.scale } else { 1.0 };
    let rotation = if entry.rotation == Quat::from_xyzw(0.0, 0.0, 0.0, 0.0) {
        Quat::IDENTITY
    } else {
        entry.rotation
    };
    NavAvoidance::new(
        entry.position + rotation * (sphere.center * scale),
        sphere.radius * scale,
    )
}

/// The resident landblocks a square region overlaps, by their terrain's
/// square or by the extent of their interior cells, which in a dungeon can
/// lie far outside that square.
#[must_use]
pub fn overlapping_landblocks(
    engine: &PhysicsEngine,
    origin_x: f32,
    origin_y: f32,
    size: f32,
) -> Vec<u32> {
    let mut overlapping = Vec::new();
    for landblock_id in engine.landblock_ids() {
        let Some(landblock) = engine.landblock(landblock_id) else {
            continue;
        };
        let (x, y) = (landblock.world_offset_x, landblock.world_offset_y);
        let terrain = x < origin_x + size
            && x + LANDBLOCK_SIZE > origin_x
            && y < origin_y + size
            && y + LANDBLOCK_SIZE > origin_y;
        if terrain || CellExtent::of(&landblock.cells).overlaps(origin_x, origin_y, size) {
            overlapping.push(landblock_id);
        }
    }
    overlapping
}

/// The horizontal extent of a resident landblock's interior cells, as
/// (minimum, maximum), or `None` when the physics world holds none for it.
#[must_use]
pub fn measure_cells(engine: &PhysicsEngine, landblock_id: u32) -> Option<(Vec2, Vec2)> {
    let landblock = engine.landblock(landblock_id)?;
    let extent = CellExtent::of(&landblock.cells);
    if extent.is_empty() {
        None
    } else {
        Some((extent.minimum, extent.maximum))
    }
}

struct CellExtent {
    minimum: Vec2,
    maximum: Vec2,
}

impl CellExtent {
    fn of(cells: &[CellSurface]) -> Self {
        let mut low = Vec2::splat(f32::INFINITY);
        let mut high = Vec2::splat(f32::NEG_INFINITY);
        for cell in cells {
            for [a, b, c] in cell.triangles() {
                low = low.min(a.truncate().min(b.truncate().min(c.truncate())));
                high = high.max(a.truncate().max(b.truncate().max(c.truncate())));
            }
        }
        Self {
            minimum: low,
            maximum: high,
        }
    }

    fn is_empty(&self) -> bool {
        self.minimum.x > self.maximum.x
    }

    fn overlaps(&self, origin_x: f32, origin_y: f32, size: f32) -> bool {
        !self.is_empty()
            && self.minimum.x < origin_x + size
            && self.maximum.x > origin_x
            && self.minimum.y < origin_y + size
            && self.maximum.y > origin_y
    }
}

fn add_building_shells(
    world: Option<&CollisionWorld>,
    landblock_id: u32,
    triangles: &mut Vec<NavTriangle>,
) {
    let Some(world) = world else {
        return;
    };
    let prefix = landblock_id & 0xFFFF_0000;
    let mut ids: Vec<u32> = world
        .building_ids()
        .filter(|id| id & 0xFFFF_0000 == prefix)
        .collect();
    ids.sort_unstable();
    for landcell_id in ids {
        let Some(building) = world.building(landcell_id) else {
            continue;
        };
        if building.model_id == 0 {
            continue;
        }
        let placement = building.placement;
        add_object_triangles(
            world.gfx_obj(building.model_id),
            &|v| placement.to_world(v),
            triangles,
        );
    }
}

/// A model's collision polygons, placed, as fan triangles in polygon-id
/// order.
fn add_object_triangles(
    physics: Option<&GfxObjPhysics>,
    place: &dyn Fn(Vec3) -> Vec3,
    triangles: &mut Vec<NavTriangle>,
) {
    let Some(physics) = physics else {
        return;
    };
    let polygons = physics.bsp.polygons();
    let mut ids: Vec<u16> = polygons.keys().copied().collect();
    ids.sort_unstable();
    for id in ids {
        let polygon = &polygons[&id];
        if polygon.vertices.len() < 3 {
            continue;
        }
        let first = place(polygon.vertices[0]);
        let mut previous = place(polygon.vertices[1]);
        for vertex in &polygon.vertices[2..] {
            let current = place(*vertex);
            triangles.push(NavTriangle::new(first, previous, current));
            previous = current;
        }
    }
}
