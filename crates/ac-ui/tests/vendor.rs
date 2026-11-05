//! The vendor window.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/VendorUiControllerTests.cs`
//! (the window's own rules: categories, the listing row, the selection's
//! name and cost line, the buttons, the tabs, the sell list's drag and drop,
//! the close confirmation) on a synthetic window shaped like the retail one
//! (layout 0x21000012; the real one is imported in `real_dats`). The buy and
//! sell lists themselves, their money and room checks and their texts are
//! the runtime's (`ac-runtime` `inventory::shop` tests):
//! here the runtime's answer is a `VendorView` handed to
//! `on_vendor_event`, and OpenAC's `TryBuy`, `TryBuyAll`, `TrySell` and
//! staging calls are `SessionCommand::Item` outputs. OpenAC's
//! `SelectionState` and `StackSplitQuantityState` are the `ItemsView`'s
//! selection and split quantity.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{
    ClientObject, ItemType, StackSplitQuantity, VendorShopItem, VendorShopProfile,
};
use ac_core::properties::PropertyInt;
use ac_runtime::inventory::shop::{StagedRow, VendorListsView};
use ac_runtime::inventory::{ItemCommand, ObjectRef, VendorCommand, VendorView, VendorViewReason};
use ac_runtime::session::SessionCommand;
use ac_ui::element_info::ElementInfo;
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::items::ItemsView;
use ac_ui::panels::vendor::{self, VendorController, VendorSprites, VendorTab};
use ac_ui::panels::{PanelOutput, panel_id};
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{ItemList, ItemSlot, Menu, Panel, ScrollSource, Scrollbar, Text};
use ac_ui::{ElementId, MouseButton, UiOutput};
use common::items::*;
use common::panels::*;
use common::*;

const WINDOW: &str = "vendor";
const VENDOR_GUID: u32 = 0x7000_0010;
const ARMOR: u32 = 0x6000_0101;
const FOOD: u32 = 0x6000_0102;
const STACKED: u32 = 0x6000_0103;
const OWNED_ARMOR: u32 = 0x6000_0201;
const OWNED_WEAPON: u32 = 0x6000_0202;
const COINS: i32 = 1500;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

fn listing(guid: u32, name: &str, item_type: ItemType, icon: u32, value: i32) -> VendorShopItem {
    VendorShopItem {
        item_guid: guid,
        stack_size: -1,
        weenie_class_id: 2,
        name: Some(name.to_owned()),
        item_type: Some(item_type.bits()),
        icon_id: icon,
        value: Some(value),
        ..VendorShopItem::default()
    }
}

fn profile(sell_rate: f32) -> VendorShopProfile {
    VendorShopProfile {
        merchandise_max_value: u32::MAX,
        buy_price: 1.0,
        sell_price: sell_rate,
        ..VendorShopProfile::default()
    }
}

fn sell_profile(types: ItemType) -> VendorShopProfile {
    VendorShopProfile {
        merchandise_item_types: types.bits(),
        merchandise_max_value: u32::MAX,
        buy_price: 1.0,
        sell_price: 1.5,
        ..VendorShopProfile::default()
    }
}

fn shop(
    reason: VendorViewReason,
    profile: VendorShopProfile,
    items: Vec<VendorShopItem>,
) -> VendorView {
    VendorView {
        vendor_guid: VENDOR_GUID,
        name: Some("Vendor".into()),
        profile,
        items,
        lists: VendorListsView::default(),
        reason,
    }
}

fn row(guid: u32, name: &str, quantity: i32) -> StagedRow {
    StagedRow {
        item_guid: guid,
        name: name.to_owned(),
        quantity,
        price: 0,
    }
}

struct Vendor {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    objects: Objects,
    icons: TestIcons,
    split: Option<StackSplitQuantity>,
    selected: Option<u32>,
    can_request: bool,
    pending: Vec<u32>,
}

fn tagged(
    ui: &mut UiRoot,
    ids: &mut HashMap<u32, ElementId>,
    parent: ElementId,
    id: u32,
) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 800.0, 110.0);
    e.dat_element_id = id;
    let p = ui.add(parent, e, Panel::default());
    ids.insert(id, p);
    p
}

fn vendor_window() -> Vendor {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 800.0, 110.0));
    let mut ids = HashMap::new();
    for id in [
        vendor::ITEMS_TAB_ID,
        vendor::BUYING_TAB_ID,
        vendor::SELLING_TAB_ID,
        vendor::CLOSE_ID,
    ] {
        add_std_button(ui, &mut ids, root, id);
    }
    let items = tagged(ui, &mut ids, root, vendor::ITEMS_PAGE_ID);
    let buying = tagged(ui, &mut ids, root, vendor::BUYING_PAGE_ID);
    let selling = tagged(ui, &mut ids, root, vendor::SELLING_PAGE_ID);
    ids.insert(vendor::ITEM_LIST_ID, list(ui, items, 0.0, 0.0, 320.0, 32.0));
    ids.insert(
        vendor::BUYING_LIST_ID,
        list(ui, buying, 0.0, 0.0, 320.0, 32.0),
    );
    ids.insert(
        vendor::SELLING_LIST_ID,
        list(ui, selling, 0.0, 0.0, 320.0, 32.0),
    );
    for (page, id) in [
        (items, vendor::ITEM_SCROLLBAR_ID),
        (buying, vendor::BUYING_SCROLLBAR_ID),
        (selling, vendor::SELLING_SCROLLBAR_ID),
    ] {
        ids.insert(
            id,
            ui.add(page, at(0.0, 40.0, 320.0, 16.0), Scrollbar::default()),
        );
    }
    let mut e = at(330.0, 0.0, 120.0, 18.0);
    Menu::configure(&mut e);
    ids.insert(
        vendor::TYPE_FILTER_MENU_ID,
        ui.add(items, e, Menu::default()),
    );
    for id in [vendor::ITEM_NAME_TEXT_ID, vendor::ITEM_COST_TEXT_ID] {
        add_std_text(ui, &mut ids, items, id);
    }
    for id in [vendor::BUY_BUTTON_ID, vendor::ADD_BUTTON_ID] {
        add_std_button(ui, &mut ids, items, id);
    }
    for id in [
        vendor::BUY_ITEM_BUTTON_ID,
        vendor::BUY_ALL_BUTTON_ID,
        vendor::BUY_CLEAR_ITEM_BUTTON_ID,
        vendor::BUY_CLEAR_LIST_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, buying, id);
    }
    for id in [vendor::BUYING_LIST_TEXT_ID, vendor::BUYING_PURSE_TEXT_ID] {
        add_std_text(ui, &mut ids, buying, id);
    }
    for id in [
        vendor::SELL_ITEM_BUTTON_ID,
        vendor::SELL_ALL_BUTTON_ID,
        vendor::SELL_CLEAR_ITEM_BUTTON_ID,
        vendor::SELL_CLEAR_LIST_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, selling, id);
    }
    for id in [vendor::SELLING_LIST_TEXT_ID, vendor::SELLING_PURSE_TEXT_ID] {
        add_std_text(ui, &mut ids, selling, id);
    }
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window(WINDOW, root, None, 0).unwrap();
    let c =
        VendorController::bind(ui, &layout, WINDOW, VendorSprites::default(), None).expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    objects
        .get_mut(PLAYER)
        .properties
        .ints
        .insert(PropertyInt::COIN_VALUE.0, COINS);
    Vendor {
        host,
        ids,
        objects,
        icons: TestIcons::default(),
        split: None,
        selected: None,
        can_request: true,
        pending: Vec::new(),
    }
}

