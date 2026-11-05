//! The outside-container window (layout 0x21000008): a chest, a corpse or
//! a pack on the ground the player is looking into. One cell for the
//! container itself, a row of the packs inside it, and a row of the open
//! pack's loose items; items are taken by double click or by dragging them
//! to the inventory, and put in by dropping them on the contents row.
//!
//! The session owns which container is open ([`ExternalContainerFacts`]);
//! the window shows while one is, opens the packs inside it locally, and
//! closes it (a use of the container) from its close button, a double
//! click on its cell, or when the player walks out of range. Presses act
//! at once; drags and drops are resolved at the next update.

use ac_core::items::placement::{
    evaluate_container_placement, item_list_drop_placement, loose_contents,
    placement_rejection_text,
};
use ac_core::items::{ClientObject, PlacementRejection, PublicWeenieFlags};
use ac_runtime::inventory::{ItemCommand, ObjectRef, PendingPlacement, REQUEST_BUSY};
use ac_runtime::session::SessionCommand;

use crate::event::{DragAcceptance, ItemDragPayload, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::inventory::{SelectionEcho, capacity_fill, is_bag};
use crate::panels::items::{ItemsView, bind_click, emit, fill_slot, offer_target};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::item_slot::{
    DEFAULT_DRAG_ACCEPT_SPRITE, DEFAULT_DRAG_REJECT_SPRITE, slot_element,
};
use crate::widgets::{ItemList, ItemSlot, ScrollSource, Scrollbar};

/// The window's layout.
pub const LAYOUT_ID: u32 = 0x2100_0008;
/// Its root.
pub const ROOT_ID: u32 = 0x1000_0063;
/// The container's own cell.
pub const TOP_CONTAINER_ID: u32 = 0x1000_0064;
/// The packs inside it.
pub const CONTAINER_LIST_ID: u32 = 0x1000_0067;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_0068;
/// The open pack's loose items.
pub const CONTENTS_LIST_ID: u32 = 0x1000_006A;
/// The contents row's scrollbar.
pub const CONTENTS_SCROLLBAR_ID: u32 = 0x1000_006B;

/// Contents cell size, pixels.
pub const ITEM_CELL_PX: f32 = 32.0;
/// Pack cell size, pixels.
pub const CONTAINER_CELL_PX: f32 = 36.0;
/// The content's width at most when mounted, pixels.
pub const DEFAULT_CONTENT_WIDTH: f32 = 700.0;
/// The content's smallest width, pixels.
pub const MINIMUM_CONTENT_WIDTH: f32 = 160.0;

/// The horizontal scrollbar's art (track, left and right buttons, thumb
/// top, middle and bottom: normal, rollover, pressed).
pub mod horizontal_bar {
    /// Track.
    pub const TRACK: u32 = 0x0600_4C7F;
    /// Left button.
    pub const LEFT: [u32; 3] = [0x0600_4C8C, 0x0600_4C8D, 0x0600_4C8E];
    /// Right button.
    pub const RIGHT: [u32; 3] = [0x0600_4C89, 0x0600_4C8A, 0x0600_4C8B];
    /// Thumb's first end.
    pub const THUMB_TOP: [u32; 3] = [0x0600_4C80, 0x0600_4C81, 0x0600_4C82];
    /// Thumb's middle.
    pub const THUMB_MID: [u32; 3] = [0x0600_4C83, 0x0600_4C84, 0x0600_4C85];
    /// Thumb's other end.
    pub const THUMB_BOT: [u32; 3] = [0x0600_4C86, 0x0600_4C87, 0x0600_4C88];
}

/// Give `bar` the horizontal scrollbar art.
pub fn apply_horizontal_bar_art(bar: &mut Scrollbar) {
    use horizontal_bar as h;
    bar.horizontal = true;
    bar.track_sprite = h::TRACK;
    [bar.up_sprite, bar.up_rollover_sprite, bar.up_pressed_sprite] = h::LEFT;
    [
        bar.down_sprite,
        bar.down_rollover_sprite,
        bar.down_pressed_sprite,
    ] = h::RIGHT;
    [
        bar.thumb_top_sprite,
        bar.thumb_top_rollover_sprite,
        bar.thumb_top_pressed_sprite,
    ] = h::THUMB_TOP;
    [
        bar.thumb_sprite,
        bar.thumb_rollover_sprite,
        bar.thumb_pressed_sprite,
    ] = h::THUMB_MID;
    [
        bar.thumb_bot_sprite,
        bar.thumb_bot_rollover_sprite,
        bar.thumb_bot_pressed_sprite,
    ] = h::THUMB_BOT;
}

/// The empty-cell images of the lists (from their cell prototypes).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ExternalContainerSprites {
    /// The contents row.
    pub contents_empty: u32,
    /// The container cell and the packs row.
    pub container_empty: u32,
}

