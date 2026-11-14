//! [`PluginUi`]: every plugin's panels, canvases, images, labels and lines
//! in one session, owned by the session (never by a window). Every plugin
//! call that touches its UI lands here and is answered the same with or
//! without a window; an attached window gets a snapshot
//! ([`PluginUi::snapshot`]) and then the changes ([`PluginUi::take_changes`]),
//! and sends back what the player did ([`PluginUi::input`]).

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::path::PathBuf;
use std::sync::Arc;

use crate::images::{ImageBudget, ImageTable};
use crate::markup::{self, normalize_icon};
use crate::model::{
    CanvasDescriptor, CanvasView, ClientWindow, ClientWindowAction, GroundShape, GroundShapeKind,
    ImageSource, MarkerAnchor, PaintCmd, PanelDescriptor, PanelView, PluginIcon, PointerKind,
    ProjectileSample, UiChange, UiEvent, UiInput, UiOwner, WorldIcon, WorldIconEntry, WorldLabel,
    WorldLine, WorldTriangle, canvas_origin,
};
use crate::value::{BindingSchema, UiValue};

mod dialogs;
mod file_icons;
mod fonts;
mod window_icons;
pub use dialogs::{DEFAULT_DIALOG_BUTTONS, MAXIMUM_DIALOG_BUTTONS};
pub use file_icons::{MAXIMUM_STEP_MS, RELEASE_AFTER_MS, RELEASE_CHECK_INTERVAL_MS};

/// The most labels one plugin may have showing at once (a larger set is
/// refused whole).
pub const MAXIMUM_LABELS: usize = 256;
/// The most characters (UTF-16 units, as .NET counts) a label may have.
pub const MAXIMUM_LABEL_TEXT: usize = 128;
/// The most canvases one plugin may hold.
pub const MAXIMUM_CANVASES: usize = 8;
/// The most lines one layer keeps (more are cut off).
pub const MAXIMUM_LINES_PER_LAYER: usize = 16_384;
/// The most triangles one plugin may have set at once, across its layers:
/// every one is placed in the world each frame before any is culled.
pub const MAXIMUM_TRIANGLES: usize = 65_536;
/// The most icons one plugin may have set at once, across its layers.
pub const MAXIMUM_ICONS: usize = 256;
/// The smallest an icon is drawn, pixels.
pub const MINIMUM_ICON_SIZE: f32 = 8.0;
/// The largest an icon is drawn, pixels.
pub const MAXIMUM_ICON_SIZE: f32 = 128.0;
/// The most ground shapes one plugin may have set at once, across its
/// layers; counted apart from its icons.
pub const MAXIMUM_SHAPES: usize = 256;
/// The smallest radius a ground shape may have, metres.
pub const MINIMUM_SHAPE_RADIUS: f32 = 0.1;
/// The largest radius a ground shape may have, metres.
pub const MAXIMUM_SHAPE_RADIUS: f32 = 100.0;
/// The most projectile markers kept.
pub const MAXIMUM_PROJECTILE_SAMPLES: usize = 4096;
/// The most commands one paint may hold (a host bound: a paint list is
/// copied to the window).
pub const MAXIMUM_PAINT_COMMANDS: usize = 65_536;

/// What the plugin UI reads from the client's content, with or without a
/// window: the interface font's measures and the sizes of client art.
pub trait UiResources: fmt::Debug + Send + Sync {
    /// The width of `text` in the interface font and the font's line
    /// height, pixels.
    fn measure_text(&self, text: &str) -> (f32, f32);
    /// The size of a client render surface, if the client has it.
    fn surface_size(&self, surface_id: u32) -> Option<(u32, u32)>;
    /// The width and height a plugin's own encoded image declares, read
    /// from its header alone; none for a header the client does not read.
    fn image_dimensions(&self, _bytes: &[u8]) -> Option<(u32, u32)> {
        None
    }
    /// A plugin's own encoded image decoded to 8-bit RGBA, or why not. A
    /// host that decodes nothing says so.
    fn decode_image(&self, _bytes: &[u8]) -> Result<crate::images::DecodedImage, String> {
        Err("this host decodes no images".to_owned())
    }
}

/// Glyph advances and a line height: the interface font's measures.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TextMetrics {
    advances: BTreeMap<char, f32>,
    line_height: f32,
}

impl TextMetrics {
    /// Measures from each character's advance (before + width + after) and
    /// the font's line height.
    #[must_use]
    pub fn new(advances: impl IntoIterator<Item = (char, f32)>, line_height: f32) -> Self {
        Self {
            advances: advances.into_iter().collect(),
            line_height,
        }
    }

    /// The text's width (characters the font lacks add nothing) and the
    /// line height; nothing for empty text (OpenAC `PluginPainter.MeasureText`).
    #[must_use]
    pub fn measure(&self, text: &str) -> (f32, f32) {
        if text.is_empty() {
            return (0.0, 0.0);
        }
        let width = text
            .chars()
            .filter_map(|c| self.advances.get(&c))
            .sum::<f32>();
        (width, self.line_height)
    }
}

#[derive(Debug, Clone)]
struct Panel {
    view: PanelView,
    schema: BindingSchema,
    /// Its plugin's install folder, where its file icons are read.
    directory: Option<PathBuf>,
}

#[derive(Debug, Clone)]
struct LineLayer {
    id: u32,
    owner: String,
    lines: Vec<WorldLine>,
}

#[derive(Debug, Clone)]
struct TriangleLayer {
    id: u32,
    owner: String,
    triangles: Vec<WorldTriangle>,
}

#[derive(Debug, Clone)]
struct MarkerLayer {
    id: u32,
    owner: String,
    icons: Vec<WorldIcon>,
    shapes: Vec<GroundShape>,
}

/// The icon as the window draws it, its size in range, or `None` when it is
/// dropped.
fn normalized_icon(icon: WorldIcon) -> Option<WorldIcon> {
    let usable = icon.anchor.is_valid()
        && icon.image != 0
        && icon.size.is_finite()
        && icon.max_range.is_finite()
        && icon.max_range > 0.0;
    usable.then(|| WorldIcon {
        size: icon.size.clamp(MINIMUM_ICON_SIZE, MAXIMUM_ICON_SIZE),
        ..icon
    })
}

/// The shape as the renderer draws it, or `None` when it is dropped. A ring
/// or disc becomes a full turn from north, whatever its start, sweep and
/// facing; a disc or filled arc has no width.
fn normalized_shape(shape: GroundShape) -> Option<GroundShape> {
    if !shape.anchor.is_valid()
        || !shape.radius.is_finite()
        || !(MINIMUM_SHAPE_RADIUS..=MAXIMUM_SHAPE_RADIUS).contains(&shape.radius)
    {
        return None;
    }
    // A band runs inward from the radius: it must have some width and
    // cannot run past the centre.
    let band = shape.width.is_finite() && shape.width > 0.0 && shape.width <= shape.radius;
    let full_turn = GroundShape {
        start_degrees: 0.0,
        sweep_degrees: 360.0,
        faces_object: false,
        ..shape
    };
    match shape.kind {
        GroundShapeKind::None => None,
        GroundShapeKind::Ring => band.then_some(GroundShape {
            filled: false,
            ..full_turn
        }),
        GroundShapeKind::Disc => Some(GroundShape {
            width: 0.0,
            filled: true,
            ..full_turn
        }),
        GroundShapeKind::Arc => {
            let sweep = shape.start_degrees.is_finite()
                && shape.sweep_degrees.is_finite()
                && shape.sweep_degrees > 0.0
                && shape.sweep_degrees <= 360.0;
            if !sweep {
                None
            } else if shape.filled {
                Some(GroundShape {
                    width: 0.0,
                    ..shape
                })
            } else {
                band.then_some(shape)
            }
        }
    }
}

