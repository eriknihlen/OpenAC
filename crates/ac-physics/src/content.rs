//! A landblock's collision, read from the dats and made resident in an
//! engine: the terrain, the interior cells (rooms of buildings and
//! dungeons) with their portals, the building shells, and the static
//! objects placed on the landblock and inside its cells.
//!
//! What a host with no renderer loads (OpenAC's headless collision
//! neighbourhood): the same builder serves a window-less session and one
//! with a window. Procedural scenery (trees, rocks and bushes generated
//! from the terrain) collides too, placed as the renderer places it.
//!
//! Positions are in the engine's frame: the landblock's corner sits at
//! `origin` (metres), so the rest of the world is measured from whichever
//! landblock the caller made the centre.

use std::sync::Arc;

use ac_core::world::{StaticBuilding, StaticObject, StaticPlacement, place_landblock_statics};
use ac_dat::objects::{
    Animation, EnvCell, Environment, GfxObj, GfxObjDegradeInfo, GfxObjFlags, LandBlock,
    LandBlockInfo, MotionTable, Region, Scene, Setup,
};
use ac_dat::{CachedDats, DatCollection, DatError, DatObject};
use ac_motion::Frame;
use glam::{Quat, Vec3};

use crate::PhysicsState;
use crate::cell_surface::CellSurface;
use crate::collision_flags::EntityCollisionFlags;
use crate::dat::{quat, vec3};
use crate::engine::PhysicsEngine;
use crate::portal::PortalPlane;
use crate::scenery::{self, ObjectShape, SceneryDats};
use crate::shadow::ShadowShape;
use crate::shadow_builder::{self, PartRef};
use crate::shadow_registry::{RegistrationSite, ShadowObjectRegistry};
use crate::terrain::TerrainSurface;
use crate::world::{BldPortalInfo, BuildingPhysics, CollisionWorld, GfxObjPhysics, Placement};

/// Placement id of a setup's resting pose.
const PLACEMENT_RESTING: u32 = 0x65;
/// Placement id of a setup's default pose.
const PLACEMENT_DEFAULT: u32 = 0;
/// Interior static ids: `0x4XXYYIII`.
const INTERIOR_ID_BASE: u32 = 0x4000_0000;
/// At most this many interior statics per landblock.
const INTERIOR_ID_MAX_COUNTER: u32 = 0xFFF;
/// Procedural scenery ids: `0x8XXYYIII`.
const SCENERY_ID_BASE: u32 = 0x8000_0000;
/// At most this many scenery objects per landblock.
const SCENERY_ID_MAX_COUNTER: u32 = 0xFFF;
/// The region, whose scene tables place scenery.
const REGION_ID: u32 = 0x1300_0000;

/// Where dat objects come from: the process's cached reader, or a plain
/// collection (tests and tools).
pub trait DatSource {
    /// The object with file id `id`, `None` when the dat has none.
    ///
    /// # Errors
    /// A read or decode failure.
    fn object<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>, DatError>;
}

impl DatSource for CachedDats {
    fn object<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>, DatError> {
        self.get::<T>(id)
    }
}

impl DatSource for DatCollection {
    fn object<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>, DatError> {
        Ok(self.get::<T>(id)?.map(Arc::new))
    }
}

/// Why a landblock's collision could not be made resident.
#[derive(Debug, thiserror::Error)]
pub enum ContentError {
    /// A dat object could not be read.
    #[error(transparent)]
    Dat(#[from] DatError),
    /// A collision tree in the dat is malformed.
    #[error("collision tree of {id:#010x}: {error}")]
    Bsp {
        /// The model or cell.
        id: u32,
        /// What was wrong.
        error: crate::bsp::BspError,
    },
    /// The landblock lists more static objects than ids exist for.
    #[error("{0}")]
    TooManyStatics(String),
    /// The dats have no such landblock.
    #[error("the dats have no landblock {0:#010x}")]
    MissingLandblock(u32),
    /// The engine has no collision world to put cells and models in.
    #[error("the engine has no collision world attached")]
    NoWorld,
    /// A shadow flood failed (a building portal names a missing partner).
    #[error("shadow flood of {owner:#010x}: {error}")]
    Flood {
        /// The object.
        owner: u32,
        /// What was wrong.
        error: crate::cell_transit::CellTransitError,
    },
}

/// One static object a landblock places (a building, a dat object, a
/// procedural tree or rock): the drawn decoration plugins read as scenery.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedStatic {
    /// The client's id for it (no server identity).
    pub id: u32,
    /// Its model: a GfxObj (`0x01`) or Setup (`0x02`) id.
    pub source_id: u32,
    /// The landblock it stands in (`0xXXYYFFFF`).
    pub landblock_id: u32,
    /// Landblock-local position, metres.
    pub position: Vec3,
    /// Orientation.
    pub rotation: Quat,
}

