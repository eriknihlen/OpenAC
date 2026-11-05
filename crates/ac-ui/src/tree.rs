//! Elements, the widget trait, and the arena that holds them.
//!
//! An element is common data ([`Element`]: geometry, flags, tree links) plus
//! behaviour ([`Widget`], a boxed trait object). Elements live in an arena
//! inside [`UiRoot`](crate::UiRoot) and are named by [`ElementId`], a
//! generational index: an id whose element was destroyed never names a
//! later element.
//!
//! While a widget handles a call it is taken out of its slot, and the call
//! gets the whole root (`&mut UiRoot`) and its own id, so it can change its
//! element, its children, focus or capture. A call that reaches a widget
//! already running (re-entrancy on the same element) is skipped.

use std::any::Any;
use std::collections::HashMap;
use std::fmt;

use crate::draw::{RenderContext, TextureRef};
use crate::element_info::CursorMedia;
use crate::event::{DragPayload, UiEvent};
use crate::geometry::{AnchorEdges, ResizeEdges};
use crate::layout_policy::LayoutPolicy;
use crate::root::UiRoot;
use crate::text::Scrollable;

/// Names one element. Stays valid until the element is destroyed; after
/// that it names nothing (never a later element).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ElementId {
    pub(crate) index: u32,
    pub(crate) generation: u32,
}

/// What a dragged item looks like under the cursor: a texture and its size.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DragGhost {
    /// The texture.
    pub texture: TextureRef,
    /// Width in pixels.
    pub width: i32,
    /// Height in pixels.
    pub height: i32,
}

/// Authored values the importer copies from the layout.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Authored {
    /// Hidden on creation.
    pub invisible: bool,
    /// Tooltips enabled.
    pub tooltip_enabled: bool,
    /// Tooltip text, resolved.
    pub tooltip_text: Option<String>,
    /// Tooltip popup root element.
    pub tooltip_root_element_id: u32,
    /// Tooltip popup layout (0: the element's own layout).
    pub tooltip_layout_did: u32,
    /// Tooltip text element in the popup.
    pub tooltip_text_child_element_id: u32,
    /// Tooltip delay override, seconds.
    pub tooltip_delay_seconds: Option<f32>,
    /// Largest width.
    pub resize_max_width: Option<i32>,
    /// Smallest width.
    pub resize_min_width: Option<i32>,
    /// Largest height.
    pub resize_max_height: Option<i32>,
    /// Smallest height.
    pub resize_min_height: Option<i32>,
}

/// An interactive mask over an image map: an element whose pointer
/// messages go to a handler that answers by position. Every method is
/// optional; a handler that does not implement one leaves that message to
/// the element.
pub trait RegionHandler: Any + fmt::Debug {
    /// A click at element-local (`x`, `y`); true when handled.
    fn clicked(&mut self, _x: i32, _y: i32) -> bool {
        false
    }
    /// True when clicks are handled (the element then claims the press).
    fn handles_click(&self) -> bool {
        false
    }
    /// A right click; true when handled.
    fn right_clicked(&mut self, _x: i32, _y: i32) -> bool {
        false
    }
    /// True when this region can start drags.
    fn is_drag_source(&self) -> bool {
        false
    }
    /// What a drag lifted at the press point carries.
    fn drag_payload_at(&mut self, _x: i32, _y: i32) -> Option<DragPayload> {
        None
    }
    /// The ghost of a drag lifted at the press point.
    fn drag_ghost_at(&mut self, _x: i32, _y: i32) -> Option<DragGhost> {
        None
    }
    /// A drag entered; true when handled.
    fn drag_over_at(&mut self, _payload: Option<&DragPayload>, _x: i32, _y: i32) -> bool {
        false
    }
    /// A drag left; true when handled.
    fn drag_left(&mut self) -> bool {
        false
    }
    /// A drag was released here; true when handled.
    fn drop_released_at(&mut self, _payload: Option<&DragPayload>, _x: i32, _y: i32) -> bool {
        false
    }
}

/// An image-map mask on an element and the point of the last press on it.
#[derive(Debug)]
pub struct PointerRegion {
    /// Answers the pointer messages.
    pub handler: Box<dyn RegionHandler>,
    /// Element-local press point.
    pub press: (i32, i32),
}

