//! The markup controls that lay out other elements or pop up over them
//! (OpenAC `UiMarkupExpander`, `UiMarkupExpanderHeader`, `UiMarkupSplit`,
//! `UiMarkupSplitBar`, `UiMarkupContextMenu`), and what controls remember
//! for the character (a [`Remembered`] value in the window's
//! [`ControlStateStore`]).

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::ActionValue;
use ac_plugin_ui::control_state::{ControlState, ControlStateStore, RememberedValue};
use ac_plugin_ui::markup::{EXPANDER_HEADER_HEIGHT, SPLIT_BAR_SIZE};

use super::Palette;
use super::content_size::text_width;
use super::controls::{
    CLASSIC_ARROW, CLASSIC_BAND, CLASSIC_CAPTION, CLASSIC_FRAME, DISABLED_ALPHA, Emit, FOCUS,
    UNLIT_INNER, UNLIT_OUTER,
};
use super::style;
use super::widgets::MarkupLabel;
use crate::draw::{Color, RenderContext};
use crate::event::{Key, UiEvent, kind};
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::root::UiRoot;
use crate::shapes::CornerRadii;
use crate::tree::{Element, ElementId, Widget};

/// The window's remembered control values, shared by every plugin window.
pub type SharedControlState = Rc<RefCell<ControlStateStore>>;

/// One value a control remembers for the character in its window, or
/// nothing (no store, or an unnamed control).
#[derive(Debug, Clone)]
pub struct Remembered {
    store: Option<SharedControlState>,
    window: String,
    value: RememberedValue,
}

impl Remembered {
    /// The value `kind` of control `control` in window `window`.
    #[must_use]
    pub fn new(
        store: Option<SharedControlState>,
        window: &str,
        control: Option<&str>,
        kind: &'static str,
    ) -> Self {
        Self {
            store,
            window: window.to_owned(),
            value: RememberedValue::new(control, kind),
        }
    }

    /// The remembered text the first time and after a character switch
    /// (inner `None`: nothing remembered); `None` when nothing changed.
    pub fn refresh(&mut self) -> Option<Option<String>> {
        let store = self.store.as_ref()?;
        let mut store = store.borrow_mut();
        let mut state = store.window(&self.window);
        self.value.try_refresh(&mut state)
    }

    /// Remembers a choice; `None` forgets it.
    pub fn remember(&self, value: Option<&str>) {
        if let Some(store) = &self.store {
            let mut store = store.borrow_mut();
            let mut state = store.window(&self.window);
            self.value
                .remember(&mut state as &mut dyn ControlState, value);
        }
    }
}

fn size(ui: &UiRoot, me: ElementId) -> (f32, f32) {
    ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height))
}

/// Shown and enabled, with every ancestor.
fn available(ui: &UiRoot, mut id: ElementId) -> bool {
    loop {
        match ui.element(id) {
            Some(e) if e.visible && e.enabled => {}
            _ => return false,
        }
        match ui.parent(id) {
            Some(p) if p != ui.root() => id = p,
            _ => return true,
        }
    }
}

fn live(ui: &UiRoot, me: ElementId) -> bool {
    available(ui, me)
}

fn fade(mut c: Color, live: bool) -> Color {
    if !live {
        c.a *= DISABLED_ALPHA;
    }
    c
}

// ── Expander ──────────────────────────────────────────────────────────────

/// How far expanders above have moved each element up, per window: an
/// element's authored top is its current top plus this shift.
pub type Shifts = Rc<RefCell<HashMap<ElementId, f32>>>;

/// A sibling's box as the window authored it: every expander open, at the
/// parent's current size.
#[derive(Debug)]
struct AuthoredBox {
    id: ElementId,
    left: f32,
    right: f32,
    top: f32,
    bottom: f32,
    /// Anchored top and bottom: it grows into the room instead of moving its
    /// bottom.
    stretches: bool,
    /// An expander showing only its header.
    folded: bool,
    /// Shown: a hidden element takes no room, so it holds nothing up.
    shown: bool,
}

impl AuthoredBox {
    /// Shown, entirely above `other`, and not off to one side of it.
    fn is_above(&self, other: &Self) -> bool {
        self.shown
            && self.bottom <= other.top + 0.5
            && self.left < other.right
            && other.left < self.right
    }

    /// Where its bottom lands when moved up by `shift`.
    fn placed_bottom(&self, shift: f32) -> f32 {
        if self.folded {
            self.top - shift + EXPANDER_HEADER_HEIGHT
        } else if self.stretches {
            self.bottom
        } else {
            self.bottom - shift
        }
    }
}

/// A header that folds away the content below it (OpenAC
/// `UiMarkupExpander`). Folded, what is below it moves up, keeping its authored
/// gap to the lowest thing still above it (stretched siblings grow instead),
/// and comes back when it opens. Expanders side by side therefore give back
/// only the room they all left free, and each column follows its own. It is
/// framed like a group box, with a band behind the header, in the theme's
/// colours or Classic's; `border="false"` leaves that out.
#[derive(Debug)]
pub struct MarkupExpander {
    /// The header element.
    pub header: ElementId,
    /// The content element (the authored children's parent).
    pub content: ElementId,
    /// The bound state, when the plugin owns it.
    pub bound: Option<bool>,
    /// The markup's `open`.
    pub open_by_default: bool,
    /// A flex container places it: folding only hides the content.
    pub flex_placed: bool,
    /// The markup's `border`: draws the frame and header band.
    pub framed: bool,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    host: Option<bool>,
    remembered: Remembered,
    laid_out: bool,
    collapse_delta: f32,
    expanded_anchors: Option<AnchorEdges>,
    parent_height_at_collapse: f32,
    shifts: Shifts,
    /// The siblings hidden when it last looked, to place them afresh when
    /// one is shown or hidden.
    hidden_siblings: Vec<ElementId>,
    onchange: Option<String>,
    emit: Emit,
}

