//! The toolbar and the stack-split quantity.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ToolbarControllerTests.cs`,
//! `ToolbarInputControllerTests.cs` and `SelectedObjectControllerTests.cs`
//! (split quantity cases), on a synthetic layout shaped like the retail
//! toolbar 0x21000016. OpenAC's `sendAddShortcut`/`sendRemoveShortcut`,
//! `useItem`, `selectItem` and `toggleCombat` delegates are
//! [`PanelOutput`]s here; its target mode is the [`ItemsView`]'s
//! `target_source`.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObject, EquipMask, ItemType, ItemUseable, ShortcutEntry};
use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue};
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::draw::TextureRef;
use ac_ui::element_info::{ElementInfo, ImageMedia, StateInfo};
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::items::ItemsView;
use ac_ui::panels::toolbar::{
    self, StackSplitController, ToolbarController, ToolbarKey, ToolbarWindowController, quick_slot,
};
use ac_ui::panels::{PanelOutput, panel_id};
use ac_ui::widgets::{Button, Field, ItemList, ItemSlot, Scrollbar, ShortcutDigits};
use ac_ui::{ElementId, MouseButton};
use common::items::*;
use common::*;

const PANEL_BUTTONS: [(u32, u32); 7] = [
    (0x1000_0197, 12),
    (0x1000_0198, 13),
    (0x1000_0199, panel_id::CHARACTER),
    (0x1000_055A, 25),
    (0x1000_019A, 16),
    (0x1000_019B, 10),
    (toolbar::INVENTORY_BUTTON_ID, panel_id::INVENTORY),
];
const CHARACTER_BUTTON: u32 = 0x1000_0199;

fn button_info(id: u32, panel: Option<u32>) -> ElementInfo {
    let mut info = ElementInfo {
        id,
        kind: 1,
        width: 32.0,
        height: 32.0,
        default_state_name: "Normal".to_owned(),
        ..ElementInfo::default()
    };
    for (name, file) in [("Normal", 1), ("Highlight", 2), ("Ghosted", 3)] {
        info.state_media
            .insert(name.to_owned(), ImageMedia { file, draw_mode: 1 });
    }
    if let Some(p) = panel {
        let mut s = StateInfo::named(DIRECT_STATE_ID, "");
        s.properties
            .set_value(toolbar::PANEL_ID_ATTRIBUTE, PropertyValue::Enum(p));
        info.set_state(DIRECT_STATE_ID, s);
    }
    info
}

struct Bar {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    icons: TestIcons,
}

/// The toolbar: eighteen slots in a row at y = 0 (x = 40 * i), the
/// buttons below, the split entry and slider.
fn bar(shortcuts: &[ShortcutEntry], digits: Option<Arc<ShortcutDigits>>) -> Bar {
    let mut host = UiHost::new();
    host.root.set_size(1000.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 900.0, 200.0));
    let mut ids = HashMap::new();
    for (i, id) in toolbar::SLOT_IDS.iter().enumerate() {
        ids.insert(*id, list(ui, root, i as f32 * 40.0, 0.0, 32.0, 32.0));
    }
    let mut x = 0.0;
    let mut add_button = |ui: &mut ac_ui::root::UiRoot, id: u32, panel: Option<u32>| {
        let info = Arc::new(button_info(id, panel));
        let b = ui.add(
            root,
            at(x, 50.0, 32.0, 32.0),
            Button::new(info, None, Vec::new(), 0.0),
        );
        x += 40.0;
        ids.insert(id, b);
    };
    for id in toolbar::COMBAT_INDICATOR_IDS {
        add_button(ui, id, None);
    }
    for (id, p) in PANEL_BUTTONS {
        add_button(ui, id, Some(p));
    }
    add_button(ui, toolbar::USE_BUTTON_ID, None);
    add_button(ui, toolbar::EXAMINE_BUTTON_ID, None);
    let mut e = at(0.0, 100.0, 60.0, 16.0);
    Field::configure(&mut e);
    ids.insert(
        toolbar::STACK_SIZE_ENTRY_ID,
        ui.add(root, e, Field::default()),
    );
    ids.insert(
        toolbar::STACK_SIZE_SLIDER_ID,
        ui.add(root, at(0.0, 120.0, 100.0, 12.0), Scrollbar::default()),
    );
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window("toolbar", root, None, 0).unwrap();
    let mut toolbar = ToolbarController::bind(ui, &layout, digits, None);
    toolbar.load_shortcuts(shortcuts.iter().copied());
    let split = StackSplitController::bind(ui, &layout);
    host.attach_controller(
        "toolbar",
        Box::new(ToolbarWindowController {
            toolbar,
            split,
            selected: None,
        }),
    )
    .unwrap();
    Bar {
        host,
        ids,
        icons: TestIcons::default(),
    }
}

fn entry(index: i32, object_id: u32) -> ShortcutEntry {
    ShortcutEntry {
        index,
        object_id,
        spell_id: 0,
    }
}

impl Bar {
    fn update(&mut self, view: &ItemsView<'_>) {
        let icons = &mut self.icons;
        self.host
            .with_controller::<ToolbarWindowController, _>("toolbar", |c, ui| {
                c.update(ui, view, icons);
            })
            .unwrap();
    }

    fn with<R>(
        &mut self,
        f: impl FnOnce(&mut ToolbarWindowController, &mut ac_ui::root::UiRoot) -> R,
    ) -> R {
        self.host
            .with_controller::<ToolbarWindowController, _>("toolbar", f)
            .unwrap()
    }

    fn cell(&self, slot: usize) -> ElementId {
        let l = self.ids[&toolbar::SLOT_IDS[slot]];
        self.host
            .root
            .widget::<ItemList>(l)
            .unwrap()
            .item(0)
            .unwrap()
    }

    fn slot(&self, slot: usize) -> &ItemSlot {
        self.host.root.widget::<ItemSlot>(self.cell(slot)).unwrap()
    }

