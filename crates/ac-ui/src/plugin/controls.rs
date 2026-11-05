//! The newer markup controls in the window (OpenAC `UiMarkupCheckbox`,
//! `UiMarkupSeparator`, `UiMarkupRadio`, `UiMarkupNumber`,
//! `UiMarkupHotkey`, `UiMarkupExpanderHeader`, `UiMarkupSplitBar`,
//! `UiMarkupContextMenu`). Each reports what the player asks for straight
//! into the plugin outbox; the plugin stores it and the control shows the
//! value the session pushes back.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::number::{Dec, NumberSpec};
use ac_plugin_ui::{ActionValue, UiInput};

use crate::draw::{Color, RenderContext};
use crate::event::{Key, Modifiers, UiEvent, kind};
use crate::font::UiFont;
use crate::root::UiRoot;
use crate::shapes::CornerRadii;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::Field;

use super::style;
use super::{Palette, PluginOutbox};

/// Where a control's handlers go: its panel and the outbox, and for a
/// control in a `<repeat>` row the row's steps its handlers are named after
/// ([`ac_plugin_ui::row`]), which the window keeps current as rows move.
#[derive(Debug, Clone)]
pub struct Emit {
    /// The panel.
    pub panel: u32,
    /// The plugin outbox.
    pub outbox: PluginOutbox,
    row: Option<Rc<RefCell<String>>>,
}

impl Emit {
    /// A window control's.
    #[must_use]
    pub fn new(panel: u32, outbox: PluginOutbox) -> Self {
        Self {
            panel,
            outbox,
            row: None,
        }
    }

    /// A row control's: its handlers are named after `row`'s text.
    #[must_use]
    pub fn row(panel: u32, outbox: PluginOutbox, row: Rc<RefCell<String>>) -> Self {
        Self {
            panel,
            outbox,
            row: Some(row),
        }
    }

    /// The name `handler` reaches the plugin as.
    #[must_use]
    pub fn handler_name(&self, handler: &str) -> String {
        match &self.row {
            Some(row) => format!("{}{handler}", row.borrow()),
            None => handler.to_owned(),
        }
    }

    /// Fires `handler` (when bound) with `value`.
    pub fn act(&self, handler: Option<&str>, value: ActionValue) {
        if let Some(handler) = handler {
            self.outbox.borrow_mut().push(UiInput::Action {
                panel: self.panel,
                handler: self.handler_name(handler),
                value,
            });
        }
    }
}

/// The lamp's unlit inner colour: the classic border of boxes, rings and
/// separators.
pub const UNLIT_INNER: Color = Color::rgba(0.38, 0.34, 0.23, 1.0);
/// The lamp's unlit outer colour: the classic split bar.
pub const UNLIT_OUTER: Color = Color::rgba(0.26, 0.22, 0.13, 1.0);
/// The lamp's lit inner colour: the classic tick and radio mark.
pub const LIT_INNER: Color = Color::rgba(0.52, 1.0, 0.08, 1.0);
/// The classic well inside a box or ring.
pub const CLASSIC_FILL: Color = Color::rgba(0.10, 0.09, 0.06, 1.0);
/// The outline a focused control draws.
pub const FOCUS: Color = Color::rgba(1.0, 0.82, 0.25, 1.0);
/// The classic arrow and spin glyph colour.
pub const CLASSIC_ARROW: Color = Color::rgba(0.80, 0.74, 0.55, 1.0);
/// The classic caption colour.
pub const CLASSIC_CAPTION: Color = Color::rgba(0.86, 0.84, 0.74, 1.0);
/// A Classic expander's frame.
pub const CLASSIC_FRAME: Color = Color::rgba(0.46, 0.41, 0.28, 1.0);
/// The band behind a Classic expander's header.
pub const CLASSIC_BAND: Color = Color::rgba(0.0, 0.0, 0.0, 0.30);
/// The alpha a disabled control fades to.
pub const DISABLED_ALPHA: f32 = super::widgets::DISABLED_ALPHA;

fn fade(mut c: Color, live: bool) -> Color {
    if !live {
        c.a *= DISABLED_ALPHA;
    }
    c
}

fn size(ui: &UiRoot, me: ElementId) -> (f32, f32) {
    ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height))
}

fn enabled(ui: &UiRoot, me: ElementId) -> bool {
    ui.element(me).is_some_and(|e| e.enabled)
}

