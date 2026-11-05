use ac_bits::Reader;

use super::{DatObject, check_unique};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// One texture swap: wherever the model uses `old_texture`, use
/// `new_texture` instead.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CloTextureEffect {
    /// Surface texture id replaced.
    pub old_texture: u32,
    /// Surface texture id used instead.
    pub new_texture: u32,
}

/// The model and texture swaps for one part of a setup.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CloObjectEffect {
    /// Part index in the setup.
    pub index: u32,
    /// Model id (`0x01......`) for the part.
    pub model_id: u32,
    /// Texture swaps on that model.
    pub texture_effects: Vec<CloTextureEffect>,
}

impl CloObjectEffect {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let index = r.u32()?;
        let model_id = r.u32()?;
        let count = r.u32()?;
        let mut texture_effects = Vec::new();
        for _ in 0..count {
            texture_effects.push(CloTextureEffect {
                old_texture: r.u32()?,
                new_texture: r.u32()?,
            });
        }
        Ok(Self {
            index,
            model_id,
            texture_effects,
        })
    }
}

/// What a piece of clothing does to one setup: per-part model and texture
/// swaps.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ClothingBaseEffect {
    /// One entry per affected part.
    pub object_effects: Vec<CloObjectEffect>,
}

impl ClothingBaseEffect {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = r.u32()?;
        let mut object_effects = Vec::new();
        for _ in 0..count {
            object_effects.push(CloObjectEffect::unpack(r)?);
        }
        Ok(Self { object_effects })
    }
}

/// A run of palette entries, in units of 8 colours.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CloSubPaletteRange {
    /// First entry, in units of 8 colours.
    pub offset: u32,
    /// Number of entries, in units of 8 colours.
    pub num_colors: u32,
}

/// Palette ranges recoloured from one palette set.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CloSubPalette {
    /// The ranges recoloured.
    pub ranges: Vec<CloSubPaletteRange>,
    /// Palette set id (`0x0F......`) the colours come from.
    pub palette_set: u32,
}

impl CloSubPalette {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = r.u32()?;
        let mut ranges = Vec::new();
        for _ in 0..count {
            ranges.push(CloSubPaletteRange {
                offset: r.u32()?,
                num_colors: r.u32()?,
            });
        }
        Ok(Self {
            ranges,
            palette_set: r.u32()?,
        })
    }
}

/// One colour scheme of a piece of clothing.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CloSubPalEffect {
    /// Icon render surface id (`0x06......`).
    pub icon: u32,
    /// Palette changes.
    pub sub_palettes: Vec<CloSubPalette>,
}

impl CloSubPalEffect {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let icon = r.u32()?;
        let count = r.u32()?;
        let mut sub_palettes = Vec::new();
        for _ in 0..count {
            sub_palettes.push(CloSubPalette::unpack(r)?);
        }
        Ok(Self { icon, sub_palettes })
    }
}

/// How a piece of clothing changes a character: model and texture swaps
/// per setup, and colour schemes. File id `0x10......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClothingTable {
    /// File id.
    pub id: u32,
    /// Effects keyed by setup id, in file order.
    pub base_effects: Vec<(u32, ClothingBaseEffect)>,
    /// Bucket count of the stored base-effect hash table.
    pub base_effects_buckets: u16,
    /// Colour schemes keyed by palette template, in file order.
    pub sub_pal_effects: Vec<(u32, CloSubPalEffect)>,
    /// Bucket count of the stored colour-scheme hash table.
    pub sub_pal_effects_buckets: u16,
}

impl DatObject for ClothingTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = r.u16()?;
        let base_effects_buckets = r.u16()?;
        let mut base_effects = Vec::new();
        for _ in 0..count {
            let key = r.u32()?;
            let effect = ClothingBaseEffect::unpack(r)?;
            check_unique("clothing base effects", &base_effects, key)?;
            base_effects.push((key, effect));
        }
        let count = r.u16()?;
        let sub_pal_effects_buckets = r.u16()?;
        let mut sub_pal_effects = Vec::new();
        for _ in 0..count {
            let key = r.u32()?;
            let effect = CloSubPalEffect::unpack(r)?;
            check_unique("clothing palette effects", &sub_pal_effects, key)?;
            sub_pal_effects.push((key, effect));
        }
        Ok(Self {
            id,
            base_effects,
            base_effects_buckets,
            sub_pal_effects,
            sub_pal_effects_buckets,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x1000_0001);
        // Base effects: one setup, one part with one texture swap.
        w.u16(1);
        w.u16(8);
        w.u32(0x0200_0001);
        w.u32(1);
        w.u32(9);
        w.u32(0x0100_0050);
        w.u32(1);
        w.u32(0x0500_0001);
        w.u32(0x0500_0002);
        // Palette effects: one scheme with one sub-palette of two ranges.
        w.u16(1);
        w.u16(32);
        w.u32(3);
        w.u32(0x0600_1000);
        w.u32(1);
        w.u32(2);
        w.u32(0);
        w.u32(24);
        w.u32(24);
        w.u32(8);
        w.u32(0x0F00_0001);
        w.into_vec()
    }

    #[test]
    fn unpacks_both_tables() {
        let bytes = sample();
        let mut r = Reader::new(&bytes);
        let c = ClothingTable::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!((c.base_effects_buckets, c.sub_pal_effects_buckets), (8, 32));
        let (setup, effect) = &c.base_effects[0];
        assert_eq!(*setup, 0x0200_0001);
        let o = &effect.object_effects[0];
        assert_eq!((o.index, o.model_id), (9, 0x0100_0050));
        assert_eq!(o.texture_effects[0].new_texture, 0x0500_0002);
        let (key, scheme) = &c.sub_pal_effects[0];
        assert_eq!((*key, scheme.icon), (3, 0x0600_1000));
        let sp = &scheme.sub_palettes[0];
        assert_eq!(sp.palette_set, 0x0F00_0001);
        assert_eq!(
            sp.ranges[1],
            CloSubPaletteRange {
                offset: 24,
                num_colors: 8
            }
        );
    }

    #[test]
    fn truncated_table_is_an_error_at_every_length() {
        let bytes = sample();
        for len in 0..bytes.len() {
            assert!(
                ClothingTable::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }
}
