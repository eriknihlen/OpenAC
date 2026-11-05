//! The shared item cooldown sweep.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ItemCooldownUiControllerTests.cs`.
//! OpenAC's cells ask the controller for their step when drawn; here the
//! controller sets each cell's step every frame, so a cell added between
//! frames shows its step from the next one.

mod common;

use ac_core::items::{ClientObject, ClientObjectTable};
use ac_core::spells::{ActiveEnchantmentRecord, BUCKET_COOLDOWN, Spellbook};
use ac_ui::panels::item_cooldown::ItemCooldownController;
use ac_ui::root::UiRoot;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{ItemList, ItemSlot};
use common::{at, panel};
use std::cell::Cell;

const ITEM: u32 = 0x5001;

fn objects() -> ClientObjectTable {
    let mut o = ClientObject::new(ITEM);
    o.cooldown_id = Some(42);
    o.cooldown_duration = Some(30.0);
    let mut t = ClientObjectTable::default();
    t.add_or_update(o, &mut Vec::new());
    t
}

fn spellbook() -> Spellbook {
    let mut b = Spellbook::new(None);
    b.on_enchantment_added(ActiveEnchantmentRecord {
        bucket: BUCKET_COOLDOWN,
        start_time: 100.0,
        ..ActiveEnchantmentRecord::new(0x802A, 1, 30.0, 0)
    });
    b
}

fn sprites() -> Vec<u32> {
    (1..=10).map(|i| 0x0600_0000 + i).collect()
}

fn list(ui: &mut UiRoot, parent: ElementId) -> ElementId {
    let l = ItemList::create(ui, parent, Element::at(0.0, 0.0, 64.0, 32.0));
    let first = ui.widget::<ItemList>(l).unwrap().cells()[0];
    ui.with::<ItemSlot, _>(first, |s, _| s.set_item(ITEM, None, None, None));
    l
}

fn sprite(ui: &UiRoot, cell: ElementId) -> u32 {
    ui.widget::<ItemSlot>(cell)
        .unwrap()
        .active_cooldown_sprite()
}

#[test]
fn one_clock_drives_every_cell_of_the_shared_group_present_and_future() {
    let mut ui = UiRoot::new();
    let r = ui.root();
    let root = panel(&mut ui, r, at(0.0, 0.0, 200.0, 200.0));
    let l = list(&mut ui, root);
    let objects = objects();
    let mut book = spellbook();
    let s = sprites();
    let mut c = ItemCooldownController::bind(&mut ui, root, &s);
    c.tick(&mut ui, Some(&mut book), &objects, || 112.5);
    let first = ui.widget::<ItemList>(l).unwrap().cells()[0];
    assert_eq!(sprite(&ui, first), s[5]);

    // A cell added later takes the overlays and its step.
    let future = ui.insert(Element::at(0.0, 0.0, 32.0, 32.0), ItemSlot::default());
    ui.with::<ItemSlot, _>(future, |s, _| s.set_item(ITEM, None, None, None));
    ui.with::<ItemList, _>(l, |w, ui| w.add_item(ui, l, future));
    c.tick(&mut ui, Some(&mut book), &objects, || 112.5);
    assert_eq!(sprite(&ui, future), s[5]);

    c.tick(&mut ui, Some(&mut book), &objects, || 129.999);
    assert_eq!(sprite(&ui, first), s[0]);

    c.tick(&mut ui, Some(&mut book), &objects, || 130.0);
    assert_eq!(sprite(&ui, first), 0);
    assert_eq!(book.active_count(), 0, "the expired cooldown is dropped");
}

#[test]
fn a_hidden_ancestor_takes_a_list_out_of_the_sweep() {
    let mut ui = UiRoot::new();
    let r = ui.root();
    let root = panel(&mut ui, r, at(0.0, 0.0, 200.0, 200.0));
    let window = panel(&mut ui, root, at(0.0, 0.0, 200.0, 200.0));
    let l = list(&mut ui, window);
    let objects = objects();
    let mut book = spellbook();
    let s = sprites();
    let mut c = ItemCooldownController::bind(&mut ui, root, &s);
    c.tick(&mut ui, Some(&mut book), &objects, || 112.5);
    let first = ui.widget::<ItemList>(l).unwrap().cells()[0];
    assert_eq!(sprite(&ui, first), s[5]);
    ui.set_visible(window, false);
    c.tick(&mut ui, Some(&mut book), &objects, || 112.5);
    assert_eq!(sprite(&ui, first), 0);
    ui.set_visible(window, true);
    c.tick(&mut ui, Some(&mut book), &objects, || 112.5);
    assert_eq!(sprite(&ui, first), s[5]);
}

#[test]
fn no_cooldown_running_skips_the_clock_and_the_walk() {
    let mut ui = UiRoot::new();
    let r = ui.root();
    let root = panel(&mut ui, r, at(0.0, 0.0, 200.0, 200.0));
    let l = list(&mut ui, root);
    let objects = objects();
    let mut book = Spellbook::new(None);
    let mut c = ItemCooldownController::bind(&mut ui, root, &sprites());
    let reads = Cell::new(0);
    c.tick(&mut ui, Some(&mut book), &objects, || {
        reads.set(reads.get() + 1);
        100.0
    });
    assert_eq!(reads.get(), 0);
    let first = ui.widget::<ItemList>(l).unwrap().cells()[0];
    assert_eq!(sprite(&ui, first), 0);
}