impl Vendor {
    /// Follow the panel's own selections, as the runtime would.
    fn follow(&mut self, outputs: &[PanelOutput]) {
        for o in outputs {
            match o {
                PanelOutput::SelectObject(g) => self.selected = Some(*g),
                PanelOutput::Session(SessionCommand::Gameplay(
                    ac_runtime::gameplay::GameplayCommand::ClearSelection,
                )) => self.selected = None,
                _ => {}
            }
        }
    }

    fn event(&mut self, view: Option<&VendorView>) -> Vec<PanelOutput> {
        let items = ItemsView {
            selected: self.selected,
            split: self.split.as_ref(),
            ..ItemsView::new(&self.objects.t, PLAYER)
        };
        let icons = &mut self.icons;
        self.host
            .with_controller::<VendorController, _>(WINDOW, |c, ui| {
                c.on_vendor_event(ui, view, &items, icons);
            })
            .unwrap();
        let out = panel_outputs(&mut self.host);
        self.follow(&out);
        let mut more = self.update();
        let mut all = out;
        all.append(&mut more);
        all
    }

    fn update(&mut self) -> Vec<PanelOutput> {
        let selected = self.selected;
        let split = self.split.clone();
        let pending = self.pending.clone();
        let items = ItemsView {
            selected,
            split: split.as_ref(),
            can_make_request: self.can_request,
            pending_sources: &pending,
            ..ItemsView::new(&self.objects.t, PLAYER)
        };
        let icons = &mut self.icons;
        self.host
            .with_controller::<VendorController, _>(WINDOW, |c, ui| c.update(ui, &items, icons))
            .unwrap();
        let out = panel_outputs(&mut self.host);
        self.follow(&out);
        out
    }

    fn fire(
        &mut self,
        element: ElementId,
        k: i32,
        payload: Option<DragPayload>,
    ) -> Vec<PanelOutput> {
        panel_outputs(&mut self.host);
        self.host.root.fire_event(k, element, payload);
        self.host.pump();
        let mut out = panel_outputs(&mut self.host);
        self.follow(&out);
        out.extend(self.update());
        out
    }

    fn click(&mut self, id: u32) -> Vec<PanelOutput> {
        let e = self.ids[&id];
        self.fire(e, kind::CLICK, None)
    }

    fn cell(&self, list: u32, i: usize) -> ElementId {
        self.host
            .root
            .widget::<ItemList>(self.ids[&list])
            .unwrap()
            .item(i)
            .unwrap()
    }

    fn slot(&self, list: u32, i: usize) -> &ItemSlot {
        self.host
            .root
            .widget::<ItemSlot>(self.cell(list, i))
            .unwrap()
    }

    fn occupied(&self, list: u32) -> Vec<u32> {
        let l = self.host.root.widget::<ItemList>(self.ids[&list]).unwrap();
        l.cells()
            .iter()
            .filter_map(|c| self.host.root.widget::<ItemSlot>(*c))
            .map(|s| s.object_id)
            .filter(|id| *id != 0)
            .collect()
    }

    fn text(&self, id: u32) -> String {
        text_of(&self.host.root, self.ids[&id])
    }

    fn menu(&self) -> &Menu {
        self.host
            .root
            .widget::<Menu>(self.ids[&vendor::TYPE_FILTER_MENU_ID])
            .unwrap()
    }

    fn select_category(&mut self, label: &str) -> Vec<PanelOutput> {
        let payload = self
            .menu()
            .items
            .iter()
            .find(|i| i.label == label)
            .unwrap()
            .payload;
        let m = self.ids[&vendor::TYPE_FILTER_MENU_ID];
        self.host
            .root
            .send_message(m, ac_ui::root::ElementMessage::MenuSelected(payload));
        self.host.pump();
        let mut out = panel_outputs(&mut self.host);
        self.follow(&out);
        out.extend(self.update());
        out
    }

    fn enabled(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().enabled
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }

    fn with<R>(&mut self, f: impl FnOnce(&mut VendorController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<VendorController, _>(WINDOW, f)
            .unwrap()
    }

    fn owned(&mut self, guid: u32, item_type: ItemType, value: i32, stack: i32, wcid: u32) {
        let mut o = ClientObject::new(guid);
        o.name = format!("Item {guid:08X}");
        o.item_type = item_type;
        o.value = value;
        o.stack_size = stack;
        o.weenie_class_id = wcid;
        self.objects.add(o);
        self.objects.mv(guid, PLAYER, 0);
    }
}

fn notices(out: &[PanelOutput]) -> Vec<String> {
    out.iter()
        .filter_map(|o| match o {
            PanelOutput::Notice(t) => Some(t.clone()),
            _ => None,
        })
        .collect()
}

fn items_commands(out: &[PanelOutput]) -> Vec<ItemCommand> {
    sessions(out)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(i) => Some(i),
            _ => None,
        })
        .collect()
}

fn armor_and_food() -> Vec<VendorShopItem> {
    vec![
        listing(FOOD, "Bread", ItemType::FOOD, 100, 5),
        listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500),
    ]
}

// ── Bind ────────────────────────────────────────────────────────────────

// Bind_WiresTheScrollbarToTheItemListsScrollModel / ItemList_FillsVisibleEmptySlots /
// Bind_FromRealDatFixture_BuyingAndSellingLists_ConfiguredForEmptySlotFill
#[test]
fn the_three_rows_are_sideways_rows_with_their_scrollbars() {
    let v = vendor_window();
    for (list, bar) in [
        (vendor::ITEM_LIST_ID, vendor::ITEM_SCROLLBAR_ID),
        (vendor::BUYING_LIST_ID, vendor::BUYING_SCROLLBAR_ID),
        (vendor::SELLING_LIST_ID, vendor::SELLING_SCROLLBAR_ID),
    ] {
        let l = v.host.root.widget::<ItemList>(v.ids[&list]).unwrap();
        assert!(l.single_row && l.horizontal_scroll && l.fill_visible_empty_slots);
        assert_eq!((l.cell_width, l.cell_height), (32.0, 32.0));
        assert!(l.empty_slot_factory.is_some());
        let b = v.host.root.widget::<Scrollbar>(v.ids[&bar]).unwrap();
        assert!(b.horizontal);
        assert_dbg_eq!(b.model, ScrollSource::Element(v.ids[&list]));
    }
}

// CategoryMenu_OpensDownward_... / CategoryMenu_ArrowCapSprites_... / CategoryMenu_TextIndentsAreFlushLeft_...
#[test]
fn the_category_menu_is_configured_as_retail() {
    let v = vendor_window();
    let m = v.menu();
    assert_eq!(m.rows_per_column, 6);
    assert_eq!(m.row_height, 18.0);
    assert_eq!(m.column_width, 100.0);
    assert!(m.popup_size_to_content && m.popup_scrollbar_hide_when_disabled);
    assert!(!m.open_upward);
    assert_ne!(m.arrow_cap_closed_sprite, m.arrow_cap_open_sprite);
    assert_ne!(m.normal_sprite, 0);
    assert_ne!(m.item_normal_sprite, 0);
    assert_ne!(m.item_highlight_sprite, 0);
    assert_eq!((m.text_indent, m.button_text_indent), (0.0, 0.0));
}

