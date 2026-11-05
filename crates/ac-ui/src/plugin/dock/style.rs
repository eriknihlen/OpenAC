//! The plugin dock's shapes, in interface points. Every theme draws the dock
//! through here; Classic passes [`Palette::CLASSIC_DOCK`], so this is the one
//! place Classic takes the rounded look.

use std::sync::Arc;

use super::layout::{DockMode, Metrics, ScrollBar};
use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext};
use crate::font::UiFont;
use crate::geometry::Rect;
use crate::plugin::style::{
    chevron, chevron_left, chevron_right, chevron_up, hover, mix, pressed, surface, with_alpha,
};
use crate::plugin::widgets::{ButtonLook, Palette};
use crate::shapes::CornerRadii;

/// How far a divider sits in from the dock's sides.
pub const DIVIDER_INSET: f32 = 8.0;
/// How far a divider's colour moves from the border toward the fill.
pub const DIVIDER_FADE: f32 = 0.6;
/// The dot beside an open window's slot, each way.
pub const OPEN_DOT: f32 = 3.0;
/// How far that dot sits in from the dock's edge.
pub const OPEN_DOT_INSET: f32 = 1.5;
/// A rail slot's well is inset this far across the rail, clear of the edge pill.
pub const RAIL_WELL_ACROSS: f32 = 5.0;
/// A rail slot's well is inset this far along the rail.
pub const RAIL_WELL_ALONG: f32 = 1.0;
/// An edge pill's thickness.
pub const PILL_THICKNESS: f32 = 3.0;
/// An edge pill's length under the pointer.
pub const PILL_HOVER: f32 = 4.0;
/// An edge pill's length while its window is open.
pub const PILL_OPEN: f32 = 8.0;
/// An edge pill's length beside the front window.
pub const PILL_FRONT: f32 = 20.0;
/// How long an edge pill takes to grow to the front window's length.
pub const PILL_SECONDS: f32 = 0.12;
/// Between the dock and a hover label.
pub const LABEL_GAP: f32 = 8.0;
/// A hover label's padding along its text.
pub const LABEL_PAD_X: f32 = 9.0;
/// A hover label's padding above and below its text.
pub const LABEL_PAD_Y: f32 = 5.0;
/// A hover label's corner radius.
pub const LABEL_RADIUS: f32 = 7.0;
/// The count on a closed tile: its height, and how far it sits from the corner.
pub const BADGE_SIZE: f32 = 14.0;
/// How far the count sits in from the tile's corner.
pub const BADGE_INSET: f32 = 2.0;
/// A grip dot, each way.
pub const GRIP_DOT: f32 = 2.0;
/// Between the grip's dots across the dock, centre to centre.
pub const GRIP_DOT_PITCH: f32 = 5.0;
/// Between the grip's two rows, centre to centre.
pub const GRIP_DOT_ROWS: f32 = 4.0;
/// How far the grip's well sits in from the dock's sides, clear of its largest
/// rounded corners.
pub const GRIP_WELL_SIDES: f32 = 9.0;
/// How far the grip's well sits in along the dock.
pub const GRIP_WELL_ENDS: f32 = 2.0;
/// The bar on the length handle: how long it runs across the dock.
pub const LENGTH_BAR_ACROSS: f32 = 16.0;
/// The bar on the length handle: how thick it is.
pub const LENGTH_BAR_THICKNESS: f32 = 2.0;
/// Colour art's alpha while its window is closed.
pub const CLOSED_ART_ALPHA: f32 = 0.85;
/// How wide an arrow's chevron is across the dock.
pub const ARROW_ACROSS: f32 = 10.0;
/// How long an arrow's chevron is along the dock.
pub const ARROW_ALONG: f32 = 5.0;
/// An arrow's alpha when the list has no more that way.
pub const SPENT_ARROW_ALPHA: f32 = 0.3;
/// The scroll bar's thickness.
pub const SCROLL_BAR_THICKNESS: f32 = 3.0;
/// How far the scroll bar sits in from the dock's side.
pub const SCROLL_BAR_INSET: f32 = 1.5;

