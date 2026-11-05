//! Plain geometry values as they travel on the wire. No math here; the
//! runtime converts them into its own types.

use ac_bits::Reader;

use crate::Result;

/// Three little-endian `f32`s.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Vector3 {
    /// East–west, meters.
    pub x: f32,
    /// North–south, meters.
    pub y: f32,
    /// Up–down, meters.
    pub z: f32,
}

impl Vector3 {
    /// Read x, y, z.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            x: r.f32()?,
            y: r.f32()?,
            z: r.f32()?,
        })
    }
}

/// A rotation, stored on the wire as w, x, y, z.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Quaternion {
    /// Scalar part.
    pub w: f32,
    /// Vector part, x.
    pub x: f32,
    /// Vector part, y.
    pub y: f32,
    /// Vector part, z.
    pub z: f32,
}

impl Default for Quaternion {
    fn default() -> Self {
        Self::IDENTITY
    }
}

impl Quaternion {
    /// No rotation.
    pub const IDENTITY: Self = Self {
        w: 1.0,
        x: 0.0,
        y: 0.0,
        z: 0.0,
    };

    /// Read w, x, y, z.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            w: r.f32()?,
            x: r.f32()?,
            y: r.f32()?,
            z: r.f32()?,
        })
    }
}

/// A place in the world: the cell it is in (landblock in the high 16 bits,
/// cell in the low 16), the offset inside that landblock, and the facing.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Position {
    /// Full cell id.
    pub cell_id: u32,
    /// Offset from the landblock's corner, meters.
    pub origin: Vector3,
    /// Facing.
    pub orientation: Quaternion,
}

impl Position {
    /// Read the full form: cell, origin, then w, x, y, z.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            cell_id: r.u32()?,
            origin: Vector3::read(r)?,
            orientation: Quaternion::read(r)?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn full_position_reads_cell_origin_then_wxyz() {
        // Layout from OpenAC PrivateUpdatePosition: cell, x, y, z, qw, qx, qy, qz.
        let mut w = Writer::new();
        w.u32(0xA9B4_0031);
        for v in [84.0f32, 132.5, 12.0, 0.5, 0.0, 0.0, -0.5] {
            w.f32(v);
        }
        let p = Position::read(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(p.cell_id, 0xA9B4_0031);
        assert_eq!(
            p.origin,
            Vector3 {
                x: 84.0,
                y: 132.5,
                z: 12.0
            }
        );
        assert_eq!(
            p.orientation,
            Quaternion {
                w: 0.5,
                x: 0.0,
                y: 0.0,
                z: -0.5
            }
        );
        assert!(Position::read(&mut Reader::new(&w.as_slice()[..27])).is_err());
    }
}
