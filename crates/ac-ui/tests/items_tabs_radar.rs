//! Item lists, tab panels, viewports, option widgets and the radar.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiItemListTests.cs`,
//! `UiItemListGridTests.cs`, `UiItemListScrollTests.cs`, `UiRadarTests.cs`
//! and the bitfield predicate of
//! `Layout/OptionsPanelLayoutConformanceTests.cs`. Cells are elements here
//! (the slot widget is a minimal [`ItemCell`]).

mod common;

use std::sync::Arc;

use ac_ui::draw::{Color, DrawCmd, TextureRef};
use ac_ui::element_info::{ElementInfo, StateInfo, TabEntry};
use ac_ui::event::{DragPayload, UiEvent, kind};
use ac_ui::radar::{BlipShape, RadarBlip, RadarSnapshot};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::item_list::{cell_offset, cell_offset_flow, row_count};
use ac_ui::widgets::{
    Bitfield64, Button, DatElement, ItemCell, ItemList, ItemListFlow, Radar, TabPanel, Viewport,
};
use ac_ui::{ElementId, MouseButton};
use common::*;

fn list(ui: &mut UiRoot, w: f32, h: f32, f: impl FnOnce(&mut ItemList)) -> ElementId {
    let r = ui.root();
    let id = ItemList::create(ui, r, Element::at(0.0, 0.0, w, h));
    f(ui.widget_mut::<ItemList>(id).unwrap());
    id
}

fn with_list<R>(
    ui: &mut UiRoot,
    id: ElementId,
    f: impl FnOnce(&mut ItemList, &mut UiRoot) -> R,
) -> R {
    ui.with::<ItemList, _>(id, f).unwrap()
}

fn fill(ui: &mut UiRoot, id: ElementId, n: usize) {
    with_list(ui, id, |l, ui| {
        l.flush(ui);
        for _ in 0..n {
            let c = ui.insert(Element::default(), ItemCell::default());
            l.add_item(ui, id, c);
        }
    });
}

fn cell(ui: &UiRoot, id: ElementId, i: usize) -> &Element {
    let c = ui.widget::<ItemList>(id).unwrap().item(i).unwrap();
    ui.element(c).unwrap()
}

fn layout(ui: &mut UiRoot, id: ElementId) {
    with_list(ui, id, |l, ui| l.layout_cells(ui, id));
}

#[test]
fn a_new_list_is_a_single_cell_slot_that_consumes_its_dat_children() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 36.0, 36.0, |_| {});
    let l = ui.widget::<ItemList>(id).unwrap();
    assert_eq!(l.num_items(), 1);
    assert!(ac_ui::Widget::consumes_dat_children(l));
    let c = l.item(0).unwrap();
    assert_eq!(
        ui.widget::<ItemCell>(c).unwrap().empty_sprite,
        ac_ui::widgets::item_list::DEFAULT_EMPTY_SPRITE
    );
}

#[test]
fn the_cell_empty_sprite_stamps_existing_and_future_cells() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 36.0, 36.0, |_| {});
    with_list(&mut ui, id, |l, ui| {
        l.set_cell_empty_sprite(ui, 0);
        let c0 = l.item(0).unwrap();
        assert_eq!(ui.widget::<ItemCell>(c0).unwrap().empty_sprite, 0x0600_74CF);
        l.set_cell_empty_sprite(ui, 0x0600_4D20);
        assert_eq!(ui.widget::<ItemCell>(c0).unwrap().empty_sprite, 0x0600_4D20);
        let c = ui.insert(Element::default(), ItemCell::default());
        l.add_item(ui, id, c);
        assert_eq!(ui.widget::<ItemCell>(c).unwrap().empty_sprite, 0x0600_4D20);
    });
}

#[test]
fn a_catalog_drop_reports_payload_and_local_coordinates() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 36.0, 36.0, |l| l.accepts_catalog_drops = true);
    let e = UiEvent {
        kind: kind::DROP_RELEASED,
        target: Some(id),
        data1: 17,
        data2: 9,
        payload: Some(DragPayload::new(42u32)),
        ..UiEvent::default()
    };
    ui.drain_output();
    assert!(ui.send_event(id, &e));
    let got = ui.drain_output().into_iter().find_map(|o| match o {
        UiOutput::Element {
            message: ElementMessage::CatalogDropped { payload, x, y },
            ..
        } => Some((*payload.get::<u32>().unwrap(), x, y)),
        _ => None,
    });
    assert_eq!(got, Some((42, 17, 9)));
}

