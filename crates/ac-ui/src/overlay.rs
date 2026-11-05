//! What the interface paints over the world without taking input: the
//! shared overlay band (full-screen, click-through layers behind every
//! window), the world labels and icons hung over objects, and the vivid
//! target indicator around the selected object.
//!
//! World positions reach the screen through the camera's view and
//! projection matrices (right-handed, the camera looking down -Z, as the
//! renderer's), with clip = projection × view × position.

use std::collections::{HashMap, HashSet};

use glam::{Mat4, Vec2, Vec3, Vec4};

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::plugin::widgets::Icon;
use crate::radar::{ObjectTraits, RelationshipTraits, blip_color};
use crate::root::UiRoot;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::Panel;

// ── The overlay band ───────────────────────────────────────────────────────

/// Where overlays sit in the root's painting order (smaller is further
/// back). A window sits at the floor and is only raised from there.
pub mod z_order {
    /// Where a real window sits.
    pub const WINDOW_FLOOR: i32 = 0;
    /// The front of the click-through debug overlays mounted straight on
    /// the root; the band starts above it.
    pub const DEBUG_OVERLAY_CEILING: i32 = -9_999;
    /// The back of the band.
    pub const BAND_FLOOR: i32 = -9_000;
    /// The front of the band, well under the windows.
    pub const BAND_CEILING: i32 = -1_000;
    /// Where the shared host root sits.
    pub const SHARED_HOST_ROOT: i32 = BAND_FLOOR;
}

/// The one owner of the overlay band: a full-screen, click-through root
/// with a layer per owner, every layer covering the viewport.
#[derive(Debug)]
pub struct OverlayHost {
    root: ElementId,
    layers: Vec<ElementId>,
    viewport: (f32, f32),
}

/// The band's root, its layers and the indicator's frame: containers that
/// draw nothing themselves (no background, no border), as OpenAC's
/// `UiOverlayLayer` and the indicator's on-screen panel.
fn clear_panel() -> Panel {
    Panel {
        background_color: Color::TRANSPARENT,
        border_color: Color::TRANSPARENT,
        ..Panel::default()
    }
}

fn overlay_element(w: f32, h: f32) -> Element {
    Element {
        click_through: true,
        anchors: AnchorEdges::NONE,
        ..Element::at(0.0, 0.0, w, h)
    }
}

impl OverlayHost {
    /// Mount the band's root on `ui`, sized to it.
    pub fn mount(ui: &mut UiRoot) -> Self {
        let (w, h) = ui.size();
        let top = ui.root();
        let mut e = overlay_element(w, h);
        e.name = Some("UiOverlayHost".to_owned());
        e.z_order = z_order::SHARED_HOST_ROOT;
        let root = ui.add(top, e, clear_panel());
        Self {
            root,
            layers: Vec::new(),
            viewport: (w, h),
        }
    }

    /// The band's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The rectangle the root and every layer cover.
    #[must_use]
    pub fn viewport(&self) -> (f32, f32) {
        self.viewport
    }

    /// A layer of its own for one owner: hidden, click-through, covering
    /// the viewport.
    pub fn add_layer(&mut self, ui: &mut UiRoot, name: &str) -> ElementId {
        let (w, h) = self.viewport;
        let mut e = overlay_element(w, h).with_visible(false);
        e.name = Some(name.to_owned());
        let layer = ui.add(self.root, e, clear_panel());
        self.layers.push(layer);
        layer
    }

    /// Add `child` (with its subtree) to `layer`: made click-through all the
    /// way down unless it `takes_input` for its own rectangle.
    pub fn add_to_layer(ui: &mut UiRoot, layer: ElementId, child: ElementId, takes_input: bool) {
        if !takes_input {
            make_click_through(ui, child);
        }
        ui.add_child(layer, child);
    }

    /// Bring the root and every layer to the interface root's current
    /// size. Called every frame after the root is sized to the screen: the
    /// root is also resized elsewhere (restoring the layout on entering the
    /// world), so the band follows it rather than waiting to be told. The
    /// root is the one source of this rectangle; a world overlay projects
    /// into its camera's viewport but never resizes the layers.
    pub fn follow_root(&mut self, ui: &mut UiRoot) {
        let viewport = ui.size();
        if self.viewport == viewport {
            return;
        }
        self.viewport = viewport;
        for id in std::iter::once(self.root).chain(self.layers.iter().copied()) {
            if let Some(e) = ui.element_mut(id) {
                e.left = 0.0;
                e.top = 0.0;
                e.width = viewport.0;
                e.height = viewport.1;
            }
        }
    }
}

fn make_click_through(ui: &mut UiRoot, id: ElementId) {
    if let Some(e) = ui.element_mut(id) {
        e.click_through = true;
    }
    for c in ui.children(id) {
        make_click_through(ui, c);
    }
}

// ── Projection ────────────────────────────────────────────────────────────

/// The camera as the overlays read it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct OverlayCamera {
    /// World to view.
    pub view: Mat4,
    /// View to clip.
    pub projection: Mat4,
    /// The screen the world is drawn into, pixels.
    pub viewport: (f32, f32),
}

