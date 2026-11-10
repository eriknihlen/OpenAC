//! The plugins' canvases (OpenAC `PluginCanvasElement`): on the overlay
//! band each a rectangle at its anchor plus offset, over the world and
//! under every window, or in a markup window's `<canvas>` element, where
//! the window's layout places and sizes it; each showing its retained paint list
//! and, when it opted in and its plugin listens, taking the pointer inside
//! its rectangle: a press holds the pointer until the button comes up, the
//! moves and the release reach the plugin wherever they happen, and a press
//! that ends otherwise is reported cancelled. With no button held the
//! pointer over it is a hover, and its going (off the canvas, or the canvas
//! hiding or going) one leave.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_plugin_ui::fonts::{FontBake, FontView};
use ac_plugin_ui::{
    CanvasPlace, CanvasView, PaintCmd, PluginUiView, PointerEvent, PointerKind, UiInput,
    canvas_origin,
};

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::event::{UiEvent, kind};
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::overlay::OverlayHost;
use crate::root::UiRoot;
use crate::tree::{Element, ElementId, Widget};

use super::{Icon, PluginOutbox};

/// The overlay band layer the canvases sit on.
pub const LAYER_NAME: &str = "PluginCanvasLayer";

/// A font a plugin holds, once its atlases are textures of this window.
#[derive(Debug, Clone)]
pub struct CanvasFont {
    /// The font, which sets every advance, kerning and line metric.
    pub font: Arc<FontBake>,
    /// Its atlas.
    pub texture: TextureRef,
    /// Its sharper bake for the display in use: the scale, the bake and its
    /// atlas.
    pub sharp: Option<(f32, Arc<FontBake>, TextureRef)>,
}

/// The eight ways an outline's copies sit round a glyph.
const OUTLINE_OFFSETS: [(f32, f32); 8] = [
    (-1.0, -1.0),
    (0.0, -1.0),
    (1.0, -1.0),
    (-1.0, 0.0),
    (1.0, 0.0),
    (-1.0, 1.0),
    (0.0, 1.0),
    (1.0, 1.0),
];

impl CanvasFont {
    /// One line of text with its top-left at `at`. The pen moves by the font's
    /// own metrics whichever atlas the glyphs come from, so text measures the
    /// same at every scale. An outline is eight copies in the outline colour
    /// under the fill.
    pub fn draw(
        &self,
        rc: &mut RenderContext<'_>,
        text: &str,
        at: (f32, f32),
        color: Color,
        outline: bool,
    ) {
        if outline {
            for (dx, dy) in OUTLINE_OFFSETS {
                self.pass(rc, text, (at.0 + dx, at.1 + dy), DEFAULT_OUTLINE_COLOR);
            }
        }
        self.pass(rc, text, at, color);
    }

    fn pass(&self, rc: &mut RenderContext<'_>, text: &str, at: (f32, f32), tint: Color) {
        let pixels = rc.pixels_per_unit();
        // At one framebuffer pixel a unit or less it would be no sharper.
        let sharp = self.sharp.as_ref().filter(|_| pixels > 1.0);
        let (scale, texture, grid) = match sharp {
            // The sharper glyphs sit on the framebuffer's pixels.
            Some((scale, _, texture)) => (*scale, *texture, scale.min(pixels)),
            None => (1.0, self.texture, 1.0),
        };
        let baseline = at.1 + self.font.ascent;
        let mut placed = Vec::with_capacity(text.len());
        self.font.lay_out(text, |c, glyph, pen| {
            let glyph = sharp
                .and_then(|(_, fine, _)| fine.glyph(c))
                .unwrap_or(glyph);
            placed.push((
                at.0 + pen + glyph.x / scale,
                baseline + glyph.y / scale,
                glyph.width / scale,
                glyph.height / scale,
                (glyph.u0, glyph.v0, glyph.u1, glyph.v1),
            ));
        });
        rc.placed_glyphs(texture, tint, grid, placed);
    }
}