/// The session's plugin UI. See the module documentation.
#[derive(Debug, Default)]
pub struct PluginUi {
    next_id: u32,
    panels: Vec<Panel>,
    canvases: Vec<CanvasView>,
    images: BTreeMap<String, ImageTable>,
    /// Each running plugin's `icon.png`, decoded when it started; its
    /// windows carry it from registration.
    file_icons: BTreeMap<String, PluginIcon>,
    /// The host's reader of the image files plugin markup names.
    file_images: Option<Arc<dyn crate::file_images::FileImageLoader>>,
    /// Each running plugin's install folder, where the file icons of its
    /// windows are read.
    plugin_folders: BTreeMap<String, PathBuf>,
    /// The dock icon files each plugin's windows named, by plugin and path.
    window_icons: BTreeMap<String, BTreeMap<String, PluginIcon>>,
    /// What each plugin window holds of the file icons it draws.
    panel_files: BTreeMap<u32, file_icons::PanelFiles>,
    /// The last image handle each plugin was given, kept after it goes so
    /// a reloaded plugin never gets a handle a window still knows.
    last_image_handles: BTreeMap<String, u32>,
    /// Each plugin's fonts.
    fonts: BTreeMap<String, crate::fonts::FontTable>,
    /// The last font handle each plugin was given, kept after it goes.
    last_font_handles: BTreeMap<String, u32>,
    /// The bundled font's bakes, shared by every plugin.
    bundled_fonts: crate::fonts::BundledBakes,
    /// The framebuffer pixels a unit of the window's display: what the fonts'
    /// sharper bakes follow.
    font_density: f32,
    /// The file icon refusals already logged.
    file_reported: BTreeSet<String>,
    /// The session clock's last reading, milliseconds.
    file_clock: u64,
    labels: Vec<(String, Vec<WorldLabel>)>,
    layers: Vec<LineLayer>,
    triangle_layers: Vec<TriangleLayer>,
    marker_layers: Vec<MarkerLayer>,
    window_attached: bool,
    client_mounted: u16,
    client_visible: u16,
    resources: Option<Arc<dyn UiResources>>,
    budget: ImageBudget,
    changes: Vec<UiChange>,
    events: Vec<(String, UiEvent)>,
    /// The canvases the pointer hovers, each owed a leave: its plugin and
    /// the latest hover.
    hovered: BTreeMap<u32, (String, crate::model::PointerEvent)>,
    reports: Vec<(String, String)>,
    hotkeys: crate::hotkeys::Hotkeys,
    /// The bound table as the window was last told it.
    sent_hotkeys: Vec<crate::hotkeys::BoundHotkey>,
    /// The plugins hearing the client's input actions, in the order they
    /// asked.
    action_listeners: Vec<String>,
    /// The plugin theme the window last told; it outlives the window, so a
    /// reattach keeps answering the same one (Classic until a window says).
    theme: crate::theme::ThemeKind,
    /// The theme changed since the session last took the change.
    theme_changed: bool,
    /// The dialogs waiting for an answer, in the order asked.
    dialogs: Vec<crate::model::DialogView>,
    /// The windows shown modal and the windows they block.
    modals: Vec<(u32, u32)>,
}

/// Every item record holds only names its item type declares, each of its
/// kind (a nested list checked against its own item type).
fn check_items(item: &BindingSchema, rows: &[crate::value::ItemRecord]) -> Result<(), String> {
    for row in rows {
        for (name, value) in &row.values {
            match item.kind(name) {
                None => {
                    return Err(format!(
                        "'{name}' is not a binding of {} (declare it with the item type)",
                        item.type_name
                    ));
                }
                Some(kind) if kind.is_action() || kind != value.kind() => {
                    return Err(format!(
                        "'{name}' of {} is declared as {kind:?}, but a {:?} was set",
                        item.type_name,
                        value.kind()
                    ));
                }
                Some(_) => {}
            }
            if let (UiValue::Items(nested), Some(inner)) = (value, item.item_schema(name)) {
                check_items(inner, nested)?;
            }
        }
    }
    Ok(())
}

fn same_owner(a: &str, b: &str) -> bool {
    a == b
}

fn blank(text: &str) -> bool {
    text.trim().is_empty()
}

impl PluginUi {
    /// Nothing registered, no window, no content.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The content the UI measures text and sizes client art with.
    pub fn set_resources(&mut self, resources: Option<Arc<dyn UiResources>>) {
        self.resources = resources;
    }

    /// The image budget (OpenAC's defaults unless a test changes it).
    pub fn set_image_budget(&mut self, budget: ImageBudget) {
        self.budget = budget;
    }

    fn next_id(&mut self) -> u32 {
        self.next_id = self.next_id.wrapping_add(1).max(1);
        self.next_id
    }

    // ── Panels ───────────────────────────────────────────────────────────

    /// The `icon.png` of plugin `owner` (none: it has none, or it did not
    /// decode), which the windows it registers from now on carry for
    /// their dock slots. The host sets it as the plugin starts;
    /// [`Self::release`] forgets it.
    pub fn set_file_icon(&mut self, owner: &str, icon: Option<PluginIcon>) {
        match icon {
            Some(icon) => {
                self.file_icons.insert(owner.to_owned(), icon);
            }
            None => {
                self.file_icons.remove(owner);
            }
        }
    }

    /// Registers a window from `markup` whose bindings are `schema`
    /// (OpenAC `RegisterPanelContent`). Returns its number.
    ///
    /// # Errors
    /// A blank id, title or markup, an id the plugin already uses, or
    /// markup that does not parse or resolve (the markup's message).
    pub fn register_panel(
        &mut self,
        owner: &UiOwner,
        descriptor: PanelDescriptor,
        markup: &str,
        schema: BindingSchema,
    ) -> Result<u32, String> {
        if blank(&descriptor.window_id) {
            return Err("A plugin window needs a window id.".to_owned());
        }
        if blank(&descriptor.title) {
            return Err("A plugin window needs a title.".to_owned());
        }
        if blank(markup) {
            return Err("A plugin window needs markup.".to_owned());
        }
        if self.panels.iter().any(|p| {
            same_owner(&p.view.owner.id, &owner.id)
                && p.view.descriptor.window_id == descriptor.window_id
        }) {
            return Err(format!(
                "Plugin window '{}' is already registered. Window ids must be unique within one plugin.",
                descriptor.window_id
            ));
        }
        let document = markup::parse(markup, &schema).map_err(|e| e.0)?;
        let id = self.next_id();
        let window_icon = descriptor
            .icon_file
            .as_deref()
            .and_then(|path| self.read_window_icon(&owner.id, path));
        let view = PanelView {
            id,
            owner: owner.clone(),
            requested_visible: descriptor.start_visible,
            descriptor,
            document: Arc::new(document),
            values: BTreeMap::new(),
            label_overrides: BTreeMap::new(),
            visible_overrides: BTreeMap::new(),
            file_icon: self.file_icons.get(&owner.id).cloned(),
            window_icon,
        };
        self.changes
            .push(UiChange::PanelAdded(Box::new(view.clone())));
        let directory = self.plugin_folders.get(&owner.id).cloned();
        self.panels.push(Panel {
            view,
            schema,
            directory,
        });
        Ok(id)
    }

    /// Removes one of `owner`'s windows; false when it has none by that
    /// number.
    pub fn remove_panel(&mut self, owner: &str, panel: u32) -> bool {
        let before = self.panels.len();
        self.panels
            .retain(|p| !(p.view.id == panel && same_owner(&p.view.owner.id, owner)));
        let removed = self.panels.len() != before;
        if removed {
            self.changes.push(UiChange::PanelRemoved(panel));
            self.drop_panel_files(panel);
        }
        self.follow_dialogs();
        removed
    }

