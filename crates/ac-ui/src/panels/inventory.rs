//! The inventory window: the open pack's contents grid, the side-pack
//! column, the main-pack cell, the burden meter and the captions, and
//! everything dropped on them (moves between and inside packs, stack
//! merges and splits, packs falling through to one with room).
//!
//! The window shows the object table of the [`ItemsView`] it is handed;
//! presses, clicks and drops on its cells come back as element messages,
//! are queued, and are resolved against the next view. Requests go
//! out as [`PanelOutput`]s (session item commands, selections, notices).

use std::sync::Arc;

use ac_core::items::placement::{
    choose_container, empty_item_slots, evaluate_container_placement, item_list_drop_placement,
    placement_rejection_text,
};
use ac_core::items::{
    ClientObject, EquipMask, ItemType, PlacementRejection, StackMergeItem, StackMergePlan, burden,
};
use ac_runtime::inventory::{ItemCommand, ObjectRef, placement_conflict_text, plan_join_stack};
use ac_runtime::session::SessionCommand;

use crate::event::{DragAcceptance, ItemDragPayload, ItemDragSource};
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::{
    ItemsView, bind_click, caption_text, emit, fill_slot, offer_target, set_caption,
    stamp_shortcut_number,
};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::item_slot::{
    DEFAULT_DRAG_ACCEPT_SPRITE, DEFAULT_DRAG_REJECT_SPRITE, PACK_DRAG_ACCEPT_SPRITE, slot_element,
};
use crate::widgets::{ItemList, ItemSlot, Meter, ScrollSource, Scrollbar, ShortcutDigits};

/// The inventory window's layout.
pub const LAYOUT_ID: u32 = 0x2100_0023;
/// The layout the contents grid's cells come from.
pub const CONTENTS_LAYOUT_ID: u32 = 0x2100_0021;
/// The layout the pack column's cells come from.
pub const BACKPACK_LAYOUT_ID: u32 = 0x2100_0022;
/// The open pack's contents.
pub const CONTENTS_GRID_ID: u32 = 0x1000_01C6;
/// The side-pack column.
pub const CONTAINER_LIST_ID: u32 = 0x1000_01CA;
/// The main-pack cell.
pub const TOP_CONTAINER_ID: u32 = 0x1000_01C9;
/// The burden meter.
pub const BURDEN_METER_ID: u32 = 0x1000_01D9;
/// The burden percentage.
pub const BURDEN_TEXT_ID: u32 = 0x1000_01D8;
/// "Burden".
pub const BURDEN_CAPTION_ID: u32 = 0x1000_01D7;
/// "Contents of ...".
pub const CONTENTS_CAPTION_ID: u32 = 0x1000_01C5;
/// "Inventory of ...".
pub const TITLE_TEXT_ID: u32 = 0x1000_01D3;
/// The contents grid's scrollbar.
pub const CONTENTS_SCROLLBAR_ID: u32 = 0x1000_01C7;
/// The window's close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_01D2;
const BACKDROP_ID: u32 = 0x1000_01D0;
const CONTENTS_WINDOW_ID: u32 = 0x1000_01CF;
const PAPERDOLL_WINDOW_ID: u32 = 0x1000_01CD;
const BACKPACK_WINDOW_ID: u32 = 0x1000_01CE;

/// Columns of the contents grid.
pub const CONTENTS_COLUMNS: usize = 6;
/// Contents cell size, pixels.
pub const CONTENTS_CELL_PX: f32 = 32.0;
/// Pack-column cell size, pixels.
pub const BACKPACK_CELL_PX: f32 = 36.0;
/// Side-pack slots shown at most.
pub const SIDE_BAG_SLOTS: usize = 7;
/// Main pack slots when the player names no capacity.
pub const MAIN_PACK_SLOTS: usize = 102;
/// Side pack slots when a pack names no capacity.
pub const SIDE_PACK_SLOTS: usize = 24;
/// The fixed icon of the main-pack cell (the player has none of his own).
pub const PLAYER_PACK_BASE_ICON: u32 = 0x0600_127E;
/// The player's carried-burden property.
pub const ENCUMBRANCE_VAL_PROPERTY: u32 = 5;
/// The player's burden augmentation property.
pub const ENCUMBRANCE_AUG_PROPERTY: u32 = 0xE6;

/// The empty-cell images of the three lists (from the cell prototypes;
/// see [`crate::panels::cell_template::empty_sprite`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct InventorySprites {
    /// The contents grid.
    pub contents_empty: u32,
    /// The side-pack column.
    pub side_bag_empty: u32,
    /// The main-pack cell.
    pub main_pack_empty: u32,
}

/// The selection as a panel shows it: its own choice at once, the
/// runtime's when that changes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SelectionEcho {
    local: Option<u32>,
    seen: Option<Option<u32>>,
}

impl SelectionEcho {
    /// Follow the runtime's selection when it changed since last seen.
    pub fn sync(&mut self, runtime: Option<u32>) {
        if self.seen != Some(runtime) {
            self.seen = Some(runtime);
            self.local = runtime;
        }
    }

    /// The panel selected `id` itself.
    pub fn select(&mut self, id: u32) {
        self.local = Some(id);
    }

    /// The selection shown.
    #[must_use]
    pub fn get(&self) -> Option<u32> {
        self.local
    }
}