/// One canvas on the screen.
#[derive(Debug)]
pub struct CanvasWidget {
    /// The canvas as the session's copy has it.
    pub canvas: CanvasView,
    /// Its plugin's images by handle.
    pub images: HashMap<u32, Icon>,
    /// Its plugin's fonts by handle.
    pub fonts: HashMap<u32, CanvasFont>,
    font: Option<Arc<UiFont>>,
    outbox: PluginOutbox,
    held: Option<u8>,
    /// The last hover delivered (element-local place and modifiers), its
    /// leave not yet.
    hovering: Option<(i32, i32, u8)>,
    releases_seen: u32,
    /// Where it sits on the overlay band; none in a markup window, whose
    /// layout places it.
    place: Option<CanvasPlace>,
}

fn color(c: ac_plugin_ui::Color8) -> Color {
    let c = c.to_rgba();
    Color::rgba(c.r, c.g, c.b, c.a)
}

impl CanvasWidget {
    fn deliver(&self, kind: PointerKind, x: f32, y: f32, button: u8, modifiers: u8, wheel: i32) {
        self.outbox.borrow_mut().push(UiInput::Pointer {
            canvas: self.canvas.id,
            event: PointerEvent {
                kind,
                x,
                y,
                button,
                modifiers,
                wheel_delta: wheel,
            },
            place: self.place,
        });
    }

    /// Whether a press is held.
    #[must_use]
    pub fn holding(&self) -> bool {
        self.held.is_some()
    }

    fn press(&mut self, ui: &mut UiRoot, me: ElementId, button: u8, e: &UiEvent) -> bool {
        if !self.canvas.takes_input() {
            return false;
        }
        if self.held.is_none() {
            self.held = Some(button);
            ui.set_capture(me);
        }
        self.deliver(
            PointerKind::Down,
            e.data1 as f32,
            e.data2 as f32,
            button,
            e.modifiers.0 & 7,
            0,
        );
        true
    }

    fn release(&mut self, ui: &mut UiRoot, me: ElementId, button: u8, e: &UiEvent) -> bool {
        if self.held != Some(button) {
            return false;
        }
        self.held = None;
        if ui.captured() == Some(me) {
            ui.release_capture();
        }
        self.deliver(
            PointerKind::Up,
            e.data1 as f32,
            e.data2 as f32,
            button,
            e.modifiers.0 & 7,
            0,
        );
        true
    }

    /// The pointer is over it with no button held (`e` element-local). The
    /// same place again (the UI checking the hover after a wheel turn) is
    /// not news. A button held anywhere (a drag begun on the world, which
    /// takes no capture) makes the moves over it no hover.
    fn hover(&mut self, ui: &UiRoot, e: &UiEvent) -> bool {
        if !self.canvas.takes_input() || ui.any_button_down() {
            return false;
        }
        let at = (e.data1, e.data2, e.modifiers.0 & 7);
        if self.hovering.replace(at) != Some(at) {
            self.deliver(PointerKind::Hover, at.0 as f32, at.1 as f32, 0, at.2, 0);
        }
        true
    }

    /// The pointer is no longer over it: the leave its hovers are owed.
    fn leave(&mut self, ui: &UiRoot, me: ElementId) {
        if self.hovering.take().is_none() {
            return;
        }
        let (mx, my) = ui.mouse();
        let (sx, sy) = ui.screen_position(me);
        self.deliver(PointerKind::Leave, mx as f32 - sx, my as f32 - sy, 0, 0, 0);
    }
}

impl Widget for CanvasWidget {
    fn hit_test(&self, element: &Element, x: f32, y: f32) -> bool {
        self.canvas.takes_input() && element.contains_local(x, y)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        self.canvas.takes_input()
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.canvas.takes_input()
    }

