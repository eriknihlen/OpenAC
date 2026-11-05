//! Drop-down menus and meters.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiMenuTests.cs`,
//! `UiMenuPopupRoutingTests.cs`, `UiMeterTests.cs`, `UiMeterVerticalTests.cs`
//! and the detail-icon cases of `Layout/VitalsDetailToggleTests.cs`.
//! OpenAC's `OnSelect` callback is [`ElementMessage::MenuSelected`] (with a
//! `u64` payload); its per-payload `EnabledProvider` is
//! [`MenuItem::enabled`].

mod common;

use ac_ui::ElementId;
use ac_ui::MouseButton;
use ac_ui::event::{UiEvent, kind};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::text::Scrollable;
use ac_ui::tree::Element;
use ac_ui::widgets::meter::{compute_detail_overlay_rect, compute_fill_rect, compute_v_fill_rect};
use ac_ui::widgets::scrollbar::thumb_rect;
use ac_ui::widgets::{DetailOverlay, Menu, MenuItem, Meter, Panel};
use common::*;

const SQUELCH: u64 = 100;
const TELL_TO_SELECTED: u64 = 101;
const SAY: u64 = 0;
const FELLOWSHIP: u64 = 1;
const TRADE: u64 = 9;
const OLTHOI: u64 = 11;

/// The chat talk-focus list: two specials, then the twelve channels;
/// only Say, General, LFG and Trade are available.
fn channel_items() -> Vec<MenuItem> {
    let channels = [
        ("Chat to All", SAY, true),
        ("Tell to Fellows", FELLOWSHIP, false),
        ("Tell to General Chat", 2, true),
        ("Tell to LFG Chat", 3, true),
        ("Tell to Society Chat", 4, false),
        ("Tell to Monarch", 5, false),
        ("Tell to Patron", 6, false),
        ("Tell to Vassals", 7, false),
        ("Tell to Allegiance", 8, false),
        ("Tell to Trade Chat", TRADE, true),
        ("Tell to Roleplay Chat", 10, false),
        ("Tell to Olthoi Chat", OLTHOI, false),
    ];
    let mut items = vec![
        MenuItem::new("Squelch (ignore)", SQUELCH),
        MenuItem::new("Tell to Selected", TELL_TO_SELECTED),
    ];
    items.extend(
        channels
            .iter()
            .map(|(l, p, e)| MenuItem::new(*l, *p).with_enabled(*e)),
    );
    items
}

fn menu_in(ui: &mut UiRoot, m: Menu, x: f32, y: f32, w: f32, h: f32) -> ElementId {
    let mut e = Element::at(x, y, w, h);
    Menu::configure(&mut e);
    let r = ui.root();
    ui.add(r, e, m)
}

fn make_menu(ui: &mut UiRoot) -> ElementId {
    let mut m = Menu::default();
    m.items = channel_items();
    m.selected = Some(SAY);
    menu_in(ui, m, 300.0, 300.0, 80.0, 18.0)
}

fn press(ui: &mut UiRoot, id: ElementId, x: i32, y: i32) -> bool {
    let e = UiEvent {
        kind: kind::MOUSE_DOWN,
        data1: x,
        data2: y,
        target: Some(id),
        ..UiEvent::default()
    };
    ui.send_event(id, &e)
}

fn send(ui: &mut UiRoot, id: ElementId, k: i32, d0: i32) -> bool {
    let e = UiEvent {
        kind: k,
        data0: d0,
        target: Some(id),
        ..UiEvent::default()
    };
    ui.send_event(id, &e)
}

fn picked(ui: &mut UiRoot) -> Vec<u64> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Element {
                message: ElementMessage::MenuSelected(p),
                ..
            } => Some(p),
            _ => None,
        })
        .collect()
}

fn menu(ui: &UiRoot, id: ElementId) -> &Menu {
    ui.widget::<Menu>(id).unwrap()
}

#[test]
fn items_have_the_fourteen_retail_entries() {
    let items = channel_items();
    assert_eq!(items.len(), 14);
    assert_eq!(items[0].label, "Squelch (ignore)");
    assert_eq!(items[13].label, "Tell to Olthoi Chat");
    assert_eq!(items[13].payload, OLTHOI);
    assert_eq!(Menu::default().selected, None);
}

#[test]
fn picking_an_available_left_column_item_reports_it() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    assert!(press(&mut ui, id, 10, 5));
    assert!(menu(&ui, id).is_open());
    ui.drain_output();
    assert!(press(&mut ui, id, 10, -76));
    assert_eq!(picked(&mut ui), vec![SAY]);
    assert!(!menu(&ui, id).is_open());
}