    fn fire(&mut self, slot: usize, k: i32, p: Option<ItemDragPayload>) {
        let c = self.cell(slot);
        self.host.root.fire_event(k, c, p.map(DragPayload::new));
        self.host.pump();
    }

    fn click(&mut self, id: u32) {
        let b = self.ids[&id];
        self.host.root.fire_event(kind::CLICK, b, None);
        self.host.pump();
    }

    fn outputs(&mut self) -> Vec<PanelOutput> {
        panel_outputs(&mut self.host)
    }

    fn button(&self, id: u32) -> &Button {
        self.host.root.widget::<Button>(self.ids[&id]).unwrap()
    }

    fn enabled(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().enabled
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }
}

fn item(o: &mut Objects, guid: u32) {
    let mut c = ClientObject::new(guid);
    c.weenie_class_id = 1;
    c.icon_id = 0x0600_0000 | (guid & 0xFFFF);
    o.add(c);
}

#[test]
fn shortcuts_show_in_their_slots_with_icons_captions_and_structure() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    item(&mut o, 0x5002);
    o.get_mut(0x5001).name = "Bread Loaf".into();
    o.get_mut(0x5002).structure = 5;
    o.get_mut(0x5002).max_structure = 10;
    let mut b = bar(&[entry(0, 0x5001), entry(10, 0x5002)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(0).object_id, 0x5001);
    assert_eq!(b.slot(0).icon, Some(TextureRef::Runtime(0x0600_5001)));
    assert_eq!(b.slot(0).tooltip.as_deref(), Some("Bread Loaf"));
    assert_eq!(b.slot(0).shortcut, Some(entry(0, 0x5001)));
    assert_eq!(b.slot(10).object_id, 0x5002);
    assert!((b.slot(10).structure_fill - 0.5).abs() < 1e-6);
    assert_eq!(b.slot(1).object_id, 0);
    // A full item hides the meter; a live update repaints it.
    o.get_mut(0x5002).structure = 10;
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(b.slot(10).structure_fill < 0.0);
    // A removed shortcut clears the cell and its meter.
    b.with(|c, _| c.toolbar.load_shortcuts([entry(0, 0x5001)]));
    o.get_mut(0x5002).structure = 5;
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(10).object_id, 0);
    assert!(b.slot(10).structure_fill < 0.0);
}

#[test]
fn a_shortcut_to_the_player_shows_the_pack_icon_and_a_late_item_binds_when_it_arrives() {
    let mut o = Objects::new();
    o.player(102, 7);
    let mut b = bar(&[entry(0, PLAYER), entry(2, 0x5001)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        b.slot(0).icon,
        Some(TextureRef::Runtime(
            ac_ui::panels::inventory::PLAYER_PACK_BASE_ICON
        ))
    );
    assert_eq!(b.slot(2).object_id, 0, "unknown until described");
    item(&mut o, 0x5001);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(2).object_id, 0x5001);
}

#[test]
fn a_press_selects_without_use_and_a_double_click_uses_once() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(0, 0x5001)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    b.outputs();
    b.host.tick(0.0, 1_000, None);
    b.host.mouse_down(MouseButton::Left, 10, 10, 0);
    b.host.mouse_up(MouseButton::Left, 10, 10, 0);
    assert!(b.slot(0).selected);
    assert_dbg_eq!(b.outputs(), vec![PanelOutput::SelectObject(0x5001)]);
    b.host.tick(0.0, 1_300, None);
    b.host.mouse_down(MouseButton::Left, 10, 10, 0);
    b.host.mouse_up(MouseButton::Left, 10, 10, 0);
    assert_eq!(uses(&b.outputs()), vec![0x5001]);
    // The runtime's selection clearing clears the square.
    b.update(&ItemsView {
        selected: Some(0x5001),
        ..ItemsView::new(&o.t, PLAYER)
    });
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(!b.slot(0).selected);
}

#[test]
fn a_right_click_selects_and_examines() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(0, 0x5001)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    b.outputs();
    b.fire(0, kind::RIGHT_CLICK, None);
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::SelectObject(0x5001),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(0x5001)
            ))),
        ]
    );
}

#[test]
fn panel_buttons_carry_their_dat_panel_ids_ghost_when_absent_and_highlight_when_open() {
    let o = Objects::new();
    let mut b = bar(&[], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    b.with(|c, ui| {
        c.toolbar
            .bind_panel_buttons(ui, |p| p == panel_id::INVENTORY || p == panel_id::CHARACTER);
    });
    b.outputs();
    b.click(toolbar::INVENTORY_BUTTON_ID);
    b.click(CHARACTER_BUTTON);
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::TogglePanel(panel_id::INVENTORY),
            PanelOutput::TogglePanel(panel_id::CHARACTER),
        ]
    );
    for (id, p) in PANEL_BUTTONS {
        assert_eq!(
            b.enabled(id),
            p == panel_id::INVENTORY || p == panel_id::CHARACTER,
            "{id:#x}"
        );
    }
    b.with(|c, ui| {
        c.toolbar.set_panel_open(ui, panel_id::INVENTORY, true);
        c.toolbar.set_panel_open(ui, panel_id::CHARACTER, true);
    });
    assert_eq!(
        b.button(toolbar::INVENTORY_BUTTON_ID).active_state(),
        "Highlight"
    );
    assert_eq!(b.button(CHARACTER_BUTTON).active_state(), "Highlight");
    b.with(|c, ui| {
        c.toolbar.set_panel_open(ui, panel_id::INVENTORY, false);
        c.toolbar.set_panel_open(ui, panel_id::CHARACTER, false);
    });
    assert_eq!(
        b.button(toolbar::INVENTORY_BUTTON_ID).active_state(),
        "Normal"
    );
}

