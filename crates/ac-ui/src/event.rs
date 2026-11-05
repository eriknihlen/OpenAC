//! Input and element events: what the window hands the UI (plain data, no
//! window types) and what the UI hands its elements.

use std::any::Any;
use std::fmt;
use std::sync::Arc;

use crate::tree::ElementId;

/// Element event kinds. The numbers follow the client's message ids where
/// it has them (Windows message numbers for raw input).
pub mod kind {
    /// A completed click (press and release over the same element).
    pub const CLICK: i32 = 0x01;
    /// The pointer entered the element.
    pub const HOVER_ENTER: i32 = 0x05;
    /// The pointer left the element.
    pub const HOVER_LEAVE: i32 = 0x06;
    /// The hover delay passed: show the tooltip.
    pub const TOOLTIP: i32 = 0x07;
    /// A second click soon after the first.
    pub const DOUBLE_CLICK: i32 = 0x08;
    /// Mouse wheel; `data0` is the wheel delta (positive up).
    pub const SCROLL: i32 = 0x0A;
    /// A horizontal wheel step (a sideways swipe, a tilt wheel, or Shift
    /// with the wheel); `data0` is +1 toward the start (left), -1 toward
    /// the end.
    pub const SCROLL_HORIZONTAL: i32 = 0x0B;
    /// A completed right click.
    pub const RIGHT_CLICK: i32 = 0x0E;
    /// A drag began from this element.
    pub const DRAG_BEGIN: i32 = 0x15;
    /// A drag left this element (sent on leave).
    pub const DRAG_OVER: i32 = 0x1C;
    /// A drag entered this element.
    pub const DRAG_ENTER: i32 = 0x21;
    /// Keyboard focus left this element.
    pub const FOCUS_LOST: i32 = 0x28;
    /// Keyboard focus came to this element.
    pub const FOCUS_GAINED: i32 = 0x29;
    /// A drag was released over this element.
    pub const DROP_RELEASED: i32 = 0x3E;
    /// Pointer moved; `data1`/`data2` are element-local.
    pub const MOUSE_MOVE: i32 = 0x200;
    /// Left button down.
    pub const MOUSE_DOWN: i32 = 0x201;
    /// Left button up.
    pub const MOUSE_UP: i32 = 0x202;
    /// Left double click (raw).
    pub const DOUBLE_CLICK_LEFT: i32 = 0x203;
    /// Right button down.
    pub const RIGHT_DOWN: i32 = 0x204;
    /// Right button up.
    pub const RIGHT_UP: i32 = 0x205;
    /// Middle button down.
    pub const MIDDLE_DOWN: i32 = 0x207;
    /// Middle button up.
    pub const MIDDLE_UP: i32 = 0x208;
    /// Pointer capture moved away from this element.
    pub const CAPTURE_CHANGED: i32 = 0x215;
    /// Key pressed; the event's `key` says which.
    pub const KEY_DOWN: i32 = 0x100;
    /// Key released.
    pub const KEY_UP: i32 = 0x101;
    /// Open the context menu this is sent to at the screen point in
    /// `data1`/`data2` (a right click its owner left unhandled); the menu
    /// answers whether it opened.
    pub const CONTEXT_MENU_OPEN: i32 = 0x7F01;
    /// A typed character; `data0` is the code point.
    pub const CHAR: i32 = 0x102;
}

/// Mouse buttons.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MouseButton {
    /// Left (primary).
    Left = 1,
    /// Right.
    Right = 2,
    /// Middle.
    Middle = 3,
}

/// Modifier keys held during an input event.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Modifiers(pub u8);

impl Modifiers {
    /// None held.
    pub const NONE: Self = Self(0);
    /// Shift.
    pub const SHIFT: Self = Self(0x01);
    /// Control.
    pub const CTRL: Self = Self(0x02);
    /// Alt.
    pub const ALT: Self = Self(0x04);
    /// Windows / command.
    pub const WIN: Self = Self(0x08);

    /// True when every modifier in `other` is held.
    #[must_use]
    pub fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

impl std::ops::BitOr for Modifiers {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

/// Physical keys, by position on a US keyboard. The window maps its own key
/// codes to these.
#[allow(missing_docs)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Key {
    #[default]
    Unknown,
    Space,
    Apostrophe,
    Comma,
    Minus,
    Period,
    Slash,
    Number0,
    Number1,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,
    Semicolon,
    Equal,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    LeftBracket,
    BackSlash,
    RightBracket,
    GraveAccent,
    Escape,
    Enter,
    Tab,
    Backspace,
    Insert,
    Delete,
    Right,
    Left,
    Down,
    Up,
    PageUp,
    PageDown,
    Home,
    End,
    CapsLock,
    ScrollLock,
    NumLock,
    PrintScreen,
    Pause,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,
    KeypadDecimal,
    KeypadDivide,
    KeypadMultiply,
    KeypadSubtract,
    KeypadAdd,
    KeypadEnter,
    KeypadEqual,
    ShiftLeft,
    ControlLeft,
    AltLeft,
    SuperLeft,
    ShiftRight,
    ControlRight,
    AltRight,
    SuperRight,
    Menu,
}

/// What a drag carries. Any value; drop targets downcast to the kinds they
/// accept (usually [`ItemDragPayload`]).
#[derive(Clone)]
pub struct DragPayload(pub Arc<dyn Any + Send + Sync>);

impl DragPayload {
    /// Wrap a value.
    pub fn new<T: Any + Send + Sync>(value: T) -> Self {
        Self(Arc::new(value))
    }

