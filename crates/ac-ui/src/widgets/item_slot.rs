//! The item slot: one cell of an item list showing an item's composed
//! icon (or the empty-slot image) and the marks drawn over it: sale and
//! trade markers, the shortcut number, the pack fullness and structure
//! meters, the waiting mesh, the open-pack triangle, the selection square,
//! the drag accept/refuse overlay and the cooldown sweep.
//!
//! The slot never decides what a press or a drop means. It tells its
//! owner with element messages: a press ([`ElementMessage::Pressed`]),
//! a click, double click or right click, a drag lifted from it
//! ([`ElementMessage::ItemDragLifted`]), a drag entering it
//! ([`ElementMessage::ItemDragEntered`]) and a drop on it
//! ([`ElementMessage::ItemDropped`]). The owner answers a drag entering by
//! setting [`ItemSlot::set_drag_acceptance`] (until it does, the slot shows
//! the refusal, as a slot with no drag handler does), and may consume a
//! press ([`ItemSlot::consume_primary_press`]) so the click that follows
//! does nothing and no drag starts from it.

use std::sync::Arc;

use ac_core::items::ShortcutEntry;

use crate::draw::{Color, RenderContext, TextureRef};
use crate::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::tooltip::{SHARED_POPUP_SKIN_LAYOUT_DID, SHARED_POPUP_SKIN_ROOT_ELEMENT_ID};
use crate::tree::{DragGhost, Element, ElementId, Widget};

/// The shared item-slot image of an empty cell (the toolbar border).
pub const DEFAULT_EMPTY_SPRITE: u32 = 0x0600_74CF;
/// The waiting mesh over an item whose request is in flight.
pub const DEFAULT_WAITING_SPRITE: u32 = 0x0600_109A;
/// A drop here would be taken (green).
pub const DEFAULT_DRAG_ACCEPT_SPRITE: u32 = 0x0600_11F9;
/// A drop on a pack icon would be taken.
pub const PACK_DRAG_ACCEPT_SPRITE: u32 = 0x0600_11F7;
/// A drop here would be refused (red).
pub const DEFAULT_DRAG_REJECT_SPRITE: u32 = 0x0600_11F8;
/// The triangle on the open pack.
pub const DEFAULT_OPEN_CONTAINER_SPRITE: u32 = 0x0600_5D9C;
/// The selection square.
pub const DEFAULT_SELECTED_SPRITE: u32 = 0x0600_4D21;
/// Pack fullness meter, back.
pub const DEFAULT_CAPACITY_BACK_SPRITE: u32 = 0x0600_4D22;
/// Pack fullness meter, front.
pub const DEFAULT_CAPACITY_FRONT_SPRITE: u32 = 0x0600_4D23;
/// Structure meter, back.
pub const DEFAULT_STRUCTURE_BACK_SPRITE: u32 = 0x0600_4D24;
/// Structure meter, front.
pub const DEFAULT_STRUCTURE_FRONT_SPRITE: u32 = 0x0600_4D25;
/// The marker over an item staged for sale.
pub const SELL_OVERLAY_SPRITE: u32 = 0x0600_12D9;
/// The marker over an item staged for trade.
pub const TRADE_OVERLAY_SPRITE: u32 = 0x0600_1DAE;

/// Pixels of a dedicated drag icon (the icon without its type underlay).
pub const DRAG_ICON_EXTENT: i32 = 32;

/// The shortcut number images: the regular, magic-mode (ghosted) and
/// empty-slot digit sets, each indexed by slot (0 = "1").
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ShortcutDigits {
    /// Digits over an occupied slot.
    pub regular: Vec<u32>,
    /// Digits over an occupied slot in magic mode.
    pub ghosted: Vec<u32>,
    /// Digits over an empty slot (`None`: empty slots show none).
    pub empty: Option<Vec<u32>>,
}

