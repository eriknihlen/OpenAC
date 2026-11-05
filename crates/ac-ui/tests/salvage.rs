//! The salvage window.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SalvageUiControllerTests.cs`
//! on a synthetic window shaped like the retail one (layout 0x2100000C).
//! OpenAC's `SendSalvage` is the `ItemCommand::Salvage` output; a send that
//! fails is the runtime's one-request gate (`ItemsView::can_make_request`).
//! OpenAC marks a staged item's trade state in the shared object table;
//! here the window tells the session the staged set (`MarkSalvage`).

mod common;

use std::collections::HashMap;

use ac_core::items::{ClientObject, ItemType};
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::items::{ItemsView, NameResolver};
use ac_ui::panels::salvage::{self, SalvageController};
use ac_ui::widgets::{ItemList, ItemSlot, Scrollbar};
use ac_ui::{ElementId, MouseButton, UiOutput};
use common::items::*;
use common::panels::*;
use common::*;

const TOOL: u32 = 0x6000_0101;
const WINDOW: &str = "salvage";

struct Harness {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    objects: Objects,
    icons: TestIcons,
    multiple: bool,
    can_make_request: bool,
}

fn harness() -> Harness {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 320.0, 80.0));
    let mut ids = HashMap::new();
    ids.insert(salvage::ITEM_LIST_ID, list(ui, root, 0.0, 0.0, 320.0, 32.0));
    let bar = ui.add(root, at(0.0, 34.0, 320.0, 16.0), Scrollbar::default());
    ids.insert(salvage::SCROLLBAR_ID, bar);
    add_std_button(ui, &mut ids, root, salvage::SALVAGE_BUTTON_ID);
    add_std_button(ui, &mut ids, root, salvage::CLOSE_BUTTON_ID);
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window(WINDOW, root, None, 0).unwrap();
    let c = SalvageController::bind(ui, &layout, WINDOW, 0).expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    let mut tool = ClientObject::new(TOOL);
    tool.item_type = ItemType::TINKERING_TOOL;
    objects.add(tool);
    objects.mv(TOOL, PLAYER, 0);
    let mut h = Harness {
        host,
        ids,
        objects,
        icons: TestIcons::default(),
        multiple: true,
        can_make_request: true,
    };
    h.update_opening(&[TOOL]);
    h
}

impl Harness {
    fn item(&mut self, id: u32, material: u32, structure: i32, container: u32) {
        let mut o = ClientObject::new(id);
        o.name = format!("Item {id}");
        o.material_type = Some(material);
        o.structure = structure;
        o.icon_id = 123;
        self.objects.add(o);
        self.objects.mv(id, container, 0);
    }

    fn with<R>(
        &mut self,
        f: impl FnOnce(&mut SalvageController, &mut ac_ui::root::UiRoot, &ItemsView<'_>) -> R,
    ) -> R {
        let names = |o: &ClientObject| composed_name(o);
        let resolver: NameResolver<'_> = &names;
        let view = ItemsView {
            names: Some(resolver),
            can_make_request: self.can_make_request,
            ..ItemsView::new(&self.objects.t, PLAYER)
        };
        self.host
            .with_controller::<SalvageController, _>(WINDOW, |c, ui| f(c, ui, &view))
            .unwrap()
    }

    fn update_opening(&mut self, opened: &[u32]) {
        let multiple = self.multiple;
        let names = |o: &ClientObject| composed_name(o);
        let resolver: NameResolver<'_> = &names;
        let view = ItemsView {
            names: Some(resolver),
            can_make_request: self.can_make_request,
            ..ItemsView::new(&self.objects.t, PLAYER)
        };
        let icons = &mut self.icons;
        self.host
            .with_controller::<SalvageController, _>(WINDOW, |c, ui| {
                c.update(ui, &view, opened, multiple, icons);
            })
            .unwrap();
    }

    fn update(&mut self) {
        self.update_opening(&[]);
    }

    fn add(&mut self, id: u32) -> bool {
        let multiple = self.multiple;
        let added = self.with(|c, ui, v| c.add_item(ui, id, v, multiple));
        self.update();
        added
    }

    fn can_add(&mut self, id: u32) -> bool {
        let multiple = self.multiple;
        self.with(|c, _, v| c.can_add(id, v, multiple))
    }

    fn count(&mut self) -> usize {
        self.with(|c, _, _| c.item_count())
    }

    fn staged(&mut self) -> Vec<u32> {
        self.with(|c, _, _| c.staged().collect())
    }

    fn button_enabled(&self) -> bool {
        self.host
            .root
            .element(self.ids[&salvage::SALVAGE_BUTTON_ID])
            .unwrap()
            .enabled
    }

    fn visible(&self) -> bool {
        self.host.root.is_window_visible(WINDOW)
    }

    fn cell(&self, i: usize) -> ElementId {
        self.host
            .root
            .widget::<ItemList>(self.ids[&salvage::ITEM_LIST_ID])
            .unwrap()
            .item(i)
            .unwrap()
    }

    fn slot(&self, i: usize) -> &ItemSlot {
        self.host.root.widget::<ItemSlot>(self.cell(i)).unwrap()
    }

    fn fire(&mut self, i: usize, k: i32, p: Option<ItemDragPayload>) {
        let c = self.cell(i);
        self.host.root.fire_event(k, c, p.map(DragPayload::new));
        self.host.pump();
    }

    fn click_salvage(&mut self) {
        let b = self.ids[&salvage::SALVAGE_BUTTON_ID];
        self.host.root.fire_event(kind::CLICK, b, None);
        self.host.pump();
        self.update();
    }

    fn outputs(&mut self) -> Vec<PanelOutput> {
        panel_outputs(&mut self.host)
    }
}

fn salvages(outputs: &[PanelOutput]) -> Vec<(u32, Vec<u32>)> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(ItemCommand::Salvage {
                tool: ObjectRef::Guid(tool),
                items,
            }) => Some((
                tool,
                items
                    .into_iter()
                    .filter_map(|i| match i {
                        ObjectRef::Guid(g) => Some(g),
                        _ => None,
                    })
                    .collect(),
            )),
            _ => None,
        })
        .collect()
}