/// The inventory window's controller.
#[derive(Debug)]
pub struct InventoryController {
    contents_grid: Option<ElementId>,
    container_list: Option<ElementId>,
    top_container: Option<ElementId>,
    burden_meter: Option<ElementId>,
    title: Option<ElementId>,
    burden_caption: Option<ElementId>,
    contents_caption: Option<ElementId>,
    burden_text: Option<ElementId>,
    close_button: Option<ElementId>,
    open_container: u32,
    /// The character and the clear count of the items last shown: a change
    /// is another session or character.
    shown_session: Option<(u32, u64)>,
    selection: SelectionEcho,
    target_source: Option<u32>,
    target_mode: bool,
    pending_cache: Vec<u32>,
    player: u32,
    queue: Vec<(ElementId, ElementMessage)>,
    burden_fill: f32,
    burden_percent: i32,
    digits: Option<Arc<ShortcutDigits>>,
    pack_column: PackColumnGeometry,
}

/// What a longer pack column (an augmentation raising the pack count)
/// grows: the column itself, the backpack window around it, the inventory,
/// and the window frame the client mounts the inventory in. The authored
/// sizes are the floor it grows from.
#[derive(Debug, Clone, Copy)]
struct PackColumnGeometry {
    inventory_window: ElementId,
    backpack_window: Option<ElementId>,
    column_height: f32,
    backpack_height: f32,
    inventory_height: f32,
    inventory_min_height: f32,
    /// The frame, its chrome (frame height less the inventory's) and its
    /// authored minimum height, when the inventory is mounted in one.
    frame: Option<(ElementId, f32, f32)>,
}

impl PackColumnGeometry {
    fn capture(ui: &UiRoot, layout: &ImportedLayout, container_list: Option<ElementId>) -> Self {
        let height =
            |id: Option<ElementId>| id.and_then(|e| ui.element(e)).map_or(0.0, |e| e.height);
        let inventory_window = layout.root;
        let (inventory_height, inventory_min_height) = ui
            .element(inventory_window)
            .map_or((0.0, 0.0), |e| (e.height, e.min_height));
        let backpack_window = layout.find(BACKPACK_WINDOW_ID);
        let frame = ui
            .parent(inventory_window)
            .filter(|&p| p != ui.root())
            .and_then(|p| {
                let e = ui.element(p)?;
                Some((p, e.height - inventory_height, e.min_height))
            });
        Self {
            inventory_window,
            backpack_window,
            column_height: height(container_list),
            backpack_height: height(backpack_window),
            inventory_height,
            inventory_min_height,
            frame,
        }
    }
}

fn configure_resize_layout(ui: &mut UiRoot, layout: &ImportedLayout) {
    let stretch = AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM;
    for (id, anchors) in [
        (BACKDROP_ID, stretch),
        (CONTENTS_WINDOW_ID, stretch),
        (CONTENTS_GRID_ID, stretch),
        (CONTENTS_SCROLLBAR_ID, stretch),
        (PAPERDOLL_WINDOW_ID, AnchorEdges::TOP_LEFT),
        (BACKPACK_WINDOW_ID, AnchorEdges::TOP_LEFT),
    ] {
        if let Some(e) = layout.find(id) {
            ui.set_anchors(e, anchors);
            ui.capture_current_anchor_baseline(e);
        }
    }
}

pub(crate) fn is_bag(item: &ClientObject) -> bool {
    ac_core::items::placement::is_pack(item)
}

fn is_capacity_rejection(r: Option<PlacementRejection>) -> bool {
    matches!(
        r,
        Some(PlacementRejection::ItemCapacityFull | PlacementRejection::ContainerCapacityFull)
    )
}

fn item_list(ui: &mut UiRoot, layout: &ImportedLayout, id: u32) -> Option<ElementId> {
    layout
        .find(id)
        .filter(|l| ui.widget::<ItemList>(*l).is_some())
}

fn cells(ui: &UiRoot, list: Option<ElementId>) -> Vec<ElementId> {
    list.and_then(|l| ui.widget::<ItemList>(l))
        .map(|l| l.cells().to_vec())
        .unwrap_or_default()
}

fn slot(ui: &UiRoot, cell: ElementId) -> Option<&ItemSlot> {
    ui.widget::<ItemSlot>(cell)
}