impl PointerRegion {
    /// A region with `handler`.
    pub fn new(handler: impl RegionHandler) -> Self {
        Self {
            handler: Box::new(handler),
            press: (0, 0),
        }
    }
}

/// The anchor margins captured from an element's first layout pass, with
/// the anchors they were captured for.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct AnchorCapture {
    pub anchors: AnchorEdges,
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    pub width: f32,
    pub height: f32,
}

/// The data every element has: geometry, flags and authored values. Tree
/// links (parent, children, event id) live in the root.
///
/// `visible`, `enabled`, `z_order` and `anchors` may be set when building
/// an element; once it is in the tree, change them through
/// [`UiRoot::set_visible`], [`UiRoot::set_enabled`], [`UiRoot::set_z_order`]
/// and [`UiRoot::set_anchors`], so focus, capture, windows and layout
/// follow.
#[derive(Debug)]
pub struct Element {
    /// Dat element id of an imported element (0 for synthesized ones).
    pub dat_element_id: u32,
    /// Name for debugging and lookup.
    pub name: Option<String>,
    /// Authored values from the layout.
    pub authored: Authored,
    /// Layout the element was imported from.
    pub source_layout_did: u32,
    /// Cursor per state name.
    pub state_cursors: HashMap<String, CursorMedia>,
    /// Left in the parent, pixels.
    pub left: f32,
    /// Top in the parent, pixels.
    pub top: f32,
    /// Width, pixels.
    pub width: f32,
    /// Height, pixels.
    pub height: f32,
    /// Shown (ancestors may still hide it).
    pub visible: bool,
    /// Takes input.
    pub enabled: bool,
    /// Hit testing passes through to what is below.
    pub click_through: bool,
    /// A left press here takes keyboard focus.
    pub accepts_focus: bool,
    /// A left press takes keyboard focus when it accepts focus (a plugin's
    /// button, tab or toggle takes it only by Tab, so a click leaves the
    /// game's keys alone).
    pub focus_on_mouse_click: bool,
    /// Tab moves keyboard focus to and from it within its window (plugin
    /// markup controls).
    pub tab_stop: bool,
    /// A text-entry control: while focused, global hotkeys are not offered
    /// keys it does not handle, and typed characters reach it.
    pub is_edit_control: bool,
    /// Order among siblings: higher draws on top and is hit first.
    pub z_order: i32,
    /// Opacity multiplier, 0..=1.
    pub opacity: f32,
    /// A window that moves when its body is dragged.
    pub draggable: bool,
    /// Dragging this element moves its top-level window.
    pub window_move_handle: bool,
    /// Resizing keeps the window inside its parent.
    pub constrain_resize_to_parent: bool,
    /// A window that resizes from its border.
    pub resizable: bool,
    /// Owns drags that start inside it (no window move).
    pub captures_pointer_drag: bool,
    /// Smallest width while resizing.
    pub min_width: f32,
    /// Smallest height while resizing.
    pub min_height: f32,
    /// Largest width while resizing.
    pub max_width: f32,
    /// Largest height while resizing.
    pub max_height: f32,
    /// Width may change by resizing.
    pub resize_x: bool,
    /// Height may change by resizing.
    pub resize_y: bool,
    /// Which flat border runs start a resize (corners always offer their
    /// two sides, filtered by `resize_x`/`resize_y`).
    pub resizable_edges: ResizeEdges,
    /// Edges kept at their distance from the parent's when it resizes
    /// (used when there is no layout policy).
    pub anchors: AnchorEdges,
    /// The imported edge-mode layout; when set it replaces the anchors.
    pub layout_policy: Option<LayoutPolicy>,
    /// Tooltip text set at run time (wins over the authored text).
    pub runtime_tooltip_text: Option<String>,
    /// Image-map mask.
    pub pointer_region: Option<PointerRegion>,
    /// Its right-click menu element: a right click the element leaves
    /// unhandled is offered to it.
    pub context_menu: Option<ElementId>,
    /// A child that stays put while its parent scrolls (a scrollbar): it is
    /// neither moved by nor clipped to the parent's content viewport.
    pub scroll_chrome: bool,
    /// The size of the part of this element its scrolled children are seen
    /// through, from its top-left corner; `None` for an element that does
    /// not scroll. While set, every child but the scroll chrome is clipped to
    /// it and moved by [`Self::content_offset`], in drawing, hit-testing and
    /// screen positions alike.
    pub content_viewport: Option<(f32, f32)>,
    /// How far this element's content is scrolled, pixels (used only while
    /// [`Self::content_viewport`] is set).
    pub content_offset: (f32, f32),
}

