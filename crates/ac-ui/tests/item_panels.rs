//! The item slot, the drag-and-drop spine and the inventory window.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiItemSlotTests.cs`,
//! `UiItemListTests.cs` (cooldown and empty-sprite propagation),
//! `DragDropSpineTests.cs`, `IconComposerTests.cs` (dat cases),
//! `Layout/ItemListCellTemplateTests.cs` and
//! `Layout/InventoryControllerTests.cs`, on synthetic layouts shaped like the
//! retail ones. OpenAC's drag handler interface is the slot's element
//! messages here, answered by the window's controller; its
//! `SelectionState` and `RuntimeItemInteraction` are the [`ItemsView`]'s
//! selection, target mode and requests in flight, and the requests it sends
//! are [`PanelOutput`]s.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

use ac_core::items::{
    ClientObject, EquipMask, ItemType, ShortcutEntry, ShortcutStore, StackSplitQuantity,
};
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::draw::TextureRef;
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::{PanelController, UiHost};
use ac_ui::icons::IconSource;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::inventory::{self, InventoryController, InventorySprites};
use ac_ui::panels::items::{ItemsView, caption_of};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::{ItemList, ItemSlot, Meter, ScrollSource, Scrollbar, ShortcutDigits, Text};
use ac_ui::{ElementId, MouseButton};
use common::items::*;
use common::*;

// ── The synthetic inventory window ──────────────────────────────────────

struct Inv {
    host: UiHost,
    grid: ElementId,
    containers: ElementId,
    top: ElementId,
    meter: ElementId,
    scrollbar: ElementId,
    close: ElementId,
    icons: TestIcons,
}

fn inventory_window(digits: Option<Arc<ShortcutDigits>>, sprites: InventorySprites) -> Inv {
    inventory_window_mounted(digits, sprites, None)
}

/// The chrome a test frame adds around the inventory (8 pixels a side).
const FRAME_BORDER: f32 = 8.0;

/// The inventory, mounted in a resizable window frame as the client
/// mounts it (the content stretches to the frame's bottom) when `frame`
/// is given; `frame` gets the frame's id.
fn inventory_window_mounted(
    digits: Option<Arc<ShortcutDigits>>,
    sprites: InventorySprites,
    frame: Option<&mut Option<ElementId>>,
) -> Inv {
    let mut host = UiHost::new();
    if frame.is_some() {
        host.root.set_size(1920.0, 1080.0);
    } else {
        host.root.set_size(800.0, 600.0);
    }
    let ui = &mut host.root;
    let r = ui.root();
    let (outer, root) = if frame.is_some() {
        let b = FRAME_BORDER;
        let outer = panel(ui, r, at(0.0, 0.0, 300.0 + 2.0 * b, 362.0 + 2.0 * b));
        if let Some(e) = ui.element_mut(outer) {
            e.min_height = 362.0 + 2.0 * b;
            e.max_height = f32::MAX;
        }
        let root = panel(ui, outer, at(b, b, 300.0, 362.0));
        ui.set_anchors(
            root,
            ac_ui::geometry::AnchorEdges::LEFT
                | ac_ui::geometry::AnchorEdges::TOP
                | ac_ui::geometry::AnchorEdges::BOTTOM,
        );
        ui.apply_anchor(root, 300.0 + 2.0 * b, 362.0 + 2.0 * b);
        (Some(outer), root)
    } else {
        (None, panel(ui, r, at(0.0, 0.0, 300.0, 362.0)))
    };
    if let Some(slot) = frame {
        *slot = outer;
    }
    let grid = list(ui, root, 100.0, 100.0, 192.0, 96.0);
    let containers = list(ui, root, 10.0, 100.0, 36.0, 252.0);
    let top = list(ui, root, 10.0, 50.0, 36.0, 36.0);
    let meter = ui.add(root, at(60.0, 50.0, 11.0, 58.0), Meter::default());
    let mut ids = HashMap::new();
    for (id, x, y, w, h) in [
        (inventory::BURDEN_TEXT_ID, 60.0, 110.0, 36.0, 15.0),
        (inventory::BURDEN_CAPTION_ID, 60.0, 30.0, 36.0, 15.0),
        (inventory::CONTENTS_CAPTION_ID, 100.0, 80.0, 192.0, 15.0),
        (inventory::TITLE_TEXT_ID, 0.0, 0.0, 276.0, 25.0),
    ] {
        let mut e = at(x, y, w, h);
        Text::configure(&mut e);
        ids.insert(id, ui.add(root, e, Text::default()));
    }
    let scrollbar = ui.add(root, at(292.0, 100.0, 8.0, 96.0), Scrollbar::default());
    let close = ui.add(
        root,
        at(280.0, 0.0, 16.0, 16.0),
        ac_ui::widgets::Button::new(
            Arc::new(ac_ui::element_info::ElementInfo::default()),
            None,
            Vec::new(),
            0.0,
        ),
    );
    ids.insert(inventory::CONTENTS_GRID_ID, grid);
    ids.insert(inventory::CONTAINER_LIST_ID, containers);
    ids.insert(inventory::TOP_CONTAINER_ID, top);
    ids.insert(inventory::BURDEN_METER_ID, meter);
    ids.insert(inventory::CONTENTS_SCROLLBAR_ID, scrollbar);
    ids.insert(inventory::CLOSE_BUTTON_ID, close);
    let layout = ImportedLayout::from_parts(root, ids);
    match outer {
        Some(outer) => ui.register_window("inventory", outer, Some(root), 0),
        None => ui.register_window("inventory", root, None, 0),
    }
    .unwrap();
    let c = InventoryController::bind(ui, &layout, sprites, None, digits);
    host.attach_controller("inventory", Box::new(c)).unwrap();
    Inv {
        host,
        grid,
        containers,
        top,
        meter,
        scrollbar,
        close,
        icons: TestIcons::default(),
    }
}

impl Inv {
    fn update(&mut self, view: &ItemsView<'_>) {
        let icons = &mut self.icons;
        self.host
            .with_controller::<InventoryController, _>("inventory", |c, ui| {
                c.update(ui, view, icons)
            })
            .unwrap();
    }

    fn cell(&self, list: ElementId, i: usize) -> ElementId {
        self.host
            .root
            .widget::<ItemList>(list)
            .unwrap()
            .item(i)
            .unwrap()
    }

    fn slot(&self, list: ElementId, i: usize) -> &ItemSlot {
        self.host
            .root
            .widget::<ItemSlot>(self.cell(list, i))
            .unwrap()
    }

    fn count(&self, list: ElementId) -> usize {
        self.host.root.widget::<ItemList>(list).unwrap().num_items()
    }

    fn fire(&mut self, list: ElementId, i: usize, k: i32) {
        let c = self.cell(list, i);
        self.host.root.fire_event(k, c, None);
        self.host.pump();
    }

    fn fire_payload(&mut self, list: ElementId, i: usize, k: i32, p: ItemDragPayload) {
        let c = self.cell(list, i);
        self.host.root.fire_event(k, c, Some(DragPayload::new(p)));
        self.host.pump();
    }

    fn click(&mut self, list: ElementId, i: usize) {
        self.fire(list, i, kind::MOUSE_DOWN);
        self.fire(list, i, kind::CLICK);
    }

    fn outputs(&mut self) -> Vec<PanelOutput> {
        self.host
            .take_outputs()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Panel(p) => Some(p),
                _ => None,
            })
            .collect()
    }
}

fn captions(inv: &mut Inv) -> [String; 4] {
    inv.host
        .with_controller::<InventoryController, _>("inventory", |c, ui| {
            c.captions().map(|id| caption_of(ui, id))
        })
        .unwrap()
}

// ── Item slot (UiItemSlotTests) ─────────────────────────────────────────

#[test]
fn an_item_slot_is_a_leaf_with_the_retail_default_sprites() {
    let s = ItemSlot::default();
    use ac_ui::tree::Widget;
    assert!(s.consumes_dat_children());
    assert_eq!(s.empty_sprite, 0x0600_74CF);
    assert_eq!(s.waiting_sprite, 0x0600_109A);
    assert_eq!(s.selected_sprite, 0x0600_4D21);
    assert_eq!(s.open_container_sprite, 0x0600_5D9C);
    assert_eq!(
        (s.capacity_back_sprite, s.capacity_front_sprite),
        (0x0600_4D22, 0x0600_4D23)
    );
    assert_eq!(
        (s.structure_back_sprite, s.structure_front_sprite),
        (0x0600_4D24, 0x0600_4D25)
    );
    assert!((s.capacity_fill + 1.0).abs() < f32::EPSILON);
    assert!((s.structure_fill + 1.0).abs() < f32::EPSILON);
    assert!(!s.selected && !s.is_open_container);
    assert_eq!(s.shortcut_num(), -1);
    assert!(!s.shortcut_ghosted());
}

#[test]
fn set_item_and_clear_round_trip() {
    let mut s = ItemSlot::default();
    assert_eq!(s.object_id, 0);
    s.set_item(0x5001, Some(TextureRef::Runtime(0x99)), None, None);
    assert_eq!(
        (s.object_id, s.icon),
        (0x5001, Some(TextureRef::Runtime(0x99)))
    );
    s.clear();
    assert_eq!((s.object_id, s.icon), (0, None));
}

#[test]
fn shortcut_numbers_pick_the_digit_set_by_occupancy_and_stance() {
    let digits = Arc::new(ShortcutDigits {
        regular: vec![0x10, 0x11, 0x12],
        ghosted: vec![0x20, 0x21, 0x22],
        empty: Some(vec![0x30, 0x31, 0x32]),
    });
    let mut s = ItemSlot {
        digits: Some(digits.clone()),
        ..ItemSlot::default()
    };
    s.set_shortcut_num(3, true);
    assert_eq!((s.shortcut_num(), s.shortcut_ghosted()), (3, true));
    s.set_shortcut_num(0, false);
    assert_eq!(s.active_digits(), Some(&[0x30, 0x31, 0x32][..]));
    s.set_shortcut_num(0, true);
    assert_eq!(s.active_digits(), Some(&[0x30, 0x31, 0x32][..]));
    s.set_item(0x5001, Some(TextureRef::Runtime(0x99)), None, None);
    s.set_shortcut_num(0, false);
    assert_eq!(s.active_digits(), Some(&[0x10, 0x11, 0x12][..]));
    s.set_shortcut_num(0, true);
    assert_eq!(s.active_digits(), Some(&[0x20, 0x21, 0x22][..]));
    s.clear_shortcut_num();
    assert_eq!(s.shortcut_num(), -1);
    let no_empty = ItemSlot {
        digits: Some(Arc::new(ShortcutDigits {
            empty: None,
            ..(*digits).clone()
        })),
        ..ItemSlot::default()
    };
    assert_eq!(no_empty.active_digits(), None);
}

#[test]
fn the_cooldown_sprite_is_the_exact_one_based_step() {
    let sprites: Vec<u32> = (1..=10).map(|i| 0x0600_0000 + i).collect();
    let mut s = ItemSlot {
        cooldown_sprites: sprites,
        cooldown_step: 6,
        ..ItemSlot::default()
    };
    assert_eq!(s.active_cooldown_sprite(), 0, "an empty slot shows none");
    s.set_item(0x5001, Some(TextureRef::Runtime(99)), None, None);
    assert_eq!(s.active_cooldown_sprite(), 0x0600_0006);
    s.cooldown_step = 0;
    assert_eq!(s.active_cooldown_sprite(), 0);
}

#[test]
fn an_item_list_stamps_cooldown_and_empty_sprites_on_existing_and_future_cells() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let l = list(&mut ui, r, 0.0, 0.0, 32.0, 32.0);
    let sprites: Vec<u32> = (1..=10).collect();
    ui.with::<ItemList, _>(l, |w, ui| {
        w.set_cooldown_sprites(ui, &sprites);
        w.set_cell_empty_sprite(ui, 0x0600_4D20);
    });
    let first = ui.widget::<ItemList>(l).unwrap().item(0).unwrap();
    assert_eq!(
        ui.widget::<ItemSlot>(first).unwrap().cooldown_sprites,
        sprites
    );
    let c = ui.insert(Element::default(), ItemSlot::default());
    ui.with::<ItemList, _>(l, |w, ui| w.add_item(ui, l, c));
    let s = ui.widget::<ItemSlot>(c).unwrap();
    assert_eq!(s.cooldown_sprites, sprites);
    assert_eq!(s.empty_sprite, 0x0600_4D20);
}

#[test]
fn structure_meter_shows_only_below_full() {
    let mut s = ItemSlot::default();
    s.set_structure(5, 10);
    assert!((s.structure_fill - 0.5).abs() < 1e-6);
    s.set_structure(10, 10);
    assert!(s.structure_fill < 0.0);
    s.set_structure(0, 10);
    assert!(
        s.structure_fill.abs() < 1e-6,
        "a depleted bag keeps the rail"
    );
    s.set_structure(3, 0);
    assert!(s.structure_fill < 0.0);
}

// ── Drag-and-drop spine (DragDropSpineTests) ────────────────────────────

/// Answers drags over cells as told and records what the slots said.
#[derive(Debug, Default)]
struct Spy {
    acceptance: Option<DragAcceptance>,
    consume_press: bool,
    lifted: Vec<(ElementId, ItemDragPayload, bool)>,
    dropped: Vec<(ElementId, ItemDragPayload)>,
    clicks: u32,
    double_clicks: u32,
    right_clicks: u32,
    presses: u32,
}

impl PanelController for Spy {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        match message {
            ElementMessage::Pressed => {
                self.presses += 1;
                if self.consume_press
                    && let Some(s) = ui.widget_mut::<ItemSlot>(element)
                {
                    s.consume_primary_press();
                }
            }
            ElementMessage::Clicked { .. } => self.clicks += 1,
            ElementMessage::DoubleClicked => self.double_clicks += 1,
            ElementMessage::RightClicked => self.right_clicks += 1,
            ElementMessage::ItemDragLifted(p) => {
                let waiting = ui
                    .widget::<ItemSlot>(element)
                    .is_some_and(ItemSlot::waiting);
                self.lifted.push((element, p.clone(), waiting));
            }
            ElementMessage::ItemDragEntered(_) => {
                if let Some(a) = self.acceptance
                    && let Some(s) = ui.widget_mut::<ItemSlot>(element)
                {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) => self.dropped.push((element, p.clone())),
            _ => return false,
        }
        true
    }
}

struct Spine {
    host: UiHost,
    frame: ElementId,
    list: ElementId,
    cell: ElementId,
}

/// A 32-pixel list at (5, 5) of a draggable window at (10, 300), its cell
/// holding `item`, with a spy controller.
fn spine(item: u32, spy: Spy) -> Spine {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let frame = ui.add(
        r,
        window(10.0, 300.0, 200.0, 60.0),
        ac_ui::widgets::Panel::default(),
    );
    let l = list(ui, frame, 5.0, 5.0, 32.0, 32.0);
    let cell = ui.widget::<ItemList>(l).unwrap().item(0).unwrap();
    if item != 0 {
        ui.widget_mut::<ItemSlot>(cell).unwrap().set_item(
            item,
            Some(TextureRef::Runtime(0x99)),
            None,
            None,
        );
    }
    ui.register_window("w", frame, None, 0).unwrap();
    host.attach_controller("w", Box::new(spy)).unwrap();
    Spine {
        host,
        frame,
        list: l,
        cell,
    }
}

fn spy_of(host: &mut UiHost) -> SpyCounts {
    host.with_controller::<Spy, _>("w", |s, _| SpyCounts {
        lifted: s.lifted.clone(),
        dropped: s.dropped.clone(),
        clicks: s.clicks,
        double_clicks: s.double_clicks,
        right_clicks: s.right_clicks,
        presses: s.presses,
    })
    .unwrap()
}

#[derive(Debug, Clone)]
struct SpyCounts {
    lifted: Vec<(ElementId, ItemDragPayload, bool)>,
    dropped: Vec<(ElementId, ItemDragPayload)>,
    clicks: u32,
    double_clicks: u32,
    right_clicks: u32,
    presses: u32,
}

