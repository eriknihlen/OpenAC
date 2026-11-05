//! Plugin UI in the window: the session's plugin windows built from their
//! markup and kept in step with the session's copy (`ac_plugin_ui`), the
//! plugins' canvases on the overlay band, and the plugin dock.
//!
//! Nothing here decides anything: the session owns the plugin UI state
//! (which values, which windows are shown, what a canvas shows), the
//! window draws its copy and reports what the player did into a
//! [`PluginOutbox`] the client sends back to the session.

pub mod canvas;
pub mod color;
pub mod content_size;
pub mod controls;
pub mod dialog;
pub mod dock;
pub mod flex_box;
pub mod fold;
pub mod hotkeys;
pub mod layout;
pub mod list_ext;
mod repeat;
pub mod style;
pub mod title_bar;
pub mod widgets;

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::flex::{FlexMeasurement, FlexNode, FlexSize};
use ac_plugin_ui::list::ColumnPress;
use ac_plugin_ui::markup::{
    Anchors, ColorSource, ColumnKind, Element as Markup, IconKind, IconSource, ListBody,
    MarkupDocument, Values, normalize_icon,
};
use ac_plugin_ui::{ActionValue, PanelView, Rgba, UiInput, UiValue};

use crate::bundled_font::ModernFonts;
use crate::draw::Color;
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId};
use crate::widgets::{Field, Menu, MenuItem, Meter, Panel, Scrollbar};

pub use widgets::{
    Cell, DrawnFiles, FileTag, Icon, MarkupButton, MarkupFrame, MarkupIcon, MarkupLabel,
    MarkupList, MarkupLog, MarkupToggle, Palette,
};

/// What the player did with the plugins' UI, for the client to send to the
/// session, oldest first. Shared by every plugin window, canvas and the
/// dock of one window.
pub type PluginOutbox = Rc<RefCell<Vec<UiInput>>>;

/// Resolves the images markup names: dat images, spell icons and live
/// object icons (the window's art and icon composer), and the image files
/// plugins ship.
pub trait MarkupIcons {
    /// A render surface (a full id).
    fn surface(&mut self, id: u32) -> Option<Icon>;
    /// A spell's icon.
    fn spell(&mut self, spell: u32) -> Option<Icon>;
    /// A live object's composed icon.
    fn item(&mut self, object: u32) -> Option<Icon>;
    /// A PNG or JPEG the plugin ships, by its path in the plugin's folder
    /// (as the markup gives it); nothing where no plugin folder is served.
    fn file(&mut self, _path: &str) -> Option<Icon> {
        None
    }
}

/// The player's choice of look for plugin windows that opt in
/// (`theme="plugin"`; OpenAC `PluginUiTheme`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PluginTheme {
    /// The retail look.
    #[default]
    Classic,
    /// Charcoal and moss.
    Moss,
    /// Warm graphite and brass.
    Brass,
    /// Dark violet and lavender.
    LavenderDusk,
    /// Blue slate and orange.
    Ember,
    /// Dark warm grey and coral red.
    Bloodmoon,
    /// Blue-black and icy cyan.
    Glacier,
    /// Paper and ink, the one light theme.
    Parchment,
}

impl From<ac_plugin_ui::ThemeKind> for PluginTheme {
    fn from(kind: ac_plugin_ui::ThemeKind) -> Self {
        match kind {
            ac_plugin_ui::ThemeKind::Classic => Self::Classic,
            ac_plugin_ui::ThemeKind::Moss => Self::Moss,
            ac_plugin_ui::ThemeKind::Brass => Self::Brass,
            ac_plugin_ui::ThemeKind::LavenderDusk => Self::LavenderDusk,
            ac_plugin_ui::ThemeKind::Ember => Self::Ember,
            ac_plugin_ui::ThemeKind::Bloodmoon => Self::Bloodmoon,
            ac_plugin_ui::ThemeKind::Glacier => Self::Glacier,
            ac_plugin_ui::ThemeKind::Parchment => Self::Parchment,
        }
    }
}

impl PluginTheme {
    /// Its colours (none for the retail look).
    #[must_use]
    pub fn palette(self) -> Option<Palette> {
        match self {
            Self::Classic => None,
            Self::Moss => Some(Palette::MOSS),
            Self::Brass => Some(Palette::BRASS),
            Self::LavenderDusk => Some(Palette::LAVENDER_DUSK),
            Self::Ember => Some(Palette::EMBER),
            Self::Bloodmoon => Some(Palette::BLOODMOON),
            Self::Glacier => Some(Palette::GLACIER),
            Self::Parchment => Some(Palette::PARCHMENT),
        }
    }
}

/// Converts a markup colour.
#[must_use]
pub fn color(c: Rgba) -> Color {
    Color::rgba(c.r, c.g, c.b, c.a)
}

/// A button, tab or toggle: a tab stop that Enter or Space activates once
/// Tab focused it, while a click acts without taking keyboard focus from
/// the game (OpenAC `MarkupDocument`: `AcceptsFocus`, `TabStop`,
/// `FocusOnMouseClick = false`).
fn keyboard_activated(mut e: Element) -> Element {
    e.accepts_focus = true;
    e.focus_on_mouse_click = false;
    e.tab_stop = true;
    e
}

/// A transparent, borderless container.
fn transparent_panel() -> Panel {
    Panel {
        background_color: Color::TRANSPARENT,
        border_color: Color::TRANSPARENT,
        border_thickness: 0.0,
        background_sprite: 0,
    }
}

fn anchors(a: Anchors) -> AnchorEdges {
    let mut edges = AnchorEdges::NONE;
    for (bit, edge) in [
        (Anchors::LEFT, AnchorEdges::LEFT),
        (Anchors::TOP, AnchorEdges::TOP),
        (Anchors::RIGHT, AnchorEdges::RIGHT),
        (Anchors::BOTTOM, AnchorEdges::BOTTOM),
    ] {
        if a.contains(bit) {
            edges |= edge;
        }
    }
    edges
}

fn read_color(src: &ColorSource, values: &dyn Values, palette: Option<&Palette>) -> Color {
    match src {
        ColorSource::Theme { token, classic } => palette
            .and_then(|p| p.token(token))
            .unwrap_or_else(|| color(*classic)),
        ColorSource::Paired { dark, light } => color(if palette.is_some_and(Palette::is_light) {
            *light
        } else {
            *dark
        }),
        _ => color(src.read(values)),
    }
}

fn texts(values: &dyn Values, name: &str) -> Vec<String> {
    match values.value(name) {
        Some(UiValue::TextList(v)) => v.clone(),
        _ => Vec::new(),
    }
}

fn uints(values: &dyn Values, name: &str) -> Vec<u32> {
    match values.value(name) {
        Some(UiValue::UintList(v)) => v.clone(),
        Some(UiValue::IntList(v)) => v.iter().map(|i| u32::try_from(*i).unwrap_or(0)).collect(),
        _ => Vec::new(),
    }
}

fn bools(values: &dyn Values, name: &str) -> Vec<bool> {
    match values.value(name) {
        Some(UiValue::BoolList(v)) => v.clone(),
        _ => Vec::new(),
    }
}

fn bool_value(values: &dyn Values, name: &str) -> bool {
    matches!(values.value(name), Some(UiValue::Bool(true)))
}

fn int_value(values: &dyn Values, name: &str) -> i32 {
    match values.value(name) {
        Some(UiValue::Int(v)) => *v,
        _ => -1,
    }
}

fn icon(icons: &mut dyn MarkupIcons, kind: IconKind, id: u32) -> Option<Icon> {
    if id == 0 {
        return None;
    }
    match kind {
        IconKind::Did => icons.surface(normalize_icon(id)),
        IconKind::Spell => icons.spell(id),
        IconKind::Item => icons.item(id),
        // A file icon names a path, never an id.
        IconKind::File => None,
    }
}

