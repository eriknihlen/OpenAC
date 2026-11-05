//! The secure-trade window.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SecureTradeUiControllerTooltipTests.cs`
//! and the behaviour of `src/AcDream.App/UI/Layout/SecureTradeUiController.cs`
//! (its button, drop and visibility rules), on a synthetic window shaped like
//! the retail one (layout 0x2100000D; the real one is imported in
//! `real_dats`). OpenAC's `OpenTrade`, `AddToTrade`, `AcceptTrade`,
//! `DeclineTrade`, `ResetTrade` and `CloseTrade` delegates are
//! `SessionCommand::Item` outputs here, and `SetWindowVisible` is the
//! window registered under the name the controller is bound with.

mod common;

use std::collections::HashMap;

use ac_runtime::inventory::{ItemCommand, ObjectRef, TradeView};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::items::{ItemsView, NameResolver};
use ac_ui::panels::trade::{self, SecureTradeController, TradeSprites};
use ac_ui::widgets::{Button, DatElement, ItemList, ItemSlot};
use common::items::*;
use common::panels::*;
use common::*;

const PARTNER: u32 = 0x5000_0002;
const MATERIAL_ITEM: u32 = 0x6000_0001;
const PLAIN_ITEM: u32 = 0x6000_0002;
const WINDOW: &str = "trade";

struct Trade {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    objects: Objects,
    icons: TestIcons,
}

fn trade_window() -> Trade {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 320.0, 200.0));
    let mut ids = HashMap::new();
    ids.insert(trade::SELF_LIST_ID, list(ui, root, 0.0, 120.0, 320.0, 32.0));
    ids.insert(
        trade::PARTNER_LIST_ID,
        list(ui, root, 0.0, 40.0, 320.0, 32.0),
    );
    for id in [
        trade::TRADE_BUTTON_ID,
        trade::CLEAR_ALL_BUTTON_ID,
        trade::CLOSE_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, root, id);
    }
    for id in [
        trade::PARTNER_NAME_ID,
        trade::PARTNER_COUNT_ID,
        trade::SELF_COUNT_ID,
    ] {
        add_std_text(ui, &mut ids, root, id);
    }
    add_std_dat(ui, &mut ids, root, trade::PARTNER_STATUS_ID, 16.0, 16.0);
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window(WINDOW, root, None, 0).unwrap();
    let c = SecureTradeController::bind(ui, &layout, WINDOW, TradeSprites::default(), None)
        .expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    Trade {
        host,
        ids,
        objects,
        icons: TestIcons::default(),
    }
}

fn open_view(self_items: &[u32]) -> TradeView {
    TradeView {
        open: true,
        partner_guid: PARTNER,
        self_items: self_items.to_vec(),
        revision: 1,
        ..TradeView::default()
    }
}

