//! The appearance block shared by the create-object, update-object and
//! appearance-change messages: palette swaps, texture swaps and part swaps
//! applied on top of an object's base model.

use ac_bits::Reader;

use crate::Result;

/// Dat id type of a palette.
pub const PALETTE_ID_TYPE: u32 = 0x0400_0000;
/// Dat id type of a surface texture.
pub const SURFACE_TEXTURE_ID_TYPE: u32 = 0x0500_0000;
/// Dat id type of a visual model part.
pub const GFX_OBJ_ID_TYPE: u32 = 0x0100_0000;

/// A palette range replaced by part of another palette.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SubPalette {
    /// Palette the colors come from.
    pub sub_palette_id: u32,
    /// First color replaced, in units of 8 colors.
    pub offset: u8,
    /// Number of colors replaced, in units of 8 colors.
    pub length: u8,
}

/// One texture on one model part replaced by another.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextureChange {
    /// Index of the part in the object's setup.
    pub part_index: u8,
    /// Texture to replace.
    pub old_texture: u32,
    /// Texture to use instead.
    pub new_texture: u32,
}

/// One model part replaced by another visual model.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AnimPartChange {
    /// Index of the part in the object's setup.
    pub part_index: u8,
    /// Visual model to draw for that part.
    pub new_model_id: u32,
}

/// The appearance block.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModelData {
    /// Base palette; present only when there are palette swaps.
    pub base_palette_id: Option<u32>,
    /// Palette swaps, in wire order.
    pub sub_palettes: Vec<SubPalette>,
    /// Texture swaps, in wire order.
    pub texture_changes: Vec<TextureChange>,
    /// Part swaps, in wire order.
    pub anim_part_changes: Vec<AnimPartChange>,
}

impl ModelData {
    /// Read the block: a marker byte (not checked) and three counts, the
    /// base palette when any palette swap follows, the three lists, then
    /// padding to a 4-byte boundary (measured from the reader's start, which
    /// must be the message start).
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let _marker = r.u8()?;
        let sub_palette_count = r.u8()?;
        let texture_change_count = r.u8()?;
        let anim_part_change_count = r.u8()?;

        let base_palette_id = if sub_palette_count > 0 {
            Some(r.packed_dword_of_known_type(PALETTE_ID_TYPE)?)
        } else {
            None
        };

        let mut sub_palettes = Vec::with_capacity(usize::from(sub_palette_count));
        for _ in 0..sub_palette_count {
            sub_palettes.push(SubPalette {
                sub_palette_id: r.packed_dword_of_known_type(PALETTE_ID_TYPE)?,
                offset: r.u8()?,
                length: r.u8()?,
            });
        }

        let mut texture_changes = Vec::with_capacity(usize::from(texture_change_count));
        for _ in 0..texture_change_count {
            texture_changes.push(TextureChange {
                part_index: r.u8()?,
                old_texture: r.packed_dword_of_known_type(SURFACE_TEXTURE_ID_TYPE)?,
                new_texture: r.packed_dword_of_known_type(SURFACE_TEXTURE_ID_TYPE)?,
            });
        }

        let mut anim_part_changes = Vec::with_capacity(usize::from(anim_part_change_count));
        for _ in 0..anim_part_change_count {
            anim_part_changes.push(AnimPartChange {
                part_index: r.u8()?,
                new_model_id: r.packed_dword_of_known_type(GFX_OBJ_ID_TYPE)?,
            });
        }

        r.align(4)?;
        Ok(Self {
            base_palette_id,
            sub_palettes,
            texture_changes,
            anim_part_changes,
        })
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;

    /// Write `value` the way the server writes a dat id of a known type:
    /// the type byte stripped when it matches, then packed.
    pub fn packed_of_known_type(w: &mut Writer, value: u32, known_type: u32) {
        let packed = if value & 0xFF00_0000 == known_type {
            value & !known_type
        } else {
            value
        };
        w.packed_dword(packed).unwrap();
    }

    /// The empty block: marker and three zero counts.
    pub const EMPTY: [u8; 4] = [0x11, 0, 0, 0];

    fn read(bytes: &[u8]) -> Result<ModelData> {
        ModelData::read(&mut Reader::new(bytes))
    }

    #[test]
    fn bare_block_reads_base_palette_and_swap() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/ObjDescEventTests.cs
        // :: ReadModelData_SameOutputFromBothCallers.
        let mut w = Writer::new();
        w.bytes(&[0x11, 1, 0, 0]);
        packed_of_known_type(&mut w, 0x0400_007E, PALETTE_ID_TYPE);
        packed_of_known_type(&mut w, 0x0400_1084, PALETTE_ID_TYPE);
        w.bytes(&[80, 12]);
        w.align(4);
        let md = read(w.as_slice()).unwrap();
        assert_eq!(md.base_palette_id, Some(0x0400_007E));
        assert_eq!(
            md.sub_palettes,
            [SubPalette {
                sub_palette_id: 0x0400_1084,
                offset: 80,
                length: 12
            }]
        );
        assert!(md.texture_changes.is_empty());
        assert!(md.anim_part_changes.is_empty());
        assert_every_prefix_fails(w.as_slice(), read);
    }

    #[test]
    fn part_swaps_take_the_model_type_and_zero_stays_zero() {
        let mut w = Writer::new();
        w.bytes(&[0x11, 0, 1, 2]);
        w.u8(4);
        packed_of_known_type(&mut w, 0x0500_0100, SURFACE_TEXTURE_ID_TYPE);
        packed_of_known_type(&mut w, 0, SURFACE_TEXTURE_ID_TYPE);
        w.u8(9);
        packed_of_known_type(&mut w, 0x0100_1234, GFX_OBJ_ID_TYPE);
        w.u8(10);
        packed_of_known_type(&mut w, 0x0101_5678, GFX_OBJ_ID_TYPE);
        w.align(4);
        let md = read(w.as_slice()).unwrap();
        assert_eq!(md.base_palette_id, None);
        assert_eq!(
            md.texture_changes,
            [TextureChange {
                part_index: 4,
                old_texture: 0x0500_0100,
                new_texture: 0
            }]
        );
        assert_eq!(
            md.anim_part_changes,
            [
                AnimPartChange {
                    part_index: 9,
                    new_model_id: 0x0100_1234
                },
                AnimPartChange {
                    part_index: 10,
                    new_model_id: 0x0101_5678
                }
            ]
        );
        assert_every_prefix_fails(w.as_slice(), read);
    }

    #[test]
    fn empty_block_is_four_bytes() {
        let mut r = Reader::new(&EMPTY);
        assert_eq!(ModelData::read(&mut r), Ok(ModelData::default()));
        assert_eq!(r.position(), 4);
    }
}