    // The plugin draws what a hover shows; the pointer resting on the
    // canvas does not end it.
    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        match e.kind {
            kind::MOUSE_DOWN => self.press(ui, me, 1, e),
            kind::RIGHT_DOWN => self.press(ui, me, 2, e),
            kind::MIDDLE_DOWN => self.press(ui, me, 3, e),
            kind::MOUSE_UP => self.release(ui, me, 1, e),
            kind::RIGHT_UP => self.release(ui, me, 2, e),
            kind::MIDDLE_UP => self.release(ui, me, 3, e),
            kind::MOUSE_MOVE => match self.held {
                Some(button) => {
                    self.deliver(
                        PointerKind::Move,
                        e.data1 as f32,
                        e.data2 as f32,
                        button,
                        e.modifiers.0 & 7,
                        0,
                    );
                    true
                }
                None => self.hover(ui, e),
            },
            kind::HOVER_ENTER => self.held.is_none() && self.hover(ui, e),
            kind::HOVER_LEAVE => {
                self.leave(ui, me);
                false
            }
            kind::SCROLL if self.canvas.takes_input() => {
                let (mx, my) = ui.mouse();
                let (sx, sy) = ui.screen_position(me);
                self.deliver(
                    PointerKind::Wheel,
                    mx as f32 - sx,
                    my as f32 - sy,
                    0,
                    e.modifiers.0 & 7,
                    e.data0.signum(),
                );
                true
            }
            kind::CAPTURE_CHANGED => match self.held.take() {
                Some(button) => {
                    let (mx, my) = ui.mouse();
                    let (sx, sy) = ui.screen_position(me);
                    self.deliver(
                        PointerKind::Cancelled,
                        mx as f32 - sx,
                        my as f32 - sy,
                        button,
                        0,
                        0,
                    );
                    true
                }
                None => false,
            },
            // The root's reading of a press and release the plugin saw.
            kind::CLICK | kind::DOUBLE_CLICK | kind::RIGHT_CLICK => self.canvas.takes_input(),
            _ => false,
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
        rc.push_clip(0.0, 0.0, w, h);
        let mut depth = 0usize;
        for command in self.canvas.paint.iter() {
            match command {
                PaintCmd::Clear(c) => rc.fill(0.0, 0.0, w, h, color(*c)),
                PaintCmd::FillRect { rect, color: c } => {
                    rc.fill(rect.x, rect.y, rect.w, rect.h, color(*c))
                }
                PaintCmd::StrokeRect {
                    rect,
                    color: c,
                    thickness,
                } => rc.rect_outline(rect.x, rect.y, rect.w, rect.h, color(*c), *thickness),
                PaintCmd::Line {
                    from,
                    to,
                    color: c,
                    thickness,
                } => rc.line(from.0, from.1, to.0, to.1, color(*c), *thickness),
                PaintCmd::Text {
                    text,
                    at,
                    color: c,
                    outline,
                    font: 0,
                } => {
                    if let Some(font) = self.font.as_ref() {
                        rc.string_dat(
                            font,
                            text,
                            at.0,
                            at.1,
                            color(*c),
                            *outline,
                            DEFAULT_OUTLINE_COLOR,
                        );
                    }
                }
                PaintCmd::Text {
                    text,
                    at,
                    color: c,
                    outline,
                    font,
                } => {
                    if let Some(font) = self.fonts.get(font) {
                        font.draw(rc, text, *at, color(*c), *outline);
                    }
                }
                PaintCmd::Image { image, rect, tint } => {
                    if let Some((texture, _, _)) = self.images.get(image) {
                        rc.sprite(
                            *texture,
                            rect.x,
                            rect.y,
                            rect.w,
                            rect.h,
                            (0.0, 0.0, 1.0, 1.0),
                            color(*tint),
                        );
                    }
                }
                PaintCmd::ImageTransformed {
                    image,
                    rect,
                    tint,
                    rotation,
                    pivot,
                    scale_x,
                    scale_y,
                } => {
                    if let Some((texture, _, _)) = self.images.get(image) {
                        rc.sprite_transformed(
                            *texture,
                            rect.x,
                            rect.y,
                            rect.w,
                            rect.h,
                            (0.0, 0.0, 1.0, 1.0),
                            color(*tint),
                            *rotation,
                            (*scale_x, *scale_y),
                            *pivot,
                        );
                    }
                }
                PaintCmd::PushClip(rect) => {
                    rc.push_clip(rect.x, rect.y, rect.w, rect.h);
                    depth += 1;
                }
                PaintCmd::PopClip => {
                    if depth > 0 {
                        rc.pop_clip();
                        depth -= 1;
                    }
                }
            }
        }
        for _ in 0..depth {
            rc.pop_clip();
        }
        rc.pop_clip();
    }
}

/// A markup `<canvas id="...">` (OpenAC `UiMarkupCanvas`): an empty
/// rectangle its window's layout places and sizes, which hosts the plugin's
/// in-markup canvas of the same id. It draws nothing itself and lets clicks
/// through (its element is click-through); the canvas it hosts fills it,
/// rounded to whole pixels, and answers for itself. It clips, so a canvas
/// whose host has no room shows nothing.
#[derive(Debug, Clone)]
pub struct MarkupCanvas {
    /// The plugin's id of the canvas it hosts.
    pub canvas_id: String,
}