/// How a landblock's static objects were registered.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StaticCollisionPublication {
    /// Objects colliding with their parts' collision trees.
    pub bsp_owners: usize,
    /// Objects colliding with their setup's cylinders or spheres.
    pub setup_owners: usize,
    /// Objects with no collision (registered for drawing only, or not at
    /// all when they have no drawn parts).
    pub no_collision: usize,
    /// Interior cells made resident.
    pub cells: usize,
    /// Buildings made resident.
    pub buildings: usize,
    /// Every static object the landblock placed, in placement order.
    pub placed: Vec<PlacedStatic>,
}

/// The dat reads scenery placement makes, from a collision loader's dats.
struct SceneryReads<'a, D>(&'a D);

impl<D: DatSource> SceneryDats for SceneryReads<'_, D> {
    fn scene(&self, id: u32) -> Option<Arc<Scene>> {
        self.0.object::<Scene>(id).ok().flatten()
    }

    fn shape(&self, object_id: u32) -> Option<ObjectShape> {
        scenery::object_shape(
            &|id| self.0.object::<GfxObj>(id).ok().flatten(),
            &|id| self.0.object::<Setup>(id).ok().flatten(),
            object_id,
        )
    }
}

/// One static object as the collision registers it.
#[derive(Debug, Clone, PartialEq)]
pub struct StaticEntity {
    /// The client's id for it.
    pub id: u32,
    /// Its model: a GfxObj (`0x01`) or Setup (`0x02`) id.
    pub source_id: u32,
    /// Its position in the engine's frame, metres.
    pub position: Vec3,
    /// Its orientation.
    pub rotation: Quat,
    /// Its parts, object-local.
    pub parts: Vec<PartRef>,
    /// Its scale.
    pub scale: f32,
    /// The interior cell it stands in (0 outdoors: derived from the
    /// position).
    pub parent_cell_id: u32,
    /// A building's shell (collides through its building record).
    pub is_building_shell: bool,
}

/// How a static object was registered.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StaticCollisionKind {
    /// A building's shell: not registered here.
    BuildingShell,
    /// With its parts' collision trees.
    Bsp,
    /// With its setup's cylinders or spheres.
    Setup,
    /// Without collision: for drawing only when it has drawn parts, else
    /// not at all.
    NoCollision,
}

/// The landblock's corner in the engine's frame when `center` is the
/// landblock at the frame's origin, metres.
#[must_use]
pub fn landblock_origin(center_landblock: u32, landblock_id: u32) -> Vec3 {
    let dx = ((landblock_id >> 24) & 0xFF) as i32 - ((center_landblock >> 24) & 0xFF) as i32;
    let dy = ((landblock_id >> 16) & 0xFF) as i32 - ((center_landblock >> 16) & 0xFF) as i32;
    Vec3::new(dx as f32 * 192.0, dy as f32 * 192.0, 0.0)
}

/// The parts of a setup in its resting pose (else its default, else its
/// first placement), each with the setup's default scale for it.
#[must_use]
pub fn setup_parts(setup: &Setup) -> Vec<PartRef> {
    let find = |id: u32| {
        setup
            .placement_frames
            .iter()
            .find(|(k, _)| *k == id)
            .map(|(_, f)| f)
    };
    let pose = find(PLACEMENT_RESTING)
        .or_else(|| find(PLACEMENT_DEFAULT))
        .or_else(|| setup.placement_frames.first().map(|(_, f)| f));
    setup
        .parts
        .iter()
        .enumerate()
        .map(|(i, &gfx_obj_id)| {
            let frame = pose.and_then(|p| p.frames.get(i));
            PartRef {
                gfx_obj_id,
                position: frame.map_or(Vec3::ZERO, |f| vec3(f.origin)),
                rotation: frame.map_or(Quat::IDENTITY, |f| quat(f.orientation)),
                scale: setup.default_scale.get(i).map_or(Vec3::ONE, |s| vec3(*s)),
            }
        })
        .collect()
}