/// A caption at `x`, vertically centred in `h` (OpenAC `UiCaption.Draw`).
#[allow(clippy::too_many_arguments)]
fn caption(
    rc: &mut RenderContext<'_>,
    font: Option<&Arc<UiFont>>,
    s: &str,
    x: f32,
    y: f32,
    h: f32,
    color: Color,
    outline: bool,
) {
    if let Some(font) = font {
        let top = y + ((h - font.line_height) * 0.5).floor();
        rc.string_dat(
            font,
            s,
            x,
            top,
            color,
            outline,
            crate::draw::DEFAULT_OUTLINE_COLOR,
        );
    }
}

fn caption_centred(
    rc: &mut RenderContext<'_>,
    font: Option<&Arc<UiFont>>,
    s: &str,
    cell: (f32, f32, f32, f32),
    color: Color,
    outline: bool,
) {
    if let Some(font) = font {
        let (x, y, w, h) = cell;
        let left = x + ((w - font.measure_width(s)) * 0.5).floor().max(0.0);
        caption(rc, Some(font), s, left, y, h, color, outline);
    }
}

/// Keyboard focus on a tab stop, Enter or Space activating it (OpenAC
/// `UiKeyboardActivation`): `Some(activate)` for an event it took.
fn keyboard_activation(
    ui: &UiRoot,
    me: ElementId,
    e: &UiEvent,
    focused: &mut bool,
    live: bool,
) -> Option<bool> {
    if e.target != Some(me) || !ui.element(me).is_some_and(|el| el.tab_stop) {
        return None;
    }
    match e.kind {
        kind::FOCUS_GAINED => {
            *focused = true;
            Some(false)
        }
        kind::FOCUS_LOST => {
            *focused = false;
            Some(false)
        }
        kind::KEY_DOWN if live && matches!(e.key, Key::Enter | Key::KeypadEnter | Key::Space) => {
            Some(true)
        }
        _ => None,
    }
}

// ── Checkbox ──────────────────────────────────────────────────────────────

/// The checkbox's 11 px square (OpenAC `UiCheckBoxGlyph`): empty, a pixel
/// tick, or a dash for indeterminate.
pub const CHECK_BOX_SIZE: f32 = 11.0;

const TICK: [(f32, f32); 7] = [
    (2.0, 5.0),
    (3.0, 6.0),
    (4.0, 7.0),
    (5.0, 6.0),
    (6.0, 5.0),
    (7.0, 4.0),
    (8.0, 3.0),
];

/// Draws the checkbox square.
pub fn draw_check_box(
    rc: &mut RenderContext<'_>,
    x: f32,
    y: f32,
    state: Option<bool>,
    palette: Option<&Palette>,
) {
    if let Some(p) = palette {
        match state {
            Some(on) => style::check(rc, p, x, y, on),
            None => {
                let (size, radius) = (CHECK_BOX_SIZE, style::SMALL_RADIUS);
                style::surface(rc, x, y, size, size, radius, p.accent, p.accent);
                rc.smooth_line(x + 3.0, y + 5.5, x + 8.0, y + 5.5, p.field, 1.5);
            }
        }
        return;
    }
    let mark = LIT_INNER;
    rc.fill(x, y, CHECK_BOX_SIZE, CHECK_BOX_SIZE, CLASSIC_FILL);
    rc.rect_outline(x, y, CHECK_BOX_SIZE, CHECK_BOX_SIZE, UNLIT_INNER, 1.0);
    match state {
        None => rc.fill(x + 3.0, y + 4.0, 5.0, 3.0, mark),
        Some(true) => {
            for (dx, dy) in TICK {
                rc.fill(x + dx, y + dy, 2.0, 2.0, mark);
            }
        }
        Some(false) => {}
    }
}

/// A tick box and caption that reports the value a click asks for
/// (OpenAC `UiMarkupCheckbox`).
#[derive(Debug)]
pub struct MarkupCheckbox {
    /// The caption.
    pub text: String,
    /// The bound state (none: indeterminate).
    pub state: Option<bool>,
    /// Caption colour.
    pub color: Color,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// The `onchange` handler.
    pub onchange: Option<String>,
    focused: bool,
    emit: Emit,
}

impl MarkupCheckbox {
    /// A checkbox for `emit`'s panel.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>, onchange: Option<String>, emit: Emit) -> Self {
        Self {
            text: String::new(),
            state: Some(false),
            color: CLASSIC_CAPTION,
            font,
            palette: None,
            onchange,
            focused: false,
            emit,
        }
    }

    /// Unchecked and indeterminate become checked; checked becomes
    /// unchecked.
    #[must_use]
    pub fn next_value(state: Option<bool>) -> bool {
        state != Some(true)
    }

    fn activate(&self, ui: &UiRoot, me: ElementId) {
        if enabled(ui, me) {
            self.emit.act(
                self.onchange.as_deref(),
                ActionValue::Bool(Self::next_value(self.state)),
            );
        }
    }
}

