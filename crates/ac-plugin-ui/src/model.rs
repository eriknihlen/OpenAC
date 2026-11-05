//! The plain records plugin UI state is made of, shared by the session's
//! state ([`crate::PluginUi`]) and a window's copy of it
//! ([`crate::PluginUiView`]).

use std::collections::BTreeMap;
use std::sync::Arc;

use crate::markup::{BoolSource, MarkupDocument, Values};
use crate::value::UiValue;

/// The plugin a registration belongs to.
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct UiOwner {
    /// Its manifest id.
    pub id: String,
    /// Its name as the player sees it.
    pub display_name: String,
}

impl UiOwner {
    /// An owner from its id and display name.
    #[must_use]
    pub fn new(id: impl Into<String>, display_name: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            display_name: display_name.into(),
        }
    }
}

// A plugin's decoded `icon.png`, from the icon crate the launcher shares.
pub use ac_plugin_icon::PluginIcon;

/// What a plugin's folder holds for the dock icon one of its windows names.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct WindowIcon {
    /// The file as named.
    pub named: Option<PluginIcon>,
    /// Its version for a light theme.
    pub light: Option<PluginIcon>,
    /// Its version for every other theme.
    pub dark: Option<PluginIcon>,
}

impl WindowIcon {
    /// The picture for a dock in a light theme or not: its version when there
    /// is one, else the named file.
    #[must_use]
    pub fn for_theme(&self, light: bool) -> Option<&PluginIcon> {
        let version = if light { &self.light } else { &self.dark };
        version.as_ref().or(self.named.as_ref())
    }

    /// Nothing there could be used.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.named.is_none() && self.light.is_none() && self.dark.is_none()
    }
}

/// How a plugin's window is identified and presented (OpenAC
/// `PluginPanelDescriptor`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PanelDescriptor {
    /// The plugin's own short name for the window; part of the key its
    /// position is saved under.
    pub window_id: String,
    /// The caption.
    pub title: String,
    /// Initials on the dock slot when there is no icon.
    pub icon_text: Option<String>,
    /// The dock slot's icon (a full image id or a bare index; 0: none).
    pub icon_surface_id: u32,
    /// Open as soon as it is registered.
    pub start_visible: bool,
    /// Gets a button on the plugin dock.
    pub show_in_side_panel: bool,
    /// The window's own dock icon: a `.png` in the plugin's folder, by a path
    /// relative to it.
    pub icon_file: Option<String>,
}

impl PanelDescriptor {
    /// A descriptor with OpenAC's defaults: no icon, open at once, on the
    /// dock.
    #[must_use]
    pub fn new(window_id: impl Into<String>, title: impl Into<String>) -> Self {
        Self {
            window_id: window_id.into(),
            title: title.into(),
            icon_text: None,
            icon_surface_id: 0,
            start_visible: true,
            show_in_side_panel: true,
            icon_file: None,
        }
    }
}

/// One plugin window as a window draws it: its markup, the values pushed
/// for its bindings, what the plugin changed on its controls, and whether
/// the player (or the plugin) wants it shown.
#[derive(Debug, Clone, PartialEq)]
pub struct PanelView {
    /// The session's number for it.
    pub id: u32,
    /// Its plugin.
    pub owner: UiOwner,
    /// Its identity and dock slot.
    pub descriptor: PanelDescriptor,
    /// Its markup, parsed.
    pub document: Arc<MarkupDocument>,
    /// The bindings' values, by name.
    pub values: BTreeMap<String, UiValue>,
    /// Text set on controls (`SetControlLabel`), by element index; it
    /// replaces the markup's text and binding.
    pub label_overrides: BTreeMap<usize, String>,
    /// Visibility set on controls (`SetControlVisible`), by element index.
    pub visible_overrides: BTreeMap<usize, bool>,
    /// The player's (or the plugin's) show or close request.
    pub requested_visible: bool,
    /// Its plugin's `icon.png`, when the plugin's folder has one that
    /// decodes: the dock slot shows it ahead of the descriptor's icon.
    pub file_icon: Option<PluginIcon>,
    /// The file its descriptor names, when any of it could be used: the dock
    /// slot shows it ahead of the plugin's `icon.png`.
    pub window_icon: Option<WindowIcon>,
}

impl PanelView {
    /// The retained window's name: `plugin:{pluginId}:{windowId}`.
    #[must_use]
    pub fn window_name(&self) -> String {
        window_name(&self.owner.id, &self.descriptor.window_id)
    }