fn notices(outputs: &[PanelOutput]) -> Vec<String> {
    outputs
        .iter()
        .filter_map(|p| match p {
            PanelOutput::Notice(t) => Some(t.clone()),
            _ => None,
        })
        .collect()
}

fn payload(id: u32) -> ItemDragPayload {
    ItemDragPayload::new(id, ItemDragSource::Inventory, 0)
}

// Hover_caption_carries_the_material_prefix_and_stays_plain_without_one.
#[test]
fn the_hover_caption_carries_the_material_prefix() {
    let mut h = harness();
    h.objects.add(material_item(1));
    h.objects.mv(1, PLAYER, 0);
    if let Some(o) = h.objects.t.get_mut(1) {
        o.structure = 0;
    }
    assert!(h.add(1));
    assert_eq!(h.slot(0).tooltip.as_deref(), Some("Pyreal Scarab"));
}

// Eligible_drop_marks_item_and_submit_sends_once_without_removing_inventory.
#[test]
fn an_eligible_drop_stages_and_the_button_sends_once() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    assert!(h.visible());
    assert!(!h.button_enabled());
    let multiple = h.multiple;
    let accept = h.with(|c, _, v| c.drag_over(&payload(1), v, multiple));
    assert_eq!(accept, DragAcceptance::Accept);
    // A drop on the row (the cells are empty slots).
    h.fire(0, kind::DROP_RELEASED, Some(payload(1)));
    h.update();
    assert_eq!(h.count(), 1);
    assert!(h.slot(0).show_trade_overlay);
    assert!(h.button_enabled());
    let again = h.with(|c, _, v| c.drag_over(&payload(1), v, multiple));
    assert_eq!(again, DragAcceptance::Reject);
    h.outputs();
    h.click_salvage();
    h.click_salvage();
    assert_eq!(salvages(&h.outputs()), [(TOOL, vec![1])]);
    assert_eq!(h.count(), 0);
    assert!(!h.button_enabled());
    assert!(h.visible());
    assert!(
        h.objects.t.get(1).is_some(),
        "the item stays until the server says"
    );
}

// Invalid_materials_and_full_bags_are_rejected.
#[test]
fn invalid_materials_and_worn_out_items_are_rejected() {
    for (material, structure) in [(0, 0), (3, 0), (9, 0), (56, 0), (65, 0), (72, 0), (60, 100)] {
        let mut h = harness();
        h.item(1, material, structure, PLAYER);
        assert!(!h.can_add(1), "{material} {structure}");
        assert!(!h.add(1));
    }
}

// Container_drop_filters_contents_and_obeys_material_option.
#[test]
fn a_pack_stages_its_suitable_contents_of_one_material() {
    let mut h = harness();
    h.multiple = false;
    let mut pack = ClientObject::new(10);
    pack.item_type = ItemType::CONTAINER;
    pack.items_capacity = 24;
    pack.name = "Pack".into();
    h.objects.add(pack);
    h.objects.mv(10, PLAYER, 0);
    h.item(1, 60, 0, 10);
    h.item(2, 61, 0, 10);
    h.item(3, 60, 100, 10);
    h.outputs();
    assert!(h.add(10));
    assert_eq!(notices(&h.outputs()), ["Adding contents of Pack"]);
    assert_eq!(h.staged(), [1]);
    h.multiple = true;
    assert!(h.add(2));
    assert_eq!(h.count(), 2);
}

