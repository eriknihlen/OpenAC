//! The scrollbar: arrows, a thumb sized to the visible share of the
//! content, page clicks on the track, thumb drags. It drives a scroll
//! model — its own, or the one another element owns (a transcript, a list).
//! The same class is also a value slider ("scalar"): a thumb moving over a
//! track, reporting a position 0..=1.

use crate::draw::{Color, RenderContext, TextureRef};
use crate::event::{UiEvent, kind};
use crate::geometry::PixelRect;
use crate::layout_policy::LayoutPolicy;
use crate::plugin::Palette;
use crate::plugin::style;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};

/// Smallest proportional thumb, pixels.
pub const MIN_THUMB: f32 = 8.0;
/// Height of a composited thumb's end caps, pixels.
const CAP_H: f32 = 3.0;

/// Where a scrollbar's scroll model lives.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScrollSource {
    /// No model: the bar draws its track only and ignores input.
    None,
    /// Its own model.
    Own(Scrollable),
    /// The model of another element (see [`UiRoot::scroll_model_mut`]).
    Element(ElementId),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum EndButton {
    #[default]
    None,
    Decrement,
    Increment,
}

/// A scrollbar or value slider.
#[derive(Debug, Clone)]
pub struct Scrollbar {
    /// The scroll model it reflects and drives.
    pub model: ScrollSource,
    /// A value slider (reports [`ElementMessage::ScalarChanged`]) instead
    /// of a scrollbar.
    pub scalar: bool,
    /// Lies along x.
    pub horizontal: bool,
    /// Draws authored art; plain colours otherwise.
    pub retail_art: bool,
    /// Plain track colour.
    pub plain_track_color: Color,
    /// Plain border colour.
    pub plain_border_color: Color,
    /// Plain thumb colour.
    pub plain_nub_color: Color,
    /// The plugin theme the plain look draws in (none: flat).
    pub theme_palette: Option<Palette>,
    /// Fill share of a slider (`None`: no fill).
    pub scalar_fill: Option<f32>,
    /// Fill image of a slider.
    pub scalar_fill_sprite: u32,
    /// Range image of a slider.
    pub scalar_range_sprite: u32,
    /// Left of the range, pixels.
    pub scalar_range_left: f32,
    /// Width of the range, pixels (infinite: to the right end).
    pub scalar_range_width: f32,
    /// The range's authored layout (wins over left and width).
    pub scalar_range_layout_policy: Option<LayoutPolicy>,
    /// The fill grows from the right.
    pub scalar_fill_from_right: bool,
    /// Run-time tooltip.
    pub tooltip_text: Option<String>,
    /// Track image.
    pub track_sprite: u32,
    /// The fill a horizontal bar draws from its start to the thumb's centre
    /// (0: none). It ends under the thumb, never past it.
    pub thumb_fill_sprite: u32,
    /// The fill's art per bar state, for a fill that authors an image for
    /// each state (an attribute slider fills in another colour while locked).
    pub thumb_fill_sprites_by_state: Vec<(u32, u32)>,
    /// Thumb image (middle of a composited thumb).
    pub thumb_sprite: u32,
    /// Thumb top cap.
    pub thumb_top_sprite: u32,
    /// Thumb bottom cap.
    pub thumb_bot_sprite: u32,
    /// Thumb, hovered.
    pub thumb_rollover_sprite: u32,
    /// Thumb, dragged.
    pub thumb_pressed_sprite: u32,
    /// Top cap, hovered.
    pub thumb_top_rollover_sprite: u32,
    /// Top cap, dragged.
    pub thumb_top_pressed_sprite: u32,
    /// Bottom cap, hovered.
    pub thumb_bot_rollover_sprite: u32,
    /// Bottom cap, dragged.
    pub thumb_bot_pressed_sprite: u32,
    /// Decrement (up/left) arrow.
    pub up_sprite: u32,
    /// Increment (down/right) arrow.
    pub down_sprite: u32,
    /// Decrement arrow, hovered.
    pub up_rollover_sprite: u32,
    /// Decrement arrow, pressed.
    pub up_pressed_sprite: u32,
    /// Increment arrow, hovered.
    pub down_rollover_sprite: u32,
    /// Increment arrow, pressed.
    pub down_pressed_sprite: u32,
    /// The decrement arrow's extent along the bar, pixels.
    pub decrement_button_extent: f32,
    /// The increment arrow's extent along the bar, pixels.
    pub increment_button_extent: f32,
    /// The thumb is the visible share of the track; otherwise it keeps its
    /// authored extent.
    pub proportional: bool,
    /// Smallest proportional thumb, pixels.
    pub min_thumb_extent: f32,
    /// Authored thumb extent when not proportional (0: the image's size).
    pub thumb_extent: f32,
    /// Hide the whole bar while there is nothing to scroll.
    pub hide_when_disabled: bool,
    /// The thumb image's size, learned when it is drawn.
    pub thumb_native_size: Option<(f32, f32)>,

