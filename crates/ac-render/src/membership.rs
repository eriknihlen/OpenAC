//! Which interior statics each interior cell draws.
//!
//! A static object inside a building or dungeon is listed in the dat under
//! the one cell it stands in, but its parts can reach into the cells next
//! to it (a staircase running down from one room into the next). The frame
//! walk draws a cell's objects only when it reaches that cell, so a static
//! filed only under its own cell vanishes whenever the view reaches a room
//! it overlaps but not the room that lists it. Instead each static is
//! registered as the collision registers it, and its drawn parts are
//! flooded through the cells' portals from the cell it stands in: it is
//! drawn in every cell its parts reach.
//!
//! The result is worked out on the loader thread when a landblock's cells
//! are read, from the dats alone, so the window can always build it again.

use std::collections::HashMap;

use ac_dat::objects::{EnvCell, GfxObj, Setup};
use ac_physics::content::{ContentError, StaticEntity, register_static, setup_parts};
use ac_physics::shadow_builder::PartRef;
use ac_physics::{CollisionWorld, ShadowObjectRegistry};
use glam::{Quat, Vec3};

use crate::cells::{BuildingData, CellData, degrade_levels};
use crate::content::Content;
use crate::degrade::is_runtime_hidden_marker;
use crate::meshing::{quat, vec3};
use crate::world_frame::LandblockCoord;

/// A static an interior cell draws: the cell whose dat lists it and its
/// index in that cell's statics.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct CellStatic {
    /// The cell that lists it (the one it stands in).
    pub cell: u32,
    /// Its index in that cell's statics.
    pub index: u32,
}

/// Work out which statics each of a landblock's interior `cells` draws
/// (into each cell's `members`), registering every static as the
/// collision does and flooding its drawn parts through the landblock's
/// cells and buildings. A static that could not be registered is drawn in
/// no cell; the failures are returned, one line each.
pub fn assign_interior_membership(
    content: &Content,
    coord: LandblockCoord,
    cells: &mut [CellData],
    buildings: &[BuildingData],
) -> Vec<String> {
    let prefix = coord.terrain_id() & 0xFFFF_0000;
    let landblock_id = prefix | 0xFFFF;
    // The landblock alone, its corner at the origin: interior cells are
    // joined only to cells of their own landblock.
    let mut world = CollisionWorld::new();
    world.register_terrain(landblock_id, Vec3::ZERO);
    for cell in cells.iter() {
        if let Some(physics) = &cell.physics {
            world.insert_cell(physics.clone());
        }
    }
    for b in buildings {
        world.insert_building(prefix | (b.land_cell as u32 + 1), b.physics.clone());
    }

    let mut shadows = ShadowObjectRegistry::new();
    let mut owners: HashMap<u32, CellStatic> = HashMap::new();
    let mut failures = Vec::new();
    let mut next_id = 1u32;
    for cell in cells.iter() {
        let Some(env) = content.dats.get::<EnvCell>(cell.id).ok().flatten() else {
            continue;
        };
        // The same statics, in the same order, as the cell's own list.
        let stabs = env
            .static_objects
            .iter()
            .filter(|s| matches!(s.id >> 24, 0x01 | 0x02));
        for (index, stab) in stabs.enumerate() {
            let parts = match interior_parts(content, &mut world, stab.id) {
                Ok(parts) => parts,
                Err(error) => {
                    failures.push(format!(
                        "static {:#010x} in cell {:#010x}: {error}",
                        stab.id, cell.id
                    ));
                    continue;
                }
            };
            if parts.is_empty() {
                continue;
            }
            let id = next_id;
            next_id += 1;
            let entity = StaticEntity {
                id,
                source_id: stab.id,
                position: vec3(stab.frame.origin),
                rotation: quat(stab.frame.orientation),
                parts,
                scale: 1.0,
                parent_cell_id: cell.id,
                is_building_shell: false,
            };
            match register_static(&mut shadows, &world, landblock_id, Vec3::ZERO, &entity) {
                Ok(_) => {
                    owners.insert(
                        id,
                        CellStatic {
                            cell: cell.id,
                            index: index as u32,
                        },
                    );
                }
                Err(error) => failures.push(format!(
                    "static {:#010x} in cell {:#010x}: {error}",
                    stab.id, cell.id
                )),
            }
        }
    }
    for cell in cells.iter_mut() {
        cell.members = cell_members(&shadows, cell.id, &owners);
    }
    failures
}