impl MarkupExpander {
    /// An expander whose parts are `header` and `content`.
    #[must_use]
    pub fn new(
        header: ElementId,
        content: ElementId,
        open_by_default: bool,
        remembered: Remembered,
        shifts: Shifts,
        onchange: Option<String>,
        emit: Emit,
    ) -> Self {
        Self {
            header,
            content,
            bound: None,
            open_by_default,
            flex_placed: false,
            framed: true,
            palette: None,
            host: None,
            remembered,
            laid_out: true,
            collapse_delta: 0.0,
            expanded_anchors: None,
            parent_height_at_collapse: 0.0,
            shifts,
            hidden_siblings: Vec::new(),
            onchange,
            emit,
        }
    }

    /// The state asked for: bound, else remembered or the markup's.
    pub fn expanded(&mut self) -> bool {
        if let Some(bound) = self.bound {
            return bound;
        }
        if let Some(text) = self.remembered.refresh() {
            self.host = Some(match text.as_deref() {
                Some("true") => true,
                Some("false") => false,
                _ => self.open_by_default,
            });
        }
        *self.host.get_or_insert(self.open_by_default)
    }

    /// The state the layout is in (what the arrow shows).
    #[must_use]
    pub fn laid_out_expanded(&self) -> bool {
        self.laid_out
    }

    /// How tall the frame is in a box `height` tall: folded it is just the
    /// header, even where a flex container keeps the height.
    fn frame_height(&self, height: f32) -> f32 {
        if self.laid_out {
            height
        } else {
            height.min(EXPANDER_HEADER_HEIGHT)
        }
    }

    /// A header click or Enter/Space.
    pub fn toggle(&mut self, ui: &mut UiRoot, me: ElementId) {
        if !live(ui, me) {
            return;
        }
        let next = !self.expanded();
        if self.bound.is_some() {
            self.emit
                .act(self.onchange.as_deref(), ActionValue::Bool(next));
            return;
        }
        self.host = Some(next);
        let remember =
            (next != self.open_by_default).then_some(if next { "true" } else { "false" });
        self.remembered.remember(remember);
        self.apply_layout(ui, me, next);
        self.emit
            .act(self.onchange.as_deref(), ActionValue::Bool(next));
    }

    fn shift_of(&self, e: ElementId) -> f32 {
        self.shifts.borrow().get(&e).copied().unwrap_or(0.0)
    }

    /// The height it opens to in a parent `parent_height` tall.
    fn open_height(&self, parent_height: f32) -> f32 {
        let resized = if self.expanded_anchors.is_some() {
            parent_height - self.parent_height_at_collapse
        } else {
            0.0
        };
        EXPANDER_HEADER_HEIGHT + self.collapse_delta + resized
    }

    fn authored_box(
        &self,
        ui: &UiRoot,
        me: ElementId,
        id: ElementId,
        parent_height: f32,
    ) -> Option<AuthoredBox> {
        let el = ui.element(id)?;
        // This expander's own widget is out of the tree while it runs.
        let expander = if id == me {
            Some(self)
        } else {
            ui.widget::<Self>(id)
        };
        let anchors = expander
            .and_then(|x| x.expanded_anchors)
            .unwrap_or(el.anchors);
        let stretches = anchors.contains(AnchorEdges::TOP | AnchorEdges::BOTTOM);
        let open = expander
            .filter(|x| !x.laid_out)
            .map(|x| x.open_height(parent_height));
        let height = open.unwrap_or(el.height);
        let top = el.top + self.shift_of(id);
        // A label sized by its text measures itself only when drawn, and the
        // anchor pass before placing resets that width to the one it was
        // built with (none); its text says how far it reaches.
        let width = ui
            .widget::<MarkupLabel>(id)
            .filter(|label| !label.sized_by_layout)
            .map_or(el.width, |label| {
                text_width(label.font.as_ref(), &label.text)
            });
        Some(AuthoredBox {
            id,
            left: el.left,
            right: el.left + width.max(1.0),
            top,
            // A stretched sibling's growth already keeps its bottom in place.
            bottom: if stretches { el.top } else { top } + height,
            stretches,
            folded: open.is_some(),
            shown: el.visible,
        })
    }

    fn grow(ui: &mut UiRoot, e: ElementId, delta: f32) {
        let handled = ui
            .with::<Self, _>(e, |x, _| {
                if x.expanded_anchors.is_some() {
                    x.collapse_delta = (x.collapse_delta + delta).max(0.0);
                    true
                } else {
                    false
                }
            })
            .unwrap_or(false);
        if !handled && let Some(el) = ui.element_mut(e) {
            el.height = (el.height + delta).max(0.0);
        }
    }

