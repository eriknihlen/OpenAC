//! The main panel host: one main panel at a time, a restoring panel
//! brings back its predecessor, and every main panel shares the shown
//! one's place and size.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailPanelUiControllerTests.cs`.
//! OpenAC's visibility delegates are the root's windows here; its
//! `Moved`/`Resized` events are [`MainPanels::observe_geometry`], which the
//! window calls when a panel window moves or is sized.

mod common;

use ac_ui::panels::main_panels::MainPanels;
use ac_ui::panels::panel_id;
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::Panel;
use common::root;

/// A hidden window `name` at (`left`, `top`), 300 wide.
fn mount(ui: &mut UiRoot, name: &str, left: f32, top: f32, height: f32) {
    let r = ui.root();
    let mut e = Element::at(left, top, 300.0, height);
    e.max_width = 2000.0;
    e.max_height = 2000.0;
    let id = ui.add(r, e, Panel::default());
    ui.register_window(name, id, Some(id), 0).unwrap();
    ui.hide_window(name);
}

fn place(ui: &UiRoot, name: &str) -> (f32, f32, f32, f32) {
    let e = ui.element(ui.window(name).unwrap().frame).unwrap();
    (e.left, e.top, e.width, e.height)
}

fn visible(ui: &UiRoot, name: &str) -> bool {
    ui.is_window_visible(name)
}

/// Two windows, the second restoring its predecessor when asked.
fn two(first: &str, second: &str, restore: bool) -> (UiRoot, MainPanels) {
    let mut ui = root(1280.0, 720.0);
    mount(&mut ui, first, 10.0, 10.0, 200.0);
    mount(&mut ui, second, 10.0, 10.0, 200.0);
    let mut p = MainPanels::new();
    p.register(&mut ui, 1, first, false);
    p.register(&mut ui, 2, second, restore);
    (ui, p)
}

#[test]
fn showing_a_panel_hides_the_current_and_closing_leaves_none() {
    let (mut ui, mut p) = two("inventory", "helpful", false);
    assert!(p.set_visibility(&mut ui, 1, true));
    assert!(visible(&ui, "inventory"));
    assert_eq!(p.active(), Some(1));
    assert!(p.set_visibility(&mut ui, 2, true));
    assert!(!visible(&ui, "inventory") && visible(&ui, "helpful"));
    assert_eq!(p.active(), Some(2));
    assert!(!p.toggle(&mut ui, 2));
    assert!(!visible(&ui, "helpful"));
    assert_eq!(p.active(), None);
}

#[test]
fn a_restoring_panel_reopens_its_non_restoring_predecessor() {
    let (mut ui, mut p) = two("ordinary", "transient", true);
    p.set_visibility(&mut ui, 1, true);
    p.set_visibility(&mut ui, 2, true);
    assert!(!visible(&ui, "ordinary") && visible(&ui, "transient"));
    p.set_visibility(&mut ui, 2, false);
    assert!(visible(&ui, "ordinary") && !visible(&ui, "transient"));
    assert_eq!(p.active(), Some(1));
}

#[test]
fn a_window_shown_by_other_means_takes_the_same_exclusive_path() {
    let (mut ui, mut p) = two("first", "second", false);
    p.set_visibility(&mut ui, 1, true);
    ui.show_window("second");
    p.observe_visibility(&mut ui, "second", true);
    assert!(!visible(&ui, "first") && visible(&ui, "second"));
    assert_eq!(p.active(), Some(2));
}

#[test]
fn the_main_panels_share_one_place_across_moves_closes_and_switches() {
    let mut ui = root(1280.0, 720.0);
    mount(&mut ui, "inventory", 40.0, 60.0, 360.0);
    mount(&mut ui, "character", 540.0, 18.0, 500.0);
    mount(&mut ui, "spellbook", 18.0, 18.0, 420.0);
    let mut p = MainPanels::new();
    p.register(&mut ui, 7, "inventory", false);
    p.register(&mut ui, 11, "character", false);
    p.register(&mut ui, 13, "spellbook", false);

    p.set_visibility(&mut ui, 7, true);
    ui.move_window("inventory", 312.0, 147.0);
    p.observe_geometry(&mut ui, "inventory");
    ui.resize_window("inventory", 310.0, 540.0);
    p.observe_geometry(&mut ui, "inventory");
    for other in ["character", "spellbook"] {
        assert_eq!(place(&ui, other), (312.0, 147.0, 310.0, 540.0), "{other}");
    }

    p.set_visibility(&mut ui, 11, true);
    assert!(!visible(&ui, "inventory") && visible(&ui, "character"));
    assert_eq!(place(&ui, "character"), (312.0, 147.0, 310.0, 540.0));

    p.set_visibility(&mut ui, 11, false);
    p.set_visibility(&mut ui, 13, true);
    assert!(!visible(&ui, "character") && visible(&ui, "spellbook"));
    assert_eq!(place(&ui, "spellbook"), (312.0, 147.0, 310.0, 540.0));
}

