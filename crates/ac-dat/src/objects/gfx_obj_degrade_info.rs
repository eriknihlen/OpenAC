use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// One level of detail: the model to draw and the distances it is used at.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct GfxObjInfo {
    /// Model id (`0x01......`).
    pub id: u32,
    /// How the level is chosen.
    pub degrade_mode: u32,
    /// Nearest distance, metres.
    pub min_dist: f32,
    /// Ideal distance, metres.
    pub ideal_dist: f32,
    /// Farthest distance, metres.
    pub max_dist: f32,
}

/// A model's levels of detail, most detailed first. File id `0x11......`.
#[derive(Debug, Clone, PartialEq)]
pub struct GfxObjDegradeInfo {
    /// File id.
    pub id: u32,
    /// Levels of detail.
    pub degrades: Vec<GfxObjInfo>,
}

impl DatObject for GfxObjDegradeInfo {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = r.u32()?;
        let mut degrades = Vec::new();
        for _ in 0..count {
            degrades.push(GfxObjInfo {
                id: r.u32()?,
                degrade_mode: r.u32()?,
                min_dist: r.f32()?,
                ideal_dist: r.f32()?,
                max_dist: r.f32()?,
            });
        }
        Ok(Self { id, degrades })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn degrades() {
        let mut w = Writer::new();
        w.u32(0x1100_0001);
        w.u32(2);
        for (id, max) in [(0x0100_0001u32, 25.0f32), (0x0100_0002, 100.0)] {
            w.u32(id);
            w.u32(1);
            w.f32(0.0);
            w.f32(10.0);
            w.f32(max);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let d = GfxObjDegradeInfo::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(d.degrades.len(), 2);
        assert_eq!(d.degrades[1].id, 0x0100_0002);
        assert_eq!(d.degrades[1].max_dist, 100.0);
        for len in 0..bytes.len() {
            assert!(GfxObjDegradeInfo::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