    scalar_position: f32,
    shown: bool,
    dragging_thumb: bool,
    hovered_thumb: bool,
    drag_offset_x: f32,
    drag_offset_y: f32,
    hovered_button: EndButton,
    pressed_button: EndButton,
}

impl Default for Scrollbar {
    fn default() -> Self {
        Self {
            model: ScrollSource::None,
            scalar: false,
            horizontal: false,
            retail_art: true,
            plain_track_color: Color::rgba(0.0, 0.0, 0.0, 0.6),
            plain_border_color: Color::rgba(0.46, 0.37, 0.16, 1.0),
            plain_nub_color: Color::rgba(0.72, 0.62, 0.34, 1.0),
            theme_palette: None,
            scalar_fill: None,
            scalar_fill_sprite: 0,
            scalar_range_sprite: 0,
            scalar_range_left: 0.0,
            scalar_range_width: f32::INFINITY,
            scalar_range_layout_policy: None,
            scalar_fill_from_right: false,
            tooltip_text: None,
            track_sprite: 0,
            thumb_fill_sprite: 0,
            thumb_fill_sprites_by_state: Vec::new(),
            thumb_sprite: 0,
            thumb_top_sprite: 0,
            thumb_bot_sprite: 0,
            thumb_rollover_sprite: 0,
            thumb_pressed_sprite: 0,
            thumb_top_rollover_sprite: 0,
            thumb_top_pressed_sprite: 0,
            thumb_bot_rollover_sprite: 0,
            thumb_bot_pressed_sprite: 0,
            up_sprite: 0,
            down_sprite: 0,
            up_rollover_sprite: 0,
            up_pressed_sprite: 0,
            down_rollover_sprite: 0,
            down_pressed_sprite: 0,
            decrement_button_extent: 16.0,
            increment_button_extent: 16.0,
            proportional: true,
            min_thumb_extent: MIN_THUMB,
            thumb_extent: 0.0,
            hide_when_disabled: false,
            thumb_native_size: None,
            scalar_position: 0.0,
            shown: true,
            dragging_thumb: false,
            hovered_thumb: false,
            drag_offset_x: 0.0,
            drag_offset_y: 0.0,
            hovered_button: EndButton::None,
            pressed_button: EndButton::None,
        }
    }
}

/// The thumb of a proportional bar with the default minimum: its start and
/// extent along a track of `track_len` pixels starting at `track_top`.
#[must_use]
pub fn thumb_rect(m: &Scrollable, track_top: f32, track_len: f32) -> (f32, f32) {
    let h = MIN_THUMB.max(track_len * m.thumb_ratio());
    let travel = track_len - h;
    (track_top + travel * m.position_ratio(), h)
}

/// The filled part of a slider's range: its left and width.
#[must_use]
pub fn scalar_fill_rect(
    range_left: f32,
    range_width: f32,
    fill: f32,
    from_right: bool,
) -> (f32, f32) {
    let safe = range_width.max(0.0);
    let visible = safe * fill.clamp(0.0, 1.0);
    (
        if from_right {
            range_left + safe - visible
        } else {
            range_left
        },
        visible,
    )
}

/// How far the fill before a horizontal thumb reaches: from the bar's start
/// to the thumb's centre, in whole pixels (the thumb's left edge truncated,
/// plus half its width rounded down).
#[must_use]
pub fn thumb_fill_width(thumb_x: f32, thumb_width: f32) -> f32 {
    (thumb_x as i32 + thumb_width as i32 / 2).max(0) as f32
}

