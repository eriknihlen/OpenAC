use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::read_bool32;

/// Number of terrain vertices on a side of a landblock.
pub const VERTICES_PER_SIDE: usize = 9;

/// Number of terrain vertices in a landblock (9 x 9).
pub const VERTEX_COUNT: usize = VERTICES_PER_SIDE * VERTICES_PER_SIDE;

/// The outdoor terrain of one landblock (a 192 m square): a 9 x 9 grid of
/// vertices, each with a terrain word and a height index.
///
/// File id `0xXXYYFFFF`, where `XX` and `YY` are the landblock's grid
/// coordinates. Arrays are indexed `x * 9 + y` for the vertex at grid
/// position (x, y).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LandBlock {
    /// File id.
    pub id: u32,
    /// Whether a matching [`super::LandBlockInfo`] (`0xXXYYFFFE`) exists.
    pub has_objects: bool,
    /// Terrain word per vertex.
    pub terrain: [TerrainInfo; VERTEX_COUNT],
    /// Height index per vertex (into the region's height table).
    pub height: [u8; VERTEX_COUNT],
}

impl DatObject for LandBlock {
    const DAT: DatFileType = DatFileType::Cell;

    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let id = r.u32()?;
        let has_objects = read_bool32(r)?;
        let mut terrain = [TerrainInfo(0); VERTEX_COUNT];
        for t in &mut terrain {
            *t = TerrainInfo(r.u16()?);
        }
        let height = r.array::<VERTEX_COUNT>()?;
        r.align(4)?;
        Ok(Self {
            id,
            has_objects,
            terrain,
            height,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A terrain vertex word: road bits, terrain type and scenery index.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TerrainInfo(pub u16);

impl TerrainInfo {
    /// Bits 0-1: road.
    #[must_use]
    pub fn road(self) -> u8 {
        (self.0 & 0x3) as u8
    }

    /// Bits 2-6: terrain texture type.
    #[must_use]
    pub fn terrain_type(self) -> u8 {
        ((self.0 & 0x7C) >> 2) as u8
    }

    /// Bits 11-15: scenery index.
    #[must_use]
    pub fn scenery(self) -> u8 {
        ((self.0 & 0xF800) >> 11) as u8
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xA9B4_FFFF);
        w.u32(1);
        for i in 0..VERTEX_COUNT as u16 {
            w.u16(0x7804 + i);
        }
        for i in 0..VERTEX_COUNT as u8 {
            w.u8(i * 2);
        }
        w.align(4);
        w.into_vec()
    }

    #[test]
    fn unpacks_terrain_and_heights() {
        let bytes = sample();
        assert_eq!(bytes.len(), 252);
        let mut r = Reader::new(&bytes);
        let lb = LandBlock::unpack(&mut r).unwrap();
        assert_eq!(r.position(), 252);
        assert_eq!(lb.id, 0xA9B4_FFFF);
        assert!(lb.has_objects);
        assert_eq!(lb.terrain[0], TerrainInfo(0x7804));
        assert_eq!(lb.terrain[80], TerrainInfo(0x7804 + 80));
        assert_eq!(lb.height[0], 0);
        assert_eq!(lb.height[80], 160);
    }

    #[test]
    fn truncated_landblock_is_an_error() {
        let bytes = sample();
        for len in [0, 4, 8, 100, 170, 250] {
            assert!(
                LandBlock::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn terrain_word_fields() {
        // 0x7804: scenery 15, type 1, no road.
        let t = TerrainInfo(0x7804);
        assert_eq!((t.road(), t.terrain_type(), t.scenery()), (0, 1, 15));
        let t = TerrainInfo(0x0038);
        assert_eq!((t.road(), t.terrain_type(), t.scenery()), (0, 14, 0));
        let t = TerrainInfo(0x0003 | 0x7C | 0xF800);
        assert_eq!((t.road(), t.terrain_type(), t.scenery()), (3, 31, 31));
    }
}
