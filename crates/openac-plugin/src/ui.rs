//! Plugin UI: windows from OpenAC's panel markup, canvases, images, world
//! labels and lines, and the client's own windows.
//!
//! A window's markup names `{Binding}`s; the plugin declares each one's
//! kind when it registers the window ([`PanelSpec::bind`]), pushes values
//! for them ([`Panel::set`] then [`Panel::flush`], which sends only what
//! changed), and hears its handlers as [`crate::Event::Ui`] events
//! ([`UiEvent::Action`], naming the handler binding). The client keeps the
//! window with the session: it answers the same with or without a window,
//! and a window that attaches later shows it as it stands.
//!
//! ```
//! use openac_plugin::ui::{BindingKind, PanelSpec};
//! use openac_plugin::{Event, Host, Plugin, StartInfo};
//!
//! #[derive(Default)]
//! struct Counter {
//!     panel: Option<openac_plugin::ui::Panel>,
//!     clicks: i32,
//! }
//!
//! impl Plugin for Counter {
//!     fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
//!         let spec = PanelSpec::new(
//!             "main",
//!             "Counter",
//!             r#"<panel w="160" h="60"><label x="8" y="8" text="{Count}"/>
//!                <button x="8" y="28" w="60" h="20" text="Add" onclick="{Add}"/></panel>"#,
//!         )
//!         .bind("Count", BindingKind::Text)
//!         .bind("Add", BindingKind::Action);
//!         let mut panel = host.register_panel(&spec)?;
//!         panel.set("Count", "0");
//!         panel.flush(host)?;
//!         self.panel = Some(panel);
//!         Ok(())
//!     }
//!
//!     fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
//!         if let Event::Ui(openac_plugin::ui::UiEvent::Action(action)) = event
//!             && action.handler == "Add"
//!             && let Some(panel) = self.panel.as_mut()
//!         {
//!             self.clicks += 1;
//!             panel.set("Count", self.clicks.to_string());
//!             let _ = panel.flush(host);
//!         }
//!     }
//! }
//!
//! let mut host = openac_plugin::testing::FakeHost::new("example.counter");
//! let mut plugin = Counter::default();
//! host.enable(&mut plugin).unwrap();
//! let panel = plugin.panel.as_ref().unwrap().id();
//! host.click(&mut plugin, panel, "Add");
//! assert_eq!(host.panel_value(panel, "Count"), Some(openac_plugin::ui::UiValue::Text("1".into())));
//! ```

pub mod map;
pub mod route;

use std::collections::BTreeMap;

pub use ac_plugin_abi::ui::theme_metrics;
pub use ac_plugin_abi::ui::{
    ActionValue, BindingDecl, BindingKind, CanvasState, CodepointRange, Color, FONT_MAXIMUM_BYTES,
    FONT_MAXIMUM_COUNT, FONT_MAXIMUM_FILE_BYTES, FONT_MAXIMUM_GLYPHS, FONT_MAXIMUM_PIXEL_SIZE,
    FONT_MINIMUM_PIXEL_SIZE, FontWeight, GroundShape, GroundShapeKind, ItemRows, MapPoint,
    MarkerAnchor, PaintCmd, PointerEvent, ProjectileSample, Rect, RowAction, RowStep,
    TypedBindingDecl, TypedBindingKind, TypedValue, UiAction, UiEvent, UiValue, WorldIcon,
    WorldLabel, WorldLine, WorldTriangle,
};
use ac_plugin_abi::ui::{
    BundledFontRequest, CanvasChange, CanvasPaint, CanvasRegistration, ClientWindowRequest,
    ControlLabel, ControlRef, ControlVisible, DialogRequest, EncodedImage, FontInfo,
    FontLoadRequest, FontMeasure, IconSet, ImageInfo, ItemRow, ItemTypeDecl, ItemValue, LineSet,
    ModalShow, NamedItemValue, NamedTypedValue, NamedValue, PanelRegistration, PanelValues,
    RgbaImage, ShapeSet, TextSize, TriangleSet, TypedPanelRegistration, TypedPanelValues, ViewShow,
};
use ac_plugin_abi::{Import, Wire};

use crate::Host;
use crate::host::decode;

/// One UI import call: the request bytes in, the reply bytes out.
macro_rules! ui_call {
    ($host:ident, $import:ident, $sys:ident, $request:expr) => {{
        let request: &[u8] = $request;
        #[cfg(target_arch = "wasm32")]
        {
            let _ = &$host;
            let _ = Import::$import;
            crate::sys_ui::$sys(request)
        }
        #[cfg(not(target_arch = "wasm32"))]
        {
            $host.transport.call(Import::$import, request)
        }
    }};
}

/// The most images a plugin may hold at once.
pub const MAXIMUM_IMAGES: usize = 256;
/// The most bytes of its own pixels a plugin may hold.
pub const MAXIMUM_IMAGE_BYTES: u64 = 32 * 1024 * 1024;
/// The widest or tallest a plugin's own image may be.
pub const MAXIMUM_IMAGE_DIMENSION: u32 = 2048;
/// The most canvases a plugin may hold.
pub const MAXIMUM_CANVASES: usize = 8;
/// The most world labels a plugin may show at once.
pub const MAXIMUM_LABELS: usize = 256;
/// The most characters one label may have.
pub const MAXIMUM_LABEL_TEXT: usize = 128;

/// A window to register: its identity, dock slot, markup and bindings
/// (OpenAC `PluginPanelDescriptor` plus the markup and the binding type).
#[derive(Debug, Clone, PartialEq)]
pub struct PanelSpec {
    registration: PanelRegistration,
    /// Every binding with its full kind (the newer kinds included).
    typed: Vec<TypedBindingDecl>,
    /// The item types of its items lists.
    item_types: Vec<ItemTypeDecl>,
    /// It uses a kind the first registration cannot carry.
    needs_typed: bool,
}

/// The item type of a `<repeat>`'s list: the names each item carries
/// (OpenAC: the list's element type). A handler it declares fires for one
/// row: its [`UiAction`] names the row, read with [`UiAction::row_action`].
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ItemType {
    type_name: String,
    bindings: Vec<TypedBindingDecl>,
    lists: Vec<(String, ItemType)>,
}

impl ItemType {
    /// An item type called `type_name` (it appears in error messages).
    #[must_use]
    pub fn new(type_name: &str) -> Self {
        Self {
            type_name: type_name.to_owned(),
            ..Self::default()
        }
    }

    /// Declares a name an item carries.
    #[must_use]
    pub fn bind(mut self, name: &str, kind: impl Into<TypedBindingKind>) -> Self {
        self.bindings.push(TypedBindingDecl {
            name: name.to_owned(),
            kind: kind.into(),
        });
        self
    }

    /// Declares a list an item carries, for a nested `<repeat>`.
    #[must_use]
    pub fn items(mut self, name: &str, item: ItemType) -> Self {
        self.bindings.push(TypedBindingDecl {
            name: name.to_owned(),
            kind: TypedBindingKind::Items,
        });
        self.lists.push((name.to_owned(), item));
        self
    }

    fn flatten(self, path: &str, out: &mut Vec<ItemTypeDecl>) {
        out.push(ItemTypeDecl {
            list: path.to_owned(),
            type_name: self.type_name,
            bindings: self.bindings,
        });
        for (name, item) in self.lists {
            item.flatten(&format!("{path}/{name}"), out);
        }
    }
}

impl PanelSpec {
    /// A window `window_id` captioned `title` showing `markup`: open at
    /// once, on the plugin dock, no bindings yet.
    #[must_use]
    pub fn new(window_id: &str, title: &str, markup: &str) -> Self {
        Self {
            registration: PanelRegistration {
                window_id: window_id.to_owned(),
                title: title.to_owned(),
                icon_text: None,
                icon_surface_id: 0,
                start_visible: true,
                show_in_side_panel: true,
                markup: markup.to_owned(),
                binding_type: "Bindings".to_owned(),
                bindings: Vec::new(),
                icon_file: None,
            },
            typed: Vec::new(),
            item_types: Vec::new(),
            needs_typed: false,
        }
    }