    fn panel_mut(&mut self, owner: &str, panel: u32) -> Option<&mut Panel> {
        self.panels
            .iter_mut()
            .find(|p| p.view.id == panel && same_owner(&p.view.owner.id, owner))
    }

    /// Pushes bound values: each name must be a declared value binding of
    /// the value's kind. All or nothing.
    ///
    /// # Errors
    /// No such panel, or a name that is undeclared, a handler, or of
    /// another kind.
    pub fn set_values(
        &mut self,
        owner: &str,
        panel: u32,
        values: Vec<(String, UiValue)>,
    ) -> Result<(), String> {
        let Some(p) = self.panel_mut(owner, panel) else {
            return Err(format!("the plugin has no window number {panel}"));
        };
        for (name, value) in &values {
            match p.schema.kind(name) {
                None => {
                    return Err(format!(
                        "'{name}' is not a binding of {} (declare it when registering the window)",
                        p.schema.type_name
                    ));
                }
                Some(kind) if kind.is_action() => {
                    return Err(format!("'{name}' is a handler, not a value"));
                }
                Some(kind) if kind != value.kind() => {
                    return Err(format!(
                        "'{name}' is declared as {kind:?}, but a {:?} was set",
                        value.kind()
                    ));
                }
                Some(_) => {}
            }
            if let (UiValue::Items(rows), Some(item)) = (value, p.schema.item_schema(name)) {
                check_items(item, rows)?;
            }
        }
        let mut changed = Vec::new();
        for (name, value) in values {
            if p.view.values.get(&name) != Some(&value) {
                p.view.values.insert(name.clone(), value.clone());
                changed.push((name, value));
            }
        }
        if !changed.is_empty() {
            self.changes.push(UiChange::PanelValues {
                panel,
                values: changed,
            });
        }
        self.follow_dialogs();
        Ok(())
    }

    fn find_view(&self, owner: &str, view: &str) -> Option<usize> {
        self.panels.iter().position(|p| {
            same_owner(&p.view.owner.id, owner)
                && (p.view.descriptor.window_id == view || p.view.descriptor.title == view)
        })
    }

    /// Whether `owner` registered a window with this id or title.
    #[must_use]
    pub fn view_exists(&self, owner: &str, view: &str) -> bool {
        self.find_view(owner, view).is_some()
    }

    /// Whether `owner`'s window is shown (requested and allowed by its
    /// bound visibility).
    #[must_use]
    pub fn is_view_visible(&self, owner: &str, view: &str) -> bool {
        self.find_view(owner, view)
            .is_some_and(|i| self.panels[i].view.shown())
    }

    /// Shows or hides `owner`'s window as its dock slot and close button
    /// do (OpenAC `ShowPanel`/`HidePanel`); false when it has none by that
    /// id or title.
    pub fn show_panel(&mut self, owner: &str, view: &str, show: bool) -> bool {
        let Some(i) = self.find_view(owner, view) else {
            return false;
        };
        self.request_visible(i, show);
        true
    }

    fn request_visible(&mut self, index: usize, show: bool) {
        let view = &mut self.panels[index].view;
        let before = view.requested_visible;
        if show {
            view.on_shown();
        } else {
            view.on_hidden();
        }
        if view.requested_visible != before {
            self.changes.push(UiChange::PanelRequestedVisible {
                panel: view.id,
                visible: view.requested_visible,
            });
        }
        self.follow_dialogs();
    }

    fn find_control(&self, owner: &str, view: &str, control: &str) -> Option<(usize, usize)> {
        let i = self.find_view(owner, view)?;
        let node = self.panels[i].view.document.find(control)?;
        Some((i, node))
    }

    /// Whether `owner`'s window holds a control by that name.
    #[must_use]
    pub fn control_exists(&self, owner: &str, view: &str, control: &str) -> bool {
        self.find_control(owner, view, control).is_some()
    }

    /// Replaces a button's, tab's, toggle's or label's text, dropping its
    /// binding; false when not found or it carries no text.
    pub fn set_control_label(
        &mut self,
        owner: &str,
        view: &str,
        control: &str,
        label: &str,
    ) -> bool {
        let Some((i, node)) = self.find_control(owner, view, control) else {
            return false;
        };
        let p = &mut self.panels[i].view;
        if !p.document.nodes[node].element.carries_text() {
            return false;
        }
        p.label_overrides.insert(node, label.to_owned());
        let panel = p.id;
        self.changes.push(UiChange::ControlLabel {
            panel,
            node,
            label: label.to_owned(),
        });
        true
    }

    /// Shows or hides a control, dropping its bound visibility; false when
    /// not found.
    pub fn set_control_visible(
        &mut self,
        owner: &str,
        view: &str,
        control: &str,
        visible: bool,
    ) -> bool {
        let Some((i, node)) = self.find_control(owner, view, control) else {
            return false;
        };
        let p = &mut self.panels[i].view;
        p.visible_overrides.insert(node, visible);
        let panel = p.id;
        self.changes.push(UiChange::ControlVisible {
            panel,
            node,
            visible,
        });
        true
    }

    // ── Client windows ───────────────────────────────────────────────────

    /// A window attached (`true`) or left: the client windows are known
    /// only while one is, once it said which it has.
    pub fn set_window_attached(&mut self, attached: bool) {
        if self.window_attached != attached {
            self.window_attached = attached;
            self.client_mounted = 0;
            self.client_visible = 0;
            if !attached {
                // Nothing draws the file icons any more.
                self.drop_all_panel_files();
                self.hotkeys.set_client_chords(None);
                self.follow_hotkeys();
                self.detach_dialogs();
                // No pointer is over anything any more.
                let hovered: Vec<u32> = self.hovered.keys().copied().collect();
                for canvas in hovered {
                    self.end_hover(canvas);
                }
                // Nothing hosts a canvas any more.
                for c in &mut self.canvases {
                    c.hosted = false;
                }
            }
        }
    }

    /// Whether a window is attached.
    #[must_use]
    pub fn window_attached(&self) -> bool {
        self.window_attached
    }

    /// Shows, hides or toggles one of the client's windows: the attached
    /// window does it; the answer is whether it ends up visible (toggle) or
    /// the window has it (show, hide). False without a window or for a
    /// window it does not mount.
    pub fn client_window(&mut self, window: ClientWindow, action: ClientWindowAction) -> bool {
        if !self.window_attached || self.client_mounted & window.bit() == 0 {
            return false;
        }
        let shown = match action {
            ClientWindowAction::Toggle => self.client_visible & window.bit() == 0,
            ClientWindowAction::Show => true,
            ClientWindowAction::Hide => false,
        };
        if shown {
            self.client_visible |= window.bit();
        } else {
            self.client_visible &= !window.bit();
        }
        self.changes.push(UiChange::ClientWindow { window, action });
        match action {
            ClientWindowAction::Toggle => shown,
            ClientWindowAction::Show | ClientWindowAction::Hide => true,
        }
    }

    /// Whether one of the client's windows is visible (false without a
    /// window).
    #[must_use]
    pub fn is_client_window_visible(&self, window: ClientWindow) -> bool {
        self.window_attached
            && self.client_mounted & window.bit() != 0
            && self.client_visible & window.bit() != 0
    }

    // ── Canvases ─────────────────────────────────────────────────────────