// ── Opening and closing ─────────────────────────────────────────────────

// Opened_ShowsWindowAndPopulatesFirstPresentCategoryInTableOrder
#[test]
fn opening_shows_the_window_on_the_first_category_in_table_order() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert!(v.host.root.is_window_visible(WINDOW));
    assert!(v.visible(vendor::ITEMS_PAGE_ID));
    assert!(!v.visible(vendor::BUYING_PAGE_ID));
    assert!(!v.visible(vendor::SELLING_PAGE_ID));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
    assert_eq!(v.menu().button_label, "Armor");
    assert_eq!(
        v.menu()
            .items
            .iter()
            .map(|i| i.label.as_str())
            .collect::<Vec<_>>(),
        ["Armor", "Food"]
    );
    // The first listing is selected.
    assert_eq!(v.selected, Some(ARMOR));
    assert!(v.slot(vendor::ITEM_LIST_ID, 0).selected);
}

// Closed_HidesWindowAndClearsListAndText / Reset_HidesWindowAndClearsContent
#[test]
fn closing_hides_the_window_and_clears_it() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert!(!v.text(vendor::ITEM_NAME_TEXT_ID).is_empty());
    v.event(None);
    assert!(!v.host.root.is_window_visible(WINDOW));
    assert!(v.occupied(vendor::ITEM_LIST_ID).is_empty());
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "");
    assert_eq!(v.text(vendor::ITEM_COST_TEXT_ID), "");
    assert!(v.menu().items.is_empty());
}

// HoverCaption_carriesTheMaterialPrefix_andStaysPlainWithoutOne
#[test]
fn a_listings_hover_caption_carries_the_material_prefix() {
    let mut v = vendor_window();
    v.objects.add(material_item(ARMOR));
    let names = |o: &ClientObject| composed_name(o);
    let resolver: ac_ui::panels::items::NameResolver<'_> = &names;
    let items = ItemsView {
        names: Some(resolver),
        ..ItemsView::new(&v.objects.t, PLAYER)
    };
    let view = shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500)],
    );
    let icons = &mut v.icons;
    v.host
        .with_controller::<VendorController, _>(WINDOW, |c, ui| {
            c.on_vendor_event(ui, Some(&view), &items, icons);
        })
        .unwrap();
    assert_eq!(
        v.slot(vendor::ITEM_LIST_ID, 0).tooltip.as_deref(),
        Some("Pyreal Scarab")
    );
}

// ShopItem_WithIconUnderlayOverlayEffects_ForwardsThemToResolveIcon / ShopRow_NeverMintsADragPayload
#[test]
fn a_listing_cell_asks_for_its_icon_and_is_never_a_drag_source() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500)],
    )));
    assert!(v.icons.asked.contains(&(ItemType::ARMOR, 200)));
    assert!(!v.slot(vendor::ITEM_LIST_ID, 0).allow_drag_source);
}

// Opened_WithDifferentVendor_ResetsToFirstPresentCategory_NotThePreviousVendors /
// Refreshed_SameVendor_PreservesCategorySelection
#[test]
fn a_refresh_keeps_the_category_and_a_new_shop_starts_at_the_first() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    v.select_category("Food");
    assert_eq!(v.menu().button_label, "Food");
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        armor_and_food(),
    )));
    assert_eq!(v.menu().button_label, "Food");
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert_eq!(v.menu().button_label, "Armor");
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
}

// Refreshed_SameVendor_ReselectsFirstItemUnconditionally_NotThePreviouslySelectedSurvivor
#[test]
fn a_refresh_reselects_the_first_listing() {
    let mut v = vendor_window();
    let items = vec![
        listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500),
        listing(0x6000_0110, "Buckler", ItemType::ARMOR, 900, 40),
    ];
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        items.clone(),
    )));
    let second = v.cell(vendor::ITEM_LIST_ID, 1);
    v.fire(second, kind::CLICK, None);
    assert_eq!(v.selected, Some(0x6000_0110));
    assert!(v.slot(vendor::ITEM_LIST_ID, 1).selected);
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        items,
    )));
    assert!(v.slot(vendor::ITEM_LIST_ID, 0).selected);
    assert!(!v.slot(vendor::ITEM_LIST_ID, 1).selected);
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "Chainmail");
}

// SelectingDifferentCategory_FiltersItemListToThatCategoryOnly / _AutoSelectsFirstFilteredItem / _ResetsListScrollToStart
#[test]
fn a_category_filters_the_row_and_selects_its_first_listing() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    v.select_category("Food");
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [FOOD]);
    assert_eq!(v.selected, Some(FOOD));
    assert!(v.slot(vendor::ITEM_LIST_ID, 0).selected);
    assert!(!v.text(vendor::ITEM_NAME_TEXT_ID).is_empty());
    assert!(!v.text(vendor::ITEM_COST_TEXT_ID).is_empty());
    assert!(v.enabled(vendor::BUY_BUTTON_ID));
    let l = v
        .host
        .root
        .widget::<ItemList>(v.ids[&vendor::ITEM_LIST_ID])
        .unwrap();
    assert_eq!(l.scroll.scroll_y(), 0);
}

/// A shop with more armor than the row shows at once.
fn long_armor_row() -> Vec<VendorShopItem> {
    (0..20)
        .map(|i| listing(0x6000_0300 + i, "Buckler", ItemType::ARMOR, 900 + i, 40))
        .collect()
}

fn item_row_scroll(v: &Vendor) -> i32 {
    v.host
        .root
        .widget::<ItemList>(v.ids[&vendor::ITEM_LIST_ID])
        .unwrap()
        .scroll
        .scroll_y()
}

fn scroll_item_row_to(v: &mut Vendor, x: i32) {
    let l = v.ids[&vendor::ITEM_LIST_ID];
    v.host
        .root
        .widget_mut::<ItemList>(l)
        .unwrap()
        .scroll
        .set_scroll_y(x);
}

// The shop sent again after a purchase keeps the row scrolled where it was,
// so listings far along it can be bought one after another.
#[test]
fn a_refresh_keeps_the_rows_sideways_scroll() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        long_armor_row(),
    )));
    scroll_item_row_to(&mut v, 160);
    assert_eq!(item_row_scroll(&v), 160);
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        long_armor_row(),
    )));
    assert_eq!(item_row_scroll(&v), 160);
}

// A refill with fewer listings keeps as much of the scroll as still fits.
#[test]
fn a_refresh_with_a_shorter_row_clamps_the_kept_scroll() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        long_armor_row(),
    )));
    scroll_item_row_to(&mut v, 320);
    assert_eq!(item_row_scroll(&v), 320);
    let mut shorter = long_armor_row();
    shorter.truncate(14);
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        shorter,
    )));
    let l = v
        .host
        .root
        .widget::<ItemList>(v.ids[&vendor::ITEM_LIST_ID])
        .unwrap();
    assert!(l.scroll.max_scroll() > 0 && l.scroll.max_scroll() < 320);
    assert_eq!(item_row_scroll(&v), l.scroll.max_scroll());
}

