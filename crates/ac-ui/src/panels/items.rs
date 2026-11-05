//! What the item panels (inventory, paperdoll, toolbar) read, and the
//! pieces they share: filling an item slot from an object, the hover
//! caption, the shortcut number stamp, clickable chrome and captions.
//!
//! The panels read the object table itself ([`ClientObjectTable`], the
//! same plain table the runtime keeps) through an [`ItemsView`] the client
//! hands them each update, together with the few interaction facts the
//! runtime owns (the selection, the target mode, the requests in flight).
//! What the player does in a panel is queued as the panel's element
//! messages and resolved against the next view.

use std::sync::Arc;

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObject, ClientObjectTable, ShortcutStore, StackSplitQuantity};

use ac_runtime::inventory::{ItemCommand, ObjectRef, PendingPlacement};
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::icons::IconSource;
use crate::panels::PanelOutput;
use crate::root::{UiOutput, UiRoot};
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Button, DatElement, ItemSlot, ShortcutDigits, Text};

/// Shortcut slots that carry a number (the top row, keys 1..9).
pub const NUMBERED_SHORTCUT_SLOTS: i32 = 9;

/// Caption colour of the panels' own text.
pub const CAPTION_COLOR: Color = Color::WHITE;

/// Composes an item's displayed name (the material prefix included).
pub type NameResolver<'a> = &'a dyn Fn(&ClientObject) -> String;

/// Everything an item panel reads, borrowed for one update.
#[derive(Clone, Copy)]
pub struct ItemsView<'a> {
    /// The object table.
    pub objects: &'a ClientObjectTable,
    /// The player's object id.
    pub player: u32,
    /// The selected object.
    pub selected: Option<u32>,
    /// A target mode waits for an object to be clicked (use, examine, or
    /// an item's target): a press on an object is offered to it.
    pub target_mode: bool,
    /// The item a target mode is waiting to use (drawn waiting).
    pub target_source: Option<u32>,
    /// Items whose inventory request is in flight (drawn waiting).
    pub pending_sources: &'a [u32],
    /// The item on its way into a pack: drawn waiting at the slot its
    /// request names until the server answers.
    pub pending_placement: Option<PendingPlacement>,
    /// An inventory request may be made now (none in flight).
    pub can_make_request: bool,
    /// The session's shortcut bar (the toolbar's copy follows it).
    pub shortcuts: Option<&'a ShortcutStore>,
    /// The combat mode (magic mode ghosts the shortcut numbers).
    pub combat_mode: CombatMode,
    /// The stack-split quantity of the selected stack.
    pub split: Option<&'a StackSplitQuantity>,
    /// The selected object is a vendor's whose split starts at one (the
    /// vendor split rule), not at the whole stack.
    pub vendor_split_exempt: bool,
    /// The player's current strength (burden capacity).
    pub strength: Option<i32>,
    /// The vendor whose shop window is open; 0 when none.
    pub open_vendor: u32,
    /// The player's name.
    pub owner_name: Option<&'a str>,
    /// Composes displayed names; the plain appropriate name without it.
    pub names: Option<NameResolver<'a>>,
}

impl std::fmt::Debug for ItemsView<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ItemsView")
            .field("player", &self.player)
            .field("selected", &self.selected)
            .field("target_source", &self.target_source)
            .finish_non_exhaustive()
    }
}

impl<'a> ItemsView<'a> {
    /// A view of `objects` for `player` with nothing else known.
    #[must_use]
    pub fn new(objects: &'a ClientObjectTable, player: u32) -> Self {
        Self {
            objects,
            player,
            selected: None,
            target_mode: false,
            target_source: None,
            pending_sources: &[],
            pending_placement: None,
            can_make_request: true,
            shortcuts: None,
            combat_mode: CombatMode::NON_COMBAT,
            split: None,
            vendor_split_exempt: false,
            strength: None,
            open_vendor: 0,
            owner_name: None,
            names: None,
        }
    }

    /// The object `id`.
    #[must_use]
    pub fn get(&self, id: u32) -> Option<&'a ClientObject> {
        self.objects.get(id)
    }

    /// `id`'s request is in flight.
    #[must_use]
    pub fn is_pending(&self, id: u32) -> bool {
        id != 0 && self.pending_sources.contains(&id)
    }

    /// `id` is the item a target mode waits to use.
    #[must_use]
    pub fn is_target_source(&self, id: u32) -> bool {
        id != 0 && self.target_source == Some(id)
    }

    /// The player is in magic mode (shortcut numbers ghost).
    #[must_use]
    pub fn magic_mode(&self) -> bool {
        self.combat_mode == CombatMode::MAGIC
    }

    /// The displayed name of `item`.
    #[must_use]
    pub fn name_of(&self, item: &ClientObject) -> String {
        match self.names {
            Some(f) => f(item),
            None => item.appropriate_name().into_owned(),
        }
    }

    /// The hover caption of object `id`: its composed name, with the stack
    /// count in front of a stack of more than one.
    #[must_use]
    pub fn tooltip(&self, id: u32) -> Option<String> {
        let item = self.get(id)?;
        let name = self.name_of(item);
        Some(item.tooltip_display_name(Some(&name)).into_owned())
    }

    /// The numbered (top-row) shortcut slot holding `id`, or -1.
    #[must_use]
    pub fn shortcut_slot_of(&self, id: u32) -> i32 {
        let Some(s) = self.shortcuts else {
            return -1;
        };
        if id == 0 {
            return -1;
        }
        (0..NUMBERED_SHORTCUT_SLOTS)
            .find(|&slot| s.get(slot) == id)
            .unwrap_or(-1)
    }

    /// How many of `id` a drag moves: the split quantity for the selected
    /// object, else the whole stack.
    #[must_use]
    pub fn split_size(&self, id: u32, stack_size: u32, selected: Option<u32>) -> u32 {
        match self.split {
            Some(s) => s.object_split_size(id, selected.unwrap_or(0), stack_size),
            None => stack_size.max(1),
        }
    }
}