fn axis_extent(authored: f32, axis_length: f32) -> f32 {
    authored.clamp(0.0, axis_length.max(0.0))
}

impl Scrollbar {
    /// The element flags a scrollbar starts with: drags inside it move the
    /// thumb, not the window.
    pub fn configure(element: &mut Element) {
        element.captures_pointer_drag = true;
        element.click_through = false;
    }

    /// A scrollbar driving the model of element `target`.
    #[must_use]
    pub fn linked(target: ElementId) -> Self {
        Self {
            model: ScrollSource::Element(target),
            ..Self::default()
        }
    }

    /// The slider position, 0..=1.
    #[must_use]
    pub fn scalar_position(&self) -> f32 {
        self.scalar_position
    }

    /// Move the slider (clamped), without reporting it.
    pub fn set_scalar_position(&mut self, position: f32) {
        self.scalar_position = position.clamp(0.0, 1.0);
    }

    /// True while the thumb is dragged.
    #[must_use]
    pub fn is_dragging(&self) -> bool {
        self.dragging_thumb
    }

    fn read_model(&self, ui: &mut UiRoot) -> Option<Scrollable> {
        match self.model {
            ScrollSource::None => None,
            ScrollSource::Own(m) => Some(m),
            ScrollSource::Element(id) => ui.scroll_model(id),
        }
    }

    fn write_model(&mut self, ui: &mut UiRoot, m: Scrollable) {
        match self.model {
            ScrollSource::None => {}
            ScrollSource::Own(_) => self.model = ScrollSource::Own(m),
            ScrollSource::Element(id) => {
                if let Some(target) = ui.scroll_model_mut(id) {
                    *target = m;
                }
            }
        }
    }

    /// The bar's model, when it has one.
    #[must_use]
    pub fn own_model(&self) -> Option<&Scrollable> {
        match &self.model {
            ScrollSource::Own(m) => Some(m),
            _ => None,
        }
    }

    fn is_model_disabled(&self, model: Option<&Scrollable>) -> bool {
        !self.scalar && model.is_some_and(|m| !m.has_overflow())
    }

    fn is_presentation_visible(&self, model: Option<&Scrollable>) -> bool {
        !self.hide_when_disabled || !self.is_model_disabled(model)
    }

    /// True when the bar shows (a bar that hides while disabled does not
    /// while there is nothing to scroll).
    pub fn presentation_visible(&self, ui: &mut UiRoot) -> bool {
        let m = self.read_model(ui);
        self.is_presentation_visible(m.as_ref())
    }

    /// The thumb's start and extent along the axis.
    #[must_use]
    pub fn model_thumb_rect(&self, m: &Scrollable, track_start: f32, track_len: f32) -> (f32, f32) {
        let extent = if self.proportional {
            self.min_thumb_extent.max(track_len * m.thumb_ratio())
        } else {
            track_len.max(0.0).min(self.fixed_thumb_extent())
        };
        let travel = track_len - extent;
        (track_start + travel * m.position_ratio(), extent)
    }

    fn fixed_thumb_extent(&self) -> f32 {
        if self.thumb_extent > 0.0 {
            return self.thumb_extent;
        }
        if self.thumb_sprite != 0
            && let Some((w, h)) = self.thumb_native_size
        {
            let native = if self.horizontal { w } else { h };
            if native > 0.0 {
                return native;
            }
        }
        self.min_thumb_extent
    }

    fn scalar_thumb_extent(&self, axis_length: f32) -> f32 {
        if self.thumb_sprite != 0
            && let Some((w, h)) = self.thumb_native_size
        {
            let native = if self.horizontal { w } else { h };
            if native > 0.0 {
                return native.min(axis_length);
            }
        }
        16.0_f32.min(axis_length)
    }

    /// The slider range's left and width inside a bar `w` × `h`.
    #[must_use]
    pub fn scalar_range_rect(&self, w: f32, h: f32) -> (f32, f32) {
        let (mut left, mut width) = (self.scalar_range_left, self.scalar_range_width);
        if let Some(policy) = self.scalar_range_layout_policy {
            let range = policy.apply(
                policy.original_child,
                PixelRect::from_position_and_size(0, 0, w as i32, h as i32),
            );
            left = range.x0 as f32;
            width = range.width() as f32;
        }
        let left = left.clamp(0.0, w);
        let requested = if width.is_infinite() && width > 0.0 {
            w - left
        } else {
            width.max(0.0)
        };
        (left, requested.clamp(0.0, (w - left).max(0.0)))
    }