#[derive(Debug)]
struct Filled;

impl ac_ui::Widget for Filled {}

#[test]
fn horizontal_empty_slots_fill_the_view_and_shrink_only_the_empty_tail() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 130.0, 32.0, |l| {
        l.cell_width = 32.0;
        l.cell_height = 32.0;
        l.single_row = true;
        l.fill_visible_empty_slots = true;
    });
    with_list(&mut ui, id, |l, ui| {
        l.flush(ui);
        let filled = ui.insert(Element::default(), Filled);
        l.add_item(ui, id, filled);
    });
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 4);
    let lefts: Vec<f32> = (0..4).map(|i| cell(&ui, id, i).left).collect();
    assert_eq!(lefts, vec![0.0, 32.0, 64.0, 96.0]);
    ui.element_mut(id).unwrap().width = 65.0;
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 2);
    ui.element_mut(id).unwrap().width = 16.0;
    layout(&mut ui, id);
    // The filled cell is never trimmed.
    assert_eq!(ui.children(id).len(), 1);
    let first = ui.widget::<ItemList>(id).unwrap().item(0).unwrap();
    assert!(ui.widget::<Filled>(first).is_some());
}

/// OpenAC `UiItemListTests.FixedSlotCount_PadsToTheCount_IgnoringViewportWidth`.
#[test]
fn a_fixed_slot_count_pads_to_the_count_whatever_the_width() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 130.0, 32.0, |l| {
        l.cell_width = 32.0;
        l.cell_height = 32.0;
        l.single_row = true;
        l.fill_visible_empty_slots = true;
        l.fixed_slot_count = Some(1);
    });
    with_list(&mut ui, id, |l, ui| l.flush(ui));
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 1);
    ui.widget_mut::<ItemList>(id).unwrap().fixed_slot_count = Some(6);
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 6);
    ui.widget_mut::<ItemList>(id).unwrap().fixed_slot_count = None;
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 4);
}

/// OpenAC `UiItemListTests.FixedSlotCount_DropsTheLastCellsPastTheCount_EvenWithItems`.
#[test]
fn a_fixed_slot_count_drops_the_last_cells_past_it_even_with_items() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 130.0, 32.0, |l| {
        l.cell_width = 32.0;
        l.cell_height = 32.0;
        l.single_row = true;
        l.fill_visible_empty_slots = true;
        l.fixed_slot_count = Some(1);
    });
    let first = with_list(&mut ui, id, |l, ui| {
        l.defer_layout();
        l.flush(ui);
        let first = ui.insert(Element::default(), Filled);
        l.add_item(ui, id, first);
        let second = ui.insert(Element::default(), Filled);
        l.add_item(ui, id, second);
        l.end_defer_layout(ui, id);
        first
    });
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 1);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().item(0), Some(first));
    ui.widget_mut::<ItemList>(id).unwrap().fixed_slot_count = Some(0);
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 0);
}

/// OpenAC `UiItemListTests.FixedSlotCount_HoldsBeforeTheCellSizeIsKnown`.
#[test]
fn a_fixed_slot_count_holds_before_the_cell_size_is_known() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 130.0, 32.0, |l| l.fixed_slot_count = Some(2));
    with_list(&mut ui, id, |l, ui| {
        l.defer_layout();
        l.flush(ui);
        for _ in 0..3 {
            let c = ui.insert(Element::default(), Filled);
            l.add_item(ui, id, c);
        }
        l.end_defer_layout(ui, id);
    });
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 2);
    ui.widget_mut::<ItemList>(id).unwrap().fixed_slot_count = Some(4);
    layout(&mut ui, id);
    assert_eq!(ui.widget::<ItemList>(id).unwrap().num_items(), 4);
}

#[test]
fn scroll_item_into_view_moves_only_when_the_row_is_outside() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 100.0, 64.0, |l| {
        l.columns = 1;
        l.cell_width = 100.0;
        l.cell_height = 32.0;
    });
    fill(&mut ui, id, 5);
    let scroll = |ui: &mut UiRoot, i: usize| {
        let me = id;
        ui.with::<ItemList, _>(id, |l, ui| {
            l.scroll_item_into_view(ui, me, i);
            l.scroll.scroll_y()
        })
        .unwrap()
    };
    assert_eq!(scroll(&mut ui, 3), 64);
    assert_eq!(scroll(&mut ui, 2), 64);
    assert_eq!(scroll(&mut ui, 0), 0);
}