    /// Declares a binding name the markup uses.
    #[must_use]
    pub fn bind(mut self, name: &str, kind: BindingKind) -> Self {
        self.registration.bindings.push(BindingDecl {
            name: name.to_owned(),
            kind,
        });
        self.typed.push(TypedBindingDecl {
            name: name.to_owned(),
            kind: kind.into(),
        });
        self
    }

    /// Declares a binding of any kind, the newer controls' included
    /// (`OptionalBool` for a checkbox's `bool?`, `BoolAction` for its
    /// `onchange`, ...).
    #[must_use]
    pub fn bind_typed(mut self, name: &str, kind: TypedBindingKind) -> Self {
        match old_kind(kind) {
            Some(old) => {
                self.registration.bindings.push(BindingDecl {
                    name: name.to_owned(),
                    kind: old,
                });
            }
            None => self.needs_typed = true,
        }
        self.typed.push(TypedBindingDecl {
            name: name.to_owned(),
            kind,
        });
        self
    }

    /// Declares a list of items of type `item` (a `<repeat>`'s `items`).
    #[must_use]
    pub fn bind_items(mut self, name: &str, item: ItemType) -> Self {
        self.typed.push(TypedBindingDecl {
            name: name.to_owned(),
            kind: TypedBindingKind::Items,
        });
        item.flatten(name, &mut self.item_types);
        self.needs_typed = true;
        self
    }

    /// The name error messages give the bindings' owner.
    #[must_use]
    pub fn binding_type(mut self, name: &str) -> Self {
        self.registration.binding_type = name.to_owned();
        self
    }

    /// Initials on the dock slot when there is no icon.
    #[must_use]
    pub fn icon_text(mut self, text: &str) -> Self {
        self.registration.icon_text = Some(text.to_owned());
        self
    }

    /// The dock slot's icon (a full image id or a bare index).
    #[must_use]
    pub fn icon(mut self, surface: u32) -> Self {
        self.registration.icon_surface_id = surface;
        self
    }

    /// The dock slot's own picture: a `.png` in the plugin's folder, by a path
    /// relative to it (API 1.4). It comes ahead of the plugin's `icon.png` and
    /// [`Self::icon`].
    #[must_use]
    pub fn icon_file(mut self, path: &str) -> Self {
        self.registration.icon_file = Some(path.to_owned());
        self
    }

    /// Closed until shown.
    #[must_use]
    pub fn start_hidden(mut self) -> Self {
        self.registration.start_visible = false;
        self
    }

    /// No slot on the plugin dock.
    #[must_use]
    pub fn off_shelf(mut self) -> Self {
        self.registration.show_in_side_panel = false;
        self
    }

    /// The registration record.
    #[must_use]
    pub fn registration(&self) -> &PanelRegistration {
        &self.registration
    }
}

/// A registered window: its number and the values last pushed for it.
#[derive(Debug, Clone, PartialEq)]
pub struct Panel {
    id: u32,
    window_id: String,
    pushed: BTreeMap<String, TypedValue>,
    pending: BTreeMap<String, TypedValue>,
}

impl Panel {
    /// Its number (the events' `panel`).
    #[must_use]
    pub fn id(&self) -> u32 {
        self.id
    }

    /// Its window id.
    #[must_use]
    pub fn window_id(&self) -> &str {
        &self.window_id
    }

    /// Sets a bound value; it reaches the client at the next
    /// [`Self::flush`], and only when it differs from what was pushed.
    pub fn set(&mut self, name: &str, value: impl Into<TypedValue>) {
        let value = value.into();
        if self.pushed.get(name) == Some(&value) {
            self.pending.remove(name);
        } else {
            self.pending.insert(name.to_owned(), value);
        }
    }

    /// Whether values wait for [`Self::flush`].
    #[must_use]
    pub fn has_pending(&self) -> bool {
        !self.pending.is_empty()
    }

    /// Sends the values set since the last flush (nothing when none
    /// changed).
    ///
    /// # Errors
    /// A name the window does not declare, or of another kind; nothing is
    /// applied then.
    pub fn flush(&mut self, host: &mut Host<'_>) -> Result<(), String> {
        if self.pending.is_empty() {
            return Ok(());
        }
        let pending = std::mem::take(&mut self.pending);
        let plain: Option<Vec<NamedValue>> = pending
            .iter()
            .map(|(name, value)| {
                old_value(value).map(|value| NamedValue {
                    name: name.clone(),
                    value,
                })
            })
            .collect();
        match plain {
            Some(values) => host.ui_panel_set(&PanelValues {
                panel: self.id,
                values,
            })?,
            None => host.ui_panel_set_typed(&TypedPanelValues {
                panel: self.id,
                values: pending
                    .iter()
                    .map(|(name, value)| NamedTypedValue {
                        name: name.clone(),
                        value: value.clone(),
                    })
                    .collect(),
            })?,
        }
        self.pushed.extend(pending);
        Ok(())
    }

    /// Shows or hides the window as its dock slot and close button do.
    pub fn show(&self, host: &mut Host<'_>, visible: bool) -> bool {
        host.show_panel(&self.window_id, visible)
    }

    /// Whether the window is shown.
    #[must_use]
    pub fn is_visible(&self, host: &mut Host<'_>) -> bool {
        host.view_visible(&self.window_id)
    }

    /// Removes the window.
    pub fn remove(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiPanelRemove, ui_panel_remove, &request);
        decode(Import::UiPanelRemove, &reply)
    }
}

/// An image the client holds for the plugin (handle 0: none).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Image {
    /// The client's number for it.
    pub handle: u32,
    /// Width, pixels.
    pub width: u32,
    /// Height, pixels.
    pub height: u32,
}

/// A font at one size that the client holds for the plugin, for drawing
/// canvas text ([`Painter::text_in`]). Each size of each font is its own
/// `Font`, and the metrics are here so text can be laid out without asking
/// again. Sizes and metrics are in canvas pixels.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Font {
    /// The client's number for it; 0 means no font.
    pub handle: u32,
    /// The size prepared: the size asked for, to the nearest quarter pixel.
    pub pixel_size: f32,
    /// How far apart two lines of this font sit.
    pub line_height: f32,
    /// How far below the top of a line its baseline sits, so text in different
    /// fonts can share a baseline.
    pub ascent: f32,
}

impl Font {
    /// No font: what every refused request returns. Text drawn with it
    /// draws nothing and measures nothing.
    pub const NONE: Self = Self {
        handle: 0,
        pixel_size: 0.0,
        line_height: 0.0,
        ascent: 0.0,
    };

    /// The client issued it. A released font stays valid by this test but
    /// draws nothing.
    #[must_use]
    pub fn is_valid(self) -> bool {
        self.handle != 0
    }
}

impl From<FontInfo> for Font {
    fn from(info: FontInfo) -> Self {
        Self {
            handle: info.handle,
            pixel_size: info.pixel_size,
            line_height: info.line_height,
            ascent: info.ascent,
        }
    }
}

impl Image {
    /// No image: what every refused request returns.
    pub const NONE: Self = Self {
        handle: 0,
        width: 0,
        height: 0,
    };

    /// The client issued it.
    #[must_use]
    pub fn is_valid(self) -> bool {
        self.handle != 0
    }
}

impl From<ImageInfo> for Image {
    fn from(i: ImageInfo) -> Self {
        Self {
            handle: i.handle,
            width: i.width,
            height: i.height,
        }
    }
}