/// The statics a cell draws: its draw rows' owners, each once, in row
/// order. Only what the registry lists counts: a static is never drawn in
/// the cell that lists it unless its rows are there.
#[must_use]
pub fn cell_members(
    shadows: &ShadowObjectRegistry,
    cell_id: u32,
    owners: &HashMap<u32, CellStatic>,
) -> Vec<CellStatic> {
    let mut out = Vec::new();
    let mut previous = None;
    for entry in shadows.retail_part_entries_in_cell(cell_id) {
        // An owner's rows in a cell are consecutive, one per part.
        if previous == Some(entry.entity_id) {
            continue;
        }
        previous = Some(entry.entity_id);
        if let Some(owner) = owners.get(&entry.entity_id)
            && !out.contains(owner)
        {
            out.push(*owner);
        }
    }
    out
}

/// The parts of an interior static the collision registers: a GfxObj is
/// one part at the object's origin, a Setup its parts in the resting
/// placement; an editor-only marker part is left out unless it collides
/// (some dungeons build invisible ledges and walls from them). Each kept
/// model is cached in `world`.
fn interior_parts(
    content: &Content,
    world: &mut CollisionWorld,
    model_id: u32,
) -> Result<Vec<PartRef>, ContentError> {
    let candidates = match model_id >> 24 {
        0x01 => vec![PartRef {
            gfx_obj_id: model_id,
            position: Vec3::ZERO,
            rotation: Quat::IDENTITY,
            scale: Vec3::ONE,
        }],
        0x02 => match content.dats.get::<Setup>(model_id)? {
            Some(setup) => {
                world.cache_setup(&setup);
                setup_parts(&setup)
            }
            None => return Ok(Vec::new()),
        },
        _ => return Ok(Vec::new()),
    };
    let mut kept = Vec::with_capacity(candidates.len());
    for part in candidates {
        let Some(gfx) = content.dats.get::<GfxObj>(part.gfx_obj_id)? else {
            continue;
        };
        world
            .cache_gfx_obj(&gfx)
            .map_err(|error| ContentError::Bsp {
                id: part.gfx_obj_id,
                error,
            })?;
        if keeps_part(content, world, &gfx) {
            kept.push(part);
        }
    }
    Ok(kept)
}