/// The parts of a model: a GfxObj is one part at the object's origin, a
/// Setup its parts in the resting pose. Each model is cached in `world`
/// (its collision tree and drawn bounds). Empty for an unknown id.
fn model_parts<D: DatSource>(
    dats: &D,
    world: &mut CollisionWorld,
    model_id: u32,
    keep: impl Fn(&CollisionWorld, u32) -> Result<bool, ContentError>,
) -> Result<Vec<PartRef>, ContentError> {
    let parts = match model_id >> 24 {
        0x01 => vec![PartRef {
            gfx_obj_id: model_id,
            position: Vec3::ZERO,
            rotation: Quat::IDENTITY,
            scale: Vec3::ONE,
        }],
        0x02 => match dats.object::<Setup>(model_id)? {
            Some(setup) => {
                world.cache_setup(&setup);
                setup_parts(&setup)
            }
            None => return Ok(Vec::new()),
        },
        _ => return Ok(Vec::new()),
    };
    let mut kept = Vec::with_capacity(parts.len());
    for part in parts {
        let Some(gfx) = dats.object::<GfxObj>(part.gfx_obj_id)? else {
            continue;
        };
        world
            .cache_gfx_obj(&gfx)
            .map_err(|error| ContentError::Bsp {
                id: part.gfx_obj_id,
                error,
            })?;
        if keep(world, part.gfx_obj_id)? {
            kept.push(part);
        }
    }
    Ok(kept)
}

/// The parts an object placed inside a cell registers, as the landblock's
/// interior statics are read: every part except an editor-only marker
/// without a collision tree (a marker draws nothing, but its collision
/// still stands, as the invisible ledges and walls of some dungeons do).
/// A part without a collision tree is kept: the object then collides with
/// its setup's cylinders or spheres. `None` when the object is dropped: no
/// part is kept and it has no light and no default script of its own; a
/// GfxObj placement that is a marker without collision is dropped too.
fn interior_parts<D: DatSource>(
    dats: &D,
    world: &mut CollisionWorld,
    model_id: u32,
) -> Result<Option<Vec<PartRef>>, ContentError> {
    let (lights, has_default_script) = match model_id >> 24 {
        0x02 => match dats.object::<Setup>(model_id)? {
            Some(setup) => (
                !setup.lights.is_empty(),
                setup.default_script != 0 || setup.default_script_table != 0,
            ),
            None => (false, false),
        },
        _ => (false, false),
    };
    let parts = model_parts(dats, world, model_id, |w, gfx| {
        keeps_interior_part(dats, w, gfx)
    })?;
    Ok((!parts.is_empty() || lights || has_default_script).then_some(parts))
}

/// Whether an interior part is kept: never an editor-only marker, unless
/// it has a collision tree.
fn keeps_interior_part<D: DatSource>(
    dats: &D,
    world: &CollisionWorld,
    gfx_obj_id: u32,
) -> Result<bool, ContentError> {
    if world
        .gfx_obj(gfx_obj_id)
        .is_some_and(GfxObjPhysics::has_physics)
    {
        return Ok(true);
    }
    Ok(!is_editor_marker(dats, gfx_obj_id)?)
}

/// An editor-only placement marker: a model whose closest level of detail
/// is meant for distance 0 only and whose ladder has an empty level, so
/// the game never draws it at any real distance.
fn is_editor_marker<D: DatSource>(dats: &D, gfx_obj_id: u32) -> Result<bool, ContentError> {
    let Some(gfx) = dats.object::<GfxObj>(gfx_obj_id)? else {
        return Ok(false);
    };
    if !gfx.flags.contains(GfxObjFlags::HAS_DID_DEGRADE) || gfx.did_degrade == 0 {
        return Ok(false);
    }
    let Some(info) = dats.object::<GfxObjDegradeInfo>(gfx.did_degrade)? else {
        return Ok(false);
    };
    Ok(info.degrades.first().is_some_and(|d| d.max_dist == 0.0)
        && info.degrades.iter().any(|d| d.id == 0))
}

fn placed_statics(
    landblock_id: u32,
    info: &LandBlockInfo,
) -> Result<Vec<StaticPlacement>, ContentError> {
    let object = |model_id: u32, frame: &ac_dat::types::Frame| StaticObject {
        model_id,
        origin: [frame.origin.x, frame.origin.y, frame.origin.z],
        orientation: [
            frame.orientation.w,
            frame.orientation.x,
            frame.orientation.y,
            frame.orientation.z,
        ],
    };
    let objects: Vec<StaticObject> = info
        .objects
        .iter()
        .map(|s| object(s.id, &s.frame))
        .collect();
    let portal_cells: Vec<Vec<u16>> = info
        .buildings
        .iter()
        .map(|b| b.portals.iter().map(|p| p.other_cell_id).collect())
        .collect();
    let buildings: Vec<StaticBuilding<'_>> = info
        .buildings
        .iter()
        .zip(&portal_cells)
        .map(|(b, cells)| StaticBuilding {
            shell: object(b.model_id, &b.frame),
            portal_other_cell_ids: cells,
        })
        .collect();
    place_landblock_statics(landblock_id, &objects, &buildings)
        .map_err(|e| ContentError::TooManyStatics(e.to_string()))
}