fn side_pack(h: &mut Harness, id: u32) {
    let mut pack = ClientObject::new(id);
    pack.item_type = ItemType::CONTAINER;
    pack.items_capacity = 24;
    h.objects.add(pack);
    h.objects.mv(id, PLAYER, 0);
}

// Main_pack_drop_adds_only_its_own_items_not_side_pack_contents
// (OpenAC issue #221): the main pack contributes the
// character's own items; side packs and everything inside them stay out
// until a side pack is dropped itself.
#[test]
fn a_main_pack_drop_stages_its_own_items_not_side_pack_contents() {
    let mut h = harness();
    side_pack(&mut h, 10);
    h.item(1, 60, 0, PLAYER);
    h.item(2, 60, 0, 10);
    let multiple = h.multiple;
    let accept = h.with(|c, _, v| c.drag_over(&payload(PLAYER), v, multiple));
    assert_eq!(accept, DragAcceptance::Accept);
    h.fire(0, kind::DROP_RELEASED, Some(payload(PLAYER)));
    h.update();
    assert_eq!(h.staged(), [1]);
    assert!(h.add(10));
    assert_eq!(h.staged(), [1, 2]);
}

// Main_pack_holding_only_side_packs_is_not_a_container_drop
// (OpenAC issue #221).
#[test]
fn a_main_pack_holding_only_side_packs_is_not_a_container_drop() {
    let mut h = harness();
    side_pack(&mut h, 10);
    h.objects.mv(TOOL, 10, 0);
    h.update();
    h.item(2, 60, 0, 10);
    assert_eq!(h.with(|c, _, _| c.tool()), TOOL, "the tool is still open");
    assert!(!h.can_add(PLAYER));
    assert!(!h.add(PLAYER));
    assert_eq!(h.count(), 0);
}

// Dragging_out_and_double_click_remove_staging.
#[test]
fn dragging_out_and_a_double_click_unstage() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.fire(0, kind::DRAG_BEGIN, Some(payload(1)));
    h.update();
    assert_eq!(h.count(), 0);
    h.add(1);
    h.fire(0, kind::DOUBLE_CLICK, None);
    h.update();
    assert_eq!(h.count(), 0);
    assert!(!h.button_enabled());
}

// Right_click_examines_and_keeps_the_item_staged (OpenAC #251).
#[test]
fn a_right_click_selects_and_examines_and_keeps_the_item_staged() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.outputs();
    h.fire(0, kind::RIGHT_CLICK, None);
    h.update();
    let out = h.outputs();
    assert!(
        out.iter()
            .any(|o| matches!(o, PanelOutput::SelectObject(1))),
        "{out:?}"
    );
    assert!(
        sessions(&out).iter().any(|c| matches!(
            c,
            SessionCommand::Item(ItemCommand::Appraise(ObjectRef::Guid(1)))
        )),
        "{out:?}"
    );
    assert_eq!(h.count(), 1);
    assert!(h.button_enabled());
}

// Staged_partial_bag_keeps_its_structure_meter.
#[test]
fn a_staged_partial_bag_keeps_its_structure_meter() {
    let mut h = harness();
    h.item(1, 60, 25, PLAYER);
    if let Some(o) = h.objects.t.get_mut(1) {
        o.max_structure = 100;
    }
    h.add(1);
    assert_eq!(h.slot(0).structure_fill, 0.25);
    assert!(h.slot(0).show_trade_overlay);
}

// Submit_orders_items_from_last_staged_to_first.
#[test]
fn the_items_go_last_staged_first() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.item(2, 60, 0, PLAYER);
    h.add(1);
    h.add(2);
    h.outputs();
    h.click_salvage();
    assert_eq!(salvages(&h.outputs()), [(TOOL, vec![2, 1])]);
}

// Failed_send_preserves_staged_items_for_retry.
#[test]
fn a_send_that_cannot_be_made_keeps_the_staging() {
    let mut h = harness();
    h.can_make_request = false;
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.outputs();
    h.click_salvage();
    let out = h.outputs();
    assert!(salvages(&out).is_empty());
    assert_eq!(notices(&out), [salvage::CANNOT_SALVAGE_NOW]);
    assert_eq!(h.count(), 1);
    h.can_make_request = true;
    h.click_salvage();
    assert_eq!(salvages(&h.outputs()), [(TOOL, vec![1])]);
}

