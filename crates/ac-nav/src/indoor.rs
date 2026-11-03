//! A landblock's indoor cells exactly as the game data places them: which
//! environment piece each is built from, where that piece stands, the
//! doorways it opens onto its neighbours through and the cells the data
//! lists as seen from it. A plugin that draws dungeons from its own tiles,
//! one per piece, places each tile with this.

use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

use ac_dat::DatError;
use ac_dat::objects::{CellPortal, CellStruct, EnvCell, EnvCellFlags, Environment, LandBlockInfo};
use ac_physics::content::DatSource;
use glam::{Quat, Vec3};

/// How many landblocks' indoor cells are kept; the landblock read longest
/// ago goes first once it is full.
pub const MAXIMUM_CACHED_INDOOR_LANDBLOCKS: usize = 64;

/// One doorway of an indoor cell: the opening its portal polygon makes onto
/// a neighbouring cell or the outdoors.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CellDoorway {
    /// The full id of the cell on the other side; zero when it leads
    /// outdoors.
    pub other_cell_id: u32,
    /// No cell is on the other side: the doorway leads outdoors.
    pub leads_outside: bool,
    /// The portal must match its partner on the other side exactly.
    pub exact_match: bool,
    /// The portal's side bit as stored (set means side 0, clear side 1).
    pub portal_side: bool,
    /// The middle of the box around the doorway polygon's corners as the
    /// cell is placed, landblock-local metres; the cell's origin when the
    /// structure has no such polygon. The box's middle, not the corners'
    /// average, so the two sides of a doorway agree when one is drawn as a
    /// quad and the other as two triangles.
    pub center: Vec3,
}

/// One indoor cell as the data stores it.
#[derive(Debug, Clone, PartialEq)]
pub struct IndoorCell {
    /// The full cell id, landblock in the high half.
    pub cell_id: u32,
    /// The environment piece (low sixteen bits of its file id); zero for a
    /// cell that names none.
    pub environment_id: u32,
    /// Which of the piece's structures the cell uses.
    pub cell_structure: u32,
    /// Where the piece's origin sits, landblock-local metres.
    pub origin: Vec3,
    /// How the piece is turned, as stored.
    pub orientation: Quat,
    /// The cell sees the landscape, as a building's rooms do.
    pub sees_outside: bool,
    /// The cell's doorways, in the data's order.
    pub doorways: Vec<CellDoorway>,
    /// The full ids of the cells the data lists as seen from this one, in
    /// the data's order: what the game draws from here, which is coarser
    /// than a line of sight.
    pub visible_cells: Vec<u32>,
}

/// The indoor cells of the landblocks asked about, read once each.
#[derive(Debug, Default)]
pub struct IndoorCellCache {
    kept: HashMap<u32, Arc<[IndoorCell]>>,
    order: VecDeque<u32>,
}

impl IndoorCellCache {
    /// Nothing kept yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The indoor cells of the landblock of `landblock_id` (any cell id in
    /// it will do), in cell order; empty for a landblock with none or not in
    /// the data. The same list comes back until the cache drops it.
    ///
    /// # Errors
    /// A dat read or decode failure; nothing is kept then.
    pub fn capture<D: DatSource>(
        &mut self,
        dats: &D,
        landblock_id: u32,
    ) -> Result<Arc<[IndoorCell]>, DatError> {
        let landblock = landblock_id & 0xFFFF_0000;
        if let Some(kept) = self.kept.get(&landblock) {
            return Ok(Arc::clone(kept));
        }
        let cells: Arc<[IndoorCell]> = read_indoor_cells(dats, landblock)?.into();
        if self.kept.len() >= MAXIMUM_CACHED_INDOOR_LANDBLOCKS
            && let Some(oldest) = self.order.pop_front()
        {
            self.kept.remove(&oldest);
        }
        self.kept.insert(landblock, Arc::clone(&cells));
        self.order.push_back(landblock);
        Ok(cells)
    }
}

