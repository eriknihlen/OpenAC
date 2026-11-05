//! The markup colour picker (OpenAC `UiMarkupColor`, `UiMarkupColorPopup`):
//! a swatch of the bound colour that opens a picker below it with a
//! saturation/value square, a hue strip, an alpha strip when allowed, a hex
//! field, a palette row and the recent colours. Edits are reported at most
//! once a tick; Escape puts back the colour it opened with.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::color::{self as cv, RecentColors};
use ac_plugin_ui::markup::{ColorValueKind, Element as Markup};
use ac_plugin_ui::{ActionValue, Rgba};

use super::controls::{DISABLED_ALPHA, Emit, FOCUS, HOTKEY_BACKGROUND, UNLIT_INNER};
use super::fold::{MENU_BACKGROUND, MENU_BORDER, MENU_TEXT, SharedControlState};
use super::style;
use super::{Palette, color as to_color};
use crate::draw::{Color, RenderContext};
use crate::event::{Key, UiEvent, kind};
use crate::font::UiFont;
use crate::root::UiRoot;
use crate::shapes::CornerRadii;
use crate::tree::{Element, ElementId, Widget};

/// The popup's margin.
pub const PAD: f32 = 6.0;
/// The saturation/value square's side.
pub const SQUARE: f32 = 128.0;
/// A strip's width.
pub const STRIP: f32 = 16.0;
/// The hex field's height.
pub const ROW: f32 = 18.0;
/// A swatch's side.
pub const SWATCH: f32 = 14.0;
/// From one swatch to the next.
pub const SWATCH_STEP: f32 = 16.0;
/// Between the swatch and the popup.
pub const GAP: f32 = 2.0;
/// Where the hue strip starts.
pub const HUE_X: f32 = PAD + SQUARE + PAD;
/// Where the alpha strip starts.
pub const ALPHA_X: f32 = HUE_X + STRIP + PAD;
/// Where the hex field starts.
pub const HEX_Y: f32 = PAD + SQUARE + PAD;
/// Where the palette row starts.
pub const PALETTE_Y: f32 = HEX_Y + ROW + PAD;
/// Where the recent row starts.
pub const RECENT_Y: f32 = PALETTE_Y + SWATCH_STEP;
/// The popup's height.
pub const POPUP_HEIGHT: f32 = RECENT_Y + SWATCH + PAD;
/// The longest hex text.
pub const MAX_HEX: usize = 9;
/// The palette row in Classic.
pub const CLASSIC_SWATCHES: [u32; 7] = [
    0xFFFF_FFFF,
    0xFF00_0000,
    0xFF80_8080,
    0xFFD0_3030,
    0xFFFF_D140,
    0xFF40_B040,
    0xFF40_70D0,
];
/// A checker cell behind a translucent colour.
pub const CHECKER: f32 = 4.0;

fn rgba(argb: u32) -> Color {
    to_color(Rgba::argb(argb))
}

fn pack(c: Color) -> u32 {
    cv::from_rgba([c.r, c.g, c.b, c.a])
}