/// A file icon's path: a null or blank one draws nothing, without asking.
fn file_icon(icons: &mut dyn MarkupIcons, path: &str) -> Option<Icon> {
    if path.trim().is_empty() {
        return None;
    }
    icons.file(path)
}

fn icon_of(icons: &mut dyn MarkupIcons, source: &IconSource, values: &dyn Values) -> Option<Icon> {
    match source {
        IconSource::Id { kind, id } => icon(icons, *kind, id.read(values)),
        IconSource::File(path) => file_icon(icons, &path.read(values)?),
    }
}

/// An element's file icon, for it to say when it draws: its path now (none
/// for another kind of icon, or a blank path).
fn file_tag(
    source: Option<&IconSource>,
    values: &dyn Values,
    drawn: &DrawnFiles,
) -> Option<FileTag> {
    match source? {
        IconSource::File(path) => path.read(values).map(|path| FileTag {
            drawn: drawn.clone(),
            path,
        }),
        IconSource::Id { .. } => None,
    }
}

/// A list's row icons: ids, or paths for file icons.
fn row_icons(
    icons: &mut dyn MarkupIcons,
    values: &dyn Values,
    name: &str,
    kind: IconKind,
) -> Vec<Option<Icon>> {
    if kind == IconKind::File {
        return texts(values, name)
            .iter()
            .map(|path| file_icon(icons, path))
            .collect();
    }
    uints(values, name)
        .into_iter()
        .map(|id| icon(icons, kind, id))
        .collect()
}

/// One plugin window built from its markup: its frame and an element per
/// markup element. See [`build_panel`].
#[derive(Debug)]
pub struct PluginPanelController {
    /// The session's number for the window.
    pub panel: u32,
    /// The window's name.
    pub window_name: String,
    /// The frame (the window's root).
    pub root: ElementId,
    document: Arc<MarkupDocument>,
    /// The window's own elements ([`repeat::WINDOW_SET`]) and each
    /// `<repeat>` row's, by set number.
    sets: HashMap<u32, repeat::ElementSet>,
    /// The number the next row's set gets.
    next_set: u32,
    /// The set and markup element each built element belongs to.
    by_element: HashMap<ElementId, (u32, usize)>,
    title: Option<ElementId>,
    minimize: Option<ElementId>,
    outbox: PluginOutbox,
    expected_visible: bool,
    synced: Option<u64>,
    /// The plugin file icons the window drew, as its elements say so.
    drawn: widgets::DrawnFiles,
    /// The window's remembered control values (rows read theirs by key).
    control_state: Option<fold::SharedControlState>,
    /// The window's expander shifts, which rows built later share.
    shifts: fold::Shifts,
    /// What the window noticed while running (a repeat's duplicate keys),
    /// one line each, for the log.
    notices: Vec<String>,
    /// The font the window was built with.
    classic_font: Option<Arc<UiFont>>,
    /// The bundled sans font the modern themes draw text in, when loaded.
    modern_fonts: Option<ModernFonts>,
    /// The title's colour without a theme (`controls.ini`'s `[title] color`,
    /// else white).
    title_color: Color,
    /// The title bar, on a window with one.
    title_bar: Option<ElementId>,
    /// The content area, on a window with chrome or a flex layout.
    content: Option<ElementId>,
    /// The chrome above and below the content area.
    chrome_h: f32,
    /// The size the window had before its content grew it, and the size it
    /// grew to; cleared once the player sizes it otherwise.
    growth: Option<((f32, f32), (f32, f32))>,
}

/// Enters `id`, just built for markup element `index` under a flex
/// container, into that container's layout: a flex group is already an
/// inner node; an absolute group is a leaf of its authored size; any other
/// control is a leaf measured from its content. The container places it, so
/// it keeps no anchors. Its item, when it added one.
fn add_flex_item(
    ui: &mut UiRoot,
    flex_of: &HashMap<Option<usize>, (layout::SharedFlex, Option<usize>)>,
    node: &ac_plugin_ui::markup::Node,
    index: usize,
    id: ElementId,
    attrs: &ac_plugin_ui::markup::FlexItem,
) -> Option<usize> {
    ui.set_anchors(id, AnchorEdges::NONE);
    let nested_flex = matches!(
        node.element,
        Markup::Group {
            layout: Some(_),
            ..
        }
    );
    if nested_flex {
        return None;
    }
    let (shared, parent_item) = flex_of.get(&node.parent)?;
    let mut flex_node = FlexNode::default();
    attrs.apply(&mut flex_node);
    let authored = match &node.element {
        Markup::Group {
            scroll_x, scroll_y, ..
        } => Some(absolute_group_size(
            node.rect.w,
            node.rect.h,
            *scroll_x,
            *scroll_y,
        )),
        _ => None,
    };
    let item = shared
        .borrow_mut()
        .add(*parent_item, id, index, flex_node, false, authored);
    match &node.element {
        Markup::Expander { .. } => {
            // Its h is the expanded height, given only while it is
            // expanded; collapsed it measures as its header.
            shared.borrow_mut().set_expander(item, node.rect.h);
            if let Some(x) = ui.widget_mut::<fold::MarkupExpander>(id) {
                x.flex_placed = true;
            }
        }
        Markup::Label { .. } => {
            if let Some(l) = ui.widget_mut::<MarkupLabel>(id) {
                l.sized_by_layout = true;
            }
        }
        _ => {}
    }
    Some(item)
}

/// An absolute group's content size in a flex container: its authored
/// size, which is also its minimum, except along an axis it scrolls, where
/// it can shrink to a 40-pixel viewport (plus the other axis's bar).
fn absolute_group_size(w: f32, h: f32, scroll_x: bool, scroll_y: bool) -> FlexMeasurement {
    use ac_plugin_ui::flex::MINIMUM_SCROLL_VIEWPORT;
    let size = FlexSize::new(w, h);
    if !scroll_x && !scroll_y {
        return FlexMeasurement::new(size, size);
    }
    let min_w = if scroll_x {
        MINIMUM_SCROLL_VIEWPORT + if scroll_y { layout::BAR_SIZE } else { 0.0 }
    } else {
        w
    };
    let min_h = if scroll_y {
        MINIMUM_SCROLL_VIEWPORT + if scroll_x { layout::BAR_SIZE } else { 0.0 }
    } else {
        h
    };
    FlexMeasurement::new(size, FlexSize::new(min_w.min(w), min_h.min(h)))
}

/// Builds the window of `view` (OpenAC `MarkupDocument.Build`): the frame
/// at the markup's position and size, its title, an element per markup
/// element in document order, and (for a window on the dock) the minimize
/// button. Nothing is shown until [`PluginPanelController::sync`].
pub fn build_panel(
    ui: &mut UiRoot,
    view: &PanelView,
    font: Option<Arc<UiFont>>,
    outbox: PluginOutbox,
) -> PluginPanelController {
    build_panel_with_state(ui, view, font, outbox, None)
}