/// A sphere's square on the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScreenRect {
    /// Top-left, pixels.
    pub min: Vec2,
    /// Bottom-right, pixels.
    pub max: Vec2,
    /// The view-space depth (the clip w), metres.
    pub depth: f32,
}

/// The screen square of the sphere at `center` of `radius` metres, or
/// `None` behind the camera or far off the screen; the square is at least
/// `min_side` pixels.
#[must_use]
pub fn project_sphere(
    center: Vec3,
    radius: f32,
    camera: &OverlayCamera,
    min_side: f32,
) -> Option<ScreenRect> {
    let (vw, vh) = camera.viewport;
    let clip = camera.projection * camera.view * Vec4::new(center.x, center.y, center.z, 1.0);
    if clip.w <= 0.001 {
        return None;
    }
    let x = (clip.x / clip.w * 0.5 + 0.5) * vw;
    let y = (1.0 - (clip.y / clip.w * 0.5 + 0.5)) * vh;
    let scale_y = camera.projection.y_axis.y;
    if scale_y <= 0.0 {
        return None;
    }
    let mut r = radius * scale_y * vh / (2.0 * clip.w);
    if x + r < -vw || x - r > 2.0 * vw || y + r < -vh || y - r > 2.0 * vh {
        return None;
    }
    r = r.max(min_side * 0.5);
    Some(ScreenRect {
        min: Vec2::new(x - r, y - r),
        max: Vec2::new(x + r, y + r),
        depth: clip.w,
    })
}

// ── World labels ──────────────────────────────────────────────────────────

/// A label hung over a world object.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldLabel {
    /// The object.
    pub object_id: u32,
    /// The text.
    pub text: String,
    /// Its colour.
    pub color: Color,
    /// Metres above the object's head.
    pub height_offset: f32,
    /// Which line (0 nearest the head, each further one above).
    pub line: u32,
    /// Past this many metres it is not shown; the last fifth fades.
    pub max_range: f32,
    /// Drawn with an outline.
    pub outline: bool,
}

/// Where a label hangs from: the object's base and its height, metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldLabelAnchor {
    /// The object's base.
    pub base: Vec3,
    /// Its height.
    pub height: f32,
}

/// One label on the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LabelPlacement {
    /// Which label.
    pub label: usize,
    /// Text left, pixels.
    pub x: f32,
    /// Text top, pixels.
    pub y: f32,
    /// Distance from the camera, metres.
    pub depth: f32,
    /// How solid.
    pub alpha: f32,
}

/// The last fifth of a label's range is its fade.
pub const FADE_FRACTION: f32 = 0.2;

/// How solid a label is at `depth`: 1 until the fade, 0 at the range.
#[must_use]
pub fn fade(depth: f32, max_range: f32) -> f32 {
    let start = max_range * (1.0 - FADE_FRACTION);
    if depth <= start {
        return 1.0;
    }
    let length = max_range - start;
    if length <= 0.0 {
        return 0.0;
    }
    ((max_range - depth) / length).clamp(0.0, 1.0)
}

/// Fill `out` with this frame's placements, farthest first (so the nearer
/// one wins where two overlap).
pub fn place_labels(
    labels: &[WorldLabel],
    anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
    camera: &OverlayCamera,
    line_height: f32,
    measure: &dyn Fn(&str) -> f32,
    out: &mut Vec<LabelPlacement>,
) {
    out.clear();
    let (vw, vh) = camera.viewport;
    for (index, label) in labels.iter().enumerate() {
        let Some(at) = anchor(label.object_id) else {
            continue;
        };
        let head = at.base + Vec3::new(0.0, 0.0, at.height + label.height_offset);
        let Some(rect) = project_sphere(head, 0.0, camera, 0.0) else {
            continue;
        };
        if rect.depth > label.max_range {
            continue;
        }
        let width = measure(&label.text);
        let x = (rect.min.x + rect.max.x) * 0.5 - width * 0.5;
        let y = (rect.min.y + rect.max.y) * 0.5 - line_height * (label.line as f32 + 1.0);
        if x + width < 0.0 || x > vw || y + line_height < 0.0 || y > vh {
            continue;
        }
        out.push(LabelPlacement {
            label: index,
            x,
            y,
            depth: rect.depth,
            alpha: fade(rect.depth, label.max_range),
        });
    }
    out.sort_by(|a, b| b.depth.total_cmp(&a.depth));
}

/// The one element drawing every label: every outline, then every fill,
/// so the whole set is two runs whatever its size.
#[derive(Debug, Default)]
pub struct WorldLabelLayer {
    font: Option<std::sync::Arc<UiFont>>,
    labels: Vec<WorldLabel>,
    placements: Vec<LabelPlacement>,
}

impl WorldLabelLayer {
    /// How many labels it draws.
    #[must_use]
    pub fn placement_count(&self) -> usize {
        self.placements.len()
    }
}

