//! The secure-trade window: the partner's name and acceptance, both sides'
//! staged items in two sideways-scrolling rows, the trade (accept) button,
//! clear all and close.
//!
//! The window shows the runtime's [`TradeView`] it is handed each update
//! (rebuilt when its revision moves) and follows the trade opening and
//! closing by showing and hiding itself. Items dropped from a pack on the
//! player's own row are offered; the buttons send the trade commands. A
//! right-click on an item in either row selects and examines it.

use std::collections::HashMap;
use std::sync::Arc;

use ac_runtime::inventory::{ItemCommand, ObjectRef, TradeView};
use ac_runtime::session::SessionCommand;

use crate::event::{DragAcceptance, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::items::{ItemsView, bind_click, emit};
use crate::panels::{PanelOutput, PanelStrings};
use crate::root::{ElementMessage, UiRoot};
use crate::strings::compute_hash;
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::item_slot::slot_element;
use crate::widgets::{Button, DatElement, ItemList, ItemSlot, Text};

/// The trade window's layout.
pub const LAYOUT_ID: u32 = 0x2100_000D;
/// Its root.
pub const ROOT_ID: u32 = 0x1000_007A;
/// The partner's name.
pub const PARTNER_NAME_ID: u32 = 0x1000_007E;
/// The partner's acceptance light.
pub const PARTNER_STATUS_ID: u32 = 0x1000_007F;
/// The partner's item count.
pub const PARTNER_COUNT_ID: u32 = 0x1000_0080;
/// The partner's staged items.
pub const PARTNER_LIST_ID: u32 = 0x1000_0081;
/// The player's name.
pub const SELF_NAME_ID: u32 = 0x1000_0085;
/// The trade (accept) button.
pub const TRADE_BUTTON_ID: u32 = 0x1000_0086;
/// The player's item count.
pub const SELF_COUNT_ID: u32 = 0x1000_0087;
/// The player's staged items.
pub const SELF_LIST_ID: u32 = 0x1000_0088;
/// Clear both sides.
pub const CLEAR_ALL_BUTTON_ID: u32 = 0x1000_008A;
/// Close the trade.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_008B;

/// The state the partner's light takes when the partner accepted.
pub const ACCEPTED_STATE: &str = "Highlight";
/// The marker drawn over the player's own staged items.
pub const TRADE_OVERLAY_SPRITE: u32 = 0x0600_1DAE;
/// The string table holding the count caption.
pub const STRING_TABLE_ID: u32 = 0x2300_0001;
/// Cell size of both rows, pixels.
pub const CELL_PX: f32 = 32.0;

/// The empty-cell images of the two rows (from the cell prototypes; see
/// [`crate::panels::cell_template::empty_sprite`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TradeSprites {
    /// The player's row.
    pub self_empty: u32,
    /// The partner's row.
    pub partner_empty: u32,
}

/// The secure-trade window's controller.
#[derive(Debug)]
pub struct SecureTradeController {
    window: String,
    partner_name: Option<ElementId>,
    partner_status: Option<ElementId>,
    partner_count: Option<ElementId>,
    partner_list: ElementId,
    self_count: Option<ElementId>,
    self_list: ElementId,
    trade_button: Option<ElementId>,
    clear_all: Option<ElementId>,
    close: Option<ElementId>,
    trade: TradeView,
    last_revision: Option<u64>,
    was_open: bool,
    pending_partner: u32,
    pending_stage_item: u32,
    font: Option<Arc<crate::UiFont>>,
}

fn configure_row(ui: &mut UiRoot, list: ElementId, empty_sprite: u32) {
    ui.with::<ItemList, _>(list, |l, ui| {
        l.flush(ui);
        l.columns = 1;
        l.single_row = true;
        l.horizontal_scroll = true;
        l.cell_width = CELL_PX;
        l.cell_height = CELL_PX;
        l.fill_visible_empty_slots = true;
        if empty_sprite != 0 {
            l.set_cell_empty_sprite(ui, empty_sprite);
        }
        l.empty_slot_factory = Some(Box::new(|ui: &mut UiRoot| {
            ui.insert(
                slot_element(),
                ItemSlot {
                    allow_drag_source: false,
                    ..ItemSlot::default()
                },
            )
        }));
    });
}

fn set_text(ui: &mut UiRoot, id: Option<ElementId>, s: &str, font: Option<&Arc<crate::UiFont>>) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        if t.font.is_none() {
            t.font = font.cloned();
        }
        t.set_lines(vec![Line::new(s, crate::draw::Color::WHITE)]);
    }
}