    /// What the markup's `<panel visible="{...}">` says (true without one).
    #[must_use]
    pub fn available(&self) -> bool {
        match &self.document.panel.visible {
            None => true,
            Some(name) => BoolSource::Bound(name.clone()).read(&self.values, true),
        }
    }

    /// On screen: requested, and the bound visibility allows it (OpenAC
    /// `PluginWindowVisibilityController.ShouldBeVisible`).
    #[must_use]
    pub fn shown(&self) -> bool {
        self.requested_visible && self.available()
    }

    /// The player opened the window (OpenAC `OnShown`).
    pub fn on_shown(&mut self) {
        self.requested_visible = true;
    }

    /// The window was hidden: a hide while the bound visibility is false is
    /// the binding's doing and leaves the request (OpenAC `OnHidden`).
    pub fn on_hidden(&mut self) {
        if self.available() {
            self.requested_visible = false;
        }
    }

    /// Whether element `node` is shown: the plugin's override, else its
    /// markup (an ancestor may still hide it).
    #[must_use]
    pub fn node_visible(&self, node: usize) -> bool {
        if let Some(v) = self.visible_overrides.get(&node) {
            return *v;
        }
        self.document
            .nodes
            .get(node)
            .is_some_and(|n| n.common.visible.read(&self.values, true))
    }
}

impl Values for PanelView {
    fn value(&self, name: &str) -> Option<&UiValue> {
        self.values.get(name)
    }
}

/// `plugin:{pluginId}:{windowId}`.
#[must_use]
pub fn window_name(plugin_id: &str, window_id: &str) -> String {
    format!("plugin:{plugin_id}:{window_id}")
}

/// A colour in a paint command: red, green, blue, alpha bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Color8 {
    /// Red.
    pub r: u8,
    /// Green.
    pub g: u8,
    /// Blue.
    pub b: u8,
    /// Alpha.
    pub a: u8,
}

impl Color8 {
    /// Opaque white: the tint that leaves an image unchanged.
    pub const WHITE: Self = Self::new(255, 255, 255, 255);

    /// A colour from its bytes.
    #[must_use]
    pub const fn new(r: u8, g: u8, b: u8, a: u8) -> Self {
        Self { r, g, b, a }
    }

    /// As 0..=1 floats.
    #[must_use]
    pub fn to_rgba(self) -> crate::value::Rgba {
        crate::value::Rgba::new(
            f32::from(self.r) / 255.0,
            f32::from(self.g) / 255.0,
            f32::from(self.b) / 255.0,
            f32::from(self.a) / 255.0,
        )
    }
}

/// A rectangle in canvas pixels.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PaintRect {
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub w: f32,
    /// Height.
    pub h: f32,
}

/// One drawing step of a canvas, in its own pixels from its top-left
/// corner, y down (OpenAC `IPluginPainter`).
#[derive(Debug, Clone, PartialEq)]
pub enum PaintCmd {
    /// Fill the whole canvas.
    Clear(Color8),
    /// Fill a rectangle.
    FillRect {
        /// Where.
        rect: PaintRect,
        /// Fill.
        color: Color8,
    },
    /// Outline a rectangle, inside its edges.
    StrokeRect {
        /// Where.
        rect: PaintRect,
        /// Outline colour.
        color: Color8,
        /// Outline width.
        thickness: f32,
    },
    /// A straight line, centred on the segment, square ends.
    Line {
        /// Start.
        from: (f32, f32),
        /// End.
        to: (f32, f32),
        /// Colour.
        color: Color8,
        /// Width.
        thickness: f32,
    },
    /// One line of text in the interface font, top-left at the position.
    Text {
        /// The text.
        text: String,
        /// Top-left.
        at: (f32, f32),
        /// Colour.
        color: Color8,
        /// A dark outline around the glyphs.
        outline: bool,
        /// One of the plugin's fonts ([`crate::fonts`]); 0: the interface
        /// font. A font it no longer holds draws nothing.
        font: u32,
    },
    /// An image stretched over a rectangle (a released image draws
    /// nothing).
    Image {
        /// The plugin's image handle.
        image: u32,
        /// Where.
        rect: PaintRect,
        /// Multiplied in (white leaves it).
        tint: Color8,
    },
    /// An image over a rectangle scaled and turned about a pivot
    /// (clockwise on screen).
    ImageTransformed {
        /// The plugin's image handle.
        image: u32,
        /// Where, unturned and unscaled.
        rect: PaintRect,
        /// Multiplied in.
        tint: Color8,
        /// Radians, clockwise.
        rotation: f32,
        /// Pixels from the rectangle's top-left.
        pivot: (f32, f32),
        /// Sideways stretch.
        scale_x: f32,
        /// Vertical stretch.
        scale_y: f32,
    },
    /// Clip what follows to a rectangle (within the clip in force).
    PushClip(PaintRect),
    /// Undo the last clip.
    PopClip,
}

