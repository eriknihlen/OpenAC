//! Where a landblock's fixed objects stand, and the local ids they get.
//!
//! A landblock's info record lists static objects (trees, rocks, signs,
//! ...; each a model id and a frame) and buildings (a model id, a frame and
//! the portals into its interior cells). Each becomes one placement with an
//! id unique across the whole world: `0xCXXYYIII`, where `XX`/`YY` are the
//! landblock's x and y and `III` counts 0..=0xFFF within the landblock. The
//! top nibble 0xC keeps these apart from other client-made ids.

use std::fmt;

use crate::physics::compute_outdoor_cell_id;

/// The largest per-landblock counter (4096 ids per landblock).
pub const STATIC_ID_MAX_COUNTER: u32 = 0xFFF;

/// Model ids of these types can be placed: a single model (0x01xxxxxx) or
/// a multi-part setup (0x02xxxxxx).
const GFX_OBJ_TYPE: u32 = 0x0100_0000;
const SETUP_TYPE: u32 = 0x0200_0000;
const TYPE_MASK: u32 = 0xFF00_0000;

/// A landblock listed more than 4096 placeable objects.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StaticIdOverflow {
    /// Landblock x.
    pub landblock_x: u32,
    /// Landblock y.
    pub landblock_y: u32,
}

impl fmt::Display for StaticIdOverflow {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "landblock ({:02X},{:02X}) exceeds the 4096-entry static entity id namespace",
            self.landblock_x & 0xFF,
            self.landblock_y & 0xFF
        )
    }
}

impl std::error::Error for StaticIdOverflow {}

/// The first static id of landblock (`landblock_x`, `landblock_y`).
#[must_use]
pub fn static_entity_id_base(landblock_x: u32, landblock_y: u32) -> u32 {
    0xC000_0000 | ((landblock_x & 0xFF) << 20) | ((landblock_y & 0xFF) << 12)
}

/// The next static id of the landblock, advancing `counter`.
///
/// # Errors
///
/// [`StaticIdOverflow`] once `counter` passes [`STATIC_ID_MAX_COUNTER`];
/// the counter is left unchanged.
pub fn allocate_static_entity_id(
    landblock_x: u32,
    landblock_y: u32,
    counter: &mut u32,
) -> Result<u32, StaticIdOverflow> {
    if *counter > STATIC_ID_MAX_COUNTER {
        return Err(StaticIdOverflow {
            landblock_x,
            landblock_y,
        });
    }
    let id = static_entity_id_base(landblock_x, landblock_y) + *counter;
    *counter += 1;
    Ok(id)
}

/// True if `entity_id` is a landblock static id (top nibble 0xC).
#[must_use]
pub fn is_static_entity_id(entity_id: u32) -> bool {
    entity_id & 0xF000_0000 == 0xC000_0000
}

/// A static object as the landblock info lists it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticObject {
    /// Model id (single model or setup).
    pub model_id: u32,
    /// Landblock-local position in meters.
    pub origin: [f32; 3],
    /// Rotation quaternion (w, x, y, z).
    pub orientation: [f32; 4],
}

/// A building as the landblock info lists it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticBuilding<'a> {
    /// The building's outside shell: model id and frame.
    pub shell: StaticObject,
    /// The low word of the cell on the far side of each portal, in portal
    /// order (0xFFFF for a portal leading outdoors).
    pub portal_other_cell_ids: &'a [u16],
}

/// One placed static object or building shell.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticPlacement {
    /// World-unique static id (`0xCXXYYIII`).
    pub id: u32,
    /// Model id (single model or setup).
    pub model_id: u32,
    /// Landblock-local position in meters.
    pub origin: [f32; 3],
    /// Rotation quaternion (w, x, y, z).
    pub orientation: [f32; 4],
    /// The outdoor cell under the origin (lighting and effects use it; it
    /// is not a parent cell).
    pub effect_cell_id: u32,
    /// True for a building shell.
    pub is_building_shell: bool,
    /// For a building: the first interior cell its portals lead to.
    pub building_shell_anchor_cell_id: Option<u32>,
}