// Another shop opening starts its row at the beginning.
#[test]
fn a_newly_opened_shop_starts_the_row_at_the_beginning() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        long_armor_row(),
    )));
    scroll_item_row_to(&mut v, 160);
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        long_armor_row(),
    )));
    assert_eq!(item_row_scroll(&v), 0);
}

// ── The selection's name and cost ───────────────────────────────────────

fn stacked_arrows() -> VendorShopItem {
    VendorShopItem {
        desc_stack_size: Some(100),
        ..listing(STACKED, "Arrows", ItemType::MISSILE_WEAPON, 300, 1000)
    }
}

fn split(stack: u32, value: u32) -> Option<StackSplitQuantity> {
    let mut s = StackSplitQuantity::new();
    s.reset(stack, Some(value));
    Some(s)
}

// SelectingItem_NotInSplitExemptMask_ShowsWholeStackPriceAndFallbackPluralName
#[test]
fn a_stack_shows_its_quantity_and_the_whole_price() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(2.0),
        vec![stacked_arrows()],
    )));
    v.split = split(100, 100);
    v.update();
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "100 Arrows");
    assert_eq!(
        v.text(vendor::ITEM_COST_TEXT_ID),
        "cost 2,000p (you have 1,500p)"
    );
}

// SelectingItem_InSplitExemptMask_ShowsPerUnitPriceAndSingularName
#[test]
fn an_exempt_kind_shows_one_unit() {
    let mut v = vendor_window();
    let bread = VendorShopItem {
        desc_stack_size: Some(50),
        ..listing(FOOD, "Bread", ItemType::FOOD, 100, 500)
    };
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(2.0),
        vec![bread],
    )));
    v.split = split(50, 1);
    v.update();
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "Bread");
    assert_eq!(
        v.text(vendor::ITEM_COST_TEXT_ID),
        "costs 20p (you have 1,500p)"
    );
}

// SelectingItem_WithAuthoredPluralName_UsesItInsteadOfSingular
#[test]
fn the_authored_plural_names_a_stack() {
    let mut v = vendor_window();
    let keys = VendorShopItem {
        desc_stack_size: Some(10),
        plural_name: Some("Iron Keys".into()),
        ..listing(STACKED, "Iron Key", ItemType::KEY, 50, 100)
    };
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![keys],
    )));
    v.split = split(10, 10);
    v.update();
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "10 Iron Keys");
}

// SliderChange_AfterSelection_UpdatesNameAndPriceToLiveQuantity_MatchingWhatBuyWouldCharge
#[test]
fn the_split_quantity_sets_the_name_the_price_and_the_purchase() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(2.0),
        vec![stacked_arrows()],
    )));
    v.split = split(100, 1);
    v.update();
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "Arrows");
    assert_eq!(
        v.text(vendor::ITEM_COST_TEXT_ID),
        "costs 20p (you have 1,500p)"
    );
    v.split = split(100, 40);
    v.update();
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "40 Arrows");
    assert_eq!(
        v.text(vendor::ITEM_COST_TEXT_ID),
        "cost 800p (you have 1,500p)"
    );
    let out = v.click(vendor::BUY_BUTTON_ID);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Buy(vec![(ObjectRef::Guid(STACKED), 40)])]
    );
}

// SelectingItem_WithAlternateCurrency_ShowsFullRetailCostSentence
#[test]
fn an_alternate_currency_shop_words_the_cost_line() {
    let mut v = vendor_window();
    let mut view = shop(
        VendorViewReason::Opened,
        VendorShopProfile {
            alternate_currency_wcid: 0x1234_5678,
            alternate_currency_plural_name: "Trade Notes".into(),
            alternate_currency_amount: 12345,
            ..profile(1.0)
        },
        vec![listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 50)],
    );
    view.lists.money = 12345;
    view.lists.purse_text = "You have 12345 Trade Notes.".into();
    v.event(Some(&view));
    assert_eq!(
        v.text(vendor::ITEM_COST_TEXT_ID),
        "This item costs 50 Trade Notes. You have 12345 Trade Notes."
    );
    assert_eq!(
        v.text(vendor::BUYING_PURSE_TEXT_ID),
        "You have 12345 Trade Notes."
    );
}

// NoSelection_DisablesBuyButton_SelectionEnablesIt / AddButton_EnablesWithSelection_NowThatStagingIsWired
#[test]
fn buy_and_add_follow_the_selection() {
    let mut v = vendor_window();
    assert!(!v.enabled(vendor::BUY_BUTTON_ID));
    assert!(!v.enabled(vendor::ADD_BUTTON_ID));
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert!(v.enabled(vendor::BUY_BUTTON_ID));
    assert!(v.enabled(vendor::ADD_BUTTON_ID));
    // A request in flight holds the buy button.
    v.can_request = false;
    v.update();
    assert!(!v.enabled(vendor::BUY_BUTTON_ID));
    assert!(v.enabled(vendor::ADD_BUTTON_ID));
    v.event(None);
    assert!(!v.enabled(vendor::BUY_BUTTON_ID));
    assert!(!v.enabled(vendor::ADD_BUTTON_ID));
}

// ── Cells ───────────────────────────────────────────────────────────────

// ShopRow_ClickEvent_SelectsItem_DespiteNotBeingADragSource
#[test]
fn a_click_on_a_listing_selects_it() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![
            listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500),
            listing(0x6000_0110, "Buckler", ItemType::ARMOR, 900, 40),
        ],
    )));
    let c = v.cell(vendor::ITEM_LIST_ID, 1);
    v.fire(c, kind::MOUSE_DOWN, None);
    v.fire(c, kind::CLICK, None);
    assert_eq!(v.selected, Some(0x6000_0110));
    assert!(v.slot(vendor::ITEM_LIST_ID, 1).selected);
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "Buckler");
}

// RightClickShopRow_SelectsAndRoutesThroughItemInteractionExamine
#[test]
fn a_right_click_selects_and_examines() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    v.select_category("Food");
    let c = v.cell(vendor::ITEM_LIST_ID, 0);
    v.selected = Some(ARMOR);
    v.update();
    let out = v.fire(c, kind::RIGHT_CLICK, None);
    assert!(items_commands(&out).contains(&ItemCommand::Appraise(ObjectRef::Guid(FOOD))));
    assert_eq!(v.text(vendor::ITEM_NAME_TEXT_ID), "Bread");
}

#[test]
fn a_double_click_on_a_listing_buys_it() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500)],
    )));
    let c = v.cell(vendor::ITEM_LIST_ID, 0);
    let out = v.fire(c, kind::DOUBLE_CLICK, None);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Buy(vec![(ObjectRef::Guid(ARMOR), 1)])]
    );
}

// ── Tabs and close ──────────────────────────────────────────────────────