impl Widget for MarkupCheckbox {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let live = enabled(ui, me);
        if let Some(activate) = keyboard_activation(ui, me, e, &mut self.focused, live) {
            if activate {
                self.activate(ui, me);
            }
            return true;
        }
        if e.target != Some(me) || e.kind != kind::CLICK {
            return false;
        }
        self.activate(ui, me);
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        if self.focused {
            match &self.palette {
                Some(p) => style::focus_ring(rc, p, w, h, style::SMALL_RADIUS),
                None => rc.rect_outline(0.0, 0.0, w, h, FOCUS, 1.0),
            }
        }
        let y = ((h - CHECK_BOX_SIZE) * 0.5).max(1.0);
        if !live {
            rc.push_alpha(DISABLED_ALPHA);
        }
        draw_check_box(rc, 1.0, y, self.state, self.palette.as_ref());
        if !live {
            rc.pop_alpha();
        }
        let mut color = self.color;
        if !live {
            color.a = DISABLED_ALPHA;
        }
        caption(
            rc,
            self.font.as_ref(),
            &self.text,
            17.0,
            0.0,
            h,
            color,
            self.palette.is_none(),
        );
    }
}

// ── Separator ─────────────────────────────────────────────────────────────

/// A 1 px line across the middle of its box (OpenAC `UiMarkupSeparator`):
/// vertical when the box is taller than wide. Click-through.
#[derive(Debug, Clone)]
pub struct MarkupSeparator {
    /// The line's colour.
    pub color: Color,
}

impl Default for MarkupSeparator {
    fn default() -> Self {
        Self { color: UNLIT_INNER }
    }
}

impl Widget for MarkupSeparator {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        if w <= 0.0 || h <= 0.0 {
            return;
        }
        if h > w {
            rc.fill(((w - 1.0) * 0.5).floor(), 0.0, 1.0, h, self.color);
        } else {
            rc.fill(0.0, ((h - 1.0) * 0.5).floor(), w, 1.0, self.color);
        }
    }
}

// ── Radio ─────────────────────────────────────────────────────────────────

/// The classic segment face.
pub const SEGMENT_FACE: Color = Color::rgba(0.16, 0.14, 0.10, 1.0);
/// The classic chosen segment.
pub const SEGMENT_SELECTED: Color = Color::rgba(0.32, 0.27, 0.15, 1.0);

/// The radio button's 11 px ring (OpenAC `UiRadioGlyph`).
fn draw_radio_glyph(
    rc: &mut RenderContext<'_>,
    x: f32,
    y: f32,
    selected: bool,
    palette: Option<&Palette>,
    alpha: f32,
) {
    let a = |mut c: Color| {
        c.a *= alpha;
        c
    };
    let ring = a(match palette {
        Some(p) if selected => p.accent,
        Some(p) => p.border,
        None => UNLIT_INNER,
    });
    let well = a(palette.map_or(CLASSIC_FILL, |p| p.field));
    let mark = a(palette.map_or(LIT_INNER, |p| p.accent));
    if palette.is_some() {
        rc.fill_ellipse(x, y, 11.0, 11.0, ring);
        rc.fill_ellipse(x + 1.0, y + 1.0, 9.0, 9.0, well);
        if selected {
            rc.fill_ellipse(x + 3.0, y + 3.0, 5.0, 5.0, mark);
        }
        return;
    }
    rc.fill(x + 3.0, y, 5.0, 1.0, ring);
    rc.fill(x + 1.0, y + 1.0, 9.0, 2.0, ring);
    rc.fill(x, y + 3.0, 11.0, 5.0, ring);
    rc.fill(x + 1.0, y + 8.0, 9.0, 2.0, ring);
    rc.fill(x + 3.0, y + 10.0, 5.0, 1.0, ring);
    rc.fill(x + 3.0, y + 2.0, 5.0, 1.0, well);
    rc.fill(x + 2.0, y + 3.0, 7.0, 5.0, well);
    rc.fill(x + 3.0, y + 8.0, 5.0, 1.0, well);
    if selected {
        rc.fill(x + 4.0, y + 4.0, 3.0, 3.0, mark);
    }
}

/// One choice of several, as round buttons or a segmented bar (OpenAC
/// `UiMarkupRadio`): the box is split equally into one cell per option.
#[derive(Debug)]
pub struct MarkupRadio {
    /// The options.
    pub items: Vec<String>,
    /// The chosen option (−1 none).
    pub selected: i32,
    /// Side by side.
    pub horizontal: bool,
    /// A joined bar.
    pub segmented: bool,
    /// Caption colour.
    pub color: Color,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// The `onchange` handler.
    pub onchange: Option<String>,
    focused: bool,
    pressed: i32,
    emit: Emit,
}