fn slot_of(host: &UiHost, cell: ElementId) -> &ItemSlot {
    host.root.widget::<ItemSlot>(cell).unwrap()
}

#[test]
fn an_empty_cell_has_no_payload_or_ghost_and_a_bound_cell_snapshots_its_fields() {
    let s = ItemSlot::default();
    assert!(s.payload(None).is_none());
    assert!(s.ghost(32.0, 32.0).is_none());
    let mut s = ItemSlot {
        slot_index: 4,
        source_kind: ItemDragSource::ShortcutBar,
        ..ItemSlot::default()
    };
    s.set_item(0x5001, Some(TextureRef::Runtime(0x99)), None, None);
    let p = s.payload(None).unwrap();
    assert_eq!(
        (p.obj_id, p.source_kind, p.source_slot),
        (0x5001, ItemDragSource::ShortcutBar, 4)
    );
    let g = s.ghost(32.0, 32.0).unwrap();
    assert_eq!(
        (g.texture, g.width, g.height),
        (TextureRef::Runtime(0x99), 32, 32)
    );
    // A dedicated underlay-free drag icon wins, at 32 px.
    s.set_item(
        0x5001,
        Some(TextureRef::Runtime(0x99)),
        Some(TextureRef::Runtime(0x77)),
        None,
    );
    let g = s.ghost(36.0, 36.0).unwrap();
    assert_eq!(
        (g.texture, g.width, g.height),
        (TextureRef::Runtime(0x77), 32, 32)
    );
    // Not a drag source: never a payload.
    s.allow_drag_source = false;
    assert!(s.payload(None).is_none());
}

#[test]
fn drag_enter_shows_the_owners_answer_and_leaving_resets_it() {
    for (answer, shown) in [
        (Some(DragAcceptance::Accept), DragAcceptance::Accept),
        (Some(DragAcceptance::Reject), DragAcceptance::Reject),
        (Some(DragAcceptance::None), DragAcceptance::None),
        // Nobody answers: refused, as a list with no drag handler.
        (None, DragAcceptance::Reject),
    ] {
        let mut s = spine(
            0x5001,
            Spy {
                acceptance: answer,
                ..Spy::default()
            },
        );
        let p = DragPayload::new(payload(0x6001));
        s.host
            .root
            .fire_event(kind::DRAG_ENTER, s.cell, Some(p.clone()));
        s.host.pump();
        assert_eq!(slot_of(&s.host, s.cell).drag_acceptance(), shown);
        s.host.root.fire_event(kind::DRAG_OVER, s.cell, Some(p));
        assert_eq!(
            slot_of(&s.host, s.cell).drag_acceptance(),
            DragAcceptance::None
        );
    }
}

#[test]
fn a_drop_reaches_the_owner_with_its_payload() {
    let mut s = spine(0x5001, Spy::default());
    let p = payload(0x6001);
    s.host.root.fire_event(
        kind::DROP_RELEASED,
        s.cell,
        Some(DragPayload::new(p.clone())),
    );
    s.host.pump();
    let spy = spy_of(&mut s.host);
    assert_eq!(spy.dropped, vec![(s.cell, p)]);
}

#[test]
fn a_drag_from_a_bound_cell_arms_lifts_and_ghosts_the_source_until_release() {
    let mut s = spine(0x5001, Spy::default());
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    assert_eq!(s.host.root.drag_source(), Some(s.cell));
    assert!(
        s.host
            .root
            .drag_payload()
            .and_then(|p| p.get::<ItemDragPayload>())
            .is_some()
    );
    let spy = spy_of(&mut s.host);
    assert_eq!(spy.lifted.len(), 1);
    // OpenAC's handler hears the lift before the source starts waiting;
    // here the message arrives after the input event, with the source
    // already waiting.
    assert!(spy.lifted[0].2);
    assert!(slot_of(&s.host, s.cell).waiting());
    // The window did not move.
    assert_eq!(s.host.root.element(s.frame).unwrap().left, 10.0);
    // The ghost was snapshotted when the drag began.
    s.host.root.widget_mut::<ItemSlot>(s.cell).unwrap().clear();
    let g = s.host.root.drag_ghost().unwrap();
    assert_eq!((g.texture, g.width), (TextureRef::Runtime(0x99), 32));
    s.host
        .root
        .widget_mut::<ItemSlot>(s.cell)
        .unwrap()
        .set_item(0x5001, Some(TextureRef::Runtime(0x99)), None, None);
    // Released over nothing: no drop, the lift stands, the source stops
    // waiting.
    s.host.mouse_up(MouseButton::Left, 600, 500, 0);
    assert!(spy_of(&mut s.host).dropped.is_empty());
    assert!(s.host.root.drag_source().is_none());
    assert!(!slot_of(&s.host, s.cell).waiting());
}

#[test]
fn a_shortcut_drag_does_not_ghost_its_source() {
    let mut s = spine(0x5001, Spy::default());
    s.host
        .root
        .widget_mut::<ItemSlot>(s.cell)
        .unwrap()
        .source_kind = ItemDragSource::ShortcutBar;
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    assert!(!slot_of(&s.host, s.cell).waiting());
}

#[test]
fn an_empty_cell_never_arms_and_its_window_moves_instead() {
    let mut s = spine(0, Spy::default());
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    assert!(s.host.root.drag_source().is_none());
    assert_eq!(s.host.root.element(s.frame).unwrap().left, 30.0);
}

#[test]
fn a_bound_cell_that_is_no_drag_source_takes_the_click_and_keeps_its_window() {
    let mut s = spine(0x5001, Spy::default());
    s.host
        .root
        .widget_mut::<ItemSlot>(s.cell)
        .unwrap()
        .allow_drag_source = false;
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    assert!(s.host.root.drag_source().is_none());
    assert_eq!(s.host.root.element(s.frame).unwrap().left, 10.0);
    s.host.mouse_up(MouseButton::Left, 40, 310, 0);
    assert_eq!(spy_of(&mut s.host).clicks, 1);
}

#[test]
fn a_press_is_heard_before_release_and_the_click_follows() {
    let mut s = spine(0x5001, Spy::default());
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    let spy = spy_of(&mut s.host);
    assert_eq!((spy.presses, spy.clicks), (1, 0));
    s.host.mouse_up(MouseButton::Left, 20, 310, 0);
    assert_eq!(spy_of(&mut s.host).clicks, 1);
}

#[test]
fn a_consumed_press_suppresses_the_click_double_click_and_drag() {
    let mut s = spine(
        0x5001,
        Spy {
            consume_press: true,
            ..Spy::default()
        },
    );
    s.host.tick(0.0, 1_000, None);
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_up(MouseButton::Left, 20, 310, 0);
    s.host.tick(0.0, 1_100, None);
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_up(MouseButton::Left, 20, 310, 0);
    let spy = spy_of(&mut s.host);
    assert_eq!((spy.clicks, spy.double_clicks), (0, 0));
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    assert!(s.host.root.drag_source().is_none());
}

#[test]
fn a_right_click_asks_for_examination_and_right_movement_cancels_it() {
    let mut s = spine(0x5001, Spy::default());
    s.host.mouse_down(MouseButton::Right, 20, 310, 0);
    s.host.mouse_up(MouseButton::Right, 20, 310, 0);
    assert_eq!(spy_of(&mut s.host).right_clicks, 1);
    s.host.mouse_down(MouseButton::Right, 20, 310, 0);
    s.host.mouse_move(24, 310);
    s.host.mouse_up(MouseButton::Right, 24, 310, 0);
    assert_eq!(spy_of(&mut s.host).right_clicks, 1);
    assert!(s.host.root.drag_source().is_none());
}

#[test]
fn a_completed_drag_is_no_click() {
    let mut s = spine(0x5001, Spy::default());
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    s.host.mouse_up(MouseButton::Left, 40, 310, 0);
    assert_eq!(spy_of(&mut s.host).clicks, 0);
}

#[test]
fn a_drag_survives_its_source_cell_being_flushed_until_release() {
    let mut s = spine(0x5001, Spy::default());
    s.host.mouse_down(MouseButton::Left, 20, 310, 0);
    s.host.mouse_move(40, 310);
    let l = s.list;
    s.host.root.with::<ItemList, _>(l, |w, ui| w.flush(ui));
    assert!(s.host.root.drag_payload().is_some());
    assert!(s.host.root.drag_ghost().is_some());
    s.host.mouse_up(MouseButton::Left, 600, 500, 0);
    let released = s.host.take_outputs().into_iter().any(|o| {
        matches!(o, UiOutput::DragReleasedOutsideUi { payload, .. }
            if payload.get::<ItemDragPayload>().is_some_and(|p| p.obj_id == 0x5001))
    });
    assert!(released);
    assert!(s.host.root.drag_payload().is_none());
}

// ── Inventory (InventoryControllerTests) ────────────────────────────────

#[test]
fn populate_fills_the_grid_with_loose_items_and_the_column_with_packs() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.contained(0xC, PLAYER, 2, ItemType::CONTAINER);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.count(inv.grid), 102);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    assert_eq!(inv.slot(inv.grid, 1).object_id, 0xB);
    assert_eq!(inv.slot(inv.grid, 2).object_id, 0);
    assert_eq!(inv.count(inv.containers), 7);
    assert_eq!(inv.slot(inv.containers, 0).object_id, 0xC);
    assert_eq!(inv.slot(inv.containers, 1).object_id, 0);
    // The grid is six columns of 32 px.
    let g = inv.host.root.widget::<ItemList>(inv.grid).unwrap();
    assert_eq!((g.columns, g.cell_width, g.cell_height), (6, 32.0, 32.0));
}

#[test]
fn equipped_items_stay_out_of_the_grid_and_the_column() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.get_mut(0xB).currently_equipped_location = EquipMask::HEAD_WEAR;
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    assert_eq!(inv.slot(inv.grid, 1).object_id, 0);
}

#[test]
fn a_new_item_shows_at_the_next_update_and_cells_carry_icons_and_captions() {
    let mut o = Objects::new();
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0);
    let mut sword = ClientObject::new(0xA);
    sword.name = "Sword".into();
    sword.icon_id = 0x0600_1234;
    o.add(sword);
    o.mv(0xA, PLAYER, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let s = inv.slot(inv.grid, 0);
    assert_eq!(s.object_id, 0xA);
    assert_eq!(s.icon, Some(TextureRef::Runtime(0x0600_1234)));
    assert_eq!(s.drag_icon, Some(TextureRef::Runtime(0x1600_1234)));
    assert_eq!(s.tooltip.as_deref(), Some("Sword"));
}

