//! The slumlord window.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/HousePurchaseUiControllerTests.cs`,
//! `HousePurchaseTextTests.cs` and `HousePurchaseLayoutInstalledDatTests.cs`,
//! on a synthetic window shaped like the retail one (layout 0x2100000E; the
//! real one is imported in `real_dats`). The staging rules are the
//! session's (`ac_runtime::inventory::house_purchase`, with their own
//! tests); here the window is fed views and its outputs are read:
//! OpenAC's `Stage`, `Unstage`, `Close`, `BuyHouse` and `RentHouse`
//! delegates are `ItemCommand::HousePurchase` outputs, and its `Confirm`
//! delegate is `PanelOutput::ConfirmEither`.

mod common;

use std::collections::HashMap;

use ac_proto::house::{HousePayment, HouseProfile};
use ac_runtime::inventory::house_purchase::{BUY_CONFIRMATION, OTHERS_MAINTENANCE_CONFIRMATION};
use ac_runtime::inventory::{
    HousePurchaseCommand, HousePurchaseTab, HousePurchaseView, ItemCommand,
};
use ac_runtime::session::SessionCommand;
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::house_purchase::{
    self as hp, HousePurchaseController, HousePurchaseSprites, owner_line, requirement_text,
};
use ac_ui::panels::items::ItemsView;
use ac_ui::widgets::{ItemList, ItemSlot};
use ac_ui::{ElementId, MouseButton, UiOutput};
use common::items::*;
use common::panels::*;
use common::*;

const SLUMLORD: u32 = 0x7000_0100;
const ITEM: u32 = 0x6000_0001;
const WINDOW: &str = "house-purchase";
const BUY: HousePurchaseTab = HousePurchaseTab::Buy;
const RENT: HousePurchaseTab = HousePurchaseTab::Rent;

struct Window {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    objects: Objects,
    icons: TestIcons,
}

fn window() -> Window {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 320.0, 240.0));
    let mut ids = HashMap::new();
    for id in [hp::BUY_PAGE_ID, hp::MAINTENANCE_PAGE_ID] {
        let mut e = at(0.0, 30.0, 320.0, 200.0);
        e.dat_element_id = id;
        ids.insert(id, panel(ui, root, e));
    }
    let buy_page = ids[&hp::BUY_PAGE_ID];
    let rent_page = ids[&hp::MAINTENANCE_PAGE_ID];
    ids.insert(
        hp::BUY_ITEMS_LIST_ID,
        list(ui, buy_page, 0.0, 120.0, 320.0, 32.0),
    );
    ids.insert(
        hp::MAINTENANCE_ITEMS_LIST_ID,
        list(ui, rent_page, 0.0, 120.0, 320.0, 32.0),
    );
    for id in [hp::BUY_TAB_ID, hp::MAINTENANCE_TAB_ID, hp::CLOSE_BUTTON_ID] {
        add_std_button(ui, &mut ids, root, id);
    }
    add_std_button(ui, &mut ids, buy_page, hp::BUY_BUTTON_ID);
    add_std_button(ui, &mut ids, rent_page, hp::MAINTENANCE_BUTTON_ID);
    for id in [hp::BUY_REQUIREMENTS_TEXT_ID, hp::BUY_OWNER_ID] {
        add_std_text(ui, &mut ids, buy_page, id);
    }
    for id in [
        hp::MAINTENANCE_REQUIREMENTS_TEXT_ID,
        hp::MAINTENANCE_OWNER_ID,
    ] {
        add_std_text(ui, &mut ids, rent_page, id);
    }
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window(WINDOW, root, None, 0).unwrap();
    let c =
        HousePurchaseController::bind(ui, &layout, WINDOW, HousePurchaseSprites::default(), None)
            .expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    let mut item = plain_item(ITEM);
    item.weenie_class_id = 273;
    objects.add(item);
    objects.mv(ITEM, PLAYER, 0);
    Window {
        host,
        ids,
        objects,
        icons: TestIcons::default(),
    }
}

fn pay(num: i32, paid: i32, wcid: u32, name: &str, plural: &str) -> HousePayment {
    HousePayment {
        num,
        paid,
        weenie_id: wcid,
        name: name.into(),
        plural_name: plural.into(),
    }
}

