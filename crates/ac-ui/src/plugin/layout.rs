//! Flex layout and scrolling for markup windows: the content area of a
//! window with chrome ([`ContentHost`]), a markup `<group>` that lays out
//! or scrolls ([`MarkupGroup`]), and the scrolling they share
//! ([`ScrollArea`]).

use std::cell::RefCell;
use std::rc::Rc;

use ac_plugin_ui::flex::{FlexNode, SCROLLBAR_THICKNESS};

use super::flex_box::FlexBox;
use crate::draw::RenderContext;
use crate::event::{UiEvent, kind};
use crate::geometry::AnchorEdges;
use crate::root::UiRoot;
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::scrollbar::ScrollSource;
use crate::widgets::{Panel, Scrollbar};

/// A flex root's layout, shared with the nested flex groups whose nodes are
/// part of its tree.
pub type SharedFlex = Rc<RefCell<FlexBox>>;

/// Where an element's flex layout lives: the root it belongs to and, for a
/// nested container, its item in that root.
#[derive(Debug, Clone)]
pub struct FlexPart {
    /// The root's layout.
    pub layout: SharedFlex,
    /// `None`: this element is the root; else its item in the root.
    pub item: Option<usize>,
}

impl FlexPart {
    /// This container's node, as the last arrange left it.
    fn with_node<R>(&self, f: impl FnOnce(&FlexNode) -> R) -> R {
        let layout = self.layout.borrow();
        f(layout.container_node(self.item))
    }
}

/// A bar's thickness, which the layout engine reserves too.
pub const BAR_SIZE: f32 = SCROLLBAR_THICKNESS;
/// Pixels moved by a bar's arrow button.
pub const LINE_STEP: i32 = 16;
/// Lines moved by one wheel step.
pub const WHEEL_LINES: i32 = 3;

/// The vertical scrollbar art: track, arrows and three-part thumb.
pub fn apply_vertical_bar_art(bar: &mut Scrollbar) {
    bar.track_sprite = 0x0600_4C5F;
    [bar.up_sprite, bar.up_rollover_sprite, bar.up_pressed_sprite] =
        [0x0600_4C6C, 0x0600_4C6D, 0x0600_4C6E];
    [
        bar.down_sprite,
        bar.down_rollover_sprite,
        bar.down_pressed_sprite,
    ] = [0x0600_4C69, 0x0600_4C6A, 0x0600_4C6B];
    [
        bar.thumb_top_sprite,
        bar.thumb_top_rollover_sprite,
        bar.thumb_top_pressed_sprite,
    ] = [0x0600_4C60, 0x0600_4C61, 0x0600_4C62];
    [
        bar.thumb_sprite,
        bar.thumb_rollover_sprite,
        bar.thumb_pressed_sprite,
    ] = [0x0600_4C63, 0x0600_4C64, 0x0600_4C65];
    [
        bar.thumb_bot_sprite,
        bar.thumb_bot_rollover_sprite,
        bar.thumb_bot_pressed_sprite,
    ] = [0x0600_4C66, 0x0600_4C67, 0x0600_4C68];
}

/// The scrolling of one markup container (a group, or a window's content
/// area, with `scroll`): a 16-pixel bar per axis it scrolls on, each with
/// its own scroll model, and the host's content viewport and offset. A bar
/// is shown only while the content overflows on its axis, and is placed
/// beside the viewport rather than over it; when both show, the corner
/// stays empty. The position is kept across hide/show and theme changes,
/// clamped when the extent or viewport shrinks, and never saved.
#[derive(Debug, Clone)]
pub struct ScrollArea {
    /// Scrolls along x.
    pub scrolls_x: bool,
    /// Scrolls along y.
    pub scrolls_y: bool,
    /// The vertical bar (only when it scrolls along y).
    pub vertical: Option<ElementId>,
    /// The horizontal bar (only when it scrolls along x).
    pub horizontal: Option<ElementId>,
}

fn bar_element(width: f32, height: f32) -> Element {
    let mut e = Element::at(0.0, 0.0, width, height)
        .with_anchors(AnchorEdges::NONE)
        .with_visible(false)
        .with_z_order(i32::MAX);
    Scrollbar::configure(&mut e);
    e.scroll_chrome = true;
    e
}