#[test]
fn burden_follows_the_wire_value_over_the_carried_sum_and_strength() {
    let mut o = Objects::new();
    let mut a = ClientObject::new(0xA);
    a.burden = 3000;
    o.add(a);
    o.mv(0xA, PLAYER, 0);
    o.player(102, 7);
    o.get_mut(PLAYER).properties.ints.insert(5, 7500);
    let mut inv = inventory_window(None, InventorySprites::default());
    let mut view = ItemsView::new(&o.t, PLAYER);
    view.strength = Some(100);
    inv.update(&view);
    let fill = inv
        .host
        .root
        .widget::<Meter>(inv.meter)
        .unwrap()
        .fill
        .unwrap();
    assert!(
        (fill - 0.16667).abs() < 1e-3,
        "7500 of 15000, a third: {fill}"
    );
    assert!(inv.host.root.widget::<Meter>(inv.meter).unwrap().vertical);
    assert_eq!(captions(&mut inv)[3], "50%");
    // A live update of the wire value.
    o.get_mut(PLAYER).properties.ints.insert(5, 9000);
    inv.update(&ItemsView {
        strength: Some(100),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(captions(&mut inv)[3], "60%");
    // The effective strength changes (an enchantment).
    o.get_mut(PLAYER).properties.ints.insert(5, 1600);
    inv.update(&ItemsView {
        strength: Some(10),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(captions(&mut inv)[3], "106%");
    inv.update(&ItemsView {
        strength: Some(20),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(captions(&mut inv)[3], "53%");
}

#[test]
fn captions_render_the_known_strings() {
    let o = Objects::new();
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView {
        owner_name: Some("Horan"),
        ..ItemsView::new(&o.t, PLAYER)
    });
    let c = captions(&mut inv);
    assert_eq!(c[0], "Inventory of Horan");
    assert_eq!(c[1], "Burden");
    assert_eq!(c[2], "Contents of Backpack");
}

// ── Augmented pack capacity (OpenAC v0.1.25, #195) ──────────────────────

fn rect(inv: &Inv, id: ElementId) -> (f32, f32) {
    let e = inv.host.root.element(id).unwrap();
    (e.top, e.height)
}

fn pack_cells_visible(inv: &Inv) -> Vec<bool> {
    let list = inv.host.root.widget::<ItemList>(inv.containers).unwrap();
    list.cells()
        .iter()
        .map(|c| inv.host.root.element(*c).unwrap().visible)
        .collect()
}

#[test]
fn an_augmented_pack_capacity_at_login_shows_an_eighth_slot_without_scrolling() {
    // AugmentedPackCapacityOnLogin_includesEmptyEighthSlot: the column grows to 288 pixels and nothing scrolls.
    let mut o = Objects::new();
    o.player(102, 8);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let list = inv.host.root.widget::<ItemList>(inv.containers).unwrap();
    assert_eq!(list.num_items(), 8);
    assert_eq!(list.scroll.content_height, 288);
    assert_eq!(list.scroll.scroll_y(), 0);
    let eighth = inv.cell(inv.containers, 7);
    assert_eq!(
        inv.host.root.widget::<ItemSlot>(eighth).unwrap().object_id,
        0
    );
    assert_eq!(rect(&inv, inv.containers).1, 288.0);
    assert_eq!(pack_cells_visible(&inv), [true; 8]);
}

#[test]
fn a_live_pack_capacity_update_adds_an_eighth_slot_and_a_refresh_keeps_it() {
    // LivePackCapacityUpdate_addsEighthSlotAndPreservesItOnRefresh.
    let mut o = Objects::new();
    o.player(102, 7);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let count = |inv: &Inv| {
        inv.host
            .root
            .widget::<ItemList>(inv.containers)
            .unwrap()
            .num_items()
    };
    assert_eq!(count(&inv), 7);
    assert!(o.t.update_int_property(PLAYER, 7, 8, &mut o.ev));
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(count(&inv), 8);
    let eighth = inv.cell(inv.containers, 7);
    assert_eq!(rect(&inv, inv.containers).1, 288.0);
    assert!(inv.host.root.element(eighth).unwrap().visible);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.cell(inv.containers, 7), eighth);
}

#[test]
fn an_augmented_pack_column_grows_the_window_frame_it_is_mounted_in() {
    // AugmentedPackColumn_growsTheWindowFrameItIsMountedIn.
    let mut frame = None;
    let mut o = Objects::new();
    o.player(102, 10);
    let mut inv = inventory_window_mounted(None, InventorySprites::default(), Some(&mut frame));
    let frame = frame.unwrap();
    let content = inv.host.root.parent(inv.containers).unwrap();
    let authored = inv.host.root.element(frame).unwrap().height;
    let chrome = authored - inv.host.root.element(content).unwrap().height;
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.host.root.capture_authored_anchor_baselines(frame);
    let extension = 10.0 * 36.0 - 252.0;
    let f = inv.host.root.element(frame).unwrap();
    assert_eq!(
        (f.min_height, f.height),
        (authored + extension, authored + extension)
    );
    let content_height = inv.host.root.element(content).unwrap().height;
    assert_eq!(
        content_height,
        f.height - chrome,
        "the contents fill the frame"
    );
    let (top, height) = rect(&inv, inv.containers);
    assert!(top + height <= content_height);
}

#[test]
fn an_augmented_pack_column_after_a_taller_saved_height_keeps_the_contents_filling_the_frame() {
    // AugmentedPackColumn_arrivingAfterATallerSavedHeight_keepsTheContentsFillingTheFrame.
    let mut frame = None;
    let mut o = Objects::new();
    o.player(102, 0);
    let mut inv = inventory_window_mounted(None, InventorySprites::default(), Some(&mut frame));
    let frame = frame.unwrap();
    let content = inv.host.root.parent(inv.containers).unwrap();
    let chrome = inv.host.root.element(frame).unwrap().height
        - inv.host.root.element(content).unwrap().height;
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.host.root.capture_authored_anchor_baselines(frame);
    // The saved height lands, and the capacity arrives before the next
    // layout pass.
    inv.host.root.element_mut(frame).unwrap().height = 651.0;
    inv.host.root.reset_anchor_capture(frame);
    o.get_mut(PLAYER).containers_capacity = 10;
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.host.root.capture_authored_anchor_baselines(frame);
    assert_eq!(inv.host.root.element(frame).unwrap().height, 651.0);
    assert_eq!(
        inv.host.root.element(content).unwrap().height,
        651.0 - chrome
    );
}

#[test]
fn the_close_button_closes_the_inventory_panel() {
    let o = Objects::new();
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let close = inv.close;
    inv.host.root.fire_event(kind::CLICK, close, None);
    inv.host.pump();
    let out = inv.outputs();
    assert_dbg_eq!(
        out,
        vec![PanelOutput::ClosePanel(ac_ui::panels::panel_id::INVENTORY)]
    );
}

#[test]
fn the_contents_scrollbar_drives_the_grid_and_a_refresh_keeps_the_offset() {
    let mut o = Objects::new();
    o.player(102, 7);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        inv.host
            .root
            .widget::<Scrollbar>(inv.scrollbar)
            .unwrap()
            .model,
        ScrollSource::Element(inv.grid)
    );
    let g = inv.grid;
    inv.host.root.with::<ItemList, _>(g, |l, ui| {
        l.scroll.set_scroll_y(40);
        l.layout_cells(ui, g);
    });
    o.get_mut(PLAYER).properties.ints.insert(5, 1000);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let c6 = inv.cell(inv.grid, 6);
    inv.host
        .root
        .with::<ItemList, _>(g, |l, ui| l.layout_cells(ui, g));
    assert_eq!(
        inv.host
            .root
            .widget::<ItemList>(inv.grid)
            .unwrap()
            .scroll
            .scroll_y(),
        40
    );
    assert_eq!(inv.host.root.element(c6).unwrap().top, -8.0);
}

#[test]
fn switching_packs_resets_the_contents_scroll_and_reclicking_keeps_it() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.bag(0xC1, 0, 102);
    o.bag(0xC2, 1, 102);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let g = inv.grid;
    let scroll = |inv: &mut Inv, y: i32| {
        inv.host
            .root
            .widget_mut::<ItemList>(g)
            .unwrap()
            .scroll
            .set_scroll_y(y);
    };
    let scroll_y = |inv: &Inv| {
        inv.host
            .root
            .widget::<ItemList>(g)
            .unwrap()
            .scroll
            .scroll_y()
    };
    // Reclicking the open main pack keeps it.
    scroll(&mut inv, 40);
    let top = inv.top;
    inv.click(top, 0);
    assert_eq!(scroll_y(&inv), 40);
    let containers = inv.containers;
    inv.click(containers, 0);
    assert_eq!(scroll_y(&inv), 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    scroll(&mut inv, 40);
    inv.click(containers, 1);
    assert_eq!(scroll_y(&inv), 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    scroll(&mut inv, 40);
    inv.click(top, 0);
    assert_eq!(scroll_y(&inv), 0);
}

#[test]
fn the_grid_pads_to_capacity_and_the_column_to_the_players_pack_slots() {
    let mut o = Objects::new();
    o.player(102, 3);
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xC, PLAYER, 1, ItemType::CONTAINER);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.count(inv.grid), 102);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    assert_eq!(inv.slot(inv.grid, 50).object_id, 0);
    assert_eq!(inv.count(inv.containers), 3);
    assert_eq!(inv.slot(inv.containers, 0).object_id, 0xC);
    assert_eq!(inv.slot(inv.containers, 2).object_id, 0);
}

#[test]
fn empty_sprites_and_drop_feedback_follow_each_lists_role() {
    let o = Objects::new();
    let mut inv = inventory_window(
        None,
        InventorySprites {
            contents_empty: 0x0600_4D20,
            side_bag_empty: 0x0600_5D9C,
            main_pack_empty: 0x0600_5D9C,
        },
    );
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(inv.slot(inv.grid, 0).empty_sprite, 0x0600_4D20);
    assert_eq!(inv.slot(inv.containers, 0).empty_sprite, 0x0600_5D9C);
    assert_eq!(inv.slot(inv.top, 0).empty_sprite, 0x0600_5D9C);
    assert_eq!(inv.slot(inv.grid, 0).drag_accept_sprite, 0x0600_11F9);
    assert_eq!(inv.slot(inv.containers, 0).drag_accept_sprite, 0x0600_11F7);
    assert_eq!(inv.slot(inv.top, 0).drag_accept_sprite, 0x0600_11F7);
}

#[test]
fn clicking_a_side_pack_uses_it_and_swaps_the_grid_to_its_contents() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.bag(0xC, 1, 24);
    o.contained(0xB1, 0xC, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let containers = inv.containers;
    inv.click(containers, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let out = inv.outputs();
    assert_eq!(uses(&out), vec![0xC]);
    // The session's pickups name the pack shown open (OpenAC
    // `InventoryController.CurrentOpenContainerId`, read through
    // `DeferredInventoryContainerSource` as the preferred pack).
    let open_packs = |out: &[PanelOutput]| {
        sessions(out)
            .into_iter()
            .filter_map(|c| match c {
                SessionCommand::Item(ItemCommand::SetOpenPack(p)) => Some(p),
                _ => None,
            })
            .collect::<Vec<_>>()
    };
    assert_eq!(open_packs(&out), [0xC]);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xB1);
    assert_eq!(inv.count(inv.grid), 24);
    assert_eq!(captions(&mut inv)[2], "Contents of Pack C");
    // The pack carries the open triangle and the selection square.
    assert!(inv.slot(inv.containers, 0).is_open_container);
    assert!(inv.slot(inv.containers, 0).selected);
    // Back to the main pack: no use for the player.
    let top = inv.top;
    inv.click(top, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let out = inv.outputs();
    assert!(uses(&out).is_empty());
    assert_eq!(open_packs(&out), [PLAYER]);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    assert_eq!(inv.count(inv.grid), 102);
}

#[test]
fn a_carried_pack_used_from_anywhere_opens_in_the_window_without_a_use() {
    // OpenAC InventoryControllerTests
    // .UsingTheMainPack_showsTheInventoryOnTheMainPack_withoutAUse: a pack
    // used from the hotbar opens in this window, the main pack when it is
    // the player; the window itself asks the server for nothing.
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.bag(0xC, 1, 24);
    o.contained(0xB1, 0xC, 0, ItemType::NONE);
    o.contained(0xD, 0x7000_0001, 0, ItemType::CONTAINER); // someone else's
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let open = |inv: &mut Inv, pack: u32| {
        let view = ItemsView::new(&o.t, PLAYER);
        let opened = inv
            .host
            .with_controller::<InventoryController, _>("inventory", |c, ui| {
                c.open_contained_container(ui, pack, &view)
            })
            .unwrap();
        inv.update(&view);
        opened
    };
    let open_packs = |out: &[PanelOutput]| {
        sessions(out)
            .into_iter()
            .filter_map(|c| match c {
                SessionCommand::Item(ItemCommand::SetOpenPack(p)) => Some(p),
                _ => None,
            })
            .collect::<Vec<_>>()
    };

    assert!(open(&mut inv, 0xC));
    let out = inv.outputs();
    assert!(uses(&out).is_empty());
    assert_eq!(open_packs(&out), [0xC]);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xB1);
    assert!(inv.slot(inv.containers, 0).is_open_container);

    assert!(open(&mut inv, PLAYER));
    let out = inv.outputs();
    assert!(uses(&out).is_empty());
    assert_eq!(open_packs(&out), [PLAYER]);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    assert_eq!(inv.count(inv.grid), 102);

    // Not a pack the player carries, or not a pack at all: nothing opens.
    assert!(!open(&mut inv, 0xD));
    assert!(!open(&mut inv, 0xA));
    assert!(inv.outputs().is_empty());
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
}

#[test]
fn switching_between_two_packs_uses_each() {
    let mut o = Objects::new();
    o.bag(0xC1, 0, 24);
    o.bag(0xC2, 1, 24);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let c = inv.containers;
    inv.click(c, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.click(c, 1);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(uses(&inv.outputs()), vec![0xC1, 0xC2]);
}

#[test]
fn double_clicking_an_owned_pack_opens_it_once_and_never_uses_it_again() {
    let mut o = Objects::new();
    o.bag(0xC, 0, 24);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let c = inv.containers;
    inv.fire(c, 0, kind::MOUSE_DOWN);
    inv.fire(c, 0, kind::CLICK);
    inv.fire(c, 0, kind::MOUSE_DOWN);
    inv.fire(c, 0, kind::CLICK);
    inv.fire(c, 0, kind::DOUBLE_CLICK);
    assert_eq!(uses(&inv.outputs()), vec![0xC]);
    assert!(inv.slot(inv.containers, 0).is_open_container);
}

#[test]
fn pressing_a_grid_item_moves_the_square_at_once_and_sends_only_the_selection() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let g = inv.grid;
    inv.fire(g, 0, kind::MOUSE_DOWN);
    assert!(inv.slot(inv.grid, 0).selected);
    assert!(inv.slot(inv.top, 0).is_open_container);
    let out = inv.outputs();
    assert_dbg_eq!(out, vec![PanelOutput::SelectObject(0xA)]);
    // The runtime's selection moving elsewhere moves the square.
    inv.update(&ItemsView {
        selected: Some(0xA),
        ..ItemsView::new(&o.t, PLAYER)
    });
    inv.update(&ItemsView {
        selected: Some(0xB),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(!inv.slot(inv.grid, 0).selected);
    assert!(inv.slot(inv.grid, 1).selected);
}

#[test]
fn right_clicking_a_grid_item_selects_and_appraises_it() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let g = inv.grid;
    inv.fire(g, 0, kind::RIGHT_CLICK);
    let out = inv.outputs();
    assert_dbg_eq!(
        out,
        vec![
            PanelOutput::SelectObject(0xA),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(0xA)
            ))),
        ]
    );
    assert!(inv.slot(inv.grid, 0).selected);
}

#[test]
fn double_clicking_a_grid_item_uses_it() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.outputs();
    let g = inv.grid;
    inv.fire(g, 0, kind::DOUBLE_CLICK);
    assert_eq!(uses(&inv.outputs()), vec![0xA]);
}

#[test]
fn a_target_mode_takes_the_press_and_suppresses_the_sources_square() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        selected: Some(0xA),
        target_mode: true,
        target_source: Some(0xA),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    assert!(
        !inv.slot(inv.grid, 0).selected,
        "the pending source shows no square"
    );
    inv.outputs();
    let g = inv.grid;
    inv.fire(g, 1, kind::MOUSE_DOWN);
    assert!(inv.slot(inv.grid, 1).primary_press_consumed());
    let out = inv.outputs();
    assert_dbg_eq!(
        out,
        vec![PanelOutput::Session(SessionCommand::Item(
            ItemCommand::OfferTarget(ObjectRef::Guid(0xB))
        ))]
    );
    // The press cannot become a drag.
    let c = inv.cell(inv.grid, 1);
    assert!(
        inv.host
            .root
            .widget::<ItemSlot>(c)
            .unwrap()
            .payload(Some(c))
            .is_none()
    );
}

#[test]
fn the_main_pack_cell_is_open_with_the_fixed_pack_icon_and_a_fullness_meter() {
    let mut o = Objects::new();
    o.player(102, 7);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let main = inv.slot(inv.top, 0);
    assert_eq!(main.object_id, PLAYER);
    assert!(main.is_open_container);
    assert_eq!(
        main.icon,
        Some(TextureRef::Runtime(inventory::PLAYER_PACK_BASE_ICON))
    );
    assert!(main.capacity_fill < 0.0, "hidden while empty");
    assert!(
        inv.icons
            .asked
            .contains(&(ItemType::CONTAINER, inventory::PLAYER_PACK_BASE_ICON))
    );
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert!((inv.slot(inv.top, 0).capacity_fill - 1.0 / 102.0).abs() < 1e-6);
}