#[test]
fn the_options_and_character_windows_share_the_exclusive_lifecycle() {
    let mut ui = root(1280.0, 720.0);
    mount(&mut ui, "character", 540.0, 18.0, 500.0);
    mount(&mut ui, "options", 150.0, 80.0, 500.0);
    let mut p = MainPanels::new();
    p.register(&mut ui, panel_id::CHARACTER, "character", false);
    p.register(&mut ui, panel_id::OPTIONS, "options", false);
    p.set_visibility(&mut ui, panel_id::CHARACTER, true);
    assert!(visible(&ui, "character") && !visible(&ui, "options"));
    assert_eq!(p.active(), Some(panel_id::CHARACTER));
    p.set_visibility(&mut ui, panel_id::OPTIONS, true);
    assert!(!visible(&ui, "character") && visible(&ui, "options"));
    assert_eq!(p.active(), Some(panel_id::OPTIONS));
}

#[test]
fn an_indicator_panel_shares_the_place_and_restores_the_previous_panel() {
    let mut ui = root(1280.0, 720.0);
    mount(&mut ui, "inventory", 40.0, 60.0, 500.0);
    mount(&mut ui, "character", 540.0, 18.0, 500.0);
    mount(&mut ui, "effects", 900.0, 30.0, 500.0);
    let mut p = MainPanels::new();
    p.register(&mut ui, 7, "inventory", false);
    p.register(&mut ui, 11, "character", false);
    p.register(&mut ui, 4, "effects", true);

    p.set_visibility(&mut ui, 7, true);
    ui.move_window("inventory", 280.0, 120.0);
    ui.resize_window("inventory", 310.0, 600.0);
    p.observe_geometry(&mut ui, "inventory");
    p.set_visibility(&mut ui, 4, true);
    assert_eq!(place(&ui, "effects"), (280.0, 120.0, 310.0, 600.0));
    assert!(!visible(&ui, "inventory") && visible(&ui, "effects"));

    p.set_visibility(&mut ui, 4, false);
    assert!(visible(&ui, "inventory"));
    assert_eq!(place(&ui, "inventory"), (280.0, 120.0, 310.0, 600.0));
    p.set_visibility(&mut ui, 11, true);
    assert_eq!(
        (place(&ui, "character").0, place(&ui, "character").1),
        (280.0, 120.0)
    );
}

fn states(ui: &UiRoot) -> (bool, bool) {
    (visible(ui, "ordinary"), visible(ui, "transient"))
}

#[test]
fn showing_through_toggle_or_set_visibility_is_the_same() {
    let (mut ui_a, mut a) = two("ordinary", "transient", true);
    a.set_visibility(&mut ui_a, 1, true);
    let result_a = a.toggle(&mut ui_a, 2);
    let (mut ui_b, mut b) = two("ordinary", "transient", true);
    b.set_visibility(&mut ui_b, 1, true);
    let result_b = b.set_visibility(&mut ui_b, 2, true);
    assert_eq!(result_a, result_b);
    assert_eq!(states(&ui_a), states(&ui_b));
    assert_eq!(a.active(), b.active());
}

#[test]
fn hiding_through_toggle_or_set_visibility_ends_in_the_same_state() {
    let (mut ui_a, mut a) = two("ordinary", "transient", true);
    a.set_visibility(&mut ui_a, 1, true);
    a.set_visibility(&mut ui_a, 2, true);
    let result_a = a.toggle(&mut ui_a, 2);
    let (mut ui_b, mut b) = two("ordinary", "transient", true);
    b.set_visibility(&mut ui_b, 1, true);
    b.set_visibility(&mut ui_b, 2, true);
    let result_b = b.set_visibility(&mut ui_b, 2, false);
    // The toggle answers whether the panel now shows; the direct call
    // answers whether the hide did something.
    assert!(!result_a);
    assert!(result_b);
    assert_eq!(states(&ui_a), states(&ui_b));
    assert_eq!(a.active(), b.active());
}
