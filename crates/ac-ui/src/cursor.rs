//! The mouse cursor: which retail cursor the pointer shows. Two layers
//! decide it, as in retail: the global cursor (default, combat, use,
//! examine, busy, target, each with a "found" form over an object) and,
//! over it, the cursor an element authors for its state or the window
//! controls' own (move, resize). The window turns the answer into the OS
//! cursor; this module only decides.

use ac_core::combat::CombatMode;

use crate::element_info::CursorMedia;
use crate::event::DragAcceptance;
use crate::geometry::ResizeEdges;
use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::ItemSlot;

/// The client enumeration (in the master enum map) naming the cursors.
pub const CURSOR_ENUM_TABLE: u32 = 6;

/// What the pointer is doing, for the cursor.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub enum CursorFeedbackKind {
    /// Nothing special.
    #[default]
    Default,
    /// In a combat mode.
    Combat,
    /// The use mode.
    Use,
    /// The examine mode.
    Examine,
    /// Waiting on the server.
    Busy,
    /// Over an editable text.
    Text,
    /// Over (or dragging) a window's move area.
    WindowMove,
    /// On a left or right edge.
    ResizeHorizontal,
    /// On a top or bottom edge.
    ResizeVertical,
    /// On the top-left or bottom-right corner.
    ResizeDiagonalNwse,
    /// On the top-right or bottom-left corner.
    ResizeDiagonalNesw,
    /// Dragging, over nothing that answers.
    Drag,
    /// Dragging over something that takes it.
    DragAccept,
    /// Dragging over something that refuses it.
    DragReject,
    /// Using an item on a target, over no object.
    TargetPending,
    /// Using an item on a target, over one it can be used on.
    TargetValid,
    /// Using an item on a target, over one it cannot.
    TargetInvalid,
}

/// The click mode the cursor shows.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub enum CursorTargetMode {
    /// Clicks select.
    #[default]
    None,
    /// The object clicked is used.
    Use,
    /// The object clicked is examined.
    Examine,
    /// An item is used on the object clicked.
    UseTarget,
}

/// The global cursors: the ones the game picks from the player's state.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub enum GlobalCursorKind {
    /// The arrow.
    #[default]
    Default,
    /// The arrow over an object.
    DefaultFound,
    /// Melee or missile combat.
    MeleeOrMissile,
    /// Melee or missile combat over an object.
    MeleeOrMissileFound,
    /// Magic combat.
    Magic,
    /// Magic combat over an object.
    MagicFound,
    /// The examine mode.
    Examine,
    /// The examine mode over an object.
    ExamineFound,
    /// The use mode.
    Use,
    /// The use mode over an object.
    UseFound,
    /// Waiting on the server.
    Busy,
    /// Waiting on the server, over an object.
    BusyFound,
    /// Choosing a target, over none.
    TargetPending,
    /// Over a target the item can be used on.
    TargetValid,
    /// Over a target it cannot.
    TargetInvalid,
}

/// A global cursor's entry in the cursor enumeration, and its hot spot.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CursorSpec {
    /// The enumeration value.
    pub enum_id: u32,
    /// Hot spot x, pixels.
    pub hotspot_x: i32,
    /// Hot spot y, pixels.
    pub hotspot_y: i32,
}

/// The enumeration entry of global cursor `kind`.
#[must_use]
pub fn global_cursor(kind: GlobalCursorKind) -> CursorSpec {
    use GlobalCursorKind as G;
    let (enum_id, hotspot) = match kind {
        G::Default => (0x01, 0),
        G::DefaultFound => (0x02, 0),
        G::MeleeOrMissile => (0x03, 0),
        G::MeleeOrMissileFound => (0x04, 0),
        G::Magic => (0x05, 0),
        G::MagicFound => (0x06, 0),
        G::Examine => (0x0A, 0),
        G::ExamineFound => (0x0B, 0),
        G::Use => (0x0C, 14),
        G::UseFound => (0x0D, 14),
        G::Busy => (0x0E, 0),
        G::BusyFound => (0x0F, 0),
        G::TargetPending => (0x27, 14),
        G::TargetValid => (0x28, 14),
        G::TargetInvalid => (0x29, 14),
    };
    CursorSpec {
        enum_id,
        hotspot_x: hotspot,
        hotspot_y: hotspot,
    }
}