#[test]
fn a_side_pack_cell_shows_its_fullness_and_loose_items_show_none() {
    let mut o = Objects::new();
    o.bag(0xC, 0, 24);
    o.contained(0xA, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert!(inv.slot(inv.containers, 0).capacity_fill < 0.0);
    assert!(inv.slot(inv.grid, 0).capacity_fill < 0.0);
    for i in 0..6 {
        o.contained(0xB0 + i, 0xC, i as i32, ItemType::NONE);
    }
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert!((inv.slot(inv.containers, 0).capacity_fill - 0.25).abs() < 1e-6);
}

#[test]
fn cells_carry_the_sale_and_trade_markers_and_the_shortcut_number() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.get_mut(0xA).sell_state = 1;
    o.get_mut(0xB).trade_state = 1;
    let regular: Vec<u32> = (0x101..=0x109).collect();
    let ghosted: Vec<u32> = (0x201..=0x209).collect();
    let digits = Arc::new(ShortcutDigits {
        regular: regular.clone(),
        ghosted: ghosted.clone(),
        empty: None,
    });
    let mut inv = inventory_window(Some(digits), InventorySprites::default());
    let mut shortcuts = ShortcutStore::new();
    shortcuts.load([ShortcutEntry {
        index: 2,
        object_id: 0xA,
        spell_id: 0,
    }]);
    inv.update(&ItemsView {
        shortcuts: Some(&shortcuts),
        ..ItemsView::new(&o.t, PLAYER)
    });
    let a = inv.slot(inv.grid, 0);
    assert!(a.show_sell_overlay && !a.show_trade_overlay);
    assert_eq!(a.sell_overlay_sprite, 0x0600_12D9);
    assert_eq!(a.shortcut_num(), 2);
    assert!(!a.shortcut_ghosted());
    let b = inv.slot(inv.grid, 1);
    assert!(b.show_trade_overlay && !b.show_sell_overlay);
    assert_eq!(b.trade_overlay_sprite, 0x0600_1DAE);
    assert_eq!(b.shortcut_num(), -1);
    // The shortcut moves; magic mode ghosts the number.
    shortcuts.load([ShortcutEntry {
        index: 4,
        object_id: 0xB,
        spell_id: 0,
    }]);
    inv.update(&ItemsView {
        shortcuts: Some(&shortcuts),
        combat_mode: ac_core::combat::CombatMode::MAGIC,
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(inv.slot(inv.grid, 0).shortcut_num(), -1);
    assert_eq!(inv.slot(inv.grid, 1).shortcut_num(), 4);
    assert!(inv.slot(inv.grid, 1).shortcut_ghosted());
    // A bottom-row shortcut carries no number.
    shortcuts.load([ShortcutEntry {
        index: 9,
        object_id: 0xA,
        spell_id: 0,
    }]);
    inv.update(&ItemsView {
        shortcuts: Some(&shortcuts),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(inv.slot(inv.grid, 0).shortcut_num(), -1);
}

fn drop_on(
    inv: &mut Inv,
    list: ElementId,
    i: usize,
    p: ItemDragPayload,
    view: &ItemsView<'_>,
) -> Vec<PanelOutput> {
    inv.outputs();
    inv.fire_payload(list, i, kind::DROP_RELEASED, p);
    inv.update(view);
    inv.outputs()
}

fn drag_over(
    inv: &mut Inv,
    list: ElementId,
    i: usize,
    p: ItemDragPayload,
    view: &ItemsView<'_>,
) -> DragAcceptance {
    inv.fire_payload(list, i, kind::DRAG_ENTER, p);
    inv.update(view);
    inv.slot(list, i).drag_acceptance()
}

#[test]
fn a_drop_from_elsewhere_on_the_grid_lands_left_of_the_item_or_appends() {
    // OpenAC #250: the hovered cell's slot whatever the source.
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.bag(0xC0, 2, 24);
    o.contained(0xC1, 0xC0, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    // Onto B: A C1 B. Onto an empty cell: A B C1.
    for (cell, slot) in [(1, 1), (10, 2)] {
        let out = drop_on(&mut inv, g, cell, payload(0xC1), &view);
        assert_dbg_eq!(moves(&out), vec![(0xC1u32, ObjectRef::Player, slot, 0u32)]);
    }
}

#[test]
fn an_equipped_item_dropped_on_the_grid_lands_left_of_the_item_or_appends() {
    // OpenAC #250: an equipped shield went to the pack's first slot.
    const SHIELD: u32 = 0xE0;
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.contained(0xD, PLAYER, 2, ItemType::NONE);
    o.equipped(SHIELD, EquipMask::SHIELD);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    for (cell, slot) in [(2, 2), (9, 3)] {
        let p = ItemDragPayload::new(SHIELD, ItemDragSource::Equipment, 0);
        let out = drop_on(&mut inv, g, cell, p, &view);
        assert_dbg_eq!(moves(&out), vec![(SHIELD, ObjectRef::Player, slot, 0u32)]);
    }
}

#[test]
fn a_reorder_inside_the_grid_lands_left_of_the_item_dropped_on_or_appends() {
    // OpenAC #249: the server takes the item out of its old slot before it
    // inserts it, so an item moving later asks for one slot less.
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    o.contained(0xD, PLAYER, 2, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    // A B D -> B A D
    let out = drop_on(&mut inv, g, 2, payload(0xA), &view);
    assert_dbg_eq!(moves(&out), vec![(0xAu32, ObjectRef::Player, 1i32, 0u32)]);
    // A B D -> B D A
    let out = drop_on(&mut inv, g, 20, payload(0xA), &view);
    assert_dbg_eq!(moves(&out), vec![(0xAu32, ObjectRef::Player, 2i32, 0u32)]);
    // A B D -> A D B: moving earlier takes the hovered slot.
    let out = drop_on(&mut inv, g, 1, payload(0xD), &view);
    assert_dbg_eq!(moves(&out), vec![(0xDu32, ObjectRef::Player, 1i32, 0u32)]);
}

#[test]
fn a_reorder_onto_itself_or_its_right_neighbour_sends_nothing() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    for cell in [0, 1] {
        let out = drop_on(&mut inv, g, cell, payload(0xA), &view);
        assert!(sessions(&out).is_empty(), "cell {cell}: {out:?}");
    }
}

#[test]
fn a_side_pack_moved_down_the_column_lands_above_the_pack_dropped_on() {
    let mut o = Objects::new();
    o.bag(0xC1, 0, 24);
    o.bag(0xC2, 1, 24);
    o.bag(0xC3, 2, 24);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    // C1 C2 C3 -> C2 C1 C3
    let out = drop_on(&mut inv, c, 2, payload(0xC1), &view);
    assert_dbg_eq!(moves(&out), vec![(0xC1u32, ObjectRef::Player, 1i32, 0u32)]);
}

#[test]
fn a_grid_item_dropped_on_a_pack_icon_goes_to_that_packs_top() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.bag(0xC0, 1, 24);
    o.contained(0xC1, 0xC0, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xAu32, ObjectRef::Guid(0xC0), 0i32, 0u32)]
    );
}

#[test]
fn a_selected_partial_stack_splits_and_others_move_whole() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.get_mut(0xA).stack_size = 10;
    o.get_mut(0xA).stack_size_max = 100;
    o.bag(0xC0, 1, 24);
    let mut split = StackSplitQuantity::new();
    split.reset(10, Some(4));
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        selected: Some(0xA),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xAu32, ObjectRef::Guid(0xC0), 0i32, 4u32)]
    );
    // Another object is selected: the whole stack moves.
    let view = ItemsView {
        selected: Some(0xC0),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xAu32, ObjectRef::Guid(0xC0), 0i32, 0u32)]
    );
}

// The merge moves the poured stack's shortcut in the runtime (OpenAC
// issue #219), not here: the panel only selects the stack it
// joined.
#[test]
fn a_matching_stack_dropped_on_a_stack_merges_and_selects_the_joined_stack() {
    let mut o = Objects::new();
    for (id, slot, size) in [(0xA, 0, 5), (0xB, 1, 20)] {
        o.contained(id, PLAYER, slot, ItemType::NONE);
        let s = o.get_mut(id);
        s.weenie_class_id = 273;
        s.stack_size = size;
        s.stack_size_max = 100;
    }
    o.contained(0xD, PLAYER, 2, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    // The session merges, or makes the plain drop when its own copy of
    // the stacks no longer allows the merge (here none: A already sits
    // left of B).
    let out = drop_on(&mut inv, g, 1, payload(0xA), &view);
    assert_dbg_eq!(
        out,
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropOnStack {
                item: ObjectRef::Guid(0xA),
                target: ObjectRef::Guid(0xB),
                merge_amount: 5,
                container: ObjectRef::Player,
                slot: None,
                amount: 0,
                pickup: false,
            })),
            PanelOutput::SelectObject(0xB),
        ]
    );
    // A matching stack from the ground merges the same way (OpenAC
    // `TryMergeStacks` asks nothing of where the stack lies); its plain
    // drop is the pickup left of B.
    let mut loose = ClientObject::new(0xE);
    loose.weenie_class_id = 273;
    loose.stack_size = 3;
    loose.stack_size_max = 100;
    o.add(loose);
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let out = drop_on(
        &mut inv,
        g,
        1,
        ItemDragPayload::new(0xE, ItemDragSource::Ground, 0),
        &view,
    );
    assert_dbg_eq!(
        out,
        vec![
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropOnStack {
                item: ObjectRef::Guid(0xE),
                target: ObjectRef::Guid(0xB),
                merge_amount: 3,
                container: ObjectRef::Player,
                slot: Some(1),
                amount: 0,
                pickup: true,
            })),
            PanelOutput::SelectObject(0xB),
        ]
    );
    // A different kind falls through to the ordinary insert: left of D,
    // its old slot closing up first.
    let out = drop_on(&mut inv, g, 2, payload(0xA), &view);
    assert_dbg_eq!(moves(&out), vec![(0xAu32, ObjectRef::Player, 1i32, 0u32)]);
}

#[test]
fn over_a_full_pack_icon_the_drag_is_refused_but_the_grid_accepts() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, 1);
    o.contained(0xE, 0xC, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let (c, g) = (inv.containers, inv.grid);
    assert_eq!(
        drag_over(&mut inv, c, 0, payload(0xA), &view),
        DragAcceptance::Reject
    );
    assert_eq!(
        drag_over(&mut inv, g, 3, payload(0xA), &view),
        DragAcceptance::Accept
    );
    // An alias (a shortcut, or a salvage, vendor or house window cell) gets no answer.
    for source in [
        ItemDragSource::ShortcutBar,
        ItemDragSource::Salvage,
        ItemDragSource::Vendor,
    ] {
        let alias = ItemDragPayload::new(0xA, source, 0);
        assert_eq!(
            drag_over(&mut inv, g, 3, alias, &view),
            DragAcceptance::None,
            "{source:?}"
        );
    }
}

#[test]
fn a_thing_in_the_side_column_that_carries_nothing_refuses() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    let mut focus = ClientObject::new(0xF);
    focus.container_type_hint = 2;
    o.add(focus);
    o.mv(0xF, PLAYER, 1);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    assert_eq!(inv.slot(inv.containers, 0).object_id, 0xF);
    let c = inv.containers;
    assert_eq!(
        drag_over(&mut inv, c, 0, payload(0xA), &view),
        DragAcceptance::Reject
    );
}

#[test]
fn a_drop_on_a_full_main_pack_goes_to_the_first_side_pack_with_room() {
    let mut o = Objects::new();
    o.player(1, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, 2);
    o.bag(0xD, 2, 2);
    o.contained(0xB, 0xD, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    assert_eq!(
        drag_over(&mut inv, g, 0, payload(0xB), &view),
        DragAcceptance::Accept
    );
    let out = drop_on(&mut inv, g, 0, payload(0xB), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xBu32, ObjectRef::Guid(0xC), 0i32, 0u32)]
    );
    assert!(notices(&out).is_empty());
}

#[test]
fn a_grid_drop_that_falls_through_to_a_side_pack_asks_for_the_grid_slot() {
    // The full main pack's grid holds A and E; B dropped on E (slot 1) or
    // on A (slot 0) goes to the side pack 0xC with the slot the grid
    // named, not its top.
    let mut o = Objects::new();
    o.player(2, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.contained(0xE, PLAYER, 1, ItemType::MISC);
    o.bag(0xC, 2, 5);
    o.bag(0xD, 3, 5);
    o.contained(0xB, 0xD, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    for (cell, slot) in [(1, 1), (0, 0)] {
        let out = drop_on(&mut inv, g, cell, payload(0xB), &view);
        assert_dbg_eq!(
            moves(&out),
            vec![(0xBu32, ObjectRef::Guid(0xC), slot, 0u32)]
        );
    }
}

#[test]
fn a_drop_when_every_pack_is_full_says_the_backpack_is_full_and_sends_nothing() {
    let mut o = Objects::new();
    o.player(1, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, 1);
    o.contained(0xE, 0xC, 0, ItemType::MISC);
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    chest.items_capacity = 10;
    o.add(chest);
    o.contained(0xB, CHEST, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    let ground = ItemDragPayload::new(0xB, ItemDragSource::Ground, 0);
    assert_eq!(
        drag_over(&mut inv, g, 0, ground.clone(), &view),
        DragAcceptance::Reject
    );
    let out = drop_on(&mut inv, g, 0, ground, &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec!["Backpack is completely full!".to_owned()]
    );
}

/// A chest item about to be dropped on a pack's icon: the player, 0xA in
/// the main pack (room for `main_capacity`), and the side pack 0xC (room
/// for `bag_capacity`, holding 0xE when `bag_holds_one`).
fn pack_icon_drop(
    main_capacity: i32,
    bag_capacity: i32,
    bag_holds_one: bool,
) -> (Objects, Inv, ItemDragPayload) {
    let mut o = Objects::new();
    o.player(main_capacity, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, bag_capacity);
    if bag_holds_one {
        o.contained(0xE, 0xC, 0, ItemType::MISC);
    }
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    chest.items_capacity = 10;
    o.add(chest);
    o.contained(0xB, CHEST, 0, ItemType::MISC);
    let inv = inventory_window(None, InventorySprites::default());
    (o, inv, ItemDragPayload::new(0xB, ItemDragSource::Ground, 0))
}

#[test]
fn a_drop_on_the_full_main_pack_icon_goes_into_the_first_side_pack_with_room() {
    // GitHub #24: looting onto the main pack's icon with the main pack
    // full and a side pack empty sent nothing and said nothing.
    let (o, mut inv, ground) = pack_icon_drop(1, 5, false);
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let top = inv.top;
    assert_eq!(inv.slot(top, 0).object_id, PLAYER);
    // Held over the icon the answer is the main pack's own: it has no
    // free slot, so the refusal shows although letting go finds room.
    assert_eq!(
        drag_over(&mut inv, top, 0, ground.clone(), &view),
        DragAcceptance::Reject
    );
    let out = drop_on(&mut inv, top, 0, ground, &view);
    assert_dbg_eq!(
        sessions(&out),
        vec![SessionCommand::Item(ItemCommand::DropInto {
            item: ObjectRef::Guid(0xB),
            container: ObjectRef::Guid(0xC),
            slot: 0,
            amount: 0,
            pickup: true,
            join_stack: false,
        })]
    );
    assert!(notices(&out).is_empty());
}

#[test]
fn a_drop_on_a_full_side_pack_icon_says_that_pack_is_full_and_sends_nothing() {
    // The main pack has room, but a drop on a pack's icon looks only in
    // that pack (and the packs inside it).
    let (o, mut inv, ground) = pack_icon_drop(5, 1, true);
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let packs = inv.containers;
    assert_eq!(inv.slot(packs, 0).object_id, 0xC);
    let out = drop_on(&mut inv, packs, 0, ground, &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec!["The Pack C is completely full!".to_owned()]
    );
}

/// The player's own stack 0xB of 10 in the side pack 0xC (room for one,
/// so full), the main pack full with 0xA, and the empty side pack 0xD.
fn own_stack_in_a_full_side_pack() -> Objects {
    let mut o = Objects::new();
    o.player(1, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, 1);
    o.bag(0xD, 2, 2);
    stack(&mut o, 0xB, 0xC, 0, 10, 273);
    o
}

#[test]
fn an_own_stack_dropped_on_the_full_main_pack_icon_stays_in_its_full_side_pack() {
    // The whole stack counts as fitting in the pack that already holds
    // it, so the search stops there: it moves to the top of that pack.
    let o = own_stack_in_a_full_side_pack();
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let top = inv.top;
    let out = drop_on(&mut inv, top, 0, payload(0xB), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xBu32, ObjectRef::Guid(0xC), 0i32, 0u32)]
    );
    assert!(notices(&out).is_empty());
}

#[test]
fn over_the_icon_of_its_own_full_pack_a_whole_stack_is_refused() {
    // The icon answers by the pack's free item slots alone: the stack
    // already inside does not make the full pack take it.
    let o = own_stack_in_a_full_side_pack();
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    assert_eq!(inv.slot(c, 0).object_id, 0xC);
    assert_eq!(
        drag_over(&mut inv, c, 0, payload(0xB), &view),
        DragAcceptance::Reject
    );
    assert_eq!(
        drag_over(&mut inv, c, 1, payload(0xB), &view),
        DragAcceptance::Accept
    );
}

#[test]
fn part_of_an_own_stack_dropped_on_the_full_main_pack_icon_goes_to_a_pack_with_room() {
    // Part of a stack needs a slot of its own, even in the pack holding
    // the rest: the full side pack is passed over for the next one.
    let o = own_stack_in_a_full_side_pack();
    let mut split = StackSplitQuantity::new();
    split.reset(10, Some(4));
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        selected: Some(0xB),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let top = inv.top;
    let out = drop_on(&mut inv, top, 0, payload(0xB), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xBu32, ObjectRef::Guid(0xD), 0i32, 4u32)]
    );
    assert!(notices(&out).is_empty());
}

#[test]
fn part_of_a_stack_dropped_on_its_full_open_side_pack_says_that_pack_is_full() {
    // A drop on the grid looks for room from the pack shown open: a side
    // pack holds no packs, so a full one has nowhere else to look. The
    // main pack is full and the side pack 0xD has room, but neither is
    // where the drop looks.
    let mut o = Objects::new();
    o.player(1, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC, 1, 2);
    o.bag(0xD, 2, 2);
    stack(&mut o, 0xB, 0xC, 0, 10, 273);
    o.contained(0xE, 0xC, 1, ItemType::MISC);
    let mut split = StackSplitQuantity::new();
    split.reset(10, Some(4));
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        selected: Some(0xB),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let containers = inv.containers;
    assert_eq!(inv.slot(containers, 0).object_id, 0xC);
    inv.click(containers, 0);
    inv.update(&view);
    inv.outputs();
    let g = inv.grid;
    assert_eq!(inv.slot(g, 0).object_id, 0xB);
    // Opening the pack selected it; select the stack again so the split
    // quantity applies to it.
    inv.click(g, 0);
    inv.update(&view);
    inv.outputs();
    assert_eq!(
        drag_over(&mut inv, g, 1, payload(0xB), &view),
        DragAcceptance::Reject
    );
    let out = drop_on(&mut inv, g, 1, payload(0xB), &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec!["The Pack C is completely full!".to_owned()]
    );
}