impl InventoryController {
    /// Bind to an imported inventory window.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        sprites: InventorySprites,
        font: Option<Arc<crate::UiFont>>,
        digits: Option<Arc<ShortcutDigits>>,
    ) -> Self {
        let contents_grid = item_list(ui, layout, CONTENTS_GRID_ID);
        let container_list = item_list(ui, layout, CONTAINER_LIST_ID);
        let top_container = item_list(ui, layout, TOP_CONTAINER_ID);
        configure_resize_layout(ui, layout);
        let pack_column = PackColumnGeometry::capture(ui, layout, container_list);
        if let Some(g) = contents_grid
            && let Some(l) = ui.widget_mut::<ItemList>(g)
        {
            l.columns = CONTENTS_COLUMNS;
            l.cell_width = CONTENTS_CELL_PX;
            l.cell_height = CONTENTS_CELL_PX;
        }
        if let (Some(g), Some(bar)) = (contents_grid, layout.find(CONTENTS_SCROLLBAR_ID))
            && let Some(b) = ui.widget_mut::<Scrollbar>(bar)
        {
            b.model = ScrollSource::Element(g);
        }
        if let Some(c) = container_list
            && let Some(l) = ui.widget_mut::<ItemList>(c)
        {
            l.columns = 1;
            l.cell_width = BACKPACK_CELL_PX;
            l.cell_height = BACKPACK_CELL_PX;
        }
        for (list, sprite) in [
            (contents_grid, sprites.contents_empty),
            (container_list, sprites.side_bag_empty),
            (top_container, sprites.main_pack_empty),
        ] {
            if let Some(l) = list {
                ui.with::<ItemList, _>(l, |w, ui| w.set_cell_empty_sprite(ui, sprite));
            }
        }
        let burden_meter = layout
            .find(BURDEN_METER_ID)
            .filter(|m| ui.widget::<Meter>(*m).is_some());
        if let Some(m) = burden_meter
            && let Some(w) = ui.widget_mut::<Meter>(m)
        {
            w.vertical = true;
            w.fill_from_bottom = true;
            w.fill = Some(0.0);
        }
        let caption = |ui: &mut UiRoot, id| {
            layout
                .find(id)
                .and_then(|h| caption_text(ui, h, font.clone()))
        };
        let title = caption(ui, TITLE_TEXT_ID);
        let burden_caption = caption(ui, BURDEN_CAPTION_ID);
        let contents_caption = caption(ui, CONTENTS_CAPTION_ID);
        let burden_text = caption(ui, BURDEN_TEXT_ID);
        let close_button = layout.find(CLOSE_BUTTON_ID).filter(|b| bind_click(ui, *b));
        let c = Self {
            contents_grid,
            container_list,
            top_container,
            burden_meter,
            title,
            burden_caption,
            contents_caption,
            burden_text,
            close_button,
            open_container: 0,
            shown_session: None,
            selection: SelectionEcho::default(),
            target_source: None,
            target_mode: false,
            pending_cache: Vec::new(),
            player: 0,
            queue: Vec::new(),
            burden_fill: 0.0,
            burden_percent: 0,
            digits,
            pack_column,
        };
        set_caption(ui, c.burden_caption, "Burden");
        c.refresh_captions(ui, None, "Player");
        c
    }

    /// The contents grid.
    #[must_use]
    pub fn contents_grid(&self) -> Option<ElementId> {
        self.contents_grid
    }

    /// The side-pack column.
    #[must_use]
    pub fn container_list(&self) -> Option<ElementId> {
        self.container_list
    }

    /// The main-pack cell's list.
    #[must_use]
    pub fn top_container(&self) -> Option<ElementId> {
        self.top_container
    }

    /// The burden meter's fill, 0..=1.
    #[must_use]
    pub fn burden_fill(&self) -> f32 {
        self.burden_fill
    }

    /// The burden percentage shown.
    #[must_use]
    pub fn burden_percent(&self) -> i32 {
        self.burden_percent
    }

    /// The caption elements: title, "Burden", contents, percentage.
    #[must_use]
    pub fn captions(&self) -> [Option<ElementId>; 4] {
        [
            self.title,
            self.burden_caption,
            self.contents_caption,
            self.burden_text,
        ]
    }

    /// The pack whose contents show (the player for the main pack).
    #[must_use]
    pub fn open_container(&self, player: u32) -> u32 {
        if self.open_container != 0 {
            self.open_container
        } else {
            player
        }
    }

    /// Resolve what was queued against `view`, then show it.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.follow_session(ui, view);
        self.player = view.player;
        self.target_source = view.target_source;
        self.target_mode = view.target_mode;
        self.pending_cache = view.pending_sources.to_vec();
        self.selection.sync(view.selected);
        let queued = std::mem::take(&mut self.queue);
        for (element, message) in queued {
            self.handle(ui, element, &message, view);
        }
        self.populate(ui, view, icons);
    }

    /// The session's items were replaced (the window's copy cleared: the
    /// session ended, or the window moved to another) or another character
    /// is shown: the pack that was open belonged to the one before, so the
    /// window goes back to the main pack, and the session hears that no
    /// side pack is open.
    fn follow_session(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>) {
        let session = (view.player, view.objects.clear_count());
        let before = self.shown_session.replace(session);
        if before.is_none_or(|b| b == session) || self.open_container == 0 {
            return;
        }
        self.open_container = 0;
        if let Some(g) = self.contents_grid
            && let Some(l) = ui.widget_mut::<ItemList>(g)
        {
            l.scroll.set_scroll_y(0);
        }
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::SetOpenPack(0))),
        );
    }

    fn list_of(&self, ui: &UiRoot, cell: ElementId) -> Option<ElementId> {
        let parent = ui.parent(cell)?;
        [self.contents_grid, self.container_list, self.top_container]
            .into_iter()
            .flatten()
            .find(|l| *l == parent)
    }

    fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
        view: &ItemsView<'_>,
    ) {
        let Some(list) = self.list_of(ui, element) else {
            return;
        };
        match message {
            ElementMessage::ItemDragLifted(p)
                if p.obj_id != 0 && self.selection.get() != Some(p.obj_id) =>
            {
                self.select(ui, p.obj_id);
            }
            ElementMessage::ItemDragEntered(p) => {
                let a = self.drag_over(ui, list, element, p, view);
                if let Some(s) = ui.widget_mut::<ItemSlot>(element) {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) => self.drop_release(ui, list, element, p, view),
            _ => {}
        }
    }

    fn select(&mut self, ui: &mut UiRoot, id: u32) {
        if id == 0 {
            return;
        }
        self.selection.select(id);
        emit(ui, PanelOutput::SelectObject(id));
    }

    fn open(&mut self, ui: &mut UiRoot, guid: u32) {
        if guid == 0 {
            return;
        }
        self.select(ui, guid);
        let open = self.open_container(self.player);
        if guid == open {
            return;
        }
        if let Some(g) = self.contents_grid
            && let Some(l) = ui.widget_mut::<ItemList>(g)
        {
            l.scroll.set_scroll_y(0);
        }
        self.open_container = guid;
        // The session's pickups name the pack this window shows open.
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::SetOpenPack(guid))),
        );
        if guid != self.player {
            emit(
                ui,
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(
                    guid,
                )))),
            );
        }
    }

    /// A pack the character carries was used from anywhere (a hotbar
    /// shortcut, a double-click): the window opens on it, the main pack when
    /// it is the character. True when `guid` is such a pack, so the caller
    /// shows the window. No use request goes out from here; a side pack's
    /// went out with the use itself.
    pub fn open_contained_container(
        &mut self,
        ui: &mut UiRoot,
        guid: u32,
        view: &ItemsView<'_>,
    ) -> bool {
        self.follow_session(ui, view);
        let player = view.player;
        let Some(item) = view.get(guid).filter(|_| guid != 0) else {
            return false;
        };
        let owned = guid == player || view.objects.is_owned_by_object(guid, player);
        if !owned || !is_bag(item) {
            return false;
        }
        self.player = player;
        if guid == self.open_container(player) {
            return true;
        }
        if let Some(g) = self.contents_grid
            && let Some(l) = ui.widget_mut::<ItemList>(g)
        {
            l.scroll.set_scroll_y(0);
        }
        self.open_container = guid;
        // The session's pickups name the pack this window shows open.
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::SetOpenPack(guid))),
        );
        true
    }

    fn press(&mut self, ui: &mut UiRoot, list: ElementId, cell: ElementId) {
        let Some(id) = slot(ui, cell).map(|s| s.object_id).filter(|id| *id != 0) else {
            return;
        };
        if self.target_mode {
            // The target mode takes the press: the click that follows does
            // nothing and the press never becomes a drag.
            if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                s.consume_primary_press();
            }
            let target = if Some(list) == self.top_container {
                ObjectRef::Player
            } else {
                ObjectRef::Guid(id)
            };
            offer_target(ui, target);
            return;
        }
        if Some(list) == self.contents_grid {
            self.select(ui, id);
        } else {
            self.open(ui, id);
        }
    }

    fn drag_over(
        &self,
        ui: &UiRoot,
        list: ElementId,
        cell: ElementId,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) -> DragAcceptance {
        if p.source_kind.is_alias() {
            return DragAcceptance::None;
        }
        let target_item = slot(ui, cell).map_or(0, |s| s.object_id);
        let (legality, source_is_bag, destination) =
            self.evaluate_drop(list, target_item, p.obj_id, view);
        // Over a pack's own icon (a side pack or the main pack) the answer
        // is that pack's free item slots alone: none refuses, even for a
        // stack already inside it, and even though letting go there still
        // finds the item a place in another pack. Something in the side
        // column that carries nothing, a focus for one, has none.
        let over_a_pack_icon = !source_is_bag
            && (Some(list) == self.container_list || Some(list) == self.top_container);
        if over_a_pack_icon {
            return if target_item != 0 && empty_item_slots(view.objects, target_item) != 0 {
                DragAcceptance::Accept
            } else {
                DragAcceptance::Reject
            };
        }
        // Anywhere else a full pack is no refusal while the drop has
        // somewhere to go, found by the same search the drop runs.
        let mut legality = legality;
        if is_capacity_rejection(legality)
            && choose_container(
                view.objects,
                p.obj_id,
                destination,
                destination,
                view.player,
                self.moving_amount(p.obj_id, view).1,
            )
            .is_ok()
        {
            legality = None;
        }
        if legality.is_none() {
            DragAcceptance::Accept
        } else {
            DragAcceptance::Reject
        }
    }

    /// Whether `item` may go where a drop on `target_item` of `list` puts
    /// it: (refusal, the item is a pack, the destination).
    fn evaluate_drop(
        &self,
        list: ElementId,
        target_item: u32,
        item: u32,
        view: &ItemsView<'_>,
    ) -> (Option<PlacementRejection>, bool, u32) {
        let source_is_bag = view.get(item).is_some_and(is_bag);
        if item == 0 {
            return (Some(PlacementRejection::InvalidItem), source_is_bag, 0);
        }
        let destination;
        if Some(list) == self.contents_grid {
            destination = self.open_container(view.player);
            if source_is_bag {
                return (
                    Some(PlacementRejection::ContainerCapacityFull),
                    source_is_bag,
                    destination,
                );
            }
        } else if Some(list) == self.container_list || Some(list) == self.top_container {
            destination = if source_is_bag {
                view.player
            } else {
                target_item
            };
            if !source_is_bag && (target_item == 0 || target_item == item) {
                return (
                    Some(PlacementRejection::InvalidDestination),
                    source_is_bag,
                    destination,
                );
            }
        } else {
            return (
                Some(PlacementRejection::InvalidDestination),
                source_is_bag,
                0,
            );
        }
        let whole_stack = self.moving_amount(item, view).1;
        (
            evaluate_container_placement(view.objects, item, destination, view.player, whole_stack)
                .err(),
            source_is_bag,
            destination,
        )
    }

    /// How much of `item` a drag moves, and whether that is the whole
    /// stack: the selected stack's split quantity splits it, wherever it
    /// comes from; the whole of it is a pickup when it comes from the
    /// ground. Part of a stack needs a slot of its own, even in the pack
    /// that holds the rest.
    fn moving_amount(&self, item: u32, view: &ItemsView<'_>) -> (u32, bool) {
        let full = view
            .get(item)
            .map_or(1, |d| d.stack_size.max(1).unsigned_abs());
        let split = view.split_size(item, full, self.selection.get());
        (split, split >= full)
    }

    fn notice(ui: &mut UiRoot, view: &ItemsView<'_>, r: PlacementRejection, item: u32, dest: u32) {
        let text = placement_rejection_text(r, view.get(item), view.get(dest), view.player);
        emit(ui, PanelOutput::Notice(text));
    }

    #[allow(clippy::too_many_lines)]
    fn drop_release(
        &mut self,
        ui: &mut UiRoot,
        list: ElementId,
        cell: ElementId,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) {
        if p.source_kind.is_alias() {
            return;
        }
        let item = p.obj_id;
        if item == 0 {
            return;
        }
        let (target_item, target_index) =
            slot(ui, cell).map_or((0, -1), |s| (s.object_id, s.slot_index));
        let (mut legality, _, mut destination) = self.evaluate_drop(list, target_item, item, view);
        if (legality.is_none() || is_capacity_rejection(legality))
            && self.try_join_stack_in_pack(ui, list, target_item, p, view)
        {
            return;
        }
        let mut fallthrough = 0;
        if is_capacity_rejection(legality) {
            // The search starts where the drop names: a pack's icon looks in
            // that pack and the packs inside it, the grid in the pack shown
            // open (the whole inventory when that is the main pack, nothing
            // more for a side pack, which holds no packs).
            let root = destination;
            let whole_stack = self.moving_amount(item, view).1;
            match choose_container(
                view.objects,
                item,
                root,
                destination,
                view.player,
                whole_stack,
            ) {
                Ok(c) => fallthrough = c,
                Err(no_room) => {
                    // Nothing has room: the notice names where it looked.
                    Self::notice(ui, view, no_room, item, root);
                    return;
                }
            }
            legality = evaluate_container_placement(
                view.objects,
                item,
                fallthrough,
                view.player,
                whole_stack,
            )
            .err();
            destination = fallthrough;
        }
        if let Some(r) = legality {
            Self::notice(ui, view, r, item, destination);
            return;
        }
        // An item already on its way into the open pack: the grid takes no
        // second one until the server answers.
        if Some(list) == self.contents_grid
            && let Some(pending) = view.pending_placement
            && pending.container_id == self.open_container(view.player)
        {
            let name = view
                .get(pending.item_id)
                .map(|o| o.name.as_str())
                .filter(|n| !n.trim().is_empty())
                .unwrap_or("that item");
            emit(ui, PanelOutput::Notice(placement_conflict_text(name)));
            return;
        }
        if !view.can_make_request {
            emit(
                ui,
                PanelOutput::Notice(ac_runtime::inventory::REQUEST_BUSY.to_owned()),
            );
            return;
        }
        if Some(list) == self.contents_grid
            && target_item != 0
            && self.try_merge(ui, p, target_item, target_index, fallthrough, view)
        {
            return;
        }
        let dragged = view.get(item);
        let source_is_bag = dragged.is_some_and(is_bag);
        let (split, whole_stack) = self.moving_amount(item, view);
        let mut container;
        let placement: Option<i32>;
        if Some(list) == self.contents_grid {
            container = self.open_container(view.player);
            // Lands left of the hovered item, wherever it came from: this
            // pack, another pack, the paperdoll or the ground.
            placement = list_drop_placement(
                view,
                container,
                item,
                false,
                target_item,
                target_index,
                whole_stack,
            );
        } else if Some(list) == self.container_list || Some(list) == self.top_container {
            if source_is_bag {
                container = view.player;
                if container == 0 {
                    return;
                }
                placement = if Some(list) != self.container_list {
                    Some(0)
                } else {
                    list_drop_placement(
                        view,
                        container,
                        item,
                        true,
                        target_item,
                        target_index,
                        whole_stack,
                    )
                };
            } else {
                if target_item == 0 || target_item == item {
                    return;
                }
                // A full pack was a capacity refusal, answered by the
                // search above; one that takes it here has room.
                container = target_item;
                placement = Some(0);
            }
        } else {
            return;
        }
        // The pack the search found takes the item at the slot this drop
        // named: the grid's drop slot, or the top for a pack's icon.
        if fallthrough != 0 {
            container = fallthrough;
        }
        let Some(placement) = placement else {
            return; // the item is already there
        };
        if container == item {
            return;
        }
        let container_ref = if container == view.player {
            ObjectRef::Player
        } else {
            ObjectRef::Guid(container)
        };
        let amount = if whole_stack { 0 } else { split };
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropInto {
                item: ObjectRef::Guid(item),
                container: container_ref,
                slot: placement,
                amount,
                pickup: p.source_kind == ItemDragSource::Ground,
                join_stack: false,
            })),
        );
    }

    /// A drop on a pack's icon (a side pack or the main pack) first joins
    /// the moving amount to the first stack in that pack, or in a pack
    /// inside it, with room for all of it, and selects that stack. Joining
    /// takes no slot, so this comes before any room check. A drop on a cell
    /// of the contents grid never searches like this.
    fn try_join_stack_in_pack(
        &mut self,
        ui: &mut UiRoot,
        list: ElementId,
        pack: u32,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) -> bool {
        if Some(list) != self.container_list && Some(list) != self.top_container {
            return false;
        }
        let item = p.obj_id;
        if pack == 0 || pack == item || view.get(pack).is_none_or(|c| c.items_capacity <= 0) {
            return false;
        }
        let Some(dragged) = view.get(item).filter(|d| !is_bag(d)) else {
            return false;
        };
        let full = dragged.stack_size.max(1).unsigned_abs();
        let split = view.split_size(item, full, self.selection.get());
        let Some(join) = plan_join_stack(view.objects, item, pack, split, view.can_make_request)
        else {
            return false;
        };
        let container = if pack == view.player {
            ObjectRef::Player
        } else {
            ObjectRef::Guid(pack)
        };
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropInto {
                item: ObjectRef::Guid(item),
                container,
                slot: 0,
                amount: if split >= full { 0 } else { split },
                pickup: p.source_kind == ItemDragSource::Ground,
                join_stack: true,
            })),
        );
        self.select(ui, join.target_object_id);
        true
    }

    /// A drop on a stack in the grid: a legal merge takes the drop. The
    /// session merges, or (the merge no longer legal by its own copy of the
    /// stacks) makes the plain drop this one would have been: into the pack
    /// shown open, left of the stack dropped on, or the pack with room.
    fn try_merge(
        &mut self,
        ui: &mut UiRoot,
        p: &ItemDragPayload,
        target: u32,
        target_index: i32,
        fallthrough: u32,
        view: &ItemsView<'_>,
    ) -> bool {
        let source = p.obj_id;
        let (Some(s), Some(t)) = (view.get(source), view.get(target)) else {
            return false;
        };
        let requested = match view.split {
            Some(q) if self.selection.get() == Some(source) => {
                i32::try_from(q.value()).unwrap_or(i32::MAX)
            }
            _ => s.stack_size.max(1),
        };
        let item = |o: &ClientObject| StackMergeItem {
            object_id: o.object_id(),
            weenie_class_id: o.weenie_class_id,
            stack_size: o.stack_size,
            max_stack_size: o.stack_size_max,
            trade_state: o.trade_state,
        };
        let Some(plan) = StackMergePlan::plan(&item(s), &item(t), view.can_make_request, requested)
        else {
            return false;
        };
        let (split, whole_stack) = self.moving_amount(source, view);
        // As the plain grid drop: left of the stack dropped on, in the pack
        // shown open or, when it has no room, the pack the search found.
        let open = self.open_container(view.player);
        let slot =
            list_drop_placement(view, open, source, false, target, target_index, whole_stack);
        let container = if fallthrough != 0 { fallthrough } else { open };
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropOnStack {
                item: ObjectRef::Guid(plan.source_object_id),
                target: ObjectRef::Guid(plan.target_object_id),
                merge_amount: plan.amount,
                container: if container == view.player {
                    ObjectRef::Player
                } else {
                    ObjectRef::Guid(container)
                },
                slot: slot.filter(|_| container != source),
                amount: if whole_stack { 0 } else { split },
                pickup: p.source_kind == ItemDragSource::Ground,
            })),
        );
        self.select(ui, plan.target_object_id);
        true
    }

    // ── Showing ─────────────────────────────────────────────────────────

    fn refresh_captions(&self, ui: &mut UiRoot, open_name: Option<&str>, owner: &str) {
        set_caption(ui, self.title, &format!("Inventory of {owner}"));
        set_caption(
            ui,
            self.contents_caption,
            &format!("Contents of {}", open_name.unwrap_or("Backpack")),
        );
        set_caption(ui, self.burden_text, &format!("{}%", self.burden_percent));
    }

    /// Rebuild the cells from `view` (refreshing in place when the same
    /// objects sit in the same cells).
    pub fn populate(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        let p = view.player;
        self.player = p;
        let open = self.open_container(p);
        let mut bags = Vec::new();
        for &guid in view.objects.get_contents(p) {
            if let Some(item) = view.get(guid)
                && item.currently_equipped_location == EquipMask::NONE
                && is_bag(item)
            {
                bags.push(guid);
            }
        }
        let mut contents = Vec::new();
        for &guid in view.objects.get_contents(open) {
            if let Some(item) = view.get(guid)
                && item.currently_equipped_location == EquipMask::NONE
                && !is_bag(item)
            {
                contents.push(guid);
            }
        }
        // An item on its way into a pack shown here already sits at the
        // slot its request names (a pack in the pack column, anything else
        // in the open pack's grid), waiting.
        let pending = view
            .pending_placement
            .filter(|pr| view.get(pr.item_id).is_some());
        let mut projected = [0u32; 2];
        if let Some(pr) = pending {
            let bag = view.get(pr.item_id).is_some_and(is_bag);
            let list = if bag && pr.container_id == p {
                Some((&mut bags, 0))
            } else if !bag && pr.container_id == open {
                Some((&mut contents, 1))
            } else {
                None
            };
            if let Some((guids, which)) = list {
                guids.retain(|g| *g != pr.item_id);
                let at = usize::try_from(pr.placement.max(0))
                    .unwrap_or(0)
                    .min(guids.len());
                guids.insert(at, pr.item_id);
                projected[which] = pr.item_id;
            }
        }
        let bag_target = self.container_slot_target(view, bags.len());
        self.resize_pack_column(ui, bag_target);
        let contents_target = self.contents_slot_target(view, open);
        // Flushing a list's cells cancels a press on one of them, so each
        // list is rebuilt only when what it shows changed: opening a pack
        // changes the contents grid alone, and the pack cell pressed to open
        // it stays to become a drag.
        let rebuild = [
            (self.container_list, &bags, bag_target, true),
            (self.contents_grid, &contents, contents_target, false),
        ]
        .into_iter()
        .filter(|(list, guids, target, _)| !matches_cells(ui, *list, guids, *target))
        .collect::<Vec<_>>();
        if !rebuild.is_empty() {
            for (list, ..) in &rebuild {
                if let Some(list) = *list {
                    ui.with::<ItemList, _>(list, |l, ui| {
                        l.defer_layout();
                        l.flush(ui);
                    });
                }
            }
            for (list, guids, target, pack_column) in rebuild {
                let Some(list) = list else {
                    continue;
                };
                for i in 0..target.max(guids.len()) {
                    let s = ItemSlot {
                        slot_index: i as i32,
                        drag_accept_sprite: if pack_column {
                            PACK_DRAG_ACCEPT_SPRITE
                        } else {
                            DEFAULT_DRAG_ACCEPT_SPRITE
                        },
                        drag_reject_sprite: DEFAULT_DRAG_REJECT_SPRITE,
                        ..ItemSlot::default()
                    };
                    let cell = ui.insert(slot_element(), s);
                    ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
                }
                ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
            }
        }
        for (list, guids, pack_column, projected_item) in [
            (self.container_list, &bags, true, projected[0]),
            (self.contents_grid, &contents, false, projected[1]),
        ] {
            for (i, cell) in cells(ui, list).into_iter().enumerate() {
                let guid = guids.get(i).copied().unwrap_or(0);
                let digits = self.digits.clone();
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    fill_slot(s, view.get(guid), view, icons);
                    stamp_shortcut_number(s, view, digits.as_ref());
                    s.set_waiting(view.is_pending(guid) || (guid != 0 && guid == projected_item));
                    s.capacity_fill = if pack_column {
                        capacity_fill(view, guid)
                    } else {
                        -1.0
                    };
                }
            }
        }
        if let Some(top) = self.top_container {
            if cells(ui, Some(top)).is_empty() {
                let cell = ui.insert(slot_element(), ItemSlot::default());
                ui.with::<ItemList, _>(top, |l, ui| l.add_item(ui, top, cell));
            }
            let icon = icons.item_icon(ItemType::CONTAINER, PLAYER_PACK_BASE_ICON, 0, 0, 0);
            let drag = icons.drag_icon(ItemType::CONTAINER, PLAYER_PACK_BASE_ICON, 0, 0, 0);
            let tooltip = view.tooltip(p);
            if let Some(main) = cells(ui, Some(top)).first().copied()
                && let Some(s) = ui.widget_mut::<ItemSlot>(main)
            {
                s.set_item(p, icon, drag, None);
                s.tooltip = tooltip;
                s.slot_index = 0;
                s.drag_accept_sprite = PACK_DRAG_ACCEPT_SPRITE;
                s.drag_reject_sprite = DEFAULT_DRAG_REJECT_SPRITE;
                s.set_waiting(view.is_pending(p));
                s.capacity_fill = capacity_fill(view, p);
            }
        }
        self.apply_indicators(ui);
        self.refresh_burden(ui, view);
        let open_name = (open != p).then(|| view.get(open).map(|o| o.name.clone()));
        let open_name = open_name.flatten();
        let owner = view
            .owner_name
            .filter(|n| !n.trim().is_empty())
            .unwrap_or("Player");
        self.refresh_captions(ui, open_name.as_deref(), owner);
    }

    fn container_slot_target(&self, view: &ItemsView<'_>, bag_count: usize) -> usize {
        if self.container_list.is_none() {
            return 0;
        }
        let capacity = view.get(view.player).map_or(0, |o| o.containers_capacity);
        let slots = if capacity > 0 {
            capacity as usize
        } else {
            SIDE_BAG_SLOTS
        };
        // An augmented capacity above seven is honoured.
        slots.max(bag_count)
    }

    /// Make the pack column tall enough for every slot, so a pack below
    /// the seventh shows without scrolling: the column, the backpack
    /// window, the inventory's minimum height and the window frame it is
    /// mounted in grow by what the slots need beyond the authored column.
    fn resize_pack_column(&self, ui: &mut UiRoot, slot_count: usize) {
        let Some(list) = self.container_list else {
            return;
        };
        let g = self.pack_column;
        let cell_height = ui
            .widget::<ItemList>(list)
            .map_or(BACKPACK_CELL_PX, |l| l.cell_height);
        let extension = (slot_count as f32 * cell_height - g.column_height).max(0.0);
        let height = g.column_height + extension;
        if ui.element(list).is_none_or(|e| e.height == height) {
            return;
        }
        if let Some(e) = ui.element_mut(list) {
            e.height = height;
        }
        ui.set_anchors(list, AnchorEdges::TOP_LEFT);
        if let Some(backpack) = g.backpack_window {
            if let Some(e) = ui.element_mut(backpack) {
                e.height = g.backpack_height + extension;
            }
            ui.reset_anchor_capture(backpack);
        }
        let inventory_min = g.inventory_min_height.max(g.inventory_height + extension);
        if let Some(e) = ui.element_mut(g.inventory_window) {
            e.min_height = inventory_min;
        }
        match g.frame {
            // Resizing the contents directly would re-measure their anchors
            // against the frame's height of the moment, and they would stop
            // following it: the frame grows and the contents stretch with it.
            Some((frame, chrome, frame_min)) => {
                let size = ui.element_mut(frame).map(|e| {
                    e.min_height = frame_min.max(inventory_min + chrome);
                    e.max_height = e.max_height.max(e.min_height);
                    e.height = e.height.max(e.min_height);
                    (e.width, e.height)
                });
                ui.reset_anchor_capture(frame);
                if let Some((w, h)) = size {
                    ui.apply_anchor(g.inventory_window, w, h);
                }
            }
            None => {
                if let Some(e) = ui.element_mut(g.inventory_window) {
                    e.height = e.height.max(e.min_height);
                }
                ui.reset_anchor_capture(g.inventory_window);
            }
        }
        ui.reset_anchor_capture(list);
        ui.with::<ItemList, _>(list, |l, ui| l.layout_cells(ui, list));
    }

    fn contents_slot_target(&self, view: &ItemsView<'_>, open: u32) -> usize {
        if self.contents_grid.is_none() {
            return 0;
        }
        let cap = view.get(open).map_or(0, |o| o.items_capacity);
        if cap > 0 {
            cap as usize
        } else if open == view.player {
            MAIN_PACK_SLOTS
        } else {
            SIDE_PACK_SLOTS
        }
    }

    /// The selection square and the open-pack triangle, from the panel's
    /// own selection and the last view's target mode and requests.
    fn apply_indicators(&self, ui: &mut UiRoot) {
        let open = self.open_container(self.player);
        let selected = self.selection.get();
        for list in [self.contents_grid, self.container_list, self.top_container] {
            for cell in cells(ui, list) {
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    let id = s.object_id;
                    let pending = id != 0
                        && (self.target_source == Some(id) || self.pending_cache.contains(&id));
                    s.selected = id != 0 && Some(id) == selected && !pending;
                    s.is_open_container = id != 0 && id == open;
                }
            }
        }
    }

    fn refresh_burden(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>) {
        let p = view.player;
        let player = view.get(p);
        let strength = view.strength.unwrap_or(10);
        let aug = player.map_or(0, |o| o.properties.get_int(ENCUMBRANCE_AUG_PROPERTY, 0));
        let capacity = burden::encumbrance_capacity(strength, aug);
        let wire = player.and_then(|o| o.properties.ints.get(&ENCUMBRANCE_VAL_PROPERTY).copied());
        let carried = wire.unwrap_or_else(|| view.objects.sum_carried_burden(p));
        let load = burden::load_ratio(capacity, carried);
        self.burden_fill = burden::load_to_fill(load);
        self.burden_percent = burden::load_to_percent(load);
        if let Some(m) = self.burden_meter
            && let Some(w) = ui.widget_mut::<Meter>(m)
        {
            w.fill = Some(self.burden_fill);
        }
    }
}