    /// Places every sibling afresh from its authored box, so where it lands
    /// depends only on which expanders are folded now, not on the order they
    /// folded and opened in.
    fn place_siblings(&mut self, ui: &mut UiRoot, me: ElementId, parent: ElementId) {
        let parent_height = size(ui, parent).1;
        let mut boxes: Vec<AuthoredBox> = ui
            .children(parent)
            .into_iter()
            .filter_map(|id| self.authored_box(ui, me, id, parent_height))
            .collect();
        boxes.sort_by(|a, b| a.top.total_cmp(&b.top).then(a.bottom.total_cmp(&b.bottom)));
        let mut placed: Vec<(AuthoredBox, f32)> = Vec::with_capacity(boxes.len());
        for b in boxes {
            let above = placed.iter().filter(|(a, _)| a.is_above(&b));
            let authored = above.clone().map(|(a, _)| a.bottom).reduce(f32::max);
            let now = above.map(|(_, bottom)| *bottom).reduce(f32::max);
            let shift = authored.zip(now).map_or(0.0, |(a, n)| (a - n).max(0.0));
            self.move_sibling(ui, me, &b, shift);
            let bottom = b.placed_bottom(shift);
            placed.push((b, bottom));
        }
    }

    /// A sibling shown or hidden changes what holds up what is below it, so
    /// every sibling is placed afresh when one is.
    fn follow_shown_siblings(&mut self, ui: &mut UiRoot, me: ElementId) {
        if self.flex_placed {
            return;
        }
        let Some(parent) = ui.parent(me) else {
            return;
        };
        let hidden: Vec<ElementId> = ui
            .children(parent)
            .into_iter()
            .filter(|&id| ui.element(id).is_some_and(|e| !e.visible))
            .collect();
        if hidden == self.hidden_siblings {
            return;
        }
        self.hidden_siblings = hidden;
        let (pw, ph) = size(ui, parent);
        for child in ui.children(parent) {
            ui.apply_anchor(child, pw, ph);
        }
        self.place_siblings(ui, me, parent);
    }

    fn move_sibling(&mut self, ui: &mut UiRoot, me: ElementId, b: &AuthoredBox, shift: f32) {
        let delta = shift - self.shift_of(b.id);
        if delta.abs() < f32::EPSILON {
            return;
        }
        if let Some(el) = ui.element_mut(b.id) {
            el.top -= delta;
        }
        if b.stretches {
            if b.id == me && self.expanded_anchors.is_some() {
                self.collapse_delta = (self.collapse_delta + delta).max(0.0);
            } else {
                Self::grow(ui, b.id, delta);
            }
        }
        self.shifts.borrow_mut().insert(b.id, shift);
        ui.capture_current_anchor_baseline(b.id);
    }

    /// Folds or opens the layout (OpenAC `ApplyLayout`).
    pub fn apply_layout(&mut self, ui: &mut UiRoot, me: ElementId, expanded: bool) {
        if expanded == self.laid_out {
            return;
        }
        self.laid_out = expanded;
        if let Some(h) = ui.widget_mut::<ExpanderHeader>(self.header) {
            h.expanded = expanded;
        }
        if self.flex_placed {
            ui.set_visible(self.content, expanded);
            return;
        }
        let parent = ui.parent(me);
        if let Some(parent) = parent {
            let (pw, ph) = size(ui, parent);
            for child in ui.children(parent) {
                ui.apply_anchor(child, pw, ph);
            }
        }
        let (height, anchors) = ui
            .element(me)
            .map_or((0.0, AnchorEdges::NONE), |e| (e.height, e.anchors));
        if !expanded {
            self.collapse_delta = height - EXPANDER_HEADER_HEIGHT;
            ui.set_visible(self.content, false);
            if let Some(parent) = parent
                && anchors.contains(AnchorEdges::TOP | AnchorEdges::BOTTOM)
            {
                // A bottom-anchored header would stretch with the parent:
                // pinned at its header while folded.
                self.expanded_anchors = Some(anchors);
                self.parent_height_at_collapse = size(ui, parent).1;
                ui.set_anchors(me, anchors & !AnchorEdges::BOTTOM);
            }
            if let Some(el) = ui.element_mut(me) {
                el.height = EXPANDER_HEADER_HEIGHT;
            }
        } else {
            let open = self.open_height(parent.map_or(0.0, |p| size(ui, p).1));
            if let Some(el) = ui.element_mut(me) {
                el.height = open;
            }
            if let Some(anchors) = self.expanded_anchors.take() {
                ui.set_anchors(me, anchors);
            }
            ui.set_visible(self.content, true);
        }
        ui.capture_current_anchor_baseline(me);
        if let Some(parent) = parent {
            self.place_siblings(ui, me, parent);
        }
    }
}

