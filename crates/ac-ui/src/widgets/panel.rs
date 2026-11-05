//! A plain panel: a fill or tiled sprite with a thin border.

use crate::draw::{Color, RenderContext};
use crate::root::UiRoot;
use crate::tree::{ElementId, Widget};

/// A rectangle with a background (sprite or colour) and a border.
#[derive(Debug, Clone, PartialEq)]
pub struct Panel {
    /// Fill colour (used without a sprite).
    pub background_color: Color,
    /// Border colour.
    pub border_color: Color,
    /// Border width, pixels.
    pub border_thickness: f32,
    /// Background render surface (0: none).
    pub background_sprite: u32,
}

impl Default for Panel {
    fn default() -> Self {
        Self {
            background_color: Color::rgba(0.0, 0.0, 0.0, 0.55),
            border_color: Color::rgba(0.15, 0.15, 0.2, 0.8),
            border_thickness: 1.0,
            background_sprite: 0,
        }
    }
}

impl Widget for Panel {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        if self.background_sprite != 0 && rc.sprite_size(self.background_sprite).is_some() {
            rc.surface(self.background_sprite, 0.0, 0.0, w, h, Color::WHITE);
        } else if self.background_color.a > 0.0 {
            rc.fill(0.0, 0.0, w, h, self.background_color);
        }
        if self.border_color.a > 0.0 && self.border_thickness > 0.0 {
            rc.rect_outline(0.0, 0.0, w, h, self.border_color, self.border_thickness);
        }
    }
}