/// The cursor art of a window control (the retail layout's move and resize
/// cursors), if `kind` is one.
#[must_use]
pub fn window_control_cursor(kind: CursorFeedbackKind) -> Option<CursorMedia> {
    use CursorFeedbackKind as K;
    let file = match kind {
        K::WindowMove => 0x0600_6119,
        K::ResizeHorizontal => 0x0600_6128,
        K::ResizeVertical => 0x0600_5E66,
        K::ResizeDiagonalNwse => 0x0600_6126,
        K::ResizeDiagonalNesw => 0x0600_6127,
        _ => return None,
    };
    Some(CursorMedia {
        file,
        hotspot_x: 16,
        hotspot_y: 16,
    })
}

/// What the cursor shows.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CursorFeedback {
    /// What the pointer is doing.
    pub kind: CursorFeedbackKind,
    /// The widget layer's cursor (an element's or a window control's);
    /// `None` shows the global cursor.
    pub cursor: Option<CursorMedia>,
    /// The global cursor.
    pub global: GlobalCursorKind,
}

/// The facts the cursor is decided from.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CursorSnapshot {
    /// A drag is in the air.
    pub dragging: bool,
    /// What the item cell under the pointer says to the drag.
    pub drag_accept: DragAcceptance,
    /// The edges of the window being resized.
    pub active_resize_edges: ResizeEdges,
    /// The edges a press would resize.
    pub hover_resize_edges: ResizeEdges,
    /// A window is being moved.
    pub window_move_active: bool,
    /// A press would move a window.
    pub hover_window_move: bool,
    /// The pointer is over the UI.
    pub hover_ui: bool,
    /// The pointer is over an editable text.
    pub hover_text_edit: bool,
    /// The object under the pointer (0: none).
    pub hover_target: u32,
    /// Whether the item in target mode can be used on it.
    pub hover_target_compatible: Option<bool>,
    /// Requests and questions in flight.
    pub busy_count: u32,
    /// The click mode.
    pub target_mode: CursorTargetMode,
    /// The combat mode.
    pub combat_mode: CombatMode,
}

/// What the client tells the cursor each frame besides the UI.
pub struct CursorFacts<'a> {
    /// The click mode.
    pub target_mode: CursorTargetMode,
    /// Requests and questions in flight.
    pub busy_count: u32,
    /// The combat mode.
    pub combat_mode: CombatMode,
    /// The object under the pointer in the world (0: none).
    pub world_target: u32,
    /// Whether the item in target mode can be used on an object.
    pub target_compatible: &'a dyn Fn(u32) -> bool,
}

impl std::fmt::Debug for CursorFacts<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CursorFacts")
            .field("target_mode", &self.target_mode)
            .field("busy_count", &self.busy_count)
            .finish_non_exhaustive()
    }
}

/// Decides the cursor.
#[derive(Debug, Default)]
pub struct CursorFeedbackController {
    current: CursorFeedback,
}

impl CursorFeedbackController {
    /// A controller showing the default cursor.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The cursor as last decided.
    #[must_use]
    pub fn current(&self) -> CursorFeedback {
        self.current
    }

    /// Decide the cursor from the UI under the pointer and `facts`.
    pub fn update(&mut self, ui: &mut UiRoot, facts: &CursorFacts<'_>) -> CursorFeedback {
        let (x, y) = ui.mouse();
        let hover = ui.pick(x, y);
        let slot = hover.and_then(|h| hovered_item_slot(ui, h));
        let hover_target = match slot {
            Some(s) => ui.widget::<ItemSlot>(s).map_or(0, |w| w.object_id),
            None => hover
                .and_then(|h| represented_object(ui, h))
                .unwrap_or(facts.world_target),
        };
        let hover_target_compatible = (facts.target_mode == CursorTargetMode::UseTarget
            && hover_target != 0)
            .then(|| (facts.target_compatible)(hover_target));
        let own_edges = |ui: &UiRoot, id: Option<ElementId>| {
            id.and_then(|id| ui.widget_dyn(id))
                .and_then(|w| w.resize_cursor_edges())
                .unwrap_or(ResizeEdges::NONE)
        };
        let active_resize_edges = ui.active_resize_edges() | own_edges(ui, ui.captured());
        let hover_resize_edges = ui.hover_resize_edges() | own_edges(ui, hover);
        let snapshot = CursorSnapshot {
            dragging: ui.drag_payload().is_some(),
            drag_accept: slot
                .and_then(|s| ui.widget::<ItemSlot>(s))
                .map_or(DragAcceptance::None, ItemSlot::drag_acceptance),
            active_resize_edges,
            hover_resize_edges,
            window_move_active: ui.is_window_move_active(),
            hover_window_move: ui.hover_window_move(),
            hover_ui: hover.is_some(),
            hover_text_edit: hover
                .and_then(|h| ui.element(h))
                .is_some_and(|e| e.is_edit_control),
            hover_target,
            hover_target_compatible,
            busy_count: facts.busy_count,
            target_mode: facts.target_mode,
            combat_mode: facts.combat_mode,
        };
        let kind = resolve_kind(&snapshot);
        let authored = authored_cursor(ui, ui.captured(), hover);
        self.current = CursorFeedback {
            kind,
            cursor: effective_cursor(kind, &snapshot, authored),
            global: resolve_global(&snapshot),
        };
        self.current
    }