impl Widget for MarkupExpander {
    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        let expanded = self.expanded();
        self.apply_layout(ui, me, expanded);
        self.follow_shown_siblings(ui, me);
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        if !self.framed || w <= 0.0 {
            return;
        }
        let h = self.frame_height(h);
        let is_live = live(ui, me);
        let Some(p) = self.palette else {
            let band = fade(CLASSIC_BAND, is_live);
            rc.fill(0.0, 0.0, w, h.min(EXPANDER_HEADER_HEIGHT), band);
            return;
        };
        let r = style::CONTROL_RADIUS;
        let radii = if h > EXPANDER_HEADER_HEIGHT {
            CornerRadii::new(r, r, 0.0, 0.0)
        } else {
            CornerRadii::all(r)
        };
        rc.fill_vertical_gradient(
            0.0,
            0.0,
            w,
            h.min(EXPANDER_HEADER_HEIGHT),
            radii,
            fade(style::mix(p.background, p.text, 0.08), is_live),
            fade(style::mix(p.background, p.text, 0.04), is_live),
        );
    }

    // The border goes over the children, so content placed at the edge
    // cannot hide it.
    fn draw_after_children(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        if !self.framed || w <= 0.0 {
            return;
        }
        let h = self.frame_height(h);
        let is_live = live(ui, me);
        let edge = if let Some(p) = &self.palette {
            let edge = fade(p.border, is_live);
            let radius = style::CONTROL_RADIUS;
            style::surface(rc, 0.0, 0.0, w, h, radius, Color::TRANSPARENT, edge);
            edge
        } else {
            let edge = fade(CLASSIC_FRAME, is_live);
            rc.rect_outline(0.0, 0.0, w, h, edge, 1.0);
            edge
        };
        if h > EXPANDER_HEADER_HEIGHT {
            rc.fill(1.0, EXPANDER_HEADER_HEIGHT - 1.0, w - 2.0, 1.0, edge);
        }
    }
}

/// Where an expander header's caption starts: after the arrow.
pub const CAPTION_X: f32 = 14.0;

/// An expander's header: an arrow and caption that toggles its expander on
/// a press and release over it, or Enter/Space while focused (OpenAC
/// `UiMarkupExpanderHeader`).
#[derive(Debug)]
pub struct ExpanderHeader {
    /// The caption.
    pub text: String,
    /// Caption colour.
    pub color: Color,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// The layout is expanded (the arrow points down).
    pub expanded: bool,
    focused: bool,
    pressed: bool,
}

impl ExpanderHeader {
    /// A header.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>) -> Self {
        Self {
            text: String::new(),
            color: CLASSIC_CAPTION,
            font,
            palette: None,
            expanded: true,
            focused: false,
            pressed: false,
        }
    }

    fn toggle_owner(&mut self, ui: &mut UiRoot, me: ElementId) {
        if let Some(owner) = ui.parent(me) {
            // The expander cannot reach this widget while it runs, so the
            // arrow is brought up to date here.
            let expanded = ui.with::<MarkupExpander, _>(owner, |x, ui| {
                x.toggle(ui, owner);
                x.laid_out_expanded()
            });
            self.expanded = expanded.unwrap_or(self.expanded);
        }
    }
}

impl Widget for ExpanderHeader {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        let is_live = live(ui, me);
        match e.kind {
            kind::FOCUS_GAINED => {
                self.focused = true;
                true
            }
            kind::FOCUS_LOST => {
                self.focused = false;
                true
            }
            kind::KEY_DOWN
                if self.focused
                    && is_live
                    && matches!(e.key, Key::Enter | Key::KeypadEnter | Key::Space) =>
            {
                self.toggle_owner(ui, me);
                true
            }
            kind::MOUSE_DOWN => {
                self.pressed = is_live;
                true
            }
            kind::MOUSE_UP => {
                let (w, h) = size(ui, me);
                let inside =
                    e.data1 >= 0 && e.data2 >= 0 && (e.data1 as f32) < w && (e.data2 as f32) < h;
                if self.pressed && inside {
                    self.pressed = false;
                    self.toggle_owner(ui, me);
                }
                self.pressed = false;
                true
            }
            kind::CAPTURE_CHANGED => {
                self.pressed = false;
                true
            }
            kind::CLICK | kind::DOUBLE_CLICK => true,
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let is_live = live(ui, me);
        if let Some(p) = &self.palette {
            // A muted smooth chevron within the arrow's room.
            let muted = fade(p.muted, is_live);
            let cy = h * 0.5;
            if self.expanded {
                style::chevron(rc, 3.0, cy - 2.0, 7.0, 4.0, muted);
            } else {
                style::chevron_right(rc, 4.5, cy - 3.5, 3.5, 7.0, muted);
            }
        } else {
            let arrow = fade(CLASSIC_ARROW, is_live);
            let top = ((h - 5.0) * 0.5).floor();
            for i in 0..3u8 {
                let i = f32::from(i);
                if self.expanded {
                    rc.fill(3.0 + i, top + 1.0 + i, 5.0 - i * 2.0, 1.0, arrow);
                } else {
                    rc.fill(4.0 + i, top + i, 1.0, 5.0 - i * 2.0, arrow);
                }
            }
        }
        if let Some(font) = &self.font {
            let y = ((h - font.line_height) * 0.5).floor();
            rc.string_dat(
                font,
                &self.text,
                CAPTION_X,
                y,
                fade(self.color, is_live),
                self.palette.is_none(),
                crate::draw::DEFAULT_OUTLINE_COLOR,
            );
        }
        if self.focused {
            match &self.palette {
                Some(p) => style::focus_ring(rc, p, w, h, style::SMALL_RADIUS),
                None => rc.rect_outline(0.0, 0.0, w, h, FOCUS, 1.0),
            }
        }
    }
}

// ── Split ─────────────────────────────────────────────────────────────────

/// How far one arrow key press moves a split bar, pixels.
pub const SPLIT_KEY_STEP: f32 = 8.0;