const fn rgb(packed: u32) -> Color {
    Color::rgba(
        ((packed >> 16) & 0xFF) as f32 / 255.0,
        ((packed >> 8) & 0xFF) as f32 / 255.0,
        (packed & 0xFF) as f32 / 255.0,
        1.0,
    )
}

const MONOGRAM_INK: Color = rgb(0xF1_ECE2);
const MONOGRAM_HUES: [Color; 8] = [
    rgb(0x6E_8B5E),
    rgb(0x5E_7F8B),
    rgb(0x8B_6E5E),
    rgb(0x7A_6A99),
    rgb(0x8B_8259),
    rgb(0x5E_6E8B),
    rgb(0x8B_5E74),
    rgb(0x5E_8B7A),
];

/// The screen edge a piece of the dock faces.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DockSide {
    /// The left edge.
    #[default]
    Left,
    /// The right edge.
    Right,
    /// The top edge.
    Top,
    /// The bottom edge.
    Bottom,
}

/// The dock's corners: all four when floating, only the two inner ones on a
/// rail.
#[must_use]
pub const fn corners(mode: DockMode, m: &Metrics) -> CornerRadii {
    let r = m.rail_radius;
    match mode {
        DockMode::Floating | DockMode::FloatingHorizontal => CornerRadii::all(m.radius),
        DockMode::Left => CornerRadii::new(0.0, r, r, 0.0),
        DockMode::Right => CornerRadii::new(r, 0.0, 0.0, r),
        DockMode::Top => CornerRadii::new(0.0, 0.0, r, r),
        DockMode::Bottom => CornerRadii::new(r, r, 0.0, 0.0),
    }
}

/// The dock's body: shadow, fill, a faint sheen along its top and a rim, which
/// a rail leaves off its screen-edge side.
pub fn body(rc: &mut RenderContext<'_>, p: &Palette, mode: DockMode, m: &Metrics, w: f32, h: f32) {
    let rail = mode.is_rail();
    let radius = if rail { m.rail_radius } else { m.radius };
    let shadow = with_alpha(Color::BLACK, 0.42);
    rc.soft_shadow(
        0.0,
        0.0,
        w,
        h,
        radius,
        if rail { 2.0 } else { 4.0 },
        14.0,
        shadow,
    );
    let radii = corners(mode, m);
    rc.fill_rounded_rect(0.0, 0.0, w, h, radii, p.background);
    let sheen = with_alpha(p.text, 0.04);
    rc.fill_vertical_gradient(
        0.0,
        0.0,
        w,
        h.min(12.0),
        CornerRadii::new(radii.top_left, radii.top_right, 0.0, 0.0),
        sheen,
        with_alpha(sheen, 0.0),
    );
    // A rail's rim runs off the screen on its edge side.
    let (x, y, width, height) = match mode {
        DockMode::Left => (-radius, 0.0, w + radius, h),
        DockMode::Right => (0.0, 0.0, w + radius, h),
        DockMode::Top => (0.0, -radius, w, h + radius),
        DockMode::Bottom => (0.0, 0.0, w, h + radius),
        DockMode::Floating | DockMode::FloatingHorizontal => (0.0, 0.0, w, h),
    };
    rc.stroke_rounded_rect(
        x + 0.5,
        y + 0.5,
        width - 1.0,
        height - 1.0,
        CornerRadii::all(radius - 0.5),
        mix(p.border, p.text, 0.08),
        1.0,
    );
}

/// A slot's hover well in the slot's space: the whole slot when floating, on
/// a rail inset clear of the edge pill.
#[must_use]
pub const fn slot_well_rect(mode: DockMode, w: f32, h: f32) -> Rect {
    let (ax, ay) = match mode {
        DockMode::Floating | DockMode::FloatingHorizontal => (0.0, 0.0),
        DockMode::Left | DockMode::Right => (RAIL_WELL_ACROSS, RAIL_WELL_ALONG),
        DockMode::Top | DockMode::Bottom => (RAIL_WELL_ALONG, RAIL_WELL_ACROSS),
    };
    Rect::new(ax, ay, w - 2.0 * ax, h - 2.0 * ay)
}