#[test]
fn a_stack_in_the_full_main_pack_dropped_on_its_icon_moves_whole_or_splits_elsewhere() {
    let mut o = Objects::new();
    o.player(1, 7);
    stack(&mut o, 0xB, PLAYER, 0, 10, 273);
    o.bag(0xC, 1, 2);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let top = inv.top;
    // The whole stack already fits where it is: it moves to the top.
    let out = drop_on(&mut inv, top, 0, payload(0xB), &view);
    assert_dbg_eq!(moves(&out), vec![(0xBu32, ObjectRef::Player, 0i32, 0u32)]);
    assert!(notices(&out).is_empty());
    // Part of it needs a free slot: the first side pack with room.
    let mut split = StackSplitQuantity::new();
    split.reset(10, Some(4));
    let view = ItemsView {
        selected: Some(0xB),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let out = drop_on(&mut inv, top, 0, payload(0xB), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xBu32, ObjectRef::Guid(0xC), 0i32, 4u32)]
    );
    assert!(notices(&out).is_empty());
}

#[test]
fn a_drop_on_the_main_pack_icon_when_every_pack_is_full_says_the_backpack_is_full() {
    let (o, mut inv, ground) = pack_icon_drop(1, 1, true);
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let top = inv.top;
    let out = drop_on(&mut inv, top, 0, ground, &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec!["Backpack is completely full!".to_owned()]
    );
}

#[test]
fn a_ground_item_dropped_on_the_grid_is_picked_up() {
    let mut o = Objects::new();
    o.player(102, 7);
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    chest.items_capacity = 10;
    o.add(chest);
    o.contained(0xB, CHEST, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    let out = drop_on(
        &mut inv,
        g,
        0,
        ItemDragPayload::new(0xB, ItemDragSource::Ground, 0),
        &view,
    );
    assert_dbg_eq!(
        sessions(&out),
        vec![SessionCommand::Item(ItemCommand::DropInto {
            item: ObjectRef::Guid(0xB),
            container: ObjectRef::Player,
            slot: 0,
            amount: 0,
            pickup: true,
            join_stack: false,
        })]
    );
}

#[test]
fn a_selected_ground_stack_dropped_on_the_grid_splits_by_the_chosen_quantity() {
    // InventoryController.HandleDropRelease: GetObjectSplitSize for any
    // source, then TrySplitToContainer when it is short of the stack
    // (PendingPickupRejectsPartialGroundSplitBeforeWireDispatch's split).
    let mut o = Objects::new();
    o.player(102, 7);
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    chest.items_capacity = 10;
    o.add(chest);
    o.contained(0xB, CHEST, 0, ItemType::MISC);
    o.get_mut(0xB).stack_size = 10;
    o.get_mut(0xB).stack_size_max = 100;
    let mut split = StackSplitQuantity::new();
    split.reset(10, Some(1));
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        selected: Some(0xB),
        split: Some(&split),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let g = inv.grid;
    let out = drop_on(
        &mut inv,
        g,
        4,
        ItemDragPayload::new(0xB, ItemDragSource::Ground, 0),
        &view,
    );
    assert_dbg_eq!(
        sessions(&out),
        vec![SessionCommand::Item(ItemCommand::DropInto {
            item: ObjectRef::Guid(0xB),
            container: ObjectRef::Player,
            slot: 0,
            amount: 1,
            pickup: true,
            join_stack: false,
        })]
    );
}

fn placement(item: u32, container: u32, slot: i32) -> ac_runtime::inventory::PendingPlacement {
    ac_runtime::inventory::PendingPlacement {
        token: 7,
        item_id: item,
        container_id: container,
        placement: slot,
    }
}

#[test]
fn an_item_on_its_way_in_shows_waiting_at_its_slot_until_the_server_answers() {
    // LootDrop_InsertsWaitingProjectionAtChosenSlotUntilServerConfirms: the
    // loot sits at the top of the grid, waiting, the pack's own items after
    // it; once the session's placement ends it shows where the server put
    // it (here: nowhere yet, it is still in the chest).
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    o.add(chest);
    o.contained(0x70, CHEST, 0, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        pending_placement: Some(placement(0x70, PLAYER, 0)),
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let g = inv.grid;
    assert_eq!(
        (0..3).map(|i| inv.slot(g, i).object_id).collect::<Vec<_>>(),
        [0x70, 0xA, 0xB]
    );
    assert!(inv.slot(g, 0).waiting());
    assert!(!inv.slot(g, 1).waiting());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(
        (0..3).map(|i| inv.slot(g, i).object_id).collect::<Vec<_>>(),
        [0xA, 0xB, 0]
    );
    // A pack on its way to the player shows in the pack column instead,
    // and one for a pack not shown shows nowhere.
    o.bag(0xC0, 2, 24);
    o.mv(0xC0, CHEST, 1);
    inv.update(&ItemsView {
        pending_placement: Some(placement(0xC0, PLAYER, 0)),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(inv.slot(inv.containers, 0).object_id, 0xC0);
    assert!(inv.slot(inv.containers, 0).waiting());
    inv.update(&ItemsView {
        pending_placement: Some(placement(0x70, 0xC1, 0)),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_eq!(inv.slot(g, 0).object_id, 0xA);
}

#[test]
fn a_drop_on_the_grid_while_an_item_is_on_its_way_into_it_is_refused_by_name() {
    // DirectPickupThenGroundDragAcceptsHoverButRejectsTheSecondRequest: the
    // drag is accepted, the drop sends nothing and says "Already
    // attempting to place that item here" (an unnamed item); a drop on the
    // pack column falls to the busy notice instead
    // (PendingContentsProjectionUsesGenericGlobalMessageOnBagColumnDrop).
    let mut o = Objects::new();
    o.player(102, 7);
    o.contained(0xA, PLAYER, 0, ItemType::MISC);
    o.bag(0xC0, 1, 24);
    const CHEST: u32 = 0x7000_0001;
    let mut chest = ClientObject::new(CHEST);
    chest.item_type = ItemType::CONTAINER;
    chest.items_capacity = 10;
    o.add(chest);
    o.contained(0x70, CHEST, 0, ItemType::MISC);
    o.contained(0x71, CHEST, 1, ItemType::MISC);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        pending_placement: Some(placement(0x70, PLAYER, 0)),
        can_make_request: false,
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let g = inv.grid;
    let ground = ItemDragPayload::new(0x71, ItemDragSource::Ground, 0);
    assert_eq!(
        drag_over(&mut inv, g, 5, ground.clone(), &view),
        DragAcceptance::Accept
    );
    let out = drop_on(&mut inv, g, 5, ground, &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec!["Already attempting to place that item here".to_owned()]
    );
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec![ac_runtime::inventory::REQUEST_BUSY.to_owned()]
    );
}

#[test]
fn a_shortcut_alias_dropped_on_the_inventory_moves_nothing() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.get_mut(0xA).currently_equipped_location = EquipMask::MELEE_WEAPON;
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let g = inv.grid;
    let out = drop_on(
        &mut inv,
        g,
        0,
        ItemDragPayload::new(0xA, ItemDragSource::ShortcutBar, 3),
        &view,
    );
    assert!(out.is_empty());
}

#[test]
fn lifting_a_drag_selects_the_item() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    inv.outputs();
    let g = inv.grid;
    inv.fire_payload(g, 0, kind::DRAG_BEGIN, payload(0xA));
    inv.update(&view);
    assert_dbg_eq!(inv.outputs(), vec![PanelOutput::SelectObject(0xA)]);
}

/// Press on the world at an empty spot, drag, lift what the world lift
/// gives `hit` (as the window does with its pick there), and let go over
/// cell `i` of `list`; the session commands that drop sent.
fn drag_off_the_world_onto(
    inv: &mut Inv,
    objects: &ac_core::items::ClientObjectTable,
    hit: u32,
    list: ElementId,
    i: usize,
) -> Vec<SessionCommand> {
    let view = ItemsView::new(objects, PLAYER);
    inv.update(&view);
    inv.outputs();
    let root = &mut inv.host.root;
    root.on_mouse_down(MouseButton::Left, 600, 500, 0);
    root.on_mouse_move(620, 520);
    let starts: Vec<(i32, i32)> = root
        .drain_output()
        .iter()
        .filter_map(|o| match o {
            UiOutput::WorldDragStart { x, y } => Some((*x, *y)),
            _ => None,
        })
        .collect();
    assert_eq!(
        starts,
        [(600, 500)],
        "the press on the world asks for a lift"
    );
    let (payload, ghost) = ac_ui::world_drag::lift(
        hit,
        PLAYER,
        ac_core::combat::CombatMode::NON_COMBAT,
        objects,
        None,
        &mut inv.icons,
    )
    .expect("the item on the ground lifts");
    assert!(inv.host.root.begin_world_drag(payload, ghost));
    let cell = inv.cell(list, i);
    let (x, y) = inv.host.root.screen_position(cell);
    let (x, y) = (x as i32 + 4, y as i32 + 4);
    inv.host.root.on_mouse_move(x, y);
    inv.host.root.on_mouse_up(MouseButton::Left, x, y, 0);
    inv.host.pump();
    inv.update(&view);
    sessions(&inv.outputs())
}

#[test]
fn an_item_dragged_off_the_ground_into_the_pack_is_picked_up() {
    // GitHub #45: an item lying on the ground could not be dragged into
    // the inventory; the drag never started. It lifts as a ground drag,
    // and each pack target picks it up.
    const LOOT: u32 = 0x7000_0001;
    let world = || {
        let mut o = Objects::new();
        o.player(102, 7);
        o.contained(0xA, PLAYER, 0, ItemType::MISC);
        o.bag(0xC, 1, 24);
        let mut loot = ClientObject::new(LOOT);
        loot.item_type = ItemType::MISC;
        loot.icon_id = 0x0600_0777;
        o.add(loot);
        o
    };
    let pickup = |container: ObjectRef, slot: i32| {
        vec![SessionCommand::Item(ItemCommand::DropInto {
            item: ObjectRef::Guid(LOOT),
            container,
            slot,
            amount: 0,
            pickup: true,
            join_stack: false,
        })]
    };
    // The open pack's grid: after the item already there.
    let o = world();
    let mut inv = inventory_window(None, InventorySprites::default());
    let g = inv.grid;
    assert_dbg_eq!(
        drag_off_the_world_onto(&mut inv, &o.t, LOOT, g, 3),
        pickup(ObjectRef::Player, 1)
    );
    // The main pack's icon.
    let o = world();
    let mut inv = inventory_window(None, InventorySprites::default());
    let top = inv.top;
    assert_dbg_eq!(
        drag_off_the_world_onto(&mut inv, &o.t, LOOT, top, 0),
        pickup(ObjectRef::Player, 0)
    );
    // A side pack's icon.
    let o = world();
    let mut inv = inventory_window(None, InventorySprites::default());
    let packs = inv.containers;
    assert_dbg_eq!(
        drag_off_the_world_onto(&mut inv, &o.t, LOOT, packs, 0),
        pickup(ObjectRef::Guid(0xC), 0)
    );
}

#[test]
fn cells_of_items_in_flight_wait() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let pending = [0xB];
    inv.update(&ItemsView {
        pending_sources: &pending,
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(!inv.slot(inv.grid, 0).waiting());
    assert!(inv.slot(inv.grid, 1).waiting());
}

#[test]
fn a_drop_while_a_request_is_in_flight_says_so() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.contained(0xB, PLAYER, 1, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView {
        can_make_request: false,
        ..ItemsView::new(&o.t, PLAYER)
    };
    inv.update(&view);
    let g = inv.grid;
    let out = drop_on(&mut inv, g, 1, payload(0xA), &view);
    assert!(sessions(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec![ac_runtime::inventory::REQUEST_BUSY.to_owned()]
    );
}

// ── Paperdoll (PaperdollControllerTests, PaperdollToggleTests,
//    PaperdollClickMapTests, PaperdollFigurePartsTests) ─────────────────

const HEAD_SLOT: u32 = 0x1000_05AB;
const CHEST_SLOT: u32 = 0x1000_01E2;
const CHEST_ARMOR_SLOT: u32 = 0x1000_05AC;
const SHIELD_SLOT: u32 = 0x1000_01E1;
const WEAPON_SLOT: u32 = 0x1000_01DF;
const FINGER_L_SLOT: u32 = 0x1000_01DC;
const BLUE_SLOT: u32 = 0x1000_0595;
const YELLOW_SLOT: u32 = 0x1000_0596;
const RED_SLOT: u32 = 0x1000_0597;
// The stand-in click map is four pixels wide and one tall: head, nothing,
// chest, foot.
const HEAD_X: i32 = 0;
const UNMAPPED_X: i32 = 1;
const CHEST_X: i32 = 2;
const FOOT_X: i32 = 3;

use ac_core::items::AetheriaUnlockState;
use ac_ui::event::UiEvent;
use ac_ui::panels::paperdoll::{self, PaperdollClickMap, PaperdollController, figure_flash};

fn doll_click_map() -> Arc<PaperdollClickMap> {
    let px: [[u8; 4]; 4] = [
        [0x00, 0x00, 0xFF, 0xFF],
        [0x00, 0x00, 0x00, 0xFF],
        [0x00, 0xFF, 0x00, 0xFF],
        [0x80, 0x00, 0x00, 0xFF],
    ];
    Arc::new(PaperdollClickMap::new(px.concat(), 4, 1).unwrap())
}

struct Doll {
    host: UiHost,
    lists: HashMap<u32, ElementId>,
    mask: ElementId,
    armor: ElementId,
    viewport: ElementId,
    toggle: ElementId,
    icons: TestIcons,
}

fn doll(empty: u32, sprites: &HashMap<u32, u32>) -> Doll {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 224.0, 214.0));
    let mut ids = HashMap::new();
    let mut lists = HashMap::new();
    for (i, id) in [
        HEAD_SLOT,
        CHEST_SLOT,
        CHEST_ARMOR_SLOT,
        SHIELD_SLOT,
        WEAPON_SLOT,
        FINGER_L_SLOT,
        BLUE_SLOT,
        YELLOW_SLOT,
        RED_SLOT,
    ]
    .into_iter()
    .enumerate()
    {
        let l = list(ui, root, 100.0, i as f32 * 20.0, 32.0, 32.0);
        ids.insert(id, l);
        lists.insert(id, l);
    }
    let mask = panel(ui, root, at(0.0, 0.0, 4.0, 1.0));
    let viewport = panel(ui, root, at(0.0, 0.0, 90.0, 200.0));
    let toggle = ui.add(
        root,
        at(0.0, 200.0, 40.0, 14.0),
        ac_ui::widgets::Button::new(
            Arc::new(ac_ui::element_info::ElementInfo::default()),
            None,
            Vec::new(),
            0.0,
        ),
    );
    ids.insert(paperdoll::DOLL_DRAG_MASK_ID, mask);
    ids.insert(paperdoll::DOLL_VIEWPORT_ID, viewport);
    ids.insert(paperdoll::SLOTS_BUTTON_ID, toggle);
    let layout = ImportedLayout::from_parts(root, ids);
    ui.register_window("inventory", root, None, 0).unwrap();
    let c = PaperdollController::bind(ui, &layout, empty, sprites, Some(doll_click_map()), None);
    host.attach_controller("inventory", Box::new(c)).unwrap();
    Doll {
        host,
        armor: lists[&HEAD_SLOT],
        lists,
        mask,
        viewport,
        toggle,
        icons: TestIcons::default(),
    }
}

impl Doll {
    fn update(&mut self, view: &ItemsView<'_>) {
        let icons = &mut self.icons;
        self.host
            .with_controller::<PaperdollController, _>("inventory", |c, ui| {
                c.update(ui, view, icons);
            })
            .unwrap();
    }

    fn cell(&self, slot: u32) -> ElementId {
        self.host
            .root
            .widget::<ItemList>(self.lists[&slot])
            .unwrap()
            .item(0)
            .unwrap()
    }

    fn slot(&self, slot: u32) -> &ItemSlot {
        self.host.root.widget::<ItemSlot>(self.cell(slot)).unwrap()
    }

    fn fire(&mut self, slot: u32, k: i32, p: Option<ItemDragPayload>) {
        let c = self.cell(slot);
        self.host.root.fire_event(k, c, p.map(DragPayload::new));
        self.host.pump();
    }

    fn mask_event(&mut self, k: i32, x: i32, p: Option<ItemDragPayload>) {
        let m = self.mask;
        let e = UiEvent::new(0, m, k)
            .with_data(0, x, 0)
            .with_payload(p.map(DragPayload::new));
        self.host.root.send_event(m, &e);
        self.host.pump();
    }

    fn mask_lift(&mut self, x: i32) -> (Option<ItemDragPayload>, Option<ac_ui::tree::DragGhost>) {
        use ac_ui::tree::Widget;
        self.mask_event(kind::MOUSE_DOWN, x, None);
        let m = self.mask;
        let payload = self
            .host
            .root
            .with::<ac_ui::widgets::Panel, _>(m, |w, ui| w.drag_payload(ui, m))
            .flatten()
            .and_then(|p| p.get::<ItemDragPayload>().cloned());
        let ghost = self
            .host
            .root
            .with::<ac_ui::widgets::Panel, _>(m, |w, ui| w.drag_ghost(ui, m))
            .flatten();
        (payload, ghost)
    }

    fn acceptance(&mut self) -> DragAcceptance {
        self.host
            .with_controller::<PaperdollController, _>("inventory", |c, _| c.doll_drag_acceptance())
            .unwrap()
    }

    fn outputs(&mut self) -> Vec<PanelOutput> {
        self.host
            .take_outputs()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Panel(p) => Some(p),
                _ => None,
            })
            .collect()
    }
}

#[test]
fn equipped_items_show_in_their_slots_and_an_npcs_do_not() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xA01, EquipMask::HEAD_WEAR);
    o.equipped(0xB01, EquipMask::MELEE_WEAPON);
    let mut npc = ClientObject::new(0xF02);
    npc.wielder_id = 0x6000_0001;
    npc.currently_equipped_location = EquipMask::SHIELD;
    o.add(npc);
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(d.slot(HEAD_SLOT).object_id, 0xA01);
    assert_eq!(
        d.slot(WEAPON_SLOT).object_id,
        0xB01,
        "the composite weapon slot"
    );
    assert_eq!(d.slot(SHIELD_SLOT).object_id, 0);
    assert_eq!(d.slot(HEAD_SLOT).source_kind, ItemDragSource::Equipment);
    assert_eq!(d.slot(HEAD_SLOT).slot_index, 0);
}