/// Two panes with a bar between them the player drags (OpenAC
/// `UiMarkupSplit`). The position is the first pane's size, clamped to
/// `min`/`max` and the room; when the split resizes the first pane keeps
/// its size.
#[derive(Debug)]
pub struct MarkupSplit {
    /// Side by side (else stacked).
    pub horizontal: bool,
    /// The bar element.
    pub bar: ElementId,
    /// The first pane.
    pub first: Option<ElementId>,
    /// The second pane.
    pub second: Option<ElementId>,
    /// The least first-pane size.
    pub min: f32,
    /// The largest first-pane size.
    pub max: Option<f32>,
    /// Where the bar starts.
    pub default_position: f32,
    /// The bound position, when the plugin owns it.
    pub bound: Option<f32>,
    /// The first pane's size as last laid out.
    pub laid_out: f32,
    host: Option<f32>,
    remembered: Remembered,
    asked: f32,
    move_start: f32,
    onchange: Option<String>,
    emit: Emit,
}

impl MarkupSplit {
    /// A split with its bar.
    #[must_use]
    pub fn new(
        horizontal: bool,
        bar: ElementId,
        remembered: Remembered,
        onchange: Option<String>,
        emit: Emit,
    ) -> Self {
        Self {
            horizontal,
            bar,
            first: None,
            second: None,
            min: 0.0,
            max: None,
            default_position: 0.0,
            bound: None,
            laid_out: 0.0,
            host: None,
            remembered,
            asked: 0.0,
            move_start: 0.0,
            onchange,
            emit,
        }
    }

    /// The position asked for (bound, remembered or default), unclamped.
    pub fn position(&mut self) -> f32 {
        if let Some(bound) = self.bound {
            return bound;
        }
        if let Some(text) = self.remembered.refresh() {
            self.host = Some(
                text.as_deref()
                    .and_then(|t| t.parse::<i32>().ok())
                    .filter(|v| *v >= 0)
                    .map_or(self.default_position, |v| v as f32),
            );
        }
        *self.host.get_or_insert(self.default_position)
    }

    fn room(&self, w: f32, h: f32) -> f32 {
        ((if self.horizontal { w } else { h }) - SPLIT_BAR_SIZE).max(0.0)
    }

    /// Whole pixels, within min/max and the room.
    #[must_use]
    pub fn clamp(&self, position: f32, w: f32, h: f32) -> f32 {
        let position = if position.is_nan() {
            self.default_position
        } else {
            position
        };
        let room = self.room(w, h);
        let hi = self.max.unwrap_or(room).min(room);
        let lo = self.min.min(hi);
        position.clamp(lo, hi).round()
    }

    /// Lays the panes and bar out at the position.
    pub fn layout(&mut self, ui: &mut UiRoot, me: ElementId) {
        let (w, h) = size(ui, me);
        let at = self.position();
        let at = self.clamp(at, w, h);
        self.layout_at(ui, w, h, at);
    }

    /// Lays out at the authored start (the panes' children record their
    /// anchors against it).
    pub fn layout_at_default(&mut self, ui: &mut UiRoot, me: ElementId) {
        let (w, h) = size(ui, me);
        let at = self.clamp(self.default_position, w, h);
        self.layout_at(ui, w, h, at);
    }

    fn layout_at(&mut self, ui: &mut UiRoot, w: f32, h: f32, at: f32) {
        self.laid_out = at;
        let second = at + SPLIT_BAR_SIZE;
        let place = |ui: &mut UiRoot, e: Option<ElementId>, r: (f32, f32, f32, f32)| {
            if let Some(el) = e.and_then(|e| ui.element_mut(e)) {
                (el.left, el.top, el.width, el.height) = r;
            }
        };
        if self.horizontal {
            place(ui, self.first, (0.0, 0.0, at, h));
            place(ui, Some(self.bar), (at, 0.0, SPLIT_BAR_SIZE, h));
            place(ui, self.second, (second, 0.0, (w - second).max(0.0), h));
        } else {
            place(ui, self.first, (0.0, 0.0, w, at));
            place(ui, Some(self.bar), (0.0, at, w, SPLIT_BAR_SIZE));
            place(ui, self.second, (0.0, second, w, (h - second).max(0.0)));
        }
    }

    /// A drag or key press starts from where the bar is now.
    pub fn begin_move(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.layout(ui, me);
        self.asked = self.laid_out;
        self.move_start = self.laid_out;
    }

    /// Asks for a position; each distinct whole pixel is reported once.
    pub fn move_to(&mut self, ui: &mut UiRoot, me: ElementId, position: f32) {
        let (w, h) = size(ui, me);
        let at = self.clamp(position, w, h);
        if (at - self.asked).abs() < f32::EPSILON {
            return;
        }
        if self.bound.is_some() && self.onchange.is_none() {
            return;
        }
        self.asked = at;
        if self.bound.is_none() {
            self.host = Some(at);
        }
        self.emit
            .act(self.onchange.as_deref(), ActionValue::Float(at));
        self.layout(ui, me);
    }

    /// The player let go: an unbound change is remembered.
    pub fn end_move(&mut self) {
        if self.bound.is_some() || (self.asked - self.move_start).abs() < f32::EPSILON {
            return;
        }
        let encoded = (self.asked.round() as i32).to_string();
        let default = (self.default_position.round() as i32).to_string();
        self.remembered
            .remember((encoded != default).then_some(encoded.as_str()));
    }

    /// An arrow key press.
    pub fn nudge(&mut self, ui: &mut UiRoot, me: ElementId, direction: f32) {
        self.begin_move(ui, me);
        let to = self.laid_out + direction * SPLIT_KEY_STEP;
        self.move_to(ui, me, to);
        self.end_move();
    }
}

