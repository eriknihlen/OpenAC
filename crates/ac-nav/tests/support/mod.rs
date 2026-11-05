//! Landblocks from the installed game files published into one physics
//! world, the way the client loads their collision, for navigation tests.
//!
//! As OpenAC's `PublishedLandblock` (tests/AcDream.App.Tests/Navigation):
//! terrain, interior cells and their polygons, building shells, and the
//! static objects of each landblock and its cells, placed around the first
//! landblock, whose south-west corner is the origin. Procedural scenery
//! (trees, rocks) is not placed here; the runtime's loader
//! (`ac_physics::content::publish_landblock`) places it, and the walk
//! corpus walks over that.

#![allow(dead_code)]

use std::collections::HashMap;
use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_dat::objects::{
    EnvCell, Environment, GfxObj, LandBlock, LandBlockInfo, Region, Setup, Stab,
};
use ac_nav::NavBody;
use ac_physics::dat::{quat, vec3};
use ac_physics::shadow_builder::{PartRef, from_landblock_bsp_parts, from_setup};
use ac_physics::{
    BldPortalInfo, BuildingPhysics, CellSurface, CollisionWorld, PhysicsEngine, Placement,
    RegistrationSite, ShadowPartGeometry, TerrainSurface,
};
use glam::{Quat, Vec3};

/// The human setup, whose steps a player's body takes.
const HUMAN_SETUP: u32 = 0x0200_004E;

/// The retail dats, from `OPENAC_DAT_DIR` or the default install folder.
pub fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

/// Landblocks published into one physics world, with the body that walks
/// them and where named cells stand.
pub struct PublishedLandblocks {
    pub engine: PhysicsEngine,
    pub body: NavBody,
    pub cell_origins: HashMap<u32, Vec3>,
}