/// Which screen corner or edge a canvas is measured from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Anchor {
    /// Top-left.
    #[default]
    TopLeft = 0,
    /// Top centre.
    TopCenter = 1,
    /// Top-right.
    TopRight = 2,
    /// Left centre.
    CenterLeft = 3,
    /// Centre.
    Center = 4,
    /// Right centre.
    CenterRight = 5,
    /// Bottom-left.
    BottomLeft = 6,
    /// Bottom centre.
    BottomCenter = 7,
    /// Bottom-right.
    BottomRight = 8,
}

/// A canvas to register (OpenAC `PluginCanvasDescriptor`).
#[derive(Debug, Clone, PartialEq)]
pub struct CanvasSpec {
    /// The plugin's own name for it.
    pub canvas_id: String,
    /// Width, pixels.
    pub width: i32,
    /// Height, pixels.
    pub height: i32,
    /// Measured from.
    pub anchor: Anchor,
    /// Pixels from the anchor.
    pub offset: (f32, f32),
    /// Shown at once.
    pub start_visible: bool,
    /// Takes pointer input while the plugin listens ([`Canvas::listen_pointer`]).
    pub accepts_pointer_input: bool,
    /// Sits inside one of the plugin's markup windows instead of on the
    /// screen by itself (API 1.12): a `<canvas id="...">` element whose id
    /// is this canvas's hosts it. The window's layout decides where it is
    /// and how large (`w`, `h`, `grow` and the other flex item attributes),
    /// and the canvas fills that rectangle in whole pixels. `width` and
    /// `height` are its size only until it is first laid out; after that
    /// [`Canvas::state`] reports the laid-out size, and each new size
    /// arrives as [`Event::CanvasResized`](crate::Event::CanvasResized),
    /// when the plugin paints it again. [`Canvas::resize`] answers false,
    /// and `anchor` and `offset` are kept but ignored. It is drawn and takes
    /// input only while it is shown and its element is: its window open, its
    /// tab or group not hidden. [`Canvas::is_available`] is true only while
    /// an element hosts it. Either may come first: a canvas waits for its
    /// element, and an element draws nothing until its canvas is
    /// registered. When two of the plugin's windows have an element with
    /// the same id, the first one mounted hosts the canvas, and the other
    /// takes over if that window goes away. Pointer input works as on the
    /// screen, in the canvas's own pixels.
    pub in_markup: bool,
}

impl CanvasSpec {
    /// A `width` x `height` canvas at the top-left, shown, click-through.
    #[must_use]
    pub fn new(canvas_id: &str, width: i32, height: i32) -> Self {
        Self {
            canvas_id: canvas_id.to_owned(),
            width,
            height,
            anchor: Anchor::TopLeft,
            offset: (0.0, 0.0),
            start_visible: true,
            accepts_pointer_input: false,
            in_markup: false,
        }
    }

    /// A canvas hosted by the `<canvas id="...">` element of the same id in
    /// one of the plugin's markup windows ([`Self::in_markup`]), shown,
    /// click-through. `width` x `height` is its size until it is laid out.
    #[must_use]
    pub fn in_markup(canvas_id: &str, width: i32, height: i32) -> Self {
        Self {
            in_markup: true,
            ..Self::new(canvas_id, width, height)
        }
    }
}

/// A registered canvas.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Canvas {
    id: u32,
    width: i32,
    height: i32,
    in_markup: bool,
}

/// A canvas paint being built: what the canvas shows after
/// [`Canvas::paint`] (OpenAC `IPluginPainter`). Pixels from the canvas's
/// top-left, y down; text in the client's interface font.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Painter {
    commands: Vec<PaintCmd>,
    width: i32,
    height: i32,
}

fn rect(x: f32, y: f32, w: f32, h: f32) -> Rect {
    Rect { x, y, w, h }
}

impl Painter {
    /// The canvas's width.
    #[must_use]
    pub fn width(&self) -> i32 {
        self.width
    }

    /// The canvas's height.
    #[must_use]
    pub fn height(&self) -> i32 {
        self.height
    }

    /// The commands so far.
    #[must_use]
    pub fn commands(&self) -> &[PaintCmd] {
        &self.commands
    }

    /// Fills the canvas; transparent black clears it.
    pub fn clear(&mut self, color: Color) {
        self.commands.push(PaintCmd::Clear(color));
    }

    /// Fills a rectangle.
    pub fn fill_rect(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color) {
        self.commands
            .push(PaintCmd::FillRect(rect(x, y, w, h), color));
    }

    /// Outlines a rectangle, inside its edges.
    pub fn stroke_rect(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color, thickness: f32) {
        self.commands
            .push(PaintCmd::StrokeRect(rect(x, y, w, h), color, thickness));
    }

    /// A straight line, centred on the segment, square ends.
    pub fn line(&mut self, from: (f32, f32), to: (f32, f32), color: Color, thickness: f32) {
        self.commands
            .push(PaintCmd::Line(from.0, from.1, to.0, to.1, color, thickness));
    }

    /// One line of text with its top-left at `at`.
    pub fn text(&mut self, text: &str, at: (f32, f32), color: Color, outline: bool) {
        self.commands.push(PaintCmd::Text(
            text.to_owned(),
            at.0,
            at.1,
            color,
            outline,
            0,
        ));
    }

    /// One line of text in one of the plugin's fonts, with its top-left at
    /// `at`. A font that was refused or released draws nothing.
    pub fn text_in(&mut self, font: Font, text: &str, at: (f32, f32), color: Color, outline: bool) {
        // Without a font this would be the interface font's text.
        if font.is_valid() {
            self.commands.push(PaintCmd::Text(
                text.to_owned(),
                at.0,
                at.1,
                color,
                outline,
                font.handle,
            ));
        }
    }

    /// An image stretched over a rectangle, tinted.
    pub fn image(&mut self, image: Image, x: f32, y: f32, w: f32, h: f32, tint: Color) {
        self.commands
            .push(PaintCmd::Image(image.handle, rect(x, y, w, h), tint));
    }

    /// An image over a rectangle, turned (radians, clockwise) and scaled
    /// about `pivot` (pixels from the rectangle's top-left).
    pub fn image_transformed(
        &mut self,
        image: Image,
        target: (f32, f32, f32, f32),
        tint: Color,
        rotation: f32,
        pivot: (f32, f32),
        scale: (f32, f32),
    ) {
        self.commands.push(PaintCmd::ImageTransformed(
            image.handle,
            rect(target.0, target.1, target.2, target.3),
            tint,
            rotation,
            pivot.0,
            pivot.1,
            scale.0,
            scale.1,
        ));
    }

    /// Clips what follows to a rectangle; pop it before painting.
    pub fn push_clip(&mut self, x: f32, y: f32, w: f32, h: f32) {
        self.commands.push(PaintCmd::PushClip(rect(x, y, w, h)));
    }

    /// Undoes the last clip.
    pub fn pop_clip(&mut self) {
        self.commands.push(PaintCmd::PopClip);
    }
}

/// The colour of bytes red, green, blue, alpha.
#[must_use]
pub const fn rgba(r: u8, g: u8, b: u8, a: u8) -> Color {
    Color { r, g, b, a }
}

impl Canvas {
    /// Its number (the pointer events' `canvas`).
    #[must_use]
    pub fn id(self) -> u32 {
        self.id
    }

    /// Width, pixels: as registered or last resized. An in-markup canvas's
    /// laid-out size is its [`Self::state`].
    #[must_use]
    pub fn width(self) -> i32 {
        self.width
    }

    /// Height, pixels, the same way.
    #[must_use]
    pub fn height(self) -> i32 {
        self.height
    }

    /// Whether it was registered in markup ([`CanvasSpec::in_markup`]).
    #[must_use]
    pub fn in_markup(self) -> bool {
        self.in_markup
    }