    /// Decide the cursor from `snapshot` alone (no element cursor).
    pub fn resolve(&mut self, snapshot: &CursorSnapshot) -> CursorFeedback {
        let kind = resolve_kind(snapshot);
        self.current = CursorFeedback {
            kind,
            cursor: effective_cursor(kind, snapshot, None),
            global: resolve_global(snapshot),
        };
        self.current
    }
}

/// The compatibility of the hovered target: the one given, else false.
fn compatible(snapshot: &CursorSnapshot) -> bool {
    snapshot.hover_target_compatible.unwrap_or(false)
}

fn resolve_kind(s: &CursorSnapshot) -> CursorFeedbackKind {
    use CursorFeedbackKind as K;
    if s.dragging {
        return match s.drag_accept {
            DragAcceptance::Accept => K::DragAccept,
            DragAcceptance::Reject => K::DragReject,
            DragAcceptance::None => K::Drag,
        };
    }
    let edges = if s.active_resize_edges == ResizeEdges::NONE {
        s.hover_resize_edges
    } else {
        s.active_resize_edges
    };
    if edges != ResizeEdges::NONE {
        return kind_for_resize(edges);
    }
    if s.window_move_active {
        return K::WindowMove;
    }
    if s.target_mode == CursorTargetMode::UseTarget {
        return if s.hover_target == 0 {
            K::TargetPending
        } else if compatible(s) {
            K::TargetValid
        } else {
            K::TargetInvalid
        };
    }
    if s.busy_count > 0 {
        return K::Busy;
    }
    match s.target_mode {
        CursorTargetMode::Use => return K::Use,
        CursorTargetMode::Examine => return K::Examine,
        _ => {}
    }
    if s.hover_window_move {
        return K::WindowMove;
    }
    if s.hover_text_edit {
        return K::Text;
    }
    if in_combat(s.combat_mode) {
        return K::Combat;
    }
    K::Default
}

fn in_combat(mode: CombatMode) -> bool {
    mode == CombatMode::MELEE || mode == CombatMode::MISSILE || mode == CombatMode::MAGIC
}

fn resolve_global(s: &CursorSnapshot) -> GlobalCursorKind {
    use GlobalCursorKind as G;
    let found = s.hover_target != 0;
    let pick = |plain, over| if found { over } else { plain };
    if s.busy_count > 0 {
        return pick(G::Busy, G::BusyFound);
    }
    match s.target_mode {
        CursorTargetMode::Use => return pick(G::Use, G::UseFound),
        CursorTargetMode::Examine => return pick(G::Examine, G::ExamineFound),
        CursorTargetMode::UseTarget => {
            return if !found {
                G::TargetPending
            } else if compatible(s) {
                G::TargetValid
            } else {
                G::TargetInvalid
            };
        }
        CursorTargetMode::None => {}
    }
    if s.combat_mode == CombatMode::MELEE || s.combat_mode == CombatMode::MISSILE {
        pick(G::MeleeOrMissile, G::MeleeOrMissileFound)
    } else if s.combat_mode == CombatMode::MAGIC {
        pick(G::Magic, G::MagicFound)
    } else {
        pick(G::Default, G::DefaultFound)
    }
}

fn kind_for_resize(edges: ResizeEdges) -> CursorFeedbackKind {
    use CursorFeedbackKind as K;
    let horizontal = edges.intersects(ResizeEdges::LEFT | ResizeEdges::RIGHT);
    let vertical = edges.intersects(ResizeEdges::TOP | ResizeEdges::BOTTOM);
    if horizontal && vertical {
        let nwse = edges.contains(ResizeEdges::LEFT | ResizeEdges::TOP)
            || edges.contains(ResizeEdges::RIGHT | ResizeEdges::BOTTOM);
        return if nwse {
            K::ResizeDiagonalNwse
        } else {
            K::ResizeDiagonalNesw
        };
    }
    if horizontal {
        K::ResizeHorizontal
    } else if vertical {
        K::ResizeVertical
    } else {
        K::Default
    }
}