#[test]
fn picking_an_available_right_column_item_reports_it() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    assert!(press(&mut ui, id, 200, -42));
    assert_eq!(picked(&mut ui), vec![TRADE]);
}

#[test]
fn picking_a_special_reports_its_payload_and_leaves_selection_alone() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    assert!(press(&mut ui, id, 10, -110));
    assert_eq!(picked(&mut ui), vec![SQUELCH]);
    assert_eq!(menu(&ui, id).selected, Some(SAY));
}

#[test]
fn an_unavailable_channel_does_not_fire() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    assert!(press(&mut ui, id, 10, -60));
    assert!(picked(&mut ui).is_empty());
    // Enabled, the same row fires.
    ui.widget_mut::<Menu>(id).unwrap().items[3].enabled = true;
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    press(&mut ui, id, 10, -60);
    assert_eq!(picked(&mut ui), vec![FELLOWSHIP]);
}

#[test]
fn opening_asks_the_owner_to_refresh_the_entries() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    ui.drain_output();
    press(&mut ui, id, 10, 5);
    assert!(ui.drain_output().iter().any(|o| matches!(
        o,
        UiOutput::Element {
            message: ElementMessage::MenuOpening,
            ..
        }
    )));
    assert_eq!(ui.active_popup(), Some(id));
}

const BORDER: f32 = 5.0;

fn scrollable_menu(ui: &mut UiRoot, count: usize) -> ElementId {
    let mut m = Menu::default();
    m.items = (0..count)
        .map(|i| MenuItem::new(format!("Category {i}"), i as u64))
        .collect();
    m.selected = Some(0);
    m.scrollable = true;
    m.rows_per_column = 6;
    m.row_height = 18.0;
    m.column_width = 100.0;
    m.scrollbar_width = 16.0;
    m.scroll_button_extent = 16.0;
    menu_in(ui, m, 300.0, 300.0, 100.0, 18.0)
}

fn raw_y(rows: usize, row_h: f32, iy: f32) -> i32 {
    let outer_h = rows as f32 * row_h + 2.0 * BORDER;
    (iy - outer_h + BORDER) as i32
}

fn raw_x(ix: f32) -> i32 {
    (ix + BORDER) as i32
}

fn popup_scroll(ui: &UiRoot, id: ElementId) -> Scrollable {
    menu(ui, id).popup_scroll
}

#[test]
fn a_scrollable_popup_takes_its_extents_from_the_authored_rows() {
    let mut ui = root(800.0, 600.0);
    let id = scrollable_menu(&mut ui, 18);
    press(&mut ui, id, 10, 5);
    send(&mut ui, id, kind::SCROLL, 0);
    let s = popup_scroll(&ui, id);
    assert_eq!(s.content_height, 18 * 18);
    assert_eq!(s.view_height, 6 * 18);
    assert!(s.has_overflow());
}

#[test]
fn a_scrollable_popup_picks_the_first_visible_row_and_its_bar_scrolls() {
    let mut ui = root(800.0, 600.0);
    let id = scrollable_menu(&mut ui, 18);
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    let row = raw_y(6, 18.0, 9.0);
    assert!(press(&mut ui, id, raw_x(10.0), row));
    assert_eq!(picked(&mut ui), vec![0]);

    // Reopen; the scrollbar track does not pick, the down arrow scrolls a
    // row and the next pick is the advanced item.
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    let bar_x = raw_x(100.0 + 8.0);
    assert!(press(&mut ui, id, bar_x, raw_y(6, 18.0, 54.0)));
    assert!(picked(&mut ui).is_empty());
    assert!(menu(&ui, id).is_open());
    ui.widget_mut::<Menu>(id)
        .unwrap()
        .popup_scroll
        .set_scroll_y(0);
    assert!(press(&mut ui, id, bar_x, raw_y(6, 18.0, 108.0 - 8.0)));
    assert_eq!(popup_scroll(&ui, id).scroll_y(), 18);
    assert!(press(&mut ui, id, bar_x, raw_y(6, 18.0, 108.0 - 8.0)));
    assert_eq!(popup_scroll(&ui, id).scroll_y(), 36);
    assert!(press(&mut ui, id, bar_x, raw_y(6, 18.0, 8.0)));
    assert_eq!(popup_scroll(&ui, id).scroll_y(), 18);
    ui.drain_output();
    assert!(press(&mut ui, id, raw_x(10.0), row));
    assert_eq!(picked(&mut ui), vec![1]);
}