fn profile(owner: u32, owner_name: &str, house_type: u32) -> HouseProfile {
    HouseProfile {
        slumlord_guid: SLUMLORD,
        dwelling_id: 1,
        owner_guid: owner,
        bitmask: 1,
        min_level: -1,
        max_level: -1,
        min_allegiance_rank: -1,
        max_allegiance_rank: -1,
        maintenance_free: false,
        house_type,
        owner_name: owner_name.into(),
        buy: vec![pay(50_000, 0, 273, "Pyreal", "Pyreals")],
        rent: vec![pay(5_000, 0, 273, "Pyreal", "Pyreals")],
    }
}

fn view(revision: u64, profile: HouseProfile) -> HousePurchaseView {
    HousePurchaseView {
        revision,
        buy_amounts: vec![0; profile.buy.len()],
        rent_amounts: vec![0; profile.rent.len()],
        profile: Some(profile),
        ..HousePurchaseView::default()
    }
}

/// `v` with `ITEM` staged on `tab`, paying `amount`.
fn staged(mut v: HousePurchaseView, tab: HousePurchaseTab, amount: i32) -> HousePurchaseView {
    match tab {
        BUY => {
            v.buy = vec![ITEM];
            v.buy_amounts[0] = amount;
        }
        RENT => {
            v.rent = vec![ITEM];
            v.rent_amounts[0] = amount;
        }
    }
    v
}

fn hp_command(c: HousePurchaseCommand) -> SessionCommand {
    SessionCommand::Item(ItemCommand::HousePurchase(c))
}

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

impl Window {
    fn update(&mut self, v: &HousePurchaseView) {
        let items = ItemsView::new(&self.objects.t, PLAYER);
        let icons = &mut self.icons;
        self.host
            .with_controller::<HousePurchaseController, _>(WINDOW, |c, ui| {
                c.update(ui, v, &items, icons);
            })
            .unwrap();
    }

    fn visible(&self) -> bool {
        self.host.root.is_window_visible(WINDOW)
    }

    fn shown(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }

    fn enabled(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().enabled
    }

    fn text(&self, id: u32) -> String {
        text_of(&self.host.root, self.ids[&id])
    }

    fn click(&mut self, id: u32) -> Vec<PanelOutput> {
        panel_outputs(&mut self.host);
        self.host.root.fire_event(kind::CLICK, self.ids[&id], None);
        self.host.pump();
        panel_outputs(&mut self.host)
    }

    fn cell(&self, list: u32, i: usize) -> ElementId {
        self.host
            .root
            .widget::<ItemList>(self.ids[&list])
            .unwrap()
            .item(i)
            .unwrap()
    }

    fn staged_count(&self, list: u32) -> usize {
        let l = self.host.root.widget::<ItemList>(self.ids[&list]).unwrap();
        (0..l.num_items())
            .filter(|&i| {
                self.host
                    .root
                    .widget::<ItemSlot>(l.item(i).unwrap())
                    .is_some_and(|s| s.object_id != 0)
            })
            .count()
    }

    fn drop_on(&mut self, list: u32, p: ItemDragPayload) -> Vec<SessionCommand> {
        let cell = self.cell(list, 0);
        panel_outputs(&mut self.host);
        self.host
            .root
            .fire_event(kind::DROP_RELEASED, cell, Some(DragPayload::new(p)));
        self.host.pump();
        sessions(&panel_outputs(&mut self.host))
    }
}

// HousePurchaseUiControllerTests.Hidden_UntilOpened_ThenShownOnBuyForAnUnownedHouse
#[test]
fn hidden_until_opened_then_shown_on_buy_for_an_unowned_house() {
    let mut w = window();
    assert!(!w.visible());
    w.update(&view(1, profile(0, "", 1)));
    assert!(w.visible());
    assert!(w.shown(hp::BUY_PAGE_ID));
    assert!(!w.shown(hp::MAINTENANCE_PAGE_ID));
    assert_eq!(w.text(hp::BUY_OWNER_ID), "Owner: None");
    assert_eq!(w.text(hp::BUY_REQUIREMENTS_TEXT_ID), "50000 Pyreals");
    w.update(&HousePurchaseView {
        revision: 2,
        ..HousePurchaseView::default()
    });
    assert!(!w.visible());
}

// HousePurchaseTextTests.OwnerLine_NamesTheOwnerOrNone; review of #244,
// defect 5: "None" follows an empty owner name, whatever the owner id (the
// server sends an empty name for an owner it cannot find).
#[test]
fn the_owner_line_names_the_owner_or_none_by_the_name() {
    assert_eq!(owner_line(&profile(0, "", 1)), "Owner: None");
    assert_eq!(owner_line(&profile(5, "Sammy", 1)), "Owner: Sammy");
    assert_eq!(owner_line(&profile(5, "", 1)), "Owner: None");
}