/// Where cell `i` of `count` starts along a side of `size`, on a whole
/// pixel.
#[must_use]
pub fn radio_edge(i: usize, size: f32, count: usize) -> f32 {
    if i >= count {
        size
    } else {
        (i as f32 * size / count as f32).floor()
    }
}

impl MarkupRadio {
    /// A radio group for `emit`'s panel.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>, onchange: Option<String>, emit: Emit) -> Self {
        Self {
            items: Vec::new(),
            selected: -1,
            horizontal: false,
            segmented: false,
            color: CLASSIC_CAPTION,
            font,
            palette: None,
            onchange,
            focused: false,
            pressed: -1,
            emit,
        }
    }

    /// The option under a local point, or −1.
    #[must_use]
    pub fn index_at(&self, w: f32, h: f32, x: f32, y: f32) -> i32 {
        let count = self.items.len();
        if count == 0 || x < 0.0 || y < 0.0 || x >= w || y >= h {
            return -1;
        }
        let (along, size) = if self.horizontal { (x, w) } else { (y, h) };
        (0..count)
            .find(|i| {
                along >= radio_edge(*i, size, count) && along < radio_edge(i + 1, size, count)
            })
            .map_or(-1, |i| i as i32)
    }

    fn select(&self, live: bool, index: i32) {
        if !live || index < 0 || index as usize >= self.items.len() || index == self.selected {
            return;
        }
        self.emit
            .act(self.onchange.as_deref(), ActionValue::Int(index));
    }

    /// One option along, wrapping; from none, the first or the last.
    fn step(&self, live: bool, delta: i32) {
        let count = self.items.len() as i32;
        if count == 0 {
            return;
        }
        let next = if (0..count).contains(&self.selected) {
            (self.selected + delta + count) % count
        } else if delta > 0 {
            0
        } else {
            count - 1
        };
        self.select(live, next);
    }

    fn cell(&self, index: usize, w: f32, h: f32) -> (f32, f32, f32, f32) {
        let count = self.items.len();
        if self.horizontal {
            let x0 = radio_edge(index, w, count);
            (x0, 0.0, radio_edge(index + 1, w, count) - x0, h)
        } else {
            let y0 = radio_edge(index, h, count);
            (0.0, y0, w, radio_edge(index + 1, h, count) - y0)
        }
    }
}

impl Widget for MarkupRadio {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let live = enabled(ui, me);
        if keyboard_activation(ui, me, e, &mut self.focused, live).is_some() {
            return true;
        }
        if e.target != Some(me) {
            return false;
        }
        let (w, h) = size(ui, me);
        match e.kind {
            kind::MOUSE_DOWN => {
                self.pressed = if live {
                    self.index_at(w, h, e.data1 as f32, e.data2 as f32)
                } else {
                    -1
                };
                true
            }
            kind::MOUSE_UP => {
                if self.pressed >= 0
                    && self.index_at(w, h, e.data1 as f32, e.data2 as f32) == self.pressed
                {
                    self.select(live, self.pressed);
                }
                self.pressed = -1;
                true
            }
            kind::CAPTURE_CHANGED => {
                self.pressed = -1;
                true
            }
            kind::CLICK | kind::DOUBLE_CLICK => true,
            kind::KEY_DOWN if self.focused && live => match e.key {
                Key::Up | Key::Left => {
                    self.step(live, -1);
                    true
                }
                Key::Down | Key::Right => {
                    self.step(live, 1);
                    true
                }
                _ => false,
            },
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        let alpha = if live { 1.0 } else { DISABLED_ALPHA };
        let text = fade(self.color, live);
        let outline = self.palette.is_none();
        let p = self.palette.as_ref();
        if let (true, Some(p)) = (self.segmented, p) {
            // A rounded track, the chosen option a pill inside it.
            let (field, edge) = (fade(p.field, live), fade(p.border, live));
            style::surface(rc, 0.0, 0.0, w, h, style::CONTROL_RADIUS, field, edge);
            for (i, item) in self.items.iter().enumerate() {
                let (x, y, cw, ch) = self.cell(i, w, h);
                if i as i32 == self.selected {
                    rc.fill_rounded_rect(
                        x + 2.0,
                        y + 2.0,
                        cw - 4.0,
                        ch - 4.0,
                        CornerRadii::all(style::SMALL_RADIUS),
                        fade(p.selected, live),
                    );
                }
                rc.push_clip(x, y, cw, ch);
                caption_centred(rc, self.font.as_ref(), item, (x, y, cw, ch), text, false);
                rc.pop_clip();
            }
        } else if self.segmented {
            let face = fade(p.map_or(SEGMENT_FACE, |p| p.field), live);
            let chosen = fade(p.map_or(SEGMENT_SELECTED, |p| p.selected), live);
            let border = fade(p.map_or(UNLIT_INNER, |p| p.border), live);
            for (i, item) in self.items.iter().enumerate() {
                let (x, y, cw, ch) = self.cell(i, w, h);
                rc.fill(
                    x,
                    y,
                    cw,
                    ch,
                    if i as i32 == self.selected {
                        chosen
                    } else {
                        face
                    },
                );
                if i > 0 {
                    if self.horizontal {
                        rc.fill(x, y, 1.0, ch, border);
                        rc.push_clip(x + 1.0, y, cw - 1.0, ch);
                    } else {
                        rc.fill(x, y, cw, 1.0, border);
                        rc.push_clip(x, y + 1.0, cw, ch - 1.0);
                    }
                } else {
                    rc.push_clip(x, y, cw, ch);
                }
                caption_centred(rc, self.font.as_ref(), item, (x, y, cw, ch), text, outline);
                rc.pop_clip();
            }
            rc.rect_outline(0.0, 0.0, w, h, border, 1.0);
        } else {
            for (i, item) in self.items.iter().enumerate() {
                let (x, y, cw, ch) = self.cell(i, w, h);
                rc.push_clip(x, y, cw, ch);
                let gy = y + ((ch - 11.0) * 0.5).max(1.0);
                draw_radio_glyph(rc, x + 1.0, gy, i as i32 == self.selected, p, alpha);
                caption(rc, self.font.as_ref(), item, x + 17.0, y, ch, text, outline);
                rc.pop_clip();
            }
        }
        if self.focused {
            match p {
                Some(p) if self.segmented => {
                    style::focus_ring(rc, p, w, h, style::CONTROL_RADIUS);
                }
                Some(p) => style::focus_ring(rc, p, w, h, style::SMALL_RADIUS),
                None => rc.rect_outline(0.0, 0.0, w, h, FOCUS, 1.0),
            }
        }
    }
}