/// One item slot. See the module documentation.
#[derive(Debug, Clone, PartialEq)]
pub struct ItemSlot {
    /// The item's object id (0: empty).
    pub object_id: u32,
    /// The item's composed icon (with its type underlay).
    pub icon: Option<TextureRef>,
    /// The icon a drag shows (without the underlay); `None` drags `icon`.
    pub drag_icon: Option<TextureRef>,
    /// The raw shortcut record a toolbar cell carries (dragged with it).
    pub shortcut: Option<ShortcutEntry>,
    /// The slot's index in its list (the drag payload's source slot).
    pub slot_index: i32,
    /// What kind of slot a drag leaves.
    pub source_kind: ItemDragSource,
    /// The overlay while a drop here would be taken.
    pub drag_accept_sprite: u32,
    /// The overlay while a drop here would be refused.
    pub drag_reject_sprite: u32,
    /// The cell is the open pack.
    pub is_open_container: bool,
    /// The open-pack triangle.
    pub open_container_sprite: u32,
    /// The cell holds the selected object.
    pub selected: bool,
    /// The selection square.
    pub selected_sprite: u32,
    /// Pack fullness 0..=1 (negative: hidden).
    pub capacity_fill: f32,
    /// Fullness meter back.
    pub capacity_back_sprite: u32,
    /// Fullness meter front.
    pub capacity_front_sprite: u32,
    /// Structure 0..=1 (negative: hidden).
    pub structure_fill: f32,
    /// Structure meter back.
    pub structure_back_sprite: u32,
    /// Structure meter front.
    pub structure_front_sprite: u32,
    /// The image of an empty cell.
    pub empty_sprite: u32,
    /// The waiting mesh.
    pub waiting_sprite: u32,
    /// Show the sale marker.
    pub show_sell_overlay: bool,
    /// The sale marker.
    pub sell_overlay_sprite: u32,
    /// Show the trade marker.
    pub show_trade_overlay: bool,
    /// The trade marker.
    pub trade_overlay_sprite: u32,
    /// The ten cooldown sweep images (step 1..=10).
    pub cooldown_sprites: Vec<u32>,
    /// The item's cooldown step now (0: none), set by the owner each frame.
    pub cooldown_step: i32,
    /// An occupied slot may start a drag.
    pub allow_drag_source: bool,
    /// The shortcut number images.
    pub digits: Option<Arc<ShortcutDigits>>,
    /// The hover caption of the item (shown only while occupied).
    pub tooltip: Option<String>,
    /// What the slot keeps itself (read through its methods).
    pub state: SlotState,
}

/// The slot's own state: the drag overlay, the waiting mesh, a consumed
/// press and the shortcut number.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SlotState {
    drag_accept: DragAcceptance,
    /// A drag is over the slot now (entered and not yet left or dropped).
    drag_hovered: bool,
    waiting: bool,
    primary_press_consumed: bool,
    shortcut_num: i32,
    shortcut_ghosted: bool,
}

impl Default for SlotState {
    fn default() -> Self {
        Self {
            drag_accept: DragAcceptance::None,
            drag_hovered: false,
            waiting: false,
            primary_press_consumed: false,
            shortcut_num: -1,
            shortcut_ghosted: false,
        }
    }
}

/// The cell type item lists build (the item slot).
pub type ItemCell = ItemSlot;

impl Default for ItemSlot {
    fn default() -> Self {
        Self {
            object_id: 0,
            icon: None,
            drag_icon: None,
            shortcut: None,
            slot_index: -1,
            source_kind: ItemDragSource::Inventory,
            drag_accept_sprite: DEFAULT_DRAG_ACCEPT_SPRITE,
            drag_reject_sprite: DEFAULT_DRAG_REJECT_SPRITE,
            is_open_container: false,
            open_container_sprite: DEFAULT_OPEN_CONTAINER_SPRITE,
            selected: false,
            selected_sprite: DEFAULT_SELECTED_SPRITE,
            capacity_fill: -1.0,
            capacity_back_sprite: DEFAULT_CAPACITY_BACK_SPRITE,
            capacity_front_sprite: DEFAULT_CAPACITY_FRONT_SPRITE,
            structure_fill: -1.0,
            structure_back_sprite: DEFAULT_STRUCTURE_BACK_SPRITE,
            structure_front_sprite: DEFAULT_STRUCTURE_FRONT_SPRITE,
            empty_sprite: DEFAULT_EMPTY_SPRITE,
            waiting_sprite: DEFAULT_WAITING_SPRITE,
            show_sell_overlay: false,
            sell_overlay_sprite: SELL_OVERLAY_SPRITE,
            show_trade_overlay: false,
            trade_overlay_sprite: TRADE_OVERLAY_SPRITE,
            cooldown_sprites: Vec::new(),
            cooldown_step: 0,
            allow_drag_source: true,
            digits: None,
            tooltip: None,
            state: SlotState::default(),
        }
    }
}