/// A part is kept unless it is an editor-only marker without collision.
fn keeps_part(content: &Content, world: &CollisionWorld, gfx: &GfxObj) -> bool {
    let collides = world
        .gfx_obj(gfx.id)
        .is_some_and(ac_physics::GfxObjPhysics::has_physics);
    collides || !is_runtime_hidden_marker(&degrade_levels(content, gfx))
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_physics::{EntityCollisionFlags, RegistrationSite, ShadowPartGeometry, ShadowShape};

    /// One collision-tree part with a 1 m sphere at the origin (OpenAC
    /// `WalkProductionWorldDataTests.Bsp`).
    fn bsp(gfx_obj_id: u32) -> ShadowShape {
        ShadowShape::bsp(
            gfx_obj_id,
            Vec3::ZERO,
            Quat::IDENTITY,
            1.0,
            ShadowPartGeometry::new(ac_physics::Sphere::new(Vec3::ZERO, 1.0), None),
        )
    }

    /// OpenAC `WalkProductionWorldDataTests.Register`: a static at the
    /// origin seeded at `seed_cell_id`, its one part its draw array.
    fn register(shadows: &mut ShadowObjectRegistry, entity_id: u32, seed_cell_id: u32) {
        let parts = [bsp(0x0100_1234)];
        shadows
            .register_multi_part(
                CollisionWorld::empty(),
                entity_id,
                &parts,
                RegistrationSite {
                    world_pos: Vec3::ZERO,
                    rotation: Quat::IDENTITY,
                    world_offset_x: 0.0,
                    world_offset_y: 0.0,
                    landblock_id: 0x8A02_0000,
                    seed_cell_id,
                },
                0,
                EntityCollisionFlags::default(),
                true,
                Some(&parts),
            )
            .unwrap();
    }

    /// OpenAC `WalkProductionWorldDataTests.GetCellStatics_RegisteredEntityIsResolvedByLocalEntityIdAtTheRetailCell`:
    /// a static is drawn in the cell its rows are in (0x8A02015F), not in
    /// the cell that lists it (here a decoy, 0x8A0201C1).
    #[test]
    fn a_static_is_drawn_where_its_rows_are_not_where_it_is_listed() {
        const ENTITY: u32 = 0x48A0_2001;
        const RETAIL_CELL: u32 = 0x8A02_015F;
        const DECOY_PARENT: u32 = 0x8A02_01C1;
        let mut shadows = ShadowObjectRegistry::new();
        register(&mut shadows, ENTITY, RETAIL_CELL);
        assert_eq!(shadows.retail_cell_array(ENTITY), Some(&[RETAIL_CELL][..]));
        let owner = CellStatic {
            cell: DECOY_PARENT,
            index: 0,
        };
        let owners = HashMap::from([(ENTITY, owner)]);
        assert_eq!(cell_members(&shadows, RETAIL_CELL, &owners), vec![owner]);
        assert!(cell_members(&shadows, DECOY_PARENT, &owners).is_empty());
    }

    /// OpenAC `WalkProductionWorldDataTests.GetCellStatics_UnregisteredEntityContributesToNoCellWithoutCounting`:
    /// a static with no rows is drawn in no cell, not even its own.
    #[test]
    fn an_unregistered_static_is_drawn_in_no_cell() {
        const ENTITY: u32 = 0x48A0_2003;
        const PARENT: u32 = 0x8A02_015F;
        let shadows = ShadowObjectRegistry::new();
        assert!(shadows.retail_cell_array(ENTITY).is_none());
        let owners = HashMap::from([(
            ENTITY,
            CellStatic {
                cell: PARENT,
                index: 0,
            },
        )]);
        assert!(cell_members(&shadows, PARENT, &owners).is_empty());
    }

    fn content() -> Content {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        Content::open(&dir).expect("open the retail dats")
    }

    /// The Facility Hub (landblock 0x8A02): the staircase (Setup
    /// 0x02000623) listed in room 0x8A02015F runs down into room
    /// 0x8A02015E, so the lower room draws it too (OpenAC registers it
    /// render-only and its flood reaches both rooms). Without that it
    /// vanished whenever the view reached the lower room but not the upper.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_facility_hub_staircase_is_drawn_in_both_rooms_it_spans() {
        const UPPER: u32 = 0x8A02_015F;
        const LOWER: u32 = 0x8A02_015E;
        const STAIR: u32 = 0x0200_0623;
        let d = content();
        let near = near_of(&d, LandblockCoord { x: 0x8A, y: 0x02 });
        assert!(
            near.membership_failures.is_empty(),
            "{:?}",
            near.membership_failures
        );
        let upper = near.cells.iter().find(|c| c.id == UPPER).unwrap();
        let lower = near.cells.iter().find(|c| c.id == LOWER).unwrap();
        let index = upper
            .statics
            .iter()
            .position(|s| s.model_id == STAIR)
            .expect("the staircase is listed in the upper room") as u32;
        let stair = CellStatic { cell: UPPER, index };
        assert!(upper.members.contains(&stair), "{:X?}", upper.members);
        assert!(lower.members.contains(&stair), "{:X?}", lower.members);
        assert!(!lower.statics.iter().any(|s| s.model_id == STAIR));
    }

    /// A landblock's near content as the loader builds it.
    fn near_of(d: &Content, coord: LandblockCoord) -> crate::assets::NearData {
        let region = d
            .dats
            .get::<ac_dat::objects::Region>(crate::assets::REGION_ID)
            .unwrap()
            .unwrap();
        crate::assets::build_near(d, coord, &region)
    }

    /// Whether a static's model has a part that draws: a model that exists,
    /// has vertices and is not an editor-only marker.
    fn has_drawn_part(d: &Content, model_id: u32) -> bool {
        let parts = match model_id >> 24 {
            0x01 => vec![model_id],
            0x02 => d
                .dats
                .get::<Setup>(model_id)
                .unwrap()
                .map_or_else(Vec::new, |s| s.parts.clone()),
            _ => Vec::new(),
        };
        parts.into_iter().any(|id| {
            d.dats.get::<GfxObj>(id).unwrap().is_some_and(|gfx| {
                !gfx.vertices.vertices.is_empty()
                    && !is_runtime_hidden_marker(&degrade_levels(d, &gfx))
            })
        })
    }

    /// Every interior static with a part that draws is drawn in at least
    /// the cell it stands in, in the Facility Hub (0x8A02), the Town
    /// Network (0x0007) and two more dungeons (0x01D9, 0x0019); every
    /// member names a real static. A static whose registration failed
    /// would be drawn nowhere: this is the guard against that.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn every_drawn_interior_static_is_in_its_own_cell() {
        let d = content();
        for (x, y) in [(0x8A, 0x02), (0x00, 0x07), (0x01, 0xD9), (0x00, 0x19)] {
            let near = near_of(&d, LandblockCoord { x, y });
            assert!(
                near.membership_failures.is_empty(),
                "{:?}",
                near.membership_failures
            );
            let mut checked = 0;
            for cell in &near.cells {
                for m in &cell.members {
                    let owner = near.cells.iter().find(|c| c.id == m.cell).unwrap();
                    assert!((m.index as usize) < owner.statics.len());
                }
                for (index, s) in cell.statics.iter().enumerate() {
                    if !has_drawn_part(&d, s.model_id) {
                        continue;
                    }
                    checked += 1;
                    let own = CellStatic {
                        cell: cell.id,
                        index: index as u32,
                    };
                    assert!(
                        cell.members.contains(&own),
                        "static {:#010x} (#{index}) of cell {:#010x} is drawn in no cell of its own",
                        s.model_id,
                        cell.id
                    );
                }
            }
            assert!(checked > 0, "landblock {x:02X}{y:02X} has no drawn statics");
        }
    }

    /// A static with several parts in a cell is listed once (OpenAC's cell
    /// view skips an owner's consecutive rows).
    #[test]
    fn a_static_with_several_parts_is_listed_once() {
        const ENTITY: u32 = 0x48A0_2004;
        const CELL: u32 = 0x8A02_015F;
        let parts = [bsp(0x0100_1234), bsp(0x0100_1235), bsp(0x0100_1236)];
        let mut shadows = ShadowObjectRegistry::new();
        shadows
            .register_multi_part(
                CollisionWorld::empty(),
                ENTITY,
                &parts,
                RegistrationSite {
                    world_pos: Vec3::ZERO,
                    rotation: Quat::IDENTITY,
                    world_offset_x: 0.0,
                    world_offset_y: 0.0,
                    landblock_id: 0x8A02_0000,
                    seed_cell_id: CELL,
                },
                0,
                EntityCollisionFlags::default(),
                true,
                Some(&parts),
            )
            .unwrap();
        assert_eq!(shadows.retail_part_entries_in_cell(CELL).len(), 3);
        let owner = CellStatic {
            cell: CELL,
            index: 2,
        };
        let owners = HashMap::from([(ENTITY, owner)]);
        assert_eq!(cell_members(&shadows, CELL, &owners), vec![owner]);
    }
}