/// Which screen corner or edge a canvas is measured from (OpenAC
/// `PluginCanvasAnchor`, same order).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum CanvasAnchor {
    /// Top-left corner.
    #[default]
    TopLeft,
    /// Centre of the top edge.
    TopCenter,
    /// Top-right corner.
    TopRight,
    /// Centre of the left edge.
    CenterLeft,
    /// The centre.
    Center,
    /// Centre of the right edge.
    CenterRight,
    /// Bottom-left corner.
    BottomLeft,
    /// Centre of the bottom edge.
    BottomCenter,
    /// Bottom-right corner.
    BottomRight,
}

impl CanvasAnchor {
    /// Every anchor, by number.
    pub const ALL: [Self; 9] = [
        Self::TopLeft,
        Self::TopCenter,
        Self::TopRight,
        Self::CenterLeft,
        Self::Center,
        Self::CenterRight,
        Self::BottomLeft,
        Self::BottomCenter,
        Self::BottomRight,
    ];

    /// The anchor numbered `n`.
    #[must_use]
    pub fn from_index(n: u8) -> Option<Self> {
        Self::ALL.get(usize::from(n)).copied()
    }

    /// Its number.
    #[must_use]
    pub fn index(self) -> u8 {
        Self::ALL.iter().position(|a| *a == self).unwrap_or(0) as u8
    }
}

/// Where a canvas of `size` sits on a layer of `layer` pixels: the anchor's
/// corner or edge plus the offset, rounded to whole pixels (OpenAC
/// `PluginCanvasElement.Layout`).
#[must_use]
pub fn canvas_origin(
    anchor: CanvasAnchor,
    offset: (f32, f32),
    size: (f32, f32),
    layer: (f32, f32),
) -> (f32, f32) {
    let mut x = offset.0;
    let mut y = offset.1;
    match anchor {
        CanvasAnchor::TopCenter | CanvasAnchor::Center | CanvasAnchor::BottomCenter => {
            x += (layer.0 - size.0) * 0.5;
        }
        CanvasAnchor::TopRight | CanvasAnchor::CenterRight | CanvasAnchor::BottomRight => {
            x += layer.0 - size.0;
        }
        _ => {}
    }
    match anchor {
        CanvasAnchor::CenterLeft | CanvasAnchor::Center | CanvasAnchor::CenterRight => {
            y += (layer.1 - size.1) * 0.5;
        }
        CanvasAnchor::BottomLeft | CanvasAnchor::BottomCenter | CanvasAnchor::BottomRight => {
            y += layer.1 - size.1;
        }
        _ => {}
    }
    ((x + 0.5).floor(), (y + 0.5).floor())
}

/// How a canvas is identified, how large it is and where it sits (OpenAC
/// `PluginCanvasDescriptor`).
#[derive(Debug, Clone, PartialEq)]
pub struct CanvasDescriptor {
    /// The plugin's own name for it, unique within the plugin.
    pub canvas_id: String,
    /// Width in pixels, at least 1.
    pub width: i32,
    /// Height in pixels, at least 1.
    pub height: i32,
    /// The corner or edge it is measured from.
    pub anchor: CanvasAnchor,
    /// Pixels from the anchor.
    pub offset: (f32, f32),
    /// Shown at once.
    pub start_visible: bool,
    /// Takes pointer input while someone listens.
    pub accepts_pointer_input: bool,
}

/// One canvas as a window draws it.
#[derive(Debug, Clone, PartialEq)]
pub struct CanvasView {
    /// The session's number for it.
    pub id: u32,
    /// Its plugin.
    pub owner: UiOwner,
    /// As registered.
    pub descriptor: CanvasDescriptor,
    /// Shown.
    pub visible: bool,
    /// Where it is measured from now.
    pub anchor: CanvasAnchor,
    /// Its offset now.
    pub offset: (f32, f32),
    /// The plugin listens for pointer events (OpenAC's `PointerHandler`
    /// set).
    pub listening: bool,
    /// What was last painted.
    pub paint: Arc<Vec<PaintCmd>>,
    /// Counts the plugin's `ReleasePointer` calls: a window holding the
    /// pointer for this canvas lets go when it moves, without a cancel.
    pub release_count: u32,
}