impl ScrollArea {
    /// Scrolling for `host`, which becomes hit-testable so the wheel
    /// reaches it over empty space (a press there still drags the window:
    /// the host neither handles clicks nor captures).
    pub fn new(ui: &mut UiRoot, host: ElementId, scrolls_x: bool, scrolls_y: bool) -> Self {
        if let Some(e) = ui.element_mut(host) {
            e.click_through = false;
        }
        let model = || {
            let mut m = Scrollable::default();
            m.line_height = LINE_STEP;
            ScrollSource::Own(m)
        };
        let vertical = scrolls_y.then(|| {
            let mut bar = Scrollbar::default();
            bar.model = model();
            apply_vertical_bar_art(&mut bar);
            ui.add(host, bar_element(BAR_SIZE, 0.0), bar)
        });
        let horizontal = scrolls_x.then(|| {
            let mut bar = Scrollbar::default();
            bar.model = model();
            crate::panels::external_container::apply_horizontal_bar_art(&mut bar);
            ui.add(host, bar_element(0.0, BAR_SIZE), bar)
        });
        Self {
            scrolls_x,
            scrolls_y,
            vertical,
            horizontal,
        }
    }

    fn model(ui: &mut UiRoot, bar: Option<ElementId>) -> Option<&mut Scrollable> {
        match &mut ui.widget_mut::<Scrollbar>(bar?)?.model {
            ScrollSource::Own(m) => Some(m),
            _ => None,
        }
    }

    /// The vertical position (tests).
    #[must_use]
    pub fn y(&self, ui: &mut UiRoot) -> Scrollable {
        Self::model(ui, self.vertical).copied().unwrap_or_default()
    }

    /// The horizontal position (tests); its `scroll_y` is the offset along x.
    #[must_use]
    pub fn x(&self, ui: &mut UiRoot) -> Scrollable {
        Self::model(ui, self.horizontal)
            .copied()
            .unwrap_or_default()
    }

    /// Takes the bars, viewport and extent a flex arrange settled for the
    /// host's node.
    pub fn apply_flex(&self, ui: &mut UiRoot, host: ElementId, node: &FlexNode) {
        self.apply(
            ui,
            host,
            (node.content_size.width, node.content_size.height),
            (node.viewport.width, node.viewport.height),
            (node.scrollbar_shown_x, node.scrollbar_shown_y),
        );
    }

    /// Places an absolute host's children for its viewport and settles its
    /// bars the way the layout engine does: start without bars, apply the
    /// children's anchors against the viewport and take the extent, add
    /// every bar whose axis overflows, and repeat in the smaller viewport
    /// until the bars stop changing. The extent is the right and bottom
    /// edges of the visible children, from the origin: a child at negative
    /// coordinates is not reachable by scrolling.
    pub fn settle_absolute(&self, ui: &mut UiRoot, host: ElementId) {
        let (width, height) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
        let (mut bar_x, mut bar_y) = (false, false);
        let (mut view_w, mut view_h) = (width.max(0.0), height.max(0.0));
        for _ in 0..3 {
            let (right, bottom) = Self::place_absolute(ui, host, view_w, view_h);
            let need_x = bar_x || (self.scrolls_x && right > view_w);
            let need_y = bar_y || (self.scrolls_y && bottom > view_h);
            if need_x == bar_x && need_y == bar_y {
                break;
            }
            bar_x = need_x;
            bar_y = need_y;
            view_w = (width - if bar_y { BAR_SIZE } else { 0.0 }).max(0.0);
            view_h = (height - if bar_x { BAR_SIZE } else { 0.0 }).max(0.0);
        }
        let extent = Self::place_absolute(ui, host, view_w, view_h);
        self.apply(ui, host, extent, (view_w, view_h), (bar_x, bar_y));
    }

    fn place_absolute(ui: &mut UiRoot, host: ElementId, view_w: f32, view_h: f32) -> (f32, f32) {
        let (mut right, mut bottom) = (0.0_f32, 0.0_f32);
        for child in ui.children(host) {
            if ui.element(child).is_none_or(|e| e.scroll_chrome) {
                continue;
            }
            ui.apply_anchor(child, view_w, view_h);
            let Some(e) = ui.element(child) else {
                continue;
            };
            if !e.visible {
                continue;
            }
            right = right.max(e.left + e.width);
            bottom = bottom.max(e.top + e.height);
        }
        (right, bottom)
    }