/// The element a fresh slot sits in: takes presses, and shows its
/// tooltip in the shared popup skin.
#[must_use]
pub fn slot_element() -> Element {
    let mut e = Element::default();
    e.authored.tooltip_root_element_id = SHARED_POPUP_SKIN_ROOT_ELEMENT_ID;
    e.authored.tooltip_layout_did = SHARED_POPUP_SKIN_LAYOUT_DID;
    e
}

impl ItemSlot {
    /// Show `object_id` with its icons and (for a toolbar cell) its raw
    /// shortcut record.
    pub fn set_item(
        &mut self,
        object_id: u32,
        icon: Option<TextureRef>,
        drag_icon: Option<TextureRef>,
        shortcut: Option<ShortcutEntry>,
    ) {
        self.object_id = object_id;
        self.icon = icon;
        self.drag_icon = drag_icon;
        self.shortcut = shortcut;
    }

    /// Empty the slot (it stops waiting and forgets a consumed press).
    pub fn clear(&mut self) {
        self.object_id = 0;
        self.icon = None;
        self.drag_icon = None;
        self.shortcut = None;
        self.state.waiting = false;
        self.state.primary_press_consumed = false;
    }

    /// The structure meter: shown only below full (`structure` <
    /// `max_structure`, with a positive maximum).
    pub fn set_structure(&mut self, structure: i32, max_structure: i32) {
        self.structure_fill = if max_structure > 0 && structure < max_structure {
            (structure as f32 / max_structure as f32).clamp(0.0, 1.0)
        } else {
            -1.0
        };
    }

    /// Show (or stop showing) the waiting mesh; an empty slot never waits.
    pub fn set_waiting(&mut self, waiting: bool) {
        self.state.waiting = waiting && self.object_id != 0;
    }

    /// The waiting mesh is showing.
    #[must_use]
    pub fn waiting(&self) -> bool {
        self.state.waiting
    }

    /// The owner's answer to a drag over the slot. The owner answers the
    /// drag's entry message later (at its next update); an answer that
    /// arrives after the drag left the slot or was dropped (an entry and a
    /// leave in one frame) is stale and is not shown.
    pub fn set_drag_acceptance(&mut self, acceptance: DragAcceptance) {
        if self.state.drag_hovered {
            self.state.drag_accept = acceptance;
        }
    }

    /// The accept/refuse overlay showing.
    #[must_use]
    pub fn drag_acceptance(&self) -> DragAcceptance {
        self.state.drag_accept
    }

    /// The owner took the press that just went down (a target-mode
    /// click): the click that follows does nothing and no drag starts.
    pub fn consume_primary_press(&mut self) {
        self.state.primary_press_consumed = self.object_id != 0;
    }

    /// The last press was consumed by the owner.
    #[must_use]
    pub fn primary_press_consumed(&self) -> bool {
        self.state.primary_press_consumed
    }

    /// Stamp shortcut number `index` (0 = "1"), ghosted in magic mode.
    pub fn set_shortcut_num(&mut self, index: i32, ghosted: bool) {
        self.state.shortcut_num = index;
        self.state.shortcut_ghosted = ghosted;
    }

    /// Remove the shortcut number.
    pub fn clear_shortcut_num(&mut self) {
        self.state.shortcut_num = -1;
    }

    /// The shortcut number stamped (-1: none).
    #[must_use]
    pub fn shortcut_num(&self) -> i32 {
        self.state.shortcut_num
    }

    /// The number is ghosted.
    #[must_use]
    pub fn shortcut_ghosted(&self) -> bool {
        self.state.shortcut_ghosted
    }