// SellingTab_SwitchesPage... / BuyingTab_SwitchesPage... / ItemsTab_ReselectedAfterVisitingOtherTabs_ShowsBrowseListAgain
#[test]
fn the_tabs_switch_pages() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    v.click(vendor::SELLING_TAB_ID);
    assert!(v.visible(vendor::SELLING_PAGE_ID));
    assert!(!v.visible(vendor::ITEMS_PAGE_ID));
    assert!(v.occupied(vendor::SELLING_LIST_ID).is_empty());
    v.click(vendor::BUYING_TAB_ID);
    assert!(v.visible(vendor::BUYING_PAGE_ID));
    assert!(v.occupied(vendor::BUYING_LIST_ID).is_empty());
    v.click(vendor::ITEMS_TAB_ID);
    assert!(v.visible(vendor::ITEMS_PAGE_ID));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
    assert_eq!(v.with(|c, _| c.tab()), VendorTab::Items);
}

// The shop sent again after a sale leaves the window on the page the player
// was on; only a newly opened shop picks its page.
#[test]
fn a_refresh_keeps_the_open_tab() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    v.click(vendor::SELLING_TAB_ID);
    assert_eq!(v.with(|c, _| c.tab()), VendorTab::Selling);
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        armor_and_food(),
    )));
    assert_eq!(v.with(|c, _| c.tab()), VendorTab::Selling);
    assert!(v.visible(vendor::SELLING_PAGE_ID));
    assert!(!v.visible(vendor::ITEMS_PAGE_ID));
    // The listing row is still refilled behind the open page.
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert_eq!(v.with(|c, _| c.tab()), VendorTab::Items);
}

// CloseButton_HidesTheWindowOnly_... / CloseButtonPressed_WithNoStaging_HidesImmediatelyWithoutADialog
#[test]
fn close_with_nothing_staged_hides_the_window_only() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    let out = v.click(vendor::CLOSE_ID);
    assert!(!v.host.root.is_window_visible(WINDOW));
    assert!(sessions(&out).is_empty());
    assert!(!out.iter().any(|o| matches!(o, PanelOutput::Confirm { .. })));
    // The shop stays: its refresh shows the window again.
    v.event(Some(&shop(
        VendorViewReason::Refreshed,
        profile(1.5),
        armor_and_food(),
    )));
    assert!(v.host.root.is_window_visible(WINDOW));
}

// CloseButtonPressed_WithStagedBuyItems_ShowsConfirmDialogInsteadOfHidingImmediately / CloseConfirmDialog_Accepted_...
#[test]
fn close_with_staged_items_asks_first() {
    let mut v = vendor_window();
    let mut view = shop(VendorViewReason::Opened, profile(1.5), armor_and_food());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 1)];
    v.event(Some(&view));
    let out = v.click(vendor::CLOSE_ID);
    assert!(v.host.root.is_window_visible(WINDOW));
    assert_dbg_eq!(
        out,
        [PanelOutput::Confirm {
            message: vendor::CLOSE_CONFIRMATION_MESSAGE.to_owned(),
            on_yes: Box::new(PanelOutput::ClosePanel(panel_id::VENDOR)),
        }]
    );
    v.with(|c, ui| c.confirm_close(ui));
    assert!(!v.host.root.is_window_visible(WINDOW));
}

// FillComponentBuyList_StagesEachShortfallAndOpensTheBuyingTab: after
// `/fillcomps` the window shows the spell components and the buying page
// with the staged rows (the runtime fills the lists).
#[test]
fn a_component_fill_shows_the_components_and_the_buying_page() {
    const TAPER: u32 = 0x6000_0120;
    const SCARAB: u32 = 0x6000_0121;
    let mut v = vendor_window();
    let mut items = armor_and_food();
    items.push(listing(
        TAPER,
        "Prismatic Taper",
        ItemType::SPELL_COMPONENTS,
        300,
        5,
    ));
    items.push(listing(
        SCARAB,
        "Lead Scarab",
        ItemType::SPELL_COMPONENTS,
        301,
        10,
    ));
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.0),
        items.clone(),
    )));
    assert_eq!(v.menu().button_label, "Armor");

    let mut filled = shop(VendorViewReason::ComponentsFilled, profile(1.0), items);
    filled.lists.buying = vec![
        row(TAPER, "Prismatic Taper", 8),
        row(SCARAB, "Lead Scarab", 5),
    ];
    v.event(Some(&filled));
    assert_eq!(v.menu().button_label, "Spell Components");
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [TAPER, SCARAB]);
    assert!(v.visible(vendor::BUYING_PAGE_ID));
    assert!(!v.visible(vendor::ITEMS_PAGE_ID));
    assert_eq!(v.occupied(vendor::BUYING_LIST_ID), [TAPER, SCARAB]);

    // A shop without components keeps its category and still shows the
    // buying page.
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.0),
        armor_and_food(),
    )));
    v.event(Some(&shop(
        VendorViewReason::ComponentsFilled,
        profile(1.0),
        armor_and_food(),
    )));
    assert_eq!(v.menu().button_label, "Armor");
    assert!(v.visible(vendor::BUYING_PAGE_ID));
}

// ── Buying ──────────────────────────────────────────────────────────────

// BuyButton_Press_NonStackedItem_BuysQuantityOne / AddToBuyList_StagesTheSelectedItem
#[test]
fn buy_and_add_send_the_selected_listing() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    let out = v.click(vendor::BUY_BUTTON_ID);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Buy(vec![(ObjectRef::Guid(ARMOR), 1)])]
    );
    let out = v.click(vendor::ADD_BUTTON_ID);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::AddToBuyList {
            item: ObjectRef::Guid(ARMOR),
            amount: 1
        })]
    );
}

// BuyButton_NoSelection_PressDoesNothing
#[test]
fn buy_without_a_selection_does_nothing() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        Vec::new(),
    )));
    assert!(items_commands(&v.click(vendor::ADD_BUTTON_ID)).is_empty());
}

// StagingConsumesLimitedShopSupply_HidingTheRowWhenExhaustedAndRestoringItWhenUnstaged /
// UnlimitedSupplyShopItem_IsNeverHiddenNoMatterHowMuchIsStaged
#[test]
fn staging_all_of_a_limited_stock_hides_its_listing() {
    let mut v = vendor_window();
    let limited = VendorShopItem {
        stack_size: 2,
        ..listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500)
    };
    let unlimited = listing(0x6000_0110, "Buckler", ItemType::ARMOR, 900, 40);
    let items = vec![limited, unlimited];
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        items.clone(),
    )));
    let mut view = shop(VendorViewReason::Lists, profile(1.5), items.clone());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 2), row(0x6000_0110, "Buckler", 99)];
    v.event(Some(&view));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [0x6000_0110]);
    assert_eq!(v.occupied(vendor::BUYING_LIST_ID), [ARMOR, 0x6000_0110]);
    v.event(Some(&shop(VendorViewReason::Lists, profile(1.5), items)));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR, 0x6000_0110]);
}

fn zero_stock_chainmail() -> VendorShopItem {
    VendorShopItem {
        stack_size: 0,
        ..listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 100)
    }
}

// OpenAC PR #218 ZeroStockShopItem_IsListedWhenTheVendorOpens:
// servers list many rows with an amount of 0; every row is shown at open.
#[test]
fn a_zero_stock_listing_is_shown_when_the_vendor_opens() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![
            zero_stock_chainmail(),
            listing(0x6000_0110, "Buckler", ItemType::ARMOR, 900, 40),
        ],
    )));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR, 0x6000_0110]);
    assert_eq!(v.selected, Some(ARMOR));
}