#[test]
fn a_horizontal_scroll_uses_pixel_offsets_and_keeps_partial_edge_cells() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 80.0, 32.0, |l| {
        l.cell_width = 32.0;
        l.cell_height = 32.0;
        l.single_row = true;
        l.horizontal_scroll = true;
    });
    fill(&mut ui, id, 4);
    ui.widget_mut::<ItemList>(id).unwrap().scroll.content_height = 128;
    ui.widget_mut::<ItemList>(id).unwrap().scroll.view_height = 80;
    ui.widget_mut::<ItemList>(id)
        .unwrap()
        .scroll
        .set_scroll_y(17);
    layout(&mut ui, id);
    let l = ui.widget::<ItemList>(id).unwrap();
    assert_eq!((l.scroll.content_height, l.scroll.view_height), (128, 80));
    assert_eq!(cell(&ui, id, 0).left, -17.0);
    assert!(cell(&ui, id, 0).visible() && cell(&ui, id, 3).visible());
    with_list(&mut ui, id, |l, ui| l.scroll_item_into_view(ui, id, 3));
    assert_eq!(ui.widget::<ItemList>(id).unwrap().scroll.scroll_y(), 48);
    layout(&mut ui, id);
    assert_eq!(cell(&ui, id, 3).left, 48.0);
}

#[test]
fn cell_offsets_row_and_column_major() {
    assert_eq!(cell_offset(0, 3, 36.0, 36.0), (0.0, 0.0));
    assert_eq!(cell_offset(2, 3, 36.0, 36.0), (72.0, 0.0));
    assert_eq!(cell_offset(4, 3, 36.0, 36.0), (36.0, 36.0));
    let cm = ItemListFlow::ColumnMajor;
    assert_eq!(cell_offset_flow(0, 3, 7, cm, 36.0, 36.0), (0.0, 0.0));
    assert_eq!(cell_offset_flow(1, 3, 7, cm, 36.0, 36.0), (0.0, 36.0));
    assert_eq!(cell_offset_flow(3, 3, 7, cm, 36.0, 36.0), (36.0, 0.0));
}

#[test]
fn grid_mode_positions_cells_in_columns_either_flow() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 200.0, 200.0, |l| {
        l.columns = 3;
        l.cell_width = 36.0;
        l.cell_height = 36.0;
    });
    fill(&mut ui, id, 7);
    let c = cell(&ui, id, 4);
    assert_eq!((c.left, c.top, c.width, c.height), (36.0, 36.0, 36.0, 36.0));
    ui.widget_mut::<ItemList>(id).unwrap().flow = ItemListFlow::ColumnMajor;
    layout(&mut ui, id);
    let c = cell(&ui, id, 3);
    assert_eq!((c.left, c.top), (36.0, 0.0));
}

#[test]
fn fill_mode_sizes_a_single_cell_to_the_list() {
    let mut ui = root(400.0, 400.0);
    let id = list(&mut ui, 36.0, 36.0, |_| {});
    fill(&mut ui, id, 1);
    let c = cell(&ui, id, 0);
    assert_eq!((c.left, c.top, c.width, c.height), (0.0, 0.0, 36.0, 36.0));
}

fn grid(ui: &mut UiRoot, items: usize) -> ElementId {
    let id = list(ui, 192.0, 96.0, |l| {
        l.columns = 6;
        l.cell_width = 32.0;
        l.cell_height = 32.0;
    });
    fill(ui, id, items);
    layout(ui, id);
    id
}

