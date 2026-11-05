//! The vendor window: the shop's listing by category (a drop-down of the
//! categories it stocks over a sideways row of item cells), the selected
//! listing's name and cost with the buy and add buttons, and the buying and
//! selling tabs (each a row of staged items, its total and purse lines, and
//! buy/sell, all, clear-item and clear-list buttons).
//!
//! The buy and sell lists, their checks and prices live in the runtime;
//! the window shows the `VendorView` the runtime sends
//! ([`VendorController::on_vendor_event`]) and asks it to change the lists
//! with vendor commands. Selection, quantities and the cost line follow the
//! [`ItemsView`] handed to each [`VendorController::update`], where the
//! window's clicks, drags and drops are resolved.

use std::sync::Arc;

use ac_core::items::vendor::{
    UNLIMITED_STOCK, VendorSellCandidate, evaluate_sell, per_unit_value,
    resolve_authored_stack_size,
};
use ac_core::items::{ClientObject, ItemType, VendorShopItem, VendorShopProfile};
use ac_core::properties::PropertyInt;
use ac_runtime::inventory::shop::{
    CANNOT_SELL_PARTIAL_STACK, NOT_ENOUGH_ROOM, group_thousands, has_room_in, listing_price,
};
use ac_runtime::inventory::{ItemCommand, ObjectRef, VendorCommand, VendorView, VendorViewReason};
use ac_runtime::session::SessionCommand;

use crate::event::{DragAcceptance, ItemDragPayload, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::inventory::SelectionEcho;
use crate::panels::items::{ItemsView, bind_click, emit};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::item_slot::{SELL_OVERLAY_SPRITE, slot_element};
use crate::widgets::{Button, ItemList, ItemSlot, Menu, MenuItem, ScrollSource, Scrollbar, Text};

/// The vendor window's layout.
pub const LAYOUT_ID: u32 = 0x2100_0012;
/// Its root.
pub const ROOT_ID: u32 = 0x1000_00B7;
/// The close button.
pub const CLOSE_ID: u32 = 0x1000_00D6;
/// The items tab.
pub const ITEMS_TAB_ID: u32 = 0x1000_00B9;
/// The buying tab.
pub const BUYING_TAB_ID: u32 = 0x1000_00BA;
/// The selling tab.
pub const SELLING_TAB_ID: u32 = 0x1000_00BB;
/// The items page.
pub const ITEMS_PAGE_ID: u32 = 0x1000_00BC;
/// The listing row.
pub const ITEM_LIST_ID: u32 = 0x1000_00BD;
/// Its scrollbar.
pub const ITEM_SCROLLBAR_ID: u32 = 0x1000_00BE;
/// The category drop-down.
pub const TYPE_FILTER_MENU_ID: u32 = 0x1000_00BF;
/// The selected listing's name.
pub const ITEM_NAME_TEXT_ID: u32 = 0x1000_00C0;
/// Its cost.
pub const ITEM_COST_TEXT_ID: u32 = 0x1000_00C1;
/// Buy the selected listing.
pub const BUY_BUTTON_ID: u32 = 0x1000_00C2;
/// Add it to the buy list.
pub const ADD_BUTTON_ID: u32 = 0x1000_00C3;
/// The buying page.
pub const BUYING_PAGE_ID: u32 = 0x1000_00C4;
/// The selling page.
pub const SELLING_PAGE_ID: u32 = 0x1000_00CD;
/// The buy list row.
pub const BUYING_LIST_ID: u32 = 0x1000_00C5;
/// Its scrollbar.
pub const BUYING_SCROLLBAR_ID: u32 = 0x1000_00C6;
/// The sell list row.
pub const SELLING_LIST_ID: u32 = 0x1000_00CE;
/// Its scrollbar.
pub const SELLING_SCROLLBAR_ID: u32 = 0x1000_00CF;
/// "Buying N items worth Xp".
pub const BUYING_LIST_TEXT_ID: u32 = 0x1000_00C7;
/// The purse line on the buying page.
pub const BUYING_PURSE_TEXT_ID: u32 = 0x1000_00C8;
/// "Selling N items worth Xp".
pub const SELLING_LIST_TEXT_ID: u32 = 0x1000_00D0;
/// The purse line on the selling page.
pub const SELLING_PURSE_TEXT_ID: u32 = 0x1000_00D1;
/// Buy the selected staged listing.
pub const BUY_ITEM_BUTTON_ID: u32 = 0x1000_00C9;
/// Buy the whole buy list.
pub const BUY_ALL_BUTTON_ID: u32 = 0x1000_00CA;
/// Take the selected listing off the buy list.
pub const BUY_CLEAR_ITEM_BUTTON_ID: u32 = 0x1000_00CB;
/// Empty the buy list.
pub const BUY_CLEAR_LIST_BUTTON_ID: u32 = 0x1000_00CC;
/// Sell the selected item.
pub const SELL_ITEM_BUTTON_ID: u32 = 0x1000_00D2;
/// Sell the whole sell list.
pub const SELL_ALL_BUTTON_ID: u32 = 0x1000_00D3;
/// Take the selected item off the sell list.
pub const SELL_CLEAR_ITEM_BUTTON_ID: u32 = 0x1000_00D4;
/// Empty the sell list.
pub const SELL_CLEAR_LIST_BUTTON_ID: u32 = 0x1000_00D5;

/// The spell components category's item-type mask (a component fill
/// shows it).
pub const SPELL_COMPONENTS_MASK: u32 = 0x0000_1000;

/// The categories in table order: caption and item-type mask.
pub const CATEGORY_FILTERS: [(&str, u32); 18] = [
    ("Armor", 0x0000_0002),
    ("Books, Paper", 0x0000_2000),
    ("Clothing", 0x0000_0004),
    ("Containers", 0x0000_0200),
    ("Food", 0x0000_0020),
    ("Gems", 0x0000_0800),
    ("Jewelry", 0x0000_0008),
    // Tinkering tools and keys.
    ("Keys, Tools", 0x2000_4000),
    // Useless, misc and creature.
    ("Miscellaneous", 0x0000_0490),
    ("Services", 0x0010_0000),
    ("Spell Components", 0x0000_1000),
    ("Trade Notes", 0x0004_0000),
    // Melee and missile weapons.
    ("Weapons", 0x0000_0101),
    ("Mana Stones", 0x0008_0000),
    ("Magic Items", 0x0000_8000),
    // Alchemy intermediates and bases.
    ("Alchemical Items", 0x0480_0000),
    ("Cooking Items", 0x0040_0000),
    // Fletching intermediates and bases.
    ("Fletching Items", 0x0900_0000),
];

/// The categories' drop-down: rows per column, row height and column
/// width, pixels.
pub const TYPE_MENU_ROWS_PER_COLUMN: usize = 6;
/// Row height of the drop-down, pixels.
pub const TYPE_MENU_ROW_HEIGHT: f32 = 18.0;
/// Column width of the drop-down, pixels.
pub const TYPE_MENU_COLUMN_WIDTH: f32 = 100.0;
const TYPE_MENU_ITEM_NORMAL_SPRITE: u32 = 0x0600_12B3;
const TYPE_MENU_ITEM_HIGHLIGHT_SPRITE: u32 = 0x0600_12B4;
const TYPE_MENU_NORMAL_SPRITE: u32 = 0x0600_12B3;
const TYPE_MENU_PRESSED_SPRITE: u32 = 0x0600_12B4;
/// The drop-down's arrow cap, closed.
pub const TYPE_MENU_ARROW_CAP_CLOSED_SPRITE: u32 = 0x0600_12B1;
/// The drop-down's arrow cap, open.
pub const TYPE_MENU_ARROW_CAP_OPEN_SPRITE: u32 = 0x0600_12B2;
const TYPE_MENU_SCROLLBAR_WIDTH: f32 = 16.0;
const TYPE_MENU_SCROLL_BUTTON_EXTENT: f32 = 16.0;
const TYPE_MENU_SCROLL_TRACK_SPRITE: u32 = 0x0600_4C5F;
const TYPE_MENU_SCROLL_THUMB_TOP_SPRITE: u32 = 0x0600_4C60;
const TYPE_MENU_SCROLL_THUMB_SPRITE: u32 = 0x0600_4C63;
const TYPE_MENU_SCROLL_THUMB_BOTTOM_SPRITE: u32 = 0x0600_4C66;
const TYPE_MENU_SCROLL_UP_SPRITE: u32 = 0x0600_4C6C;
const TYPE_MENU_SCROLL_DOWN_SPRITE: u32 = 0x0600_4C69;

/// Cell size of the three rows, pixels.
pub const CELL_PX: f32 = 32.0;

/// Asked before closing with items still on either list.
pub const CLOSE_CONFIRMATION_MESSAGE: &str =
    "You have not completed all transactions. Are you sure you want to leave this vendor?";
/// A staged row is lifted with only part of its stack selected.
pub const CANNOT_SPLIT_HERE_MESSAGE: &str = "You cannot split items from this panel";
/// A split before selling could not be asked for.
pub const CANNOT_SPLIT_TO_SELL_MESSAGE: &str = "Cannot split the stack to sell it";

/// The empty-cell images of the three rows (from the cell prototypes; see
/// [`crate::panels::cell_template::empty_sprite`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VendorSprites {
    /// The listing row.
    pub items_empty: u32,
    /// The buy list row.
    pub buying_empty: u32,
    /// The sell list row.
    pub selling_empty: u32,
}

