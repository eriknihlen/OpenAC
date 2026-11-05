//! The session's plugin UI (plugin UI is runtime state a window only
//! draws): the content it measures text and
//! sizes client art with (the dats, the same with or without a window), and
//! the world facts its image calls read.
//!
//! The state itself is [`ac_plugin_ui::PluginUi`] in the session state
//! (`SessionState::plugin_ui`); plugins reach it through
//! [`super::PluginBinding`], an attached window gets a snapshot and its
//! changes as [`crate::session::interface::InterfaceEvent::PluginUi`], and
//! sends the player's input back as
//! [`crate::session::SessionCommand::PluginUi`].

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use ac_dat::objects::{DEFAULT_FONT_ID, Font, RenderSurface};
use ac_plugin_ui::{TextMetrics, UiResources};

use crate::movement::WorldContent;
use crate::session::SessionState;

/// The plugin UI's content from the dats: the interface font's glyph
/// advances (the font a window draws plugin text with) and the sizes of
/// render surfaces.
pub struct DatUiResources {
    content: Arc<WorldContent>,
    metrics: TextMetrics,
    sizes: Mutex<HashMap<u32, Option<(u32, u32)>>>,
}

impl std::fmt::Debug for DatUiResources {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatUiResources").finish_non_exhaustive()
    }
}

/// The interface font's measures: each glyph's advance (before + width +
/// after) and the tallest glyph as the line height, as the window's font
/// measures them (`ac_ui::font::UiFont::measure_width`, `line_height`).
#[must_use]
pub fn font_metrics(font: &Font) -> TextMetrics {
    TextMetrics::new(
        font.char_descs
            .iter()
            .filter_map(|g| char::from_u32(u32::from(g.unicode)).map(|c| (c, g.advance() as f32))),
        font.max_char_height as f32,
    )
}

impl DatUiResources {
    /// From the session's content; without the interface font, text
    /// measures nothing.
    #[must_use]
    pub fn new(content: Arc<WorldContent>) -> Self {
        let metrics = content
            .dats()
            .get::<Font>(DEFAULT_FONT_ID)
            .ok()
            .flatten()
            .map(|font| font_metrics(&font))
            .unwrap_or_default();
        Self {
            content,
            metrics,
            sizes: Mutex::new(HashMap::new()),
        }
    }
}

impl UiResources for DatUiResources {
    fn measure_text(&self, text: &str) -> (f32, f32) {
        self.metrics.measure(text)
    }

    fn surface_size(&self, surface_id: u32) -> Option<(u32, u32)> {
        let mut sizes = self.sizes.lock().unwrap_or_else(PoisonError::into_inner);
        *sizes.entry(surface_id).or_insert_with(|| {
            match self.content.dats().get::<RenderSurface>(surface_id) {
                // A JPEG surface's header says 0 x 0: its size is the image's.
                Ok(Some(rs)) if rs.width == 0 || rs.height == 0 => {
                    ac_dat::texture::decode(&rs, None, ac_dat::texture::DecodeOptions::default())
                        .ok()
                        .map(|d| (d.width, d.height))
                }
                Ok(Some(rs)) => Some((rs.width, rs.height)),
                _ => None,
            }
        })
    }

    fn image_dimensions(&self, bytes: &[u8]) -> Option<(u32, u32)> {
        ac_plugin::image_codec::dimensions(bytes)
    }

    fn decode_image(&self, bytes: &[u8]) -> Result<ac_plugin_ui::DecodedImage, String> {
        ac_plugin::image_codec::decode(bytes)
            .map(|d| ac_plugin_ui::DecodedImage {
                width: d.width,
                height: d.height,
                rgba: d.rgba,
            })
            .map_err(|error| error.to_string())
    }
}

/// A spell's icon render surface, if the session's spell table has the
/// spell.
#[must_use]
pub fn spell_icon(state: &SessionState, spell: u32) -> Option<u32> {
    state
        .gameplay
        .tables()
        .spells()
        .get(spell)
        .map(|m| m.icon_id)
        .filter(|icon| *icon != 0)
}

/// A known object's icon (0 for none is no icon).
#[must_use]
pub fn object_icon(state: &SessionState, object: u32) -> Option<u32> {
    state
        .world
        .items()
        .get(object)
        .map(|o| o.icon_id)
        .filter(|icon| *icon != 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_dat::objects::FontCharDesc;

    #[test]
    fn text_measures_like_the_windows_font() {
        // The window's font sums glyph advances (OpenAC UiDatFontTests'
        // golden: "291/291" is 53 pixels) and its line height is the
        // tallest glyph.
        let glyph = |c: char, width: u8, before: i8, after: i8| FontCharDesc {
            unicode: c as u16,
            offset_x: 0,
            offset_y: 0,
            width,
            height: 16,
            horizontal_offset_before: before,
            horizontal_offset_after: after,
            vertical_offset_before: 0,
        };
        let font = Font {
            id: DEFAULT_FONT_ID,
            max_char_height: 16,
            max_char_width: 16,
            char_descs: vec![
                glyph('2', 7, 1, 1),
                glyph('9', 7, 1, 1),
                glyph('1', 3, 2, 1),
                glyph('/', 4, 0, 1),
            ],
            num_horizontal_border_pixels: 0,
            num_vertical_border_pixels: 0,
            baseline_offset: 12,
            foreground_surface_data_id: 0,
            background_surface_data_id: 0,
        };
        assert_eq!(font_metrics(&font).measure("291/291"), (53.0, 16.0));
        assert_eq!(font_metrics(&font).measure(""), (0.0, 0.0));
    }
}
