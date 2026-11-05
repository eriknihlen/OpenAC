use ac_bits::Reader;

use super::DatObject;
use super::bsp::{BspTree, BspTreeKind};
use super::geometry::{Polygon, VertexArray, polygon_table};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Vector3;

type Result<T> = core::result::Result<T, DecodeError>;

/// Model flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct GfxObjFlags(pub u32);

impl GfxObjFlags {
    /// Physics polygons and a physics BSP tree are stored.
    pub const HAS_PHYSICS: u32 = 0x1;
    /// Drawing polygons and a drawing BSP tree are stored.
    pub const HAS_DRAWING: u32 = 0x2;
    /// Unnamed bit; it adds no fields.
    pub const UNKNOWN: u32 = 0x4;
    /// A degrade table id is stored.
    pub const HAS_DID_DEGRADE: u32 = 0x8;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// A model: vertices, polygons, the surfaces they use, and the BSP trees
/// for collision and drawing. File id `0x01......`.
#[derive(Debug, Clone, PartialEq)]
pub struct GfxObj {
    /// File id.
    pub id: u32,
    /// Flags.
    pub flags: GfxObjFlags,
    /// Surface ids; polygons index into this list.
    pub surfaces: Vec<u32>,
    /// Vertices.
    pub vertices: VertexArray,
    /// Collision polygons keyed by id, in file order; empty without
    /// [`GfxObjFlags::HAS_PHYSICS`].
    pub physics_polygons: Vec<(u16, Polygon)>,
    /// Collision tree; present with [`GfxObjFlags::HAS_PHYSICS`].
    pub physics_bsp: Option<BspTree>,
    /// Centre used to sort the model for drawing.
    pub sort_center: Vector3,
    /// Drawn polygons keyed by id, in file order; empty without
    /// [`GfxObjFlags::HAS_DRAWING`].
    pub polygons: Vec<(u16, Polygon)>,
    /// Drawing tree; present with [`GfxObjFlags::HAS_DRAWING`].
    pub drawing_bsp: Option<BspTree>,
    /// Degrade table id; 0 without [`GfxObjFlags::HAS_DID_DEGRADE`].
    pub did_degrade: u32,
}

impl DatObject for GfxObj {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let flags = GfxObjFlags(r.u32()?);
        let num_surfaces = r.compressed_u32()?;
        let mut surfaces = Vec::new();
        for _ in 0..num_surfaces {
            surfaces.push(r.u32()?);
        }
        let vertices = VertexArray::unpack(r)?;
        let mut physics_polygons = Vec::new();
        let mut physics_bsp = None;
        if flags.contains(GfxObjFlags::HAS_PHYSICS) {
            let count = r.compressed_u32()?;
            physics_polygons = polygon_table(r, "physics polygons", count)?;
            physics_bsp = Some(BspTree::unpack(r, BspTreeKind::Physics)?);
        }
        let sort_center = Vector3::unpack(r)?;
        let mut polygons = Vec::new();
        let mut drawing_bsp = None;
        if flags.contains(GfxObjFlags::HAS_DRAWING) {
            let count = r.compressed_u32()?;
            polygons = polygon_table(r, "polygons", count)?;
            drawing_bsp = Some(BspTree::unpack(r, BspTreeKind::Drawing)?);
        }
        let did_degrade = if flags.contains(GfxObjFlags::HAS_DID_DEGRADE) {
            r.u32()?
        } else {
            0
        };
        Ok(Self {
            id,
            flags,
            surfaces,
            vertices,
            physics_polygons,
            physics_bsp,
            sort_center,
            polygons,
            drawing_bsp,
            did_degrade,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::objects::bsp::{BspNode, BspTag};
    use crate::objects::geometry::{SidesType, Stippling, Uv};
    use ac_bits::Writer;

    fn vertex(w: &mut Writer, id: u16, uvs: &[(f32, f32)]) {
        w.u16(id);
        w.u16(u16::try_from(uvs.len()).unwrap());
        for v in [1.0f32, 2.0, 3.0, 0.0, 0.0, 1.0] {
            w.f32(v);
        }
        for &(u, v) in uvs {
            w.f32(u);
            w.f32(v);
        }
    }

    fn triangle(w: &mut Writer, id: u16, stippling: u8, sides: i32) {
        w.u16(id);
        w.u8(3);
        w.u8(stippling);
        w.i32(sides);
        w.i16(0);
        w.i16(-1);
        for v in [0u16, 1, 2] {
            w.u16(v);
        }
        if stippling & Stippling::NO_POS == 0 {
            w.bytes(&[0, 1, 0]);
        }
        if sides == SidesType::CLOCKWISE && stippling & Stippling::NO_NEG == 0 {
            w.bytes(&[1, 1, 1]);
        }
    }

    fn leaf_tree(w: &mut Writer, physics: bool) {
        w.u32(BspTag::LEAF.0);
        w.i32(0);
        if physics {
            w.i32(1);
            for v in [0.0f32, 0.0, 0.0, 1.0] {
                w.f32(v);
            }
            w.u32(1);
            w.u16(0);
        }
    }

    /// Every optional part present.
    fn full_sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0100_0439);
        w.u32(0xB);
        w.compressed_u32(2).unwrap();
        w.u32(0x0800_04A5);
        w.u32(0x0800_04A6);
        w.i32(1);
        w.u32(3);
        vertex(&mut w, 0, &[(0.5, 0.0)]);
        vertex(&mut w, 1, &[(0.25, 1.0), (0.0, 1.0)]);
        vertex(&mut w, 2, &[]);
        w.compressed_u32(1).unwrap();
        triangle(&mut w, 0, Stippling::NO_POS, SidesType::LANDBLOCK);
        leaf_tree(&mut w, true);
        for v in [0.1f32, 0.2, 0.3] {
            w.f32(v);
        }
        w.compressed_u32(2).unwrap();
        triangle(&mut w, 0, Stippling::NONE, SidesType::LANDBLOCK);
        triangle(&mut w, 1, Stippling::POSITIVE, SidesType::CLOCKWISE);
        leaf_tree(&mut w, false);
        w.u32(0x1100_02ED);
        w.into_vec()
    }