/// What the session says about the outside container.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ExternalContainerFacts {
    /// The open container (0: none).
    pub container: u32,
    /// The player is within its use range.
    pub in_range: bool,
}

/// The outside-container window's controller.
#[derive(Debug)]
pub struct ExternalContainerController {
    window: String,
    top: ElementId,
    containers: ElementId,
    contents: ElementId,
    close: ElementId,
    root: u32,
    open_container: u32,
    close_requested: bool,
    selection: SelectionEcho,
    target_mode: bool,
    target_source: Option<u32>,
    pending: Vec<u32>,
    /// The item on its way into the pack this window showed open when its
    /// placement was made (shown there, waiting).
    projection: Option<PendingPlacement>,
    /// The placement last seen (its token; 0 for none).
    seen_placement: u64,
    /// The containers whose items the strips show now, so an update keeps
    /// the shown items of a full strip instead of letting an arrival push
    /// one out.
    packs_shown_for: u32,
    contents_shown_for: u32,
    queue: Vec<(ElementId, ElementMessage)>,
    /// The lists were built for the container open now (false after they
    /// were emptied: the next fill builds all three).
    built: bool,
}

fn is_container(item: Option<&ClientObject>) -> bool {
    item.is_some_and(is_bag)
}

fn configure_list(ui: &mut UiRoot, list: ElementId, cell: f32, horizontal: bool, empty: u32) {
    ui.with::<ItemList, _>(list, |l, ui| {
        l.flush(ui);
        l.columns = 1;
        l.cell_width = cell;
        l.cell_height = cell;
        l.single_row = true;
        l.horizontal_scroll = horizontal;
        l.fill_visible_empty_slots = true;
        if empty != 0 {
            l.set_cell_empty_sprite(ui, empty);
        }
        l.empty_slot_factory = Some(Box::new(|ui: &mut UiRoot| {
            ui.insert(
                slot_element(),
                ItemSlot {
                    source_kind: ItemDragSource::Ground,
                    drag_accept_sprite: DEFAULT_DRAG_ACCEPT_SPRITE,
                    drag_reject_sprite: DEFAULT_DRAG_REJECT_SPRITE,
                    ..ItemSlot::default()
                },
            )
        }));
    });
}

/// A container's strip has one cell per unit of its capacity: the pack
/// strip from its pack capacity, the item strip from its item capacity, so
/// a house hook (item capacity 1) shows one cell. A container that is not
/// openable (and is not the player) has capacity 0 and shows no cells. A
/// negative capacity means no limit: the strip fills its width (`None`).
fn slot_count(view: &ItemsView<'_>, container: u32, packs: bool) -> Option<usize> {
    let c = view.get(container)?;
    if !c.flags().contains(PublicWeenieFlags::OPENABLE) && container != view.player {
        return Some(0);
    }
    let capacity = if packs {
        c.containers_capacity
    } else {
        c.items_capacity
    };
    usize::try_from(capacity).ok()
}

fn cells(ui: &UiRoot, list: ElementId) -> Vec<ElementId> {
    ui.widget::<ItemList>(list)
        .map(|l| l.cells().to_vec())
        .unwrap_or_default()
}

fn slot_object(ui: &UiRoot, cell: ElementId) -> u32 {
    ui.widget::<ItemSlot>(cell).map_or(0, |s| s.object_id)
}

