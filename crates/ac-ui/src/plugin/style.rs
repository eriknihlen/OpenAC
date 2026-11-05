//! How the modern plugin themes draw: every rounded shape, state colour and
//! size of the look lives here, so a themed control only says what it is and
//! which state it is in. Colours come from the palette, with hover, pressed
//! and focus colours worked out from it. Classic windows never come here; the
//! plugin dock's shapes build on these in every theme (`dock::style`).
//!
//! Everything but a shadow is drawn inside the bounds given: an element clips
//! to its own rectangle, so a ring or glow outside it would be cut off.

use super::widgets::{ButtonLook, Palette};
use crate::draw::{Color, RenderContext};
use crate::event::{UiEvent, kind};
use crate::shapes::CornerRadii;

/// A themed window's corner radius.
pub const WINDOW_RADIUS: f32 = ac_plugin_ui::theme::metrics::WINDOW_RADIUS;
/// A button's, input's or other control's corner radius.
pub const CONTROL_RADIUS: f32 = ac_plugin_ui::theme::metrics::CONTROL_RADIUS;
/// A small control's corner radius.
pub const SMALL_RADIUS: f32 = 4.0;
/// The close button's X, each way.
pub const CLOSE_GLYPH_SIZE: f32 = 8.0;
/// A list's, log's or popup's corner radius.
pub const CONTAINER_RADIUS: f32 = 8.0;
/// A row highlight's corner radius.
pub const ROW_RADIUS: f32 = 5.0;
/// How far a row highlight sits in from its container's sides.
pub const ROW_INSET: f32 = 3.0;
/// The height of a themed window's title band.
pub const HEADER_HEIGHT: f32 = ac_plugin_ui::theme::metrics::HEADER_HEIGHT;
/// The width of a switch's track.
pub const SWITCH_WIDTH: f32 = ac_plugin_ui::theme::metrics::SWITCH_WIDTH;
/// The height of a switch's track.
pub const SWITCH_HEIGHT: f32 = ac_plugin_ui::theme::metrics::SWITCH_HEIGHT;
/// The diameter of a switch's knob.
pub const SWITCH_KNOB: f32 = ac_plugin_ui::theme::metrics::SWITCH_KNOB;
/// Between a switch and its caption.
pub const SWITCH_CAPTION_GAP: f32 = 8.0;
/// A check box's side.
pub const CHECK_SIZE: f32 = 11.0;
/// The height of a slider's track.
pub const SLIDER_TRACK_HEIGHT: f32 = 4.0;
/// A slider thumb's diameter.
pub const SLIDER_THUMB_SIZE: f32 = 14.0;
/// A scroll thumb's width at rest.
pub const SCROLL_THUMB_WIDTH: f32 = 4.0;
/// A scroll thumb's width while hovered or dragged.
pub const SCROLL_THUMB_ACTIVE_WIDTH: f32 = 6.0;
/// The alpha a disabled control fades by.
pub const DISABLED_ALPHA: f32 = 0.45;

/// `a`'s colour moved `t` of the way to `b`'s, keeping `a`'s alpha.
#[must_use]
pub fn mix(a: Color, b: Color, t: f32) -> Color {
    Color::rgba(
        a.r + (b.r - a.r) * t,
        a.g + (b.g - a.g) * t,
        a.b + (b.b - a.b) * t,
        a.a,
    )
}

/// `color` with its alpha set to `a`.
#[must_use]
pub const fn with_alpha(color: Color, a: f32) -> Color {
    Color::rgba(color.r, color.g, color.b, a)
}

/// A fill under the pointer.
#[must_use]
pub fn hover(p: &Palette, fill: Color) -> Color {
    mix(fill, p.border, 0.30)
}

/// An edge under the pointer.
#[must_use]
pub fn hover_edge(p: &Palette, edge: Color) -> Color {
    mix(edge, p.text, 0.15)
}

/// A fill held down.
#[must_use]
pub fn pressed(fill: Color) -> Color {
    mix(fill, Color::BLACK, 0.25)
}

/// The glow inside a focused field.
#[must_use]
pub const fn focus_glow(p: &Palette) -> Color {
    with_alpha(p.accent, 0.30)
}

/// The ring round a control with keyboard focus.
#[must_use]
pub const fn focus_ring_color(p: &Palette) -> Color {
    with_alpha(p.accent, 0.60)
}

/// A colour on a disabled control.
#[must_use]
pub const fn faded(color: Color) -> Color {
    with_alpha(color, color.a * DISABLED_ALPHA)
}