#[test]
fn the_remaining_amount_wraps_as_openacs_does_instead_of_panicking() {
    // OpenAC `VendorUiController.IsShopSupplyConsumedByStaging`
    // subtracts unchecked: -2 minus the largest staged quantity wraps to a
    // large remainder, so the row stays listed.
    let mut v = vendor_window();
    let items = vec![VendorShopItem {
        stack_size: -2,
        ..listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 100)
    }];
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        items.clone(),
    )));
    let mut view = shop(VendorViewReason::Lists, profile(1.5), items);
    view.lists.buying = vec![row(ARMOR, "Chainmail", i32::MAX)];
    v.event(Some(&view));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
}

// OpenAC PR #218 StagingAZeroStockShopItem_StagesItAndTakesTheRowOutOfTheList:
// an amount of 0 is limited; staging is not refused, and the first unit
// staged removes the row and clears the selection; clearing brings it back.
#[test]
fn staging_a_zero_stock_listing_stages_it_and_takes_the_row_out() {
    let mut v = vendor_window();
    let items = vec![zero_stock_chainmail()];
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        items.clone(),
    )));
    assert_dbg_eq!(
        items_commands(&v.click(vendor::ADD_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::AddToBuyList {
            item: ObjectRef::Guid(ARMOR),
            amount: 1
        })]
    );
    let mut view = shop(VendorViewReason::Lists, profile(1.5), items.clone());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 1)];
    v.event(Some(&view));
    assert_eq!(v.occupied(vendor::BUYING_LIST_ID), [ARMOR]);
    assert!(v.occupied(vendor::ITEM_LIST_ID).is_empty());
    assert_eq!(v.selected, None);
    assert_dbg_eq!(
        items_commands(&v.click(vendor::BUY_CLEAR_LIST_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::ClearBuyList)]
    );
    v.event(Some(&shop(VendorViewReason::Lists, profile(1.5), items)));
    assert_eq!(v.occupied(vendor::ITEM_LIST_ID), [ARMOR]);
}

// BuyAllButton_SendsOneBatchedBuy... / BuyAllButton_WithNothingStaged_IsANoOp / BuyClearListButton_...
#[test]
fn buy_all_and_clear_list_act_on_the_buy_list() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        armor_and_food(),
    )));
    assert!(items_commands(&v.click(vendor::BUY_ALL_BUTTON_ID)).is_empty());
    let mut view = shop(VendorViewReason::Lists, profile(1.5), armor_and_food());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 1)];
    v.event(Some(&view));
    assert_dbg_eq!(
        items_commands(&v.click(vendor::BUY_ALL_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::BuyAll)]
    );
    assert_dbg_eq!(
        items_commands(&v.click(vendor::BUY_CLEAR_LIST_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::ClearBuyList)]
    );
}

// BuyItemButton_BuysTheSelectedStagedItemAndRemovesItFromStagingOnSuccess /
// BuyItemButton_MainPackFull_RefusesAndLeavesTheStagedRowIntact
#[test]
fn the_buying_pages_buy_takes_the_row_off_only_when_it_fits() {
    let mut v = vendor_window();
    let mut view = shop(VendorViewReason::Opened, profile(1.5), armor_and_food());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 1)];
    v.event(Some(&view));
    let out = v.click(vendor::BUY_ITEM_BUTTON_ID);
    assert_dbg_eq!(
        items_commands(&out),
        [
            ItemCommand::Buy(vec![(ObjectRef::Guid(ARMOR), 1)]),
            ItemCommand::Vendor(VendorCommand::RemoveFromBuyList {
                item: ObjectRef::Guid(ARMOR),
                amount: 1
            })
        ]
    );
    // A full main pack: refused with the retail notice, nothing sent.
    v.objects.player(0, 7);
    let out = v.click(vendor::BUY_ITEM_BUTTON_ID);
    assert!(items_commands(&out).is_empty());
    assert_eq!(
        notices(&out),
        ["You must empty some slots in your backpack first"]
    );
}

// BuyClearItemButton_RemovesOnlyTheSelectedStagedEntryWithoutBuying
#[test]
fn clear_item_takes_the_selection_off_the_buy_list() {
    let mut v = vendor_window();
    let stackable = VendorShopItem {
        max_stack_size: Some(100),
        ..listing(ARMOR, "Chainmail", ItemType::ARMOR, 200, 500)
    };
    v.event(Some(&shop(
        VendorViewReason::Opened,
        profile(1.5),
        vec![stackable],
    )));
    assert_dbg_eq!(
        items_commands(&v.click(vendor::BUY_CLEAR_ITEM_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::RemoveFromBuyList {
            item: ObjectRef::Guid(ARMOR),
            amount: -1
        })]
    );
}

// DoubleClickStagedBuyingRow_RemovesOneUnitAndReportsRetailsNotice
#[test]
fn a_double_click_on_a_staged_listing_takes_one_off() {
    let mut v = vendor_window();
    let mut view = shop(VendorViewReason::Opened, profile(1.5), armor_and_food());
    view.lists.buying = vec![row(ARMOR, "Chainmail", 2)];
    v.event(Some(&view));
    let c = v.cell(vendor::BUYING_LIST_ID, 0);
    let out = v.fire(c, kind::DOUBLE_CLICK, None);
    assert_eq!(notices(&out), ["Removing Chainmail from shopping list"]);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::RemoveFromBuyList {
            item: ObjectRef::Guid(ARMOR),
            amount: 1
        })]
    );
}

// BuyingTabSummaryText_TracksStagedCountValueAndPlayerPurse / BuyingTabPurseText_UpdatesOnAPlayerMoneyChangeAloneWithNoStagingChange
#[test]
fn the_summary_lines_show_the_lists_and_the_live_purse() {
    let mut v = vendor_window();
    let mut view = shop(VendorViewReason::Opened, profile(1.5), armor_and_food());
    view.lists.buying_text = "Buying 1 item worth 750p".into();
    view.lists.selling_text = "Selling 0 items worth 0p".into();
    v.event(Some(&view));
    assert_eq!(
        v.text(vendor::BUYING_LIST_TEXT_ID),
        "Buying 1 item worth 750p"
    );
    assert_eq!(
        v.text(vendor::SELLING_LIST_TEXT_ID),
        "Selling 0 items worth 0p"
    );
    assert_eq!(v.text(vendor::BUYING_PURSE_TEXT_ID), "You have 1,500p");
    v.objects
        .get_mut(PLAYER)
        .properties
        .ints
        .insert(PropertyInt::COIN_VALUE.0, 2_000_000);
    v.update();
    assert_eq!(v.text(vendor::SELLING_PURSE_TEXT_ID), "You have 2,000,000p");
}

// ── Selling ─────────────────────────────────────────────────────────────

fn drag_onto_sell_list(v: &mut Vendor, guid: u32, k: i32) -> Vec<PanelOutput> {
    let c = v.cell(vendor::SELLING_LIST_ID, 0);
    v.fire(c, k, Some(DragPayload::new(payload(guid))))
}