#[test]
fn the_wheel_scrolls_an_open_scrollable_popup() {
    let mut ui = root(800.0, 600.0);
    let id = scrollable_menu(&mut ui, 18);
    press(&mut ui, id, 10, 5);
    assert!(send(&mut ui, id, kind::SCROLL, -1));
    assert_eq!(popup_scroll(&ui, id).scroll_y(), 18);
}

#[test]
fn dragging_the_popup_thumb_scrolls_and_keeps_the_popup_open() {
    let mut ui = root(800.0, 600.0);
    let id = scrollable_menu(&mut ui, 18);
    press(&mut ui, id, 10, 5);
    send(&mut ui, id, kind::SCROLL, 0);
    let track_top = 16.0;
    let track_len = 6.0 * 18.0 - 32.0;
    let (ty, th) = thumb_rect(&popup_scroll(&ui, id), track_top, track_len);
    let bar_x = raw_x(108.0);
    assert!(press(&mut ui, id, bar_x, raw_y(6, 18.0, ty + th / 2.0)));
    let to = (track_top + track_len - th / 2.0) as i32 as f32;
    let mv = UiEvent {
        kind: kind::MOUSE_MOVE,
        data1: bar_x,
        data2: raw_y(6, 18.0, to),
        target: Some(id),
        ..UiEvent::default()
    };
    assert!(ui.send_event(id, &mv));
    let s = popup_scroll(&ui, id);
    assert!(s.scroll_y() > 0 && s.position_ratio() > 0.5);
    let up = UiEvent {
        kind: kind::MOUSE_UP,
        target: Some(id),
        ..UiEvent::default()
    };
    assert!(ui.send_event(id, &up));
    assert!(menu(&ui, id).is_open());
}

#[test]
fn the_arrow_cap_flips_between_closed_and_open() {
    let mut ui = root(800.0, 600.0);
    let mut m = Menu::default();
    m.items = channel_items();
    m.arrow_cap_closed_sprite = 111;
    m.arrow_cap_open_sprite = 222;
    let id = menu_in(&mut ui, m, 300.0, 300.0, 80.0, 18.0);
    assert_eq!(menu(&ui, id).current_arrow_cap_sprite(), 111);
    press(&mut ui, id, 10, 5);
    assert_eq!(menu(&ui, id).current_arrow_cap_sprite(), 222);
    press(&mut ui, id, 10, -1);
    assert_eq!(menu(&ui, id).current_arrow_cap_sprite(), 111);
    assert_eq!(Menu::default().current_arrow_cap_sprite(), 0);
}

#[test]
fn a_downward_popup_sits_below_the_button() {
    let mut ui = root(800.0, 600.0);
    let mut m = Menu::default();
    m.items = channel_items();
    m.selected = Some(SAY);
    m.open_upward = false;
    let id = menu_in(&mut ui, m, 300.0, 100.0, 80.0, 18.0);
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    // Above the button is no longer the popup: it toggles closed.
    press(&mut ui, id, 10, -76);
    assert!(picked(&mut ui).is_empty());
    press(&mut ui, id, 10, 5);
    ui.drain_output();
    let ly = (18.0 + 2.0 * 17.0 + 8.5 + BORDER) as i32;
    press(&mut ui, id, 10, ly);
    assert_eq!(picked(&mut ui), vec![SAY]);
    assert!(Menu::default().open_upward);
    assert_eq!(
        (
            Menu::default().text_indent,
            Menu::default().button_text_indent
        ),
        (19.0, 20.0)
    );
}

fn content_menu(ui: &mut UiRoot, count: usize, size_to_content: bool) -> ElementId {
    let mut m = Menu::default();
    m.scrollable = true;
    m.open_upward = false;
    m.rows_per_column = 6;
    m.row_height = 18.0;
    m.column_width = 100.0;
    m.popup_size_to_content = size_to_content;
    m.items = (0..count)
        .map(|i| MenuItem::new(format!("Item {i}"), i as u64))
        .collect();
    menu_in(ui, m, 300.0, 100.0, 120.0, 18.0)
}

#[test]
fn size_to_content_fits_the_popup_to_the_items() {
    let mut ui = root(800.0, 600.0);
    let a = content_menu(&mut ui, 3, true);
    let b = content_menu(&mut ui, 3, false);
    assert_eq!(menu(&ui, a).popup_outer_height(), 3.0 * 18.0 + 10.0);
    assert_eq!(menu(&ui, b).popup_outer_height(), 6.0 * 18.0 + 10.0);

    let c = content_menu(&mut ui, 9, true);
    assert_eq!(menu(&ui, c).popup_outer_height(), 9.0 * 18.0 + 10.0);
    press(&mut ui, c, 10, 5);
    assert!(menu(&ui, c).is_open());
    ui.drain_output();
    let ly = 18.0 + 5.0 + 8.0 * 18.0 + 9.0;
    press(&mut ui, c, 10, ly as i32);
    assert_eq!(picked(&mut ui), vec![8]);
    assert!(!menu(&ui, c).is_open());
}