    fn apply(
        &self,
        ui: &mut UiRoot,
        host: ElementId,
        (extent_w, extent_h): (f32, f32),
        (view_w, view_h): (f32, f32),
        (bar_x, bar_y): (bool, bool),
    ) {
        let (host_w, host_h) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
        if let Some(e) = ui.element_mut(host) {
            e.content_viewport = Some((view_w, view_h));
        }
        let content_x = if self.scrolls_x {
            extent_w.ceil() as i32
        } else {
            0
        };
        let content_y = if self.scrolls_y {
            extent_h.ceil() as i32
        } else {
            0
        };
        if let Some(m) = Self::model(ui, self.horizontal) {
            m.set_extents(content_x, view_w.floor() as i32, false);
        }
        if let Some(m) = Self::model(ui, self.vertical) {
            m.set_extents(content_y, view_h.floor() as i32, false);
        }
        // A bar sits along the host's far edge, hidden or not, so it never
        // reaches past the host.
        if let Some(bar) = self.vertical
            && let Some(e) = ui.element_mut(bar)
        {
            e.visible = bar_y;
            e.left = (host_w - BAR_SIZE).max(0.0);
            e.top = 0.0;
            e.width = BAR_SIZE;
            e.height = view_h;
        }
        if let Some(bar) = self.horizontal
            && let Some(e) = ui.element_mut(bar)
        {
            e.visible = bar_x;
            e.left = 0.0;
            e.top = (host_h - BAR_SIZE).max(0.0);
            e.width = view_w;
            e.height = BAR_SIZE;
        }
        self.sync_offset(ui, host);
    }

    /// Moves the host's content to the bars' positions.
    pub fn sync_offset(&self, ui: &mut UiRoot, host: ElementId) {
        let x = Self::model(ui, self.horizontal).map_or(0, |m| m.scroll_y());
        let y = Self::model(ui, self.vertical).map_or(0, |m| m.scroll_y());
        if let Some(e) = ui.element_mut(host) {
            e.content_offset = (x as f32, y as f32);
        }
    }

    /// Scrolls with the wheel: a vertical step on an area that scrolls
    /// vertically, a horizontal step on one that scrolls horizontally, and
    /// only while there is something to scroll; anything else is left to
    /// bubble on to an outer area.
    pub fn on_event(&self, ui: &mut UiRoot, host: ElementId, e: &UiEvent) -> bool {
        let bar = match e.kind {
            kind::SCROLL if self.scrolls_y => self.vertical,
            kind::SCROLL_HORIZONTAL if self.scrolls_x => self.horizontal,
            _ => return false,
        };
        let Some(m) = Self::model(ui, bar) else {
            return false;
        };
        if !m.has_overflow() {
            return false;
        }
        m.scroll_by_lines(-e.data0.signum() * WHEEL_LINES);
        self.sync_offset(ui, host);
        true
    }

    /// Scrolls the least distance that brings `target`, a descendant of the
    /// host, into view: its start when it is larger than the viewport. Uses
    /// the geometry the host last settled.
    pub fn reveal(&self, ui: &mut UiRoot, host: ElementId, target: ElementId) {
        let Some(((vw, vh), (ox, oy))) = ui
            .element(host)
            .and_then(|e| e.content_viewport.map(|v| (v, e.content_offset)))
        else {
            return;
        };
        let (tx, ty) = ui.screen_position(target);
        let (hx, hy) = ui.screen_position(host);
        let (tw, th) = ui
            .element(target)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let (start_x, start_y) = (tx - hx + ox, ty - hy + oy);
        if self.scrolls_y
            && let Some(m) = Self::model(ui, self.vertical)
        {
            let to = into(m.scroll_y(), start_y, th, vh);
            m.set_scroll_y(to);
        }
        if self.scrolls_x
            && let Some(m) = Self::model(ui, self.horizontal)
        {
            let to = into(m.scroll_y(), start_x, tw, vw);
            m.set_scroll_y(to);
        }
        self.sync_offset(ui, host);
    }

    /// Gives both bars the window's theme (`None`: the Classic art).
    pub fn apply_theme(&self, ui: &mut UiRoot, palette: Option<&crate::plugin::Palette>) {
        for bar in [self.vertical, self.horizontal].into_iter().flatten() {
            if let Some(w) = ui.widget_mut::<Scrollbar>(bar) {
                let classic = Scrollbar::default();
                w.retail_art = palette.is_none();
                w.plain_track_color = palette.map_or(classic.plain_track_color, |p| p.field);
                w.plain_border_color = palette.map_or(classic.plain_border_color, |p| p.border);
                w.plain_nub_color = palette.map_or(classic.plain_nub_color, |p| p.muted);
                w.theme_palette = palette.copied();
            }
        }
    }
}