    /// Replaces what the canvas shows with what `paint` draws. The
    /// painter's size is the canvas's now: for an in-markup canvas, the size
    /// its element last laid it out at.
    ///
    /// # Errors
    /// Clips that do not balance, or the canvas is gone.
    pub fn paint(
        self,
        host: &mut Host<'_>,
        paint: impl FnOnce(&mut Painter),
    ) -> Result<(), String> {
        let (width, height) = if self.in_markup {
            self.state(host)
                .map_or((self.width, self.height), |s| (s.width, s.height))
        } else {
            (self.width, self.height)
        };
        let mut painter = Painter {
            commands: Vec::new(),
            width,
            height,
        };
        paint(&mut painter);
        let request = CanvasPaint {
            canvas: self.id,
            commands: painter.commands,
        }
        .to_bytes();
        let reply = ui_call!(host, UiCanvasPaint, ui_canvas_paint, &request);
        decode(Import::UiCanvasPaint, &reply)
    }

    fn set(self, host: &mut Host<'_>, change: CanvasChange) -> bool {
        let request = ac_plugin_abi::ui::CanvasSet {
            canvas: self.id,
            change,
        }
        .to_bytes();
        let reply = ui_call!(host, UiCanvasSet, ui_canvas_set, &request);
        decode(Import::UiCanvasSet, &reply)
    }

    /// Shows or hides it (what was painted is kept).
    pub fn set_visible(self, host: &mut Host<'_>, visible: bool) -> bool {
        self.set(host, CanvasChange::Visible(visible))
    }

    /// Moves its anchor.
    pub fn set_anchor(self, host: &mut Host<'_>, anchor: Anchor) -> bool {
        self.set(host, CanvasChange::Anchor(anchor as u8))
    }

    /// Moves its offset.
    pub fn set_offset(self, host: &mut Host<'_>, x: f32, y: f32) -> bool {
        self.set(host, CanvasChange::Offset(x, y))
    }

    /// Starts or stops listening for its pointer (it takes input only when
    /// it opted in and someone listens). The pointer arrives as
    /// [`UiEvent::Pointer`]; match its `kind` with [`PointerEvent::DOWN`],
    /// [`PointerEvent::UP`], [`PointerEvent::MOVE`] (a button held),
    /// [`PointerEvent::WHEEL`] and [`PointerEvent::CANCELLED`], and from API
    /// 1.6 [`PointerEvent::HOVER`] (over it, no button held, at most one per
    /// delivery at the latest place) and [`PointerEvent::LEAVE`] (one when
    /// the hovering ends), say for a tooltip.
    pub fn listen_pointer(self, host: &mut Host<'_>, listening: bool) -> bool {
        self.set(host, CanvasChange::Listen(listening))
    }

    /// Ends the press it holds; no cancel follows.
    pub fn release_pointer(self, host: &mut Host<'_>) -> bool {
        self.set(host, CanvasChange::ReleasePointer)
    }

    /// Changes the size of a canvas on the screen (API 1.12). It keeps its
    /// anchor, offset, visibility, listening and what it shows, so a canvas
    /// anchored at a right or bottom edge keeps that edge where it was; paint
    /// it again for the new size. [`Self::width`] and [`Self::height`]
    /// report the new size at once. The size it already has changes nothing
    /// and answers true. False, and nothing changes, for a width or height
    /// below 1, a removed canvas, or an in-markup canvas (its element
    /// decides its size).
    pub fn resize(&mut self, host: &mut Host<'_>, width: i32, height: i32) -> bool {
        let request = ac_plugin_abi::ui::CanvasResize {
            canvas: self.id,
            width,
            height,
        }
        .to_bytes();
        let reply = ui_call!(host, UiCanvasResize, ui_canvas_resize, &request);
        let done: bool = decode(Import::UiCanvasResize, &reply);
        if done {
            self.width = width;
            self.height = height;
        }
        done
    }

    /// The canvas as it stands (`None` once removed): its size is as
    /// registered, as last resized, or for an in-markup canvas as its
    /// element last laid it out.
    #[must_use]
    pub fn state(self, host: &mut Host<'_>) -> Option<CanvasState> {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiCanvasState, ui_canvas_state, &request);
        decode(Import::UiCanvasState, &reply)
    }

    /// Whether a window draws it right now.
    #[must_use]
    pub fn is_available(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiCanvasAvailable, ui_canvas_available, &request);
        decode(Import::UiCanvasAvailable, &reply)
    }

    /// Removes it.
    pub fn remove(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiCanvasRemove, ui_canvas_remove, &request);
        decode(Import::UiCanvasRemove, &reply)
    }
}

/// One of the client's own windows (OpenAC `PluginClientWindow`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClientWindow {
    /// Backpack and equipment.
    Inventory = 0,
    /// Attributes, vitals and skills.
    Character = 1,
    /// Birth, play time, deaths, augmentations.
    CharacterInformation = 2,
    /// The spellbook.
    Spellbook = 3,
    /// Map and housing.
    Map = 4,
    /// Settings.
    Options = 5,
    /// Friends, allegiance, fellowship, squelch.
    Social = 6,
    /// The quest journal.
    Journal = 7,
    /// Beneficial effects.
    PositiveEffects = 8,
    /// Harmful effects.
    NegativeEffects = 9,
    /// The connection indicator.
    LinkStatus = 10,
    /// The death penalty.
    Vitae = 11,
    /// The radar.
    Radar = 12,
}

/// A layer of world lines.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LineLayer {
    id: u32,
}

impl LineLayer {
    /// Replaces the layer's lines.
    pub fn set_lines(self, host: &mut Host<'_>, lines: &[WorldLine]) -> bool {
        let request = LineSet {
            layer: self.id,
            lines: lines.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(host, UiLinesSet, ui_lines_set, &request);
        decode(Import::UiLinesSet, &reply)
    }

    /// Removes the layer and its lines.
    pub fn remove(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiLinesRemove, ui_lines_remove, &request);
        decode(Import::UiLinesRemove, &reply)
    }
}

/// A layer of filled world triangles (API 1.3).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TriangleLayer {
    id: u32,
}

impl TriangleLayer {
    /// Replaces the layer's triangles. False, and the layer keeps what it
    /// had, when this set and the plugin's other layers' triangles come to
    /// more than 65,536. Meant for sets of up to a few thousand replaced a
    /// few times a second, not every frame.
    pub fn set_triangles(self, host: &mut Host<'_>, triangles: &[WorldTriangle]) -> bool {
        let request = TriangleSet {
            layer: self.id,
            triangles: triangles.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(host, UiTrianglesSet, ui_triangles_set, &request);
        decode(Import::UiTrianglesSet, &reply)
    }

    /// Removes the layer and its triangles.
    pub fn remove(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiTrianglesRemove, ui_triangles_remove, &request);
        decode(Import::UiTrianglesRemove, &reply)
    }
}

/// A layer of world markers (API 1.3).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MarkerLayer {
    id: u32,
}

