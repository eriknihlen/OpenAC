//! Whether a cell belongs to a sealed dungeon, as the game data marks its
//! indoor cells. A walk inside one plans over one grid covering every cell
//! of the dungeon and no terrain.

use ac_dat::objects::{EnvCell, EnvCellFlags};
use ac_physics::content::DatSource;

/// True for an indoor cell that sees nothing outside, as a dungeon's cells
/// do; false for outdoor cells, building interiors that see outside, and
/// cells the data lacks (or cannot read).
#[must_use]
pub fn is_sealed_dungeon<D: DatSource>(dats: &D, cell_id: u32) -> bool {
    let low = cell_id & 0xFFFF;
    if !(0x0100..0xFFFE).contains(&low) {
        return false;
    }
    match dats.object::<EnvCell>(cell_id) {
        Ok(Some(cell)) => !cell.flags.contains(EnvCellFlags::SEEN_OUTSIDE),
        _ => false,
    }
}
