//! The searchable drop-down menu.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiMenuSearchTests.cs`.
//! OpenAC's `OnSelect` is [`ElementMessage::MenuSelected`];
//! its `BeforeOpen` is [`ElementMessage::MenuOpening`], answered here by
//! the test before the next event; `FilteredItemsForTest` is
//! [`Menu::display_indices`].

mod common;

use std::sync::Arc;

use ac_dat::objects::FontCharDesc;
use ac_ui::ElementId;
use ac_ui::MouseButton;
use ac_ui::draw::TextureRef;
use ac_ui::event::{Key, Modifiers, UiEvent, kind};
use ac_ui::font::{Atlas, UiFont};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::{Menu, MenuItem};
use common::root;

const ITEMS: [&str; 4] = [
    "Olthoi north route",
    "Olthoi south route",
    "North town",
    "Olthoi north cave",
];

/// OpenAC `PrepareSearchField`'s font: every glyph 8 pixels wide.
fn font() -> Arc<UiFont> {
    let glyphs = (0x20u16..0x7F).map(|c| FontCharDesc {
        unicode: c,
        offset_x: 0,
        offset_y: 0,
        width: 8,
        height: 8,
        horizontal_offset_before: 0,
        horizontal_offset_after: 0,
        vertical_offset_before: 0,
    });
    Arc::new(UiFont::new(
        0x4000_0001,
        Atlas {
            texture: TextureRef::Surface(0x0600_0001),
            width: 128,
            height: 128,
        },
        None,
        16.0,
        12.0,
        glyphs,
        0,
        0,
    ))
}

/// OpenAC `Create`: a searchable scrolling menu of four routes, two rows
/// high, opening below its button, opened by a click.
fn create() -> (UiRoot, ElementId) {
    let mut ui = root(800.0, 600.0);
    let mut m = Menu::default();
    m.font = Some(font());
    m.searchable = true;
    m.scrollable = true;
    m.open_upward = false;
    m.rows_per_column = 2;
    m.row_height = 18.0;
    m.items = ITEMS
        .iter()
        .enumerate()
        .map(|(i, l)| MenuItem::new(*l, i as u64))
        .collect();
    let mut e = Element::at(10.0, 20.0, 180.0, 20.0);
    Menu::configure(&mut e);
    let r = ui.root();
    let id = ui.add(r, e, m);
    click(&mut ui, 20, 30);
    (ui, id)
}

fn click(ui: &mut UiRoot, x: i32, y: i32) {
    ui.on_mouse_down(MouseButton::Left, x, y, 0);
    ui.on_mouse_up(MouseButton::Left, x, y, 0);
}

fn typed(ui: &mut UiRoot, text: &str) {
    for c in text.chars() {
        ui.on_char(c);
    }
}

fn key(ui: &mut UiRoot, k: Key) {
    ui.on_key_down(k, Modifiers::NONE);
    ui.on_key_up(k, Modifiers::NONE);
}

fn menu(ui: &UiRoot, id: ElementId) -> &Menu {
    ui.widget::<Menu>(id).unwrap()
}

fn shown(ui: &UiRoot, id: ElementId) -> Vec<usize> {
    menu(ui, id).display_indices()
}

/// The payloads picked since the last call.
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

#[test]
fn words_filter_case_insensitively_without_selecting_and_enter_picks() {
    // WordsFilterCaseInsensitivelyWithoutSelecting_EnterRetainsPayload.
    let (mut ui, id) = create();
    ui.with::<Menu, _>(id, |m, _| m.selected = Some(1));
    ui.drain_output();
    typed(&mut ui, "NORTH olthoi");
    assert_eq!(shown(&ui, id), [0, 3]);
    assert!(picked(&mut ui).is_empty());
    assert_eq!(menu(&ui, id).selected, Some(1));
    key(&mut ui, Key::Enter);
    assert_eq!(picked(&mut ui), [0]);
    assert!(!menu(&ui, id).is_open());
    assert_eq!(ui.keyboard_focus(), None);
}

#[test]
fn an_empty_result_picks_nothing_editing_restores_it_and_escape_dismisses() {
    // EmptyResultDoesNotSelect_EditingRestoresResults_EscapeDismisses.
    let (mut ui, id) = create();
    ui.drain_output();
    typed(&mut ui, "zz");
    assert!(shown(&ui, id).is_empty());
    key(&mut ui, Key::Enter);
    assert!(menu(&ui, id).is_open());
    key(&mut ui, Key::Backspace);
    key(&mut ui, Key::Backspace);
    assert_eq!(shown(&ui, id).len(), 4);
    key(&mut ui, Key::Escape);
    assert!(!menu(&ui, id).is_open());
    assert!(picked(&mut ui).is_empty());
}