// HousePurchaseTextTests.RequirementText_Buy_OneLineOfPlainNumbers and
// RequirementText_Maintenance_PaidOverOwed_EvenWhenFree
#[test]
fn the_requirements_are_one_line_of_plain_numbers() {
    let mut p = profile(0, "", 3);
    p.min_level = 20;
    p.buy.push(pay(1, 0, 9000, "Bone Totem", ""));
    p.rent = vec![pay(5_000, 1_250, 273, "Pyreal", "Pyreals")];
    assert_eq!(requirement_text(&p, BUY), "50000 Pyreals, 1 Bone Totem");
    assert_eq!(requirement_text(&p, RENT), "1250/5000 Pyreals");
    p.maintenance_free = true;
    assert_eq!(requirement_text(&p, RENT), "1250/5000 Pyreals");
}

// HousePurchaseUiControllerTests.AProfileArriving_NeverSwitchesTheTab_OnlyAClickDoes
#[test]
fn a_profile_arriving_never_switches_the_tab_only_a_click_does() {
    let mut w = window();
    w.update(&view(1, profile(PLAYER, "Me", 1)));
    assert!(w.shown(hp::BUY_PAGE_ID));
    assert!(!w.shown(hp::MAINTENANCE_PAGE_ID));
    w.click(hp::MAINTENANCE_TAB_ID);
    assert!(w.shown(hp::MAINTENANCE_PAGE_ID));
    assert!(!w.shown(hp::BUY_PAGE_ID));
    w.update(&view(2, profile(0, "", 1)));
    assert!(w.shown(hp::MAINTENANCE_PAGE_ID));
}

// HousePurchaseUiControllerTests.Drop_StagesAndEnablesTheButton_WhichSendsTheStagedGuidsAndEmptiesTheStrip,
// with review of #244, defect 3: the question's no closes the window.
#[test]
fn a_drop_stages_and_the_button_asks_before_buying_and_a_no_closes() {
    let mut w = window();
    let open = view(1, profile(0, "", 1));
    w.update(&open);
    assert!(!w.enabled(hp::BUY_BUTTON_ID));
    assert_dbg_eq!(
        w.drop_on(hp::BUY_ITEMS_LIST_ID, payload(ITEM)),
        [hp_command(HousePurchaseCommand::Stage {
            tab: BUY,
            item: ITEM
        })]
    );
    w.update(&staged(open.clone(), BUY, 50_000));
    let mut v = staged(open, BUY, 50_000);
    v.revision = 2;
    w.update(&v);
    assert_eq!(w.staged_count(hp::BUY_ITEMS_LIST_ID), 1);
    assert!(w.enabled(hp::BUY_BUTTON_ID));
    let asked = w.click(hp::BUY_BUTTON_ID);
    assert_dbg_eq!(
        asked,
        [PanelOutput::ConfirmEither {
            message: BUY_CONFIRMATION.to_owned(),
            on_yes: Box::new(PanelOutput::Session(hp_command(
                HousePurchaseCommand::Submit {
                    tab: BUY,
                    slumlord: SLUMLORD
                }
            ))),
            on_no: Box::new(PanelOutput::Session(hp_command(
                HousePurchaseCommand::Close
            ))),
        }]
    );
}

// HousePurchaseUiControllerTests.BuyingAnApartment_AsksNothing
#[test]
fn buying_an_apartment_asks_nothing() {
    let mut w = window();
    w.update(&staged(view(1, profile(0, "", 4)), BUY, 50_000));
    assert_dbg_eq!(
        sessions(&w.click(hp::BUY_BUTTON_ID)),
        [hp_command(HousePurchaseCommand::Submit {
            tab: BUY,
            slumlord: SLUMLORD
        })]
    );
}

// HousePurchaseUiControllerTests.Maintenance_AsksOnlyOnSomeoneElsesHouse
#[test]
fn maintenance_asks_only_on_someone_elses_house() {
    let mut w = window();
    w.update(&staged(view(1, profile(PLAYER, "Me", 1)), RENT, 100));
    assert!(
        w.enabled(hp::MAINTENANCE_BUTTON_ID),
        "maintenance may be part-paid"
    );
    assert_dbg_eq!(
        w.click(hp::MAINTENANCE_BUTTON_ID),
        [PanelOutput::Session(hp_command(
            HousePurchaseCommand::Submit {
                tab: RENT,
                slumlord: SLUMLORD
            }
        ))]
    );
    w.update(&staged(
        view(2, profile(0x5000_0042, "Sammy", 1)),
        RENT,
        100,
    ));
    let asked = w.click(hp::MAINTENANCE_BUTTON_ID);
    assert!(
        matches!(&asked[..], [PanelOutput::ConfirmEither { message, .. }]
            if message == OTHERS_MAINTENANCE_CONFIRMATION),
        "{asked:?}"
    );
}