#[test]
fn the_grid_drives_its_scroll_model_and_clips_rows() {
    assert_eq!(row_count(0, 6), 0);
    assert_eq!(row_count(1, 6), 1);
    assert_eq!(row_count(41, 6), 7);
    let mut ui = root(400.0, 400.0);
    let id = grid(&mut ui, 30);
    let s = ui.widget::<ItemList>(id).unwrap().scroll;
    assert_eq!(
        (s.content_height, s.view_height, s.max_scroll()),
        (160, 96, 64)
    );
    assert!(s.has_overflow());
    assert!(cell(&ui, id, 0).visible() && cell(&ui, id, 12).visible());
    assert!(!cell(&ui, id, 18).visible());

    ui.widget_mut::<ItemList>(id)
        .unwrap()
        .scroll
        .set_scroll_y(32);
    layout(&mut ui, id);
    assert!(!cell(&ui, id, 0).visible());
    assert_eq!(cell(&ui, id, 6).top, 0.0);
    assert!(cell(&ui, id, 18).visible());

    ui.widget_mut::<ItemList>(id)
        .unwrap()
        .scroll
        .set_scroll_y(8);
    layout(&mut ui, id);
    assert!(cell(&ui, id, 0).visible());
    assert_eq!(cell(&ui, id, 0).top, -8.0);
    assert_eq!(cell(&ui, id, 18).top, 88.0);
    assert!(!cell(&ui, id, 24).visible());
    // The list clips its cells: a point above it hits nothing of it.
    assert!(ui.hit_test(id, 5.0, -1.0).is_none());
    assert!(ui.hit_test(id, 5.0, 1.0).is_some());
}

#[test]
fn a_deferred_rebuild_keeps_the_pixel_offset() {
    let mut ui = root(400.0, 400.0);
    let id = grid(&mut ui, 30);
    ui.widget_mut::<ItemList>(id)
        .unwrap()
        .scroll
        .set_scroll_y(40);
    with_list(&mut ui, id, |l, ui| {
        l.defer_layout();
        l.flush(ui);
        for _ in 0..30 {
            let c = ui.insert(Element::default(), ItemCell::default());
            l.add_item(ui, id, c);
        }
        l.end_defer_layout(ui, id);
    });
    assert_eq!(ui.widget::<ItemList>(id).unwrap().scroll.scroll_y(), 40);
    assert_eq!(cell(&ui, id, 6).top, -8.0);
}

#[test]
fn scroll_survives_the_per_frame_anchor_pass() {
    let mut ui = root(400.0, 400.0);
    let id = grid(&mut ui, 30);
    let cells: Vec<ElementId> = ui.children(id);
    for c in &cells {
        ui.apply_anchor(*c, 192.0, 96.0);
    }
    ui.widget_mut::<ItemList>(id)
        .unwrap()
        .scroll
        .set_scroll_y(32);
    layout(&mut ui, id);
    for c in &cells {
        ui.apply_anchor(*c, 192.0, 96.0);
    }
    assert_eq!(cell(&ui, id, 6).top, 0.0);
    assert!(!cell(&ui, id, 0).visible());
}

#[test]
fn the_wheel_down_scrolls_toward_the_bottom() {
    let mut ui = root(400.0, 400.0);
    let id = grid(&mut ui, 30);
    let e = UiEvent {
        kind: kind::SCROLL,
        data0: -1,
        ..UiEvent::default()
    };
    assert!(ui.send_event(id, &e));
    assert_eq!(ui.widget::<ItemList>(id).unwrap().scroll.scroll_y(), 32);
}

// ── Tab panel ───────────────────────────────────────────────────────────

fn tab_tree(ui: &mut UiRoot) -> (ElementId, [ElementId; 2], [ElementId; 2]) {
    let mut info = ElementInfo {
        id: 0x1000_0100,
        kind: 8,
        width: 200.0,
        height: 200.0,
        ..ElementInfo::default()
    };
    info.tab_table = vec![
        TabEntry {
            button: 0x1000_0101,
            page: 0x1000_0201,
            is_default: false,
        },
        TabEntry {
            button: 0x1000_0102,
            page: 0x1000_0202,
            is_default: true,
        },
    ];
    let r = ui.root();
    let panel = ui.add(
        r,
        Element::at(0.0, 0.0, 200.0, 200.0),
        TabPanel::new(Arc::new(info)),
    );
    let mut tab_info = ElementInfo {
        kind: 1,
        width: 50.0,
        height: 20.0,
        ..ElementInfo::default()
    };
    tab_info.set_state(11, StateInfo::named(11, "Closed"));
    tab_info.set_state(12, StateInfo::named(12, "Open"));
    let mut buttons = [r; 2];
    let mut pages = [r; 2];
    for i in 0..2 {
        let mut b = Element::at(i as f32 * 60.0, 0.0, 50.0, 20.0);
        b.dat_element_id = 0x1000_0101 + i as u32;
        let tab = DatElement::new(Arc::new(tab_info.clone()));
        buttons[i] = ui.add(panel, b, tab);
        let mut p = Element::at(0.0, 30.0, 200.0, 170.0);
        p.dat_element_id = 0x1000_0201 + i as u32;
        p.click_through = true;
        pages[i] = ui.add(panel, p, ac_ui::tree::Container);
    }
    (panel, buttons, pages)
}