// Item_move_removes_staging_and_tool_removal_closes_window.
#[test]
fn a_moved_item_unstages_and_a_lost_tool_closes_the_window() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.objects.mv(1, 999, 0);
    h.update();
    assert_eq!(h.count(), 0);
    h.objects.mv(TOOL, 999, 0);
    h.update();
    assert!(!h.visible());
    assert_eq!(h.with(|c, _, _| c.tool()), 0);
}

// Moving_container_out_of_inventory_clears_its_staged_children.
#[test]
fn moving_a_pack_away_unstages_what_is_in_it() {
    let mut h = harness();
    let mut pack = ClientObject::new(10);
    pack.item_type = ItemType::CONTAINER;
    pack.items_capacity = 24;
    h.objects.add(pack);
    h.objects.mv(10, PLAYER, 0);
    h.item(1, 60, 0, 10);
    h.add(1);
    assert_eq!(h.count(), 1);
    h.objects.mv(10, 999, 0);
    h.update();
    assert_eq!(h.count(), 0);
    assert!(!h.button_enabled());
}

/// SalvageUiController.AddRecursive / RemoveItem / Salvage / ClearItems:
/// a staged item's trade state is set in the shared table, and cleared as
/// it leaves the staging (sent, unstaged, the window hidden). Here the
/// window tells the session the staged set when it changes
/// (`ItemCommand::MarkSalvage`); the session marks the table.
#[test]
fn the_session_is_told_the_staged_items_as_they_change() {
    let marks = |out: &[PanelOutput]| {
        sessions(out)
            .into_iter()
            .filter_map(|c| match c {
                SessionCommand::Item(ItemCommand::MarkSalvage(items)) => Some(items),
                _ => None,
            })
            .collect::<Vec<_>>()
    };
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.item(2, 60, 0, PLAYER);
    h.outputs();
    h.add(1);
    h.add(2);
    assert_eq!(marks(&h.outputs()), [vec![1], vec![1, 2]]);
    h.update();
    assert!(marks(&h.outputs()).is_empty(), "told only on a change");
    h.fire(0, kind::DOUBLE_CLICK, None);
    h.update();
    assert_eq!(marks(&h.outputs()), [vec![2]]);
    h.click_salvage();
    let out = h.outputs();
    assert_eq!(salvages(&out), [(TOOL, vec![2])]);
    assert_eq!(marks(&out), [Vec::<u32>::new()]);
    h.add(1);
    h.outputs();
    h.host.root.hide_window(WINDOW);
    h.host.pump();
    assert_eq!(marks(&h.outputs()), [Vec::<u32>::new()]);
}

// Hiding_and_reopening_clears_staging.
#[test]
fn hiding_and_reopening_clears_the_staging() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.host.root.hide_window(WINDOW);
    h.host.pump();
    assert_eq!(h.count(), 0);
    assert!(!h.can_add(1));
    h.update_opening(&[TOOL]);
    assert!(h.can_add(1));
    assert!(!h.button_enabled());
}

/// A staged item the window's table updates (a new record for the same
/// item, as every update of its copy is) stays staged; one no longer
/// suitable leaves (OpenAC `OnObjectChanged`).
#[test]
fn an_updated_item_stays_staged_until_it_is_no_longer_suitable() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.item(1, 60, 0, PLAYER);
    h.update();
    assert_eq!(h.count(), 1);
    h.item(1, 60, 100, PLAYER);
    h.update();
    assert_eq!(h.count(), 0);
}

impl Harness {
    /// The centre of cell `i` in window pixels.
    fn cell_centre(&self, i: usize) -> (i32, i32) {
        let c = self.cell(i);
        let (x, y) = self.host.root.screen_position(c);
        let e = self.host.root.element(c).unwrap();
        ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32)
    }

    /// A press and release of `button` on cell `i` through the root, with a
    /// frame's update between them (the window is fed every frame it is
    /// open), then a frame after.
    fn press_release_across_update(&mut self, button: MouseButton, i: usize) {
        let (x, y) = self.cell_centre(i);
        self.host.mouse_move(x, y);
        self.host.mouse_down(button, x, y, 0);
        self.update();
        self.host.mouse_up(button, x, y, 0);
        self.update();
    }
}