/// Make landblock `landblock_id` resident in `engine` with its corner at
/// `origin`, replacing a resident copy: terrain, interior cells and their
/// portals, buildings, and static objects (registered in the shadow lists).
/// Objects already registered that reach into the landblock are re-flooded.
/// `Ok(None)` when the dats have no such landblock.
///
/// # Errors
/// A dat read or decode failure, a malformed collision tree, more statics
/// than ids, a shadow flood failure, or an engine without a collision
/// world.
pub fn publish_landblock<D: DatSource>(
    engine: &mut PhysicsEngine,
    dats: &D,
    height_table: &[f32; 256],
    landblock_id: u32,
    origin: Vec3,
) -> Result<Option<StaticCollisionPublication>, ContentError> {
    let landblock_id = (landblock_id & 0xFFFF_0000) | 0xFFFF;
    let prefix = landblock_id & 0xFFFF_0000;
    let Some(block) = dats.object::<LandBlock>(landblock_id)? else {
        return Ok(None);
    };
    let info = dats.object::<LandBlockInfo>(prefix | 0xFFFE)?;
    let terrain = TerrainSurface::from_landblock(&block, height_table);
    let landblock_x = (landblock_id >> 24) & 0xFF;
    let landblock_y = (landblock_id >> 16) & 0xFF;

    let mut publication = StaticCollisionPublication::default();
    let mut cell_surfaces = Vec::new();
    let mut portal_planes = Vec::new();
    let mut statics: Vec<StaticEntity> = Vec::new();
    {
        let world = engine.world_mut().ok_or(ContentError::NoWorld)?;

        // Interior cells: collision, portals, the triangle soup.
        let num_cells = info.as_ref().map_or(0, |i| i.num_cells);
        let mut interior_counter = 0u32;
        for offset in 0..num_cells {
            let cell_id = prefix | (0x0100 + offset);
            let Some(env_cell) = dats.object::<EnvCell>(cell_id)? else {
                continue;
            };
            publish_cell(
                dats,
                world,
                cell_id,
                &env_cell,
                origin,
                &mut cell_surfaces,
                &mut portal_planes,
                &mut publication,
            )?;
            for stab in &env_cell.static_objects {
                let Some(parts) = interior_parts(dats, world, stab.id)? else {
                    continue;
                };
                if interior_counter > INTERIOR_ID_MAX_COUNTER {
                    return Err(ContentError::TooManyStatics(format!(
                        "landblock ({landblock_x:02X},{landblock_y:02X}) exceeds the \
                         4096-entry interior entity id namespace"
                    )));
                }
                let id = INTERIOR_ID_BASE | (landblock_x << 20) | (landblock_y << 12);
                statics.push(StaticEntity {
                    id: id + interior_counter,
                    source_id: stab.id,
                    position: vec3(stab.frame.origin) + origin,
                    rotation: quat(stab.frame.orientation),
                    parts,
                    scale: 1.0,
                    parent_cell_id: cell_id,
                    is_building_shell: false,
                });
                interior_counter += 1;
            }
        }

        if let Some(info) = &info {
            // Buildings: the shell collides through the outdoor cell it
            // stands in.
            for building in &info.buildings {
                let portals = building
                    .portals
                    .iter()
                    .map(|p| BldPortalInfo {
                        other_cell_id: prefix | u32::from(p.other_cell_id),
                        other_portal_id: p.other_portal_id as i16,
                        flags: p.flags,
                    })
                    .collect();
                let local = vec3(building.frame.origin);
                let landcell_id = prefix | terrain.compute_outdoor_cell_id(local.x, local.y);
                let mut shell_part_zero = building.model_id;
                if shell_part_zero >> 24 == 0x02 {
                    shell_part_zero = match dats.object::<Setup>(building.model_id)? {
                        Some(setup) => {
                            world.cache_setup(&setup);
                            setup.parts.first().copied().unwrap_or(0)
                        }
                        None => 0,
                    };
                }
                if shell_part_zero >> 24 == 0x01
                    && let Some(gfx) = dats.object::<GfxObj>(shell_part_zero)?
                {
                    world
                        .cache_gfx_obj(&gfx)
                        .map_err(|error| ContentError::Bsp {
                            id: shell_part_zero,
                            error,
                        })?;
                }
                world.cache_building(
                    landcell_id,
                    BuildingPhysics {
                        placement: Placement::new(quat(building.frame.orientation), local + origin),
                        portals,
                        model_id: shell_part_zero,
                    },
                );
                publication.buildings += 1;
            }

            // Static objects (building shells are placed too, but collide
            // through their building record).
            for placed in placed_statics(landblock_id, info)? {
                let parts = if placed.is_building_shell {
                    Vec::new()
                } else {
                    model_parts(dats, world, placed.model_id, |_, _| Ok(true))?
                };
                let [w, x, y, z] = placed.orientation;
                statics.push(StaticEntity {
                    id: placed.id,
                    source_id: placed.model_id,
                    position: Vec3::from(placed.origin) + origin,
                    rotation: Quat::from_xyzw(x, y, z, w),
                    parts,
                    scale: 1.0,
                    parent_cell_id: 0,
                    is_building_shell: placed.is_building_shell,
                });
            }
        }

        // Procedural scenery: the trees, rocks and bushes generated from
        // the terrain, each scaled as placed.
        let building_origins: Vec<Vec3> = info
            .iter()
            .flat_map(|i| i.buildings.iter().map(|b| vec3(b.frame.origin)))
            .collect();
        let spawns = match dats.object::<Region>(REGION_ID)? {
            Some(region) => {
                scenery::generate(&region, &block, &building_origins, &SceneryReads(dats))
            }
            None => Vec::new(),
        };
        let mut scenery_counter = 0u32;
        for spawn in spawns {
            let parts = model_parts(dats, world, spawn.object_id, |_, _| Ok(true))?;
            if parts.is_empty() {
                continue;
            }
            if scenery_counter > SCENERY_ID_MAX_COUNTER {
                return Err(ContentError::TooManyStatics(format!(
                    "landblock ({landblock_x:02X},{landblock_y:02X}) exceeds the \
                     4096-entry procedural scenery id namespace"
                )));
            }
            let scale = if spawn.scale > 0.0 { spawn.scale } else { 1.0 };
            statics.push(StaticEntity {
                id: SCENERY_ID_BASE | (landblock_x << 20) | (landblock_y << 12) | scenery_counter,
                source_id: spawn.object_id,
                position: spawn.origin + origin,
                rotation: spawn.rotation,
                parts: parts
                    .into_iter()
                    .map(|part| PartRef {
                        position: part.position * scale,
                        scale: part.scale * scale,
                        ..part
                    })
                    .collect(),
                scale,
                parent_cell_id: 0,
                is_building_shell: false,
            });
            scenery_counter += 1;
        }
    }

    engine.add_landblock(
        landblock_id,
        terrain,
        cell_surfaces,
        portal_planes,
        origin.x,
        origin.y,
    );
    publication.placed = statics
        .iter()
        .map(|s| PlacedStatic {
            id: s.id,
            source_id: s.source_id,
            landblock_id,
            position: s.position - origin,
            rotation: s.rotation,
        })
        .collect();
    publish_statics(engine, landblock_id, origin, &statics, &mut publication)?;
    let (shadows, world) = engine.shadows_and_world();
    for owner in shadows.capture_reflood_owners_for_landblock(landblock_id) {
        shadows
            .reflood_owner_for_landblock(world, owner, landblock_id)
            .map_err(|error| ContentError::Flood { owner, error })?;
    }
    Ok(Some(publication))
}