/// `size` rounded to whole pixels, when that is at least one each way.
fn whole_pixels(size: (f32, f32)) -> Option<(f32, f32)> {
    let w = (size.0 + 0.5).floor();
    let h = (size.1 + 0.5).floor();
    (w >= 1.0 && h >= 1.0).then_some((w, h))
}

impl MarkupCanvas {
    /// Fits `canvas` (its hosted element) to `host`: at its top-left,
    /// filling it in whole pixels. With no room it keeps its last size and
    /// the host's clip hides it. The tooltip authored on `<canvas>` goes on
    /// the canvas, which answers for the rectangle.
    fn fill(ui: &mut UiRoot, host: ElementId, canvas: ElementId) {
        let Some((size, tooltip)) = ui
            .element(host)
            .map(|e| ((e.width, e.height), e.runtime_tooltip_text.clone()))
        else {
            return;
        };
        if let Some(e) = ui.element_mut(canvas) {
            e.left = 0.0;
            e.top = 0.0;
            if let Some((w, h)) = whole_pixels(size) {
                e.width = w;
                e.height = h;
            }
            e.runtime_tooltip_text = tooltip;
        }
    }
}

impl Widget for MarkupCanvas {
    fn layout_children(&mut self, ui: &mut UiRoot, me: ElementId) -> bool {
        for child in ui.children(me) {
            Self::fill(ui, me, child);
        }
        true
    }
}

/// A `<canvas>` element of a mounted plugin window, offered to the layer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CanvasHost {
    /// The window's plugin.
    pub owner: String,
    /// The canvas id it names.
    pub canvas_id: String,
    /// The element ([`MarkupCanvas`]).
    pub element: ElementId,
}

/// A `<canvas>` element as the layer keeps it: the canvas it hosts, and
/// whether why it shows nothing was said.
#[derive(Debug)]
struct Slot {
    host: CanvasHost,
    hosted: Option<u32>,
    reported: bool,
}

/// Every canvas: those on the screen on one layer of the overlay band, and
/// the in-markup ones in the `<canvas>` elements of their plugins' windows
/// (OpenAC `PluginMarkupCanvases`). Either side of an in-markup pair may
/// come first: a canvas waits for its element, and an element draws
/// nothing until its canvas is registered. Of two elements naming the same
/// canvas the first mounted hosts it; when its window goes, the canvas moves
/// to the other or waits. What shows nothing says why once.
#[derive(Debug)]
pub struct CanvasLayer {
    layer: ElementId,
    elements: BTreeMap<u32, ElementId>,
    /// The `<canvas>` elements, in the order they were mounted.
    slots: Vec<Slot>,
    /// What the session was last told of each in-markup canvas's hosting:
    /// its size while hosted.
    told: BTreeMap<u32, Option<(i32, i32)>>,
    /// The canvases whose waiting was said (or need not be).
    waiting_said: std::collections::BTreeSet<u32>,
    /// The snapshot the session's copy was at: a new one (a window
    /// attached) has heard nothing of the hosting yet.
    snapshot: u64,
    notices: Vec<String>,
}

/// Lets go of a canvas's element: a press it holds is cancelled and a
/// hover left, then the element goes.
fn drop_canvas_element(ui: &mut UiRoot, el: ElementId) {
    if let Some(w) = ui.widget_mut::<CanvasWidget>(el) {
        if let Some(button) = w.held.take() {
            w.deliver(PointerKind::Cancelled, 0.0, 0.0, button, 0, 0);
        }
        if w.hovering.take().is_some() {
            w.deliver(PointerKind::Leave, 0.0, 0.0, 0, 0, 0);
        }
    }
    if ui.captured() == Some(el) {
        ui.release_capture();
    }
    ui.destroy(el);
}

/// What one frame of [`CanvasLayer::sync`] draws canvases with.
struct Paints<'a, 'b> {
    view: &'a PluginUiView,
    image: &'a mut dyn FnMut(&str, u32) -> Option<Icon>,
    font_of: &'a mut dyn FnMut(&FontView) -> CanvasFont,
    font: Option<&'b Arc<UiFont>>,
    outbox: &'a PluginOutbox,
}