/// [`build_panel`] with the window's remembered control values: named
/// expanders, splits and lists remember what the player left them at for
/// the character.
#[allow(clippy::too_many_lines)]
pub fn build_panel_with_state(
    ui: &mut UiRoot,
    view: &PanelView,
    font: Option<Arc<UiFont>>,
    outbox: PluginOutbox,
    control_state: Option<fold::SharedControlState>,
) -> PluginPanelController {
    let doc = Arc::clone(&view.document);
    let window_name = view.window_name();
    let shifts: fold::Shifts = Rc::default();
    let spec = &doc.panel;
    let mut frame_element = Element::at(spec.rect.x, spec.rect.y, spec.rect.w, spec.rect.h)
        .with_anchors(AnchorEdges::NONE)
        .with_visible(false);
    frame_element.name = Some(view.window_name());
    frame_element.draggable = true;
    frame_element.resizable = spec.resizable || spec.resize_x || spec.resize_y;
    frame_element.resize_x = spec.resize_x;
    frame_element.resize_y = spec.resize_y;
    frame_element.min_width = spec.min_width;
    frame_element.min_height = spec.min_height;
    let root = ui.insert(
        frame_element,
        MarkupFrame {
            chrome: crate::widgets::NineSliceFrame {
                draw_resize_affordances: spec.resizable,
                ..crate::widgets::NineSliceFrame::default()
            },
            palette: None,
            has_title: false,
        },
    );
    // A window with chrome or a flex layout has a content area inside the
    // border (and title bar) that holds every authored child.
    let content_area = spec.has_content_area();
    let chrome_w = if content_area {
        ac_plugin_ui::chrome::HORIZONTAL_INSETS
    } else {
        0.0
    };
    let chrome_h = if content_area {
        ac_plugin_ui::chrome::vertical_insets_for(spec.title_bar)
    } else {
        0.0
    };
    let title_bar = spec.title_bar.then(|| {
        let bar = title_bar::TitleBar::add(ui, root, spec.rect.w, font.clone());
        if let Some(b) = ui.widget_mut::<title_bar::TitleBar>(bar) {
            b.title = spec
                .title
                .clone()
                .unwrap_or_else(|| view.descriptor.title.clone());
        }
        bar
    });
    // The legacy title label: only on a window without a bar or a layout.
    let title = if spec.title_bar || spec.layout.is_some() {
        None
    } else {
        spec.title.as_ref().map(|t| {
            let mut e = Element::at(LEGACY_TITLE_LEFT, LEGACY_TITLE_TOP, 0.0, 0.0);
            e.click_through = true;
            let mut label = MarkupLabel::new(font.clone());
            label.text.clone_from(t);
            ui.add(root, e, label)
        })
    };
    let content = content_area.then(|| {
        let mut e = Element::at(
            ac_plugin_ui::chrome::BORDER,
            ac_plugin_ui::chrome::top_inset(spec.title_bar),
            spec.rect.w - chrome_w,
            spec.rect.h - chrome_h,
        )
        .with_anchors(AnchorEdges::ALL);
        e.click_through = true;
        e.name = Some("PluginContent".to_owned());
        ui.add(root, e, layout::ContentHost::default())
    });
    let content_parent = content.unwrap_or(root);
    // Each flex root's layout, by the markup element that is its container
    // (`None`: the content area), with the item a nested container is.
    let mut flex_of: HashMap<Option<usize>, (layout::SharedFlex, Option<usize>)> = HashMap::new();
    if let (Some(container), Some(c)) = (&spec.layout, content) {
        let mut node = FlexNode::default();
        container.apply(&mut node);
        let mut layout = flex_box::FlexBox::new(Arc::clone(&doc), node);
        layout.tracks_minimum = true;
        let shared = Rc::new(RefCell::new(layout));
        if let Some(host) = ui.widget_mut::<layout::ContentHost>(c) {
            host.flex = Some(Rc::clone(&shared));
        }
        flex_of.insert(None, (shared, None));
    }
    if let Some(c) = content
        && (spec.scroll_x || spec.scroll_y)
    {
        let area = layout::ScrollArea::new(ui, c, spec.scroll_x, spec.scroll_y);
        if let Some((shared, _)) = flex_of.get(&None) {
            let mut layout = shared.borrow_mut();
            let node = layout.root_node_mut();
            node.scroll_x = spec.scroll_x;
            node.scroll_y = spec.scroll_y;
            node.scrollbar_size = layout::BAR_SIZE;
        }
        if let Some(host) = ui.widget_mut::<layout::ContentHost>(c) {
            host.scroll = Some(area);
        }
    }
    let builder = repeat::Builder {
        doc: &doc,
        font: font.clone(),
        emit: controls::Emit::new(view.id, Rc::clone(&outbox)),
        control_state: control_state.clone(),
        window_name: &window_name,
        state_path: None,
        shifts: Rc::clone(&shifts),
    };
    let mut containers = HashMap::new();
    containers.insert(None, content_parent);
    let window = repeat::build_set(ui, &builder, view, None, containers, flex_of.clone());
    let mut by_element = HashMap::new();
    for (&node, &id) in &window.nodes {
        by_element.insert(id, (repeat::WINDOW_SET, node));
    }
    let mut sets = HashMap::new();
    sets.insert(repeat::WINDOW_SET, window);
    let mut controller = PluginPanelController {
        panel: view.id,
        window_name: view.window_name(),
        root,
        document: Arc::clone(&doc),
        sets,
        next_set: repeat::WINDOW_SET + 1,
        by_element,
        title,
        minimize: None,
        outbox,
        expected_visible: false,
        synced: None,
        drawn: widgets::DrawnFiles::default(),
        control_state,
        shifts,
        notices: Vec::new(),
        classic_font: font.clone(),
        modern_fonts: None,
        title_color: Color::WHITE,
        title_bar,
        content,
        chrome_h,
        growth: None,
    };
    // Each `<repeat>`'s first rows, before the first measurement, so the
    // window opens at a size that holds them.
    controller.update_repeats(ui, view);
    if let Some((shared, _)) = flex_of.get(&None).cloned() {
        // The window's first measurement, with the values and fonts known
        // now (a seeding sync: captions, and what is bound hidden, such as
        // a tabbed window's other pages): it sizes a root that states no
        // w/h, and sets the minimum, so mounting and registration see real
        // geometry. An authored size below the content's minimum opens at
        // the minimum.
        controller.sync(ui, view, 0, &mut NoMarkupIcons, PluginTheme::Classic);
        controller.synced = None;
        let measured = shared.borrow_mut().measure_now(ui);
        let (authored_w, authored_h) = spec.authored_size;
        let (authored_min_w, authored_min_h) = spec.authored_min;
        let min_w = authored_min_w.unwrap_or(measured.minimum.width);
        let min_h = authored_min_h.unwrap_or(measured.minimum.height);
        let content_w = authored_w.unwrap_or(measured.preferred.width).max(min_w);
        let content_h = authored_h.unwrap_or(measured.preferred.height).max(min_h);
        if let Some(e) = ui.element_mut(root) {
            e.width = content_w + chrome_w;
            e.height = content_h + chrome_h;
            e.min_width = ac_plugin_ui::chrome::frame_minimum_width(min_w, spec.title_bar);
            e.min_height = min_h + chrome_h;
        }
        if let Some(c) = content
            && let Some(e) = ui.element_mut(c)
        {
            e.width = content_w;
            e.height = content_h;
        }
    }
    if let Some(bar) = title_bar {
        // A window narrower than its title bar needs opens at the bar's
        // minimum.
        let (width, min_width) = ui
            .element(root)
            .map_or((0.0, 0.0), |e| (e.width, e.min_width));
        let width = if width < ac_plugin_ui::chrome::MINIMUM_BAR_FRAME_WIDTH {
            if let Some(e) = ui.element_mut(root) {
                e.width = min_width;
            }
            if let Some(c) = content
                && let Some(e) = ui.element_mut(c)
            {
                e.width = min_width - chrome_w;
            }
            min_width
        } else {
            width
        };
        title_bar::TitleBar::set_frame_width(ui, bar, width);
        if view.descriptor.show_in_side_panel
            && let Some(close) = ui.widget::<title_bar::TitleBar>(bar).map(|b| b.close)
            && let Some(e) = ui.element_mut(close)
        {
            // The bar's close button takes the dock's minimize button's
            // place and says where the window went.
            e.runtime_tooltip_text = Some(ac_plugin_ui::chrome::DOCKED_CLOSE_TOOLTIP.to_owned());
        }
    }
    // A window with a title bar already has a close button, which does what
    // "-" would; a layout window without a bar has content right up to its
    // border, where "-" would sit over it, and is closed from its dock
    // slot, as the author chose no chrome.
    let frame_width = ui.element(root).map_or(spec.rect.w, |e| e.width);
    controller.minimize = (view.descriptor.show_in_side_panel && !content_area).then(|| {
        let mut e = Element::at((frame_width - 23.0).max(8.0), 3.0, 18.0, 17.0)
            .with_anchors(AnchorEdges::TOP | AnchorEdges::RIGHT)
            .with_z_order(1);
        e.name = Some("PluginMinimize".to_owned());
        e.runtime_tooltip_text = Some("Minimize to the plugin dock".to_owned());
        let button = MarkupButton {
            text: "\u{2013}".to_owned(),
            background: MINIMIZE_BACKGROUND,
            border: MINIMIZE_BORDER,
            ..MarkupButton::new(font.clone())
        };
        ui.add(root, e, button)
    });
    // The whole document now sits at its first sizes, so this is the one
    // moment every anchor margin can be read off the layout its author
    // wrote.
    ui.capture_authored_anchor_baselines(root);
    controller
}