/// The guids a strip with `slots` cells shows, in their order. When there
/// are more than its cells, an item it shows already (`current`, the strip
/// as it stands for the same container) keeps its cell, so an item arriving
/// in a full strip is not shown; the free cells go to the first of the
/// others.
fn shown(ui: &UiRoot, guids: &[u32], slots: Option<usize>, current: Option<ElementId>) -> Vec<u32> {
    let Some(count) = slots.filter(|&c| guids.len() > c) else {
        return guids.to_vec();
    };
    let mut keep: Vec<u32> = Vec::with_capacity(count);
    if let Some(list) = current {
        for cell in cells(ui, list) {
            if keep.len() >= count {
                break;
            }
            let id = slot_object(ui, cell);
            if id != 0 && guids.contains(&id) && !keep.contains(&id) {
                keep.push(id);
            }
        }
    }
    for &guid in guids {
        if keep.len() >= count {
            break;
        }
        if !keep.contains(&guid) {
            keep.push(guid);
        }
    }
    guids.iter().copied().filter(|g| keep.contains(g)).collect()
}

/// The list's cells show `guids` in order, empty cells after them (a list
/// pads itself past its items, to its width or to its fixed cell count).
fn matches(ui: &UiRoot, list: ElementId, guids: &[u32]) -> bool {
    let cells = cells(ui, list);
    if cells.len() < guids.len() {
        return false;
    }
    cells
        .iter()
        .enumerate()
        .all(|(i, c)| slot_object(ui, *c) == guids.get(i).copied().unwrap_or(0))
}