#[test]
fn in_a_target_mode_the_backpack_button_and_a_slot_press_name_targets() {
    let mut o = Objects::new();
    o.player(102, 7);
    item(&mut o, 0x5004);
    let mut b = bar(&[entry(0, 0x5004)], None);
    let view = ItemsView {
        target_mode: true,
        target_source: Some(0x5003),
        ..ItemsView::new(&o.t, PLAYER)
    };
    b.update(&view);
    b.with(|c, ui| c.toolbar.bind_panel_buttons(ui, |_| true));
    b.outputs();
    b.click(toolbar::INVENTORY_BUTTON_ID);
    b.fire(0, kind::MOUSE_DOWN, None);
    b.fire(0, kind::CLICK, None);
    b.fire(0, kind::DOUBLE_CLICK, None);
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::OfferTarget(
                ObjectRef::Player
            ))),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::OfferTarget(
                ObjectRef::Guid(0x5004)
            ))),
        ]
    );
}

#[test]
fn an_item_dragged_off_the_ground_onto_the_backpack_button_is_put_away() {
    // GitHub #45: an item lying on the ground could not be dragged onto the
    // backpack button; the drag never started.
    const LOOT: u32 = 0x7000_5001;
    let mut o = Objects::new();
    o.player(102, 7);
    let mut loot = ClientObject::new(LOOT);
    loot.item_type = ItemType::MISC;
    loot.icon_id = 0x0600_0777;
    o.add(loot);
    let mut b = bar(&[], None);
    let view = ItemsView::new(&o.t, PLAYER);
    b.update(&view);
    b.outputs();
    let root = &mut b.host.root;
    root.on_mouse_down(MouseButton::Left, 600, 500, 0);
    root.on_mouse_move(620, 520);
    let (payload, ghost) = ac_ui::world_drag::lift(
        LOOT,
        PLAYER,
        CombatMode::NON_COMBAT,
        &o.t,
        None,
        &mut b.icons,
    )
    .expect("the item on the ground lifts");
    assert!(b.host.root.begin_world_drag(payload, ghost));
    let button = b.ids[&toolbar::INVENTORY_BUTTON_ID];
    let (x, y) = b.host.root.screen_position(button);
    let (x, y) = (x as i32 + 4, y as i32 + 4);
    b.host.root.on_mouse_move(x, y);
    b.host.root.on_mouse_up(MouseButton::Left, x, y, 0);
    b.host.pump();
    b.update(&view);
    assert_dbg_eq!(
        sessions(&b.outputs()),
        vec![SessionCommand::Item(ItemCommand::DropOnBackpack(
            ObjectRef::Guid(LOOT)
        ))]
    );
}

#[test]
fn a_drop_on_the_backpack_button_hands_the_session_the_item_to_put_away() {
    // ToolbarController.HandleInventoryButtonDrop: the owned item goes into
    // the preferred pack, loot straight into the main pack; the session
    // tells them apart (`ItemCommand::DropOnBackpack`).
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0x5001, PLAYER, 0, ItemType::MISC);
    let mut loot = ClientObject::new(0x7000_5001);
    loot.name = "Loot".into();
    o.add(loot);
    let mut b = bar(&[], None);
    let view = ItemsView::new(&o.t, PLAYER);
    b.update(&view);
    let button = b.ids[&toolbar::INVENTORY_BUTTON_ID];
    let p = ItemDragPayload::new(0x5001, ItemDragSource::Inventory, 12);
    b.host
        .root
        .fire_event(kind::DRAG_ENTER, button, Some(DragPayload::new(p.clone())));
    assert_eq!(
        b.button(toolbar::INVENTORY_BUTTON_ID)
            .item_drag_acceptance(),
        DragAcceptance::Accept
    );
    assert_eq!(
        b.button(toolbar::INVENTORY_BUTTON_ID)
            .item_drag_accept_sprite,
        0x0600_11F7
    );
    b.outputs();
    b.host
        .root
        .fire_event(kind::DROP_RELEASED, button, Some(DragPayload::new(p)));
    b.host.pump();
    let ground = ItemDragPayload::new(0x7000_5001, ItemDragSource::Ground, 0);
    b.host
        .root
        .fire_event(kind::DROP_RELEASED, button, Some(DragPayload::new(ground)));
    b.host.pump();
    b.update(&view);
    assert_dbg_eq!(
        sessions(&b.outputs()),
        vec![
            SessionCommand::Item(ItemCommand::DropOnBackpack(ObjectRef::Guid(0x5001))),
            SessionCommand::Item(ItemCommand::DropOnBackpack(ObjectRef::Guid(0x7000_5001))),
        ]
    );
    // An alias (a shortcut, or a salvage, vendor or house window cell) stays neutral and
    // moves nothing.
    for source in [
        ItemDragSource::ShortcutBar,
        ItemDragSource::Salvage,
        ItemDragSource::Vendor,
    ] {
        let alias = ItemDragPayload::new(0x5001, source, 2);
        b.host.root.fire_event(
            kind::DRAG_ENTER,
            button,
            Some(DragPayload::new(alias.clone())),
        );
        assert_eq!(
            b.button(toolbar::INVENTORY_BUTTON_ID)
                .item_drag_acceptance(),
            DragAcceptance::None,
            "{source:?}"
        );
        b.host
            .root
            .fire_event(kind::DROP_RELEASED, button, Some(DragPayload::new(alias)));
        b.host.pump();
        b.update(&view);
        assert!(sessions(&b.outputs()).is_empty(), "{source:?}");
    }
}

#[test]
fn the_use_and_examine_buttons_act_on_the_selection_or_enter_their_modes() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0x5003, PLAYER, 0, ItemType::MISC);
    o.get_mut(0x5003).useability = Some(ItemUseable(0x0000_0008));
    let mut b = bar(&[], None);
    b.update(&ItemsView {
        selected: Some(0x5003),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(b.enabled(toolbar::USE_BUTTON_ID));
    b.outputs();
    b.click(toolbar::USE_BUTTON_ID);
    b.click(toolbar::EXAMINE_BUTTON_ID);
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(
                0x5003
            )))),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(0x5003)
            ))),
        ]
    );
    b.update(&ItemsView::new(&o.t, PLAYER));
    b.click(toolbar::EXAMINE_BUTTON_ID);
    assert_dbg_eq!(b.outputs(), vec![PanelOutput::EnterExamineMode]);
}