fn checker(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32, alpha: f32) {
    let mut cy = 0.0;
    while cy < h {
        let mut cx = 0.0;
        while cx < w {
            let light = ((cx / CHECKER) as i32 + (cy / CHECKER) as i32) % 2 == 0;
            let v = if light { 0.8 } else { 0.5 };
            rc.fill(
                x + cx,
                y + cy,
                CHECKER.min(w - cx),
                CHECKER.min(h - cy),
                Color::rgba(v, v, v, alpha),
            );
            cx += CHECKER;
        }
        cy += CHECKER;
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Drag {
    None,
    Square,
    Hue,
    Alpha,
}

/// The recent colours every picker of a window shares, remembered for the
/// character when the window has a store.
#[derive(Debug, Clone, Default)]
pub struct SharedRecent {
    list: Rc<RefCell<RecentColors>>,
    store: Option<SharedControlState>,
}

impl SharedRecent {
    /// Over `store` (none: they last the session).
    #[must_use]
    pub fn new(store: Option<SharedControlState>) -> Self {
        Self {
            list: Rc::default(),
            store,
        }
    }

    fn colors(&self) -> Vec<u32> {
        let mut list = self.list.borrow_mut();
        match &self.store {
            Some(store) => {
                let mut store = store.borrow_mut();
                let mut shared = store.shared();
                list.colors(Some(&mut shared)).to_vec()
            }
            None => list.colors(None).to_vec(),
        }
    }

    fn add(&self, argb: u32) {
        let mut list = self.list.borrow_mut();
        match &self.store {
            Some(store) => {
                let mut store = store.borrow_mut();
                let mut shared = store.shared();
                list.add(argb, Some(&mut shared));
            }
            None => list.add(argb, None),
        }
    }
}

/// A colour swatch that opens a picker (OpenAC `UiMarkupColor` and its
/// popup in one element: the popup is drawn in the overlay pass and hit
/// only while open).
#[derive(Debug)]
pub struct MarkupColor {
    /// The bound colour, `0xAARRGGBB`.
    pub value: u32,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The recent colours.
    pub recent: SharedRecent,
    kind: ColorValueKind,
    allow_alpha: bool,
    onchange: String,
    emit: Emit,
    focused: bool,
    open: bool,
    rect: (f32, f32, f32, f32),
    hue: f32,
    sat: f32,
    val: f32,
    alpha: f32,
    color: u32,
    opened: u32,
    start: u32,
    pending: Option<u32>,
    drag: Drag,
    hex_editing: bool,
    hex_text: String,
    return_focus: Option<ElementId>,
}

impl MarkupColor {
    /// A picker for markup element `element` (a `<color>`).
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>, element: &Markup, emit: Emit) -> Self {
        let (kind, allow_alpha, onchange) = match element {
            Markup::Color {
                kind,
                alpha,
                onchange,
                ..
            } => (*kind, *alpha, onchange.clone()),
            _ => (ColorValueKind::Uint, false, String::new()),
        };
        Self {
            value: cv::WHITE,
            palette: None,
            font,
            recent: SharedRecent::default(),
            kind,
            allow_alpha,
            onchange,
            emit,
            focused: false,
            open: false,
            rect: (0.0, 0.0, 0.0, 0.0),
            hue: 0.0,
            sat: 0.0,
            val: 0.0,
            alpha: 1.0,
            color: cv::WHITE,
            opened: cv::WHITE,
            start: cv::WHITE,
            pending: None,
            drag: Drag::None,
            hex_editing: false,
            hex_text: String::new(),
            return_focus: None,
        }
    }

    /// The colour a bound value reads as.
    #[must_use]
    pub fn read(kind: ColorValueKind, value: Option<&ac_plugin_ui::UiValue>) -> u32 {
        use ac_plugin_ui::UiValue as V;
        match (kind, value) {
            (ColorValueKind::Uint, Some(V::Uint(v))) => *v,
            (ColorValueKind::Int, Some(V::Int(v))) => *v as u32,
            (ColorValueKind::Text, Some(V::Text(t))) => cv::parse_strict(t).unwrap_or(cv::WHITE),
            _ => cv::WHITE,
        }
    }

    /// The kind it binds.
    #[must_use]
    pub fn kind(&self) -> ColorValueKind {
        self.kind
    }

    /// Open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open
    }

    /// The working colour.
    #[must_use]
    pub fn working(&self) -> u32 {
        self.color
    }

    fn report(&self, argb: u32) {
        if argb == self.value {
            return;
        }
        let arg = match self.kind {
            ColorValueKind::Uint => ActionValue::Uint(argb),
            ColorValueKind::Int => ActionValue::Int(argb as i32),
            ColorValueKind::Text => ActionValue::Text(cv::format(argb, true)),
        };
        self.emit.act(Some(&self.onchange), arg);
    }

    fn alpha_byte(&self, alpha: f32) -> u8 {
        if self.allow_alpha {
            cv::to_byte(alpha)
        } else {
            0xFF
        }
    }

    fn set_working(&mut self, color: u32) {
        self.color = color;
        let (h, s, v) = cv::to_hsv(color);
        if v > 0.0 {
            if s > 0.0 {
                self.hue = h;
            }
            self.sat = s;
        }
        self.val = v;
        self.alpha = f32::from(cv::alpha(color)) / 255.0;
    }

    /// Opens the picker below the swatch (above it near the bottom).
    pub fn open(&mut self, ui: &mut UiRoot, me: ElementId) -> bool {
        if self.open || !ui.element(me).is_some_and(|e| e.enabled && e.visible) {
            return false;
        }
        self.opened = self.value;
        self.hue = 0.0;
        self.sat = 0.0;
        self.set_working(if self.allow_alpha {
            self.opened
        } else {
            cv::with_alpha(self.opened, 0xFF)
        });
        self.start = self.color;
        self.pending = None;
        self.drag = Drag::None;
        self.hex_editing = false;
        let w = (if self.allow_alpha { ALPHA_X } else { HUE_X }) + STRIP + PAD;
        let h = POPUP_HEIGHT;
        let (screen_w, screen_h) = ui.effective_canvas_size();
        let (ox, oy) = ui.screen_position(me);
        let height = ui.element(me).map_or(0.0, |e| e.height);
        let x = ox.clamp(0.0, (screen_w - w).max(0.0));
        let mut y = oy + height + GAP;
        if y + h > screen_h {
            y = oy - GAP - h;
        }
        let y = y.clamp(0.0, (screen_h - h).max(0.0));
        self.rect = (x - ox, y - oy, w, h);
        self.open = true;
        if let Some(e) = ui.element_mut(me) {
            // Open, a press inside keeps the keyboard here.
            e.is_edit_control = true;
            e.focus_on_mouse_click = true;
            e.captures_pointer_drag = true;
        }
        ui.set_active_popup(me, me);
        self.return_focus = ui.keyboard_focus();
        ui.set_keyboard_focus(Some(me));
        true
    }

    /// Closes; keeping applies the hex text and reports the colour, else the
    /// colour it opened with goes back.
    pub fn close(&mut self, ui: &mut UiRoot, me: ElementId, keep: bool, restore_focus: bool) {
        if !self.open {
            return;
        }
        if keep {
            self.commit_hex();
        }
        let report = if keep {
            self.pending
        } else {
            Some(self.opened)
        };
        let remember = keep && self.color != self.start;
        let last = self.color;
        self.open = false;
        self.pending = None;
        self.drag = Drag::None;
        self.hex_editing = false;
        if let Some(e) = ui.element_mut(me) {
            e.is_edit_control = false;
            e.focus_on_mouse_click = false;
            e.captures_pointer_drag = false;
        }
        let previous = self.return_focus.take();
        ui.clear_active_popup(me);
        if restore_focus && ui.keyboard_focus() == Some(me) {
            ui.set_keyboard_focus(previous);
        }
        if remember {
            self.recent.add(last);
        }
        if let Some(c) = report {
            self.report(c);
        }
    }

    fn hex_text(&self) -> String {
        if self.hex_editing {
            self.hex_text.clone()
        } else {
            cv::format(self.color, self.allow_alpha)
        }
    }

    fn commit_hex(&mut self) {
        if !std::mem::take(&mut self.hex_editing) {
            return;
        }
        if let Some(c) = cv::parse(&self.hex_text, cv::alpha(self.color))
            && c != self.color
        {
            self.pick(c);
        }
    }

    fn pick(&mut self, color: u32) {
        self.set_working(if self.allow_alpha {
            color
        } else {
            cv::with_alpha(color, 0xFF)
        });
        self.pending = Some(self.color);
    }

    fn palette_swatches(&self) -> Vec<u32> {
        match &self.palette {
            None => CLASSIC_SWATCHES.to_vec(),
            Some(p) => [
                p.background,
                p.field,
                p.border,
                p.text,
                p.muted,
                p.accent,
                p.selected,
            ]
            .into_iter()
            .map(pack)
            .collect(),
        }
    }

    fn inside(x: f32, y: f32, l: f32, t: f32, w: f32, h: f32) -> bool {
        x >= l && x < l + w && y >= t && y < t + h
    }

    fn in_hex(&self, x: f32, y: f32) -> bool {
        Self::inside(x, y, PAD, HEX_Y, self.rect.2 - 2.0 * PAD, ROW)
    }

    fn swatch_at(&self, x: f32, y: f32) -> Option<u32> {
        if x < PAD {
            return None;
        }
        let index = ((x - PAD) / SWATCH_STEP) as usize;
        if x - PAD - index as f32 * SWATCH_STEP >= SWATCH {
            return None;
        }
        let row = if Self::inside(x, y, PAD, PALETTE_Y, self.rect.2, SWATCH) {
            self.palette_swatches()
        } else if Self::inside(x, y, PAD, RECENT_Y, self.rect.2, SWATCH) {
            self.recent.colors()
        } else {
            return None;
        };
        row.get(index).copied()
    }

    fn press(&mut self, x: f32, y: f32) {
        if self.hex_editing && !self.in_hex(x, y) {
            self.commit_hex();
        }
        if Self::inside(x, y, PAD, PAD, SQUARE, SQUARE) {
            self.drag = Drag::Square;
        } else if Self::inside(x, y, HUE_X, PAD, STRIP, SQUARE) {
            self.drag = Drag::Hue;
        } else if self.allow_alpha && Self::inside(x, y, ALPHA_X, PAD, STRIP, SQUARE) {
            self.drag = Drag::Alpha;
        } else if self.in_hex(x, y) {
            if !self.hex_editing {
                self.hex_text = self.hex_text();
                self.hex_editing = true;
            }
            return;
        } else {
            if let Some(c) = self.swatch_at(x, y) {
                self.pick(c);
            }
            return;
        }
        self.drag_to(x, y);
    }

    fn drag_to(&mut self, x: f32, y: f32) {
        let across = ((x - PAD) / SQUARE).clamp(0.0, 1.0);
        let down = ((y - PAD) / SQUARE).clamp(0.0, 1.0);
        match self.drag {
            Drag::Square => {
                self.sat = across;
                self.val = 1.0 - down;
            }
            Drag::Hue => self.hue = down * 360.0,
            Drag::Alpha => self.alpha = 1.0 - down,
            Drag::None => return,
        }
        self.color = cv::from_hsv(self.hue, self.sat, self.val, self.alpha_byte(self.alpha));
        self.pending = Some(self.color);
    }

    fn draw_popup(&self, rc: &mut RenderContext<'_>) {
        let p = self.palette.as_ref();
        let (x, y, w, h) = self.rect;
        let border = p.map_or(MENU_BORDER, |p| p.border);
        rc.push_alpha_absolute(1.0);
        if let Some(p) = p {
            style::popup_shadow(rc, x, y, w, h);
            style::surface(rc, x, y, w, h, style::CONTAINER_RADIUS, p.field, border);
        } else {
            rc.fill(x, y, w, h, MENU_BACKGROUND);
            rc.rect_outline(x, y, w, h, border, 1.0);
        }
        // The square: drawn in cells of the hue at each saturation and value.
        let (sx, sy) = (x + PAD, y + PAD);
        let cells = 16;
        let cell = SQUARE / cells as f32;
        for j in 0..cells {
            for i in 0..cells {
                let c = cv::from_hsv(
                    self.hue,
                    (i as f32 + 0.5) / cells as f32,
                    1.0 - (j as f32 + 0.5) / cells as f32,
                    0xFF,
                );
                rc.fill(
                    sx + i as f32 * cell,
                    sy + j as f32 * cell,
                    cell,
                    cell,
                    rgba(c),
                );
            }
        }
        let (mx, my) = (sx + self.sat * SQUARE, sy + (1.0 - self.val) * SQUARE);
        rc.rect_outline(
            mx - 3.0,
            my - 3.0,
            7.0,
            7.0,
            Color::rgba(0.0, 0.0, 0.0, 1.0),
            1.0,
        );
        rc.rect_outline(mx - 2.0, my - 2.0, 5.0, 5.0, Color::WHITE, 1.0);
        // The hue strip.
        let hx = x + HUE_X;
        let hue_cells = 32;
        let hue_cell = SQUARE / hue_cells as f32;
        for j in 0..hue_cells {
            let c = cv::from_hsv(360.0 * (j as f32 + 0.5) / hue_cells as f32, 1.0, 1.0, 0xFF);
            rc.fill(hx, sy + j as f32 * hue_cell, STRIP, hue_cell, rgba(c));
        }
        strip_marker(rc, hx, sy + self.hue / 360.0 * SQUARE);
        if self.allow_alpha {
            let ax = x + ALPHA_X;
            checker(rc, ax, sy, STRIP, SQUARE, 1.0);
            let mut rgb = rgba(self.color);
            for j in 0..cells {
                rgb.a = 1.0 - (j as f32 + 0.5) / cells as f32;
                rc.fill(ax, sy + j as f32 * cell, STRIP, cell, rgb);
            }
            strip_marker(rc, ax, sy + (1.0 - self.alpha) * SQUARE);
        }
        // The hex field.
        let (fx, fy, fw) = (x + PAD, y + HEX_Y, w - 2.0 * PAD);
        let edge = if self.hex_editing {
            p.map_or(FOCUS, |p| p.accent)
        } else {
            p.map_or(UNLIT_INNER, |p| p.border)
        };
        if let Some(p) = p {
            style::surface(rc, fx, fy, fw, ROW, style::SMALL_RADIUS, p.field, edge);
        } else {
            rc.fill(fx, fy, fw, ROW, HOTKEY_BACKGROUND);
            rc.rect_outline(fx, fy, fw, ROW, edge, 1.0);
        }
        if let Some(font) = &self.font {
            let ty = fy + ((ROW - font.line_height) * 0.5).floor();
            rc.string_dat(
                font,
                &self.hex_text(),
                fx + 4.0,
                ty,
                p.map_or(MENU_TEXT, |p| p.text),
                p.is_none(),
                crate::draw::DEFAULT_OUTLINE_COLOR,
            );
        }
        draw_row(rc, &self.palette_swatches(), x, y + PALETTE_Y, border, None);
        draw_row(
            rc,
            &self.recent.colors(),
            x,
            y + RECENT_Y,
            border,
            Some(RecentColors::CAPACITY),
        );
        rc.pop_alpha();
    }
}

