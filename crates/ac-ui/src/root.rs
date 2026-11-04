//! The UI root: the element tree, input routing (hit testing, bubbling,
//! focus, capture, hover and tooltip timing, double clicks, drag and drop,
//! window move and resize, popups, modal), the per-frame tick and the draw
//! walk.
//!
//! The root never calls out. Whatever the rest of the client must hear
//! (input that fell through to the world, window events, widget messages,
//! tooltip show/hide) is queued as [`UiOutput`] and drained by the host.

use std::collections::HashMap;
use std::sync::Arc;

use crate::draw::{Color, DrawList, RenderContext, SpriteSizes};
use crate::event::{DragPayload, Key, Modifiers, MouseButton, UiEvent, kind};
use crate::font::SharpFonts;
use crate::geometry::{
    AnchorEdges, PixelRect, Rect, ResizeEdges, clamp, compute_anchored_rect, resize_rect,
};
use crate::tree::{
    AnchorCapture, DragGhost, Element, ElementId, Links, Node, RootWidget, Slot, Widget,
};
use crate::windows::{WindowEvent, WindowManager};

/// Pixels the pointer may travel before a press becomes a drag (and that a
/// second click may be off the first to count as a double click).
pub const DRAG_DISTANCE_THRESHOLD: i32 = 3;
/// Milliseconds within which a second click is a double click.
pub const DOUBLE_CLICK_DELAY_MS: i64 = 500;
/// Pixels from a window edge where a press starts a resize.
pub const RESIZE_GRIP: i32 = 5;
/// Border band of a draggable window that shows the move cursor.
pub const MOVE_BORDER_BAND: i32 = 8;
/// First event id handed to an attached element.
const FIRST_EVENT_ID: u32 = 0x1000_0001;

/// What a text field asks the owner of its sent lines (OpenAC
/// `IChatEntryHistory`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecallRequest {
    /// Show the line sent before the one shown (the newest at first).
    Previous,
    /// Show the line sent after the one shown (past the newest, nothing).
    Next,
    /// Stop walking through the lines (the player edited).
    Stop,
}

/// A message a widget sends to whoever controls it.
#[derive(Debug, Clone)]
pub enum ElementMessage {
    /// A click (or a hot-click repeat) at element-local coordinates.
    Clicked {
        /// X.
        x: i32,
        /// Y.
        y: i32,
    },
    /// A double click.
    DoubleClicked,
    /// A right click.
    RightClicked,
    /// The button went down on the element.
    Pressed,
    /// The button came up after a press on the element.
    Released,
    /// An item was dropped on the element.
    ItemDropped(crate::event::ItemDragPayload),
    /// A clickable piece of text was clicked.
    TextClicked(crate::text::Pos),
    /// A dialog asks to be cancelled (Escape).
    Cancel,
    /// A text field's line was submitted (Enter or a send button).
    Submitted(String),
    /// A text field's text changed (typing, paste, recall).
    TextChanged(String),
    /// A text field that reports it lost keyboard focus, with its text then.
    FocusLost(String),
    /// A text field whose sent lines live elsewhere
    /// ([`crate::widgets::Field::shared_history`]) asks their owner to
    /// recall one or to stop recalling.
    Recall(RecallRequest),
    /// A read-only text field was clicked.
    ReadOnlyClicked,
    /// A value slider moved to this position, 0..=1.
    ScalarChanged(f32),
    /// A scrollbar or slider drag ended.
    DragCompleted,
    /// A drop-down menu is opening (rebuild its items now).
    MenuOpening,
    /// A drop-down menu entry was picked, by its payload.
    MenuSelected(u64),
    /// A tab panel switched pages (dat element ids, 0 for none).
    TabChanged {
        /// The page shown before.
        previous: u32,
        /// The page shown now.
        page: u32,
    },
    /// The object under the pointer changed (radar hover).
    HoveredObjectChanged(Option<u32>),
    /// An object was picked (radar click).
    ObjectSelected(u32),
    /// A viewport was clicked at element-local coordinates.
    ViewportClicked {
        /// X.
        x: i32,
        /// Y.
        y: i32,
    },
    /// A checkbox bitfield's value changed.
    BitfieldChanged {
        /// Low 64 bits.
        low: u64,
        /// High 64 bits.
        high: u64,
    },
    /// Something dropped a non-item payload on an item list.
    CatalogDropped {
        /// What was dropped.
        payload: DragPayload,
        /// Element-local x.
        x: i32,
        /// Element-local y.
        y: i32,
    },
    /// A drag of an item was lifted from an item slot.
    ItemDragLifted(crate::event::ItemDragPayload),
    /// A drag of an item entered an item slot: the owner answers with
    /// [`crate::widgets::ItemSlot::set_drag_acceptance`].
    ItemDragEntered(crate::event::ItemDragPayload),
    /// A drag of a catalog entry (a spell) was lifted from a catalog slot.
    CatalogDragLifted(DragPayload),
    /// The mouse wheel turned over an element whose owner scrolls it
    /// (notches, positive up).
    Wheel(i32),
    /// A list's button cell was clicked: the left button went down and
    /// came up over the same button, the rows unmoved in between.
    ListButtonClicked {
        /// The button's column.
        column: usize,
        /// Its row.
        row: usize,
    },
}

/// What the UI tells the rest of the client.
#[derive(Debug, Clone)]
pub enum UiOutput {
    /// A mouse button went down or up over no UI element (or up with no
    /// capture): it belongs to the world.
    WorldMouse {
        /// The button.
        button: MouseButton,
        /// Down (true) or up.
        down: bool,
        /// Canvas x.
        x: i32,
        /// Canvas y.
        y: i32,
        /// Button flags from the window.
        flags: u32,
    },
    /// The pointer moved over no captured element.
    WorldMouseMove {
        /// Canvas x.
        x: i32,
        /// Canvas y.
        y: i32,
    },
    /// The wheel turned over no UI element.
    WorldScroll {
        /// Wheel delta (positive up).
        dy: i32,
    },
    /// A key no UI element handled.
    WorldKey {
        /// The key.
        key: Key,
        /// Modifiers.
        modifiers: Modifiers,
    },
    /// A left press on the world (over no UI element) moved past the drag
    /// distance: the window may lift what was under the press point with
    /// [`UiRoot::begin_world_drag`].
    WorldDragStart {
        /// Canvas x of the press.
        x: i32,
        /// Canvas y of the press.
        y: i32,
    },
    /// A drag was released over no UI element.
    DragReleasedOutsideUi {
        /// What was dragged.
        payload: DragPayload,
        /// Canvas x.
        x: i32,
        /// Canvas y.
        y: i32,
    },
    /// The hover delay passed on an element: show its tooltip.
    TooltipShow(ElementId),
    /// The tooltip of an element should go.
    TooltipHide(ElementId),
    /// Keyboard focus moved.
    FocusChanged {
        /// Previous owner.
        old: Option<ElementId>,
        /// New owner.
        new: Option<ElementId>,
    },
    /// Pointer capture moved.
    CaptureChanged {
        /// Previous owner.
        old: Option<ElementId>,
        /// New owner.
        new: Option<ElementId>,
    },
    /// The UI lock changed.
    UiLockChanged(bool),
    /// Something happened to a registered window.
    Window {
        /// Window name.
        name: String,
        /// What.
        event: WindowEvent,
    },
    /// A widget's message.
    Element {
        /// The widget's element.
        element: ElementId,
        /// The message.
        message: ElementMessage,
    },
    /// Copy this text to the system clipboard.
    SetClipboard(String),
    /// A text field asks for the clipboard (Ctrl+V): read it and hand it
    /// back with [`UiRoot::paste`].
    PasteRequest(ElementId),
    /// A panel controller's command for the rest of the client.
    Panel(crate::panels::PanelOutput),
}

#[derive(Debug, Clone, Copy)]
struct ResizeDrag {
    target: ElementId,
    edges: ResizeEdges,
    start: Rect,
    mouse: (i32, i32),
}

#[derive(Debug, Clone, Copy)]
struct WindowDrag {
    target: ElementId,
    offset: (i32, i32),
}

/// The UI root. See the module documentation.
#[derive(Debug)]
pub struct UiRoot {
    slots: Vec<Slot>,
    free: Vec<u32>,
    root: ElementId,
    next_event_id: u32,

    mouse: (i32, i32),
    left_down: bool,
    right_down: bool,
    middle_down: bool,
    keyboard_focus: Option<ElementId>,
    suppressed_key: Option<Key>,
    suppressed_char_tail_pending: bool,
    /// The text field Tab or Enter focuses when nothing has focus.
    pub default_text_input: Option<ElementId>,
    /// While set, only this element's subtree receives the pointer.
    pub modal: Option<ElementId>,
    captured: Option<ElementId>,
    ui_locked: bool,

    drag_source: Option<ElementId>,
    drag_payload: Option<DragPayload>,
    drag_ghost: Option<DragGhost>,
    last_drag_hover: Option<ElementId>,
    press: (i32, i32),
    drag_candidate: bool,
    /// A left press on the world that may still become a drag.
    world_drag_candidate: bool,
    window_drag: Option<WindowDrag>,
    last_click: Option<(ElementId, i64, i32, i32)>,
    resize: Option<ResizeDrag>,

    hover: Option<ElementId>,
    hover_started_ms: i64,
    /// Hover delay before a tooltip, milliseconds (retail 250).
    pub tooltip_delay_ms: i64,
    /// How long a tooltip stays, milliseconds (retail 10 000).
    pub tooltip_duration_ms: i64,
    tooltip_fired: bool,
    tooltip_shown_ms: i64,
    now_ms: i64,
    last_mouse_move_ms: i64,
    /// The modifier keys held, for the pointer's events.
    modifiers: Modifiers,

    active_popup: Option<(ElementId, ElementId)>,
    fixed_canvases: Vec<(u64, (f32, f32))>,
    fixed_canvas: Option<(f32, f32)>,
    media_seconds: f64,
    pixels_per_unit: f32,
    sharp_fonts: Option<Arc<SharpFonts>>,

    pub(crate) windows: WindowManager,
    output: Vec<UiOutput>,
    /// Window-modal dialogs: each blocked top-level window with what blocks
    /// it, newest last.
    window_blocks: HashMap<ElementId, Vec<ElementId>>,
    /// Buttons whose press a block swallowed and whose release is due.
    swallowed_releases: Vec<MouseButton>,
    /// Blockers flashing after a press on what they block, seconds left.
    flashing: HashMap<ElementId, f32>,
}

impl Default for UiRoot {
    fn default() -> Self {
        Self::new()
    }
}

/// A fixed canvas was declared with a size that disagrees with another
/// active declaration.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CanvasConflict {
    /// The size already declared.
    pub declared: (f32, f32),
    /// The size asked for.
    pub requested: (f32, f32),
}

impl UiRoot {
    /// An empty root (screen size 0 until the first draw or
    /// [`UiRoot::set_size`]).
    #[must_use]
    pub fn new() -> Self {
        let root_node = Node {
            element: Element::default(),
            widget: Some(Box::new(RootWidget)),
            links: Links::default(),
        };
        Self {
            slots: vec![Slot {
                generation: 0,
                node: Some(root_node),
            }],
            free: Vec::new(),
            root: ElementId {
                index: 0,
                generation: 0,
            },
            next_event_id: FIRST_EVENT_ID,
            mouse: (0, 0),
            left_down: false,
            right_down: false,
            middle_down: false,
            keyboard_focus: None,
            suppressed_key: None,
            suppressed_char_tail_pending: false,
            default_text_input: None,
            modal: None,
            captured: None,
            ui_locked: false,
            drag_source: None,
            drag_payload: None,
            drag_ghost: None,
            last_drag_hover: None,
            press: (0, 0),
            drag_candidate: false,
            world_drag_candidate: false,
            window_drag: None,
            last_click: None,
            resize: None,
            hover: None,
            hover_started_ms: 0,
            tooltip_delay_ms: 250,
            tooltip_duration_ms: 10_000,
            tooltip_fired: false,
            tooltip_shown_ms: 0,
            now_ms: 0,
            last_mouse_move_ms: 0,
            modifiers: Modifiers::NONE,
            active_popup: None,
            fixed_canvases: Vec::new(),
            fixed_canvas: None,
            media_seconds: 0.0,
            pixels_per_unit: 1.0,
            sharp_fonts: None,
            windows: WindowManager::default(),
            output: Vec::new(),
            window_blocks: HashMap::new(),
            swallowed_releases: Vec::new(),
            flashing: HashMap::new(),
        }
    }

