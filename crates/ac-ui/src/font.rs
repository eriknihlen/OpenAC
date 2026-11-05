//! Dat bitmap fonts as the UI uses them: glyph lookup, measuring, and the
//! atlas textures the draw list names.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{Font, FontCharDesc};

use crate::draw::TextureRef;

/// One font atlas: the render surface and its size in pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Atlas {
    /// The texture (a render surface id).
    pub texture: TextureRef,
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
}

/// A dat font ready for layout and drawing.
#[derive(Debug, Clone, PartialEq)]
pub struct UiFont {
    /// Font id (`0x40......`).
    pub id: u32,
    /// Glyph pixels.
    pub foreground: Atlas,
    /// Outline pixels; `None` when the font has no background atlas (the
    /// outline is then drawn from the foreground at eight offsets).
    pub background: Option<Atlas>,
    /// Line height in pixels (the tallest glyph).
    pub line_height: f32,
    /// Baseline below the line top, pixels.
    pub baseline_offset: f32,
    /// Outline width added left and right in the background atlas.
    pub border_x: i32,
    /// Outline height added above and below in the background atlas.
    pub border_y: i32,
    glyphs: HashMap<char, FontCharDesc>,
    /// The glyph a printable character without one draws as (the bundled
    /// font's `?`); dat fonts have none and skip such characters.
    fallback_character: Option<char>,
}

/// A font baked again at a multiple of its size. Its glyphs are drawn in the
/// font's place on a high-density display; every measurement, and so all
/// layout, still comes from the font itself.
#[derive(Debug, Clone)]
pub struct SharpFont {
    /// The sharper bake.
    pub font: Arc<UiFont>,
    /// How many times the font's size it was baked at.
    pub scale: f32,
}

/// The sharper bakes for the display in use, by the id of the font each one
/// draws for.
pub type SharpFonts = HashMap<u32, SharpFont>;

/// The default UI font id.
pub const DEFAULT_FONT_ID: u32 = ac_dat::objects::DEFAULT_FONT_ID;

impl UiFont {
    /// A font from its parts (tests and synthetic fonts). A later glyph for
    /// the same character replaces an earlier one.
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn new(
        id: u32,
        foreground: Atlas,
        background: Option<Atlas>,
        line_height: f32,
        baseline_offset: f32,
        glyphs: impl IntoIterator<Item = FontCharDesc>,
        border_x: i32,
        border_y: i32,
    ) -> Self {
        let mut map = HashMap::new();
        for g in glyphs {
            // A UTF-16 surrogate has no char of its own; no text can name it.
            if let Some(c) = char::from_u32(u32::from(g.unicode)) {
                map.insert(c, g);
            }
        }
        Self {
            id,
            foreground,
            background,
            line_height,
            baseline_offset,
            border_x,
            border_y,
            glyphs: map,
            fallback_character: None,
        }
    }

    /// The same font, drawing printable characters it has no glyph for as
    /// `fallback` (OpenAC `UiDatFont`'s `fallbackCharacter`).
    #[must_use]
    pub fn with_fallback(mut self, fallback: char) -> Self {
        self.fallback_character = Some(fallback);
        self
    }

    /// Build from a dat font and the sizes of its atlases. `None` when the
    /// font has no foreground atlas or its size is unknown.
    #[must_use]
    pub fn from_dat(font: &Font, atlas_size: impl Fn(u32) -> Option<(u32, u32)>) -> Option<Self> {
        if font.foreground_surface_data_id == 0 {
            return None;
        }
        let (fw, fh) = atlas_size(font.foreground_surface_data_id)?;
        let foreground = Atlas {
            texture: TextureRef::Surface(font.foreground_surface_data_id),
            width: fw,
            height: fh,
        };
        let background = if font.background_surface_data_id != 0 {
            atlas_size(font.background_surface_data_id).map(|(w, h)| Atlas {
                texture: TextureRef::Surface(font.background_surface_data_id),
                width: w,
                height: h,
            })
        } else {
            None
        };
        Some(Self::new(
            font.id,
            foreground,
            background,
            font.max_char_height as f32,
            font.baseline_offset as f32,
            font.char_descs.iter().copied(),
            i32::try_from(font.num_horizontal_border_pixels).unwrap_or(0),
            i32::try_from(font.num_vertical_border_pixels).unwrap_or(0),
        ))
    }

    /// The glyph for `c`; for a character without one that is not a
    /// control character, the fallback character's glyph when the font has
    /// one (OpenAC `UiDatFont.TryGetGlyph`).
    #[must_use]
    pub fn glyph(&self, c: char) -> Option<&FontCharDesc> {
        self.glyphs
            .get(&c)
            .or_else(|| match self.fallback_character {
                Some(fallback) if !c.is_control() => self.glyphs.get(&fallback),
                _ => None,
            })
    }

    /// True when the font has an outline atlas.
    #[must_use]
    pub fn has_background(&self) -> bool {
        self.background.is_some()
    }

    /// Width of `text` in pixels: the sum of the glyph advances; characters
    /// without a glyph (and no fallback) add nothing.
    #[must_use]
    pub fn measure_width(&self, text: &str) -> f32 {
        text.chars()
            .filter_map(|c| self.glyph(c))
            .map(glyph_advance)
            .sum()
    }

    /// The advance of `c`, 0 without a glyph.
    #[must_use]
    pub fn advance(&self, c: char) -> f32 {
        self.glyph(c).map_or(0.0, glyph_advance)
    }
}

/// How far a glyph moves the pen: before + width + after.
#[must_use]
pub fn glyph_advance(g: &FontCharDesc) -> f32 {
    g.advance() as f32
}

