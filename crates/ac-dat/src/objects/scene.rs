use ac_bits::Reader;

use super::DatObject;
use super::read::list;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Frame;

type Result<T> = core::result::Result<T, DecodeError>;

/// A scenery recipe: objects scattered over terrain cells with random
/// placement, scale and rotation. File id `0x12......`.
#[derive(Debug, Clone, PartialEq)]
pub struct Scene {
    /// File id.
    pub id: u32,
    /// Objects, in file order.
    pub objects: Vec<ObjectDesc>,
}

impl DatObject for Scene {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            id,
            objects: list(r, count, ObjectDesc::unpack)?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One scenery object and the ranges its placement is drawn from.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ObjectDesc {
    /// Model or setup id.
    pub object_id: u32,
    /// Base position within the cell.
    pub base_loc: Frame,
    /// Chance per cell to place it.
    pub frequency: f32,
    /// Random displacement range, x, metres.
    pub displace_x: f32,
    /// Random displacement range, y, metres.
    pub displace_y: f32,
    /// Smallest scale.
    pub min_scale: f32,
    /// Largest scale.
    pub max_scale: f32,
    /// Largest random rotation, degrees.
    pub max_rotation: f32,
    /// Smallest terrain slope it stands on (z of the normal).
    pub min_slope: f32,
    /// Largest terrain slope it stands on (z of the normal).
    pub max_slope: f32,
    /// Non-zero: align to the terrain slope.
    pub align: i32,
    /// Non-zero: orient to the terrain.
    pub orient: i32,
    /// Server object id, when the scenery is an object.
    pub weenie_obj: u32,
}

impl ObjectDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            object_id: r.u32()?,
            base_loc: Frame::unpack(r)?,
            frequency: r.f32()?,
            displace_x: r.f32()?,
            displace_y: r.f32()?,
            min_scale: r.f32()?,
            max_scale: r.f32()?,
            max_rotation: r.f32()?,
            min_slope: r.f32()?,
            max_slope: r.f32()?,
            align: r.i32()?,
            orient: r.i32()?,
            weenie_obj: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn unpacks_objects_in_order() {
        let mut w = Writer::new();
        w.u32(0x1200_0009);
        w.u32(2);
        for i in 0..2u32 {
            w.u32(0x0200_02D3 + i);
            for v in [1.44f32, -10.92, 0.0, 1.0, 0.0, 0.0, 0.0] {
                w.f32(v);
            }
            for v in [0.73f32, 8.52, 2.16, 0.3, 0.8, 324.1, 0.78, 1.0] {
                w.f32(v);
            }
            w.i32(1);
            w.i32(0);
            w.u32(i);
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let s = Scene::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.objects.len(), 2);
        let o = s.objects[1];
        assert_eq!(o.object_id, 0x0200_02D4);
        assert_eq!(o.base_loc.origin.y, -10.92);
        assert_eq!(o.max_rotation, 324.1);
        assert_eq!((o.align, o.orient, o.weenie_obj), (1, 0, 1));
        for len in 0..bytes.len() {
            assert!(Scene::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