/// The well behind a slot while it is under the pointer or held; nothing at
/// rest.
pub fn slot_well(rc: &mut RenderContext<'_>, p: &Palette, r: Rect, radius: f32, look: ButtonLook) {
    let face = match look {
        ButtonLook::Hovered => hover(p, p.field),
        ButtonLook::Pressed => pressed(hover(p, p.field)),
        ButtonLook::Normal | ButtonLook::Disabled => return,
    };
    rc.fill_rounded_rect(r.x, r.y, r.w, r.h, CornerRadii::all(radius), face);
}

/// The well of the grip or the length handle in `r`.
fn grip_well(r: Rect, horizontal: bool) -> Rect {
    let (inset_x, inset_y) = if horizontal {
        (GRIP_WELL_ENDS, GRIP_WELL_SIDES)
    } else {
        (GRIP_WELL_SIDES, GRIP_WELL_ENDS)
    };
    Rect::new(
        r.x + inset_x,
        r.y + inset_y,
        r.w - 2.0 * inset_x,
        r.h - 2.0 * inset_y,
    )
}

/// The length handle's bar, running across the dock and centred in `r`, unlike the
/// grip's dots, with a well behind it under the pointer or held.
pub fn length_handle(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    r: Rect,
    horizontal: bool,
    radius: f32,
    look: ButtonLook,
) {
    slot_well(rc, p, grip_well(r, horizontal), radius, look);
    let color = if look == ButtonLook::Normal {
        p.muted
    } else {
        p.text
    };
    let (w, h) = if horizontal {
        (LENGTH_BAR_THICKNESS, LENGTH_BAR_ACROSS)
    } else {
        (LENGTH_BAR_ACROSS, LENGTH_BAR_THICKNESS)
    };
    rc.fill_rounded_rect(
        r.x + (r.w - w) / 2.0,
        r.y + (r.h - h) / 2.0,
        w,
        h,
        CornerRadii::all(LENGTH_BAR_THICKNESS / 2.0),
        color,
    );
}

/// The grip's dots, two rows of four running across the dock, centred in `r`
/// with a well behind them under the pointer or held.
pub fn grip(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    r: Rect,
    horizontal: bool,
    radius: f32,
    look: ButtonLook,
) {
    slot_well(rc, p, grip_well(r, horizontal), radius, look);
    let color = if look == ButtonLook::Normal {
        p.muted
    } else {
        p.text
    };
    let (cx, cy) = (r.x + r.w / 2.0, r.y + r.h / 2.0);
    for row in [-0.5, 0.5] {
        for dot in [-1.5, -0.5, 0.5, 1.5] {
            let (dx, dy) = if horizontal {
                (row * GRIP_DOT_ROWS, dot * GRIP_DOT_PITCH)
            } else {
                (dot * GRIP_DOT_PITCH, row * GRIP_DOT_ROWS)
            };
            rc.fill_ellipse(
                cx + dx - GRIP_DOT / 2.0,
                cy + dy - GRIP_DOT / 2.0,
                GRIP_DOT,
                GRIP_DOT,
                color,
            );
        }
    }
}

/// The small accent dot beside an open window's slot on a floating dock, on
/// the side facing `side`.
pub fn open_dot(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    side: DockSide,
    (w, h): (f32, f32),
    slot: Rect,
) {
    let (x, y) = match side {
        DockSide::Left => (OPEN_DOT_INSET, slot.y + (slot.h - OPEN_DOT) / 2.0),
        DockSide::Right => (
            w - OPEN_DOT_INSET - OPEN_DOT,
            slot.y + (slot.h - OPEN_DOT) / 2.0,
        ),
        DockSide::Top => (slot.x + (slot.w - OPEN_DOT) / 2.0, OPEN_DOT_INSET),
        DockSide::Bottom => (
            slot.x + (slot.w - OPEN_DOT) / 2.0,
            h - OPEN_DOT_INSET - OPEN_DOT,
        ),
    };
    rc.fill_ellipse(x, y, OPEN_DOT, OPEN_DOT, p.accent);
}