#[cfg(test)]
pub(crate) mod test_fonts {
    use super::*;

    /// A glyph of the given size and offsets.
    pub fn glyph(
        c: char,
        offset: (u16, u16),
        size: (u8, u8),
        before: i8,
        after: i8,
        down: i8,
    ) -> FontCharDesc {
        FontCharDesc {
            unicode: c as u16,
            offset_x: offset.0,
            offset_y: offset.1,
            width: size.0,
            height: size.1,
            horizontal_offset_before: before,
            horizontal_offset_after: after,
            vertical_offset_before: down,
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiDatFontTests.cs`.

    use super::test_fonts::glyph;
    use super::*;

    fn font() -> UiFont {
        UiFont::new(
            1,
            Atlas {
                texture: TextureRef::Surface(1),
                width: 64,
                height: 64,
            },
            None,
            16.0,
            12.0,
            [
                glyph('A', (0, 0), (5, 8), 1, 2, 0),
                glyph('B', (0, 0), (6, 8), -1, 0, 0),
            ],
            0,
            0,
        )
    }

    fn table(glyphs: &[FontCharDesc]) -> UiFont {
        UiFont::new(
            1,
            Atlas {
                texture: TextureRef::Surface(1),
                width: 64,
                height: 64,
            },
            None,
            16.0,
            12.0,
            glyphs.iter().copied(),
            0,
            0,
        )
    }

    #[test]
    fn glyph_advance_sums_before_width_after() {
        assert_eq!(glyph_advance(&glyph('A', (0, 0), (8, 16), 1, 2, 0)), 11.0);
        assert_eq!(glyph_advance(&glyph('j', (0, 0), (4, 16), -1, 0, 0)), 3.0);
    }

    #[test]
    fn measure_width_sums_each_glyph_advance() {
        let f = table(&[
            glyph('2', (0, 0), (7, 16), 1, 1, 0),
            glyph('9', (0, 0), (7, 16), 1, 1, 0),
            glyph('1', (0, 0), (3, 16), 2, 1, 0),
            glyph('/', (0, 0), (4, 16), 0, 1, 0),
        ]);
        assert_eq!(f.measure_width("291/291"), 53.0);
        let f = table(&[glyph('5', (0, 0), (6, 16), 1, 1, 0)]);
        assert_eq!(f.measure_width("5X5"), 16.0);
        assert_eq!(f.measure_width(""), 0.0);
        let f = font();
        assert_eq!(f.measure_width("AB"), 13.0);
        assert_eq!(f.measure_width("AxB"), 13.0);
    }

    /// OpenAC `UiDatFont.TryGetGlyph` with a fallback character (the
    /// bundled font's `?`): printable characters without a glyph measure
    /// and draw as it, control characters stay without one; a dat font has
    /// no fallback.
    #[test]
    fn a_fallback_character_stands_in_for_printable_characters_only() {
        let plain = table(&[glyph('?', (0, 0), (5, 8), 1, 1, 0)]);
        assert!(plain.glyph('X').is_none());
        assert_eq!(plain.measure_width("X?"), 7.0);
        let f = plain.with_fallback('?');
        assert_eq!(f.glyph('X'), f.glyph('?'));
        assert_eq!(f.measure_width("X?漢"), 21.0);
        assert!(f.glyph('\n').is_none());
        assert!(f.glyph('\u{7}').is_none());
        assert_eq!(f.advance('\t'), 0.0);
        let without = table(&[glyph('A', (0, 0), (5, 8), 0, 0, 0)]).with_fallback('?');
        assert!(without.glyph('X').is_none());
    }

    #[test]
    fn border_pixels_are_kept_verbatim() {
        let mut f = font();
        assert_eq!((f.border_x, f.border_y), (0, 0));
        f = UiFont::new(1, f.foreground, None, 16.0, 12.0, [], 2, 1);
        assert_eq!((f.border_x, f.border_y), (2, 1));
    }

    #[test]
    fn a_later_glyph_for_the_same_character_wins() {
        let f = UiFont::new(
            1,
            Atlas {
                texture: TextureRef::Surface(1),
                width: 8,
                height: 8,
            },
            None,
            16.0,
            12.0,
            [
                glyph('A', (0, 0), (5, 8), 0, 0, 0),
                glyph('A', (0, 0), (7, 8), 0, 0, 0),
            ],
            0,
            0,
        );
        assert_eq!(f.measure_width("A"), 7.0);
    }

    #[test]
    fn from_dat_needs_a_foreground_atlas() {
        let mut dat = Font {
            id: 0x4000_0000,
            max_char_height: 16,
            max_char_width: 16,
            char_descs: vec![glyph('A', (405, 4), (9, 8), 0, 0, 4)],
            num_horizontal_border_pixels: 4,
            num_vertical_border_pixels: 4,
            baseline_offset: 12,
            foreground_surface_data_id: 0x0600_5EE5,
            background_surface_data_id: 0x0600_5EE6,
        };
        let f = UiFont::from_dat(&dat, |_| Some((1024, 312))).unwrap();
        assert_eq!((f.line_height, f.baseline_offset), (16.0, 12.0));
        assert_eq!((f.border_x, f.border_y), (4, 4));
        assert_eq!(
            f.background.unwrap().texture,
            TextureRef::Surface(0x0600_5EE6)
        );
        assert_eq!(f.measure_width("A"), 9.0);
        assert!(UiFont::from_dat(&dat, |_| None).is_none());
        dat.foreground_surface_data_id = 0;
        assert!(UiFont::from_dat(&dat, |_| Some((1, 1))).is_none());
    }
}