fn matches_cells(ui: &UiRoot, list: Option<ElementId>, guids: &[u32], target: usize) -> bool {
    let Some(list) = list else {
        return guids.is_empty();
    };
    let cells = cells(ui, Some(list));
    if cells.len() != target.max(guids.len()) {
        return false;
    }
    cells
        .iter()
        .enumerate()
        .all(|(i, c)| slot(ui, *c).map(|s| s.object_id) == Some(guids.get(i).copied().unwrap_or(0)))
}

pub(crate) fn count_loose_contents(view: &ItemsView<'_>, container: u32) -> usize {
    view.objects
        .get_contents(container)
        .iter()
        .filter_map(|&g| view.get(g))
        .filter(|o| o.currently_equipped_location == EquipMask::NONE && !is_bag(o))
        .count()
}

/// The placement a drop on a cell of one of the pack's own lists asks for:
/// the loose-item grid (`bags` false) or the pack column (true). `None`
/// when the drop leaves the item where it is.
fn list_drop_placement(
    view: &ItemsView<'_>,
    container: u32,
    item: u32,
    bags: bool,
    target_item: u32,
    target_index: i32,
    whole_stack: bool,
) -> Option<i32> {
    let listed: Vec<u32> = view
        .objects
        .get_contents(container)
        .iter()
        .copied()
        .filter(|&g| {
            view.get(g).is_some_and(|o| {
                o.currently_equipped_location == EquipMask::NONE && is_bag(o) == bags
            })
        })
        .collect();
    let source = listed.iter().position(|&g| g == item);
    // An empty cell is past the last item.
    let drop_index = if target_item != 0 {
        target_index
    } else {
        i32::MAX
    };
    item_list_drop_placement(drop_index, listed.len(), source, whole_stack)
}