    /// The value as `T`, if it is one.
    #[must_use]
    pub fn get<T: Any>(&self) -> Option<&T> {
        self.0.downcast_ref()
    }

    /// True when both name the same payload object.
    #[must_use]
    pub fn same(&self, other: &DragPayload) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

impl fmt::Debug for DragPayload {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("DragPayload(..)")
    }
}

/// What kind of slot an item drag left.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ItemDragSource {
    /// A pack slot.
    Inventory,
    /// A toolbar shortcut slot.
    ShortcutBar,
    /// An equipment slot.
    Equipment,
    /// The ground or an outside container.
    Ground,
    /// A salvage window cell: a pack item staged there. Dragging it out
    /// only unstages it.
    Salvage,
    /// A vendor window or house purchase window cell: a pack item staged
    /// there to sell or to pay with. Dragging it out only unstages it.
    Vendor,
}

impl ItemDragSource {
    /// Whether the drag stands for an item rather than carrying it: the
    /// world and the item windows refuse it, so letting it go anywhere
    /// moves nothing.
    #[must_use]
    pub fn is_alias(self) -> bool {
        matches!(self, Self::ShortcutBar | Self::Salvage | Self::Vendor)
    }
}

/// An item being dragged.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItemDragPayload {
    /// Object id of the item.
    pub obj_id: u32,
    /// What kind of slot it left.
    pub source_kind: ItemDragSource,
    /// Slot index it left.
    pub source_slot: i32,
    /// The slot element it was lifted from (none when lifted from an image
    /// region rather than a slot).
    pub source_cell: Option<ElementId>,
    /// The raw shortcut record a toolbar cell carries (a shortcut alias
    /// drag changes the shortcut, never the item).
    pub shortcut: Option<ac_core::items::ShortcutEntry>,
}

impl ItemDragPayload {
    /// A payload for `obj_id` lifted from `source_kind` slot `source_slot`.
    #[must_use]
    pub fn new(obj_id: u32, source_kind: ItemDragSource, source_slot: i32) -> Self {
        Self {
            obj_id,
            source_kind,
            source_slot,
            source_cell: None,
            shortcut: None,
        }
    }
}

/// A drop target's answer while a drag hovers it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum DragAcceptance {
    /// No opinion (neutral look).
    #[default]
    None,
    /// The drop would be accepted.
    Accept,
    /// The drop would be refused.
    Reject,
}

/// One event delivered to an element (and bubbled to its ancestors until
/// one handles it).
#[derive(Debug, Clone, Default)]
pub struct UiEvent {
    /// Event id of the element the event came from (the drag source for
    /// drag events).
    pub source_id: u32,
    /// The element the event is aimed at (coordinates are local to it).
    pub target: Option<ElementId>,
    /// Event kind, one of [`kind`].
    pub kind: i32,
    /// First argument (button flags, wheel delta, code point).
    pub data0: i32,
    /// Second argument (x, local to the target).
    pub data1: i32,
    /// Third argument (y, local to the target).
    pub data2: i32,
    /// Fourth argument.
    pub data3: i32,
    /// The key of a key event.
    pub key: Key,
    /// Modifiers held.
    pub modifiers: Modifiers,
    /// Drag payload of drag events.
    pub payload: Option<DragPayload>,
}

impl UiEvent {
    /// An event of `kind` aimed at `target`.
    #[must_use]
    pub fn new(source_id: u32, target: ElementId, kind: i32) -> Self {
        Self {
            source_id,
            target: Some(target),
            kind,
            ..Self::default()
        }
    }

    /// With the given arguments.
    #[must_use]
    pub fn with_data(mut self, data0: i32, data1: i32, data2: i32) -> Self {
        self.data0 = data0;
        self.data1 = data1;
        self.data2 = data2;
        self
    }

    /// With a drag payload.
    #[must_use]
    pub fn with_payload(mut self, payload: Option<DragPayload>) -> Self {
        self.payload = payload;
        self
    }
}
