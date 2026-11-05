//! A menu with [`Menu::fits_long_labels`] (every plugin markup `<menu>`)
//! widens its popup to its longest item, keeps the scrollbar beside the
//! text, shifts left at an edge, and shortens a long button label. With no
//! font the menu measures 7 pixels per character.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiMenuFitItemsTests.cs`.
//! OpenAC's `OnSelect` callback is [`ElementMessage::MenuSelected`] (the
//! payload here is the item's index); its `HitTest` is
//! [`UiRoot::hit_test`].

mod common;

use ac_ui::ElementId;
use ac_ui::MouseButton;
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::menu::PLAIN_PADDING;
use ac_ui::widgets::{Menu, MenuItem};
use common::*;

const BORDER: f32 = 5.0;
const INDENT: f32 = 6.0;

fn long_label() -> String {
    "x".repeat(40) // 280 pixels
}

fn items(labels: &[&str]) -> Vec<MenuItem> {
    labels
        .iter()
        .enumerate()
        .map(|(i, l)| MenuItem::new(*l, i as u64))
        .collect()
}

fn make_menu(width: f32, labels: &[&str]) -> Menu {
    let mut m = Menu::default();
    m.column_width = width;
    m.fits_long_labels = true;
    m.retail_button_art = false;
    m.open_upward = false;
    m.scrollable = true;
    m.popup_scrollbar_hide_when_disabled = true;
    m.rows_per_column = 4;
    m.row_height = 18.0;
    m.text_indent = INDENT;
    m.items = items(labels);
    m
}

fn add_menu(ui: &mut UiRoot, parent: ElementId, m: Menu, x: f32, y: f32, w: f32) -> ElementId {
    let mut e = Element::at(x, y, w, 20.0);
    Menu::configure(&mut e);
    ui.add(parent, e, m)
}

fn click(ui: &mut UiRoot, x: i32, y: i32) {
    ui.on_mouse_down(MouseButton::Left, x, y, 0);
    ui.on_mouse_up(MouseButton::Left, x, y, 0);
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

/// Where the longest label's text ends inside the popup.
fn long_text_right() -> f32 {
    BORDER + PLAIN_PADDING + long_label().len() as f32 * 7.0
}

#[test]
fn a_menu_keeps_its_authored_widths_unless_it_opts_in() {
    assert!(!Menu::default().fits_long_labels);
}

#[test]
fn a_long_item_widens_the_popup_to_fit_it() {
    let mut ui = root(800.0, 600.0);
    let long = long_label();
    let r = ui.root();
    let id = add_menu(
        &mut ui,
        r,
        make_menu(100.0, &["Short", &long]),
        0.0,
        0.0,
        100.0,
    );

    click(&mut ui, 10, 10);

    assert!(menu(&ui, id).is_open());
    let outer = menu(&ui, id).popup_outer_width();
    assert!(
        outer >= long_text_right() + PLAIN_PADDING + BORDER,
        "popup {outer} is narrower than the text ({})",
        long_text_right()
    );
}

#[test]
fn the_scrollbar_sits_beside_the_longest_item_not_over_it() {
    let mut ui = root(800.0, 600.0);
    let long = long_label();
    let mut labels: Vec<String> = (0..10).map(|i| format!("Item {i}")).collect();
    labels.push(long);
    let labels: Vec<&str> = labels.iter().map(String::as_str).collect();
    let r = ui.root();
    let id = add_menu(&mut ui, r, make_menu(100.0, &labels), 0.0, 0.0, 100.0);

    click(&mut ui, 10, 10);

    let m = menu(&ui, id);
    assert!(m.popup_scrollbar_visible());
    assert!(
        m.popup_scrollbar_left() >= long_text_right(),
        "scrollbar at {} overlaps text ending at {}",
        m.popup_scrollbar_left(),
        long_text_right()
    );
    assert_eq!(
        m.popup_scrollbar_left() + m.scrollbar_width + BORDER,
        m.popup_outer_width()
    );
}

#[test]
fn short_items_keep_the_popup_at_the_button_width() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let id = add_menu(&mut ui, r, make_menu(120.0, &["A", "B"]), 0.0, 0.0, 120.0);

    click(&mut ui, 10, 10);

    assert_eq!(menu(&ui, id).popup_outer_width(), 120.0 + 2.0 * BORDER);
    assert!(menu(&ui, id).popup_outer_width() >= 120.0);
}