impl ExternalContainerController {
    /// Bind to the imported window registered as `window` (`None` without
    /// its three lists or its close button). The window starts hidden.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        window: &str,
        sprites: ExternalContainerSprites,
    ) -> Option<Self> {
        let list = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
        };
        let top = list(ui, TOP_CONTAINER_ID)?;
        let containers = list(ui, CONTAINER_LIST_ID)?;
        let contents = list(ui, CONTENTS_LIST_ID)?;
        let close = layout
            .find(CLOSE_BUTTON_ID)
            .filter(|b| bind_click(ui, *b))?;
        configure_list(ui, top, CONTAINER_CELL_PX, false, sprites.container_empty);
        configure_list(
            ui,
            containers,
            CONTAINER_CELL_PX,
            true,
            sprites.container_empty,
        );
        configure_list(ui, contents, ITEM_CELL_PX, true, sprites.contents_empty);
        if let Some(bar) = layout.find(CONTENTS_SCROLLBAR_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(contents);
            apply_horizontal_bar_art(s);
        }
        ui.hide_window(window);
        Some(Self {
            window: window.to_owned(),
            top,
            containers,
            contents,
            close,
            root: 0,
            open_container: 0,
            close_requested: false,
            selection: SelectionEcho::default(),
            target_mode: false,
            target_source: None,
            pending: Vec::new(),
            projection: None,
            seen_placement: 0,
            packs_shown_for: 0,
            contents_shown_for: 0,
            queue: Vec::new(),
            built: false,
        })
    }

    /// The container's own cell's list.
    #[must_use]
    pub fn top_list(&self) -> ElementId {
        self.top
    }

    /// The packs row.
    #[must_use]
    pub fn container_list(&self) -> ElementId {
        self.containers
    }

    /// The contents row.
    #[must_use]
    pub fn contents_list(&self) -> ElementId {
        self.contents
    }

    /// The pack whose contents show (0: none open).
    #[must_use]
    pub fn open_container(&self) -> u32 {
        self.open_container
    }

    /// Follow the session's outside container, act on what was queued
    /// against `view`, close the window when out of range, and show it.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &ItemsView<'_>,
        facts: ExternalContainerFacts,
        icons: &mut dyn IconSource,
    ) {
        self.target_mode = view.target_mode;
        self.target_source = view.target_source;
        self.pending = view.pending_sources.to_vec();
        self.selection.sync(view.selected);
        if facts.container != self.root {
            self.root = facts.container;
            self.close_requested = false;
            if facts.container == 0 {
                self.open_container = 0;
                ui.hide_window(&self.window);
                self.clear_lists(ui);
            } else {
                self.open_container = facts.container;
                self.built = false;
                self.packs_shown_for = 0;
                self.contents_shown_for = 0;
                self.scroll_to_home(ui);
                ui.show_window(&self.window);
            }
        }
        let queued = std::mem::take(&mut self.queue);
        for (element, message) in queued {
            self.handle(ui, element, &message, view);
        }
        self.follow_placement(view.pending_placement);
        if self.root != 0
            && ui.is_window_visible(&self.window)
            && !self.close_requested
            && !facts.in_range
        {
            self.request_close(ui);
        }
        if self.root != 0 && !self.close_requested {
            self.populate(ui, view, icons);
        }
    }

    /// A placement is shown here when it names the pack this window shows
    /// open at the moment it is first seen; it is kept until the session
    /// says it ended.
    fn follow_placement(&mut self, placement: Option<PendingPlacement>) {
        match placement {
            None => {
                self.projection = None;
                self.seen_placement = 0;
            }
            Some(p) if p.token != self.seen_placement => {
                self.seen_placement = p.token;
                self.projection = (self.open_container != 0
                    && p.container_id == self.open_container)
                    .then_some(p);
            }
            Some(_) => {}
        }
    }

    /// Close the container: the window hides, its lists empty, and the
    /// container is used (the server closes the view). Once per opening.
    pub fn request_close(&mut self, ui: &mut UiRoot) {
        if self.root == 0 || self.close_requested {
            return;
        }
        self.close_requested = true;
        ui.hide_window(&self.window);
        self.clear_lists(ui);
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::CloseContainer(
                ObjectRef::Guid(self.root),
            ))),
        );
    }

    fn clear_lists(&mut self, ui: &mut UiRoot) {
        self.packs_shown_for = 0;
        self.contents_shown_for = 0;
        for list in [self.top, self.containers, self.contents] {
            ui.with::<ItemList, _>(list, |l, ui| l.flush(ui));
        }
        self.built = false;
        self.scroll_to_home(ui);
    }

    fn scroll_to_home(&self, ui: &mut UiRoot) {
        for list in [self.top, self.containers, self.contents] {
            if let Some(l) = ui.widget_mut::<ItemList>(list) {
                l.scroll.set_scroll_y(0);
            }
        }
    }

    fn select(&mut self, ui: &mut UiRoot, id: u32) {
        if id == 0 {
            return;
        }
        self.selection.select(id);
        emit(ui, PanelOutput::SelectObject(id));
    }

    fn list_of(&self, ui: &UiRoot, cell: ElementId) -> Option<ElementId> {
        let parent = ui.parent(cell)?;
        [self.top, self.containers, self.contents]
            .into_iter()
            .find(|l| *l == parent)
    }

    /// A press on an item: a waiting target mode takes it; a pack inside
    /// the container (its row holds only packs) opens; anything else is
    /// selected.
    fn press(&mut self, ui: &mut UiRoot, list: ElementId, cell: ElementId) {
        let id = slot_object(ui, cell);
        if id == 0 {
            return;
        }
        if self.target_mode {
            if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                s.consume_primary_press();
            }
            offer_target(ui, ObjectRef::Guid(id));
            return;
        }
        if list == self.containers && id != self.root {
            self.select(ui, id);
            self.open_container = id;
            if let Some(l) = ui.widget_mut::<ItemList>(self.contents) {
                l.scroll.set_scroll_y(0);
            }
        } else {
            self.select(ui, id);
        }
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
            ElementMessage::ItemDragLifted(p) if p.obj_id != 0 => self.select(ui, p.obj_id),
            ElementMessage::ItemDragEntered(p) => {
                let a = self.drag_over(list, p, view);
                if let Some(s) = ui.widget_mut::<ItemSlot>(element) {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) if list == self.contents => {
                self.drop_release(ui, element, p, view);
            }
            _ => {}
        }
    }

    /// Whether `item` may go into the open pack: a pack never does, and
    /// part of a stack needs a slot of its own, even beside the rest of it.
    fn evaluate_drop(&self, item: u32, view: &ItemsView<'_>) -> Option<PlacementRejection> {
        if is_container(view.get(item)) {
            return Some(PlacementRejection::ContainerCapacityFull);
        }
        let whole_stack = self.moving_amount(item, view).1;
        evaluate_container_placement(view.objects, item, self.open_container, 0, whole_stack).err()
    }

    /// How much of `item` a drag moves, and whether that is the whole
    /// stack: the selected stack's split quantity splits it.
    fn moving_amount(&self, item: u32, view: &ItemsView<'_>) -> (u32, bool) {
        let full = view
            .get(item)
            .map_or(1, |d| d.stack_size.max(1).unsigned_abs());
        let split = view.split_size(item, full, self.selection.get());
        (split, split >= full)
    }

    fn drag_over(
        &self,
        list: ElementId,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) -> DragAcceptance {
        if p.source_kind.is_alias() {
            return DragAcceptance::None;
        }
        if list != self.contents || self.open_container == 0 {
            return DragAcceptance::Reject;
        }
        if self.evaluate_drop(p.obj_id, view).is_none() {
            DragAcceptance::Accept
        } else {
            DragAcceptance::Reject
        }
    }

    fn drop_release(
        &mut self,
        ui: &mut UiRoot,
        cell: ElementId,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) {
        if p.source_kind.is_alias() || self.open_container == 0 {
            return;
        }
        if let Some(r) = self.evaluate_drop(p.obj_id, view) {
            let text =
                placement_rejection_text(r, view.get(p.obj_id), view.get(self.open_container), 0);
            emit(ui, PanelOutput::Notice(text));
            return;
        }
        if !view.can_make_request {
            emit(ui, PanelOutput::Notice(REQUEST_BUSY.to_owned()));
            return;
        }
        if view.get(p.obj_id).is_none() {
            return;
        }
        let (amount, whole_stack) = self.moving_amount(p.obj_id, view);
        // Lands left of the hovered item, whether it is reordered inside the
        // container or arrives from the inventory.
        let (target_item, target_index) = ui
            .widget::<ItemSlot>(cell)
            .map_or((0, -1), |s| (s.object_id, s.slot_index));
        let Some(placement) =
            self.contents_drop_placement(view, p.obj_id, target_item, target_index, whole_stack)
        else {
            return; // the item is already there
        };
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropInto {
                item: ObjectRef::Guid(p.obj_id),
                container: ObjectRef::Guid(self.open_container),
                slot: placement,
                amount: if whole_stack { 0 } else { amount },
                pickup: false,
                join_stack: false,
            })),
        );
    }

    /// The placement a drop on a cell of the open container's item list
    /// asks for, or `None` when the drop leaves the item where it is.
    fn contents_drop_placement(
        &self,
        view: &ItemsView<'_>,
        item: u32,
        target_item: u32,
        target_index: i32,
        whole_stack: bool,
    ) -> Option<i32> {
        let listed = loose_contents(view.objects, self.open_container);
        let source = listed.iter().position(|&g| g == item);
        // An empty cell is past the last item.
        let drop_index = if target_item != 0 {
            target_index
        } else {
            i32::MAX
        };
        item_list_drop_placement(drop_index, listed.len(), source, whole_stack)
    }

    /// Show the container, the packs in it and the open pack's loose items
    /// (refreshing the cells in place when the same objects sit in them).
    pub fn populate(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        let root = self.root;
        if root == 0 {
            self.clear_lists(ui);
            return;
        }
        if self.open_container == 0 {
            self.open_container = root;
        }
        let packs: Vec<u32> = view
            .objects
            .get_contents(root)
            .iter()
            .copied()
            .filter(|&g| is_container(view.get(g)))
            .collect();
        let mut loose: Vec<u32> = view
            .objects
            .get_contents(self.open_container)
            .iter()
            .copied()
            .filter(|&g| !is_container(view.get(g)))
            .collect();
        // An item on its way in already sits at the slot its request names.
        let projected = self
            .projection
            .filter(|pr| {
                pr.container_id == self.open_container
                    && view.get(pr.item_id).is_some()
                    && !is_container(view.get(pr.item_id))
            })
            .map_or(0, |pr| {
                loose.retain(|g| *g != pr.item_id);
                let at = usize::try_from(pr.placement.max(0))
                    .unwrap_or(0)
                    .min(loose.len());
                loose.insert(at, pr.item_id);
                pr.item_id
            });
        let pack_cells = slot_count(view, root, true);
        let item_cells = slot_count(view, self.open_container, false);
        // A full strip keeps the items it shows already; each strip is
        // compared below with what it shows, not with every item inside.
        let packs = shown(
            ui,
            &packs,
            pack_cells,
            (self.packs_shown_for == root).then_some(self.containers),
        );
        let loose = shown(
            ui,
            &loose,
            item_cells,
            (self.contents_shown_for == self.open_container).then_some(self.contents),
        );
        self.packs_shown_for = root;
        self.contents_shown_for = self.open_container;
        let fixed =
            |ui: &UiRoot, list| ui.widget::<ItemList>(list).and_then(|l| l.fixed_slot_count);
        // Flushing a list's cells cancels a press on one of them, so each
        // list is rebuilt only when what it shows changed or the cell count
        // it gets now (from its container's capacity) differs from the one
        // it has: opening a pack inside the container changes the contents
        // row alone, and the pack cell pressed to open it stays to become a
        // drag.
        let rebuild: Vec<(ElementId, &[u32], Option<usize>)> = [
            (self.top, std::slice::from_ref(&root), None),
            (self.containers, packs.as_slice(), pack_cells),
            (self.contents, loose.as_slice(), item_cells),
        ]
        .into_iter()
        .filter(|&(list, guids, count)| {
            !self.built
                || fixed(ui, list) != count
                || !matches(ui, list, guids)
                || (list == self.top && cells(ui, list).len() != 1)
        })
        .collect();
        self.built = true;
        for (list, guids, count) in rebuild {
            ui.with::<ItemList, _>(list, |l, ui| {
                l.defer_layout();
                l.flush(ui);
                l.fixed_slot_count = count;
            });
            for (i, &guid) in guids.iter().enumerate() {
                let mut s = ItemSlot {
                    slot_index: i32::try_from(i).unwrap_or(i32::MAX),
                    drag_accept_sprite: DEFAULT_DRAG_ACCEPT_SPRITE,
                    drag_reject_sprite: DEFAULT_DRAG_REJECT_SPRITE,
                    ..ItemSlot::default()
                };
                // Filled before it joins the list: the list trims empty
                // cells past its visible width when it lays out, and an
                // unfilled cell would count as one.
                fill_slot(&mut s, view.get(guid), view, icons);
                s.source_kind = ItemDragSource::Ground;
                let cell = ui.insert(slot_element(), s);
                ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
            }
            ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
        }
        for (list, guids, pack) in [
            (self.top, std::slice::from_ref(&root), true),
            (self.containers, packs.as_slice(), true),
            (self.contents, loose.as_slice(), false),
        ] {
            for (i, cell) in cells(ui, list).into_iter().enumerate() {
                let Some(&guid) = guids.get(i) else {
                    continue;
                };
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    fill_slot(s, view.get(guid), view, icons);
                    s.source_kind = ItemDragSource::Ground;
                    if pack {
                        s.capacity_fill = capacity_fill(view, guid);
                    } else {
                        s.set_waiting(view.is_pending(guid) || (guid != 0 && guid == projected));
                    }
                }
            }
        }
        self.apply_indicators(ui);
    }

    /// The selection square and the open-pack triangle.
    fn apply_indicators(&self, ui: &mut UiRoot) {
        let selected = self.selection.get();
        let open = self.open_container;
        for list in [self.top, self.containers, self.contents] {
            for cell in cells(ui, list) {
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    let id = s.object_id;
                    let pending =
                        id != 0 && (self.target_source == Some(id) || self.pending.contains(&id));
                    s.selected = id != 0 && Some(id) == selected && !pending;
                    s.is_open_container = id != 0 && id == open;
                }
            }
        }
    }
}

impl PanelController for ExternalContainerController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if element == self.close {
            if matches!(message, ElementMessage::Clicked { .. }) {
                self.request_close(ui);
            }
            return true;
        }
        let Some(list) = self.list_of(ui, element) else {
            return false;
        };
        match message {
            ElementMessage::Pressed => self.press(ui, list, element),
            ElementMessage::DoubleClicked => {
                let id = slot_object(ui, element);
                if list == self.top {
                    self.request_close(ui);
                } else if list == self.contents && id != 0 {
                    emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(
                            ObjectRef::Guid(id),
                        ))),
                    );
                }
            }
            ElementMessage::RightClicked => {
                let id = slot_object(ui, element);
                if id != 0 {
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
        self.apply_indicators(ui);
        true
    }
}