#[test]
fn a_tab_panel_shows_the_default_page_and_switches_on_a_tab_click() {
    let mut ui = root(400.0, 400.0);
    let (panel, buttons, pages) = tab_tree(&mut ui);
    ui.with::<TabPanel, _>(panel, |t, ui| t.activate(ui, panel));
    let t = ui.widget::<TabPanel>(panel).unwrap();
    assert_eq!(t.active_page(), 0x1000_0202);
    assert!(t.unresolved().is_empty());
    assert!(!ui.element(pages[0]).unwrap().visible());
    assert!(ui.element(pages[1]).unwrap().visible());
    assert_eq!(ui.retail_state(buttons[1]), Some(12));
    assert_eq!(ui.retail_state(buttons[0]), Some(11));
    ui.drain_output();
    // The tab buttons take presses now; a click switches pages.
    ui.on_mouse_down(MouseButton::Left, 10, 10, 0);
    ui.on_mouse_up(MouseButton::Left, 10, 10, 0);
    assert_eq!(
        ui.widget::<TabPanel>(panel).unwrap().active_page(),
        0x1000_0201
    );
    assert!(ui.element(pages[0]).unwrap().visible());
    assert!(!ui.element(pages[1]).unwrap().visible());
    assert!(ui.drain_output().iter().any(|o| matches!(
        o,
        UiOutput::Element {
            message: ElementMessage::TabChanged {
                previous: 0x1000_0202,
                page: 0x1000_0201
            },
            ..
        }
    )));
}

// ── Viewport ────────────────────────────────────────────────────────────

#[test]
fn a_viewport_draws_the_renderers_texture_flipped_when_bottom_up() {
    let mut ui = root(400.0, 400.0);
    let r = ui.root();
    let id = ui.add(
        r,
        Element::at(10.0, 20.0, 100.0, 50.0),
        Viewport {
            texture: Some(TextureRef::Runtime(7)),
            texture_is_bottom_up: true,
            emits_clicks: true,
        },
    );
    let list = ui.draw((400.0, 400.0), &ac_ui::draw::NoSprites);
    let quad = list
        .commands()
        .find_map(|c| match c {
            DrawCmd::Sprite {
                texture: TextureRef::Runtime(7),
                quad,
                ..
            } => Some(*quad),
            _ => None,
        })
        .unwrap();
    assert_eq!((quad.x, quad.y, quad.w, quad.h), (10.0, 20.0, 100.0, 50.0));
    assert_eq!((quad.v0, quad.v1), (1.0, 0.0));
    ui.drain_output();
    ui.on_mouse_down(MouseButton::Left, 15, 25, 0);
    ui.on_mouse_up(MouseButton::Left, 15, 25, 0);
    assert!(ui.drain_output().iter().any(|o| matches!(
        o,
        UiOutput::Element { element, message: ElementMessage::ViewportClicked { x: 5, y: 5 } }
            if *element == id
    )));
}

// ── Bitfield ────────────────────────────────────────────────────────────

#[test]
fn a_bitfield_row_is_checked_when_any_bit_of_its_mask_is_set() {
    let mut b = Bitfield64::default();
    b.set_default_value(0b01, 0);
    assert!(b.is_any_set(0b11, 0));
    assert!(!b.is_all_set(0b11, 0));
    assert!(!b.is_any_set(0b100, 0));
    assert_eq!(b.current(), (0b01, 0));
}