#[allow(clippy::too_many_arguments)]
fn publish_cell<D: DatSource>(
    dats: &D,
    world: &mut CollisionWorld,
    cell_id: u32,
    env_cell: &EnvCell,
    origin: Vec3,
    cell_surfaces: &mut Vec<CellSurface>,
    portal_planes: &mut Vec<PortalPlane>,
    publication: &mut StaticCollisionPublication,
) -> Result<(), ContentError> {
    if env_cell.environment_id == 0 {
        return Ok(());
    }
    let Some(environment) = dats.object::<Environment>(env_cell.environment_file_id())? else {
        return Ok(());
    };
    let Some(structure) = environment.cell(u32::from(env_cell.cell_structure)) else {
        return Ok(());
    };
    let rotation = quat(env_cell.position.orientation);
    let cell_origin = vec3(env_cell.position.origin) + origin;
    world
        .cache_cell_struct(
            cell_id,
            env_cell,
            structure,
            Placement::new(rotation, cell_origin),
        )
        .map_err(|error| ContentError::Bsp { id: cell_id, error })?;
    publication.cells += 1;

    let local_vertex = |id: &u16| {
        structure
            .vertex_array
            .vertices
            .iter()
            .find(|(k, _)| k == id)
            .map(|(_, v)| vec3(v.origin))
    };
    for portal in &env_cell.cell_portals {
        let Some((_, polygon)) = structure
            .polygons
            .iter()
            .find(|(k, _)| *k == portal.polygon_id)
        else {
            continue;
        };
        let vertices: Option<Vec<Vec3>> = polygon
            .vertex_ids
            .iter()
            .map(|id| local_vertex(id).map(|v| rotation * v + cell_origin))
            .collect();
        if let Some(plane) = vertices.and_then(|v| {
            PortalPlane::from_vertices(
                &v,
                u32::from(portal.other_cell_id),
                cell_id & 0xFFFF,
                portal.flags,
            )
        }) {
            portal_planes.push(plane);
        }
    }
    let polygons: Vec<Vec<Vec3>> = structure
        .physics_polygons
        .iter()
        .filter_map(|(_, p)| p.vertex_ids.iter().map(local_vertex).collect())
        .collect();
    cell_surfaces.push(CellSurface::transformed(
        cell_id,
        &polygons,
        rotation,
        cell_origin,
    ));
    Ok(())
}