fn strip_marker(rc: &mut RenderContext<'_>, x: f32, y: f32) {
    rc.fill(
        x - 1.0,
        y - 1.0,
        STRIP + 2.0,
        3.0,
        Color::rgba(0.0, 0.0, 0.0, 1.0),
    );
    rc.fill(x, y, STRIP, 1.0, Color::WHITE);
}

/// A row of swatches; the recent row shows all its slots, empty ones as
/// outlines.
fn draw_row(
    rc: &mut RenderContext<'_>,
    colors: &[u32],
    x: f32,
    y: f32,
    border: Color,
    slots: Option<usize>,
) {
    for i in 0..slots.unwrap_or(colors.len()) {
        let sx = x + PAD + i as f32 * SWATCH_STEP;
        if let Some(c) = colors.get(i) {
            if cv::alpha(*c) < 0xFF {
                checker(rc, sx, y, SWATCH, SWATCH, 1.0);
            }
            rc.fill(sx, y, SWATCH, SWATCH, rgba(*c));
        }
        rc.rect_outline(sx, y, SWATCH, SWATCH, border, 1.0);
    }
}

impl Widget for MarkupColor {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        let live = ui.element(me).is_some_and(|el| el.enabled);
        if !self.open {
            match e.kind {
                kind::FOCUS_GAINED => self.focused = true,
                kind::FOCUS_LOST => self.focused = false,
                kind::CLICK => {
                    self.open(ui, me);
                }
                kind::KEY_DOWN
                    if self.focused
                        && live
                        && matches!(e.key, Key::Enter | Key::KeypadEnter | Key::Space) =>
                {
                    self.open(ui, me);
                }
                _ => return false,
            }
            return true;
        }
        let (px, py) = (e.data1 as f32 - self.rect.0, e.data2 as f32 - self.rect.1);
        match e.kind {
            kind::FOCUS_LOST => self.close(ui, me, true, false),
            kind::KEY_DOWN => match e.key {
                Key::Escape => self.close(ui, me, false, true),
                Key::Enter | Key::KeypadEnter => {
                    if self.hex_editing {
                        self.commit_hex();
                    } else {
                        self.close(ui, me, true, true);
                    }
                }
                Key::Tab => self.close(ui, me, true, true),
                Key::Backspace if self.hex_editing => {
                    self.hex_text.pop();
                }
                _ => {}
            },
            kind::CHAR => {
                if let Some(c) = char::from_u32(e.data0 as u32)
                    && self.hex_editing
                    && (c.is_ascii_hexdigit() || c == '#')
                    && self.hex_text.chars().count() < MAX_HEX
                {
                    self.hex_text.push(c);
                }
            }
            kind::MOUSE_DOWN => {
                let (_, _, w, h) = self.rect;
                if px >= 0.0 && py >= 0.0 && px < w && py < h {
                    self.press(px, py);
                }
            }
            kind::MOUSE_MOVE if self.drag != Drag::None => {
                self.drag_to(px, py);
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED => self.drag = Drag::None,
            _ => {}
        }
        true
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        if !self.open {
            return;
        }
        if !ui.element(me).is_some_and(|e| e.enabled && e.visible) {
            self.close(ui, me, true, true);
            return;
        }
        if let Some(c) = self.pending.take() {
            self.report(c);
        }
    }