/// The window's pages.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum VendorTab {
    /// The listing.
    #[default]
    Items,
    /// The buy list.
    Buying,
    /// The sell list.
    Selling,
}

/// A split asked for so part of a stack can be sold: the new stack is
/// staged when it appears.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct PendingSplit {
    source: u32,
    weenie_class_id: u32,
    quantity: i32,
    seen_in_flight: bool,
}

/// The vendor window's controller.
#[derive(Debug)]
pub struct VendorController {
    window: String,
    items_page: ElementId,
    buying_page: ElementId,
    selling_page: ElementId,
    items_tab: ElementId,
    buying_tab: ElementId,
    selling_tab: ElementId,
    item_list: ElementId,
    buying_list: Option<ElementId>,
    selling_list: Option<ElementId>,
    type_menu: ElementId,
    item_name: ElementId,
    item_cost: ElementId,
    buy_list_text: Option<ElementId>,
    buy_purse_text: Option<ElementId>,
    sell_list_text: Option<ElementId>,
    sell_purse_text: Option<ElementId>,
    close: Option<ElementId>,
    buy: Option<ElementId>,
    add: Option<ElementId>,
    buy_item: Option<ElementId>,
    buy_all: Option<ElementId>,
    buy_clear_item: Option<ElementId>,
    buy_clear_list: Option<ElementId>,
    sell_item: Option<ElementId>,
    sell_all: Option<ElementId>,
    sell_clear_item: Option<ElementId>,
    sell_clear_list: Option<ElementId>,
    view: Option<VendorView>,
    categories: Vec<(&'static str, u32)>,
    selected_category: Option<usize>,
    tab: VendorTab,
    selection: SelectionEcho,
    queue: Vec<(ElementId, ElementMessage)>,
    pending_split: Option<PendingSplit>,
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

fn link_bar(ui: &mut UiRoot, bar: Option<ElementId>, list: Option<ElementId>) {
    if let (Some(b), Some(l)) = (bar, list)
        && let Some(s) = ui.widget_mut::<Scrollbar>(b)
    {
        s.model = ScrollSource::Element(l);
        s.horizontal = true;
    }
}

fn session(ui: &mut UiRoot, c: SessionCommand) {
    emit(ui, PanelOutput::Session(c));
}

fn vendor_command(ui: &mut UiRoot, c: VendorCommand) {
    session(ui, SessionCommand::Item(ItemCommand::Vendor(c)));
}

fn notice(ui: &mut UiRoot, text: &str) {
    emit(ui, PanelOutput::Notice(text.to_owned()));
}

/// The player's pyreals (the coin value the server keeps on them).
#[must_use]
pub fn coin_value(items: &ItemsView<'_>) -> i32 {
    items
        .get(items.player)
        .map_or(0, |p| p.properties.get_int(PropertyInt::COIN_VALUE.0, 0))
}

/// The cost line of a selected listing: the alternate currency's sentence,
/// else "costs 1,200p (you have 5,000p)" ("cost" for more than one).
#[must_use]
pub fn cost_text(profile: &VendorShopProfile, quantity: i32, price: i32, money: i32) -> String {
    if profile.alternate_currency_wcid != 0 {
        return format!(
            "This item costs {price} {0}. You have {money} {0}.",
            profile.alternate_currency_plural_name
        );
    }
    let verb = if quantity <= 1 { "costs" } else { "cost" };
    format!(
        "{verb} {}p (you have {}p)",
        group_thousands(price),
        group_thousands(money)
    )
}

/// The selected listing's caption: its name, or "N plural" for more than
/// one (the singular when no plural is authored).
#[must_use]
pub fn listing_caption(item: &VendorShopItem, quantity: i32) -> String {
    let name = item.name.as_deref().unwrap_or("");
    if quantity <= 1 {
        return name.to_owned();
    }
    let plural = item
        .plural_name
        .as_deref()
        .filter(|p| !p.is_empty())
        .unwrap_or(name);
    format!("{quantity} {plural}")
}

/// Whether the vendor takes `item`, as the window checks a drag over the
/// sell list.
fn sell_check(
    items: &ItemsView<'_>,
    profile: &VendorShopProfile,
    guid: u32,
) -> Result<(), ac_core::items::VendorSellRejection> {
    let Some(item) = items.get(guid) else {
        return Err(ac_core::items::VendorSellRejection::WrongType);
    };
    let player = items.player;
    evaluate_sell(
        &VendorSellCandidate {
            owned_by_player: items.objects.is_owned_by_object(guid, player),
            contained_item_count: i32::try_from(items.objects.get_contained_items(guid).len())
                .unwrap_or(i32::MAX),
            item_type: item.item_type.bits(),
            per_unit_value: per_unit_value(item.value, Some(item.stack_size)),
            public_weenie_bitfield: item.public_weenie_bitfield.map_or(0, |f| f.bits()),
        },
        profile,
    )
}

fn split_of(items: &ItemsView<'_>, guid: u32, selected: Option<u32>, stack: u32) -> u32 {
    items.split_size(guid, stack, selected)
}

impl VendorController {
    /// Bind to an imported vendor window registered as `window` (`None`
    /// when a page, a tab, the listing, the drop-down or the name and cost
    /// texts are missing). The window starts on the items page, empty.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        window: &str,
        sprites: VendorSprites,
        font: Option<Arc<crate::UiFont>>,
    ) -> Option<Self> {
        let items_page = layout.find(ITEMS_PAGE_ID)?;
        let buying_page = layout.find(BUYING_PAGE_ID)?;
        let selling_page = layout.find(SELLING_PAGE_ID)?;
        let items_tab = layout.find(ITEMS_TAB_ID)?;
        let buying_tab = layout.find(BUYING_TAB_ID)?;
        let selling_tab = layout.find(SELLING_TAB_ID)?;
        let item_list = layout
            .find(ITEM_LIST_ID)
            .filter(|l| ui.widget::<ItemList>(*l).is_some())?;
        let type_menu = layout
            .find(TYPE_FILTER_MENU_ID)
            .filter(|m| ui.widget::<Menu>(*m).is_some())?;
        let text = |ui: &UiRoot, id| layout.find(id).filter(|t| ui.widget::<Text>(*t).is_some());
        let item_name = text(ui, ITEM_NAME_TEXT_ID)?;
        let item_cost = text(ui, ITEM_COST_TEXT_ID)?;
        let list = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
        };
        let buying_list = list(ui, BUYING_LIST_ID);
        let selling_list = list(ui, SELLING_LIST_ID);
        let button = |ui: &mut UiRoot, id| {
            layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some() && bind_click(ui, *b))
        };
        for tab in [items_tab, buying_tab, selling_tab] {
            bind_click(ui, tab);
        }
        configure_row(ui, item_list, sprites.items_empty);
        link_bar(ui, layout.find(ITEM_SCROLLBAR_ID), Some(item_list));
        if let Some(l) = buying_list {
            configure_row(ui, l, sprites.buying_empty);
        }
        link_bar(ui, layout.find(BUYING_SCROLLBAR_ID), buying_list);
        if let Some(l) = selling_list {
            configure_row(ui, l, sprites.selling_empty);
        }
        link_bar(ui, layout.find(SELLING_SCROLLBAR_ID), selling_list);
        if let Some(m) = ui.widget_mut::<Menu>(type_menu) {
            if m.font.is_none() {
                m.font = font.clone();
            }
            m.normal_sprite = TYPE_MENU_NORMAL_SPRITE;
            m.pressed_sprite = TYPE_MENU_PRESSED_SPRITE;
            m.item_normal_sprite = TYPE_MENU_ITEM_NORMAL_SPRITE;
            m.item_highlight_sprite = TYPE_MENU_ITEM_HIGHLIGHT_SPRITE;
            m.rows_per_column = TYPE_MENU_ROWS_PER_COLUMN;
            m.row_height = TYPE_MENU_ROW_HEIGHT;
            m.column_width = TYPE_MENU_COLUMN_WIDTH;
            m.scrollable = true;
            m.popup_size_to_content = true;
            m.popup_scrollbar_hide_when_disabled = true;
            m.scrollbar_width = TYPE_MENU_SCROLLBAR_WIDTH;
            m.scroll_button_extent = TYPE_MENU_SCROLL_BUTTON_EXTENT;
            m.scroll_track_sprite = TYPE_MENU_SCROLL_TRACK_SPRITE;
            m.scroll_thumb_top_sprite = TYPE_MENU_SCROLL_THUMB_TOP_SPRITE;
            m.scroll_thumb_sprite = TYPE_MENU_SCROLL_THUMB_SPRITE;
            m.scroll_thumb_bottom_sprite = TYPE_MENU_SCROLL_THUMB_BOTTOM_SPRITE;
            m.scroll_up_sprite = TYPE_MENU_SCROLL_UP_SPRITE;
            m.scroll_down_sprite = TYPE_MENU_SCROLL_DOWN_SPRITE;
            m.arrow_cap_closed_sprite = TYPE_MENU_ARROW_CAP_CLOSED_SPRITE;
            m.arrow_cap_open_sprite = TYPE_MENU_ARROW_CAP_OPEN_SPRITE;
            m.open_upward = false;
            m.text_indent = 0.0;
            m.button_text_indent = 0.0;
        }
        let mut c = Self {
            window: window.to_owned(),
            items_page,
            buying_page,
            selling_page,
            items_tab,
            buying_tab,
            selling_tab,
            item_list,
            buying_list,
            selling_list,
            type_menu,
            item_name,
            item_cost,
            buy_list_text: text(ui, BUYING_LIST_TEXT_ID),
            buy_purse_text: text(ui, BUYING_PURSE_TEXT_ID),
            sell_list_text: text(ui, SELLING_LIST_TEXT_ID),
            sell_purse_text: text(ui, SELLING_PURSE_TEXT_ID),
            close: button(ui, CLOSE_ID),
            buy: button(ui, BUY_BUTTON_ID),
            add: button(ui, ADD_BUTTON_ID),
            buy_item: button(ui, BUY_ITEM_BUTTON_ID),
            buy_all: button(ui, BUY_ALL_BUTTON_ID),
            buy_clear_item: button(ui, BUY_CLEAR_ITEM_BUTTON_ID),
            buy_clear_list: button(ui, BUY_CLEAR_LIST_BUTTON_ID),
            sell_item: button(ui, SELL_ITEM_BUTTON_ID),
            sell_all: button(ui, SELL_ALL_BUTTON_ID),
            sell_clear_item: button(ui, SELL_CLEAR_ITEM_BUTTON_ID),
            sell_clear_list: button(ui, SELL_CLEAR_LIST_BUTTON_ID),
            view: None,
            categories: Vec::new(),
            selected_category: None,
            tab: VendorTab::Items,
            selection: SelectionEcho::default(),
            queue: Vec::new(),
            pending_split: None,
            font,
        };
        c.show_tab(ui, VendorTab::Items);
        c.clear_content(ui);
        Some(c)
    }

    /// The page showing.
    #[must_use]
    pub fn tab(&self) -> VendorTab {
        self.tab
    }

    /// The listing row.
    #[must_use]
    pub fn item_list(&self) -> ElementId {
        self.item_list
    }

    /// The buy list row.
    #[must_use]
    pub fn buying_list(&self) -> Option<ElementId> {
        self.buying_list
    }

    /// The sell list row.
    #[must_use]
    pub fn selling_list(&self) -> Option<ElementId> {
        self.selling_list
    }

    /// The categories the shop stocks, in table order.
    #[must_use]
    pub fn categories(&self) -> Vec<&'static str> {
        self.categories.iter().map(|(l, _)| *l).collect()
    }

    /// The category shown.
    #[must_use]
    pub fn selected_category(&self) -> Option<&'static str> {
        self.selected_category
            .and_then(|i| self.categories.get(i))
            .map(|(l, _)| *l)
    }

    /// Show page `tab`.
    pub fn show_tab(&mut self, ui: &mut UiRoot, tab: VendorTab) {
        self.tab = tab;
        ui.set_visible(self.items_page, tab == VendorTab::Items);
        ui.set_visible(self.buying_page, tab == VendorTab::Buying);
        ui.set_visible(self.selling_page, tab == VendorTab::Selling);
        for (t, open) in [
            (self.items_tab, tab == VendorTab::Items),
            (self.buying_tab, tab == VendorTab::Buying),
            (self.selling_tab, tab == VendorTab::Selling),
        ] {
            crate::widgets::tab_panel::set_open(ui, t, open);
        }
    }

    // ── The shop's events ───────────────────────────────────────────────

    /// The runtime sent the shop: opened or refreshed (`Some`), its lists
    /// changed, or it closed (`None`).
    pub fn on_vendor_event(
        &mut self,
        ui: &mut UiRoot,
        view: Option<&VendorView>,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let Some(view) = view else {
            self.view = None;
            self.pending_split = None;
            self.clear_content(ui);
            self.show_tab(ui, VendorTab::Items);
            ui.hide_window(&self.window);
            return;
        };
        self.view = Some(view.clone());
        match view.reason {
            VendorViewReason::Opened => {
                self.pending_split = None;
                self.selected_category = None;
                // Another shop: its row starts at the beginning.
                self.scroll_item_list_to(ui, 0);
                self.show_tab(ui, VendorTab::Items);
                self.rebuild_categories(ui, items, icons);
                ui.show_window(&self.window);
            }
            // The open shop sent again (after a purchase or a sale): the
            // window stays on the page the player was on.
            VendorViewReason::Refreshed => {
                self.rebuild_categories(ui, items, icons);
                ui.show_window(&self.window);
            }
            VendorViewReason::Asked => {
                self.show_tab(ui, VendorTab::Items);
                self.rebuild_categories(ui, items, icons);
                ui.show_window(&self.window);
            }
            VendorViewReason::Lists => {
                self.rebuild_item_list(ui, items, icons, false);
            }
            // OpenAC `FillComponentBuyList`: the spell components listing
            // (when the shop has it) and the buying page.
            VendorViewReason::ComponentsFilled => {
                let before = self.selected_category;
                self.select_category(ui, SPELL_COMPONENTS_MASK, items, icons);
                if self.selected_category == before {
                    self.rebuild_item_list(ui, items, icons, false);
                }
                self.show_tab(ui, VendorTab::Buying);
            }
        }
        // The staged rows follow the lists every time the shop is sent.
        self.rebuild_buying_list(ui, items, icons);
        self.rebuild_selling_list(ui, items, icons);
        self.refresh_texts(ui, items);
    }

    fn clear_content(&mut self, ui: &mut UiRoot) {
        self.categories.clear();
        self.selected_category = None;
        self.scroll_item_list_to(ui, 0);
        if let Some(m) = ui.widget_mut::<Menu>(self.type_menu) {
            m.items.clear();
            m.selected = None;
            m.button_label.clear();
        }
        let list = self.item_list;
        ui.with::<ItemList, _>(list, |l, ui| l.flush(ui));
        for l in [self.buying_list, self.selling_list].into_iter().flatten() {
            ui.with::<ItemList, _>(l, |w, ui| w.flush(ui));
        }
        self.clear_selection_display(ui);
        for t in [
            self.buy_list_text,
            self.buy_purse_text,
            self.sell_list_text,
            self.sell_purse_text,
        ] {
            self.set_text(ui, t, "");
        }
    }

    fn set_text(&self, ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
        let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) else {
            return;
        };
        if t.font.is_none() {
            t.font.clone_from(&self.font);
        }
        let lines = if s.is_empty() {
            Vec::new()
        } else {
            vec![Line::new(s, t.default_color)]
        };
        if t.content_lines() != lines {
            t.set_lines(lines);
        }
    }

    /// Scroll the listing row sideways to `x` pixels, clamped to the row.
    fn scroll_item_list_to(&self, ui: &mut UiRoot, x: i32) {
        if let Some(l) = ui.widget_mut::<ItemList>(self.item_list) {
            l.scroll.set_scroll_y(x);
        }
    }

    /// Refill the category menu and the listing row from the shop. The row
    /// keeps how far it was scrolled sideways (clamped to the refilled row),
    /// so a shop sent again after a purchase or a sale stays where the
    /// player was; a newly opened shop is scrolled to the start first.
    fn rebuild_categories(
        &mut self,
        ui: &mut UiRoot,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let scroll_x = ui
            .widget::<ItemList>(self.item_list)
            .map_or(0, |l| l.scroll.scroll_y());
        let listing = self.view.as_ref().map_or(&[][..], |v| &v.items[..]);
        self.categories = CATEGORY_FILTERS
            .iter()
            .filter(|(_, mask)| listing.iter().any(|i| i.item_type.unwrap_or(0) & mask != 0))
            .copied()
            .collect();
        // The kept choice, clamped to what this shop stocks.
        let count = self.categories.len();
        let mut selected = self.selected_category.map_or(-1, |i| i as isize);
        if selected >= count as isize - 1 {
            selected = count as isize - 1;
        }
        if selected < 0 {
            selected = 0;
        }
        self.selected_category = usize::try_from(selected).ok().filter(|i| *i < count);
        self.refresh_menu(ui);
        self.rebuild_item_list(ui, items, icons, true);
        self.scroll_item_list_to(ui, scroll_x);
    }

    fn refresh_menu(&self, ui: &mut UiRoot) {
        let selected = self
            .selected_category
            .and_then(|i| self.categories.get(i).copied());
        if let Some(m) = ui.widget_mut::<Menu>(self.type_menu) {
            m.items = self
                .categories
                .iter()
                .map(|(label, mask)| MenuItem::new(*label, u64::from(*mask)))
                .collect();
            m.selected = selected.map(|(_, mask)| u64::from(mask));
            m.button_label = selected.map_or_else(String::new, |(l, _)| l.to_owned());
        }
    }

    fn select_category(
        &mut self,
        ui: &mut UiRoot,
        mask: u32,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let Some(i) = self.categories.iter().position(|(_, m)| *m == mask) else {
            return;
        };
        if Some(i) == self.selected_category {
            return;
        }
        self.selected_category = Some(i);
        self.refresh_menu(ui);
        self.rebuild_item_list(ui, items, icons, true);
    }

    /// True when staging has used up a limited listing, which takes it out
    /// of the browse list. A listing that is not staged is always shown,
    /// whatever amount the vendor sent, 0 included; a staged one with a
    /// limited amount leaves once the amount minus the staged quantity is 0
    /// or less (so a listing sent with 0 leaves as soon as anything is
    /// staged). Only [`UNLIMITED_STOCK`] never runs out. It is worked out
    /// from the untouched listing each time, so un-staging brings it back.
    fn is_shop_supply_consumed_by_staging(&self, item: &VendorShopItem) -> bool {
        if item.stack_size == UNLIMITED_STOCK {
            return false;
        }
        self.view
            .as_ref()
            .and_then(|v| {
                v.lists
                    .buying
                    .iter()
                    .find(|r| r.item_guid == item.item_guid)
            })
            .is_some_and(|staged| item.stack_size.wrapping_sub(staged.quantity) <= 0)
    }

    fn rebuild_item_list(
        &mut self,
        ui: &mut UiRoot,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
        reselect_first: bool,
    ) {
        let mask = self
            .selected_category
            .and_then(|i| self.categories.get(i))
            .map_or(0, |(_, m)| *m);
        let selected = self.selection.get();
        let listing: Vec<VendorShopItem> = self
            .view
            .as_ref()
            .map(|v| v.items.clone())
            .unwrap_or_default();
        let list = self.item_list;
        ui.with::<ItemList, _>(list, |l, ui| {
            l.defer_layout();
            l.flush(ui);
        });
        let mut first = None;
        let mut selected_visible = false;
        if mask != 0 {
            for item in &listing {
                if item.item_type.unwrap_or(0) & mask == 0
                    || self.is_shop_supply_consumed_by_staging(item)
                {
                    continue;
                }
                first.get_or_insert(item.item_guid);
                if Some(item.item_guid) == selected {
                    selected_visible = true;
                }
                let cell = listing_cell(ui, list, item, items, icons);
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    s.selected = Some(item.item_guid) == selected;
                }
            }
        }
        ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
        if reselect_first {
            match first {
                Some(g) => self.select(ui, g),
                None => {
                    self.selection = SelectionEcho::default();
                    session(
                        ui,
                        SessionCommand::Gameplay(
                            ac_runtime::gameplay::GameplayCommand::ClearSelection,
                        ),
                    );
                }
            }
            if let Some(l) = ui.widget_mut::<ItemList>(list) {
                l.scroll.set_scroll_y(0);
            }
        } else if selected.is_some() && !selected_visible && self.is_listing(selected) {
            // A listing that ran out leaves the selection.
            self.selection = SelectionEcho::default();
            session(
                ui,
                SessionCommand::Gameplay(ac_runtime::gameplay::GameplayCommand::ClearSelection),
            );
        }
    }

    fn is_listing(&self, guid: Option<u32>) -> bool {
        guid.is_some_and(|g| self.listing(g).is_some())
    }

    fn listing(&self, guid: u32) -> Option<&VendorShopItem> {
        self.view
            .as_ref()
            .and_then(|v| v.items.iter().find(|i| i.item_guid == guid))
    }

    fn rebuild_buying_list(
        &mut self,
        ui: &mut UiRoot,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let Some(list) = self.buying_list else {
            return;
        };
        let rows: Vec<VendorShopItem> = self
            .view
            .as_ref()
            .map(|v| {
                v.lists
                    .buying
                    .iter()
                    .filter_map(|r| v.items.iter().find(|i| i.item_guid == r.item_guid))
                    .cloned()
                    .collect()
            })
            .unwrap_or_default();
        ui.with::<ItemList, _>(list, |l, ui| {
            l.defer_layout();
            l.flush(ui);
        });
        for item in &rows {
            listing_cell(ui, list, item, items, icons);
        }
        ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
    }

    fn rebuild_selling_list(
        &mut self,
        ui: &mut UiRoot,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let Some(list) = self.selling_list else {
            return;
        };
        let guids: Vec<u32> = self
            .view
            .as_ref()
            .map(|v| v.lists.selling.iter().map(|r| r.item_guid).collect())
            .unwrap_or_default();
        ui.with::<ItemList, _>(list, |l, ui| {
            l.defer_layout();
            l.flush(ui);
        });
        for guid in guids {
            let Some(item) = items.get(guid) else {
                continue;
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
            let index = ui.widget::<ItemList>(list).map_or(0, ItemList::num_items);
            let mut slot = ItemSlot {
                slot_index: i32::try_from(index).unwrap_or(i32::MAX),
                allow_drag_source: true,
                source_kind: ItemDragSource::Vendor,
                show_sell_overlay: true,
                sell_overlay_sprite: SELL_OVERLAY_SPRITE,
                tooltip: items.tooltip(guid),
                ..ItemSlot::default()
            };
            slot.set_item(guid, icon, drag, None);
            let cell = ui.insert(slot_element(), slot);
            ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
        }
        ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
    }

    // ── Every frame ─────────────────────────────────────────────────────

    /// Resolve what the player did in the window against `items`, then show
    /// the selection, the cost line and the purse.
    pub fn update(&mut self, ui: &mut UiRoot, items: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.selection.sync(items.selected);
        let queued = std::mem::take(&mut self.queue);
        for (element, message) in queued {
            self.handle(ui, element, &message, items, icons);
        }
        self.resolve_pending_split(ui, items);
        self.refresh_texts(ui, items);
    }

    /// How many units the selected listing stands for: one for a kind that
    /// does not stack, else the split quantity of its authored stack.
    fn buy_quantity(&self, item: &VendorShopItem, items: &ItemsView<'_>) -> i32 {
        let stack = resolve_authored_stack_size(item.desc_stack_size, item.max_stack_size);
        if stack <= 1 {
            return 1;
        }
        let selected = self.selection.get().unwrap_or(item.item_guid);
        let n = split_of(items, item.item_guid, Some(selected), stack.unsigned_abs());
        i32::try_from(n).unwrap_or(i32::MAX)
    }

    fn money(&self, items: &ItemsView<'_>) -> i32 {
        match &self.view {
            Some(v) if v.profile.alternate_currency_wcid != 0 => v.lists.money,
            _ => coin_value(items),
        }
    }

    fn refresh_texts(&mut self, ui: &mut UiRoot, items: &ItemsView<'_>) {
        let Some(view) = self.view.clone() else {
            return;
        };
        let selected = self.selection.get();
        // The listing cells and the staged rows follow the selection.
        for list in [Some(self.item_list), self.buying_list, self.selling_list]
            .into_iter()
            .flatten()
        {
            let cells = ui
                .widget::<ItemList>(list)
                .map(|l| l.cells().to_vec())
                .unwrap_or_default();
            for c in cells {
                if let Some(s) = ui.widget_mut::<ItemSlot>(c) {
                    s.selected = s.object_id != 0 && Some(s.object_id) == selected;
                }
            }
        }
        let money = self.money(items);
        match selected.and_then(|g| self.listing(g).cloned()) {
            Some(item) => {
                let quantity = self.buy_quantity(&item, items);
                let price = listing_price(&item, &view.profile, quantity);
                self.set_text(ui, Some(self.item_name), &listing_caption(&item, quantity));
                self.set_text(
                    ui,
                    Some(self.item_cost),
                    &cost_text(&view.profile, quantity, price, money),
                );
                self.set_action_buttons(ui, true, items.can_make_request);
            }
            None => self.clear_selection_display(ui),
        }
        let purse = if view.profile.alternate_currency_wcid != 0 {
            view.lists.purse_text.clone()
        } else {
            format!("You have {}p", group_thousands(money))
        };
        self.set_text(ui, self.buy_list_text, &view.lists.buying_text);
        self.set_text(ui, self.sell_list_text, &view.lists.selling_text);
        self.set_text(ui, self.buy_purse_text, &purse);
        self.set_text(ui, self.sell_purse_text, &purse);
    }

    fn clear_selection_display(&self, ui: &mut UiRoot) {
        self.set_text(ui, Some(self.item_name), "");
        self.set_text(ui, Some(self.item_cost), "");
        self.set_action_buttons(ui, false, false);
    }

    fn set_action_buttons(&self, ui: &mut UiRoot, by_selection: bool, can_request: bool) {
        if let Some(b) = self.buy {
            ui.set_enabled(b, by_selection && can_request);
        }
        if let Some(b) = self.add {
            ui.set_enabled(b, by_selection);
        }
    }

    fn select(&mut self, ui: &mut UiRoot, guid: u32) {
        if guid == 0 {
            return;
        }
        if self.selection.get() != Some(guid) {
            emit(ui, PanelOutput::SelectObject(guid));
        }
        self.selection.select(guid);
    }

    fn list_of(&self, ui: &UiRoot, cell: ElementId) -> Option<ElementId> {
        let parent = ui.parent(cell)?;
        [Some(self.item_list), self.buying_list, self.selling_list]
            .into_iter()
            .flatten()
            .find(|l| *l == parent)
    }

    #[allow(clippy::too_many_lines)]
    fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        if let ElementMessage::MenuSelected(payload) = message {
            if let Ok(mask) = u32::try_from(*payload) {
                self.select_category(ui, mask, items, icons);
            }
            return;
        }
        if let Some(list) = self.list_of(ui, element) {
            self.handle_cell(ui, list, element, message, items);
            return;
        }
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return;
        }
        let Some(view) = self.view.clone() else {
            return;
        };
        let selected = self.selection.get();
        let e = Some(element);
        if e == self.buy {
            self.buy_selected(ui, items, false);
        } else if e == self.add {
            if let Some(item) = selected.and_then(|g| self.listing(g).cloned()) {
                let amount = self.buy_quantity(&item, items);
                vendor_command(
                    ui,
                    VendorCommand::AddToBuyList {
                        item: ObjectRef::Guid(item.item_guid),
                        amount,
                    },
                );
            }
        } else if e == self.buy_item {
            self.buy_selected(ui, items, true);
        } else if e == self.buy_all {
            if !view.lists.buying.is_empty() {
                vendor_command(ui, VendorCommand::BuyAll);
            }
        } else if e == self.buy_clear_item {
            if let Some(g) = selected {
                let amount = self.listing(g).map_or(-1, removal_amount);
                vendor_command(
                    ui,
                    VendorCommand::RemoveFromBuyList {
                        item: ObjectRef::Guid(g),
                        amount,
                    },
                );
            }
        } else if e == self.buy_clear_list {
            vendor_command(ui, VendorCommand::ClearBuyList);
        } else if e == self.sell_item {
            self.sell_selected(ui, items);
        } else if e == self.sell_all {
            if !view.lists.selling.is_empty() {
                vendor_command(ui, VendorCommand::SellAll);
            }
        } else if e == self.sell_clear_item {
            if let Some(g) = selected {
                vendor_command(ui, VendorCommand::RemoveFromSellList(ObjectRef::Guid(g)));
            }
        } else if e == self.sell_clear_list {
            vendor_command(ui, VendorCommand::ClearSellList);
        }
    }

    /// The listing's buy button (or the buying page's, which also takes the
    /// row off the buy list when the purchase goes out): the purchase must
    /// fit in the main pack.
    fn buy_selected(&mut self, ui: &mut UiRoot, items: &ItemsView<'_>, from_buy_list: bool) {
        let Some(item) = self.selection.get().and_then(|g| self.listing(g).cloned()) else {
            return;
        };
        let quantity = self.buy_quantity(&item, items);
        let listing = self.view.as_ref().map_or(&[][..], |v| &v.items[..]);
        let rows = [(quantity, item.item_guid)];
        if from_buy_list && !has_room_in(&rows, listing, items.objects, items.player) {
            notice(ui, NOT_ENOUGH_ROOM);
            return;
        }
        session(
            ui,
            SessionCommand::Item(ItemCommand::Buy(vec![(
                ObjectRef::Guid(item.item_guid),
                quantity,
            )])),
        );
        // A purchase that cannot go out (one already in flight) is refused
        // by the runtime and leaves the row.
        if from_buy_list && items.can_make_request {
            vendor_command(
                ui,
                VendorCommand::RemoveFromBuyList {
                    item: ObjectRef::Guid(item.item_guid),
                    amount: removal_amount(&item),
                },
            );
        }
    }

    /// The selling page's sell button: the selection (staged or not) is sold
    /// as one; part of a stack cannot be.
    fn sell_selected(&mut self, ui: &mut UiRoot, items: &ItemsView<'_>) {
        let Some(guid) = self.selection.get() else {
            return;
        };
        let Some(item) = items.get(guid) else {
            return;
        };
        let full = item.stack_size.max(1).unsigned_abs();
        if full > 1 && split_of(items, guid, Some(guid), full) < full {
            notice(ui, CANNOT_SELL_PARTIAL_STACK);
            return;
        }
        session(
            ui,
            SessionCommand::Item(ItemCommand::Sell(vec![(ObjectRef::Guid(guid), 1)])),
        );
        if items.can_make_request {
            vendor_command(ui, VendorCommand::RemoveFromSellList(ObjectRef::Guid(guid)));
        }
    }

    fn staged_name(&self, guid: u32, selling: bool) -> String {
        self.view
            .as_ref()
            .and_then(|v| {
                let rows = if selling {
                    &v.lists.selling
                } else {
                    &v.lists.buying
                };
                rows.iter().find(|r| r.item_guid == guid)
            })
            .map(|r| r.name.clone())
            .filter(|n| !n.trim().is_empty())
            .unwrap_or_else(|| "that item".to_owned())
    }

    fn is_staged(&self, guid: u32, selling: bool) -> bool {
        self.view.as_ref().is_some_and(|v| {
            let rows = if selling {
                &v.lists.selling
            } else {
                &v.lists.buying
            };
            rows.iter().any(|r| r.item_guid == guid)
        })
    }

    fn handle_cell(
        &mut self,
        ui: &mut UiRoot,
        list: ElementId,
        cell: ElementId,
        message: &ElementMessage,
        items: &ItemsView<'_>,
    ) {
        let id = ui.widget::<ItemSlot>(cell).map_or(0, |s| s.object_id);
        let selling = Some(list) == self.selling_list;
        let buying = Some(list) == self.buying_list;
        match message {
            ElementMessage::ItemDragEntered(p) => {
                let a = if selling && self.can_sell(p, items) {
                    DragAcceptance::Accept
                } else {
                    DragAcceptance::Reject
                };
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) if selling => self.drop_to_sell(ui, p, items),
            ElementMessage::ItemDragLifted(p) if selling && p.obj_id != 0 => {
                self.select(ui, p.obj_id);
                vendor_command(
                    ui,
                    VendorCommand::RemoveFromSellList(ObjectRef::Guid(p.obj_id)),
                );
                if let Some(item) = items.get(p.obj_id) {
                    let full = item.stack_size.max(1).unsigned_abs();
                    if split_of(items, p.obj_id, Some(p.obj_id), full) < full {
                        notice(ui, CANNOT_SPLIT_HERE_MESSAGE);
                        emit(ui, PanelOutput::ResetSplitQuantity { stack: full });
                    }
                }
            }
            _ if id == 0 => {}
            ElementMessage::Pressed | ElementMessage::Clicked { .. } => self.select(ui, id),
            ElementMessage::RightClicked => {
                self.select(ui, id);
                session(
                    ui,
                    SessionCommand::Item(ItemCommand::Appraise(ObjectRef::Guid(id))),
                );
            }
            ElementMessage::DoubleClicked if buying && self.is_staged(id, false) => {
                self.select(ui, id);
                notice(
                    ui,
                    &format!(
                        "Removing {} from shopping list",
                        self.staged_name(id, false)
                    ),
                );
                vendor_command(
                    ui,
                    VendorCommand::RemoveFromBuyList {
                        item: ObjectRef::Guid(id),
                        amount: 1,
                    },
                );
            }
            ElementMessage::DoubleClicked if selling && self.is_staged(id, true) => {
                self.select(ui, id);
                notice(
                    ui,
                    &format!("Removing {} from shopping list", self.staged_name(id, true)),
                );
                vendor_command(ui, VendorCommand::RemoveFromSellList(ObjectRef::Guid(id)));
            }
            ElementMessage::DoubleClicked if !buying && !selling => {
                // A listing: select it and buy.
                self.select(ui, id);
                self.buy_selected(ui, items, false);
            }
            _ => {}
        }
    }

    fn can_sell(&self, p: &ItemDragPayload, items: &ItemsView<'_>) -> bool {
        p.obj_id != 0
            && !p.source_kind.is_alias()
            && self
                .view
                .as_ref()
                .is_some_and(|v| sell_check(items, &v.profile, p.obj_id).is_ok())
    }

    /// An item dropped on the sell list: refused with the vendor's reason,
    /// else staged (a pack holding items stages its contents; part of a
    /// stack is split off first and the new stack staged when it appears).
    fn drop_to_sell(&mut self, ui: &mut UiRoot, p: &ItemDragPayload, items: &ItemsView<'_>) {
        let Some(view) = self.view.clone() else {
            return;
        };
        if p.obj_id == 0 || p.source_kind.is_alias() {
            return;
        }
        if let Err(r) = sell_check(items, &view.profile, p.obj_id) {
            notice(ui, r.message());
            return;
        }
        self.show_tab(ui, VendorTab::Selling);
        self.select(ui, p.obj_id);
        let Some(item) = items.get(p.obj_id) else {
            return;
        };
        let has_contents = !items.objects.get_contents(p.obj_id).is_empty();
        let full = item.stack_size.max(1);
        let quantity = i32::try_from(split_of(
            items,
            p.obj_id,
            self.selection.get(),
            full.unsigned_abs(),
        ))
        .unwrap_or(i32::MAX)
        .min(full);
        if !has_contents && quantity < full {
            self.split_to_sell(ui, item, quantity, items);
            return;
        }
        vendor_command(ui, VendorCommand::AddToSellList(ObjectRef::Guid(p.obj_id)));
    }

    fn split_to_sell(
        &mut self,
        ui: &mut UiRoot,
        item: &ClientObject,
        quantity: i32,
        items: &ItemsView<'_>,
    ) {
        if !items.can_make_request {
            notice(ui, CANNOT_SPLIT_TO_SELL_MESSAGE);
            return;
        }
        let guid = item.object_id();
        self.pending_split = Some(PendingSplit {
            source: guid,
            weenie_class_id: item.weenie_class_id,
            quantity,
            seen_in_flight: false,
        });
        session(
            ui,
            SessionCommand::Item(ItemCommand::MoveToContainer {
                item: ObjectRef::Guid(guid),
                container: ObjectRef::Guid(item.container_id),
                slot: 0,
                amount: quantity.unsigned_abs(),
                join_stack: false,
            }),
        );
        let name = if item.name.trim().is_empty() {
            "item"
        } else {
            item.name.as_str()
        };
        notice(ui, &format!("Splitting the {name} before selling them"));
    }

    /// The new stack of a split asked for to sell part of a stack: staged
    /// once it is in the player's pack; forgotten when the split failed
    /// (the request ended without one).
    fn resolve_pending_split(&mut self, ui: &mut UiRoot, items: &ItemsView<'_>) {
        let Some(mut pending) = self.pending_split else {
            return;
        };
        let found = items.objects.objects().find(|o| {
            o.object_id() != pending.source
                && o.weenie_class_id == pending.weenie_class_id
                && o.stack_size == pending.quantity
                && items
                    .objects
                    .is_owned_by_object(o.object_id(), items.player)
        });
        if let Some(o) = found {
            self.pending_split = None;
            vendor_command(
                ui,
                VendorCommand::AddToSellList(ObjectRef::Guid(o.object_id())),
            );
            return;
        }
        if items.is_pending(pending.source) {
            pending.seen_in_flight = true;
            self.pending_split = Some(pending);
        } else if pending.seen_in_flight {
            self.pending_split = None;
        }
    }

    fn close_pressed(&mut self, ui: &mut UiRoot) {
        let staged = self
            .view
            .as_ref()
            .is_some_and(|v| !v.lists.buying.is_empty() || !v.lists.selling.is_empty());
        if !staged {
            ui.hide_window(&self.window);
            return;
        }
        emit(
            ui,
            PanelOutput::Confirm {
                message: CLOSE_CONFIRMATION_MESSAGE.to_owned(),
                on_yes: Box::new(PanelOutput::ClosePanel(crate::panels::panel_id::VENDOR)),
            },
        );
    }

    /// The close confirmation was answered yes: hide the window (the shop
    /// and its lists stay).
    pub fn confirm_close(&mut self, ui: &mut UiRoot) {
        ui.hide_window(&self.window);
    }

    fn poll_drag_over(&mut self, ui: &mut UiRoot) {
        if self.tab == VendorTab::Selling || !ui.is_window_visible(&self.window) {
            return;
        }
        if ui.drag_source().is_none() {
            return;
        }
        let Some(frame) = ui.window(&self.window).map(|w| w.frame) else {
            return;
        };
        let (x0, y0) = ui.screen_position(frame);
        let (w, h) = ui
            .element(frame)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let (mx, my) = ui.mouse();
        #[allow(clippy::cast_precision_loss)]
        let (mx, my) = (mx as f32, my as f32);
        if mx > x0 && mx < x0 + w && my > y0 && my < y0 + h {
            self.show_tab(ui, VendorTab::Selling);
        }
    }
}