    // ── Arena ───────────────────────────────────────────────────────────

    /// The root element (parent of every top-level window).
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    pub(crate) fn node(&self, id: ElementId) -> Option<&Node> {
        self.slots
            .get(id.index as usize)
            .filter(|s| s.generation == id.generation)
            .and_then(|s| s.node.as_ref())
    }

    pub(crate) fn node_mut(&mut self, id: ElementId) -> Option<&mut Node> {
        self.slots
            .get_mut(id.index as usize)
            .filter(|s| s.generation == id.generation)
            .and_then(|s| s.node.as_mut())
    }

    /// True while `id` names a live element.
    #[must_use]
    pub fn exists(&self, id: ElementId) -> bool {
        self.node(id).is_some()
    }

    /// The element `id`.
    #[must_use]
    pub fn element(&self, id: ElementId) -> Option<&Element> {
        self.node(id).map(|n| &n.element)
    }

    /// The element `id`, mutable. Visibility, enabled state and z-order
    /// change through the root ([`UiRoot::set_visible`], ...), so the
    /// root's bookkeeping follows.
    pub fn element_mut(&mut self, id: ElementId) -> Option<&mut Element> {
        self.node_mut(id).map(|n| &mut n.element)
    }

    /// The widget of `id` as a `T`, unless it is busy handling a call.
    #[must_use]
    pub fn widget<T: Widget>(&self, id: ElementId) -> Option<&T> {
        self.node(id)?.widget.as_ref()?.as_any().downcast_ref()
    }

    /// The widget of `id` as a mutable `T`.
    pub fn widget_mut<T: Widget>(&mut self, id: ElementId) -> Option<&mut T> {
        self.node_mut(id)?
            .widget
            .as_mut()?
            .as_any_mut()
            .downcast_mut()
    }

    /// The widget of `id`, whatever it is.
    #[must_use]
    pub fn widget_dyn(&self, id: ElementId) -> Option<&dyn Widget> {
        self.node(id)?.widget.as_deref()
    }

    /// Run `f` on the widget of `id` with the whole root available. `None`
    /// when the element is gone or its widget is already running.
    pub fn with_widget<R>(
        &mut self,
        id: ElementId,
        f: impl FnOnce(&mut dyn Widget, &mut UiRoot) -> R,
    ) -> Option<R> {
        let mut widget = self.node_mut(id)?.widget.take()?;
        let r = f(widget.as_mut(), self);
        if let Some(node) = self.node_mut(id) {
            node.widget = Some(widget);
        }
        Some(r)
    }

    /// Run `f` on the widget of `id` as a `T`, with the whole root
    /// available. `None` when the element is gone, busy, or not a `T`.
    pub fn with<T: Widget, R>(
        &mut self,
        id: ElementId,
        f: impl FnOnce(&mut T, &mut UiRoot) -> R,
    ) -> Option<R> {
        self.with_widget(id, |w, ui| {
            w.as_any_mut().downcast_mut::<T>().map(|t| f(t, ui))
        })
        .flatten()
    }

    /// Create a detached element.
    pub fn insert(&mut self, element: Element, widget: impl Widget) -> ElementId {
        self.insert_boxed(element, Box::new(widget))
    }

    /// Create a detached element from a boxed widget.
    pub fn insert_boxed(&mut self, element: Element, widget: Box<dyn Widget>) -> ElementId {
        let node = Node {
            element,
            widget: Some(widget),
            links: Links::default(),
        };
        if let Some(index) = self.free.pop() {
            let slot = &mut self.slots[index as usize];
            slot.node = Some(node);
            ElementId {
                index,
                generation: slot.generation,
            }
        } else {
            let index = u32::try_from(self.slots.len()).expect("fewer than 2^32 elements");
            self.slots.push(Slot {
                generation: 0,
                node: Some(node),
            });
            ElementId {
                index,
                generation: 0,
            }
        }
    }

    /// Create an element as the last child of `parent`.
    pub fn add(&mut self, parent: ElementId, element: Element, widget: impl Widget) -> ElementId {
        let id = self.insert(element, widget);
        self.add_child(parent, id);
        id
    }

    /// Attach `child` (and its subtree) as the last child of `parent`,
    /// detaching it from any previous parent first. Elements attached under
    /// the root get event ids.
    pub fn add_child(&mut self, parent: ElementId, child: ElementId) {
        if !self.exists(parent) || !self.exists(child) || parent == child {
            return;
        }
        if let Some(old) = self.parent(child) {
            self.remove_child(old, child);
        }
        if let Some(n) = self.node_mut(child) {
            n.links.parent = Some(parent);
        }
        if let Some(p) = self.node_mut(parent) {
            p.links.children.push(child);
        }
        if self.is_connected(parent) {
            self.assign_event_ids(child);
        }
    }

    fn assign_event_ids(&mut self, id: ElementId) {
        let mut stack = vec![id];
        while let Some(id) = stack.pop() {
            let next = self.next_event_id;
            if let Some(n) = self.node_mut(id)
                && n.links.event_id == 0
            {
                n.links.event_id = next;
                self.next_event_id += 1;
            }
            if let Some(n) = self.node(id) {
                stack.extend(n.links.children.iter().rev());
            }
        }
    }

    /// Detach `child` from `parent`; false when it is not a child of it.
    /// The subtree stays alive (detached) until [`UiRoot::destroy`]; focus,
    /// capture, hover and window registrations inside it are released.
    pub fn remove_child(&mut self, parent: ElementId, child: ElementId) -> bool {
        let is_child = self
            .node(parent)
            .is_some_and(|p| p.links.children.contains(&child));
        if !is_child {
            return false;
        }
        if self.is_connected(parent) {
            self.on_subtree_removing(child);
        }
        if let Some(p) = self.node_mut(parent) {
            p.links.children.retain(|c| *c != child);
        }
        if let Some(c) = self.node_mut(child) {
            c.links.parent = None;
        }
        true
    }

    /// Moves `sequence` (children of `parent`, in that order) to just after
    /// `after`, another child of `parent` (the end when it is not one);
    /// the other children keep their order. Nothing is detached, so focus,
    /// capture and popups inside them stay (OpenAC `UiElement.MoveChild`,
    /// for a run of children at once).
    pub fn place_after(&mut self, parent: ElementId, after: ElementId, sequence: &[ElementId]) {
        let run: Vec<ElementId> = sequence
            .iter()
            .copied()
            .filter(|c| *c != after && self.parent(*c) == Some(parent))
            .collect();
        let moving: std::collections::HashSet<ElementId> = run.iter().copied().collect();
        let Some(p) = self.node_mut(parent) else {
            return;
        };
        let children = &mut p.links.children;
        children.retain(|c| !moving.contains(c));
        let at = children
            .iter()
            .position(|c| *c == after)
            .map_or(children.len(), |i| i + 1);
        children.splice(at..at, run);
    }

    /// Detach `id` if attached, then free it and its whole subtree.
    pub fn destroy(&mut self, id: ElementId) {
        if id == self.root {
            return;
        }
        if let Some(parent) = self.parent(id) {
            self.remove_child(parent, id);
        }
        let mut stack = vec![id];
        while let Some(id) = stack.pop() {
            let Some(slot) = self.slots.get_mut(id.index as usize) else {
                continue;
            };
            if slot.generation != id.generation {
                continue;
            }
            if let Some(node) = slot.node.take() {
                stack.extend(node.links.children.iter().copied());
            }
            slot.generation = slot.generation.wrapping_add(1);
            self.free.push(id.index);
        }
    }

    /// The parent of `id`.
    #[must_use]
    pub fn parent(&self, id: ElementId) -> Option<ElementId> {
        self.node(id)?.links.parent
    }

    /// The children of `id`, in insertion order.
    #[must_use]
    pub fn children(&self, id: ElementId) -> Vec<ElementId> {
        self.node(id)
            .map(|n| n.links.children.clone())
            .unwrap_or_default()
    }

    /// True when `id` has children.
    #[must_use]
    pub fn has_children(&self, id: ElementId) -> bool {
        self.node(id).is_some_and(|n| !n.links.children.is_empty())
    }

    /// The id `id`'s events carry as their source (0 until it is attached
    /// under the root).
    #[must_use]
    pub fn event_id(&self, id: ElementId) -> u32 {
        self.node(id).map_or(0, |n| n.links.event_id)
    }

    /// True when `id` is the root or attached under it.
    #[must_use]
    pub fn is_connected(&self, id: ElementId) -> bool {
        let mut cur = Some(id);
        while let Some(c) = cur {
            if c == self.root {
                return true;
            }
            cur = self.parent(c);
        }
        false
    }

    /// True when `element` is `subtree` or one of its descendants.
    #[must_use]
    pub fn is_within(&self, element: Option<ElementId>, subtree: ElementId) -> bool {
        let mut cur = element;
        while let Some(c) = cur {
            if c == subtree {
                return true;
            }
            cur = self.parent(c);
        }
        false
    }

    /// Top-left of `id` in canvas pixels.
    #[must_use]
    pub fn screen_position(&self, id: ElementId) -> (f32, f32) {
        let mut x = 0.0;
        let mut y = 0.0;
        let mut cur = Some(id);
        while let Some(c) = cur {
            let Some(n) = self.node(c) else {
                break;
            };
            x += n.element.left;
            y += n.element.top;
            let chrome = n.element.scroll_chrome;
            cur = n.links.parent;
            if let Some(p) = cur.and_then(|p| self.element(p)) {
                let (ox, oy) = scroll_offset_for(p, chrome);
                x -= ox;
                y -= oy;
            }
        }
        (x, y)
    }

    /// The first element under `start` (itself included, depth first) with
    /// dat element id `dat_id`.
    #[must_use]
    pub fn find_descendant(&self, start: ElementId, dat_id: u32) -> Option<ElementId> {
        let n = self.node(start)?;
        if n.element.dat_element_id == dat_id {
            return Some(start);
        }
        n.links
            .children
            .iter()
            .find_map(|c| self.find_descendant(*c, dat_id))
    }

    /// Children sorted for drawing: lowest z-order first; equal z-orders
    /// keep insertion order.
    #[must_use]
    pub fn back_to_front(&self, id: ElementId) -> Vec<ElementId> {
        let mut children = self.children(id);
        children.sort_by_key(|c| self.element(*c).map_or(0, |e| e.z_order));
        children
    }

    fn front_to_back(&self, id: ElementId) -> Vec<ElementId> {
        let mut v = self.back_to_front(id);
        v.reverse();
        v
    }

    // ── Element state that the root tracks ──────────────────────────────

    /// Show or hide `id`. Hiding releases focus, capture, hover and drag
    /// state inside it; registered windows report the change.
    pub fn set_visible(&mut self, id: ElementId, visible: bool) {
        let Some(e) = self.element(id) else {
            return;
        };
        if e.visible == visible {
            return;
        }
        let connected = self.is_connected(id);
        if connected && !visible {
            self.windows_prepare_to_hide(id);
            self.clear_subtree_ownership(id, false);
        }
        if let Some(e) = self.element_mut(id) {
            e.visible = visible;
        }
        if connected {
            self.windows_on_visibility_changed(id, visible);
        }
    }