fn into(offset: i32, start: f32, size: f32, view: f32) -> i32 {
    if start < offset as f32 {
        return start.floor() as i32;
    }
    if start + size > offset as f32 + view {
        return start.min(start + size - view).ceil() as i32;
    }
    offset
}

/// The content area of a plugin window with chrome: owns every authored
/// child, sits inside the frame's border and title bar, and stretches with
/// the frame. It draws nothing and lets presses through to the frame, so
/// the window still drags from any empty point. When the root panel has
/// `layout` it is the root of the window's flex tree.
#[derive(Debug, Default)]
pub struct ContentHost {
    /// The content area's flex layout (`None`: placed by coordinates).
    pub flex: Option<SharedFlex>,
    /// The content area's scrolling (root `scroll`).
    pub scroll: Option<ScrollArea>,
}

impl ContentHost {
    fn settle(&self, ui: &mut UiRoot, me: ElementId) {
        let Some(area) = &self.scroll else {
            return;
        };
        match &self.flex {
            Some(flex) => {
                let node = flex.borrow().root_node().clone();
                area.apply_flex(ui, me, &node);
            }
            None => area.settle_absolute(ui, me),
        }
    }
}

impl Widget for ContentHost {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        self.scroll.as_ref().is_some_and(|a| a.on_event(ui, me, e))
    }

    fn layout_children(&mut self, ui: &mut UiRoot, me: ElementId) -> bool {
        let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
        if let Some(flex) = &self.flex {
            flex.borrow_mut().ensure_layout(ui, w, h);
            self.settle(ui, me);
            return true;
        }
        if self.scroll.is_some() {
            self.settle(ui, me);
            return true;
        }
        false
    }

    fn reveal(&mut self, ui: &mut UiRoot, me: ElementId, target: ElementId) {
        if let Some(area) = &self.scroll {
            if let Some(flex) = &self.flex {
                let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
                flex.borrow_mut().ensure_layout(ui, w, h);
            }
            self.settle(ui, me);
            area.reveal(ui, me, target);
        }
    }
}

/// A markup `<group>`: a panel that, with `layout`, places its children
/// with flex instead of their coordinates (as a root it lays out its whole
/// tree when drawn; nested in another flex container its children are
/// placed by that container's root), and with `scroll` is seen through a
/// viewport beside its bars.
#[derive(Debug)]
pub struct MarkupGroup {
    /// Its fill and border.
    pub panel: Panel,
    /// Its flex layout.
    pub flex: Option<FlexPart>,
    /// Its scrolling.
    pub scroll: Option<ScrollArea>,
}

impl MarkupGroup {
    /// A transparent group placed by coordinates.
    #[must_use]
    pub fn new(panel: Panel) -> Self {
        Self {
            panel,
            flex: None,
            scroll: None,
        }
    }

    fn settle(&self, ui: &mut UiRoot, me: ElementId) {
        let Some(area) = &self.scroll else {
            return;
        };
        match &self.flex {
            // A nested scrolling group's node was arranged by its flex root.
            Some(part) => {
                let node = part.with_node(Clone::clone);
                area.apply_flex(ui, me, &node);
            }
            None => area.settle_absolute(ui, me),
        }
    }

    fn lay_out(&self, ui: &mut UiRoot, me: ElementId) {
        if let Some(part) = &self.flex
            && part.item.is_none()
        {
            let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
            part.layout.borrow_mut().ensure_layout(ui, w, h);
        }
        self.settle(ui, me);
    }
}

impl Widget for MarkupGroup {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        self.panel.draw(ui, me, rc);
    }

    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        self.scroll.as_ref().is_some_and(|a| a.on_event(ui, me, e))
    }

    fn layout_children(&mut self, ui: &mut UiRoot, me: ElementId) -> bool {
        if self.flex.is_none() && self.scroll.is_none() {
            return false;
        }
        self.lay_out(ui, me);
        true
    }

    fn reveal(&mut self, ui: &mut UiRoot, me: ElementId, target: ElementId) {
        if let Some(area) = &self.scroll {
            self.lay_out(ui, me);
            area.reveal(ui, me, target);
        }
    }
}