fn publish_statics(
    engine: &mut PhysicsEngine,
    landblock_id: u32,
    origin: Vec3,
    statics: &[StaticEntity],
    publication: &mut StaticCollisionPublication,
) -> Result<(), ContentError> {
    for entity in statics {
        let (shadows, world) = engine.shadows_and_world();
        match register_static(shadows, world, landblock_id, origin, entity)? {
            StaticCollisionKind::BuildingShell => {}
            StaticCollisionKind::Bsp => publication.bsp_owners += 1,
            StaticCollisionKind::Setup => publication.setup_owners += 1,
            StaticCollisionKind::NoCollision => publication.no_collision += 1,
        }
    }
    Ok(())
}

/// Register one static object in `shadows` as a landblock publication
/// does: its collision (its parts' collision trees, else its setup's
/// cylinders, else its spheres) with its drawn part array, or the part
/// array alone (for drawing only) when it has no collision. A building's
/// shell is not registered. `world` holds the object's models (collision
/// trees, drawn bounds, setup) and the cells its flood reaches;
/// `landblock_id` and its corner `origin` place the landblock in the
/// engine's frame.
///
/// # Errors
/// The shadow flood failed (a building portal names a missing partner).
pub fn register_static(
    shadows: &mut ShadowObjectRegistry,
    world: &CollisionWorld,
    landblock_id: u32,
    origin: Vec3,
    entity: &StaticEntity,
) -> Result<StaticCollisionKind, ContentError> {
    if entity.is_building_shell {
        return Ok(StaticCollisionKind::BuildingShell);
    }
    let get_gfx = |id: u32| world.gfx_obj(id);
    let bsp_shapes = shadow_builder::from_landblock_bsp_parts(&entity.parts, false, &get_gfx);
    let (part_array, _) =
        shadow_builder::from_static_render_parts(&entity.parts, &get_gfx, &|id| {
            world.visual_bounds(id).copied()
        });
    let site = RegistrationSite {
        world_pos: entity.position,
        rotation: entity.rotation,
        world_offset_x: origin.x,
        world_offset_y: origin.y,
        landblock_id,
        seed_cell_id: entity.parent_cell_id,
    };
    let shapes: Vec<ShadowShape> = if bsp_shapes.is_empty() {
        setup_shapes(world, entity)
    } else {
        bsp_shapes
    };
    let kind = if shapes.is_empty() {
        StaticCollisionKind::NoCollision
    } else if shapes[0].collision_type == crate::shadow::ShadowCollisionType::Bsp {
        StaticCollisionKind::Bsp
    } else {
        StaticCollisionKind::Setup
    };
    if shapes.is_empty() && part_array.is_empty() {
        return Ok(kind);
    }
    shadows
        .register_multi_part(
            world,
            entity.id,
            &shapes,
            site,
            // An object made from the dats is static: an ethereal mover
            // still collides with it.
            PhysicsState::STATIC.bits(),
            EntityCollisionFlags::default(),
            true,
            Some(&part_array),
        )
        .map_err(|error| ContentError::Flood {
            owner: entity.id,
            error,
        })?;
    Ok(kind)
}