/// How much of a listing the buy list gives up when its row is bought or
/// cleared: the whole row for a stacking kind, else one.
fn removal_amount(item: &VendorShopItem) -> i32 {
    if item.max_stack_size.unwrap_or(1) > 1 {
        -1
    } else {
        1
    }
}

fn listing_cell(
    ui: &mut UiRoot,
    list: ElementId,
    item: &VendorShopItem,
    items: &ItemsView<'_>,
    icons: &mut dyn IconSource,
) -> ElementId {
    let icon = icons.item_icon(
        ItemType(item.item_type.unwrap_or(0)),
        item.icon_id,
        item.icon_underlay_id,
        item.icon_overlay_id,
        item.effects,
    );
    let index = ui.widget::<ItemList>(list).map_or(0, ItemList::num_items);
    let mut slot = ItemSlot {
        slot_index: i32::try_from(index).unwrap_or(i32::MAX),
        allow_drag_source: false,
        tooltip: items.tooltip(item.item_guid),
        ..ItemSlot::default()
    };
    slot.set_item(item.item_guid, icon, None, None);
    let cell = ui.insert(slot_element(), slot);
    ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
    cell
}

impl PanelController for VendorController {
    fn tick(&mut self, ui: &mut UiRoot, _dt: f64) {
        self.poll_drag_over(ui);
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if element == self.items_tab || element == self.buying_tab || element == self.selling_tab {
            if clicked {
                let tab = if element == self.items_tab {
                    VendorTab::Items
                } else if element == self.buying_tab {
                    VendorTab::Buying
                } else {
                    VendorTab::Selling
                };
                self.show_tab(ui, tab);
            }
            return true;
        }
        if Some(element) == self.close {
            if clicked {
                self.close_pressed(ui);
            }
            return true;
        }
        if element == self.type_menu {
            if matches!(message, ElementMessage::MenuSelected(_)) {
                self.queue.push((element, message.clone()));
            }
            return true;
        }
        if self.list_of(ui, element).is_some() {
            if let ElementMessage::ItemDragEntered(_) = message
                && let Some(s) = ui.widget_mut::<ItemSlot>(element)
            {
                // Refused until the next update answers.
                s.set_drag_acceptance(DragAcceptance::Reject);
            }
            self.queue.push((element, message.clone()));
            return true;
        }
        let ours = [
            self.buy,
            self.add,
            self.buy_item,
            self.buy_all,
            self.buy_clear_item,
            self.buy_clear_list,
            self.sell_item,
            self.sell_all,
            self.sell_clear_item,
            self.sell_clear_list,
        ]
        .contains(&Some(element));
        if ours {
            if clicked {
                self.queue.push((element, message.clone()));
            }
            return true;
        }
        false
    }
}