impl Paints<'_, '_> {
    fn widget(&self, canvas: &CanvasView, place: Option<CanvasPlace>) -> CanvasWidget {
        CanvasWidget {
            canvas: canvas.clone(),
            images: HashMap::new(),
            fonts: HashMap::new(),
            font: self.font.cloned(),
            outbox: std::rc::Rc::clone(self.outbox),
            held: None,
            hovering: None,
            releases_seen: canvas.release_count,
            place,
        }
    }

    /// The canvas's widget as the copy has it now: its paint, its plugin's
    /// images and fonts, and whether it takes the pointer.
    fn follow(
        &mut self,
        ui: &mut UiRoot,
        el: ElementId,
        canvas: &CanvasView,
        place: Option<CanvasPlace>,
    ) {
        let images: HashMap<u32, Icon> = self
            .view
            .images
            .values()
            .filter(|i| i.owner == canvas.owner.id)
            .filter_map(|i| (self.image)(&i.owner, i.handle).map(|icon| (i.handle, icon)))
            .collect();
        let fonts: HashMap<u32, CanvasFont> = self
            .view
            .fonts
            .values()
            .filter(|f| f.owner == canvas.owner.id)
            .map(|f| (f.handle, (self.font_of)(f)))
            .collect();
        let mut release = false;
        if let Some(w) = ui.widget_mut::<CanvasWidget>(el) {
            w.canvas = canvas.clone();
            w.images = images;
            w.place = place;
            w.fonts = fonts;
            if w.releases_seen != canvas.release_count {
                w.releases_seen = canvas.release_count;
                // The plugin let go: no cancel follows.
                release = w.held.take().is_some();
            }
        }
        if release && ui.captured() == Some(el) {
            ui.release_capture();
        }
        let takes_input = canvas.takes_input();
        if let Some(e) = ui.element_mut(el) {
            e.click_through = !takes_input;
        }
        if !takes_input {
            // It stopped taking the pointer: the hover ends now, though
            // the pointer may not move again, and a listen after starts
            // a new stretch.
            if ui.hovered() == Some(el) {
                ui.leave_hover();
            }
            let (mx, my) = ui.mouse();
            let (sx, sy) = ui.screen_position(el);
            if let Some(w) = ui.widget_mut::<CanvasWidget>(el)
                && w.hovering.take().is_some()
            {
                w.deliver(PointerKind::Leave, mx as f32 - sx, my as f32 - sy, 0, 0, 0);
            }
        }
        ui.set_visible(el, canvas.visible);
    }
}

fn element_name(canvas: &CanvasView) -> String {
    format!(
        "PluginCanvas:{}:{}",
        canvas.owner.id, canvas.descriptor.canvas_id
    )
}

fn pairs(host: &CanvasHost, canvas: &CanvasView) -> bool {
    host.owner == canvas.owner.id && host.canvas_id == canvas.descriptor.canvas_id
}

impl CanvasLayer {
    /// Takes a layer of the band.
    pub fn mount(ui: &mut UiRoot, host: &mut OverlayHost) -> Self {
        Self {
            layer: host.add_layer(ui, LAYER_NAME),
            elements: BTreeMap::new(),
            slots: Vec::new(),
            told: BTreeMap::new(),
            waiting_said: std::collections::BTreeSet::new(),
            snapshot: 0,
            notices: Vec::new(),
        }
    }

    /// The layer.
    #[must_use]
    pub fn layer(&self) -> ElementId {
        self.layer
    }

    /// The element of canvas `id`.
    #[must_use]
    pub fn element(&self, id: u32) -> Option<ElementId> {
        self.elements.get(&id).copied()
    }

    /// The `<canvas>` element hosting canvas `id`, if one does.
    #[must_use]
    pub fn host_of(&self, id: u32) -> Option<ElementId> {
        self.slots
            .iter()
            .find(|s| s.hosted == Some(id))
            .map(|s| s.host.element)
    }