impl CanvasView {
    /// Takes the pointer now: it opted in and someone listens.
    #[must_use]
    pub fn takes_input(&self) -> bool {
        self.descriptor.accepts_pointer_input && self.listening
    }
}

/// What a pointer did over a canvas (OpenAC `PluginPointerEventKind`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PointerKind {
    /// A button went down inside it.
    Down,
    /// The held button came up.
    Up,
    /// The pointer moved with the button held.
    Move,
    /// The wheel turned over it.
    Wheel,
    /// A press ended without its up (hidden, removed, taken).
    Cancelled,
    /// The pointer is over it with no button held (plugins built for API
    /// 1.6 or later hear it).
    Hover,
    /// The pointer stopped being over it after a hover (API 1.6 or later).
    Leave,
}

/// Where the window had a canvas when the pointer did something on it: the
/// window follows the plugin's moves a moment late. There is no default: a
/// place the window did not measure is `None`, never a zero-sized layer.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CanvasPlace {
    /// Its top-left corner on the layer.
    pub origin: (f32, f32),
    /// The layer's size.
    pub layer: (f32, f32),
}

/// One pointer event on a canvas, in its own pixels (OpenAC
/// `PluginPointerEvent`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PointerEvent {
    /// What happened.
    pub kind: PointerKind,
    /// Where, canvas pixels (outside it only during a drag).
    pub x: f32,
    /// Where.
    pub y: f32,
    /// 0 none, 1 left, 2 right, 3 middle.
    pub button: u8,
    /// Bits: 1 shift, 2 control, 4 alt.
    pub modifiers: u8,
    /// Wheel notches, positive away from the user.
    pub wheel_delta: i32,
}

/// Where in the world, on the global map grid: east-west and north-south
/// in map units (240 metres, as the in-game location readout), elevation in
/// the same units (OpenAC `PluginNavigationPosition`'s coordinates).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MapPoint {
    /// East-west, growing east.
    pub east_west: f64,
    /// North-south, growing north.
    pub north_south: f64,
    /// Height, in the same units.
    pub elevation: f64,
}

impl MapPoint {
    /// Metres per map unit.
    pub const UNIT_METRES: f64 = 240.0;
    /// The global metre position of map point (0, 0): 101.95 map units in
    /// from the world's south-west corner.
    pub const ORIGIN_METRES: f64 = 127.0 * 192.0 + 84.0;

    /// The landblock (x, y) and the metres inside it, and the height in
    /// metres.
    #[must_use]
    pub fn to_landblock(self) -> Option<(u8, u8, f32, f32, f32)> {
        let gx = self.east_west * Self::UNIT_METRES + Self::ORIGIN_METRES;
        let gy = self.north_south * Self::UNIT_METRES + Self::ORIGIN_METRES;
        if !(gx.is_finite() && gy.is_finite() && self.elevation.is_finite()) {
            return None;
        }
        let bx = (gx / 192.0).floor();
        let by = (gy / 192.0).floor();
        if !(0.0..=255.0).contains(&bx) || !(0.0..=255.0).contains(&by) {
            return None;
        }
        Some((
            bx as u8,
            by as u8,
            (gx - bx * 192.0) as f32,
            (gy - by * 192.0) as f32,
            (self.elevation * Self::UNIT_METRES) as f32,
        ))
    }
}

/// A line of text hung over an object (OpenAC `PluginWorldLabel`).
#[derive(Debug, Clone, PartialEq)]
pub struct WorldLabel {
    /// The object it follows.
    pub object_id: u32,
    /// What it says (one line, at most 128 characters).
    pub text: String,
    /// Red, green, blue, alpha, 0..=1.
    pub color: [f32; 4],
    /// Metres added to the object's height.
    pub height_offset: f32,
    /// Which line over the object, 0 at the head.
    pub line: i32,
    /// Shown up to this many metres away, fading over the last fifth.
    pub max_range: f32,
    /// A dark outline.
    pub outline: bool,
}

/// A straight line in the world (OpenAC `PluginWorldLine`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldLine {
    /// One end.
    pub start: MapPoint,
    /// The other end.
    pub end: MapPoint,
    /// `0xRRGGBB`.
    pub color_rgb: u32,
    /// Kept just above the ground along its length.
    pub follow_terrain: bool,
    /// Thickness, metres.
    pub width_metres: f32,
}