#[test]
fn the_use_button_tracks_an_empty_an_unusable_and_a_usable_selection() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0x5003, PLAYER, 0, ItemType::SPELL_COMPONENTS);
    o.get_mut(0x5003).useability = Some(ItemUseable(0x0000_0001));
    o.contained(0x5004, PLAYER, 1, ItemType::MISC);
    o.get_mut(0x5004).useability = Some(ItemUseable(0x0022_0008));
    o.contained(0x5005, PLAYER, 2, ItemType::MELEE_WEAPON);
    o.get_mut(0x5005).combat_use = Some(1);
    let mut b = bar(&[], None);
    for (selected, enabled) in [
        (None, false),
        (Some(0x5003), false),
        (Some(0x5004), true),
        (Some(0x5005), true),
    ] {
        b.update(&ItemsView {
            selected,
            ..ItemsView::new(&o.t, PLAYER)
        });
        assert_eq!(b.enabled(toolbar::USE_BUTTON_ID), enabled, "{selected:?}");
    }
}

#[test]
fn the_ammunition_counter_follows_a_thrown_stack_or_the_ammo_slot() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xB, EquipMask::MISSILE_WEAPON);
    o.get_mut(0xB).stack_size = 12;
    o.get_mut(0xB).stack_size_max = 100;
    o.equipped(0xC, EquipMask::MISSILE_AMMO);
    o.get_mut(0xC).stack_size = 50;
    let mut b = bar(&[], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        b.button(toolbar::AMMO_INDICATOR_ID).label.as_deref(),
        Some("12")
    );
    o.get_mut(0xB).stack_size = 11;
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        b.button(toolbar::AMMO_INDICATOR_ID).label.as_deref(),
        Some("11")
    );
    // A launcher: the ammo slot's count; nothing once unwielded.
    o.get_mut(0xB).stack_size_max = 1;
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        b.button(toolbar::AMMO_INDICATOR_ID).label.as_deref(),
        Some("50")
    );
    o.get_mut(0xC).currently_equipped_location = EquipMask::NONE;
    o.get_mut(0xC).wielder_id = 0;
    let mut ev = Vec::new();
    o.t.notify_object_updated(0xC, &mut ev);
    let mut fresh = Objects::new();
    fresh.player(102, 7);
    fresh.equipped(0xB, EquipMask::MISSILE_WEAPON);
    b.update(&ItemsView::new(&fresh.t, PLAYER));
    assert_eq!(b.button(toolbar::AMMO_INDICATOR_ID).label, None);
}