impl Widget for WorldLabelLayer {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        let Some(font) = self.font.clone() else {
            return;
        };
        for p in &self.placements {
            let label = &self.labels[p.label];
            if !label.outline {
                continue;
            }
            rc.push_alpha(p.alpha);
            rc.string_dat_pass(&font, &label.text, p.x, p.y, DEFAULT_OUTLINE_COLOR, true);
            rc.pop_alpha();
        }
        for p in &self.placements {
            let label = &self.labels[p.label];
            rc.push_alpha(p.alpha);
            rc.string_dat_pass(&font, &label.text, p.x, p.y, label.color, false);
            rc.pop_alpha();
        }
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// Hangs labels over world objects on the overlay band.
#[derive(Debug)]
pub struct WorldLabelOverlay {
    layer: ElementId,
    element: ElementId,
    scratch: Vec<LabelPlacement>,
}

impl WorldLabelOverlay {
    /// Mount on `host` drawing with `font`.
    pub fn mount(
        ui: &mut UiRoot,
        host: &mut OverlayHost,
        font: Option<std::sync::Arc<UiFont>>,
    ) -> Self {
        let layer = host.add_layer(ui, "WorldLabelOverlay");
        let (w, h) = host.viewport();
        let mut e = overlay_element(w, h);
        e.name = Some("WorldLabels".to_owned());
        let element = ui.insert(
            e,
            WorldLabelLayer {
                font,
                ..WorldLabelLayer::default()
            },
        );
        OverlayHost::add_to_layer(ui, layer, element, false);
        Self {
            layer,
            element,
            scratch: Vec::new(),
        }
    }

    /// The drawing element.
    #[must_use]
    pub fn element(&self) -> ElementId {
        self.element
    }

    /// Place `labels` for this frame (none without a camera).
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        labels: &[WorldLabel],
        anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
        camera: Option<&OverlayCamera>,
    ) {
        self.scratch.clear();
        if let Some(camera) = camera.filter(|c| c.viewport.0 > 0.0 && c.viewport.1 > 0.0)
            && !labels.is_empty()
        {
            if let Some(e) = ui.element_mut(self.element) {
                e.width = camera.viewport.0;
                e.height = camera.viewport.1;
            }
            let (line_height, font) = ui
                .widget::<WorldLabelLayer>(self.element)
                .and_then(|w| w.font.clone())
                .map_or((0.0, None), |f| (f.line_height, Some(f)));
            let measure = |s: &str| font.as_ref().map_or(0.0, |f| f.measure_width(s));
            place_labels(
                labels,
                anchor,
                camera,
                line_height,
                &measure,
                &mut self.scratch,
            );
        }
        let shown = !self.scratch.is_empty();
        if let Some(w) = ui.widget_mut::<WorldLabelLayer>(self.element) {
            std::mem::swap(&mut w.placements, &mut self.scratch);
            w.labels.clear();
            w.labels.extend_from_slice(labels);
        }
        ui.set_visible(self.layer, shown);
    }
}

// ── World icons ───────────────────────────────────────────────────────────

/// The most icons in one row over an object.
pub const ICONS_PER_ROW: usize = 8;
/// The gap between icons, between rows and above the labels, pixels.
pub const ICON_GAP: f32 = 2.0;

/// Where an icon is.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum WorldIconAnchor {
    /// Over an object: in rows above its head and its labels.
    Object(u32),
    /// Centred on a position, in the camera's world frame.
    Point(Vec3),
}

/// An image hung in the world at a constant size on the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldIcon {
    /// Where it is.
    pub anchor: WorldIconAnchor,
    /// The image and its size in texels. Without one (a released image,
    /// art the client cannot find) the icon keeps its place but draws
    /// nothing.
    pub image: Option<Icon>,
    /// The side of the square the image is fitted into, UI units.
    pub size: f32,
    /// Multiplied into the image.
    pub tint: Color,
    /// A thin frame around the image.
    pub border: Option<Color>,
    /// Past this many metres it is not shown; the last fifth fades.
    pub max_range: f32,
}

/// One icon on the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct IconPlacement {
    /// Which icon.
    pub icon: usize,
    /// The left of its square, pixels.
    pub x: f32,
    /// The top of its square, pixels.
    pub y: f32,
    /// Distance from the camera to its anchor, metres.
    pub depth: f32,
    /// How solid.
    pub alpha: f32,
}

/// How many label lines hang over each object (its highest line plus one)
/// and the largest upward offset among them, metres.
fn label_stacks(labels: &[WorldLabel]) -> HashMap<u32, (u32, f32)> {
    let mut stacks: HashMap<u32, (u32, f32)> = HashMap::new();
    for label in labels {
        let stack = stacks.entry(label.object_id).or_default();
        stack.0 = stack.0.max(label.line.saturating_add(1));
        if label.height_offset.is_finite() {
            stack.1 = stack.1.max(label.height_offset);
        }
    }
    stacks
}