    /// Registers a canvas (OpenAC `RegisterCanvas`).
    ///
    /// # Errors
    /// A blank id, a size below 1, an id the plugin already has, or the
    /// plugin already holds [`MAXIMUM_CANVASES`].
    pub fn register_canvas(
        &mut self,
        owner: &UiOwner,
        descriptor: CanvasDescriptor,
    ) -> Result<u32, String> {
        if blank(&descriptor.canvas_id) {
            return Err("A plugin canvas needs a canvas id.".to_owned());
        }
        if descriptor.width <= 0 || descriptor.height <= 0 {
            return Err(format!(
                "Plugin canvas '{}' must be at least 1 pixel wide and high (asked for {}x{}).",
                descriptor.canvas_id, descriptor.width, descriptor.height
            ));
        }
        let mut held = 0;
        for existing in self.canvases.iter().filter(|c| c.owner.id == owner.id) {
            held += 1;
            if existing.descriptor.canvas_id == descriptor.canvas_id {
                return Err(format!(
                    "Plugin canvas '{}' is already registered. Canvas ids must be unique within one plugin.",
                    descriptor.canvas_id
                ));
            }
        }
        if held >= MAXIMUM_CANVASES {
            return Err(format!(
                "Plugin canvas '{}' refused: the plugin already holds {MAXIMUM_CANVASES} canvases, which is the most it may.",
                descriptor.canvas_id
            ));
        }
        let id = self.next_id();
        let view = CanvasView {
            id,
            owner: owner.clone(),
            size: (descriptor.width, descriptor.height),
            hosted: false,
            visible: descriptor.start_visible,
            anchor: descriptor.anchor,
            offset: descriptor.offset,
            descriptor,
            listening: false,
            paint: Arc::new(Vec::new()),
            release_count: 0,
        };
        self.changes
            .push(UiChange::CanvasAdded(Box::new(view.clone())));
        self.canvases.push(view);
        Ok(id)
    }

    fn canvas_mut(&mut self, owner: &str, canvas: u32) -> Option<&mut CanvasView> {
        self.canvases
            .iter_mut()
            .find(|c| c.id == canvas && c.owner.id == owner)
    }

    /// One of `owner`'s canvases as it stands.
    #[must_use]
    pub fn canvas(&self, owner: &str, canvas: u32) -> Option<&CanvasView> {
        self.canvases
            .iter()
            .find(|c| c.id == canvas && c.owner.id == owner)
    }

    /// Whether a window draws the canvas now (OpenAC `IsAvailable`): a
    /// window is attached, and for an in-markup canvas an element of one of
    /// its plugin's windows hosts it.
    #[must_use]
    pub fn canvas_available(&self, owner: &str, canvas: u32) -> bool {
        self.window_attached
            && self
                .canvas(owner, canvas)
                .is_some_and(|c| !c.descriptor.in_markup || c.hosted)
    }

    /// Resizes a canvas on the screen (OpenAC `TryResize`): it keeps its
    /// anchor, offset, visibility, listening and paint, so one anchored at
    /// a right or bottom edge keeps that edge; the plugin paints again for
    /// the new size. The same size again changes nothing. False for a size
    /// below 1, no such canvas, or an in-markup canvas (its element sizes
    /// it).
    pub fn resize_canvas(&mut self, owner: &str, canvas: u32, width: i32, height: i32) -> bool {
        if width < 1 || height < 1 {
            return false;
        }
        let Some(c) = self.canvas_mut(owner, canvas) else {
            return false;
        };
        if c.descriptor.in_markup {
            return false;
        }
        let size = (width, height);
        if c.size != size {
            c.size = size;
            self.changes.push(UiChange::CanvasResized { canvas, size });
        }
        true
    }

    /// The window says whether an element hosts an in-markup canvas, and at
    /// what size: a new size is the canvas's, and its plugin hears of it.
    fn canvas_hosted(&mut self, canvas: u32, size: Option<(i32, i32)>) {
        if !self.window_attached {
            return;
        }
        let Some(c) = self
            .canvases
            .iter_mut()
            .find(|c| c.id == canvas && c.descriptor.in_markup)
        else {
            return;
        };
        c.hosted = size.is_some();
        let Some((width, height)) = size.filter(|s| s.0 >= 1 && s.1 >= 1 && *s != c.size) else {
            return;
        };
        c.size = (width, height);
        self.events.push((
            c.owner.id.clone(),
            UiEvent::CanvasResized {
                canvas,
                width,
                height,
            },
        ));
    }

    /// Replaces what a canvas shows.
    ///
    /// # Errors
    /// No such canvas, too many commands, or clips that do not balance
    /// (OpenAC drops a paint that leaves a clip pushed).
    pub fn paint_canvas(
        &mut self,
        owner: &str,
        canvas: u32,
        commands: Vec<PaintCmd>,
    ) -> Result<(), String> {
        if commands.len() > MAXIMUM_PAINT_COMMANDS {
            return Err(format!(
                "a canvas paint may hold at most {MAXIMUM_PAINT_COMMANDS} commands (this one has {})",
                commands.len()
            ));
        }
        let mut depth = 0usize;
        for command in &commands {
            match command {
                PaintCmd::PushClip(_) => depth += 1,
                PaintCmd::PopClip => {
                    if depth == 0 {
                        return Err("the paint pops a clip it did not push".to_owned());
                    }
                    depth -= 1;
                }
                _ => {}
            }
        }
        if depth != 0 {
            return Err(format!("the paint leaves {depth} clip(s) pushed"));
        }
        let Some(c) = self.canvas_mut(owner, canvas) else {
            return Err(format!("the plugin has no canvas number {canvas}"));
        };
        let paint = Arc::new(commands);
        c.paint = Arc::clone(&paint);
        self.changes.push(UiChange::CanvasPaint { canvas, paint });
        Ok(())
    }

    /// Shows, hides or moves a canvas; false when there is no such canvas.
    pub fn place_canvas(
        &mut self,
        owner: &str,
        canvas: u32,
        visible: Option<bool>,
        anchor: Option<crate::model::CanvasAnchor>,
        offset: Option<(f32, f32)>,
    ) -> bool {
        let Some(c) = self.canvas_mut(owner, canvas) else {
            return false;
        };
        let before = (c.visible, c.anchor, c.offset);
        if let Some(v) = visible {
            c.visible = v;
        }
        if let Some(a) = anchor {
            c.anchor = a;
        }
        if let Some(o) = offset {
            c.offset = o;
        }
        let after = (c.visible, c.anchor, c.offset);
        if after != before {
            self.changes.push(UiChange::CanvasPlacement {
                canvas,
                visible: after.0,
                anchor: after.1,
                offset: after.2,
            });
        }
        true
    }

    /// The plugin starts or stops listening for a canvas's pointer; false
    /// when there is no such canvas.
    pub fn listen_canvas(&mut self, owner: &str, canvas: u32, listening: bool) -> bool {
        let Some(c) = self.canvas_mut(owner, canvas) else {
            return false;
        };
        if c.listening != listening {
            c.listening = listening;
            self.changes
                .push(UiChange::CanvasListening { canvas, listening });
        }
        true
    }

    /// The plugin lets go of the press its canvas holds; false when there
    /// is no such canvas.
    pub fn release_canvas_pointer(&mut self, owner: &str, canvas: u32) -> bool {
        let Some(c) = self.canvas_mut(owner, canvas) else {
            return false;
        };
        c.release_count = c.release_count.wrapping_add(1);
        self.changes.push(UiChange::CanvasReleasePointer(canvas));
        true
    }

    /// Removes a canvas; false when there is no such canvas.
    pub fn remove_canvas(&mut self, owner: &str, canvas: u32) -> bool {
        let before = self.canvases.len();
        self.canvases
            .retain(|c| !(c.id == canvas && c.owner.id == owner));
        let removed = self.canvases.len() != before;
        if removed {
            self.changes.push(UiChange::CanvasRemoved(canvas));
            self.end_hover(canvas);
        }
        removed
    }