    #[test]
    fn unpacks_every_part() {
        let bytes = full_sample();
        let mut r = Reader::new(&bytes);
        let g = GfxObj::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(g.id, 0x0100_0439);
        assert!(
            g.flags
                .contains(GfxObjFlags::HAS_PHYSICS | GfxObjFlags::HAS_DRAWING)
        );
        assert_eq!(g.surfaces, vec![0x0800_04A5, 0x0800_04A6]);
        assert_eq!(g.vertices.vertices.len(), 3);
        let (key, v) = &g.vertices.vertices[1];
        assert_eq!(*key, 1);
        assert_eq!(v.uvs, vec![Uv { u: 0.25, v: 1.0 }, Uv { u: 0.0, v: 1.0 }]);
        assert_eq!(v.origin.z, 3.0);
        assert_eq!(v.normal.z, 1.0);
        let (_, p) = &g.physics_polygons[0];
        assert!(p.pos_uv_indices.is_empty());
        assert_eq!(p.vertex_ids, vec![0, 1, 2]);
        let Some(BspNode::Leaf(leaf)) = g.physics_bsp.as_ref().and_then(BspTree::root) else {
            panic!("no physics leaf")
        };
        assert_eq!((leaf.solid, leaf.polygons.len()), (1, 1));
        assert_eq!(g.sort_center.y, 0.2);
        assert_eq!(g.polygons.len(), 2);
        assert_eq!(g.polygons[0].1.pos_uv_indices, vec![0, 1, 0]);
        assert!(g.polygons[0].1.neg_uv_indices.is_empty());
        assert_eq!(g.polygons[1].1.neg_uv_indices, vec![1, 1, 1]);
        assert_eq!(g.polygons[1].1.neg_surface, -1);
        assert!(g.drawing_bsp.is_some());
        assert_eq!(g.did_degrade, 0x1100_02ED);
    }

    #[test]
    fn flags_gate_the_optional_parts() {
        let mut w = Writer::new();
        w.u32(0x0100_0C22);
        w.u32(GfxObjFlags::UNKNOWN);
        w.compressed_u32(0).unwrap();
        w.i32(1);
        w.u32(0);
        for v in [0.0f32, 0.0, 0.0] {
            w.f32(v);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let g = GfxObj::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert!(g.physics_bsp.is_none() && g.drawing_bsp.is_none());
        assert!(g.polygons.is_empty() && g.physics_polygons.is_empty());
        assert_eq!(g.did_degrade, 0);
    }

    #[test]
    fn truncated_gfx_obj_is_an_error_at_every_length() {
        let bytes = full_sample();
        for len in 0..bytes.len() {
            assert!(
                GfxObj::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }
}