/// Fill `out` with this frame's placements, farthest first and then by
/// icon, so where icons overlap the nearer anchor's are drawn last. An
/// object's icons sit in rows of [`ICONS_PER_ROW`] above its labels, hung
/// from the same raised point so they stay above them, in the order given
/// even when other icons come between.
pub fn place_icons(
    icons: &[WorldIcon],
    anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
    labels: &[WorldLabel],
    line_height: f32,
    camera: &OverlayCamera,
    out: &mut Vec<IconPlacement>,
) {
    out.clear();
    let stacks = label_stacks(labels);
    let mut placed = HashSet::new();
    for (index, icon) in icons.iter().enumerate() {
        match icon.anchor {
            WorldIconAnchor::Point(point) => place_at_point(index, icon, point, camera, out),
            WorldIconAnchor::Object(id) if placed.insert(id) => {
                let (lines, offset) = stacks.get(&id).copied().unwrap_or_default();
                if let Some(at) = anchor(id) {
                    let head = at.base + Vec3::new(0.0, 0.0, at.height + offset);
                    let raised = lines as f32 * line_height;
                    place_over_object(icons, index, id, head, raised, camera, out);
                }
            }
            WorldIconAnchor::Object(_) => {}
        }
    }
    out.sort_by(|a, b| {
        b.depth
            .total_cmp(&a.depth)
            .then_with(|| a.icon.cmp(&b.icon))
    });
}

fn place_at_point(
    index: usize,
    icon: &WorldIcon,
    point: Vec3,
    camera: &OverlayCamera,
    out: &mut Vec<IconPlacement>,
) {
    let Some(rect) = project_sphere(point, 0.0, camera, 0.0) else {
        return;
    };
    if rect.depth > icon.max_range {
        return;
    }
    let centre = (rect.min + rect.max) * 0.5;
    let (x, y) = (centre.x - icon.size * 0.5, centre.y - icon.size * 0.5);
    if on_screen(x, y, icon.size, camera) {
        out.push(IconPlacement {
            icon: index,
            x,
            y,
            depth: rect.depth,
            alpha: fade(rect.depth, icon.max_range),
        });
    }
}

/// The icons over object `id`, from `first` on: those in range, so a row
/// closes up rather than keeping a hole. The first row's bottom sits a gap
/// above the labels, `raised` pixels above the head; each further row a gap
/// above the tallest icon of the row below.
fn place_over_object(
    icons: &[WorldIcon],
    first: usize,
    id: u32,
    head: Vec3,
    raised: f32,
    camera: &OverlayCamera,
    out: &mut Vec<IconPlacement>,
) {
    let Some(rect) = project_sphere(head, 0.0, camera, 0.0) else {
        return;
    };
    let centre = (rect.min + rect.max) * 0.5;
    let shown: Vec<usize> = (first..icons.len())
        .filter(|&i| {
            icons[i].anchor == WorldIconAnchor::Object(id) && rect.depth <= icons[i].max_range
        })
        .collect();
    let mut bottom = centre.y - raised - ICON_GAP;
    for row in shown.chunks(ICONS_PER_ROW) {
        let sizes = row.iter().map(|&i| icons[i].size);
        let width = sizes.clone().sum::<f32>() + ICON_GAP * (row.len() - 1) as f32;
        let height = sizes.fold(0.0, f32::max);
        let mut x = centre.x - width * 0.5;
        for &i in row {
            let icon = &icons[i];
            let y = bottom - icon.size;
            if on_screen(x, y, icon.size, camera) {
                out.push(IconPlacement {
                    icon: i,
                    x,
                    y,
                    depth: rect.depth,
                    alpha: fade(rect.depth, icon.max_range),
                });
            }
            x += icon.size + ICON_GAP;
        }
        bottom -= height + ICON_GAP;
    }
}

fn on_screen(x: f32, y: f32, size: f32, camera: &OverlayCamera) -> bool {
    let (vw, vh) = camera.viewport;
    x + size >= 0.0 && x <= vw && y + size >= 0.0 && y <= vh
}

/// Where an image of `texels` sits inside a square of side `size`: as large
/// as fits, keeping its shape, centred. Offset from the square's corner,
/// then width and height.
#[must_use]
pub fn fit_icon(size: f32, texels: (f32, f32)) -> (f32, f32, f32, f32) {
    let (width, height) = texels;
    let (mut w, mut h) = (size, size);
    if width > height {
        h = size * height / width;
    } else if height > width {
        w = size * width / height;
    }
    ((size - w) * 0.5, (size - h) * 0.5, w, h)
}

/// The one element drawing every icon: every border, then every image, so
/// the borders are one run and icons sharing an image share another. A
/// border can then sit under a nearer icon's neighbour; that is the price
/// of not paying a run per bordered icon.
#[derive(Debug, Default)]
pub struct WorldIconLayer {
    icons: Vec<WorldIcon>,
    placements: Vec<IconPlacement>,
}

impl WorldIconLayer {
    /// How many icons it draws.
    #[must_use]
    pub fn placement_count(&self) -> usize {
        self.placements.len()
    }

    /// Its texture and where its image is drawn: whole pixels, so it does
    /// not shimmer as its anchor drifts.
    fn image_rect(&self, p: &IconPlacement) -> Option<(TextureRef, f32, f32, f32, f32)> {
        let icon = &self.icons[p.icon];
        let (texture, width, height) = icon.image.filter(|i| i.1 > 0.0 && i.2 > 0.0)?;
        let (dx, dy, w, h) = fit_icon(icon.size, (width, height));
        Some((texture, (p.x + dx).round(), (p.y + dy).round(), w, h))
    }
}