    /// The digit set the number draws from: empty digits for an empty
    /// slot, else ghosted or regular by the stance.
    #[must_use]
    pub fn active_digits(&self) -> Option<&[u32]> {
        let d = self.digits.as_deref()?;
        if self.object_id != 0 {
            Some(if self.state.shortcut_ghosted {
                &d.ghosted
            } else {
                &d.regular
            })
        } else {
            d.empty.as_deref()
        }
    }

    /// The cooldown sweep image for the current step (0: none).
    #[must_use]
    pub fn active_cooldown_sprite(&self) -> u32 {
        if self.object_id == 0 || !(1..=10).contains(&self.cooldown_step) {
            return 0;
        }
        self.cooldown_sprites
            .get(self.cooldown_step as usize - 1)
            .copied()
            .unwrap_or(0)
    }

    /// What a drag from this slot carries (`None` while empty, not a drag
    /// source, or after a consumed press).
    #[must_use]
    pub fn payload(&self, me: Option<ElementId>) -> Option<ItemDragPayload> {
        (self.allow_drag_source && self.object_id != 0 && !self.state.primary_press_consumed)
            .then_some(ItemDragPayload {
                obj_id: self.object_id,
                source_kind: self.source_kind,
                source_slot: self.slot_index,
                source_cell: me,
                shortcut: self.shortcut,
            })
    }

    /// The ghost a drag shows: the dedicated drag icon at 32 px, else the
    /// icon at the cell's size.
    #[must_use]
    pub fn ghost(&self, width: f32, height: f32) -> Option<DragGhost> {
        if self.object_id == 0 {
            return None;
        }
        if let Some(t) = self.drag_icon {
            return Some(DragGhost {
                texture: t,
                width: DRAG_ICON_EXTENT,
                height: DRAG_ICON_EXTENT,
            });
        }
        self.icon.map(|t| DragGhost {
            texture: t,
            width: width as i32,
            height: height as i32,
        })
    }

    fn draw_meter(rc: &mut RenderContext<'_>, width: f32, fill: f32, back: u32, front: u32) {
        if fill < 0.0 {
            return;
        }
        let (by, bw, bh) = (1.0, 5.0, 30.0);
        let bx = width - bw;
        stretch(rc, back, bx, by, bw, bh);
        let f = fill.clamp(0.0, 1.0);
        if f > 0.0 && front != 0 {
            let fh = bh * f;
            rc.sprite(
                TextureRef::Surface(front),
                bx,
                by + (bh - fh),
                bw,
                fh,
                (0.0, 1.0 - f, 1.0, 1.0),
                Color::WHITE,
            );
        }
    }
}

/// Draw render surface `sprite` stretched over a local rectangle (nothing
/// for 0).
pub fn stretch(rc: &mut RenderContext<'_>, sprite: u32, x: f32, y: f32, w: f32, h: f32) {
    if sprite != 0 {
        rc.sprite(
            TextureRef::Surface(sprite),
            x,
            y,
            w,
            h,
            (0.0, 0.0, 1.0, 1.0),
            Color::WHITE,
        );
    }
}

fn item_payload(e: &UiEvent) -> Option<&ItemDragPayload> {
    e.payload.as_ref().and_then(|p| p.get::<ItemDragPayload>())
}

