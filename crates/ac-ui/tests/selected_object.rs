//! The selected-object strip.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SelectedObjectControllerTests.cs`
//! (the split-quantity cases are in `toolbar.rs`). OpenAC's selection,
//! health and object-updated events are the views handed to `update`;
//! the health-changed event is the health share arriving in the facts.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{ClientObject, ItemType};
use ac_dat::objects::DIRECT_STATE_ID;
use ac_ui::element_info::{ElementInfo, ImageMedia};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::items::ItemsView;
use ac_ui::panels::selected_object::{
    self as so, SelectedObjectController, SelectionFacts, SelectionHealth,
};
use ac_ui::states;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{DatElement, Meter, Panel, Text};
use common::items::{Objects, PLAYER, panel_outputs};

struct Strip {
    host: UiHost,
    layout: ImportedLayout,
    name: ElementId,
    overlay: ElementId,
    health: ElementId,
    mana: ElementId,
}

fn strip() -> Strip {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = ui.add(r, Element::at(0.0, 0.0, 300.0, 60.0), Panel::default());
    let name = ui.add(root, Element::at(0.0, 0.0, 100.0, 20.0), Panel::default());
    let mut info = ElementInfo {
        id: so::OVERLAY_ID,
        kind: 3,
        ..ElementInfo::default()
    };
    for (state, file) in [
        ("", 0x0600_0001),
        ("ObjectSelected", 0x0600_1937),
        ("StackedItemSelected", 0x0600_4CF4),
    ] {
        info.state_media
            .insert(state.to_owned(), ImageMedia { file, draw_mode: 3 });
    }
    let overlay = ui.add(root, Element::default(), DatElement::new(Arc::new(info)));
    let meter = |ui: &mut ac_ui::root::UiRoot| {
        ui.add(root, Element::at(0.0, 20.0, 100.0, 10.0), Meter::default())
    };
    let health = meter(ui);
    let mana = meter(ui);
    let by_id: HashMap<u32, ElementId> = [
        (so::NAME_ID, name),
        (so::OVERLAY_ID, overlay),
        (so::HEALTH_METER_ID, health),
        (so::MANA_METER_ID, mana),
    ]
    .into_iter()
    .collect();
    Strip {
        host,
        layout: ImportedLayout::from_parts(root, by_id),
        name,
        overlay,
        health,
        mana,
    }
}

impl Strip {
    fn bind(&mut self) -> SelectedObjectController {
        SelectedObjectController::bind(&mut self.host.root, &self.layout, None)
    }

    fn visible(&self, id: ElementId) -> bool {
        self.host.root.element(id).unwrap().visible()
    }

    fn name_lines(&self, c: &SelectedObjectController) -> Vec<String> {
        c.name_lines()
            .into_iter()
            .flatten()
            .flat_map(|l| self.host.root.widget::<Text>(l).unwrap().content_lines())
            .map(|l| l.text)
            .collect()
    }

    fn overlay_state(&self) -> u32 {
        self.host
            .root
            .widget::<DatElement>(self.overlay)
            .unwrap()
            .active_retail_state_id()
    }

    fn fill(&self, id: ElementId) -> f32 {
        self.host
            .root
            .widget::<Meter>(id)
            .unwrap()
            .fill
            .unwrap_or(0.0)
    }

    fn select(
        &mut self,
        c: &mut SelectedObjectController,
        objects: &Objects,
        selected: Option<u32>,
        facts: &SelectionFacts,
    ) {
        let view = ItemsView {
            selected,
            ..ItemsView::new(&objects.t, PLAYER)
        };
        c.update(&mut self.host.root, &view, facts);
    }

    fn mana_queries(&mut self) -> Vec<u32> {
        self.host.pump();
        panel_outputs(&mut self.host)
            .into_iter()
            .filter_map(|o| match o {
                PanelOutput::QueryItemMana(g) => Some(g),
                _ => None,
            })
            .collect()
    }
}