// OnDragOver_AcceptableItemOverSellingList_Accepts / OnDragOver_UnacceptableItemOverSellingList_RejectsSilently
#[test]
fn a_drag_over_the_sell_list_is_answered_by_what_the_vendor_buys() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    v.owned(OWNED_WEAPON, ItemType::MELEE_WEAPON, 100, 1, 2);
    let out = drag_onto_sell_list(&mut v, OWNED_ARMOR, kind::DRAG_ENTER);
    assert_eq!(
        v.slot(vendor::SELLING_LIST_ID, 0).drag_acceptance(),
        DragAcceptance::Accept
    );
    assert!(notices(&out).is_empty());
    drag_onto_sell_list(&mut v, OWNED_WEAPON, kind::DRAG_ENTER);
    assert_eq!(
        v.slot(vendor::SELLING_LIST_ID, 0).drag_acceptance(),
        DragAcceptance::Reject
    );
}

// HandleDropRelease_AcceptableItem_StagesItSwitchesToSellingTabAndSelectsIt
#[test]
fn an_acceptable_drop_stages_it_on_the_selling_page() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    let out = drag_onto_sell_list(&mut v, OWNED_ARMOR, kind::DROP_RELEASED);
    assert_eq!(v.with(|c, _| c.tab()), VendorTab::Selling);
    assert_eq!(v.selected, Some(OWNED_ARMOR));
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::AddToSellList(
            ObjectRef::Guid(OWNED_ARMOR)
        ))]
    );
}

// HandleDropRelease_UnacceptableType_... / _NoValueItem_... / _NotOwnedByPlayer_...
#[test]
fn a_refused_drop_says_why_and_stages_nothing() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_WEAPON, ItemType::MELEE_WEAPON, 100, 1, 2);
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 0, 1, 1);
    let mut stranger = ClientObject::new(0x6000_0299);
    stranger.item_type = ItemType::ARMOR;
    stranger.value = 100;
    stranger.stack_size = 1;
    v.objects.add(stranger);
    for (guid, text) in [
        (OWNED_WEAPON, "You cannot sell that here"),
        (OWNED_ARMOR, "That item has no value and cannot be sold"),
        (0x6000_0299, "You can only sell items you are carrying"),
    ] {
        let out = drag_onto_sell_list(&mut v, guid, kind::DROP_RELEASED);
        assert_eq!(notices(&out), [text]);
        assert!(items_commands(&out).is_empty());
    }
}

// The main pack (the character itself) is the player's own: a drop of it
// with an item in an item slot is taken; one holding only pack-slot objects
// (a spell focus, container type 2) is judged as the character, which a
// vendor does not buy.
#[test]
fn the_main_pack_is_taken_when_an_item_slot_holds_something() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR | ItemType::MISC),
        Vec::new(),
    )));
    // "Foci of Enchantment" (wcid 15268): item type Misc, value 500.
    v.owned(0x6000_0301, ItemType::MISC, 500, 1, 15268);
    v.objects.t.replace_contents(
        PLAYER,
        &[ac_core::items::ContainerContentEntry {
            guid: 0x6000_0301,
            container_type: 2,
        }],
        &mut v.objects.ev,
    );
    let out = drag_onto_sell_list(&mut v, PLAYER, kind::DROP_RELEASED);
    assert_eq!(notices(&out), ["You cannot sell that here"]);
    assert!(items_commands(&out).is_empty());

    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    let out = drag_onto_sell_list(&mut v, PLAYER, kind::DROP_RELEASED);
    assert!(notices(&out).is_empty());
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::AddToSellList(
            ObjectRef::Guid(PLAYER)
        ))]
    );
}

// HandleDropRelease_PartialStack_SplitsThenStagesTheNewExactStack
#[test]
fn part_of_a_stack_is_split_off_and_the_new_stack_staged() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::MISSILE_WEAPON),
        Vec::new(),
    )));
    v.owned(OWNED_WEAPON, ItemType::MISSILE_WEAPON, 100, 20, 0x2345);
    v.selected = Some(OWNED_WEAPON);
    v.split = split(20, 5);
    let out = drag_onto_sell_list(&mut v, OWNED_WEAPON, kind::DROP_RELEASED);
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::MoveToContainer {
            item: ObjectRef::Guid(OWNED_WEAPON),
            container: ObjectRef::Guid(PLAYER),
            slot: 0,
            amount: 5,
            join_stack: false,
        }]
    );
    assert_eq!(
        notices(&out),
        ["Splitting the Item 60000202 before selling them"]
    );
    // The split's new stack arrives: it is staged.
    v.pending = vec![OWNED_WEAPON];
    assert!(v.update().is_empty());
    v.owned(0x6000_0222, ItemType::MISSILE_WEAPON, 25, 5, 0x2345);
    v.pending.clear();
    let out = v.update();
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::AddToSellList(
            ObjectRef::Guid(0x6000_0222)
        ))]
    );
}

// SellItemButton_SellsTheSelectedStagedItem... / _ActsOnTheGlobalSelection... / _PartialStackSelected_... / _FullStackSelected_SendsLiteralAmountOne...
#[test]
fn sell_item_sells_the_selection_as_one_and_refuses_part_of_a_stack() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 10, 1);
    v.selected = Some(OWNED_ARMOR);
    v.split = split(10, 10);
    let out = v.click(vendor::SELL_ITEM_BUTTON_ID);
    assert_dbg_eq!(
        items_commands(&out),
        [
            ItemCommand::Sell(vec![(ObjectRef::Guid(OWNED_ARMOR), 1)]),
            ItemCommand::Vendor(VendorCommand::RemoveFromSellList(ObjectRef::Guid(
                OWNED_ARMOR
            )))
        ]
    );
    v.split = split(10, 4);
    let out = v.click(vendor::SELL_ITEM_BUTTON_ID);
    assert!(items_commands(&out).is_empty());
    assert_eq!(notices(&out), ["Cannot sell part of a stack"]);
}

// SellAllButton_... / SellClearItemButton_... / SellClearListButton_... / SellAllButton_WithNothingStaged_IsANoOp
#[test]
fn sell_all_and_the_clear_buttons_act_on_the_sell_list() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    assert!(items_commands(&v.click(vendor::SELL_ALL_BUTTON_ID)).is_empty());
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    let mut view = shop(
        VendorViewReason::Lists,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    );
    view.lists.selling = vec![row(OWNED_ARMOR, "Item", 1)];
    v.event(Some(&view));
    v.selected = Some(OWNED_ARMOR);
    assert_dbg_eq!(
        items_commands(&v.click(vendor::SELL_ALL_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::SellAll)]
    );
    assert_dbg_eq!(
        items_commands(&v.click(vendor::SELL_CLEAR_ITEM_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::RemoveFromSellList(
            ObjectRef::Guid(OWNED_ARMOR)
        ))]
    );
    assert_dbg_eq!(
        items_commands(&v.click(vendor::SELL_CLEAR_LIST_BUTTON_ID)),
        [ItemCommand::Vendor(VendorCommand::ClearSellList)]
    );
}