impl Widget for ItemSlot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        match e.kind {
            kind::MOUSE_DOWN => {
                self.state.primary_press_consumed = false;
                if self.object_id != 0 {
                    ui.send_message(me, ElementMessage::Pressed);
                }
                true
            }
            kind::CLICK => {
                if !self.state.primary_press_consumed {
                    ui.send_message(
                        me,
                        ElementMessage::Clicked {
                            x: e.data1,
                            y: e.data2,
                        },
                    );
                }
                true
            }
            kind::DOUBLE_CLICK => {
                if !self.state.primary_press_consumed {
                    ui.send_message(me, ElementMessage::DoubleClicked);
                }
                true
            }
            kind::RIGHT_CLICK => {
                if self.object_id != 0 {
                    ui.send_message(me, ElementMessage::RightClicked);
                }
                true
            }
            kind::DRAG_BEGIN => {
                if let Some(p) = item_payload(e) {
                    ui.send_message(me, ElementMessage::ItemDragLifted(p.clone()));
                }
                true
            }
            kind::DRAG_ENTER => {
                // Refused until the owner says otherwise (a slot nobody
                // answers for refuses, as one with no drag handler does).
                self.state.drag_accept = DragAcceptance::Reject;
                self.state.drag_hovered = true;
                if let Some(p) = item_payload(e) {
                    ui.send_message(me, ElementMessage::ItemDragEntered(p.clone()));
                }
                true
            }
            kind::DRAG_OVER => {
                self.state.drag_accept = DragAcceptance::None;
                self.state.drag_hovered = false;
                true
            }
            kind::DROP_RELEASED => {
                self.state.drag_accept = DragAcceptance::None;
                self.state.drag_hovered = false;
                if let Some(p) = item_payload(e) {
                    ui.send_message(me, ElementMessage::ItemDropped(p.clone()));
                }
                true
            }
            _ => false,
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        match self.icon {
            Some(t) if self.object_id != 0 => {
                rc.sprite(t, 0.0, 0.0, w, h, (0.0, 0.0, 1.0, 1.0), Color::WHITE);
            }
            _ => stretch(rc, self.empty_sprite, 0.0, 0.0, w, h),
        }
        if self.show_sell_overlay && self.object_id != 0 {
            stretch(rc, self.sell_overlay_sprite, 0.0, 0.0, w, h);
        }
        if self.show_trade_overlay && self.object_id != 0 {
            stretch(rc, self.trade_overlay_sprite, 0.0, 0.0, w, h);
        }
        if self.state.shortcut_num >= 0
            && let Some(digit) = self
                .active_digits()
                .and_then(|d| d.get(self.state.shortcut_num as usize).copied())
        {
            stretch(rc, digit, 0.0, 0.0, w, h);
        }
        Self::draw_meter(
            rc,
            w,
            self.capacity_fill,
            self.capacity_back_sprite,
            self.capacity_front_sprite,
        );
        // Full structure hides its meter; depleted structure keeps the rail.
        if (0.0..1.0).contains(&self.structure_fill) {
            Self::draw_meter(
                rc,
                w,
                self.structure_fill,
                self.structure_back_sprite,
                self.structure_front_sprite,
            );
        }
        if self.state.waiting {
            stretch(rc, self.waiting_sprite, 0.0, 0.0, w, h);
        }
        if self.is_open_container {
            stretch(rc, self.open_container_sprite, 0.0, 0.0, w, h);
        }
        if self.selected {
            stretch(rc, self.selected_sprite, 0.0, 0.0, w, h);
        }
        match self.state.drag_accept {
            DragAcceptance::Accept => stretch(rc, self.drag_accept_sprite, 0.0, 0.0, w, h),
            DragAcceptance::Reject => stretch(rc, self.drag_reject_sprite, 0.0, 0.0, w, h),
            DragAcceptance::None => {}
        }
        let cooldown = self.active_cooldown_sprite();
        stretch(rc, cooldown, 0.0, 0.0, w, h);
    }

    fn handles_click(&self, _element: &Element) -> bool {
        self.object_id != 0
    }

    fn is_drag_source(&self, _element: &Element) -> bool {
        self.object_id != 0 && self.allow_drag_source
    }

    fn drag_payload(&mut self, _ui: &mut UiRoot, me: ElementId) -> Option<DragPayload> {
        self.payload(Some(me)).map(DragPayload::new)
    }

    fn drag_ghost(&mut self, ui: &mut UiRoot, me: ElementId) -> Option<DragGhost> {
        let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
        self.ghost(w, h)
    }

    fn set_drag_source_active(
        &mut self,
        _ui: &mut UiRoot,
        _me: ElementId,
        active: bool,
        _payload: Option<&DragPayload>,
    ) {
        self.set_waiting(active && self.source_kind != ItemDragSource::ShortcutBar);
    }

    fn tooltip_text(&self, element: &Element) -> Option<String> {
        if self.object_id == 0 {
            return None;
        }
        self.tooltip
            .clone()
            .filter(|t| !t.trim().is_empty())
            .or_else(|| {
                element
                    .runtime_tooltip_text
                    .clone()
                    .filter(|t| !t.trim().is_empty())
            })
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn is_empty_slot(&self) -> bool {
        self.object_id == 0 && self.icon.is_none()
    }
}