impl Widget for WorldIconLayer {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        for p in &self.placements {
            if let (Some(border), Some((_, x, y, w, h))) =
                (self.icons[p.icon].border, self.image_rect(p))
            {
                rc.push_alpha(p.alpha);
                rc.rect_outline(x - 1.0, y - 1.0, w + 2.0, h + 2.0, border, 1.0);
                rc.pop_alpha();
            }
        }
        for p in &self.placements {
            if let Some((texture, x, y, w, h)) = self.image_rect(p) {
                let tint = self.icons[p.icon].tint;
                rc.push_alpha(p.alpha);
                rc.sprite(texture, x, y, w, h, (0.0, 0.0, 1.0, 1.0), tint);
                rc.pop_alpha();
            }
        }
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// Hangs icons in the world on the overlay band, on a layer of its own
/// mounted before the labels' so their text reads over icons.
#[derive(Debug)]
pub struct WorldIconOverlay {
    layer: ElementId,
    element: ElementId,
    line_height: f32,
    scratch: Vec<IconPlacement>,
}

impl WorldIconOverlay {
    /// Mount on `host`, the labels' lines being `line_height` pixels tall.
    pub fn mount(ui: &mut UiRoot, host: &mut OverlayHost, line_height: f32) -> Self {
        let layer = host.add_layer(ui, "PluginWorldIconOverlay");
        let (w, h) = host.viewport();
        let mut e = overlay_element(w, h);
        e.name = Some("PluginWorldIcons".to_owned());
        let element = ui.insert(e, WorldIconLayer::default());
        OverlayHost::add_to_layer(ui, layer, element, false);
        Self {
            layer,
            element,
            line_height,
            scratch: Vec::new(),
        }
    }

    /// The drawing element.
    #[must_use]
    pub fn element(&self) -> ElementId {
        self.element
    }

    /// Place `icons` for this frame (none without a camera).
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        icons: &[WorldIcon],
        anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
        labels: &[WorldLabel],
        camera: Option<&OverlayCamera>,
    ) {
        self.scratch.clear();
        if let Some(camera) = camera.filter(|c| c.viewport.0 > 0.0 && c.viewport.1 > 0.0)
            && !icons.is_empty()
        {
            if let Some(e) = ui.element_mut(self.element) {
                e.width = camera.viewport.0;
                e.height = camera.viewport.1;
            }
            place_icons(
                icons,
                anchor,
                labels,
                self.line_height,
                camera,
                &mut self.scratch,
            );
        }
        let shown = !self.scratch.is_empty();
        if let Some(w) = ui.widget_mut::<WorldIconLayer>(self.element) {
            std::mem::swap(&mut w.placements, &mut self.scratch);
            w.icons.clear();
            w.icons.extend_from_slice(icons);
        }
        ui.set_visible(self.layer, shown);
    }
}

// ── World lines ───────────────────────────────────────────────────────────

/// How near the camera a segment is cut off, metres of view depth.
pub const LINE_NEAR_DEPTH: f32 = 0.01;
/// A world line's width, pixels.
pub const WORLD_LINE_THICKNESS: f32 = 1.0;

/// One segment drawn over the world.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldLine {
    /// One end, in the camera's world frame.
    pub from: Vec3,
    /// The other end.
    pub to: Vec3,
    /// Its colour.
    pub color: Color,
}

/// The part of the segment from `from` to `to` in front of the camera, on
/// the screen in pixels; `None` when all of it is behind.
#[must_use]
pub fn project_segment(from: Vec3, to: Vec3, camera: &OverlayCamera) -> Option<(Vec2, Vec2)> {
    let m = camera.projection * camera.view;
    let mut a = m * Vec4::new(from.x, from.y, from.z, 1.0);
    let mut b = m * Vec4::new(to.x, to.y, to.z, 1.0);
    if a.w < LINE_NEAR_DEPTH && b.w < LINE_NEAR_DEPTH {
        return None;
    }
    // Cut the segment where it crosses the near depth (clip space is
    // linear along it).
    if a.w < LINE_NEAR_DEPTH {
        a = a.lerp(b, (LINE_NEAR_DEPTH - a.w) / (b.w - a.w));
    } else if b.w < LINE_NEAR_DEPTH {
        b = b.lerp(a, (LINE_NEAR_DEPTH - b.w) / (a.w - b.w));
    }
    let (vw, vh) = camera.viewport;
    let screen = |c: Vec4| {
        Vec2::new(
            (c.x / c.w * 0.5 + 0.5) * vw,
            (1.0 - (c.y / c.w * 0.5 + 0.5)) * vh,
        )
    };
    Some((screen(a), screen(b)))
}

/// The one element drawing a set of screen segments.
#[derive(Debug, Default)]
pub struct WorldLineLayer {
    segments: Vec<(Vec2, Vec2, Color)>,
}

