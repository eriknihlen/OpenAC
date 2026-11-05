//! The shared window frame the client wraps panels in when their layout
//! has no frame of its own (the inventory, character and spellbook
//! windows, the toolbar): the tiled centre under the content, then the
//! bevel edges and corners and the resize grips over it. The toolbar's
//! variant snaps its dragged height to two stops and shows its second row
//! only when expanded.

use crate::draw::{Color, RenderContext, TextureRef};
use crate::root::UiRoot;
use crate::tree::{ElementId, Widget};
use crate::widgets::chrome::{
    BORDER, BOTTOM_EDGE, CENTER_FILL, CORNER_BL, CORNER_BR, CORNER_TL, CORNER_TR, LEFT_EDGE,
    RIGHT_EDGE, Rect, TOP_EDGE, compute_frame_rects,
};

/// The resize grip's corner art.
pub const GRIP_CORNER: u32 = 0x0600_6129;
/// The top edge grip (10×5, tiled across).
pub const GRIP_TOP: u32 = 0x0600_612A;
/// The left edge grip (5×10, tiled down).
pub const GRIP_LEFT: u32 = 0x0600_612B;
/// The bottom edge grip (10×5).
pub const GRIP_BOTTOM: u32 = 0x0600_612C;
/// The right edge grip (5×10).
pub const GRIP_RIGHT: u32 = 0x0600_612D;

/// A nine-piece frame around a window's content.
#[derive(Debug, Clone, PartialEq)]
pub struct NineSliceFrame {
    /// Tile the centre art under the content.
    pub draw_center_fill: bool,
    /// Draw the resize grips over the edges.
    pub draw_resize_affordances: bool,
    /// The collapsed height of a two-stop frame (0: not one).
    pub collapsed_height: f32,
    /// The expanded height of a two-stop frame.
    pub expanded_height: f32,
    /// What shows only while expanded.
    pub second_row: Vec<ElementId>,
}

impl Default for NineSliceFrame {
    fn default() -> Self {
        Self {
            draw_center_fill: true,
            draw_resize_affordances: true,
            collapsed_height: 0.0,
            expanded_height: 0.0,
            second_row: Vec::new(),
        }
    }
}

impl NineSliceFrame {
    /// The frame is at (or nearer) its expanded stop.
    #[must_use]
    pub fn is_expanded(&self, height: f32) -> bool {
        height >= (self.collapsed_height + self.expanded_height) * 0.5
    }

    fn tiled(rc: &mut RenderContext<'_>, id: u32, r: Rect) {
        if r.w > 0.0 && r.h > 0.0 {
            rc.surface(id, r.x, r.y, r.w, r.h, Color::WHITE);
        }
    }

    fn stretched(rc: &mut RenderContext<'_>, id: u32, r: Rect) {
        if r.w > 0.0 && r.h > 0.0 && rc.sprite_size(id).is_some() {
            rc.sprite(
                TextureRef::Surface(id),
                r.x,
                r.y,
                r.w,
                r.h,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        }
    }
}

impl Widget for NineSliceFrame {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        if self.draw_center_fill {
            Self::tiled(rc, CENTER_FILL, compute_frame_rects(w, h, BORDER).center);
        }
    }

    fn draw_after_children(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        let r = compute_frame_rects(w, h, BORDER);
        Self::tiled(rc, TOP_EDGE, r.top);
        Self::tiled(rc, BOTTOM_EDGE, r.bottom);
        Self::tiled(rc, LEFT_EDGE, r.left);
        Self::tiled(rc, RIGHT_EDGE, r.right);
        Self::stretched(rc, CORNER_TL, r.tl);
        Self::stretched(rc, CORNER_TR, r.tr);
        Self::stretched(rc, CORNER_BL, r.bl);
        Self::stretched(rc, CORNER_BR, r.br);
        if !self.draw_resize_affordances {
            return;
        }
        Self::tiled(rc, GRIP_TOP, r.top);
        Self::tiled(rc, GRIP_BOTTOM, r.bottom);
        Self::tiled(rc, GRIP_LEFT, r.left);
        Self::tiled(rc, GRIP_RIGHT, r.right);
        for corner in [r.tl, r.tr, r.bl, r.br] {
            Self::stretched(rc, GRIP_CORNER, corner);
        }
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        if self.expanded_height <= self.collapsed_height {
            return;
        }
        let Some(height) = ui.element(me).map(|e| e.height) else {
            return;
        };
        // The dragged height snaps to the nearer stop.
        let expanded = self.is_expanded(height);
        let stop = if expanded {
            self.expanded_height
        } else {
            self.collapsed_height
        };
        if let Some(e) = ui.element_mut(me) {
            e.height = stop;
        }
        for &row in &self.second_row {
            ui.set_visible(row, expanded);
        }
    }
}
