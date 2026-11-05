//! Mesh building blocks shared by models (`GfxObj`) and interior cell
//! shapes (`CellStruct`): vertices, texture coordinates and polygons.

use ac_bits::Reader;

use super::read::{self, keyed};
use crate::error::DecodeError;
use crate::types::Vector3;

type Result<T> = core::result::Result<T, DecodeError>;

/// A texture coordinate pair.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Uv {
    /// Horizontal coordinate.
    pub u: f32,
    /// Vertical coordinate.
    pub v: f32,
}

/// A vertex: position, normal and one or more texture coordinates.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Vertex {
    /// Position, metres, in the owner's frame.
    pub origin: Vector3,
    /// Unit normal.
    pub normal: Vector3,
    /// Texture coordinates; polygons pick one by index.
    pub uvs: Vec<Uv>,
}

impl Vertex {
    /// Read a vertex (after its id).
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let num_uvs = r.u16()?;
        let origin = Vector3::unpack(r)?;
        let normal = Vector3::unpack(r)?;
        let mut uvs = Vec::new();
        for _ in 0..num_uvs {
            uvs.push(Uv {
                u: r.f32()?,
                v: r.f32()?,
            });
        }
        Ok(Self {
            origin,
            normal,
            uvs,
        })
    }
}

/// A mesh's vertices.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct VertexArray {
    /// Vertex layout; 1 is the only layout stored.
    pub vertex_type: i32,
    /// Vertices keyed by id, in file order.
    pub vertices: Vec<(u16, Vertex)>,
}

impl VertexArray {
    /// The only vertex layout: position, normal, texture coordinates.
    pub const SW_VERTEX: i32 = 1;

    /// Read a vertex array.
    pub fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let vertex_type = r.i32()?;
        let count = r.u32()?;
        if vertex_type != Self::SW_VERTEX && count != 0 {
            return Err(DecodeError::UnknownVertexType(vertex_type));
        }
        let vertices = keyed(r, "vertices", u64::from(count), read::u16, Vertex::unpack)?;
        Ok(Self {
            vertex_type,
            vertices,
        })
    }
}

/// Which faces of a polygon have texture coordinates stored.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Stippling(pub u8);

impl Stippling {
    /// No stippling.
    pub const NONE: u8 = 0x00;
    /// The positive face is stippled.
    pub const POSITIVE: u8 = 0x01;
    /// The negative face is stippled.
    pub const NEGATIVE: u8 = 0x02;
    /// Both faces are stippled.
    pub const BOTH: u8 = 0x03;
    /// No texture coordinates for the positive face.
    pub const NO_POS: u8 = 0x04;
    /// No texture coordinates for the negative face.
    pub const NO_NEG: u8 = 0x08;
    /// No texture coordinates at all.
    pub const NO_UVS: u8 = 0x14;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u8) -> bool {
        self.0 & bits == bits
    }
}

/// Which faces of a polygon are drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SidesType(pub i32);

impl SidesType {
    /// Landblock (terrain) culling.
    pub const LANDBLOCK: i32 = 0;
    /// Both faces drawn with the positive face's surface.
    pub const NONE: i32 = 1;
    /// Both faces drawn, each with its own surface and texture
    /// coordinates.
    pub const CLOCKWISE: i32 = 2;
    /// Counter-clockwise culling.
    pub const COUNTER_CLOCKWISE: i32 = 3;
}

/// A polygon of a model or cell shape: vertex ids plus the surface and
/// texture coordinates of each face.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Polygon {
    /// Which faces have texture coordinates stored.
    pub stippling: Stippling,
    /// Which faces are drawn.
    pub sides_type: SidesType,
    /// Surface index (into the owner's surface list) of the positive face.
    pub pos_surface: i16,
    /// Surface index of the negative face; -1 for none.
    pub neg_surface: i16,
    /// Vertex ids, in winding order.
    pub vertex_ids: Vec<u16>,
    /// Texture coordinate index per vertex for the positive face; empty
    /// with [`Stippling::NO_POS`].
    pub pos_uv_indices: Vec<u8>,
    /// Texture coordinate index per vertex for the negative face; stored
    /// only for [`SidesType::CLOCKWISE`] polygons without
    /// [`Stippling::NO_NEG`].
    pub neg_uv_indices: Vec<u8>,
}

impl Polygon {
    /// Read a polygon (after its id).
    pub fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let num_vertices = r.u8()?;
        let stippling = Stippling(r.u8()?);
        let sides_type = SidesType(r.i32()?);
        let pos_surface = r.i16()?;
        let neg_surface = r.i16()?;
        let mut vertex_ids = Vec::new();
        for _ in 0..num_vertices {
            vertex_ids.push(r.u16()?);
        }
        let mut pos_uv_indices = Vec::new();
        if !stippling.contains(Stippling::NO_POS) {
            pos_uv_indices = r.bytes(usize::from(num_vertices))?.to_vec();
        }
        let mut neg_uv_indices = Vec::new();
        if sides_type.0 == SidesType::CLOCKWISE && !stippling.contains(Stippling::NO_NEG) {
            neg_uv_indices = r.bytes(usize::from(num_vertices))?.to_vec();
        }
        Ok(Self {
            stippling,
            sides_type,
            pos_surface,
            neg_surface,
            vertex_ids,
            pos_uv_indices,
            neg_uv_indices,
        })
    }
}