/// Whether the pointer is over a control and whether the left button went
/// down on it. It only watches events: the control's own handling decides
/// what they do. A press shows as pressed only while the pointer is still
/// over the control.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct PointerState {
    hovered: bool,
    pressed: bool,
}

impl PointerState {
    /// Follows one event.
    pub fn observe(&mut self, e: &UiEvent) {
        match e.kind {
            kind::HOVER_ENTER => self.hovered = true,
            kind::HOVER_LEAVE => self.hovered = false,
            kind::MOUSE_DOWN => {
                self.pressed = true;
                self.hovered = true;
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED => self.pressed = false,
            _ => {}
        }
    }

    /// The look a control shows now.
    #[must_use]
    pub fn look(&self, enabled: bool) -> ButtonLook {
        if !enabled {
            ButtonLook::Disabled
        } else if self.pressed && self.hovered {
            ButtonLook::Pressed
        } else if self.hovered {
            ButtonLook::Hovered
        } else {
            ButtonLook::Normal
        }
    }
}

/// A rounded fill with a one-pixel edge lying just inside it.
#[allow(clippy::too_many_arguments)]
pub fn surface(
    rc: &mut RenderContext<'_>,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    radius: f32,
    fill: Color,
    edge: Color,
) {
    if !(w > 0.0 && h > 0.0) {
        return;
    }
    if fill.a > 0.0 {
        rc.fill_rounded_rect(x, y, w, h, CornerRadii::all(radius), fill);
    }
    if edge.a > 0.0 {
        rc.stroke_rounded_rect(
            x + 0.5,
            y + 0.5,
            w - 1.0,
            h - 1.0,
            CornerRadii::all((radius - 0.5).max(0.0)),
            edge,
            1.0,
        );
    }
}

/// A two-pixel ring one pixel inside a control's edge.
pub fn focus_ring(rc: &mut RenderContext<'_>, p: &Palette, w: f32, h: f32, radius: f32) {
    rc.stroke_rounded_rect(
        2.0,
        2.0,
        w - 4.0,
        h - 4.0,
        CornerRadii::all((radius - 2.0).max(0.0)),
        focus_ring_color(p),
        2.0,
    );
}

/// A button's face in its look, at (`x`, `y`).
#[allow(clippy::too_many_arguments)]
pub fn button(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    (x, y, w, h): (f32, f32, f32, f32),
    fill: Color,
    edge: Color,
    look: ButtonLook,
) {
    let (fill, edge) = match look {
        ButtonLook::Hovered => (hover(p, fill), hover_edge(p, edge)),
        ButtonLook::Pressed => (pressed(fill), edge),
        ButtonLook::Disabled => (faded(fill), faded(edge)),
        ButtonLook::Normal => (fill, edge),
    };
    surface(rc, x, y, w, h, CONTROL_RADIUS, fill, edge);
}

/// A button with no face until the pointer is over it.
pub fn ghost_button(rc: &mut RenderContext<'_>, p: &Palette, w: f32, h: f32, look: ButtonLook) {
    let face = match look {
        ButtonLook::Hovered => hover(p, p.field),
        ButtonLook::Pressed => pressed(hover(p, p.field)),
        ButtonLook::Normal | ButtonLook::Disabled => return,
    };
    rc.fill_rounded_rect(0.0, 0.0, w, h, CornerRadii::all(SMALL_RADIUS), face);
}

/// A title bar's close button: a ghost face and an X, muted at rest and in
/// the text colour while hovered or pressed, ringed while focused.
pub fn close_button(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    w: f32,
    h: f32,
    look: ButtonLook,
    focused: bool,
) {
    ghost_button(rc, p, w, h, look);
    let color = if matches!(look, ButtonLook::Hovered | ButtonLook::Pressed) {
        p.text
    } else {
        p.muted
    };
    let (x, y) = ((w - CLOSE_GLYPH_SIZE) / 2.0, (h - CLOSE_GLYPH_SIZE) / 2.0);
    let (x1, y1) = (x + CLOSE_GLYPH_SIZE, y + CLOSE_GLYPH_SIZE);
    rc.smooth_line(x, y, x1, y1, color, 1.5);
    rc.smooth_line(x1, y, x, y1, color, 1.5);
    if focused {
        focus_ring(rc, p, w, h, SMALL_RADIUS);
    }
}

/// A tab: a pill while selected, or under the pointer.
pub fn tab(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    w: f32,
    h: f32,
    selected: bool,
    look: ButtonLook,
) {
    let radii = CornerRadii::all(CONTROL_RADIUS);
    if selected {
        rc.fill_rounded_rect(0.0, 0.0, w, h, radii, p.selected);
    } else if matches!(look, ButtonLook::Hovered | ButtonLook::Pressed) {
        rc.fill_rounded_rect(0.0, 0.0, w, h, radii, hover(p, p.field));
    }
}

/// A text box: the accent edge and a glow inside it while focused.
pub fn field(rc: &mut RenderContext<'_>, p: &Palette, w: f32, h: f32, fill: Color, focused: bool) {
    let edge = if focused { p.accent } else { p.border };
    surface(rc, 0.0, 0.0, w, h, CONTROL_RADIUS, fill, edge);
    if focused {
        rc.stroke_rounded_rect(
            2.0,
            2.0,
            w - 4.0,
            h - 4.0,
            CornerRadii::all(CONTROL_RADIUS - 2.0),
            focus_glow(p),
            2.0,
        );
    }
}

/// A pill switch at (`x`, `y`), [`SWITCH_WIDTH`] by [`SWITCH_HEIGHT`].
pub fn switch(rc: &mut RenderContext<'_>, p: &Palette, x: f32, y: f32, on: bool, enabled: bool) {
    let (mut track, mut edge, mut knob) = if on {
        (with_alpha(p.accent, 0.35), p.accent, p.text)
    } else {
        (p.field, p.border, p.muted)
    };
    if !enabled {
        (track, edge, knob) = (faded(track), faded(edge), faded(knob));
    }
    let radius = SWITCH_HEIGHT / 2.0;
    surface(rc, x, y, SWITCH_WIDTH, SWITCH_HEIGHT, radius, track, edge);
    let knob_x = if on {
        x + SWITCH_WIDTH - 3.0 - SWITCH_KNOB
    } else {
        x + 3.0
    };
    let knob_y = y + (SWITCH_HEIGHT - SWITCH_KNOB) / 2.0;
    rc.fill_ellipse(knob_x, knob_y, SWITCH_KNOB, SWITCH_KNOB, knob);
}

/// A rounded box with a tick, for check columns in lists.
pub fn check(rc: &mut RenderContext<'_>, p: &Palette, x: f32, y: f32, on: bool) {
    let (fill, edge) = if on {
        (p.accent, p.accent)
    } else {
        (p.field, p.border)
    };
    surface(rc, x, y, CHECK_SIZE, CHECK_SIZE, SMALL_RADIUS, fill, edge);
    if on {
        rc.smooth_line(x + 2.5, y + 5.5, x + 4.5, y + 7.5, p.field, 1.5);
        rc.smooth_line(x + 4.5, y + 7.5, x + 8.5, y + 3.5, p.field, 1.5);
    }
}

/// A slider's track, filled part and round thumb centred on `thumb_centre`.
pub fn slider(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    w: f32,
    h: f32,
    thumb_centre: f32,
    active: bool,
) {
    let track_y = (h - SLIDER_TRACK_HEIGHT) / 2.0;
    let radii = CornerRadii::all(SLIDER_TRACK_HEIGHT / 2.0);
    rc.fill_rounded_rect(0.0, track_y, w, SLIDER_TRACK_HEIGHT, radii, p.field);
    let filled = thumb_centre.clamp(0.0, w.max(0.0));
    if filled > 0.0 {
        rc.fill_rounded_rect(0.0, track_y, filled, SLIDER_TRACK_HEIGHT, radii, p.accent);
    }
    let size = SLIDER_THUMB_SIZE.min(h);
    let tx = (thumb_centre - size / 2.0).clamp(0.0, (w - size).max(0.0));
    let ty = (h - size) / 2.0;
    rc.fill_ellipse(tx, ty + 1.0, size, size, with_alpha(Color::BLACK, 0.35));
    rc.fill_ellipse(tx, ty, size, size, p.text);
    if active {
        rc.stroke_ellipse(tx + 1.0, ty + 1.0, size - 2.0, size - 2.0, p.accent, 2.0);
    }
}

fn thumb_color(p: &Palette, active: bool) -> Color {
    if active {
        with_alpha(p.text, 0.7)
    } else {
        p.muted
    }
}

fn thumb_width(active: bool) -> f32 {
    if active {
        SCROLL_THUMB_ACTIVE_WIDTH
    } else {
        SCROLL_THUMB_WIDTH
    }
}

/// A slim rounded scroll thumb `h` long, centred in a lane `lane_width` wide.
#[allow(clippy::too_many_arguments)]
pub fn scroll_thumb(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    x: f32,
    y: f32,
    lane_width: f32,
    h: f32,
    active: bool,
) {
    let w = thumb_width(active);
    rc.fill_rounded_rect(
        x + (lane_width - w) / 2.0,
        y,
        w,
        h,
        CornerRadii::all(w / 2.0),
        thumb_color(p, active),
    );
}

/// A slim rounded scroll thumb `w` long, centred in a horizontal lane
/// `lane_height` tall.
#[allow(clippy::too_many_arguments)]
pub fn scroll_thumb_horizontal(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    x: f32,
    y: f32,
    w: f32,
    lane_height: f32,
    active: bool,
) {
    let h = thumb_width(active);
    rc.fill_rounded_rect(
        x,
        y + (lane_height - h) / 2.0,
        w,
        h,
        CornerRadii::all(h / 2.0),
        thumb_color(p, active),
    );
}

/// A row highlight inset from the container's sides.
pub fn row(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, color: Color) {
    rc.fill_rounded_rect(
        x + ROW_INSET,
        y + 1.0,
        w - 2.0 * ROW_INSET,
        h - 2.0,
        CornerRadii::all(ROW_RADIUS),
        color,
    );
}

/// A downward chevron, `w` wide and `h` tall.
pub fn chevron(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, color: Color) {
    rc.smooth_line(x, y, x + w / 2.0, y + h, color, 1.5);
    rc.smooth_line(x + w / 2.0, y + h, x + w, y, color, 1.5);
}

/// An upward chevron, `w` wide and `h` tall.
pub fn chevron_up(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, color: Color) {
    rc.smooth_line(x, y + h, x + w / 2.0, y, color, 1.5);
    rc.smooth_line(x + w / 2.0, y, x + w, y + h, color, 1.5);
}

/// A rightward chevron, `w` wide and `h` tall.
pub fn chevron_right(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, color: Color) {
    rc.smooth_line(x, y, x + w, y + h / 2.0, color, 1.5);
    rc.smooth_line(x + w, y + h / 2.0, x, y + h, color, 1.5);
}

/// A leftward chevron, `w` wide and `h` tall.
pub fn chevron_left(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, color: Color) {
    rc.smooth_line(x + w, y, x, y + h / 2.0, color, 1.5);
    rc.smooth_line(x, y + h / 2.0, x + w, y + h, color, 1.5);
}

/// A meter's rounded track, and its filled part in `bar`.
pub fn meter(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    w: f32,
    h: f32,
    (fx, fy, fw, fh): (f32, f32, f32, f32),
    bar: Color,
) {
    let radii = CornerRadii::all(3.0_f32.min(h / 2.0));
    rc.fill_rounded_rect(0.0, 0.0, w, h, radii, p.field);
    if fw > 0.0 && fh > 0.0 {
        rc.fill_rounded_rect(fx, fy, fw, fh, radii, bar);
    }
}

/// The shadow under a window.
pub fn window_shadow(rc: &mut RenderContext<'_>, w: f32, h: f32) {
    let color = with_alpha(Color::BLACK, 0.45);
    rc.soft_shadow(0.0, 0.0, w, h, WINDOW_RADIUS, 4.0, 12.0, color);
}

/// The shadow under a popup.
pub fn popup_shadow(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32) {
    let color = with_alpha(Color::BLACK, 0.35);
    rc.soft_shadow(x, y, w, h, CONTAINER_RADIUS, 3.0, 8.0, color);
}

/// The band behind a window's title, `height` tall, with a separator line
/// under it.
pub fn header(rc: &mut RenderContext<'_>, p: &Palette, w: f32, height: f32) {
    rc.fill_vertical_gradient(
        0.0,
        0.0,
        w,
        height,
        CornerRadii::new(WINDOW_RADIUS, WINDOW_RADIUS, 0.0, 0.0),
        mix(p.background, p.text, 0.06),
        mix(p.background, p.text, 0.02),
    );
    rc.fill(0.0, height, w, 1.0, p.border);
}

/// The resize grip: three dots in the bottom-right corner.
pub fn resize_grip(rc: &mut RenderContext<'_>, p: &Palette, w: f32, h: f32) {
    for i in 0..3 {
        rc.fill_ellipse(w - 8.0 - i as f32 * 4.0, h - 6.0, 2.0, 2.0, p.muted);
    }
}

#[cfg(test)]
pub(super) mod tests;