impl Trade {
    fn update(&mut self, view: &TradeView) {
        let names = |o: &ac_core::items::ClientObject| composed_name(o);
        let resolver: NameResolver<'_> = &names;
        let items = ItemsView {
            names: Some(resolver),
            ..ItemsView::new(&self.objects.t, PLAYER)
        };
        let icons = &mut self.icons;
        self.host
            .with_controller::<SecureTradeController, _>(WINDOW, |c, ui| {
                c.update(ui, view, &items, icons, None);
            })
            .unwrap();
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

    fn click(&mut self, id: u32) -> Vec<SessionCommand> {
        panel_outputs(&mut self.host);
        self.host.root.fire_event(kind::CLICK, self.ids[&id], None);
        self.host.pump();
        sessions(&panel_outputs(&mut self.host))
    }

    fn visible(&self) -> bool {
        self.host.root.is_window_visible(WINDOW)
    }
}

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

// SecureTradeUiControllerTooltipTests.HoverCaption_carriesTheMaterialPrefix_andStaysPlainWithoutOne
#[test]
fn the_hover_caption_carries_the_material_prefix_and_stays_plain_without_one() {
    let mut t = trade_window();
    t.objects.add(material_item(MATERIAL_ITEM));
    t.objects.add(plain_item(PLAIN_ITEM));
    t.update(&open_view(&[MATERIAL_ITEM, PLAIN_ITEM]));
    assert_eq!(
        t.slot(trade::SELF_LIST_ID, 0).tooltip.as_deref(),
        Some("Pyreal Scarab")
    );
    assert_eq!(
        t.slot(trade::SELF_LIST_ID, 1).tooltip.as_deref(),
        Some("Bread Loaf")
    );
}

#[test]
fn the_window_starts_hidden_and_follows_the_trade_opening_and_closing() {
    let mut t = trade_window();
    assert!(!t.visible());
    t.update(&open_view(&[]));
    assert!(t.visible());
    t.update(&TradeView {
        revision: 2,
        ..TradeView::default()
    });
    assert!(!t.visible());
}

#[test]
fn both_rows_show_their_items_and_only_the_players_carry_the_trade_marker() {
    let mut t = trade_window();
    t.objects.add(plain_item(PLAIN_ITEM));
    t.objects.add(material_item(MATERIAL_ITEM));
    let mut v = open_view(&[PLAIN_ITEM]);
    v.partner_items = vec![MATERIAL_ITEM];
    v.partner_accepted = true;
    t.update(&v);
    let own = t.slot(trade::SELF_LIST_ID, 0);
    assert_eq!(own.object_id, PLAIN_ITEM);
    assert!(own.show_trade_overlay);
    assert_eq!(own.trade_overlay_sprite, trade::TRADE_OVERLAY_SPRITE);
    assert!(!own.allow_drag_source);
    let theirs = t.slot(trade::PARTNER_LIST_ID, 0);
    assert_eq!(theirs.object_id, MATERIAL_ITEM);
    assert!(!theirs.show_trade_overlay);
    let ui = &t.host.root;
    assert_eq!(text_of(ui, t.ids[&trade::SELF_COUNT_ID]), "1");
    assert_eq!(text_of(ui, t.ids[&trade::PARTNER_COUNT_ID]), "1");
    let status = ui
        .widget::<DatElement>(t.ids[&trade::PARTNER_STATUS_ID])
        .unwrap();
    assert_eq!(status.active_state, trade::ACCEPTED_STATE);
}

#[test]
fn the_rows_are_single_sideways_rows_of_32_pixel_cells_filled_with_empty_cells() {
    let t = trade_window();
    for id in [trade::SELF_LIST_ID, trade::PARTNER_LIST_ID] {
        let l = t.host.root.widget::<ItemList>(t.ids[&id]).unwrap();
        assert!(l.single_row && l.horizontal_scroll && l.fill_visible_empty_slots);
        assert_eq!((l.cell_width, l.cell_height), (32.0, 32.0));
        assert!(l.empty_slot_factory.is_some());
    }
}

#[test]
fn the_trade_button_accepts_then_declines_and_shows_the_players_acceptance() {
    let mut t = trade_window();
    t.update(&open_view(&[]));
    assert_dbg_eq!(
        t.click(trade::TRADE_BUTTON_ID),
        [SessionCommand::Item(ItemCommand::AcceptTrade)]
    );
    let mut v = open_view(&[]);
    v.self_accepted = true;
    v.revision = 2;
    t.update(&v);
    let b = t.ids[&trade::TRADE_BUTTON_ID];
    assert!(t.host.root.widget::<Button>(b).unwrap().selected());
    assert_dbg_eq!(
        t.click(trade::TRADE_BUTTON_ID),
        [SessionCommand::Item(ItemCommand::DeclineTrade)]
    );
}

#[test]
fn clear_all_and_close_act_only_on_an_open_trade() {
    let mut t = trade_window();
    assert!(t.click(trade::CLEAR_ALL_BUTTON_ID).is_empty());
    assert!(t.click(trade::CLOSE_BUTTON_ID).is_empty());
    assert!(t.click(trade::TRADE_BUTTON_ID).is_empty());
    t.update(&open_view(&[]));
    assert_dbg_eq!(
        t.click(trade::CLEAR_ALL_BUTTON_ID),
        [SessionCommand::Item(ItemCommand::ResetTrade)]
    );
    assert_dbg_eq!(
        t.click(trade::CLOSE_BUTTON_ID),
        [SessionCommand::Item(ItemCommand::CloseTrade)]
    );
}

#[test]
fn a_pack_item_dropped_on_the_players_row_is_offered_and_others_are_refused() {
    let mut t = trade_window();
    t.update(&open_view(&[]));
    let cell = t.cell(trade::SELF_LIST_ID, 0);
    let from_pack = payload(PLAIN_ITEM);
    t.host.root.fire_event(
        kind::DRAG_ENTER,
        cell,
        Some(DragPayload::new(from_pack.clone())),
    );
    t.host.pump();
    assert_eq!(
        t.slot(trade::SELF_LIST_ID, 0).drag_acceptance(),
        DragAcceptance::Accept
    );
    let worn = ItemDragPayload::new(PLAIN_ITEM, ItemDragSource::Equipment, 0);
    t.host
        .root
        .fire_event(kind::DRAG_ENTER, cell, Some(DragPayload::new(worn.clone())));
    t.host.pump();
    assert_eq!(
        t.slot(trade::SELF_LIST_ID, 0).drag_acceptance(),
        DragAcceptance::Reject
    );
    panel_outputs(&mut t.host);
    t.host
        .root
        .fire_event(kind::DROP_RELEASED, cell, Some(DragPayload::new(worn)));
    t.host.pump();
    assert!(sessions(&panel_outputs(&mut t.host)).is_empty());
    t.host
        .root
        .fire_event(kind::DROP_RELEASED, cell, Some(DragPayload::new(from_pack)));
    t.host.pump();
    assert_dbg_eq!(
        sessions(&panel_outputs(&mut t.host)),
        [SessionCommand::Item(ItemCommand::AddToTrade(
            ObjectRef::Guid(PLAIN_ITEM)
        ))]
    );
}

#[test]
fn a_trade_request_offers_its_item_once_the_window_opens_with_that_partner() {
    let mut t = trade_window();
    t.host
        .with_controller::<SecureTradeController, _>(WINDOW, |c, ui| {
            c.request_secure_trade(ui, PARTNER, PLAIN_ITEM);
        })
        .unwrap();
    assert_dbg_eq!(
        sessions(&panel_outputs(&mut t.host)),
        [SessionCommand::Item(ItemCommand::OpenTrade(
            ObjectRef::Guid(PARTNER)
        ))]
    );
    t.update(&open_view(&[]));
    assert_dbg_eq!(
        sessions(&panel_outputs(&mut t.host)),
        [SessionCommand::Item(ItemCommand::AddToTrade(
            ObjectRef::Guid(PLAIN_ITEM)
        ))]
    );
    // With the trade open, a second request offers at once.
    t.host
        .with_controller::<SecureTradeController, _>(WINDOW, |c, ui| {
            c.request_secure_trade(ui, PARTNER, MATERIAL_ITEM);
        })
        .unwrap();
    assert_dbg_eq!(
        sessions(&panel_outputs(&mut t.host)),
        [SessionCommand::Item(ItemCommand::AddToTrade(
            ObjectRef::Guid(MATERIAL_ITEM)
        ))]
    );
}

#[test]
fn a_trade_that_opens_with_another_partner_drops_the_queued_item() {
    let mut t = trade_window();
    t.host
        .with_controller::<SecureTradeController, _>(WINDOW, |c, ui| {
            c.request_secure_trade(ui, PARTNER, PLAIN_ITEM);
        })
        .unwrap();
    panel_outputs(&mut t.host);
    let mut v = open_view(&[]);
    v.partner_guid = PARTNER + 1;
    t.update(&v);
    assert!(sessions(&panel_outputs(&mut t.host)).is_empty());
}

// SecureTradeUiControllerExamineTests.RightClickingOwnTradeItem_ExaminesIt
// and RightClickingPartnerTradeItem_ExaminesIt.
#[test]
fn a_right_click_on_either_row_selects_and_examines_the_item() {
    let mut t = trade_window();
    t.objects.add(plain_item(PLAIN_ITEM));
    t.objects.add(material_item(MATERIAL_ITEM));
    let mut v = open_view(&[PLAIN_ITEM]);
    v.partner_items = vec![MATERIAL_ITEM];
    t.update(&v);
    for (list, item) in [
        (trade::SELF_LIST_ID, PLAIN_ITEM),
        (trade::PARTNER_LIST_ID, MATERIAL_ITEM),
    ] {
        panel_outputs(&mut t.host);
        let cell = t.cell(list, 0);
        t.host.root.fire_event(kind::RIGHT_CLICK, cell, None);
        t.host.pump();
        let out = panel_outputs(&mut t.host);
        assert!(
            out.iter()
                .any(|o| matches!(o, PanelOutput::SelectObject(id) if *id == item)),
            "{list:#X}: {out:?}"
        );
        assert_dbg_eq!(
            sessions(&out),
            [SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(item)
            ))]
        );
    }
}