    /// The leave a hovered canvas is owed, now: at the last place it was
    /// hovered, with no modifiers.
    fn end_hover(&mut self, canvas: u32) {
        if let Some((owner, mut event)) = self.hovered.remove(&canvas) {
            event.kind = PointerKind::Leave;
            event.modifiers = 0;
            self.events.push((
                owner,
                UiEvent::Pointer {
                    canvas,
                    event,
                    place: None,
                },
            ));
        }
    }

    /// The width of `text` in the interface font and its line height
    /// (nothing without content, or for empty text).
    #[must_use]
    pub fn measure_text(&self, text: &str) -> (f32, f32) {
        self.resources
            .as_ref()
            .map_or((0.0, 0.0), |r| r.measure_text(text))
    }

    // ── Images ───────────────────────────────────────────────────────────

    fn table(&mut self, owner: &str) -> &mut ImageTable {
        let budget = self.budget;
        let last = self.last_image_handles.get(owner).copied().unwrap_or(0);
        self.images
            .entry(owner.to_owned())
            .or_insert_with(|| ImageTable::after(budget, last))
    }

    fn image_result(
        &mut self,
        owner: &str,
        result: Result<crate::images::Acquired, String>,
    ) -> (u32, u32, u32) {
        match result {
            Ok(acquired) => {
                if let Some(view) = acquired.added {
                    self.changes.push(UiChange::ImageAdded(Box::new(view)));
                }
                (acquired.handle, acquired.width, acquired.height)
            }
            Err(report) => {
                if !report.is_empty() {
                    self.reports.push((owner.to_owned(), report));
                }
                (0, 0, 0)
            }
        }
    }

    /// The client's art for a render surface (an id or a bare index):
    /// `(handle, width, height)`, handle 0 when refused.
    pub fn image_client_art(&mut self, owner: &str, surface: u32) -> (u32, u32, u32) {
        let surface = normalize_icon(surface);
        let size = self
            .resources
            .as_ref()
            .and_then(|r| (surface != 0).then(|| r.surface_size(surface)).flatten());
        let result = self.table(owner).acquire_shared(
            owner,
            ImageSource::ClientArt(surface),
            size,
            &format!("client art 0x{surface:08X}"),
        );
        self.image_result(owner, result)
    }

    /// A spell's icon (`icon`: its render surface, `None` when the client
    /// has no such spell).
    pub fn image_spell_icon(
        &mut self,
        owner: &str,
        spell: u32,
        icon: Option<u32>,
    ) -> (u32, u32, u32) {
        let size = icon.filter(|_| spell != 0).map(|icon| {
            self.resources
                .as_ref()
                .and_then(|r| r.surface_size(icon))
                .unwrap_or((32, 32))
        });
        let result = self.table(owner).acquire_shared(
            owner,
            ImageSource::SpellIcon {
                spell,
                icon: icon.unwrap_or(0),
            },
            size,
            &format!("spell icon {spell}"),
        );
        self.image_result(owner, result)
    }

    /// A live object's composed icon (`known`: the client knows the object
    /// and it has an icon).
    pub fn image_object_icon(&mut self, owner: &str, object: u32, known: bool) -> (u32, u32, u32) {
        let size = (known && object != 0).then_some((32, 32));
        let result = self.table(owner).acquire_shared(
            owner,
            ImageSource::ObjectIcon(object),
            size,
            &format!("object icon 0x{object:08X}"),
        );
        self.image_result(owner, result)
    }

    /// The plugin's own pixels under its name for them (8-bit RGBA rows).
    pub fn image_rgba(
        &mut self,
        owner: &str,
        name: &str,
        width: u32,
        height: u32,
        rgba: Vec<u8>,
    ) -> (u32, u32, u32) {
        let result = self
            .table(owner)
            .acquire_rgba(owner, name, width, height, rgba);
        self.image_result(owner, result)
    }

    /// The plugin's own encoded art (PNG, JPEG, BMP, TGA, GIF or WebP) under its
    /// name for it, decoded by the host's resources (OpenAC
    /// `IPluginImages.FromStream`); refused where the host lends none.
    pub fn image_decode(&mut self, owner: &str, name: &str, bytes: &[u8]) -> (u32, u32, u32) {
        let resources = self.resources.clone();
        let result = self.table(owner).acquire_decoded(
            owner,
            name,
            bytes,
            |b| resources.as_ref().and_then(|r| r.image_dimensions(b)),
            |b| match resources.as_ref() {
                Some(r) => r.decode_image(b),
                None => Err("this host decodes no images".to_owned()),
            },
        );
        self.image_result(owner, result)
    }

    /// Lets go of one hold; false for a handle the plugin does not hold.
    pub fn image_release(&mut self, owner: &str, handle: u32) -> bool {
        let Some(table) = self.images.get_mut(owner) else {
            return false;
        };
        match table.release(handle) {
            None => false,
            Some(removed) => {
                if removed {
                    self.changes.push(UiChange::ImageRemoved {
                        owner: owner.to_owned(),
                        handle,
                    });
                }
                true
            }
        }
    }

    /// How many distinct images `owner` holds.
    #[must_use]
    pub fn image_count(&self, owner: &str) -> usize {
        self.images.get(owner).map_or(0, ImageTable::count)
    }

    // ── Labels, lines, projectile markers ───────────────────────────────

    /// Replaces `owner`'s world labels (OpenAC `ShowLabels`): false, and
    /// nothing changed, for more than [`MAXIMUM_LABELS`]; unusable labels
    /// are dropped quietly.
    pub fn show_labels(&mut self, owner: &str, labels: Vec<WorldLabel>) -> bool {
        if labels.len() > MAXIMUM_LABELS {
            return false;
        }
        let kept: Vec<WorldLabel> = labels
            .into_iter()
            .filter(|l| {
                l.object_id != 0
                    && !l.text.is_empty()
                    && l.text.encode_utf16().count() <= MAXIMUM_LABEL_TEXT
                    && l.color.iter().all(|c| c.is_finite())
                    && l.max_range.is_finite()
                    && l.max_range > 0.0
                    && l.height_offset.is_finite()
            })
            .collect();
        let at = self.labels.iter().position(|(o, _)| o == owner);
        match (at, kept.is_empty()) {
            (Some(i), true) => {
                self.labels.remove(i);
            }
            (Some(i), false) => self.labels[i].1 = kept,
            (None, true) => {}
            (None, false) => self.labels.push((owner.to_owned(), kept)),
        }
        self.changes.push(UiChange::Labels(self.merged_labels()));
        true
    }

    /// Takes every plugin's labels down: the character left the world, and
    /// with it the objects the labels sat on. The plugins stay and show
    /// labels again on the next stay.
    pub fn clear_labels(&mut self) {
        if self.labels.is_empty() {
            return;
        }
        self.labels.clear();
        self.changes.push(UiChange::Labels(self.merged_labels()));
    }

    /// Every plugin's labels, in the order the plugins first showed some.
    #[must_use]
    pub fn merged_labels(&self) -> Arc<Vec<WorldLabel>> {
        Arc::new(
            self.labels
                .iter()
                .flat_map(|(_, set)| set.iter().cloned())
                .collect(),
        )
    }

    /// A new, empty line layer for `owner`.
    pub fn create_line_layer(&mut self, owner: &str) -> u32 {
        let id = self.next_id();
        self.layers.push(LineLayer {
            id,
            owner: owner.to_owned(),
            lines: Vec::new(),
        });
        id
    }