// HandleDropRelease_StagingMarksTheObjectForSaleAndTheSellingRow / DoubleClickStagedSellingRow_RemovesTheEntryAndReportsRetailsNotice
#[test]
fn a_staged_sale_shows_marked_and_a_double_click_takes_it_off() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    let mut view = shop(
        VendorViewReason::Lists,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    );
    view.lists.selling = vec![row(OWNED_ARMOR, "Chainmail Shirt", 1)];
    v.event(Some(&view));
    let s = v.slot(vendor::SELLING_LIST_ID, 0);
    assert_eq!(s.object_id, OWNED_ARMOR);
    assert!(s.show_sell_overlay && s.allow_drag_source);
    let c = v.cell(vendor::SELLING_LIST_ID, 0);
    let out = v.fire(c, kind::DOUBLE_CLICK, None);
    assert_eq!(
        notices(&out),
        ["Removing Chainmail Shirt from shopping list"]
    );
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::RemoveFromSellList(
            ObjectRef::Guid(OWNED_ARMOR)
        ))]
    );
}

// DragStagedSellingRow_RemovesItAndPartialSelectionPrintsExactRefusalThenResets
#[test]
fn lifting_a_staged_sale_takes_it_off_and_refuses_a_partial_selection() {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 10, 1);
    let mut view = shop(
        VendorViewReason::Lists,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    );
    view.lists.selling = vec![row(OWNED_ARMOR, "Item", 10)];
    v.event(Some(&view));
    v.selected = Some(OWNED_ARMOR);
    v.split = split(10, 3);
    let c = v.cell(vendor::SELLING_LIST_ID, 0);
    let out = v.fire(
        c,
        kind::DRAG_BEGIN,
        Some(DragPayload::new(payload(OWNED_ARMOR))),
    );
    assert_dbg_eq!(
        items_commands(&out),
        [ItemCommand::Vendor(VendorCommand::RemoveFromSellList(
            ObjectRef::Guid(OWNED_ARMOR)
        ))]
    );
    assert_eq!(notices(&out), [vendor::CANNOT_SPLIT_HERE_MESSAGE]);
    // "...ThenResets": the split quantity goes back to the whole stack.
    assert!(
        out.iter()
            .any(|o| matches!(o, PanelOutput::ResetSplitQuantity { stack: 10 })),
        "{out:?}"
    );
}

/// A vendor window with `OWNED_ARMOR` staged on the sell list, on the
/// selling tab.
fn staged_sale() -> Vendor {
    let mut v = vendor_window();
    v.event(Some(&shop(
        VendorViewReason::Opened,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    )));
    v.owned(OWNED_ARMOR, ItemType::ARMOR, 100, 1, 1);
    let mut view = shop(
        VendorViewReason::Lists,
        sell_profile(ItemType::ARMOR),
        Vec::new(),
    );
    view.lists.selling = vec![row(OWNED_ARMOR, "Chainmail Shirt", 1)];
    v.event(Some(&view));
    v.click(vendor::SELLING_TAB_ID);
    v
}

/// A staged sale let go over the world says it came from the vendor
/// window, so the world only takes it off the sell list and never drops
/// it on the ground (issue #44).
#[test]
fn a_staged_sale_dragged_out_over_the_world_comes_from_the_vendor_window() {
    let mut v = staged_sale();
    assert_eq!(
        v.slot(vendor::SELLING_LIST_ID, 0).source_kind,
        ItemDragSource::Vendor
    );
    let c = v.cell(vendor::SELLING_LIST_ID, 0);
    let (x, y) = v.host.root.screen_position(c);
    let e = v.host.root.element(c).unwrap();
    let (x, y) = ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32);
    v.host.take_outputs();
    v.host.mouse_move(x, y);
    v.host.mouse_down(MouseButton::Left, x, y, 0);
    v.host.mouse_move(x + 200, y + 300);
    v.host.mouse_up(MouseButton::Left, x + 200, y + 300, 0);
    let released: Vec<ItemDragSource> = v
        .host
        .take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::DragReleasedOutsideUi { payload, .. } => {
                payload.get::<ItemDragPayload>().map(|p| p.source_kind)
            }
            _ => None,
        })
        .collect();
    assert_eq!(released, [ItemDragSource::Vendor]);
}

/// A drag out of the sell list is refused everywhere, the sell list
/// included: a staged sale lifted and let go over the list again is not
/// staged a second time.
#[test]
fn a_staged_sale_dropped_back_on_the_sell_list_is_refused() {
    let mut v = staged_sale();
    let c = v.cell(vendor::SELLING_LIST_ID, 0);
    let lifted = ItemDragPayload::new(OWNED_ARMOR, ItemDragSource::Vendor, 0);
    v.fire(c, kind::DRAG_ENTER, Some(DragPayload::new(lifted.clone())));
    assert_eq!(
        v.slot(vendor::SELLING_LIST_ID, 0).drag_acceptance(),
        DragAcceptance::Reject
    );
    let out = v.fire(c, kind::DROP_RELEASED, Some(DragPayload::new(lifted)));
    assert!(items_commands(&out).is_empty(), "{out:?}");
    assert!(notices(&out).is_empty(), "{out:?}");
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use std::path::PathBuf;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    // Bind_FromRealDatFixture_ResolvesAllRequiredControls on the installed layout.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vendor_window_imports_binds_and_shows_a_shop() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            vendor::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("vendor");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        for id in [
            vendor::CLOSE_ID,
            vendor::ITEM_SCROLLBAR_ID,
            vendor::BUY_BUTTON_ID,
            vendor::ADD_BUTTON_ID,
            vendor::BUYING_LIST_ID,
            vendor::SELLING_LIST_ID,
            vendor::BUY_ALL_BUTTON_ID,
            vendor::SELL_ALL_BUTTON_ID,
            vendor::BUYING_PURSE_TEXT_ID,
            vendor::SELLING_PURSE_TEXT_ID,
        ] {
            assert!(layout.find(id).is_some(), "element {id:#X}");
        }
        let ctl = VendorController::bind(
            &mut host.root,
            &layout,
            WINDOW,
            VendorSprites::default(),
            None,
        )
        .expect("binds");
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        let items = ItemsView::new(&o.t, PLAYER);
        let mut icons = TestIcons::default();
        let view = shop(VendorViewReason::Opened, profile(1.5), armor_and_food());
        host.with_controller::<VendorController, _>(WINDOW, |w, ui| {
            w.on_vendor_event(ui, Some(&view), &items, &mut icons);
        });
        let out = panel_outputs(&mut host);
        assert!(
            out.iter()
                .any(|p| matches!(p, PanelOutput::SelectObject(ARMOR)))
        );
        let items = ItemsView {
            selected: Some(ARMOR),
            ..ItemsView::new(&o.t, PLAYER)
        };
        host.with_controller::<VendorController, _>(WINDOW, |w, ui| {
            w.update(ui, &items, &mut icons)
        });
        assert!(host.root.is_window_visible(WINDOW));
        let name = layout.find(vendor::ITEM_NAME_TEXT_ID).unwrap();
        assert_eq!(text_of(&host.root, name), "Chainmail");
        let list = layout.find(vendor::ITEM_LIST_ID).unwrap();
        assert_eq!(
            host.root
                .widget::<ItemList>(list)
                .unwrap()
                .item(0)
                .and_then(|c| host.root.widget::<ItemSlot>(c).map(|s| s.object_id)),
            Some(ARMOR)
        );
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
        let _ = ElementInfo::default();
        let _ = Text::default();
    }
}