#[test]
fn right_clicking_an_equipped_slot_selects_and_appraises_and_a_press_selects() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xA01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    d.outputs();
    d.fire(HEAD_SLOT, kind::RIGHT_CLICK, None);
    assert_dbg_eq!(
        d.outputs(),
        vec![
            PanelOutput::SelectObject(0xA01),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(0xA01)
            ))),
        ]
    );
    assert!(d.slot(HEAD_SLOT).selected);
    d.fire(HEAD_SLOT, kind::MOUSE_DOWN, None);
    assert_dbg_eq!(d.outputs(), vec![PanelOutput::SelectObject(0xA01)]);
}

#[test]
fn a_drag_over_a_slot_is_taken_only_for_a_valid_location_and_never_for_an_alias() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.pack_item(0xC01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    let p = ItemDragPayload::new(0xC01, ItemDragSource::Inventory, 0);
    d.fire(HEAD_SLOT, kind::DRAG_ENTER, Some(p.clone()));
    d.fire(SHIELD_SLOT, kind::DRAG_ENTER, Some(p));
    d.update(&view);
    assert_eq!(d.slot(HEAD_SLOT).drag_acceptance(), DragAcceptance::Accept);
    assert_eq!(
        d.slot(SHIELD_SLOT).drag_acceptance(),
        DragAcceptance::Reject
    );
    for source in [
        ItemDragSource::ShortcutBar,
        ItemDragSource::Salvage,
        ItemDragSource::Vendor,
    ] {
        let alias = ItemDragPayload::new(0xC01, source, 2);
        d.fire(HEAD_SLOT, kind::DRAG_ENTER, Some(alias.clone()));
        d.update(&view);
        assert_eq!(
            d.slot(HEAD_SLOT).drag_acceptance(),
            DragAcceptance::None,
            "{source:?}"
        );
        d.outputs();
        d.fire(HEAD_SLOT, kind::DROP_RELEASED, Some(alias));
        d.update(&view);
        assert!(wields(&d.outputs()).is_empty(), "{source:?}");
    }
}

/// A drag that enters a slot and leaves it in one frame (a fast mouse, or
/// two queued moves) is answered by the paperdoll only at its next update,
/// after the leave: the stale answer is not shown. An acceptance run saw
/// the refusal stay on the chest
/// and shield slots after such a drag.
#[test]
fn an_answer_after_the_drag_left_the_slot_is_not_shown() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.pack_item(0xC01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    let p = ItemDragPayload::new(0xC01, ItemDragSource::Inventory, 0);
    d.fire(SHIELD_SLOT, kind::DRAG_ENTER, Some(p.clone()));
    d.fire(SHIELD_SLOT, kind::DRAG_OVER, Some(p.clone()));
    d.fire(HEAD_SLOT, kind::DRAG_ENTER, Some(p.clone()));
    d.fire(HEAD_SLOT, kind::DROP_RELEASED, Some(p));
    d.update(&view);
    assert_eq!(d.slot(SHIELD_SLOT).drag_acceptance(), DragAcceptance::None);
    assert_eq!(d.slot(HEAD_SLOT).drag_acceptance(), DragAcceptance::None);
}

#[test]
fn a_drop_on_a_slot_wields_at_the_slots_location_or_all_for_worn_things() {
    let coat = mask_of(&[
        EquipMask::CHEST_WEAR,
        EquipMask::UPPER_ARM_WEAR,
        EquipMask::LOWER_ARM_WEAR,
    ]);
    let hauberk = mask_of(&[
        EquipMask::CHEST_ARMOR,
        EquipMask::ABDOMEN_ARMOR,
        EquipMask::UPPER_ARM_ARMOR,
        EquipMask::LOWER_ARM_ARMOR,
    ]);
    let ring = mask_of(&[EquipMask::FINGER_WEAR_LEFT, EquipMask::FINGER_WEAR_RIGHT]);
    for (item, valid, slot, sent) in [
        (0xD01, EquipMask::HEAD_WEAR, HEAD_SLOT, EquipMask::HEAD_WEAR),
        (0xE01, ring, FINGER_L_SLOT, EquipMask::FINGER_WEAR_LEFT),
        (0xE02, coat, CHEST_SLOT, coat),
        (0xE03, hauberk, CHEST_ARMOR_SLOT, hauberk),
    ] {
        let mut o = Objects::new();
        o.player(102, 7);
        o.pack_item(item, valid);
        let mut d = doll(0, &HashMap::new());
        let view = ItemsView::new(&o.t, PLAYER);
        d.update(&view);
        d.outputs();
        d.fire(
            slot,
            kind::DROP_RELEASED,
            Some(ItemDragPayload::new(item, ItemDragSource::Inventory, 0)),
        );
        d.update(&view);
        assert_eq!(wields(&d.outputs()), vec![(item, sent.bits())], "{item:#x}");
        // Nothing moves until the server places it.
        assert_eq!(
            o.t.get(item).unwrap().currently_equipped_location,
            EquipMask::NONE
        );
    }
}

fn off_hand_wields(outputs: &[PanelOutput]) -> Vec<u32> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(ItemCommand::WieldSecondary(ObjectRef::Guid(i))) => Some(i),
            _ => None,
        })
        .collect()
}

// OpenAC issue #225
// `OnDragOver_shieldSlot_acceptsAMeleeWeaponAsOffHand_butNotABowOrTwoHander`.
#[test]
fn the_shield_slot_takes_a_melee_weapon_as_off_hand_but_not_a_bow_or_two_hander() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.pack_item(0xD21, EquipMask::MELEE_WEAPON);
    o.pack_item(0xD22, EquipMask::MISSILE_WEAPON);
    o.pack_item(0xD23, EquipMask::TWO_HANDED);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    for (item, expected) in [
        (0xD21, DragAcceptance::Accept),
        (0xD22, DragAcceptance::Reject),
        (0xD23, DragAcceptance::Reject),
    ] {
        let p = ItemDragPayload::new(item, ItemDragSource::Inventory, 0);
        d.fire(SHIELD_SLOT, kind::DRAG_ENTER, Some(p));
        d.update(&view);
        assert_eq!(d.slot(SHIELD_SLOT).drag_acceptance(), expected, "{item:#x}");
    }
}

// OpenAC issue #225
// `HandleDropRelease_meleeWeaponOnShieldSlot_wieldsItInTheShieldSlot` and
// `HandleDropRelease_meleeWeaponOnOccupiedShieldSlot_movesTheShieldToThePackFirst`:
// the drop asks the session for the off-hand wield; what the session then
// sends (the shield to the pack first, then the wield in the shield slot)
// is the runtime's, tested there.
#[test]
fn a_melee_weapon_dropped_on_the_shield_slot_is_wielded_in_the_off_hand() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xD25, EquipMask::MELEE_WEAPON);
    o.pack_item(0xD24, EquipMask::MELEE_WEAPON);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    d.outputs();
    d.fire(
        SHIELD_SLOT,
        kind::DROP_RELEASED,
        Some(ItemDragPayload::new(0xD24, ItemDragSource::Inventory, 0)),
    );
    d.update(&view);
    let out = d.outputs();
    assert_eq!(off_hand_wields(&out), vec![0xD24]);
    assert!(wields(&out).is_empty(), "no main-hand wield");
    assert_eq!(
        o.t.get(0xD24).unwrap().currently_equipped_location,
        EquipMask::NONE
    );
}

// OpenAC issue #225
// `HandleDropRelease_shieldOnShieldSlotHoldingAnOffHandWeapon_movesTheWeaponToThePackFirst`:
// a shield dropped on the shield slot holding an off-hand sword asks the
// session for a shield-slot wield (not an off-hand one); moving the sword
// to the pack first is the runtime's, tested there.
#[test]
fn a_shield_dropped_on_an_off_hand_weapon_asks_for_a_shield_slot_wield() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xD2A, EquipMask::MELEE_WEAPON);
    o.equipped(0xD28, EquipMask::SHIELD);
    o.get_mut(0xD28).valid_locations = EquipMask::MELEE_WEAPON;
    o.pack_item(0xD29, EquipMask::SHIELD);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    d.outputs();
    d.fire(
        SHIELD_SLOT,
        kind::DROP_RELEASED,
        Some(ItemDragPayload::new(0xD29, ItemDragSource::Inventory, 0)),
    );
    d.update(&view);
    let out = d.outputs();
    assert_eq!(wields(&out), vec![(0xD29, EquipMask::SHIELD.bits())]);
    assert!(off_hand_wields(&out).is_empty());
}

#[test]
fn a_drop_while_a_request_is_in_flight_wields_nothing_and_says_so() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.pack_item(0xD01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView {
        can_make_request: false,
        ..ItemsView::new(&o.t, PLAYER)
    };
    d.update(&view);
    d.outputs();
    d.fire(
        HEAD_SLOT,
        kind::DROP_RELEASED,
        Some(ItemDragPayload::new(0xD01, ItemDragSource::Inventory, 0)),
    );
    d.update(&view);
    let out = d.outputs();
    assert!(wields(&out).is_empty());
    assert_eq!(
        notices(&out),
        vec![ac_runtime::inventory::REQUEST_BUSY.to_owned()]
    );
}

#[test]
fn empty_slots_show_their_own_art_over_the_fallback() {
    let sprites: HashMap<u32, u32> = [
        (HEAD_SLOT, 0x0600_6D7F),
        (CHEST_SLOT, 0x0600_32C5),
        (CHEST_ARMOR_SLOT, 0x0600_6D7B),
        (SHIELD_SLOT, 0x0600_0F6C),
        (WEAPON_SLOT, 0x0600_0F66),
    ]
    .into_iter()
    .collect();
    let d = doll(0x0600_4D20, &sprites);
    for (slot, sprite) in &sprites {
        assert_eq!(d.slot(*slot).empty_sprite, *sprite);
    }
    assert_eq!(d.slot(FINGER_L_SLOT).empty_sprite, 0x0600_4D20);
}

#[test]
fn aetheria_slots_follow_the_players_unlock_bits() {
    let mut o = Objects::new();
    o.player(102, 7);
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    let visible = |d: &Doll, s: u32| d.host.root.element(d.lists[&s]).unwrap().visible;
    assert!(!visible(&d, BLUE_SLOT) && !visible(&d, YELLOW_SLOT) && !visible(&d, RED_SLOT));
    let bits = (AetheriaUnlockState::BLUE.bits() | AetheriaUnlockState::RED.bits()) as i32;
    o.get_mut(PLAYER)
        .properties
        .ints
        .insert(ac_core::items::AETHERIA_UNLOCK_PROPERTY, bits);
    o.equipped(0xA37, EquipMask::SIGIL_ONE);
    d.update(&ItemsView::new(&o.t, PLAYER));
    assert!(visible(&d, BLUE_SLOT) && !visible(&d, YELLOW_SLOT) && visible(&d, RED_SLOT));
    assert_eq!(d.slot(BLUE_SLOT).object_id, 0xA37);
}

#[test]
fn the_slots_button_switches_between_the_doll_and_the_armor_slots() {
    assert_eq!(paperdoll::ARMOR_SLOT_ELEMENT_IDS.len(), 9);
    let o = Objects::new();
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    let visible = |d: &Doll, e: ElementId| d.host.root.element(e).unwrap().visible;
    assert!(visible(&d, d.viewport) && visible(&d, d.mask));
    assert!(!visible(&d, d.armor));
    let t = d.toggle;
    d.host.root.fire_event(kind::CLICK, t, None);
    d.host.pump();
    assert!(!visible(&d, d.viewport) && !visible(&d, d.mask));
    assert!(visible(&d, d.armor));
    d.host.root.fire_event(kind::CLICK, t, None);
    d.host.pump();
    assert!(visible(&d, d.viewport));
}

#[test]
fn the_click_map_names_the_nine_retail_colours_and_nothing_else() {
    let cases = [
        ([0x00, 0x00, 0xFF], EquipMask::HEAD_WEAR),
        (
            [0x00, 0xFF, 0x00],
            mask_of(&[EquipMask::CHEST_WEAR, EquipMask::CHEST_ARMOR]),
        ),
        (
            [0xFF, 0x00, 0x00],
            mask_of(&[EquipMask::ABDOMEN_WEAR, EquipMask::ABDOMEN_ARMOR]),
        ),
        (
            [0x00, 0xFF, 0xFF],
            mask_of(&[EquipMask::UPPER_ARM_WEAR, EquipMask::UPPER_ARM_ARMOR]),
        ),
        (
            [0xFF, 0x00, 0xFF],
            mask_of(&[EquipMask::LOWER_ARM_WEAR, EquipMask::LOWER_ARM_ARMOR]),
        ),
        (
            [0xFF, 0xFF, 0x00],
            mask_of(&[EquipMask::UPPER_LEG_WEAR, EquipMask::UPPER_LEG_ARMOR]),
        ),
        (
            [0x00, 0x00, 0x80],
            mask_of(&[EquipMask::LOWER_LEG_WEAR, EquipMask::LOWER_LEG_ARMOR]),
        ),
        ([0x00, 0x80, 0x00], EquipMask::HAND_WEAR),
        ([0x80, 0x00, 0x00], EquipMask::FOOT_WEAR),
    ];
    for (rgb, expected) in cases {
        let map = PaperdollClickMap::new(vec![rgb[0], rgb[1], rgb[2], 0xFF], 1, 1).unwrap();
        assert_eq!(map.body_location(0, 0), expected);
    }
    let map = PaperdollClickMap::new(vec![0xFF; 4], 1, 1).unwrap();
    assert_eq!(map.body_location(0, 0), EquipMask::NONE);
    assert_eq!(map.body_location(-1, 0), EquipMask::NONE);
    assert_eq!(map.body_location(1, 0), EquipMask::NONE);
}