/// The pill on a rail's screen edge beside a slot, `length` long along the
/// rail.
pub fn edge_pill(
    rc: &mut RenderContext<'_>,
    color: Color,
    side: DockSide,
    (w, h): (f32, f32),
    slot: Rect,
    length: f32,
) {
    if length <= 0.0 {
        return;
    }
    let r = PILL_THICKNESS;
    let along_x = slot.x + (slot.w - length) / 2.0;
    let along_y = slot.y + (slot.h - length) / 2.0;
    let (x, y, pw, ph, radii) = match side {
        DockSide::Left => (0.0, along_y, r, length, CornerRadii::new(0.0, r, r, 0.0)),
        DockSide::Right => (w - r, along_y, r, length, CornerRadii::new(r, 0.0, 0.0, r)),
        DockSide::Top => (along_x, 0.0, length, r, CornerRadii::new(0.0, 0.0, r, r)),
        DockSide::Bottom => (along_x, h - r, length, r, CornerRadii::new(r, r, 0.0, 0.0)),
    };
    rc.fill_rounded_rect(x, y, pw, ph, radii, color);
}

/// A faint hairline between two plugins' slots, `at` along the dock: the
/// border colour faded toward the fill.
pub fn divider(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    mode: DockMode,
    (w, h): (f32, f32),
    at: f32,
) {
    let color = mix(p.border, p.background, DIVIDER_FADE);
    if mode.is_horizontal() {
        rc.fill(at, DIVIDER_INSET, 1.0, h - 2.0 * DIVIDER_INSET, color);
    } else {
        rc.fill(DIVIDER_INSET, at, w - 2.0 * DIVIDER_INSET, 1.0, color);
    }
}

/// An arrow's chevron, centred in an arrow `w` by `h`, pointing along the dock:
/// toward its far end when `forward`, else its near end.
pub fn arrow(
    rc: &mut RenderContext<'_>,
    color: Color,
    horizontal: bool,
    forward: bool,
    (w, h): (f32, f32),
) {
    let (across, along) = (ARROW_ACROSS, ARROW_ALONG);
    if horizontal {
        let (x, y) = ((w - along) / 2.0, (h - across) / 2.0);
        if forward {
            chevron_right(rc, x, y, along, across, color);
        } else {
            chevron_left(rc, x, y, along, across, color);
        }
    } else {
        let (x, y) = ((w - across) / 2.0, (h - along) / 2.0);
        if forward {
            chevron(rc, x, y, across, along, color);
        } else {
            chevron_up(rc, x, y, across, along, color);
        }
    }
}

/// The bar beside an overflowing list, on the dock's side away from `side`: open
/// dots and edge pills are on the other.
pub fn scroll_bar(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    side: DockSide,
    (w, h): (f32, f32),
    bar: &ScrollBar,
) {
    let thick = SCROLL_BAR_THICKNESS;
    let across = match side {
        DockSide::Left => w - SCROLL_BAR_INSET - thick,
        DockSide::Top => h - SCROLL_BAR_INSET - thick,
        DockSide::Right | DockSide::Bottom => SCROLL_BAR_INSET,
    };
    let radii = CornerRadii::all(thick / 2.0);
    let mut piece = |start: f32, end: f32, color: Color| {
        if matches!(side, DockSide::Left | DockSide::Right) {
            rc.fill_rounded_rect(across, start, thick, end - start, radii, color);
        } else {
            rc.fill_rounded_rect(start, across, end - start, thick, radii, color);
        }
    };
    piece(
        bar.start,
        bar.end,
        mix(p.border, p.background, DIVIDER_FADE),
    );
    piece(bar.thumb_start, bar.thumb_end, p.muted);
}

