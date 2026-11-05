use ac_bits::Reader;

use super::DatObject;
use super::bsp::{BspTree, BspTreeKind};
use super::geometry::{Polygon, VertexArray, polygon_table};
use super::read::{self, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::read_bool32;

type Result<T> = core::result::Result<T, DecodeError>;

/// A set of interior cell shapes that cells (`EnvCell`) reuse. File id
/// `0x0D......`.
#[derive(Debug, Clone, PartialEq)]
pub struct Environment {
    /// File id.
    pub id: u32,
    /// Cell shapes keyed by index, in file order.
    pub cells: Vec<(u32, CellStruct)>,
}

impl DatObject for Environment {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = u64::from(r.u32()?);
        let cells = keyed(r, "cell structures", count, read::u32, CellStruct::unpack)?;
        Ok(Self { id, cells })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

impl Environment {
    /// The cell shape with index `index`.
    #[must_use]
    pub fn cell(&self, index: u32) -> Option<&CellStruct> {
        self.cells
            .iter()
            .find(|(key, _)| *key == index)
            .map(|(_, cell)| cell)
    }
}

/// The shape of one interior cell: its mesh, its portals to other cells
/// and three trees over its polygons.
#[derive(Debug, Clone, PartialEq)]
pub struct CellStruct {
    /// Vertices.
    pub vertex_array: VertexArray,
    /// Drawn polygons keyed by index, in file order.
    pub polygons: Vec<(u16, Polygon)>,
    /// Indices of the polygons that are portals.
    pub portals: Vec<u16>,
    /// Point-in-cell tree.
    pub cell_bsp: BspTree,
    /// Collision polygons keyed by index, in file order.
    pub physics_polygons: Vec<(u16, Polygon)>,
    /// Collision tree over the collision polygons.
    pub physics_bsp: BspTree,
    /// Visibility tree over the drawn polygons, when stored.
    pub drawing_bsp: Option<BspTree>,
}

impl CellStruct {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let num_polygons = r.u32()?;
        let num_physics_polygons = r.u32()?;
        let num_portals = u64::from(r.u32()?);
        let vertex_array = VertexArray::unpack(r)?;
        let polygons = polygon_table(r, "polygons", num_polygons)?;
        let portals = list(r, num_portals, read::u16)?;
        r.align(4)?;
        let cell_bsp = BspTree::unpack(r, BspTreeKind::Cell)?;
        let physics_polygons = polygon_table(r, "physics polygons", num_physics_polygons)?;
        let physics_bsp = BspTree::unpack(r, BspTreeKind::Physics)?;
        let drawing_bsp = if read_bool32(r)? {
            Some(BspTree::unpack(r, BspTreeKind::Drawing)?)
        } else {
            None
        };
        r.align(4)?;
        Ok(Self {
            vertex_array,
            polygons,
            portals,
            cell_bsp,
            physics_polygons,
            physics_bsp,
            drawing_bsp,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::objects::bsp::BspNode;
    use crate::objects::bsp::tests::small_tree;
    use crate::objects::geometry::tests::{polygon, vertex_array};
    use crate::objects::geometry::{SidesType, Stippling};
    use ac_bits::Writer;

    fn cell_struct(w: &mut Writer, drawing: bool) {
        w.u32(2); // polygons
        w.u32(1); // physics polygons
        w.u32(1); // portals
        vertex_array(w, 4);
        w.u16(0);
        polygon(w, 4, 0, SidesType::NONE);
        w.u16(1);
        polygon(w, 3, 0, SidesType::CLOCKWISE);
        w.u16(1); // portal: polygon 1
        w.align(4);
        small_tree(w, BspTreeKind::Cell);
        w.u16(0);
        polygon(w, 4, Stippling::NO_POS, SidesType::NONE);
        small_tree(w, BspTreeKind::Physics);
        w.u32(u32::from(drawing));
        if drawing {
            small_tree(w, BspTreeKind::Drawing);
        }
        w.align(4);
    }

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0D00_00CA);
        w.u32(2);
        w.u32(0);
        cell_struct(&mut w, true);
        w.u32(5);
        cell_struct(&mut w, false);
        w.into_vec()
    }

    #[test]
    fn unpacks_cell_structures() {
        let bytes = sample();
        let r = &mut Reader::new(&bytes);
        let e = Environment::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(e.cells.len(), 2);
        let c = e.cell(0).unwrap();
        assert_eq!(c.vertex_array.vertices.len(), 4);
        assert_eq!(c.polygons.len(), 2);
        assert_eq!(c.polygons[1].1.neg_uv_indices, vec![3, 2, 1]);
        assert_eq!(c.portals, vec![1]);
        assert_eq!(c.physics_polygons[0].1.pos_uv_indices.len(), 0);
        assert!(matches!(c.cell_bsp.root(), Some(BspNode::Split(_))));
        assert!(c.drawing_bsp.is_some());
        assert!(e.cell(5).unwrap().drawing_bsp.is_none());
        assert!(e.cell(1).is_none());
    }

    #[test]
    fn truncated_environment_is_an_error_at_every_length() {
        let bytes = sample();
        for len in 0..bytes.len() {
            assert!(
                Environment::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }
}