    /// Replaces a layer's lines (the first [`MAXIMUM_LINES_PER_LAYER`]);
    /// false when `owner` has no such layer.
    pub fn set_lines(&mut self, owner: &str, layer: u32, mut lines: Vec<WorldLine>) -> bool {
        let Some(l) = self
            .layers
            .iter_mut()
            .find(|l| l.id == layer && l.owner == owner)
        else {
            return false;
        };
        lines.truncate(MAXIMUM_LINES_PER_LAYER);
        l.lines = lines;
        self.changes.push(UiChange::Lines(self.merged_lines()));
        true
    }

    /// Removes a layer and its lines; false when `owner` has no such layer.
    pub fn remove_line_layer(&mut self, owner: &str, layer: u32) -> bool {
        let before = self.layers.len();
        self.layers.retain(|l| !(l.id == layer && l.owner == owner));
        let removed = self.layers.len() != before;
        if removed {
            self.changes.push(UiChange::Lines(self.merged_lines()));
        }
        removed
    }

    /// Every layer's lines, in layer order.
    #[must_use]
    pub fn merged_lines(&self) -> Arc<Vec<WorldLine>> {
        Arc::new(
            self.layers
                .iter()
                .flat_map(|l| l.lines.iter().copied())
                .collect(),
        )
    }

    /// A new, empty triangle layer for `owner`.
    pub fn create_triangle_layer(&mut self, owner: &str) -> u32 {
        let id = self.next_id();
        self.triangle_layers.push(TriangleLayer {
            id,
            owner: owner.to_owned(),
            triangles: Vec::new(),
        });
        id
    }

    /// Replaces a layer's triangles. False, and nothing changed, when
    /// `owner` has no such layer, or when the triangles given and those on
    /// the plugin's other layers come to more than [`MAXIMUM_TRIANGLES`].
    pub fn set_triangles(
        &mut self,
        owner: &str,
        layer: u32,
        triangles: Vec<WorldTriangle>,
    ) -> bool {
        if !self
            .triangle_layers
            .iter()
            .any(|l| l.id == layer && l.owner == owner)
        {
            return false;
        }
        let others: usize = self
            .triangle_layers
            .iter()
            .filter(|l| l.owner == owner && l.id != layer)
            .map(|l| l.triangles.len())
            .sum();
        if triangles.len() + others > MAXIMUM_TRIANGLES {
            return false;
        }
        let Some(l) = self.triangle_layers.iter_mut().find(|l| l.id == layer) else {
            return false;
        };
        l.triangles = triangles;
        self.changes
            .push(UiChange::Triangles(self.merged_triangles()));
        true
    }

    /// Removes a layer and its triangles; false when `owner` has no such
    /// layer.
    pub fn remove_triangle_layer(&mut self, owner: &str, layer: u32) -> bool {
        let before = self.triangle_layers.len();
        self.triangle_layers
            .retain(|l| !(l.id == layer && l.owner == owner));
        let removed = self.triangle_layers.len() != before;
        if removed {
            self.changes
                .push(UiChange::Triangles(self.merged_triangles()));
        }
        removed
    }

    /// Every layer's triangles, in layer order.
    #[must_use]
    pub fn merged_triangles(&self) -> Arc<Vec<WorldTriangle>> {
        Arc::new(
            self.triangle_layers
                .iter()
                .flat_map(|l| l.triangles.iter().copied())
                .collect(),
        )
    }

    /// A new, empty marker layer for `owner`.
    pub fn create_marker_layer(&mut self, owner: &str) -> u32 {
        let id = self.next_id();
        self.marker_layers.push(MarkerLayer {
            id,
            owner: owner.to_owned(),
            icons: Vec::new(),
            shapes: Vec::new(),
        });
        id
    }

    /// Replaces a layer's icons; unusable ones are dropped quietly. False,
    /// and nothing changed, when `owner` has no such layer, or when the
    /// icons given and those on the plugin's other layers come to more than
    /// [`MAXIMUM_ICONS`].
    pub fn set_icons(&mut self, owner: &str, layer: u32, icons: Vec<WorldIcon>) -> bool {
        if !self
            .marker_layers
            .iter()
            .any(|l| l.id == layer && l.owner == owner)
        {
            return false;
        }
        let others: usize = self
            .marker_layers
            .iter()
            .filter(|l| l.owner == owner && l.id != layer)
            .map(|l| l.icons.len())
            .sum();
        if icons.len() + others > MAXIMUM_ICONS {
            return false;
        }
        let kept = icons.into_iter().filter_map(normalized_icon).collect();
        if let Some(l) = self.marker_layers.iter_mut().find(|l| l.id == layer) {
            l.icons = kept;
        }
        self.changes.push(UiChange::Icons(self.merged_icons()));
        true
    }

    /// Replaces a layer's ground shapes; unusable ones are dropped quietly.
    /// False, and nothing changed, when `owner` has no such layer, or when
    /// the shapes given and those on the plugin's other layers come to more
    /// than [`MAXIMUM_SHAPES`].
    pub fn set_shapes(&mut self, owner: &str, layer: u32, shapes: Vec<GroundShape>) -> bool {
        if !self
            .marker_layers
            .iter()
            .any(|l| l.id == layer && l.owner == owner)
        {
            return false;
        }
        let others: usize = self
            .marker_layers
            .iter()
            .filter(|l| l.owner == owner && l.id != layer)
            .map(|l| l.shapes.len())
            .sum();
        if shapes.len() + others > MAXIMUM_SHAPES {
            return false;
        }
        let kept = shapes.into_iter().filter_map(normalized_shape).collect();
        if let Some(l) = self.marker_layers.iter_mut().find(|l| l.id == layer) {
            l.shapes = kept;
        }
        self.changes.push(UiChange::Shapes(self.merged_shapes()));
        true
    }

    /// Removes a layer and its markers; false when `owner` has no such
    /// layer.
    pub fn remove_marker_layer(&mut self, owner: &str, layer: u32) -> bool {
        let before = self.marker_layers.len();
        self.marker_layers
            .retain(|l| !(l.id == layer && l.owner == owner));
        let removed = self.marker_layers.len() != before;
        if removed {
            self.push_markers();
        }
        removed
    }

    /// Tells the window every kind of marker changed.
    fn push_markers(&mut self) {
        self.changes.push(UiChange::Icons(self.merged_icons()));
        self.changes.push(UiChange::Shapes(self.merged_shapes()));
    }

    /// Takes every plugin's markers over objects down: the character left
    /// the world, and with it the objects they sat on, whose ids mean
    /// nothing on the next stay. Markers at fixed positions stay, as lines
    /// and triangles do: a position is the same place in the world after a
    /// relog. The layers stay, so a plugin sets its markers again on the
    /// next stay without a new layer.
    pub fn clear_object_markers(&mut self) {
        let on_object = |anchor: &MarkerAnchor| matches!(anchor, MarkerAnchor::Object(_));
        if !self.marker_layers.iter().any(|l| {
            l.icons.iter().any(|i| on_object(&i.anchor))
                || l.shapes.iter().any(|s| on_object(&s.anchor))
        }) {
            return;
        }
        for layer in &mut self.marker_layers {
            layer.icons.retain(|i| !on_object(&i.anchor));
            layer.shapes.retain(|s| !on_object(&s.anchor));
        }
        self.push_markers();
    }

    /// Every icon of every plugin, by plugin id, then its layers in the
    /// order they were made, then the order given.
    #[must_use]
    pub fn merged_icons(&self) -> Arc<Vec<WorldIconEntry>> {
        let mut layers: Vec<&MarkerLayer> = self.marker_layers.iter().collect();
        layers.sort_by(|a, b| a.owner.cmp(&b.owner));
        Arc::new(
            layers
                .into_iter()
                .flat_map(|l| {
                    l.icons.iter().map(|icon| WorldIconEntry {
                        owner: l.owner.clone(),
                        icon: *icon,
                    })
                })
                .collect(),
        )
    }