    /// Enable or disable input to `id`.
    pub fn set_enabled(&mut self, id: ElementId, enabled: bool) {
        let Some(e) = self.element_mut(id) else {
            return;
        };
        if e.enabled == enabled {
            return;
        }
        e.enabled = enabled;
        self.with_widget(id, |w, ui| w.on_enabled_changed(ui, id));
    }

    /// Set the z-order of `id` among its siblings.
    pub fn set_z_order(&mut self, id: ElementId, z: i32) {
        if let Some(e) = self.element_mut(id) {
            e.z_order = z;
        }
    }

    /// Set the compatibility anchors of `id`: drops its layout policy and
    /// its captured margins.
    pub fn set_anchors(&mut self, id: ElementId, anchors: AnchorEdges) {
        if let Some(n) = self.node_mut(id) {
            n.element.set_anchors(anchors);
            n.links.anchor_capture = None;
        }
    }

    /// Enter retail state `state` on `id` (no-op false for widgets without
    /// states).
    pub fn set_retail_state(&mut self, id: ElementId, state: u32) -> bool {
        self.with_widget(id, |w, ui| w.set_retail_state(ui, id, state))
            .unwrap_or(false)
    }

    /// The active retail state of `id`.
    #[must_use]
    pub fn retail_state(&self, id: ElementId) -> Option<u32> {
        self.widget_dyn(id)?.retail_state()
    }

    /// The scroll model of `id` (following a widget that stands in for
    /// another element's model), unless that widget is busy.
    pub fn scroll_model_mut(&mut self, id: ElementId) -> Option<&mut crate::text::Scrollable> {
        let mut target = id;
        for _ in 0..4 {
            match self.widget_dyn(target)?.scroll_model_element() {
                Some(next) if next != target => target = next,
                _ => break,
            }
        }
        self.node_mut(target)?.widget.as_mut()?.scroll_model()
    }

    /// A copy of the scroll model of `id`.
    #[must_use]
    pub fn scroll_model(&mut self, id: ElementId) -> Option<crate::text::Scrollable> {
        self.scroll_model_mut(id).map(|m| *m)
    }

    /// Hand clipboard text to the text field that asked for it
    /// ([`UiOutput::PasteRequest`]).
    pub fn paste(&mut self, id: ElementId, text: &str) {
        if self.widget::<crate::plugin::MarkupField>(id).is_some() {
            self.with::<crate::plugin::MarkupField, _>(id, |f, ui| f.inner.paste(ui, id, text));
            return;
        }
        if self.widget::<crate::widgets::Menu>(id).is_some() {
            self.with::<crate::widgets::Menu, _>(id, |m, ui| m.paste(ui, id, text));
            return;
        }
        self.with::<crate::widgets::Field, _>(id, |f, ui| f.paste(ui, id, text));
    }

    /// The current cursor of `id`: its active state's, else its default's.
    #[must_use]
    pub fn active_cursor(&self, id: ElementId) -> Option<crate::element_info::CursorMedia> {
        let node = self.node(id)?;
        let name = node
            .widget
            .as_ref()
            .map_or("", |w| w.active_cursor_state_name());
        node.element.cursor_for_state(name, true)
    }

    // ── Output ──────────────────────────────────────────────────────────

    /// Queue an output for the host.
    pub fn emit(&mut self, output: UiOutput) {
        self.output.push(output);
    }

    /// Queue a widget message from `element`.
    pub fn send_message(&mut self, element: ElementId, message: ElementMessage) {
        self.output.push(UiOutput::Element { element, message });
    }

    /// Take every queued output, oldest first.
    pub fn drain_output(&mut self) -> Vec<UiOutput> {
        std::mem::take(&mut self.output)
    }

    /// True when outputs are queued.
    #[must_use]
    pub fn has_output(&self) -> bool {
        !self.output.is_empty()
    }

    // ── Size, canvas, clock ─────────────────────────────────────────────

    /// Set the screen size (the root element's size).
    pub fn set_size(&mut self, width: f32, height: f32) {
        let root = self.root;
        if let Some(e) = self.element_mut(root) {
            e.width = width;
            e.height = height;
        }
    }