/// The landblock's cell count from its information file, then each cell the
/// count covers, as stored. A cell the files do not have is left out.
///
/// # Errors
/// A dat read or decode failure.
pub fn read_indoor_cells<D: DatSource>(
    dats: &D,
    landblock: u32,
) -> Result<Vec<IndoorCell>, DatError> {
    let landblock = landblock & 0xFFFF_0000;
    let mut cells = Vec::new();
    let Some(info) = dats.object::<LandBlockInfo>(landblock | 0xFFFE)? else {
        return Ok(cells);
    };
    let first_cell = landblock | 0x0100;
    // Hundreds of cells share a few dozen environments.
    let mut environments: HashMap<u16, Option<Arc<Environment>>> = HashMap::new();
    for offset in 0..info.num_cells {
        let cell_id = first_cell + offset;
        let Some(cell) = dats.object::<EnvCell>(cell_id)? else {
            continue;
        };
        let o = cell.position.orientation;
        let p = cell.position.origin;
        let origin = Vec3::new(p.x, p.y, p.z);
        let orientation = Quat::from_xyzw(o.x, o.y, o.z, o.w);
        let environment = if cell.environment_id == 0 || cell.cell_portals.is_empty() {
            None
        } else if let Some(kept) = environments.get(&cell.environment_id) {
            kept.clone()
        } else {
            let read = dats.object::<Environment>(cell.environment_file_id())?;
            environments.insert(cell.environment_id, read.clone());
            read
        };
        let structure = environment
            .as_deref()
            .and_then(|e| e.cell(u32::from(cell.cell_structure)));
        let doorways = cell
            .cell_portals
            .iter()
            .map(|portal| doorway(portal, landblock, structure, origin, orientation))
            .collect();
        cells.push(IndoorCell {
            cell_id,
            environment_id: u32::from(cell.environment_id),
            cell_structure: u32::from(cell.cell_structure),
            origin,
            orientation,
            sees_outside: cell.flags.contains(EnvCellFlags::SEEN_OUTSIDE),
            doorways,
            visible_cells: cell.visible_cell_ids().collect(),
        });
    }
    Ok(cells)
}

/// A portal as a doorway, its polygon placed as the cell is.
fn doorway(
    portal: &CellPortal,
    landblock: u32,
    structure: Option<&CellStruct>,
    origin: Vec3,
    orientation: Quat,
) -> CellDoorway {
    // The data marks a doorway outdoors by its flag, and names no cell
    // there (0xFFFF); either says so.
    let leads_outside =
        portal.flags & CellPortal::NO_OTHER_CELL != 0 || portal.other_cell_id == 0xFFFF;
    let center = structure
        .and_then(|s| placed_box_middle(s, portal.polygon_id, origin, orientation))
        .unwrap_or(origin);
    CellDoorway {
        other_cell_id: if leads_outside {
            0
        } else {
            landblock | u32::from(portal.other_cell_id)
        },
        leads_outside,
        exact_match: portal.flags & CellPortal::EXACT_MATCH != 0,
        portal_side: portal.flags & CellPortal::PORTAL_SIDE != 0,
        center,
    }
}