/// Publishes landblocks into one physics world around the first.
pub fn load(
    dats: &DatCollection,
    landblock_ids: &[u32],
    cells_to_locate: &[u32],
) -> PublishedLandblocks {
    let region: Region = dats
        .get(0x1300_0000)
        .expect("read the region")
        .expect("the region");
    let heights = region.land_defs.land_height_table;
    let human: Setup = dats
        .get(HUMAN_SETUP)
        .expect("read the human setup")
        .expect("the human setup");

    let base_x = ((landblock_ids[0] >> 24) & 0xFF) as i32;
    let base_y = ((landblock_ids[0] >> 16) & 0xFF) as i32;
    let mut world = CollisionWorld::new();
    let mut offsets = HashMap::new();
    let mut blocks = Vec::new();
    let mut statics: Vec<(u32, u32, Stab, Vec3, u32)> = Vec::new();
    let mut next_id = 0x7000_0000u32;
    for &landblock_id in landblock_ids {
        let block_x = ((landblock_id >> 24) & 0xFF) as i32;
        let block_y = ((landblock_id >> 16) & 0xFF) as i32;
        let origin = Vec3::new(
            (block_x - base_x) as f32 * 192.0,
            (block_y - base_y) as f32 * 192.0,
            0.0,
        );
        let prefix = landblock_id & 0xFFFF_0000;
        offsets.insert(prefix, origin);
        let block: LandBlock = dats
            .get(prefix | 0xFFFF)
            .expect("read the landblock")
            .expect("the landblock");
        let terrain = TerrainSurface::from_landblock(&block, &heights);

        let mut cells = Vec::new();
        if let Ok(Some(info)) = dats.get::<LandBlockInfo>(prefix | 0xFFFE) {
            for offset in 0..info.num_cells {
                let cell_id = prefix | (0x0100 + offset);
                let Ok(Some(cell)) = dats.get::<EnvCell>(cell_id) else {
                    continue;
                };
                let Ok(Some(environment)) = dats.get::<Environment>(cell.environment_file_id())
                else {
                    continue;
                };
                let Some(structure) = environment.cell(u32::from(cell.cell_structure)) else {
                    continue;
                };
                let rotation = quat(cell.position.orientation);
                let at = vec3(cell.position.origin) + origin;
                world
                    .cache_cell_struct(cell_id, &cell, structure, Placement::new(rotation, at))
                    .expect("cell trees prepare");
                let vertices: HashMap<u16, Vec3> = structure
                    .vertex_array
                    .vertices
                    .iter()
                    .map(|(id, vertex)| (*id, vec3(vertex.origin)))
                    .collect();
                let polygons: Vec<Vec<Vec3>> = structure
                    .physics_polygons
                    .iter()
                    .filter_map(|(_, polygon)| {
                        polygon
                            .vertex_ids
                            .iter()
                            .map(|id| vertices.get(id).copied())
                            .collect::<Option<Vec<Vec3>>>()
                    })
                    .collect();
                cells.push(CellSurface::transformed(cell_id, &polygons, rotation, at));
                for stab in &cell.static_objects {
                    statics.push((next_id, landblock_id, *stab, origin, cell_id));
                    next_id += 1;
                }
            }
            for building in &info.buildings {
                let portals = building
                    .portals
                    .iter()
                    .map(|portal| BldPortalInfo {
                        other_cell_id: prefix | u32::from(portal.other_cell_id),
                        other_portal_id: portal.other_portal_id as i16,
                        flags: portal.flags,
                    })
                    .collect();
                let local = vec3(building.frame.origin);
                let landcell_id = prefix | terrain.compute_outdoor_cell_id(local.x, local.y);
                let mut shell = building.model_id;
                if shell & 0xFF00_0000 == 0x0200_0000 {
                    shell = dats
                        .get::<Setup>(building.model_id)
                        .ok()
                        .flatten()
                        .and_then(|setup| setup.parts.first().copied())
                        .unwrap_or(0);
                }
                cache_gfx(dats, &mut world, shell);
                world.cache_building(
                    landcell_id,
                    BuildingPhysics {
                        placement: Placement::new(quat(building.frame.orientation), local + origin),
                        portals,
                        model_id: shell,
                    },
                );
            }
            for stab in &info.objects {
                statics.push((next_id, landblock_id, *stab, origin, 0));
                next_id += 1;
            }
        }
        blocks.push((prefix | 0xFFFF, terrain, cells, origin));
    }

    for (_, _, stab, _, _) in &statics {
        cache_object(dats, &mut world, stab.id);
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    for (landblock_id, terrain, cells, origin) in blocks {
        engine.add_landblock(landblock_id, terrain, cells, Vec::new(), origin.x, origin.y);
    }
    for (entity_id, landblock_id, stab, origin, seed_cell_id) in statics {
        register_static(
            dats,
            &mut engine,
            entity_id,
            landblock_id,
            &stab,
            origin,
            seed_cell_id,
        );
    }

    let mut cell_origins = HashMap::new();
    for &cell_id in cells_to_locate {
        if let Ok(Some(cell)) = dats.get::<EnvCell>(cell_id)
            && let Some(offset) = offsets.get(&(cell_id & 0xFFFF_0000))
        {
            cell_origins.insert(cell_id, vec3(cell.position.origin) + *offset);
        }
    }

    PublishedLandblocks {
        engine,
        body: NavBody::player(human.step_up_height, human.step_down_height),
        cell_origins,
    }
}

fn cache_gfx(dats: &DatCollection, world: &mut CollisionWorld, id: u32) {
    if id & 0xFF00_0000 != 0x0100_0000 || world.gfx_obj(id).is_some() {
        return;
    }
    if let Ok(Some(gfx)) = dats.get::<GfxObj>(id) {
        world.cache_gfx_obj(&gfx).expect("model trees prepare");
    }
}

fn cache_object(dats: &DatCollection, world: &mut CollisionWorld, id: u32) {
    match id & 0xFF00_0000 {
        0x0100_0000 => cache_gfx(dats, world, id),
        0x0200_0000 => {
            if let Ok(Some(setup)) = dats.get::<Setup>(id) {
                for part in &setup.parts {
                    cache_gfx(dats, world, *part);
                }
                world.cache_setup(&setup);
            }
        }
        _ => {}
    }
}

/// Registers a landblock or cell static the way OpenAC publishes static
/// collision: its collision-tree parts, else its setup's cylinders or
/// spheres.
fn register_static(
    dats: &DatCollection,
    engine: &mut PhysicsEngine,
    entity_id: u32,
    landblock_id: u32,
    stab: &Stab,
    origin: Vec3,
    seed_cell_id: u32,
) {
    let (shadows, world) = engine.shadows_and_world();
    let shapes = match stab.id & 0xFF00_0000 {
        0x0100_0000 => from_landblock_bsp_parts(
            &[PartRef {
                gfx_obj_id: stab.id,
                position: Vec3::ZERO,
                rotation: Quat::IDENTITY,
                scale: Vec3::ONE,
            }],
            false,
            &|id| world.gfx_obj(id),
        ),
        0x0200_0000 => {
            let Ok(Some(setup)) = dats.get::<Setup>(stab.id) else {
                return;
            };
            let has_bsp = |id: u32| world.gfx_obj(id).is_some_and(|g| g.has_physics());
            let bounds = |id: u32| {
                world
                    .gfx_obj(id)
                    .map(|g| ShadowPartGeometry::new(g.bounding_sphere, g.visual_bounds.as_ref()))
            };
            from_setup(&setup, 1.0, &has_bsp, None, None, Some(&bounds), None)
        }
        _ => return,
    };
    if shapes.is_empty() {
        return;
    }
    let site = RegistrationSite {
        world_pos: vec3(stab.frame.origin) + origin,
        rotation: quat(stab.frame.orientation),
        world_offset_x: origin.x,
        world_offset_y: origin.y,
        landblock_id,
        seed_cell_id,
    };
    shadows
        .register_multi_part(
            world,
            entity_id,
            &shapes,
            site,
            0,
            Default::default(),
            true,
            None,
        )
        .expect("a static registers");
}