impl MarkerLayer {
    /// Replaces the layer's icons. False, and the layer keeps what it had,
    /// when this set and the plugin's other layers' icons come to more than
    /// 256. Icons pinned to nothing or with an image of 0, or a size or
    /// range that is not usable, are dropped from the set.
    pub fn set_icons(self, host: &mut Host<'_>, icons: &[WorldIcon]) -> bool {
        let request = IconSet {
            layer: self.id,
            icons: icons.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(host, UiMarkersSetIcons, ui_markers_set_icons, &request);
        decode(Import::UiMarkersSetIcons, &reply)
    }

    /// Replaces the layer's ground shapes. False, and the layer keeps what
    /// it had, when this set and the plugin's other layers' shapes come to
    /// more than 256. Shapes pinned to nothing, or with a radius, band or
    /// sweep that is not usable, are dropped from the set.
    pub fn set_shapes(self, host: &mut Host<'_>, shapes: &[GroundShape]) -> bool {
        let request = ShapeSet {
            layer: self.id,
            shapes: shapes.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(host, UiMarkersSetShapes, ui_markers_set_shapes, &request);
        decode(Import::UiMarkersSetShapes, &reply)
    }

    /// Removes the layer and its markers.
    pub fn remove(self, host: &mut Host<'_>) -> bool {
        let request = self.id.to_bytes();
        let reply = ui_call!(host, UiMarkersRemove, ui_markers_remove, &request);
        decode(Import::UiMarkersRemove, &reply)
    }
}

/// An icon of `image` at `anchor`: 24 points, untinted, unframed, shown up to
/// 60 m away and over the world. Set [`WorldIcon::hidden_by_world`] for one
/// that hides behind it.
#[must_use]
pub fn world_icon(anchor: MarkerAnchor, image: Image) -> WorldIcon {
    WorldIcon {
        anchor,
        image: image.handle,
        size: 24.0,
        tint_rgba: 0xFFFF_FFFF,
        border_rgba: None,
        max_range: 60.0,
        hidden_by_world: false,
    }
}

/// The width of an arc's band when none is given, metres.
pub const DEFAULT_ARC_WIDTH: f32 = 0.15;

/// A band all the way round `anchor`, `width` metres wide inward from
/// `radius`, in one colour, `0xRRGGBBAA`.
#[must_use]
pub fn ground_ring(anchor: MarkerAnchor, radius: f32, width: f32, rgba: u32) -> GroundShape {
    GroundShape {
        anchor,
        kind: GroundShapeKind::Ring,
        radius,
        width,
        sweep_degrees: 360.0,
        color_rgba: rgba,
        ..GroundShape::default()
    }
}

/// A filled circle around `anchor`, in one colour, `0xRRGGBBAA`.
#[must_use]
pub fn ground_disc(anchor: MarkerAnchor, radius: f32, rgba: u32) -> GroundShape {
    GroundShape {
        anchor,
        kind: GroundShapeKind::Disc,
        radius,
        sweep_degrees: 360.0,
        filled: true,
        color_rgba: rgba,
        ..GroundShape::default()
    }
}

/// Part of a circle around `anchor`, in one colour, `0xRRGGBBAA`:
/// `sweep_degrees` clockwise from the compass bearing `start_degrees`, a
/// wedge when `filled` and otherwise a band [`DEFAULT_ARC_WIDTH`] wide. Set
/// [`GroundShape::faces_object`] for an arc that turns with its object.
#[must_use]
pub fn ground_arc(
    anchor: MarkerAnchor,
    radius: f32,
    start_degrees: f32,
    sweep_degrees: f32,
    filled: bool,
    rgba: u32,
) -> GroundShape {
    GroundShape {
        anchor,
        kind: GroundShapeKind::Arc,
        radius,
        width: DEFAULT_ARC_WIDTH,
        start_degrees,
        sweep_degrees,
        filled,
        color_rgba: rgba,
        ..GroundShape::default()
    }
}

/// A triangle between `a`, `b` and `c` filled in one colour,
/// `0xRRGGBBAA` (its alpha is how opaque the fill is). Give
/// [`WorldTriangle::color_b`] and [`WorldTriangle::color_c`] their own
/// colours to blend across the face.
#[must_use]
pub fn triangle(a: MapPoint, b: MapPoint, c: MapPoint, rgba: u32) -> WorldTriangle {
    WorldTriangle {
        a,
        b,
        c,
        color_a: rgba,
        color_b: rgba,
        color_c: rgba,
    }
}

/// A label over `object` saying `text`: white, outlined, on the first
/// line, shown up to 60 m away (OpenAC `PluginWorldLabel`'s defaults).
#[must_use]
pub fn label(object: u32, text: &str) -> WorldLabel {
    WorldLabel {
        object_id: object,
        text: text.to_owned(),
        r: 1.0,
        g: 1.0,
        b: 1.0,
        a: 1.0,
        height_offset: 0.0,
        line: 0,
        max_range: 60.0,
        outline: true,
    }
}

impl Host<'_> {
    /// Registers a window (OpenAC `RegisterPanelContent`).
    ///
    /// # Errors
    /// A blank id or title, an id the plugin already uses, or markup that
    /// does not parse or whose bindings do not resolve.
    pub fn register_panel(&mut self, spec: &PanelSpec) -> Result<Panel, String> {
        let id: Result<u32, String> = if spec.needs_typed {
            let r = &spec.registration;
            let request = TypedPanelRegistration {
                window_id: r.window_id.clone(),
                title: r.title.clone(),
                icon_text: r.icon_text.clone(),
                icon_surface_id: r.icon_surface_id,
                start_visible: r.start_visible,
                show_in_side_panel: r.show_in_side_panel,
                markup: r.markup.clone(),
                binding_type: r.binding_type.clone(),
                bindings: spec.typed.clone(),
                item_types: spec.item_types.clone(),
                icon_file: r.icon_file.clone(),
            }
            .to_bytes();
            let r = &request;
            let reply = ui_call!(self, UiPanelRegisterTyped, ui_panel_register_typed, r);
            decode(Import::UiPanelRegisterTyped, &reply)
        } else {
            let request = spec.registration.to_bytes();
            let reply = ui_call!(self, UiPanelRegister, ui_panel_register, &request);
            decode(Import::UiPanelRegister, &reply)
        };
        Ok(Panel {
            id: id?,
            window_id: spec.registration.window_id.clone(),
            pushed: BTreeMap::new(),
            pending: BTreeMap::new(),
        })
    }

    fn ui_panel_set(&mut self, values: &PanelValues) -> Result<(), String> {
        let request = values.to_bytes();
        let reply = ui_call!(self, UiPanelSet, ui_panel_set, &request);
        decode(Import::UiPanelSet, &reply)
    }

    /// Shows or hides one of the plugin's windows by id or title; false
    /// when it has none.
    pub fn show_panel(&mut self, view: &str, show: bool) -> bool {
        let request = ViewShow {
            view: view.to_owned(),
            show,
        }
        .to_bytes();
        let reply = ui_call!(self, UiPanelShow, ui_panel_show, &request);
        decode(Import::UiPanelShow, &reply)
    }

    /// Whether the plugin has a window with this id or title.
    #[must_use]
    pub fn view_exists(&mut self, view: &str) -> bool {
        let request = view.to_owned().to_bytes();
        let reply = ui_call!(self, UiViewExists, ui_view_exists, &request);
        decode(Import::UiViewExists, &reply)
    }

    /// Whether one of the plugin's windows is shown.
    #[must_use]
    pub fn view_visible(&mut self, view: &str) -> bool {
        let request = view.to_owned().to_bytes();
        let reply = ui_call!(self, UiViewVisible, ui_view_visible, &request);
        decode(Import::UiViewVisible, &reply)
    }

    /// Whether one of the plugin's windows holds a named control.
    #[must_use]
    pub fn control_exists(&mut self, view: &str, control: &str) -> bool {
        let request = ControlRef {
            view: view.to_owned(),
            control: control.to_owned(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiControlExists, ui_control_exists, &request);
        decode(Import::UiControlExists, &reply)
    }

    /// Replaces a button's, tab's, toggle's or label's text, dropping its
    /// binding; false when not found or it carries no text.
    pub fn set_control_label(&mut self, view: &str, control: &str, label: &str) -> bool {
        let request = ControlLabel {
            view: view.to_owned(),
            control: control.to_owned(),
            label: label.to_owned(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiControlLabel, ui_control_label, &request);
        decode(Import::UiControlLabel, &reply)
    }

    /// Shows or hides a control, dropping its bound visibility; false when
    /// not found.
    pub fn set_control_visible(&mut self, view: &str, control: &str, visible: bool) -> bool {
        let request = ControlVisible {
            view: view.to_owned(),
            control: control.to_owned(),
            visible,
        }
        .to_bytes();
        let reply = ui_call!(self, UiControlVisible, ui_control_visible, &request);
        decode(Import::UiControlVisible, &reply)
    }

    /// OpenAC's `InvalidateView`: bound values are pushed, so there is
    /// nothing to re-read; no host does it (false).
    #[must_use]
    pub fn invalidate_view(&mut self, _view: &str) -> bool {
        false
    }

    /// OpenAC's `FocusControl`: no host implements it (false).
    #[must_use]
    pub fn focus_control(&mut self, _view: &str, _control: &str) -> bool {
        false
    }

    fn client_window(&mut self, window: ClientWindow, action: u8) -> bool {
        let request = ClientWindowRequest {
            window: window as u32,
            action,
        }
        .to_bytes();
        let reply = ui_call!(self, UiClientWindow, ui_client_window, &request);
        decode(Import::UiClientWindow, &reply)
    }

    /// Shows a client window if hidden, hides it if shown: whether it ended
    /// up visible (false without a window).
    pub fn toggle_client_window(&mut self, window: ClientWindow) -> bool {
        self.client_window(window, 0)
    }

    /// Shows a client window: whether it is now visible.
    pub fn show_client_window(&mut self, window: ClientWindow) -> bool {
        self.client_window(window, 1)
    }

    /// Hides a client window: whether the window has it.
    pub fn hide_client_window(&mut self, window: ClientWindow) -> bool {
        self.client_window(window, 2)
    }

    /// Whether a client window is visible.
    #[must_use]
    pub fn is_client_window_visible(&mut self, window: ClientWindow) -> bool {
        self.client_window(window, 3)
    }

    /// Registers a canvas.
    ///
    /// # Errors
    /// A blank id, a size below 1, an id the plugin already has, or the
    /// plugin already holds [`MAXIMUM_CANVASES`].
    pub fn register_canvas(&mut self, spec: &CanvasSpec) -> Result<Canvas, String> {
        let request = CanvasRegistration {
            canvas_id: spec.canvas_id.clone(),
            width: spec.width,
            height: spec.height,
            anchor: spec.anchor as u8,
            offset_x: spec.offset.0,
            offset_y: spec.offset.1,
            start_visible: spec.start_visible,
            accepts_pointer_input: spec.accepts_pointer_input,
            in_markup: spec.in_markup,
        }
        .to_bytes();
        let reply = ui_call!(self, UiCanvasRegister, ui_canvas_register, &request);
        let id: Result<u32, String> = decode(Import::UiCanvasRegister, &reply);
        Ok(Canvas {
            id: id?,
            width: spec.width,
            height: spec.height,
            in_markup: spec.in_markup,
        })
    }

    /// The width of `text` in the interface font and the font's line
    /// height, pixels (the same with or without a window).
    #[must_use]
    pub fn measure_text(&mut self, text: &str) -> (f32, f32) {
        let request = text.to_owned().to_bytes();
        let reply = ui_call!(self, UiMeasureText, ui_measure_text, &request);
        let size: TextSize = decode(Import::UiMeasureText, &reply);
        (size.width, size.height)
    }

    /// The client's bundled font, Noto Sans, at a size from
    /// [`FONT_MINIMUM_PIXEL_SIZE`] to [`FONT_MAXIMUM_PIXEL_SIZE`] pixels, in a
    /// weight. [`FontWeight::SemiBold`] is the weight themed window titles are
    /// set in. Shared with every plugin: a size counts its atlas against
    /// [`FONT_MAXIMUM_BYTES`] only while no other plugin holds it too.
    /// [`Font::NONE`] when refused, which is logged (see [`Self::font_load`]).
    /// Asking twice returns the same font, held twice. The same with or
    /// without a window.
    pub fn font_bundled(&mut self, pixel_size: f32, weight: FontWeight) -> Font {
        let request = BundledFontRequest { pixel_size, weight }.to_bytes();
        let reply = ui_call!(self, UiFontBundled, ui_font_bundled, &request);
        decode::<FontInfo>(Import::UiFontBundled, &reply).into()
    }

    /// A TrueType (`.ttf`) or OpenType (`.otf`) font the plugin ships, usually
    /// with `include_bytes!`, under the plugin's own name for the file. It
    /// prepares the characters `ranges` names, where the font has them, so an
    /// icon font can name the whole private-use area; no ranges prepare U+0020
    /// to U+024F, U+0370 to U+052F and U+2000 to U+206F. A character that was
    /// not prepared draws as the font's `?` if that was prepared, and as
    /// nothing otherwise.
    ///
    /// A plugin may hold [`FONT_MAXIMUM_COUNT`] fonts, each of at most
    /// [`FONT_MAXIMUM_GLYPHS`] characters, and its own fonts may take
    /// [`FONT_MAXIMUM_BYTES`], counting the files and their atlases. A name
    /// may be 256 bytes and a request may list 256 ranges. A file over
    /// [`FONT_MAXIMUM_FILE_BYTES`], one that is not a usable font or whose
    /// outlines take too much work to draw, or a request past a limit is
    /// [`Font::NONE`]. A refusal is logged once; each kind of refusal (size,
    /// count, budget, an unusable file and so on) for at most eight requests,
    /// then once more to say the rest are left out. A font whose kerning
    /// takes too long to read is kept with part of it, and that is logged. A
    /// second request for the same name, size and characters is the same font
    /// held once more.
    /// Preparing a font takes some milliseconds, so ask up front.
    pub fn font_load(
        &mut self,
        name: &str,
        bytes: &[u8],
        pixel_size: f32,
        ranges: &[CodepointRange],
    ) -> Font {
        // Past this the request itself would not fit a call.
        let bytes = if bytes.len() > FONT_MAXIMUM_FILE_BYTES {
            &bytes[..FONT_MAXIMUM_FILE_BYTES + 1]
        } else {
            bytes
        };
        let request = FontLoadRequest {
            name: name.to_owned(),
            bytes: bytes.to_vec(),
            pixel_size,
            ranges: ranges.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiFontLoad, ui_font_load, &request);
        decode::<FontInfo>(Import::UiFontLoad, &reply).into()
    }

    /// The width of `text` in a font, with its kerning, as it is drawn, and the
    /// font's line height, pixels. Nothing for a font the plugin does not hold.
    #[must_use]
    pub fn font_measure(&mut self, font: Font, text: &str) -> (f32, f32) {
        let request = FontMeasure {
            font: font.handle,
            text: text.to_owned(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiFontMeasure, ui_font_measure, &request);
        let size: TextSize = decode(Import::UiFontMeasure, &reply);
        (size.width, size.height)
    }

    /// Lets go of one hold on a font: it goes on the last. False for a font
    /// the plugin does not hold. A plugin's fonts go when it unloads.
    pub fn font_release(&mut self, font: Font) -> bool {
        let request = font.handle.to_bytes();
        let reply = ui_call!(self, UiFontRelease, ui_font_release, &request);
        decode(Import::UiFontRelease, &reply)
    }

    /// How many distinct fonts the plugin holds.
    #[must_use]
    pub fn font_count(&mut self) -> u32 {
        let reply = ui_call!(self, UiFontCount, ui_font_count, &[]);
        decode(Import::UiFontCount, &reply)
    }

    /// The plugin theme the player picked under Appearance and its colours,
    /// so a canvas can paint like the plugin's themed windows (the client
    /// restyles `theme="plugin"` windows itself). Classic, with no palette,
    /// until a window tells the session; `Event::ThemeChanged` carries each
    /// change.
    #[must_use]
    pub fn theme(&mut self) -> ac_plugin_abi::ui::ThemeInfo {
        let request = ().to_bytes();
        let reply = ui_call!(self, UiTheme, ui_theme, &request);
        decode(Import::UiTheme, &reply)
    }

    /// The client's art for a render surface (a full id or a bare index).
    pub fn image_client_art(&mut self, surface: u32) -> Image {
        let request = surface.to_bytes();
        let reply = ui_call!(self, UiImageClientArt, ui_image_client_art, &request);
        decode::<ImageInfo>(Import::UiImageClientArt, &reply).into()
    }

    /// A spell's icon.
    pub fn image_spell_icon(&mut self, spell: u32) -> Image {
        let request = spell.to_bytes();
        let reply = ui_call!(self, UiImageSpellIcon, ui_image_spell_icon, &request);
        decode::<ImageInfo>(Import::UiImageSpellIcon, &reply).into()
    }

    /// A live object's composed icon.
    pub fn image_object_icon(&mut self, object: u32) -> Image {
        let request = object.to_bytes();
        let reply = ui_call!(self, UiImageObjectIcon, ui_image_object_icon, &request);
        decode::<ImageInfo>(Import::UiImageObjectIcon, &reply).into()
    }

    /// The plugin's own pixels (8-bit RGBA rows) under its own name for
    /// them; asking again under the same name is the same image held once
    /// more.
    pub fn image_rgba(&mut self, name: &str, width: u32, height: u32, rgba: &[u8]) -> Image {
        let request = RgbaImage {
            name: name.to_owned(),
            width,
            height,
            rgba: rgba.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiImageRgba, ui_image_rgba, &request);
        decode::<ImageInfo>(Import::UiImageRgba, &reply).into()
    }

    /// The plugin's own art as a file (PNG, JPEG, BMP, TGA or GIF; a GIF's
    /// first frame) under its own name for it, decoded by the client
    /// (OpenAC `IPluginImages.FromStream`); asking again under the same
    /// name is the same image held once more. Refused (handle 0) for a file
    /// that does not decode or is over the image budget.
    pub fn image_decode(&mut self, name: &str, bytes: &[u8]) -> Image {
        let request = EncodedImage {
            name: name.to_owned(),
            bytes: bytes.to_vec(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiImageDecode, ui_image_decode, &request);
        decode::<ImageInfo>(Import::UiImageDecode, &reply).into()
    }

    /// Lets go of one hold on an image.
    pub fn image_release(&mut self, image: Image) -> bool {
        let request = image.handle.to_bytes();
        let reply = ui_call!(self, UiImageRelease, ui_image_release, &request);
        decode(Import::UiImageRelease, &reply)
    }

    /// How many distinct images the plugin holds: the ones it asked for,
    /// and the file icons its markup windows are drawing, which count
    /// against the same limits.
    #[must_use]
    pub fn image_count(&mut self) -> u32 {
        let reply = ui_call!(self, UiImageCount, ui_image_count, &[]);
        decode(Import::UiImageCount, &reply)
    }

    /// Replaces the plugin's world labels; false (nothing changed) for more
    /// than [`MAXIMUM_LABELS`].
    pub fn show_labels(&mut self, labels: &[WorldLabel]) -> bool {
        let request = labels.to_vec().to_bytes();
        let reply = ui_call!(self, UiLabelsShow, ui_labels_show, &request);
        decode(Import::UiLabelsShow, &reply)
    }

    /// A new, empty world-line layer.
    pub fn create_line_layer(&mut self) -> LineLayer {
        let reply = ui_call!(self, UiLinesCreate, ui_lines_create, &[]);
        LineLayer {
            id: decode(Import::UiLinesCreate, &reply),
        }
    }

    /// A new, empty world-triangle layer (API 1.3).
    pub fn create_triangle_layer(&mut self) -> TriangleLayer {
        let reply = ui_call!(self, UiTrianglesCreate, ui_triangles_create, &[]);
        TriangleLayer {
            id: decode(Import::UiTrianglesCreate, &reply),
        }
    }

    /// A new, empty world-marker layer (API 1.3). Its markers over objects
    /// go when the character leaves the world (those at fixed positions
    /// stay), and the layer stays usable for the next stay.
    pub fn create_marker_layer(&mut self) -> MarkerLayer {
        let reply = ui_call!(self, UiMarkersCreate, ui_markers_create, &[]);
        MarkerLayer {
            id: decode(Import::UiMarkersCreate, &reply),
        }
    }

    /// Projectile markers to draw for a moment.
    pub fn show_projectile_samples(&mut self, samples: &[ProjectileSample]) {
        let request = samples.to_vec().to_bytes();
        ui_call!(self, UiProjectileDebug, ui_projectile_debug, &request);
    }
}

pub use ac_plugin_abi::keys::PluginKey;
pub use ac_plugin_abi::ui::{HotkeyState, KeyChord};

/// A chord: `key` with Ctrl, Alt and Shift as given (OpenAC
/// `PluginKeyChord`).
#[must_use]
pub fn chord(key: PluginKey, ctrl: bool, alt: bool, shift: bool) -> KeyChord {
    KeyChord {
        key: key.code(),
        ctrl,
        alt,
        shift,
    }
}

/// Hotkeys (OpenAC `IHotkeyRegistry`): registered with the session, bound
/// while a window with a key map is attached; a press arrives as
/// [`UiEvent::Hotkey`] with the id.
impl Host<'_> {
    /// Registers a hotkey under the plugin's own `id` (a second
    /// registration replaces the first). A stored player override replaces
    /// `chord`; a chord the client or another plugin already uses is
    /// refused (unbound) until it is free.
    ///
    /// # Errors
    /// A blank id or display name.
    pub fn register_hotkey(
        &mut self,
        id: &str,
        display_name: &str,
        chord: KeyChord,
    ) -> Result<HotkeyState, String> {
        self.call::<ac_plugin_abi::calls::UiHotkeyRegister>(
            &ac_plugin_abi::ui::HotkeyRegistration {
                id: id.to_owned(),
                display_name: display_name.to_owned(),
                chord,
            },
        )
    }

    /// Revokes one of the plugin's hotkeys.
    pub fn unregister_hotkey(&mut self, id: &str) -> bool {
        self.call::<ac_plugin_abi::calls::UiHotkeyUnregister>(&id.to_owned())
    }

    /// Rebinds one of the plugin's hotkeys, stored as the player's override
    /// (nothing changes with no window attached).
    pub fn rebind_hotkey(&mut self, id: &str, chord: KeyChord) -> Option<HotkeyState> {
        self.call::<ac_plugin_abi::calls::UiHotkeyRebind>(&ac_plugin_abi::ui::HotkeyRebind {
            id: id.to_owned(),
            chord,
        })
    }

    /// How one of the plugin's hotkeys stands now.
    pub fn hotkey_state(&mut self, id: &str) -> Option<HotkeyState> {
        self.call::<ac_plugin_abi::calls::UiHotkeyState>(&id.to_owned())
    }

    /// Hear (`true`) or stop hearing (`false`) the client's input actions
    /// as its key map fires them, each as [`UiEvent::InputAction`] with the
    /// action's and the activation's names; answers the setting before.
    /// Actions fire only from an attached window's keys, buttons and wheel.
    pub fn listen_input_actions(&mut self, listen: bool) -> bool {
        self.call::<ac_plugin_abi::calls::UiInputActionsListen>(&listen)
    }
}

/// The first registration's kind for `kind`, when it has one.
fn old_kind(kind: TypedBindingKind) -> Option<BindingKind> {
    use TypedBindingKind as T;
    Some(match kind {
        T::Bool => BindingKind::Bool,
        T::Int => BindingKind::Int,
        T::Uint => BindingKind::Uint,
        T::Float => BindingKind::Float,
        T::Text => BindingKind::Text,
        T::TextList => BindingKind::TextList,
        T::UintList => BindingKind::UintList,
        T::IntList => BindingKind::IntList,
        T::BoolList => BindingKind::BoolList,
        T::Action => BindingKind::Action,
        T::IntAction => BindingKind::IntAction,
        T::FloatAction => BindingKind::FloatAction,
        T::TextAction => BindingKind::TextAction,
        _ => return None,
    })
}

/// The first push's value for `value`, when it has one.
fn old_value(value: &TypedValue) -> Option<UiValue> {
    use TypedValue as T;
    Some(match value {
        T::Bool(v) => UiValue::Bool(*v),
        T::Int(v) => UiValue::Int(*v),
        T::Uint(v) => UiValue::Uint(*v),
        T::Float(v) => UiValue::Float(*v),
        T::Text(v) => UiValue::Text(v.clone()),
        T::TextList(v) => UiValue::TextList(v.clone()),
        T::UintList(v) => UiValue::UintList(v.clone()),
        T::IntList(v) => UiValue::IntList(v.clone()),
        T::BoolList(v) => UiValue::BoolList(v.clone()),
        _ => return None,
    })
}

/// One item of a `<repeat>`'s list: its values by name, and the lists it
/// carries for nested repeats.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Item {
    values: Vec<NamedItemValue>,
    lists: Vec<(String, Items)>,
}

impl Item {
    /// An item with no values yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets a value (any kind but a list of items; see [`Self::items`]).
    #[must_use]
    pub fn set(mut self, name: &str, value: impl Into<TypedValue>) -> Self {
        let value = match value.into() {
            TypedValue::Bool(v) => ItemValue::Bool(v),
            TypedValue::Int(v) => ItemValue::Int(v),
            TypedValue::Uint(v) => ItemValue::Uint(v),
            TypedValue::Float(v) => ItemValue::Float(v),
            TypedValue::Text(v) => ItemValue::Text(v),
            TypedValue::TextList(v) => ItemValue::TextList(v),
            TypedValue::UintList(v) => ItemValue::UintList(v),
            TypedValue::IntList(v) => ItemValue::IntList(v),
            TypedValue::BoolList(v) => ItemValue::BoolList(v),
            TypedValue::OptionalBool(v) => ItemValue::OptionalBool(v),
            TypedValue::FloatList(v) => ItemValue::FloatList(v),
            TypedValue::Items(_) => return self,
        };
        self.values.retain(|v| v.name != name);
        self.values.push(NamedItemValue {
            name: name.to_owned(),
            value,
        });
        self
    }

    /// Sets a list it carries (a nested `<repeat>`'s items).
    #[must_use]
    pub fn items(mut self, name: &str, items: Items) -> Self {
        self.lists.retain(|(n, _)| n != name);
        self.lists.push((name.to_owned(), items));
        self
    }
}

/// A `<repeat>`'s list, built item by item.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Items {
    items: Vec<Item>,
}

impl Items {
    /// An empty list.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds an item at the end.
    #[must_use]
    pub fn item(mut self, item: Item) -> Self {
        self.items.push(item);
        self
    }

    fn flatten(self, parent: i32, list: &str, rows: &mut Vec<ItemRow>) {
        for item in self.items {
            let index = i32::try_from(rows.len()).unwrap_or(i32::MAX);
            rows.push(ItemRow {
                parent,
                list: list.to_owned(),
                values: item.values,
            });
            for (name, nested) in item.lists {
                nested.flatten(index, &name, rows);
            }
        }
    }
}

impl From<Items> for TypedValue {
    fn from(items: Items) -> Self {
        let mut rows = Vec::new();
        items.flatten(-1, "", &mut rows);
        Self::Items(ItemRows { rows })
    }
}

impl FromIterator<Item> for Items {
    fn from_iter<I: IntoIterator<Item = Item>>(iter: I) -> Self {
        Self {
            items: iter.into_iter().collect(),
        }
    }
}

/// A question for the player in a dialog box (OpenAC `PluginDialog`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Dialog {
    /// The caption (may be empty).
    pub title: String,
    /// The question; long text wraps (may be empty).
    pub message: String,
    /// The buttons left to right, one to four; the last is the cancel
    /// button. Empty means OK and Cancel.
    pub buttons: Vec<String>,
    /// The id or title of the window it is centred on and blocks until it
    /// is answered (none: the screen, blocking nothing).
    pub owner_view: Option<String>,
}

impl Dialog {
    /// A dialog with OK and Cancel, centred on the screen.
    #[must_use]
    pub fn new(title: &str, message: &str) -> Self {
        Self {
            title: title.to_owned(),
            message: message.to_owned(),
            ..Self::default()
        }
    }