#[test]
fn the_combat_indicators_show_the_mode_and_each_toggles_combat() {
    let o = Objects::new();
    let mut b = bar(&[], None);
    let shown = |b: &Bar| toolbar::COMBAT_INDICATOR_IDS.map(|id| b.visible(id));
    assert_eq!(shown(&b), [true, false, false, false], "peace after bind");
    b.update(&ItemsView {
        combat_mode: CombatMode::MELEE,
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(shown(&b), [false, true, false, false]);
    b.update(&ItemsView {
        combat_mode: CombatMode::MAGIC,
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(shown(&b), [false, false, false, true]);
    b.outputs();
    for id in toolbar::COMBAT_INDICATOR_IDS {
        b.click(id);
    }
    assert_eq!(
        b.outputs()
            .iter()
            .filter(|o| matches!(o, PanelOutput::ToggleCombat))
            .count(),
        4
    );
}

#[test]
fn shortcut_numbers_mark_the_top_row_and_ghost_in_magic_mode() {
    let regular: Vec<u32> = (0x101..=0x109).collect();
    let digits = Arc::new(ShortcutDigits {
        regular: regular.clone(),
        ghosted: (0x201..=0x209).collect(),
        empty: Some((0x301..=0x309).collect()),
    });
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(0, 0x5001)], Some(digits.clone()));
    b.update(&ItemsView::new(&o.t, PLAYER));
    for i in 0..9 {
        assert_eq!(b.slot(i).shortcut_num(), i as i32);
        assert!(!b.slot(i).shortcut_ghosted());
    }
    for i in 9..18 {
        assert_eq!(b.slot(i).shortcut_num(), -1);
    }
    assert_eq!(b.slot(0).active_digits(), Some(&regular[..]));
    assert_eq!(b.slot(1).active_digits(), digits.empty.as_deref());
    b.update(&ItemsView {
        combat_mode: CombatMode::MAGIC,
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(b.slot(0).shortcut_ghosted());
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(!b.slot(0).shortcut_ghosted());
}

#[test]
fn slots_are_shortcut_bar_sources_with_the_green_cross_and_accept_real_items() {
    let o = Objects::new();
    let mut b = bar(&[], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    for i in 0..18 {
        let s = b.slot(i);
        assert_eq!(s.slot_index, i as i32);
        assert_eq!(s.source_kind, ItemDragSource::ShortcutBar);
        assert_eq!(s.drag_accept_sprite, 0x0600_11FA);
    }
    b.fire(0, kind::DRAG_ENTER, Some(payload(0x5001)));
    assert_eq!(b.slot(0).drag_acceptance(), DragAcceptance::Accept);
    b.fire(1, kind::DRAG_ENTER, Some(payload(0)));
    assert_eq!(b.slot(1).drag_acceptance(), DragAcceptance::None);
}

fn shortcut_changes(outputs: &[PanelOutput]) -> Vec<String> {
    outputs
        .iter()
        .filter_map(|o| match o {
            PanelOutput::AddShortcut(e) => Some(format!("add {} {:#x}", e.index, e.object_id)),
            PanelOutput::RemoveShortcut(s) => Some(format!("remove {s}")),
            _ => None,
        })
        .collect()
}

#[test]
fn lifting_a_shortcut_selects_it_and_empties_its_slot() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(3, 0x5001)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    b.outputs();
    let p = ItemDragPayload {
        shortcut: Some(entry(3, 0x5001)),
        ..ItemDragPayload::new(0x5001, ItemDragSource::ShortcutBar, 3)
    };
    b.fire(3, kind::DRAG_BEGIN, Some(p));
    let out = b.outputs();
    assert!(matches!(out[0], PanelOutput::SelectObject(0x5001)));
    assert_eq!(shortcut_changes(&out), vec!["remove 3"]);
    assert_eq!(b.slot(3).object_id, 0);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(3).object_id, 0);
}

#[test]
fn the_bar_shows_the_sessions_shortcuts_whenever_they_change() {
    // The session owns the bar: the toolbar's copy is
    // replaced by the view's bar when that changes, and a change made here
    // stands until the session's bar changes again.
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    item(&mut o, 0x5002);
    let mut b = bar(&[], None);
    let mut session = ac_core::items::ShortcutStore::new();
    session.set(entry(3, 0x5001));
    let view = ItemsView {
        shortcuts: Some(&session),
        ..ItemsView::new(&o.t, PLAYER)
    };
    b.update(&view);
    assert_eq!(b.slot(3).object_id, 0x5001);
    let p = ItemDragPayload {
        shortcut: Some(entry(3, 0x5001)),
        ..ItemDragPayload::new(0x5001, ItemDragSource::ShortcutBar, 3)
    };
    b.fire(3, kind::DRAG_BEGIN, Some(p));
    b.update(&view);
    assert_eq!(
        b.slot(3).object_id,
        0,
        "the same session bar does not undo it"
    );
    let mut session = ac_core::items::ShortcutStore::new();
    session.set(entry(5, 0x5002));
    b.update(&ItemsView {
        shortcuts: Some(&session),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(b.slot(5).object_id, 0x5002);
    assert_eq!(b.slot(3).object_id, 0);
}

#[test]
fn dropping_a_lifted_shortcut_on_an_occupied_slot_swaps_them() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    item(&mut o, 0x5002);
    let mut b = bar(&[entry(3, 0x5001), entry(5, 0x5002)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    let p = ItemDragPayload {
        shortcut: Some(entry(3, 0x5001)),
        ..ItemDragPayload::new(0x5001, ItemDragSource::ShortcutBar, 3)
    };
    b.outputs();
    b.fire(3, kind::DRAG_BEGIN, Some(p.clone()));
    b.fire(5, kind::DROP_RELEASED, Some(p));
    assert_eq!(
        shortcut_changes(&b.outputs()),
        vec!["remove 3", "remove 5", "add 5 0x5001", "add 3 0x5002"]
    );
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(5).object_id, 0x5001);
    assert_eq!(b.slot(3).object_id, 0x5002);
}

#[test]
fn a_drop_keeps_the_raw_shortcut_fields_and_an_empty_target_only_places() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let raw = ShortcutEntry {
        index: 3,
        object_id: 0x5001,
        spell_id: 0x1234,
    };
    let mut b = bar(&[raw], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    let p = ItemDragPayload {
        shortcut: Some(raw),
        ..ItemDragPayload::new(0x5001, ItemDragSource::ShortcutBar, 3)
    };
    b.outputs();
    b.fire(3, kind::DRAG_BEGIN, Some(p.clone()));
    b.fire(7, kind::DROP_RELEASED, Some(p));
    let out = b.outputs();
    assert_eq!(shortcut_changes(&out), vec!["remove 3", "add 7 0x5001"]);
    assert!(out.iter().any(|o| matches!(o,
        PanelOutput::AddShortcut(e) if e.spell_id == 0x1234 && e.index == 7)));
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(b.slot(3).object_id, 0);
    assert_eq!(b.slot(7).object_id, 0x5001);
}

#[test]
fn a_fresh_item_on_an_occupied_slot_pushes_the_old_one_to_the_first_empty_on_the_right() {
    let mut o = Objects::new();
    o.player(102, 7);
    item(&mut o, 0x5001);
    item(&mut o, 0x5002);
    o.mv(0x5001, PLAYER, 0);
    o.mv(0x5002, PLAYER, 1);
    let mut b = bar(&[entry(2, 0x5002), entry(3, 0x5003)], None);
    let view = ItemsView::new(&o.t, PLAYER);
    b.update(&view);
    b.outputs();
    let p = ItemDragPayload::new(0x5001, ItemDragSource::Inventory, 40);
    b.fire(2, kind::DROP_RELEASED, Some(p));
    b.update(&view);
    let out = b.outputs();
    assert_eq!(
        shortcut_changes(&out),
        vec!["remove 2", "add 2 0x5001", "add 4 0x5002"]
    );
    // The player's own item asks for no pickup.
    assert!(sessions(&out).is_empty());
}

/// The bar with `occupant` in slot 4, after a drop of `dropped` (with
/// `setup` applied to it, lying on the ground) on slot 4: the shortcut
/// changes and the session commands.
fn drop_from_the_ground_on_slot_4(
    occupant: Option<u32>,
    setup: impl FnOnce(&mut ClientObject),
    open_vendor: u32,
) -> (Vec<String>, Vec<SessionCommand>) {
    const LOOT: u32 = 0x7000_5001;
    let mut o = Objects::new();
    o.player(102, 7);
    item(&mut o, 0x5002);
    o.mv(0x5002, PLAYER, 0);
    let mut loot = ClientObject::new(LOOT);
    loot.item_type = ItemType::MISC;
    loot.icon_id = 0x0600_0777;
    setup(&mut loot);
    o.add(loot);
    let shortcuts: Vec<ShortcutEntry> = occupant.map(|id| entry(4, id)).into_iter().collect();
    let mut b = bar(&shortcuts, None);
    let view = ItemsView {
        open_vendor,
        ..ItemsView::new(&o.t, PLAYER)
    };
    b.update(&view);
    b.outputs();
    let p = ItemDragPayload::new(LOOT, ItemDragSource::Ground, 0);
    b.fire(4, kind::DRAG_ENTER, Some(p.clone()));
    b.fire(4, kind::DROP_RELEASED, Some(p));
    b.update(&view);
    let out = b.outputs();
    (shortcut_changes(&out), sessions(&out))
}

#[test]
fn an_item_dropped_from_the_ground_on_a_slot_is_picked_up_and_the_session_gives_it_the_shortcut() {
    // GitHub #45: a ground item dropped on the hotbar put a shortcut to an
    // object lying on the ground and picked nothing up. It is picked up
    // into the preferred pack first; the shortcut follows the pickup.
    let pickup = vec![SessionCommand::Item(ItemCommand::PickUpToShortcut {
        item: ObjectRef::Guid(0x7000_5001),
        slot: 4,
    })];
    let (changes, sent) = drop_from_the_ground_on_slot_4(None, |_| {}, 0);
    assert!(changes.is_empty(), "{changes:?}");
    assert_dbg_eq!(sent, pickup);
    // An occupied slot makes way to the first empty slot on its right.
    let (changes, sent) = drop_from_the_ground_on_slot_4(Some(0x5002), |_| {}, 0);
    assert_eq!(changes, vec!["remove 4", "add 5 0x5002"]);
    assert_dbg_eq!(sent, pickup);
}

#[test]
fn a_stuck_object_a_creature_or_a_vendors_item_dropped_on_a_slot_gets_no_shortcut() {
    // The slot's shortcut still makes way, as the game does before it
    // judges the dropped object; nothing is picked up or added.
    let stuck = |o: &mut ClientObject| {
        o.public_weenie_bitfield = Some(ac_core::items::PublicWeenieFlags::STUCK);
    };
    let creature = |o: &mut ClientObject| o.item_type = ItemType::CREATURE;
    const VENDOR: u32 = 0x7000_9000;
    let (changes, sent) = drop_from_the_ground_on_slot_4(None, stuck, 0);
    assert!(
        changes.is_empty() && sent.is_empty(),
        "{changes:?} {sent:?}"
    );
    let (changes, sent) = drop_from_the_ground_on_slot_4(Some(0x5002), stuck, 0);
    assert_eq!(changes, vec!["remove 4", "add 5 0x5002"]);
    assert!(sent.is_empty());
    let (changes, sent) = drop_from_the_ground_on_slot_4(None, creature, 0);
    assert!(
        changes.is_empty() && sent.is_empty(),
        "{changes:?} {sent:?}"
    );
    let in_shop = |o: &mut ClientObject| o.container_id = VENDOR;
    let (changes, sent) = drop_from_the_ground_on_slot_4(None, in_shop, VENDOR);
    assert!(
        changes.is_empty() && sent.is_empty(),
        "{changes:?} {sent:?}"
    );
}

/// A salvage, vendor or house window cell is an alias the bar refuses: it
/// makes no shortcut.
#[test]
fn a_salvage_or_vendor_window_drag_makes_no_shortcut() {
    for source in [ItemDragSource::Salvage, ItemDragSource::Vendor] {
        let mut o = Objects::new();
        item(&mut o, 0x5001);
        let mut b = bar(&[], None);
        b.update(&ItemsView::new(&o.t, PLAYER));
        b.outputs();
        let p = ItemDragPayload::new(0x5001, source, 0);
        b.fire(2, kind::DRAG_ENTER, Some(p.clone()));
        assert_eq!(
            b.slot(2).drag_acceptance(),
            DragAcceptance::None,
            "{source:?}"
        );
        b.fire(2, kind::DROP_RELEASED, Some(p));
        assert!(shortcut_changes(&b.outputs()).is_empty(), "{source:?}");
    }
}

#[test]
fn dropping_a_shortcut_back_on_its_own_slot_puts_it_back() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(3, 0x5001)], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    let p = ItemDragPayload {
        shortcut: Some(entry(3, 0x5001)),
        ..ItemDragPayload::new(0x5001, ItemDragSource::ShortcutBar, 3)
    };
    b.outputs();
    b.fire(3, kind::DRAG_BEGIN, Some(p.clone()));
    b.fire(3, kind::DROP_RELEASED, Some(p));
    assert_eq!(
        shortcut_changes(&b.outputs()),
        vec!["remove 3", "add 3 0x5001"]
    );
}

#[test]
fn quick_slot_keys_use_or_select_and_a_target_mode_takes_them_first() {
    let mut o = Objects::new();
    item(&mut o, 0x5001);
    let mut b = bar(&[entry(0, 0x5001)], None);
    let view = ItemsView::new(&o.t, PLAYER);
    b.update(&view);
    b.outputs();
    assert!(b.with(|c, ui| c.toolbar.use_shortcut(ui, 0, true)));
    assert!(b.with(|c, ui| c.toolbar.use_shortcut(ui, 0, false)));
    assert!(!b.with(|c, ui| c.toolbar.use_shortcut(ui, 1, true)));
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(
                0x5001
            )))),
            PanelOutput::SelectObject(0x5001),
        ]
    );
    b.update(&ItemsView {
        target_mode: true,
        target_source: Some(0x5003),
        ..view
    });
    assert!(b.with(|c, ui| c.toolbar.use_shortcut(ui, 0, false)));
    assert!(b.with(|c, ui| c.toolbar.use_shortcut(ui, 1, true)));
    assert_dbg_eq!(
        b.outputs(),
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::OfferTarget(
                ObjectRef::Guid(0x5001)
            ))),
            PanelOutput::CancelTargetMode,
        ]
    );
}