#[test]
fn a_bitfield_row_click_toggles_its_whole_mask() {
    let mut ui = root(400.0, 400.0);
    let r = ui.root();
    let field = ui.add(r, Element::at(0.0, 0.0, 200.0, 0.0), Bitfield64::default());
    let row = ui.insert(Element::at(0.0, 0.0, 200.0, 20.0), ac_ui::tree::Container);
    let info = Arc::new(ElementInfo {
        id: 0x1000_0219,
        kind: 0x1000_0035,
        width: 20.0,
        height: 20.0,
        ..ElementInfo::default()
    });
    let mut check = Element::at(0.0, 0.0, 20.0, 20.0);
    check.dat_element_id = 0x1000_0219;
    Button::configure(&mut check);
    let toggle = ui.add(row, check, Button::new(info, None, Vec::new(), 0.0));
    let added = ui
        .with::<Bitfield64, _>(field, |b, ui| {
            b.add_row(ui, field, row, 0b11, 0, "Speech", Some("Spoken text"))
        })
        .unwrap();
    assert_eq!(added, Some(toggle));
    assert_eq!(ui.element(field).unwrap().height, 20.0);
    assert_eq!(
        ui.widget::<Button>(toggle).unwrap().label.as_deref(),
        Some("Speech")
    );
    ui.drain_output();
    ui.on_mouse_down(MouseButton::Left, 5, 5, 0);
    ui.on_mouse_up(MouseButton::Left, 5, 5, 0);
    assert_eq!(ui.widget::<Bitfield64>(field).unwrap().current(), (0b11, 0));
    assert!(ui.widget::<Button>(toggle).unwrap().selected());
    assert!(ui.drain_output().iter().any(|o| matches!(
        o,
        UiOutput::Element {
            message: ElementMessage::BitfieldChanged { low: 0b11, high: 0 },
            ..
        }
    )));
    ui.on_mouse_down(MouseButton::Left, 5, 5, 0);
    ui.on_mouse_up(MouseButton::Left, 5, 5, 0);
    assert_eq!(ui.widget::<Bitfield64>(field).unwrap().current(), (0, 0));
}

// ── Radar widget ────────────────────────────────────────────────────────

fn blip(id: u32, name: &str, x: f32, y: f32, shape: BlipShape) -> RadarBlip {
    RadarBlip {
        object_id: id,
        name: name.to_owned(),
        pixel_x: x,
        pixel_y: y,
        color: Color::WHITE,
        shape,
        selected: false,
    }
}

fn snapshot(blips: Vec<RadarBlip>) -> RadarSnapshot {
    RadarSnapshot {
        blips,
        ..RadarSnapshot::default()
    }
}

fn radar(ui: &mut UiRoot, s: RadarSnapshot) -> ElementId {
    let r = ui.root();
    let id = ui.add(r, Element::at(0.0, 0.0, 120.0, 140.0), Radar::default());
    ui.with::<Radar, _>(id, |w, ui| w.apply_snapshot(ui, id, s));
    id
}

fn radar_w(ui: &UiRoot, id: ElementId) -> &Radar {
    ui.widget::<Radar>(id).unwrap()
}

fn messages(ui: &mut UiRoot) -> Vec<ElementMessage> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Element { message, .. } => Some(message),
            _ => None,
        })
        .collect()
}

#[test]
fn hover_and_click_select_the_nearest_projected_blip() {
    let mut ui = root(200.0, 200.0);
    let mut s = snapshot(vec![
        blip(0x10, "Far", 31.0, 30.0, BlipShape::Plus),
        blip(0x20, "Near", 28.0, 30.0, BlipShape::Circle),
    ]);
    s.coordinates_text = Some("1.0N,1.0E".into());
    let id = radar(&mut ui, s);
    ui.on_mouse_move(27, 30);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), Some(0x20));
    assert!(
        messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::HoveredObjectChanged(Some(0x20))))
    );
    assert_eq!(radar_w(&ui, id).hovered_name(), Some("Near"));
    ui.on_mouse_down(MouseButton::Left, 27, 30, 0);
    ui.on_mouse_up(MouseButton::Left, 27, 30, 0);
    assert!(
        messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::ObjectSelected(0x20)))
    );
    ui.on_mouse_move(100, 100);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), None);
    assert!(
        messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::HoveredObjectChanged(None)))
    );
}

#[test]
fn blank_blips_disable_hover_but_keep_the_snapshot() {
    let mut ui = root(200.0, 200.0);
    let mut s = snapshot(vec![blip(0x10, "Hidden", 30.0, 30.0, BlipShape::Plus)]);
    s.blank_blips = true;
    let id = radar(&mut ui, s);
    ui.on_mouse_move(30, 30);
    assert_eq!(radar_w(&ui, id).snapshot().blips.len(), 1);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), None);
}