fn creature(objects: &mut Objects, guid: u32, name: &str) {
    let mut o = ClientObject::new(guid);
    o.name = name.into();
    o.item_type = ItemType::CREATURE;
    objects.add(o);
}

fn stack(objects: &mut Objects, guid: u32, name: &str, plural: &str, size: i32) {
    let mut o = ClientObject::new(guid);
    o.name = name.into();
    o.plural_name = plural.into();
    o.stack_size = size;
    objects.add(o);
}

fn target(percent: Option<f32>) -> SelectionFacts {
    SelectionFacts {
        health: SelectionHealth {
            health_target: true,
            percent,
        },
        world_name: None,
        mana: 0.0,
    }
}

#[test]
fn bind_hides_the_health_bar_and_floats_two_centred_name_lines() {
    let mut s = strip();
    let c = s.bind();
    assert!(!s.visible(s.health));
    let lines = c.name_lines();
    let t = s.host.root.widget::<Text>(lines[0].unwrap()).unwrap();
    assert!(t.centered);
    let e = s.host.root.element(lines[0].unwrap()).unwrap();
    assert!(e.click_through);
    assert!(!e.accepts_focus);
    assert!(!e.is_edit_control);
    assert!(!e.captures_pointer_drag);
    assert!(s.host.root.element(s.name).unwrap().z_order() > 1000);
}

#[test]
fn an_owned_coinstack_reads_the_retail_count_name_and_total() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    objects.player(102, 7);
    objects.get_mut(PLAYER).properties.ints.insert(20, 12_345);
    let coins = 0x5000_0111;
    stack(&mut objects, coins, "Pyreal", "Pyreals", 2_345);
    objects.get_mut(coins).weenie_class_id = so::COINSTACK_WEENIE_CLASS;
    objects.mv(coins, PLAYER, 0);
    s.select(&mut c, &objects, Some(coins), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["2345 Pyreals (of 12345)"]);
}

#[test]
fn nothing_selected_shows_no_name() {
    let mut s = strip();
    let mut c = s.bind();
    let objects = Objects::new();
    s.select(&mut c, &objects, None, &SelectionFacts::default());
    assert!(s.name_lines(&c).is_empty());
}

#[test]
fn a_health_target_without_health_keeps_the_bar_hidden_and_shows_name_and_flash() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA01, "Drudge Prowler");
    s.select(&mut c, &objects, Some(0xAA01), &target(None));
    assert!(!s.visible(s.health));
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
    assert_eq!(s.name_lines(&c), ["Drudge Prowler"]);
    let line = s
        .host
        .root
        .widget::<Text>(c.name_lines()[0].unwrap())
        .unwrap()
        .content_lines();
    assert_eq!(line[0].color, so::NAME_COLOR);
}

#[test]
fn health_arriving_for_the_selection_shows_the_bar() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA02, "Drudge Slinker");
    s.select(&mut c, &objects, Some(0xAA02), &target(None));
    assert!(!s.visible(s.health), "hidden until health arrives");
    s.select(&mut c, &objects, Some(0xAA02), &target(Some(0.6)));
    assert!(s.visible(s.health));
}

#[test]
fn health_for_another_object_does_not_show_the_bar() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA03, "Selected");
    s.select(&mut c, &objects, Some(0xAA03), &target(None));
    // The window hands only the selection's health; another object's
    // arrives as no health for this one.
    s.select(&mut c, &objects, Some(0xAA03), &target(None));
    assert!(!s.visible(s.health));
}

#[test]
fn known_health_shows_the_bar_at_once() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA04, "Olthoi");
    s.select(&mut c, &objects, Some(0xAA04), &target(Some(0.9)));
    assert!(s.visible(s.health));
}

#[test]
fn a_stacked_item_flashes_the_stacked_art_and_has_no_health_bar() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    stack(&mut objects, 0xBB02, "Heal Kit", "Heal Kits", 5);
    s.select(&mut c, &objects, Some(0xBB02), &SelectionFacts::default());
    assert_eq!(s.overlay_state(), states::STACKED_ITEM_SELECTED);
    assert!(!s.visible(s.health));
    assert_eq!(s.name_lines(&c), ["5 Heal Kits"]);
}