    fn on_popup_dismissed(&mut self, ui: &mut UiRoot, me: ElementId, _popup: ElementId) {
        self.close(ui, me, true, true);
    }

    fn hit_test(&self, element: &Element, x: f32, y: f32) -> bool {
        let (rx, ry, rw, rh) = self.rect;
        let on_popup = self.open && x >= rx && x < rx + rw && y >= ry && y < ry + rh;
        on_popup || (x >= 0.0 && y >= 0.0 && x < element.width && y < element.height)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.open
    }

    fn clips_children(&self) -> bool {
        false
    }

    fn expands_clip_for_popup(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h, live) = ui
            .element(me)
            .map_or((0.0, 0.0, false), |e| (e.width, e.height, e.enabled));
        let fade = |mut c: Color| {
            if !live {
                c.a *= DISABLED_ALPHA;
            }
            c
        };
        if cv::alpha(self.value) < 0xFF {
            checker(
                rc,
                1.0,
                1.0,
                w - 2.0,
                h - 2.0,
                if live { 1.0 } else { DISABLED_ALPHA },
            );
        }
        let focused = self.focused || self.open;
        if let Some(p) = &self.palette {
            let radii = CornerRadii::all(style::CONTROL_RADIUS - 1.0);
            rc.fill_rounded_rect(1.0, 1.0, w - 2.0, h - 2.0, radii, fade(rgba(self.value)));
            if !live {
                rc.push_alpha(style::DISABLED_ALPHA);
            }
            style::field(rc, p, w, h, Color::TRANSPARENT, focused);
            if !live {
                rc.pop_alpha();
            }
            return;
        }
        rc.fill(1.0, 1.0, w - 2.0, h - 2.0, fade(rgba(self.value)));
        let edge = if focused { FOCUS } else { UNLIT_INNER };
        rc.rect_outline(0.0, 0.0, w, h, fade(edge), 1.0);
    }

    fn draw_overlay(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        if self.open {
            self.draw_popup(rc);
        }
    }
}