#[test]
fn creating_a_shortcut_takes_the_first_empty_slot_once_for_an_owned_item() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.bag(0x5002, 0, 24);
    o.contained(0x5003, 0x5002, 0, ItemType::MISC);
    let mut npc = ClientObject::new(0x5004);
    npc.item_type = ItemType::CREATURE;
    o.add(npc);
    let mut b = bar(&[], None);
    let view = ItemsView::new(&o.t, PLAYER);
    b.update(&view);
    b.outputs();
    assert!(b.with(|c, ui| c.toolbar.create_shortcut_to_item(ui, &view, 0x5003)));
    assert!(!b.with(|c, ui| c.toolbar.create_shortcut_to_item(ui, &view, 0x5003)));
    assert!(!b.with(|c, ui| c.toolbar.create_shortcut_to_item(ui, &view, 0x5004)));
    assert_eq!(shortcut_changes(&b.outputs()), vec!["add 0 0x5003"]);
    b.update(&view);
    assert_eq!(b.slot(0).object_id, 0x5003);
    // The create key makes one to the selection.
    let selected = ItemsView {
        selected: Some(PLAYER),
        ..view
    };
    assert!(b.with(|c, ui| {
        c.toolbar
            .handle_key(ui, &selected, ToolbarKey::CreateShortcut)
    }));
    assert_eq!(shortcut_changes(&b.outputs()), vec!["add 1 0x50000001"]);
}