/// Places the static objects, then the buildings, of landblock
/// `landblock_id` (any id within the landblock; the low word is ignored).
/// Entries whose model is neither a single model nor a setup are skipped
/// and take no id.
///
/// # Errors
///
/// [`StaticIdOverflow`] if more than 4096 entries are placed.
pub fn place_landblock_statics(
    landblock_id: u32,
    objects: &[StaticObject],
    buildings: &[StaticBuilding<'_>],
) -> Result<Vec<StaticPlacement>, StaticIdOverflow> {
    let landblock_x = (landblock_id >> 24) & 0xFF;
    let landblock_y = (landblock_id >> 16) & 0xFF;
    let mut counter = 0;
    let mut placements = Vec::with_capacity(objects.len() + buildings.len());

    let mut place = |object: &StaticObject, anchor: Option<Option<u32>>| {
        let id = allocate_static_entity_id(landblock_x, landblock_y, &mut counter)?;
        placements.push(StaticPlacement {
            id,
            model_id: object.model_id,
            origin: object.origin,
            orientation: object.orientation,
            effect_cell_id: compute_outdoor_cell_id(
                landblock_id,
                object.origin[0],
                object.origin[1],
            ),
            is_building_shell: anchor.is_some(),
            building_shell_anchor_cell_id: anchor.flatten(),
        });
        Ok(())
    };

    for object in objects.iter().filter(|o| is_placeable(o.model_id)) {
        place(object, None)?;
    }
    for building in buildings.iter().filter(|b| is_placeable(b.shell.model_id)) {
        let anchor = building
            .portal_other_cell_ids
            .iter()
            .find(|&&cell| cell != 0xFFFF)
            .map(|&cell| (landblock_id & 0xFFFF_0000) | u32::from(cell));
        place(&building.shell, Some(anchor))?;
    }
    Ok(placements)
}

fn is_placeable(model_id: u32) -> bool {
    matches!(model_id & TYPE_MASK, GFX_OBJ_TYPE | SETUP_TYPE)
}

#[cfg(test)]
mod tests {
    use super::*;

    const IDENTITY: [f32; 4] = [1.0, 0.0, 0.0, 0.0];

    fn object(model_id: u32, origin: [f32; 3]) -> StaticObject {
        StaticObject {
            model_id,
            origin,
            orientation: IDENTITY,
        }
    }

    // Golden values below: OpenAC
    // tests/AcDream.Core.Tests/World/LandblockStaticEntityIdAllocatorTests.cs.

    #[test]
    fn bases_are_distinct_and_in_the_namespace() {
        let mut seen = std::collections::HashSet::new();
        for y in 0..=255 {
            for x in 0..=255 {
                let base = static_entity_id_base(x, y);
                assert!(is_static_entity_id(base));
                assert!(seen.insert(base), "duplicate base for ({x},{y})");
            }
        }
    }

    #[test]
    fn the_last_counter_does_not_reach_the_next_landblock() {
        for y in 0..255 {
            assert!(
                static_entity_id_base(0, y) + STATIC_ID_MAX_COUNTER
                    < static_entity_id_base(0, y + 1)
            );
        }
    }

    #[test]
    fn allocation_accepts_the_last_counter_then_fails() {
        let mut counter = STATIC_ID_MAX_COUNTER;
        let last = allocate_static_entity_id(0xA9, 0xB4, &mut counter).unwrap();
        assert_eq!(
            last,
            static_entity_id_base(0xA9, 0xB4) + STATIC_ID_MAX_COUNTER
        );
        let err = allocate_static_entity_id(0xA9, 0xB4, &mut counter).unwrap_err();
        assert!(err.to_string().contains("4096-entry"));
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/World/LandblockLoaderTests.cs.

    #[test]
    fn objects_and_buildings_become_placements() {
        let objects = [
            object(0x0100_0042, [10.0, 20.0, 5.0]),
            object(0x0200_0099, [30.0, 40.0, 10.0]),
        ];
        let buildings = [StaticBuilding {
            shell: object(0x0200_00AA, [50.0, 60.0, 0.0]),
            portal_other_cell_ids: &[],
        }];
        let p = place_landblock_statics(0xA9B4_FFFF, &objects, &buildings).unwrap();
        let got: Vec<_> = p.iter().map(|p| (p.model_id, p.origin)).collect();
        assert_eq!(
            got,
            [
                (0x0100_0042, [10.0, 20.0, 5.0]),
                (0x0200_0099, [30.0, 40.0, 10.0]),
                (0x0200_00AA, [50.0, 60.0, 0.0]),
            ]
        );
        let ids: Vec<_> = p.iter().map(|p| p.id).collect();
        let base = static_entity_id_base(0xA9, 0xB4);
        assert_eq!(ids, [base, base + 1, base + 2]);
    }

    #[test]
    fn unsupported_model_types_are_skipped() {
        let objects = [
            object(0x0100_0001, [0.0; 3]),
            object(0x0300_0002, [0.0; 3]),
            object(0x0200_0003, [0.0; 3]),
        ];
        let p = place_landblock_statics(0xA9B4_0000, &objects, &[]).unwrap();
        assert_eq!(p.len(), 2);
        assert!(p.iter().all(|p| p.model_id != 0x0300_0002));
    }

    #[test]
    fn empty_info_places_nothing() {
        assert!(
            place_landblock_statics(0xA9B4_0000, &[], &[])
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn ids_are_unique_across_landblocks() {
        let objects = [object(0x0100_0001, [0.0; 3]), object(0x0100_0002, [0.0; 3])];
        let a = place_landblock_statics(0xA9B4_0000, &objects, &[]).unwrap();
        let b = place_landblock_statics(0xA9B5_0000, &objects, &[]).unwrap();
        assert!(a.iter().all(|x| b.iter().all(|y| x.id != y.id)));
        assert!(a.iter().chain(&b).all(|p| is_static_entity_id(p.id)));
    }

    #[test]
    fn effect_cell_is_the_outdoor_cell_under_the_origin() {
        let p =
            place_landblock_statics(0xA9B4_0000, &[object(0x0100_0001, [25.0, 49.0, 0.0])], &[])
                .unwrap();
        assert_eq!(p[0].effect_cell_id, 0xA9B4_000B);
        assert!(!p[0].is_building_shell);
        assert_eq!(p[0].building_shell_anchor_cell_id, None);
    }

    #[test]
    fn more_than_255_entries_stay_in_the_landblock_range() {
        let objects = vec![object(0x0100_0001, [0.0; 3]); 300];
        let p = place_landblock_statics(0xA9B4_0000, &objects, &[]).unwrap();
        assert_eq!(p.len(), 300);
        let mut ids: Vec<_> = p.iter().map(|p| p.id).collect();
        ids.dedup();
        assert_eq!(ids.len(), 300);
        assert!(p.last().unwrap().id < static_entity_id_base(0xA9, 0xB5));
    }

    #[test]
    fn overflow_fails_before_aliasing_the_next_landblock() {
        let objects = vec![object(0x0100_0001, [0.0; 3]); STATIC_ID_MAX_COUNTER as usize + 2];
        let err = place_landblock_statics(0xA9B4_0000, &objects, &[]).unwrap_err();
        assert!(err.to_string().contains("4096-entry"));
    }

    #[test]
    fn buildings_are_shells_anchored_at_their_first_interior_portal() {
        let buildings = [StaticBuilding {
            shell: object(0x0200_0123, [10.0, 20.0, 30.0]),
            portal_other_cell_ids: &[0xFFFF, 0x013F, 0x0140],
        }];
        let p = place_landblock_statics(0xA9B4_0000, &[], &buildings).unwrap();
        assert_eq!(p.len(), 1);
        assert!(p[0].is_building_shell);
        assert_eq!(p[0].building_shell_anchor_cell_id, Some(0xA9B4_013F));
    }

    #[test]
    fn a_building_without_interior_portals_has_no_anchor() {
        let buildings = [StaticBuilding {
            shell: object(0x0200_0123, [0.0; 3]),
            portal_other_cell_ids: &[0xFFFF],
        }];
        let p = place_landblock_statics(0xA9B4_0000, &[], &buildings).unwrap();
        assert!(p[0].is_building_shell);
        assert_eq!(p[0].building_shell_anchor_cell_id, None);
    }
}