#[test]
fn a_non_health_target_shows_its_name_without_a_bar() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    let mut o = ClientObject::new(0xCC03);
    o.name = "Town Crier".into();
    objects.add(o);
    s.select(&mut c, &objects, Some(0xCC03), &SelectionFacts::default());
    assert!(!s.visible(s.health));
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
    assert_eq!(s.name_lines(&c), ["Town Crier"]);
}

#[test]
fn a_world_object_not_in_the_table_is_named_from_the_world() {
    let mut s = strip();
    let mut c = s.bind();
    let objects = Objects::new();
    let facts = SelectionFacts {
        world_name: Some("Drudge Skulker".into()),
        ..target(None)
    };
    s.select(&mut c, &objects, Some(0x8000_0010), &facts);
    assert_eq!(s.name_lines(&c), ["Drudge Skulker"]);
}

#[test]
fn deselecting_clears_the_strip() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xDD04, "Wolf");
    s.select(&mut c, &objects, Some(0xDD04), &target(Some(0.5)));
    assert!(s.visible(s.health));
    s.select(&mut c, &objects, None, &SelectionFacts::default());
    assert!(!s.visible(s.health));
    assert_eq!(s.overlay_state(), DIRECT_STATE_ID);
    assert!(s.name_lines(&c).is_empty());
}

#[test]
fn reselecting_another_object_clears_the_first() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xEE05, "Bandit");
    let mut chest = ClientObject::new(0xFF06);
    chest.name = "Chest".into();
    objects.add(chest);
    s.select(&mut c, &objects, Some(0xEE05), &target(Some(1.0)));
    assert!(s.visible(s.health));
    s.select(&mut c, &objects, Some(0xFF06), &SelectionFacts::default());
    assert!(!s.visible(s.health));
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
    assert_eq!(s.name_lines(&c), ["Chest"]);
}

#[test]
fn the_flash_goes_back_to_blank_after_its_time() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    let mut lever = ClientObject::new(0xAB06);
    lever.name = "Lever".into();
    objects.add(lever);
    s.select(&mut c, &objects, Some(0xAB06), &SelectionFacts::default());
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
    c.tick(&mut s.host.root, 0.1);
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
    c.tick(&mut s.host.root, 0.2);
    assert_eq!(s.overlay_state(), DIRECT_STATE_ID);
}

#[test]
fn a_partial_layout_is_harmless() {
    let mut host = UiHost::new();
    let r = host.root.root();
    let root = host.root.add(r, Element::default(), Panel::default());
    let layout = ImportedLayout::from_parts(root, HashMap::new());
    let mut c = SelectedObjectController::bind(&mut host.root, &layout, None);
    let mut objects = Objects::new();
    creature(&mut objects, 0x1234_5678, "Something");
    let view = ItemsView {
        selected: Some(0x1234_5678),
        ..ItemsView::new(&objects.t, PLAYER)
    };
    c.update(&mut host.root, &view, &target(Some(0.5)));
    c.tick(&mut host.root, 0.5);
    let none = ItemsView::new(&objects.t, PLAYER);
    c.update(&mut host.root, &none, &SelectionFacts::default());
}

#[test]
fn the_health_fill_follows_the_live_share() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA07, "Arwic Banderling");
    s.select(&mut c, &objects, Some(0xAA07), &target(Some(0.5)));
    assert_eq!(s.fill(s.health), 0.5);
    s.select(&mut c, &objects, Some(0xAA07), &target(Some(0.25)));
    assert_eq!(s.fill(s.health), 0.25);
}

#[test]
fn the_health_fill_is_zero_with_nothing_selected() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA08, "Spider");
    s.select(&mut c, &objects, Some(0xAA08), &target(Some(0.8)));
    assert_eq!(s.fill(s.health), 0.8);
    s.select(&mut c, &objects, None, &SelectionFacts::default());
    assert_eq!(s.fill(s.health), 0.0);
}