    /// The root's size.
    #[must_use]
    pub fn size(&self) -> (f32, f32) {
        self.element(self.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height))
    }

    /// Declare that `owner` (a pre-game screen) is drawn on a fixed canvas
    /// of `size`, scaled to the window. Every active declaration must agree;
    /// re-declaring the same size is a no-op.
    pub fn declare_fixed_canvas(
        &mut self,
        owner: u64,
        size: (f32, f32),
    ) -> Result<(), CanvasConflict> {
        if let Some((_, existing)) = self.fixed_canvases.iter().find(|(o, _)| *o == owner) {
            if *existing == size {
                return Ok(());
            }
            return Err(CanvasConflict {
                declared: *existing,
                requested: size,
            });
        }
        if let Some((_, other)) = self.fixed_canvases.iter().find(|(_, s)| *s != size) {
            return Err(CanvasConflict {
                declared: *other,
                requested: size,
            });
        }
        self.fixed_canvases.push((owner, size));
        self.fixed_canvas = Some(size);
        Ok(())
    }

    /// Withdraw `owner`'s fixed canvas.
    pub fn revoke_fixed_canvas(&mut self, owner: u64) {
        let before = self.fixed_canvases.len();
        self.fixed_canvases.retain(|(o, _)| *o != owner);
        if self.fixed_canvases.len() == before {
            return;
        }
        self.fixed_canvas = self.fixed_canvases.first().map(|(_, s)| *s);
    }

    /// The fixed canvas in force.
    #[must_use]
    pub fn fixed_canvas(&self) -> Option<(f32, f32)> {
        self.fixed_canvas
    }

    /// The canvas the UI lays out on: the fixed canvas, or the screen.
    #[must_use]
    pub fn effective_canvas_size(&self) -> (f32, f32) {
        match self.fixed_canvas {
            Some((w, h)) if w > 0.0 && h > 0.0 => (w, h),
            _ => self.size(),
        }
    }

    /// Screen pixels per canvas pixel; (1, 1) without a usable fixed canvas.
    #[must_use]
    pub fn canvas_scale(&self) -> (f32, f32) {
        let (w, h) = self.size();
        match self.fixed_canvas {
            Some((cw, ch)) if cw > 0.0 && ch > 0.0 && w > 0.0 && h > 0.0 => (w / cw, h / ch),
            _ => (1.0, 1.0),
        }
    }

    /// Tell the UI how many framebuffer pixels one of its screen units covers
    /// (two on a Retina Mac), for the width of a shape's soft edge.
    pub fn set_pixels_per_unit(&mut self, pixels: f32) {
        self.pixels_per_unit = pixels;
    }

    /// Tell the UI which sharper font bakes to draw from on this display. The
    /// fonts themselves are untouched, so nothing measures differently.
    pub fn set_sharp_fonts(&mut self, fonts: Option<Arc<SharpFonts>>) {
        self.sharp_fonts = fonts;
    }

    /// The sharper font bakes the UI draws from on this display, if any.
    #[must_use]
    pub fn sharp_fonts(&self) -> Option<&SharpFonts> {
        self.sharp_fonts.as_deref()
    }

    fn map_window_to_canvas(&self, x: i32, y: i32) -> (i32, i32) {
        let (sx, sy) = self.canvas_scale();
        if (sx, sy) == (1.0, 1.0) {
            (x, y)
        } else {
            ((x as f32 / sx) as i32, (y as f32 / sy) as i32)
        }
    }

    /// The UI clock of the last tick, milliseconds.
    #[must_use]
    pub fn now_ms(&self) -> i64 {
        self.now_ms
    }

    /// Milliseconds since the pointer last moved.
    #[must_use]
    pub fn mouse_idle_ms(&self) -> i64 {
        self.now_ms - self.last_mouse_move_ms
    }

    /// Seconds of UI media time (animated media sample this).
    #[must_use]
    pub fn media_seconds(&self) -> f64 {
        self.media_seconds
    }

    // ── Device state ────────────────────────────────────────────────────

    /// Pointer position in canvas pixels.
    #[must_use]
    pub fn mouse(&self) -> (i32, i32) {
        self.mouse
    }

    /// True while the button is held.
    #[must_use]
    pub fn button_down(&self, button: MouseButton) -> bool {
        match button {
            MouseButton::Left => self.left_down,
            MouseButton::Right => self.right_down,
            MouseButton::Middle => self.middle_down,
        }
    }

    /// True while any mouse button is held, wherever it went down (the
    /// world takes no capture, so its drags reach the UI as plain moves).
    #[must_use]
    pub fn any_button_down(&self) -> bool {
        self.left_down || self.right_down || self.middle_down
    }

    /// The element with keyboard focus.
    #[must_use]
    pub fn keyboard_focus(&self) -> Option<ElementId> {
        self.keyboard_focus
    }

    /// The element holding pointer capture.
    #[must_use]
    pub fn captured(&self) -> Option<ElementId> {
        self.captured
    }

    /// The element a drag started from.
    #[must_use]
    pub fn drag_source(&self) -> Option<ElementId> {
        self.drag_source
    }

    /// What the drag in progress carries.
    #[must_use]
    pub fn drag_payload(&self) -> Option<&DragPayload> {
        self.drag_payload.as_ref()
    }

    /// The ghost of the drag in progress, as captured when it began.
    #[must_use]
    pub fn drag_ghost(&self) -> Option<DragGhost> {
        self.drag_ghost
    }

    /// True while windows are locked against move and resize.
    #[must_use]
    pub fn ui_locked(&self) -> bool {
        self.ui_locked
    }

    /// Lock or unlock windows against move and resize.
    pub fn set_ui_locked(&mut self, locked: bool) {
        if self.ui_locked == locked {
            return;
        }
        self.ui_locked = locked;
        self.emit(UiOutput::UiLockChanged(locked));
        self.windows_on_lock_changed(locked);
    }

    /// True while a window is being moved.
    #[must_use]
    pub fn is_window_move_active(&self) -> bool {
        self.window_drag.is_some()
    }

    /// The edges of the window being resized.
    #[must_use]
    pub fn active_resize_edges(&self) -> ResizeEdges {
        self.resize.map_or(ResizeEdges::NONE, |r| r.edges)
    }

    /// True when the UI owns the pointer (capture, a popup or an element
    /// under it).
    #[must_use]
    pub fn wants_mouse(&mut self) -> bool {
        let (x, y) = self.mouse;
        self.captured.is_some() || self.popup_hit(x, y).is_some() || self.pick(x, y).is_some()
    }

    /// True when an element has keyboard focus.
    #[must_use]
    pub fn wants_keyboard(&self) -> bool {
        self.keyboard_focus.is_some()
    }

    /// The edges a press at the pointer would resize.
    #[must_use]
    pub fn hover_resize_edges(&mut self) -> ResizeEdges {
        let (x, y) = self.mouse;
        let target = self.pick(x, y);
        let window = self.find_window(target);
        let Some(w) = window.filter(|w| self.element(*w).is_some_and(|e| e.resizable)) else {
            return ResizeEdges::NONE;
        };
        if self.ui_locked {
            return ResizeEdges::NONE;
        }
        if let Some(edges) = target.and_then(|t| self.grip_edges(t)) {
            return self.effective_grip_edges(edges, w);
        }
        if self.find_drag_handle_window(target).is_some() {
            return ResizeEdges::NONE;
        }
        self.element(w)
            .map_or(ResizeEdges::NONE, |e| hit_edges(e, x, y, RESIZE_GRIP))
    }

    /// True when a press at the pointer would move a window.
    #[must_use]
    pub fn hover_window_move(&mut self) -> bool {
        let (x, y) = self.mouse;
        let Some(target) = self.pick(x, y) else {
            return false;
        };
        if self.ui_locked {
            return false;
        }
        if self.hover_resize_edges() != ResizeEdges::NONE {
            return false;
        }
        if self.find_drag_handle_window(Some(target)).is_some() {
            return true;
        }
        let Some(window) = self.find_window(Some(target)) else {
            return false;
        };
        let Some(w) = self.element(window).filter(|e| e.draggable) else {
            return false;
        };
        target == window && within_border_band(w, x, y, MOVE_BORDER_BAND)
    }

    fn grip_edges(&self, id: ElementId) -> Option<ResizeEdges> {
        self.widget_dyn(id)?.resize_grip_edges()
    }

    fn effective_grip_edges(&self, grip: ResizeEdges, window: ElementId) -> ResizeEdges {
        let Some(w) = self.element(window) else {
            return ResizeEdges::NONE;
        };
        let mut edges = grip;
        if !w.resize_x {
            edges &= !ResizeEdges::HORIZONTAL;
        }
        if !w.resize_y {
            edges &= !ResizeEdges::VERTICAL;
        }
        edges & w.resizable_edges
    }

    // ── Ownership release ───────────────────────────────────────────────

    fn on_subtree_removing(&mut self, subtree: ElementId) {
        let replacing_drag_source = Some(subtree) == self.drag_source;
        self.clear_subtree_ownership(subtree, replacing_drag_source);
        self.windows_on_subtree_removing(subtree);
    }

    /// Release focus, capture, hover, drag and window-move state held by
    /// anything inside `subtree`. With `preserve_detached_drag`, a drag
    /// whose source is being replaced survives (capture moves to the root).
    pub(crate) fn clear_subtree_ownership(
        &mut self,
        subtree: ElementId,
        preserve_detached_drag: bool,
    ) {
        if let Some((popup, owner)) = self.active_popup
            && self.is_within(Some(popup), subtree)
        {
            // A popup whose owner hides or leaves the tree closes with it, so
            // it does not come back open when the owner is shown again.
            self.active_popup = None;
            self.dismiss_popup(popup, owner);
        }
        if self.is_within(self.keyboard_focus, subtree) {
            self.set_keyboard_focus(None);
        }
        if self.is_within(self.captured, subtree) {
            if preserve_detached_drag && self.captured == self.drag_source {
                let root = self.root;
                self.set_capture(root);
            } else {
                self.release_capture();
            }
            self.drag_candidate = false;
        }
        if self.is_within(self.default_text_input, subtree) {
            self.default_text_input = None;
        }
        if self.is_within(self.modal, subtree) {
            self.modal = None;
        }
        if self.is_within(self.drag_source, subtree) {
            if let Some(source) = self.drag_source {
                let payload = self.drag_payload.clone();
                self.with_widget(source, |w, ui| {
                    w.set_drag_source_active(ui, source, false, payload.as_ref());
                });
            }
            if !preserve_detached_drag {
                self.drag_source = None;
                self.drag_payload = None;
                self.drag_ghost = None;
                self.drag_candidate = false;
            }
        }
        if self.is_within(self.hover, subtree) {
            self.leave_hover();
        }
        if self.is_within(self.last_drag_hover, subtree) {
            self.last_drag_hover = None;
        }
        if self.is_within(self.last_click.map(|c| c.0), subtree) {
            self.last_click = None;
        }
        if self.is_within(self.window_drag.map(|d| d.target), subtree) {
            self.window_drag = None;
            self.drag_candidate = false;
        }
        if self.is_within(self.resize.map(|r| r.target), subtree) {
            self.resize = None;
            self.drag_candidate = false;
        }
    }

    // ── Events ──────────────────────────────────────────────────────────

    fn event_for(&self, target: ElementId, k: i32) -> UiEvent {
        let source = self.event_id(target);
        let mut event = UiEvent::new(source, target, k);
        event.modifiers = self.modifiers;
        event
    }

    /// Deliver `e` to the widget of `id` alone; true when handled.
    pub fn send_event(&mut self, id: ElementId, e: &UiEvent) -> bool {
        self.with_widget(id, |w, ui| w.on_event(ui, id, e))
            .unwrap_or(false)
    }

    /// Deliver `e` to `start`, then each ancestor in turn, until one
    /// handles it.
    pub fn bubble_event(&mut self, start: ElementId, e: &UiEvent) -> bool {
        let mut cur = Some(start);
        while let Some(w) = cur {
            if self.send_event(w, e) {
                return true;
            }
            cur = self.parent(w);
        }
        false
    }

    /// Bubbles a right click like any event, but once an element leaves
    /// it unhandled, offers it to that element's context menu (opened at
    /// the screen point `x`, `y`) before going on.
    fn bubble_right_click(&mut self, start: ElementId, e: &UiEvent, x: i32, y: i32) -> bool {
        let mut cur = Some(start);
        while let Some(w) = cur {
            if self.send_event(w, e) {
                return true;
            }
            if let Some(menu) = self
                .element(w)
                .filter(|el| el.enabled)
                .and_then(|el| el.context_menu)
            {
                let open = self
                    .event_for(menu, kind::CONTEXT_MENU_OPEN)
                    .with_data(0, x, y);
                if self.send_event(menu, &open) {
                    return true;
                }
            }
            cur = self.parent(w);
        }
        false
    }

    /// Send event `k` (with a payload) to `target` alone.
    pub fn fire_event(&mut self, k: i32, target: ElementId, payload: Option<DragPayload>) -> bool {
        let e = self.event_for(target, k).with_payload(payload);
        self.send_event(target, &e)
    }

    /// Offer a pointer message to the element's image-map region; true
    /// when the region consumed it. Only the event's own target reads the
    /// coordinates; recording a press never consumes it.
    pub fn dispatch_to_pointer_region(&mut self, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        let Some(region) = self
            .element_mut(me)
            .and_then(|el| el.pointer_region.as_mut())
        else {
            return false;
        };
        match e.kind {
            kind::MOUSE_DOWN | kind::RIGHT_DOWN => {
                region.press = (e.data1, e.data2);
                false
            }
            kind::CLICK => region.handler.clicked(e.data1, e.data2),
            kind::RIGHT_CLICK => region.handler.right_clicked(e.data1, e.data2),
            kind::DRAG_BEGIN => region.handler.is_drag_source(),
            kind::DRAG_ENTER => region
                .handler
                .drag_over_at(e.payload.as_ref(), e.data1, e.data2),
            kind::DRAG_OVER => region.handler.drag_left(),
            kind::DROP_RELEASED => {
                region
                    .handler
                    .drop_released_at(e.payload.as_ref(), e.data1, e.data2)
            }
            _ => false,
        }
    }

    // ── Per-frame ───────────────────────────────────────────────────────

    /// One UI frame: advance the media clock, fire or expire the hover
    /// tooltip, send the time pulse to every element, then tick the visible
    /// tree.
    pub fn tick(&mut self, dt: f64, now_ms: i64) {
        if dt.is_finite() && dt > 0.0 {
            self.media_seconds += dt;
            let step = dt as f32;
            self.flashing.retain(|_, left| {
                *left -= step;
                *left > 0.0
            });
        }
        self.now_ms = now_ms;

        if let Some(hover) = self.hover {
            if !self.tooltip_fired
                && self.captured.is_none()
                && self.now_ms - self.hover_started_ms >= self.effective_tooltip_delay_ms(hover)
            {
                let e = self.event_for(hover, kind::TOOLTIP);
                self.send_event(hover, &e);
                self.tooltip_fired = true;
                self.tooltip_shown_ms = self.now_ms;
                self.emit(UiOutput::TooltipShow(hover));
            } else if self.tooltip_fired
                && self.now_ms - self.tooltip_shown_ms >= self.tooltip_duration_ms
                && !self
                    .widget_dyn(hover)
                    .is_some_and(|w| w.hover_outlasts_tooltip())
            {
                let e = self.event_for(hover, kind::HOVER_LEAVE);
                self.send_event(hover, &e);
                self.emit(UiOutput::TooltipHide(hover));
                self.hover = None;
                self.tooltip_fired = false;
            }
        }

        let root = self.root;
        self.broadcast_global_time(root, now_ms as f64 / 1000.0);
        self.tick_tree(root, dt);
    }

    fn effective_tooltip_delay_ms(&self, id: ElementId) -> i64 {
        self.element(id)
            .and_then(|e| e.authored.tooltip_delay_seconds)
            .map_or(self.tooltip_delay_ms, |s| (s * 1000.0) as i64)
    }

    fn broadcast_global_time(&mut self, id: ElementId, now: f64) {
        self.with_widget(id, |w, ui| w.global_time(ui, id, now));
        let order = self.back_to_front(id);
        for &child in order.iter() {
            if self.parent(child) == Some(id) {
                self.broadcast_global_time(child, now);
            }
        }
    }

    fn tick_tree(&mut self, id: ElementId, dt: f64) {
        if !self.element(id).is_some_and(|e| e.visible) {
            return;
        }
        self.with_widget(id, |w, ui| w.tick(ui, id, dt));
        for child in self.children(id) {
            self.tick_tree(child, dt);
        }
    }

    // ── Anchors ─────────────────────────────────────────────────────────

    /// Lay `id` out in a parent of the given size: its imported edge-mode
    /// policy when it has one, else its anchors against the margins
    /// captured on the first pass.
    pub fn apply_anchor(&mut self, id: ElementId, parent_w: f32, parent_h: f32) {
        let Some(e) = self.element_mut(id) else {
            return;
        };
        if let Some(policy) = e.layout_policy {
            let current = PixelRect::from_position_and_size(
                e.left as i32,
                e.top as i32,
                e.width as i32,
                e.height as i32,
            );
            let parent = PixelRect::from_position_and_size(0, 0, parent_w as i32, parent_h as i32);
            let next = policy.apply(current, parent);
            e.left = next.x0 as f32;
            e.top = next.y0 as f32;
            e.width = next.width() as f32;
            e.height = next.height() as f32;
            return;
        }
        if e.anchors.is_empty() {
            return;
        }
        let Some(n) = self.node_mut(id) else {
            return;
        };
        let e = &mut n.element;
        let c = match n.links.anchor_capture {
            Some(c) if c.anchors == e.anchors => c,
            _ => {
                let c = AnchorCapture {
                    anchors: e.anchors,
                    left: e.left,
                    top: e.top,
                    right: parent_w - (e.left + e.width),
                    bottom: parent_h - (e.top + e.height),
                    width: e.width,
                    height: e.height,
                };
                n.links.anchor_capture = Some(c);
                c
            }
        };
        let r = compute_anchored_rect(
            e.anchors, c.left, c.top, c.right, c.bottom, c.width, c.height, parent_w, parent_h,
        );
        e.left = r.x;
        e.top = r.y;
        e.width = r.w;
        e.height = r.h;
    }

    /// Forget the captured margins of `id` and re-base its layout policy on
    /// its current rectangle, so a deliberate move or resize sticks.
    pub fn reset_anchor_capture(&mut self, id: ElementId) {
        let parent_size = self
            .parent(id)
            .and_then(|p| self.element(p))
            .map(|p| (p.width, p.height));
        let Some(n) = self.node_mut(id) else {
            return;
        };
        n.links.anchor_capture = None;
        let e = &mut n.element;
        let Some((pw, ph)) = parent_size else {
            return;
        };
        let rect = PixelRect::from_position_and_size(
            e.left as i32,
            e.top as i32,
            e.width as i32,
            e.height as i32,
        );
        if let Some(policy) = e.layout_policy.as_mut() {
            policy.rebase(
                rect,
                PixelRect::from_position_and_size(0, 0, pw as i32, ph as i32),
            );
        }
    }

    /// Capture the anchor margins of `id` against its parent's size now,
    /// instead of on the first layout pass that reaches it: a pass skips
    /// hidden windows, and a window resized while hidden would otherwise
    /// be measured at its new size.
    pub fn capture_current_anchor_baseline(&mut self, id: ElementId) {
        let Some((pw, ph)) = self
            .parent(id)
            .and_then(|p| self.element(p))
            .map(|p| (p.width, p.height))
        else {
            return;
        };
        let Some(n) = self.node_mut(id) else {
            return;
        };
        if n.element.anchors.is_empty() {
            return;
        }
        n.links.anchor_capture = None;
        self.apply_anchor(id, pw, ph);
    }

    /// Re-base every child of `id` (see [`UiRoot::reset_anchor_capture`]).
    pub fn rebase_child_layout_baselines(&mut self, id: ElementId) {
        for child in self.children(id) {
            self.reset_anchor_capture(child);
        }
    }

    /// Capture every descendant's anchor baseline from its authored
    /// rectangle now, so where an anchored child lands does not depend on
    /// when it was first visible.
    pub fn capture_authored_anchor_baselines(&mut self, id: ElementId) {
        let Some((w, h)) = self.element(id).map(|e| (e.width, e.height)) else {
            return;
        };
        for child in self.children(id) {
            self.apply_anchor(child, w, h);
            self.capture_authored_anchor_baselines(child);
        }
    }

    // ── Drawing ─────────────────────────────────────────────────────────

    /// Draw the whole UI for a screen of `screen` pixels: the tree, then the
    /// overlay layer (popups), then the drag ghost.
    pub fn draw(&mut self, screen: (f32, f32), sprites: &dyn SpriteSizes) -> DrawList {
        self.set_size(screen.0, screen.1);
        let mut list = DrawList {
            canvas_scale: self.canvas_scale(),
            ..DrawList::default()
        };
        let media = self.media_seconds;
        // A fixed canvas stretched unevenly has no one pixel size; the larger
        // scale keeps a soft edge from going wider than a pixel.
        let (sx, sy) = self.canvas_scale();
        let mut rc = RenderContext::new(&mut list, screen, sprites)
            .with_media_seconds(media)
            .with_pixels_per_unit(self.pixels_per_unit * sx.max(sy))
            .with_sharp_fonts(self.sharp_fonts.clone());
        let root = self.root;
        self.draw_tree(root, &mut rc);
        rc.begin_overlay();
        self.draw_overlays(root, &mut rc);
        if let Some(g) = self.drag_ghost {
            let (mx, my) = self.mouse;
            rc.sprite(
                g.texture,
                mx as f32 - g.width as f32 / 2.0,
                my as f32 - g.height as f32 / 2.0,
                g.width as f32,
                g.height as f32,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        }
        rc.end_overlay();
        list
    }

    /// Draw `id` and its subtree into `rc` (origin at its parent).
    pub fn draw_tree(&mut self, id: ElementId, rc: &mut RenderContext<'_>) {
        let Some(e) = self.element(id) else {
            return;
        };
        if !e.visible {
            return;
        }
        let (left, top, opacity) = (e.left, e.top, e.opacity);
        let Some(mut widget) = self.node_mut(id).and_then(|n| n.widget.take()) else {
            return;
        };
        let clips = widget.clips_children();
        rc.push_transform(left, top);
        rc.push_alpha(opacity);
        if clips {
            let (w, h) = self.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
            rc.push_clip(0.0, 0.0, w, h);
        }
        if !rc.current_clip_is_empty() {
            widget.draw(self, id, rc);
            if !widget.layout_children(self, id) {
                let (w, h) = self.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
                for child in self.children(id) {
                    self.apply_anchor(child, w, h);
                }
            }
            let scroll = self
                .element(id)
                .and_then(|e| e.content_viewport.map(|v| (v, e.content_offset)));
            let order = self.back_to_front(id);
            for &child in order.iter() {
                let chrome = self.element(child).is_some_and(|e| e.scroll_chrome);
                match scroll {
                    // A scrolled child: clipped to the viewport, moved by
                    // the offset.
                    Some(((vw, vh), (ox, oy))) if !chrome => {
                        rc.push_clip(0.0, 0.0, vw, vh);
                        rc.push_transform(-ox, -oy);
                        self.draw_tree(child, rc);
                        rc.pop_transform();
                        rc.pop_clip();
                    }
                    _ => self.draw_tree(child, rc),
                }
            }
            widget.draw_after_children(self, id, rc);
            if self.window_blocks.contains_key(&id) {
                let (w, h) = self.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
                rc.fill(0.0, 0.0, w, h, Color::rgba(0.0, 0.0, 0.0, 0.45));
            }
            if let Some(left) = self.flashing.get(&id).copied()
                && ((left * 10.0) as i32) & 1 == 0
            {
                let (w, h) = self.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
                rc.rect_outline(0.0, 0.0, w, h, Color::rgba(1.0, 0.82, 0.25, 1.0), 2.0);
            }
        }
        if clips {
            rc.pop_clip();
        }
        rc.pop_alpha();
        rc.pop_transform();
        if let Some(node) = self.node_mut(id) {
            node.widget = Some(widget);
        }
    }

    fn draw_overlays(&mut self, id: ElementId, rc: &mut RenderContext<'_>) {
        let Some(e) = self.element(id) else {
            return;
        };
        if !e.visible {
            return;
        }
        let (left, top, opacity, w, h) = (e.left, e.top, e.opacity, e.width, e.height);
        let Some(mut widget) = self.node_mut(id).and_then(|n| n.widget.take()) else {
            return;
        };
        rc.push_transform(left, top);
        rc.push_alpha(opacity);
        if widget.expands_clip_for_popup() {
            rc.push_clip_unbounded();
            widget.draw_overlay(self, id, rc);
            rc.pop_clip();
        } else {
            widget.draw_overlay(self, id, rc);
        }
        let has_children = self.has_children(id);
        if has_children {
            let clips = widget.clips_children();
            if clips {
                rc.push_clip(0.0, 0.0, w, h);
            }
            let offset = self
                .element(id)
                .and_then(|e| e.content_viewport.map(|_| e.content_offset));
            let order = self.back_to_front(id);
            for &child in order.iter() {
                let chrome = self.element(child).is_some_and(|e| e.scroll_chrome);
                match offset {
                    Some((ox, oy)) if !chrome => {
                        rc.push_transform(-ox, -oy);
                        self.draw_overlays(child, rc);
                        rc.pop_transform();
                    }
                    _ => self.draw_overlays(child, rc),
                }
            }
            if clips {
                rc.pop_clip();
            }
        }
        rc.pop_alpha();
        rc.pop_transform();
        if let Some(node) = self.node_mut(id) {
            node.widget = Some(widget);
        }
    }

    // ── Hit testing ─────────────────────────────────────────────────────

    /// The deepest element of `id`'s subtree at local (`x`, `y`).
    pub fn hit_test(&mut self, id: ElementId, x: f32, y: f32) -> Option<ElementId> {
        let node = self.node(id)?;
        let e = &node.element;
        if !e.visible || !e.enabled {
            return None;
        }
        let clips = node.widget.as_ref().is_none_or(|w| w.clips_children());
        if clips && !e.contains_local(x, y) {
            return None;
        }
        let scroll = e.content_viewport.map(|v| (v, e.content_offset));
        if !node.links.children.is_empty() {
            for child in self.front_to_back(id) {
                let Some(c) = self.element(child) else {
                    continue;
                };
                let (cl, ct) = (c.left, c.top);
                let (mut cx, mut cy) = (x, y);
                if let Some(((vw, vh), (ox, oy))) = scroll
                    && !c.scroll_chrome
                {
                    // Scrolled children are only reachable through the
                    // viewport.
                    if cx < 0.0 || cy < 0.0 || cx >= vw || cy >= vh {
                        continue;
                    }
                    cx += ox;
                    cy += oy;
                }
                if let Some(hit) = self.hit_test(child, cx - cl, cy - ct) {
                    return Some(hit);
                }
            }
        }
        let node = self.node(id)?;
        if node.element.click_through {
            return None;
        }
        let hit = node.widget.as_ref().map_or_else(
            || node.element.contains_local(x, y),
            |w| w.hit_test(&node.element, x, y),
        );
        hit.then_some(id)
    }

    fn hit_test_top_down(&mut self, x: i32, y: i32) -> Option<(ElementId, f32, f32)> {
        if let Some(modal) = self.modal {
            let (mx, my) = self.screen_position(modal);
            let (lx, ly) = (x as f32 - mx, y as f32 - my);
            return self.hit_test(modal, lx, ly).map(|h| (h, lx, ly));
        }
        let root = self.root;
        for child in self.front_to_back(root) {
            let (cx, cy) = self.screen_position(child);
            let (lx, ly) = (x as f32 - cx, y as f32 - cy);
            if let Some(hit) = self.hit_test(child, lx, ly) {
                return Some((hit, lx, ly));
            }
        }
        None
    }

    /// The topmost element at canvas (`x`, `y`), honouring the modal.
    pub fn pick(&mut self, x: i32, y: i32) -> Option<ElementId> {
        self.hit_test_top_down(x, y).map(|h| h.0)
    }

    fn find_window(&self, start: Option<ElementId>) -> Option<ElementId> {
        let mut cur = start;
        while let Some(c) = cur {
            let e = self.element(c)?;
            if e.draggable || e.resizable {
                return Some(c);
            }
            cur = self.parent(c);
        }
        None
    }

    fn find_drag_handle_window(&self, start: Option<ElementId>) -> Option<ElementId> {
        let mut cur = start;
        loop {
            let c = cur?;
            if c == self.root {
                return None;
            }
            let e = self.element(c)?;
            if e.window_move_handle {
                break;
            }
            cur = self.parent(c);
        }
        let mut e = cur?;
        while let Some(p) = self.parent(e) {
            if p == self.root {
                break;
            }
            e = p;
        }
        Some(e)
    }

    fn contains_absolute(&self, id: ElementId, x: i32, y: i32) -> bool {
        let Some(e) = self.element(id) else {
            return false;
        };
        let (sx, sy) = self.screen_position(id);
        let (x, y) = (x as f32, y as f32);
        x >= sx && x < sx + e.width && y >= sy && y < sy + e.height
    }

    // ── Popups ──────────────────────────────────────────────────────────

    /// Make `popup` the live popup, owned by `owner`; a press outside it
    /// dismisses it (the owner hears `on_popup_dismissed`). A different
    /// popup already live is dismissed first.
    pub fn set_active_popup(&mut self, popup: ElementId, owner: ElementId) {
        if let Some((current, current_owner)) = self.active_popup
            && current != popup
        {
            self.active_popup = None;
            self.dismiss_popup(current, current_owner);
        }
        self.active_popup = Some((popup, owner));
    }

    /// Forget `popup` as the live popup (it closed itself).
    pub fn clear_active_popup(&mut self, popup: ElementId) {
        if self.active_popup.is_some_and(|(p, _)| p == popup) {
            self.active_popup = None;
        }
    }

    /// The live popup.
    #[must_use]
    pub fn active_popup(&self) -> Option<ElementId> {
        self.active_popup.map(|(p, _)| p)
    }

    // ── Window-modal blocks ─────────────────────────────────────────────

    /// How long a blocker flashes after a press on what it blocks, seconds.
    pub const MODAL_FLASH_SECONDS: f32 = 0.6;

    /// Blocks a top-level window until [`Self::unblock_window`]: it draws
    /// dimmed, presses and wheel steps on it go nowhere, and nothing in it
    /// can take the keyboard. A blocked press flashes `blocker`, brings it
    /// to the front and gives it the keyboard when it accepts focus.
    /// Blocking closes a popup open in the window and takes the pointer
    /// capture and the keyboard out of it.
    pub fn block_window(&mut self, window: ElementId, blocker: ElementId) {
        if self.parent(window) != Some(self.root) {
            return;
        }
        self.window_blocks.entry(window).or_default().push(blocker);
        if let Some((popup, owner)) = self.active_popup
            && self.is_within(Some(popup), window)
        {
            self.active_popup = None;
            self.dismiss_popup(popup, owner);
        }
        if self.is_within(self.captured, window) {
            self.release_capture();
        }
        if self.is_within(self.keyboard_focus, window) {
            self.set_keyboard_focus(None);
        }
    }

    /// Takes one block off.
    pub fn unblock_window(&mut self, window: ElementId, blocker: ElementId) {
        if let Some(blockers) = self.window_blocks.get_mut(&window) {
            blockers.retain(|b| *b != blocker);
            if blockers.is_empty() {
                self.window_blocks.remove(&window);
            }
        }
    }

    /// Whether a top-level window is blocked.
    #[must_use]
    pub fn is_window_blocked(&self, window: ElementId) -> bool {
        self.window_blocks.contains_key(&window)
    }

    /// The top-level elements blocking a window: each dialog box or modal
    /// window that holds another window back.
    #[must_use]
    pub fn window_blockers(&self) -> Vec<ElementId> {
        let mut tops: Vec<ElementId> = self
            .window_blocks
            .values()
            .flatten()
            .map(|b| self.top_level_of(*b))
            .collect();
        tops.sort_unstable();
        tops.dedup();
        tops
    }

    fn top_level_of(&self, element: ElementId) -> ElementId {
        let mut top = element;
        while let Some(parent) = self.parent(top) {
            if parent == self.root {
                break;
            }
            top = parent;
        }
        top
    }

    /// The newest element blocking the window `element` is in.
    fn blocker_for(&self, element: ElementId) -> Option<ElementId> {
        if self.window_blocks.is_empty() {
            return None;
        }
        let top = self.top_level_of(element);
        self.window_blocks.get(&top).and_then(|b| b.last().copied())
    }

    /// Whether an element can take the keyboard now: shown, enabled,
    /// mounted, and not in a blocked window.
    #[must_use]
    pub fn can_focus(&self, element: ElementId) -> bool {
        self.is_available_input_target(element) && self.blocker_for(element).is_none()
    }

    fn flash_blocker(&mut self, blocker: ElementId) {
        self.flashing.insert(blocker, Self::MODAL_FLASH_SECONDS);
        let top = self.top_level_of(blocker);
        if self.parent(top) == Some(self.root) {
            self.bring_to_front(top);
        }
        if self.element(blocker).is_some_and(|e| e.accepts_focus) && self.can_focus(blocker) {
            self.set_keyboard_focus(Some(blocker));
        }
    }

    fn dismiss_popup(&mut self, popup: ElementId, owner: ElementId) {
        self.with_widget(owner, |w, ui| w.on_popup_dismissed(ui, owner, popup));
    }

    fn popup_hit(&mut self, x: i32, y: i32) -> Option<ElementId> {
        let (popup, owner) = self.active_popup?;
        let mut cur = Some(popup);
        while let Some(c) = cur {
            if c == self.root {
                break;
            }
            let stale = match self.element(c) {
                Some(e) => !e.visible || !e.enabled || self.parent(c).is_none(),
                None => true,
            };
            if stale {
                self.active_popup = None;
                self.dismiss_popup(popup, owner);
                return None;
            }
            cur = self.parent(c);
        }
        let (px, py) = self.screen_position(popup);
        self.hit_test(popup, x as f32 - px, y as f32 - py)
    }

    // ── Focus and capture ───────────────────────────────────────────────

    /// Tab from a tab stop: focus moves to the next (Shift: the previous)
    /// visible, enabled tab stop of the same window, in tree order,
    /// wrapping. False when the focus is no tab stop.
    fn move_markup_focus(&mut self, backwards: bool) -> bool {
        let Some(focused) = self.keyboard_focus else {
            return false;
        };
        if !self.element(focused).is_some_and(|e| e.tab_stop) {
            return false;
        }
        let mut window = focused;
        while let Some(parent) = self.parent(window) {
            if parent == self.root {
                break;
            }
            window = parent;
        }
        let mut stops = Vec::new();
        self.collect_tab_stops(window, &mut stops);
        if stops.is_empty() {
            return false;
        }
        let count = stops.len();
        let next = match stops.iter().position(|&s| s == focused) {
            Some(index) if backwards => (index + count - 1) % count,
            Some(index) => (index + 1) % count,
            // Not among the stops (hidden or disabled): as from index -1.
            None if backwards => (count - 2 + count) % count,
            None => 0,
        };
        self.set_keyboard_focus(Some(stops[next]));
        true
    }

    fn collect_tab_stops(&self, id: ElementId, stops: &mut Vec<ElementId>) {
        let Some(element) = self.element(id) else {
            return;
        };
        if !element.visible || !element.enabled {
            return;
        }
        if element.tab_stop && element.accepts_focus {
            stops.push(id);
        }
        for child in self.children(id) {
            self.collect_tab_stops(child, stops);
        }
    }

    /// Give keyboard focus to `e` (or nobody): the old owner hears focus
    /// lost, then the new one focus gained.
    pub fn set_keyboard_focus(&mut self, e: Option<ElementId>) {
        if let Some(el) = e
            && self.blocker_for(el).is_some()
        {
            return;
        }
        if self.keyboard_focus == e {
            return;
        }
        let previous = self.keyboard_focus;
        let mut e = e;
        if let Some(p) = previous {
            // Focus is already gone when the old element hears it is lost,
            // so a handler that hides, removes or refocuses does not lose
            // it again.
            self.keyboard_focus = None;
            let ev = self.event_for(p, kind::FOCUS_LOST);
            self.send_event(p, &ev);
            if let Some(moved) = self.keyboard_focus {
                // The handler moved focus itself; its choice stands.
                self.emit(UiOutput::FocusChanged {
                    old: previous,
                    new: Some(moved),
                });
                self.windows_on_focus_changed(previous, Some(moved));
                return;
            }
            // The handler may have hidden, disabled or removed the
            // destination.
            e = e.filter(|&n| self.is_available_input_target(n));
        }
        self.keyboard_focus = e;
        if let Some(n) = e {
            let ev = self.event_for(n, kind::FOCUS_GAINED);
            self.send_event(n, &ev);
            self.reveal_in_scrolling_ancestors(n);
        }
        self.emit(UiOutput::FocusChanged {
            old: previous,
            new: e,
        });
        self.windows_on_focus_changed(previous, e);
    }

    /// `id` is in the tree, and it and every ancestor up to the root are
    /// visible and enabled.
    fn is_available_input_target(&self, id: ElementId) -> bool {
        let mut current = Some(id);
        while let Some(c) = current {
            let Some(e) = self.element(c) else {
                return false;
            };
            if !e.visible || !e.enabled {
                return false;
            }
            if c == self.root {
                return true;
            }
            current = self.parent(c);
        }
        false
    }

    /// Give pointer capture to `e`; the previous owner hears it lost.
    pub fn set_capture(&mut self, e: ElementId) {
        if self.captured == Some(e) {
            return;
        }
        let previous = self.captured;
        self.captured = Some(e);
        self.notify_capture_lost(previous);
        self.emit(UiOutput::CaptureChanged {
            old: previous,
            new: Some(e),
        });
        self.windows_on_capture_changed(previous, Some(e));
    }

    /// Release pointer capture; restarts the hover and idle clocks.
    pub fn release_capture(&mut self) {
        let previous = self.captured.take();
        self.hover_started_ms = self.now_ms;
        self.last_mouse_move_ms = self.now_ms;
        self.notify_capture_lost(previous);
        if previous.is_some() {
            self.emit(UiOutput::CaptureChanged {
                old: previous,
                new: None,
            });
            self.windows_on_capture_changed(previous, None);
        }
    }

    fn notify_capture_lost(&mut self, previous: Option<ElementId>) {
        if let Some(p) = previous {
            let ev = self.event_for(p, kind::CAPTURE_CHANGED);
            self.send_event(p, &ev);
        }
    }

    /// While `key` stays down, ignore its repeats and the one character it
    /// produces (a key that just opened a text field must not type into it).
    pub fn suppress_physical_key_until_release(&mut self, key: Key) {
        self.suppressed_key = Some(key);
        self.suppressed_char_tail_pending = true;
    }

    // ── Hover and tooltip ───────────────────────────────────────────────

    /// Restart the hover clock (no tooltip until the delay passes again).
    pub fn reset_tooltip_tracking(&mut self) {
        self.hover_started_ms = self.now_ms;
        self.last_mouse_move_ms = self.now_ms;
        self.tooltip_fired = false;
    }

    /// The tooltip text of `id` changed: drop a tooltip it owns and re-arm
    /// the dwell from the last pointer movement.
    pub fn notify_tooltip_text_changed(&mut self, id: ElementId) {
        if self.hover != Some(id) || !self.tooltip_fired {
            return;
        }
        self.emit(UiOutput::TooltipHide(id));
        self.tooltip_fired = false;
        self.hover_started_ms = self.last_mouse_move_ms;
    }

    /// The hovered element.
    #[must_use]
    pub fn hovered(&self) -> Option<ElementId> {
        self.hover
    }

    /// Nothing is hovered any more: the hovered element hears it left, and
    /// a tooltip it showed goes.
    pub fn leave_hover(&mut self) {
        if let Some(hover) = self.hover {
            let e = self.event_for(hover, kind::HOVER_LEAVE);
            self.send_event(hover, &e);
            if self.tooltip_fired {
                self.emit(UiOutput::TooltipHide(hover));
            }
            self.hover = None;
            self.tooltip_fired = false;
        }
    }

    fn update_hover(&mut self, x: i32, y: i32) {
        let w = match self.popup_hit(x, y) {
            Some(h) => Some(h),
            None => self.pick(x, y),
        };
        if w == self.hover {
            if let Some(h) = w
                && self
                    .widget_dyn(h)
                    .is_some_and(|w| w.receives_hover_mouse_move())
            {
                self.dispatch_mouse_move(h, x, y);
            }
            if !self.tooltip_fired {
                self.hover_started_ms = self.now_ms;
            }
            return;
        }
        if let Some(old) = self.hover {
            let ev = self.event_for(old, kind::HOVER_LEAVE);
            self.send_event(old, &ev);
            if self.tooltip_fired {
                self.emit(UiOutput::TooltipHide(old));
            }
        }
        self.hover = w;
        self.hover_started_ms = self.now_ms;
        self.tooltip_fired = false;
        if let Some(n) = w {
            let (sx, sy) = self.screen_position(n);
            let ev = self.event_for(n, kind::HOVER_ENTER).with_data(
                0,
                (x as f32 - sx) as i32,
                (y as f32 - sy) as i32,
            );
            self.send_event(n, &ev);
        }
    }

    fn dispatch_mouse_move(&mut self, target: ElementId, x: i32, y: i32) {
        let (sx, sy) = self.screen_position(target);
        let ev = self.event_for(target, kind::MOUSE_MOVE).with_data(
            0,
            (x as f32 - sx) as i32,
            (y as f32 - sy) as i32,
        );
        self.bubble_event(target, &ev);
    }

    // ── Pointer input ───────────────────────────────────────────────────

    /// The modifier keys now held, which the pointer's events carry from here
    /// on.
    pub fn set_modifiers(&mut self, modifiers: Modifiers) {
        self.modifiers = modifiers;
    }

    /// The pointer moved to window (`x`, `y`).
    pub fn on_mouse_move(&mut self, x: i32, y: i32) {
        let (x, y) = self.map_window_to_canvas(x, y);
        self.mouse = (x, y);
        self.last_mouse_move_ms = self.now_ms;

        if let Some(r) = self.resize {
            let Some(target) = self.element(r.target) else {
                self.resize = None;
                return;
            };
            let mut max_w = target.max_width;
            let mut max_h = target.max_height;
            if target.constrain_resize_to_parent
                && let Some(parent) = self.parent(r.target).and_then(|p| self.element(p))
            {
                max_w = max_w.min(if r.edges.contains(ResizeEdges::LEFT) {
                    r.start.x + r.start.w
                } else {
                    parent.width - r.start.x
                });
                max_h = max_h.min(if r.edges.contains(ResizeEdges::TOP) {
                    r.start.y + r.start.h
                } else {
                    parent.height - r.start.y
                });
            }
            let next = resize_rect(
                r.start,
                r.edges,
                (x - r.mouse.0) as f32,
                (y - r.mouse.1) as f32,
                target.min_width,
                target.min_height,
                target.min_width.max(max_w),
                target.min_height.max(max_h),
            );
            if let Some(t) = self.element_mut(r.target) {
                t.left = next.x;
                t.top = next.y;
                t.width = next.w;
                t.height = next.h;
            }
            self.reset_anchor_capture(r.target);
            return;
        }

        if let Some(d) = self.window_drag {
            let mut left = (x - d.offset.0) as f32;
            let mut top = (y - d.offset.1) as f32;
            if let Some(t) = self.element(d.target)
                && let Some(parent) = self.parent(d.target).and_then(|p| self.element(p))
            {
                left = clamp(left, 0.0, (parent.width - t.width).max(0.0));
                top = clamp(top, 0.0, (parent.height - t.height).max(0.0));
            }
            if let Some(t) = self.element_mut(d.target) {
                t.left = left;
                t.top = top;
            }
            self.reset_anchor_capture(d.target);
            return;
        }

        if let Some(captured) = self.captured {
            self.dispatch_mouse_move(captured, x, y);
            if self.drag_candidate
                && self.drag_source.is_none()
                && ((x - self.press.0).abs() > DRAG_DISTANCE_THRESHOLD
                    || (y - self.press.1).abs() > DRAG_DISTANCE_THRESHOLD)
                && let Some(captured) = self.captured
            {
                self.begin_drag(captured);
            }
            if self.drag_source.is_some() {
                self.update_drag_hover(x, y);
            }
            return;
        }

        if self.world_drag_candidate
            && self.left_down
            && ((x - self.press.0).abs() > DRAG_DISTANCE_THRESHOLD
                || (y - self.press.1).abs() > DRAG_DISTANCE_THRESHOLD)
        {
            self.world_drag_candidate = false;
            let (px, py) = self.press;
            self.emit(UiOutput::WorldDragStart { x: px, y: py });
        }

        self.update_hover(x, y);
        self.emit(UiOutput::WorldMouseMove { x, y });
    }

    /// Lift `payload` off the world: a drag started on the world has no
    /// element to start from, so the root holds it, capturing the pointer,
    /// and the release lands on whatever is under the pointer as any drag
    /// does. Refused (false) once the left button is up or while another
    /// drag runs.
    pub fn begin_world_drag(&mut self, payload: DragPayload, ghost: Option<DragGhost>) -> bool {
        if !self.left_down || self.drag_source.is_some() {
            return false;
        }
        let root = self.root;
        self.drag_source = Some(root);
        self.drag_payload = Some(payload);
        self.drag_ghost = ghost;
        self.set_capture(root);
        let (x, y) = self.mouse;
        self.update_drag_hover(x, y);
        true
    }

    fn set_button(&mut self, b: MouseButton, down: bool) {
        match b {
            MouseButton::Left => self.left_down = down,
            MouseButton::Right => self.right_down = down,
            MouseButton::Middle => self.middle_down = down,
        }
    }

    /// A mouse button went down at window (`x`, `y`).
    pub fn on_mouse_down(&mut self, button: MouseButton, x: i32, y: i32, flags: u32) {
        let (x, y) = self.map_window_to_canvas(x, y);
        self.mouse = (x, y);
        self.set_button(button, true);
        self.press = (x, y);

        // A press off the hovered element ends its hover: the pointer can
        // reach a press with no move between, as when what was under it
        // moved or closed.
        if self.captured.is_none()
            && self.hover.is_some()
            && self.popup_hit(x, y).or_else(|| self.pick(x, y)) != self.hover
        {
            self.leave_hover();
        }

        if let Some(modal) = self.modal
            && !self.contains_absolute(modal, x, y)
        {
            return;
        }

        let target = if let Some((popup, owner)) = self.active_popup {
            match self.popup_hit(x, y) {
                Some(t) => Some(t),
                None => {
                    if self.active_popup.is_some() {
                        // A press outside a live popup dismisses it and is
                        // swallowed.
                        self.active_popup = None;
                        self.dismiss_popup(popup, owner);
                        return;
                    }
                    // The stale popup healed itself: an ordinary press.
                    self.pick(x, y)
                }
            }
        } else {
            self.pick(x, y)
        };

        if let Some(target) = target
            && let Some(blocker) = self.blocker_for(target)
        {
            // A window-modal dialog owns this window: the press, and its
            // release, go nowhere but flash the dialog.
            self.swallowed_releases.push(button);
            self.flash_blocker(blocker);
            return;
        }
        let Some(target) = target else {
            if button == MouseButton::Left {
                self.set_keyboard_focus(None);
            }
            self.world_drag_candidate = button == MouseButton::Left;
            self.emit(UiOutput::WorldMouse {
                button,
                down: true,
                x,
                y,
                flags,
            });
            return;
        };

        // Focus may scroll a scrolling ancestor to reveal the target; the
        // press is reported where it landed, so read the position first.
        let (sx, sy) = self.screen_position(target);
        if button == MouseButton::Left {
            let accepts = self
                .element(target)
                .is_some_and(|e| e.accepts_focus && e.focus_on_mouse_click);
            self.set_keyboard_focus(accepts.then_some(target));
        }
        // Losing focus can close the panel that held this target.
        if !self.is_available_input_target(target) {
            return;
        }
        self.set_capture(target);

        let window = self.find_window(Some(target));
        let handle_window = self.find_drag_handle_window(Some(target));
        if let Some(raise) = window.or(handle_window) {
            self.bring_to_front(raise);
        }
        let raise_window = window.or(handle_window);
        let (target_drag_source, target_captures, target_handles_click) = {
            let node = self.node(target);
            let e = node.map(|n| &n.element);
            let w = node.and_then(|n| n.widget.as_deref());
            (
                match (w, e) {
                    (Some(w), Some(e)) => w.is_drag_source(e),
                    _ => false,
                },
                e.is_some_and(|e| e.captures_pointer_drag),
                match (w, e) {
                    (Some(w), Some(e)) => w.handles_click(e),
                    _ => false,
                },
            )
        };
        if button == MouseButton::Left && raise_window.is_some() && !self.ui_locked {
            let edges = match (self.grip_edges(target), window) {
                (Some(grip), Some(w)) => self.effective_grip_edges(grip, w),
                (Some(_), None) => ResizeEdges::NONE,
                (None, Some(w)) if handle_window.is_none() => self
                    .element(w)
                    .filter(|e| e.resizable)
                    .map_or(ResizeEdges::NONE, |e| hit_edges(e, x, y, RESIZE_GRIP)),
                _ => ResizeEdges::NONE,
            };
            let draggable_window = window.filter(|w| self.element(*w).is_some_and(|e| e.draggable));
            if edges != ResizeEdges::NONE
                && let Some(w) = window
                && let Some(e) = self.element(w)
            {
                self.resize = Some(ResizeDrag {
                    target: w,
                    edges,
                    start: Rect::new(e.left, e.top, e.width, e.height),
                    mouse: (x, y),
                });
                self.drag_candidate = false;
            } else if let Some(h) = handle_window {
                let (hl, ht) = self
                    .element(h)
                    .map_or((0, 0), |e| (e.left as i32, e.top as i32));
                self.window_drag = Some(WindowDrag {
                    target: h,
                    offset: (x - hl, y - ht),
                });
                self.drag_candidate = false;
            } else if target_drag_source {
                self.drag_candidate = true;
            } else if target_captures || target_handles_click {
                self.drag_candidate = false;
            } else if let Some(w) = draggable_window {
                let (wl, wt) = self
                    .element(w)
                    .map_or((0, 0), |e| (e.left as i32, e.top as i32));
                self.window_drag = Some(WindowDrag {
                    target: w,
                    offset: (x - wl, y - wt),
                });
                self.drag_candidate = false;
            } else {
                self.drag_candidate = true;
            }
        } else if target_captures {
            self.drag_candidate = false;
        } else {
            self.drag_candidate = button == MouseButton::Left;
        }

        let raw = match button {
            MouseButton::Left => kind::MOUSE_DOWN,
            MouseButton::Right => kind::RIGHT_DOWN,
            MouseButton::Middle => kind::MIDDLE_DOWN,
        };
        let ev = self.event_for(target, raw).with_data(
            flags as i32,
            (x as f32 - sx) as i32,
            (y as f32 - sy) as i32,
        );
        self.bubble_event(target, &ev);
    }

    /// A mouse button came up at window (`x`, `y`).
    pub fn on_mouse_up(&mut self, button: MouseButton, x: i32, y: i32, flags: u32) {
        let (x, y) = self.map_window_to_canvas(x, y);
        self.mouse = (x, y);
        self.set_button(button, false);
        if let Some(i) = self.swallowed_releases.iter().position(|b| *b == button) {
            self.swallowed_releases.remove(i);
            return;
        }
        if button == MouseButton::Left {
            self.world_drag_candidate = false;
        }

        if let Some(r) = self.resize.take() {
            self.release_capture();
            self.windows_notify_resized(r.target);
            return;
        }
        if let Some(d) = self.window_drag.take() {
            self.release_capture();
            self.windows_notify_moved(d.target);
            return;
        }
        if self.drag_source.is_some() {
            self.finish_drag(x, y);
            self.release_capture();
            self.drag_candidate = false;
            return;
        }

        let Some(target) = self.captured else {
            self.emit(UiOutput::WorldMouse {
                button,
                down: false,
                x,
                y,
                flags,
            });
            return;
        };

        let raw = match button {
            MouseButton::Left => kind::MOUSE_UP,
            MouseButton::Right => kind::RIGHT_UP,
            MouseButton::Middle => kind::MIDDLE_UP,
        };
        let (sx, sy) = self.screen_position(target);
        let (lx, ly) = ((x as f32 - sx) as i32, (y as f32 - sy) as i32);
        let ev = self.event_for(target, raw).with_data(flags as i32, lx, ly);
        self.bubble_event(target, &ev);

        if button == MouseButton::Left && self.contains_absolute(target, x, y) {
            let now = self.now_ms;
            let is_double = self.last_click.is_some_and(|(t, ms, cx, cy)| {
                t == target
                    && now - ms <= DOUBLE_CLICK_DELAY_MS
                    && (x - cx).abs() <= DRAG_DISTANCE_THRESHOLD
                    && (y - cy).abs() <= DRAG_DISTANCE_THRESHOLD
            });
            let click = self
                .event_for(target, kind::CLICK)
                .with_data(flags as i32, lx, ly);
            self.bubble_event(target, &click);
            if is_double {
                let dbl =
                    self.event_for(target, kind::DOUBLE_CLICK)
                        .with_data(flags as i32, lx, ly);
                self.bubble_event(target, &dbl);
            }
            self.last_click = Some((target, now, x, y));
        } else if button == MouseButton::Right
            && self.contains_absolute(target, x, y)
            && (x - self.press.0).abs() <= DRAG_DISTANCE_THRESHOLD
            && (y - self.press.1).abs() <= DRAG_DISTANCE_THRESHOLD
        {
            let click = self
                .event_for(target, kind::RIGHT_CLICK)
                .with_data(flags as i32, lx, ly);
            self.bubble_right_click(target, &click, x, y);
        }

        if self.captured == Some(target) {
            self.release_capture();
        }
        self.drag_candidate = false;
    }

    /// The wheel turned by `dy` notches (positive up) at the pointer: a
    /// vertical step.
    pub fn on_scroll(&mut self, dy: i32) {
        self.on_scroll_xy(0, dy, false);
    }

    /// A wheel event with its horizontal and vertical steps (each -1, 0 or
    /// +1; +1 is left or up) and whether Shift was held. Shift turns a
    /// vertical step into a horizontal one before routing. A vertical step
    /// goes to the element under the pointer and up through its parents
    /// until one takes it, else to the world. A horizontal step goes the
    /// same way as [`kind::SCROLL_HORIZONTAL`], which only a group scrolling
    /// horizontally takes; nothing passes it to the world. A Shift step
    /// that nothing horizontal takes is routed as the vertical step it was;
    /// so is a sideways step with Shift held (what macOS sends for Shift
    /// with a mouse wheel).
    ///
    /// A step the interface takes may move content under the pointer, so
    /// the hover is checked again; and taken anywhere but over an open
    /// popup, it closes the popup, which may have scrolled away with its
    /// group.
    pub fn on_scroll_xy(&mut self, dx: i32, dy: i32, shift: bool) {
        let (mx, my) = self.mouse;
        let over_popup = self.popup_hit(mx, my).is_some();
        let taken = if shift && dx == 0 && dy != 0 {
            self.route_horizontal(dy) || self.route_vertical(dy)
        } else if shift && dy == 0 && dx != 0 {
            self.route_horizontal(dx) || self.route_vertical(dx)
        } else {
            let mut taken = false;
            if dx != 0 {
                taken |= self.route_horizontal(dx);
            }
            if dy != 0 {
                taken |= self.route_vertical(dy);
            }
            taken
        };
        if !taken {
            return;
        }
        if !over_popup && let Some((popup, owner)) = self.active_popup.take() {
            self.dismiss_popup(popup, owner);
        }
        // As a move would: not while a resize, window drag or capture owns
        // the pointer.
        if self.resize.is_none() && self.window_drag.is_none() && self.captured.is_none() {
            self.update_hover(mx, my);
        }
    }

    fn route_horizontal(&mut self, dx: i32) -> bool {
        let (mx, my) = self.mouse;
        let popup = self.popup_hit(mx, my);
        let Some(target) = popup.or_else(|| self.hit_test_top_down(mx, my).map(|h| h.0)) else {
            return false;
        };
        if popup.is_none() && self.blocker_for(target).is_some() {
            return false;
        }
        let (sx, sy) = self.screen_position(target);
        let ev = self.event_for(target, kind::SCROLL_HORIZONTAL).with_data(
            dx.signum(),
            (mx as f32 - sx) as i32,
            (my as f32 - sy) as i32,
        );
        self.bubble_event(target, &ev)
    }

    /// Routes a vertical step; true when the interface took it.
    fn route_vertical(&mut self, dy: i32) -> bool {
        let (mx, my) = self.mouse;
        if let Some(popup_target) = self.popup_hit(mx, my) {
            let (px, py) = self.screen_position(popup_target);
            let ev = self.event_for(popup_target, kind::SCROLL).with_data(
                dy,
                (mx as f32 - px) as i32,
                (my as f32 - py) as i32,
            );
            return self.bubble_event(popup_target, &ev);
        }
        let Some((target, lx, ly)) = self.hit_test_top_down(mx, my) else {
            self.emit(UiOutput::WorldScroll { dy });
            return false;
        };
        if self.blocker_for(target).is_some() {
            // A window-modal dialog owns this window: the step goes nowhere.
            return false;
        }
        let ev = self
            .event_for(target, kind::SCROLL)
            .with_data(dy, lx as i32, ly as i32);
        self.bubble_event(target, &ev)
    }

    /// Scrolls each scrolling ancestor of `id`, innermost first, just far
    /// enough to show it.
    fn reveal_in_scrolling_ancestors(&mut self, id: ElementId) {
        let mut cur = self.parent(id);
        while let Some(p) = cur {
            self.with_widget(p, |w, ui| w.reveal(ui, p, id));
            cur = self.parent(p);
        }
    }

    // ── Keyboard input ──────────────────────────────────────────────────

    /// A key went down (or repeated).
    pub fn on_key_down(&mut self, key: Key, modifiers: Modifiers) {
        if self.suppressed_key == Some(key) {
            return;
        }
        self.suppressed_char_tail_pending = false;

        if self.keyboard_focus.is_none()
            && let Some(input) = self.default_text_input
            && (key == Key::Tab || key == Key::Enter)
        {
            self.set_keyboard_focus(Some(input));
            return;
        }

        if key == Key::Tab && self.move_markup_focus(modifiers.contains(Modifiers::SHIFT)) {
            return;
        }

        if let Some(focus) = self.keyboard_focus {
            let mut ev = self.event_for(focus, kind::KEY_DOWN);
            ev.key = key;
            ev.modifiers = modifiers;
            if self.bubble_event(focus, &ev) {
                return;
            }
        }

        let focus_is_edit = self
            .keyboard_focus
            .and_then(|f| self.element(f))
            .is_some_and(|e| e.is_edit_control);
        if !focus_is_edit {
            let target = self.modal.unwrap_or(self.root);
            let mut ev = self.event_for(target, kind::KEY_DOWN);
            ev.key = key;
            ev.modifiers = modifiers;
            if self.bubble_event(target, &ev) {
                return;
            }
        }
        self.emit(UiOutput::WorldKey { key, modifiers });
    }

    /// A key came up.
    pub fn on_key_up(&mut self, key: Key, modifiers: Modifiers) {
        if self.suppressed_key == Some(key) {
            self.suppressed_key = None;
            self.suppressed_char_tail_pending = false;
            return;
        }
        if let Some(focus) = self.keyboard_focus {
            let mut ev = self.event_for(focus, kind::KEY_UP);
            ev.key = key;
            ev.modifiers = modifiers;
            self.bubble_event(focus, &ev);
        }
    }

    /// A character was typed.
    pub fn on_char(&mut self, c: char) {
        if self.suppressed_char_tail_pending {
            self.suppressed_char_tail_pending = false;
            return;
        }
        let Some(focus) = self.keyboard_focus else {
            return;
        };
        if !self.element(focus).is_some_and(|e| e.is_edit_control) {
            return;
        }
        let ev = self.event_for(focus, kind::CHAR).with_data(c as i32, 0, 0);
        self.bubble_event(focus, &ev);
    }

    // ── Drag and drop ───────────────────────────────────────────────────

    fn begin_drag(&mut self, source: ElementId) {
        let payload = self
            .with_widget(source, |w, ui| w.drag_payload(ui, source))
            .flatten();
        let Some(payload) = payload else {
            self.drag_candidate = false;
            return;
        };
        self.drag_source = Some(source);
        self.drag_payload = Some(payload.clone());
        self.drag_ghost = self
            .with_widget(source, |w, ui| w.drag_ghost(ui, source))
            .flatten();
        let ev = self
            .event_for(source, kind::DRAG_BEGIN)
            .with_payload(Some(payload.clone()));
        self.send_event(source, &ev);
        self.with_widget(source, |w, ui| {
            w.set_drag_source_active(ui, source, true, Some(&payload));
        });
    }

    fn update_drag_hover(&mut self, x: i32, y: i32) {
        let hit = self.hit_test_top_down(x, y);
        let t = hit.map(|h| h.0);
        if t == self.last_drag_hover {
            return;
        }
        let source_id = self.drag_source.map_or(0, |s| self.event_id(s));
        if let Some(old) = self.last_drag_hover {
            let ev = UiEvent {
                source_id,
                target: Some(old),
                kind: kind::DRAG_OVER,
                data1: x,
                data2: y,
                payload: self.drag_payload.clone(),
                ..UiEvent::default()
            };
            self.send_event(old, &ev);
        }
        if let Some((n, lx, ly)) = hit {
            let ev = UiEvent {
                source_id,
                target: Some(n),
                kind: kind::DRAG_ENTER,
                data1: lx as i32,
                data2: ly as i32,
                payload: self.drag_payload.clone(),
                ..UiEvent::default()
            };
            self.send_event(n, &ev);
        }
        self.last_drag_hover = t;
    }

    fn finish_drag(&mut self, x: i32, y: i32) {
        let source = self.drag_source;
        let payload = self.drag_payload.clone();
        if let Some(s) = source {
            self.with_widget(s, |w, ui| {
                w.set_drag_source_active(ui, s, false, payload.as_ref())
            });
        }
        if let Some((t, lx, ly)) = self.hit_test_top_down(x, y) {
            let source_id = source.map_or(0, |s| self.event_id(s));
            let ev = UiEvent {
                source_id,
                target: Some(t),
                kind: kind::DROP_RELEASED,
                data1: lx as i32,
                data2: ly as i32,
                payload: payload.clone(),
                ..UiEvent::default()
            };
            self.send_event(t, &ev);
        } else if let Some(p) = payload {
            self.emit(UiOutput::DragReleasedOutsideUi { payload: p, x, y });
        }
        self.drag_source = None;
        self.drag_payload = None;
        self.drag_ghost = None;
        self.last_drag_hover = None;
    }
}