/// Read a table of `count` polygons keyed by a `u16` id, in file order; a
/// repeated id is an error.
pub(crate) fn polygon_table(
    r: &mut Reader<'_>,
    table: &'static str,
    count: u32,
) -> Result<Vec<(u16, Polygon)>> {
    keyed(r, table, u64::from(count), read::u16, Polygon::unpack)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use ac_bits::Writer;

    /// A vertex with id `id` and the given texture coordinates.
    pub(crate) fn vertex(w: &mut Writer, id: u16, uvs: &[(f32, f32)]) {
        w.u16(id);
        w.u16(u16::try_from(uvs.len()).unwrap());
        for v in [f32::from(id), 2.0, 3.0, 0.0, 0.0, 1.0] {
            w.f32(v);
        }
        for &(u, v) in uvs {
            w.f32(u);
            w.f32(v);
        }
    }

    /// A vertex array of `n` vertices with one UV each.
    pub(crate) fn vertex_array(w: &mut Writer, n: u16) {
        w.i32(1);
        w.u32(u32::from(n));
        for i in 0..n {
            vertex(w, i, &[(0.25, 0.75)]);
        }
    }

    /// A polygon with `n` vertices (after its id): positive surface 3, no
    /// negative surface, vertex ids `0..n`, positive UV indices `0..n` and
    /// negative ones `n..1`.
    pub(crate) fn polygon(w: &mut Writer, n: u8, stippling: u8, sides_type: i32) {
        w.u8(n);
        w.u8(stippling);
        w.i32(sides_type);
        w.i16(3);
        w.i16(-1);
        for i in 0..n {
            w.u16(u16::from(i));
        }
        if stippling & Stippling::NO_POS == 0 {
            for i in 0..n {
                w.u8(i);
            }
        }
        if sides_type == SidesType::CLOCKWISE && stippling & Stippling::NO_NEG == 0 {
            for i in 0..n {
                w.u8(n - i);
            }
        }
    }

    #[test]
    fn uv_indices_follow_stippling_and_sides() {
        for (stippling, sides, pos, neg) in [
            (Stippling::NONE, SidesType::LANDBLOCK, 4, 0),
            (Stippling::NONE, SidesType::NONE, 4, 0),
            (Stippling::NONE, SidesType::CLOCKWISE, 4, 4),
            (Stippling::NO_POS, SidesType::NONE, 0, 0),
            (Stippling::NO_NEG, SidesType::CLOCKWISE, 4, 0),
            (Stippling::NO_POS, SidesType::CLOCKWISE, 0, 4),
            (
                Stippling::NO_POS | Stippling::NO_NEG,
                SidesType::CLOCKWISE,
                0,
                0,
            ),
            (Stippling::NO_UVS, SidesType::NONE, 0, 0),
        ] {
            let mut w = Writer::new();
            polygon(&mut w, 4, stippling, sides);
            let bytes = w.into_vec();
            let mut r = Reader::new(&bytes);
            let p = Polygon::unpack(&mut r).unwrap();
            assert_eq!(r.remaining(), 0);
            assert_eq!(p.vertex_ids, vec![0, 1, 2, 3]);
            assert_eq!((p.pos_surface, p.neg_surface), (3, -1));
            assert_eq!(
                (p.pos_uv_indices.len(), p.neg_uv_indices.len()),
                (pos, neg),
                "stippling {stippling:#x} sides {sides}"
            );
            if neg > 0 {
                assert_eq!(p.neg_uv_indices, vec![4, 3, 2, 1]);
            }
            for len in 0..bytes.len() {
                assert!(Polygon::unpack(&mut Reader::new(&bytes[..len])).is_err());
            }
        }
    }

    #[test]
    fn vertex_array_reads_keyed_vertices() {
        let mut w = Writer::new();
        vertex_array(&mut w, 2);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let a = VertexArray::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(a.vertex_type, VertexArray::SW_VERTEX);
        assert_eq!(a.vertices[1].0, 1);
        assert_eq!(a.vertices[1].1.origin.x, 1.0);
        assert_eq!(a.vertices[1].1.normal.z, 1.0);
        assert_eq!(a.vertices[1].1.uvs, vec![Uv { u: 0.25, v: 0.75 }]);
        for len in 0..bytes.len() {
            assert!(VertexArray::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn duplicate_vertex_id_is_an_error() {
        let mut w = Writer::new();
        w.i32(1);
        w.u32(2);
        vertex(&mut w, 7, &[]);
        vertex(&mut w, 7, &[]);
        let bytes = w.into_vec();
        assert_eq!(
            VertexArray::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::DuplicateKey {
                table: "vertices",
                key: 7
            })
        );
    }

    #[test]
    fn unknown_vertex_type_with_vertices_is_an_error() {
        let mut w = Writer::new();
        w.i32(2);
        w.u32(1);
        vertex(&mut w, 0, &[]);
        let bytes = w.into_vec();
        assert_eq!(
            VertexArray::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::UnknownVertexType(2))
        );
        let empty = [2u8, 0, 0, 0, 0, 0, 0, 0];
        assert!(VertexArray::unpack(&mut Reader::new(&empty)).is_ok());
    }
}