#[test]
fn a_widened_popup_shifts_left_at_the_window_edge_and_its_rows_still_pick() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let win = panel(&mut ui, r, window(100.0, 50.0, 500.0, 300.0));
    let long = long_label();
    // Screen x 480: a 296-pixel popup would end at 776.
    let id = add_menu(
        &mut ui,
        win,
        make_menu(100.0, &["Short", &long]),
        380.0,
        10.0,
        100.0,
    );

    click(&mut ui, 490, 70);

    assert!(menu(&ui, id).is_open());
    let (sx, _) = ui.screen_position(id);
    let m = menu(&ui, id);
    assert_eq!(sx + m.popup_left() + m.popup_outer_width(), 600.0);
    ui.drain_output();
    // Second row, left of the button: only reachable because the popup moved.
    click(&mut ui, 340, 60 + 20 + BORDER as i32 + 18 + 9);
    assert_eq!(picked(&mut ui), vec![1]);
}

#[test]
fn an_open_widened_menu_does_not_own_the_button_row_beside_the_button() {
    let mut ui = root(800.0, 600.0);
    let long = long_label();
    let r = ui.root();
    let id = add_menu(
        &mut ui,
        r,
        make_menu(100.0, &["Short", &long]),
        0.0,
        0.0,
        100.0,
    );

    click(&mut ui, 10, 10);

    assert!(menu(&ui, id).is_open());
    assert!(menu(&ui, id).popup_outer_width() > 200.0);
    assert_eq!(ui.hit_test(id, 50.0, 10.0), Some(id)); // the button
    assert_eq!(ui.hit_test(id, 150.0, 10.0), None); // beside it, over the popup's width
    assert_eq!(ui.hit_test(id, 150.0, 30.0), Some(id)); // the popup under it
}

#[test]
fn items_replaced_while_open_remeasure_the_popup() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let id = add_menu(&mut ui, r, make_menu(100.0, &["A", "B"]), 0.0, 0.0, 100.0);
    click(&mut ui, 10, 10);
    assert_eq!(menu(&ui, id).popup_outer_width(), 100.0 + 2.0 * BORDER);

    ui.widget_mut::<Menu>(id)
        .unwrap()
        .set_items(vec![MenuItem::new(long_label(), 0)]);

    let outer = menu(&ui, id).popup_outer_width();
    assert!(
        outer >= long_text_right() + PLAIN_PADDING + BORDER,
        "popup {outer} did not grow for the new item ({})",
        long_text_right()
    );
}

#[test]
fn a_long_caption_is_not_cut_inside_a_character() {
    let label = "\u{1F600}".repeat(20);
    let mut m = make_menu(80.0, &[&label]);
    m.button_label.clone_from(&label);

    let shown = m.button_caption(80.0).to_owned();

    assert!(shown.ends_with('\u{2026}'), "'{shown}'");
    let kept = shown.trim_end_matches('\u{2026}');
    assert!(!kept.is_empty(), "cut to nothing");
    assert!(kept.chars().all(|c| c == '\u{1F600}'), "'{kept}'");
}

#[test]
fn a_long_selected_label_ends_in_an_ellipsis_inside_the_button() {
    let long = long_label();
    let mut m = make_menu(80.0, &[&long]);
    m.button_label.clone_from(&long);

    let shown = m.button_caption(80.0).to_owned();

    assert!(shown.ends_with('\u{2026}'), "'{shown}'");
    assert!(
        PLAIN_PADDING + shown.chars().count() as f32 * 7.0 <= 80.0 - 18.0,
        "'{shown}' is still too wide"
    );
}