// ── Number ────────────────────────────────────────────────────────────────

/// The spin buttons' width at the right edge.
pub const SPIN_WIDTH: f32 = 14.0;
/// How long a held spin waits before it repeats, seconds.
pub const REPEAT_DELAY: f64 = 0.40;
/// How often a held spin repeats, seconds.
pub const REPEAT_RATE: f64 = 0.06;
/// Typed text that is not a valid number draws in this colour.
pub const NUMBER_ERROR: Color = Color::rgba(0.95, 0.38, 0.32, 1.0);
/// The classic spin face.
pub const SPIN_FACE: Color = Color::rgba(0.16, 0.14, 0.10, 1.0);
/// The classic held spin.
pub const SPIN_PRESSED: Color = Color::rgba(0.26, 0.23, 0.16, 1.0);

/// A number field with spin buttons (OpenAC `UiMarkupNumber`): the text
/// field, which only ever reports a valid number, with the spin buttons
/// drawn over its right edge.
pub struct MarkupNumber {
    /// The text field.
    pub inner: Field,
    /// Range, step, decimals.
    pub spec: NumberSpec,
    /// The bound value.
    pub current: Dec,
    /// An `int` (else a `float`).
    pub integral: bool,
    /// The bound text colour.
    pub text_color: Color,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    onchange: String,
    emit: Emit,
    cancelling: bool,
    spin: i64,
    spin_timer: f64,
    shift: bool,
}

impl std::fmt::Debug for MarkupNumber {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MarkupNumber")
            .field("current", &self.current)
            .finish_non_exhaustive()
    }
}

impl MarkupNumber {
    /// A number field reporting to `onchange`.
    #[must_use]
    pub fn new(
        inner: Field,
        spec: NumberSpec,
        integral: bool,
        onchange: String,
        emit: Emit,
    ) -> Self {
        Self {
            inner,
            spec,
            current: Dec::ZERO,
            integral,
            text_color: CLASSIC_CAPTION,
            palette: None,
            onchange,
            emit,
            cancelling: false,
            spin: 0,
            spin_timer: 0.0,
            shift: false,
        }
    }

    fn report(&self, value: Option<Dec>) {
        let Some(value) = value else {
            return;
        };
        let arg = if self.integral {
            ActionValue::Int(value.to_i32())
        } else {
            ActionValue::Float(value.to_f32())
        };
        self.emit.act(Some(&self.onchange), arg);
    }

    fn show_value(&mut self) {
        let text = self.spec.format(self.current);
        self.inner.set_text(&text);
    }