#[test]
fn every_retail_quick_slot_key_names_its_slot_and_intent() {
    assert_eq!(quick_slot(Some(1), None), Some(ToolbarKey::Use(0)));
    assert_eq!(quick_slot(Some(9), None), Some(ToolbarKey::Use(8)));
    assert_eq!(quick_slot(Some(10), None), Some(ToolbarKey::Use(9)));
    assert_eq!(quick_slot(Some(13), None), Some(ToolbarKey::Use(12)));
    assert_eq!(quick_slot(Some(14), None), Some(ToolbarKey::Use(13)));
    assert_eq!(quick_slot(Some(18), None), Some(ToolbarKey::Use(17)));
    assert_eq!(quick_slot(None, Some(1)), Some(ToolbarKey::Select(0)));
    assert_eq!(quick_slot(None, Some(9)), Some(ToolbarKey::Select(8)));
    assert_eq!(quick_slot(Some(19), None), None);
    assert_eq!(quick_slot(None, Some(10)), None);
    assert_eq!(quick_slot(None, None), None);
}

// ── The split quantity (SelectedObjectControllerTests split cases,
//    StackSplitQuantityStateTests through the panel) ──────────────────────

#[test]
fn a_selected_stack_shows_the_split_controls_at_the_whole_stack() {
    let mut o = Objects::new();
    o.contained(0x5001, PLAYER, 0, ItemType::MISC);
    o.get_mut(0x5001).stack_size = 20;
    o.contained(0x5002, PLAYER, 1, ItemType::MISC);
    let mut b = bar(&[], None);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(!b.visible(toolbar::STACK_SIZE_ENTRY_ID));
    assert!(!b.visible(toolbar::STACK_SIZE_SLIDER_ID));
    b.update(&ItemsView {
        selected: Some(0x5001),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(b.visible(toolbar::STACK_SIZE_ENTRY_ID));
    assert!(b.visible(toolbar::STACK_SIZE_SLIDER_ID));
    let field = |b: &Bar| {
        b.host
            .root
            .widget::<Field>(b.ids[&toolbar::STACK_SIZE_ENTRY_ID])
            .unwrap()
            .text()
    };
    assert_eq!(field(&b), "20");
    assert_eq!(b.with(|c, _| c.split.quantity().value()), 20);
    // One item: hidden, quantity 1.
    b.update(&ItemsView {
        selected: Some(0x5002),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(!b.visible(toolbar::STACK_SIZE_ENTRY_ID));
    assert_eq!(b.with(|c, _| c.split.quantity().value()), 1);
}

#[test]
fn the_slider_and_the_typed_entry_set_the_quantity_and_mirror_each_other() {
    let mut o = Objects::new();
    o.contained(0x5001, PLAYER, 0, ItemType::MISC);
    o.get_mut(0x5001).stack_size = 20;
    let mut b = bar(&[], None);
    b.update(&ItemsView {
        selected: Some(0x5001),
        ..ItemsView::new(&o.t, PLAYER)
    });
    let slider = b.ids[&toolbar::STACK_SIZE_SLIDER_ID];
    let entry = b.ids[&toolbar::STACK_SIZE_ENTRY_ID];
    b.host
        .root
        .send_message(slider, ac_ui::root::ElementMessage::ScalarChanged(0.5));
    b.host.pump();
    // Step 500 of 1000 of 20: 1 + 500 * 20 / 1000.
    assert_eq!(b.with(|c, _| c.split.quantity().value()), 11);
    assert_eq!(b.host.root.widget::<Field>(entry).unwrap().text(), "11");
    b.host
        .root
        .widget_mut::<Field>(entry)
        .unwrap()
        .set_text("7");
    b.host.root.send_message(
        entry,
        ac_ui::root::ElementMessage::Submitted("7".to_owned()),
    );
    b.host.pump();
    assert_eq!(b.with(|c, _| c.split.quantity().value()), 7);
    let pos = b
        .host
        .root
        .widget::<Scrollbar>(slider)
        .unwrap()
        .scalar_position();
    assert!((pos - 0.35).abs() < 1e-6);
    // Too much clamps to the stack; leaving the entry commits it.
    b.host.root.set_keyboard_focus(Some(entry));
    b.host.pump();
    b.host
        .root
        .widget_mut::<Field>(entry)
        .unwrap()
        .set_text("99");
    b.host.root.set_keyboard_focus(None);
    b.host.pump();
    assert_eq!(b.with(|c, _| c.split.quantity().value()), 20);
    // The entry takes digits only.
    let f = b.host.root.widget_mut::<Field>(entry).unwrap();
    f.set_text("");
    f.insert_char('4');
    f.insert_char('x');
    assert_eq!(f.text(), "4");
}

#[test]
fn the_split_key_focuses_the_entry_of_the_selected_stack_only() {
    let mut o = Objects::new();
    o.contained(0x5001, PLAYER, 0, ItemType::MISC);
    o.get_mut(0x5001).stack_size = 20;
    let mut b = bar(&[], None);
    b.update(&ItemsView {
        selected: Some(0x5001),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(!b.with(|c, ui| c.split.focus_entry(ui, 0x5002)));
    assert!(b.with(|c, ui| c.split.focus_entry(ui, 0x5001)));
    assert_eq!(
        b.host.root.keyboard_focus(),
        Some(b.ids[&toolbar::STACK_SIZE_ENTRY_ID])
    );
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

    // ToolbarControllerTests (RetailFixture_panelButtonsExposeExactDatPanelIds,
    // RetailFixture_ammoNumberUsesAuthoredMissileIndicatorElement).
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_toolbar_imports_binds_and_a_drop_on_a_slot_makes_a_shortcut() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            toolbar::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("toolbar layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        for (id, panel) in PANEL_BUTTONS {
            let b = layout.find(id).expect("panel button");
            let w = host.root.widget::<Button>(b).expect("a button");
            assert_eq!(w.enum_attribute(toolbar::PANEL_ID_ATTRIBUTE), Some(panel));
        }
        let ammo = layout.find(toolbar::AMMO_INDICATOR_ID).unwrap();
        assert!(host.root.widget::<Button>(ammo).is_some());
        let e = host.root.element(ammo).unwrap();
        assert_eq!((e.width, e.height), (55.0, 58.0));
        for id in toolbar::SLOT_IDS {
            let l = layout.find(id).expect("slot");
            assert!(host.root.widget::<ItemList>(l).is_some(), "{id:#x}");
        }
        assert!(
            host.root
                .widget::<Field>(layout.find(toolbar::STACK_SIZE_ENTRY_ID).unwrap())
                .is_some()
        );
        assert!(
            host.root
                .widget::<Scrollbar>(layout.find(toolbar::STACK_SIZE_SLIDER_ID).unwrap())
                .is_some()
        );
        host.root
            .register_window("toolbar", layout.root, None, 0)
            .unwrap();
        let toolbar = ToolbarController::bind(&mut host.root, &layout, None, None);
        let split = StackSplitController::bind(&mut host.root, &layout);
        host.attach_controller(
            "toolbar",
            Box::new(ToolbarWindowController {
                toolbar,
                split,
                selected: None,
            }),
        )
        .unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        o.contained(0x5001, PLAYER, 0, ItemType::MISC);
        let view = ItemsView::new(&o.t, PLAYER);
        let mut icons = TestIcons::default();
        host.with_controller::<ToolbarWindowController, _>("toolbar", |w, ui| {
            w.toolbar.bind_panel_buttons(ui, |_| true);
            w.update(ui, &view, &mut icons);
        });
        let slot = layout.find(toolbar::SLOT_IDS[0]).unwrap();
        let cell = host.root.widget::<ItemList>(slot).unwrap().item(0).unwrap();
        host.take_outputs();
        host.root.fire_event(
            kind::DROP_RELEASED,
            cell,
            Some(DragPayload::new(payload(0x5001))),
        );
        host.pump();
        host.with_controller::<ToolbarWindowController, _>("toolbar", |w, ui| {
            w.update(ui, &view, &mut icons);
        });
        let out = panel_outputs(&mut host);
        assert!(out.iter().any(|o| matches!(o,
            PanelOutput::AddShortcut(e) if e.index == 0 && e.object_id == 0x5001)));
        assert_eq!(
            host.root.widget::<ItemSlot>(cell).unwrap().object_id,
            0x5001
        );
        let list = host.draw((1024.0, 768.0), &c);
        assert!(!list.main.is_empty());
        assert!(LayoutSource::layout(&c, toolbar::LAYOUT_ID).is_some());
        assert!(c.take_errors().is_empty());
    }
}

// OpenAC `Populate_packShortcut_showsItsCapacityBar_andAnEmptyPackNone` and
// `ItemMovedIntoOrOutOfAPackShortcut_refreshesItsCapacityBar`: every item
// cell shows a container's fullness meter, the toolbar's too (its cell
// prototype carries the meter): loose items over item capacity for a pack
// holding something, none for an empty pack or a plain item.
#[test]
fn a_pack_shortcut_shows_how_full_the_pack_is_and_follows_its_contents() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.bag(0xC0, 0, 24);
    for i in 0..6 {
        o.contained(0xB0 + i, 0xC0, i as i32, ItemType::MISC);
    }
    o.bag(0xC1, 1, 24);
    item(&mut o, 0x5001);
    let mut b = bar(
        &[
            entry(0, 0xC0),
            entry(1, 0xC1),
            entry(2, 0x5001),
            entry(3, PLAYER),
        ],
        None,
    );
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!((b.slot(0).capacity_fill - 0.25).abs() < 1e-6);
    assert!(b.slot(1).capacity_fill < 0.0, "an empty pack shows none");
    assert!(b.slot(2).capacity_fill < 0.0, "a plain item shows none");
    assert!(
        b.slot(3).capacity_fill < 0.0,
        "the empty main pack shows none"
    );
    // Something goes in and out.
    o.contained(0xD0, 0xC1, 0, ItemType::MISC);
    o.contained(0xD1, PLAYER, 0, ItemType::MISC);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!((b.slot(1).capacity_fill - 1.0 / 24.0).abs() < 1e-6);
    assert!((b.slot(3).capacity_fill - 1.0 / 102.0).abs() < 1e-6);
    o.mv(0xD0, PLAYER, 1);
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(b.slot(1).capacity_fill < 0.0);
    // A cleared slot shows none.
    b.with(|c, _| c.toolbar.load_shortcuts([entry(1, 0xC1)]));
    b.update(&ItemsView::new(&o.t, PLAYER));
    assert!(b.slot(0).capacity_fill < 0.0);
}