    /// Why canvases and `<canvas>` elements show nothing, each said once,
    /// since the last call.
    pub fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }

    /// Takes canvas `id` out of whatever element hosts it.
    fn unhost(&mut self, ui: &mut UiRoot, id: u32) {
        for slot in &mut self.slots {
            if slot.hosted == Some(id) {
                slot.hosted = None;
            }
        }
        if let Some(el) = self.elements.remove(&id) {
            drop_canvas_element(ui, el);
        }
    }

    /// The `<canvas>` elements now: one gone (its window went) gives up
    /// its canvas, which waits without saying so (the plugin closed that
    /// window, or the client is shutting down); a new one goes last.
    fn follow_hosts(&mut self, ui: &mut UiRoot, hosts: &[CanvasHost]) {
        let gone: Vec<usize> = self
            .slots
            .iter()
            .enumerate()
            .filter(|(_, s)| ui.element(s.host.element).is_none() || !hosts.contains(&s.host))
            .map(|(i, _)| i)
            .collect();
        for i in gone.into_iter().rev() {
            let slot = self.slots.remove(i);
            if let Some(id) = slot.hosted {
                self.unhost(ui, id);
                self.waiting_said.insert(id);
            }
        }
        for host in hosts {
            if !self.slots.iter().any(|s| s.host == *host) {
                self.slots.push(Slot {
                    host: host.clone(),
                    hosted: None,
                    reported: false,
                });
            }
        }
    }

    /// One frame: the canvases as the copy has them, those on the screen
    /// placed on the band's viewport and the in-markup ones in the
    /// `<canvas>` elements of `hosts` (in the order their windows mounted),
    /// with their plugins' images (`image`: a plugin's handle to its
    /// texture) and fonts (`font_of`: a font to its textures). The session
    /// hears through `outbox` when an in-markup canvas is hosted, at what
    /// size, and when it no longer is.
    #[allow(clippy::too_many_arguments)]
    pub fn sync(
        &mut self,
        ui: &mut UiRoot,
        host: &OverlayHost,
        view: &PluginUiView,
        hosts: &[CanvasHost],
        image: &mut dyn FnMut(&str, u32) -> Option<Icon>,
        font_of: &mut dyn FnMut(&FontView) -> CanvasFont,
        font: Option<&Arc<UiFont>>,
        outbox: &PluginOutbox,
    ) {
        if self.snapshot != view.snapshots {
            self.snapshot = view.snapshots;
            self.told.clear();
        }
        let gone: Vec<u32> = self
            .elements
            .keys()
            .copied()
            .filter(|id| view.canvas(*id).is_none())
            .collect();
        for id in gone {
            self.unhost(ui, id);
        }
        self.told.retain(|id, _| view.canvas(*id).is_some());
        self.waiting_said.retain(|id| view.canvas(*id).is_some());
        self.follow_hosts(ui, hosts);
        let mut paints = Paints {
            view,
            image,
            font_of,
            font,
            outbox,
        };
        let viewport = host.viewport();
        for canvas in &view.canvases {
            if canvas.descriptor.in_markup {
                self.sync_in_markup(ui, canvas, &mut paints);
                continue;
            }
            let size = (canvas.size.0 as f32, canvas.size.1 as f32);
            let (x, y) = canvas_origin(canvas.anchor, canvas.offset, size, viewport);
            let place = CanvasPlace {
                origin: (x, y),
                layer: viewport,
            };
            let el = *self.elements.entry(canvas.id).or_insert_with(|| {
                let mut e = Element::at(x, y, size.0, size.1).with_anchors(AnchorEdges::NONE);
                e.name = Some(element_name(canvas));
                e.captures_pointer_drag = true;
                let el = ui.insert(e, paints.widget(canvas, Some(place)));
                OverlayHost::add_to_layer(ui, self.layer, el, true);
                el
            });
            if let Some(e) = ui.element_mut(el) {
                e.left = x;
                e.top = y;
                e.width = size.0;
                e.height = size.1;
            }
            paints.follow(ui, el, canvas, Some(place));
        }
        self.report_idle(view);
        ui.set_visible(
            self.layer,
            view.canvases.iter().any(|c| !c.descriptor.in_markup),
        );
    }

    /// One in-markup canvas this frame: hosted by the first free element
    /// naming it, else waiting; the session told of a change.
    fn sync_in_markup(
        &mut self,
        ui: &mut UiRoot,
        canvas: &CanvasView,
        paints: &mut Paints<'_, '_>,
    ) {
        let id = canvas.id;
        let hosted = self.slots.iter().position(|s| s.hosted == Some(id));
        let slot = hosted.or_else(|| {
            self.slots
                .iter()
                .position(|s| s.hosted.is_none() && pairs(&s.host, canvas))
        });
        let Some(slot) = slot else {
            if self.waiting_said.insert(id) {
                self.notices.push(format!(
                    "plugin canvas '{}/{}' is registered in markup and waits for a <canvas id=\"{}\"> \
                     in one of the plugin's windows; nothing is drawn until one is mounted",
                    canvas.owner.id, canvas.descriptor.canvas_id, canvas.descriptor.canvas_id
                ));
            }
            self.tell(id, None, paints.outbox);
            return;
        };
        let parent = self.slots[slot].host.element;
        let el = match self.elements.get(&id) {
            Some(el) if hosted.is_some() => *el,
            _ => {
                let mut e = Element::at(0.0, 0.0, canvas.size.0 as f32, canvas.size.1 as f32)
                    .with_anchors(AnchorEdges::NONE);
                e.name = Some(element_name(canvas));
                e.captures_pointer_drag = true;
                let el = ui.add(parent, e, paints.widget(canvas, None));
                MarkupCanvas::fill(ui, parent, el);
                self.slots[slot].hosted = Some(id);
                self.elements.insert(id, el);
                el
            }
        };
        paints.follow(ui, el, canvas, None);
        let size = ui
            .element(el)
            .map(|e| (e.width as i32, e.height as i32))
            .filter(|s| s.0 >= 1 && s.1 >= 1);
        self.tell(id, size, paints.outbox);
    }

    /// Tells the session an in-markup canvas's hosting, when it changed.
    fn tell(&mut self, canvas: u32, size: Option<(i32, i32)>, outbox: &PluginOutbox) {
        if self.told.get(&canvas) != Some(&size) {
            self.told.insert(canvas, size);
            outbox
                .borrow_mut()
                .push(UiInput::CanvasHosted { canvas, size });
        }
    }

    /// Says once, for each `<canvas>` element still empty, why it shows
    /// nothing when that is known: another window already shows its canvas,
    /// or its canvas was registered for the screen. One whose canvas is not
    /// registered yet just waits.
    fn report_idle(&mut self, view: &PluginUiView) {
        for slot in &mut self.slots {
            if slot.reported || slot.hosted.is_some() {
                continue;
            }
            let Some(canvas) = view.canvases.iter().find(|c| pairs(&slot.host, c)) else {
                continue;
            };
            slot.reported = true;
            let where_ = format!(
                "<canvas id=\"{}\"> of plugin '{}'",
                slot.host.canvas_id, slot.host.owner
            );
            self.notices.push(if canvas.descriptor.in_markup {
                format!(
                    "{where_} shows nothing: another window of the plugin already shows that \
                     canvas; it takes over if that window goes away"
                )
            } else {
                format!(
                    "{where_} shows nothing: the plugin registered canvas '{}' without in_markup, \
                     so it sits on the screen by itself",
                    slot.host.canvas_id
                )
            });
        }
    }
}