    /// Follows the bound value while nobody types in it.
    pub fn set_current(&mut self, value: Dec) {
        self.current = value;
        let text = self.spec.format(value);
        self.inner.follow_bound_text(&text);
    }

    fn commit(&mut self, text: &str) {
        let report = self.spec.commit(text, self.current);
        self.report(report);
        self.show_value();
    }

    fn step(&mut self, live: bool, direction: i64) {
        if !live {
            return;
        }
        let typed = self.inner.is_focused().then(|| self.inner.text());
        let report = self
            .spec
            .step_by(direction, self.shift, typed.as_deref(), self.current);
        self.report(report);
        self.show_value();
    }
}

impl Widget for MarkupNumber {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return self.inner.on_event(ui, me, e);
        }
        self.shift = e.modifiers.contains(Modifiers::SHIFT);
        let live = enabled(ui, me);
        let (w, h) = size(ui, me);
        match e.kind {
            kind::KEY_DOWN => match e.key {
                Key::Up | Key::Down if live => {
                    self.step(live, if e.key == Key::Up { 1 } else { -1 });
                    return true;
                }
                Key::Escape | Key::Enter | Key::KeypadEnter => {
                    // The field lets go of the keyboard itself, so its focus
                    // loss never reaches this widget: Enter commits, Escape
                    // puts the bound value back.
                    let text = self.inner.text();
                    let was_focused = self.inner.is_focused();
                    let handled = self.inner.on_event(ui, me, e);
                    if was_focused && !self.inner.is_focused() {
                        if e.key == Key::Escape {
                            self.show_value();
                        } else {
                            self.commit(&text);
                        }
                    }
                    return handled;
                }
                _ => {}
            },
            kind::CHAR => {
                if let Some(c) = char::from_u32(e.data0 as u32)
                    && !c.is_control()
                    && !self.spec.accepts_character(c)
                {
                    return true;
                }
            }
            kind::FOCUS_LOST => {
                let handled = self.inner.on_event(ui, me, e);
                if std::mem::take(&mut self.cancelling) {
                    self.show_value();
                } else {
                    let text = self.inner.text();
                    self.commit(&text);
                }
                return handled;
            }
            kind::SCROLL => {
                if !live || !self.inner.is_focused() || e.data0 == 0 {
                    return false;
                }
                self.step(live, i64::from(e.data0.signum()));
                return true;
            }
            kind::MOUSE_DOWN if e.data1 as f32 >= w - SPIN_WIDTH => {
                if !live {
                    return true;
                }
                // A spin ends typing: the typed text is committed first.
                if self.inner.is_focused() {
                    let text = self.inner.text();
                    ui.set_keyboard_focus(None);
                    let lost = UiEvent::new(0, me, kind::FOCUS_LOST);
                    self.inner.on_event(ui, me, &lost);
                    self.commit(&text);
                }
                self.spin = if (e.data2 as f32) < (h * 0.5).floor() {
                    1
                } else {
                    -1
                };
                self.spin_timer = REPEAT_DELAY;
                self.step(live, self.spin);
                return true;
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED if self.spin != 0 => {
                self.spin = 0;
                return true;
            }
            _ => {}
        }
        self.inner.on_event(ui, me, e)
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, dt: f64) {
        self.inner.tick(ui, me, dt);
        let live = enabled(ui, me);
        if self.inner.editable() != live {
            if !live && self.inner.is_focused() {
                self.cancelling = true;
            }
            self.inner.set_editable(ui, me, live);
        }
        if self.spin == 0 {
            return;
        }
        if !live {
            self.spin = 0;
            return;
        }
        self.spin_timer -= dt;
        if self.spin_timer <= 0.0 {
            self.step(live, self.spin);
            self.spin_timer = REPEAT_RATE;
        }
    }

    fn global_time(&mut self, ui: &mut UiRoot, me: ElementId, now: f64) {
        self.inner.global_time(ui, me, now);
    }

    fn handles_click(&self, element: &Element) -> bool {
        self.inner.handles_click(element)
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.inner.receives_hover_mouse_move()
    }

    fn scroll_model(&mut self) -> Option<&mut crate::text::Scrollable> {
        self.inner.scroll_model()
    }

    fn on_enabled_changed(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.inner.on_enabled_changed(ui, me);
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        let valid = self.spec.is_valid_text(&self.inner.text());
        let color = if valid { self.text_color } else { NUMBER_ERROR };
        let x = w - SPIN_WIDTH;
        let half = (h * 0.5).floor();
        if let Some(p) = self.palette {
            // The field stops at the spin buttons, which are ghosts beside it:
            // a pressed face, and chevrons.
            self.inner.text_color = color;
            if !live {
                rc.push_alpha(style::DISABLED_ALPHA);
            }
            self.inner.draw_detached(x.max(0.0), h, rc);
            if !live {
                rc.pop_alpha();
            }
            let face = fade(style::pressed(style::hover(&p, p.field)), live);
            let radii = CornerRadii::all(style::SMALL_RADIUS);
            if self.spin > 0 {
                rc.fill_rounded_rect(x + 1.0, 1.0, SPIN_WIDTH - 2.0, half - 1.0, radii, face);
            } else if self.spin < 0 {
                let height = h - half - 1.0;
                rc.fill_rounded_rect(x + 1.0, half, SPIN_WIDTH - 2.0, height, radii, face);
            }
            let chevron = fade(p.muted, live);
            let cx = x + (SPIN_WIDTH - 6.0) * 0.5;
            let up = (half * 0.5).floor() - 1.5;
            style::chevron_up(rc, cx, up, 6.0, 3.0, chevron);
            let down = (half + (h - half) * 0.5).floor() - 1.5;
            style::chevron(rc, cx, down, 6.0, 3.0, chevron);
            return;
        }
        self.inner.text_color = fade(color, live);
        rc.push_clip(0.0, 0.0, (w - SPIN_WIDTH).max(0.0), h);
        self.inner.draw(ui, me, rc);
        rc.pop_clip();
        let p = self.palette.as_ref();
        let face = fade(p.map_or(SPIN_FACE, |p| p.field), live);
        let pressed = fade(p.map_or(SPIN_PRESSED, |p| p.selected), live);
        let border = fade(p.map_or(UNLIT_INNER, |p| p.border), live);
        let arrow = fade(p.map_or(CLASSIC_ARROW, |p| p.text), live);
        rc.fill(
            x,
            0.0,
            SPIN_WIDTH,
            half,
            if self.spin > 0 { pressed } else { face },
        );
        rc.fill(
            x,
            half,
            SPIN_WIDTH,
            h - half,
            if self.spin < 0 { pressed } else { face },
        );
        rc.rect_outline(x, 0.0, SPIN_WIDTH, h, border, 1.0);
        rc.fill(x, half, SPIN_WIDTH, 1.0, border);
        let centre = x + SPIN_WIDTH * 0.5;
        draw_arrow(rc, centre, half * 0.5, true, arrow);
        draw_arrow(rc, centre, half + (h - half) * 0.5, false, arrow);
    }

    fn draw_after_children(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        self.inner.draw_after_children(ui, me, rc);
    }
}