/// Where the legacy title label sits in Classic.
const LEGACY_TITLE_LEFT: f32 = 8.0;
/// Its top.
const LEGACY_TITLE_TOP: f32 = 4.0;

/// The minimize button's fill in Classic.
const MINIMIZE_BACKGROUND: Color = Color::rgba(0.02, 0.02, 0.015, 0.94);
/// Its border in Classic.
const MINIMIZE_BORDER: Color = Color::rgba(0.58, 0.46, 0.17, 1.0);

/// No icons: the build's seeding sync shows no images (the first real sync
/// resolves them).
struct NoMarkupIcons;

/// The look one sync shows: the palette on a themed window under a modern
/// theme, and with the bundled font loaded the text font and a menu's
/// caption font for the theme (none: the fonts stay as built), and the
/// weight a list's header takes under the theme.
#[derive(Debug, Default)]
struct Look {
    palette: Option<Palette>,
    fonts: Option<ThemeFonts>,
    heading: Option<Arc<UiFont>>,
}

/// A theme's text font and a menu's caption font.
type ThemeFonts = (Option<Arc<UiFont>>, Option<Arc<UiFont>>);

impl MarkupIcons for NoMarkupIcons {
    fn surface(&mut self, _id: u32) -> Option<Icon> {
        None
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

impl PluginPanelController {
    /// The window with the bundled sans font: a themed (`theme="plugin"`)
    /// window draws its title, labels, buttons, tabs, toggles, fields,
    /// menus, logs and lists in it under a modern theme, and in the font it
    /// was built with under Classic (OpenAC `PluginMarkupTheme` with
    /// `PluginUiThemeSettings.ModernFont`). Without it, every theme keeps
    /// the built font.
    #[must_use]
    pub fn with_modern_fonts(mut self, fonts: Option<ModernFonts>) -> Self {
        self.modern_fonts = fonts;
        self
    }

    /// The window's title in `color` where no theme colours it (OpenAC
    /// `MarkupDocument.Build`: the player's `controlsntrols.ini` `[title]
    /// color`, else white).
    #[must_use]
    pub fn with_title_color(mut self, ui: &mut UiRoot, color: Color) -> Self {
        self.title_color = color;
        if let Some(title) = self.title
            && let Some(label) = ui.widget_mut::<MarkupLabel>(title)
        {
            label.color = color;
        }
        self
    }

    /// The element built for markup element `node`.
    #[must_use]
    pub fn element(&self, node: usize) -> Option<ElementId> {
        self.sets
            .get(&repeat::WINDOW_SET)
            .and_then(|s| s.nodes.get(&node).copied())
    }

    /// The minimize button, on a window that is on the dock.
    #[must_use]
    pub fn minimize_button(&self) -> Option<ElementId> {
        self.minimize
    }

    /// The title bar, on a window with one.
    #[must_use]
    pub fn title_bar(&self) -> Option<ElementId> {
        self.title_bar
    }

    /// The content area, on a window with chrome or a flex layout.
    #[must_use]
    pub fn content_root(&self) -> Option<ElementId> {
        self.content
    }

    /// The revision the window's saved geometry is kept under.
    #[must_use]
    pub fn geometry_revision(&self) -> i32 {
        self.document.panel.geometry_revision
    }

    /// The session revision the window's elements were last shown at
    /// (none before its first sync): a [`Self::sync`] at another revision
    /// reads every element, and its icons, again.
    #[must_use]
    pub fn synced_revision(&self) -> Option<u64> {
        self.synced
    }

    /// The plugin file icons the window drew since the last call, by path
    /// as its markup gives them: only elements drawn say so (not a hidden
    /// one, a closed tab's page or a list row scrolled away).
    #[must_use]
    pub fn take_drawn_files(&self) -> Vec<String> {
        self.drawn.take()
    }

    /// What the build ignored, one line each.
    #[must_use]
    pub fn warnings(&self) -> &[String] {
        &self.document.warnings
    }

    /// Keeps the frame's minimum on the content's: when a flex content area
    /// finds its content needs more room (a longer caption), the frame's
    /// minimum becomes that plus the chrome, unless the markup states its
    /// own `minw`/`minh`, and the window grows to it. When the content needs
    /// less again, a window it grew goes back toward the size it had
    /// before, unless it was sized otherwise in between.
    fn track_content_minimum(&mut self, ui: &mut UiRoot) {
        let Some(content) = self.content else {
            return;
        };
        let Some(minimum) = ui
            .widget::<layout::ContentHost>(content)
            .and_then(|h| h.flex.as_ref().map(Rc::clone))
            .and_then(|f| f.borrow_mut().take_minimum_changed())
        else {
            return;
        };
        let spec = &self.document.panel;
        let (authored_min_w, authored_min_h) = spec.authored_min;
        let title_bar = self.title_bar.is_some();
        let Some((current, min)) = ui.element_mut(self.root).map(|e| {
            e.min_width = ac_plugin_ui::chrome::frame_minimum_width(
                authored_min_w.unwrap_or(minimum.width),
                title_bar,
            );
            e.min_height = authored_min_h.unwrap_or(minimum.height) + self.chrome_h;
            ((e.width, e.height), (e.min_width, e.min_height))
        }) else {
            return;
        };
        if let Some((_, grown_to)) = self.growth
            && current != grown_to
        {
            self.growth = None;
        }
        let from = self.growth.map_or(current, |(before, _)| before);
        if ui.window(&self.window_name).is_some() {
            ui.enforce_minimum_size(&self.window_name, from.0, from.1);
        } else if let Some(e) = ui.element_mut(self.root) {
            e.width = from.0.max(min.0);
            e.height = from.1.max(min.1);
        }
        let now = ui.element(self.root).map_or(from, |e| (e.width, e.height));
        self.growth = (now != from).then_some((from, now));
    }

    fn push(&self, input: UiInput) {
        self.outbox.borrow_mut().push(input);
    }

    /// Fires `handler` of an element of set `set` (a row's handler is
    /// named after its row).
    fn action(&self, set: u32, handler: &Option<String>, value: ActionValue) -> bool {
        let Some(handler) = handler else {
            return false;
        };
        let handler = match self.sets.get(&set) {
            Some(s) => format!("{}{handler}", s.prefix.borrow()),
            None => handler.clone(),
        };
        self.push(UiInput::Action {
            panel: self.panel,
            handler,
            value,
        });
        true
    }

    /// Shows the window as the session wants it and every element as the
    /// panel's values say (OpenAC's per-frame bindings, re-read here when
    /// the session's copy changed: `revision`).
    #[allow(clippy::too_many_lines)]
    pub fn sync(
        &mut self,
        ui: &mut UiRoot,
        view: &PanelView,
        revision: u64,
        icons: &mut dyn MarkupIcons,
        theme: PluginTheme,
    ) {
        let shown = view.shown();
        if shown != ui.is_window_visible(&self.window_name) || self.synced.is_none() {
            self.expected_visible = shown;
            if shown {
                ui.show_window(&self.window_name);
            } else {
                ui.hide_window(&self.window_name);
            }
        }
        if self.synced == Some(revision) {
            return;
        }
        self.synced = Some(revision);
        let doc = Arc::clone(&self.document);
        let palette = if doc.panel.themed {
            theme.palette()
        } else {
            None
        };
        let p = palette.as_ref();
        // With the bundled font loaded: the text font and a menu's caption
        // font for this theme (OpenAC `PluginMarkupTheme`'s font bindings;
        // a menu is built without a caption font of its own).
        let fonts = self.modern_fonts.as_ref().map(|modern| match p {
            Some(_) => (
                Some(Arc::clone(&modern.regular)),
                Some(Arc::clone(&modern.regular)),
            ),
            None => (self.classic_font.clone(), None),
        });
        let text_font = fonts.as_ref().map(|(text, _)| text);
        // The title weight under a theme; Classic's own font under Classic.
        let title_font = match (&self.modern_fonts, p) {
            (Some(modern), Some(_)) => Some(Arc::clone(&modern.semibold)),
            _ => text_font
                .cloned()
                .unwrap_or_else(|| self.classic_font.clone()),
        };
        let has_title = self.title.is_some() || self.title_bar.is_some();
        if let Some(frame) = ui.widget_mut::<MarkupFrame>(self.root) {
            frame.palette = palette;
            frame.has_title = has_title;
        }
        if let Some(title) = self.title {
            let line = title_font.as_ref().map_or(14.0, |f| f.line_height);
            if let Some(label) = ui.widget_mut::<MarkupLabel>(title) {
                label.color = p.map_or(self.title_color, |p| p.text);
                label.outline = p.is_none();
                label.font.clone_from(&title_font);
            }
            // Under a theme the title sits in the header band.
            let at = match p {
                Some(_) => (
                    title_bar::THEMED_TEXT_LEFT,
                    ((style::HEADER_HEIGHT - line) / 2.0).round().max(0.0),
                ),
                None => (LEGACY_TITLE_LEFT, LEGACY_TITLE_TOP),
            };
            if let Some(e) = ui.element_mut(title)
                && (e.left, e.top) != at
            {
                (e.left, e.top) = at;
                ui.capture_current_anchor_baseline(title);
            }
        }
        if let Some(bar) = self.title_bar {
            title_bar::TitleBar::set_palette(ui, bar, palette, title_font, self.title_color);
        }
        if let Some(content) = self.content
            && let Some(area) = ui
                .widget::<layout::ContentHost>(content)
                .and_then(|h| h.scroll.clone())
        {
            area.apply_theme(ui, p);
        }
        if let Some(minimize) = self.minimize
            && let Some(b) = ui.widget_mut::<MarkupButton>(minimize)
        {
            // The "-" follows the window's theme as the bar's close
            // button does: a button's face, edge and caption in the palette.
            b.palette = palette;
            b.background = p.map_or(MINIMIZE_BACKGROUND, |p| p.field);
            b.border = p.map_or(MINIMIZE_BORDER, |p| p.border);
            b.text_color = p.map_or(Color::WHITE, |p| p.text);
            b.outline = p.is_none();
            if let Some(f) = text_font {
                b.font.clone_from(f);
            }
        }
        let heading = self
            .modern_fonts
            .as_ref()
            .filter(|_| p.is_some())
            .map(|m| Arc::clone(&m.semibold));
        let look = Look {
            palette,
            fonts,
            heading,
        };
        self.sync_set(ui, repeat::WINDOW_SET, view, Some(view), icons, &look);
        self.sync_repeats(ui, repeat::WINDOW_SET, view, icons, &look);
    }

    /// Shows set `set`'s elements as `values` says (OpenAC's per-frame
    /// bindings, read through the set's binding source: the panel's values
    /// for the window's own elements, a row's item for a `<repeat>` row's).
    /// `overrides` carries the plugin's by-name labels and visibility,
    /// which only the window's own elements take.
    #[allow(clippy::too_many_lines)]
    fn sync_set(
        &mut self,
        ui: &mut UiRoot,
        set: u32,
        values: &dyn Values,
        overrides: Option<&PanelView>,
        icons: &mut dyn MarkupIcons,
        look: &Look,
    ) {
        let doc = Arc::clone(&self.document);
        let drawn = self.drawn.clone();
        let Some(s) = self.sets.get_mut(&set) else {
            return;
        };
        let palette = look.palette;
        let p = palette.as_ref();
        let fonts = &look.fonts;
        let text_font = fonts.as_ref().map(|(text, _)| text);
        for k in 0..s.order.len() {
            let index = s.order[k];
            let node = &doc.nodes[index];
            if let Markup::Repeat { .. } = node.element {
                continue;
            }
            let el = s.nodes[&index];
            if let (Some(menu), Some(&menu_el)) = (&node.common.context_menu, s.menus.get(&index)) {
                let items = menu.items.read(values);
                let enabled = menu
                    .enabled
                    .as_ref()
                    .map(|e| bools(values, e))
                    .unwrap_or_default();
                if let Some(w) = ui.widget_mut::<fold::MarkupContextMenu>(menu_el) {
                    w.items = items;
                    w.enabled = enabled;
                    w.palette = palette;
                    if let Some(f) = text_font {
                        w.font.clone_from(f);
                    }
                }
            }
            let visible = overrides
                .and_then(|v| v.visible_overrides.get(&index).copied())
                .unwrap_or_else(|| node.common.visible.read(values, true));
            ui.set_visible(el, visible);
            ui.set_enabled(el, node.common.enabled.read(values, true));
            if let Some(t) = &node.common.tooltip
                && let Some(e) = ui.element_mut(el)
            {
                e.runtime_tooltip_text = t.read(values);
            }
            let label = overrides.and_then(|v| v.label_overrides.get(&index).cloned());
            match &node.element {
                Markup::Group {
                    background, border, ..
                } => {
                    let mut bars = None;
                    if let Some(g) = ui.widget_mut::<layout::MarkupGroup>(el) {
                        let w = &mut g.panel;
                        w.background_color = background
                            .as_ref()
                            .map_or(Color::TRANSPARENT, |c| read_color(c, values, p));
                        w.border_color = border
                            .as_ref()
                            .map_or(Color::TRANSPARENT, |c| read_color(c, values, p));
                        w.border_thickness = if border.is_some() { 1.0 } else { 0.0 };
                        bars.clone_from(&g.scroll);
                    }
                    if let Some(area) = bars {
                        area.apply_theme(ui, p);
                    }
                }
                Markup::Meter {
                    fill,
                    cur,
                    max,
                    color: bar,
                    ..
                } => {
                    let caption = match (cur.read_u32(values), max.read_u32(values)) {
                        (Some(c), Some(m)) => Some(format!("{c}/{m}")),
                        _ => None,
                    };
                    let bar_color = read_color(bar, values, p);
                    if let Some(w) = ui.widget_mut::<Meter>(el) {
                        w.fill = fill.read_f32(values);
                        w.label = caption;
                        w.bar_color = bar_color;
                        w.theme_palette = palette;
                    }
                }
                Markup::Label { text, color: c } => {
                    let s = label.or_else(|| text.read(values)).unwrap_or_default();
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(Color::WHITE, |p| p.text),
                    };
                    if let Some(w) = ui.widget_mut::<MarkupLabel>(el) {
                        w.text = s;
                        w.color = col;
                        w.outline = p.is_none();
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Button {
                    text,
                    color: c,
                    background,
                    border,
                    icon: source,
                    ..
                } => {
                    let s = label.or_else(|| text.read(values)).unwrap_or_default();
                    let resolved = source.as_ref().and_then(|s| icon_of(icons, s, values));
                    let text_color = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(Color::WHITE, |p| p.text),
                    };
                    let bg = match background {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(Color::rgba(0.1, 0.1, 0.15, 0.8), |p| p.field),
                    };
                    let edge = match border {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(Color::rgba(0.45, 0.45, 0.55, 1.0), |p| p.border),
                    };
                    if let Some(w) = ui.widget_mut::<MarkupButton>(el) {
                        w.text = s;
                        w.text_color = text_color;
                        w.background = bg;
                        w.border = edge;
                        w.icon = resolved;
                        w.icon_file = file_tag(source.as_ref(), values, &drawn);
                        w.outline = p.is_none();
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Icon { icon: source } => {
                    let resolved = icon_of(icons, source, values);
                    if let Some(w) = ui.widget_mut::<MarkupIcon>(el) {
                        w.icon = resolved;
                        w.file = file_tag(Some(source), values, &drawn);
                    }
                }
                Markup::Tab {
                    text,
                    selected,
                    icon: source,
                    ..
                } => {
                    let s = label.unwrap_or_else(|| text.clone());
                    let on = bool_value(values, selected);
                    let resolved = source.as_ref().and_then(|s| icon_of(icons, s, values));
                    if let Some(w) = ui.widget_mut::<MarkupButton>(el) {
                        w.text = s;
                        w.icon = resolved;
                        w.icon_file = file_tag(source.as_ref(), values, &drawn);
                        w.tab_selected = Some(on);
                        w.outline = p.is_none();
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Toggle {
                    text,
                    checked,
                    color: c,
                    ..
                } => {
                    let s = label.or_else(|| text.read(values)).unwrap_or_default();
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(Color::rgba(0.86, 0.84, 0.74, 1.0), |p| p.text),
                    };
                    let on = bool_value(values, checked);
                    if let Some(w) = ui.widget_mut::<MarkupToggle>(el) {
                        w.text = s;
                        w.checked = on;
                        w.color = col;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Slider {
                    min,
                    max,
                    value,
                    retail_art,
                    ..
                } => {
                    let position = value
                        .read_f32(values)
                        .map(|v| ((v - min) / (max - min)).clamp(0.0, 1.0));
                    if let Some(w) = ui.widget_mut::<Scrollbar>(el) {
                        // OpenAC `PluginMarkupTheme`: themed, the plain look
                        // in the theme's colours; Classic, as authored.
                        let classic = Scrollbar::default();
                        w.retail_art = p.is_none() && *retail_art;
                        w.plain_track_color = p.map_or(classic.plain_track_color, |p| p.field);
                        w.plain_border_color = p.map_or(classic.plain_border_color, |p| p.border);
                        w.plain_nub_color = p.map_or(classic.plain_nub_color, |p| p.muted);
                        w.theme_palette = palette;
                        if !w.is_dragging()
                            && let Some(position) = position
                        {
                            w.set_scalar_position(position);
                        }
                    }
                }
                Markup::Field {
                    text,
                    background,
                    color: c,
                    default_background,
                    default_color,
                    ..
                } => {
                    let bound = text.read(values).unwrap_or_default();
                    let bg = match (p, default_background) {
                        (Some(p), true) => p.field,
                        _ => read_color(background, values, p),
                    };
                    let col = match (p, default_color) {
                        (Some(p), true) => p.text,
                        _ => read_color(c, values, p),
                    };
                    if let Some(w) = ui.widget_mut::<MarkupField>(el) {
                        w.inner.follow_bound_text(&bound);
                        w.inner.background_color = bg;
                        w.inner.text_color = col;
                        w.inner.theme_palette = palette;
                        w.inner.selection_color =
                            p.map_or(Field::default().selection_color, |p| p.selected);
                        if let Some(f) = text_font {
                            w.inner.font.clone_from(f);
                        }
                    }
                }
                Markup::Menu {
                    items,
                    selected,
                    retail_art,
                    ..
                } => {
                    let entries = texts(values, items);
                    let chosen = selected.read(values);
                    if let Some(w) = ui.widget_mut::<Menu>(el) {
                        w.set_items(
                            entries
                                .iter()
                                .enumerate()
                                .map(|(i, s)| MenuItem::new(s.clone(), i as u64))
                                .collect(),
                        );
                        w.selected = chosen
                            .as_ref()
                            .and_then(|c| entries.iter().position(|s| s == c))
                            .map(|i| i as u64);
                        w.button_label = chosen.unwrap_or_default();
                        // OpenAC `PluginMarkupTheme`: themed, the plain look
                        // in the theme's colours with a flat scrollbar;
                        // Classic, the authored art and the classic colours.
                        let classic = Menu::default();
                        w.retail_button_art = p.is_none() && *retail_art;
                        w.plain_popup_scrollbar = p.is_some();
                        w.plain_background_color =
                            p.map_or(classic.plain_background_color, |p| p.field);
                        w.plain_border_color = p.map_or(classic.plain_border_color, |p| p.border);
                        w.plain_open_border_color =
                            p.map_or(classic.plain_open_border_color, |p| p.accent);
                        w.plain_text_color = p.map_or(classic.plain_text_color, |p| p.text);
                        w.plain_triangle_color =
                            p.map_or(classic.plain_triangle_color, |p| p.muted);
                        w.plain_selected_color =
                            p.map_or(classic.plain_selected_color, |p| p.selected);
                        w.plain_hover_color =
                            p.map_or(classic.plain_hover_color, |p| style::hover(p, p.field));
                        w.theme_palette = palette;
                        if let Some((text, caption)) = &fonts {
                            w.font.clone_from(text);
                            w.button_font.clone_from(caption);
                        }
                    }
                    s.menu_items.insert(index, entries);
                }
                Markup::Log { items, first_index } => {
                    let entries = texts(values, items);
                    let first = first_index.as_ref().map_or(0, |f| int_value(values, f));
                    if let Some(w) = ui.widget_mut::<MarkupLog>(el) {
                        w.set_entries(&entries, first);
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.set_font(f.clone());
                        }
                    }
                }
                Markup::List {
                    selected,
                    body,
                    header,
                    tree,
                    multi,
                    ..
                } => {
                    let selected_row = if *multi {
                        -1
                    } else {
                        int_value(values, selected)
                    };
                    let (rows, column_rows, row_icons) = list_rows(values, body, icons, p);
                    let floats = |name: &str| match values.value(name) {
                        Some(UiValue::FloatList(v)) => Some(v.clone()),
                        _ => None,
                    };
                    let ints = |name: &str| match values.value(name) {
                        Some(UiValue::IntList(v)) => v.clone(),
                        _ => Vec::new(),
                    };
                    if let Some(w) = ui.widget_mut::<MarkupList>(el) {
                        if let (Some(ext), ListBody::Columns(columns)) = (w.ext.as_mut(), body) {
                            ext.bound_sort = header
                                .sort
                                .as_ref()
                                .map(|(c, d)| (int_value(values, c), bool_value(values, d)));
                            ext.bound_widths = header
                                .widths
                                .as_deref()
                                .map(|n| floats(n).unwrap_or_default());
                            for (i, column) in columns.iter().enumerate() {
                                ext.captions[i] = match &column.header {
                                    ac_plugin_ui::markup::ColumnHeader::Text(t) => t.read(values),
                                    _ => None,
                                };
                                ext.number_keys[i] = None;
                                ext.text_keys[i] = None;
                                match &column.sort_keys {
                                    Some(ac_plugin_ui::markup::SortKeysSource::Numbers(n)) => {
                                        ext.number_keys[i] = Some(floats(n).unwrap_or_default());
                                    }
                                    Some(ac_plugin_ui::markup::SortKeysSource::Texts(n)) => {
                                        ext.text_keys[i] = Some(texts(values, n));
                                    }
                                    None => {}
                                }
                            }
                            if *multi {
                                ext.selected_rows = ints(selected);
                            }
                            if let (Some(state), Some(spec)) = (ext.tree.as_mut(), tree) {
                                state.levels = ints(&spec.levels);
                                state.keys = texts(values, &spec.keys);
                                state.bound = spec
                                    .expanded
                                    .as_deref()
                                    .map(|n| texts(values, n).into_iter().collect());
                            }
                            w.all_rows = rows;
                            w.row_icons = row_icons;
                        } else {
                            w.rows = rows;
                            w.column_rows = column_rows;
                            w.row_icons = row_icons;
                        }
                        w.selected = selected_row;
                        w.row_files = list_row_files(values, body);
                        w.drawn_files = (!w.row_files.is_empty()).then(|| drawn.clone());
                        w.palette = palette;
                        w.header_font.clone_from(&look.heading);
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                        w.arrange();
                    }
                }
                Markup::Checkbox {
                    text,
                    checked,
                    color: c,
                    ..
                } => {
                    let s = label.or_else(|| text.read(values)).unwrap_or_default();
                    let state = match values.value(checked) {
                        Some(UiValue::Bool(b)) => Some(*b),
                        Some(UiValue::OptionalBool(b)) => *b,
                        _ => Some(false),
                    };
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(controls::CLASSIC_CAPTION, |p| p.text),
                    };
                    if let Some(w) = ui.widget_mut::<controls::MarkupCheckbox>(el) {
                        w.text = s;
                        w.state = state;
                        w.color = col;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Separator { color: c } => {
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(controls::UNLIT_INNER, |p| p.border),
                    };
                    if let Some(w) = ui.widget_mut::<controls::MarkupSeparator>(el) {
                        w.color = col;
                    }
                }
                Markup::Radio {
                    items,
                    selected,
                    horizontal,
                    segmented,
                    color: c,
                    ..
                } => {
                    let entries = items.read(values);
                    let chosen = int_value(values, selected);
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(controls::CLASSIC_CAPTION, |p| p.text),
                    };
                    if let Some(w) = ui.widget_mut::<controls::MarkupRadio>(el) {
                        w.items = entries;
                        w.selected = chosen;
                        w.horizontal = *horizontal;
                        w.segmented = *segmented;
                        w.color = col;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Number {
                    value,
                    background,
                    color: c,
                    default_background,
                    default_color,
                    ..
                } => {
                    let current = match values.value(value) {
                        Some(UiValue::Int(v)) => ac_plugin_ui::number::Dec::from_i64(i64::from(*v)),
                        Some(UiValue::Float(v)) => ac_plugin_ui::number::Dec::from_f32(*v),
                        _ => ac_plugin_ui::number::Dec::ZERO,
                    };
                    let bg = match (p, default_background) {
                        (Some(p), true) => p.field,
                        _ => read_color(background, values, p),
                    };
                    let col = match (p, default_color) {
                        (Some(p), true) => p.text,
                        _ => read_color(c, values, p),
                    };
                    if let Some(w) = ui.widget_mut::<controls::MarkupNumber>(el) {
                        w.set_current(current);
                        w.inner.background_color = bg;
                        w.inner.theme_palette = palette;
                        w.inner.selection_color =
                            p.map_or(Field::default().selection_color, |p| p.selected);
                        w.text_color = col;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.inner.font.clone_from(f);
                        }
                    }
                }
                Markup::Hotkey { value, .. } => {
                    let text = match values.value(value) {
                        Some(UiValue::Text(t)) => t.clone(),
                        _ => String::new(),
                    };
                    if let Some(w) = ui.widget_mut::<controls::MarkupHotkey>(el) {
                        w.value = text;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Color { value, kind, .. } => {
                    let argb = color::MarkupColor::read(*kind, values.value(value));
                    if let Some(w) = ui.widget_mut::<color::MarkupColor>(el) {
                        w.value = argb;
                        w.palette = palette;
                        if let Some(f) = text_font {
                            w.font.clone_from(f);
                        }
                    }
                }
                Markup::Expander {
                    text,
                    expanded,
                    color: c,
                    ..
                } => {
                    let s = label.or_else(|| text.read(values)).unwrap_or_default();
                    let col = match c {
                        Some(c) => read_color(c, values, p),
                        None => p.map_or(controls::CLASSIC_CAPTION, |p| p.text),
                    };
                    let bound = expanded.as_ref().map(|e| bool_value(values, e));
                    let header = ui.widget_mut::<fold::MarkupExpander>(el).map(|w| {
                        w.bound = bound;
                        w.palette = palette;
                        w.header
                    });
                    if let Some(h) = header.and_then(|h| ui.widget_mut::<fold::ExpanderHeader>(h)) {
                        h.text = s;
                        h.color = col;
                        h.palette = palette;
                        if let Some(f) = text_font {
                            h.font.clone_from(f);
                        }
                    }
                }
                // A marker shows nothing; its rows are synced as sets of
                // their own.
                Markup::Repeat { .. } => {}
                Markup::Split { position, .. } => {
                    let bound = position.as_ref().map(|name| match values.value(name) {
                        Some(UiValue::Float(v)) => *v,
                        _ => 0.0,
                    });
                    let bar = ui.widget_mut::<fold::MarkupSplit>(el).map(|w| {
                        w.bound = bound;
                        w.bar
                    });
                    if let Some(b) = bar.and_then(|b| ui.widget_mut::<fold::SplitBar>(b)) {
                        b.palette = palette;
                    }
                }
            }
        }
    }
}

type Rows = (Vec<Vec<Cell>>, Vec<usize>, Option<Vec<Option<Icon>>>);

/// A list's rows from the panel's values (OpenAC `UiMarkupList`'s sources).
fn list_rows(
    values: &dyn Values,
    body: &ListBody,
    icons: &mut dyn MarkupIcons,
    palette: Option<&Palette>,
) -> Rows {
    let default = palette.map_or(Color::rgba(0.91, 0.87, 0.76, 1.0), |p| p.text);
    let row_color =
        |colors: &[u32], i: usize| colors.get(i).map_or(default, |c| color(Rgba::rgb(*c)));
    match body {
        ListBody::Single {
            items,
            colors,
            icons: row_icons,
        } => {
            let entries = texts(values, items);
            let colors = colors
                .as_deref()
                .map(|c| uints(values, c))
                .unwrap_or_default();
            let rows: Vec<Vec<Cell>> = entries
                .iter()
                .enumerate()
                .map(|(i, s)| vec![Cell::Text(s.clone(), row_color(&colors, i))])
                .collect();
            let resolved = row_icons
                .as_ref()
                .map(|(name, kind)| self::row_icons(icons, values, name, *kind));
            let count = rows.len();
            (rows, vec![count], resolved)
        }
        ListBody::Columns(columns) => {
            let mut cells: Vec<Vec<Cell>> = Vec::with_capacity(columns.len());
            let mut counts = Vec::with_capacity(columns.len());
            for column in columns {
                let column_cells: Vec<Cell> = match &column.kind {
                    ColumnKind::Text { items, colors, .. } => {
                        let colors = colors
                            .as_deref()
                            .map(|c| uints(values, c))
                            .unwrap_or_default();
                        texts(values, items)
                            .into_iter()
                            .enumerate()
                            .map(|(i, s)| Cell::Text(s, row_color(&colors, i)))
                            .collect()
                    }
                    ColumnKind::Check { values: name, .. } => {
                        bools(values, name).into_iter().map(Cell::Check).collect()
                    }
                    ColumnKind::Icon {
                        values: name, kind, ..
                    } => row_icons(icons, values, name, *kind)
                        .into_iter()
                        .map(Cell::Icon)
                        .collect(),
                    ColumnKind::Button { items, .. } => {
                        texts(values, items).into_iter().map(Cell::Button).collect()
                    }
                };
                counts.push(column_cells.len());
                cells.push(column_cells);
            }
            let count = counts.iter().copied().max().unwrap_or(0);
            let rows = (0..count)
                .map(|row| {
                    columns
                        .iter()
                        .zip(&cells)
                        .map(|(column, column_cells)| match column_cells.get(row) {
                            Some(cell) => cell.clone(),
                            // A short check column still draws its empty box.
                            None if matches!(column.kind, ColumnKind::Check { .. }) => {
                                Cell::Check(false)
                            }
                            None => Cell::Empty,
                        })
                        .collect()
                })
                .collect();
            (rows, counts, None)
        }
    }
}

/// The file icon paths each of a list's rows draws (none for a list
/// without file icons), by the plugin's row.
fn list_row_files(values: &dyn Values, body: &ListBody) -> Vec<Vec<String>> {
    let sources: Vec<&str> = match body {
        ListBody::Single {
            icons: Some((name, IconKind::File)),
            ..
        } => vec![name.as_str()],
        ListBody::Single { .. } => Vec::new(),
        ListBody::Columns(columns) => columns
            .iter()
            .filter_map(|c| match &c.kind {
                ColumnKind::Icon {
                    values,
                    kind: IconKind::File,
                    ..
                } => Some(values.as_str()),
                _ => None,
            })
            .collect(),
    };
    let mut rows: Vec<Vec<String>> = Vec::new();
    for name in sources {
        for (row, path) in texts(values, name).into_iter().enumerate() {
            if rows.len() <= row {
                rows.resize_with(row + 1, Vec::new);
            }
            if !path.trim().is_empty() {
                rows[row].push(path);
            }
        }
    }
    rows
}

impl PanelController for PluginPanelController {
    fn tick(&mut self, ui: &mut UiRoot, _dt: f64) {
        self.track_content_minimum(ui);
        // A flex menu has no authored w (the container sizes it): its popup
        // column follows the laid-out menu.
        for &menu in self.sets.values().flat_map(|s| &s.flex_menus) {
            let width = ui.element(menu).map_or(0.0, |e| e.width);
            if let Some(m) = ui.widget_mut::<Menu>(menu) {
                m.column_width = width.max(20.0);
            }
        }
    }

    fn on_shown(&mut self, _ui: &mut UiRoot) {
        if !self.expected_visible {
            self.expected_visible = true;
            self.push(UiInput::PanelShown {
                panel: self.panel,
                shown: true,
            });
        }
    }

    fn on_hidden(&mut self, _ui: &mut UiRoot) {
        if self.expected_visible {
            self.expected_visible = false;
            self.push(UiInput::PanelShown {
                panel: self.panel,
                shown: false,
            });
        }
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.minimize {
            if matches!(message, ElementMessage::Clicked { .. }) {
                ui.hide_window(&self.window_name);
            }
            return true;
        }
        if let Some(bar) = self.title_bar
            && ui
                .widget::<title_bar::TitleBar>(bar)
                .is_some_and(|b| b.close == element)
        {
            if matches!(message, ElementMessage::Clicked { .. }) {
                ui.close_window(&self.window_name);
            }
            return true;
        }
        let Some(&(set, index)) = self.by_element.get(&element) else {
            return false;
        };
        let doc = Arc::clone(&self.document);
        match (&doc.nodes[index].element, message) {
            (
                Markup::Button { onclick, .. }
                | Markup::Tab { onclick, .. }
                | Markup::Toggle { onclick, .. },
                ElementMessage::Clicked { .. },
            ) => self.action(set, onclick, ActionValue::None),
            (
                Markup::Slider {
                    onchange, min, max, ..
                },
                ElementMessage::ScalarChanged(position),
            ) => self.action(
                set,
                onchange,
                ActionValue::Float(min + position * (max - min)),
            ),
            (Markup::Field { onchange, .. }, ElementMessage::TextChanged(text)) => {
                self.action(set, onchange, ActionValue::Text(text.clone()))
            }
            (Markup::Field { onsubmit, .. }, ElementMessage::Submitted(text)) => {
                self.action(set, onsubmit, ActionValue::Text(text.clone()))
            }
            (Markup::Field { onblur, .. }, ElementMessage::FocusLost(text)) => {
                self.action(set, onblur, ActionValue::Text(text.clone()))
            }
            (Markup::Menu { onchange, .. }, ElementMessage::MenuSelected(payload)) => {
                let chosen = self
                    .sets
                    .get(&set)
                    .and_then(|s| s.menu_items.get(&index))
                    .and_then(|items| items.get(usize::try_from(*payload).ok()?))
                    .cloned();
                match chosen {
                    Some(text) => self.action(set, onchange, ActionValue::Text(text)),
                    None => true,
                }
            }
            (Markup::List { onchange, body, .. }, ElementMessage::Clicked { x, y }) => {
                let (w, h) = ui
                    .element(element)
                    .map_or((0.0, 0.0), |e| (e.width, e.height));
                let Some(list) = ui.widget::<MarkupList>(element) else {
                    return true;
                };
                let row = |i: usize| {
                    ActionValue::Int(i32::try_from(list.plugin_row(i)).unwrap_or(i32::MAX))
                };
                let column_handler = |c: usize, pick: fn(&ColumnKind) -> Option<&String>| match body
                {
                    ListBody::Columns(columns) => {
                        columns.get(c).and_then(|col| pick(&col.kind)).cloned()
                    }
                    ListBody::Single { .. } => None,
                };
                match list.press(w, h, *x as f32, *y as f32) {
                    ColumnPress::Select(i) => self.action(set, onchange, row(i)),
                    ColumnPress::TextClicked(c, i) => {
                        let handler = column_handler(c, |k| match k {
                            ColumnKind::Text { onclick, .. } => onclick.as_ref(),
                            _ => None,
                        });
                        self.action(set, &handler, row(i))
                    }
                    ColumnPress::CheckChanged(c, i) => {
                        let handler = column_handler(c, |k| match k {
                            ColumnKind::Check { onchange, .. } => Some(onchange),
                            _ => None,
                        });
                        self.action(set, &handler, row(i))
                    }
                    ColumnPress::IconClicked(c, i) => {
                        let handler = column_handler(c, |k| match k {
                            ColumnKind::Icon { onclick, .. } => Some(onclick),
                            _ => None,
                        });
                        self.action(set, &handler, row(i))
                    }
                    ColumnPress::Nothing => true,
                }
            }
            (
                Markup::List {
                    body: ListBody::Columns(columns),
                    ..
                },
                ElementMessage::ListButtonClicked { column, row },
            ) => {
                let handler = match columns.get(*column).map(|c| &c.kind) {
                    Some(ColumnKind::Button { onclick, .. }) => Some(onclick.clone()),
                    _ => None,
                };
                let plugin = ui
                    .widget::<MarkupList>(element)
                    .map_or(*row, |l| l.plugin_row(*row));
                self.action(
                    set,
                    &handler,
                    ActionValue::Int(i32::try_from(plugin).unwrap_or(i32::MAX)),
                )
            }
            _ => false,
        }
    }
}

/// A markup field: the retail text field, with the up and down arrows
/// going to the plugin's `onup`/`ondown` handlers when it has them
/// (OpenAC `UiField.OnUp`/`OnDown`).
#[derive(Debug)]
pub struct MarkupField {
    /// The text field.
    pub inner: Field,
    emit: controls::Emit,
    onup: Option<String>,
    ondown: Option<String>,
}

impl crate::tree::Widget for MarkupField {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &crate::event::UiEvent) -> bool {
        if e.target == Some(me) && e.kind == crate::event::kind::KEY_DOWN {
            let handler = match e.key {
                crate::event::Key::Up => self.onup.clone(),
                crate::event::Key::Down => self.ondown.clone(),
                _ => None,
            };
            if let Some(handler) = handler {
                self.emit.act(Some(&handler), ActionValue::None);
                return true;
            }
        }
        self.inner.on_event(ui, me, e)
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut crate::draw::RenderContext<'_>) {
        let faded =
            self.inner.theme_palette.is_some() && !ui.element(me).is_some_and(|e| e.enabled);
        if faded {
            rc.push_alpha(style::DISABLED_ALPHA);
        }
        self.inner.draw(ui, me, rc);
        if faded {
            rc.pop_alpha();
        }
    }

    fn draw_after_children(
        &mut self,
        ui: &mut UiRoot,
        me: ElementId,
        rc: &mut crate::draw::RenderContext<'_>,
    ) {
        self.inner.draw_after_children(ui, me, rc);
    }

    fn tick(&mut self, ui: &mut UiRoot, me: ElementId, dt: f64) {
        self.inner.tick(ui, me, dt);
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
}

#[cfg(test)]
mod file_icon_tests;
#[cfg(test)]
mod flex_tests;
#[cfg(test)]
mod list_button_tests;
#[cfg(test)]
mod repeat_tests;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod theme_control_tests;
#[cfg(test)]
mod theme_tests;