#[test]
fn a_hidden_when_disabled_popup_scrollbar_takes_no_room_or_input() {
    let mut ui = root(800.0, 600.0);
    let id = content_menu(&mut ui, 3, true);
    ui.widget_mut::<Menu>(id)
        .unwrap()
        .popup_scrollbar_hide_when_disabled = true;
    assert_eq!(menu(&ui, id).popup_outer_width(), 100.0 + 10.0);
    press(&mut ui, id, 10, 5);
    send(&mut ui, id, kind::SCROLL, 0);
    assert!(!popup_scroll(&ui, id).has_overflow());
    assert!(!menu(&ui, id).popup_scrollbar_visible());
    press(
        &mut ui,
        id,
        (5.0 + 100.0 + 8.0) as i32,
        (18.0 + 5.0 + 9.0) as i32,
    );
    assert_eq!(popup_scroll(&ui, id).scroll_y(), 0);
    assert!(!menu(&ui, id).is_open());

    let full = content_menu(&mut ui, 18, false);
    ui.widget_mut::<Menu>(full)
        .unwrap()
        .popup_scrollbar_hide_when_disabled = true;
    press(&mut ui, full, 10, 5);
    send(&mut ui, full, kind::SCROLL, 0);
    assert!(menu(&ui, full).popup_scrollbar_visible());
    assert_eq!(menu(&ui, full).popup_outer_width(), 100.0 + 16.0 + 10.0);
}

#[test]
fn an_empty_menu_never_opens() {
    let mut ui = root(800.0, 600.0);
    let id = content_menu(&mut ui, 0, true);
    assert!(press(&mut ui, id, 10, 5));
    assert!(!menu(&ui, id).is_open());
}

#[test]
fn the_face_flicks_pressed_and_is_not_latched_while_open() {
    let mut ui = root(800.0, 600.0);
    let id = make_menu(&mut ui);
    {
        let m = ui.widget_mut::<Menu>(id).unwrap();
        m.normal_sprite = 10;
        m.pressed_sprite = 20;
    }
    assert_eq!(menu(&ui, id).current_face_sprite(), 10);
    press(&mut ui, id, 5, 5);
    assert!(menu(&ui, id).is_open());
    assert_eq!(menu(&ui, id).current_face_sprite(), 20);
    send(&mut ui, id, kind::MOUSE_UP, 0);
    assert!(menu(&ui, id).is_open());
    assert_eq!(menu(&ui, id).current_face_sprite(), 10);
    press(&mut ui, id, 5, 5);
    assert!(!menu(&ui, id).is_open());
    assert_eq!(menu(&ui, id).current_face_sprite(), 20);
    send(&mut ui, id, kind::MOUSE_UP, 0);
    assert_eq!(menu(&ui, id).current_face_sprite(), 10);
}

// ── Popup routing through the root ──────────────────────────────────────

fn overlapping_tree(ui: &mut UiRoot) -> (ElementId, ElementId, ElementId) {
    let r = ui.root();
    let panel = ui.add(r, Element::at(0.0, 0.0, 300.0, 300.0), Panel::default());
    let mut m = Menu::default();
    m.open_upward = false;
    m.items = vec![MenuItem::new("A", 1), MenuItem::new("B", 2)];
    let mut e = Element::at(10.0, 20.0, 46.0, 20.0);
    Menu::configure(&mut e);
    let menu_id = ui.add(panel, e, m);
    let stealer = ui.add(
        panel,
        Element::at(0.0, 44.0, 250.0, 30.0),
        ClickRecorder {
            handles_click: true,
            ..ClickRecorder::default()
        },
    );
    (panel, menu_id, stealer)
}

fn click(ui: &mut UiRoot, x: i32, y: i32) {
    ui.on_mouse_down(MouseButton::Left, x, y, 0);
    ui.on_mouse_up(MouseButton::Left, x, y, 0);
}

fn stealer_clicks(ui: &UiRoot, id: ElementId) -> u32 {
    ui.widget::<ClickRecorder>(id).unwrap().clicks
}