fn wand(objects: &mut Objects, guid: u32, size: i32) {
    objects.player(102, 7);
    let mut o = ClientObject::new(guid);
    o.name = "Wand".into();
    o.stack_size = size;
    objects.add(o);
    objects.mv(guid, PLAYER, 0);
}

#[test]
fn an_owned_single_item_asks_for_its_mana_and_a_valid_answer_shows_the_bar() {
    // OpenAC `OwnedNonStackItem_QueriesMana_AndValidResponseShowsMeter`: the
    // fill reads the runtime's mana of the selection (OpenAC's `ManaMap`,
    // here the facts' `mana`).
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    wand(&mut objects, 0x5000_0A01, 1);
    let facts = SelectionFacts {
        mana: 0.625,
        ..SelectionFacts::default()
    };
    s.select(&mut c, &objects, Some(0x5000_0A01), &facts);
    assert_eq!(s.mana_queries(), [0x5000_0A01]);
    assert!(!s.visible(s.mana));
    c.on_item_mana(&mut s.host.root, 0x5000_0A01, true);
    assert!(s.visible(s.mana));
    s.select(&mut c, &objects, Some(0x5000_0A01), &facts);
    assert_eq!(s.fill(s.mana), 0.625);
}

#[test]
fn an_answer_for_another_item_shows_no_bar() {
    // OpenAC `SelectedObjectController.OnItemManaChanged`: only the
    // selection's answer counts.
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    wand(&mut objects, 0x5000_0A05, 1);
    s.select(
        &mut c,
        &objects,
        Some(0x5000_0A05),
        &SelectionFacts::default(),
    );
    c.on_item_mana(&mut s.host.root, 0x5000_0A06, true);
    assert!(!s.visible(s.mana));
    assert_eq!(s.mana_queries(), [0x5000_0A05]);
}

#[test]
fn an_invalid_mana_answer_closes_the_query_without_the_bar() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    wand(&mut objects, 0x5000_0A02, 1);
    s.select(
        &mut c,
        &objects,
        Some(0x5000_0A02),
        &SelectionFacts::default(),
    );
    c.on_item_mana(&mut s.host.root, 0x5000_0A02, false);
    assert_eq!(s.mana_queries(), [0x5000_0A02, 0]);
    assert!(!s.visible(s.mana));
}

#[test]
fn leaving_a_shown_mana_bar_closes_its_query() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    wand(&mut objects, 0x5000_0A03, 1);
    s.select(
        &mut c,
        &objects,
        Some(0x5000_0A03),
        &SelectionFacts::default(),
    );
    c.on_item_mana(&mut s.host.root, 0x5000_0A03, true);
    s.select(
        &mut c,
        &objects,
        Some(0x6000_0001),
        &SelectionFacts::default(),
    );
    assert_eq!(s.mana_queries(), [0x5000_0A03, 0]);
}

#[test]
fn an_owned_stack_asks_for_no_mana() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    wand(&mut objects, 0x5000_0A04, 2);
    s.select(
        &mut c,
        &objects,
        Some(0x5000_0A04),
        &SelectionFacts::default(),
    );
    assert!(s.mana_queries().is_empty());
}

#[test]
fn a_disposed_strip_follows_nothing() {
    let mut s = strip();
    let mut c = s.bind();
    c.dispose();
    c.dispose();
    let mut objects = Objects::new();
    creature(&mut objects, 0xAA09, "Mite");
    s.select(&mut c, &objects, Some(0xAA09), &target(Some(0.5)));
    assert!(!s.visible(s.health));
}

#[test]
fn a_stack_size_change_renames_the_selection() {
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    stack(&mut objects, 0xBB04, "Healing Kit", "Healing Kits", 5);
    s.select(&mut c, &objects, Some(0xBB04), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["5 Healing Kits"]);
    objects.get_mut(0xBB04).stack_size = 1;
    s.select(&mut c, &objects, Some(0xBB04), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["Healing Kit"]);
    assert_eq!(s.overlay_state(), states::OBJECT_SELECTED);
}