impl WorldLineLayer {
    /// The segments on the screen, pixels.
    #[must_use]
    pub fn segments(&self) -> &[(Vec2, Vec2, Color)] {
        &self.segments
    }
}

impl Widget for WorldLineLayer {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        for &(a, b, color) in &self.segments {
            rc.line(a.x, a.y, b.x, b.y, color, WORLD_LINE_THICKNESS);
        }
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// Draws world segments on a layer of the overlay band, over the world
/// (not hidden behind it).
#[derive(Debug)]
pub struct WorldLineOverlay {
    layer: ElementId,
    element: ElementId,
}

impl WorldLineOverlay {
    /// Mount a layer called `name` on `host`.
    pub fn mount(ui: &mut UiRoot, host: &mut OverlayHost, name: &str) -> Self {
        let layer = host.add_layer(ui, name);
        let (w, h) = host.viewport();
        let mut e = overlay_element(w, h);
        e.name = Some(format!("{name}Lines"));
        let element = ui.insert(e, WorldLineLayer::default());
        OverlayHost::add_to_layer(ui, layer, element, false);
        Self { layer, element }
    }

    /// The layer.
    #[must_use]
    pub fn layer(&self) -> ElementId {
        self.layer
    }

    /// The drawing element.
    #[must_use]
    pub fn element(&self) -> ElementId {
        self.element
    }

    /// Project `lines` for this frame (nothing without a camera).
    pub fn tick(&mut self, ui: &mut UiRoot, lines: &[WorldLine], camera: Option<&OverlayCamera>) {
        let mut segments = Vec::new();
        if let Some(camera) = camera.filter(|c| c.viewport.0 > 0.0 && c.viewport.1 > 0.0)
            && !lines.is_empty()
        {
            if let Some(e) = ui.element_mut(self.element) {
                e.width = camera.viewport.0;
                e.height = camera.viewport.1;
            }
            segments.reserve(lines.len());
            for line in lines {
                if let Some((a, b)) = project_segment(line.from, line.to, camera) {
                    segments.push((a, b, line.color));
                }
            }
        }
        let shown = !segments.is_empty();
        if let Some(w) = ui.widget_mut::<WorldLineLayer>(self.element) {
            w.segments = segments;
        }
        ui.set_visible(self.layer, shown);
    }
}

// ── The vivid target indicator ────────────────────────────────────────────

/// What the indicator needs of the selected object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VividTargetInfo {
    /// The selection sphere's centre, world.
    pub center: Vec3,
    /// Its radius, metres.
    pub radius: f32,
    /// The item type.
    pub item_type: u32,
    /// The public description bits.
    pub description_flags: u32,
    /// The server's blip colour (0: none).
    pub blip_color: u8,
}

/// Where the target is relative to the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum TargetProjection {
    /// No answer (no screen, a bad radius).
    Invalid,
    /// On the screen, its sphere's square.
    OnScreen {
        /// Top-left.
        min: Vec2,
        /// Bottom-right.
        max: Vec2,
    },
    /// Off the screen, the direction to it in degrees (0 right, 90 up).
    OffScreen {
        /// Degrees.
        angle: f32,
    },
}

/// The client enumeration of the indicator's images (1-4 the corners, 5-12
/// the off-screen arrows).
pub const VIVID_TARGET_ENUM_CATEGORY: u32 = 0x1000_0009;
/// The images.
pub const VIVID_TARGET_IMAGE_COUNT: usize = 12;
/// How far the indicator stays from the screen's edge, pixels.
pub const VIEWPORT_MARGIN: f32 = 8.0;

fn positive_modulo(value: f32, modulus: f32) -> f32 {
    let r = value % modulus;
    if r < 0.0 { r + modulus } else { r }
}

/// Where the target is: on the screen (any distance), or the direction to
/// it from the viewer's axes.
#[must_use]
pub fn compute_projection(center: Vec3, radius: f32, camera: &OverlayCamera) -> TargetProjection {
    let (vw, vh) = camera.viewport;
    if vw <= 0.0 || vh <= 0.0 || !radius.is_finite() || radius < 0.0 {
        return TargetProjection::Invalid;
    }
    if let Some(r) = project_sphere(center, radius, camera, 0.0)
        && r.max.x >= 0.0
        && r.min.x <= vw
        && r.max.y >= 0.0
        && r.min.y <= vh
    {
        return TargetProjection::OnScreen {
            min: r.min,
            max: r.max,
        };
    }
    let local = camera.view.transform_point3(center);
    let length = local.length();
    let mut angle = 0.0;
    if length.is_finite() && length > 1e-6 {
        let right = local.x / length;
        let forward = -local.z / length;
        let up = local.y / length;
        let radians = if forward > 0.0 {
            right.atan2(up)
        } else {
            right.atan2(0.0)
        };
        angle = positive_modulo(450.0 - radians.to_degrees(), 360.0);
    }
    TargetProjection::OffScreen { angle }
}