/// The captured element's authored cursor, else the hovered one's; a
/// window's move handle shows none while the UI is locked.
fn authored_cursor(
    ui: &UiRoot,
    captured: Option<ElementId>,
    hover: Option<ElementId>,
) -> Option<CursorMedia> {
    let of = |id: Option<ElementId>| {
        let id = id?;
        let e = ui.element(id)?;
        if ui.ui_locked() && e.window_move_handle {
            return None;
        }
        ui.active_cursor(id).filter(CursorMedia::is_valid)
    };
    of(captured).or_else(|| of(hover))
}

/// The window control's cursor while it owns the pointer, else the
/// element's authored one, else the control's for `kind`.
fn effective_cursor(
    kind: CursorFeedbackKind,
    s: &CursorSnapshot,
    authored: Option<CursorMedia>,
) -> Option<CursorMedia> {
    let control_owns = s.active_resize_edges != ResizeEdges::NONE
        || s.hover_resize_edges != ResizeEdges::NONE
        || s.window_move_active;
    if control_owns && let Some(c) = window_control_cursor(kind) {
        return Some(c);
    }
    authored.or_else(|| window_control_cursor(kind))
}

fn hovered_item_slot(ui: &UiRoot, from: ElementId) -> Option<ElementId> {
    let mut at = Some(from);
    while let Some(e) = at {
        if ui.widget::<ItemSlot>(e).is_some() {
            return Some(e);
        }
        at = ui.parent(e);
    }
    None
}

fn represented_object(ui: &UiRoot, from: ElementId) -> Option<u32> {
    let mut at = Some(from);
    while let Some(e) = at {
        let guid = ui.widget_dyn(e).map_or(0, |w| w.represented_object());
        if guid != 0 {
            return Some(guid);
        }
        at = ui.parent(e);
    }
    None
}

/// Which cursor layers to set when the cursor changes: the global one
/// when it changed (or first), then the widget layer's when that changed
/// (its own cursor, or the global one again when it has none).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorLayer {
    /// Set the global cursor.
    Global,
    /// Set the widget layer's cursor.
    Widget,
}

/// The layers to set going from the previous cursor to the current one;
/// `previous` is `None` before the first.
#[must_use]
pub fn plan_application(
    previous: Option<(GlobalCursorKind, Option<CursorMedia>)>,
    current_global: GlobalCursorKind,
    current_widget: Option<CursorMedia>,
) -> Vec<CursorLayer> {
    let global_changed = previous.is_none_or(|(g, _)| g != current_global);
    let widget_changed = previous.is_none_or(|(_, w)| w != current_widget);
    let mut layers = Vec::with_capacity(2);
    if global_changed {
        layers.push(CursorLayer::Global);
    }
    if widget_changed {
        let layer = if current_widget.is_some() {
            CursorLayer::Widget
        } else {
            CursorLayer::Global
        };
        if layers.last() != Some(&layer) {
            layers.push(layer);
        }
    }
    layers
}

/// Make the key colour of opaque cursor art transparent: retail cursor
/// surfaces are fully opaque, pure white standing for "nothing here" (as
/// the item icons). Art that already carries transparency is left alone.
pub fn knock_out_key_color(rgba: &mut [u8]) {
    if rgba.chunks_exact(4).any(|p| p[3] != 255) {
        return;
    }
    for p in rgba.chunks_exact_mut(4) {
        if p[0] == 255 && p[1] == 255 && p[2] == 255 {
            p[3] = 0;
        }
    }
}

/// The render surface of global cursor `kind` (through the master enum
/// map's cursor table), with its hot spot; `None` when the dats lack it.
#[must_use]
pub fn resolve_global_cursor(
    dats: &ac_dat::DatCollection,
    kind: GlobalCursorKind,
) -> Option<CursorMedia> {
    let spec = global_cursor(kind);
    let table = crate::panels::appraisal::client_enum_map(dats, CURSOR_ENUM_TABLE)?;
    let file = table.client_id(spec.enum_id)?;
    (file != 0).then_some(CursorMedia {
        file,
        hotspot_x: spec.hotspot_x,
        hotspot_y: spec.hotspot_y,
    })
}