/// A static setup without collision-tree parts collides with its cylinders,
/// else its spheres, scaled.
fn setup_shapes(world: &CollisionWorld, entity: &StaticEntity) -> Vec<ShadowShape> {
    let Some(setup) = world.setup(entity.source_id) else {
        return Vec::new();
    };
    let scale = if entity.scale > 0.0 {
        entity.scale
    } else {
        1.0
    };
    let mut shapes = Vec::new();
    for &(cyl_origin, radius, height) in &setup.cyl_spheres {
        let radius_scaled = radius * scale;
        let height_scaled = if height > 0.0 { height } else { radius * 4.0 } * scale;
        if radius_scaled <= 0.0 {
            continue;
        }
        shapes.push(ShadowShape::cylinder(
            entity.source_id,
            cyl_origin * scale,
            Quat::IDENTITY,
            scale,
            radius_scaled,
            height_scaled,
        ));
    }
    if setup.cyl_spheres.is_empty() {
        for sphere in &setup.spheres {
            if sphere.radius <= 0.0 {
                continue;
            }
            shapes.push(ShadowShape::sphere(
                entity.source_id,
                sphere.center * scale,
                Quat::IDENTITY,
                scale,
                sphere.radius * scale,
            ));
        }
    }
    shapes
}

/// What a server-placed object's collision is built from: the fields of
/// its physics and public descriptions a session and a window both hold.
#[derive(Debug, Clone, PartialEq)]
pub struct ServerObjectShape {
    /// Its setup.
    pub setup_id: u32,
    /// Its scale (1 when the server sent none).
    pub scale: f32,
    /// The part swaps it was sent: part index and the model in its place.
    pub part_changes: Vec<(usize, u32)>,
    /// Its motion table, whose default state poses its parts.
    pub motion_table_id: Option<u32>,
    /// The placement its parts rest in when its motion table gives no pose:
    /// the one it is drawn in.
    pub placement: u32,
    /// Its collision flags ([`server_object_flags`]).
    pub flags: EntityCollisionFlags,
}

/// A server object's collision flags: it has a weenie, the player, PK,
/// PK-lite, impenetrable and bypass bits of its public bitfield, and the
/// creature bit when its item type is a creature.
#[must_use]
pub fn server_object_flags(
    public_weenie_bitfield: Option<u32>,
    is_creature: bool,
) -> EntityCollisionFlags {
    let mut flags = EntityCollisionFlags::HAS_WEENIE;
    if let Some(bitfield) = public_weenie_bitfield {
        flags |= EntityCollisionFlags::from_pwd_bitfield(bitfield).0;
    }
    if is_creature {
        flags |= EntityCollisionFlags::IS_CREATURE;
    }
    EntityCollisionFlags(flags)
}

/// The part frames of a motion table's default state: the first frame of
/// the default style's default substate cycle. `None` when the table has
/// no such cycle or its animation has no frames.
pub fn default_state_part_frames(
    table: &MotionTable,
    load_animation: impl Fn(u32) -> Option<Arc<Animation>>,
) -> Option<Vec<Frame>> {
    let first = default_state_first_anim(table)?;
    let animation = load_animation(first.anim_id)?;
    if animation.part_frames.is_empty() {
        return None;
    }
    let index = usize::try_from(first.low_frame.max(0))
        .unwrap_or(0)
        .min(animation.part_frames.len() - 1);
    let frames = &animation.part_frames[index].frames;
    (!frames.is_empty()).then(|| {
        frames
            .iter()
            .map(|f| Frame {
                origin: Vec3::new(f.origin.x, f.origin.y, f.origin.z),
                orientation: Quat::from_xyzw(
                    f.orientation.x,
                    f.orientation.y,
                    f.orientation.z,
                    f.orientation.w,
                ),
            })
            .collect()
    })
}

/// The first animation of a motion table's default state cycle.
fn default_state_first_anim(table: &MotionTable) -> Option<&ac_dat::objects::AnimData> {
    let substate = table
        .style_defaults
        .iter()
        .find(|(style, _)| *style == table.default_style)
        .map(|(_, substate)| *substate)?;
    let key = (table.default_style << 16) | (substate & 0x00FF_FFFF);
    table
        .cycles
        .iter()
        .find(|(k, _)| *k == key)
        .and_then(|(_, cycle)| cycle.anims.first())
}