/// The off-screen arrow image (its enumeration value) for a direction.
#[must_use]
pub fn off_screen_image(angle: f32) -> u32 {
    let a = positive_modulo(angle, 360.0);
    if !(23.0..338.0).contains(&a) {
        6
    } else if a < 68.0 {
        7
    } else if a < 113.0 {
        9
    } else if a < 158.0 {
        12
    } else if a < 203.0 {
        11
    } else if a < 248.0 {
        10
    } else if a < 293.0 {
        8
    } else {
        5
    }
}

/// Where the off-screen arrow of `image` size goes for a direction: from
/// the screen's centre towards it, stopped `VIEWPORT_MARGIN` from the edge.
#[must_use]
pub fn off_screen_position(angle: f32, viewport: Vec2, image: Vec2) -> Vec2 {
    let radians = positive_modulo(angle, 360.0).to_radians();
    let direction = Vec2::new(radians.cos(), -radians.sin());
    let half = image * 0.5;
    let center = viewport * 0.5;
    let min_center = Vec2::splat(VIEWPORT_MARGIN) + half;
    let max_center = viewport - Vec2::splat(VIEWPORT_MARGIN) - half;
    let along = |d: f32, lo: f32, hi: f32, c: f32| {
        if d > 1e-6 {
            (hi - c) / d
        } else if d < -1e-6 {
            (lo - c) / d
        } else {
            f32::INFINITY
        }
    };
    let mut distance = along(direction.x, min_center.x, max_center.x, center.x).min(along(
        direction.y,
        min_center.y,
        max_center.y,
        center.y,
    ));
    if !distance.is_finite() || distance < 0.0 {
        distance = 0.0;
    }
    let p = center + direction * distance - half;
    Vec2::new(
        p.x.clamp(
            VIEWPORT_MARGIN,
            VIEWPORT_MARGIN.max(viewport.x - image.x - VIEWPORT_MARGIN),
        ),
        p.y.clamp(
            VIEWPORT_MARGIN,
            VIEWPORT_MARGIN.max(viewport.y - image.y - VIEWPORT_MARGIN),
        ),
    )
}

/// The indicator's frame: where it sits, its size, and its four corners'
/// places inside it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TargetLayout {
    /// Top-left.
    pub position: Vec2,
    /// Size.
    pub size: Vec2,
    /// The corners, top-left, top-right, bottom-right, bottom-left.
    pub corners: [Vec2; 4],
}

/// The corners outside the sphere's square, the whole kept a margin inside
/// the screen.
#[must_use]
pub fn target_layout(min: Vec2, max: Vec2, viewport: Vec2, sizes: [Vec2; 4]) -> TargetLayout {
    let (cw, ch) = (sizes[0].x, sizes[0].y);
    let mut left = min.x - cw;
    let mut top = min.y - ch;
    let mut right = max.x;
    let mut bottom = max.y;
    if left > right {
        left = right - 1.0;
    }
    if top > bottom {
        top = bottom - 1.0;
    }
    left = left.max(VIEWPORT_MARGIN);
    top = top.max(VIEWPORT_MARGIN);
    right = right.max(cw + VIEWPORT_MARGIN);
    bottom = bottom.max(ch + VIEWPORT_MARGIN);
    let max_right = viewport.x - cw - VIEWPORT_MARGIN;
    let max_bottom = viewport.y - ch - VIEWPORT_MARGIN;
    left = left.min(max_right - cw);
    top = top.min(max_bottom - ch);
    right = right.min(max_right);
    bottom = bottom.min(max_bottom);
    let size = Vec2::new((right - left + cw).max(1.0), (bottom - top + ch).max(1.0));
    TargetLayout {
        position: Vec2::new(left, top),
        size,
        corners: [
            Vec2::ZERO,
            Vec2::new(size.x - sizes[1].x, 0.0),
            Vec2::new(size.x - sizes[2].x, size.y - sizes[2].y),
            Vec2::new(0.0, size.y - sizes[3].y),
        ],
    }
}

/// The indicator's colour: the radar's for the object.
#[must_use]
pub fn target_tint(target: &VividTargetInfo, relationship: &RelationshipTraits) -> Color {
    blip_color(
        &ObjectTraits::from_public_weenie_description(
            target.item_type,
            target.description_flags,
            target.blip_color,
        ),
        relationship,
    )
}

/// A tinted image (the indicator's pieces).
#[derive(Debug, Clone, Copy, Default)]
pub struct TintedImage {
    /// The art.
    pub texture: Option<TextureRef>,
    /// Multiplied into it.
    pub tint: Color,
}

impl Widget for TintedImage {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (Some(t), Some((w, h))) = (self.texture, ui.element(me).map(|e| (e.width, e.height)))
        else {
            return;
        };
        rc.sprite(t, 0.0, 0.0, w, h, (0.0, 0.0, 1.0, 1.0), self.tint);
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// One of the indicator's images: its art and size.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TargetImage {
    /// The render surface.
    pub file: u32,
    /// Size, pixels.
    pub size: Vec2,
}