    fn active_thumb(&self, normal: u32, rollover: u32, pressed: u32) -> u32 {
        if self.dragging_thumb && pressed != 0 {
            pressed
        } else if self.hovered_thumb && !self.dragging_thumb && rollover != 0 {
            rollover
        } else {
            normal
        }
    }

    /// The thumb image drawn now.
    #[must_use]
    pub fn active_thumb_sprite(&self) -> u32 {
        self.active_thumb(
            self.thumb_sprite,
            self.thumb_rollover_sprite,
            self.thumb_pressed_sprite,
        )
    }

    fn active_thumb_top_sprite(&self) -> u32 {
        self.active_thumb(
            self.thumb_top_sprite,
            self.thumb_top_rollover_sprite,
            self.thumb_top_pressed_sprite,
        )
    }

    fn active_thumb_bot_sprite(&self) -> u32 {
        self.active_thumb(
            self.thumb_bot_sprite,
            self.thumb_bot_rollover_sprite,
            self.thumb_bot_pressed_sprite,
        )
    }

    /// The decrement arrow image drawn now.
    #[must_use]
    pub fn active_start_sprite(&self) -> u32 {
        if self.pressed_button == EndButton::Decrement
            && self.hovered_button == EndButton::Decrement
            && self.up_pressed_sprite != 0
        {
            self.up_pressed_sprite
        } else if self.hovered_button == EndButton::Decrement && self.up_rollover_sprite != 0 {
            self.up_rollover_sprite
        } else {
            self.up_sprite
        }
    }

    /// The increment arrow image drawn now.
    #[must_use]
    pub fn active_end_sprite(&self) -> u32 {
        if self.pressed_button == EndButton::Increment
            && self.hovered_button == EndButton::Increment
            && self.down_pressed_sprite != 0
        {
            self.down_pressed_sprite
        } else if self.hovered_button == EndButton::Increment && self.down_rollover_sprite != 0 {
            self.down_rollover_sprite
        } else {
            self.down_sprite
        }
    }

    // ── Drawing ─────────────────────────────────────────────────────────

    fn draw_sprite(rc: &mut RenderContext<'_>, id: u32, x: f32, y: f32, w: f32, h: f32) {
        if id == 0 || w <= 0.0 || h <= 0.0 || rc.sprite_size(id).is_none() {
            return;
        }
        rc.sprite(
            TextureRef::Surface(id),
            x,
            y,
            w,
            h,
            (0.0, 0.0, 1.0, 1.0),
            Color::WHITE,
        );
    }