impl Widget for MarkupSplit {
    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        self.layout(ui, me);
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, _rc: &mut RenderContext<'_>) {
        self.layout(ui, me);
    }
}

/// A split's bar (OpenAC `UiMarkupSplitBar`): dragged, or moved with the
/// arrow keys along its axis while focused.
#[derive(Debug, Default)]
pub struct SplitBar {
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// Side by side.
    pub horizontal: bool,
    focused: bool,
    hovered: bool,
    dragging: bool,
    grab: f32,
}

impl SplitBar {
    /// A bar for a split laid out `horizontal`ly.
    #[must_use]
    pub fn new(horizontal: bool) -> Self {
        Self {
            horizontal,
            ..Self::default()
        }
    }

    fn with_split(
        ui: &mut UiRoot,
        me: ElementId,
        f: impl FnOnce(&mut MarkupSplit, &mut UiRoot, ElementId),
    ) {
        if let Some(split) = ui.parent(me) {
            ui.with::<MarkupSplit, _>(split, |s, ui| f(s, ui, split));
        }
    }
}

impl Widget for SplitBar {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        let is_live = live(ui, me);
        let along =
            |e: &UiEvent, horizontal: bool| if horizontal { e.data1 } else { e.data2 } as f32;
        match e.kind {
            kind::FOCUS_GAINED => self.focused = true,
            kind::FOCUS_LOST => self.focused = false,
            kind::HOVER_ENTER => self.hovered = true,
            kind::HOVER_LEAVE => self.hovered = false,
            kind::MOUSE_DOWN => {
                if is_live {
                    self.dragging = true;
                    self.grab = along(e, self.horizontal);
                    Self::with_split(ui, me, |s, ui, split| s.begin_move(ui, split));
                }
            }
            kind::MOUSE_MOVE => {
                if self.dragging {
                    let offset = ui
                        .element(me)
                        .map_or(0.0, |el| if self.horizontal { el.left } else { el.top });
                    let to = offset + along(e, self.horizontal) - self.grab;
                    Self::with_split(ui, me, |s, ui, split| s.move_to(ui, split, to));
                }
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED => {
                if std::mem::take(&mut self.dragging) {
                    Self::with_split(ui, me, |s, _, _| s.end_move());
                }
            }
            kind::CLICK | kind::DOUBLE_CLICK => {}
            kind::KEY_DOWN if self.focused && is_live => {
                let direction = match (self.horizontal, e.key) {
                    (true, Key::Left) | (false, Key::Up) => -1.0,
                    (true, Key::Right) | (false, Key::Down) => 1.0,
                    _ => return false,
                };
                Self::with_split(ui, me, |s, ui, split| s.nudge(ui, split, direction));
            }
            _ => return false,
        }
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.dragging
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        if w <= 0.0 || h <= 0.0 {
            return;
        }
        let is_live = live(ui, me);
        let hot = is_live && (self.dragging || self.hovered);
        if let Some(p) = &self.palette {
            // No band, only a slim grip pill across the middle.
            const GRIP_LENGTH: f32 = 24.0;
            const GRIP_WIDTH: f32 = 3.0;
            let pill = fade(if hot { p.accent } else { p.border }, is_live);
            let radii = CornerRadii::all(GRIP_WIDTH * 0.5);
            let (gx, gy, gw, gh) = if self.horizontal {
                let length = GRIP_LENGTH.min(h);
                (
                    ((w - GRIP_WIDTH) * 0.5).floor(),
                    ((h - length) * 0.5).floor(),
                    GRIP_WIDTH,
                    length,
                )
            } else {
                let length = GRIP_LENGTH.min(w);
                (
                    ((w - length) * 0.5).floor(),
                    ((h - GRIP_WIDTH) * 0.5).floor(),
                    length,
                    GRIP_WIDTH,
                )
            };
            rc.fill_rounded_rect(gx, gy, gw, gh, radii, pill);
            if self.focused {
                style::focus_ring(rc, p, w, h, style::SMALL_RADIUS);
            }
            return;
        }
        let fill = fade(UNLIT_OUTER, is_live);
        let grip = fade(if hot { CLASSIC_ARROW } else { UNLIT_INNER }, is_live);
        rc.fill(0.0, 0.0, w, h, fill);
        if self.horizontal {
            rc.fill(((w - 1.0) * 0.5).floor(), 0.0, 1.0, h, grip);
        } else {
            rc.fill(0.0, ((h - 1.0) * 0.5).floor(), w, 1.0, grip);
        }
        if self.focused {
            rc.rect_outline(0.0, 0.0, w, h, FOCUS, 1.0);
        }
    }
}

// ── Context menu ──────────────────────────────────────────────────────────

/// An item that draws as a separator line.
pub const MENU_SEPARATOR: &str = "-";
/// An item's height.
pub const MENU_ITEM_HEIGHT: f32 = 18.0;
/// A separator's height.
pub const MENU_SEPARATOR_HEIGHT: f32 = 7.0;
/// The popup's inner margin.
pub const MENU_INSET: f32 = 3.0;
/// Where an item's text starts.
pub const MENU_TEXT_INDENT: f32 = 8.0;
/// The narrowest popup.
pub const MENU_MIN_WIDTH: f32 = 80.0;
/// The classic popup fill.
pub const MENU_BACKGROUND: Color = Color::rgba(0.0, 0.0, 0.0, 0.92);
/// The classic popup border.
pub const MENU_BORDER: Color = Color::rgba(0.46, 0.37, 0.16, 1.0);
/// The classic item text.
pub const MENU_TEXT: Color = Color::rgba(0.91, 0.87, 0.76, 1.0);
/// The classic disabled item text.
pub const MENU_DISABLED: Color = Color::rgba(0.5, 0.5, 0.5, 1.0);
/// The classic hovered item.
pub const MENU_HIGHLIGHT: Color = Color::rgba(0.40, 0.33, 0.14, 0.95);