    /// Its buttons, left to right (the last is the cancel button).
    #[must_use]
    pub fn buttons(mut self, buttons: &[&str]) -> Self {
        self.buttons = buttons.iter().map(|b| (*b).to_owned()).collect();
        self
    }

    /// The window it blocks.
    #[must_use]
    pub fn owner(mut self, view: &str) -> Self {
        self.owner_view = Some(view.to_owned());
        self
    }
}

/// A dialog waiting for its answer, which arrives as [`UiEvent::Action`]
/// read with [`UiAction::dialog_answer`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DialogHandle(pub u32);

impl DialogHandle {
    /// Withdraws a question that went stale: a dialog still showing or
    /// queued closes and is answered with its last button; false once it is
    /// answered.
    pub fn withdraw(self, host: &mut Host<'_>) -> bool {
        let request = self.0.to_bytes();
        let reply = ui_call!(host, UiDialogWithdraw, ui_dialog_withdraw, &request);
        decode(Import::UiDialogWithdraw, &reply)
    }
}

/// Dialogs and modal windows (OpenAC `IUiRegistry.Confirm`, `ShowModal`).
impl Host<'_> {
    /// Asks the player a question in a dialog box. The dialog blocks only
    /// its owner window; several for one owner queue. Its answer arrives
    /// once, as [`UiEvent::Action`] read with [`UiAction::dialog_answer`]:
    /// the button picked; Escape, the close button, the owner window
    /// closing and the window detaching answer with the last one. None when
    /// no window draws it (then it is never answered).
    ///
    /// # Errors
    /// More than four buttons, or a blank one.
    pub fn confirm(&mut self, dialog: &Dialog) -> Result<Option<DialogHandle>, String> {
        let request = DialogRequest {
            title: dialog.title.clone(),
            message: dialog.message.clone(),
            buttons: dialog.buttons.clone(),
            owner_view: dialog.owner_view.clone(),
        }
        .to_bytes();
        let reply = ui_call!(self, UiDialogConfirm, ui_dialog_confirm, &request);
        let handle: Result<Option<u32>, String> = decode(Import::UiDialogConfirm, &reply);
        handle.map(|h| h.map(DialogHandle))
    }

    /// Shows one of the plugin's windows, as [`Host::show_panel`] does, and
    /// makes it modal over another of its windows (`owner_view`), which
    /// draws dimmed and ignores presses until the shown window hides or
    /// goes. False when the plugin has no such window, and without a window
    /// attached.
    pub fn show_modal(&mut self, view: &str, owner_view: Option<&str>) -> bool {
        let request = ModalShow {
            view: view.to_owned(),
            owner_view: owner_view.map(str::to_owned),
        }
        .to_bytes();
        let reply = ui_call!(self, UiPanelShowModal, ui_panel_show_modal, &request);
        decode(Import::UiPanelShowModal, &reply)
    }

    fn ui_panel_set_typed(&mut self, values: &TypedPanelValues) -> Result<(), String> {
        let request = values.to_bytes();
        let reply = ui_call!(self, UiPanelSetTyped, ui_panel_set_typed, &request);
        decode(Import::UiPanelSetTyped, &reply)
    }
}
