use ac_bits::Reader;

use super::{DatObject, check_unique};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Frame;

/// The objects placed on one landblock: static objects, buildings and the
/// restriction table. File id `0xXXYYFFFE`.
#[derive(Debug, Clone, PartialEq)]
pub struct LandBlockInfo {
    /// File id.
    pub id: u32,
    /// Number of interior cells (`0xXXYY0100` onward) the landblock owns.
    pub num_cells: u32,
    /// Static objects.
    pub objects: Vec<Stab>,
    /// Buildings.
    pub buildings: Vec<BuildingInfo>,
    /// Restriction table, `(key, value)` in file order.
    pub restriction_table: Vec<(u32, u32)>,
}

impl DatObject for LandBlockInfo {
    const DAT: DatFileType = DatFileType::Cell;

    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let id = r.u32()?;
        let num_cells = r.u32()?;
        let num_objects = r.u32()?;
        let mut objects = Vec::new();
        for _ in 0..num_objects {
            objects.push(Stab::unpack(r)?);
        }
        let num_buildings = r.u16()?;
        let has_restriction_table = r.u16()? != 0;
        let mut buildings = Vec::new();
        for _ in 0..num_buildings {
            buildings.push(BuildingInfo::unpack(r)?);
        }
        let mut restriction_table = Vec::new();
        if has_restriction_table {
            let num_entries = r.u16()?;
            let _num_buckets = r.u16()?;
            for _ in 0..num_entries {
                let key = r.u32()?;
                let value = r.u32()?;
                check_unique("restriction table", &restriction_table, key)?;
                restriction_table.push((key, value));
            }
        }
        Ok(Self {
            id,
            num_cells,
            objects,
            buildings,
            restriction_table,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A static object: a model or setup id and where it stands.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Stab {
    /// Model (`0x01......`) or setup (`0x02......`) id.
    pub id: u32,
    /// Position within the landblock.
    pub frame: Frame,
}

impl Stab {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        Ok(Self {
            id: r.u32()?,
            frame: Frame::unpack(r)?,
        })
    }
}

/// A building: its exterior model, placement and portals into its cells.
#[derive(Debug, Clone, PartialEq)]
pub struct BuildingInfo {
    /// Exterior model id.
    pub model_id: u32,
    /// Position within the landblock.
    pub frame: Frame,
    /// Number of leaves in the building's cell tree.
    pub num_leaves: u32,
    /// Portals from outside into the building.
    pub portals: Vec<BuildingPortal>,
}

impl BuildingInfo {
    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let model_id = r.u32()?;
        let frame = Frame::unpack(r)?;
        let num_leaves = r.u32()?;
        let num_portals = r.u32()?;
        let mut portals = Vec::new();
        for _ in 0..num_portals {
            portals.push(BuildingPortal::unpack(r)?);
        }
        Ok(Self {
            model_id,
            frame,
            num_leaves,
            portals,
        })
    }
}

/// A portal from a building's exterior into one of its cells.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuildingPortal {
    /// Portal flags (bit 0: exact match, bit 1: portal side).
    pub flags: u16,
    /// Low 16 bits of the cell on the other side.
    pub other_cell_id: u16,
    /// Portal index within that cell.
    pub other_portal_id: u16,
    /// Cells visible through the portal (low 16 bits of each id).
    pub stab_list: Vec<u16>,
}

impl BuildingPortal {
    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let flags = r.u16()?;
        let other_cell_id = r.u16()?;
        let other_portal_id = r.u16()?;
        let num_stabs = r.u16()?;
        let mut stab_list = Vec::new();
        for _ in 0..num_stabs {
            stab_list.push(r.u16()?);
        }
        // Padded to 4 bytes from the start of the object.
        r.align(4)?;
        Ok(Self {
            flags,
            other_cell_id,
            other_portal_id,
            stab_list,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn frame(w: &mut Writer, x: f32) {
        for v in [x, 2.0, 3.0, 1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
    }

    fn sample(with_restrictions: bool) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xA9B4_FFFE);
        w.u32(123); // cells
        w.u32(2); // objects
        w.u32(0x0100_0AEF);
        frame(&mut w, 75.5);
        w.u32(0x0200_0279);
        frame(&mut w, 93.25);
        w.u16(1); // buildings
        w.u16(u16::from(with_restrictions));
        w.u32(0x0100_0C1E);
        frame(&mut w, 84.0);
        w.u32(64); // leaves
        w.u32(2); // portals
        // Portal with 3 stabs: 8 + 6 = 14 bytes, padded by 2.
        w.u16(1);
        w.u16(0x0145);
        w.u16(0);
        w.u16(3);
        for s in [0x013F, 0x0140, 0x0141] {
            w.u16(s);
        }
        w.align(4);
        // Portal with 2 stabs: 12 bytes, no padding.
        w.u16(3);
        w.u16(0x016F);
        w.u16(1);
        w.u16(2);
        w.u16(0x016F);
        w.u16(0x0170);
        w.align(4);
        if with_restrictions {
            w.u16(2);
            w.u16(8);
            w.u32(0xA9B4_0100);
            w.u32(0x7000_0001);
            w.u32(0xA9B4_0101);
            w.u32(0x7000_0002);
        }
        w.into_vec()
    }

    #[test]
    fn unpacks_objects_buildings_and_portals() {
        let bytes = sample(false);
        let mut r = Reader::new(&bytes);
        let info = LandBlockInfo::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(info.num_cells, 123);
        assert_eq!(info.objects.len(), 2);
        assert_eq!(info.objects[1].id, 0x0200_0279);
        assert_eq!(info.objects[1].frame.origin.x, 93.25);
        assert_eq!(info.objects[1].frame.orientation.w, 1.0);
        let b = &info.buildings[0];
        assert_eq!((b.model_id, b.num_leaves), (0x0100_0C1E, 64));
        assert_eq!(b.portals.len(), 2);
        assert_eq!(b.portals[0].stab_list, vec![0x013F, 0x0140, 0x0141]);
        assert_eq!(b.portals[1].flags, 3);
        assert_eq!(b.portals[1].other_cell_id, 0x016F);
        assert_eq!(b.portals[1].other_portal_id, 1);
        assert!(info.restriction_table.is_empty());
    }

    #[test]
    fn unpacks_the_restriction_table() {
        let bytes = sample(true);
        let info = LandBlockInfo::unpack(&mut Reader::new(&bytes)).unwrap();
        assert_eq!(
            info.restriction_table,
            vec![(0xA9B4_0100, 0x7000_0001), (0xA9B4_0101, 0x7000_0002)]
        );
    }

    #[test]
    fn duplicate_restriction_key_is_an_error() {
        let mut bytes = sample(true);
        let n = bytes.len();
        // Make the second key equal the first.
        bytes.copy_within(n - 16..n - 12, n - 8);
        assert!(matches!(
            LandBlockInfo::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::DuplicateKey { .. })
        ));
    }

    #[test]
    fn truncated_info_is_an_error_at_every_length() {
        let bytes = sample(true);
        for len in 0..bytes.len() {
            assert!(
                LandBlockInfo::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }
}
