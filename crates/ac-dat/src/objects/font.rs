//! Bitmap fonts: glyph rectangles in a foreground atlas (the glyph pixels)
//! and an optional background atlas (the outline pixels).

use ac_bits::Reader;

use super::DatObject;
use super::read::list;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// The font most UI text uses.
pub const DEFAULT_FONT_ID: u32 = 0x4000_0000;

/// One glyph: where it sits in the atlases and how it advances the pen.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct FontCharDesc {
    /// The character (a UTF-16 unit).
    pub unicode: u16,
    /// Left of the glyph in the atlas, pixels.
    pub offset_x: u16,
    /// Top of the glyph in the atlas, pixels.
    pub offset_y: u16,
    /// Glyph width, pixels.
    pub width: u8,
    /// Glyph height, pixels.
    pub height: u8,
    /// Pen movement before the glyph, pixels.
    pub horizontal_offset_before: i8,
    /// Pen movement after the glyph, pixels.
    pub horizontal_offset_after: i8,
    /// Glyph top below the line top, pixels.
    pub vertical_offset_before: i8,
}

impl FontCharDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            unicode: r.u16()?,
            offset_x: r.u16()?,
            offset_y: r.u16()?,
            width: r.u8()?,
            height: r.u8()?,
            horizontal_offset_before: r.i8()?,
            horizontal_offset_after: r.i8()?,
            vertical_offset_before: r.i8()?,
        })
    }

    /// How far the glyph moves the pen: before + width + after, pixels.
    #[must_use]
    pub fn advance(&self) -> i32 {
        i32::from(self.horizontal_offset_before)
            + i32::from(self.width)
            + i32::from(self.horizontal_offset_after)
    }
}

/// A bitmap font. File id `0x40000000`..`0x40000FFF`, in the portal dat.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Font {
    /// File id.
    pub id: u32,
    /// Tallest glyph, pixels (the line height).
    pub max_char_height: u32,
    /// Widest glyph, pixels.
    pub max_char_width: u32,
    /// Glyphs, in file order.
    pub char_descs: Vec<FontCharDesc>,
    /// Outline width added on each side in the background atlas, pixels.
    pub num_horizontal_border_pixels: u32,
    /// Outline height added above and below in the background atlas.
    pub num_vertical_border_pixels: u32,
    /// Baseline below the line top, pixels.
    pub baseline_offset: u32,
    /// Render surface holding the glyph pixels.
    pub foreground_surface_data_id: u32,
    /// Render surface holding the outline pixels (0 for none).
    pub background_surface_data_id: u32,
}

impl DatObject for Font {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let max_char_height = r.u32()?;
        let max_char_width = r.u32()?;
        let count = u64::from(r.u32()?);
        let char_descs = list(r, count, FontCharDesc::unpack)?;
        Ok(Self {
            id,
            max_char_height,
            max_char_width,
            char_descs,
            num_horizontal_border_pixels: r.u32()?,
            num_vertical_border_pixels: r.u32()?,
            baseline_offset: r.u32()?,
            foreground_surface_data_id: r.u32()?,
            background_surface_data_id: r.u32()?,
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

    #[test]
    fn unpacks_metrics_and_glyphs() {
        let mut w = Writer::new();
        w.u32(DEFAULT_FONT_ID);
        w.u32(14);
        w.u32(12);
        w.u32(1);
        w.u16(u16::from(b'A'));
        w.u16(16);
        w.u16(32);
        w.u8(7);
        w.u8(10);
        w.i8(-1);
        w.i8(2);
        w.i8(3);
        for v in [1u32, 1, 11, 0x0600_5EE5, 0x0600_5EE6] {
            w.u32(v);
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let f = Font::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!((f.max_char_height, f.baseline_offset), (14, 11));
        assert_eq!(
            (f.foreground_surface_data_id, f.background_surface_data_id),
            (0x0600_5EE5, 0x0600_5EE6)
        );
        let g = f.char_descs[0];
        assert_eq!((g.unicode, g.offset_x, g.offset_y), (65, 16, 32));
        assert_eq!(g.advance(), 8);
        assert_eq!(g.vertical_offset_before, 3);
        for len in 0..bytes.len() {
            assert!(Font::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