// HousePurchaseUiControllerTests.Buy_StaysDisabledUntilEveryLineIsCovered
#[test]
fn buy_stays_disabled_until_every_line_is_covered() {
    let mut w = window();
    w.update(&staged(view(1, profile(0, "", 1)), BUY, 20_000));
    assert!(!w.enabled(hp::BUY_BUTTON_ID));
    assert!(sessions(&w.click(hp::BUY_BUTTON_ID)).is_empty());
    w.update(&staged(view(2, profile(0, "", 1)), BUY, 50_000));
    assert!(w.enabled(hp::BUY_BUTTON_ID));
}

// HousePurchaseUiControllerTests.Drop_Refused_OnlyAnItemNotCarriedIsReported,
// with review of #244, defect 4: a drop from the ground or an outside
// container reaches the staging (which says the player does not carry it);
// a shortcut does not.
#[test]
fn a_ground_drop_reaches_the_staging_and_a_shortcut_does_not() {
    let mut w = window();
    w.update(&view(1, profile(0, "", 1)));
    let ground = ItemDragPayload::new(0x7000_0999, ItemDragSource::Ground, 0);
    assert_dbg_eq!(
        w.drop_on(hp::BUY_ITEMS_LIST_ID, ground),
        [hp_command(HousePurchaseCommand::Stage {
            tab: BUY,
            item: 0x7000_0999
        })]
    );
    let shortcut = ItemDragPayload::new(ITEM, ItemDragSource::ShortcutBar, 0);
    assert!(w.drop_on(hp::BUY_ITEMS_LIST_ID, shortcut).is_empty());
}

// HousePurchaseUiControllerTests.TheText_IsRebuiltOnlyWhenANewProfileArrives
#[test]
fn the_text_is_rebuilt_only_when_a_new_profile_arrives() {
    let mut w = window();
    let open = view(1, profile(PLAYER, "Me", 1));
    w.update(&open);
    assert_eq!(
        w.text(hp::MAINTENANCE_REQUIREMENTS_TEXT_ID),
        "0/5000 Pyreals"
    );
    let mut more = staged(open, RENT, 10);
    more.revision = 2;
    w.update(&more);
    assert_eq!(
        w.text(hp::MAINTENANCE_REQUIREMENTS_TEXT_ID),
        "0/5000 Pyreals"
    );
    let mut paid = profile(PLAYER, "Me", 1);
    paid.rent[0].paid = 10;
    w.update(&view(3, paid));
    assert_eq!(
        w.text(hp::MAINTENANCE_REQUIREMENTS_TEXT_ID),
        "10/5000 Pyreals"
    );
}

// HousePurchaseUiControllerTests.DragOffAndRightClick_Unstage
#[test]
fn dragging_off_and_a_right_click_unstage() {
    let mut w = window();
    w.update(&staged(view(1, profile(PLAYER, "Me", 1)), RENT, 10));
    let cell = w.cell(hp::MAINTENANCE_ITEMS_LIST_ID, 0);
    panel_outputs(&mut w.host);
    w.host.root.fire_event(kind::RIGHT_CLICK, cell, None);
    w.host.pump();
    let unstage = hp_command(HousePurchaseCommand::Unstage {
        tab: RENT,
        item: ITEM,
    });
    assert_dbg_eq!(
        sessions(&panel_outputs(&mut w.host)),
        std::slice::from_ref(&unstage)
    );
    w.host.root.fire_event(
        kind::DRAG_BEGIN,
        cell,
        Some(DragPayload::new(payload(ITEM))),
    );
    w.host.pump();
    let lifted = format!("{:?}", sessions(&panel_outputs(&mut w.host)));
    assert!(lifted.contains(&format!("{unstage:?}")), "{lifted}");
}