/// The vivid target indicator: four corners around the selected object on
/// the screen, or an arrow at the edge pointing to it.
#[derive(Debug)]
pub struct VividTargetIndicator {
    on_screen: ElementId,
    corners: [ElementId; 4],
    off_screen: ElementId,
    images: [TargetImage; VIVID_TARGET_IMAGE_COUNT],
}

/// The indicator stays behind every window.
pub const VIVID_TARGET_Z_ORDER: i32 = -10_000;

impl VividTargetIndicator {
    /// Mount on `ui` with the twelve images (enumeration values 1-12).
    pub fn mount(ui: &mut UiRoot, images: [TargetImage; VIVID_TARGET_IMAGE_COUNT]) -> Self {
        let top = ui.root();
        let mut e = overlay_element(0.0, 0.0).with_visible(false);
        e.name = Some("VividTargetIndicatorOnScreen".to_owned());
        e.z_order = VIVID_TARGET_Z_ORDER;
        let on_screen = ui.add(top, e, clear_panel());
        let corners = std::array::from_fn(|i| {
            let image = images[i];
            ui.add(
                on_screen,
                overlay_element(image.size.x, image.size.y),
                TintedImage {
                    texture: Some(TextureRef::Surface(image.file)),
                    tint: Color::WHITE,
                },
            )
        });
        let mut e = overlay_element(0.0, 0.0).with_visible(false);
        e.name = Some("VividTargetIndicatorOffScreen".to_owned());
        e.z_order = VIVID_TARGET_Z_ORDER;
        let off_screen = ui.add(top, e, TintedImage::default());
        Self {
            on_screen,
            corners,
            off_screen,
            images,
        }
    }

    /// The on-screen frame and the off-screen arrow.
    #[must_use]
    pub fn elements(&self) -> (ElementId, [ElementId; 4], ElementId) {
        (self.on_screen, self.corners, self.off_screen)
    }

    /// Show the indicator for `target` (none hides it).
    pub fn tick(
        &self,
        ui: &mut UiRoot,
        target: Option<&VividTargetInfo>,
        relationship: &RelationshipTraits,
        camera: Option<&OverlayCamera>,
    ) {
        let (Some(target), Some(camera)) = (target, camera) else {
            self.hide(ui);
            return;
        };
        let projection = compute_projection(target.center, target.radius, camera);
        let tint = target_tint(target, relationship);
        let viewport = Vec2::new(camera.viewport.0, camera.viewport.1);
        match projection {
            TargetProjection::Invalid => self.hide(ui),
            TargetProjection::OnScreen { min, max } => {
                let sizes = std::array::from_fn(|i| self.images[i].size);
                let layout = target_layout(min, max, viewport, sizes);
                if let Some(e) = ui.element_mut(self.on_screen) {
                    e.left = layout.position.x;
                    e.top = layout.position.y;
                    e.width = layout.size.x;
                    e.height = layout.size.y;
                }
                for (i, corner) in self.corners.iter().enumerate() {
                    if let Some(e) = ui.element_mut(*corner) {
                        e.left = layout.corners[i].x;
                        e.top = layout.corners[i].y;
                    }
                    if let Some(w) = ui.widget_mut::<TintedImage>(*corner) {
                        w.tint = tint;
                    }
                }
                ui.set_visible(self.off_screen, false);
                ui.set_visible(self.on_screen, true);
            }
            TargetProjection::OffScreen { angle } => {
                let image = self.images[off_screen_image(angle) as usize - 1];
                let at = off_screen_position(angle, viewport, image.size);
                if let Some(e) = ui.element_mut(self.off_screen) {
                    e.left = at.x;
                    e.top = at.y;
                    e.width = image.size.x;
                    e.height = image.size.y;
                }
                if let Some(w) = ui.widget_mut::<TintedImage>(self.off_screen) {
                    w.texture = Some(TextureRef::Surface(image.file));
                    w.tint = tint;
                }
                ui.set_visible(self.on_screen, false);
                ui.set_visible(self.off_screen, true);
            }
        }
    }

    fn hide(&self, ui: &mut UiRoot) {
        ui.set_visible(self.on_screen, false);
        ui.set_visible(self.off_screen, false);
    }
}

/// The indicator's twelve images from the dats (the client enumeration's
/// render surfaces and their sizes), `None` when any is missing.
#[must_use]
pub fn load_vivid_target_images(
    dats: &ac_dat::DatCollection,
) -> Option<[TargetImage; VIVID_TARGET_IMAGE_COUNT]> {
    use ac_dat::objects::RenderSurface;
    let table = crate::panels::appraisal::client_enum_map(dats, VIVID_TARGET_ENUM_CATEGORY)?;
    let mut images = [TargetImage {
        file: 0,
        size: Vec2::ZERO,
    }; VIVID_TARGET_IMAGE_COUNT];
    for (i, image) in images.iter_mut().enumerate() {
        let file = table.client_id(i as u32 + 1).filter(|f| *f != 0)?;
        let rs = dats.get::<RenderSurface>(file).ok()??;
        if rs.width == 0 || rs.height == 0 {
            return None;
        }
        *image = TargetImage {
            file,
            size: Vec2::new(rs.width as f32, rs.height as f32),
        };
    }
    Some(images)
}