#[test]
fn the_filter_resets_the_scroll_and_a_click_below_the_band_picks() {
    // FilterResetsScrollAndMouseSelectionUsesOffsetBelowSearchBand.
    let (mut ui, id) = create();
    ui.with::<Menu, _>(id, |m, _| {
        m.popup_scroll.set_extents(200, 36, false);
        m.popup_scroll.set_scroll_y(100);
    });
    typed(&mut ui, "south");
    assert_eq!(menu(&ui, id).popup_scroll.scroll_y(), 0);
    ui.drain_output();
    click(&mut ui, 25, 75);
    assert_eq!(picked(&mut ui), [1]);
}

#[test]
fn reopening_refreshes_the_entries_and_clears_the_query() {
    // ReopenRefreshesItemsAndClearsQuery: the owner answers the opening by
    // replacing the entries.
    let (mut ui, id) = create();
    typed(&mut ui, "south");
    key(&mut ui, Key::Escape);
    ui.drain_output();
    click(&mut ui, 20, 30);
    let asked = ui.drain_output().into_iter().any(|o| {
        matches!(
            o,
            UiOutput::Element {
                message: ElementMessage::MenuOpening,
                ..
            }
        )
    });
    assert!(asked);
    ui.with::<Menu, _>(id, |m, _| m.items = vec![MenuItem::new("New profile", 77)]);
    assert_eq!(shown(&ui, id), [0]);
    assert_eq!(menu(&ui, id).search_text(), "");
}

#[test]
fn the_arrows_skip_disabled_rows_and_scroll_before_the_pick() {
    // ArrowNavigationSkipsDisabledRowsAndScrollsBeforeCommit.
    let (mut ui, id) = create();
    ui.with::<Menu, _>(id, |m, _| m.items[1].enabled = false);
    ui.drain_output();
    key(&mut ui, Key::Down);
    key(&mut ui, Key::Down);
    assert!(picked(&mut ui).is_empty());
    assert!(menu(&ui, id).popup_scroll.scroll_y() > 0);
    key(&mut ui, Key::Enter);
    assert_eq!(picked(&mut ui), [2]);
}

#[test]
fn a_drag_in_the_search_band_selects_text_without_hovering_a_result() {
    // SearchDragSelectsTextAcrossRowsWithoutHoveringAResult: the drag from
    // the band's end back across the rows selects the query, so typing
    // replaces it.
    let (mut ui, id) = create();
    typed(&mut ui, "north");
    ui.on_mouse_down(MouseButton::Left, 65, 50, 0);
    ui.on_mouse_move(15, 100);
    ui.on_mouse_up(MouseButton::Left, 15, 100, 0);
    assert_eq!(menu(&ui, id).hovered_popup_index(), None);
    typed(&mut ui, "south");
    assert_eq!(shown(&ui, id), [1]);
}

#[test]
fn losing_the_capture_ends_a_search_drag() {
    // CaptureLossEndsSearchDrag: after the press in the band and a lost
    // capture, a later move selects nothing, so typing edits the query.
    let (mut ui, id) = create();
    typed(&mut ui, "north");
    let event = |k: i32, x: i32, y: i32| UiEvent {
        kind: k,
        data1: x,
        data2: y,
        target: Some(id),
        ..UiEvent::default()
    };
    ui.send_event(id, &event(kind::MOUSE_DOWN, 55, 30));
    ui.send_event(id, &event(kind::CAPTURE_CHANGED, 0, 0));
    ui.send_event(id, &event(kind::MOUSE_MOVE, 5, 60));
    key(&mut ui, Key::Backspace);
    typed(&mut ui, "h");
    assert_eq!(shown(&ui, id).len(), 3);
}

#[test]
fn a_menu_without_search_never_takes_the_keyboard() {
    // DefaultMenuDoesNotAcquireTextFocus.
    let mut ui = root(400.0, 400.0);
    let mut m = Menu::default();
    m.items = vec![MenuItem::new("A", 0)];
    let mut e = Element::at(0.0, 0.0, 100.0, 20.0);
    Menu::configure(&mut e);
    let r = ui.root();
    let id = ui.add(r, e, m);
    ui.on_mouse_down(MouseButton::Left, 10, 10, 0);
    assert!(!menu(&ui, id).searchable);
    assert_eq!(ui.keyboard_focus(), None);
    assert!(!ui.element(id).unwrap().is_edit_control);
}