/// A filled triangle in the world, flat between its corners, see-through by
/// its colours' alpha, drawn from both sides and unlit. It does not follow
/// the ground.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldTriangle {
    /// The corners.
    pub corners: [MapPoint; 3],
    /// Each corner's colour, `0xRRGGBBAA`, blended across the face.
    pub colors_rgba: [u32; 3],
}

/// What a world marker is pinned to.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub enum MarkerAnchor {
    /// Nothing: a marker pinned to nothing is dropped.
    #[default]
    None,
    /// An object, by the id the server gave it. While the client does not
    /// hold it the marker is not drawn, and it comes back when it does.
    Object(u32),
    /// A fixed position.
    Position {
        /// Where.
        point: MapPoint,
        /// The position is on the landscape, not inside a building or
        /// dungeon.
        is_outdoor: bool,
    },
}

impl MarkerAnchor {
    /// Whether it pins something: an object with an id, or a position whose
    /// coordinates are all finite.
    #[must_use]
    pub fn is_valid(&self) -> bool {
        match self {
            Self::None => false,
            Self::Object(id) => *id != 0,
            Self::Position { point, .. } => {
                point.east_west.is_finite()
                    && point.north_south.is_finite()
                    && point.elevation.is_finite()
            }
        }
    }
}

/// An image hung in the world at a constant size on the screen. It is drawn
/// after the world, like a label, so walls and hills do not hide it unless
/// it asks with `hidden_by_world`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldIcon {
    /// Over an object, the client lays its icons out in rows above its head
    /// and its labels; at a position, the icon is centred on it.
    pub anchor: MarkerAnchor,
    /// The plugin's own image.
    pub image: u32,
    /// The side of the square the image is fitted into, interface points.
    pub size: f32,
    /// Multiplied into the image, `0xRRGGBBAA`.
    pub tint_rgba: u32,
    /// A thin frame around the image, `0xRRGGBBAA`.
    pub border_rgba: Option<u32>,
    /// Shown up to this many metres away, fading over the last fifth.
    pub max_range: f32,
    /// The whole icon goes while walls, doors, hills or anything else in the
    /// world stands between the camera and its anchor.
    pub hidden_by_world: bool,
}

/// An icon with the plugin it belongs to, whose images are the only ones
/// its image is looked up in.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldIconEntry {
    /// The plugin.
    pub owner: String,
    /// The icon, already checked and with its size in range.
    pub icon: WorldIcon,
}

/// What form a ground shape takes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum GroundShapeKind {
    /// Nothing: a shape of no kind is dropped.
    #[default]
    None,
    /// A band all the way round the anchor, from the radius less the width
    /// out to the radius.
    Ring,
    /// A circle around the anchor, filled from its centre.
    Disc,
    /// Part of a circle: a wedge filled from the centre, or a band like a
    /// ring's.
    Arc,
}

/// A shape lying on the ground around an anchor: a ring under a target, a
/// disc over an area, a wedge in front of a creature. Drawn in the world,
/// so walls and hills hide it, and outdoors it follows the slope of the
/// land.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GroundShape {
    /// Around an object's feet, following it, or around a fixed position.
    pub anchor: MarkerAnchor,
    /// What form it takes.
    pub kind: GroundShapeKind,
    /// The outer radius, metres.
    pub radius: f32,
    /// How wide a ring's or unfilled arc's band is, metres, inward from the
    /// radius.
    pub width: f32,
    /// Where an arc starts, as a compass bearing in degrees (0 north, 90
    /// east); with `faces_object`, from the way the object faces.
    pub start_degrees: f32,
    /// How far round an arc goes, clockwise from its start, degrees.
    pub sweep_degrees: f32,
    /// An arc filled from the centre, a wedge, rather than a band.
    pub filled: bool,
    /// An arc over an object turns with it.
    pub faces_object: bool,
    /// Its colour, `0xRRGGBBAA`; the alpha is how opaque it is.
    pub color_rgba: u32,
}

/// One step of a simulated projectile flight, for drawing (OpenAC
/// `PluginProjectileDebugSample`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProjectileSample {
    /// Where the step ended.
    pub position: MapPoint,
    /// Unobstructed (or on the target).
    pub is_clear: bool,
    /// The probe's radius, metres.
    pub radius: f32,
}