impl SecureTradeController {
    /// Bind to an imported trade window registered as `window` (`None`
    /// without both rows). The window starts hidden.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        window: &str,
        sprites: TradeSprites,
        font: Option<Arc<crate::UiFont>>,
    ) -> Option<Self> {
        let list = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
        };
        let self_list = list(ui, SELF_LIST_ID)?;
        let partner_list = list(ui, PARTNER_LIST_ID)?;
        let text = |ui: &UiRoot, id| layout.find(id).filter(|t| ui.widget::<Text>(*t).is_some());
        let trade_button = layout
            .find(TRADE_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some());
        if let Some(b) = trade_button {
            bind_click(ui, b);
            if let Some(w) = ui.widget_mut::<Button>(b) {
                // The button shows the player's acceptance, not its own
                // toggling.
                w.suppress_self_toggle = true;
            }
        }
        let button = |ui: &mut UiRoot, id| {
            layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some() && bind_click(ui, *b))
        };
        let clear_all = button(ui, CLEAR_ALL_BUTTON_ID);
        let close = button(ui, CLOSE_BUTTON_ID);
        configure_row(ui, self_list, sprites.self_empty);
        configure_row(ui, partner_list, sprites.partner_empty);
        let c = Self {
            window: window.to_owned(),
            partner_name: text(ui, PARTNER_NAME_ID),
            partner_status: layout.find(PARTNER_STATUS_ID),
            partner_count: text(ui, PARTNER_COUNT_ID),
            partner_list,
            self_count: text(ui, SELF_COUNT_ID),
            self_list,
            trade_button,
            clear_all,
            close,
            trade: TradeView::default(),
            last_revision: None,
            was_open: false,
            pending_partner: 0,
            pending_stage_item: 0,
            font,
        };
        ui.hide_window(window);
        Some(c)
    }

    /// The player's row.
    #[must_use]
    pub fn self_list(&self) -> ElementId {
        self.self_list
    }

    /// The partner's row.
    #[must_use]
    pub fn partner_list(&self) -> ElementId {
        self.partner_list
    }

    /// Offer `item` to `partner`: into the open trade with that partner at
    /// once, else ask for the trade and offer the item once the window
    /// opens with that partner (a drag of an item onto a player).
    pub fn request_secure_trade(&mut self, ui: &mut UiRoot, partner: u32, item: u32) {
        if partner == 0 {
            return;
        }
        if self.trade.open && self.trade.partner_guid == partner {
            if item != 0 {
                item_command(ui, ItemCommand::AddToTrade(ObjectRef::Guid(item)));
            }
            return;
        }
        self.pending_partner = partner;
        self.pending_stage_item = item;
        item_command(ui, ItemCommand::OpenTrade(ObjectRef::Guid(partner)));
    }

    /// Show `trade`: open or close the window as the trade does, and
    /// rebuild both sides when its revision moved.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        trade: &TradeView,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
        strings: Option<&dyn PanelStrings>,
    ) {
        self.trade.clone_from(trade);
        if trade.open && !self.was_open {
            self.was_open = true;
            ui.show_window(&self.window);
            // The item queued by a trade request is offered now that the
            // window opened, if it opened with the partner asked for.
            if self.pending_stage_item != 0
                && (self.pending_partner == 0 || trade.partner_guid == self.pending_partner)
            {
                item_command(
                    ui,
                    ItemCommand::AddToTrade(ObjectRef::Guid(self.pending_stage_item)),
                );
            }
            self.pending_stage_item = 0;
            self.pending_partner = 0;
        } else if !trade.open && self.was_open {
            self.was_open = false;
            ui.hide_window(&self.window);
        }
        if self.last_revision == Some(trade.revision) {
            return;
        }
        self.last_revision = Some(trade.revision);
        let name = trade
            .partner_name
            .clone()
            .or_else(|| {
                items
                    .get(trade.partner_guid)
                    .map(|o| o.appropriate_name().into_owned())
            })
            .unwrap_or_default();
        let font = self.font.clone();
        set_text(ui, self.partner_name, &name, font.as_ref());
        if let Some(s) = self
            .partner_status
            .and_then(|s| ui.widget_mut::<DatElement>(s))
        {
            s.active_state = if trade.partner_accepted {
                ACCEPTED_STATE.to_owned()
            } else {
                String::new()
            };
        }
        if let Some(b) = self.trade_button {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, trade.self_accepted));
        }
        let count = |n: usize| total_items_text(n, strings);
        set_text(
            ui,
            self.self_count,
            &count(trade.self_items.len()),
            font.as_ref(),
        );
        set_text(
            ui,
            self.partner_count,
            &count(trade.partner_items.len()),
            font.as_ref(),
        );
        populate(ui, self.self_list, &trade.self_items, true, items, icons);
        populate(
            ui,
            self.partner_list,
            &trade.partner_items,
            false,
            items,
            icons,
        );
    }

    /// Show or hide the window as the trade stands now (after the window
    /// was attached).
    pub fn sync_visibility(
        &mut self,
        ui: &mut UiRoot,
        trade: &TradeView,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
        strings: Option<&dyn PanelStrings>,
    ) {
        self.was_open = !trade.open;
        self.update(ui, trade, items, icons, strings);
    }
}