/// A pixel triangle three rows tall (1, 3 and 5 px wide), centred on
/// (`cx`, `cy`).
fn draw_arrow(rc: &mut RenderContext<'_>, cx: f32, cy: f32, up: bool, color: Color) {
    let top = (cy - 1.5).floor();
    for row in 0..3u8 {
        let width = if up {
            1.0 + f32::from(row) * 2.0
        } else {
            5.0 - f32::from(row) * 2.0
        };
        rc.fill(
            (cx - width * 0.5).floor(),
            top + f32::from(row),
            width,
            1.0,
            color,
        );
    }
}

// ── Hotkey ────────────────────────────────────────────────────────────────

/// What a recording box shows.
pub const HOTKEY_PROMPT: &str = "Press a key...";
/// What an unbound box shows.
pub const HOTKEY_UNBOUND: &str = "None";
/// The classic box fill.
pub const HOTKEY_BACKGROUND: Color = Color::rgba(0.0, 0.0, 0.0, 0.9);
/// The classic text.
pub const HOTKEY_TEXT: Color = Color::rgba(0.91, 0.87, 0.76, 1.0);
/// The classic muted text.
pub const HOTKEY_MUTED: Color = Color::rgba(0.5, 0.5, 0.5, 1.0);

/// A box that records a key chord (OpenAC `UiMarkupHotkey`): a click, or
/// Enter or Space while focused, starts recording; the first key a plugin
/// hotkey can hold ends it and the plugin hears the chord's text.
#[derive(Debug)]
pub struct MarkupHotkey {
    /// The bound text.
    pub value: String,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// Recording.
    pub capturing: bool,
    focused: bool,
    release_focus: bool,
    start_key: Option<Key>,
    end_key: Option<Key>,
    onchange: String,
    emit: Emit,
}

