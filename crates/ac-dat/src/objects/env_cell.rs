use ac_bits::Reader;

use super::read::{self, list};
use super::{DatObject, Stab};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Frame;

type Result<T> = core::result::Result<T, DecodeError>;

/// Base id of surfaces; cells store surface ids relative to it.
const SURFACE_BASE: u32 = 0x0800_0000;
/// Base id of environments; cells store environment ids relative to it.
const ENVIRONMENT_BASE: u32 = 0x0D00_0000;

/// Interior cell flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EnvCellFlags(pub u32);

impl EnvCellFlags {
    /// The outdoors can be seen from the cell.
    pub const SEEN_OUTSIDE: u32 = 0x1;
    /// A static object list follows the visible cells.
    pub const HAS_STATIC_OBJS: u32 = 0x2;
    /// A restriction object id ends the cell.
    pub const HAS_RESTRICTION_OBJ: u32 = 0x8;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// One interior cell (a room of a building or dungeon): which shape it
/// uses, where it stands, its portals, the cells visible from it and the
/// objects inside. File id `0xXXYYNNNN` with `NNNN` from `0x0100`.
#[derive(Debug, Clone, PartialEq)]
pub struct EnvCell {
    /// File id.
    pub id: u32,
    /// Flags.
    pub flags: EnvCellFlags,
    /// The cell id stored after the flags.
    pub cell_id: u32,
    /// Surfaces of the cell's polygons, relative to `0x08000000`.
    pub surfaces: Vec<u16>,
    /// Environment holding the shape, relative to `0x0D000000`.
    pub environment_id: u16,
    /// Index of the shape within the environment.
    pub cell_structure: u16,
    /// Position and orientation within the landblock.
    pub position: Frame,
    /// Portals to neighbouring cells.
    pub cell_portals: Vec<CellPortal>,
    /// Cells visible from this one (low 16 bits of each id).
    pub visible_cells: Vec<u16>,
    /// Static objects inside; empty without
    /// [`EnvCellFlags::HAS_STATIC_OBJS`].
    pub static_objects: Vec<Stab>,
    /// Restriction object id; zero without
    /// [`EnvCellFlags::HAS_RESTRICTION_OBJ`].
    pub restriction_obj: u32,
}

impl DatObject for EnvCell {
    const DAT: DatFileType = DatFileType::Cell;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let flags = EnvCellFlags(r.u32()?);
        let cell_id = r.u32()?;
        let num_surfaces = r.u8()?;
        let num_portals = r.u8()?;
        let num_visible_cells = r.u16()?;
        let surfaces = list(r, u64::from(num_surfaces), read::u16)?;
        let environment_id = r.u16()?;
        let cell_structure = r.u16()?;
        let position = Frame::unpack(r)?;
        let cell_portals = list(r, u64::from(num_portals), CellPortal::unpack)?;
        let visible_cells = list(r, u64::from(num_visible_cells), read::u16)?;
        let static_objects = if flags.contains(EnvCellFlags::HAS_STATIC_OBJS) {
            let count = u64::from(r.u32()?);
            list(r, count, Stab::unpack)?
        } else {
            Vec::new()
        };
        let restriction_obj = if flags.contains(EnvCellFlags::HAS_RESTRICTION_OBJ) {
            r.u32()?
        } else {
            0
        };
        Ok(Self {
            id,
            flags,
            cell_id,
            surfaces,
            environment_id,
            cell_structure,
            position,
            cell_portals,
            visible_cells,
            static_objects,
            restriction_obj,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

impl EnvCell {
    /// File id of the environment holding the cell's shape.
    #[must_use]
    pub fn environment_file_id(&self) -> u32 {
        ENVIRONMENT_BASE | u32::from(self.environment_id)
    }

    /// File ids of the surfaces, in order.
    pub fn surface_file_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.surfaces.iter().map(|&s| SURFACE_BASE | u32::from(s))
    }

    /// Full ids of the visible cells, in order (same landblock).
    pub fn visible_cell_ids(&self) -> impl Iterator<Item = u32> + '_ {
        let landblock = self.id & 0xFFFF_0000;
        self.visible_cells
            .iter()
            .map(move |&c| landblock | u32::from(c))
    }
}

/// A portal from a cell into a neighbouring cell or the outdoors.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CellPortal {
    /// Flag bits: [`CellPortal::EXACT_MATCH`], [`CellPortal::PORTAL_SIDE`],
    /// [`CellPortal::NO_OTHER_CELL`].
    pub flags: u16,
    /// Polygon of the cell's shape that is the portal.
    pub polygon_id: u16,
    /// Low 16 bits of the cell on the other side.
    pub other_cell_id: u16,
    /// Portal index within that cell.
    pub other_portal_id: u16,
}

impl CellPortal {
    /// The portal must match its partner exactly.
    pub const EXACT_MATCH: u16 = 0x1;
    /// Side bit, stored inverted: clear means side 1, set means side 0.
    pub const PORTAL_SIDE: u16 = 0x2;
    /// No cell is on the other side (it leads outdoors);
    /// [`CellPortal::other_cell_id`] is then meaningless.
    pub const NO_OTHER_CELL: u16 = 0x4;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            flags: r.u16()?,
            polygon_id: r.u16()?,
            other_cell_id: r.u16()?,
            other_portal_id: r.u16()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn sample(flags: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xA9B4_0105);
        w.u32(flags);
        w.u32(0xA9B4_0105);
        w.u8(2); // surfaces
        w.u8(1); // portals
        w.u16(3); // visible
        w.u16(0x0371);
        w.u16(0x0372);
        w.u16(0x034F);
        w.u16(5);
        for v in [84.09f32, 131.5, 66.0, 0.707, 0.0, 0.0, -0.707] {
            w.f32(v);
        }
        w.u16(7);
        w.u16(25);
        w.u16(0xFFFF);
        w.u16(0xFFFF);
        for c in [0x0100u16, 0x0101, 0x0106] {
            w.u16(c);
        }
        if flags & EnvCellFlags::HAS_STATIC_OBJS != 0 {
            w.u32(1);
            w.u32(0x0200_016D);
            for v in [78.1f32, 2.0, 3.0, 1.0, 0.0, 0.0, 0.0] {
                w.f32(v);
            }
        }
        if flags & EnvCellFlags::HAS_RESTRICTION_OBJ != 0 {
            w.u32(0x7000_0001);
        }
        w.into_vec()
    }