    /// Every ground shape of every plugin, in the same order as
    /// [`Self::merged_icons`].
    #[must_use]
    pub fn merged_shapes(&self) -> Arc<Vec<GroundShape>> {
        let mut layers: Vec<&MarkerLayer> = self.marker_layers.iter().collect();
        layers.sort_by(|a, b| a.owner.cmp(&b.owner));
        Arc::new(
            layers
                .into_iter()
                .flat_map(|l| l.shapes.iter().copied())
                .collect(),
        )
    }

    /// Projectile markers to draw for a moment; unusable ones (a position
    /// that is not finite, a radius of zero or less) are dropped, and at
    /// most [`MAXIMUM_PROJECTILE_SAMPLES`] are kept.
    pub fn show_projectile_samples(&mut self, samples: Vec<ProjectileSample>) {
        let kept: Vec<ProjectileSample> = samples
            .into_iter()
            .take(MAXIMUM_PROJECTILE_SAMPLES)
            .filter(|s| {
                s.position.east_west.is_finite()
                    && s.position.north_south.is_finite()
                    && s.position.elevation.is_finite()
                    && s.radius.is_finite()
                    && s.radius > 0.0
            })
            .collect();
        self.changes.push(UiChange::ProjectileDebug(Arc::new(kept)));
    }

    // ── Hotkeys ──────────────────────────────────────────────────────────

    /// The plugins' hotkeys.
    #[must_use]
    pub fn hotkeys(&self) -> &crate::hotkeys::Hotkeys {
        &self.hotkeys
    }

    /// The player's stored hotkey overrides, by scoped id (read at the
    /// session's start).
    pub fn set_hotkey_overrides(
        &mut self,
        overrides: std::collections::BTreeMap<String, crate::hotkeys::Chord>,
    ) {
        self.hotkeys.set_overrides(overrides);
        self.follow_hotkeys();
    }

    /// Whether a rebind changed the stored overrides since the last call.
    pub fn take_hotkey_overrides_changed(&mut self) -> bool {
        self.hotkeys.take_overrides_changed()
    }

    /// Registers one of `owner`'s hotkeys ([`crate::hotkeys::Hotkeys::register`]).
    ///
    /// # Errors
    /// A blank id or display name.
    pub fn register_hotkey(
        &mut self,
        owner: &str,
        id: &str,
        display_name: &str,
        chord: crate::hotkeys::Chord,
    ) -> Result<crate::hotkeys::HotkeyState, String> {
        let state = self.hotkeys.register(owner, id, display_name, chord);
        self.follow_hotkeys();
        state
    }

    /// Revokes one of `owner`'s hotkeys.
    pub fn unregister_hotkey(&mut self, owner: &str, id: &str) -> bool {
        let removed = self.hotkeys.unregister(owner, id);
        self.follow_hotkeys();
        removed
    }

    /// Rebinds one of `owner`'s hotkeys.
    pub fn rebind_hotkey(
        &mut self,
        owner: &str,
        id: &str,
        chord: crate::hotkeys::Chord,
    ) -> Option<crate::hotkeys::HotkeyState> {
        let state = self.hotkeys.rebind(owner, id, chord);
        self.follow_hotkeys();
        state
    }

    /// The window learns of every change to the bound table.
    /// `owner` hears (or stops hearing) the client's input actions; the
    /// setting before.
    pub fn listen_input_actions(&mut self, owner: &str, listen: bool) -> bool {
        let index = self
            .action_listeners
            .iter()
            .position(|o| same_owner(o, owner));
        match (index, listen) {
            (None, true) => self.action_listeners.push(owner.to_owned()),
            (Some(i), false) => {
                self.action_listeners.remove(i);
            }
            _ => {}
        }
        index.is_some()
    }

    /// Whether `owner` hears the client's input actions.
    #[must_use]
    pub fn hears_input_actions(&self, owner: &str) -> bool {
        self.action_listeners.iter().any(|o| same_owner(o, owner))
    }

    fn follow_hotkeys(&mut self) {
        let bound = self.hotkeys.bound();
        if bound != self.sent_hotkeys {
            self.sent_hotkeys.clone_from(&bound);
            self.changes.push(UiChange::Hotkeys(Arc::new(bound)));
        }
    }

    // ── Lifetime ─────────────────────────────────────────────────────────

    /// Everything `owner` registered goes (it was unloaded).
    pub fn release(&mut self, owner: &str) {
        let panels: Vec<u32> = self
            .panels
            .iter()
            .filter(|p| p.view.owner.id == owner)
            .map(|p| p.view.id)
            .collect();
        for panel in panels {
            self.remove_panel(owner, panel);
        }
        let canvases: Vec<u32> = self
            .canvases
            .iter()
            .filter(|c| c.owner.id == owner)
            .map(|c| c.id)
            .collect();
        // A plugin that is gone hears no leave.
        self.hovered.retain(|_, (o, _)| o != owner);
        for canvas in canvases {
            self.remove_canvas(owner, canvas);
        }
        if let Some(table) = self.images.remove(owner) {
            // Its handles are never given out again, even after a reload.
            self.last_image_handles
                .insert(owner.to_owned(), table.last_handle());
            for handle in table.handles() {
                self.changes.push(UiChange::ImageRemoved {
                    owner: owner.to_owned(),
                    handle,
                });
            }
        }
        if let Some(i) = self.labels.iter().position(|(o, _)| o == owner) {
            self.labels.remove(i);
            self.changes.push(UiChange::Labels(self.merged_labels()));
        }
        let before = self.layers.len();
        self.layers.retain(|l| l.owner != owner);
        if self.layers.len() != before {
            self.changes.push(UiChange::Lines(self.merged_lines()));
        }
        let before = self.triangle_layers.len();
        self.triangle_layers.retain(|l| l.owner != owner);
        if self.triangle_layers.len() != before {
            self.changes
                .push(UiChange::Triangles(self.merged_triangles()));
        }
        let before = self.marker_layers.len();
        self.marker_layers.retain(|l| l.owner != owner);
        if self.marker_layers.len() != before {
            self.push_markers();
        }
        self.hotkeys.release(owner);
        self.follow_hotkeys();
        let dialogs: Vec<u32> = self
            .dialogs
            .iter()
            .filter(|d| same_owner(&d.owner.id, owner))
            .map(|d| d.handle)
            .collect();
        for handle in dialogs {
            self.dialogs.retain(|d| d.handle != handle);
            self.changes.push(UiChange::DialogRemoved(handle));
        }
        self.action_listeners.retain(|o| !same_owner(o, owner));
        self.events.retain(|(o, _)| o != owner);
        self.release_fonts(owner);
        self.file_icons.remove(owner);
        self.window_icons.remove(owner);
        self.plugin_folders.remove(owner);
    }

    // ── The window ───────────────────────────────────────────────────────