fn within_border_band(w: &Element, x: i32, y: i32, band: i32) -> bool {
    let (l, t, r, b) = (w.left, w.top, w.left + w.width, w.top + w.height);
    let (x, y, band) = (x as f32, y as f32, band as f32);
    if x < l || x >= r || y < t || y >= b {
        return false;
    }
    x - l < band || r - x <= band || y - t < band || b - y <= band
}

/// Which edges of `w`'s rectangle (in its parent's space) the point is
/// within `grip` pixels of. Corners always offer both their sides; flat
/// runs only the window's resizable edges; locked axes are dropped.
#[must_use]
pub fn hit_edges(w: &Element, x: i32, y: i32, grip: i32) -> ResizeEdges {
    let (l, t, r, b) = (w.left, w.top, w.left + w.width, w.top + w.height);
    let (x, y, g) = (x as f32, y as f32, grip as f32);
    if x < l - g || x > r + g || y < t - g || y > b + g {
        return ResizeEdges::NONE;
    }
    let mut e = ResizeEdges::NONE;
    if (x - l).abs() <= g {
        e |= ResizeEdges::LEFT;
    }
    if (x - r).abs() <= g {
        e |= ResizeEdges::RIGHT;
    }
    if (y - t).abs() <= g {
        e |= ResizeEdges::TOP;
    }
    if (y - b).abs() <= g {
        e |= ResizeEdges::BOTTOM;
    }
    let corner = e.intersects(ResizeEdges::HORIZONTAL) && e.intersects(ResizeEdges::VERTICAL);
    if !corner {
        e &= w.resizable_edges;
    }
    if !w.resize_x {
        e &= !ResizeEdges::HORIZONTAL;
    }
    if !w.resize_y {
        e &= !ResizeEdges::VERTICAL;
    }
    e
}

impl UiRoot {
    /// Raise `window` above every top-level window (its z-order becomes one
    /// more than the highest of the root's other children).
    pub fn bring_to_front(&mut self, window: ElementId) {
        let mut top = self.element(window).map_or(0, |e| e.z_order);
        for sibling in self.children(self.root) {
            if sibling != window
                && let Some(e) = self.element(sibling)
            {
                // Wraps as the client's 32-bit sum does: an element at the
                // top of the range (the status box) is passed over.
                top = top.max(e.z_order.wrapping_add(1));
            }
        }
        self.set_z_order(window, top);
    }

    /// The anchors of an element authored with the given raw edge modes;
    /// see [`crate::element_info::to_anchors`].
    #[must_use]
    pub fn anchors_for(left: u32, top: u32, right: u32, bottom: u32) -> AnchorEdges {
        crate::element_info::to_anchors(left, top, right, bottom)
    }
}

/// How far `parent`'s scrolling moves a child: nothing for its scroll
/// chrome or when it does not scroll.
fn scroll_offset_for(parent: &Element, chrome: bool) -> (f32, f32) {
    match parent.content_viewport {
        Some(_) if !chrome => parent.content_offset,
        _ => (0.0, 0.0),
    }
}