impl Default for Element {
    fn default() -> Self {
        Self {
            dat_element_id: 0,
            name: None,
            authored: Authored::default(),
            source_layout_did: 0,
            state_cursors: HashMap::new(),
            left: 0.0,
            top: 0.0,
            width: 0.0,
            height: 0.0,
            visible: true,
            enabled: true,
            click_through: false,
            accepts_focus: false,
            focus_on_mouse_click: true,
            tab_stop: false,
            is_edit_control: false,
            z_order: 0,
            opacity: 1.0,
            draggable: false,
            window_move_handle: false,
            constrain_resize_to_parent: false,
            resizable: false,
            captures_pointer_drag: false,
            min_width: 40.0,
            min_height: 40.0,
            max_width: f32::MAX,
            max_height: f32::MAX,
            resize_x: true,
            resize_y: true,
            resizable_edges: ResizeEdges::ALL,
            anchors: AnchorEdges::TOP_LEFT,
            layout_policy: None,
            runtime_tooltip_text: None,
            pointer_region: None,
            context_menu: None,
            scroll_chrome: false,
            content_viewport: None,
            content_offset: (0.0, 0.0),
        }
    }
}

impl Element {
    /// An element at (`left`, `top`) of the given size.
    #[must_use]
    pub fn at(left: f32, top: f32, width: f32, height: f32) -> Self {
        Self {
            left,
            top,
            width,
            height,
            ..Self::default()
        }
    }

    /// This element, initially shown or hidden.
    #[must_use]
    pub fn with_visible(mut self, visible: bool) -> Self {
        self.visible = visible;
        self
    }

    /// This element with an initial z-order.
    #[must_use]
    pub fn with_z_order(mut self, z: i32) -> Self {
        self.z_order = z;
        self
    }

    /// This element with compatibility anchors (dropping any layout policy).
    #[must_use]
    pub fn with_anchors(mut self, anchors: AnchorEdges) -> Self {
        self.set_anchors(anchors);
        self
    }

    /// True when shown (ancestors may still hide it).
    #[must_use]
    pub fn visible(&self) -> bool {
        self.visible
    }

    /// True when it takes input.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// Order among siblings.
    #[must_use]
    pub fn z_order(&self) -> i32 {
        self.z_order
    }

    /// Set the compatibility anchors, dropping any imported layout policy.
    /// (For an element in the tree, [`UiRoot::set_anchors`] also forgets
    /// the captured margins.)
    pub fn set_anchors(&mut self, anchors: AnchorEdges) {
        self.anchors = anchors;
        self.layout_policy = None;
    }

    /// The cursor of state `name`: that state's, or (when allowed) the
    /// default state's, or Normal's.
    #[must_use]
    pub fn cursor_for_state(&self, name: &str, allow_fallback: bool) -> Option<CursorMedia> {
        if !name.is_empty()
            && let Some(c) = self.state_cursors.get(name).filter(|c| c.is_valid())
        {
            return Some(*c);
        }
        if !allow_fallback {
            return None;
        }
        self.state_cursors
            .get("")
            .filter(|c| c.is_valid())
            .or_else(|| self.state_cursors.get("Normal").filter(|c| c.is_valid()))
            .copied()
    }

    /// Replace the state cursors, keeping only valid ones.
    pub fn set_state_cursors(&mut self, cursors: &HashMap<String, CursorMedia>) {
        self.state_cursors = cursors
            .iter()
            .filter(|(_, c)| c.is_valid())
            .map(|(k, c)| (k.clone(), *c))
            .collect();
    }

    /// True when local (`x`, `y`) is inside the element.
    #[must_use]
    pub fn contains_local(&self, x: f32, y: f32) -> bool {
        x >= 0.0 && x < self.width && y >= 0.0 && y < self.height
    }
}