#[test]
fn the_figure_selects_and_examines_the_worn_item_and_ignores_unmapped_points() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xA01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    d.outputs();
    d.mask_event(kind::RIGHT_CLICK, UNMAPPED_X, None);
    d.update(&ItemsView::new(&o.t, PLAYER));
    assert!(d.outputs().is_empty());
    d.mask_event(kind::RIGHT_CLICK, HEAD_X, None);
    d.update(&ItemsView::new(&o.t, PLAYER));
    assert_dbg_eq!(
        d.outputs(),
        vec![
            PanelOutput::SelectObject(0xA01),
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(0xA01)
            ))),
        ]
    );
    d.mask_event(kind::CLICK, HEAD_X, None);
    d.update(&ItemsView::new(&o.t, PLAYER));
    assert_dbg_eq!(d.outputs(), vec![PanelOutput::SelectObject(0xA01)]);
}

#[test]
fn the_figure_lifts_the_worn_item_the_players_pack_where_bare_and_nothing_off_the_map() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xA01, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    let (p, g) = d.mask_lift(HEAD_X);
    let p = p.unwrap();
    assert_eq!(
        (p.obj_id, p.source_kind, p.source_slot, p.source_cell),
        (0xA01, ItemDragSource::Equipment, 0, None)
    );
    assert_eq!(g.unwrap().texture, TextureRef::Runtime(0x1600_0A01));
    let (p, g) = d.mask_lift(UNMAPPED_X);
    assert!(p.is_none() && g.is_none());
    // Nothing on the feet: the player's own pack, as the first pack slot
    // lifts it, with the main pack's picture.
    let (p, g) = d.mask_lift(FOOT_X);
    let p = p.unwrap();
    assert_eq!(
        (p.obj_id, p.source_kind, p.source_slot),
        (PLAYER, ItemDragSource::Inventory, 0)
    );
    let g = g.unwrap();
    assert_eq!(
        (g.texture, g.width, g.height),
        (
            TextureRef::Runtime(0x1000_0000 | inventory::PLAYER_PACK_BASE_ICON),
            32,
            32
        )
    );
}

#[test]
fn a_drop_on_the_figure_wears_a_wearable_item_and_refuses_anything_else() {
    let hauberk = mask_of(&[
        EquipMask::CHEST_ARMOR,
        EquipMask::ABDOMEN_ARMOR,
        EquipMask::UPPER_ARM_ARMOR,
        EquipMask::LOWER_ARM_ARMOR,
    ]);
    let mut o = Objects::new();
    o.player(102, 7);
    o.pack_item(0xC10, hauberk);
    o.pack_item(0xC11, EquipMask::MELEE_WEAPON);
    o.pack_item(0xC12, EquipMask::HEAD_WEAR);
    let mut d = doll(0, &HashMap::new());
    let view = ItemsView::new(&o.t, PLAYER);
    d.update(&view);
    d.outputs();
    let p = ItemDragPayload::new(0xC10, ItemDragSource::Inventory, 0);
    d.mask_event(kind::DRAG_ENTER, CHEST_X, Some(p.clone()));
    d.mask_event(kind::DROP_RELEASED, CHEST_X, Some(p));
    d.update(&view);
    assert_eq!(wields(&d.outputs()), vec![(0xC10, hauberk.bits())]);
    // A weapon is wielded, never worn: refused.
    let sword = ItemDragPayload::new(0xC11, ItemDragSource::Inventory, 0);
    d.mask_event(kind::DRAG_ENTER, CHEST_X, Some(sword.clone()));
    d.update(&view);
    assert_eq!(d.acceptance(), DragAcceptance::Reject);
    d.mask_event(kind::DROP_RELEASED, CHEST_X, Some(sword));
    d.update(&view);
    assert!(wields(&d.outputs()).is_empty());
    // A helm is taken while over the figure, neutral once the drag leaves.
    let helm = ItemDragPayload::new(0xC12, ItemDragSource::Inventory, 0);
    d.mask_event(kind::DRAG_ENTER, HEAD_X, Some(helm.clone()));
    d.update(&view);
    assert_eq!(d.acceptance(), DragAcceptance::Accept);
    d.mask_event(kind::DRAG_OVER, HEAD_X, Some(helm));
    d.update(&view);
    assert_eq!(d.acceptance(), DragAcceptance::None);
    // An alias (a shortcut, or a salvage, vendor or house window cell) never wields.
    for source in [
        ItemDragSource::ShortcutBar,
        ItemDragSource::Salvage,
        ItemDragSource::Vendor,
    ] {
        let alias = ItemDragPayload::new(0xC12, source, 2);
        d.mask_event(kind::DRAG_ENTER, HEAD_X, Some(alias.clone()));
        d.update(&view);
        assert_eq!(d.acceptance(), DragAcceptance::None, "{source:?}");
        d.mask_event(kind::DROP_RELEASED, HEAD_X, Some(alias));
        d.update(&view);
        assert!(wields(&d.outputs()).is_empty(), "{source:?}");
    }
}

#[test]
fn the_figure_flashes_the_parts_the_selected_item_covers() {
    let hauberk = mask_of(&[
        EquipMask::CHEST_ARMOR,
        EquipMask::ABDOMEN_ARMOR,
        EquipMask::UPPER_ARM_ARMOR,
        EquipMask::LOWER_ARM_ARMOR,
    ]);
    let mut o = Objects::new();
    o.player(102, 7);
    o.equipped(0xA, EquipMask::HEAD_WEAR);
    o.equipped(0xB, hauberk);
    o.equipped(0xC, EquipMask::FOOT_WEAR);
    o.equipped(0xD, EquipMask::HAND_WEAR);
    // A shirt under the hauberk is not the outermost thing anywhere.
    o.equipped(0xE, EquipMask::CHEST_WEAR);
    o.get_mut(0xB).priority = 0x100;
    o.get_mut(0xE).priority = 0x8;
    o.pack_item(0xF, EquipMask::HEAD_WEAR);
    let flash = |id| figure_flash(&o.t, PLAYER, id);
    assert_eq!(flash(0xA).part_mask, 1 << 0x10);
    assert_eq!(
        flash(0xB).part_mask,
        (1 << 0x09) | (1 << 0x00) | (1 << 0x0A) | (1 << 0x0D) | (1 << 0x0B) | (1 << 0x0E)
    );
    assert_eq!(
        flash(0xC).part_mask,
        (1 << 0x03) | (1 << 0x07) | (1 << 0x04) | (1 << 0x08)
    );
    assert_eq!(flash(0xD).part_mask, (1 << 0x0C) | (1 << 0x0F));
    assert!(flash(PLAYER).whole_figure);
    assert!(!flash(0xF).any());
    assert!(!flash(0xE).any());
    // A selection change flashes the worn pieces through the panel.
    let mut d = doll(0, &HashMap::new());
    d.update(&ItemsView::new(&o.t, PLAYER));
    d.outputs();
    d.update(&ItemsView {
        selected: Some(0xA),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert_dbg_eq!(
        d.outputs(),
        vec![PanelOutput::FlashFigure {
            whole_figure: false,
            part_mask: 1 << 0x10
        }]
    );
    d.update(&ItemsView {
        selected: Some(0xF),
        ..ItemsView::new(&o.t, PLAYER)
    });
    assert!(d.outputs().is_empty());
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use std::cell::RefCell;
    use std::rc::Rc;

    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::icons::{IconComposer, IconUploader, Rgba};
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::panels::cell_template;
    use std::path::PathBuf;

    pub(super) fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    /// Hands out texture handles in order and keeps what it was given.
    #[derive(Debug, Default)]
    struct Uploads(Vec<Rgba>);

    impl IconUploader for Uploads {
        fn upload(&mut self, image: &Rgba) -> TextureRef {
            self.0.push(image.clone());
            TextureRef::Runtime(self.0.len() as u32)
        }
    }

    /// [`Uploads`] the test can still read once the composer holds it.
    struct SharedUploads(Rc<RefCell<Uploads>>);

    impl IconUploader for SharedUploads {
        fn upload(&mut self, image: &Rgba) -> TextureRef {
            self.0.borrow_mut().upload(image)
        }
    }

    /// The image MossTank and MosswartMassacre name as their dock icon has an
    /// opaque white outline, which keying replaces.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn a_keyed_plugin_icon_has_no_opaque_white_left() {
        const OPAQUE_WHITE: [u8; 4] = [255, 255, 255, 255];
        const ICON: u32 = 0x0600_2C41;
        let c = content();
        let uploads = Rc::new(RefCell::new(Uploads::default()));
        let mut composer = IconComposer::new(
            c.dats().clone(),
            Box::new(SharedUploads(Rc::clone(&uploads))),
        );
        let raw = composer.surface(ICON).expect("the icon decodes");
        let white = |pixels: &[u8]| pixels.chunks(4).filter(|p| *p == OPAQUE_WHITE).count();
        assert!(white(&raw.pixels) > 0, "the art has its white outline");

        let texture = composer.keyed_icon(ICON).expect("the icon is keyed");
        let uploads = uploads.borrow();
        let keyed = &uploads.0[0];
        assert_eq!(texture, TextureRef::Runtime(1));
        assert_eq!((keyed.width, keyed.height), (raw.width, raw.height));
        assert_eq!(white(&keyed.pixels), 0);
    }

    // ItemListCellTemplateTests.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_inventory_lists_resolve_their_pinned_empty_sprites() {
        let c = content();
        let sprite = |layout, list| cell_template::empty_sprite(&c, layout, list);
        assert_eq!(
            sprite(inventory::CONTENTS_LAYOUT_ID, inventory::CONTENTS_GRID_ID),
            0x0600_4D20
        );
        assert_eq!(
            sprite(inventory::BACKPACK_LAYOUT_ID, inventory::CONTAINER_LIST_ID),
            0x0600_0F6E
        );
        assert_eq!(
            sprite(inventory::BACKPACK_LAYOUT_ID, inventory::TOP_CONTAINER_ID),
            0x0600_0F6E
        );
        assert!(c.take_errors().is_empty());
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_shared_item_prototype_resolves_all_ten_cooldown_overlays() {
        let c = content();
        let sprites = cell_template::cooldown_sprites(&c).expect("ten overlays");
        assert_eq!(sprites, (0x0600_67CF..=0x0600_67D8).collect::<Vec<u32>>());
    }

    // IconComposerTests (dat cases).
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_icon_composer_resolves_the_retail_underlays_effects_and_tiles() {
        let c = content();
        let mut ic = IconComposer::new(c.dats().clone(), Box::new(Uploads::default()));
        assert_eq!(ic.resolve_underlay(ItemType::MELEE_WEAPON), 0x0600_11CB);
        assert_eq!(ic.resolve_underlay(ItemType::ARMOR), 0x0600_11CF);
        assert_eq!(ic.resolve_underlay(ItemType::CLOTHING), 0x0600_11F3);
        assert_eq!(ic.resolve_underlay(ItemType::JEWELRY), 0x0600_11D5);
        assert_eq!(ic.resolve_underlay(ItemType::NONE), 0x0600_11D4);
        assert_eq!(ic.resolve_effect(0x0001), 0x0600_11CA);
        assert_eq!(ic.resolve_effect(0x0002), 0x0600_11C6);
        assert_eq!(ic.resolve_effect(0x0004), 0x0600_1B05);
        assert_eq!(ic.resolve_effect(0x0010), 0x0600_1B06);
        assert_eq!(ic.resolve_effect(0x1000), 0x0600_11C5);
        assert_eq!(ic.resolve_effect(0), 0x0600_11C5);
        let black = ic.effect_tile(0).expect("the no-effect tile");
        assert_eq!((black.width, black.height), (32, 32));
        assert!(black.pixels[..3].iter().all(|&v| v <= 8));
        let magic = ic.effect_tile(1).expect("the magical tile");
        let first = magic.pixels[0..3].to_vec();
        assert!(
            magic.pixels.chunks(4).any(|p| p[0..3] != first[..]),
            "a textured tile, not a flat colour"
        );
        // A composed icon is uploaded once and remembered.
        let t = ic.item_icon(ItemType::MELEE_WEAPON, 0x0600_11CB, 0, 0, 0);
        assert!(t.is_some());
        assert_eq!(
            ic.item_icon(ItemType::MELEE_WEAPON, 0x0600_11CB, 0, 0, 0),
            t
        );
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_inventory_window_imports_binds_and_a_drop_on_a_pack_moves_the_item() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import(
            &mut host.root,
            &c,
            inventory::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("inventory layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        for id in [
            inventory::CONTENTS_GRID_ID,
            inventory::CONTAINER_LIST_ID,
            inventory::TOP_CONTAINER_ID,
        ] {
            let l = layout.find(id).expect("item list");
            assert!(host.root.widget::<ItemList>(l).is_some(), "{id:#x}");
        }
        let meter = layout.find(inventory::BURDEN_METER_ID).unwrap();
        assert!(host.root.widget::<Meter>(meter).is_some());
        let sprite = |layout, list| cell_template::empty_sprite(&c, layout, list);
        let sprites = InventorySprites {
            contents_empty: sprite(inventory::CONTENTS_LAYOUT_ID, inventory::CONTENTS_GRID_ID),
            side_bag_empty: sprite(inventory::BACKPACK_LAYOUT_ID, inventory::CONTAINER_LIST_ID),
            main_pack_empty: sprite(inventory::BACKPACK_LAYOUT_ID, inventory::TOP_CONTAINER_ID),
        };
        host.root
            .register_window("inventory", layout.root, None, 0)
            .unwrap();
        let font = c.font(DEFAULT_FONT_ID);
        let ctl = InventoryController::bind(&mut host.root, &layout, sprites, font, None);
        host.attach_controller("inventory", Box::new(ctl)).unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        o.contained(0xA, PLAYER, 0, ItemType::MISC);
        o.bag(0xC0, 1, 24);
        let view = ItemsView {
            owner_name: Some("Horan"),
            ..ItemsView::new(&o.t, PLAYER)
        };
        let mut icons = TestIcons::default();
        host.with_controller::<InventoryController, _>("inventory", |ctl, ui| {
            ctl.update(ui, &view, &mut icons);
        });
        let grid = layout.find(inventory::CONTENTS_GRID_ID).unwrap();
        let column = layout.find(inventory::CONTAINER_LIST_ID).unwrap();
        let g = host.root.widget::<ItemList>(grid).unwrap();
        assert_eq!(g.num_items(), 102);
        let (first, second) = (g.item(0).unwrap(), g.item(1).unwrap());
        assert_eq!(host.root.widget::<ItemSlot>(first).unwrap().object_id, 0xA);
        assert_eq!(
            host.root.widget::<ItemSlot>(second).unwrap().empty_sprite,
            0x0600_4D20
        );
        let pack = host
            .root
            .widget::<ItemList>(column)
            .unwrap()
            .item(0)
            .unwrap();
        assert_eq!(host.root.widget::<ItemSlot>(pack).unwrap().object_id, 0xC0);
        let title = host
            .with_controller::<InventoryController, _>("inventory", |ctl, ui| {
                caption_of(ui, ctl.captions()[0])
            })
            .unwrap();
        assert_eq!(title, "Inventory of Horan");
        let list = host.draw((800.0, 600.0), &c);
        assert!(!list.main.is_empty());
        // Drop the grid item on the pack.
        host.take_outputs();
        host.root.fire_event(
            kind::DROP_RELEASED,
            pack,
            Some(DragPayload::new(payload(0xA))),
        );
        host.pump();
        host.with_controller::<InventoryController, _>("inventory", |ctl, ui| {
            ctl.update(ui, &view, &mut icons);
        });
        let out: Vec<PanelOutput> = host
            .take_outputs()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Panel(p) => Some(p),
                _ => None,
            })
            .collect();
        assert_dbg_eq!(
            moves(&out),
            vec![(0xAu32, ObjectRef::Guid(0xC0), 0i32, 0u32)]
        );
        assert!(LayoutSource::layout(&c, inventory::LAYOUT_ID).is_some());
        assert!(c.take_errors().is_empty());
    }

    // InventoryPackScrollingInstalledDatTests (OpenAC v0.1.25,
    // #195): seven or eight side packs all show in the retail layout
    // without scrolling, also after the window grows.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn an_augmented_pack_column_shows_every_side_slot_without_scrolling() {
        let c = content();
        for capacity in [7usize, 8] {
            let mut host = UiHost::new();
            host.root.set_size(1920.0, 1080.0);
            let layout =
                import(&mut host.root, &c, inventory::LAYOUT_ID, None).expect("inventory layout");
            let r = host.root.root();
            host.root.add_child(r, layout.root);
            host.root
                .register_window("inventory", layout.root, None, 0)
                .unwrap();
            let ctl = InventoryController::bind(
                &mut host.root,
                &layout,
                InventorySprites::default(),
                None,
                None,
            );
            host.attach_controller("inventory", Box::new(ctl)).unwrap();
            let mut o = Objects::new();
            o.player(102, capacity as i32);
            let view = ItemsView::new(&o.t, PLAYER);
            let mut icons = TestIcons::default();
            host.with_controller::<InventoryController, _>("inventory", |ctl, ui| {
                ctl.update(ui, &view, &mut icons);
            });
            let column = layout.find(inventory::CONTAINER_LIST_ID).unwrap();
            let height = |host: &UiHost| host.root.element(column).unwrap().height;
            let n = host.root.widget::<ItemList>(column).unwrap().num_items();
            assert_eq!(n, capacity);
            assert_eq!(height(&host), capacity as f32 * 36.0);
            for _ in 0..3 {
                host.root.capture_authored_anchor_baselines(r);
            }
            host.root.element_mut(layout.root).unwrap().height += 120.0;
            host.root.capture_authored_anchor_baselines(r);
            let h = host.root.element(layout.root).unwrap().height;
            host.root.capture_authored_anchor_baselines(layout.root);
            assert_eq!(host.root.element(layout.root).unwrap().height, h);
            assert_eq!(height(&host), capacity as f32 * 36.0, "{capacity}");
            let list = host.root.widget::<ItemList>(column).unwrap();
            assert_eq!(list.scroll.scroll_y(), 0);
            let (_, list_top) = host.root.screen_position(column);
            for &cell in list.cells() {
                let e = host.root.element(cell).unwrap();
                assert!(e.visible, "{capacity}");
                let (_, top) = host.root.screen_position(cell);
                let mut parent = host.root.parent(cell);
                while let Some(p) = parent.filter(|&p| p != r) {
                    let (_, ptop) = host.root.screen_position(p);
                    let pe = host.root.element(p).unwrap();
                    assert!(
                        top + e.height <= ptop + pe.height,
                        "slot inside {:#x} ({capacity})",
                        pe.dat_element_id
                    );
                    parent = host.root.parent(p);
                }
            }
            let main = layout.find(inventory::TOP_CONTAINER_ID).unwrap();
            let (_, main_top) = host.root.screen_position(main);
            assert!(main_top + host.root.element(main).unwrap().height <= list_top);
        }
    }

    // PaperdollSlotBackgroundTests, PaperdollClickMapTests (dat case),
    // PaperdollMaskInstalledDatTests.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_paperdoll_binds_its_slots_art_and_figure_mask_from_the_dats() {
        use ac_ui::panels::paperdoll::{InventoryWindowController, resolve_empty_sprites};
        let c = content();
        let expected: HashMap<u32, u32> = [
            (0x1000_01DA, 0x0600_0F68),
            (0x1000_01DB, 0x0600_0F5D),
            (0x1000_01DC, 0x0600_0F5A),
            (0x1000_01DD, 0x0600_0F6A),
            (0x1000_01DE, 0x0600_0F6B),
            (0x1000_01DF, 0x0600_0F66),
            (0x1000_01E0, 0x0600_0F5E),
            (0x1000_01E1, 0x0600_0F6C),
            (0x1000_01E2, 0x0600_32C5),
            (0x1000_01E3, 0x0600_32C4),
            (0x1000_058E, 0x0600_6A6C),
            (0x1000_05E9, 0x0600_708F),
            (0x1000_0595, 0x0600_6BEF),
            (0x1000_0596, 0x0600_6BF0),
            (0x1000_0597, 0x0600_6BF1),
            (0x1000_05AB, 0x0600_6D7F),
            (0x1000_05AC, 0x0600_6D7B),
            (0x1000_05AD, 0x0600_6D79),
            (0x1000_05AE, 0x0600_6D87),
            (0x1000_05AF, 0x0600_6D81),
            (0x1000_05B0, 0x0600_6D7D),
            (0x1000_05B1, 0x0600_6D89),
            (0x1000_05B2, 0x0600_6D83),
            (0x1000_05B3, 0x0600_6D85),
        ]
        .into_iter()
        .collect();
        let sprites = resolve_empty_sprites(&c);
        assert_eq!(sprites, expected);
        let map = Arc::new(PaperdollClickMap::load(c.dats()).expect("the click map"));
        let mut regions = std::collections::HashSet::new();
        for y in 0..map.height() as i32 {
            for x in 0..map.width() as i32 {
                let r = map.body_location(x, y);
                if r != EquipMask::NONE {
                    regions.insert(r.bits());
                }
            }
        }
        assert_eq!(regions.len(), 9);
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(&mut host.root, &c, inventory::LAYOUT_ID, None).unwrap();
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let mask = layout
            .find(paperdoll::DOLL_DRAG_MASK_ID)
            .expect("the doll mask");
        let e = host.root.element(mask).unwrap();
        assert_eq!(
            (e.width as u32, e.height as u32),
            (map.width(), map.height())
        );
        host.root
            .register_window("inventory", layout.root, None, 0)
            .unwrap();
        let inventory = InventoryController::bind(
            &mut host.root,
            &layout,
            InventorySprites::default(),
            None,
            None,
        );
        let doll = PaperdollController::bind(
            &mut host.root,
            &layout,
            0,
            &sprites,
            Some(map.clone()),
            None,
        );
        assert!(!host.root.element(mask).unwrap().click_through);
        host.attach_controller(
            "inventory",
            Box::new(InventoryWindowController {
                inventory,
                paperdoll: doll,
            }),
        )
        .unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        o.equipped(0xA01, EquipMask::CHEST_ARMOR);
        let view = ItemsView::new(&o.t, PLAYER);
        let mut icons = TestIcons::default();
        let mut update = |host: &mut UiHost| {
            host.with_controller::<InventoryWindowController, _>("inventory", |w, ui| {
                w.update(ui, &view, &mut icons);
            })
            .unwrap();
        };
        update(&mut host);
        let chest_slot = layout.find(0x1000_05AC).unwrap();
        let cell = host
            .root
            .widget::<ItemList>(chest_slot)
            .unwrap()
            .item(0)
            .unwrap();
        assert_eq!(host.root.widget::<ItemSlot>(cell).unwrap().object_id, 0xA01);
        assert_eq!(
            host.root.widget::<ItemSlot>(cell).unwrap().empty_sprite,
            0x0600_6D7B
        );
        // Drive the mask through the root over the chest region.
        let (mut px, mut py) = (0, 0);
        'find: for y in 0..map.height() as i32 {
            for x in 0..map.width() as i32 {
                if map.body_location(x, y).intersects(EquipMask::CHEST_ARMOR) {
                    (px, py) = (x, y);
                    break 'find;
                }
            }
        }
        let (ox, oy) = host.root.screen_position(mask);
        let (sx, sy) = (ox as i32 + px, oy as i32 + py);
        host.take_outputs();
        host.mouse_down(MouseButton::Left, sx, sy, 0);
        host.mouse_up(MouseButton::Left, sx, sy, 0);
        host.mouse_down(MouseButton::Right, sx, sy, 0);
        host.mouse_up(MouseButton::Right, sx, sy, 0);
        update(&mut host);
        let out: Vec<PanelOutput> = host
            .take_outputs()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Panel(p) => Some(p),
                _ => None,
            })
            .collect();
        assert_dbg_eq!(
            out,
            vec![
                PanelOutput::SelectObject(0xA01),
                PanelOutput::SelectObject(0xA01),
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                    ObjectRef::Guid(0xA01)
                ))),
            ]
        );
        host.mouse_down(MouseButton::Left, sx, sy, 0);
        host.mouse_move(sx, sy + 20);
        let lifted = host
            .root
            .drag_payload()
            .and_then(|p| p.get::<ItemDragPayload>().cloned())
            .expect("a lift from the figure");
        assert_eq!(
            (lifted.obj_id, lifted.source_kind),
            (0xA01, ItemDragSource::Equipment)
        );
        host.mouse_up(MouseButton::Left, sx, sy + 20, 0);
        assert!(c.take_errors().is_empty());
    }
}