#[test]
fn an_open_popup_wins_over_an_overlapping_front_sibling() {
    let mut ui = root(800.0, 600.0);
    let (_, m, stealer) = overlapping_tree(&mut ui);
    click(&mut ui, 30, 50);
    assert_eq!(stealer_clicks(&ui, stealer), 1);
    click(&mut ui, 30, 30);
    assert!(menu(&ui, m).is_open());
    ui.drain_output();
    click(&mut ui, 30, 50);
    assert_eq!(picked(&mut ui), vec![1]);
    assert!(!menu(&ui, m).is_open());
    assert_eq!(stealer_clicks(&ui, stealer), 1);
}

#[test]
fn a_press_outside_the_popup_dismisses_and_is_swallowed() {
    let mut ui = root(800.0, 600.0);
    let (_, m, stealer) = overlapping_tree(&mut ui);
    click(&mut ui, 30, 30);
    assert!(menu(&ui, m).is_open());
    ui.drain_output();
    click(&mut ui, 230, 60);
    assert!(!menu(&ui, m).is_open());
    assert!(picked(&mut ui).is_empty());
    assert_eq!(stealer_clicks(&ui, stealer), 0);
    click(&mut ui, 230, 60);
    assert_eq!(stealer_clicks(&ui, stealer), 1);
    // Reopening still routes item clicks.
    click(&mut ui, 30, 30);
    assert!(menu(&ui, m).is_open());
    ui.drain_output();
    click(&mut ui, 30, 50);
    assert_eq!(picked(&mut ui), vec![1]);
}

// ── Meter ───────────────────────────────────────────────────────────────

#[test]
fn fill_rects_clamp_the_share() {
    assert_eq!(compute_fill_rect(0.5, 200.0, 12.0), (0.0, 0.0, 100.0, 12.0));
    for (pct, w) in [(-1.0, 0.0), (2.0, 200.0), (0.0, 0.0), (1.0, 200.0)] {
        assert_eq!(compute_fill_rect(pct, 200.0, 12.0).2, w);
    }
    assert_eq!(
        compute_v_fill_rect(0.5, 11.0, 58.0, true),
        (0.0, 29.0, 11.0, 29.0)
    );
    let (_, y, _, h) = compute_v_fill_rect(0.25, 11.0, 58.0, false);
    assert_eq!((y, h), (0.0, 14.5));
    assert_eq!(compute_v_fill_rect(-1.0, 11.0, 58.0, true).3, 0.0);
    assert_eq!(compute_v_fill_rect(2.0, 11.0, 58.0, true).3, 58.0);
}

fn overlay(sprite: u32, x: f32, w: f32) -> DetailOverlay {
    DetailOverlay {
        sprite,
        x,
        y: 0.0,
        w,
        h: 16.0,
        left_mode: 3,
        top_mode: 1,
        right_mode: 3,
        bottom_mode: 1,
        parent_w: 150.0,
        parent_h: 16.0,
    }
}

#[test]
fn detail_icons_keep_the_authored_rect_and_centre_on_wider_meters() {
    let sword = overlay(0x0600_7492, 32.0, 85.0);
    assert_eq!(
        compute_detail_overlay_rect(&sword, 150.0, 16.0),
        (32.0, 0.0, 85.0, 16.0)
    );
    for (x, w, meter_w, ex, ew) in [
        (66.0, 18.0, 300.0, 141.0, 18.0),
        (66.0, 18.0, 220.0, 101.0, 18.0),
        (25.0, 100.0, 300.0, 100.0, 100.0),
    ] {
        let r = compute_detail_overlay_rect(&overlay(0x0600_7490, x, w), meter_w, 16.0);
        assert_eq!(r, (ex, 0.0, ew, 16.0));
    }
    // An odd width loses one pixel, as retail's centring does.
    let (x, _, w, _) = compute_detail_overlay_rect(&sword, 300.0, 16.0);
    assert_eq!((x, w), (108.0, 84.0));
}

#[test]
fn a_stateful_meter_switches_fill_and_caption_by_state() {
    let mut ui = root(400.0, 400.0);
    let mut m = Meter::default();
    m.configure_state_fill(0x1000_0042, 0x0600_1354);
    m.configure_state_label(0x1000_0042, "Jump", ac_ui::widgets::MeterLabelAlign::Left);
    let r = ui.root();
    let id = ui.add(r, Element::at(0.0, 0.0, 100.0, 10.0), m);
    assert!(!ui.set_retail_state(id, 0x1000_0043));
    assert!(ui.set_retail_state(id, 0x1000_0042));
    let m = ui.widget::<Meter>(id).unwrap();
    assert_eq!(m.front_tile, 0x0600_1354);
    assert_eq!(m.active_state_label(), Some("Jump"));
    assert_eq!(m.active_retail_state_id(), 0x1000_0042);
}