/// Register a server-placed object (`entity_id`) in `engine`'s shadow
/// lists at `site` with physics state `state`: the collision of its parts
/// (posed as its motion table's default state, else the setup's resting
/// pose, with its part swaps) or its setup's cylinders and spheres, at its
/// scale; the caller has taken out anything registered under the id before. `Ok(false)`
/// when nothing was registered: the dats lack its setup, it has no
/// collision and no drawn parts, or the engine has no collision world.
///
/// # Errors
/// A dat read, a malformed collision tree, or a shadow flood that failed;
/// the object is left unregistered.
pub fn register_server_object<D: DatSource>(
    engine: &mut PhysicsEngine,
    dats: &D,
    entity_id: u32,
    shape: &ServerObjectShape,
    state: u32,
    site: RegistrationSite,
) -> Result<bool, ContentError> {
    let Some(setup) = dats.object::<Setup>(shape.setup_id)? else {
        return Ok(false);
    };
    let mut parts: Vec<u32> = setup.parts.clone();
    for &(index, model) in &shape.part_changes {
        if let Some(part) = parts.get_mut(index) {
            *part = model;
        }
    }
    let table = match shape.motion_table_id.filter(|id| *id != 0) {
        Some(id) => dats.object::<MotionTable>(id)?,
        None => None,
    };
    let pose = match table {
        Some(table) => {
            let animation = match default_state_first_anim(&table) {
                Some(first) => dats.object::<Animation>(first.anim_id)?,
                None => None,
            };
            default_state_part_frames(&table, |_| animation.clone())
        }
        None => None,
    };

    let Some(collision_world) = engine.world_mut() else {
        return Ok(false);
    };
    collision_world.cache_setup(&setup);
    for &part in &parts {
        if collision_world.gfx_obj(part).is_some() {
            continue;
        }
        if let Some(gfx) = dats.object::<GfxObj>(part)? {
            collision_world
                .cache_gfx_obj(&gfx)
                .map_err(|error| ContentError::Bsp { id: part, error })?;
        }
    }
    let (shadows, collision_world) = engine.shadows_and_world();
    let has_bsp = |id: u32| {
        collision_world
            .gfx_obj(id)
            .is_some_and(crate::world::GfxObjPhysics::has_physics)
    };
    let bounds = |id: u32| {
        collision_world.gfx_obj(id).map(|g| {
            crate::shadow::ShadowPartGeometry::new(g.bounding_sphere, g.visual_bounds.as_ref())
        })
    };
    let shapes = shadow_builder::from_setup(
        &setup,
        shape.scale,
        &has_bsp,
        pose.as_deref(),
        Some(&parts),
        Some(&bounds),
        Some(shape.placement),
    );
    let render_parts = shadow_builder::from_setup_render_parts(
        &setup,
        shape.scale,
        Some(&parts),
        pose.as_deref(),
        &|id| collision_world.gfx_obj(id),
        &|id| collision_world.visual_bounds(id).copied(),
        Some(shape.placement),
    );
    if shapes.is_empty() && render_parts.is_empty() {
        return Ok(false);
    }
    shadows
        .register_multi_part(
            collision_world,
            entity_id,
            &shapes,
            site,
            state,
            shape.flags,
            false,
            Some(&render_parts),
        )
        .map_err(|error| ContentError::Flood {
            owner: entity_id,
            error,
        })?;
    Ok(true)
}

#[cfg(test)]
mod server_object_tests {
    use super::*;

    /// OpenAC `HubDoorPoseInspectionTests.MotionTablePose_DefaultState_ResolvesOnRealTable`:
    /// the human motion table's default state gives a pose for its parts.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_default_state_pose_resolves_on_a_real_table() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = DatCollection::open(dir).expect("open the retail dats");
        let table: MotionTable = dats.get(0x0900_0001).unwrap().unwrap();
        let pose = default_state_part_frames(&table, |id| {
            dats.get::<Animation>(id).ok().flatten().map(Arc::new)
        })
        .expect("a default pose");
        assert!(!pose.is_empty());
    }

    #[test]
    fn a_server_object_takes_its_bitfield_and_creature_flags() {
        // OpenAC `LiveEntityCollisionBuilder`: every server object has a
        // weenie, the public bitfield's player bits and, for a creature, the
        // creature bit.
        let plain = server_object_flags(None, false);
        assert_eq!(plain.0, EntityCollisionFlags::HAS_WEENIE);
        let creature = server_object_flags(None, true);
        assert_eq!(
            creature.0,
            EntityCollisionFlags::HAS_WEENIE | EntityCollisionFlags::IS_CREATURE
        );
        let player = server_object_flags(Some(0x8), true);
        assert_eq!(player.0, creature.0 | EntityCollisionFlags::IS_PLAYER);
    }
}