/// A staged payment let go over the world says it came from the house
/// purchase window, so the world only unstages it and never drops it on
/// the ground (the same bug as issue #44's vendor sell list).
#[test]
fn a_staged_item_dragged_out_over_the_world_comes_from_the_house_window() {
    let mut w = window();
    w.update(&staged(view(1, profile(PLAYER, "Me", 1)), RENT, 10));
    w.click(hp::MAINTENANCE_TAB_ID);
    let cell = w.cell(hp::MAINTENANCE_ITEMS_LIST_ID, 0);
    assert_eq!(
        w.host.root.widget::<ItemSlot>(cell).unwrap().source_kind,
        ItemDragSource::Vendor
    );
    let (x, y) = w.host.root.screen_position(cell);
    let e = w.host.root.element(cell).unwrap();
    let (x, y) = ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32);
    w.host.take_outputs();
    w.host.mouse_move(x, y);
    w.host.mouse_down(MouseButton::Left, x, y, 0);
    w.host.mouse_move(x + 200, y + 300);
    w.host.mouse_up(MouseButton::Left, x + 200, y + 300, 0);
    let released: Vec<ItemDragSource> = w
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

/// A drag out of the house window is refused by the window itself: let
/// go over a row again, it stages nothing.
#[test]
fn a_staged_item_dropped_back_on_the_row_stages_nothing() {
    let mut w = window();
    w.update(&staged(view(1, profile(PLAYER, "Me", 1)), RENT, 10));
    let lifted = ItemDragPayload::new(ITEM, ItemDragSource::Vendor, 0);
    let cell = w.cell(hp::MAINTENANCE_ITEMS_LIST_ID, 0);
    w.host.root.fire_event(
        kind::DRAG_ENTER,
        cell,
        Some(DragPayload::new(lifted.clone())),
    );
    w.host.pump();
    assert_eq!(
        w.host
            .root
            .widget::<ItemSlot>(cell)
            .unwrap()
            .drag_acceptance(),
        DragAcceptance::Reject
    );
    assert!(w.drop_on(hp::MAINTENANCE_ITEMS_LIST_ID, lifted).is_empty());
}

// HousePurchaseUiControllerTests.CloseButton_ClosesAndHides
#[test]
fn the_close_button_closes() {
    let mut w = window();
    w.update(&view(1, profile(0, "", 1)));
    assert_dbg_eq!(
        sessions(&w.click(hp::CLOSE_BUTTON_ID)),
        [hp_command(HousePurchaseCommand::Close)]
    );
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_rooted};
    use ac_ui::widgets::{Button, Text};
    use std::path::PathBuf;
    use std::sync::Arc;

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

    fn caption(host: &UiHost, id: ElementId) -> Option<String> {
        let ui = &host.root;
        if let Some(b) = ui.widget::<Button>(id) {
            return b.label.clone();
        }
        ui.widget::<Text>(id).map(|_| text_of(ui, id))
    }

    // HousePurchaseLayoutInstalledDatTests.Layout2100000E_BindsAndCarriesTheRetailCaptions
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_slumlord_window_imports_binds_and_carries_the_retail_captions() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            hp::LAYOUT_ID,
            hp::ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("slumlord layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        for id in [hp::BUY_ITEMS_LIST_ID, hp::MAINTENANCE_ITEMS_LIST_ID] {
            let e = layout.find(id).expect("list");
            assert!(host.root.widget::<ItemList>(e).is_some(), "{id:#X}");
        }
        for id in [
            hp::BUY_REQUIREMENTS_TEXT_ID,
            hp::MAINTENANCE_REQUIREMENTS_TEXT_ID,
            hp::BUY_OWNER_ID,
            hp::MAINTENANCE_OWNER_ID,
        ] {
            let e = layout.find(id).expect("text");
            assert!(host.root.widget::<Text>(e).is_some(), "{id:#X}");
        }
        assert!(layout.find(hp::CLOSE_BUTTON_ID).is_some());
        for (id, expected) in [
            (hp::BUY_TAB_ID, "Buy"),
            (hp::MAINTENANCE_TAB_ID, "Maintenance"),
            (hp::BUY_BUTTON_ID, "Buy"),
            (hp::MAINTENANCE_BUTTON_ID, "Maintenance"),
        ] {
            let e = layout.find(id).expect("captioned element");
            println!("{id:#X}: {:?}", caption(&host, e));
            assert_eq!(caption(&host, e).as_deref(), Some(expected), "{id:#X}");
        }
        let ctl = HousePurchaseController::bind(
            &mut host.root,
            &layout,
            WINDOW,
            HousePurchaseSprites::default(),
            None,
        )
        .expect("binds");
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
    }
}
