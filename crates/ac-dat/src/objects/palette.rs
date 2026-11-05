use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::ColorArgb;

type Result<T> = core::result::Result<T, DecodeError>;

/// A colour table for paletted textures. File id `0x04......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Palette {
    /// File id.
    pub id: u32,
    /// Colours, indexed by the texture's pixel values.
    pub colors: Vec<ColorArgb>,
}

impl DatObject for Palette {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = r.i32()?;
        let mut colors = Vec::new();
        for _ in 0..count {
            colors.push(ColorArgb::unpack(r)?);
        }
        Ok(Self { id, colors })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A list of palettes to choose from (for example, the shades a piece of
/// clothing can be dyed). File id `0x0F......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PaletteSet {
    /// File id.
    pub id: u32,
    /// Palette ids (`0x04......`).
    pub palettes: Vec<u32>,
}

impl DatObject for PaletteSet {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = r.u32()?;
        let mut palettes = Vec::new();
        for _ in 0..count {
            palettes.push(r.u32()?);
        }
        Ok(Self { id, palettes })
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
    fn palette() {
        let mut w = Writer::new();
        w.u32(0x0400_0001);
        w.i32(2);
        w.bytes(&[1, 2, 3, 4, 5, 6, 7, 8]);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let p = Palette::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(p.colors.len(), 2);
        assert_eq!((p.colors[1].blue, p.colors[1].alpha), (5, 8));
        for len in 0..bytes.len() {
            assert!(Palette::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn palette_set() {
        let mut w = Writer::new();
        w.u32(0x0F00_0001);
        w.u32(2);
        w.u32(0x0400_0010);
        w.u32(0x0400_0011);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let s = PaletteSet::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.palettes, vec![0x0400_0010, 0x0400_0011]);
        for len in 0..bytes.len() {
            assert!(PaletteSet::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