/// An item cell's container fullness meter (every item cell, the toolbar's
/// too): shown for a container with a positive item capacity holding at
/// least one loose item (an empty one hides it, rail included).
pub(crate) fn capacity_fill(view: &ItemsView<'_>, container: u32) -> f32 {
    let cap = view.get(container).map_or(0, |o| o.items_capacity);
    if cap <= 0 {
        return -1.0;
    }
    let n = count_loose_contents(view, container);
    if n == 0 {
        return -1.0;
    }
    (n as f32 / cap as f32).clamp(0.0, 1.0)
}

impl PanelController for InventoryController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close_button {
            if matches!(message, ElementMessage::Clicked { .. }) {
                emit(
                    ui,
                    PanelOutput::ClosePanel(crate::panels::panel_id::INVENTORY),
                );
            }
            return true;
        }
        let Some(list) = self.list_of(ui, element) else {
            return false;
        };
        match message {
            ElementMessage::Pressed => self.press(ui, list, element),
            ElementMessage::Clicked { .. } => {
                // A pack cell opens its pack (already open from the press:
                // nothing more is sent).
                if Some(list) != self.contents_grid
                    && let Some(id) = slot(ui, element).map(|s| s.object_id)
                {
                    self.open(ui, id);
                }
            }
            ElementMessage::DoubleClicked => {
                if Some(list) == self.contents_grid
                    && let Some(id) = slot(ui, element).map(|s| s.object_id).filter(|i| *i != 0)
                {
                    emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(
                            ObjectRef::Guid(id),
                        ))),
                    );
                }
            }
            ElementMessage::RightClicked => {
                if let Some(id) = slot(ui, element).map(|s| s.object_id).filter(|i| *i != 0) {
                    self.select(ui, id);
                    emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                            ObjectRef::Guid(id),
                        ))),
                    );
                }
            }
            ElementMessage::ItemDragLifted(_)
            | ElementMessage::ItemDragEntered(_)
            | ElementMessage::ItemDropped(_) => {
                self.queue.push((element, message.clone()));
                return true;
            }
            _ => return false,
        }
        // The square moves at once; the contents follow at the next update.
        self.apply_indicators(ui);
        true
    }
}
