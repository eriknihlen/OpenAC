//! Small value types shared by several dat objects.

use ac_bits::Reader;

use crate::error::DecodeError;

type Result<T> = core::result::Result<T, DecodeError>;

/// Read a 4-byte boolean (any non-zero value is true).
pub(crate) fn read_bool32(r: &mut Reader<'_>) -> Result<bool> {
    Ok(r.u32()? != 0)
}

/// A 3-component vector of `f32`, stored x, y, z.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Vector3 {
    /// X.
    pub x: f32,
    /// Y.
    pub y: f32,
    /// Z.
    pub z: f32,
}

impl Vector3 {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            x: r.f32()?,
            y: r.f32()?,
            z: r.f32()?,
        })
    }
}

/// A rotation quaternion, stored w first, then x, y, z.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Quaternion {
    /// Scalar part.
    pub w: f32,
    /// X.
    pub x: f32,
    /// Y.
    pub y: f32,
    /// Z.
    pub z: f32,
}

impl Quaternion {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            w: r.f32()?,
            x: r.f32()?,
            y: r.f32()?,
            z: r.f32()?,
        })
    }
}

/// A position and orientation (origin, then rotation).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Frame {
    /// Position.
    pub origin: Vector3,
    /// Rotation.
    pub orientation: Quaternion,
}

impl Frame {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            origin: Vector3::unpack(r)?,
            orientation: Quaternion::unpack(r)?,
        })
    }
}

/// A sphere: centre and radius.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Sphere {
    /// Centre.
    pub origin: Vector3,
    /// Radius.
    pub radius: f32,
}

impl Sphere {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            origin: Vector3::unpack(r)?,
            radius: r.f32()?,
        })
    }
}

/// An upright cylinder: base centre, radius and height.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CylSphere {
    /// Base centre.
    pub origin: Vector3,
    /// Radius.
    pub radius: f32,
    /// Height.
    pub height: f32,
}

impl CylSphere {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            origin: Vector3::unpack(r)?,
            radius: r.f32()?,
            height: r.f32()?,
        })
    }
}

/// A colour stored blue, green, red, alpha.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ColorArgb {
    /// Blue.
    pub blue: u8,
    /// Green.
    pub green: u8,
    /// Red.
    pub red: u8,
    /// Alpha.
    pub alpha: u8,
}

impl ColorArgb {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            blue: r.u8()?,
            green: r.u8()?,
            red: r.u8()?,
            alpha: r.u8()?,
        })
    }
}

/// A part index and a frame relative to it (a holding or connection point).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct LocationType {
    /// Part the location hangs off.
    pub part_id: i32,
    /// Offset from that part.
    pub frame: Frame,
}

impl LocationType {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            part_id: r.i32()?,
            frame: Frame::unpack(r)?,
        })
    }
}

/// A light attached to an object.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct LightInfo {
    /// Position and direction relative to the object.
    pub view_space_location: Frame,
    /// Colour.
    pub color: ColorArgb,
    /// Intensity.
    pub intensity: f32,
    /// Falloff distance.
    pub falloff: f32,
    /// Cone angle (unused fill pattern for point lights).
    pub cone_angle: f32,
}

impl LightInfo {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            view_space_location: Frame::unpack(r)?,
            color: ColorArgb::unpack(r)?,
            intensity: r.f32()?,
            falloff: r.f32()?,
            cone_angle: r.f32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn quaternion_is_stored_w_first() {
        let mut w = Writer::new();
        for v in [1.0f32, 2.0, 3.0, 4.0] {
            w.f32(v);
        }
        let q = Quaternion::unpack(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(
            q,
            Quaternion {
                w: 1.0,
                x: 2.0,
                y: 3.0,
                z: 4.0
            }
        );
    }

    #[test]
    fn color_is_stored_blue_first() {
        let c = ColorArgb::unpack(&mut Reader::new(&[1, 2, 3, 4])).unwrap();
        assert_eq!(
            c,
            ColorArgb {
                blue: 1,
                green: 2,
                red: 3,
                alpha: 4
            }
        );
    }

    #[test]
    fn short_frame_is_an_error() {
        assert!(Frame::unpack(&mut Reader::new(&[0; 27])).is_err());
        assert!(Frame::unpack(&mut Reader::new(&[0; 28])).is_ok());
    }
}