    #[test]
    fn unpacks_with_every_optional_part() {
        let bytes = sample(0xB);
        let r = &mut Reader::new(&bytes);
        let c = EnvCell::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(c.cell_id, 0xA9B4_0105);
        assert_eq!(c.surfaces, vec![0x0371, 0x0372]);
        assert_eq!(c.environment_file_id(), 0x0D00_034F);
        assert_eq!(c.cell_structure, 5);
        assert_eq!(c.position.origin.y, 131.5);
        assert_eq!(c.cell_portals[0].flags, 7);
        assert_eq!(c.cell_portals[0].polygon_id, 25);
        assert_eq!(
            c.visible_cell_ids().collect::<Vec<_>>(),
            vec![0xA9B4_0100, 0xA9B4_0101, 0xA9B4_0106]
        );
        assert_eq!(
            c.surface_file_ids().collect::<Vec<_>>(),
            vec![0x0800_0371, 0x0800_0372]
        );
        assert_eq!(c.static_objects[0].id, 0x0200_016D);
        assert_eq!(c.restriction_obj, 0x7000_0001);
        for len in 0..bytes.len() {
            assert!(
                EnvCell::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn optional_parts_follow_the_flags() {
        let bytes = sample(EnvCellFlags::SEEN_OUTSIDE);
        let r = &mut Reader::new(&bytes);
        let c = EnvCell::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert!(c.static_objects.is_empty());
        assert_eq!(c.restriction_obj, 0);
        assert!(c.flags.contains(EnvCellFlags::SEEN_OUTSIDE));
    }
}