/// `Any` access for widgets, implemented for every sized `'static` type.
pub trait AsAny: Any {
    /// As `&dyn Any`.
    fn as_any(&self) -> &dyn Any;
    /// As `&mut dyn Any`.
    fn as_any_mut(&mut self) -> &mut dyn Any;
    /// The concrete type's name, without its module path (diagnostics:
    /// the automation probe's dump).
    fn type_name(&self) -> &'static str;
}

impl<T: Any> AsAny for T {
    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
    fn type_name(&self) -> &'static str {
        let full = std::any::type_name::<T>();
        let base = full.split('<').next().unwrap_or(full);
        base.rsplit("::").next().unwrap_or(base)
    }
}

/// The behaviour of an element. Every method has a default; a widget
/// overrides what it needs. Methods that receive `ui` and `me` may change
/// anything in the root, including their own element (`ui.element_mut(me)`).
pub trait Widget: AsAny + fmt::Debug {
    /// Handle an event aimed at this element or bubbled from a descendant
    /// (`e.target` says which); true stops the bubbling. The default offers
    /// it to the element's image-map region.
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        ui.dispatch_to_pointer_region(me, e)
    }

    /// Draw this element (origin at its top-left), before its children.
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, _rc: &mut RenderContext<'_>) {}

    /// Draw after the children.
    fn draw_after_children(
        &mut self,
        _ui: &mut UiRoot,
        _me: ElementId,
        _rc: &mut RenderContext<'_>,
    ) {
    }

    /// Draw into the overlay layer (popups that escape their window).
    fn draw_overlay(&mut self, _ui: &mut UiRoot, _me: ElementId, _rc: &mut RenderContext<'_>) {}

    /// Put the children where they belong for this element's current size,
    /// just before they are drawn; false leaves it to their anchors (a flex
    /// container or a scrolling group lays them out itself).
    fn layout_children(&mut self, _ui: &mut UiRoot, _me: ElementId) -> bool {
        false
    }

    /// Scroll just far enough to show `target`, a descendant that took
    /// keyboard focus (a scrolling group).
    fn reveal(&mut self, _ui: &mut UiRoot, _me: ElementId, _target: ElementId) {}

    /// Per-frame update, visible elements only.
    fn tick(&mut self, _ui: &mut UiRoot, _me: ElementId, _dt: f64) {}

    /// The UI-wide time pulse, every element, every frame (caret blink,
    /// hot-click repeat).
    fn global_time(&mut self, _ui: &mut UiRoot, _me: ElementId, _now_seconds: f64) {}

    /// The element clips its children to its rectangle (and is hit only
    /// inside it).
    fn clips_children(&self) -> bool {
        true
    }

    /// Overlay drawing ignores the ancestors' clip.
    fn expands_clip_for_popup(&self) -> bool {
        false
    }

    /// True when local (`x`, `y`) hits the element itself.
    fn hit_test(&self, element: &Element, x: f32, y: f32) -> bool {
        element.contains_local(x, y)
    }

    /// The element handles clicks (a press on it inside a draggable window
    /// does not move the window).
    fn handles_click(&self, element: &Element) -> bool {
        element
            .pointer_region
            .as_ref()
            .is_some_and(|r| r.handler.handles_click())
    }

    /// Pointer moves reach the element while it is hovered (not only while
    /// it holds capture).
    fn receives_hover_mouse_move(&self) -> bool {
        false
    }

    /// The element stays hovered while the pointer rests on it, past the
    /// time a tooltip shows (it draws its own label, which is not a timed
    /// tooltip).
    fn hover_outlasts_tooltip(&self) -> bool {
        false
    }

    /// The element can start a drag.
    fn is_drag_source(&self, element: &Element) -> bool {
        element
            .pointer_region
            .as_ref()
            .is_some_and(|r| r.handler.is_drag_source())
    }

    /// What a drag from this element carries; `None` cancels the drag.
    fn drag_payload(&mut self, ui: &mut UiRoot, me: ElementId) -> Option<DragPayload> {
        let region = ui.element_mut(me)?.pointer_region.as_mut()?;
        let (x, y) = region.press;
        region.handler.drag_payload_at(x, y)
    }

    /// The ghost a drag from this element shows.
    fn drag_ghost(&mut self, ui: &mut UiRoot, me: ElementId) -> Option<DragGhost> {
        let region = ui.element_mut(me)?.pointer_region.as_mut()?;
        let (x, y) = region.press;
        region.handler.drag_ghost_at(x, y)
    }

    /// A drag from this element started (`true`) or ended (`false`).
    fn set_drag_source_active(
        &mut self,
        _ui: &mut UiRoot,
        _me: ElementId,
        _active: bool,
        _payload: Option<&DragPayload>,
    ) {
    }

    /// Run-time tooltip text (whitespace-only counts as none).
    fn tooltip_text(&self, element: &Element) -> Option<String> {
        element
            .runtime_tooltip_text
            .as_ref()
            .filter(|t| !t.trim().is_empty())
            .cloned()
    }

    /// The importer should not build this element's dat children (the
    /// widget draws them itself).
    fn consumes_dat_children(&self) -> bool {
        false
    }

    /// Which dat children the importer builds: all, or none when the
    /// widget consumes them.
    fn child_import(&self) -> crate::widgets::ChildImport {
        if self.consumes_dat_children() {
            crate::widgets::ChildImport::None
        } else {
            crate::widgets::ChildImport::All
        }
    }

    /// The element's enabled flag changed.
    fn on_enabled_changed(&mut self, _ui: &mut UiRoot, _me: ElementId) {}

    /// Name of the state whose cursor applies.
    fn active_cursor_state_name(&self) -> &str {
        ""
    }

    /// The active retail state id, for widgets driven by dat states.
    fn retail_state(&self) -> Option<u32> {
        None
    }

    /// Enter retail state `state`; false when the widget has no such state.
    fn set_retail_state(&mut self, _ui: &mut UiRoot, _me: ElementId, _state: u32) -> bool {
        false
    }

    /// The importer attached the element's children.
    fn on_children_attached(&mut self, _ui: &mut UiRoot, _me: ElementId) {}

    /// A popup this element owns was dismissed by a press outside it.
    fn on_popup_dismissed(&mut self, _ui: &mut UiRoot, _me: ElementId, _popup: ElementId) {}

    /// For a resize grip: the window edges it drags.
    fn resize_grip_edges(&self) -> Option<ResizeEdges> {
        None
    }

    /// For an element that changes its window's size itself: the edges whose
    /// resize cursor shows over it and while it holds the pointer. The window
    /// is left alone: a press on it does not start a resize.
    fn resize_cursor_edges(&self) -> Option<ResizeEdges> {
        None
    }

    /// The vertical scroll model this widget owns (a text's transcript, a
    /// list's rows), which a linked scrollbar reads and drives.
    fn scroll_model(&mut self) -> Option<&mut Scrollable> {
        None
    }

    /// Another element whose scroll model stands for this widget's (a list
    /// box scrolls through its inner viewport).
    fn scroll_model_element(&self) -> Option<ElementId> {
        None
    }

    /// An item cell with nothing in it (the empty tail an item list trims).
    fn is_empty_slot(&self) -> bool {
        false
    }

    /// The object this element stands for (the toolbar's backpack is the
    /// player), for the cursor's "found" form; 0 for none.
    fn represented_object(&self) -> u32 {
        0
    }
}

/// A widget with no behaviour: a plain container.
#[derive(Debug, Clone, Copy, Default)]
pub struct Container;

impl Widget for Container {}

/// The root's own widget: draws nothing and does not clip.
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct RootWidget;

impl Widget for RootWidget {
    fn clips_children(&self) -> bool {
        false
    }
}

/// Tree bookkeeping of one element, kept by the root.
#[derive(Debug, Default)]
pub(crate) struct Links {
    pub event_id: u32,
    pub parent: Option<ElementId>,
    pub children: Vec<ElementId>,
    pub anchor_capture: Option<AnchorCapture>,
}

pub(crate) struct Node {
    pub element: Element,
    pub widget: Option<Box<dyn Widget>>,
    pub links: Links,
}

impl fmt::Debug for Node {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Node")
            .field("element", &self.element)
            .field("widget", &self.widget)
            .field("links", &self.links)
            .finish()
    }
}

#[derive(Debug, Default)]
pub(crate) struct Slot {
    pub generation: u32,
    pub node: Option<Node>,
}