#[test]
fn the_count_caption_goes_through_the_authored_entry() {
    let mut s = FakeStrings::default();
    s.templates.insert(
        (
            trade::STRING_TABLE_ID,
            ac_ui::strings::compute_hash("ID_SecureTrade_TotalItemsLabel"),
        ),
        Box::new(|v| format!("Items: {}", v[&ac_ui::strings::compute_hash("ITEMS")])),
    );
    assert_eq!(trade::total_items_text(3, Some(&s)), "Items: 3");
    assert_eq!(trade::total_items_text(3, None), "3");
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
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

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_trade_window_imports_binds_and_shows_an_open_trade() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            trade::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("trade layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        for id in [
            trade::PARTNER_NAME_ID,
            trade::PARTNER_STATUS_ID,
            trade::PARTNER_COUNT_ID,
            trade::TRADE_BUTTON_ID,
            trade::SELF_COUNT_ID,
            trade::CLEAR_ALL_BUTTON_ID,
            trade::CLOSE_BUTTON_ID,
        ] {
            assert!(layout.find(id).is_some(), "element {id:#X}");
        }
        let ctl = SecureTradeController::bind(
            &mut host.root,
            &layout,
            WINDOW,
            TradeSprites::default(),
            None,
        )
        .expect("binds");
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        o.add(plain_item(PLAIN_ITEM));
        let items = ItemsView::new(&o.t, PLAYER);
        let mut icons = TestIcons::default();
        host.with_controller::<SecureTradeController, _>(WINDOW, |w, ui| {
            w.update(
                ui,
                &open_view(&[PLAIN_ITEM]),
                &items,
                &mut icons,
                Some(c.strings()),
            );
        });
        assert!(host.root.is_window_visible(WINDOW));
        let count = layout.find(trade::SELF_COUNT_ID).unwrap();
        // The authored entry: fragments ["Total Items: ", ""] with one ITEMS variable.
        assert_eq!(text_of(&host.root, count), "Total Items: 1");
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}