    /// Everything as it stands, for a window that attaches: [`UiChange::Clear`]
    /// then every panel, canvas and image, the labels, the lines, the
    /// triangles, the icons and the ground shapes.
    #[must_use]
    pub fn snapshot(&self) -> Vec<UiChange> {
        let mut out = vec![UiChange::Clear];
        out.extend(
            self.panels
                .iter()
                .map(|p| UiChange::PanelAdded(Box::new(p.view.clone()))),
        );
        out.extend(
            self.canvases
                .iter()
                .map(|c| UiChange::CanvasAdded(Box::new(c.clone()))),
        );
        for table in self.images.values() {
            out.extend(
                table
                    .views()
                    .map(|v| UiChange::ImageAdded(Box::new(v.clone()))),
            );
        }
        out.extend(self.font_snapshot());
        out.extend(self.file_icon_snapshot());
        out.push(UiChange::Labels(self.merged_labels()));
        out.push(UiChange::Lines(self.merged_lines()));
        out.push(UiChange::Triangles(self.merged_triangles()));
        out.push(UiChange::Icons(self.merged_icons()));
        out.push(UiChange::Shapes(self.merged_shapes()));
        out.push(UiChange::Hotkeys(Arc::new(self.hotkeys.bound())));
        out.extend(
            self.dialogs
                .iter()
                .map(|d| UiChange::DialogAdded(Box::new(d.clone()))),
        );
        out.extend(self.modals.iter().map(|(panel, owner)| UiChange::Modal {
            panel: *panel,
            owner: Some(*owner),
        }));
        out
    }

    /// The changes since the last call, oldest first.
    pub fn take_changes(&mut self) -> Vec<UiChange> {
        std::mem::take(&mut self.changes)
    }

    /// What the window reports: a handler fired, the player opened or
    /// closed a window, pointer input, the client windows. Input for
    /// something that is gone, or a handler the panel does not declare with
    /// that argument, is dropped.
    pub fn input(&mut self, input: UiInput) {
        match input {
            UiInput::Action {
                panel,
                handler,
                value,
            } => {
                let Some(p) = self.panels.iter().find(|p| p.view.id == panel) else {
                    return;
                };
                if p.schema.handler_kind(&handler) != Some(value.kind()) {
                    return;
                }
                let owner = p.view.owner.id.clone();
                self.events.push((
                    owner,
                    UiEvent::Action {
                        panel,
                        handler,
                        value,
                    },
                ));
            }
            UiInput::PanelShown { panel, shown } => {
                if let Some(i) = self.panels.iter().position(|p| p.view.id == panel) {
                    self.request_visible(i, shown);
                }
            }
            UiInput::Pointer {
                canvas,
                event,
                place,
            } => {
                if event.kind == PointerKind::Leave {
                    // Owed whether or not the canvas still takes the
                    // pointer; once only.
                    if let Some((owner, _)) = self.hovered.remove(&canvas) {
                        self.events.push((
                            owner,
                            UiEvent::Pointer {
                                canvas,
                                event,
                                place,
                            },
                        ));
                    }
                    return;
                }
                let Some(c) = self.canvases.iter().find(|c| c.id == canvas) else {
                    return;
                };
                if !c.takes_input() {
                    return;
                }
                let owner = c.owner.id.clone();
                if event.kind == PointerKind::Hover {
                    self.hovered.insert(canvas, (owner.clone(), event));
                    // A newer hover replaces one not yet delivered, so a
                    // fast pointer does not flood the plugin; anything
                    // since for this canvas keeps the older one.
                    let last = self.events.iter().rposition(
                        |(_, e)| matches!(e, UiEvent::Pointer { canvas: c, .. } if *c == canvas),
                    );
                    if let Some(at) = last
                        && let UiEvent::Pointer { event: older, .. } = &self.events[at].1
                        && older.kind == PointerKind::Hover
                    {
                        self.events.remove(at);
                    }
                }
                self.events.push((
                    owner,
                    UiEvent::Pointer {
                        canvas,
                        event,
                        place,
                    },
                ));
            }
            UiInput::ClientWindows { mounted, visible } => {
                if self.window_attached {
                    self.client_mounted = mounted;
                    self.client_visible = visible & mounted;
                }
            }
            UiInput::ClientChords(chords) => {
                if self.window_attached {
                    self.hotkeys.set_client_chords(Some(chords));
                    self.follow_hotkeys();
                }
            }
            UiInput::Hotkey { owner, id } => {
                if self.hotkeys.is_bound(&owner, &id) {
                    self.events.push((owner, UiEvent::Hotkey(id)));
                }
            }
            UiInput::InputAction { action, activation } => {
                if self.window_attached {
                    for owner in &self.action_listeners {
                        self.events.push((
                            owner.clone(),
                            UiEvent::InputAction {
                                action: action.clone(),
                                activation: activation.clone(),
                            },
                        ));
                    }
                }
            }
            UiInput::Theme(theme) => {
                if self.theme != theme {
                    self.theme = theme;
                    self.theme_changed = true;
                }
            }
            UiInput::DialogAnswered { handle, button } => self.answer_dialog(handle, button),
            UiInput::FileIconsShown { panel, paths } => self.file_icons_shown(panel, paths),
            UiInput::DisplayDensity(density) => self.set_font_density(density),
            UiInput::CanvasHosted { canvas, size } => self.canvas_hosted(canvas, size),
        }
    }

    /// The plugin theme plugins read (OpenAC `IUiRegistry.Theme`): the one
    /// the window last told, Classic before any window did.
    #[must_use]
    pub fn theme(&self) -> crate::theme::ThemeKind {
        self.theme
    }

    /// The new theme, once, when it changed since the last call (OpenAC
    /// `BufferedUiRegistry.PublishTheme` tells plugins only of a change).
    pub fn take_theme_change(&mut self) -> Option<crate::theme::ThemeKind> {
        std::mem::take(&mut self.theme_changed).then_some(self.theme)
    }

    /// A held pointer's move or release, or a hover or its leave, as the
    /// plugin should hear it, just before it does: measured from where the
    /// plugin last put the canvas, not from where the window still had it
    /// (a hover and its leave share one reference). A plugin that
    /// moves its canvas by how far the pointer is from a grabbed point would
    /// otherwise count each step again until the window caught up, and
    /// overshoot. A press stays as the player saw it, and so does an event
    /// whose place is not known.
    #[must_use]
    pub fn pointer_as_placed_now(&self, event: UiEvent) -> UiEvent {
        let UiEvent::Pointer {
            canvas,
            event: mut pointer,
            place,
        } = event
        else {
            return event;
        };
        if let Some(place) = place
            && matches!(
                pointer.kind,
                PointerKind::Move | PointerKind::Up | PointerKind::Hover | PointerKind::Leave
            )
            && let Some(c) = self.canvases.iter().find(|c| c.id == canvas)
        {
            let size = (c.size.0 as f32, c.size.1 as f32);
            let now = canvas_origin(c.anchor, c.offset, size, place.layer);
            pointer.x += place.origin.0 - now.0;
            pointer.y += place.origin.1 - now.1;
        }
        UiEvent::Pointer {
            canvas,
            event: pointer,
            place,
        }
    }

    /// What the plugins hear from their UI, oldest first, with the plugin
    /// each is for.
    pub fn take_events(&mut self) -> Vec<(String, UiEvent)> {
        std::mem::take(&mut self.events)
    }

    /// Requests refused for a reason the plugin's author should see (an
    /// image over budget, art the client does not have), with the plugin.
    pub fn take_reports(&mut self) -> Vec<(String, String)> {
        std::mem::take(&mut self.reports)
    }

    /// Every panel, in registration order.
    pub fn panels(&self) -> impl Iterator<Item = &PanelView> {
        self.panels.iter().map(|p| &p.view)
    }

    /// Every canvas, in registration order.
    pub fn canvases(&self) -> impl Iterator<Item = &CanvasView> {
        self.canvases.iter()
    }

    /// The handler argument a panel's `handler` takes, if it declares one.
    #[must_use]
    pub fn handler_kind(&self, panel: u32, handler: &str) -> Option<crate::value::BindingKind> {
        self.panels
            .iter()
            .find(|p| p.view.id == panel)
            .and_then(|p| p.schema.handler_kind(handler))
            .filter(|k| k.is_action())
    }
}

#[cfg(test)]
mod tests;