/// One of the client's own windows a plugin may show, hide, toggle or ask
/// about (OpenAC `PluginClientWindow`, same order).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum ClientWindow {
    /// The backpack and equipment.
    Inventory,
    /// Attributes, vitals and skills.
    Character,
    /// Birth, play time, deaths, augmentations.
    CharacterInformation,
    /// The spellbook.
    Spellbook,
    /// The map and housing pages.
    Map,
    /// The settings.
    Options,
    /// Friends, allegiance, fellowship, squelch.
    Social,
    /// The quest journal.
    Journal,
    /// Beneficial effects.
    PositiveEffects,
    /// Harmful effects.
    NegativeEffects,
    /// The connection indicator.
    LinkStatus,
    /// The death penalty.
    Vitae,
    /// The radar.
    Radar,
}

impl ClientWindow {
    /// Every window, by number.
    pub const ALL: [Self; 13] = [
        Self::Inventory,
        Self::Character,
        Self::CharacterInformation,
        Self::Spellbook,
        Self::Map,
        Self::Options,
        Self::Social,
        Self::Journal,
        Self::PositiveEffects,
        Self::NegativeEffects,
        Self::LinkStatus,
        Self::Vitae,
        Self::Radar,
    ];

    /// The window numbered `n`.
    #[must_use]
    pub fn from_index(n: u32) -> Option<Self> {
        Self::ALL.get(usize::try_from(n).ok()?).copied()
    }

    /// Its number.
    #[must_use]
    pub fn index(self) -> usize {
        Self::ALL.iter().position(|w| *w == self).unwrap_or(0)
    }

    /// Its bit in a window set.
    #[must_use]
    pub fn bit(self) -> u16 {
        1 << self.index()
    }
}

/// What a plugin asks of a client window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClientWindowAction {
    /// Show it if hidden, hide it if shown.
    Toggle,
    /// Show it.
    Show,
    /// Hide it.
    Hide,
}

/// A handler's argument, by the handler's kind.
#[derive(Debug, Clone, PartialEq)]
pub enum ActionValue {
    /// `Action` (a click).
    None,
    /// `Action<int>` (a row).
    Int(i32),
    /// `Action<float>` (a slider).
    Float(f32),
    /// `Action<string>` (a field, a menu entry).
    Text(String),
    /// `Action<bool>` (a checkbox, an expander).
    Bool(bool),
    /// `Action<uint>` (a colour picker bound to a `uint`).
    Uint(u32),
    /// `Action<int, bool>` (a sort request: the column, descending).
    IntBool(i32, bool),
    /// `Action<string, bool>` (a tree row's key and its asked-for state).
    TextBool(String, bool),
    /// `Action<IReadOnlyList<float>>` (every column's width).
    FloatList(Vec<f32>),
    /// `Action<IReadOnlyList<int>>` (the selected plugin rows).
    IntList(Vec<i32>),
}

impl ActionValue {
    /// The handler kind that takes this argument.
    #[must_use]
    pub fn kind(&self) -> crate::value::BindingKind {
        use crate::value::BindingKind;
        match self {
            Self::None => BindingKind::Action,
            Self::Int(_) => BindingKind::IntAction,
            Self::Float(_) => BindingKind::FloatAction,
            Self::Text(_) => BindingKind::TextAction,
            Self::Bool(_) => BindingKind::BoolAction,
            Self::Uint(_) => BindingKind::UintAction,
            Self::IntBool(..) => BindingKind::IntBoolAction,
            Self::TextBool(..) => BindingKind::TextBoolAction,
            Self::FloatList(_) => BindingKind::FloatListAction,
            Self::IntList(_) => BindingKind::IntListAction,
        }
    }
}

/// Where an image a plugin holds comes from.
#[derive(Debug, Clone, PartialEq)]
pub enum ImageSource {
    /// A client render surface.
    ClientArt(u32),
    /// A spell's icon (the spell and its icon surface).
    SpellIcon {
        /// The spell.
        spell: u32,
        /// Its icon render surface.
        icon: u32,
    },
    /// A live object's composed icon.
    ObjectIcon(u32),
    /// The plugin's own pixels, 8-bit RGBA rows from the top.
    Rgba(Arc<Vec<u8>>),
    /// An image file the plugin ships, which its markup draws, with its
    /// smaller levels (sampled smoothly, never repeated).
    File(Arc<crate::file_images::FileImage>),
}