/// The middle of the box around a structure polygon's corners placed as the
/// cell is; `None` when the polygon or any corner is missing.
fn placed_box_middle(
    structure: &CellStruct,
    polygon_id: u16,
    origin: Vec3,
    orientation: Quat,
) -> Option<Vec3> {
    let (_, polygon) = structure
        .polygons
        .iter()
        .find(|(id, _)| *id == polygon_id)?;
    let mut low = Vec3::splat(f32::INFINITY);
    let mut high = Vec3::splat(f32::NEG_INFINITY);
    for vertex_id in &polygon.vertex_ids {
        let (_, vertex) = structure
            .vertex_array
            .vertices
            .iter()
            .find(|(id, _)| id == vertex_id)?;
        let at =
            orientation * Vec3::new(vertex.origin.x, vertex.origin.y, vertex.origin.z) + origin;
        low = low.min(at);
        high = high.max(at);
    }
    (low.x <= high.x).then(|| (low + high) * 0.5)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Maps/IndoorCellCaptureTests.cs`
    //! over its `TwoRoomLandblock.cs` fixture (shared with the floorplan
    //! tests). The read-only list test has no counterpart: the shared list
    //! is an `Arc<[_]>`, which cannot be changed through.

    use super::*;
    use crate::floorplan::tests::{
        CELL_SEEING_OUTSIDE, EMPTY_LANDBLOCK, TURNED_ROOM, TWO_ROOMS, UPSTAIRS_ROOM, two_rooms,
    };

    #[test]
    fn every_listed_cell_comes_back_with_its_piece_and_placement() {
        let dats = two_rooms();
        let mut cache = IndoorCellCache::new();
        let cells = cache.capture(&dats, TURNED_ROOM).unwrap();

        let ids: Vec<u32> = cells.iter().map(|c| c.cell_id).collect();
        assert_eq!(ids, [TURNED_ROOM, UPSTAIRS_ROOM, CELL_SEEING_OUTSIDE]);

        let turned = &cells[0];
        assert_eq!(turned.environment_id, 0x0123);
        assert_eq!(turned.cell_structure, 1);
        assert_eq!(turned.origin, Vec3::new(20.0, 30.0, 0.5));
        let quarter = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
        assert!(turned.orientation.abs_diff_eq(quarter, 1e-6));
        assert!(!turned.sees_outside);

        let upstairs = &cells[1];
        assert_eq!(upstairs.cell_structure, 2);
        assert_eq!(upstairs.origin, Vec3::new(40.0, 30.0, 7.2));
        assert_eq!(upstairs.orientation, Quat::IDENTITY);

        let outside = &cells[2];
        assert_eq!(outside.environment_id, 0);
        assert!(outside.sees_outside);

        let reads = dats.reads();
        let again = cache.capture(&dats, TWO_ROOMS).unwrap();
        assert!(Arc::ptr_eq(&cells, &again));
        assert_eq!(dats.reads(), reads);
    }

    #[test]
    fn each_doorway_names_its_neighbour_and_stands_where_the_cell_places_it() {
        let dats = two_rooms();
        let cells = IndoorCellCache::new().capture(&dats, TWO_ROOMS).unwrap();

        // The east wall's doorway, x = 10 from y 0 to 8 and z 0 to 3 in the
        // piece, turned a quarter and moved to (20, 30, 0.5).
        assert_eq!(cells[0].doorways.len(), 1);
        let east = cells[0].doorways[0];
        assert_eq!(east.other_cell_id, UPSTAIRS_ROOM);
        assert!(!east.leads_outside && !east.exact_match && !east.portal_side);
        assert!(east.center.abs_diff_eq(Vec3::new(16.0, 40.0, 2.0), 1e-4));

        // A doorway outdoors through a polygon the piece lacks stands at the
        // cell's origin and names no cell.
        let out = cells[1].doorways[0];
        assert_eq!(out.other_cell_id, 0);
        assert!(out.leads_outside && out.exact_match && out.portal_side);
        assert_eq!(out.center, Vec3::new(40.0, 30.0, 7.2));
        let unflagged = cells[1].doorways[1];
        assert_eq!(unflagged.other_cell_id, 0);
        assert!(unflagged.leads_outside, "0xFFFF names no cell: outdoors");

        assert!(cells[2].doorways.is_empty());
    }

    #[test]
    fn the_cells_seen_from_a_cell_come_back_as_full_ids() {
        let dats = two_rooms();
        let cells = IndoorCellCache::new().capture(&dats, TWO_ROOMS).unwrap();
        assert_eq!(cells[0].visible_cells, [UPSTAIRS_ROOM]);
        assert_eq!(cells[1].visible_cells, [TURNED_ROOM, CELL_SEEING_OUTSIDE]);
        assert!(cells[2].visible_cells.is_empty());
    }

    #[test]
    fn a_landblock_without_cells_or_without_files_has_none() {
        let dats = two_rooms();
        let mut cache = IndoorCellCache::new();
        assert!(cache.capture(&dats, EMPTY_LANDBLOCK).unwrap().is_empty());
        assert!(cache.capture(&dats, 0x7F7F_0000).unwrap().is_empty());
    }

    #[test]
    fn the_landblock_read_longest_ago_is_dropped_once_the_cache_is_full() {
        let dats = two_rooms();
        let mut cache = IndoorCellCache::new();
        let first = cache.capture(&dats, TWO_ROOMS).unwrap();

        for index in 1..MAXIMUM_CACHED_INDOOR_LANDBLOCKS {
            let other = 0x7000_0000 + (u32::try_from(index).unwrap() << 16);
            let _ = cache.capture(&dats, other).unwrap();
        }
        assert!(Arc::ptr_eq(
            &first,
            &cache.capture(&dats, TWO_ROOMS).unwrap()
        ));

        let _ = cache.capture(&dats, 0x6F00_0000).unwrap();
        let again = cache.capture(&dats, TWO_ROOMS).unwrap();
        assert!(!Arc::ptr_eq(&first, &again));
        assert_eq!(*first, *again);
    }
}