// ── Pack presses, pack-icon joins, a new character ──────────────────────

// OpenAC `PressingAnUnopenedPack_opensIt_andTheSamePressDragsIt`: opening
// a pack changes only the contents grid, so the pressed pack cell stays
// and the same press can become a drag of it.
#[test]
fn pressing_an_unopened_pack_opens_it_and_the_same_press_drags_it() {
    let mut o = Objects::new();
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    o.bag(0xC0, 1, 24);
    o.contained(0xD0, 0xC0, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let pack = inv.cell(inv.containers, 0);
    let main = inv.cell(inv.top, 0);
    // The pack column is at (10, 100); its first cell is under (28, 118).
    inv.host.mouse_down(MouseButton::Left, 28, 118, 0);
    inv.update(&view);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xD0, "the pack opened");
    assert_eq!(inv.cell(inv.containers, 0), pack);
    assert_eq!(inv.cell(inv.top, 0), main);
    inv.host.mouse_move(48, 118);
    assert_eq!(inv.host.root.drag_source(), Some(pack));
    assert!(
        inv.host
            .root
            .drag_payload()
            .and_then(|p| p.get::<ItemDragPayload>())
            .is_some_and(|p| p.obj_id == 0xC0)
    );
}

fn stack(o: &mut Objects, id: u32, container: u32, slot: i32, size: i32, kind: u32) {
    o.contained(id, container, slot, ItemType::NONE);
    let s = o.get_mut(id);
    s.weenie_class_id = kind;
    s.stack_size = size;
    s.stack_size_max = 100;
}

fn join_drop(item: u32, container: ObjectRef, amount: u32) -> PanelOutput {
    PanelOutput::Session(SessionCommand::Item(ItemCommand::DropInto {
        item: ObjectRef::Guid(item),
        container,
        slot: 0,
        amount,
        pickup: false,
        join_stack: true,
    }))
}

// OpenAC `StackDroppedOnAPackIcon_joinsTheFirstStackThereWithRoomForAllOfIt`:
// a stack dropped on a pack's icon joins the first stack there with room
// for all of it (one with room for part is passed over) and selects it.
#[test]
fn a_stack_dropped_on_a_pack_icon_joins_the_first_stack_there_with_room_for_all_of_it() {
    let mut o = Objects::new();
    stack(&mut o, 0xA, PLAYER, 0, 10, 273);
    o.bag(0xC0, 1, 24);
    stack(&mut o, 0xC1, 0xC0, 0, 95, 273);
    stack(&mut o, 0xC2, 0xC0, 1, 50, 273);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        out,
        vec![
            join_drop(0xA, ObjectRef::Guid(0xC0), 0),
            PanelOutput::SelectObject(0xC2),
        ]
    );
}

// OpenAC `StackDroppedOnAFullPackIcon_stillJoinsAStackInIt`: joining takes
// no slot.
#[test]
fn a_stack_dropped_on_a_full_pack_icon_still_joins_a_stack_in_it() {
    let mut o = Objects::new();
    stack(&mut o, 0xA, PLAYER, 0, 10, 273);
    o.bag(0xC0, 1, 2);
    stack(&mut o, 0xC1, 0xC0, 0, 5, 999);
    stack(&mut o, 0xC2, 0xC0, 1, 50, 273);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        out,
        vec![
            join_drop(0xA, ObjectRef::Guid(0xC0), 0),
            PanelOutput::SelectObject(0xC2),
        ]
    );
}

// OpenAC `StackDroppedOnTheMainPackIcon_joinsAStackInASidePack`.
#[test]
fn a_stack_dropped_on_the_main_pack_icon_joins_a_stack_in_a_side_pack() {
    let mut o = Objects::new();
    o.player(102, 7);
    o.bag(0xC0, 0, 24);
    stack(&mut o, 0xC2, 0xC0, 0, 50, 273);
    o.bag(0xC3, 1, 24);
    stack(&mut o, 0xA, 0xC3, 0, 10, 273);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let t = inv.top;
    let out = drop_on(&mut inv, t, 0, payload(0xA), &view);
    assert_dbg_eq!(
        out,
        vec![
            join_drop(0xA, ObjectRef::Player, 0),
            PanelOutput::SelectObject(0xC2),
        ]
    );
}

// OpenAC `StackDroppedOnAPackIcon_withNoStackWithRoomForAllOfIt_movesIntoThePack`.
#[test]
fn a_stack_dropped_on_a_pack_icon_with_no_stack_to_join_moves_into_the_pack() {
    let mut o = Objects::new();
    stack(&mut o, 0xA, PLAYER, 0, 10, 273);
    o.bag(0xC0, 1, 24);
    stack(&mut o, 0xC1, 0xC0, 0, 95, 273);
    let mut inv = inventory_window(None, InventorySprites::default());
    let view = ItemsView::new(&o.t, PLAYER);
    inv.update(&view);
    let c = inv.containers;
    let out = drop_on(&mut inv, c, 0, payload(0xA), &view);
    assert_dbg_eq!(
        moves(&out),
        vec![(0xAu32, ObjectRef::Guid(0xC0), 0i32, 0u32)]
    );
}

// OpenAC `SessionTableClear_returnsTheWindowToTheMainPack`: the session
// ending (the window's copy of the items cleared) or another character
// sends the window back to the main pack.
#[test]
fn a_new_session_or_character_opens_the_window_on_the_main_pack() {
    let open_of = |inv: &mut Inv, player: u32| {
        inv.host
            .with_controller::<InventoryController, _>("inventory", |c, _| c.open_container(player))
            .unwrap()
    };
    let mut o = Objects::new();
    o.bag(0xC0, 0, 24);
    o.contained(0xD0, 0xC0, 0, ItemType::NONE);
    let mut inv = inventory_window(None, InventorySprites::default());
    inv.update(&ItemsView::new(&o.t, PLAYER));
    let c = inv.containers;
    inv.click(c, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(open_of(&mut inv, PLAYER), 0xC0);
    inv.outputs();
    // The same character again, after the session's items were cleared.
    let mut events = Vec::new();
    o.t.clear(&mut events);
    o.contained(0xA, PLAYER, 0, ItemType::NONE);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(open_of(&mut inv, PLAYER), PLAYER);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xA);
    // The runtime hears that no side pack is open any more.
    assert_dbg_eq!(
        sessions(&inv.outputs()),
        vec![SessionCommand::Item(ItemCommand::SetOpenPack(0))]
    );
    // Another character.
    o.bag(0xC0, 1, 24);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    inv.click(c, 0);
    inv.update(&ItemsView::new(&o.t, PLAYER));
    assert_eq!(open_of(&mut inv, PLAYER), 0xC0);
    const OTHER: u32 = 0x5000_0002;
    let mut other = Objects::new();
    other.contained(0xE0, OTHER, 0, ItemType::NONE);
    inv.update(&ItemsView::new(&other.t, OTHER));
    assert_eq!(open_of(&mut inv, OTHER), OTHER);
    assert_eq!(inv.slot(inv.grid, 0).object_id, 0xE0);
}