/// One image a plugin holds, as a window resolves it.
#[derive(Debug, Clone, PartialEq)]
pub struct ImageView {
    /// The plugin.
    pub owner: String,
    /// The plugin's handle (never reused).
    pub handle: u32,
    /// Width, pixels.
    pub width: u32,
    /// Height, pixels.
    pub height: u32,
    /// What it is.
    pub source: ImageSource,
}

/// One change to plugin UI state, in the order it happened; a window's
/// copy applies them in turn ([`crate::PluginUiView::apply`]).
#[derive(Debug, Clone, PartialEq)]
pub enum UiChange {
    /// Forget everything (the start of a snapshot).
    Clear,
    /// A panel was registered (or is in a snapshot).
    PanelAdded(Box<PanelView>),
    /// A panel was removed.
    PanelRemoved(u32),
    /// Bound values changed.
    PanelValues {
        /// The panel.
        panel: u32,
        /// The new values.
        values: Vec<(String, UiValue)>,
    },
    /// The show or close request changed.
    PanelRequestedVisible {
        /// The panel.
        panel: u32,
        /// Requested shown.
        visible: bool,
    },
    /// A control's text was set.
    ControlLabel {
        /// The panel.
        panel: u32,
        /// The element.
        node: usize,
        /// The text.
        label: String,
    },
    /// A control's visibility was set.
    ControlVisible {
        /// The panel.
        panel: u32,
        /// The element.
        node: usize,
        /// Shown.
        visible: bool,
    },
    /// A canvas was registered (or is in a snapshot).
    CanvasAdded(Box<CanvasView>),
    /// A canvas was removed.
    CanvasRemoved(u32),
    /// A canvas was painted.
    CanvasPaint {
        /// The canvas.
        canvas: u32,
        /// What it shows now.
        paint: Arc<Vec<PaintCmd>>,
    },
    /// A canvas was shown, hidden or moved.
    CanvasPlacement {
        /// The canvas.
        canvas: u32,
        /// Shown.
        visible: bool,
        /// Anchor.
        anchor: CanvasAnchor,
        /// Offset.
        offset: (f32, f32),
    },
    /// Someone started or stopped listening for a canvas's pointer.
    CanvasListening {
        /// The canvas.
        canvas: u32,
        /// Listening.
        listening: bool,
    },
    /// The plugin let go of the pointer its canvas held.
    CanvasReleasePointer(u32),
    /// A plugin took hold of an image.
    ImageAdded(Box<ImageView>),
    /// A plugin window's file icon: the image the session holds for
    /// the path the window draws (`handle` 0: none, or no more).
    FileIcon {
        /// The window.
        panel: u32,
        /// The path as the markup gives it.
        path: String,
        /// The image, by the plugin's handle.
        handle: u32,
    },
    /// A plugin let go of an image for good.
    ImageRemoved {
        /// The plugin.
        owner: String,
        /// Its handle.
        handle: u32,
    },
    /// Every plugin's world labels, merged.
    Labels(Arc<Vec<WorldLabel>>),
    /// Every plugin's world lines, merged.
    Lines(Arc<Vec<WorldLine>>),
    /// Every plugin's world triangles, merged.
    Triangles(Arc<Vec<WorldTriangle>>),
    /// Every plugin's world icons, merged.
    Icons(Arc<Vec<WorldIconEntry>>),
    /// Every plugin's ground shapes, merged.
    Shapes(Arc<Vec<GroundShape>>),
    /// Projectile markers to show for a moment.
    ProjectileDebug(Arc<Vec<ProjectileSample>>),
    /// A plugin asks the window to show, hide or toggle a client window.
    ClientWindow {
        /// Which.
        window: ClientWindow,
        /// What.
        action: ClientWindowAction,
    },
    /// The bound plugin hotkeys, in registration order (all of them, each
    /// time the table changes).
    Hotkeys(Arc<Vec<crate::hotkeys::BoundHotkey>>),
    /// A plugin holds a font, or its sharper bake changed.
    FontAdded(Box<crate::fonts::FontView>),
    /// A plugin let go of a font.
    FontRemoved {
        /// The plugin.
        owner: String,
        /// The handle.
        handle: u32,
    },
    /// A plugin asked the player a question (or a snapshot carries one).
    DialogAdded(Box<DialogView>),
    /// A dialog was answered or withdrawn: it goes.
    DialogRemoved(u32),
    /// A plugin window was shown modal over another (`owner`), or (none)
    /// stopped being modal.
    Modal {
        /// The modal window.
        panel: u32,
        /// The window it blocks.
        owner: Option<u32>,
    },
}