#[cfg(test)]
mod font_tests {
    use ac_plugin_ui::fonts::{DEFAULT_RANGES, MAXIMUM_GLYPHS, NOTO_SANS_REGULAR, bake};

    use super::*;
    use crate::draw::{DrawCmd, DrawList, NoSprites, Quad};

    const ATLAS: TextureRef = TextureRef::Runtime(70);
    const SHARP: TextureRef = TextureRef::Runtime(71);

    fn font(size: f32) -> Arc<FontBake> {
        Arc::new(
            bake(
                NOTO_SANS_REGULAR.to_vec(),
                size,
                &DEFAULT_RANGES,
                MAXIMUM_GLYPHS,
            )
            .unwrap(),
        )
    }

    fn plain() -> CanvasFont {
        CanvasFont {
            font: font(16.0),
            texture: ATLAS,
            sharp: None,
        }
    }

    fn sharper() -> CanvasFont {
        CanvasFont {
            sharp: Some((2.0, font(32.0), SHARP)),
            ..plain()
        }
    }

    /// The glyph runs of a line of text at `pixels` framebuffer pixels a unit.
    fn runs(
        font: &CanvasFont,
        text: &str,
        at: (f32, f32),
        outline: bool,
        pixels: f32,
    ) -> Vec<(TextureRef, Color, Vec<Quad>)> {
        let mut list = DrawList::default();
        let mut rc =
            RenderContext::new(&mut list, (800.0, 600.0), &NoSprites).with_pixels_per_unit(pixels);
        font.draw(&mut rc, text, at, Color::WHITE, outline);
        list.commands()
            .filter_map(|c| match c {
                DrawCmd::Glyphs {
                    texture,
                    tint,
                    quads,
                } => Some((*texture, *tint, quads.clone())),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn drawing_lands_at_the_measured_positions_in_one_coverage_run() {
        let f = plain();
        let text = "AV To";
        let runs = runs(&f, text, (10.0, 20.0), false, 1.0);
        assert_eq!(runs.len(), 1);
        let (texture, _, quads) = &runs[0];
        assert_eq!(*texture, ATLAS);
        assert_eq!(quads.len(), 4, "a space has nothing to draw");
        let width = f.font.measure_width(text);
        let last = quads.last().unwrap();
        let o = f.font.glyph('o').unwrap();
        assert!((last.x - (10.0 + width - o.advance + o.x)).abs() <= 0.5);
        let baseline = (20.0 + f.font.ascent).floor();
        assert_eq!(quads[0].y, baseline + f.font.glyph('A').unwrap().y);
        for q in quads {
            assert_eq!((q.x.fract(), q.y.fract()), (0.0, 0.0), "on whole pixels");
        }
    }

    #[test]
    fn an_outline_is_eight_copies_in_the_outline_colour_under_the_fill() {
        let runs = runs(&plain(), "A", (10.0, 20.0), true, 1.0);
        assert_eq!(runs.len(), 9);
        for (_, tint, _) in &runs[..8] {
            assert_eq!(*tint, DEFAULT_OUTLINE_COLOR);
        }
        assert_eq!(runs[8].1, Color::WHITE);
        let fill = &runs[8].2[0];
        let offsets: Vec<(f32, f32)> = runs[..8]
            .iter()
            .map(|(_, _, q)| (q[0].x - fill.x, q[0].y - fill.y))
            .collect();
        assert_eq!(offsets, OUTLINE_OFFSETS);
    }

    #[test]
    fn at_a_higher_density_glyphs_come_from_the_sharper_bake_on_device_pixels() {
        let f = sharper();
        let runs = runs(&f, "AV", (10.25, 20.25), false, 2.0);
        assert_eq!(runs.len(), 1);
        let (texture, _, quads) = &runs[0];
        assert_eq!(*texture, SHARP);
        let (_, fine, _) = f.sharp.as_ref().unwrap();
        let a = fine.glyph('A').unwrap();
        assert_eq!((quads[0].w, quads[0].h), (a.width / 2.0, a.height / 2.0));
        for q in quads {
            assert_eq!(((q.x * 2.0).fract(), (q.y * 2.0).fract()), (0.0, 0.0));
        }
        assert_eq!((quads[0].u0, quads[0].v0), (a.u0, a.v0));
    }

    #[test]
    fn a_sharper_bake_changes_no_advance() {
        let (plain, sharp) = (plain(), sharper());
        let at_one = runs(&plain, "AVTo", (0.0, 0.0), false, 1.0);
        let at_two = runs(&sharp, "AVTo", (0.0, 0.0), false, 2.0);
        let last = |r: &[(TextureRef, Color, Vec<Quad>)]| r[0].2.last().unwrap().x;
        assert!((last(&at_one) - last(&at_two)).abs() <= 1.0);
    }

    #[test]
    fn at_one_pixel_a_unit_the_sharper_bake_is_not_used() {
        let runs = runs(&sharper(), "A", (0.0, 0.0), false, 1.0);
        assert_eq!(runs[0].0, ATLAS);
    }

    #[test]
    fn without_a_sharper_bake_a_high_density_display_draws_the_fonts_own_glyphs_on_whole_units() {
        let runs = runs(&plain(), "A", (10.5, 20.5), false, 2.0);
        assert_eq!(runs[0].0, ATLAS);
        let q = &runs[0].2[0];
        assert_eq!((q.x.fract(), q.y.fract()), (0.0, 0.0));
    }

    #[test]
    fn nothing_to_draw_makes_no_run() {
        assert!(runs(&plain(), "", (0.0, 0.0), false, 1.0).is_empty());
        assert!(runs(&plain(), "   ", (0.0, 0.0), false, 1.0).is_empty());
    }
}