    fn draw_tiled(rc: &mut RenderContext<'_>, id: u32, x: f32, y: f32, w: f32, h: f32) {
        if id == 0 || w <= 0.0 || h <= 0.0 {
            return;
        }
        rc.surface(id, x, y, w, h, Color::WHITE);
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_thumb_marker(
        rc: &mut RenderContext<'_>,
        id: u32,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        vertical: bool,
    ) {
        if id == 0 || w <= 0.0 || h <= 0.0 {
            return;
        }
        let Some((nw, nh)) = rc.sprite_size(id) else {
            return;
        };
        let t = TextureRef::Surface(id);
        if vertical {
            let dh = nh.min(h);
            let dy = y + (h - dh) * 0.5;
            rc.sprite(t, x, dy, w, dh, (0.0, 0.0, w / nw, dh / nh), Color::WHITE);
        } else {
            let dw = nw.min(w);
            let dx = x + (w - dw) * 0.5;
            rc.sprite(t, dx, y, dw, h, (0.0, 0.0, dw / nw, h / nh), Color::WHITE);
        }
    }

    /// The themed plain look. A slider's thumb is centred on the thumb its
    /// hit test uses, so it is dragged where it is drawn; a bar that follows
    /// a scroll model draws its thumb where the model's hit test puts it.
    fn draw_themed(
        &self,
        p: &Palette,
        model: Option<&Scrollable>,
        (w, h): (f32, f32),
        rc: &mut RenderContext<'_>,
    ) {
        let active = self.dragging_thumb || self.hovered_thumb;
        let axis = if self.horizontal { w } else { h };
        let start = axis_extent(self.decrement_button_extent, axis);
        let len = (axis - start - axis_extent(self.increment_button_extent, axis)).max(0.0);
        match model.filter(|_| !self.scalar) {
            Some(m) => {
                let (at, extent) = self.model_thumb_rect(m, start, len);
                if self.horizontal {
                    style::scroll_thumb_horizontal(rc, p, at, 0.0, extent, h, active);
                } else {
                    style::scroll_thumb(rc, p, 0.0, at, w, extent, active);
                }
            }
            None => {
                let thumb = self.scalar_thumb_extent(axis);
                let at = (axis - thumb).max(0.0) * self.scalar_position;
                if self.horizontal {
                    style::slider(rc, p, w, h, at + thumb * 0.5, active);
                } else {
                    style::scroll_thumb(rc, p, 0.0, at, w, thumb, active);
                }
            }
        }
    }

    fn draw_plain_model(&self, m: &Scrollable, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        rc.fill(0.0, 0.0, w, h, self.plain_track_color);
        rc.rect_outline(0.0, 0.0, w, h, self.plain_border_color, 1.0);
        if self.horizontal {
            let start = axis_extent(self.decrement_button_extent, w);
            let len = (w - start - axis_extent(self.increment_button_extent, w)).max(0.0);
            let (tx, tw) = self.model_thumb_rect(m, start, len);
            rc.fill(tx, 3.0, tw, (h - 6.0).max(0.0), self.plain_nub_color);
        } else {
            let start = axis_extent(self.decrement_button_extent, h);
            let len = (h - start - axis_extent(self.increment_button_extent, h)).max(0.0);
            let (ty, th) = self.model_thumb_rect(m, start, len);
            rc.fill(3.0, ty, (w - 6.0).max(0.0), th, self.plain_nub_color);
        }
    }

    fn draw_plain_scalar(&self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        rc.fill(0.0, 0.0, w, h, self.plain_track_color);
        rc.rect_outline(0.0, 0.0, w, h, self.plain_border_color, 1.0);
        if self.horizontal {
            let nub = 6.0_f32.min(w);
            let x = (w - nub).max(0.0) * self.scalar_position;
            rc.fill(x, 0.0, nub, h, self.plain_nub_color);
        } else {
            let nub = 6.0_f32.min(h);
            let y = (h - nub).max(0.0) * self.scalar_position;
            rc.fill(0.0, y, w, nub, self.plain_nub_color);
        }
    }

    fn draw_model(&self, m: &Scrollable, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        Self::draw_tiled(rc, self.track_sprite, 0.0, 0.0, w, h);
        if self.horizontal {
            let dec = axis_extent(self.decrement_button_extent, w);
            let inc = axis_extent(self.increment_button_extent, w);
            Self::draw_sprite(rc, self.active_start_sprite(), 0.0, 0.0, dec, h);
            Self::draw_sprite(rc, self.active_end_sprite(), w - inc, 0.0, inc, h);
            let track_len = (w - dec - inc).max(0.0);
            let (tx, tw) = self.model_thumb_rect(m, dec, track_len);
            if self.thumb_top_sprite != 0 && self.thumb_bot_sprite != 0 && tw >= 2.0 * CAP_H {
                Self::draw_sprite(rc, self.active_thumb_top_sprite(), tx, 0.0, CAP_H, h);
                Self::draw_tiled(
                    rc,
                    self.active_thumb_sprite(),
                    tx + CAP_H,
                    0.0,
                    tw - 2.0 * CAP_H,
                    h,
                );
                Self::draw_sprite(
                    rc,
                    self.active_thumb_bot_sprite(),
                    tx + tw - CAP_H,
                    0.0,
                    CAP_H,
                    h,
                );
            } else {
                Self::draw_thumb_marker(rc, self.active_thumb_sprite(), tx, 0.0, tw, h, false);
            }
            return;
        }
        let dec = axis_extent(self.decrement_button_extent, h);
        let inc = axis_extent(self.increment_button_extent, h);
        Self::draw_sprite(rc, self.active_start_sprite(), 0.0, 0.0, w, dec);
        Self::draw_sprite(rc, self.active_end_sprite(), 0.0, h - inc, w, inc);
        let track_len = (h - dec - inc).max(0.0);
        let (ty, th) = self.model_thumb_rect(m, dec, track_len);
        if self.thumb_top_sprite != 0 && self.thumb_bot_sprite != 0 && th >= 2.0 * CAP_H {
            Self::draw_sprite(rc, self.active_thumb_top_sprite(), 0.0, ty, w, CAP_H);
            Self::draw_tiled(
                rc,
                self.active_thumb_sprite(),
                0.0,
                ty + CAP_H,
                w,
                th - 2.0 * CAP_H,
            );
            Self::draw_sprite(
                rc,
                self.active_thumb_bot_sprite(),
                0.0,
                ty + th - CAP_H,
                w,
                CAP_H,
            );
        } else {
            Self::draw_thumb_marker(rc, self.active_thumb_sprite(), 0.0, ty, w, th, true);
        }
    }

    fn draw_scalar(&self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        Self::draw_tiled(rc, self.track_sprite, 0.0, 0.0, w, h);
        if self.horizontal {
            let thumb_w = self.scalar_thumb_extent(w);
            let x = (w - thumb_w).max(0.0) * self.scalar_position;
            // The fill under the thumb's left half: it ends at the thumb's
            // centre, so what lies past the thumb never takes its colour.
            Self::draw_tiled(
                rc,
                self.thumb_fill_sprite,
                0.0,
                0.0,
                thumb_fill_width(x, thumb_w),
                h,
            );
            let (range_left, range_width) = self.scalar_range_rect(w, h);
            if self.scalar_range_sprite != 0 {
                Self::draw_tiled(
                    rc,
                    self.scalar_range_sprite,
                    range_left,
                    0.0,
                    range_width,
                    h,
                );
            }
            if let Some(fill) = self.scalar_fill
                && self.scalar_fill_sprite != 0
                && let Some((tw, th)) = rc.sprite_size(self.scalar_fill_sprite)
            {
                let (fx, fw) = scalar_fill_rect(0.0, w, fill, self.scalar_fill_from_right);
                if fw > 0.0 && h > 0.0 {
                    let u0 = fx / tw;
                    rc.sprite(
                        TextureRef::Surface(self.scalar_fill_sprite),
                        fx,
                        0.0,
                        fw,
                        h,
                        (u0, 0.0, u0 + fw / tw, h / th),
                        Color::WHITE,
                    );
                }
            }
            Self::draw_sprite(rc, self.active_thumb_sprite(), x, 0.0, thumb_w, h);
        } else {
            let thumb_h = self.scalar_thumb_extent(h);
            let y = (h - thumb_h).max(0.0) * self.scalar_position;
            Self::draw_sprite(rc, self.active_thumb_sprite(), 0.0, y, w, thumb_h);
        }
    }

    // ── Hit regions ─────────────────────────────────────────────────────

    fn thumb_at(&self, m: Option<&Scrollable>, w: f32, h: f32, x: f32, y: f32) -> bool {
        if x < 0.0 || x >= w || y < 0.0 || y >= h {
            return false;
        }
        if self.scalar {
            if self.horizontal {
                let tw = self.scalar_thumb_extent(w);
                let tx = (w - tw).max(0.0) * self.scalar_position;
                return x >= tx && x <= tx + tw;
            }
            let th = self.scalar_thumb_extent(h);
            let ty = (h - th).max(0.0) * self.scalar_position;
            return y >= ty && y <= ty + th;
        }
        let Some(m) = m else {
            return false;
        };
        if self.horizontal {
            let dec = axis_extent(self.decrement_button_extent, w);
            let inc = axis_extent(self.increment_button_extent, w);
            let (tx, tw) = self.model_thumb_rect(m, dec, (w - dec - inc).max(0.0));
            return x >= tx && x <= tx + tw;
        }
        let dec = axis_extent(self.decrement_button_extent, h);
        let inc = axis_extent(self.increment_button_extent, h);
        let (ty, th) = self.model_thumb_rect(m, dec, (h - dec - inc).max(0.0));
        y >= ty && y <= ty + th
    }

    fn button_at(&self, w: f32, h: f32, x: f32, y: f32) -> EndButton {
        if x < 0.0 || x >= w || y < 0.0 || y >= h {
            return EndButton::None;
        }
        let (pos, len) = if self.horizontal { (x, w) } else { (y, h) };
        if pos < axis_extent(self.decrement_button_extent, len) {
            EndButton::Decrement
        } else if pos >= len - axis_extent(self.increment_button_extent, len) {
            EndButton::Increment
        } else {
            EndButton::None
        }
    }

    fn end_drag(&mut self, ui: &mut UiRoot, me: ElementId) {
        let was_dragging = self.dragging_thumb;
        self.dragging_thumb = false;
        self.pressed_button = EndButton::None;
        if was_dragging {
            ui.send_message(me, ElementMessage::DragCompleted);
        }
    }

    fn change_scalar_position(&mut self, ui: &mut UiRoot, me: ElementId, position: f32) {
        self.set_scalar_position(position);
        ui.send_message(me, ElementMessage::ScalarChanged(self.scalar_position));
    }

    fn on_scalar_event(
        &mut self,
        ui: &mut UiRoot,
        me: ElementId,
        e: &UiEvent,
        w: f32,
        h: f32,
    ) -> bool {
        let (pos, len) = if self.horizontal {
            (e.data1 as f32, w)
        } else {
            (e.data2 as f32, h)
        };
        match e.kind {
            kind::MOUSE_DOWN => {
                let thumb = self.scalar_thumb_extent(len);
                let travel = (len - thumb).max(1.0);
                let start = travel * self.scalar_position;
                self.dragging_thumb = true;
                let offset = if pos >= start && pos <= start + thumb {
                    pos - start
                } else {
                    let o = thumb * 0.5;
                    self.change_scalar_position(ui, me, (pos - o) / travel);
                    o
                };
                if self.horizontal {
                    self.drag_offset_x = offset;
                } else {
                    self.drag_offset_y = offset;
                }
                true
            }
            kind::MOUSE_MOVE if self.dragging_thumb => {
                let thumb = self.scalar_thumb_extent(len);
                let travel = (len - thumb).max(1.0);
                let offset = if self.horizontal {
                    self.drag_offset_x
                } else {
                    self.drag_offset_y
                };
                self.change_scalar_position(ui, me, (pos - offset) / travel);
                true
            }
            kind::MOUSE_UP => {
                self.end_drag(ui, me);
                true
            }
            _ => false,
        }
    }

    fn on_model_event(
        &mut self,
        ui: &mut UiRoot,
        me: ElementId,
        e: &UiEvent,
        mut m: Scrollable,
        w: f32,
        h: f32,
    ) -> bool {
        let (pos, len) = if self.horizontal {
            (e.data1 as f32, w)
        } else {
            (e.data2 as f32, h)
        };
        let dec = axis_extent(self.decrement_button_extent, len);
        let inc = axis_extent(self.increment_button_extent, len);
        let track_len = (len - dec - inc).max(0.0);
        let handled = match e.kind {
            kind::MOUSE_DOWN => {
                self.pressed_button = self.button_at(w, h, e.data1 as f32, e.data2 as f32);
                if pos < dec {
                    m.scroll_by_lines(-1);
                } else if pos >= len - inc {
                    m.scroll_by_lines(1);
                } else {
                    let (start, extent) = self.model_thumb_rect(&m, dec, track_len);
                    if pos >= start && pos <= start + extent {
                        self.dragging_thumb = true;
                        if self.horizontal {
                            self.drag_offset_x = pos - start;
                        } else {
                            self.drag_offset_y = pos - start;
                        }
                    } else {
                        m.scroll_by_page(if pos < start { -1 } else { 1 });
                    }
                }
                true
            }
            kind::MOUSE_MOVE if self.dragging_thumb => {
                let (_, extent) = self.model_thumb_rect(&m, dec, track_len);
                let travel = (track_len - extent).max(1.0);
                let offset = if self.horizontal {
                    self.drag_offset_x
                } else {
                    self.drag_offset_y
                };
                m.set_position_ratio((pos - offset - dec) / travel);
                true
            }
            kind::MOUSE_UP => {
                self.end_drag(ui, me);
                true
            }
            _ => false,
        };
        self.write_model(ui, m);
        handled
    }
}

impl Widget for Scrollbar {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == kind::CAPTURE_CHANGED {
            self.end_drag(ui, me);
            return false;
        }
        let Some((w, h)) = ui.element(me).map(|el| (el.width, el.height)) else {
            return false;
        };
        let model = self.read_model(ui);
        self.shown = self.is_presentation_visible(model.as_ref());
        if !self.shown {
            self.dragging_thumb = false;
            self.hovered_thumb = false;
            self.hovered_button = EndButton::None;
            self.pressed_button = EndButton::None;
            return false;
        }
        let own_coords = e.target == Some(me);
        match e.kind {
            kind::HOVER_ENTER => {
                self.hovered_button = self.button_at(w, h, e.data1 as f32, e.data2 as f32);
                self.hovered_thumb =
                    self.thumb_at(model.as_ref(), w, h, e.data1 as f32, e.data2 as f32);
                return true;
            }
            kind::HOVER_LEAVE => {
                self.hovered_button = EndButton::None;
                self.hovered_thumb = false;
                return true;
            }
            kind::MOUSE_MOVE if own_coords => {
                self.hovered_button = self.button_at(w, h, e.data1 as f32, e.data2 as f32);
                self.hovered_thumb =
                    self.thumb_at(model.as_ref(), w, h, e.data1 as f32, e.data2 as f32);
            }
            _ => {}
        }
        if self.scalar {
            return self.on_scalar_event(ui, me, e, w, h);
        }
        match model {
            Some(m) => self.on_model_event(ui, me, e, m, w, h),
            None => false,
        }
    }