/// The gear on the dock's appearance slot, filling a `size` box at (`x`, `y`).
pub fn gear(rc: &mut RenderContext<'_>, x: f32, y: f32, size: f32, color: Color) {
    let c = size / 2.0;
    let ring = size * 0.34;
    rc.stroke_ellipse(
        x + c - ring,
        y + c - ring,
        ring * 2.0,
        ring * 2.0,
        color,
        1.75,
    );
    for i in 0..8 {
        let (dy, dx) = (i as f32 * std::f32::consts::FRAC_PI_4).sin_cos();
        rc.smooth_line(
            x + c + dx * ring,
            y + c + dy * ring,
            x + c + dx * size * 0.46,
            y + c + dy * size * 0.46,
            color,
            2.0,
        );
    }
}

/// The colour behind a plugin's monogram: one of eight fixed hues, picked by
/// FNV-1a of the plugin's id, so a plugin keeps its colour between sessions.
#[must_use]
pub fn monogram_hue(plugin_id: &str) -> Color {
    let hash = plugin_id.bytes().fold(2_166_136_261_u32, |hash, b| {
        (hash ^ u32::from(b)).wrapping_mul(16_777_619)
    });
    MONOGRAM_HUES[hash as usize % MONOGRAM_HUES.len()]
}

/// A plugin's letters on its hue, filling the `size` box at (`x`, `y`).
#[allow(clippy::too_many_arguments)]
pub fn monogram(
    rc: &mut RenderContext<'_>,
    font: Option<&Arc<UiFont>>,
    letters: &str,
    hue: Color,
    x: f32,
    y: f32,
    size: f32,
    alpha: f32,
) {
    let top = mix(hue, Color::WHITE, 0.12);
    rc.fill_vertical_gradient(
        x,
        y,
        size,
        size,
        CornerRadii::all(size / 4.0),
        with_alpha(top, alpha),
        with_alpha(hue, alpha),
    );
    if let Some(font) = font
        && !letters.is_empty()
    {
        let tx = x + (size - font.measure_width(letters)) / 2.0;
        let ty = y + (size - font.line_height) / 2.0;
        let ink = with_alpha(MONOGRAM_INK, alpha);
        rc.string_dat(font, letters, tx, ty, ink, false, DEFAULT_OUTLINE_COLOR);
    }
}

/// The count of open plugin windows on a closed tile: a small accent circle in
/// the top-right corner of a tile `w` wide, in the fill's colour on the accent.
pub fn count_badge(
    rc: &mut RenderContext<'_>,
    p: &Palette,
    font: Option<&Arc<UiFont>>,
    w: f32,
    count: usize,
) {
    let text = count.to_string();
    let width = font.map_or(0.0, |f| f.measure_width(&text));
    let side = (width + 6.0).max(BADGE_SIZE);
    let (x, y) = (w - side - BADGE_INSET, BADGE_INSET);
    let ink = if p.is_light() {
        Color::WHITE
    } else {
        Color::BLACK
    };
    rc.fill_rounded_rect(
        x,
        y,
        side,
        BADGE_SIZE,
        CornerRadii::all(BADGE_SIZE / 2.0),
        p.accent,
    );
    if let Some(font) = font {
        let tx = x + (side - width) / 2.0;
        let ty = y + (BADGE_SIZE - font.line_height) / 2.0;
        rc.string_dat(font, &text, tx, ty, ink, false, DEFAULT_OUTLINE_COLOR);
    }
}

/// A hover label's box: fill, edge and shadow, at `r` in the dock's space.
pub fn hover_label(rc: &mut RenderContext<'_>, p: &Palette, r: Rect) {
    let shadow = with_alpha(Color::BLACK, 0.35);
    rc.soft_shadow(r.x, r.y, r.w, r.h, LABEL_RADIUS, 3.0, 8.0, shadow);
    surface(rc, r.x, r.y, r.w, r.h, LABEL_RADIUS, p.background, p.border);
}
