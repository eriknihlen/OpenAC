//! The radar widget: draws the object blips and the player marker over its
//! authored disc and compass art, names the blip under the pointer, and
//! reports a click on a blip.

use crate::draw::{Color, RenderContext};
use crate::event::{UiEvent, kind};
use crate::radar::{
    PLAYER_MARKER_COLOR, PLAYER_MARKER_PIXELS, PixelOffset, RadarBlip, RadarSnapshot,
    SELECTION_PIXELS, blip_pixels, round_pixel,
};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId, Widget};

/// The radar's widget class.
pub const RETAIL_CLASS_ID: u32 = 0x1000_0010;
/// A blip is under the pointer within six pixels (compared squared).
pub const HOVER_RADIUS_PIXELS_SQUARED: i32 = 36;

/// The radar widget.
#[derive(Debug, Clone)]
pub struct Radar {
    /// The disc's centre, element-local pixels.
    pub center: (f32, f32),
    snapshot: RadarSnapshot,
    hovered: Option<(u32, String)>,
}

impl Default for Radar {
    fn default() -> Self {
        Self {
            center: (60.0, 60.0),
            snapshot: RadarSnapshot::default(),
            hovered: None,
        }
    }
}

impl Radar {
    /// The frame drawn.
    #[must_use]
    pub fn snapshot(&self) -> &RadarSnapshot {
        &self.snapshot
    }

    /// The object under the pointer.
    #[must_use]
    pub fn hovered_object_id(&self) -> Option<u32> {
        self.hovered.as_ref().map(|(id, _)| *id)
    }

    /// The name under the pointer (the tooltip).
    #[must_use]
    pub fn hovered_name(&self) -> Option<&str> {
        self.hovered.as_ref().map(|(_, n)| n.as_str())
    }

    /// Show a new frame; a hovered object that left the frame is let go.
    pub fn apply_snapshot(&mut self, ui: &mut UiRoot, me: ElementId, snapshot: RadarSnapshot) {
        self.snapshot = snapshot;
        if let Some(id) = self.hovered_object_id()
            && !self.snapshot.blips.iter().any(|b| b.object_id == id)
        {
            self.set_hovered(ui, me, None);
        }
    }

    /// The blip within the hover radius nearest (`x`, `y`); an exact tie
    /// keeps the blip drawn first.
    #[must_use]
    pub fn blip_at(&self, x: i32, y: i32) -> Option<&RadarBlip> {
        if self.snapshot.blank_blips {
            return None;
        }
        let mut best: Option<&RadarBlip> = None;
        let mut nearest = i32::MAX;
        for b in &self.snapshot.blips {
            let dx = x - round_pixel(b.pixel_x);
            let dy = y - round_pixel(b.pixel_y);
            let d2 = dx * dx + dy * dy;
            if d2 > HOVER_RADIUS_PIXELS_SQUARED || d2 >= nearest {
                continue;
            }
            nearest = d2;
            best = Some(b);
        }
        best.filter(|b| b.object_id != 0)
    }

    fn resolve_object_under_cursor(&mut self, ui: &mut UiRoot, me: ElementId, x: i32, y: i32) {
        let hit = self.blip_at(x, y).map(|b| (b.object_id, b.name.clone()));
        self.set_hovered(ui, me, hit);
    }

    fn set_hovered(&mut self, ui: &mut UiRoot, me: ElementId, next: Option<(u32, String)>) {
        let name_changed = self.hovered.as_ref().map(|(_, n)| n) != next.as_ref().map(|(_, n)| n);
        if self.hovered_object_id() == next.as_ref().map(|(id, _)| *id) && !name_changed {
            return;
        }
        self.hovered = next;
        // A shown name belongs to the blip it was raised for.
        if name_changed {
            ui.notify_tooltip_text_changed(me);
        }
        ui.send_message(
            me,
            ElementMessage::HoveredObjectChanged(self.hovered_object_id()),
        );
    }

    fn draw_pixels(
        rc: &mut RenderContext<'_>,
        cx: i32,
        cy: i32,
        color: Color,
        pixels: &[PixelOffset],
    ) {
        for (dx, dy) in pixels {
            rc.fill((cx + dx) as f32, (cy + dy) as f32, 1.0, 1.0, color);
        }
    }
}

impl Widget for Radar {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        // Only the event's own target reads its coordinates.
        if (e.kind == kind::HOVER_ENTER || e.kind == kind::MOUSE_MOVE) && e.target == Some(me) {
            self.resolve_object_under_cursor(ui, me, e.data1, e.data2);
            return false;
        }
        if e.kind == kind::HOVER_LEAVE {
            self.set_hovered(ui, me, None);
            return false;
        }
        if e.kind == kind::CLICK
            && let Some(id) = self.hovered_object_id()
        {
            ui.send_message(me, ElementMessage::ObjectSelected(id));
            return true;
        }
        false
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        // Blips move under a parked pointer: resolve against it every frame.
        let (mx, my) = ui.mouse();
        let (sx, sy) = ui.screen_position(me);
        self.resolve_object_under_cursor(ui, me, (mx as f32 - sx) as i32, (my as f32 - sy) as i32);
    }

    fn receives_hover_mouse_move(&self) -> bool {
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn tooltip_text(&self, _element: &Element) -> Option<String> {
        self.hovered.as_ref().map(|(_, n)| n.clone())
    }

    fn draw_after_children(
        &mut self,
        _ui: &mut UiRoot,
        _me: ElementId,
        rc: &mut RenderContext<'_>,
    ) {
        if !self.snapshot.blank_blips {
            for b in &self.snapshot.blips {
                let (x, y) = (round_pixel(b.pixel_x), round_pixel(b.pixel_y));
                Self::draw_pixels(rc, x, y, b.color, blip_pixels(b.shape));
                if b.selected {
                    Self::draw_pixels(rc, x, y, b.color, &SELECTION_PIXELS);
                }
            }
        }
        Self::draw_pixels(
            rc,
            round_pixel(self.center.0),
            round_pixel(self.center.1),
            PLAYER_MARKER_COLOR,
            &PLAYER_MARKER_PIXELS,
        );
    }
}