/// "Items: N" as the string table words the count (the bare number
/// without the entry).
#[must_use]
pub fn total_items_text(count: usize, strings: Option<&dyn PanelStrings>) -> String {
    let n = count.to_string();
    strings
        .and_then(|s| {
            s.template(
                STRING_TABLE_ID,
                compute_hash("ID_SecureTrade_TotalItemsLabel"),
                &HashMap::from([(compute_hash("ITEMS"), n.clone())]),
            )
        })
        .unwrap_or(n)
}

fn item_command(ui: &mut UiRoot, c: ItemCommand) {
    emit(ui, PanelOutput::Session(SessionCommand::Item(c)));
}

fn populate(
    ui: &mut UiRoot,
    list: ElementId,
    guids: &[u32],
    own: bool,
    items: &ItemsView<'_>,
    icons: &mut dyn IconSource,
) {
    ui.with::<ItemList, _>(list, |l, ui| {
        l.defer_layout();
        l.flush(ui);
    });
    for &guid in guids {
        let icon = items.get(guid).and_then(|o| {
            icons.item_icon(
                o.item_type,
                o.icon_id,
                o.icon_underlay_id,
                o.icon_overlay_id,
                o.effects,
            )
        });
        let index = ui.widget::<ItemList>(list).map_or(0, ItemList::num_items);
        let mut slot = ItemSlot {
            slot_index: i32::try_from(index).unwrap_or(i32::MAX),
            allow_drag_source: false,
            show_trade_overlay: own,
            trade_overlay_sprite: TRADE_OVERLAY_SPRITE,
            tooltip: items.tooltip(guid),
            ..ItemSlot::default()
        };
        slot.set_item(guid, icon, None, None);
        let cell = ui.insert(slot_element(), slot);
        ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
    }
    ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
}

impl PanelController for SecureTradeController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.trade_button {
            if clicked && self.trade.open {
                item_command(
                    ui,
                    if self.trade.self_accepted {
                        ItemCommand::DeclineTrade
                    } else {
                        ItemCommand::AcceptTrade
                    },
                );
            }
            return true;
        }
        if Some(element) == self.clear_all {
            if clicked && self.trade.open {
                item_command(ui, ItemCommand::ResetTrade);
            }
            return true;
        }
        if Some(element) == self.close {
            if clicked && self.trade.open {
                item_command(ui, ItemCommand::CloseTrade);
            }
            return true;
        }
        let parent = ui.parent(element);
        // Right-click examines an item on either side like any other item
        // cell; the partner's items are examinable too.
        if matches!(message, ElementMessage::RightClicked)
            && (parent == Some(self.self_list) || parent == Some(self.partner_list))
        {
            let id = ui.widget::<ItemSlot>(element).map_or(0, |s| s.object_id);
            if id != 0 {
                emit(ui, PanelOutput::SelectObject(id));
                item_command(ui, ItemCommand::Appraise(ObjectRef::Guid(id)));
            }
            return true;
        }
        if parent == Some(self.self_list) {
            match message {
                ElementMessage::ItemDragEntered(p) => {
                    let a = if p.source_kind == ItemDragSource::Inventory {
                        DragAcceptance::Accept
                    } else {
                        DragAcceptance::Reject
                    };
                    if let Some(s) = ui.widget_mut::<ItemSlot>(element) {
                        s.set_drag_acceptance(a);
                    }
                }
                ElementMessage::ItemDropped(p)
                    if p.source_kind == ItemDragSource::Inventory && self.trade.open =>
                {
                    item_command(ui, ItemCommand::AddToTrade(ObjectRef::Guid(p.obj_id)));
                }
                _ => {}
            }
            return true;
        }
        false
    }
}