impl MarkupHotkey {
    /// A hotkey box reporting to `onchange`.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>, onchange: String, emit: Emit) -> Self {
        Self {
            value: String::new(),
            font,
            palette: None,
            capturing: false,
            focused: false,
            release_focus: false,
            start_key: None,
            end_key: None,
            onchange,
            emit,
        }
    }

    /// The prompt while recording, else the bound chord or "None".
    #[must_use]
    pub fn display_text(&self) -> String {
        if self.capturing {
            return HOTKEY_PROMPT.to_owned();
        }
        ac_plugin_ui::hotkeys::Chord::parse_hotkey_text(&self.value)
            .map_or_else(|| HOTKEY_UNBOUND.to_owned(), |c| c.to_hotkey_text())
    }

    fn begin(&mut self, live: bool, release_focus: bool, start_key: Option<Key>) {
        if !live || self.capturing {
            return;
        }
        self.capturing = true;
        self.release_focus = release_focus;
        self.start_key = start_key;
        self.end_key = None;
    }

    fn end(&mut self, ui: &mut UiRoot, me: ElementId, end_key: Option<Key>) {
        self.capturing = false;
        self.start_key = None;
        if !self.release_focus {
            return;
        }
        match end_key {
            None => self.give_back(ui, me),
            Some(key) => self.end_key = Some(key),
        }
    }

    fn give_back(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.release_focus = false;
        self.end_key = None;
        if ui.keyboard_focus() == Some(me) {
            ui.set_keyboard_focus(None);
        }
    }

    fn report(&self, value: &str) {
        if value != self.value {
            self.emit
                .act(Some(&self.onchange), ActionValue::Text(value.to_owned()));
        }
    }

    fn key_down(&mut self, ui: &mut UiRoot, me: ElementId, key: Key, modifiers: Modifiers) -> bool {
        let live = enabled(ui, me);
        if !self.capturing {
            if !live || !matches!(key, Key::Enter | Key::KeypadEnter | Key::Space) {
                return false;
            }
            self.begin(live, false, Some(key));
            return true;
        }
        if Some(key) == self.start_key {
            return true;
        }
        match key {
            Key::Escape => self.end(ui, me, Some(key)),
            Key::Backspace | Key::Delete => {
                self.end(ui, me, Some(key));
                self.report("");
            }
            _ => {
                let code = super::hotkeys::plugin_key(key);
                if code == 0 || modifiers.contains(Modifiers::WIN) {
                    return true;
                }
                self.end(ui, me, Some(key));
                let chord = ac_plugin_ui::hotkeys::Chord {
                    key: code,
                    ctrl: modifiers.contains(Modifiers::CTRL),
                    alt: modifiers.contains(Modifiers::ALT),
                    shift: modifiers.contains(Modifiers::SHIFT),
                };
                self.report(&chord.to_hotkey_text());
            }
        }
        true
    }
}

impl Widget for MarkupHotkey {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        match e.kind {
            kind::FOCUS_GAINED => {
                self.focused = true;
                true
            }
            kind::FOCUS_LOST => {
                self.focused = false;
                self.capturing = false;
                self.start_key = None;
                self.end_key = None;
                self.release_focus = false;
                true
            }
            kind::CLICK => {
                let live = enabled(ui, me);
                self.begin(live, true, None);
                true
            }
            kind::KEY_DOWN => self.key_down(ui, me, e.key, e.modifiers),
            kind::KEY_UP => {
                if Some(e.key) == self.start_key {
                    self.start_key = None;
                    return true;
                }
                if Some(e.key) == self.end_key {
                    self.give_back(ui, me);
                    return true;
                }
                self.capturing
            }
            kind::CHAR => self.capturing,
            _ => false,
        }
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        if self.capturing && !enabled(ui, me) {
            self.end(ui, me, None);
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        let p = self.palette.as_ref();
        let focus = p.map_or(FOCUS, |p| p.accent);
        let lit = self.focused || self.capturing;
        if let Some(p) = p {
            if !live {
                rc.push_alpha(style::DISABLED_ALPHA);
            }
            style::field(rc, p, w, h, p.field, lit);
            if !live {
                rc.pop_alpha();
            }
        } else {
            rc.fill(0.0, 0.0, w, h, fade(HOTKEY_BACKGROUND, live));
            let edge = if lit { focus } else { UNLIT_INNER };
            rc.rect_outline(0.0, 0.0, w, h, fade(edge, live), 1.0);
        }
        let bound = ac_plugin_ui::hotkeys::Chord::parse_hotkey_text(&self.value).is_some();
        let color = if self.capturing {
            focus
        } else if bound {
            p.map_or(HOTKEY_TEXT, |p| p.text)
        } else {
            p.map_or(HOTKEY_MUTED, |p| p.muted)
        };
        rc.push_clip(1.0, 1.0, w - 2.0, h - 2.0);
        caption(
            rc,
            self.font.as_ref(),
            &self.display_text(),
            4.0,
            0.0,
            h,
            fade(color, live),
            p.is_none(),
        );
        rc.pop_clip();
    }
}

#[cfg(test)]
mod tests;