/// Queue a panel output.
pub fn emit(ui: &mut UiRoot, output: PanelOutput) {
    ui.emit(UiOutput::Panel(output));
}

/// Offer a click on `target` to the waiting target mode (the session
/// uses, examines, or uses the waiting item on it).
pub fn offer_target(ui: &mut UiRoot, target: ObjectRef) {
    emit(
        ui,
        PanelOutput::Session(SessionCommand::Item(ItemCommand::OfferTarget(target))),
    );
}

/// Show `item` (`None`: empty) in `slot`: its icons, its hover caption,
/// its structure meter and its sale/trade markers.
pub fn fill_slot(
    slot: &mut ItemSlot,
    item: Option<&ClientObject>,
    view: &ItemsView<'_>,
    icons: &mut dyn IconSource,
) {
    let Some(item) = item else {
        slot.clear();
        slot.tooltip = None;
        slot.set_structure(0, 0);
        slot.show_sell_overlay = false;
        slot.show_trade_overlay = false;
        return;
    };
    let icon = icons.item_icon(
        item.item_type,
        item.icon_id,
        item.icon_underlay_id,
        item.icon_overlay_id,
        item.effects,
    );
    let drag = icons.drag_icon(
        item.item_type,
        item.icon_id,
        item.icon_underlay_id,
        item.icon_overlay_id,
        item.effects,
    );
    let shortcut = slot.shortcut;
    slot.set_item(item.object_id(), icon, drag, shortcut);
    slot.tooltip = view.tooltip(item.object_id());
    slot.set_structure(item.structure, item.max_structure);
    slot.show_sell_overlay = item.sell_state != 0;
    slot.show_trade_overlay = item.trade_state != 0;
}

/// Stamp (or clear) the shortcut number of `slot`'s object.
pub fn stamp_shortcut_number(
    slot: &mut ItemSlot,
    view: &ItemsView<'_>,
    digits: Option<&Arc<ShortcutDigits>>,
) {
    let n = view.shortcut_slot_of(slot.object_id);
    if n < 0 {
        slot.clear_shortcut_num();
        return;
    }
    slot.digits = digits.cloned();
    slot.set_shortcut_num(n, view.magic_mode());
}

/// Make `id` report its clicks ([`crate::root::ElementMessage::Clicked`]):
/// a button listens for them, a plain element takes the press and emits
/// them. False when the element is neither.
pub fn bind_click(ui: &mut UiRoot, id: ElementId) -> bool {
    if let Some(b) = ui.widget_mut::<Button>(id) {
        b.listeners.click = true;
        return true;
    }
    if let Some(d) = ui.widget_mut::<DatElement>(id) {
        d.emits_clicks = true;
        if let Some(e) = ui.element_mut(id) {
            e.click_through = false;
            e.captures_pointer_drag = true;
        }
        return true;
    }
    // Text tabs (the spellbook's) click like buttons.
    if let Some(t) = ui.widget_mut::<crate::widgets::Text>(id) {
        t.emits_clicks = true;
        if let Some(e) = ui.element_mut(id) {
            e.click_through = false;
        }
        return true;
    }
    false
}

/// The text element a caption is written into: `host` itself when it is
/// text, else a text child filling it.
pub fn caption_text(
    ui: &mut UiRoot,
    host: ElementId,
    font: Option<Arc<crate::UiFont>>,
) -> Option<ElementId> {
    ui.element(host)?;
    let text = if ui.widget::<Text>(host).is_some() {
        host
    } else {
        let (w, h) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
        let mut e = Element::at(0.0, 0.0, w, h)
            .with_anchors(AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT);
        Text::configure(&mut e);
        ui.add(host, e, Text::default())
    };
    if let Some(e) = ui.element_mut(text) {
        e.click_through = true;
        e.accepts_focus = false;
        e.is_edit_control = false;
        e.captures_pointer_drag = false;
    }
    if let Some(t) = ui.widget_mut::<Text>(text) {
        t.centered = true;
        t.one_line = true;
        if font.is_some() {
            t.font = font;
        }
    }
    Some(text)
}

/// Write `s` into caption text `id` ("" shows nothing).
pub fn set_caption(ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        t.set_lines(if s.is_empty() {
            Vec::new()
        } else {
            vec![Line::new(s, CAPTION_COLOR)]
        });
    }
}

/// The text a caption shows (first line), for tests and probes.
#[must_use]
pub fn caption_of(ui: &UiRoot, id: Option<ElementId>) -> String {
    id.and_then(|id| ui.widget::<Text>(id))
        .and_then(|t| t.content_lines().first().map(|l| l.text.clone()))
        .unwrap_or_default()
}