fn tooltip_names(ui: &mut UiRoot, id: ElementId) -> (Vec<Option<String>>, usize) {
    let mut shown = Vec::new();
    let mut hidden = 0;
    for o in ui.drain_output() {
        match o {
            UiOutput::TooltipShow(e) if e == id => {
                shown.push(radar_w(ui, id).hovered_name().map(str::to_owned));
            }
            UiOutput::TooltipHide(e) if e == id => hidden += 1,
            _ => {}
        }
    }
    (shown, hidden)
}

#[test]
fn moving_between_blips_shows_the_second_name() {
    let mut ui = root(200.0, 200.0);
    let id = radar(
        &mut ui,
        snapshot(vec![
            blip(0x10, "Alpha", 20.0, 20.0, BlipShape::Plus),
            blip(0x20, "Beta", 60.0, 60.0, BlipShape::Circle),
        ]),
    );
    ui.tick(0.0, 0);
    ui.on_mouse_move(20, 20);
    ui.tick(0.3, 300);
    assert_eq!(tooltip_names(&mut ui, id).0, vec![Some("Alpha".into())]);
    ui.on_mouse_move(60, 60);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), Some(0x20));
    ui.tick(0.3, 600);
    assert_eq!(tooltip_names(&mut ui, id).0, vec![Some("Beta".into())]);
}

#[test]
fn moving_onto_empty_radar_clears_the_name() {
    let mut ui = root(200.0, 200.0);
    let id = radar(
        &mut ui,
        snapshot(vec![blip(0x10, "Alpha", 20.0, 20.0, BlipShape::Plus)]),
    );
    ui.tick(0.0, 0);
    ui.on_mouse_move(20, 20);
    ui.tick(0.3, 300);
    assert_eq!(tooltip_names(&mut ui, id).0, vec![Some("Alpha".into())]);
    ui.on_mouse_move(90, 90);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), None);
    let (_, hidden) = tooltip_names(&mut ui, id);
    assert_eq!(hidden, 1);
}

#[test]
fn blips_moving_under_a_parked_cursor_change_the_hovered_object() {
    let mut ui = root(200.0, 200.0);
    let id = radar(
        &mut ui,
        snapshot(vec![
            blip(0x10, "Alpha", 20.0, 20.0, BlipShape::Plus),
            blip(0x20, "Beta", 60.0, 60.0, BlipShape::Circle),
        ]),
    );
    ui.tick(0.0, 0);
    ui.on_mouse_move(20, 20);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), Some(0x10));
    let moved = snapshot(vec![
        blip(0x10, "Alpha", 60.0, 60.0, BlipShape::Plus),
        blip(0x20, "Beta", 20.0, 20.0, BlipShape::Circle),
    ]);
    ui.with::<Radar, _>(id, |w, ui| w.apply_snapshot(ui, id, moved));
    ui.tick(0.016, 16);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), Some(0x20));
    assert_eq!(radar_w(&ui, id).hovered_name(), Some("Beta"));
}

#[test]
fn equidistant_blips_keep_the_one_drawn_first() {
    let mut ui = root(200.0, 200.0);
    let id = radar(
        &mut ui,
        snapshot(vec![
            blip(0x10, "First", 20.0, 20.0, BlipShape::Plus),
            blip(0x20, "Second", 24.0, 20.0, BlipShape::Circle),
        ]),
    );
    ui.on_mouse_move(22, 20);
    assert_eq!(radar_w(&ui, id).hovered_object_id(), Some(0x10));
}

#[test]
fn the_radar_draws_blips_and_the_player_marker_over_its_children() {
    let mut ui = root(200.0, 200.0);
    let mut b = blip(0x10, "A", 20.0, 20.0, BlipShape::Plus);
    b.selected = true;
    radar(&mut ui, snapshot(vec![b]));
    let list = ui.draw((200.0, 200.0), &ac_ui::draw::NoSprites);
    let fills = list
        .commands()
        .filter(|c| matches!(c, DrawCmd::Fill { w, h, .. } if *w == 1.0 && *h == 1.0))
        .count();
    // Plus 5 + selection ring 20 + player marker 9.
    assert_eq!(fills, 5 + 20 + 9);
}