/// A plugin's question in a dialog box (OpenAC `PluginDialog`), as a window
/// shows it: the dialogs of one owner window (and the ownerless ones)
/// queue and show one at a time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DialogView {
    /// The session's number for it (the plugin's handle).
    pub handle: u32,
    /// Its plugin.
    pub owner: UiOwner,
    /// The caption.
    pub title: String,
    /// The question.
    pub message: String,
    /// The buttons left to right; the last is the cancel button.
    pub buttons: Vec<String>,
    /// The plugin window it is centred on and blocks (none: the screen).
    pub owner_panel: Option<u32>,
}

impl DialogView {
    /// The cancel button's index (Escape, the close button).
    #[must_use]
    pub fn cancel_index(&self) -> i32 {
        i32::try_from(self.buttons.len()).unwrap_or(1) - 1
    }
}

/// What a window tells the session's plugin UI.
#[derive(Debug, Clone, PartialEq)]
pub enum UiInput {
    /// A control's handler fired.
    Action {
        /// The panel.
        panel: u32,
        /// The handler's binding name.
        handler: String,
        /// Its argument.
        value: ActionValue,
    },
    /// The player opened (`true`) or closed (`false`) a plugin window
    /// (dock slot, close or minimize button).
    PanelShown {
        /// The panel.
        panel: u32,
        /// Opened.
        shown: bool,
    },
    /// Pointer input on a canvas.
    Pointer {
        /// The canvas.
        canvas: u32,
        /// What happened.
        event: PointerEvent,
        /// Where the window had the canvas when it happened; `None` when
        /// that is not known (input a test or script sends), and the event
        /// is heard as sent.
        place: Option<CanvasPlace>,
    },
    /// Which client windows the window has, and which are shown (bit sets
    /// by [`ClientWindow::bit`]); sent at attach and whenever they change.
    ClientWindows {
        /// Mounted.
        mounted: u16,
        /// Shown.
        visible: u16,
    },
    /// The chords the window's own key map uses (sent at attach and when
    /// the player changes the bindings): plugin hotkeys bind around them.
    ClientChords(Vec<crate::hotkeys::Chord>),
    /// The player pressed a bound plugin hotkey.
    Hotkey {
        /// The plugin.
        owner: String,
        /// Its id for the hotkey.
        id: String,
    },
    /// The window's key map fired an input action (not a held action's
    /// per-tick repeat).
    InputAction {
        /// The action's stored name.
        action: String,
        /// How it fired (the activation's stored name).
        activation: String,
    },
    /// The plugin theme the player saved (sent at attach) or just picked.
    Theme(crate::theme::ThemeKind),
    /// The file icons a plugin window draws now: those of the elements it
    /// draws (none in a hidden window, a closed tab's page or a list row
    /// scrolled away),
    /// sent when they change: the session holds each while it is drawn and
    /// lets go of one not drawn for a few seconds.
    FileIconsShown {
        /// The window.
        panel: u32,
        /// The paths, as its markup gives them.
        paths: Vec<String>,
    },
    /// How many framebuffer pixels a unit the window's display has, sent when
    /// it changes.
    DisplayDensity(f32),
    /// The player answered a dialog (the button's index).
    DialogAnswered {
        /// The dialog.
        handle: u32,
        /// The button.
        button: i32,
    },
}

/// Something a plugin hears from its UI.
#[derive(Debug, Clone, PartialEq)]
pub enum UiEvent {
    /// A handler of one of its panels fired.
    Action {
        /// The panel.
        panel: u32,
        /// The handler's binding name.
        handler: String,
        /// Its argument.
        value: ActionValue,
    },
    /// Pointer input on one of its canvases.
    Pointer {
        /// The canvas.
        canvas: u32,
        /// What happened.
        event: PointerEvent,
        /// Where the window had the canvas when it happened, if known.
        place: Option<CanvasPlace>,
    },
    /// One of its hotkeys was pressed (its own id).
    Hotkey(String),
    /// The client's key map fired an input action it listens for.
    InputAction {
        /// The action's stored name.
        action: String,
        /// How it fired.
        activation: String,
    },
    /// One of its dialogs was answered (OpenAC `Confirm`'s `onResult`).
    DialogAnswered {
        /// The dialog's handle.
        handle: u32,
        /// The button picked.
        button: i32,
    },
}