#[test]
fn a_pickup_joining_a_stack_shows_the_stacks_count_then_its_new_count() {
    // A loose single selected, then (the merge sent) the stack it joins,
    // then that stack grown by the server's new size.
    let mut s = strip();
    let mut c = s.bind();
    let mut objects = Objects::new();
    stack(&mut objects, 0xBB05, "Gold Pea", "Gold Peas", 1);
    stack(&mut objects, 0xBB06, "Gold Pea", "Gold Peas", 5);
    s.select(&mut c, &objects, Some(0xBB05), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["Gold Pea"]);
    s.select(&mut c, &objects, Some(0xBB06), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["5 Gold Peas"]);
    objects.get_mut(0xBB06).stack_size = 6;
    s.select(&mut c, &objects, Some(0xBB06), &SelectionFacts::default());
    assert_eq!(s.name_lines(&c), ["6 Gold Peas"]);
    assert_eq!(s.overlay_state(), states::STACKED_ITEM_SELECTED);
}

#[test]
fn a_long_name_wraps_at_the_last_space_that_fits() {
    let font = ac_ui::font::UiFont::new(
        0x4000_0001,
        ac_ui::font::Atlas {
            texture: ac_ui::draw::TextureRef::Surface(1),
            width: 256,
            height: 256,
        },
        None,
        10.0,
        8.0,
        (0x20u16..0x7F).map(|c| ac_dat::objects::FontCharDesc {
            unicode: c,
            offset_x: 0,
            offset_y: 0,
            width: 6,
            height: 10,
            horizontal_offset_before: 0,
            horizontal_offset_after: 0,
            vertical_offset_before: 0,
        }),
        0,
        0,
    );
    // 6 pixels a character: 60 pixels hold ten.
    assert_eq!(
        so::wrap_name_two_lines("Mattekar Hunter", 60.0, Some(&font)),
        ("Mattekar".to_owned(), "Hunter".to_owned())
    );
    assert_eq!(
        so::wrap_name_two_lines("Drudge", 60.0, Some(&font)),
        ("Drudge".to_owned(), String::new())
    );
    assert_eq!(
        so::wrap_name_two_lines("Unbreakablelongname x", 60.0, Some(&font)),
        ("Unbreakablelongname x".to_owned(), String::new())
    );
    assert_eq!(
        so::wrap_name_two_lines("Mattekar Hunter", 60.0, None),
        ("Mattekar Hunter".to_owned(), String::new())
    );
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::panels::toolbar;
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

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_toolbar_strip_binds_its_name_flash_and_bars() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let font = c.font(DEFAULT_FONT_ID);
        let layout = import(&mut host.root, &c, toolbar::LAYOUT_ID, font.clone()).unwrap();
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let mut strip = SelectedObjectController::bind(&mut host.root, &layout, font);
        assert!(strip.name().is_some());
        assert!(strip.name_lines().iter().all(Option::is_some));
        for id in [so::HEALTH_METER_ID, so::MANA_METER_ID] {
            let m = layout.find(id).expect("bar");
            assert!(host.root.widget::<Meter>(m).is_some());
            assert!(!host.root.element(m).unwrap().visible());
        }
        let overlay = layout.find(so::OVERLAY_ID).unwrap();
        let mut objects = Objects::new();
        creature(&mut objects, 0xAA01, "Drudge Prowler");
        let view = ItemsView {
            selected: Some(0xAA01),
            ..ItemsView::new(&objects.t, PLAYER)
        };
        strip.update(&mut host.root, &view, &target(Some(0.5)));
        assert_eq!(
            host.root.retail_state(overlay),
            Some(states::OBJECT_SELECTED),
            "the authored flash state"
        );
        let health = layout.find(so::HEALTH_METER_ID).unwrap();
        assert!(host.root.element(health).unwrap().visible());
    }
}