    fn hit_test(&self, element: &Element, x: f32, y: f32) -> bool {
        let shown = match self.model {
            ScrollSource::Own(m) => self.is_presentation_visible(Some(&m)),
            // A linked model is read when the bar draws or handles input.
            _ => self.shown,
        };
        shown && element.contains_local(x, y)
    }

    fn receives_hover_mouse_move(&self) -> bool {
        true
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    /// Entering a state shows that state's fill art; false when the fill
    /// authors none for it.
    fn set_retail_state(&mut self, _ui: &mut UiRoot, _me: ElementId, state: u32) -> bool {
        let Some(&(_, sprite)) = self
            .thumb_fill_sprites_by_state
            .iter()
            .find(|(s, _)| *s == state)
        else {
            return false;
        };
        self.thumb_fill_sprite = sprite;
        true
    }

    fn tooltip_text(&self, element: &Element) -> Option<String> {
        self.tooltip_text
            .as_ref()
            .filter(|t| !t.trim().is_empty())
            .cloned()
            .or_else(|| {
                element
                    .runtime_tooltip_text
                    .as_ref()
                    .filter(|t| !t.trim().is_empty())
                    .cloned()
            })
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        if self.thumb_sprite != 0 {
            self.thumb_native_size = rc.sprite_size(self.thumb_sprite);
        }
        let model = self.read_model(ui);
        self.shown = self.is_presentation_visible(model.as_ref());
        if !self.shown {
            return;
        }
        if !self.retail_art {
            if let Some(p) = self.theme_palette {
                self.draw_themed(&p, model.as_ref(), (w, h), rc);
                return;
            }
            // A model-backed bar (a scrolling group, a log) has no scalar
            // position: its thumb goes where the model puts it, flat, inset 3.
            match model {
                Some(m) if !self.scalar => self.draw_plain_model(&m, w, h, rc),
                _ => self.draw_plain_scalar(w, h, rc),
            }
            return;
        }
        if self.scalar {
            self.draw_scalar(w, h, rc);
            return;
        }
        match model {
            Some(m) => self.draw_model(&m, w, h, rc),
            // A horizontal bar with no model draws as a slider.
            None if self.horizontal => self.draw_scalar(w, h, rc),
            None => {}
        }
    }

    fn scroll_model(&mut self) -> Option<&mut Scrollable> {
        match &mut self.model {
            ScrollSource::Own(m) => Some(m),
            _ => None,
        }
    }
}