/// Where a popup starts on one axis: at the point, or on the other side of
/// it when it would cross the screen edge, then shifted onto the screen
/// (OpenAC `UiMarkupContextMenu.Place`).
#[must_use]
pub fn place_popup(point: f32, size: f32, screen: f32) -> f32 {
    let start = if point + size > screen {
        point - size
    } else {
        point
    };
    start.min(screen - size).max(0.0)
}

/// What opens a context menu over an element: the right click's screen
/// point, sent to the menu element; it answers whether it opened.
pub const CONTEXT_MENU_OPEN: i32 = crate::event::kind::CONTEXT_MENU_OPEN;

/// A right-click menu, a child of the element it belongs to that takes no
/// room there and draws its popup in the overlay pass (OpenAC
/// `UiMarkupContextMenu`). Open, it holds the root's popup slot and the
/// keyboard, and gives the keyboard back when it closes.
#[derive(Debug)]
pub struct MarkupContextMenu {
    /// The items as the session last pushed them (`-` a separator).
    pub items: Vec<String>,
    /// One flag per item (shorter: the rest enabled).
    pub enabled: Vec<bool>,
    /// The font.
    pub font: Option<Arc<UiFont>>,
    /// The theme on a themed panel.
    pub palette: Option<Palette>,
    /// The item picked handler.
    pub onselect: String,
    /// The opening handler.
    pub onopen: Option<String>,
    /// The list row a right click selects first, worked out by the owner
    /// (a list) before the menu opens: the plugin row, and the selection
    /// handler to call when it is not already selected.
    pub list_row: Option<(i32, Option<(String, ActionValue)>)>,
    open: bool,
    shown: Vec<String>,
    shown_enabled: Vec<bool>,
    row_tops: Vec<f32>,
    rect: (f32, f32, f32, f32),
    highlight: i32,
    return_focus: Option<ElementId>,
    emit: Emit,
}

impl MarkupContextMenu {
    /// A menu reporting to `onselect`.
    #[must_use]
    pub fn new(
        font: Option<Arc<UiFont>>,
        onselect: String,
        onopen: Option<String>,
        emit: Emit,
    ) -> Self {
        Self {
            items: Vec::new(),
            enabled: Vec::new(),
            font,
            palette: None,
            onselect,
            onopen,
            list_row: None,
            open: false,
            shown: Vec::new(),
            shown_enabled: Vec::new(),
            row_tops: Vec::new(),
            rect: (0.0, 0.0, 0.0, 0.0),
            highlight: -1,
            return_focus: None,
            emit,
        }
    }

    /// Open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open
    }

    fn measure(&self, s: &str) -> f32 {
        self.font
            .as_ref()
            .map_or(s.chars().count() as f32 * 7.0, |f| f.measure_width(s))
    }

    /// Opens at a screen point, reading the items now; false when the
    /// owner is not available or there are no items.
    pub fn try_open(&mut self, ui: &mut UiRoot, me: ElementId, sx: i32, sy: i32) -> bool {
        if self.open {
            self.close(ui, me, true);
        }
        let Some(owner) = ui.parent(me) else {
            return false;
        };
        if !available(ui, owner) {
            return false;
        }
        let row = match self.list_row.take() {
            Some((row, select)) => {
                if let Some((handler, value)) = select {
                    self.emit.act(Some(&handler), value);
                }
                row
            }
            None => -1,
        };
        self.emit.act(self.onopen.as_deref(), ActionValue::Int(row));
        if self.items.is_empty() {
            return false;
        }
        self.shown.clone_from(&self.items);
        self.shown_enabled = Vec::with_capacity(self.shown.len());
        self.row_tops = Vec::with_capacity(self.shown.len() + 1);
        let mut y = MENU_INSET;
        let mut text_width: f32 = 0.0;
        for (i, item) in self.shown.iter().enumerate() {
            self.row_tops.push(y);
            let separator = item == MENU_SEPARATOR;
            y += if separator {
                MENU_SEPARATOR_HEIGHT
            } else {
                MENU_ITEM_HEIGHT
            };
            self.shown_enabled
                .push(!separator && self.enabled.get(i).copied().unwrap_or(true));
            if !separator {
                text_width = text_width.max(self.measure(item));
            }
        }
        self.row_tops.push(y);
        let w = MENU_MIN_WIDTH
            .max(text_width + 2.0 * MENU_TEXT_INDENT)
            .ceil();
        let h = y + MENU_INSET;
        let (screen_w, screen_h) = ui.effective_canvas_size();
        let (ox, oy) = ui.screen_position(me);
        self.rect = (
            place_popup(sx as f32, w, screen_w) - ox,
            place_popup(sy as f32, h, screen_h) - oy,
            w,
            h,
        );
        self.highlight = -1;
        self.open = true;
        ui.set_active_popup(me, me);
        self.return_focus = ui.keyboard_focus();
        ui.set_keyboard_focus(Some(me));
        true
    }

    /// Closes; with `restore_focus`, the keyboard goes back.
    pub fn close(&mut self, ui: &mut UiRoot, me: ElementId, restore_focus: bool) {
        if !self.open {
            return;
        }
        self.open = false;
        self.highlight = -1;
        let previous = self.return_focus.take();
        ui.clear_active_popup(me);
        if restore_focus && ui.keyboard_focus() == Some(me) {
            ui.set_keyboard_focus(previous);
        }
    }

    /// The item at a local point, or −1.
    #[must_use]
    pub fn item_at(&self, x: f32, y: f32) -> i32 {
        let (rx, ry, rw, _) = self.rect;
        if !self.open || x < rx || x >= rx + rw {
            return -1;
        }
        let local = y - ry;
        (0..self.shown.len())
            .find(|i| local >= self.row_tops[*i] && local < self.row_tops[i + 1])
            .map_or(-1, |i| i as i32)
    }

    fn pick(&mut self, ui: &mut UiRoot, me: ElementId, index: i32) {
        self.close(ui, me, true);
        self.emit.act(Some(&self.onselect), ActionValue::Int(index));
    }

    fn step(&mut self, delta: i32) {
        let count = self.shown.len() as i32;
        let mut index = self.highlight;
        for _ in 0..count {
            index = if index < 0 {
                if delta > 0 { 0 } else { count - 1 }
            } else {
                (index + delta + count) % count
            };
            if self.shown_enabled[index as usize] {
                self.highlight = index;
                return;
            }
        }
    }

    fn picks(&self, index: i32) -> bool {
        index >= 0
            && self
                .shown_enabled
                .get(index as usize)
                .copied()
                .unwrap_or(false)
    }
}