/// A right click is a press and a release on the same cell; the window is
/// fed every frame between them, so the cell must outlive the frame for
/// the release to examine the item.
#[test]
fn a_right_click_through_the_root_examines_across_a_frame() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.outputs();
    h.press_release_across_update(MouseButton::Right, 0);
    let out = h.outputs();
    assert!(
        out.iter()
            .any(|o| matches!(o, PanelOutput::SelectObject(1))),
        "{out:?}"
    );
    assert!(
        sessions(&out).iter().any(|c| matches!(
            c,
            SessionCommand::Item(ItemCommand::Appraise(ObjectRef::Guid(1)))
        )),
        "{out:?}"
    );
    assert_eq!(h.count(), 1);
}

/// A double click through the root, with frames between its clicks,
/// unstages the item.
#[test]
fn a_double_click_through_the_root_unstages_across_frames() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.press_release_across_update(MouseButton::Left, 0);
    assert_eq!(h.count(), 1);
    h.press_release_across_update(MouseButton::Left, 0);
    assert_eq!(h.count(), 0);
    assert!(!h.button_enabled());
}

/// Dragging a staged item out through the root, with a frame between the
/// press and the move, unstages it.
#[test]
fn a_drag_out_through_the_root_unstages_across_a_frame() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    let (x, y) = h.cell_centre(0);
    h.host.mouse_move(x, y);
    h.host.mouse_down(MouseButton::Left, x, y, 0);
    h.update();
    h.host.mouse_move(x + 200, y + 300);
    h.update();
    h.host.mouse_up(MouseButton::Left, x + 200, y + 300, 0);
    h.update();
    assert_eq!(h.count(), 0);
}

/// A staged item let go over the world says it came from the salvage
/// window, so the world only unstages it and never drops it on the ground
/// (issue #32).
#[test]
fn a_drag_out_over_the_world_comes_from_the_salvage_window() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    h.host.take_outputs();
    let (x, y) = h.cell_centre(0);
    h.host.mouse_move(x, y);
    h.host.mouse_down(MouseButton::Left, x, y, 0);
    h.host.mouse_move(x + 200, y + 300);
    h.host.mouse_up(MouseButton::Left, x + 200, y + 300, 0);
    let released: Vec<ItemDragSource> = h
        .host
        .take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::DragReleasedOutsideUi { payload, .. } => {
                payload.get::<ItemDragPayload>().map(|p| p.source_kind)
            }
            _ => None,
        })
        .collect();
    assert_eq!(released, [ItemDragSource::Salvage]);
}

/// An alias drop is refused everywhere, the salvage window included: a
/// staged item dragged out and let go over the row again stays unstaged.
#[test]
fn a_staged_item_dropped_back_on_the_row_stays_unstaged() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.item(2, 60, 0, PLAYER);
    h.add(1);
    h.add(2);
    let lifted = ItemDragPayload::new(1, ItemDragSource::Salvage, 1);
    h.fire(1, kind::DRAG_BEGIN, Some(lifted.clone()));
    h.update();
    assert_eq!(h.staged(), [2]);
    h.fire(0, kind::DRAG_ENTER, Some(lifted.clone()));
    h.update();
    assert_eq!(h.slot(0).drag_acceptance(), DragAcceptance::Reject);
    h.fire(0, kind::DROP_RELEASED, Some(lifted));
    h.update();
    assert_eq!(h.staged(), [2]);
}

/// A frame with nothing changed keeps the row's cells (the same elements),
/// and still draws what changed on a staged item.
#[test]
fn a_frame_keeps_the_cells_and_refreshes_what_they_draw() {
    let mut h = harness();
    h.item(1, 60, 0, PLAYER);
    h.add(1);
    let before = h.cell(0);
    h.update();
    assert_eq!(h.cell(0), before);
    if let Some(o) = h.objects.t.get_mut(1) {
        o.icon_id = 456;
    }
    let icon_before = h.slot(0).icon;
    h.update();
    assert_eq!(h.cell(0), before);
    assert_ne!(h.slot(0).icon, icon_before);
}

/// More staged items than the row shows at once (10 cells fit): every
/// staged item keeps its cell, and the next update leaves the cells alone.
#[test]
fn more_staged_items_than_fit_keep_a_cell_each_across_updates() {
    let mut h = harness();
    let items: Vec<u32> = (1..=14).collect();
    for &id in &items {
        h.item(id, 60, 0, PLAYER);
        assert!(h.add(id));
    }
    let list = h.ids[&salvage::ITEM_LIST_ID];
    let cells = |h: &Harness| {
        h.host
            .root
            .widget::<ItemList>(list)
            .unwrap()
            .cells()
            .to_vec()
    };
    let first = cells(&h);
    assert_eq!(first.len(), items.len());
    for (i, &id) in items.iter().enumerate() {
        assert_eq!(h.slot(i).object_id, id);
    }
    h.update();
    assert_eq!(cells(&h), first);
}