impl Widget for MarkupContextMenu {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == CONTEXT_MENU_OPEN {
            return self.try_open(ui, me, e.data1, e.data2);
        }
        if e.target != Some(me) {
            return false;
        }
        if e.kind == kind::FOCUS_LOST {
            self.close(ui, me, false);
            return true;
        }
        if !self.open {
            return true;
        }
        match e.kind {
            kind::KEY_DOWN => match e.key {
                Key::Escape => self.close(ui, me, true),
                Key::Up => self.step(-1),
                Key::Down => self.step(1),
                Key::Enter | Key::KeypadEnter if self.picks(self.highlight) => {
                    let index = self.highlight;
                    self.pick(ui, me, index);
                }
                _ => {}
            },
            kind::HOVER_ENTER | kind::MOUSE_MOVE => {
                let hovered = self.item_at(e.data1 as f32, e.data2 as f32);
                self.highlight = if self.picks(hovered) { hovered } else { -1 };
            }
            kind::HOVER_LEAVE => self.highlight = -1,
            kind::MOUSE_DOWN => {
                let item = self.item_at(e.data1 as f32, e.data2 as f32);
                if self.picks(item) {
                    self.pick(ui, me, item);
                }
            }
            _ => {}
        }
        true
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, _dt: f64) {
        if self.open && ui.parent(me).is_some_and(|owner| !available(ui, owner)) {
            self.close(ui, me, true);
        }
    }

    fn on_popup_dismissed(&mut self, ui: &mut UiRoot, me: ElementId, _popup: ElementId) {
        self.close(ui, me, true);
    }

    fn hit_test(&self, _element: &Element, x: f32, y: f32) -> bool {
        let (rx, ry, rw, rh) = self.rect;
        self.open && x >= rx && x < rx + rw && y >= ry && y < ry + rh
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

    fn draw_overlay(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        if !self.open {
            return;
        }
        let p = self.palette.as_ref();
        let (x, y, w, h) = self.rect;
        rc.push_alpha_absolute(1.0);
        if let Some(p) = p {
            style::popup_shadow(rc, x, y, w, h);
            style::surface(rc, x, y, w, h, style::CONTAINER_RADIUS, p.field, p.border);
        } else {
            rc.fill(x, y, w, h, MENU_BACKGROUND);
            rc.rect_outline(x, y, w, h, MENU_BORDER, 1.0);
        }
        for (i, item) in self.shown.iter().enumerate() {
            let top = y + self.row_tops[i];
            if item == MENU_SEPARATOR {
                rc.fill(
                    x + MENU_INSET,
                    top + ((MENU_SEPARATOR_HEIGHT - 1.0) * 0.5).floor(),
                    w - 2.0 * MENU_INSET,
                    1.0,
                    p.map_or(UNLIT_INNER, |p| p.border),
                );
                continue;
            }
            if i as i32 == self.highlight {
                match p {
                    Some(p) => {
                        style::row(rc, x, top, w, MENU_ITEM_HEIGHT, style::hover(p, p.field));
                    }
                    None => rc.fill(x + 1.0, top, w - 2.0, MENU_ITEM_HEIGHT, MENU_HIGHLIGHT),
                }
            }
            let color = if self.shown_enabled[i] {
                p.map_or(MENU_TEXT, |p| p.text)
            } else {
                p.map_or(MENU_DISABLED, |p| p.muted)
            };
            if let Some(font) = &self.font {
                let ty = top + ((MENU_ITEM_HEIGHT - font.line_height) * 0.5).floor();
                rc.string_dat(
                    font,
                    item,
                    x + MENU_TEXT_INDENT,
                    ty,
                    color,
                    p.is_none(),
                    crate::draw::DEFAULT_OUTLINE_COLOR,
                );
            }
        }
        rc.pop_alpha();
    }
}
